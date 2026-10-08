// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// Managed replacement for the native Line Services engine (PresentationNative LoCreateContext /
// LoCreateLine / LoDisplayLine / ...), used off-Windows so TextBox/RichTextBox and any wrapped or
// otherwise "complex" text (which WPF routes through FullTextLine instead of the managed
// SimpleTextLine) can format and render without the native line-layout DLL.
//
// It does NOT reimplement WPF's 31 LS callbacks (LineServicesCallbacks.cs already provides them);
// it reimplements the ENGINE that drives them. LoCreateLine fetches runs and glyphs through those
// callbacks, does greedy line breaking, and builds an opaque managed line; LoDisplayLine walks the
// line and calls the draw callbacks; the query entry points answer caret/hit-test.
//
// Implemented here: bidirectional reordering (ReorderRunsVisually -- runs are accumulated in
// logical order and given their visual x afterwards, so a line mixing directions lays out correctly
// while the run list stays cp-ordered for the caret) and justification (JustifyLine). See
// Documentation/text-shaping.md for how the first fits with shaping.
//
// Line breaking within a line is greedy, and there is no optimal-break entry point here at all:
// LoCreateBreaks / LoCreateParaBreakingSession are the LS interface for that, and nothing can reach
// them in this port. The public API is compiled out (TextBreakpoint and TextFormatter.
// CreateParagraphCache are internal unless OPTIMALBREAK_API is defined), and the only internal
// caller is the native PTS host, which FlowDocumentPage bypasses on every platform. Optimal
// paragraph breaking for FlowDocument lives where the layout actually happens now --
// ManagedFlowLayout.BreakParagraphOptimally, selected by FlowDocument.IsOptimalParagraphEnabled.
//
// Handles (ploc / ploline / break records) are GCHandles to managed objects, surfaced as IntPtr.
//

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;
using MS.Internal.Text.TextInterface;

namespace MS.Internal.TextFormatting
{
    // A formatted run within a managed line: the LS run handle plus the glyph data we shaped, and
    // the pen position where it starts. DrawGlyphs replays exactly this during LoDisplayLine.
    internal sealed class ManagedLsRun
    {
        public Plsrun Plsrun;
        public int CpFirst;                 // first cp of this run
        public int CchText;                 // characters consumed
        public char[] Text;                 // run characters (Text[0..CchText])

        /// <summary>
        ///  The characters LineServices measures, shapes and draws in place of <see cref="Text"/>,
        ///  or null when they are the same: a special-character dnode is presented as the character
        ///  its class is formatted with (FormatSpecial: U+00A0 as a space, U+2011 as a hyphen), and
        ///  a tab as a space (FormatStartTab). Text keeps the originals, which line breaking reads.
        /// </summary>
        public char[] Pres;

        /// <summary>A tab dnode: as wide as the tab stop it reaches, drawn as a space.</summary>
        public bool IsTab;

        /// <summary>A typographic-space dnode (FormatSpecial kind 8): drawn as wchSpace characters at
        /// its own characters' widths (LsDisplayText).</summary>
        public bool DrawSpaces;

        public char[] Shaped => Pres ?? Text;
        public ushort[] Glyphs;             // glyph indices
        public ushort[] ClusterMap;         // char->glyph cluster map
        public ushort[] CharProps;          // per-char properties (from GetGlyphs)
        public uint[] GlyphProps;           // per-glyph properties
        public int[] Advances;              // ideal glyph advances
        public GlyphOffset[] Offsets;       // glyph offsets
        public int GlyphCount;
        public IntPtr PlsrunPtr;            // LS run pointer, needed to re-shape a truncated run

        /// <summary>
        ///  Ideal x of this run's LEFT edge, line-relative and in VISUAL order -- assigned by the
        ///  bidi reordering pass, not by the order the runs were fetched in.
        /// </summary>
        public int PenX;

        public int Width;                   // ideal advance width of the run
        public int[] CharWidths;            // ideal width of each character (a cluster's split over its characters)

        /// <summary>
        ///  The run's glyphs come from the shaping engine (LsChp.fGlyphBased), so its width is what
        ///  its glyphs advance by rather than its characters' nominal widths.
        /// </summary>
        public bool GlyphBased;

        /// <summary>
        ///  Shaped in one piece with the run before it -- the pair shares a glyph chunk, the way
        ///  LineServices groups neighbouring runs that FInterruptShaping does not separate.
        /// </summary>
        public bool JoinsPrevious;

        public int Ascent;
        public int Descent;
        public bool IsText;                 // false for control/object runs (skipped when drawing)

        /// <summary>
        ///  The run's resolved bidi embedding level. Odd is right-to-left. Accumulated from the
        ///  Reverse / CloseAnchor control runs the text store emits around every level change, which
        ///  is how LineServices is told about direction -- there is no level on the run itself.
        /// </summary>
        public int BidiLevel;

        public bool IsRightToLeft => (BidiLevel & 1) != 0;
    }

    internal sealed class ManagedLsLine
    {
        public readonly List<ManagedLsRun> Runs = new();
        public LineServicesCallbacks Callbacks;   // to replay draw callbacks during LoDisplayLine
        public int CpFirst;
        public int CpLim;                   // one past the last cp on the line
        public int Ascent;
        public int Descent;
        public int Width;                   // ideal width including trailing spaces
        public int WidthNoTrailing;         // ideal width excluding trailing whitespace
        public bool ForcedBreak;            // ended at a hard line/para break
        public bool RightToLeft;            // paragraph flow direction is right-to-left
    }

    internal sealed class ManagedLsContext
    {
        public LineServicesCallbacks Callbacks;
        public LsContextInfo ContextInfo;
        public GCHandle Self;

        // LoSetTabs: the paragraph's incremental tab and its own tab stops, ideal units.
        public int IncrementalTab;
        public LsTbd[] Tabs = Array.Empty<LsTbd>();
    }

    internal static class ManagedLineServices
    {
        // The LineServicesCallbacks object for the context currently being created. Set by
        // TextFormatterContext.Init right before LoCreateContext; consumed by CreateContext.
        [ThreadStatic] private static LineServicesCallbacks t_pendingCallbacks;

        internal static void SetPendingCallbacks(LineServicesCallbacks callbacks)
        {
            t_pendingCallbacks = callbacks;
        }

        private static ManagedLsContext ContextFrom(IntPtr ploc)
            => (ManagedLsContext)GCHandle.FromIntPtr(ploc).Target;

        private static ManagedLsLine LineFrom(IntPtr ploline)
            => (ManagedLsLine)GCHandle.FromIntPtr(ploline).Target;

        // ---- context lifetime ----

        internal static LsErr CreateContext(ref LsContextInfo contextInfo, ref LscbkRedefined lscbkRedef, out IntPtr ploc)
        {
            var ctx = new ManagedLsContext
            {
                Callbacks = t_pendingCallbacks,
                ContextInfo = contextInfo,
            };
            t_pendingCallbacks = null;
            ctx.Self = GCHandle.Alloc(ctx);
            ploc = GCHandle.ToIntPtr(ctx.Self);
            return ctx.Callbacks != null ? LsErr.None : LsErr.InvalidParameter;
        }

        internal static LsErr DestroyContext(IntPtr ploc)
        {
            if (ploc != IntPtr.Zero)
            {
                GCHandle h = GCHandle.FromIntPtr(ploc);
                if (h.IsAllocated) h.Free();
            }
            return LsErr.None;
        }

        // ---- configuration (we are device-independent; nothing to store) ----

        internal static LsErr SetDoc(IntPtr ploc, int isDisplay, int isReferencePresentationEqual, ref LsDevRes deviceInfo)
            => LsErr.None;

        internal static LsErr SetBreaking(IntPtr ploc, int strategy) => LsErr.None;

        internal static unsafe LsErr SetTabs(IntPtr ploc, int durIncrementalTab, int tabCount, LsTbd* pTabs)
        {
            ManagedLsContext ctx = ContextFrom(ploc);
            ctx.IncrementalTab = durIncrementalTab;
            var tabs = new LsTbd[pTabs != null && tabCount > 0 ? tabCount : 0];
            for (int i = 0; i < tabs.Length; i++) tabs[i] = pTabs[i];
            ctx.Tabs = tabs;
            return LsErr.None;
        }

        // ---- break records (single-line formatting doesn't chain, but TextBox may clone) ----

        internal static LsErr AcquireBreakRecord(IntPtr ploline, out IntPtr pbreakrec)
        {
            // A break record identifies where the next line continues. For our greedy single-line
            // engine the line's CpLim is sufficient; wrap it in a tiny managed object.
            ManagedLsLine line = LineFrom(ploline);
            var rec = new BreakRecord { CpLim = line.CpLim, ForcedBreak = line.ForcedBreak };
            pbreakrec = GCHandle.ToIntPtr(GCHandle.Alloc(rec));
            return LsErr.None;
        }

        internal static LsErr DisposeBreakRecord(IntPtr pbreakrec, bool finalizing)
        {
            if (pbreakrec != IntPtr.Zero)
            {
                GCHandle h = GCHandle.FromIntPtr(pbreakrec);
                if (h.IsAllocated) h.Free();
            }
            return LsErr.None;
        }

        internal static LsErr CloneBreakRecord(IntPtr pbreakrec, out IntPtr pclone)
        {
            var src = (BreakRecord)GCHandle.FromIntPtr(pbreakrec).Target;
            var rec = new BreakRecord { CpLim = src.CpLim, ForcedBreak = src.ForcedBreak };
            pclone = GCHandle.ToIntPtr(GCHandle.Alloc(rec));
            return LsErr.None;
        }

        internal sealed class BreakRecord
        {
            public int CpLim;
            public bool ForcedBreak;
        }

        // ---- the engine: format one line ----

