// The COM-import rewriter (Documentation/comimport-rewriter.md).
//
// An assembly that declares [ComImport] types is rewritten so that its COM calls go through
// WpfMac.ComInterop (vtable calls, ComObject wrappers) instead of built-in COM interop:
//
// - each ComImport interface I keeps its name, methods and Guid, loses ComImport and InterfaceType, and
//   gets a nested I.ComImpl: a [DynamicInterfaceCastableImplementation] interface whose methods
//   QueryInterface-free call through I's vtable slots, marshalling as built-in COM did;
// - P/Invokes and delegates whose signatures carry ComImport interfaces take IntPtr instead, and their
//   callers convert (Com.Borrow / Com.Take);
// - Marshal.GetObjectForIUnknown, GetComInterfaceForObject, ReleaseComObject, ... become Com's;
// - new C() of a ComImport coclass becomes Com.CreateInstance(clsid).
//
// Per assembly, all or nothing on Windows: Analyze lists every construct the rewriter does not support,
// and the caller rewrites only when there is none (off Windows it rewrites anyway, and an unsupported
// interface method throws by name when called).

using Mono.Cecil;
using Mono.Cecil.Cil;
using Mono.Cecil.Rocks;
using WpfMac.ComInterop;

namespace WpfMac.ComRewrite;

public sealed class RewriteResult
{
    /// <summary>The assembly declares or uses ComImport types at all.</summary>
    public bool UsesComImport;
    /// <summary>What the rewriter cannot do in it, one line each (empty: all of it is supported).</summary>
    public List<string> Unsupported { get; } = new();
    /// <summary>Assemblies whose COM types this one uses: it may be rewritten only together with them
    /// (their objects must be ComObjects too). The caller decides over the whole app.</summary>
    public SortedSet<string> UsesComTypesOf { get; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Written to the output path.</summary>
    public bool Written;
    public int Interfaces, Methods, PInvokes, Delegates, CallSites, EventInterfaces;

    public override string ToString() => !UsesComImport ? "no ComImport"
        : $"{Interfaces} interfaces ({Methods} methods), {PInvokes} P/Invokes, {Delegates} delegates, {CallSites} call sites" + (EventInterfaces > 0 ? $", {EventInterfaces} event interfaces" : "") +
          (UsesComTypesOf.Count == 0 ? "" : $"; uses the COM types of {string.Join(", ", UsesComTypesOf)}") +
          (Unsupported.Count == 0 ? "" : $"; unsupported: {string.Join("; ", Unsupported.Take(8))}{(Unsupported.Count > 8 ? $" (+{Unsupported.Count - 8} more)" : "")}");
}

public static partial class ComRewriter
{
    /// <summary>Whether the file declares a ComImport type (metadata only, cheap).</summary>
    public static bool DeclaresComImport(string path)
    {
        try
        {
            using var fs = File.OpenRead(path);
            using var pe = new System.Reflection.PortableExecutable.PEReader(fs);
            if (!pe.HasMetadata) return false;
            var md = System.Reflection.Metadata.PEReaderExtensions.GetMetadataReader(pe);
            foreach (var th in md.TypeDefinitions)
                if ((md.GetTypeDefinition(th).Attributes & System.Reflection.TypeAttributes.Import) != 0) return true;
            return false;
        }
        catch (Exception) { return false; }
    }

    /// <summary>
    /// Reads <paramref name="input"/>, rewrites it, and when <paramref name="output"/> is given and
    /// (<paramref name="partial"/> or nothing is unsupported) writes the result there.
    /// <paramref name="searchDirs"/>: where its references are (to resolve base types and structs).
    /// </summary>
    public static RewriteResult Rewrite(string input, string? output, IEnumerable<string> searchDirs, bool partial)
        => Rewrite(input, output, searchDirs, partial, externallyReferenced: null);

    /// <summary>
    /// The verdict alone, the launcher's judge: what the rewrite would support, found by reading the metadata
    /// and IL -- no code is generated (that happens when the assembly loads, ComImportLoader).
    /// </summary>
    public static RewriteResult Analyze(string input, IEnumerable<string> searchDirs, ISet<string>? externallyReferenced)
    {
        var result = new RewriteResult();
        var resolver = new DefaultAssemblyResolver();
        foreach (string d in searchDirs) if (Directory.Exists(d)) resolver.AddSearchDirectory(d);
        resolver.AddSearchDirectory(Path.GetDirectoryName(Path.GetFullPath(input))!);
        using var module = ModuleDefinition.ReadModule(input, new ReaderParameters { AssemblyResolver = resolver, ReadingMode = ReadingMode.Deferred, InMemory = true });
        if (!module.Types.SelectMany(AllTypes).Any(t => t.IsImport)) return result;
        result.UsesComImport = true;
        if (!module.IsILOnly()) { result.Unsupported.Add("mixed-mode (not IL-only) assembly"); return result; }
        try { new Work(module, result, externallyReferenced) { AnalyzeOnly = true }.Run(); }
        catch (AssemblyResolutionException e) { result.Unsupported.Add($"cannot resolve {e.AssemblyReference.Name}"); }
        return result;
    }

    /// <param name="externallyReferenced">The names (Namespace.Name, or Name for a nested type) of this
    /// assembly's types that other assemblies of the app reference; null: assume any visible type is.</param>
    public static RewriteResult Rewrite(string input, string? output, IEnumerable<string> searchDirs, bool partial, ISet<string>? externallyReferenced)
    {
        var result = new RewriteResult();
        var resolver = new DefaultAssemblyResolver();
        foreach (string d in searchDirs) if (Directory.Exists(d)) resolver.AddSearchDirectory(d);
        resolver.AddSearchDirectory(Path.GetDirectoryName(Path.GetFullPath(input))!);
        using var module = ModuleDefinition.ReadModule(input, new ReaderParameters { AssemblyResolver = resolver, ReadingMode = ReadingMode.Deferred, InMemory = true });
        if (!module.Types.SelectMany(AllTypes).Any(t => t.IsImport)) return result;
        result.UsesComImport = true;
        if (!module.IsILOnly()) { result.Unsupported.Add("mixed-mode (not IL-only) assembly"); return result; }
        var work = new Work(module, result, externallyReferenced);
        try { work.Run(); }
        catch (AssemblyResolutionException e)
        {
            // a reference not found where the app's are: what depends on it cannot be judged
            result.Unsupported.Add($"cannot resolve {e.AssemblyReference.Name}");
            return result;
        }
        if (output != null && (partial || (result.Unsupported.Count == 0 && result.UsesComTypesOf.Count == 0)))
        {
            module.Attributes &= ~ModuleAttributes.StrongNameSigned;
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
            try
            {
                module.Write(output);
                result.Written = true;
            }
            catch (AssemblyResolutionException e)
            {
                result.Unsupported.Add($"cannot resolve {e.AssemblyReference.Name} (to write it)");
                try { File.Delete(output); } catch (IOException) { }
            }
        }
        return result;
    }

    /// <summary>
    /// The rewritten image of <paramref name="input"/> (and its symbols, when a .pdb lies beside it), in
    /// memory: what the loader hands to LoadFromStream. Rewrites what is supported whatever else the
    /// assembly does (the decision to rewrite it was made already).
    /// </summary>
    /// <param name="locationOnly">only the location calls (caller rewriting): the COM types stay as they are,
    /// for built-in COM.</param>
    public static (MemoryStream Image, MemoryStream? Symbols, RewriteResult Result) RewriteToMemory(string input, IEnumerable<string> searchDirs, bool locationOnly = false)
    {
        var result = new RewriteResult();
        var resolver = new DefaultAssemblyResolver();
        foreach (string d in searchDirs) if (Directory.Exists(d)) resolver.AddSearchDirectory(d);
        resolver.AddSearchDirectory(Path.GetDirectoryName(Path.GetFullPath(input))!);
        string pdb = Path.ChangeExtension(input, ".pdb");
        bool symbols = File.Exists(pdb);
        var rp = new ReaderParameters { AssemblyResolver = resolver, ReadingMode = ReadingMode.Deferred, InMemory = true };
        ModuleDefinition module;
        try
        {
            rp.ReadSymbols = symbols;
            module = ModuleDefinition.ReadModule(input, rp);
        }
        catch (Exception) when (symbols)
        {
            // symbols that do not match (or a format Cecil cannot read): the image without them
            symbols = false;
            rp.ReadSymbols = false;
            module = ModuleDefinition.ReadModule(input, rp);
        }
        using (module)
        {
            result.UsesComImport = module.Types.SelectMany(AllTypes).Any(t => t.IsImport);
            if (locationOnly) new Work(module, result, externallyReferenced: null).RunLocationOnly();
            else new Work(module, result, externallyReferenced: null).Run();
            module.Attributes &= ~ModuleAttributes.StrongNameSigned;
            var image = new MemoryStream();
            MemoryStream? sym = null;
            if (symbols)
            {
                sym = new MemoryStream();
                module.Write(image, new WriterParameters { WriteSymbols = true, SymbolStream = sym, SymbolWriterProvider = new Mono.Cecil.Cil.PortablePdbWriterProvider() });
                sym.Position = 0;
            }
            else module.Write(image);
            image.Position = 0;
            result.Written = true;
            return (image, sym, result);
        }
    }

    private static bool IsILOnly(this ModuleDefinition m) => (m.Attributes & ModuleAttributes.ILOnly) != 0;

    internal static IEnumerable<TypeDefinition> AllTypes(TypeDefinition t)
    {
        yield return t;
        foreach (var n in t.NestedTypes) foreach (var x in AllTypes(n)) yield return x;
    }

    // ------------------------------------------------------------------------------------------------

    private sealed partial class Work
    {
        private readonly ModuleDefinition _m;
        private readonly RewriteResult _r;
        private readonly HashSet<TypeDefinition> _comTypes = new();
        private readonly Dictionary<MethodDefinition, MethodDefinition> _invokeShims = new();
        private readonly HashSet<TypeDefinition> _rewrittenDelegates = new();
        private TypeDefinition? _shims;

