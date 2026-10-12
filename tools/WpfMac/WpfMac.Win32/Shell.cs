// The smaller DLLs: dwmapi, uxtheme, wtsapi32, ole32, comctl32, secur32/advapi32 identity.
//
// The desktop window manager is always composing (as on every Windows since 8); visual styles are
// reported as off, so code that would paint with a theme part it cannot open paints classically; the
// session is the console, local, not remote; COM is initialised and has no classes registered.

using System.Runtime.InteropServices;

namespace WpfMac.Win32;

public static unsafe partial class Exports
{
    private static void RegisterShell()
    {
        // dwmapi
        R((delegate* unmanaged<int*, int>)&DwmIsCompositionEnabled, "DwmIsCompositionEnabled");
        R((delegate* unmanaged<uint, int>)&DwmEnableComposition, "DwmEnableComposition");
        R((delegate* unmanaged<nint, void*, int>)&DwmExtendFrameIntoClientArea, "DwmExtendFrameIntoClientArea");
        R((delegate* unmanaged<nint, uint, void*, uint, int>)&DwmSetWindowAttribute, "DwmSetWindowAttribute");
        R((delegate* unmanaged<nint, uint, void*, uint, int>)&DwmGetWindowAttribute, "DwmGetWindowAttribute");
        R((delegate* unmanaged<uint*, int*, int>)&DwmGetColorizationColor, "DwmGetColorizationColor");
        R((delegate* unmanaged<nint, uint, nint, nint, nint*, int>)&DwmDefWindowProc, "DwmDefWindowProc");
        R((delegate* unmanaged<int>)&DwmFlush, "DwmFlush");

        // uxtheme
        R((delegate* unmanaged<int>)&IsThemeActive, "IsThemeActive", "IsAppThemed", "IsCompositionActive");
        R((delegate* unmanaged<nint, char*, char*, int>)&SetWindowTheme, "SetWindowTheme");
        R((delegate* unmanaged<nint, char*, nint>)&OpenThemeData, "OpenThemeData");
        R((delegate* unmanaged<nint, char*, uint, nint>)&OpenThemeDataEx, "OpenThemeDataEx");
        R((delegate* unmanaged<nint, int>)&CloseThemeData, "CloseThemeData");
        R((delegate* unmanaged<uint>)&GetThemeAppProperties, "GetThemeAppProperties");
        R((delegate* unmanaged<uint, void>)&SetThemeAppProperties, "SetThemeAppProperties");
        R((delegate* unmanaged<nint, uint, int>)&EnableThemeDialogTexture, "EnableThemeDialogTexture");
        R((delegate* unmanaged<int, int>)&EnableTheming, "EnableTheming");

        // wtsapi32: the console session, local
        R((delegate* unmanaged<nint, uint, int, char**, uint*, int>)&WTSQuerySessionInformationW, "WTSQuerySessionInformationW", "WTSQuerySessionInformation");
        R((delegate* unmanaged<void*, void>)&WTSFreeMemory, "WTSFreeMemory");
        R((delegate* unmanaged<nint, uint, int>)&WTSRegisterSessionNotification, "WTSRegisterSessionNotification");
        R((delegate* unmanaged<nint, int>)&WTSUnRegisterSessionNotification, "WTSUnRegisterSessionNotification");
        R((delegate* unmanaged<uint>)&WTSGetActiveConsoleSessionId, "WTSGetActiveConsoleSessionId");
        R((delegate* unmanaged<uint, uint*, int>)&ProcessIdToSessionId, "ProcessIdToSessionId");

        // ole32 / oleaut32
        R((delegate* unmanaged<void*, uint, int>)&CoInitializeEx, "CoInitializeEx");
        R((delegate* unmanaged<void*, int>)&CoInitialize, "CoInitialize", "OleInitialize");
        R((delegate* unmanaged<void>)&CoUninitialize, "CoUninitialize", "OleUninitialize");
        R((delegate* unmanaged<void*, int, void*, void*, uint, uint, void*, uint, void*, int>)&CoInitializeSecurity, "CoInitializeSecurity");
        R((delegate* unmanaged<Guid*, void*, uint, Guid*, void**, int>)&CoCreateInstance, "CoCreateInstance");
        R((delegate* unmanaged<void*, int>)&PropVariantClear, "PropVariantClear", "VariantClear");

        // comctl32
        R((delegate* unmanaged<void>)&InitCommonControls, "InitCommonControls");
        R((delegate* unmanaged<void*, int>)&InitCommonControlsEx, "InitCommonControlsEx");

        // who is logged on
        R((delegate* unmanaged<char*, uint*, int>)&GetUserNameW, "GetUserNameW", "GetUserName");
        R((delegate* unmanaged<int, char*, uint*, byte>)&GetUserNameExW, "GetUserNameExW", "GetUserNameEx");
    }

    [UnmanagedCallersOnly] private static int DwmIsCompositionEnabled(int* enabled) { *enabled = TRUE; return S_OK; }
    [UnmanagedCallersOnly] private static int DwmEnableComposition(uint action) => S_OK;
    [UnmanagedCallersOnly] private static int DwmExtendFrameIntoClientArea(nint hwnd, void* margins) => S_OK;
    [UnmanagedCallersOnly] private static int DwmSetWindowAttribute(nint hwnd, uint attr, void* value, uint size) => S_OK;

