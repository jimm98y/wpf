// The WINDOWS half of the Microsoft.Win32.SystemEvents stand-in: a real message-only window on a
// real pump thread, turning the OS broadcasts into the events this assembly declares.
//
// WHY A SHIM NEEDS A WINDOWS PATH AT ALL. Off Windows this assembly substitutes for one whose every
// member throws, and Windows never loaded it: the head referenced the real runtime package, and
// which of the two shipped was a build-time decision. A PORTABLE publish
// (-p:WpfWebGpuPortable=true) has no build-time decision available -- one output has to run on all
// three desktop systems, and the two flavours share an assembly identity, so exactly one can ship.
// Whichever ships has to be right everywhere.
//
// Getting this wrong is silent in the way that costs an afternoon: nothing throws, the app just
// stops noticing that the theme, the DPI, the display layout or the system colours changed, and
// keeps rendering what it decided at startup.
//
// This is deliberately the SUBSET that matters, not a reimplementation of the framework's
// SystemEvents. The broadcasts below are the ones WPF and ordinary apps subscribe to; everything
// else keeps the no-op behaviour it had.

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;

namespace Microsoft.Win32
{
    internal static class WindowsSystemEvents
    {
        // ---- window messages ----------------------------------------------------------------
        private const int WM_DESTROY = 0x0002;
        private const int WM_QUERYENDSESSION = 0x0011;
        private const int WM_ENDSESSION = 0x0016;
        private const int WM_SETTINGCHANGE = 0x001A;
        private const int WM_DEVMODECHANGE = 0x001B;
        private const int WM_FONTCHANGE = 0x001D;
        private const int WM_TIMECHANGE = 0x001E;
        private const int WM_DISPLAYCHANGE = 0x007E;
        private const int WM_TIMER = 0x0113;
        private const int WM_PALETTECHANGED = 0x0311;
        private const int WM_THEMECHANGED = 0x031A;
        private const int WM_POWERBROADCAST = 0x0218;
        private const int WM_WTSSESSION_CHANGE = 0x02B1;
        private const int WM_USER_INVOKE = 0x0400 + 1;   // our own: run a queued delegate

        private const int PBT_APMRESUMESUSPEND = 0x0007;
        private const int PBT_APMSUSPEND = 0x0004;
        private const int PBT_APMPOWERSTATUSCHANGE = 0x000A;

        private const int ENDSESSION_LOGOFF = unchecked((int)0x80000000);
        private const int WS_OVERLAPPED = 0x00000000;
        private const int WS_EX_TOOLWINDOW = 0x00000080;

        private delegate IntPtr WndProc(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct WNDCLASS
        {
            public int style;
            public WndProc lpfnWndProc;
            public int cbClsExtra;
            public int cbWndExtra;
            public IntPtr hInstance;
            public IntPtr hIcon;
            public IntPtr hCursor;
            public IntPtr hbrBackground;
            [MarshalAs(UnmanagedType.LPWStr)] public string lpszMenuName;
            [MarshalAs(UnmanagedType.LPWStr)] public string lpszClassName;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MSG
        {
            public IntPtr hwnd; public int message; public IntPtr wParam; public IntPtr lParam;
            public int time; public int x; public int y;
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "RegisterClassW")]
        private static extern ushort RegisterClass(ref WNDCLASS wc);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "CreateWindowExW")]
        private static extern IntPtr CreateWindowEx(int exStyle, string className, string windowName, int style,
                                                    int x, int y, int w, int h, IntPtr parent, IntPtr menu,
                                                    IntPtr instance, IntPtr param);

