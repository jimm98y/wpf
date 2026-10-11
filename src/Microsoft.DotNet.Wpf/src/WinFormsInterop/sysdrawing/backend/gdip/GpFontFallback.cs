// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// GDI+'s own font fallback for the full imager (gdiplus.dll 10.0.26100 arm64): the face a run of
// characters the item's face has no glyph for is drawn from. FullTextImager::CreateTextRuns
// @18003aaf8 shapes an item with its face, and where a glyph is the face's missing one (unless the
// format says NoFontFallback, or the face is a symbol face) cuts the run there and asks
// GpFamilyFallback for the characters from it on:
//
//   GpFamilyFallback::GetUniformFallbackFace @1800ecac0   the first character's fallback family and
//       the face of it to use (a style table over which styles the family has the character in,
//       @1802b2fd8), and how many following characters (hot-key markers skipped) take the same.
//   GpFamilyFallback::CacheCodepointFallback @1800ec258   a code point's family:
//         the item's family in another of its styles (bold or italic asked);
//         a supplementary-plane character: LanguagePack\SurrogateFallback's plane fonts;
//         a CJK script (ItemScript 0x21..0x28): the family's font links, else the family;
//         any other BMP character outside the private use area: the script's own face (the table
//           @1802b3130 into Microsoft Sans Serif, Arial, Segoe UI, Segoe UI Symbol, Segoe UI
//           Historic, Nirmala UI, Leelawadee UI, Gadugi, Ebrima, Mv Boli, Microsoft Himalaya,
//           Mongolian Baiti, Microsoft Yi Baiti, Myanmar Text @1802b3240; 0 is the generic sans
//           serif), then the family's font links, else the family;
//         the private use area: the EUDC font, then the font links.
//   GpFamilyFallback::FallbackUsingFontLinking @1800ec990   GpFontLink's list for the family
//       (FontLink\SystemLink: each "FILE,Family" line; GetFontLinkingDataFromRegistryW @1800a63b8
//       takes the text after the last comma, so a line with a scale "FILE,Family,128,96" names no
//       family), then the default list (GetDefaultFamily @1800a6078: MS Shell Dlg's substitute and
//       its links), the first that has the character.
//   FullTextImager::GetFallbackFontSize @1802410b8   the fallback run's em: the item's em times the
//       ratio of the faces' cell heights (win ascent + descent over the em), clamped to 1..1.1.
//

using System.Collections.Generic;
using System.Runtime.InteropServices;
using Microsoft.Wpf.Interop.WebGpu.Composition.Text;
using GdipText = Microsoft.Wpf.Interop.WebGpu.Composition.Text.GdiPlusText;

namespace System.Drawing.WebGpuBackend.Gdip
{
    internal sealed class GpFontFallback
    {
        readonly List<string> _families = new List<string> { null, null };   // index 1 is the item's family
        readonly Dictionary<int, byte> _map = new Dictionary<int, byte> ();

        static readonly Dictionary<string, GpFontFallback> s_cache = new Dictionary<string, GpFontFallback> (StringComparer.OrdinalIgnoreCase);

        GpFontFallback (string family) { _families [1] = family; }

        /// <summary>GpFontFamily::GetFamilyFallback: the family's fallback (one per family).</summary>
        public static GpFontFallback For (string family)
        {
            lock (s_cache) {
                if (!s_cache.TryGetValue (family, out GpFontFallback f)) s_cache [family] = f = new GpFontFallback (family);
                return f;
            }
        }

        // ItemScript -> the script's own fallback face (@1802b3130), names @1802b3240.
        static readonly byte[] s_scriptFamily = {
            0, 0, 0, 0, 0, 2, 0, 0, 4, 9, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 0, 6, 10, 13, 2, 8, 7, 7, 4, 4, 6, 11, 3, 0, 0, 0,
            0, 0, 0, 0, 0, 12, 0, 0, 1, 0, 0, 5, 5, 5, 5, 5, 5, 5, 5, 5, 10, 6, 6, 13, 11, 0, 0, 5, 0, 0, 0, 0,
        };
        static readonly string[] s_scriptFamilyNames = {
            "Microsoft Sans Serif", "Arial", "Segoe UI", "Segoe UI Symbol", "Segoe UI Historic", "Nirmala UI",
            "Leelawadee UI", "Gadugi", "Ebrima", "Mv Boli", "Microsoft Himalaya", "Mongolian Baiti",
            "Microsoft Yi Baiti", "Myanmar Text",
        };
        // [supported styles (bits 1..3: bold, italic, bold italic) << 2 | asked style] -> the face's style (@1802b2fd8).
        static readonly byte[] s_styleMap = { 0, 0, 0, 0, 0, 1, 0, 1, 0, 0, 2, 2, 0, 1, 2, 2, 0, 0, 0, 3, 0, 1, 0, 3, 0, 0, 2, 3, 0, 1, 2, 3 };

