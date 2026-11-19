// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// What Windows' source reader puts after an audio decoder when the type asked for is not one the decoder
// gives (its resampler, CResamplerMediaObject, as probed: MFSourceReaderProbe's audio report):
//
//   - the sample format: 8, 16, 24 or 32-bit integers, 32-bit float;
//   - the channels: to mono (L + R) / 2, mono to stereo duplicated, mono to more to the centre, stereo to
//     more to the front left and right, more to fewer by ITU's coefficients, scaled not to clip;
//   - the rate: a windowed sinc, its delay taken out -- the sound where it was in time, every frame there.
//

using System;
using System.Collections.Generic;

namespace Microsoft.Wpf.Interop.WebGpu.MediaFoundation.Source
{
    internal sealed class AudioConverter
    {
        public readonly int InChannels, OutChannels, InRate, OutRate;
        private readonly bool _inFloat, _outFloat;
        private readonly int _inBits, _outBits;
        private readonly float[,] _mix;
        private readonly List<float>[] _history;          // per output channel, the input frames not yet used up
        private double _pos;                              // the next output frame's position in the history
        private readonly double _step;
        private const int K = 16;                         // taps each side
        private bool _started;

        public AudioConverter(bool inFloat, int inBits, int inChannels, int inRate, bool outFloat, int outBits, int outChannels, int outRate)
        {
            _inFloat = inFloat; _inBits = inBits; InChannels = inChannels; InRate = inRate;
            _outFloat = outFloat; _outBits = outBits; OutChannels = outChannels; OutRate = outRate;
            _mix = Matrix(inChannels, outChannels);
            _history = new List<float>[outChannels];
            for (int c = 0; c < outChannels; c++) _history[c] = new List<float>();
            _step = (double)inRate / outRate;
        }

        public bool Resamples => InRate != OutRate;

        /// <summary>The mask of WAVE's speakers for a count of channels (MF_MT_AUDIO_CHANNEL_MASK).</summary>
        public static uint Mask(int channels) => channels switch { 1 => 0x4, 2 => 0x3, 3 => 0x7, 4 => 0x33, 5 => 0x37, 6 => 0x3F, 7 => 0x13F, 8 => 0x63F, _ => 0 };

        /// <summary>The mixing matrix [out, in], in WAVE's order (FL, FR, FC, LFE, BL, BR, SL, SR).</summary>
        private static float[,] Matrix(int n, int m)
        {
            var a = new float[m, n];
            if (n == m) { for (int i = 0; i < n; i++) a[i, i] = 1; return a; }
            if (n == 1)
            {
                // mono: to stereo both, to more the centre (where there is one)
                if (m == 2 || m == 4) { a[0, 0] = 1; a[1, 0] = 1; }
                else a[2, 0] = 1;
                return a;
            }
            if (m == 1)
            {
                // to mono: the mean of the front left and right (and of the rest, by ITU's weights)
                if (n == 2) { a[0, 0] = 0.5f; a[0, 1] = 0.5f; return a; }
                float[] w = Weights(n);
                float sum = 0; foreach (var x in w) sum += x;
                for (int i = 0; i < n; i++) a[0, i] = w[i] / sum;
                return a;
            }
            if (n == 2) { a[0, 0] = 1; a[1, 1] = 1; return a; }          // stereo to more: the front left and right
            if (m == 2)
            {
                // ITU-R BS.775: L = FL + 0.707 C + 0.707 SL, R likewise, scaled not to clip
                const float h = 0.7071f;
                for (int i = 0; i < n; i++)
                {
                    switch (i)
                    {
                        case 0: a[0, 0] = 1; break;
                        case 1: a[1, 1] = 1; break;
                        case 2: a[0, 2] = h; a[1, 2] = h; break;
                        case 3: break;                                          // the LFE dropped
                        default: if ((i & 1) == 0) a[0, i] = h; else a[1, i] = h; break;
                    }
                }
                float s = 1 + h + (n > 4 ? h : 0);
                for (int o = 0; o < 2; o++) for (int i = 0; i < n; i++) a[o, i] /= s;
                return a;
            }
            // otherwise: channel to channel where both have it
            for (int i = 0; i < Math.Min(n, m); i++) a[i, i] = 1;
            return a;
        }

        private static float[] Weights(int n)
        {
            var w = new float[n];
            for (int i = 0; i < n; i++) w[i] = i == 3 ? 0 : i < 2 ? 1 : 0.7071f;
            return w;
        }

        private float Sample(byte[] src, int i)
        {
            if (_inFloat) return BitConverter.ToSingle(src, i * 4);
            return _inBits switch
            {
                8 => (src[i] - 128) / 128f,
                16 => BitConverter.ToInt16(src, i * 2) / 32768f,
                24 => (src[i * 3] | src[i * 3 + 1] << 8 | (sbyte)src[i * 3 + 2] << 16) / 8388608f,
                _ => (float)(BitConverter.ToInt32(src, i * 4) / 2147483648.0),
            };
        }

