// win32shim-gen: writes the sources of libwin32shim, the library macOS hands an unmodified Windows app
// when it P/Invokes user32, kernel32, gdi32 and the rest of Windows.
//
// For EVERY function the Windows API metadata (Windows.Win32.winmd) says a Windows DLL exports, the
// library exports a symbol of that name. Each is a three-instruction arm64 stub that jumps through a
// slot. The slot starts out pointing at a per-function default that tells the managed side which
// function was called (once, for the compatibility log) and returns that function's failure value;
// the bootstrap overwrites the slot with a managed [UnmanagedCallersOnly] implementation for the
// functions that have one. The stubs never look at the arguments, so one shape serves every signature.
//
// Why every function and not just the ones some app uses: a missing export is an
// EntryPointNotFoundException at the call, so coverage has to be complete for apps nobody has audited.
// The cost is ~20k stubs of 12 bytes plus a slot each.
//
// Output, into the directory given:
//   win32shim_stubs.s     the stubs, the default thunks and the slot table
//   win32shim_tables.c    names, the module each comes from, and the failure each returns
//   win32shim_modules.txt the DLL names covered, one per line (lower case, no .dll)

using System.Collections.Immutable;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text;

namespace WpfMac.Win32Native;

internal static class Generator
{
    // What an unimplemented function returns. Mirrored in WpfMac.Bootstrap/Win32/ShimLibrary.cs.
    private enum Failure : byte
    {
        Void = 0,
        Zero = 1,           // FALSE, NULL, 0: with GetLastError() = ERROR_CALL_NOT_IMPLEMENTED
        HResult = 2,        // E_NOTIMPL
        NtStatus = 3,       // STATUS_NOT_IMPLEMENTED
        Win32Error = 4,     // ERROR_CALL_NOT_IMPLEMENTED, returned rather than set
        InvalidHandle = 5,  // INVALID_HANDLE_VALUE (-1)
    }

    // Functions whose HANDLE result signals failure with INVALID_HANDLE_VALUE rather than NULL. The
    // metadata types the result as plain HANDLE either way, so this cannot be derived from it.
    private static readonly HashSet<string> s_invalidHandleOnFailure = new(StringComparer.Ordinal)
    {
        "CreateFileA", "CreateFileW", "CreateFile2", "CreateFileTransactedA", "CreateFileTransactedW",
        "FindFirstFileA", "FindFirstFileW", "FindFirstFileExA", "FindFirstFileExW", "FindFirstFileTransactedA",
        "FindFirstFileTransactedW", "FindFirstFileNameW", "FindFirstStreamW", "FindFirstVolumeA", "FindFirstVolumeW",
        "FindFirstChangeNotificationA", "FindFirstChangeNotificationW", "CreateToolhelp32Snapshot",
        "CreateNamedPipeA", "CreateNamedPipeW", "CreateMailslotA", "CreateMailslotW", "CreateConsoleScreenBuffer",
        "GetStdHandle", "OpenEventLogA", "OpenEventLogW",
    };

    private sealed record Export(string Name, string Module, Failure Failure);

    public static int Main(string[] args)
    {
        if (args.Length < 1)
        {
            Console.Error.WriteLine("usage: win32shim-gen <output dir> [Windows.Win32.winmd]");
            return 2;
        }
        string outDir = args[0];
        string winmd = args.Length > 1 ? args[1] : typeof(Generator).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .First(a => a.Key == "Win32Metadata").Value!;

        var exports = Read(winmd);
        Directory.CreateDirectory(outDir);
        WriteStubs(Path.Combine(outDir, "win32shim_stubs.s"), exports);
        WriteTables(Path.Combine(outDir, "win32shim_tables.c"), exports);
        File.WriteAllLines(Path.Combine(outDir, "win32shim_modules.txt"),
            exports.Select(e => e.Module).Distinct().OrderBy(m => m, StringComparer.Ordinal));
        Console.WriteLine($"win32shim-gen: {exports.Count} exports from {exports.Select(e => e.Module).Distinct().Count()} modules");
        return 0;
    }