        internal static unsafe LsErr CreateLine(
            IntPtr ploc, int cpFirst, int ccpLim, int durColumn, uint dwLineFlags,
            IntPtr pInputBreakRec, out LsLInfo plslineInfo, out IntPtr pploline,
            out int maxDepth, out LsLineWidths lineWidths)
        {
            plslineInfo = new LsLInfo();
            lineWidths = new LsLineWidths();
            pploline = IntPtr.Zero;
            maxDepth = 1;

            ManagedLsContext ctx = ContextFrom(ploc);
            LineServicesCallbacks cb = ctx.Callbacks;
            if (cb == null) return LsErr.InvalidContext;

            var line = new ManagedLsLine { CpFirst = cpFirst };

            // Paragraph flow direction. FetchPap reports it as the text flow (WS = right-to-left).
            // Runs are accumulated in LOGICAL order and given their visual x afterwards, by the
            // bidi reordering pass; the RTL flag additionally mirrors the whole line, because an
            // RTL paragraph's line origin is its right edge.
            LsPap pap = new LsPap();
            cb.FetchPap(ploc, cpFirst, ref pap);
            line.RightToLeft = pap.lstflow == LsTFlow.lstflowWS;

            // The paragraph's own embedding level, and the level every run starts at. The text
            // store counts its Reverse / CloseAnchor markers from here, not from zero.
            int baseLevel = line.RightToLeft ? 1 : 0;
            int bidiLevel = baseLevel;

            int column = (durColumn <= 0) ? int.MaxValue : durColumn;
            int penX = 0;
            int cp = cpFirst;
            int lineAscent = 0, lineDescent = 0;
            bool forced = false;
            bool lineFull = false;

            const int MaxCharsGuard = 1 << 20;
            char[] fetchBuf = new char[512];
            PendingTab pendingTab = default;

            while (!lineFull)
            {
                if (cp - cpFirst > MaxCharsGuard) { forced = true; break; }

                LsChp chp = new LsChp();
                int fBufUsed = 0, cchText = 0, fHidden = 0;
                IntPtr plsrunPtr = IntPtr.Zero;
                char* pwchText = null;
                LsErr fr;
                char[] runText = null;
                fixed (char* pFetch = fetchBuf)
                {
                    fr = cb.FetchRunRedefined(ploc, cp, 0, IntPtr.Zero, pFetch, fetchBuf.Length,
                        ref fBufUsed, out pwchText, ref cchText, ref fHidden, ref chp, ref plsrunPtr);

                    if (fr == LsErr.None && cchText > 0)
                    {
                        // When fIsBufferUsed the text was copied into our fixed buffer and pwchText
                        // is null; otherwise pwchText points at the run's own (transient) characters.
                        char* src = (fBufUsed != 0) ? pFetch : pwchText;
                        if (src != null)
                        {
                            runText = new char[cchText];
                            for (int i = 0; i < cchText; i++) runText[i] = src[i];
                        }
                    }
                }
                if (fr != LsErr.None) return fr;
                if (cchText <= 0 || runText == null) { forced = true; break; }

                Plsrun plsrun = (Plsrun)(uint)plsrunPtr.ToInt64();

                // Bidi level changes arrive as control runs, one CHARACTER per level step: the text
                // store brackets every embedding with Reverse (level up) and CloseAnchor (level
                // down). That is the only place direction is expressed -- FetchRun says nothing about
                // it -- so tracking these brackets is what makes reordering possible at all. Note the
                // marker itself belongs to the level OUTSIDE the bracket it opens or closes.
                // Consecutive markers of one kind merge into one run (CreateReverseLSRuns writes one
                // cp each into the plsrun vector), so stepping once per RUN left "shalom 12 abc" --
                // 2 -> 0 in a single two-character CloseAnchor -- with " abc" still at level 1,
                // reversed onto the digits.
                Plsrun runKind = TextStore.ToIndex(plsrun);
                if (runKind == Plsrun.Reverse)
                {
                    bidiLevel += cchText;
                }
                else if (runKind == Plsrun.CloseAnchor)
                {
                    bidiLevel = Math.Max(baseLevel, bidiLevel - cchText);
                }

                bool isText = chp.idObj == (ushort)TextStore.ObjectId.Text_chp;

                // Line/paragraph breaks are delivered as Text_chp runs whose characters are the
                // separator markers (LS routes LineBreak/ParaBreak runs through the text object id).
                // They terminate the current line; consume the break and stop so we don't fetch past
                // the end of the paragraph (which would request an out-of-range cp).
                bool isBreak = IsLineOrParaBreak(runText);

                if (isBreak)
                {
                    // A hard line/paragraph break terminates the line. Record it as a zero-width
                    // non-text run so its codepoints stay QUERYABLE (a selection that spans lines
                    // includes the break cp; FullTextLine.GetTextBounds needs QueryLineCpPpoint to
                    // resolve it). DisplayLine skips non-text runs, so nothing is drawn for it.
                    line.Runs.Add(new ManagedLsRun
                    {
                        Plsrun = plsrun, CpFirst = cp, CchText = cchText, Text = runText,
                        Glyphs = Array.Empty<ushort>(), ClusterMap = Array.Empty<ushort>(),
                        CharProps = Array.Empty<ushort>(), GlyphProps = Array.Empty<uint>(),
                        Advances = Array.Empty<int>(), Offsets = Array.Empty<GlyphOffset>(),
                        GlyphCount = 0, PenX = penX, Width = 0, IsText = false, BidiLevel = bidiLevel,
                    });
                    cp += cchText;
                    forced = true;
                    break;
                }

                // A real inline object (an embedded UIElement) is delivered with idObj ==
                // ObjectId.InlineObject. Unlike a break it does not terminate the line and unlike a
                // control run it reserves horizontal space: format it through the object handler
                // (InlineFormat -> TextEmbeddedObject.Format) to get its width/height, place it at the
                // current pen, extend the line metrics, and CONTINUE the line. The object's own visual
                // is drawn by the framework (InlineUIContainer), so the run itself carries no glyphs.
                if (chp.idObj == (ushort)TextStore.ObjectId.InlineObject)
                {
                    ObjDim objDim = new ObjDim();
                    int rightMargin = (column == int.MaxValue) ? int.MaxValue : column;
                    LsErr ofr = cb.InlineFormat(ploc, plsrun, cp, penX, rightMargin,
                        ref objDim, out _, out _, out _, out _);

                    int objWidth  = ofr == LsErr.None ? objDim.dur : 0;
                    int objHeight = ofr == LsErr.None ? objDim.heightsRef.dvMultiLineHeight : 0;
                    int objAscent = ofr == LsErr.None ? objDim.heightsRef.dvAscent : 0;
                    if (objAscent < 0) objAscent = 0;
                    if (objHeight < objAscent) objHeight = objAscent;

                    line.Runs.Add(new ManagedLsRun
                    {
                        Plsrun = plsrun, CpFirst = cp, CchText = cchText, Text = runText,
                        Glyphs = Array.Empty<ushort>(), ClusterMap = Array.Empty<ushort>(),
                        CharProps = Array.Empty<ushort>(), GlyphProps = Array.Empty<uint>(),
                        Advances = Array.Empty<int>(), Offsets = Array.Empty<GlyphOffset>(),
                        GlyphCount = 0, PenX = penX, Width = objWidth, BidiLevel = bidiLevel,
                        Ascent = objAscent, Descent = objHeight - objAscent, IsText = false,
                    });
                    lineAscent = Math.Max(lineAscent, objAscent);
                    lineDescent = Math.Max(lineDescent, objHeight - objAscent);
                    penX += objWidth;
                    cp += cchText;
                    continue;
                }

                // CloseAnchor arrives as text, its characters the reverse object's terminator escape
                // (TextStore's esc.szObjectTerminator). LineServices consumes an escape as the end of
                // the object it closes: it is not a dnode, has no width and adds nothing to the line's
                // height -- where the paragraph's default ideal metrics, unrounded in Display mode,
                // made a right-to-left line in a composite face a pixel taller than stock's.
                if (!isText || fHidden != 0 || runKind == Plsrun.CloseAnchor)
                {
                    // A control (e.g. bidi Reverse, whose placeholder char is also U+FFFC) or hidden
                    // run. Unlike a hard break it does NOT terminate the line -- it is a zero-width,
                    // non-drawn placeholder that the line steps over. Consume it and CONTINUE so the
                    // line runs on to the real line/paragraph break. Force-breaking here instead
                    // produced a degenerate control-run-only line whose length the caller
                    // (FullTextLine/TextBlock) could not advance past, so it re-formatted from the same
                    // cp forever -> 100% CPU hang on any page whose text carries such a run.
                    line.Runs.Add(new ManagedLsRun
                    {
                        Plsrun = plsrun, CpFirst = cp, CchText = cchText, Text = runText,
                        Glyphs = Array.Empty<ushort>(), ClusterMap = Array.Empty<ushort>(),
                        CharProps = Array.Empty<ushort>(), GlyphProps = Array.Empty<uint>(),
                        Advances = Array.Empty<int>(), Offsets = Array.Empty<GlyphOffset>(),
                        GlyphCount = 0, PenX = penX, Width = 0, IsText = false, BidiLevel = bidiLevel,
                    });
                    cp += cchText;
                    continue;
                }

                // LineServices formats a fetched text run as one or more DNODES, and every dnode is
                // drawn on its own (DrawTextRun / DrawGlyphs per dnode) even where its glyphs were
                // shaped with its neighbours'. Cut the run to its first dnode; the rest is fetched
                // again from where it ends, as LS does.
                bool glyphBased = (chp.flags & LsChp.Flags.fGlyphBased) != 0;
                DnodeKind dnode = FirstDnode(runText, cchText, glyphBased, out int dnodeLength, out char presChar);
                if (dnodeLength < cchText)
                {
                    runText = runText[..dnodeLength];
                    cchText = dnodeLength;
                }
                if (dnode is DnodeKind.SpecialUnshaped or DnodeKind.SpecialSpace)
                {
                    glyphBased = false;
                }

                if (dnode == DnodeKind.Empty)
                {
                    // FormatStartEmptyDobj (the soft hyphen): no width and nothing drawn.
                    line.Runs.Add(new ManagedLsRun
                    {
                        Plsrun = plsrun, CpFirst = cp, CchText = cchText, Text = runText,
                        Glyphs = Array.Empty<ushort>(), ClusterMap = Array.Empty<ushort>(),
                        CharProps = Array.Empty<ushort>(), GlyphProps = Array.Empty<uint>(),
                        Advances = Array.Empty<int>(), Offsets = Array.Empty<GlyphOffset>(),
                        GlyphCount = 0, PenX = penX, Width = 0, IsText = false, BidiLevel = bidiLevel,
                    });
                    cp += cchText;
                    continue;
                }

                char[] presText = null;
                if (presChar != '\0')
                {
                    presText = new char[cchText];
                    Array.Fill(presText, presChar);
                }

                if (dnode == DnodeKind.Tab)
                {
                    // LsHandleTab: a tab first settles the right/centre/decimal tab before it, whose
                    // width depended on the text up to here, then reaches for its own stop.
                    penX = ResolvePendingTab(line, ref pendingTab, penX, column);
                    int tabWidth = BeginTab(ctx, line.Runs.Count, penX, ref pendingTab);
                    var tab = new ManagedLsRun
                    {
                        Plsrun = plsrun, PlsrunPtr = plsrunPtr, CpFirst = cp, CchText = cchText, Text = runText,
                        Pres = presText, IsTab = true, IsText = true, BidiLevel = bidiLevel,
                        Glyphs = Array.Empty<ushort>(), ClusterMap = new ushort[cchText],
                        CharProps = new ushort[cchText], GlyphProps = Array.Empty<uint>(),
                        Advances = Array.Empty<int>(), Offsets = Array.Empty<GlyphOffset>(),
                        GlyphCount = 0, PenX = penX, Width = tabWidth, CharWidths = new[] { tabWidth },
                    };
                    LsTxM tabMetrics = new LsTxM();
                    cb.GetRunTextMetrics(ploc, plsrun, LsDevice.Presentation, LsTFlow.lstflowES, ref tabMetrics);
                    tab.Ascent = tabMetrics.dvAscent;
                    tab.Descent = tabMetrics.dvDescent;
                    lineAscent = Math.Max(lineAscent, tab.Ascent);
                    lineDescent = Math.Max(lineDescent, tab.Descent);
                    line.Runs.Add(tab);
                    penX += tabWidth;
                    cp += cchText;
                    continue;
                }

                // Measure the run (ideal char widths), capped to the remaining column width. This is
                // also what records how far formatting has measured (FullText.CpMeasured), so it runs
                // for every text run, including the glyph-based ones whose widths Place replaces.
                int[] charWidths = new int[cchText];
                int totalWidth = 0, fitted = 0;
                fixed (char* pText = presText ?? runText)
                fixed (int* pCw = charWidths)
                {
                    cb.GetRunCharWidths(ploc, plsrun, LsDevice.Presentation, pText, cchText,
                        column == int.MaxValue ? int.MaxValue : Math.Max(0, column - penX),
                        LsTFlow.lstflowES, pCw, ref totalWidth, ref fitted);
                }

                // Shape the run onto the line -- together with the runs before it that it shares a
                // glyph chunk with, which can re-measure those too -- and fit what it now measures.
                ManagedLsRun prev = line.Runs.Count > 0 ? line.Runs[^1] : null;
                var run = new ManagedLsRun
                {
                    Plsrun = plsrun, PlsrunPtr = plsrunPtr, CpFirst = cp, CchText = cchText, Text = runText,
                    Pres = presText, IsText = true, BidiLevel = bidiLevel, GlyphBased = glyphBased,
                    JoinsPrevious = glyphBased && prev != null && prev.IsText && prev.GlyphBased
                                    && ShapesWith(cb, ploc, prev.Plsrun, plsrun),
                    DrawSpaces = dnode == DnodeKind.SpecialSpace,
                };
                line.Runs.Add(run);
                Place(cb, ploc, line, line.Runs.Count - 1, charWidths);
                int runPen = run.PenX;

                int consume = cchText;
                if (column != int.MaxValue && runPen + run.Width > column)
                {
                    // How many characters actually fit. GetRunCharWidths' stringLengthFitted counts
                    // the first character that CROSSES the boundary as well (LS's own contract: it
                    // adds the width, then tests), so using it as a break position overhangs the
                    // column by one character. Harmless for Latin, where the break lands on the
                    // preceding space, but for scripts with no spaces -- CJK above all -- the break
                    // IS that position, and every wrapped line spilled one ideograph past its edge.
                    int fits = 0;
                    for (int w = runPen; fits < cchText && w + run.CharWidths[fits] <= column; fits++)
                        w += run.CharWidths[fits];

                    // Trailing whitespace is not drawn, so a space that only just overflows still
                    // counts as fitting. Without this the break opportunity it carries falls outside
                    // the window and the word before it moves to the next line for no visible reason.
                    while (fits < cchText && IsBreakableSpace(runText[fits])) fits++;

                    // Break at the last opportunity within what fits.
                    int brk = FindBreak(runText, fits);
                    if (brk <= 0)
                    {
                        if (line.Runs.Count == 1 && runPen == 0)
                        {
                            // Emergency: guarantee forward progress with at least one character.
                            brk = Math.Max(1, Math.Min(fits, cchText));
                        }
                        else
                        {
                            // The run goes to the next line whole: take it off this one, and give the
                            // run it was shaped with back the shape it had without it.
                            penX = Unplace(cb, ploc, line);

                            // Nothing more fits and this run carries no break opportunity of its own.
                            // Runs end wherever the formatting or the script changes, not where a line
                            // may break, so ending the line at THIS run boundary would break wherever
                            // the runs happen to meet -- e.g. a bold "," that no longer fits would start
                            // the next line. Backtrack to the last real break opportunity already on the line
                            // (which also returns the runs after it to the next line); only if the line
                            // holds no opportunity at all does it end at the run boundary.
                            BackTrackToBreak(cb, ploc, line, ref cp, ref penX);
                            break;
                        }
                    }
                    consume = brk;
                    lineFull = true;
                }

                if (consume <= 0)
                {
                    penX = Unplace(cb, ploc, line);
                    break;
                }

                if (consume < cchText)
                {
                    run.Text = runText[..consume];
                    run.Pres = presText?[..consume];
                    run.CchText = consume;
                    Place(cb, ploc, line, line.Runs.Count - 1, charWidths);
                }

                LsTxM txm = new LsTxM();
                cb.GetRunTextMetrics(ploc, plsrun, LsDevice.Presentation, LsTFlow.lstflowES, ref txm);
                lineAscent = Math.Max(lineAscent, txm.dvAscent);
                lineDescent = Math.Max(lineDescent, txm.dvDescent);
                run.Ascent = txm.dvAscent;
                run.Descent = txm.dvDescent;

                penX = run.PenX + run.Width;
                cp += consume;
            }

            // The last right/centre/decimal tab is settled by the text after it on the line.
            penX = ResolvePendingTab(line, ref pendingTab, penX, column);

            // A blank line still needs a height; fall back to the paragraph's line metrics.
            if (lineAscent == 0 && lineDescent == 0)
            {
                LsTxM txm = new LsTxM();
                if (cb.GetRunTextMetrics(ploc, (Plsrun)(uint)TextStore.ObjectId.Text_chp,
                        LsDevice.Presentation, LsTFlow.lstflowES, ref txm) == LsErr.None && txm.dvAscent > 0)
                {
                    lineAscent = txm.dvAscent;
                    lineDescent = txm.dvDescent;
                }
            }

            // Trailing whitespace hangs past the end of the line: it is not drawn, does not count
            // towards the line's width, and must not be stretched by justification.
            int trailingWidth = TrailingWhitespaceWidth(line);

            // Justify BEFORE reordering, because reordering derives every run's position from the
            // run widths this adjusts. Only a line that WRAPPED is justified: `forced` means the
            // line ended at a hard break or ran out of text, which makes it the last line of its
            // paragraph, and stretching that one is what turns a two-word final line into two words
            // at opposite edges of the column.
            if (pap.fJustify != 0 && column != int.MaxValue && !forced)
            {
                JustifyLine(line, column, penX - trailingWidth);
            }

            // Runs were accumulated in logical order with a running pen; now put them where they
            // actually go. Everything downstream (drawing, caret, hit-testing) reads run.PenX, so
            // this is the single place the visual order is decided.
            int lineWidth = ReorderRunsVisually(line, baseLevel);

            // How deep the sublines go -- one per embedding level above the paragraph's -- which is
            // the size of the subline arrays FullTextLine queries with.
            foreach (ManagedLsRun r in line.Runs)
            {
                maxDepth = Math.Max(maxDepth, r.BidiLevel - baseLevel + 1);
            }

            line.Callbacks = cb;
            line.CpLim = cp;
            line.Ascent = lineAscent;
            line.Descent = lineDescent;
            line.Width = lineWidth;
            line.WidthNoTrailing = lineWidth - trailingWidth;
            line.ForcedBreak = forced;

            plslineInfo.dvpAscent = plslineInfo.dvrAscent = lineAscent;
            plslineInfo.dvpDescent = plslineInfo.dvrDescent = lineDescent;
            plslineInfo.dvpMultiLineHeight = plslineInfo.dvrMultiLineHeight = lineAscent + lineDescent;
            plslineInfo.cpLimToContinue = cp;
            plslineInfo.cpLimToStay = cp;
            plslineInfo.cpFirstVis = cpFirst;
            plslineInfo.endr = forced ? LsEndRes.endrEndPara : LsEndRes.endrNormal;
            plslineInfo.fForcedBreak = forced ? 1 : 0;

            lineWidths.upStartMainText = 0;
            lineWidths.upStartTrailing = lineWidth - trailingWidth;
            lineWidths.upLimLine = lineWidth;
            lineWidths.upMinStartTrailing = lineWidth - trailingWidth;
            lineWidths.upMinLimLine = lineWidth;

            pploline = GCHandle.ToIntPtr(GCHandle.Alloc(line));
            return LsErr.None;
        }

