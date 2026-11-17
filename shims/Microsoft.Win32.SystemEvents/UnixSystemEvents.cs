// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// SystemEvents off Windows: what the system says, turned into the broadcasts Windows sends its events window, and
// raised from an events thread of the same kind (a queue its own thread works through, where CreateTimer's timers
// fire and InvokeOnEventsThread runs). Started by the first subscription, as the real one's window is.
//
//   the clock      the wall clock checked against the monotonic one each second: a jump is the clock set
//                  (WM_TIMECHANGE); a new time zone (TZ, /etc/localtime) likewise, the cached zone dropped first
//   sleep          macOS: IOKit's system power notifications (the sleep held until the subscribers have heard of
//                  it, as Windows gives them PBT_APMSUSPEND); Linux: logind's PrepareForSleep, under a delay
//                  inhibitor taken for the same reason; elsewhere a resume seen afterwards, as the clock that
//                  counts sleep runs ahead of the one that does not
//   power source   macOS: IOKit's power source notification; Linux: /sys/class/power_supply, read every few
//                  seconds (PBT_APMPOWERSTATUSCHANGE)
//   the session    macOS: the screen's lock and unlock, the session switched to and from (the window server's
//                  session, read every second); Linux: logind -- the session's
//                  LockedHint (SessionLock, SessionUnlock), the system's PrepareForShutdown (SessionEnding, then
//                  SessionEnded, shutdown) and the session's removal (logoff)
//   displays       macOS: the displays' arrangement and modes, read every two seconds; Linux: the compositor's
//                  outputs, as the Wayland layer reports them (WM_DISPLAYCHANGE)
//   fonts          the font folders watched (WM_FONTCHANGE)

using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;

namespace Microsoft.Win32
{
    internal static unsafe class UnixSystemEvents
    {
        private static readonly object s_gate = new object();
        private static bool s_started;
        private static readonly BlockingCollection<System.Action> s_queue = new BlockingCollection<System.Action>();
        private static Thread? s_thread;

        internal static void Ensure()
        {
            lock (s_gate)
            {
                if (s_started) return;
                s_started = true;
                s_thread = new Thread(() =>
                {
                    foreach (System.Action a in s_queue.GetConsumingEnumerable())
                    {
                        try { a(); } catch { }
                    }
                }) { IsBackground = true, Name = ".NET System Events" };
                s_thread.Start();
                StartMonitors();
            }
        }

        /// <summary>Runs <paramref name="action"/> on the events thread.</summary>
        internal static void Post(System.Action action)
        {
            if (!s_started) { try { action(); } catch { } return; }
            s_queue.Add(action);
        }

        // whether the caller is the events thread (a raise that waits for itself would wait for ever)
        private static bool OnEventsThread => Thread.CurrentThread == s_thread;

        // a raise its caller waits for: the subscribers have heard of it when this returns (or the time is up)
        private static void PostAndWait(System.Action action, int timeoutMs)
        {
            if (OnEventsThread) { try { action(); } catch { } return; }
            using var done = new ManualResetEventSlim();
            Post(() => { try { action(); } finally { done.Set(); } });
            done.Wait(timeoutMs);
        }

        // ---- timers ----------------------------------------------------------------------------------------------

        private static readonly Dictionary<long, Timer> s_timers = new Dictionary<long, Timer>();
        private static long s_nextTimer;

        internal static System.IntPtr CreateTimer(int interval)
        {
            Ensure();
            long id = Interlocked.Increment(ref s_nextTimer);
            var timer = new Timer(_ => Post(() => { lock (s_timers) if (!s_timers.ContainsKey(id)) return; SystemEvents.RaiseTimerElapsed((System.IntPtr)id); }));
            lock (s_timers) s_timers[id] = timer;
            timer.Change(interval, interval);
            return (System.IntPtr)id;
        }

        internal static bool KillTimer(System.IntPtr id)
        {
            Timer? timer;
            lock (s_timers)
            {
                if (!s_timers.Remove((long)id, out timer)) return false;
            }
            timer.Dispose();
            return true;
        }

        // ---- the monitors ----------------------------------------------------------------------------------------

