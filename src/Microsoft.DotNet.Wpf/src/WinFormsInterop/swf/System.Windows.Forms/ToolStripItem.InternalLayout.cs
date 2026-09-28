// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// From .NET's System.Windows.Forms (dotnet/winforms), adapted to the port where noted.

// .NET's ToolStripItem.ToolStripItemInternalLayout: where an item's image and text go, laid out by
// the same ButtonBaseAdapter.LayoutOptions a Button uses (two pixels of border, no padding), and
// the text flags they are drawn with.

using System.Drawing;
using System.Windows.Forms.ButtonInternal;

namespace System.Windows.Forms
{
	public abstract partial class ToolStripItem
	{
		private ToolStripItemInternalLayout internal_layout;

		internal ToolStripItemInternalLayout InternalLayout => internal_layout ??= CreateInternalLayout ();

		private protected virtual ToolStripItemInternalLayout CreateInternalLayout ()
		{
			return new ToolStripItemInternalLayout (this);
		}

		/// <summary>.NET's PreferredImageSize: the image the layout makes room for.</summary>
		internal Size PreferredImageSize {
			get {
				if ((DisplayStyle & ToolStripItemDisplayStyle.Image) != ToolStripItemDisplayStyle.Image)
					return Size.Empty;
				bool usingImageList = owner != null && owner.ImageList != null
					&& (image_index >= 0 || !string.IsNullOrEmpty (image_key));
				if (ImageScaling == ToolStripItemImageScaling.SizeToFit && owner != null && (image != null || usingImageList))
					return owner.ImageScalingSize;
				if (!usingImageList)
					return image?.Size ?? Size.Empty;
				return owner?.ImageList?.ImageSize ?? Size.Empty;
			}
		}

		internal class ToolStripItemInternalLayout
		{
			internal class ToolStripItemLayoutOptions : ButtonBaseAdapter.LayoutOptions
			{
				private Size _cachedSize = System.Windows.Forms.Layout.LayoutUtils.s_invalidSize;
				private Size _cachedProposedConstraints = System.Windows.Forms.Layout.LayoutUtils.s_invalidSize;

				protected override Size GetTextSize (Size proposedConstraints)
				{
					if (_cachedSize != System.Windows.Forms.Layout.LayoutUtils.s_invalidSize
					    && (_cachedProposedConstraints == proposedConstraints || _cachedSize.Width <= proposedConstraints.Width))
						return _cachedSize;
					_cachedSize = base.GetTextSize (proposedConstraints);
					_cachedProposedConstraints = proposedConstraints;
					return _cachedSize;
				}
			}

			private sealed class ToolStripLayoutData
			{
				private readonly ToolStripLayoutStyle _layoutStyle;
				private readonly bool _autoSize;
				private Size _size;

				public ToolStripLayoutData (ToolStrip toolStrip)
				{
					_layoutStyle = toolStrip.LayoutStyle;
					_autoSize = toolStrip.AutoSize;
					_size = toolStrip.Size;
				}

				public bool IsCurrent (ToolStrip toolStrip)
				{
					return toolStrip != null && toolStrip.Size == _size && toolStrip.LayoutStyle == _layoutStyle
						&& toolStrip.AutoSize == _autoSize;
				}
			}

			private ToolStripItemLayoutOptions _currentLayoutOptions;
			private readonly ToolStripItem _ownerItem;
			private ButtonBaseAdapter.LayoutData _layoutData;
			private ToolStripLayoutData _parentLayoutData;

			protected virtual ToolStripItem Owner => _ownerItem;

			protected virtual ToolStrip ParentInternal => _ownerItem?.Parent;

			public ToolStripItemInternalLayout (ToolStripItem ownerItem)
			{
				_ownerItem = ownerItem ?? throw new ArgumentNullException (nameof (ownerItem));
			}

			public virtual Rectangle ImageRectangle {
				get {
					ButtonBaseAdapter.LayoutData layoutData = LayoutData;
					Rectangle imageBounds = layoutData.ImageBounds;
					imageBounds.Intersect (layoutData.Field);
					return imageBounds;
				}
			}