        // imported references
        private readonly MethodReference _toVariant, _fromVariant, _clearVariant;
        private readonly MethodReference _toSafeArray, _fromSafeArray, _takeSafeArray, _destroySafeArray;
        private readonly MethodReference _dispInvoke, _dispatchResult, _borrowDispatch, _eventProvider;
        private readonly TypeReference _variant;
        private readonly MethodReference _adopt, _toBstr, _freeBstr, _takeBstr, _takeCoTaskUni;
        private readonly MethodReference _this, _borrow, _borrowUnknown, _release, _take, _check, _unsupported, _createInstance, _keepAlive;
        private readonly TypeReference _intPtr, _int32, _int16, _byte, _object, _void, _guid;
        private readonly MethodReference _guidCtor, _strToUni, _strToAnsi, _freeCoTask;

        private readonly ISet<string>? _external;
        /// <summary>Judge only: the checks, without generating code (Analyze).</summary>
        public bool AnalyzeOnly;
        private readonly List<(TypeDefinition Class, TypeReference Interface)> _ccwUsers = new();

        public Work(ModuleDefinition m, RewriteResult r, ISet<string>? externallyReferenced)
        {
            _m = m; _r = r; _external = externallyReferenced;
            var com = typeof(Com);
            MethodReference Imp(string name, params Type[] args) => m.ImportReference(com.GetMethod(name, args) ?? throw new MissingMethodException(name));
            _this = Imp(nameof(Com.This), typeof(object), typeof(RuntimeTypeHandle));
            _borrow = Imp(nameof(Com.Borrow), typeof(object), typeof(RuntimeTypeHandle));
            _borrowUnknown = Imp(nameof(Com.BorrowUnknown), typeof(object));
            _release = Imp(nameof(Com.Release), typeof(IntPtr));
            _take = Imp(nameof(Com.Take), typeof(IntPtr));
            _check = Imp(nameof(Com.Check), typeof(int));
            _unsupported = Imp(nameof(Com.Unsupported), typeof(string), typeof(string));
            _createInstance = Imp(nameof(Com.CreateInstance), typeof(Guid));
            _adopt = Imp(nameof(Com.Adopt), typeof(object));
            _toBstr = Imp(nameof(Com.ToBstr), typeof(string));
            _toVariant = Imp(nameof(Com.ToVariant), typeof(object), typeof(IntPtr));
            _fromVariant = Imp(nameof(Com.FromVariant), typeof(IntPtr));
            _clearVariant = Imp(nameof(Com.ClearVariant), typeof(IntPtr));
            _toSafeArray = Imp(nameof(Com.ToSafeArray), typeof(object), typeof(int));
            _fromSafeArray = Imp(nameof(Com.FromSafeArray), typeof(IntPtr), typeof(RuntimeTypeHandle));
            _takeSafeArray = Imp(nameof(Com.TakeSafeArray), typeof(IntPtr), typeof(RuntimeTypeHandle));
            _destroySafeArray = Imp(nameof(Com.DestroySafeArray), typeof(IntPtr));
            _dispInvoke = Imp(nameof(Com.DispInvoke), typeof(object), typeof(RuntimeTypeHandle), typeof(int), typeof(string), typeof(int), typeof(object[]), typeof(long));
            _dispatchResult = Imp(nameof(Com.DispatchResult), typeof(object), typeof(RuntimeTypeHandle));
            _borrowDispatch = Imp(nameof(Com.BorrowDispatch), typeof(object));
            _eventProvider = Imp(nameof(Com.EventProvider), typeof(object), typeof(RuntimeTypeHandle));
            _variant = m.ImportReference(typeof(ComVariant));
            _freeBstr = Imp(nameof(Com.FreeBstr), typeof(IntPtr));
            _takeBstr = Imp(nameof(Com.TakeBstr), typeof(IntPtr));
            _takeCoTaskUni = Imp(nameof(Com.TakeCoTaskString), typeof(IntPtr));
            _keepAlive = m.ImportReference(typeof(GC).GetMethod(nameof(GC.KeepAlive))!);
            _intPtr = m.TypeSystem.IntPtr; _int32 = m.TypeSystem.Int32; _int16 = m.TypeSystem.Int16; _byte = m.TypeSystem.Byte;
            _object = m.TypeSystem.Object; _void = m.TypeSystem.Void;
            _guid = m.ImportReference(typeof(Guid));
            _guidCtor = m.ImportReference(typeof(Guid).GetConstructor(new[] { typeof(string) })!);
            var marshal = typeof(System.Runtime.InteropServices.Marshal);
            _strToUni = m.ImportReference(marshal.GetMethod(nameof(System.Runtime.InteropServices.Marshal.StringToCoTaskMemUni), new[] { typeof(string) })!);
            _strToAnsi = m.ImportReference(marshal.GetMethod(nameof(System.Runtime.InteropServices.Marshal.StringToCoTaskMemAnsi), new[] { typeof(string) })!);
            _freeCoTask = m.ImportReference(marshal.GetMethod(nameof(System.Runtime.InteropServices.Marshal.FreeCoTaskMem), new[] { typeof(IntPtr) })!);
        }

        private TypeDefinition Shims
        {
            get
            {
                if (_shims != null) return _shims;
                _shims = new TypeDefinition("", "<WpfMacComShims>", TypeAttributes.NotPublic | TypeAttributes.Abstract | TypeAttributes.Sealed | TypeAttributes.BeforeFieldInit, _object);
                _m.Types.Add(_shims);
                return _shims;
            }
        }

        /// <summary>Caller rewriting: the location calls alone.</summary>
        public void RunLocationOnly()
        {
            foreach (var t in _m.Types.SelectMany(AllTypes).ToList())
                foreach (var md in t.Methods)
                    if (md.HasBody)
                        foreach (var ins in md.Body.Instructions)
                            if ((ins.OpCode == OpCodes.Call || ins.OpCode == OpCodes.Callvirt) && ins.Operand is MethodReference mr && LocationReplacement(mr) is { } loc)
                            { ins.OpCode = OpCodes.Call; ins.Operand = loc; _r.CallSites++; }
        }

        public void Run()
        {
            foreach (var t in _m.Types.SelectMany(AllTypes).ToList()) if (t.IsImport) _comTypes.Add(t);

            // constructs left as they were
            foreach (var t in _m.Types.SelectMany(AllTypes))
            {
                if (t.IsImport || t.IsInterface) continue;
                foreach (var i in t.Interfaces)
                    if (IsCom(i.InterfaceType)) _ccwUsers.Add((t, i.InterfaceType));
                // [ComSourceInterfaces]: its CCW answers IConnectionPointContainer (WpfMac.ComInterop's EventSource)
            }
            // COM types of other assemblies (and the BCL's ComTypes): their objects come from built-in COM, which a
            // rewritten assembly's own wrappers cannot stand in for, nor take
            foreach (var tr in _m.GetTypeReferences())
            {
                var d = Safe(tr);
                if (d != null && d.IsImport && d.Module != _m)
                {
                    // the BCL's ComTypes are built-in COM's own, and never rewritten; WpfMac.ComInterop supports
                    // some of them by hand (SupportedBclComTypes)
                    if (IsBclCom(d)) continue;
                    if (IsBcl(d))
                        _r.Unsupported.Add($"uses COM type {d.FullName} of {d.Module.Assembly.Name.Name}");
                    else _r.UsesComTypesOf.Add(d.Module.Assembly.Name.Name);
                }
            }
            // tlbimp-style event interfaces: their add_/remove_ go to the provider made for the COM object
            foreach (var t in _m.Types.SelectMany(AllTypes).Where(t => t.IsInterface).ToList())
                if (t.CustomAttributes.FirstOrDefault(a => a.AttributeType.FullName == "System.Runtime.InteropServices.ComEventInterfaceAttribute") is { } ev)
                    RewriteEventInterface(t, ev);

            // A COM interface other assemblies can see may receive their built-in RCWs.
            foreach (var t in _comTypes.Where(t => t.IsInterface && Visible(t)))
                if (_external == null || _external.Contains(t.IsNested ? t.Name : t.FullName))
                    _r.Unsupported.Add($"{t.FullName} is {(_external == null ? "visible to" : "used by")} other assemblies (they may hand it built-in RCWs)");
            foreach (var t in _comTypes.Where(t => t.IsInterface).ToList()) RewriteInterface(t);
            // a managed class implementing a COM interface: its CCW must cover every method
            foreach (var (cls, it) in _ccwUsers)
            {
                var d = Safe(it);
                if (d != null && IsBclCom(d)) continue;            // its CCW is WpfMac.ComInterop's
                if (d == null || !_ccwRefused.TryGetValue(d, out var refused)) { _r.Unsupported.Add($"managed class {cls.FullName} implements COM interface {it.Name} (no CCW)"); continue; }
                foreach (var why in refused) _r.Unsupported.Add($"managed class {cls.FullName} implements {it.Name}, whose CCW cannot convert {why}");
            }
            foreach (var t in _m.Types.SelectMany(AllTypes).ToList()) if (IsDelegate(t) && !t.IsImport) MaybeRewriteDelegate(t);
            foreach (var t in _m.Types.SelectMany(AllTypes).ToList())
                foreach (var md in t.Methods.ToList())
                    if (md.IsPInvokeImpl && SignatureHasCom(md)) RewritePInvoke(md);
            foreach (var t in _m.Types.SelectMany(AllTypes).ToList())
                foreach (var md in t.Methods.ToList())
                    if (md.HasBody) RewriteCallSites(md);
            foreach (var t in _comTypes.Where(t => !t.IsInterface).ToList()) RewriteCoClass(t);
        }

        // ---- types ------------------------------------------------------------------------------

        private static bool Visible(TypeDefinition t)
        {
            for (var x = t; x != null; x = x.DeclaringType)
                if (!(x.IsPublic || x.IsNestedPublic)) return false;
            return true;
        }

