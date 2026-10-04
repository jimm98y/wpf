// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Drawing.WebGpuBackend.Gdip;

namespace System.Drawing.Drawing2D
{
    // A managed GpCustomLineCap (WebGpuBackend.Gdip.GpCustomLineCap): the fill and stroke paths,
    // base cap and inset, stroke caps and join, width scale, as gdiplus.dll keeps them.
    public partial class CustomLineCap : MarshalByRefObject, ICloneable, IDisposable
    {
        internal GpCustomLineCap gp;

        internal CustomLineCap(GpCustomLineCap cap) { gp = cap; }

        public CustomLineCap(GraphicsPath fillPath, GraphicsPath strokePath) : this(fillPath, strokePath, LineCap.Flat) { }

        public CustomLineCap(GraphicsPath fillPath, GraphicsPath strokePath, LineCap baseCap) : this(fillPath, strokePath, baseCap, 0) { }

        // GdipCreateCustomLineCap @1800557a0: at least one path; each path validated
        // (GpCustomLineCap::SetFillPath/SetStrokePath), which fails when it does not cross the
        // cap's negative y axis.
        public CustomLineCap(GraphicsPath fillPath, GraphicsPath strokePath, LineCap baseCap, float baseInset)
        {
            if (fillPath == null && strokePath == null)
                throw new ArgumentException("Parameter is not valid.");
            int status = GpCustomLineCap.Create(fillPath?.gp, strokePath?.gp, baseCap, baseInset, out gp);
            if (status != 0)
                throw SafeNativeMethods.Gdip.StatusException(status);
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
        }

        ~CustomLineCap() => Dispose(false);

        public object Clone() => CoreClone();

        internal virtual object CoreClone() => new CustomLineCap(gp.Clone());

        public void SetStrokeCaps(LineCap startCap, LineCap endCap)
        {
            gp.StrokeStartCap = startCap;
            gp.StrokeEndCap = endCap;
        }

        public void GetStrokeCaps(out LineCap startCap, out LineCap endCap)
        {
            startCap = gp.StrokeStartCap;
            endCap = gp.StrokeEndCap;
        }

        public LineJoin StrokeJoin
        {
            get => gp.StrokeJoin;
            set => gp.StrokeJoin = value;
        }

        public LineCap BaseCap
        {
            get => gp.BaseCap;
            set => gp.BaseCap = value;
        }

        public float BaseInset
        {
            get => gp.BaseInset;
            set => gp.BaseInset = value;
        }

        public float WidthScale
        {
            get => gp.WidthScale;
            set => gp.WidthScale = value;
        }
    }
}