        private static void StartMonitors()
        {
            bool sleepWatched = false;
            if (System.OperatingSystem.IsMacOS()) sleepWatched = Mac.Start();
            else if (System.OperatingSystem.IsLinux() && !System.OperatingSystem.IsAndroid())
            {
                sleepWatched = Logind.Start();
                StartPowerSupplyPoll();
                WaylandOutputs.Hook();
            }
            new Thread(() => ClockLoop(reportResume: !sleepWatched)) { IsBackground = true, Name = "system events: clock" }.Start();
            StartFontWatch();
        }

        // ---- the clock -------------------------------------------------------------------------------------------

        /// <summary>The clocks a second's check compares: the wall clock, one that counts sleep and one that does
        /// not (both in ticks).</summary>
        internal readonly record struct ClockSample(long Wall, long WithSleep, long WithoutSleep, string Zone);

        /// <summary>What changed between two samples: the clock set (beyond a second's drift), a time zone, a sleep
        /// (of more than two seconds).</summary>
        internal static (bool ClockSet, bool ZoneChanged, bool Slept) Compare(ClockSample a, ClockSample b)
        {
            long ran = b.WithSleep - a.WithSleep, slept = ran - (b.WithoutSleep - a.WithoutSleep);
            bool set = System.Math.Abs((b.Wall - a.Wall) - ran) > System.TimeSpan.TicksPerSecond;
            return (set, a.Zone != b.Zone, slept > 2 * System.TimeSpan.TicksPerSecond);
        }

        internal static ClockSample Sample() => new ClockSample(System.DateTime.UtcNow.Ticks, ClockWithSleep(), ClockWithoutSleep(), ZoneSignature());

        private static void ClockLoop(bool reportResume)
        {
            ClockSample last = Sample();
            while (true)
            {
                Thread.Sleep(1000);
                ClockSample now = Sample();
                var (set, zone, slept) = Compare(last, now);
                last = now;
                if (zone) System.TimeZoneInfo.ClearCachedData();
                if (set || zone) Post(SystemEvents.RaiseTimeChanged);
                if (slept && reportResume) Post(() => SystemEvents.RaisePowerMode(PowerModes.Resume));
            }
        }

        // the time zone as the system names it: TZ, else what /etc/localtime is (its target, its contents' time)
        private static string ZoneSignature()
        {
            string tz = System.Environment.GetEnvironmentVariable("TZ") ?? "";
            try
            {
                var info = new FileInfo("/etc/localtime");
                string target = info.LinkTarget ?? "";
                return tz + "|" + target + "|" + (info.Exists ? info.LastWriteTimeUtc.Ticks : 0);
            }
            catch (System.Exception) { return tz; }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct Timespec { public long Seconds, Nanoseconds; }

        [DllImport("libc", EntryPoint = "clock_gettime")]
        private static extern int clock_gettime(int clock, out Timespec ts);

        [DllImport("/usr/lib/libSystem.dylib")]
        private static extern ulong mach_continuous_time();

        [DllImport("/usr/lib/libSystem.dylib")]
        private static extern ulong mach_absolute_time();

        [StructLayout(LayoutKind.Sequential)]
        private struct MachTimebase { public uint Numer, Denom; }

        [DllImport("/usr/lib/libSystem.dylib")]
        private static extern int mach_timebase_info(out MachTimebase info);

        private static readonly MachTimebase s_timebase = System.OperatingSystem.IsMacOS() && mach_timebase_info(out var tb) == 0 ? tb : new MachTimebase { Numer = 1, Denom = 1 };

        private static long MachTicks(ulong t) => (long)((System.UInt128)t * s_timebase.Numer / s_timebase.Denom / 100);

        // Linux: CLOCK_BOOTTIME counts sleep, CLOCK_MONOTONIC does not; macOS: mach_continuous_time does,
        // mach_absolute_time does not
        private static long ClockWithSleep()
        {
            if (System.OperatingSystem.IsMacOS()) return MachTicks(mach_continuous_time());
            if (System.OperatingSystem.IsLinux() && clock_gettime(7, out Timespec ts) == 0) return ts.Seconds * System.TimeSpan.TicksPerSecond + ts.Nanoseconds / 100;
            return Stopwatch.GetTimestamp() * System.TimeSpan.TicksPerSecond / Stopwatch.Frequency;
        }

        private static long ClockWithoutSleep()
        {
            if (System.OperatingSystem.IsMacOS()) return MachTicks(mach_absolute_time());
            if (System.OperatingSystem.IsLinux() && clock_gettime(1, out Timespec ts) == 0) return ts.Seconds * System.TimeSpan.TicksPerSecond + ts.Nanoseconds / 100;
            return Stopwatch.GetTimestamp() * System.TimeSpan.TicksPerSecond / Stopwatch.Frequency;
        }

        // ---- fonts -----------------------------------------------------------------------------------------------

        private static readonly List<FileSystemWatcher> s_fontWatchers = new List<FileSystemWatcher>();
        private static Timer? s_fontsSettle;

        internal static IEnumerable<string> FontFolders()
        {
            string home = System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile);
            if (System.OperatingSystem.IsMacOS())
                return new[] { "/Library/Fonts", Path.Combine(home, "Library", "Fonts") };
            string data = System.Environment.GetEnvironmentVariable("XDG_DATA_HOME") is { Length: > 0 } d ? d : Path.Combine(home, ".local", "share");
            return new[] { "/usr/share/fonts", "/usr/local/share/fonts", Path.Combine(data, "fonts"), Path.Combine(home, ".fonts") };
        }

