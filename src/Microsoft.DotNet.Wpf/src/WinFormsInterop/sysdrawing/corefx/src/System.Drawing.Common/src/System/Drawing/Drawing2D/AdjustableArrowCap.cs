// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Drawing.WebGpuBackend.Gdip;

namespace System.Drawing.Drawing2D
{
    public sealed partial class AdjustableArrowCap : CustomLineCap
    {
        internal AdjustableArrowCap(GpCustomLineCap cap) : base(cap) { }

        public AdjustableArrowCap(float width, float height) : this(width, height, true) { }

        // GdipCreateAdjustableArrowCap @180054b70 + GpAdjustableArrowCap::Update @18007c3f8.
        public AdjustableArrowCap(float width, float height, bool isFilled)
            : base(GpCustomLineCap.CreateArrow(width, height, isFilled))
        {
        }

        public float Height
        {
            get => gp.ArrowHeight;
            set { gp.ArrowHeight = value; gp.UpdateArrow(); }
        }

        public float Width
        {
            get => gp.ArrowWidth;
            set { gp.ArrowWidth = value; gp.UpdateArrow(); }
        }

        public float MiddleInset
        {
            get => gp.ArrowMiddleInset;
            set { gp.ArrowMiddleInset = value; gp.UpdateArrow(); }
        }

        public bool Filled
        {
            get => gp.ArrowFilled;
            set { gp.ArrowFilled = value; gp.UpdateArrow(); }
        }

        internal override object CoreClone() => new AdjustableArrowCap(gp.Clone());
    }
}
