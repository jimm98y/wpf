// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
//

using MS.Internal;
using MS.Win32.PresentationCore;

namespace System.Windows.Media.Imaging
{
    #region GifBitmapEncoder

    /// <summary>
    /// Built-in Encoder for Gif files.
    /// </summary>
    public sealed class GifBitmapEncoder : BitmapEncoder
    {
        #region Constructors

        /// <summary>
        /// Constructor for GifBitmapEncoder
        /// </summary>
        public GifBitmapEncoder() :
            base(true)
        {
            _supportsPreview = false;
            _supportsGlobalThumbnail = false;
            _supportsGlobalMetadata = false;
            _supportsFrameThumbnails = false;
            _supportsMultipleFrames = true;
            _supportsFrameMetadata = false;
        }

        #endregion

        #region Internal Properties / Methods

        /// <summary>
        /// Returns the container format for this encoder
        /// </summary>
        internal override Guid ContainerFormat
        {
            get
            {
                return _containerFormat;
            }
        }

        /// <summary>
        /// Setups the encoder and other properties before encoding each frame
        /// </summary>
        internal override void SetupFrame(SafeMILHandle frameEncodeHandle, SafeMILHandle encoderOptions)
        {
            HRESULT.Check(UnsafeNativeMethods.WICBitmapFrameEncode.Initialize(
                frameEncodeHandle,
                encoderOptions
                ));
        }

        #endregion

        #region Internal Abstract

        /// Need to implement this to derive from the "sealed" object
        internal override void SealObject()
        {
            throw new NotImplementedException();
        }

        #endregion

        /// <summary>
        /// Managed encode for platforms without native WIC: single-frame GIF89a with a global colour
        /// table. See ManagedGifEncoder.
        /// </summary>
        internal override bool TryManagedEncode(System.IO.Stream stream)
        {
            BitmapFrame frame = Frames[0];
            BitmapSource source = (frame as BitmapFrameEncode)?._source ?? (BitmapSource)frame;
            ManagedGifEncoder.Save(source, stream);
            return true;
        }

        #region Data Members

        private Guid _containerFormat = MILGuidData.GUID_ContainerFormatGif;

        #endregion
    }

    #endregion // GifBitmapEncoder
}
