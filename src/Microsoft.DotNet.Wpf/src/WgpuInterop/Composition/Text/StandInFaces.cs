// Windows faces the stack builds where the system has none of that name: an open font's glyphs under a
// Windows face's metrics.
//
// Microsoft Sans Serif is WinForms' SystemFonts.DefaultFont and the font a designer-made form is in --
// DotNetBar's ribbon is laid out in it. Windows always has it; a browser, Linux, iOS and Android do not,
// and the lookup fell to the default face, Liberation Sans: Arial's widths and line heights, not
// Microsoft Sans Serif's. Item labels wrapped and measured differently, the ribbon came out eleven pixels
// shorter than its items, and its rows of buttons were cut off at the top.
//
// So where no Microsoft Sans Serif is installed, one is made: Liberation Sans' outlines with Microsoft
// Sans Serif's advance for every character it has, its hinted widths at the sizes GDI looks them up at
// ('hdmx'), its line metrics and 'VDMX' (StandInMetrics, numbers only), and no kerning, which Microsoft
// Sans Serif does not do. Text lays out as on Windows; only the glyph shapes are Liberation's. The face
// is written to a folder of its own that both font catalogs read (FontFiles, SystemFontCatalog). Liberation
// Sans' own files are not touched.

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace Microsoft.Wpf.Interop.WebGpu.Composition.Text
{
    internal static class StandInFaces
    {
        private static readonly object s_lock = new object();
        private static bool s_tried;
        private static string? s_directory;

        /// <summary>The folder holding the faces built for this process, or null where none is needed or
        /// none could be built. <paramref name="fontDirectories"/> are where the real faces and the open
        /// ones would be.</summary>
        internal static string? Directory(IEnumerable<string> fontDirectories)
        {
            if (OperatingSystem.IsWindows()) return null;
            lock (s_lock)
            {
                if (s_tried) return s_directory;
                s_tried = true;
                var dirs = new List<string>(fontDirectories);
                if (Has(dirs, "micross.ttf", "Microsoft Sans Serif.ttf")) return null;
                string? liberation = Locate(dirs, "LiberationSans-Regular.ttf");
                if (liberation == null) return null;
                try
                {
                    byte[] face = MicrosoftSansSerif(File.ReadAllBytes(liberation));
                    string dir = Path.Combine(Path.GetTempPath(), "WpfWebGpu-StandIns-" + Environment.ProcessId);
                    System.IO.Directory.CreateDirectory(dir);
                    File.WriteAllBytes(Path.Combine(dir, "micross.ttf"), face);
                    s_directory = dir;
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException or IndexOutOfRangeException)
                {
                    s_directory = null;   // a face that cannot be made leaves the family to the default face, as before
                }
                return s_directory;
            }
        }

        /// <summary>Microsoft Sans Serif over <paramref name="liberationSans"/>' glyphs (LiberationSans-Regular.ttf).</summary>
        internal static byte[] MicrosoftSansSerif(byte[] liberationSans)
            => Build(liberationSans, Metrics(StandInMetrics.MicrosoftSansSerif), "Microsoft Sans Serif", "MicrosoftSansSerif");

        private static bool Has(List<string> dirs, params string[] names) => Locate(dirs, names) != null;

        private static string? Locate(List<string> dirs, params string[] names)
        {
            foreach (string d in dirs)
            {
                if (!System.IO.Directory.Exists(d)) continue;
                foreach (string n in names)
                {
                    string p = Path.Combine(d, n);
                    if (File.Exists(p)) return p;
                }
            }
            return null;
        }

        private sealed class Data
        {
            public int UnitsPerEm;
            public int[] Codepoints = Array.Empty<int>();
            public ushort[] Advances = Array.Empty<ushort>();
            public byte[] Ppems = Array.Empty<byte>();
            public byte[][] Widths = Array.Empty<byte[]>();   // per size, per codepoint
            public byte[] Hhea = Array.Empty<byte>();         // ascender, descender, lineGap, advanceWidthMax
            public byte[] Os2Avg = Array.Empty<byte>(), Os2Ranges = Array.Empty<byte>(), Os2Metrics = Array.Empty<byte>(), Os2CodePages = Array.Empty<byte>();
            public byte[] Vdmx = Array.Empty<byte>();
        }

        private static Data Metrics(string packed)
        {
            byte[] raw;
            using (var input = new ZLibStream(new MemoryStream(Convert.FromBase64String(packed)), CompressionMode.Decompress))
            using (var output = new MemoryStream())
            {
                input.CopyTo(output);
                raw = output.ToArray();
            }
            if (raw.Length < 4 || raw[0] != 'W' || raw[1] != 'S' || raw[2] != 'I' || raw[3] != '1') throw new InvalidDataException("stand-in metrics");
            int p = 4;
            var d = new Data { UnitsPerEm = BitConverter.ToUInt16(raw, p) };
            p += 2;
            int n = (int)BitConverter.ToUInt32(raw, p); p += 4;
            d.Codepoints = new int[n];
            for (int i = 0; i < n; i++, p += 4) d.Codepoints[i] = (int)BitConverter.ToUInt32(raw, p);
            d.Advances = new ushort[n];
            for (int i = 0; i < n; i++, p += 2) d.Advances[i] = BitConverter.ToUInt16(raw, p);
            int recs = raw[p++];
            d.Ppems = raw.AsSpan(p, recs).ToArray(); p += recs;
            d.Widths = new byte[recs][];
            for (int r = 0; r < recs; r++, p += n) d.Widths[r] = raw.AsSpan(p, n).ToArray();
            d.Hhea = raw.AsSpan(p, 8).ToArray(); p += 8;
            d.Os2Avg = raw.AsSpan(p, 2).ToArray(); p += 2;
            d.Os2Ranges = raw.AsSpan(p, 26).ToArray(); p += 26;
            d.Os2Metrics = raw.AsSpan(p, 10).ToArray(); p += 10;
            d.Os2CodePages = raw.AsSpan(p, 8).ToArray(); p += 8;
            int vdmx = (int)BitConverter.ToUInt32(raw, p); p += 4;
            d.Vdmx = raw.AsSpan(p, vdmx).ToArray();
            return d;
        }

        private static int U16(byte[] b, int o) => b[o] << 8 | b[o + 1];
        private static void Put16(byte[] b, int o, int v) { b[o] = (byte)(v >> 8); b[o + 1] = (byte)v; }

        /// <summary>The open face <paramref name="glyphs"/> with <paramref name="m"/>'s metrics, named
        /// <paramref name="family"/>: a complete sfnt.</summary>
        private static byte[] Build(byte[] glyphs, Data m, string family, string postScript)
        {
            var tables = new Dictionary<string, byte[]>(StringComparer.Ordinal);
            int count = U16(glyphs, 4);
            for (int i = 0; i < count; i++)
            {
                int e = 12 + 16 * i;
                string tag = Encoding.ASCII.GetString(glyphs, e, 4);
                int off = (int)((uint)(U16(glyphs, e + 8) << 16 | U16(glyphs, e + 10)));
                int len = (int)((uint)(U16(glyphs, e + 12) << 16 | U16(glyphs, e + 14)));
                tables[tag] = glyphs.AsSpan(off, len).ToArray();
            }
            if (U16(tables["head"], 18) != m.UnitsPerEm) throw new InvalidDataException("stand-in: units per em differ");

            int numGlyphs = U16(tables["maxp"], 4);
            byte[] hhea = tables["hhea"], hmtx = tables["hmtx"];
            int longMetrics = U16(hhea, 34);
            var advance = new int[numGlyphs];
            var lsb = new short[numGlyphs];
            for (int g = 0; g < numGlyphs; g++)
            {
                int k = Math.Min(g, longMetrics - 1);
                advance[g] = U16(hmtx, 4 * k);
                lsb[g] = g < longMetrics ? (short)U16(hmtx, 4 * g + 2) : (short)U16(hmtx, 4 * longMetrics + 2 * (g - longMetrics));
            }

            // the Windows face's advance and hinted widths for each glyph one of its characters maps to
            var font = new TrueTypeFont(glyphs);
            var widths = new byte[m.Ppems.Length][];
            for (int r = 0; r < widths.Length; r++)
            {
                widths[r] = new byte[numGlyphs];
                for (int g = 0; g < numGlyphs; g++)
                    widths[r][g] = (byte)Math.Min(255, (advance[g] * m.Ppems[r] + m.UnitsPerEm / 2) / m.UnitsPerEm);
            }
            var taken = new bool[numGlyphs];
            for (int i = 0; i < m.Codepoints.Length; i++)
            {
                int g = font.GlyphIndexOf(m.Codepoints[i]);
                if (g <= 0 || g >= numGlyphs || taken[g]) continue;
                taken[g] = true;
                advance[g] = m.Advances[i];
                for (int r = 0; r < widths.Length; r++) widths[r][g] = m.Widths[r][i];
            }

            var newHmtx = new byte[4 * numGlyphs];
            int maxAdvance = 0;
            for (int g = 0; g < numGlyphs; g++)
            {
                Put16(newHmtx, 4 * g, advance[g]);
                Put16(newHmtx, 4 * g + 2, (ushort)lsb[g]);
                maxAdvance = Math.Max(maxAdvance, advance[g]);
            }
            tables["hmtx"] = newHmtx;

            byte[] newHhea = (byte[])hhea.Clone();
            Array.Copy(m.Hhea, 0, newHhea, 4, 6);             // ascender, descender, lineGap
            Put16(newHhea, 10, maxAdvance);
            Put16(newHhea, 34, numGlyphs);
            tables["hhea"] = newHhea;

            byte[] os2 = (byte[])tables["OS/2"].Clone();
            Array.Copy(m.Os2Avg, 0, os2, 2, 2);               // xAvgCharWidth: GDI's tmAveCharWidth
            Array.Copy(m.Os2Ranges, 0, os2, 32, 26);          // panose, Unicode ranges
            Array.Copy(m.Os2Metrics, 0, os2, 68, 10);         // typo and Windows ascent and descent
            if (os2.Length >= 86) Array.Copy(m.Os2CodePages, 0, os2, 78, 8);
            Put16(os2, 62, U16(os2, 62) & ~0x80);             // the Windows metrics govern (no USE_TYPO_METRICS)
            tables["OS/2"] = os2;

            int stride = (2 + numGlyphs + 3) & ~3;
            var hdmx = new byte[8 + stride * widths.Length];
            Put16(hdmx, 2, widths.Length);
            hdmx[4] = (byte)(stride >> 24); hdmx[5] = (byte)(stride >> 16); hdmx[6] = (byte)(stride >> 8); hdmx[7] = (byte)stride;
            for (int r = 0; r < widths.Length; r++)
            {
                int o = 8 + stride * r;
                hdmx[o] = m.Ppems[r];
                byte max = 0;
                foreach (byte w in widths[r]) if (w > max) max = w;
                hdmx[o + 1] = max;
                Array.Copy(widths[r], 0, hdmx, o + 2, numGlyphs);
            }
            tables["hdmx"] = hdmx;
            tables["VDMX"] = m.Vdmx;
            tables["name"] = Names(family, postScript);
            foreach (string gone in new[] { "kern", "GPOS", "DSIG", "FFTM", "LTSH" }) tables.Remove(gone);

            return Sfnt(tables);
        }

        private static byte[] Names(string family, string postScript)
        {
            var records = new (int Id, string Text)[] { (1, family), (2, "Regular"), (3, family + " (stand-in)"), (4, family), (6, postScript) };
            var storage = new MemoryStream();
            var table = new byte[6 + 12 * records.Length];
            Put16(table, 2, records.Length);
            Put16(table, 4, table.Length);
            for (int i = 0; i < records.Length; i++)
            {
                byte[] s = Encoding.BigEndianUnicode.GetBytes(records[i].Text);
                int o = 6 + 12 * i;
                Put16(table, o, 3); Put16(table, o + 2, 1); Put16(table, o + 4, 0x409);
                Put16(table, o + 6, records[i].Id); Put16(table, o + 8, s.Length); Put16(table, o + 10, (int)storage.Length);
                storage.Write(s);
            }
            var all = new byte[table.Length + storage.Length];
            table.CopyTo(all, 0);
            storage.ToArray().CopyTo(all, table.Length);
            return all;
        }

        private static uint Checksum(byte[] b, int off, int len)
        {
            uint sum = 0;
            for (int i = 0; i < len; i += 4)
            {
                uint v = 0;
                for (int k = 0; k < 4; k++) v = v << 8 | (uint)(i + k < len ? b[off + i + k] : 0);
                sum += v;
            }
            return sum;
        }

        private static byte[] Sfnt(Dictionary<string, byte[]> tables)
        {
            var tags = new List<string>(tables.Keys);
            tags.Sort(StringComparer.Ordinal);
            int n = tags.Count, header = 12 + 16 * n;
            int size = header;
            foreach (string t in tags) size += (tables[t].Length + 3) & ~3;
            var file = new byte[size];
            file[0] = 0; file[1] = 1;                         // TrueType outlines
            Put16(file, 4, n);
            int pow = 1, log = 0;
            while (pow * 2 <= n) { pow *= 2; log++; }
            Put16(file, 6, pow * 16); Put16(file, 8, log); Put16(file, 10, n * 16 - pow * 16);
            tables["head"][8] = tables["head"][9] = tables["head"][10] = tables["head"][11] = 0;
            int at = header, headAt = 0;
            for (int i = 0; i < n; i++)
            {
                byte[] t = tables[tags[i]];
                int e = 12 + 16 * i;
                Encoding.ASCII.GetBytes(tags[i]).CopyTo(file, e);
                uint sum = Checksum(t, 0, t.Length);
                file[e + 4] = (byte)(sum >> 24); file[e + 5] = (byte)(sum >> 16); file[e + 6] = (byte)(sum >> 8); file[e + 7] = (byte)sum;
                file[e + 8] = (byte)(at >> 24); file[e + 9] = (byte)(at >> 16); file[e + 10] = (byte)(at >> 8); file[e + 11] = (byte)at;
                file[e + 12] = (byte)(t.Length >> 24); file[e + 13] = (byte)(t.Length >> 16); file[e + 14] = (byte)(t.Length >> 8); file[e + 15] = (byte)t.Length;
                if (tags[i] == "head") headAt = at;
                t.CopyTo(file, at);
                at += (t.Length + 3) & ~3;
            }
            uint adjust = 0xB1B0AFBA - Checksum(file, 0, file.Length);
            file[headAt + 8] = (byte)(adjust >> 24); file[headAt + 9] = (byte)(adjust >> 16); file[headAt + 10] = (byte)(adjust >> 8); file[headAt + 11] = (byte)adjust;
            return file;
        }
    }
}
