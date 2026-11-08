// The installation's apps as pages for the browser, served by the desktop installations' web server: each app
// marked "web" as the browser template's page (browser.pack) with the app in it -- its folder's assemblies prepared
// for the stack (as a phone's app takes them) and its data, mounted at the page's file system root when the page
// starts (payload/, listed in payload/index.json: each file's path, and the numbered blob it is served as -- IIS
// serves only the types it knows and refuses some names, *.config and bin/ among them), its name in /wpfrun-app.txt; the installation's registry under
// /registry and its ProgramData under /state, as the browser head keeps the machine's state.
//
// Each page has a web.config, which IIS reads on Windows and the web server here as IIS does: the headers that make
// the page cross-origin isolated (the runtime's threads are Web Workers sharing its memory, which a browser allows
// only then), and the content types of the runtime's files that IIS does not know.

namespace WpfWebGpu.Packager.Desktop;

public static class WebPages
{
    /// <summary>A page: the app, where it is served (its URL path) and the folder it is in (under web/).</summary>
    public sealed record Page(AppEntry App, string UrlPath, string Folder, string Built);

    /// <summary>The URL path an app's page is served at: its webPath, else /webassembly/&lt;its name, lower case&gt;.</summary>
    public static string UrlPath(PackageManifest manifest, AppEntry app)
    {
        string path = app.WebPath.Trim();
        if (path.Length == 0) path = "/webassembly/" + AppName(manifest, app).ToLowerInvariant();
        path = "/" + path.Trim('/');
        if (path.Length == 1 || path.Contains("..", StringComparison.Ordinal) || path.Any(c => char.IsWhiteSpace(c) || c is '?' or '#' or '\\'))
            throw new PackagerError($"{app.Exe}: \"{app.WebPath}\" is not a path a page can be served at");
        return path;
    }

    // the app's assembly name (App for App.exe, and for App_ARM64.exe that starts App.dll)
    private static string AppName(PackageManifest manifest, AppEntry app)
    {
        string exe = Path.Combine(manifest.Inputs.InstallDir, app.Exe);
        return Path.GetFileNameWithoutExtension(AppHost.AppDll(exe) ?? exe);
    }

    /// <summary>The pages of the apps marked "web", made in <paramref name="workDir"/>/web/&lt;folder&gt;.</summary>
    public static List<Page> Build(PackageManifest manifest, StagedState staged, string packsFolder, string workDir, Action<string> log)
    {
        var pages = new List<Page>();
        var web = manifest.Apps.Where(a => a.Web).ToList();
        if (web.Count == 0) return pages;
        var pack = PackFile.Open(packsFolder, "browser");
        string refs = Path.Combine(pack.Directory, "refs"), site = Path.Combine(pack.Directory, "site");
        var provided = new HashSet<string>(Directory.EnumerateFiles(refs, "*.dll").Select(Path.GetFileNameWithoutExtension).OfType<string>(), StringComparer.OrdinalIgnoreCase);
        var paths = new Dictionary<string, AppEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in web)
        {
            string url = UrlPath(manifest, entry);
            if (!paths.TryAdd(url, entry)) throw new PackagerError($"{entry.Exe} and {paths[url].Exe} are both served at {url}");
            string folder = url.Trim('/').Replace('/', '-');
            string built = Path.Combine(workDir, "web", folder);
            if (Directory.Exists(built)) Directory.Delete(built, recursive: true);
            var app = MobileApp.Prepare(manifest.Inputs.InstallDir, entry, provided, new[] { refs }, Path.Combine(workDir, "web-work", folder), log);

            foreach (string f in Directory.EnumerateFiles(site, "*", SearchOption.AllDirectories))
                Copy(f, Path.Combine(built, Path.GetRelativePath(site, f)));
            var payload = new List<Dictionary<string, string>>();
            string Blob(string target)
            {
                string file = $"{payload.Count:D6}.bin";
                payload.Add(new() { ["path"] = target.Replace('\\', '/'), ["file"] = file });
                return Path.Combine(built, "payload", file);
            }
            void Add(string source, string target) => Copy(source, Blob(target));
            void AddText(string text, string target)
            {
                string to = Blob(target);
                Directory.CreateDirectory(Path.GetDirectoryName(to)!);
                File.WriteAllText(to, text);
            }
            foreach (var (source, target) in app.Files) Add(source, target);
            // the installation's paths, as the page has them: the app's folder is its root
            foreach (string hive in Directory.EnumerateFiles(staged.RegistryDir, "*.reg"))
                AddText(app.MapRegistry(File.ReadAllText(hive))
                    .Replace(PathTokens.AppDir, "", StringComparison.Ordinal)
                    .Replace(PathTokens.ProgramData, "/state/ProgramData", StringComparison.Ordinal)
                    .Replace(PathTokens.InstallDir, "/install", StringComparison.Ordinal), "registry/" + Path.GetFileName(hive));
            var excludes = new GlobSet(manifest.MobileExclude);
            // not what the machine recorded as it ran (its historians' data): a page holds what it mounts in memory
            foreach (var (source, target) in staged.ProgramDataFiles)
                if (!excludes.Matches(target) && !target.Contains("/DataHistorian/", StringComparison.OrdinalIgnoreCase))
                    Add(source, "state/ProgramData/" + target);
            AddText($"name={app.Name}\nexe={app.Exe}\nosidentity={manifest.Product.OsIdentity}\n", "wpfrun-app.txt");
            File.WriteAllText(Path.Combine(built, "payload", "index.json"), System.Text.Json.JsonSerializer.Serialize(payload));
            File.WriteAllText(Path.Combine(built, "web.config"), WebConfig);
            log($"  web page: {entry.Exe} at {url} ({app.Files.Count} files of its folder{(app.LeftOut.Count > 0 ? "; left out: " + string.Join(", ", app.LeftOut.Take(6)) : "")})");
            pages.Add(new Page(entry, url, folder, built));
        }
        return pages;
    }

    /// <summary>A page's web.config: cross-origin isolated, and the runtime's files' content types.</summary>
    public const string WebConfig = """
        <?xml version="1.0" encoding="utf-8"?>
        <configuration>
          <system.webServer>
            <httpProtocol>
              <customHeaders>
                <add name="Cross-Origin-Opener-Policy" value="same-origin" />
                <add name="Cross-Origin-Embedder-Policy" value="require-corp" />
                <add name="Cross-Origin-Resource-Policy" value="same-origin" />
              </customHeaders>
            </httpProtocol>
            <staticContent>
              <remove fileExtension=".wasm" />
              <mimeMap fileExtension=".wasm" mimeType="application/wasm" />
              <remove fileExtension=".dll" />
              <mimeMap fileExtension=".dll" mimeType="application/octet-stream" />
              <remove fileExtension=".pdb" />
              <mimeMap fileExtension=".pdb" mimeType="application/octet-stream" />
              <remove fileExtension=".dat" />
              <mimeMap fileExtension=".dat" mimeType="application/octet-stream" />
              <remove fileExtension=".mjs" />
              <mimeMap fileExtension=".mjs" mimeType="text/javascript" />
              <remove fileExtension=".json" />
              <mimeMap fileExtension=".json" mimeType="application/json" />
            </staticContent>
          </system.webServer>
        </configuration>

        """;

    private static void Copy(string from, string to)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(to)!);
        File.Copy(from, to, overwrite: true);
    }
}
