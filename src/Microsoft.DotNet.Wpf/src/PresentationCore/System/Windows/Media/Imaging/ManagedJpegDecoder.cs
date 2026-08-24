// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// Managed JPEG decoder for platforms without native WIC. Supports baseline
// (SOF0) and progressive (SOF2) DCT, Huffman entropy coding, restart intervals,
// arbitrary chroma subsampling, and 1- (grayscale) and 3-component (YCbCr)
// frames. Output is straight BGRA32. Arithmetic coding and lossless JPEG are
// not supported. The progressive scan logic mirrors the well-known reference
// state machine used by pdf.js / libjpeg.
//

using System.IO;

namespace System.Windows.Media.Imaging
{
    internal sealed class ManagedJpegDecoder
    {
        // Natural (row-major) position for each zig-zag coefficient index.
        private static ReadOnlySpan<byte> ZigZag =>
        [
             0,  1,  8, 16,  9,  2,  3, 10,
            17, 24, 32, 25, 18, 11,  4,  5,
            12, 19, 26, 33, 40, 48, 41, 34,
            27, 20, 13,  6,  7, 14, 21, 28,
            35, 42, 49, 56, 57, 50, 43, 36,
            29, 22, 15, 23, 30, 37, 44, 51,
            58, 59, 52, 45, 38, 31, 39, 46,
            53, 60, 61, 54, 47, 55, 62, 63
        ];

        private sealed class HuffmanTable
        {
            // Canonical decode tables (JPEG spec Annex F.2.2.3).
            public readonly int[] MaxCode = new int[18];
            public readonly int[] ValPtr = new int[18];
            public readonly int[] MinCode = new int[18];
            public byte[] Values;
        }

        private sealed class Component
        {
            public int Id;
            public int H, V;            // sampling factors
            public int QuantId;
            public HuffmanTable Dc, Ac;
            public int Pred;            // running DC predictor
            public int BlocksPerLine, BlocksPerColumn;
            public int BlocksPerLineForMcu, BlocksPerColumnForMcu;
            public short[] Coeff;       // 64 coefficients per block, natural order
            public byte[] Samples;      // reconstructed 8-bit plane (block-padded)
        }

        private readonly byte[] _data;
        private int _offset;

        private readonly ushort[][] _quant = new ushort[4][];     // de-zigzagged (natural order)
        private readonly HuffmanTable[] _huffDc = new HuffmanTable[4];
        private readonly HuffmanTable[] _huffAc = new HuffmanTable[4];

        private Component[] _components;
        private int _frameWidth, _frameHeight;
        private int _hMax, _vMax;
        private int _mcusPerLine, _mcusPerColumn;
        private bool _progressive;
        private int _restartInterval;

        // Bit reader state.
        private int _bitsData, _bitsCount;

        // Progressive scan state.
        private int _spectralStart, _spectralEnd, _successive, _successivePrev;
        private int _eobrun;
        private int _successiveAcState, _successiveAcNextValue;

        private ManagedJpegDecoder(byte[] data)
        {
            _data = data;
        }

        internal static byte[] Decode(byte[] data, out int width, out int height)
        {
            var d = new ManagedJpegDecoder(data);
            return d.Run(out width, out height);
        }

        private byte[] Run(out int width, out int height)
        {
            Parse();
            width = _frameWidth;
            height = _frameHeight;
            return Reconstruct();
        }

        // ---- Marker parsing --------------------------------------------------------------

