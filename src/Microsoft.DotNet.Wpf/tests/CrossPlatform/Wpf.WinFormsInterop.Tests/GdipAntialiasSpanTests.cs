// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// GdipAntialias.FillPath counts each span's subsamples per pixel arithmetically (ragged ends directly,
// whole pixels through a difference row). It must give exactly what counting them one at a time gives
// -- GDI+'s own filler, which the reference below is, line for line, for polygons.
//

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.WebGpuBackend;
using Xunit;

namespace Wpf.WinFormsInterop.Tests
{
    public class GdipAntialiasSpanTests
    {
        [Fact]
        public void SpanCounting_IsExactlyTheSubsampleCount()
        {
            var rnd = new Random(1234);
            int shapes = 0;
            for (int t = 0; t < 400; t++)
            {
                int n = rnd.Next(3, 12);
                var pts = new PointF[n];
                var types = new byte[n];
                float span = t % 4 == 0 ? 600f : 60f;     // big panels as well as small shapes
                for (int i = 0; i < n; i++)
                {
                    pts[i] = new PointF((float)(rnd.NextDouble() * span - 10), (float)(rnd.NextDouble() * span * 0.6 - 10));
                    types[i] = (byte)(i == 0 ? 0 : 1);
                }
                bool winding = t % 2 == 0;
                bool ok = GdipAntialias.FillPath(pts, types, winding, 1, 0, 0, 1, 0, 0, out byte[] alpha, out Rectangle r);
                bool okRef = Reference(pts, winding, out byte[] alphaRef, out Rectangle rRef);
                Assert.Equal(okRef, ok);
                if (!ok) continue;
                shapes++;
                Assert.Equal(rRef, r);
                Assert.Equal(alphaRef, alpha);
            }
            Assert.True(shapes > 300, $"only {shapes} shapes were filled");
        }

        /// <summary>The filler as it was: every subsample of every span counted one at a time.</summary>
        private static bool Reference(PointF[] p, bool winding, out byte[] alpha, out Rectangle bounds)
        {
            alpha = null!; bounds = Rectangle.Empty;
            var pts = new (int X, int Y)[p.Length];
            for (int i = 0; i < p.Length; i++)
            {
                float fx = (16f * p[i].X + 0f) * 16f + 0.5f, fy = (p[i].Y * 16f + 0f) * 16f + 0.5f;
                pts[i] = (((int)fx + 15) >> 4, ((int)fy + 15) >> 4);
            }
            var edges = new List<(long X0, long Y0, long X1, long Y1, int Dir)>();
            long minY = long.MaxValue, maxY = long.MinValue;
            int n = pts.Length;
            for (int i = 0; i < n; i++)
            {
                long x0 = (pts[i].X + 8L) * 8, y0 = (pts[i].Y + 8L) << 2;
                long x1 = (pts[(i + 1) % n].X + 8L) * 8, y1 = (pts[(i + 1) % n].Y + 8L) << 2;
                minY = Math.Min(minY, y0); maxY = Math.Max(maxY, y0);
                if (y0 == y1) continue;
                int dir = 1;
                if (y1 < y0) { (x0, x1) = (x1, x0); (y0, y1) = (y1, y0); dir = -1; }
                edges.Add((x0, y0, x1, y1, dir));
            }
            if (edges.Count == 0) return false;
            long k0 = CeilDiv(minY, 16), k1 = CeilDiv(maxY, 16);
            if (k1 <= k0) return false;
            var rows = new List<(long X, int Dir)>[k1 - k0];
            foreach (var e in edges)
            {
                long dM = e.X1 - e.X0, dN = e.Y1 - e.Y0;
                for (long k = CeilDiv(e.Y0, 16); k < CeilDiv(e.Y1, 16); k++)
                    (rows[k - k0] ??= new List<(long, int)>()).Add((CeilDiv(e.X0 * dN + dM * (16 * k - e.Y0), 16 * dN), e.Dir));
            }
            long sMin = long.MaxValue, sMax = long.MinValue;
            foreach (var r in rows) if (r != null) foreach (var v in r) { sMin = Math.Min(sMin, v.X); sMax = Math.Max(sMax, v.X); }
            if (sMin > sMax) return false;
            int px0 = (int)(sMin >> 3), px1 = (int)((sMax - 1) >> 3) + 1;
            int py0 = (int)(k0 >> 2), py1 = (int)((k1 - 1) >> 2) + 1;
            int w = px1 - px0, h = py1 - py0;
            if (w <= 0 || h <= 0 || (long)w * h > 1 << 22) return false;
            var count = new int[w * h];
            for (int ri = 0; ri < rows.Length; ri++)
            {
                var r = rows[ri];
                if (r == null) continue;
                r.Sort((u, v) => u.X.CompareTo(v.X));
                int row = (int)((k0 + ri) >> 2) - py0;
                int wind = 0;
                for (int j = 0; j + 1 < r.Count; j++)
                {
                    wind += winding ? r[j].Dir : 1;
                    bool inside = winding ? wind != 0 : (wind & 1) != 0;
                    if (!inside) continue;
                    for (long sx = r[j].X; sx < r[j + 1].X; sx++)
                        count[row * w + (int)(sx >> 3) - px0]++;
                }
            }
            alpha = new byte[w * h];
            for (int i = 0; i < count.Length; i++) alpha[i] = (byte)Math.Min(255, (int)(count[i] * 255 / 32.0 + 0.5));
            bounds = new Rectangle(px0, py0, w, h);
            return true;
        }

        private static long CeilDiv(long a, long b)
        {
            long q = a / b;
            if (a % b != 0 && (a > 0) == (b > 0)) q++;
            return q;
        }
    }
}
