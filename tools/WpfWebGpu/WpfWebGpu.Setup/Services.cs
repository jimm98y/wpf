// The service control manager: a service created (or, on an upgrade, changed) as the original installer created it --
// its image path, start type, dependencies, restart after a failure -- started, stopped and deleted.

using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace WpfWebGpu.Setup;

internal static unsafe class Services
{
    private const uint SC_MANAGER_ALL_ACCESS = 0xF003F, SERVICE_ALL_ACCESS = 0xF01FF, SERVICE_NO_CHANGE = 0xFFFFFFFF;
    private const uint SERVICE_WIN32_OWN_PROCESS = 0x10, SERVICE_ERROR_NORMAL = 1;
    private const uint SERVICE_AUTO_START = 2, SERVICE_DEMAND_START = 3, SERVICE_DISABLED = 4;
    private const uint SERVICE_CONTROL_STOP = 1, SERVICE_STOPPED = 1;
    private const uint SERVICE_CONFIG_DESCRIPTION = 1, SERVICE_CONFIG_FAILURE_ACTIONS = 2, SERVICE_CONFIG_DELAYED_AUTO_START_INFO = 3;
    private const uint DACL_SECURITY_INFORMATION = 4;
    private const int ERROR_SERVICE_DOES_NOT_EXIST = 1060, ERROR_SERVICE_ALREADY_RUNNING = 1056, ERROR_SERVICE_NOT_ACTIVE = 1062;

    public static void CreateOrUpdate(string name, string displayName, string command, string start, List<string> dependsOn, Dictionary<string, string> environment, string account,
        string startName = "", string description = "", bool delayedAutoStart = false, string security = "")
    {
        // the account it runs as: as it was registered (a virtual account, NT SERVICE\Name, included), else the
        // manager's names for Windows' own service accounts
        string user = startName.Length > 0 ? startName
            : account switch { "NetworkService" => @"NT AUTHORITY\NetworkService", "LocalService" => @"NT AUTHORITY\LocalService", _ => "LocalSystem" };
        uint startType = start switch { "Manual" => SERVICE_DEMAND_START, "Disabled" => SERVICE_DISABLED, _ => SERVICE_AUTO_START };
        string deps = dependsOn.Count == 0 ? "\0" : string.Concat(dependsOn.Select(d => d + "\0")) + "\0";
        IntPtr scm = OpenSCManagerW(null, null, SC_MANAGER_ALL_ACCESS);
        if (scm == IntPtr.Zero) throw new Win32Exception();
        try
        {
            IntPtr svc = OpenServiceW(scm, name, SERVICE_ALL_ACCESS);
            if (svc != IntPtr.Zero)
            {
                if (!ChangeServiceConfigW(svc, SERVICE_WIN32_OWN_PROCESS, startType, SERVICE_ERROR_NORMAL, command, null, IntPtr.Zero, deps, user, null, displayName))
                    throw new Win32Exception();
            }
            else
            {
                svc = CreateServiceW(scm, name, displayName, SERVICE_ALL_ACCESS, SERVICE_WIN32_OWN_PROCESS, startType, SERVICE_ERROR_NORMAL,
                    command, null, IntPtr.Zero, deps, user, null);
                if (svc == IntPtr.Zero) throw new Win32Exception();
            }
            try
            {
                RestartOnFailure(svc);
                // its description and delayed start, and who may control it, as it was registered
                fixed (char* text = description + "\0")
                {
                    IntPtr p = (IntPtr)text;
                    ChangeServiceConfig2W(svc, SERVICE_CONFIG_DESCRIPTION, &p);
                }
                int delayed = delayedAutoStart ? 1 : 0;
                ChangeServiceConfig2W(svc, SERVICE_CONFIG_DELAYED_AUTO_START_INFO, &delayed);
                if (security.Length > 0)
                {
                    byte[] sd = Convert.FromBase64String(security);
                    fixed (byte* d = sd)
                        if (!SetServiceObjectSecurity(svc, DACL_SECURITY_INFORMATION, d))
                            Console.WriteLine($"  {name}: its security descriptor not set ({new Win32Exception().Message})");
                }
            }
            finally { CloseServiceHandle(svc); }
        }
        finally { CloseServiceHandle(scm); }
        // the service's environment, as the manager gives it to its process (the service key's Environment)
        using var key = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Services\{name}", writable: true);
        if (key == null) return;
        if (environment.Count > 0) key.SetValue("Environment", environment.Select(e => $"{e.Key}={e.Value}").ToArray(), RegistryValueKind.MultiString);
        else key.DeleteValue("Environment", throwOnMissingValue: false);
    }

