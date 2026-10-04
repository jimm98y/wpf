// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Runtime.InteropServices;
using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.ComponentModel;

namespace System.Drawing.Imaging
{
    // sdkinc\GDIplusImageAttributes.h

    // There are 5 possible sets of color adjustments:
    //          ColorAdjustDefault,
    //          ColorAdjustBitmap,
    //          ColorAdjustBrush,
    //          ColorAdjustPen,
    //          ColorAdjustText,

    // Bitmaps, Brushes, Pens, and Text will all use any color adjustments
    // that have been set into the default ImageAttributes until their own
    // color adjustments have been set.  So as soon as any "Set" method is
    // called for Bitmaps, Brushes, Pens, or Text, then they start from
    // scratch with only the color adjustments that have been set for them.
    // Calling Reset removes any individual color adjustments for a type
    // and makes it revert back to using all the default color adjustments
    // (if any).  The SetToIdentity method is a way to force a type to
    // have no color adjustments at all, regardless of what previous adjustments
    // have been set for the defaults or for that type.
    //
    // THE ADJUSTMENTS ARE KEPT HERE, in managed state, and applied by Apply(): the GPU-raster path has
    // no GDI+ to hand them to (none at all in the browser), and it draws every image through this.
    // The native object is still created and kept in step where libgdiplus is loaded.