        private static void StartFontWatch()
        {
            foreach (string folder in FontFolders().Where(Directory.Exists))
            {
                try
                {
                    var w = new FileSystemWatcher(folder) { IncludeSubdirectories = true, NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite };
                    w.Created += (_, _) => FontsTouched();
                    w.Deleted += (_, _) => FontsTouched();
                    w.Renamed += (_, _) => FontsTouched();
                    w.Changed += (_, _) => FontsTouched();
                    w.EnableRaisingEvents = true;
                    s_fontWatchers.Add(w);
                }
                catch (System.Exception) { }
            }
        }

        // an install copies a family's files one by one: one broadcast once they have settled
        private static void FontsTouched()
        {
            lock (s_fontWatchers)
            {
                s_fontsSettle ??= new Timer(_ => Post(SystemEvents.RaiseInstalledFontsChanged));
                s_fontsSettle.Change(1000, Timeout.Infinite);
            }
        }

        // ---- the power source (Linux) ----------------------------------------------------------------------------

        private static void StartPowerSupplyPoll()
        {
            const string root = "/sys/class/power_supply";
            if (!Directory.Exists(root)) return;
            static string Read()
            {
                try
                {
                    return string.Join(";", Directory.GetDirectories(root).OrderBy(d => d, System.StringComparer.Ordinal).Select(d =>
                    {
                        string Get(string f) { try { return File.ReadAllText(Path.Combine(d, f)).Trim(); } catch (System.Exception) { return ""; } }
                        return Path.GetFileName(d) + "=" + Get("online") + Get("status");
                    }));
                }
                catch (System.Exception) { return ""; }
            }
            string last = Read();
            new Thread(() =>
            {
                while (true)
                {
                    Thread.Sleep(5000);
                    string now = Read();
                    if (now != last) Post(() => SystemEvents.RaisePowerMode(PowerModes.StatusChange));
                    last = now;
                }
            }) { IsBackground = true, Name = "system events: power source" }.Start();
        }

        // ---- the compositor's outputs (Linux) ---------------------------------------------------------------------

        // The Wayland layer (WindowsBase) knows the outputs; it is not loaded by a process with no windows, nor made
        // to be: hooked once it is.
        private static class WaylandOutputs
        {
            private static int s_hooked;

            public static void Hook()
            {
                System.AppDomain.CurrentDomain.AssemblyLoad += (_, e) => TryHook(e.LoadedAssembly);
                foreach (var a in System.AppDomain.CurrentDomain.GetAssemblies()) TryHook(a);
            }

            private static void TryHook(System.Reflection.Assembly a)
            {
                if (a.GetName().Name != "WindowsBase" || Volatile.Read(ref s_hooked) != 0) return;
                var changed = a.GetType("MS.Internal.Interop.Wayland.WaylandWindow")?.GetEvent("ScreensChanged");
                if (changed == null || Interlocked.Exchange(ref s_hooked, 1) != 0) return;
                try { changed.AddEventHandler(null, new System.Action(() => Post(SystemEvents.RaiseDisplaySettingsChanged))); }
                catch (System.Exception) { }
            }
        }

