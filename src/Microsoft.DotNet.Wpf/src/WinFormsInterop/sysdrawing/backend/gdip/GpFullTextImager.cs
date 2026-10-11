// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// GDI+'s full text imager (FullTextImager, gdiplus.dll 10.0.26100 arm64) -- the path every string
// takes that the fast imager refuses, and every string drawn into a metafile or a path. One
// implementation for the screen (GpGraphics), the metafile recorder (DriverMeta through
// GpMetaText.cs) and GraphicsPath.AddString; each is a target the imager hands its glyph runs and
// lines to (IGpTextTarget).
//
//   FullTextImager::FullTextImager @180039fa0   the string copied; with a hotkey prefix every '&'
//       becomes U+FFFF (an escaped "&&" keeps one), and under Show the position of the marked
//       character is kept for its underline. r = 2048 / em ideal units per world unit (+0x34); the
//       default tab increment 4 em (+0x288).
//   BuildRunsUpToAndIncluding @1800f00f0, ItemizationFiniteStateMachine @18003ce90, CreateTextRuns
//       @18003aaf8   items (script runs) from CharacterAttributes; each item shaped (GetGlyphs) into
//       one run, and the string closed by a two-cp end-of-paragraph run.
//   BuildAllLines @1800efc18 / BuiltLine::BuiltLine @1800f3670 / CreateLine @1800f3b40   a line
//       per LsCreateLine; the room is round(extent * r) less both margins (round(margin * em * r));
//       lines are added while they fit the height (LineLimit: wholly; trimming: a quarter of one;
//       otherwise all of them); the last line rebuilt trimmed when text remains.
//   Line Services (the subset GDI+ drives, GpLineServices below): a line formatted from runs with
//       nominal character widths (GdipLscbkGetRunCharWidths, SimpleGetRunCharWidths @18003e0c8),
//       turned into glyph advances (ApplyNominalToIdeal @180120bc0 -> GdipLscbkGetGlyphPositions),
//       broken at the last opportunity before the margin (TruncateCore @180115460, FindPrevBreakText
//       @18011b8b0 through LineBreakBehavior), else forced at a character (ForceBreakCore).
//   Render @18003c900 / RenderLine @18003cbe8 / LogicalToXY @18003d548   each line at its baseline,
//       the line alignment applied in LogicalToXY, the paragraph alignment in BuiltLine (+0x54).
//   GdipLscbkDrawGlyphs @1800f8590 -> FullTextImager::DrawGlyphs ($$h @18023f4d8) -> GlyphImager
//       (GpGlyphImager.cs)   each run's glyphs on the device.
//   GdipLscbkDrawUnderline @1800f8940   underline and strikeout as lines with a GetDevicePenWidth pen.
//   FullTextImager::Measure @1800f17e0 / GpGraphics::MeasureString @180077318   the size.
//

using System.Collections.Generic;
using Microsoft.Wpf.Interop.WebGpu.Composition.Text;

namespace System.Drawing.WebGpuBackend.Gdip
{
    /// <summary>What the full imager draws into.</summary>
    internal interface IGpTextTarget
    {
        /// <summary>World to device (null: there is no device -- a path).</summary>
        GpMatrix? WorldToDevice { get; }
        /// <summary>The render mode the target's realization of this face takes (GpFaceRealization +0x1c).</summary>
        int RealizationMode (TrueTypeFont face, string family, float emDevice, bool square);
        /// <summary>The same with the world em too: Realize @1800a22a0 asks IsGrayscaleFontSize about
        /// (int) (em + 0.5) of the WORLD em and GetEmbeddedBitmapCount about the device one.</summary>
        int RealizationMode (TrueTypeFont face, string family, float em, float emDevice, bool square)
            => RealizationMode (face, family, emDevice, square);
        /// <summary>GpGraphics::DrawPlacedGlyphs: the glyphs at their device origins.</summary>
        void DrawPlacedGlyphs (GpFullTextImager.Run run, int mode, ushort[] glyphs, PointF[] deviceOrigins,
                               string chars, ushort[] map, int flags);
        /// <summary>GpGraphics::DrawLines with a pen of <paramref name="devicePenWidth"/> pixels (unit pixel).</summary>
        void DrawLine (float devicePenWidth, PointF a, PointF b);
        /// <summary>The layout rectangle into the clip for the time the imager draws (FullTextImager::Draw).</summary>
        object PushClip (RectangleF layout);
        void PopClip (object saved);
        /// <summary>FullTextImager::DrawGlyphs with no device (AddToPath): the run's glyph outlines
        /// at their world origins, a marker before each cluster and after the run.</summary>
        void AddGlyphs (GpFullTextImager.Run run, ushort[] glyphs, ushort[] glyphProps, PointF[] origins) { }
        /// <summary>GdipLscbkDrawUnderline with no device: the line as a rectangle (GpPath::AddRects).</summary>
        void AddRect (RectangleF r) { }
        /// <summary>FullTextImager::DrawGlyphs' SwitchToPath: the target's realization of the face
        /// under its transform is drawn as outlines, not glyph bitmaps.</summary>
        bool DrawsAsPath (TrueTypeFont face, float em, int mode, bool sideways = false) => false;
        /// <summary>That path: the glyphs' outlines at their world origins, filled (GpGraphics::FillPath).</summary>
        void FillGlyphOutlines (GpFullTextImager.Run run, ushort[] glyphs, PointF[] worldOrigins) { }
    }

    internal sealed partial class GpFullTextImager
    {
        public const int Ideal = 2048;

