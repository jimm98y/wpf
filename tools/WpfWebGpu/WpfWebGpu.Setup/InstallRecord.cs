// What an installation put on the machine besides its own folder (ProgramData\WpfWebGpu\installations\<id>.json: it
// outlives an uninstall that keeps the data, so that one after a reinstall still knows it), so that uninstall /all can
// take it all away again: the ProgramData files it wrote and the folders it made, the registry values it set and the
// keys it made (none that were there before it: an upgrade's record grows, never claims what the machine had), the
// access rules it added, the SQL Server instance it installed, the user whose HKEY_CURRENT_USER it wrote to.

using System.Text.Json;
using System.Text.Json.Serialization;

namespace WpfWebGpu.Setup;

public sealed class InstallRecord
{
    public List<string> ProgramDataFiles { get; set; } = new();
    public List<string> ProgramDataFolders { get; set; } = new();
    public List<RegistryItem> RegistryValues { get; set; } = new();
    public List<RegistryItem> RegistryKeys { get; set; } = new();
    public List<AclItem> Acls { get; set; } = new();
    public string? SqlInstance { get; set; }
    public string? UserSid { get; set; }

    public static string PathOf(string id) =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "WpfWebGpu", "installations", id + ".json");

    public static InstallRecord Load(string id)
    {
        string path = PathOf(id);
        if (!File.Exists(path)) return new InstallRecord();
        try { using var s = File.OpenRead(path); return JsonSerializer.Deserialize(s, RecordJson.Default.InstallRecord) ?? new InstallRecord(); }
        catch (JsonException) { return new InstallRecord(); }
    }

    public void Save(string id)
    {
        static List<T> Unique<T>(List<T> l) => l.Distinct().ToList();
        ProgramDataFiles = ProgramDataFiles.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        ProgramDataFolders = ProgramDataFolders.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        RegistryValues = Unique(RegistryValues); RegistryKeys = Unique(RegistryKeys); Acls = Unique(Acls);
        Directory.CreateDirectory(Path.GetDirectoryName(PathOf(id))!);
        using var s = File.Create(PathOf(id));
        JsonSerializer.Serialize(s, this, RecordJson.Default.InstallRecord);
    }

    /// <summary>The record gone (uninstall /all), and its folders where no other installation's is left.</summary>
    public static void Delete(string id)
    {
        string path = PathOf(id);
        if (File.Exists(path)) File.Delete(path);
        for (string? d = Path.GetDirectoryName(path); d != null && d.EndsWith("installations", StringComparison.Ordinal) || d != null && d.EndsWith("WpfWebGpu", StringComparison.Ordinal); d = Path.GetDirectoryName(d))
            if (Directory.Exists(d) && !Directory.EnumerateFileSystemEntries(d).Any()) Directory.Delete(d); else break;
    }
}

public sealed record RegistryItem(string Hive, string Key, string? Name);

public sealed record AclItem(string Folder, string Sid);

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(InstallRecord))]
internal sealed partial class RecordJson : JsonSerializerContext;
