using System.Reflection;
using WpfWebGpu.Services;
using Xunit;

namespace WpfWebGpu.Tests;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ServiceDatabaseEnvironment { public const string Name = "the machine's service database (WPFWEBGPU_SERVICES)"; }

// advapi32's service control manager client (WpfWebGpu.Win32/Advapi32.ServiceControl.cs), over the stack's service
// manager: a service opened by its key name for the access its descriptor grants, its status and configuration read.
[Collection(ServiceDatabaseEnvironment.Name)]
public unsafe class ServiceControlExportTests
{
    private static MethodInfo Export(string name) => Win32Exports.Method(name);

    private static nint Call(string name, params object[] args) => Export(name).Invoke(null, args) switch
    {
        nint handle => handle,
        object value => (nint)Convert.ToInt64(value),
        null => 0,
    };

    [Fact]
    public void A_service_opens_by_its_name_for_what_its_descriptor_grants_and_reports_its_status_and_configuration()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Windows has its own service control manager");
        Assert.SkipWhen(WindowsAccounts.CurrentUid() == 0, "root may do anything a descriptor names");
        string dir = TestEnvironment.NewDirectory("scm");
        string path = Path.Combine(dir, ServiceDatabase.FileName);
        new ServiceDatabase
        {
            Services =
            {
                new ServiceRecord { Name = "Core", DisplayName = "Product Core", PathName = "C:\\Product\\Core.exe", Start = ServiceStart.Auto,
                    StartName = "NT AUTHORITY\\NetworkService", DependsOn = { "Base" }, Unit = "test.wpfwebgpu.core" },
            },
        }.Save(path);
        string? previous = Environment.GetEnvironmentVariable("WPFWEBGPU_SERVICES");
        Environment.SetEnvironmentVariable("WPFWEBGPU_SERVICES", path);
        try
        {
            nint scm;
            scm = Call("OpenSCManagerW", Pointer.Box(null, typeof(char*)), Pointer.Box(null, typeof(char*)), 0x1u);
            Assert.NotEqual(0, scm);

            nint Open(string name, uint access)
            {
                fixed (char* n = name) return Call("OpenServiceW", scm, Pointer.Box(n, typeof(char*)), access);
            }
            // by key name only, and only for what the default descriptor gives a user: query, not start
            Assert.Equal(0, Open("Product Core", 0x4));
            Assert.Equal(0, Open("NoSuchService", 0x4));
            Assert.Equal(0, Open("Core", 0x10));                          // SERVICE_START: access denied
            nint service = Open("core", 0x1 | 0x4);                       // QUERY_CONFIG | QUERY_STATUS, case aside
            Assert.NotEqual(0, service);

            uint* status = stackalloc uint[7];
            Assert.Equal(1, Call("QueryServiceStatus", service, Pointer.Box(status, typeof(uint*))));
            Assert.Equal(1u, status[1]);                                  // SERVICE_STOPPED: no such unit runs

            uint needed = 0;
            Assert.Equal(0, Call("QueryServiceConfigW", service, Pointer.Box(null, typeof(byte*)), 0u, Pointer.Box(&needed, typeof(uint*))));
            var buffer = new byte[needed];
            fixed (byte* b = buffer)
            {
                Assert.Equal(1, Call("QueryServiceConfigW", service, Pointer.Box(b, typeof(byte*)), needed, Pointer.Box(&needed, typeof(uint*))));
                Assert.Equal(2u, *(uint*)(b + 4));                        // SERVICE_AUTO_START
                int p = sizeof(nint);
                byte* block = b;
                string At(int offset) => new string((char*)*(nint*)(block + offset));
                Assert.Equal("C:\\Product\\Core.exe", At(p == 8 ? 16 : 12));
                Assert.Equal("Base", At(p == 8 ? 40 : 24));
                Assert.Equal("NT AUTHORITY\\NetworkService", At(p == 8 ? 48 : 28));
                Assert.Equal("Product Core", At(p == 8 ? 56 : 32));
            }
            // a control the handle was not opened for, and one no service takes
            Assert.Equal(0, Call("ControlService", service, 1u, Pointer.Box(null, typeof(uint*))));
            Assert.Equal(0, Call("ControlService", service, 0x7Fu, Pointer.Box(null, typeof(uint*))));
            Assert.Equal(1, Call("CloseServiceHandle", service));
            Assert.Equal(1, Call("CloseServiceHandle", scm));
        }
        finally
        {
            Environment.SetEnvironmentVariable("WPFWEBGPU_SERVICES", previous);
        }
    }
}
