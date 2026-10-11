# How Windows 11's ChooseFont builds its lists

The pipeline is GDI, then fms.dll (Font Management Service), then comdlg32. Addresses are fms.dll / comdlg32.dll /
fontdrvhost.exe (ARM64, public symbols). Python model: `fmsmodel.py`. C# port: `FontFamilyModel.cs`. Score on this
machine: 178/178 family names and order; 178/178 style lists; 178/178 script lists; 178/178 per-face GDI LOGFONTs;
178/178 script charsets and samples.

Intermediate oracles used: `fd/w/fms.ps1` calls fms.dll directly (FmsInitializeEnumerator(&e, flags),
FmsSetFilter(e, {op=1, prop=2, family}, 1), FmsGetFilteredFontList, FmsGetFontProperty, FmsGetGDILogFont).
Flags=1 turns simulations on. `natfont2.ps1` reads WM_CHOOSEFONT_GETLOGFONT for each style and the 0x444 sample for
each script. `natfont3.ps1` dumps the combo item data (LOGFONT and font-type flags).

## 1. Which fonts are included, in what order
- GDI loads fonts in this order: Marlett (a system font that is not in the registry), then
  `HKLM\...\CurrentVersion\Fonts` in RegEnumValue order, then HKCU, then packaged fonts (AppX `windows.sharedFonts`).
  On this machine the packaged fonts are Windows Terminal's Cascadia*.ttf. They add Cascadia's italics, and Hebrew and
  Arabic to its scripts. Raster and vector fonts drop out (CF_TTONLY).
- Hidden fonts: comdlg32 always adds the filter {0x8000, prop 0x1a, 0} unless CF_INACTIVEFONTS is set. Prop 0x1a is
  fms's "shown" flag, which comes from the `Inactive Fonts` and auto-activation registry state (GetInactiveFontList,
  GetInputLanguageList). Nothing is hidden on this machine, so the C# port takes `hiddenFamilies` as a parameter.
- GDI faces:
  - Static face: lfFaceName is name ID 1 in the system LANGID, else en-US, truncated to 31 characters. lfWeight is
    usWeightClass. lfItalic is fsSelection bit 0.
  - Variable font: one face per fvar named instance. The name is built from STAT values in AxisOrdering order, with
    ELIDABLE values dropped; if nothing is left it is the elidedFallbackName. The GDI family is the typographic family
    (ID16, else ID1) plus those names, minus a 700 wght value and the ital axis. lfWeight is the wght coordinate.
- fms enumeration order (GdiEnumerateFamilies/GdiEnumFamiliesCallback, checked against the fms dump id for id):
  1. One face per GDI family, in first-load order. It is the face EnumFontFamiliesEx reports: upright first, then
     closest to weight 400.
  2. Then, for each fms family in the order of step 1, the remaining faces of each of its GDI families, in load order.
  3. A face already seen (same GDI family, weight and italic) is dropped. This is how a packaged copy of a system file
     disappears.

## 2. fms family and face names (GetFontNameTables @1800190a0, ResolveFontName @1800154b0)
- OS/2 values (GetFontOs2Table):
  - weight is usWeightClass; values 1..9 are multiplied by 100, and 1000 or more becomes 400.
  - stretch is usWidthClass, or 5 if it is outside 1..9.
  - style is 2 if fsSelection bit 9 (oblique) is set, else 1 if bit 0 (italic) is set, else 0.
  - The WWS flag is fsSelection bit 8.
- Name records (GetNameRecordsFromNameTable): IDs 1,2,3,4,5,8,16,17,21,22 on platform 3 with encoding 0 or 1. The
  language is tried as exact 0x409, then the primary language (mask 0x3ff), then any language, then Macintosh. The
  first family/face record found pins the language, and later records overwrite earlier ones.
- SelectFontFamilyAndFaceNameString @180019990: if the WWS bit is set, or ID21 or ID22 is missing, use (ID16 else ID1,
  ID17 else ID2). Otherwise use (ID21, ID22) and treat the face as WWS. Names of 65 bytes or more fail; the face then
  falls back to the GDI family plus WWS strings (GdiPopulateFontNameProps).
