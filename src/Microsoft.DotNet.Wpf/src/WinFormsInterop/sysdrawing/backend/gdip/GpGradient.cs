// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// What GDI+'s gradient brushes share (gdiplus.dll 10.0.26100, arm64, public PDB):
//
//   GpGradientBrush::GetLinearBlendArray @1801aca78   SetBlendTriangularShape's blend
//   GpGradientBrush::GetSigmaBlendArray  @1801acb70   SetSigmaBellShape's blend, from the 128-entry
//                                                     half-bell table at 1802ab5e0 (in 1/65536ths)
//   LinearGradientRectFromPoints         @18006f2d8   a point-built line brush's rectangle
//   IsClosePointF                        @18006f0a0
//

using System.Drawing;

namespace System.Drawing.WebGpuBackend.Gdip
{
    internal static class GpGradient
    {
        public const float Eps = 1.1920928955078125e-07f;

        // The rising half of the bell, 0 .. 32553 of 65536 (table at 1802ab5e0).
        static readonly uint[] s_bell = {
            0x0U, 0x426c0000U, 0x42f00000U, 0x43360000U, 0x43770000U, 0x439d0000U, 0x43bf8000U, 0x43e30000U,
            0x4403c000U, 0x44168000U, 0x442a0000U, 0x443dc000U, 0x44524000U, 0x44678000U, 0x447d4000U, 0x4489c000U,
            0x44954000U, 0x44a10000U, 0x44ad2000U, 0x44b9a000U, 0x44c66000U, 0x44d38000U, 0x44e10000U, 0x44eee000U,
            0x44fd0000U, 0x4505d000U, 0x450d4000U, 0x4514f000U, 0x451cc000U, 0x4524d000U, 0x452d1000U, 0x45358000U,
            0x453e2000U, 0x4546f000U, 0x45500000U, 0x45595000U, 0x4562c000U, 0x456c7000U, 0x45766000U, 0x45804000U,
            0x45857000U, 0x458ab800U, 0x45902000U, 0x4595a800U, 0x459b4800U, 0x45a10800U, 0x45a6e800U, 0x45ace800U,
            0x45b30000U, 0x45b94000U, 0x45bf9800U, 0x45c61000U, 0x45cca800U, 0x45d36000U, 0x45da3800U, 0x45e13000U,
            0x45e85000U, 0x45ef8800U, 0x45f6e000U, 0x45fe5800U, 0x4602f800U, 0x4606d800U, 0x460ac400U, 0x460ec000U,
            0x4612d000U, 0x4616f000U, 0x461b1c00U, 0x461f5c00U, 0x4623ac00U, 0x46280c00U, 0x462c7c00U, 0x4630fc00U,
            0x46358c00U, 0x463a3000U, 0x463ee000U, 0x4643a000U, 0x46487000U, 0x464d5000U, 0x46524000U, 0x46574000U,
            0x465c4c00U, 0x46616c00U, 0x46669800U, 0x466bd400U, 0x46711c00U, 0x46767800U, 0x467bdc00U, 0x4680aa00U,
            0x46836c00U, 0x46863400U, 0x46890400U, 0x468bda00U, 0x468eb600U, 0x46919a00U, 0x46948200U, 0x46977200U,
            0x469a6800U, 0x469d6400U, 0x46a06400U, 0x46a36c00U, 0x46a67800U, 0x46a98a00U, 0x46aca000U, 0x46afbc00U,
            0x46b2dc00U, 0x46b60200U, 0x46b92c00U, 0x46bc5a00U, 0x46bf8c00U, 0x46c2c200U, 0x46c5fc00U, 0x46c93800U,
            0x46cc7a00U, 0x46cfbe00U, 0x46d30400U, 0x46d64e00U, 0x46d99a00U, 0x46dce800U, 0x46e03a00U, 0x46e38c00U,
            0x46e6e000U, 0x46ea3600U, 0x46ed8e00U, 0x46f0e800U, 0x46f44000U, 0x46f79c00U, 0x46faf600U, 0x46fe5200U,
        };

        static float T (int i) => BitConverter.Int32BitsToSingle (unchecked ((int) s_bell [i]));

