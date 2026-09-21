// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// Fonts, as PDF wants them.
//
// A WPF GlyphRun is already expressed the way PDF's most capable text model wants it: glyph INDICES
// with advances, not characters needing an encoding decided. So the mapping is a Type0 font with
// Identity-H encoding, which says "the two-byte codes in the content stream are glyph ids, use them
// directly". No cmap round trip, no encoding table, and -- the reason it matters here -- no 256-glyph
// ceiling, which is what makes Japanese, Chinese and Korean text work at all.
//
// The tradeoff taken deliberately: the whole font file is embedded rather than a subset. The
// platform's subsetter (GlyphTypeface.ComputeSubset) throws off Windows, and writing one is a
// separate piece of work. A page of Japanese therefore carries all of Noto Sans CJK, which is
// correct and about 19 MB. The seam for fixing that is right here, in Embed.
//

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows.Media;

namespace System.Windows.Xps.Pdf
{
    /// <summary>One embedded font, and the name the content stream refers to it by.</summary>
    internal sealed class PdfFont
    {
        internal string ResourceName;
        internal int ObjectId;
        internal GlyphTypeface Typeface;

        /// <summary>Glyphs actually used, so only their widths need be written out.</summary>
        internal readonly SortedSet<ushort> UsedGlyphs = new SortedSet<ushort>();
    }

    /// <summary>
    /// Embeds each distinct typeface once per document and hands back a stable resource name.
    /// </summary>
    internal sealed class PdfFontCache
    {
        private readonly PdfWriter _writer;
        private readonly Dictionary<GlyphTypeface, PdfFont> _fonts = new Dictionary<GlyphTypeface, PdfFont>();

        internal PdfFontCache(PdfWriter writer)
        {
            _writer = writer;
        }

        internal IEnumerable<PdfFont> Fonts => _fonts.Values;

        /// <summary>The font resource for a typeface, creating it on first sight. Null if unusable.</summary>
        internal PdfFont For(GlyphTypeface typeface)
        {
            if (typeface == null) return null;

            if (_fonts.TryGetValue(typeface, out PdfFont existing)) return existing;

            var font = new PdfFont
            {
                ResourceName = "F" + _fonts.Count.ToString(CultureInfo.InvariantCulture),
                ObjectId = _writer.AllocateObject(),
                Typeface = typeface,
            };

            _fonts[typeface] = font;
            return font;
        }

        /// <summary>
        /// Writes every font gathered during the document. Called once at the end, because the width
        /// array can only be written when it is known which glyphs were used.
        /// </summary>
        internal void WriteAll()
        {
            foreach (PdfFont font in _fonts.Values) Write(font);
        }

        private void Write(PdfFont font)
        {
            byte[] fontFile = ReadFontFile(font.Typeface, out SfntReader sfnt);

            string baseName = BaseName(font.Typeface);
            int descendantId = _writer.AllocateObject();
            int descriptorId = _writer.AllocateObject();

            // The composite font the content stream names. Identity-H is what makes the two-byte
            // codes in a Tj string mean glyph ids directly.
            _writer.WriteObject(font.ObjectId, string.Concat(
                "<< /Type /Font /Subtype /Type0 /BaseFont ", PdfWriter.Name(baseName),
                " /Encoding /Identity-H /DescendantFonts [ ",
                descendantId.ToString(CultureInfo.InvariantCulture), " 0 R ] >>"));

            bool cff = sfnt?.IsCff ?? false;

            _writer.WriteObject(descendantId, string.Concat(
                "<< /Type /Font /Subtype ", cff ? "/CIDFontType0" : "/CIDFontType2",
                " /BaseFont ", PdfWriter.Name(baseName),
                " /CIDSystemInfo << /Registry (Adobe) /Ordering (Identity) /Supplement 0 >>",
                " /FontDescriptor ", descriptorId.ToString(CultureInfo.InvariantCulture), " 0 R",
                cff ? string.Empty : " /CIDToGIDMap /Identity",
                " /DW 1000 /W ", Widths(font), " >>"));

            (int XMin, int YMin, int XMax, int YMax) box = sfnt?.BoundingBox ?? (-500, -300, 1500, 1000);

            var descriptor = new StringBuilder();
            descriptor.Append("<< /Type /FontDescriptor /FontName ").Append(PdfWriter.Name(baseName));

            // Symbolic, because with Identity-H the codes are glyph ids and no standard encoding
            // applies. Claiming Nonsymbolic invites readers to second-guess the mapping.
            descriptor.Append(" /Flags 4");
            descriptor.Append(" /FontBBox [ ").Append(box.XMin).Append(' ').Append(box.YMin).Append(' ')
                      .Append(box.XMax).Append(' ').Append(box.YMax).Append(" ]");
            descriptor.Append(" /ItalicAngle ").Append(PdfWriter.Number(sfnt?.ItalicAngle ?? 0));
            descriptor.Append(" /Ascent ").Append(PdfWriter.Number(font.Typeface.Baseline * 1000));
            descriptor.Append(" /Descent ").Append(PdfWriter.Number((font.Typeface.Baseline - font.Typeface.Height) * 1000));
            descriptor.Append(" /CapHeight ").Append(PdfWriter.Number(font.Typeface.CapsHeight * 1000));

            // StemV is required and no reader of an embedded font uses it; a plausible value keeps
            // validators quiet without pretending to a precision we do not have.
            descriptor.Append(" /StemV 80");

            if (fontFile != null)
            {
                int fileId = _writer.AllocateObject();
                descriptor.Append(cff ? " /FontFile3 " : " /FontFile2 ")
                          .Append(fileId.ToString(CultureInfo.InvariantCulture)).Append(" 0 R");
                descriptor.Append(" >>");

                _writer.WriteObject(descriptorId, descriptor.ToString());

                string extra = cff
                    ? "/Subtype /OpenType"
                    : "/Length1 " + fontFile.Length.ToString(CultureInfo.InvariantCulture);
                _writer.WriteStreamObject(fileId, fontFile, extra);
            }
            else
            {
                // No embeddable file. The text still lays out correctly because the widths are
                // written either way; the reader substitutes a face.
                descriptor.Append(" >>");
                _writer.WriteObject(descriptorId, descriptor.ToString());
            }
        }

