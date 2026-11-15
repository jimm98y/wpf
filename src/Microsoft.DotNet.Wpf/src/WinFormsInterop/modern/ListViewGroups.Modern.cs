// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// A list view group's parts Windows Vista added, ported from .NET's ListViewGroup and ListView: a group that collapses
// (its chevron at the end of the header; collapsed, only its header and footer show), its subtitle under the header,
// its footer under the items, a task link in the header, and a title image from the list view's GroupImageList. The
// layout and drawing are Mono's (ListView.LayoutGroupHeader, ThemeWin32Classic.DrawListViewGroupHeader), told of them.

using System.ComponentModel;
using System.Drawing;
using System.Drawing.Drawing2D;

namespace System.Windows.Forms
{
	/// <summary>Whether a group can collapse, and whether it is (LVGS_COLLAPSIBLE, LVGS_COLLAPSED).</summary>
	public enum ListViewGroupCollapsedState
	{
		Default = 0,
		Expanded = 1,
		Collapsed = 2,
	}

	public class ListViewGroupEventArgs : EventArgs
	{
		public ListViewGroupEventArgs (int groupIndex) => GroupIndex = groupIndex;

		/// <summary>The group's index in the list view's Groups.</summary>
		public int GroupIndex { get; }
	}

	/// <summary>A state image index, among the list view's StateImageList (no "none").</summary>
	public class ListViewItemStateImageIndexConverter : ImageIndexConverter
	{
		protected override bool IncludeNoneAsStandardValue => false;

		public override StandardValuesCollection GetStandardValues (ITypeDescriptorContext context)
		{
			if (context?.Instance is ListViewItem item && item.ListView?.StateImageList is ImageList images) {
				var indexes = new int [images.Images.Count];
				for (int i = 0; i < indexes.Length; i++)
					indexes [i] = i;
				return new StandardValuesCollection (indexes);
			}
			return new StandardValuesCollection (Array.Empty<int> ());
		}
	}

	public sealed partial class ListViewGroup
	{
		/// <summary>Where a collapsed group's items are put: out of every view, hit test and paint.</summary>
		internal const int HiddenItemY = -0x100000;

		private string _footer = string.Empty, _subtitle = string.Empty, _taskLink = string.Empty, _titleImageKey = string.Empty;
		private HorizontalAlignment _footerAlignment = HorizontalAlignment.Left;
		private ListViewGroupCollapsedState _collapsedState;
		private int _titleImageIndex = -1;
		internal Rectangle footer_bounds;

		/// <summary>The text under the group's items.</summary>
		[DefaultValue ("")]
		public string Footer {
			get => _footer;
			set => Change (ref _footer, value ?? string.Empty);
		}

		[DefaultValue (HorizontalAlignment.Left)]
		public HorizontalAlignment FooterAlignment {
			get => _footerAlignment;
			set {
				if (value < HorizontalAlignment.Left || value > HorizontalAlignment.Center)
					throw new InvalidEnumArgumentException (nameof (value), (int) value, typeof (HorizontalAlignment));
				if (_footerAlignment != value) {
					_footerAlignment = value;
					Relayout ();
				}
			}
		}

		/// <summary>Whether the group can collapse (Expanded, Collapsed) and is collapsed.</summary>
		[DefaultValue (ListViewGroupCollapsedState.Default)]
		public ListViewGroupCollapsedState CollapsedState {
			get => _collapsedState;
			set {
				if (value < ListViewGroupCollapsedState.Default || value > ListViewGroupCollapsedState.Collapsed)
					throw new InvalidEnumArgumentException (nameof (value), (int) value, typeof (ListViewGroupCollapsedState));
				if (_collapsedState == value)
					return;
				_collapsedState = value;
				Relayout ();
				list_view_owner?.OnGroupCollapsedStateChangedInternal (new ListViewGroupEventArgs (list_view_owner.Groups.IndexOf (this)));
			}
		}

