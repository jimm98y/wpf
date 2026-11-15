// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// GDI+ 1.1's effects (GdipCreateEffect / GdipSetEffectParameters / GdipBitmapApplyEffect / GdipDrawImageFX),
// which .NET 9 put on System.Drawing: each effect class is what .NET's is -- its parameters, its ranges (GDI+
// refuses one outside them, InvalidParameter), its derived matrices -- and the effect itself is worked here on
// the bitmap's pixels, as straight ARGB, the colour effects per channel through a table, blur and sharpen as
// a separable Gaussian over premultiplied pixels.

using System.Drawing.Drawing2D;

namespace System.Drawing.Imaging.Effects
{
	/// <summary>The channel a curve adjustment changes.</summary>
	public enum CurveChannel
	{
		All = 0,
		Red = 1,
		Green = 2,
		Blue = 3,
	}

	/// <summary>An effect applied to a bitmap (<see cref="Bitmap.ApplyEffect"/>) or as an image is drawn.</summary>
	public abstract class Effect : IDisposable
	{
		bool disposed;

		private protected Effect ()
		{
		}

		~Effect () => Dispose (false);

		public void Dispose ()
		{
			Dispose (true);
			GC.SuppressFinalize (this);
		}

		protected virtual void Dispose (bool disposing)
		{
			disposed = true;
		}

		/// <summary>A disposed effect has no GDI+ object: GDI+ is handed none (InvalidParameter).</summary>
		internal void CheckLive ()
		{
			if (disposed)
				GDIPlus.CheckStatus (Status.InvalidParameter);
		}

		/// <summary>GdipSetEffectParameters: a value outside the effect's range is InvalidParameter.</summary>
		private protected static void Range (bool ok)
		{
			if (!ok)
				GDIPlus.CheckStatus (Status.InvalidParameter);
		}

		/// <summary>How far past the image the effect reaches when drawn with its edge expanded.</summary>
		internal virtual int Margin => 0;

		/// <summary>Works the effect on straight ARGB pixels, row by row.</summary>
		internal abstract void Apply (uint [] argb, int width, int height);

		// ---- the pixel kit ------------------------------------------------------------------------------

		internal static byte Clamp (float v) => v <= 0 ? (byte) 0 : v >= 255 ? (byte) 255 : (byte) (v + 0.5f);

		/// <summary>The same table for each colour channel, alpha untouched.</summary>
		private protected static void ApplyTables (uint [] argb, byte [] r, byte [] g, byte [] b)
		{
			for (int i = 0; i < argb.Length; i++) {
				uint p = argb [i];
				argb [i] = (p & 0xFF000000) | ((uint) r [(p >> 16) & 0xFF] << 16) | ((uint) g [(p >> 8) & 0xFF] << 8) | b [p & 0xFF];
			}
		}

		private protected static byte [] Table (Func<float, float> curve)
		{
			var t = new byte [256];
			for (int v = 0; v < 256; v++)
				t [v] = Clamp (curve (v / 255f) * 255f);
			return t;
		}

		internal static readonly byte [] Identity = Table (x => x);

		private protected static float Luminance (float r, float g, float b) => 0.299f * r + 0.587f * g + 0.114f * b;
	}

	/// <summary>A Gaussian blur.</summary>
	public class BlurEffect : Effect
	{
		readonly float radius;
		readonly bool expandEdge;

		public BlurEffect (float radius, bool expandEdge)
		{
			Range (radius >= 0 && radius <= 255);
			this.radius = radius;
			this.expandEdge = expandEdge;
		}

		public float Radius => radius;

		public bool ExpandEdge => expandEdge;

		internal override int Margin => expandEdge ? (int) MathF.Ceiling (radius) : 0;

		internal override void Apply (uint [] argb, int width, int height)
		{
			// With the edge expanded, what lies past the image is transparent: the edge softens into it.
			// Without, the edge pixels carry on outward and the image keeps its hard edge.
			Gaussian.Blur (argb, width, height, radius, clampEdges: !expandEdge);
		}
	}

	/// <summary>Sharpening: the image less a blur of it, by an amount.</summary>
	public class SharpenEffect : Effect
	{
		readonly float radius, amount;

