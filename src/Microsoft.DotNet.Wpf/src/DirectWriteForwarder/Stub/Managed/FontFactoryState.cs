// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// Process-wide state for the off-Windows TextInterface backend: the lazily-built system
// font catalog, the shared FontCollection, family lookup with substitution for common
// Windows UI families absent on macOS, and design-unit -> FontMetrics conversion.
//

using System;
using System.Collections.Generic;

namespace MS.Internal.Text.TextInterface.Managed
{
    internal static class FontFactoryState
    {
        private static readonly object s_lock = new();
        private static SystemFontCatalog s_catalog;
        private static FontCollection s_collection;

        internal static SystemFontCatalog Catalog
        {
            get
            {
                if (s_catalog == null)
                {
                    lock (s_lock)
                        s_catalog ??= SystemFontCatalog.Build();
                }
                return s_catalog;
            }
        }

        internal static FontCollection SystemCollection
        {
            get
            {
                if (s_collection == null)
                {
                    lock (s_lock)
                        s_collection ??= new FontCollection(Catalog);
                }
                return s_collection;
            }
        }

        // Substitutions for common Windows/WPF default families with no macOS equivalent.
        // Keys are lower-cased; each maps to an ordered list of candidate replacements.
        private static readonly Dictionary<string, string[]> s_substitutes = new(StringComparer.OrdinalIgnoreCase)
        {
            ["segoe ui"] = new[] { "Helvetica Neue", "Helvetica", "Arial", "DejaVu Sans" },
            ["segoe ui semibold"] = new[] { "Helvetica Neue", "Helvetica", "DejaVu Sans" },
            ["segoe ui symbol"] = new[] { "Apple Symbols", "Helvetica", "DejaVu Sans" },
            ["tahoma"] = new[] { "Helvetica Neue", "Helvetica", "Arial", "DejaVu Sans" },
            ["ms shell dlg"] = new[] { "Helvetica Neue", "Helvetica", "DejaVu Sans" },
            ["ms shell dlg 2"] = new[] { "Helvetica Neue", "Helvetica", "DejaVu Sans" },
            ["microsoft sans serif"] = new[] { "Helvetica", "Arial", "DejaVu Sans" },
            ["arial"] = new[] { "Arial", "Helvetica Neue", "Helvetica", "DejaVu Sans" },
            ["calibri"] = new[] { "Helvetica Neue", "Helvetica", "DejaVu Sans" },
            ["consolas"] = new[] { "Menlo", "Courier New", "Monaco", "DejaVu Sans Mono" },
            ["courier new"] = new[] { "Courier New", "Menlo", "Monaco", "DejaVu Sans Mono" },
            ["times new roman"] = new[] { "Times New Roman", "Times", "DejaVu Serif" },
            ["cambria"] = new[] { "Times New Roman", "Times", "DejaVu Serif" },
        };

        // The ultimate fallback family when nothing else resolves (a face is guaranteed to exist).
        private static readonly string[] s_lastResort = { "Helvetica Neue", "Helvetica", "Arial", "Times New Roman", "DejaVu Sans" };

        internal static FamilyRecord LookupFamily(string name)
        {
            SystemFontCatalog cat = Catalog;
            if (!string.IsNullOrEmpty(name) && cat.TryGetFamily(name, out FamilyRecord fam))
                return fam;

            if (!string.IsNullOrEmpty(name) && s_substitutes.TryGetValue(name, out string[] subs))
            {
                foreach (string s in subs)
                    if (cat.TryGetFamily(s, out FamilyRecord sub))
                        return sub;
            }

            foreach (string s in s_lastResort)
                if (cat.TryGetFamily(s, out FamilyRecord lr))
                    return lr;

            // Absolute last resort: the first enumerated family.
            return cat.FamilyCount > 0 ? cat[0] : null;
        }
    }

    internal static class FontMetricsBuilder
    {
        public static FontMetrics Build(OpenTypeFontData d)
        {
            // Prefer OS/2 typographic metrics (present on virtually all modern fonts); fall
            // back to hhea. DWrite reports ascent/descent as positive magnitudes.
            short asc = d.HasOS2 && d.STypoAscender != 0 ? d.STypoAscender : d.Ascender;
            short desc = d.HasOS2 && d.STypoDescender != 0 ? d.STypoDescender : d.Descender;
            short gap = d.HasOS2 && d.STypoAscender != 0 ? d.STypoLineGap : d.LineGap;

            ushort cap = (ushort)(d.SCapHeight != 0 ? d.SCapHeight : (int)(asc * 0.72));
            ushort xh = (ushort)(d.SxHeight != 0 ? d.SxHeight : (int)(asc * 0.5));

            return new FontMetrics
            {
                DesignUnitsPerEm = d.UnitsPerEm,
                Ascent = (ushort)Math.Abs(asc),
                Descent = (ushort)Math.Abs(desc),
                LineGap = gap,
                CapHeight = cap,
                XHeight = xh,
                UnderlinePosition = d.UnderlinePosition,
                UnderlineThickness = (ushort)Math.Abs(d.UnderlineThickness),
                StrikethroughPosition = d.YStrikeoutPosition != 0 ? d.YStrikeoutPosition : (short)(asc / 2),
                StrikethroughThickness = (ushort)(d.YStrikeoutSize != 0 ? Math.Abs(d.YStrikeoutSize) : Math.Max(1, (int)d.UnderlineThickness)),
            };
        }
    }
}
