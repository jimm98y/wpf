// Owns the on-screen life of a WinForms app on this stack: the native windows, the OS event pump and
// the WebGPU present. It lives in System.Windows.Forms itself, because that is where it belongs --
// real WinForms puts its own windows on screen, so `Application.Run(new Form1())` has to work for an
// app that references nothing but WinForms. Until this existed the driver rendered into memory and
// GetMessage returned false the moment the queue drained, so every sample had to hand-roll a
// window + pump + present loop, and an SDK app could not show a window at all.
//
// The pump seam is XplatUIWebGpu.GetMessage: when the message queue is empty this drives one host
// tick (present, then route OS input back into the queue) instead of ending the loop. A WPF app has
// no WinForms message loop to reach that seam, so WindowsFormsHost drives Tick from the composition
// tick instead -- otherwise a form opened with Show() reports Visible == true and never appears.

using System;
using System.Collections.Generic;
using System.Threading;

namespace System.Windows.Forms
{
    internal static class PresentationHost
    {
        // WF_WEBGPU=0 keeps the original in-memory behaviour (render to backing stores, no window):
        // that is what the headless render tests want.
        private static readonly bool s_enabled = Environment.GetEnvironmentVariable("WF_WEBGPU") != "0";

        // One host per top-level form on screen. There is more than one as soon as an app opens a
        // dialog over its main window, so this cannot be a single slot: with one, whichever form got
        // there first kept the only window and every later form stayed invisible.
        private static readonly List<IWinFormsHost> s_hosts = new List<IWinFormsHost>();
        private static readonly Dictionary<IWinFormsHost, Form> s_forms = new Dictionary<IWinFormsHost, Form>();

        // Forms that must never get a window of their own. The offscreen container a
        // WindowsFormsHost paints into is a visible top-level Form, but its pixels belong to the WPF
        // element that composites them. Hosting it put an empty, borderless window on screen and --
        // worse -- consumed the one host slot, so the dialog the app had actually opened never
        // appeared at all.
        private static readonly HashSet<Form> s_suppressed = new HashSet<Form>();

        // An application may run a second UI thread -- SharpDevelop shows its assertion dialog from
        // a private STA thread -- and each thread drives this independently. The registry is shared,
        // so it has to be guarded, and each thread only ever adopts, presents and pumps the windows
        // of forms created on it: a form's handle belongs to its own thread.
        private static readonly object s_lock = new object();

        /// <summary>Whether windows are put on screen at all. False is the headless mode
        /// (WF_WEBGPU=0) the render tests use, where there is never a host to wait for.</summary>
        internal static bool Enabled => s_enabled;

        /// <summary>The newest live host, if any -- the one popups and other unclaimed windows
        /// belong to. Hosts add themselves through <see cref="Attach"/>: either from here, or by a
        /// host an app created itself (the browser head, or a sample driving its own loop).</summary>
        internal static IWinFormsHost Current => s_hosts.Count == 0 ? null : s_hosts[s_hosts.Count - 1];

        /// <summary>How many top-level windows are on screen. One is the common case, and the
        /// present path keeps its original whole-world behaviour there.</summary>
        internal static int HostCount => s_hosts.Count;

        /// <summary>Whether <paramref name="host"/> is the newest one, and so owns every window no
        /// other host claims -- menus, combo drop-downs and tooltips are top-level windows of their
        /// own that no form subtree contains.</summary>
        internal static bool IsTopHost(IWinFormsHost host) => ReferenceEquals(Current, host);

        /// <summary>The forms that must never appear on screen -- the compositing containers.
        /// A host has to know them: their windows are real windows of the driver, and a host that
        /// drew every window it could see would paint the whole hosted workbench into its own
        /// dialog.</summary>
        internal static Form[] SuppressedForms()
        {
            lock (s_lock)
            {
                var list = new List<Form>(s_suppressed.Count);
                foreach (Form f in s_suppressed)
                    if (f != null && !f.IsDisposed) list.Add(f);
                return list.ToArray();
            }
        }

