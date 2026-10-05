// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// A font family as GDI+ answers for it, from the font files (no GDI+, no GDI): the managed font
// stack's scan (Composition.Text.FontFiles) says which families are installed and where their files
// are; the metrics are the face's own:
//
//   em height      head.unitsPerEm
//   cell ascent    OS/2.usWinAscent
//   cell descent   OS/2.usWinDescent
//   line spacing   max(winAscent + winDescent, hhea.ascender - hhea.descender + hhea.lineGap) --
//                  DirectWrite's GDI-compatible line gap, which GDI+ 1.1 reads its metrics through
//
// A style the family has no file for is synthesized from the regular face, as GDI+ emboldens or
// slants, so its metrics are the regular face's.
//

using System.Collections.Generic;
using System.Drawing.Drawing2D;
using System.IO;
using Microsoft.Wpf.Interop.WebGpu.Composition.Text;

namespace System.Drawing.WebGpuBackend.Gdip
{
    internal static class GpFontFamily
    {
        public const string SansSerif = "Microsoft Sans Serif", Serif = "Times New Roman", Monospace = "Courier New";

        public readonly struct Metrics
        {
            public readonly int Em, Ascent, Descent, LineSpacing;
            public Metrics (int em, int a, int d, int ls) { Em = em; Ascent = a; Descent = d; LineSpacing = ls; }
        }

        static readonly Dictionary<string, Metrics?> s_metrics = new Dictionary<string, Metrics?> (StringComparer.OrdinalIgnoreCase);

        /// <summary>The family's name as its font declares it, or null when no installed (or
        /// registered) font is called that.</summary>
        public static string Canonical (string name) => FontFiles.CanonicalFamily (name);

        /// <summary>Whether the managed font stack can draw <paramref name="name"/> at all (an installed
        /// family, or one it substitutes for).</summary>
        public static bool Resolvable (string name) => FontFiles.Find (name, false, false) != null;

        public static Metrics? Get (string family, FontStyle style)
        {
            bool bold = (style & FontStyle.Bold) != 0, italic = (style & FontStyle.Italic) != 0;
            string key = family + "|" + (bold ? "b" : "") + (italic ? "i" : "");
            lock (s_metrics) {
                if (s_metrics.TryGetValue (key, out Metrics? m)) return m;
                m = Read (family, bold, italic);
                s_metrics [key] = m;
                return m;
            }
        }

        static Metrics? Read (string family, bool bold, bool italic)
        {
            string path = FontFiles.Find (family, bold, italic);
            if (path == null) return null;
            byte[] data;
            try { data = File.ReadAllBytes (path); }
            catch (IOException) { return null; }
            catch (UnauthorizedAccessException) { return null; }
            int sfnt = FontFiles.SfntOffset (data, family, bold, italic);
            if (!Table (data, sfnt, "head", out int head) || !Table (data, sfnt, "hhea", out int hhea)) return null;
            int em = U16 (data, head + 18);
            int hAsc = S16 (data, hhea + 4), hDesc = S16 (data, hhea + 6), hGap = S16 (data, hhea + 8);
            int winAsc = hAsc, winDesc = -hDesc;
            if (Table (data, sfnt, "OS/2", out int os2)) {
                // USE_TYPO_METRICS (fsSelection bit 7): DirectWrite's metrics are the typographic
                // ones, line gap included (Gabriola, Bahnschrift: a cell of exactly one em).
                if ((U16 (data, os2 + 62) & 0x80) != 0) {
                    int tAsc = S16 (data, os2 + 68), tDesc = -S16 (data, os2 + 70), tGap = S16 (data, os2 + 72);
                    return new Metrics (em, tAsc, tDesc, tAsc + tDesc + Math.Max (0, tGap));
                }
                winAsc = U16 (data, os2 + 74);
                winDesc = U16 (data, os2 + 76);
            }
            // GpFontFamily::GetDesignLineSpacing (@180085c80) is DWRITE_FONT_METRICS ascent +
            // descent + lineGap, the gap DirectWrite's GDI-compatible one.
            int ls = Math.Max (winAsc + winDesc, hAsc - hDesc + hGap);
            return new Metrics (em, winAsc, winDesc, ls);
        }

        static bool Table (byte[] d, int sfnt, string tag, out int offset)
        {
            offset = 0;
            if (sfnt + 12 > d.Length) return false;
            int n = U16 (d, sfnt + 4);
            for (int i = 0; i < n; i++) {
                int r = sfnt + 12 + i * 16;
                if (r + 16 > d.Length) return false;
                if (d [r] == tag [0] && d [r + 1] == tag [1] && d [r + 2] == tag [2] && d [r + 3] == tag [3]) {
                    offset = (int) ((uint) (d [r + 8] << 24 | d [r + 9] << 16 | d [r + 10] << 8 | d [r + 11]));
                    return offset < d.Length;
                }
            }
            return false;
        }

        static int U16 (byte[] d, int o) => d [o] << 8 | d [o + 1];
        static int S16 (byte[] d, int o) => (short) (d [o] << 8 | d [o + 1]);

        /// <summary>The installed families, sorted as GDI+ lists them.</summary>
        public static string[] Installed ()
        {
            var names = new List<string> (FontFiles.FamilyNames ());
            names.Sort (StringComparer.OrdinalIgnoreCase);
            return names.ToArray ();
        }
    }
}
