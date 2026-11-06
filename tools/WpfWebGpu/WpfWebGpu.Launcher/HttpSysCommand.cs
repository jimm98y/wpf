// wpfrun httpsys [--on-demand]: the machine's http.sys service (WpfWebGpu.HttpSys) -- the ports the registered URL
// prefixes name, served apart from every app. Installed, it runs as a system service (a LaunchDaemon, a systemd
// unit) and waits to take over while another instance serves; on demand (started by the first app that registers
// where none is installed), it leaves once no prefix has been registered for a while.

using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;

namespace WpfWebGpu;

internal static class HttpSysCommand
{
    public static int Run(string[] args)
    {
        bool onDemand = args.Contains("--on-demand");
        // the registry apps see (http.sys's SSL bindings are read from it)
        if (!HostInfo.IsWindows && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WPFWEBGPU_REGISTRY_DIR")))
            Environment.SetEnvironmentVariable("WPFWEBGPU_REGISTRY_DIR", Path.Combine(Locations.StateRoot, "registry"));
        using var loggers = LoggerFactory.Create(b => b.AddSimpleConsole(o => { o.SingleLine = true; o.TimestampFormat = "HH:mm:ss "; }).SetMinimumLevel(LogLevel.Information));
        var log = loggers.CreateLogger("httpsys");
        using var stop = new CancellationTokenSource();
        using var term = PosixSignalRegistration.Create(PosixSignal.SIGTERM, c => { c.Cancel = true; stop.Cancel(); });
        using var intr = PosixSignalRegistration.Create(PosixSignal.SIGINT, c => { c.Cancel = true; stop.Cancel(); });
        return WpfWebGpu.HttpSys.HttpSysService.Run(onDemand, log, stop.Token).GetAwaiter().GetResult();
    }
}
