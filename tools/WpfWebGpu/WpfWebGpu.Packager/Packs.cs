// The Packager's templates (packs/<kind>.pack, made by `wpfrun pack`): a zip of pack.json and the template's
// files, opened into the Packager's cache once per pack file.

using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

namespace WpfWebGpu.Packager;

public sealed class PackFile
{
    public required string Kind { get; init; }
    /// <summary>Where the pack's files are, unpacked.</summary>
    public required string Directory { get; init; }
    public required IReadOnlyDictionary<string, string> Info { get; init; }

    public string this[string key] => Info.TryGetValue(key, out var v) ? v : throw new PackagerError($"{Kind}.pack has no '{key}'");

    /// <summary>The packs folder: beside the Packager, or WPFPKG_PACKS.</summary>
    public static string DefaultFolder =>
        Environment.GetEnvironmentVariable("WPFPKG_PACKS") is { Length: > 0 } env ? env : Path.Combine(AppContext.BaseDirectory, "packs");

    public static PackFile Open(string packsFolder, string kind)
    {
        string file = Path.Combine(packsFolder, kind + ".pack");
        if (!File.Exists(file)) throw new PackagerError($"no {kind}.pack in {packsFolder}: the template for this target is not installed");
        var fi = new FileInfo(file);
        string stamp = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes($"{fi.FullName}|{fi.Length}|{fi.LastWriteTimeUtc.Ticks}")))[..16];
        string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WpfWebGpu", "packager", "packs", kind + "-" + stamp);
        if (!File.Exists(Path.Combine(dir, ".complete")))
        {
            if (System.IO.Directory.Exists(dir)) System.IO.Directory.Delete(dir, recursive: true);
            ZipFile.ExtractToDirectory(file, dir);
            File.WriteAllText(Path.Combine(dir, ".complete"), "");
        }
        var info = new Dictionary<string, string>(StringComparer.Ordinal);
        using (var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, "pack.json"))))
            foreach (var p in doc.RootElement.EnumerateObject()) info[p.Name] = p.Value.ToString();
        return new PackFile { Kind = kind, Directory = dir, Info = info };
    }
}
