// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// From .NET's System.Windows.Forms (dotnet/winforms), adapted to the port where noted.

// .NET's LayoutUtils (System.Windows.Forms.Layout), which its painting and layout code leans on.

using System.Collections;
using System.Drawing;

namespace System.Windows.Forms.Layout;

internal static class LayoutUtils
{
	public sealed class MeasureTextCache
	{
		private const int MaxCacheSize = 6;

		private int _nextCacheEntry = -1;

		private PreferredSizeCache[]? _sizeCacheList;

		private Size _unconstrainedPreferredSize = s_invalidSize;

		public Size GetTextSize(string? text, Font? font, Size proposedConstraints, TextFormatFlags flags)
		{
			if (!TextRequiresWordBreak(text, font, proposedConstraints, flags))
			{
				return _unconstrainedPreferredSize;
			}
			if (_sizeCacheList == null)
			{
				_sizeCacheList = new PreferredSizeCache[6];
			}
			PreferredSizeCache[] sizeCacheList = _sizeCacheList;
			for (int i = 0; i < sizeCacheList.Length; i++)
			{
				PreferredSizeCache preferredSizeCache = sizeCacheList[i];
				if (preferredSizeCache.ConstrainingSize == proposedConstraints)
				{
					return preferredSizeCache.PreferredSize;
				}
				if (preferredSizeCache.ConstrainingSize.Width == proposedConstraints.Width && preferredSizeCache.PreferredSize.Height <= proposedConstraints.Height)
				{
					return preferredSizeCache.PreferredSize;
				}
			}
			Size size = TextRenderer.MeasureText(text, font, proposedConstraints, flags);
			_nextCacheEntry = (_nextCacheEntry + 1) % 6;
			_sizeCacheList[_nextCacheEntry] = new PreferredSizeCache(proposedConstraints, size);
			return size;
		}

		public void InvalidateCache()
		{
			_unconstrainedPreferredSize = s_invalidSize;
			_sizeCacheList = null;
		}

		public bool TextRequiresWordBreak(string? text, Font? font, Size size, TextFormatFlags flags)
		{
			return GetUnconstrainedSize(text, font, flags).Width > size.Width;
		}

		private Size GetUnconstrainedSize(string? text, Font? font, TextFormatFlags flags)
		{
			if (_unconstrainedPreferredSize == s_invalidSize)
			{
				flags &= ~TextFormatFlags.WordBreak;
				_unconstrainedPreferredSize = TextRenderer.MeasureText(text, font, s_maxSize, flags);
			}
			return _unconstrainedPreferredSize;
		}
	}

	private struct PreferredSizeCache(Size constrainingSize, Size preferredSize)
	{
		public Size ConstrainingSize = constrainingSize;

		public Size PreferredSize = preferredSize;
	}

	public static readonly Size s_maxSize = new Size(int.MaxValue, int.MaxValue);

	public static readonly Size s_invalidSize = new Size(int.MinValue, int.MinValue);

	public static readonly Rectangle s_maxRectangle = new Rectangle(0, 0, int.MaxValue, int.MaxValue);

	public const ContentAlignment AnyTop = (ContentAlignment)7;

	public const ContentAlignment AnyBottom = (ContentAlignment)1792;

	public const ContentAlignment AnyLeft = (ContentAlignment)273;

	public const ContentAlignment AnyRight = (ContentAlignment)1092;

	public const ContentAlignment AnyCenter = (ContentAlignment)546;

	public const ContentAlignment AnyMiddle = (ContentAlignment)112;

	public const AnchorStyles HorizontalAnchorStyles = AnchorStyles.Left | AnchorStyles.Right;

	public const AnchorStyles VerticalAnchorStyles = AnchorStyles.Top | AnchorStyles.Bottom;

	private static readonly AnchorStyles[] s_dockingToAnchor = new AnchorStyles[6]
	{
		AnchorStyles.Top | AnchorStyles.Left,
		AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
		AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right,
		AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left,
		AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Right,
		AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right
	};

	public const string TestString = "j^";

	public static Size OldGetLargestStringSizeInCollection(Font? font, ICollection? objects)
	{
		Size empty = Size.Empty;
		if (objects != null)
		{
			foreach (object @object in objects)
			{
				Size size = TextRenderer.MeasureText(@object.ToString(), font, new Size(32767, 32767), TextFormatFlags.SingleLine);
				empty.Width = Math.Max(empty.Width, size.Width);
				empty.Height = Math.Max(empty.Height, size.Height);
			}
		}
		return empty;
	}

	public static int ContentAlignmentToIndex(ContentAlignment alignment)
	{
		int num = xContentAlignmentToIndex((int)(alignment & (ContentAlignment)0xF));
		int num2 = xContentAlignmentToIndex(((int)alignment >> 4) & 0xF);
		int num3 = xContentAlignmentToIndex(((int)alignment >> 8) & 0xF);
		return (((num2 != 0) ? 4 : 0) | ((num3 != 0) ? 8 : 0) | num | num2 | num3) - 1;
	}

