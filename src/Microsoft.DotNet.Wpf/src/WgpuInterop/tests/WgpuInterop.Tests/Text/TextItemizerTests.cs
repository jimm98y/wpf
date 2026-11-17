// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// TextItemizer: a line in runs of one direction and one face, in visual order -- the bidi algorithm's levels
// (GpBidi) and GDI's per-character font link, which a string run of one face and one direction did not have.

using System.Collections.Generic;
using System.Linq;
using Microsoft.Wpf.Interop.WebGpu.Composition.Text;
using Xunit;

namespace WgpuInterop.Tests.Text
{
    public class TextItemizerTests
    {
        // a face with glyphs for some characters only
        private sealed class Face : IFont
        {
            private readonly System.Func<char, bool> _has;
            public Face(System.Func<char, bool> has) => _has = has;
            public int PixelsPerEm => 16;
            public bool TryGetGlyph(int glyphId, out GlyphBitmap glyph) { glyph = default; return false; }
            public int GlyphIndex(char c) => _has(c) ? c : 0;
            public float Advance(int glyphId) => 8;
            public bool TryGetKerning(int l, int r, out float k) { k = 0; return false; }
        }

        private static readonly Face Latin = new Face(c => c < 0x0590);
        private static readonly Face Hebrew = new Face(c => c >= 0x0590 && c <= 0x05FF || c == ' ');

        private static string[] Visual(string text, bool rtl, out List<TextSegment> segments)
        {
            segments = TextItemizer.Itemize(text, rtl, Latin, c => c >= 0x0590 && c <= 0x05FF ? Hebrew : null)!;
            return segments.Select(s => text.Substring(s.Start, s.Length) + (s.RightToLeft ? "<" : ">")).ToArray();
        }

        [Fact]
        public void ALatinLineInItsOwnFace_IsOneRun()
        {
            Assert.Null(TextItemizer.Itemize("Hello world 123", false, Latin, c => null));
        }

        [Fact]
        public void HebrewInALeftToRightLine_IsItsOwnRightToLeftRun_InTheLinkedFace()
        {
            string[] runs = Visual("Hello שלום עולם world", false, out var segments);
            // the space between the Hebrew and "world" sits between the two directions: the paragraph's (rule N2)
            Assert.Equal(new[] { "Hello >", "שלום עולם<", " world>" }, runs);
            Assert.Same(Hebrew, segments[1].Font);
            Assert.Same(Latin, segments[0].Font);
        }

        [Fact]
        public void ARightToLeftParagraph_PutsItsRunsRightToLeft()
        {
            // DT_RTLREADING: "Hello" at the right end, "world" at the left
            string[] runs = Visual("Hello שלום world", true, out _);
            Assert.Equal("world>", runs[0]);
            Assert.Equal("Hello>", runs[runs.Length - 1]);
        }

        [Fact]
        public void DigitsAfterHebrew_StayLeftToRight_InsideTheRightToLeftRun()
        {
            string[] runs = Visual("שלום 123", false, out _);
            // the digits read left to right and come out on the left of the Hebrew
            Assert.Equal("123>", runs[0]);
            Assert.Contains(runs, r => r.StartsWith("שלום"));
        }
    }
}
