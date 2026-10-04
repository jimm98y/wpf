// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// GDI+'s metafile driver, Globals::MetaDriver (DriverMeta), managed (gdiplus.dll 10.0.26100, arm64,
// public PDB): what an EmfPlusDual or EmfOnly recording renders down-level into the metafile's own
// HDC, which is GDI's EMF DC (GpEmfDc). GpGraphics::GetForMetafile @180079480 sets DownLevel for
// every type but EmfPlusOnly and installs this driver; each GpGraphics verb records its EMF+ record
// and then renders through it with the device draw bounds and the context's world-to-device matrix:
//
//   DpContext::GetHdc @180037df8   the HDC, saved once (SaveDC) and cleaned (CleanTheHdc @180037c28:
//                                  ICM off; map mode, origins, ROP2 and clip put back only if changed)
//   DpContext::ResetHdc            the saved level restored (NoOpPatBlt, GetHdc, the end)
//   DpDriver::SetupClipping @1800dea20 / RestoreClipping @18022b0b0   nothing when the draw bounds
//                                  are wholly visible; else SaveDC and IntersectClipRect (a rectangle)
//                                  or ExtSelectClipRgn(RGN_AND) (a complex region); RestoreDC(-1)
//   DriverMeta::FillRects @1800d4d40 + ConvertRectFToGdi @180033c80 / Fill @1800340b8 / AlphaFill
//                                  @1800d8b60   rectangles as 5-point polygons with a null pen, at
//                                  the increased resolution (GetIncreasedResolutionMultiplier
//                                  @180223cc8: x16 while the bounds fit in 11 bits, then 8, 4, 2)
//                                  under a 1/16 MODIFYWORLDTRANSFORM
//   DriverMeta::FillPath @1800d4a90 / StrokePath @1800d5ef0 / StrokeAndFillWidenedPath @1800d5c58
//                                  + ConvertPathToGdi @1800d8248 (Fill @1800d95c0, Draw @1800d9270,
//                                  FillAndDraw @1800d9748, AlphaFill @1800d8a50, AndClip @1802227b8,
//                                  DrawMixedPath @1802232c8, TransformPoints @1800da4d8,
//                                  GetDeviceBounds @180223c10) and ConvertPenToGdi @1800d8620
//                                  (ExtCreatePen geometric pens, the miter limit set and put back)
//   DriverMeta::FillRegion @1800d5030 + ConvertRegionToGdi @1800cbe30   FillRgn
//   DriverMeta::GetBrush @1800d5a80, ConvertBrushToGdi::SetColor @1800342c8 (the one solid brush the
//                                  driver keeps), hatch brushes as 8x8 DIB patterns
//   ConvertAlphaToGdi::SetAlpha @1800d9c70 + CreateAlphaBrush @1800d9000   translucency as the XOR
//                                  trick: PATINVERT the colour, AND (DPa) an ordered-dither mask,
//                                  PATINVERT again; the mask's phase steps on every forced use
//   DriverMeta::BrushFillUsingBitmap @1800d3728 / DrawImage @1800d46b0   see GpMetaDriver.Bitmap.cs
//
// The driver is one object for the whole process, so its cached brushes outlive a recording (the
// next file records them afresh, GpEmfDc keeps which objects a file holds).
//

using System.Collections.Generic;
using System.Drawing.Drawing2D;

namespace System.Drawing.WebGpuBackend.Gdip
{
    /// <summary>Globals::MetaDriver's state: the solid brush it keeps (ConvertBrushToGdi at +0x28),
    /// the alpha mask brush (ConvertAlphaToGdi at +0x58), and the mask's phase (DAT_1802e8398).</summary>
    internal static class GpMetaDriverState
    {
        public static readonly object Lock = new object();
        public static GpEmfDc.GdiObject Brush;
        public static int BrushType;
        public static uint BrushColor;
        public static bool BrushValid;
        public static GpEmfDc.GdiObject AlphaBrush;
        public static uint AlphaLevel;
        public static uint AlphaPhase;

