// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using Microsoft.Wpf.Interop.WebGpu.Composition;
using Microsoft.Wpf.Interop.WebGpu.Composition.Text;
using Xunit;

namespace WgpuInterop.Tests.Text
{
    /// <summary>How tall a face's HINTED glyphs actually stand at a size.
    /// <para>Windows asks a linked font for a CELL HEIGHT, and win32k computes that cell from what
    /// the font DRIVER reports -- fxMaxAscender + fxMaxDescender, the realized extremes. Every way
    /// of measuring those from outside goes through GetGlyphOutline, which returns the BI-LEVEL fit
    /// whatever the DC asks for, so it answers the wrong regime. This renderer reproduces the
    /// driver's ClearType hinting, so it can answer the right one.</para>
    /// <para>Diagnostic only: prints, never asserts. WPF_CELL_FACES and WPF_CELL_SIZES choose what
    /// to measure and WPF_CELL_REPORT where to write it.</para></summary>
    public class HintedCellTests
    {
        [Fact]
        public void WhatCellTheHintedGlyphsFill()
        {
            string? report = Environment.GetEnvironmentVariable("WPF_CELL_REPORT");
            if (string.IsNullOrEmpty(report)) return;          // off unless asked for

            string[] faces = (Environment.GetEnvironmentVariable("WPF_CELL_FACES") ?? "Tahoma")
                             .Split('|', StringSplitOptions.RemoveEmptyEntries);
            int[] sizes = Array.ConvertAll(
                (Environment.GetEnvironmentVariable("WPF_CELL_SIZES") ?? "16,20,24").Split(','),
                int.Parse);

            var log = new System.Text.StringBuilder();
            foreach (string family in faces)
            {
                string? file = FontFiles.Find(family, bold: false, italic: false);
                if (file is null) { log.AppendLine($"{family}: not installed"); continue; }
                byte[] bytes = File.ReadAllBytes(file);
                int sfnt = FontFiles.SfntOffset(bytes, family, false, false);
                if (CffFont.IsCff(bytes, sfnt)) { log.AppendLine($"{family}: CFF"); continue; }
                var font = new TrueTypeFont(bytes, false, false, sfnt);
                foreach (int ppem in sizes)
                {
                    // y is DOWN and the baseline is zero, so the ascender is the most negative y.
                    float up = 0f, down = 0f;
                    int measured = 0, blank = 0;
                    for (int gid = 0; gid < font.GlyphCount; gid++)
                    {
                        if (!font.TryGetHintedOutline(gid, ppem, out List<PathFigure> figures))
                        { blank++; continue; }
                        measured++;
                        foreach (PathFigure f in figures)
                        {
                            Extend(f.Start, ref up, ref down);
                            foreach (PathSegment seg in f.Segments)
                                switch (seg)
                                {
                                    case LineSegment line: Extend(line.Point, ref up, ref down); break;
                                    case QuadraticBezierSegment q:
                                        Extend(q.Control, ref up, ref down);
                                        Extend(q.Point, ref up, ref down);
                                        break;
                                }
                        }
                    }
                    int ascender = (int) MathF.Ceiling(-up), descender = (int) MathF.Ceiling(down);
                    log.AppendLine($"{family} @{ppem}ppem  glyphs={measured} blank={blank}"
                                   + $"  ascender={-up:0.###} ({ascender})  descender={down:0.###} ({descender})"
                                   + $"  CELL={ascender + descender}");
                }
            }
            File.WriteAllText(report, log.ToString());

            static void Extend(Vector2 p, ref float up, ref float down)
            {
                if (p.Y < up) up = p.Y;
                if (p.Y > down) down = p.Y;
            }
        }
    }
}
