// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.ComponentModel;
using MS.Internal;
using MS.Win32.PresentationCore;

namespace System.Windows.Media.Imaging
{
    #region TransformedBitmap
    /// <summary>
    /// TransformedBitmap provides caching functionality for a BitmapSource.
    /// </summary>
    public sealed partial class TransformedBitmap : Imaging.BitmapSource, ISupportInitialize
    {
        /// <summary>
        /// TransformedBitmap construtor
        /// </summary>
        public TransformedBitmap()
            : base(true) // Use base class virtuals
        {
        }

        /// <summary>
        /// Construct a TransformedBitmap with the given newTransform
        /// </summary>
        /// <param name="source">BitmapSource to apply to the newTransform to</param>
        /// <param name="newTransform">Transform to apply to the bitmap</param>
        public TransformedBitmap(BitmapSource source, Transform newTransform)
            : base(true) // Use base class virtuals
        {
            ArgumentNullException.ThrowIfNull(source);

            if (newTransform == null)
            {
                throw new InvalidOperationException(SR.Format(SR.Image_NoArgument, "Transform"));
            }

            if (!CheckTransform(newTransform))
            {
                throw new InvalidOperationException(SR.Image_OnlyOrthogonal);
            }

            _bitmapInit.BeginInit();

            Source = source;
            Transform = newTransform;

            _bitmapInit.EndInit();
            FinalizeCreation();
        }

        // ISupportInitialize

        /// <summary>
        /// Prepare the bitmap to accept initialize paramters.
        /// </summary>
        public void BeginInit()
        {
            WritePreamble();
            _bitmapInit.BeginInit();
        }

        /// <summary>
        /// Prepare the bitmap to accept initialize paramters.
        /// </summary>
        public void EndInit()
        {
            WritePreamble();
            _bitmapInit.EndInit();

            IsValidForFinalizeCreation(throwIfInvalid: true);
            FinalizeCreation();
        }

        private void ClonePrequel(TransformedBitmap otherTransformedBitmap)
        {
            BeginInit();
        }

        private void ClonePostscript(TransformedBitmap otherTransformedBitmap)
        {
            EndInit();
        }

        /// <summary>
        /// Check the transformation to see if it's a simple scale and/or rotation and/or flip.
        /// </summary>
        internal bool CheckTransform(Transform newTransform)
        {
            Matrix m = newTransform.Value;
            bool canHandle = false;

            if ( (DoubleUtil.IsZero(m.M11) && DoubleUtil.IsZero(m.M22)) ||
                 (DoubleUtil.IsZero(m.M12) && DoubleUtil.IsZero(m.M21)) )
            {
                canHandle = true;
            }

            return canHandle;
        }

        /// <summary>
        /// Check the transformation to see if it's a simple scale and/or rotation and/or flip.
        /// </summary>
        internal void GetParamsFromTransform(
            Transform newTransform,
            out double scaleX,
            out double scaleY,
            out WICBitmapTransformOptions options)
        {
            Matrix m = newTransform.Value;

            if (DoubleUtil.IsZero(m.M12) && DoubleUtil.IsZero(m.M21))
            {
                scaleX = Math.Abs(m.M11);
                scaleY = Math.Abs(m.M22);

                options = WICBitmapTransformOptions.WICBitmapTransformRotate0;

                if (m.M11 < 0)
                {
                    options |= WICBitmapTransformOptions.WICBitmapTransformFlipHorizontal;
                }

                if (m.M22 < 0)
                {
                    options |= WICBitmapTransformOptions.WICBitmapTransformFlipVertical;
                }
            }
            else
            {
                Debug.Assert(DoubleUtil.IsZero(m.M11) && DoubleUtil.IsZero(m.M22));

                scaleX = Math.Abs(m.M12);
                scaleY = Math.Abs(m.M21);

                options = WICBitmapTransformOptions.WICBitmapTransformRotate90;

                if (m.M12 < 0)
                {
                    options |= WICBitmapTransformOptions.WICBitmapTransformFlipHorizontal;
                }

                if (m.M21 >= 0)
                {
                    options |= WICBitmapTransformOptions.WICBitmapTransformFlipVertical;
                }
            }
        }

