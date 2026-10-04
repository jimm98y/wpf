// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Runtime.InteropServices;
using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.ComponentModel;
using System.Drawing.WebGpuBackend.Gdip;

namespace System.Drawing.Imaging
{
    // sdkinc\GDIplusImageAttributes.h
    //
    // There are 5 possible sets of color adjustments: Default, Bitmap, Brush, Pen, Text. Bitmaps,
    // Brushes, Pens and Text use the Default adjustments until one of their own is set; SetToIdentity
    // forces a type to have none.
    //
    // THE ADJUSTMENTS ARE KEPT HERE as gdiplus.dll keeps them: GpImageAttributes -> GpRecolor (ported
    // in backend/gdip/GpRecolor.cs), with the Gdip*ImageAttributes* flat entry points' rules below.
    // The managed GDI+ engine and the GPU path both apply them through that port.

    /// <summary>
    /// Contains information about how image colors are manipulated during rendering.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public sealed class ImageAttributes : ICloneable, IDisposable
    {
        private GpRecolor _recolor = new GpRecolor();
        private WrapMode _wrapMode = WrapMode.Clamp;   // the GpImageAttributes default (wrap 4, colour 0)
        private Color _wrapColor;
        private bool _wrapClamp;

        private static void Invalid() => throw new ArgumentException("Parameter is not valid.");

        private static void CheckType(ColorAdjustType type)
        {
            if ((uint)type > 4u) Invalid();
        }

        public ImageAttributes()
        {
        }

        public void Dispose()
        {
            GC.SuppressFinalize(this);
        }

        ~ImageAttributes()
        {
        }

        public object Clone()
        {
            var copy = new ImageAttributes();
            copy._recolor = _recolor.Clone();
            copy._wrapMode = _wrapMode;
            copy._wrapColor = _wrapColor;
            copy._wrapClamp = _wrapClamp;
            return copy;
        }

        public void SetColorMatrix(ColorMatrix newColorMatrix) => SetColorMatrix(newColorMatrix, ColorMatrixFlag.Default, ColorAdjustType.Default);

        public void SetColorMatrix(ColorMatrix newColorMatrix, ColorMatrixFlag flags) => SetColorMatrix(newColorMatrix, flags, ColorAdjustType.Default);

        public void SetColorMatrix(ColorMatrix newColorMatrix, ColorMatrixFlag mode, ColorAdjustType type) => SetColorMatrices(newColorMatrix, null, mode, type);

        public void ClearColorMatrix() => ClearColorMatrix(ColorAdjustType.Default);

        /// <summary>GdipSetImageAttributesColorMatrix(enable = false): flags 0x82 off.</summary>
        public void ClearColorMatrix(ColorAdjustType type)
        {
            CheckType(type);
            GpRecolorObject o = _recolor.Existing(type);
            if (o != null) o.Flags &= ~0x82u;
        }

        public void SetColorMatrices(ColorMatrix newColorMatrix, ColorMatrix grayMatrix) => SetColorMatrices(newColorMatrix, grayMatrix, ColorMatrixFlag.Default, ColorAdjustType.Default);

        public void SetColorMatrices(ColorMatrix newColorMatrix, ColorMatrix grayMatrix, ColorMatrixFlag flags) => SetColorMatrices(newColorMatrix, grayMatrix, flags, ColorAdjustType.Default);

        /// <summary>GpRecolor::SetColorMatrices @180084110 / GpRecolorObject::SetColorMatrices @180084178.</summary>
        public void SetColorMatrices(ColorMatrix newColorMatrix, ColorMatrix grayMatrix, ColorMatrixFlag mode, ColorAdjustType type)
        {
            CheckType(type);
            if (newColorMatrix == null && grayMatrix == null) Invalid();
            int flags = (int)mode;
            if (grayMatrix == null || flags != 2)
            {
                if (newColorMatrix == null || (uint)flags > 1u) Invalid();
            }
            else if (newColorMatrix == null) Invalid();
            GpRecolorObject o = _recolor.Set(type);
            Copy(newColorMatrix, o.M);
            if (grayMatrix != null && flags == 2)
            {
                Copy(grayMatrix, o.Gray);
                o.Flags |= 0x82;
            }
            else
            {
                o.Flags = (o.Flags & ~0x80u) | 2;
            }
            o.MatrixFlags = flags;
        }

        private static void Copy(ColorMatrix m, float[] dst)
        {
            for (int r = 0; r < 5; r++)
                for (int k = 0; k < 5; k++)
                    dst[r * 5 + k] = m[r, k];
        }

        public void SetThreshold(float threshold) => SetThreshold(threshold, ColorAdjustType.Default);

        public void SetThreshold(float threshold, ColorAdjustType type)
        {
            CheckType(type);
            GpRecolorObject o = _recolor.Set(type);
            o.Threshold = threshold;
            o.Flags |= 4;
        }

        public void ClearThreshold() => ClearThreshold(ColorAdjustType.Default);

        public void ClearThreshold(ColorAdjustType type)
        {
            CheckType(type);
            GpRecolorObject o = _recolor.Existing(type);
            if (o != null) o.Flags &= ~4u;
        }

        public void SetGamma(float gamma) => SetGamma(gamma, ColorAdjustType.Default);

        /// <summary>GpRecolor::SetGamma @1800845e8: the object is made even when the gamma is refused.</summary>
        public void SetGamma(float gamma, ColorAdjustType type)
        {
            CheckType(type);
            GpRecolorObject o = _recolor.Set(type);
            if (!(0f < gamma)) Invalid();
            o.Gamma = gamma;
            o.Flags |= 8;
        }

        public void ClearGamma() => ClearGamma(ColorAdjustType.Default);

        public void ClearGamma(ColorAdjustType type)
        {
            CheckType(type);
            GpRecolorObject o = _recolor.Existing(type);
            if (o != null) o.Flags &= ~8u;
        }

        public void SetNoOp() => SetNoOp(ColorAdjustType.Default);

        public void SetNoOp(ColorAdjustType type)
        {
            CheckType(type);
            _recolor.Set(type).Flags |= 1;
        }

        public void ClearNoOp() => ClearNoOp(ColorAdjustType.Default);

        public void ClearNoOp(ColorAdjustType type)
        {
            CheckType(type);
            GpRecolorObject o = _recolor.Existing(type);
            if (o != null) o.Flags &= ~1u;
        }

        public void SetColorKey(Color colorLow, Color colorHigh) => SetColorKey(colorLow, colorHigh, ColorAdjustType.Default);

        /// <summary>GpRecolor::SetColorKey @18008f000: low must not exceed high in R, G or B.</summary>
        public void SetColorKey(Color colorLow, Color colorHigh, ColorAdjustType type)
        {
            CheckType(type);
            uint lo = (uint)colorLow.ToArgb(), hi = (uint)colorHigh.ToArgb();
            if (!((lo >> 16 & 0xff) <= (hi >> 16 & 0xff) && (lo >> 8 & 0xff) <= (hi >> 8 & 0xff) && (lo & 0xff) <= (hi & 0xff))) Invalid();
            GpRecolorObject o = _recolor.Set(type);
            o.KeyLow = lo;
            o.KeyHigh = hi;
            o.Flags |= 0x10;
        }

        public void ClearColorKey() => ClearColorKey(ColorAdjustType.Default);

        public void ClearColorKey(ColorAdjustType type)
        {
            CheckType(type);
            GpRecolorObject o = _recolor.Existing(type);
            if (o != null) o.Flags &= ~0x10u;
        }

        public void SetOutputChannel(ColorChannelFlag flags) => SetOutputChannel(flags, ColorAdjustType.Default);

        /// <summary>GdipSetImageAttributesOutputChannel @1800686b0: C, M, Y or K (0..3).</summary>
        public void SetOutputChannel(ColorChannelFlag flags, ColorAdjustType type)
        {
            CheckType(type);
            if ((uint)flags > 3u) Invalid();
            GpRecolorObject o = _recolor.Set(type);
            o.Channel = (int)flags;
            o.Flags |= 0x40;
        }

        public void ClearOutputChannel() => ClearOutputChannel(ColorAdjustType.Default);

        public void ClearOutputChannel(ColorAdjustType type)
        {
            CheckType(type);
            GpRecolorObject o = _recolor.Existing(type);
            if (o != null) o.Flags &= ~0x40u;
        }

        public void SetOutputChannelColorProfile(String colorProfileFilename) => SetOutputChannelColorProfile(colorProfileFilename, ColorAdjustType.Default);

        public void SetOutputChannelColorProfile(String colorProfileFilename, ColorAdjustType type)
        {
            // Called in order to emulate exception behavior from netfx related to invalid file paths.
            Path.GetFullPath(colorProfileFilename);
            CheckType(type);
            _recolor.Set(type);
        }

        public void ClearOutputChannelColorProfile() => ClearOutputChannel(ColorAdjustType.Default);

        public void ClearOutputChannelColorProfile(ColorAdjustType type) => ClearOutputChannel(type);

        public void SetRemapTable(ColorMap[] map) => SetRemapTable(map, ColorAdjustType.Default);

        /// <summary>GpRecolorObject::SetRemapTable @18008f270: exact ARGB pairs, the first match wins.</summary>
        public void SetRemapTable(ColorMap[] map, ColorAdjustType type)
        {
            CheckType(type);
            if (map == null) throw new ArgumentNullException(nameof(map));
            if (map.Length == 0) Invalid();
            GpRecolorObject o = _recolor.Set(type);
            o.Remap = new uint[map.Length * 2];
            for (int i = 0; i < map.Length; i++)
            {
                o.Remap[i * 2] = (uint)map[i].OldColor.ToArgb();
                o.Remap[i * 2 + 1] = (uint)map[i].NewColor.ToArgb();
            }
            o.Flags |= 0x20;
        }

        public void ClearRemapTable() => ClearRemapTable(ColorAdjustType.Default);

        public void ClearRemapTable(ColorAdjustType type)
        {
            CheckType(type);
            GpRecolorObject o = _recolor.Existing(type);
            if (o != null) o.Flags &= ~0x20u;
        }

        public void SetBrushRemapTable(ColorMap[] map) => SetRemapTable(map, ColorAdjustType.Brush);

        public void ClearBrushRemapTable() => ClearRemapTable(ColorAdjustType.Brush);

        public void SetWrapMode(WrapMode mode) => SetWrapMode(mode, new Color(), false);

        public void SetWrapMode(WrapMode mode, Color color) => SetWrapMode(mode, color, false);

        public void SetWrapMode(WrapMode mode, Color color, bool clamp)
        {
            _wrapMode = mode;
            _wrapColor = color;
            _wrapClamp = clamp;
        }

        /// <summary>GdipGetImageAttributesAdjustedPalette @18005e310 adjusts the native copy of the
        /// palette it is handed, and System.Drawing never reads that copy back: the caller's palette
        /// comes back as it went in. Only the argument checks show.</summary>
        public void GetAdjustedPalette(ColorPalette palette, ColorAdjustType type)
        {
            if (palette == null || palette.Entries.Length == 0 || (uint)(type - 1) >= 4u)
                Invalid();
        }

        /// <summary>The state the managed GDI+ engine draws with (GpImageAttributes: +0x24 wrap mode,
        /// +0x28 clamp colour as ARGB, +0x2c clamp flag, +0x18 the GpRecolor).</summary>
        internal GpImageAttr ToEngine()
        {
            var a = new GpImageAttr();
            a.Dp.Wrap = (int)_wrapMode;
            a.Dp.Clamp = (uint)_wrapColor.ToArgb();
            a.Dp.SrcRectClamp = _wrapClamp ? 1 : 0;
            a.Recolor = _recolor;
            return a;
        }

        /// <summary>The wrap mode an image is drawn with, for the GPU path.</summary>
        internal WrapMode WrapModeForDrawing => _wrapMode;

        /// <summary>The adjustments for <paramref name="type"/> on straight-alpha RGBA bytes (the GPU
        /// path), through the GpRecolor port.</summary>
        internal void Apply(byte[] rgba, ColorAdjustType type)
        {
            if (!_recolor.HasRecoloring(type)) return;
            _recolor.Flush();
            int n = rgba.Length / 4;
            var px = new uint[n];
            for (int i = 0; i < n; i++)
                px[i] = (uint)(rgba[i * 4 + 3] << 24 | rgba[i * 4] << 16 | rgba[i * 4 + 1] << 8 | rgba[i * 4 + 2]);
            _recolor.ColorAdjust(px, 0, n, type);
            for (int i = 0; i < n; i++)
            {
                uint c = px[i];
                rgba[i * 4] = (byte)(c >> 16); rgba[i * 4 + 1] = (byte)(c >> 8); rgba[i * 4 + 2] = (byte)c; rgba[i * 4 + 3] = (byte)(c >> 24);
            }
        }
    }
}