        // ---- the imager's inputs ---------------------------------------------------------------------
        internal readonly string Text;           // +0x18, '&' as U+FFFF under a hotkey prefix
        readonly int _n;                          // +0x20
        readonly float _width, _height;           // +0x24 / +0x28
        readonly float _along, _across;           // +0x2c / +0x30: the extents along and across the lines
        internal readonly float R;                // +0x34
        internal readonly float Em;               // +0x98
        internal readonly GpTextFormat Format;    // +0xb8 (null: no format)
        internal readonly TrueTypeFont Face;
        internal readonly string Family;
        internal readonly int Style;              // +0x78 (FontStyle bits)
        readonly int _defaultTab;                 // +0x288
        readonly List<int> _hotkeys = new List<int> ();   // +0x2a8
        internal readonly FaceMetrics Metrics;
        bool _wide;                               // +0x280 = BreakClassFromCharClassWide

        /// <summary>The face as GDI+'s GpFontFace holds it: +0x70 upem, +0x72 cell ascent, +0x74
        /// cell descent, +0x76 the line gap (line spacing less both), +0x7c/+0x7e underline
        /// position/thickness, +0x80/+0x82 strikeout position/size.</summary>
        internal readonly struct FaceMetrics
        {
            public readonly int Upem, Ascent, Descent, Gap, UlPos, UlThick, StPos, StSize;
            public FaceMetrics (int upem, int a, int d, int gap, int ulp, int ult, int stp, int sts)
            { Upem = upem; Ascent = a; Descent = d; Gap = gap; UlPos = ulp; UlThick = ult; StPos = stp; StSize = sts; }
        }

        public bool Valid { get; }

        public GpFullTextImager (string s, float width, float height, string family, int style, float emWorld, GpTextFormat format)
        {
            Family = family;
            Style = style;
            Em = emWorld;
            Format = format;
            _width = width; _height = height;
            if (format != null && format.IsVertical) { _along = height; _across = width; }
            else { _along = width; _across = height; }
            R = (float) (2048.0 / emWorld);
            _defaultTab = Rnd (emWorld * 4f * R);
            Face = Microsoft.Wpf.Interop.WebGpu.Composition.Text.GdiPlusText.Face (family, style & 3);
            GpFontFamily.Metrics? mm = GpFontFamily.Get (family, (FontStyle) (style & 3));
            if (Face == null || mm == null || string.IsNullOrEmpty (s)) { Text = s ?? ""; return; }
            GpFontFamily.Metrics m = mm.Value;
            int stPos = Face.StrikeoutPosition, stSize = Face.StrikeoutSize;
            Metrics = new FaceMetrics (m.Em, m.Ascent, m.Descent, m.LineSpacing - m.Ascent - m.Descent,
                                       Face.UnderlinePosition, Face.UnderlineThickness, stPos, stSize);
            var chars = s.ToCharArray ();
            int hk = format?.Hotkey ?? 0;
            if (hk != 0)
                for (int i = 0; i < chars.Length; i++) {
                    if (chars [i] != '&') continue;
                    chars [i] = '￿';
                    int next = i + 1;
                    if (hk == 1 && next < chars.Length && chars [next] != '&')
                        _hotkeys.Add (i);
                    i = next;     // "&&": the second one stays a character
                }
            Text = new string (chars);
            _n = Text.Length;
            Valid = true;
        }

        static int Rnd (float v) => (int) MathF.Floor (v + 0.5f);

        internal int FormatFlags => Format?.Flags ?? 0;
        internal bool IsVertical => Format != null && Format.IsVertical;
        internal bool IsRightToLeft => Format != null && Format.IsRightToLeft;
        /// <summary>FullTextImager::GetParagraphEmbeddingLevel: 1 for a right-to-left horizontal format.</summary>
        internal int ParagraphLevel => Format != null && Format.IsRightToLeft && !Format.IsVertical ? 1 : 0;
        /// <summary>The format's tracking (+0x50), 1.03 without a format.</summary>
        internal float Tracking => Format?.Tracking ?? 1.03f;
        internal bool Typographic => Format != null && Format.LeadMargin == 0f;

        // ---- runs ----------------------------------------------------------------------------------------

        /// <summary>An lsrun: a text run (an item shaped with one face) or the end of the paragraph.</summary>
        internal sealed class Run
        {
            public int Kind;                 // 0 text, 1 end of paragraph
            public int Cp, Len;              // Line Services cps
            public int Str;                  // the string position of Cp
            public int Script, ItemFlags, Level;
            public TrueTypeFont Face;
            public string Family;
            public int Style;
            public float Em, BaseOffset;     // +0x20 / +0x24
            public GpTextShaper.Shaped Shape;
            public int Underline;            // lschp: 1 underline, 2 strikeout
            public string EllipsisText;      // the ellipsis' own characters (it is not in the string)
            public bool Rtl => (Level & 1) != 0;
        }

        readonly List<Run> _runs = new List<Run> ();
        bool _runsBuilt;

        /// <summary>A GpTextItem: a span of the string with its script, flags (byte 1) and bidi level.</summary>
        struct Item
        {
            public int Start, Len, Script, Flags, Level;
            public Item (int start, int len, int script, int flags, int level) { Start = start; Len = len; Script = script; Flags = flags; Level = level; }
        }

        void BuildRuns ()
        {
            if (_runsBuilt) return;
            _runsBuilt = true;
            byte[] levels = BidiLevels ();
            var items = new List<Item> ();
            foreach ((int start, int len, int script, int flags, int level) in Itemize ()) {
                if (levels == null) { items.Add (new Item (start, len, script, flags, level)); continue; }
                // BidirectionalAnalysis: an item is cut where its characters' levels change.
                for (int s = start; s < start + len;) {
                    int e = s + 1;
                    while (e < start + len && levels [e] == levels [s]) e++;
                    items.Add (new Item (s, e - s, script, flags, levels [s]));
                    s = e;
                }
            }
            // BuildRunsUpToAndIncluding: MirroredNumericAndVerticalAnalysis for a bidi string, a
            // vertical format, or digits the format substitutes.
            int digitScript = Format != null ? GpTextTables.DigitSubstitutionsScript (Format.DigitMethod, Format.DigitLanguage) : 0;
            bool digits = false;
            for (int i = 0; i < _n && !digits; i++) digits = (GpTextTables.Flags (Text [i]) & 0x100) != 0;
            if (levels != null || (digits && digitScript != 0) || IsVertical)
                SecondaryItemization (items, digitScript);
            foreach (Item it in items) CreateTextRuns (it.Start, it.Len, it.Script, it.Flags, it.Level);
            _runs.Add (new Run { Kind = 1, Cp = _n, Len = 2, Str = _n, Face = Face, Family = Family, Style = Style, Em = Em, Level = ParagraphLevel });
        }