        /// <summary>
        ///  Assigns every run its visual x, reordering by bidi level along the way.
        /// </summary>
        /// <remarks>
        ///  <para>
        ///   This is the Unicode bidirectional algorithm's rule L2, over runs rather than characters:
        ///   from the highest level present down to the lowest odd level, reverse every contiguous
        ///   sequence of runs at that level or above. Reversing at successive levels composes into
        ///   the nesting the embeddings describe, which is why one loop handles arbitrary depth.
        ///  </para>
        ///  <para>
        ///   The run LIST stays in logical order and only <see cref="ManagedLsRun.PenX"/> changes.
        ///   Everything that looks a run up does so by cp -- the caret, selection bounds,
        ///   hit-testing -- and a logically ordered list keeps all of that a simple scan; the visual
        ///   order is a property of where each run is drawn, not of the collection.
        ///  </para>
        ///  <para>
        ///   An RTL paragraph needs no special case: its base level is 1, so every run is at level 1
        ///   or above and the outermost pass reverses the whole line, which is exactly what "the line
        ///   reads right to left" means.
        ///  </para>
        /// </remarks>
        /// <returns>The total width of the line: where the pen ends up.</returns>
        private static int ReorderRunsVisually(ManagedLsLine line, int baseLevel)
        {
            int count = line.Runs.Count;
            if (count == 0)
            {
                return 0;
            }

            int highest = baseLevel;
            int lowestOdd = int.MaxValue;
            foreach (ManagedLsRun run in line.Runs)
            {
                if (run.BidiLevel > highest) highest = run.BidiLevel;
                if ((run.BidiLevel & 1) != 0 && run.BidiLevel < lowestOdd) lowestOdd = run.BidiLevel;
            }

            // order[i] is the index of the run that occupies visual slot i.
            var order = new int[count];
            for (int i = 0; i < count; i++) order[i] = i;

            if (lowestOdd != int.MaxValue)
            {
                for (int level = highest; level >= lowestOdd; level--)
                {
                    int spanStart = -1;
                    for (int i = 0; i <= count; i++)
                    {
                        bool atOrAbove = i < count && line.Runs[order[i]].BidiLevel >= level;

                        if (atOrAbove && spanStart < 0)
                        {
                            spanStart = i;
                        }
                        else if (!atOrAbove && spanStart >= 0)
                        {
                            Array.Reverse(order, spanStart, i - spanStart);
                            spanStart = -1;
                        }
                    }
                }
            }

            int penX = 0;
            for (int i = 0; i < count; i++)
            {
                ManagedLsRun run = line.Runs[order[i]];
                run.PenX = penX;
                penX += run.Width;
            }
            return penX;
        }

