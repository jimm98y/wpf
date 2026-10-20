// The smaller DLLs: dwmapi, uxtheme, wtsapi32, ole32, comctl32, secur32/advapi32 identity.
//
// The desktop window manager is always composing (as on every Windows since 8); visual styles are on
// and drawn by the stack's own theme (UxTheme.cs); the session is the console, local, not remote; COM
// is initialised and has no classes registered.

using System.Runtime.InteropServices;

namespace WpfMac.Win32;

public static unsafe partial class Exports
{

    [Win32Export("DwmIsCompositionEnabled")] public static int DwmIsCompositionEnabled(int* enabled) { *enabled = TRUE; return S_OK; }
    [Win32Export("DwmEnableComposition")] public static int DwmEnableComposition(uint action) => S_OK;
    [Win32Export("DwmExtendFrameIntoClientArea")] public static int DwmExtendFrameIntoClientArea(nint hwnd, void* margins) => S_OK;
    [Win32Export("DwmSetWindowAttribute")] public static int DwmSetWindowAttribute(nint hwnd, uint attr, void* value, uint size) => S_OK;

    [Win32Export("DwmGetWindowAttribute")]
    public static int DwmGetWindowAttribute(nint hwnd, uint attr, void* value, uint size)
    {
        switch (attr)
        {
            case 9:     // DWMWA_EXTENDED_FRAME_BOUNDS
                if (size < 16) return E_INVALIDARG;
                *(RECT*)value = WindowRect(hwnd, out bool found);
                return found ? S_OK : E_INVALIDARG;
            case 14:    // DWMWA_CLOAKED
                if (size < 4) return E_INVALIDARG;
                *(int*)value = 0;
                return S_OK;
            case 1:     // DWMWA_NCRENDERING_ENABLED
                if (size < 4) return E_INVALIDARG;
                *(int*)value = TRUE;
                return S_OK;
            default:
                return E_INVALIDARG;
        }
    }

    [Win32Export("DwmGetColorizationColor")] public static int DwmGetColorizationColor(uint* color, int* opaque) { *color = 0xC40078D7; *opaque = FALSE; return S_OK; }
    [Win32Export("DwmDefWindowProc")] public static int DwmDefWindowProc(nint hwnd, uint msg, nint w, nint l, nint* result) { *result = 0; return FALSE; }
    [Win32Export("DwmFlush")] public static int DwmFlush() => S_OK;


    // WTS_INFO_CLASS: 4 WTSConnectState? no: 1 WTSInitialProgram ... 5 WTSUserName, 7 WTSDomainName,
    // 10 WTSClientName, 16 WTSClientProtocolType. The answers of a local console session.
    [Win32Export("WTSQuerySessionInformationW")]
    public static int WTSQuerySessionInformationW(nint server, uint session, int infoClass, char** buffer, uint* bytes) =>
        QuerySession(infoClass, (byte**)buffer, bytes, wide: true);

    [Win32Export("WTSQuerySessionInformationA")]
    public static int WTSQuerySessionInformationA(nint server, uint session, int infoClass, byte** buffer, uint* bytes) =>
        QuerySession(infoClass, buffer, bytes, wide: false);

    private static int QuerySession(int infoClass, byte** buffer, uint* bytes, bool wide)
    {
        switch (infoClass)
        {
            case 16:    // WTSClientProtocolType: 0, the console
            case 8:     // WTSConnectState: 0, WTSActive
            {
                *buffer = (byte*)NativeMemory.AllocZeroed(4);
                *bytes = infoClass == 16 ? 2u : 4u;
                return TRUE;
            }
            default:
            {
                string value = infoClass switch
                {
                    5 => Environment.UserName,                 // WTSUserName
                    7 => Environment.UserDomainName,           // WTSDomainName
                    10 => "",                                  // WTSClientName: no remote client
                    _ => "",
                };
                // the A function's text is the ANSI code page's, which .NET off Windows reads as UTF-8
                int size = wide ? (value.Length + 1) * 2 : System.Text.Encoding.UTF8.GetByteCount(value) + 1;
                byte* p = (byte*)NativeMemory.AllocZeroed((nuint)size);
                if (wide) WriteUtf16((char*)p, value.Length + 1, value);
                else WriteUtf8(p, size, value);
                *buffer = p;
                *bytes = (uint)size;
                return TRUE;
            }
        }
    }

