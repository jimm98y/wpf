// A container image as an OCI image archive (the oci-archive format: oci-layout, index.json and blobs/sha256/), taken
// from its registry in managed code -- what an installer loads into the machine's container runtime (Podman's
// `podman load`, Apple's `container image load`) with no registry to reach at install time and no Docker on the
// machine that builds the installer.
//
// Only anonymous pulls (Microsoft's registry, mcr.microsoft.com, needs no sign-in); the manifest of one platform is
// chosen from an image index, and its layers stay compressed as the registry has them.

using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace WpfWebGpu.Packager.SqlServer;

public static class ContainerImage
{
    private static readonly string[] s_accept =
    {
        "application/vnd.oci.image.index.v1+json",
        "application/vnd.docker.distribution.manifest.list.v2+json",
        "application/vnd.oci.image.manifest.v1+json",
        "application/vnd.docker.distribution.manifest.v2+json",
    };

    /// <summary>Writes <paramref name="registry"/>/<paramref name="repository"/>:<paramref name="tag"/> for
    /// linux/<paramref name="architecture"/> to <paramref name="archive"/>, its index naming it
    /// <paramref name="refName"/> (what it is loaded as).</summary>
    public static void Pull(string registry, string repository, string tag, string architecture, string refName, string archive, Action<string> log)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
        string baseUrl = $"https://{registry}/v2/{repository}";

        // the manifest: the platform's, where the tag names an index
        (byte[] manifest, string mediaType) = Get(http, $"{baseUrl}/manifests/{tag}", s_accept);
        var node = JsonNode.Parse(manifest)!;
        if (node["manifests"] is JsonArray platforms)
        {
            JsonNode chosen = platforms.FirstOrDefault(m => (string?)m?["platform"]?["os"] == "linux" && (string?)m?["platform"]?["architecture"] == architecture)
                ?? throw new PackagerError($"{registry}/{repository}:{tag} has no linux/{architecture} image");
            (manifest, mediaType) = Get(http, $"{baseUrl}/manifests/{(string)chosen["digest"]!}", new[] { (string)chosen["mediaType"]! });
            node = JsonNode.Parse(manifest)!;
        }
        else
        {
            // one platform only: it has to be the one asked for
            (byte[] config0, _) = Get(http, $"{baseUrl}/blobs/{(string)node["config"]!["digest"]!}", new[] { "*/*" });
            string arch = (string?)JsonNode.Parse(config0)!["architecture"] ?? "";
            if (arch != architecture) throw new PackagerError($"{registry}/{repository}:{tag} is for {arch}, not {architecture}");
        }

        string work = archive + ".parts";
        if (Directory.Exists(work)) Directory.Delete(work, recursive: true);
        string blobs = Path.Combine(work, "blobs", "sha256");
        Directory.CreateDirectory(blobs);
        string manifestDigest = Write(blobs, manifest);
        var config = node["config"]!;
        Fetch(http, baseUrl, (string)config["digest"]!, (long)config["size"]!, blobs, log);
        long total = 0;
        foreach (var layer in node["layers"]!.AsArray())
        {
            Fetch(http, baseUrl, (string)layer!["digest"]!, (long)layer["size"]!, blobs, log);
            total += (long)layer["size"]!;
        }
        File.WriteAllText(Path.Combine(work, "oci-layout"), "{\"imageLayoutVersion\":\"1.0.0\"}");
        var index = new JsonObject
        {
            ["schemaVersion"] = 2,
            ["mediaType"] = "application/vnd.oci.image.index.v1+json",
            ["manifests"] = new JsonArray(new JsonObject
            {
                ["mediaType"] = mediaType,
                ["digest"] = manifestDigest,
                ["size"] = manifest.Length,
                ["platform"] = new JsonObject { ["os"] = "linux", ["architecture"] = architecture },
                ["annotations"] = new JsonObject { ["org.opencontainers.image.ref.name"] = refName },
            }),
        };
        File.WriteAllText(Path.Combine(work, "index.json"), index.ToJsonString());

        // the archive: a tar of the layout (its blobs are compressed already)
        using (var output = File.Create(archive))
            System.Formats.Tar.TarFile.CreateFromDirectory(work, output, includeBaseDirectory: false);
        Directory.Delete(work, recursive: true);
        log($"  {registry}/{repository}:{tag} (linux/{architecture}): {total / (1024 * 1024)} MB in {node["layers"]!.AsArray().Count} layers");
    }

    private static (byte[] Body, string MediaType) Get(HttpClient http, string url, string[] accept)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        foreach (string a in accept) request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(a));
        using var response = http.Send(request);
        if (!response.IsSuccessStatusCode) throw new PackagerError($"{url}: {(int)response.StatusCode} {response.ReasonPhrase}");
        using var s = response.Content.ReadAsStream();
        using var m = new MemoryStream();
        s.CopyTo(m);
        return (m.ToArray(), response.Content.Headers.ContentType?.MediaType ?? accept[0]);
    }

    private static string Write(string blobs, byte[] content)
    {
        string digest = "sha256:" + Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();
        File.WriteAllBytes(Path.Combine(blobs, digest[7..]), content);
        return digest;
    }

    /// <summary>A blob into the layout, checked against its digest.</summary>
    private static void Fetch(HttpClient http, string baseUrl, string digest, long size, string blobs, Action<string> log)
    {
        string to = Path.Combine(blobs, digest["sha256:".Length..]);
        if (File.Exists(to) && new FileInfo(to).Length == size) return;
        using var response = http.Send(new HttpRequestMessage(HttpMethod.Get, $"{baseUrl}/blobs/{digest}"), HttpCompletionOption.ResponseHeadersRead);
        if (!response.IsSuccessStatusCode) throw new PackagerError($"{digest}: {(int)response.StatusCode} {response.ReasonPhrase}");
        using (var s = response.Content.ReadAsStream())
        using (var file = File.Create(to))
        using (var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
        {
            var buffer = new byte[1 << 20];
            for (int n; (n = s.Read(buffer)) > 0;)
            {
                file.Write(buffer, 0, n);
                hash.AppendData(buffer, 0, n);
            }
            string got = "sha256:" + Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
            if (got != digest) throw new PackagerError($"{digest}: the registry sent {got}");
        }
        if (size > 50 * 1024 * 1024) log($"    {digest[..19]}: {size / (1024 * 1024)} MB");
    }
}
