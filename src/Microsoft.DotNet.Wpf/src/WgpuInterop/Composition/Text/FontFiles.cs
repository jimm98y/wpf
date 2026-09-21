// Which file holds a font family.
//
// Everything drawn on this stack used to come out in one hard-coded face -- Arial, whatever the
// caller asked for -- because a text run reached the renderer carrying nothing but a size, a colour
// and a bold/italic flag. So a WinForms application asking for Segoe UI got Arial, and one asking
// for a fixed-width face (the assembly list in SharpDevelop's About box, a code view, anything
// aligned in columns) got a proportional one and lined up with nothing.
//
// Resolving a family name to a file is the part the platform normally does. Windows keeps the
// mapping in the registry, but the names there are the FULL names ("Segoe UI Bold"), not families,
// and the file names are the old eight-character ones that cannot be guessed from the family:
// Courier New lives in cour.ttf. So: a table for the faces that actually turn up, a guess from the
// family name for the ones that do not, and the caller's own fallback when neither finds anything.

using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;

namespace Microsoft.Wpf.Interop.WebGpu.Composition.Text
{
    /// <summary>Finds the file backing a font family in a given style.</summary>
    public static class FontFiles
    {
        // family (lower case) -> { regular, bold, italic, bold-italic }, as they are named in the
        // system font directory. A null slot means "no such file; synthesize it from the regular".
        private static readonly Dictionary<string, string?[]> s_known =
            new Dictionary<string, string?[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["Segoe UI"] = new[] { "segoeui.ttf", "segoeuib.ttf", "segoeuii.ttf", "segoeuiz.ttf" },
            ["Segoe UI Semibold"] = new[] { "seguisb.ttf", "seguisb.ttf", "seguisbi.ttf", "seguisbi.ttf" },
            ["Tahoma"] = new[] { "tahoma.ttf", "tahomabd.ttf", null, null },
            ["Verdana"] = new[] { "verdana.ttf", "verdanab.ttf", "verdanai.ttf", "verdanaz.ttf" },
            ["Arial"] = new[] { "arial.ttf", "arialbd.ttf", "ariali.ttf", "arialbi.ttf" },
            ["Calibri"] = new[] { "calibri.ttf", "calibrib.ttf", "calibrii.ttf", "calibriz.ttf" },
            ["Cambria"] = new[] { "cambria.ttc", "cambriab.ttf", "cambriai.ttf", "cambriaz.ttf" },
            ["Georgia"] = new[] { "georgia.ttf", "georgiab.ttf", "georgiai.ttf", "georgiaz.ttf" },
            ["Times New Roman"] = new[] { "times.ttf", "timesbd.ttf", "timesi.ttf", "timesbi.ttf" },
            ["Microsoft Sans Serif"] = new[] { "micross.ttf", null, null, null },
            ["MS Sans Serif"] = new[] { "micross.ttf", null, null, null },
            ["MS Shell Dlg"] = new[] { "micross.ttf", null, null, null },
            ["MS Shell Dlg 2"] = new[] { "tahoma.ttf", "tahomabd.ttf", null, null },
            ["Segoe UI Symbol"] = new[] { "seguisym.ttf", null, null, null },

            // The fixed-width ones, which is the whole reason any of this exists.
            ["Consolas"] = new[] { "consola.ttf", "consolab.ttf", "consolai.ttf", "consolaz.ttf" },
            ["Courier New"] = new[] { "cour.ttf", "courbd.ttf", "couri.ttf", "courbi.ttf" },
            ["Lucida Console"] = new[] { "lucon.ttf", null, null, null },
            ["Cascadia Code"] = new[] { "CascadiaCode.ttf", null, null, null },
            ["Cascadia Mono"] = new[] { "CascadiaMono.ttf", null, null, null },
        };

