// Builds the deps.json an unmodified app runs with on the WebGPU stack: the app's own, with the
// overlay's assemblies taking the place of the ones Microsoft.WindowsDesktop.App would have provided.
//
// WHY EVERY LIBRARY IS REWRITTEN. The generated file lives in wpfrun's cache, not beside the app, and
// the host probes "app-local" assets in the directory of the deps.json it was given, not the
// directory of the app. So every app library becomes a "package" whose path is the app folder's name,
// and the folder's parent is passed as an additional probing path; the overlay is the same, under its
// own folder. Each asset then resolves to exactly one file, wherever the same file NAME also occurs:
// the app may ship its own Microsoft.Web.WebView2.Wpf.dll, the overlay's still wins, because it is
// the only one the deps.json names.
//
// WHO WINS A NAME BOTH SIDES PROVIDE.
//   - The overlay, for everything that is the stack: the fork's WPF, its WinForms and System.Drawing,
//     WebView2, and the shims. These are referenced by the payload as files or projects, or are
//     package assets the SDK overwrote with a shim (detected by the file on disk not being what the
//     package entry says it is).
//   - The higher version, for an ordinary package both sides carry (System.CodeDom, say). The app
//     may well be ahead of the overlay, and then its copy is the one its code was built against.

using System.Text.Json;
using System.Text.Json.Nodes;

namespace WpfWebGpu;

internal sealed class DepsMerger
{
    /// <summary>The synthetic library that carries the overlay's assets in the merged file.</summary>
    public const string OverlayLibrary = "WpfWebGpu.Overlay/1.0.0";

    private readonly IReadOnlyList<string> _ridChain;
    private readonly List<string> _notes = new();
    private readonly PackageFetcher? _fetcher;
    private JsonObject _appLibraries = new();
    private readonly Dictionary<string, JsonObject> _fetched = new(StringComparer.OrdinalIgnoreCase);

    public DepsMerger(string rid, PackageFetcher? fetcher = null)
    {
        _ridChain = HostInfo.RidChain(rid);
        _fetcher = fetcher;
    }

    public IReadOnlyList<string> Notes => _notes;

    /// <summary>Names (simple assembly names) the overlay provides and the app's copies were dropped for.</summary>
    public SortedSet<string> Replaced { get; } = new(StringComparer.OrdinalIgnoreCase);

    public JsonObject Merge(AppFiles app, JsonObject? appDeps, string payloadDir, JsonObject payloadDeps)
    {
        var overlay = ReadOverlay(payloadDir, payloadDeps);

        appDeps ??= SynthesizeAppDeps(app);
        string targetName = appDeps["runtimeTarget"]?["name"]?.GetValue<string>() ?? ".NETCoreApp,Version=v10.0";
        JsonObject appTarget = appDeps["targets"]?[targetName] as JsonObject ?? new JsonObject();
        _appLibraries = appDeps["libraries"] as JsonObject ?? new JsonObject();

        var outTarget = new JsonObject();
        var outLibraries = new JsonObject();
        string appDirName = Path.GetFileName(app.Directory);

        foreach (var (libKey, libNode) in appTarget)
        {
            if (libNode is not JsonObject lib) continue;
            var outLib = new JsonObject();
            if (lib["dependencies"] is JsonObject deps) outLib["dependencies"] = deps.DeepClone();

            CopyAssets(app.Directory, libKey, lib, "runtime", outLib, overlay, isRuntime: true);
            CopyAssets(app.Directory, libKey, lib, "native", outLib, overlay, isRuntime: false);
            CopyResources(app.Directory, libKey, lib, outLib);
            CopyRuntimeTargets(app.Directory, libKey, lib, outLib, overlay);

            outTarget[libKey] = outLib;
            outLibraries[libKey] = new JsonObject
            {
                ["type"] = "package",
                ["serviceable"] = false,
                ["sha512"] = "",
                ["path"] = appDirName,
            };
        }

        // Assets fetched because the app listed but did not ship them, each under its package's folder.
        foreach (var (libKey, runtimeTargets) in _fetched)
        {
            string fetchedKey = "WpfWebGpu.Fetched." + libKey;
            outTarget[fetchedKey] = new JsonObject { ["runtimeTargets"] = runtimeTargets };
            int slash = libKey.IndexOf('/');
            outLibraries[fetchedKey] = new JsonObject
            {
                ["type"] = "package",
                ["serviceable"] = false,
                ["sha512"] = "",
                ["path"] = PackageFetcher.LibraryPath(libKey[..slash], libKey[(slash + 1)..]),
            };
        }

        // The overlay, as one library under its own folder.
        var overlayLib = new JsonObject();
        var runtime = new JsonObject();
        foreach (var asset in overlay.Assemblies.Values.Where(a => !a.Dropped).OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase))
        {
            runtime[asset.RelativePath] = VersionNode(asset.AssemblyVersion, asset.FileVersion);
        }
        overlayLib["runtime"] = runtime;
        var native = new JsonObject();
        foreach (var n in overlay.Natives.Values.OrderBy(n => n.FileName, StringComparer.OrdinalIgnoreCase))
        {
            native[n.RelativePath] = new JsonObject { ["fileVersion"] = "0.0.0.0" };
        }
        if (native.Count > 0) overlayLib["native"] = native;
        if (overlay.Resources.Count > 0)
        {
            var res = new JsonObject();
            foreach (var (path, locale) in overlay.Resources) res[path] = new JsonObject { ["locale"] = locale };
            overlayLib["resources"] = res;
        }
        outTarget[OverlayLibrary] = overlayLib;
        outLibraries[OverlayLibrary] = new JsonObject
        {
            ["type"] = "package",
            ["serviceable"] = false,
            ["sha512"] = "",
            ["path"] = Path.GetFileName(Path.TrimEndingDirectorySeparator(payloadDir)),
        };

