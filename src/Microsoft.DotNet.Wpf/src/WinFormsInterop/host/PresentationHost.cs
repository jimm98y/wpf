// Owns the on-screen life of a WinForms app on this stack: the native window, the OS event pump and
// the WebGPU present. It lives in System.Windows.Forms itself, because that is where it belongs --
// real WinForms puts its own windows on screen, so `Application.Run(new Form1())` has to work for an
// app that references nothing but WinForms. Until this existed the driver rendered into memory and
// GetMessage returned false the moment the queue drained, so every sample had to hand-roll a
// window + pump + present loop, and an SDK app could not show a window at all.
//
// The pump seam is XplatUIWebGpu.GetMessage: when the message queue is empty this drives one host
// tick (present, then route OS input back into the queue) instead of ending the loop.

using System;
using System.Threading;

namespace System.Windows.Forms
{
    internal static class PresentationHost
    {
        // WF_WEBGPU=0 keeps the original in-memory behaviour (render to backing stores, no window):
        // that is what the headless render tests want.
        private static readonly bool s_enabled = Environment.GetEnvironmentVariable("WF_WEBGPU") != "0";

        private static IWinFormsHost s_current;
        private static bool s_tried;

        /// <summary>The live host, if any. Set by <see cref="Attach"/> — either from here, or by a
        /// host an app created itself (the browser head, or a sample driving its own loop), so this
        /// never puts up a second window over one that already exists.</summary>
        internal static IWinFormsHost Current => s_current;

        internal static void Attach(IWinFormsHost host)
        {
            s_current = host;
            s_tried = true;
        }

        internal static void Detach(IWinFormsHost host)
        {
            if (ReferenceEquals(s_current, host)) { s_current = null; s_tried = false; }
        }

        /// <summary>Drive one frame: present the current UI, then route OS input back into the driver's
        /// queue. False means "no on-screen host" (headless) or "the window closed" — either way the
        /// message loop should end.</summary>
        internal static bool Tick()
        {
            if (!s_enabled) return false;

            if (s_current == null)
            {
                if (s_tried) return false;                       // already decided there is no host
                s_tried = true;
                Form main = MainForm();
                if (main == null) return false;                  // nothing to show yet
                // The driver paints only what has been invalidated, and until now nothing has been:
                // it renders into backing stores, so becoming visible is not by itself a reason to
                // paint. Without this the window comes up empty and each control only appears when
                // something else invalidates it -- in practice, when you hover it. Dispatch those
                // paints BEFORE the window exists, so its very first present already has content.
                InvalidateTree(main);
                Application.DoEvents();

                IWinFormsHost host;
                if (OperatingSystem.IsWindows()) host = new Win32Host(main);
                else if (OperatingSystem.IsMacOS()) host = new CocoaHost(main);
                else { s_tried = true; return false; }           // no windowing shell for this OS yet
                host.Show();                                     // Attach happens in the ctor
                s_current = host;
                return true;
            }

            EmbeddedScenes.Pump();      // embedded frameworks (a hosted WPF tree) run their frame first
            s_current.Present();
            if (s_current.Pump()) return true;
            s_current = null;                                    // window closed: stop the loop
            return false;
        }

        private static void InvalidateTree(Control c)
        {
            c.Invalidate(true);
            foreach (Control child in c.Controls) InvalidateTree(child);
        }

        // The form to put on screen: the running message loop's main form, else the first open one.
        // Application.OpenForms is populated by Form.Show/Visible, which RunLoop does before pumping.
        private static Form MainForm()
        {
            FormCollection open = Application.OpenForms;
            for (int i = 0; i < open.Count; i++)
                if (open[i] != null && open[i].Visible) return open[i];
            return open.Count > 0 ? open[0] : null;
        }

        /// <summary>Yield when nothing happened, so an idle app costs ~nothing. The hosts poll the OS
        /// queue rather than blocking on it, so the loop has to sleep somewhere — but never past the
        /// next due timer, and never so long that input feels laggy.</summary>
        internal static void Idle(int nextTimerMs)
        {
            const int MaxIdle = 8;      // ~120Hz polling: input stays responsive, idle CPU stays low
            int ms = nextTimerMs < 0 ? MaxIdle : Math.Min(nextTimerMs, MaxIdle);
            if (ms > 0) Thread.Sleep(ms);
        }
    }
}