        /// <summary>Input frames in, output bytes out (fewer at first: the filter's look-ahead; <see cref="Drain"/> gives the rest).</summary>
        public byte[] Convert(byte[] src)
        {
            int frames = src.Length / (InChannels * (_inFloat ? 4 : _inBits / 8));
            var mixed = new float[OutChannels][];
            for (int c = 0; c < OutChannels; c++) mixed[c] = new float[frames];
            for (int f = 0; f < frames; f++)
                for (int i = 0; i < InChannels; i++)
                {
                    float v = Sample(src, f * InChannels + i);
                    for (int o = 0; o < OutChannels; o++) if (_mix[o, i] != 0) mixed[o][f] += _mix[o, i] * v;
                }
            if (!Resamples) return Write(mixed, frames);
            if (!_started)
            {
                // K frames of silence before the first: the filter centred on frame 0 from the start
                for (int c = 0; c < OutChannels; c++) _history[c].AddRange(new float[K]);
                _pos = K;
                _started = true;
            }
            for (int c = 0; c < OutChannels; c++) _history[c].AddRange(mixed[c]);
            _realEnd = _history[0].Count;
            return Write(Filter(false), -1);
        }

        /// <summary>What the filter still holds, at the end of the stream.</summary>
        public byte[] Drain()
        {
            if (!Resamples || !_started) return Array.Empty<byte>();
            for (int c = 0; c < OutChannels; c++) _history[c].AddRange(new float[K + 1]);
            var r = Write(Filter(true), -1);
            Reset();
            return r;
        }

        public void Reset()
        {
            foreach (var h in _history) h.Clear();
            _pos = 0; _started = false; _realEnd = 0;
        }

        /// <summary>The history's end of real input (past it, the padding of the drain).</summary>
        private int _realEnd;

        private float[][] Filter(bool draining)
        {
            int have = _history[0].Count;
            var outs = new List<float>[OutChannels];
            for (int c = 0; c < OutChannels; c++) outs[c] = new List<float>();
            double fc = Math.Min(1.0, (double)OutRate / InRate);
            // each output needs K frames after it; draining, the outputs up to the real input's end
            while (draining ? _pos < _realEnd : _pos + K < have)
            {
                int i0 = (int)Math.Floor(_pos);
                double frac = _pos - i0;
                for (int c = 0; c < OutChannels; c++)
                {
                    var h = _history[c];
                    double acc = 0;
                    for (int k = -K + 1; k <= K; k++)
                    {
                        int idx = i0 + k;
                        if (idx < 0 || idx >= have) continue;
                        acc += h[idx] * Kernel(k - frac, fc);
                    }
                    outs[c].Add((float)acc);
                }
                _pos += _step;
            }
            // drop what no output needs any more
            int drop = Math.Max(0, (int)Math.Floor(_pos) - K);
            if (drop > 0) { foreach (var h in _history) h.RemoveRange(0, Math.Min(drop, h.Count)); _pos -= drop; _realEnd -= drop; }
            var r = new float[OutChannels][];
            for (int c = 0; c < OutChannels; c++) r[c] = outs[c].ToArray();
            return r;
        }

        private static double Kernel(double t, double fc)
        {
            double x = t * fc;
            double sinc = Math.Abs(x) < 1e-9 ? 1 : Math.Sin(Math.PI * x) / (Math.PI * x);
            double r = t / K;
            if (Math.Abs(r) >= 1) return 0;
            // a Kaiser window, beta 8.6
            const double beta = 8.6;
            return fc * sinc * Bessel0(beta * Math.Sqrt(1 - r * r)) / Bessel0(beta);
        }

        private static double Bessel0(double x)
        {
            double sum = 1, term = 1, q = x * x / 4;
            for (int k = 1; k < 30; k++) { term *= q / (k * k); sum += term; if (term < 1e-12 * sum) break; }
            return sum;
        }

        private byte[] Write(float[][] ch, int frames)
        {
            if (frames < 0) frames = ch[0].Length;
            int bytes = _outFloat ? 4 : _outBits / 8;
            var o = new byte[frames * OutChannels * bytes];
            for (int f = 0; f < frames; f++)
                for (int c = 0; c < OutChannels; c++)
                {
                    double v = ch[c][f];
                    int i = f * OutChannels + c;
                    if (_outFloat) { BitConverter.TryWriteBytes(o.AsSpan(i * 4), (float)v); continue; }
                    switch (bytes)
                    {
                        case 1: o[i] = (byte)(Math.Clamp(Math.Round(v * 128), -128, 127) + 128); break;
                        case 2: BitConverter.TryWriteBytes(o.AsSpan(i * 2), (short)Math.Clamp(Math.Round(v * 32768), -32768, 32767)); break;
                        case 3: { int x = (int)Math.Clamp(Math.Round(v * 8388608), -8388608, 8388607); o[i * 3] = (byte)x; o[i * 3 + 1] = (byte)(x >> 8); o[i * 3 + 2] = (byte)(x >> 16); break; }
                        default: BitConverter.TryWriteBytes(o.AsSpan(i * 4), (int)Math.Clamp(Math.Round(v * 2147483648.0), int.MinValue, int.MaxValue)); break;
                    }
                }
            return o;
        }
    }
}
