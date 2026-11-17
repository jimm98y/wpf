// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// Microsoft.Win32.SystemEvents stand-in, with TWO implementations behind one public surface:
//
//   Windows      a real message-only window on its own pump thread, raising these events from the
//                OS broadcasts (see WindowsSystemEvents.cs)
//   elsewhere    an events thread of the same kind, raising them from what the system says
//                (see UnixSystemEvents.cs): the clock and time zone, sleep and the power source, the
//                session's lock, shutdown and logoff, the displays, the installed fonts
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
        // thread for one. Off Windows the system's own notices (UnixSystemEvents: the clock, power,
        // the session's lock, displays, fonts) raise the same events, from an events thread of the
        // same kind, started the same way.
        private static void Started()
        {
            if (s_onWindows) WindowsSystemEvents.Ensure();
            else UnixSystemEvents.Ensure();
        }

        // Each handler with the synchronization context it was added from, as .NET keeps it
        // (AsyncOperationManager's: the thread's own, a plain one where it has none): a WinForms or WPF handler runs on
        // its UI thread, Sent there from the events thread, rather than touching its controls from a thread of ours.
        private sealed class Handlers
        {
            private readonly System.Collections.Generic.List<(System.Delegate Handler, System.Threading.SynchronizationContext? Context)> _list = new();

            public void Add(System.Delegate? handler)
            {
                if (handler is null) return;
                Started();
                var context = System.Threading.SynchronizationContext.Current;
                lock (_list) _list.Add((handler, context));
            }

            public void Remove(System.Delegate? handler)
            {
                if (handler is null) return;
                lock (_list)
                {
                    int i = _list.FindIndex(e => e.Handler.Equals(handler));
                    if (i >= 0) _list.RemoveAt(i);
                }
            }

            // One subscriber that throws must not take down the events thread, nor stop the others from being told.
            public void Raise(params object[] args)
            {
                (System.Delegate Handler, System.Threading.SynchronizationContext? Context)[] snapshot;
                lock (_list) snapshot = _list.ToArray();
                foreach (var (handler, context) in snapshot)
                {
                    void Call(object? _) { try { handler.DynamicInvoke(args); } catch { } }
                    if (context is null) { Call(null); continue; }
                    // a context whose thread has gone: called here, as .NET calls it
                    try { context.Send(Call, null); }
                    catch (System.ComponentModel.InvalidAsynchronousStateException) { Call(null); }
                    catch { }
                }
            }
        }

        private static readonly Handlers s_displaySettingsChanged = new(), s_displaySettingsChanging = new(), s_installedFontsChanged = new(),
            s_paletteChanged = new(), s_timeChanged = new(), s_powerModeChanged = new(), s_sessionEnded = new(), s_sessionEnding = new(),
            s_sessionSwitch = new(), s_timerElapsed = new(), s_userPreferenceChanged = new(), s_userPreferenceChanging = new();

        public static event System.EventHandler? DisplaySettingsChanged { add => s_displaySettingsChanged.Add(value); remove => s_displaySettingsChanged.Remove(value); }
        public static event System.EventHandler? DisplaySettingsChanging { add => s_displaySettingsChanging.Add(value); remove => s_displaySettingsChanging.Remove(value); }

        // Never raised on any platform: the real one fires it from a process-exit path that does not
        // run reliably either, which is why it is obsolete upstream.
        [System.Obsolete("SystemEvents.EventsThreadShutdown callbacks are not run before the process exits. Use AppDomain.ProcessExit instead.", DiagnosticId = "SYSLIB0059", UrlFormat = "https://aka.ms/dotnet-warnings/{0}")]
        public static event System.EventHandler? EventsThreadShutdown { add { } remove { } }

        public static event System.EventHandler? InstalledFontsChanged { add => s_installedFontsChanged.Add(value); remove => s_installedFontsChanged.Remove(value); }

        [System.ComponentModel.Browsable(false)]
        [System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)]
        [System.Obsolete("The LowMemory event has been deprecated and is not supported.")]
        public static event System.EventHandler? LowMemory { add { } remove { } }

        public static event System.EventHandler? PaletteChanged { add => s_paletteChanged.Add(value); remove => s_paletteChanged.Remove(value); }
        public static event PowerModeChangedEventHandler? PowerModeChanged { add => s_powerModeChanged.Add(value); remove => s_powerModeChanged.Remove(value); }
        public static event SessionEndedEventHandler? SessionEnded { add => s_sessionEnded.Add(value); remove => s_sessionEnded.Remove(value); }
        public static event SessionEndingEventHandler? SessionEnding { add => s_sessionEnding.Add(value); remove => s_sessionEnding.Remove(value); }
        public static event SessionSwitchEventHandler? SessionSwitch { add => s_sessionSwitch.Add(value); remove => s_sessionSwitch.Remove(value); }
        public static event System.EventHandler? TimeChanged { add => s_timeChanged.Add(value); remove => s_timeChanged.Remove(value); }
        public static event TimerElapsedEventHandler? TimerElapsed { add => s_timerElapsed.Add(value); remove => s_timerElapsed.Remove(value); }
        public static event UserPreferenceChangedEventHandler? UserPreferenceChanged { add => s_userPreferenceChanged.Add(value); remove => s_userPreferenceChanged.Remove(value); }
        public static event UserPreferenceChangingEventHandler? UserPreferenceChanging { add => s_userPreferenceChanging.Add(value); remove => s_userPreferenceChanging.Remove(value); }

        // ---- raised from the events thread ----------------------------------------------------

        internal static void RaiseDisplaySettingsChanged()
        {
            s_displaySettingsChanging.Raise(null!, System.EventArgs.Empty);
            s_displaySettingsChanged.Raise(null!, System.EventArgs.Empty);
        }
        internal static void RaiseInstalledFontsChanged() => s_installedFontsChanged.Raise(null!, System.EventArgs.Empty);
        internal static void RaisePaletteChanged() => s_paletteChanged.Raise(null!, System.EventArgs.Empty);
        internal static void RaiseTimeChanged() => s_timeChanged.Raise(null!, System.EventArgs.Empty);
        internal static void RaisePowerMode(PowerModes mode) => s_powerModeChanged.Raise(null!, new PowerModeChangedEventArgs(mode));
        internal static void RaiseSessionEnded(SessionEndReasons r) => s_sessionEnded.Raise(null!, new SessionEndedEventArgs(r));
        internal static void RaiseSessionSwitch(SessionSwitchReason r) => s_sessionSwitch.Raise(null!, new SessionSwitchEventArgs(r));
        internal static void RaiseTimerElapsed(System.IntPtr id) => s_timerElapsed.Raise(null!, new TimerElapsedEventArgs(id));

        internal static void RaiseUserPreference(UserPreferenceCategory category)
        {
            s_userPreferenceChanging.Raise(null!, new UserPreferenceChangingEventArgs(category));
            s_userPreferenceChanged.Raise(null!, new UserPreferenceChangedEventArgs(category));
        }

        /// <summary>Returns true if a subscriber vetoed the shutdown.</summary>
        internal static bool RaiseSessionEnding(SessionEndReasons reason)
        {
            var e = new SessionEndingEventArgs(reason);
            s_sessionEnding.Raise(null!, e);
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
            UnixSystemEvents.Post(() => RaiseUserPreference(UserPreferenceCategory.Color));
        }

        // ---- the events thread ---------------------------------------------------------------
        //
        // On Windows these are the real thing: a WM_TIMER on the message window, and a post to that
        // window's queue. Elsewhere the events thread is UnixSystemEvents', its timers and its queue.
        // the exceptions .NET's own throws