		public SharpenEffect (float radius, float amount)
		{
			Range (radius >= 0 && radius <= 255 && amount >= 0 && amount <= 100);
			this.radius = radius;
			this.amount = amount;
		}

		public float Radius => radius;

		public float Amount => amount;

		internal override void Apply (uint [] argb, int width, int height)
		{
			if (radius <= 0 || amount <= 0)
				return;
			var blurred = (uint []) argb.Clone ();
			Gaussian.Blur (blurred, width, height, radius, clampEdges: true);
			float k = amount / 100f * 2f;
			for (int i = 0; i < argb.Length; i++) {
				uint p = argb [i], q = blurred [i];
				float r = (p >> 16) & 0xFF, g = (p >> 8) & 0xFF, b = p & 0xFF;
				r += k * (r - ((q >> 16) & 0xFF));
				g += k * (g - ((q >> 8) & 0xFF));
				b += k * (b - (q & 0xFF));
				argb [i] = (p & 0xFF000000) | ((uint) Clamp (r) << 16) | ((uint) Clamp (g) << 8) | Clamp (b);
			}
		}
	}

	/// <summary>A tint: the colours drawn toward a hue (or away from it, toward its opposite).</summary>
	public class TintEffect : Effect
	{
		readonly int hue, amount;

		// .NET's: the colour's hue, and no tint for none or for white.
		public TintEffect (Color color, int amount) : this ((int) color.GetHue (), color.IsEmpty || color == Color.White ? 0 : amount)
		{
		}

		public TintEffect (int hue, int amount)
		{
			if (hue < 0 || hue > 360)
				throw new ArgumentOutOfRangeException (nameof (hue));
			if (hue > 180)
				hue -= 360;
			Range (amount >= -100 && amount <= 100);
			this.hue = hue;
			this.amount = amount;
		}

		public int Hue => hue < 0 ? hue + 360 : hue;

		public int Amount => amount;

		internal override void Apply (uint [] argb, int width, int height)
		{
			if (amount == 0)
				return;
			// GDI+'s wheel: 0 is blue, clockwise (positive) through cyan (60) to green (120), counter-clockwise
			// through magenta (-60) to red (-120). A negative amount tints toward the opposite hue.
			float wheel = 240 - hue + (amount < 0 ? 180 : 0);
			float a = Math.Abs (amount) / 100f;
			HueToRgb (((wheel % 360) + 360) % 360, out float tr, out float tg, out float tb);
			float tl = Luminance (tr, tg, tb);
			for (int i = 0; i < argb.Length; i++) {
				uint p = argb [i];
				float r = ((p >> 16) & 0xFF) / 255f, g = ((p >> 8) & 0xFF) / 255f, b = (p & 0xFF) / 255f;
				// the tint colour at the pixel's own lightness, the pixel moved toward it by the amount
				float s = Luminance (r, g, b) / tl;
				r += a * (Math.Min (1, tr * s) - r);
				g += a * (Math.Min (1, tg * s) - g);
				b += a * (Math.Min (1, tb * s) - b);
				argb [i] = (p & 0xFF000000) | ((uint) Clamp (r * 255) << 16) | ((uint) Clamp (g * 255) << 8) | Clamp (b * 255);
			}
		}

		static void HueToRgb (float h, out float r, out float g, out float b)
		{
			float x = 1 - Math.Abs ((h / 60f) % 2 - 1);
			switch ((int) (h / 60) % 6) {
			case 0: r = 1; g = x; b = 0; break;
			case 1: r = x; g = 1; b = 0; break;
			case 2: r = 0; g = 1; b = x; break;
			case 3: r = 0; g = x; b = 1; break;
			case 4: r = x; g = 0; b = 1; break;
			default: r = 1; g = 0; b = x; break;
			}
		}
	}

	/// <summary>Brightness and contrast.</summary>
	public class BrightnessContrastEffect : Effect
	{
		readonly int brightness, contrast;

		public BrightnessContrastEffect (int brightnessLevel, int contrastLevel)
		{
			Range (brightnessLevel >= -255 && brightnessLevel <= 255 && contrastLevel >= -100 && contrastLevel <= 100);
			brightness = brightnessLevel;
			contrast = contrastLevel;
		}

		public int BrightnessLevel => brightness;

