// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// .NET's internal ControlPaint helpers that its button and label painting call.

using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;

namespace System.Windows.Forms
{
	public sealed partial class ControlPaint
	{
		/// <summary>A one-pixel border on the rectangle's outermost pixels (GDI's Rectangle with a
		/// hollow brush), or a dashed one for the other styles.</summary>
		internal static void DrawBorderSimple (IDeviceContext context, Rectangle bounds, Color color, ButtonBorderStyle style = ButtonBorderStyle.Solid)
		{
			Graphics g = context as Graphics ?? (context as PaintEventArgs)?.Graphics;
			if (g == null || bounds.Width <= 0 || bounds.Height <= 0)
				return;
			if (style == ButtonBorderStyle.Solid) {
				using (var b = new SolidBrush (color)) {
					g.FillRectangle (b, bounds.X, bounds.Y, bounds.Width, 1);
					if (bounds.Height > 1)
						g.FillRectangle (b, bounds.X, bounds.Bottom - 1, bounds.Width, 1);
					if (bounds.Height > 2) {
						g.FillRectangle (b, bounds.X, bounds.Y + 1, 1, bounds.Height - 2);
						if (bounds.Width > 1)
							g.FillRectangle (b, bounds.Right - 1, bounds.Y + 1, 1, bounds.Height - 2);
					}
				}
				return;
			}
			using (Pen pen = new Pen (color) { DashStyle = BorderStyleToDashStyle (style) })
				g.DrawRectangle (pen, bounds.X, bounds.Y, bounds.Width - 1, bounds.Height - 1);
		}

		private static DashStyle BorderStyleToDashStyle (ButtonBorderStyle style)
		{
			switch (style) {
			case ButtonBorderStyle.Dotted: return DashStyle.Dot;
			case ButtonBorderStyle.Dashed: return DashStyle.Dash;
			default: return DashStyle.Solid;
			}
		}

		internal static void DrawHighContrastFocusRectangle (Graphics graphics, Rectangle rectangle, Color color)
		{
			DrawFocusRectangle (graphics, rectangle, color, SystemColors.Control);
		}

		/// <summary>Draws the image with its black remapped to <paramref name="replaceBlack"/> and its
		/// white kept, transparency preserved.</summary>
		internal static void DrawImageColorized (Graphics graphics, Image image, Rectangle destination, Color replaceBlack)
		{
			using (var attributes = new ImageAttributes ()) {
				attributes.SetColorMatrix (RemapBlackAndWhitePreserveTransparentMatrix (replaceBlack, Color.White));
				graphics.DrawImage (image, destination, 0, 0, image.Width, image.Height, GraphicsUnit.Pixel, attributes, null, IntPtr.Zero);
			}
		}

		private static ColorMatrix RemapBlackAndWhitePreserveTransparentMatrix (Color replaceBlack, Color replaceWhite)
		{
			float br = replaceBlack.R / 255f, bg = replaceBlack.G / 255f, bb = replaceBlack.B / 255f;
			float wr = replaceWhite.R / 255f, wg = replaceWhite.G / 255f, wb = replaceWhite.B / 255f;
			return new ColorMatrix {
				Matrix00 = -br, Matrix01 = -bg, Matrix02 = -bb,
				Matrix10 = wr, Matrix11 = wg, Matrix12 = wb,
				Matrix33 = 1f,
				Matrix40 = br, Matrix41 = bg, Matrix42 = bb,
				Matrix44 = 1f,
			};
		}

		private static ImageAttributes disabled_image_attributes;