        private void Parse()
        {
            if (_data.Length < 2 || _data[0] != 0xFF || _data[1] != 0xD8)
            {
                throw new InvalidDataException("Not a JPEG (missing SOI).");
            }
            _offset = 2;

            while (_offset + 1 < _data.Length)
            {
                if (_data[_offset] != 0xFF)
                {
                    _offset++;
                    continue;
                }
                byte marker = _data[_offset + 1];
                _offset += 2;

                switch (marker)
                {
                    case 0xD9:   // EOI
                        return;
                    case 0xC0:   // SOF0 baseline
                    case 0xC1:   // SOF1 extended sequential
                        ReadFrame(false);
                        break;
                    case 0xC2:   // SOF2 progressive
                        ReadFrame(true);
                        break;
                    case 0xC4:   // DHT
                        ReadHuffmanTables();
                        break;
                    case 0xDB:   // DQT
                        ReadQuantTables();
                        break;
                    case 0xDD:   // DRI
                        _offset += 2; // segment length
                        _restartInterval = (_data[_offset] << 8) | _data[_offset + 1];
                        _offset += 2;
                        break;
                    case 0xDA:   // SOS
                        ReadScan();
                        break;
                    case >= 0xD0 and <= 0xD7:   // stray RSTn
                        break;
                    default:
                        // APPn, COM, and other length-prefixed segments: skip.
                        int len = (_data[_offset] << 8) | _data[_offset + 1];
                        _offset += len;
                        break;
                }
            }
        }

        private void ReadQuantTables()
        {
            int len = (_data[_offset] << 8) | _data[_offset + 1];
            int end = _offset + len;
            _offset += 2;
            while (_offset < end)
            {
                int pq = _data[_offset] >> 4;   // precision: 0 = 8-bit, 1 = 16-bit
                int tq = _data[_offset] & 15;
                _offset++;
                var table = new ushort[64];
                for (int k = 0; k < 64; k++)
                {
                    int v;
                    if (pq == 0)
                    {
                        v = _data[_offset++];
                    }
                    else
                    {
                        v = (_data[_offset] << 8) | _data[_offset + 1];
                        _offset += 2;
                    }
                    table[ZigZag[k]] = (ushort)v;   // store in natural order
                }
                _quant[tq] = table;
            }
        }

        private void ReadHuffmanTables()
        {
            int len = (_data[_offset] << 8) | _data[_offset + 1];
            int end = _offset + len;
            _offset += 2;
            while (_offset < end)
            {
                int tc = _data[_offset] >> 4;    // 0 = DC, 1 = AC
                int th = _data[_offset] & 15;
                _offset++;

                var counts = new int[17];
                int total = 0;
                for (int i = 1; i <= 16; i++)
                {
                    counts[i] = _data[_offset++];
                    total += counts[i];
                }
                var values = new byte[total];
                for (int i = 0; i < total; i++)
                {
                    values[i] = _data[_offset++];
                }

                var table = BuildHuffman(counts, values);
                if (tc == 0)
                {
                    _huffDc[th] = table;
                }
                else
                {
                    _huffAc[th] = table;
                }
            }
        }

        private static HuffmanTable BuildHuffman(int[] counts, byte[] values)
        {
            var t = new HuffmanTable { Values = values };
            int code = 0, k = 0;
            for (int l = 1; l <= 16; l++)
            {
                if (counts[l] == 0)
                {
                    t.MaxCode[l] = -1;
                }
                else
                {
                    t.ValPtr[l] = k;
                    t.MinCode[l] = code;
                    code += counts[l];
                    k += counts[l];
                    t.MaxCode[l] = code - 1;
                }
                code <<= 1;
            }
            t.MaxCode[17] = int.MaxValue;
            return t;
        }

        private void ReadFrame(bool progressive)
        {
            _progressive = progressive;
            _offset += 2; // segment length
            _offset += 1; // sample precision (assume 8)
            _frameHeight = (_data[_offset] << 8) | _data[_offset + 1];
            _frameWidth = (_data[_offset + 2] << 8) | _data[_offset + 3];
            int nc = _data[_offset + 4];
            _offset += 5;

            _components = new Component[nc];
            _hMax = 0;
            _vMax = 0;
            for (int i = 0; i < nc; i++)
            {
                var c = new Component
                {
                    Id = _data[_offset],
                    H = _data[_offset + 1] >> 4,
                    V = _data[_offset + 1] & 15,
                    QuantId = _data[_offset + 2],
                };
                _offset += 3;
                _hMax = Math.Max(_hMax, c.H);
                _vMax = Math.Max(_vMax, c.V);
                _components[i] = c;
            }

            _mcusPerLine = (_frameWidth + 8 * _hMax - 1) / (8 * _hMax);
            _mcusPerColumn = (_frameHeight + 8 * _vMax - 1) / (8 * _vMax);

            foreach (var c in _components)
            {
                c.BlocksPerLine = (int)Math.Ceiling(_frameWidth / 8.0 * c.H / _hMax);
                c.BlocksPerColumn = (int)Math.Ceiling(_frameHeight / 8.0 * c.V / _vMax);
                c.BlocksPerLineForMcu = _mcusPerLine * c.H;
                c.BlocksPerColumnForMcu = _mcusPerColumn * c.V;
                c.Coeff = new short[c.BlocksPerLineForMcu * c.BlocksPerColumnForMcu * 64];
            }
        }