	private static byte xContentAlignmentToIndex(int threeBitFlag)
	{
		if (threeBitFlag != 4)
		{
			return (byte)threeBitFlag;
		}
		return 3;
	}

	public static Size ConvertZeroToUnbounded(Size size)
	{
		if (size.Width == 0)
		{
			size.Width = int.MaxValue;
		}
		if (size.Height == 0)
		{
			size.Height = int.MaxValue;
		}
		return size;
	}

	public static Padding ClampNegativePaddingToZero(Padding padding)
	{
		if (padding.All < 0)
		{
			padding.Left = Math.Max(0, padding.Left);
			padding.Top = Math.Max(0, padding.Top);
			padding.Right = Math.Max(0, padding.Right);
			padding.Bottom = Math.Max(0, padding.Bottom);
		}
		return padding;
	}

	private static AnchorStyles GetOppositeAnchor(AnchorStyles anchor)
	{
		AnchorStyles anchorStyles = AnchorStyles.None;
		if (anchor == AnchorStyles.None)
		{
			return anchorStyles;
		}
		for (int num = 1; num <= 8; num <<= 1)
		{
			switch ((AnchorStyles)((uint)anchor & (uint)num))
			{
			case AnchorStyles.Left:
				anchorStyles |= AnchorStyles.Right;
				break;
			case AnchorStyles.Top:
				anchorStyles |= AnchorStyles.Bottom;
				break;
			case AnchorStyles.Right:
				anchorStyles |= AnchorStyles.Left;
				break;
			case AnchorStyles.Bottom:
				anchorStyles |= AnchorStyles.Top;
				break;
			}
		}
		return anchorStyles;
	}

	public static TextImageRelation GetOppositeTextImageRelation(TextImageRelation relation)
	{
		return (TextImageRelation)GetOppositeAnchor((AnchorStyles)relation);
	}

	public static Size UnionSizes(Size a, Size b)
	{
		return new Size(Math.Max(a.Width, b.Width), Math.Max(a.Height, b.Height));
	}

	public static Size IntersectSizes(Size a, Size b)
	{
		return new Size(Math.Min(a.Width, b.Width), Math.Min(a.Height, b.Height));
	}

	public static bool IsIntersectHorizontally(Rectangle rect1, Rectangle rect2)
	{
		if (!rect1.IntersectsWith(rect2))
		{
			return false;
		}
		if (rect1.X <= rect2.X && rect1.X + rect1.Width >= rect2.X + rect2.Width)
		{
			return true;
		}
		if (rect2.X <= rect1.X && rect2.X + rect2.Width >= rect1.X + rect1.Width)
		{
			return true;
		}
		return false;
	}

	public static bool IsIntersectVertically(Rectangle rect1, Rectangle rect2)
	{
		if (!rect1.IntersectsWith(rect2))
		{
			return false;
		}
		if (rect1.Y <= rect2.Y && rect1.Y + rect1.Width >= rect2.Y + rect2.Width)
		{
			return true;
		}
		if (rect2.Y <= rect1.Y && rect2.Y + rect2.Width >= rect1.Y + rect1.Width)
		{
			return true;
		}
		return false;
	}

	internal static AnchorStyles GetUnifiedAnchor(IArrangedElement element)
	{
		// .NET reads these through DefaultLayout's property store; the port keeps them on the Control.
		DockStyle dock = element is Control c ? c.Dock : DockStyle.None;
		if (dock != DockStyle.None)
		{
			return s_dockingToAnchor[(int)dock];
		}
		return element is Control a ? a.Anchor : AnchorStyles.Top | AnchorStyles.Left;
	}

	public static Rectangle AlignAndStretch(Size fitThis, Rectangle withinThis, AnchorStyles anchorStyles)
	{
		return Align(Stretch(fitThis, withinThis.Size, anchorStyles), withinThis, anchorStyles);
	}

	public static Rectangle Align(Size alignThis, Rectangle withinThis, AnchorStyles anchorStyles)
	{
		return VAlign(alignThis, HAlign(alignThis, withinThis, anchorStyles), anchorStyles);
	}

	public static Rectangle Align(Size alignThis, Rectangle withinThis, ContentAlignment align)
	{
		return VAlign(alignThis, HAlign(alignThis, withinThis, align), align);
	}

	public static Rectangle HAlign(Size alignThis, Rectangle withinThis, AnchorStyles anchorStyles)
	{
		if ((anchorStyles & AnchorStyles.Right) != 0)
		{
			withinThis.X += withinThis.Width - alignThis.Width;
		}
		else if (anchorStyles == AnchorStyles.None || (anchorStyles & (AnchorStyles.Left | AnchorStyles.Right)) == 0)
		{
			withinThis.X += (withinThis.Width - alignThis.Width) / 2;
		}
		withinThis.Width = alignThis.Width;
		return withinThis;
	}

