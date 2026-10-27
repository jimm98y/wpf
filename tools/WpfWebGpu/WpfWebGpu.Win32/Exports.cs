// The implementations of Windows exports, which an app's P/Invokes reach rewritten (WpfWebGpu.ComRewrite's
// P/Invoke rewriter, Documentation/pinvoke-rewriter.md): each method here tagged [Win32Export] is called,
// directly, by every P/Invoke of a function it names. Initialize is what the bootstrap calls first (by
// reflection, so the bootstrap itself stays free of WPF and WinForms references).

using System.Runtime.InteropServices;

namespace WpfWebGpu.Win32;

public static unsafe partial class Exports
{
    private static Action<string, string>? s_logOnce;

    /// <summary>
    /// Sets up what the implementations need before the first call. <paramref name="logOnce"/> writes a line
    /// to the compatibility log the first time its key is seen.
    /// </summary>
    public static void Initialize(Action<string, string> logOnce)
    {
        s_logOnce = logOnce;
        OnWindows = OperatingSystem.IsWindows();
        if (OnWindows) return;
        // for the Direct3D shim (DXGI's EnqueueSetEvent signals an event of ours)
        AppContext.SetData("WpfWebGpu.SetEvent", (Action<IntPtr>)(h => SetEvent(h)));
        RegisterGdi();
        RegisterHooks();
    }

    /// <summary>
    /// A rewritten P/Invoke of a function nothing here implements: logs the call once and returns the
    /// function's failure value (<paramref name="failure"/>: Win32Catalog.Failure), setting the last error as
    /// the function would on a Windows that lacked it.
    /// </summary>
    public static long Unimplemented(string module, string name, int failure)
    {
        LogOnce("unimpl:" + module + "!" + name, $"win32 shim: {module}!{name} is not implemented; returned {Describe(failure)}");
        switch (failure)
        {
            case 0: return 0;                                         // void
            case 2: return unchecked((int)0x80004001);                // E_NOTIMPL
            case 3: return unchecked((int)0xC0000002);                // STATUS_NOT_IMPLEMENTED
            case 4: return ERROR_CALL_NOT_IMPLEMENTED;                // WIN32_ERROR, returned
            case 5: SetLastError(ERROR_CALL_NOT_IMPLEMENTED); Marshal.SetLastPInvokeError(ERROR_CALL_NOT_IMPLEMENTED); return -1;  // INVALID_HANDLE_VALUE
            default: SetLastError(ERROR_CALL_NOT_IMPLEMENTED); Marshal.SetLastPInvokeError(ERROR_CALL_NOT_IMPLEMENTED); return 0;  // FALSE / NULL / 0
        }
    }

    private static string Describe(int failure) => failure switch
    {
        0 => "nothing",
        2 => "E_NOTIMPL",
        3 => "STATUS_NOT_IMPLEMENTED",
        4 => "ERROR_CALL_NOT_IMPLEMENTED",
        5 => "INVALID_HANDLE_VALUE",
        _ => "0 (last error ERROR_CALL_NOT_IMPLEMENTED)",
    };

    internal static void LogOnce(string key, string message) => s_logOnce?.Invoke(key, message);

    /// <summary>Every call: through the callback the bootstrap publishes (WpfWebGpu.Log).</summary>
    internal static void Log(string message) => (AppContext.GetData("WpfWebGpu.Log") as Action<string>)?.Invoke(message);

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
