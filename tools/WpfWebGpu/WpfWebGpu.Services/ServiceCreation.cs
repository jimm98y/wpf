// Creating and deleting services, as the SCM does for CreateService and DeleteService: a program of the installation
// (its own setup, a configuration tool) registers another service, or removes one. Only an administrator may create one
// (the SCM's own descriptor grants SC_MANAGER_CREATE_SERVICE to administrators and SYSTEM alone: an elevated
// program, root here); deleting takes the service's DELETE right. The service is written as the package writes those
// it installs (ServiceUnits) -- a launchd daemon, a systemd unit under /etc, as an administrator's -- and kept in the
// database; it starts with the system if it is automatic, but not now (CreateService does not start it). A service
// deleted while it runs is marked, refuses what is asked of it but stopping, and goes once it has stopped.

using System.Text;
using System.Text.Json;

namespace WpfWebGpu.Services;

/// <summary>A service a program asks to create: as registered (its name, display name, command line as given, start
/// type, account, dependencies), and the program and arguments that command line names here.</summary>
public sealed class ServiceCreateRequest
{
    public ServiceRecord Service { get; set; } = new();
    public string Program { get; set; } = "";
    public List<string> Args { get; set; } = new();

    private static readonly JsonSerializerOptions s_json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public string Encode() => Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(this, s_json));

    public static ServiceCreateRequest Decode(string text)
    {
        try { return JsonSerializer.Deserialize<ServiceCreateRequest>(Convert.FromBase64String(text), s_json) ?? throw Bad(); }
        catch (Exception e) when (e is FormatException or JsonException) { throw Bad(); }
        static ServiceException Bad() => new(ServiceManager.ERROR_INVALID_PARAMETER, "The parameter is incorrect.");
    }
}

public static partial class ServiceManager
{
    /// <summary>Registers a service (CreateService); throws Windows' refusal.</summary>
    public static void Create(ServiceCreateRequest request)
    {
        if (OperatingSystem.IsWindows()) { WindowsServiceManager.Create(request); return; }
        Control("create", request.Service.Name, request.Encode());
    }

    /// <summary>Removes a service (DeleteService): now, or once it stops if it runs.</summary>
    public static void Delete(string name) => Control("delete", name, "");

    /// <summary>The program and arguments of a service's command line, as the SCM reads one: a quoted program, or the
    /// shortest run of the words that names a file (C:\Program Files\A B\svc.exe unquoted is found as Windows finds
    /// it), then the arguments as a program's command line splits them. <paramref name="disk"/> maps a path as the
    /// program gave it to this system's.</summary>
    public static (string Program, List<string> Args) SplitCommandLine(string commandLine, Func<string, string> disk)
    {
        string line = commandLine.Trim();
        string program, rest;
        if (line.StartsWith('"'))
        {
            int end = line.IndexOf('"', 1);
            program = end < 0 ? line[1..] : line[1..end];
            rest = end < 0 ? "" : line[(end + 1)..];
        }
        else
        {
            // each run of words in turn, the whole line last; none a file: the first word
            int split = -1;
            for (int at = line.IndexOf(' '); split < 0; at = line.IndexOf(' ', at + 1))
            {
                string candidate = at < 0 ? line : line[..at];
                if (File.Exists(disk(candidate)) || File.Exists(disk(candidate + ".exe"))) split = at < 0 ? line.Length : at;
                else if (at < 0) split = line.IndexOf(' ') is int first and >= 0 ? first : line.Length;
            }
            program = line[..split];
            rest = line[split..];
        }
        string mapped = disk(program);
        if (!File.Exists(mapped) && File.Exists(disk(program + ".exe"))) mapped = disk(program + ".exe");
        return (mapped, SplitArguments(rest));
    }

