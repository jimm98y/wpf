// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// GlyphImager (gdiplus.dll 10.0.26100 arm64): one Line Services glyph run of the full imager on the
// device. Shared by every target of GpFullTextImager.
//
//   GlyphImager::Initialize @1800f5df0   r (+0x18), em (+0x1c), f78 = r / |world-to-device row|
//       (+0x78: ideal units per device pixel). A run is fitted to the device (+0x7c) unless the
//       format asks not to (0x20000000), its script is a control run, or -- for a script other
//       than 7..10 (Arabic, Syriac, Thaana, Devanagari) -- the face is fixed-pitch or the
//       realization is not grid-fitted with both margins present. Fitted: the device advances
//       (GetGdiCompatibleGlyphPlacements under world to device, GDI natural for ClearType) in
//       ideal units, rounded; a realization that does not fit takes Line Services' own.
//   GlyphImager::AdjustGlyphAdvances @1800f4e18   brings the device advances back towards Line
//       Services' nominal ones: leading and trailing blanks nominal; the difference absorbed by the
//       line's margins and its alignment (side bearings permitting), then spread over the spaces
//       or over the gaps between letters a device pixel at a time; what is left goes onto the
//       last glyph (TrailingAdjustCollector @180247d90), the cell origin moved (+0x220) by what
//       the leading side took.
//   GetDisplayCellOrigin @18003d690   the cell origin moved by +0x220 and, for a fitted run under
//       an axis-aligned transform, put on the device grid.
//   GlyphPlacementToGlyphOrigins @1800f59d0   the advances stepped from it along the line
//       (backwards for right-to-left), the offsets added, through world to device.
//

using System.Collections.Generic;
using Microsoft.Wpf.Interop.WebGpu.Composition.Text;
using GdipText = Microsoft.Wpf.Interop.WebGpu.Composition.Text.GdiPlusText;

namespace System.Drawing.WebGpuBackend.Gdip
{
    internal sealed class GpGlyphImager
    {
        // Initialize's state.
        public float R, Em, F78;
        public int Count;
        public ushort[] Glyphs;
        public ushort[] GlyphProps;
        public int[] Nominal;                // +0x30: Line Services' advances
        public int[] NomOffU, NomOffV;       // +0x38
        public int[] Device;                 // +0x88
        public int[] DevOffU, DevOffV;       // +0x118
        public int Flags, Align;             // +0x68 / +0x6c
        public int M70, M74;                 // the margins left at the line's two ends
        public int Shift, TrailOut;          // +0x220 / +0x224
        public bool Fitted;                  // +0x7c
        public bool Rtl;                     // +0x240
        public int Script, ItemFlags;
        public TrueTypeFont Face;
        public float Sx = 1f, Sy = 1f;       // world-to-device axis scales the device advances are taken under
        public int Mode;
        public GpMatrix W2D;
        public bool Path;                    // AddToPath: no realization, design advances
        static readonly bool s_quarterSnap = Environment.GetEnvironmentVariable ("WF_FTI_QSNAP") == "1";   // measured: GDI+ does not
        static readonly bool s_rotFit = Environment.GetEnvironmentVariable ("WF_FTI_ROTFIT") == "1";
        static readonly bool s_debug = Environment.GetEnvironmentVariable ("WF_FTI_DEBUG") == "1";

