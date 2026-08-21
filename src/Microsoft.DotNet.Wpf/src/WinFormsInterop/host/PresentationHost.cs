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
            var list = new List<Form>(s_suppressed.Count);
            foreach (Form f in s_suppressed)
                if (f != null && !f.IsDisposed) list.Add(f);
            return list.ToArray();
        }

        /// <summary>The forms owned by every host except <paramref name="host"/>.</summary>
        internal static Form[] OtherHostedForms(IWinFormsHost host)
        {
            var list = new List<Form>(s_hosts.Count);
            foreach (IWinFormsHost h in s_hosts)
            {
                Form f;
                if (!ReferenceEquals(h, host) && s_forms.TryGetValue(h, out f) && f != null) list.Add(f);
            }
            return list.ToArray();
        }

        /// <summary>Keep <paramref name="form"/> off the screen for good: it is a compositing
        /// surface, not a window. See <see cref="s_suppressed"/>.</summary>
        internal static void Suppress(Form form) { if (form != null) s_suppressed.Add(form); }

        internal static void Unsuppress(Form form) { if (form != null) s_suppressed.Remove(form); }

        internal static void Attach(IWinFormsHost host, Form form)
        {
            if (host == null) return;
            if (!s_hosts.Contains(host)) s_hosts.Add(host);
            if (form != null) s_forms[host] = form;
        }

        internal static void Detach(IWinFormsHost host)
        {
            if (host == null) return;
            s_hosts.Remove(host);
            s_forms.Remove(host);
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
            IWinFormsHost[] hosts = s_hosts.ToArray();
            for (int i = hosts.Length - 1; i >= 0; i--)
            {
                IWinFormsHost host = hosts[i];
                Form form;
                if (!s_forms.TryGetValue(host, out form) || form == null || form.IsDisposed || !form.Visible)
                {
                    // The form closed itself (an OK button, not the window close box): take its
                    // window down with it, or a dead dialog would stay on screen for ever.
                    host.Close();
                    Detach(host);
                    continue;
                }
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
                if (s_suppressed.Contains(form)) continue;
                if (s_forms.ContainsValue(form)) continue;

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
