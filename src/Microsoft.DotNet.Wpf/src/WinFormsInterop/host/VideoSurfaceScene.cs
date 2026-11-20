// The video Media Foundation's video renderer shows in a WinForms control (WgpuInterop's Composition/VideoSurfaces.cs):
// an embedded scene over the control whose handle the renderer was given, in its form's host only, each frame a new
// version the hosts present on. A surface of a handle that is no control here (a WPF window's) is not this one's.

using System;
using System.Collections.Generic;
using System.Drawing;
using Microsoft.Wpf.Interop.WebGpu.Composition;

namespace System.Windows.Forms
{
    internal sealed class VideoSurfaceScene : IEmbeddedScene
    {
        internal static readonly VideoSurfaceScene Instance = new();

        public void Pump() { }

        public int Version => VideoSurfaces.Version;

        public void Collect(int ox, int oy, List<(object Scene, int X, int Y)> into)
        {
            var surfaces = VideoSurfaces.Snapshot();
            if (surfaces.Count == 0) return;
            var driver = XplatUIWebGpu.GetInstance();
            foreach (var s in surfaces)
            {
                Control c = Control.FromHandle(s.Hwnd);
                if (c == null || c.IsDisposed || !c.Visible || c.TopLevelControl is not Control top || !top.IsHandleCreated) continue;
                Hwnd h = Hwnd.ObjectFromHandle(s.Hwnd), th = Hwnd.ObjectFromHandle(top.Handle);
                if (h == null || th == null) continue;
                // the host of the control's own form: its origin is the form's
                Point tp = driver.ScreenLocation(th);
                if (tp.X != ox || tp.Y != oy) continue;
                Point p = driver.ScreenLocation(h);
                into.Add((VideoSurfaces.SceneOf(s, h.ClientRect.Width, h.ClientRect.Height), p.X - ox, p.Y - oy));
            }
        }
    }
}
