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
            // Selawik is Microsoft's open, metric-compatible Segoe UI replacement (github.com/microsoft/Selawik);
            // prefer it so text lays out like Windows, with Helvetica etc. as last-resort fallbacks.
            ["segoe ui"] = new[] { "Selawik", "Helvetica Neue", "Helvetica", "Arial", "Liberation Sans", "DejaVu Sans", "Roboto", "Droid Sans" },
            ["segoe ui semibold"] = new[] { "Selawik", "Helvetica Neue", "Helvetica", "Liberation Sans", "DejaVu Sans", "Roboto", "Droid Sans" },
            ["segoe ui variable"] = new[] { "Selawik", "Helvetica Neue", "Helvetica", "Liberation Sans", "DejaVu Sans", "Roboto", "Droid Sans" },
            // Fluent/MDL2 icon glyphs: "Symbols" (github.com/robloo/SymbolIconManager, WinSymbols3) maps the
            // Segoe Fluent Icons / Segoe MDL2 Assets PUA codepoints, so control glyphs render off-Windows.
            ["segoe fluent icons"] = new[] { "Symbols" },
            ["segoe mdl2 assets"] = new[] { "Symbols" },
            ["segoe ui symbol"] = new[] { "Symbols", "Apple Symbols", "Helvetica", "Liberation Sans", "DejaVu Sans", "Roboto", "Droid Sans" },
            ["tahoma"] = new[] { "Helvetica Neue", "Helvetica", "Arial", "Liberation Sans", "DejaVu Sans", "Roboto", "Droid Sans" },
            ["ms shell dlg"] = new[] { "Helvetica Neue", "Helvetica", "Liberation Sans", "DejaVu Sans", "Roboto", "Droid Sans" },
            ["ms shell dlg 2"] = new[] { "Helvetica Neue", "Helvetica", "Liberation Sans", "DejaVu Sans", "Roboto", "Droid Sans" },
            ["microsoft sans serif"] = new[] { "Helvetica", "Arial", "Liberation Sans", "DejaVu Sans", "Roboto", "Droid Sans" },
            ["arial"] = new[] { "Arial", "Helvetica Neue", "Helvetica", "Liberation Sans", "DejaVu Sans", "Roboto", "Droid Sans" },
            ["calibri"] = new[] { "Helvetica Neue", "Helvetica", "Liberation Sans", "DejaVu Sans", "Roboto", "Droid Sans" },
            // Cascadia Code ships with this repo (sdk/WpfWebGpu.Sdk/web/fonts), so it normally
            // resolves by its own name; the chain is for a head that does not bundle it. Nothing
            // else here has its programming ligatures, so those degrade to plain characters.
            ["cascadia code"] = new[] { "Cascadia Code", "Cascadia Mono", "Menlo", "Consolas", "DejaVu Sans Mono", "Droid Sans Mono" },
            ["cascadia mono"] = new[] { "Cascadia Mono", "Cascadia Code", "Menlo", "Consolas", "DejaVu Sans Mono", "Droid Sans Mono" },
            ["consolas"] = new[] { "Menlo", "Courier New", "Monaco", "DejaVu Sans Mono", "Droid Sans Mono", "Cutive Mono" },
            ["courier new"] = new[] { "Courier New", "Menlo", "Monaco", "DejaVu Sans Mono", "Droid Sans Mono", "Cutive Mono" },
            ["times new roman"] = new[] { "Times New Roman", "Times", "DejaVu Serif", "Noto Serif", "Droid Serif" },
            ["cambria"] = new[] { "Times New Roman", "Times", "DejaVu Serif", "Noto Serif", "Droid Serif" },
        };

        // The ultimate fallback family when nothing else resolves (a face is guaranteed to exist).
        // Roboto/Droid Sans are Android's -- it ships none of the others, and without them every
        // lookup fell through to cat[0], which on Android is AndroidClock.ttf: a font that contains
        // digits and nothing else, so text rendered as "20" with every letter missing.
        private static readonly string[] s_lastResort =
        {
            "Helvetica Neue", "Helvetica", "Arial", "Times New Roman", "Liberation Sans", "DejaVu Sans",
            "Roboto", "Droid Sans", "Noto Sans",
        };

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
        // OS/2 fsSelection bit 7 (USE_TYPO_METRICS): the font asks consumers to use the
        // sTypo* metrics for line spacing instead of the usWin* metrics.
        private const ushort FsSelectionUseTypoMetrics = 0x80;

        public static FontMetrics Build(OpenTypeFontData d)
        {
            // Match DWrite's DWRITE_FONT_METRICS. DWrite reports the OS/2 *Windows* metrics
            // (usWinAscent/usWinDescent) by default and only switches to the typographic
            // metrics when the font sets USE_TYPO_METRICS. The usWin* box is taller and
            // carries the internal leading above the caps that WPF control templates (e.g.
            // the Fluent CheckBox, which top-aligns its content with a fixed padding) rely
            // on. Using sTypo* unconditionally made the line box too tight, so top-aligned
            // text rendered a few pixels too high relative to the box.
            int asc, desc, gap;
            bool useTypo = d.HasOS2 && (d.FsSelection & FsSelectionUseTypoMetrics) != 0;
            if (d.HasOS2 && !useTypo && (d.UsWinAscent != 0 || d.UsWinDescent != 0))
            {
                asc = d.UsWinAscent;
                desc = d.UsWinDescent;   // stored as a positive magnitude
                gap = 0;                 // usWin* already envelop the full character box
            }
            else if (d.HasOS2 && d.STypoAscender != 0)
            {
                asc = d.STypoAscender;
                desc = d.STypoDescender;
                gap = d.STypoLineGap;
            }
            else
            {
                asc = d.Ascender;
                desc = d.Descender;
                gap = d.LineGap;
            }

            ushort cap = (ushort)(d.SCapHeight != 0 ? d.SCapHeight : (int)(asc * 0.72));
            ushort xh = (ushort)(d.SxHeight != 0 ? d.SxHeight : (int)(asc * 0.5));

            return new FontMetrics
            {
                DesignUnitsPerEm = d.UnitsPerEm,
                Ascent = (ushort)Math.Abs(asc),
                Descent = (ushort)Math.Abs(desc),
                LineGap = (short)gap,
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
