// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// Reading and asserting on rendered RGBA8 buffers.
//
// Every ported test used some private variant of "is the pixel at (x,y) roughly this colour", each
// with its own tolerance and its own failure message. They are one thing here so the tolerances are
// visible and comparable, and so a failure says WHICH pixel and WHAT it actually was -- the standalone
// apps mostly printed "[FAIL] rect is blue" with no measured value, which is nearly useless from a
// CI log.
//

using System;
using Xunit;

namespace WgpuInterop.Tests.Harness
{
    internal readonly struct Rgb
    {
        public readonly int R, G, B;
        public Rgb(int r, int g, int b) { R = r; G = g; B = b; }
        public override string ToString() => $"({R},{G},{B})";
    }

    internal sealed class Image
    {
        public readonly byte[] Data;
        public readonly int Width, Height;

        public Image(byte[] rgba, int width, int height)
        { Data = rgba; Width = width; Height = height; }

        public Rgb this[int x, int y]
        {
            get
            {
                Assert.InRange(x, 0, Width - 1);
                Assert.InRange(y, 0, Height - 1);
                int i = (y * Width + x) * 4;
                return new Rgb(Data[i], Data[i + 1], Data[i + 2]);
            }
        }

        public int Alpha(int x, int y) => Data[(y * Width + x) * 4 + 3];

        /// <summary>Assert a pixel is within <paramref name="tol"/> of the expected colour, per channel.</summary>
        public void AssertPixel(int x, int y, int r, int g, int b, int tol, string what)
        {
            Rgb c = this[x, y];
            bool ok = Math.Abs(c.R - r) <= tol && Math.Abs(c.G - g) <= tol && Math.Abs(c.B - b) <= tol;
            Assert.True(ok, $"{what}: pixel ({x},{y}) was {c}, expected ({r},{g},{b}) +/-{tol}");
        }

        /// <summary>Count pixels whose colour is within <paramref name="tol"/> of the given one.</summary>
        public int CountNear(int r, int g, int b, int tol)
        {
            int n = 0;
            for (int i = 0; i < Width * Height; i++)
            {
                int o = i * 4;
                if (Math.Abs(Data[o] - r) <= tol && Math.Abs(Data[o + 1] - g) <= tol && Math.Abs(Data[o + 2] - b) <= tol) n++;
            }
            return n;
        }

        /// <summary>Distinct luminance levels in a region: the standard anti-aliasing-is-present check.</summary>
        public int DistinctLevels(int x0, int y0, int x1, int y1)
        {
            var seen = new bool[256];
            int n = 0;
            for (int y = y0; y < y1; y++)
                for (int x = x0; x < x1; x++)
                {
                    int v = this[x, y].R;
                    if (!seen[v]) { seen[v] = true; n++; }
                }
            return n;
        }

        /// <summary>
        /// Total ink coverage over a region, normalised against the run's own ink colour rather than
        /// against black. Measuring against black counts a fully-covered pixel of non-black ink as
        /// partial coverage, which is exactly the mistake that hid a 23% ink excess in the text
        /// pipeline for the life of the renderer (see Documentation/linux-head.md).
        /// </summary>
        public double Coverage(int x0, int y0, int x1, int y1, int inkLevel, int bgLevel = 255)
        {
            double span = bgLevel - inkLevel;
            Assert.True(Math.Abs(span) > 1e-6, "ink and background levels must differ");
            double total = 0;
            for (int y = y0; y < y1; y++)
                for (int x = x0; x < x1; x++)
                {
                    double cov = (bgLevel - this[x, y].R) / span;
                    if (cov > 0.002) total += cov;
                }
            return total;
        }
    }
}
