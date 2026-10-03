// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#nullable enable

using System.Collections.Generic;
using System.IO;
using System.Text;

namespace System.Windows.Forms
{
    /// <summary>
    ///  Reproduces the lists of Windows 11's ChooseFont dialog (comdlg32 over fms.dll over GDI) from font files alone:
    ///  how installed fonts are grouped into families, how each face is named, which faces are simulated, the order
    ///  of the style list and the script list. Reads the OpenType tables itself (name, OS/2, head, cmap, fvar, STAT);
    ///  no platform API is used, so it runs on every head. See ChooseFontModel.md beside this file for the algorithm and its provenance.
    /// </summary>
    internal static class ChooseFontModel
    {
        internal enum FaceStyle { Normal = 0, Italic = 1, Oblique = 2 }

        internal sealed class Face
        {
            /// <summary>The name in the dialog's style list (e.g. "Narrow Bold Italic", "Bold Oblique").</summary>
            public string Name = "";
            /// <summary>fms weight (OS/2 usWeightClass, possibly corrected from the name), 700 for a simulated bold.</summary>
            public int Weight;
            /// <summary>fms stretch 1..9 (OS/2 usWidthClass, possibly corrected from the name).</summary>
            public int Stretch;
            public FaceStyle Style;
            public bool SimulatedBold;
            public bool SimulatedOblique;
            public bool IsSimulated => SimulatedBold || SimulatedOblique;
            /// <summary>For a simulated face, the real face it is drawn from.</summary>
            public Face? SimulatedFrom;
            public string FilePath = "";
            public int FaceIndex;
            /// <summary>fvar named-instance index for a variable font, otherwise -1.</summary>
            public int NamedInstance = -1;
            /// <summary>The GDI LOGFONT for this face: lfFaceName, lfWeight, lfItalic (as FmsGetGDILogFont returns it).
            ///  new Font(GdiFamilyName, size, GdiWeight >= 700 ? Bold : 0 | GdiItalic ? Italic : 0) selects it.</summary>
            public string GdiFamilyName = "";
            public int GdiWeight;
            public bool GdiItalic;
            public override string ToString() => Name;
        }

        internal sealed class Script
        {
            public string Name = "";
            public byte CharSet;
            /// <summary>The sample comdlg32 shows for the script (string resource 0x700 + charset).</summary>
            public string Sample = "";
            public override string ToString() => Name;
        }

        internal sealed class Family
        {
            public string Name = "";
            /// <summary>GDI face name of the family's most representative face; the dialog enumerates scripts with it.</summary>
            public string RepresentativeGdiName = "";
            public List<Face> Faces = new();
            public List<Script> Scripts = new();
            public override string ToString() => Name;
        }

        /// <summary>
        ///  Builds the dialog's family list (sorted as the combo box sorts it), each family's style list (in the
        ///  dialog's order, simulations included) and script list.
        /// </summary>
        /// <param name="fontFilesInLoadOrder">TrueType/OpenType files (.ttf/.otf/.ttc) in the order the system loads
        ///  them: on Windows Marlett, then HKLM and HKCU ...\CurrentVersion\Fonts in registry order, then packaged
        ///  (AppX windows.sharedFonts) fonts. The order decides the order of equal-weight styles.</param>
        /// <param name="ansiCodePage">The system ANSI code page; its script is listed first when a font supports it.</param>
        /// <param name="gdiLanguageId">The system locale's LANGID, which picks the GDI (legacy) family names.</param>
        /// <param name="hiddenFamilies">Families the font settings hide (fms "Inactive Fonts"); null for none.</param>
        public static List<Family> Build(IEnumerable<string> fontFilesInLoadOrder, int ansiCodePage = 1252, int gdiLanguageId = 0x409,
            ICollection<string>? hiddenFamilies = null)
        {
            // GDI families in first-load order; faces of a family in load order.
            var gdiOrder = new List<string>();
            var gdiFamilies = new Dictionary<string, List<GdiFace>>(StringComparer.OrdinalIgnoreCase);
            foreach (string path in fontFilesInLoadOrder)
            {
                List<Sfnt> faces;
                try { faces = Sfnt.ReadFile(path); }
                catch (Exception) { continue; }
                foreach (Sfnt sf in faces)
                {
                    foreach (GdiFace g in GdiFacesOf(sf, gdiLanguageId))
                    {
                        if (!gdiFamilies.TryGetValue(g.Family, out List<GdiFace>? list))
                        {
                            gdiFamilies[g.Family] = list = new List<GdiFace>();
                            gdiOrder.Add(g.Family);
                        }
                        list.Add(g);
                    }
                }
            }

            foreach (string k in gdiOrder)
            {
                foreach (GdiFace g in gdiFamilies[k])
                    FmsName(g);
            }

            // fms enumeration order: one face per GDI family (the one EnumFontFamiliesEx reports), in GDI family order;
            // then, fms family by fms family, the remaining faces of each of its GDI families.
            var reps = new Dictionary<string, GdiFace>(StringComparer.OrdinalIgnoreCase);
            foreach (string k in gdiOrder)
                reps[k] = Representative(gdiFamilies[k]);
            var seq = new List<GdiFace>();
            foreach (string k in gdiOrder)
                seq.Add(reps[k]);
            var fmsFamilyOrder = new List<string>();
            var seenFamily = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string k in gdiOrder)
            {
                if (seenFamily.Add(reps[k].FmsFamily))
                    fmsFamilyOrder.Add(reps[k].FmsFamily);
            }

            foreach (string f in fmsFamilyOrder)
            {
                foreach (string k in gdiOrder)
                {
                    if (!string.Equals(reps[k].FmsFamily, f, StringComparison.OrdinalIgnoreCase))
                        continue;
                    foreach (GdiFace g in gdiFamilies[k])
                    {
                        if (g != reps[k])
                            seq.Add(g);
                    }
                }
            }

            // The same face registered twice (e.g. a packaged copy of a system font) is enumerated once.
            var families = new Dictionary<string, List<Entry>>(StringComparer.Ordinal);
            var familyOrder = new List<string>();
            var seenFace = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (GdiFace g in seq)
            {
                if (!seenFace.Add(g.Family + "\u0001" + g.Weight + "\u0001" + g.Italic))
                    continue;
                if (hiddenFamilies is not null && hiddenFamilies.Contains(g.FmsFamily))
                    continue;
                var e = new Entry
                {
                    Real = g, FaceName = g.FmsFace, Weight = g.FmsWeight, Stretch = g.FmsStretch, Style = g.FmsStyle,
                    LfWeight = g.Weight, LfItalic = g.Italic
                };
                if (!families.TryGetValue(g.FmsFamily, out List<Entry>? list))
                {
                    families[g.FmsFamily] = list = new List<Entry>();
                    familyOrder.Add(g.FmsFamily);
                }

                list.Add(e);
            }

