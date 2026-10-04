// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// A Graphics and metafiles, both ways.
//
// RECORDING: Graphics.FromImage(metafile) is GpMetafile::GetGraphicsContext -- one Graphics per
// recording, whose every verb is handed to the metafile's GpMetafileRecorder with the arguments the
// caller gave it, in world units, as gdiplus.dll's GpGraphics hands them to its MetafileRecorder
// (each GpGraphics verb records first, then renders down-level through the metafile driver; the
// recorder does both). The Graphics keeps its own transform, page and state ids as for any other
// target, so what it answers (Transform, PageUnit, Save tokens) is what GDI+'s does; drawing goes
// only to the recorder. Disposing it ends the recording (GpGraphics::~GpGraphics ->
// GpMetafile::EndRecording).
//
// PLAYING: DrawImage(metafile, ...) on any Graphics is GpGraphics::DrawImage with a metafile --
// EnumerateMetafile with the default callback -- and EnumerateMetafile hands each record to the
// caller's: both are GpMetafilePlayer, which plays through this Graphics' public API.
//

using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.WebGpuBackend.Gdip;

namespace System.Drawing
{
	public sealed partial class Graphics
	{
		/// <summary>The recorder of the metafile this Graphics records into (FromImage(metafile));
		/// null for any other Graphics.</summary>
		internal GpMetafileRecorder mf_rec;

		/// <summary>GpMetafile::GetGraphicsContext: the recording's one Graphics.</summary>
		static Graphics ForMetafile (Metafile mf)
		{
			GpMetafileRecorder rec = mf.TakeRecorder ();
			var g = new Graphics (IntPtr.Zero, mf);
			g.mf_rec = rec;
			return g;
		}

		// ---- what an image is to DrawImage: GpImage::GetBounds and its resolution ------------------

		/// <summary>The image's bounds and their unit (a bitmap's pixels; a metafile's frame, in
		/// pixels at its dpi) and its resolution.</summary>
		static RectangleF ImageBounds (Image image, out GraphicsUnit unit, out float dpiX, out float dpiY)
		{
			if (image is Metafile mf) {
				unit = GraphicsUnit.Pixel;
				RectangleF b = mf.MetafileBounds (ref unit);
				dpiX = mf.MetafileDpiX;
				dpiY = mf.MetafileDpiY;
				return b;
			}
			unit = GraphicsUnit.Pixel;
			dpiX = image.HorizontalResolution;
			dpiY = image.VerticalResolution;
			return new RectangleF (0, 0, image.Width, image.Height);
		}

		/// <summary>GpGraphics::GetImageDestPageSize for this Graphics: the size, in page units, an
		/// image's <paramref name="w"/> x <paramref name="h"/> (in <paramref name="unit"/>) is drawn at.</summary>
		void ImageDestPageSize (float dpiX, float dpiY, float w, float h, GraphicsUnit unit, out float dw, out float dh)
		{
			if (mf_rec != null) { mf_rec.GetImageDestPageSize (dpiX, dpiY, w, h, unit, out dw, out dh); return; }
			if (gp != null) { SyncEngine (); gp.GetImageDestPageSize (dpiX, dpiY, w, h, unit, out dw, out dh); return; }
			// A recording Graphics: its page factor is recording units per page unit.
			float pm = PageFactor;
			if (unit != GraphicsUnit.Pixel) {
				dw = UnitScale (unit) * w / pm;
				dh = UnitScale (unit) * h / pm;
				return;
			}
			float px = UnitScale (GraphicsUnit.Pixel);
			dw = px * DpiX * w / (dpiX * pm);
			dh = px * DpiY * h / (dpiY * pm);
		}

		static PointF [] Parallelogram (RectangleF r)
			=> new [] { new PointF (r.X, r.Y), new PointF (r.Right, r.Y), new PointF (r.X, r.Bottom) };

		// ---- DrawImage onto a recording, or of a metafile onto anything ------------------------------
		//
		// Each returns false only when the caller's own checks should run (a null image).

