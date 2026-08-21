// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// Composites content hosted by an HwndHost-derived class.
//
// HwndHost hands its subclass a parent HWND and expects a child HWND back. There are none here, so
// PresentationFramework offers the returned handle to HwndHostForeignContent instead of throwing;
// this is what claims it. The handle is a WinForms control's, minted by XplatUIWebGpu, so the
// content is already a live WinForms tree with its own layout, painting and timers - only its
// PRESENTATION is missing, which is exactly what WindowsFormsHost solves for the case where it owns
// the element. The same collection runs here against the claimed subtree.
//
// This lives in WindowsFormsIntegration because it is the one assembly that sees all three sides:
// WPF (HwndHost), WinForms (the driver) and the WebGPU compositor.
//

using System.Collections.Generic;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Wpf.Interop.WebGpu.Composition;
using SWF = System.Windows.Forms;

namespace System.Windows.Forms.Integration
{
    internal sealed class ForeignHwndHostContent : IEmbeddedContentSource
    {
        private static readonly object s_lock = new object();
        private static readonly Dictionary<HwndHost, ForeignHwndHostContent> s_claimed
            = new Dictionary<HwndHost, ForeignHwndHostContent>();

        private readonly HwndHost _host;
        private readonly IntPtr _root;
        private readonly XplatUIWebGpu _driver;
        private bool _visible = true;
        private float _lastDevX, _lastDevY, _lastDevW, _lastDevH;

        private ForeignHwndHostContent(HwndHost host, IntPtr root)
        {
            _host = host;
            _root = root;
            _driver = XplatUIWebGpu.GetInstance();
        }

        /// <summary>
        /// Installs the handlers PresentationFramework calls. Idempotent, and safe before any host
        /// exists - EnableWindowsFormsInterop and the first WindowsFormsHost both reach it.
        /// </summary>
        internal static void Install()
        {
            if (HwndHostForeignContent.Attach != null)
            {
                return;
            }

            HwndHostForeignContent.Attach = OnAttach;
            HwndHostForeignContent.Detach = OnDetach;
            HwndHostForeignContent.SetVisible = OnSetVisible;
            HwndHostForeignContent.SetBounds = OnSetBounds;
        }

        private static bool OnAttach(HwndHost host, IntPtr handle)
        {
            if (Environment.GetEnvironmentVariable("WF_TRACE_WINDOWS") == "1")
                Console.Error.WriteLine($"foreign claim: host={host?.GetType().Name} handle=0x{handle.ToInt64():x} " +
                    $"known={XplatUIWebGpu.GetInstance()?.KnowsWindow(handle)}");

            // Only OUR handles. Anything the driver does not know is a real HWND, or someone else's,
            // and declining lets HwndHost report ChildWindowNotCreated as it always would.
            if (host is null || XplatUIWebGpu.GetInstance()?.KnowsWindow(handle) != true)
            {
                return false;
            }

            // Give the hosted tree its handles. WinForms creates a child's handle lazily -- adding a
            // control to a parent only RE-parents a handle that already exists (Control.ChangeParent),
            // it never makes one -- and Control.CreateControl is what walks a tree creating them. An
            // HwndHost whose BuildWindowCore simply takes its container's Handle creates that one
            // window and no other, so every child stayed handle-less, the driver had no window to
            // paint for any of them, and the host showed just the container's background: a plain
            // Control-coloured rectangle. SharpDevelop hosts all of its WinForms option panels and
            // pads this way.
            SWF.Control hostedRoot = SWF.Control.FromHandle(handle);
            if (hostedRoot != null) hostedRoot.CreateControl();

            var claim = new ForeignHwndHostContent(host, handle);
            lock (s_lock)
            {
                if (s_claimed.ContainsKey(host)) return true;
                s_claimed[host] = claim;
            }

            WindowsFormsHost.AddSource(claim);
            return true;
        }

        private static void OnDetach(HwndHost host, IntPtr handle)
        {
            ForeignHwndHostContent claim = null;
            lock (s_lock)
            {
                if (s_claimed.TryGetValue(host, out claim)) s_claimed.Remove(host);
            }

            if (claim != null) WindowsFormsHost.RemoveSource(claim);
        }

        private static void OnSetVisible(HwndHost host, IntPtr handle, bool visible)
        {
            ForeignHwndHostContent claim;
            lock (s_lock) s_claimed.TryGetValue(host, out claim);
            if (claim != null) claim._visible = visible;
        }

        private static void OnSetBounds(HwndHost host, IntPtr handle, int x, int y, int w, int h)
        {
            // Deliberately ignored. HwndHost computes this rect for SetWindowPos, in the root's
            // device space; the scene positions below are derived from the element's own transform
            // every frame, which stays correct through transforms and DPI changes that a cached
            // rect would not survive. The handler exists so HwndHost has somewhere to send it.
        }

