// An app's calls through native function pointers (IL calli, unmanaged): SharpDX makes every COM call this
// way, the Direct3D shim's vtables among them. Mono on WebAssembly calls a native function only through an
// interp-to-native trampoline for its signature, and its build makes one for each signature it finds on a
// P/Invoke -- never for a calli. A calli whose signature no P/Invoke has stopped the runtime
// (aot-runtime-wasm.c: no trampoline): SharpDX's ID3D11DeviceContext::ClearDepthStencilView, (I, I, I, F, I)
// returning nothing, was the first. Each module with such calls gets a class of P/Invoke declarations of the
// same signatures -- never called, never bound -- so that the build makes their trampolines.

using Mono.Cecil;
using Mono.Cecil.Cil;

namespace WpfWebGpu.ComRewrite;

public static class CalliSignatures
{
    private const string ClassName = "<WpfWebGpuCalliSignatures>";

    /// <summary>Whether <paramref name="module"/> calls through unmanaged function pointers (metadata and bodies).</summary>
    public static bool Uses(ModuleDefinition module)
    {
        if (module.GetType(ClassName) != null) return false;
        foreach (TypeDefinition t in module.GetTypes())
            foreach (MethodDefinition m in t.Methods)
                if (m.HasBody && m.Body.Instructions.Any(IsUnmanagedCalli)) return true;
        return false;
    }

    private static bool IsUnmanagedCalli(Instruction i)
        => i.OpCode == OpCodes.Calli && i.Operand is CallSite cs
           && cs.CallingConvention is not (MethodCallingConvention.Default or MethodCallingConvention.Generic or MethodCallingConvention.VarArg);

    /// <summary>Adds the declarations; how many signatures.</summary>
    public static int Apply(ModuleDefinition module)
    {
        var seen = new Dictionary<string, (TypeReference Ret, TypeReference[] Params)>(StringComparer.Ordinal);
        foreach (TypeDefinition t in module.GetTypes())
            foreach (MethodDefinition m in t.Methods)
            {
                if (!m.HasBody) continue;
                foreach (Instruction i in m.Body.Instructions)
                {
                    if (!IsUnmanagedCalli(i)) continue;
                    var cs = (CallSite)i.Operand;
                    TypeReference? ret = Primitive(module, cs.ReturnType, result: true);
                    if (ret == null) continue;
                    var ps = new TypeReference[cs.Parameters.Count];
                    bool ok = true;
                    for (int k = 0; k < ps.Length && ok; k++) ok = (ps[k] = Primitive(module, cs.Parameters[k].ParameterType, result: false)!) != null && ps[k].FullName != "System.Void";
                    if (!ok) continue;
                    string key = ret.FullName + "(" + string.Join(",", ps.Select(p => p.FullName)) + ")";
                    seen.TryAdd(key, (ret, ps));
                }
            }
        if (seen.Count == 0) return 0;
        var cls = new TypeDefinition("", ClassName, TypeAttributes.NotPublic | TypeAttributes.Abstract | TypeAttributes.Sealed | TypeAttributes.BeforeFieldInit, module.TypeSystem.Object);
        module.Types.Add(cls);
        var lib = new ModuleReference("wpfwebgpu-calli-signatures");
        module.ModuleReferences.Add(lib);
        int n = 0;
        foreach (var (ret, ps) in seen.Values)
        {
            var md = new MethodDefinition("Signature" + n, MethodAttributes.Assembly | MethodAttributes.Static | MethodAttributes.HideBySig | MethodAttributes.PInvokeImpl, ret)
            {
                ImplAttributes = MethodImplAttributes.PreserveSig,
                PInvokeInfo = new PInvokeInfo(PInvokeAttributes.CallConvCdecl | PInvokeAttributes.NoMangle, "signature" + n, lib),
            };
            for (int k = 0; k < ps.Length; k++) md.Parameters.Add(new ParameterDefinition("a" + k, ParameterAttributes.None, ps[k]));
            cls.Methods.Add(md);
            n++;
        }
        return n;
    }

    // what the trampoline passes the value as: a word, a long, a float or a double (a pointer, a reference,
    // an enum, a struct of one such field: what it is made of; any other struct by reference, a word, as
    // Mono passes a struct that is not a scalar); null for what it cannot say (a returned struct that is not
    // a scalar, which Mono returns through memory of its own)
    private static TypeReference? Primitive(ModuleDefinition module, TypeReference t, bool result)
    {
        var ts = module.TypeSystem;
        if (t is ByReferenceType or PointerType || t.IsFunctionPointer) return ts.IntPtr;
        if (t is RequiredModifierType rm) return Primitive(module, rm.ElementType, result);
        if (t is OptionalModifierType om) return Primitive(module, om.ElementType, result);
        switch (t.FullName)
        {
            case "System.Void": return ts.Void;
            case "System.Int64": case "System.UInt64": return ts.Int64;
            case "System.Single": return ts.Single;
            case "System.Double": return ts.Double;
            case "System.IntPtr": case "System.UIntPtr": return ts.IntPtr;
            case "System.Boolean": case "System.Char": case "System.SByte": case "System.Byte": case "System.Int16": case "System.UInt16":
            case "System.Int32": case "System.UInt32": return ts.Int32;
        }
        TypeDefinition? d;
        try { d = t.Resolve(); } catch { return null; }
        if (d == null) return null;
        if (!d.IsValueType) return ts.IntPtr;                 // a class, an interface, a string: a pointer
        var fields = d.Fields.Where(f => !f.IsStatic).ToList();
        if (d.IsEnum) return Primitive(module, fields[0].FieldType, result);
        if (fields.Count == 1) return Primitive(module, fields[0].FieldType, result);
        return result ? null : ts.IntPtr;
    }
}