		public int ContrastLevel => contrast;

		internal override void Apply (uint [] argb, int width, int height)
		{
			float f = Contrast (contrast), shift = brightness / 255f;
			byte [] t = Table (x => (x + shift - 0.5f) * f + 0.5f);
			ApplyTables (argb, t, t, t);
		}

		/// <summary>The slope a contrast level gives about the middle grey: flat at -100, four times at 100.</summary>
		internal static float Contrast (int level)
		{
			float s = (100 + level) / 100f;
			return s * s;
		}
	}

	/// <summary>The balance of each colour against its complement.</summary>
	public class ColorBalanceEffect : Effect
	{
		readonly int cyanRed, magentaGreen, yellowBlue;

		public ColorBalanceEffect (int cyanRed, int magentaGreen, int yellowBlue)
		{
			Range (cyanRed >= -100 && cyanRed <= 100 && magentaGreen >= -100 && magentaGreen <= 100 && yellowBlue >= -100 && yellowBlue <= 100);
			this.cyanRed = cyanRed;
			this.magentaGreen = magentaGreen;
			this.yellowBlue = yellowBlue;
		}

		public int CyanRed => cyanRed;

		public int MagentaGreen => magentaGreen;

		public int YellowBlue => yellowBlue;

		internal override void Apply (uint [] argb, int width, int height)
		{
			// Toward red is away from cyan: the red channel moves, half its range at the ends.
			ApplyTables (argb, Shift (cyanRed), Shift (magentaGreen), Shift (yellowBlue));
		}

		static byte [] Shift (int level) => level == 0 ? Identity : Table (x => x + level / 200f * (level > 0 ? 1 - x : x) * 2);
	}

	/// <summary>Levels: the shadows and highlights clipped, the midtones lightened or darkened.</summary>
	public class LevelsEffect : Effect
	{
		readonly int highlight, midtone, shadow;

		public LevelsEffect (int highlight, int midtone, int shadow)
		{
			Range (highlight >= 0 && highlight <= 100 && midtone >= -100 && midtone <= 100 && shadow >= 0 && shadow <= 100);
			this.highlight = highlight;
			this.midtone = midtone;
			this.shadow = shadow;
		}

		public int Highlight => highlight;

		public int Midtone => midtone;

		public int Shadow => shadow;

		internal override void Apply (uint [] argb, int width, int height)
		{
			// A channel above the highlight's percentage of full is full, one below the shadow's is none, and
			// what is between spreads over the whole range, bent by the midtone.
			float lo = shadow / 100f, hi = highlight / 100f;
			float gamma = MathF.Pow (2, -midtone / 100f);
			byte [] t = Table (x => {
				if (hi <= lo)
					return x >= hi ? 1 : 0;
				float v = Math.Clamp ((x - lo) / (hi - lo), 0, 1);
				return MathF.Pow (v, gamma);
			});
			ApplyTables (argb, t, t, t);
		}
	}

	/// <summary>A 5x5 colour matrix applied to each pixel.</summary>
	public class ColorMatrixEffect : Effect
	{
		readonly ColorMatrix matrix;

		public ColorMatrixEffect (ColorMatrix matrix)
		{
			if (matrix == null)
				throw new NullReferenceException ();
			this.matrix = matrix;
		}

		public ColorMatrix Matrix => matrix;

		internal override void Apply (uint [] argb, int width, int height)
		{
			var m = new float [5, 5];
			for (int i = 0; i < 5; i++)
				for (int j = 0; j < 5; j++)
					m [i, j] = matrix [i, j];
			for (int i = 0; i < argb.Length; i++) {
				uint p = argb [i];
				float r = ((p >> 16) & 0xFF) / 255f, g = ((p >> 8) & 0xFF) / 255f, b = (p & 0xFF) / 255f, a = (p >> 24) / 255f;
				float nr = r * m [0, 0] + g * m [1, 0] + b * m [2, 0] + a * m [3, 0] + m [4, 0];
				float ng = r * m [0, 1] + g * m [1, 1] + b * m [2, 1] + a * m [3, 1] + m [4, 1];
				float nb = r * m [0, 2] + g * m [1, 2] + b * m [2, 2] + a * m [3, 2] + m [4, 2];
				float na = r * m [0, 3] + g * m [1, 3] + b * m [2, 3] + a * m [3, 3] + m [4, 3];
				argb [i] = ((uint) Clamp (na * 255) << 24) | ((uint) Clamp (nr * 255) << 16) | ((uint) Clamp (ng * 255) << 8) | Clamp (nb * 255);
			}
		}
	}