        // Where the OS keeps its fonts. The per-user directory matters too: fonts installed without
        // administrator rights land there and nowhere else.
        private static readonly Lazy<string[]> s_directories = new Lazy<string[]>(() =>
        {
            var dirs = new List<string>();
            if (OperatingSystem.IsWindows())
            {
                string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
                if (!string.IsNullOrEmpty(windows)) dirs.Add(Path.Combine(windows, "Fonts"));
                string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                if (!string.IsNullOrEmpty(local)) dirs.Add(Path.Combine(local, "Microsoft", "Windows", "Fonts"));
            }
            else if (OperatingSystem.IsMacOS())
            {
                dirs.Add("/System/Library/Fonts");
                dirs.Add("/System/Library/Fonts/Supplemental");
                dirs.Add("/Library/Fonts");
            }
            else
            {
                dirs.Add("/usr/share/fonts");
                dirs.Add("/usr/local/share/fonts");
                dirs.Add("/fonts");                       // the browser's virtual file system
            }
            return dirs.ToArray();
        });

        private static readonly Dictionary<string, string?> s_resolved = new Dictionary<string, string?>();

        /// <summary>The file holding <paramref name="family"/> in the requested style, or null when
        /// this machine has nothing by that name. A style with no file of its own resolves to the
        /// regular one, which the font stack then synthesizes -- that is what the bold and oblique
        /// flags on TrueTypeFont are for.</summary>
        /// <summary>Where a face's sfnt header starts in the file: 0 for an ordinary font, and
        /// the first face's offset for a COLLECTION.
        /// <para>A .ttc begins with a 'ttcf' header and a table of offsets, one per face, so
        /// there is no sfnt at byte zero and everything that reads a table directory from
        /// there fails. Cambria is the case in point: FontFiles resolves it to cambria.ttc,
        /// the renderer opened it at zero, TrueTypeFont threw "missing 'head' table", the
        /// catch swallowed it and the family fell back to the default face -- so Cambria text
        /// drew in the wrong typeface entirely, silently.</para>
        /// <para>The first face is the right one for a family lookup: a collection groups
        /// related faces and the one the family names comes first.</para></summary>
        public static int SfntOffset(byte[] data)
        {
            if (data.Length < 16 || data[0] != (byte) 't' || data[1] != (byte) 't'
                || data[2] != (byte) 'c' || data[3] != (byte) 'f')
                return 0;
            int count = (data[8] << 24) | (data[9] << 16) | (data[10] << 8) | data[11];
            if (count <= 0 || data.Length < 16) return 0;
            int off = (data[12] << 24) | (data[13] << 16) | (data[14] << 8) | data[15];
            return off > 0 && off < data.Length ? off : 0;
        }

        /// <summary>Where the requested FAMILY's face starts in a collection.
        /// <para>The overload above returns the first face in the file, which is right for a plain
        /// font and wrong for every family that is not first in its collection. msgothic.ttc holds
        /// MS Gothic, MS UI Gothic and MS PGothic; asking for MS UI Gothic got MS Gothic, and since
        /// the two share their kanji and differ in their kana, CJK measured EXACT for Chinese and
        /// 3% out for hiragana -- a discrepancy that looked like a bitmap decoding bug and was a
        /// font identity bug. PMingLiU, MS PGothic and their kin are all in the same position.</para>
        /// <para>The faces are asked what they are called rather than assumed to be in any order,
        /// and only for a collection, so a plain font pays nothing.</para></summary>
        public static int SfntOffset(byte[] data, string? family, bool bold = false, bool italic = false)
        {
            if (string.IsNullOrEmpty(family)) return SfntOffset(data);
            int fallback = -1;
            foreach (int sfnt in FaceOffsets(data))
            {
                if (!ReadNames(data, sfnt, out string? declared, out bool isBold, out bool isItalic))
                    continue;
                if (!string.Equals(declared, family, StringComparison.OrdinalIgnoreCase)) continue;
                if (isBold == bold && isItalic == italic) return sfnt;
                if (fallback < 0) fallback = sfnt;      // the family, in another style
            }
            return fallback >= 0 ? fallback : SfntOffset(data);
        }

        public static string? Find(string? family, bool bold, bool italic)
        {
            if (string.IsNullOrWhiteSpace(family)) return null;

            string key = family + "|" + (bold ? "b" : "") + (italic ? "i" : "");
            lock (s_resolved)
            {
                if (s_resolved.TryGetValue(key, out string? cached)) return cached;
                string? found = Resolve(family!, bold, italic);
                s_resolved[key] = found;
                return found;
            }
        }