        /// <summary>GlyphImager::Initialize.</summary>
        public void Initialize (GpFullTextImager fti, GpFullTextImager.Run run, GpLineServices.Seg seg, GpMatrix w2d, int mode,
                                int leadMargin, int trailMargin, bool atStart, bool atEnd)
        {
            R = fti.R;
            Em = run.Em;
            Face = run.Face;
            Count = seg.GCount;
            Glyphs = new ushort [Count];
            GlyphProps = new ushort [Count];
            Array.Copy (run.Shape.Glyphs, seg.G0, Glyphs, 0, Count);
            Array.Copy (run.Shape.GlyphProps, seg.G0, GlyphProps, 0, Count);
            Nominal = seg.Adv; NomOffU = seg.OffU; NomOffV = seg.OffV;
            Flags = fti.FormatFlags;
            Align = fti.Format?.Align ?? 0;
            M70 = leadMargin; M74 = trailMargin;
            Rtl = run.Rtl;
            Script = run.Script;
            ItemFlags = run.ItemFlags;
            Mode = mode;
            W2D = w2d;
            bool sideways = (ItemFlags & 0x20) != 0 && (ItemFlags & 0x8) == 0;
            float a = sideways ? w2d.M21 : w2d.M11, b = sideways ? w2d.M22 : w2d.M12;
            float len2 = b * b + a * a;
            F78 = len2 <= 0f ? 0f : R / MathF.Sqrt (len2);
            Sx = MathF.Sqrt (w2d.M11 * w2d.M11 + w2d.M12 * w2d.M12);
            Sy = MathF.Sqrt (w2d.M21 * w2d.M21 + w2d.M22 * w2d.M22);
            bool special = (uint) ((Script + 0xf9) & 0xff) < 4;
            bool gridFit = mode == 1 || mode == 3 || mode == 5;
            // A turned or sheared transform: the realization is not fitted, its advances the design
            // ones with the kerning unrounded (measured: Arial under 30 degrees).
            const float tol = 1f / 65536f;
            float tl = MathF.Max (MathF.Abs (w2d.M11) + MathF.Abs (w2d.M12), MathF.Abs (w2d.M21) + MathF.Abs (w2d.M22)) * tol;
            bool quarterTurn = MathF.Abs (w2d.M11) <= tl && MathF.Abs (w2d.M22) <= tl, quarter = quarterTurn && !sideways;
            bool turned = !s_rotFit && !(MathF.Abs (w2d.M12) <= tl && MathF.Abs (w2d.M21) <= tl) && !quarterTurn;
            // FullTextImager::DrawGlyphs @18003b720 realizes a vertical line's non-upright item
            // under the world-to-device turned by 90 degrees (GetFontTransform, inlined: rotate when
            // the format's vertical bit and the item's upright bit 8 differ) and hands the
            // GlyphImager the plain world-to-device: the placements stay upright, but
            // GetGlyphStringSidebearings measures under the realization's quarter turn.
            QuarterCT = (quarter || (sideways && !quarterTurn && !turned)) && (mode == 5 || mode == 1 || mode == 3);
            BearingSx = sideways && !quarterTurn ? Sy : Sx;
            BearingSy = sideways && !quarterTurn ? Sx : Sy;
            Turned = turned;
            MirrorX = mode == 5 &&MathF.Abs (w2d.M12) <= tl && MathF.Abs (w2d.M21) <= tl && w2d.M11 < 0f;
            if ((Flags & 0x20000000) == 0 && Script != GpTextTables.ScriptControl
                && (special || (!Face.IsFixedPitch && (gridFit || leadMargin < 0 || trailMargin < 0)))) {
                Device = new int [Count]; DevOffU = new int [Count]; DevOffV = new int [Count];
                if (!gridFit && !special) {
                    Array.Copy (Nominal, Device, Count);
                    Array.Copy (NomOffU, DevOffU, Count);
                    Array.Copy (NomOffV, DevOffV, Count);
                } else {
                    // GetGdiCompatibleGlyphPlacements (TextShaping's GenericEngineGetGlyphPositions):
                    // each glyph's hinted device advance in whole pixels, the face's kerning added in
                    // pixels (otlValueRecord::adjustPos -> DesignToPP @18001ae98: the design value
                    // at the ppem, rounded half away), back in DIPs, times r.
                    // Under a transform that is not a uniform scale (dwrite's TransformToScaleFactor
                    // @1800902d0 says no) the shaper positions in design units: the kerning stays
                    // unrounded.
                    int upem = Face.UnitsPerEmForHinting;
                    bool uniform = TransformToScaleFactor (w2d, out float scale);
                    int ppem = GdipText.AxisPpem (Em * scale);
                    for (int i = 0; i < Count; i++) {
                        float px;
                        if (quarter && (mode == 5 || mode == 1 || mode == 3)) {
                            // A quarter-turned realization's advance is the sideways fit's span
                            // (GetGdiCompatibleGlyphMetrics isSideways). The classic measure is the
                            // same fit: MakeRasterizerFlagsForMeasuring @180091290 hints a matrix
                            // with a zero entry and GDI_CLASSIC adds 0x10, NewTransform's word 3,
                            // whose compatible widths fs__NewTransformation drops for m01 != 0 and
                            // whose bit 2 it toggles for m00 == 0 -- the sideways natural word.
                            // A bold simulation's outline is a device pixel wider there too.
                            GdipText.SidewaysMetrics (Face, Glyphs [i], Em, Sy, Sx, out int advDu, out _);
                            px = MathF.Floor (advDu * (Em * Sx / upem) + 0.5f)
                                 + (Face.SynthesizesBold && Face.DesignContours (Glyphs [i]).Count > 0 ? 1 : 0);
                        // GetGdiCompatibleGlyphPlacements measures GDI natural only for ClearType: a script
                        // that is placed though not grid-fitted (7..10 under AntiAlias) takes GDI classic.
                        } else px = GpTextShaper.DeviceAdvancePx (Face, Glyphs [i], Em, Sx, Sy, turned ? 2 : gridFit ? mode : 1) + MirrorPx (Glyphs [i], true);
                        if ((GlyphProps [i] & GpTextShaper.PropZeroWidth) != 0 && run.Script != GpTextTables.ScriptControl) px = 0f;
                        if (i + 1 < Count) {
                            int ku = GpTextShaper.Kern (Face, Script, Glyphs [i], Glyphs [i + 1]);
                            if (ku != 0) px += uniform && !turned ? DesignToPP (upem, ppem, ku) : ku * (Em * Sx / upem);
                        }
                        Device [i] = (int) MathF.Floor (px / Sx * R + 0.5f);
                    }
                }
                AdjustGlyphAdvances (0, Count, atStart, atEnd);
                Fitted = true;
            }
        }

