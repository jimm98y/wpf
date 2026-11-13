// Windows: the service control manager is the real one, and a service an installation's program creates
// (CreateService) or points at another program (ChangeServiceConfig) is registered with it as asked -- but a .NET
// program's own executable is registered as the command that runs it on the installation's runtime
// (ServiceManager.OnInstallationRuntime), as Setup registers the services it installs: the program's executable
// would look for a .NET installed on the machine, which an installation of this stack does not need.

using System.Runtime.InteropServices;
using WpfWebGpu.Services;

namespace WpfWebGpu.Win32;

public static unsafe partial class Exports
{
    [Win32ExportOnWindows(Always, "CreateServiceW")]
    public static nint WinCreateServiceW(nint scm, char* name, char* displayName, uint access, uint type, uint start, uint errorControl,
        char* path, char* group, uint* tag, char* dependencies, char* account, char* password)
    {
        string? prepared = path == null ? null : ServiceManager.OnInstallationRuntime(new string(path));
        fixed (char* p = prepared)
        {
            nint h = RealCreateServiceW(scm, name, displayName, access, type, start, errorControl, prepared != null ? p : path, group, tag, dependencies, account, password);
            SetLastError(Marshal.GetLastPInvokeError());
            return h;
        }
    }

    [Win32ExportOnWindows(Always, "CreateServiceA")]
    public static nint WinCreateServiceA(nint scm, byte* name, byte* displayName, uint access, uint type, uint start, uint errorControl,
        byte* path, byte* group, uint* tag, byte* dependencies, byte* account, byte* password)
    {
        nint prepared = Ansi(path);
        try
        {
            nint h = RealCreateServiceA(scm, name, displayName, access, type, start, errorControl, prepared != 0 ? (byte*)prepared : path, group, tag, dependencies, account, password);
            SetLastError(Marshal.GetLastPInvokeError());
            return h;
        }
        finally { Marshal.FreeHGlobal(prepared); }
    }

    [Win32ExportOnWindows(Always, "ChangeServiceConfigW")]
    public static int WinChangeServiceConfigW(nint service, uint type, uint start, uint errorControl, char* path, char* group, uint* tag,
        char* dependencies, char* account, char* password, char* displayName)
    {
        string? prepared = path == null ? null : ServiceManager.OnInstallationRuntime(new string(path));
        fixed (char* p = prepared)
        {
            int ok = RealChangeServiceConfigW(service, type, start, errorControl, prepared != null ? p : path, group, tag, dependencies, account, password, displayName);
            SetLastError(Marshal.GetLastPInvokeError());
            return ok;
        }
    }

    [Win32ExportOnWindows(Always, "ChangeServiceConfigA")]
    public static int WinChangeServiceConfigA(nint service, uint type, uint start, uint errorControl, byte* path, byte* group, uint* tag,
        byte* dependencies, byte* account, byte* password, byte* displayName)
    {
        nint prepared = Ansi(path);
        try
        {
            int ok = RealChangeServiceConfigA(service, type, start, errorControl, prepared != 0 ? (byte*)prepared : path, group, tag, dependencies, account, password, displayName);
            SetLastError(Marshal.GetLastPInvokeError());
            return ok;
        }
        finally { Marshal.FreeHGlobal(prepared); }
    }

    // an ANSI command line, prepared for the installation's runtime and in the ANSI code page again; 0 where it stays
    private static nint Ansi(byte* path)
    {
        if (path == null || Marshal.PtrToStringAnsi((nint)path) is not { } given) return 0;
        return ServiceManager.OnInstallationRuntime(given) is { } prepared ? Marshal.StringToHGlobalAnsi(prepared) : 0;
    }

    [DllImport("advapi32", EntryPoint = "CreateServiceW", SetLastError = true)]
    private static extern nint RealCreateServiceW(nint scm, char* name, char* displayName, uint access, uint type, uint start, uint errorControl,
        char* path, char* group, uint* tag, char* dependencies, char* account, char* password);

    [DllImport("advapi32", EntryPoint = "CreateServiceA", SetLastError = true)]
    private static extern nint RealCreateServiceA(nint scm, byte* name, byte* displayName, uint access, uint type, uint start, uint errorControl,
        byte* path, byte* group, uint* tag, byte* dependencies, byte* account, byte* password);

    [DllImport("advapi32", EntryPoint = "ChangeServiceConfigW", SetLastError = true)]
    private static extern int RealChangeServiceConfigW(nint service, uint type, uint start, uint errorControl, char* path, char* group, uint* tag,
        char* dependencies, char* account, char* password, char* displayName);

    [DllImport("advapi32", EntryPoint = "ChangeServiceConfigA", SetLastError = true)]
    private static extern int RealChangeServiceConfigA(nint service, uint type, uint start, uint errorControl, byte* path, byte* group, uint* tag,
        byte* dependencies, byte* account, byte* password, byte* displayName);
}
