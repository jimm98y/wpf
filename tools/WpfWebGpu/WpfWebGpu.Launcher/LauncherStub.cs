// A program's own launcher: an executable that is wpfrun under another name (.NET's apphost pointed at wpfrun.dll --
// Packager.exe on Windows, where a shortcut or a double-click needs an .exe and no console), with a <name>.wpfrun
// beside it saying what it runs. Its lines, before the arguments it was given:
//
//   run                     an argument as it is
//   @app/Packager.exe       an argument that is a path beside the launcher (made absolute)
//   set WPFPKG_PACKS=@packs an environment variable (its value a path beside the launcher where it starts with @)
//   # ...                   a comment
//
// So `Packager.exe scan C:\x` runs `wpfrun run <here>\app\Packager.exe scan C:\x` with WPFPKG_PACKS=<here>\packs.

namespace WpfWebGpu;

internal static class LauncherStub
{
    public static string[] Arguments(string[] args)
    {
        string? exe = Environment.ProcessPath;
        if (exe == null) return args;
        string name = Path.GetFileNameWithoutExtension(exe);
        if (name.Equals("dotnet", StringComparison.OrdinalIgnoreCase) || name.Equals("dotnetw", StringComparison.OrdinalIgnoreCase)) return args;
        string stub = Path.Combine(Path.GetDirectoryName(exe)!, name + ".wpfrun");
        if (!File.Exists(stub)) return args;
        string here = Path.GetDirectoryName(stub)!;
        string Value(string v) => v.StartsWith('@') ? Path.GetFullPath(Path.Combine(here, v[1..])) : v;
        var prefix = new List<string>();
        foreach (string raw in File.ReadAllLines(stub))
        {
            string line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            if (line.StartsWith("set ", StringComparison.Ordinal) && line.IndexOf('=') is int eq and > 4)
                Environment.SetEnvironmentVariable(line[4..eq].Trim(), Value(line[(eq + 1)..].Trim()));
            else
                prefix.Add(Value(line));
        }
        return prefix.Concat(args).ToArray();
    }
}