        ///
        /// Create the unmanaged resources
        ///
        // Managed WIC-free scale + flip/rotate of a Bgra32 source (off-Windows). Returns false if the
        // source has no managed pixel backing (falls back to the native path, which will throw).
        /// <summary>
        /// The source's pixels as a tightly packed buffer, with the bytes per pixel to read them
        /// by. Whole-byte formats are returned as they are; a sub-byte format (1/2/4bpp indexed or
        /// grey) is expanded to Bgra32 through its palette, because the scale and rotate below
        /// address whole pixels and cannot index into a packed byte.
        /// </summary>
        private static bool ReadSourcePixels(BitmapSource source, int sw, int sh,
            out byte[] pixels, out int stride, out int bytesPerPixel)
        {
            pixels = null; stride = 0; bytesPerPixel = 0;

            int bits = source.Format.BitsPerPixel;
            if (bits <= 0) return false;

            var rect = new Int32Rect(0, 0, sw, sh);
            int packedStride = (sw * bits + 7) / 8;
            byte[] packed = new byte[checked(packedStride * sh)];
            try
            {
                source.CopyPixels(rect, packed, packedStride, 0);
            }
            catch (Exception)
            {
                return false;       // unreadable source: let the caller fall back
            }

            if (bits % 8 == 0)
            {
                pixels = packed;
                stride = packedStride;
                bytesPerPixel = bits / 8;
                return true;
            }

            // Sub-byte: expand through the palette (or a grey ramp when there is none).
            System.Collections.Generic.IList<Color> colors = null;
            BitmapPalette palette = source.Palette;
            if (palette != null) colors = palette.Colors;

            int mask = (1 << bits) - 1;
            int perByte = 8 / bits;
            stride = sw * 4;
            pixels = new byte[checked(stride * sh)];
            for (int y = 0; y < sh; y++)
            {
                int rowStart = y * packedStride;
                for (int x = 0; x < sw; x++)
                {
                    int bitPos = x * bits;
                    int byteIndex = rowStart + (bitPos >> 3);
                    if (byteIndex >= packed.Length) continue;
                    int shift = 8 - bits - (bitPos & 7);
                    int index = (packed[byteIndex] >> shift) & mask;

                    byte b, g, r, a;
                    if (colors != null && index < colors.Count)
                    {
                        Color c = colors[index];
                        b = c.B; g = c.G; r = c.R; a = c.A;
                    }
                    else
                    {
                        byte level = (byte)(mask == 0 ? 0 : index * 255 / mask);
                        b = g = r = level; a = 255;
                    }

                    int d = y * stride + x * 4;
                    pixels[d] = b; pixels[d + 1] = g; pixels[d + 2] = r; pixels[d + 3] = a;
                }
            }
            bytesPerPixel = 4;
            return true;
        }

