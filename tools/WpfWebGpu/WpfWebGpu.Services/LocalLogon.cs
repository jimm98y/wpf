// A local account's password checked as LogonUser checks it on Windows -- by the OS's own authentication, PAM:
// OpenPAM's "chkpasswd" service on macOS (Open Directory, made for exactly this), Linux-PAM's "login" elsewhere --
// with LogonUser's errors: a wrong user or password ERROR_LOGON_FAILURE, a locked account ERROR_ACCOUNT_LOCKED_OUT,
// an expired or must-change password, an expired account.
//
// Linux's pam_unix lets an account that is not root check its own password only (its setuid helper refuses the
// others'), so a service account checking another's asks the service manager's daemon, root, over its socket.

using System.Runtime.InteropServices;
using System.Text;

namespace WpfWebGpu.Services;

public static unsafe class LocalLogon
{
    public const int ERROR_LOGON_FAILURE = 1326, ERROR_ACCOUNT_RESTRICTION = 1327, ERROR_PASSWORD_EXPIRED = 1330,
        ERROR_ACCOUNT_EXPIRED = 1793, ERROR_PASSWORD_MUST_CHANGE = 1907, ERROR_ACCOUNT_LOCKED_OUT = 1909,
        ERROR_INTERNAL_ERROR = 1359, ERROR_NOT_SUPPORTED = 50;

    /// <summary>0 where <paramref name="password"/> is the local account <paramref name="user"/>'s; else the error
    /// LogonUser would set.</summary>
    public static int Verify(string user, string password)
    {
        if (OperatingSystem.IsWindows()) return ERROR_NOT_SUPPORTED;     // Windows has LogonUser itself
        // Linux, not root, someone else's password: the daemon checks it
        if (OperatingSystem.IsLinux() && WindowsAccounts.CurrentUid() != 0 && WindowsAccounts.Uid(user) is uint uid && uid != WindowsAccounts.CurrentUid())
        {
            try
            {
                ServiceBroker.Request(ServiceDatabase.DefaultPath(), "logon", user, Convert.ToBase64String(Encoding.UTF8.GetBytes(password)));
                return 0;
            }
            catch (ServiceException e) { return e.Error; }
        }
        return VerifyHere(user, password);
    }

    /// <summary>The check in this process, through PAM.</summary>
    public static int VerifyHere(string user, string password)
    {
        if (!Pam.Load()) return ERROR_INTERNAL_ERROR;
        string service = OperatingSystem.IsMacOS() ? "chkpasswd" : "login";
        IntPtr secret = Marshal.StringToCoTaskMemUTF8(password);
        try
        {
            var conversation = new PamConv { Function = &Converse, AppData = secret };
            IntPtr handle;
            int r = Pam.Start(service, user, &conversation, &handle);
            if (r != 0) return Translate(r);
            r = Pam.Authenticate(handle, 0);
            if (r == 0) r = Pam.AccountManagement(handle, 0);   // expiry, lock-out, must-change
            Pam.End(handle, r);
            return Translate(r);
        }
        finally
        {
            // the password does not outlive the question
            new Span<byte>((void*)secret, Encoding.UTF8.GetByteCount(password)).Clear();
            Marshal.FreeCoTaskMem(secret);
        }
    }

    // PAM's results, numbered differently by OpenPAM (macOS) and Linux-PAM, as LogonUser's errors
    private static int Translate(int pam)
    {
        if (pam == 0) return 0;
        string name = OperatingSystem.IsMacOS()
            ? pam switch { 7 => "PERM_DENIED", 8 => "MAXTRIES", 9 => "AUTH_ERR", 10 => "NEW_AUTHTOK_REQD", 11 => "CRED_INSUFFICIENT", 12 => "AUTHINFO_UNAVAIL", 13 => "USER_UNKNOWN", 17 => "ACCT_EXPIRED", 18 => "AUTHTOK_EXPIRED", _ => "" }
            : pam switch { 6 => "PERM_DENIED", 7 => "AUTH_ERR", 8 => "CRED_INSUFFICIENT", 9 => "AUTHINFO_UNAVAIL", 10 => "USER_UNKNOWN", 11 => "MAXTRIES", 12 => "NEW_AUTHTOK_REQD", 13 => "ACCT_EXPIRED", 27 => "AUTHTOK_EXPIRED", _ => "" };
        return name switch
        {
            "AUTH_ERR" or "USER_UNKNOWN" or "CRED_INSUFFICIENT" => ERROR_LOGON_FAILURE,
            "MAXTRIES" => ERROR_ACCOUNT_LOCKED_OUT,
            "NEW_AUTHTOK_REQD" => ERROR_PASSWORD_MUST_CHANGE,
            "AUTHTOK_EXPIRED" => ERROR_PASSWORD_EXPIRED,
            "ACCT_EXPIRED" => ERROR_ACCOUNT_EXPIRED,
            "PERM_DENIED" => ERROR_ACCOUNT_RESTRICTION,
            _ => ERROR_INTERNAL_ERROR,
        };
    }