    // CommandLineToArgvW's rules: whitespace separates, quotes group, 2n backslashes before a quote are n and the
    // quote groups, 2n+1 are n and a literal quote
    internal static List<string> SplitArguments(string text)
    {
        var args = new List<string>();
        var current = new StringBuilder();
        bool inQuotes = false, any = false;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '\\')
            {
                int n = 0;
                while (i < text.Length && text[i] == '\\') { n++; i++; }
                if (i < text.Length && text[i] == '"')
                {
                    current.Append('\\', n / 2);
                    if (n % 2 == 1) current.Append('"');
                    else { inQuotes = !inQuotes; }
                    any = true;
                }
                else { current.Append('\\', n); i--; any = true; }
            }
            else if (c == '"')
            {
                if (inQuotes && i + 1 < text.Length && text[i + 1] == '"') { current.Append('"'); i++; }
                else inQuotes = !inQuotes;
                any = true;
            }
            else if (char.IsWhiteSpace(c) && !inQuotes)
            {
                if (any) { args.Add(current.ToString()); current.Clear(); any = false; }
            }
            else { current.Append(c); any = true; }
        }
        if (any) args.Add(current.ToString());
        return args;
    }
}

public static partial class ServiceControl
{
    internal static ServiceException MarkedForDelete() =>
        new(ServiceManager.ERROR_SERVICE_MARKED_FOR_DELETE, "The specified service has been marked for deletion.");

    private static void Create(string? databasePath, string encoded, uint caller)
    {
        var request = ServiceCreateRequest.Decode(encoded);
        if (!WindowsAccounts.AccessAllowed(ServiceDatabase.ManagerDescriptor, WindowsAccounts.TokenOf(caller), ServiceManager.ManagerCreateService))
            throw new ServiceException(ServiceManager.ERROR_ACCESS_DENIED, $"Access is denied ({WindowsAccounts.Describe(caller)} may not create services).");
        if (databasePath == null) throw new ServiceException(ServiceManager.ERROR_CALL_NOT_IMPLEMENTED, "There is no installation to create the service in.");
        var db = ServiceDatabase.Load(databasePath);
        ServiceHost host = db.Host ?? throw new ServiceException(ServiceManager.ERROR_CALL_NOT_IMPLEMENTED,
            "The installation's service manager does not know where the installation is (a package of an older Packager).");

        ServiceRecord service = request.Service;
        string name = service.Name;
        if (name.Length == 0 || name.Length > 256 || name.IndexOfAny(new[] { '/', '\\' }) >= 0)
            throw new ServiceException(ServiceManager.ERROR_INVALID_NAME, "The name is invalid.");
        if (service.DisplayName.Length == 0) service.DisplayName = name;
        if (db.Services.FirstOrDefault(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) is { } existing)
            throw existing.MarkedForDelete ? MarkedForDelete() : new ServiceException(ServiceManager.ERROR_SERVICE_EXISTS, "The specified service already exists.");
        if (db.Services.Any(s => s.DisplayName.Equals(service.DisplayName, StringComparison.OrdinalIgnoreCase) || s.Name.Equals(service.DisplayName, StringComparison.OrdinalIgnoreCase)
                                 || s.DisplayName.Equals(name, StringComparison.OrdinalIgnoreCase)))
            throw new ServiceException(ServiceManager.ERROR_DUPLICATE_SERVICE_NAME, "The name is already in use as either a service name or a service display name.");
        if (request.Program.Length == 0) throw new ServiceException(ServiceManager.ERROR_INVALID_PARAMETER, "The parameter is incorrect.");

        bool mac = OperatingSystem.IsMacOS();
        // its unit, by its name as the package names them; another name that makes the same one gets a number
        string unit = ServiceUnits.UnitOf(host, name, mac);
        for (int n = 2; db.Services.Any(s => s.Unit == unit) || File.Exists(ServiceUnits.PathOf(unit, mac, created: true)) || File.Exists(ServiceUnits.PathOf(unit, mac, created: false)); n++)
            unit = ServiceUnits.UnitOf(host, name + "-" + n, mac);

        var definition = new ServiceDefinition
        {
            Unit = unit,
            Title = service.DisplayName,
            Program = request.Program,
            Args = request.Args,
            WorkingDirectory = Path.GetDirectoryName(request.Program) ?? host.Root,
            Environment = new Dictionary<string, string>(host.Environment),
            Account = ServiceUnits.SystemAccount(service.StartName, mac),
            Start = service.Start,
            DependsOn = service.DependsOn.Select(d => db.Find(d)?.Unit).Where(u => !string.IsNullOrEmpty(u)).Select(u => u!).ToList(),
            LogName = Path.GetFileNameWithoutExtension(request.Program),
        };
        string file = ServiceUnits.PathOf(unit, mac, created: true);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, mac ? ServiceUnits.Launchd(host, definition) : ServiceUnits.Systemd(host, definition));
        // launchd loads no daemon a group or others may write (the service manager's umask leaves them that)
        File.SetUnixFileMode(file, (UnixFileMode)Convert.ToInt32("644", 8));
        if (mac)
        {
            // loaded at the next boot when automatic, by its start otherwise: CreateService starts nothing now
            if (service.Start == ServiceStart.Disabled) ServiceManager.Run("/bin/launchctl", "disable", "system/" + unit);
        }
        else
        {
            ServiceManager.Run("/usr/bin/systemctl", "daemon-reload");
            if (service.Start == ServiceStart.Auto) ServiceManager.Run("/usr/bin/systemctl", "enable", unit);
            else if (service.Start == ServiceStart.Disabled) ServiceManager.Run("/usr/bin/systemctl", "mask", unit);
        }

