// The assemblies the host will put on the trusted list for an app launched by wpfrun: the merged
// deps.json's runtime assets (app, overlay, fetched packages) plus Microsoft.NETCore.App, resolved the
// way the host resolves them. The audit checks an app's references against exactly this set, so what
// it reports is what the running app would hit.

using System.Text.Json.Nodes;

namespace WpfWebGpu.Audit;

internal sealed class LoadSet
{
    public sealed record Entry(string Name, string Path, Version Version, string Origin);

    private readonly Dictionary<string, Entry> _byName = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyDictionary<string, Entry> ByName => _byName;

    public static LoadSet Build(JsonObject mergedDeps, IReadOnlyList<string> probeRoots, string rid)
    {
        var set = new LoadSet();
        var chain = HostInfo.RidChain(rid);
        string targetName = mergedDeps["runtimeTarget"]?["name"]?.GetValue<string>() ?? "";
        var target = mergedDeps["targets"]?[targetName] as JsonObject ?? new JsonObject();
        var libraries = mergedDeps["libraries"] as JsonObject ?? new JsonObject();

        foreach (var (libKey, libNode) in target)
        {
            if (libNode is not JsonObject lib) continue;
            string? libPath = libraries[libKey]?["path"]?.GetValue<string>();
            if (libPath == null) continue;
            string origin = libKey == DepsMerger.OverlayLibrary ? "overlay" : libKey.StartsWith("WpfWebGpu.Fetched.", StringComparison.Ordinal) ? "fetched" : "app";

            var keys = new List<string>();
            if (lib["runtime"] is JsonObject rt) keys.AddRange(rt.Select(kv => kv.Key));
            if (lib["runtimeTargets"] is JsonObject rts)
            {
                keys.AddRange(rts.Where(kv => kv.Value?["assetType"]?.GetValue<string>() == "runtime"
                                              && chain.Contains(kv.Value?["rid"]?.GetValue<string>() ?? "", StringComparer.OrdinalIgnoreCase))
                                 .Select(kv => kv.Key));
            }
            foreach (string key in keys)
            {
                string? path = probeRoots.Select(r => System.IO.Path.Combine(r, libPath, key)).FirstOrDefault(File.Exists);
                if (path == null) continue;
                set.Add(new Entry(System.IO.Path.GetFileNameWithoutExtension(key), path, FileIdentity.AssemblyVersion(path) ?? new Version(0, 0), origin));
            }
        }

        // The shared framework: an app-level assembly wins over it only with a higher version, which is
        // the host's rule too.
        string fx = System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory();
        foreach (string path in Directory.EnumerateFiles(fx, "*.dll"))
        {
            string name = System.IO.Path.GetFileNameWithoutExtension(path);
            Version? v = FileIdentity.AssemblyVersion(path);
            if (v == null) continue;
            if (set._byName.TryGetValue(name, out var existing) && existing.Version >= v) continue;
            set._byName[name] = new Entry(name, path, v, "framework");
        }
        return set;
    }

    internal void Add(Entry e)
    {
        if (!_byName.TryGetValue(e.Name, out var existing) || existing.Version < e.Version) _byName[e.Name] = e;
    }
}
