// The SQL Server an installation can bring with it, optional and chosen by default, as the packs that carry it
// (sqlserver-<target>.pack, made by `wpfrun pack --sqlserver <target>[,...]`):
//
//   sqlserver-win-x64.pack     Microsoft SQL Server 2025 Express's own setup (SQLEXPR_x64_ENU.exe), run unattended
//                              by Setup.exe -- on Windows on Arm, under Windows' x64 emulation.
//   sqlserver-linux-x64.pack   SQL Server 2025 (Express) as Microsoft publishes it for containers, an OCI image
//                              archive: run by Podman on Linux and by Apple's container on a Mac (in Rosetta).
//   sqlserver-linux-arm64.pack Azure SQL Edge, the SQL Server engine Microsoft built for Arm Linux: SQL Server itself
//                              is published for x64 only and does not run in qemu's emulation (it crashes as it
//                              starts), so an Arm Linux machine runs SQL Edge.
//
// Each pack.json says what it holds (engine, image, MSSQL_PID) for the installer that runs it.

using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

namespace WpfWebGpu.Packager.SqlServer;

public static class SqlServerPacks
{
    /// <summary>The targets a pack is made for.</summary>
    public static readonly string[] Targets = { "win-x64", "linux-x64", "linux-arm64" };

    // Microsoft's SQL Server 2025 Express (Core: the engine), 17.0.1000.7, from its Download Center
    private const string ExpressUrl = "https://download.microsoft.com/download/dea8c210-c44a-4a9d-9d80-0c81578860c5/ENU/SQLEXPR_x64_ENU.exe";
    private const string ExpressSha256 = "74aa90c11202a5524e769b9bc22531baef22d91e9b2d2e8c3cb99e89a65c5297";

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
            case "linux-x64":
                payload = Path.Combine(cache, "mssql-server-2025-amd64.tar");
                ContainerImage.Pull("mcr.microsoft.com", "mssql/server", "2025-latest", "amd64", "mcr.microsoft.com/mssql/server:2025-latest", payload, log);
                payloadName = "image.tar";
                info["engine"] = "SQL Server 2025 Express";
                info["image"] = "mcr.microsoft.com/mssql/server:2025-latest";
                info["pid"] = "Express";
                info["eula"] = "Y";
                info["architecture"] = "amd64";
                break;
            case "linux-arm64":
                payload = Path.Combine(cache, "azure-sql-edge-arm64.tar");
                ContainerImage.Pull("mcr.microsoft.com", "azure-sql-edge", "latest", "arm64", "mcr.microsoft.com/azure-sql-edge:latest", payload, log);
                payloadName = "image.tar";
                info["engine"] = "Azure SQL Edge";
                info["image"] = "mcr.microsoft.com/azure-sql-edge:latest";
                info["pid"] = "Developer";
                info["eula"] = "1";
                info["architecture"] = "arm64";
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