            var result = new List<Family>();
            foreach (string name in familyOrder)
            {
                List<Entry> entries = families[name];
                bool variable = false;
                foreach (Entry e in entries)
                    variable |= e.Real.Instance >= 0;
                if (!variable)
                    entries.AddRange(AddSimulations(entries));

                var fam = new Family { Name = name };
                var made = new Dictionary<Entry, Face>();
                foreach (Entry e in DialogStyles(entries))
                {
                    var face = new Face
                    {
                        Name = e.FaceName, Weight = e.Weight, Stretch = e.Stretch, Style = (FaceStyle)e.Style,
                        SimulatedBold = e.SimBold, SimulatedOblique = e.SimOblique,
                        FilePath = e.Real.Font.Path, FaceIndex = e.Real.Font.Index, NamedInstance = e.Real.Instance,
                        GdiFamilyName = e.Real.Family, GdiWeight = e.LfWeight, GdiItalic = e.LfItalic
                    };
                    made[e] = face;
                    fam.Faces.Add(face);
                }

                foreach (Entry e in entries)
                {
                    if (e.Base is not null && made.TryGetValue(e, out Face? sim) && made.TryGetValue(e.Base, out Face? from))
                        sim.SimulatedFrom = from;
                }

                GdiFace rep = BestMatch(entries).Real;
                fam.RepresentativeGdiName = rep.Family;
                var seenScript = new HashSet<string>(StringComparer.Ordinal);
                foreach (GdiFace g in gdiFamilies[rep.Family])
                {
                    foreach (byte cs in FaceCharsets(g.Font, ansiCodePage))
                    {
                        string sn = ScriptName(cs);
                        if (seenScript.Add(sn))
                            fam.Scripts.Add(new Script { Name = sn, CharSet = cs, Sample = ScriptSample(cs) });
                    }
                }

                result.Add(fam);
            }

            result.Sort((a, b) => CompareFamilyNames(a.Name, b.Name));
            return result;
        }

        // ------------------------------------------------------------------ GDI's view of a font face

        private sealed class GdiFace
        {
            public Sfnt Font = null!;
            public int Instance = -1;
            public string Family = "";
            public int Weight;
            public bool Italic;
            public string TypoFamily = "";
            public string StyleName = "";
            public string FmsFamily = "";
            public string FmsFace = "";
            public int FmsWeight, FmsStretch, FmsStyle;
        }

        private static List<GdiFace> GdiFacesOf(Sfnt f, int lang)
        {
            var list = new List<GdiFace>();
            int os2Weight = f.Os2 is not null ? f.Os2.Weight : 400;
            bool italic = f.Os2 is not null ? (f.Os2.FsSelection & 1) != 0 : (f.MacStyle & 2) != 0;
            if (f.Fvar is null)
            {
                list.Add(new GdiFace { Font = f, Family = Trunc31(f.NameByLang(1, lang) ?? ""), Weight = os2Weight, Italic = italic });
                return list;
            }

            // A variable font: GDI enumerates every named instance, named from STAT (elidable values dropped,
            // axes in STAT order); the GDI family leaves out the RIBBI part (a 700 weight, the italic axis).
            string typo = f.NameByLang(16, lang) ?? f.NameByLang(1, lang) ?? "";
            for (int k = 0; k < f.Fvar.Instances.Count; k++)
            {
                FvarInstance inst = f.Fvar.Instances[k];
                int w = inst.Coords.TryGetValue("wght", out double wv) ? (int)Math.Round(wv) : os2Weight;
                var named = new List<string>();
                var famParts = new List<string>();
                string? fallback = null;
                if (f.Stat is not null)
                {
                    fallback = f.NameByLang(f.Stat.ElidedFallbackNameId, 0x409);
                    var axes = new List<int>();
                    for (int a = 0; a < f.Stat.Axes.Count; a++)
                        axes.Add(a);
                    axes.Sort((x, y) => f.Stat.Axes[x].Ordering != f.Stat.Axes[y].Ordering
                        ? f.Stat.Axes[x].Ordering.CompareTo(f.Stat.Axes[y].Ordering) : x.CompareTo(y));
                    foreach (int a in axes)
                    {
                        string tag = f.Stat.Axes[a].Tag;
                        StatValue? hit = null;
                        bool inFvar = inst.Coords.TryGetValue(tag, out double c);
                        foreach (StatValue v in f.Stat.Values)
                        {
                            if (v.AxisIndex != a)
                                continue;
                            if (!inFvar) { hit = v; break; }
                            if (v.Format == 2 ? (v.RangeMin <= c && c <= v.RangeMax) : Math.Abs(v.Value - c) < 1e-3) { hit = v; break; }
                        }

                        if (hit is null || (hit.Flags & 2) != 0)
                            continue;
                        string nm = f.NameByLang(hit.NameId, 0x409) ?? "";
                        named.Add(nm);
                        if (!(tag == "wght" && w == 700) && tag != "ital")
                            famParts.Add(nm);
                    }
                }
                else
                {
                    string sub = f.NameByLang(inst.SubfamilyNameId, 0x409) ?? "";
                    if (sub.Length > 0 && sub != "Regular")
                        named.Add(sub);
                }

                list.Add(new GdiFace
                {
                    Font = f, Instance = k, Weight = w, Italic = italic, TypoFamily = typo,
                    StyleName = named.Count > 0 ? string.Join(" ", named) : (fallback ?? "Regular"),
                    Family = Trunc31(famParts.Count > 0 ? typo + " " + string.Join(" ", famParts) : typo)
                });
            }

            return list;
        }

        private static string Trunc31(string s) => s.Length > 31 ? s.Substring(0, 31) : s;

        /// <summary>The face EnumFontFamiliesEx reports for a family when enumerating families.</summary>
        private static GdiFace Representative(List<GdiFace> faces)
        {
            GdiFace best = faces[0];
            foreach (GdiFace g in faces)
            {
                int c = (g.Italic ? 1 : 0).CompareTo(best.Italic ? 1 : 0);
                if (c == 0) c = Math.Abs(g.Weight - 400).CompareTo(Math.Abs(best.Weight - 400));
                if (c == 0) c = g.Weight.CompareTo(best.Weight);
                if (c < 0) best = g;
            }

            return best;
        }

        // ------------------------------------------------------------------ fms naming