		/// <summary>GdipDrawImage(x, y): at the image's physical size, its bounds in their unit.</summary>
		bool MetaDrawImage (Image image, float x, float y)
		{
			if (image == null || (mf_rec == null && !(image is Metafile))) return false;
			RectangleF src = ImageBounds (image, out GraphicsUnit unit, out float dx, out float dy);
			ImageDestPageSize (dx, dy, src.Width, src.Height, unit, out float w, out float h);
			return MetaDrawImage (image, new RectangleF (x, y, w, h), src, unit, null);
		}

		/// <summary>GdipDrawImageRect: the image's bounds into the rectangle.</summary>
		bool MetaDrawImage (Image image, RectangleF dst)
		{
			if (image == null || (mf_rec == null && !(image is Metafile))) return false;
			RectangleF src = ImageBounds (image, out GraphicsUnit unit, out _, out _);
			return MetaDrawImage (image, dst, src, unit, null);
		}

		/// <summary>GdipDrawImagePointRect: at (x, y), the source's size in its unit.</summary>
		bool MetaDrawImage (Image image, float x, float y, RectangleF src, GraphicsUnit unit)
		{
			if (image == null || (mf_rec == null && !(image is Metafile))) return false;
			CheckImageUnit (unit);
			ImageBounds (image, out _, out float dx, out float dy);
			ImageDestPageSize (dx, dy, src.Width, src.Height, unit, out float w, out float h);
			return MetaDrawImage (image, new RectangleF (x, y, w, h), src, unit, null);
		}

		/// <summary>GdipDrawImageRectRect.</summary>
		bool MetaDrawImage (Image image, RectangleF dst, RectangleF src, GraphicsUnit unit, ImageAttributes ia)
		{
			if (image == null || (mf_rec == null && !(image is Metafile))) return false;
			CheckImageUnit (unit);
			if (mf_rec != null) {
				mf_rec.DrawImage (image, dst, src, unit, ia);
				return true;
			}
			GpMetafilePlayer.Play (this, (Metafile) image, Parallelogram (dst), src, unit, ia);
			return true;
		}

		/// <summary>GdipDrawImagePoints / GdipDrawImagePointsRect: three points (four is not
		/// implemented in GDI+), the image's bounds when no source is given.</summary>
		bool MetaDrawImage (Image image, PointF [] pts, RectangleF? src, GraphicsUnit unit, ImageAttributes ia)
		{
			if (image == null || pts == null || (mf_rec == null && !(image is Metafile))) return false;
			if (pts.Length == 0) throw new ArgumentException ("Parameter is not valid.");
			CheckImageUnit (unit);
			if (pts.Length == 4) throw new NotImplementedException ();
			if (pts.Length != 3) throw new ArgumentException ("Parameter is not valid.");
			RectangleF s;
			if (src.HasValue) s = src.Value;
			else s = ImageBounds (image, out unit, out _, out _);
			if (mf_rec != null) {
				mf_rec.DrawImagePoints (image, (PointF []) pts.Clone (), s, unit, ia);
				return true;
			}
			GpMetafilePlayer.Play (this, (Metafile) image, (PointF []) pts.Clone (), s, unit, ia);
			return true;
		}

		// ---- EnumerateMetafile ----------------------------------------------------------------------

		void Enumerate (Metafile metafile, PointF [] dest, RectangleF? src, GraphicsUnit unit,
				EnumerateMetafileProc callback, IntPtr callbackData, ImageAttributes ia)
		{
			if (metafile == null) throw new ArgumentNullException ("metafile");
			if (dest == null) throw new ArgumentNullException ("destPoints");
			RectangleF s;
			if (src.HasValue) {
				CheckImageUnit (unit);
				s = src.Value;
			} else {
				s = ImageBounds (metafile, out unit, out _, out _);
			}
			GpMetafilePlayer.Enumerate (this, metafile, (PointF []) dest.Clone (), s, unit, callback, callbackData, ia);
		}