    private static List<Export> Read(string winmd)
    {
        using var pe = new PEReader(File.OpenRead(winmd));
        var md = pe.GetMetadataReader();
        var provider = new ReturnTypeNames();
        var byName = new Dictionary<string, Export>(StringComparer.Ordinal);

        foreach (var th in md.TypeDefinitions)
        {
            var type = md.GetTypeDefinition(th);
            if (md.GetString(type.Name) != "Apis") continue;
            foreach (var mh in type.GetMethods())
            {
                var method = md.GetMethodDefinition(mh);
                var import = method.GetImport();
                if (import.Module.IsNil) continue;

                string entry = import.Name.IsNil ? md.GetString(method.Name) : md.GetString(import.Name);
                // CRT functions (memcpy, strlen, _wcsicmp...) that ntdll and msvcrt also export: a
                // definition of those here would shadow libSystem's inside this very library.
                if (entry.Length == 0 || !char.IsUpper(entry[0]) || !entry.All(c => char.IsAsciiLetterOrDigit(c) || c == '_')) continue;

                string module = Normalize(md.GetString(md.GetModuleReference(import.Module).Name));
                if (byName.ContainsKey(entry)) continue;          // same name in two DLLs: one export

                string ret = method.DecodeSignature(provider, null).ReturnType;
                Failure failure = s_invalidHandleOnFailure.Contains(entry) ? Failure.InvalidHandle : ret switch
                {
                    "void" => Failure.Void,
                    "Windows.Win32.Foundation.HRESULT" => Failure.HResult,
                    "Windows.Win32.Foundation.NTSTATUS" => Failure.NtStatus,
                    "Windows.Win32.Foundation.WIN32_ERROR" => Failure.Win32Error,
                    _ => Failure.Zero,
                };
                byName[entry] = new Export(entry, module, failure);
            }
        }
        return byName.Values.OrderBy(e => e.Name, StringComparer.Ordinal).ToList();
    }

    /// <summary>USER32.dll -> user32; api-ms-win-core-com-l1-1-0.dll -> api-ms-win-core-com-l1-1-0.</summary>
    internal static string Normalize(string module)
    {
        string m = module.ToLowerInvariant();
        return m.EndsWith(".dll", StringComparison.Ordinal) ? m[..^4] : m;
    }

    private static void WriteStubs(string path, List<Export> exports)
    {
        var sb = new StringBuilder();
        sb.AppendLine("// GENERATED by win32shim-gen. One stub, default thunk and slot per Windows export.");
        sb.AppendLine("\t.section __TEXT,__text,regular,pure_instructions");
        for (int i = 0; i < exports.Count; i++)
        {
            string n = exports[i].Name;
            sb.Append("\t.p2align 2\n\t.globl _").Append(n).Append("\n_").Append(n).Append(":\n");
            sb.Append("\tadrp x16, _wpfmac_slots@PAGE\n");
            sb.Append("\tadd x16, x16, _wpfmac_slots@PAGEOFF\n");
            // The slot is at index*8; the immediate form of ldr only reaches 32760, so add the offset first.
            int offset = i * 8;
            if (offset <= 4095 * 8)
            {
                sb.Append("\tldr x16, [x16, #").Append(offset).Append("]\n");
            }
            else
            {
                sb.Append("\tmov x17, #").Append(offset & 0xFFFF).Append('\n');
                if (offset > 0xFFFF) sb.Append("\tmovk x17, #").Append(offset >> 16).Append(", lsl #16\n");
                sb.Append("\tldr x16, [x16, x17]\n");
            }
            sb.Append("\tbr x16\n");
            sb.Append("Ldefault_").Append(i).Append(":\n");
            sb.Append("\tmov w17, #").Append(i & 0xFFFF).Append('\n');
            if (i > 0xFFFF) sb.Append("\tmovk w17, #").Append(i >> 16).Append(", lsl #16\n");
            sb.Append("\tb _wpfmac_unimplemented\n");
        }

        sb.AppendLine("\t.section __DATA,__data");
        sb.AppendLine("\t.p2align 3");
        sb.AppendLine("\t.globl _wpfmac_slots");
        sb.AppendLine("_wpfmac_slots:");
        for (int i = 0; i < exports.Count; i++) sb.Append("\t.quad Ldefault_").Append(i).Append('\n');
        sb.AppendLine("\t.globl _wpfmac_defaults");
        sb.AppendLine("_wpfmac_defaults:");
        for (int i = 0; i < exports.Count; i++) sb.Append("\t.quad Ldefault_").Append(i).Append('\n');
        File.WriteAllText(path, sb.ToString());
    }

