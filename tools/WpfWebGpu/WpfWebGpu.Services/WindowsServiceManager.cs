// The service manager on Windows: the service control manager itself (advapi32). The same stand-ins answer from it as
// answer from the database elsewhere -- the installed services and their configuration (EnumServicesStatusEx,
// QueryServiceConfig), their state (QueryServiceStatusEx), starting, stopping and reconfiguring them (StartService,
// ControlService, ChangeServiceConfig) -- and the service control manager decides each request by the service's own
// security descriptor and the caller's token, as it always does.

using System.ComponentModel;
using System.Runtime.InteropServices;

namespace WpfWebGpu.Services;

internal static partial class WindowsServiceManager
{
    private const uint SC_MANAGER_CONNECT = 0x1, SC_MANAGER_ENUMERATE_SERVICE = 0x4;
    private const uint SERVICE_WIN32 = 0x30, SERVICE_STATE_ALL = 0x3, SC_ENUM_PROCESS_INFO = 0, SC_STATUS_PROCESS_INFO = 0;
    private const uint SERVICE_CONTROL_STOP = 1, SERVICE_NO_CHANGE = 0xFFFFFFFF;
    private const uint SERVICE_CONFIG_DESCRIPTION = 1, SERVICE_CONFIG_DELAYED_AUTO_START_INFO = 3;
    private const int ERROR_INSUFFICIENT_BUFFER = 122, ERROR_MORE_DATA = 234;

    public static ServiceDatabase Database()
    {
        var db = new ServiceDatabase();
        IntPtr scm = OpenSCManagerW(null, null, SC_MANAGER_CONNECT | SC_MANAGER_ENUMERATE_SERVICE);
        if (scm == IntPtr.Zero) throw Error();
        try
        {
            uint resume = 0;
            while (true)
            {
                EnumServicesStatusExW(scm, SC_ENUM_PROCESS_INFO, SERVICE_WIN32, SERVICE_STATE_ALL, IntPtr.Zero, 0, out uint needed, out _, ref resume, null);
                IntPtr buffer = Marshal.AllocHGlobal((int)needed + 1024);
                try
                {
                    bool done = EnumServicesStatusExW(scm, SC_ENUM_PROCESS_INFO, SERVICE_WIN32, SERVICE_STATE_ALL, buffer, needed + 1024, out _, out uint count, ref resume, null);
                    int last = Marshal.GetLastWin32Error();
                    if (!done && last != ERROR_MORE_DATA) throw new ServiceException(last, new Win32Exception(last).Message);
                    // ENUM_SERVICE_STATUS_PROCESSW: lpServiceName, lpDisplayName, SERVICE_STATUS_PROCESS (9 DWORDs)
                    int size = 2 * IntPtr.Size + 36 + (IntPtr.Size == 8 ? 4 : 0);
                    for (int i = 0; i < count; i++)
                    {
                        IntPtr entry = buffer + i * size;
                        string name = Marshal.PtrToStringUni(Marshal.ReadIntPtr(entry)) ?? "";
                        db.Services.Add(Record(scm, name, Marshal.PtrToStringUni(Marshal.ReadIntPtr(entry, IntPtr.Size)) ?? name));
                    }
                    if (done) break;
                }
                finally { Marshal.FreeHGlobal(buffer); }
            }
        }
        finally { CloseServiceHandle(scm); }
        return db;
    }

    // a service's configuration (QUERY_SERVICE_CONFIGW), its description and delayed start
    private static ServiceRecord Record(IntPtr scm, string name, string displayName)
    {
        var record = new ServiceRecord { Name = name, DisplayName = displayName, Unit = name };
        IntPtr service = OpenServiceW(scm, name, ServiceManager.QueryConfig);
        if (service == IntPtr.Zero) return record;
        try
        {
            QueryServiceConfigW(service, IntPtr.Zero, 0, out uint needed);
            if (needed > 0)
            {
                IntPtr buffer = Marshal.AllocHGlobal((int)needed);
                try
                {
                    if (QueryServiceConfigW(service, buffer, needed, out _))
                    {
                        // dwServiceType, dwStartType, dwErrorControl, lpBinaryPathName, lpLoadOrderGroup, dwTagId,
                        // lpDependencies, lpServiceStartName, lpDisplayName
                        uint start = (uint)Marshal.ReadInt32(buffer, 4);
                        int p = IntPtr.Size == 8 ? 16 : 12;
                        record.PathName = Marshal.PtrToStringUni(Marshal.ReadIntPtr(buffer, p)) ?? "";
                        int deps = p + 2 * IntPtr.Size + (IntPtr.Size == 8 ? 8 : 4);
                        record.DependsOn = MultiString(Marshal.ReadIntPtr(buffer, deps)).Where(d => !d.StartsWith('+')).ToList();
                        record.StartName = Marshal.PtrToStringUni(Marshal.ReadIntPtr(buffer, deps + IntPtr.Size)) ?? "";
                        record.Start = start <= 2 ? ServiceStart.Auto : start == 3 ? ServiceStart.Manual : ServiceStart.Disabled;
                    }
                }
                finally { Marshal.FreeHGlobal(buffer); }
            }
            record.Description = Config2String(service, SERVICE_CONFIG_DESCRIPTION) ?? "";
            record.DelayedAutoStart = Config2Bool(service, SERVICE_CONFIG_DELAYED_AUTO_START_INFO);
        }
        finally { CloseServiceHandle(service); }
        return record;
    }

