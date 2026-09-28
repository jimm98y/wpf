// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// From .NET's System.Windows.Forms (dotnet/winforms), adapted to the port where noted.

using System.Collections.Specialized;
using System.Drawing;
using System.Drawing.Text;
using System.Runtime.CompilerServices;
using System.Windows.Forms.Layout;

namespace System.Windows.Forms.ButtonInternal;

internal abstract class ButtonBaseAdapter
{
	internal class ColorData(ColorOptions options)
	{
		public Color ButtonFace { get; set; }

		public Color ButtonShadow { get; set; }

		public Color ButtonShadowDark { get; set; }

		public Color ContrastButtonShadow { get; set; }

		public Color WindowText { get; set; }

		public Color WindowDisabled { get; set; }

		public Color Highlight { get; set; }

		public Color LowHighlight { get; set; }

		public Color LowButtonFace { get; set; }

		public Color WindowFrame { get; set; }

		public ColorOptions Options { get; set; } = options;
	}

	internal class ColorOptions
	{
		private readonly Color _backColor;

		private readonly Color _foreColor;

		private readonly IDeviceContext _deviceContext;

		public bool Enabled { get; set; }

		public bool HighContrast { get; }

		internal ColorOptions(IDeviceContext deviceContext, Color foreColor, Color backColor)
		{
			_deviceContext = deviceContext;
			_backColor = backColor;
			_foreColor = foreColor;
			HighContrast = SystemInformation.HighContrast;
		}

		internal static int Adjust255(float percentage, int value)
		{
			int num = (int)(percentage * (float)value);
			if (num <= 255)
			{
				return num;
			}
			return 255;
		}

		internal ColorData Calculate()
		{
			ColorData colorData = new ColorData(this)
			{
				ButtonFace = _backColor
			};
			if (_backColor == SystemColors.Control)
			{
				colorData.ButtonShadow = SystemColors.ControlDark;
				colorData.ButtonShadowDark = SystemColors.ControlDarkDark;
				colorData.Highlight = SystemColors.ControlLightLight;
			}
			else if (!HighContrast)
			{
				colorData.ButtonShadow = ControlPaint.Dark(_backColor);
				colorData.ButtonShadowDark = ControlPaint.DarkDark(_backColor);
				colorData.Highlight = ControlPaint.LightLight(_backColor);
			}
			else
			{
				colorData.ButtonShadow = ControlPaint.Dark(_backColor);
				colorData.ButtonShadowDark = ControlPaint.LightLight(_backColor);
				colorData.Highlight = ControlPaint.LightLight(_backColor);
			}
			colorData.WindowDisabled = (HighContrast ? SystemColors.GrayText : colorData.ButtonShadow);
			float percentage = 0.9f;
			if ((double)colorData.ButtonFace.GetBrightness() < 0.5)
			{
				percentage = 1.2f;
			}
			colorData.LowButtonFace = Color.FromArgb(Adjust255(percentage, colorData.ButtonFace.R), Adjust255(percentage, colorData.ButtonFace.G), Adjust255(percentage, colorData.ButtonFace.B));
			percentage = 0.9f;
			if ((double)colorData.Highlight.GetBrightness() < 0.5)
			{
				percentage = 1.2f;
			}
			colorData.LowHighlight = Color.FromArgb(Adjust255(percentage, colorData.Highlight.R), Adjust255(percentage, colorData.Highlight.G), Adjust255(percentage, colorData.Highlight.B));
			if (HighContrast && _backColor != SystemColors.Control)
			{
				colorData.Highlight = colorData.LowHighlight;
			}
			colorData.WindowFrame = _foreColor;
			colorData.ContrastButtonShadow = (((double)colorData.ButtonFace.GetBrightness() < 0.5) ? colorData.LowHighlight : colorData.ButtonShadow);
			if (!Enabled)
			{
				colorData.WindowText = colorData.WindowDisabled;
				if (HighContrast)
				{
					colorData.WindowFrame = colorData.WindowDisabled;
					colorData.ButtonShadow = colorData.WindowDisabled;
				}
			}
			else
			{
				colorData.WindowText = colorData.WindowFrame;
			}
			using DeviceContextHdcScope hdc = _deviceContext.ToHdcScope(ApplyGraphicsProperties.None);
			colorData.ButtonFace = hdc.FindNearestColor(colorData.ButtonFace);
			colorData.ButtonShadow = hdc.FindNearestColor(colorData.ButtonShadow);
			colorData.ButtonShadowDark = hdc.FindNearestColor(colorData.ButtonShadowDark);
			colorData.ContrastButtonShadow = hdc.FindNearestColor(colorData.ContrastButtonShadow);
			colorData.WindowText = hdc.FindNearestColor(colorData.WindowText);
			colorData.Highlight = hdc.FindNearestColor(colorData.Highlight);
			colorData.LowHighlight = hdc.FindNearestColor(colorData.LowHighlight);
			colorData.LowButtonFace = hdc.FindNearestColor(colorData.LowButtonFace);
			colorData.WindowFrame = hdc.FindNearestColor(colorData.WindowFrame);
			colorData.WindowDisabled = hdc.FindNearestColor(colorData.WindowDisabled);
			return colorData;
		}
	}

	internal class LayoutData
	{
		public Rectangle Client;

		public Rectangle Face;

		public Rectangle CheckArea;

		public Rectangle CheckBounds;

		public Rectangle TextBounds;

		public Rectangle Field;

		public Rectangle Focus;

		public Rectangle ImageBounds;

		public Point ImageStart;

		public LayoutOptions Options;

		internal LayoutData(LayoutOptions options)
		{
			Options = options;
		}
	}

	internal class LayoutOptions
	{
		private enum Composition
		{
			NoneCombined,
			CheckCombined,
			TextImageCombined,
			AllCombined
		}

		private static readonly int s_combineCheck = BitVector32.CreateMask();

		private static readonly int s_combineImageText = BitVector32.CreateMask(s_combineCheck);

		private bool _disableWordWrapping;

		public Rectangle Client;

