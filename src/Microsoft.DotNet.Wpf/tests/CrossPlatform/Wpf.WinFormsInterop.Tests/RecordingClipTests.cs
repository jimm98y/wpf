// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// A screen recording's Graphics.Clip is the clip in force, not "infinite": painters read it and put
// it back (`Region old = g.Clip; ...; g.Clip = old`), and answering infinite threw the clip away in
// the middle of a paint -- DotNetBar's ribbon form's File button covered the form's border.
//

using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.WebGpuBackend;
using Xunit;

namespace Wpf.WinFormsInterop.Tests
{
    public class RecordingClipTests
    {
        private static RectangleF Bounds(Region r)
        {
            RectangleF[] scans = r.GetRegionScans(new Matrix());
            RectangleF b = scans.Length > 0 ? scans[0] : RectangleF.Empty;
            foreach (RectangleF s in scans) b = RectangleF.Union(b, s);
            return b;
        }

        [Fact]
        public void Clip_IsTheClipInForce_AndSurvivesARestore()
        {
            Graphics g = GpuRaster.NewRecording();
            try
            {
                Assert.True(g.Clip.IsInfinite(g));
                g.TranslateTransform(1, 1);
                g.TranslateTransform(-1, 0);
                g.SetClip(new Rectangle(1, 1, 100, 50), CombineMode.Replace);
                Region old = g.Clip;
                Assert.Equal(new RectangleF(1, 1, 100, 50), Bounds(old));
                g.SetClip(new Rectangle(0, 0, 10, 10), CombineMode.Intersect);
                Assert.Equal(new RectangleF(1, 1, 9, 9), Bounds(g.Clip));
                g.Clip = old;
                Assert.Equal(new RectangleF(1, 1, 100, 50), Bounds(g.Clip));
                g.ResetClip();
                Assert.True(g.Clip.IsInfinite(g));
            }
            finally { GpuRaster.EndScene(g); }
        }

        [Fact]
        public void Clip_FollowsSaveAndRestore()
        {
            Graphics g = GpuRaster.NewRecording();
            try
            {
                g.SetClip(new Rectangle(5, 5, 20, 20));
                GraphicsState s = g.Save();
                g.SetClip(new Rectangle(0, 0, 10, 10), CombineMode.Intersect);
                g.Restore(s);
                Assert.Equal(new RectangleF(5, 5, 20, 20), Bounds(g.Clip));
            }
            finally { GpuRaster.EndScene(g); }
        }
    }
}