        return new JsonObject
        {
            ["runtimeTarget"] = new JsonObject { ["name"] = targetName, ["signature"] = "" },
            ["compilationOptions"] = new JsonObject(),
            ["targets"] = new JsonObject { [targetName] = outTarget },
            ["libraries"] = outLibraries,
        };
    }

    // ------------------------------------------------------------------ app side

    private void CopyAssets(string appDir, string libKey, JsonObject lib, string kind, JsonObject outLib, Overlay overlay, bool isRuntime)
    {
        if (lib[kind] is not JsonObject assets) return;
        var outAssets = new JsonObject();
        foreach (var (key, props) in assets)
        {
            if (isRuntime ? !KeepAppAssembly(appDir, libKey, key, props as JsonObject, overlay) : !KeepAppNative(libKey, key, overlay))
            {
                continue;
            }
            string? located = Locate(appDir, key, locale: null);
            if (located == null)
            {
                _notes.Add($"missing  {libKey}: {key} is listed but not in the app folder; left out");
                continue;
            }
            outAssets[located] = props?.DeepClone() ?? new JsonObject();
        }
        if (outAssets.Count > 0) outLib[kind] = outAssets;
    }

    private void CopyResources(string appDir, string libKey, JsonObject lib, JsonObject outLib)
    {
        if (lib["resources"] is not JsonObject assets) return;
        var outAssets = new JsonObject();
        foreach (var (key, props) in assets)
        {
            string? locale = props?["locale"]?.GetValue<string>();
            string? located = Locate(appDir, key, locale);
            if (located == null) continue;             // a missing satellite only costs a translation
            outAssets[located] = props?.DeepClone() ?? new JsonObject();
        }
        if (outAssets.Count > 0) outLib["resources"] = outAssets;
    }

    private void CopyRuntimeTargets(string appDir, string libKey, JsonObject lib, JsonObject outLib, Overlay overlay)
    {
        if (lib["runtimeTargets"] is not JsonObject assets) return;
        var outAssets = new JsonObject();
        foreach (var (key, propsNode) in assets)
        {
            var props = propsNode as JsonObject;
            string rid = props?["rid"]?.GetValue<string>() ?? "";
            string type = props?["assetType"]?.GetValue<string>() ?? "runtime";
            if (!_ridChain.Contains(rid, StringComparer.OrdinalIgnoreCase))
            {
                continue;                               // another platform's asset; the host would ignore it
            }
            if (type == "runtime" && !KeepAppAssembly(appDir, libKey, key, props, overlay)) continue;
            if (type == "native" && !KeepAppNative(libKey, key, overlay)) continue;
            if (!File.Exists(Path.Combine(appDir, key)))
            {
                if (TryFetch(libKey, key, props))
                {
                    continue;
                }
                _notes.Add($"missing  {libKey}: {key} ({rid}) is listed but not in the app folder; left out");
                continue;
            }
            outAssets[key] = props?.DeepClone() ?? new JsonObject();
        }
        if (outAssets.Count > 0) outLib["runtimeTargets"] = outAssets;
    }

    /// <summary>
    /// An asset of a NuGet package the app lists for this platform but did not deploy: take it from
    /// that exact package version instead. Only package libraries qualify; a project's assets exist
    /// nowhere but in the app's own build.
    /// </summary>
    private bool TryFetch(string libKey, string key, JsonObject? props)
    {
        if (_fetcher == null) return false;
        if (_appLibraries[libKey]?["type"]?.GetValue<string>() != "package") return false;
        int slash = libKey.IndexOf('/');
        if (slash <= 0) return false;
        string? dir = _fetcher.Ensure(libKey[..slash], libKey[(slash + 1)..], out string? error);
        if (dir == null)
        {
            _notes.Add($"missing  {libKey}: {key} could not be fetched ({error})");
            return false;
        }
        if (!File.Exists(Path.Combine(dir, key)))
        {
            _notes.Add($"missing  {libKey}: {key} is not in the package either");
            return false;
        }
        if (!_fetched.TryGetValue(libKey, out var assets)) _fetched[libKey] = assets = new JsonObject();
        assets[key] = props?.DeepClone() ?? new JsonObject();
        _notes.Add($"fetched  {libKey}: {key}");
        return true;
    }

    private bool KeepAppAssembly(string appDir, string libKey, string key, JsonObject? props, Overlay overlay)
    {
        string name = Path.GetFileNameWithoutExtension(key);
        if (!overlay.Assemblies.TryGetValue(name, out var ours)) return true;

        if (ours.StackOwned)
        {
            Replaced.Add(name);
            return false;
        }

        // An ordinary package both carry: the newer one.
        string? located = Locate(appDir, key, null);
        Version appAsm = ParseVersion(props?["assemblyVersion"]) ?? (located != null ? FileIdentity.AssemblyVersion(Path.Combine(appDir, located)) : null) ?? new Version(0, 0);
        Version appFile = ParseVersion(props?["fileVersion"]) ?? (located != null ? FileIdentity.FileVersion(Path.Combine(appDir, located)) : null) ?? new Version(0, 0);
        int cmp = appAsm.CompareTo(ours.AssemblyVersion);
        if (cmp == 0) cmp = appFile.CompareTo(ours.FileVersion);
        if (cmp > 0)
        {
            ours.Dropped = true;
            _notes.Add($"app-wins {name}: the app's {appAsm}/{appFile} is newer than the overlay's {ours.AssemblyVersion}/{ours.FileVersion}");
            return true;
        }
        Replaced.Add(name);
        return false;
    }

    private bool KeepAppNative(string libKey, string key, Overlay overlay)
    {
        string file = Path.GetFileName(key);
        if (!overlay.Natives.ContainsKey(file)) return true;
        _notes.Add($"native   {libKey}: {file} replaced by the overlay's");
        return false;
    }

    /// <summary>
    /// Where an asset actually is, relative to <paramref name="baseDir"/>. A published or built app is
    /// flat (lib/net10.0/X.dll sits at X.dll, satellites at de/X.resources.dll) while RID-specific
    /// assets keep their runtimes/... path, which is exactly how the host itself looks them up.
    /// </summary>
    internal static string? Locate(string baseDir, string key, string? locale)
    {
        string file = Path.GetFileName(key);
        foreach (string candidate in locale == null
                     ? new[] { key, file }
                     : new[] { key, $"{locale}/{file}", file })
        {
            if (File.Exists(Path.Combine(baseDir, candidate))) return candidate.Replace('\\', '/');
        }
        return null;
    }

    // ------------------------------------------------------------------ overlay side

    internal sealed class OverlayAssembly
    {
        public required string Name { get; init; }
        public required string RelativePath { get; init; }
        public required Version AssemblyVersion { get; init; }
        public required Version FileVersion { get; init; }
        public required bool StackOwned { get; init; }
        public bool Dropped { get; set; }
    }

    internal sealed record OverlayNative(string FileName, string RelativePath);

    internal sealed class Overlay
    {
        public Dictionary<string, OverlayAssembly> Assemblies { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, OverlayNative> Natives { get; } = new(StringComparer.OrdinalIgnoreCase);
        public List<(string Path, string Locale)> Resources { get; } = new();
    }

    internal Overlay ReadOverlay(string payloadDir, JsonObject payloadDeps)
    {
        var overlay = new Overlay();
        string targetName = payloadDeps["runtimeTarget"]?["name"]?.GetValue<string>() ?? "";
        var target = payloadDeps["targets"]?[targetName] as JsonObject ?? new JsonObject();
        var libraries = payloadDeps["libraries"] as JsonObject ?? new JsonObject();

        foreach (var (libKey, libNode) in target)
        {
            if (libNode is not JsonObject lib) continue;
            if (libKey.StartsWith(Locations.PayloadName + "/", StringComparison.OrdinalIgnoreCase)) continue;
            string type = libraries[libKey]?["type"]?.GetValue<string>() ?? "package";

            var runtimeAssets = new List<(string Key, JsonObject? Props)>();
            if (lib["runtime"] is JsonObject rt) runtimeAssets.AddRange(rt.Select(kv => (kv.Key, kv.Value as JsonObject)));
            if (lib["runtimeTargets"] is JsonObject rts)
            {
                runtimeAssets.AddRange(rts.Where(kv => kv.Value?["assetType"]?.GetValue<string>() == "runtime"
                                                       && _ridChain.Contains(kv.Value?["rid"]?.GetValue<string>() ?? "", StringComparer.OrdinalIgnoreCase))
                                          .Select(kv => (kv.Key, kv.Value as JsonObject)));
            }

            foreach (var (key, props) in runtimeAssets)
            {
                string? located = Locate(payloadDir, key, null);
                if (located == null) continue;
                string full = Path.Combine(payloadDir, located);
                Version asmVer = FileIdentity.AssemblyVersion(full) ?? ParseVersion(props?["assemblyVersion"]) ?? new Version(0, 0);
                Version fileVer = FileIdentity.FileVersion(full) ?? ParseVersion(props?["fileVersion"]) ?? new Version(0, 0);
                Version? declared = ParseVersion(props?["fileVersion"]);
                bool overwritten = type == "package" && declared != null && declared != fileVer;
                overlay.Assemblies[Path.GetFileNameWithoutExtension(located)] = new OverlayAssembly
                {
                    Name = Path.GetFileNameWithoutExtension(located),
                    RelativePath = located,
                    AssemblyVersion = asmVer,
                    FileVersion = fileVer,
                    StackOwned = type != "package" || overwritten,
                };
            }

            if (lib["native"] is JsonObject natives)
            {
                foreach (var (key, _) in natives)
                {
                    string? located = Locate(payloadDir, key, null);
                    if (located != null) overlay.Natives[Path.GetFileName(located)] = new OverlayNative(Path.GetFileName(located), located);
                }
            }

            if (lib["resources"] is JsonObject resources)
            {
                foreach (var (key, props) in resources)
                {
                    string? locale = props?["locale"]?.GetValue<string>();
                    string? located = Locate(payloadDir, key, locale);
                    if (located != null && locale != null) overlay.Resources.Add((located, locale));
                }
            }
        }

        // The SDK deploys the GPU backend as plain files beside the app (it is not a package asset), so
        // the payload's deps.json does not name it. Without a native entry here the host would not put
        // the overlay folder on the native search path, and wgpu-native would not load.
        foreach (string file in Directory.EnumerateFiles(payloadDir))
        {
            string name = Path.GetFileName(file);
            if (overlay.Natives.ContainsKey(name) || !FileIdentity.IsNativeLibraryName(name)) continue;
            if (name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) && FileIdentity.IsManaged(file)) continue;
            overlay.Natives[name] = new OverlayNative(name, name);
        }

        return overlay;
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>An app without a deps.json: every managed assembly in its folder, as the host would take.</summary>
    private JsonObject SynthesizeAppDeps(AppFiles app)
    {
        _notes.Add($"no-deps  {app.Name} has no deps.json; using every managed assembly in its folder");
        var runtime = new JsonObject();
        foreach (string file in Directory.EnumerateFiles(app.Directory, "*.dll").OrderBy(f => f, StringComparer.Ordinal))
        {
            if (FileIdentity.IsManaged(file)) runtime[Path.GetFileName(file)] = new JsonObject();
        }
        string key = $"{app.Name}/1.0.0";
        return new JsonObject
        {
            ["runtimeTarget"] = new JsonObject { ["name"] = ".NETCoreApp,Version=v10.0" },
            ["targets"] = new JsonObject { [".NETCoreApp,Version=v10.0"] = new JsonObject { [key] = new JsonObject { ["runtime"] = runtime } } },
            ["libraries"] = new JsonObject { [key] = new JsonObject { ["type"] = "project" } },
        };
    }

    private static JsonObject VersionNode(Version asm, Version file) => new()
    {
        ["assemblyVersion"] = asm.ToString(),
        ["fileVersion"] = file.ToString(),
    };

    private static Version? ParseVersion(JsonNode? node) =>
        node is JsonValue v && v.TryGetValue(out string? s) && Version.TryParse(s, out var ver) ? ver : null;

    public static JsonObject ReadJson(string path) =>
        JsonNode.Parse(File.ReadAllText(path), documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }) as JsonObject
        ?? throw new UserError($"{path} is not a JSON object.");
}