        private bool IsCom(TypeReference? t)
        {
            if (t == null) return false;
            if (t is ByReferenceType br) return IsCom(br.ElementType);
            TypeDefinition? d = t.Module == _m || t.Scope == _m ? Safe(t) : null;
            if (d != null && _comTypes.Contains(d)) return true;
            return t is not GenericParameter && t.Namespace == "System.Runtime.InteropServices.ComTypes" && (d ?? Safe(t)) is { } b && IsBclCom(b);
        }

        private static bool IsBcl(TypeDefinition d) => d.Module.Assembly.Name.Name is "System.Private.CoreLib" or "System.Runtime" or "System.Runtime.InteropServices" or "mscorlib" or "netstandard";

        /// <summary>A BCL COM interface WpfMac.ComInterop implements by hand (both ways).</summary>
        private static bool IsBclCom(TypeDefinition d) => d.IsImport && d.IsInterface && IsBcl(d) && s_bclCom.Contains(d.FullName);

        private static readonly HashSet<string> s_bclCom = new(WpfMac.ComInterop.Com.SupportedBclComTypes, StringComparer.Ordinal);

        private bool IsComInterface(TypeReference t) => IsCom(t) && Safe(t is ByReferenceType b ? b.ElementType : t)!.IsInterface;

        private static TypeDefinition? Safe(TypeReference t) { try { return t.Resolve(); } catch { return null; } }

        private static bool IsDelegate(TypeDefinition t) => t.BaseType?.FullName == "System.MulticastDelegate";

        private bool SignatureHasCom(MethodReference m) => IsCom(m.ReturnType) || m.Parameters.Any(p => IsCom(p.ParameterType));

        // ---- marshalling ------------------------------------------------------------------------

        private enum Kind { Blit, Bool4, Bool2, Bool1, Iface, Unknown, OutIface, ByRefBlit, StrW, StrA, StrB, BlitArray, RefBool4, RefBool2, RefBool1, RefStrB, RefStrW, Variant, RefVariant, SafeArray, RefSafeArray }

        /// <param name="Vt">a SAFEARRAY's element VARTYPE (0: the managed element type's default, at run time)</param>
        private sealed record Arg(Kind Kind, TypeReference Native, TypeReference? Iface = null, int Vt = 0);

        private static readonly HashSet<string> s_prim = new()
        {
            "System.SByte", "System.Byte", "System.Int16", "System.UInt16", "System.Int32", "System.UInt32", "System.Int64",
            "System.UInt64", "System.IntPtr", "System.UIntPtr", "System.Single", "System.Double", "System.Char",
        };

        private bool IsBlittable(TypeReference t, int depth = 0)
        {
            if (t.IsPointer || t.IsFunctionPointer) return true;
            if (s_prim.Contains(t.FullName)) return true;
            if (!t.IsValueType || depth > 8) return false;
            var d = Safe(t);
            if (d == null) return false;
            if (d.IsEnum) return true;
            if (d.IsAutoLayout && !d.IsSequentialLayout && !d.IsExplicitLayout) return false;
            if (t.FullName == "System.Guid" || t.FullName == "System.Decimal") return t.FullName == "System.Guid";
            if (d.HasGenericParameters) return false;
            foreach (var f in d.Fields)
            {
                if (f.IsStatic) continue;
                if (f.HasMarshalInfo || !IsBlittable(f.FieldType, depth + 1)) return false;
            }
            return true;
        }

        /// <summary>How built-in COM interop marshals parameter <paramref name="p"/> of a COM interface method,
        /// as one of the kinds here, or why it cannot.</summary>
        private Arg? Classify(TypeReference type, MarshalInfo? mi, bool comDefaults, out string? why)
        {
            why = null;
            var nt = mi?.NativeType ?? NativeType.None;
            if (type is ByReferenceType br)
            {
                var e = br.ElementType;
                if (IsComInterface(e)) return new Arg(Kind.OutIface, _intPtr, e);
                if (e.FullName == "System.Object" && nt is NativeType.IUnknown or NativeType.IntF) return new Arg(Kind.OutIface, _intPtr, null);
                if (e.FullName == "System.Object" && nt is NativeType.IDispatch) return new Arg(Kind.OutIface, _intPtr, null, 9);
                if (IsBlittable(e) && e.FullName != "System.Boolean") return new Arg(Kind.ByRefBlit, _intPtr);
                if (e.FullName == "System.Object" && (nt is NativeType.Struct || (nt == NativeType.None && comDefaults)))
                    return new Arg(Kind.RefVariant, _intPtr);
                if (e.FullName == "System.String")
                {
                    if (nt is NativeType.BStr || (nt == NativeType.None && comDefaults)) return new Arg(Kind.RefStrB, _intPtr);
                    if (nt is NativeType.LPWStr or NativeType.LPTStr) return new Arg(Kind.RefStrW, _intPtr);
                }
                if (e.FullName == "System.Boolean")
                {
                    if (nt is NativeType.Boolean || (nt == NativeType.None && !comDefaults)) return new Arg(Kind.RefBool4, _intPtr);
                    if (nt is NativeType.U1 or NativeType.I1) return new Arg(Kind.RefBool1, _intPtr);
                    if (nt is NativeType.VariantBool || nt == NativeType.None) return new Arg(Kind.RefBool2, _intPtr);
                }
                if (IsSafeArray(e, nt, comDefaults))
                    return SafeArrayVt(e, mi, out why) is int rvt ? new Arg(Kind.RefSafeArray, _intPtr, null, rvt) : null;
                why = $"by-reference {e.Name}"; return null;
            }
            if (IsComInterface(type)) return new Arg(Kind.Iface, _intPtr, type);
            switch (type.FullName)
            {
                case "System.Boolean":
                    if (nt is NativeType.Boolean) return new Arg(Kind.Bool4, _int32);
                    if (nt is NativeType.U1 or NativeType.I1) return new Arg(Kind.Bool1, _byte);
                    if (nt is NativeType.VariantBool || (nt == NativeType.None && comDefaults)) return new Arg(Kind.Bool2, _int16);
                    if (nt == NativeType.None) return new Arg(Kind.Bool4, _int32);
                    break;
                case "System.String":
                    if (nt is NativeType.LPWStr or NativeType.LPTStr) return new Arg(Kind.StrW, _intPtr);
                    if (nt is NativeType.LPStr) return new Arg(Kind.StrA, _intPtr);
                    if (nt is NativeType.BStr || (nt == NativeType.None && comDefaults)) return new Arg(Kind.StrB, _intPtr);
                    why = $"string as {nt}"; return null;
                case "System.Object":
                    if (nt is NativeType.IUnknown or NativeType.IntF) return new Arg(Kind.Unknown, _intPtr);
                    if (nt is NativeType.Struct || (nt == NativeType.None && comDefaults)) return new Arg(Kind.Variant, _variant);
                    if (nt is NativeType.IDispatch) return new Arg(Kind.Unknown, _intPtr, null, 9);
                    why = $"object as {nt}"; return null;
            }
            if (IsBlittable(type)) return new Arg(Kind.Blit, type);
            // an array marshalled as a C array (not COM's default SAFEARRAY) of blittable elements: pinned
            if (type is ArrayType at && at.IsVector && IsBlittable(at.ElementType) && at.ElementType.FullName != "System.Char"
                && (nt is NativeType.Array || (nt == NativeType.None && !comDefaults)))
                return new Arg(Kind.BlitArray, _intPtr);
            if (IsSafeArray(type, nt, comDefaults))
                return SafeArrayVt(type, mi, out why) is int vt ? new Arg(Kind.SafeArray, _intPtr, null, vt) : null;
            why = type.IsArray ? $"array {type.Name}{(nt == NativeType.None ? " (a SAFEARRAY)" : $" as {nt}")}" : $"{type.FullName}"; return null;
        }

        /// <summary>An array marshalled as a SAFEARRAY: COM's default for an array (or System.Array) in a COM
        /// interface method, or [MarshalAs(UnmanagedType.SafeArray)].</summary>
        private static bool IsSafeArray(TypeReference t, NativeType nt, bool comDefaults) =>
            (t is ArrayType || t.FullName == "System.Array") && (nt == NativeType.SafeArray || (nt == NativeType.None && comDefaults));

        /// <summary>The element VARTYPE to pass (0: the run time's default for the element type), or null and why.</summary>
        private int? SafeArrayVt(TypeReference t, MarshalInfo? mi, out string? why)
        {
            why = null;
            if (mi is SafeArrayMarshalInfo sm && sm.ElementType != VariantType.None)
            {
                if ((int)sm.ElementType is 36 or 29) { why = "a SAFEARRAY of records"; return null; }          // VT_RECORD, VT_USERDEFINED
                return (int)sm.ElementType;
            }
            if (t is not ArrayType at) return 0;                       // System.Array: its element type at run time
            var e = at.ElementType;
            if (e is ArrayType) { why = "a SAFEARRAY of arrays"; return null; }
            if (IsComInterface(e))
            {
                var d = Safe(e);
                var ita = d?.CustomAttributes.FirstOrDefault(a => a.AttributeType.FullName == "System.Runtime.InteropServices.InterfaceTypeAttribute");
                int type = ita != null && ita.ConstructorArguments.Count == 1 ? Convert.ToInt32(ita.ConstructorArguments[0].Value) : 0;
                return type == 1 ? 13 : 9;                               // VT_UNKNOWN / VT_DISPATCH (dual)
            }
            if (s_prim.Contains(e.FullName) || e.FullName is "System.Boolean" or "System.String" or "System.Object" or "System.Decimal" or "System.DateTime")
                return 0;
            if (Safe(e) is { IsEnum: true }) return 0;
            if (Safe(e) is { IsInterface: true } or { IsClass: true, IsValueType: false }) return 13;
            why = $"a SAFEARRAY of {e.Name} (VT_RECORD)"; return null;
        }

        // ---- interfaces -------------------------------------------------------------------------