        /// <summary>The fallback for text[start, start + count): the family (null: the item's own), the
        /// style of its face and how many characters take it.</summary>
        public int GetUniformFallbackFace (string text, int start, int count, int style, int script, out string family, out int faceStyle)
        {
            family = null; faceStyle = 0;
            int i = start, end = start + count;
            while (i < end && text [i] == '￿') i++;
            if (i >= end) return count;
            int cp = CodePoint (text, i, end, out int w);
            byte d = Data (cp, style, script, 0);
            string f0 = _families [d & 0x1f];
            byte s0 = s_styleMap [(d >> 5) << 2 | (style & 3)];
            if (FaceExists (f0, s0)) { family = f0; faceStyle = s0; }
            i += w;
            while (i < end) {
                if (text [i] == '￿') { i++; continue; }
                cp = CodePoint (text, i, end, out w);
                byte dd = Data (cp, style, script, 1);
                if (!ReferenceEquals (_families [dd & 0x1f], f0) || s_styleMap [(dd >> 5) << 2 | (style & 3)] != s0) break;
                i += w;
            }
            return i - start;
        }

        static int CodePoint (string text, int i, int end, out int width)
        {
            width = 1;
            if (char.IsHighSurrogate (text [i]) && i + 1 < end && char.IsLowSurrogate (text [i + 1])) { width = 2; return char.ConvertToUtf32 (text [i], text [i + 1]); }
            return text [i];
        }

        byte Data (int cp, int style, int script, int direct)
        {
            lock (_map) {
                if (_map.TryGetValue (cp, out byte d) && (d & 0x1f) != 0) return d;
                d = Cache (cp, style, script, direct);
                _map [cp] = d;
                return d;
            }
        }

        /// <summary>CacheCodepointFallback + SetFallbackFamilyData.</summary>
        byte Cache (int cp, int style, int script, int direct)
        {
            string baseFamily = _families [1];
            if (script == 0x2b && cp < 0x10000) script = GpTextTables.Script (cp);
            string f = null;
            if ((style & 3) != 0 && SupportsCodepoint (baseFamily, cp, style, direct != 0)) f = baseFamily;
            else if (script == 0x2b) f = SurrogateFont (cp, style) ?? baseFamily;
            else if ((uint) (script - 0x21) < 8) {
                f = FontLinking (baseFamily, cp, style);
                if (f == null) f = (uint) (cp - 0xe7c7) > 0x9d ? baseFamily : Eudc (baseFamily, cp, style);
            } else if ((cp < 0xe000 || cp > 0xf8ff) && (uint) (cp - 0xf0000) > 0x1ffff) {
                int idx = script >= 0 && script < s_scriptFamily.Length ? s_scriptFamily [script] : 0;
                string sf = FontFiles.CanonicalFamily (idx == 0 ? GpFontFamily.SansSerif : s_scriptFamilyNames [idx]);
                if (sf != null && SupportsCodepoint (sf, cp, style, true)) f = sf;
                else f = FontLinking (baseFamily, cp, style) ?? baseFamily;
            } else f = Eudc (baseFamily, cp, style);
            return FamilyData (f ?? baseFamily, cp);
        }

        string Eudc (string baseFamily, int cp, int style)
        {
            // FallbackByEUDCfont: the per-family and system EUDC fonts are not part of the port's
            // font list; then the font links (outside the range EUDC owns).
            if ((uint) (cp - 0xe7c7) <= 0x9d) return baseFamily;
            return FontLinking (baseFamily, cp, style) ?? baseFamily;
        }

        byte FamilyData (string family, int cp)
        {
            int idx = -1;
            for (int k = 1; k < _families.Count; k++)
                if (string.Equals (_families [k], family, StringComparison.OrdinalIgnoreCase)) { idx = k; break; }
            if (idx < 0) {
                if (_families.Count < 32) { _families.Add (family); idx = _families.Count - 1; }
                else { idx = 1; family = _families [1]; }
            }
            int bits = (DirectlySupports (family, cp, 1) ? 0x20 : 0) | (DirectlySupports (family, cp, 2) ? 0x40 : 0) | (DirectlySupports (family, cp, 3) ? 0x80 : 0);
            return (byte) (bits | (idx & 0x1f));
        }

        // ---- the family queries ------------------------------------------------------------------------

        /// <summary>GpFontFamily::GetFaceAbsolute: the family has a face of exactly this style.</summary>
        static bool FaceExists (string family, int style)
        {
            if (string.IsNullOrEmpty (family)) return false;
            if ((style & 3) == 0) return FontFiles.Find (family, false, false) != null;
            return FontFiles.HasStyledFile (family, (style & 1) != 0, (style & 2) != 0);
        }

