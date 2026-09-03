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
                string? path = Locate(stem + suffix + ext) ?? (suffix.Length > 0 ? Locate(stem + ext) : null);
                if (path != null) return path;
            }

            // The guess missed, so ask the files what they are called.
            if (ScannedFamilies().TryGetValue(family, out string?[]? declared))
                foreach (int candidate in Order(slot))
                    if (declared[candidate] != null) return declared[candidate];
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
                // Platform 3 (Windows) is UTF-16BE; platform 1 (Mac) is single-byte.
                string value = platform == 3
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
