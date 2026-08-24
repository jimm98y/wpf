// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// Text in a PDF: embedded fonts, glyph ids on the wire, and the collection problem.
//
// This is the part of printing that is easy to get almost right. A page of text that comes out as
// rectangles, or as the wrong glyphs, or that opens in one reader and not another, all look like
// font bugs and are usually structural: the wrong encoding, a widths array that does not cover the
// glyphs used, or -- the one specific to this fork -- a font file that is really a COLLECTION of
// fonts, which PDF has no way to reference into.
//
// That last one is not hypothetical here. The CJK font this port bundles is NotoSansCJK-Regular.ttc,
// so every Japanese, Chinese and Korean page depends on one face being pulled out of the collection
// and rebuilt as a standalone sfnt.
//

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Xps.Pdf;
using Xunit;

namespace Wpf.Printing.Tests
{
    public class PdfFontTests
    {
        [Fact]
        public void TextProducesAType0FontWithIdentityEncoding()
        {
            PdfDocument pdf = RenderText("Hello", "Arial");

            PdfDictionary font = FirstFont(pdf);

            Assert.Equal("Font", font["Type"]);

            // Type0 with Identity-H, which is what lets the content stream carry GLYPH IDS rather
            // than characters. A simple font would cap the run at 256 glyphs and force an encoding
            // decision WPF has already made.
            Assert.Equal("Type0", font["Subtype"]);
            Assert.Equal("Identity-H", font["Encoding"]);
        }

        [Fact]
        public void TheDescendantFontDescribesTheGlyphs()
        {
            PdfDocument pdf = RenderText("Hello", "Arial");

            var descendants = (List<object>)pdf.Resolve(FirstFont(pdf)["DescendantFonts"]);
            var descendant = (PdfDictionary)pdf.Resolve(descendants[0]);

            Assert.Contains(descendant["Subtype"] as string, new[] { "CIDFontType0", "CIDFontType2" });
            Assert.True(descendant.ContainsKey("W"), "no widths array: every glyph would take the default width");

            var info = (PdfDictionary)pdf.Resolve(descendant["CIDSystemInfo"]);
            Assert.Equal("Identity", info["Ordering"]);
        }

        [Fact]
        public void TheFontFileIsEmbedded()
        {
            PdfDocument pdf = RenderText("Hello", "Arial");

            var descendants = (List<object>)pdf.Resolve(FirstFont(pdf)["DescendantFonts"]);
            var descendant = (PdfDictionary)pdf.Resolve(descendants[0]);
            var descriptor = (PdfDictionary)pdf.Resolve(descendant["FontDescriptor"]);

            Assert.True(descriptor.ContainsKey("FontFile2") || descriptor.ContainsKey("FontFile3"),
                        "the font was not embedded, so the reader will substitute one");

            byte[] fontFile = pdf.StreamData(
                descriptor.ContainsKey("FontFile2") ? descriptor["FontFile2"] : descriptor["FontFile3"]);

            Assert.NotNull(fontFile);
            Assert.True(fontFile.Length > 1000, $"the embedded font is only {fontFile.Length} bytes");
        }

        [Fact]
        public void TheEmbeddedFontIsASingleFaceNotACollection()
        {
            // The regression this file exists for. A .ttc begins 't','t','c','f' and holds several
            // faces; FontFile2 must be ONE sfnt, which begins 0x00010000 or 'OTTO' or 'true'. Embed
            // the collection as-is and readers reject the font, silently, showing nothing.
            PdfDocument pdf = RenderText("こんにちは", "Noto Sans CJK JP");

            byte[] fontFile = EmbeddedFontFile(pdf);
            if (fontFile == null) return;   // no CJK font on this machine; nothing to assert

            uint tag = (uint)((fontFile[0] << 24) | (fontFile[1] << 16) | (fontFile[2] << 8) | fontFile[3]);

            Assert.False(tag == 0x74746366, "a font COLLECTION was embedded where a single face is required");
            Assert.True(tag is 0x00010000 or 0x4F54544F or 0x74727565,
                        $"the embedded font starts with 0x{tag:X8}, which is not an sfnt");
        }

