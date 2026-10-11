// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Drawing.Drawing2D;

namespace System.Drawing.WebGpuBackend.Gdip
{
    /// <summary>A growable little-endian byte buffer: what GDI+'s object GetData methods write their
    /// serialisation into (an IStream there).</summary>
    internal sealed class GpEmfPlusBuffer
    {
        byte[] _b = new byte[64];
        int _n;

        public int Length => _n;

        public byte[] ToArray()
        {
            var r = new byte[_n];
            Buffer.BlockCopy(_b, 0, r, 0, _n);
            return r;
        }

        void Grow(int more)
        {
            if (_n + more <= _b.Length) return;
            int cap = Math.Max(_b.Length * 2, _n + more);
            Array.Resize(ref _b, cap);
        }

        public void I32(int v) { Grow(4); Le.W32(_b, _n, v); _n += 4; }
        public void U32(uint v) => I32(unchecked((int)v));
        public void I16(int v) { Grow(2); Le.W16(_b, _n, v); _n += 2; }
        public void F(float v) => I32(BitConverter.SingleToInt32Bits(v));
        public void Byte(int v) { Grow(1); _b[_n++] = (byte)v; }

        public void Bytes(byte[] b) => Bytes(b, 0, b?.Length ?? 0);

        public void Bytes(byte[] b, int o, int n)
        {
            if (n <= 0) return;
            Grow(n);
            Buffer.BlockCopy(b, o, _b, _n, n);
            _n += n;
        }

        public void Zeros(int n)
        {
            if (n <= 0) return;
            Grow(n);
            Array.Clear(_b, _n, n);
            _n += n;
        }

        /// <summary>Pads to a multiple of four with zeros.</summary>
        public void Pad4() => Zeros((4 - (_n & 3)) & 3);

        public void Rect(RectangleF r) { F(r.X); F(r.Y); F(r.Width); F(r.Height); }

        /// <summary>WriteMatrix: the six elements.</summary>
        public void Matrix(Matrix m)
        {
            float[] e = m.Elements;
            for (int i = 0; i < 6; i++) F(e[i]);
        }

        public void Matrix(float[] e)
        {
            for (int i = 0; i < 6; i++) F(e[i]);
        }

        public void Argb(Color c) => I32(c.ToArgb());
    }
}
