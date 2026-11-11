// netapi32 and the domain queries: the machine is in no domain -- a workgroup's, as a Windows machine that never
// joined one -- so the questions about a domain controller have Windows' answers for that: none found. Buffers the
// Net* functions hand out are the caller's to free with NetApiBufferFree.

using System.Runtime.InteropServices;

namespace WpfWebGpu.Win32;

public static unsafe partial class Exports
{
    private const int ERROR_NO_SUCH_DOMAIN = 1355, NERR_DCNotFound = 2453;
    private const int NetSetupWorkgroupName = 2;

    [Win32Export("DsGetDcNameW", "DsGetDcName", "DsGetDcNameA")]
    public static int DsGetDcNameW(char* computer, char* domain, void* guid, char* site, uint flags, void** info)
    {
        if (info != null) *info = null;
        return ERROR_NO_SUCH_DOMAIN;
    }

    [Win32Export("NetGetDCName")]
    public static int NetGetDCName(char* server, char* domain, char** buffer)
    {
        if (buffer != null) *buffer = null;
        return NERR_DCNotFound;
    }

    [Win32Export("NetGetAnyDCName")]
    public static int NetGetAnyDCName(char* server, char* domain, char** buffer) => NetGetDCName(server, domain, buffer);

    [Win32Export("NetGetJoinInformation")]
    public static int NetGetJoinInformation(char* server, char** name, int* status)
    {
        if (name == null || status == null) return ERROR_INVALID_PARAMETER;
        *name = NetString("WORKGROUP");
        *status = NetSetupWorkgroupName;
        return 0;
    }

    [Win32Export("NetApiBufferFree")]
    public static int NetApiBufferFree(void* buffer)
    {
        if (buffer != null) NativeMemory.Free(buffer);
        return 0;
    }

    // a string the caller frees with NetApiBufferFree
    private static char* NetString(string s)
    {
        char* p = (char*)NativeMemory.Alloc((nuint)(s.Length + 1) * 2);
        s.AsSpan().CopyTo(new Span<char>(p, s.Length));
        p[s.Length] = '\0';
        return p;
    }

    /// <summary>Whether the process runs elevated: an administrator's token, which off Windows is root's.</summary>
    [Win32Export("IsUserAnAdmin")]
    public static int IsUserAnAdmin() => !OperatingSystem.IsWindows() && WpfWebGpu.Services.WindowsAccounts.CurrentUid() == 0 ? TRUE : FALSE;
}
