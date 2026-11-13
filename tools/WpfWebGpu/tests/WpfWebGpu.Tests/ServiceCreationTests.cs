using System.Reflection;
using WpfWebGpu.Services;
using Xunit;

namespace WpfWebGpu.Tests;

// Creating and deleting services (WpfWebGpu.Services/ServiceCreation.cs, advapi32's CreateService and DeleteService):
// a command line read as the SCM reads it, creating refused to all but administrators, a deleted service still opened
// and removed once it has stopped and its last handle is closed, and the unit a created service runs as.
[Collection(ServiceDatabaseEnvironment.Name)]
public class ServiceCreationTests
{
    [Fact]
    public void A_command_line_is_read_as_the_SCM_reads_it()
    {
        string dir = TestEnvironment.NewDirectory("cmdline");
        string spaced = Path.Combine(dir, "Program Files", "A B");
        Directory.CreateDirectory(spaced);
        string exe = Path.Combine(spaced, "svc.exe");
        File.WriteAllText(exe, "");
        string Disk(string p) => p.Replace('\\', '/');

        // quoted, then CommandLineToArgvW's arguments
        var (program, args) = ServiceManager.SplitCommandLine($"\"{exe}\" -s \"two words\" a\\\\\"b c\" \\\"q", Disk);
        Assert.Equal(exe, program);
        Assert.Equal(new[] { "-s", "two words", "a\\b c", "\"q" }, args);

        // unquoted with spaces in its folders: the run of words that names the file, as Windows finds it
        (program, args) = ServiceManager.SplitCommandLine($"{exe} /run now", Disk);
        Assert.Equal(exe, program);
        Assert.Equal(new[] { "/run", "now" }, args);

        // named without .exe
        (program, _) = ServiceManager.SplitCommandLine($"\"{exe[..^4]}\"", Disk);
        Assert.Equal(exe, program);

        // nothing there: the first word is the program
        (program, args) = ServiceManager.SplitCommandLine("/no/such/thing.exe -x", Disk);
        Assert.Equal("/no/such/thing.exe", program);
        Assert.Equal(new[] { "-x" }, args);
    }

    [Fact]
    public void Only_an_administrator_creates_services()
    {
        var user = WindowsAccounts.TokenOf(501, "someone");
        var networkService = WindowsAccounts.TokenOf(501, "_wpfnetworkservice");
        var system = WindowsAccounts.TokenOf(0);
        Assert.True(WindowsAccounts.AccessAllowed(ServiceDatabase.ManagerDescriptor, user, ServiceManager.ManagerConnect | ServiceManager.ManagerEnumerate));
        Assert.False(WindowsAccounts.AccessAllowed(ServiceDatabase.ManagerDescriptor, user, ServiceManager.ManagerCreateService));
        Assert.False(WindowsAccounts.AccessAllowed(ServiceDatabase.ManagerDescriptor, networkService, ServiceManager.ManagerCreateService));
        Assert.True(WindowsAccounts.AccessAllowed(ServiceDatabase.ManagerDescriptor, system, ServiceManager.ManagerCreateService));
    }

    [Fact]
    public async Task The_daemon_creates_for_an_administrator_only_and_removes_a_deleted_service_once_stopped()
    {
        if (OperatingSystem.IsWindows() || WindowsAccounts.CurrentUid() == 0) return;
        string dir = TestEnvironment.NewDirectory("scm-create");
        string path = Path.Combine(dir, ServiceDatabase.FileName);
        new ServiceDatabase
        {
            Host = new ServiceHost { Root = dir, State = dir, UnitPrefix = "test.wpfwebgpu." },
            Services = { new ServiceRecord { Name = "Going", Unit = "test.wpfwebgpu.going", MarkedForDelete = true, Security = AllToEveryone() } },
        }.Save(path);
        using var stop = new CancellationTokenSource();
        var serving = ServiceBroker.Serve(path, _ => { }, stop.Token);
        for (int i = 0; i < 100 && !File.Exists(ServiceBroker.SocketPath(path)); i++) await Task.Delay(20);
        try
        {
            var request = new ServiceCreateRequest { Service = new ServiceRecord { Name = "New", PathName = "/x/New.exe" }, Program = "/x/New.exe" };
            var denied = Assert.Throws<ServiceException>(() => ServiceBroker.Request(path, "create", "New", request.Encode()));
            Assert.Equal(ServiceManager.ERROR_ACCESS_DENIED, denied.Error);
            Assert.Null(ServiceDatabase.Load(path).Find("New"));
            // a service deleted while it ran is gone once it has stopped (this one runs no more)
            var gone = Assert.Throws<ServiceException>(() => ServiceBroker.Request(path, "start", "Going", ""));
            Assert.Equal(ServiceManager.ERROR_SERVICE_DOES_NOT_EXIST, gone.Error);
            Assert.Null(ServiceDatabase.Load(path).Find("Going"));
        }
        finally
        {
            stop.Cancel();
            await serving;
        }
    }