        /// <summary>SecondaryItemization @1800f2388: a state machine over the characters' secondary
        /// classes (masked 0xc, 0xf when the format substitutes digits, | 0x30 for a vertical format)
        /// that gives a run of digits (with its separators, sign and currency) the digit script,
        /// a run of mirrored brackets in a right-to-left item script 0x41, and a vertical line's
        /// CJK runs the upright flag (8; 0x10 in a right-to-left item).</summary>
        void SecondaryItemization (List<Item> items, int digitScript)
        {
            int mask = digitScript == 0 ? 0xc : 0xf;
            if (IsVertical) mask |= 0x30;
            int state = 0, mark = -1, start = 0;
            int strongClass = 0x14, strongChar = 0;
            for (int i = 0; i <= _n;) {
                bool pair = false;
                int col = 0;
                if (i < _n) {
                    int cp = Text [i];
                    if (char.IsHighSurrogate ((char) cp) && i + 1 < _n && char.IsLowSurrogate (Text [i + 1])) {
                        cp = char.ConvertToUtf32 ((char) cp, Text [i + 1]);
                        pair = true;
                    }
                    col = GpTextTables.SecondaryColumn (cp, mask);
                    if (digitScript == 0x40) {
                        // The contextual Arabic digits remember the last strong character.
                        int dc = GpTextTables.DirClass (cp);
                        if ((dc & ~5) == 0 && dc != 5) { strongClass = dc; strongChar = cp; }
                    }
                }
                int act = GpTextTables.SecondaryAct (col, state);
                state = GpTextTables.SecondaryNextState (col, state);
                int end = -1, script = 0, flags = 0;
                switch (act) {
                case 1: mark = i; break;
                case 2: start = i; break;
                case 3: start = mark; break;
                case 4: end = i; script = digitScript; break;
                case 5: end = mark; script = digitScript; break;
                case 6: end = i; script = 0x41; break;
                case 7: end = i; flags = 0x10; break;
                case 8: end = i; flags = 0x8; break;
                }
                if (end > 0) {
                    if (start < end) SetSpan (items, start, end, script, flags, strongClass, strongChar);
                    if (start < end) start = end;
                }
                i += pair ? 2 : 1;
            }
        }

        /// <summary>SpanVector&lt;GpTextItem&gt;::SetSpan over [from, to): each item there takes the
        /// script (unless 0) and the flags. A mirrored-bracket script and the 0x10 flag hold only in
        /// a right-to-left item; the contextual digit script 0x40 is Arabic-Indic (0x2d) after no
        /// strong character in a right-to-left paragraph, in an Arabic item or after an RLM, and
        /// otherwise leaves the script (the item is still cut).</summary>
        void SetSpan (List<Item> items, int from, int to, int script, int flags, int strongClass, int strongChar)
        {
            for (int k = 0; k < items.Count; k++) {
                Item it = items [k];
                int a = Math.Max (from, it.Start), b = Math.Min (to, it.Start + it.Len);
                if (a >= b) continue;
                int s = script, f = flags;
                if ((it.Level & 1) == 0) { f &= ~0x10; if (s == 0x41) s = 0; }
                if (s == 0 && f == 0) continue;
                var piece = it;
                if (s == 0x40) {
                    if ((strongClass == 0x14 && ParagraphLevel == 1) || it.Script == 7 || strongChar == 0x200f) piece.Script = 0x2d;
                } else if (s != 0) piece.Script = s;
                piece.Flags |= f;
                piece.Start = a; piece.Len = b - a;
                // Split the item around the piece.
                items.RemoveAt (k);
                int ins = k;
                if (it.Start < a) { var head = it; head.Len = a - it.Start; items.Insert (ins++, head); }
                items.Insert (ins++, piece);
                if (b < it.Start + it.Len) { var tail = it; tail.Start = b; tail.Len = it.Start + it.Len - b; items.Insert (ins++, tail); }
                k = ins - 1;
            }
        }

        /// <summary>BuildRunsUpToAndIncluding: the bidi analysis (UnicodeBidiAnalyze, GpBidi) when the
        /// paragraph is right to left or the text holds right-to-left characters; each character's
        /// embedding level, or null for a left-to-right paragraph of left-to-right text.</summary>
        byte[] BidiLevels ()
        {
            bool any = ParagraphLevel == 1;
            // BuildRunsUpToAndIncluding @1800f00f0: only for a right-to-left paragraph or when the
            // itemizer's OR of every character's CharacterAttributes flags has 0x200 (the
            // right-to-left scripts); a mark or an embedding code alone runs no bidi analysis.
            for (int i = 0; i < _n && !any; i++) {
                int cp = Text [i];
                if (char.IsHighSurrogate ((char) cp) && i + 1 < _n && char.IsLowSurrogate (Text [i + 1])) cp = char.ConvertToUtf32 ((char) cp, Text [++i]);
                any = (GpTextTables.Flags (cp) & 0x200) != 0;
            }
            if (!any || _n == 0) return null;
            var flags = ParagraphLevel == 1 ? GpBidi.Flags.DirectionRightToLeft : 0;
            if (!GpBidi.Analyze (Text.ToCharArray (), _n, _n, flags, null, out byte[] levels, out _)) return null;
            for (int i = 0; i < levels.Length; i++)
                if (levels [i] == 0xff) levels [i] = (byte) ParagraphLevel;
            return levels;
        }

