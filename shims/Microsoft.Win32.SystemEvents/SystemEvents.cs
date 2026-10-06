// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// Microsoft.Win32.SystemEvents stand-in, with TWO implementations behind one public surface:
//
//   Windows      a real message-only window on its own pump thread, raising these events from the
//                OS broadcasts (see WindowsSystemEvents.cs)
//   elsewhere    handlers are accepted and simply never raised: there is no OS setting-change
//                broadcast to observe, and a no-op beats the real assembly's
//                PlatformNotSupportedException from every member
//
// It used to be the second half only, because the choice was made at BUILD time: the Windows head
// referenced the real runtime package and never loaded this assembly. A portable publish
// (-p:WpfWebGpuPortable=true) has no build-time choice available: one output runs on all three
// desktop systems and the two flavours share an assembly identity, so whichever ships has to be
// correct everywhere.
//
// WPF apps subscribe to this routinely (UserPreferenceChanged is how they react to light/dark, to a
// DPI change, to display layout), and the failure mode without a Windows path is silent: nothing
// throws, the app simply never learns that anything changed.

namespace Microsoft.Win32
{
    public sealed class SystemEvents
    {
        internal SystemEvents() { }

        private static readonly bool s_onWindows =
            System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Windows);

        // Subscribing is what starts the message pump on Windows, exactly as the real implementation
        // creates its window lazily: a process that never asks for a system event never grows a
        // thread for one. Off Windows Ensure() is never called and these stay inert.
        private static void Started() { if (s_onWindows) WindowsSystemEvents.Ensure(); }

        private static System.EventHandler? s_displaySettingsChanged;
        private static System.EventHandler? s_displaySettingsChanging;
        private static System.EventHandler? s_installedFontsChanged;
        private static System.EventHandler? s_paletteChanged;
        private static System.EventHandler? s_timeChanged;
        private static PowerModeChangedEventHandler? s_powerModeChanged;
        private static SessionEndedEventHandler? s_sessionEnded;
        private static SessionEndingEventHandler? s_sessionEnding;
        private static SessionSwitchEventHandler? s_sessionSwitch;
        private static TimerElapsedEventHandler? s_timerElapsed;
        private static UserPreferenceChangedEventHandler? s_userPreferenceChanged;
        private static UserPreferenceChangingEventHandler? s_userPreferenceChanging;

        public static event System.EventHandler? DisplaySettingsChanged { add { Started(); s_displaySettingsChanged += value; } remove { s_displaySettingsChanged -= value; } }
        public static event System.EventHandler? DisplaySettingsChanging { add { Started(); s_displaySettingsChanging += value; } remove { s_displaySettingsChanging -= value; } }

        // Never raised on any platform: the real one fires it from a process-exit path that does not
        // run reliably either, which is why it is obsolete upstream.
        [System.Obsolete("SystemEvents.EventsThreadShutdown callbacks are not run before the process exits. Use AppDomain.ProcessExit instead.", DiagnosticId = "SYSLIB0059", UrlFormat = "https://aka.ms/dotnet-warnings/{0}")]
        public static event System.EventHandler? EventsThreadShutdown { add { } remove { } }

        public static event System.EventHandler? InstalledFontsChanged { add { Started(); s_installedFontsChanged += value; } remove { s_installedFontsChanged -= value; } }