        private static void FmsName(GdiFace g)
        {
            int w, st, style; bool wws;
            Os2Props(g.Font, out w, out st, out style, out wws);
            if (g.Instance >= 0)
            {
                g.FmsFamily = g.TypoFamily; g.FmsFace = g.StyleName;
                g.FmsWeight = w; g.FmsStretch = st; g.FmsStyle = style;
                return;
            }

            var props = new Props { Weight = w, Stretch = st, Style = style };
            Dictionary<int, string> recs = g.Font.FmsNameRecords(0x409);
            string? fam, face; bool isWws = wws;
            if (wws || !(recs.ContainsKey(21) && recs.ContainsKey(22)))
            {
                fam = recs.TryGetValue(16, out string? t16) ? t16 : recs.TryGetValue(1, out string? t1) ? t1 : null;
                face = recs.TryGetValue(17, out string? t17) ? t17 : recs.TryGetValue(2, out string? t2) ? t2 : null;
            }
            else
            {
                fam = recs[21]; face = recs[22]; isWws = true;
            }

            if (fam is null || face is null || fam.Length * 2 >= 0x41 || face.Length * 2 >= 0x41)
            {
                // GdiPopulateFontNameProps: GDI family name and WWS strings.
                props = new Props { Weight = g.Weight, Stretch = 5, Style = g.Italic ? 1 : 0 };
                fam = g.Family;
                int nw = NormalizedWeight(g.Weight);
                face = MakeFontName(new[] { null, null, nw != 400 ? WwsWeight(nw) : null, props.Style != 0 ? WwsStyle(props.Style) : null }, "Regular");
            }
            else if (!isWws)
            {
                ResolveFontName(ref fam, ref face, props);
            }

            g.FmsFamily = fam; g.FmsFace = face ?? "Regular";
            g.FmsWeight = props.Weight; g.FmsStretch = props.Stretch; g.FmsStyle = props.Style;
        }

        private sealed class Props { public int Weight, Stretch, Style; }

        private static void Os2Props(Sfnt f, out int weight, out int stretch, out int style, out bool wws)
        {
            if (f.Os2 is null)
            {
                weight = (f.MacStyle & 1) != 0 ? 700 : 400; stretch = 5; style = (f.MacStyle & 2) != 0 ? 1 : 0; wws = false;
                return;
            }

            weight = f.Os2.Weight;
            if (weight >= 1000) weight = 400;
            else if (weight >= 1 && weight <= 9) weight *= 100;
            stretch = f.Os2.Width >= 1 && f.Os2.Width <= 9 ? f.Os2.Width : 5;
            int fs = f.Os2.FsSelection;
            style = (fs & 0x200) != 0 ? 2 : (fs & 1) != 0 ? 1 : 0;
            wws = (fs & 0x100) != 0;
        }

        // The fms term table (fms.dll, 107 entries at VA 0x180044660): category, value, text.
        // Categories: 10 regular, 7 style (1 italic, 2 oblique), 15 stretch, 12 weight, 21 optical purpose.
        private static readonly (int Cat, int Val, string Text)[] s_terms =
        {
            (10,0,"Book"),(10,0,"Normal"),(10,0,"Regular"),(10,0,"Upright"),(10,0,"Roman"),
            (7,1,"Ita"),(7,1,"Ital"),(7,1,"Italic"),(7,1,"Cursive"),(7,1,"Kursiv"),(7,2,"Inclined"),(7,2,"Oblique"),(7,2,"Backslanted"),(7,2,"Backslant"),(7,2,"Slanted"),
            (15,1,"Ultra Compressed"),(15,1,"Ultra Condensed"),(15,1,"Ultra Cond"),(15,2,"Compressed"),(15,2,"Extra Condensed"),(15,2,"Ext Condensed"),(15,2,"Extra Cond"),(15,2,"Ext Cond"),
            (15,4,"Narrow"),(15,4,"Compact"),(15,4,"Semi Condensed"),(15,4,"Semi Cond"),(15,6,"Wide"),(15,6,"Semi Expanded"),(15,6,"Semi Extended"),(15,8,"Extra Expanded"),(15,8,"Ext Expanded"),
            (15,8,"Extra Extended"),(15,8,"Ext Extended"),(15,9,"Ultra Expanded"),(15,9,"Ultra Extended"),(15,3,"Condensed"),(15,3,"Cond"),(15,7,"Expanded"),(15,7,"Extended"),
            (12,100,"Extra Thin"),(12,100,"Ext Thin"),(12,100,"Ultra Thin"),(12,200,"Extra Light"),(12,200,"Ext Light"),(12,200,"Ultra Light"),(12,350,"Semi Light"),(12,350,"Demi Light"),
            (12,600,"Semi Bold"),(12,600,"Demi Bold"),(12,800,"Extra Bold"),(12,800,"Ext Bold"),(12,800,"Ultra Bold"),(12,950,"Extra Black"),(12,950,"Ext Black"),(12,950,"Ultra Black"),
            (12,700,"Bold"),(12,100,"Thin"),(12,300,"Light"),(12,500,"Medium"),(12,900,"Black"),(12,900,"Heavy"),(12,900,"Nord"),(12,600,"Demi"),(12,800,"Ultra"),
            (12,200,"EL"),(12,800,"EB"),(12,350,"SL"),(12,600,"SB"),(12,700,"B"),(12,300,"L"),(12,500,"M"),(12,400,"R"),(12,900,"H"),(12,950,"UH"),(12,800,"U"),(12,0,"W#"),
            (12,100,"Extra Thin Face"),(12,100,"Ext Thin Face"),(12,100,"Ultra Thin Face"),(12,200,"Extra light Face"),(12,200,"Ext light Face"),(12,200,"Ultra Light Face"),
            (12,350,"Semi Light Face"),(12,350,"Demi Light Face"),(12,600,"Semi Bold Face"),(12,600,"Demi Bold Face"),(12,800,"Extra Bold Face"),(12,800,"Ext Bold Face"),
            (12,800,"Ultra Bold Face"),(12,950,"Extra Black Face"),(12,950,"Ext Black Face"),(12,950,"Ultra Black Face"),(12,700,"Bold Face"),(12,100,"Thin Face"),(12,300,"Light Face"),
            (12,500,"Medium Face"),(12,900,"Black Face"),(12,900,"Heavy Face"),(12,900,"Nord Face"),(12,600,"Demi Face"),(12,800,"Ultra Face"),(12,0,"Face"),
            (21,0,"Text"),(21,1,"Display"),(21,2,"Informal"),(21,3,"Symbol/Pictograph")
        };

        private const int NoIx = 0x6b;

