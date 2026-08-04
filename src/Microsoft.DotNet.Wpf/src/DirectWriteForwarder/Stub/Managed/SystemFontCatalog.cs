// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// Enumerates installed fonts (no platform font API) and groups faces by family so
// the off-Windows FontCollection/FontFamily can resolve typefaces the way DirectWrite's
// system font collection does on Windows. Scans the standard macOS font directories
// for .ttf/.otf/.ttc/.otc, parses each face's name/OS2/head tables, and buckets by
// family name (typographic family preferred). Fonts are re-parsed on demand (design-unit
// metrics/cmap) via OpenTypeFontData; only lightweight metadata is retained here.
//

using System;
using System.Collections.Generic;
using System.IO;

namespace MS.Internal.Text.TextInterface.Managed
{
    // Lightweight per-face metadata; the heavy OpenTypeFontData is loaded lazily.
    internal sealed class FaceRecord
    {
        public string FilePath;
        public int FaceIndex;
        public int SfntOffset;
        public string FamilyName;
        public string FaceName;          // subfamily, e.g. "Bold Italic"
        public FontWeight Weight;
        public FontStretch Stretch;
        public FontStyle Style;
        public bool IsCff;
        public bool IsSymbol;

        private OpenTypeFontData _data;
        private static readonly Dictionary<string, byte[]> s_fileCache = new(StringComparer.OrdinalIgnoreCase);

        // Builds a face record for an arbitrary font file + face index (used for CreateFontFace
        // on files that may live outside the system catalog, e.g. app fonts).
        public static FaceRecord FromFile(string path, int faceIndex)
        {
            byte[] bytes;
            lock (s_fileCache)
            {
                if (!s_fileCache.TryGetValue(path, out bytes))
                {
                    bytes = File.ReadAllBytes(path);
                    s_fileCache[path] = bytes;
                }
            }
            int sfntOffset = ResolveSfntOffset(bytes, faceIndex);
            if (sfntOffset < 0) sfntOffset = 0;
            var data = new OpenTypeFontData(bytes, sfntOffset);
            return new FaceRecord
            {
                FilePath = path,
                FaceIndex = faceIndex,
                SfntOffset = sfntOffset,
                FamilyName = data.TypographicFamilyName ?? data.FamilyName,
                FaceName = data.TypographicSubfamilyName ?? data.SubfamilyName ?? "Regular",
                Weight = (FontWeight)data.UsWeightClass,
                Stretch = (data.UsWidthClass is >= 1 and <= 9) ? (FontStretch)data.UsWidthClass : FontStretch.Normal,
                Style = (data.HasOS2 ? ((data.FsSelection & 0x001) != 0) : ((data.MacStyle & 0x2) != 0)) ? FontStyle.Italic : FontStyle.Normal,
                IsCff = data.IsCff,
                IsSymbol = data.IsSymbolFont,
                _data = data,
            };
        }

        private static int ResolveSfntOffset(byte[] bytes, int faceIndex)
        {
            if (bytes.Length >= 12 && bytes[0] == (byte)'t' && bytes[1] == (byte)'t' &&
                bytes[2] == (byte)'c' && bytes[3] == (byte)'f')
            {
                uint numFonts = (uint)((bytes[8] << 24) | (bytes[9] << 16) | (bytes[10] << 8) | bytes[11]);
                if (faceIndex < 0 || (uint)faceIndex >= numFonts) return -1;
                int dirPos = 12 + faceIndex * 4;
                if (dirPos + 4 > bytes.Length) return -1;
                return (bytes[dirPos] << 24) | (bytes[dirPos + 1] << 16) | (bytes[dirPos + 2] << 8) | bytes[dirPos + 3];
            }
            return faceIndex <= 0 ? 0 : -1;
        }

        public OpenTypeFontData GetData()
        {
            if (_data != null) return _data;
            byte[] bytes;
            lock (s_fileCache)
            {
                if (!s_fileCache.TryGetValue(FilePath, out bytes))
                {
                    bytes = File.ReadAllBytes(FilePath);
                    s_fileCache[FilePath] = bytes;
                }
            }
            _data = new OpenTypeFontData(bytes, SfntOffset);
            return _data;
        }
    }

    internal sealed class FamilyRecord
    {
        public string Name;
        public readonly List<FaceRecord> Faces = new();
    }

    internal sealed class SystemFontCatalog
    {
        // Ordinal-ignore-case family lookup, mirroring DWrite's case-insensitive family names.
        private readonly Dictionary<string, FamilyRecord> _families = new(StringComparer.OrdinalIgnoreCase);
        private readonly List<FamilyRecord> _ordered = new();

        public IReadOnlyList<FamilyRecord> Families => _ordered;
        public int FamilyCount => _ordered.Count;

        public bool TryGetFamily(string name, out FamilyRecord family)
            => _families.TryGetValue(name, out family);

        public FamilyRecord this[int index] => _ordered[index];

        private static readonly string[] s_customUri = Array.Empty<string>();

        public static SystemFontCatalog Build()
        {
            var catalog = new SystemFontCatalog();
            foreach (string dir in FontDirectories())
            {
                if (!Directory.Exists(dir)) continue;
                IEnumerable<string> files;
                try { files = Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories); }
                catch { continue; }
                foreach (string file in files)
                {
                    string ext = Path.GetExtension(file).ToLowerInvariant();
                    if (ext != ".ttf" && ext != ".otf" && ext != ".ttc" && ext != ".otc") continue;
                    try { catalog.AddFile(file); } catch { /* skip unreadable/broken font */ }
                }
            }
            return catalog;
        }

