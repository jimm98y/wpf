# Text shaping off Windows

Shaping is the step between "these are the characters" and "these are the glyphs, here": it applies
the font's OpenType tables, so a run of Arabic comes out as cursive joined forms rather than as a row
of isolated letters, and a combining mark lands over its base rather than at its own cell origin.

On Windows WPF gets this from DWrite. Off Windows there is no DWrite, and the managed
`DirectWriteForwarder` stub produces only a **nominal** run — one cmap glyph per codepoint, hmtx
advances, zero offsets. `MS/Internal/TextFormatting/ManagedOpenTypeShaper.cs` is what turns that into
a shaped one, by driving WPF's own managed OpenType engine (`MS.Internal.Shaping.OpenTypeLayout`) —
the same engine WPF shipped for GSUB/GPOS before it moved to DWrite.

## Where it plugs in

Three places turn characters into glyphs, and all three shape:

| call site | entry point | what it does |
|---|---|---|
| `LineServicesCallbacks.GetGlyphsRedefined` | `Substitute` | GSUB: joining forms, ligatures, conjuncts |
| `LineServicesCallbacks.GetGlyphPositions` | `Position` | GPOS: mark attachment, cursive attachment, kerning |
| `FormattedTextSymbols` | `ShapeArrays` | both, for a trimmed line's collapsing symbol |

Everything that formats text through `TextFormatter` — `TextBlock`, `TextBox`, `FlowDocument`,
`FormattedText`/`DrawingContext.DrawText` — reaches the first two. The third is the odd one out: a
collapsing symbol is glyphed in a single call rather than through the callbacks, so it needs its own
pass or it is the one unshaped fragment on an otherwise correctly shaped line. Only a custom
`TextCollapsingProperties.Symbol` reaches it — the default ellipsis is one Latin character — and
reaching it in a test needs a line that OVERFLOWS (`Collapse` returns a line that fits untouched),
hence a narrow, non-wrapping paragraph.

## What the shaper decides

**The script.** The engine looks every feature up under a script table, so the run has to be
classified first — `arab`, `hebr`, `dev2`, `thai` — or the font's lookups for that script are simply
unreachable. Two details are easy to get wrong and were:

* OpenType's default *script* tag is `DFLT` in capitals. Lower-case `dflt` is the default *langsys*
  tag. `FindScript` compares exactly, so asking for script `dflt` never matches anything.
* The Indic scripts have two generations of tag. A font declares either the v2 spelling (`dev2`) or
  the original (`deva`), and which one says which shaping model it was built for. Both are tried,
  v2 first, then `DFLT`.

**The features.** Latin and most scripts get `ccmp locl rlig liga clig calt` plus `kern mark mkmk` —
what DWrite enables by default for horizontal text. Arabic and Syriac add the positional forms and
`curs`; Indic adds its own long list.

**The positional forms**, for Arabic and Syriac. A letter's shape depends on whether its neighbours
join to it, looking *through* transparent characters (the harakat and other combining marks, which do
not break a join). Each character gets its own `init`/`medi`/`fina`/`isol` feature over a
single-character range — they cannot be run-wide, because adjacent letters get different ones. A
letter only takes a form its joining type allows: alef, dal, reh and waw join rightward only, so they
have a final form and never an initial or medial one.

## What it does not do

**Indic reordering.** The Indic feature set is applied, which gets conjuncts, half forms, nukta
composition and rakar. It does not segment syllables or *move* anything, and Devanagari and its
relatives need a pre-base matra moved ahead of its consonant and reph moved to the end of the
syllable. So Indic is better than nominal and still not correct; a real Indic/USE shaper is its own
project. `ShapingTests.DevanagariFormsConjuncts` is deliberately scoped to what is actually claimed.

**GPOS anchor format 2 contour points.** `FontFaceLayoutInfo.GetGlyphPointCoord` returns "no contour
point", so a format 2 anchor uses its design coordinates. That is what other shapers do with format 2
as well; it costs sub-pixel accuracy at small sizes.

## Two things that bite

