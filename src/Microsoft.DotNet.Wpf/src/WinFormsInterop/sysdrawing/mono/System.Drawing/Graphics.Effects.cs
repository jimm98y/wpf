// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// What .NET 8 and 9 added to drawing with images: an effect worked as an image is drawn (GdipDrawImageFX), a
// cached bitmap drawn at a device position (GdipDrawCachedBitmap), a bitmap's own pixels put through an effect
// (GdipBitmapApplyEffect) -- and the context .NET keeps beside GDI+'s state stack, which GetContextInfo reads:
// each Save and BeginContainer remembers the translation and clip it was made under, so the offset and clip of
// a Graphics nested in containers come out as the ones the device sees.

using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Imaging.Effects;
using System.Numerics;

namespace System.Drawing
{
	public sealed partial class Graphics
	{
		// ---- DrawImage with an effect -----------------------------------------------------------------

		public void DrawImage (Image image, Effect effect) =>
			DrawImage (image, effect, srcRect: default, transform: default, GraphicsUnit.Pixel, imageAttr: null);

		/// <summary>The image's portion, through the effect, into the parallelogram the transform makes of the
		/// source rectangle (the source rectangle itself, in world units, with none).</summary>
		public void DrawImage (Image image, Effect effect, RectangleF srcRect = default, Matrix transform = default,
			GraphicsUnit srcUnit = GraphicsUnit.Pixel, ImageAttributes imageAttr = default)
		{
			if (image == null)
				GDIPlus.CheckStatus (Status.InvalidParameter);
			effect?.CheckLive ();
			CheckUse ();
			RectangleF src = srcRect.IsEmpty ? new RectangleF (0, 0, image.Width, image.Height) : srcRect;
			// the portion in the image's pixels
			float k = PixelsPerUnit (srcUnit, image);
			Rectangle pixels = Rectangle.Intersect (new Rectangle (0, 0, image.Width, image.Height),
				Rectangle.Round (new RectangleF (src.X * k, src.Y * k, src.Width * k, src.Height * k)));
			if (pixels.Width <= 0 || pixels.Height <= 0)
				return;
			int margin = effect?.Margin ?? 0;
			using var work = new Bitmap (pixels.Width + 2 * margin, pixels.Height + 2 * margin, PixelFormat.Format32bppArgb);
			using (Graphics g = FromImage (work)) {
				g.CompositingMode = CompositingMode.SourceCopy;
				g.DrawImage (image, new Rectangle (margin, margin, pixels.Width, pixels.Height), pixels.X, pixels.Y, pixels.Width, pixels.Height, GraphicsUnit.Pixel);
			}
			if (effect != null)
				work.Apply (effect, new Rectangle (0, 0, work.Width, work.Height));
			// what the effect reached past the image, in the source rectangle's units
			float grow = margin / k;
			var dest = new RectangleF (src.X - grow, src.Y - grow, src.Width + 2 * grow, src.Height + 2 * grow);
			PointF [] corners = { dest.Location, new PointF (dest.Right, dest.Top), new PointF (dest.Left, dest.Bottom) };
			transform?.TransformPoints (corners);
			DrawImage (work, corners, new RectangleF (0, 0, work.Width, work.Height), GraphicsUnit.Pixel, imageAttr, (DrawImageAbort) null);
		}

		static float PixelsPerUnit (GraphicsUnit unit, Image image)
		{
			float dpi = image.HorizontalResolution;
			return unit switch {
				GraphicsUnit.Point => dpi / 72f,
				GraphicsUnit.Inch => dpi,
				GraphicsUnit.Document => dpi / 300f,
				GraphicsUnit.Millimeter => dpi / 25.4f,
				GraphicsUnit.Display => dpi / 96f,
				_ => 1f,
			};
		}

		// ---- the cached bitmap ------------------------------------------------------------------------