	private static Rectangle HAlign(Size alignThis, Rectangle withinThis, ContentAlignment align)
	{
		if ((align & (ContentAlignment)0x444) != 0)
		{
			withinThis.X += withinThis.Width - alignThis.Width;
		}
		else if ((align & (ContentAlignment)0x222) != 0)
		{
			withinThis.X += (withinThis.Width - alignThis.Width) / 2;
		}
		withinThis.Width = alignThis.Width;
		return withinThis;
	}

	public static Rectangle VAlign(Size alignThis, Rectangle withinThis, AnchorStyles anchorStyles)
	{
		if ((anchorStyles & AnchorStyles.Bottom) != 0)
		{
			withinThis.Y += withinThis.Height - alignThis.Height;
		}
		else if (anchorStyles == AnchorStyles.None || (anchorStyles & (AnchorStyles.Top | AnchorStyles.Bottom)) == 0)
		{
			withinThis.Y += (withinThis.Height - alignThis.Height) / 2;
		}
		withinThis.Height = alignThis.Height;
		return withinThis;
	}

	public static Rectangle VAlign(Size alignThis, Rectangle withinThis, ContentAlignment align)
	{
		if ((align & (ContentAlignment)0x700) != 0)
		{
			withinThis.Y += withinThis.Height - alignThis.Height;
		}
		else if ((align & (ContentAlignment)0x70) != 0)
		{
			withinThis.Y += (withinThis.Height - alignThis.Height) / 2;
		}
		withinThis.Height = alignThis.Height;
		return withinThis;
	}

	public static Size Stretch(Size stretchThis, Size withinThis, AnchorStyles anchorStyles)
	{
		Size result = new Size(((anchorStyles & (AnchorStyles.Left | AnchorStyles.Right)) == (AnchorStyles.Left | AnchorStyles.Right)) ? withinThis.Width : stretchThis.Width, ((anchorStyles & (AnchorStyles.Top | AnchorStyles.Bottom)) == (AnchorStyles.Top | AnchorStyles.Bottom)) ? withinThis.Height : stretchThis.Height);
		if (result.Width > withinThis.Width)
		{
			result.Width = withinThis.Width;
		}
		if (result.Height > withinThis.Height)
		{
			result.Height = withinThis.Height;
		}
		return result;
	}

	public static Rectangle InflateRect(Rectangle rect, Padding padding)
	{
		rect.X -= padding.Left;
		rect.Y -= padding.Top;
		rect.Width += padding.Horizontal;
		rect.Height += padding.Vertical;
		return rect;
	}

	public static Rectangle DeflateRect(Rectangle rect, Padding padding)
	{
		rect.X += padding.Left;
		rect.Y += padding.Top;
		rect.Width -= padding.Horizontal;
		rect.Height -= padding.Vertical;
		return rect;
	}

	public static Size AddAlignedRegion(Size textSize, Size imageSize, TextImageRelation relation)
	{
		return AddAlignedRegionCore(textSize, imageSize, IsVerticalRelation(relation));
	}

	public static Size AddAlignedRegionCore(Size currentSize, Size contentSize, bool vertical)
	{
		if (vertical)
		{
			currentSize.Width = Math.Max(currentSize.Width, contentSize.Width);
			currentSize.Height += contentSize.Height;
		}
		else
		{
			currentSize.Width += contentSize.Width;
			currentSize.Height = Math.Max(currentSize.Height, contentSize.Height);
		}
		return currentSize;
	}

	public static Padding FlipPadding(Padding padding)
	{
		if (padding.All != -1)
		{
			return padding;
		}
		int top = padding.Top;
		padding.Top = padding.Left;
		padding.Left = top;
		top = padding.Bottom;
		padding.Bottom = padding.Right;
		padding.Right = top;
		return padding;
	}

	public static Point FlipPoint(Point point)
	{
		int x = point.X;
		int y = point.Y;
		point.Y = x;
		point.X = y;
		return point;
	}

	public static Rectangle FlipRectangle(Rectangle rect)
	{
		rect.Location = FlipPoint(rect.Location);
		rect.Size = FlipSize(rect.Size);
		return rect;
	}

	public static Rectangle FlipRectangleIf(bool condition, Rectangle rect)
	{
		if (!condition)
		{
			return rect;
		}
		return FlipRectangle(rect);
	}

	public static Size FlipSize(Size size)
	{
		int width = size.Width;
		int height = size.Height;
		size.Height = width;
		size.Width = height;
		return size;
	}