        static GpMetaDriverState() => Reset();

        /// <summary>The state of a freshly loaded gdiplus.dll (DriverMeta's constructor).</summary>
        public static void Reset()
        {
            lock (Lock)
            {
                Brush = GpEmfDc.Stock(0);
                BrushType = 0;
                BrushColor = 0xffffff;
                BrushValid = true;
                AlphaBrush = GpEmfDc.Stock(4);
                AlphaLevel = 0xff;
                AlphaPhase = 0;
            }
        }
    }

    internal sealed partial class GpMetafileRecorder
    {
        GpEmfDc _dc;
        int _hdcLevel;          // DpContext +0x290: the SaveDC level GetHdc left

        internal GpEmfDc Dc => _dc ?? (_dc = new GpEmfDc(Emf));

        // ---- DpContext::GetHdc / ResetHdc --------------------------------------------------------

        GpEmfDc ContextHdc()
        {
            GpEmfDc dc = Dc;
            if (_hdcLevel == 0)
            {
                _hdcLevel = dc.SaveDC();
                CleanTheHdc(dc);
            }
            return dc;
        }

        void ResetHdc()
        {
            if (_hdcLevel == 0) return;
            Dc.RestoreDC(_hdcLevel);
            _hdcLevel = 0;
        }

        // DpContext::CleanTheHdc with +0x284 set (a metafile's context): ask, and put back only what
        // is not GDI's default.
        static void CleanTheHdc(GpEmfDc dc)
        {
            dc.SetICMMode(1);
            int mm = dc.GetMapMode();
            Point vp = dc.GetViewportOrg(), wo = dc.GetWindowOrg();
            int rop = dc.GetROP2();
            bool clip = dc.HasClip;
            if (mm != 1) dc.SetMapMode(1);
            if (vp.X != 0 || vp.Y != 0) dc.SetViewportOrgEx(0, 0);
            if (wo.X != 0 || wo.Y != 0) dc.SetWindowOrgEx(0, 0);
            if (rop != 13) dc.SetROP2(13);
            if (clip) dc.SelectClipRgn(null);
        }

        // ---- the visible clip and DpDriver::SetupClipping ----------------------------------------

        /// <summary>The context's visible clip (+0x130) in device pixels: the application clip under
        /// the container's.</summary>
        DpRegion VisibleClip()
        {
            DpRegion app = _state.Clip == null ? DpRegion.Infinite() : _state.Clip.Device(GpMatrix.CreateIdentity());
            if (_state.ContainerClip != null) app = DpRegion.Combine(app, _state.ContainerClip, DpRegion.Op.And);
            return app;
        }

        /// <summary>GpGraphics::BeginContainer: the container begins under the visible clip, its own
        /// application clip infinite.</summary>
        void BeginContainerClip()
        {
            if (_state.Clip != null || _state.ContainerClip != null) _state.ContainerClip = VisibleClip();
            _state.Clip = null;
        }

        static bool Contains(DpRegion r, Rectangle rc)
        {
            if (r.IsInfinite) return true;
            if (rc.Width <= 0 || rc.Height <= 0) return true;
            return DpRegion.Combine(DpRegion.FromRect(rc.X, rc.Y, rc.Width, rc.Height), r, DpRegion.Op.Exclude).IsEmpty;
        }

        bool SetupClipping(GpEmfDc dc, Rectangle bounds)
        {
            DpRegion vis = VisibleClip();
            if (Contains(vis, bounds)) return false;
            if (vis.IsRect || vis.IsEmpty)
            {
                dc.SaveDC();
                if (!vis.IsInfinite)
                {
                    Rectangle b = vis.IsEmpty ? Rectangle.Empty : vis.Bounds;
                    dc.IntersectClipRect(b.Left, b.Top, b.Right, b.Bottom);
                }
                return true;
            }
            // Every DriverMeta caller asks for path clipping first (SetupPathClipping @1800deb48).
            if (SetupPathClipping(dc, vis)) return true;
            dc.SaveDC();
            dc.ExtSelectClipRgn(vis, 1);
            return true;
        }