    [Fact]
    public async Task A_deleted_service_goes_once_stopped_and_its_last_handle_closed()
    {
        if (OperatingSystem.IsWindows() || WindowsAccounts.CurrentUid() == 0) return;
        string dir = TestEnvironment.NewDirectory("scm-handles");
        string path = Path.Combine(dir, ServiceDatabase.FileName);
        new ServiceDatabase
        {
            Host = new ServiceHost { Root = dir, State = dir, UnitPrefix = "test.wpfwebgpu." },
            Services = { new ServiceRecord { Name = "Held", Unit = "test.wpfwebgpu.held", Security = AllToEveryone() } },
        }.Save(path);
        using var stop = new CancellationTokenSource();
        var serving = ServiceBroker.Serve(path, _ => { }, stop.Token);
        for (int i = 0; i < 100 && !File.Exists(ServiceBroker.SocketPath(path)); i++) await Task.Delay(20);
        try
        {
            // this process holds two handles; deleting marks it (it is stopped: it would be gone at once with none)
            ServiceBroker.Request(path, "open", "Held", "");
            ServiceBroker.Request(path, "open", "Held", "");
            ServiceBroker.Request(path, "delete", "Held", "");
            Assert.True(ServiceDatabase.Load(path).Find("Held")?.MarkedForDelete);
            // as the SCM answers a deleted service: starting it as a disabled one, deleting it again as marked
            Assert.Equal(ServiceManager.ERROR_SERVICE_DISABLED, Assert.Throws<ServiceException>(() => ServiceBroker.Request(path, "start", "Held", "")).Error);
            Assert.Equal(ServiceManager.ERROR_SERVICE_MARKED_FOR_DELETE, Assert.Throws<ServiceException>(() => ServiceBroker.Request(path, "delete", "Held", "")).Error);
            ServiceBroker.Request(path, "close", "Held", "");
            Assert.NotNull(ServiceDatabase.Load(path).Find("Held"));
            // the last handle closed: gone
            ServiceBroker.Request(path, "close", "Held", "");
            Assert.Null(ServiceDatabase.Load(path).Find("Held"));
        }
        finally
        {
            stop.Cancel();
            await serving;
        }
    }

