// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

//
// A TextureBrush with no GDI+ brush behind it: the tile (the image, cut to the destination
// rectangle and recoloured by any ImageAttributes when the brush is made, as GDI+ does), the wrap
// mode and the brush transform, held here. Graphics records a fill with it as a tiled image
// (Graphics.TryTexture).
//

using System.ComponentModel;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace System.Drawing
{
    public sealed class TextureBrush : Brush
    {
        private Bitmap _tile;
        private WrapMode _wrapMode;
        private float[] _transform = { 1f, 0f, 0f, 1f, 0f, 0f };

        public TextureBrush(Image bitmap) : this(bitmap, WrapMode.Tile)
        {
        }

        public TextureBrush(Image image, WrapMode wrapMode)
        {
            if (image == null)
            {
                throw new ArgumentNullException(nameof(image));
            }
            CheckWrap(wrapMode);
            Init(image, wrapMode, null, null);
        }

        public TextureBrush(Image image, WrapMode wrapMode, RectangleF dstRect)
        {
            if (image == null)
            {
                throw new ArgumentNullException(nameof(image));
            }
            CheckWrap(wrapMode);
            Init(image, wrapMode, dstRect, null);
        }

        public TextureBrush(Image image, WrapMode wrapMode, Rectangle dstRect)
            : this(image, wrapMode, (RectangleF)dstRect)
        {
        }

        public TextureBrush(Image image, RectangleF dstRect) : this(image, dstRect, null) { }

        public TextureBrush(Image image, RectangleF dstRect, ImageAttributes imageAttr)
        {
            if (image == null)
            {
                throw new ArgumentNullException(nameof(image));
            }
            Init(image, WrapMode.Tile, dstRect, imageAttr);
        }

        public TextureBrush(Image image, Rectangle dstRect) : this(image, dstRect, null) { }

        public TextureBrush(Image image, Rectangle dstRect, ImageAttributes imageAttr)
            : this(image, (RectangleF)dstRect, imageAttr)
        {
        }

        private TextureBrush()
        {
        }

        private static void CheckWrap(WrapMode wrapMode)
        {
            if (wrapMode < WrapMode.Tile || wrapMode > WrapMode.Clamp)
            {
                throw new InvalidEnumArgumentException(nameof(wrapMode), unchecked((int)wrapMode), typeof(WrapMode));
            }
        }

        private void Init(Image image, WrapMode wrapMode, RectangleF? dstRect, ImageAttributes imageAttr)
        {
            _wrapMode = wrapMode;
            var whole = new Rectangle(0, 0, image.Width, image.Height);
            Rectangle r = whole;
            if (dstRect is RectangleF d)
            {
                r = Rectangle.Intersect(whole, Rectangle.Truncate(d));
                if (r.Width <= 0 || r.Height <= 0)
                {
                    throw new OutOfMemoryException();
                }
            }
            if (!(image is Bitmap bmp))
            {
                _tile = new Bitmap(Math.Max(1, r.Width), Math.Max(1, r.Height));
                return;
            }
            _tile = bmp.Clone(r, PixelFormat.Format32bppArgb);
            if (imageAttr != null)
            {
                GdipFrame f = _tile.Data.Frame;
                byte[] rgba = GdipPixels.ToRgba(f, new Rectangle(0, 0, f.Width, f.Height));
                imageAttr.Apply(rgba, ColorAdjustType.Brush);
                var row = new uint[f.Width];
                for (int y = 0; y < f.Height; y++)
                {
                    for (int x = 0; x < f.Width; x++)
                    {
                        int o = (y * f.Width + x) * 4;
                        row[x] = (uint)rgba[o + 3] << 24 | (uint)rgba[o] << 16 | (uint)rgba[o + 1] << 8 | rgba[o + 2];
                    }
                    GdipPixels.WriteArgb(f, 0, y, f.Width, row, 0);
                }
            }
        }

        /// <summary>The tile as straight RGBA, and the brush transform (m11 m12 m21 m22 dx dy).</summary>
        internal byte[] TileRgba(out int width, out int height)
        {
            GdipFrame f = _tile.Data.Frame;
            width = f.Width;
            height = f.Height;
            return GdipPixels.ToRgba(f, new Rectangle(0, 0, width, height));
        }

        internal float[] TransformElements => _transform;

        public override object Clone()
        {
            return new TextureBrush
            {
                _tile = (Bitmap)_tile.Clone(),
                _wrapMode = _wrapMode,
                _transform = (float[])_transform.Clone(),
            };
        }

        public Matrix Transform
        {
            get
            {
                return new Matrix(_transform[0], _transform[1], _transform[2], _transform[3], _transform[4], _transform[5]);
            }
            set
            {
                if (value == null)
                {
                    throw new ArgumentNullException(nameof(value));
                }
                _transform = value.Elements;
            }
        }

        public WrapMode WrapMode
        {
            get
            {
                return _wrapMode;
            }
            set
            {
                CheckWrap(value);
                _wrapMode = value;
            }
        }

        public Image Image
        {
            get
            {
                return (Image)_tile.Clone();
            }
        }

        public void ResetTransform()
        {
            _transform = new float[] { 1f, 0f, 0f, 1f, 0f, 0f };
        }

        public void MultiplyTransform(Matrix matrix) => MultiplyTransform(matrix, MatrixOrder.Prepend);

        public void MultiplyTransform(Matrix matrix, MatrixOrder order)
        {
            if (matrix == null)
            {
                throw new ArgumentNullException(nameof(matrix));
            }
            Combine(matrix.Elements, order);
        }

        public void TranslateTransform(float dx, float dy) => TranslateTransform(dx, dy, MatrixOrder.Prepend);

        public void TranslateTransform(float dx, float dy, MatrixOrder order)
            => Combine(new float[] { 1, 0, 0, 1, dx, dy }, order);

        public void ScaleTransform(float sx, float sy) => ScaleTransform(sx, sy, MatrixOrder.Prepend);

        public void ScaleTransform(float sx, float sy, MatrixOrder order)
            => Combine(new float[] { sx, 0, 0, sy, 0, 0 }, order);

        public void RotateTransform(float angle) => RotateTransform(angle, MatrixOrder.Prepend);

        public void RotateTransform(float angle, MatrixOrder order)
        {
            double a = angle * Math.PI / 180.0;
            float cos = (float)Math.Cos(a), sin = (float)Math.Sin(a);
            Combine(new float[] { cos, sin, -sin, cos, 0, 0 }, order);
        }

        private void Combine(float[] m, MatrixOrder order)
        {
            if (order == MatrixOrder.Prepend) Matrix.Mul(m, _transform, _transform);
            else Matrix.Mul(_transform, m, _transform);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _tile?.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