        private static bool IsSep(char c) => c == ' ' || c == '-' || c == '.' || c == '_';
        private static bool Eq(char a, char b) => char.ToLowerInvariant(a) == char.ToLowerInvariant(b);
        private static bool IsDigit(char c) => c >= '0' && c <= '9';

        /// <summary>fms!FindSubstr (VA 0x180004268): searches backwards for a separator-delimited, case-insensitive
        ///  occurrence ('#' matches digits, a needle separator matches any separator run or nothing) and returns the span
        ///  a removal takes out (the word plus the separators after it, or before it when it ends the string).</summary>
        private static bool FindSubstr(string hay, string needle, out int start, out int end)
        {
            int n = hay.Length, m = needle.Length;
            start = 0; end = n;
            if (n == 0 || m == 0) return false;
            int state = 1, p5 = 0, p7 = n, pos = n, j = m;
            while (true)
            {
                int jj = j;
                if (pos == 0)
                {
                    if (state == 3 && jj == 0) { start = p5; end = p7; return true; }
                    return false;
                }

                int i = pos - 1; char c = hay[i];
                if (IsSep(c))
                {
                    if (state == 0)
                    {
                        p7 = pos;
                        while (i != 0 && IsSep(hay[i - 1])) i--;
                        state = 1; pos = i; j = m;
                    }
                    else if (state == 1)
                    {
                        if (jj != 0 && !IsSep(needle[j - 1])) { p7 = pos; j = m; }
                        while (i != 0 && IsSep(hay[i - 1])) i--;
                        pos = i;
                    }
                    else if (state == 3)
                    {
                        while (i != 0 && IsSep(hay[i - 1])) i--;
                        start = p7 != n ? i + 1 : i; end = p7;
                        return true;
                    }
                    else
                    {
                        return false;
                    }

                    continue;
                }

                pos = i;
                if (state == 0) continue;
                if (state == 2)
                {
                    if (IsDigit(c)) continue;
                    state = 1;
                }

                if (state == 1)
                {
                    if (jj != 0)
                    {
                        j = jj - 1;
                        if (Eq(c, needle[j]))
                        {
                            if (j == 0) { state = 3; p5 = i; }
                            continue;
                        }
                    }

                    if (IsSep(needle[j]))
                    {
                        if (j != 0)
                        {
                            j--;
                            if (Eq(c, needle[j]))
                            {
                                if (j != 0) continue;
                                state = 3; p5 = i; continue;
                            }
                        }
                        else
                        {
                            state = 3; p5 = i; continue;
                        }
                    }

                    if (j < m && needle[j] == '#')
                    {
                        if (IsDigit(c)) state = 2;
                        continue;
                    }

                    state = 0; continue;
                }

                if (state == 3) { state = 0; continue; }
                return false;
            }
        }

        /// <summary>FindAndRemoveInvariantWSSStr: the first term (in table order lo..hi) present in s.</summary>
        private static string FindRemoveTerm(string s, int lo, int hi, out int ix, out int pos, bool remove = true)
        {
            ix = NoIx; pos = -1;
            for (int k = lo; k <= hi; k++)
            {
                if (s.Length == 0) break;
                if (FindSubstr(s, s_terms[k].Text, out int a, out int b))
                {
                    ix = k; pos = a;
                    return remove ? s.Substring(0, a) + s.Substring(b) : s;
                }
            }

            return s;
        }

        private static int NormalizedWeight(int w)
        {
            if (w == 0) return 400;
            if (w < 151) return 100;
            if (w < 250) return 200;
            if (w < 350) return 300;
            if (w < 400) return 350;
            if (w < 450) return 400;
            if (w < 550) return 500;
            if (w < 650) return 600;
            if (w < 750) return 700;
            if (w < 850) return 800;
            if (w < 950) return 900;
            return 950;
        }

        /// <summary>GetInvariantNameIx: the term naming a style, stretch or weight value.</summary>
        private static int InvariantIx(int cat, int v)
        {
            switch (cat)
            {
                case 7: return v == 0 ? 1 : v == 1 ? 7 : v == 2 ? 11 : NoIx;
                case 15:
                    switch (v)
                    {
                        case 1: return 0x10; case 2: return 0x13; case 3: return 0x24; case 4: return 0x19; case 5: return 1;
                        case 6: return 0x1c; case 7: return 0x26; case 8: return 0x1e; case 9: return 0x22; default: return NoIx;
                    }
                case 12:
                    if (v / 10 == 35) return 0x2e;
                    int q = v / 100;
                    if (v % 100 > 49)
                    {
                        if (v > 925) return 0x35;
                        q++;
                    }

                    switch (q)
                    {
                        case 0: case 1: return 0x39; case 2: return 0x2b; case 3: return 0x3a; case 4: return 2; case 5: return 0x3b;
                        case 6: return 0x30; case 7: return 0x38; case 8: return 0x32; case 9: return 0x3c; default: return NoIx;
                    }
                default: return 2;
            }
        }

        // fms.dll string resources (101..127) as GetWwsPropertyString hands them out.
        private static string WwsWeight(int nw) => nw switch
        {
            100 => "Thin", 200 => "Extra Light", 300 => "Light", 350 => "Semi Light", 500 => "Medium", 600 => "Semi Bold",
            700 => "Bold", 800 => "Extra Bold", 900 => "Black", 950 => "Extra Black", _ => "Regular"
        };

        private static string WwsStyle(int s) => s == 1 ? "Italic" : s == 2 ? "Oblique" : "Normal";

        /// <summary>MakeFontName (VA 0x180015270): each part loses its first Book/Normal/Regular/Upright term; a part
        ///  that had none gets a trailing space; the last character is dropped at the end.</summary>
        private static string? MakeFontName(string?[] parts, string? fallback)
        {
            var sb = new StringBuilder();
            foreach (string? p in parts)
            {
                if (string.IsNullOrEmpty(p)) continue;
                string q = FindRemoveTerm(p.Length > 64 ? p.Substring(0, 64) : p, 0, 3, out int ix, out _);
                if (ix == NoIx)
                {
                    if (q.Length > 0) sb.Append(q).Append(' ');
                }
                else
                {
                    sb.Append(q);
                }
            }

            if (sb.Length > 0) return sb.ToString(0, sb.Length - 1);
            return fallback;
        }