        // WF_TRACE_WINDOWS=1: what this host actually publishes, once.
        private static readonly bool s_trace = Environment.GetEnvironmentVariable("WF_TRACE_WINDOWS") == "1";
        private int _traced = -1;

        private string _lastExit;

        private bool TraceExit(string why)
        {
            if (s_trace && _lastExit != why) { _lastExit = why; Console.Error.WriteLine($"foreign host skip: {why}"); }
            return false;
        }

        public bool Collect(List<EmbeddedItem> into)
        {
            if (_driver is null || !_visible || !_host.IsVisible)
                return TraceExit($"driver={_driver != null} visible={_visible} hostVisible={_host?.IsVisible}");

            PresentationSource src = PresentationSource.FromVisual(_host);
            if (src?.CompositionTarget is null || src.RootVisual is null)
                return TraceExit("no presentation source");

            double dpi = src.CompositionTarget.TransformToDevice.M11;
            Point origin;
            try { origin = _host.TransformToAncestor(src.RootVisual).Transform(new Point(0, 0)); }
            catch { return false; }                 // transient, during layout or teardown

            float hostDevX = (float)(origin.X * dpi), hostDevY = (float)(origin.Y * dpi);

            // Only this host's subtree: with several hosts, GetPresentWindows would hand each of
            // them every other host's windows as well.
            long[] wins = _driver.GetSubtreeWindows(_root);
            if (wins is null || wins.Length < 3)
                return TraceExit($"empty subtree for root 0x{_root.ToInt64():x} " +
                    $"({(SWF.Control.FromHandle(_root)?.GetType().Name ?? "<none>")})");

            int ox = (int)wins[1], oy = (int)wins[2];
            if (s_trace && _traced != wins.Length)
            {
                _traced = wins.Length;
                Console.Error.WriteLine($"foreign host root=0x{_root.ToInt64():x} windows={wins.Length / 3}");
                SWF.Control rootControl = SWF.Control.FromHandle(_root);
                if (rootControl != null)
                {
                    Console.Error.WriteLine($"   winforms children of {rootControl.GetType().Name}: {rootControl.Controls.Count}");
                    foreach (SWF.Control kid in rootControl.Controls)
                        Console.Error.WriteLine($"     {kid.GetType().Name} '{kid.Name}' {kid.Bounds} " +
                            $"visible={kid.Visible} handleCreated={kid.IsHandleCreated} " +
                            $"handle=0x{(kid.IsHandleCreated ? kid.Handle.ToInt64() : 0):x} children={kid.Controls.Count}");
                }
                for (int j = 0; j + 2 < wins.Length; j += 3)
                {
                    var wh = (IntPtr)wins[j];
                    SWF.Control wc = SWF.Control.FromHandle(wh);
                    Console.Error.WriteLine($"   0x{wins[j]:x} at ({wins[j + 1]},{wins[j + 2]}) " +
                        $"{(wc == null ? "<none>" : wc.GetType().Name + " '" + wc.Name + "' " + wc.Bounds + " visible=" + wc.Visible)} " +
                        $"scene={(_driver.GetWindowScene(wh) == null ? "null" : "ok")}");
                }
            }
            for (int i = 0; i + 2 < wins.Length; i += 3)
            {
                IntPtr h = (IntPtr)wins[i];
                object scene = _driver.GetWindowScene(h);
                if (scene is null) continue;

                long packed = _driver.GetWindowSizePacked(h);
                int w = (int)(packed >> 32), ht = (int)(packed & 0xFFFFFFFF);
                into.Add(new EmbeddedItem
                {
                    Scene = scene,
                    DeviceX = hostDevX + ((int)wins[i + 1] - ox) * (float)dpi,
                    DeviceY = hostDevY + ((int)wins[i + 2] - oy) * (float)dpi,
                    DeviceW = w * (float)dpi,
                    DeviceH = ht * (float)dpi,
                    Scale = (float)dpi,
                    // Tag the window: the registry is process-wide, and without this every WPF
                    // window composited every other one's hosted content.
                    Window = (src as HwndSource)?.Handle ?? IntPtr.Zero,
                });
            }

            bool moved = _lastDevX != hostDevX || _lastDevY != hostDevY
                      || _lastDevW != _host.RenderSize.Width || _lastDevH != _host.RenderSize.Height;
            _lastDevX = hostDevX; _lastDevY = hostDevY;
            _lastDevW = (float)_host.RenderSize.Width; _lastDevH = (float)_host.RenderSize.Height;
            return moved;
        }

        public void Invalidate() => _host.InvalidateVisual();
    }
}
