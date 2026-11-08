// Workarounds for the testing app: imports of the platform's own libraries that are declared wrongly, which fail
// on the platform they are written for as much as here -- repaired as the app's assemblies are rewritten, since
// the stack is where they run.
//
//   CoreFoundation's CFStringCreateWithCString declared with the string alone. The function takes three arguments
//   (CFAllocatorRef, const char *, CFStringEncoding): the string went in as the allocator and the call returned
//   NULL -- which the app handed CFRunLoopAddSource as its run-loop mode, and CoreFoundation trapped on hashing it,
//   again and again, a core spinning for as long as the process ran. The import becomes a method calling the
//   function as it is: the default allocator, the string, UTF-8.

using Mono.Cecil;
using Mono.Cecil.Cil;

namespace WpfWebGpu.ComRewrite;

public static partial class ComRewriter
{
    private const uint kCFStringEncodingUTF8 = 0x08000100;

    /// <summary>Whether an import (its module, entry point and parameter count) is one the workarounds repair.</summary>
    internal static bool NeedsWorkaround(string module, string entryPoint, int parameters) =>
        entryPoint == "CFStringCreateWithCString" && parameters == 1
        && module.EndsWith("CoreFoundation", StringComparison.OrdinalIgnoreCase);

    /// <summary>Whether the file has an import the workarounds repair (metadata only).</summary>
    internal static bool HasWorkaround(System.Reflection.Metadata.MetadataReader md, System.Reflection.Metadata.MethodDefinition m)
    {
        var import = m.GetImport();
        if (import.Module.IsNil) return false;
        string module = md.GetString(md.GetModuleReference(import.Module).Name);
        string ep = import.Name.IsNil ? md.GetString(m.Name) : md.GetString(import.Name);
        if (ep.Length == 0) ep = md.GetString(m.Name);
        var sig = md.GetBlobReader(m.Signature);
        sig.ReadSignatureHeader();
        return NeedsWorkaround(module, ep, sig.ReadCompressedInteger());
    }

    private sealed partial class Work
    {
        /// <summary>Repairs <paramref name="p"/> when it is one of the wrongly declared imports; false otherwise.</summary>
        private bool ApplyWorkaround(MethodDefinition p)
        {
            var info = p.PInvokeInfo;
            string ep = string.IsNullOrEmpty(info.EntryPoint) ? p.Name : info.EntryPoint;
            if (!NeedsWorkaround(info.Module.Name, ep, p.Parameters.Count) || p.Parameters[0].ParameterType.MetadataType != MetadataType.String
                || p.ReturnType.MetadataType != MetadataType.IntPtr) return false;
            if (AnalyzeOnly) return true;

            // the import as the function is: CFStringCreateWithCString(CFAllocatorRef, const char *, CFStringEncoding)
            var real = new MethodDefinition(p.Name + "<as declared by CoreFoundation>",
                MethodAttributes.Private | MethodAttributes.Static | MethodAttributes.PInvokeImpl | MethodAttributes.HideBySig, p.ReturnType)
            {
                ImplAttributes = MethodImplAttributes.PreserveSig,
                PInvokeInfo = new PInvokeInfo(PInvokeAttributes.CharSetAnsi | PInvokeAttributes.CallConvWinapi | PInvokeAttributes.NoMangle, ep, info.Module),
            };
            real.Parameters.Add(new ParameterDefinition("alloc", ParameterAttributes.None, _m.TypeSystem.IntPtr));
            real.Parameters.Add(new ParameterDefinition("cStr", ParameterAttributes.None, _m.TypeSystem.String));
            real.Parameters.Add(new ParameterDefinition("encoding", ParameterAttributes.None, _m.TypeSystem.UInt32));
            p.DeclaringType.Methods.Add(real);

            // the app's import: a method calling it with the default allocator and UTF-8
            ToStub(p, il =>
            {
                il.Emit(OpCodes.Ldc_I4_0);
                il.Emit(OpCodes.Conv_I);
                il.Emit(OpCodes.Ldarg_0);
                il.Emit(OpCodes.Ldc_I4, unchecked((int)kCFStringEncodingUTF8));
                il.Emit(OpCodes.Call, real);
                il.Emit(OpCodes.Ret);
            });
            _r.Workarounds.Add($"{p.DeclaringType.FullName}.{p.Name}: CFStringCreateWithCString called with its allocator and encoding");
            return true;
        }
    }
}