**Substitution can GROW a run.** A one-to-many lookup — GSUB type 2, and the `ccmp` decompositions
Arabic and Indic fonts lean on — turns one glyph into several. The LineServices buffer contract
already covers this (leave `fIsGlyphBuffersUsed` clear, report the count you need, get called again
with a bigger buffer) and both `ManagedOpenTypeShaper.Substitute` and `ManagedLineServices.ShapeRun`
now honour it.

Writing a partial result instead is *silently fatal*, and it was the bug that hid all of this: the
truncated glyph array no longer agreed with the cluster map written beside it, and the inconsistent
run was dropped further down. Not "Arabic looked wrong" — Arabic produced no glyph runs at all and
the text was invisible, in any font whose `ccmp` grew the run. Noto Nastaliq Urdu does exactly that,
and it is what the fallback chain picks for Arabic on a machine with no Windows fonts.

**A throw from shaping costs more than shaping.** Both passes catch: an exception out of `GetGlyphs`
or `GetGlyphPositions` aborts the callback, and LS answers that by dropping the run. A font this
engine cannot digest must cost its own shaping and nothing else. (`GetGlyphPointCoord` threw
`NotSupportedException` for exactly this reason and took every GPOS feature in the run with it —
no kerning, no marks, nothing positioned.)

## Units

GPOS runs entirely in **font design units** and converts once at the end. Mark attachment subtracts
the advances accumulated between a base glyph and its mark from an anchor difference, so the advances
fed in have to be in the same space as the anchors; the caller's ideal-unit advances would misplace
every mark by the scale factor. `LayoutMetrics` is constructed with `DesignEmHeight = 0`, which is
how the engine is asked for design-unit output, and `PixelsEm = 0` so that GPOS device tables — which
are hinting deltas in *pixels* — are out of range and contribute nothing rather than mixing units.

Advances are then adjusted by their **delta** rather than overwritten, so a run whose GPOS does
nothing keeps exactly the advances the caller computed.

## Bidi reordering

Shaping decides which glyphs; bidi decides where the runs go. The two halves live apart:

* the **analysis** — which characters are right-to-left, and at what embedding level — is WPF's own
  managed `Bidi` class in `TextFormatting`, and it runs off Windows unchanged;
* the **reordering** — turning a logically ordered list of runs into a visually ordered line — is
  `ManagedLineServices.ReorderRunsVisually`.

The level never appears on a run. LineServices learns about direction from CONTROL RUNS: the text
store brackets every embedding with a `Plsrun.Reverse` (level up) and a `Plsrun.CloseAnchor` (level
down), one marker per level step, counted from the paragraph's own base level rather than from zero.
The managed engine used to skip those as zero-width placeholders, which is precisely why it could not
reorder. It now counts them, stamps each run with the level it was fetched at, and applies rule L2 of
UAX #9 over runs: from the highest level present down to the lowest odd level, reverse every
contiguous sequence of runs at that level or above. Successive reversals compose into the nesting the
embeddings describe, so one loop handles arbitrary depth.

An RTL paragraph needs no special case. Its base level is 1, so every run is at level 1 or above and
the outermost pass reverses the whole line — which is what "the line reads right to left" means.

Two things follow from this that are easy to get wrong:

**The run list stays in logical order.** Only `PenX` changes. Everything that looks a run up does so
by cp — the caret, selection bounds, hit-testing — and a logically ordered list keeps all of that a
simple scan. Visual order is a property of where a run is drawn, not of the collection. What it does
mean is that `PenX` is no longer monotonic across the list, so point→cp hit-testing has to ask each
run whether the point is inside *it* rather than walking the line left to right accumulating widths.

**A right-to-left run is anchored at its RIGHT edge**, and its glyphs march leftward from there
(`GlyphRun.BuildGeometry`). Handing such a run its left edge as the origin draws it one full
run-width too far left, on top of whatever precedes it — so a lone Hebrew word inside an English
sentence was misplaced even before any reordering question arose. Characters within the run follow
the same rule: the first logical character is at the right edge, which is what `CellBounds` mirrors
for the caret. Text decorations are the exception — a rectangle wants the left edge whichever way the
run reads.

## Justification and line breaking