        int[] Adv => Fitted ? Device : Nominal;

        /// <summary>A ClearType realization under an axis-aligned transform that mirrors x: the
        /// natural advances DirectWrite gives it (GdiPlusText.NaturalMetrics' mirrored rounding).</summary>
        public bool MirrorX;

        /// <summary>What mirroring changes a glyph's natural advance by, in device pixels: whole
        /// pixels (<paramref name="rounded"/>, GetGdiCompatibleGlyphPlacements) or the realization's own.</summary>
        float MirrorPx (int gid, bool rounded)
        {
            if (!MirrorX) return 0f;
            GdipText.NaturalMetrics (Face, gid, Em, Sx, Sy, false, out int up, out _, out _);
            GdipText.NaturalMetrics (Face, gid, Em, Sx, Sy, true, out int mi, out _, out _);
            if (up == mi) return 0f;
            float k = Em * Sx / Face.UnitsPerEmForHinting;
            return rounded ? MathF.Floor (mi * k + 0.5f) - MathF.Floor (up * k + 0.5f) : (mi - up) * k;
        }

        /// <summary>dwrite's TransformToScaleFactor @1800902d0: a transform that is a uniform scale
        /// (or a quarter turn of one), within 2^-16, and its scale.</summary>
        internal static bool TransformToScaleFactor (in GpMatrix m, out float scale)
        {
            const float tol = 1.52587890625e-05f;
            static bool Zero (float v) => v > -tol && v < tol;
            float a, b;
            if (Zero (m.M12)) {
                if (!Zero (m.M21)) { scale = 0f; return false; }
                a = m.M11; b = m.M22;
            } else {
                if (!Zero (m.M11) || !Zero (m.M22)) { scale = 0f; return false; }
                a = m.M12; b = m.M21;
            }
            float d = MathF.Abs (a) - MathF.Abs (b);
            if (d > -tol && d < tol) { scale = MathF.Abs (a); return true; }
            scale = 0f;
            return false;
        }