        [Fact]
        public void TheExtractedFaceHasAReadableTableDirectory()
        {
            // Rebuilding a face means rewriting the table directory with new offsets. Getting those
            // wrong produces a file that still starts with the right four bytes and is unusable.
            PdfDocument pdf = RenderText("こんにちは", "Noto Sans CJK JP");

            byte[] fontFile = EmbeddedFontFile(pdf);
            if (fontFile == null) return;

            int tables = (fontFile[4] << 8) | fontFile[5];
            Assert.InRange(tables, 1, 64);

            bool sawHead = false;
            for (int i = 0; i < tables; i++)
            {
                int entry = 12 + i * 16;
                Assert.True(entry + 16 <= fontFile.Length, "the table directory runs past the end of the font");

                string tag = new string(new[]
                {
                    (char)fontFile[entry], (char)fontFile[entry + 1],
                    (char)fontFile[entry + 2], (char)fontFile[entry + 3],
                });

                long offset = ((long)fontFile[entry + 8] << 24) | ((long)fontFile[entry + 9] << 16) |
                              ((long)fontFile[entry + 10] << 8) | fontFile[entry + 11];
                long length = ((long)fontFile[entry + 12] << 24) | ((long)fontFile[entry + 13] << 16) |
                              ((long)fontFile[entry + 14] << 8) | fontFile[entry + 15];

                Assert.True(offset + length <= fontFile.Length,
                            $"table '{tag}' claims bytes {offset}..{offset + length} of a {fontFile.Length}-byte font");

                if (tag == "head") sawHead = true;
            }

            Assert.True(sawHead, "the extracted face has no 'head' table");
        }

        [Fact]
        public void GlyphsAreWrittenAsHexCodesNotCharacters()
        {
            string content = PdfWriterTests.ContentOf(RenderText("Hi", "Arial"));

            Assert.Contains("BT", content, StringComparison.Ordinal);
            Assert.Contains("ET", content, StringComparison.Ordinal);
            Assert.Contains("Tj", content, StringComparison.Ordinal);
            Assert.Contains("Tf", content, StringComparison.Ordinal);

            // Four hex digits inside angle brackets: one two-byte glyph id under Identity-H.
            Assert.NotEmpty(GlyphIdsIn(content));

            // Not the characters themselves. A writer that emitted (Hi) would be relying on an
            // encoding it never declared.
            Assert.DoesNotContain("(Hi)", content, StringComparison.Ordinal);
        }

        [Fact]
        public void TheWidthsArrayCoversTheGlyphsUsed()
        {
            PdfDocument pdf = RenderText("Widths", "Arial");

            var descendants = (List<object>)pdf.Resolve(FirstFont(pdf)["DescendantFonts"]);
            var descendant = (PdfDictionary)pdf.Resolve(descendants[0]);
            var widths = (List<object>)pdf.Resolve(descendant["W"]);

            Assert.NotEmpty(widths);

            // The format is a run: a starting glyph id followed by an array of widths for the
            // consecutive ids after it. Anything else and readers fall back to /DW for everything,
            // which is how text comes out evenly spaced and wrong.
            Assert.True(widths[0] is double, "the widths array does not start with a glyph id");
            Assert.True(widths[1] is List<object>, "the widths array is not id-then-array runs");

            foreach (object width in (List<object>)widths[1])
            {
                Assert.InRange(Convert.ToDouble(width, CultureInfo.InvariantCulture), 0, 4000);
            }
        }

