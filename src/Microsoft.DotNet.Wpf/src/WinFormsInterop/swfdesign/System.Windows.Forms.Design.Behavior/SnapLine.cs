// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
//
// Ported from dotnet/winforms (Design/Behavior/SnapLine.cs): the margin/padding filter rules of ShouldSnap,
// AdjustOffset and ToString (the Mono stub threw from the first two), and Low as the default priority (Mono
// defaulted to 0, which is not a SnapLinePriority at all).

namespace System.Windows.Forms.Design.Behavior
{
	public sealed class SnapLine
	{
		// Used in the filter to define custom margin/padding snap lines. Margins attract their opposites
		// (margin right == margin left), paddings attract the like margin.
		internal const string Margin = "Margin";
		internal const string MarginRight = Margin + ".Right";
		internal const string MarginLeft = Margin + ".Left";
		internal const string MarginBottom = Margin + ".Bottom";
		internal const string MarginTop = Margin + ".Top";
		internal const string Padding = "Padding";
		internal const string PaddingRight = Padding + ".Right";
		internal const string PaddingLeft = Padding + ".Left";
		internal const string PaddingBottom = Padding + ".Bottom";
		internal const string PaddingTop = Padding + ".Top";

		public SnapLine (SnapLineType type, int offset)
			: this (type, offset, null, SnapLinePriority.Low)
		{
		}

		public SnapLine (SnapLineType type, int offset, string filter)
			: this (type, offset, filter, SnapLinePriority.Low)
		{
		}

		public SnapLine (SnapLineType type, int offset, SnapLinePriority priority)
			: this (type, offset, null, priority)
		{
		}

		public SnapLine (SnapLineType type, int offset, string filter, SnapLinePriority priority)
		{
			SnapLineType = type;
			Offset = offset;
			Filter = filter;
			Priority = priority;
		}

		public string Filter { get; }

		public bool IsHorizontal => SnapLineType == SnapLineType.Top || SnapLineType == SnapLineType.Bottom
			|| SnapLineType == SnapLineType.Horizontal || SnapLineType == SnapLineType.Baseline;

		public bool IsVertical => SnapLineType == SnapLineType.Left || SnapLineType == SnapLineType.Right
			|| SnapLineType == SnapLineType.Vertical;

		public int Offset { get; private set; }

		public SnapLinePriority Priority { get; }

		public SnapLineType SnapLineType { get; }

		public void AdjustOffset (int adjustment)
		{
			Offset += adjustment;
		}

		public static bool ShouldSnap (SnapLine line1, SnapLine line2)
		{
			if (line1.SnapLineType != line2.SnapLineType)
				return false;
			if (line1.Filter == null && line2.Filter == null)
				return true;
			if (line1.Filter == null || line2.Filter == null)
				return false;

			if (line1.Filter.Contains (Margin)) {
				return (line1.Filter.Equals (MarginRight) && (line2.Filter.Equals (MarginLeft) || line2.Filter.Equals (PaddingRight)))
					|| (line1.Filter.Equals (MarginLeft) && (line2.Filter.Equals (MarginRight) || line2.Filter.Equals (PaddingLeft)))
					|| (line1.Filter.Equals (MarginTop) && (line2.Filter.Equals (MarginBottom) || line2.Filter.Equals (PaddingTop)))
					|| (line1.Filter.Equals (MarginBottom) && (line2.Filter.Equals (MarginTop) || line2.Filter.Equals (PaddingBottom)));
			}

			if (line1.Filter.Contains (Padding)) {
				return (line1.Filter.Equals (PaddingLeft) && line2.Filter.Equals (MarginLeft))
					|| (line1.Filter.Equals (PaddingRight) && line2.Filter.Equals (MarginRight))
					|| (line1.Filter.Equals (PaddingTop) && line2.Filter.Equals (MarginTop))
					|| (line1.Filter.Equals (PaddingBottom) && line2.Filter.Equals (MarginBottom));
			}

			return line1.Filter.Equals (line2.Filter);
		}

		public override string ToString ()
			=> $"SnapLine: {{type = {SnapLineType}, offset = {Offset}, priority = {Priority}, filter = {Filter ?? "<null>"}}}";
	}
}
