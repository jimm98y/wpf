// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Runtime.InteropServices;

namespace System.Drawing.Imaging
{
    // sdkinc\imaging.h
    public sealed class ImageCodecInfo
    {
        private Guid _clsid;
        private Guid _formatID;
        private string _codecName;
        private string _dllName;
        private string _formatDescription;
        private string _filenameExtension;
        private string _mimeType;
        private ImageCodecFlags _flags;
        private int _version;
        private byte[][] _signaturePatterns;
        private byte[][] _signatureMasks;

        internal ImageCodecInfo()
        {
        }

        public Guid Clsid
        {
            get { return _clsid; }
            set { _clsid = value; }
        }

        public Guid FormatID
        {
            get { return _formatID; }
            set { _formatID = value; }
        }

        public string CodecName
        {
            get { return _codecName; }
            set { _codecName = value; }
        }

        public string DllName
        {
            get
            {
                return _dllName;
            }
            set
            {
                _dllName = value;
            }
        }

        public string FormatDescription
        {
            get { return _formatDescription; }
            set { _formatDescription = value; }
        }

        public string FilenameExtension
        {
            get { return _filenameExtension; }
            set { _filenameExtension = value; }
        }

        public string MimeType
        {
            get { return _mimeType; }
            set { _mimeType = value; }
        }

        public ImageCodecFlags Flags
        {
            get { return _flags; }
            set { _flags = value; }
        }

        public int Version
        {
            get { return _version; }
            set { _version = value; }
        }

        [CLSCompliant(false)]
        public byte[][] SignaturePatterns
        {
            get { return _signaturePatterns; }
            set { _signaturePatterns = value; }
        }

        [CLSCompliant(false)]
        public byte[][] SignatureMasks
        {
            get { return _signatureMasks; }
            set { _signatureMasks = value; }
        }

        // Encoder/Decoder selection APIs

        // GDI+'s built-in codecs, listed as gdiplus.dll lists them (backend/GdipCodecs).
        public static ImageCodecInfo[] GetImageDecoders() => GdipCodecs.Decoders();

        public static ImageCodecInfo[] GetImageEncoders() => GdipCodecs.Encoders();
    }
}
