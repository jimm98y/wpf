// The P/Invoke rewriter (Documentation/pinvoke-rewriter.md): an app's P/Invokes of Windows DLLs become
// managed methods that call the stack's implementation of the function directly -- no native library, no
// function pointers, so the same rewrite serves a JIT head at load and an AOT head (iOS, Android, the
// browser) at build.
//
// Each such P/Invoke's entry point is found as Windows finds it (ExactSpelling, CharSet: Unicode and Auto
// look for nameW first, Ansi for name then nameA). Then, off Windows:
//   - the function has an implementation ([Win32Export]): the arguments are marshalled as the runtime
//     marshals them for the import (the COM rewriter's engine: strings by CharSet, bool as BOOL, structs,
//     StringBuilders, arrays, SafeHandles, delegates as function pointers), the implementation is called,
//     the results come back the same way, and SetLastError captures what it set;
//   - it has none: the call logs once and returns the function's failure value (FALSE/NULL with
//     ERROR_CALL_NOT_IMPLEMENTED, E_NOTIMPL, ...), as on a Windows that lacked it;
//   - no Windows DLL exports it: the call throws EntryPointNotFoundException, as there.
// On Windows the real functions are underneath, and only those with a [Win32ExportOnWindows] are rewritten:
// the implementation runs when a handle argument the mask names is one of the stack's windows (or always,
// with the Always bit); every other call reaches the original import, kept as name<native>.

using Mono.Cecil;
using Mono.Cecil.Cil;
using WpfWebGpu.ComInterop;

namespace WpfWebGpu.ComRewrite;

public static partial class ComRewriter
{
    /// <summary>Whether the file P/Invokes a Windows DLL (metadata only); on Windows (<paramref name="windows"/>),
    /// one of the functions the stack routes there.</summary>
    public static bool ImportsWindows(string path, Win32Catalog catalog, bool windows)
    {
        try
        {
            using var fs = File.OpenRead(path);
            using var pe = new System.Reflection.PortableExecutable.PEReader(fs);
            if (!pe.HasMetadata) return false;
            var md = System.Reflection.Metadata.PEReaderExtensions.GetMetadataReader(pe);
            foreach (var h in md.MethodDefinitions)
            {
                var m = md.GetMethodDefinition(h);
                var import = m.GetImport();
                if (import.Module.IsNil) continue;
                if (!catalog.IsWindowsModule(Win32Catalog.Normalize(md.GetString(md.GetModuleReference(import.Module).Name)))) continue;
                if (!windows) return true;
                string ep = import.Name.IsNil ? md.GetString(m.Name) : md.GetString(import.Name);
                if (ep.Length == 0) ep = md.GetString(m.Name);
                if (catalog.HasOnWindows(ep) || catalog.HasOnWindows(ep + "W") || catalog.HasOnWindows(ep + "A")) return true;
            }
            return false;
        }
        catch (Exception e) when (e is IOException or BadImageFormatException or UnauthorizedAccessException) { return false; }
    }

