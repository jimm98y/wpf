// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// GDI's enhanced-metafile DC, as far as a recording needs one: GDI+ records into an EMF DC that
// CreateEnhMetaFileW made on the reference device, and what lands in the file around the EMF+
// comments -- EMR_HEADER, EMR_GDICOMMENT, the drawing GDI+'s driver renders down-level, EMR_EOF -- is
// GDI's writing, not GDI+'s. This writes the same records, and accumulates the bounds GDI keeps
// (inclusive device pixels) and derives the frame from them as GDI does when CreateEnhMetaFile was
// given none: frame = round(bounds * micrometres / pixels / 10), in 0.01 mm.
//
// The EMR_HEADER is the 108-byte ENHMETAHEADER with szlMicrometers that GDI writes, with the
// description (when there is one) after it; nHandles counts handle 0 and every GDI object slot ever
// used, as GDI's handle table does.
//

using System.Collections.Generic;
using System.Text;

namespace System.Drawing.WebGpuBackend.Gdip
{
    internal sealed class GpEmfWriter
    {
        readonly List<byte[]> _records = new List<byte[]>();
        readonly GpRefDevice _dev;
        readonly int[] _frame;          // 0.01 mm, or null: GDI derives it from the bounds
        readonly string _description;   // what CreateEnhMetaFileW was handed (double-NUL terminated there)
        bool _haveBounds;
        int _bl, _bt, _br, _bb;
        int _maxHandle;

        public GpEmfWriter(GpRefDevice dev, int[] frame, string description)
        {
            _dev = dev;
            _frame = frame;
            _description = description;
        }

        public int RecordCount => _records.Count + 2;

        /// <summary>A record with its type and size filled in from <paramref name="body"/>.</summary>
        public void Add(int type, byte[] body)
        {
            int n = 8 + (body?.Length ?? 0);
            n = (n + 3) & ~3;
            var r = new byte[n];
            Le.W32(r, 0, type);
            Le.W32(r, 4, n);
            if (body != null) Buffer.BlockCopy(body, 0, r, 8, body.Length);
            _records.Add(r);
        }

        public void AddRaw(byte[] record) => _records.Add(record);

        /// <summary>GdiComment: an EMR_GDICOMMENT carrying <paramref name="n"/> bytes.</summary>
        public void Comment(byte[] data, int o, int n)
        {
            int size = (12 + n + 3) & ~3;
            var r = new byte[size];
            Le.W32(r, 0, GpMetafileFormat.EmrGdiComment);
            Le.W32(r, 4, size);
            Le.W32(r, 8, n);
            Buffer.BlockCopy(data, o, r, 12, n);
            _records.Add(r);
        }

        public void UseHandle(int index) { if (index > _maxHandle) _maxHandle = index; }

        /// <summary>Accumulates an inclusive device rectangle into the bounds GDI keeps.</summary>
        public void AddBounds(int l, int t, int r, int b)
        {
            if (r < l || b < t) return;
            if (!_haveBounds) { _bl = l; _bt = t; _br = r; _bb = b; _haveBounds = true; return; }
            if (l < _bl) _bl = l;
            if (t < _bt) _bt = t;
            if (r > _br) _br = r;
            if (b > _bb) _bb = b;
        }

        public bool HaveBounds => _haveBounds;

        static int MulDivRound(long a, long b, long c)
        {
            if (c == 0) return 0;
            long n = a * b;
            long half = Math.Abs(c) / 2;
            long q = n >= 0 ? (n + half) / c : (n - half) / c;
            return (int)q;
        }

        /// <summary>The finished file: EMR_HEADER, the records, EMR_EOF.</summary>
        public byte[] Finish()
        {
            // The description: GDI copies the string up to its double NUL.
            byte[] desc = null;
            int nDesc = 0;
            if (!string.IsNullOrEmpty(_description))
            {
                string d = _description;
                int end = d.IndexOf("\0\0", StringComparison.Ordinal);
                d = end >= 0 ? d.Substring(0, end + 2) : d + "\0\0";
                nDesc = d.Length;
                desc = Encoding.Unicode.GetBytes(d);
            }
            int headerSize = 108 + ((desc?.Length ?? 0) + 3 & ~3);
            var eof = new byte[20];
            Le.W32(eof, 0, GpMetafileFormat.EmrEof);
            Le.W32(eof, 4, 20);
            Le.W32(eof, 8, 0);
            Le.W32(eof, 12, 16);
            Le.W32(eof, 16, 20);

            long total = headerSize + 20;
            foreach (var r in _records) total += r.Length;

            var h = new byte[headerSize];
            Le.W32(h, 0, GpMetafileFormat.EmrHeader);
            Le.W32(h, 4, headerSize);
            int bl = 0, bt = 0, br = -1, bb = -1;
            if (_haveBounds) { bl = _bl; bt = _bt; br = _br; bb = _bb; }
            Le.W32(h, 8, bl); Le.W32(h, 12, bt); Le.W32(h, 16, br); Le.W32(h, 20, bb);
            int umX = _dev.HorzSize * 1000, umY = _dev.VertSize * 1000;
            int[] f = _frame;
            if (f == null)
            {
                // GDI: the frame of what was drawn, in 0.01 mm.
                f = new int[4];
                f[0] = MulDivRound(bl, umX, (long)_dev.HorzRes * 10);
                f[1] = MulDivRound(bt, umY, (long)_dev.VertRes * 10);
                f[2] = MulDivRound(br, umX, (long)_dev.HorzRes * 10);
                f[3] = MulDivRound(bb, umY, (long)_dev.VertRes * 10);
            }
            Le.W32(h, 24, f[0]); Le.W32(h, 28, f[1]); Le.W32(h, 32, f[2]); Le.W32(h, 36, f[3]);
            Le.W32(h, 40, GpMetafileFormat.EmfSignature);
            Le.W32(h, 44, 0x10000);
            Le.W32(h, 48, (int)total);
            Le.W32(h, 52, _records.Count + 2);
            Le.W16(h, 56, _maxHandle + 1);
            Le.W16(h, 58, 0);
            Le.W32(h, 60, nDesc);
            Le.W32(h, 64, nDesc > 0 ? 108 : 0);
            Le.W32(h, 68, 0);
            Le.W32(h, 72, _dev.HorzRes); Le.W32(h, 76, _dev.VertRes);
            Le.W32(h, 80, _dev.HorzSize); Le.W32(h, 84, _dev.VertSize);
            Le.W32(h, 88, 0); Le.W32(h, 92, 0); Le.W32(h, 96, 0);
            Le.W32(h, 100, umX); Le.W32(h, 104, umY);
            if (desc != null) Buffer.BlockCopy(desc, 0, h, 108, desc.Length);

            var o = new byte[total];
            int p = 0;
            Buffer.BlockCopy(h, 0, o, p, h.Length); p += h.Length;
            foreach (var r in _records) { Buffer.BlockCopy(r, 0, o, p, r.Length); p += r.Length; }
            Buffer.BlockCopy(eof, 0, o, p, 20);
            return o;
        }
    }
}