        private void RewriteInterface(TypeDefinition t)
        {
            int ifaceType = 0;      // InterfaceIsDual
            var ita = t.CustomAttributes.FirstOrDefault(a => a.AttributeType.FullName == "System.Runtime.InteropServices.InterfaceTypeAttribute");
            if (ita != null && ita.ConstructorArguments.Count == 1) ifaceType = Convert.ToInt32(ita.ConstructorArguments[0].Value);
            if (ifaceType == 3 /* IInspectable */)
            {
                _r.Unsupported.Add($"{t.FullName}: a WinRT interface");
                return;
            }
            if (!t.CustomAttributes.Any(a => a.AttributeType.FullName == "System.Runtime.InteropServices.GuidAttribute"))
            {
                _r.Unsupported.Add($"{t.FullName}: ComImport interface without a Guid");
                return;
            }
            int first = ifaceType == 1 ? 3 : 7;
            if (ifaceType == 2) { RewriteDispInterface(t, ita); return; }
            if (AnalyzeOnly)
            {
                _r.Interfaces++;
                var refused = new List<string>();
                foreach (var md in t.Methods.Where(x => !x.IsStatic))
                {
                    _r.Methods++;
                    if (ForwardWhy(md) is { } f) _r.Unsupported.Add($"{t.FullName}.{md.Name}: {f}");
                    if (ReverseWhy(md) is { } rv) refused.Add($"{md.Name}: {rv}");
                }
                _ccwRefused[t] = refused;
                return;
            }

            // the plain interface, and its implementation
            t.IsImport = false;
            if (ita != null) t.CustomAttributes.Remove(ita);
            var impl = new TypeDefinition("", "ComImpl", TypeAttributes.NestedPublic | TypeAttributes.Interface | TypeAttributes.Abstract);
            impl.Interfaces.Add(new InterfaceImplementation(t));
            impl.CustomAttributes.Add(new CustomAttribute(_m.ImportReference(typeof(System.Runtime.InteropServices.DynamicInterfaceCastableImplementationAttribute).GetConstructor(Type.EmptyTypes)!)));
            t.NestedTypes.Add(impl);
            _r.Interfaces++;

            int slot = first;
            var methods = t.Methods.Where(x => !x.IsStatic).ToList();
            EmitCcw(t, first, methods);
            foreach (var md in methods)
            {
                var body = new MethodDefinition($"{t.FullName}.{md.Name}", MethodAttributes.Private | MethodAttributes.HideBySig | MethodAttributes.NewSlot | MethodAttributes.Virtual | MethodAttributes.Final, md.ReturnType);
                foreach (var p in md.Parameters) body.Parameters.Add(new ParameterDefinition(p.Name, p.Attributes & ~ParameterAttributes.HasFieldMarshal, p.ParameterType));
                body.Overrides.Add(md);
                impl.Methods.Add(body);
                EmitVtableCall(t, md, body, slot++);
                _r.Methods++;
            }
        }

        // ---- event interfaces ([ComEventInterface(typeof(Source), typeof(Provider))]) -------------------

        /// <summary>I.ComImpl forwards each member to Com.EventProvider(this, Provider), the provider tlbimp
        /// wrote (it finds the connection point and Advises its sink helper); the nested ComEvents marks it, so a
        /// cast asks the object for IConnectionPointContainer.</summary>
        private void RewriteEventInterface(TypeDefinition t, CustomAttribute ev)
        {
            if (ev.ConstructorArguments.Count != 2 || ev.ConstructorArguments[1].Value is not TypeReference provider)
            {
                _r.Unsupported.Add($"COM events: {t.FullName}'s [ComEventInterface] names no provider");
                return;
            }
            _r.EventInterfaces++;
            if (AnalyzeOnly || t.NestedTypes.Any(n => n.Name == "ComImpl")) return;
            var impl = new TypeDefinition("", "ComImpl", TypeAttributes.NestedPublic | TypeAttributes.Interface | TypeAttributes.Abstract);
            impl.Interfaces.Add(new InterfaceImplementation(t));
            impl.CustomAttributes.Add(new CustomAttribute(_m.ImportReference(typeof(System.Runtime.InteropServices.DynamicInterfaceCastableImplementationAttribute).GetConstructor(Type.EmptyTypes)!)));
            t.NestedTypes.Add(impl);
            t.NestedTypes.Add(new TypeDefinition("", "ComEvents", TypeAttributes.NestedPublic | TypeAttributes.Abstract | TypeAttributes.Sealed, _object));
            foreach (var md in t.Methods.Where(x => !x.IsStatic).ToList())
            {
                var body = new MethodDefinition($"{t.FullName}.{md.Name}", MethodAttributes.Private | MethodAttributes.HideBySig | MethodAttributes.NewSlot | MethodAttributes.Virtual | MethodAttributes.Final, md.ReturnType);
                foreach (var p in md.Parameters) body.Parameters.Add(new ParameterDefinition(p.Name, p.Attributes, p.ParameterType));
                body.Overrides.Add(md);
                impl.Methods.Add(body);
                var il = body.Body.GetILProcessor();
                il.Emit(OpCodes.Ldarg_0);
                il.Emit(OpCodes.Ldtoken, provider);
                il.Emit(OpCodes.Call, _eventProvider);
                il.Emit(OpCodes.Castclass, t);
                for (int i = 0; i < md.Parameters.Count; i++) il.Emit(OpCodes.Ldarg, i + 1);
                il.Emit(OpCodes.Callvirt, md);
                il.Emit(OpCodes.Ret);
            }
        }

        // ---- dispinterfaces: called through IDispatch::Invoke ---------------------------------------

        /// <summary>A dispinterface ([InterfaceType(InterfaceIsIDispatch)]): I.ComImpl's bodies box the arguments
        /// and call Com.DispInvoke with the member's DISPID; I.ComCcw answers IDispatch by reflection; the
        /// nested ComDispatch marks it (a cast accepts an object answering IDispatch only).</summary>
        private void RewriteDispInterface(TypeDefinition t, CustomAttribute? ita)
        {
            var methods = t.Methods.Where(x => !x.IsStatic).ToList();
            _r.Interfaces++;
            foreach (var md in methods)
            {
                _r.Methods++;
                if (DispWhy(md) is { } why) _r.Unsupported.Add($"{t.FullName}.{md.Name}: {why}");
            }
            if (AnalyzeOnly) { _ccwRefused[t] = new List<string>(); return; }
            t.IsImport = false;
            if (ita != null) t.CustomAttributes.Remove(ita);
            var impl = new TypeDefinition("", "ComImpl", TypeAttributes.NestedPublic | TypeAttributes.Interface | TypeAttributes.Abstract);
            impl.Interfaces.Add(new InterfaceImplementation(t));
            impl.CustomAttributes.Add(new CustomAttribute(_m.ImportReference(typeof(System.Runtime.InteropServices.DynamicInterfaceCastableImplementationAttribute).GetConstructor(Type.EmptyTypes)!)));
            t.NestedTypes.Add(impl);
            t.NestedTypes.Add(new TypeDefinition("", "ComDispatch", TypeAttributes.NestedPublic | TypeAttributes.Abstract | TypeAttributes.Sealed, _object));
            EmitCcw(t, 7, new List<MethodDefinition>());
            foreach (var md in methods)
            {
                var body = new MethodDefinition($"{t.FullName}.{md.Name}", MethodAttributes.Private | MethodAttributes.HideBySig | MethodAttributes.NewSlot | MethodAttributes.Virtual | MethodAttributes.Final, md.ReturnType);
                foreach (var p in md.Parameters) body.Parameters.Add(new ParameterDefinition(p.Name, p.Attributes & ~ParameterAttributes.HasFieldMarshal, p.ParameterType));
                body.Overrides.Add(md);
                impl.Methods.Add(body);
                if (DispWhy(md) is { } why) { Refuse(body.Body.GetILProcessor(), $"{t.FullName}.{md.Name}", why); continue; }
                EmitDispatchCall(t, md, body);
            }
        }

        /// <summary>What a VARIANT cannot carry in <paramref name="md"/>'s signature; null when every part converts.</summary>
        private string? DispWhy(MethodDefinition md)
        {
            string? Of(TypeReference t, string what)
            {
                if (t is ByReferenceType br) t = br.ElementType;
                if (t.IsPointer || t.IsFunctionPointer || t is GenericParameter) return $"{what}: {t.Name}";
                if (t is ArrayType at) return at.ElementType is ArrayType ? $"{what}: an array of arrays" : Of(at.ElementType, what);
                if (s_prim.Contains(t.FullName) || t.FullName is "System.Boolean" or "System.String" or "System.Object" or "System.Decimal" or "System.DateTime" or "System.Void" or "System.Array") return null;
                var d = Safe(t);
                if (d == null) return $"{what}: {t.Name} (unresolved)";
                if (d.IsEnum || d.IsInterface || !d.IsValueType) return null;
                return $"{what}: {t.Name} (a struct in a VARIANT)";
            }
            foreach (var p in md.Parameters) if (Of(p.ParameterType, $"parameter {p.Name}") is { } w) return w;
            return Of(md.ReturnType, "return");
        }

        private static int? DispIdOf(ICustomAttributeProvider p)
        {
            var a = p.CustomAttributes.FirstOrDefault(x => x.AttributeType.FullName == "System.Runtime.InteropServices.DispIdAttribute");
            return a != null && a.ConstructorArguments.Count == 1 ? Convert.ToInt32(a.ConstructorArguments[0].Value) : null;
        }

