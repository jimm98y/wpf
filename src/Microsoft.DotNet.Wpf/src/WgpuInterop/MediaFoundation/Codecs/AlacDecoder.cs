// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// Apple Lossless decoded in managed code, for a system whose decoders have none (the browser's WebCodecs): a port of
// the algorithm of Apple's open-source ALAC decoder (ALACDecoder.cpp, ag_dec.c, dp_dec.c, matrix_dec.c; Apache
// License 2.0) -- each frame's elements (single channels, channel pairs, fill and data elements) of adaptive
// Golomb codes, the dynamic predictor, the pair's unmixing and the shifted low bytes; PCM out of the stream's bits,
// interleaved (20-bit in 24-bit containers, as Apple's decoder writes it).
//

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Numerics;

namespace Microsoft.Wpf.Interop.WebGpu.MediaFoundation.Codecs
{
    internal sealed class AlacDecoder
    {
        private const int QBSHIFT = 9, QB = 1 << QBSHIFT, MMULSHIFT = 2, MDENSHIFT = QBSHIFT - MMULSHIFT - 1, MOFF = 1 << (MDENSHIFT - 2), BITOFF = 24;
        private const int MAX_PREFIX_16 = 9, MAX_PREFIX_32 = 9, MAX_DATATYPE_BITS_16 = 16, N_MAX_MEAN_CLAMP = 0xffff, N_MEAN_CLAMP_VAL = 0xffff;

        // ALACSpecificConfig
        private readonly uint _frameLength;
        private readonly int _bitDepth, _pb, _mb, _kb, _channels, _maxRun;
        public uint SampleRate { get; }
        public uint Channels => (uint)_channels;
        /// <summary>The PCM's bits: the stream's, 20-bit in 24.</summary>
        public uint BitsPerSample => _bitDepth == 20 ? 24u : (uint)_bitDepth;

        private readonly int[] _mixU, _mixV, _predictor;
        private readonly ushort[] _shift;
        private readonly Queue<byte[]> _out = new();

        /// <summary>Of the ALACSpecificConfig (24 bytes), or a cookie of 'frma'/'alac' atoms holding one.</summary>
        public AlacDecoder(byte[] config)
        {
            var c = Specific(config) ?? throw new ArgumentException("no ALACSpecificConfig");
            _frameLength = BinaryPrimitives.ReadUInt32BigEndian(c);
            _bitDepth = c[5]; _pb = c[6]; _mb = c[7]; _kb = c[8]; _channels = c[9];
            _maxRun = BinaryPrimitives.ReadUInt16BigEndian(c.AsSpan(10));
            SampleRate = BinaryPrimitives.ReadUInt32BigEndian(c.AsSpan(20));
            if (_bitDepth is not (16 or 20 or 24 or 32) || _channels is < 1 or > 8 || _frameLength == 0 || _frameLength > 1 << 20) throw new ArgumentException("an ALACSpecificConfig of no stream this decodes");
            _mixU = new int[_frameLength]; _mixV = new int[_frameLength]; _predictor = new int[_frameLength];
            _shift = new ushort[_frameLength * 2];
        }

        private static byte[]? Specific(byte[] cookie)
        {
            if (cookie.Length == 24) return cookie;
            for (int i = 0; i + 12 + 24 <= cookie.Length; i++)
                if (cookie[i + 4] == 'a' && cookie[i + 5] == 'l' && cookie[i + 6] == 'a' && cookie[i + 7] == 'c')
                {
                    int size = cookie[i] << 24 | cookie[i + 1] << 16 | cookie[i + 2] << 8 | cookie[i + 3];
                    if (size >= 36 && i + size <= cookie.Length) return cookie.AsSpan(i + 12, 24).ToArray();
                }
            return cookie.Length >= 24 ? cookie.AsSpan(cookie.Length - 24).ToArray() : null;
        }

        public void Push(ReadOnlySpan<byte> packet)
        {
            if (packet.IsEmpty) return;
            var pcm = Decode(packet, out int frames);
            if (frames > 0) _out.Enqueue(pcm.AsSpan(0, frames * _channels * (int)(BitsPerSample / 8)).ToArray());
        }

        public bool TryTake(ref byte[] buffer, out uint length)
        {
            length = 0;
            if (!_out.TryDequeue(out var p)) return false;
            if (buffer == null || buffer.Length < p.Length) buffer = new byte[Math.Max(p.Length, 4096)];
            p.CopyTo(buffer, 0);
            length = (uint)p.Length;
            return true;
        }

