// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Drawing.Imaging;

namespace System.Drawing.WebGpuBackend.Gdip
{
    internal static partial class GpMetafilePlayer
    {
        public static void Play(Graphics target, Metafile mf, PointF[] destParallelogram, RectangleF srcRect, GraphicsUnit srcUnit, ImageAttributes ia)
        {
        }

        public static void Enumerate(Graphics target, Metafile mf, PointF[] destParallelogram, RectangleF srcRect, GraphicsUnit srcUnit,
            Graphics.EnumerateMetafileProc callback, IntPtr callbackData, ImageAttributes ia)
        {
        }

        public static void PlayRecord(Metafile mf, EmfPlusRecordType recordType, int flags, int dataSize, byte[] data)
        {
        }
    }
}