        /// <summary>GetLinearBlendArray: a triangle peaking at <paramref name="focus"/>; false (InvalidParameter)
        /// when either argument is outside [0, 1].</summary>
        public static bool LinearBlendArray (float focus, float scale, out float[] factors, out float[] positions)
        {
            factors = positions = null;
            if (focus < 0f || 1f < focus || scale < 0f || 1f < scale) return false;
            if (focus <= 0f || 1f <= focus) {
                bool one = focus == 1f;
                factors = one ? new[] { 0f, scale } : new[] { scale, 0f };
                positions = new[] { 0f, 1f };
            } else {
                factors = new[] { 0f, scale, 0f };
                positions = new[] { 0f, focus, 1f };
            }
            return true;
        }

        /// <summary>GetSigmaBlendArray: the bell, 256 samples when the focus is an end and 511 when it is
        /// inside, every factor scaled by scale/65536.</summary>
        public static bool SigmaBlendArray (float focus, float scale, out float[] factors, out float[] positions)
        {
            factors = positions = null;
            if (focus < 0f || 1f < focus || scale < 0f || 1f < scale) return false;
            const float Full = 65536f, Steps = 255f;
            float k = scale * 1.52587890625e-05f;
            int n;
            float[] f = new float [511], p = new float [511];
            if (focus <= 0f || 1f <= focus) {
                if (focus == 1f) {
                    for (int i = 0; i < 128; i++) { f [i] = T (i); p [i] = (float) i / Steps; }
                    for (int i = 128; i < 256; i++) { f [i] = Full - T (255 - i); p [i] = (float) i / Steps; }
                } else {
                    for (int i = 0; i < 128; i++) { f [i] = Full - T (i); p [i] = (float) i / Steps; }
                    for (int i = 128; i < 256; i++) { f [i] = T (255 - i); p [i] = (float) i / Steps; }
                }
                n = 256;
            } else {
                for (int i = 0; i < 128; i++) { f [i] = T (i); p [i] = ((float) i * focus) / Steps; }
                for (int i = 128; i < 256; i++) { f [i] = Full - T (255 - i); p [i] = ((float) i * focus) / Steps; }
                double fd = focus;
                for (int j = 1; j < 128; j++) { f [255 + j] = Full - T (j); p [255 + j] = (float) (((double) j * (1.0 - fd)) / 255.0 + fd); }
                for (int j = 128; j < 256; j++) { f [255 + j] = T (255 - j); p [255 + j] = (float) (((double) j * (1.0 - fd)) / 255.0 + fd); }
                n = 511;
            }
            factors = new float [n]; positions = new float [n];
            for (int i = 0; i < n; i++) { factors [i] = k * f [i]; positions [i] = p [i]; }
            return true;
        }

        public static bool IsClose (PointF a, PointF b)
        {
            float d = b.X == 0f ? 1f : b.X;
            if (!(MathF.Abs ((a.X - b.X) / d) < 1.1920928955078125e-06f)) return false;
            d = b.Y == 0f ? 1f : b.Y;
            return MathF.Abs ((a.Y - b.Y) / d) < 1.1920928955078125e-06f;
        }

        /// <summary>LinearGradientRectFromPoints: the points' box, made square about a horizontal or
        /// vertical line; false when the points coincide.</summary>
        public static bool RectFromPoints (PointF p1, PointF p2, out RectangleF r)
        {
            r = default;
            if (IsClose (p1, p2)) return false;
            const float e = 1.1920928955078125e-06f;
            float x = p1.X < p2.X ? p1.X : p2.X;
            float y = p1.Y < p2.Y ? p1.Y : p2.Y;
            float w = MathF.Abs (p1.X - p2.X), h = MathF.Abs (p1.Y - p2.Y);
            float d = p2.X == 0f ? 1f : p2.X;
            if (MathF.Abs ((p1.X - p2.X) / d) < e) { w = h; x = x - h * 0.5f; }
            d = p2.Y == 0f ? 1f : p2.Y;
            if (MathF.Abs ((p1.Y - p2.Y) / d) < e) { h = w; y = y - w * 0.5f; }
            r = new RectangleF (x, y, w, h);
            return true;
        }

        /// <summary>The angle a point-built line brush is made with: atan2 in degrees, in double, stored
        /// as float.</summary>
        public static float PointsAngle (PointF p1, PointF p2)
            => (float) ((Math.Atan2 ((double) (p2.Y - p1.Y), (double) (p2.X - p1.X)) * 180.0) / 3.141592653589793);
    }
}
