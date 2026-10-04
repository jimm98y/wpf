// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// Brush spans (DpOutputSpan and its kinds): each writes a span of premultiplied ARGB into the scan
// buffer, which the scan then blends out. The antialiaser scales what they wrote by coverage.
//
//   DpOutputSolidColorSpan::OutputSpan @180027150   the brush colour, premultiplied by
//                                                   GpColor::ConvertToPremultiplied @1801aaf50
//

using System.Drawing.Drawing2D;

namespace System.Drawing.WebGpuBackend.Gdip
{
    internal abstract class GpSpan : IAaTarget
    {
        protected readonly GpScan Scan;
        uint[] _buf;
        protected GpSpan (GpScan scan) { Scan = scan; }
        public uint[] Buffer => _buf;
        public void OutputSpan (int y, int left, int right)
        {
            int n = right - left;
            if (n <= 0) return;
            _buf = Scan.Next (left, y, n);
            Fill (_buf, y, left, n);
        }
        /// <summary>Writes the brush's premultiplied colours for pixels x .. x + n - 1 of row y.</summary>
        protected abstract void Fill (uint[] buf, int y, int x, int n);
        /// <summary>The brush's colours for a span without asking the scan for its buffer (the text
        /// outputs scale or blend them themselves).</summary>
        public void Colors (int y, int x, int n, uint[] buf) => Fill (buf, y, x, n);
    }

    internal sealed class SolidSpan : GpSpan
    {
        readonly uint _c;
        public SolidSpan (GpScan scan, uint argb) : base (scan) { _c = Premultiply (argb); }
        protected override void Fill (uint[] buf, int y, int x, int n)
        {
            for (int i = 0; i < n; i++) buf [i] = _c;
        }

        /// <summary>GpColor::ConvertToPremultiplied.</summary>
        public static uint Premultiply (uint c)
        {
            uint a = c >> 24;
            if (a == 255) return c;
            if (a == 0) return 0;
            uint r = ((c >> 16) & 0xff) * a + 0x80, g = ((c >> 8) & 0xff) * a + 0x80, b = (c & 0xff) * a + 0x80;
            return a << 24 | ((r + (r >> 8)) >> 8 & 0xff) << 16 | ((g + (g >> 8)) >> 8 & 0xff) << 8 | ((b + (b >> 8)) >> 8 & 0xff);
        }
    }

    /// <summary>A span whose colour comes from a per-pixel function (gradients, textures, hatches) --
    /// the function returns premultiplied ARGB for device pixel (x, y).</summary>
    internal sealed class PixelSpan : GpSpan
    {
        readonly Func<int, int, uint> _px;
        public PixelSpan (GpScan scan, Func<int, int, uint> px) : base (scan) { _px = px; }
        protected override void Fill (uint[] buf, int y, int x, int n)
        {
            for (int i = 0; i < n; i++) buf [i] = _px (x + i, y);
        }
    }

    /// <summary>A span that writes a whole row at a time (image spans, gradients with incremental
    /// stepping): fills buf[0..n) for pixels x .. x + n - 1 of row y.</summary>
    internal sealed class RowSpan : GpSpan
    {
        readonly Action<uint[], int, int, int> _row;
        public RowSpan (GpScan scan, Action<uint[], int, int, int> row) : base (scan) { _row = row; }
        protected override void Fill (uint[] buf, int y, int x, int n) => _row (buf, y, x, n);
    }
}
