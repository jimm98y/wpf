// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// Region combining, pixel for pixel, and what a clip that is set and restored around every item of a
// repaint costs. DotNetBar does that for each button of a ribbon on each hover: the clip was a tree
// copied whole on every Graphics.Clip, its device region worked out again from its leaves on every
// change, and each row of each combine went through a SortedSet -- most of a ribbon repaint's time
// was the garbage collector's.
//

using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using Xunit;

namespace Wpf.WinFormsInterop.Tests
{
    public class RegionCombineTests
    {
        /// <summary>Random rectangles combined by every mode, checked against the set algebra itself
        /// at every pixel of the area.</summary>
        [Fact]
        public void CombiningRectangles_GivesExactlyTheSetAlgebra()
        {
            var rnd = new Random(7);
            const int W = 48, H = 40;
            using var bmp = new Bitmap(4, 4);
            using var g = Graphics.FromImage(bmp);
            for (int round = 0; round < 60; round++)
            {
                var truth = new bool[W, H];
                Rectangle first = RandomRect(rnd, W, H);
                using var region = new Region(first);
                Mark(truth, first, (a, b) => b);
                int steps = 1 + rnd.Next(6);
                for (int s = 0; s < steps; s++)
                {
                    Rectangle r = RandomRect(rnd, W, H);
                    int op = rnd.Next(5);
                    switch (op)
                    {
                        case 0: region.Intersect(r); Mark(truth, r, (a, b) => a && b); break;
                        case 1: region.Union(r); Mark(truth, r, (a, b) => a || b); break;
                        case 2: region.Xor(r); Mark(truth, r, (a, b) => a != b); break;
                        case 3: region.Exclude(r); Mark(truth, r, (a, b) => a && !b); break;
                        default: region.Complement(r); Mark(truth, r, (a, b) => b && !a); break;
                    }
                }
                for (int y = 0; y < H; y++)
                    for (int x = 0; x < W; x++)
                        Assert.True(region.IsVisible(x, y, g) == truth[x, y], $"round {round}: pixel {x},{y} should be {(truth[x, y] ? "in" : "out")}");
            }
        }

        static Rectangle RandomRect(Random rnd, int w, int h)
        {
            int x = rnd.Next(w), y = rnd.Next(h);
            return new Rectangle(x, y, 1 + rnd.Next(w - x), 1 + rnd.Next(h - y));
        }

        static void Mark(bool[,] t, Rectangle r, Func<bool, bool, bool> op)
        {
            for (int y = 0; y < t.GetLength(1); y++)
                for (int x = 0; x < t.GetLength(0); x++)
                    t[x, y] = op(t[x, y], r.Contains(x, y));
        }

        /// <summary>Setting and restoring a clip around each of many items, under a clip already many
        /// levels deep: what it allocates does not grow with the depth or the count the way a copy of the
        /// whole tree each time did.</summary>
        [Fact]
        public void SavingAndRestoringAClip_UnderADeepClip_StaysCheap()
        {
            using var bmp = new Bitmap(400, 100, PixelFormat.Format32bppArgb);
            using var g = Graphics.FromImage(bmp);
            for (int i = 0; i < 40; i++) g.SetClip(new Rectangle(i, 0, 400 - 2 * i, 100), CombineMode.Intersect);
            for (int i = 0; i < 50; i++) { Region r = g.Clip; g.SetClip(new Rectangle(i * 4, 10, 30, 30), CombineMode.Intersect); g.Clip = r; r.Dispose(); }   // warm
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 400; i++)
            {
                Region old = g.Clip;
                g.SetClip(new Rectangle(i % 360, 10, 30, 30), CombineMode.Intersect);
                g.FillRectangle(Brushes.Gold, i % 360, 10, 30, 30);
                g.Clip = old;
                old.Dispose();
            }
            long perItem = (GC.GetAllocatedBytesForCurrentThread() - before) / 400;
            Console.WriteLine($"RegionCombineTests: {perItem} bytes per clip"); Assert.True(perItem < 24 * 1024, $"{perItem} bytes allocated per saved, narrowed and restored clip");
        }
    }
}