        /// <summary>TextShaping's DesignToPP @18001ae98: a design value at a ppem, rounded half
        /// away from zero in integers.</summary>
        internal static int DesignToPP (int upem, int ppem, int v)
        {
            if (upem == 0) return v;
            int bias = v < 0 ? 1 - (upem >> 1) : upem >> 1;
            return (bias + ppem * v) / upem;
        }

        /// <summary>GlyphImager::AdjustGlyphAdvances over glyphs [from, to).</summary>
        void AdjustGlyphAdvances (int from, int to, bool lead, bool trail)
        {
            int n = to - from;
            int[] dev = Device, nom = Nominal;
            if (s_debug) Console.Error.WriteLine ($"ADJ lead={lead} trail={trail} m70={M70} m74={M74} al={Align} g=[{string.Join (",", Glyphs)}] nom=[{string.Join (",", nom)}] dev=[{string.Join (",", dev)}]");
            int blank = Face.GlyphIndex (' ');
            int upem = Face.UnitsPerEmForHinting;
            int spaceNom = (int) MathF.Floor (Em * (float) Face.DesignAdvance (blank) * R / upem + 0.5f);
            int ls = 0;
            while (ls < n && Glyphs [from + ls] == blank && dev [from + ls] != 0) { dev [from + ls] = spaceNom; ls++; }
            int ts = 0, k = n, done = ls;
            while (done < n) {
                k--;
                if (Glyphs [from + k] != blank || dev [from + k] == 0) break;
                done++;
                dev [from + k] = spaceNom;
                ts++;
            }
            int mid = n - ts - ls;
            int lastAt = from + ls + mid - 1;   // the collector's glyph
            int adj = 0;
            bool IsSpace (int i) => Glyphs [from + ls + i] == blank && dev [from + ls + i] != 0;
            try {
                if (mid < 2) {
                    if (mid == 1) dev [from + ls] = nom [from + ls];
                    return;
                }
                if (ls != 0) M70 += spaceNom * ls;
                int spCount = 0, nsDev = 0, nsNom = 0, spNom = 0, spDev = 0, nsCount = 0;
                for (int i = 0; i < mid; i++) {
                    int g = from + ls + i;
                    if (IsSpace (i)) { spDev += dev [g]; spCount++; spNom += nom [g]; }
                    else {
                        nsDev += dev [g]; nsNom += nom [g];
                        if ((GlyphProps [g] & GpTextShaper.PropDiacritic) == 0) nsCount++;
                    }
                }
                int delta = spNom - spDev - nsDev + nsNom;
                bool rtlFmt = (Flags & 1) != 0 && (Flags & 2) == 0;
                int al = Align;
                if (Rtl != rtlFmt) { if (al == 0) al = 2; else if (al == 2) al = 0; }
                bool skipZeroCheck = false;
                if ((Flags & 4) == 0 && (lead || trail)) {
                    SideBearings (from + ls, mid, out int lsb16, out int rsb16);
                    if (s_debug) Console.Error.WriteLine ($"ADJ sb lsb16={lsb16} rsb16={rsb16} f78={F78} delta={delta} qct={QuarterCT}");
                    if (lead) {
                        int v = (int) MathF.Floor (F78 * lsb16 * 0.0625f + 0.5f);
                        if (v < 0) {
                            v += M70; M70 = v;
                            if (v < 0) { M70 = 0; delta += v; Shift -= v; }
                        } else if (v > 0 && delta < 0 && al != 0) { delta += v; Shift -= v; }
                    }
                    if (trail) {
                        int v = (int) MathF.Floor (F78 * rsb16 * 0.0625f + 0.5f);
                        if (v < 0) {
                            v += M74; M74 = v;
                            if (v < 0) { delta += v; M74 = 0; }
                        } else if (v >= 1 && delta < 0) {
                            if (al != 2) delta += v;
                            else skipZeroCheck = true;
                        }
                    }
                }
                if (!skipZeroCheck && delta == 0) return;
                if (nsCount + spCount < 2) {
                    Shift += delta / 2;
                    adj = delta - delta / 2;
                    return;
                }
                int emIdeal = (int) MathF.Floor (Em * R + 0.5f);
                int taken = 0;
                if (al == 0) {
                    if (trail) {
                        int t = -M74;
                        if (delta < t) { delta += M74; taken = t; adj = t; }
                        else {
                            adj = delta;
                            if (delta < emIdeal) return;
                            delta -= emIdeal; taken = emIdeal; adj = emIdeal;
                        }
                    }
                } else if (al == 1) {
                    if (lead && trail) {
                        int mn = Math.Min (M70, M74);
                        if (-2 * mn <= delta) {
                            Shift += delta / 2;
                            adj = delta - delta / 2;
                            return;
                        }
                        delta += -2 * mn;
                        int half = (2 * mn) / 2;
                        taken = 2 * mn - half;
                        Shift += half;
                        adj = taken;
                    }
                } else if (al == 2 && lead) {
                    if (delta < -M70) { delta += M70; Shift -= M70; }
                    else {
                        if (delta < emIdeal) { Shift += delta; return; }
                        delta -= emIdeal; Shift += emIdeal;
                    }
                }
                int minSp = (int) MathF.Floor ((float) (Em * R / 6.0 + 0.5));
                bool special = (uint) ((Script + 0xf9) & 0xff) < 4;
                bool overSpaces = false;
                if (spCount >= 1) {
                    if (spNom < delta) overSpaces = delta >= 1 && special;
                    else {
                        int lim = Math.Max (spNom / 2, spCount * minSp);
                        overSpaces = !(delta < lim - spNom) || (delta >= 1 && special);
                    }
                } else if (delta > 0 && special) {
                    Shift += delta / 2;
                    adj = taken - delta / 2 + delta;
                    return;
                }
                if (overSpaces) {
                    int per = (spDev + spCount / 2 + delta) / spCount;
                    for (int i = 0; i < mid; i++) if (IsSpace (i)) dev [from + ls + i] = per;
                    return;
                }
                // Over the gaps between letters.
                int spW = minSp;
                if (spCount == 0) spW = 0;
                else {
                    if (delta >= 0) spW = (2 * spNom) / spCount;
                    delta = spDev - spCount * spW + delta;
                }
                int runs = 0;
                for (int j = 0; j < mid;) {
                    if (IsSpace (j)) {
                        do { j++; } while (j < mid && IsSpace (j));
                        runs++;
                    } else {
                        while (j < mid && !IsSpace (j)) j++;
                    }
                }
                int gaps = nsCount - runs - 1;
                int px = (int) MathF.Floor (F78 + 0.5f);
                int per2, extra;
                if (gaps < 1) {
                    if (spCount == 0) return;
                    spW += (delta + spCount / 2) / spCount;
                    per2 = 0; extra = 0;
                } else if (px < 1) {
                    per2 = delta / gaps; extra = 0;
                } else {
                    int perPx = delta / px;
                    int q = perPx / gaps;
                    extra = (-(px * q * gaps) - px / 2 + delta) / px;
                    if (extra < 0) { per2 = (int) MathF.Floor ((q - 1) * F78 + 0.5f); extra += gaps; }
                    else per2 = (int) MathF.Floor (q * F78 + 0.5f);
                }
                bool prevSpace = IsSpace (0);
                for (int i = 1; i <= mid; i++) {
                    if (prevSpace) {
                        dev [from + ls + i - 1] = spW;
                        if (i < mid) prevSpace = IsSpace (i);
                        continue;
                    }
                    for (; i < mid; i++) {
                        if ((GlyphProps [from + ls + i] & GpTextShaper.PropDiacritic) != 0) continue;
                        if (!IsSpace (i)) {
                            int add = extra >= 1 ? px : 0;
                            extra--;
                            dev [from + ls + i - 1] += add + per2;
                        }
                        prevSpace = IsSpace (i);
                        break;
                    }
                }
            } finally {
                if (lastAt >= 0 && lastAt < dev.Length) dev [lastAt] += adj;
                if (trail) TrailOut = -adj;
                if (s_debug) Console.Error.WriteLine ($"ADJ out adj={adj} shift={Shift} dev=[{string.Join (",", dev)}]");
            }
        }