        /// <summary>What styles the FILE itself declares, read from 'head'.macStyle.
        /// <para>Asking whether a family 'has a styled file' cannot answer a two-dimensional
        /// question with one boolean, and answering it that way lost the oblique on every
        /// bold-italic of a family that ships bold but not bold-italic: Tahoma resolved Bold+Italic
        /// to tahomabd.ttf, which is not the regular file, so 'styled' came back true and NEITHER
        /// simulation was applied -- we drew upright bold where Windows draws a sheared bold, and
        /// that one row was a third of the whole specimen's error at 12ppem.</para>
        /// <para>The file's own macStyle settles it per axis and needs no guessing about which
        /// path came back: bit 0 is bold, bit 1 is italic.</para></summary>
        public static void DeclaredStyle(byte[] data, int sfntOffset, out bool bold, out bool italic)
        {
            bold = italic = false;
            if (data.Length < sfntOffset + 12) return;
            int numTables = (data[sfntOffset + 4] << 8) | data[sfntOffset + 5];
            for (int i = 0; i < numTables; i++)
            {
                int rec = sfntOffset + 12 + i * 16;
                if (rec + 16 > data.Length) return;
                if (data[rec] != (byte) 'h' || data[rec + 1] != (byte) 'e'
                    || data[rec + 2] != (byte) 'a' || data[rec + 3] != (byte) 'd') continue;
                int off = (data[rec + 8] << 24) | (data[rec + 9] << 16)
                        | (data[rec + 10] << 8) | data[rec + 11];
                if (off + 46 > data.Length) return;
                int macStyle = (data[off + 44] << 8) | data[off + 45];
                bold = (macStyle & 1) != 0;
                italic = (macStyle & 2) != 0;
                return;
            }
        }

        /// <summary>Whether the requested style has a file of its own. When it does not, the caller
        /// has to synthesize it -- drawing the regular face bold rather than pretending it is bold
        /// already, which would lay the text out at the wrong widths.</summary>
        public static bool HasStyledFile(string? family, bool bold, bool italic)
        {
            if (string.IsNullOrWhiteSpace(family) || (!bold && !italic)) return false;
            return Find(family, bold, italic) != Find(family, false, false);
        }

        private static string? Resolve(string family, bool bold, bool italic)
        {
            int slot = (bold ? 1 : 0) | (italic ? 2 : 0);

            if (s_known.TryGetValue(family, out string?[]? files))
            {
                // Fall back through the styles the family actually ships: bold-italic to bold, to
                // italic, to regular.
                foreach (int candidate in Order(slot))
                {
                    string? name = files[candidate];
                    if (name == null) continue;
                    string? path = Locate(name);
                    if (path != null) return path;
                }
                return null;
            }

            // Not in the table. The modern naming convention is the family with its spaces removed
            // and a style letter appended, which is right often enough to be worth trying.
            string stem = family.Replace(" ", string.Empty).ToLowerInvariant();
            string suffix = slot == 1 ? "b" : slot == 2 ? "i" : slot == 3 ? "z" : string.Empty;
            foreach (string ext in new[] { ".ttf", ".otf", ".ttc" })
            {
                string? path = Locate(stem + suffix + ext);
                if (path != null) return path;
            }

            // The guess missed, so ask the files what they are called -- BEFORE settling for the
            // regular file. Falling back to "stem.ttf" first resolved Ebrima Bold to ebrima.ttf
            // (the bold is ebrimabd.ttf) and Gadugi Bold Italic to gadugi.ttf rather than
            // gadugib.ttf, so we synthesized a weight GDI draws from a real bold face: 18M of the
            // other-faces battery.
            if (ScannedFamilies().TryGetValue(family, out string?[]? declared))
            {
                foreach (int candidate in Order(slot))
                    if (declared[candidate] != null) return declared[candidate];
                // AND THEN ANY FACE THE FAMILY HAS. Order() walks towards the regular, which
                // is no help to a family that HAS no regular: Brush Script MT, Vivaldi,
                // Lucida Calligraphy and Harlow Solid ship an italic and nothing else, and
                // Magneto and Berlin Sans FB Demi ship a bold. Asking those for their regular
                // returned null and the renderer drew the whole family in the fallback face.
                // A face of the family in the wrong style is far closer to right than a
                // different typeface, and it is what the name says the user asked for.
                for (int i = 0; i < declared.Length; i++)
                    if (declared[i] != null) return declared[i];
            }
            if (suffix.Length > 0)
                foreach (string ext in new[] { ".ttf", ".otf", ".ttc" })
                    if (Locate(stem + ext) is { } regular) return regular;
            return null;
        }