		/// <summary>The cached bitmap at (x, y) in world units, which must reach the device by a translation
		/// alone (GDI+: WrongState otherwise), pixel for pixel.</summary>
		public void DrawCachedBitmap (CachedBitmap cachedBitmap, int x, int y)
		{
			ArgumentNullException.ThrowIfNull (cachedBitmap);
			Bitmap bits = cachedBitmap.Bits;
			if (bits == null)
				GDIPlus.CheckStatus (Status.InvalidParameter);
			CheckUse ();
			PointF [] p = { new PointF (x, y), new PointF (x + 1, y), new PointF (x, y + 1) };
			TransformPoints (CoordinateSpace.Device, CoordinateSpace.World, p);
			const float e = 1e-4f;
			if (Math.Abs (p [1].X - p [0].X - 1) > e || Math.Abs (p [1].Y - p [0].Y) > e
				|| Math.Abs (p [2].X - p [0].X) > e || Math.Abs (p [2].Y - p [0].Y - 1) > e)
				GDIPlus.CheckStatus (Status.WrongState);
			GraphicsState state = Save ();
			try {
				ResetTransform ();
				PageUnit = GraphicsUnit.Pixel;
				PageScale = 1;
				var at = new Point ((int) MathF.Round (p [0].X), (int) MathF.Round (p [0].Y));
				DrawImage (bits, new Rectangle (at, bits.Size), 0, 0, bits.Width, bits.Height, GraphicsUnit.Pixel);
			} finally {
				Restore (state);
			}
		}

		// ---- GetContextInfo -----------------------------------------------------------------------------

		/// <summary>What .NET remembers of a Save or BeginContainer (GraphicsContext): the translation and the
		/// clip it was made under.</summary>
		sealed class SavedContext
		{
			internal int State;
			internal Vector2 TransformOffset;
			internal Region Clip;
			internal bool IsCumulative;
			internal SavedContext Next, Previous;

			internal void Dispose ()
			{
				Next?.Dispose ();
				Next = null;
				Clip?.Dispose ();
				Clip = null;
			}
		}

		SavedContext previousContext;

		SavedContext NewContext () => new SavedContext { TransformOffset = TransformElements.Translation, Clip = RegionIfNotInfinite () };

		void PushContext (SavedContext context, int state, bool cumulative)
		{
			context.State = state;
			context.IsCumulative = cumulative;
			if (previousContext != null) {
				context.Previous = previousContext;
				previousContext.Next = context;
			}
			previousContext = context;
		}

		void PopContext (int state)
		{
			for (SavedContext c = previousContext; c != null; c = c.Previous) {
				if (c.State == state) {
					previousContext = c.Previous;
					c.Dispose ();
					return;
				}
			}
		}

		Region RegionIfNotInfinite ()
		{
			Region clip = Clip;
			if (clip.IsInfinite (this)) {
				clip.Dispose ();
				return null;
			}
			return clip;
		}

		public void GetContextInfo (out PointF offset)
		{
			ContextInfo (out Matrix3x2 cumulative, calculateClip: false, out _);
			offset = new PointF (cumulative.Translation.X, cumulative.Translation.Y);
		}

		[System.ComponentModel.EditorBrowsable (System.ComponentModel.EditorBrowsableState.Never)]
		public void GetContextInfo (out PointF offset, out Region clip)
		{
			ContextInfo (out Matrix3x2 cumulative, calculateClip: true, out clip);
			offset = new PointF (cumulative.Translation.X, cumulative.Translation.Y);
		}

		// .NET's walk up the contexts: each one's translation added in, the clip moved by the offset of the
		// context inside it and cut by its own; a run of Saves within one container counts once.
		void ContextInfo (out Matrix3x2 cumulativeTransform, bool calculateClip, out Region cumulativeClip)
		{
			cumulativeClip = calculateClip ? RegionIfNotInfinite () : null;
			cumulativeTransform = TransformElements;
			Vector2 currentOffset = default;
			SavedContext context = previousContext;
			if (!cumulativeTransform.IsIdentity)
				currentOffset = cumulativeTransform.Translation;
			while (context != null) {
				if (context.TransformOffset != Vector2.Zero)
					cumulativeTransform.Translation += context.TransformOffset;
				if (currentOffset != Vector2.Zero)
					cumulativeClip?.Translate (currentOffset.X, currentOffset.Y);
				if (calculateClip && context.Clip != null) {
					if (cumulativeClip == null)
						cumulativeClip = context.Clip.Clone ();
					else
						cumulativeClip.Intersect (context.Clip);
				}
				currentOffset = context.TransformOffset;
				do {
					context = context.Previous;
					if (context == null || !context.Next.IsCumulative)
						break;
				} while (context.IsCumulative);
			}
		}
	}