        /// <summary>The face of exactly this style, unsimulated.</summary>
        static TrueTypeFont ExactFace (string family, int style)
        {
            if (!FaceExists (family, style)) return null;
            return GdipText.Face (family, style & 3);
        }

        static bool DirectlySupports (string family, int cp, int style)
        {
            TrueTypeFont f = ExactFace (family, style);
            return f != null && f.GlyphIndexOf (cp) != 0;
        }

        static bool SupportsCodepoint (string family, int cp, int style, bool direct)
        {
            if (direct && DirectlySupports (family, cp, style)) return true;
            switch (style & 3) {
            case 1:
            case 2: return DirectlySupports (family, cp, 0);
            case 3: return DirectlySupports (family, cp, 1) || DirectlySupports (family, cp, 2) || DirectlySupports (family, cp, 0);
            default: return false;
            }
        }

        /// <summary>IsGlyphableCodepoint: the style's face (or the nearest one) has a glyph with an advance.</summary>
        static bool Glyphable (string family, int cp, int style)
        {
            TrueTypeFont f = ExactFace (family, style);
            if (f == null) {
                int s = style & 3;
                if (s == 3) f = ExactFace (family, 1) ?? ExactFace (family, 2);
                if (f == null && s != 0) f = ExactFace (family, 0);
            }
            if (f == null) return false;
            int g = f.GlyphIndexOf (cp);
            return g != 0 && f.DesignAdvance (g) != 0;
        }

        static string FontLinking (string baseFamily, int cp, int style)
        {
            foreach (string l in Links (baseFamily))
                if (SupportsCodepoint (l, cp, style, true)) return l;
            foreach (string l in DefaultFamilies ())
                if (SupportsCodepoint (l, cp, style, true)) return l;
            return null;
        }

        static string SurrogateFont (int cp, int style)
        {
            string[] planes = SurrogatePlanes ();
            for (int plane = (cp >> 16) & 0x1f; plane >= 1 && plane <= 16; plane++) {
                string f = planes [plane - 1];
                if (f != null && Glyphable (f, cp, style)) return f;
            }
            return null;
        }

        // ---- GpFontLink (the registry's link table) ----------------------------------------------------

        static Dictionary<string, List<string>> s_links;
        static List<string> s_defaults;
        static string[] s_planes;

