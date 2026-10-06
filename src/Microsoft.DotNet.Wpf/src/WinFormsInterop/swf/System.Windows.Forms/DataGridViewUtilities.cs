// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// From .NET's System.Windows.Forms (dotnet/winforms).

namespace System.Windows.Forms
{
	internal static class DataGridViewUtilities
	{
		/// <summary>.NET's text flags for a cell style's alignment and wrap mode.</summary>
		internal static TextFormatFlags ComputeTextFormatFlagsForCellStyleAlignment (bool rightToLeft, DataGridViewContentAlignment alignment, DataGridViewTriState wrapMode)
		{
			TextFormatFlags flags;
			switch (alignment) {
			case DataGridViewContentAlignment.TopLeft:
				flags = rightToLeft ? TextFormatFlags.Right : TextFormatFlags.Default;
				break;
			case DataGridViewContentAlignment.TopCenter:
				flags = TextFormatFlags.HorizontalCenter;
				break;
			case DataGridViewContentAlignment.TopRight:
				flags = rightToLeft ? TextFormatFlags.Default : TextFormatFlags.Right;
				break;
			case DataGridViewContentAlignment.MiddleLeft:
				flags = TextFormatFlags.VerticalCenter | (rightToLeft ? TextFormatFlags.Right : TextFormatFlags.Default);
				break;
			case DataGridViewContentAlignment.MiddleCenter:
				flags = TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter;
				break;
			case DataGridViewContentAlignment.MiddleRight:
				flags = TextFormatFlags.VerticalCenter | (rightToLeft ? TextFormatFlags.Default : TextFormatFlags.Right);
				break;
			case DataGridViewContentAlignment.BottomLeft:
				flags = TextFormatFlags.Bottom | (rightToLeft ? TextFormatFlags.Right : TextFormatFlags.Default);
				break;
			case DataGridViewContentAlignment.BottomCenter:
				flags = TextFormatFlags.Bottom | TextFormatFlags.HorizontalCenter;
				break;
			case DataGridViewContentAlignment.BottomRight:
				flags = TextFormatFlags.Bottom | (rightToLeft ? TextFormatFlags.Default : TextFormatFlags.Right);
				break;
			default:
				flags = TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter;
				break;
			}
			flags |= wrapMode != DataGridViewTriState.False ? TextFormatFlags.WordBreak : TextFormatFlags.SingleLine;
			flags |= TextFormatFlags.NoPrefix | TextFormatFlags.PreserveGraphicsClipping;
			if (rightToLeft)
				flags |= TextFormatFlags.RightToLeft;
			return flags;
		}
	}
}
