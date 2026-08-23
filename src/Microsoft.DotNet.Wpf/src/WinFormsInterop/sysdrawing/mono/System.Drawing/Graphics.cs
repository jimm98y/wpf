//
// System.Drawing.Graphics.cs
//
// Authors:
//	Gonzalo Paniagua Javier (gonzalo@ximian.com) (stubbed out)
//      Alexandre Pigolkine(pigolkine@gmx.de)
//	Jordi Mas i Hernandez (jordi@ximian.com)
//	Sebastien Pouliot  <sebastien@ximian.com>
//
// Copyright (C) 2003 Ximian, Inc. (http://www.ximian.com)
// Copyright (C) 2004-2006 Novell, Inc. (http://www.novell.com)
//
// Permission is hereby granted, free of charge, to any person obtaining
// a copy of this software and associated documentation files (the
// "Software"), to deal in the Software without restriction, including
// without limitation the rights to use, copy, modify, merge, publish,
// distribute, sublicense, and/or sell copies of the Software, and to
// permit persons to whom the Software is furnished to do so, subject to
// the following conditions:
// 
// The above copyright notice and this permission notice shall be
// included in all copies or substantial portions of the Software.
// 
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND,
// EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF
// MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND
// NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE
// LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION
// OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION
// WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
//