		internal bool IsCollapsed => _collapsedState == ListViewGroupCollapsedState.Collapsed;

		/// <summary>The text under the header.</summary>
		[DefaultValue ("")]
		public string Subtitle {
			get => _subtitle;
			set => Change (ref _subtitle, value ?? string.Empty);
		}

		/// <summary>The link at the end of the header's line (ListView.GroupTaskLinkClick).</summary>
		[DefaultValue ("")]
		public string TaskLink {
			get => _taskLink;
			set => Change (ref _taskLink, value ?? string.Empty);
		}

		/// <summary>The title image's index in the list view's GroupImageList; -1 for none.</summary>
		[DefaultValue (-1)]
		[TypeConverter (typeof (ImageIndexConverter))]
		public int TitleImageIndex {
			get => _titleImageIndex;
			set {
				if (value < -1)
					throw new ArgumentOutOfRangeException (nameof (value));
				if (_titleImageIndex != value) {
					_titleImageIndex = value;
					if (value != -1)
						_titleImageKey = string.Empty;
					Relayout ();
				}
			}
		}

		/// <summary>The title image's key in the list view's GroupImageList.</summary>
		[DefaultValue ("")]
		[TypeConverter (typeof (ImageKeyConverter))]
		public string TitleImageKey {
			get => _titleImageKey;
			set {
				value ??= string.Empty;
				if (_titleImageKey != value) {
					_titleImageKey = value;
					if (value.Length != 0)
						_titleImageIndex = -1;
					Relayout ();
				}
			}
		}

		internal Image TitleImage {
			get {
				ImageList images = list_view_owner?.GroupImageList;
				if (images == null)
					return null;
				if (_titleImageIndex >= 0 && _titleImageIndex < images.Images.Count)
					return images.Images [_titleImageIndex];
				if (_titleImageKey.Length != 0 && images.Images.IndexOfKey (_titleImageKey) is int i && i >= 0)
					return images.Images [i];
				return null;
			}
		}

		/// <summary>The footer, as laid out: scrolled as the header is.</summary>
		internal Rectangle FooterBounds {
			get {
				if (footer_bounds.IsEmpty || list_view_owner == null)
					return Rectangle.Empty;
				Rectangle r = footer_bounds;
				r.X -= list_view_owner.h_marker;
				r.Y -= list_view_owner.v_marker;
				return r;
			}
		}

		// the header's first line, where the chevron and the task link are
		private Rectangle HeaderLine {
			get {
				Rectangle header = HeaderBounds;
				int line = (list_view_owner?.Font.Height ?? 13) + 2;
				return new Rectangle (header.Left + 8, header.Top, Math.Max (0, header.Width - 16), line);
			}
		}

		/// <summary>The chevron of a group that can collapse; empty for one that cannot.</summary>
		internal Rectangle ChevronBounds {
			get {
				if (_collapsedState == ListViewGroupCollapsedState.Default || list_view_owner == null)
					return Rectangle.Empty;
				Rectangle line = HeaderLine;
				int size = line.Height;
				return new Rectangle (line.Right - size, line.Top, size, size);
			}
		}

		/// <summary>The task link, before the chevron; empty without one.</summary>
		internal Rectangle TaskLinkBounds {
			get {
				if (_taskLink.Length == 0 || list_view_owner == null)
					return Rectangle.Empty;
				Rectangle line = HeaderLine;
				Size text = TextRenderer.MeasureText (_taskLink, list_view_owner.Font);
				Rectangle chevron = ChevronBounds;
				int right = chevron.IsEmpty ? line.Right : chevron.Left - 4;
				return new Rectangle (right - text.Width, line.Top, text.Width, line.Height);
			}
		}

		private void Change (ref string field, string value)
		{
			if (field != value) {
				field = value;
				Relayout ();
			}
		}

		private void Relayout () => list_view_owner?.Redraw (true);
	}

