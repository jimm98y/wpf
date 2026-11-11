extern alias PrincipalShim;

using System.Reflection;
using Xunit;

namespace WpfWebGpu.Tests;

// advapi32's accounts, SIDs and account rights in the stack's user-mode Windows (WpfWebGpu.Win32/Advapi32.Accounts.cs),
// over the Principal stand-in's accounts.
public unsafe class AccountExportTests
{
    private static MethodInfo Export(string name)
    {
        // the stand-in, loaded where the exports find it (the framework's own refuses off Windows)
        _ = typeof(PrincipalShim::System.Security.Principal.NTAccount).Assembly;
        return Win32Exports.Method(name);
    }

    private static int LastError() => (int)(uint)Export("GetLastError").Invoke(null, null)!;

    private static object[] Args(char* name, byte* sid, uint* sidSize, char* domain, uint* domainLength, int* use) => new object[]
    {
        Pointer.Box(null, typeof(char*)), Pointer.Box(name, typeof(char*)), Pointer.Box(sid, typeof(byte*)), Pointer.Box(sidSize, typeof(uint*)),
        Pointer.Box(domain, typeof(char*)), Pointer.Box(domainLength, typeof(uint*)), Pointer.Box(use, typeof(int*)),
    };

    private static (int Result, byte[] Sid, string Domain, int Use) Lookup(string name)
    {
        uint sidSize = 0, domainLength = 0;
        int use = 0;
        fixed (char* n = name)
        {
            // the sizes first: ERROR_INSUFFICIENT_BUFFER, and what is needed
            int first = (int)Export("LookupAccountNameW").Invoke(null, Args(n, null, &sidSize, null, &domainLength, &use))!;
            if (first != 0 || LastError() != 122) return (first, Array.Empty<byte>(), "", 0);
            var sid = new byte[sidSize];
            char* domain = stackalloc char[(int)domainLength];
            fixed (byte* s = sid)
            {
                int r = (int)Export("LookupAccountNameW").Invoke(null, Args(n, s, &sidSize, domain, &domainLength, &use))!;
                return (r, sid, new string(domain), use);
            }
        }
    }

    private static string Text(byte[] sid)
    {
        char* text = null;
        fixed (byte* s = sid) Assert.Equal(1, (int)Export("ConvertSidToStringSidW").Invoke(null, new object[] { Pointer.Box(s, typeof(byte*)), Pointer.Box(&text, typeof(char**)) })!);
        string value = new string(text);
        Export("LocalFree").Invoke(null, new object[] { Pointer.Box(text, typeof(void*)) });
        return value;
    }

    [Fact]
    public void Accounts_have_their_SIDs_and_rights_granted_are_kept()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Windows has its own accounts");
        var network = Lookup("NT AUTHORITY\\NETWORK SERVICE");
        Assert.Equal(1, network.Result);
        Assert.Equal("S-1-5-20", Text(network.Sid));
        Assert.Equal(("NT AUTHORITY", 5), (network.Domain, network.Use));               // SidTypeWellKnownGroup

        var me = Lookup(Environment.UserName);
        Assert.Equal(1, me.Result);
        Assert.StartsWith("S-1-5-21-", Text(me.Sid));
        Assert.Equal(1, me.Use);                                                         // SidTypeUser

        Assert.Equal(0, Lookup("no-such-account-" + Environment.ProcessId).Result);
        Assert.Equal(1332, LastError());                                                 // ERROR_NONE_MAPPED

        // "log on as a service" for NETWORK SERVICE: granted, seen, taken back
        nint policy = 0;
        Assert.Equal(0u, (uint)Export("LsaOpenPolicy").Invoke(null, new object[] { Pointer.Box(null, typeof(void*)), Pointer.Box(null, typeof(void*)), 0x800u, Pointer.Box(&policy, typeof(nint*)) })!);
        fixed (byte* sid = network.Sid)
        fixed (char* right = "SeServiceLogonRight")
        {
            byte* unicode = stackalloc byte[16];
            *(ushort*)unicode = 38;
            *(ushort*)(unicode + 2) = 40;
            *(char**)(unicode + (IntPtr.Size == 8 ? 8 : 4)) = right;
            Assert.Equal(0u, (uint)Export("LsaAddAccountRights").Invoke(null, new object[] { policy, Pointer.Box(sid, typeof(byte*)), Pointer.Box(unicode, typeof(void*)), 1u })!);

            void* rights = null;
            uint count = 0;
            Assert.Equal(0u, (uint)Export("LsaEnumerateAccountRights").Invoke(null, new object[] { policy, Pointer.Box(sid, typeof(byte*)), Pointer.Box(&rights, typeof(void**)), Pointer.Box(&count, typeof(uint*)) })!);
            Assert.Equal(1u, count);
            Assert.Equal("SeServiceLogonRight", new string(*(char**)((byte*)rights + (IntPtr.Size == 8 ? 8 : 4)), 0, *(ushort*)rights / 2));
            Export("LsaFreeMemory").Invoke(null, new object[] { Pointer.Box(rights, typeof(void*)) });

            Assert.Equal(0u, (uint)Export("LsaRemoveAccountRights").Invoke(null, new object[] { policy, Pointer.Box(sid, typeof(byte*)), (byte)1, Pointer.Box(null, typeof(void*)), 0u })!);
            Assert.Equal(0xC0000034u, (uint)Export("LsaEnumerateAccountRights").Invoke(null, new object[] { policy, Pointer.Box(sid, typeof(byte*)), Pointer.Box(&rights, typeof(void**)), Pointer.Box(&count, typeof(uint*)) })!);
        }
        Assert.Equal(0u, (uint)Export("LsaClose").Invoke(null, new object[] { policy })!);
        Assert.Equal(2u, (uint)Export("LsaNtStatusToWinError").Invoke(null, new object[] { 0xC0000034u })!);
    }
}
