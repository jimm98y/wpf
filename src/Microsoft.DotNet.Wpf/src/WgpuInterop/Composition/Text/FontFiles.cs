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
            return null;
        }

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