        /// <summary>
        ///  Stretches a line to fill its column by widening the spaces between words.
        /// </summary>
        /// <remarks>
        ///  <para>
        ///   Justification is expressed in the glyph ADVANCES rather than in the run positions,
        ///   because everything downstream measures from them: the run widths feed the reordering
        ///   pass, which feeds drawing and the caret. Widening a space glyph therefore moves every
        ///   following word and keeps hit-testing consistent for free.
        ///  </para>
        ///  <para>
        ///   Only inter-word spaces expand. Widening letters instead ("letter-spacing") is a
        ///   different typographic effect and a worse-looking one; LS reserves inter-character
        ///   expansion for scripts with no spaces to expand, which this does not attempt.
        ///  </para>
        ///  <para>
        ///   The slack is spread in whole ideal units with the remainder going to the leftmost gaps,
        ///   so the line lands on exactly the column width rather than a rounding error short of it.
        ///  </para>
        /// </remarks>
        private static void JustifyLine(ManagedLsLine line, int column, int textWidth)
        {
            int slack = column - textWidth;
            if (slack <= 0)
            {
                return;
            }

            // The expansion points, as (run, glyph, char) triples. Trailing whitespace is excluded:
            // it hangs past the end of the line and stretching it would push the last word left of
            // the edge. A run can hold several words and the spaces after the last of them, so the
            // cut-off is the line's last non-space CHARACTER, not its last non-space run.
            var points = new List<(ManagedLsRun Run, int Glyph, int Char)>();
            (int lastRun, int lastChar) = LastInk(line);
            if (lastRun < 0)
            {
                return;
            }

            for (int r = 0; r <= lastRun; r++)
            {
                ManagedLsRun run = line.Runs[r];
                if (!run.IsText || run.Text == null) continue;

                int end = r == lastRun ? lastChar : Math.Min(run.CchText, run.Text.Length);
                for (int i = 0; i < end; i++)
                {
                    if (!IsBreakableSpace(run.Text[i])) continue;

                    int glyph = i < run.ClusterMap.Length ? run.ClusterMap[i] : -1;
                    if (glyph >= 0 && glyph < run.Advances.Length)
                    {
                        points.Add((run, glyph, i));
                    }
                }
            }

            if (points.Count == 0)
            {
                return;
            }

            int share = slack / points.Count;
            int remainder = slack - share * points.Count;

            for (int i = 0; i < points.Count; i++)
            {
                int add = share + (i < remainder ? 1 : 0);
                if (add == 0) continue;

                (ManagedLsRun run, int glyph, int ch) = points[i];
                run.Advances[glyph] += add;
                run.Width += add;
                if (run.CharWidths != null && ch < run.CharWidths.Length) run.CharWidths[ch] += add;
            }
        }

        /// <summary>The width of the whitespace the line ends with, which hangs past its edge.</summary>
        private static int TrailingWhitespaceWidth(ManagedLsLine line)
        {
            int width = 0;
            for (int r = line.Runs.Count - 1; r >= 0; r--)
            {
                ManagedLsRun run = line.Runs[r];
                if (!run.IsText || run.Text == null)
                {
                    continue;   // a break or control run carries no width either way
                }
                for (int i = Math.Min(run.CchText, run.Text.Length) - 1; i >= 0; i--)
                {
                    if (!IsBreakableSpace(run.Text[i]))
                    {
                        return width;
                    }
                    width += run.CharWidths != null && i < run.CharWidths.Length ? run.CharWidths[i] : 0;
                }
            }
            return width;
        }

        /// <summary>
        ///  The line's last character that is not a breakable space, as (run index, character index
        ///  within the run); (-1, -1) when the line holds none.
        /// </summary>
        private static (int Run, int Char) LastInk(ManagedLsLine line)
        {
            for (int r = line.Runs.Count - 1; r >= 0; r--)
            {
                ManagedLsRun run = line.Runs[r];
                if (!run.IsText || run.Text == null) continue;
                for (int i = Math.Min(run.CchText, run.Text.Length) - 1; i >= 0; i--)
                {
                    if (!IsBreakableSpace(run.Text[i])) return (r, i);
                }
            }
            return (-1, -1);
        }

        // True if the run is a line/paragraph break: its leading character is a separator marker
        // (U+2028 line separator, U+2029 paragraph separator, LF, CR).
        private static bool IsLineOrParaBreak(char[] text)
        {
            if (text == null || text.Length == 0) return false;
            char c = text[0];
            return c == '\u2028' || c == '\u2029' || c == '\n' || c == '\r';
        }

        // Greedy break: the last position within [0..limit] at which the line may be cut, or 0 if
        // there is none. Two kinds of opportunity are considered, and the LAST of either wins:
        //
        //   * just after a breakable space (Latin and friends), and
        //   * between two characters where at least one is ideographic (Han, kana, Hangul, the
        //     fullwidth forms). CJK writes without spaces, so this is the only opportunity such a
        //     paragraph offers -- without it a Japanese TextBlock had no legal break anywhere and
        //     only wrapped by way of the caller's emergency "at least one character" path, which
        //     cannot honour kinsoku and cut wherever the column happened to land.
        private static int FindBreak(char[] text, int limit)
        {
            int n = Math.Min(limit, text.Length);
            for (int i = n; i > 0; i--)
            {
                if (IsBreakableSpace(text[i - 1]))
                    return i;
                if (CanBreakBetween(text, i))
                    return i;
            }
            return 0;
        }

        // A space that a line may break after. U+00A0 (no-break space) deliberately is NOT one --
        // that is the whole point of the character.
        private static bool IsBreakableSpace(char c)
            => c == ' ' || c == '\t' || c == '\u2003' || c == '\u2002';

        // True if the line may be cut between text[i-1] and text[i] on ideographic grounds, i.e.
        // one of the two is CJK and neither kinsoku rule forbids the cut. Callers scan backwards, so
        // a forbidden position simply moves the offending character down to the next line ("oidashi").
        private static bool CanBreakBetween(char[] text, int i)
        {
            if (i <= 0 || i >= text.Length) return false;

            char prev = text[i - 1];
            char next = text[i];

            // Never split a surrogate pair or separate a combining mark from its base.
            if (char.IsHighSurrogate(prev)) return false;
            if (char.IsLowSurrogate(next)) return false;
            if (CharUnicodeInfo.GetUnicodeCategory(next) is UnicodeCategory.NonSpacingMark
                or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark) return false;

            // At least one side has to be ideographic; between two Latin letters the only legal
            // break is at a space, which the caller has already looked for.
            if (!IsIdeographic(prev) && !IsIdeographic(next)) return false;

            // Kinsoku shori: characters that may not begin a line (closing brackets, the CJK commas
            // and stops, small kana, the prolonged sound mark, iteration marks) and characters that
            // may not end one (opening brackets, currency signs that lead their amount).
            if (IsProhibitedLineStart(next)) return false;
            if (IsProhibitedLineEnd(prev)) return false;

            return true;
        }

        // Han, kana, Hangul, bopomofo, the CJK symbol/punctuation block and the fullwidth forms --
        // the scripts that break between characters rather than between words.
        private static bool IsIdeographic(char c)
            => c is >= '\u1100' and <= '\u11ff'      // Hangul Jamo
                or >= '\u2e80' and <= '\u2fdf'       // CJK radicals / Kangxi radicals
                or >= '\u3000' and <= '\u303f'       // CJK symbols and punctuation
                or >= '\u3040' and <= '\u30ff'       // Hiragana, Katakana
                or >= '\u3100' and <= '\u312f'       // Bopomofo
                or >= '\u3130' and <= '\u318f'       // Hangul compatibility Jamo
                or >= '\u31c0' and <= '\u31ef'       // CJK strokes
                or >= '\u31f0' and <= '\u31ff'       // Katakana phonetic extensions
                or >= '\u3200' and <= '\u4dbf'       // enclosed CJK letters, CJK extension A
                or >= '\u4e00' and <= '\u9fff'       // CJK unified ideographs
                or >= '\ua960' and <= '\ua97f'       // Hangul Jamo extended-A
                or >= '\uac00' and <= '\ud7ff'       // Hangul syllables, Jamo extended-B
                or >= '\uf900' and <= '\ufaff'       // CJK compatibility ideographs
                or >= '\ufe30' and <= '\ufe4f'       // CJK compatibility forms
                or >= '\uff00' and <= '\uff60'       // fullwidth forms
                or >= '\uffe0' and <= '\uffe6'       // fullwidth signs
                || char.IsHighSurrogate(c);          // SIP: CJK extension B and beyond

        // May not start a line (UAX #14 classes CL and NS, plus the fullwidth EX/IS punctuation):
        // the closing brackets, the ideographic comma and full stop, the small kana, the prolonged
        // sound mark and the iteration marks -- all of them trail what precedes them.
        private static bool IsProhibitedLineStart(char c)
            => c is '\u3001' or '\u3002'                                   // ideographic comma, full stop
                or '\uff0c' or '\uff0e' or '\uff1a' or '\uff1b'            // fullwidth , . : ;
                or '\uff01' or '\uff1f'                                    // fullwidth ! ?
                or '\u30fb' or '\uff65'                                    // katakana middle dot
                or '\u2019' or '\u201d'                                    // closing curly quotes
                or '\u3009' or '\u300b' or '\u300d' or '\u300f'            // closing angle/corner brackets
                or '\u3011' or '\u3015' or '\u3017' or '\u3019' or '\u301b'
                or '\uff09' or '\uff3d' or '\uff5d' or '\uff60'            // fullwidth ) ] } closing
                or '\uff63' or '\uff64'                                    // halfwidth corner bracket, comma
                or '\u30fc' or '\u301c' or '\uff5e'                        // prolonged sound mark, wave dashes
                or '\u3005' or '\u303b' or '\u309d' or '\u309e'            // iteration marks
                or '\u30fd' or '\u30fe'
                or '\u3063' or '\u30c3'                                    // small tsu
                || IsSmallKana(c);