        private static bool ManagedTransform(BitmapSource source, double scaleX, double scaleY,
            WICBitmapTransformOptions options, out byte[] result, out int outW, out int outH, out int bpp)
        {
            result = null; outW = 0; outH = 0; bpp = 0;
            if (source == null) return false;
            int sw = source.PixelWidth, sh = source.PixelHeight;
            if (sw <= 0 || sh <= 0) return false;

            // Read through CopyPixels rather than reaching into _managedPixels: it is the accessor
            // that knows how the source stores its pixels, including the bit-shifting a sub-byte
            // format needs. Going straight to the field meant a source without a managed backing
            // fell through to the WIC path below, and a managed-only source has no native handle
            // there -- "SafeHandle cannot be null (pHandle)", thrown out of FinalizeCreation on
            // every layout pass.
            byte[] src;
            int sstride;
            if (!ReadSourcePixels(source, sw, sh, out src, out sstride, out bpp)) return false;

            // 1) nearest-neighbour scale
            int cw = Math.Max(1, (int)(scaleX * sw + 0.5));
            int ch = Math.Max(1, (int)(scaleY * sh + 0.5));
            int cstride = cw * bpp;
            byte[] scaled = new byte[cstride * ch];
            for (int y = 0; y < ch; y++)
            {
                int sy = (int)((long)y * sh / ch);
                for (int x = 0; x < cw; x++)
                {
                    int sx = (int)((long)x * sw / cw);
                    long si = (long)sy * sstride + (long)sx * bpp;
                    // A short or differently strided buffer must not throw here: leave those pixels
                    // zero rather than take the whole render pass down.
                    if (si < 0 || si + bpp > src.LongLength) continue;
                    Array.Copy(src, (int)si, scaled, y * cstride + x * bpp, bpp);
                }
            }

            // 2) rotation (Rotate90=1, 180=2, 270=3), then optional flips
            int rot = (int)options & 0x3;
            bool flipH = ((int)options & (int)WICBitmapTransformOptions.WICBitmapTransformFlipHorizontal) != 0;
            bool flipV = ((int)options & (int)WICBitmapTransformOptions.WICBitmapTransformFlipVertical) != 0;
            int rw = (rot == 1 || rot == 3) ? ch : cw;
            int rh = (rot == 1 || rot == 3) ? cw : ch;
            int rstride = rw * bpp;
            byte[] rotated = new byte[rstride * rh];
            for (int dy = 0; dy < rh; dy++)
            {
                for (int dx = 0; dx < rw; dx++)
                {
                    int sx, sy;
                    switch (rot)
                    {
                        case 1: sx = dy;          sy = ch - 1 - dx; break;  // 90 CW
                        case 2: sx = cw - 1 - dx; sy = ch - 1 - dy; break;  // 180
                        case 3: sx = cw - 1 - dy; sy = dx;          break;  // 270 CW
                        default: sx = dx;         sy = dy;          break;  // 0
                    }
                    Array.Copy(scaled, sy * cstride + sx * bpp, rotated, dy * rstride + dx * bpp, bpp);
                }
            }

            if (flipH || flipV)
            {
                byte[] flipped = new byte[rstride * rh];
                for (int y = 0; y < rh; y++)
                    for (int x = 0; x < rw; x++)
                    {
                        int sx = flipH ? rw - 1 - x : x;
                        int sy = flipV ? rh - 1 - y : y;
                        Array.Copy(rotated, sy * rstride + sx * bpp, flipped, y * rstride + x * bpp, bpp);
                    }
                rotated = flipped;
            }

            result = rotated; outW = rw; outH = rh;
            return true;
        }

        internal override void FinalizeCreation()
        {
            _bitmapInit.EnsureInitializedComplete();
            BitmapSourceSafeMILHandle wicTransformer = null;

            double scaleX, scaleY;
            WICBitmapTransformOptions options;

            GetParamsFromTransform(Transform, out scaleX, out scaleY, out options);

            // Off-Windows there is no native WIC to scale/rotate/flip. Do it on the source's managed
            // (Bgra32) pixel backing instead, and publish the result as this bitmap's managed backing.
            if (ManagedTransform(_source, scaleX, scaleY, options, out byte[] mpx, out int mw, out int mh, out int mbpp))
            {
                _managedPixels = mpx;
                _managedStride = mw * mbpp;
                // The transform moves pixels around; it does not convert them, so the result keeps
                // the source's format -- publishing Bgra32 regardless made every non-32bpp source
                // render as garbage. The one exception is a sub-byte source, which ReadSourcePixels
                // has already expanded through its palette to reach whole pixels.
                bool expanded = _source.Format.BitsPerPixel != mbpp * 8;
                _format = expanded ? PixelFormats.Bgra32 : _source.Format;
                _palette = expanded ? null : _source.Palette;
                _pixelWidth = mw;
                _pixelHeight = mh;
                _isSourceCached = _source.IsSourceCached;
                CreationCompleted = true;
                UpdateCachedSettings();
                return;
            }

            using (FactoryMaker factoryMaker = new FactoryMaker())
            {
                try
                {
                    IntPtr wicFactory = factoryMaker.ImagingFactoryPtr;

                    wicTransformer = _source.WicSourceHandle;

                    if (!DoubleUtil.IsOne(scaleX) || !DoubleUtil.IsOne(scaleY))
                    {
                        uint width = Math.Max(1, (uint)(scaleX * _source.PixelWidth + 0.5));
                        uint height = Math.Max(1, (uint)(scaleY * _source.PixelHeight + 0.5));

                        HRESULT.Check(UnsafeNativeMethods.WICImagingFactory.CreateBitmapScaler(
                                wicFactory,
                                out wicTransformer));

                        lock (_syncObject)
                        {
                            HRESULT.Check(UnsafeNativeMethods.WICBitmapScaler.Initialize(
                                    wicTransformer,
                                    _source.WicSourceHandle,
                                    width,
                                    height,
                                    WICInterpolationMode.Fant));
                        }
                    }

                    if (options != WICBitmapTransformOptions.WICBitmapTransformRotate0)
                    {
                        // Rotations are extremely slow if we're pulling from a decoder because we end
                        // up decoding multiple times.  Caching the source lets us rotate faster at the cost
                        // of increased memory usage.
                        wicTransformer = CreateCachedBitmap(
                            null,
                            wicTransformer,
                            BitmapCreateOptions.PreservePixelFormat,
                            BitmapCacheOption.Default,
                            _source.Palette);
                        // BitmapSource.CreateCachedBitmap already calculates memory pressure for
                        // the new bitmap, so there's no need to do it before setting it to
                        // WicSourceHandle.

                        BitmapSourceSafeMILHandle rotator = null;

                        HRESULT.Check(UnsafeNativeMethods.WICImagingFactory.CreateBitmapFlipRotator(
                                wicFactory,
                                out rotator));

                        lock (_syncObject)
                        {
                            HRESULT.Check(UnsafeNativeMethods.WICBitmapFlipRotator.Initialize(
                                    rotator,
                                    wicTransformer,
                                    options));
                        }

                        wicTransformer = rotator;
                    }

                    // If we haven't introduced either a scaler or rotator, add a null rotator
                    // so that our WicSourceHandle isn't the same as our Source's.
                    if (options == WICBitmapTransformOptions.WICBitmapTransformRotate0 &&
                        DoubleUtil.IsOne(scaleX) && DoubleUtil.IsOne(scaleY))
                    {
                        HRESULT.Check(UnsafeNativeMethods.WICImagingFactory.CreateBitmapFlipRotator(
                                wicFactory,
                                out wicTransformer));

                        lock (_syncObject)
                        {
                            HRESULT.Check(UnsafeNativeMethods.WICBitmapFlipRotator.Initialize(
                                    wicTransformer,
                                    _source.WicSourceHandle,
                                    WICBitmapTransformOptions.WICBitmapTransformRotate0));
                        }
                    }

                    WicSourceHandle = wicTransformer;
                    _isSourceCached = _source.IsSourceCached;
                }
                catch
                {
                    _bitmapInit.Reset();
                    throw;
                }
            }

            CreationCompleted = true;
            UpdateCachedSettings();
        }

