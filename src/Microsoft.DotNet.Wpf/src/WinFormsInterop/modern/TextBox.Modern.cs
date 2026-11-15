// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// What .NET's TextBox and TextBoxBase have that Mono's did not, ported from .NET's: the PlaceholderText an empty box
// shows in grey while it does not have focus (drawn where .NET draws it over the edit control: top-aligned, its text
// alignment, one pixel in), the IME mode of a read-only or password box (Disable), ContainsNavigationKeyCode.

using System.ComponentModel;
using System.Drawing;

namespace System.Windows.Forms
{
	public partial class TextBox
	{
		private string _placeholderText = string.Empty;

		/// <summary>The text shown in grey while the box is empty and does not have focus.</summary>
		[Localizable (true)]
		[DefaultValue ("")]
		[MWFCategory ("Appearance")]
		public virtual string PlaceholderText {
			get => _placeholderText;
			set {
				value ??= string.Empty;
				if (_placeholderText != value) {
					_placeholderText = value;
					if (IsHandleCreated)
						Invalidate ();
				}
			}
		}

		internal bool ShouldRenderPlaceholderText (bool ignoreFocus = false) =>
			!string.IsNullOrEmpty (_placeholderText) && !GetStyle (ControlStyles.UserPaint) && (ignoreFocus || !Focused) && TextLength == 0;

		internal void DrawPlaceholderText (Graphics g)
		{
			TextFormatFlags flags = TextFormatFlags.NoPadding | TextFormatFlags.Top | TextFormatFlags.EndEllipsis;
			Rectangle rectangle = ClientRectangle;

			if (RightToLeft == RightToLeft.Yes) {
				flags |= TextFormatFlags.RightToLeft;
				switch (TextAlign) {
				case HorizontalAlignment.Center:
					flags |= TextFormatFlags.HorizontalCenter;
					rectangle.Offset (0, 1);
					break;
				case HorizontalAlignment.Left:
					flags |= TextFormatFlags.Right;
					rectangle.Offset (1, 1);
					break;
				case HorizontalAlignment.Right:
					flags |= TextFormatFlags.Left;
					rectangle.Offset (0, 1);
					break;
				}
			} else {
				switch (TextAlign) {
				case HorizontalAlignment.Center:
					flags |= TextFormatFlags.HorizontalCenter;
					rectangle.Offset (0, 1);
					break;
				case HorizontalAlignment.Left:
					flags |= TextFormatFlags.Left;
					rectangle.Offset (1, 1);
					break;
				case HorizontalAlignment.Right:
					flags |= TextFormatFlags.Right;
					rectangle.Offset (0, 1);
					break;
				}
			}

			TextRenderer.DrawText (g, _placeholderText, Font, rectangle, SystemColors.GrayText, flags);
		}

		protected override void OnLostFocus (EventArgs e)
		{
			base.OnLostFocus (e);
			if (ShouldRenderPlaceholderText ())
				Invalidate ();
		}
	}

	public abstract partial class TextBoxBase
	{
		/// <summary>A read-only or password box takes no input method: its IME mode is Disable.</summary>
		protected override ImeMode ImeModeBase {
			get => (DesignMode || CanEnableIme) ? base.ImeModeBase : ImeMode.Disable;
			set => base.ImeModeBase = value;
		}

		/// <summary>Whether <paramref name="keyCode"/> moves the caret: the arrows, Page Up and Down, Home, End.</summary>
		protected bool ContainsNavigationKeyCode (Keys keyCode) => keyCode switch {
			Keys.Up or Keys.Down or Keys.PageUp or Keys.PageDown or Keys.Home or Keys.End or Keys.Left or Keys.Right => true,
			_ => false,
		};
	}

	public sealed partial class TextRenderer
	{
		public static void DrawText (IDeviceContext dc, ReadOnlySpan<char> text, Font font, Point pt, Color foreColor) =>
			DrawText (dc, text.ToString (), font, pt, foreColor);

		public static void DrawText (IDeviceContext dc, ReadOnlySpan<char> text, Font font, Point pt, Color foreColor, Color backColor) =>
			DrawText (dc, text.ToString (), font, pt, foreColor, backColor);

		public static void DrawText (IDeviceContext dc, ReadOnlySpan<char> text, Font font, Point pt, Color foreColor, TextFormatFlags flags) =>
			DrawText (dc, text.ToString (), font, pt, foreColor, flags);

		public static void DrawText (IDeviceContext dc, ReadOnlySpan<char> text, Font font, Point pt, Color foreColor, Color backColor, TextFormatFlags flags) =>
			DrawText (dc, text.ToString (), font, pt, foreColor, backColor, flags);

		public static void DrawText (IDeviceContext dc, ReadOnlySpan<char> text, Font font, Rectangle bounds, Color foreColor) =>
			DrawText (dc, text.ToString (), font, bounds, foreColor);

		public static void DrawText (IDeviceContext dc, ReadOnlySpan<char> text, Font font, Rectangle bounds, Color foreColor, Color backColor) =>
			DrawText (dc, text.ToString (), font, bounds, foreColor, backColor);

		public static void DrawText (IDeviceContext dc, ReadOnlySpan<char> text, Font font, Rectangle bounds, Color foreColor, TextFormatFlags flags) =>
			DrawText (dc, text.ToString (), font, bounds, foreColor, flags);

		public static void DrawText (IDeviceContext dc, ReadOnlySpan<char> text, Font font, Rectangle bounds, Color foreColor, Color backColor, TextFormatFlags flags) =>
			DrawText (dc, text.ToString (), font, bounds, foreColor, backColor, flags);

		public static Size MeasureText (ReadOnlySpan<char> text, Font font) => MeasureText (text.ToString (), font);

		public static Size MeasureText (ReadOnlySpan<char> text, Font font, Size proposedSize) => MeasureText (text.ToString (), font, proposedSize);

		public static Size MeasureText (ReadOnlySpan<char> text, Font font, Size proposedSize, TextFormatFlags flags) =>
			MeasureText (text.ToString (), font, proposedSize, flags);

		public static Size MeasureText (IDeviceContext dc, ReadOnlySpan<char> text, Font font) => MeasureText (dc, text.ToString (), font);

		public static Size MeasureText (IDeviceContext dc, ReadOnlySpan<char> text, Font font, Size proposedSize) =>
			MeasureText (dc, text.ToString (), font, proposedSize);

		public static Size MeasureText (IDeviceContext dc, ReadOnlySpan<char> text, Font font, Size proposedSize, TextFormatFlags flags) =>
			MeasureText (dc, text.ToString (), font, proposedSize, flags);
	}
}