        // The small kana, which modify the syllable before them and so may not lead a line.
        private static bool IsSmallKana(char c)
            => c is '\u3041' or '\u3043' or '\u3045' or '\u3047' or '\u3049'   // small a i u e o
                or '\u3083' or '\u3085' or '\u3087' or '\u308e'                // small ya yu yo wa
                or '\u3095' or '\u3096'                                        // small ka ke
                or '\u30a1' or '\u30a3' or '\u30a5' or '\u30a7' or '\u30a9'    // katakana equivalents
                or '\u30e3' or '\u30e5' or '\u30e7' or '\u30ee'
                or '\u30f5' or '\u30f6'
                or >= '\uff67' and <= '\uff6f';                                // halfwidth small kana

        // May not end a line (UAX #14 class OP): the opening brackets and quotes, and the currency
        // signs that lead their amount.
        private static bool IsProhibitedLineEnd(char c)
            => c is '\u3008' or '\u300a' or '\u300c' or '\u300e'            // opening angle/corner brackets
                or '\u3010' or '\u3014' or '\u3016' or '\u3018' or '\u301a'
                or '\uff08' or '\uff3b' or '\uff5b' or '\uff5f'             // fullwidth ( [ { opening
                or '\uff62'                                                 // halfwidth opening corner bracket
                or '\u2018' or '\u201c'                                     // opening curly quotes
                or '\uffe1' or '\uffe5' or '\uff04';                        // fullwidth pound, yen, dollar

        // Ends the line at the last break opportunity among the runs already placed on it, dropping
        // (and, where the opportunity falls inside a run, truncating) everything after it so those
        // characters format onto the next line. Returns false -- leaving the line untouched -- when
        // the line holds no break opportunity at all, in which case the caller's run-boundary break
        // stands as the last resort.
        private static unsafe bool BackTrackToBreak(
            LineServicesCallbacks cb, IntPtr ploc, ManagedLsLine line, ref int cp, ref int penX)
        {
            for (int i = line.Runs.Count - 1; i >= 0; i--)
            {
                ManagedLsRun r = line.Runs[i];
                if (!r.IsText || r.Text == null) continue;

                int brk = FindBreak(r.Text, r.CchText);
                if (brk <= 0) continue;                       // no opportunity in this run

                bool nextJoined = i + 1 < line.Runs.Count && line.Runs[i + 1].JoinsPrevious;

                if (i + 1 < line.Runs.Count)
                    line.Runs.RemoveRange(i + 1, line.Runs.Count - i - 1);

                if (brk < r.CchText)
                {
                    if (!TruncateRun(cb, ploc, line, i, brk)) return false;
                }
                else if (nextJoined)
                {
                    // The run it was shaped with has gone to the next line, and so has anything that
                    // run's text did to this one's glyphs (a kerning pair across the boundary).
                    Place(cb, ploc, line, i, null);
                }

                cp = r.CpFirst + r.CchText;
                penX = r.PenX + r.Width;
                return true;
            }
            return false;
        }

        // Cuts a placed run down to its first `keep` characters, re-measuring and re-shaping so the
        // glyphs and advances match the characters that remain on the line. The run must be the
        // line's last.
        private static unsafe bool TruncateRun(
            LineServicesCallbacks cb, IntPtr ploc, ManagedLsLine line, int index, int keep)
        {
            ManagedLsRun r = line.Runs[index];
            char[] text = r.Shaped[..keep];
            int[] charWidths = new int[keep];
            int totalWidth = 0, fitted = 0;
            fixed (char* pText = text)
            fixed (int* pCw = charWidths)
            {
                if (cb.GetRunCharWidths(ploc, r.Plsrun, LsDevice.Presentation, pText, keep, int.MaxValue,
                                        LsTFlow.lstflowES, pCw, ref totalWidth, ref fitted) != LsErr.None)
                {
                    return false;
                }
            }

            r.Text = r.Text[..keep];
            r.Pres = r.Pres?[..keep];
            r.CchText = keep;
            Place(cb, ploc, line, index, charWidths);
            return true;
        }

        // ---- dnodes (LSTXTFMT.C) ----

        // Special: a FormatSpecial dnode of kind 0/1 (shaped like text); SpecialUnshaped: kind 5/6
        // (U+00A0, U+2011); SpecialSpace: kind 8, the typographic spaces, drawn as wchSpace.
        private enum DnodeKind { Regular, Spaces, Special, SpecialUnshaped, SpecialSpace, OneChar, Empty, Tab }

        // No dnode holds more characters than this: FormatRegularCharacters, FormatSpaces and
        // FormatSpecial all format at most 0x7D of a run's characters at a time.
        private const int MaxDnodeCharacters = 0x7D;

        /// <summary>
        ///  The class LineServices files a character under (LsSetTextConfig's TxtAddSpec calls, over
        ///  the special characters TextFormatterContext.Init configures), or 0 for an ordinary one.
        /// </summary>
        private static int SpecialClass(char c) => c switch
        {
            '\u0000' => 0x10,     // wchNull
            ' ' => 0x01,          // wchSpace
            '\t' => 0x02,         // wchTab
            '\u2029' => 0x04,     // wchEndPara1 (and wchEscAnmRun)
            '\u2028' => 0x07,     // wchEndLineInPara
            '\u00A0' => 0x0B,     // wchNonBreakSpace
            '\u2011' => 0x0C,     // wchNonBreakHyphen
            '\u00AD' => 0x0D,     // wchNonReqHyphen
            '\u2003' => 0x0E,     // wchEmSpace
            '\u2002' => 0x0F,     // wchEnSpace
            '-' or '\u2013' or '\u2014' => 0x11,   // wchHyphen, wchEnDash, wchEmDash
            '\u2009' => 0x12,     // wchNarrowSpace
            '\u3000' => 0x15,     // wchFESpace
            '\u200D' => 0x16,     // wchJoiner
            '\u200C' => 0x17,     // wchNonJoiner
            _ => 0,
        };

        /// <summary>
        ///  How LsFmtText formats the start of a fetched text run: the kind and length of its first
        ///  dnode, and the character it is presented as ('\0' for the run's own characters).
        /// </summary>
        /// <remarks>
        ///  <list type="bullet">
        ///   <item>FormatRegularCharacters: ordinary characters and the spaces among them, up to the
        ///   first other special character.</item>
        ///   <item>FormatSpaces: a run that STARTS with spaces has them as a dnode of their own -- so
        ///   " abc" draws as two glyph runs, the space's and the word's, though they are one run of
        ///   text and shaped together.</item>
        ///   <item>FormatSpecial: a repeat of one special character -- a hyphen or dash, a no-break or
        ///   typographic space -- presented as the character its class is formatted with.</item>
        ///   <item>FormatStartTab / FormatStartEmptyDobj / FormatStartOneRegularChar: one character.</item>
        ///  </list>
        ///  A joiner inside glyph-based text is left in its run: its cluster belongs to the letters
        ///  around it, and a dnode boundary through a cluster would cut the shaping it exists for.
        /// </remarks>
        private static DnodeKind FirstDnode(char[] text, int cch, bool glyphBased, out int length, out char pres)
        {
            pres = '\0';
            int limit = Math.Min(cch, MaxDnodeCharacters);
            int cls = SpecialClass(text[0]);
            if (glyphBased && cls is 0x16 or 0x17) cls = 0;
            switch (cls)
            {
                case 0x01:
                    length = 1;
                    while (length < limit && text[length] == ' ') length++;
                    return DnodeKind.Spaces;
                case 0x02:
                    length = 1;
                    pres = ' ';
                    return DnodeKind.Tab;
                case 0x0D:
                    length = 1;
                    return DnodeKind.Empty;
                case 0x16:
                case 0x17:
                    length = 1;
                    return DnodeKind.OneChar;
                case 0x0B:
                case 0x0C:
                case 0x0E:
                case 0x0F:
                case 0x10:
                case 0x11:
                case 0x12:
                case 0x15:
                    // FormatSpecial(ref, pres, kind): U+00A0 is formatted as wchSpace (kind 5), U+2011
                    // as wchHyphen (6), the typographic spaces as themselves (8), the hyphen, the
                    // dashes (0 or 1) and wchNull (0) as themselves. A repeat of one character is one
                    // dnode. Only kinds 0 and 1 are text LS shapes; the others are drawn from their
                    // characters (LsDisplayText), and kind 8 as spaces.
                    pres = cls == 0x0B ? ' ' : cls == 0x0C ? '-' : '\0';
                    length = 1;
                    while (length < limit && text[length] == text[0]) length++;
                    return cls is 0x10 or 0x11 ? DnodeKind.Special
                         : cls is 0x0B or 0x0C ? DnodeKind.SpecialUnshaped
                         : DnodeKind.SpecialSpace;
            }

            length = 1;
            while (length < limit)
            {
                int c = SpecialClass(text[length]);
                if (glyphBased && c is 0x16 or 0x17) c = 0;
                if (c != 0 && c != 0x01) break;
                length++;
            }
            return DnodeKind.Regular;
        }

        // ---- tabs (TABUTILS.C) ----

        // A right, centre or decimal tab waits for the text after it before it knows its width.
        private struct PendingTab
        {
            public int RunIndex;            // the tab's run, -1 when none is pending
            public LsKTab Kind;
            public int Ur;                  // the tab stop
            public int UrBefore;            // the pen where the tab began
            public bool Active;
        }

        /// <summary>
        ///  FindTab + LsGetCurTabInfoCore: the stop a tab at <paramref name="pen"/> reaches -- the
        ///  first of the paragraph's stops beyond the pen, else the next multiple of the incremental
        ///  tab -- and the tab's width. A left tab's width is known now; any other kind is pending and
        ///  zero wide until <see cref="ResolvePendingTab"/>.
        /// </summary>
        private static int BeginTab(ManagedLsContext ctx, int runIndex, int pen, ref PendingTab pending)
        {
            LsKTab kind = LsKTab.lsktLeft;
            int ur = 0;
            bool found = false;
            foreach (LsTbd tbd in ctx.Tabs)
            {
                if (pen < tbd.ur)
                {
                    kind = tbd.lskt;
                    ur = tbd.ur;
                    found = true;
                    break;
                }
            }
            if (!found)
            {
                int inc = ctx.IncrementalTab == 0 ? 1 : ctx.IncrementalTab;
                ur = ((pen < 0 ? 0 : inc) + pen) / inc * inc;
            }

            if (kind == LsKTab.lsktLeft)
            {
                return ur - pen;
            }

            pending = new PendingTab { RunIndex = runIndex, Kind = kind, Ur = ur, UrBefore = pen, Active = true };
            return 0;
        }