        // ---- macOS -----------------------------------------------------------------------------------------------

        private static class Mac
        {
            private const string IOKit = "/System/Library/Frameworks/IOKit.framework/IOKit";
            private const string CF = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
            private const string CG = "/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics";

            private const uint MessageCanSystemSleep = 0xE0000270, MessageSystemWillSleep = 0xE0000280,
                MessageSystemHasPoweredOn = 0xE0000300;

            [DllImport(IOKit)] private static extern uint IORegisterForSystemPower(nint refcon, out nint port, delegate* unmanaged<nint, uint, uint, nint, void> callback, out uint notifier);
            [DllImport(IOKit)] private static extern nint IONotificationPortGetRunLoopSource(nint port);
            [DllImport(IOKit)] private static extern int IOAllowPowerChange(uint kernelPort, nint notificationId);
            [DllImport(IOKit)] private static extern nint IOPSNotificationCreateRunLoopSource(delegate* unmanaged<nint, void> callback, nint context);
            [DllImport(CF)] private static extern nint CFRunLoopGetCurrent();
            [DllImport(CF)] private static extern void CFRunLoopAddSource(nint loop, nint source, nint mode);
            [DllImport(CF)] private static extern void CFRunLoopRun();
            [DllImport(CF)] private static extern nint CFDictionaryGetValue(nint dictionary, nint key);
            [DllImport(CF)] private static extern byte CFBooleanGetValue(nint boolean);
            [DllImport(CF)] private static extern void CFRelease(nint obj);
            [DllImport(CG)] private static extern nint CGSessionCopyCurrentDictionary();
            [DllImport(CF)] private static extern nint CFStringCreateWithCString(nint allocator, byte* text, uint encoding);
            [DllImport(CG)] private static extern int CGGetActiveDisplayList(uint max, uint* displays, out uint count);
            [DllImport(CG)] private static extern CGRect CGDisplayBounds(uint display);
            [DllImport(CG)] private static extern nint CGDisplayPixelsWide(uint display);
            [DllImport(CG)] private static extern nint CGDisplayPixelsHigh(uint display);

            [StructLayout(LayoutKind.Sequential)] private struct CGRect { public double X, Y, Width, Height; }

            private static uint s_rootPort;

            public static bool Start()
            {
                bool power = false;
                using var ready = new ManualResetEventSlim();
                new Thread(() =>
                {
                    nint loop = CFRunLoopGetCurrent();
                    nint mode = DefaultMode();
                    try
                    {
                        s_rootPort = IORegisterForSystemPower(0, out nint port, &OnPower, out _);
                        if (s_rootPort != 0) { CFRunLoopAddSource(loop, IONotificationPortGetRunLoopSource(port), mode); power = true; }
                        nint ps = IOPSNotificationCreateRunLoopSource(&OnPowerSource, 0);
                        if (ps != 0) CFRunLoopAddSource(loop, ps, mode);
                    }
                    catch (System.Exception) { }
                    ready.Set();
                    CFRunLoopRun();
                }) { IsBackground = true, Name = "system events: macOS notices" }.Start();
                ready.Wait(5000);
                new Thread(SessionAndDisplayLoop) { IsBackground = true, Name = "system events: session and displays" }.Start();
                return power;
            }

            private static nint DefaultMode()
            {
                nint lib = NativeLibrary.Load(CF);
                return *(nint*)NativeLibrary.GetExport(lib, "kCFRunLoopDefaultMode");
            }

            internal static nint CFString(string s)
            {
                byte[] utf8 = System.Text.Encoding.UTF8.GetBytes(s + "\0");
                fixed (byte* p = utf8) return CFStringCreateWithCString(0, p, 0x08000100);     // kCFStringEncodingUTF8
            }

            [UnmanagedCallersOnly]
            private static void OnPower(nint refcon, uint service, uint message, nint argument)
            {
                switch (message)
                {
                    case MessageCanSystemSleep:
                        IOAllowPowerChange(s_rootPort, argument);
                        break;
                    case MessageSystemWillSleep:
                        // the subscribers hear of it before the system sleeps, as PBT_APMSUSPEND reaches them first
                        PostAndWait(() => SystemEvents.RaisePowerMode(PowerModes.Suspend), 20_000);
                        IOAllowPowerChange(s_rootPort, argument);
                        break;
                    case MessageSystemHasPoweredOn:
                        Post(() => SystemEvents.RaisePowerMode(PowerModes.Resume));
                        break;
                }
            }