        private void EmitDispatchCall(TypeDefinition iface, MethodDefinition decl, MethodDefinition body)
        {
            body.Body.InitLocals = true;
            var il = body.Body.GetILProcessor();
            // the member: a property's accessor goes by the property's DISPID and name
            var prop = decl.IsSpecialName ? iface.Properties.FirstOrDefault(p => p.GetMethod == decl || p.SetMethod == decl) : null;
            int dispid = DispIdOf(decl) ?? (prop != null ? DispIdOf(prop) : null) ?? Com.DispIdUnknown;
            string name = prop?.Name ?? decl.Name;
            bool isVoid = decl.ReturnType.FullName == "System.Void";
            int flags = prop == null ? (isVoid ? Com.DISPATCH_METHOD : Com.DISPATCH_METHOD | Com.DISPATCH_PROPERTYGET)
                : prop.GetMethod == decl ? Com.DISPATCH_PROPERTYGET : Com.DISPATCH_PROPERTYPUT;
            var arr = Local(body, new ArrayType(_object));
            il.Emit(OpCodes.Ldc_I4, decl.Parameters.Count);
            il.Emit(OpCodes.Newarr, _object);
            il.Emit(OpCodes.Stloc, arr);
            long mask = 0;
            for (int i = 0; i < decl.Parameters.Count; i++)
            {
                var pt = decl.Parameters[i].ParameterType;
                bool byRef = pt is ByReferenceType;
                var et = byRef ? ((ByReferenceType)pt).ElementType : pt;
                if (byRef && i < 64) mask |= 1L << i;
                il.Emit(OpCodes.Ldloc, arr);
                il.Emit(OpCodes.Ldc_I4, i);
                il.Emit(OpCodes.Ldarg, i + 1);
                if (byRef) il.Emit(OpCodes.Ldobj, et);
                il.Emit(OpCodes.Box, et);                 // a no-op for a reference type
                il.Emit(OpCodes.Stelem_Ref);
            }
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Ldtoken, iface);
            il.Emit(OpCodes.Ldc_I4, dispid);
            il.Emit(OpCodes.Ldstr, name);
            il.Emit(OpCodes.Ldc_I4, flags);
            il.Emit(OpCodes.Ldloc, arr);
            il.Emit(OpCodes.Ldc_I8, mask);
            il.Emit(OpCodes.Call, _dispInvoke);
            var res = Local(body, _object);
            il.Emit(OpCodes.Stloc, res);
            for (int i = 0; i < decl.Parameters.Count; i++)
            {
                if (decl.Parameters[i].ParameterType is not ByReferenceType br) continue;
                il.Emit(OpCodes.Ldarg, i + 1);
                il.Emit(OpCodes.Ldloc, arr);
                il.Emit(OpCodes.Ldc_I4, i);
                il.Emit(OpCodes.Ldelem_Ref);
                il.Emit(OpCodes.Ldtoken, br.ElementType);
                il.Emit(OpCodes.Call, _dispatchResult);
                il.Emit(OpCodes.Unbox_Any, br.ElementType);
                il.Emit(OpCodes.Stobj, br.ElementType);
            }
            if (!isVoid)
            {
                il.Emit(OpCodes.Ldloc, res);
                il.Emit(OpCodes.Ldtoken, decl.ReturnType);
                il.Emit(OpCodes.Call, _dispatchResult);
                il.Emit(OpCodes.Unbox_Any, decl.ReturnType);
            }
            il.Emit(OpCodes.Ret);
        }

        /// <summary>What a vtable call to <paramref name="decl"/> could not convert; null when it can (the same
        /// checks as EmitVtableCall's).</summary>
        private string? ForwardWhy(MethodDefinition decl)
        {
            foreach (var p in decl.Parameters)
                if (Classify(p.ParameterType, p.MarshalInfo, comDefaults: true, out string? why) == null) return $"parameter {p.Name}: {why}";
            if (decl.ReturnType.FullName == "System.Void") return null;
            var ret = Classify(decl.ReturnType, decl.MethodReturnType.MarshalInfo, comDefaults: true, out string? w);
            if (ret != null && ret.Kind == Kind.Variant && decl.IsPreserveSig) return "a VARIANT returned by value";
            if (ret == null || ret.Kind is Kind.ByRefBlit or Kind.OutIface or Kind.StrA or Kind.BlitArray or Kind.RefBool1 or Kind.RefBool2 or Kind.RefBool4 or Kind.RefStrB or Kind.RefStrW)
                return $"return {w ?? decl.ReturnType.Name}";
            return null;
        }

        /// <summary>What a CCW stub for <paramref name="md"/> could not convert; null when it can (the same checks
        /// as Stub's).</summary>
        private string? ReverseWhy(MethodDefinition md)
        {
            foreach (var p in md.Parameters)
            {
                var a = Classify(p.ParameterType, p.MarshalInfo, comDefaults: true, out string? w);
                if (a == null || a.Kind is Kind.BlitArray) return $"parameter {p.Name}: {w ?? "an array of unknown length"}";
            }
            if (md.ReturnType.FullName == "System.Void") return null;
            var ret = Classify(md.ReturnType, md.MethodReturnType.MarshalInfo, comDefaults: true, out string? w2);
            if (ret == null || ret.Kind is Kind.ByRefBlit or Kind.OutIface or Kind.BlitArray or Kind.RefBool1 or Kind.RefBool2 or Kind.RefBool4 or Kind.RefStrB or Kind.RefStrW or Kind.RefVariant or Kind.StrA
                || (md.IsPreserveSig && ret.Kind == Kind.Variant))
                return $"return {w2 ?? md.ReturnType.Name}";
            return null;
        }

        private void EmitVtableCall(TypeDefinition iface, MethodDefinition decl, MethodDefinition body, int slot)
        {
            body.Body.InitLocals = true;
            var il = body.Body.GetILProcessor();
            bool preserve = decl.IsPreserveSig;
            string member = $"{iface.FullName}.{decl.Name}";

            var args = new List<Arg>();
            foreach (var p in decl.Parameters)
            {
                var a = Classify(p.ParameterType, p.MarshalInfo, comDefaults: true, out string? why);
                if (a == null) { Refuse(il, member, $"parameter {p.Name}: {why}"); return; }
                args.Add(a);
            }
            bool isVoid = decl.ReturnType.FullName == "System.Void";
            Arg? ret = null;
            if (!isVoid)
            {
                ret = Classify(decl.ReturnType, decl.MethodReturnType.MarshalInfo, comDefaults: true, out string? why);
                if (ret != null && ret.Kind == Kind.Variant && preserve) { Refuse(il, member, "a VARIANT returned by value"); return; }
                if (ret == null || ret.Kind is Kind.ByRefBlit or Kind.OutIface or Kind.StrA or Kind.BlitArray or Kind.RefBool1 or Kind.RefBool2 or Kind.RefBool4 or Kind.RefStrB or Kind.RefStrW)
                { Refuse(il, member, $"return {why ?? decl.ReturnType.Name}"); return; }
            }

            var thisPtr = Local(body, _intPtr);
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Ldtoken, iface);
            il.Emit(OpCodes.Call, _this);
            il.Emit(OpCodes.Stloc, thisPtr);

            var site = new CallSite(preserve ? (ret?.Native ?? _void) : _int32) { CallingConvention = MethodCallingConvention.StdCall };
            site.Parameters.Add(new ParameterDefinition(_intPtr));
            var cleanup = new List<Action>();
            var after = new List<Action>();
            var pushes = new List<Action>();
            for (int i = 0; i < args.Count; i++)
            {
                int argIndex = i + 1;
                var a = args[i];
                var p = decl.Parameters[i];
                site.Parameters.Add(new ParameterDefinition(a.Native));
                Prepare(body, il, a, argIndex, p.ParameterType, pushes, cleanup, after);
            }
            VariableDefinition? retLocal = null;
            if (!preserve && ret != null)
            {
                retLocal = Local(body, ret.Native);
                site.Parameters.Add(new ParameterDefinition(_intPtr));
                pushes.Add(() => { il.Emit(OpCodes.Ldloca, retLocal); il.Emit(OpCodes.Conv_U); });
            }

            Instruction? tryStart = cleanup.Count > 0 ? il.Create(OpCodes.Nop) : null;
            if (tryStart != null) il.Append(tryStart);
            il.Emit(OpCodes.Ldloc, thisPtr);
            foreach (var push in pushes) push();
            // the function: (*thisPtr)[slot]
            il.Emit(OpCodes.Ldloc, thisPtr);
            il.Emit(OpCodes.Ldind_I);
            il.Emit(OpCodes.Ldc_I4, slot);
            il.Emit(OpCodes.Conv_I);
            il.Emit(OpCodes.Sizeof, _intPtr);
            il.Emit(OpCodes.Mul);
            il.Emit(OpCodes.Add);
            il.Emit(OpCodes.Ldind_I);
            il.Emit(OpCodes.Calli, site);
            VariableDefinition? preservedRet = null;
            if (preserve && ret != null) { preservedRet = Local(body, ret.Native); il.Emit(OpCodes.Stloc, preservedRet); }
            else if (!preserve) il.Emit(OpCodes.Call, _check);
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Call, _keepAlive);
            foreach (var a2 in after) a2();

            if (tryStart != null)
            {
                var leaveTarget = il.Create(OpCodes.Nop);
                il.Emit(OpCodes.Leave, leaveTarget);
                var handlerStart = il.Create(OpCodes.Nop);
                il.Append(handlerStart);
                foreach (var c in cleanup) c();
                il.Emit(OpCodes.Endfinally);
                il.Append(leaveTarget);
                body.Body.ExceptionHandlers.Add(new ExceptionHandler(ExceptionHandlerType.Finally)
                {
                    TryStart = tryStart, TryEnd = handlerStart, HandlerStart = handlerStart, HandlerEnd = leaveTarget,
                });
            }

