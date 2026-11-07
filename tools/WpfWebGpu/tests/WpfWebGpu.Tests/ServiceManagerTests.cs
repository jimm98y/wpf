using System.ComponentModel;
using System.Management;
using System.ServiceProcess;
using WpfWebGpu.Packager;
using WpfWebGpu.Services;
using Xunit;

namespace WpfWebGpu.Tests;

/// <summary>The service manager off Windows: services as the registry registers them, the database the packages install,
/// the access check by each service's security descriptor, and the stand-ins answering from it.</summary>
[Collection("service manager")]
public class ServiceManagerTests
{
    // a product's service descriptor as its installer sets it: NETWORK SERVICE and administrators control it, the
    // system nearly so, interactive users and services query it
    private const string FrameworkSd =
        "AQAUgKAAAACsAAAAFAAAADAAAAACABwAAQAAAAKAFAD/AQ8AAQEAAAAAAAEAAAAAAgBwAAUAAAAAAxQA/wEPAAEBAAAAAAAFFAAAAAAAFAD9AQIAAQEAAAAAAAUSAAAAAAAYAP8BDwABAgAAAAAABSAAAAAgAgAAAAAUAI0BAgABAQAAAAAABQQAAAAAABQAjQECAAEBAAAAAAAFBgAAAAEBAAAAAAAFEgAAAAEBAAAAAAAFEgAAAA==";

    [Fact]
    public void Services_are_taken_from_the_registry_s_service_keys()
    {
        string install = TestEnvironment.NewDirectory("svc-install");
        Directory.CreateDirectory(Path.Combine(install, "Services", "Core"));
        // the installation carries the registered Core.exe as Core_ARM64.exe, whose apphost starts Core.dll
        File.WriteAllBytes(Path.Combine(install, "Services", "Core", "Core_ARM64.exe"), System.Text.Encoding.ASCII.GetBytes("MZ\0padding\0Core.dll\0\0\0"));
        File.WriteAllText(Path.Combine(install, "Services", "Core", "Core.dll"), "");
        File.WriteAllText(Path.Combine(install, "Services", "Core", "Core.runtimeconfig.json"), "{}");
        var manifest = new PackageManifest();
        manifest.Apps.Add(new AppEntry { Exe = "Services/Core/Core_ARM64.exe", Name = "Core", Kind = AppKind.Console });
        manifest.Apps.Add(new AppEntry { Exe = "Services/Old/Old.exe", Name = "Old", Kind = AppKind.Console, Warnings = { "32-bit only" } });
        manifest.PathMappings.Add(new PathMapping { From = @"C:\Program Files\P", To = PathTokens.InstallDir });
        const string reg = """
            Windows Registry Editor Version 5.00

            [HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Services\CoreService]
            "ImagePath"=hex(2):22,00,43,00,3a,00,5c,00,50,00,72,00,6f,00,67,00,72,00,61,00,6d,00,20,00,46,00,69,00,6c,00,65,00,73,00,5c,00,50,00,5c,00,53,00,65,00,72,00,76,00,69,00,63,00,65,00,73,00,5c,00,43,00,6f,00,72,00,65,00,5c,00,43,00,6f,00,72,00,65,00,2e,00,65,00,78,00,65,00,22,00,20,00,2d,00,73,00,00,00
            "DisplayName"="Product Core Server"
            "Description"="The core."
            "Start"=dword:00000002
            "DelayedAutostart"=dword:00000001
            "ObjectName"="NT SERVICE\\CoreService"
            "DependOnService"=hex(7):54,00,63,00,70,00,69,00,70,00,00,00,4f,00,6c,00,64,00,53,00,65,00,72,00,76,00,69,00,63,00,65,00,00,00,00,00

            [HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Services\CoreService\Security]
            "Security"=hex:01,00,14,80

            [HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Services\OldService]
            "ImagePath"="C:\\Program Files\\P\\Services\\Old\\Old.exe"
            "Start"=dword:00000003
            "ObjectName"="LocalSystem"

            [HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Services\Spooler]
            "ImagePath"="C:\\Windows\\System32\\spoolsv.exe"
            "Start"=dword:00000002
            """;
        var services = Scanner.RegisteredServices(manifest, RegText.Parse(reg).ToList(), install, null);

        Assert.Equal(new[] { "CoreService", "OldService" }, services.Select(s => s.Name));   // not Windows' own
        var core = services[0];
        Assert.Equal("Services/Core/Core_ARM64.exe", core.Exe);
        Assert.Equal(new[] { "-s" }, core.Args);
        Assert.Equal(("Product Core Server", "The core.", StartType.Auto, true), (core.DisplayName, core.Description, core.Start, core.DelayedAutoStart));
        Assert.Equal((@"NT SERVICE\CoreService", ServiceAccount.NetworkService), (core.StartName, core.Account));
        Assert.Equal(new[] { "OldService" }, core.DependsOn);            // Tcpip is Windows' own
        Assert.Equal("AQAUgA==", core.Security);
        Assert.Equal(StartType.Disabled, services[1].Start);              // 32-bit only: registered, not started
    }