		private static readonly TextImageRelation[] s_imageAlignToRelation = new TextImageRelation[11]
		{
			(TextImageRelation)5,
			TextImageRelation.ImageAboveText,
			(TextImageRelation)9,
			TextImageRelation.Overlay,
			TextImageRelation.ImageBeforeText,
			TextImageRelation.Overlay,
			TextImageRelation.TextBeforeImage,
			TextImageRelation.Overlay,
			(TextImageRelation)6,
			TextImageRelation.TextAboveImage,
			(TextImageRelation)10
		};

		public bool GrowBorderBy1PxWhenDefault { get; set; }

		public bool IsDefault { get; set; }

		public int BorderSize { get; set; }

		public int PaddingSize { get; set; }

		public bool MaxFocus { get; set; }

		public bool FocusOddEvenFixup { get; set; }

		public Font Font { get; set; }

		public string? Text { get; set; }

		public Size ImageSize { get; set; }

		public int CheckSize { get; set; }

		public int CheckPaddingSize { get; set; }

		public ContentAlignment CheckAlign { get; set; }

		public ContentAlignment ImageAlign { get; set; }

		public ContentAlignment TextAlign { get; set; }

		public TextImageRelation TextImageRelation { get; set; }

		public bool HintTextUp { get; set; }

		public bool TextOffset { get; set; }

		public bool ShadowedText { get; set; }

		public bool LayoutRTL { get; set; }

		public bool VerticalText { get; set; }

		public bool UseCompatibleTextRendering { get; set; }

		public bool DotNetOneButtonCompat { get; set; } = true;

		public TextFormatFlags GdiTextFormatFlags { get; set; } = TextFormatFlags.TextBoxControl | TextFormatFlags.WordBreak;

		public StringFormatFlags GdiPlusFormatFlags { get; set; }

		public StringTrimming GdiPlusTrimming { get; set; }

		public HotkeyPrefix GdiPlusHotkeyPrefix { get; set; }

		public StringAlignment GdiPlusAlignment { get; set; }

		public StringAlignment GdiPlusLineAlignment { get; set; }

		public StringFormat StringFormat
		{
			get
			{
				StringFormat stringFormat = new StringFormat
				{
					FormatFlags = GdiPlusFormatFlags,
					Trimming = GdiPlusTrimming,
					HotkeyPrefix = GdiPlusHotkeyPrefix,
					Alignment = GdiPlusAlignment,
					LineAlignment = GdiPlusLineAlignment
				};
				if (_disableWordWrapping)
				{
					stringFormat.FormatFlags |= StringFormatFlags.NoWrap;
				}
				return stringFormat;
			}
			set
			{
				GdiPlusFormatFlags = value.FormatFlags;
				GdiPlusTrimming = value.Trimming;
				GdiPlusHotkeyPrefix = value.HotkeyPrefix;
				GdiPlusAlignment = value.Alignment;
				GdiPlusLineAlignment = value.LineAlignment;
			}
		}

		public TextFormatFlags TextFormatFlags
		{
			get
			{
				if (!_disableWordWrapping)
				{
					return GdiTextFormatFlags;
				}
				return GdiTextFormatFlags & ~TextFormatFlags.WordBreak;
			}
		}

		public int TextImageInset { get; set; } = 2;

		public Padding Padding { get; set; }

		private int FullBorderSize
		{
			get
			{
				if (!OnePixExtraBorder)
				{
					return BorderSize;
				}
				return BorderSize++;
			}
		}

		private bool OnePixExtraBorder
		{
			get
			{
				if (GrowBorderBy1PxWhenDefault)
				{
					return IsDefault;
				}
				return false;
			}
		}

		private int FullCheckSize => CheckSize + CheckPaddingSize;

		private Size Compose(Size checkSize, Size imageSize, Size textSize)
		{
			Composition horizontalComposition = GetHorizontalComposition();
			return new Size(height: xCompose(GetVerticalComposition(), checkSize.Height, imageSize.Height, textSize.Height), width: xCompose(horizontalComposition, checkSize.Width, imageSize.Width, textSize.Width));
			static int xCompose(Composition composition, int num, int num2, int num3)
			{
				return composition switch
				{
					Composition.NoneCombined => num + num2 + num3, 
					Composition.CheckCombined => Math.Max(num, num2 + num3), 
					Composition.TextImageCombined => Math.Max(num2, num3) + num, 
					Composition.AllCombined => Math.Max(Math.Max(num, num2), num3), 
					_ => -7107, 
				};
			}
		}

		private Size Decompose(Size checkSize, Size imageSize, Size proposedSize)
		{
			Composition horizontalComposition = GetHorizontalComposition();
			return new Size(height: xDecompose(GetVerticalComposition(), checkSize.Height, imageSize.Height, proposedSize.Height), width: xDecompose(horizontalComposition, checkSize.Width, imageSize.Width, proposedSize.Width));
			static int xDecompose(Composition composition, int num2, int num3, int num)
			{
				return composition switch
				{
					Composition.NoneCombined => num - (num2 + num3), 
					Composition.CheckCombined => num - num3, 
					Composition.TextImageCombined => num - num2, 
					Composition.AllCombined => num, 
					_ => -7109, 
				};
			}
		}

		private Composition GetHorizontalComposition()
		{
			BitVector32 bitVector = new BitVector32
			{
				[s_combineCheck] = CheckAlign == ContentAlignment.MiddleCenter || !LayoutUtils.IsHorizontalAlignment(CheckAlign),
				[s_combineImageText] = !LayoutUtils.IsHorizontalRelation(TextImageRelation)
			};
			return (Composition)bitVector.Data;
		}

		internal Size GetPreferredSizeCore(Size proposedSize)
		{
			int num = BorderSize * 2 + PaddingSize * 2;
			if (GrowBorderBy1PxWhenDefault)
			{
				num += 2;
			}
			Size size = new Size(num, num);
			proposedSize -= size;
			int fullCheckSize = FullCheckSize;
			Size checkSize = ((fullCheckSize > 0) ? new Size(fullCheckSize + 1, fullCheckSize) : Size.Empty);
			Size size2 = new Size(TextImageInset * 2, TextImageInset * 2);
			Size imageSize = ((ImageSize != Size.Empty) ? (ImageSize + size2) : Size.Empty);
			proposedSize -= size2;
			proposedSize = Decompose(checkSize, imageSize, proposedSize);
			Size textSize = Size.Empty;
			if (!string.IsNullOrEmpty(Text))
			{
				try
				{
					_disableWordWrapping = true;
					textSize = GetTextSize(proposedSize) + size2;
				}
				finally
				{
					_disableWordWrapping = false;
				}
			}
			return Compose(checkSize, ImageSize, textSize) + size;
		}