		/// <summary>.NET's disabled image: a luminance-weighted grey lifted by 0.38, at the image's
		/// own size when <paramref name="unscaledImage"/>.</summary>
		internal static void DrawImageDisabled (Graphics graphics, Image image, Rectangle imageBounds, bool unscaledImage)
		{
			if (disabled_image_attributes == null) {
				var m = new ColorMatrix (new [] {
					new [] { 0.2125f, 0.2125f, 0.2125f, 0f, 0f },
					new [] { 0.2577f, 0.2577f, 0.2577f, 0f, 0f },
					new [] { 0.0361f, 0.0361f, 0.0361f, 0f, 0f },
					new [] { 0f, 0f, 0f, 1f, 0f },
					new [] { 0.38f, 0.38f, 0.38f, 0f, 1f },
				});
				disabled_image_attributes = new ImageAttributes ();
				disabled_image_attributes.ClearColorKey ();
				disabled_image_attributes.SetColorMatrix (m);
			}
			Size size = image.Size;
			Rectangle dest = unscaledImage ? new Rectangle (imageBounds.Location, size) : imageBounds;
			graphics.DrawImage (image, dest, 0, 0, size.Width, size.Height, GraphicsUnit.Pixel, disabled_image_attributes);
		}

		internal static bool IsImageTransparent (Image backgroundImage)
			=> backgroundImage != null && (backgroundImage.Flags & 2) > 0;

		internal static TextFormatFlags ConvertAlignmentToTextFormat (ContentAlignment alignment)
		{
			TextFormatFlags flags = TextFormatFlags.Default;
			if ((alignment & (ContentAlignment) 0x700) != 0) flags |= TextFormatFlags.Bottom;
			else if ((alignment & (ContentAlignment) 0x70) != 0) flags |= TextFormatFlags.VerticalCenter;
			if ((alignment & (ContentAlignment) 0x444) != 0) flags |= TextFormatFlags.Right;
			else if ((alignment & (ContentAlignment) 0x222) != 0) flags |= TextFormatFlags.HorizontalCenter;
			return flags;
		}

		internal static StringFormat CreateStringFormat (Control control, ContentAlignment textAlign, bool showEllipsis, bool useMnemonic)
		{
			var format = new StringFormat {
				Alignment = (textAlign & (ContentAlignment) 0x444) != 0 ? StringAlignment.Far
				          : (textAlign & (ContentAlignment) 0x222) != 0 ? StringAlignment.Center : StringAlignment.Near,
				LineAlignment = (textAlign & (ContentAlignment) 0x700) != 0 ? StringAlignment.Far
				              : (textAlign & (ContentAlignment) 0x70) != 0 ? StringAlignment.Center : StringAlignment.Near,
			};
			if (control.RightToLeft == RightToLeft.Yes)
				format.FormatFlags |= StringFormatFlags.DirectionRightToLeft;
			if (showEllipsis) {
				format.Trimming = StringTrimming.EllipsisCharacter;
				format.FormatFlags |= StringFormatFlags.LineLimit;
			}
			if (!useMnemonic)
				format.HotkeyPrefix = HotkeyPrefix.None;
			else if (control.ShowKeyboardCuesInternal)
				format.HotkeyPrefix = HotkeyPrefix.Show;
			else
				format.HotkeyPrefix = HotkeyPrefix.Hide;
			if (control.AutoSize)
				format.FormatFlags |= StringFormatFlags.MeasureTrailingSpaces;
			return format;
		}

		// .NET's HLSColor luminosity, 0..240, rounded the way it rounds it.
		private static int Luminosity (Color c)
		{
			int max = Math.Max (Math.Max (c.R, c.G), c.B), min = Math.Min (Math.Min (c.R, c.G), c.B);
			return ((max + min) * 240 + 255) / 510;
		}

		internal static bool IsDarker (Color c1, Color c2) => Luminosity (c1) < Luminosity (c2);

		internal static TextFormatFlags CreateTextFormatFlags (Control control, ContentAlignment alignment, bool showEllipsis, bool useMnemonic)
		{
			alignment = control.RtlTranslateContentInternal (alignment);
			TextFormatFlags flags = ConvertAlignmentToTextFormat (alignment);
			flags |= TextFormatFlags.TextBoxControl | TextFormatFlags.WordBreak;
			if (showEllipsis)
				flags |= TextFormatFlags.EndEllipsis;
			if (control.RightToLeft == RightToLeft.Yes)
				flags |= TextFormatFlags.RightToLeft;
			if (!useMnemonic)
				flags |= TextFormatFlags.NoPrefix;
			else if (!control.ShowKeyboardCuesInternal)
				flags |= TextFormatFlags.HidePrefix;
			return flags;
		}
	}
}
