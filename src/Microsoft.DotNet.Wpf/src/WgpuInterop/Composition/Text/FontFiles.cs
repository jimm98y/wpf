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
            // ...AND THE FALLBACK WALKS TOWARDS THE REGULAR, as Order does for files. Taking the
            // first face of the family in ANY other style meant a bold-italic request got face
            // zero -- Nirmala UI's REGULAR -- and then we emboldened and sheared it, where GDI
            // takes the family's BOLD face and shears that. Nirmala UI BI was 1.15 of GDI's ink
            // and its run 38 pixels long, while its regular, bold and italic were all exact.
            var byStyle = new int[4];
            for (int i = 0; i < 4; i++) byStyle[i] = -1;
            foreach (int sfnt in FaceOffsets(data))
            {
                if (!ReadNames(data, sfnt, out string? declared, out bool isBold, out bool isItalic))
                    continue;
                if (!string.Equals(declared, family, StringComparison.OrdinalIgnoreCase)) continue;
                int slot = (isBold ? 1 : 0) | (isItalic ? 2 : 0);
                if (byStyle[slot] < 0) byStyle[slot] = sfnt;
            }
            foreach (int candidate in Order((bold ? 1 : 0) | (italic ? 2 : 0)))
                if (byStyle[candidate] >= 0) return byStyle[candidate];
            for (int i = 0; i < 4; i++)
                if (byStyle[i] >= 0) return byStyle[i];
            return SfntOffset(data);
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

        /// <summary>A variable font's NAMED INSTANCE: the file it lives in and the axis settings
        /// that make it.</summary>
        internal readonly struct NamedInstance
        {
            internal NamedInstance(string path, int sfnt, Dictionary<uint, float> coords)
            { Path = path; Sfnt = sfnt; Coords = coords; }

            internal string Path { get; }
            internal int Sfnt { get; }
            internal Dictionary<uint, float> Coords { get; }
        }

        /// <summary>WINDOWS REGISTERS A VARIABLE FONT'S INSTANCES AS FAMILIES, and it names them
        /// from 'STAT'. EnumFontFamiliesEx on a stock Windows 11 lists "Segoe UI Variable Text",
        /// "Segoe UI Variable Text Light", "Segoe UI Variable Display Semibold" and nine more --
        /// families an application asks for by name, Windows' own shell among them -- and NOT the
        /// typographic family "Segoe UI Variable", which GDI does not know at all: asked for it,
        /// it hands back ARIAL. We knew only the file's name-1 record, so every one of those
        /// families resolved to nothing and its text drew in the fallback face.
        /// <para>The rule, read off 'STAT' and confirmed against that enumeration: take each fvar
        /// instance's coordinates, name each axis through STAT's value records in the table's
        /// AxisOrdering, drop the ones flagged ELIDABLE (bit 1 -- Segoe UI Variable's weight 400
        /// is "Regular" and elided), and what is left joins the family name, except Bold and
        /// Italic which are the STYLE. So (opsz 10.5, wght 400) is "Segoe UI Variable Text"
        /// regular, (opsz 10.5, wght 700) is that family in bold, and (opsz 36, wght 300) is
        /// "Segoe UI Variable Display Light".</para>
        /// <para>A face with no 'STAT' falls back to the instance's own subfamily string split the
        /// same way, which is what Bahnschrift's "Light SemiCondensed" needs.</para></summary>
        private static void ScanNamedInstances(byte[] d, int sfnt, string path, string family,
                                               Dictionary<string, NamedInstance?[]> into,
                                               bool faceBold, bool faceItalic)
        {
            Dictionary<string, int> tables = SfntTables(d, sfnt);
            if (!tables.TryGetValue("fvar", out int fvar) || fvar + 16 > d.Length) return;

            int axesOff = fvar + Be16(d, fvar + 4);
            int axisCount = Be16(d, fvar + 8), axisSize = Be16(d, fvar + 10);
            int instCount = Be16(d, fvar + 12), instSize = Be16(d, fvar + 14);
            if (axisCount == 0 || axisSize < 20 || instSize < 4 + axisCount * 4) return;

            var axisTags = new uint[axisCount];
            for (int i = 0; i < axisCount; i++)
            {
                int at = axesOff + i * axisSize;
                if (at + 20 > d.Length) return;
                axisTags[i] = (uint) Be32(d, at);
            }

            // STAT says how the axes are ordered in a name and what each value on them is called.
            var order = new int[axisCount];
            for (int i = 0; i < axisCount; i++) order[i] = i;
            // STAT indexes ITS OWN design axes, which need not be fvar's order: Segoe UI Variable
            // lists wght then opsz in fvar and opsz then wght in STAT, so matching the two by
            // index found nothing and every instance fell back to its subfamily string.
            var statTags = new List<uint>();
            var values = new List<(int Axis, float Value, float Min, float Max, int Flags, int NameId)>();
            if (tables.TryGetValue("STAT", out int stat) && stat + 20 <= d.Length)
            {
                // STAT: majorVersion(0) minorVersion(2) designAxisSize(4) designAxisCount(6)
                // designAxesOffset(8) axisValueCount(12) offsetToAxisValueOffsets(14).
                int designOff = stat + Be32(d, stat + 8);
                int designSize = Be16(d, stat + 4), designCount = Be16(d, stat + 6);
                for (int i = 0; i < designCount; i++)
                {
                    int at = designOff + i * designSize;
                    if (at + 8 > d.Length) break;
                    uint tag = (uint) Be32(d, at);
                    int ordering = Be16(d, at + 6);
                    statTags.Add(tag);
                    for (int a = 0; a < axisCount; a++)
                        if (axisTags[a] == tag && ordering < axisCount) order[a] = ordering;
                }

                int avOff = stat + Be32(d, stat + 14);
                int avCount = Be16(d, stat + 12);
                for (int i = 0; i < avCount; i++)
                {
                    int rec = avOff + i * 2;
                    if (rec + 2 > d.Length) break;
                    int v = avOff + Be16(d, rec);
                    if (v + 12 > d.Length) break;
                    int format = Be16(d, v), axis = Be16(d, v + 2), flags = Be16(d, v + 4);
                    int nameId = Be16(d, v + 6);
                    if (format == 1 || format == 3)
                        values.Add((axis, Fixed(d, v + 8), float.NaN, float.NaN, flags, nameId));
                    else if (format == 2 && v + 20 <= d.Length)
                        values.Add((axis, Fixed(d, v + 8), Fixed(d, v + 12), Fixed(d, v + 16), flags, nameId));
                    else if (format == 4)
                    {
                        int count = axis;                     // format 4 puts the count where the axis is
                        flags = Be16(d, v + 4); nameId = Be16(d, v + 6);
                        for (int k = 0; k < count && v + 8 + k * 6 + 6 <= d.Length; k++)
                            values.Add((Be16(d, v + 8 + k * 6), Fixed(d, v + 8 + k * 6 + 2),
                                        float.NaN, float.NaN, flags, nameId));
                    }
                }
            }

            int instOff = axesOff + axisCount * axisSize;
            for (int i = 0; i < instCount; i++)
            {
                int at = instOff + i * instSize;
                if (at + 4 + axisCount * 4 > d.Length) return;

                var coords = new Dictionary<uint, float>();
                var named = new List<(int Order, string Name, int Flags)>();
                for (int a = 0; a < axisCount; a++)
                {
                    float value = Fixed(d, at + 4 + a * 4);
                    coords[axisTags[a]] = value;
                    foreach ((int axis, float v, float min, float max, int flags, int nameId) in values)
                    {
                        if (axis < 0 || axis >= statTags.Count || statTags[axis] != axisTags[a]) continue;
                        bool hit = float.IsNaN(min)
                            ? Math.Abs(v - value) < 0.001f
                            : value >= min && value <= max && Math.Abs(v - value) < 0.001f;
                        if (!hit) continue;
                        if (NameById(d, sfnt, nameId) is { Length: > 0 } n) named.Add((order[a], n, flags));
                        break;
                    }
                }

                List<string> parts;
                if (named.Count > 0)
                {
                    named.Sort((x, y) => x.Order.CompareTo(y.Order));
                    parts = new List<string>();
                    foreach ((int _, string valueName, int flags) in named)
                        if ((flags & 2) == 0) parts.Add(valueName);   // ELIDABLE_AXIS_VALUE_NAME
                }
                // The fvar InstanceRecord is subfamilyNameID, flags, then the coordinates -- the
                // name is at offset ZERO, and reading the flags instead named every instance after
                // name id 0, which is the COPYRIGHT.
                else if (NameById(d, sfnt, Be16(d, at)) is { Length: > 0 } subfamily)
                    parts = new List<string>(subfamily.Split(' ', StringSplitOptions.RemoveEmptyEntries));
                else continue;

                // THE FACE'S OWN STYLE IS THE BASE. Cascadia Code Italic is one file whose
                // instances are named Light, Regular, Bold and so on -- none of them says
                // "Italic", because the whole FACE is -- so slotting by the instance name alone
                // filed its Bold as the family's upright bold and bold-italic fell back to the
                // upright file with a synthetic shear.
                bool bold = faceBold, italic = faceItalic;
                var keep = new List<string>();
                foreach (string part in parts)
                {
                    if (part.Equals("Bold", StringComparison.OrdinalIgnoreCase)) { bold = true; continue; }
                    if (part.Equals("Italic", StringComparison.OrdinalIgnoreCase)
                        || part.Equals("Oblique", StringComparison.OrdinalIgnoreCase)) { italic = true; continue; }
                    if (part.Equals("Regular", StringComparison.OrdinalIgnoreCase)
                        || part.Equals("Normal", StringComparison.OrdinalIgnoreCase)) continue;
                    keep.Add(part);
                }

                string name = keep.Count == 0 ? family : family + " " + string.Join(" ", keep);
                int slot = (bold ? 1 : 0) | (italic ? 2 : 0);
                RegisterInstance(into, name, slot, new NamedInstance(path, sfnt, coords));
                // ...AND UNDER THE NAME A LOGFONT CAN HOLD: lfFaceName is 32 characters including
                // its terminator, so Windows enumerates "Segoe UI Variable Display Semib" and an
                // application that read that back asks for exactly it.
                if (name.Length > 31)
                    RegisterInstance(into, name.Substring(0, 31), slot, new NamedInstance(path, sfnt, coords));
            }
        }

        private static void RegisterInstance(Dictionary<string, NamedInstance?[]> into, string family,
                                             int slot, NamedInstance instance)
        {
            if (!into.TryGetValue(family, out NamedInstance?[]? slots))
                into[family] = slots = new NamedInstance?[4];
            slots[slot] ??= instance;
        }

        private static float Fixed(byte[] d, int at) => at + 4 <= d.Length ? Be32(d, at) / 65536f : 0f;

        /// <summary>One name record of the face at <paramref name="sfnt"/>, English preferred.
        /// </summary>
        private static string? NameById(byte[] d, int sfnt, int wanted)
        {
            Dictionary<string, int> tables = SfntTables(d, sfnt);
            if (!tables.TryGetValue("name", out int nameOff) || nameOff + 6 > d.Length) return null;
            int count = Be16(d, nameOff + 2), strings = nameOff + Be16(d, nameOff + 4);
            string? best = null;
            for (int i = 0; i < count; i++)
            {
                int rec = nameOff + 6 + i * 12;
                if (rec + 12 > d.Length) break;
                int platform = Be16(d, rec), language = Be16(d, rec + 4), nameId = Be16(d, rec + 6);
                int len = Be16(d, rec + 8), off = strings + Be16(d, rec + 10);
                if (nameId != wanted || len <= 0 || off + len > d.Length) continue;
                string value = platform == 3 || platform == 0
                    ? System.Text.Encoding.BigEndianUnicode.GetString(d, off, len)
                    : System.Text.Encoding.ASCII.GetString(d, off, len);
                if (platform == 3 && language == 0x409) return value;
                best ??= value;
            }
            return best;
        }

        /// <summary>The table directory of one face.</summary>
        private static Dictionary<string, int> SfntTables(byte[] d, int sfnt)
        {
            var tables = new Dictionary<string, int>(StringComparer.Ordinal);
            if (sfnt + 12 > d.Length) return tables;
            int numTables = Be16(d, sfnt + 4);
            for (int i = 0; i < numTables; i++)
            {
                int rec = sfnt + 12 + i * 16;
                if (rec + 16 > d.Length) break;
                tables[System.Text.Encoding.ASCII.GetString(d, rec, 4)] = Be32(d, rec + 8);
            }
            return tables;
        }

        /// <summary>What weight the FILE declares, from 'OS/2'.usWeightClass; 400 when it has no
        /// OS/2 table to ask.
        /// <para>GDI does not simulate bold on a face that is already heavy, and "already heavy"
        /// is not macStyle -- Arial Black, Segoe UI Black and Segoe UI Semibold all leave the bold
        /// bit CLEAR and GDI still draws them unsimulated. Measured with GetTextExtentPoint32W at
        /// lfWeight 400 against 700: no advance grows for weight 900 (Arial Black, Segoe UI Black)
        /// or 600 (Segoe UI Semibold), and every one grows by a pixel per glyph for 500 (Dubai
        /// Medium, Yu Gothic Medium, Engravers MT), 400 (Impact, Franklin Gothic Medium), 350
        /// (Segoe UI Semilight) and 300 (Segoe UI Light, Calibri Light). So the line is two
        /// hundred below the weight asked for.</para></summary>
        public static int DeclaredWeight(byte[] data, int sfntOffset)
        {
            if (data.Length < sfntOffset + 12) return 400;
            int numTables = (data[sfntOffset + 4] << 8) | data[sfntOffset + 5];
            for (int i = 0; i < numTables; i++)
            {
                int rec = sfntOffset + 12 + i * 16;
                if (rec + 16 > data.Length) return 400;
                if (data[rec] != (byte) 'O' || data[rec + 1] != (byte) 'S'
                    || data[rec + 2] != (byte) '/' || data[rec + 3] != (byte) '2') continue;
                int off = Be32(data, rec + 8);
                if (off + 6 > data.Length) return 400;
                int weight = Be16(data, off + 4);
                return weight > 0 ? weight : 400;
            }
            return 400;
        }

        /// <summary>Whether a bold run has to be SIMULATED on this face: only when the face is
        /// neither declared bold nor already within two hundred of the weight asked for.</summary>
        public static bool NeedsBoldSimulation(byte[] data, int sfntOffset, bool declaredBold)
            => !declaredBold && DeclaredWeight(data, sfntOffset) <= 500;

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
                // THE TABLE IS A SHORTCUT, NOT THE LAST WORD. Its entry for a family names the
                // files this port was written against, and a machine can have MORE: Windows
                // Terminal installs a newer Cascadia Code with an ITALIC (registered under HKCU,
                // in Program Files\WindowsApps), and the table's "no italic file here" sent us to
                // the upright with a synthetic shear while GDI drew the real italic. So when the
                // table has nothing for the style asked for, the scan gets a say before the
                // fallback does.
                ScannedFamilies().TryGetValue(family, out string?[]? alsoScanned);
                foreach (int candidate in Order(slot))
                {
                    if (files[candidate] is { } tabled && Locate(tabled) is { } tabledPath)
                        return tabledPath;
                    if (alsoScanned?[candidate] is { } scannedPath) return scannedPath;
                    if (candidate == slot && s_scannedInstances is not null
                        && s_scannedInstances.TryGetValue(family, out NamedInstance?[]? inst)
                        && inst[candidate] is { } exactInstance)
                        return exactInstance.Path;
                }
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
            // A VARIABLE FONT'S NAMED INSTANCES ARE FAMILIES TOO -- "Segoe UI Variable Text" is
            // one, and Windows' own shell asks for it. See ScanNamedInstances.
            if (s_scannedInstances is not null
                && s_scannedInstances.TryGetValue(family, out NamedInstance?[]? instances))
            {
                foreach (int candidate in Order(slot))
                    if (instances[candidate] is { } instance) return instance.Path;
                for (int i = 0; i < instances.Length; i++)
                    if (instances[i] is { } any) return any.Path;
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
        private static Dictionary<string, NamedInstance?[]>? s_scannedInstances;

        /// <summary>The named instance a family resolves to, or null when the family is a plain
        /// face. The caller applies the coordinates to the font it opens.</summary>
        internal static NamedInstance? FindInstance(string? family, bool bold, bool italic)
        {
            if (string.IsNullOrWhiteSpace(family)) return null;
            ScannedFamilies();                       // fills s_scannedInstances
            if (s_scannedInstances is null) return null;
            // A REAL FILE FIRST. Bahnschrift's own name-1 record is "Bahnschrift" and so is its
            // Regular instance's family, and the file is the same either way; but a family that
            // BOTH a file and an instance claim (a static "Sitka Text" beside a variable one)
            // should come from the file, which is what Windows lists.
            if (Resolve(family!, bold, italic) is { } direct
                && (!s_scannedInstances.TryGetValue(family!, out NamedInstance?[]? byName)
                    || byName[(bold ? 1 : 0) | (italic ? 2 : 0)] is not { } exact
                    || !string.Equals(exact.Path, direct, StringComparison.OrdinalIgnoreCase)))
                return null;
            if (!s_scannedInstances.TryGetValue(family!, out NamedInstance?[]? slots)) return null;
            foreach (int candidate in Order((bold ? 1 : 0) | (italic ? 2 : 0)))
                if (slots[candidate] is { } instance) return instance;
            for (int i = 0; i < slots.Length; i++)
                if (slots[i] is { } any) return any;
            return null;
        }

        /// <summary>What the scan found, for a diagnostic that can say whether it ran at all.
        /// </summary>
        internal static IReadOnlyCollection<string> ScannedFamilyNames() => ScannedFamilies().Keys;

        /// <summary>Every font file the REGISTRY names, which is where Windows keeps the ones that
        /// are not in a font directory.
        /// <para>An application can install a font for itself, and a packaged one does it as a
        /// matter of course: Windows Terminal ships Cascadia Code and Cascadia Mono -- each with an
        /// ITALIC, which the copies in %WINDIR%\Fonts do not have -- under
        /// HKCU\...\CurrentVersion\Fonts\&lt;package&gt;, pointing into Program Files\WindowsApps.
        /// GDI finds them (asked for Cascadia Code Italic it hands back a real italic face, version
        /// 2407 with 3,076 glyphs, against the 2102 upright in the font folder); we walked two
        /// directories and found neither, so we sheared the upright and drew a different lean.</para>
        /// <para>Values are "Face Name (TrueType)" -> file, and the file is a bare name (relative to
        /// the system font directory) or a full path. The per-user hive nests them one level deeper,
        /// a subkey per package.</para></summary>
        private static List<string> RegistryFontFiles()
        {
            var paths = new List<string>();
            if (!OperatingSystem.IsWindows()) return paths;
            const int HkeyCurrentUser = unchecked((int) 0x80000001);
            const int HkeyLocalMachine = unchecked((int) 0x80000002);
            const string FontsKey = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Fonts";
            CollectFontValues((IntPtr) HkeyLocalMachine, FontsKey, paths, recurse: false);
            CollectFontValues((IntPtr) HkeyCurrentUser, FontsKey, paths, recurse: true);
            return paths;
        }

        private static void CollectFontValues(IntPtr hive, string subKey, List<string> into, bool recurse)
        {
            const int KeyRead = 0x20019, ErrorSuccess = 0;
            if (RegOpenKeyExW(hive, subKey, 0, KeyRead, out IntPtr key) != ErrorSuccess) return;
            try
            {
                var name = new char[512];
                var data = new byte[1024];
                for (int i = 0; ; i++)
                {
                    int nameLen = name.Length, dataLen = data.Length;
                    int rc = RegEnumValueW(key, i, name, ref nameLen, IntPtr.Zero, out int _, data, ref dataLen);
                    if (rc != ErrorSuccess) break;
                    string value = System.Text.Encoding.Unicode.GetString(data, 0, Math.Max(0, dataLen)).TrimEnd('\0');
                    if (value.Length == 0) continue;
                    string path = Path.IsPathRooted(value) ? value : Locate(value) ?? "";
                    if (path.Length > 0) into.Add(path);
                }

                if (!recurse) return;
                for (int i = 0; ; i++)
                {
                    int len = name.Length;
                    if (RegEnumKeyExW(key, i, name, ref len, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero,
                                      IntPtr.Zero) != ErrorSuccess)
                        break;
                    CollectFontValues(hive, subKey + "\\" + new string(name, 0, len), into, recurse: false);
                }
            }
            finally { RegCloseKey(key); }
        }

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
        private static extern int RegEnumValueW(IntPtr key, int index, char[] name, ref int nameLen,
                                                IntPtr reserved, out int type, byte[] data, ref int dataLen);

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
        private static extern int RegEnumKeyExW(IntPtr key, int index, char[] name, ref int nameLen,
                                                IntPtr reserved, IntPtr className, IntPtr classLen,
                                                IntPtr lastWrite);

        private static Dictionary<string, string?[]> ScannedFamilies()
        {
            if (s_scanned is not null) return s_scanned;
            var found = new Dictionary<string, string?[]>(StringComparer.OrdinalIgnoreCase);
            var instances = new Dictionary<string, NamedInstance?[]>(StringComparer.OrdinalIgnoreCase);
            var toScan = new List<string>();
            foreach (string dir in s_directories.Value)
            {
                try { toScan.AddRange(Directory.GetFiles(dir)); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
            toScan.AddRange(RegistryFontFiles());
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            {
                foreach (string path in toScan)
                {
                    if (!seen.Add(path)) continue;
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
                        // ...UNDER THE TYPOGRAPHIC FAMILY (name 16) where the face has one: Sitka's
                        // name-1 family is "Sitka Text" (its default optical size), and Windows
                        // lists the instances as "Sitka Banner", "Sitka Heading" and so on off the
                        // typographic "Sitka".
                        ScanNamedInstances(head, sfnt, path,
                                           NameById(head, sfnt, 16) is { Length: > 0 } typographic
                                               ? typographic : family!, instances, bold, italic);
                    }
                }
            }
            s_scannedInstances = instances;
            // WPF_FONT_INSTANCES=<path>: every named-instance family the scan registered, for
            // asking whether a family Windows lists is one we know.
            if (Environment.GetEnvironmentVariable("WPF_FONT_INSTANCES") is { Length: > 0 } dump)
            {
                var sb = new System.Text.StringBuilder();
                foreach (KeyValuePair<string, NamedInstance?[]> kv in instances)
                {
                    sb.Append(kv.Key).Append(" :");
                    for (int i = 0; i < 4; i++)
                        if (kv.Value[i] is { } inst)
                        {
                            sb.Append(' ').Append(i).Append("={");
                            foreach (KeyValuePair<uint, float> c in inst.Coords)
                                sb.Append((char) (c.Key >> 24)).Append((char) ((c.Key >> 16) & 0xFF))
                                  .Append((char) ((c.Key >> 8) & 0xFF)).Append((char) (c.Key & 0xFF))
                                  .Append('=').Append(c.Value).Append(' ');
                            sb.Append('}');
                        }
                    sb.Append(Environment.NewLine);
                }
                try { File.WriteAllText(dump, sb.ToString()); } catch (IOException) { }
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
        internal static bool ReadNames(byte[] d, int sfnt, out string? family, out bool bold, out bool italic)
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
                int language = Be16(d, rec + 4);
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
                // ENGLISH FIRST, and not merely "the first record in the file". A name table is
                // localized: Leelawadee UI Bold's subfamily records are SPANISH, and the first of
                // them is "Negreta", which contains neither "bold" nor "italic" -- so the bold file
                // called itself regular, took the regular slot (the scan keeps the first file per
                // slot and LeelaUIb sorts before LeelawUI), and EVERY style of that family resolved
                // to the bold face: its regular text drew at nearly twice GDI's ink, 12M of the
                // untested-families battery. The same applies to the family name, where a CJK
                // face's first name-1 record is the Japanese or Chinese one and the English name is
                // what a caller asks for.
                bool english = platform == 3 && language == 0x409;
                if (nameId == 1) { if (english || family is null) family = value; }
                else { if (english || subfamily is null) subfamily = value; }
            }
            if (string.IsNullOrWhiteSpace(family)) return false;
            if (subfamily is not null)
            {
                bold = subfamily.Contains("bold", StringComparison.OrdinalIgnoreCase);
                italic = subfamily.Contains("italic", StringComparison.OrdinalIgnoreCase)
                         || subfamily.Contains("oblique", StringComparison.OrdinalIgnoreCase);
            }
            // ...AND 'head'.macStyle HAS A VOTE, because it says the same thing in no language at
            // all. Four faces on a stock Windows install disagree with their own subfamily string
            // -- Monotype Corsiva and Palace Script MT say "Regular" and lean, Lucida Calligraphy
            // and Lucida Handwriting say "Italic" and clear the bit -- and all four are italic in
            // fact, so the two are OR-ed rather than ranked.
            DeclaredStyle(d, sfnt, out bool headBold, out bool headItalic);
            bold |= headBold; italic |= headItalic;
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
