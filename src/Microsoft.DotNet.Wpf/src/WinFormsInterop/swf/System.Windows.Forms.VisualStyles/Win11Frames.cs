// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// The Windows 11 theme's image parts, drawn from geometry.
//
// uxtheme draws most Windows 11 parts -- the push button, check box, radio button, combo box drop
// button, scroll bar pieces -- by blitting a small premultiplied bitmap FRAME out of the theme file,
// one frame per state, and stretching it nine-grid around its SIZINGMARGINS (see NineGrid). The
// frames are rasterized vector art: every alpha in them is a whole number of sixteenths, the corners
// are 90-degree-rotation symmetric but not mirror symmetric, which is what sixteen samples on a rook
// lattice give. So each frame here is the same shapes rasterized the same way -- rounded rectangles,
// circles, stroked polylines, sampled sixteen times a pixel and composited per sample -- with the
// colours and geometry the theme's frames were measured to have. No bitmap from the theme is used;
// the frames are made, not copied, and a Windows-only test holds them to the theme's own.
//
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace System.Windows.Forms.VisualStyles
{
	internal static class Win11Frames
	{
		/// <summary>A frame: premultiplied 0xAARRGGBB pixels, row-major.</summary>
		internal sealed class Frame
		{
			public readonly int Width, Height;
			public readonly uint [] Pixels;
			/// <summary>SIZINGTYPE: false draws the frame at its own size (TRUESIZE), true stretches
			/// it nine-grid (STRETCH) around <see cref="Margins"/>.</summary>
			public bool Stretch;
			/// <summary>SIZINGMARGINS, left, right, top, bottom: the rows and columns kept as they are
			/// when the frame is stretched.</summary>
			public (int L, int R, int T, int B) Margins;
			/// <summary>A glyph drawn over the background at its own size, centred (GLYPHTYPE
			/// IMAGEGLYPH): the combo box's chevron.</summary>
			public Frame Glyph;
			/// <summary>Larger glyphs for larger buttons (IMAGESELECTTYPE SIZE): each is taken in
			/// place of <see cref="Glyph"/> once the button is at least its size square.</summary>
			public (int MinSize, Frame Glyph) [] LargerGlyphs;
			public Frame (int width, int height)
			{
				Width = width;
				Height = height;
				Pixels = new uint [width * height];
			}
		}

		// ---- uxtheme's blit ------------------------------------------------------------------

		/// <summary>Draws a frame into <paramref name="bounds"/> the way DrawThemeBackground draws an
		/// image part: a true-size frame at its own size, centred (the offset rounded down); a
		/// stretched one with its margins copied and its middle scaled by whole source pixels.</summary>
		internal static void Draw (Graphics g, Frame f, Rectangle bounds)
		{
			if (bounds.Width <= 0 || bounds.Height <= 0)
				return;
			Rectangle dest = bounds;
			if (!f.Stretch) {
				int w = Math.Min (f.Width, bounds.Width), h = Math.Min (f.Height, bounds.Height);
				// Too small for the frame: scaled down whole, keeping its aspect (UNIFORMSIZING).
				if (w < f.Width || h < f.Height)
					w = h = Math.Min (w * f.Height / f.Width, h);
				dest = new Rectangle (bounds.X + (bounds.Width - w) / 2, bounds.Y + (bounds.Height - h) / 2, w, h);
			}
			g.DrawImage (Blit (f, dest.Width, dest.Height), dest.X, dest.Y, dest.Width, dest.Height);

			// A stretched frame whose middle is one opaque colour gets that middle again as a solid
			// fill -- the same pixels, but text drawn on the face then stands on a paper colour the
			// renderer can see, and blends onto it exactly rather than onto an image.
			if (f.Stretch && Solid (f) is (uint face, int l, int r, int t, int b)) {
				var middle = new Rectangle (dest.X + l, dest.Y + t, dest.Width - l - r, dest.Height - t - b);
				if (middle.Width > 0 && middle.Height > 0)
					using (var brush = new SolidBrush (Color.FromArgb ((int) face)))
						g.FillRectangle (brush, middle);
			}

			Frame glyph = f.Glyph;
			if (f.LargerGlyphs != null)
				foreach (var (min, larger) in f.LargerGlyphs)
					if (bounds.Width >= min && bounds.Height >= min)
						glyph = larger;
			if (glyph != null) {
				// Centred with the offset rounded down, and never scaled: a glyph taller than the
				// button hangs from its top.
				int gx = bounds.X + Math.Max (0, (bounds.Width - glyph.Width) / 2);
				int gy = bounds.Y + Math.Max (0, (bounds.Height - glyph.Height) / 2);
				g.DrawImage (Blit (glyph, glyph.Width, glyph.Height), gx, gy, glyph.Width, glyph.Height);
			}
		}

		/// <summary>A check box as a tree or list view's STATE IMAGE draws it. comctl32's
		/// CreateCheckBoxImagelistEx has DrawThemeBackgroundEx paint the part into a 32-bit DIB --
		/// premultiplied, as AlphaBlend leaves it -- and the image list then draws those pixels as if
		/// they were straight alpha, so every partly covered pixel is premultiplied a second time:
		/// out = P * A + paper * (1 - A). Measured to the digit on a stock tree's boxes.</summary>
		internal static void DrawStateImage (Graphics g, Frame f, Rectangle bounds)
		{
			if (bounds.Width <= 0 || bounds.Height <= 0)
				return;
			Bitmap bmp;
			lock (s_stateImages) {
				if (!s_stateImages.TryGetValue (f, out bmp)) {
					var px = (uint []) f.Pixels.Clone ();
					if (f.Glyph is Frame glyph) {
						int gx = Math.Max (0, (f.Width - glyph.Width) / 2), gy = Math.Max (0, (f.Height - glyph.Height) / 2);
						for (int y = 0; y < glyph.Height && gy + y < f.Height; y++)
							for (int x = 0; x < glyph.Width && gx + x < f.Width; x++) {
								int i = (gy + y) * f.Width + gx + x;
								px [i] = Over (glyph.Pixels [y * glyph.Width + x], px [i]);
							}
					}
					bmp = new Bitmap (f.Width, f.Height, PixelFormat.Format32bppArgb);
					BitmapData data = bmp.LockBits (new Rectangle (0, 0, f.Width, f.Height), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
					var argb = new int [px.Length];
					for (int i = 0; i < px.Length; i++)
						argb [i] = (int) px [i];     // the premultiplied values, read as straight
					for (int y = 0; y < f.Height; y++)
						Marshal.Copy (argb, y * f.Width, data.Scan0 + y * data.Stride, f.Width);
					bmp.UnlockBits (data);
					s_stateImages [f] = bmp;
				}
			}
			int w = Math.Min (f.Width, bounds.Width), h = Math.Min (f.Height, bounds.Height);
			g.DrawImage (bmp, new Rectangle (bounds.X, bounds.Y, w, h), 0, 0, w, h, GraphicsUnit.Pixel);
		}

		static readonly Dictionary<Frame, Bitmap> s_stateImages = new ();

		/// <summary>Premultiplied source over destination, per channel.</summary>
		static uint Over (uint s, uint d)
		{
			uint sa = s >> 24, inv = 255 - sa, r = 0;
			for (int sh = 0; sh < 32; sh += 8) {
				uint c = ((s >> sh) & 0xff) + (((d >> sh) & 0xff) * inv + 127) / 255;
				r |= Math.Min (255u, c) << sh;
			}
			return r;
		}

		/// <summary>A frame drawn true-size and centred into a <paramref name="w"/> x <paramref name="h"/>
		/// cell of opaque <paramref name="paper"/>: straight ARGB, row-major.</summary>
		internal static int [] Composite (Frame f, int w, int h, Color paper)
		{
			var px = new int [w * h];
			uint bg = (uint) paper.ToArgb () | 0xff000000u;
			int ox = (w - f.Width) / 2, oy = (h - f.Height) / 2;
			for (int y = 0; y < h; y++)
				for (int x = 0; x < w; x++) {
					int fx = x - ox, fy = y - oy;
					uint p = bg;
					if (fx >= 0 && fy >= 0 && fx < f.Width && fy < f.Height)
						p = Over (f.Pixels [fy * f.Width + fx], bg);
					px [y * w + x] = (int) (p | 0xff000000u);
				}
			return px;
		}

		/// <summary>Explorer::TreeView TVP_GLYPH, 16x16 true size: a chevron pointing right in #8B8B8B
		/// (closed) or down in #1B1B1B (opened). Unlike the frames above this art is EXACT-AREA
		/// coverage, not sixteen samples -- its faintest pixels carry an alpha of 1 -- and it is a
		/// 1.55-pixel stroke through pixel centres, (5.5, 4.5) to (9.5, 8.5) to (5.5, 12.5), butt ends,
		/// a round join; the opened one is the same stroke turned, a pixel to the left.</summary>
		internal static Frame ExplorerTreeGlyph (bool open)
		{
			lock (s_explorerGlyphs) {
				int k = open ? 1 : 0;
				if (s_explorerGlyphs [k] != null)
					return s_explorerGlyphs [k];
				List<PointF> poly = RoundJoinChevron (5.5f, 4.5f, 9.5f, 8.5f, 5.5f, 12.5f, 1.55f, -1 / 32f);
				if (open)
					for (int i = 0; i < poly.Count; i++)
						poly [i] = new PointF (poly [i].Y - 1, poly [i].X);
				return s_explorerGlyphs [k] = RenderExactArea (16, 16, poly, open ? 0x1b1b1bu : 0x8b8b8bu);
			}
		}

		static readonly Frame [] s_explorerGlyphs = new Frame [2];

		/// <summary>The outline of a two-segment stroke of width <paramref name="w"/>: butt ends moved
		/// <paramref name="ext"/> along the stroke, a mitred inner corner and a round outer one.</summary>
		static List<PointF> RoundJoinChevron (float x0, float y0, float xa, float ya, float x2, float y2, float w, float ext)
		{
			static (float X, float Y) Unit (float dx, float dy) { float l = MathF.Sqrt (dx * dx + dy * dy); return (dx / l, dy / l); }
			var u1 = Unit (xa - x0, ya - y0);
			var u2 = Unit (x2 - xa, y2 - ya);
			(float X, float Y) n1 = (-u1.Y, u1.X), n2 = (-u2.Y, u2.X);
			float h = w / 2;
			var s0 = (X: x0 - u1.X * ext, Y: y0 - u1.Y * ext);
			var e2 = (X: x2 + u2.X * ext, Y: y2 + u2.Y * ext);
			PointF Miter (float sign)
			{
				float p1x = xa + sign * n1.X * h, p1y = ya + sign * n1.Y * h;
				float p2x = xa + sign * n2.X * h, p2y = ya + sign * n2.Y * h;
				float det = u1.X * -u2.Y - u1.Y * -u2.X;
				float t = ((p2x - p1x) * -u2.Y - (p2y - p1y) * -u2.X) / det;
				return new PointF (p1x + t * u1.X, p1y + t * u1.Y);
			}
			PointF mp = Miter (1), mm = Miter (-1);
			float outer = (mp.X - xa) * (u1.X - u2.X) + (mp.Y - ya) * (u1.Y - u2.Y) > (mm.X - xa) * (u1.X - u2.X) + (mm.Y - ya) * (u1.Y - u2.Y) ? 1 : -1;
			List<PointF> Side (float sign)
			{
				if (sign != outer)
					return new List<PointF> { Miter (sign) };
				float a0 = MathF.Atan2 (sign * n1.Y, sign * n1.X), a1 = MathF.Atan2 (sign * n2.Y, sign * n2.X);
				float d = a1 - a0;
				while (d > MathF.PI) d -= 2 * MathF.PI;
				while (d < -MathF.PI) d += 2 * MathF.PI;
				var arc = new List<PointF> ();
				for (int i = 0; i <= 16; i++) {
					float t = a0 + d * i / 16;
					arc.Add (new PointF (xa + h * MathF.Cos (t), ya + h * MathF.Sin (t)));
				}
				return arc;
			}
			var poly = new List<PointF> { new PointF (s0.X + n1.X * h, s0.Y + n1.Y * h) };
			poly.AddRange (Side (1));
			poly.Add (new PointF (e2.X + n2.X * h, e2.Y + n2.Y * h));
			poly.Add (new PointF (e2.X - n2.X * h, e2.Y - n2.Y * h));
			List<PointF> back = Side (-1);
			back.Reverse ();
			poly.AddRange (back);
			poly.Add (new PointF (s0.X - n1.X * h, s0.Y - n1.Y * h));
			return poly;
		}

		/// <summary>A polygon's exact area coverage of each pixel (the polygon clipped to the pixel's
		/// square), as premultiplied <paramref name="rgb"/>.</summary>
		static Frame RenderExactArea (int w, int h, List<PointF> poly, uint rgb)
		{
			var f = new Frame (w, h);
			for (int y = 0; y < h; y++)
				for (int x = 0; x < w; x++) {
					double a = Math.Abs (ClippedArea (poly, x, y, x + 1, y + 1));
					uint alpha = (uint) Math.Min (255, (int) (a * 255 + 0.5));
					if (alpha != 0)
						f.Pixels [y * w + x] = Premultiply (rgb, alpha);
				}
			return f;
		}

		static double ClippedArea (List<PointF> poly, float x0, float y0, float x1, float y1)
		{
			List<PointF> p = poly;
			p = ClipEdge (p, q => q.X >= x0, (a, b) => new PointF (x0, a.Y + (b.Y - a.Y) * (x0 - a.X) / (b.X - a.X)));
			if (p.Count > 0) p = ClipEdge (p, q => q.X <= x1, (a, b) => new PointF (x1, a.Y + (b.Y - a.Y) * (x1 - a.X) / (b.X - a.X)));
			if (p.Count > 0) p = ClipEdge (p, q => q.Y >= y0, (a, b) => new PointF (a.X + (b.X - a.X) * (y0 - a.Y) / (b.Y - a.Y), y0));
			if (p.Count > 0) p = ClipEdge (p, q => q.Y <= y1, (a, b) => new PointF (a.X + (b.X - a.X) * (y1 - a.Y) / (b.Y - a.Y), y1));
			double s = 0;
			for (int i = 0; i < p.Count; i++) {
				PointF a = p [(i + p.Count - 1) % p.Count], b = p [i];
				s += (double) a.X * b.Y - (double) b.X * a.Y;
			}
			return s / 2;
		}

		static List<PointF> ClipEdge (List<PointF> p, Func<PointF, bool> inside, Func<PointF, PointF, PointF> cross)
		{
			var o = new List<PointF> (p.Count + 4);
			for (int i = 0; i < p.Count; i++) {
				PointF a = p [(i + p.Count - 1) % p.Count], b = p [i];
				bool ia = inside (a), ib = inside (b);
				if (ib) {
					if (!ia) o.Add (cross (a, b));
					o.Add (b);
				} else if (ia)
					o.Add (cross (a, b));
			}
			return o;
		}

		/// <summary>MONTHCAL's navigation arrows, 16x16 true size: part 11 (previous) points left
		/// from column 8, part 10 (next) right from column 7, seven rows from row 4, solid runs of
		/// 1, 2, 3, 4, 3, 2, 1 pixels. At rest and pressed each row is one grey, darkening down the
		/// glyph; hot it is #0066CC throughout; disabled the rest greys at alpha 0x66.</summary>
		internal static Frame MonthCalArrow (bool previous, int state)
		{
			uint [] ramp = { 0x3b, 0x3a, 0x38, 0x31, 0x26, 0x19, 0x0b };
			int [] width = { 1, 2, 3, 4, 3, 2, 1 };
			var f = new Frame (16, 16);
			for (int r = 0; r < 7; r++) {
				uint g = ramp [r];
				uint px = state switch {
					2 => 0xff0066ccu,
					4 => Premultiply (g << 16 | g << 8 | g, 0x66),
					_ => 0xff000000u | g << 16 | g << 8 | g,
				};
				for (int i = 0; i < width [r]; i++)
					f.Pixels [(4 + r) * 16 + (previous ? 8 - i : 7 + i)] = px;
			}
			return f;
		}

		/// <summary>MC_TODAY in its today states (5 and 6 share one image), 5x5 nine-grid 2/2/2/2: a
		/// one-pixel #0066CC ring whose runs turn #0080FF a pixel before each corner, the corner and
		/// the pixel diagonally inside it a translucent #1D92FF -- alpha 70, and 65 on the bottom
		/// corners. Stretched, only the middle pixel of each edge repeats, so a box of any size keeps
		/// the lighter shoulders at its ends.</summary>
		internal static Frame MonthCalToday ()
		{
			const uint e = 0xff0080ffu, m = 0xff0066ccu, c = 0x46082846u, b = 0x41072541u;
			var f = new Frame (5, 5) { Stretch = true, Margins = (2, 2, 2, 2) };
			uint [] px = {
				c, e, m, e, c,
				e, c, 0, c, e,
				m, 0, 0, 0, m,
				e, c, 0, c, e,
				b, e, m, e, b,
			};
			Array.Copy (px, f.Pixels, px.Length);
			return f;
		}

		/// <summary>DP_SHOWCALENDARBUTTONRIGHT's glyph, 20x14: a calendar page beside a chevron.
		/// <para>The page is 11x12 in #776B68 with its three square corners at alpha 0x96, and its
		/// bottom-right corner folded: a #5D5452 crease inside two border pixels, then a fall-off of
		/// alpha 0xB0, 0x40 and 0x47 along the diagonal. Inside, a grid: a pale header band, then
		/// rows of cells whose lines and faces both darken from top left to bottom right -- each
		/// pixel's colour is a function of x + y alone, one ramp for the lines and one for the
		/// faces. A soft shadow, black at alpha 0x10 at most, runs down its right and under it.
		/// The chevron is #4D6185, rows of 7, 5, 3 and 1 from (13, 4).</para>
		/// <para>Disabled (the theme's fourth state) is the page's premultiplied pixels scaled by
		/// 0x66/255, each channel rounded, beside an opaque #C9C9C2 chevron.</para></summary>
		internal static Frame DatePickerGlyph (bool disabled = false)
		{
			if (disabled)
				return s_datePickerGlyphDisabled ??= Disabled (DatePickerGlyph ());
			if (s_datePickerGlyph != null)
				return s_datePickerGlyph;
			var f = new Frame (20, 14);
			void Put (int x, int y, uint rgb, uint a) => f.Pixels [y * 20 + x] = Premultiply (rgb, a);
			// The faces and the lines, by x + y (2..17 and 3..16).
			uint [] face = { 0xffffff, 0xffffff, 0xffffff, 0xffffff, 0xfdfefe, 0xfafbfd, 0xf7f8fc, 0xf3f5fa,
			                 0xeff2f9, 0xeaeff7, 0xe7ecf5, 0xe2e8f4, 0xdee5f2, 0xdbe3f1, 0xd8e1f0, 0xd6dfef };
			uint [] line = { 0xc8c8c8, 0xc8c8c8, 0xc8c8c8, 0xc6c7c7, 0xc4c5c6, 0xc2c3c6, 0xbfc0c4, 0xbbbec3,
			                 0xb8bbc2, 0xb5b9c0, 0xb1b6bf, 0xaeb4be, 0xacb2bd, 0xa9b0bc };
			const uint border = 0x776b68;
			for (int y = 1; y <= 10; y++)
				for (int x = 1; x <= 9; x++) {
					// Row 2 and the last row are all face, and so are the first and last columns; the
					// rows between alternate full lines with cells, whose even columns are lines.
					bool isLine = x > 1 && x < 9 && y != 2 && y != 10 && ((y & 1) == 1 || (x & 1) == 0);
					if (isLine ? x + y - 3 >= line.Length : x + y - 2 >= face.Length)
						continue;          // the fold's own pixels, drawn below
					Put (x, y, isLine ? line [x + y - 3] : face [x + y - 2], 0xff);
				}
			for (int x = 0; x <= 10; x++) { Put (x, 0, border, 0xff); Put (x, 11, border, 0xff); }
			for (int y = 0; y <= 11; y++) { Put (0, y, border, 0xff); Put (10, y, border, 0xff); }
			Put (0, 0, border, 0x96); Put (10, 0, border, 0x96); Put (0, 11, border, 0x96);
			// The fold.
			Put (8, 9, 0x5d5452, 0xff); Put (9, 9, border, 0xff); Put (8, 10, border, 0xff);
			Put (9, 10, 0x655b58, 0xb0); Put (10, 10, 0x2b2727, 0x40); Put (9, 11, 0x272323, 0x47);
			Put (10, 11, 0, 0x1e); Put (11, 11, 0, 0x08); Put (11, 10, 0, 0x0e);
			// The shadow.
			uint [] right = { 0x04, 0x0c, 0x10, 0x10, 0x10, 0x10, 0x10, 0x10 };
			for (int i = 0; i < right.Length; i++) Put (11, 2 + i, 0, right [i]);
			uint [] under1 = { 0x0c, 0x24, 0x30, 0x30, 0x30, 0x30, 0x30, 0x2c, 0x1e, 0x0c, 0x02 };
			uint [] under2 = { 0x04, 0x0c, 0x10, 0x10, 0x10, 0x10, 0x10, 0x0e, 0x08, 0x02 };
			for (int i = 0; i < under1.Length; i++) Put (1 + i, 12, 0, under1 [i]);
			for (int i = 0; i < under2.Length; i++) Put (1 + i, 13, 0, under2 [i]);
			// The chevron.
			for (int r = 0; r < 4; r++)
				for (int x = 13 + r; x <= 19 - r; x++)
					Put (x, 4 + r, 0x4d6185, 0xff);
			return s_datePickerGlyph = f;

			static Frame Disabled (Frame normal)
			{
				var d = new Frame (normal.Width, normal.Height);
				for (int i = 0; i < d.Pixels.Length; i++) {
					uint p = normal.Pixels [i];
					uint Scale (int shift) => (uint) Math.Round (((p >> shift) & 0xff) * 0x66 / 255.0) << shift;
					d.Pixels [i] = p == 0xff4d6185u ? 0xffc9c9c2u : Scale (24) | Scale (16) | Scale (8) | Scale (0);
				}
				return d;
			}
		}

		static Frame s_datePickerGlyph, s_datePickerGlyphDisabled;

		/// <summary>MC_TODAY state 4, the selected day, 7x7 nine-grid 1/1/1/1: black at alpha 0x26
		/// inside a one-pixel edge at 0x6B whose corners are 0x62 -- on white, #D9 inside and a
		/// darker rim that the today ring drawn over it leaves showing only at the corners.</summary>
		internal static Frame MonthCalSelected ()
		{
			var f = new Frame (7, 7) { Stretch = true, Margins = (1, 1, 1, 1) };
			for (int y = 0; y < 7; y++)
				for (int x = 0; x < 7; x++) {
					bool edgeX = x == 0 || x == 6, edgeY = y == 0 || y == 6;
					uint a = edgeX && edgeY ? 0x62u : edgeX || edgeY ? 0x6bu : 0x26u;
					f.Pixels [y * 7 + x] = a << 24;
				}
			return f;
		}

		/// <summary>PP_MOVEOVERLAY, 127x18 stretched: the glow that sweeps along a progress bar's
		/// fill -- green (#4DC94D) whose alpha falls away from the middle column like a bell, peak
		/// 153 and a spread of 20 pixels, a little stronger along its second row.</summary>
		internal static Frame ProgressMoveOverlay ()
		{
			if (s_moveOverlay != null)
				return s_moveOverlay;
			var f = new Frame (127, 18);
			for (int y = 0; y < 18; y++)
				for (int x = 0; x < 127; x++) {
					double d = x - 62.5 + 0.5;
					double a = 153 * Math.Exp (-d * d / (2 * 20.0 * 20.0)) * (y == 1 ? 201 / 152.0 : 1);
					int ia = Math.Min (255, (int) (a + 0.5));
					if (ia > 0)
						f.Pixels [y * 127 + x] = Premultiply (0x4dc94du, (uint) ia);
				}
			return s_moveOverlay = Stretched (f, 0, 0, 0, 0);
		}

		/// <summary>PP_PULSEOVERLAY, 42x18, sizing margins 21/20: a white sheen along the bottom six
		/// rows, fading in over the first dozen columns and out over the last ten.</summary>
		internal static Frame ProgressPulseOverlay ()
		{
			if (s_pulseOverlay != null)
				return s_pulseOverlay;
			int [] rows = { 12, 43, 70, 80, 90, 96 };
			var f = new Frame (42, 18);
			for (int y = 12; y < 18; y++)
				for (int x = 0; x < 42; x++) {
					double c = Math.Clamp ((x - 3.5) / 8.0, 0, 1) * Math.Clamp ((37 - x) / 9.0, 0, 1);
					int ia = (int) (rows [y - 12] * c + 0.5);
					if (ia > 0)
						f.Pixels [y * 42 + x] = Premultiply (0xffffffu, (uint) ia);
				}
			return s_pulseOverlay = Stretched (f, 21, 20, 0, 0);
		}

		static Frame s_moveOverlay, s_pulseOverlay;

		/// <summary>A frame blitted into <paramref name="bounds"/> and blended at a constant
		/// <paramref name="alpha"/> (0..255), as AlphaBlend's SourceConstantAlpha does.</summary>
		internal static void DrawFaded (Graphics g, Frame f, Rectangle bounds, int alpha)
		{
			if (bounds.Width <= 0 || bounds.Height <= 0 || alpha <= 0)
				return;
			Bitmap src = Blit (f, bounds.Width, bounds.Height);
			using var bmp = new Bitmap (bounds.Width, bounds.Height, PixelFormat.Format32bppArgb);
			BitmapData sd = src.LockBits (new Rectangle (0, 0, bounds.Width, bounds.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
			BitmapData dd = bmp.LockBits (new Rectangle (0, 0, bounds.Width, bounds.Height), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
			var row = new int [bounds.Width];
			for (int y = 0; y < bounds.Height; y++) {
				Marshal.Copy (sd.Scan0 + y * sd.Stride, row, 0, bounds.Width);
				for (int x = 0; x < row.Length; x++) {
					uint p = (uint) row [x];
					row [x] = (int) ((p & 0xffffffu) | (((p >> 24) * (uint) alpha + 127) / 255) << 24);
				}
				Marshal.Copy (row, 0, dd.Scan0 + y * dd.Stride, bounds.Width);
			}
			src.UnlockBits (sd);
			bmp.UnlockBits (dd);
			g.DrawImage (bmp, bounds.X, bounds.Y, bounds.Width, bounds.Height);
		}

		// ---- border-fill parts ---------------------------------------------------------------

		/// <summary>A BGTYPE BORDERFILL part: FILLCOLOR inside a BORDERSIZE frame of BORDERCOLOR,
		/// as the theme's property table gives them; null for a part that is not one.</summary>
		internal static (int size, uint border, uint fill)? BorderFill (string cls, int part, int state)
			=> cls switch {
				// EP_EDITTEXT and the other plain edit parts; the theme fills state 4 #B1CEED.
				"EDIT" when part >= 1 && part <= 5 => (1, 0xffabadb3u, part == 1 && state == 4 ? 0xffb1ceedu : 0xffffffffu),
				"SCROLLBAR" when part == 11 => (0, 0u, 0xfff0f0f0u),
				_ => null,
			};

		internal static void DrawBorderFill (Graphics g, (int size, uint border, uint fill) p, Rectangle r)
		{
			using (var fill = new SolidBrush (Color.FromArgb ((int) p.fill)))
				g.FillRectangle (fill, r);
			if (p.size <= 0)
				return;
			using var border = new SolidBrush (Color.FromArgb ((int) p.border));
			int n = Math.Min (p.size, Math.Min (r.Width, r.Height) / 2);
			g.FillRectangle (border, r.X, r.Y, r.Width, n);
			g.FillRectangle (border, r.X, r.Bottom - n, r.Width, n);
			g.FillRectangle (border, r.X, r.Y + n, n, r.Height - 2 * n);
			g.FillRectangle (border, r.Right - n, r.Y + n, n, r.Height - 2 * n);
		}

		/// <summary>The colour of a stretched frame's middle, when it is all one opaque colour, and
		/// how far that colour reaches: from the sizing margins, each side is pulled outward while
		/// the rows or columns it takes in are still that colour, so a caption that runs into the
		/// margins still stands on it.</summary>
		static (uint face, int l, int r, int t, int b)? Solid (Frame f)
		{
			var (l, r, t, b) = f.Margins;
			if (l + r >= f.Width || t + b >= f.Height)
				return null;
			uint c = f.Pixels [t * f.Width + l];
			if (c >> 24 != 255 || !Uniform (f, c, l, r, t, b))
				return null;
			bool grew = true;
			while (grew) {
				grew = false;
				if (l > 0 && Uniform (f, c, l - 1, r, t, b)) { l--; grew = true; }
				if (r > 0 && Uniform (f, c, l, r - 1, t, b)) { r--; grew = true; }
				if (t > 0 && Uniform (f, c, l, r, t - 1, b)) { t--; grew = true; }
				if (b > 0 && Uniform (f, c, l, r, t, b - 1)) { b--; grew = true; }
			}
			return (c, l, r, t, b);
		}

		static bool Uniform (Frame f, uint c, int l, int r, int t, int b)
		{
			for (int y = t; y < f.Height - b; y++)
				for (int x = l; x < f.Width - r; x++)
					if (f.Pixels [y * f.Width + x] != c)
						return false;
			return true;
		}

		static readonly Dictionary<(Frame, int, int), Bitmap> s_blits = new ();

		static Bitmap Blit (Frame f, int w, int h)
		{
			lock (s_blits) {
				if (s_blits.TryGetValue ((f, w, h), out Bitmap cached))
					return cached;
				if (s_blits.Count > 512) {
					foreach (Bitmap b in s_blits.Values)
						b.Dispose ();
					s_blits.Clear ();
				}
				var (l, r, t, bm) = f.Stretch ? f.Margins : (0, 0, 0, 0);
				var argb = new int [w * h];
				for (int y = 0; y < h; y++) {
					int sy = Source (y, h, f.Height, t, bm);
					for (int x = 0; x < w; x++) {
						uint p = f.Pixels [sy * f.Width + Source (x, w, f.Width, l, r)];
						argb [y * w + x] = (int) Straight (p);
					}
				}
				var bmp = new Bitmap (w, h, PixelFormat.Format32bppArgb);
				BitmapData data = bmp.LockBits (new Rectangle (0, 0, w, h), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
				for (int y = 0; y < h; y++)
					Marshal.Copy (argb, y * w, data.Scan0 + y * data.Stride, w);
				bmp.UnlockBits (data);
				s_blits [(f, w, h)] = bmp;
				return bmp;
			}
		}

		/// <summary>The source column (or row) destination pixel <paramref name="d"/> of
		/// <paramref name="size"/> takes: the margins one to one, the middle scaled.</summary>
		static int Source (int d, int size, int source, int lo, int hi)
		{
			if (lo + hi >= source)
				return d * source / size;
			if (lo + hi >= size) {
				// Smaller than its two margins: they share the space in proportion (the near one
				// rounded down) and nothing is left for the middle. Each margin is shrunk on its
				// pixel CENTRES, a tie going to the lower row -- measured through uxtheme on the
				// SPIN parts at 5..13 rows, where the up button's 9-row margin into 6 takes rows
				// 0 and 2 and the down button's 8 into 4 takes row 0.
				int near = lo * size / (lo + hi), far = size - near;
				return d < near ? ((2 * d + 1) * lo - 1) / (2 * Math.Max (1, near))
					: source - hi + ((2 * (d - near) + 1) * hi - 1) / (2 * Math.Max (1, far));
			}
			if (d < lo)
				return d;
			if (d >= size - hi)
				return source - (size - d);
			return lo + (d - lo) * (source - lo - hi) / (size - lo - hi);
		}

		static uint Straight (uint p)
		{
			uint a = p >> 24;
			if (a == 0)
				return 0;
			if (a == 255)
				return p;
			uint r = Math.Min (255u, (((p >> 16) & 0xff) * 255 + a / 2) / a);
			uint g = Math.Min (255u, (((p >> 8) & 0xff) * 255 + a / 2) / a);
			uint b = Math.Min (255u, ((p & 0xff) * 255 + a / 2) / a);
			return a << 24 | r << 16 | g << 8 | b;
		}

		// ---- the rasterizer ------------------------------------------------------------------

		// Sixteen samples on a rook lattice: sample k sits in column k and row 7k+11 (mod 16).
		static readonly float [] s_sx = new float [16], s_sy = new float [16];

		static Win11Frames ()
		{
			for (int k = 0; k < 16; k++) {
				s_sx [k] = (k + 0.5f) / 16f;
				s_sy [k] = ((7 * k + 11) % 16 + 0.5f) / 16f;
			}
		}

		internal delegate bool Shape (float x, float y);
		/// <summary>The straight (not premultiplied) 0xAARRGGBB colour a layer has at a point.</summary>
		internal delegate uint Paint (float x, float y);

		internal readonly struct Layer
		{
			public readonly Shape Shape;
			public readonly Paint Paint;
			public Layer (Shape shape, Paint paint) { Shape = shape; Paint = paint; }
			public Layer (Shape shape, uint colour) { Shape = shape; Paint = (x, y) => colour; }
		}

		/// <summary>Rasterizes the layers, in order, source-over, per sample; a pixel is the mean of
		/// its sixteen samples.</summary>
		internal static Frame Render (int width, int height, params Layer [] layers)
			=> RenderSampled (width, height, s_sx, s_sy, layers);

		// The sixteen sample positions of the check box and radio button art, in 64ths of a pixel:
		// solved from the theme's own images (the unchecked box, the unchecked ring and the checked
		// dot at once) rather than assumed. Not a rook lattice -- two samples share a column band --
		// and not D3D's standard pattern, both of which were tried and miss the art by far more.
		static readonly float [] s_markX = { 4, 5, 10, 14, 18, 16, 26, 33, 31, 39, 46, 47, 50, 54, 59, 60 };
		static readonly float [] s_markY = { 50, 22, 37, 2, 30, 58, 26, 52, 12, 44, 8, 33, 60, 28, 42, 12 };
		static readonly float [] s_msx = Array.ConvertAll (s_markX, v => v / 64f), s_msy = Array.ConvertAll (s_markY, v => v / 64f);

		internal static Frame RenderSampled (int width, int height, float [] s_sx, float [] s_sy, params Layer [] layers)
		{
			var f = new Frame (width, height);
			for (int y = 0; y < height; y++)
				for (int x = 0; x < width; x++) {
					float a = 0, r = 0, g = 0, b = 0;
					for (int k = 0; k < 16; k++) {
						float px = x + s_sx [k], py = y + s_sy [k];
						float sa = 0, sr = 0, sg = 0, sb = 0;
						foreach (Layer l in layers) {
							if (!l.Shape (px, py))
								continue;
							uint c = l.Paint (px, py);
							float ca = (c >> 24) / 255f;
							sr = ((c >> 16) & 0xff) * ca + sr * (1 - ca);
							sg = ((c >> 8) & 0xff) * ca + sg * (1 - ca);
							sb = (c & 0xff) * ca + sb * (1 - ca);
							sa = 255 * ca + sa * (1 - ca);
						}
						a += sa; r += sr; g += sg; b += sb;
					}
					f.Pixels [y * width + x] = Pack (a / 16, r / 16, g / 16, b / 16);
				}
			return f;
		}

		/// <summary>Rasterizes each layer to its own per-pixel coverage and composites the layers
		/// per PIXEL. Where two antialiased edges meet, this leaves the seam a little transparent
		/// (1 - (1-a)(1-b) rather than a + b) -- which the tab frames were drawn with: their inner
		/// corner, border meeting face, is not opaque.</summary>
		internal static Frame RenderPerPixel (int width, int height, params Layer [] layers)
		{
			var f = new Frame (width, height);
			for (int y = 0; y < height; y++)
				for (int x = 0; x < width; x++) {
					float a = 0, r = 0, g = 0, b = 0;
					foreach (Layer l in layers) {
						float la = 0, lr = 0, lg = 0, lb = 0;
						for (int k = 0; k < 16; k++) {
							float px = x + s_sx [k], py = y + s_sy [k];
							if (!l.Shape (px, py))
								continue;
							uint c = l.Paint (px, py);
							float ca = (c >> 24) / 255f;
							la += ca;
							lr += ((c >> 16) & 0xff) * ca;
							lg += ((c >> 8) & 0xff) * ca;
							lb += (c & 0xff) * ca;
						}
						float sa = la / 16;
						r = lr / 16 + r * (1 - sa);
						g = lg / 16 + g * (1 - sa);
						b = lb / 16 + b * (1 - sa);
						a = sa * 255 + a * (1 - sa);
					}
					f.Pixels [y * width + x] = Pack (a, r, g, b);
				}
			return f;
		}

		static uint Pack (float a, float r, float g, float b)
			=> (uint) (int) (a + 0.5f) << 24 | (uint) (int) (r + 0.5f) << 16 | (uint) (int) (g + 0.5f) << 8 | (uint) (int) (b + 0.5f);

		// ---- shapes --------------------------------------------------------------------------

		internal static Shape RoundRect (float x0, float y0, float x1, float y1, float radius)
			=> (px, py) => {
				if (px < x0 || px > x1 || py < y0 || py > y1)
					return false;
				float cx = Math.Min (Math.Max (px, x0 + radius), x1 - radius);
				float cy = Math.Min (Math.Max (py, y0 + radius), y1 - radius);
				float dx = px - cx, dy = py - cy;
				return dx * dx + dy * dy <= radius * radius;
			};

		/// <summary>A rectangle rounded at its two top corners only.</summary>
		internal static Shape RoundTop (float x0, float y0, float x1, float y1, float radius)
			=> (px, py) => {
				if (px < x0 || px > x1 || py < y0 || py > y1)
					return false;
				if (py >= y0 + radius)
					return true;
				float cx = Math.Min (Math.Max (px, x0 + radius), x1 - radius), cy = y0 + radius;
				return (px - cx) * (px - cx) + (py - cy) * (py - cy) <= radius * radius;
			};

		internal static Shape Box (float x0, float y0, float x1, float y1)
			=> (px, py) => px >= x0 && px < x1 && py >= y0 && py < y1;

		internal static Shape Circle (float cx, float cy, float radius)
			=> (px, py) => (px - cx) * (px - cx) + (py - cy) * (py - cy) <= radius * radius;

		/// <summary>A polyline stroked <paramref name="width"/> wide with round joins and caps.</summary>
		internal static Shape Stroke (float width, params float [] xy)
			=> (px, py) => {
				float h = width / 2;
				for (int i = 0; i + 3 < xy.Length; i += 2) {
					float ax = xy [i], ay = xy [i + 1], bx = xy [i + 2], by = xy [i + 3];
					float dx = bx - ax, dy = by - ay, len2 = dx * dx + dy * dy;
					float t = len2 == 0 ? 0 : Math.Clamp (((px - ax) * dx + (py - ay) * dy) / len2, 0, 1);
					float ex = ax + t * dx - px, ey = ay + t * dy - py;
					if (ex * ex + ey * ey <= h * h)
						return true;
				}
				return false;
			};

		internal static Shape Minus (Shape a, Shape b) => (px, py) => a (px, py) && !b (px, py);

		/// <summary>A colour that darkens toward the bottom: black laid over it at an alpha rising
		/// linearly from nothing at <paramref name="from"/> to <paramref name="alpha"/> at
		/// <paramref name="to"/> -- the shadow edge of a raised control.</summary>
		internal static Paint Shaded (uint colour, float from, float to, float alpha)
			=> (px, py) => {
				float t = Math.Clamp ((py - from) / (to - from), 0, 1) * alpha;
				uint r = (uint) Math.Round (((colour >> 16) & 0xff) * (1 - t));
				uint g = (uint) Math.Round (((colour >> 8) & 0xff) * (1 - t));
				uint b = (uint) Math.Round ((colour & 0xff) * (1 - t));
				return colour & 0xff000000 | r << 16 | g << 8 | b;
			};

		// ---- the parts -----------------------------------------------------------------------

		static readonly Dictionary<(int cls, int part, int state), Frame> s_cache = new ();

		const int Button = 1, ComboBox = 2, Edit = 3, Tab = 4, TrackBar = 5, TreeView = 6, ScrollBar = 7,
			Header = 8, Progress = 9, Spin = 10, Toolbar = 11, Status = 12, MonthCal = 13;

		/// <summary>The frame uxtheme would take for a part and state at 96 DPI, or null for a part
		/// that is not drawn from a frame.</summary>
		internal static Frame Get (string cls, int part, int state)
		{
			int c = cls switch { "BUTTON" => Button, "COMBOBOX" => ComboBox, "EDIT" => Edit, "TAB" => Tab, "TRACKBAR" => TrackBar, "TREEVIEW" => TreeView, "SCROLLBAR" => ScrollBar,
				"HEADER" => Header, "PROGRESS" => Progress, "SPIN" => Spin, "TOOLBAR" => Toolbar, "STATUS" => Status, "MONTHCAL" => MonthCal, _ => 0 };
			if (c == 0)
				return null;
			lock (s_cache) {
				if (s_cache.TryGetValue ((c, part, state), out Frame f))
					return f;
				f = c switch {
					Button => part switch {
						1 => PushButton (state),
						2 => RadioButton (state),
						3 => CheckBox (state),
						// BP_GROUPBOX: a one-pixel frame, its last row left clear (nine-grid 2/2/2/3,
						// border only).
						4 => Stretched (Render (5, 6, new Layer (Minus (Box (0, 0, 5, 5), Box (1, 1, 4, 4)), 0xffdcdcdcu)), 2, 2, 2, 3),
						_ => null,
					},
					ComboBox => part switch {
						1 => DropDownButton (state),
						4 => ComboBorder (state),
						5 => ComboReadOnly (state),
						6 or 7 => DropDownButtonSide (part, state),
						_ => null,
					},
					Edit => part >= 6 && part <= 9 ? EditBorder (state) : null,
					TrackBar => part switch {
						1 or 2 => Stretched (Render (3, 3, new Layer (Box (0, 0, 3, 3), 0xffd6d6d6u), new Layer (Box (1, 1, 2, 2), 0xffe7eaeau)), 1, 1, 1, 1),
						3 or 6 => Stretched (Render (6, 22, new Layer (Box (0, 0, 5, 21), Thumb (state, true))), 2, 3, part == 3 ? 9 : 5, part == 3 ? 9 : 5),
						4 => PointedThumb (11, 19, 0, 1, state),
						5 => PointedThumb (11, 19, 0, -1, state),
						7 => PointedThumb (19, 11, -1, 0, state),
						8 => PointedThumb (19, 11, 1, 0, state),
						_ => null,
					},
					TreeView => part == 2 ? TreeGlyph (state == 2) : null,
					Header => part == 1 ? HeaderItem (state) : null,
					Progress => part switch {
						1 => Stretched (ProgressTrack (19, 14), 9, 9, 7, 6),
						2 => Stretched (ProgressTrack (17, 19), 10, 6, 9, 9),
						// TRANSPARENTBAR(VERT): the track again, with a narrower frame for its
						// partially-transparent state.
						11 => state == 2 ? Stretched (ProgressTrack (7, 17), 3, 3, 10, 6) : Stretched (ProgressTrack (19, 14), 9, 9, 7, 6),
						12 => state == 2 ? Stretched (ProgressTrack (17, 7), 10, 6, 3, 3) : Stretched (ProgressTrack (17, 19), 10, 6, 9, 9),
						3 => Stretched (Render (12, 12, new Layer (Box (0, 0, 12, 12), 0xff06b025u)), 0, 0, 6, 5),
						4 => Stretched (Render (12, 12, new Layer (Box (0, 0, 12, 12), 0xff06b025u)), 6, 5, 0, 0),
						5 => ProgressFill (false, state),
						6 => ProgressFill (true, state),
						_ => null,
					},
					Spin => part >= 1 && part <= 4 ? SpinButton (part, state) : null,
					Toolbar => part == 1 || part == 2 ? ToolbarButton (state) : null,
					Status => part switch {
						// The bar: #F0F0F0 under a #D7D7D7 rule along its top (nine-grid 1/1/2/1).
						0 => Stretched (Render (3, 4, new Layer (Box (0, 0, 3, 4), 0xfff0f0f0u), new Layer (Box (0, 0, 3, 1), 0xffd7d7d7u)), 1, 1, 2, 1),
						// SP_PANE: a one-pixel divider down its right edge, clear on the last row.
						1 => Stretched (Render (2, 2, new Layer (Box (1, 0, 2, 1), 0xffd7d7d7u)), 0, 1, 0, 1),
						3 => SizeBox (),
						_ => null,
					},
					ScrollBar => part switch {
						1 => ScrollArrow (state),
						2 => Stretched (ScrollThumb (false, state), 11, 8, 8, 8),
						3 => Stretched (ScrollThumb (true, state), 8, 8, 5, 5),
						4 or 5 => Stretched (Render (1, 8, new Layer (Box (0, 0, 1, 8), ScrollFace), new Layer (Box (0, 0, 1, 1), 0xffffffffu)), 0, 0, 4, 3),
						6 or 7 => Stretched (Render (14, 1, new Layer (Box (0, 0, 14, 1), ScrollFace), new Layer (Box (0, 0, 1, 1), 0xffffffffu)), 10, 3, 0, 0),
						8 => new Frame (10, 9),
						9 => new Frame (9, 10),
						10 => SizeBox (),
						_ => null,
					},
					MonthCal => part == 5 && (state == 5 || state == 6) ? MonthCalToday ()
						: part == 5 && state == 4 ? MonthCalSelected () : null,
					Tab => part switch {
						>= 1 and <= 8 => TabItem (part, state),
						9 => TabPane (),
						10 => Stretched (Render (1, 1, new Layer (Box (0, 0, 1, 1), 0xfff9f9f9u)), 0, 0, 0, 0),
						_ => null,
					},
					_ => null,
				};
				s_cache [(c, part, state)] = f;
				return f;
			}
		}

		/// <summary>BP_PUSHBUTTON, 13x11, nine-grid 6/6/5/5: a rounded border over a face, the
		/// border's bottom row shaded on the raised states.</summary>
		internal static Frame PushButton (int state)
		{
			(uint border, uint face, bool raised) = state switch {
				2 => (0xff0078d4u, 0xffe0eef9u, true),   // hot
				3 => (0xff005499u, 0xffcce4f7u, false),  // pressed
				4 => (0xffe9e9e9u, 0xfff9f9f9u, false),  // disabled
				5 or 6 => (0xff0078d4u, 0xfffdfdfdu, true), // default, default-animating
				_ => (0xffd0d0d0u, 0xfffdfdfdu, true),   // normal
			};
			Paint edge = raised ? Shaded (border, 9, 10, 0.21f) : (x, y) => border;
			Frame f = Render (13, 11,
				new Layer (RoundRect (1, 1, 12, 10, 4), edge),
				new Layer (RoundRect (2, 2, 11, 9, 3), face));
			f.Stretch = true;
			f.Margins = (6, 6, 5, 5);
			return f;
		}

		/// <summary>BP_CHECKBOX, 13x13, true size: a rounded box, and on the marked states the box
		/// filled with the accent under a white tick, dash or cross.</summary>
		internal static Frame CheckBox (int state)
		{
			int mark = (state - 1) / 4;     // unchecked, checked, mixed, implicit, excluded
			int look = (state - 1) % 4;     // normal, hot, pressed, disabled
			if (mark == 3)
				mark = 1;                   // an implicit check draws as a check
			var layers = new List<Layer> ();
			if (mark == 0) {
				(uint border, uint face) = Unmarked (look);
				layers.Add (new Layer (RoundRect (0, 0, 13, 13, 3.1211f), border));
				layers.Add (new Layer (RoundRect (1, 1, 12, 12, 2), face));
			} else {
				layers.Add (new Layer (RoundRect (0, 0, 13, 13, 3.1211f), Marked (look)));
				Shape glyph = mark switch {
					// Solved under the art's own sample pattern (s_markX/Y): the tick is within 15
					// levels of the theme image over its whole area, the dash within 1.
					1 => Stroke (0.7031f, 3.5157f, 6.7891f, 5.4844f, 8.8281f, 9.6172f, 4.7266f),
					2 => Stroke (0.7031f, 4.2109f, 6.6641f, 8.7578f, 6.6719f),
					_ => (x, y) => Stroke (0.75f, 4.25f, 4.25f, 8.75f, 8.75f) (x, y) || Stroke (0.75f, 8.75f, 4.25f, 4.25f, 8.75f) (x, y),
				};
				uint ink = 0xffffffffu;
				if (look == 2 && mark == 1) {
					// Pressed, the mark is thinner and not quite opaque: white at 0.835 (tick) and
					// 0.761 (dash) over the pressed accent.
					glyph = Stroke (0.5078f, 3.375f, 6.7891f, 5.5234f, 8.8906f, 9.6719f, 4.7266f);
					ink = (uint) Math.Round (0.8352 * 255) << 24 | 0xffffffu;
				} else if (look == 2 && mark == 2) {
					glyph = Stroke (0.6406f, 4.1328f, 6.6016f, 8.8125f, 6.5859f);
					ink = (uint) Math.Round (0.7609 * 255) << 24 | 0xffffffu;
				}
				layers.Add (new Layer (glyph, ink));
			}
			return RenderSampled (13, 13, s_msx, s_msy, layers.ToArray ());
		}

		/// <summary>BP_RADIOBUTTON, 13x13, true size: a ring, or the accent disc under a white dot
		/// that grows on hover and shrinks while pressed. Pressing an unchecked one shows the dot
		/// it is about to get.</summary>
		internal static Frame RadioButton (int state)
		{
			bool check = state > 4;
			int look = (state - 1) % 4;
			var layers = new List<Layer> ();
			const float outer = 6.4883f;
			if (!check) {
				(uint border, uint face) = Unmarked (look);
				layers.Add (new Layer (Circle (6.5f, 6.5f, outer), border));
				layers.Add (new Layer (Circle (6.5f, 6.5f, 5.4883f), face));
				if (look == 2)
					layers.Add (new Layer (Circle (6.5f, 6.5f, 1.9844f), 0xffffffffu));
			} else {
				float dot = look switch { 1 => 3.2578f, 2 => 1.9766f, _ => 2.5195f };
				layers.Add (new Layer (Circle (6.5f, 6.5f, outer), Marked (look)));
				layers.Add (new Layer (Circle (6.5f, 6.5f, dot), 0xffffffffu));
			}
			return RenderSampled (13, 13, s_msx, s_msy, layers.ToArray ());
		}

		/// <summary>A bordered field: the outer rounded rectangle is the border, its bottom
		/// <paramref name="band"/> rows in <paramref name="bottom"/>, and the face a rounded
		/// rectangle one pixel in (and <paramref name="faceBottom"/> up from the bottom).</summary>
		static Frame Field (int w, int h, float r0, float r1, uint border, uint bottom, float band, uint face, float faceBottom)
			=> Render (w, h,
				new Layer (RoundRect (0, 0, w, h, r0), (x, y) => y >= h - band ? bottom : border),
				new Layer (RoundRect (1, 1, w - 1, h - faceBottom, r1), face));

		static Frame Stretched (Frame f, int l, int r, int t, int b)
		{
			f.Stretch = true;
			f.Margins = (l, r, t, b);
			return f;
		}

		/// <summary>The four looks the combo box's buttons share: normal, hot, pressed, disabled.</summary>
		static (uint border, uint bottom, uint face) ComboLook (int state) => state switch {
			2 => (0xff0078d4u, 0xff006cbeu, 0xffe5f1fbu),
			3 => (0xff005fb7u, 0xff005fb7u, 0xffcce4f7u),
			4 => (0xffeaeaeau, 0xffeaeaeau, 0xfffafafau),
			_ => (0xffd2d2d2u, 0xffbcbcbcu, 0xfffdfdfdu),
		};

		/// <summary>The face colour of the combo box's buttons in a state.</summary>
		internal static uint ComboFace (int state) => ComboLook (state).face;

		/// <summary>CP_DROPDOWNBUTTON, 7x21 nine-grid 3/3/7/8, with the chevron over it.</summary>
		internal static Frame DropDownButton (int state)
		{
			var (border, bottom, face) = ComboLook (state);
			Frame f = Stretched (ExactRing (7, 21, 1.9375f, 0.875f, border, bottom, face), 3, 3, 7, 8);
			f.Glyph = Chevron (state);
			return f;
		}

		/// <summary>CP_READONLY, the face of a drop-down list: 7x21 nine-grid 3/3/4/4.</summary>
		internal static Frame ComboReadOnly (int state)
		{
			var (border, bottom, face) = ComboLook (state);
			return Stretched (ExactRing (7, 21, 1.9375f, 0.875f, border, bottom, face), 3, 3, 4, 4);
		}

		/// <summary>CP_DROPDOWNBUTTONRIGHT/LEFT, the button inside an editable combo box: nothing
		/// but the chevron until the pointer is on it, then an accent-framed face.</summary>
		internal static Frame DropDownButtonSide (int part, int state)
		{
			int w = part == 6 ? 5 : 6;
			Frame f = state == 2 || state == 3
				? Field (w, 23, 2, 1, ComboLook (state).border, ComboLook (state).border, 0, ComboLook (state).face, 1)
				: new Frame (w, 23);
			Stretched (f, part == 6 ? 2 : 3, 2, 7, 8);
			f.Glyph = Chevron (state);
			return f;
		}

		/// <summary>CP_BORDER (and DP_DATEBORDER, the same art), 5x5 nine-grid 2/2/2/2, premultiplied:
		/// a one-pixel ring with rounded corners over the face. The exact-area ring this used to be
		/// computed as reproduces three states to the level and misses the disabled one's inner
		/// corners by one (#F5 where the theme has #F4) -- each state's rounding is its own, so the
		/// four are written out.</summary>
		internal static Frame ComboBorder (int state)
		{
			uint [] px = state switch {
				2 => new uint [] {
					0x57303030u, 0xec828282u, 0xff8d8d8du, 0xec828282u, 0x57303030u,
					0xec828282u, 0xffe8e8e8u, 0xfffcfcfcu, 0xffe8e8e8u, 0xec828282u,
					0xff8d8d8du, 0xfffcfcfcu, 0xfffcfcfcu, 0xfffcfcfcu, 0xff8d8d8du,
					0xec828282u, 0xffe8e8e8u, 0xfffcfcfcu, 0xffe8e8e8u, 0xec828282u,
					0x57303030u, 0xec828282u, 0xff8d8d8du, 0xec828282u, 0x57303030u,
				},
				3 => new uint [] {
					0x57002948u, 0xec006fc4u, 0xff0078d4u, 0xec006fc4u, 0x57002948u,
					0xec006fc4u, 0xffd2e7f7u, 0xffffffffu, 0xffd2e7f7u, 0xec006fc4u,
					0xff0078d4u, 0xffffffffu, 0xffffffffu, 0xffffffffu, 0xff0078d4u,
					0xec006fc4u, 0xffd2e7f7u, 0xffffffffu, 0xffd2e7f7u, 0xec006fc4u,
					0x57002948u, 0xec006fc4u, 0xff0078d4u, 0xec006fc4u, 0x57002948u,
				},
				4 => new uint [] {
					0x57444444u, 0xecb9b9b9u, 0xffc8c8c8u, 0xecb9b9b9u, 0x57444444u,
					0xecb9b9b9u, 0xfff4f4f4u, 0xfffefefeu, 0xfff4f4f4u, 0xecb9b9b9u,
					0xffc8c8c8u, 0xfffefefeu, 0xfffefefeu, 0xfffefefeu, 0xffc8c8c8u,
					0xecb9b9b9u, 0xfff4f4f4u, 0xfffefefeu, 0xfff4f4f4u, 0xecb9b9b9u,
					0x57444444u, 0xecb9b9b9u, 0xffc8c8c8u, 0xecb9b9b9u, 0x57444444u,
				},
				_ => new uint [] {
					0x57303030u, 0xec828282u, 0xff8d8d8du, 0xec828282u, 0x57303030u,
					0xec828282u, 0xffebebebu, 0xffffffffu, 0xffebebebu, 0xec828282u,
					0xff8d8d8du, 0xffffffffu, 0xffffffffu, 0xffffffffu, 0xff8d8d8du,
					0xec828282u, 0xffebebebu, 0xffffffffu, 0xffebebebu, 0xec828282u,
					0x57303030u, 0xec828282u, 0xff8d8d8du, 0xec828282u, 0x57303030u,
				},
			};
			var f = new Frame (5, 5);
			Array.Copy (px, f.Pixels, 25);
			return Stretched (f, 2, 2, 2, 2);
		}

		/// <summary>A rounded rectangle the size of the frame in <paramref name="border"/>, with a
		/// rounded rectangle a pixel inside it in <paramref name="face"/>, each pixel the exact area
		/// each covers -- the colour mixed by area, premultiplied by the alpha after that is rounded.</summary>
		static Frame ExactRing (int w, int h, float outer, float inner, uint border, uint face)
			=> ExactRing (w, h, outer, inner, border, border, face);

		/// <summary>As above, the border's last row in <paramref name="bottom"/>.</summary>
		static Frame ExactRing (int w, int h, float outer, float inner, uint border, uint bottom, uint face)
		{
			List<PointF> o = RoundRectPolygon (0, 0, w, h, outer), i = RoundRectPolygon (1, 1, w - 1, h - 1, inner);
			var f = new Frame (w, h);
			for (int y = 0; y < h; y++)
				for (int x = 0; x < w; x++) {
					double co = Math.Abs (ClippedArea (o, x, y, x + 1, y + 1));
					double ci = Math.Min (co, Math.Abs (ClippedArea (i, x, y, x + 1, y + 1)));
					int a = (int) (co * 255 + 0.5);
					if (a == 0)
						continue;
					uint px = (uint) a << 24;
					for (int sh = 0; sh < 24; sh += 8) {
						uint edge = y == h - 1 ? bottom : border;
						double straight = (((edge >> sh) & 0xff) * (co - ci) + ((face >> sh) & 0xff) * ci) / co;
						px |= (uint) Math.Min (255, (int) (straight * a / 255 + 0.5)) << sh;
					}
					f.Pixels [y * w + x] = px;
				}
			return f;
		}

		static List<PointF> RoundRectPolygon (float x0, float y0, float x1, float y1, float r)
		{
			var pts = new List<PointF> ();
			if (r <= 0) {
				pts.Add (new PointF (x0, y0)); pts.Add (new PointF (x1, y0)); pts.Add (new PointF (x1, y1)); pts.Add (new PointF (x0, y1));
				return pts;
			}
			(float cx, float cy, float a0) [] corners = { (x1 - r, y0 + r, -90), (x1 - r, y1 - r, 0), (x0 + r, y1 - r, 90), (x0 + r, y0 + r, 180) };
			foreach (var (cx, cy, a0) in corners)
				for (int k = 0; k <= 64; k++) {
					double t = (a0 + 90.0 * k / 64) * Math.PI / 180;
					pts.Add (new PointF ((float) (cx + r * Math.Cos (t)), (float) (cy + r * Math.Sin (t))));
				}
			return pts;
		}

		/// <summary>EP_EDITBORDER_*, 5x5 nine-grid 2/2/2/2, premultiplied: a pale frame over a darker
		/// bottom line, which turns into two rows of the accent while the field has the focus. The
		/// frame is NOT symmetric: the column inside the right edge is a shade darker than the one
		/// inside the left (#F7 against #FE at rest) and the right edge is #E6 where the left is #EC,
		/// so a field's right side reads a little heavier. Written out per state (normal, hot,
		/// focused, disabled) because no symmetric construction produces it.</summary>
		internal static Frame EditBorder (int state)
		{
			uint [] px = state switch {
				2 => new uint [] {
				0x4f494949u, 0xe6d5d5d5u, 0xffecececu, 0xe7cfcfcfu, 0x54474747u,
				0xebd9d9d9u, 0xfff7f7f7u, 0xfffafafau, 0xfff0f0f0u, 0xecd4d4d4u,
				0xffecececu, 0xfffafafau, 0xfffafafau, 0xfff3f3f3u, 0xffe6e6e6u,
				0xefddddddu, 0xffdadadau, 0xfffafafau, 0xffd9d9d9u, 0xf0cad1d6u,
				0x5a535353u, 0xf68a8a8au, 0xff838383u, 0xf68a8a8au, 0x644e545au,
				},
				3 => new uint [] {
				0x514b4b4bu, 0xe9d8d8d8u, 0xffecececu, 0xe9d8d8d8u, 0x5c464d52u,
				0xebd9d9d9u, 0xfffbfbfbu, 0xffffffffu, 0xfffbfbfbu, 0xecccd2d8u,
				0xffecececu, 0xffffffffu, 0xffffffffu, 0xffffffffu, 0xffdde4e9u,
				0xff0067c0u, 0xff0067c0u, 0xff0067c0u, 0xff0067c0u, 0xff0067c0u,
				0xe40d63aeu, 0xff0067c0u, 0xff0067c0u, 0xff0067c0u, 0xe40d63aeu,
				},
				4 => new uint [] {
				0x524b4b4cu, 0xecd9dadau, 0xffecececu, 0xecd9dadau, 0x524b4b4cu,
				0xecdadadau, 0xfff8f8f8u, 0xfffbfbfbu, 0xfff8f8f8u, 0xecdadadau,
				0xffecececu, 0xfffbfbfbu, 0xfffbfbfbu, 0xfffbfbfbu, 0xffecececu,
				0xeddbdbdbu, 0xfff8f8f8u, 0xfffbfbfbu, 0xfff8f8f8u, 0xeddbdbdbu,
				0x57515151u, 0xecdadadau, 0xffedededu, 0xecdadadau, 0x57515151u,
				},
				_ => new uint [] {
				0x4d474747u, 0xe3d2d2d2u, 0xffecececu, 0xe4cdcdcdu, 0x52454545u,
				0xead9d9d9u, 0xfffafafau, 0xfffefefeu, 0xfff3f3f3u, 0xebd3d3d3u,
				0xffecececu, 0xfffefefeu, 0xfffefefeu, 0xfff7f7f7u, 0xffe6e6e6u,
				0xefddddddu, 0xffddddddu, 0xfffefefeu, 0xffddddddu, 0xefd8d8d8u,
				0x5b545454u, 0xf88b8b8bu, 0xff838383u, 0xf88b8b8bu, 0x5b545454u,
				},
			};
			var f = new Frame (5, 5);
			Array.Copy (px, f.Pixels, 25);
			return Stretched (f, 2, 2, 2, 2);
		}

		/// <summary>TABP_TABITEM and its edge variants, 5x19 (7x19 for the right-edge ones) nine-grid
		/// 2/2/13/4: rounded at the top only, and neighbouring tabs share a border, so a plain item
		/// has none on its left. The border is a ring round the face, composited per pixel. States:
		/// normal, hot, selected (both sides bordered, the bottom row opening into the pane), disabled,
		/// focused.</summary>
		internal static Frame TabItem (int part, int state)
		{
			(uint border, uint face) = state switch {
				2 => (0xffe5e5e5u, 0xffedededu),
				3 => (0xffe5e5e5u, 0xfff9f9f9u),
				4 => (0xffd9d9d9u, 0xffecececu),
				5 => (0xff0078d7u, 0xffffffffu),
				_ => (0xffe5e5e5u, 0xfff3f3f3u),
			};
			bool leftEdge = part == 2 || part == 6, rightEdge = part == 3 || part == 7;
			bool selected = state == 3;
			bool left = selected || leftEdge;
			int w = rightEdge ? 7 : 5;
			float x0 = left ? 0 : -6, x1 = 5;
			Shape outer = RoundTop (x0, 0, x1, 30, 2), inner = RoundTop (x0 + 1, 1, x1 - 1, 30, 1.125f);
			Frame f = RenderPerPixel (w, 19, new Layer (Minus (outer, inner), border), new Layer (inner, face));
			if (selected)
				for (int x = 0; x < 5; x++)
					f.Pixels [18 * w + x] = (leftEdge && x == 0) || (rightEdge && x == 4) ? border : face;
			return Stretched (f, 2, rightEdge ? 4 : 2, 13, 4);
		}

		/// <summary>TABP_PANE, 5x4 nine-grid 1/3/1/2: a one-pixel frame round the page face, with
		/// the tab control's own face showing in the two columns right of it and the row below.</summary>
		static Frame TabPane ()
			=> Stretched (Render (5, 4,
				new Layer (Box (0, 0, 5, 4), 0xfff3f3f3u),
				new Layer (Box (0, 0, 3, 3), 0xffe5e5e5u),
				new Layer (Box (1, 1, 2, 2), 0xfff9f9f9u)), 1, 3, 1, 2);

		const uint ScrollFace = 0xfff0f0f0u;

		/// <summary>SBP_ARROWBTN: a 17x17 tile of the track (nine-grid 8/8/8/8) with the arrow as a
		/// glyph over it. At rest the arrow is not drawn at all; hot, pressed, disabled and the
		/// hover states show a soft-cornered triangle of translucent black, each its own size.
		/// The states run up, down, left, right in fours (normal, hot, pressed, disabled), then the
		/// four hover states.</summary>
		static Frame ScrollArrow (int state)
		{
			int i = state - 1;
			int dir = i < 16 ? i / 4 : i - 16, look = i < 16 ? i % 4 : 4;
			// The track's white edge: down the left of a vertical bar's buttons, along the top of
			// a horizontal one's.
			Shape edge = dir < 2 ? Box (0, 0, 1, 17) : Box (0, 0, 17, 1);
			Frame f = Stretched (Render (17, 17, new Layer (Box (0, 0, 17, 17), ScrollFace), new Layer (edge, 0xffffffffu)), 8, 8, 8, 8);
			if (look > 0) {
				f.Glyph = s_arrowsExact.TryGetValue (state, out var exact) ? ArrowExact (dir, 13, exact)
					: Arrow (dir, 13, s_arrows [dir, look - 1]);
				// The glyph is picked by the button's smaller side (IMAGESELECTTYPE SIZE): the theme's
				// MINSIZE1..3 give 13 px below 21, 16 px from 21, 20 px from 26; its fourth image, 32
				// px, carries no MINSIZE and uxtheme takes it from 32 (the 39 and 52 px ones are never
				// taken up to 80, measured).
				f.LargerGlyphs = new [] { (21, Arrow (dir, 16, s_arrows16 [dir, look - 1])),
							  (26, Arrow (dir, 20, s_arrows20 [dir, look - 1])),
							  (32, Arrow (dir, 32, s_arrows32 [dir, look - 1])) };
			}
			return f;
		}

		// The 13-pixel glyphs as exact-area rounded triangles (ArrowExact), by SBP_ARROWBTN state:
		// cx, apex, base, half-width, rounding and alpha, fitted to each state's image in its own
		// up-pointing frame. The theme's arrows are not one shape turned four ways -- the up arrow
		// sits lower in its frame than the others -- so each state has its own.
		static readonly Dictionary<int, (float cx, float apex, float bse, float hw, float r, float a)> s_arrowsExact = new () {
			{ 2, (7.0067f, 4.6446f, 9.1335f, 3.3488f, 1.2494f, 0.5783f) },
			{ 3, (7.0033f, 3.3524f, 9.5647f, 4.1248f, 0.0601f, 0.5758f) },
			{ 4, (7.0014f, 4.6894f, 8.9636f, 3.0488f, 1.041f, 0.3171f) },
			{ 6, (7.0007f, 3.439f, 8.218f, 3.4148f, 1.166f, 0.5755f) },
			{ 7, (6.9926f, 4.2855f, 7.62f, 2.5323f, 1.0144f, 0.5715f) },
			{ 8, (7.0038f, 3.7203f, 7.9402f, 3.0029f, 1.0633f, 0.3171f) },
			{ 10, (7.0003f, 3.4365f, 8.187f, 3.3997f, 1.1884f, 0.5772f) },
			{ 11, (7.0001f, 4.1498f, 7.7007f, 2.6621f, 0.9254f, 0.5762f) },
			{ 12, (7.0069f, 4.0129f, 7.8686f, 2.927f, 1.137f, 0.3178f) },
			{ 14, (6.9917f, 3.6447f, 8.1573f, 3.36f, 1.2195f, 0.577f) },
			{ 15, (7.002f, 4.2508f, 7.6448f, 2.5576f, 0.9837f, 0.5759f) },
			{ 16, (7.0075f, 4.0973f, 7.8453f, 2.9338f, 1.1589f, 0.319f) },
			{ 17, (6.9983f, 4.7767f, 8.9188f, 2.9754f, 1.0841f, 0.4479f) },
			{ 18, (6.9999f, 3.7229f, 7.9468f, 3.0319f, 1.0557f, 0.4468f) },
			{ 19, (7.0107f, 3.9429f, 7.9005f, 2.9773f, 1.1006f, 0.4484f) },
			{ 20, (7.003f, 3.6502f, 7.9866f, 3.0914f, 1.0115f, 0.4478f) },
		};

		// Apex, base, half-width, corner radius, alpha and centre of each arrow, by direction and
		// look (hot, pressed, disabled, hover), fitted in the arrow's own up-pointing frame: the
		// 13-pixel arrow, then the 16-, 20- and 32-pixel ones, which are not the small one scaled.
		static readonly (float apex, float bse, float hw, float r, float a, float cx) [,] s_arrows = {
			{ (2.8125f, 10.075f, 4.875f, 0.275f, 0.5781f, 7.0625f), (3.6719f, 9.45f, 4.1094f, 0.15f, 0.5781f, 7f),
			  (3.625f, 9.6375f, 4.375f, 0.3375f, 0.3203f, 7f), (3.8438f, 9.5125f, 4.0625f, 0.4781f, 0.4453f, 7.0625f) },
			{ (2.6719f, 8.7f, 4.375f, 0.65f, 0.5781f, 7f), (2.6719f, 8.45f, 4.1094f, 0.15f, 0.5781f, 7f),
			  (2.625f, 8.6375f, 4.375f, 0.3375f, 0.3203f, 7f), (1.8125f, 8.95f, 4.8125f, 0f, 0.4531f, 7.0625f) },
			{ (2.625f, 8.7f, 4.3438f, 0.65f, 0.5781f, 7.0234f), (2.5f, 8.6375f, 4.4375f, 0f, 0.5781f, 7.0625f),
			  (2.5625f, 8.6375f, 4.3125f, 0.3375f, 0.3203f, 7.0625f), (2.3125f, 8.8875f, 4.9219f, 0.0875f, 0.4453f, 7.0625f) },
			{ (2.625f, 8.7f, 4.3438f, 0.65f, 0.5781f, 6.9766f), (2.5f, 8.6375f, 4.4375f, 0f, 0.5781f, 6.9375f),
			  (2.5625f, 8.6375f, 4.3125f, 0.3375f, 0.3203f, 6.9375f), (2.3125f, 8.8875f, 4.9219f, 0.0875f, 0.4453f, 6.9375f) },
		};

		static readonly (float apex, float bse, float hw, float r, float a, float cx) [,] s_arrows16 = {
			{ (3.8678f, 12.2125f, 5.625f, 0.3385f, 0.5781f, 8.5048f), (4.863f, 11.3808f, 4.4327f, 0.0596f, 0.5781f, 8.4904f),
			  (4.6412f, 11.674f, 4.8221f, 0.4154f, 0.3203f, 8.5529f), (4.7308f, 11.4577f, 4.4766f, 0.5884f, 0.4453f, 8.4423f) },
			{ (3.0385f, 10.9577f, 5.1346f, 0.55f, 0.5781f, 8.4904f), (3.726f, 10.4625f, 4.4952f, 0f, 0.5781f, 8.4748f),
			  (3.2308f, 10.8183f, 5.2596f, 0.1654f, 0.3203f, 8.4904f), (2.6995f, 11.0154f, 5.4856f, 0f, 0.4531f, 8.5048f) },
			{ (2.9183f, 11.0202f, 4.9712f, 0.55f, 0.5781f, 8.3942f), (3.7175f, 10.4433f, 4.4615f, 0f, 0.5781f, 8.5048f),
			  (3.0601f, 10.8495f, 5.1827f, 0.1654f, 0.3203f, 8.4423f), (2.7837f, 10.9697f, 5.4952f, 0f, 0.4531f, 8.4736f) },
			{ (3.1526f, 10.9889f, 5.1353f, 0.55f, 0.5781f, 8.5241f), (3.7097f, 10.4433f, 4.4928f, 0f, 0.5781f, 8.5385f),
			  (3.0288f, 10.8105f, 5.1202f, 0.1654f, 0.3203f, 8.5385f), (2.7837f, 11.0166f, 5.6202f, 0f, 0.4453f, 8.5072f) },
		};

		static readonly (float apex, float bse, float hw, float r, float a, float cx) [,] s_arrows20 = {
			{ (5.5144f, 16.0625f, 7.3125f, 0f, 0.5781f, 10.6154f), (7.2116f, 14.851f, 5.5097f, 0f, 0.5781f, 10.5505f),
			  (5.9832f, 15.5301f, 6.6839f, 0.0192f, 0.3203f, 10.5192f), (6.8354f, 15.0721f, 5.6563f, 0.4855f, 0.4453f, 10.5998f) },
			{ (4.2981f, 13.5096f, 6.4495f, 0.5f, 0.5781f, 10.5192f), (5.1966f, 12.875f, 5.4472f, 0f, 0.5781f, 10.5192f),
			  (4.6635f, 13.226f, 6.2308f, 0.3005f, 0.3203f, 10.5192f), (4.2728f, 13.488f, 6.7788f, 0f, 0.4453f, 10.4904f) },
			{ (4.1635f, 13.4784f, 6.3078f, 0.5f, 0.5781f, 10.4927f), (5.1665f, 12.851f, 5.5144f, 0f, 0.5781f, 10.4904f),
			  (4.5986f, 13.2885f, 6.1346f, 0.2692f, 0.3203f, 10.6154f), (4.0733f, 13.5325f, 6.8847f, 0f, 0.4453f, 10.4904f) },
			{ (5.101f, 14.494f, 6.339f, 0.5f, 0.5781f, 10.4832f), (5.7837f, 13.851f, 5.2644f, 0f, 0.5781f, 10.5481f),
			  (5.1298f, 14.476f, 6.8534f, 0f, 0.3203f, 10.5168f), (4.9249f, 14.4934f, 6.6972f, 0f, 0.4453f, 10.4856f) },
		};

		static readonly (float apex, float bse, float hw, float r, float a, float cx) [,] s_arrows32 = {
			{ (4.7668f, 22.55f, 12.3438f, 0f, 0.5781f, 17.0096f), (7.9135f, 20.449f, 8.9279f, 0f, 0.5781f, 16.9808f),
			  (6.3606f, 21.4731f, 10.9411f, 0.0808f, 0.3203f, 17.0433f), (6.4929f, 21.4154f, 10.8594f, 0.1769f, 0.4453f, 17.0096f) },
			{ (6.827f, 21.3763f, 10.2849f, 1.1f, 0.5781f, 16.9886f), (8.1864f, 20.3f, 8.7404f, 0.1192f, 0.5781f, 16.9808f),
			  (6.649f, 21.199f, 10.3317f, 0.3308f, 0.3203f, 16.9808f), (5.8678f, 21.5308f, 10.9087f, 0f, 0.4453f, 17.0096f) },
			{ (6.8209f, 21.3841f, 10.2237f, 1.1f, 0.5781f, 17.0071f), (7.982f, 20.449f, 8.9543f, 0f, 0.5781f, 17.0252f),
			  (6.6749f, 21.1678f, 10.3966f, 0.3308f, 0.3203f, 17.0096f), (6.2236f, 21.5097f, 11.1155f, 0f, 0.4453f, 17.0096f) },
			{ (6.7897f, 21.4154f, 10.1924f, 1.1f, 0.5781f, 16.9857f), (8.0288f, 20.449f, 9.0481f, 0f, 0.5781f, 17.0144f),
			  (6.7452f, 21.1678f, 10.4279f, 0.3308f, 0.3203f, 16.9519f), (6.2079f, 21.5019f, 11.0842f, 0f, 0.4453f, 16.9597f) },
		};

		static Frame Arrow (int dir, int n, (float apex, float bse, float hw, float r, float a, float cx) t)
		{
			Shape up = Triangle (t.cx, t.apex, t.bse, t.hw, t.r);
			Shape shape = dir switch {
				1 => (x, y) => up (x, n - y),
				2 => (x, y) => up (y, x),
				3 => (x, y) => up (y, n - x),
				_ => up,
			};
			return Render (n, n, new Layer (shape, (uint) Math.Round (t.a * 255) << 24));
		}

		/// <summary>SBP_ARROWBTN's 13-pixel glyph as the theme holds it: EXACT-AREA art (its
		/// partial pixels are not sixteenths of the full one), a triangle -- apex at (cx, apex),
		/// base on y = bse, hw either side, in the up-pointing frame -- grown by r (Minkowski, which
		/// rounds its corners), at alpha a, turned to face <paramref name="dir"/>.</summary>
		static Frame ArrowExact (int dir, int n, (float cx, float apex, float bse, float hw, float r, float a) t)
		{
			var tri = new [] { new PointF (t.cx, t.apex), new PointF (t.cx + t.hw, t.bse), new PointF (t.cx - t.hw, t.bse) };
			var poly = new List<PointF> ();
			const int Seg = 24;
			for (int i = 0; i < 3; i++) {
				PointF p0 = tri [(i + 2) % 3], p1 = tri [i], p2 = tri [(i + 1) % 3];
				double a1 = Math.Atan2 (-(p1.X - p0.X), p1.Y - p0.Y), a2 = Math.Atan2 (-(p2.X - p1.X), p2.Y - p1.Y);
				while (a2 < a1) a2 += 2 * Math.PI;
				for (int k = 0; k <= Seg; k++) {
					double u = a1 + (a2 - a1) * k / Seg;
					float ux = (float) (p1.X + t.r * Math.Cos (u)), uy = (float) (p1.Y + t.r * Math.Sin (u));
					// up-frame (U, V) to the device: down (U, n - V), left (V, U), right (n - V, U).
					poly.Add (dir switch {
						1 => new PointF (ux, n - uy),
						2 => new PointF (uy, ux),
						3 => new PointF (n - uy, ux),
						_ => new PointF (ux, uy),
					});
				}
			}
			var f = new Frame (n, n);
			for (int y = 0; y < n; y++)
				for (int x = 0; x < n; x++) {
					double c = Math.Abs (ClippedArea (poly, x, y, x + 1, y + 1));
					uint alpha = (uint) Math.Min (255, (int) (c * t.a * 255 + 0.5));
					if (alpha != 0)
						f.Pixels [y * n + x] = alpha << 24;
				}
			return f;
		}

		/// <summary>An upward triangle -- apex at (<paramref name="cx"/>, <paramref name="apex"/>),
		/// base on y = <paramref name="bse"/>, <paramref name="hw"/> either side -- grown by
		/// <paramref name="r"/>, which rounds its corners.</summary>
		static Shape Triangle (float cx, float apex, float bse, float hw, float r)
			=> (px, py) => {
				if (py <= bse && py >= apex && Math.Abs (px - cx) <= hw * (py - apex) / (bse - apex))
					return true;
				float d = Math.Min (SegmentDistance (px, py, cx, apex, cx + hw, bse),
					  Math.Min (SegmentDistance (px, py, cx + hw, bse, cx - hw, bse),
						    SegmentDistance (px, py, cx - hw, bse, cx, apex)));
				return d <= r;
			};

		static float SegmentDistance (float px, float py, float ax, float ay, float bx, float by)
		{
			float dx = bx - ax, dy = by - ay;
			float t = Math.Clamp (((px - ax) * dx + (py - ay) * dy) / (dx * dx + dy * dy), 0, 1);
			float ex = ax + t * dx - px, ey = ay + t * dy - py;
			return MathF.Sqrt (ex * ex + ey * ey);
		}

		/// <summary>SBP_THUMBBTNVERT (17x11) / THUMBBTNHORZ (20x17): a tile of the track with a
		/// bar down its middle -- two pixels wide at rest, six and rounded under the pointer or
		/// while dragged, and not there at all when the bar is disabled.</summary>
		static Frame ScrollThumb (bool vertical, int state)
		{
			int w = vertical ? 17 : 20, h = vertical ? 11 : 17;
			var layers = new List<Layer> {
				new Layer (Box (0, 0, w, h), ScrollFace),
				new Layer (vertical ? Box (0, 0, 1, h) : Box (0, 0, w, 1), 0xffffffffu),
			};
			if (state != 4) {
				(float a0, float a1, float r) = state == 1 ? (8f, 10f, 0.75f) : (6f, 12f, 3f);
				layers.Add (new Layer (vertical ? RoundRect (a0, 0, a1, h, r) : RoundRect (0, a0, w, a1, r), 0xff858585u));
			}
			return Render (w, h, layers.ToArray ());
		}

		/// <summary>SBP_SIZEBOX, 16x16: six 2x2 dots stacked into a triangle in the corner.</summary>
		static Frame SizeBox ()
		{
			var layers = new List<Layer> ();
			foreach (var (x, y) in new [] { (12, 6), (9, 9), (12, 9), (6, 12), (9, 12), (12, 12) })
				layers.Add (new Layer (Box (x, y, x + 2, y + 2), 0xffbfbfbfu));
			return Render (16, 16, layers.ToArray ());
		}

		/// <summary>HP_HEADERITEM, 6x24 nine-grid 2/3/9/14: white with a one-pixel divider on the
		/// right at rest, a flat blue under the pointer and a deeper one pressed -- the same in
		/// each of the four groups of three (plain, sorted, icon, sorted icon).</summary>
		static Frame HeaderItem (int state)
		{
			int look = (state - 1) % 3;
			Frame f = look switch {
				1 => Render (6, 24, new Layer (Box (0, 0, 6, 24), 0xffd9ebf9u)),
				2 => Render (6, 24, new Layer (Box (0, 0, 6, 24), 0xffbcdcf4u)),
				_ => Render (6, 24, new Layer (Box (0, 0, 6, 24), 0xffffffffu), new Layer (Box (5, 0, 6, 24), 0xffe5e5e5u)),
			};
			return Stretched (f, 2, 3, 9, 14);
		}

		/// <summary>The progress bar's track: a #BCBCBC frame round #E6E6E6.</summary>
		static Frame ProgressTrack (int w, int h)
			=> Render (w, h, new Layer (Box (0, 0, w, h), 0xffbcbcbcu), new Layer (Box (1, 1, w - 1, h - 1), 0xffe6e6e6u));

		/// <summary>PP_FILL / PP_FILLVERT: the state's colour, inset a clear pixel all round --
		/// normal, error, paused, and the wider partial frame.</summary>
		static Frame ProgressFill (bool vertical, int state)
		{
			uint c = state switch { 2 => 0xffc42b1cu, 3 => 0xff9d5d00u, 4 => 0xff0070cbu, _ => 0xff0f7b0fu };
			bool partial = state == 4;
			int w = vertical ? (partial ? 18 : 17) : (partial ? 125 : 51);
			int h = vertical ? (partial ? 125 : 51) : 14;
			Frame f = Render (w, h, new Layer (Box (1, 1, w - 1, h - 1), c));
			return vertical ? Stretched (f, 10, partial ? 7 : 6, partial ? 62 : 25, partial ? 62 : 25)
					: Stretched (f, partial ? 62 : 25, partial ? 62 : 25, 7, 6);
		}

		/// <summary>SPNP_UP / DOWN / UPHORZ / DOWNHORZ: a bordered face rounded on its outer
		/// corners only, its last row a darker band, standing in a #F0F0F0 margin; the arrow is a
		/// small solid triangle, fainter when pressed or disabled.</summary>
		static Frame SpinButton (int part, int state)
		{
			(uint border, uint face, uint band) = state switch {
				2 => (0xff0078d4u, 0xffd8e6f1u, 0xff006bbeu),
				3 or 4 => (0xffe5e5e5u, 0xfff3f3f3u, 0xffe5e5e5u),
				_ => (0xffd2d2d2u, 0xfffafafau, 0xffbcbcbcu),
			};
			int w = part <= 2 ? 8 : 7;
			(float x0, float y0, float x1, float y1) = part switch { 1 => (1f, 1f, 7f, 16f), 2 => (1f, 0f, 7f, 15f), 3 => (0f, 1f, 6f, 16f), _ => (1f, 1f, 7f, 16f) };
			(bool tl, bool tr, bool br, bool bl) = part switch { 1 => (true, true, false, false), 2 => (false, false, true, true), 3 => (false, true, true, false), _ => (true, false, false, true) };
			float r0 = part == 2 || part == 3 ? 2.0625f : 2f;
			Shape outer = Corners (x0, y0, x1, y1, r0, tl, tr, br, bl);
			Shape inner = Corners (x0 + 1, y0 + 1, x1 - 1, y1 - 1, 0.75f, tl, tr, br, bl);
			float bandY = y1 - 1;
			Frame f = Render (w, 16, new Layer (Box (0, 0, w, 16), 0xfff0f0f0u),
					  new Layer (outer, (x, y) => y >= bandY ? band : border), new Layer (inner, face));
			Stretched (f, part == 4 ? 4 : part == 3 ? 3 : 4, part == 4 ? 2 : 3, part == 2 ? 8 : 9, 3);
			// The arrow: exact-area art (alphas down to 5), an up-pointing triangle -- apex (3.5, 1.43),
			// base y = 5, half-width 3.34 -- with its base corners cut at 2.57 either side of the
			// centre, then turned: 7x6 for up/down, 6x7 across. Within 4 levels of the theme image.
			int gw = part <= 2 ? 7 : 6, gh = part <= 2 ? 6 : 7;
			var tri = new List<PointF> { new PointF (3.5f, 1.4297f), new PointF (3.5f + 3.3438f, 5f), new PointF (3.5f - 3.3438f, 5f) };
			const float Cut = 2.5703f;
			tri = ClipEdge (tri, q => q.X >= 3.5f - Cut, (a, b) => new PointF (3.5f - Cut, a.Y + (b.Y - a.Y) * (3.5f - Cut - a.X) / (b.X - a.X)));
			tri = ClipEdge (tri, q => q.X <= 3.5f + Cut, (a, b) => new PointF (3.5f + Cut, a.Y + (b.Y - a.Y) * (3.5f + Cut - a.X) / (b.X - a.X)));
			for (int i = 0; i < tri.Count; i++) {
				PointF u = tri [i];
				tri [i] = part switch {
					2 => new PointF (u.X, gh - u.Y),
					3 => new PointF (gw - u.Y, u.X),
					4 => new PointF (u.Y, u.X),
					_ => u,
				};
			}
			uint alpha = state switch { 3 => 0x9bu, 4 => 0x5cu, _ => 0xe4u };
			Frame g = RenderExactArea (gw, gh, tri, 0);
			for (int i = 0; i < g.Pixels.Length; i++)
				g.Pixels [i] = (uint) ((g.Pixels [i] >> 24) * alpha / 255.0 + 0.5) << 24;
			f.Glyph = g;
			return f;
		}

		/// <summary>A rectangle rounded only at the corners asked for.</summary>
		internal static Shape Corners (float x0, float y0, float x1, float y1, float r, bool tl, bool tr, bool br, bool bl)
			=> (px, py) => {
				if (px < x0 || px > x1 || py < y0 || py > y1)
					return false;
				bool left = px < x0 + r, right = px > x1 - r, top = py < y0 + r, bottom = py > y1 - r;
				float cx, cy;
				if (left && top && tl) { cx = x0 + r; cy = y0 + r; }
				else if (right && top && tr) { cx = x1 - r; cy = y0 + r; }
				else if (right && bottom && br) { cx = x1 - r; cy = y1 - r; }
				else if (left && bottom && bl) { cx = x0 + r; cy = y1 - r; }
				else return true;
				return (px - cx) * (px - cx) + (py - cy) * (py - cy) <= r * r;
			};

		/// <summary>TP_BUTTON / TP_DROPDOWNBUTTON, 7x18 nine-grid 3/3/13/4: nothing at rest or
		/// disabled; a pale-blue rounded face under the pointer, a deeper one pressed or checked.
		/// The last row stays clear.</summary>
		static Frame ToolbarButton (int state)
		{
			bool hot = state == 2 || state == 8, deep = state == 3 || state == 5 || state == 6;
			Frame f = !hot && !deep ? new Frame (7, 18)
				: Render (7, 18,
					new Layer (RoundRect (0, 0, 7, 17, 3), hot ? 0xffcce8ffu : 0xff99d1ffu),
					new Layer (RoundRect (1, 1, 6, 16, 2), hot ? 0xffe5f3ffu : 0xffcce8ffu));
			return Stretched (f, 3, 3, 13, 4);
		}

		/// <summary>TVP_GLYPH, 9x9 true size: a square with its corners softened, a face banded
		/// darker toward the bottom, and a plus (closed) or minus (open) in two blues -- the
		/// horizontal stroke over the vertical one.</summary>
		static Frame TreeGlyph (bool open)
		{
			var layers = new List<Layer> {
				new Layer (Box (0, 0, 9, 9), 0xff919191u),
				new Layer ((x, y) => (x < 1 || x >= 8) && (y < 1 || y >= 8), 0xffbabbbcu),
			};
			// The face, a band at a time: the closed glyph starts two rows lighter than the open one.
			uint [] bands = open
				? new [] { 0xfffafbfbu, 0xfffafbfbu, 0xfffafbfbu, 0xffededecu, 0xffededecu, 0xffe3e3e3u, 0xffe3e3e3u }
				: new [] { 0xfffcfcfcu, 0xfffcfcfcu, 0xfffafbfbu, 0xfffafbfbu, 0xffededecu, 0xffe3e3e3u, 0xffe3e3e3u };
			for (int row = 0; row < 7; row++) {
				layers.Add (new Layer (Box (1, 1 + row, 8, 2 + row), bands [row]));
			}
			if (!open)
				layers.Add (new Layer (Box (4, 2, 5, 7), 0xff294272u));
			layers.Add (new Layer (Box (2, 4, 7, 5), 0xff4b63a7u));
			return Render (9, 9, layers.ToArray ());
		}

		/// <summary>A trackbar thumb's colour: normal, hot, pressed, focused, disabled. The plain
		/// thumb's rest colour is a shade off the pointed ones'.</summary>
		static uint Thumb (int state, bool plain) => state switch {
			2 => 0xff171717u,
			3 or 5 => 0xffccccccu,
			4 => 0xff0078d7u,
			_ => plain ? 0xff007ad9u : 0xff0078d7u,
		};

		/// <summary>TKP_THUMBBOTTOM/TOP/LEFT/RIGHT: a solid body and a 45-degree point
		/// (<paramref name="dx"/>, <paramref name="dy"/> the way it points) whose edge pixels are
		/// half covered. A half is 127.5, which the theme rounds down on an edge facing up and up on
		/// one facing down.</summary>
		static Frame PointedThumb (int w, int h, int dx, int dy, int state)
		{
			uint c = Thumb (state, false);
			var f = new Frame (w, h);
			int across = dy != 0 ? w : h, along = dy != 0 ? h : w;
			int tip = (across + 1) / 2;                         // rows the point takes
			for (int i = 0; i < along; i++) {
				// i counts from the flat end toward the point.
				int inset = Math.Max (0, i - (along - tip));    // how far this row is cut in on each side
				for (int j = 0; j < across; j++) {
					int from = Math.Min (j, across - 1 - j);    // distance in from the nearer side
					if (from < inset - 1)
						continue;
					uint a = 255;
					if (from == inset - 1) {
						// The edge faces up when the point itself is up, or when the point runs
						// sideways and this is its upper edge.
						bool facesUp = dy < 0 || (dy == 0 && j < across / 2);
						a = facesUp ? 127u : 128u;
					}
					int x = dy != 0 ? j : (dx > 0 ? i : w - 1 - i);
					int y = dy != 0 ? (dy > 0 ? i : h - 1 - i) : j;
					f.Pixels [y * w + x] = Premultiply (c, a);
				}
			}
			return f;
		}

		static uint Premultiply (uint c, uint a)
			=> a << 24 | (uint) Math.Round (((c >> 16) & 0xff) * a / 255.0) << 16
			 | (uint) Math.Round (((c >> 8) & 0xff) * a / 255.0) << 8 | (uint) Math.Round ((c & 0xff) * a / 255.0);

		/// <summary>The combo box's chevron, 10x19, in the state's ink. Exact-area art like the
		/// Explorer tree glyph (its faintest pixels carry alpha 6): a 0.77-pixel stroke from
		/// (1.04, 7.86) down to (5, 11.81) and back up, round-joined -- within 31 levels of the theme
		/// image over its whole area.</summary>
		static Frame Chevron (int state)
		{
			uint ink = state switch { 2 => 0x1f1f1fu, 4 => 0xa8a8a8u, _ => 0x3f3f3fu };
			return RenderExactArea (10, 19, RoundJoinChevron (1.0391f, 7.8594f, 5f, 11.8125f, 8.9609f, 7.8594f, 0.7672f, 0), ink);
		}

		/// <summary>The border and face of an unmarked check box or radio button.</summary>
		static (uint border, uint face) Unmarked (int look) => look switch {
			1 => (0xff626262u, 0xffeaeaeau),   // hot
			2 => (0xffc3c3c3u, 0xffe2e2e2u),   // pressed
			3 => (0xffc3c3c3u, 0xfff9f9f9u),   // disabled
			_ => (0xff626262u, 0xfff3f3f3u),
		};

		/// <summary>The accent a marked check box or radio button is filled with.</summary>
		static uint Marked (int look) => look switch {
			1 => 0xff196ebfu,
			2 => 0xff327ec5u,
			3 => 0xffc3c3c3u,
			_ => 0xff005fb8u,
		};
	}
}