        service.Unit = unit;
        service.Created = true;
        service.MarkedForDelete = false;
        db.Services.Add(service);
        db.Save(databasePath);
    }

    private static void Delete(string? databasePath, ServiceDatabase db, ServiceRecord service)
    {
        if (ServiceManager.StatusOf(db, service).State is not ServiceState.Stopped)
        {
            // as the SCM: marked, and gone once it stops
            service.MarkedForDelete = true;
            if (databasePath != null) db.Save(databasePath);
            return;
        }
        Remove(db, service);
        if (databasePath != null) db.Save(databasePath);
    }

    /// <summary>Removes the services deleted while they ran that have stopped since.</summary>
    public static void RemoveStoppedDeleted(string? databasePath)
    {
        if (databasePath == null) return;
        var db = ServiceDatabase.Load(databasePath);
        var marked = db.Services.Where(s => s.MarkedForDelete).ToList();
        if (marked.Count == 0) return;
        var statuses = ServiceManager.Statuses(db);
        bool changed = false;
        foreach (var s in marked)
            if (statuses.GetValueOrDefault(s.Name).State is ServiceState.Stopped or (ServiceState)0) { Remove(db, s); changed = true; }
        if (changed) db.Save(databasePath);
    }

    // its unit unloaded and removed, then the service from the database
    private static void Remove(ServiceDatabase db, ServiceRecord service)
    {
        bool mac = OperatingSystem.IsMacOS();
        if (service.Unit.Length > 0)
        {
            if (mac)
            {
                ServiceManager.Run("/bin/launchctl", "bootout", "system/" + service.Unit);
                ServiceManager.Run("/bin/launchctl", "enable", "system/" + service.Unit);     // no override left behind
            }
            else if (OperatingSystem.IsLinux())
            {
                ServiceManager.Run("/usr/bin/systemctl", "disable", service.Unit);
                ServiceManager.Run("/usr/bin/systemctl", "unmask", service.Unit);
            }
            foreach (bool created in new[] { true, false })
            {
                string file = ServiceUnits.PathOf(service.Unit, mac, created);
                if (File.Exists(file)) File.Delete(file);
            }
            if (OperatingSystem.IsLinux()) ServiceManager.Run("/usr/bin/systemctl", "daemon-reload");
        }
        db.Services.Remove(service);
    }
}