        /// <summary>ItemizationFiniteStateMachine: the string cut where the script changes. A
        /// neutral (state class 1) takes the script it sits in; a control character (script 0x2c)
        /// is an item of its own kind.</summary>
        IEnumerable<(int, int, int, int, int)> Itemize ()
        {
            // ItemizationFiniteStateMachine @18003ce90. The item is GDI+'s 32-bit GpTextItem head:
            // byte 0 the script, bits 8.. the flags (0x100 state 3, 0x200 a joiner, 0x400 state 2,
            // 0x80000 an item that continues a ZWJ).
            int level = ParagraphLevel;
            var result = new List<(int, int, int, int, int)> ();
            int item = GpTextTables.ScriptLatin;
            int start = 0, i = 0, complexAt = -1;
            int Cp (int k)
            {
                int c = Text [k];
                if (char.IsHighSurrogate ((char) c) && k + 1 < _n && char.IsLowSurrogate (Text [k + 1])) return char.ConvertToUtf32 ((char) c, Text [k + 1]);
                if (char.IsLowSurrogate ((char) c) && k > 0 && char.IsHighSurrogate (Text [k - 1])) return char.ConvertToUtf32 (Text [k - 1], (char) c);
                return c;
            }
            void Emit (int s, int e) { if (e > s) result.Add ((s, e - s, (sbyte) (item & 0xff), item & ~0xff, level)); }
            // The simple pass: scripts change only at state-0 characters; a complex character
            // (state 3 or more) hands the rest to the full pass.
            for (; i < _n; i++) {
                uint attr = GpTextTables.Attributes (Cp (i));
                int st = (int) ((attr >> 8) & 0xff), sc = (int) (attr & 0xff);
                if (st == 0) {
                    if ((item & 0xff) != sc) {
                        if (i > 0) { Emit (start, i); start = i; }
                        item = (item & ~0xffff) | sc;
                    }
                } else if (st == 2) item |= 0x400;
                else if (st != 1) { complexAt = i; break; }
            }
            if (complexAt >= 0) {
                int split = 0, joiner = -1, zwj = -1, neutral = -1;
                if (complexAt >= 1 && ((GpTextTables.Attributes (Cp (complexAt - 1)) >> 8) & 0xff) == 1) neutral = complexAt - 1;
                for (i = complexAt; i < _n; i++) {
                    int cp = Cp (i);
                    uint attr = GpTextTables.Attributes (cp);
                    int st = (int) ((attr >> 8) & 0xff), sc = (sbyte) (attr & 0xff);
                    int cur = (sbyte) (item & 0xff);
                    if (cur == GpTextTables.ScriptControl && sc != GpTextTables.ScriptControl) split = i;
                    else switch (st) {
                    case 0: if (cur != sc) split = i; break;
                    case 1: neutral = i; break;
                    case 2: item |= 0x400; break;
                    case 3: item |= 0x100; break;
                    case 4:
                        if (sc != cur && !(cur == 8 && sc == 7)) {
                            if (joiner != i - 1 || neutral != i - 2) { split = i; break; }
                            split = neutral;
                            if (neutral <= start) item = (item & ~0xff) | (sc & 0xff);
                        }
                        break;
                    case 5: if (cur != GpTextTables.ScriptControl) split = i; break;
                    case 6:
                        item |= 0x200;
                        joiner = i;
                        if (cp == 0x200d) zwj = i;
                        break;
                    default: if (cur == GpTextTables.ScriptControl) split = i; break;
                    }
                    if (start < split) {
                        Emit (start, split);
                        int next = (item & ~0xff) | (sc & 0xff);
                        item = (next & ~0xff00) | 0x80000;
                        if (zwj != split - 1) item = next & ~0x8ff00;
                        start = split;
                    }
                }
            }
            Emit (start, _n);
            return result;
        }