        public void Flush() => _out.Clear();

        // ---- the bit buffer: big-endian, most significant bit first (padded past its end) --------------

        private sealed class Bits
        {
            public readonly byte[] B;
            public readonly int End;
            public int Pos;                                           // in bits
            public Bits(ReadOnlySpan<byte> data) { End = data.Length; B = new byte[data.Length + 8]; data.CopyTo(B); }
            public uint Read(int n)
            {
                if (n == 0) return 0;
                uint r = 0;
                for (int i = 0; i < n; i++) { r = (r << 1) | (uint)((B[(Pos >> 3)] >> (7 - (Pos & 7))) & 1); Pos++; }
                return r;
            }
            public void Advance(long n) => Pos += (int)n;
            public void ByteAlign() => Pos = (Pos + 7) & ~7;
        }

        private static uint Read32(byte[] b, int at) => (uint)(b[at] << 24 | b[at + 1] << 16 | b[at + 2] << 8 | b[at + 3]);
        private static int Lead(uint x) => BitOperations.LeadingZeroCount(x);
        private static int Lg3a(uint x) => 31 - Lead(x + 3);

        private static uint GetStreamBits(byte[] input, int bitOffset, int numBits)
        {
            int byteOffset = bitOffset / 8;
            uint load1 = Read32(input, byteOffset), result;
            if (numBits + (bitOffset & 7) > 32)
            {
                result = load1 << (bitOffset & 7);
                uint load2 = input[byteOffset + 4];
                int load2shift = 8 - (numBits + (bitOffset & 7) - 32);
                load2 >>= load2shift;
                result >>= 32 - numBits;
                result |= load2;
            }
            else result = load1 >> (32 - numBits - (bitOffset & 7));
            if (numBits != 32) result &= ~(0xffffffffu << numBits);
            return result;
        }

        private static uint DynGet(byte[] input, ref int bitPos, uint m, int k)
        {
            int tempbits = bitPos;
            uint streamlong = Read32(input, tempbits >> 3) << (tempbits & 7);
            int pre = Lead(~streamlong);
            uint result;
            if (pre >= MAX_PREFIX_16)
            {
                pre = MAX_PREFIX_16;
                tempbits += pre;
                streamlong <<= pre;
                result = streamlong >> (32 - MAX_DATATYPE_BITS_16);
                tempbits += MAX_DATATYPE_BITS_16;
            }
            else
            {
                tempbits += pre + 1;
                streamlong <<= pre + 1;
                uint v = k == 0 ? 0 : streamlong >> (32 - k);
                tempbits += k;
                result = (uint)pre * m + v - 1;
                if (v < 2) { result -= v - 1; tempbits -= 1; }
            }
            bitPos = tempbits;
            return result;
        }

        private static uint DynGet32(byte[] input, ref int bitPos, uint m, int k, int maxBits)
        {
            int tempbits = bitPos;
            uint streamlong = Read32(input, tempbits >> 3) << (tempbits & 7);
            uint result = (uint)Lead(~streamlong);
            if (result >= MAX_PREFIX_32)
            {
                result = GetStreamBits(input, tempbits + MAX_PREFIX_32, maxBits);
                tempbits += MAX_PREFIX_32 + maxBits;
            }
            else
            {
                tempbits += (int)result + 1;
                if (k != 1)
                {
                    streamlong <<= (int)result + 1;
                    uint v = streamlong >> (32 - k);
                    tempbits += k - 1;
                    result *= m;
                    if (v >= 2) { result += v - 1; tempbits += 1; }
                }
            }
            bitPos = tempbits;
            return result;
        }