        /// <summary>The forms owned by every host except <paramref name="host"/>.</summary>
        internal static Form[] OtherHostedForms(IWinFormsHost host)
        {
            lock (s_lock)
            {
                var list = new List<Form>(s_hosts.Count);
                foreach (IWinFormsHost h in s_hosts)
                {
                    Form f;
                    if (!ReferenceEquals(h, host) && s_forms.TryGetValue(h, out f) && f != null) list.Add(f);
                }
                return list.ToArray();
            }
        }

        // Driver windows that a WPF element composites: the container an HwndHost claims. A window
        // host must not draw these -- they are somebody else's pixels, positioned by a WPF element
        // rather than by the driver, and drawing them put a pad's contents inside an unrelated
        // dialog.
        private static readonly HashSet<IntPtr> s_composited = new HashSet<IntPtr>();

        internal static void SuppressWindow(IntPtr handle)
        {
            if (handle != IntPtr.Zero) lock (s_lock) s_composited.Add(handle);
        }

        internal static void UnsuppressWindow(IntPtr handle)
        {
            if (handle != IntPtr.Zero) lock (s_lock) s_composited.Remove(handle);
        }

        /// <summary>The driver windows a WPF element composites; a host skips their subtrees.</summary>
        internal static IntPtr[] CompositedWindows()
        {
            lock (s_lock)
            {
                var copy = new IntPtr[s_composited.Count];
                s_composited.CopyTo(copy);
                return copy;
            }
        }

        /// <summary>Keep <paramref name="form"/> off the screen for good: it is a compositing
        /// surface, not a window. See <see cref="s_suppressed"/>.</summary>
        internal static void Suppress(Form form) { if (form != null) lock (s_lock) s_suppressed.Add(form); }

        internal static void Unsuppress(Form form) { if (form != null) lock (s_lock) s_suppressed.Remove(form); }

        internal static void Attach(IWinFormsHost host, Form form)
        {
            if (host == null) return;
            lock (s_lock)
            {
                if (!s_hosts.Contains(host)) s_hosts.Add(host);
                if (form != null) s_forms[host] = form;
            }
        }

        /// <summary>The host putting <paramref name="form"/> on screen, or null when it has none.
        /// A popup needs it to work out where on the real screen its owner's client area landed.
        /// </summary>
        internal static IWinFormsHost HostOf(Form form)
        {
            if (form == null) return null;
            lock (s_lock)
                foreach (var pair in s_forms)
                    if (ReferenceEquals(pair.Value, form)) return pair.Key;
            return null;
        }

        internal static void Detach(IWinFormsHost host)
        {
            if (host == null) return;
            lock (s_lock)
            {
                s_hosts.Remove(host);
                s_forms.Remove(host);
            }
        }

        /// <summary>Drive one frame from OUTSIDE a WinForms message loop -- a WPF app, whose thread
        /// belongs to the dispatcher. The loop is what normally fires WinForms timers and drains
        /// posted callbacks, so both have to happen here or neither ever does: a modeless dialog
        /// would never animate, and Control.BeginInvoke would never run. SharpDevelop disposes its
        /// splash screen through exactly such a BeginInvoke -- so the splash sat on screen, blank,
        /// for the life of the process.</summary>
        internal static bool TickExternal()
        {
            if (!s_enabled) return false;
            try
            {
                XplatUIWebGpu driver = XplatUIWebGpu.GetInstance();
                if (driver != null) driver.TickTimers();
                Application.DoEvents();
                return Tick();
            }
            catch (Exception ex)
            {
                // This runs on the WPF dispatcher, so anything that escapes is reported to the
                // application as an "unhandled WPF exception" -- a baffling label for what is
                // really a WinForms paint, layout or timer failure, and one that fires again on
                // every tick. WinForms' own message loop hands these to Application.ThreadException;
                // do the same, and report each distinct failure once.
                ReportTickFailure(ex);
                return false;
            }
        }

        private static readonly HashSet<string> s_reported = new HashSet<string>();

        private static void ReportTickFailure(Exception ex)
        {
            string key = ex.GetType().FullName + "|" + ex.StackTrace;
            if (!s_reported.Add(key)) return;      // same failure every frame: say it once
            Console.Error.WriteLine("WinForms host tick failed: " + ex);
            try { Application.OnThreadException(ex); } catch { }
        }

