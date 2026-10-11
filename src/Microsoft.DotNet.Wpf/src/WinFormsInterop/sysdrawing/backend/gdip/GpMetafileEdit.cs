// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Generic;

namespace System.Drawing.WebGpuBackend.Gdip
{
    internal static class GpMetafileEdit
    {
        /// <summary>The EMF's records: (offset, type, size), as EnumEnhMetaFile visits them.</summary>
        public static IEnumerable<(int Offset, int Type, int Size)> Records(byte[] emf)
        {
            int o = 0;
            while (o + 8 <= emf.Length)
            {
                int type = Le.I32(emf, o);
                int size = Le.I32(emf, o + 4);
                if (size < 8 || (size & 3) != 0 || o + size > emf.Length) yield break;
                yield return (o, type, size);
                if (type == GpMetafileFormat.EmrEof) yield break;
                o += size;
            }
        }

        /// <summary>EnumEmfRemoveDualRecords: an EmfPlusDual file as EMF+ only -- the header, every EMF+
        /// comment, EMR_EOF, and the GDI records that follow an EmfPlusGetDC (they are drawing done
        /// through the HDC, not the down-level copy); the header's size and record count fixed up.</summary>
        public static byte[] RemoveDualRecords(byte[] emf)
        {
            var keep = new List<(int, int)>();
            bool keepGdi = true;
            int total = 0;
            foreach (var (o, type, size) in Records(emf))
            {
                if (GpMetafileFormat.IsEmfPlusRecord(emf, o, emf.Length))
                {
                    int end = o + size;
                    keepGdi = size >= 28 && Le.I16(emf, end - 12) == 0x4004 && Le.I32(emf, end - 8) == 12 && Le.I32(emf, end - 4) == 0;
                }
                else if (type != GpMetafileFormat.EmrEof && !keepGdi)
                    continue;
                keep.Add((o, size));
                total += size;
            }
            var r = new byte[total];
            int p = 0;
            foreach (var (o, size) in keep)
            {
                Buffer.BlockCopy(emf, o, r, p, size);
                p += size;
            }
            if (r.Length >= 88)
            {
                Le.W32(r, 0x30, total);
                Le.W32(r, 0x34, keep.Count);
            }
            return r;
        }
    }
}