		private Composition GetVerticalComposition()
		{
			BitVector32 bitVector = new BitVector32
			{
				[s_combineCheck] = CheckAlign == ContentAlignment.MiddleCenter || !LayoutUtils.IsVerticalAlignment(CheckAlign),
				[s_combineImageText] = !LayoutUtils.IsVerticalRelation(TextImageRelation)
			};
			return (Composition)bitVector.Data;
		}

		internal LayoutData Layout()
		{
			LayoutData layoutData = new LayoutData(this)
			{
				Client = Client
			};
			int fullBorderSize = FullBorderSize;
			layoutData.Face = Rectangle.Inflate(layoutData.Client, -fullBorderSize, -fullBorderSize);
			CalcCheckmarkRectangle(layoutData);
			LayoutTextAndImage(layoutData);
			if (MaxFocus)
			{
				layoutData.Focus = layoutData.Field;
				layoutData.Focus.Inflate(-1, -1);
				layoutData.Focus = LayoutUtils.InflateRect(layoutData.Focus, Padding);
			}
			else
			{
				Rectangle rectangle = new Rectangle(layoutData.TextBounds.X - 1, layoutData.TextBounds.Y - 1, layoutData.TextBounds.Width + 2, layoutData.TextBounds.Height + 3);
				layoutData.Focus = ((ImageSize != Size.Empty) ? Rectangle.Union(rectangle, layoutData.ImageBounds) : rectangle);
			}
			if (FocusOddEvenFixup)
			{
				if (layoutData.Focus.Height % 2 == 0)
				{
					layoutData.Focus.Y++;
					layoutData.Focus.Height--;
				}
				if (layoutData.Focus.Width % 2 == 0)
				{
					layoutData.Focus.X++;
					layoutData.Focus.Width--;
				}
			}
			return layoutData;
		}

		private TextImageRelation RtlTranslateRelation(TextImageRelation relation)
		{
			if (LayoutRTL)
			{
				switch (relation)
				{
				case TextImageRelation.ImageBeforeText:
					return TextImageRelation.TextBeforeImage;
				case TextImageRelation.TextBeforeImage:
					return TextImageRelation.ImageBeforeText;
				}
			}
			return relation;
		}

		internal ContentAlignment RtlTranslateContent(ContentAlignment align)
		{
			if (LayoutRTL)
			{
				ContentAlignment[][] array = new ContentAlignment[3][]
				{
					new ContentAlignment[2]
					{
						ContentAlignment.TopLeft,
						ContentAlignment.TopRight
					},
					new ContentAlignment[2]
					{
						ContentAlignment.MiddleLeft,
						ContentAlignment.MiddleRight
					},
					new ContentAlignment[2]
					{
						ContentAlignment.BottomLeft,
						ContentAlignment.BottomRight
					}
				};
				for (int i = 0; i < 3; i++)
				{
					if (array[i][0] == align)
					{
						return array[i][1];
					}
					if (array[i][1] == align)
					{
						return array[i][0];
					}
				}
			}
			return align;
		}

		private void CalcCheckmarkRectangle(LayoutData layout)
		{
			int fullCheckSize = FullCheckSize;
			layout.CheckBounds = new Rectangle(Client.X, Client.Y, fullCheckSize, fullCheckSize);
			ContentAlignment contentAlignment = RtlTranslateContent(CheckAlign);
			Rectangle rectangle = (layout.Field = Rectangle.Inflate(layout.Face, -PaddingSize, -PaddingSize));
			if (fullCheckSize > 0)
			{
				if ((contentAlignment & (ContentAlignment)0x444) != 0)
				{
					layout.CheckBounds.X = rectangle.X + rectangle.Width - layout.CheckBounds.Width;
				}
				else if ((contentAlignment & (ContentAlignment)0x222) != 0)
				{
					layout.CheckBounds.X = rectangle.X + (rectangle.Width - layout.CheckBounds.Width) / 2;
				}
				if ((contentAlignment & (ContentAlignment)0x700) != 0)
				{
					layout.CheckBounds.Y = rectangle.Y + rectangle.Height - layout.CheckBounds.Height;
				}
				else if ((contentAlignment & (ContentAlignment)7) != 0)
				{
					layout.CheckBounds.Y = rectangle.Y + 2;
				}
				else
				{
					layout.CheckBounds.Y = rectangle.Y + (rectangle.Height - layout.CheckBounds.Height) / 2;
				}
				switch (contentAlignment)
				{
				case ContentAlignment.TopLeft:
				case ContentAlignment.MiddleLeft:
				case ContentAlignment.BottomLeft:
					layout.CheckArea.X = rectangle.X;
					layout.CheckArea.Width = fullCheckSize + 1;
					layout.CheckArea.Y = rectangle.Y;
					layout.CheckArea.Height = rectangle.Height;
					layout.Field.X += fullCheckSize + 1;
					layout.Field.Width -= fullCheckSize + 1;
					break;
				case ContentAlignment.TopRight:
				case ContentAlignment.MiddleRight:
				case ContentAlignment.BottomRight:
					layout.CheckArea.X = rectangle.X + rectangle.Width - fullCheckSize;
					layout.CheckArea.Width = fullCheckSize + 1;
					layout.CheckArea.Y = rectangle.Y;
					layout.CheckArea.Height = rectangle.Height;
					layout.Field.Width -= fullCheckSize + 1;
					break;
				case ContentAlignment.TopCenter:
					layout.CheckArea.X = rectangle.X;
					layout.CheckArea.Width = rectangle.Width;
					layout.CheckArea.Y = rectangle.Y;
					layout.CheckArea.Height = fullCheckSize;
					layout.Field.Y += fullCheckSize;
					layout.Field.Height -= fullCheckSize;
					break;
				case ContentAlignment.BottomCenter:
					layout.CheckArea.X = rectangle.X;
					layout.CheckArea.Width = rectangle.Width;
					layout.CheckArea.Y = rectangle.Y + rectangle.Height - fullCheckSize;
					layout.CheckArea.Height = fullCheckSize;
					layout.Field.Height -= fullCheckSize;
					break;
				case ContentAlignment.MiddleCenter:
					layout.CheckArea = layout.CheckBounds;
					break;
				}
				layout.CheckBounds.Width -= CheckPaddingSize;
				layout.CheckBounds.Height -= CheckPaddingSize;
			}
		}

