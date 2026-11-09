// The SQL Server a Windows installation brings, optional and chosen by default, as the pack that carries it
// (sqlserver-win-x64.pack, made by `wpfrun pack --sqlserver win-x64`): Microsoft SQL Server 2025 Express's own setup
// (SQLEXPR_x64_ENU.exe), run unattended by Setup.exe -- on Windows on Arm, under Windows' x64 emulation. Linux and
// macOS pull SQL Server's container image when installing instead (SqlServerImages): it is not carried. What a Mac
// runs it with is (sqlserver-macos.pack): Apple's container (Apache 2.0), its signed installer as Apple publishes it.

using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

namespace WpfWebGpu.Packager.SqlServer;

public static class SqlServerPacks
{
    /// <summary>The targets a pack is made for.</summary>
    public static readonly string[] Targets = { "win-x64", "macos" };

    // Microsoft's SQL Server 2025 Express (Core: the engine), 17.0.1000.7, from its Download Center
    private const string ExpressUrl = "https://download.microsoft.com/download/dea8c210-c44a-4a9d-9d80-0c81578860c5/ENU/SQLEXPR_x64_ENU.exe";
    private const string ExpressSha256 = "74aa90c11202a5524e769b9bc22531baef22d91e9b2d2e8c3cb99e89a65c5297";

    // Apple's container 1.7.1, its installer signed by Apple and notarized, from its GitHub release
    private const string ContainerUrl = "https://github.com/apple/container/releases/download/1.7.1/container-1.7.1-installer-signed.pkg";
    private const string ContainerSha256 = "b6739caf3d47ca53a78f1a7edf32521420a75c7941838c3cefa75f30b80f5a97";

    /// <summary>The pack's kind for a target (its file is &lt;kind&gt;.pack).</summary>
    public static string Kind(string target) => "sqlserver-" + target;

    /// <summary>Makes sqlserver-<paramref name="target"/>.pack in <paramref name="outDir"/>; downloads go through
    /// <paramref name="cache"/>.</summary>
    public static string Make(string target, string outDir, string cache, Action<string> log)
    {
        Directory.CreateDirectory(cache);
        string pack = Path.Combine(outDir, Kind(target) + ".pack");
        var info = new Dictionary<string, object> { ["kind"] = Kind(target), ["target"] = target, ["built"] = DateTime.UtcNow.ToString("O") };
        string payloadName, payload;
        switch (target)
        {
            case "win-x64":
                payload = Path.Combine(cache, "SQLEXPR_x64_ENU-" + ExpressSha256[..12] + ".exe");
                if (!File.Exists(payload)) Download(ExpressUrl, ExpressSha256, payload, log);
                payloadName = "SQLEXPR_x64_ENU.exe";
                info["engine"] = "SQL Server 2025 Express";
                info["version"] = "17.0.1000.7";
                info["instance"] = "SQLEXPRESS";
                break;
            case "macos":
                payload = Path.Combine(cache, "container-" + ContainerSha256[..12] + ".pkg");
                if (!File.Exists(payload)) Download(ContainerUrl, ContainerSha256, payload, log);
                payloadName = "container.pkg";
                info["engine"] = SqlServerImage.X64.Engine;
                info["runtime"] = "Apple container 1.7.1";
                info["package"] = "com.apple.container-installer";
                break;
            default:
                throw new PackagerError($"no SQL Server for {target} (one of {string.Join(", ", Targets)})");
        }
        if (File.Exists(pack)) File.Delete(pack);
        using (var zip = System.IO.Compression.ZipFile.Open(pack, System.IO.Compression.ZipArchiveMode.Create))
        {
            using (var w = new StreamWriter(zip.CreateEntry("pack.json").Open()))
                w.Write(JsonSerializer.Serialize(info, new JsonSerializerOptions { WriteIndented = true }));
            // compressed already (an installer, gzip layers): stored
            zip.CreateEntryFromFile(payload, payloadName, System.IO.Compression.CompressionLevel.NoCompression);
        }
        log($"{pack}: {info["engine"]} ({new FileInfo(payload).Length / (1024 * 1024)} MB)");
        return pack;
    }

    private static void Download(string url, string sha256, string to, Action<string> log)
    {
        log($"  fetching {url}");
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(60) };
        using var response = http.Send(new HttpRequestMessage(HttpMethod.Get, url), HttpCompletionOption.ResponseHeadersRead);
        if (!response.IsSuccessStatusCode) throw new PackagerError($"{url}: {(int)response.StatusCode} {response.ReasonPhrase}");
        string part = to + ".part";
        using (var s = response.Content.ReadAsStream())
        using (var file = File.Create(part))
            s.CopyTo(file);
        string got;
        using (var file = File.OpenRead(part)) got = Convert.ToHexString(SHA256.HashData(file)).ToLowerInvariant();
        if (got != sha256) { File.Delete(part); throw new PackagerError($"{url}: SHA-256 {got}, expected {sha256}"); }
        File.Move(part, to, overwrite: true);
    }
}