- Non-WWS faces go through ResolveFontName. The term table (107 entries @180044660) is in the code.
  1. Remove the first "regular" term (Book, Normal, Regular, Upright, Roman) from the face. If what is left does not
     already occur in the family, append it to the family.
  2. Remove one term from the family for each category, the first in table order: style (5..14), then stretch
     (15..39), then weight (40..64). For weight, the "X Face" variant is removed first. Abbreviations (EL..W#) count
     only when they are the whole face name at the family's end.
  3. Matching is FindSubstr @180004268. It searches backwards and matches whole words only, case-insensitively. The
     separators are space, '-', '.' and '_'. A needle space matches any run of separators or nothing, so
     "SemiCondensed" matches "Semi Condensed". '#' matches digits. The removed span includes the following separators,
     or the preceding ones when the word ends the string.
  4. The name's stretch overrides the OS/2 stretch when they disagree in direction. The name's style replaces the OS/2
     style. The name's weight is adopted unless:
     - both weights are below 400, or
     - both weights are 400 or 500, or
     - the name says above 500 and OS/2 is above 500 but not 700, or
     - OS/2 is neither 400 nor 700 and the two are less than 150 apart.
  5. If the family changed, or an OS/2 style or weight is not reflected in the name, rebuild the face with MakeFontName
     @180015270. The parts are [regular term if nothing else] + stretch + weight + style, each the matched term, or
     GetInvariantNameIx(value) when it came only from OS/2 (e.g. width 6 gives "Semi Expanded", weight 300 gives
     "Light"). MakeFontName removes Book/Normal/Regular/Upright from every part.
  6. Results: "Eras Bold ITC"/Regular becomes "Eras ITC"/"Bold". "Gill Sans Ultra Bold Condensed" becomes
     "Gill Sans"/"Condensed Ultra Bold". "Harlow Solid Italic" becomes "Harlow Solid"/"Semi Expanded Italic".
     "Arial Narrow"+(16/17) gives "Arial"/"Narrow Bold Italic".
- Variable instances: the fms family is the typographic family and the face is the STAT name. The fms weight, stretch
  and style still come from the file's OS/2, so they are all 400/5/0.

## 3. Simulated faces (AddFamilySimulatedFonts @180008a60, AddSimulatedFont @180008e50)
Families that contain variable instances get none.
- For each stretch, two bitmasks of the weights present: one for upright faces, one for slanted faces. The weight bit
  is GetPropertyWeightStringIx(GetNormalizedWeight(w)): 400 is bit 5, 500 is bit 7, 700 is bit 10. BOLD is 0x1bf00
  (weights of 600 and up). REG is 0xa0 (400 or 500).
- Walk the real faces in fms order:
  - Upright face whose weight is in REG:
    - Add a Bold twin if the upright mask has no BOLD bit, and the face is 500 or the upright mask has no 500.
    - Add a Bold Oblique twin if the slanted mask has no BOLD bit and no REG bit.
  - Every upright face: add an Oblique twin if the slanted mask lacks this face's weight bit, then set that bit.
  - Slanted face whose weight is in REG: add a Bold twin under the same rule as the upright Bold twin, using the
    slanted mask.
  - Adding a bold twin sets 0x400 in the mask it filled. That is why Bell MT's Bold Oblique (from Bold) blocks
    "Italic Bold", while Elephant and Goudy, which have no bold at the italic's width, get "Italic Bold".
- Twin name: MakeFontName(base face with one weight word removed if bold, "Bold"?, "Oblique"?, default "Regular").
  Examples: Regular gives Bold Oblique, Italic gives Italic Bold, Medium plus bold gives Bold.
- Twin values: fms weight is 700 if bold, style is 2 if oblique. The GDI LOGFONT is the base face's, with lfWeight 700
  if bold and lfItalic 255 if oblique. The font type has SIMULATED_FONTTYPE 0x8000 (comdlg32 GetFontType, props
  0x11 and 0x12).

## 4. comdlg32: style order (InsertStyleSorted @1800be8a8 via CBAddStyle)
- Items are inserted in fms order: real faces, then twins.
- A name that is already in the list is skipped (CB_FINDSTRINGEXACT).
- Position:
  - The list is ordered by lfWeight.
  - An upright face of equal weight goes before the first face of that weight.
  - An italic face of equal weight goes after that first face if it is upright, otherwise before it.
  - A twin compares only against twins.
- So Arial is Narrow|Narrow Italic|Italic|Regular because fms enumerates Regular, Narrow, Italic, Narrow Italic.

## 5. Family order
- Family names come from FmsGetFilteredPropertyList(prop 2).
- The combo box is CBS_SORT, which uses CompareString with NORM_IGNORECASE (word sort): hyphen and apostrophe are
  ignored, punctuation sorts before digits, and digits before letters. For example MingLiU_HKSCS-ExtB, then
  MingLiU_MSCS-ExtB, then MingLiU-ExtB.

## 6. Scripts (GetFontStylesAndSizes, FontScriptEnumProc, CBAddScript)
- The family's representative is FmsGetBestMatchInFamily: the real face nearest upright, width 5, weight 400. Its GDI
  lfFaceName is passed to EnumFontFamiliesExW with DEFAULT_CHARSET.
- GDI then enumerates every face of that GDI family in load order, including duplicates. For each face it lists the
  charsets from fontdrvhost's vFillIFICharsets:
  1. The ANSI code page's charset first, if ulCodePageRange1 has its bit and it is not Hebrew, Arabic or Thai.
  2. Then the remaining bits in the `fs`/`charsets` table order: Western, Japanese, Hangul, Johab, GB2312, Big5,
     Hebrew, Arabic, Greek, Turkish, Baltic, Central European, Cyrillic, Thai, Vietnamese, Symbol.
  3. A font with OS/2 version 0 or no code-page bits gets one charset: Symbol if it has a (3,0) cmap, else Western.
- No OEM or Mac entries appear.
- comdlg32 keeps the first occurrence of each script name. The name is string 0x950 plus the index in its charset table
  at @180168d70. The sample (static 0x444) is string 0x700 plus the charset; the samples were checked against the
  dialog.

## Not covered / assumptions
- The UI language is assumed to equal fms's invariant language (en-US). The second, localized pass of
  GetFontNameTables and FindLocFaceOnCombinedWSSPropsTable are not modeled.
- GDI's handling of DBCS charsets (the 0xFE marker) and of IsBogusSignature is not modeled.
- The OS/2 v0 glyph-probe path is not modeled.
- The rules for which face represents a GDI family, and for FmsGetBestMatchInFamily, were inferred and match every
  family on this machine, but the decompiled code was not traced for them.