            [UnmanagedCallersOnly]
            private static void OnPowerSource(nint context) => Post(() => SystemEvents.RaisePowerMode(PowerModes.StatusChange));

            internal static string DisplaySignature()
            {
                uint* ids = stackalloc uint[32];
                if (CGGetActiveDisplayList(32, ids, out uint count) != 0) return "";
                var parts = new List<string>();
                for (int i = 0; i < count; i++)
                {
                    CGRect r = CGDisplayBounds(ids[i]);
                    parts.Add($"{ids[i]}:{r.X},{r.Y},{r.Width},{r.Height}:{CGDisplayPixelsWide(ids[i])}x{CGDisplayPixelsHigh(ids[i])}");
                }
                return string.Join(";", parts);
            }

            private static readonly nint s_lockedKey = CFString("CGSSessionScreenIsLocked"), s_onConsoleKey = CFString("kCGSSessionOnConsoleKey");

            /// <summary>The session as the window server describes it: whether its screen is locked, whether it is the
            /// one on the console (not switched away from); null without a window server session.</summary>
            internal static (bool Locked, bool OnConsole)? Session()
            {
                nint d = CGSessionCopyCurrentDictionary();
                if (d == 0) return null;
                try
                {
                    nint locked = CFDictionaryGetValue(d, s_lockedKey), console = CFDictionaryGetValue(d, s_onConsoleKey);
                    return (locked != 0 && CFBooleanGetValue(locked) != 0, console == 0 || CFBooleanGetValue(console) != 0);
                }
                finally { CFRelease(d); }
            }

            // The screen's lock is told by distributed notifications only on the main thread's run loop, which a
            // service or a console program does not run: the session is read every second instead, as the displays are
            // every other (their arrangement, sizes and modes)
            private static void SessionAndDisplayLoop()
            {
                string lastDisplays;
                (bool Locked, bool OnConsole)? lastSession;
                try { lastDisplays = DisplaySignature(); lastSession = Session(); } catch (System.Exception) { return; }
                for (int tick = 1; ; tick++)
                {
                    Thread.Sleep(1000);
                    var session = Session();
                    if (session is { } now && lastSession is { } was)
                    {
                        if (now.OnConsole != was.OnConsole)
                            Post(() => SystemEvents.RaiseSessionSwitch(now.OnConsole ? SessionSwitchReason.ConsoleConnect : SessionSwitchReason.ConsoleDisconnect));
                        if (now.Locked != was.Locked)
                            Post(() => SystemEvents.RaiseSessionSwitch(now.Locked ? SessionSwitchReason.SessionLock : SessionSwitchReason.SessionUnlock));
                    }
                    lastSession = session ?? lastSession;
                    if (tick % 2 != 0) continue;
                    string displays = DisplaySignature();
                    if (displays != lastDisplays) Post(SystemEvents.RaiseDisplaySettingsChanged);
                    lastDisplays = displays;
                }
            }
        }

        // ---- Linux: logind, on the system bus --------------------------------------------------------------------

        private static class Logind
        {
            private const string Lib = "libdbus-1.so.3";
            private const int DBUS_BUS_SYSTEM = 1;
            private const int TYPE_STRING = 's', TYPE_OBJECT_PATH = 'o', TYPE_BOOLEAN = 'b', TYPE_VARIANT = 'v', TYPE_ARRAY = 'a',
                TYPE_DICT_ENTRY = 'e', TYPE_UNIX_FD = 'h';

            [StructLayout(LayoutKind.Sequential)]
            private struct DBusError { public nint Name, Message; public uint Dummies; public nint Pad1; }

