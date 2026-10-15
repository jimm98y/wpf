// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// The small internal helpers .NET's WinForms code leans on (System.Windows.Forms.Primitives), for
// the code ported from .NET to compile as .NET wrote it. The colour helpers are .NET's exactly;
// the caches hand out a fresh brush or pen (the port has no GDI handles to husband); the screen DC
// is the shared measurement Graphics; and DPI is ScaleHelper's.

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

	/// <summary>
	/// .NET's DPI helpers (System.Windows.Forms.Primitives' ScaleHelper), and where this port decides
	/// which high-DPI mode the application runs in.
	/// </summary>
	/// <remarks>
	/// <para>An application that is per-monitor aware (PerMonitor, PerMonitorV2) lays out in device
	/// pixels, as on Windows: fonts measure at the system DPI, forms auto-scale from their
	/// AutoScaleDimensions, DeviceDpi follows the window's monitor and the host renders 1:1. Any
	/// other mode keeps the virtual 96-dpi surface the host scales to the display.</para>
	/// <para>The mode is the application's: its manifest (dpiAwareness / dpiAware), then
	/// Application.SetHighDpiMode (or a SetProcessDpiAwareness* call the Windows shim forwards), and
	/// DpiUnaware otherwise, as stock .NET. It is fixed the first time anything asks -- the first
	/// control -- as Windows fixes a process's awareness at its first window.</para>
	/// </remarks>
	internal static class ScaleHelper
	{
		internal const int OneHundredPercentLogicalDpi = 96;

		private static readonly object s_lock = new object ();
		private static volatile bool s_resolved;
		private static HighDpiMode s_mode = HighDpiMode.DpiUnaware;
		private static bool s_scaled, s_perMonitorV2;
		private static int s_systemDpi = OneHundredPercentLogicalDpi;
		internal static HighDpiMode? s_requested;
		private static HighDpiMode? s_manifest;
		private static bool s_manifestRead;

		/// <summary>The mode the application declared in its manifest, if it did.</summary>
		internal static HighDpiMode? ManifestMode {
			get {
				if (!s_manifestRead) {
					s_manifestRead = true;
					s_manifest = ReadManifestMode ();
				}
				return s_manifest;
			}
		}

		/// <summary>Whether the mode is fixed (the first control exists).</summary>
		internal static bool IsResolved => s_resolved;

		/// <summary>The mode the application runs in.</summary>
		internal static HighDpiMode Mode { get { Resolve (); return s_mode; } }

		/// <summary>Whether layout is in device pixels at the system DPI (a per-monitor-aware application).</summary>
		internal static bool IsDeviceScaled { get { Resolve (); return s_scaled; } }

		/// <summary>The primary display's DPI at startup for a scaled application, 96 for any other.</summary>
		internal static int InitialSystemDpi { get { Resolve (); return s_systemDpi; } }

		internal static bool IsScalingRequired => InitialSystemDpi != OneHundredPercentLogicalDpi;

		internal static bool IsScalingRequirementMet => IsScalingRequired || IsDeviceScaled;

		/// <summary>Whether DPI changes are processed (WM_DPICHANGED and the before/after-parent
		/// messages): a PerMonitorV2 application whose process the OS made per-monitor aware.</summary>
		internal static bool IsThreadPerMonitorV2Aware { get { Resolve (); return s_perMonitorV2; } }

		private static void Resolve ()
		{
			if (s_resolved)
				return;
			lock (s_lock) {
				if (s_resolved)
					return;
				HighDpiMode mode = ManifestMode ?? s_requested ?? EnvironmentMode () ?? HighDpiMode.DpiUnaware;
				s_mode = mode;
				if (mode == HighDpiMode.PerMonitor || mode == HighDpiMode.PerMonitorV2) {
					// The OS decides what the process can be: an application that already made itself
					// unaware (or a host that did) stays on the scaled 96-dpi surface.
					int awareness = XplatUIWebGpu.EnterPerMonitorAwareness (out int systemDpi);
					if (awareness > 0 && systemDpi > 0) {
						s_scaled = true;
						s_systemDpi = systemDpi;
						s_perMonitorV2 = mode == HighDpiMode.PerMonitorV2 && awareness == 2;
					}
				}
				System.Drawing.WebGpuBackend.DisplayDpi.Set (s_systemDpi);
				s_resolved = true;
			}
		}

		/// <summary>Tests: run as an application of <paramref name="mode"/> on a display of
		/// <paramref name="systemDpi"/>, whatever the machine is, until disposed.</summary>
		internal static IDisposable OverrideForTests (HighDpiMode mode, int systemDpi)
		{
			Resolve ();
			var saved = (s_mode, s_scaled, s_perMonitorV2, s_systemDpi);
			bool scaled = mode == HighDpiMode.PerMonitor || mode == HighDpiMode.PerMonitorV2;
			s_mode = mode;
			s_scaled = scaled;
			s_perMonitorV2 = mode == HighDpiMode.PerMonitorV2;
			s_systemDpi = scaled ? systemDpi : OneHundredPercentLogicalDpi;
			System.Drawing.WebGpuBackend.DisplayDpi.Set (s_systemDpi);
			return new Restore (() => {
				(s_mode, s_scaled, s_perMonitorV2, s_systemDpi) = saved;
				System.Drawing.WebGpuBackend.DisplayDpi.Set (s_systemDpi);
			});
		}

		private sealed class Restore : IDisposable
		{
			private Action _undo;
			internal Restore (Action undo) { _undo = undo; }
			public void Dispose () { _undo?.Invoke (); _undo = null; }
		}

		/// <summary>WINFORMS_HIGHDPIMODE: the mode, for an application run where neither a manifest
		/// nor code states one.</summary>
		private static HighDpiMode? EnvironmentMode ()
		{
			string v = Environment.GetEnvironmentVariable ("WINFORMS_HIGHDPIMODE");
			return !string.IsNullOrEmpty (v) && Enum.TryParse (v, true, out HighDpiMode m) && Enum.IsDefined (typeof (HighDpiMode), m) ? m : (HighDpiMode?) null;
		}

		/// <summary>The awareness an application's manifest declares: dpiAwareness (Windows 10's,
		/// the first value Windows knows wins) over dpiAware. The manifest is the entry assembly's
		/// application host's (app.exe next to app.dll), or the assembly's own Win32 resource.</summary>
		private static HighDpiMode? ReadManifestMode ()
		{
			try {
				var entry = System.Reflection.Assembly.GetEntryAssembly ();
				string location = entry?.Location;
				if (string.IsNullOrEmpty (location))
					return null;
				string exe = System.IO.Path.ChangeExtension (location, ".exe");
				foreach (string file in new [] { exe, location }) {
					if (!System.IO.File.Exists (file))
						continue;
					HighDpiMode? m = ManifestModeOf (System.IO.File.ReadAllBytes (file));
					if (m != null)
						return m;
				}
			} catch (Exception) {
			}
			return null;
		}

		internal static HighDpiMode? ManifestModeOf (byte [] image)
		{
			string text = System.Text.Encoding.UTF8.GetString (image);
			int start = text.IndexOf ("<assembly", StringComparison.Ordinal);
			while (start >= 0) {
				int end = text.IndexOf ("</assembly>", start, StringComparison.Ordinal);
				if (end < 0)
					break;
				string xml = text.Substring (start, end - start);
				string awareness = Element (xml, "dpiAwareness");
				if (awareness != null) {
					foreach (string part in awareness.Split (',')) {
						switch (part.Trim ().ToLowerInvariant ()) {
						case "permonitorv2": return HighDpiMode.PerMonitorV2;
						case "permonitor": return HighDpiMode.PerMonitor;
						case "system": return HighDpiMode.SystemAware;
						case "unaware": return HighDpiMode.DpiUnaware;
						case "unaware-gdiscaled": return HighDpiMode.DpiUnawareGdiScaled;
						}
					}
				}
				string aware = Element (xml, "dpiAware");
				if (aware != null) {
					string v = aware.Trim ().ToLowerInvariant ();
					if (v.Contains ("per monitor") || v.Contains ("permonitor") || v.EndsWith ("/pm"))
						return HighDpiMode.PerMonitor;
					if (v == "true" || v == "system")
						return HighDpiMode.SystemAware;
					if (v == "false")
						return HighDpiMode.DpiUnaware;
				}
				start = text.IndexOf ("<assembly", end, StringComparison.Ordinal);
			}
			return null;
		}

		private static string Element (string xml, string name)
		{
			int i = xml.IndexOf ("<" + name, StringComparison.Ordinal);
			while (i >= 0) {
				int after = i + 1 + name.Length;
				char c = after < xml.Length ? xml [after] : ' ';
				if (c == '>' || char.IsWhiteSpace (c)) {
					int gt = xml.IndexOf ('>', i);
					if (gt < 0)
						return null;
					int close = xml.IndexOf ("</", gt + 1, StringComparison.Ordinal);
					if (close < 0)
						return null;
					return xml.Substring (gt + 1, close - gt - 1);
				}
				i = xml.IndexOf ("<" + name, i + 1, StringComparison.Ordinal);
			}
			return null;
		}

		internal static int ScaleToDpi (int logicalValue, int dpi)
		{
			if (dpi == OneHundredPercentLogicalDpi)
				return logicalValue;
			return (int) Math.Round (dpi / (double) OneHundredPercentLogicalDpi * logicalValue);
		}

		internal static Size ScaleToDpi (Size logicalSize, int dpi) => dpi == OneHundredPercentLogicalDpi
			? logicalSize
			: new Size (ScaleToDpi (logicalSize.Width, dpi), ScaleToDpi (logicalSize.Height, dpi));

		internal static Padding ScaleToDpi (Padding logicalPadding, int dpi) => dpi == OneHundredPercentLogicalDpi
			? logicalPadding
			: new Padding (ScaleToDpi (logicalPadding.Left, dpi), ScaleToDpi (logicalPadding.Top, dpi),
				ScaleToDpi (logicalPadding.Right, dpi), ScaleToDpi (logicalPadding.Bottom, dpi));

		internal static int ScaleToInitialSystemDpi (int logicalValue) => ScaleToDpi (logicalValue, InitialSystemDpi);

		internal static int ScaleToPercent (int value, double percent) => Math.Max (1, (int) Math.Round (value * percent));

		private static int s_scaleMinMax = -1;

		/// <summary>The System.Windows.Forms.ScaleTopLevelFormMinMaxSizeForDpi switch: on for an
		/// application built for .NET 8 or later unless it says otherwise, as stock.</summary>
		internal static bool ScaleTopLevelFormMinMaxSizeForDpi {
			get {
				if (s_scaleMinMax < 0) {
					bool on;
					if (!AppContext.TryGetSwitch ("System.Windows.Forms.ScaleTopLevelFormMinMaxSizeForDpi", out on)) {
						on = false;
						var tfm = System.Reflection.Assembly.GetEntryAssembly ()?.GetCustomAttributes (typeof (System.Runtime.Versioning.TargetFrameworkAttribute), false);
						if (tfm != null && tfm.Length > 0 && tfm [0] is System.Runtime.Versioning.TargetFrameworkAttribute t) {
							var name = new System.Runtime.Versioning.FrameworkName (t.FrameworkName);
							on = name.Identifier == ".NETCoreApp" && name.Version.Major >= 8;
						}
					}
					s_scaleMinMax = on ? 1 : 0;
				}
				return s_scaleMinMax == 1;
			}
		}

		/// <summary>.NET's ScaleToDpi for a bitmap: a copy at the DPI's size, or the bitmap itself at 96.</summary>
		internal static Bitmap ScaleToDpi (Bitmap logicalBitmap, int dpi, bool disposeBitmap = false)
		{
			if (dpi == OneHundredPercentLogicalDpi || logicalBitmap == null)
				return logicalBitmap;
			Size size = ScaleToDpi (logicalBitmap.Size, dpi);
			var scaled = new Bitmap (size.Width, size.Height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
			using (Graphics g = Graphics.FromImage (scaled)) {
				g.InterpolationMode = InterpolationMode.HighQualityBicubic;
				g.DrawImage (logicalBitmap, new Rectangle (0, 0, size.Width, size.Height),
					new Rectangle (0, 0, logicalBitmap.Width, logicalBitmap.Height), GraphicsUnit.Pixel);
			}
			if (disposeBitmap)
				logicalBitmap.Dispose ();
			return scaled;
		}

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