#pragma warning disable CA2201
        public static System.IntPtr CreateTimer(int interval)
        {
            if (interval <= 0)
                throw new System.ArgumentException($"Invalid value '{interval}' for parameter 'interval'. 'interval' must be greater than 0.");
            System.IntPtr id = s_onWindows ? WindowsSystemEvents.CreateTimer(interval) : UnixSystemEvents.CreateTimer(interval);
            if (id == System.IntPtr.Zero) throw new System.Runtime.InteropServices.ExternalException("Cannot create timer.");
            return id;
        }

        public static void KillTimer(System.IntPtr timerId)
        {
            if (s_onWindows) WindowsSystemEvents.KillTimerCore(timerId);
            else if (!UnixSystemEvents.KillTimer(timerId)) throw new System.Runtime.InteropServices.ExternalException("Cannot end timer.");
        }
#pragma warning restore CA2201

        // asynchronously, on the events thread, as a post to the events window runs it
        public static void InvokeOnEventsThread(System.Delegate method)
        {
            if (method is null) return;
            if (s_onWindows)
            {
                if (!WindowsSystemEvents.TryInvokeOnEventsThread(method)) method.DynamicInvoke();
                return;
            }
            UnixSystemEvents.Ensure();
            UnixSystemEvents.Post(() => { try { method.DynamicInvoke(); } catch { } });
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