	public static Size FlipSizeIf(bool condition, Size size)
	{
		if (!condition)
		{
			return size;
		}
		return FlipSize(size);
	}

	public static bool IsHorizontalAlignment(ContentAlignment align)
	{
		return !IsVerticalAlignment(align);
	}

	public static bool IsHorizontalRelation(TextImageRelation relation)
	{
		return (relation & (TextImageRelation)0xC) != 0;
	}

	public static bool IsVerticalAlignment(ContentAlignment align)
	{
		return (align & (ContentAlignment)0x202) != 0;
	}

	public static bool IsVerticalRelation(TextImageRelation relation)
	{
		return (relation & (TextImageRelation)3) != 0;
	}

	public static bool IsZeroWidthOrHeight(Rectangle rectangle)
	{
		if (rectangle.Width != 0)
		{
			return rectangle.Height == 0;
		}
		return true;
	}

	public static bool IsZeroWidthOrHeight(Size size)
	{
		if (size.Width != 0)
		{
			return size.Height == 0;
		}
		return true;
	}

	public static bool AreWidthAndHeightLarger(Size size1, Size size2)
	{
		if (size1.Width >= size2.Width)
		{
			return size1.Height >= size2.Height;
		}
		return false;
	}

	public static void SplitRegion(Rectangle bounds, Size specifiedContent, AnchorStyles region1Align, out Rectangle region1, out Rectangle region2)
	{
		region1 = (region2 = bounds);
		switch (region1Align)
		{
		case AnchorStyles.Left:
			region1.Width = specifiedContent.Width;
			region2.X += specifiedContent.Width;
			region2.Width -= specifiedContent.Width;
			break;
		case AnchorStyles.Right:
			region1.X += bounds.Width - specifiedContent.Width;
			region1.Width = specifiedContent.Width;
			region2.Width -= specifiedContent.Width;
			break;
		case AnchorStyles.Top:
			region1.Height = specifiedContent.Height;
			region2.Y += specifiedContent.Height;
			region2.Height -= specifiedContent.Height;
			break;
		case AnchorStyles.Bottom:
			region1.Y += bounds.Height - specifiedContent.Height;
			region1.Height = specifiedContent.Height;
			region2.Height -= specifiedContent.Height;
			break;
		}
	}

	public static void ExpandRegionsToFillBounds(Rectangle bounds, AnchorStyles region1Align, ref Rectangle region1, ref Rectangle region2)
	{
		switch (region1Align)
		{
		case AnchorStyles.Left:
			region1 = SubstituteSpecifiedBounds(bounds, region1, AnchorStyles.Right);
			region2 = SubstituteSpecifiedBounds(bounds, region2, AnchorStyles.Left);
			break;
		case AnchorStyles.Right:
			region1 = SubstituteSpecifiedBounds(bounds, region1, AnchorStyles.Left);
			region2 = SubstituteSpecifiedBounds(bounds, region2, AnchorStyles.Right);
			break;
		case AnchorStyles.Top:
			region1 = SubstituteSpecifiedBounds(bounds, region1, AnchorStyles.Bottom);
			region2 = SubstituteSpecifiedBounds(bounds, region2, AnchorStyles.Top);
			break;
		case AnchorStyles.Bottom:
			region1 = SubstituteSpecifiedBounds(bounds, region1, AnchorStyles.Top);
			region2 = SubstituteSpecifiedBounds(bounds, region2, AnchorStyles.Bottom);
			break;
		}
	}

	public static Size SubAlignedRegion(Size currentSize, Size contentSize, TextImageRelation relation)
	{
		return SubAlignedRegionCore(currentSize, contentSize, IsVerticalRelation(relation));
	}

	public static Size SubAlignedRegionCore(Size currentSize, Size contentSize, bool vertical)
	{
		if (vertical)
		{
			currentSize.Height -= contentSize.Height;
		}
		else
		{
			currentSize.Width -= contentSize.Width;
		}
		return currentSize;
	}

	private static Rectangle SubstituteSpecifiedBounds(Rectangle originalBounds, Rectangle substitutionBounds, AnchorStyles specified)
	{
		int left = (((specified & AnchorStyles.Left) != 0) ? substitutionBounds.Left : originalBounds.Left);
		int top = (((specified & AnchorStyles.Top) != 0) ? substitutionBounds.Top : originalBounds.Top);
		int right = (((specified & AnchorStyles.Right) != 0) ? substitutionBounds.Right : originalBounds.Right);
		int bottom = (((specified & AnchorStyles.Bottom) != 0) ? substitutionBounds.Bottom : originalBounds.Bottom);
		return Rectangle.FromLTRB(left, top, right, bottom);
	}

	public static Rectangle RTLTranslate(Rectangle bounds, Rectangle withinBounds)
	{
		bounds.X = withinBounds.Width - bounds.Right;
		return bounds;
	}
}
