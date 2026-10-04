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
        private System.Drawing.WebGpuBackend.Gdip.GpMatrix _m = System.Drawing.WebGpuBackend.Gdip.GpMatrix.CreateIdentity();

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
            // GpTexture::GpTexture(image, rect, ia) takes the wrap mode of the ImageAttributes.
            _wrapMode = imageAttr != null ? imageAttr.WrapModeForDrawing : wrapMode;
            var whole = new Rectangle(0, 0, image.Width, image.Height);
            Rectangle r = whole;
            if (dstRect is RectangleF d)
            {
                // InitializeBrushBitmap: floor(v + 0.5) of each of x, y, width, height.
                r = new Rectangle((int)MathF.Floor(d.X + 0.5f), (int)MathF.Floor(d.Y + 0.5f),
                                  (int)MathF.Floor(d.Width + 0.5f), (int)MathF.Floor(d.Height + 0.5f));
                if (r.Width <= 0 || r.Height <= 0 || !whole.Contains(r))
                {
                    throw new OutOfMemoryException();
                }
            }
            if (!(image is Bitmap bmp))
            {
                _tile = new Bitmap(Math.Max(1, r.Width), Math.Max(1, r.Height));
                return;
            }
            var recolor = imageAttr?.Recolor;
            if (recolor != null && recolor.HasRecoloring((ColorAdjustType)6))
            {
                // GpBitmap::Recolor: a clone in ARGB, ColorAdjust'ed as a Bitmap.
                _tile = bmp.Clone(r, PixelFormat.Format32bppArgb);
                recolor.Flush();
                GdipFrame f = _tile.Data.Frame;
                var row = new uint[f.Width];
                for (int y = 0; y < f.Height; y++)
                {
                    GdipPixels.ReadArgb(f, 0, y, f.Width, row, 0);
                    recolor.ColorAdjust(row, 0, f.Width, ColorAdjustType.Bitmap);
                    GdipPixels.WriteArgb(f, 0, y, f.Width, row, 0);
                }
                return;
            }
            _tile = bmp.Clone(r, PixelFormat.Format32bppPArgb);
        }

        internal GdipFrame TileFrame => _tile?.Data.Frame;

        /// <summary>The tile as straight RGBA, and the brush transform (m11 m12 m21 m22 dx dy).</summary>
        internal byte[] TileRgba(out int width, out int height)
        {
            GdipFrame f = _tile.Data.Frame;
            width = f.Width;
            height = f.Height;
            return GdipPixels.ToRgba(f, new Rectangle(0, 0, width, height));
        }

        internal float[] TransformElements => new float[] { _m.M11, _m.M12, _m.M21, _m.M22, _m.Dx, _m.Dy };

        internal System.Drawing.WebGpuBackend.Gdip.GpMatrix Gp => _m;

        internal Bitmap Tile => _tile;

        public override object Clone()
        {
            return new TextureBrush
            {
                _tile = (Bitmap)_tile.Clone(),
                _wrapMode = _wrapMode,
                _m = _m,
            };
        }

        public Matrix Transform
        {
            get
            {
                return new Matrix(_m);
            }
            set
            {
                if (value == null)
                {
                    throw new ArgumentNullException(nameof(value));
                }
                _m = value.Gp;
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
            _m = System.Drawing.WebGpuBackend.Gdip.GpMatrix.CreateIdentity();
        }

        public void MultiplyTransform(Matrix matrix) => MultiplyTransform(matrix, MatrixOrder.Prepend);

        public void MultiplyTransform(Matrix matrix, MatrixOrder order)
        {
            if (matrix == null)
            {
                throw new ArgumentNullException(nameof(matrix));
            }
            _m.Multiply(matrix.Gp, order == MatrixOrder.Append);
        }

        public void TranslateTransform(float dx, float dy) => TranslateTransform(dx, dy, MatrixOrder.Prepend);

        public void TranslateTransform(float dx, float dy, MatrixOrder order)
            => _m.Translate(dx, dy, order == MatrixOrder.Append);

        public void ScaleTransform(float sx, float sy) => ScaleTransform(sx, sy, MatrixOrder.Prepend);

        public void ScaleTransform(float sx, float sy, MatrixOrder order)
            => _m.Scale(sx, sy, order == MatrixOrder.Append);

        public void RotateTransform(float angle) => RotateTransform(angle, MatrixOrder.Prepend);

        public void RotateTransform(float angle, MatrixOrder order)
            => _m.Rotate(angle, order == MatrixOrder.Append);

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