		private static TextImageRelation ImageAlignToRelation(ContentAlignment alignment)
		{
			return s_imageAlignToRelation[LayoutUtils.ContentAlignmentToIndex(alignment)];
		}

		private static TextImageRelation TextAlignToRelation(ContentAlignment alignment)
		{
			return LayoutUtils.GetOppositeTextImageRelation(ImageAlignToRelation(alignment));
		}

		internal void LayoutTextAndImage(LayoutData layout)
		{
			ContentAlignment contentAlignment = RtlTranslateContent(ImageAlign);
			ContentAlignment contentAlignment2 = RtlTranslateContent(TextAlign);
			TextImageRelation textImageRelation = RtlTranslateRelation(TextImageRelation);
			Rectangle rectangle = Rectangle.Inflate(layout.Field, -TextImageInset, -TextImageInset);
			if (OnePixExtraBorder)
			{
				rectangle.Inflate(1, 1);
			}
			if (ImageSize == Size.Empty || Text == null || Text.Length == 0 || textImageRelation == TextImageRelation.Overlay)
			{
				Size textSize = GetTextSize(rectangle.Size);
				Size alignThis = ImageSize;
				if (layout.Options.DotNetOneButtonCompat && ImageSize != Size.Empty)
				{
					alignThis = new Size(alignThis.Width + 1, alignThis.Height + 1);
				}
				layout.ImageBounds = LayoutUtils.Align(alignThis, rectangle, contentAlignment);
				layout.TextBounds = LayoutUtils.Align(textSize, rectangle, contentAlignment2);
			}
			else
			{
				Size proposedSize = LayoutUtils.SubAlignedRegion(rectangle.Size, ImageSize, textImageRelation);
				Size textSize2 = GetTextSize(proposedSize);
				Rectangle rectangle2 = rectangle;
				Size size = LayoutUtils.AddAlignedRegion(textSize2, ImageSize, textImageRelation);
				rectangle2.Size = LayoutUtils.UnionSizes(rectangle2.Size, size);
				Rectangle bounds = LayoutUtils.Align(size, rectangle2, ContentAlignment.MiddleCenter);
				bool flag = (ImageAlignToRelation(contentAlignment) & textImageRelation) != 0;
				bool flag2 = (TextAlignToRelation(contentAlignment2) & textImageRelation) != 0;
				if (flag)
				{
					LayoutUtils.SplitRegion(rectangle2, ImageSize, (AnchorStyles)textImageRelation, out layout.ImageBounds, out layout.TextBounds);
				}
				else if (flag2)
				{
					LayoutUtils.SplitRegion(rectangle2, textSize2, (AnchorStyles)LayoutUtils.GetOppositeTextImageRelation(textImageRelation), out layout.TextBounds, out layout.ImageBounds);
				}
				else
				{
					LayoutUtils.SplitRegion(bounds, ImageSize, (AnchorStyles)textImageRelation, out layout.ImageBounds, out layout.TextBounds);
					LayoutUtils.ExpandRegionsToFillBounds(rectangle2, (AnchorStyles)textImageRelation, ref layout.ImageBounds, ref layout.TextBounds);
				}
				layout.ImageBounds = LayoutUtils.Align(ImageSize, layout.ImageBounds, contentAlignment);
				layout.TextBounds = LayoutUtils.Align(textSize2, layout.TextBounds, contentAlignment2);
			}
			if ((textImageRelation == TextImageRelation.ImageBeforeText || textImageRelation == TextImageRelation.TextBeforeImage) ? true : false)
			{
				int num = Math.Min(layout.TextBounds.Bottom, layout.Field.Bottom);
				layout.TextBounds.Y = Math.Max(Math.Min(layout.TextBounds.Y, layout.Field.Y + (layout.Field.Height - layout.TextBounds.Height) / 2), layout.Field.Y);
				layout.TextBounds.Height = num - layout.TextBounds.Y;
			}
			if ((uint)(textImageRelation - 1) <= 1u)
			{
				int num2 = Math.Min(layout.TextBounds.Right, layout.Field.Right);
				layout.TextBounds.X = Math.Max(Math.Min(layout.TextBounds.X, layout.Field.X + (layout.Field.Width - layout.TextBounds.Width) / 2), layout.Field.X);
				layout.TextBounds.Width = num2 - layout.TextBounds.X;
			}
			if (textImageRelation == TextImageRelation.ImageBeforeText && layout.ImageBounds.Size.Width != 0)
			{
				layout.ImageBounds.Width = Math.Max(0, Math.Min(rectangle.Width - layout.TextBounds.Width, layout.ImageBounds.Width));
				layout.TextBounds.X = layout.ImageBounds.X + layout.ImageBounds.Width;
			}
			if (textImageRelation == TextImageRelation.ImageAboveText && layout.ImageBounds.Size.Height != 0)
			{
				layout.ImageBounds.Height = Math.Max(0, Math.Min(rectangle.Height - layout.TextBounds.Height, layout.ImageBounds.Height));
				layout.TextBounds.Y = layout.ImageBounds.Y + layout.ImageBounds.Height;
			}
			layout.TextBounds = Rectangle.Intersect(layout.TextBounds, layout.Field);
			if (HintTextUp)
			{
				layout.TextBounds.Y--;
			}
			if (TextOffset)
			{
				layout.TextBounds.Offset(1, 1);
			}
			if (layout.Options.DotNetOneButtonCompat)
			{
				layout.ImageStart = layout.ImageBounds.Location;
				layout.ImageBounds = Rectangle.Intersect(layout.ImageBounds, layout.Field);
			}
			else if (!Application.RenderWithVisualStyles)
			{
				layout.TextBounds.X++;
			}
			int num3;
			if (!UseCompatibleTextRendering)
			{
				num3 = Math.Min(layout.TextBounds.Bottom, rectangle.Bottom);
				layout.TextBounds.Y = Math.Max(layout.TextBounds.Y, rectangle.Y);
			}
			else
			{
				num3 = Math.Min(layout.TextBounds.Bottom, layout.Field.Bottom);
				layout.TextBounds.Y = Math.Max(layout.TextBounds.Y, layout.Field.Y);
			}
			layout.TextBounds.Height = num3 - layout.TextBounds.Y;
		}

