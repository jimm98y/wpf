// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// WPF's half of the managed GIF encoder: a BitmapSource's straight BGRA handed to the shared encoder
// (Shared/MS/Internal/Imaging/ManagedGifEncoder.cs).
//

using System.IO;

namespace System.Windows.Media.Imaging
{
    internal static partial class ManagedGifEncoder
    {
        internal static void Save(BitmapSource source, Stream stream)
        {
            byte[] bgra = source.CopyPixelsForManagedComposition(out int width, out int height, out int stride);
            Write(stream, bgra, width, height, stride);
        }
    }
}