        static List<string> Links (string family)
        {
            lock (s_cache) {
                if (s_links == null) {
                    s_links = new Dictionary<string, List<string>> (StringComparer.OrdinalIgnoreCase);
                    foreach ((string name, string[] lines) in MultiStrings (@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\FontLink\SystemLink")) {
                        string key = FontFiles.CanonicalFamily (name);
                        if (key == null) continue;
                        var list = new List<string> ();
                        foreach (string line in lines) {
                            int comma = line.LastIndexOf (',');
                            if (comma < 0) continue;
                            string f = FontFiles.CanonicalFamily (line.Substring (comma + 1));
                            if (f != null) list.Add (f);
                        }
                        s_links [key] = list;
                    }
                }
                return s_links.TryGetValue (family ?? "", out List<string> l) ? l : new List<string> ();
            }
        }

        static List<string> DefaultFamilies ()
        {
            lock (s_cache) {
                if (s_defaults != null) return s_defaults;
                s_defaults = new List<string> ();
                string shell = FontFiles.CanonicalFamily (Substitute ("MS Shell Dlg") ?? "");
                if (shell != null) {
                    s_defaults.Add (shell);
                    s_defaults.AddRange (Links (shell));
                }
                return s_defaults;
            }
        }

        static string[] SurrogatePlanes ()
        {
            lock (s_cache) {
                if (s_planes != null) return s_planes;
                s_planes = new string [16];
                foreach ((string name, string value) in RegStrings (@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\LanguagePack\SurrogateFallback")) {
                    if (name.Length < 6 || name.Length > 7 || !name.StartsWith ("plane", StringComparison.OrdinalIgnoreCase)) continue;
                    if (!int.TryParse (name.Substring (5), out int plane) || plane < 1 || plane > 16) continue;
                    s_planes [plane - 1] = FontFiles.CanonicalFamily (value);
                }
                return s_planes;
            }
        }

        static string Substitute (string name)
        {
            foreach ((string n, string v) in RegStrings (@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\FontSubstitutes"))
                if (string.Equals (n, name, StringComparison.OrdinalIgnoreCase)) {
                    int c = v.IndexOf (',');
                    return c >= 0 ? v.Substring (0, c) : v;
                }
            return string.Equals (name, "MS Shell Dlg", StringComparison.OrdinalIgnoreCase) ? GpFontFamily.SansSerif : null;
        }

        // ---- registry (Windows only) -----------------------------------------------------------------

        static IEnumerable<(string, string[])> MultiStrings (string subKey)
        {
            foreach ((string n, int type, byte[] data) in RegValues (subKey))
                if (type == 7) yield return (n, System.Text.Encoding.Unicode.GetString (data).Split ('\0', StringSplitOptions.RemoveEmptyEntries));
        }

        static IEnumerable<(string, string)> RegStrings (string subKey)
        {
            foreach ((string n, int type, byte[] data) in RegValues (subKey))
                if (type == 1 || type == 2) yield return (n, System.Text.Encoding.Unicode.GetString (data).TrimEnd ('\0'));
        }

        static List<(string, int, byte[])> RegValues (string subKey)
        {
            var list = new List<(string, int, byte[])> ();
            if (!OperatingSystem.IsWindows ()) return list;
            if (RegOpenKeyExW ((IntPtr) unchecked((int) 0x80000002), subKey, 0, 0x20019, out IntPtr key) != 0) return list;
            try {
                var name = new char [512];
                for (int i = 0; ; i++) {
                    int nameLen = name.Length, dataLen = 0;
                    if (RegEnumValueW (key, i, name, ref nameLen, IntPtr.Zero, out int type, null, ref dataLen) != 0) break;
                    var data = new byte [Math.Max (2, dataLen)];
                    nameLen = name.Length;
                    if (RegEnumValueW (key, i, name, ref nameLen, IntPtr.Zero, out type, data, ref dataLen) != 0) break;
                    Array.Resize (ref data, dataLen);
                    list.Add ((new string (name, 0, nameLen), type, data));
                }
            } finally { RegCloseKey (key); }
            return list;
        }

        [DllImport ("advapi32.dll", CharSet = CharSet.Unicode)]
        static extern int RegOpenKeyExW (IntPtr key, string subKey, int options, int desired, out IntPtr result);
        [DllImport ("advapi32.dll", CharSet = CharSet.Unicode)]
        static extern int RegEnumValueW (IntPtr key, int index, char[] name, ref int nameLen, IntPtr reserved, out int type, byte[] data, ref int dataLen);
        [DllImport ("advapi32.dll")]
        static extern int RegCloseKey (IntPtr key);

        static readonly System.Runtime.CompilerServices.ConditionalWeakTable<TrueTypeFont, object> s_symbol = new ();

        /// <summary>GpFontFace +0x6c (IDWriteFontFace::IsSymbolFont): the face's cmap is the symbol
        /// (3,0) one. A symbol face takes no fallback.</summary>
        public static bool IsSymbolFace (TrueTypeFont face)
        {
            if (face == null) return false;
            object v = s_symbol.GetValue (face, f => {
                byte[] d = f.FontData;
                int b = f.SfntOffset;
                if (d == null || b + 12 > d.Length) return false;
                int n = (d [b + 4] << 8) | d [b + 5];
                for (int i = 0; i < n; i++) {
                    int e = b + 12 + i * 16;
                    if (e + 16 > d.Length) break;
                    if (d [e] != 'c' || d [e + 1] != 'm' || d [e + 2] != 'a' || d [e + 3] != 'p') continue;
                    int off = (d [e + 8] << 24) | (d [e + 9] << 16) | (d [e + 10] << 8) | d [e + 11];
                    if (off + 4 > d.Length) break;
                    int nt = (d [off + 2] << 8) | d [off + 3];
                    for (int k = 0; k < nt && off + 4 + k * 8 + 4 <= d.Length; k++)
                        if (((d [off + 4 + k * 8] << 8) | d [off + 5 + k * 8]) == 3 && ((d [off + 6 + k * 8] << 8) | d [off + 7 + k * 8]) == 0) return true;
                }
                return false;
            });
            return (bool) v;
        }

        /// <summary>FullTextImager::GetFallbackFontSize: the fallback face's em.</summary>
        public static float FallbackEm (TrueTypeFont itemFace, float em, TrueTypeFont face)
        {
            if (ReferenceEquals (itemFace, face) || face == null) return em;
            float fa = (float) face.WinAscent / face.UnitsPerEmForHinting, fd = (float) face.WinDescent / face.UnitsPerEmForHinting;
            if (!(fa > 0f) || !(fd > 0f)) return em;
            float ratio = ((float) itemFace.WinDescent / itemFace.UnitsPerEmForHinting + (float) itemFace.WinAscent / itemFace.UnitsPerEmForHinting) / (fd + fa);
            double s = ratio > 1f ? ratio : 1.0;
            if (s < 1.1) s = ratio <= 1f ? 1.0 : ratio;
            else s = 1.1;
            return (float) s * em;
        }
    }
}