        /// <summary>GpFaceRealization::GetGlyphStringSidebearings: the least left and right side
        /// bearings, in sixteenths of a pixel, of the glyphs within the line height of each end.</summary>
        void SideBearings (int from, int count, out int left, out int right)
        {
            int upem = Face.UnitsPerEmForHinting;
            float scale = Em * (QuarterCT ? BearingSx : Sx) / upem;
            GdipText.DeviceAscentDescent (Face, Em / upem * Sx, out int asc, out int desc);
            int lim = (asc + desc) * 32;
            int cum = 0;
            left = lim;
            for (int i = from; i < from + count; i++) {
                if (cum >= lim) break;
                Bearings (Glyphs [i], out int adv, out int lsb, out _);
                left = Math.Min (left, cum + (int) (lsb * scale * 16f));
                cum += (int) (adv * scale * 16f);
            }
            cum = 0;
            right = lim;
            for (int i = from + count - 1; i >= from; i--) {
                if (cum >= lim) break;
                Bearings (Glyphs [i], out int adv, out _, out int rsb);
                right = Math.Min (right, cum + (int) (rsb * scale * 16f));
                cum += (int) (adv * scale * 16f);
            }
        }

        /// <summary>The metrics GetGlyphStringSidebearings reads: under the realization's own
        /// transform, so a quarter-turned ClearType one is measured sideways; upright, GDI natural for
        /// ClearType, GDI classic for the other grid-fitted realizations, design otherwise.</summary>
        void Bearings (int gid, out int adv, out int lsb, out int rsb)
        {
            if (Turned) {
                // A turned transform is not a measuring one: FontFace::GetGlyphMetrics @18002d488
                // answers with GetDesignGlyphMetrics -- with the face's simulations: a bold one
                // widens the advance and the box by round(upem / 50), an oblique one shears the
                // glyf box's corners (Microsoft Sans Serif 'o': lsb 72 -> 63, rsb 72 -> -296).
                adv = GpTextShaper.DesignAdvance (Face, gid);
                if (!Face.TryGetDesignXExtent (gid, out int xMin, out int xMax)
                    || !Face.TryGetNotionalMetrics (gid, out _, out _, out int yMin, out int yMax)) { lsb = 0; rsb = adv; return; }
                float s = Face.ObliqueShearApplied;
                float x0 = xMin + MathF.Min (s * yMin, s * yMax), x1 = xMax + MathF.Max (s * yMin, s * yMax) + (adv - Face.DesignAdvance (gid));
                lsb = (int) MathF.Floor (x0 + 0.5f);
                rsb = adv - (int) MathF.Floor (x1 + 0.5f);
            } else if (QuarterCT) {
                GdipText.SidewaysMetrics (Face, gid, Em, BearingSy, BearingSx, out adv, out _);
                GdipText.SidewaysBearings (Face, gid, Em, BearingSy, BearingSx, out lsb, out rsb);
            } else if (Mode == 1 || Mode == 3) GpTextShaper.ClassicMetrics (Face, gid, Em * Sx, out adv, out lsb, out rsb);
            else if (Mode == 2 || Mode == 4) GdipText.DesignMetrics (Face, gid, out adv, out lsb, out rsb);
            else GdipText.NaturalMetrics (Face, gid, Em, Sx, Sx, MirrorX, out adv, out lsb, out rsb);
        }