    // restarted ten seconds after it fails, as systemd's and launchd's units do
    private static void RestartOnFailure(IntPtr svc)
    {
        var actions = stackalloc SC_ACTION[3];
        for (int i = 0; i < 3; i++) actions[i] = new SC_ACTION { Type = 1 /* SC_ACTION_RESTART */, Delay = 10_000 };
        var fa = new SERVICE_FAILURE_ACTIONS { dwResetPeriod = 86400, cActions = 3, lpsaActions = actions };
        ChangeServiceConfig2W(svc, SERVICE_CONFIG_FAILURE_ACTIONS, &fa);
    }

    public static void Start(string name)
    {
        Using(name, svc =>
        {
            if (!StartServiceW(svc, 0, IntPtr.Zero) && Marshal.GetLastWin32Error() is int e && e != ERROR_SERVICE_ALREADY_RUNNING)
                Console.WriteLine($"  {name}: not started ({new Win32Exception(e).Message})");
        });
    }

    public static void Stop(string name)
    {
        Using(name, svc =>
        {
            SERVICE_STATUS status;
            if (!ControlService(svc, SERVICE_CONTROL_STOP, &status)) return;      // not running
            for (int i = 0; i < 60 && QueryServiceStatus(svc, &status) && status.dwCurrentState != SERVICE_STOPPED; i++) Thread.Sleep(500);
        });
    }

    public static void Delete(string name) => Using(name, svc => DeleteService(svc));

    private static void Using(string name, Action<IntPtr> action)
    {
        IntPtr scm = OpenSCManagerW(null, null, SC_MANAGER_ALL_ACCESS);
        if (scm == IntPtr.Zero) throw new Win32Exception();
        try
        {
            IntPtr svc = OpenServiceW(scm, name, SERVICE_ALL_ACCESS);
            if (svc == IntPtr.Zero) return;           // not installed
            try { action(svc); }
            finally { CloseServiceHandle(svc); }
        }
        finally { CloseServiceHandle(scm); }
    }

    [StructLayout(LayoutKind.Sequential)] private struct SERVICE_STATUS { public uint dwServiceType, dwCurrentState, dwControlsAccepted, dwWin32ExitCode, dwServiceSpecificExitCode, dwCheckPoint, dwWaitHint; }
    [StructLayout(LayoutKind.Sequential)] private struct SC_ACTION { public int Type; public uint Delay; }
    [StructLayout(LayoutKind.Sequential)] private struct SERVICE_FAILURE_ACTIONS { public uint dwResetPeriod; public IntPtr lpRebootMsg, lpCommand; public uint cActions; public SC_ACTION* lpsaActions; }

    [DllImport("advapi32", SetLastError = true, CharSet = CharSet.Unicode)] private static extern IntPtr OpenSCManagerW(string? machine, string? db, uint access);
    [DllImport("advapi32", SetLastError = true, CharSet = CharSet.Unicode)] private static extern IntPtr OpenServiceW(IntPtr scm, string name, uint access);
    [DllImport("advapi32", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateServiceW(IntPtr scm, string name, string display, uint access, uint type, uint start, uint error, string path, string? group, IntPtr tag, string deps, string? user, string? password);
    [DllImport("advapi32", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool ChangeServiceConfigW(IntPtr svc, uint type, uint start, uint error, string path, string? group, IntPtr tag, string deps, string? user, string? password, string display);
    [DllImport("advapi32", SetLastError = true)] private static extern bool ChangeServiceConfig2W(IntPtr svc, uint level, void* info);
    [DllImport("advapi32", SetLastError = true)] private static extern bool SetServiceObjectSecurity(IntPtr svc, uint information, byte* descriptor);
    [DllImport("advapi32", SetLastError = true)] private static extern bool StartServiceW(IntPtr svc, uint argc, IntPtr argv);
    [DllImport("advapi32", SetLastError = true)] private static extern bool ControlService(IntPtr svc, uint control, SERVICE_STATUS* status);
    [DllImport("advapi32", SetLastError = true)] private static extern bool QueryServiceStatus(IntPtr svc, SERVICE_STATUS* status);
    [DllImport("advapi32", SetLastError = true)] private static extern bool DeleteService(IntPtr svc);
    [DllImport("advapi32", SetLastError = true)] private static extern bool CloseServiceHandle(IntPtr h);
}
