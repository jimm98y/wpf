// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

namespace System.Drawing
{
    public sealed class SolidBrush : Brush
    {
        private Color _color = Color.Empty;
        private bool _immutable;

        public SolidBrush(Color color)
        {
            _color = color;
        }

        internal SolidBrush(Color color, bool immutable) : this(color)
        {
            _immutable = immutable;
        }

        public override object Clone() => new SolidBrush(_color);

        protected override void Dispose(bool disposing)
        {
            if (!disposing)
            {
                _immutable = false;
            }
            else if (_immutable)
            {
                throw new ArgumentException(SR.Format(SR.CantChangeImmutableObjects, "Brush"));
            }

            base.Dispose(disposing);
        }

        public Color Color
        {
            get => _color;
            set
            {
                if (_immutable)
                {
                    throw new ArgumentException(SR.Format(SR.CantChangeImmutableObjects, "Brush"));
                }
                _color = value;
            }
        }
    }
}
