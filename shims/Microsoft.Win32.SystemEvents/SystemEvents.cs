// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// Cross-platform shim for Microsoft.Win32.SystemEvents. The real runtime package assembly is
// Windows-only: every member throws PlatformNotSupportedException off-Windows (it relies on a hidden
// message window listening for WM_SETTINGCHANGE / WM_DISPLAYCHANGE / WM_POWERBROADCAST / etc.). WPF apps
// routinely subscribe to SystemEvents (e.g. UserPreferenceChanged for light/dark theme reaction), so the
// WebGPU WPF fork ships this drop-in replacement (same assembly identity + public API) that the SDK
// bundles for the non-Windows heads. The system-notification events simply never fire off-Windows (there
// is no OS setting-change broadcast to observe from managed code), so subscribing/unsubscribing is a
// harmless no-op instead of a crash. On Windows the real WindowsDesktop package is used unchanged.

namespace Microsoft.Win32
{
    public sealed class SystemEvents
    {
        internal SystemEvents() { }

        // System-notification events. No OS broadcast is observed off-Windows, so these never raise;
        // add/remove are no-ops (rather than storing handlers that could never be invoked).
        public static event System.EventHandler? DisplaySettingsChanged { add { } remove { } }
        public static event System.EventHandler? DisplaySettingsChanging { add { } remove { } }
        [System.Obsolete("SystemEvents.EventsThreadShutdown callbacks are not run before the process exits. Use AppDomain.ProcessExit instead.", DiagnosticId = "SYSLIB0059", UrlFormat = "https://aka.ms/dotnet-warnings/{0}")]
        public static event System.EventHandler? EventsThreadShutdown { add { } remove { } }
        public static event System.EventHandler? InstalledFontsChanged { add { } remove { } }
        [System.ComponentModel.Browsable(false)]
        [System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)]
        [System.Obsolete("The LowMemory event has been deprecated and is not supported.")]
        public static event System.EventHandler? LowMemory { add { } remove { } }
        public static event System.EventHandler? PaletteChanged { add { } remove { } }
        public static event PowerModeChangedEventHandler? PowerModeChanged { add { } remove { } }
        public static event SessionEndedEventHandler? SessionEnded { add { } remove { } }
        public static event SessionEndingEventHandler? SessionEnding { add { } remove { } }
        public static event SessionSwitchEventHandler? SessionSwitch { add { } remove { } }
        public static event System.EventHandler? TimeChanged { add { } remove { } }
        public static event TimerElapsedEventHandler? TimerElapsed { add { } remove { } }
        public static event UserPreferenceChangedEventHandler? UserPreferenceChanged { add { } remove { } }
        public static event UserPreferenceChangingEventHandler? UserPreferenceChanging { add { } remove { } }

        // No SystemEvents worker thread/timer off-Windows. CreateTimer returns a null handle;
        // KillTimer is a no-op. InvokeOnEventsThread runs the delegate inline (best effort) since there
        // is no dedicated events thread to marshal onto.
        public static System.IntPtr CreateTimer(int interval) => System.IntPtr.Zero;
        public static void KillTimer(System.IntPtr timerId) { }
        public static void InvokeOnEventsThread(System.Delegate method) => method?.DynamicInvoke();
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