		protected virtual Size GetTextSize(Size proposedSize)
		{
			proposedSize = LayoutUtils.FlipSizeIf(VerticalText, proposedSize);
			Size size = Size.Empty;
			if (UseCompatibleTextRendering)
			{
				using GdiCache.ScreenGraphicsScope screenGraphicsScope = GdiCache.GetScreenDCGraphics();
				using StringFormat stringFormat = StringFormat;
				size = Size.Ceiling(screenGraphicsScope.Graphics.MeasureString(Text, Font, new SizeF(proposedSize.Width, proposedSize.Height), stringFormat));
			}
			else if (!string.IsNullOrEmpty(Text))
			{
				size = TextRenderer.MeasureText(Text, Font, proposedSize, TextFormatFlags);
			}
			return LayoutUtils.FlipSizeIf(VerticalText, size);
		}
	}

	protected const int ButtonBorderSize = 4;

	protected ButtonBase Control { get; }

	internal ButtonBaseAdapter(ButtonBase control)
	{
		Control = control.OrThrowIfNull("control");
	}

	private protected static Color GetContrastingBorderColor(Color buttonBorderShadowColor)
	{
		return Color.FromArgb(buttonBorderShadowColor.A, (int)((float)(int)buttonBorderShadowColor.R * 0.8f), (int)((float)(int)buttonBorderShadowColor.G * 0.8f), (int)((float)(int)buttonBorderShadowColor.B * 0.8f));
	}

	internal void Paint(PaintEventArgs e)
	{
		if (Control.MouseIsDown)
		{
			PaintDown(e, CheckState.Unchecked);
		}
		else if (Control.MouseIsOver)
		{
			PaintOver(e, CheckState.Unchecked);
		}
		else
		{
			PaintUp(e, CheckState.Unchecked);
		}
	}

	internal virtual Size GetPreferredSizeCore(Size proposedSize)
	{
		LayoutOptions layoutOptions = null;
		using (ScreenDcCache.ScreenDcScope scope = GdiCache.GetScreenHdc())
		{
			using PaintEventArgs e = new PaintEventArgs(scope, default);
			layoutOptions = Layout(e);
		}
		return layoutOptions.GetPreferredSizeCore(proposedSize);
	}

	protected abstract LayoutOptions Layout(PaintEventArgs e);

	internal abstract void PaintUp(PaintEventArgs e, CheckState state);

	internal abstract void PaintDown(PaintEventArgs e, CheckState state);

	internal abstract void PaintOver(PaintEventArgs e, CheckState state);

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	protected bool IsHighContrastHighlighted()
	{
		if (SystemInformation.HighContrast && Application.RenderWithVisualStyles)
		{
			if (!Control.Focused && !Control.MouseIsOver)
			{
				if (Control.IsDefault)
				{
					return Control.Enabled;
				}
				return false;
			}
			return true;
		}
		return false;
	}

	internal static Brush CreateDitherBrush(Color color1, Color color2)
	{
		using Bitmap bitmap = new Bitmap(2, 2);
		bitmap.SetPixel(0, 0, color1);
		bitmap.SetPixel(0, 1, color2);
		bitmap.SetPixel(1, 1, color1);
		bitmap.SetPixel(1, 0, color2);
		return new TextureBrush(bitmap);
	}

	internal virtual StringFormat CreateStringFormat()
	{
		return ControlPaint.CreateStringFormat(Control, Control.TextAlign, Control.ShowToolTip, Control.UseMnemonic);
	}

	internal virtual TextFormatFlags CreateTextFormatFlags()
	{
		return ControlPaint.CreateTextFormatFlags(Control, Control.TextAlign, Control.ShowToolTip, Control.UseMnemonic);
	}

	internal static void DrawDitheredFill(Graphics g, Color color1, Color color2, Rectangle bounds)
	{
		using Brush brush = CreateDitherBrush(color1, color2);
		g.FillRectangle(brush, bounds);
	}

	protected void Draw3DBorder(IDeviceContext deviceContext, Rectangle bounds, ColorData colors, bool raised)
	{
		if (Control.BackColor != SystemColors.Control && SystemInformation.HighContrast)
		{
			if (raised)
			{
				Draw3DBorderHighContrastRaised(deviceContext, ref bounds, colors);
			}
			else
			{
				ControlPaint.DrawBorderSimple(deviceContext, bounds, ControlPaint.Dark(Control.BackColor));
			}
		}
		else if (raised)
		{
			Draw3DBorderRaised(deviceContext, ref bounds, colors);
		}
		else
		{
			Draw3DBorderNormal(deviceContext, ref bounds, colors);
		}
	}