        // ---- Scan / entropy decoding -----------------------------------------------------

        private void ReadScan()
        {
            int scanStart = _offset;
            int len = (_data[_offset] << 8) | _data[_offset + 1];
            int ns = _data[_offset + 2];
            int p = _offset + 3;

            var scanComps = new Component[ns];
            for (int i = 0; i < ns; i++)
            {
                int cs = _data[p];
                int td = _data[p + 1] >> 4;
                int ta = _data[p + 1] & 15;
                p += 2;
                Component c = Array.Find(_components, x => x.Id == cs);
                c.Dc = _huffDc[td];
                c.Ac = _huffAc[ta];
                scanComps[i] = c;
            }

            _spectralStart = _data[p];
            _spectralEnd = _data[p + 1];
            _successive = _data[p + 2] & 15;
            _successivePrev = _data[p + 2] >> 4;

            _offset = scanStart + len;   // entropy-coded segment begins here
            DecodeScanData(scanComps);
        }

        private void DecodeScanData(Component[] scanComps)
        {
            _bitsCount = 0;
            _eobrun = 0;
            _successiveAcState = 0;
            foreach (var c in scanComps)
            {
                c.Pred = 0;
            }

            bool interleaved = scanComps.Length > 1;
            int mcuExpected = interleaved
                ? _mcusPerLine * _mcusPerColumn
                : scanComps[0].BlocksPerLine * scanComps[0].BlocksPerColumn;

            int reset = _restartInterval > 0 ? _restartInterval : mcuExpected;

            int mcu = 0;
            while (mcu < mcuExpected)
            {
                foreach (var c in scanComps)
                {
                    c.Pred = 0;
                }
                _eobrun = 0;
                _successiveAcState = 0;

                int n = Math.Min(reset, mcuExpected - mcu);
                for (int i = 0; i < n; i++)
                {
                    if (interleaved)
                    {
                        DecodeMcu(scanComps, mcu);
                    }
                    else
                    {
                        DecodeBlockNonInterleaved(scanComps[0], mcu);
                    }
                    mcu++;
                }

                // Byte-align and, if there is more data, consume the restart marker.
                _bitsCount = 0;
                if (mcu < mcuExpected)
                {
                    SkipToRestartMarker();
                }
            }
        }

        private void SkipToRestartMarker()
        {
            while (_offset + 1 < _data.Length)
            {
                if (_data[_offset] == 0xFF)
                {
                    byte m = _data[_offset + 1];
                    if (m >= 0xD0 && m <= 0xD7)
                    {
                        _offset += 2;
                        return;
                    }
                    if (m != 0x00)
                    {
                        return; // some other marker: scan data ended early
                    }
                }
                _offset++;
            }
        }

        private void DecodeMcu(Component[] scanComps, int mcu)
        {
            int mcuRow = mcu / _mcusPerLine;
            int mcuCol = mcu % _mcusPerLine;
            foreach (var c in scanComps)
            {
                for (int v = 0; v < c.V; v++)
                {
                    for (int h = 0; h < c.H; h++)
                    {
                        int blockRow = mcuRow * c.V + v;
                        int blockCol = mcuCol * c.H + h;
                        int offset = 64 * (blockRow * c.BlocksPerLineForMcu + blockCol);
                        DecodeOneBlock(c, offset);
                    }
                }
            }
        }

