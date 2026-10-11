// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// The small internal helpers .NET's WinForms code leans on (System.Windows.Forms.Primitives), for
// the code ported from .NET to compile as .NET wrote it. The colour helpers are .NET's exactly;
// the caches hand out a fresh brush or pen (the port has no GDI handles to husband); the screen DC
// is the shared measurement Graphics; and the port draws at 96 dpi with no per-monitor scaling.

using System.Drawing;
using System.Drawing.Drawing2D;

namespace System.Windows.Forms
{
	internal static class SystemDrawingExtensions
	{
		internal static Color FindNearestColor (this Graphics graphics, Color color) => color;

		internal static bool HasTransparency (this Color color) => color.A != byte.MaxValue;

		internal static bool IsFullyTransparent (this Color color) => color.A == 0;

		internal static Color MixColor (this Color color1, Color color2)
			=> Color.FromArgb ((color1.A + color2.A) / 2, (color1.R + color2.R) / 2, (color1.G + color2.G) / 2, (color1.B + color2.B) / 2);

		internal static Color InvertColor (this Color color)
			=> Color.FromArgb (color.A, (byte) ~color.R, (byte) ~color.G, (byte) ~color.B);

		internal static Pen CreateStaticPen (this Color color, DashStyle dashStyle)
			=> new Pen (Color.FromArgb (color.ToArgb ())) { DashStyle = dashStyle };

		internal static Pen CreateStaticPen (this Brush brush, float width = 1f) => new Pen (brush, width);

		internal static SolidBrush CreateStaticBrush (this Color color) => new SolidBrush (Color.FromArgb (color.ToArgb ()));

		internal static void DrawLines (this Graphics graphics, Pen pen, ReadOnlySpan<int> lines)
		{
			for (int i = 0; i < lines.Length; i += 4)
				graphics.DrawLine (pen, lines [i], lines [i + 1], lines [i + 2], lines [i + 3]);
		}

		internal static T OrThrowIfNull<T> (this T value, string paramName) where T : class
			=> value ?? throw new ArgumentNullException (paramName);

		/// <summary>The Graphics behind a device context, when it has one.</summary>
		internal static Graphics TryGetGraphics (this IDeviceContext deviceContext, bool create = false)
			=> deviceContext as Graphics ?? (deviceContext as PaintEventArgs)?.Graphics;
	}

	/// <summary>A brush from .NET's brush cache, as a scope: here simply a brush of its own.</summary>
	internal readonly struct SolidBrushScope : IDisposable
	{
		private readonly SolidBrush _brush;
		internal SolidBrushScope (Color color) { _brush = new SolidBrush (color); }
		public static explicit operator SolidBrush (SolidBrushScope scope) => scope._brush;
		public static implicit operator Brush (SolidBrushScope scope) => scope._brush;
		public void Dispose () => _brush?.Dispose ();
	}

	internal readonly struct PenScope : IDisposable
	{
		private readonly Pen _pen;
		internal PenScope (Color color, int width) { _pen = new Pen (color, width); }
		public static implicit operator Pen (PenScope scope) => scope._pen;
		public void Dispose () => _pen?.Dispose ();
	}

	internal static class GdiPlusCache
	{
		internal static SolidBrushScope GetCachedSolidBrushScope (this Color color) => new SolidBrushScope (color);
		internal static PenScope GetCachedPenScope (this Color color, int width = 1) => new PenScope (color, width);
	}

	/// <summary>.NET's screen DC for measuring: here the shared measurement Graphics.</summary>
	internal static class ScreenDcCache
	{
		internal readonly struct ScreenDcScope : IDisposable, IDeviceContext
		{
			internal readonly Graphics Graphics;
			internal ScreenDcScope (Graphics g) { Graphics = g; }
			public static implicit operator Graphics (ScreenDcScope scope) => scope.Graphics;
			public IntPtr GetHdc () => Graphics.GetHdc ();
			public void ReleaseHdc () => Graphics.ReleaseHdc ();
			public void Dispose () { }
		}
	}

	internal static class GdiCache
	{
		internal readonly struct ScreenGraphicsScope : IDisposable
		{
			internal readonly Graphics Graphics;
			internal ScreenGraphicsScope (Graphics g) { Graphics = g; }
			public static implicit operator Graphics (ScreenGraphicsScope scope) => scope.Graphics;
			public void Dispose () { }
		}

		internal static ScreenDcCache.ScreenDcScope GetScreenHdc () => new ScreenDcCache.ScreenDcScope (Hwnd.GraphicsContext);

		internal static ScreenGraphicsScope GetScreenDCGraphics () => new ScreenGraphicsScope (Hwnd.GraphicsContext);
	}

	/// <summary>.NET's DPI helpers. The port lays out and draws at 96 dpi: nothing is rescaled.</summary>
	internal static class ScaleHelper
	{
		internal const int OneHundredPercentLogicalDpi = 96;
		internal static int InitialSystemDpi => OneHundredPercentLogicalDpi;
		internal static bool IsScalingRequired => false;
		internal static bool IsScalingRequirementMet => false;
		internal static bool IsThreadPerMonitorV2Aware => false;
		internal static int ScaleToDpi (int value, int dpi) => (int) Math.Round (value * (double) dpi / OneHundredPercentLogicalDpi);
		internal static int ScaleToInitialSystemDpi (int value) => value;

		/// <summary>.NET's GetIconResourceAsBitmap: one of its icon resources, at a size, as a bitmap.</summary>
		internal static Bitmap GetIconResourceAsBitmap (Type type, string resource, Size size)
		{
			using (var stream = type.Assembly.GetManifestResourceStream ("System.Windows.Forms." + resource))
			using (var icon = new Icon (stream, size))
				return icon.ToBitmap ();
		}

		internal static Bitmap GetIconResourceAsDefaultSizeBitmap (Type type, string resource)
			=> GetIconResourceAsBitmap (type, resource, SystemInformation.SmallIconSize);
	}

	internal static class DisplayInformation
	{
		internal static bool HighContrast => SystemInformation.HighContrast;
		internal static bool LowResolution => false;
	}
}
