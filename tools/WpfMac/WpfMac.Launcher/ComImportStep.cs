// The COM-import rewrite as a step of preparing an app (Documentation/comimport-rewriter.md): every app
// assembly that declares [ComImport] types is judged, rewritten into the cache when it may be, and the
// merged deps.json then names the rewritten copy instead of the app's file.
//
// Windows: all or nothing per assembly -- rewritten only when the rewriter supports every ComImport use
// in it, else left on built-in COM. Elsewhere there is no built-in COM: what is supported is rewritten,
// and the rest throws by name when called.

using System.Security.Cryptography;
using System.Text.Json.Nodes;
using WpfMac.ComRewrite;

namespace WpfMac;

internal static class ComImportStep
{
    /// <summary>The library the rewritten copies are listed under; its folder is under the cache root.</summary>
    public const string Library = "WpfMac.ComImport/1.0.0";
    public const string Folder = "comimport";
    /// <summary>Bumped when the rewriter's output changes, so cached copies are made again.</summary>
    private const string Version = "2";

    /// <summary>WPF_COMIMPORT_REWRITE: on (default), off, or a ';'-separated list of assembly names.</summary>
    internal static Func<string, bool> Scope(string? setting)
    {
        if (string.IsNullOrWhiteSpace(setting) || setting.Equals("on", StringComparison.OrdinalIgnoreCase)) return _ => true;
        if (setting.Equals("off", StringComparison.OrdinalIgnoreCase)) return _ => false;
        var names = setting.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(n => new System.Text.RegularExpressions.Regex("^" + System.Text.RegularExpressions.Regex.Escape(n).Replace("\\*", ".*") + "$",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase)).ToList();
        return name => names.Any(r => r.IsMatch(name));
    }

    /// <summary>
    /// Rewrites what may be rewritten among the app's runtime assemblies in <paramref name="deps"/> and
    /// points the deps at the copies under <paramref name="cacheRoot"/>/comimport. One line per ComImport
    /// assembly goes to <paramref name="notes"/>.
    /// </summary>
    public static void Apply(JsonObject deps, string appDir, string overlayDir, string cacheRoot, bool windows, string? setting, List<string> notes)
    {
        var inScope = Scope(setting);
        string targetName = deps["runtimeTarget"]?["name"]?.GetValue<string>() ?? "";
        if (deps["targets"]?[targetName] is not JsonObject target || deps["libraries"] is not JsonObject libraries) return;
        var outRuntime = new JsonObject();
        string store = Path.Combine(cacheRoot, Folder);
        string[] searchDirs = { appDir, Path.Combine(overlayDir), System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory() };

        foreach (var (libKey, libNode) in target.ToList())
        {
            if (libKey == DepsMerger.OverlayLibrary || libKey.StartsWith("WpfMac.", StringComparison.Ordinal)) continue;      // the stack's own: never
            if (libNode is not JsonObject lib || lib["runtime"] is not JsonObject runtime) continue;
            foreach (var (asset, props) in runtime.ToList())
            {
                string file = Path.Combine(appDir, asset);
                if (!File.Exists(file) || !ComRewriter.DeclaresComImport(file)) continue;
                string name = Path.GetFileNameWithoutExtension(file);
                if (!inScope(name)) { notes.Add($"comimport {name}: left (WPF_COMIMPORT_REWRITE)"); continue; }
                bool partial = !windows;
                string hash = Hash(file) + (partial ? "p" : "") + Version;
                string outRel = Path.Combine(hash, Path.GetFileName(file));
                string outPath = Path.Combine(store, outRel);
                string reportPath = outPath + ".txt";
                string verdict;
                bool written;
                if (File.Exists(reportPath))
                {
                    verdict = File.ReadAllText(reportPath);
                    written = File.Exists(outPath);
                }
                else
                {
                    RewriteResult r;
                    try { r = ComRewriter.Rewrite(file, outPath, searchDirs, partial); }
                    catch (Exception e) { notes.Add($"comimport {name}: left (the rewriter failed: {e.GetType().Name}: {e.Message})"); continue; }
                    written = r.Written;
                    verdict = r.ToString();
                    Directory.CreateDirectory(Path.GetDirectoryName(reportPath)!);
                    File.WriteAllText(reportPath, verdict);
                }
                notes.Add($"comimport {name}: {(written ? "rewritten" : "left on built-in COM")} -- {verdict}");
                if (!written) continue;
                runtime.Remove(asset);
                outRuntime[outRel.Replace('\\', '/')] = props?.DeepClone() ?? new JsonObject();
            }
            if (runtime.Count == 0) lib.Remove("runtime");
        }
        if (outRuntime.Count == 0) return;
        target[Library] = new JsonObject { ["runtime"] = outRuntime };
        libraries[Library] = new JsonObject
        {
            ["type"] = "package",
            ["serviceable"] = false,
            ["sha512"] = "",
            ["path"] = Folder,
        };
    }

    private static string Hash(string file)
    {
        using var fs = File.OpenRead(file);
        return Convert.ToHexString(SHA256.HashData(fs))[..32].ToLowerInvariant();
    }
}
