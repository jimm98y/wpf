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
            => LsErr.None;

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

                // Bidi level changes arrive as control runs, one per level step: the text store
                // brackets every embedding with Reverse (level up) and CloseAnchor (level down).
                // That is the only place direction is expressed -- FetchRun says nothing about it --
                // so tracking these brackets is what makes reordering possible at all. Note the
                // marker itself belongs to the level OUTSIDE the bracket it opens or closes.
                Plsrun runKind = TextStore.ToIndex(plsrun);
                if (runKind == Plsrun.Reverse)
                {
                    bidiLevel++;
                }
                else if (runKind == Plsrun.CloseAnchor)
                {
                    bidiLevel = Math.Max(baseLevel, bidiLevel - 1);
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

                if (!isText || fHidden != 0)
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

                // Measure the run (ideal char widths), capped to the remaining column width.
                int[] charWidths = new int[cchText];
                int totalWidth = 0, fitted = 0;
                fixed (char* pText = runText)
                fixed (int* pCw = charWidths)
                {
                    cb.GetRunCharWidths(ploc, plsrun, LsDevice.Presentation, pText, cchText,
                        column == int.MaxValue ? int.MaxValue : Math.Max(0, column - penX),
                        LsTFlow.lstflowES, pCw, ref totalWidth, ref fitted);
                }

                int consume = cchText;
                if (column != int.MaxValue && penX + totalWidth > column)
                {
                    // How many characters actually fit. GetRunCharWidths' stringLengthFitted counts
                    // the first character that CROSSES the boundary as well (LS's own contract: it
                    // adds the width, then tests), so using it as a break position overhangs the
                    // column by one character. Harmless for Latin, where the break lands on the
                    // preceding space, but for scripts with no spaces -- CJK above all -- the break
                    // IS that position, and every wrapped line spilled one ideograph past its edge.
                    int fits = 0;
                    for (int w = penX; fits < cchText && w + charWidths[fits] <= column; fits++)
                        w += charWidths[fits];

                    // Trailing whitespace is not drawn, so a space that only just overflows still
                    // counts as fitting. Without this the break opportunity it carries falls outside
                    // the window and the word before it moves to the next line for no visible reason.
                    while (fits < cchText && IsBreakableSpace(runText[fits])) fits++;

                    // Break at the last opportunity within what fits.
                    int brk = FindBreak(runText, fits);
                    if (brk <= 0)
                    {
                        if (line.Runs.Count == 0 && penX == 0)
                        {
                            // Emergency: guarantee forward progress with at least one character.
                            brk = Math.Max(1, Math.Min(fits, cchText));
                        }
                        else
                        {
                            // Nothing more fits and this run carries no break opportunity of its own.
                            // The store itemizes into one run per word / space / punctuation mark, so
                            // ending the line at THIS run boundary would break wherever the runs happen
                            // to meet -- e.g. a trailing "," that no longer fits would start the next
                            // line. Backtrack to the last real break opportunity already on the line
                            // (which also returns the runs after it to the next line); only if the line
                            // holds no opportunity at all does it end at the run boundary.
                            BackTrackToBreak(cb, ploc, line, ref cp, ref penX);
                            break;
                        }
                    }
                    consume = brk;
                    lineFull = true;
                }

                if (consume <= 0) break;

                int usedWidth = 0;
                for (int i = 0; i < consume; i++) usedWidth += charWidths[i];

                LsTxM txm = new LsTxM();
                cb.GetRunTextMetrics(ploc, plsrun, LsDevice.Presentation, LsTFlow.lstflowES, ref txm);
                lineAscent = Math.Max(lineAscent, txm.dvAscent);
                lineDescent = Math.Max(lineDescent, txm.dvDescent);

                char[] shapeText = (consume == cchText) ? runText : runText[..consume];
                ShapeRun(cb, ploc, plsrun, plsrunPtr, shapeText,
                         out ushort[] glyphs, out ushort[] clusters, out ushort[] charProps,
                         out uint[] glyphProps, out int[] advances, out GlyphOffset[] offsets, out int glyphCount);

                line.Runs.Add(new ManagedLsRun
                {
                    Plsrun = plsrun, PlsrunPtr = plsrunPtr, CpFirst = cp, CchText = consume, Text = shapeText,
                    Glyphs = glyphs, ClusterMap = clusters, CharProps = charProps, GlyphProps = glyphProps,
                    Advances = advances, Offsets = offsets, GlyphCount = glyphCount,
                    PenX = penX, Width = usedWidth, Ascent = txm.dvAscent, Descent = txm.dvDescent, IsText = true,
                    BidiLevel = bidiLevel,
                });

                penX += usedWidth;
                cp += consume;
            }

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

            // The expansion points, as (run, glyph) pairs. Trailing whitespace is excluded: it hangs
            // past the end of the line and stretching it would push the last word left of the edge.
            var points = new List<(ManagedLsRun Run, int Glyph)>();
            int lastNonSpaceRun = -1;
            for (int r = 0; r < line.Runs.Count; r++)
            {
                if (line.Runs[r].IsText && !IsAllWhitespace(line.Runs[r])) lastNonSpaceRun = r;
            }
            if (lastNonSpaceRun < 0)
            {
                return;
            }

            for (int r = 0; r <= lastNonSpaceRun; r++)
            {
                ManagedLsRun run = line.Runs[r];
                if (!run.IsText || run.Text == null) continue;

                for (int i = 0; i < run.CchText && i < run.Text.Length; i++)
                {
                    if (!IsBreakableSpace(run.Text[i])) continue;

                    // The last character of the last non-space run cannot be an expansion point that
                    // matters, but the general rule is simply: every space before the final word.
                    int glyph = i < run.ClusterMap.Length ? run.ClusterMap[i] : -1;
                    if (glyph >= 0 && glyph < run.Advances.Length)
                    {
                        points.Add((run, glyph));
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

                (ManagedLsRun run, int glyph) = points[i];
                run.Advances[glyph] += add;
                run.Width += add;
            }
        }

        /// <summary>The width of the whitespace the line ends with, which hangs past its edge.</summary>
        private static int TrailingWhitespaceWidth(ManagedLsLine line)
        {
            int width = 0;
            for (int r = line.Runs.Count - 1; r >= 0; r--)
            {
                ManagedLsRun run = line.Runs[r];
                if (!run.IsText)
                {
                    continue;   // a break or control run carries no width either way
                }
                if (!IsAllWhitespace(run))
                {
                    break;
                }
                width += run.Width;
            }
            return width;
        }

        private static bool IsAllWhitespace(ManagedLsRun run)
        {
            if (run.Text == null || run.CchText == 0) return false;
            for (int i = 0; i < run.CchText && i < run.Text.Length; i++)
            {
                if (!IsBreakableSpace(run.Text[i])) return false;
            }
            return true;
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

                if (brk < r.CchText && !TruncateRun(cb, ploc, r, brk)) return false;

                if (i + 1 < line.Runs.Count)
                    line.Runs.RemoveRange(i + 1, line.Runs.Count - i - 1);

                cp = r.CpFirst + r.CchText;
                penX = r.PenX + r.Width;
                return true;
            }
            return false;
        }

        // Cuts a placed run down to its first `keep` characters, re-measuring and re-shaping so the
        // glyphs and advances match the characters that remain on the line.
        private static unsafe bool TruncateRun(
            LineServicesCallbacks cb, IntPtr ploc, ManagedLsRun r, int keep)
        {
            char[] text = r.Text[..keep];
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

            ShapeRun(cb, ploc, r.Plsrun, r.PlsrunPtr, text,
                     out ushort[] glyphs, out ushort[] clusters, out ushort[] charProps,
                     out uint[] glyphProps, out int[] advances, out GlyphOffset[] offsets, out int glyphCount);

            r.Text = text;
            r.CchText = keep;
            r.Width = totalWidth;
            r.Glyphs = glyphs;
            r.ClusterMap = clusters;
            r.CharProps = charProps;
            r.GlyphProps = glyphProps;
            r.Advances = advances;
            r.Offsets = offsets;
            r.GlyphCount = glyphCount;
            return true;
        }

        private static unsafe void ShapeRun(
            LineServicesCallbacks cb, IntPtr ploc, Plsrun plsrun, IntPtr plsrunPtr, char[] text,
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

            IntPtr plsrunLocal = plsrunPtr;
            int cchLocal = cch;

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

                fixed (char* pText = text)
                fixed (ushort* pGlyphs = glyphBuf)
                fixed (uint* pGlyphProps = glyphPropBuf)
                fixed (ushort* pCluster = clusterBuf)
                fixed (ushort* pCharProps = charPropBuf)
                fixed (int* pCanAlone = canAlone)
                {
                    cb.GetGlyphsRedefined(ploc, &plsrunLocal, &cchLocal, 1, pText, cch, LsTFlow.lstflowES,
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

            fixed (char* pText = text)
            fixed (ushort* pCluster = clusters)
            fixed (ushort* pCharProps = charProps)
            fixed (ushort* pGlyphs = glyphs)
            fixed (uint* pGlyphProps = glyphProps)
            fixed (int* pAdvances = advances)
            fixed (GlyphOffset* pOffsets = offsets)
            {
                cb.GetGlyphPositions(ploc, &plsrunLocal, &cchLocal, 1, LsDevice.Presentation, pText,
                    pCluster, pCharProps, cch, pGlyphs, pGlyphProps, gc, LsTFlow.lstflowES, pAdvances, pOffsets);
            }
        }

        // ---- display: replay the glyph runs through the draw callbacks ----

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
                if (!run.IsText || run.GlyphCount == 0) continue;

                // Run x-origin, which is the run's LEADING edge and therefore depends on the run's own
                // direction: a right-to-left run is anchored at its RIGHT edge and its glyphs march
                // leftward from there (see GlyphRun.BuildGeometry). Handing an RTL run its left edge
                // draws it one run-width too far left, on top of whatever precedes it.
                int leftEdge = pt.x + run.PenX;
                int originEdge = run.IsRightToLeft ? leftEdge + run.Width : leftEdge;

                // In an RTL paragraph the line origin is the line's RIGHT edge, and LS expresses run
                // positions as negative offsets from it -- ComputeShapedGlyphRun negates what it gets.
                // So send the mirrored distance and let it undo the sign.
                int runX = line.RightToLeft ? originEdge - line.Width : originEdge;
                LSPOINT ptRun = new LSPOINT(runX, baseline);
                var lsHeights = new LsHeights { dvAscent = run.Ascent, dvDescent = run.Descent, dvMultiLineHeight = run.Ascent + run.Descent };
                var clip = clipRect;
                var expTypes = new LsExpType[run.GlyphCount];

                fixed (char* pText = run.Text)
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

                // Native LS draws underline/strikethrough/overline/baseline during LoDisplayLine; replay
                // that here so paragraph-/run-level TextDecorations render off-Windows too. A
                // decoration is a rectangle, so it wants the run's LEFT edge whichever way the run
                // reads -- not the direction-dependent leading edge the glyphs are anchored at.
                int decorationX = line.RightToLeft ? leftEdge - line.Width : leftEdge;
                cb.DrawManagedTextDecorations(run.Plsrun, decorationX, baseline, run.Width, LsTFlow.lstflowES, displayMode, ref clip);
            }
            return LsErr.None;
        }

        // ---- hit-testing (greedy single-direction cp<->x mapping) ----

        // Fills the caller's LsQSubInfo buffer (one level deep: no nested sublines in the
        // greedy single-direction engine) and the text cell for one character of a run.
        // FullTextLine's hit-testing REQUIRES actualDepthQuery > 0 and dupCell > 0 —
        // with an empty subline array it silently falls back to "line start", which
        // broke caret placement and word selection wherever the shim formats text.
        // NOTE lscpEndCell is the LAST lscp still inside the cell (== start for a
        // single-codepoint cell), not one-past-the-end.
        private static unsafe void FillQueryResult(ManagedLsLine line, ManagedLsRun run, int cp, int cellX, int cellW,
            int depthQueryMax, IntPtr pSubLineInfo, out int actualDepthQuery, ref LsTextCell lsTextCell)
        {
            lsTextCell.lscpStartCell = cp;
            lsTextCell.lscpEndCell = cp;
            lsTextCell.pointUvStartCell = new LSPOINT(cellX, 0);
            lsTextCell.dupCell = Math.Max(cellW, 1);
            lsTextCell.cCharsInCell = 1;
            lsTextCell.cGlyphsInCell = 1;

            actualDepthQuery = 0;
            if (pSubLineInfo != IntPtr.Zero && depthQueryMax >= 1)
            {
                var sub = new LsQSubInfo
                {
                    lstflowSubLine = LsTFlow.lstflowES,
                    lscpFirstSubLine = line.CpFirst,
                    lsdcpSubLine = Math.Max(1, line.CpLim - line.CpFirst),
                    pointUvStartSubLine = new LSPOINT(0, 0),
                    dupSubLine = line.Width,
                    idobj = (uint)MS.Internal.TextFormatting.TextStore.ObjectId.Text_chp,
                    plsrun = (IntPtr)(uint)run.Plsrun,
                    lscpFirstRun = run.CpFirst,
                    lsdcpRun = run.CchText,
                    pointUvStartRun = new LSPOINT(run.PenX, 0),
                    dupRun = run.Width,
                };
                *(LsQSubInfo*)pSubLineInfo = sub;
                actualDepthQuery = 1;
            }
        }

        internal static unsafe LsErr QueryLineCpPpoint(IntPtr ploline, int lscpQuery, int depthQueryMax,
            IntPtr pSubLineInfo, out int actualDepthQuery, out LsTextCell lsTextCell)
        {
            ManagedLsLine line = LineFrom(ploline);
            actualDepthQuery = 0;
            lsTextCell = new LsTextCell();
            foreach (ManagedLsRun run in line.Runs)
            {
                if (lscpQuery >= run.CpFirst && lscpQuery < run.CpFirst + run.CchText)
                {
                    int offset = lscpQuery - run.CpFirst;
                    CellBounds(run, offset, out int x, out int w);
                    FillQueryResult(line, run, lscpQuery, x, w, depthQueryMax, pSubLineInfo, out actualDepthQuery, ref lsTextCell);
                    return LsErr.None;
                }
            }
            return LsErr.None;
        }

        /// <summary>
        ///  Where the character at <paramref name="offset"/> within a run sits, and how wide it is.
        /// </summary>
        /// <remarks>
        ///  Characters run the way their run does. In a right-to-left run the first logical character
        ///  is at the run's RIGHT edge and later ones march leftward, so measuring from PenX forward
        ///  puts the caret at the mirror image of where the glyph actually is -- click at the start of
        ///  a Hebrew word and the caret lands at its end.
        /// </remarks>
        private static void CellBounds(ManagedLsRun run, int offset, out int x, out int width)
        {
            int before = 0;
            for (int i = 0; i < offset && i < run.Advances.Length; i++) before += run.Advances[i];
            width = offset < run.Advances.Length ? run.Advances[offset] : 0;

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
            int qx = ptQuery.x;

            // Point->cp maps only onto TEXT runs. The trailing break/control run exists so
            // cp->x queries (selection bounds) can resolve its codepoints, but a POINT past
            // the text must resolve to the last real character: returning the break cp put
            // the caret beyond the document and TextBoxView.GetTextPositionFromDistance
            // throws ("Requested distance is outside the content...").
            // Runs are no longer in x order once bidi reordering has run, so this asks each run
            // whether the point is inside IT rather than walking the line from left to right.
            // The rightmost run is likewise the one with the largest PenX, not the last in the list.
            ManagedLsRun rightmost = null;
            foreach (ManagedLsRun run in line.Runs)
            {
                if (!run.IsText) continue;
                if (rightmost == null || run.PenX > rightmost.PenX) rightmost = run;

                if (qx < run.PenX || qx >= run.PenX + run.Width) continue;

                for (int i = 0; i < run.CchText; i++)
                {
                    CellBounds(run, i, out int x, out int w);
                    if (qx >= x && qx < x + w)
                    {
                        FillQueryResult(line, run, run.CpFirst + i, x, w, depthQueryMax, pSubLineInfo, out actualDepthQuery, ref lsTextCell);
                        return LsErr.None;
                    }
                }
            }

            // Outside every run: the nearest edge of the text. A line with no text runs (blank line)
            // returns an empty cell; the caller's fallback places the caret at the line start, which
            // is correct there.
            if (rightmost != null && rightmost.CchText > 0)
            {
                ManagedLsRun edgeRun = rightmost;
                if (qx < 0)
                {
                    foreach (ManagedLsRun run in line.Runs)
                    {
                        if (run.IsText && run.PenX < edgeRun.PenX) edgeRun = run;
                    }
                }

                // The logical character at that visual edge: the last one for a left-to-right run,
                // the first one for a right-to-left run.
                int i = edgeRun.IsRightToLeft ? 0 : edgeRun.CchText - 1;
                CellBounds(edgeRun, i, out int x, out int w);
                FillQueryResult(line, edgeRun, edgeRun.CpFirst + i, x, w, depthQueryMax, pSubLineInfo, out actualDepthQuery, ref lsTextCell);
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
