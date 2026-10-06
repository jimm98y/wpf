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

            // Off-Windows there is no native WIC format converter, so the conversion runs on the
            // source's managed pixel backing.
            //
            // The SOURCE is expanded to straight BGRA32 by ManagedPixelConverter, which is what
            // knows how to read a format narrower than 32 bits -- sub-byte formats especially, where
            // a byte holds up to eight pixels. Doing that inline here is what this used to attempt,
            // by stepping the source four bytes per pixel whatever its format actually was: Bgr24
            // skewed along each row, Gray8 read four pixels' worth per pixel, and 1/2/4bpp read
            // clean off the end of the buffer.
            //
            // The DESTINATION is still only honoured to the extent the managed backing can express
            // it: greyscale destinations (Gray8/16/32Float, BlackWhite) are computed by luminance,
            // anything else keeps the expanded colour. Either way the backing is published as
            // Bgra32, which is what this bitmap then reports as its format.
            if (Source?._managedPixels != null)
            {
                int sw = Source.PixelWidth, sh = Source.PixelHeight;
                byte[] dst = ManagedPixelConverter.ToBgra32(
                    Source._managedPixels, Source._managedStride, sw, sh, Source.Format, Source.Palette);

                if (dst == null)
                {
                    // A format the converter does not know. Leaving the source untouched keeps
                    // whatever it already was rather than publishing pixels read as the wrong shape.
                    dst = new byte[sw * 4 * sh];
                }

                Guid g = DestinationFormat.Guid;

                // A destination that is not 32bpp BGRA is PACKED into its real layout and reported
                // as itself -- including the sub-byte and indexed formats, which is what makes
                // asking for Indexed4 or BlackWhite mean anything. Previously every destination came
                // back 32bpp wearing the requested format's name, so quantising an image and then
                // reading Format (or saving it) was told something untrue.
                if (g != PixelFormats.Bgra32.Guid && g != PixelFormats.Pbgra32.Guid)
                {
                    byte[] packed = ManagedPixelConverter.FromBgra32(
                        dst, sw, sh, DestinationFormat, DestinationPalette, out int packedStride);
                    if (packed != null)
                    {
                        _managedPixels = packed;
                        _managedStride = packedStride;
                        _format = DestinationFormat;
                        _palette = DestinationPalette;
                        _pixelWidth = sw; _pixelHeight = sh; _isSourceCached = Source.IsSourceCached;
                        CreationCompleted = true;
                        UpdateCachedSettings();
                        return;
                    }
                }

                // Destinations the packer does not model (Gray32Float and the other float formats)
                // keep the old best effort: greyscale by luminance, published as Bgra32.
                bool gray = g == PixelFormats.Gray8.Guid || g == PixelFormats.Gray16.Guid
                         || g == PixelFormats.Gray32Float.Guid || g == PixelFormats.BlackWhite.Guid;
                if (gray)
                {
                    for (int i = 0; i < dst.Length; i += 4)
                    {
                        byte l = (byte)((dst[i + 2] * 77 + dst[i + 1] * 150 + dst[i] * 29) >> 8);  // Rec.601 luma
                        dst[i] = dst[i + 1] = dst[i + 2] = l;
                    }
                }

                // A PREMULTIPLIED destination has to come back premultiplied, and be labelled so.
                // The converter above always produces straight colour, so asking for Pbgra32 and
                // getting straight bytes back leaves the caller to premultiply-or-not by guesswork:
                // the XPS/PDF image path asks for Pbgra32 precisely so it can undo the
                // premultiplication itself, and undoing it on data that was never premultiplied
                // washes half-transparent pixels out (a 50% grey came back at 253 instead of 127).
                bool premultiplied = g == PixelFormats.Pbgra32.Guid;
                if (premultiplied)
                {
                    for (int i = 0; i < dst.Length; i += 4)
                    {
                        byte a = dst[i + 3];
                        if (a != 255)
                        {
                            dst[i] = (byte)(dst[i] * a / 255);
                            dst[i + 1] = (byte)(dst[i + 1] * a / 255);
                            dst[i + 2] = (byte)(dst[i + 2] * a / 255);
                        }
                    }
                }

                _managedPixels = dst;
                _managedStride = sw * 4;
                _format = premultiplied ? PixelFormats.Pbgra32 : PixelFormats.Bgra32;
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
