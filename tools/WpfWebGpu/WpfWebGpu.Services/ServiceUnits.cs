// What runs a service here: the launchd daemon (macOS) or the systemd unit (Linux) for a Windows service of an
// installation -- its program run by the installation's runtime, the machine's state, the account it runs as, whether
// it starts with the system, how it is restarted after a failure. The package writes them for the services it
// installs; the service manager writes them for a service a program creates (CreateService), as the SCM registers one.

using System.Security;
using System.Text;

namespace WpfWebGpu.Services;

/// <summary>The installation its services belong to: where it is, its machine's state, and how its units are named.
/// Kept in the service manager's database (services.json), for the services created after the install.</summary>
public sealed class ServiceHost
{
    /// <summary>The installation's folder (app/, runtime/): /opt/&lt;product&gt;, /Library/Application Support/&lt;product&gt;.</summary>
    public string Root { get; set; } = "";
    public string State { get; set; } = "";
    public string Cache { get; set; } = "";
    public string Logs { get; set; } = "";
    /// <summary>The product's name, in what the system shows of a unit.</summary>
    public string Product { get; set; } = "";
    /// <summary>What a service's unit is named by, before its name: a launchd label's ("com.example.p."), a systemd
    /// unit's ("p-").</summary>
    public string UnitPrefix { get; set; } = "";
    /// <summary>What every service of the installation runs with (the system it is told it is on, the runtime's settings).</summary>
    public Dictionary<string, string> Environment { get; set; } = new();
}

/// <summary>A service as the system service that runs it is written.</summary>
public sealed class ServiceDefinition
{
    /// <summary>The launchd label or the systemd unit's name.</summary>
    public string Unit { get; set; } = "";
    /// <summary>What it is called (the display name).</summary>
    public string Title { get; set; } = "";
    /// <summary>The program: a Windows .NET program (.exe), run by the installation's runtime, or this system's own.</summary>
    public string Program { get; set; } = "";
    public List<string> Args { get; set; } = new();
    public string WorkingDirectory { get; set; } = "";
    /// <summary>Its environment, the installation's (<see cref="ServiceHost.Environment"/>) included.</summary>
    public Dictionary<string, string> Environment { get; set; } = new();
    /// <summary>The system's account it runs as; null for root (LocalSystem).</summary>
    public string? Account { get; set; }
    public ServiceStart Start { get; set; } = ServiceStart.Manual;
    /// <summary>Seconds before it is started again after a failure (its recovery actions); 0 to leave it stopped.</summary>
    public int RestartAfterFailure { get; set; }
    /// <summary>The units of the services it depends on.</summary>
    public List<string> DependsOn { get; set; } = new();
    /// <summary>What its output is logged as: the log file's name (macOS), the syslog identifier (Linux).</summary>
    public string LogName { get; set; } = "";
}

public static class ServiceUnits
{
    /// <summary>A name as an identifier's segment: lower case letters, digits and '-'.</summary>
    public static string Segment(string s)
    {
        var sb = new StringBuilder();
        foreach (char c in s.ToLowerInvariant()) if (char.IsAsciiLetterOrDigit(c) || c == '-') sb.Append(c);
        return sb.Length == 0 ? "app" : sb.ToString();
    }

    /// <summary>The unit of the installation's service <paramref name="name"/>: a launchd label, a systemd unit.</summary>
    public static string UnitOf(ServiceHost host, string name, bool mac) => host.UnitPrefix + Segment(name) + (mac ? "" : ".service");

    /// <summary>The system's account for a Windows service account: NETWORK SERVICE's and LOCAL SERVICE's own; null
    /// for LocalSystem (root); a local account's name for a local account (.\name, MACHINE\name).</summary>
    public static string? SystemAccount(string startName, bool mac)
    {
        string name = startName.Trim();
        int slash = name.LastIndexOf('\\');
        string domain = slash < 0 ? "" : name[..slash], user = slash < 0 ? name : name[(slash + 1)..];
        if (user.Length == 0 || user.Equals("LocalSystem", StringComparison.OrdinalIgnoreCase)
            || (domain.Equals("NT AUTHORITY", StringComparison.OrdinalIgnoreCase) && user.Equals("SYSTEM", StringComparison.OrdinalIgnoreCase)))
            return null;
        if (user.Replace(" ", "").Equals("NetworkService", StringComparison.OrdinalIgnoreCase)) return mac ? "_wpfnetworkservice" : "wpf-networkservice";
        if (user.Replace(" ", "").Equals("LocalService", StringComparison.OrdinalIgnoreCase)) return mac ? "_wpflocalservice" : "wpf-localservice";
        return user;
    }