        private static IEnumerable<string> FontDirectories()
        {
            if (OperatingSystem.IsBrowser())
            {
                // WebAssembly: no OS fonts. The app head bundles fonts into the wasm
                // virtual filesystem under /fonts (see the GalleryWasm csproj).
                yield return "/fonts";
            }
            else if (OperatingSystem.IsIOS() || OperatingSystem.IsMacCatalyst() || OperatingSystem.IsTvOS())
            {
                // iOS ships its system fonts outside the app sandbox, so an app cannot read
                // /System/Library/Fonts the way macOS does - fonts have to travel in the app
                // bundle (BundleResource items under a "fonts" folder), which is also what the
                // browser head does. The bundle is the ONLY source that works: the simulator
                // runtime has no /System/Library/Fonts at all (verified), and on device it is
                // outside the sandbox - so unlike macOS there is no system fallback to lean on.
                yield return Path.Combine(AppContext.BaseDirectory, "fonts");
            }
            else if (OperatingSystem.IsMacOS())
            {
                yield return "/System/Library/Fonts";
                yield return "/System/Library/Fonts/Supplemental";
                yield return "/Library/Fonts";
                string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                if (!string.IsNullOrEmpty(home)) yield return Path.Combine(home, "Library", "Fonts");
            }
            else if (OperatingSystem.IsWindows())
            {
                // Machine fonts (C:\Windows\Fonts) plus per-user fonts installed without admin
                // (%LOCALAPPDATA%\Microsoft\Windows\Fonts). Without this the catalog was empty on
                // Windows and every font-family lookup failed, FailFast-ing in FontFamily.
                string systemFonts = Environment.GetFolderPath(Environment.SpecialFolder.Fonts);
                if (!string.IsNullOrEmpty(systemFonts)) yield return systemFonts;
                string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                if (!string.IsNullOrEmpty(localAppData)) yield return Path.Combine(localAppData, "Microsoft", "Windows", "Fonts");
            }
            else
            {
                // Generic *nix fallbacks.
                yield return "/usr/share/fonts";
                yield return "/usr/local/share/fonts";
                string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                if (!string.IsNullOrEmpty(home)) yield return Path.Combine(home, ".fonts");
            }
        }

        private void AddFile(string file)
        {
            byte[] bytes = File.ReadAllBytes(file);
            foreach ((int faceIndex, int sfntOffset) in EnumerateFaces(bytes))
            {
                OpenTypeFontData data;
                try { data = new OpenTypeFontData(bytes, sfntOffset); }
                catch { continue; }

                string family = data.TypographicFamilyName ?? data.FamilyName;
                if (string.IsNullOrEmpty(family)) continue;

                var rec = new FaceRecord
                {
                    FilePath = file,
                    FaceIndex = faceIndex,
                    SfntOffset = sfntOffset,
                    FamilyName = family,
                    FaceName = data.TypographicSubfamilyName ?? data.SubfamilyName ?? "Regular",
                    Weight = (FontWeight)data.UsWeightClass,
                    Stretch = ClampStretch(data.UsWidthClass),
                    Style = DeriveStyle(data),
                    IsCff = data.IsCff,
                    IsSymbol = data.IsSymbolFont,
                };

                if (!_families.TryGetValue(family, out FamilyRecord fam))
                {
                    fam = new FamilyRecord { Name = family };
                    _families[family] = fam;
                    _ordered.Add(fam);
                }
                fam.Faces.Add(rec);
            }
        }

        private static FontStyle DeriveStyle(OpenTypeFontData d)
        {
            // OS/2 fsSelection: bit0 ITALIC, bit9 OBLIQUE. Fall back to head.macStyle bit1.
            if (d.HasOS2)
            {
                if ((d.FsSelection & 0x200) != 0) return FontStyle.Oblique;
                if ((d.FsSelection & 0x001) != 0) return FontStyle.Italic;
                return FontStyle.Normal;
            }
            return (d.MacStyle & 0x2) != 0 ? FontStyle.Italic : FontStyle.Normal;
        }

        private static FontStretch ClampStretch(ushort usWidthClass)
        {
            if (usWidthClass < 1 || usWidthClass > 9) return FontStretch.Normal;
            return (FontStretch)usWidthClass;
        }

        // Yields (faceIndex, sfntOffset) for each face in a file: one for a plain sfnt,
        // several for a 'ttcf'/'otc' collection.
        private static IEnumerable<(int faceIndex, int sfntOffset)> EnumerateFaces(byte[] bytes)
        {
            if (bytes.Length >= 12 &&
                bytes[0] == (byte)'t' && bytes[1] == (byte)'t' &&
                bytes[2] == (byte)'c' && bytes[3] == (byte)'f')
            {
                uint numFonts = ReadU32(bytes, 8);
                for (int i = 0; i < numFonts; i++)
                {
                    int dirPos = 12 + i * 4;
                    if (dirPos + 4 > bytes.Length) yield break;
                    yield return (i, (int)ReadU32(bytes, dirPos));
                }
            }
            else
            {
                yield return (0, 0);
            }
        }

        private static uint ReadU32(byte[] b, int o)
            => (uint)((b[o] << 24) | (b[o + 1] << 16) | (b[o + 2] << 8) | b[o + 3]);
    }
}