            [DllImport(Lib)] private static extern void dbus_error_init(DBusError* error);
            [DllImport(Lib)] private static extern void dbus_error_free(DBusError* error);
            [DllImport(Lib)] private static extern nint dbus_bus_get_private(int type, DBusError* error);
            [DllImport(Lib)] private static extern void dbus_connection_set_exit_on_disconnect(nint connection, int exit);
            [DllImport(Lib)] private static extern void dbus_bus_add_match(nint connection, byte* rule, DBusError* error);
            [DllImport(Lib)] private static extern int dbus_connection_read_write(nint connection, int timeout);
            [DllImport(Lib)] private static extern nint dbus_connection_pop_message(nint connection);
            [DllImport(Lib)] private static extern void dbus_message_unref(nint message);
            [DllImport(Lib)] private static extern int dbus_message_is_signal(nint message, byte* iface, byte* member);
            [DllImport(Lib)] private static extern nint dbus_message_get_path(nint message);
            [DllImport(Lib)] private static extern int dbus_message_iter_init(nint message, byte* iter);
            [DllImport(Lib)] private static extern int dbus_message_iter_get_arg_type(byte* iter);
            [DllImport(Lib)] private static extern void dbus_message_iter_get_basic(byte* iter, void* value);
            [DllImport(Lib)] private static extern int dbus_message_iter_next(byte* iter);
            [DllImport(Lib)] private static extern void dbus_message_iter_recurse(byte* iter, byte* sub);
            [DllImport(Lib)] private static extern nint dbus_message_new_method_call(byte* destination, byte* path, byte* iface, byte* method);
            [DllImport(Lib)] private static extern void dbus_message_iter_init_append(nint message, byte* iter);
            [DllImport(Lib)] private static extern int dbus_message_iter_append_basic(byte* iter, int type, void* value);
            [DllImport(Lib)] private static extern nint dbus_connection_send_with_reply_and_block(nint connection, nint message, int timeout, DBusError* error);
            [DllImport("libc")] private static extern int close(int fd);

            private const int IterSize = 128;
            private static nint s_bus;
            private static string s_session = "";
            private static int s_sleepDelay = -1, s_shutdownDelay = -1;
            private static bool s_locked;

            private static byte[] Z(string s) => System.Text.Encoding.UTF8.GetBytes(s + "\0");

            public static bool Start()
            {
                try
                {
                    DBusError e;
                    dbus_error_init(&e);
                    s_bus = dbus_bus_get_private(DBUS_BUS_SYSTEM, &e);
                    if (e.Name != 0) dbus_error_free(&e);
                    if (s_bus == 0) return false;
                    dbus_connection_set_exit_on_disconnect(s_bus, 0);
                }
                catch (System.Exception) { return false; }       // no libdbus, no system bus

                s_session = SessionPath();
                AddMatch("type='signal',sender='org.freedesktop.login1',interface='org.freedesktop.login1.Manager',member='PrepareForSleep'");
                AddMatch("type='signal',sender='org.freedesktop.login1',interface='org.freedesktop.login1.Manager',member='PrepareForShutdown'");
                AddMatch("type='signal',sender='org.freedesktop.login1',interface='org.freedesktop.login1.Manager',member='SessionRemoved'");
                if (s_session.Length > 0)
                {
                    AddMatch($"type='signal',sender='org.freedesktop.login1',interface='org.freedesktop.DBus.Properties',member='PropertiesChanged',path='{s_session}'");
                    s_locked = LockedHint();
                }
                s_sleepDelay = Inhibit("sleep");
                s_shutdownDelay = Inhibit("shutdown");
                new Thread(Loop) { IsBackground = true, Name = "system events: logind" }.Start();
                return true;
            }

            // the session this process runs in: XDG_SESSION_ID's object (logind's paths escape what is not a letter
            // or digit, and a leading digit, as _xx)
            private static string SessionPath()
            {
                string? id = System.Environment.GetEnvironmentVariable("XDG_SESSION_ID");
                if (string.IsNullOrEmpty(id)) return "";
                var sb = new System.Text.StringBuilder("/org/freedesktop/login1/session/");
                for (int i = 0; i < id.Length; i++)
                {
                    char c = id[i];
                    bool plain = (c is >= 'a' and <= 'z' or >= 'A' and <= 'Z') || (c is >= '0' and <= '9' && i > 0);
                    if (plain) sb.Append(c); else sb.Append('_').Append(((int)c).ToString("x2"));
                }
                return sb.ToString();
            }

            private static void AddMatch(string rule)
            {
                DBusError e;
                dbus_error_init(&e);
                fixed (byte* r = Z(rule)) dbus_bus_add_match(s_bus, r, &e);
                if (e.Name != 0) dbus_error_free(&e);
            }