		void Enumerate (Metafile metafile, RectangleF dest, RectangleF? src, GraphicsUnit unit,
				EnumerateMetafileProc callback, IntPtr callbackData, ImageAttributes ia)
			=> Enumerate (metafile, Parallelogram (dest), src, unit, callback, callbackData, ia);

		/// <summary>GpGraphics::EnumerateMetafile(destPoint): at the metafile's (or the source's)
		/// physical size.</summary>
		void Enumerate (Metafile metafile, PointF dest, RectangleF? src, GraphicsUnit unit,
				EnumerateMetafileProc callback, IntPtr callbackData, ImageAttributes ia)
		{
			if (metafile == null) throw new ArgumentNullException ("metafile");
			RectangleF bounds = ImageBounds (metafile, out GraphicsUnit bu, out float dx, out float dy);
			RectangleF s = src ?? bounds;
			GraphicsUnit su = src.HasValue ? unit : bu;
			if (src.HasValue) CheckImageUnit (unit);
			ImageDestPageSize (dx, dy, s.Width, s.Height, su, out float w, out float h);
			Enumerate (metafile, new RectangleF (dest.X, dest.Y, w, h), s, su, callback, callbackData, ia);
		}

		static PointF [] PointsF (Point [] p) => p == null ? null : Array.ConvertAll (p, q => (PointF) q);

		public void EnumerateMetafile (Metafile metafile, Point [] destPoints, EnumerateMetafileProc callback)
			=> Enumerate (metafile, PointsF (destPoints), null, GraphicsUnit.Pixel, callback, IntPtr.Zero, null);

		public void EnumerateMetafile (Metafile metafile, RectangleF destRect, EnumerateMetafileProc callback)
			=> Enumerate (metafile, destRect, null, GraphicsUnit.Pixel, callback, IntPtr.Zero, null);

		public void EnumerateMetafile (Metafile metafile, PointF [] destPoints, EnumerateMetafileProc callback)
			=> Enumerate (metafile, destPoints, null, GraphicsUnit.Pixel, callback, IntPtr.Zero, null);

		public void EnumerateMetafile (Metafile metafile, Rectangle destRect, EnumerateMetafileProc callback)
			=> Enumerate (metafile, (RectangleF) destRect, null, GraphicsUnit.Pixel, callback, IntPtr.Zero, null);

		public void EnumerateMetafile (Metafile metafile, Point destPoint, EnumerateMetafileProc callback)
			=> Enumerate (metafile, (PointF) destPoint, null, GraphicsUnit.Pixel, callback, IntPtr.Zero, null);

		public void EnumerateMetafile (Metafile metafile, PointF destPoint, EnumerateMetafileProc callback)
			=> Enumerate (metafile, destPoint, null, GraphicsUnit.Pixel, callback, IntPtr.Zero, null);

		public void EnumerateMetafile (Metafile metafile, PointF destPoint, EnumerateMetafileProc callback, IntPtr callbackData)
			=> Enumerate (metafile, destPoint, null, GraphicsUnit.Pixel, callback, callbackData, null);

		public void EnumerateMetafile (Metafile metafile, Rectangle destRect, EnumerateMetafileProc callback, IntPtr callbackData)
			=> Enumerate (metafile, (RectangleF) destRect, null, GraphicsUnit.Pixel, callback, callbackData, null);

		public void EnumerateMetafile (Metafile metafile, PointF [] destPoints, EnumerateMetafileProc callback, IntPtr callbackData)
			=> Enumerate (metafile, destPoints, null, GraphicsUnit.Pixel, callback, callbackData, null);

		public void EnumerateMetafile (Metafile metafile, Point destPoint, EnumerateMetafileProc callback, IntPtr callbackData)
			=> Enumerate (metafile, (PointF) destPoint, null, GraphicsUnit.Pixel, callback, callbackData, null);