`TextAlignment.Justify` reaches the managed line engine as `LsPap.fJustify` and is applied by
`ManagedLineServices.JustifyLine`: the slack between the line's natural width and the column is
spread across the inter-word spaces, in the glyph ADVANCES rather than in the run positions. That
matters because everything downstream measures from the advances — run widths feed the reordering
pass, which feeds drawing and the caret — so widening a space moves every following word and keeps
hit-testing consistent without a second mechanism. Only lines that WRAPPED are justified; a line that
ended at a hard break or ran out of text is the last line of its paragraph, and stretching that one
is what turns a two-word final line into two words at opposite edges of the column.

Trailing whitespace hangs past the edge rather than being stretched into it, which is also what makes
`TextLine.Width` and `TextLine.WidthIncludingTrailingWhitespace` differ; they used to be identical.

**Line breaking within a line is greedy, and optimal break does not live here.** LineServices exposes
it through `LoCreateBreaks` / `LoCreateParaBreakingSession`, and nothing in this port can reach those:
the public API is compiled out (`TextBreakpoint` and `TextFormatter.CreateParagraphCache` are internal
unless `OPTIMALBREAK_API` is defined) and the only internal caller is the native PTS host, which
`FlowDocumentPage` bypasses on every platform — `s_managed` is unconditionally true. Implementing the
LS entry points would be unreachable code.

The reachable feature of the same name is `FlowDocument.IsOptimalParagraphEnabled`, and it now works:
it selects `ManagedFlowLayout.BreakParagraphOptimally`, a minimum-raggedness breaker over the
paragraph's words. Greedy fills each line as far as it can and never reconsiders, so a long word late
in a paragraph can leave a nearly empty last line; the optimal breaker costs each line by the CUBE of
its leftover space (Knuth-Plass — squaring treats one very short line as no worse than two mildly
short ones, which is the case the feature exists to fix), leaves the last line free, and takes the
cheapest set of breaks. `Wpf.Document.Tests/OptimalParagraphTests.cs` holds a paragraph that greedy
splits into four lines and the optimal breaker fits into three.

## Rendering

A right-to-left run's glyphs are in **logical** order, and its baseline origin is the run's **right**
edge: glyph *i* sits one of its own nominal advances further left than the pen. That is what
`GlyphRun.BuildGeometry` does and what WPF's measured bounds assume, so `MilcoreEngine.EmitGlyphRun`
does the same, keyed off the `BidiLevel` on the wire (`MILCMD_GLYPHRUN_CREATE` offset 68). Walking an
RTL run left-to-right — which is what it used to do, ignoring the field — draws every Arabic and
Hebrew line mirrored and in the wrong place.

## Debugging

`WPF_SHAPE_LOG=1` traces each pass: the face, the script tag it settled on, how many features, the
glyph count in and out, and the result. The first question when text looks wrong is always "which
script did it pick, and did the font declare that script at all".

```bash
WPF_SHAPE_LOG=1 eng/run-linux.sh --gallery
```

## Tests

Both suites go through the public `TextFormatter` path rather than the internals, and skip rather
than fail on a machine with no font for the script.

`tests/CrossPlatform/Wpf.Text.Tests/ShapingTests.cs` — which glyphs:

* the same Arabic letter is a different glyph joined and isolated — the assertion a nominal run
  cannot pass, and it needs no knowledge of any font's glyph ids;
* Arabic produces drawable glyphs at all, including in a font whose substitution grows the run;
* a run with combining marks carries a non-zero glyph offset somewhere;
* Devanagari forms conjuncts;
* a trimmed line's collapsing symbol is shaped;
* Latin is unchanged.

`tests/CrossPlatform/Wpf.Text.Tests/BidiReorderingTests.cs` — where they go. These assert on
GEOMETRY, so they hold in any font, and they use Hebrew rather than Arabic so that a failure is a
reordering failure and not a shaping one. Two properties carry most of the weight:

* the runs **tile** the line — sorted by position they are contiguous, no gap and no overlap. Every
  placement bug found here showed up first as a violation of that one invariant;
* the visual **order** is the one bidi prescribes: a pure RTL line puts its first logical word
  rightmost, an RTL island stays between its Latin neighbours, an RTL paragraph reverses its Latin
  runs, and digits inside RTL text keep their own left-to-right order in place.

Plus a caret check: hit-testing the middle of each character's own drawn box must return that
character, in right-to-left runs too.