        /// <summary>ResolveFontName (VA 0x1800154b0): moves weight/width/slope words out of a legacy family name.</summary>
        private static void ResolveFontName(ref string fam, ref string? face, Props props)
        {
            string fam0 = fam, face0 = face!;
            string facebuf = FindRemoveTerm(face0, 0, 4, out int regIx, out _);
            if (facebuf.Length > 0 && !FindSubstr(fam, facebuf, out _, out _) && fam.Length + facebuf.Length + 1 < 0x40)
                fam = fam + " " + facebuf;

            fam = FindRemoveTerm(fam, 5, 0xe, out int styleIx, out _);
            int stretchIx = NoIx;
            if (fam.Length > 0) fam = FindRemoveTerm(fam, 0xf, 0x27, out stretchIx, out _);
            int weightIx = NoIx, abbrPos = -1;
            if (fam.Length > 0)
            {
                FindRemoveTerm(fam, 0x28, 0x40, out int wix, out _, remove: false);
                if (wix != NoIx)
                {
                    string f2 = FindRemoveTerm(fam, wix + 0x25, wix + 0x25, out int ix2, out _);
                    if (ix2 != NoIx) { fam = f2; weightIx = ix2; }
                    else
                    {
                        f2 = FindRemoveTerm(fam, wix, wix, out ix2, out _);
                        if (ix2 != NoIx) { fam = f2; weightIx = ix2; }
                    }
                }
                else
                {
                    FindRemoveTerm(fam, 0x41, 0x4c, out int aix, out int apos, remove: false);
                    if (aix != NoIx) { weightIx = aix; abbrPos = apos; }
                }
            }

            int nameW = props.Weight;
            if (styleIx != NoIx || stretchIx != NoIx || weightIx != NoIx)
            {
                if (stretchIx != NoIx)
                {
                    int v = s_terms[stretchIx].Val;
                    if (v < 5) { if (props.Stretch > 4) props.Stretch = v; }
                    else if (v < 6 || props.Stretch < 6) props.Stretch = v;
                }

                if (styleIx != NoIx) props.Style = s_terms[styleIx].Val;
                if (weightIx != NoIx)
                {
                    nameW = s_terms[weightIx].Val;
                    if (weightIx >= 0x41 && weightIx <= 0x4c)
                    {
                        // An abbreviation (B, SB, W5...) counts only when it is the whole face name at the family's end.
                        if (abbrPos >= 0 && abbrPos < fam.Length && string.Equals(fam.Substring(abbrPos), facebuf, StringComparison.OrdinalIgnoreCase))
                        {
                            if (weightIx == 0x4c)
                            {
                                int k = abbrPos + 1, d = 0, nd = 0;
                                while (k < fam.Length && IsDigit(fam[k])) { d = d * 10 + (fam[k] - '0'); k++; nd++; }
                                if (nd > 0 && d < 10) nameW = d * 100;
                            }

                            if (Math.Abs(props.Weight - nameW) > 100) { weightIx = NoIx; nameW = props.Weight; }
                            else
                            {
                                string f2 = FindRemoveTerm(fam, weightIx, weightIx, out int ix2, out _);
                                if (ix2 != NoIx) fam = f2; else nameW = props.Weight;
                            }
                        }
                        else
                        {
                            weightIx = NoIx; nameW = props.Weight;
                        }
                    }
                }

                int w = props.Weight, u = nameW;
                if (w != u)
                {
                    bool? adopt = null;
                    if (u < 400)
                    {
                        if (w < 400) adopt = false;
                    }
                    else
                    {
                        if (u > 500)
                        {
                            if (w < 501) adopt = true;
                            else if (w != 700) adopt = false;
                        }

                        if (adopt is null && ((u == 500 && (w == 400 || w == 500)) || (u == 400 && (w == 400 || w == 500)))) adopt = false;
                    }

                    if (adopt != false)
                        adopt = !(w != 400 && w != 700 && Math.Abs(u - w) < 150);
                    if (adopt == true) props.Weight = u;
                }
            }

            bool changed = fam != fam0;
            bool rebuild = changed;
            string? outFace = facebuf;
            if (!changed)
            {
                if ((styleIx == NoIx && props.Style != 0) || (weightIx == NoIx && NormalizedWeight(props.Weight) != 400))
                    rebuild = true;
                else if (regIx != NoIx || (weightIx != NoIx && props.Weight != nameW))
                    outFace = face0;
            }

            bool useSt = stretchIx != NoIx, useW = weightIx != NoIx, useS = styleIx != NoIx;
            if (stretchIx == NoIx && props.Stretch != 5) { stretchIx = InvariantIx(15, props.Stretch); useSt = true; }
            if (weightIx == NoIx && NormalizedWeight(props.Weight) != 400) { weightIx = InvariantIx(12, props.Weight); useW = true; }
            if (styleIx == NoIx && props.Style != 0) { styleIx = InvariantIx(7, props.Style); useS = true; }
            if (rebuild)
            {
                string? reg = null;
                if (!(useSt || useW || useS)) reg = regIx != NoIx ? s_terms[regIx].Text : "Regular";
                string? nm = MakeFontName(new[]
                {
                    reg, useSt && stretchIx != NoIx ? s_terms[stretchIx].Text : null, useW && weightIx != NoIx ? s_terms[weightIx].Text : null,
                    useS && styleIx != NoIx ? s_terms[styleIx].Text : null
                }, null);
                outFace = nm ?? face0;
            }

            face = string.IsNullOrEmpty(outFace) ? "Regular" : outFace;
        }

        // ------------------------------------------------------------------ fms simulations (AddFamilySimulatedFonts)

        private sealed class Entry
        {
            public GdiFace Real = null!;
            public Entry? Base;
            public bool SimBold, SimOblique;
            public bool IsSim => Base is not null;
            public string FaceName = "";
            public int Weight, Stretch, Style;
            public int LfWeight;
            public bool LfItalic;
        }

        private const int BoldMask = 0x1bf00;

        private static int WeightStringIx(int w) => NormalizedWeight(w) switch
        {
            100 => 0, 200 => 1, 300 => 3, 350 => 4, 400 => 5, 500 => 7, 600 => 8, 700 => 10, 800 => 11, 900 => 13, _ => 15
        };

        private static string RemoveWeightForBold(string name)
        {
            foreach ((int lo, int hi) in new[] { (0x2b, 0x31), (0x38, 0x3b), (0x3f, 0x3f) })
            {
                string s = FindRemoveTerm(name, lo, hi, out int ix, out _);
                if (ix != NoIx) return s;
            }

            return name;
        }

        private static Entry SimEntry(Entry b, bool bold, bool oblique) => new Entry
        {
            Real = b.Real, Base = b, SimBold = bold, SimOblique = oblique,
            Weight = bold ? 700 : b.Weight, Stretch = b.Stretch, Style = oblique ? 2 : b.Style,
            LfWeight = bold ? 700 : b.LfWeight, LfItalic = oblique || b.LfItalic,
            FaceName = MakeFontName(new[] { bold ? RemoveWeightForBold(b.FaceName) : b.FaceName, null, bold ? "Bold" : null, oblique ? "Oblique" : null }, "Regular")!
        };