	public sealed partial class Bitmap
	{
		/// <summary>The effect worked on the bitmap's pixels, in the area (the whole bitmap for none).</summary>
		public void ApplyEffect (Effect effect, Rectangle area = default)
		{
			if (effect == null)
				GDIPlus.CheckStatus (Status.InvalidParameter);
			effect.CheckLive ();
			Rectangle bounds = new Rectangle (0, 0, Width, Height);
			Rectangle r = area.IsEmpty ? bounds : Rectangle.Intersect (area, bounds);
			if (r.Width <= 0 || r.Height <= 0)
				GDIPlus.CheckStatus (Status.InvalidParameter);
			Apply (effect, r);
		}

		internal void Apply (Effect effect, Rectangle r)
		{
			CheckUnlocked ();
			BitmapData data = LockBits (r, ImageLockMode.ReadWrite, PixelFormat.Format32bppArgb);
			try {
				var px = new uint [r.Width * r.Height];
				var row = new int [r.Width];
				for (int y = 0; y < r.Height; y++) {
					System.Runtime.InteropServices.Marshal.Copy (data.Scan0 + y * data.Stride, row, 0, r.Width);
					Buffer.BlockCopy (row, 0, px, y * r.Width * 4, r.Width * 4);
				}
				effect.Apply (px, r.Width, r.Height);
				for (int y = 0; y < r.Height; y++) {
					Buffer.BlockCopy (px, y * r.Width * 4, row, 0, r.Width * 4);
					System.Runtime.InteropServices.Marshal.Copy (row, 0, data.Scan0 + y * data.Stride, r.Width);
				}
			} finally {
				UnlockBits (data);
			}
		}
	}
}

namespace System.Drawing.Imaging
{
	/// <summary>A bitmap kept in the device's format (32-bit premultiplied), drawn fast and only at a position.</summary>
	public sealed class CachedBitmap : IDisposable
	{
		Bitmap bits;

		public CachedBitmap (Bitmap bitmap, Graphics graphics)
		{
			ArgumentNullException.ThrowIfNull (bitmap);
			ArgumentNullException.ThrowIfNull (graphics);
			bits = bitmap.Clone (new Rectangle (0, 0, bitmap.Width, bitmap.Height), PixelFormat.Format32bppPArgb);
		}

		internal Bitmap Bits => bits;

		public void Dispose ()
		{
			System.Threading.Interlocked.Exchange (ref bits, null)?.Dispose ();
			GC.SuppressFinalize (this);
		}
	}

	public sealed partial class Encoder
	{
		public static readonly Encoder ColorSpace = new Encoder (new Guid (unchecked ((int) 0xae7a62a0), unchecked ((short) 0xee2c), 0x49d8, new byte [] { 0x9d, 0x07, 0x1b, 0xa8, 0xa9, 0x27, 0x59, 0x6e }));

		public static readonly Encoder ImageItems = new Encoder (new Guid (0x63875e13, 0x1f1d, 0x45ab, new byte [] { 0x91, 0x95, 0xa2, 0x9b, 0x60, 0x66, 0xa6, 0x50 }));

		public static readonly Encoder SaveAsCmyk = new Encoder (new Guid (unchecked ((int) 0xa219bbc9), 0x0a9d, 0x4005, new byte [] { 0xa3, 0xee, 0x3a, 0x42, 0x1b, 0x8b, 0xb0, 0x6c }));
	}
}