        /// <summary>
        /// The /W array: widths for the glyphs this document used, in 1/1000 em. Runs of consecutive
        /// glyph ids are written as one range, which for a CJK font is the difference between a few
        /// hundred bytes and a few hundred thousand.
        /// </summary>
        private static string Widths(PdfFont font)
        {
            var widths = new StringBuilder("[ ");

            IDictionary<ushort, double> advances = null;
            try { advances = font.Typeface.AdvanceWidths; }
            catch (NotSupportedException) { }

            if (advances == null) return "[ ]";

            var run = new List<double>();
            int runStart = -1;
            int previous = -2;

            foreach (ushort glyph in font.UsedGlyphs)
            {
                if (glyph != previous + 1)
                {
                    Flush(widths, runStart, run);
                    runStart = glyph;
                    run.Clear();
                }

                run.Add(advances.TryGetValue(glyph, out double advance) ? advance * 1000 : 1000);
                previous = glyph;
            }

            Flush(widths, runStart, run);
            return widths.Append(']').ToString();
        }

        private static void Flush(StringBuilder widths, int start, List<double> run)
        {
            if (start < 0 || run.Count == 0) return;

            widths.Append(start.ToString(CultureInfo.InvariantCulture)).Append(" [ ");
            foreach (double w in run) widths.Append(PdfWriter.Number(w)).Append(' ');
            widths.Append("] ");
        }

        /// <summary>
        /// The font's bytes, reduced to a single face. Returns null when the font cannot be embedded,
        /// either because it does not permit it or because its file cannot be read.
        /// </summary>
        private static byte[] ReadFontFile(GlyphTypeface typeface, out SfntReader sfnt)
        {
            sfnt = null;

            try
            {
                // A font may forbid embedding outright, and honouring that is not optional.
                FontEmbeddingRight rights = typeface.EmbeddingRights;
                if (rights == FontEmbeddingRight.RestrictedLicense) return null;

                using Stream stream = typeface.GetFontStream();
                if (stream == null) return null;

                using var buffer = new MemoryStream();
                stream.CopyTo(buffer);
                byte[] bytes = buffer.ToArray();

                sfnt = SfntReader.Open(bytes, FaceIndex(typeface));
                return sfnt?.ExtractFace() ?? bytes;
            }
            catch (IOException) { return null; }
            catch (NotSupportedException) { return null; }
            catch (UnauthorizedAccessException) { return null; }
        }

        /// <summary>
        /// Which face of a collection this typeface is. WPF puts it in the font URI's fragment, so
        /// "file:///.../NotoSansCJK-Regular.ttc#2" is the third face.
        /// </summary>
        private static int FaceIndex(GlyphTypeface typeface)
        {
            try
            {
                string fragment = typeface.FontUri?.Fragment;
                if (string.IsNullOrEmpty(fragment) || fragment.Length < 2) return 0;

                return int.TryParse(fragment.AsSpan(1), NumberStyles.Integer, CultureInfo.InvariantCulture,
                                    out int index) ? index : 0;
            }
            catch (InvalidOperationException) { return 0; }
        }

        private static string BaseName(GlyphTypeface typeface)
        {
            try
            {
                foreach (KeyValuePair<Globalization.CultureInfo, string> name in typeface.FamilyNames)
                {
                    if (!string.IsNullOrEmpty(name.Value)) return Sanitize(name.Value);
                }
            }
            catch (NotSupportedException) { }

            return "Embedded";
        }

        /// <summary>PostScript names admit no spaces, and readers are unforgiving about it.</summary>
        private static string Sanitize(string name)
        {
            var sb = new StringBuilder(name.Length);
            foreach (char c in name)
            {
                if (c > ' ' && c < 127 && c != '(' && c != ')' && c != '/' && c != '<' && c != '>') sb.Append(c);
            }
            return sb.Length != 0 ? sb.ToString() : "Embedded";
        }
    }
}
