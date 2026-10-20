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
    /// <summary>Members built-in COM interop cannot marshal either: they throw when called, as with built-in
    /// interop, and do not keep the assembly on built-in COM.</summary>
    public List<string> BuiltInCannot { get; } = new();
    /// <summary>Assemblies whose COM types this one uses: it may be rewritten only together with them
    /// (their objects must be ComObjects too). The caller decides over the whole app.</summary>
    public SortedSet<string> UsesComTypesOf { get; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Written to the output path.</summary>
    public bool Written;
    public int Interfaces, Methods, PInvokes, Delegates, CallSites, EventInterfaces;

    public override string ToString() => !UsesComImport ? "no ComImport"
        : $"{Interfaces} interfaces ({Methods} methods), {PInvokes} P/Invokes, {Delegates} delegates, {CallSites} call sites" + (EventInterfaces > 0 ? $", {EventInterfaces} event interfaces" : "") +
          (UsesComTypesOf.Count == 0 ? "" : $"; uses the COM types of {string.Join(", ", UsesComTypesOf)}") +
          (BuiltInCannot.Count == 0 ? "" : $"; {BuiltInCannot.Count} that built-in interop cannot marshal either (they throw as there)") +
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
        => Analyze(input, searchDirs, externallyReferenced, force: false);

    /// <param name="force">judge it even when it declares no COM types: an assembly that uses other assemblies'
    /// COM types and makes RCWs of its own, rewritten together with them (ComImportStep's group)</param>
    public static RewriteResult Analyze(string input, IEnumerable<string> searchDirs, ISet<string>? externallyReferenced, bool force)
    {
        var result = new RewriteResult();
        var resolver = new DefaultAssemblyResolver();
        foreach (string d in searchDirs) if (Directory.Exists(d)) resolver.AddSearchDirectory(d);
        resolver.AddSearchDirectory(Path.GetDirectoryName(Path.GetFullPath(input))!);
        using var module = ModuleDefinition.ReadModule(input, new ReaderParameters { AssemblyResolver = resolver, ReadingMode = ReadingMode.Deferred, InMemory = true });
        if (!force && !module.Types.SelectMany(AllTypes).Any(t => t.IsImport)) return result;
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
        private readonly MethodReference _dispInvoke, _dispatchResult, _borrowDispatch, _eventProvider, _registerGenerated, _takeEnumerable, _takeEnumerator;
        private readonly MethodReference _structToNative, _newStruct, _structFromNative, _freeStruct, _structBack, _structArrayToNative, _structArrayFromNative,
            _freeStructArray, _lengthOf, _customMarshaler, _customToNative, _customFromNative, _customCleanUp, _sbToNative, _sbFromNative, _freeCoTaskMem,
            _elemToNative, _elemFromNative, _freeElem, _blitFromNativeN, _blitToNative, _elemFromNativeN, _elemBack, _structFromNativeN, _structArrayBack,
            _sbOfNative, _sbBack, _nativeRoom, _structToShadow, _freeStructAt, _structFromShadow;
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
            _takeEnumerable = Imp(nameof(Com.TakeEnumerable), typeof(IntPtr));
            _takeEnumerator = Imp(nameof(Com.TakeEnumerator), typeof(IntPtr));
            _registerGenerated = Imp(nameof(Com.RegisterGenerated), typeof(RuntimeTypeHandle), typeof(RuntimeTypeHandle), typeof(RuntimeTypeHandle));
            _structToNative = Imp(nameof(Com.StructToNative), typeof(object), typeof(RuntimeTypeHandle));
            _newStruct = Imp(nameof(Com.NewStruct), typeof(RuntimeTypeHandle));
            _structFromNative = Imp(nameof(Com.StructFromNative), typeof(IntPtr), typeof(object), typeof(RuntimeTypeHandle));
            _freeStruct = Imp(nameof(Com.FreeStruct), typeof(IntPtr), typeof(RuntimeTypeHandle));
            _structBack = Imp(nameof(Com.StructBackToNative), typeof(object), typeof(IntPtr), typeof(RuntimeTypeHandle));
            _structArrayToNative = Imp(nameof(Com.StructArrayToNative), typeof(Array), typeof(RuntimeTypeHandle));
            _structArrayFromNative = Imp(nameof(Com.StructArrayFromNative), typeof(IntPtr), typeof(Array), typeof(RuntimeTypeHandle));
            _freeStructArray = Imp(nameof(Com.FreeStructArray), typeof(IntPtr), typeof(int), typeof(RuntimeTypeHandle));
            _lengthOf = Imp(nameof(Com.LengthOf), typeof(Array));
            _customMarshaler = Imp(nameof(Com.CustomMarshaler), typeof(RuntimeTypeHandle), typeof(string));
            _customToNative = Imp(nameof(Com.CustomToNative), typeof(object), typeof(object));
            _customFromNative = Imp(nameof(Com.CustomFromNative), typeof(object), typeof(IntPtr));
            _customCleanUp = Imp(nameof(Com.CustomCleanUp), typeof(object), typeof(IntPtr));
            _sbToNative = Imp(nameof(Com.StringBuilderToNative), typeof(System.Text.StringBuilder), typeof(bool), typeof(bool));
            _sbFromNative = Imp(nameof(Com.StringBuilderFromNative), typeof(System.Text.StringBuilder), typeof(IntPtr), typeof(bool));
            _freeCoTaskMem = Imp(nameof(Com.FreeCoTaskMem), typeof(IntPtr));
            _elemToNative = Imp(nameof(Com.ElemArrayToNative), typeof(Array), typeof(int), typeof(RuntimeTypeHandle), typeof(bool));
            _elemFromNative = Imp(nameof(Com.ElemArrayFromNative), typeof(IntPtr), typeof(Array), typeof(int), typeof(RuntimeTypeHandle));
            _freeElem = Imp(nameof(Com.FreeElemArray), typeof(IntPtr), typeof(int), typeof(int));
            _blitFromNativeN = Imp(nameof(Com.BlitArrayFromNative), typeof(IntPtr), typeof(int), typeof(RuntimeTypeHandle));
            _blitToNative = Imp(nameof(Com.BlitArrayToNative), typeof(Array), typeof(IntPtr), typeof(int));
            _elemFromNativeN = Imp(nameof(Com.ElemArrayFromNativeN), typeof(IntPtr), typeof(int), typeof(int), typeof(RuntimeTypeHandle), typeof(bool));
            _elemBack = Imp(nameof(Com.ElemArrayBackToNative), typeof(Array), typeof(IntPtr), typeof(int), typeof(int), typeof(RuntimeTypeHandle));
            _structFromNativeN = Imp(nameof(Com.StructArrayFromNativeN), typeof(IntPtr), typeof(int), typeof(RuntimeTypeHandle), typeof(bool));
            _structArrayBack = Imp(nameof(Com.StructArrayBackToNative), typeof(Array), typeof(IntPtr), typeof(int));
            _sbOfNative = Imp(nameof(Com.StringBuilderOfNative), typeof(IntPtr), typeof(bool));
            _sbBack = Imp(nameof(Com.StringBuilderBackToNative), typeof(System.Text.StringBuilder), typeof(IntPtr), typeof(bool), typeof(int));
            _nativeRoom = Imp(nameof(Com.NativeStringRoom), typeof(IntPtr), typeof(bool));
            _structToShadow = Imp(nameof(Com.StructToShadow), typeof(object), typeof(IntPtr), typeof(int), typeof(RuntimeTypeHandle));
            _freeStructAt = Imp(nameof(Com.FreeStructAt), typeof(IntPtr), typeof(RuntimeTypeHandle));
            _structFromShadow = Imp(nameof(Com.StructFromShadow), typeof(IntPtr), typeof(int), typeof(RuntimeTypeHandle));
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
            CollectBclInterfaces();

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
                    if (IsBclCom(d) || IsBclGen(d)) continue;
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
            foreach (var b in _bclGen.ToList()) GenerateBcl(b);
            // a managed class implementing a COM interface: its CCW must cover every method
            foreach (var (cls, it) in _ccwUsers)
            {
                var d = Safe(it);
                if (d != null && IsBclCom(d)) continue;            // its CCW is WpfMac.ComInterop's
                if (d != null && IsBclGen(d))
                {
                    if (_ccwRefused.FirstOrDefault(kv => kv.Key.FullName == d.FullName).Value is { } bref) foreach (var why in bref) Reason($"managed class {cls.FullName}", $"implements {it.Name}, whose CCW cannot convert {why}");
                    continue;
                }
                if (d != null && IsForeignCom(d)) continue;         // its CCW is its own assembly's (rewritten with this one)
                if (d == null || !_ccwRefused.TryGetValue(d, out var refused)) { _r.Unsupported.Add($"managed class {cls.FullName} implements COM interface {it.Name} (no CCW)"); continue; }
                foreach (var why in refused) Reason($"managed class {cls.FullName}", $"implements {it.Name}, whose CCW cannot convert {why}");
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
            // Resolve() of an array (or pointer, or generic instance) gives its element's definition: not itself COM
            if (t is ArrayType or PointerType or GenericInstanceType or PinnedType or RequiredModifierType or OptionalModifierType) return false;
            TypeDefinition? d = t.Module == _m || t.Scope == _m ? Safe(t) : null;
            if (d != null && _comTypes.Contains(d)) return true;
            // another app assembly's COM interface: rewritten with this one (the judge's fixpoint), so its objects are
            // ComObjects here too
            if (d != null && IsForeignCom(d)) return true;
            return t is not GenericParameter && t.Namespace == "System.Runtime.InteropServices.ComTypes" && (d ?? Safe(t)) is { } b && (IsBclCom(b) || IsBclGen(b));
        }

        /// <summary>A ComImport interface of another app assembly (not the BCL's).</summary>
        private bool IsForeignCom(TypeDefinition d) => d.IsImport && d.IsInterface && d.Module != _m && !IsBcl(d);

        private static bool IsBcl(TypeDefinition d) => d.Module.Assembly.Name.Name is "System.Private.CoreLib" or "System.Runtime" or "System.Runtime.InteropServices" or "mscorlib" or "netstandard";

        /// <summary>A BCL COM interface WpfMac.ComInterop implements by hand (both ways).</summary>
        private static bool IsBclCom(TypeDefinition d) => d.IsImport && d.IsInterface && IsBcl(d) && s_bclCom.Contains(d.FullName);

        private static readonly HashSet<string> s_bclCom = new(WpfMac.ComInterop.Com.SupportedBclComTypes, StringComparer.Ordinal);

        private bool IsComInterface(TypeReference t) => IsCom(t) && Safe(t is ByReferenceType b ? b.ElementType : t)!.IsInterface;

        private static TypeDefinition? Safe(TypeReference t) { try { return t.Resolve(); } catch { return null; } }

        private static bool IsDelegate(TypeDefinition t) => t.BaseType?.FullName == "System.MulticastDelegate";

        private bool SignatureHasCom(MethodReference m) => IsCom(m.ReturnType) || m.Parameters.Any(p => IsCom(p.ParameterType));

        // ---- marshalling ------------------------------------------------------------------------

        private enum Kind { Blit, Bool4, Bool2, Bool1, Iface, Unknown, OutIface, ByRefBlit, StrW, StrA, StrB, BlitArray, RefBool4, RefBool2, RefBool1, RefStrB, RefStrW, Variant, RefVariant, SafeArray, RefSafeArray,
            LPStructBlit, FormattedClass, RefStruct, Custom, RefCustom, StringBuilder, StructArray, ElemArray,
            StructByValue, RefFormattedClass, RefStringBuilder, RefArray, Enumerable }

        /// <summary>A refusal's prefix when built-in interop cannot marshal it either.</summary>
        private const string BuiltInCannotPrefix = "[built-in cannot] ";

        private void Reason(string member, string why)
        {
            if (why.Contains(BuiltInCannotPrefix, StringComparison.Ordinal)) _r.BuiltInCannot.Add($"{member}: {why.Replace(BuiltInCannotPrefix, "")}");
            else _r.Unsupported.Add($"{member}: {why}");
        }

        // Arg.Vt's bits for the formatted kinds: copy back after the call (an [Out], or a blittable class,
        // which built-in interop pins), out only (nothing copied in), ANSI (a StringBuilder)
        private const int CopyBack = 1, OutOnly = 2, Ansi = 4;

        /// <summary>Kinds a forward call cannot return (a retval of these is refused).</summary>
        private static bool BadReturn(Kind k) => k is Kind.ByRefBlit or Kind.OutIface or Kind.StrA or Kind.BlitArray or Kind.RefBool1 or Kind.RefBool2
            or Kind.RefBool4 or Kind.RefStrB or Kind.RefStrW or Kind.LPStructBlit or Kind.RefStruct or Kind.Custom or Kind.RefArray
            or Kind.RefCustom or Kind.StringBuilder or Kind.StructArray or Kind.ElemArray or Kind.RefFormattedClass or Kind.RefStringBuilder;

        /// <summary>Kinds a CCW stub cannot take from native code (it does not know their extent or owner).</summary>
        private static bool BadReverseParam(Kind k) => k is Kind.Custom or Kind.RefCustom or Kind.RefFormattedClass or Kind.RefStringBuilder or Kind.RefArray or Kind.Enumerable;

        /// <summary>Why a CCW stub cannot take argument <paramref name="i"/> of <paramref name="md"/> (null: it can): an
        /// array needs its length (SizeParamIndex/SizeConst), an [Out]-only StringBuilder its buffer's size (the next
        /// parameter, by value).</summary>
        private static string? ReverseSizeWhy(Arg a, MethodDefinition md, int i)
        {
            if (a.Kind is Kind.BlitArray or Kind.StructArray or Kind.ElemArray)
            {
                // no SizeParamIndex or SizeConst: one element, as built-in reverse interop marshals it (the BCL's
                // IEnumFORMATETC.Next pceltFetched)
                if (a.SizeParam >= md.Parameters.Count || (a.SizeParam >= 0 && !IsCount(md.Parameters[a.SizeParam].ParameterType))) return "an array whose length parameter is not a by-value integer";
            }
            if (a.Kind == Kind.StringBuilder && (a.Vt & OutOnly) != 0 && SizeOfBuffer(md, i) < 0) return "an [Out] StringBuilder whose buffer's size is unknown";
            return null;
        }

        private static bool IsCount(TypeReference t) => t.FullName is "System.Int32" or "System.UInt32" or "System.Int16" or "System.UInt16" or "System.Int64" or "System.UInt64" or "System.IntPtr" or "System.UIntPtr";

        /// <summary>A StringBuilder's buffer size in characters: the next parameter when it is a by-value integer
        /// (the cch convention), else -1.</summary>
        private static int SizeOfBuffer(MethodDefinition md, int i) => i + 1 < md.Parameters.Count && IsCount(md.Parameters[i + 1].ParameterType) ? i + 1 : -1;

        /// <summary>The parameter's direction into a formatted kind's bits ([Out] copies back; [Out] alone: out only).</summary>
        private static Arg WithDirection(Arg a, ParameterDefinition p)
        {
            bool outAttr = p.IsOut, inAttr = p.IsIn;
            return a.Kind switch
            {
                Kind.FormattedClass => outAttr ? a with { Vt = a.Vt | CopyBack } : a,
                Kind.StructArray or Kind.ElemArray or Kind.BlitArray => a with { Vt = a.Vt | (outAttr ? CopyBack : 0) | (outAttr && !inAttr ? OutOnly : 0) },
                Kind.StringBuilder => outAttr && !inAttr ? a with { Vt = a.Vt | OutOnly } : a,
                Kind.Custom => outAttr ? a with { Vt = a.Vt | CopyBack } : a,
                Kind.RefStruct => outAttr && !inAttr ? a with { Vt = a.Vt | OutOnly } : a,
                _ => a,
            };
        }

        /// <param name="Vt">a SAFEARRAY's element VARTYPE (0: the managed element type's default, at run time)</param>
        /// <param name="Elem">an ElemArray's element kind (Com.ElemLPWStr ...)</param>
        /// <param name="SizeParam">a C array's SizeParamIndex (-1: none), <paramref name="SizeConst"/> its SizeConst (-1: none)</param>
        private sealed record Arg(Kind Kind, TypeReference Native, TypeReference? Iface = null, int Vt = 0, string? Cookie = null, int Elem = 0, int SizeParam = -1, int SizeConst = -1);

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

        /// <summary>A type Marshal.StructureToPtr converts with no COM: a struct or a class with sequential or
        /// explicit layout whose fields are blittable, bool, char, string, an enum, a fixed array or such a struct.</summary>
        private bool IsFormatted(TypeReference t, out string? why, int depth = 0)
        {
            why = null;
            var d = Safe(t);
            if (d == null) { why = "unresolved"; return false; }
            if (d.IsInterface || d.IsEnum || d.HasGenericParameters || depth > 8) { why = $"{t.Name} is not a formatted type"; return false; }
            if (!(d.IsSequentialLayout || d.IsExplicitLayout)) { why = $"{t.Name} has automatic layout"; return false; }
            if (!d.IsValueType && d.BaseType != null && d.BaseType.FullName != "System.Object" && !IsFormatted(d.BaseType, out why, depth + 1)) return false;
            foreach (var f in d.Fields)
            {
                if (f.IsStatic) continue;
                var ft = f.FieldType;
                if (IsBlittable(ft) || ft.FullName is "System.Boolean" or "System.Char" or "System.String" or "System.Decimal" or "System.DateTime") continue;
                if (ft is ArrayType fa && f.MarshalInfo is FixedArrayMarshalInfo && (IsBlittable(fa.ElementType) || fa.ElementType.FullName is "System.Boolean" or "System.Char")) continue;
                // an array that is not ByValArray: built-in struct marshalling makes it a SAFEARRAY (Marshal.StructureToPtr
                // does the same)
                if (ft is ArrayType) continue;
                // an interface (or object as IUnknown/IDispatch): Com.StructToNative passes its pointer itself
                if (IsComInterface(ft) || (ft.FullName == "System.Object" && f.MarshalInfo?.NativeType is NativeType.IUnknown or NativeType.IntF or NativeType.IDispatch)) continue;
                // object without [MarshalAs]: a VARIANT, as built-in struct marshalling (and StructureToPtr) makes it
                if (ft.FullName == "System.Object" && f.MarshalInfo == null) continue;
                // an embedded struct or class with layout
                if (Safe(ft) is { IsInterface: false, IsEnum: false } fd && (fd.IsValueType || fd.FullName != "System.Object") && !IsDelegate(fd) && IsFormatted(ft, out _, depth + 1)) continue;
                if (Safe(ft) is { } dd && IsDelegate(dd)) continue;           // a function pointer
                why = $"field {f.Name} ({ft.Name}) of {d.Name}"; return false;
            }
            return true;
        }

        private readonly Dictionary<TypeDefinition, TypeDefinition> _shadows = new();

        /// <summary>A blittable struct with <paramref name="t"/>'s native size and alignment (made once per type, in
        /// the rewritten module): what a by-value <paramref name="t"/> is passed as. Its fields are integers of the
        /// alignment's width, so it is classified as the integer aggregate the native one is.</summary>
        private TypeReference? Shadow(TypeReference t, out string? why)
        {
            why = null;
            var d = Safe(t)!;
            if (_shadows.TryGetValue(d, out var known)) return known;
            if (NativeLayout(t, 0) is not var (size, align))
            { why = $"{t.Name} by value: its native layout is not one the rewriter can compute"; return null; }
            if (AllFloat(d, 0)) { why = $"{t.Name} by value: a floating-point aggregate (passed in FP registers)"; return null; }
            if (AnalyzeOnly) return _m.TypeSystem.IntPtr;          // the verdict needs no type
            var sh = new TypeDefinition("WpfMac.Shadows", $"{d.Name}Native{_shadows.Count}",
                TypeAttributes.Public | TypeAttributes.Sealed | TypeAttributes.SequentialLayout | TypeAttributes.BeforeFieldInit,
                _m.ImportReference(typeof(ValueType))) { PackingSize = (short)align, ClassSize = size };
            var unit = align switch { 8 => _m.TypeSystem.Int64, 4 => _m.TypeSystem.Int32, 2 => _m.TypeSystem.Int16, _ => _m.TypeSystem.Byte };
            int n = Math.Max(1, size / Math.Max(1, align));
            for (int i = 0; i < n; i++) sh.Fields.Add(new FieldDefinition($"f{i}", FieldAttributes.Public, unit));
            for (int r = n * align; r < size; r++) sh.Fields.Add(new FieldDefinition($"b{r}", FieldAttributes.Public, _m.TypeSystem.Byte));
            _m.Types.Add(sh);
            _shadows[d] = sh;
            _shadowSizes[sh] = size;
            return sh;
        }

        private readonly Dictionary<TypeReference, int> _shadowSizes = new();

        private bool AllFloat(TypeDefinition d, int depth)
        {
            bool any = false;
            foreach (var f in d.Fields.Where(f => !f.IsStatic))
            {
                if (f.FieldType.FullName is "System.Single" or "System.Double") { any = true; continue; }
                if (depth < 4 && f.FieldType.IsValueType && Safe(f.FieldType) is { IsEnum: false } fd && AllFloat(fd, depth + 1)) { any = true; continue; }
                return false;
            }
            return any;
        }

        /// <summary>The native size and alignment of <paramref name="t"/> as built-in struct marshalling lays it out
        /// (checked against Marshal.SizeOf at run time before use); null when it is not one computed here.</summary>
        private (int Size, int Align)? NativeLayout(TypeReference t, int depth)
        {
            int ptr = IntPtr.Size;
            if (depth > 8) return null;
            if (t.IsPointer || t.IsFunctionPointer) return (ptr, ptr);
            switch (t.FullName)
            {
                case "System.Byte": case "System.SByte": return (1, 1);
                case "System.Int16": case "System.UInt16": return (2, 2);
                case "System.Int32": case "System.UInt32": case "System.Single": case "System.Boolean": return (4, 4);
                case "System.Int64": case "System.UInt64": case "System.Double": case "System.DateTime": return (8, 8);
                case "System.IntPtr": case "System.UIntPtr": return (ptr, ptr);
                case "System.Guid": return (16, 4);
                case "System.Decimal": return (16, 8);
            }
            var d = Safe(t);
            if (d == null) return null;
            if (d.IsEnum) return NativeLayout(d.Fields.First(f => !f.IsStatic).FieldType, depth + 1);
            if (!(d.IsSequentialLayout || d.IsExplicitLayout)) return null;
            bool unicode = (d.Attributes & TypeAttributes.StringFormatMask) == TypeAttributes.UnicodeClass;
            int pack = d.PackingSize > 0 ? d.PackingSize : 8;
            int offset = 0, size = 0, align = 1;
            if (!d.IsValueType && d.BaseType != null && d.BaseType.FullName != "System.Object")
            {
                if (NativeLayout(d.BaseType, depth + 1) is not var (bs, ba)) return null;
                offset = size = bs; align = ba;
            }
            foreach (var f in d.Fields.Where(f => !f.IsStatic))
            {
                if (FieldLayout(f, unicode, depth) is not var (fs, fa)) return null;
                fa = Math.Min(fa, pack);
                int start = d.IsExplicitLayout ? f.Offset : (offset + fa - 1) / fa * fa;
                offset = start + fs;
                size = Math.Max(size, offset);
                align = Math.Max(align, fa);
            }
            size = (size + align - 1) / align * align;
            if (d.ClassSize > 0) size = Math.Max(size, d.ClassSize);
            return (Math.Max(size, 1), align);
        }

        private (int Size, int Align)? FieldLayout(FieldDefinition f, bool unicode, int depth)
        {
            int ptr = IntPtr.Size;
            var mi = f.MarshalInfo;
            var nt = mi?.NativeType ?? NativeType.None;
            var ft = f.FieldType;
            switch (ft.FullName)
            {
                case "System.Boolean": return nt switch { NativeType.U1 or NativeType.I1 => (1, 1), NativeType.VariantBool => (2, 2), _ => (4, 4) };
                case "System.Char": return unicode || nt is NativeType.U2 or NativeType.I2 ? (2, 2) : (1, 1);
                case "System.String":
                    if (mi is FixedSysStringMarshalInfo fss) { int cs = unicode || nt == NativeType.FixedSysString && unicode ? 2 : (unicode ? 2 : 1); return (fss.Size * cs, cs); }
                    return (ptr, ptr);
                case "System.Object": return nt is NativeType.IUnknown or NativeType.IntF or NativeType.IDispatch ? (ptr, ptr) : (ptr == 8 ? 24 : 16, 8);
            }
            if (ft is ArrayType at)
            {
                if (mi is FixedArrayMarshalInfo fam)
                {
                    var el = at.ElementType.FullName == "System.Boolean" ? (4, 4) : at.ElementType.FullName == "System.Char" ? (unicode ? (2, 2) : (1, 1)) : NativeLayout(at.ElementType, depth + 1);
                    if (el is not var (es, ea)) return null;
                    return (es * Math.Max(1, fam.Size), ea);
                }
                return (ptr, ptr);
            }
            if (IsComInterface(ft)) return (ptr, ptr);
            if (Safe(ft) is { } fd && IsDelegate(fd)) return (ptr, ptr);
            return NativeLayout(ft, depth + 1);
        }

        /// <summary>A formatted class whose fields (and its bases') are all blittable: built-in interop pins it.</summary>
        private bool IsBlittableClass(TypeDefinition d)
        {
            for (var x = d; x != null && x.FullName != "System.Object"; x = x.BaseType == null ? null : Safe(x.BaseType))
                foreach (var f in x.Fields)
                    if (!f.IsStatic && (f.HasMarshalInfo || !IsBlittable(f.FieldType))) return false;
            return true;
        }

        /// <summary>How built-in COM interop marshals parameter <paramref name="p"/> of a COM interface method,
        /// as one of the kinds here, or why it cannot.</summary>
        private Arg? Classify(TypeReference type, MarshalInfo? mi, bool comDefaults, out string? why)
        {
            why = null;
            var nt = mi?.NativeType ?? NativeType.None;
            // modifiers (an `in` parameter's modreq(InAttribute)) do not change the marshalling
            while (type is RequiredModifierType or OptionalModifierType) type = ((TypeSpecification)type).ElementType;
            if (type is ByReferenceType mbr && mbr.ElementType is RequiredModifierType or OptionalModifierType)
            {
                var inner = mbr.ElementType;
                while (inner is RequiredModifierType or OptionalModifierType) inner = ((TypeSpecification)inner).ElementType;
                type = new ByReferenceType(inner);
            }
            if (mi is CustomMarshalInfo cmi)
                return new Arg(type is ByReferenceType ? Kind.RefCustom : Kind.Custom, _intPtr, cmi.ManagedType, 0, cmi.Cookie);
            if (type is ByReferenceType br)
            {
                var e = br.ElementType;
                if (IsComInterface(e)) return new Arg(Kind.OutIface, _intPtr, e);
                if (e.FullName == "System.Object" && nt is NativeType.IUnknown or NativeType.IntF) return new Arg(Kind.OutIface, _intPtr, null);
                if (e.FullName == "System.Object" && nt is NativeType.IDispatch) return new Arg(Kind.OutIface, _intPtr, null, 9);
                if (IsBlittable(e) && e.FullName != "System.Boolean") return new Arg(Kind.ByRefBlit, _intPtr);
                string? fw = null;
                if (e.IsValueType && IsFormatted(e, out fw)) return new Arg(Kind.RefStruct, _intPtr, e);
                // a formatted class by reference, and a StringBuilder by reference: built-in interop passes a pointer
                // to the pointer to its native form
                if (!e.IsValueType && Safe(e) is { IsClass: true } rc && rc.FullName is not ("System.String" or "System.Object" or "System.Text.StringBuilder")
                    && e is not ArrayType && IsFormatted(e, out _))
                    return new Arg(Kind.RefFormattedClass, _intPtr, e);
                if (e.FullName == "System.Text.StringBuilder" && (nt is NativeType.LPWStr or NativeType.LPTStr or NativeType.LPStr || (nt == NativeType.None && comDefaults)))
                    return new Arg(Kind.RefStringBuilder, _intPtr, null, nt == NativeType.LPStr ? Ansi : 0);
                if (e.IsValueType && Safe(e) is { IsEnum: false, IsPrimitive: false } && e.FullName is not ("System.Boolean" or "System.Decimal" or "System.DateTime"))
                { why = $"by-reference {e.Name}: {fw ?? "not convertible"}"; return null; }
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
                // a C array by reference (out T[] with SizeParamIndex): the callee's CoTaskMem array, its length read
                // after the call
                if (e is ArrayType rat && rat.IsVector && (nt is NativeType.Array || (nt == NativeType.None && !comDefaults)))
                {
                    var rami = mi as ArrayMarshalInfo;
                    int rsp = rami?.SizeParameterIndex ?? -1, rsc = rami?.Size ?? -1;
                    if (rsp < 0 && rsc < 0) { why = $"by-reference {e.Name}: an array of unknown length"; return null; }
                    if (IsBlittable(rat.ElementType)) return new Arg(Kind.RefArray, _intPtr, rat.ElementType, 0, null, 0, rsp, rsc);
                    if (IsFormatted(rat.ElementType, out _)) return new Arg(Kind.RefArray, _intPtr, rat.ElementType, 1, null, 0, rsp, rsc);
                }
                why = $"by-reference {e.Name}"; return null;
            }
            if (IsComInterface(type)) return new Arg(Kind.Iface, _intPtr, type);
            // IEnumerable/IEnumerator from a COM method: IEnumVARIANT underneath (built-in interop's marshalers);
            // returned only (FromNative)
            if (type.FullName is "System.Collections.IEnumerable" or "System.Collections.IEnumerator" && comDefaults && nt is NativeType.None or NativeType.IntF or NativeType.IUnknown)
                return new Arg(Kind.Enumerable, _intPtr, type);
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
            // [MarshalAs(LPStruct)] on a blittable value type (a Guid, mostly): a pointer to a copy
            if (IsBlittable(type) && nt == NativeType.LPStruct) return new Arg(Kind.LPStructBlit, _intPtr, type);
            if (IsBlittable(type)) return new Arg(Kind.Blit, type);
            // a non-blittable struct by value: its native layout in a blittable shadow of the same size, passed as
            // that; a BCL one built-in interop refuses (System.Drawing.Color: "Invalid managed/unmanaged type
            // combination")
            if (type.IsValueType && type is not ArrayType && Safe(type) is { IsEnum: false } sv && type.FullName is not ("System.Boolean" or "System.Decimal" or "System.DateTime" or "System.Char"))
            {
                if (IsBcl(sv) || sv.Module.Assembly.Name.Name.StartsWith("System.", StringComparison.Ordinal))
                { why = $"{BuiltInCannotPrefix}{type.Name} by value"; return null; }
                if (!IsFormatted(type, out string? fw2)) { why = $"{type.Name} by value: {fw2}"; return null; }
                if (Shadow(type, out why) is not { } shadow) return null;
                return new Arg(Kind.StructByValue, shadow, type, 0);
            }
            if (type.FullName == "System.Text.StringBuilder")
            {
                if (nt is NativeType.LPWStr or NativeType.LPTStr || (nt == NativeType.None && comDefaults)) return new Arg(Kind.StringBuilder, _intPtr);
                if (nt is NativeType.LPStr) return new Arg(Kind.StringBuilder, _intPtr, null, Ansi);
                why = $"StringBuilder as {nt}"; return null;
            }
            // a formatted class (sequential or explicit layout) by value: a pointer to its native layout
            if (!type.IsValueType && type is not ArrayType && Safe(type) is { IsInterface: false, IsClass: true } fc && fc.FullName != "System.Object"
                && fc.FullName is not ("System.String" or "System.Array") && (nt is NativeType.None or NativeType.LPStruct or NativeType.Struct))
            {
                if (!IsFormatted(type, out string? fw)) { why = $"{type.Name}: {fw}"; return null; }
                return new Arg(Kind.FormattedClass, _intPtr, type, IsBlittableClass(fc) ? CopyBack : 0);
            }
            // an array marshalled as a C array (not COM's default SAFEARRAY) of blittable elements: pinned (a
            // char is UTF-16 in a COM method)
            var ami = mi as ArrayMarshalInfo;
            int sizeParam = ami?.SizeParameterIndex ?? -1, sizeConst = ami?.Size ?? -1;
            if (type is ArrayType at && at.IsVector && IsBlittable(at.ElementType) && (at.ElementType.FullName != "System.Char" || comDefaults)
                && (nt is NativeType.Array || (nt == NativeType.None && !comDefaults)))
                return new Arg(Kind.BlitArray, _intPtr, null, 0, null, 0, sizeParam, sizeConst);
            // a C array of strings, interfaces or VARIANTs
            if (type is ArrayType eat && eat.IsVector && (nt is NativeType.Array || (nt == NativeType.None && !comDefaults)))
            {
                var sub = ami?.ElementType ?? NativeType.None;
                if (sub == NativeType.Max) sub = NativeType.None;            // ArraySubType not given
                var et = eat.ElementType;
                int elem = et.FullName == "System.String"
                        ? sub switch { NativeType.LPWStr or NativeType.LPTStr => Com.ElemLPWStr, NativeType.LPStr => Com.ElemLPStr, NativeType.BStr => Com.ElemBStr, NativeType.None => comDefaults ? Com.ElemBStr : Com.ElemLPStr, _ => 0 }
                    : et.FullName == "System.Object"
                        ? sub switch { NativeType.IUnknown or NativeType.IntF => Com.ElemIface, NativeType.IDispatch => Com.ElemDispatch, NativeType.Struct or NativeType.None => Com.ElemVariant, _ => 0 }
                    : IsComInterface(et) ? Com.ElemIface : -1;
                if (elem == 0) { why = $"array {type.Name} of {sub}"; return null; }
                if (elem > 0) return new Arg(Kind.ElemArray, _intPtr, et, 0, null, elem, sizeParam, sizeConst);
            }
            // a C array of formatted structs or classes: one native buffer of their layouts
            if (type is ArrayType sat && sat.IsVector && (nt is NativeType.Array || (nt == NativeType.None && !comDefaults)))
            {
                if (!IsFormatted(sat.ElementType, out string? fw)) { why = $"array {type.Name}: {fw}"; return null; }
                return new Arg(Kind.StructArray, _intPtr, sat.ElementType, Safe(sat.ElementType) is { IsValueType: false } ec && IsBlittableClass(ec) ? CopyBack : 0, null, 0, sizeParam, sizeConst);
            }
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
                    if (ForwardWhy(md) is { } f) Reason($"{t.FullName}.{md.Name}", f);
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

        // ---- the BCL's other COM interfaces: generated here ---------------------------------------------

        /// <summary>The BCL COM interfaces (System.Runtime.InteropServices.ComTypes) this module uses that WpfMac.ComInterop
        /// does not write by hand, and those their signatures reach (IDataObject: IEnumFORMATETC, IAdviseSink, ...).
        /// Each gets a ComImpl and a ComCcw generated in this module, as its own interfaces do.</summary>
        private readonly HashSet<TypeDefinition> _bclGen = new();
        /// <summary>The same, by full name (a BCL type may resolve to more than one TypeDefinition instance).</summary>
        private readonly HashSet<string> _bclGenNames = new(StringComparer.Ordinal);
        private bool IsBclGen(TypeDefinition d) => _bclGenNames.Contains(d.FullName) && IsBcl(d);

        private void CollectBclInterfaces()
        {
            var queue = new Queue<TypeDefinition>();
            void Consider(TypeReference? t)
            {
                if (t == null) return;
                t = StripMods(t);
                while (t is ByReferenceType or ArrayType or PointerType) t = ((TypeSpecification)t).ElementType;
                t = StripMods(t);
                if (t is GenericParameter) return;
                var d = Safe(t);
                if (d != null && d.IsImport && d.IsInterface && IsBcl(d) && !IsBclCom(d) && _bclGenNames.Add(d.FullName)) { _bclGen.Add(d); queue.Enqueue(d); }
            }
            foreach (var tr in _m.GetTypeReferences()) Consider(tr);
            while (queue.Count > 0)
                foreach (var m in queue.Dequeue().Methods)
                {
                    Consider(m.ReturnType);
                    foreach (var p in m.Parameters) Consider(p.ParameterType);
                }
        }

        /// <summary><paramref name="md"/> (a BCL method) as a method of this module's, for classifying and emitting:
        /// its types imported, its marshalling and attributes kept. Not added to any type.</summary>
        private MethodDefinition ImportedSignature(MethodDefinition md)
        {
            var c = new MethodDefinition(md.Name, md.Attributes, _m.ImportReference(md.ReturnType)) { ImplAttributes = md.ImplAttributes };
            c.MethodReturnType.MarshalInfo = ImportMarshal(md.MethodReturnType.MarshalInfo);
            foreach (var p in md.Parameters)
                c.Parameters.Add(new ParameterDefinition(p.Name, p.Attributes, _m.ImportReference(p.ParameterType)) { MarshalInfo = ImportMarshal(p.MarshalInfo) });
            return c;
        }

        private MarshalInfo? ImportMarshal(MarshalInfo? mi) =>
            mi is CustomMarshalInfo cm ? new CustomMarshalInfo { ManagedType = _m.ImportReference(cm.ManagedType), Cookie = cm.Cookie, Guid = cm.Guid, UnmanagedType = cm.UnmanagedType } : mi;

        private MethodDefinition? _moduleInit;

        /// <summary>The module initializer's body, made when first needed: registrations run before anything of the
        /// module is used.</summary>
        private ILProcessor ModuleInit()
        {
            if (_moduleInit == null)
            {
                var module = _m.Types.First(t => t.Name == "<Module>");
                _moduleInit = module.Methods.FirstOrDefault(m => m.IsConstructor && m.IsStatic);
                if (_moduleInit == null)
                {
                    _moduleInit = new MethodDefinition(".cctor", MethodAttributes.Private | MethodAttributes.Static | MethodAttributes.HideBySig
                        | MethodAttributes.SpecialName | MethodAttributes.RTSpecialName, _void);
                    _moduleInit.Body.GetILProcessor().Emit(OpCodes.Ret);
                    module.Methods.Add(_moduleInit);
                }
            }
            return _moduleInit.Body.GetILProcessor();
        }

        private void GenerateBcl(TypeDefinition bcl)
        {
            int ifaceType = 1;
            var ita = bcl.CustomAttributes.FirstOrDefault(a => a.AttributeType.FullName == "System.Runtime.InteropServices.InterfaceTypeAttribute");
            if (ita != null && ita.ConstructorArguments.Count == 1) ifaceType = Convert.ToInt32(ita.ConstructorArguments[0].Value);
            int first = ifaceType == 1 ? 3 : 7;
            var methods = bcl.Methods.Where(x => !x.IsStatic).ToList();
            var sigs = methods.Select(ImportedSignature).ToList();
            if (AnalyzeOnly)
            {
                var refused = new List<string>();
                foreach (var (md, sig) in methods.Zip(sigs))
                {
                    if (ForwardWhy(sig) is { } f) Reason($"{bcl.FullName}.{md.Name}", f);
                    if (ReverseWhy(sig) is { } rv) refused.Add($"{md.Name}: {rv}");
                }
                _ccwRefused[bcl] = refused;
                return;
            }
            var iface = _m.ImportReference(bcl);
            var impl = new TypeDefinition("WpfMac.BclComImpl", bcl.Name + "Impl", TypeAttributes.Public | TypeAttributes.Interface | TypeAttributes.Abstract);
            impl.Interfaces.Add(new InterfaceImplementation(iface));
            impl.CustomAttributes.Add(new CustomAttribute(_m.ImportReference(typeof(System.Runtime.InteropServices.DynamicInterfaceCastableImplementationAttribute).GetConstructor(Type.EmptyTypes)!)));
            _m.Types.Add(impl);
            int slot = first;
            foreach (var (md, sig) in methods.Zip(sigs))
            {
                var body = new MethodDefinition($"{bcl.FullName}.{md.Name}", MethodAttributes.Private | MethodAttributes.HideBySig | MethodAttributes.NewSlot | MethodAttributes.Virtual | MethodAttributes.Final, sig.ReturnType);
                foreach (var p in sig.Parameters) body.Parameters.Add(new ParameterDefinition(p.Name, p.Attributes & ~ParameterAttributes.HasFieldMarshal, p.ParameterType));
                body.Overrides.Add(_m.ImportReference(md));
                impl.Methods.Add(body);
                EmitVtableCall(iface, sig, body, slot++);
                _r.Methods++;
            }
            _r.Interfaces++;
            var ccw = EmitCcw(iface, impl, bcl, first, sigs.Zip(methods, (s, m) => (s, (MethodReference)_m.ImportReference(m))).ToList());
            // registered with WpfMac.ComInterop when the module is first used
            var il = ModuleInit();
            var at = il.Body.Instructions[0];
            il.InsertBefore(at, il.Create(OpCodes.Ldtoken, iface));
            il.InsertBefore(at, il.Create(OpCodes.Ldtoken, impl));
            il.InsertBefore(at, il.Create(OpCodes.Ldtoken, ccw));
            il.InsertBefore(at, il.Create(OpCodes.Call, _registerGenerated));
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
            {
                var pa = Classify(p.ParameterType, p.MarshalInfo, comDefaults: true, out string? why);
                if (pa == null) return $"parameter {p.Name}: {why}";
                if (pa.Kind == Kind.Enumerable) return $"parameter {p.Name}: an IEnumerable passed in";
            }
            if (decl.ReturnType.FullName == "System.Void") return null;
            var ret = Classify(decl.ReturnType, decl.MethodReturnType.MarshalInfo, comDefaults: true, out string? w);
            if (ret != null && ret.Kind == Kind.Variant && decl.IsPreserveSig) return "a VARIANT returned by value";
            if (ret != null && ret.Kind == Kind.StructByValue && decl.IsPreserveSig) return "a non-blittable struct returned by value";
            if (ret == null || BadReturn(ret.Kind))
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
                if (a == null || BadReverseParam(a.Kind)) return $"parameter {p.Name}: {w ?? ReverseWhyOf(a!.Kind)}";
                if (ReverseSizeWhy(WithDirection(a, p), md, md.Parameters.IndexOf(p)) is { } sw) return $"parameter {p.Name}: {sw}";
            }
            if (md.ReturnType.FullName == "System.Void") return null;
            var ret = Classify(md.ReturnType, md.MethodReturnType.MarshalInfo, comDefaults: true, out string? w2);
            if (ret == null || BadReturn(ret.Kind) || ret.Kind is Kind.RefVariant or Kind.FormattedClass or Kind.StructByValue or Kind.Enumerable || (md.IsPreserveSig && ret.Kind == Kind.Variant))
                return $"return {w2 ?? md.ReturnType.Name}";
            return null;
        }

        private void EmitVtableCall(TypeReference iface, MethodDefinition decl, MethodDefinition body, int slot)
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
                if (a.Kind == Kind.Enumerable) { Refuse(il, member, $"parameter {p.Name}: an IEnumerable passed in"); return; }
                args.Add(WithDirection(a, p));
            }
            bool isVoid = decl.ReturnType.FullName == "System.Void";
            Arg? ret = null;
            if (!isVoid)
            {
                ret = Classify(decl.ReturnType, decl.MethodReturnType.MarshalInfo, comDefaults: true, out string? why);
                if (ret != null && ret.Kind == Kind.Variant && preserve) { Refuse(il, member, "a VARIANT returned by value"); return; }
                if (ret != null && ret.Kind == Kind.StructByValue && preserve) { Refuse(il, member, "a non-blittable struct returned by value"); return; }
                if (ret == null || BadReturn(ret.Kind))
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
                if (ret.Kind == Kind.FormattedClass)
                {
                    // [out, retval] T*: a buffer of the caller's (as built-in interop allocates it)
                    il.Emit(OpCodes.Ldtoken, ret.Iface!); il.Emit(OpCodes.Call, _newStruct); il.Emit(OpCodes.Stloc, retLocal);
                    pushes.Add(() => il.Emit(OpCodes.Ldloc, retLocal));
                }
                else pushes.Add(() => { il.Emit(OpCodes.Ldloca, retLocal); il.Emit(OpCodes.Conv_U); });
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

            if (ret != null && ret.Kind == Kind.FormattedClass && !preserve)
            {
                il.Emit(OpCodes.Ldloc, retLocal!); il.Emit(OpCodes.Ldnull); il.Emit(OpCodes.Ldtoken, ret.Iface!);
                il.Emit(OpCodes.Call, _structFromNative); il.Emit(OpCodes.Castclass, ret.Iface!);
                il.Emit(OpCodes.Ldloc, retLocal!); il.Emit(OpCodes.Ldtoken, ret.Iface!); il.Emit(OpCodes.Call, _freeStruct);
            }
            else if (ret != null && ret.Kind == Kind.StructByValue && !preserve)
            {
                // [out, retval] T*: the shadow local the callee filled -> T; what it holds is freed
                il.Emit(OpCodes.Ldloca, retLocal!); il.Emit(OpCodes.Conv_U);
                il.Emit(OpCodes.Ldc_I4, ShadowSize(ret.Native));
                il.Emit(OpCodes.Ldtoken, ret.Iface!);
                il.Emit(OpCodes.Call, _structFromShadow);
                il.Emit(OpCodes.Unbox_Any, ret.Iface!);
                il.Emit(OpCodes.Ldloca, retLocal!); il.Emit(OpCodes.Conv_U); il.Emit(OpCodes.Ldtoken, ret.Iface!); il.Emit(OpCodes.Call, _freeStructAt);
            }
            else if (ret != null && ret.Kind == Kind.Variant && !preserve)
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
        /// <summary><paramref name="t"/> without its modifiers (an `in` parameter's modreq), by reference or not.</summary>
        internal static TypeReference StripMods(TypeReference t)
        {
            while (t is RequiredModifierType or OptionalModifierType) t = ((TypeSpecification)t).ElementType;
            if (t is ByReferenceType br && br.ElementType is RequiredModifierType or OptionalModifierType) return new ByReferenceType(StripMods(br.ElementType));
            return t;
        }

        private void Prepare(MethodDefinition body, ILProcessor il, Arg a, int argIndex, TypeReference managed, List<Action> pushes, List<Action> cleanup, List<Action> after)
        {
            managed = StripMods(managed);
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
                case Kind.RefArray:
                {
                    var arrType = ((ByReferenceType)managed).ElementType;
                    var slot = Local(body, _intPtr);
                    pushes.Add(() => { il.Emit(OpCodes.Ldloca, slot); il.Emit(OpCodes.Conv_U); });
                    var len = Local(body, _int32);
                    after.Add(() =>
                    {
                        if (a.SizeParam >= 0)
                        {
                            il.Emit(OpCodes.Ldarg, a.SizeParam + 1);
                            if (body.Parameters[a.SizeParam].ParameterType is ByReferenceType) il.Emit(OpCodes.Ldind_I4);
                            il.Emit(OpCodes.Conv_I4);
                            if (a.SizeConst > 0) { il.Emit(OpCodes.Ldc_I4, a.SizeConst); il.Emit(OpCodes.Add); }
                        }
                        else il.Emit(OpCodes.Ldc_I4, a.SizeConst);
                        il.Emit(OpCodes.Stloc, len);
                        il.Emit(OpCodes.Ldarg, argIndex);
                        il.Emit(OpCodes.Ldloc, slot); il.Emit(OpCodes.Ldloc, len); il.Emit(OpCodes.Ldtoken, arrType);
                        if (a.Vt == 0) il.Emit(OpCodes.Call, _blitFromNativeN);
                        else { il.Emit(OpCodes.Ldc_I4_0); il.Emit(OpCodes.Call, _structFromNativeN); }
                        il.Emit(OpCodes.Castclass, arrType);
                        il.Emit(OpCodes.Stind_Ref);
                    });
                    cleanup.Add(() =>
                    {
                        if (a.Vt == 0) { il.Emit(OpCodes.Ldloc, slot); il.Emit(OpCodes.Call, _freeCoTaskMem); }
                        else { il.Emit(OpCodes.Ldloc, slot); il.Emit(OpCodes.Ldloc, len); il.Emit(OpCodes.Ldtoken, a.Iface!); il.Emit(OpCodes.Call, _freeStructArray); }
                    });
                    break;
                }
                case Kind.StructByValue:
                {
                    // the struct's native layout in the shadow local, passed by value; freed after the call
                    var sh = Local(body, a.Native);
                    il.Emit(OpCodes.Ldloca, sh); il.Emit(OpCodes.Initobj, a.Native);
                    il.Emit(OpCodes.Ldarg, argIndex); il.Emit(OpCodes.Box, a.Iface!);
                    il.Emit(OpCodes.Ldloca, sh); il.Emit(OpCodes.Conv_U);
                    il.Emit(OpCodes.Ldc_I4, ShadowSize(a.Native));
                    il.Emit(OpCodes.Ldtoken, a.Iface!);
                    il.Emit(OpCodes.Call, _structToShadow);
                    pushes.Add(() => il.Emit(OpCodes.Ldloc, sh));
                    cleanup.Add(() => { il.Emit(OpCodes.Ldloca, sh); il.Emit(OpCodes.Conv_U); il.Emit(OpCodes.Ldtoken, a.Iface!); il.Emit(OpCodes.Call, _freeStructAt); });
                    break;
                }
                case Kind.RefFormattedClass:
                {
                    // a pointer to the pointer to its native form; the callee may hand back another (CoTaskMem)
                    var t = a.Iface!;
                    var mine = Local(body, _intPtr);
                    var slot = Local(body, _intPtr);
                    il.Emit(OpCodes.Ldarg, argIndex); il.Emit(OpCodes.Ldind_Ref); il.Emit(OpCodes.Ldtoken, t); il.Emit(OpCodes.Call, _structToNative);
                    il.Emit(OpCodes.Dup); il.Emit(OpCodes.Stloc, mine); il.Emit(OpCodes.Stloc, slot);
                    pushes.Add(() => { il.Emit(OpCodes.Ldloca, slot); il.Emit(OpCodes.Conv_U); });
                    after.Add(() =>
                    {
                        il.Emit(OpCodes.Ldarg, argIndex);
                        il.Emit(OpCodes.Ldloc, slot); il.Emit(OpCodes.Ldarg, argIndex); il.Emit(OpCodes.Ldind_Ref); il.Emit(OpCodes.Ldtoken, t);
                        il.Emit(OpCodes.Call, _structFromNative); il.Emit(OpCodes.Castclass, t);
                        il.Emit(OpCodes.Stind_Ref);
                    });
                    cleanup.Add(() =>
                    {
                        il.Emit(OpCodes.Ldloc, mine); il.Emit(OpCodes.Ldtoken, t); il.Emit(OpCodes.Call, _freeStruct);
                        var same = il.Create(OpCodes.Nop);
                        il.Emit(OpCodes.Ldloc, slot); il.Emit(OpCodes.Ldloc, mine); il.Emit(OpCodes.Beq, same);
                        il.Emit(OpCodes.Ldloc, slot); il.Emit(OpCodes.Ldtoken, t); il.Emit(OpCodes.Call, _freeStruct);
                        il.Append(same);
                    });
                    break;
                }
                case Kind.RefStringBuilder:
                {
                    bool ansi = (a.Vt & Ansi) != 0;
                    var mine = Local(body, _intPtr);
                    var slot = Local(body, _intPtr);
                    il.Emit(OpCodes.Ldarg, argIndex); il.Emit(OpCodes.Ldind_Ref); il.Emit(ansi ? OpCodes.Ldc_I4_1 : OpCodes.Ldc_I4_0); il.Emit(OpCodes.Ldc_I4_0);
                    il.Emit(OpCodes.Call, _sbToNative);
                    il.Emit(OpCodes.Dup); il.Emit(OpCodes.Stloc, mine); il.Emit(OpCodes.Stloc, slot);
                    pushes.Add(() => { il.Emit(OpCodes.Ldloca, slot); il.Emit(OpCodes.Conv_U); });
                    after.Add(() => { il.Emit(OpCodes.Ldarg, argIndex); il.Emit(OpCodes.Ldind_Ref); il.Emit(OpCodes.Ldloc, slot); il.Emit(ansi ? OpCodes.Ldc_I4_1 : OpCodes.Ldc_I4_0); il.Emit(OpCodes.Call, _sbFromNative); });
                    cleanup.Add(() =>
                    {
                        il.Emit(OpCodes.Ldloc, mine); il.Emit(OpCodes.Call, _freeCoTaskMem);
                        var same = il.Create(OpCodes.Nop);
                        il.Emit(OpCodes.Ldloc, slot); il.Emit(OpCodes.Ldloc, mine); il.Emit(OpCodes.Beq, same);
                        il.Emit(OpCodes.Ldloc, slot); il.Emit(OpCodes.Call, _freeCoTaskMem);
                        il.Append(same);
                    });
                    break;
                }
                case Kind.LPStructBlit:
                {
                    var copy = Local(body, a.Iface!);
                    il.Emit(OpCodes.Ldarg, argIndex);
                    il.Emit(OpCodes.Stloc, copy);
                    pushes.Add(() => { il.Emit(OpCodes.Ldloca, copy); il.Emit(OpCodes.Conv_U); });
                    break;
                }
                case Kind.FormattedClass:
                {
                    var tmp = Local(body, _intPtr);
                    il.Emit(OpCodes.Ldarg, argIndex);
                    il.Emit(OpCodes.Ldtoken, a.Iface!);
                    il.Emit(OpCodes.Call, _structToNative);
                    il.Emit(OpCodes.Stloc, tmp);
                    pushes.Add(() => il.Emit(OpCodes.Ldloc, tmp));
                    if ((a.Vt & CopyBack) != 0)
                        after.Add(() =>
                        {
                            il.Emit(OpCodes.Ldloc, tmp); il.Emit(OpCodes.Ldarg, argIndex); il.Emit(OpCodes.Ldtoken, a.Iface!);
                            il.Emit(OpCodes.Call, _structFromNative); il.Emit(OpCodes.Pop);
                        });
                    cleanup.Add(() => { il.Emit(OpCodes.Ldloc, tmp); il.Emit(OpCodes.Ldtoken, a.Iface!); il.Emit(OpCodes.Call, _freeStruct); });
                    break;
                }
                case Kind.RefStruct:
                {
                    var t = a.Iface!;
                    var tmp = Local(body, _intPtr);
                    if ((a.Vt & OutOnly) != 0) { il.Emit(OpCodes.Ldtoken, t); il.Emit(OpCodes.Call, _newStruct); }
                    else { il.Emit(OpCodes.Ldarg, argIndex); il.Emit(OpCodes.Ldobj, t); il.Emit(OpCodes.Box, t); il.Emit(OpCodes.Ldtoken, t); il.Emit(OpCodes.Call, _structToNative); }
                    il.Emit(OpCodes.Stloc, tmp);
                    pushes.Add(() => il.Emit(OpCodes.Ldloc, tmp));
                    after.Add(() =>
                    {
                        il.Emit(OpCodes.Ldarg, argIndex);
                        il.Emit(OpCodes.Ldloc, tmp); il.Emit(OpCodes.Ldnull); il.Emit(OpCodes.Ldtoken, t);
                        il.Emit(OpCodes.Call, _structFromNative);
                        il.Emit(OpCodes.Unbox_Any, t);
                        il.Emit(OpCodes.Stobj, t);
                    });
                    cleanup.Add(() => { il.Emit(OpCodes.Ldloc, tmp); il.Emit(OpCodes.Ldtoken, t); il.Emit(OpCodes.Call, _freeStruct); });
                    break;
                }
                case Kind.Custom:
                case Kind.RefCustom:
                {
                    // built-in interop's order: ManagedToNative, the call, NativeToManaged (an out), CleanUpNativeData
                    var m = Local(body, _object);
                    var tmp = Local(body, _intPtr);
                    il.Emit(OpCodes.Ldtoken, a.Iface!);
                    if (a.Cookie == null) il.Emit(OpCodes.Ldnull); else il.Emit(OpCodes.Ldstr, a.Cookie);
                    il.Emit(OpCodes.Call, _customMarshaler);
                    il.Emit(OpCodes.Stloc, m);
                    il.Emit(OpCodes.Ldloc, m);
                    il.Emit(OpCodes.Ldarg, argIndex);
                    if (a.Kind == Kind.RefCustom) il.Emit(OpCodes.Ldind_Ref);
                    il.Emit(OpCodes.Call, _customToNative);
                    il.Emit(OpCodes.Stloc, tmp);
                    if (a.Kind == Kind.RefCustom)
                    {
                        var elem = ((ByReferenceType)managed).ElementType;
                        pushes.Add(() => { il.Emit(OpCodes.Ldloca, tmp); il.Emit(OpCodes.Conv_U); });
                        after.Add(() =>
                        {
                            il.Emit(OpCodes.Ldarg, argIndex);
                            il.Emit(OpCodes.Ldloc, m); il.Emit(OpCodes.Ldloc, tmp); il.Emit(OpCodes.Call, _customFromNative);
                            il.Emit(OpCodes.Castclass, elem);
                            il.Emit(OpCodes.Stind_Ref);
                        });
                    }
                    else
                    {
                        pushes.Add(() => il.Emit(OpCodes.Ldloc, tmp));
                        if ((a.Vt & CopyBack) != 0)
                            after.Add(() => { il.Emit(OpCodes.Ldloc, m); il.Emit(OpCodes.Ldloc, tmp); il.Emit(OpCodes.Call, _customFromNative); il.Emit(OpCodes.Pop); });
                    }
                    cleanup.Add(() => { il.Emit(OpCodes.Ldloc, m); il.Emit(OpCodes.Ldloc, tmp); il.Emit(OpCodes.Call, _customCleanUp); });
                    break;
                }
                case Kind.StringBuilder:
                {
                    bool ansi = (a.Vt & Ansi) != 0;
                    var tmp = Local(body, _intPtr);
                    il.Emit(OpCodes.Ldarg, argIndex);
                    il.Emit(ansi ? OpCodes.Ldc_I4_1 : OpCodes.Ldc_I4_0);
                    il.Emit((a.Vt & OutOnly) != 0 ? OpCodes.Ldc_I4_1 : OpCodes.Ldc_I4_0);
                    il.Emit(OpCodes.Call, _sbToNative);
                    il.Emit(OpCodes.Stloc, tmp);
                    pushes.Add(() => il.Emit(OpCodes.Ldloc, tmp));
                    after.Add(() => { il.Emit(OpCodes.Ldarg, argIndex); il.Emit(OpCodes.Ldloc, tmp); il.Emit(ansi ? OpCodes.Ldc_I4_1 : OpCodes.Ldc_I4_0); il.Emit(OpCodes.Call, _sbFromNative); });
                    cleanup.Add(() => { il.Emit(OpCodes.Ldloc, tmp); il.Emit(OpCodes.Call, _freeCoTaskMem); });
                    break;
                }
                case Kind.ElemArray:
                {
                    var tmp = Local(body, _intPtr);
                    il.Emit(OpCodes.Ldarg, argIndex);
                    il.Emit(OpCodes.Ldc_I4, a.Elem);
                    il.Emit(OpCodes.Ldtoken, a.Iface!);
                    il.Emit((a.Vt & OutOnly) != 0 ? OpCodes.Ldc_I4_1 : OpCodes.Ldc_I4_0);
                    il.Emit(OpCodes.Call, _elemToNative);
                    il.Emit(OpCodes.Stloc, tmp);
                    pushes.Add(() => il.Emit(OpCodes.Ldloc, tmp));
                    if ((a.Vt & CopyBack) != 0)
                        after.Add(() => { il.Emit(OpCodes.Ldloc, tmp); il.Emit(OpCodes.Ldarg, argIndex); il.Emit(OpCodes.Ldc_I4, a.Elem); il.Emit(OpCodes.Ldtoken, a.Iface!); il.Emit(OpCodes.Call, _elemFromNative); });
                    cleanup.Add(() => { il.Emit(OpCodes.Ldloc, tmp); il.Emit(OpCodes.Ldarg, argIndex); il.Emit(OpCodes.Call, _lengthOf); il.Emit(OpCodes.Ldc_I4, a.Elem); il.Emit(OpCodes.Call, _freeElem); });
                    break;
                }
                case Kind.StructArray:
                {
                    var tmp = Local(body, _intPtr);
                    il.Emit(OpCodes.Ldarg, argIndex);
                    il.Emit(OpCodes.Ldtoken, a.Iface!);
                    il.Emit(OpCodes.Call, _structArrayToNative);
                    il.Emit(OpCodes.Stloc, tmp);
                    pushes.Add(() => il.Emit(OpCodes.Ldloc, tmp));
                    if ((a.Vt & CopyBack) != 0)
                        after.Add(() => { il.Emit(OpCodes.Ldloc, tmp); il.Emit(OpCodes.Ldarg, argIndex); il.Emit(OpCodes.Ldtoken, a.Iface!); il.Emit(OpCodes.Call, _structArrayFromNative); });
                    cleanup.Add(() =>
                    {
                        il.Emit(OpCodes.Ldloc, tmp); il.Emit(OpCodes.Ldarg, argIndex); il.Emit(OpCodes.Call, _lengthOf);
                        il.Emit(OpCodes.Ldtoken, a.Iface!); il.Emit(OpCodes.Call, _freeStructArray);
                    });
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
                case Kind.Enumerable:
                    il.Emit(OpCodes.Call, managed.FullName == "System.Collections.IEnumerator" ? _takeEnumerator : _takeEnumerable);
                    il.Emit(OpCodes.Castclass, managed);
                    break;
                case Kind.FormattedClass:
                    // a PreserveSig T*: copied into a new object; the memory stays the callee's
                    il.Emit(OpCodes.Ldnull); il.Emit(OpCodes.Ldtoken, managed);
                    il.Emit(OpCodes.Call, _structFromNative); il.Emit(OpCodes.Castclass, managed);
                    break;
                case Kind.StrW:
                    il.Emit(OpCodes.Call, _takeCoTaskUni);
                    break;
            }
        }

        private void Refuse(ILProcessor il, string member, string why)
        {
            Reason(member, why);
            why = why.Replace(BuiltInCannotPrefix, "");
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
                    // the old [MarshalAs(Interface)] must go: null is not enough (Cecil writes the original
                    // FieldMarshal row of a lazily read module again), so the pointer says SysInt, valid for IntPtr
                    if (ch) prm.MarshalInfo = new MarshalInfo(NativeType.Int);
                }
                if (m.Name != "BeginInvoke")
                {
                    m.ReturnType = NativeOf(m.ReturnType, out bool rch);
                    if (rch) m.MethodReturnType.MarshalInfo = new MarshalInfo(NativeType.Int);
                }
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

        /// <summary>The interface method a COM class's method implements (an explicit override, or the same name and
        /// signature on one of its interfaces).</summary>
        private MethodDefinition? InterfaceMethodOf(MethodDefinition def)
        {
            foreach (var o in def.Overrides) if (SafeM(o) is { } d && d.DeclaringType.IsInterface) return d;
            string Sig(MethodReference m) => m.ReturnType.FullName + "(" + string.Join(",", m.Parameters.Select(p => p.ParameterType.FullName)) + ")";
            string want = Sig(def);
            var seen = new HashSet<TypeDefinition>();
            var queue = new Queue<TypeReference>(def.DeclaringType.Interfaces.Select(i => i.InterfaceType));
            while (queue.Count > 0)
            {
                var i = Safe(queue.Dequeue());
                if (i == null || !seen.Add(i)) continue;
                foreach (var m in i.Methods) if (m.Name == def.Name && Sig(m) == want) return m;
                foreach (var b in i.Interfaces) queue.Enqueue(b.InterfaceType);
            }
            return null;
        }

        private HashSet<TypeDefinition>? _nativeDelegates;
        private bool _fnPtrForDelegate;

        /// <summary>Whether a managed delegate of <paramref name="t"/> can be handed to native code here: the module
        /// calls Marshal.GetFunctionPointerForDelegate, or a P/Invoke or COM method takes that delegate type.</summary>
        private bool DelegateReachesNative(TypeDefinition t)
        {
            if (_nativeDelegates == null)
            {
                _nativeDelegates = new HashSet<TypeDefinition>();
                _fnPtrForDelegate = _m.GetMemberReferences().Any(r => r.Name == "GetFunctionPointerForDelegate" && r.DeclaringType.FullName == "System.Runtime.InteropServices.Marshal");
                foreach (var ty in _m.Types.SelectMany(AllTypes))
                    foreach (var m in ty.Methods)
                        if (m.IsPInvokeImpl || (ty.IsInterface && (ty.IsImport || _comTypes.Contains(ty))))
                            foreach (var pt in m.Parameters.Select(p => p.ParameterType).Append(m.ReturnType))
                                if (Safe(pt is ByReferenceType br ? br.ElementType : pt) is { } d && IsDelegate(d)) _nativeDelegates.Add(d);
            }
            return _fnPtrForDelegate || _nativeDelegates.Contains(t);
        }

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
                    {
                        // a tlbimp coclass's own method: the interface method it implements, on the same object
                        if (InterfaceMethodOf(def) is { } im) { ins.OpCode = OpCodes.Callvirt; ins.Operand = _m.ImportReference(im); _r.CallSites++; }
                        else _r.Unsupported.Add($"{md.DeclaringType.FullName}.{md.Name}: calls {def.DeclaringType.Name}.{def.Name} on a COM class");
                    }
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
                    else if (_rewrittenDelegates.Contains(t) && DelegateReachesNative(t))
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
                    else if (t != null && (_comTypes.Contains(t) || IsForeignCom(t)) && !FromComWrapper(ins.Previous) && !IsCom(StackType(md, ins.Previous)))
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