        /// <summary>The adaptive Golomb codes of a channel (ag_dec.c's dyn_decomp).</summary>
        private void DynDecomp(Bits bits, int[] pc, int numSamples, int maxSize, int pb, int mb0, int kb)
        {
            uint wb = (1u << kb) - 1;
            uint mb = (uint)mb0;
            uint zmode = 0;
            int c = 0, outAt = 0;
            int bitPos = bits.Pos, maxPos = bits.End * 8;
            while (c < numSamples)
            {
                if (bitPos >= maxPos) throw new InvalidOperationException("an ALAC frame past its end");
                uint m = mb >> QBSHIFT;
                int k = Math.Min(Lg3a(m), kb);
                m = (1u << k) - 1;
                uint n = DynGet32(bits.B, ref bitPos, m, k, maxSize);
                uint ndecode = n + zmode;
                int multiplier = -(int)(ndecode & 1) | 1;
                pc[outAt++] = (int)((ndecode + 1) >> 1) * multiplier;
                c++;
                mb = (uint)pb * (n + zmode) + mb - (((uint)pb * mb) >> QBSHIFT);
                if (n > N_MAX_MEAN_CLAMP) mb = N_MEAN_CLAMP_VAL;
                zmode = 0;
                if ((mb << MMULSHIFT) < QB && c < numSamples)
                {
                    zmode = 1;
                    k = Lead(mb) - BITOFF + (int)((mb + MOFF) >> MDENSHIFT);
                    uint mz = ((1u << k) - 1) & wb;
                    n = DynGet(bits.B, ref bitPos, mz, k);
                    if (c + n > numSamples) throw new InvalidOperationException("an ALAC run past the frame");
                    for (uint j = 0; j < n; j++) { pc[outAt++] = 0; c++; }
                    if (n >= 65535) zmode = 0;
                    mb = 0;
                }
            }
            bits.Pos = bitPos;
        }

        private static int Sign(int i) => (int)((uint)-i >> 31) | (i >> 31);

        /// <summary>The dynamic predictor, undone (dp_dec.c's unpc_block): of the residuals into the samples, the coefficients adapting.</summary>
        private static void UnpcBlock(int[] pc1, int[] output, int num, short[]? coefs, int numActive, int chanBits, int denShift)
        {
            int chanShift = 32 - chanBits;
            int denHalf = denShift > 0 ? 1 << (denShift - 1) : 0;
            output[0] = pc1[0];
            if (numActive == 0)
            {
                if (num > 1 && !ReferenceEquals(pc1, output)) Array.Copy(pc1, 1, output, 1, num - 1);
                return;
            }
            if (numActive == 31)
            {
                int prev = output[0];
                for (int j = 1; j < num; j++) { int d = pc1[j] + prev; prev = (d << chanShift) >> chanShift; output[j] = prev; }
                return;
            }
            for (int j = 1; j <= numActive && j < num; j++) { int d = pc1[j] + output[j - 1]; output[j] = (d << chanShift) >> chanShift; }
            int lim = numActive + 1;
            for (int j = lim; j < num; j++)
            {
                int sum1 = 0, p = j - 1, top = output[j - lim];
                for (int k = 0; k < numActive; k++) sum1 += coefs![k] * (output[p - k] - top);
                int del = pc1[j], del0 = del;
                int sg = Sign(del);
                del += top + ((sum1 + denHalf) >> denShift);
                output[j] = (del << chanShift) >> chanShift;
                if (sg > 0)
                {
                    for (int k = numActive - 1; k >= 0; k--)
                    {
                        int dd = top - output[p - k];
                        int sgn = Sign(dd);
                        coefs![k] -= (short)sgn;
                        del0 -= (numActive - k) * ((sgn * dd) >> denShift);
                        if (del0 <= 0) break;
                    }
                }
                else if (sg < 0)
                {
                    for (int k = numActive - 1; k >= 0; k--)
                    {
                        int dd = top - output[p - k];
                        int sgn = Sign(dd);
                        coefs![k] += (short)sgn;
                        del0 -= (numActive - k) * ((-sgn * dd) >> denShift);
                        if (del0 >= 0) break;
                    }
                }
            }
        }

        // ---- a frame --------------------------------------------------------------------------------

        private byte[] _pcm = Array.Empty<byte>();