			internal ButtonBaseAdapter.LayoutData LayoutData {
				get {
					EnsureLayout ();
					return _layoutData;
				}
			}

			public Size PreferredImageSize => Owner.PreferredImageSize;

			public virtual Rectangle TextRectangle {
				get {
					ButtonBaseAdapter.LayoutData layoutData = LayoutData;
					Rectangle textBounds = layoutData.TextBounds;
					textBounds.Intersect (layoutData.Field);
					return textBounds;
				}
			}

			public virtual Rectangle ContentRectangle => LayoutData.Field;

			public virtual TextFormatFlags TextFormat
				=> _currentLayoutOptions != null ? _currentLayoutOptions.GdiTextFormatFlags : CommonLayoutOptions ().GdiTextFormatFlags;

			internal static TextFormatFlags ContentAlignmentToTextFormat (ContentAlignment alignment, bool rightToLeft)
			{
				TextFormatFlags flags = TextFormatFlags.Default;
				if (rightToLeft)
					flags |= TextFormatFlags.RightToLeft;
				return flags | ControlPaint.ConvertAlignmentToTextFormat (alignment);
			}

			protected virtual ToolStripItemLayoutOptions CommonLayoutOptions ()
			{
				var options = new ToolStripItemLayoutOptions ();
				options.Client = new Rectangle (Point.Empty, _ownerItem.Size);
				options.GrowBorderBy1PxWhenDefault = false;
				options.BorderSize = 2;
				options.PaddingSize = 0;
				options.MaxFocus = true;
				options.FocusOddEvenFixup = false;
				options.Font = _ownerItem.Font;
				options.Text = (Owner.DisplayStyle & ToolStripItemDisplayStyle.Text) == ToolStripItemDisplayStyle.Text ? Owner.Text : string.Empty;
				options.ImageSize = PreferredImageSize;
				options.CheckSize = 0;
				options.CheckPaddingSize = 0;
				options.CheckAlign = ContentAlignment.TopLeft;
				options.ImageAlign = Owner.ImageAlign;
				options.TextAlign = Owner.TextAlign;
				options.HintTextUp = false;
				options.ShadowedText = !_ownerItem.Enabled;
				options.LayoutRTL = Owner.RightToLeft == RightToLeft.Yes;
				options.TextImageRelation = Owner.TextImageRelation;
				options.TextImageInset = 0;
				options.DotNetOneButtonCompat = false;
				options.GdiTextFormatFlags = ContentAlignmentToTextFormat (Owner.TextAlign, Owner.RightToLeft == RightToLeft.Yes);
				options.GdiTextFormatFlags = Owner.ShowKeyboardCues ? options.GdiTextFormatFlags : options.GdiTextFormatFlags | TextFormatFlags.HidePrefix;
				return options;
			}

			// .NET re-lays out when its parent changes and whenever the item invalidates the layout --
			// on its text, font, image, alignment, style, size. The port raises none of those, so it
			// lays out every time it is asked: a measure per paint, which the text cache absorbs.
			private void EnsureLayout ()
			{
				PerformLayout ();
			}

			private ButtonBaseAdapter.LayoutData GetLayoutData ()
			{
				_currentLayoutOptions = CommonLayoutOptions ();
				if (Owner.TextDirection != ToolStripTextDirection.Horizontal)
					_currentLayoutOptions.VerticalText = true;
				return _currentLayoutOptions.Layout ();
			}

			public virtual Size GetPreferredSize (Size constrainingSize)
			{
				EnsureLayout ();
				return _ownerItem != null ? _currentLayoutOptions.GetPreferredSizeCore (constrainingSize) : Size.Empty;
			}

			internal void PerformLayout ()
			{
				_layoutData = GetLayoutData ();
				ToolStrip parent = ParentInternal;
				_parentLayoutData = parent != null ? new ToolStripLayoutData (parent) : null;
			}
		}
	}
}