	private void Draw3DBorderHighContrastRaised(IDeviceContext deviceContext, ref Rectangle bounds, ColorData colors)
	{
		bool flag = colors.ButtonFace.ToKnownColor() == SystemColors.Control.ToKnownColor();
		bool flag2 = !Control.Enabled && SystemInformation.HighContrast;
		using DeviceContextHdcScope hdc = deviceContext.ToHdcScope();
		Point point = new Point(bounds.X + bounds.Width - 1, bounds.Y);
		Point point2 = new Point(bounds.X, bounds.Y);
		Point point3 = new Point(bounds.X, bounds.Y + bounds.Height - 1);
		Point point4 = new Point(bounds.X + bounds.Width - 1, bounds.Y + bounds.Height - 1);
		Color color;
		if (flag2)
		{
			color = colors.WindowDisabled;
		}
		else
		{
			color = (flag ? SystemColors.ControlLightLight : colors.Highlight);
		}
		using CreatePenScope scope = new CreatePenScope(color);
		hdc.DrawLine(scope, point, point2);
		hdc.DrawLine(scope, point2, point3);
		Color color2;
		if (flag2)
		{
			color2 = colors.WindowDisabled;
		}
		else
		{
			color2 = (flag ? SystemColors.ControlDarkDark : colors.ButtonShadowDark);
		}
		using CreatePenScope scope2 = new CreatePenScope(color2);
		point.Offset(0, -1);
		hdc.DrawLine(scope2, point3, point4);
		hdc.DrawLine(scope2, point4, point);
		Color color3;
		if (flag)
		{
			color3 = (SystemInformation.HighContrast ? SystemColors.ControlLight : SystemColors.Control);
		}
		else
		{
			color3 = (SystemInformation.HighContrast ? colors.Highlight : colors.ButtonFace);
		}
		using CreatePenScope scope3 = new CreatePenScope(color3);
		point.Offset(-1, 2);
		point2.Offset(1, 1);
		point3.Offset(1, -1);
		point4.Offset(-1, -1);
		hdc.DrawLine(scope3, point, point2);
		hdc.DrawLine(scope3, point2, point3);
		Color color4;
		if (flag2)
		{
			color4 = colors.WindowDisabled;
		}
		else
		{
			color4 = (flag ? SystemColors.ControlDark : colors.ButtonShadow);
		}
		using CreatePenScope scope4 = new CreatePenScope(color4);
		point.Offset(0, -1);
		hdc.DrawLine(scope4, point3, point4);
		hdc.DrawLine(scope4, point4, point);
	}

	private static void Draw3DBorderNormal(IDeviceContext deviceContext, ref Rectangle bounds, ColorData colors)
	{
		using DeviceContextHdcScope hdc = deviceContext.ToHdcScope();
		Point point = new Point(bounds.X + bounds.Width - 1, bounds.Y);
		Point point2 = new Point(bounds.X, bounds.Y);
		Point point3 = new Point(bounds.X, bounds.Y + bounds.Height - 1);
		Point point4 = new Point(bounds.X + bounds.Width - 1, bounds.Y + bounds.Height - 1);
		using CreatePenScope scope = new CreatePenScope(colors.ButtonShadowDark);
		hdc.DrawLine(scope, point, point2);
		hdc.DrawLine(scope, point2, point3);
		using CreatePenScope scope2 = new CreatePenScope(colors.Highlight);
		point.Offset(0, -1);
		hdc.DrawLine(scope2, point3, point4);
		hdc.DrawLine(scope2, point4, point);
		using CreatePenScope scope3 = new CreatePenScope(colors.ButtonFace);
		point.Offset(-1, 2);
		point2.Offset(1, 1);
		point3.Offset(1, -1);
		point4.Offset(-1, -1);
		hdc.DrawLine(scope3, point, point2);
		hdc.DrawLine(scope3, point2, point3);
		using CreatePenScope scope4 = new CreatePenScope((colors.ButtonFace.ToKnownColor() == SystemColors.Control.ToKnownColor()) ? SystemColors.ControlLight : colors.ButtonFace);
		point.Offset(0, -1);
		hdc.DrawLine(scope4, point3, point4);
		hdc.DrawLine(scope4, point4, point);
	}

	private void Draw3DBorderRaised(IDeviceContext deviceContext, ref Rectangle bounds, ColorData colors)
	{
		bool flag = colors.ButtonFace.ToKnownColor() == SystemColors.Control.ToKnownColor();
		bool flag2 = !Control.Enabled && SystemInformation.HighContrast;
		using DeviceContextHdcScope hdc = deviceContext.ToHdcScope();
		Point point = new Point(bounds.X + bounds.Width - 1, bounds.Y);
		Point point2 = new Point(bounds.X, bounds.Y);
		Point point3 = new Point(bounds.X, bounds.Y + bounds.Height - 1);
		Point point4 = new Point(bounds.X + bounds.Width - 1, bounds.Y + bounds.Height - 1);
		Color color;
		if (flag2)
		{
			color = colors.WindowDisabled;
		}
		else
		{
			color = (flag ? SystemColors.ControlLightLight : colors.Highlight);
		}
		using CreatePenScope scope = new CreatePenScope(color);
		hdc.DrawLine(scope, point, point2);
		hdc.DrawLine(scope, point2, point3);
		Color color2;
		if (flag2)
		{
			color2 = colors.WindowDisabled;
		}
		else
		{
			color2 = (flag ? SystemColors.ControlDarkDark : colors.ButtonShadowDark);
		}
		using CreatePenScope scope2 = new CreatePenScope(color2);
		point.Offset(0, -1);
		hdc.DrawLine(scope2, point3, point4);
		hdc.DrawLine(scope2, point4, point);
		point.Offset(-1, 2);
		point2.Offset(1, 1);
		point3.Offset(1, -1);
		point4.Offset(-1, -1);
		Color color3;
		if (flag)
		{
			color3 = (SystemInformation.HighContrast ? SystemColors.ControlLight : SystemColors.Control);
		}
		else
		{
			color3 = colors.ButtonFace;
		}
		using CreatePenScope scope3 = new CreatePenScope(color3);
		hdc.DrawLine(scope3, point, point2);
		hdc.DrawLine(scope3, point2, point3);
		Color color4;
		if (flag2)
		{
			color4 = colors.WindowDisabled;
		}
		else
		{
			color4 = (flag ? SystemColors.ControlDark : colors.ButtonShadow);
		}
		using CreatePenScope scope4 = new CreatePenScope(color4);
		point.Offset(0, -1);
		hdc.DrawLine(scope4, point3, point4);
		hdc.DrawLine(scope4, point4, point);
	}