    public static Dictionary<string, ServiceStatus> Statuses(ServiceDatabase db)
    {
        var result = new Dictionary<string, ServiceStatus>(StringComparer.OrdinalIgnoreCase);
        IntPtr scm = OpenSCManagerW(null, null, SC_MANAGER_CONNECT);
        if (scm == IntPtr.Zero) throw Error();
        try
        {
            foreach (var s in db.Services)
            {
                IntPtr service = OpenServiceW(scm, s.Name, ServiceManager.QueryStatus);
                if (service == IntPtr.Zero) { result[s.Name] = new ServiceStatus(ServiceState.Stopped, 0, 0); continue; }
                try
                {
                    // SERVICE_STATUS_PROCESS: type, state, controls, exit, specific exit, checkpoint, wait hint, pid, flags
                    Span<uint> status = stackalloc uint[9];
                    unsafe
                    {
                        fixed (uint* p = status)
                            if (!QueryServiceStatusEx(service, SC_STATUS_PROCESS_INFO, (IntPtr)p, 36, out _)) { result[s.Name] = new ServiceStatus(ServiceState.Stopped, 0, 0); continue; }
                    }
                    result[s.Name] = new ServiceStatus((ServiceState)status[1], (int)status[7], (int)status[3]);
                }
                finally { CloseServiceHandle(service); }
            }
        }
        finally { CloseServiceHandle(scm); }
        return result;
    }

    public static void Execute(string op, string name, string arg)
    {
        IntPtr scm = OpenSCManagerW(null, null, SC_MANAGER_CONNECT);
        if (scm == IntPtr.Zero) throw Error();
        try
        {
            uint right = op switch { "start" => ServiceManager.StartRight, "stop" => ServiceManager.StopRight, "config" => ServiceManager.ChangeConfig, _ => 0 };
            if (right == 0) throw new ServiceException(ServiceManager.ERROR_INVALID_SERVICE_CONTROL, "The requested control is not valid for this service.");
            IntPtr service = OpenServiceW(scm, name, right | ServiceManager.QueryStatus);
            if (service == IntPtr.Zero) throw Error();
            try
            {
                bool ok = op switch
                {
                    "start" => StartServiceW(service, 0, IntPtr.Zero),
                    "stop" => ControlService(service, SERVICE_CONTROL_STOP, new uint[7]),
                    _ => Enum.TryParse(arg, ignoreCase: true, out ServiceStart start)
                         && ChangeServiceConfigW(service, SERVICE_NO_CHANGE, start switch { ServiceStart.Auto => 2u, ServiceStart.Manual => 3u, _ => 4u },
                                                 SERVICE_NO_CHANGE, null, null, IntPtr.Zero, null, null, null, null),
                };
                if (!ok) throw Error();
            }
            finally { CloseServiceHandle(service); }
        }
        finally { CloseServiceHandle(scm); }
    }

    private static ServiceException Error()
    {
        int e = Marshal.GetLastWin32Error();
        return new ServiceException(e, new Win32Exception(e).Message);
    }

    private static IEnumerable<string> MultiString(IntPtr p)
    {
        if (p == IntPtr.Zero) yield break;
        while (true)
        {
            string s = Marshal.PtrToStringUni(p) ?? "";
            if (s.Length == 0) yield break;
            yield return s;
            p += (s.Length + 1) * 2;
        }
    }

    private static string? Config2String(IntPtr service, uint level)
    {
        QueryServiceConfig2W(service, level, IntPtr.Zero, 0, out uint needed);
        if (needed == 0) return null;
        IntPtr buffer = Marshal.AllocHGlobal((int)needed);
        try { return QueryServiceConfig2W(service, level, buffer, needed, out _) ? Marshal.PtrToStringUni(Marshal.ReadIntPtr(buffer)) : null; }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    private static bool Config2Bool(IntPtr service, uint level)
    {
        IntPtr buffer = Marshal.AllocHGlobal(16);
        try { return QueryServiceConfig2W(service, level, buffer, 16, out _) && Marshal.ReadInt32(buffer) != 0; }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    [LibraryImport("advapi32", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial IntPtr OpenSCManagerW(string? machine, string? database, uint access);

    [LibraryImport("advapi32", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial IntPtr OpenServiceW(IntPtr scm, string name, uint access);

    [LibraryImport("advapi32", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseServiceHandle(IntPtr handle);

    [LibraryImport("advapi32", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool EnumServicesStatusExW(IntPtr scm, uint level, uint type, uint state, IntPtr buffer, uint size,
        out uint needed, out uint count, ref uint resume, string? group);

    [LibraryImport("advapi32", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool QueryServiceConfigW(IntPtr service, IntPtr config, uint size, out uint needed);

    [LibraryImport("advapi32", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool QueryServiceConfig2W(IntPtr service, uint level, IntPtr buffer, uint size, out uint needed);

    [LibraryImport("advapi32", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool QueryServiceStatusEx(IntPtr service, uint level, IntPtr buffer, uint size, out uint needed);

    [LibraryImport("advapi32", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool StartServiceW(IntPtr service, uint argc, IntPtr argv);

    [LibraryImport("advapi32", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ControlService(IntPtr service, uint control, uint[] status);

    [LibraryImport("advapi32", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ChangeServiceConfigW(IntPtr service, uint type, uint start, uint errorControl, string? binaryPath,
        string? loadOrderGroup, IntPtr tagId, string? dependencies, string? startName, string? password, string? displayName);
}
