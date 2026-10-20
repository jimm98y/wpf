// win32shim-gen: writes the table of every Windows export, which WpfMac.Win32 embeds (win32_exports.tsv)
// and the P/Invoke rewriter reads (Documentation/pinvoke-rewriter.md).
//
// For EVERY function the Windows API metadata (Windows.Win32.winmd) says a Windows DLL exports: its DLL and
// what it returns when it fails, which is what a rewritten P/Invoke of it returns when nothing implements
// it (logged once). A function no Windows DLL exports is an EntryPointNotFoundException at the call, as on
// Windows, so the table has to be complete for apps nobody has audited.
//
// Output, into the directory given:
//   win32_exports.tsv   "module<TAB>name<TAB>failure" per export, then "module<TAB>*" per DLL
//
// Besides the SDK metadata, extra-exports.txt lists functions apps P/Invoke that it does not describe:
// the WDK's (ntdll's RtlGetVersion), undocumented ones (SetWindowCompositionAttribute) and imports by
// ordinal ("#135").

using System.Collections.Immutable;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text;

namespace WpfMac.Win32Native;

internal static class Generator
{
    // What an unimplemented function returns. Mirrored in WpfMac.ComRewrite's Win32Catalog.Failure and
    // WpfMac.Win32's Exports.Unimplemented.
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
        string winmd = args.Length > 1 && args[1].Length > 0 ? args[1] : typeof(Generator).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .First(a => a.Key == "Win32Metadata").Value!;

        var exports = Read(winmd);
        string extras = Path.Combine(AppContext.BaseDirectory, "extra-exports.txt");
        if (args.Length > 2) extras = args[2];
        if (File.Exists(extras)) AddExtras(exports, extras);
        Directory.CreateDirectory(outDir);
        File.WriteAllLines(Path.Combine(outDir, "win32_exports.tsv"),
            exports.Select(e => $"{e.Module}\t{e.Name}\t{(int)e.Failure}")
                .Concat(exports.Select(e => e.Module).Concat(ExtraModules).Distinct().OrderBy(m => m, StringComparer.Ordinal).Select(m => m + "\t*")));
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
                string module = Normalize(md.GetString(md.GetModuleReference(import.Module).Name));
                if (entry.Length == 0 || !entry.All(c => char.IsAsciiLetterOrDigit(c) || c == '_')) continue;
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

    /// <summary>
    /// extra-exports.txt: "module name failure" per line (failure: void, zero, hresult, ntstatus,
    /// win32error, invalidhandle); '#' starts a comment unless it begins the name of an ordinal.
    /// </summary>
    // DLL names from extra-exports.txt that the metadata attributes no function to (shcore's functions
    // are filed under an API set there, but apps import them from "shcore").
    private static readonly HashSet<string> ExtraModules = new(StringComparer.Ordinal);

    private static void AddExtras(List<Export> exports, string path)
    {
        var names = exports.Select(e => e.Name).ToHashSet(StringComparer.Ordinal);
        foreach (string raw in File.ReadAllLines(path))
        {
            string line = raw.Trim();
            if (line.Length == 0 || line.StartsWith("//", StringComparison.Ordinal)) continue;
            string[] parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2) continue;
            ExtraModules.Add(Normalize(parts[0]));
            if (names.Contains(parts[1])) continue;
            Failure failure = parts.Length > 2 ? parts[2].ToLowerInvariant() switch
            {
                "void" => Failure.Void, "hresult" => Failure.HResult, "ntstatus" => Failure.NtStatus,
                "win32error" => Failure.Win32Error, "invalidhandle" => Failure.InvalidHandle, _ => Failure.Zero,
            } : Failure.Zero;
            exports.Add(new Export(parts[1], Normalize(parts[0]), failure));
            names.Add(parts[1]);
        }
        exports.Sort((a, b) => StringComparer.Ordinal.Compare(a.Name, b.Name));
    }

    /// <summary>USER32.dll -> user32; api-ms-win-core-com-l1-1-0.dll -> api-ms-win-core-com-l1-1-0.</summary>
    internal static string Normalize(string module)
    {
        string m = module.ToLowerInvariant();
        return m.EndsWith(".dll", StringComparison.Ordinal) ? m[..^4] : m;
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