        private void DecodeBlockNonInterleaved(Component c, int mcu)
        {
            int blockRow = mcu / c.BlocksPerLine;
            int blockCol = mcu % c.BlocksPerLine;
            int offset = 64 * (blockRow * c.BlocksPerLineForMcu + blockCol);
            DecodeOneBlock(c, offset);
        }

        private void DecodeOneBlock(Component c, int offset)
        {
            if (!_progressive)
            {
                DecodeBaseline(c, offset);
            }
            else if (_spectralStart == 0)
            {
                if (_successivePrev == 0)
                {
                    DecodeDcFirst(c, offset);
                }
                else
                {
                    DecodeDcSuccessive(c, offset);
                }
            }
            else if (_successivePrev == 0)
            {
                DecodeAcFirst(c, offset);
            }
            else
            {
                DecodeAcSuccessive(c, offset);
            }
        }

        private void DecodeBaseline(Component c, int offset)
        {
            short[] block = c.Coeff;
            int t = DecodeHuffman(c.Dc);
            int diff = t == 0 ? 0 : ReceiveExtend(t);
            c.Pred += diff;
            block[offset] = (short)c.Pred;

            int k = 1;
            while (k < 64)
            {
                int rs = DecodeHuffman(c.Ac);
                int s = rs & 15;
                int r = rs >> 4;
                if (s == 0)
                {
                    if (r < 15)
                    {
                        break;
                    }
                    k += 16;
                    continue;
                }
                k += r;
                if (k >= 64)
                {
                    break;
                }
                block[offset + ZigZag[k]] = (short)ReceiveExtend(s);
                k++;
            }
        }

        private void DecodeDcFirst(Component c, int offset)
        {
            int t = DecodeHuffman(c.Dc);
            int diff = t == 0 ? 0 : (ReceiveExtend(t) << _successive);
            c.Pred += diff;
            c.Coeff[offset] = (short)c.Pred;
        }

        private void DecodeDcSuccessive(Component c, int offset)
        {
            if (ReadBit() != 0)
            {
                c.Coeff[offset] |= (short)(1 << _successive);
            }
        }

        private void DecodeAcFirst(Component c, int offset)
        {
            if (_eobrun > 0)
            {
                _eobrun--;
                return;
            }
            short[] block = c.Coeff;
            int k = _spectralStart;
            int e = _spectralEnd;
            while (k <= e)
            {
                int rs = DecodeHuffman(c.Ac);
                int s = rs & 15;
                int r = rs >> 4;
                if (s == 0)
                {
                    if (r < 15)
                    {
                        _eobrun = (1 << r) + ReadBits(r) - 1;
                        break;
                    }
                    k += 16;
                    continue;
                }
                k += r;
                if (k > e)
                {
                    break;
                }
                block[offset + ZigZag[k]] = (short)(ReceiveExtend(s) * (1 << _successive));
                k++;
            }
        }

