// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// A BitmapDecoder whose frames come from ManagedImageDecoder rather than from native WIC.
//
// BitmapImage and BitmapFrame.Create already decode through ManagedImageDecoder off Windows, but the
// BitmapDecoder family did not: BitmapDecoder.Create and the typed constructors (PngBitmapDecoder,
// JpegBitmapDecoder, ...) went straight to wpfgfx_cor3.dll and threw DllNotFoundException. That is a
// gap an app hits the moment it wants frame or metadata access rather than just an image to show.
//
// Only the decoding is replaced. The base class already stores frames in _frames and hands them out
// through the virtual Frames property, so populating that is enough; the members that would reach for
// a native handle are overridden to answer "nothing" instead of faulting.
//

#nullable enable

using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;

namespace System.Windows.Media.Imaging
{
    internal sealed class ManagedBitmapDecoder : BitmapDecoder
    {
        private readonly ReadOnlyCollection<BitmapFrame> _managedFrames;

        private ManagedBitmapDecoder(BitmapFrame frame)
            : base(isBuiltIn: true)
        {
            var frames = new List<BitmapFrame>(1) { frame };
            _frames = frames;
            _managedFrames = new ReadOnlyCollection<BitmapFrame>(frames);
        }

        /// <summary>
        /// Decode with the managed codecs, or return null if they cannot handle this image -- the
        /// caller then falls through to the native path, which is still the right answer on Windows.
        /// </summary>
        internal static ManagedBitmapDecoder? TryCreate(Uri? uri, Stream? stream)
        {
            try
            {
                BitmapSource? decoded = ManagedImageDecoder.Decode(uri, stream);
                if (decoded is null) return null;
                return new ManagedBitmapDecoder(BitmapFrame.Create(decoded));
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                return null;
            }
        }

        public override ReadOnlyCollection<BitmapFrame> Frames => _managedFrames;

        // The managed decoders produce a single decoded image and no sidecar data. These would
        // otherwise run the base implementations, which dereference a native decoder handle.
        public override BitmapSource? Preview => null;
        public override BitmapSource? Thumbnail => null;
        public override BitmapMetadata? Metadata => null;
        public override BitmapCodecInfo? CodecInfo => null;
        public override BitmapPalette? Palette => null;
        public override ReadOnlyCollection<ColorContext>? ColorContexts => null;

        // Nothing is fetched asynchronously: the stream was fully decoded before this existed.
        public override bool IsDownloading => false;

        public override InPlaceBitmapMetadataWriter CreateInPlaceBitmapMetadataWriter() =>
            throw new NotSupportedException();

        /// <summary>
        /// Nothing to seal: the frames were frozen by the managed decoder before this object existed.
        /// The built-in decoders throw here, which would be wrong for one that is genuinely usable.
        /// </summary>
        internal override void SealObject()
        {
        }
    }
}