        /// <summary>Drive one frame: present the current UI, then route OS input back into the driver's
        /// queue. False means "no on-screen host" (headless) or "every window closed" -- either way the
        /// message loop should end.</summary>
        internal static bool Tick()
        {
            if (!s_enabled) return false;

            AdoptNewForms();
            if (s_hosts.Count == 0) return false;

            EmbeddedScenes.Pump();      // embedded frameworks (a hosted WPF tree) run their frame first

            // Newest first: a dialog window is the one the user is looking at. Walk a copy, so a
            // host that goes away mid-frame cannot disturb the iteration.
            IWinFormsHost[] hosts;
            lock (s_lock) hosts = s_hosts.ToArray();
            for (int i = hosts.Length - 1; i >= 0; i--)
            {
                IWinFormsHost host = hosts[i];
                Form form;
                lock (s_lock) s_forms.TryGetValue(host, out form);
                if (form != null && form.InvokeRequired) continue;   // another thread drives this one
                if (form == null || form.IsDisposed)
                {
                    // The form closed itself (an OK button, not the window close box): take its
                    // window down with it, or a dead dialog would stay on screen for ever.
                    host.Close();
                    Detach(host);
                    continue;
                }

                // Hidden is not closed. A drop-down is hidden between uses and shown again -- the
                // property grid keeps one and reuses it -- so destroying its window here took the whole
                // application down with it: this is called from inside the drop-down's own nested
                // message loop, and detaching the last host makes Tick answer "every window has gone",
                // which ends the loop that was pumping the window underneath. The window follows the
                // form on and off screen by itself; there is nothing to do here but leave it alone.
                if (!form.Visible)
                    continue;
                host.Present();
                if (!host.Pump()) Detach(host);
            }

            return s_hosts.Count > 0;
        }

        /// <summary>Give every visible top-level form that has no window one of its own. Menus,
        /// drop-downs and tooltips are not Forms on this stack, so they are not swept up here --
        /// they stay windows of the driver, composited into whichever host claims them.</summary>
        private static void AdoptNewForms()
        {
            FormCollection open = Application.OpenForms;
            for (int i = 0; i < open.Count; i++)
            {
                Form form = open[i];
                if (form == null || form.IsDisposed || !form.Visible) continue;
                if (form.InvokeRequired) continue;      // belongs to another UI thread

                // A Form with a parent is not a top-level window and must never get one: the forms
                // designer parents the form being designed into its design surface, so this adopted
                // it, gave it a window of its own next to the IDE, and -- being a host with no
                // subtree of its own to draw -- filled that window with whatever unclaimed driver
                // windows it could find. The user saw the designed form twice, the second copy full
                // of other pads' controls.
                if (form.Parent != null || !form.TopLevel) continue;
                lock (s_lock)
                {
                    if (s_suppressed.Contains(form)) continue;
                    if (s_forms.ContainsValue(form)) continue;
                }

                // The driver paints only what has been invalidated, and until now nothing has been:
                // it renders into backing stores, so becoming visible is not by itself a reason to
                // paint. Without this the window comes up empty and each control only appears when
                // something else invalidates it -- in practice, when you hover it. Dispatch those
                // paints BEFORE the window exists, so its very first present already has content.
                InvalidateTree(form);
                Application.DoEvents();

                IWinFormsHost host;
                if (OperatingSystem.IsWindows()) host = new Win32Host(form);
                else if (OperatingSystem.IsMacOS()) host = new CocoaHost(form);
                else return;                             // no windowing shell for this OS yet
                host.Show();                             // Attach happens in the ctor
            }
        }

        private static void InvalidateTree(Control c)
        {
            c.Invalidate(true);
            foreach (Control child in c.Controls) InvalidateTree(child);
        }

        /// <summary>Yield when nothing happened, so an idle app costs ~nothing. The hosts poll the OS
        /// queue rather than blocking on it, so the loop has to sleep somewhere -- but never past the
        /// next due timer, and never so long that input feels laggy.</summary>
        internal static void Idle(int nextTimerMs)
        {
            const int MaxIdle = 8;      // ~120Hz polling: input stays responsive, idle CPU stays low
            int ms = nextTimerMs < 0 ? MaxIdle : Math.Min(nextTimerMs, MaxIdle);
            if (ms > 0) Thread.Sleep(ms);
        }
    }
}