        /// <summary>
        ///  LsResolvePrevTabCore: gives the pending tab the width that puts the text after it where
        ///  its kind says (ending at the stop, or centred on it), moves that text along, and returns
        ///  the pen after it.
        /// </summary>
        private static int ResolvePendingTab(ManagedLsLine line, ref PendingTab pending, int pen, int column)
        {
            if (!pending.Active) return pen;
            int index = pending.RunIndex;
            pending.Active = false;
            if (index < 0 || index >= line.Runs.Count) return pen;

            int text = pen - pending.UrBefore;
            int trailing = 0;
            for (int r = line.Runs.Count - 1; r > index; r--)
            {
                ManagedLsRun run = line.Runs[r];
                if (!run.IsText || run.Text == null) continue;
                bool ink = false;
                for (int i = Math.Min(run.CchText, run.Text.Length) - 1; i >= 0; i--)
                {
                    if (run.Text[i] != ' ') { ink = true; break; }
                    trailing += run.CharWidths != null && i < run.CharWidths.Length ? run.CharWidths[i] : 0;
                }
                if (ink) break;
            }
            if (pending.Kind is LsKTab.lsktCenter or LsKTab.lsktRight)
            {
                text -= trailing;
                if (pending.Kind == LsKTab.lsktCenter) text /= 2;
            }

            int width = pending.Ur - text - pending.UrBefore;
            if (pending.Ur < column && column < width - trailing + pen)
            {
                width = column - pen + trailing;
            }
            if (width <= 0) return pen;

            ManagedLsRun tab = line.Runs[index];
            tab.Width = width;
            tab.CharWidths = new[] { width };
            for (int r = index + 1; r < line.Runs.Count; r++)
            {
                line.Runs[r].PenX += width;
            }
            return pen + width;
        }

        /// <summary>Whether two neighbouring runs are shaped as one glyph chunk.</summary>
        private static bool ShapesWith(LineServicesCallbacks cb, IntPtr ploc, Plsrun first, Plsrun second)
        {
            int interrupt = 1;
            return cb.FInterruptShaping(ploc, LsTFlow.lstflowES, first, second, ref interrupt) == LsErr.None
                && interrupt == 0;
        }

        /// <summary>The index of the first run of the glyph chunk that run <paramref name="index"/> is in.</summary>
        private static int ChunkStart(ManagedLsLine line, int index)
        {
            while (index > 0 && line.Runs[index].JoinsPrevious) index--;
            return index;
        }

        /// <summary>
        ///  Shapes run <paramref name="index"/> -- which ends the line so far -- and gives it its
        ///  glyphs, width and x. A glyph-based run is shaped together with the runs of its glyph
        ///  chunk, which re-measures those as well.
        /// </summary>
        /// <remarks>
        ///  <para>
        ///   This is how LineServices measures text: a glyph-based run is as wide as its glyphs
        ///   advance once shaped, and neighbouring runs that FInterruptShaping does not separate go
        ///   to GetGlyphs and GetGlyphPositions as ONE string. The width GetRunCharWidths reports is
        ///   each character's nominal hmtx advance, so measuring with it lost every kerning pair:
        ///   the ones inside a run moved glyphs without narrowing the run, and the ones across a run
        ///   boundary were never shaped at all. Every Arial, Times New Roman and Verdana line came out wider than stock WPF's by
        ///   exactly its kerning.
        ///  </para>
        ///  <para>
        ///   <paramref name="unshapedWidths"/> are GetRunCharWidths' widths for the run's characters,
        ///   which remain the measure of a run that is not glyph-based; null when the run is.
        ///  </para>
        /// </remarks>
        private static void Place(LineServicesCallbacks cb, IntPtr ploc, ManagedLsLine line, int index, int[] unshapedWidths)
        {
            ManagedLsRun run = line.Runs[index];
            int first = run.GlyphBased ? ChunkStart(line, index) : index;

            if (first < index && ShapeChunk(cb, ploc, line, first, index))
            {
                return;
            }

            // Shaped on its own: not glyph-based, the first of its chunk, or a chunk whose glyphs do
            // not divide cleanly between its runs (see ShapeChunk).
            run.JoinsPrevious = false;
            ShapeRuns(cb, ploc, new[] { run.PlsrunPtr }, new[] { run.CchText }, run.Shaped[..run.CchText],
                      out ushort[] glyphs, out ushort[] clusters, out ushort[] charProps,
                      out uint[] glyphProps, out int[] advances, out GlyphOffset[] offsets, out int glyphCount);

            run.Glyphs = glyphs;
            run.ClusterMap = clusters;
            run.CharProps = charProps;
            run.GlyphProps = glyphProps;
            run.Advances = advances;
            run.Offsets = offsets;
            run.GlyphCount = glyphCount;
            run.PenX = PenAfter(line, index - 1);

            if (run.GlyphBased || unshapedWidths == null)
            {
                run.CharWidths = CharWidthsFromClusters(clusters, run.CchText, glyphCount, advances);
                run.Width = Sum(advances, 0, glyphCount);
            }
            else
            {
                run.CharWidths = unshapedWidths[..run.CchText];
                run.Width = Sum(run.CharWidths, 0, run.CchText);
            }
        }

        /// <summary>
        ///  Takes the line's last run off it and re-places the run it shared a glyph chunk with.
        ///  Returns the pen position where the removed run started.
        /// </summary>
        private static int Unplace(LineServicesCallbacks cb, IntPtr ploc, ManagedLsLine line)
        {
            ManagedLsRun removed = line.Runs[^1];
            line.Runs.RemoveAt(line.Runs.Count - 1);
            if (removed.JoinsPrevious)
            {
                Place(cb, ploc, line, line.Runs.Count - 1, null);
            }
            return PenAfter(line, line.Runs.Count - 1);
        }

        private static int PenAfter(ManagedLsLine line, int index)
            => index < 0 ? 0 : line.Runs[index].PenX + line.Runs[index].Width;

        /// <summary>
        ///  Shapes runs <paramref name="first"/>..<paramref name="last"/> as one string and divides
        ///  the glyphs back between them by cluster. False, with every run untouched, when that
        ///  cannot be done cleanly: a cluster that spans two runs (a ligature across the boundary)
        ///  or a cluster map that is not in logical order.
        /// </summary>
        private static bool ShapeChunk(LineServicesCallbacks cb, IntPtr ploc, ManagedLsLine line, int first, int last)
        {
            int runCount = last - first + 1;
            var plsruns = new IntPtr[runCount];
            var cchs = new int[runCount];
            int total = 0;
            for (int k = 0; k < runCount; k++)
            {
                ManagedLsRun r = line.Runs[first + k];
                plsruns[k] = r.PlsrunPtr;
                cchs[k] = r.CchText;
                total += r.CchText;
            }

            var text = new char[total];
            for (int k = 0, at = 0; k < runCount; k++)
            {
                Array.Copy(line.Runs[first + k].Shaped, 0, text, at, cchs[k]);
                at += cchs[k];
            }

            ShapeRuns(cb, ploc, plsruns, cchs, text,
                      out ushort[] glyphs, out ushort[] clusters, out ushort[] charProps,
                      out uint[] glyphProps, out int[] advances, out GlyphOffset[] offsets, out int glyphCount);

            for (int c = 1; c < total; c++)
            {
                if (clusters[c] < clusters[c - 1]) return false;
            }
            if (total == 0 || clusters[total - 1] >= glyphCount) return false;
            for (int k = 1, at = cchs[0]; k < runCount; at += cchs[k], k++)
            {
                if (clusters[at] == clusters[at - 1]) return false;
            }

            int pen = line.Runs[first].PenX;
            for (int k = 0, cs = 0; k < runCount; cs += cchs[k], k++)
            {
                ManagedLsRun r = line.Runs[first + k];
                int ce = cs + cchs[k];
                int gs = clusters[cs];
                int ge = ce < total ? clusters[ce] : glyphCount;
                int n = ge - gs;

                r.Glyphs = glyphs[gs..ge];
                r.GlyphProps = glyphProps[gs..ge];
                r.Advances = advances[gs..ge];
                r.Offsets = offsets[gs..ge];
                r.GlyphCount = n;
                r.CharProps = charProps[cs..ce];
                r.ClusterMap = new ushort[cchs[k]];
                for (int c = 0; c < cchs[k]; c++) r.ClusterMap[c] = (ushort)(clusters[cs + c] - gs);
                r.CharWidths = CharWidthsFromClusters(r.ClusterMap, cchs[k], n, r.Advances);
                r.Width = Sum(r.Advances, 0, n);
                r.PenX = pen;
                pen += r.Width;
            }
            return true;
        }

        /// <summary>
        ///  Each character's share of its cluster's advance: split evenly, the remainder on the
        ///  cluster's first character, so a run's characters always add up to the run.
        /// </summary>
        private static int[] CharWidthsFromClusters(ushort[] clusters, int charCount, int glyphCount, int[] advances)
        {
            var widths = new int[charCount];
            int c = 0;
            while (c < charCount)
            {
                int end = c + 1;
                while (end < charCount && clusters[end] == clusters[c]) end++;
                int g0 = Math.Min(clusters[c], glyphCount);
                int g1 = Math.Max(g0, Math.Min(end < charCount ? clusters[end] : glyphCount, glyphCount));
                int w = Sum(advances, g0, g1);
                int each = w / (end - c);
                for (int i = c; i < end; i++) widths[i] = each;
                widths[c] += w - each * (end - c);
                c = end;
            }
            return widths;
        }

        private static int Sum(int[] values, int from, int to)
        {
            int s = 0;
            for (int i = from; i < to; i++) s += values[i];
            return s;
        }

