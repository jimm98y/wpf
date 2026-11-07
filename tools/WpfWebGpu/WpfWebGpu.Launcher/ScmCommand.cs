// wpfrun scm: the machine's service manager (WpfWebGpu.Services) -- the daemon, root, that starts, stops and
// reconfigures the installed services for callers that are not root, each request decided by the service's security
// descriptor and the caller's account, as the service control manager decides an RPC caller's. Installed as a system
// service (a LaunchDaemon, a systemd unit) beside the services it manages; its database is services.json in the
// machine's state (WPFRUN_STATE).

using System.Runtime.InteropServices;
using WpfWebGpu.Services;

namespace WpfWebGpu;

internal static class ScmCommand
{
    public static int Run(string[] args)
    {
        string? database = ServiceDatabase.DefaultPath();
        if (database == null)
        {
            Console.Error.WriteLine("wpfrun scm: no service database (WPFRUN_STATE or WPFWEBGPU_SERVICES)");
            return 2;
        }
        if (WindowsAccounts.CurrentUid() != 0) Console.Error.WriteLine("wpfrun scm: not root -- requests for system services will fail");
        using var stop = new CancellationTokenSource();
        using var term = PosixSignalRegistration.Create(PosixSignal.SIGTERM, c => { c.Cancel = true; stop.Cancel(); });
        using var intr = PosixSignalRegistration.Create(PosixSignal.SIGINT, c => { c.Cancel = true; stop.Cancel(); });
        ServiceBroker.Serve(database, line => Console.Error.WriteLine($"{DateTime.Now:HH:mm:ss} {line}"), stop.Token).GetAwaiter().GetResult();
        return 0;
    }
}