    /// <summary>WPF_D3D11_SHIM, as the Direct3D shim reads it: whether assembly <paramref name="name"/>'s
    /// Direct3D imports go to the shim (unset/all/1: every one; 0/off: none; A*;B: those).</summary>
    internal static bool Direct3DCovers(string name)
    {
        string spec = Environment.GetEnvironmentVariable("WPF_D3D11_SHIM")?.Trim() is { Length: > 0 } s ? s : "all";
        if (spec is "0" || spec.Equals("off", StringComparison.OrdinalIgnoreCase) || spec.Equals("false", StringComparison.OrdinalIgnoreCase)) return false;
        if (spec is "1" || spec.Equals("all", StringComparison.OrdinalIgnoreCase)) return true;
        foreach (string p in spec.Split(new[] { ';', ',' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            if (p.EndsWith('*') ? name.StartsWith(p[..^1], StringComparison.OrdinalIgnoreCase) : name.Equals(p, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    private static bool IsDirect3D(string module) => module is "d3d11" or "dxgi" or "d3d9" or "d3dcompiler_47";

    private sealed partial class Work
    {
        /// <summary>The index of a method's first argument in the bodies being generated (1: an interface
        /// method's this; 0: a P/Invoke).</summary>
        private int _argBase = 1;

        private Win32Catalog? _cat;
        private MethodReference? _pAddRef, _pRelease, _pNewSafeHandle, _pHandleOf, _pDelegateToNative, _pDelegateFromNative, _pTakeAnsi,
            _pBeginLastError, _pEndLastError, _pEntryPointNotFound, _pUnsupported, _handleRefHandle, _handleRefWrapper;

        /// <summary>Rewrites the module's Windows P/Invokes (see the header).</summary>
        public void RunPInvokes(Win32Catalog cat, bool windows)
        {
            _cat = cat;
            var pi = typeof(PInvoke);
            MethodReference Imp(string name, params Type[] args) => _m.ImportReference(pi.GetMethod(name, args) ?? throw new MissingMethodException(name));
            _pAddRef = Imp(nameof(PInvoke.AddRef), typeof(System.Runtime.InteropServices.SafeHandle), typeof(bool).MakeByRefType());
            _pRelease = Imp(nameof(PInvoke.Release), typeof(System.Runtime.InteropServices.SafeHandle), typeof(bool));
            _pNewSafeHandle = Imp(nameof(PInvoke.NewSafeHandle), typeof(IntPtr), typeof(RuntimeTypeHandle));
            _pHandleOf = Imp(nameof(PInvoke.HandleOf), typeof(System.Runtime.InteropServices.SafeHandle));
            _pDelegateToNative = Imp(nameof(PInvoke.DelegateToNative), typeof(Delegate));
            _pDelegateFromNative = Imp(nameof(PInvoke.DelegateFromNative), typeof(IntPtr), typeof(RuntimeTypeHandle));
            _pTakeAnsi = Imp(nameof(PInvoke.TakeAnsi), typeof(IntPtr));
            _pBeginLastError = Imp(nameof(PInvoke.BeginLastError));
            _pEndLastError = Imp(nameof(PInvoke.EndLastError));
            _pEntryPointNotFound = Imp(nameof(PInvoke.EntryPointNotFound), typeof(string), typeof(string));
            _pUnsupported = Imp(nameof(PInvoke.Unsupported), typeof(string), typeof(string));
            var hr = typeof(System.Runtime.InteropServices.HandleRef);
            _handleRefHandle = _m.ImportReference(hr.GetProperty(nameof(System.Runtime.InteropServices.HandleRef.Handle))!.GetMethod!);
            _handleRefWrapper = _m.ImportReference(hr.GetProperty(nameof(System.Runtime.InteropServices.HandleRef.Wrapper))!.GetMethod!);

            _argBase = 0;
            try
            {
                foreach (var t in _m.Types.SelectMany(AllTypes).ToList())
                    foreach (var p in t.Methods.ToList())
                        if (p.IsPInvokeImpl && p.PInvokeInfo?.Module != null && !p.HasGenericParameters)
                        {
                            string module = Win32Catalog.Normalize(p.PInvokeInfo.Module.Name);
                            if (cat.IsWindowsModule(module)) RewriteWin32(p, module, windows);
                        }
            }
            finally { _argBase = 1; }
        }

        /// <summary>The import's PreserveSig, read before it becomes a managed method (which clears the flag).</summary>
        private bool _preserveSig;

        private void RewriteWin32(MethodDefinition p, string module, bool windows)
        {
            var info = p.PInvokeInfo;
            _preserveSig = p.IsPreserveSig;
            string member = $"{p.DeclaringType.FullName}.{p.Name}";
            string ep = string.IsNullOrEmpty(info.EntryPoint) ? p.Name : info.EntryPoint;
            // by ordinal ("#135"): what it names depends on the DLL's build; the table has the ones apps use
            if (ep.StartsWith('#') && windows) return;
            bool unicode = info.IsCharSetUnicode || info.IsCharSetAuto;           // Auto is Unicode on Windows
            string[] candidates = info.IsNoMangle || ep.StartsWith('#') ? new[] { ep } : unicode ? new[] { ep + "W", ep } : new[] { ep, ep + "A" };
            string? name = null;
            var failure = Win32Catalog.Failure.Zero;
            foreach (string c in candidates)
                if (_cat!.Exports(module, c, out failure)) { name = c; break; }

            if (windows)
            {
                if (name == null) return;
                var (impl, mask) = _cat!.OnWindows(name);
                if (mask == 0) return;
                if (IsDirect3D(module) && !Direct3DCovers(_m.Assembly.Name.Name)) return;
                RouteOnWindows(p, member, module, name, impl, mask, unicode);
                return;
            }

            if (name == null)
            {
                ToStub(p, il =>
                {
                    il.Emit(OpCodes.Ldstr, info.Module.Name); il.Emit(OpCodes.Ldstr, ep);
                    il.Emit(OpCodes.Call, _pEntryPointNotFound); il.Emit(OpCodes.Throw);
                });
                _r.Win32Missing++;
                return;
            }
            MethodDefinition? implDef = IsDirect3D(module) && !Direct3DCovers(_m.Assembly.Name.Name) ? null : _cat!.OffWindows(name);
            if (implDef == null && !info.IsNoMangle && !ep.StartsWith('#'))
            {
                // the same function under its other name: the A entry point of an undecorated ANSI import, the
                // undecorated one of a W import (both marshal as declared); an ANSI import whose function only
                // has a Unicode implementation, called as Unicode when no argument carries text the call
                // does not convert (a string or StringBuilder is converted either way)
                if (!unicode) implDef = _cat.OffWindows(ep + "A") ?? (TextFree(p) ? _cat.OffWindows(ep + "W") is { } w ? Bridge(w, ref unicode) : null : null);
                else implDef = _cat.OffWindows(ep);
            }
            if (implDef == null)
            {
                ToStub(p, il => EmitUnimplemented(il, p, module, name, failure));
                _r.Win32Unimplemented++;
                return;
            }
            if (Plan(p, implDef, unicode, out var args, out var ret, out string? why) is false)
            {
                _r.Win32Unsupported.Add($"{member} ({module}!{name}): {why}");
                ToStub(p, il => { il.Emit(OpCodes.Ldstr, member); il.Emit(OpCodes.Ldstr, why!); il.Emit(OpCodes.Call, _pUnsupported); il.Emit(OpCodes.Throw); });
                return;
            }
            bool lastError = info.SupportsLastError;
            ToStub(p, il => EmitImplementationCall(p, il, implDef, args!, ret, lastError));
            _r.Win32Implemented++;
        }

        private static MethodDefinition Bridge(MethodDefinition w, ref bool unicode) { unicode = true; return w; }

        /// <summary>Whether no parameter of <paramref name="p"/> could carry text in the import's own character
        /// set past the marshaller: only numbers (by value, or a count by reference), handles, strings and StringBuilders (converted either way),
        /// delegates. A struct, an array, a reference, or a pointer that may point at text (lParam, lpsz...) can.</summary>
        private bool TextFree(MethodDefinition p)
        {
            foreach (var prm in p.Parameters)
            {
                var t = StripMods(prm.ParameterType);
                // a count or size by reference (GetUserName's ref int): a number either way
                if (t is ByReferenceType br && br.ElementType.FullName is "System.Int32" or "System.UInt32") continue;
                if (t is ByReferenceType) return false;
                if (t.FullName is "System.String" or "System.Text.StringBuilder" or "System.Boolean" or "System.Runtime.InteropServices.HandleRef") continue;
                if (t.FullName is "System.IntPtr" or "System.UIntPtr")
                {
                    string n = prm.Name.ToLowerInvariant();
                    if (n.Contains("param") || n.StartsWith("lp") || n.StartsWith("psz") || n.Contains("buf") || n.Contains("text") || n.Contains("str")) return false;
                    continue;
                }
                if (t.FullName != "System.Char" && s_prim.Contains(t.FullName)) continue;
                if (Safe(t) is { } d && (d.IsEnum || IsDelegate(d) || IsSafeHandle(t))) continue;
                return false;
            }
            return true;
        }

        /// <summary>Makes <paramref name="p"/> a managed method whose body <paramref name="emit"/> writes.</summary>
        private void ToStub(MethodDefinition p, Action<ILProcessor> emit)
        {
            if (AnalyzeOnly) return;
            p.PInvokeInfo = null;
            p.IsPInvokeImpl = false;
            p.ImplAttributes = MethodImplAttributes.IL | MethodImplAttributes.Managed;
            p.Body = new MethodBody(p) { InitLocals = true };
            emit(p.Body.GetILProcessor());
        }

        /// <summary>A copy of the P/Invoke <paramref name="p"/> under name&lt;native&gt;: the real function.</summary>
        private MethodDefinition NativeCopy(MethodDefinition p)
        {
            var info = p.PInvokeInfo;
            var native = new MethodDefinition(p.Name + "<native>", p.Attributes, p.ReturnType)
            {
                ImplAttributes = p.ImplAttributes,
                PInvokeInfo = new PInvokeInfo(info.Attributes, string.IsNullOrEmpty(info.EntryPoint) ? p.Name : info.EntryPoint, info.Module),
            };
            foreach (var prm in p.Parameters)
            {
                var np = new ParameterDefinition(prm.Name, prm.Attributes, prm.ParameterType);
                if (prm.HasMarshalInfo) np.MarshalInfo = prm.MarshalInfo;
                native.Parameters.Add(np);
            }
            if (p.MethodReturnType.HasMarshalInfo) native.MethodReturnType.MarshalInfo = p.MethodReturnType.MarshalInfo;
            p.DeclaringType.Methods.Add(native);
            return native;
        }

        // ---- the call -----------------------------------------------------------------------------

        private bool IsSafeHandle(TypeReference t)
        {
            for (var d = Safe(t); d != null; d = d.BaseType == null ? null : Safe(d.BaseType))
                if (d.FullName == "System.Runtime.InteropServices.SafeHandle") return true;
            return false;
        }

        /// <summary>A string's (or StringBuilder's, or string array's) marshalling by the import's CharSet when
        /// the parameter does not say.</summary>
        private static MarshalInfo? WithCharSet(TypeReference t, MarshalInfo? mi, bool unicode)
        {
            if (mi != null && mi.NativeType != NativeType.None && mi is not ArrayMarshalInfo) return mi;
            var e = t is ByReferenceType br ? br.ElementType : t;
            if (mi == null && e.FullName is "System.String" or "System.Text.StringBuilder") return new MarshalInfo(unicode ? NativeType.LPWStr : NativeType.LPStr);
            if (e is ArrayType { IsVector: true } at && at.ElementType.FullName == "System.String")
            {
                var ami = mi as ArrayMarshalInfo ?? new ArrayMarshalInfo();
                if (ami.ElementType is NativeType.None or NativeType.Max) ami.ElementType = unicode ? NativeType.LPWStr : NativeType.LPStr;
                return ami;
            }
            return mi;
        }

        /// <summary>How the import's parameter (or return) is marshalled: the COM rewriter's kinds, and the
        /// P/Invoke-only ones (a delegate, a SafeHandle, a HandleRef).</summary>
        private Arg? ClassifyPInvoke(TypeReference type, MarshalInfo? mi, bool unicode, out string? why)
        {
            why = null;
            var t = StripMods(type);
            var e = t is ByReferenceType br ? br.ElementType : null;
            if (e == null && Safe(t) is { } d && IsDelegate(d)) return new Arg(Kind.Delegate, _intPtr, t);
            if (e != null && Safe(e) is { } ed && IsDelegate(ed)) { why = "a delegate by reference"; return null; }
            if (e == null && IsSafeHandle(t)) return new Arg(Kind.SafeHandle, _intPtr, t);
            if (e != null && IsSafeHandle(e)) return new Arg(Kind.OutSafeHandle, _intPtr, e);
            if (t.FullName == "System.Runtime.InteropServices.HandleRef") return new Arg(Kind.HandleRef, _intPtr);
            if (e != null && e.FullName == "System.Runtime.InteropServices.HandleRef") { why = "a HandleRef by reference"; return null; }
            // a char array in a Unicode import: UTF-16 already, pinned
            if (t is ArrayType { IsVector: true } ca && ca.ElementType.FullName == "System.Char" && unicode && mi is null or ArrayMarshalInfo)
            {
                var ami = mi as ArrayMarshalInfo;
                return new Arg(Kind.BlitArray, _intPtr, null, 0, null, 0, ami?.SizeParameterIndex ?? -1, ami?.Size ?? -1);
            }
            // a char: one byte in an ANSI import
            if (t.FullName == "System.Char" && !unicode && mi == null) return new Arg(Kind.Blit, _byte);
            return Classify(t, WithCharSet(t, mi, unicode), comDefaults: false, out why);
        }

        private bool Plan(MethodDefinition p, MethodDefinition impl, bool unicode, out List<Arg>? args, out Arg? ret, out string? why)
        {
            args = null; ret = null; why = null;
            if (p.CallingConvention == MethodCallingConvention.VarArg) { why = "a varargs import"; return false; }
            // a parameter count other than the function's is passed as the native call would pass it: the
            // extra arguments go nowhere, the missing ones are zero (EmitImplementationCall)
            var list = new List<Arg>();
            foreach (var prm in p.Parameters)
            {
                var a = ClassifyPInvoke(prm.ParameterType, prm.MarshalInfo, unicode, out why);
                if (a == null) { why = $"parameter {prm.Name}: {why}"; return false; }
                if (a.Kind is Kind.Enumerable or Kind.Variant or Kind.RefVariant or Kind.SafeArray or Kind.RefSafeArray)
                { why = $"parameter {prm.Name}: {a.Kind}"; return false; }
                list.Add(WithDirection(a, prm));
            }
            if (p.ReturnType.FullName != "System.Void")
            {
                var r = ClassifyPInvoke(p.ReturnType, p.MethodReturnType.MarshalInfo, unicode, out why);
                if (r == null) { why = $"return: {why}"; return false; }
                if (r.Kind is Kind.HandleRef or Kind.OutSafeHandle or Kind.StructByValue || (BadReturn(r.Kind) && r.Kind != Kind.StrA))
                { why = $"return: {r.Kind}"; return false; }
                // PreserveSig = false: the value comes back through a last pointer argument, the HRESULT is thrown
                if (!p.IsPreserveSig && r.Kind is not (Kind.Blit or Kind.Bool4 or Kind.Bool2 or Kind.Bool1 or Kind.Iface or Kind.Unknown or Kind.StrW or Kind.StrB or Kind.SafeHandle))
                { why = $"PreserveSig = false returning {r.Kind}"; return false; }
                ret = r;
            }
            args = list;
            return true;
        }

        private void EmitImplementationCall(MethodDefinition p, ILProcessor il, MethodDefinition implDef, List<Arg> args, Arg? ret, bool lastError)
        {
            var impl = _m.ImportReference(implDef);
            bool preserve = _preserveSig;
            var cleanup = new List<Action>();
            var after = new List<Action>();
            var pushes = new List<Action>();
            if (lastError) il.Emit(OpCodes.Call, _pBeginLastError);
            int n = impl.Parameters.Count;
            for (int i = 0; i < args.Count; i++)
            {
                int k = i;
                var mine = new List<Action>();
                PreparePInvoke(p, il, args[i], i, p.Parameters[i].ParameterType, mine, cleanup, after);
                if (i >= n) continue;                                   // an argument the function does not take
                var to = impl.Parameters[i].ParameterType;
                pushes.Add(() => { foreach (var x in mine) x(); Coerce(il, p, args[k].Native, to); });
            }
            // PreserveSig = false: the [out, retval] the function writes
            VariableDefinition? retval = null;
            if (!preserve && ret != null)
            {
                retval = Local(p, ret.Native);
                if (args.Count < n) { var to = impl.Parameters[args.Count].ParameterType; pushes.Add(() => { il.Emit(OpCodes.Ldloca, retval); il.Emit(OpCodes.Conv_U); Coerce(il, p, _intPtr, to); }); }
            }
            for (int i = args.Count + (retval != null ? 1 : 0); i < n; i++)
            {
                var to = impl.Parameters[i].ParameterType;
                pushes.Add(() => EmitZero(il, p, to));                  // an argument the import does not pass
            }
            Instruction? tryStart = cleanup.Count > 0 ? il.Create(OpCodes.Nop) : null;
            if (tryStart != null) il.Append(tryStart);
            foreach (var push in pushes) push();
            il.Emit(OpCodes.Call, impl);
            VariableDefinition? r = null;
            bool implVoid = impl.ReturnType.FullName == "System.Void";
            if (!preserve)
            {
                if (implVoid) il.Emit(OpCodes.Ldc_I4_0);
                else Coerce(il, p, impl.ReturnType, _int32);
                if (lastError) il.Emit(OpCodes.Call, _pEndLastError);
                il.Emit(OpCodes.Call, _check);                          // a failed HRESULT, thrown as the runtime throws it
                r = retval;
            }
            else
            {
                if (ret != null)
                {
                    if (implVoid) EmitZero(il, p, ret.Native);
                    else Coerce(il, p, impl.ReturnType, ret.Native);
                    r = Local(p, ret.Native);
                    il.Emit(OpCodes.Stloc, r);
                }
                else if (!implVoid) il.Emit(OpCodes.Pop);
                if (lastError) il.Emit(OpCodes.Call, _pEndLastError);
            }
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
                p.Body.ExceptionHandlers.Add(new ExceptionHandler(ExceptionHandlerType.Finally) { TryStart = tryStart, TryEnd = handlerStart, HandlerStart = handlerStart, HandlerEnd = leaveTarget });
            }
            if (r != null)
            {
                il.Emit(OpCodes.Ldloc, r);
                FromNativePInvoke(il, ret!, p.ReturnType);
            }
            il.Emit(OpCodes.Ret);
        }

        private void PreparePInvoke(MethodDefinition body, ILProcessor il, Arg a, int argIndex, TypeReference managed, List<Action> pushes, List<Action> cleanup, List<Action> after)
        {
            switch (a.Kind)
            {
                case Kind.Delegate:
                    pushes.Add(() => { il.Emit(OpCodes.Ldarg, argIndex); il.Emit(OpCodes.Call, _pDelegateToNative); });
                    cleanup.Add(() => { il.Emit(OpCodes.Ldarg, argIndex); il.Emit(OpCodes.Call, _keepAlive); });
                    break;
                case Kind.SafeHandle:
                {
                    var added = Local(body, _m.TypeSystem.Boolean);
                    var h = Local(body, _intPtr);
                    il.Emit(OpCodes.Ldarg, argIndex); il.Emit(OpCodes.Ldloca, added); il.Emit(OpCodes.Call, _pAddRef); il.Emit(OpCodes.Stloc, h);
                    pushes.Add(() => il.Emit(OpCodes.Ldloc, h));
                    cleanup.Add(() => { il.Emit(OpCodes.Ldarg, argIndex); il.Emit(OpCodes.Ldloc, added); il.Emit(OpCodes.Call, _pRelease); });
                    break;
                }
                case Kind.OutSafeHandle:
                {
                    // the handle the function hands back, owned by a new instance of the declared type
                    var h = Local(body, _intPtr);
                    pushes.Add(() => { il.Emit(OpCodes.Ldloca, h); il.Emit(OpCodes.Conv_U); });
                    after.Add(() =>
                    {
                        il.Emit(OpCodes.Ldarg, argIndex);
                        il.Emit(OpCodes.Ldloc, h); il.Emit(OpCodes.Ldtoken, a.Iface!); il.Emit(OpCodes.Call, _pNewSafeHandle); il.Emit(OpCodes.Castclass, a.Iface!);
                        il.Emit(OpCodes.Stind_Ref);
                    });
                    break;
                }
                case Kind.HandleRef:
                    pushes.Add(() => { il.Emit(OpCodes.Ldarga, argIndex); il.Emit(OpCodes.Call, _handleRefHandle); });
                    cleanup.Add(() => { il.Emit(OpCodes.Ldarga, argIndex); il.Emit(OpCodes.Call, _handleRefWrapper); il.Emit(OpCodes.Call, _keepAlive); });
                    break;
                default:
                    Prepare(body, il, a, argIndex, managed, pushes, cleanup, after);
                    break;
            }
        }

        private void FromNativePInvoke(ILProcessor il, Arg a, TypeReference managed)
        {
            switch (a.Kind)
            {
                case Kind.SafeHandle:
                    il.Emit(OpCodes.Ldtoken, a.Iface!); il.Emit(OpCodes.Call, _pNewSafeHandle); il.Emit(OpCodes.Castclass, a.Iface!);
                    break;
                case Kind.Delegate:
                    il.Emit(OpCodes.Ldtoken, a.Iface!); il.Emit(OpCodes.Call, _pDelegateFromNative); il.Emit(OpCodes.Castclass, a.Iface!);
                    break;
                case Kind.StrA:
                    il.Emit(OpCodes.Call, _pTakeAnsi);
                    break;
                default:
                    FromNative(il, a, managed);
                    break;
            }
        }

        // ---- functions without an implementation -------------------------------------------------

        /// <summary>The logged failure (WpfWebGpu.Win32's Exports.Unimplemented: FALSE/NULL with
        /// ERROR_CALL_NOT_IMPLEMENTED, E_NOTIMPL, ...), as the import's return type.</summary>
        private void EmitUnimplemented(ILProcessor il, MethodDefinition p, string module, string name, Win32Catalog.Failure failure)
        {
            if (_cat!.Unimplemented is { } u)
            {
                il.Emit(OpCodes.Ldstr, module); il.Emit(OpCodes.Ldstr, name); il.Emit(OpCodes.Ldc_I4, (int)failure);
                il.Emit(OpCodes.Call, _m.ImportReference(u));
            }
            else il.Emit(OpCodes.Ldc_I8, 0L);
            var rt = StripMods(p.ReturnType);
            if (!_preserveSig)
            {
                // the failure is an HRESULT, thrown as the runtime throws it (E_NOTIMPL: NotImplementedException)
                il.Emit(OpCodes.Conv_I4); il.Emit(OpCodes.Call, _check);
                if (rt.FullName != "System.Void") EmitZero(il, p, rt);
                il.Emit(OpCodes.Ret);
                return;
            }
            if (rt.FullName == "System.Void") { il.Emit(OpCodes.Pop); il.Emit(OpCodes.Ret); return; }
            if (IsSafeHandle(rt))
            {
                il.Emit(OpCodes.Conv_I); il.Emit(OpCodes.Ldtoken, rt); il.Emit(OpCodes.Call, _pNewSafeHandle); il.Emit(OpCodes.Castclass, rt);
            }
            else if (rt.FullName == "System.Boolean") { il.Emit(OpCodes.Ldc_I8, 0L); il.Emit(OpCodes.Cgt_Un); }
            else if (Cat(rt) is StackCat.Struct or StackCat.Ref or StackCat.Other)
            {
                il.Emit(OpCodes.Pop);
                EmitZero(il, p, rt);
            }
            else Coerce(il, p, _m.TypeSystem.Int64, rt);
            il.Emit(OpCodes.Ret);
        }

        /// <summary>The zero of <paramref name="t"/> on the stack.</summary>
        private void EmitZero(ILProcessor il, MethodDefinition body, TypeReference t)
        {
            switch (Cat(t))
            {
                case StackCat.I4: case StackCat.U4: il.Emit(OpCodes.Ldc_I4_0); break;
                case StackCat.I8: il.Emit(OpCodes.Ldc_I8, 0L); break;
                case StackCat.I: il.Emit(OpCodes.Ldc_I4_0); il.Emit(OpCodes.Conv_I); break;
                case StackCat.R4: il.Emit(OpCodes.Ldc_R4, 0f); break;
                case StackCat.R8: il.Emit(OpCodes.Ldc_R8, 0d); break;
                case StackCat.Ref: il.Emit(OpCodes.Ldnull); break;
                default:
                {
                    var v = Local(body, t);
                    il.Emit(OpCodes.Ldloca, v); il.Emit(OpCodes.Initobj, t); il.Emit(OpCodes.Ldloc, v);
                    break;
                }
            }
        }

        // ---- the implementation's types ------------------------------------------------------------

        private enum StackCat { I4, U4, I8, I, R4, R8, Struct, Ref, Other }

        private StackCat Cat(TypeReference t)
        {
            t = StripMods(t);
            if (t.IsPointer || t.IsFunctionPointer || t is ByReferenceType) return StackCat.I;
            switch (t.FullName)
            {
                case "System.IntPtr": case "System.UIntPtr": return StackCat.I;
                case "System.Int32": case "System.Int16": case "System.SByte": return StackCat.I4;
                case "System.UInt32": case "System.UInt16": case "System.Byte": case "System.Char": case "System.Boolean": return StackCat.U4;
                case "System.Int64": case "System.UInt64": return StackCat.I8;
                case "System.Single": return StackCat.R4;
                case "System.Double": return StackCat.R8;
                case "System.Void": return StackCat.Other;
            }
            if (t.IsGenericParameter) return StackCat.Other;
            var d = Safe(t);
            if (d == null) return t.IsValueType ? StackCat.Struct : StackCat.Ref;
            if (d.IsEnum) return Cat(d.Fields.First(f => !f.IsStatic).FieldType);
            return d.IsValueType ? StackCat.Struct : StackCat.Ref;
        }

        /// <summary>The value on the stack, of the marshalled type <paramref name="from"/>, as the implementation's
        /// <paramref name="to"/>: integers widened or narrowed as the native call would see them, a struct of the
        /// app's read as the implementation's struct of the same layout.</summary>
        private void Coerce(ILProcessor il, MethodDefinition body, TypeReference from, TypeReference to)
        {
            from = StripMods(from); to = StripMods(to);
            if (from.FullName == to.FullName) return;
            StackCat f = Cat(from), t = Cat(to);
            bool unsignedFrom = f == StackCat.U4 || from.FullName is "System.UIntPtr" or "System.UInt64" || from.IsPointer;
            switch (t)
            {
                case StackCat.I:
                    if (f is StackCat.I4) il.Emit(OpCodes.Conv_I);
                    else if (f is StackCat.U4) il.Emit(OpCodes.Conv_U);
                    else if (f is StackCat.I8) il.Emit(unsignedFrom ? OpCodes.Conv_U : OpCodes.Conv_I);
                    else if (f is StackCat.Struct) Reinterpret(il, body, from, to);
                    return;
                case StackCat.I4:
                case StackCat.U4:
                    if (f is StackCat.I or StackCat.I8) il.Emit(t == StackCat.I4 ? OpCodes.Conv_I4 : OpCodes.Conv_U4);
                    else if (f is StackCat.Struct) Reinterpret(il, body, from, to);
                    return;
                case StackCat.I8:
                    if (f is StackCat.I4) il.Emit(OpCodes.Conv_I8);
                    else if (f is StackCat.U4) il.Emit(OpCodes.Conv_U8);
                    else if (f is StackCat.I) il.Emit(unsignedFrom ? OpCodes.Conv_U8 : OpCodes.Conv_I8);
                    else if (f is StackCat.Struct) Reinterpret(il, body, from, to);
                    return;
                case StackCat.R4:
                    if (f is StackCat.R8) il.Emit(OpCodes.Conv_R4);
                    else if (f is StackCat.Struct) Reinterpret(il, body, from, to);
                    return;
                case StackCat.R8:
                    if (f is StackCat.R4) il.Emit(OpCodes.Conv_R8);
                    else if (f is StackCat.Struct) Reinterpret(il, body, from, to);
                    return;
                case StackCat.Struct:
                    if (f != StackCat.Ref) Reinterpret(il, body, from, to);
                    return;
            }
        }

        /// <summary>The bits of the <paramref name="from"/> on the stack as a <paramref name="to"/> (the app's POINT
        /// as the implementation's): through a local, never reading or writing past the smaller of the two.</summary>
        private void Reinterpret(ILProcessor il, MethodDefinition body, TypeReference from, TypeReference to)
        {
            var src = Local(body, from);
            il.Emit(OpCodes.Stloc, src);
            int fs = NativeLayout(from, 0)?.Size ?? 0, ts = NativeLayout(to, 0)?.Size ?? 0;
            if (fs > 0 && ts > 0 && fs < ts)
            {
                var dst = Local(body, to);
                il.Emit(OpCodes.Ldloca, dst); il.Emit(OpCodes.Initobj, to);
                il.Emit(OpCodes.Ldloca, dst); il.Emit(OpCodes.Ldloc, src); il.Emit(OpCodes.Stobj, from);
                il.Emit(OpCodes.Ldloc, dst);
            }
            else
            {
                il.Emit(OpCodes.Ldloca, src); il.Emit(OpCodes.Ldobj, to);
            }
        }

        // ---- Windows ------------------------------------------------------------------------------

        /// <summary>Windows: the implementation for the stack's windows, the real function for any other.</summary>
        private void RouteOnWindows(MethodDefinition p, string member, string module, string name, MethodDefinition? implDef, int mask, bool unicode)
        {
            List<Arg>? args = null; Arg? ret = null; string? why = null;
            bool managed = implDef != null && (mask & 0xFFFF) != 0;
            if (managed && !Plan(p, implDef!, unicode, out args, out ret, out why))
            {
                _r.Win32Unsupported.Add($"{member} ({module}!{name}): {why}; it stays on the real function");
                managed = false;
                if ((mask >> 16) == 0) return;
            }
            if (AnalyzeOnly) { _r.Win32Routed++; return; }
            var native = NativeCopy(p);
            bool lastError = p.PInvokeInfo.SupportsLastError;
            ToStub(p, il =>
            {
                // the drawing a Graphics has pending on a device context argument, written to it first
                if (_cat!.FlushDC is { } flush)
                    for (int k = 0; k < 15 && k < p.Parameters.Count; k++)
                        if ((mask & (1 << (16 + k))) != 0 && LoadHandle(il, p, k)) il.Emit(OpCodes.Call, _m.ImportReference(flush));
                var real = il.Create(OpCodes.Nop);
                if (!managed) il.Emit(OpCodes.Br, real);
                else if ((mask & Win32Catalog.Always) == 0)
                {
                    var stack = il.Create(OpCodes.Nop);
                    if (_cat.OnStack is { } onStack)
                        for (int k = 0; k < 15 && k < p.Parameters.Count; k++)
                            if ((mask & (1 << k)) != 0 && LoadHandle(il, p, k)) { il.Emit(OpCodes.Call, _m.ImportReference(onStack)); il.Emit(OpCodes.Brtrue, stack); }
                    il.Emit(OpCodes.Br, real);
                    il.Append(stack);
                }
                if (managed) EmitImplementationCall(p, il, implDef!, args!, ret, lastError);
                il.Append(real);
                for (int k = 0; k < p.Parameters.Count; k++) il.Emit(OpCodes.Ldarg, k);
                il.Emit(OpCodes.Call, native);
                il.Emit(OpCodes.Ret);
            });
            _r.Win32Routed++;
        }

        /// <summary>Argument <paramref name="k"/> as a handle value (nint) on the stack; false when its type
        /// carries none.</summary>
        private bool LoadHandle(ILProcessor il, MethodDefinition p, int k)
        {
            var t = StripMods(p.Parameters[k].ParameterType);
            switch (Cat(t))
            {
                case StackCat.I when t is not ByReferenceType: il.Emit(OpCodes.Ldarg, k); return true;
                case StackCat.I4: il.Emit(OpCodes.Ldarg, k); il.Emit(OpCodes.Conv_I); return true;
                case StackCat.U4: il.Emit(OpCodes.Ldarg, k); il.Emit(OpCodes.Conv_U); return true;
                case StackCat.I8: il.Emit(OpCodes.Ldarg, k); il.Emit(OpCodes.Conv_I); return true;
            }
            if (t.FullName == "System.Runtime.InteropServices.HandleRef") { il.Emit(OpCodes.Ldarga, k); il.Emit(OpCodes.Call, _handleRefHandle); return true; }
            if (IsSafeHandle(t)) { il.Emit(OpCodes.Ldarg, k); il.Emit(OpCodes.Call, _pHandleOf); return true; }
            return false;
        }
    }
}