        /// <summary>SetupPathClipping @1800deb48: an application clip that is one path, with no
        /// container clip, is that path (and the surface's bounds when the path goes past them);
        /// anything else is the visible region's outline (GpPath::GpPath(DpRegion*)) as a path.</summary>
        bool SetupPathClipping(GpEmfDc dc, DpRegion vis)
        {
            GpRegion app = _state.Clip;
            PathToGdi cp;
            if (app != null && app.IsLeaf && _state.ContainerClip == null)
            {
                RectangleF mb = MetafileDeviceBounds();
                int left = (int)mb.Left, top = (int)mb.Top, right = (int)mb.Right, bottom = (int)mb.Bottom;
                if (app.Type != GpRegion.NodePath)
                {
                    Rectangle b = vis.IsEmpty ? Rectangle.Empty : vis.Bounds;
                    Rectangle r = Rectangle.Intersect(b, Rectangle.FromLTRB(left, top, right, bottom));
                    dc.SaveDC();
                    dc.IntersectClipRect(r.Left, r.Top, r.Right, r.Bottom);
                    return true;
                }
                var path = new GpPath(app.Points, app.Types, app.Fill);
                cp = new PathToGdi(path, GpMatrix.CreateIdentity(), 0x10, null);
                if (!cp.Valid) return false;
                dc.SaveDC();
                cp.AndClip(dc, null);
                Rectangle pb = vis.IsEmpty ? Rectangle.Empty : vis.Bounds;
                if (pb.Left < left || right < pb.Right || pb.Top < top || bottom < pb.Bottom)
                    dc.IntersectClipRect(left, top, right, bottom);
                return true;
            }
            GpPath outline = GpRegionToPath.Convert(vis);
            if (outline == null) return false;
            cp = new PathToGdi(new GpPath(outline.PointArray(), outline.TypeArray(), System.Drawing.Drawing2D.FillMode.Alternate), GpMatrix.CreateIdentity(), 0x10, null);
            if (!cp.Valid) return false;
            dc.SaveDC();
            cp.AndClip(dc, null);
            return true;
        }

        static void RestoreClipping(GpEmfDc dc, bool saved)
        {
            if (saved) dc.RestoreDC(-1);
        }

        // ---- device rectangles -------------------------------------------------------------------

        static GpMatrix ToMatrix(GpMat m) => new GpMatrix(m.M11, m.M12, m.M21, m.M22, m.Dx, m.Dy);

        GpMatrix DeviceMatrix => ToMatrix(WorldToDevice);

        static int Floor(float v) => GpMetafileFormat.Floor(v);
        static int Ceil(float v) => -GpMetafileFormat.Floor(-v);

        /// <summary>RenderFillPath / RenderDrawPath / BoundsFToRect: the device box floored and ceiled
        /// into the drawing rectangle, or null when it is empty or out of range.</summary>
        static Rectangle? DrawRect(RectangleF? box)
        {
            if (!box.HasValue) return null;
            RectangleF b = box.Value;
            const float eps = 1.1920928955078125e-07f, lo = -1073741824f, hi = 1073741824f;
            if (Math.Abs(b.Width) < eps || Math.Abs(b.Height) < eps) return null;
            if (b.X < lo || b.X > hi || b.Y < lo || b.Y > hi) return null;
            if (b.Width < 0f || b.Width > hi || b.Height < 0f || b.Height > hi) return null;
            int x = Floor(b.X), y = Floor(b.Y);
            int r = Ceil(b.X + b.Width), bt = Ceil(b.Y + b.Height);
            return new Rectangle(x, y, r - x + 1, bt - y + 1);
        }

