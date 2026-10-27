// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// The hosted WPF window, on Windows, is a REAL child window of the form's host window -- but the
// WinForms controls around it are not windows at all, only drawn by the host. On Windows a child
// window disappears with any hidden ancestor, is cut off by its ancestors' client areas, and lies
// under siblings above it, all for free. None of that held here: the hosted window stayed as it was
// first shown. In app an ElementHost on one tab of the right-hand dock kept its window visible
// over the other tabs after they were switched to, and caught every click meant for Preferences,
// Dynamics and Properties; one whose control was taken off the form stayed where it last was.
//
// So the window's visibility and shape follow the control's EFFECTIVE visibility, worked out here as
// Windows works it out for child windows:
//   * shown only while the control is in a shown, not minimised form, with every container up to the
//     form visible, and with a WPF child to show -- otherwise hidden at once (SW_HIDE);
//   * where the control is cut away by an ancestor's client area (a scrolled or partly covered pane)
//     or covered by a sibling at any level above it in z-order (a docked panel, a control drawn over
//     it), the hosted window lets the mouse through (HTTRANSPARENT) to the host and the control drawn
//     there; wholly covered, it is hidden.
// What the host composites is cut the same way (see Collect), so the picture and the input agree.

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using SD = System.Drawing;

namespace System.Windows.Forms.Integration
{
    public partial class ElementHost
    {

        /// <summary>The part of this control that is on screen, in its own client coordinates (driver
        /// units), cut to its ancestors' client areas; and the rectangles of controls above it that
        /// cover parts of that, with the top-level windows over its form when <paramref name="windowsOver"/>
        /// (for input: a menu over the hosted window takes its own clicks). Empty when the control is not
        /// effectively visible at all.</summary>
        internal SD.Rectangle VisiblePart(List<SD.Rectangle> covered, bool windowsOver = true)
        {
            covered?.Clear();
            if (IsDisposed || Disposing || _child == null || !IsHandleCreated) return SD.Rectangle.Empty;
            Form form = FindForm();
            if (form == null || !form.Visible || form.IsDisposed || form.WindowState == FormWindowState.Minimized) return SD.Rectangle.Empty;
            SD.Rectangle r = RectangleToScreen(ClientRectangle);
            for (Control c = this; c != null && c != form; c = c.Parent)
            {
                if (!c.is_visible) return SD.Rectangle.Empty;
                Control p = c.Parent;
                if (p == null) return SD.Rectangle.Empty;            // taken off the form
                r.Intersect(p.RectangleToScreen(p.ClientRectangle));
                if (r.IsEmpty) return SD.Rectangle.Empty;
                if (covered != null)
                {
                    // Siblings above c: a lower index in the parent's collection is higher in z-order.
                    int mine = p.Controls.GetChildIndex(c, false);
                    for (int i = 0; i < mine && i < p.Controls.Count; i++)
                    {
                        Control s = p.Controls[i];
                        if (s == null || !s.is_visible || s.Width <= 0 || s.Height <= 0) continue;
                        SD.Rectangle sr = p.RectangleToScreen(s.Bounds);
                        sr.Intersect(r);
                        if (!sr.IsEmpty) covered.Add(sr);
                    }
                }
            }
            // ...and the top-level windows over its form: its menus and popups (a DotNetBar gallery dropped
            // over a 3D view was drawn over it but pointed at under it, and none of its items could be picked)
            if (covered != null && windowsOver && XplatUI.RunningWebGpuDriver && XplatUIWebGpu.GetInstance() is { } webgpu)
                foreach (SD.Rectangle w in webgpu.WindowsOver(form.Handle))
                {
                    SD.Rectangle wr = w;
                    wr.Intersect(r);
                    if (!wr.IsEmpty) covered.Add(wr);
                }
            SD.Point o = PointToScreen(SD.Point.Empty);
            r.Offset(-o.X, -o.Y);
            if (covered != null)
                for (int i = 0; i < covered.Count; i++)
                {
                    SD.Rectangle cr = covered[i];
                    cr.Offset(-o.X, -o.Y);
                    covered[i] = cr;
                }
            return r;
        }

        /// <summary>What of this control shows, as disjoint rectangles in its client coordinates: the
        /// visible part (see VisiblePart) minus every control above it, and minus the top-level windows
        /// over its form when <paramref name="windowsOver"/>. Empty when nothing shows.</summary>
        /// <remarks>The picture is not cut by those windows: the host draws them over it (a menu, and the
        /// drop shadow DotNetBar gives a menu, a window of its own that is mostly transparent). Cut out,
        /// the hole showed the form's background through the shadow, a white band around the menu.</remarks>
        internal List<SD.Rectangle> VisiblePieces(bool windowsOver = true)
        {
            var covered = new List<SD.Rectangle>();
            SD.Rectangle vis = VisiblePart(covered, windowsOver);
            var pieces = new List<SD.Rectangle>();
            if (vis.IsEmpty) return pieces;
            pieces.Add(vis);
            foreach (SD.Rectangle c in covered)
            {
                var next = new List<SD.Rectangle>(pieces.Count + 3);
                foreach (SD.Rectangle p in pieces)
                {
                    if (!p.IntersectsWith(c)) { next.Add(p); continue; }
                    SD.Rectangle i = SD.Rectangle.Intersect(p, c);
                    if (i.Top > p.Top) next.Add(SD.Rectangle.FromLTRB(p.Left, p.Top, p.Right, i.Top));
                    if (i.Bottom < p.Bottom) next.Add(SD.Rectangle.FromLTRB(p.Left, i.Bottom, p.Right, p.Bottom));
                    if (i.Left > p.Left) next.Add(SD.Rectangle.FromLTRB(p.Left, i.Top, i.Left, i.Bottom));
                    if (i.Right < p.Right) next.Add(SD.Rectangle.FromLTRB(i.Right, i.Top, p.Right, i.Bottom));
                }
                pieces = next;
                if (pieces.Count == 0) break;
            }
            return pieces;
        }

