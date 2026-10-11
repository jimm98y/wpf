// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// WPF's half of the managed TIFF encoder: BitmapSources as the straight-BGRA pages the shared
// encoder (Shared/MS/Internal/Imaging/ManagedTiffEncoder.cs) writes.
//

using System.IO;

namespace System.Windows.Media.Imaging
{
    internal static partial class ManagedTiffEncoder
    {
        internal static void Save(BitmapSource source, Stream stream) => Save(new[] { source }, stream);

        internal static void Save(IReadOnlyList<BitmapSource> sources, Stream stream)
        {
            if (sources == null || sources.Count == 0)
            {
                throw new InvalidOperationException("The bitmap has no pixels to encode.");
            }

            var pages = new List<ManagedRaster>(sources.Count);
            foreach (BitmapSource source in sources)
            {
                byte[] pixels = source.CopyPixelsForManagedComposition(out int w, out int h, out int s);
                if (pixels == null || w <= 0 || h <= 0)
                {
                    throw new InvalidOperationException("The bitmap has no pixels to encode.");
                }
                pages.Add(new ManagedRaster(w, h, ManagedPixelLayout.Bgra32, pixels, s)
                {
                    DpiX = source.DpiX,
                    DpiY = source.DpiY,
                });
            }
            Write(pages, stream);
        }
    }
}