		public void EnumerateMetafile (Metafile metafile, Point [] destPoints, EnumerateMetafileProc callback, IntPtr callbackData)
			=> Enumerate (metafile, PointsF (destPoints), null, GraphicsUnit.Pixel, callback, callbackData, null);

		public void EnumerateMetafile (Metafile metafile, RectangleF destRect, EnumerateMetafileProc callback, IntPtr callbackData)
			=> Enumerate (metafile, destRect, null, GraphicsUnit.Pixel, callback, callbackData, null);

		public void EnumerateMetafile (Metafile metafile, PointF destPoint, RectangleF srcRect, GraphicsUnit srcUnit, EnumerateMetafileProc callback)
			=> Enumerate (metafile, destPoint, srcRect, srcUnit, callback, IntPtr.Zero, null);

		public void EnumerateMetafile (Metafile metafile, Point destPoint, Rectangle srcRect, GraphicsUnit srcUnit, EnumerateMetafileProc callback)
			=> Enumerate (metafile, (PointF) destPoint, srcRect, srcUnit, callback, IntPtr.Zero, null);

		public void EnumerateMetafile (Metafile metafile, PointF [] destPoints, RectangleF srcRect, GraphicsUnit srcUnit, EnumerateMetafileProc callback)
			=> Enumerate (metafile, destPoints, srcRect, srcUnit, callback, IntPtr.Zero, null);

		public void EnumerateMetafile (Metafile metafile, Point [] destPoints, Rectangle srcRect, GraphicsUnit srcUnit, EnumerateMetafileProc callback)
			=> Enumerate (metafile, PointsF (destPoints), srcRect, srcUnit, callback, IntPtr.Zero, null);

		public void EnumerateMetafile (Metafile metafile, RectangleF destRect, RectangleF srcRect, GraphicsUnit srcUnit, EnumerateMetafileProc callback)
			=> Enumerate (metafile, destRect, srcRect, srcUnit, callback, IntPtr.Zero, null);

		public void EnumerateMetafile (Metafile metafile, Rectangle destRect, Rectangle srcRect, GraphicsUnit srcUnit, EnumerateMetafileProc callback)
			=> Enumerate (metafile, (RectangleF) destRect, srcRect, srcUnit, callback, IntPtr.Zero, null);

		public void EnumerateMetafile (Metafile metafile, RectangleF destRect, EnumerateMetafileProc callback, IntPtr callbackData, ImageAttributes imageAttr)
			=> Enumerate (metafile, destRect, null, GraphicsUnit.Pixel, callback, callbackData, imageAttr);

		public void EnumerateMetafile (Metafile metafile, Point destPoint, EnumerateMetafileProc callback, IntPtr callbackData, ImageAttributes imageAttr)
			=> Enumerate (metafile, (PointF) destPoint, null, GraphicsUnit.Pixel, callback, callbackData, imageAttr);

		public void EnumerateMetafile (Metafile metafile, PointF destPoint, EnumerateMetafileProc callback, IntPtr callbackData, ImageAttributes imageAttr)
			=> Enumerate (metafile, destPoint, null, GraphicsUnit.Pixel, callback, callbackData, imageAttr);

		public void EnumerateMetafile (Metafile metafile, Point [] destPoints, EnumerateMetafileProc callback, IntPtr callbackData, ImageAttributes imageAttr)
			=> Enumerate (metafile, PointsF (destPoints), null, GraphicsUnit.Pixel, callback, callbackData, imageAttr);

		public void EnumerateMetafile (Metafile metafile, PointF [] destPoints, EnumerateMetafileProc callback, IntPtr callbackData, ImageAttributes imageAttr)
			=> Enumerate (metafile, destPoints, null, GraphicsUnit.Pixel, callback, callbackData, imageAttr);

		public void EnumerateMetafile (Metafile metafile, Rectangle destRect, EnumerateMetafileProc callback, IntPtr callbackData, ImageAttributes imageAttr)
			=> Enumerate (metafile, (RectangleF) destRect, null, GraphicsUnit.Pixel, callback, callbackData, imageAttr);