        /// <summary>GetIncreasedResolutionMultiplier @180223cc8.</summary>
        static int IncreasedResolutionMultiplier(Rectangle r)
        {
            int x = r.X, y = r.Y, right = r.Width + x, bottom = r.Height + y;
            if (x >= -0x7ff)
            {
                if (-0x800 < y && right < 0x800 && bottom < 0x800) return 16;
            }
            else if (x <= -0x1000)
            {
                if (x < -0x1fff)
                {
                    if (x < -0x3fff) return 1;
                    goto two;
                }
                goto four;
            }
            if (-0x1000 < y && right < 0x1000 && bottom < 0x1000) return 8;
        four:
            if (-0x2000 < y && right < 0x2000 && bottom < 0x2000) return 4;
        two:
            if (-0x4000 < y && right < 0x4000 && bottom <= 0x3fff) return 2;
            return 1;
        }

        static float[] Xform(float s) => new[] { s, 0f, 0f, s, 0f, 0f };

        /// <summary>SetupForIncreasedResolution @180224320: GM_ADVANCED and a 1/m world transform.</summary>
        static int SetupForIncreasedResolution(int m, GpEmfDc dc)
        {
            if (m <= 1) return 2;
            int mode = dc.GetGraphicsMode();
            if (mode != 2) dc.SetGraphicsMode(2);
            dc.ModifyWorldTransform(Xform(1.0f / (float)m), 2);
            return mode;
        }

        /// <summary>CleanupForIncreasedResolution @180164508.</summary>
        static void CleanupForIncreasedResolution(int m, int mode, GpEmfDc dc)
        {
            if (m <= 1) return;
            dc.ModifyWorldTransform(Xform((float)m), 2);
            if (mode != 2) dc.SetGraphicsMode(mode);
        }

        // ---- brushes: DriverMeta::GetBrush, ConvertBrushToGdi, ConvertAlphaToGdi ----------------

        /// <summary>DpBrush's type: solid 0, hatch 1, texture 2, path gradient 3, linear gradient 4.</summary>
        static int BrushType(Brush b)
        {
            switch (b)
            {
                case SolidBrush _: return 0;
                case HatchBrush _: return 1;
                case TextureBrush _: return 2;
                case PathGradientBrush _: return 3;
                case LinearGradientBrush _: return 4;
                default: return 0;
            }
        }

        static uint ColorRef(Color c) => (uint)(c.R | (c.G << 8) | (c.B << 16));

        // AverageColors @18006cf40 (two) and @18006d000 (four).
        static uint Average(params Color[] cs)
        {
            float r = 0, g = 0, b = 0;
            foreach (Color c in cs) { r = c.R + r; g = c.G + g; b = c.B + b; }
            float k = cs.Length == 2 ? 0.5f : 0.25f;
            int ir = Floor(r * k + 0.5f), ig = Floor(g * k + 0.5f), ib = Floor(b * k + 0.5f);
            return (uint)((ir & 0xff) | (ig & 0xff) << 8 | (ib & 0xff) << 16);
        }

        /// <summary>ToCOLORREF @1800704d0: a brush's one colour.</summary>
        static uint ToColorRef(Brush b)
        {
            switch (b)
            {
                case SolidBrush s: return ColorRef(s.Color);
                case HatchBrush h: return Average(h.ForegroundColor, h.BackgroundColor);
                case TextureBrush _: return 0x808080;
                case PathGradientBrush p:
                {
                    Color[] sc = p.SurroundColors;
                    return Average(p.CenterColor, sc != null && sc.Length > 0 ? sc[0] : p.CenterColor);
                }
                case LinearGradientBrush l:
                {
                    Color[] lc = l.LinearColors;
                    return Average(lc[0], lc[1], lc[0], lc[1]);
                }
                default: return 0;
            }
        }