        /// <summary>CreateTextRuns: one run per item, shaped by GetGlyphs; a control item's
        /// characters take the face's blank glyph.</summary>
        void CreateTextRuns (int start, int len, int script, int flags, int level)
        {
            // MirroredNumericAndVerticalAnalysis @1800f1b30: every item of a vertical format is vertical.
            if (IsVertical) flags |= 0x20;
            var run = new Run {
                Kind = 0, Cp = start, Len = len, Str = start, Script = script, ItemFlags = flags, Level = level,
                Face = Face, Family = Family, Style = Style, Em = Em,
                Underline = (Style & 4) != 0 ? 1 : 0,
            };
            if ((Style & 8) != 0) run.Underline |= 2;
            bool hotkey = (Format?.Hotkey ?? 0) != 0;
            if (script == GpTextTables.ScriptControl) {
                // A control item: each character's cmap glyph, or the blank glyph (with a zero-width
                // space's properties) for a glyph the face lacks, for the zero-width and directional
                // marks U+200B..U+200F and U+FEFF unless the format says DisplayFormatControl, and for
                // a hot-key marker.
                var sh = new GpTextShaper.Shaped {
                    Glyphs = new ushort [len], ClusterMap = new ushort [len], GlyphProps = new ushort [len], TextProps = new ushort [len],
                };
                int blank = Face.GlyphIndex (' ');
                bool show = (FormatFlags & GpTextFormat.DisplayFormatControl) != 0;
                for (int k = 0; k < len; k++) {
                    int ch = Text [start + k];
                    int g = Face.GlyphIndexOf (ch);
                    sh.ClusterMap [k] = (ushort) k;
                    sh.GlyphProps [k] = GpTextShaper.PropClusterStart;
                    if ((!show && ((uint) (ch - 0x200b) < 5 || ch == 0xfeff)) || g == 0 || (ch == 0xffff && hotkey)) {
                        g = blank;
                        sh.GlyphProps [k] = GpTextShaper.PropClusterStart | GpTextShaper.PropZeroWidth;
                    }
                    sh.Glyphs [k] = (ushort) g;
                }
                run.Shape = sh;
                _runs.Add (run);
                return;
            }
            // CreateTextRuns @18003aaf8: the item shaped with its face; from the first glyph the face
            // lacks, the run is cut and the characters it lacks go to GpFamilyFallback (unless
            // NoFontFallback, or the face is a symbol face).
            bool fallback = (FormatFlags & GpTextFormat.NoFontFallback) == 0 && !GpFontFallback.IsSymbolFace (Face);
            int pos = start, rem = len;
            while (rem > 0) {
                GpTextShaper.Shaped sh = Shape (Face, pos, rem, script, level, hotkey);
                int missing = -1;
                if (fallback)
                    for (int g = 0; g < sh.Glyphs.Length; g++) if (sh.Glyphs [g] == 0) { missing = g; break; }
                if (missing < 0) {
                    _runs.Add (Piece (run, pos, rem, sh));
                    return;
                }
                // The first character of the missing glyph's cluster, and the missing ones after it.
                int c0 = 0;
                while (c0 < rem && sh.ClusterMap [c0] < missing) c0++;
                int c1 = c0;
                while (c1 < rem && sh.Glyphs [sh.ClusterMap [c1]] == 0) c1++;
                if (c0 == rem || sh.ClusterMap [c0] > missing) {
                    c0--;
                    while (c0 > 0 && sh.ClusterMap [c0 - 1] == sh.ClusterMap [c0]) c0--;
                    missing = sh.ClusterMap [Math.Max (0, c0)];
                }
                if (c0 > 0) {
                    var head = new GpTextShaper.Shaped {
                        Glyphs = sh.Glyphs [..missing], GlyphProps = sh.GlyphProps [..missing], ClusterMap = sh.ClusterMap [..c0], TextProps = sh.TextProps [..c0],
                    };
                    _runs.Add (Piece (run, pos, c0, head));
                    pos += c0; rem -= c0;
                }
                int count = Math.Max (1, c1 - Math.Max (0, c0));
                int n = GpFontFallback.For (Family).GetUniformFallbackFace (Text, pos, Math.Min (count, rem), Style & 3, script, out string fam, out int faceStyle);
                n = Math.Max (1, Math.Min (n, rem));
                // The fallback family's face for the style asked: GDI+ takes the slot of the style the family
                // supports and its realization simulates the rest (flags 0x2000 / 0x4000 for a bold / italic
                // asked of a face that is not), a slot DirectWrite fills with a simulated face included.
                TrueTypeFont face = fam == null ? Face : GdiPlusText.Face (fam, Style & 3) ?? Face;
                var piece = Piece (run, pos, n, Shape (face, pos, n, script, level, hotkey));
                if (!ReferenceEquals (face, Face)) {
                    piece.Face = face; piece.Family = fam;
                    piece.Em = GpFontFallback.FallbackEm (Face, Em, face);
                }
                _runs.Add (piece);
                pos += n; rem -= n;
            }
        }

        GpTextShaper.Shaped Shape (TrueTypeFont face, int start, int len, int script, int level, bool hotkey)
            => hotkey ? GpTextShaper.GetGlyphsWithHotKeys (face, Text, start, len, script, (level & 1) != 0)
                      : GpTextShaper.GetGlyphs (face, Text, start, len, script, (level & 1) != 0);

        static Run Piece (Run item, int start, int len, GpTextShaper.Shaped shape) => new Run {
            Kind = 0, Cp = start, Len = len, Str = start, Script = item.Script, ItemFlags = item.ItemFlags, Level = item.Level,
            Face = item.Face, Family = item.Family, Style = item.Style, Em = item.Em, Underline = item.Underline, Shape = shape,
        };

        /// <summary>The run holding cp (GdipLscbkFetchRun): a fetch from inside a run splits it
        /// at the cluster there, the earlier part keeping its glyphs up to it.</summary>
        internal Run RunAt (int cp)
        {
            BuildRuns ();
            for (int i = 0; i < _runs.Count; i++) {
                Run r = _runs [i];
                if (cp < r.Cp || cp >= r.Cp + r.Len) continue;
                if (cp == r.Cp || r.Kind != 0) return r;
                int off = cp - r.Cp;
                int g0 = r.Shape.ClusterMap [off];
                var a = r.Shape;
                int ng = a.Glyphs.Length - g0, nc = r.Len - off;
                if (ng <= 0 || nc <= 0) return r;
                var b = new GpTextShaper.Shaped {
                    Glyphs = new ushort [ng], GlyphProps = new ushort [ng], ClusterMap = new ushort [nc], TextProps = new ushort [nc],
                };
                Array.Copy (a.Glyphs, g0, b.Glyphs, 0, ng);
                Array.Copy (a.GlyphProps, g0, b.GlyphProps, 0, ng);
                Array.Copy (a.TextProps, off, b.TextProps, 0, nc);
                for (int k = 0; k < nc; k++) b.ClusterMap [k] = (ushort) Math.Min (a.ClusterMap [off + k] - g0, ng - 1);
                var head = new GpTextShaper.Shaped {
                    Glyphs = a.Glyphs [..g0], GlyphProps = a.GlyphProps [..g0], ClusterMap = new ushort [off], TextProps = a.TextProps [..off],
                };
                for (int k = 0; k < off; k++) head.ClusterMap [k] = (ushort) Math.Min (a.ClusterMap [k], Math.Max (0, g0 - 1));
                var tail = new Run {
                    Kind = 0, Cp = cp, Len = nc, Str = r.Str + off, Script = r.Script, ItemFlags = r.ItemFlags, Level = r.Level,
                    Face = r.Face, Family = r.Family, Style = r.Style, Em = r.Em, BaseOffset = r.BaseOffset, Shape = b, Underline = r.Underline,
                };
                r.Len = off; r.Shape = head;
                _runs.Insert (i + 1, tail);
                return tail;
            }
            return null;
        }