	public sealed class GrayScaleEffect : ColorMatrixEffect
	{
		public GrayScaleEffect () : base (new ColorMatrix (new float [] {
			0.299f, 0.299f, 0.299f, 0, 0,
			0.587f, 0.587f, 0.587f, 0, 0,
			0.114f, 0.114f, 0.114f, 0, 0,
			0, 0, 0, 1, 0,
			0, 0, 0, 0, 1 }))
		{
		}
	}

	public sealed class SepiaEffect : ColorMatrixEffect
	{
		public SepiaEffect () : base (new ColorMatrix (new float [] {
			0.393f, 0.349f, 0.272f, 0, 0,
			0.769f, 0.686f, 0.534f, 0, 0,
			0.189f, 0.168f, 0.131f, 0, 0,
			0, 0, 0, 1, 0,
			0, 0, 0, 0, 1 }))
		{
		}
	}

	public sealed class VividEffect : ColorMatrixEffect
	{
		public VividEffect () : base (new ColorMatrix (new float [] {
			1.2f, -0.1f, -0.1f, 0, 0,
			-0.1f, 1.2f, -0.1f, 0, 0,
			-0.1f, -0.1f, 1.2f, 0, 0,
			0, 0, 0, 1, 0,
			0, 0, 0, 0, 1 }))
		{
		}
	}

	public sealed class InvertEffect : ColorMatrixEffect
	{
		public InvertEffect () : base (new ColorMatrix (new float [] {
			-1.0f, 0, 0, 0, 0,
			0, -1.0f, 0, 0, 0,
			0, 0, -1.0f, 0, 0,
			0, 0, 0, 1, 0,
			1, 1, 1, 1, 1 }))
		{
		}
	}

	/// <summary>A table for each channel: each value replaced by its entry.</summary>
	public class ColorLookupTableEffect : Effect
	{
		// GDI+'s ColorLUTParams: blue, green, red, alpha, 256 entries each; entries not given are 0.
		readonly byte [] bytes = new byte [1024];

		public ColorLookupTableEffect (byte [] redLookupTable, byte [] greenLookupTable, byte [] blueLookupTable, byte [] alphaLookupTable)
			: this (redLookupTable.AsSpan (), greenLookupTable, blueLookupTable, alphaLookupTable)
		{
		}

		public ColorLookupTableEffect (ReadOnlySpan<byte> redLookupTable, ReadOnlySpan<byte> greenLookupTable, ReadOnlySpan<byte> blueLookupTable, ReadOnlySpan<byte> alphaLookupTable)
		{
			ArgumentOutOfRangeException.ThrowIfGreaterThan (redLookupTable.Length, 256, nameof (redLookupTable));
			ArgumentOutOfRangeException.ThrowIfGreaterThan (greenLookupTable.Length, 256, nameof (greenLookupTable));
			ArgumentOutOfRangeException.ThrowIfGreaterThan (blueLookupTable.Length, 256, nameof (blueLookupTable));
			ArgumentOutOfRangeException.ThrowIfGreaterThan (alphaLookupTable.Length, 256, nameof (alphaLookupTable));
			Span<byte> b = bytes;
			blueLookupTable.CopyTo (b);
			greenLookupTable.CopyTo (b [256..]);
			redLookupTable.CopyTo (b [512..]);
			alphaLookupTable.CopyTo (b [768..]);
		}

		public ReadOnlyMemory<byte> BlueLookupTable => new ReadOnlyMemory<byte> (bytes, 0, 256);

		public ReadOnlyMemory<byte> GreenLookupTable => new ReadOnlyMemory<byte> (bytes, 256, 256);

		public ReadOnlyMemory<byte> RedLookupTable => new ReadOnlyMemory<byte> (bytes, 512, 256);

		public ReadOnlyMemory<byte> AlphaLookupTable => new ReadOnlyMemory<byte> (bytes, 768, 256);