        /// <summary>Every installed family, found by asking the FILES what they are called.
        /// <para>The table above plus a naming guess resolves the families this port was tested
        /// with and silently fails for the rest, because the guess is "the family with its spaces
        /// removed": Trebuchet MS lives in trebuc.ttf and Book Antiqua in BKANT.TTF, so neither is
        /// found. The renderer's caller then falls back to the default face, so the text draws in
        /// the wrong typeface with nothing to say so -- the same failure Cambria had for a
        /// different reason.</para>
        /// <para>So when the guess misses, read the 'name' table of every font in the directories
        /// and build the map from what the faces actually declare. Done once, lazily, and only on a
        /// miss, so the families in the table above never pay for it.</para></summary>
        /// <summary>Families to try, in order, for a character the requested face lacks.
        /// <para>The named ones first because they are what Windows itself links to and they cover
        /// the scripts a UI actually meets; then everything installed, so a character none of them
        /// has is still found if anything on the machine has it.</para>
        /// <para>This is not GDI's own list -- that lives in the registry under FontLink\SystemLink
        /// and is the thing to read if these ever disagree with Windows about WHICH face a
        /// character comes from. Getting the script right is the first order of business; getting
        /// the same face as GDI is the second.</para></summary>
        /// <summary>GDI's OWN font-link list, read from the registry.
        /// <para>Which face a character comes from when the requested one lacks it is not a matter
        /// of taste: Windows keeps an ordered list per family under FontLink\SystemLink, and GDI
        /// walks it. Guessing a good order instead got the script right and the FACE wrong -- our
        /// Chinese was drawn in Microsoft YaHei against GDI's choice, 12% too much ink, which is a
        /// visibly different typeface and not a rendering difference at all.</para>
        /// <para>Each line is "FILE.TTC,Face Name", sometimes just a file. The face name is what
        /// this returns, because that is what the rest of this class resolves.</para></summary>
        public static IEnumerable<string> SystemLink(string family)
        {
            if (!OperatingSystem.IsWindows()) return System.Array.Empty<string>();
            if (s_systemLink is null)
                s_systemLink = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
            if (s_systemLink.TryGetValue(family, out string[]? cached)) return cached;

            var faces = new List<string>();
            foreach (string line in MultiString(
                         @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\FontLink\SystemLink", family))
            {
                // "MSGOTHIC.TTC,MS UI Gothic" -- the second field names the FACE inside a
                // collection, and a line without one names a file whose family we already know.
                // There may be a THIRD and fourth field: "MEIRYO.TTC,Meiryo UI,128,96" scales the
                // linked face to 128/96 of the asked-for size. Taking everything after the first
                // comma made the face name "Meiryo UI,128,96", which resolves to nothing.
                string[] fields = line.Split(',');
                string face = fields.Length > 1 ? fields[1]
                                                : Path.GetFileNameWithoutExtension(fields[0]);
                if (!string.IsNullOrWhiteSpace(face)) faces.Add(face.Trim());
            }
            return s_systemLink[family] = faces.ToArray();
        }

        private static Dictionary<string, string[]>? s_systemLink;

        /// <summary>One REG_MULTI_SZ value, or nothing at all if it is absent.</summary>
        private static string[] MultiString(string subKey, string valueName)
        {
            if (!OperatingSystem.IsWindows()) return System.Array.Empty<string>();
            const int HkeyLocalMachine = unchecked((int) 0x80000002);
            const int KeyRead = 0x20019, ErrorSuccess = 0;

            if (RegOpenKeyExW((IntPtr) HkeyLocalMachine, subKey, 0, KeyRead, out IntPtr key) != ErrorSuccess)
                return System.Array.Empty<string>();
            try
            {
                int size = 0;
                if (RegQueryValueExW(key, valueName, IntPtr.Zero, out int _, null, ref size) != ErrorSuccess
                    || size <= 0)
                    return System.Array.Empty<string>();
                var buffer = new byte[size];
                if (RegQueryValueExW(key, valueName, IntPtr.Zero, out int _, buffer, ref size) != ErrorSuccess)
                    return System.Array.Empty<string>();

                // REG_MULTI_SZ: UTF-16 strings, each null-terminated, the lot ending in a second null.
                string all = System.Text.Encoding.Unicode.GetString(buffer, 0, size);
                return all.Split('\0', StringSplitOptions.RemoveEmptyEntries);
            }
            finally { RegCloseKey(key); }
        }

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
        private static extern int RegOpenKeyExW(IntPtr key, string subKey, int options, int desired,
                                                out IntPtr result);

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
        private static extern int RegQueryValueExW(IntPtr key, string valueName, IntPtr reserved,
                                                   out int type, byte[]? data, ref int size);

        [DllImport("advapi32.dll")]
        private static extern int RegCloseKey(IntPtr key);

        /// <summary>An EMOJI face comes before the link list for an emoji codepoint.
        /// <para>Segoe UI Emoji is not in SystemLink at all, and yet GDI draws the snowman from it:
        /// asked which installed face reproduces GDI's U+2603, only Segoe UI Emoji does, at 68,659
        /// of ink against GDI's 69,233 where every other candidate is a third of that. So the link
        /// list is not the whole rule -- an emoji codepoint goes to the emoji face first, and only
        /// then to whatever else happens to have a picture of a snowman.</para></summary>
        private static bool IsEmoji(char c)
            => c is >= '☀' and <= '➿'          // symbols and dingbats
               || c is >= '️' and <= '️'       // the variation selector itself
               || c is >= '⬀' and <= '⯿';      // arrows and shapes drawn as emoji

        public static IEnumerable<string> LinkCandidates(string? requested = null, char needed = ' ')
        {
            if (needed != ' ' && IsEmoji(needed))
            {
                yield return "Segoe UI Emoji";
                yield return "Segoe UI Symbol";
            }

            // What Windows itself would do, first.
            if (requested is not null)
                foreach (string linked in SystemLink(requested)) yield return linked;
            foreach (string fallback in SystemLink("Segoe UI")) yield return fallback;

            foreach (string named in new[]
            {
                "Segoe UI", "Segoe UI Symbol", "Segoe UI Emoji", "Microsoft YaHei", "SimSun",
                "Microsoft JhengHei", "Yu Gothic UI", "MS Gothic", "Malgun Gothic",
                "Nirmala UI", "Segoe UI Historic", "Arial Unicode MS", "Microsoft Sans Serif",
            })
                yield return named;
            foreach (string scanned in ScannedFamilies().Keys) yield return scanned;
        }

        private static Dictionary<string, string?[]>? s_scanned;

        /// <summary>What the scan found, for a diagnostic that can say whether it ran at all.
        /// </summary>
        internal static IReadOnlyCollection<string> ScannedFamilyNames() => ScannedFamilies().Keys;

        private static Dictionary<string, string?[]> ScannedFamilies()
        {
            if (s_scanned is not null) return s_scanned;
            var found = new Dictionary<string, string?[]>(StringComparer.OrdinalIgnoreCase);
            foreach (string dir in s_directories.Value)
            {
                string[] files;
                try { files = Directory.GetFiles(dir); }
                catch (IOException) { continue; }
                catch (UnauthorizedAccessException) { continue; }
                foreach (string path in files)
                {
                    string ext = Path.GetExtension(path);
                    if (!ext.Equals(".ttf", StringComparison.OrdinalIgnoreCase)
                        && !ext.Equals(".otf", StringComparison.OrdinalIgnoreCase)
                        && !ext.Equals(".ttc", StringComparison.OrdinalIgnoreCase))
                        continue;
                    byte[] head;
                    try
                    {
                        // The name table can sit anywhere in the file, so the whole thing has to be
                        // read. These are a few hundred kilobytes each and this runs once.
                        head = File.ReadAllBytes(path);
                    }
                    catch (IOException) { continue; }
                    catch (UnauthorizedAccessException) { continue; }

                    foreach (int sfnt in FaceOffsets(head))
                    {
                        if (!ReadNames(head, sfnt, out string? family, out bool bold, out bool italic))
                            continue;
                        int slot = (bold ? 1 : 0) | (italic ? 2 : 0);
                        if (!found.TryGetValue(family!, out string?[]? slots))
                            found[family!] = slots = new string?[4];
                        slots[slot] ??= path;
                    }
                }
            }
            return s_scanned = found;
        }

        /// <summary>Where each face's sfnt header starts: one entry for a font, several for a
        /// collection.</summary>
        private static IEnumerable<int> FaceOffsets(byte[] data)
        {
            if (data.Length >= 12 && data[0] == (byte) 't' && data[1] == (byte) 't'
                && data[2] == (byte) 'c' && data[3] == (byte) 'f')
            {
                int count = Be32(data, 8);
                for (int i = 0; i < count && 12 + i * 4 + 4 <= data.Length; i++)
                {
                    int off = Be32(data, 12 + i * 4);
                    if (off > 0 && off < data.Length) yield return off;
                }
                yield break;
            }
            yield return 0;
        }

        /// <summary>The family this face declares, and whether it calls itself bold or italic.
        /// <para>Name id 1 is the family and id 2 the subfamily, and the subfamily is the styles in
        /// words -- so it is read rather than guessed from the filename, which is the mistake that
        /// made this necessary.</para></summary>
        private static bool ReadNames(byte[] d, int sfnt, out string? family, out bool bold, out bool italic)
        {
            family = null; bold = italic = false;
            if (sfnt + 12 > d.Length) return false;
            int numTables = Be16(d, sfnt + 4);
            int nameOff = -1;
            for (int i = 0; i < numTables; i++)
            {
                int rec = sfnt + 12 + i * 16;
                if (rec + 16 > d.Length) return false;
                if (d[rec] == 'n' && d[rec + 1] == 'a' && d[rec + 2] == 'm' && d[rec + 3] == 'e')
                { nameOff = Be32(d, rec + 8); break; }
            }
            if (nameOff < 0 || nameOff + 6 > d.Length) return false;
            int count = Be16(d, nameOff + 2), strings = nameOff + Be16(d, nameOff + 4);
            string? subfamily = null;
            for (int i = 0; i < count; i++)
            {
                int rec = nameOff + 6 + i * 12;
                if (rec + 12 > d.Length) break;
                int platform = Be16(d, rec), nameId = Be16(d, rec + 6);
                int len = Be16(d, rec + 8), off = strings + Be16(d, rec + 10);
                if (nameId != 1 && nameId != 2) continue;
                if (off + len > d.Length || len <= 0) continue;
                // Platform 3 (Windows) AND platform 0 (Unicode) are UTF-16BE; platform 1 (Mac)
                // is single-byte. Reading a platform-0 record as ASCII keeps every second byte --
                // the NULs -- and the family comes out as " A r i a l ", which then matches
                // nothing anyone asks for. It was visible only in a report that happened to print
                // the scanned names.
                string value = platform == 3 || platform == 0
                    ? System.Text.Encoding.BigEndianUnicode.GetString(d, off, len)
                    : System.Text.Encoding.ASCII.GetString(d, off, len);
                if (nameId == 1) family ??= value;
                else subfamily ??= value;
            }
            if (string.IsNullOrWhiteSpace(family)) return false;
            if (subfamily is not null)
            {
                bold = subfamily.Contains("bold", StringComparison.OrdinalIgnoreCase);
                italic = subfamily.Contains("italic", StringComparison.OrdinalIgnoreCase)
                         || subfamily.Contains("oblique", StringComparison.OrdinalIgnoreCase);
            }
            return true;
        }

        private static int Be16(byte[] d, int at) => (d[at] << 8) | d[at + 1];

        private static int Be32(byte[] d, int at)
            => (d[at] << 24) | (d[at + 1] << 16) | (d[at + 2] << 8) | d[at + 3];

        private static int[] Order(int slot) => slot switch
        {
            3 => new[] { 3, 1, 2, 0 },
            2 => new[] { 2, 0 },
            1 => new[] { 1, 0 },
            _ => new[] { 0 },
        };

        private static string? Locate(string fileName)
        {
            foreach (string dir in s_directories.Value)
            {
                try
                {
                    string path = Path.Combine(dir, fileName);
                    if (File.Exists(path)) return path;
                }
                catch (Exception)
                {
                    // An unreadable font directory is not worth failing a paint over.
                }
            }
            return null;
        }
    }
}
