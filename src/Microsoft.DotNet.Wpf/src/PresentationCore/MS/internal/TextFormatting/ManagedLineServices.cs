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
// line and calls the draw callbacks; the query entry points answer caret/hit-test. Complex scripts,
// bidi reordering, optimal-break and justification are intentionally simplified (single-direction,
// greedy) -- enough for the common Latin editing/wrapping case.
//
// Handles (ploc / ploline / break records) are GCHandles to managed objects, surfaced as IntPtr.
//

using System;
using System.Collections.Generic;
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
        public int PenX;                    // ideal x where this run starts (line-relative)
        public int Width;                   // ideal advance width of the run
        public int Ascent;
        public int Descent;
        public bool IsText;                 // false for control/object runs (skipped when drawing)
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

                bool isText = chp.idObj == (ushort)TextStore.ObjectId.Text_chp;

                // Line/paragraph breaks are delivered as Text_chp runs whose characters are the
                // separator markers (LS routes LineBreak/ParaBreak runs through the text object id).
                // They terminate the current line; consume the break and stop so we don't fetch past
                // the end of the paragraph (which would request an out-of-range cp).
                bool isBreak = IsLineOrParaBreak(runText);

                if (!isText || fHidden != 0 || isBreak)
                {
                    // A control/object/hidden run or a hard line/paragraph break. Record it as a
                    // zero-width non-text run so its codepoints stay QUERYABLE: a selection that
                    // spans lines includes the break cp as the last cp of the first line's range,
                    // and FullTextLine.GetTextBounds hard-requires QueryLineCpPpoint to resolve it
                    // (an unresolved cp returns degenerate bounds and the segment's highlight
                    // vanishes). DisplayLine skips non-text runs, so nothing is drawn for it.
                    line.Runs.Add(new ManagedLsRun
                    {
                        Plsrun = plsrun, CpFirst = cp, CchText = cchText, Text = runText,
                        Glyphs = Array.Empty<ushort>(), ClusterMap = Array.Empty<ushort>(),
                        CharProps = Array.Empty<ushort>(), GlyphProps = Array.Empty<uint>(),
                        Advances = Array.Empty<int>(), Offsets = Array.Empty<GlyphOffset>(),
                        GlyphCount = 0, PenX = penX, Width = 0, IsText = false,
                    });
                    cp += cchText;
                    forced = true;
                    break;
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
                    // Wrapping needed: break at the last whitespace opportunity within what fits.
                    int brk = FindBreak(runText, Math.Min(fitted, cchText));
                    if (brk <= 0)
                    {
                        if (line.Runs.Count == 0 && penX == 0)
                        {
                            // Emergency: guarantee forward progress with at least one character.
                            brk = Math.Max(1, Math.Min(fitted, cchText));
                        }
                        else
                        {
                            // Nothing more fits; end the line here without consuming this run.
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
                    Plsrun = plsrun, CpFirst = cp, CchText = consume, Text = shapeText,
                    Glyphs = glyphs, ClusterMap = clusters, CharProps = charProps, GlyphProps = glyphProps,
                    Advances = advances, Offsets = offsets, GlyphCount = glyphCount,
                    PenX = penX, Width = usedWidth, Ascent = txm.dvAscent, Descent = txm.dvDescent, IsText = true,
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

            line.Callbacks = cb;
            line.CpLim = cp;
            line.Ascent = lineAscent;
            line.Descent = lineDescent;
            line.Width = penX;
            line.WidthNoTrailing = penX;
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
            lineWidths.upStartTrailing = penX;
            lineWidths.upLimLine = penX;
            lineWidths.upMinStartTrailing = penX;
            lineWidths.upMinLimLine = penX;

            pploline = GCHandle.ToIntPtr(GCHandle.Alloc(line));
            return LsErr.None;
        }

        // True if the run is a line/paragraph break: its leading character is a separator marker
        // (U+2028 line separator, U+2029 paragraph separator, LF, CR).
        private static bool IsLineOrParaBreak(char[] text)
        {
            if (text == null || text.Length == 0) return false;
            char c = text[0];
            return c == '\u2028' || c == '\u2029' || c == '\n' || c == '\r';
        }

        // Greedy break: index just past the last whitespace within [0..limit], or 0 if none.
        private static int FindBreak(char[] text, int limit)
        {
            int n = Math.Min(limit, text.Length);
            for (int i = n - 1; i >= 0; i--)
            {
                char c = text[i];
                if (c == ' ' || c == '\t' || c == '\u00A0' || c == '\u2003' || c == '\u2002')
                    return i + 1;
            }
            return 0;
        }

        private static unsafe void ShapeRun(
            LineServicesCallbacks cb, IntPtr ploc, Plsrun plsrun, IntPtr plsrunPtr, char[] text,
            out ushort[] glyphs, out ushort[] clusters, out ushort[] charProps,
            out uint[] glyphProps, out int[] advances, out GlyphOffset[] offsets, out int glyphCount)
        {
            int cch = text.Length;
            int capacity = cch * 3 + 16;
            var glyphBuf = new ushort[capacity];
            var glyphPropBuf = new uint[capacity];
            var clusterBuf = new ushort[cch];
            var charPropBuf = new ushort[cch];
            var canAlone = new int[cch];
            int gc = capacity;
            int fBuffersUsed = 0;

            IntPtr plsrunLocal = plsrunPtr;
            int cchLocal = cch;

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

                LSPOINT ptRun = new LSPOINT(pt.x + run.PenX, baseline);
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
                    int x = run.PenX;
                    for (int i = 0; i < offset && i < run.Advances.Length; i++) x += run.Advances[i];
                    int w = offset < run.Advances.Length ? run.Advances[offset] : 0;
                    FillQueryResult(line, run, lscpQuery, x, w, depthQueryMax, pSubLineInfo, out actualDepthQuery, ref lsTextCell);
                    return LsErr.None;
                }
            }
            return LsErr.None;
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
            ManagedLsRun lastText = null;
            foreach (ManagedLsRun run in line.Runs)
            {
                if (!run.IsText) continue;
                lastText = run;
                int x = run.PenX;
                for (int i = 0; i < run.CchText; i++)
                {
                    int w = i < run.Advances.Length ? run.Advances[i] : 0;
                    if (qx < x + w)
                    {
                        FillQueryResult(line, run, run.CpFirst + i, x, w, depthQueryMax, pSubLineInfo, out actualDepthQuery, ref lsTextCell);
                        return LsErr.None;
                    }
                    x += w;
                }
            }

            // Past the end of the text: the trailing edge of the last character. A line with
            // no text runs (blank line) returns an empty cell; the caller's fallback places
            // the caret at the line start, which is correct there.
            if (lastText != null && lastText.CchText > 0)
            {
                int i = lastText.CchText - 1;
                int x = lastText.PenX;
                for (int j = 0; j < i && j < lastText.Advances.Length; j++) x += lastText.Advances[j];
                int w = i < lastText.Advances.Length ? lastText.Advances[i] : 0;
                FillQueryResult(line, lastText, lastText.CpFirst + i, x, w, depthQueryMax, pSubLineInfo, out actualDepthQuery, ref lsTextCell);
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
