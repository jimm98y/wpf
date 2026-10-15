// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// GpBitmapScaler with InterpolationHintAveraging: the image sink CopyOnWriteBitmap::PipeLockBits puts
// between a large bitmap and the memory bitmap a shrinking DrawImage samples (InitImageBitmap @18003fe10
// with hint 3). Each premultiplied byte is box-averaged in 16.16, x first (ScaleLineAveraging @180102130)
// and then y (PushSrcLineAveraging @180101d30), each pass rounding to a byte. The step is the source over
// the destination size and the normaliser its inverse, both truncated to 16.16 (InitScalerState
// @180102c78); a destination band that starts inside a source row begins with that row's remaining
// weight and throws the row it completes away.
//

using System;
using System.Drawing.Imaging;

namespace System.Drawing.WebGpuBackend.Gdip
{
    internal static class GpBitmapScaler
    {
        [ThreadStatic] static byte[] s_row;
        /// <summary>Rows <paramref name="top"/>..<paramref name="bottom"/> of <paramref name="src"/>
        /// (32bpp PARGB) averaged down to <paramref name="dstW"/> x <paramref name="dstH"/>, as packed
        /// PARGB pixels.</summary>
        public static uint[] Averaging (GdipFrame src, int dstW, int dstH, int top, int bottom)
        {
            int n = Math.Max (0, bottom - top);
            var output = new uint [dstW * n];
            Averaging (src, dstW, dstH, top, bottom, false, output);
            return output;
        }

        /// <summary><see cref="Averaging(GdipFrame,int,int,int,int)"/> into <paramref name="output"/>
        /// (at least dstW x the band's rows, cleared). With <paramref name="premultiply"/> the source
        /// is 32bpp ARGB and each pixel is premultiplied as it is read -- what converting the whole
        /// bitmap to PARGB first gives, without the copy.</summary>
        public static void Averaging (GdipFrame src, int dstW, int dstH, int top, int bottom, bool premultiply, uint[] output)
        {
            int srcW = src.Width, srcH = src.Height;
            int n = Math.Max (0, bottom - top);
            if (n == 0 || dstW <= 0 || dstH <= 0) return;
            uint stepX = (uint) (int) (srcW * 65536.0 / dstW), invX = (uint) (int) (dstW * 65536.0 / srcW);
            int stepY = (int) (srcH * 65536.0 / dstH);
            uint invY = (uint) (int) (dstH * 65536.0 / srcH);
            var line = new byte [dstW * 4];
            byte[] bits = src.Bits;
            int stride = src.Stride;

            if (srcH == dstH) {
                // InitScalerState: equal heights take the nearest-neighbour (copy) row path.
                for (int y = top; y < bottom; y++) {
                    ScaleLine (bits, y * stride, srcW, dstW, stepX, invX, line, premultiply);
                    Pack (line, output, (y - top) * dstW, dstW);
                }
                return;
            }

            var acc = new uint [dstW * 4];
            double ratio = srcH / (double) dstH;
            int srcRow = (int) (top * ratio);
            float frac = (float) (top * ratio - srcRow);
            int row = top, rem;
            bool discard = false;
            if (frac <= 0f) rem = stepY;
            else {
                // The first source row is shared with the row above the band: finish that one
                // (into scratch) with the part of the row that belongs to it.
                row = top - 1;
                rem = (int) (frac * 65536f + 0.5f);
                discard = true;
            }
            int lastSrc = Math.Min (srcH, (int) -MathF.Floor (-(bottom * (float) (srcH / (float) dstH))));
            int count = dstW * 4;
            unsafe {
                fixed (byte* pl = line)
                fixed (uint* pa = acc) {
                    for (int sy = srcRow; sy < lastSrc && row < bottom; sy++) {
                        ScaleLine (bits, sy * stride, srcW, dstW, stepX, invX, line, premultiply);
                        if (rem <= 0x10000) {
                            uint r = (uint) rem, keep = 0x10000 - (uint) rem;
                            for (int i = 0; i < count; i++) {
                                uint v = pl [i];
                                uint t = pa [i] + v * r;
                                pa [i] = v * keep;
                                pl [i] = Round (t, invY);
                            }
                            if (!discard) Pack (line, output, (row - top) * dstW, dstW);
                            discard = false;
                            row++;
                            rem += stepY - 0x10000;
                        } else {
                            for (int i = 0; i < count; i++) pa [i] += (uint) pl [i] << 16;
                            rem -= 0x10000;
                        }
                    }
                }
            }
        }

        static byte Round (uint sum, uint inv) => (byte) ((uint) ((int) ((ulong) sum * inv >> 16) + 0x8000) >> 16);

        /// <summary>ScaleLineAveraging (or CopyLine for equal widths) of one PARGB row.</summary>
        static unsafe void ScaleLine (byte[] bits, int o, int srcW, int dstW, uint step, uint inv, byte[] dst, bool premultiply)
        {
            if (premultiply) {
                // Premultiply the row into scratch first (bits + o -> words), then scale those.
                if (s_row == null || s_row.Length < srcW * 4) s_row = new byte [srcW * 4];
                var src = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, uint> (bits.AsSpan (o, srcW * 4));
                var to = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, uint> (s_row.AsSpan (0, srcW * 4));
                for (int x = 0; x < srcW; x++) {
                    uint v = src [x];
                    to [x] = v >= 0xff000000u ? v : GdipPixels.PremultiplyArgb (v);
                }
                bits = s_row; o = 0;
            }
            if (srcW == dstW) { Buffer.BlockCopy (bits, o, dst, 0, dstW * 4); return; }
            uint a0 = 0, a1 = 0, a2 = 0, a3 = 0;
            uint rem = step;
            int end = dstW * 4;
            fixed (byte* pb = bits)
            fixed (byte* pd = dst) {
                byte* s = pb + o;
                int d = 0;
                for (int x = 0; x < srcW; x++, s += 4) {
                    uint v0 = s [0], v1 = s [1], v2 = s [2], v3 = s [3];
                    if (rem <= 0x10000) {
                        uint keep = 0x10000 - rem;
                        pd [d] = Round (a0 + v0 * rem, inv);
                        pd [d + 1] = Round (a1 + v1 * rem, inv);
                        pd [d + 2] = Round (a2 + v2 * rem, inv);
                        pd [d + 3] = Round (a3 + v3 * rem, inv);
                        a0 = v0 * keep; a1 = v1 * keep; a2 = v2 * keep; a3 = v3 * keep;
                        d += 4;
                        if (d == end) return;
                        rem += step;
                    } else {
                        a0 += v0 << 16; a1 += v1 << 16; a2 += v2 << 16; a3 += v3 << 16;
                    }
                    rem -= 0x10000;
                }
            }
        }

        static void Pack (byte[] line, uint[] output, int at, int w)
        {
            for (int x = 0, o = 0; x < w; x++, o += 4)
                output [at + x] = (uint) (line [o] | line [o + 1] << 8 | line [o + 2] << 16 | line [o + 3] << 24);
        }
    }
}