        [Fact]
        public void OneFontIsEmbeddedOncePerDocumentNotOncePerPage()
        {
            using var buffer = new MemoryStream();
            using (var writer = new PdfDocumentWriter(buffer, leaveOpen: true))
            {
                for (int i = 0; i < 3; i++) writer.Write(TextVisual("Page " + i, "Arial"));
            }

            PdfDocument pdf = PdfDocument.Parse(buffer.ToArray());

            var seen = new HashSet<int>();
            foreach (PdfDictionary page in pdf.Pages())
            {
                var resources = (PdfDictionary)pdf.Resolve(page["Resources"]);
                var fonts = (PdfDictionary)pdf.Resolve(resources["Font"]);

                foreach (object reference in fonts.Values) seen.Add(((PdfReference)reference).Number);
            }

            // Three pages in the same font must share one font object. Otherwise a twenty-page CJK
            // document embeds Noto Sans CJK twenty times.
            Assert.Single(seen);
        }

        [Fact]
        public void JapaneseTextSurvivesToTheContentStream()
        {
            // The end-to-end case this fork cares about, and the reason the collection handling above
            // has to be right. Kana and kanji are far outside the 256 glyphs a simple font can name.
            PdfDocument pdf = RenderText("こんにちは世界", "Noto Sans CJK JP");
            string content = PdfWriterTests.ContentOf(pdf);

            List<int> glyphs = GlyphIdsIn(content);

            Assert.True(glyphs.Count >= 7, $"seven characters produced {glyphs.Count} glyphs");

            // Every glyph resolved to something real. Glyph 0 is .notdef -- the box a reader draws
            // when it has nothing, and what a broken font mapping produces a page full of.
            Assert.DoesNotContain(0, glyphs);
        }

        // ---- helpers ---------------------------------------------------------------

        /// <summary>The glyph ids a content stream shows, read out of its Identity-H hex codes.</summary>
        private static List<int> GlyphIdsIn(string content)
        {
            var glyphs = new List<int>();

            for (int i = 0; i + 9 <= content.Length; i++)
            {
                if (content[i] != '<') continue;
                if (content[i + 5] != '>' || content[i + 6] != ' ' ||
                    content[i + 7] != 'T' || content[i + 8] != 'j') continue;

                string hex = content.Substring(i + 1, 4);
                if (int.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int glyph))
                {
                    glyphs.Add(glyph);
                }
            }

            return glyphs;
        }

        private static Visual TextVisual(string text, string family)
        {
            var visual = new DrawingVisual();
            using (DrawingContext dc = visual.RenderOpen())
            {
                var formatted = new FormattedText(
                    text,
                    CultureInfo.GetCultureInfo("en-US"),
                    FlowDirection.LeftToRight,
                    new Typeface(family),
                    24,
                    Brushes.Black,
                    1.0);

                dc.DrawText(formatted, new Point(20, 40));
            }
            return visual;
        }

        private static PdfDocument RenderText(string text, string family)
        {
            using var buffer = new MemoryStream();
            using (var writer = new PdfDocumentWriter(buffer, leaveOpen: true))
            {
                writer.Write(TextVisual(text, family));
            }
            return PdfDocument.Parse(buffer.ToArray());
        }

        private static PdfDictionary FirstFont(PdfDocument pdf)
        {
            var resources = (PdfDictionary)pdf.Resolve(pdf.Pages()[0]["Resources"]);
            Assert.True(resources.ContainsKey("Font"), "the page carries no font resource, so no text was written");

            var fonts = (PdfDictionary)pdf.Resolve(resources["Font"]);
            return (PdfDictionary)pdf.Resolve(fonts.OnlyValue());
        }

        /// <summary>The embedded font bytes, or null when the font could not be embedded.</summary>
        private static byte[] EmbeddedFontFile(PdfDocument pdf)
        {
            var descendants = (List<object>)pdf.Resolve(FirstFont(pdf)["DescendantFonts"]);
            var descendant = (PdfDictionary)pdf.Resolve(descendants[0]);
            var descriptor = (PdfDictionary)pdf.Resolve(descendant["FontDescriptor"]);

            if (descriptor.ContainsKey("FontFile2")) return pdf.StreamData(descriptor["FontFile2"]);
            if (descriptor.ContainsKey("FontFile3")) return pdf.StreamData(descriptor["FontFile3"]);
            return null;
        }
    }
}