        [DllImport("user32.dll", EntryPoint = "DefWindowProcW")]
        private static extern IntPtr DefWindowProc(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", EntryPoint = "GetMessageW")]
        private static extern int GetMessage(out MSG msg, IntPtr hWnd, int min, int max);

        [DllImport("user32.dll", EntryPoint = "TranslateMessage")]
        private static extern bool TranslateMessage(ref MSG msg);

        [DllImport("user32.dll", EntryPoint = "DispatchMessageW")]
        private static extern IntPtr DispatchMessage(ref MSG msg);

        [DllImport("user32.dll", EntryPoint = "PostMessageW")]
        private static extern bool PostMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", EntryPoint = "SetTimer")]
        private static extern IntPtr SetTimer(IntPtr hWnd, IntPtr id, int elapse, IntPtr proc);

        [DllImport("user32.dll", EntryPoint = "KillTimer")]
        private static extern bool KillTimer(IntPtr hWnd, IntPtr id);

        [DllImport("kernel32.dll", EntryPoint = "GetModuleHandleW", CharSet = CharSet.Unicode)]
        private static extern IntPtr GetModuleHandle(string name);

        [DllImport("wtsapi32.dll", EntryPoint = "WTSRegisterSessionNotification")]
        private static extern bool WTSRegisterSessionNotification(IntPtr hWnd, int flags);

        // The delegate must outlive the window: the OS holds a raw function pointer to it, and a
        // collected delegate is a crash in the message pump rather than an exception anywhere useful.
        private static WndProc s_wndProc;
        private static IntPtr s_hwnd;
        private static readonly object s_gate = new object();
        private static bool s_starting;
        private static readonly Queue<Delegate> s_invokeQueue = new Queue<Delegate>();
        private static int s_nextTimerId = 1;

        internal static IntPtr Hwnd => s_hwnd;

        /// <summary>
        /// Creates the message window on its own pump thread, once. Called the first time anything
        /// subscribes, mirroring the real implementation, so a process that never asks for a system
        /// event never grows a thread.
        /// </summary>
        internal static void Ensure()
        {
            lock (s_gate)
            {
                if (s_starting) return;
                s_starting = true;
            }

            using var ready = new ManualResetEventSlim(false);

            var thread = new Thread(() =>
            {
                try
                {
                    s_wndProc = Proc;
                    var wc = new WNDCLASS
                    {
                        lpfnWndProc = s_wndProc,
                        hInstance = GetModuleHandle(null),
                        // Unique per process: two AppDomains, or a host that already registered the
                        // framework's own class, must not collide on the name.
                        lpszClassName = "WpfWebGpuSystemEvents+" + Environment.ProcessId,
                    };
                    RegisterClass(ref wc);

                    // A TOP-LEVEL window, not a message-only one (HWND_MESSAGE), even though nothing
                    // is ever drawn in it. Message-only windows do not receive BROADCAST messages,
                    // and WM_SETTINGCHANGE -- the whole point of this class, and how light/dark
                    // reaches an app -- is broadcast to top-level windows only. Built that way
                    // first: the timer ticked, the pump ran, and not one system notification ever
                    // arrived.
                    //
                    // It stays invisible (never shown, zero size, no WS_VISIBLE) and WS_EX_TOOLWINDOW
                    // keeps it out of the taskbar and alt-tab.
                    s_hwnd = CreateWindowEx(WS_EX_TOOLWINDOW, wc.lpszClassName, string.Empty, WS_OVERLAPPED,
                                            0, 0, 0, 0, IntPtr.Zero, IntPtr.Zero, wc.hInstance, IntPtr.Zero);

                    if (s_hwnd != IntPtr.Zero)
                    {
                        // Session lock/unlock and connect/disconnect are not broadcast to every
                        // window; they have to be asked for. NOTIFY_FOR_THIS_SESSION == 0.
                        try { WTSRegisterSessionNotification(s_hwnd, 0); } catch (DllNotFoundException) { }
                    }
                }
                finally
                {
                    ready.Set();
                }

                if (s_hwnd == IntPtr.Zero) return;

                while (GetMessage(out MSG msg, IntPtr.Zero, 0, 0) > 0)
                {
                    TranslateMessage(ref msg);
                    DispatchMessage(ref msg);
                }
            })
            {
                // Background: this thread must never be the reason a process stays alive, and it has
                // no shutdown protocol of its own.
                IsBackground = true,
                Name = ".NET SystemEvents (WpfWebGpu)",
            };

            // STA because the window belongs to this thread and some of what the broadcasts reach
            // (shell notifications, WTS) expects an apartment-threaded owner.
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();

            // Wait for the HWND: CreateTimer and InvokeOnEventsThread are useless without it, and
            // callers reasonably expect them to work as soon as they have subscribed.
            ready.Wait(TimeSpan.FromSeconds(5));
        }

        private static IntPtr Proc(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam)
        {
            switch (msg)
            {
                case WM_SETTINGCHANGE:
                    SystemEvents.RaiseUserPreference(CategoryOf(wParam.ToInt64(), lParam));
                    break;

                case WM_THEMECHANGED:
                    SystemEvents.RaiseUserPreference(UserPreferenceCategory.VisualStyle);
                    break;

                case WM_DEVMODECHANGE:
                    SystemEvents.RaiseUserPreference(UserPreferenceCategory.Desktop);
                    break;

                case WM_DISPLAYCHANGE:
                    SystemEvents.RaiseDisplaySettingsChanged();
                    break;

                case WM_FONTCHANGE:
                    SystemEvents.RaiseInstalledFontsChanged();
                    break;

                case WM_PALETTECHANGED:
                    SystemEvents.RaisePaletteChanged();
                    break;

                case WM_TIMECHANGE:
                    SystemEvents.RaiseTimeChanged();
                    break;

                case WM_TIMER:
                    SystemEvents.RaiseTimerElapsed(wParam);
                    break;

                case WM_POWERBROADCAST:
                    switch (wParam.ToInt32())
                    {
                        case PBT_APMRESUMESUSPEND: SystemEvents.RaisePowerMode(PowerModes.Resume); break;
                        case PBT_APMSUSPEND: SystemEvents.RaisePowerMode(PowerModes.Suspend); break;
                        case PBT_APMPOWERSTATUSCHANGE: SystemEvents.RaisePowerMode(PowerModes.StatusChange); break;
                    }
                    break;

                case WM_QUERYENDSESSION:
                    // Returning zero would VETO the shutdown. An app that wants to do that sets
                    // Cancel, and only then is the veto passed on.
                    return SystemEvents.RaiseSessionEnding(ReasonOf(lParam)) ? IntPtr.Zero : (IntPtr)1;

                case WM_ENDSESSION:
                    if (wParam != IntPtr.Zero) SystemEvents.RaiseSessionEnded(ReasonOf(lParam));
                    break;

                case WM_WTSSESSION_CHANGE:
                    int reason = wParam.ToInt32();
                    if (reason >= 1 && reason <= 9) SystemEvents.RaiseSessionSwitch((SessionSwitchReason)reason);
                    break;

                case WM_USER_INVOKE:
                    Delegate d = null;
                    lock (s_invokeQueue) { if (s_invokeQueue.Count > 0) d = s_invokeQueue.Dequeue(); }
                    try { d?.DynamicInvoke(); } catch { /* a subscriber's exception must not kill the pump */ }
                    break;

                case WM_DESTROY:
                    s_hwnd = IntPtr.Zero;
                    break;
            }

            return DefWindowProc(hWnd, msg, wParam, lParam);
        }

        private static SessionEndReasons ReasonOf(IntPtr lParam) =>
            (lParam.ToInt32() & ENDSESSION_LOGOFF) != 0 ? SessionEndReasons.Logoff : SessionEndReasons.SystemShutdown;

        /// <summary>
        /// Maps WM_SETTINGCHANGE to a category. wParam is an SPI_SET* code for most changes; the
        /// broadcasts that carry a string in lParam ("ImmersiveColorSet" being the one that matters
        /// for light/dark) carry no code at all.
        /// </summary>
        private static UserPreferenceCategory CategoryOf(long wParam, IntPtr lParam)
        {
            string area = lParam == IntPtr.Zero ? null : Marshal.PtrToStringUni(lParam);
            if (!string.IsNullOrEmpty(area))
            {
                switch (area)
                {
                    case "ImmersiveColorSet": return UserPreferenceCategory.Color;
                    case "WindowsThemeElement": return UserPreferenceCategory.VisualStyle;
                    case "Policy": return UserPreferenceCategory.Policy;
                    case "intl": return UserPreferenceCategory.Locale;
                    case "Environment": return UserPreferenceCategory.General;
                }
            }

            // SPI_SET* ranges, condensed to the categories the enum offers.
            return wParam switch
            {
                0x0002 or 0x000A or 0x000C or 0x0018 => UserPreferenceCategory.Desktop,   // BEEP/BORDER/DESKWALLPAPER
                0x0004 or 0x0006 or 0x0008 => UserPreferenceCategory.Mouse,
                0x000B or 0x004B => UserPreferenceCategory.Keyboard,
                0x0057 or 0x0059 => UserPreferenceCategory.Icon,
                0x0011 or 0x0013 or 0x0015 => UserPreferenceCategory.Screensaver,
                0x003D or 0x003F or 0x0041 or 0x0053 => UserPreferenceCategory.Accessibility,
                0x1035 or 0x1043 => UserPreferenceCategory.Menu,
                0x0079 or 0x102B => UserPreferenceCategory.Window,
                0x0051 => UserPreferenceCategory.Locale,
                0x0091 or 0x0093 => UserPreferenceCategory.Power,
                _ => UserPreferenceCategory.General,
            };
        }

        internal static IntPtr CreateTimer(int interval)
        {
            Ensure();
            if (s_hwnd == IntPtr.Zero) return IntPtr.Zero;
            IntPtr id = (IntPtr)Interlocked.Increment(ref s_nextTimerId);
            return SetTimer(s_hwnd, id, interval, IntPtr.Zero) == IntPtr.Zero ? IntPtr.Zero : id;
        }

        internal static void KillTimerCore(IntPtr id)
        {
            if (s_hwnd != IntPtr.Zero) KillTimer(s_hwnd, id);
        }

        internal static bool TryInvokeOnEventsThread(Delegate method)
        {
            Ensure();
            if (s_hwnd == IntPtr.Zero) return false;
            lock (s_invokeQueue) { s_invokeQueue.Enqueue(method); }
            return PostMessage(s_hwnd, WM_USER_INVOKE, IntPtr.Zero, IntPtr.Zero);
        }
    }
}