    /// <summary>
    /// Contains information about how image colors are manipulated during rendering.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public sealed class ImageAttributes : ICloneable, IDisposable
    {
#if FINALIZATION_WATCH
        private string allocationSite = Graphics.GetAllocationStack();
#endif


        /// <summary>One adjust type's adjustments. A type with none set falls back to Default's.</summary>
        private sealed class Adjustments
        {
            internal bool NoOp;
            internal ColorMatrix Matrix, GrayMatrix;
            internal ColorMatrixFlag MatrixFlags;
            internal bool HasThreshold; internal float Threshold;
            internal bool HasGamma; internal float Gamma;
            internal bool HasColorKey; internal Color KeyLow, KeyHigh;
            internal ColorMap[] Remap;

            internal Adjustments Clone()
            {
                var a = (Adjustments)MemberwiseClone();
                if (Remap != null) a.Remap = (ColorMap[])Remap.Clone();
                return a;
            }
        }

        // Indexed by ColorAdjustType (Default..Text); null = never set for that type.
        private Adjustments[] _adjust = new Adjustments[(int)ColorAdjustType.Count];
        private WrapMode _wrapMode = WrapMode.Clamp;   // GpImageAttributes' default (wrap 4, colour 0)
        private Color _wrapColor;
        private bool _wrapClamp;

        private Adjustments For(ColorAdjustType type)
        {
            int i = (int)type;
            if ((uint)i >= (uint)_adjust.Length) throw new InvalidEnumArgumentException(nameof(type), i, typeof(ColorAdjustType));
            return _adjust[i] ??= new Adjustments();
        }


        private static void Check(int status)
        {
            if (status != SafeNativeMethods.Gdip.Ok)
                throw SafeNativeMethods.Gdip.StatusException(status);
        }


        /// <summary>
        /// Initializes a new instance of the <see cref='ImageAttributes'/> class.
        /// </summary>
        public ImageAttributes()
        {
        }


        /// <summary>
        /// Cleans up Windows resources for this <see cref='ImageAttributes'/>.
        /// </summary>
        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        private void Dispose(bool disposing)
        {
        }

        /// <summary>
        /// Cleans up Windows resources for this <see cref='ImageAttributes'/>.
        /// </summary>
        ~ImageAttributes()
        {
            Dispose(false);
        }

        /// <summary>
        /// Creates an exact copy of this <see cref='ImageAttributes'/>.
        /// </summary>
        public object Clone()
        {
            var copy = new ImageAttributes();
            for (int i = 0; i < _adjust.Length; i++)
                copy._adjust[i] = _adjust[i]?.Clone();
            copy._wrapMode = _wrapMode;
            copy._wrapColor = _wrapColor;
            copy._wrapClamp = _wrapClamp;
            return copy;
        }

        /// <summary>
        /// Sets the 5 X 5 color adjust matrix to the specified <see cref='Matrix'/>.
        /// </summary>
        public void SetColorMatrix(ColorMatrix newColorMatrix)
        {
            SetColorMatrix(newColorMatrix, ColorMatrixFlag.Default, ColorAdjustType.Default);
        }

        /// <summary>
        /// Sets the 5 X 5 color adjust matrix to the specified 'Matrix' with the specified 'ColorMatrixFlags'.
        /// </summary>
        public void SetColorMatrix(ColorMatrix newColorMatrix, ColorMatrixFlag flags)
        {
            SetColorMatrix(newColorMatrix, flags, ColorAdjustType.Default);
        }

        /// <summary>
        /// Sets the 5 X 5 color adjust matrix to the specified 'Matrix' with the  specified 'ColorMatrixFlags'.
        /// </summary>
        public void SetColorMatrix(ColorMatrix newColorMatrix, ColorMatrixFlag mode, ColorAdjustType type)
        {
            SetColorMatrices(newColorMatrix, null, mode, type);
        }

        /// <summary>
        /// Clears the color adjust matrix to all zeroes.
        /// </summary>
        public void ClearColorMatrix()
        {
            ClearColorMatrix(ColorAdjustType.Default);
        }

        /// <summary>
        /// Clears the color adjust matrix.
        /// </summary>
        public void ClearColorMatrix(ColorAdjustType type)
        {
            Adjustments a = For(type);
            a.Matrix = a.GrayMatrix = null;
            a.MatrixFlags = ColorMatrixFlag.Default;
        }

        /// <summary>
        /// Sets a color adjust matrix for image colors and a separate gray scale adjust matrix for gray scale values.
        /// </summary>
        public void SetColorMatrices(ColorMatrix newColorMatrix, ColorMatrix grayMatrix)
        {
            SetColorMatrices(newColorMatrix, grayMatrix, ColorMatrixFlag.Default, ColorAdjustType.Default);
        }

        public void SetColorMatrices(ColorMatrix newColorMatrix, ColorMatrix grayMatrix, ColorMatrixFlag flags)
        {
            SetColorMatrices(newColorMatrix, grayMatrix, flags, ColorAdjustType.Default);
        }

        public void SetColorMatrices(ColorMatrix newColorMatrix, ColorMatrix grayMatrix, ColorMatrixFlag mode,
                                     ColorAdjustType type)
        {
            if (newColorMatrix == null)
                throw new ArgumentNullException(nameof(newColorMatrix));
            Adjustments a = For(type);
            a.Matrix = Copy(newColorMatrix);
            a.GrayMatrix = grayMatrix == null ? null : Copy(grayMatrix);
            a.MatrixFlags = mode;
        }

        private static ColorMatrix Copy(ColorMatrix m)
        {
            var c = new ColorMatrix();
            for (int r = 0; r < 5; r++)
                for (int k = 0; k < 5; k++)
                    c[r, k] = m[r, k];
            return c;
        }

        public void SetThreshold(float threshold)
        {
            SetThreshold(threshold, ColorAdjustType.Default);
        }

        public void SetThreshold(float threshold, ColorAdjustType type)
        {
            Adjustments a = For(type);
            a.HasThreshold = true;
            a.Threshold = threshold;
        }

        public void ClearThreshold()
        {
            ClearThreshold(ColorAdjustType.Default);
        }

        public void ClearThreshold(ColorAdjustType type)
        {
            For(type).HasThreshold = false;
        }

        public void SetGamma(float gamma)
        {
            SetGamma(gamma, ColorAdjustType.Default);
        }

        public void SetGamma(float gamma, ColorAdjustType type)
        {
            Adjustments a = For(type);
            a.HasGamma = true;
            a.Gamma = gamma;
        }

        public void ClearGamma()
        {
            ClearGamma(ColorAdjustType.Default);
        }

        public void ClearGamma(ColorAdjustType type)
        {
            For(type).HasGamma = false;
        }

        public void SetNoOp()
        {
            SetNoOp(ColorAdjustType.Default);
        }

        public void SetNoOp(ColorAdjustType type)
        {
            For(type).NoOp = true;
        }

        public void ClearNoOp()
        {
            ClearNoOp(ColorAdjustType.Default);
        }

        public void ClearNoOp(ColorAdjustType type)
        {
            For(type).NoOp = false;
        }

        public void SetColorKey(Color colorLow, Color colorHigh)
        {
            SetColorKey(colorLow, colorHigh, ColorAdjustType.Default);
        }

        public void SetColorKey(Color colorLow, Color colorHigh, ColorAdjustType type)
        {
            Adjustments a = For(type);
            a.HasColorKey = true;
            a.KeyLow = colorLow;
            a.KeyHigh = colorHigh;
        }

        public void ClearColorKey()
        {
            ClearColorKey(ColorAdjustType.Default);
        }

        public void ClearColorKey(ColorAdjustType type)
        {
            For(type).HasColorKey = false;
        }

        // The output channel (CMYK separation) is a printing feature; the screen path ignores it.
        public void SetOutputChannel(ColorChannelFlag flags)
        {
            SetOutputChannel(flags, ColorAdjustType.Default);
        }

        public void SetOutputChannel(ColorChannelFlag flags, ColorAdjustType type)
        {
            For(type);
        }

        public void ClearOutputChannel()
        {
            ClearOutputChannel(ColorAdjustType.Default);
        }

        public void ClearOutputChannel(ColorAdjustType type)
        {
            For(type);
        }

        public void SetOutputChannelColorProfile(String colorProfileFilename)
        {
            SetOutputChannelColorProfile(colorProfileFilename, ColorAdjustType.Default);
        }

        public void SetOutputChannelColorProfile(String colorProfileFilename,
                                                 ColorAdjustType type)
        {
            // Called in order to emulate exception behavior from netfx related to invalid file paths.
            Path.GetFullPath(colorProfileFilename);
            For(type);
        }

        public void ClearOutputChannelColorProfile()
        {
            ClearOutputChannel(ColorAdjustType.Default);
        }

        public void ClearOutputChannelColorProfile(ColorAdjustType type)
        {
            ClearOutputChannel(type);
        }

        public void SetRemapTable(ColorMap[] map)
        {
            SetRemapTable(map, ColorAdjustType.Default);
        }

        public void SetRemapTable(ColorMap[] map, ColorAdjustType type)
        {
            For(type).Remap = (ColorMap[])map.Clone();
        }

        public void ClearRemapTable()
        {
            ClearRemapTable(ColorAdjustType.Default);
        }

        public void ClearRemapTable(ColorAdjustType type)
        {
            For(type).Remap = null;
        }

        public void SetBrushRemapTable(ColorMap[] map)
        {
            SetRemapTable(map, ColorAdjustType.Brush);
        }

        public void ClearBrushRemapTable()
        {
            ClearRemapTable(ColorAdjustType.Brush);
        }

        public void SetWrapMode(WrapMode mode)
        {
            SetWrapMode(mode, new Color(), false);
        }

        public void SetWrapMode(WrapMode mode, Color color)
        {
            SetWrapMode(mode, color, false);
        }

        public void SetWrapMode(WrapMode mode, Color color, bool clamp)
        {
            _wrapMode = mode;
            _wrapColor = color;
            _wrapClamp = clamp;
        }

        public void GetAdjustedPalette(ColorPalette palette, ColorAdjustType type)
        {
            Color[] entries = palette.Entries;
            for (int i = 0; i < entries.Length; i++)
            {
                Color c = entries[i];
                byte[] px = { c.R, c.G, c.B, c.A };
                Apply(px, type);
                entries[i] = Color.FromArgb(px[3], px[0], px[1], px[2]);
            }
        }

        /// <summary>The state the managed GDI+ engine draws with (GpImageAttributes: +0x24 wrap mode,
        /// +0x28 clamp colour as ARGB, +0x2c clamp flag, +0x18 the GpRecolor).</summary>
        internal System.Drawing.WebGpuBackend.Gdip.GpImageAttr ToEngine()
        {
            var a = new System.Drawing.WebGpuBackend.Gdip.GpImageAttr();
            a.Dp.Wrap = (int)_wrapMode;
            a.Dp.Clamp = (uint)_wrapColor.ToArgb();
            a.Dp.SrcRectClamp = _wrapClamp ? 1 : 0;
            return a;
        }

        /// <summary>The wrap mode an image is drawn with, for the GPU path.</summary>
        internal WrapMode WrapModeForDrawing => _wrapMode;

        /// <summary>Applies the adjustments for <paramref name="type"/> to straight-alpha RGBA pixels,
        /// in GDI+'s order: colour key, remap table, colour matrix, threshold, gamma. A type with no
        /// adjustments of its own uses Default's.</summary>
        internal void Apply(byte[] rgba, ColorAdjustType type)
        {
            Adjustments a = _adjust[(int)type] ?? _adjust[(int)ColorAdjustType.Default];
            if (a == null || a.NoOp)
                return;
            bool any = a.HasColorKey || a.Remap != null || a.Matrix != null || a.HasThreshold || (a.HasGamma && a.Gamma != 1f);
            if (!any)
                return;

            byte[] gammaTable = null;
            if (a.HasGamma && a.Gamma != 1f)
            {
                gammaTable = new byte[256];
                for (int i = 0; i < 256; i++)
                    gammaTable[i] = (byte)Math.Clamp((int)Math.Round(Math.Pow(i / 255.0, a.Gamma) * 255.0), 0, 255);
            }

            for (int p = 0; p + 3 < rgba.Length; p += 4)
            {
                int r = rgba[p], g = rgba[p + 1], b = rgba[p + 2], al = rgba[p + 3];

                if (a.HasColorKey
                    && r >= a.KeyLow.R && r <= a.KeyHigh.R
                    && g >= a.KeyLow.G && g <= a.KeyHigh.G
                    && b >= a.KeyLow.B && b <= a.KeyHigh.B)
                {
                    rgba[p] = rgba[p + 1] = rgba[p + 2] = rgba[p + 3] = 0;
                    continue;
                }

                if (a.Remap != null)
                {
                    foreach (ColorMap m in a.Remap)
                    {
                        Color o = m.OldColor;
                        if (o.R == r && o.G == g && o.B == b && o.A == al)
                        {
                            Color n = m.NewColor;
                            r = n.R; g = n.G; b = n.B; al = n.A;
                            break;
                        }
                    }
                }

                if (a.Matrix != null)
                {
                    bool gray = r == g && g == b;
                    ColorMatrix m = a.Matrix;
                    if (gray && a.MatrixFlags == ColorMatrixFlag.SkipGrays)
                        m = null;
                    else if (gray && a.MatrixFlags == ColorMatrixFlag.AltGrays && a.GrayMatrix != null)
                        m = a.GrayMatrix;
                    if (m != null)
                    {
                        float fr = r, fg = g, fb = b, fa = al;
                        float nr = fr * m[0, 0] + fg * m[1, 0] + fb * m[2, 0] + fa * m[3, 0] + 255f * m[4, 0];
                        float ng = fr * m[0, 1] + fg * m[1, 1] + fb * m[2, 1] + fa * m[3, 1] + 255f * m[4, 1];
                        float nb = fr * m[0, 2] + fg * m[1, 2] + fb * m[2, 2] + fa * m[3, 2] + 255f * m[4, 2];
                        float na = fr * m[0, 3] + fg * m[1, 3] + fb * m[2, 3] + fa * m[3, 3] + 255f * m[4, 3];
                        r = ClampByte(nr); g = ClampByte(ng); b = ClampByte(nb); al = ClampByte(na);
                    }
                }

                if (a.HasThreshold)
                {
                    float t = a.Threshold * 255f;
                    r = r > t ? 255 : 0;
                    g = g > t ? 255 : 0;
                    b = b > t ? 255 : 0;
                }

                if (gammaTable != null)
                {
                    r = gammaTable[r]; g = gammaTable[g]; b = gammaTable[b];
                }

                rgba[p] = (byte)r; rgba[p + 1] = (byte)g; rgba[p + 2] = (byte)b; rgba[p + 3] = (byte)al;
            }
        }

        private static int ClampByte(float v) => v <= 0f ? 0 : v >= 255f ? 255 : (int)(v + 0.5f);
    }
}
