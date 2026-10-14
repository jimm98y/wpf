// The assembly names the app can bind to when it runs the stock way: its own .exe, its own folder and
// the shared frameworks its own runtimeconfig.json asks for (Microsoft.WindowsDesktop.App included).
// A reference that does not resolve there either is not something the WebGPU stack is missing: the app
// ships without it, and stock reaches the same FileNotFoundException on that code path. The audit
// lists those apart from the real gaps.

using System.Text.Json.Nodes;

namespace WpfMac.Audit;

internal sealed class StockSet
{
    private readonly HashSet<string> _names = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>False when a framework the app asks for is not installed here (WindowsDesktop.App off
    /// Windows): then nothing can be said about what stock would find, and every miss stays a gap.</summary>
    public bool Known { get; private set; } = true;

    public List<string> Frameworks { get; } = new();

    public bool Contains(string assemblyName) => _names.Contains(assemblyName);

    public static StockSet Build(AppFiles app)
    {
        var set = new StockSet();
        foreach (string f in Directory.EnumerateFiles(app.Directory, "*.dll", SearchOption.AllDirectories)
                     .Concat(Directory.EnumerateFiles(app.Directory, "*.exe", SearchOption.AllDirectories)))
        {
            set._names.Add(Path.GetFileNameWithoutExtension(f));
        }

        string? shared = SharedRoot();
        JsonNode? config = JsonNode.Parse(File.ReadAllText(app.RuntimeConfig));
        var options = config?["runtimeOptions"];
        var frameworks = options?["frameworks"] as JsonArray ?? (options?["framework"] is JsonObject one ? new JsonArray(one.DeepClone()) : new JsonArray());
        foreach (var node in frameworks)
        {
            string? name = node?["name"]?.GetValue<string>();
            string? version = node?["version"]?.GetValue<string>();
            if (name == null || version == null) continue;
            string? dir = shared == null ? null : Pick(Path.Combine(shared, name), version);
            if (dir == null)
            {
                set.Known = false;
                set.Frameworks.Add($"{name} {version} (not installed here)");
                continue;
            }
            set.Frameworks.Add($"{name} {Path.GetFileName(dir)}");
            foreach (string f in Directory.EnumerateFiles(dir, "*.dll")) set._names.Add(Path.GetFileNameWithoutExtension(f));
        }
        return set;
    }

    /// <summary>The host's default roll-forward (Minor): the highest installed version with the same
    /// major that is not lower than the one asked for.</summary>
    private static string? Pick(string frameworkDir, string requested)
    {
        if (!Directory.Exists(frameworkDir) || !Version.TryParse(Strip(requested), out Version? want)) return null;
        return Directory.EnumerateDirectories(frameworkDir)
            .Select(d => (Dir: d, V: Version.TryParse(Strip(Path.GetFileName(d)), out Version? v) ? v : null))
            .Where(x => x.V != null && x.V.Major == want.Major && x.V >= want)
            .OrderByDescending(x => x.V)
            .Select(x => x.Dir)
            .FirstOrDefault();
    }

    private static string Strip(string v) => v.Split('-', '+')[0];

    /// <summary>&lt;dotnet root&gt;/shared, from the runtime running wpfmac (shared/Microsoft.NETCore.App/&lt;version&gt;/).</summary>
    private static string? SharedRoot()
    {
        string runtimeDir = Path.TrimEndingDirectorySeparator(System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory());
        string? shared = Path.GetDirectoryName(Path.GetDirectoryName(runtimeDir));
        return shared != null && Directory.Exists(shared) ? shared : null;
    }
}