    /// <summary>Where the system service is installed: a daemon's plist, a unit file (a package's under /lib, one the
    /// service manager created under /etc, as an administrator's).</summary>
    public static string PathOf(string unit, bool mac, bool created) =>
        mac ? "/Library/LaunchDaemons/" + unit + ".plist" : (created ? "/etc/systemd/system/" : "/lib/systemd/system/") + unit;

    private static bool IsWindowsProgram(string program) => program.EndsWith(".exe", StringComparison.OrdinalIgnoreCase);

    // ---- launchd ----------------------------------------------------------------------------------------------

    public static string Launchd(ServiceHost host, ServiceDefinition d)
    {
        var args = new List<string>();
        if (IsWindowsProgram(d.Program)) args.Add(host.Root + "/runtime/run");
        args.Add(d.Program);
        args.AddRange(d.Args);
        string log = host.Logs + "/" + d.LogName + ".log";
        return $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
            <plist version="1.0">
            <dict>
              <key>Label</key><string>{X(d.Unit)}</string>
              <key>ProgramArguments</key>
              <array>
            {string.Concat(args.Select(a => $"    <string>{X(a)}</string>\n"))}  </array>
              <key>EnvironmentVariables</key>
              <dict>
                <key>WPFRUN_STATE</key><string>{X(host.State)}</string>
                <key>WPFRUN_CACHE</key><string>{X(host.Cache)}</string>
                <key>WPFRUN_LOGS</key><string>{X(host.Logs)}</string>
            {string.Concat(d.Environment.Select(e => $"    <key>{X(e.Key)}</key><string>{X(e.Value)}</string>\n"))}
              </dict>
              <key>WorkingDirectory</key><string>{X(d.WorkingDirectory)}</string>
              {(d.Account is { } user ? $"<key>UserName</key><string>{X(user)}</string>\n  <key>GroupName</key><string>staff</string>" : "")}
              <key>RunAtLoad</key><{(d.Start == ServiceStart.Auto ? "true" : "false")}/>
              {KeepAlive(d)}<key>ThrottleInterval</key><integer>{(d.RestartAfterFailure > 0 ? d.RestartAfterFailure : 10)}</integer>
              <key>ExitTimeOut</key><integer>30</integer>
              <key>Umask</key><integer>2</integer>
              <key>StandardOutPath</key><string>{X(log)}</string>
              <key>StandardErrorPath</key><string>{X(log)}</string>
            </dict>
            </plist>
            """;
    }

    // started again after a failure only where its recovery actions say so, as Windows does: a service without them is
    // left stopped. launchd's KeepAlive on an unsuccessful exit implies RunAtLoad (the job has to have run to have an
    // exit status), which started every manual service with the system; a manual one is kept alive only after a crash
    private static string KeepAlive(ServiceDefinition d) =>
        d.RestartAfterFailure <= 0 || d.Start == ServiceStart.Disabled ? ""
        : d.Start == ServiceStart.Auto ? "<key>KeepAlive</key><dict><key>SuccessfulExit</key><false/></dict>\n  "
        : "<key>KeepAlive</key><dict><key>Crashed</key><true/></dict>\n  ";

    private static string X(string s) => SecurityElement.Escape(s)!;

    // ---- systemd ----------------------------------------------------------------------------------------------

    public static string Systemd(ServiceHost host, ServiceDefinition d)
    {
        string program = IsWindowsProgram(d.Program) ? Quote(host.Root + "/runtime/run") + " " + Quote(d.Program) : Quote(d.Program);
        string args = string.Concat(d.Args.Select(a => " " + Quote(a)));
        string after = string.Join(" ", new[] { "network-online.target" }.Concat(d.DependsOn));
        return $"""
            [Unit]
            Description={d.Title} ({host.Product})
            After={after}
            Wants=network-online.target
            {(d.DependsOn.Count > 0 ? "Requires=" + string.Join(" ", d.DependsOn) + "\n" : "")}
            [Service]
            Type=simple
            ExecStart={program}{args}
            WorkingDirectory={d.WorkingDirectory}
            Environment=WPFRUN_STATE={host.State}
            Environment=WPFRUN_CACHE={host.Cache}
            Environment=WPFRUN_LOGS={host.Logs}
            {string.Concat(d.Environment.Select(e => $"Environment={e.Key}={e.Value}\n"))}
            {(d.Account is { } user
                ?$"User={user}\nGroup=users\n# a port below 1024, which Windows lets any account listen on\nAmbientCapabilities=CAP_NET_BIND_SERVICE\n" : "")}UMask=0002
            {(d.RestartAfterFailure > 0 ? $"Restart=on-failure\nRestartSec={d.RestartAfterFailure}\n" : "")}TimeoutStopSec=30
            SyslogIdentifier={d.LogName}

            [Install]
            WantedBy=multi-user.target

            """;
    }

    public static string Quote(string s) => s.IndexOfAny(new[] { ' ', '"', '\'', '\\' }) < 0 ? s : "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
}