    [UnmanagedCallersOnly]
    private static int DwmGetWindowAttribute(nint hwnd, uint attr, void* value, uint size)
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

    [UnmanagedCallersOnly] private static int DwmGetColorizationColor(uint* color, int* opaque) { *color = 0xC40078D7; *opaque = FALSE; return S_OK; }
    [UnmanagedCallersOnly] private static int DwmDefWindowProc(nint hwnd, uint msg, nint w, nint l, nint* result) { *result = 0; return FALSE; }
    [UnmanagedCallersOnly] private static int DwmFlush() => S_OK;

    [UnmanagedCallersOnly] private static int IsThemeActive() => FALSE;
    [UnmanagedCallersOnly] private static int SetWindowTheme(nint hwnd, char* app, char* idList) => S_OK;
    [UnmanagedCallersOnly] private static nint OpenThemeData(nint hwnd, char* classList) { SetLastError(unchecked((int)0x80070490)); return 0; }   // E_PROP_ID_UNSUPPORTED-ish: no theme
    [UnmanagedCallersOnly] private static nint OpenThemeDataEx(nint hwnd, char* classList, uint flags) => 0;
    [UnmanagedCallersOnly] private static int CloseThemeData(nint theme) => S_OK;
    [UnmanagedCallersOnly] private static uint GetThemeAppProperties() => 0;
    [UnmanagedCallersOnly] private static void SetThemeAppProperties(uint flags) { }
    [UnmanagedCallersOnly] private static int EnableThemeDialogTexture(nint hwnd, uint flags) => S_OK;
    [UnmanagedCallersOnly] private static int EnableTheming(int enable) => S_OK;

    // WTS_INFO_CLASS: 4 WTSConnectState? no: 1 WTSInitialProgram ... 5 WTSUserName, 7 WTSDomainName,
    // 10 WTSClientName, 16 WTSClientProtocolType. The answers of a local console session.
    [UnmanagedCallersOnly]
    private static int WTSQuerySessionInformationW(nint server, uint session, int infoClass, char** buffer, uint* bytes)
    {
        switch (infoClass)
        {
            case 16:    // WTSClientProtocolType: 0, the console
            case 8:     // WTSConnectState: 0, WTSActive
            {
                void* p = NativeMemory.AllocZeroed(4);
                *buffer = (char*)p;
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
                char* p = (char*)NativeMemory.AllocZeroed((nuint)(value.Length + 1) * 2);
                WriteUtf16(p, value.Length + 1, value);
                *buffer = p;
                *bytes = (uint)(value.Length + 1) * 2;
                return TRUE;
            }
        }
    }

    [UnmanagedCallersOnly] private static void WTSFreeMemory(void* p) => NativeMemory.Free(p);
    [UnmanagedCallersOnly] private static int WTSRegisterSessionNotification(nint hwnd, uint flags) => TRUE;
    [UnmanagedCallersOnly] private static int WTSUnRegisterSessionNotification(nint hwnd) => TRUE;
    [UnmanagedCallersOnly] private static uint WTSGetActiveConsoleSessionId() => 1;
    [UnmanagedCallersOnly] private static int ProcessIdToSessionId(uint pid, uint* session) { *session = 1; return TRUE; }

    [UnmanagedCallersOnly] private static int CoInitializeEx(void* reserved, uint model) => S_OK;
    [UnmanagedCallersOnly] private static int CoInitialize(void* reserved) => S_OK;
    [UnmanagedCallersOnly] private static void CoUninitialize() { }
    [UnmanagedCallersOnly] private static int CoInitializeSecurity(void* a, int b, void* c, void* d, uint e, uint f, void* g, uint h, void* i) => S_OK;

    [UnmanagedCallersOnly]
    private static int CoCreateInstance(Guid* clsid, void* outer, uint context, Guid* iid, void** result)
    {
        *result = null;
        LogOnce("clsid:" + *clsid, $"win32: CoCreateInstance({*clsid}): no COM class is registered here");
        return unchecked((int)0x80040154);   // REGDB_E_CLASSNOTREG
    }

    [UnmanagedCallersOnly] private static int PropVariantClear(void* pv) { NativeMemory.Clear(pv, 16); return S_OK; }
    [UnmanagedCallersOnly] private static void InitCommonControls() { }
    [UnmanagedCallersOnly] private static int InitCommonControlsEx(void* icc) => TRUE;

    [UnmanagedCallersOnly]
    private static int GetUserNameW(char* buffer, uint* size)
    {
        string name = Environment.UserName;
        if (*size <= name.Length) { *size = (uint)name.Length + 1; SetLastError(ERROR_INSUFFICIENT_BUFFER); return FALSE; }
        *size = (uint)WriteUtf16(buffer, (int)*size, name) + 1;
        return TRUE;
    }

    [UnmanagedCallersOnly]
    private static byte GetUserNameExW(int format, char* buffer, uint* size)
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