        /// <summary>
        ///     Notification on source changing.
        /// </summary>
        private void SourcePropertyChangedHook(DependencyPropertyChangedEventArgs e)
        {
            if (!e.IsASubPropertyChange)
            {
                BitmapSource newSource = e.NewValue as BitmapSource;
                _source = newSource;
                RegisterDownloadEventSource(_source);
                _syncObject = (newSource != null) ? newSource.SyncObject : _bitmapInit;
            }
        }

        internal override bool IsValidForFinalizeCreation(bool throwIfInvalid)
        {
            if (Source == null)
            {
                if (throwIfInvalid)
                {
                    throw new InvalidOperationException(SR.Format(SR.Image_NoArgument, "Source"));
                }
                return false;
            }

            Transform transform = Transform;
            if (transform == null)
            {
                if (throwIfInvalid)
                {
                    throw new InvalidOperationException(SR.Format(SR.Image_NoArgument, "Transform"));
                }
                return false;
            }

            if (!CheckTransform(transform))
            {
                if (throwIfInvalid)
                {
                    throw new InvalidOperationException(SR.Image_OnlyOrthogonal);
                }
                return false;
            }

            return true;
        }

        /// <summary>
        ///     Notification on transform changing.
        /// </summary>
        private void TransformPropertyChangedHook(DependencyPropertyChangedEventArgs e)
        {
            if (!e.IsASubPropertyChange)
            {
                _transform = e.NewValue as Transform;
            }
        }

        /// <summary>
        ///     Coerce Source
        /// </summary>
        private static object CoerceSource(DependencyObject d, object value)
        {
            TransformedBitmap bitmap = (TransformedBitmap)d;
            if (!bitmap._bitmapInit.IsInInit)
            {
                return bitmap._source;
            }
            else
            {
                return value;
            }
        }

        /// <summary>
        ///     Coerce Transform
        /// </summary>
        private static object CoerceTransform(DependencyObject d, object value)
        {
            TransformedBitmap bitmap = (TransformedBitmap)d;
            if (!bitmap._bitmapInit.IsInInit)
            {
                return bitmap._transform;
            }
            else
            {
                return value;
            }
        }

        #region Data Members

        private BitmapSource _source;

        private Transform _transform;

        #endregion
    }

    #endregion // TransformedBitmap
}