        /// <summary>A transform that is neither axis-aligned nor a quarter turn.</summary>
        public bool Turned;

        /// <summary>A ClearType realization under a quarter turn (its glyphs fitted sideways).</summary>
        public bool QuarterCT;

        /// <summary>The realization's axis scales where QuarterCT measures (a vertical item's are turned).</summary>
        float BearingSx = 1f, BearingSy = 1f;

        /// <summary>GetDisplayCellOrigin.</summary>
        public PointF CellOrigin (PointF world)
        {
            PointF p = world;
            int sh = Rtl ? -Shift : Shift;
            if (Shift != 0) {
                if ((Flags & 2) == 0) p.X += sh / R;
                else p.Y += sh / R;
            }
            if (Fitted && s_quarterSnap && (W2D.Complexity & ~3) != 0) {
                float tl = MathF.Max (Sx, Sy) / 65536f;
                if (MathF.Abs (W2D.M11) <= tl && MathF.Abs (W2D.M22) <= tl) {
                    // A quarter turn is put on the device grid as an axis scale is.
                    var d = new[] { p };
                    W2D.Transform (d);
                    d [0] = new PointF (MathF.Floor (d [0].X + 0.5f), MathF.Floor (d [0].Y + 0.5f));
                    GpMatrix inv = W2D;
                    if (inv.Invert ()) { inv.Transform (d); p = d [0]; }
                }
            }
            if (Fitted && (W2D.Complexity & ~3) == 0) {
                float m11 = W2D.M11, m22 = W2D.M22;
                if (m11 != 0f) p.X = MathF.Floor (p.X * m11 + 0.5f) / m11;
                if (m22 != 0f) p.Y = MathF.Floor (p.Y * m22 + 0.5f) / m22;
            }
            return p;
        }