		internal override void Apply (uint [] argb, int width, int height)
		{
			for (int i = 0; i < argb.Length; i++) {
				uint p = argb [i];
				argb [i] = ((uint) bytes [768 + (p >> 24)] << 24) | ((uint) bytes [512 + ((p >> 16) & 0xFF)] << 16)
					| ((uint) bytes [256 + ((p >> 8) & 0xFF)] << 8) | bytes [p & 0xFF];
			}
		}
	}

	// ---- the curves --------------------------------------------------------------------------------------

	/// <summary>A tone curve on one channel or on all of them.</summary>
	public abstract class ColorCurveEffect : Effect
	{
		readonly CurveChannel channel;
		readonly int adjustValue;

		private protected ColorCurveEffect (CurveChannel channel, int adjustValue, int min, int max)
		{
			Range (channel >= CurveChannel.All && channel <= CurveChannel.Blue && adjustValue >= min && adjustValue <= max);
			this.channel = channel;
			this.adjustValue = adjustValue;
		}

		public CurveChannel Channel => channel;

		private protected int AdjustValue => adjustValue;

		/// <summary>The curve, from a channel's value to its new one, both 0 to 1.</summary>
		private protected abstract float Curve (float x);

		internal override void Apply (uint [] argb, int width, int height)
		{
			byte [] t = Table (Curve);
			ApplyTables (argb,
				channel == CurveChannel.All || channel == CurveChannel.Red ? t : Identity,
				channel == CurveChannel.All || channel == CurveChannel.Green ? t : Identity,
				channel == CurveChannel.All || channel == CurveChannel.Blue ? t : Identity);
		}
	}

	/// <summary>Exposure: the light let in, doubled at 255, none at -255.</summary>
	public class ExposureCurveEffect : ColorCurveEffect
	{
		public ExposureCurveEffect (CurveChannel channel, int exposure) : base (channel, exposure, -255, 255)
		{
		}

		public int Exposure => AdjustValue;

		private protected override float Curve (float x) => x * (1 + AdjustValue / 255f);
	}

	/// <summary>Density: positive darkens (a denser negative), negative lightens.</summary>
	public class DensityCurveEffect : ColorCurveEffect
	{
		public DensityCurveEffect (CurveChannel channel, int density) : base (channel, density, -255, 255)
		{
		}

		public int Density => AdjustValue;

		private protected override float Curve (float x) => MathF.Pow (x, MathF.Pow (2, AdjustValue / 128f));
	}

	/// <summary>Contrast about the middle grey.</summary>
	public class ContrastCurveEffect : ColorCurveEffect
	{
		public ContrastCurveEffect (CurveChannel channel, int contrast) : base (channel, contrast, -100, 100)
		{
		}

		public int Contrast => AdjustValue;

		private protected override float Curve (float x) => (x - 0.5f) * BrightnessContrastEffect.Contrast (AdjustValue) + 0.5f;
	}

	/// <summary>The highlights lightened (positive) or darkened, the shadows left as they are.</summary>
	public class HighlightCurveEffect : ColorCurveEffect
	{
		public HighlightCurveEffect (CurveChannel channel, int highlight) : base (channel, highlight, -100, 100)
		{
		}

		public int Highlight => AdjustValue;

		private protected override float Curve (float x) => x + AdjustValue / 100f * 2 * x * x * (1 - x);
	}

	/// <summary>The shadows lightened (positive) or darkened, the highlights left as they are.</summary>
	public class ShadowCurveEffect : ColorCurveEffect
	{
		public ShadowCurveEffect (CurveChannel channel, int shadow) : base (channel, shadow, -100, 100)
		{
		}

		public int Shadow => AdjustValue;

		private protected override float Curve (float x) => x + AdjustValue / 100f * 2 * x * (1 - x) * (1 - x);
	}

	/// <summary>The midtones lightened (positive) or darkened, black and white where they are.</summary>
	public class MidtoneCurveEffect : ColorCurveEffect
	{
		public MidtoneCurveEffect (CurveChannel channel, int midtone) : base (channel, midtone, -100, 100)
		{
		}

		public int Midtone => AdjustValue;

		private protected override float Curve (float x) => MathF.Pow (x, MathF.Pow (2, -AdjustValue / 100f));
	}

