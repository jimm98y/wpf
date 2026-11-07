// One app of the installation as a head that runs it from a bundle (a phone's, a headset's, a browser's) takes
// it: its folder's assemblies -- those the template does not hold itself, prepared for the stack (placeholders
// for what they reference and nothing ships, their Windows calls rewritten) -- and its data, beside them as on
// Windows; and the installation's registry with the app's folder as its own.

using WpfWebGpu.ComRewrite;

namespace WpfWebGpu.Packager;

public sealed class MobileApp
{
    /// <summary>The app's assembly name (App, for App.exe / App.dll).</summary>
    public required string Name { get; init; }
    /// <summary>The file it is started as, in its folder (App.exe).</summary>
    public required string Exe { get; init; }
    /// <summary>The app's folder, relative to the installation ('/'-separated).</summary>
    public required string Folder { get; init; }
    /// <summary>Each file of the app's folder in the bundle: where it comes from, its path in the folder.</summary>
    public required IReadOnlyList<(string Source, string Target)> Files { get; init; }
    public required IReadOnlyList<string> LeftOut { get; init; }

    private static readonly HashSet<string> s_nativeFolders = new(StringComparer.OrdinalIgnoreCase) { "runtimes", "x86", "x64", "arm64", "amd64" };
    private static readonly HashSet<string> s_windowsRuntime = new(StringComparer.OrdinalIgnoreCase) { "WinRT.Runtime", "Microsoft.Windows.SDK.NET" };

    /// <param name="provided">the assemblies the template holds (the stack's, the runtime's): never the app's copy</param>
    /// <param name="references">the folders holding them, where the app's references resolve</param>
    public static MobileApp Prepare(string installDir, AppEntry entry, ISet<string> provided, IReadOnlyList<string> references, string workDir, Action<string> log)
    {
        string exe = Path.Combine(installDir, entry.Exe);
        string dir = Path.GetDirectoryName(exe)!;
        string name = Path.GetFileNameWithoutExtension(AppHost.AppDll(exe) ?? exe);
        var files = new List<(string, string)>();
        var leftOut = new List<string>();

        // the folder's managed assemblies the template does not hold
        var assemblies = new List<string>();
        foreach (string dll in Directory.EnumerateFiles(dir, "*.dll").Order(StringComparer.OrdinalIgnoreCase))
        {
            string n = Path.GetFileNameWithoutExtension(dll);
            if (PeFile.TryRead(dll) is not { Managed: true } pe) continue;                 // native: Windows' own
            if (provided.Contains(n)) { if (n != name) leftOut.Add(n + " (the stack's)"); continue; }
            if (s_windowsRuntime.Contains(n)) { leftOut.Add(n + " (Windows Runtime)"); continue; }
            if (!pe.ILOnly) { leftOut.Add(n + " (mixed-mode)"); continue; }
            assemblies.Add(dll);
        }
        if (!assemblies.Any(a => Path.GetFileNameWithoutExtension(a).Equals(name, StringComparison.OrdinalIgnoreCase)))
            throw new PackagerError($"{entry.Exe}: its assembly {name}.dll is not one a bundle can run");

        // placeholders for what they reference and nothing provides, then their Windows calls onto the stack
        string placeholders = Path.Combine(workDir, "placeholders"), rewritten = Path.Combine(workDir, "rewrite");
        foreach (string d in new[] { placeholders, rewritten })
            if (Directory.Exists(d)) Directory.Delete(d, recursive: true);
        var search = references.Append(dir).ToList();
        var made = RewriteSteps.Placeholders(assemblies, provided.Concat(assemblies.Select(Path.GetFileNameWithoutExtension)).OfType<string>(), search, placeholders, log);
        var inputs = assemblies.Concat(made).ToList();
        if (Directory.Exists(placeholders)) search.Add(placeholders);
        var done = RewriteSteps.Rewrite(inputs, search, rewritten, primitives: null, log);
        var rewrittenNames = new HashSet<string>(done.Select(r => Path.GetFileName(r.Output)), StringComparer.OrdinalIgnoreCase);
        foreach (string a in inputs)
        {
            string file = Path.GetFileName(a);
            files.Add((rewrittenNames.Contains(file) ? Path.Combine(rewritten, file) : a, file));
        }

        // the data: the folder's other files, and its folders that are not a native runtime's
        foreach (string f in Directory.EnumerateFiles(dir))
        {
            string fileName = Path.GetFileName(f);
            string ext = Path.GetExtension(f).ToLowerInvariant();
            if (ext is ".dll" or ".exe" or ".pdb" or ".so" or ".dylib" || fileName.StartsWith('.')) continue;
            if (fileName.EndsWith(".deps.json", StringComparison.OrdinalIgnoreCase) || fileName.EndsWith(".runtimeconfig.json", StringComparison.OrdinalIgnoreCase)) continue;
            files.Add((f, fileName));
        }
        foreach (string sub in Directory.EnumerateDirectories(dir))
        {
            string folder = Path.GetFileName(sub);
            if (folder.StartsWith('.') || s_nativeFolders.Contains(folder)) continue;
            var all = Directory.EnumerateFiles(sub, "*", SearchOption.AllDirectories).Where(f => !Path.GetFileName(f).StartsWith('.')).ToList();
            if (all.Count == 0) continue;
            // a native SDK's runtime (its libraries and resources): nothing a bundle can use
            bool native = all.Any(f => Path.GetExtension(f).ToLowerInvariant() is ".so" or ".dylib" || (f.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) && PeFile.TryRead(f) is { Managed: false }));
            if (native && !all.Any(f => PeFile.TryRead(f) is { Managed: true })) { leftOut.Add(folder + "/ (native)"); continue; }
            foreach (string f in all) files.Add((f, Path.GetRelativePath(dir, f).Replace('\\', '/')));
        }

        return new MobileApp
        {
            Name = name,
            Exe = Path.GetFileName(exe),
            Folder = Path.GetRelativePath(installDir, dir).Replace('\\', '/'),
            Files = files,
            LeftOut = leftOut,
        };
    }

    /// <summary>A staged hive file for this app: paths into its own folder made the app's folder's
    /// (PathTokens.AppDir); the rest of the installation, and ProgramData, left to the head.</summary>
    public string MapRegistry(string hive)
    {
        if (Folder == ".") return hive.Replace(PathTokens.InstallDir, PathTokens.AppDir, StringComparison.Ordinal);
        string own = PathTokens.InstallDir + "\\" + Folder.Replace('/', '\\');
        return System.Text.RegularExpressions.Regex.Replace(hive, System.Text.RegularExpressions.Regex.Escape(own) + @"(?=\\|$|[^\w])",
            PathTokens.AppDir.Replace("$", "$$"), System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Multiline);
    }
}