        private void DecodeAcSuccessive(Component c, int offset)
        {
            short[] block = c.Coeff;
            int k = _spectralStart;
            int e = _spectralEnd;
            int r = 0;
            while (k <= e)
            {
                int z = offset + ZigZag[k];
                int sign = block[z] < 0 ? -1 : 1;
                switch (_successiveAcState)
                {
                    case 0: // initial
                        int rs = DecodeHuffman(c.Ac);
                        int s = rs & 15;
                        r = rs >> 4;
                        if (s == 0)
                        {
                            if (r < 15)
                            {
                                _eobrun = ReadBits(r) + (1 << r);
                                _successiveAcState = 4;
                            }
                            else
                            {
                                r = 16;
                                _successiveAcState = 1;
                            }
                        }
                        else
                        {
                            _successiveAcNextValue = ReceiveExtend(s);
                            _successiveAcState = r != 0 ? 2 : 3;
                        }
                        continue;
                    case 1: // skipping r zero-history coefficients
                    case 2:
                        if (block[z] != 0)
                        {
                            block[z] += (short)(sign * (ReadBit() << _successive));
                        }
                        else
                        {
                            r--;
                            if (r == 0)
                            {
                                _successiveAcState = _successiveAcState == 2 ? 3 : 0;
                            }
                        }
                        break;
                    case 3: // place a newly non-zero coefficient
                        if (block[z] != 0)
                        {
                            block[z] += (short)(sign * (ReadBit() << _successive));
                        }
                        else
                        {
                            block[z] = (short)(_successiveAcNextValue << _successive);
                            _successiveAcState = 0;
                        }
                        break;
                    case 4: // within an EOB run: correction bits only
                        if (block[z] != 0)
                        {
                            block[z] += (short)(sign * (ReadBit() << _successive));
                        }
                        break;
                }
                k++;
            }
            if (_successiveAcState == 4)
            {
                _eobrun--;
                if (_eobrun == 0)
                {
                    _successiveAcState = 0;
                }
            }
        }

        // ---- Bit reader ------------------------------------------------------------------

        private int ReadBit()
        {
            if (_bitsCount > 0)
            {
                _bitsCount--;
                return (_bitsData >> _bitsCount) & 1;
            }
            if (_offset >= _data.Length)
            {
                return 0;
            }
            _bitsData = _data[_offset++];
            if (_bitsData == 0xFF)
            {
                byte next = _offset < _data.Length ? _data[_offset] : (byte)0;
                if (next == 0x00)
                {
                    _offset++;   // stuffed 0xFF00 -> literal 0xFF
                }
                else
                {
                    // A marker inside the entropy segment: no more bits here.
                    _offset--;   // leave the 0xFF for the marker scanner
                    _bitsData = 0;
                    return 0;
                }
            }
            _bitsCount = 7;
            return (_bitsData >> 7) & 1;
        }

        private int ReadBits(int count)
        {
            int v = 0;
            for (int i = 0; i < count; i++)
            {
                v = (v << 1) | ReadBit();
            }
            return v;
        }

        private int ReceiveExtend(int s)
        {
            int v = ReadBits(s);
            // Sign-extend: values in the lower half of the range are negative.
            if (v < (1 << (s - 1)))
            {
                v += (-1 << s) + 1;
            }
            return v;
        }

        private int DecodeHuffman(HuffmanTable t)
        {
            int code = ReadBit();
            int l = 1;
            while (code > t.MaxCode[l])
            {
                code = (code << 1) | ReadBit();
                l++;
                if (l > 16)
                {
                    return 0;   // corrupt stream; bail with a benign value
                }
            }
            return t.Values[t.ValPtr[l] + code - t.MinCode[l]];
        }

        // ---- Reconstruction --------------------------------------------------------------