        private byte[] Decode(ReadOnlySpan<byte> packet, out int frames)
        {
            var bits = new Bits(packet);
            int bytesPer = (int)BitsPerSample / 8;
            int needed = (int)_frameLength * _channels * bytesPer;
            if (_pcm.Length < needed) _pcm = new byte[needed];
            Array.Clear(_pcm, 0, needed);
            int channel = 0;
            frames = (int)_frameLength;
            var coefsU = new short[32]; var coefsV = new short[32];
            while (bits.Pos < bits.End * 8)
            {
                int tag = (int)bits.Read(3);
                switch (tag)
                {
                    case 0:                     // ID_SCE
                    case 3:                     // ID_LFE
                    {
                        bits.Read(4); bits.Read(12);
                        int header = (int)bits.Read(4);
                        bool partial = (header >> 3) != 0;
                        int bytesShifted = (header >> 1) & 3;
                        bool escape = (header & 1) != 0;
                        int chanBits = _bitDepth - bytesShifted * 8;
                        int numSamples = (int)_frameLength;
                        if (partial) numSamples = (int)(bits.Read(16) << 16 | bits.Read(16));
                        if (numSamples > _frameLength) throw new InvalidOperationException("an ALAC frame longer than the stream's");
                        Bits? shiftBits = null;
                        if (!escape)
                        {
                            bits.Read(8); bits.Read(8);                          // mixBits, mixRes: of no single channel
                            int h = (int)bits.Read(8); int modeU = h >> 4, denShiftU = h & 15;
                            h = (int)bits.Read(8); int pbFactorU = h >> 5, numU = h & 31;
                            for (int i = 0; i < numU; i++) coefsU[i] = (short)bits.Read(16);
                            if (bytesShifted != 0) { shiftBits = new Bits(packet) { Pos = bits.Pos }; bits.Advance((long)bytesShifted * 8 * numSamples); }
                            DynDecomp(bits, _predictor, numSamples, chanBits, _pb * pbFactorU / 4, _mb, _kb);
                            if (modeU == 0) UnpcBlock(_predictor, _mixU, numSamples, coefsU, numU, chanBits, denShiftU);
                            else { UnpcBlock(_predictor, _predictor, numSamples, null, 31, chanBits, 0); UnpcBlock(_predictor, _mixU, numSamples, coefsU, numU, chanBits, denShiftU); }
                        }
                        else
                        {
                            int shift = 32 - chanBits;
                            if (chanBits <= 16) for (int i = 0; i < numSamples; i++) _mixU[i] = (int)(bits.Read(chanBits) << shift) >> shift;
                            else
                            {
                                int extra = chanBits - 16;
                                for (int i = 0; i < numSamples; i++) { int v = (int)(bits.Read(16) << 16) >> shift; _mixU[i] = v | (int)bits.Read(extra); }
                            }
                            bytesShifted = 0;
                        }
                        if (bytesShifted != 0) for (int i = 0; i < numSamples; i++) _shift[i] = (ushort)shiftBits!.Read(bytesShifted * 8);
                        for (int i = 0; i < numSamples; i++)
                        {
                            int v = _mixU[i];
                            if (bytesShifted != 0) v = (v << (bytesShifted * 8)) | _shift[i];
                            Put(i, channel, v);
                        }
                        frames = numSamples;
                        channel++;
                        break;
                    }
                    case 1:                     // ID_CPE
                    {
                        bits.Read(4); bits.Read(12);
                        int header = (int)bits.Read(4);
                        bool partial = (header >> 3) != 0;
                        int bytesShifted = (header >> 1) & 3;
                        bool escape = (header & 1) != 0;
                        int chanBits = _bitDepth - bytesShifted * 8 + 1;
                        int numSamples = (int)_frameLength;
                        if (partial) numSamples = (int)(bits.Read(16) << 16 | bits.Read(16));
                        if (numSamples > _frameLength) throw new InvalidOperationException("an ALAC frame longer than the stream's");
                        int mixBits = 0, mixRes = 0;
                        Bits? shiftBits = null;
                        if (!escape)
                        {
                            mixBits = (int)bits.Read(8); mixRes = (sbyte)bits.Read(8);
                            int h = (int)bits.Read(8); int modeU = h >> 4, denShiftU = h & 15;
                            h = (int)bits.Read(8); int pbFactorU = h >> 5, numU = h & 31;
                            for (int i = 0; i < numU; i++) coefsU[i] = (short)bits.Read(16);
                            h = (int)bits.Read(8); int modeV = h >> 4, denShiftV = h & 15;
                            h = (int)bits.Read(8); int pbFactorV = h >> 5, numV = h & 31;
                            for (int i = 0; i < numV; i++) coefsV[i] = (short)bits.Read(16);
                            if (bytesShifted != 0) { shiftBits = new Bits(packet) { Pos = bits.Pos }; bits.Advance((long)bytesShifted * 8 * 2 * numSamples); }
                            DynDecomp(bits, _predictor, numSamples, chanBits, _pb * pbFactorU / 4, _mb, _kb);
                            if (modeU == 0) UnpcBlock(_predictor, _mixU, numSamples, coefsU, numU, chanBits, denShiftU);
                            else { UnpcBlock(_predictor, _predictor, numSamples, null, 31, chanBits, 0); UnpcBlock(_predictor, _mixU, numSamples, coefsU, numU, chanBits, denShiftU); }
                            DynDecomp(bits, _predictor, numSamples, chanBits, _pb * pbFactorV / 4, _mb, _kb);
                            if (modeV == 0) UnpcBlock(_predictor, _mixV, numSamples, coefsV, numV, chanBits, denShiftV);
                            else { UnpcBlock(_predictor, _predictor, numSamples, null, 31, chanBits, 0); UnpcBlock(_predictor, _mixV, numSamples, coefsV, numV, chanBits, denShiftV); }
                        }
                        else
                        {
                            chanBits = _bitDepth;
                            int shift = 32 - chanBits;
                            if (chanBits <= 16)
                                for (int i = 0; i < numSamples; i++)
                                {
                                    _mixU[i] = (int)(bits.Read(chanBits) << shift) >> shift;
                                    _mixV[i] = (int)(bits.Read(chanBits) << shift) >> shift;
                                }
                            else
                            {
                                int extra = chanBits - 16;
                                for (int i = 0; i < numSamples; i++)
                                {
                                    int u = (int)(bits.Read(16) << 16) >> shift; _mixU[i] = u | (int)bits.Read(extra);
                                    int v = (int)(bits.Read(16) << 16) >> shift; _mixV[i] = v | (int)bits.Read(extra);
                                }
                            }
                            bytesShifted = 0;
                        }
                        if (bytesShifted != 0)
                            for (int i = 0; i < numSamples; i++) { _shift[2 * i] = (ushort)shiftBits!.Read(bytesShifted * 8); _shift[2 * i + 1] = (ushort)shiftBits.Read(bytesShifted * 8); }
                        // unmixed (matrix_dec.c): l = u + v - ((mixRes * v) >> mixBits), r = l - v
                        for (int i = 0; i < numSamples; i++)
                        {
                            int l, r;
                            if (mixRes != 0) { l = _mixU[i] + _mixV[i] - ((mixRes * _mixV[i]) >> mixBits); r = l - _mixV[i]; }
                            else { l = _mixU[i]; r = _mixV[i]; }
                            if (bytesShifted != 0) { l = (l << (bytesShifted * 8)) | _shift[2 * i]; r = (r << (bytesShifted * 8)) | _shift[2 * i + 1]; }
                            Put(i, channel, l);
                            Put(i, channel + 1, r);
                        }
                        frames = numSamples;
                        channel += 2;
                        break;
                    }
                    case 2:                     // ID_CCE: not in ALAC
                    case 5:                     // ID_PCE
                        throw new InvalidOperationException("an ALAC element of no ALAC stream");
                    case 4:                     // ID_DSE
                    {
                        bits.Read(4);
                        bool align = bits.Read(1) != 0;
                        int count = (int)bits.Read(8);
                        if (count == 255) count += (int)bits.Read(8);
                        if (align) bits.ByteAlign();
                        bits.Advance(count * 8L);
                        break;
                    }
                    case 6:                     // ID_FIL
                    {
                        int count = (int)bits.Read(4);
                        if (count == 15) count += (int)bits.Read(8) - 1;
                        bits.Advance(count * 8L);
                        break;
                    }
                    case 7:                     // ID_END
                        return _pcm;
                }
                if (channel > _channels) throw new InvalidOperationException("an ALAC frame of more channels than the stream's");
            }
            return _pcm;
        }

        /// <summary>A sample into the PCM: 16, 24 (20-bit shifted up 4) or 32 bits, little-endian, interleaved.</summary>
        private void Put(int frame, int channel, int v)
        {
            if (channel >= _channels) return;
            int at = (frame * _channels + channel) * (int)(BitsPerSample / 8);
            switch (_bitDepth)
            {
                case 16: _pcm[at] = (byte)v; _pcm[at + 1] = (byte)(v >> 8); break;
                case 20: v <<= 4; _pcm[at] = (byte)v; _pcm[at + 1] = (byte)(v >> 8); _pcm[at + 2] = (byte)(v >> 16); break;
                case 24: _pcm[at] = (byte)v; _pcm[at + 1] = (byte)(v >> 8); _pcm[at + 2] = (byte)(v >> 16); break;
                default: BinaryPrimitives.WriteInt32LittleEndian(_pcm.AsSpan(at), v); break;
            }
        }
    }
}