    // struct pam_conv { int (*conv)(int, const struct pam_message **, struct pam_response **, void *); void *appdata_ptr; }
    [StructLayout(LayoutKind.Sequential)]
    private struct PamConv
    {
        public delegate* unmanaged<int, IntPtr*, IntPtr*, IntPtr, int> Function;
        public IntPtr AppData;
    }

    // every prompt PAM puts is answered with the password (a module asking twice gets it twice); messages are not
    // shown: there is no one to show them to. The responses are PAM's to free, so they come from malloc.
    [UnmanagedCallersOnly]
    private static int Converse(int count, IntPtr* messages, IntPtr* responses, IntPtr appData)
    {
        if (count <= 0 || count > 32) return OperatingSystem.IsMacOS() ? 6 : 19;          // PAM_CONV_ERR
        // struct pam_response { char *resp; int resp_retcode; }
        IntPtr array = (IntPtr)NativeMemory.AllocZeroed((nuint)count, (nuint)(2 * IntPtr.Size));
        for (int i = 0; i < count; i++)
        {
            int style = *(int*)messages[i];                    // struct pam_message { int msg_style; const char *msg; }
            if (style is 1 or 2)                                 // PAM_PROMPT_ECHO_OFF, PAM_PROMPT_ECHO_ON
                *(IntPtr*)(array + i * 2 * IntPtr.Size) = StrDup((byte*)appData);
        }
        *responses = array;
        return 0;
    }

    private static IntPtr StrDup(byte* s)
    {
        int n = 0;
        while (s[n] != 0) n++;
        byte* copy = (byte*)NativeMemory.Alloc((nuint)n + 1);
        Buffer.MemoryCopy(s, copy, n + 1, n + 1);
        return (IntPtr)copy;
    }

    private static class Pam
    {
        private static bool s_loaded, s_tried;
        private static delegate* unmanaged<byte*, byte*, PamConv*, IntPtr*, int> s_start;
        private static delegate* unmanaged<IntPtr, int, int> s_authenticate, s_account, s_end;

        public static bool Load()
        {
            if (s_tried) return s_loaded;
            s_tried = true;
            string[] names = OperatingSystem.IsMacOS() ? new[] { "/usr/lib/libpam.2.dylib", "/usr/lib/libpam.dylib" } : new[] { "libpam.so.0", "libpam.so" };
            foreach (string name in names)
            {
                if (!NativeLibrary.TryLoad(name, out IntPtr lib)) continue;
                s_start = (delegate* unmanaged<byte*, byte*, PamConv*, IntPtr*, int>)NativeLibrary.GetExport(lib, "pam_start");
                s_authenticate = (delegate* unmanaged<IntPtr, int, int>)NativeLibrary.GetExport(lib, "pam_authenticate");
                s_account = (delegate* unmanaged<IntPtr, int, int>)NativeLibrary.GetExport(lib, "pam_acct_mgmt");
                s_end = (delegate* unmanaged<IntPtr, int, int>)NativeLibrary.GetExport(lib, "pam_end");
                return s_loaded = true;
            }
            return false;
        }

        public static int Start(string service, string user, PamConv* conversation, IntPtr* handle)
        {
            fixed (byte* s = Encoding.UTF8.GetBytes(service + "\0"))
            fixed (byte* u = Encoding.UTF8.GetBytes(user + "\0"))
                return s_start(s, u, conversation, handle);
        }

        public static int Authenticate(IntPtr handle, int flags) => s_authenticate(handle, flags);
        public static int AccountManagement(IntPtr handle, int flags) => s_account(handle, flags);
        public static int End(IntPtr handle, int status) => s_end(handle, status);
    }
}
