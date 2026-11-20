// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// winmm's wave formats (WaveExports.cs): which a waveOutOpen or waveInOpen takes, as Windows' does (WaveProbe,
// Wave.windows.txt): PCM of 8, 16, 24 or 32 bits and one or two channels at any rate (its block and byte rate not
// checked); IEEE float; WAVE_FORMAT_EXTENSIBLE of PCM or float, of any channels, its cbSize at least 22 (else the
// mapper's WAVERR_BADFORMAT, a device's MMSYSERR_INVALPARAM); A-law and mu-law only through the mapper (Windows'
// ACM converts them); nothing else. The samples of each to 32-bit float and back, and a linear resampler between
// the rates.
//

using System;

namespace Microsoft.Wpf.Interop.WebGpu.Winmm
{
    internal enum SampleKind { Pcm, Float, ALaw, MuLaw }

    internal sealed class WaveFormat
    {
        public SampleKind Kind;
        public int Channels, Rate, Bits, Container, BlockAlign;

        public const ushort WAVE_FORMAT_PCM = 1, WAVE_FORMAT_IEEE_FLOAT = 3, WAVE_FORMAT_ALAW = 6, WAVE_FORMAT_MULAW = 7, WAVE_FORMAT_EXTENSIBLE = 0xFFFE;
        private static readonly Guid KSDATAFORMAT_SUBTYPE_PCM = new("00000001-0000-0010-8000-00aa00389b71"), KSDATAFORMAT_SUBTYPE_IEEE_FLOAT = new("00000003-0000-0010-8000-00aa00389b71");

        /// <summary>The format of a WAVEFORMATEX(TENSIBLE), or the error an open gives of it.</summary>
        public static unsafe uint Parse(byte* p, bool mapped, out WaveFormat? f)
        {
            f = null;
            ushort tag = *(ushort*)p, ch = *(ushort*)(p + 2), bits = *(ushort*)(p + 14);
            uint rate = *(uint*)(p + 4);
            if (ch == 0 || rate == 0) return WaveExports.WAVERR_BADFORMAT;
            switch (tag)
            {
                case WAVE_FORMAT_PCM:
                    if (ch > 2 || bits is not (8 or 16 or 24 or 32)) return WaveExports.WAVERR_BADFORMAT;
                    f = new WaveFormat { Kind = SampleKind.Pcm, Bits = bits, Container = bits };
                    break;
                case WAVE_FORMAT_IEEE_FLOAT:
                    if (ch > 2 || bits is not (16 or 32 or 64)) return WaveExports.WAVERR_BADFORMAT;
                    f = new WaveFormat { Kind = SampleKind.Float, Bits = bits, Container = bits };
                    break;
                case WAVE_FORMAT_ALAW or WAVE_FORMAT_MULAW:
                    if (!mapped || ch > 2 || bits != 8) return WaveExports.WAVERR_BADFORMAT;
                    f = new WaveFormat { Kind = tag == WAVE_FORMAT_ALAW ? SampleKind.ALaw : SampleKind.MuLaw, Bits = 8, Container = 8 };
                    break;
                case WAVE_FORMAT_EXTENSIBLE:
                {
                    if (*(ushort*)(p + 16) < 22) return mapped ? WaveExports.WAVERR_BADFORMAT : WaveExports.MMSYSERR_INVALPARAM;
                    ushort valid = *(ushort*)(p + 18);
                    var sub = *(Guid*)(p + 24);
                    if (sub == KSDATAFORMAT_SUBTYPE_PCM && bits is 8 or 16 or 24 or 32)
                        f = new WaveFormat { Kind = SampleKind.Pcm, Container = bits, Bits = valid is > 0 and <= 32 ? Math.Min(valid, bits) : bits };
                    else if (sub == KSDATAFORMAT_SUBTYPE_IEEE_FLOAT && bits is 32 or 64)
                        f = new WaveFormat { Kind = SampleKind.Float, Bits = bits, Container = bits };
                    else return WaveExports.WAVERR_BADFORMAT;
                    break;
                }
                default:
                    return WaveExports.WAVERR_BADFORMAT;
            }
            f.Channels = ch; f.Rate = (int)rate;
            f.BlockAlign = ch * f.Container / 8;
            return 0;
        }

        /// <summary>The frames of the bytes as float.</summary>
        public void Decode(ReadOnlySpan<byte> b, Span<float> into)
        {
            int n = b.Length / (Container / 8);
            for (int i = 0; i < n && i < into.Length; i++)
            {
                into[i] = (Kind, Container) switch
                {
                    (SampleKind.Pcm, 8) => (b[i] - 128) / 128f,
                    (SampleKind.Pcm, 16) => BitConverter.ToInt16(b.Slice(i * 2)) / 32768f,
                    (SampleKind.Pcm, 24) => ((b[i * 3] | (b[i * 3 + 1] << 8) | ((sbyte)b[i * 3 + 2] << 16))) / 8388608f,
                    (SampleKind.Pcm, 32) => BitConverter.ToInt32(b.Slice(i * 4)) / 2147483648f,
                    (SampleKind.Float, 16) => (float)BitConverter.ToHalf(b.Slice(i * 2)),
                    (SampleKind.Float, 32) => BitConverter.ToSingle(b.Slice(i * 4)),
                    (SampleKind.Float, 64) => (float)BitConverter.ToDouble(b.Slice(i * 8)),
                    (SampleKind.ALaw, _) => ALaw(b[i]) / 32768f,
                    (SampleKind.MuLaw, _) => MuLaw(b[i]) / 32768f,
                    _ => 0,
                };
            }
        }

