// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// H.264's sequence parameter set, as far as the coded size: what a decoder configured before its first frame needs
// where the input type names no size (WebCodecs', MediaCodec's).
//

using System;
using System.Collections.Generic;

namespace Microsoft.Wpf.Interop.WebGpu.MediaFoundation.Codecs
{
    internal static class H264Sps
    {
        /// <summary>The coded size (in macroblocks, before the cropping) of the access unit's SPS (H.264 7.3.2.1.1); null where it has none.</summary>
        public static (int W, int H)? CodedSize(ReadOnlySpan<byte> au)
        {
            foreach (var (o, n) in Nals(au))
            {
                if (n < 4 || (au[o] & 0x1F) != 7) continue;
                var r = new BitReader(Rbsp(au.Slice(o + 1, n - 1), 256));
                try
                {
                    int profile = (int)r.U(8); r.U(16); r.Ue();
                    if (profile is 100 or 110 or 122 or 244 or 44 or 83 or 86 or 118 or 128 or 138 or 139 or 134 or 135)
                    {
                        if (r.Ue() == 3) r.U(1);                         // chroma_format_idc; separate_colour_plane_flag
                        r.Ue(); r.Ue(); r.U(1);                          // bit depths, qpprime_y_zero_transform_bypass_flag
                        if (r.U(1) == 1)                                 // seq_scaling_matrix_present_flag
                            for (int i = 0; i < 8; i++)
                                if (r.U(1) == 1)
                                {
                                    int size = i < 6 ? 16 : 64, last = 8, next = 8;
                                    for (int j = 0; j < size && next != 0; j++) { next = (last + r.Se() + 256) % 256; last = next == 0 ? last : next; }
                                }
                    }
                    r.Ue();                                              // log2_max_frame_num_minus4
                    uint poc = r.Ue();
                    if (poc == 0) r.Ue();
                    else if (poc == 1) { r.U(1); r.Se(); r.Se(); uint k = r.Ue(); for (uint i = 0; i < k; i++) r.Se(); }
                    r.Ue(); r.U(1);                                      // max_num_ref_frames, gaps
                    uint w = r.Ue() + 1, h = r.Ue() + 1;
                    uint frameMbsOnly = r.U(1);
                    return ((int)w * 16, (int)((2 - frameMbsOnly) * h * 16));
                }
                catch (IndexOutOfRangeException) { return null; }
            }
            return null;
        }

        /// <summary>Bits of an RBSP, most significant first, with Exp-Golomb codes.</summary>
        private sealed class BitReader
        {
            private readonly byte[] _b;
            private int _bit;
            public BitReader(byte[] b) { _b = b; }
            public uint U(int n) { uint v = 0; for (int i = 0; i < n; i++, _bit++) v = (v << 1) | (uint)((_b[_bit >> 3] >> (7 - (_bit & 7))) & 1); return v; }
            public uint Ue() { int z = 0; while (U(1) == 0) { if (++z > 31) throw new IndexOutOfRangeException(); } return z == 0 ? 0 : (1u << z) - 1 + U(z); }
            public int Se() { uint k = Ue(); return (k & 1) != 0 ? (int)((k + 1) / 2) : -(int)(k / 2); }
        }

        /// <summary>The NAL units of Annex B: each one's payload's offset and length (past its start code).</summary>
        private static List<(int Offset, int Length)> Nals(ReadOnlySpan<byte> b)
        {
            var starts = new List<int>();
            for (int i = 0; i + 3 <= b.Length; i++)
                if (b[i] == 0 && b[i + 1] == 0 && b[i + 2] == 1) { starts.Add(i + 3); i += 2; }
            var r = new List<(int, int)>();
            for (int k = 0; k < starts.Count; k++)
            {
                int end = k + 1 < starts.Count ? starts[k + 1] - 3 : b.Length;
                while (k + 1 < starts.Count && end > starts[k] && b[end - 1] == 0) end--;
                r.Add((starts[k], end - starts[k]));
            }
            return r;
        }

        private static byte[] Rbsp(ReadOnlySpan<byte> nal, int max)
        {
            var r = new List<byte>();
            int zeros = 0;
            for (int i = 0; i < nal.Length && r.Count < max; i++)
            {
                if (zeros >= 2 && nal[i] == 3) { zeros = 0; continue; }
                zeros = nal[i] == 0 ? zeros + 1 : 0;
                r.Add(nal[i]);
            }
            return r.ToArray();
        }
    }
}