	protected internal static void Draw3DLiteBorder(IDeviceContext deviceContext, Rectangle r, ColorData colors, bool up)
	{
		using DeviceContextHdcScope hdc = deviceContext.ToHdcScope();
		Point point = new Point(r.Right - 1, r.Top);
		Point point2 = new Point(r.Left, r.Top);
		Point point3 = new Point(r.Left, r.Bottom - 1);
		Point point4 = new Point(r.Right - 1, r.Bottom - 1);
		Color contrastingBorderColor = GetContrastingBorderColor(colors.ButtonShadow);
		using CreatePenScope scope = new CreatePenScope(up ? colors.Highlight : contrastingBorderColor);
		hdc.DrawLine(scope, point, point2);
		hdc.DrawLine(scope, point2, point3);
		using CreatePenScope scope2 = new CreatePenScope(up ? contrastingBorderColor : colors.Highlight);
		point.Offset(0, -1);
		hdc.DrawLine(scope2, point3, point4);
		hdc.DrawLine(scope2, point4, point);
	}

	internal static void DrawFlatBorderWithSize(PaintEventArgs e, Rectangle bounds, Color color, int size)
	{
		size = Math.Min(size, Math.Min(bounds.Width, bounds.Height));
		Rectangle rectangle = new Rectangle(bounds.X, bounds.Y, size, bounds.Height);
		Rectangle rectangle2 = new Rectangle(bounds.X + bounds.Width - size, bounds.Y, size, bounds.Height);
		Rectangle rectangle3 = new Rectangle(bounds.X + size, bounds.Y, bounds.Width - size * 2, size);
		Rectangle rectangle4 = new Rectangle(bounds.X + size, bounds.Y + bounds.Height - size, bounds.Width - size * 2, size);
		if (color.HasTransparency())
		{
			Graphics graphicsInternal = e.GraphicsInternal;
			SolidBrushScope scope = color.GetCachedSolidBrushScope();
			try
			{
				graphicsInternal.FillRectangle((SolidBrush)scope, rectangle);
				graphicsInternal.FillRectangle((SolidBrush)scope, rectangle2);
				graphicsInternal.FillRectangle((SolidBrush)scope, rectangle3);
				graphicsInternal.FillRectangle((SolidBrush)scope, rectangle4);
				return;
			}
			finally
			{
				scope.Dispose();
			}
		}
		using DeviceContextHdcScope hdc = new DeviceContextHdcScope(e);
		using CreateBrushScope scope2 = new CreateBrushScope(color);
		hdc.FillRectangle(rectangle, scope2);
		hdc.FillRectangle(rectangle2, scope2);
		hdc.FillRectangle(rectangle3, scope2);
		hdc.FillRectangle(rectangle4, scope2);
	}

	internal static void DrawFlatFocus(IDeviceContext deviceContext, Rectangle r, Color color)
	{
		using DeviceContextHdcScope hdc = deviceContext.ToHdcScope();
		using CreatePenScope scope = new CreatePenScope(color);
		hdc.DrawRectangle(r, scope);
	}

	private void DrawFocus(Graphics g, Rectangle r)
	{
		if (Control.Focused && Control.ShowFocusCues)
		{
			ControlPaint.DrawFocusRectangle(g, r, Control.ForeColor, Control.BackColor);
		}
	}

	internal virtual void DrawImageCore(Graphics graphics, Image image, Rectangle imageBounds, Point imageStart, LayoutData layout)
	{
		Region clip = graphics.Clip;
		if (!layout.Options.DotNetOneButtonCompat)
		{
			Rectangle rect = new Rectangle(4, 4, Control.Width - 8, Control.Height - 8);
			Region region = clip.Clone();
			region.Intersect(rect);
			region.Intersect(imageBounds);
			graphics.Clip = region;
		}
		else
		{
			imageBounds.Width++;
			imageBounds.Height++;
			imageBounds.X = imageStart.X + 1;
			imageBounds.Y = imageStart.Y + 1;
		}
		try
		{
			if (!Control.Enabled)
			{
				ControlPaint.DrawImageDisabled(graphics, image, imageBounds, unscaledImage: true);
			}
			else
			{
				graphics.DrawImage(image, imageBounds.X, imageBounds.Y, image.Width, image.Height);
			}
		}
		finally
		{
			if (!layout.Options.DotNetOneButtonCompat)
			{
				graphics.Clip = clip;
			}
		}
	}

	internal static void DrawDefaultBorder(IDeviceContext deviceContext, Rectangle r, Color color, bool isDefault)
	{
		if (!isDefault)
		{
			return;
		}
		r.Inflate(1, 1);
		if (color.HasTransparency())
		{
			Graphics graphics = deviceContext.TryGetGraphics(create: true);
			if (graphics != null)
			{
				PenScope scope = color.GetCachedPenScope();
				try
				{
					graphics.DrawRectangle(scope, r.X, r.Y, r.Width - 1, r.Height - 1);
					return;
				}
				finally
				{
					scope.Dispose();
				}
			}
		}
		using CreatePenScope scope2 = new CreatePenScope(color);
		using DeviceContextHdcScope hdc = deviceContext.ToHdcScope();
		hdc.DrawRectangle(r, scope2);
	}