        /// <summary>The float samples into the format's bytes.</summary>
        public void Encode(ReadOnlySpan<float> s, Span<byte> into)
        {
            int size = Container / 8;
            for (int i = 0; i < s.Length && (i + 1) * size <= into.Length; i++)
            {
                float v = Math.Clamp(s[i], -1f, 1f);
                var o = into.Slice(i * size);
                switch (Kind, Container)
                {
                    // (of the scale the decoding's, the top clamped)
                    case (SampleKind.Pcm, 8): o[0] = (byte)(Math.Clamp((int)MathF.Round(v * 128f), -128, 127) + 128); break;
                    case (SampleKind.Pcm, 16): BitConverter.TryWriteBytes(o, (short)Math.Clamp((int)MathF.Round(v * 32768f), -32768, 32767)); break;
                    case (SampleKind.Pcm, 24): { int x = Math.Clamp((int)Math.Round(v * 8388608.0), -8388608, 8388607); o[0] = (byte)x; o[1] = (byte)(x >> 8); o[2] = (byte)(x >> 16); break; }
                    case (SampleKind.Pcm, 32): BitConverter.TryWriteBytes(o, (int)Math.Clamp(Math.Round(v * 2147483648.0), int.MinValue, int.MaxValue)); break;
                    case (SampleKind.Float, 16): BitConverter.TryWriteBytes(o, (Half)s[i]); break;
                    case (SampleKind.Float, 32): BitConverter.TryWriteBytes(o, s[i]); break;
                    case (SampleKind.Float, 64): BitConverter.TryWriteBytes(o, (double)s[i]); break;
                    case (SampleKind.ALaw, _): o[0] = ToALaw((short)MathF.Round(v * 32767f)); break;
                    case (SampleKind.MuLaw, _): o[0] = ToMuLaw((short)MathF.Round(v * 32767f)); break;
                }
            }
        }

        // G.711
        private static short ALaw(byte a)
        {
            a ^= 0x55;
            int t = (a & 0x0F) << 4, seg = (a & 0x70) >> 4;
            t = seg == 0 ? t + 8 : seg == 1 ? t + 0x108 : (t + 0x108) << (seg - 1);
            return (short)((a & 0x80) != 0 ? t : -t);
        }

        private static short MuLaw(byte u)
        {
            u = (byte)~u;
            int t = ((u & 0x0F) << 3) + 0x84;
            t <<= (u & 0x70) >> 4;
            return (short)((u & 0x80) != 0 ? 0x84 - t : t - 0x84);
        }

        private static byte ToALaw(short pcm)
        {
            int mask = pcm >= 0 ? 0xD5 : 0x55, v = pcm >= 0 ? pcm >> 3 : (-pcm - 1) >> 3;
            int seg = 0;
            while (seg < 8 && v > (0x20 << seg) - 1) seg++;
            if (seg >= 8) return (byte)(0x7F ^ mask);
            int a = seg << 4 | (seg < 2 ? (v >> 1) & 0x0F : (v >> seg) & 0x0F);
            return (byte)(a ^ mask);
        }

        private static byte ToMuLaw(short pcm)
        {
            int sign = pcm < 0 ? 0x80 : 0, v = Math.Min(Math.Abs((int)pcm), 32635) + 0x84;
            int exp = 7;
            for (int m = 0x4000; (v & m) == 0 && exp > 0; m >>= 1) exp--;
            int mant = (v >> (exp + 3)) & 0x0F;
            return (byte)~(sign | exp << 4 | mant);
        }
    }

    /// <summary>Linear interpolation of interleaved frames from one rate to another, across calls.</summary>
    internal sealed class LinearResampler
    {
        private readonly int _channels;
        private readonly float[] _last;
        private double _pos;          // the next output's place, in input frames after the last one kept
        private bool _primed;

        public LinearResampler(int channels) { _channels = channels; _last = new float[channels]; }

        public void Reset() { _pos = 0; _primed = false; }

        /// <summary>The input's frames at <paramref name="step"/> input frames per output frame, appended.</summary>
        public void Process(ReadOnlySpan<float> input, double step, System.Collections.Generic.List<float> output)
        {
            int frames = input.Length / _channels;
            if (frames == 0) return;
            if (!_primed) { input.Slice(0, _channels).CopyTo(_last); _primed = true; _pos = 1; }
            // the frames as: _last at index 0, input at 1..frames
            while (_pos <= frames)
            {
                int i = (int)_pos;
                double t = _pos - i;
                for (int c = 0; c < _channels; c++)
                {
                    float a = i == 0 ? _last[c] : input[(i - 1) * _channels + c];
                    float b = i >= frames ? a : input[i * _channels + c];
                    output.Add((float)(a + (b - a) * t));
                }
                _pos += step;
            }
            _pos -= frames;
            input.Slice((frames - 1) * _channels, _channels).CopyTo(_last);
        }
    }
}