        /// <summary>Per stretch, a bitmask of the weights present upright and slanted; a regular-weight upright face
        ///  gets a bold, a bold-oblique and an oblique twin when no real face (or earlier twin) fills that slot, a
        ///  slanted regular-weight face gets a bold twin.</summary>
        private static List<Entry> AddSimulations(List<Entry> entries)
        {
            var up = new int[10]; var it = new int[10];
            foreach (Entry e in entries)
            {
                if (e.Stretch <= 0 || e.Stretch > 9) continue;
                int b = 1 << WeightStringIx(e.Weight);
                if (e.Style == 0) up[e.Stretch] |= b; else it[e.Stretch] |= b;
            }

            var sims = new List<Entry>();
            foreach (Entry e in entries)
            {
                int s = e.Stretch;
                if (s <= 0 || s > 9) continue;
                int b = 1 << WeightStringIx(e.Weight);
                if (e.Style == 0)
                {
                    if ((b & 0xa0) != 0)
                    {
                        if ((up[s] & BoldMask) == 0 && (b == 0x80 || (up[s] & 0x80) == 0)) { sims.Add(SimEntry(e, true, false)); up[s] |= 0x400; }
                        if ((it[s] & BoldMask) == 0 && (it[s] & 0xa0) == 0) { sims.Add(SimEntry(e, true, true)); it[s] |= 0x400; }
                    }

                    if ((b & it[s]) == 0) { sims.Add(SimEntry(e, false, true)); it[s] |= b; }
                }
                else if ((b & 0xa0) != 0 && (it[s] & BoldMask) == 0 && (b == 0x80 || (it[s] & 0x80) == 0))
                {
                    sims.Add(SimEntry(e, true, false)); it[s] |= 0x400;
                }
            }

            return sims;
        }

        /// <summary>comdlg32 InsertStyleSorted (VA 0x1800be8a8) over CB_FINDSTRINGEXACT: by GDI weight; an equal-weight
        ///  upright goes before the first face of that weight, an italic after it when that one is upright; a
        ///  simulated face only compares against simulated faces.</summary>
        private static List<Entry> DialogStyles(List<Entry> entries)
        {
            var items = new List<Entry>();
            foreach (Entry e in entries)
            {
                bool dup = false;
                foreach (Entry x in items)
                    dup |= string.Equals(x.FaceName, e.FaceName, StringComparison.OrdinalIgnoreCase);
                if (dup) continue;
                int i = 0;
                for (; i < items.Count; i++)
                {
                    Entry o = items[i];
                    if (e.IsSim && !o.IsSim) continue;
                    if (e.LfWeight < o.LfWeight) break;
                    if (e.LfWeight == o.LfWeight)
                    {
                        if (e.LfItalic && !o.LfItalic) i++;
                        break;
                    }
                }

                items.Insert(i, e);
            }

            return items;
        }

        /// <summary>FmsGetBestMatchInFamily with no request: the real face nearest upright, normal width, 400.</summary>
        private static Entry BestMatch(List<Entry> entries)
        {
            Entry? best = null;
            foreach (Entry e in entries)
            {
                if (e.IsSim) continue;
                if (best is null) { best = e; continue; }
                int c = (e.Style != 0).CompareTo(best.Style != 0);
                if (c == 0) c = Math.Abs(e.Stretch - 5).CompareTo(Math.Abs(best.Stretch - 5));
                if (c == 0) c = Math.Abs(e.Weight - 400).CompareTo(Math.Abs(best.Weight - 400));
                if (c == 0) c = e.Weight.CompareTo(best.Weight);
                if (c < 0) best = e;
            }

            return best ?? entries[0];
        }

        // ------------------------------------------------------------------ scripts

        // fontdrvhost vFillIFICharsets: the 'fs' signature bits and 'charsets' in enumeration order.
        private static readonly (uint Bit, byte CharSet)[] s_fsCharsets =
        {
            (0x1, 0), (0x20000, 128), (0x80000, 129), (0x200000, 130), (0x40000, 134), (0x100000, 136), (0x20, 177), (0x40, 178),
            (0x8, 161), (0x10, 162), (0x80, 186), (0x2, 238), (0x4, 204), (0x10000, 222), (0x100, 163), (0x80000000, 2)
        };

        private static (uint Bit, byte CharSet) AcpSignature(int acp) => acp switch
        {
            1250 => (0x2, 238), 1251 => (0x4, 204), 1253 => (0x8, 161), 1254 => (0x10, 162), 1255 => (0x20, 177), 1256 => (0x40, 178),
            1257 => (0x80, 186), 1258 => (0x100, 163), 874 => (0x10000, 222), 932 => (0x20000, 128), 936 => (0x40000, 134),
            949 => (0x80000, 129), 950 => (0x100000, 136), 1361 => (0x200000, 130), _ => (0x1, 0)
        };

        /// <summary>The charsets GDI enumerates for a face: the ANSI code page's first when the font covers it (not for
        ///  Hebrew/Arabic/Thai), then the font's code-page bits in fontdrvhost's table order.</summary>
        private static List<byte> FaceCharsets(Sfnt f, int acp)
        {
            var list = new List<byte>();
            if (f.Os2 is null || f.Os2.Version == 0 || f.Os2.CodePage1 == 0)
            {
                list.Add(f.SymbolCmap ? (byte)2 : (byte)0);
                return list;
            }

            uint cp1 = f.Os2.CodePage1;
            (uint sig, byte cur) = AcpSignature(acp);
            if ((cp1 & sig) != 0 && (sig & 0x10060) == 0) list.Add(cur);
            foreach ((uint bit, byte cs) in s_fsCharsets)
            {
                if ((bit != sig || (sig & 0x10060) != 0) && (bit & cp1) != 0 && list.Count < 16)
                    list.Add(cs);
            }

            return list;
        }

        // comdlg32: name = string 0x950 + index of the charset in the table at VA 0x180168d70; sample = string 0x700 + charset.
        private static string ScriptName(byte cs) => cs switch
        {
            0 => "Western", 186 => "Baltic", 136 => "Chinese Big5", 134 => "Chinese GB2312", 238 => "Central European", 161 => "Greek",
            129 => "Hangul", 130 => "Hangul(Johab)", 204 => "Cyrillic", 128 => "Japanese", 162 => "Turkish", 163 => "Vietnamese",
            178 => "Arabic", 177 => "Hebrew", 222 => "Thai", 2 => "Symbol", 77 => "Mac", 255 => "OEM/DOS", _ => "Other"
        };