        private byte[] Reconstruct()
        {
            // IDCT every component into a full-resolution (block-padded) sample plane.
            foreach (var c in _components)
            {
                BuildComponentSamples(c);
            }

            var bgra = new byte[_frameWidth * _frameHeight * 4];

            if (_components.Length == 1)
            {
                Component c = _components[0];
                int stride = c.BlocksPerLineForMcu * 8;
                byte[] plane = c.Samples;
                for (int y = 0; y < _frameHeight; y++)
                {
                    for (int x = 0; x < _frameWidth; x++)
                    {
                        byte g = plane[y * stride + x];
                        int o = (y * _frameWidth + x) * 4;
                        bgra[o] = g; bgra[o + 1] = g; bgra[o + 2] = g; bgra[o + 3] = 255;
                    }
                }
                return bgra;
            }

            Component cy = _components[0], cb = _components[1], cr = _components[2];
            int strideY = cy.BlocksPerLineForMcu * 8;
            int strideCb = cb.BlocksPerLineForMcu * 8;
            int strideCr = cr.BlocksPerLineForMcu * 8;

            // Per-axis scale from image space to each component's sample grid.
            double sxCb = (double)cb.H / _hMax, syCb = (double)cb.V / _vMax;
            double sxCr = (double)cr.H / _hMax, syCr = (double)cr.V / _vMax;
            double sxY = (double)cy.H / _hMax, syY = (double)cy.V / _vMax;

            for (int y = 0; y < _frameHeight; y++)
            {
                int yY = (int)(y * syY);
                int yCb = (int)(y * syCb);
                int yCr = (int)(y * syCr);
                for (int x = 0; x < _frameWidth; x++)
                {
                    int Y = cy.Samples[yY * strideY + (int)(x * sxY)];
                    int Cb = cb.Samples[yCb * strideCb + (int)(x * sxCb)] - 128;
                    int Cr = cr.Samples[yCr * strideCr + (int)(x * sxCr)] - 128;

                    int r = Y + ((91881 * Cr) >> 16);
                    int g = Y - ((22554 * Cb + 46802 * Cr) >> 16);
                    int b = Y + ((116130 * Cb) >> 16);

                    int o = (y * _frameWidth + x) * 4;
                    bgra[o] = Clamp(b);
                    bgra[o + 1] = Clamp(g);
                    bgra[o + 2] = Clamp(r);
                    bgra[o + 3] = 255;
                }
            }
            return bgra;
        }

        private void BuildComponentSamples(Component c)
        {
            int w = c.BlocksPerLineForMcu * 8;
            int h = c.BlocksPerColumnForMcu * 8;
            c.Samples = new byte[w * h];
            ushort[] quant = _quant[c.QuantId];

            var work = new double[64];
            for (int by = 0; by < c.BlocksPerColumnForMcu; by++)
            {
                for (int bx = 0; bx < c.BlocksPerLineForMcu; bx++)
                {
                    int coeffOffset = 64 * (by * c.BlocksPerLineForMcu + bx);
                    for (int i = 0; i < 64; i++)
                    {
                        work[i] = c.Coeff[coeffOffset + i] * quant[i];
                    }
                    Idct8x8(work);
                    int px = bx * 8, py = by * 8;
                    for (int yy = 0; yy < 8; yy++)
                    {
                        int row = (py + yy) * w + px;
                        for (int xx = 0; xx < 8; xx++)
                        {
                            c.Samples[row + xx] = Clamp((int)Math.Round(work[yy * 8 + xx]) + 128);
                        }
                    }
                }
            }
        }

        // Separable float inverse DCT (rows then columns), in place on 64 samples.
        private static readonly double[] IdctCos = BuildIdctCos();

        private static double[] BuildIdctCos()
        {
            // cos[x*8+u] = c(u) * cos((2x+1) u pi / 16), with c(0) = 1/sqrt2.
            var t = new double[64];
            for (int x = 0; x < 8; x++)
            {
                for (int u = 0; u < 8; u++)
                {
                    double cu = u == 0 ? Math.Sqrt(0.5) : 1.0;
                    t[x * 8 + u] = cu * Math.Cos((2 * x + 1) * u * Math.PI / 16.0);
                }
            }
            return t;
        }

        private static void Idct8x8(double[] block)
        {
            var tmp = new double[64];
            // Rows.
            for (int y = 0; y < 8; y++)
            {
                int r = y * 8;
                for (int x = 0; x < 8; x++)
                {
                    double sum = 0;
                    for (int u = 0; u < 8; u++)
                    {
                        sum += IdctCos[x * 8 + u] * block[r + u];
                    }
                    tmp[r + x] = sum * 0.5;
                }
            }
            // Columns.
            for (int x = 0; x < 8; x++)
            {
                for (int y = 0; y < 8; y++)
                {
                    double sum = 0;
                    for (int v = 0; v < 8; v++)
                    {
                        sum += IdctCos[y * 8 + v] * tmp[v * 8 + x];
                    }
                    block[y * 8 + x] = sum * 0.5;
                }
            }
        }

        private static byte Clamp(int v) => v < 0 ? (byte)0 : v > 255 ? (byte)255 : (byte)v;
    }
}
