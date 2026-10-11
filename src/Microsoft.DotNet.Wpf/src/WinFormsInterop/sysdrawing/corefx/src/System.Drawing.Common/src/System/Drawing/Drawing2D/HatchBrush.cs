// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

namespace System.Drawing.Drawing2D
{
    public sealed class HatchBrush : Brush
    {
        private readonly HatchStyle _style;
        private readonly Color _foreColor, _backColor;

        public HatchBrush(HatchStyle hatchstyle, Color foreColor) : this(hatchstyle, foreColor, Color.FromArgb(unchecked((int)0xff000000)))
        {
        }

        public HatchBrush(HatchStyle hatchstyle, Color foreColor, Color backColor)
        {
            if (hatchstyle < HatchStyle.Min || hatchstyle > HatchStyle.SolidDiamond)
            {
                throw new ArgumentException(SR.Format(SR.InvalidEnumArgument, nameof(hatchstyle), hatchstyle, nameof(HatchStyle)), nameof(hatchstyle));
            }

            _style = hatchstyle; _foreColor = foreColor; _backColor = backColor;
        }

        public override object Clone() => new HatchBrush(_style, _foreColor, _backColor);

        public HatchStyle HatchStyle => _style;

        public Color ForegroundColor => _foreColor;

        public Color BackgroundColor => _backColor;
    }
}