        /// <summary>ConvertBrushToGdi::SetColor: the driver's one solid brush, made again only when
        /// its colour changes.</summary>
        static GpEmfDc.GdiObject SetColor(GpEmfDc dc, uint color)
        {
            if (GpMetaDriverState.BrushValid)
            {
                if (GpMetaDriverState.BrushType == 0 && color == GpMetaDriverState.BrushColor) return GpMetaDriverState.Brush;
                dc.DeleteObject(GpMetaDriverState.Brush);
            }
            GpMetaDriverState.BrushType = 0;
            GpMetaDriverState.Brush = GpEmfDc.CreateSolidBrush(color);
            GpMetaDriverState.BrushColor = color;
            GpMetaDriverState.BrushValid = true;
            return GpMetaDriverState.Brush;
        }

        // The 53 hatch patterns as DriverMeta::GetBrush reads them (@1802b1bb0), 8 rows of 8 pixels.
        static readonly byte[] HatchRows = Convert.FromHexString(
            "ff00000000000000808080808080808080402010080402010102040810204080" +
            "ff80808080808080814224181824428180000000080000008000080080000800" +
            "88002200880022008822882288228822aa44aa11aa44aa11aa55aa51aa55aa15" +
            "aa55aa55aa55aa55ee55bb55ee55bb5577dd77dd77dd77dd77ffddff77ffddff" +
            "effffeffeffffefffffffff7ffffff7f88442211884422111122448811224488" +
            "cc663399cc6633993366cc993366cc99c1e070381c0e078383070e1c3870e0c1" +
            "8888888888888888ff000000ff0000005555555555555555ff00ff00ff00ff00" +
            "ccccccccccccccccffff0000ffff000000008844221100000000112244880000" +
            "f00000000f00000080808080080808088008400210012004b130031bd8c00c8d" +
            "8142241881422418001825c0001825c00102040818244281ff808080ff080808" +
            "8854224588142251aa55aa55f0f0f0f00010081000800180aa00800080008000" +
            "8000220008002200038448300c020101ff66ff99ff66ff9977898f8f7798f8f8" +
            "ff888888ff8888889966669999666699f0f0f0f00f0f0f0f8244281028448201" +
            "10387cfe7c381000");

        /// <summary>DriverMeta::GetBrush: a GDI brush for the brush, its alpha (0 = draw nothing, 0xff
        /// = opaque) and whether the caller deletes it; null when only a bitmap can render it.</summary>
        static GpEmfDc.GdiObject GetBrush(GpEmfDc dc, Brush brush, out uint alpha, out bool delete)
        {
            delete = false;
            switch (brush)
            {
                case SolidBrush s:
                    if (s.Color.A > 1)
                    {
                        alpha = s.Color.A;
                        return SetColor(dc, ColorRef(s.Color));
                    }
                    alpha = 0;
                    return GpEmfDc.Stock(5);
                case HatchBrush h:
                {
                    byte fa = h.ForegroundColor.A, ba = h.BackgroundColor.A;
                    if (fa < 0xfe)
                    {
                        if (fa < 2 && ba < 2) { alpha = 0; return GpEmfDc.Stock(5); }
                    }
                    else if (ba > 0xfd)
                    {
                        delete = true;
                        alpha = 0xff;
                        int style = (int)h.HatchStyle;
                        if ((uint)style >= 0x35) style = 12;
                        var p = new byte[40 + 8 + 32];
                        Le.W32(p, 0, 0x28); Le.W32(p, 4, 8); Le.W32(p, 8, 8);
                        Le.W16(p, 12, 1); Le.W16(p, 14, 1);
                        Color bk = h.BackgroundColor, fg = h.ForegroundColor;
                        p[40] = bk.B; p[41] = bk.G; p[42] = bk.R;
                        p[44] = fg.B; p[45] = fg.G; p[46] = fg.R;
                        for (int i = 0; i < 8; i++) p[48 + i * 4] = HatchRows[style * 8 + 7 - i];
                        return GpEmfDc.CreateDIBPatternBrushPt(p, 0);
                    }
                    break;
                }
            }
            alpha = 0xff;
            return null;
        }