        private static string ScriptSample(byte cs) => cs switch
        {
            2 => "Symbol",
            128 => "Aaあぁアァ亜宇",
            129 or 130 => "가나다AaBbYyZz",
            134 => "微软中文软件",
            136 => "中文字型範例",
            161 => "AaBbΑαΒβ",
            162 => "AaBbĞğŞş",
            163 => "AaBbƠơƯư",
            177 => "AaBbנסשת",
            178 => "AaBbابجدهوز",
            204 => "AaBbБбФф",
            222 => "AaBbอักษรไทย",
            238 => "AaBbÁáÔô",
            255 => "AaBbøñý",
            _ => "AaBbYyZz"
        };

        // ------------------------------------------------------------------ family combo order

        /// <summary>The family combo is CBS_SORT: CompareString word sort, ignoring case (hyphen and apostrophe
        ///  ignored; punctuation before digits before letters).</summary>
        internal static int CompareFamilyNames(string a, string b)
        {
            List<(int, char)> ka = SortKey(a), kb = SortKey(b);
            for (int i = 0; i < Math.Min(ka.Count, kb.Count); i++)
            {
                int c = ka[i].Item1.CompareTo(kb[i].Item1);
                if (c == 0) c = ka[i].Item2.CompareTo(kb[i].Item2);
                if (c != 0) return c;
            }

            int r = ka.Count.CompareTo(kb.Count);
            if (r == 0) r = string.CompareOrdinal(a.ToLowerInvariant(), b.ToLowerInvariant());
            return r != 0 ? r : string.CompareOrdinal(a, b);
        }

        private static List<(int, char)> SortKey(string s)
        {
            var k = new List<(int, char)>();
            foreach (char ch in s)
            {
                if (ch == '-' || ch == '\'' || ch == '’') continue;
                string d = ch.ToString().Normalize(NormalizationForm.FormD);
                char c = d.Length > 0 ? d[0] : ch;
                if (char.IsLetter(c)) k.Add((3, char.ToLowerInvariant(c)));
                else if (char.IsDigit(c)) k.Add((2, c));
                else k.Add((1, c));
            }

            return k;
        }

        // ------------------------------------------------------------------ OpenType reader

        private sealed class Os2Table
        {
            public int Version, Weight, Width, FsSelection;
            public uint CodePage1, CodePage2;
        }

        private sealed class FvarInstance
        {
            public int SubfamilyNameId;
            public Dictionary<string, double> Coords = new();
        }

        private sealed class FvarTable
        {
            public List<FvarInstance> Instances = new();
        }

        private sealed class StatValue
        {
            public int Format, AxisIndex, Flags, NameId;
            public double Value, RangeMin, RangeMax;
        }

        private sealed class StatTable
        {
            public List<(string Tag, int Ordering)> Axes = new();
            public List<StatValue> Values = new();
            public int ElidedFallbackNameId = 2;
        }

        private sealed class Sfnt
        {
            public string Path = "";
            public int Index;
            public Os2Table? Os2;
            public int MacStyle;
            public bool SymbolCmap;
            public FvarTable? Fvar;
            public StatTable? Stat;
            public List<(int Pid, int Eid, int Lid, int Nid, string Text)> Names = new();

            private static int U16(byte[] b, int o) => (b[o] << 8) | b[o + 1];
            private static uint U32(byte[] b, int o) => (uint)((b[o] << 24) | (b[o + 1] << 16) | (b[o + 2] << 8) | b[o + 3]);
            private static double Fixed(byte[] b, int o) => (int)U32(b, o) / 65536.0;

            private static byte[] ReadAt(FileStream fs, long off, int len)
            {
                if (off < 0 || len < 0 || off + len > fs.Length) throw new InvalidDataException();
                var b = new byte[len];
                fs.Position = off;
                int got = 0;
                while (got < len)
                {
                    int r = fs.Read(b, got, len - got);
                    if (r <= 0) throw new EndOfStreamException();
                    got += r;
                }

                return b;
            }

            /// <summary>Reads only the table directory and the tables the model needs.</summary>
            public static List<Sfnt> ReadFile(string path)
            {
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.RandomAccess);
                byte[] hdr = ReadAt(fs, 0, 12);
                var offsets = new List<long>();
                if (hdr[0] == 't' && hdr[1] == 't' && hdr[2] == 'c' && hdr[3] == 'f')
                {
                    uint n = U32(hdr, 8);
                    byte[] dir = ReadAt(fs, 12, (int)(4 * n));
                    for (int i = 0; i < n; i++) offsets.Add(U32(dir, 4 * i));
                }
                else
                {
                    offsets.Add(0);
                }

                var list = new List<Sfnt>();
                for (int idx = 0; idx < offsets.Count; idx++)
                {
                    long off = offsets[idx];
                    int nt = U16(ReadAt(fs, off, 12), 4);
                    byte[] dir = ReadAt(fs, off + 12, 16 * nt);
                    var tabs = new Dictionary<string, byte[]>(StringComparer.Ordinal);
                    for (int i = 0; i < nt; i++)
                    {
                        string tag = Encoding.ASCII.GetString(dir, 16 * i, 4);
                        if (tag is "name" or "OS/2" or "head" or "cmap" or "fvar" or "STAT")
                        {
                            uint toff = U32(dir, 16 * i + 8), tlen = U32(dir, 16 * i + 12);
                            if (tag == "cmap") tlen = Math.Min(tlen, 4 + 8 * 64u);
                            tabs[tag] = ReadAt(fs, toff, (int)tlen);
                        }
                    }

                    var f = new Sfnt { Path = path, Index = idx };
                    if (tabs.TryGetValue("name", out byte[]? nm)) f.ParseName(nm, 0);
                    if (tabs.TryGetValue("OS/2", out byte[]? os2)) f.ParseOs2(os2, 0, os2.Length);
                    if (tabs.TryGetValue("head", out byte[]? head) && head.Length >= 46) f.MacStyle = U16(head, 44);
                    if (tabs.TryGetValue("cmap", out byte[]? cmap) && cmap.Length >= 4)
                    {
                        int n = Math.Min(U16(cmap, 2), (cmap.Length - 4) / 8);
                        for (int i = 0; i < n; i++)
                            f.SymbolCmap |= U16(cmap, 4 + 8 * i) == 3 && U16(cmap, 6 + 8 * i) == 0;
                    }

                    if (tabs.TryGetValue("fvar", out byte[]? fvar))
                    {
                        f.ParseFvar(fvar, 0);
                        if (tabs.TryGetValue("STAT", out byte[]? stat)) f.ParseStat(stat, 0);
                    }

                    list.Add(f);
                }