    [Fact]
    public unsafe void The_stand_ins_refuse_a_user_creating_and_open_a_deleted_service()
    {
        if (OperatingSystem.IsWindows() || WindowsAccounts.CurrentUid() == 0) return;
        string dir = TestEnvironment.NewDirectory("scm-create-exports");
        string path = Path.Combine(dir, ServiceDatabase.FileName);
        new ServiceDatabase { Services = { new ServiceRecord { Name = "Going", Unit = "test.wpfwebgpu.going", MarkedForDelete = true } } }.Save(path);
        string? previous = Environment.GetEnvironmentVariable("WPFWEBGPU_SERVICES");
        Environment.SetEnvironmentVariable("WPFWEBGPU_SERVICES", path);
        try
        {
            nint Call(string name, params object[] args) => Win32Exports.Method(name).Invoke(null, args) switch
            {
                nint handle => handle,
                object value => (nint)Convert.ToInt64(value),
                null => 0,
            };
            object NoString = Pointer.Box(null, typeof(char*));
            // SC_MANAGER_CREATE_SERVICE: an administrator's; SC_MANAGER_ALL_ACCESS the same
            Assert.Equal(0, Call("OpenSCManagerW", NoString, NoString, 0x3u));
            Assert.Equal(0, Call("OpenSCManagerW", NoString, NoString, 0xF003Fu));
            nint scm = Call("OpenSCManagerW", NoString, NoString, 0x1u);
            Assert.NotEqual(0, scm);
            fixed (char* name = "New")
            fixed (char* program = "/x/New.exe")
            {
                // a handle opened to connect only: CreateService is refused for it
                Assert.Equal(0, Call("CreateServiceW", scm, Pointer.Box(name, typeof(char*)), NoString, 0xF01FFu, 0x10u, 3u, 1u,
                    Pointer.Box(program, typeof(char*)), NoString, Pointer.Box(null, typeof(uint*)), NoString, NoString, NoString));
            }
            // a deleted service still there is opened, as the SCM opens one
            nint opened;
            fixed (char* going = "Going")
                opened = Call("OpenServiceW", scm, Pointer.Box(going, typeof(char*)), 0x4u);
            Assert.NotEqual(0, opened);
            Assert.Equal(1, Call("CloseServiceHandle", opened));
            Assert.Equal(1, Call("CloseServiceHandle", scm));
        }
        finally
        {
            Environment.SetEnvironmentVariable("WPFWEBGPU_SERVICES", previous);
        }
    }

    [Fact]
    public void A_created_service_runs_its_program_as_the_package_runs_its_own()
    {
        var host = new ServiceHost { Root = "/opt/P", State = "/var/opt/P/state", Cache = "/var/cache/p", Logs = "/var/log/p", Product = "P", UnitPrefix = "p-" };
        Assert.Equal("p-my-svc.service", ServiceUnits.UnitOf(host, "My-Svc!", mac: false));
        Assert.Equal("com.example.p.my-svc", ServiceUnits.UnitOf(new ServiceHost { UnitPrefix = "com.example.p." }, "My-Svc!", mac: true));
        var windows = new ServiceDefinition { Unit = "p-a.service", Title = "A", Program = "/opt/P/app/A.exe", WorkingDirectory = "/opt/P/app", LogName = "A" };
        Assert.Contains("ExecStart=/opt/P/runtime/run /opt/P/app/A.exe\n", ServiceUnits.Systemd(host, windows));
        var native = new ServiceDefinition { Unit = "p-b.service", Title = "B", Program = "/usr/bin/b", Args = { "-x" }, WorkingDirectory = "/", LogName = "b" };
        Assert.Contains("ExecStart=/usr/bin/b -x\n", ServiceUnits.Systemd(host, native));
        Assert.DoesNotContain("runtime/run", ServiceUnits.Launchd(host, native));
        Assert.Equal("_wpfnetworkservice", ServiceUnits.SystemAccount(@"NT AUTHORITY\NetworkService", mac: true));
        Assert.Equal("wpf-localservice", ServiceUnits.SystemAccount(@"NT AUTHORITY\LOCAL SERVICE", mac: false));
        Assert.Null(ServiceUnits.SystemAccount("LocalSystem", mac: false));
        Assert.Equal("someone", ServiceUnits.SystemAccount(@".\someone", mac: false));
    }

    // a descriptor granting everyone every right: what the daemon answers is the deletion's, not the descriptor's
    private static string AllToEveryone()
    {
        var ace = new byte[20];
        BitConverter.GetBytes((ushort)20).CopyTo(ace, 2); BitConverter.GetBytes(0x000F01FFu).CopyTo(ace, 4);
        ace[8] = 1; ace[9] = 1; ace[15] = 1;                     // S-1-1-0
        var sd = new byte[20 + 8 + 20];
        sd[0] = 1; BitConverter.GetBytes((ushort)0x8004).CopyTo(sd, 2); BitConverter.GetBytes(20).CopyTo(sd, 16);
        sd[20] = 2; BitConverter.GetBytes((ushort)28).CopyTo(sd, 22); BitConverter.GetBytes((ushort)1).CopyTo(sd, 24);
        ace.CopyTo(sd, 28);
        return Convert.ToBase64String(sd);
    }
}