        // The ordered-dither thresholds CreateAlphaBrush reads (HT_8x8 @1802aea50, HT_16x16 @1802aa950).
        static readonly byte[] HT8 = {
            1, 129, 33, 161, 9, 137, 41, 169, 193, 65, 225, 97, 201, 73, 233, 105,
            49, 177, 17, 145, 57, 185, 25, 153, 241, 113, 209, 81, 249, 121, 217, 89,
            13, 141, 45, 173, 5, 133, 37, 165, 205, 77, 237, 109, 197, 69, 229, 101,
            61, 189, 29, 157, 53, 181, 21, 149, 253, 125, 221, 93, 245, 117, 213, 85,
        };

        internal static readonly byte[] HT16 = {
            0, 128, 32, 160, 8, 136, 40, 168, 2, 130, 34, 162, 10, 138, 42, 170,
            192, 64, 224, 96, 200, 72, 232, 104, 194, 66, 226, 98, 202, 74, 234, 106,
            48, 176, 16, 144, 56, 184, 24, 152, 50, 178, 18, 146, 58, 186, 26, 154,
            240, 112, 208, 80, 248, 120, 216, 88, 242, 114, 210, 82, 250, 122, 218, 90,
            12, 140, 44, 172, 4, 132, 36, 164, 14, 142, 46, 174, 6, 134, 38, 166,
            204, 76, 236, 108, 196, 68, 228, 100, 206, 78, 238, 110, 198, 70, 230, 102,
            60, 188, 28, 156, 52, 180, 20, 148, 62, 190, 30, 158, 54, 182, 22, 150,
            252, 124, 220, 92, 244, 116, 212, 84, 254, 126, 222, 94, 246, 118, 214, 86,
            3, 131, 35, 163, 11, 139, 43, 171, 1, 129, 33, 161, 9, 137, 41, 169,
            195, 67, 227, 99, 203, 75, 235, 107, 193, 65, 225, 97, 201, 73, 233, 105,
            51, 179, 19, 147, 59, 187, 27, 155, 49, 177, 17, 145, 57, 185, 25, 153,
            243, 115, 211, 83, 251, 123, 219, 91, 241, 113, 209, 81, 249, 121, 217, 89,
            15, 143, 47, 175, 7, 135, 39, 167, 13, 141, 45, 173, 5, 133, 37, 165,
            207, 79, 239, 111, 199, 71, 231, 103, 205, 77, 237, 109, 197, 69, 229, 101,
            63, 191, 31, 159, 55, 183, 23, 151, 61, 189, 29, 157, 53, 181, 21, 149,
            254, 127, 223, 95, 247, 119, 215, 87, 253, 125, 221, 93, 245, 117, 213, 85,
        };

        /// <summary>CreateAlphaBrush @1800d9000: a 1bpp DIB pattern, black where the dither keeps the
        /// colour, its phase stepped by the driver's counter.</summary>
        static GpEmfDc.GdiObject CreateAlphaBrush(uint level, bool big)
        {
            int size = big ? 16 : 8;
            byte[] table = big ? HT16 : HT8;
            int stride = ((size >> 3) + 3) & ~3;
            var p = new byte[40 + 8 + size * stride];
            Le.W32(p, 0, 0x28); Le.W32(p, 4, size); Le.W32(p, 8, size);
            Le.W16(p, 12, 1); Le.W16(p, 14, 1);
            Le.W32(p, 20, size * stride);
            Le.W32(p, 44, 0xffffff);
            int phase = (int)(GpMetaDriverState.AlphaPhase % (uint)(size * size));
            int rowPhase = phase / size;
            for (int r = 0; r < size; r++)
            {
                for (int k = 0; k < size >> 3; k++)
                {
                    int bits = 0;
                    for (int j = 0; j < 8; j++)
                    {
                        int x = k * 8 + j;
                        bool on = false;
                        if (x < size)
                        {
                            int col = (x + phase) % size, row = (rowPhase + r) % size;
                            on = level <= table[col + row * size];
                        }
                        bits = (bits << 1) | (on ? 1 : 0);
                    }
                    p[48 + r * stride + k] = (byte)bits;
                }
            }
            return GpEmfDc.CreateDIBPatternBrushPt(p, 0);
        }