                return list;
            }

            private void ParseName(byte[] d, int o)
            {
                int count = U16(d, o + 2), so = o + U16(d, o + 4);
                for (int i = 0; i < count; i++)
                {
                    int r = o + 6 + 12 * i;
                    int pid = U16(d, r), eid = U16(d, r + 2), lid = U16(d, r + 4), nid = U16(d, r + 6), len = U16(d, r + 8), off = U16(d, r + 10);
                    if (so + off + len > d.Length) continue;
                    string text = pid == 3 || pid == 0 ? Encoding.BigEndianUnicode.GetString(d, so + off, len & ~1) : MacRoman(d, so + off, len);
                    Names.Add((pid, eid, lid, nid, text));
                }
            }

            private void ParseOs2(byte[] d, int o, int len)
            {
                if (len < 64) return;
                var t = new Os2Table { Version = U16(d, o), Weight = U16(d, o + 4), Width = U16(d, o + 6), FsSelection = U16(d, o + 62) };
                if (t.Version >= 1 && len >= 86) { t.CodePage1 = U32(d, o + 78); t.CodePage2 = U32(d, o + 82); }
                Os2 = t;
            }

            private void ParseFvar(byte[] d, int o)
            {
                int axOff = U16(d, o + 4), axCount = U16(d, o + 8), axSize = U16(d, o + 10), iCount = U16(d, o + 12), iSize = U16(d, o + 14);
                var tags = new string[axCount];
                for (int i = 0; i < axCount; i++) tags[i] = Encoding.ASCII.GetString(d, o + axOff + i * axSize, 4);
                var t = new FvarTable();
                for (int i = 0; i < iCount; i++)
                {
                    int a = o + axOff + axCount * axSize + i * iSize;
                    var inst = new FvarInstance { SubfamilyNameId = U16(d, a) };
                    for (int k = 0; k < axCount; k++) inst.Coords[tags[k]] = Fixed(d, a + 4 + 4 * k);
                    t.Instances.Add(inst);
                }

                Fvar = t;
            }

            private void ParseStat(byte[] d, int o)
            {
                int minor = U16(d, o + 2), aSize = U16(d, o + 4), aCount = U16(d, o + 6), vCount = U16(d, o + 12);
                int aOff = (int)U32(d, o + 8), vOff = (int)U32(d, o + 14);
                var t = new StatTable { ElidedFallbackNameId = minor >= 1 ? U16(d, o + 18) : 2 };
                for (int i = 0; i < aCount; i++)
                {
                    int a = o + aOff + i * aSize;
                    t.Axes.Add((Encoding.ASCII.GetString(d, a, 4), U16(d, a + 6)));
                }

                int vb = o + vOff;
                for (int i = 0; i < vCount; i++)
                {
                    int v = vb + U16(d, vb + 2 * i);
                    int fmt = U16(d, v);
                    if (fmt < 1 || fmt > 3) continue;
                    var sv = new StatValue { Format = fmt, AxisIndex = U16(d, v + 2), Flags = U16(d, v + 4), NameId = U16(d, v + 6), Value = Fixed(d, v + 8) };
                    if (fmt == 2) { sv.RangeMin = Fixed(d, v + 12); sv.RangeMax = Fixed(d, v + 16); }
                    t.Values.Add(sv);
                }

                Stat = t;
            }

            /// <summary>A Windows-platform name in the preferred language, else en-US, else any English, else the
            ///  first; else the Macintosh English one.</summary>
            public string? NameByLang(int nid, int lang)
            {
                string? english = null, first = null;
                foreach (var r in Names)
                {
                    if (r.Nid != nid || r.Pid != 3 || r.Eid > 1) continue;
                    if (r.Lid == lang) return r.Text;
                    if (r.Lid == 0x409 && english is null) english = r.Text;
                    first ??= r.Text;
                }

                if (english is not null) return english;
                foreach (var r in Names)
                {
                    if (r.Nid == nid && r.Pid == 3 && r.Eid <= 1 && (r.Lid & 0x3ff) == 0x09) return r.Text;
                }

                if (first is not null) return first;
                foreach (var r in Names)
                {
                    if (r.Nid == nid && r.Pid == 1 && r.Lid == 0) return r.Text;
                }

                return null;
            }

            /// <summary>GetNameRecordsFromNameTable with GetFontNameTables' fallback: exact language, primary language,
            ///  any language, then Macintosh. The first family/face record found pins the language; later records of
            ///  that language overwrite earlier ones.</summary>
            public Dictionary<int, string> FmsNameRecords(int lang)
            {
                foreach ((int platform, int l0, int m0) in new[] { (3, lang, 0xffff), (3, lang, 0x3ff), (3, lang, 0), (1, 0, 0xffff) })
                {
                    var got = new Dictionary<int, string>();
                    int L = l0, M = m0;
                    foreach (var r in Names)
                    {
                        if (r.Pid != platform || r.Eid >= 2) continue;
                        if ((L & M) != (r.Lid & M)) continue;
                        switch (r.Nid)
                        {
                            case 1: case 2: case 4: case 16: case 17: case 21: case 22:
                                got[r.Nid] = r.Text; M = 0xffff; L = r.Lid; break;
                            case 3: case 5: case 8:
                                got[r.Nid] = r.Text; break;
                        }
                    }

                    if (got.Count > 0) return got;
                }

                return new Dictionary<int, string>();
            }

            private static readonly string s_macRomanHigh =
                "ÄÅÇÉÑÖÜáàâäãåçéè" +
                "êëíìîïñóòôöõúùûü" +
                "†°¢£§•¶ß®©™´¨≠ÆØ" +
                "∞±≤≥¥µ∂∑∏π∫ªºΩæø" +
                "¿¡¬√ƒ≈∆«»… ÀÃÕŒœ" +
                "–—“”‘’÷◊ÿŸ⁄€‹›ﬁﬂ" +
                "‡·‚„‰ÂÊÁËÈÍÎÏÌÓÔ" +
                "ÒÚÛÙıˆ˜¯˘˙˚¸˝˛ˇ";

            private static string MacRoman(byte[] d, int o, int len)
            {
                var sb = new StringBuilder(len);
                for (int i = 0; i < len; i++) sb.Append(d[o + i] < 0x80 ? (char)d[o + i] : s_macRomanHigh[d[o + i] - 0x80]);
                return sb.ToString();
            }
        }
    }
}