        [System.ComponentModel.Browsable(false)]
        [System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)]
        [System.Obsolete("The LowMemory event has been deprecated and is not supported.")]
        public static event System.EventHandler? LowMemory { add { } remove { } }

        public static event System.EventHandler? PaletteChanged { add { Started(); s_paletteChanged += value; } remove { s_paletteChanged -= value; } }
        public static event PowerModeChangedEventHandler? PowerModeChanged { add { Started(); s_powerModeChanged += value; } remove { s_powerModeChanged -= value; } }
        public static event SessionEndedEventHandler? SessionEnded { add { Started(); s_sessionEnded += value; } remove { s_sessionEnded -= value; } }
        public static event SessionEndingEventHandler? SessionEnding { add { Started(); s_sessionEnding += value; } remove { s_sessionEnding -= value; } }
        public static event SessionSwitchEventHandler? SessionSwitch { add { Started(); s_sessionSwitch += value; } remove { s_sessionSwitch -= value; } }
        public static event System.EventHandler? TimeChanged { add { Started(); s_timeChanged += value; } remove { s_timeChanged -= value; } }
        public static event TimerElapsedEventHandler? TimerElapsed { add { Started(); s_timerElapsed += value; } remove { s_timerElapsed -= value; } }
        public static event UserPreferenceChangedEventHandler? UserPreferenceChanged { add { Started(); s_userPreferenceChanged += value; } remove { s_userPreferenceChanged -= value; } }
        public static event UserPreferenceChangingEventHandler? UserPreferenceChanging { add { Started(); s_userPreferenceChanging += value; } remove { s_userPreferenceChanging -= value; } }

        // ---- raised from the pump thread (Windows only) --------------------------------------
        //
        // One subscriber that throws must not take down the pump, nor stop the others from being
        // told: the real implementation is equally defensive, for the same reason.
        private static void Safe(System.Action a) { try { a(); } catch { } }

        internal static void RaiseDisplaySettingsChanged()
        {
            Safe(() => s_displaySettingsChanging?.Invoke(null, System.EventArgs.Empty));
            Safe(() => s_displaySettingsChanged?.Invoke(null, System.EventArgs.Empty));
        }
        internal static void RaiseInstalledFontsChanged() => Safe(() => s_installedFontsChanged?.Invoke(null, System.EventArgs.Empty));
        internal static void RaisePaletteChanged() => Safe(() => s_paletteChanged?.Invoke(null, System.EventArgs.Empty));
        internal static void RaiseTimeChanged() => Safe(() => s_timeChanged?.Invoke(null, System.EventArgs.Empty));
        internal static void RaisePowerMode(PowerModes mode) => Safe(() => s_powerModeChanged?.Invoke(null, new PowerModeChangedEventArgs(mode)));
        internal static void RaiseSessionEnded(SessionEndReasons r) => Safe(() => s_sessionEnded?.Invoke(null, new SessionEndedEventArgs(r)));
        internal static void RaiseSessionSwitch(SessionSwitchReason r) => Safe(() => s_sessionSwitch?.Invoke(null, new SessionSwitchEventArgs(r)));
        internal static void RaiseTimerElapsed(System.IntPtr id) => Safe(() => s_timerElapsed?.Invoke(null, new TimerElapsedEventArgs(id)));

        internal static void RaiseUserPreference(UserPreferenceCategory category)
        {
            Safe(() => s_userPreferenceChanging?.Invoke(null, new UserPreferenceChangingEventArgs(category)));
            Safe(() => s_userPreferenceChanged?.Invoke(null, new UserPreferenceChangedEventArgs(category)));
        }

        /// <summary>Returns true if a subscriber vetoed the shutdown.</summary>
        internal static bool RaiseSessionEnding(SessionEndReasons reason)
        {
            var e = new SessionEndingEventArgs(reason);
            Safe(() => s_sessionEnding?.Invoke(null, e));
            return e.Cancel;
        }

        /// <summary>
        /// Called by PresentationFramework when the DESKTOP's light/dark preference changes on a
        /// platform that has no WM_SETTINGCHANGE: macOS (NSAppearance) and Linux (the
        /// xdg-desktop-portal colour-scheme signal). Both detections already exist in the platform
        /// layer, with their fallbacks and their watchers; this is only the notification crossing
        /// over, so that an app subscribing to UserPreferenceChanged hears about a theme switch
        /// everywhere rather than on Windows alone.
        ///
        /// Category.Color, because that is what Windows reports for the same user action: toggling
        /// Dark/Light broadcasts WM_SETTINGCHANGE with "ImmersiveColorSet", which maps to Color.
        /// (VisualStyle is a different action there -- changing the visual style itself.)
        /// </summary>
        internal static void NotifySystemAppearanceChanged()
        {
            if (s_onWindows) return;      // the message pump already reports this, and would duplicate it
            RaiseUserPreference(UserPreferenceCategory.Color);
        }

        // ---- the events thread ---------------------------------------------------------------
        //
        // On Windows these are the real thing: a WM_TIMER on the message window, and a post to that
        // window's queue. Off Windows there is no such thread, so CreateTimer returns a null handle,
        // KillTimer does nothing, and InvokeOnEventsThread runs the delegate inline, which is the
        // best available approximation of "on some other thread, soon".
        public static System.IntPtr CreateTimer(int interval) =>
            s_onWindows ? WindowsSystemEvents.CreateTimer(interval) : System.IntPtr.Zero;

        public static void KillTimer(System.IntPtr timerId)
        {
            if (s_onWindows) WindowsSystemEvents.KillTimerCore(timerId);
        }

        public static void InvokeOnEventsThread(System.Delegate method)
        {
            if (method is null) return;
            if (s_onWindows && WindowsSystemEvents.TryInvokeOnEventsThread(method)) return;
            method.DynamicInvoke();
        }
    }

    public delegate void PowerModeChangedEventHandler(object sender, PowerModeChangedEventArgs e);
    public delegate void SessionEndedEventHandler(object sender, SessionEndedEventArgs e);
    public delegate void SessionEndingEventHandler(object sender, SessionEndingEventArgs e);
    public delegate void SessionSwitchEventHandler(object sender, SessionSwitchEventArgs e);
    public delegate void TimerElapsedEventHandler(object sender, TimerElapsedEventArgs e);
    public delegate void UserPreferenceChangedEventHandler(object sender, UserPreferenceChangedEventArgs e);
    public delegate void UserPreferenceChangingEventHandler(object sender, UserPreferenceChangingEventArgs e);

    public enum PowerModes { Resume = 1, StatusChange = 2, Suspend = 3 }
    public enum SessionEndReasons { Logoff = 1, SystemShutdown = 2 }
    public enum SessionSwitchReason
    {
        ConsoleConnect = 1, ConsoleDisconnect = 2, RemoteConnect = 3, RemoteDisconnect = 4,
        SessionLogon = 5, SessionLogoff = 6, SessionLock = 7, SessionUnlock = 8, SessionRemoteControl = 9,
    }
    public enum UserPreferenceCategory
    {
        Accessibility = 1, Color = 2, Desktop = 3, General = 4, Icon = 5, Keyboard = 6, Menu = 7,
        Mouse = 8, Policy = 9, Power = 10, Screensaver = 11, Window = 12, Locale = 13, VisualStyle = 14,
    }

    public class PowerModeChangedEventArgs : System.EventArgs
    {
        public PowerModeChangedEventArgs(PowerModes mode) { Mode = mode; }
        public PowerModes Mode { get; }
    }
    public class SessionEndedEventArgs : System.EventArgs
    {
        public SessionEndedEventArgs(SessionEndReasons reason) { Reason = reason; }
        public SessionEndReasons Reason { get; }
    }
    public class SessionEndingEventArgs : System.EventArgs
    {
        public SessionEndingEventArgs(SessionEndReasons reason) { Reason = reason; }
        public bool Cancel { get; set; }
        public SessionEndReasons Reason { get; }
    }
    public class SessionSwitchEventArgs : System.EventArgs
    {
        public SessionSwitchEventArgs(SessionSwitchReason reason) { Reason = reason; }
        public SessionSwitchReason Reason { get; }
    }
    public class TimerElapsedEventArgs : System.EventArgs
    {
        public TimerElapsedEventArgs(System.IntPtr timerId) { TimerId = timerId; }
        public System.IntPtr TimerId { get; }
    }
    public class UserPreferenceChangedEventArgs : System.EventArgs
    {
        public UserPreferenceChangedEventArgs(UserPreferenceCategory category) { Category = category; }
        public UserPreferenceCategory Category { get; }
    }
    public class UserPreferenceChangingEventArgs : System.EventArgs
    {
        public UserPreferenceChangingEventArgs(UserPreferenceCategory category) { Category = category; }
        public UserPreferenceCategory Category { get; }
    }
}