using System.Collections.Generic;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace System.Drawing
{
	public sealed class Graphics : MarshalByRefObject, IDisposable
	, IDeviceContext
	{
		internal IntPtr nativeObject = IntPtr.Zero;

		// GPU-rasterization seam: when set, the core drawing verbs record WebGPU scene primitives
		// instead of calling libgdiplus (see backend/IGpuSceneRecorder.cs). Attached by the driver
		// for window paints. Only SolidBrush fills / solid Pen lines / text are rerouted; anything
		// else falls through to libgdiplus so nothing regresses.
		internal IGpuSceneRecorder GpuRecorder;

		// GPU-raster mode: route ALL text measurement to the managed metrics (no libgdiplus) — for
		// both layout (control sizing) and paint. Correct because we render every run with the same
		// font, so controls are sized to fit what's actually drawn. A browser prerequisite.
		// On unless switched off; see XplatUIWebGpu.s_gpuRaster for why it cannot be opt-in.
		static readonly bool s_gpuRasterMode = Environment.GetEnvironmentVariable ("WF_GPU_RASTER") != "0"
			&& Environment.GetEnvironmentVariable ("WF_WEBGPU") != "0";

		// WF_TRACE_TEXT=1: every text run the recorder is given, with where it lands.
		static readonly bool s_traceText = Environment.GetEnvironmentVariable ("WF_TRACE_TEXT") == "1";

		static int ArgbOf (Brush b)
		{
			if (b is SolidBrush sb) return sb.Color.ToArgb ();
			// A HatchBrush (e.g. the Percent50 dither the theme uses for pressed radio/checkbox glyphs)
			// has no single colour; approximate it as the average of fore/back so it records as a solid
			// instead of falling through to libgdiplus (which throws with no native Graphics).
			if (b is Drawing2D.HatchBrush hb) return Blend (hb.ForegroundColor, hb.BackgroundColor);
			return 0;
		}
		static int ArgbOf (Pen p)
		{
			if (p == null) return 0;
			if (p.Brush is SolidBrush sb) return sb.Color.ToArgb ();
			if (p.Brush is Drawing2D.HatchBrush hb) return Blend (hb.ForegroundColor, hb.BackgroundColor);
			return p.Color.ToArgb ();
		}
		// A pen's dash pattern, in GDI+ units (multiples of the pen width), or null when solid.
		// The built-in styles have fixed patterns; only a custom one has to be read back from the pen.
		static float [] DashOf (Pen p)
		{
			if (p == null)
				return null;
			switch (p.DashStyle) {
			case Drawing2D.DashStyle.Solid:      return null;
			case Drawing2D.DashStyle.Dash:       return new float [] { 3f, 1f };
			case Drawing2D.DashStyle.Dot:        return new float [] { 1f, 1f };
			case Drawing2D.DashStyle.DashDot:    return new float [] { 3f, 1f, 1f, 1f };
			case Drawing2D.DashStyle.DashDotDot: return new float [] { 3f, 1f, 1f, 1f, 1f, 1f };
			default:
				try { return p.DashPattern; } catch { return null; }
			}
		}

		// True when this pen draws a dashed line, which the recorder has to keep as a stroke: the
		// solid path collapses a line to a filled 1px rect and the pattern would be lost.
		static bool RecordDash (Pen p, out float [] pattern)
		{
			pattern = DashOf (p);
			return pattern != null && pattern.Length > 0;
		}

		// A path, flattened into its subpaths' points. The recorder draws polygons and lines, not
		// curves, so the curves are approximated here -- by GDI+, which owns the path's geometry --
		// rather than each caller having to avoid GraphicsPath. Without this FillPath and DrawPath
		// went straight to a libgdiplus surface that does not exist in GPU-raster mode and silently
		// drew nothing at all: a rounded button came out with no face and no border.
		static List<PointF []> FlattenSubpaths (Drawing2D.GraphicsPath path)
		{
			var subpaths = new List<PointF []> ();
			if (path == null || path.PointCount == 0)
				return subpaths;

			Drawing2D.GraphicsPath flat;
			try {
				flat = (Drawing2D.GraphicsPath) path.Clone ();
				flat.Flatten ();
			} catch {
				return subpaths;
			}

			using (flat) {
				PointF [] points;
				byte [] types;
				try {
					points = flat.PathPoints;
					types = flat.PathTypes;
				} catch {
					return subpaths;
				}

				const byte TypeMask = 0x07, TypeStart = 0x00;
				var current = new List<PointF> ();
				for (int i = 0; i < points.Length; i++) {
					if ((types [i] & TypeMask) == TypeStart && current.Count > 0) {
						subpaths.Add (current.ToArray ());
						current = new List<PointF> ();
					}
					current.Add (points [i]);
				}
				if (current.Count > 0)
					subpaths.Add (current.ToArray ());
			}

			return subpaths;
		}

		static float [] ToXY (PointF [] points)
		{
			var xy = new float [points.Length * 2];
			for (int i = 0; i < points.Length; i++) {
				xy [i * 2] = points [i].X;
				xy [i * 2 + 1] = points [i].Y;
			}
			return xy;
		}

		static int Blend (Color a, Color b) =>
			Color.FromArgb ((a.A + b.A) / 2, (a.R + b.R) / 2, (a.G + b.G) / 2, (a.B + b.B) / 2).ToArgb ();
		/// <summary>
		/// CheckStatus for a drawing primitive. A recording-only Graphics (GPU-raster paint) has no
		/// native surface: anything the recorder does not know how to record -- a TextureBrush fill,
		/// say -- still reaches GDI+, which rejects the null handle with InvalidParameter. There is
		/// nothing to draw on, so that is "not drawn", not an error. Throwing turned a missing
		/// background into an ArgumentException on every single paint.
		/// </summary>
		void CheckDrawStatus (Status status)
		{
			if (nativeObject == IntPtr.Zero && status == Status.InvalidParameter)
				return;
			GDIPlus.CheckStatus (status);
		}

		bool RecordSolid (Brush b) => GpuRecorder != null && b is SolidBrush;

		// A HatchBrush rendered as a real repeating tile (fore/back pattern), recorded as a tiling
		// ImageBrush so the GPU reproduces the actual hatch — horizontal/diagonal/cross lines and the
		// Percent* dithers (e.g. the Percent50 the theme fills pressed radio/checkbox glyphs with).
		internal readonly struct HatchTile
		{
			public readonly byte[] Rgba; public readonly int W, H; public readonly float Size;
			public HatchTile (byte[] rgba, int w, int h, float size) { Rgba = rgba; W = w; H = h; Size = size; }
		}
		bool TryHatch (Brush b, out HatchTile tile)
		{
			tile = default;
			if (GpuRecorder == null || !(b is Drawing2D.HatchBrush hb)) return false;
			tile = BuildHatchTile (hb.HatchStyle, hb.ForegroundColor, hb.BackgroundColor);
			return true;
		}
		// 8x8 pattern; Size is its extent in POINTS so cells are ~1 device px (fine dithers read as the
		// intended grey, line hatches stay crisp) and the pattern scales with DPI.
		static HatchTile BuildHatchTile (Drawing2D.HatchStyle style, Color fg, Color bg)
		{
			const int N = 8;
			var px = new byte[N * N * 4];
			for (int y = 0; y < N; y++)
				for (int x = 0; x < N; x++) {
					Color c = HatchHit (style, x, y, N) ? fg : bg;
					int i = (y * N + x) * 4;
					px[i] = c.R; px[i + 1] = c.G; px[i + 2] = c.B; px[i + 3] = c.A;
				}
			return new HatchTile (px, N, N, N / 2f);
		}
		// Foreground test for the hatch styles WinForms themes actually use; anything else falls back to
		// a 50% dither (a reasonable stand-in that still reads as the foreground/background mix).
		static bool HatchHit (Drawing2D.HatchStyle s, int x, int y, int n)
		{
			switch (s) {
				case Drawing2D.HatchStyle.Horizontal: return y % n == 0;
				case Drawing2D.HatchStyle.Vertical: return x % n == 0;
				case Drawing2D.HatchStyle.Cross: return x % n == 0 || y % n == 0;
				case Drawing2D.HatchStyle.ForwardDiagonal: return (x + y) % n == 0;
				case Drawing2D.HatchStyle.BackwardDiagonal: return (x - y + n) % n == 0;
				case Drawing2D.HatchStyle.DiagonalCross: return (x + y) % n == 0 || (x - y + n) % n == 0;
				case Drawing2D.HatchStyle.Percent25: return ((x & 1) == 0) && ((y & 1) == 0);
				case Drawing2D.HatchStyle.Percent75: return !(((x & 1) == 1) && ((y & 1) == 1));
				default: return ((x + y) & 1) == 0;   // Percent50 and everything else
			}
		}
		bool RecordPen (Pen p) => GpuRecorder != null && (p?.Brush is SolidBrush || p != null);
		// Resolve a gradient brush (linear multi-stop or path/radial) to a GradientDesc; false = not a
		// gradient in GPU mode (fall through to libgdiplus).
		bool TryGradient (Brush b, out GradientDesc g)
		{
			g = default;
			if (GpuRecorder == null) return false;
			if (b is Drawing2D.LinearGradientBrush lg) {
				lg.GetGpuGradient (out PointF s, out PointF e, out _, out _);
				lg.GetGpuStops (out float[] offs, out int[] argb);
				g = new GradientDesc (false, s.X, s.Y, e.X, e.Y, offs, argb);
				return true;
			}
			if (b is Drawing2D.PathGradientBrush pg) {
				pg.GetGpuGradient (out PointF c, out float rx, out float ry, out float[] offs, out int[] argb);
				g = new GradientDesc (true, c.X, c.Y, rx, ry, offs, argb);
				return true;
			}
			return false;
		}
		static float[] Flatten (PointF[] p) { var a = new float[p.Length * 2]; for (int i = 0; i < p.Length; i++) { a[i*2] = p[i].X; a[i*2+1] = p[i].Y; } return a; }
		static float[] Flatten (Point[] p) { var a = new float[p.Length * 2]; for (int i = 0; i < p.Length; i++) { a[i*2] = p[i].X; a[i*2+1] = p[i].Y; } return a; }

		// Record a Bitmap draw as an image quad (whole image -> dest rect). Returns false (fall through
		// to libgdiplus) when not in GPU-raster mode or the image isn't a Bitmap we can read pixels from.
		bool RecordImage (Image image, float dx, float dy, float dw, float dh)
		{
			if (GpuRecorder == null || !(image is Bitmap bmp)) return false;
			int w = bmp.Width, h = bmp.Height;
			var data = bmp.LockBits (new Rectangle (0, 0, w, h), Imaging.ImageLockMode.ReadOnly, Imaging.PixelFormat.Format32bppArgb);
			try {
				byte[] buf = new byte[data.Stride * h];
				System.Runtime.InteropServices.Marshal.Copy (data.Scan0, buf, 0, buf.Length);
				byte[] rgba = new byte[w * h * 4];
				for (int yy = 0; yy < h; yy++)
					for (int xx = 0; xx < w; xx++) {
						int s = yy * data.Stride + xx * 4, d = (yy * w + xx) * 4;
						rgba[d] = buf[s + 2]; rgba[d + 1] = buf[s + 1]; rgba[d + 2] = buf[s + 0]; rgba[d + 3] = buf[s + 3];
					}
				GpuRecorder.DrawImage (rgba, w, h, dx, dy, dw, dh);
			} finally { bmp.UnlockBits (data); }
			return true;
		}
		internal IMacContext maccontext;
		private bool disposed = false;
		private static float defDpiX = 0;
		private static float defDpiY = 0;
		private IntPtr deviceContextHdc;
		private Metafile.MetafileHolder _metafileHolder;

		public delegate bool EnumerateMetafileProc (EmfPlusRecordType recordType,
							    int flags,
							    int dataSize,
							    IntPtr data,
							    PlayRecordCallback callbackData);
		
		public delegate bool DrawImageAbort (IntPtr callbackdata);

		internal Graphics (IntPtr nativeGraphics)
		{
			nativeObject = nativeGraphics;
		}

		internal Graphics(IntPtr nativeGraphics, Image image) : this(nativeGraphics)
		{
			if (image is Metafile mf) {
				_metafileHolder = mf.AddMetafileHolder();
			}
		}

		~Graphics ()
		{
			Dispose ();			
		}		

		static internal float systemDpiX {
			get {
				if (defDpiX == 0) {
					// No libgdiplus (browser): can't create a probe Bitmap; assume 96 DPI. The GPU-raster
					// path renders at the real backing scale separately, so control layout uses 96 like Windows.
					if (!GDIPlus.Initialized) { defDpiX = 96f; defDpiY = 96f; }
					else {
						Bitmap bmp = new Bitmap (1, 1);
						Graphics g = Graphics.FromImage (bmp);
						defDpiX = g.DpiX;
						defDpiY = g.DpiY;
					}
				}
				return defDpiX;
			}
		}

		static internal float systemDpiY {
			get {
				if (defDpiY == 0) {
					if (!GDIPlus.Initialized) { defDpiX = 96f; defDpiY = 96f; }
					else {
						Bitmap bmp = new Bitmap (1, 1);
						Graphics g = Graphics.FromImage (bmp);
						defDpiX = g.DpiX;
						defDpiY = g.DpiY;
					}
				}
				return defDpiY;
			}
		}

		// For CoreFX compatibility
		internal IntPtr NativeGraphics {
			get {
				return nativeObject;
			}
		}

		internal IntPtr NativeObject {
			get {
				return nativeObject;
			}

			set {
				nativeObject = value;
			}
		}
		
		[MonoTODO ("Metafiles, both WMF and EMF formats, aren't supported.")]
		public void AddMetafileComment (byte [] data)
		{
			throw new NotImplementedException ();
		}

		public GraphicsContainer BeginContainer ()
		{
			uint state;
			Status status;
			status = GDIPlus.GdipBeginContainer2 (nativeObject, out state);
        		CheckDrawStatus (status);

                        return new GraphicsContainer(state);
		}

		[MonoTODO ("The rectangles and unit parameters aren't supported in libgdiplus")]		
		public GraphicsContainer BeginContainer (Rectangle dstrect, Rectangle srcrect, GraphicsUnit unit)
		{
			uint state;
			Status status;
			status = GDIPlus.GdipBeginContainerI (nativeObject, ref dstrect, ref srcrect, unit, out state);
			CheckDrawStatus (status);

			return new GraphicsContainer (state);
		}

		[MonoTODO ("The rectangles and unit parameters aren't supported in libgdiplus")]		
		public GraphicsContainer BeginContainer (RectangleF dstrect, RectangleF srcrect, GraphicsUnit unit)
		{
			uint state;
			Status status;
			status = GDIPlus.GdipBeginContainer (nativeObject, ref dstrect, ref srcrect, unit, out state);
			CheckDrawStatus (status);

			return new GraphicsContainer (state);
		}

		
		public void Clear (Color color)
		{
			Status status;
			if (GpuRecorder != null) {
				// Clear fills the ENTIRE surface. A recorder has no surface of its own, but a
				// window's scene is clipped to that window, so a rectangle large enough to cover
				// anything comes out as exactly the window. Skipping it left every control that
				// clears its background transparent -- a ToolStripDropDown paints itself that way,
				// so a context menu came up with its items floating over whatever was behind it.
				const float Big = 1 << 20;
				GpuRecorder.FillRect (-Big, -Big, Big * 2, Big * 2, color.ToArgb ());
				return;
			}
 			if (nativeObject == IntPtr.Zero) return;
 			status = GDIPlus.GdipGraphicsClear (nativeObject, color.ToArgb ());
 			CheckDrawStatus (status);
		}
		[MonoLimitation ("Works on Win32 and on X11 (but not on Cocoa and Quartz)")]
		public void CopyFromScreen (Point upperLeftSource, Point upperLeftDestination, Size blockRegionSize)
		{
			CopyFromScreen (upperLeftSource.X, upperLeftSource.Y, upperLeftDestination.X, upperLeftDestination.Y,
				blockRegionSize, CopyPixelOperation.SourceCopy);				
		}

		[MonoLimitation ("Works on Win32 and (for CopyPixelOperation.SourceCopy only) on X11 but not on Cocoa and Quartz")]
		public void CopyFromScreen (Point upperLeftSource, Point upperLeftDestination, Size blockRegionSize, CopyPixelOperation copyPixelOperation)
		{
			CopyFromScreen (upperLeftSource.X, upperLeftSource.Y, upperLeftDestination.X, upperLeftDestination.Y,
				blockRegionSize, copyPixelOperation);
		}
		
		[MonoLimitation ("Works on Win32 and on X11 (but not on Cocoa and Quartz)")]
		public void CopyFromScreen (int sourceX, int sourceY, int destinationX, int destinationY, Size blockRegionSize)
		{
			CopyFromScreen (sourceX, sourceY, destinationX, destinationY, blockRegionSize,
				CopyPixelOperation.SourceCopy);
		}

		[MonoLimitation ("Works on Win32 and (for CopyPixelOperation.SourceCopy only) on X11 but not on Cocoa and Quartz")]
		public void CopyFromScreen (int sourceX, int sourceY, int destinationX, int destinationY, Size blockRegionSize, CopyPixelOperation copyPixelOperation)
		{
			if (!Enum.IsDefined (typeof (CopyPixelOperation), copyPixelOperation))
				throw new InvalidEnumArgumentException (Locale.GetText ("Enum argument value '{0}' is not valid for CopyPixelOperation", copyPixelOperation));

			if (GDIPlus.UseX11Drawable) {
				CopyFromScreenX11 (sourceX, sourceY, destinationX, destinationY, blockRegionSize, copyPixelOperation);
			} else if (GDIPlus.UseCarbonDrawable) {
				CopyFromScreenMac (sourceX, sourceY, destinationX, destinationY, blockRegionSize, copyPixelOperation);
			} else if (GDIPlus.UseCocoaDrawable) {
				CopyFromScreenMac (sourceX, sourceY, destinationX, destinationY, blockRegionSize, copyPixelOperation);
			} else {
				CopyFromScreenWin32 (sourceX, sourceY, destinationX, destinationY, blockRegionSize, copyPixelOperation);
			}
		}

		private void CopyFromScreenWin32 (int sourceX, int sourceY, int destinationX, int destinationY, Size blockRegionSize, CopyPixelOperation copyPixelOperation)
		{
			IntPtr window = GDIPlus.GetDesktopWindow ();
			IntPtr srcDC = GDIPlus.GetDC (window);
			IntPtr dstDC = GetHdc ();
			GDIPlus.BitBlt (dstDC, destinationX, destinationY, blockRegionSize.Width,
				blockRegionSize.Height, srcDC, sourceX, sourceY, (int) copyPixelOperation);

			GDIPlus.ReleaseDC (IntPtr.Zero, srcDC);
			ReleaseHdc (dstDC);			
		}
		
		private void CopyFromScreenMac (int sourceX, int sourceY, int destinationX, int destinationY, Size blockRegionSize, CopyPixelOperation copyPixelOperation)
		{
			throw new NotImplementedException ();
		}

		private void CopyFromScreenX11 (int sourceX, int sourceY, int destinationX, int destinationY, Size blockRegionSize, CopyPixelOperation copyPixelOperation)
		{
			IntPtr window, image, defvisual, vPtr;
			int AllPlanes = ~0, nitems = 0, pixel;

			if (copyPixelOperation != CopyPixelOperation.SourceCopy)
				throw new NotImplementedException ("Operation not implemented under X11");
		
			if (GDIPlus.Display == IntPtr.Zero) {
				GDIPlus.Display = GDIPlus.XOpenDisplay (IntPtr.Zero);
			}

			window = GDIPlus.XRootWindow (GDIPlus.Display, 0);
			defvisual = GDIPlus.XDefaultVisual (GDIPlus.Display, 0);
			XVisualInfo visual = new XVisualInfo ();

			/* Get XVisualInfo for this visual */
			visual.visualid = GDIPlus.XVisualIDFromVisual(defvisual);
			vPtr = GDIPlus.XGetVisualInfo (GDIPlus.Display, 0x1 /* VisualIDMask */, ref visual, ref nitems);
			visual = (XVisualInfo) Marshal.PtrToStructure(vPtr, typeof (XVisualInfo));
#if false
			Console.WriteLine ("visual\t{0}", visual.visual);
			Console.WriteLine ("visualid\t{0}", visual.visualid);
			Console.WriteLine ("screen\t{0}", visual.screen);
			Console.WriteLine ("depth\t{0}", visual.depth);
			Console.WriteLine ("klass\t{0}", visual.klass);
			Console.WriteLine ("red_mask\t{0:X}", visual.red_mask);
			Console.WriteLine ("green_mask\t{0:X}", visual.green_mask);
			Console.WriteLine ("blue_mask\t{0:X}", visual.blue_mask);
			Console.WriteLine ("colormap_size\t{0}", visual.colormap_size);
			Console.WriteLine ("bits_per_rgb\t{0}", visual.bits_per_rgb);
#endif
			image = GDIPlus.XGetImage (GDIPlus.Display, window, sourceX, sourceY, blockRegionSize.Width,
				blockRegionSize.Height, AllPlanes, 2 /* ZPixmap*/);
			if (image == IntPtr.Zero) {
				string s = String.Format ("XGetImage returned NULL when asked to for a {0}x{1} region block", 
					blockRegionSize.Width, blockRegionSize.Height);
				throw new InvalidOperationException (s);
			}
				
			Bitmap bmp = new Bitmap (blockRegionSize.Width, blockRegionSize.Height);
			int red, blue, green;
			int red_mask = (int) visual.red_mask;
			int blue_mask = (int) visual.blue_mask;
			int green_mask = (int) visual.green_mask;
			for (int y = 0; y < blockRegionSize.Height; y++) {
				for (int x = 0; x < blockRegionSize.Width; x++) {
					pixel = GDIPlus.XGetPixel (image, x, y);

					switch (visual.depth) {
						case 16: /* 16bbp pixel transformation */
							red = (int) ((pixel & red_mask ) >> 8) & 0xff;
							green = (int) (((pixel & green_mask ) >> 3 )) & 0xff;
							blue = (int) ((pixel & blue_mask ) << 3 ) & 0xff;
							break;
						case 24:
						case 32:
							red = (int) ((pixel & red_mask ) >> 16) & 0xff;
							green = (int) (((pixel & green_mask ) >> 8 )) & 0xff;
							blue = (int) ((pixel & blue_mask )) & 0xff;
							break;
						default:
							string text = Locale.GetText ("{0}bbp depth not supported.", visual.depth);
							throw new NotImplementedException (text);
					}
						
					bmp.SetPixel (x, y, Color.FromArgb (255, red, green, blue));							 
				}
			}

			DrawImage (bmp, destinationX, destinationY);
			bmp.Dispose ();
			GDIPlus.XDestroyImage (image);
			GDIPlus.XFree (vPtr);
		}

		/// <summary>Called once when this Graphics is disposed, before the recorder is detached.
		/// The GPU-raster driver uses it to fold what was drawn through CreateGraphics into the
		/// window's scene -- otherwise that drawing has nowhere to go.</summary>
		internal Action<Graphics> DisposeHook;

		private void RunDisposeHook ()
		{
			Action<Graphics> hook = DisposeHook;
			if (hook == null)
				return;
			DisposeHook = null;      // once only, even if Dispose is called twice
			hook (this);
		}

		public void Dispose ()
		{
			RunDisposeHook ();
			// Recording-only Graphics (GPU-raster, no libgdiplus backing): nothing native to free.
			if (nativeObject == IntPtr.Zero) { disposed = true; GpuRecorder = null; return; }
			Status status;
			if (! disposed) {
				if (deviceContextHdc != IntPtr.Zero)
					ReleaseHdc ();

				if (GDIPlus.UseCarbonDrawable || GDIPlus.UseCocoaDrawable) {
					Flush ();
					if (maccontext != null)
						maccontext.Release ();
				}

				status = GDIPlus.GdipDeleteGraphics (nativeObject);
				nativeObject = IntPtr.Zero;
				CheckDrawStatus (status);

				if (_metafileHolder != null)
				{
					var mh = _metafileHolder;
					_metafileHolder = null;
					mh.GraphicsDisposed();
				}

				disposed = true;				
			}

			GC.SuppressFinalize(this);
		}

		
		public void DrawArc (Pen pen, Rectangle rect, float startAngle, float sweepAngle)
		{
			DrawArc (pen, rect.X, rect.Y, rect.Width, rect.Height, startAngle, sweepAngle);
		}

		
		public void DrawArc (Pen pen, RectangleF rect, float startAngle, float sweepAngle)
		{
			DrawArc (pen, rect.X, rect.Y, rect.Width, rect.Height, startAngle, sweepAngle);
		}

		
		public void DrawArc (Pen pen, float x, float y, float width, float height, float startAngle, float sweepAngle)
		{
			Status status;
			if (pen == null)
				throw new ArgumentNullException ("pen");
			if (RecordPen (pen)) { GpuRecorder.DrawArc (x, y, width, height, startAngle, sweepAngle, ArgbOf (pen), pen.Width); return; }
			status = GDIPlus.GdipDrawArc (nativeObject, pen.NativePen,
                                        x, y, width, height, startAngle, sweepAngle);
			CheckDrawStatus (status);
		}

		// Microsoft documentation states that the signature for this member should be
		// public void DrawArc( Pen pen,  int x,  int y,  int width,  int height,   int startAngle,
   		// int sweepAngle. However, GdipDrawArcI uses also float for the startAngle and sweepAngle params
   		public void DrawArc (Pen pen, int x, int y, int width, int height, int startAngle, int sweepAngle)
		{
			Status status;
			if (pen == null)
				throw new ArgumentNullException ("pen");
			if (RecordPen (pen)) { GpuRecorder.DrawArc (x, y, width, height, startAngle, sweepAngle, ArgbOf (pen), pen.Width); return; }
			status = GDIPlus.GdipDrawArcI (nativeObject, pen.NativePen,
						x, y, width, height, startAngle, sweepAngle);
			CheckDrawStatus (status);
		}

		public void DrawBezier (Pen pen, PointF pt1, PointF pt2, PointF pt3, PointF pt4)
		{
			Status status;
			if (pen == null)
				throw new ArgumentNullException ("pen");
			status = GDIPlus.GdipDrawBezier (nativeObject, pen.NativePen,
							pt1.X, pt1.Y, pt2.X, pt2.Y, pt3.X,
							pt3.Y, pt4.X, pt4.Y);
			CheckDrawStatus (status);
		}

		public void DrawBezier (Pen pen, Point pt1, Point pt2, Point pt3, Point pt4)
		{
			Status status;
			if (pen == null)
				throw new ArgumentNullException ("pen");
			status = GDIPlus.GdipDrawBezierI (nativeObject, pen.NativePen,
							pt1.X, pt1.Y, pt2.X, pt2.Y, pt3.X,
							pt3.Y, pt4.X, pt4.Y);
			CheckDrawStatus (status);
		}

		public void DrawBezier (Pen pen, float x1, float y1, float x2, float y2, float x3, float y3, float x4, float y4)
		{
			Status status;
			if (pen == null)
				throw new ArgumentNullException ("pen");
			status = GDIPlus.GdipDrawBezier (nativeObject, pen.NativePen, x1,
							y1, x2, y2, x3, y3, x4, y4);
			CheckDrawStatus (status);
		}

		public void DrawBeziers (Pen pen, Point [] points)
		{
			if (pen == null)
				throw new ArgumentNullException ("pen");
			if (points == null)
				throw new ArgumentNullException ("points");
			
                        int length = points.Length;
			Status status;

                        if (length < 4)
                                return;

			for (int i = 0; i < length - 1; i += 3) {
                                Point p1 = points [i];
                                Point p2 = points [i + 1];
                                Point p3 = points [i + 2];
                                Point p4 = points [i + 3];

                                status = GDIPlus.GdipDrawBezier (nativeObject, 
							pen.NativePen,
                                                        p1.X, p1.Y, p2.X, p2.Y, 
                                                        p3.X, p3.Y, p4.X, p4.Y);
				CheckDrawStatus (status);
                        }
		}

		public void DrawBeziers (Pen pen, PointF [] points)
		{
			if (pen == null)
				throw new ArgumentNullException ("pen");
			if (points == null)
				throw new ArgumentNullException ("points");
			
			int length = points.Length;
			Status status;

                        if (length < 4)
                                return;

			for (int i = 0; i < length - 1; i += 3) {
                                PointF p1 = points [i];
                                PointF p2 = points [i + 1];
                                PointF p3 = points [i + 2];
                                PointF p4 = points [i + 3];

                                status = GDIPlus.GdipDrawBezier (nativeObject, 
							pen.NativePen,
                                                        p1.X, p1.Y, p2.X, p2.Y, 
                                                        p3.X, p3.Y, p4.X, p4.Y);
				CheckDrawStatus (status);
                        }
		}

		
		public void DrawClosedCurve (Pen pen, PointF [] points)
		{
			if (pen == null)
				throw new ArgumentNullException ("pen");
			if (points == null)
				throw new ArgumentNullException ("points");
			
			Status status;
			status = GDIPlus.GdipDrawClosedCurve (nativeObject, pen.NativePen, points, points.Length);
			CheckDrawStatus (status);
		}
		
		public void DrawClosedCurve (Pen pen, Point [] points)
		{
			if (pen == null)
				throw new ArgumentNullException ("pen");
			if (points == null)
				throw new ArgumentNullException ("points");
			
			Status status;
			status = GDIPlus.GdipDrawClosedCurveI (nativeObject, pen.NativePen, points, points.Length);
			CheckDrawStatus (status);
		}
 			
		// according to MSDN fillmode "is required but ignored" which makes _some_ sense since the unmanaged 
		// GDI+ call doesn't support it (issue spotted using Gendarme's AvoidUnusedParametersRule)
		public void DrawClosedCurve (Pen pen, Point [] points, float tension, FillMode fillmode)
		{
			if (pen == null)
				throw new ArgumentNullException ("pen");
			if (points == null)
				throw new ArgumentNullException ("points");
			
			Status status;
			status = GDIPlus.GdipDrawClosedCurve2I (nativeObject, pen.NativePen, points, points.Length, tension);
			CheckDrawStatus (status);
		}

		// according to MSDN fillmode "is required but ignored" which makes _some_ sense since the unmanaged 
		// GDI+ call doesn't support it (issue spotted using Gendarme's AvoidUnusedParametersRule)
		public void DrawClosedCurve (Pen pen, PointF [] points, float tension, FillMode fillmode)
		{
			if (pen == null)
				throw new ArgumentNullException ("pen");
			if (points == null)
				throw new ArgumentNullException ("points");
			
			Status status;
			status = GDIPlus.GdipDrawClosedCurve2 (nativeObject, pen.NativePen, points, points.Length, tension);
			CheckDrawStatus (status);
		}
		
		public void DrawCurve (Pen pen, Point [] points)
		{
			if (pen == null)
				throw new ArgumentNullException ("pen");
			if (points == null)
				throw new ArgumentNullException ("points");
			
			Status status;
			status = GDIPlus.GdipDrawCurveI (nativeObject, pen.NativePen, points, points.Length);
			CheckDrawStatus (status);
		}
		
		public void DrawCurve (Pen pen, PointF [] points)
		{
			if (pen == null)
				throw new ArgumentNullException ("pen");
			if (points == null)
				throw new ArgumentNullException ("points");
			
			Status status;
			status = GDIPlus.GdipDrawCurve (nativeObject, pen.NativePen, points, points.Length);
			CheckDrawStatus (status);
		}
		
		public void DrawCurve (Pen pen, PointF [] points, float tension)
		{
			if (pen == null)
				throw new ArgumentNullException ("pen");
			if (points == null)
				throw new ArgumentNullException ("points");
			
			Status status;
			status = GDIPlus.GdipDrawCurve2 (nativeObject, pen.NativePen, points, points.Length, tension);
			CheckDrawStatus (status);
		}
		
		public void DrawCurve (Pen pen, Point [] points, float tension)
		{
			if (pen == null)
				throw new ArgumentNullException ("pen");
			if (points == null)
				throw new ArgumentNullException ("points");
			
			Status status;
			status = GDIPlus.GdipDrawCurve2I (nativeObject, pen.NativePen, points, points.Length, tension);		
			CheckDrawStatus (status);
		}
		
		public void DrawCurve (Pen pen, PointF [] points, int offset, int numberOfSegments)
		{
			if (pen == null)
				throw new ArgumentNullException ("pen");
			if (points == null)
				throw new ArgumentNullException ("points");
			
			Status status;
			status = GDIPlus.GdipDrawCurve3 (nativeObject, pen.NativePen,
							points, points.Length, offset,
							numberOfSegments, 0.5f);
			CheckDrawStatus (status);
		}

		public void DrawCurve (Pen pen, Point [] points, int offset, int numberOfSegments, float tension)
		{
			if (pen == null)
				throw new ArgumentNullException ("pen");
			if (points == null)
				throw new ArgumentNullException ("points");
			
			Status status;
			status = GDIPlus.GdipDrawCurve3I (nativeObject, pen.NativePen,
							points, points.Length, offset,
							numberOfSegments, tension);
			CheckDrawStatus (status);
		}

		public void DrawCurve (Pen pen, PointF [] points, int offset, int numberOfSegments, float tension)
		{
			if (pen == null)
				throw new ArgumentNullException ("pen");
			if (points == null)
				throw new ArgumentNullException ("points");
			
			Status status;
			status = GDIPlus.GdipDrawCurve3 (nativeObject, pen.NativePen,
							points, points.Length, offset,
							numberOfSegments, tension);
			CheckDrawStatus (status);
		}

		public void DrawEllipse (Pen pen, Rectangle rect)
		{
			if (pen == null)
				throw new ArgumentNullException ("pen");
			
			DrawEllipse (pen, rect.X, rect.Y, rect.Width, rect.Height);
		}

		public void DrawEllipse (Pen pen, RectangleF rect)
		{
			if (pen == null)
				throw new ArgumentNullException ("pen");
			DrawEllipse (pen, rect.X, rect.Y, rect.Width, rect.Height);
		}

		public void DrawEllipse (Pen pen, int x, int y, int width, int height)
		{
			if (pen == null)
				throw new ArgumentNullException ("pen");
			// An ellipse outline is a full circle of arc, and the recorder has an arc primitive. It
			// had no path here at all, so the call went to gdiplus with no surface behind it and drew
			// nothing: a radio button lost its ring and kept only the dot inside it, because the dot
			// is a FILL and fills were already recorded.
			if (RecordPen (pen)) { GpuRecorder.DrawArc (x, y, width, height, 0f, 360f, ArgbOf (pen), pen.Width); return; }
			Status status;
			status = GDIPlus.GdipDrawEllipseI (nativeObject, pen.NativePen, x, y, width, height);
			CheckDrawStatus (status);
		}

		public void DrawEllipse (Pen pen, float x, float y, float width, float height)
		{
			if (pen == null)
				throw new ArgumentNullException ("pen");
			if (RecordPen (pen)) { GpuRecorder.DrawArc (x, y, width, height, 0f, 360f, ArgbOf (pen), pen.Width); return; }
			Status status = GDIPlus.GdipDrawEllipse (nativeObject, pen.NativePen, x, y, width, height);
			CheckDrawStatus (status);
		}

		public void DrawIcon (Icon icon, Rectangle targetRect)
		{
			if (icon == null)
				throw new ArgumentNullException ("icon");

			DrawImage (icon.GetInternalBitmap (), targetRect);
		}

		public void DrawIcon (Icon icon, int x, int y)
		{
			if (icon == null)
				throw new ArgumentNullException ("icon");

			DrawImage (icon.GetInternalBitmap (), x, y);
		}

		public void DrawIconUnstretched (Icon icon, Rectangle targetRect)
		{
			if (icon == null)
				throw new ArgumentNullException ("icon");

			DrawImageUnscaled (icon.GetInternalBitmap (), targetRect);
		}
		
		public void DrawImage (Image image, RectangleF rect)
		{
			if (image == null)
				throw new ArgumentNullException ("image");
			if (RecordImage (image, rect.X, rect.Y, rect.Width, rect.Height)) return;
			Status status = GDIPlus.GdipDrawImageRect(nativeObject, image.NativeObject, rect.X, rect.Y, rect.Width, rect.Height);
			CheckDrawStatus (status);
		}

		public void DrawImage (Image image, PointF point)
		{
			if (image == null)
				throw new ArgumentNullException ("image");
			if (RecordImage (image, point.X, point.Y, image.Width, image.Height)) return;
			Status status = GDIPlus.GdipDrawImage (nativeObject, image.NativeObject, point.X, point.Y);
			CheckDrawStatus (status);
		}

		public void DrawImage (Image image, Point [] destPoints)
		{
			if (image == null)
				throw new ArgumentNullException ("image");
			if (destPoints == null)
				throw new ArgumentNullException ("destPoints");
			
			Status status = GDIPlus.GdipDrawImagePointsI (nativeObject, image.NativeObject, destPoints, destPoints.Length);
			CheckDrawStatus (status);
		}

		public void DrawImage (Image image, Point point)
		{
			if (image == null)
				throw new ArgumentNullException ("image");
			DrawImage (image, point.X, point.Y);
		}

		public void DrawImage (Image image, Rectangle rect)
		{
			if (image == null)
				throw new ArgumentNullException ("image");
			DrawImage (image, rect.X, rect.Y, rect.Width, rect.Height);
		}

		public void DrawImage (Image image, PointF [] destPoints)
		{
			if (image == null)
				throw new ArgumentNullException ("image");
			if (destPoints == null)
				throw new ArgumentNullException ("destPoints");
			Status status = GDIPlus.GdipDrawImagePoints (nativeObject, image.NativeObject, destPoints, destPoints.Length);
			CheckDrawStatus (status);
		}

		public void DrawImage (Image image, int x, int y)
		{
			if (image == null)
				throw new ArgumentNullException ("image");
			if (RecordImage (image, x, y, image.Width, image.Height)) return;
			Status status = GDIPlus.GdipDrawImageI (nativeObject, image.NativeObject, x, y);
			CheckDrawStatus (status);
		}

		public void DrawImage (Image image, float x, float y)
		{
			if (image == null)
				throw new ArgumentNullException ("image");
			if (RecordImage (image, x, y, image.Width, image.Height)) return;
			Status status = GDIPlus.GdipDrawImage (nativeObject, image.NativeObject, x, y);
			CheckDrawStatus (status);
		}

		public void DrawImage (Image image, Rectangle destRect, Rectangle srcRect, GraphicsUnit srcUnit)
		{
			if (image == null)
				throw new ArgumentNullException ("image");
			Status status = GDIPlus.GdipDrawImageRectRectI (nativeObject, image.NativeObject,
				destRect.X, destRect.Y, destRect.Width, destRect.Height,
				srcRect.X, srcRect.Y, srcRect.Width, srcRect.Height,
				srcUnit, IntPtr.Zero, null, IntPtr.Zero);
			CheckDrawStatus (status);
		}
		
		public void DrawImage (Image image, RectangleF destRect, RectangleF srcRect, GraphicsUnit srcUnit)
		{			
			if (image == null)
				throw new ArgumentNullException ("image");
			Status status = GDIPlus.GdipDrawImageRectRect (nativeObject, image.NativeObject,
				destRect.X, destRect.Y, destRect.Width, destRect.Height,
				srcRect.X, srcRect.Y, srcRect.Width, srcRect.Height,
				srcUnit, IntPtr.Zero, null, IntPtr.Zero);
			CheckDrawStatus (status);
		}

		public void DrawImage (Image image, Point [] destPoints, Rectangle srcRect, GraphicsUnit srcUnit)
		{
			if (image == null)
				throw new ArgumentNullException ("image");
			if (destPoints == null)
				throw new ArgumentNullException ("destPoints");
			
			Status status = GDIPlus.GdipDrawImagePointsRectI (nativeObject, image.NativeObject,
				destPoints, destPoints.Length , srcRect.X, srcRect.Y, 
				srcRect.Width, srcRect.Height, srcUnit, IntPtr.Zero, 
				null, IntPtr.Zero);
			CheckDrawStatus (status);
		}

		public void DrawImage (Image image, PointF [] destPoints, RectangleF srcRect, GraphicsUnit srcUnit)
		{
			if (image == null)
				throw new ArgumentNullException ("image");
			if (destPoints == null)
				throw new ArgumentNullException ("destPoints");
			
			Status status = GDIPlus.GdipDrawImagePointsRect (nativeObject, image.NativeObject,
				destPoints, destPoints.Length , srcRect.X, srcRect.Y, 
				srcRect.Width, srcRect.Height, srcUnit, IntPtr.Zero, 
				null, IntPtr.Zero);
			CheckDrawStatus (status);
		}

		public void DrawImage (Image image, Point [] destPoints, Rectangle srcRect, GraphicsUnit srcUnit, 
                                ImageAttributes imageAttr)
		{
			if (image == null)
				throw new ArgumentNullException ("image");
			if (destPoints == null)
				throw new ArgumentNullException ("destPoints");
			Status status = GDIPlus.GdipDrawImagePointsRectI (nativeObject, image.NativeObject,
				destPoints, destPoints.Length , srcRect.X, srcRect.Y,
				srcRect.Width, srcRect.Height, srcUnit,
				imageAttr != null ? imageAttr.nativeImageAttributes : IntPtr.Zero, null, IntPtr.Zero);
			CheckDrawStatus (status);
		}
		
		public void DrawImage (Image image, float x, float y, float width, float height)
		{
			if (image == null)
				throw new ArgumentNullException ("image");
			if (RecordImage (image, x, y, width, height)) return;
			Status status = GDIPlus.GdipDrawImageRect(nativeObject, image.NativeObject, x, y,
                           width, height);
			CheckDrawStatus (status);
		}

		public void DrawImage (Image image, PointF [] destPoints, RectangleF srcRect, GraphicsUnit srcUnit, 
                                ImageAttributes imageAttr)
		{
			if (image == null)
				throw new ArgumentNullException ("image");
			if (destPoints == null)
				throw new ArgumentNullException ("destPoints");
			Status status = GDIPlus.GdipDrawImagePointsRect (nativeObject, image.NativeObject,
				destPoints, destPoints.Length , srcRect.X, srcRect.Y,
				srcRect.Width, srcRect.Height, srcUnit, 
				imageAttr != null ? imageAttr.nativeImageAttributes : IntPtr.Zero, null, IntPtr.Zero);
			CheckDrawStatus (status);
		}

		public void DrawImage (Image image, int x, int y, Rectangle srcRect, GraphicsUnit srcUnit)
		{			
			if (image == null)
				throw new ArgumentNullException ("image");
			Status status = GDIPlus.GdipDrawImagePointRectI(nativeObject, image.NativeObject, x, y, srcRect.X, srcRect.Y, srcRect.Width, srcRect.Height, srcUnit);
			CheckDrawStatus (status);
		}
		
		public void DrawImage (Image image, int x, int y, int width, int height)
		{
			if (image == null)
				throw new ArgumentNullException ("image");
			if (RecordImage (image, x, y, width, height)) return;
			Status status = GDIPlus.GdipDrawImageRectI (nativeObject, image.nativeObject, x, y, width, height);
			CheckDrawStatus (status);
		}

		public void DrawImage (Image image, float x, float y, RectangleF srcRect, GraphicsUnit srcUnit)
		{			
			if (image == null)
				throw new ArgumentNullException ("image");
			Status status = GDIPlus.GdipDrawImagePointRect (nativeObject, image.nativeObject, x, y, srcRect.X, srcRect.Y, srcRect.Width, srcRect.Height, srcUnit);
			CheckDrawStatus (status);
		}

		public void DrawImage (Image image, PointF [] destPoints, RectangleF srcRect, GraphicsUnit srcUnit, ImageAttributes imageAttr, DrawImageAbort callback)
		{
			if (image == null)
				throw new ArgumentNullException ("image");
			if (destPoints == null)
				throw new ArgumentNullException ("destPoints");
			Status status = GDIPlus.GdipDrawImagePointsRect (nativeObject, image.NativeObject,
				destPoints, destPoints.Length , srcRect.X, srcRect.Y,
				srcRect.Width, srcRect.Height, srcUnit, 
				imageAttr != null ? imageAttr.nativeImageAttributes : IntPtr.Zero, callback, IntPtr.Zero);
			CheckDrawStatus (status);
		}

		public void DrawImage (Image image, Point [] destPoints, Rectangle srcRect, GraphicsUnit srcUnit, ImageAttributes imageAttr, DrawImageAbort callback)
		{
			if (image == null)
				throw new ArgumentNullException ("image");
			if (destPoints == null)
				throw new ArgumentNullException ("destPoints");
			
			Status status = GDIPlus.GdipDrawImagePointsRectI (nativeObject, image.NativeObject,
				destPoints, destPoints.Length , srcRect.X, srcRect.Y,
				srcRect.Width, srcRect.Height, srcUnit, 
				imageAttr != null ? imageAttr.nativeImageAttributes : IntPtr.Zero, callback, IntPtr.Zero);
			CheckDrawStatus (status);
		}

		public void DrawImage (Image image, Point [] destPoints, Rectangle srcRect, GraphicsUnit srcUnit, ImageAttributes imageAttr, DrawImageAbort callback, int callbackData)
		{
			if (image == null)
				throw new ArgumentNullException ("image");
			if (destPoints == null)
				throw new ArgumentNullException ("destPoints");

			Status status = GDIPlus.GdipDrawImagePointsRectI (nativeObject, image.NativeObject,
				destPoints, destPoints.Length , srcRect.X, srcRect.Y, 
				srcRect.Width, srcRect.Height, srcUnit, 
				imageAttr != null ? imageAttr.nativeImageAttributes : IntPtr.Zero, callback, (IntPtr) callbackData);
			CheckDrawStatus (status);
		}

		public void DrawImage (Image image, Rectangle destRect, float srcX, float srcY, float srcWidth, float srcHeight, GraphicsUnit srcUnit)
		{
			if (image == null)
				throw new ArgumentNullException ("image");
			Status status = GDIPlus.GdipDrawImageRectRect (nativeObject, image.NativeObject,
                                destRect.X, destRect.Y, destRect.Width, destRect.Height,
                       		srcX, srcY, srcWidth, srcHeight, srcUnit, IntPtr.Zero, 
                       		null, IntPtr.Zero);
			CheckDrawStatus (status);
		}
		
		public void DrawImage (Image image, PointF [] destPoints, RectangleF srcRect, GraphicsUnit srcUnit, ImageAttributes imageAttr, DrawImageAbort callback, int callbackData)
		{
			Status status = GDIPlus.GdipDrawImagePointsRect (nativeObject, image.NativeObject,
				destPoints, destPoints.Length , srcRect.X, srcRect.Y,
				srcRect.Width, srcRect.Height, srcUnit, 
				imageAttr != null ? imageAttr.nativeImageAttributes : IntPtr.Zero, callback, (IntPtr) callbackData);
			CheckDrawStatus (status);
		}

		public void DrawImage (Image image, Rectangle destRect, int srcX, int srcY, int srcWidth, int srcHeight, GraphicsUnit srcUnit)
		{
			if (image == null)
				throw new ArgumentNullException ("image");
			Status status = GDIPlus.GdipDrawImageRectRectI (nativeObject, image.NativeObject,
                                destRect.X, destRect.Y, destRect.Width, destRect.Height,
                       		srcX, srcY, srcWidth, srcHeight, srcUnit, IntPtr.Zero, 
                       		null, IntPtr.Zero);
			CheckDrawStatus (status);
		}

		public void DrawImage (Image image, Rectangle destRect, float srcX, float srcY, float srcWidth, float srcHeight, GraphicsUnit srcUnit, ImageAttributes imageAttrs)
		{
			if (image == null)
				throw new ArgumentNullException ("image");
			Status status = GDIPlus.GdipDrawImageRectRect (nativeObject, image.NativeObject,
                                destRect.X, destRect.Y, destRect.Width, destRect.Height,
                       		srcX, srcY, srcWidth, srcHeight, srcUnit,
				imageAttrs != null ? imageAttrs.nativeImageAttributes : IntPtr.Zero, null, IntPtr.Zero);
			CheckDrawStatus (status);
		}
		
		public void DrawImage (Image image, Rectangle destRect, int srcX, int srcY, int srcWidth, int srcHeight, GraphicsUnit srcUnit, ImageAttributes imageAttr)
		{			
			if (image == null)
				throw new ArgumentNullException ("image");
			Status status = GDIPlus.GdipDrawImageRectRectI (nativeObject, image.NativeObject, 
                                        destRect.X, destRect.Y, destRect.Width, 
					destRect.Height, srcX, srcY, srcWidth, srcHeight,
					srcUnit, imageAttr != null ? imageAttr.nativeImageAttributes : IntPtr.Zero, null, IntPtr.Zero);
			CheckDrawStatus (status);
		}
		
		public void DrawImage (Image image, Rectangle destRect, int srcX, int srcY, int srcWidth, int srcHeight, GraphicsUnit srcUnit, ImageAttributes imageAttr, DrawImageAbort callback)
		{
			if (image == null)
				throw new ArgumentNullException ("image");
			Status status = GDIPlus.GdipDrawImageRectRectI (nativeObject, image.NativeObject, 
                                        destRect.X, destRect.Y, destRect.Width, 
					destRect.Height, srcX, srcY, srcWidth, srcHeight,
					srcUnit, imageAttr != null ? imageAttr.nativeImageAttributes : IntPtr.Zero, callback,
					IntPtr.Zero);
			CheckDrawStatus (status);
		}
		
		public void DrawImage (Image image, Rectangle destRect, float srcX, float srcY, float srcWidth, float srcHeight, GraphicsUnit srcUnit, ImageAttributes imageAttrs, DrawImageAbort callback)
		{
			if (image == null)
				throw new ArgumentNullException ("image");
			Status status = GDIPlus.GdipDrawImageRectRect (nativeObject, image.NativeObject, 
                                        destRect.X, destRect.Y, destRect.Width, 
					destRect.Height, srcX, srcY, srcWidth, srcHeight,
					srcUnit, imageAttrs != null ? imageAttrs.nativeImageAttributes : IntPtr.Zero, 
					callback, IntPtr.Zero);
			CheckDrawStatus (status);
		}

		public void DrawImage (Image image, Rectangle destRect, float srcX, float srcY, float srcWidth, float srcHeight, GraphicsUnit srcUnit, ImageAttributes imageAttrs, DrawImageAbort callback, IntPtr callbackData)
		{
			if (image == null)
				throw new ArgumentNullException ("image");
			Status status = GDIPlus.GdipDrawImageRectRect (nativeObject, image.NativeObject, 
				destRect.X, destRect.Y, destRect.Width, destRect.Height,
				srcX, srcY, srcWidth, srcHeight, srcUnit, 
				imageAttrs != null ? imageAttrs.nativeImageAttributes : IntPtr.Zero, callback, callbackData);
			CheckDrawStatus (status);
		}

		public void DrawImage (Image image, Rectangle destRect, int srcX, int srcY, int srcWidth, int srcHeight, GraphicsUnit srcUnit, ImageAttributes imageAttrs, DrawImageAbort callback, IntPtr callbackData)
		{
			if (image == null)
				throw new ArgumentNullException ("image");
			Status status = GDIPlus.GdipDrawImageRectRect (nativeObject, image.NativeObject, 
                       		destRect.X, destRect.Y, destRect.Width, destRect.Height,
				srcX, srcY, srcWidth, srcHeight, srcUnit,
				imageAttrs != null ? imageAttrs.nativeImageAttributes : IntPtr.Zero, callback, callbackData);
			CheckDrawStatus (status);
		}		
		
		public void DrawImageUnscaled (Image image, Point point)
		{
			DrawImageUnscaled (image, point.X, point.Y);
		}
		
		public void DrawImageUnscaled (Image image, Rectangle rect)
		{
			DrawImageUnscaled (image, rect.X, rect.Y, rect.Width, rect.Height);
		}
		
		public void DrawImageUnscaled (Image image, int x, int y)
		{
			if (image == null)
				throw new ArgumentNullException ("image");
			DrawImage (image, x, y, image.Width, image.Height);
		}

		public void DrawImageUnscaled (Image image, int x, int y, int width, int height)
		{
			if (image == null)
				throw new ArgumentNullException ("image");

			// avoid creating an empty, or negative w/h, bitmap...
			if ((width <= 0) || (height <= 0))
				return;

			using (Image tmpImg = new Bitmap (width, height)) {
				using (Graphics g = FromImage (tmpImg)) {
					g.DrawImage (image, 0, 0, image.Width, image.Height);
					DrawImage (tmpImg, x, y, width, height);
				}
			}
		}

		public void DrawImageUnscaledAndClipped (Image image, Rectangle rect)
		{
			if (image == null)
				throw new ArgumentNullException ("image");

			int width = (image.Width > rect.Width) ? rect.Width : image.Width;
			int height = (image.Height > rect.Height) ? rect.Height : image.Height;

			DrawImageUnscaled (image, rect.X, rect.Y, width, height);			
		}

		public void DrawLine (Pen pen, PointF pt1, PointF pt2)
		{
			if (pen == null)
				throw new ArgumentNullException ("pen");
                        Status status = GDIPlus.GdipDrawLine (nativeObject, pen.NativePen,
		                                pt1.X, pt1.Y, pt2.X, pt2.Y);
			CheckDrawStatus (status);
		}

		public void DrawLine (Pen pen, Point pt1, Point pt2)
		{
			if (pen == null)
				throw new ArgumentNullException ("pen");
			if (RecordPen (pen)) {
				if (RecordDash (pen, out float [] dash))
					GpuRecorder.DrawDashedLine (pt1.X, pt1.Y, pt2.X, pt2.Y, ArgbOf (pen), pen.Width, dash);
				else
					GpuRecorder.DrawLine (pt1.X, pt1.Y, pt2.X, pt2.Y, ArgbOf (pen));
				return;
			}
                        Status status = GDIPlus.GdipDrawLineI (nativeObject, pen.NativePen,
		                                pt1.X, pt1.Y, pt2.X, pt2.Y);
			CheckDrawStatus (status);
		}

		public void DrawLine (Pen pen, int x1, int y1, int x2, int y2)
		{
			if (pen == null)
				throw new ArgumentNullException ("pen");
			if (RecordPen (pen)) {
				if (RecordDash (pen, out float [] dash))
					GpuRecorder.DrawDashedLine (x1, y1, x2, y2, ArgbOf (pen), pen.Width, dash);
				else
					GpuRecorder.DrawLine (x1, y1, x2, y2, ArgbOf (pen));
				return;
			}
			Status status = GDIPlus.GdipDrawLineI (nativeObject, pen.NativePen, x1, y1, x2, y2);
			CheckDrawStatus (status);
		}

		public void DrawLine (Pen pen, float x1, float y1, float x2, float y2)
		{
			if (pen == null)
				throw new ArgumentNullException ("pen");
			if (!float.IsNaN(x1) && !float.IsNaN(y1) &&
			    !float.IsNaN(x2) && !float.IsNaN(y2)) {
				if (RecordPen (pen)) {
					if (RecordDash (pen, out float [] dash))
						GpuRecorder.DrawDashedLine (x1, y1, x2, y2, ArgbOf (pen), pen.Width, dash);
					else
						GpuRecorder.DrawLine (x1, y1, x2, y2, ArgbOf (pen));
					return;
				}
				Status status = GDIPlus.GdipDrawLine (nativeObject, pen.NativePen, x1, y1, x2, y2);
				CheckDrawStatus (status);
			}
		}

		public void DrawLines (Pen pen, PointF [] points)
		{
			if (pen == null)
				throw new ArgumentNullException ("pen");
			if (points == null)
				throw new ArgumentNullException ("points");
			if (RecordPen (pen)) {
				int c = ArgbOf (pen);
				for (int i = 1; i < points.Length; i++)
					GpuRecorder.DrawLine (points[i-1].X, points[i-1].Y, points[i].X, points[i].Y, c);
				return;
			}
			Status status = GDIPlus.GdipDrawLines (nativeObject, pen.NativePen, points, points.Length);
			CheckDrawStatus (status);
		}

		public void DrawLines (Pen pen, Point [] points)
		{
			if (pen == null)
				throw new ArgumentNullException ("pen");
			if (points == null)
				throw new ArgumentNullException ("points");
			if (RecordPen (pen)) {
				int c = ArgbOf (pen);
				for (int i = 1; i < points.Length; i++)
					GpuRecorder.DrawLine (points[i-1].X, points[i-1].Y, points[i].X, points[i].Y, c);
				return;
			}
			Status status = GDIPlus.GdipDrawLinesI (nativeObject, pen.NativePen, points, points.Length);
			CheckDrawStatus (status);
		}

		public void DrawPath (Pen pen, GraphicsPath path)
		{
			if (pen == null)
				throw new ArgumentNullException ("pen");
			if (path == null)
				throw new ArgumentNullException ("path");
			if (RecordPen (pen)) {
				int c = ArgbOf (pen);
				RecordDash (pen, out float [] dash);
				foreach (PointF [] sub in FlattenSubpaths (path)) {
					for (int i = 1; i < sub.Length; i++)
						if (dash != null)
							GpuRecorder.DrawDashedLine (sub [i - 1].X, sub [i - 1].Y, sub [i].X, sub [i].Y, c, pen.Width, dash);
						else
							GpuRecorder.DrawLine (sub [i - 1].X, sub [i - 1].Y, sub [i].X, sub [i].Y, c);
					// A flattened closed figure does not repeat its first point; join it up.
					if (sub.Length > 2 && sub [0] != sub [sub.Length - 1])
						GpuRecorder.DrawLine (sub [sub.Length - 1].X, sub [sub.Length - 1].Y, sub [0].X, sub [0].Y, c);
				}
				return;
			}
			Status status = GDIPlus.GdipDrawPath (nativeObject, pen.NativePen, path.nativePath);
			CheckDrawStatus (status);
		}
		
		public void DrawPie (Pen pen, Rectangle rect, float startAngle, float sweepAngle)
		{
			if (pen == null)
				throw new ArgumentNullException ("pen");
			DrawPie (pen, rect.X, rect.Y, rect.Width, rect.Height, startAngle, sweepAngle);
		}
		
		public void DrawPie (Pen pen, RectangleF rect, float startAngle, float sweepAngle)
		{
			if (pen == null)
				throw new ArgumentNullException ("pen");
			DrawPie (pen, rect.X, rect.Y, rect.Width, rect.Height, startAngle, sweepAngle);
		}
		
		public void DrawPie (Pen pen, float x, float y, float width, float height, float startAngle, float sweepAngle)
		{
			if (pen == null)
				throw new ArgumentNullException ("pen");
			Status status = GDIPlus.GdipDrawPie (nativeObject, pen.NativePen, x, y, width, height, startAngle, sweepAngle);
			CheckDrawStatus (status);
		}
		
		// Microsoft documentation states that the signature for this member should be
		// public void DrawPie(Pen pen, int x,  int y,  int width,   int height,   int startAngle
   		// int sweepAngle. However, GdipDrawPieI uses also float for the startAngle and sweepAngle params
   		public void DrawPie (Pen pen, int x, int y, int width, int height, int startAngle, int sweepAngle)
		{
			if (pen == null)
				throw new ArgumentNullException ("pen");
			Status status = GDIPlus.GdipDrawPieI (nativeObject, pen.NativePen, x, y, width, height, startAngle, sweepAngle);
			CheckDrawStatus (status);
		}

		public void DrawPolygon (Pen pen, Point [] points)
		{
			if (pen == null)
				throw new ArgumentNullException ("pen");
			if (points == null)
				throw new ArgumentNullException ("points");
			if (RecordPen (pen)) {
				int c = ArgbOf (pen);
				for (int i = 0; i < points.Length; i++) {
					var a = points[i]; var b = points[(i + 1) % points.Length];   // closed
					GpuRecorder.DrawLine (a.X, a.Y, b.X, b.Y, c);
				}
				return;
			}
			Status status = GDIPlus.GdipDrawPolygonI (nativeObject, pen.NativePen, points, points.Length);
			CheckDrawStatus (status);
		}

		public void DrawPolygon (Pen pen, PointF [] points)
		{
			if (pen == null)
				throw new ArgumentNullException ("pen");
			if (points == null)
				throw new ArgumentNullException ("points");
			if (RecordPen (pen)) {
				int c = ArgbOf (pen);
				for (int i = 0; i < points.Length; i++) {
					var a = points[i]; var b = points[(i + 1) % points.Length];   // closed
					GpuRecorder.DrawLine (a.X, a.Y, b.X, b.Y, c);
				}
				return;
			}
			Status status = GDIPlus.GdipDrawPolygon (nativeObject, pen.NativePen, points, points.Length);
			CheckDrawStatus (status);
		}

		public void DrawRectangle (Pen pen, Rectangle rect)
		{
			if (pen == null)
				throw new ArgumentNullException ("pen");
			DrawRectangle (pen, rect.Left, rect.Top, rect.Width, rect.Height);
		}

		public void DrawRectangle (Pen pen, float x, float y, float width, float height)
		{
			if (pen == null)
				throw new ArgumentNullException ("pen");
			if (RecordPen (pen)) {
				int c = ArgbOf (pen);
				if (RecordDash (pen, out float [] dash)) {
					GpuRecorder.DrawDashedLine (x, y, x + width, y, c, pen.Width, dash);
					GpuRecorder.DrawDashedLine (x, y + height, x + width, y + height, c, pen.Width, dash);
					GpuRecorder.DrawDashedLine (x, y, x, y + height, c, pen.Width, dash);
					GpuRecorder.DrawDashedLine (x + width, y, x + width, y + height, c, pen.Width, dash);
					return;
				}
				GpuRecorder.DrawLine (x, y, x + width, y, c);
				GpuRecorder.DrawLine (x, y + height, x + width, y + height, c);
				GpuRecorder.DrawLine (x, y, x, y + height, c);
				GpuRecorder.DrawLine (x + width, y, x + width, y + height, c);
				return;
			}
			Status status = GDIPlus.GdipDrawRectangle (nativeObject, pen.NativePen, x, y, width, height);
			CheckDrawStatus (status);
		}

		public void DrawRectangle (Pen pen, int x, int y, int width, int height)
		{
			if (pen == null)
				throw new ArgumentNullException ("pen");
			if (RecordPen (pen)) {
				int c = ArgbOf (pen);
				if (RecordDash (pen, out float [] dash)) {
					GpuRecorder.DrawDashedLine (x, y, x + width, y, c, pen.Width, dash);
					GpuRecorder.DrawDashedLine (x, y + height, x + width, y + height, c, pen.Width, dash);
					GpuRecorder.DrawDashedLine (x, y, x, y + height, c, pen.Width, dash);
					GpuRecorder.DrawDashedLine (x + width, y, x + width, y + height, c, pen.Width, dash);
					return;
				}
				GpuRecorder.DrawLine (x, y, x + width, y, c);
				GpuRecorder.DrawLine (x, y + height, x + width, y + height, c);
				GpuRecorder.DrawLine (x, y, x, y + height, c);
				GpuRecorder.DrawLine (x + width, y, x + width, y + height, c);
				return;
			}
			Status status = GDIPlus.GdipDrawRectangleI (nativeObject, pen.NativePen, x, y, width, height);
			CheckDrawStatus (status);
		}

		public void DrawRectangles (Pen pen, RectangleF [] rects)
		{
			if (pen == null)
				throw new ArgumentNullException ("image");
			if (rects == null)
				throw new ArgumentNullException ("rects");
			Status status = GDIPlus.GdipDrawRectangles (nativeObject, pen.NativePen, rects, rects.Length);
			CheckDrawStatus (status);
		}

		public void DrawRectangles (Pen pen, Rectangle [] rects)
		{
			if (pen == null)
				throw new ArgumentNullException ("image");
			if (rects == null)
				throw new ArgumentNullException ("rects");
			Status status = GDIPlus.GdipDrawRectanglesI (nativeObject, pen.NativePen, rects, rects.Length);
			CheckDrawStatus (status);
		}

		public void DrawString (string s, Font font, Brush brush, RectangleF layoutRectangle)
		{
			DrawString (s, font, brush, layoutRectangle, null);
		}

		public void DrawString (string s, Font font, Brush brush, PointF point)
		{
			DrawString (s, font, brush, new RectangleF (point.X, point.Y, 0, 0), null);
		}

		public void DrawString (string s, Font font, Brush brush, PointF point, StringFormat format)
		{
			DrawString(s, font, brush, new RectangleF(point.X, point.Y, 0, 0), format);
		}

		public void DrawString (string s, Font font, Brush brush, float x, float y)
		{
			DrawString (s, font, brush, new RectangleF (x, y, 0, 0), null);
		}

		public void DrawString (string s, Font font, Brush brush, float x, float y, StringFormat format)
		{
			DrawString (s, font, brush, new RectangleF(x, y, 0, 0), format);
		}

		/// <summary>Remove the hotkey markers from <paramref name="text"/>, reporting where the
		/// mnemonic character ended up. "&amp;&amp;" is a literal ampersand, as in Win32.</summary>
		static string StripHotkeyPrefix (string text, out int index)
		{
			index = -1;
			if (text.IndexOf ('&') < 0) return text;

			var sb = new System.Text.StringBuilder (text.Length);
			for (int i = 0; i < text.Length; i++) {
				if (text[i] != '&') { sb.Append (text[i]); continue; }
				if (i + 1 < text.Length && text[i + 1] == '&') { sb.Append ('&'); i++; continue; }
				if (i + 1 < text.Length && index < 0) index = sb.Length;
			}
			return sb.ToString ();
		}

		/// <summary>Break a string into the lines GDI+ would draw it as: at its own newlines, and
		/// again wherever a line runs past the layout rectangle. The recorder draws a run wherever
		/// it is told and has no idea a rectangle was involved, so a paragraph handed to DrawString
		/// with a width came out as one very long line -- the scrolling credits in SharpDevelop's
		/// About box ran off the side of the dialog instead of filling the column.</summary>
		static string[] WrapLines (string text, float emPx, int sims, string family, float width, StringFormat format)
		{
			string[] hard = text.Split ('\n');
			// A rectangle with no width is a point, not a column; NoWrap is the caller saying so
			// outright. Either way GDI+ lets the line run.
			if (width <= 0 || (format != null && (format.FormatFlags & StringFormatFlags.NoWrap) != 0))
				return hard;

			var outLines = new System.Collections.Generic.List<string> (hard.Length);
			foreach (string raw in hard) {
				string line = raw.TrimEnd ('\r');
				if (line.Length == 0) { outLines.Add (line); continue; }

				float lineWidth;
				WebGpuBackend.GpuRaster.MeasureText (line, emPx, sims, family, out lineWidth, out float _);
				if (lineWidth <= width) { outLines.Add (line); continue; }

				// Break at spaces, and only inside a word when a single word is wider than the
				// column -- which is what GDI+ does with a long path or identifier.
				int start = 0;
				while (start < line.Length) {
					int fit = FitCount (line, start, emPx, sims, family, width);
					int brk = -1;
					for (int i = start + fit - 1; i > start; i--)
						if (line[i] == ' ') { brk = i; break; }
					int take = brk > start ? brk - start : fit;
					outLines.Add (line.Substring (start, take));
					start += take;
					while (start < line.Length && line[start] == ' ') start++;
				}
			}
			return outLines.ToArray ();
		}

		/// <summary>How many characters from <paramref name="start"/> fit in <paramref name="width"/>,
		/// at least one so a column narrower than a single glyph still makes progress.</summary>
		static int FitCount (string line, int start, float emPx, int sims, string family, float width)
		{
			int lo = 1, hi = line.Length - start;
			while (lo < hi) {
				int mid = (lo + hi + 1) / 2;
				WebGpuBackend.GpuRaster.MeasureText (line.Substring (start, mid), emPx, sims, family, out float w, out float _);
				if (w <= width) lo = mid; else hi = mid - 1;
			}
			return lo;
		}

		/// <summary>How far down a line box the baseline sits: the font's ascent, truncated the
		/// way Windows truncates a scaled metric. Segoe UI at nine point asks for 12.95 pixels
		/// and Windows uses 12, which is why text drawn on the fraction sat a pixel low.</summary>
		static float Ascent (Font font, float emPx)
		{
			FontFamily family = font.FontFamily;
			if (family == null)
				return 0.8f * emPx;
			try {
				int em = family.GetEmHeight (font.Style);
				if (em <= 0)
					return 0.8f * emPx;
				return (float) Math.Floor (family.GetCellAscent (font.Style) * emPx / em);
			} catch (Exception) {
				return 0.8f * emPx;
			}
		}

		/// <summary>The margin a string is laid out inside: a sixth of the font's height, which is
		/// what both GDI+ and Windows leave for a glyph that overhangs its cell. A typographic
		/// format asks for none -- that is what it is for.</summary>
		static float Overhang (Font font, StringFormat format)
		{
			if (format != null && format.IsTypographic)
				return 0f;
			return (float) Math.Ceiling (font.Height / 6f);
		}

		/// <summary>Both margins together: the one before the first glyph and the wider one after
		/// the last, which leaves room for an italic's tail. A measurement has to include them,
		/// because they are room the drawing will use.</summary>
		static float Margins (Font font, StringFormat format)
		{
			float left = Overhang (font, format);
			return left == 0f ? 0f : left + (float) Math.Ceiling (font.Height / 6f * 1.5f);
		}

		public void DrawString (string s, Font font, Brush brush, RectangleF layoutRectangle, StringFormat format)
		{
			if (font == null)
				throw new ArgumentNullException ("font");
			if (brush == null)
				throw new ArgumentNullException ("brush");
			if (s == null || s.Length == 0)
				return;
			if (GpuRecorder != null && brush is SolidBrush) {
				// GDI+ DrawString positions by the layout rect's top-left; em size in pixels ~ points*96/72.
				// Honour the StringFormat alignment (buttons/labels centre text in a rect) by measuring
				// the run and offsetting within the layout rectangle. MeasureString uses libgdiplus for
				// MEASUREMENT only — the glyphs are still rasterized by WGSL at present time.
				float emPx = font.SizeInPoints * 96f / 72f;
				// The style the caller asked for, in the terms the renderer states them
				// (WPF's StyleSimulations: 1 = bold, 2 = italic). Without carrying this the run
				// arrives with nothing but a size and a colour, and every piece of bold text in
				// every hosted control -- a property grid's modified values, a group heading --
				// came out regular.
				int sims = (font.Bold ? 1 : 0) | (font.Italic ? 2 : 0);
				// The family the caller asked for. A run used to arrive at the renderer with
				// nothing but a size, a colour and a style, so everything came out in one
				// hard-coded face -- and a fixed-width font could not be had at all.
				string family = font.FontFamily?.Name;
				int argb = ArgbOf (brush);

				// Multi-line strings arrive here whole -- a message box's text, a multi-line Label.
				// The recorder draws ONE run, so every newline became a glyph and the whole message
				// one very long line: an exception message came out about 15000px wide, past the
				// GPU's maximum texture dimension, and the window presented nothing at all. Draw a
				// run per line, and align each line within the layout rectangle on its own.
				// GDI+ confines a string to its layout rectangle; the recorder draws a run wherever it
				// is told. WinForms leans on that: a ListView hands each subitem its column bounds as
				// the layout rect and expects the text to stop there. Unclipped, a value wider than
				// its column painted straight over the next ones -- one long assembly name covered
				// every other column of the version list.
				// "&Copy" means a C with an underline and Alt+C to press it -- StringFormat.HotkeyPrefix
				// is how WinForms asks for that, and the recorder path ignored it, so buttons and
				// menu items showed their ampersands raw.
				string text = s;
				int mnemonic = -1;
				Text.HotkeyPrefix prefix = format == null ? Text.HotkeyPrefix.None : format.HotkeyPrefix;
				if (prefix != Text.HotkeyPrefix.None)
					text = StripHotkeyPrefix (s, out mnemonic);

				// NoClip means the caller accepts overhang; otherwise GDI+ confines the string to its
				// layout rectangle, and WinForms leans on that: a ListView hands each subitem its
				// column bounds and expects the text to stop there.
				bool clipToLayout = layoutRectangle.Width > 0 && layoutRectangle.Height > 0
					&& (format == null || (format.FormatFlags & StringFormatFlags.NoClip) == 0);
				if (clipToLayout)
					GpuRecorder.SetClipRect (layoutRectangle.X, layoutRectangle.Y,
						layoutRectangle.Width, layoutRectangle.Height, false);
				// The margin left before the first glyph, so that one which overhangs its cell is not
				// clipped by the rectangle it was asked to fit in. GDI+ leaves it and Windows leaves it
				// -- a sixth of the font's height -- and this path left none, so every caption in the
				// application sat three pixels to the left of the same caption in Windows. Centred text
				// is left alone: the margins either side of it very nearly cancel.
				float overhang = Overhang (font, format);
				string[] lines = WrapLines (text, emPx, sims, family, layoutRectangle.Width, format);
				// A line of text is taller than its em square: the font's line height adds the
				// descender and its leading. Stepping by the em size instead packed every
				// multi-line run tighter than the same text drawn by Windows.
				float lineHeight = font.GetHeight ();
				if (lineHeight <= 0) lineHeight = emPx;
				// Where the glyphs sit INSIDE that line box: on a baseline as far down as the font's own
				// ascent, truncated to whole pixels exactly as Windows truncates it. Guessing at four
				// fifths of the line box came out a fraction low, and a fraction low rounds to a whole
				// pixel: every caption in the application sat one pixel below the same caption in
				// Windows. The recorder turns the y it is given into a baseline by dropping 0.8 of the
				// em size, so what it wants is the ascent less that.
				float baseline = Ascent (font, emPx) - 0.8f * emPx;
				float ty = layoutRectangle.Y;
				if (format != null && layoutRectangle.Height > 0) {
					float totalH = lineHeight * lines.Length;
					if (format.LineAlignment == StringAlignment.Center) ty += (layoutRectangle.Height - totalH) / 2f;
					else if (format.LineAlignment == StringAlignment.Far) ty += layoutRectangle.Height - totalH;
				}
				for (int i = 0; i < lines.Length; i++) {
					string line = lines[i].TrimEnd ('\r');
					if (line.Length == 0) continue;
					float tx = layoutRectangle.X;
					if (format != null && layoutRectangle.Width > 0 && format.Alignment != StringAlignment.Near) {
						// Managed measurement (no libgdiplus) with the renderer's font -> exact centring.
						WebGpuBackend.GpuRaster.MeasureText (line, emPx, sims, family, out float mw, out float mh);
						if (format.Alignment == StringAlignment.Center) tx += (layoutRectangle.Width - mw) / 2f;
						else tx += layoutRectangle.Width - mw - overhang;
					} else {
						tx += overhang;
					}
					// Underline the mnemonic, if it falls on this line.
					if (prefix == Text.HotkeyPrefix.Show && mnemonic >= 0)
					{
						int lineStart = 0;
						for (int j = 0; j < i; j++) lineStart += lines[j].Length + 1;
						int col = mnemonic - lineStart;
						if (col >= 0 && col < line.Length)
						{
							float ux = 0f, uw, unused2;
							if (col > 0)
								WebGpuBackend.GpuRaster.MeasureText (line.Substring (0, col), emPx, sims, family, out ux, out unused2);
							WebGpuBackend.GpuRaster.MeasureText (line.Substring (col, 1), emPx, sims, family, out uw, out unused2);
							float uy = ty + i * lineHeight + baseline + emPx;
							GpuRecorder.DrawLine (tx + ux, uy, tx + ux + uw, uy, argb);
						}
					}

					if (s_traceText)
						Console.Error.WriteLine ($"drawtext '{line}' at ({tx},{ty + i * emPx}) em={emPx} rect={layoutRectangle} align={(format == null ? "-" : format.Alignment.ToString ())}");
					GpuRecorder.DrawText (line, tx, ty + i * lineHeight + baseline, emPx, argb, sims, family);

					// Underline and strike-out are part of the run for GDI+, but this stack draws a run
					// as glyphs and nothing else -- the style never travelled with it, so a LinkLabel
					// came out as plain text with no rule under it. Draw the rules ourselves, on the
					// font's own scale so they hold at any size.
					if (font.Underline || font.Strikeout) {
						WebGpuBackend.GpuRaster.MeasureText (line, emPx, sims, family, out float rw, out float _);
						if (rw > 0f) {
							float top = ty + i * lineHeight + baseline;
							if (font.Underline)
								GpuRecorder.DrawLine (tx, top + emPx, tx + rw, top + emPx, argb);
							if (font.Strikeout)
								GpuRecorder.DrawLine (tx, top + emPx * 0.55f, tx + rw, top + emPx * 0.55f, argb);
						}
					}
				}

				if (clipToLayout) GpuRecorder.ClearClip ();
				return;
			}

			Status status = GDIPlus.GdipDrawString (nativeObject, s, s.Length, font.NativeObject, ref layoutRectangle, format != null ? format.NativeObject : IntPtr.Zero, brush.NativeBrush);
			CheckDrawStatus (status);
		}

		public void EndContainer (GraphicsContainer container)
		{
			if (container == null)
				throw new ArgumentNullException ("container");
			Status status = GDIPlus.GdipEndContainer(nativeObject, container.NativeObject);
			CheckDrawStatus (status);
		}

		private const string MetafileEnumeration = "Metafiles enumeration, for both WMF and EMF formats, isn't supported.";

		[MonoTODO (MetafileEnumeration)]
		public void EnumerateMetafile (Metafile metafile, Point [] destPoints, EnumerateMetafileProc callback)
		{
			throw new NotImplementedException ();
		}

		[MonoTODO (MetafileEnumeration)]
		public void EnumerateMetafile (Metafile metafile, RectangleF destRect, EnumerateMetafileProc callback)
		{
			throw new NotImplementedException ();
		}

		[MonoTODO (MetafileEnumeration)]
		public void EnumerateMetafile (Metafile metafile, PointF [] destPoints, EnumerateMetafileProc callback)
		{
			throw new NotImplementedException ();
		}

		[MonoTODO (MetafileEnumeration)]
		public void EnumerateMetafile (Metafile metafile, Rectangle destRect, EnumerateMetafileProc callback)
		{
			throw new NotImplementedException ();
		}

		[MonoTODO (MetafileEnumeration)]
		public void EnumerateMetafile (Metafile metafile, Point destPoint, EnumerateMetafileProc callback)
		{
			throw new NotImplementedException ();
		}

		[MonoTODO (MetafileEnumeration)]
		public void EnumerateMetafile (Metafile metafile, PointF destPoint, EnumerateMetafileProc callback)
		{
			throw new NotImplementedException ();
		}

		[MonoTODO (MetafileEnumeration)]
		public void EnumerateMetafile (Metafile metafile, PointF destPoint, EnumerateMetafileProc callback, IntPtr callbackData)
		{
			throw new NotImplementedException ();
		}

		[MonoTODO (MetafileEnumeration)]
		public void EnumerateMetafile (Metafile metafile, Rectangle destRect, EnumerateMetafileProc callback, IntPtr callbackData)
		{
			throw new NotImplementedException ();
		}

		[MonoTODO (MetafileEnumeration)]
		public void EnumerateMetafile (Metafile metafile, PointF [] destPoints, EnumerateMetafileProc callback, IntPtr callbackData)
		{
			throw new NotImplementedException ();
		}

		[MonoTODO (MetafileEnumeration)]
		public void EnumerateMetafile (Metafile metafile, Point destPoint, EnumerateMetafileProc callback, IntPtr callbackData)
		{
			throw new NotImplementedException ();
		}

		[MonoTODO (MetafileEnumeration)]
		public void EnumerateMetafile (Metafile metafile, Point [] destPoints, EnumerateMetafileProc callback, IntPtr callbackData)
		{
			throw new NotImplementedException ();
		}

		[MonoTODO (MetafileEnumeration)]
		public void EnumerateMetafile (Metafile metafile, RectangleF destRect, EnumerateMetafileProc callback, IntPtr callbackData)
		{
			throw new NotImplementedException ();
		}

		[MonoTODO (MetafileEnumeration)]
		public void EnumerateMetafile (Metafile metafile, PointF destPoint, RectangleF srcRect, GraphicsUnit srcUnit, EnumerateMetafileProc callback)
		{
			throw new NotImplementedException ();
		}

		[MonoTODO (MetafileEnumeration)]
		public void EnumerateMetafile (Metafile metafile, Point destPoint, Rectangle srcRect, GraphicsUnit srcUnit, EnumerateMetafileProc callback)
		{
			throw new NotImplementedException ();
		}

		[MonoTODO (MetafileEnumeration)]
		public void EnumerateMetafile (Metafile metafile, PointF [] destPoints, RectangleF srcRect, GraphicsUnit srcUnit, EnumerateMetafileProc callback)
		{
			throw new NotImplementedException ();
		}

		[MonoTODO (MetafileEnumeration)]
		public void EnumerateMetafile (Metafile metafile, Point [] destPoints, Rectangle srcRect, GraphicsUnit srcUnit, EnumerateMetafileProc callback)
		{
			throw new NotImplementedException ();
		}

		[MonoTODO (MetafileEnumeration)]
		public void EnumerateMetafile (Metafile metafile, RectangleF destRect, RectangleF srcRect, GraphicsUnit srcUnit, EnumerateMetafileProc callback)
		{
			throw new NotImplementedException ();
		}

		[MonoTODO (MetafileEnumeration)]
		public void EnumerateMetafile (Metafile metafile, Rectangle destRect, Rectangle srcRect, GraphicsUnit srcUnit, EnumerateMetafileProc callback)
		{
			throw new NotImplementedException ();
		}

		[MonoTODO (MetafileEnumeration)]
		public void EnumerateMetafile (Metafile metafile, RectangleF destRect, EnumerateMetafileProc callback, IntPtr callbackData, ImageAttributes imageAttr)
		{
			throw new NotImplementedException ();
		}

		[MonoTODO (MetafileEnumeration)]
		public void EnumerateMetafile (Metafile metafile, Point destPoint, EnumerateMetafileProc callback, IntPtr callbackData, ImageAttributes imageAttr)
		{
			throw new NotImplementedException ();
		}

		[MonoTODO (MetafileEnumeration)]
		public void EnumerateMetafile (Metafile metafile, PointF destPoint, EnumerateMetafileProc callback, IntPtr callbackData, ImageAttributes imageAttr)
		{
			throw new NotImplementedException ();
		}

		[MonoTODO (MetafileEnumeration)]
		public void EnumerateMetafile (Metafile metafile, Point [] destPoints, EnumerateMetafileProc callback, IntPtr callbackData, ImageAttributes imageAttr)
		{
			throw new NotImplementedException ();
		}

		[MonoTODO (MetafileEnumeration)]
		public void EnumerateMetafile (Metafile metafile, PointF [] destPoints, EnumerateMetafileProc callback, IntPtr callbackData, ImageAttributes imageAttr)
		{
			throw new NotImplementedException ();
		}

		[MonoTODO (MetafileEnumeration)]
		public void EnumerateMetafile (Metafile metafile, Rectangle destRect, EnumerateMetafileProc callback, IntPtr callbackData, ImageAttributes imageAttr)
		{
			throw new NotImplementedException ();
		}

		[MonoTODO (MetafileEnumeration)]
		public void EnumerateMetafile (Metafile metafile, Rectangle destRect, Rectangle srcRect, GraphicsUnit srcUnit, EnumerateMetafileProc callback, IntPtr callbackData)
		{
			throw new NotImplementedException ();
		}

		[MonoTODO (MetafileEnumeration)]
		public void EnumerateMetafile (Metafile metafile, PointF [] destPoints, RectangleF srcRect, GraphicsUnit srcUnit, EnumerateMetafileProc callback, IntPtr callbackData)
		{
			throw new NotImplementedException ();
		}

		[MonoTODO (MetafileEnumeration)]
		public void EnumerateMetafile (Metafile metafile, RectangleF destRect, RectangleF srcRect, GraphicsUnit srcUnit, EnumerateMetafileProc callback, IntPtr callbackData)
		{
			throw new NotImplementedException ();
		}

		[MonoTODO (MetafileEnumeration)]
		public void EnumerateMetafile (Metafile metafile, PointF destPoint, RectangleF srcRect, GraphicsUnit srcUnit, EnumerateMetafileProc callback, IntPtr callbackData)
		{
			throw new NotImplementedException ();
		}

		[MonoTODO (MetafileEnumeration)]
		public void EnumerateMetafile (Metafile metafile, Point destPoint, Rectangle srcRect, GraphicsUnit srcUnit, EnumerateMetafileProc callback, IntPtr callbackData)
		{
			throw new NotImplementedException ();
		}

		[MonoTODO (MetafileEnumeration)]
		public void EnumerateMetafile (Metafile metafile, Point [] destPoints, Rectangle srcRect, GraphicsUnit srcUnit, EnumerateMetafileProc callback, IntPtr callbackData)
		{
			throw new NotImplementedException ();
		}

		[MonoTODO (MetafileEnumeration)]
		public void EnumerateMetafile (Metafile metafile, Point [] destPoints, Rectangle srcRect, GraphicsUnit unit, EnumerateMetafileProc callback, IntPtr callbackData, ImageAttributes imageAttr)
		{
			throw new NotImplementedException ();
		}

		[MonoTODO (MetafileEnumeration)]
		public void EnumerateMetafile (Metafile metafile, Rectangle destRect, Rectangle srcRect, GraphicsUnit unit, EnumerateMetafileProc callback, IntPtr callbackData, ImageAttributes imageAttr)
		{
			throw new NotImplementedException ();
		}

		[MonoTODO (MetafileEnumeration)]
		public void EnumerateMetafile (Metafile metafile, Point destPoint, Rectangle srcRect, GraphicsUnit unit, EnumerateMetafileProc callback, IntPtr callbackData, ImageAttributes imageAttr)
		{
			throw new NotImplementedException ();
		}

		[MonoTODO (MetafileEnumeration)]
		public void EnumerateMetafile (Metafile metafile, RectangleF destRect, RectangleF srcRect, GraphicsUnit unit, EnumerateMetafileProc callback, IntPtr callbackData, ImageAttributes imageAttr)
		{
			throw new NotImplementedException ();
		}

		[MonoTODO (MetafileEnumeration)]
		public void EnumerateMetafile (Metafile metafile, PointF [] destPoints, RectangleF srcRect, GraphicsUnit unit, EnumerateMetafileProc callback, IntPtr callbackData, ImageAttributes imageAttr)
		{
			throw new NotImplementedException ();
		}

		[MonoTODO (MetafileEnumeration)]
		public void EnumerateMetafile (Metafile metafile, PointF destPoint, RectangleF srcRect, GraphicsUnit unit, EnumerateMetafileProc callback, IntPtr callbackData, ImageAttributes imageAttr)
		{
			throw new NotImplementedException ();
		}
	
		public void ExcludeClip (Rectangle rect)
		{
			if (nativeObject == IntPtr.Zero) return;
			Status status = GDIPlus.GdipSetClipRectI (nativeObject, rect.X, rect.Y, rect.Width, rect.Height, CombineMode.Exclude);
			CheckDrawStatus (status);
		}

		public void ExcludeClip (Region region)
		{
			if (region == null)
				throw new ArgumentNullException ("region");
			if (nativeObject == IntPtr.Zero) return;
			Status status = GDIPlus.GdipSetClipRegion (nativeObject, region.NativeObject, CombineMode.Exclude);
			CheckDrawStatus (status);
		}

		
		public void FillClosedCurve (Brush brush, PointF [] points)
		{
			if (brush == null)
				throw new ArgumentNullException ("brush");
			if (points == null)
				throw new ArgumentNullException ("points");
			Status status = GDIPlus.GdipFillClosedCurve (nativeObject, brush.NativeBrush, points, points.Length);
			CheckDrawStatus (status);
		}
		
		public void FillClosedCurve (Brush brush, Point [] points)
		{
			if (brush == null)
				throw new ArgumentNullException ("brush");
			if (points == null)
				throw new ArgumentNullException ("points");
			Status status = GDIPlus.GdipFillClosedCurveI (nativeObject, brush.NativeBrush, points, points.Length);
			CheckDrawStatus (status);
		}

		
		public void FillClosedCurve (Brush brush, PointF [] points, FillMode fillmode)
		{
			if (brush == null)
				throw new ArgumentNullException ("brush");
			if (points == null)
				throw new ArgumentNullException ("points");
			FillClosedCurve (brush, points, fillmode, 0.5f);
		}
		
		public void FillClosedCurve (Brush brush, Point [] points, FillMode fillmode)
		{
			if (brush == null)
				throw new ArgumentNullException ("brush");
			if (points == null)
				throw new ArgumentNullException ("points");
			FillClosedCurve (brush, points, fillmode, 0.5f);
		}

		public void FillClosedCurve (Brush brush, PointF [] points, FillMode fillmode, float tension)
		{
			if (brush == null)
				throw new ArgumentNullException ("brush");
			if (points == null)
				throw new ArgumentNullException ("points");
			Status status = GDIPlus.GdipFillClosedCurve2 (nativeObject, brush.NativeBrush, points, points.Length, tension, fillmode);
			CheckDrawStatus (status);
		}

		public void FillClosedCurve (Brush brush, Point [] points, FillMode fillmode, float tension)
		{
			if (brush == null)
				throw new ArgumentNullException ("brush");
			if (points == null)
				throw new ArgumentNullException ("points");
			Status status = GDIPlus.GdipFillClosedCurve2I (nativeObject, brush.NativeBrush, points, points.Length, tension, fillmode);
			CheckDrawStatus (status);
		}

		public void FillEllipse (Brush brush, Rectangle rect)
		{
			if (brush == null)
				throw new ArgumentNullException ("brush");
			FillEllipse (brush, rect.X, rect.Y, rect.Width, rect.Height);
		}

		public void FillEllipse (Brush brush, RectangleF rect)
		{
			if (brush == null)
				throw new ArgumentNullException ("brush");
			FillEllipse (brush, rect.X, rect.Y, rect.Width, rect.Height);
		}

		public void FillEllipse (Brush brush, float x, float y, float width, float height)
		{
			if (brush == null)
				throw new ArgumentNullException ("brush");
			if (RecordSolid (brush)) { GpuRecorder.FillEllipse (x, y, width, height, ArgbOf (brush)); return; }
			if (TryHatch (brush, out HatchTile eh)) { GpuRecorder.FillHatch (GradientShape.Ellipse, x, y, width, height, null, eh.Rgba, eh.W, eh.H, eh.Size); return; }
			if (TryGradient (brush, out GradientDesc ge)) { GpuRecorder.FillGradient (GradientShape.Ellipse, x, y, width, height, null, ge); return; }
                        Status status = GDIPlus.GdipFillEllipse (nativeObject, brush.NativeBrush, x, y, width, height);
			CheckDrawStatus (status);
		}

		public void FillEllipse (Brush brush, int x, int y, int width, int height)
		{
			if (brush == null)
				throw new ArgumentNullException ("brush");
			if (RecordSolid (brush)) { GpuRecorder.FillEllipse (x, y, width, height, ArgbOf (brush)); return; }
			if (TryHatch (brush, out HatchTile eh)) { GpuRecorder.FillHatch (GradientShape.Ellipse, x, y, width, height, null, eh.Rgba, eh.W, eh.H, eh.Size); return; }
			if (TryGradient (brush, out GradientDesc ge)) { GpuRecorder.FillGradient (GradientShape.Ellipse, x, y, width, height, null, ge); return; }
			Status status = GDIPlus.GdipFillEllipseI (nativeObject, brush.NativeBrush, x, y, width, height);
			CheckDrawStatus (status);
		}

		public void FillPath (Brush brush, GraphicsPath path)
		{
			if (brush == null)
				throw new ArgumentNullException ("brush");
			if (path == null)
				throw new ArgumentNullException ("path");
			if (GpuRecorder != null) {
				int c = ArgbOf (brush);
				foreach (PointF [] sub in FlattenSubpaths (path))
					if (sub.Length >= 3)
						GpuRecorder.FillPolygon (ToXY (sub), c);
				return;
			}
			Status status = GDIPlus.GdipFillPath (nativeObject, brush.NativeBrush,  path.nativePath);
			CheckDrawStatus (status);
		}

		public void FillPie (Brush brush, Rectangle rect, float startAngle, float sweepAngle)
		{
			if (brush == null)
				throw new ArgumentNullException ("brush");
			// Approximated as a full ellipse, as the other two overloads already were. Without
			// this the call reached gdiplus with no surface behind it and drew nothing at all:
			// the month calendar fills the selected day with a pie and then writes the day
			// number over it in the background colour, so today's date came out invisible.
			if (RecordSolid (brush)) { GpuRecorder.FillEllipse (rect.X, rect.Y, rect.Width, rect.Height, ArgbOf (brush)); return; }
			if (TryHatch (brush, out HatchTile ph)) { GpuRecorder.FillHatch (GradientShape.Ellipse, rect.X, rect.Y, rect.Width, rect.Height, null, ph.Rgba, ph.W, ph.H, ph.Size); return; }
			if (TryGradient (brush, out GradientDesc pg)) { GpuRecorder.FillGradient (GradientShape.Ellipse, rect.X, rect.Y, rect.Width, rect.Height, null, pg); return; }
			Status status = GDIPlus.GdipFillPie (nativeObject, brush.NativeBrush, rect.X, rect.Y, rect.Width, rect.Height, startAngle, sweepAngle);
			CheckDrawStatus (status);
		}

		public void FillPie (Brush brush, int x, int y, int width, int height, int startAngle, int sweepAngle)
		{
			if (brush == null)
				throw new ArgumentNullException ("brush");
			// Approximate as a full ellipse fill (covers the common full-circle case, e.g. a radio dot).
			if (RecordSolid (brush)) { GpuRecorder.FillEllipse (x, y, width, height, ArgbOf (brush)); return; }
			if (TryHatch (brush, out HatchTile eh)) { GpuRecorder.FillHatch (GradientShape.Ellipse, x, y, width, height, null, eh.Rgba, eh.W, eh.H, eh.Size); return; }
			if (TryGradient (brush, out GradientDesc ge)) { GpuRecorder.FillGradient (GradientShape.Ellipse, x, y, width, height, null, ge); return; }
			Status status = GDIPlus.GdipFillPieI (nativeObject, brush.NativeBrush, x, y, width, height, startAngle, sweepAngle);
			CheckDrawStatus (status);
		}

		public void FillPie (Brush brush, float x, float y, float width, float height, float startAngle, float sweepAngle)
		{
			if (brush == null)
				throw new ArgumentNullException ("brush");
			if (RecordSolid (brush)) { GpuRecorder.FillEllipse (x, y, width, height, ArgbOf (brush)); return; }
			if (TryHatch (brush, out HatchTile eh)) { GpuRecorder.FillHatch (GradientShape.Ellipse, x, y, width, height, null, eh.Rgba, eh.W, eh.H, eh.Size); return; }
			if (TryGradient (brush, out GradientDesc ge)) { GpuRecorder.FillGradient (GradientShape.Ellipse, x, y, width, height, null, ge); return; }
			Status status = GDIPlus.GdipFillPie (nativeObject, brush.NativeBrush, x, y, width, height, startAngle, sweepAngle);
			CheckDrawStatus (status);
		}

		public void FillPolygon (Brush brush, PointF [] points)
		{
			if (brush == null)
				throw new ArgumentNullException ("brush");
			if (points == null)
				throw new ArgumentNullException ("points");
			if (RecordSolid (brush)) { GpuRecorder.FillPolygon (Flatten (points), ArgbOf (brush)); return; }
			if (TryHatch (brush, out HatchTile ph)) { GpuRecorder.FillHatch (GradientShape.Polygon, 0, 0, 0, 0, Flatten (points), ph.Rgba, ph.W, ph.H, ph.Size); return; }
			if (TryGradient (brush, out GradientDesc gp)) { GpuRecorder.FillGradient (GradientShape.Polygon, 0, 0, 0, 0, Flatten (points), gp); return; }
			Status status = GDIPlus.GdipFillPolygon2 (nativeObject, brush.NativeBrush, points, points.Length);
			CheckDrawStatus (status);
		}

		public void FillPolygon (Brush brush, Point [] points)
		{
			if (brush == null)
				throw new ArgumentNullException ("brush");
			if (points == null)
				throw new ArgumentNullException ("points");
			if (RecordSolid (brush)) { GpuRecorder.FillPolygon (Flatten (points), ArgbOf (brush)); return; }
			if (TryHatch (brush, out HatchTile ph)) { GpuRecorder.FillHatch (GradientShape.Polygon, 0, 0, 0, 0, Flatten (points), ph.Rgba, ph.W, ph.H, ph.Size); return; }
			if (TryGradient (brush, out GradientDesc gp)) { GpuRecorder.FillGradient (GradientShape.Polygon, 0, 0, 0, 0, Flatten (points), gp); return; }
			Status status = GDIPlus.GdipFillPolygon2I (nativeObject, brush.NativeBrush, points, points.Length);
			CheckDrawStatus (status);
		}

		public void FillPolygon (Brush brush, Point [] points, FillMode fillMode)
		{
			if (brush == null)
				throw new ArgumentNullException ("brush");
			if (points == null)
				throw new ArgumentNullException ("points");
			if (RecordSolid (brush)) { GpuRecorder.FillPolygon (Flatten (points), ArgbOf (brush)); return; }
			if (TryHatch (brush, out HatchTile ph)) { GpuRecorder.FillHatch (GradientShape.Polygon, 0, 0, 0, 0, Flatten (points), ph.Rgba, ph.W, ph.H, ph.Size); return; }
			if (TryGradient (brush, out GradientDesc gp)) { GpuRecorder.FillGradient (GradientShape.Polygon, 0, 0, 0, 0, Flatten (points), gp); return; }
			Status status = GDIPlus.GdipFillPolygonI (nativeObject, brush.NativeBrush, points, points.Length, fillMode);
			CheckDrawStatus (status);
		}

		public void FillPolygon (Brush brush, PointF [] points, FillMode fillMode)
		{
			if (brush == null)
				throw new ArgumentNullException ("brush");
			if (points == null)
				throw new ArgumentNullException ("points");
			if (RecordSolid (brush)) { GpuRecorder.FillPolygon (Flatten (points), ArgbOf (brush)); return; }
			if (TryHatch (brush, out HatchTile ph)) { GpuRecorder.FillHatch (GradientShape.Polygon, 0, 0, 0, 0, Flatten (points), ph.Rgba, ph.W, ph.H, ph.Size); return; }
			if (TryGradient (brush, out GradientDesc gp)) { GpuRecorder.FillGradient (GradientShape.Polygon, 0, 0, 0, 0, Flatten (points), gp); return; }
			Status status = GDIPlus.GdipFillPolygon (nativeObject, brush.NativeBrush, points, points.Length, fillMode);
			CheckDrawStatus (status);
		}

		public void FillRectangle (Brush brush, RectangleF rect)
		{
			if (brush == null)
				throw new ArgumentNullException ("brush");
                        FillRectangle (brush, rect.Left, rect.Top, rect.Width, rect.Height);
		}

		public void FillRectangle (Brush brush, Rectangle rect)
		{
			if (brush == null)
				throw new ArgumentNullException ("brush");
			if (rect == null)
				throw new ArgumentNullException ("rect");
				
                        FillRectangle (brush, rect.Left, rect.Top, rect.Width, rect.Height);
		}

		public void FillRectangle (Brush brush, int x, int y, int width, int height)
		{
			if (brush == null)
				throw new ArgumentNullException ("brush");
			if (RecordSolid (brush)) { GpuRecorder.FillRect (x, y, width, height, ArgbOf (brush)); return; }
			if (TryHatch (brush, out HatchTile rh)) { GpuRecorder.FillHatch (GradientShape.Rect, x, y, width, height, null, rh.Rgba, rh.W, rh.H, rh.Size); return; }
			if (TryGradient (brush, out GradientDesc gd)) { GpuRecorder.FillGradient (GradientShape.Rect, x, y, width, height, null, gd); return; }

			Status status = GDIPlus.GdipFillRectangleI (nativeObject, brush.NativeBrush, x, y, width, height);
			CheckDrawStatus (status);
		}

		public void FillRectangle (Brush brush, float x, float y, float width, float height)
		{
			if (brush == null)
				throw new ArgumentNullException ("brush");
			if (RecordSolid (brush)) { GpuRecorder.FillRect (x, y, width, height, ArgbOf (brush)); return; }
			if (TryHatch (brush, out HatchTile rh)) { GpuRecorder.FillHatch (GradientShape.Rect, x, y, width, height, null, rh.Rgba, rh.W, rh.H, rh.Size); return; }
			if (TryGradient (brush, out GradientDesc gd)) { GpuRecorder.FillGradient (GradientShape.Rect, x, y, width, height, null, gd); return; }

			Status status = GDIPlus.GdipFillRectangle (nativeObject, brush.NativeBrush, x, y, width, height);
			CheckDrawStatus (status);
		}

		public void FillRectangles (Brush brush, Rectangle [] rects)
		{
			if (brush == null)
				throw new ArgumentNullException ("brush");
			if (rects == null)
				throw new ArgumentNullException ("rects");

			Status status = GDIPlus.GdipFillRectanglesI (nativeObject, brush.NativeBrush, rects, rects.Length);
			CheckDrawStatus (status);
		}

		public void FillRectangles (Brush brush, RectangleF [] rects)
		{
			if (brush == null)
				throw new ArgumentNullException ("brush");
			if (rects == null)
				throw new ArgumentNullException ("rects");

			Status status = GDIPlus.GdipFillRectangles (nativeObject, brush.NativeBrush, rects, rects.Length);
			CheckDrawStatus (status);
		}

		
		public void FillRegion (Brush brush, Region region)
		{
			if (brush == null)
				throw new ArgumentNullException ("brush");
			if (region == null)
				throw new ArgumentNullException ("region");
			
			Status status = GDIPlus.GdipFillRegion (nativeObject, brush.NativeBrush, region.NativeObject);                  
                        GDIPlus.CheckStatus(status);
		}

		
		public void Flush ()
		{
			Flush (FlushIntention.Flush);
		}

		
		public void Flush (FlushIntention intention)
		{
			if (nativeObject == IntPtr.Zero) {
				return;
			}

			Status status = GDIPlus.GdipFlush (nativeObject, intention);
                        CheckDrawStatus (status);                    

			if (maccontext != null)
				maccontext.Synchronize ();
		}

		[EditorBrowsable (EditorBrowsableState.Advanced)]		
		public static Graphics FromHdc (IntPtr hdc)
		{
			IntPtr graphics;
			Status status = GDIPlus.GdipCreateFromHDC (hdc, out graphics);
			GDIPlus.CheckStatus (status);
			return new Graphics (graphics);
		}

		[MonoTODO]
		[EditorBrowsable (EditorBrowsableState.Advanced)]
		public static Graphics FromHdc (IntPtr hdc, IntPtr hdevice)
		{
			throw new NotImplementedException ();
		}

		[EditorBrowsable (EditorBrowsableState.Advanced)]
		public static Graphics FromHdcInternal (IntPtr hdc)
		{
			GDIPlus.Display = hdc;
			return null;
		}

		[EditorBrowsable (EditorBrowsableState.Advanced)]		
		public static Graphics FromHwnd (IntPtr hwnd)
		{
			IntPtr graphics;

			if (GDIPlus.UseCocoaDrawable) {
				if (hwnd == IntPtr.Zero) {
					throw new NotSupportedException ("Opening display graphics is not supported");
				}

				CocoaContext context = MacSupport.GetCGContextForNSView (hwnd);
				GDIPlus.GdipCreateFromContext_macosx (context.ctx, context.width, context.height, out graphics);

				Graphics g = new Graphics (graphics);
				g.maccontext = context;

				return g;
			}

			if (GDIPlus.UseCarbonDrawable) {
				CarbonContext context = MacSupport.GetCGContextForView (hwnd);
				GDIPlus.GdipCreateFromContext_macosx (context.ctx, context.width, context.height, out graphics);
				
				Graphics g = new Graphics (graphics);
				g.maccontext = context;
				
				return g;
			}
			if (GDIPlus.UseX11Drawable) {
				if (GDIPlus.Display == IntPtr.Zero) {
					GDIPlus.Display = GDIPlus.XOpenDisplay (IntPtr.Zero);
					if (GDIPlus.Display == IntPtr.Zero)
						throw new NotSupportedException ("Could not open display (X-Server required. Check your DISPLAY environment variable)");
				}
				if (hwnd == IntPtr.Zero) {
					hwnd = GDIPlus.XRootWindow (GDIPlus.Display, GDIPlus.XDefaultScreen (GDIPlus.Display));
				}

				return FromXDrawable (hwnd, GDIPlus.Display);

			}

			Status status = GDIPlus.GdipCreateFromHWND (hwnd, out graphics);
			GDIPlus.CheckStatus (status);

			return new Graphics (graphics);
		}
		
		[EditorBrowsable (EditorBrowsableState.Advanced)]
		public static Graphics FromHwndInternal (IntPtr hwnd)
		{
			return FromHwnd (hwnd);
		}

		public static Graphics FromImage (Image image)
		{
			IntPtr graphics;

			if (image == null) 
				throw new ArgumentNullException ("image");

			// No libgdiplus (browser): the image has no native handle -> return a null-native Graphics.
			// The GPU-raster path records verbs / measures text managed, so it never touches the (absent)
			// native drawing surface. This backs Hwnd.GraphicsContext (the shared measurement Graphics).
			if (image.nativeObject == IntPtr.Zero)
				return new Graphics (IntPtr.Zero, image);

			if ((image.PixelFormat & PixelFormat.Indexed) != 0)
				throw new Exception (Locale.GetText ("Cannot create Graphics from an indexed bitmap."));

			Status status = GDIPlus.GdipGetImageGraphicsContext (image.nativeObject, out graphics);
			GDIPlus.CheckStatus (status);
			Graphics result = new Graphics (graphics, image);
			if (GDIPlus.RunningOnUnix ()) {
				Rectangle rect  = new Rectangle (0,0, image.Width, image.Height);
				GDIPlus.GdipSetVisibleClip_linux (result.NativeObject, ref rect);
			}
				
			return result;
		}

		internal static Graphics FromXDrawable (IntPtr drawable, IntPtr display)
		{
			IntPtr graphics;

			Status s = GDIPlus.GdipCreateFromXDrawable_linux (drawable, display, out graphics);
			GDIPlus.CheckStatus (s);
			return new Graphics (graphics);
		}

		[MonoTODO]
		public static IntPtr GetHalftonePalette ()
		{
			throw new NotImplementedException ();
		}

		public IntPtr GetHdc ()
		{
			GDIPlus.CheckStatus (GDIPlus.GdipGetDC (this.nativeObject, out deviceContextHdc));
			return deviceContextHdc;
		}
		
		public Color GetNearestColor (Color color)
		{
			int argb;
			
			Status status = GDIPlus.GdipGetNearestColor (nativeObject, out argb);
			CheckDrawStatus (status);

			return Color.FromArgb (argb);
		}

		
		public void IntersectClip (Region region)
		{
			if (region == null)
				throw new ArgumentNullException ("region");
			if (nativeObject == IntPtr.Zero) return;
			Status status = GDIPlus.GdipSetClipRegion (nativeObject, region.NativeObject, CombineMode.Intersect);
			CheckDrawStatus (status);
		}
		
		public void IntersectClip (RectangleF rect)
		{
			if (GpuRecorder != null) { GpuRecorder.SetClipRect (rect.X, rect.Y, rect.Width, rect.Height, false); return; }
			if (nativeObject == IntPtr.Zero) return;
			Status status = GDIPlus.GdipSetClipRect (nativeObject, rect.X, rect.Y, rect.Width, rect.Height, CombineMode.Intersect);
			CheckDrawStatus (status);
		}

		public void IntersectClip (Rectangle rect)
		{			
			if (nativeObject == IntPtr.Zero) return;
			Status status = GDIPlus.GdipSetClipRectI (nativeObject, rect.X, rect.Y, rect.Width, rect.Height, CombineMode.Intersect);
			CheckDrawStatus (status);
		}

		public bool IsVisible (Point point)
		{
			bool isVisible = false;

			Status status = GDIPlus.GdipIsVisiblePointI (nativeObject, point.X, point.Y, out isVisible);
			CheckDrawStatus (status);

                        return isVisible;
		}

		
		public bool IsVisible (RectangleF rect)
		{
			bool isVisible = false;

			Status status = GDIPlus.GdipIsVisibleRect (nativeObject, rect.X, rect.Y, rect.Width, rect.Height, out isVisible);
			CheckDrawStatus (status);

                        return isVisible;
		}

		public bool IsVisible (PointF point)
		{
			bool isVisible = false;

			Status status = GDIPlus.GdipIsVisiblePoint (nativeObject, point.X, point.Y, out isVisible);
			CheckDrawStatus (status);

                        return isVisible;
		}
		
		public bool IsVisible (Rectangle rect)
		{
			bool isVisible = false;

			Status status = GDIPlus.GdipIsVisibleRectI (nativeObject, rect.X, rect.Y, rect.Width, rect.Height, out isVisible);
			CheckDrawStatus (status);

                        return isVisible;
		}
		
		public bool IsVisible (float x, float y)
		{
			return IsVisible (new PointF (x, y));
		}
		
		public bool IsVisible (int x, int y)
		{
			return IsVisible (new Point (x, y));
		}
		
		public bool IsVisible (float x, float y, float width, float height)
		{
			return IsVisible (new RectangleF (x, y, width, height));
		}

		
		public bool IsVisible (int x, int y, int width, int height)
		{
			return IsVisible (new Rectangle (x, y, width, height));
		}

		
		public Region[] MeasureCharacterRanges (string text, Font font, RectangleF layoutRect, StringFormat stringFormat)
		{
			if ((text == null) || (text.Length == 0))
				return new Region [0];

			if (font == null)
				throw new ArgumentNullException ("font");

			if (stringFormat == null)
				throw new ArgumentException ("stringFormat");

			int regcount = stringFormat.GetMeasurableCharacterRangeCount ();
			if (regcount == 0)
				return new Region[0];

			if (s_gpuRasterMode)
				return MeasureCharacterRangesManaged (text, font, layoutRect, stringFormat, regcount);

			IntPtr[] native_regions = new IntPtr [regcount];
			Region[] regions = new Region [regcount];
			
			for (int i = 0; i < regcount; i++) {
				regions[i] = new Region ();
				native_regions[i] = regions[i].NativeObject;
			}
			
			Status status = GDIPlus.GdipMeasureCharacterRanges (nativeObject, text, text.Length,
				font.NativeObject, ref layoutRect, stringFormat.NativeObject, regcount, out native_regions[0]); 
			CheckDrawStatus (status);				

			return regions;							
		}

		// Managed character-range measurement for GPU-raster mode. The Font carries no native GDI+
		// handle there (see Font.s_gpuRasterMode), so GdipMeasureCharacterRanges would fail with
		// InvalidParameter. Lay the text out on a single line - the in-tree callers (TabControl tab
		// sizing, LinkLabel link hit-testing) all measure with NoWrap - and turn each requested
		// range into its bounding rectangle, using the same metrics the WGSL renderer draws with.
		private Region[] MeasureCharacterRangesManaged (string text, Font font, RectangleF layoutRect,
			StringFormat stringFormat, int regcount)
		{
			CharacterRange[] ranges = stringFormat.MeasurableCharacterRanges;
			Region[] regions = new Region [regcount];
			float em = font.SizeInPoints * 96f / 72f;

			for (int i = 0; i < regcount; i++) {
				CharacterRange range = (ranges != null && i < ranges.Length)
					? ranges [i] : new CharacterRange (0, text.Length);

				int first = Math.Max (0, Math.Min (range.First, text.Length));
				int length = Math.Max (0, Math.Min (range.Length, text.Length - first));

				float x = 0f, unused;
				if (first > 0)
					WebGpuBackend.GpuRaster.MeasureText (text.Substring (0, first), em, out x, out unused);

				float w = 0f, h;
				WebGpuBackend.GpuRaster.MeasureText (length > 0 ? text.Substring (first, length) : "I", em, out w, out h);
				h = font.GetHeight () > 0 ? font.GetHeight () : h;
				if (length == 0)
					w = 0f;

				regions [i] = new Region (new RectangleF (layoutRect.X + x, layoutRect.Y, w, h));
			}

			return regions;
		}

		private unsafe SizeF GdipMeasureString (IntPtr graphics, string text, Font font, ref RectangleF layoutRect,
			IntPtr stringFormat, StringFormat managedFormat = null)
		{
			if ((text == null) || (text.Length == 0))
				return SizeF.Empty;

			// "&File" is a File with a line under the F: the ampersand names the key that reaches
			// it and is no part of the caption. Windows does not measure it, and neither does the
			// drawing below -- but this did, so every menu came out about a character wider than
			// the same menu in Windows.
			if (s_gpuRasterMode && managedFormat != null
				&& managedFormat.HotkeyPrefix != Text.HotkeyPrefix.None) {
				int ignored;
				text = StripHotkeyPrefix (text, out ignored);
			}

			if (font == null)
				throw new ArgumentNullException ("font");

			if (s_gpuRasterMode) {
				// Managed measurement (no libgdiplus), consistent with the WGSL-rendered font.
				// A layout width means "wrap here", and the caller wants the height that wrapping
				// produces -- SharpDevelop's About box measures its credits exactly this way, to know
				// how far it has to scroll them.
				float em = font.SizeInPoints * 96f / 72f;
				int simulations = (font.Bold ? 1 : 0) | (font.Italic ? 2 : 0);
				// Measured in the family it will be DRAWN in: a run measured with one face and drawn
				// with another does not fit where the caller was told it would.
				string measureFamily = font.FontFamily?.Name;
				// The height a caller gets back has to be the height the text will occupy, which is
				// the font's line height and not its em size. ListBox asks exactly this question to
				// size its rows, so answering 12 where Windows answers 16 made every list, and every
				// other control that measures a line, a quarter tighter than the real thing.
				float lineHeight = font.GetHeight ();
				if (lineHeight <= 0) lineHeight = em;
				// GDI+ reports a line box in whole pixels, and callers truncate what they get back:
				// ListBox does (int) sz.Height to size its rows, so handing it 15.96 produced 15 where
				// Windows produces 16, and every row was a pixel short.
				lineHeight = (float) Math.Ceiling (lineHeight);
				// The margin the text will be drawn inside -- see Overhang. A caller that sizes a
				// control to what it is told here and then draws the text in that space needs the
				// margin counted in, or the last letter or two run out of the room measured for them.
				float margin = Margins (font, managedFormat);
				if (layoutRect.Width > 0) {
						string[] wrapped = WrapLines (text, em, simulations, measureFamily, layoutRect.Width, null);
						float widest = 0f;
						foreach (string line in wrapped) {
							WebGpuBackend.GpuRaster.MeasureText (line, em, simulations, measureFamily, out float lw, out float _);
							if (lw > widest) widest = lw;
						}
						return new SizeF (widest + margin, lineHeight * Math.Max (1, wrapped.Length));
				}
				WebGpuBackend.GpuRaster.MeasureText (text, em, simulations, measureFamily, out float mw, out float mh);
				return new SizeF (mw + margin, lineHeight * Math.Max (1, mh / Math.Max (1f, em)));
			}

			RectangleF boundingBox = new RectangleF ();

			Status status = GDIPlus.GdipMeasureString (nativeObject, text, text.Length, font.NativeObject,
				ref layoutRect, stringFormat, out boundingBox, null, null);
			CheckDrawStatus (status);

			return new SizeF (boundingBox.Width, boundingBox.Height);
		}

		public SizeF MeasureString (string text, Font font)
		{
			return MeasureString (text, font, SizeF.Empty);
		}

		public SizeF MeasureString (string text, Font font, SizeF layoutArea)
		{
			RectangleF rect = new RectangleF (0, 0, layoutArea.Width, layoutArea.Height);
			return GdipMeasureString (nativeObject, text, font, ref rect, IntPtr.Zero);
		}

		public SizeF MeasureString (string text, Font font, int width)
		{				
			RectangleF rect = new RectangleF (0, 0, width, Int32.MaxValue);
			return GdipMeasureString (nativeObject, text, font, ref rect, IntPtr.Zero);
		}

		public SizeF MeasureString (string text, Font font, SizeF layoutArea, StringFormat stringFormat)
		{
			RectangleF rect = new RectangleF (0, 0, layoutArea.Width, layoutArea.Height);
			IntPtr format = (stringFormat == null) ? IntPtr.Zero : stringFormat.NativeObject;
			return GdipMeasureString (nativeObject, text, font, ref rect, format, stringFormat);
		}

		public SizeF MeasureString (string text, Font font, int width, StringFormat format)
		{
			RectangleF rect = new RectangleF (0, 0, width, Int32.MaxValue);
			IntPtr stringFormat = (format == null) ? IntPtr.Zero : format.NativeObject;
			return GdipMeasureString (nativeObject, text, font, ref rect, stringFormat, format);
		}

		public SizeF MeasureString (string text, Font font, PointF origin, StringFormat stringFormat)
		{
			RectangleF rect = new RectangleF (origin.X, origin.Y, 0, 0);
			IntPtr format = (stringFormat == null) ? IntPtr.Zero : stringFormat.NativeObject;
			return GdipMeasureString (nativeObject, text, font, ref rect, format, stringFormat);
		}

		public SizeF MeasureString (string text, Font font, SizeF layoutArea, StringFormat stringFormat, 
			out int charactersFitted, out int linesFilled)
		{	
			charactersFitted = 0;
			linesFilled = 0;

			if ((text == null) || (text.Length == 0))
				return SizeF.Empty;

			if (font == null)
				throw new ArgumentNullException ("font");

			RectangleF boundingBox = new RectangleF ();
			RectangleF rect = new RectangleF (0, 0, layoutArea.Width, layoutArea.Height);

			IntPtr format = (stringFormat == null) ? IntPtr.Zero : stringFormat.NativeObject;

			unsafe {
				fixed (int* pc = &charactersFitted, pl = &linesFilled) {
					Status status = GDIPlus.GdipMeasureString (nativeObject, text, text.Length, 
					font.NativeObject, ref rect, format, out boundingBox, pc, pl);
					CheckDrawStatus (status);
				}
			}
			return new SizeF (boundingBox.Width, boundingBox.Height);
		}

		public void MultiplyTransform (Matrix matrix)
		{
			MultiplyTransform (matrix, MatrixOrder.Prepend);
		}

		public void MultiplyTransform (Matrix matrix, MatrixOrder order)
		{
			if (matrix == null)
				throw new ArgumentNullException ("matrix");

			if (nativeObject == IntPtr.Zero) return;
			Status status = GDIPlus.GdipMultiplyWorldTransform (nativeObject, matrix.nativeMatrix, order);
			CheckDrawStatus (status);
		}

		[EditorBrowsable (EditorBrowsableState.Advanced)]
		public void ReleaseHdc (IntPtr hdc)
		{
			ReleaseHdcInternal (hdc);
		}

		public void ReleaseHdc ()
		{
			ReleaseHdcInternal (deviceContextHdc);
		}

		[MonoLimitation ("Can only be used when hdc was provided by Graphics.GetHdc() method")]
		[EditorBrowsable (EditorBrowsableState.Never)]
		public void ReleaseHdcInternal (IntPtr hdc)
		{
			Status status = Status.InvalidParameter;
			if (hdc == deviceContextHdc) {
				status = GDIPlus.GdipReleaseDC (nativeObject, deviceContextHdc);
				deviceContextHdc = IntPtr.Zero;
			}
			CheckDrawStatus (status);
		}
		
		public void ResetClip ()
		{
			if (GpuRecorder != null) { GpuRecorder.ClearClip (); return; }
			if (nativeObject == IntPtr.Zero) return;
			Status status = GDIPlus.GdipResetClip (nativeObject);
			CheckDrawStatus (status);
		}

		public void ResetTransform ()
		{
			GpuRecorder?.ResetTransform ();
			if (nativeObject == IntPtr.Zero) return;
			Status status = GDIPlus.GdipResetWorldTransform (nativeObject);
			CheckDrawStatus (status);
		}

		public void Restore (GraphicsState gstate)
		{			
			// the possible NRE thrown by gstate.nativeState match MS behaviour
			Status status = GDIPlus.GdipRestoreGraphics (nativeObject, (uint)gstate.nativeState);
			CheckDrawStatus (status);
		}

		public void RotateTransform (float angle)
		{
			RotateTransform (angle, MatrixOrder.Prepend);
		}

		public void RotateTransform (float angle, MatrixOrder order)
		{
			if (nativeObject == IntPtr.Zero) return;
			Status status = GDIPlus.GdipRotateWorldTransform (nativeObject, angle, order);
			CheckDrawStatus (status);
		}

		public GraphicsState Save ()
		{						
			uint saveState;
			Status status = GDIPlus.GdipSaveGraphics (nativeObject, out saveState);
			CheckDrawStatus (status);

			GraphicsState state = new GraphicsState ((int)saveState);
			return state;
		}

		public void ScaleTransform (float sx, float sy)
		{
			ScaleTransform (sx, sy, MatrixOrder.Prepend);
		}

		public void ScaleTransform (float sx, float sy, MatrixOrder order)
		{
                        if (nativeObject == IntPtr.Zero) return;
                        Status status = GDIPlus.GdipScaleWorldTransform (nativeObject, sx, sy, order);
			CheckDrawStatus (status);
		}

		
		public void SetClip (RectangleF rect)
		{
                        SetClip (rect, CombineMode.Replace);
		}

		
		public void SetClip (GraphicsPath path)
		{
			SetClip (path, CombineMode.Replace);
		}

		
		public void SetClip (Rectangle rect)
		{
			SetClip (rect, CombineMode.Replace);
		}

		
		public void SetClip (Graphics g)
		{
			SetClip (g, CombineMode.Replace);
		}

		
		public void SetClip (Graphics g, CombineMode combineMode)
		{
			if (g == null)
				throw new ArgumentNullException ("g");
			
			if (GpuRecorder != null) { GpuRecorder.ClearClip (); return; }
			if (nativeObject == IntPtr.Zero) return;
			Status status = GDIPlus.GdipSetClipGraphics (nativeObject, g.NativeObject, combineMode);
			CheckDrawStatus (status);
		}

		
		public void SetClip (Rectangle rect, CombineMode combineMode)
		{
			if (GpuRecorder != null) { GpuRecorder.SetClipRect (rect.X, rect.Y, rect.Width, rect.Height, combineMode == CombineMode.Exclude); return; }
			if (nativeObject == IntPtr.Zero) return;
			Status status = GDIPlus.GdipSetClipRectI (nativeObject, rect.X, rect.Y, rect.Width, rect.Height, combineMode);
			CheckDrawStatus (status);
		}

		
		public void SetClip (RectangleF rect, CombineMode combineMode)
		{
			if (GpuRecorder != null) { GpuRecorder.SetClipRect (rect.X, rect.Y, rect.Width, rect.Height, combineMode == CombineMode.Exclude); return; }
			if (nativeObject == IntPtr.Zero) return;
			Status status = GDIPlus.GdipSetClipRect (nativeObject, rect.X, rect.Y, rect.Width, rect.Height, combineMode);
			CheckDrawStatus (status);
		}

		
		/// <summary>The bounding box of a region, without needing a Graphics to ask against.
		/// Region.GetBounds wants one, and the whole point here is that there is not a usable
		/// one -- a recording Graphics has no GDI+ surface at all.</summary>
		static RectangleF RegionBounds (Region region)
		{
			RectangleF[] scans = region.GetRegionScans (new Drawing2D.Matrix ());
			if (scans.Length == 0)
				return RectangleF.Empty;
			RectangleF bounds = scans[0];
			for (int i = 1; i < scans.Length; i++)
				bounds = RectangleF.Union (bounds, scans[i]);
			return bounds;
		}

		public void SetClip (Region region, CombineMode combineMode)
		{
			if (region == null)
				throw new ArgumentNullException ("region");
			if (GpuRecorder != null) {
				// Replacing the clip means "from here on, draw inside this instead". It does NOT
				// mean "go back to drawing where whatever came before went" -- but that is what
				// clearing the recorder's clip and stopping there did. The recorder nests a
				// container per clip and renders a container's own content before its children, so
				// popping out of the container a control had been painting into recorded everything
				// after it BEFORE that container rather than after it. LinkLabel assigns
				// Graphics.Clip immediately before drawing its text, so the text was recorded
				// underneath the control's own background and never appeared at all -- emitted at
				// the right place, in the right colour, and painted over.
				GpuRecorder.ClearClip ();
				RectangleF bounds = RegionBounds (region);
				if (bounds.Width > 0 && bounds.Height > 0)
					GpuRecorder.SetClipRect (bounds.X, bounds.Y, bounds.Width, bounds.Height, false);
				return;
			}
			if (nativeObject == IntPtr.Zero) return;
			Status status =   GDIPlus.GdipSetClipRegion(nativeObject,  region.NativeObject, combineMode); 
			CheckDrawStatus (status);
		}

		
		public void SetClip (GraphicsPath path, CombineMode combineMode)
		{
			if (path == null)
				throw new ArgumentNullException ("path");
			Status status = GDIPlus.GdipSetClipPath (nativeObject, path.nativePath, combineMode);
			CheckDrawStatus (status);
		}

		
		public void TransformPoints (CoordinateSpace destSpace, CoordinateSpace srcSpace, PointF [] pts)
		{
			if (pts == null)
				throw new ArgumentNullException ("pts");

			IntPtr ptrPt =  GDIPlus.FromPointToUnManagedMemory (pts);
            
                        Status status = GDIPlus.GdipTransformPoints (nativeObject, destSpace, srcSpace,  ptrPt, pts.Length);
			CheckDrawStatus (status);
			
			GDIPlus.FromUnManagedMemoryToPoint (ptrPt, pts);
		}


		public void TransformPoints (CoordinateSpace destSpace, CoordinateSpace srcSpace, Point [] pts)
		{						
			if (pts == null)
				throw new ArgumentNullException ("pts");
                        IntPtr ptrPt =  GDIPlus.FromPointToUnManagedMemoryI (pts);
            
                        Status status = GDIPlus.GdipTransformPointsI (nativeObject, destSpace, srcSpace, ptrPt, pts.Length);
			CheckDrawStatus (status);
			
			GDIPlus.FromUnManagedMemoryToPointI (ptrPt, pts);
		}

		
		public void TranslateClip (int dx, int dy)
		{
			Status status = GDIPlus.GdipTranslateClipI (nativeObject, dx, dy);
			CheckDrawStatus (status);
		}

		
		public void TranslateClip (float dx, float dy)
		{
			Status status = GDIPlus.GdipTranslateClip (nativeObject, dx, dy);
			CheckDrawStatus (status);
		}

		public void TranslateTransform (float dx, float dy)
		{
			TranslateTransform (dx, dy, MatrixOrder.Prepend);
		}

		
		public void TranslateTransform (float dx, float dy, MatrixOrder order)
		{			
			// WinForms draws a composite control by translating to each part's bounds, drawing it at
			// the origin and resetting -- ToolStrip does exactly this per item. The recorder ignored
			// the transform, so every item was drawn at the same place and their labels sat on top of
			// one another.
			GpuRecorder?.PushTranslate (dx, dy);
			if (nativeObject == IntPtr.Zero) return;
			Status status = GDIPlus.GdipTranslateWorldTransform (nativeObject, dx, dy, order);
			CheckDrawStatus (status);
		}

		public Region Clip {
			get {
				Region reg = new Region();
				if (nativeObject == IntPtr.Zero) return reg;   // recording-only: infinite clip
				Status status = GDIPlus.GdipGetClip (nativeObject, reg.NativeObject);
				CheckDrawStatus (status);
				return reg;
			}
			set {
				SetClip (value, CombineMode.Replace);
			}
		}

		public RectangleF ClipBounds {
			get {
                                if (nativeObject == IntPtr.Zero) return new RectangleF (0, 0, 1 << 20, 1 << 20);
                                RectangleF rect = new RectangleF ();
                                Status status = GDIPlus.GdipGetClipBounds (nativeObject, out rect);
				CheckDrawStatus (status);
				return rect;
			}
		}

		// Mirrored in managed state so it survives on a RECORDING Graphics, which has no
		// libgdiplus handle (GpuRaster.NewRecording -> nativeObject == 0). Previously the
		// setter returned early there, so CompositingMode did not even round-trip, and
		// SourceCopy silently alpha-blended.
		private CompositingMode _compositingMode = CompositingMode.SourceOver;

		public CompositingMode CompositingMode {
			get {
                                if (nativeObject == IntPtr.Zero || GpuRecorder != null)
                                        return _compositingMode;
                                CompositingMode mode;
                                Status status = GDIPlus.GdipGetCompositingMode (nativeObject, out mode);
				CheckDrawStatus (status);

				return mode;
			}
			set {
                                _compositingMode = value;
                                GpuRecorder?.SetCompositingMode (value == CompositingMode.SourceCopy);
                                if (nativeObject == IntPtr.Zero) return;
                                Status status = GDIPlus.GdipSetCompositingMode (nativeObject, value);
				CheckDrawStatus (status);
			}

		}

		public CompositingQuality CompositingQuality {
			get {
                                CompositingQuality quality;

                                Status status = GDIPlus.GdipGetCompositingQuality (nativeObject, out quality);
				CheckDrawStatus (status);
        			return quality;
			}
			set {
                                if (nativeObject == IntPtr.Zero) return;
                                Status status = GDIPlus.GdipSetCompositingQuality (nativeObject, value);
				CheckDrawStatus (status);
			}
		}

		public float DpiX {
			get {
                                if (nativeObject == IntPtr.Zero) return 96f;   // recording-only
                                float x;

       				Status status = GDIPlus.GdipGetDpiX (nativeObject, out x);
				CheckDrawStatus (status);
        			return x;
			}
		}

		public float DpiY {
			get {
                                if (nativeObject == IntPtr.Zero) return 96f;   // recording-only
                                float y;

       				Status status = GDIPlus.GdipGetDpiY (nativeObject, out y);
				CheckDrawStatus (status);
        			return y;
			}
		}

		public InterpolationMode InterpolationMode {
			get {				
                                InterpolationMode imode = InterpolationMode.Invalid;
        			Status status = GDIPlus.GdipGetInterpolationMode (nativeObject, out imode);
				CheckDrawStatus (status);
        			return imode;
			}
			set {
                                if (nativeObject == IntPtr.Zero) return;
                                Status status = GDIPlus.GdipSetInterpolationMode (nativeObject, value);
				CheckDrawStatus (status);
			}
		}

		public bool IsClipEmpty {
			get {
                                bool isEmpty = false;

        			Status status = GDIPlus.GdipIsClipEmpty (nativeObject, out isEmpty);
				CheckDrawStatus (status);
        			return isEmpty;
			}
		}

		public bool IsVisibleClipEmpty {
			get {
                                bool isEmpty = false;

        			Status status = GDIPlus.GdipIsVisibleClipEmpty (nativeObject, out isEmpty);
				CheckDrawStatus (status);
        			return isEmpty;
			}
		}

		public float PageScale {
			get {
                                float scale;

        			Status status = GDIPlus.GdipGetPageScale (nativeObject, out scale);
				CheckDrawStatus (status);
        			return scale;
			}
			set {
                                if (nativeObject == IntPtr.Zero) return;
                                Status status = GDIPlus.GdipSetPageScale (nativeObject, value);
				CheckDrawStatus (status);
			}
		}

		public GraphicsUnit PageUnit {
			get {
                                GraphicsUnit unit;
                                
                                Status status = GDIPlus.GdipGetPageUnit (nativeObject, out unit);
				CheckDrawStatus (status);
        			return unit;
			}
			set {
                                if (nativeObject == IntPtr.Zero) return;
                                Status status = GDIPlus.GdipSetPageUnit (nativeObject, value);
				CheckDrawStatus (status);
			}
		}

		[MonoTODO ("This property does not do anything when used with libgdiplus.")]
		public PixelOffsetMode PixelOffsetMode {
			get {
			        PixelOffsetMode pixelOffset = PixelOffsetMode.Invalid;
                                
                                Status status = GDIPlus.GdipGetPixelOffsetMode (nativeObject, out pixelOffset);
				CheckDrawStatus (status);
        			return pixelOffset;
			}
			set {
                                if (nativeObject == IntPtr.Zero) return;
                                Status status = GDIPlus.GdipSetPixelOffsetMode (nativeObject, value); 
				CheckDrawStatus (status);
			}
		}

		public Point RenderingOrigin {
			get {
                                int x, y;
				Status status = GDIPlus.GdipGetRenderingOrigin (nativeObject, out x, out y);
				CheckDrawStatus (status);
                                return new Point (x, y);
			}

			set {
                                if (nativeObject == IntPtr.Zero) return;
                                Status status = GDIPlus.GdipSetRenderingOrigin (nativeObject, value.X, value.Y);
				CheckDrawStatus (status);
			}
		}

		public SmoothingMode SmoothingMode {
			get {
                                SmoothingMode mode = SmoothingMode.Invalid;

				Status status = GDIPlus.GdipGetSmoothingMode (nativeObject, out mode);
				CheckDrawStatus (status);
                                return mode;
			}

			set {
                                if (nativeObject == IntPtr.Zero) return;
                                Status status = GDIPlus.GdipSetSmoothingMode (nativeObject, value);
				CheckDrawStatus (status);
			}
		}

		[MonoTODO ("This property does not do anything when used with libgdiplus.")]
		public int TextContrast {
			get {	
                                int contrast;
					
                                Status status = GDIPlus.GdipGetTextContrast (nativeObject, out contrast);
				CheckDrawStatus (status);
                                return contrast;
			}

                        set {
                                if (nativeObject == IntPtr.Zero) return;
                                Status status = GDIPlus.GdipSetTextContrast (nativeObject, value);
				CheckDrawStatus (status);
			}
		}

		public TextRenderingHint TextRenderingHint {
			get {
                                TextRenderingHint hint;

                                Status status = GDIPlus.GdipGetTextRenderingHint (nativeObject, out hint);
				CheckDrawStatus (status);
                                return hint;        
			}

			set {
                                if (nativeObject == IntPtr.Zero) return;
                                Status status = GDIPlus.GdipSetTextRenderingHint (nativeObject, value);
				CheckDrawStatus (status);
			}
		}

		public Matrix Transform {
			get {
                                Matrix matrix = new Matrix ();
                                if (nativeObject == IntPtr.Zero) return matrix;   // recording-only: identity
                                Status status = GDIPlus.GdipGetWorldTransform (nativeObject, matrix.nativeMatrix);
				CheckDrawStatus (status);
                                return matrix;
			}
			set {
				if (value == null)
					throw new ArgumentNullException ("value");
				
                                if (nativeObject == IntPtr.Zero) return;
                                Status status = GDIPlus.GdipSetWorldTransform (nativeObject, value.nativeMatrix);
				CheckDrawStatus (status);
			}
		}

		public RectangleF VisibleClipBounds {
			get {
                                if (nativeObject == IntPtr.Zero) return new RectangleF (0, 0, 1 << 20, 1 << 20);  // recording-only
                                RectangleF rect;
					
                                Status status = GDIPlus.GdipGetVisibleClipBounds (nativeObject, out rect);
				CheckDrawStatus (status);
                                return rect;
			}
		}

		[MonoTODO]
		[EditorBrowsable (EditorBrowsableState.Never)]
		public object GetContextInfo ()
		{
			// only known source of information @ http://blogs.wdevs.com/jdunlap/Default.aspx
			throw new NotImplementedException ();
		}
	}
}
