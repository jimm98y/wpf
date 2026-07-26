// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.ComponentModel;
using MS.Internal;
using MS.Win32.PresentationCore;

namespace System.Windows.Media.Imaging
{
    #region FormatConvertedBitmap

    /// <summary>
    /// FormatConvertedBitmap provides caching functionality for a BitmapSource.
    /// </summary>
    public sealed partial class FormatConvertedBitmap : Imaging.BitmapSource, ISupportInitialize
    {
        /// <summary>
        /// Constructor
        /// </summary>
        public FormatConvertedBitmap() : base(true)
        {
        }

        /// <summary>
        /// Construct a FormatConvertedBitmap
        /// </summary>
        /// <param name="source">BitmapSource to apply to the format conversion to</param>
        /// <param name="destinationFormat">Destionation Format to  apply to the bitmap</param>
        /// <param name="destinationPalette">Palette if format is palettized</param>
        /// <param name="alphaThreshold">Alpha threshold</param>
        public FormatConvertedBitmap(BitmapSource source, PixelFormat destinationFormat, BitmapPalette destinationPalette, double alphaThreshold)
            : base(true) // Use base class virtuals
        {
            ArgumentNullException.ThrowIfNull(source);
            if (alphaThreshold < (double)(0.0) || alphaThreshold > (double)(100.0))
            {
                throw new ArgumentException(SR.Image_AlphaThresholdOutOfRange);
            }

            _bitmapInit.BeginInit();

            Source = source;
            DestinationFormat = destinationFormat;
            DestinationPalette = destinationPalette;
            AlphaThreshold = alphaThreshold;

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

        private void ClonePrequel(FormatConvertedBitmap otherFormatConvertedBitmap)
        {
            BeginInit();
        }

        private void ClonePostscript(FormatConvertedBitmap otherFormatConvertedBitmap)
        {
            EndInit();
        }

        ///
        /// Create the unmanaged resources
        ///
        internal override void FinalizeCreation()
        {
            _bitmapInit.EnsureInitializedComplete();
            BitmapSourceSafeMILHandle wicFormatter = null;

            // Off-Windows there is no native WIC format converter. Do the conversion on the source's
            // managed (Bgra32) pixel backing. Grayscale destination formats (Gray8/16/32Float, BlackWhite)
            // are computed via luminance; any other destination is passed through as Bgra32 (best effort,
            // enough to keep the image visible). Result is published as this bitmap's Bgra32 backing.
            if (!OperatingSystem.IsWindows() && Source?._managedPixels != null)
            {
                int sw = Source.PixelWidth, sh = Source.PixelHeight, sstride = Source._managedStride;
                byte[] src = Source._managedPixels;
                byte[] dst = new byte[sw * 4 * sh];
                Guid g = DestinationFormat.Guid;
                bool gray = g == PixelFormats.Gray8.Guid || g == PixelFormats.Gray16.Guid
                         || g == PixelFormats.Gray32Float.Guid || g == PixelFormats.BlackWhite.Guid;
                for (int y = 0; y < sh; y++)
                {
                    int si = y * sstride, di = y * sw * 4;
                    for (int x = 0; x < sw; x++, si += 4, di += 4)
                    {
                        byte b = src[si], gg = src[si + 1], r = src[si + 2], a = src[si + 3];
                        if (gray)
                        {
                            byte l = (byte)((r * 77 + gg * 150 + b * 29) >> 8);   // Rec.601 luma
                            dst[di] = dst[di + 1] = dst[di + 2] = l; dst[di + 3] = a;
                        }
                        else { dst[di] = b; dst[di + 1] = gg; dst[di + 2] = r; dst[di + 3] = a; }
                    }
                }
                _managedPixels = dst; _managedStride = sw * 4; _format = PixelFormats.Bgra32;
                _pixelWidth = sw; _pixelHeight = sh; _isSourceCached = Source.IsSourceCached;
                CreationCompleted = true;
                UpdateCachedSettings();
                return;
            }

            using (FactoryMaker factoryMaker = new FactoryMaker())
            {
                try
                {
                    IntPtr wicFactory = factoryMaker.ImagingFactoryPtr;

                    HRESULT.Check(UnsafeNativeMethods.WICImagingFactory.CreateFormatConverter(
                            wicFactory,
                            out wicFormatter));

                    SafeMILHandle internalPalette;
                    if (DestinationPalette != null)
                        internalPalette = DestinationPalette.InternalPalette;
                    else
                        internalPalette = new SafeMILHandle();

                    Guid format = DestinationFormat.Guid;

                    lock (_syncObject)
                    {
                        HRESULT.Check(UnsafeNativeMethods.WICFormatConverter.Initialize(
                                wicFormatter,
                                Source.WicSourceHandle,
                                ref format,
                                DitherType.DitherTypeErrorDiffusion,
                                internalPalette,
                                AlphaThreshold,
                                WICPaletteType.WICPaletteTypeOptimal
                                ));
                    }

                    //
                    // This is just a link in a BitmapSource chain. The memory is being used by
                    // the BitmapSource at the end of the chain, so no memory pressure needs
                    // to be added here.
                    //
                    WicSourceHandle = wicFormatter;

                    // Even if our source is cached, format conversion is expensive and so we'll
                    // always maintain our own cache for the purpose of rendering.
                    _isSourceCached = false;
                }
                catch
                {
                    _bitmapInit.Reset();
                    throw;
                }
                finally
                {
                    wicFormatter?.Close();
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
            if (DestinationFormat.Palettized)
            {
                if (DestinationPalette == null)
                {
                    if (throwIfInvalid)
                    {
                        throw new InvalidOperationException(SR.Image_IndexedPixelFormatRequiresPalette);
                    }
                    return false;
                }
                else if ((1 << DestinationFormat.BitsPerPixel) < DestinationPalette.Colors.Count)
                {
                    if (throwIfInvalid)
                    {
                        throw new InvalidOperationException(SR.Image_PaletteColorsDoNotMatchFormat);
                    }
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        ///     Notification on destination format changing.
        /// </summary>
        private void DestinationFormatPropertyChangedHook(DependencyPropertyChangedEventArgs e)
        {
            if (!e.IsASubPropertyChange)
            {
                _destinationFormat = (PixelFormat)e.NewValue;
            }
        }

        /// <summary>
        ///     Notification on destination palette changing.
        /// </summary>
        private void DestinationPalettePropertyChangedHook(DependencyPropertyChangedEventArgs e)
        {
            if (!e.IsASubPropertyChange)
            {
                _destinationPalette = e.NewValue as BitmapPalette;
            }
        }

        /// <summary>
        ///     Notification on alpha threshold changing.
        /// </summary>
        private void AlphaThresholdPropertyChangedHook(DependencyPropertyChangedEventArgs e)
        {
            if (!e.IsASubPropertyChange)
            {
                _alphaThreshold = (double)e.NewValue;
            }
        }

        /// <summary>
        ///     Coerce Source
        /// </summary>
        private static object CoerceSource(DependencyObject d, object value)
        {
            FormatConvertedBitmap bitmap = (FormatConvertedBitmap)d;
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
        ///     Coerce DestinationFormat
        /// </summary>
        private static object CoerceDestinationFormat(DependencyObject d, object value)
        {
            FormatConvertedBitmap bitmap = (FormatConvertedBitmap)d;
            if (!bitmap._bitmapInit.IsInInit)
            {
                return bitmap._destinationFormat;
            }
            else
            {
                //
                // If the client is trying to create a FormatConvertedBitmap with a
                // DestinationFormat == PixelFormats.Default, then coerce it to either
                // the Source bitmaps format (providing the Source bitmap is non-null)
                // or the original DP value.
                //
                if (((PixelFormat)value).Format == PixelFormatEnum.Default)
                {
                    if (bitmap.Source != null)
                    {
                        return bitmap.Source.Format;
                    }
                    else
                    {
                        return bitmap._destinationFormat;
                    }
                }
                else
                {
                    return value;
                }
            }
        }

        /// <summary>
        ///     Coerce DestinationPalette
        /// </summary>
        private static object CoerceDestinationPalette(DependencyObject d, object value)
        {
            FormatConvertedBitmap bitmap = (FormatConvertedBitmap)d;
            if (!bitmap._bitmapInit.IsInInit)
            {
                return bitmap._destinationPalette;
            }
            else
            {
                return value;
            }
        }

        /// <summary>
        ///     Coerce Transform
        /// </summary>
        private static object CoerceAlphaThreshold(DependencyObject d, object value)
        {
            FormatConvertedBitmap bitmap = (FormatConvertedBitmap)d;
            if (!bitmap._bitmapInit.IsInInit)
            {
                return bitmap._alphaThreshold;
            }
            else
            {
                return value;
            }
        }

        #region Data Members

        private BitmapSource _source;

        private PixelFormat _destinationFormat;

        private BitmapPalette _destinationPalette;

        private double _alphaThreshold;

        #endregion
    }

    #endregion // FormatConvertedBitmap
}
