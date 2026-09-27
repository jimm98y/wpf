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
			if (f.Stretch && Solid (f) is uint face) {
				var (l, r, t, b) = f.Margins;
				var middle = new Rectangle (dest.X + l, dest.Y + t, dest.Width - l - r, dest.Height - t - b);
				if (middle.Width > 0 && middle.Height > 0)
					using (var brush = new SolidBrush (Color.FromArgb ((int) face)))
						g.FillRectangle (brush, middle);
			}
		}

		/// <summary>The colour of a stretched frame's middle, when it is all one opaque colour.</summary>
		static uint? Solid (Frame f)
		{
			var (l, r, t, b) = f.Margins;
			if (l + r >= f.Width || t + b >= f.Height)
				return null;
			uint c = f.Pixels [t * f.Width + l];
			if (c >> 24 != 255)
				return null;
			for (int y = t; y < f.Height - b; y++)
				for (int x = l; x < f.Width - r; x++)
					if (f.Pixels [y * f.Width + x] != c)
						return null;
			return c;
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
			if (lo + hi >= size || lo + hi >= source)
				return d * source / size;
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

		const int Button = 1;

		/// <summary>The frame uxtheme would take for a part and state at 96 DPI, or null for a part
		/// that is not drawn from a frame.</summary>
		internal static Frame Get (string cls, int part, int state)
		{
			int c = cls switch { "BUTTON" => Button, _ => 0 };
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
				layers.Add (new Layer (RoundRect (0, 0, 13, 13, 3.125f), border));
				layers.Add (new Layer (RoundRect (1, 1, 12, 12, 2), face));
			} else {
				layers.Add (new Layer (RoundRect (0, 0, 13, 13, 3.125f), Marked (look)));
				Shape glyph = mark switch {
					1 => Stroke (0.6875f, 3.5938f, 7f, 5.5625f, 8.875f, 9.625f, 4.8125f),
					2 => Stroke (0.75f, 4.1875f, 6.5625f, 8.75f, 6.5625f),
					_ => (x, y) => Stroke (0.75f, 4.25f, 4.25f, 8.75f, 8.75f) (x, y) || Stroke (0.75f, 8.75f, 4.25f, 4.25f, 8.75f) (x, y),
				};
				layers.Add (new Layer (glyph, 0xffffffffu));
			}
			return Render (13, 13, layers.ToArray ());
		}

		/// <summary>BP_RADIOBUTTON, 13x13, true size: a ring, or the accent disc under a white dot
		/// that grows on hover and shrinks while pressed. Pressing an unchecked one shows the dot
		/// it is about to get.</summary>
		internal static Frame RadioButton (int state)
		{
			bool check = state > 4;
			int look = (state - 1) % 4;
			var layers = new List<Layer> ();
			const float outer = 6.4844f;
			if (!check) {
				(uint border, uint face) = Unmarked (look);
				layers.Add (new Layer (Circle (6.5f, 6.5f, outer), border));
				layers.Add (new Layer (Circle (6.5f, 6.5f, 5.4922f), face));
				if (look == 2)
					layers.Add (new Layer (Circle (6.5f, 6.5f, 1.9844f), 0xffffffffu));
			} else {
				float dot = look switch { 1 => 3.2578f, 2 => 1.9766f, _ => 2.5156f };
				layers.Add (new Layer (Circle (6.5f, 6.5f, outer), Marked (look)));
				layers.Add (new Layer (Circle (6.5f, 6.5f, dot), 0xffffffffu));
			}
			return Render (13, 13, layers.ToArray ());
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