            if (ret != null && ret.Kind == Kind.Variant && !preserve)
            {
                il.Emit(OpCodes.Ldloca, retLocal!); il.Emit(OpCodes.Conv_U); il.Emit(OpCodes.Call, _fromVariant);
                il.Emit(OpCodes.Ldloca, retLocal!); il.Emit(OpCodes.Conv_U); il.Emit(OpCodes.Call, _clearVariant);
            }
            else if (ret != null)
            {
                var v = preserve ? preservedRet! : retLocal!;
                il.Emit(OpCodes.Ldloc, v);
                FromNative(il, ret, decl.ReturnType);
            }
            il.Emit(OpCodes.Ret);
        }

        /// <summary>Emits what argument <paramref name="argIndex"/> needs before the call (into
        /// <paramref name="pushes"/>: loading it), after it and in the finally.</summary>
        private void Prepare(MethodDefinition body, ILProcessor il, Arg a, int argIndex, TypeReference managed, List<Action> pushes, List<Action> cleanup, List<Action> after)
        {
            switch (a.Kind)
            {
                case Kind.Blit:
                    pushes.Add(() => il.Emit(OpCodes.Ldarg, argIndex));
                    break;
                case Kind.Bool4:
                case Kind.Bool1:
                    pushes.Add(() => il.Emit(OpCodes.Ldarg, argIndex));      // 0/1 already
                    break;
                case Kind.Bool2:
                    pushes.Add(() => { il.Emit(OpCodes.Ldarg, argIndex); il.Emit(OpCodes.Ldc_I4_0); il.Emit(OpCodes.Cgt_Un); il.Emit(OpCodes.Neg); il.Emit(OpCodes.Conv_I2); });
                    break;
                case Kind.Iface:
                case Kind.Unknown:
                {
                    var tmp = Local(body, _intPtr);
                    // borrowed before the try: a failed cast throws before anything is held
                    il.Emit(OpCodes.Ldarg, argIndex);
                    if (a.Kind == Kind.Iface) { il.Emit(OpCodes.Ldtoken, a.Iface!); il.Emit(OpCodes.Call, _borrow); }
                    else il.Emit(OpCodes.Call, a.Vt == 9 ? _borrowDispatch : _borrowUnknown);
                    il.Emit(OpCodes.Stloc, tmp);
                    pushes.Add(() => il.Emit(OpCodes.Ldloc, tmp));
                    cleanup.Add(() => { il.Emit(OpCodes.Ldloc, tmp); il.Emit(OpCodes.Call, _release); });
                    break;
                }
                case Kind.OutIface:
                {
                    var tmp = Local(body, _intPtr);
                    var elem = ((ByReferenceType)managed).ElementType;
                    pushes.Add(() => { il.Emit(OpCodes.Ldloca, tmp); il.Emit(OpCodes.Conv_U); });
                    after.Add(() =>
                    {
                        il.Emit(OpCodes.Ldarg, argIndex);
                        il.Emit(OpCodes.Ldloc, tmp);
                        il.Emit(OpCodes.Call, _take);
                        if (a.Iface != null) il.Emit(OpCodes.Castclass, a.Iface);
                        il.Emit(OpCodes.Stind_Ref);
                    });
                    break;
                }
                case Kind.ByRefBlit:
                {
                    var pin = new VariableDefinition(new PinnedType(managed));
                    body.Body.Variables.Add(pin);
                    il.Emit(OpCodes.Ldarg, argIndex);
                    il.Emit(OpCodes.Stloc, pin);
                    pushes.Add(() => { il.Emit(OpCodes.Ldloc, pin); il.Emit(OpCodes.Conv_U); });
                    break;
                }
                case Kind.BlitArray:
                {
                    var elem = ((ArrayType)managed).ElementType;
                    var pin = new VariableDefinition(new PinnedType(managed));
                    body.Body.Variables.Add(pin);
                    il.Emit(OpCodes.Ldarg, argIndex);
                    il.Emit(OpCodes.Stloc, pin);
                    pushes.Add(() =>
                    {
                        // &a[0], or null for a null or empty array
                        var zero = il.Create(OpCodes.Ldc_I4_0);
                        var done = il.Create(OpCodes.Nop);
                        il.Emit(OpCodes.Ldloc, pin);
                        il.Emit(OpCodes.Brfalse, zero);
                        il.Emit(OpCodes.Ldloc, pin);
                        il.Emit(OpCodes.Ldlen);
                        il.Emit(OpCodes.Brfalse, zero);
                        il.Emit(OpCodes.Ldloc, pin);
                        il.Emit(OpCodes.Ldc_I4_0);
                        il.Emit(OpCodes.Ldelema, elem);
                        il.Emit(OpCodes.Conv_U);
                        il.Emit(OpCodes.Br, done);
                        il.Append(zero);
                        il.Emit(OpCodes.Conv_U);
                        il.Append(done);
                    });
                    break;
                }
                case Kind.RefBool4:
                case Kind.RefBool2:
                case Kind.RefBool1:
                {
                    var nativeT = a.Kind == Kind.RefBool4 ? _int32 : a.Kind == Kind.RefBool2 ? _int16 : _byte;
                    var tmp = Local(body, nativeT);
                    // in: the current value (a ref) -- harmless for an out
                    il.Emit(OpCodes.Ldarg, argIndex);
                    il.Emit(OpCodes.Ldind_U1);
                    if (a.Kind == Kind.RefBool2) { il.Emit(OpCodes.Ldc_I4_0); il.Emit(OpCodes.Cgt_Un); il.Emit(OpCodes.Neg); }
                    il.Emit(OpCodes.Stloc, tmp);
                    pushes.Add(() => { il.Emit(OpCodes.Ldloca, tmp); il.Emit(OpCodes.Conv_U); });
                    after.Add(() =>
                    {
                        il.Emit(OpCodes.Ldarg, argIndex);
                        il.Emit(OpCodes.Ldloc, tmp);
                        il.Emit(OpCodes.Ldc_I4_0);
                        il.Emit(OpCodes.Cgt_Un);
                        il.Emit(OpCodes.Stind_I1);
                    });
                    break;
                }
                case Kind.Variant:
                {
                    var v = Local(body, _variant);
                    il.Emit(OpCodes.Ldarg, argIndex);
                    il.Emit(OpCodes.Ldloca, v); il.Emit(OpCodes.Conv_U);
                    il.Emit(OpCodes.Call, _toVariant);
                    pushes.Add(() => il.Emit(OpCodes.Ldloc, v));            // by value, as COM passes it
                    cleanup.Add(() => { il.Emit(OpCodes.Ldloca, v); il.Emit(OpCodes.Conv_U); il.Emit(OpCodes.Call, _clearVariant); });
                    break;
                }
                case Kind.RefVariant:
                {
                    var v = Local(body, _variant);
                    il.Emit(OpCodes.Ldarg, argIndex);
                    il.Emit(OpCodes.Ldind_Ref);
                    il.Emit(OpCodes.Ldloca, v); il.Emit(OpCodes.Conv_U);
                    il.Emit(OpCodes.Call, _toVariant);
                    pushes.Add(() => { il.Emit(OpCodes.Ldloca, v); il.Emit(OpCodes.Conv_U); });
                    after.Add(() =>
                    {
                        il.Emit(OpCodes.Ldarg, argIndex);
                        il.Emit(OpCodes.Ldloca, v); il.Emit(OpCodes.Conv_U);
                        il.Emit(OpCodes.Call, _fromVariant);
                        il.Emit(OpCodes.Stind_Ref);
                    });
                    cleanup.Add(() => { il.Emit(OpCodes.Ldloca, v); il.Emit(OpCodes.Conv_U); il.Emit(OpCodes.Call, _clearVariant); });
                    break;
                }
                case Kind.SafeArray:
                {
                    var tmp = Local(body, _intPtr);
                    il.Emit(OpCodes.Ldarg, argIndex);
                    il.Emit(OpCodes.Ldc_I4, a.Vt);
                    il.Emit(OpCodes.Call, _toSafeArray);
                    il.Emit(OpCodes.Stloc, tmp);
                    pushes.Add(() => il.Emit(OpCodes.Ldloc, tmp));
                    cleanup.Add(() => { il.Emit(OpCodes.Ldloc, tmp); il.Emit(OpCodes.Call, _destroySafeArray); });
                    break;
                }
                case Kind.RefSafeArray:
                {
                    // in: the current array (an out's is null); out: the callee's (it may have replaced ours),
                    // destroyed in the finally either way
                    var elem = ((ByReferenceType)managed).ElementType;
                    var tmp = Local(body, _intPtr);
                    il.Emit(OpCodes.Ldarg, argIndex);
                    il.Emit(OpCodes.Ldind_Ref);
                    il.Emit(OpCodes.Ldc_I4, a.Vt);
                    il.Emit(OpCodes.Call, _toSafeArray);
                    il.Emit(OpCodes.Stloc, tmp);
                    pushes.Add(() => { il.Emit(OpCodes.Ldloca, tmp); il.Emit(OpCodes.Conv_U); });
                    after.Add(() =>
                    {
                        il.Emit(OpCodes.Ldarg, argIndex);
                        il.Emit(OpCodes.Ldloc, tmp);
                        il.Emit(OpCodes.Ldtoken, elem);
                        il.Emit(OpCodes.Call, _fromSafeArray);
                        il.Emit(OpCodes.Castclass, elem);
                        il.Emit(OpCodes.Stind_Ref);
                    });
                    cleanup.Add(() => { il.Emit(OpCodes.Ldloc, tmp); il.Emit(OpCodes.Call, _destroySafeArray); });
                    break;
                }
                case Kind.StrB:
                {
                    var tmp = Local(body, _intPtr);
                    il.Emit(OpCodes.Ldarg, argIndex);
                    il.Emit(OpCodes.Call, _toBstr);
                    il.Emit(OpCodes.Stloc, tmp);
                    pushes.Add(() => il.Emit(OpCodes.Ldloc, tmp));
                    cleanup.Add(() => { il.Emit(OpCodes.Ldloc, tmp); il.Emit(OpCodes.Call, _freeBstr); });
                    break;
                }
                case Kind.RefStrB:
                case Kind.RefStrW:
                {
                    // in: the current string (a ref; an out's is null); out: the callee's, which the caller frees
                    var tmp = Local(body, _intPtr);
                    il.Emit(OpCodes.Ldarg, argIndex);
                    il.Emit(OpCodes.Ldind_Ref);
                    il.Emit(OpCodes.Call, a.Kind == Kind.RefStrB ? _toBstr : _strToUni);
                    il.Emit(OpCodes.Stloc, tmp);
                    pushes.Add(() => { il.Emit(OpCodes.Ldloca, tmp); il.Emit(OpCodes.Conv_U); });
                    after.Add(() =>
                    {
                        il.Emit(OpCodes.Ldarg, argIndex);
                        il.Emit(OpCodes.Ldloc, tmp);
                        il.Emit(OpCodes.Call, a.Kind == Kind.RefStrB ? _takeBstr : _takeCoTaskUni);
                        il.Emit(OpCodes.Stind_Ref);
                        il.Emit(OpCodes.Ldc_I4_0); il.Emit(OpCodes.Conv_I); il.Emit(OpCodes.Stloc, tmp);      // taken
                    });
                    cleanup.Add(() =>
                    {
                        // what the callee did not hand back (it failed): ours to free
                        il.Emit(OpCodes.Ldloc, tmp);
                        il.Emit(OpCodes.Call, a.Kind == Kind.RefStrB ? _freeBstr : _freeCoTask);
                    });
                    break;
                }
                case Kind.StrW:
                case Kind.StrA:
                {
                    var tmp = Local(body, _intPtr);
                    il.Emit(OpCodes.Ldarg, argIndex);
                    il.Emit(OpCodes.Call, a.Kind == Kind.StrW ? _strToUni : _strToAnsi);
                    il.Emit(OpCodes.Stloc, tmp);
                    pushes.Add(() => il.Emit(OpCodes.Ldloc, tmp));
                    cleanup.Add(() => { il.Emit(OpCodes.Ldloc, tmp); il.Emit(OpCodes.Call, _freeCoTask); });
                    break;
                }
            }
        }

        /// <summary>The native value on the stack, converted to the managed type.</summary>
        private void FromNative(ILProcessor il, Arg a, TypeReference managed)
        {
            switch (a.Kind)
            {
                case Kind.Bool4: case Kind.Bool1: case Kind.Bool2:
                    il.Emit(OpCodes.Ldc_I4_0); il.Emit(OpCodes.Cgt_Un);
                    break;
                case Kind.Iface: case Kind.Unknown:
                    il.Emit(OpCodes.Call, _take);
                    if (a.Iface != null) il.Emit(OpCodes.Castclass, a.Iface);
                    break;
                case Kind.StrB:
                    il.Emit(OpCodes.Call, _takeBstr);
                    break;
                case Kind.SafeArray:
                    il.Emit(OpCodes.Ldtoken, managed);
                    il.Emit(OpCodes.Call, _takeSafeArray);
                    il.Emit(OpCodes.Castclass, managed);
                    break;
                case Kind.StrW:
                    il.Emit(OpCodes.Call, _takeCoTaskUni);
                    break;
            }
        }

        private void Refuse(ILProcessor il, string member, string why)
        {
            _r.Unsupported.Add($"{member}: {why}");
            il.Body.ExceptionHandlers.Clear();
            il.Body.Instructions.Clear();
            il.Emit(OpCodes.Ldstr, member);
            il.Emit(OpCodes.Ldstr, why);
            il.Emit(OpCodes.Call, _unsupported);
            il.Emit(OpCodes.Throw);
        }

        private static VariableDefinition Local(MethodDefinition m, TypeReference t)
        {
            var v = new VariableDefinition(t);
            m.Body.Variables.Add(v);
            return v;
        }

        // ---- P/Invokes and delegates ------------------------------------------------------------

        /// <summary>A parameter of a P/Invoke or delegate: a COM interface becomes a pointer (by value,
        /// out); everything else stays as it is, for the built-in marshaller.</summary>
        private TypeReference NativeOf(TypeReference t, out bool changed)
        {
            changed = false;
            if (t is ByReferenceType br && IsComInterface(br.ElementType)) { changed = true; return new ByReferenceType(_intPtr); }
            if (IsComInterface(t)) { changed = true; return _intPtr; }
            return t;
        }

        private void RewritePInvoke(MethodDefinition p)
        {
            foreach (var prm in p.Parameters)
                if (prm.ParameterType is ByReferenceType b && IsComInterface(b.ElementType) && !prm.IsOut)
                { _r.Unsupported.Add($"P/Invoke {p.DeclaringType.FullName}.{p.Name}: ref interface {prm.Name}"); return; }
            if (AnalyzeOnly) { _r.PInvokes++; return; }
            var info = p.PInvokeInfo;
            var native = new MethodDefinition(p.Name + "<native>", p.Attributes, NativeOf(p.ReturnType, out _))
            {
                ImplAttributes = p.ImplAttributes,
                // the entry point by its name, which was the method's when none was given
                PInvokeInfo = new PInvokeInfo(info.Attributes, string.IsNullOrEmpty(info.EntryPoint) ? p.Name : info.EntryPoint, info.Module),
            };
            foreach (var prm in p.Parameters)
            {
                var np = new ParameterDefinition(prm.Name, prm.Attributes, NativeOf(prm.ParameterType, out bool ch));
                if (!ch && prm.HasMarshalInfo) np.MarshalInfo = prm.MarshalInfo;
                else np.Attributes &= ~ParameterAttributes.HasFieldMarshal;
                native.Parameters.Add(np);
            }
            if (!IsComInterface(p.ReturnType) && p.MethodReturnType.HasMarshalInfo) native.MethodReturnType.MarshalInfo = p.MethodReturnType.MarshalInfo;
            p.DeclaringType.Methods.Add(native);

            // the P/Invoke becomes a managed method that converts and calls it
            // (in this order: Cecil's PInvokeInfo setter sets the PInvokeImpl flag, even to null)
            p.PInvokeInfo = null;
            p.IsPInvokeImpl = false;
            p.ImplAttributes = MethodImplAttributes.IL | MethodImplAttributes.Managed;
            p.Body = new MethodBody(p);
            EmitForward(p, p.IsStatic ? null : default(int?), native, p.Parameters, p.ReturnType, isStatic: true);
            _r.PInvokes++;
        }

        private void MaybeRewriteDelegate(TypeDefinition d)
        {
            var invoke = d.Methods.FirstOrDefault(m => m.Name == "Invoke");
            if (invoke == null || !SignatureHasCom(invoke)) return;
            foreach (var prm in invoke.Parameters)
                if (prm.ParameterType is ByReferenceType b && IsComInterface(b.ElementType) && !prm.IsOut)
                { _r.Unsupported.Add($"delegate {d.FullName}: ref interface {prm.Name}"); return; }
            if (AnalyzeOnly) { _rewrittenDelegates.Add(d); _r.Delegates++; return; }
            // the shim callers use: the delegate with its old signature
            var shim = new MethodDefinition($"{d.Name}<Invoke>", MethodAttributes.Assembly | MethodAttributes.Static | MethodAttributes.HideBySig, invoke.ReturnType);
            shim.Parameters.Add(new ParameterDefinition("d", ParameterAttributes.None, d));
            foreach (var prm in invoke.Parameters) shim.Parameters.Add(new ParameterDefinition(prm.Name, prm.Attributes & ~ParameterAttributes.HasFieldMarshal, prm.ParameterType));
            var oldParams = shim.Parameters.Skip(1).ToList();
            var oldRet = invoke.ReturnType;
            // the delegate takes pointers now
            foreach (var m in d.Methods.Where(m => m.Name is "Invoke" or "BeginInvoke" or "EndInvoke"))
            {
                foreach (var prm in m.Parameters)
                {
                    prm.ParameterType = NativeOf(prm.ParameterType, out bool ch);
                    if (ch) { prm.MarshalInfo = null; prm.Attributes &= ~ParameterAttributes.HasFieldMarshal; }
                }
                if (m.Name != "BeginInvoke") m.ReturnType = NativeOf(m.ReturnType, out _);
            }
            Shims.Methods.Add(shim);
            EmitForward(shim, 0, invoke, oldParams, oldRet, isStatic: false, firstArg: 1);
            _invokeShims[invoke] = shim;
            _rewrittenDelegates.Add(d);
            _r.Delegates++;
        }

        /// <summary>The body of <paramref name="stub"/>: converts its parameters (old types) for
        /// <paramref name="target"/> (pointers), calls it, converts back.</summary>
        private void EmitForward(MethodDefinition stub, int? targetArg, MethodReference target, IList<ParameterDefinition> oldParams, TypeReference oldRet, bool isStatic, int firstArg = 0)
        {
            stub.Body.InitLocals = true;
            var il = stub.Body.GetILProcessor();
            var cleanup = new List<Action>();
            var after = new List<Action>();
            var pushes = new List<Action>();
            if (!isStatic) pushes.Add(() => il.Emit(OpCodes.Ldarg, targetArg!.Value));
            for (int i = 0; i < oldParams.Count; i++)
            {
                int argIndex = i + firstArg;
                var t = oldParams[i].ParameterType;
                if (t is ByReferenceType br && IsComInterface(br.ElementType)) Prepare(stub, il, new Arg(Kind.OutIface, _intPtr, br.ElementType), argIndex, t, pushes, cleanup, after);
                else if (IsComInterface(t)) Prepare(stub, il, new Arg(Kind.Iface, _intPtr, t), argIndex, t, pushes, cleanup, after);
                else pushes.Add(() => il.Emit(OpCodes.Ldarg, argIndex));
            }
            Instruction? tryStart = cleanup.Count > 0 ? il.Create(OpCodes.Nop) : null;
            if (tryStart != null) il.Append(tryStart);
            foreach (var p in pushes) p();
            il.Emit(isStatic ? OpCodes.Call : OpCodes.Callvirt, target);
            VariableDefinition? r = null;
            if (oldRet.FullName != "System.Void") { r = Local(stub, oldRet); if (IsComInterface(oldRet)) { il.Emit(OpCodes.Call, _take); il.Emit(OpCodes.Castclass, oldRet); } il.Emit(OpCodes.Stloc, r); }
            foreach (var a in after) a();
            if (tryStart != null)
            {
                var leaveTarget = il.Create(OpCodes.Nop);
                il.Emit(OpCodes.Leave, leaveTarget);
                var handlerStart = il.Create(OpCodes.Nop);
                il.Append(handlerStart);
                foreach (var c in cleanup) c();
                il.Emit(OpCodes.Endfinally);
                il.Append(leaveTarget);
                stub.Body.ExceptionHandlers.Add(new ExceptionHandler(ExceptionHandlerType.Finally) { TryStart = tryStart, TryEnd = handlerStart, HandlerStart = handlerStart, HandlerEnd = leaveTarget });
            }
            if (r != null) il.Emit(OpCodes.Ldloc, r);
            il.Emit(OpCodes.Ret);
        }

        // ---- call sites -------------------------------------------------------------------------

        private static readonly HashSet<string> s_marshal = new()
        {
            "GetObjectForIUnknown", "GetTypedObjectForIUnknown", "GetUniqueObjectForIUnknown", "GetIUnknownForObject",
            "GetComInterfaceForObject", "ReleaseComObject", "FinalReleaseComObject", "IsComObject",
        };

        private void RewriteCallSites(MethodDefinition md)
        {
            if (md.DeclaringType == _shims) return;
            var il = md.Body.GetILProcessor();
            foreach (var ins in md.Body.Instructions.ToList())
            {
                if ((ins.OpCode == OpCodes.Call || ins.OpCode == OpCodes.Callvirt) && ins.Operand is MethodReference mr)
                {
                    // a rewritten delegate's Invoke
                    var def = SafeM(mr);
                    if (def != null && _invokeShims.TryGetValue(def, out var shim) && md != shim)
                    {
                        ins.OpCode = OpCodes.Call; ins.Operand = shim; _r.CallSites++;
                        continue;
                    }
                    // where the assembly came from: it is loaded from memory (Com.AssemblyLocation and kin)
                    if (LocationReplacement(mr) is { } loc)
                    {
                        ins.OpCode = OpCodes.Call; ins.Operand = loc; _r.CallSites++;
                        continue;
                    }
                    // Marshal's COM members
                    if (mr.DeclaringType.FullName == "System.Runtime.InteropServices.Marshal" && s_marshal.Contains(mr.Name))
                    {
                        var repl = ComReplacement(mr);
                        if (repl != null) { ins.Operand = repl; ins.OpCode = OpCodes.Call; _r.CallSites++; }
                        continue;
                    }
                    if (def != null && !def.DeclaringType.IsInterface && _comTypes.Contains(def.DeclaringType) && !def.IsConstructor)
                        _r.Unsupported.Add($"{md.DeclaringType.FullName}.{md.Name}: calls {def.DeclaringType.Name}.{def.Name} on a COM class");
                }
                else if (ins.OpCode == OpCodes.Newobj && ins.Operand is MethodReference ctor)
                {
                    var t = Safe(ctor.DeclaringType);
                    if (t == null) continue;
                    if (_comTypes.Contains(t) && !t.IsInterface)
                    {
                        var g = t.CustomAttributes.FirstOrDefault(a => a.AttributeType.FullName == "System.Runtime.InteropServices.GuidAttribute");
                        if (g == null) { _r.Unsupported.Add($"new {t.FullName}: a COM class without a Guid"); continue; }
                        // new C()  ->  Com.CreateInstance(new Guid("..."))
                        // in place, so that a branch to the newobj reaches the whole sequence
                        var call = il.Create(OpCodes.Call, _createInstance);
                        var guid = il.Create(OpCodes.Newobj, _guidCtor);
                        ins.OpCode = OpCodes.Ldstr; ins.Operand = (string)g.ConstructorArguments[0].Value;
                        il.InsertAfter(ins, guid);
                        il.InsertAfter(guid, call);
                        _r.CallSites++;
                    }
                    else if (_rewrittenDelegates.Contains(t))
                        _r.Unsupported.Add($"{md.DeclaringType.FullName}.{md.Name}: makes a managed {t.Name} delegate (native code calling back with COM interfaces)");
                }
                else if ((ins.OpCode == OpCodes.Castclass || ins.OpCode == OpCodes.Isinst) && ins.Operand is TypeReference tr)
                {
                    var t = Safe(tr);
                    if (t != null && _comTypes.Contains(t) && !t.IsInterface)
                        _r.Unsupported.Add($"{md.DeclaringType.FullName}.{md.Name}: casts to the COM class {t.Name}");
                    // An object of unknown origin cast to a rewritten interface may be a built-in RCW (from
                    // another assembly, an ActiveX control, the BCL), which would no longer convert: it is
                    // adopted first (Com.Adopt: the same native object, as a ComObject).
                    else if (t != null && _comTypes.Contains(t) && !FromComWrapper(ins.Previous) && !IsCom(StackType(md, ins.Previous)))
                    {
                        // in place (a branch to the cast reaches the adoption), then the cast again
                        var cast = il.Create(ins.OpCode, (TypeReference)ins.Operand);
                        ins.OpCode = OpCodes.Call; ins.Operand = _adopt;
                        il.InsertAfter(ins, cast);
                        _r.CallSites++;

                    }
                }
            }
        }

        /// <summary>The static type of what <paramref name="prev"/> pushed, where it says (a call's return, a
        /// local, an argument, a field); null otherwise.</summary>
        private static TypeReference? StackType(MethodDefinition md, Instruction? prev)
        {
            if (prev == null) return null;
            switch (prev.Operand)
            {
                case MethodReference m when prev.OpCode == OpCodes.Call || prev.OpCode == OpCodes.Callvirt: return m.ReturnType;
                case VariableDefinition v: return v.VariableType;
                case ParameterDefinition p: return p.ParameterType;
                case FieldReference f when prev.OpCode == OpCodes.Ldfld || prev.OpCode == OpCodes.Ldsfld: return f.FieldType;
            }
            var c = prev.OpCode.Code;
            if (c is Code.Ldloc_0 or Code.Ldloc_1 or Code.Ldloc_2 or Code.Ldloc_3)
            {
                int i = c - Code.Ldloc_0;
                return i < md.Body.Variables.Count ? md.Body.Variables[i].VariableType : null;
            }
            if (c is Code.Ldarg_0 or Code.Ldarg_1 or Code.Ldarg_2 or Code.Ldarg_3)
            {
                int i = c - Code.Ldarg_0 - (md.HasThis ? 1 : 0);
                if (i < 0) return md.DeclaringType;
                return i < md.Parameters.Count ? md.Parameters[i].ParameterType : null;
            }
            return null;
        }

        private MethodReference? LocationReplacement(MethodReference mr)
        {
            if (!mr.HasThis) return null;
            string? helper = (mr.DeclaringType.FullName, mr.Name, mr.Parameters.Count) switch
            {
                ("System.Reflection.Assembly", "get_Location", 0) => nameof(Com.AssemblyLocation),
                ("System.Reflection.Assembly", "get_CodeBase", 0) => nameof(Com.AssemblyCodeBase),
                ("System.Reflection.Assembly", "get_EscapedCodeBase", 0) => nameof(Com.AssemblyEscapedCodeBase),
                ("System.Reflection.Assembly", "GetName", 0) => nameof(Com.AssemblyGetName),
                ("System.Reflection.Assembly", "GetName", 1) => nameof(Com.AssemblyGetName),
                ("System.Reflection.Module", "get_FullyQualifiedName", 0) => nameof(Com.ModuleFullyQualifiedName),
                ("System.Reflection.Module", "get_Name", 0) => nameof(Com.ModuleName),
                _ => null,
            };
            if (helper == null) return null;
            var self = mr.DeclaringType.Name == "Assembly" ? typeof(System.Reflection.Assembly) : typeof(System.Reflection.Module);
            var args = new[] { self }.Concat(mr.Parameters.Select(_ => typeof(bool))).ToArray();
            return _m.ImportReference(typeof(Com).GetMethod(helper, args)!);
        }

        private static bool FromComWrapper(Instruction? prev)
            => prev != null && prev.OpCode == OpCodes.Call && prev.Operand is MethodReference m
               && m.DeclaringType.FullName == typeof(Com).FullName && (m.Name.Contains("ForIUnknown") || m.Name is nameof(Com.Take) or nameof(Com.Adopt));

        private static MethodDefinition? SafeM(MethodReference m) { try { return m.Resolve(); } catch { return null; } }

        private MethodReference? ComReplacement(MethodReference mr)
        {
            var com = typeof(Com);
            var types = mr.Parameters.Select(p => p.ParameterType.FullName).ToArray();
            foreach (var cand in com.GetMethods().Where(x => x.Name == mr.Name))
            {
                var ps = cand.GetParameters();
                if (ps.Length != types.Length) continue;
                if (cand.IsGenericMethodDefinition != mr is GenericInstanceMethod && !(mr.HasGenericParameters && cand.IsGenericMethodDefinition)) continue;
                bool ok = true;
                for (int i = 0; i < ps.Length && ok; i++)
                {
                    string want = ps[i].ParameterType.IsGenericParameter ? "!!" + ps[i].ParameterType.GenericParameterPosition : ps[i].ParameterType.FullName!;
                    string got = types[i];
                    ok = want == got || (want.StartsWith("!!") && got.StartsWith("!!"));
                }
                if (!ok) continue;
                if (mr is GenericInstanceMethod gim)
                {
                    var gm = new GenericInstanceMethod(_m.ImportReference(cand));
                    foreach (var a in gim.GenericArguments) gm.GenericArguments.Add(a);
                    return gm;
                }
                return _m.ImportReference(cand);
            }
            _r.Unsupported.Add($"Marshal.{mr.Name}({string.Join(", ", types)}) has no replacement");
            return null;
        }

        private void RewriteCoClass(TypeDefinition c)
        {
            // the class no longer comes from COM: its instances are ComObjects made by CreateInstance
            c.IsImport = false;
            foreach (var m in c.Methods)
                if (m.IsRuntime || m.IsInternalCall)
                {
                    m.ImplAttributes = MethodImplAttributes.IL | MethodImplAttributes.Managed;
                    m.Body = new MethodBody(m);
                    var il = m.Body.GetILProcessor();
                    if (m.IsConstructor && m.Parameters.Count == 0)
                    {
                        il.Emit(OpCodes.Ldarg_0);
                        il.Emit(OpCodes.Call, _m.ImportReference(typeof(object).GetConstructor(Type.EmptyTypes)!));
                        il.Emit(OpCodes.Ret);
                    }
                    else
                    {
                        il.Emit(OpCodes.Ldstr, $"{c.FullName}.{m.Name}");
                        il.Emit(OpCodes.Ldstr, "a COM class's own method");
                        il.Emit(OpCodes.Call, _unsupported);
                        il.Emit(OpCodes.Throw);
                    }
                }
        }
    }
}
