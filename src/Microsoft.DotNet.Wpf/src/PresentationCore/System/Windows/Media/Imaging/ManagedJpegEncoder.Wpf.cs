// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// WPF's half of the managed JPEG encoder: a BitmapSource's straight BGRA and resolution handed to
// the shared encoder (Shared/MS/Internal/Imaging/ManagedJpegEncoder.cs).
//

using System.IO;

namespace System.Windows.Media.Imaging
{
    internal static partial class ManagedJpegEncoder
    {
        internal static void Save(BitmapSource source, Stream stream, int quality)
        {
            byte[] bgra = source.CopyPixelsForManagedComposition(out int width, out int height, out int stride);
            Write(stream, bgra, width, height, stride, source.DpiX, source.DpiY, quality);
        }
    }
}
