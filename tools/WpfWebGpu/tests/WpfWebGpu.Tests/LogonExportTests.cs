using System.Reflection;
using Xunit;

namespace WpfWebGpu.Tests;

// advapi32!LogonUser in the stack's user-mode Windows (WpfWebGpu.Win32/Advapi32.Logon.cs): refusals that touch no real
// account. (A good password is checked where the test machine's account is known: WPFWEBGPU_TEST_LOGON=user:password.)
public unsafe class LogonExportTests
{
    private static MethodInfo Export(string name) => Win32Exports.Method(name);

    private static (int Result, int Error, nint Token) Logon(string user, string? domain, string password)
    {
        nint token = 0;
        fixed (char* u = user)
        fixed (char* d = domain)
        fixed (char* p = password)
        {
            int r = (int)Export("LogonUserW").Invoke(null, new object[]
            {
                Pointer.Box(u, typeof(char*)), Pointer.Box(domain == null ? null : d, typeof(char*)), Pointer.Box(p, typeof(char*)),
                2u, 0u, Pointer.Box(&token, typeof(nint*)),
            })!;
            return (r, System.Runtime.InteropServices.Marshal.GetLastPInvokeError(), token);
        }
    }

    private static int LastError() => (int)(uint)Export("GetLastError").Invoke(null, null)!;

    [Fact]
    public void An_unknown_account_or_another_domain_is_refused_and_a_known_one_logs_on()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Windows has LogonUser itself");
        var (r, _, token) = Logon("wpfwebgpu-no-such-user-" + Environment.ProcessId, ".", "x");
        Assert.Equal(0, r);
        Assert.Equal(0, token);
        Assert.Equal(1326, LastError());                     // ERROR_LOGON_FAILURE: as for a wrong password

        Assert.Equal(0, Logon("someone", "CORPORATE", "x").Result);
        Assert.Equal(1311, LastError());                     // ERROR_NO_LOGON_SERVERS: no domain to ask
        Assert.Equal(0, Logon("CORPORATE\\someone", null, "x").Result);
        Assert.Equal(1311, LastError());

        if (Environment.GetEnvironmentVariable("WPFWEBGPU_TEST_LOGON") is { Length: > 0 } known && known.Split(':', 2) is [var user, var password])
        {
            (r, _, token) = Logon(user, Environment.MachineName, password);
            Assert.Equal(1, r);
            Assert.NotEqual(0, token);
            Assert.Equal(1, (int)Export("CloseHandle").Invoke(null, new object[] { token })!);
            Assert.Equal(0, Logon(user + "@" + Environment.MachineName, null, password + "-wrong").Result);
            Assert.Equal(1326, LastError());
        }
    }

    [Fact]
    public void The_machine_is_in_a_workgroup_and_no_domain_controller_answers()
    {
        char* name = null;
        int status = 0;
        Assert.Equal(0, (int)Export("NetGetJoinInformation").Invoke(null, new object[] { Pointer.Box(null, typeof(char*)), Pointer.Box(&name, typeof(char**)), Pointer.Box(&status, typeof(int*)) })!);
        Assert.Equal(("WORKGROUP", 2), (new string(name), status));                     // NetSetupWorkgroupName
        Assert.Equal(0, (int)Export("NetApiBufferFree").Invoke(null, new object[] { Pointer.Box(name, typeof(void*)) })!);

        void* info = (void*)1;
        Assert.Equal(1355, (int)Export("DsGetDcNameW").Invoke(null, new object[]
        {
            Pointer.Box(null, typeof(char*)), Pointer.Box(null, typeof(char*)), Pointer.Box(null, typeof(void*)), Pointer.Box(null, typeof(char*)), 0u,
            Pointer.Box(&info, typeof(void**)),
        })!);                                                                              // ERROR_NO_SUCH_DOMAIN
        Assert.True(info == null);
    }
}