        /// <summary>Make the hosted window's visibility and region what the control's are (Windows,
        /// where it is a real child window). Cheap when nothing changed.</summary>
        private void SyncHostedWindow()
        {
            if (OperatingSystem.IsMacOS()) { SyncHostedRegion(); return; }
            if (!CompositeIntoHostFrame || _source == null || _source.IsDisposed || !OnTreeThread) return;
            // the browser's hosted canvas lies over the whole control; it shows and takes the pointer
            // only where the control does, as a clipped child window
            if (OperatingSystem.IsBrowser()) SyncHostedRegion();
            // shown while any of it shows: a menu over all of it does not hide it
            List<SD.Rectangle> pieces = VisiblePieces(windowsOver: false);
            bool on = pieces.Count > 0 && _placedW > 0 && _placedH > 0;
            if (on != _shown)
            {
                if (s_trace) Console.WriteLine($"ElementHost: hosted window 0x{_source.Handle:x} {(on ? "shown" : "hidden")} (pieces {pieces.Count}, placed {_placedW}x{_placedH})");
                _shown = on;
                ShowHostedWindow(on);
            }
        }

        private string _region;

        /// <summary>macOS, where the hosted window presents itself over the control (an NSWindow, a child
        /// of the form's), and the browser, where it is a canvas over the control: only the parts of it
        /// that would show on Windows show, and take the pointer (CocoaWindow's or BrowserWindow's region).
        /// Without this the whole of it did -- app's display canvas ran out of the form's bottom,
        /// over the dock, and over the panes beside it.</summary>
        private void SyncHostedRegion()
        {
            if (_source == null || _source.IsDisposed || !OnTreeThread) return;
            List<SD.Rectangle> pieces = VisiblePieces();
            // The region is in the window's own units -- CSS pixels on the page, points in AppKit -- and
            // the pieces in the control's: device pixels for a per-monitor application, whose windows'
            // units are not its own.
            double backing = OperatingSystem.IsBrowser() ? MS.Internal.Interop.BrowserWindow.FromHandle(_source.Handle)?.GetBackingScale() ?? 1
                : MS.Internal.Interop.CocoaWindow.FromHandle(_source.Handle)?.GetBackingScale() ?? 1;
            double k = (_scale > 0 ? _scale : 1) / (backing > 0 ? backing : 1);
            var rects = new int[pieces.Count * 4];
            for (int i = 0; i < pieces.Count; i++)
            {
                int l = (int)Math.Floor(pieces[i].Left * k), t = (int)Math.Floor(pieces[i].Top * k);
                rects[i * 4] = l; rects[i * 4 + 1] = t;
                rects[i * 4 + 2] = (int)Math.Ceiling(pieces[i].Right * k) - l; rects[i * 4 + 3] = (int)Math.Ceiling(pieces[i].Bottom * k) - t;
            }
            // the window's size as well: the same pieces of a window that has since grown are another mask
            string region = $"{_placedW}x{_placedH}:" + string.Join(",", rects);
            if (region == _region) return;
            _region = region;
            if (s_trace) Console.WriteLine($"ElementHost: hosted window 0x{_source.Handle:x} region [{region}]");
            if (OperatingSystem.IsBrowser()) MS.Internal.Interop.BrowserWindow.FromHandle(_source.Handle)?.SetVisibleRegion(rects);
            else MS.Internal.Interop.CocoaWindow.FromHandle(_source.Handle)?.SetVisibleRegion(rects);
        }

        /// <summary>Whether the screen point (device pixels) is over a part of this control that a WinForms
        /// control above it covers, or that its ancestors cut away: there the hosted window lets the mouse
        /// through (WM_NCHITTEST answers HTTRANSPARENT), to the host window and the control drawn there.
        /// (A window region would say the same to Windows, but SetWindowRgn's WM_WINDOWPOSCHANGED left
        /// WPF placing popups by a stale window rectangle.)</summary>
        internal bool CoveredAt(int screenX, int screenY)
        {
            if (_source == null || _source.IsDisposed) return false;
            var p = new POINT { x = screenX, y = screenY };
            if (!ScreenToClient(_source.Handle, ref p)) return false;
            float k = _scale > 0 ? _scale : 1f;
            var at = new SD.Point((int)Math.Floor(p.x / k), (int)Math.Floor(p.y / k));
            foreach (SD.Rectangle piece in VisiblePieces())
                if (piece.Contains(at)) return false;
            return true;
        }

        /// <summary>For tests: whether the hosted window is shown, and its HWND.</summary>
        internal bool HostedWindowShown => _shown;
        internal IntPtr HostedWindowHandle => _source?.Handle ?? IntPtr.Zero;

        private static Microsoft.Wpf.Interop.WebGpu.Composition.Rect Intersect(Microsoft.Wpf.Interop.WebGpu.Composition.Rect a, Microsoft.Wpf.Interop.WebGpu.Composition.Rect b)
        {
            float l = Math.Max(a.X, b.X), t = Math.Max(a.Y, b.Y);
            float r = Math.Min(a.X + a.Width, b.X + b.Width), bt = Math.Min(a.Y + a.Height, b.Y + b.Height);
            return new Microsoft.Wpf.Interop.WebGpu.Composition.Rect(l, t, Math.Max(0, r - l), Math.Max(0, bt - t));
        }

        protected override void OnParentChanged(EventArgs e)
        {
            base.OnParentChanged(e);
            SyncHostedWindow();
        }

    }
}
