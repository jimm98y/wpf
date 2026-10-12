// The entry point the bootstrap calls (by reflection, so the bootstrap itself stays free of WPF and
// WinForms references): registers every implementation in this assembly with libwin32shim.

using System.Runtime.InteropServices;

namespace WpfMac.Win32;

public static unsafe partial class Exports
{
    private static Func<string, IntPtr, bool>? s_register;
    private static Action<string, string>? s_logOnce;
    private static int s_count;

    /// <summary>
    /// Registers every implementation. <paramref name="register"/> points an export's slot at a
    /// function and reports whether the library has that export; <paramref name="logOnce"/> writes a
    /// line to the compatibility log the first time its key is seen.
    /// </summary>
    public static int RegisterAll(Func<string, IntPtr, bool> register, Action<string, string> logOnce)
    {
        s_register = register;
        s_logOnce = logOnce;
        s_count = 0;
        RegisterKernel32();
        RegisterUser32System();
        RegisterUser32Windows();
        RegisterShell();
        RegisterGdi();
        return s_count;
    }

    /// <summary>Registers <paramref name="fn"/> under each of <paramref name="names"/>.</summary>
    private static void R(void* fn, params string[] names)
    {
        foreach (string name in names)
        {
            if (s_register!(name, (IntPtr)fn)) s_count++;
        }
    }

    internal static void LogOnce(string key, string message) => s_logOnce?.Invoke(key, message);

    // ---- helpers shared by the implementations ------------------------------------------------

    internal const int TRUE = 1;
    internal const int FALSE = 0;
    internal const int S_OK = 0;
    internal const int S_FALSE = 1;
    internal const int E_NOTIMPL = unchecked((int)0x80004001);
    internal const int E_INVALIDARG = unchecked((int)0x80070057);
    internal const int E_FAIL = unchecked((int)0x80004005);

    internal const int ERROR_SUCCESS = 0;
    internal const int ERROR_FILE_NOT_FOUND = 2;
    internal const int ERROR_INVALID_HANDLE = 6;
    internal const int ERROR_INSUFFICIENT_BUFFER = 122;
    internal const int ERROR_INVALID_PARAMETER = 87;
    internal const int ERROR_CALL_NOT_IMPLEMENTED = 120;
    internal const int ERROR_INVALID_WINDOW_HANDLE = 1400;

    /// <summary>Sets what GetLastError (Marshal.GetLastPInvokeError for a SetLastError import) reads next.</summary>
    internal static void SetLastError(int error)
    {
        t_lastError = error;                      // what a direct P/Invoke of GetLastError reads
        Marshal.SetLastSystemError(error);        // what an import with SetLastError = true captures
    }

    /// <summary>
    /// Writes <paramref name="value"/> into a caller's UTF-16 buffer of <paramref name="capacity"/>
    /// chars, truncated and NUL-terminated as the Windows functions do. Returns the chars written
    /// (without the terminator).
    /// </summary>
    internal static int WriteUtf16(char* buffer, int capacity, string value)
    {
        if (buffer == null || capacity <= 0) return 0;
        int n = Math.Min(value.Length, capacity - 1);
        value.AsSpan(0, n).CopyTo(new Span<char>(buffer, n));
        buffer[n] = '\0';
        return n;
    }

    /// <summary>The UTF-8 counterpart, for the undecorated entry points .NET marshals as UTF-8 off Windows.</summary>
    internal static int WriteUtf8(byte* buffer, int capacity, string value)
    {
        if (buffer == null || capacity <= 0) return 0;
        byte[] bytes = System.Text.Encoding.UTF8.GetBytes(value);
        int n = Math.Min(bytes.Length, capacity - 1);
        bytes.AsSpan(0, n).CopyTo(new Span<byte>(buffer, n));
        buffer[n] = 0;
        return n;
    }

    internal static string? ReadUtf16(char* s) => s == null ? null : new string(s);

    /// <summary>
    /// A string argument of an UNDECORATED entry point (GetWindowText rather than GetWindowTextW).
    /// .NET marshals those as UTF-16 when the import says CharSet.Unicode and as UTF-8 otherwise, and
    /// nothing at the call says which; ASCII text in UTF-16 has a zero in every other byte, which
    /// UTF-8 never has before its terminator.
    /// </summary>
    internal static string? ReadEither(byte* s)
    {
        if (s == null) return null;
        if (s[0] != 0 && s[1] == 0) return new string((char*)s);
        return Marshal.PtrToStringUTF8((IntPtr)s);
    }
}