            // a delay inhibitor (Inhibit(what, who, why, "delay")): the system waits for its descriptor to close, a few
            // seconds at most, before it sleeps or shuts down -- the subscribers' time to hear of it
            private static int Inhibit(string what)
            {
                nint call;
                fixed (byte* d = Z("org.freedesktop.login1")) fixed (byte* p = Z("/org/freedesktop/login1")) fixed (byte* i = Z("org.freedesktop.login1.Manager")) fixed (byte* m = Z("Inhibit"))
                    call = dbus_message_new_method_call(d, p, i, m);
                if (call == 0) return -1;
                byte* iter = stackalloc byte[IterSize];
                dbus_message_iter_init_append(call, iter);
                foreach (string arg in new[] { what, System.AppDomain.CurrentDomain.FriendlyName, "SystemEvents subscribers are told first", "delay" })
                {
                    fixed (byte* a = Z(arg)) { byte* ap = a; dbus_message_iter_append_basic(iter, TYPE_STRING, &ap); }
                }
                DBusError e;
                dbus_error_init(&e);
                nint reply = dbus_connection_send_with_reply_and_block(s_bus, call, 2000, &e);
                dbus_message_unref(call);
                if (e.Name != 0) dbus_error_free(&e);
                if (reply == 0) return -1;
                int fd = -1;
                byte* r = stackalloc byte[IterSize];
                if (dbus_message_iter_init(reply, r) != 0 && dbus_message_iter_get_arg_type(r) == TYPE_UNIX_FD) dbus_message_iter_get_basic(r, &fd);
                dbus_message_unref(reply);
                return fd;
            }

            private static bool LockedHint()
            {
                nint call;
                fixed (byte* d = Z("org.freedesktop.login1")) fixed (byte* p = Z(s_session)) fixed (byte* i = Z("org.freedesktop.DBus.Properties")) fixed (byte* m = Z("Get"))
                    call = dbus_message_new_method_call(d, p, i, m);
                if (call == 0) return false;
                byte* iter = stackalloc byte[IterSize];
                dbus_message_iter_init_append(call, iter);
                foreach (string arg in new[] { "org.freedesktop.login1.Session", "LockedHint" })
                {
                    fixed (byte* a = Z(arg)) { byte* ap = a; dbus_message_iter_append_basic(iter, TYPE_STRING, &ap); }
                }
                DBusError e;
                dbus_error_init(&e);
                nint reply = dbus_connection_send_with_reply_and_block(s_bus, call, 2000, &e);
                dbus_message_unref(call);
                if (e.Name != 0) dbus_error_free(&e);
                if (reply == 0) return false;
                bool locked = false;
                byte* r = stackalloc byte[IterSize];
                byte* v = stackalloc byte[IterSize];
                if (dbus_message_iter_init(reply, r) != 0 && dbus_message_iter_get_arg_type(r) == TYPE_VARIANT)
                {
                    dbus_message_iter_recurse(r, v);
                    if (dbus_message_iter_get_arg_type(v) == TYPE_BOOLEAN) { uint b = 0; dbus_message_iter_get_basic(v, &b); locked = b != 0; }
                }
                dbus_message_unref(reply);
                return locked;
            }

            private static bool Is(nint message, string iface, string member)
            {
                fixed (byte* i = Z(iface)) fixed (byte* m = Z(member)) return dbus_message_is_signal(message, i, m) != 0;
            }

            private static bool FirstBool(nint message)
            {
                byte* iter = stackalloc byte[IterSize];
                if (dbus_message_iter_init(message, iter) == 0 || dbus_message_iter_get_arg_type(iter) != TYPE_BOOLEAN) return false;
                uint b = 0;
                dbus_message_iter_get_basic(iter, &b);
                return b != 0;
            }

            private static string? FirstString(nint message, int index)
            {
                byte* iter = stackalloc byte[IterSize];
                if (dbus_message_iter_init(message, iter) == 0) return null;
                for (int i = 0; i < index; i++) if (dbus_message_iter_next(iter) == 0) return null;
                int t = dbus_message_iter_get_arg_type(iter);
                if (t != TYPE_STRING && t != TYPE_OBJECT_PATH) return null;
                nint s = 0;
                dbus_message_iter_get_basic(iter, &s);
                return Marshal.PtrToStringUTF8(s);
            }

