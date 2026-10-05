// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// Fonts, as PDF wants them.
//
// A glyph run is already expressed the way PDF's most capable text model wants it: glyph INDICES
// with advances, not characters needing an encoding decided. So the mapping is a Type0 font with
// Identity-H encoding, which says "the two-byte codes in the content stream are glyph ids, use them
// directly". No cmap round trip, no encoding table, and -- the reason it matters here -- no 256-glyph
// ceiling, which is what makes Japanese, Chinese and Korean text work at all.
//
// Glyph ids say nothing about what text they ARE, so each font also carries a ToUnicode map built
// from the characters the runs stood for: that is what makes the text of a page searchable, and
// what a reader copies when someone selects it. A font no run told us the characters of gets none.
//
// This file knows PDF and nothing about where a font comes from. It is shared, link-compiled, by
// WPF's printing (ReachFramework, where PdfFontCache.Wpf.cs describes a GlyphTypeface) and by
// WinForms' (System.Drawing, which describes a face of its own scene renderer): each says what a
// font is through a PdfFontSource and this writes it.
//
// Each face is embedded as a SUBSET of the glyphs the document drew (SfntSubsetter.cs: glyph ids
// kept, so nothing written here changes with it), named with the six-letter tag PDF gives a subset.
// A face the subsetter does not understand is embedded whole.
//

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace System.Windows.Xps.Pdf
{
    /// <summary>What a font is, in the terms a PDF font dictionary needs.</summary>
    internal sealed class PdfFontSource
    {
        /// <summary>The font file and which face of it (0 for a plain font); null file when it
        /// cannot or may not be embedded.</summary>
        internal Func<(byte[] Data, int FaceIndex)> ReadFile;

        /// <summary>A family name for /BaseFont; sanitized on the way out.</summary>
        internal string FamilyName;

        /// <summary>Ascent, descent (negative) and cap height, as fractions of the em.</summary>
        internal double Ascent = 0.8, Descent = -0.2, CapHeight = 0.7;

        /// <summary>A glyph's advance as a fraction of the em, or null when unknown.</summary>
        internal Func<ushort, double?> Advance;
    }

    /// <summary>One embedded font, and the name the content stream refers to it by.</summary>
    internal sealed class PdfFont
    {
        internal string ResourceName;
        internal int ObjectId;
        internal PdfFontSource Source;

        /// <summary>Glyphs actually used, so only their widths need be written out.</summary>
        internal readonly SortedSet<ushort> UsedGlyphs = new SortedSet<ushort>();

        /// <summary>The characters each glyph stood for, where a run said: the ToUnicode map.</summary>
        internal readonly Dictionary<ushort, string> Unicode = new Dictionary<ushort, string>();

        /// <summary>Records that <paramref name="glyph"/> was drawn for <paramref name="text"/>.</summary>
        internal void MapGlyph(ushort glyph, string text)
        {
            UsedGlyphs.Add(glyph);
            if (!string.IsNullOrEmpty(text) && !Unicode.ContainsKey(glyph)) Unicode[glyph] = text;
        }
    }

    /// <summary>
    /// Embeds each distinct font once per document and hands back a stable resource name.
    /// </summary>
    internal sealed partial class PdfFontCache
    {
        private readonly PdfWriter _writer;
        private readonly Dictionary<object, PdfFont> _fonts = new Dictionary<object, PdfFont>();

        internal PdfFontCache(PdfWriter writer)
        {
            _writer = writer;
        }

        internal IEnumerable<PdfFont> Fonts => _fonts.Values;

        /// <summary>The font resource for <paramref name="key"/>, describing it on first sight.
        /// Null if the description is.</summary>
        internal PdfFont For(object key, Func<PdfFontSource> describe)
        {
            if (key == null) return null;

            if (_fonts.TryGetValue(key, out PdfFont existing)) return existing;

            PdfFontSource source = describe();
            if (source == null) return null;

            var font = new PdfFont
            {
                ResourceName = "F" + _fonts.Count.ToString(CultureInfo.InvariantCulture),
                ObjectId = _writer.AllocateObject(),
                Source = source,
            };

            _fonts[key] = font;
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
            PdfFontSource source = font.Source;
            byte[] fontFile = null;
            SfntReader sfnt = null;
            bool subset = false;
            try
            {
                (byte[] data, int faceIndex) = source.ReadFile?.Invoke() ?? (null, 0);
                if (data != null)
                {
                    sfnt = SfntReader.Open(data, faceIndex);
                    fontFile = sfnt?.Subset(font.UsedGlyphs);
                    subset = fontFile != null;
                    fontFile ??= sfnt?.ExtractFace() ?? data;
                }
            }
            catch (System.IO.IOException) { fontFile = null; }
            catch (NotSupportedException) { fontFile = null; }
            catch (UnauthorizedAccessException) { fontFile = null; }

            string baseName = Sanitize(source.FamilyName);
            if (subset) baseName = SubsetTag(font) + "+" + baseName;
            int descendantId = _writer.AllocateObject();
            int descriptorId = _writer.AllocateObject();
            int toUnicodeId = font.Unicode.Count != 0 ? _writer.AllocateObject() : 0;

            // The composite font the content stream names. Identity-H is what makes the two-byte
            // codes in a Tj string mean glyph ids directly.
            _writer.WriteObject(font.ObjectId, string.Concat(
                "<< /Type /Font /Subtype /Type0 /BaseFont ", PdfWriter.Name(baseName),
                " /Encoding /Identity-H /DescendantFonts [ ",
                descendantId.ToString(CultureInfo.InvariantCulture), " 0 R ]",
                toUnicodeId != 0 ? " /ToUnicode " + toUnicodeId.ToString(CultureInfo.InvariantCulture) + " 0 R" : string.Empty,
                " >>"));

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
            descriptor.Append(" /Ascent ").Append(PdfWriter.Number(source.Ascent * 1000));
            descriptor.Append(" /Descent ").Append(PdfWriter.Number(source.Descent * 1000));
            descriptor.Append(" /CapHeight ").Append(PdfWriter.Number(source.CapHeight * 1000));

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

            if (toUnicodeId != 0)
                _writer.WriteStreamObject(toUnicodeId, Encoding.ASCII.GetBytes(ToUnicode(font)));
        }

        /// <summary>
        /// The ToUnicode CMap: two-byte glyph codes to UTF-16BE text. Written as bfchar entries in
        /// blocks of at most a hundred, which is the limit the CMap format sets.
        /// </summary>
        private static string ToUnicode(PdfFont font)
        {
            var sb = new StringBuilder();
            sb.Append("/CIDInit /ProcSet findresource begin\n12 dict begin\nbegincmap\n");
            sb.Append("/CIDSystemInfo << /Registry (Adobe) /Ordering (UCS) /Supplement 0 >> def\n");
            sb.Append("/CMapName /Adobe-Identity-UCS def\n/CMapType 2 def\n");
            sb.Append("1 begincodespacerange\n<0000> <FFFF>\nendcodespacerange\n");

            var entries = new List<KeyValuePair<ushort, string>>(font.Unicode);
            entries.Sort((a, b) => a.Key.CompareTo(b.Key));
            for (int start = 0; start < entries.Count; start += 100)
            {
                int n = Math.Min(100, entries.Count - start);
                sb.Append(n.ToString(CultureInfo.InvariantCulture)).Append(" beginbfchar\n");
                for (int i = start; i < start + n; i++)
                {
                    sb.Append('<').Append(entries[i].Key.ToString("X4", CultureInfo.InvariantCulture)).Append("> <");
                    foreach (char c in entries[i].Value) sb.Append(((int)c).ToString("X4", CultureInfo.InvariantCulture));
                    sb.Append(">\n");
                }
                sb.Append("endbfchar\n");
            }

            sb.Append("endcmap\nCMapName currentdict /CMap defineresource pop\nend\nend\n");
            return sb.ToString();
        }

        /// <summary>
        /// The /W array: widths for the glyphs this document used, in 1/1000 em. Runs of consecutive
        /// glyph ids are written as one range, which for a CJK font is the difference between a few
        /// hundred bytes and a few hundred thousand.
        /// </summary>
        private static string Widths(PdfFont font)
        {
            if (font.Source.Advance == null) return "[ ]";

            var widths = new StringBuilder("[ ");
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

                double? advance = font.Source.Advance(glyph);
                run.Add(advance.HasValue ? advance.Value * 1000 : 1000);
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

        /// <summary>The six capital letters PDF prefixes a subset's name with (ISO 32000 9.6.4): a
        /// hash of the glyphs it holds and of which font it is, so two different subsets of one face
        /// in one document cannot be mistaken for each other.</summary>
        private static string SubsetTag(PdfFont font)
        {
            uint h = 2166136261;
            foreach (char c in font.ResourceName) h = (h ^ c) * 16777619;
            foreach (ushort g in font.UsedGlyphs) h = (h ^ g) * 16777619;
            var tag = new char[6];
            for (int i = 0; i < 6; i++) { tag[i] = (char)('A' + h % 26); h /= 26; if (h == 0) h = 0x9E3779B9; }
            return new string(tag);
        }

        /// <summary>PostScript names admit no spaces, and readers are unforgiving about it.</summary>
        internal static string Sanitize(string name)
        {
            if (string.IsNullOrEmpty(name)) return "Embedded";
            var sb = new StringBuilder(name.Length);
            foreach (char c in name)
            {
                if (c > ' ' && c < 127 && c != '(' && c != ')' && c != '/' && c != '<' && c != '>'
                    && c != '[' && c != ']' && c != '{' && c != '}' && c != '%' && c != '#') sb.Append(c);
            }
            return sb.Length != 0 ? sb.ToString() : "Embedded";
        }
    }
}