	/// <summary>White saturation: 0 to the value spread over the whole range, what is above it white.</summary>
	public class WhiteSaturationCurveEffect : ColorCurveEffect
	{
		public WhiteSaturationCurveEffect (CurveChannel channel, int whiteSaturation) : base (channel, whiteSaturation, 0, 255)
		{
		}

		public int WhiteSaturation => AdjustValue;

		private protected override float Curve (float x) => AdjustValue == 0 ? (x > 0 ? 1 : 0) : x * 255f / AdjustValue;
	}

	/// <summary>Black saturation: the value to 255 spread over the whole range, what is below it black.</summary>
	public class BlackSaturationCurveEffect : ColorCurveEffect
	{
		public BlackSaturationCurveEffect (CurveChannel channel, int blackSaturation) : base (channel, blackSaturation, 0, 255)
		{
		}

		public int BlackSaturation => AdjustValue;

		private protected override float Curve (float x) => AdjustValue == 255 ? (x >= 1 ? 1 : 0) : (x * 255f - AdjustValue) / (255f - AdjustValue);
	}

	// ---- the blur --------------------------------------------------------------------------------------

	static class Gaussian
	{
		/// <summary>A separable Gaussian of the radius (three standard deviations), over premultiplied colour so
		/// a transparent neighbour darkens nothing. Past the edges: the edge pixel again, or transparency.</summary>
		internal static void Blur (uint [] argb, int width, int height, float radius, bool clampEdges)
		{
			if (radius <= 0 || width <= 0 || height <= 0)
				return;
			int reach = (int) MathF.Ceiling (radius);
			float sigma = Math.Max (radius / 3f, 0.3f);
			var kernel = new float [reach * 2 + 1];
			float sum = 0;
			for (int i = -reach; i <= reach; i++)
				sum += kernel [i + reach] = MathF.Exp (-(i * i) / (2 * sigma * sigma));
			for (int i = 0; i < kernel.Length; i++)
				kernel [i] /= sum;

			int n = width * height;
			var a = new float [n]; var r = new float [n]; var g = new float [n]; var b = new float [n];
			for (int i = 0; i < n; i++) {
				uint p = argb [i];
				float al = (p >> 24) / 255f;
				a [i] = al;
				r [i] = ((p >> 16) & 0xFF) * al;
				g [i] = ((p >> 8) & 0xFF) * al;
				b [i] = (p & 0xFF) * al;
			}
			Pass (a, r, g, b, width, height, kernel, reach, clampEdges, horizontal: true);
			Pass (a, r, g, b, width, height, kernel, reach, clampEdges, horizontal: false);
			for (int i = 0; i < n; i++) {
				float al = a [i];
				argb [i] = al <= 0 ? 0 : ((uint) Effect.Clamp (al * 255) << 24) | ((uint) Effect.Clamp (r [i] / al) << 16)
					| ((uint) Effect.Clamp (g [i] / al) << 8) | Effect.Clamp (b [i] / al);
			}
		}

		static void Pass (float [] a, float [] r, float [] g, float [] b, int width, int height, float [] kernel, int reach, bool clamp, bool horizontal)
		{
			int lines = horizontal ? height : width, length = horizontal ? width : height;
			var la = new float [length]; var lr = new float [length]; var lg = new float [length]; var lb = new float [length];
			for (int line = 0; line < lines; line++) {
				int Index (int k) => horizontal ? line * width + k : k * width + line;
				for (int k = 0; k < length; k++) {
					int at = Index (k);
					la [k] = a [at]; lr [k] = r [at]; lg [k] = g [at]; lb [k] = b [at];
				}
				for (int k = 0; k < length; k++) {
					float sa = 0, sr = 0, sg = 0, sb = 0;
					for (int t = -reach; t <= reach; t++) {
						int s = k + t;
						if (s < 0 || s >= length) {
							if (!clamp)
								continue;
							s = s < 0 ? 0 : length - 1;
						}
						float w = kernel [t + reach];
						sa += la [s] * w; sr += lr [s] * w; sg += lg [s] * w; sb += lb [s] * w;
					}
					int at = Index (k);
					a [at] = sa; r [at] = sr; g [at] = sg; b [at] = sb;
				}
			}
		}
	}
}
