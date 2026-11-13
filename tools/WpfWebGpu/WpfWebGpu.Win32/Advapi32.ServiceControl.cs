// advapi32, the service control manager's client side: what an app's service tools call to see and manage the
// machine's services -- OpenSCManager, OpenService, QueryServiceStatus(Ex), QueryServiceConfig, StartService,
// ControlService (stop, interrogate), ChangeServiceConfig's start type, CreateService and DeleteService. Answered by
// the stack's service manager
// (WpfWebGpu.Services): the installed services, their state from launchd or systemd, and every request decided by
// the service's security descriptor and the caller's account, as the SCM decides -- OpenService refuses the access
// a descriptor does not grant, and starting or stopping is done by root or the service manager's daemon. A service a
// program creates (an elevated one: the SCM lets administrators alone) is written as the package writes those it
// installs, a launchd daemon or a systemd unit running its program on the installation's runtime. The handles to a
// service are counted by the daemon, for a deleted one goes once it has stopped and the last is closed.

using WpfWebGpu.Services;

namespace WpfWebGpu.Win32;

public static unsafe partial class Exports
{
    private const uint SERVICE_WIN32_OWN_PROCESS = 0x10, SERVICE_NO_CHANGE = 0xFFFFFFFF;
    private const uint SERVICE_CONTROL_INTERROGATE = 4;
    private const uint SERVICE_ACCEPT_STOP = 1, SERVICE_ACCEPT_SHUTDOWN = 4;
    private const int RPC_S_SERVER_UNAVAILABLE = 1722, ERROR_INVALID_LEVEL = 124,
        ERROR_SERVICE_DOES_NOT_EXIST = 1060, ERROR_INVALID_SERVICE_CONTROL = 1052, ERROR_CALL_NOT_IMPLEMENTED_SCM = 120;

    private sealed record ScmHandle(uint Access);
    private sealed record ServiceHandle(string Name, uint Access);

    [Win32Export("OpenSCManagerW")]
    public static nint OpenSCManagerW(char* machine, char* database, uint access) => OpenScm(ReadUtf16(machine), access);

    [Win32Export("OpenSCManagerA", "OpenSCManager")]
    public static nint OpenSCManagerA(byte* machine, byte* database, uint access) => OpenScm(ReadEither(machine), access);

    private static nint OpenScm(string? machine, uint access)
    {
        // this machine's only: another's SCM is reached over RPC, and there is none to reach
        if (!string.IsNullOrEmpty(machine) && !machine.TrimStart('\\').Equals(Environment.MachineName, StringComparison.OrdinalIgnoreCase)
            && machine.TrimStart('\\') != "." && !machine.TrimStart('\\').Equals("localhost", StringComparison.OrdinalIgnoreCase))
        {
            SetLastError(RPC_S_SERVER_UNAVAILABLE);
            return 0;
        }
        // the access asked for, against the SCM's own descriptor: creating services, locking it are administrators'
        uint specific = MapManagerAccess(access);
        if (!OperatingSystem.IsWindows() && !WindowsAccounts.AccessAllowed(ServiceDatabase.ManagerDescriptor, WindowsAccounts.TokenOf(WindowsAccounts.CurrentUid()), specific))
        {
            SetLastError(ServiceManager.ERROR_ACCESS_DENIED);
            return 0;
        }
        return NewHandle(new ScmHandle(specific));
    }

    // GENERIC_* to the SCM's rights (SC_MANAGER_ALL_ACCESS is 0xF003F); SC_MANAGER_CONNECT is implied
    private static uint MapManagerAccess(uint access)
    {
        uint mapped = (access & 0x0001FFFF) | ServiceManager.ManagerConnect;
        if ((access & 0x80000000) != 0) mapped |= 0x20014;     // GENERIC_READ: ENUMERATE_SERVICE, QUERY_LOCK_STATUS, READ_CONTROL
        if ((access & 0x40000000) != 0) mapped |= 0x20022;     // GENERIC_WRITE: CREATE_SERVICE, MODIFY_BOOT_CONFIG
        if ((access & 0x20000000) != 0) mapped |= 0x20009;     // GENERIC_EXECUTE: CONNECT, LOCK
        if ((access & 0x10000000) != 0) mapped |= 0xF003F;     // GENERIC_ALL
        return mapped;
    }