        /// <summary>ConvertAlphaToGdi::SetAlpha: the mask brush for an alpha (quantized to fours
        /// unless exact); a forced call makes it afresh and steps the phase.</summary>
        static GpEmfDc.GdiObject SetAlpha(GpEmfDc dc, uint level, bool force, bool exact)
        {
            if (!exact)
            {
                if (level < 2) level = 0;
                else if (level < 0xfe) level = ((level - 2) & ~3u) + 4;
                else level = 0xff;
            }
            if (!force && level == GpMetaDriverState.AlphaLevel) return GpMetaDriverState.AlphaBrush;
            dc.DeleteObject(GpMetaDriverState.AlphaBrush);
            GpEmfDc.GdiObject b;
            if (level == 0) b = GpEmfDc.Stock(0);
            else if (level == 0xff) b = GpEmfDc.Stock(4);
            else
            {
                if (force) GpMetaDriverState.AlphaPhase++;
                b = CreateAlphaBrush(level, exact);
            }
            GpMetaDriverState.AlphaBrush = b;
            GpMetaDriverState.AlphaLevel = level;
            return b;
        }

        // ---- ConvertRectFToGdi ----------------------------------------------------------------------

        sealed class RectFToGdi
        {
            public int X, Y, W, H;            // +4: the device rectangle
            public int Mult = 1;              // +0xa8
            public readonly List<int[]> Rects = new List<int[]>();   // l, t, r, b at the multiplier

            public RectFToGdi(RectangleF[] rects, GpMatrix m, Rectangle? draw)
            {
                GpMatrix mm = m;
                bool inc = false;
                if (draw.HasValue)
                {
                    Mult = IncreasedResolutionMultiplier(draw.Value);
                    if (Mult != 1) { inc = true; mm.Scale(Mult, Mult, true); }
                }
                int l = 0, t = 0, r = 0, b = 0;
                for (int i = 0; i < rects.Length; i++)
                {
                    RectangleF q = rects[i];
                    if (!(0f < q.Width) || !(0f < q.Height)) continue;
                    TransformBounds(mm, q.X, q.Y, q.X + q.Width, q.Y + q.Height, out float bx, out float by, out float bw, out float bh);
                    int il = GpMatrix.RasterizerCeiling(bx), it = GpMatrix.RasterizerCeiling(by);
                    int ir = GpMatrix.RasterizerCeiling(bw + bx), ib = GpMatrix.RasterizerCeiling(bh + by);
                    Rects.Add(new[] { il, it, ir, ib });
                    if (i != 0)
                    {
                        if (l <= il) il = l;
                        if (t <= it) it = t;
                        if (ir <= r) ir = r;
                        if (ib <= b) ib = b;
                    }
                    l = il; t = it; r = ir; b = ib;
                }
                if (!inc) { X = l; Y = t; W = r - l; H = b - t; }
                else
                {
                    X = l / Mult; Y = t / Mult;
                    W = (Mult - l + r - 1) / Mult;
                    H = (Mult - t + b - 1) / Mult;
                }
            }