    [Fact]
    public void Who_may_control_a_service_is_its_descriptor_s_answer()
    {
        byte[] sd = Convert.FromBase64String(FrameworkSd);
        uint user = 501;
        var networkService = WindowsAccounts.TokenOf(user, "_wpfnetworkservice");
        var interactive = WindowsAccounts.TokenOf(user, "someone");
        var system = WindowsAccounts.TokenOf(0);

        Assert.True(WindowsAccounts.AccessAllowed(sd, networkService, ServiceManager.StartRight | ServiceManager.StopRight));
        Assert.True(WindowsAccounts.AccessAllowed(sd, system, ServiceManager.StartRight));
        Assert.True(WindowsAccounts.AccessAllowed(sd, interactive, ServiceManager.QueryStatus));
        Assert.False(WindowsAccounts.AccessAllowed(sd, interactive, ServiceManager.StartRight));
        Assert.False(WindowsAccounts.AccessAllowed(sd, interactive, ServiceManager.ChangeConfig));
        // no descriptor of its own: Windows' default, the same for a user
        var plain = new ServiceRecord { Name = "Plain" };
        Assert.False(WindowsAccounts.AccessAllowed(ServiceDatabase.DescriptorOf(plain), interactive, ServiceManager.StartRight));
        Assert.True(WindowsAccounts.AccessAllowed(ServiceDatabase.DescriptorOf(plain), system, ServiceManager.StartRight));
    }

    [Fact]
    public async Task The_daemon_decides_for_the_account_at_the_other_end()
    {
        if (OperatingSystem.IsWindows()) return;
        string dir = TestEnvironment.NewDirectory("scm");
        string path = Path.Combine(dir, ServiceDatabase.FileName);
        new ServiceDatabase { Services = { new ServiceRecord { Name = "Core", DisplayName = "Product Core", Security = FrameworkSd, Unit = "test.core" } } }.Save(path);
        using var stop = new CancellationTokenSource();
        var log = new List<string>();
        var serving = ServiceBroker.Serve(path, l => { lock (log) log.Add(l); }, stop.Token);
        for (int i = 0; i < 100 && !File.Exists(ServiceBroker.SocketPath(path)); i++) await Task.Delay(20);
        try
        {
            // this test runs as a user: an interactive account, which the descriptor lets query and nothing more
            var denied = Assert.Throws<ServiceException>(() => ServiceBroker.Request(path, "start", "Core", ""));
            Assert.Equal(ServiceManager.ERROR_ACCESS_DENIED, denied.Error);
            var missing = Assert.Throws<ServiceException>(() => ServiceBroker.Request(path, "stop", "Nothing", ""));
            Assert.Equal(ServiceManager.ERROR_SERVICE_DOES_NOT_EXIST, missing.Error);
            lock (log) Assert.Contains(log, l => l.Contains("refused (5)") && l.Contains($"uid {WindowsAccounts.CurrentUid()}"));
        }
        finally
        {
            stop.Cancel();
            await serving;
        }
    }

    [Fact]
    public void The_stand_ins_answer_from_the_installed_services()
    {
        if (OperatingSystem.IsWindows()) return;
        string dir = TestEnvironment.NewDirectory("svcdb");
        string path = Path.Combine(dir, ServiceDatabase.FileName);
        new ServiceDatabase
        {
            Services =
            {
                new ServiceRecord { Name = "Core", DisplayName = "Product Core", Description = "The core.", PathName = "\"/opt/p/Core.exe\"", Start = ServiceStart.Auto,
                    DelayedAutoStart = true, StartName = @"NT AUTHORITY\NetworkService", Security = FrameworkSd, Unit = "test.wpfwebgpu.core.notloaded" },
                new ServiceRecord { Name = "Helper", DisplayName = "Product Helper", Start = ServiceStart.Manual, DependsOn = { "Core" }, Unit = "test.wpfwebgpu.helper.notloaded" },
            },
        }.Save(path);
        Environment.SetEnvironmentVariable("WPFWEBGPU_SERVICES", path);
        try
        {
            var wmi = new ManagementObjectSearcher("SELECT * FROM Win32_Service WHERE Name = 'Core'").Get().Cast<ManagementBaseObject>().Single();
            Assert.Equal(("Product Core", "The core.", "Auto", true, "Stopped"), ((string)wmi["DisplayName"], (string)wmi["Description"], (string)wmi["StartMode"], (bool)wmi["DelayedAutoStart"], (string)wmi["State"]));
            Assert.Equal(@"NT AUTHORITY\NetworkService", wmi["StartName"]);
            Assert.Equal(2, new ManagementObjectSearcher("SELECT Name FROM Win32_Service").Get().Count);

            Assert.Equal(new[] { "Core", "Helper" }, ServiceController.GetServices().Select(s => s.ServiceName));
            var core = new ServiceController("Product Core");                 // by its display name too
            Assert.Equal(("Core", ServiceStartMode.Automatic, ServiceControllerStatus.Stopped), (core.ServiceName, core.StartType, core.Status));
            Assert.Equal(new[] { "Helper" }, core.DependentServices.Select(s => s.ServiceName));
            Assert.Equal(new[] { "Core" }, new ServiceController("Helper").ServicesDependedOn.Select(s => s.ServiceName));
            // a user's start: refused (by the daemon, or -- none running here -- the manager is unreachable)
            var e = Assert.Throws<InvalidOperationException>(() => core.Start());
            Assert.Contains(Assert.IsType<Win32Exception>(e.InnerException).NativeErrorCode, new[] { 5, 1722 });
            Assert.Throws<System.ServiceProcess.TimeoutException>(() => core.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromMilliseconds(300)));
            var gone = Assert.Throws<InvalidOperationException>(() => new ServiceController("Spooler").Status);
            Assert.Equal(1060, Assert.IsType<Win32Exception>(gone.InnerException).NativeErrorCode);
        }
        finally { Environment.SetEnvironmentVariable("WPFWEBGPU_SERVICES", null); }
    }
}