	public partial class ListView
	{
		private static readonly object s_groupTaskLinkClickEvent = new object ();
		private static readonly object s_groupCollapsedStateChangedEvent = new object ();
		private ImageList _groupImageList;

		/// <summary>The images groups show as their title images.</summary>
		[DefaultValue (null)]
		public ImageList GroupImageList {
			get => _groupImageList;
			set {
				if (_groupImageList == value)
					return;
				_groupImageList = value;
				if (UsingGroups)
					Redraw (true);
			}
		}

		/// <summary>Occurs when a group's task link is clicked.</summary>
		public event EventHandler<ListViewGroupEventArgs> GroupTaskLinkClick {
			add { Events.AddHandler (s_groupTaskLinkClickEvent, value); }
			remove { Events.RemoveHandler (s_groupTaskLinkClickEvent, value); }
		}

		/// <summary>Occurs when a group collapses or expands.</summary>
		public event EventHandler<ListViewGroupEventArgs> GroupCollapsedStateChanged {
			add { Events.AddHandler (s_groupCollapsedStateChangedEvent, value); }
			remove { Events.RemoveHandler (s_groupCollapsedStateChangedEvent, value); }
		}

		protected virtual void OnGroupCollapsedStateChanged (ListViewGroupEventArgs e) =>
			(Events [s_groupCollapsedStateChangedEvent] as EventHandler<ListViewGroupEventArgs>)?.Invoke (this, e);

		protected virtual void OnGroupTaskLinkClick (ListViewGroupEventArgs e) =>
			(Events [s_groupTaskLinkClickEvent] as EventHandler<ListViewGroupEventArgs>)?.Invoke (this, e);

		internal void OnGroupCollapsedStateChangedInternal (ListViewGroupEventArgs e) => OnGroupCollapsedStateChanged (e);

		/// <summary>A click in the item area on a group's header: the chevron, or a double click on the header, collapses or
		/// expands it; the task link raises GroupTaskLinkClick. True where the click was the header's.</summary>
		internal bool GroupHeaderClicked (Point pt, int clicks)
		{
			for (int i = 0; i < groups.InternalCount; i++) {
				ListViewGroup group = groups.GetInternalGroup (i);
				if (group.ItemCount == 0 || !group.HeaderBounds.Contains (pt))
					continue;
				int index = groups.IndexOf (group);
				if (group.TaskLinkBounds.Contains (pt)) {
					OnGroupTaskLinkClick (new ListViewGroupEventArgs (index));
					return true;
				}
				bool collapsible = group.CollapsedState != ListViewGroupCollapsedState.Default;
				if (collapsible && (group.ChevronBounds.Contains (pt) || clicks == 2)) {
					group.CollapsedState = group.IsCollapsed ? ListViewGroupCollapsedState.Expanded : ListViewGroupCollapsedState.Collapsed;
					return true;
				}
				return false;
			}
			return false;
		}
	}

	internal partial class ThemeWin32Classic
	{
		/// <summary>The chevron of a group that collapses: pointing up while it is expanded, down while collapsed,
		/// as Windows draws it at the end of the header.</summary>
		protected virtual void DrawListViewGroupChevron (Graphics dc, Rectangle bounds, bool collapsed)
		{
			int cx = bounds.Left + bounds.Width / 2, cy = bounds.Top + bounds.Height / 2;
			int half = Math.Max (3, bounds.Width / 4);
			int rise = collapsed ? half / 2 : -half / 2;
			var points = new [] {
				new Point (cx - half, cy - rise),
				new Point (cx, cy + rise),
				new Point (cx + half, cy - rise),
			};
			SmoothingMode smoothing = dc.SmoothingMode;
			dc.SmoothingMode = SmoothingMode.AntiAlias;
			using (var pen = new Pen (SystemColors.ControlDarkDark, 1.5f))
				dc.DrawLines (pen, points);
			dc.SmoothingMode = smoothing;
		}
	}
}
