// advapi32!LogonUser: a local account's password checked by the OS's own authentication (WpfWebGpu.Services'
// LocalLogon: PAM), with LogonUser's errors. The accounts are this machine's: "user", ".\user", "MACHINE\user",
// "user@machine" or the domain given apart. Off Windows there is no domain to log on to -- another domain is
// ERROR_NO_LOGON_SERVERS, what Windows says when no domain controller answers.
//
// The token it gives is the account's; CloseHandle frees it.

using WpfWebGpu.Services;

namespace WpfWebGpu.Win32;

public static unsafe partial class Exports
{
    private const int ERROR_NO_LOGON_SERVERS = 1311;

    /// <summary>A logged-on account's token.</summary>
    internal sealed record LogonToken(string User, uint? Uid);

    [Win32Export("LogonUserW")]
    public static int LogonUserW(char* user, char* domain, char* password, uint type, uint provider, nint* token) =>
        LogonUserCore(ReadUtf16(user), ReadUtf16(domain), ReadUtf16(password), token);

    [Win32Export("LogonUserA", "LogonUser")]
    public static int LogonUserA(byte* user, byte* domain, byte* password, uint type, uint provider, nint* token) =>
        LogonUserCore(ReadEither(user), ReadEither(domain), ReadEither(password), token);

    private static int LogonUserCore(string? user, string? domain, string? password, nint* token)
    {
        if (token != null) *token = 0;
        if (string.IsNullOrEmpty(user) || token == null) return Fail(ERROR_INVALID_PARAMETER);

        // DOMAIN\user and user@domain name the domain themselves (the second with no domain given apart)
        int slash = user.IndexOf('\\');
        if (slash >= 0) { domain = user[..slash]; user = user[(slash + 1)..]; }
        else if (string.IsNullOrEmpty(domain) && user.LastIndexOf('@') is int at && at > 0) { domain = user[(at + 1)..]; user = user[..at]; }
        if (!IsThisMachine(domain)) return Fail(ERROR_NO_LOGON_SERVERS);

        int error = LocalLogon.Verify(user, password ?? "");
        if (error != 0) return Fail(error);
        *token = NewHandle(new LogonToken(user, WindowsAccounts.Uid(user)));
        return TRUE;
    }

    private static bool IsThisMachine(string? domain)
    {
        if (string.IsNullOrEmpty(domain) || domain == ".") return true;
        string d = domain.Split('.')[0];      // a fully qualified host name stands for the machine too
        return d.Equals(Environment.MachineName.Split('.')[0], StringComparison.OrdinalIgnoreCase)
            || d.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            || domain.Equals(Environment.UserDomainName, StringComparison.OrdinalIgnoreCase);
    }
}