        private static unsafe void ShapeRuns(
            LineServicesCallbacks cb, IntPtr ploc, IntPtr[] plsruns, int[] cchs, char[] text,
            out ushort[] glyphs, out ushort[] clusters, out ushort[] charProps,
            out uint[] glyphProps, out int[] advances, out GlyphOffset[] offsets, out int glyphCount)
        {
            int cch = text.Length;
            int capacity = cch * 3 + 16;
            var clusterBuf = new ushort[cch];
            var charPropBuf = new ushort[cch];
            var canAlone = new int[cch];

            ushort[] glyphBuf;
            uint[] glyphPropBuf;
            int gc;

            // GetGlyphs is allowed to need more glyphs than the buffer holds -- shaping can turn one
            // glyph into several -- and says so by leaving fBuffersUsed clear and reporting the count
            // it needs. Honouring that is not optional: the buffers hold nothing meaningful in that
            // case, and reading them anyway yields a run of zeros. The loop runs twice in the worst
            // case (the second capacity is the exact count the shaper asked for), and the guard is
            // there so a backend that never sets the flag cannot spin.
            for (int attempt = 0; ; attempt++)
            {
                glyphBuf = new ushort[capacity];
                glyphPropBuf = new uint[capacity];
                gc = capacity;
                int fBuffersUsed = 0;

                fixed (IntPtr* pPlsruns = plsruns)
                fixed (int* pCchs = cchs)
                fixed (char* pText = text)
                fixed (ushort* pGlyphs = glyphBuf)
                fixed (uint* pGlyphProps = glyphPropBuf)
                fixed (ushort* pCluster = clusterBuf)
                fixed (ushort* pCharProps = charPropBuf)
                fixed (int* pCanAlone = canAlone)
                {
                    cb.GetGlyphsRedefined(ploc, pPlsruns, pCchs, plsruns.Length, pText, cch, LsTFlow.lstflowES,
                        pGlyphs, pGlyphProps, capacity, ref fBuffersUsed, pCluster, pCharProps, pCanAlone, ref gc);
                }

                if (fBuffersUsed != 0 || gc <= capacity || attempt >= 1)
                {
                    break;
                }

                capacity = gc;
            }

            if (gc > capacity)
            {
                gc = capacity;   // the backend never filled a buffer this large; take what fits
            }

            glyphCount = gc;
            glyphs = new ushort[gc];
            glyphProps = new uint[gc];
            Array.Copy(glyphBuf, glyphs, gc);
            Array.Copy(glyphPropBuf, glyphProps, gc);
            clusters = clusterBuf;
            charProps = charPropBuf;

            advances = new int[gc];
            offsets = new GlyphOffset[gc];

            fixed (IntPtr* pPlsruns = plsruns)
            fixed (int* pCchs = cchs)
            fixed (char* pText = text)
            fixed (ushort* pCluster = clusters)
            fixed (ushort* pCharProps = charProps)
            fixed (ushort* pGlyphs = glyphs)
            fixed (uint* pGlyphProps = glyphProps)
            fixed (int* pAdvances = advances)
            fixed (GlyphOffset* pOffsets = offsets)
            {
                cb.GetGlyphPositions(ploc, pPlsruns, pCchs, plsruns.Length, LsDevice.Presentation, pText,
                    pCluster, pCharProps, cch, pGlyphs, pGlyphProps, gc, LsTFlow.lstflowES, pAdvances, pOffsets);

                // Shaped: from here on a combining mark is part of its base's cluster (carets, cells,
                // the glyph run's cluster map). Not before -- GSUB's per-character features and GPOS's
                // mark-to-ligature components both read which glyph each character became.
                ManagedOpenTypeShaper.MergeMarkClusters(pText, cch, pCluster, gc);
            }
        }

        // ---- display: replay the glyph runs through the draw callbacks ----

        private static readonly int s_rtlCell = Environment.GetEnvironmentVariable("WPF_LS_RTLCELL") == "0" ? 0 : 1;

        internal static unsafe LsErr DisplayLine(IntPtr ploline, ref LSPOINT pt, uint displayMode, ref LSRECT clipRect)
        {
            ManagedLsLine line = LineFrom(ploline);
            // pt is the LS line reference origin, which callers pass as the BASELINE
            // position (FullTextLine passes (0, _metrics._baselineOffset)); native LS
            // anchors glyph runs at it directly. Adding line.Ascent here double-counted
            // the ascent and drew all shim-formatted text a full ascent too low.
            int baseline = pt.y;

            // The current FullTextLine sets up its Draw state (Draw.CurrentLine) before calling us;
            // the callbacks resolve the drawing context from there.
            LineServicesCallbacks cb = line.Callbacks;
            if (cb == null) return LsErr.None;

            foreach (ManagedLsRun run in line.Runs)
            {
                // A run that is not glyph-based is drawn as LS draws a text dnode, from its characters
                // and their widths (DrawTextRun -> ComputeUnshapedGlyphRun): the nominal fast-path
                // runs, symbol fonts, and every tab.
                bool unshaped = !run.GlyphBased || run.IsTab;
                if (!run.IsText || (unshaped ? run.CchText == 0 : run.GlyphCount == 0)) continue;

                // Run x-origin, which is the run's LEADING edge and therefore depends on the run's own
                // direction: a right-to-left run is anchored at its RIGHT edge and its glyphs march
                // leftward from there (see GlyphRun.BuildGeometry). Handing an RTL run its left edge
                // draws it one run-width too far left, on top of whatever precedes it.
                int leftEdge = pt.x + run.PenX;
                // LS positions are cells, laid out in the PARAGRAPH's direction, so a run that reads
                // against it starts in the last ideal unit it covers: an RTL run in an LTR paragraph
                // one unit short of its right edge (stock: 55.967 vs 55.970 at 12px, 74.623 vs 74.627
                // at 16 -- 1/300 DIP at every size), an LTR run in an RTL paragraph one unit past its
                // left edge (714.683 vs 714.680), and a run that reads with it exactly on its edge.
                int originEdge = run.IsRightToLeft ? leftEdge + run.Width : leftEdge;
                if (run.IsRightToLeft != line.RightToLeft) originEdge += line.RightToLeft ? s_rtlCell : -s_rtlCell;

                // In an RTL paragraph the line origin is the line's RIGHT edge, and LS expresses run
                // positions as negative offsets from it -- ComputeShapedGlyphRun negates what it gets.
                // So send the mirrored distance and let it undo the sign.
                int runX = line.RightToLeft ? originEdge - line.Width : originEdge;
                LSPOINT ptRun = new LSPOINT(runX, baseline);
                var lsHeights = new LsHeights { dvAscent = run.Ascent, dvDescent = run.Descent, dvMultiLineHeight = run.Ascent + run.Descent };
                var clip = clipRect;

                if (unshaped)
                {
                    int[] widths = run.CharWidths ?? new int[run.CchText];
                    char[] chars = run.Shaped;
                    if (run.DrawSpaces)
                    {
                        chars = new char[run.CchText];
                        Array.Fill(chars, ' ');
                    }
                    fixed (char* pText = chars)
                    fixed (int* pWidths = widths)
                    {
                        // An unshaped glyph run always reads left to right; LS hands a right-to-left
                        // dnode over with its subline's flow and its leading edge, and
                        // ComputeUnshapedGlyphRun steps back to the left edge from there.
                        cb.DrawTextRun(ploline, run.Plsrun, ref ptRun, pText, pWidths, run.CchText,
                            run.IsRightToLeft ? LsTFlow.lstflowWS : LsTFlow.lstflowES, displayMode,
                            ref ptRun, ref lsHeights, run.Width, ref clip);
                    }
                }
                else
                {
                    var expTypes = new LsExpType[run.GlyphCount];

                    fixed (char* pText = run.Shaped)
                    fixed (ushort* pCluster = run.ClusterMap)
                    fixed (ushort* pCharProps = run.CharProps)
                    fixed (ushort* pGlyphs = run.Glyphs)
                    fixed (int* pAdvances = run.Advances)
                    fixed (uint* pGlyphProps = run.GlyphProps)
                    fixed (GlyphOffset* pOffsets = run.Offsets)
                    fixed (LsExpType* pExp = expTypes)
                    {
                        cb.DrawGlyphs(ploline, run.Plsrun, pText, pCluster, pCharProps, run.CchText,
                            pGlyphs, pAdvances, pAdvances, pOffsets, pGlyphProps, pExp, run.GlyphCount,
                            LsTFlow.lstflowES, displayMode, ref ptRun, ref lsHeights, run.Width, ref clip);
                    }
                }

                // Native LS draws underline/strikethrough/overline/baseline during LoDisplayLine; replay
                // that here so paragraph-/run-level TextDecorations render off-Windows too. A
                // decoration is a rectangle, so it wants the run's LEFT edge whichever way the run
                // reads -- not the direction-dependent leading edge the glyphs are anchored at.
                int decorationX = line.RightToLeft ? leftEdge - line.Width : leftEdge;
                cb.DrawManagedTextDecorations(run.Plsrun, decorationX, baseline, run.Width, LsTFlow.lstflowES, displayMode, ref clip);
            }
            return LsErr.None;
        }

        // ---- hit-testing: cp <-> u, the way LineServices reports it ----
        //
        // LS answers queries in the MAIN subline's coordinate u, which runs in the paragraph's
        // direction from the line's start (its right edge in a right-to-left paragraph), and
        // describes each cell by the sublines that contain it: the main one, then one per bidi
        // embedding (the reverse objects), each flowing its own way. FullTextLine turns that into
        // carets, hit tests and selection bounds -- the leading edge of a cell in a run that reads
        // against the main flow is its far edge, a trailing hit walks backwards from there -- so
        // reporting every cell left-to-right from the line's left edge put the caret of every
        // right-to-left character, and every character of a right-to-left paragraph, on the wrong
        // side. A run reading against the paragraph sits one ideal unit inside its leading edge,
        // as it is drawn (DisplayLine).

        /// <summary>The main-direction u of the visual (left-based) span [x, x + w).</summary>
        private static void SpanU(ManagedLsLine line, bool against, int x, int w, out int u0, out int u1)
        {
            if (line.RightToLeft)
            {
                u0 = line.Width - x - w;
                u1 = line.Width - x;
            }
            else
            {
                u0 = x;
                u1 = x + w;
            }
            if (against)
            {
                u0 -= s_rtlCell;
                u1 -= s_rtlCell;
            }
        }

        /// <summary>
        ///  The u at which something flowing <paramref name="rightToLeft"/> starts, given the
        ///  visual span it covers.
        /// </summary>
        private static int LeadingU(ManagedLsLine line, bool rightToLeft, int x, int w)
        {
            bool against = rightToLeft != line.RightToLeft;
            SpanU(line, against, x, w, out int u0, out int u1);
            return against ? u1 : u0;
        }

        /// <summary>
        ///  The text runs around <paramref name="index"/> whose level is at least
        ///  <paramref name="level"/>: one subline's content, as [first, last] run indices.
        /// </summary>
        private static void SublineSpan(ManagedLsLine line, int index, int level, out int first, out int last)
        {
            first = index;
            last = index;
            for (int r = index - 1; r >= 0; r--)
            {
                ManagedLsRun run = line.Runs[r];
                if (run.BidiLevel < level && run.IsText) break;
                if (run.BidiLevel < level) continue;      // a marker inside: keep looking
                if (run.IsText) first = r;
            }
            for (int r = index + 1; r < line.Runs.Count; r++)
            {
                ManagedLsRun run = line.Runs[r];
                if (run.BidiLevel < level && run.IsText) break;
                if (run.BidiLevel < level) continue;
                if (run.IsText) last = r;
            }
            // A marker run between two spans belongs to neither; trim to text at the ends.
            while (first < index && (!line.Runs[first].IsText || line.Runs[first].BidiLevel < level)) first++;
            while (last > index && (!line.Runs[last].IsText || line.Runs[last].BidiLevel < level)) last--;
        }