	private void DrawText(PaintEventArgs e, LayoutData layout, Color color, ColorData colors)
	{
		Rectangle textBounds = layout.TextBounds;
		bool shadowedText = layout.Options.ShadowedText;
		if (Control.UseCompatibleTextRendering)
		{
			Graphics graphicsInternal = e.GraphicsInternal;
			using StringFormat format = CreateStringFormat();
			if ((Control.TextAlign & (ContentAlignment)0x222) == 0)
			{
				textBounds.X--;
			}
			textBounds.Width++;
			if (shadowedText && !Control.Enabled && !colors.Options.HighContrast)
			{
				SolidBrushScope scope = colors.Highlight.GetCachedSolidBrushScope();
				try
				{
					textBounds.Offset(1, 1);
					graphicsInternal.DrawString(Control.Text, Control.Font, (SolidBrush)scope, textBounds, format);
					textBounds.Offset(-1, -1);
					SolidBrushScope scope2 = colors.ButtonShadow.GetCachedSolidBrushScope();
					try
					{
						graphicsInternal.DrawString(Control.Text, Control.Font, (SolidBrush)scope2, textBounds, format);
						return;
					}
					finally
					{
						scope2.Dispose();
					}
				}
				finally
				{
					scope.Dispose();
				}
			}
			SolidBrushScope scope3 = color.GetCachedSolidBrushScope();
			try
			{
				graphicsInternal.DrawString(Control.Text, Control.Font, (SolidBrush)scope3, textBounds, format);
				return;
			}
			finally
			{
				scope3.Dispose();
			}
		}
		TextFormatFlags flags = CreateTextFormatFlags();
		if (shadowedText && !Control.Enabled && !colors.Options.HighContrast)
		{
			if (Application.RenderWithVisualStyles)
			{
				TextRenderer.DrawText(e, Control.Text, Control.Font, textBounds, colors.ButtonShadow, flags);
				return;
			}
			textBounds.Offset(1, 1);
			TextRenderer.DrawText(e, Control.Text, Control.Font, textBounds, colors.Highlight, flags);
			textBounds.Offset(-1, -1);
			TextRenderer.DrawText(e, Control.Text, Control.Font, textBounds, colors.ButtonShadow, flags);
		}
		else
		{
			TextRenderer.DrawText(e, Control.Text, Control.Font, textBounds, color, flags);
		}
	}

	internal void PaintButtonBackground(PaintEventArgs e, Rectangle bounds, Brush? background)
	{
		if (background == null)
		{
			Control.PaintBackground(e, bounds);
		}
		else
		{
			e.GraphicsInternal.FillRectangle(background, bounds);
		}
	}

	internal void PaintField(PaintEventArgs e, LayoutData layout, ColorData colors, Color foreColor, bool drawFocus)
	{
		Rectangle focus = layout.Focus;
		DrawText(e, layout, foreColor, colors);
		if (drawFocus)
		{
			DrawFocus(e.GraphicsInternal, focus);
		}
	}

	internal void PaintImage(PaintEventArgs e, LayoutData layout)
	{
		if (Control.Image != null)
		{
			DrawImageCore(e.GraphicsInternal, Control.Image, layout.ImageBounds, layout.ImageStart, layout);
		}
	}

	internal static LayoutOptions CommonLayout(Rectangle clientRectangle, Padding padding, bool isDefault, Font font, string text, bool enabled, ContentAlignment textAlign, RightToLeft rtl)
	{
		return new LayoutOptions
		{
			Client = LayoutUtils.DeflateRect(clientRectangle, padding),
			Padding = padding,
			GrowBorderBy1PxWhenDefault = true,
			IsDefault = isDefault,
			BorderSize = 2,
			PaddingSize = 0,
			MaxFocus = true,
			FocusOddEvenFixup = false,
			Font = font,
			Text = text,
			ImageSize = Size.Empty,
			CheckSize = 0,
			CheckPaddingSize = 0,
			CheckAlign = ContentAlignment.TopLeft,
			ImageAlign = ContentAlignment.MiddleCenter,
			TextAlign = textAlign,
			HintTextUp = false,
			ShadowedText = !enabled,
			LayoutRTL = (rtl == RightToLeft.Yes),
			TextImageRelation = TextImageRelation.Overlay,
			UseCompatibleTextRendering = false
		};
	}

	internal virtual LayoutOptions CommonLayout()
	{
		LayoutOptions layoutOptions = new LayoutOptions
		{
			Client = LayoutUtils.DeflateRect(Control.ClientRectangle, Control.Padding),
			Padding = Control.Padding,
			GrowBorderBy1PxWhenDefault = true,
			IsDefault = Control.IsDefault,
			BorderSize = 2,
			PaddingSize = 0,
			MaxFocus = true,
			FocusOddEvenFixup = false,
			Font = Control.Font,
			Text = Control.Text,
			ImageSize = ((Control.Image == null) ? Size.Empty : Control.Image.Size),
			CheckSize = 0,
			CheckPaddingSize = 0,
			CheckAlign = ContentAlignment.TopLeft,
			ImageAlign = Control.ImageAlign,
			TextAlign = Control.TextAlign,
			HintTextUp = false,
			ShadowedText = !Control.Enabled,
			LayoutRTL = (Control.RightToLeft == RightToLeft.Yes),
			TextImageRelation = Control.TextImageRelation,
			UseCompatibleTextRendering = Control.UseCompatibleTextRendering
		};
		if (Control.FlatStyle != FlatStyle.System)
		{
			if (layoutOptions.UseCompatibleTextRendering)
			{
				using StringFormat stringFormat = Control.CreateStringFormat();
				layoutOptions.StringFormat = stringFormat;
			}
			else
			{
				layoutOptions.GdiTextFormatFlags = Control.CreateTextFormatFlags();
			}
		}
		return layoutOptions;
	}

	private static ColorOptions CommonRender(IDeviceContext deviceContext, Color foreColor, Color backColor, bool enabled)
	{
		return new ColorOptions(deviceContext, foreColor, backColor)
		{
			Enabled = enabled
		};
	}

	private ColorOptions CommonRender(IDeviceContext deviceContext)
	{
		return new ColorOptions(deviceContext, Control.ForeColor, Control.BackColor)
		{
			Enabled = Control.Enabled
		};
	}

	protected ColorOptions PaintRender(IDeviceContext deviceContext)
	{
		return CommonRender(deviceContext);
	}

	internal static ColorOptions PaintFlatRender(Graphics g, Color foreColor, Color backColor, bool enabled)
	{
		return CommonRender(g, foreColor, backColor, enabled);
	}

	protected ColorOptions PaintFlatRender(IDeviceContext deviceContext)
	{
		return CommonRender(deviceContext);
	}

	internal static ColorOptions PaintPopupRender(Graphics g, Color foreColor, Color backColor, bool enabled)
	{
		return CommonRender(g, foreColor, backColor, enabled);
	}

	protected ColorOptions PaintPopupRender(IDeviceContext deviceContext)
	{
		return CommonRender(deviceContext);
	}
}