		public void EnumerateMetafile (Metafile metafile, Rectangle destRect, Rectangle srcRect, GraphicsUnit srcUnit, EnumerateMetafileProc callback, IntPtr callbackData)
			=> Enumerate (metafile, (RectangleF) destRect, srcRect, srcUnit, callback, callbackData, null);

		public void EnumerateMetafile (Metafile metafile, PointF [] destPoints, RectangleF srcRect, GraphicsUnit srcUnit, EnumerateMetafileProc callback, IntPtr callbackData)
			=> Enumerate (metafile, destPoints, srcRect, srcUnit, callback, callbackData, null);

		public void EnumerateMetafile (Metafile metafile, RectangleF destRect, RectangleF srcRect, GraphicsUnit srcUnit, EnumerateMetafileProc callback, IntPtr callbackData)
			=> Enumerate (metafile, destRect, srcRect, srcUnit, callback, callbackData, null);

		public void EnumerateMetafile (Metafile metafile, PointF destPoint, RectangleF srcRect, GraphicsUnit srcUnit, EnumerateMetafileProc callback, IntPtr callbackData)
			=> Enumerate (metafile, destPoint, srcRect, srcUnit, callback, callbackData, null);

		public void EnumerateMetafile (Metafile metafile, Point destPoint, Rectangle srcRect, GraphicsUnit srcUnit, EnumerateMetafileProc callback, IntPtr callbackData)
			=> Enumerate (metafile, (PointF) destPoint, srcRect, srcUnit, callback, callbackData, null);

		public void EnumerateMetafile (Metafile metafile, Point [] destPoints, Rectangle srcRect, GraphicsUnit srcUnit, EnumerateMetafileProc callback, IntPtr callbackData)
			=> Enumerate (metafile, PointsF (destPoints), srcRect, srcUnit, callback, callbackData, null);

		public void EnumerateMetafile (Metafile metafile, Point [] destPoints, Rectangle srcRect, GraphicsUnit unit, EnumerateMetafileProc callback, IntPtr callbackData, ImageAttributes imageAttr)
			=> Enumerate (metafile, PointsF (destPoints), srcRect, unit, callback, callbackData, imageAttr);

		public void EnumerateMetafile (Metafile metafile, Rectangle destRect, Rectangle srcRect, GraphicsUnit unit, EnumerateMetafileProc callback, IntPtr callbackData, ImageAttributes imageAttr)
			=> Enumerate (metafile, (RectangleF) destRect, srcRect, unit, callback, callbackData, imageAttr);

		public void EnumerateMetafile (Metafile metafile, Point destPoint, Rectangle srcRect, GraphicsUnit unit, EnumerateMetafileProc callback, IntPtr callbackData, ImageAttributes imageAttr)
			=> Enumerate (metafile, (PointF) destPoint, srcRect, unit, callback, callbackData, imageAttr);

		public void EnumerateMetafile (Metafile metafile, RectangleF destRect, RectangleF srcRect, GraphicsUnit unit, EnumerateMetafileProc callback, IntPtr callbackData, ImageAttributes imageAttr)
			=> Enumerate (metafile, destRect, srcRect, unit, callback, callbackData, imageAttr);

		public void EnumerateMetafile (Metafile metafile, PointF [] destPoints, RectangleF srcRect, GraphicsUnit unit, EnumerateMetafileProc callback, IntPtr callbackData, ImageAttributes imageAttr)
			=> Enumerate (metafile, destPoints, srcRect, unit, callback, callbackData, imageAttr);

		public void EnumerateMetafile (Metafile metafile, PointF destPoint, RectangleF srcRect, GraphicsUnit unit, EnumerateMetafileProc callback, IntPtr callbackData, ImageAttributes imageAttr)
			=> Enumerate (metafile, destPoint, srcRect, unit, callback, callbackData, imageAttr);
	}
}
