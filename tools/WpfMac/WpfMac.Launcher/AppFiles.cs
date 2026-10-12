using System.Diagnostics;
using System.Reflection;
using System.Reflection.PortableExecutable;

namespace WpfMac;

/// <summary>The files of the app being launched. Read-only: wpfmac never writes beside them.</summary>
internal sealed class AppFiles
{
    public required string Directory { get; init; }       // full path, no trailing separator
    public required string Name { get; init; }            // App, for App.dll
    public string Dll => Path.Combine(Directory, Name + ".dll");
    public string RuntimeConfig => Path.Combine(Directory, Name + ".runtimeconfig.json");
    public string Deps => Path.Combine(Directory, Name + ".deps.json");
    public string Exe => Path.Combine(Directory, Name + ".exe");

    /// <summary>
    /// Accepts what a user would naturally point at: App.dll, App.exe (the per-RID apphost, which
    /// only starts App.dll), or the app's folder when it holds exactly one app.
    /// </summary>
    public static AppFiles Resolve(string input)
    {
        string path = Path.GetFullPath(input);
        if (System.IO.Directory.Exists(path))
        {
            string[] apps = System.IO.Directory.GetFiles(path, "*.runtimeconfig.json")
                .Select(f => Path.GetFileName(f)[..^".runtimeconfig.json".Length])
                .Where(n => File.Exists(Path.Combine(path, n + ".dll")))
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (apps.Length == 1) return Create(path, apps[0]);
            throw new UserError(apps.Length == 0
                ? $"{path} holds no .NET app (no <name>.runtimeconfig.json beside a <name>.dll)."
                : $"{path} holds {apps.Length} apps; name one: {string.Join(", ", apps.Select(a => a + ".dll"))}");
        }

        string ext = Path.GetExtension(path);
        if (!ext.Equals(".dll", StringComparison.OrdinalIgnoreCase) && !ext.Equals(".exe", StringComparison.OrdinalIgnoreCase))
        {
            throw new UserError($"{input}: expected an app's .dll, its .exe or its folder.");
        }
        var app = Create(Path.GetDirectoryName(path)!, Path.GetFileNameWithoutExtension(path));
        if (!File.Exists(app.Dll)) throw new UserError($"{app.Dll} does not exist.");
        if (!File.Exists(app.RuntimeConfig))
        {
            throw new UserError($"{app.RuntimeConfig} does not exist: {app.Name}.dll is not a framework-dependent .NET app.");
        }
        return app;
    }

    private static AppFiles Create(string dir, string name) =>
        new() { Directory = Path.TrimEndingDirectorySeparator(dir), Name = name };
}

/// <summary>An error the user caused and can fix; printed without a stack trace.</summary>
internal sealed class UserError(string message) : Exception(message);

/// <summary>What a file on disk actually is, as opposed to what a deps.json says it is.</summary>
internal static class FileIdentity
{
    public static bool IsManaged(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            using var pe = new PEReader(stream);
            return pe.HasMetadata;
        }
        catch
        {
            return false;
        }
    }

    public static Version? AssemblyVersion(string path)
    {
        try { return AssemblyName.GetAssemblyName(path).Version; }
        catch { return null; }
    }

    public static Version? FileVersion(string path)
    {
        try
        {
            var info = FileVersionInfo.GetVersionInfo(path);
            return new Version(info.FileMajorPart, info.FileMinorPart, info.FileBuildPart, info.FilePrivatePart);
        }
        catch
        {
            return null;
        }
    }

    public static bool IsNativeLibraryName(string fileName) =>
        fileName.EndsWith(".dylib", StringComparison.OrdinalIgnoreCase)
        || fileName.EndsWith(".so", StringComparison.OrdinalIgnoreCase)
        || fileName.Contains(".so.", StringComparison.OrdinalIgnoreCase)
        || fileName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase);
}