        /// <summary>LS cp to string position (no level-change runs are inserted here: one to one).</summary>
        internal int StringPosition (int cp) => Math.Min (cp, _n);

        /// <summary>FullTextImager::LineServicesStringPosition: string position to LS cp.</summary>
        internal int LsCp (int pos) => pos;

        // ---- the widths Line Services formats with -------------------------------------------------------

        /// <summary>GdipLscbkGetRunCharWidths -> SimpleGetRunCharWidths: each character's design
        /// advance (its cmap glyph's) through (float) tracking * em / upem * r, rounded.</summary>
        internal int[] CharWidths (Run run, int from, int count)
        {
            var w = new int [count];
            if (run.Kind != 0) return w;
            double k = (double) (Tracking * ((run.Em / run.Face.UnitsPerEmForHinting) * R));
            for (int i = 0; i < count; i++) {
                int ch = Text [run.Str + from + i];
                int g = run.Face.GlyphIndexOf (ch);
                w [i] = (int) MathF.Floor ((float) (GpTextShaper.DesignAdvance (run.Face, g) * k) + 0.5f);
            }
            return w;
        }

        /// <summary>SimpleGetRunCharWidths for one character in a run's face.</summary>
        internal int CharWidths (Run run, char ch)
        {
            double k = (double) (Tracking * ((run.Em / run.Face.UnitsPerEmForHinting) * R));
            int g = run.Face.GlyphIndexOf (ch);
            // SimpleGetRunCharWidths @18003e0c8: GetDesignGlyphAdvances sideways (the run's format
            // flags & 2) in a vertical format -- the advance height.
            int adv = IsVertical ? GpTextShaper.DesignAdvanceHeight (run.Face, g) : GpTextShaper.DesignAdvance (run.Face, g);
            return (int) MathF.Floor ((float) (adv * k) + 0.5f);
        }

        /// <summary>GdipLscbkGetGlyphPositions: the glyphs' advances from GetGlyphPlacements at
        /// em * r (ideal units), tracked and rounded; a control run's glyphs advance by their
        /// design width unless they are zero-width.</summary>
        internal void GlyphPositions (Run run, int g0, int count, int[] adv, int[] offU, int[] offV)
        {
            int upem = run.Face.UnitsPerEmForHinting;
            float k = run.Em * R / upem;
            float tr = Tracking;
            if (run.Script == GpTextTables.ScriptControl) {
                for (int i = 0; i < count; i++) {
                    adv [i] = (run.Shape.GlyphProps [g0 + i] & GpTextShaper.PropZeroWidth) == 0
                        ? (int) MathF.Floor (run.Face.DesignAdvance (run.Shape.Glyphs [g0 + i]) * k * tr + 0.5f) : 0;
                    offU [i] = offV [i] = 0;
                }
                return;
            }
            // GdipLscbkGetGlyphPositions @18003d790 measures a face DirectWrite emboldens (its
            // GetSimulations & 1), not sideways and not of 2048 units to the em, unemboldened -- with
            // the family's face of the style less bold -- and each advance there that is not zero
            // gains (2 * upem - 1) / 100 ideal units. (An italic asked of a slot face that is not
            // italic is measured with an oblique-only face; DirectWrite fills every italic slot with
            // a simulated oblique where the family has none, so the port never meets that case.)
            bool boldConst = run.Face.SynthesizesBold && GpFontMapper.DWriteSimulatesBold (run.Face)
                             && (run.ItemFlags & 0x8) == 0 && upem != 2048;
            int boldAdd = boldConst ? (2 * upem - 1) / 100 : 0;
            float[] a = GpTextShaper.GetGlyphAdvances (run.Face, run.Shape.Glyphs, g0, count, run.Script, upem * k, unsimulated: boldConst);
            for (int i = 0; i < count; i++) {
                adv [i] = (run.Shape.GlyphProps [g0 + i] & GpTextShaper.PropZeroWidth) != 0 ? 0 : (int) MathF.Floor (a [i] * tr + 0.5f);
                if (adv [i] != 0) adv [i] += boldAdd;
                offU [i] = offV [i] = 0;
            }
        }

        // ---- lines ---------------------------------------------------------------------------------------

        /// <summary>A built line (BuiltLine): what Line Services made of it and where GDI+ puts it.</summary>
        internal sealed class Line
        {
            public int CpFirst, CpCount;     // +0x18 / +0x20
            public int StrFirst, Chars;      // +0x1c / +0x24
            public int Ascent, Descent, Height;   // +0x28 / +0x2c / +0x30
            public int Length;               // +0x34
            public int LeadMargin, TrailMargin;   // +0x44 / +0x48
            public int Trimmed;              // +0x50
            public int Start;                // +0x5c: the line's first u (margin plus alignment)
            public int Origin;               // +0x54: where Line Services starts drawing it
            public int EllipsisAt = -1;      // +0x58
            public int Consumed;             // the line's characters in the imager's line list (+0x168[i].count)
            public GpLineServices.LsLine Ls;
            public int[] DisplayUr;          // each dnode's u once the reversal objects are laid out
        }

        internal readonly List<Line> Lines = new List<Line> ();
        bool _linesBuilt;
        internal int LineCount, CharCount, MinLeft, MaxRight;   // +0x230 .. +0x23c
        internal float TextHeight;                               // +0x240 (world)