    [Win32Export("OpenServiceW")]
    public static nint OpenServiceW(nint scm, char* name, uint access) => OpenServiceCore(scm, ReadUtf16(name), access);

    [Win32Export("OpenServiceA", "OpenService")]
    public static nint OpenServiceA(nint scm, byte* name, uint access) => OpenServiceCore(scm, ReadEither(name), access);

    private static nint OpenServiceCore(nint scm, string? name, uint access)
    {
        if (Handle(scm) is not ScmHandle) { SetLastError(ERROR_INVALID_HANDLE); return 0; }
        if (string.IsNullOrEmpty(name)) { SetLastError(ERROR_INVALID_PARAMETER); return 0; }
        ServiceRecord? service = ServiceManager.Database().Find(name);
        if (service == null || !service.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
        {
            SetLastError(ERROR_SERVICE_DOES_NOT_EXIST);    // OpenService takes the key name, not the display name
            return 0;
        }
        // a deleted service is opened while it is still there (stopped and its last handle closed, it is gone), as
        // the SCM opens one
        // the access asked for, against the service's descriptor (generic and standard rights mapped as the SCM maps them)
        uint specific = MapServiceAccess(access);
        if (!OperatingSystem.IsWindows() && !WindowsAccounts.AccessAllowed(ServiceDatabase.DescriptorOf(service), WindowsAccounts.TokenOf(WindowsAccounts.CurrentUid()), specific))
        {
            SetLastError(ServiceManager.ERROR_ACCESS_DENIED);
            return 0;
        }
        return OpenedHandle(service.Name, specific);
    }

    // a handle the service manager counts, as the SCM counts them: a deleted service goes once the last is closed
    private static nint OpenedHandle(string name, uint access)
    {
        ServiceManager.Holding(name, open: true);
        return NewHandle(new ServiceHandle(name, access));
    }

    // GENERIC_READ/WRITE/EXECUTE/ALL to the service rights they stand for (SERVICE_ALL_ACCESS is 0xF01FF)
    private static uint MapServiceAccess(uint access)
    {
        uint mapped = access & 0x0001FFFF;
        if ((access & 0x80000000) != 0) mapped |= 0x2008D;     // GENERIC_READ: QUERY_CONFIG, QUERY_STATUS, ENUMERATE_DEPENDENTS, INTERROGATE, USER_DEFINED_CONTROL... as READ_CONTROL
        if ((access & 0x40000000) != 0) mapped |= 0x20002;     // GENERIC_WRITE: CHANGE_CONFIG
        if ((access & 0x20000000) != 0) mapped |= 0x20170;     // GENERIC_EXECUTE: START, STOP, PAUSE_CONTINUE, USER_DEFINED_CONTROL
        if ((access & 0x10000000) != 0) mapped |= 0xF01FF;     // GENERIC_ALL
        return mapped;
    }

    [Win32Export("CloseServiceHandle")]
    public static int CloseServiceHandle(nint handle)
    {
        object? h = Handle(handle);
        if (h is not (ScmHandle or ServiceHandle)) return Fail(ERROR_INVALID_HANDLE);
        int closed = CloseHandle(handle);
        if (h is ServiceHandle service) ServiceManager.Holding(service.Name, open: false);
        return closed;
    }

    private static int Fail(int error) { SetLastError(error); return FALSE; }

    private static ServiceHandle? ServiceOf(nint handle, uint right)
    {
        if (Handle(handle) is not ServiceHandle h) { SetLastError(ERROR_INVALID_HANDLE); return null; }
        if ((h.Access & right) != right) { SetLastError(ServiceManager.ERROR_ACCESS_DENIED); return null; }
        return h;
    }

    // SERVICE_STATUS: type, current state, controls accepted, exit code, service exit code, checkpoint, wait hint
    private static void WriteStatus(uint* status, ServiceStatus s)
    {
        status[0] = SERVICE_WIN32_OWN_PROCESS;
        status[1] = (uint)s.State;
        status[2] = s.State == ServiceState.Running ? SERVICE_ACCEPT_STOP | SERVICE_ACCEPT_SHUTDOWN : 0;
        status[3] = s.ExitCode == 0 ? 0u : 1066u;                 // ERROR_SERVICE_SPECIFIC_ERROR where it exited with a code
        status[4] = (uint)s.ExitCode;
        status[5] = 0;
        status[6] = 0;
    }

    private static ServiceStatus? StatusOf(ServiceHandle h)
    {
        ServiceDatabase db = ServiceManager.Database();
        return db.Find(h.Name) is { } service ? ServiceManager.StatusOf(db, service) : null;
    }

    [Win32Export("QueryServiceStatus")]
    public static int QueryServiceStatus(nint handle, uint* status)
    {
        if (ServiceOf(handle, ServiceManager.QueryStatus) is not { } h) return FALSE;
        if (StatusOf(h) is not { } s) return Fail(ERROR_SERVICE_DOES_NOT_EXIST);
        WriteStatus(status, s);
        return TRUE;
    }

    [Win32Export("QueryServiceStatusEx")]
    public static int QueryServiceStatusEx(nint handle, int level, byte* buffer, uint size, uint* needed)
    {
        if (level != 0) return Fail(ERROR_INVALID_LEVEL);         // SC_STATUS_PROCESS_INFO
        if (needed != null) *needed = 36;
        if (size < 36) return Fail(ERROR_INSUFFICIENT_BUFFER);
        if (ServiceOf(handle, ServiceManager.QueryStatus) is not { } h) return FALSE;
        if (StatusOf(h) is not { } s) return Fail(ERROR_SERVICE_DOES_NOT_EXIST);
        // SERVICE_STATUS_PROCESS: SERVICE_STATUS, then process id and flags
        WriteStatus((uint*)buffer, s);
        ((uint*)buffer)[7] = (uint)s.ProcessId;
        ((uint*)buffer)[8] = 0;
        return TRUE;
    }

    [Win32Export("StartServiceW")]
    public static int StartServiceW(nint handle, uint argc, char** argv) => StartServiceCore(handle);

    [Win32Export("StartServiceA", "StartService")]
    public static int StartServiceA(nint handle, uint argc, byte** argv) => StartServiceCore(handle);

    private static int StartServiceCore(nint handle)
    {
        if (ServiceOf(handle, ServiceManager.StartRight) is not { } h) return FALSE;
        return ServiceRequest(() => ServiceManager.Start(h.Name));
    }

    [Win32Export("ControlService")]
    public static int ControlService(nint handle, uint control, uint* status)
    {
        switch (control)
        {
            case SERVICE_CONTROL_STOP:
            {
                if (ServiceOf(handle, ServiceManager.StopRight) is not { } h) return FALSE;
                if (ServiceRequest(() => ServiceManager.Stop(h.Name)) == FALSE) return FALSE;
                if (status != null && StatusOf(h) is { } s) WriteStatus(status, s);
                return TRUE;
            }
            case SERVICE_CONTROL_INTERROGATE:
            {
                if (ServiceOf(handle, 0x80) is not { } h) return FALSE;      // SERVICE_INTERROGATE
                if (StatusOf(h) is not { } s) return Fail(ERROR_SERVICE_DOES_NOT_EXIST);
                if (s.State != ServiceState.Running) return Fail(ServiceManager.ERROR_SERVICE_NOT_ACTIVE);
                if (status != null) WriteStatus(status, s);
                return TRUE;
            }
            default:
                return Fail(ERROR_INVALID_SERVICE_CONTROL);
        }
    }

    [Win32Export("ChangeServiceConfigW")]
    public static int ChangeServiceConfigW(nint handle, uint type, uint start, uint errorControl, char* path, char* group, uint* tag,
        char* dependencies, char* account, char* password, char* displayName) =>
        ChangeServiceConfigCore(handle, start, path != null || group != null || tag != null || dependencies != null || account != null || password != null || displayName != null,
            type != SERVICE_NO_CHANGE || errorControl != SERVICE_NO_CHANGE);

    [Win32Export("ChangeServiceConfigA", "ChangeServiceConfig")]
    public static int ChangeServiceConfigA(nint handle, uint type, uint start, uint errorControl, byte* path, byte* group, uint* tag,
        byte* dependencies, byte* account, byte* password, byte* displayName) =>
        ChangeServiceConfigCore(handle, start, path != null || group != null || tag != null || dependencies != null || account != null || password != null || displayName != null,
            type != SERVICE_NO_CHANGE || errorControl != SERVICE_NO_CHANGE);

    private static int ChangeServiceConfigCore(nint handle, uint start, bool otherStrings, bool otherValues)
    {
        if (ServiceOf(handle, ServiceManager.ChangeConfig) is not { } h) return FALSE;
        // a deleted service is changed no more, whatever is asked
        if (ServiceManager.Database().Find(h.Name) is { MarkedForDelete: true }) return Fail(ServiceManager.ERROR_SERVICE_MARKED_FOR_DELETE);
        // the start type is the service manager's to change; the rest is how the installer set the service up
        if (otherStrings || otherValues)
        {
            LogOnce($"scm:config:{h.Name}", $"win32 shim: advapi32!ChangeServiceConfig changes only a service's start type here ({h.Name})");
            return Fail(ERROR_CALL_NOT_IMPLEMENTED_SCM);
        }
        if (start == SERVICE_NO_CHANGE) return TRUE;
        ServiceStart? to = start switch { 2 => ServiceStart.Auto, 3 => ServiceStart.Manual, 4 => ServiceStart.Disabled, _ => null };
        if (to == null) return Fail(ERROR_INVALID_PARAMETER);
        return ServiceRequest(() => ServiceManager.SetStart(h.Name, to.Value));
    }

    [Win32Export("QueryServiceConfigW")]
    public static int QueryServiceConfigW(nint handle, byte* buffer, uint size, uint* needed)
    {
        if (ServiceOf(handle, ServiceManager.QueryConfig) is not { } h) return FALSE;
        if (ServiceManager.Database().Find(h.Name) is not { } service) return Fail(ERROR_SERVICE_DOES_NOT_EXIST);

        // QUERY_SERVICE_CONFIGW: type, start type, error control, binary path, load order group, tag, dependencies (a
        // double-terminated list), start name, display name -- the strings in the buffer after the structure
        int p = sizeof(nint);
        int fixedSize = p == 8 ? 64 : 36;
        string dependencies = string.Concat(service.DependsOn.Select(d => d + "\0")) + "\0";
        string[] strings = { service.PathName + "\0", "\0", dependencies, service.StartName + "\0", service.DisplayName + "\0" };
        uint total = (uint)(fixedSize + strings.Sum(s => s.Length * 2));
        if (needed != null) *needed = total;
        if (buffer == null || size < total) return Fail(ERROR_INSUFFICIENT_BUFFER);

        byte* at = buffer + fixedSize;
        nint Put(string s)
        {
            nint here = (nint)at;
            s.AsSpan().CopyTo(new Span<char>(at, s.Length));
            at += s.Length * 2;
            return here;
        }
        *(uint*)buffer = SERVICE_WIN32_OWN_PROCESS;
        *(uint*)(buffer + 4) = service.Start switch { ServiceStart.Auto => 2u, ServiceStart.Disabled => 4u, _ => 3u };
        *(uint*)(buffer + 8) = 1;                                   // SERVICE_ERROR_NORMAL
        if (p == 8)
        {
            *(nint*)(buffer + 16) = Put(strings[0]);
            *(nint*)(buffer + 24) = Put(strings[1]);
            *(uint*)(buffer + 32) = 0;
            *(nint*)(buffer + 40) = Put(strings[2]);
            *(nint*)(buffer + 48) = Put(strings[3]);
            *(nint*)(buffer + 56) = Put(strings[4]);
        }
        else
        {
            *(nint*)(buffer + 12) = Put(strings[0]);
            *(nint*)(buffer + 16) = Put(strings[1]);
            *(uint*)(buffer + 20) = 0;
            *(nint*)(buffer + 24) = Put(strings[2]);
            *(nint*)(buffer + 28) = Put(strings[3]);
            *(nint*)(buffer + 32) = Put(strings[4]);
        }
        return TRUE;
    }

    [Win32Export("CreateServiceW")]
    public static nint CreateServiceW(nint scm, char* name, char* displayName, uint access, uint type, uint start, uint errorControl,
        char* path, char* group, uint* tag, char* dependencies, char* account, char* password) =>
        CreateServiceCore(scm, ReadUtf16(name), ReadUtf16(displayName), access, type, start, ReadUtf16(path), tag,
            MultiUtf16(dependencies), ReadUtf16(account));

    [Win32Export("CreateServiceA", "CreateService")]
    public static nint CreateServiceA(nint scm, byte* name, byte* displayName, uint access, uint type, uint start, uint errorControl,
        byte* path, byte* group, uint* tag, byte* dependencies, byte* account, byte* password)
    {
        // the undecorated name takes either encoding, as each string shows (ReadEither)
        bool wide = name != null && name[0] != 0 && name[1] == 0;
        return CreateServiceCore(scm, ReadEither(name), ReadEither(displayName), access, type, start, ReadEither(path), tag,
            wide ? MultiUtf16((char*)dependencies) : MultiUtf8(dependencies), ReadEither(account));
    }

    private const uint SERVICE_WIN32_SHARE_PROCESS = 0x20, SERVICE_INTERACTIVE_PROCESS = 0x100;

    private static nint CreateServiceCore(nint scm, string? name, string? displayName, uint access, uint type, uint start,
        string? path, uint* tag, List<string> dependencies, string? account)
    {
        if (Handle(scm) is not ScmHandle h) { SetLastError(ERROR_INVALID_HANDLE); return 0; }
        if ((h.Access & ServiceManager.ManagerCreateService) == 0) { SetLastError(ServiceManager.ERROR_ACCESS_DENIED); return 0; }
        if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(path)) { SetLastError(ERROR_INVALID_PARAMETER); return 0; }
        if (tag != null) { SetLastError(ERROR_INVALID_PARAMETER); return 0; }     // a tag is a driver's, in a load order group
        // a process of its own, or sharing one (each runs in its own here, as a shared one would on its own); a
        // driver is the kernel's, and there is no Windows kernel to load one into
        if ((type & ~SERVICE_INTERACTIVE_PROCESS) is not (SERVICE_WIN32_OWN_PROCESS or SERVICE_WIN32_SHARE_PROCESS))
        {
            LogOnce($"scm:create:{name}", $"win32 shim: advapi32!CreateService of a driver ({name}, type 0x{type:X}) is not supported here");
            SetLastError(50);                                                            // ERROR_NOT_SUPPORTED
            return 0;
        }
        ServiceStart? to = start switch { 2 => ServiceStart.Auto, 3 => ServiceStart.Manual, 4 => ServiceStart.Disabled, _ => null };
        if (to == null) { SetLastError(ERROR_INVALID_PARAMETER); return 0; }       // boot and system start are drivers'

        var (program, args) = ServiceManager.SplitCommandLine(path, p => WpfWebGpu.ComInterop.WindowsPaths.Disk(p)!);
        var request = new ServiceCreateRequest
        {
            Service = new ServiceRecord
            {
                Name = name,
                DisplayName = string.IsNullOrEmpty(displayName) ? name : displayName,
                PathName = path,
                Start = to.Value,
                StartName = string.IsNullOrEmpty(account) ? "LocalSystem" : account,
                DependsOn = dependencies,
            },
            Program = program,
            Args = args,
        };
        if (ServiceRequest(() => ServiceManager.Create(request)) == FALSE) return 0;
        // the creator gets the access it asked for (its own service, as an administrator)
        return OpenedHandle(name, MapServiceAccess(access));
    }

    [Win32Export("DeleteService")]
    public static int DeleteService(nint handle)
    {
        if (ServiceOf(handle, ServiceManager.DeleteRight) is not { } h) return FALSE;
        return ServiceRequest(() => ServiceManager.Delete(h.Name));
    }

    // a double-terminated list of strings (dependencies: "A\0B\0\0")
    private static List<string> MultiUtf16(char* p)
    {
        var list = new List<string>();
        if (p == null) return list;
        while (*p != 0)
        {
            string s = new(p);
            list.Add(s);
            p += s.Length + 1;
        }
        return list;
    }

    private static List<string> MultiUtf8(byte* p)
    {
        var list = new List<string>();
        if (p == null) return list;
        while (*p != 0)
        {
            int n = 0;
            while (p[n] != 0) n++;
            list.Add(System.Text.Encoding.UTF8.GetString(p, n));
            p += n + 1;
        }
        return list;
    }

    // a service manager request, its refusal as Windows' error
    private static int ServiceRequest(Action action)
    {
        try { action(); return TRUE; }
        catch (ServiceException e) { return Fail(e.Error); }
    }
}