            /// <summary>ConvertRectFToGdi::Fill: polygons with a null pen, or PatBlts with the ROP.</summary>
            public bool Fill(GpEmfDc dc, GpEmfDc.GdiObject brush, int rop, bool polygon)
            {
                int mode = 2;
                if (Mult > 1)
                {
                    mode = dc.GetGraphicsMode();
                    if (mode != 2) dc.SetGraphicsMode(2);
                    dc.ModifyWorldTransform(Xform(1.0f / (float)Mult), 2);
                }
                bool ok = true;
                GpEmfDc.GdiObject h = dc.SelectObject(brush);
                GpEmfDc.GdiObject p = dc.SelectObject(GpEmfDc.Stock(8));
                foreach (int[] q in Rects)
                {
                    if (!ok) break;
                    if (!polygon) ok = dc.PatBlt(q[0], q[1], q[2] - q[0], q[3] - q[1], rop);
                    else ok = dc.Polygon(new[] { q[0], q[1], q[0], q[3], q[2], q[3], q[2], q[1], q[0], q[1] }, 5);
                }
                dc.SelectObject(p);
                dc.SelectObject(h);
                if (Mult > 1)
                {
                    dc.ModifyWorldTransform(Xform((float)Mult), 2);
                    if (mode != 2) dc.SetGraphicsMode(mode);
                }
                return ok;
            }

            /// <summary>ConvertRectFToGdi::AlphaFill: XOR the colour, AND the mask, XOR again.</summary>
            public bool AlphaFill(GpEmfDc dc, GpEmfDc.GdiObject brush, GpEmfDc.GdiObject mask)
            {
                GpEmfDc.GdiObject h = dc.SelectObject(brush);
                bool ok = dc.PatBlt(X, Y, W, H, 0x5a0049);
                uint old = dc.SetTextColor(dc.GetBkColor());
                ok = ok && Fill(dc, mask, 0xa000c9, false);
                dc.SetTextColor(old);
                ok = ok && dc.PatBlt(X, Y, W, H, 0x5a0049);
                dc.SelectObject(h);
                return ok;
            }
        }

        /// <summary>TransformBounds: a rectangle's box under the matrix, as (x, y, w, h).</summary>
        static void TransformBounds(GpMatrix m, float l, float t, float r, float b, out float x, out float y, out float w, out float h)
        {
            var gm = new GpMat(m.M11, m.M12, m.M21, m.M22, m.Dx, m.Dy) { Complexity = m.Complexity };
            gm.TransformBounds(ref l, ref t, ref r, ref b);
            x = l; y = t; w = r - l; h = b - t;
        }

        /// <summary>DriverMeta::FillRects: several rectangles one call; each on its own unless solid.</summary>
        void DriverFillRects(Rectangle draw, RectangleF[] rects, Brush brush, GpMatrix? matrix = null)
        {
            int n = rects.Length;
            int first = 0;
            if (n > 1 && BrushType(brush) != 0)
            {
                for (; first < n - 1; first++) DriverFillRects(draw, new[] { rects[first] }, brush, matrix);
            }
            RectangleF[] rs = first == 0 ? rects : new[] { rects[n - 1] };
            var cr = new RectFToGdi(rs, matrix ?? DeviceMatrix, draw);
            if (cr.W <= 0 || cr.H <= 0) return;
            GpEmfDc dc = Dc;
            GpEmfDc.GdiObject hb = GetBrush(dc, brush, out uint alpha, out bool del);
            if (hb == null && BrushFillUsingBitmap(new Rectangle(cr.X, cr.Y, cr.W, cr.H), brush, null)) return;
            if (alpha <= 1) return;
            if (hb == null) hb = SetColor(dc, ToColorRef(brush));
            dc = ContextHdc();
            bool saved = SetupClipping(dc, new Rectangle(cr.X, cr.Y, cr.W, cr.H));
            if (alpha < 0xfe)
            {
                GpEmfDc.GdiObject mask = SetAlpha(dc, alpha, true, false);
                cr.AlphaFill(dc, hb, mask);
            }
            else
            {
                cr.Fill(dc, hb, 0xf00021, true);
                if (del) dc.DeleteObject(hb);
            }
            RestoreClipping(dc, saved);
        }
    }
}