        /// <summary>BuildLines @1800f0040 -> BuildAllLines.</summary>
        internal void BuildLines ()
        {
            if (_linesBuilt || !Valid) return;
            _linesBuilt = true;
            BuildRuns ();
            int trimming = Format?.Trimming ?? 1;
            int flags = FormatFlags;
            MinLeft = 0x7fffffff; MaxRight = int.MinValue;
            float limitFactor = 0f;
            if (R * _across > 0f)
                limitFactor = (flags & GpTextFormat.LineLimit) != 0 ? 1f : trimming != 0 ? 0.25f : 0f;
            int lineTrim = (flags & GpTextFormat.NoWrap) != 0 ? trimming : 0;
            int pos = 0, cp = 0, total = 0;
            Line prev = null;
            while (pos < _n) {
                var line = BuildLine (cp, pos, lineTrim, false, prev);
                if (line == null) return;
                if (limitFactor > 0f && R * _across - total < line.Height * limitFactor) {
                    // The line does not fit: when text remains, the last line that did is rebuilt trimmed.
                    if ((flags & GpTextFormat.NoWrap) == 0 && trimming != 0 && Lines.Count > 0) {
                        Line last = Lines [^1];
                        int lastPos = last.StrFirst;
                        Lines.RemoveAt (Lines.Count - 1);
                        // The count: every line's own characters, the last one's span swapped for the rebuilt line's.
                        CharCount -= last.Consumed;
                        if (trimming == 2) {
                            // Word trimming keeps the line as it broke unless it ended a paragraph.
                            char before = pos > 0 ? Text [pos - 1] : '\n';
                            last.Trimmed = before != '\r' && before != '\n' ? 2 : 0;
                            Lines.Add (last);
                            last.Consumed = UntrimmedCount (last);
                            CharCount += last.Chars;
                        } else {
                            var re = BuildLine (last.CpFirst, lastPos, trimming, true, Lines.Count > 0 ? Lines [^1] : null);
                            Lines.Add (re);
                            re.Consumed = UntrimmedCount (re);
                            CharCount += re.Chars;
                            Account (re);
                        }
                    }
                    break;
                }
                total += line.Height;
                Lines.Add (line);
                int count = UntrimmedCount (line);
                line.Consumed = count;
                if (count < 1) return;
                CharCount += line.Chars;
                LineCount = Lines.Count;
                Account (line);
                pos += count;
                // The next line starts where the untrimmed count ends (GetUntrimmedCharacterCount's
                // Line Services position), not where a trimmed line's Line Services line did.
                cp = LsCp (pos);
                prev = line;
            }
            LineCount = Lines.Count;
            int sum = 0;
            foreach (Line l in Lines) sum += l.Height;
            TextHeight = sum / R;
            if (Format == null || Format.LeadMargin != 0f) TextHeight = Em * 0.125f + sum / R;
        }

        void Account (Line line)
        {
            MinLeft = Math.Min (MinLeft, line.Start - line.LeadMargin);
            MaxRight = Math.Max (MaxRight, line.TrailMargin + line.Length + line.Start);
        }

        /// <summary>BuiltLine::GetUntrimmedCharacterCount: a trimmed line takes the rest of its paragraph with it.</summary>
        int UntrimmedCount (Line line)
        {
            int count = line.Chars;
            if (line.Trimmed != 0 && line.Trimmed != 5) {
                int i = line.StrFirst + count;
                char before = i > 0 ? Text [i - 1] : '\0';
                if (before != '\r' && before != '\n') {
                    while (i < _n && Text [i] != '\n') i++;
                    if (i < _n) i++;
                    count = i - line.StrFirst;
                }
            }
            return count;
        }

        /// <summary>BuiltLine::BuiltLine.</summary>
        Line BuildLine (int cp, int pos, int trimming, bool lastLine, Line prev)
        {
            int lm, tm;
            float emr = Em * R;
            if (Format == null) { lm = tm = Rnd (emr * (1f / 6f)); }
            else { lm = Rnd (Format.LeadMargin * emr); tm = Rnd (Format.TrailMargin * emr); }
            int flags = FormatFlags;
            int extent = Rnd (_along * R);
            int room;
            if (extent < 1 || ((flags & GpTextFormat.NoWrap) != 0 && trimming == 0)) room = 0x1000000;
            else room = Math.Max (0, extent - lm - tm);
            var line = new Line { CpFirst = cp, StrFirst = pos, LeadMargin = lm, TrailMargin = tm };
            CreateLine (line, room, trimming, flags, lastLine);
            int share = 0;
            int align = Format?.PhysicalAlignment ?? 0;
            if (align != 0) {
                int used = lm + line.Length + tm;
                share = extent < 1 ? -used : extent - used;
                if (align == 1) share /= 2;
            }
            line.Start = share + lm;
            line.Origin = line.Start;
            if (!IsVertical && IsRightToLeft) line.Origin = line.Start + line.Length;
            return line;
        }

        /// <summary>BuiltLine::CreateLine: Line Services' line, trimmed as the format asks.</summary>
        void CreateLine (Line line, int room, int trimming, int flags, bool lastLine)
        {
            int dua = trimming == 5 ? 0x1000000 : room;
            line.Ls = GpLineServices.CreateLine (this, line.CpFirst, dua, trimming == 1 || trimming == 3);
            bool endPara = line.Ls.EndPara;
            if (trimming == 1 || trimming == 2) {
                if (!endPara) line.Trimmed = trimming;
            } else if (trimming == 3 || trimming == 4) {
                if (lastLine || !endPara) RecreateLineEllipsis (line, room, trimming);
            }
            line.CpCount = line.Ls.CpLim - line.CpFirst;
            line.Ascent = line.Ls.Ascent;
            line.Descent = line.Ls.Descent;
            line.Height = line.Ls.Height;
            bool trailing = (line.Trimmed == 0 || line.Trimmed == 5) && (flags & GpTextFormat.MeasureTrailingSpaces) != 0;
            line.Length = trailing ? line.Ls.UrLimWithSpaces : line.Ls.UrLim;
            if (line.EllipsisAt >= 0) line.Length += _ellipsisWidth;
            line.Chars = StringPosition (Math.Min (line.Ls.CpLim, _n)) - line.StrFirst;
        }

        int _ellipsisWidth = -1;