        private static void SpanExtent(ManagedLsLine line, int first, int last, int level,
            out int cpFirst, out int cpLim, out int x0, out int x1)
        {
            cpFirst = int.MaxValue; cpLim = int.MinValue; x0 = int.MaxValue; x1 = int.MinValue;
            for (int r = first; r <= last; r++)
            {
                ManagedLsRun run = line.Runs[r];
                if (!run.IsText || run.BidiLevel < level) continue;
                cpFirst = Math.Min(cpFirst, run.CpFirst);
                cpLim = Math.Max(cpLim, run.CpFirst + run.CchText);
                x0 = Math.Min(x0, run.PenX);
                x1 = Math.Max(x1, run.PenX + run.Width);
            }
            if (cpFirst == int.MaxValue) { cpFirst = cpLim = line.Runs[first].CpFirst; x0 = x1 = line.Runs[first].PenX; }
        }

        private static unsafe void FillQueryResult(ManagedLsLine line, int runIndex, int cp, int offset,
            int depthQueryMax, IntPtr pSubLineInfo, out int actualDepthQuery, ref LsTextCell lsTextCell)
        {
            ManagedLsRun run = line.Runs[runIndex];
            ClusterBounds(run, offset, out int first, out int last, out int x, out int w);

            // The cell is the glyph CLUSTER: a base with its marks, a ligature. FullTextLine puts the
            // caret stops inside it (one per character, or one for the cluster in a run with marks).
            lsTextCell.lscpStartCell = run.CpFirst + first;
            lsTextCell.lscpEndCell = run.CpFirst + last;
            lsTextCell.pointUvStartCell = new LSPOINT(LeadingU(line, run.IsRightToLeft, x, w), 0);
            lsTextCell.dupCell = w;   // zero for a zero-width cell: its trailing edge is its leading edge
            lsTextCell.cCharsInCell = last - first + 1;
            lsTextCell.cGlyphsInCell = 1;

            actualDepthQuery = 0;
            if (pSubLineInfo == IntPtr.Zero || depthQueryMax < 1)
            {
                return;
            }

            // One subline per embedding level from the paragraph's up to the run's.
            int baseLevel = line.RightToLeft ? 1 : 0;
            int depth = Math.Max(1, Math.Min(depthQueryMax, (run.IsText ? run.BidiLevel : baseLevel) - baseLevel + 1));
            var subs = (LsQSubInfo*)pSubLineInfo;
            for (int k = 0; k < depth; k++)
            {
                int level = baseLevel + k;
                bool rtl = (level & 1) != 0;
                var sub = new LsQSubInfo
                {
                    lstflowSubLine = rtl ? LsTFlow.lstflowWS : LsTFlow.lstflowES,
                    idobj = (uint)MS.Internal.TextFormatting.TextStore.ObjectId.Text_chp,
                };

                if (k == 0)
                {
                    sub.lscpFirstSubLine = line.CpFirst;
                    sub.lsdcpSubLine = Math.Max(1, line.CpLim - line.CpFirst);
                    sub.pointUvStartSubLine = new LSPOINT(0, 0);
                    sub.dupSubLine = line.Width;
                }
                else
                {
                    SublineSpan(line, runIndex, level, out int f, out int l);
                    SpanExtent(line, f, l, level, out int c0, out int c1, out int x0, out int x1);
                    sub.lscpFirstSubLine = c0;
                    sub.lsdcpSubLine = Math.Max(1, c1 - c0);
                    sub.pointUvStartSubLine = new LSPOINT(LeadingU(line, rtl, x0, x1 - x0), 0);
                    sub.dupSubLine = x1 - x0;
                }

                if (k == depth - 1)
                {
                    // The run itself.
                    sub.plsrun = (IntPtr)(uint)run.Plsrun;
                    sub.lscpFirstRun = run.CpFirst;
                    sub.lsdcpRun = run.CchText;
                    sub.pointUvStartRun = new LSPOINT(LeadingU(line, run.IsRightToLeft, run.PenX, run.Width), 0);
                    sub.dupRun = run.Width;
                }
                else
                {
                    // The reverse object that holds the next subline in: its content's extent.
                    SublineSpan(line, runIndex, level + 1, out int f, out int l);
                    SpanExtent(line, f, l, level + 1, out int c0, out int c1, out int x0, out int x1);
                    sub.plsrun = (IntPtr)(uint)run.Plsrun;
                    sub.lscpFirstRun = c0;
                    sub.lsdcpRun = Math.Max(1, c1 - c0);
                    sub.pointUvStartRun = new LSPOINT(LeadingU(line, rtl, x0, x1 - x0), 0);
                    sub.dupRun = x1 - x0;
                }
                subs[k] = sub;
            }
            actualDepthQuery = depth;
        }

        internal static unsafe LsErr QueryLineCpPpoint(IntPtr ploline, int lscpQuery, int depthQueryMax,
            IntPtr pSubLineInfo, out int actualDepthQuery, out LsTextCell lsTextCell)
        {
            ManagedLsLine line = LineFrom(ploline);
            actualDepthQuery = 0;
            lsTextCell = new LsTextCell();
            for (int r = 0; r < line.Runs.Count; r++)
            {
                ManagedLsRun run = line.Runs[r];
                if (lscpQuery >= run.CpFirst && lscpQuery < run.CpFirst + run.CchText)
                {
                    FillQueryResult(line, r, lscpQuery, lscpQuery - run.CpFirst, depthQueryMax, pSubLineInfo,
                        out actualDepthQuery, ref lsTextCell);
                    return LsErr.None;
                }
            }
            return LsErr.None;
        }

        /// <summary>
        ///  The glyph cluster the character at <paramref name="offset"/> of a run belongs to, as its
        ///  first and last character, and where it sits visually (left-based, line-relative).
        /// </summary>
        /// <remarks>
        ///  Characters run the way their run does: in a right-to-left run the first logical character
        ///  is at the run's RIGHT edge and later ones march leftward. Widths are per CHARACTER (a
        ///  cluster's advance split over its characters), so a cluster is the sum of its characters'.
        /// </remarks>
        private static void ClusterBounds(ManagedLsRun run, int offset, out int first, out int last, out int x, out int width)
        {
            first = offset;
            last = offset;
            ushort[] map = run.ClusterMap;
            if (run.GlyphBased && !run.IsTab && map != null && offset < run.CchText && map.Length >= run.CchText)
            {
                while (first > 0 && map[first - 1] == map[offset]) first--;
                while (last + 1 < run.CchText && map[last + 1] == map[offset]) last++;
            }

            int[] widths = run.CharWidths ?? run.Advances ?? Array.Empty<int>();
            int before = 0;
            for (int i = 0; i < first && i < widths.Length; i++) before += widths[i];
            width = 0;
            for (int i = first; i <= last && i < widths.Length; i++) width += widths[i];

            x = run.IsRightToLeft
                ? run.PenX + run.Width - before - width
                : run.PenX + before;
        }

        internal static unsafe LsErr QueryLinePointPcp(IntPtr ploline, ref LSPOINT ptQuery, int depthQueryMax,
            IntPtr pSubLineInfo, out int actualDepthQuery, out LsTextCell lsTextCell)
        {
            ManagedLsLine line = LineFrom(ploline);
            actualDepthQuery = 0;
            lsTextCell = new LsTextCell();
            int u = ptQuery.x;

            // Point->cp maps only onto TEXT runs. The trailing break/control run exists so
            // cp->x queries (selection bounds) can resolve its codepoints, but a POINT past
            // the text must resolve to the last real character: returning the break cp put
            // the caret beyond the document and TextBoxView.GetTextPositionFromDistance
            // throws ("Requested distance is outside the content...").
            //
            // A cell is half open in its OWN subline's direction, [v, v + w) from the run's leading
            // edge, so in main-direction u a cell of a run reading against the paragraph is
            // (lead - v - w, lead - v]. Such a run also sits one unit into its neighbour's edge
            // cell, and where they overlap the point is its: those runs are asked first. The last
            // pass forgives the one-unit gap the shift leaves at the run's other end.
            for (int pass = 0; pass < 3; pass++)
            {
                for (int r = 0; r < line.Runs.Count; r++)
                {
                    ManagedLsRun run = line.Runs[r];
                    if (!run.IsText || run.CchText == 0) continue;
                    bool against = run.IsRightToLeft != line.RightToLeft;
                    if (pass < 2 && against != (pass == 0)) continue;
                    int slack = pass == 2 ? s_rtlCell : 0;

                    int lead = LeadingU(line, run.IsRightToLeft, run.PenX, run.Width);
                    int v = 0;
                    for (int i = 0; i < run.CchText; )
                    {
                        ClusterBounds(run, i, out _, out int last, out _, out int w);
                        bool hit = against
                            ? u > lead - v - w - slack && u <= lead - v + slack
                            : u >= lead + v - slack && u < lead + v + w + slack;
                        if (hit && w > 0)
                        {
                            FillQueryResult(line, r, run.CpFirst + i, i, depthQueryMax, pSubLineInfo, out actualDepthQuery, ref lsTextCell);
                            return LsErr.None;
                        }
                        v += w;
                        i = last + 1;
                    }
                }
            }

            // Outside every run: the character at the nearest visual edge of the text. A line with no
            // text runs (blank line) returns an empty cell; the caller's fallback places the caret at
            // the line start, which is correct there.
            int qx = line.RightToLeft ? line.Width - u : u;
            int leftmost = -1, rightmost = -1;
            for (int r = 0; r < line.Runs.Count; r++)
            {
                ManagedLsRun run = line.Runs[r];
                if (!run.IsText || run.CchText == 0) continue;
                if (rightmost < 0 || run.PenX + run.Width > line.Runs[rightmost].PenX + line.Runs[rightmost].Width) rightmost = r;
                if (leftmost < 0 || run.PenX < line.Runs[leftmost].PenX) leftmost = r;
            }
            bool left = qx < line.Width / 2;
            int edge = left ? leftmost : rightmost;
            if (edge >= 0)
            {
                ManagedLsRun edgeRun = line.Runs[edge];
                // The logical character at that visual edge.
                int i = left == edgeRun.IsRightToLeft ? edgeRun.CchText - 1 : 0;
                FillQueryResult(line, edge, edgeRun.CpFirst + i, i, depthQueryMax, pSubLineInfo, out actualDepthQuery, ref lsTextCell);
            }
            return LsErr.None;
        }

        internal static LsErr EnumLine(IntPtr ploline, bool reverseOrder, bool geometryNeeded, ref LSPOINT pt)
            => LsErr.None;

        internal static LsErr DisposeLine(IntPtr ploline, bool finalizing)
        {
            if (ploline != IntPtr.Zero)
            {
                GCHandle h = GCHandle.FromIntPtr(ploline);
                if (h.IsAllocated) h.Free();
            }
            return LsErr.None;
        }
    }
}
