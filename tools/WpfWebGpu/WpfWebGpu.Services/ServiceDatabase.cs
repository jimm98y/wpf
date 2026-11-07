// The service manager's database off Windows: the services an installation registered, as Windows keeps them in
// HKLM\SYSTEM\CurrentControlSet\Services -- each one's name, display name, description, program, start type, account,
// dependencies and security descriptor -- and the system service that runs it here (a launchd daemon, a systemd
// unit). The package writes it into the machine's state on every install (services.json beside registry/ and
// ProgramData/), so it is what is installed now, not what the first install seeded; the service manager (WpfWebGpu.
// Services.ServiceManager) answers from it and from launchd or systemd.

using System.Text.Json;
using System.Text.Json.Serialization;

namespace WpfWebGpu.Services;

public enum ServiceStart { Auto, Manual, Disabled }

public sealed class ServiceRecord
{
    public string Name { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string Description { get; set; } = "";
    /// <summary>The program and its arguments, as the service manager reports it (Win32_Service.PathName).</summary>
    public string PathName { get; set; } = "";
    [JsonConverter(typeof(JsonStringEnumConverter<ServiceStart>))]
    public ServiceStart Start { get; set; } = ServiceStart.Manual;
    public bool DelayedAutoStart { get; set; }
    /// <summary>The account as Windows names it (LocalSystem, NT AUTHORITY\NetworkService).</summary>
    public string StartName { get; set; } = "LocalSystem";
    public List<string> DependsOn { get; set; } = new();
    /// <summary>The security descriptor (self-relative, base64); empty for Windows' default.</summary>
    public string Security { get; set; } = "";
    /// <summary>The system service that runs it: a launchd label (system domain), a systemd unit.</summary>
    public string Unit { get; set; } = "";
}

public sealed class ServiceDatabase
{
    public List<ServiceRecord> Services { get; set; } = new();

    public const string FileName = "services.json";

    private static readonly JsonSerializerOptions s_json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true, PropertyNameCaseInsensitive = true,
    };

    /// <summary>The machine's database: WPFWEBGPU_SERVICES, else services.json in the machine's state (WPFRUN_STATE).</summary>
    public static string? DefaultPath()
    {
        if (Environment.GetEnvironmentVariable("WPFWEBGPU_SERVICES") is { Length: > 0 } explicitPath) return explicitPath;
        return Environment.GetEnvironmentVariable("WPFRUN_STATE") is { Length: > 0 } state ? Path.Combine(state, FileName) : null;
    }

    /// <summary>The database at <paramref name="path"/>; an empty one where there is none.</summary>
    public static ServiceDatabase Load(string? path)
    {
        if (path == null || !File.Exists(path)) return new ServiceDatabase();
        try { return JsonSerializer.Deserialize<ServiceDatabase>(File.ReadAllText(path), s_json) ?? new ServiceDatabase(); }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException) { return new ServiceDatabase(); }
    }

    public void Save(string path)
    {
        string temp = path + "." + Environment.ProcessId + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(this, s_json));
        File.Move(temp, path, overwrite: true);
    }

    public ServiceRecord? Find(string name) =>
        Services.FirstOrDefault(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
        ?? Services.FirstOrDefault(s => s.DisplayName.Equals(name, StringComparison.OrdinalIgnoreCase));

    // Windows' default for a service with no descriptor of its own: the system and administrators control it,
    // users (interactive, services) query and interrogate it
    private static readonly byte[] s_default = DefaultDescriptor();

    public static byte[] DescriptorOf(ServiceRecord service)
    {
        if (service.Security.Length > 0)
            try { return Convert.FromBase64String(service.Security); } catch (FormatException) { }
        return s_default;
    }

    private static byte[] DefaultDescriptor()
    {
        var aces = new List<byte[]>
        {
            Ace(0x000F01FF, 1, 5, 18),          // SYSTEM: all access
            Ace(0x000F01FF, 2, 5, 32, 544),     // Administrators: all access
            Ace(0x0002018D, 1, 5, 4),           // INTERACTIVE: query, interrogate, user-defined controls
            Ace(0x0002018D, 1, 5, 6),           // SERVICE: the same
        };
        int daclSize = 8 + aces.Sum(a => a.Length);
        var sd = new byte[20 + daclSize];
        sd[0] = 1; BitConverter.GetBytes((ushort)0x8004).CopyTo(sd, 2);   // self-relative, DACL present
        BitConverter.GetBytes(20).CopyTo(sd, 16);
        sd[20] = 2; BitConverter.GetBytes((ushort)daclSize).CopyTo(sd, 22); BitConverter.GetBytes((ushort)aces.Count).CopyTo(sd, 24);
        int at = 28;
        foreach (byte[] a in aces) { a.CopyTo(sd, at); at += a.Length; }
        return sd;

        static byte[] Ace(uint mask, int subCount, byte authority, params uint[] subs)
        {
            var ace = new byte[8 + 8 + 4 * subCount];
            ace[0] = 0; BitConverter.GetBytes((ushort)ace.Length).CopyTo(ace, 2); BitConverter.GetBytes(mask).CopyTo(ace, 4);
            ace[8] = 1; ace[9] = (byte)subCount; ace[15] = authority;
            for (int i = 0; i < subCount; i++) BitConverter.GetBytes(subs[i]).CopyTo(ace, 16 + 4 * i);
            return ace;
        }
    }
}