        /// <summary>GlyphPlacementToGlyphOrigins: world origins stepped from the cell origin, to the device.</summary>
        public PointF[] Origins (PointF cell, bool vertical)
        {
            int[] adv = Adv;
            int[] ou = Fitted ? DevOffU : NomOffU, ov = Fitted ? DevOffV : NomOffV;
            var o = new PointF [Count];
            float x = cell.X, y = cell.Y;
            for (int i = 0; i < Count; i++) {
                o [i] = new PointF (x, y);
                float d = adv [i] / R;
                if (!vertical) x = Rtl ? x - d : d + x;
                else y = Rtl ? y - d : d + y;
            }
            if (Rtl && (ItemFlags & 0x10) == 0) {
                // Right to left: each glyph's origin is at its right; back to its left by its ideal advance.
                // GetGlyphStringIdealAdvanceVector: the realization's advance (the device's hinted
                // one, in ideal units through +0x78) -- for a path, the design advance at em * r.
                int upem = Face.UnitsPerEmForHinting;
                for (int i = 0; i < Count; i++) {
                    int ideal = Path
                        ? (int) MathF.Floor (GpTextShaper.DesignAdvance (Face, Glyphs [i]) * (Em * R / upem) + 0.5f)
                        : (int) MathF.Floor ((GpTextShaper.RealizationAdvancePx (Face, Glyphs [i], Em, Sx, Sy, Mode) + MirrorPx (Glyphs [i], false)) * F78 + 0.5f);
                    if (!vertical) o [i].X -= ideal / R;
                    else o [i].Y -= ideal / R;
                }
            }
            if (ou != null)
                for (int i = 0; i < Count; i++) {
                    if (!vertical) { o [i].X = Rtl ? o [i].X - ou [i] / R : ou [i] / R + o [i].X; o [i].Y -= ov [i] / R; }
                    else { o [i].X = ov [i] / R + o [i].X; o [i].Y = Rtl ? o [i].Y - ou [i] / R : ou [i] / R + o [i].Y; }
                }
            W2D.Transform (o);
            return o;
        }

        /// <summary>The advances the line ends up with (DrawGlyphs' out-parameter): fitted or Line Services'.</summary>
        public int[] FinalAdvances => Adv;
    }
}