    [Win32Export("WTSFreeMemory")] public static void WTSFreeMemory(void* p) => NativeMemory.Free(p);
    [Win32Export("WTSRegisterSessionNotification")] public static int WTSRegisterSessionNotification(nint hwnd, uint flags) => TRUE;
    [Win32Export("WTSUnRegisterSessionNotification")] public static int WTSUnRegisterSessionNotification(nint hwnd) => TRUE;
    [Win32Export("WTSGetActiveConsoleSessionId")] public static uint WTSGetActiveConsoleSessionId() => 1;
    [Win32Export("ProcessIdToSessionId")] public static int ProcessIdToSessionId(uint pid, uint* session) { *session = 1; return TRUE; }

    [Win32Export("CoInitializeEx")] public static int CoInitializeEx(void* reserved, uint model) => S_OK;
    [Win32Export("CoInitialize", "OleInitialize")] public static int CoInitialize(void* reserved) => S_OK;
    [Win32Export("CoUninitialize", "OleUninitialize")] public static void CoUninitialize() { }

    // Keeping the multithreaded apartment alive without a thread in it (CsWinRT's module does, from its
    // constructor to its finalizer): a cookie per call, the MTA's usage counted. Every thread here is in
    // the MTA as far as anything asks, so there is nothing else to keep alive.
    private static int s_mtaUsage;
    [Win32Export("CoIncrementMTAUsage")]
    public static int CoIncrementMTAUsage(nint* cookie)
    {
        if (cookie == null) return E_INVALIDARG;
        *cookie = Interlocked.Increment(ref s_mtaUsage);
        return S_OK;
    }

    [Win32Export("CoDecrementMTAUsage")]
    public static int CoDecrementMTAUsage(nint cookie)
    {
        if (cookie == 0) return E_INVALIDARG;
        Interlocked.Decrement(ref s_mtaUsage);
        return S_OK;
    }
    [Win32Export("CoInitializeSecurity")] public static int CoInitializeSecurity(void* a, int b, void* c, void* d, uint e, uint f, void* g, uint h, void* i) => S_OK;

    [Win32Export("CoCreateInstance")]
    public static int CoCreateInstance(Guid* clsid, void* outer, uint context, Guid* iid, void** result)
    {
        *result = null;
        LogOnce("clsid:" + *clsid, $"win32: CoCreateInstance({*clsid}): no COM class is registered here");
        return unchecked((int)0x80040154);   // REGDB_E_CLASSNOTREG
    }

    [Win32Export("PropVariantClear")] public static int PropVariantClear(void* pv) => WpfMac.ComInterop.Com.PropVariantClear((nint)pv);
    [Win32Export("PropVariantCopy")] public static int PropVariantCopy(void* dst, void* src) => WpfMac.ComInterop.Com.PropVariantCopy((nint)dst, (nint)src);
    [Win32Export("VariantClear")] public static int VariantClear(void* pv) => WpfMac.ComInterop.Com.VariantClear((nint)pv);
    [Win32Export("VariantCopy")] public static int VariantCopy(void* dst, void* src) => WpfMac.ComInterop.Com.VariantCopy((nint)dst, (nint)src);
    [Win32Export("VariantInit")] public static void VariantInit(void* pv) => WpfMac.ComInterop.Com.VariantInit((nint)pv);
    [Win32Export("InitCommonControls")] public static void InitCommonControls() { }
    [Win32Export("InitCommonControlsEx")] public static int InitCommonControlsEx(void* icc) => TRUE;

    [Win32Export("GetUserNameW", "GetUserName")]
    public static int GetUserNameW(char* buffer, uint* size)
    {
        string name = Environment.UserName;
        if (*size <= name.Length) { *size = (uint)name.Length + 1; SetLastError(ERROR_INSUFFICIENT_BUFFER); return FALSE; }
        *size = (uint)WriteUtf16(buffer, (int)*size, name) + 1;
        return TRUE;
    }

    [Win32Export("GetUserNameExW", "GetUserNameEx")]
    public static byte GetUserNameExW(int format, char* buffer, uint* size)
    {
        // NameSamCompatible (2) is DOMAIN\user; NameDisplay (3) a display name. Others are directory
        // forms a machine outside a domain has none of.
        string? name = format switch
        {
            2 => Environment.UserDomainName + "\\" + Environment.UserName,
            3 => Environment.UserName,
            _ => null,
        };
        if (name == null) { SetLastError(1332); return 0; }   // ERROR_NONE_MAPPED
        if (*size <= name.Length) { *size = (uint)name.Length + 1; SetLastError(234); return 0; }   // ERROR_MORE_DATA
        *size = (uint)WriteUtf16(buffer, (int)*size, name);
        return 1;
    }
}
