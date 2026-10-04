// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace System.Drawing.WebGpuBackend.Gdip
{
    internal static partial class GpWmfToEmf
    {
        /// <summary>The records of a WMF (METAHEADER first): offset, size in bytes, function.</summary>
        public static System.Collections.Generic.IEnumerable<(int Offset, int Size, int Function)> WmfRecords(byte[] wmf)
        {
            if (wmf.Length < 18) yield break;
            int o = Le.U16(wmf, 2) * 2;
            while (o + 6 <= wmf.Length)
            {
                long size = (long)Le.U32(wmf, o) * 2;
                int fn = Le.U16(wmf, o + 4);
                if (size < 6 || o + size > wmf.Length) yield break;
                yield return (o, (int)size, fn);
                if (fn == 0) yield break;
                o += (int)size;
            }
        }

        public static byte[] Convert(byte[] wmf, GpPlaceable? placeable, out GpMetafileHeader header)
        {
            header = null;
            return null;
        }
    }
}
