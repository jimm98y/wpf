// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
//

using MS.Internal;
using MS.Win32.PresentationCore;

namespace System.Windows.Media.Imaging
{
    #region BmpBitmapEncoder

    /// <summary>
    /// Built-in Encoder for Bmp files.
    /// </summary>
    public sealed class BmpBitmapEncoder : BitmapEncoder
    {
        #region Constructors

        /// <summary>
        /// Constructor for BmpBitmapEncoder
        /// </summary>
        public BmpBitmapEncoder() :
            base(true)
        {
            _supportsPreview = false;
            _supportsGlobalThumbnail = false;
            _supportsGlobalMetadata = false;
            _supportsFrameThumbnails = false;
            _supportsMultipleFrames = false;
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
        /// Managed encode for platforms without native WIC: 32-bit BGRA with a BITMAPV5HEADER, so alpha survives. See ManagedBmpBEncoder.
        /// </summary>
        internal override bool TryManagedEncode(System.IO.Stream stream)
        {
            BitmapFrame frame = Frames[0];
            BitmapSource source = (frame as BitmapFrameEncode)?._source ?? (BitmapSource)frame;
            ManagedBmpEncoder.Save(source, stream);
            return true;
        }

        #region Data Members

        private Guid _containerFormat = MILGuidData.GUID_ContainerFormatBmp;

        #endregion
    }

    #endregion // BmpBitmapEncoder
}