    private static void WriteTables(string path, List<Export> exports)
    {
        var modules = exports.Select(e => e.Module).Distinct().OrderBy(m => m, StringComparer.Ordinal).ToList();
        var moduleIndex = modules.Select((m, i) => (m, i)).ToDictionary(p => p.m, p => p.i, StringComparer.Ordinal);
        var sb = new StringBuilder();
        sb.AppendLine("// GENERATED by win32shim-gen. The managed side reads these to find a function's slot by name.");
        sb.AppendLine($"const int wpfmac_export_count = {exports.Count};");
        sb.AppendLine($"const int wpfmac_module_count = {modules.Count};");
        sb.AppendLine("const char *const wpfmac_module_names[] = {");
        foreach (string m in modules) sb.Append("\t\"").Append(m).Append("\",\n");
        sb.AppendLine("};");
        sb.AppendLine("const char *const wpfmac_export_names[] = {");
        foreach (var e in exports) sb.Append("\t\"").Append(e.Name).Append("\",\n");
        sb.AppendLine("};");
        sb.AppendLine("const unsigned short wpfmac_export_modules[] = {");
        foreach (var e in exports) sb.Append('\t').Append(moduleIndex[e.Module]).Append(",\n");
        sb.AppendLine("};");
        sb.AppendLine("const unsigned char wpfmac_export_failures[] = {");
        foreach (var e in exports) sb.Append('\t').Append((int)e.Failure).Append(",\n");
        sb.AppendLine("};");
        File.WriteAllText(path, sb.ToString());
    }

    /// <summary>Just enough of a signature decoder to name the return type.</summary>
    private sealed class ReturnTypeNames : ISignatureTypeProvider<string, object?>
    {
        public string GetPrimitiveType(PrimitiveTypeCode typeCode) => typeCode == PrimitiveTypeCode.Void ? "void" : typeCode.ToString();
        public string GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind)
        {
            var t = reader.GetTypeDefinition(handle);
            return reader.GetString(t.Namespace) + "." + reader.GetString(t.Name);
        }
        public string GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte rawTypeKind)
        {
            var t = reader.GetTypeReference(handle);
            return reader.GetString(t.Namespace) + "." + reader.GetString(t.Name);
        }
        public string GetTypeFromSpecification(MetadataReader reader, object? genericContext, TypeSpecificationHandle handle, byte rawTypeKind) => "spec";
        public string GetSZArrayType(string elementType) => elementType + "[]";
        public string GetArrayType(string elementType, ArrayShape shape) => elementType + "[,]";
        public string GetByReferenceType(string elementType) => elementType + "&";
        public string GetPointerType(string elementType) => elementType + "*";
        public string GetPinnedType(string elementType) => elementType;
        public string GetModifiedType(string modifier, string unmodifiedType, bool isRequired) => unmodifiedType;
        public string GetGenericInstantiation(string genericType, ImmutableArray<string> typeArguments) => genericType;
        public string GetGenericMethodParameter(object? genericContext, int index) => "!!" + index;
        public string GetGenericTypeParameter(object? genericContext, int index) => "!" + index;
        public string GetFunctionPointerType(MethodSignature<string> signature) => "fnptr";
    }
}