        /// <summary>BuiltLine::RecreateLineEllipsis: room for the ellipsis taken off the line, or,
        /// when half the room is less than the ellipsis, plain trimming.</summary>
        void RecreateLineEllipsis (Line line, int room, int trimming)
        {
            int ew = EllipsisWidth ();
            if (room / 2 < ew) {
                trimming = trimming == 3 ? 1 : 2;
            } else {
                line.Ls = GpLineServices.CreateLine (this, line.CpFirst, room - ew, trimming == 3);
                line.EllipsisAt = line.Ls.UrLim;
            }
            line.Trimmed = trimming;
        }

        /// <summary>EllipsisInfo::EllipsisInfo @1800ef130 (FullTextImager::GetEllipsisInfo): U+2026,
        /// or three full stops where the face has no glyph for it; each glyph's design advance
        /// through (double) (r * em / upem), rounded, not tracked.</summary>
        internal Run Ellipsis;
        internal int[] EllipsisAdvances;

        int EllipsisWidth ()
        {
            if (_ellipsisWidth >= 0) return _ellipsisWidth;
            int g = Face.GlyphIndex ('…');
            int iflags = 0;
            if (g != 0 && IsVertical) {
                // A vertical format asks for the vertical variant; a face with none at all leaves
                // the glyph the query wrote, 0, drawn upright; one with variants but none for U+2026
                // takes the full stops.
                iflags |= 0x20;
                GsubTable gs = Face.Gsub;
                bool has = gs != null && (gs.HasFeature ("DFLT", "vert") || gs.HasFeature ("latn", "vert") || gs.HasFeature ("DFLT", "vrt2") || gs.HasFeature ("latn", "vrt2"));
                if (!has) { g = -1; iflags |= 0x8; }
                else {
                    int v = gs.Substitute ("latn", "vert", g);
                    if (v == g) v = gs.Substitute ("DFLT", "vert", g);
                    if (v == g) g = 0;
                    else { g = v; iflags |= 0x8; }
                }
            }
            ushort[] glyphs = g > 0 ? new[] { (ushort) g } : g < 0 ? new ushort[] { 0 }
                : new ushort[] { (ushort) Face.GlyphIndex ('.'), (ushort) Face.GlyphIndex ('.'), (ushort) Face.GlyphIndex ('.') };
            string chars = glyphs.Length == 1 ? "…" : "...";
            double k = (double) (R * Em / Face.UnitsPerEmForHinting);
            EllipsisAdvances = new int [glyphs.Length];
            _ellipsisWidth = 0;
            for (int i = 0; i < glyphs.Length; i++) {
                // GetDesignGlyphAdvances, sideways in a vertical format: the advance height (the
                // typographic ascent less descent for a face with no vertical metrics).
                int adv = (iflags & 0x20) != 0 ? GpTextShaper.DesignAdvanceHeight (Face, glyphs [i]) : GpTextShaper.DesignAdvance (Face, glyphs [i]);
                EllipsisAdvances [i] = (int) MathF.Floor ((float) (adv * k) + 0.5f);
                _ellipsisWidth += EllipsisAdvances [i];
            }
            var sh = new GpTextShaper.Shaped {
                Glyphs = glyphs, GlyphProps = new ushort [glyphs.Length], ClusterMap = new ushort [glyphs.Length], TextProps = new ushort [glyphs.Length],
            };
            for (int i = 0; i < glyphs.Length; i++) { sh.GlyphProps [i] = GpTextShaper.PropClusterStart; sh.ClusterMap [i] = (ushort) i; }
            // The ellipsis item takes the paragraph's direction: in a right-to-left format its cell
            // origin is its right end (snapped there, the glyph one advance left of it).
            Ellipsis = new Run {
                Kind = 0, Cp = 0, Len = glyphs.Length, Str = -1, Script = GpTextTables.ScriptLatin,
                ItemFlags = iflags, Level = IsRightToLeft && !IsVertical ? 1 : 0, Face = Face, Family = Family, Style = Style, Em = Em, Shape = sh,
                EllipsisText = chars,
            };
            return _ellipsisWidth;
        }

        // ---- MeasureString ----------------------------------------------------------------------------

        /// <summary>FullTextImager::Measure: the text's extent (left, right, height in world units),
        /// the characters and lines it laid.</summary>
        public void Measure (out float left, out float right, out float height, out int chars, out int lines)
        {
            BuildLines ();
            left = MinLeft == 0x7fffffff ? 0f : MinLeft / R;
            right = MaxRight == int.MinValue ? 0f : MaxRight / R;
            height = TextHeight;
            chars = CharCount;
            lines = LineCount;
        }

        /// <summary>GpGraphics::MeasureString's rectangle from the imager's extent.</summary>
        public RectangleF MeasureRect (RectangleF layout, out int chars, out int lines)
        {
            Measure (out float l, out float r, out float h, out chars, out lines);
            if (r < l) { l = 0f; r = 0f; }
            var o = layout;
            float w, hh;
            if (!IsVertical) {
                o.Width = r - l;
                o.X = layout.X + l;
                if (Format != null) {
                    if (Format.LineAlign == 1) o.Y += (o.Height - h) * 0.5f;
                    else if (Format.LineAlign == 2) o.Y += o.Height - h;
                }
                o.Height = h;
                w = o.Width; hh = h;
            } else {
                float off = 0f;
                o.Y = layout.Y + l;
                o.Height = r - l;
                if (Format.LineAlign == 1) off = (o.Width - h) * 0.5f;
                else if (Format.LineAlign == 2) off = o.Width - h;
                if (Format.IsRightToLeft) off = (o.Width - h) - off;
                o.X = off + layout.X;
                o.Width = h;
                w = h; hh = r - l;
            }
            if (Format != null && (Format.Flags & GpTextFormat.NoClip) != 0) return o;
            if (layout.Width > 0f && !(w <= layout.Width)) { o.Width = layout.Width; o.X = layout.X; }
            if (layout.Height > 0f && !(hh <= layout.Height)) { o.Height = layout.Height; o.Y = layout.Y; }
            return o;
        }
    }
}