            // PropertiesChanged(interface, {name: variant}, invalidated): the session's LockedHint, where it is given
            private static bool? ChangedLockedHint(nint message)
            {
                byte* iter = stackalloc byte[IterSize];
                byte* dict = stackalloc byte[IterSize];
                byte* entry = stackalloc byte[IterSize];
                byte* value = stackalloc byte[IterSize];
                if (dbus_message_iter_init(message, iter) == 0 || dbus_message_iter_next(iter) == 0 || dbus_message_iter_get_arg_type(iter) != TYPE_ARRAY) return null;
                dbus_message_iter_recurse(iter, dict);
                while (dbus_message_iter_get_arg_type(dict) == TYPE_DICT_ENTRY)
                {
                    dbus_message_iter_recurse(dict, entry);
                    nint key = 0;
                    if (dbus_message_iter_get_arg_type(entry) == TYPE_STRING) dbus_message_iter_get_basic(entry, &key);
                    if (Marshal.PtrToStringUTF8(key) == "LockedHint" && dbus_message_iter_next(entry) != 0 && dbus_message_iter_get_arg_type(entry) == TYPE_VARIANT)
                    {
                        dbus_message_iter_recurse(entry, value);
                        if (dbus_message_iter_get_arg_type(value) != TYPE_BOOLEAN) return null;
                        uint b = 0;
                        dbus_message_iter_get_basic(value, &b);
                        return b != 0;
                    }
                    if (dbus_message_iter_next(dict) == 0) break;
                }
                return null;
            }

            private static void Loop()
            {
                while (dbus_connection_read_write(s_bus, -1) != 0)
                {
                    nint message;
                    while ((message = dbus_connection_pop_message(s_bus)) != 0)
                    {
                        try { Handle(message); }
                        catch (System.Exception) { }
                        finally { dbus_message_unref(message); }
                    }
                }
            }

            private static void Handle(nint message)
            {
                if (Is(message, "org.freedesktop.login1.Manager", "PrepareForSleep"))
                {
                    if (FirstBool(message))
                    {
                        PostAndWait(() => SystemEvents.RaisePowerMode(PowerModes.Suspend), 4000);
                        if (s_sleepDelay >= 0) { close(s_sleepDelay); s_sleepDelay = -1; }
                    }
                    else
                    {
                        Post(() => SystemEvents.RaisePowerMode(PowerModes.Resume));
                        if (s_sleepDelay < 0) s_sleepDelay = Inhibit("sleep");
                    }
                }
                else if (Is(message, "org.freedesktop.login1.Manager", "PrepareForShutdown"))
                {
                    if (!FirstBool(message)) { if (s_shutdownDelay < 0) s_shutdownDelay = Inhibit("shutdown"); return; }
                    // a shutdown logind has decided on: told, not asked (a veto has nothing to stop)
                    PostAndWait(() =>
                    {
                        SystemEvents.RaiseSessionEnding(SessionEndReasons.SystemShutdown);
                        SystemEvents.RaiseSessionEnded(SessionEndReasons.SystemShutdown);
                    }, 4000);
                    if (s_shutdownDelay >= 0) { close(s_shutdownDelay); s_shutdownDelay = -1; }
                }
                else if (Is(message, "org.freedesktop.login1.Manager", "SessionRemoved"))
                {
                    if (s_session.Length > 0 && FirstString(message, 1) == s_session)
                        PostAndWait(() =>
                        {
                            SystemEvents.RaiseSessionEnding(SessionEndReasons.Logoff);
                            SystemEvents.RaiseSessionEnded(SessionEndReasons.Logoff);
                        }, 4000);
                }
                else if (Is(message, "org.freedesktop.DBus.Properties", "PropertiesChanged")
                    && Marshal.PtrToStringUTF8(dbus_message_get_path(message)) == s_session
                    && FirstString(message, 0) == "org.freedesktop.login1.Session"
                    && ChangedLockedHint(message) is bool locked && locked != s_locked)
                {
                    s_locked = locked;
                    Post(() => SystemEvents.RaiseSessionSwitch(locked ? SessionSwitchReason.SessionLock : SessionSwitchReason.SessionUnlock));
                }
            }
        }
    }
}
