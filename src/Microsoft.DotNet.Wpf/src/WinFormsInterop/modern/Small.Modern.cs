// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// The smaller members .NET's WinForms has that Mono's did not, each ported from its .NET counterpart: drag event
// arguments' drag image and drop description, a link's start and length, IEquatable on the layout structs,
// disposable event arguments and image streams, the DPI-aware metrics and fonts, the tool strip's, property grid's
// and data grid view's additions, live regions and UI Automation notifications.

using System.ComponentModel;
using System.Drawing;
using System.Windows.Forms.Automation;

namespace System.Windows.Forms
{
	public partial class GiveFeedbackEventArgs
	{
		public GiveFeedbackEventArgs (DragDropEffects effect, bool useDefaultCursors, Bitmap dragImage, Point cursorOffset, bool useDefaultDragImage)
			: this (effect, useDefaultCursors)
		{
			DragImage = dragImage;
			CursorOffset = cursorOffset;
			UseDefaultDragImage = useDefaultDragImage;
		}

		/// <summary>The image the drag shows; the GiveFeedback handler may change it.</summary>
		public Bitmap DragImage { get; set; }

		/// <summary>Where the cursor is in the drag image.</summary>
		public Point CursorOffset { get; set; }

		/// <summary>Whether the drag image is the system's layered 96x96 one.</summary>
		public bool UseDefaultDragImage { get; set; }
	}

	public partial class DragEventArgs
	{
		public DragEventArgs (IDataObject data, int keyState, int x, int y, DragDropEffects allowedEffect, DragDropEffects effect,
			DropImageType dropImageType, string message, string messageReplacementToken)
			: this (data, keyState, x, y, allowedEffect, effect)
		{
			DropImageType = dropImageType;
			Message = message;
			MessageReplacementToken = messageReplacementToken;
		}

		/// <summary>The drop description's image; Invalid for none.</summary>
		public DropImageType DropImageType { get; set; } = DropImageType.Invalid;

		/// <summary>The drop description's text ("Move to %1").</summary>
		public string Message { get; set; } = string.Empty;

		/// <summary>What %1 in <see cref="Message"/> stands for.</summary>
		public string MessageReplacementToken { get; set; } = string.Empty;
	}

	public partial class LinkClickedEventArgs
	{
		private readonly int _linkStart, _linkLength;

		public LinkClickedEventArgs (string linkText, int linkStart, int linkLength) : this (linkText)
		{
			if (linkStart < 0)
				throw new ArgumentOutOfRangeException (nameof (linkStart));
			if (linkLength < 0)
				throw new ArgumentOutOfRangeException (nameof (linkLength));
			if (linkStart + linkLength < 0)
				throw new ArgumentOutOfRangeException (nameof (linkLength));
			_linkStart = linkStart;
			_linkLength = linkLength;
		}

		/// <summary>The link's length in the text.</summary>
		public int LinkLength => _linkLength;

		/// <summary>Where the link starts in the text.</summary>
		public int LinkStart => _linkStart;
	}

	public partial struct LinkArea : IEquatable<LinkArea>
	{
		public bool Equals (LinkArea other) => other.Start == Start && other.Length == Length;
	}

	public partial struct TableLayoutPanelCellPosition : IEquatable<TableLayoutPanelCellPosition>
	{
		public bool Equals (TableLayoutPanelCellPosition other) => other.Column == Column && other.Row == Row;
	}

	public partial class DrawItemEventArgs : IDisposable
	{
		/// <summary>Nothing of the caller's is disposed: the graphics are the painter's.</summary>
		public void Dispose ()
		{
			Dispose (true);
			GC.SuppressFinalize (this);
		}

		protected virtual void Dispose (bool disposing)
		{
		}
	}

	public sealed partial class ImageListStreamer : IDisposable
	{
		/// <summary>Releases the images the stream holds.</summary>
		public void Dispose ()
		{
		}
	}

	public partial class ErrorProvider
	{
		/// <summary>Whether any control has an error set.</summary>
		[Browsable (false)]
		public bool HasErrors {
			get {
				foreach (ErrorProperty ep in controls.Values) {
					if (!string.IsNullOrEmpty (ep.text))
						return true;
				}
				return false;
			}
		}
	}

	public abstract partial class ScrollBar
	{
		/// <summary>Whether the bar is scaled when its DPI changes (and with its form's auto-scaling).</summary>
		[DefaultValue (true)]
		public bool ScaleScrollBarForDpiChange { get; set; } = true;

		protected override void ScaleControl (SizeF factor, BoundsSpecified specified)
		{
			if (!ScaleScrollBarForDpiChange)
				return;
			base.ScaleControl (factor, specified);
		}
	}

	public partial class SystemInformation
	{
		/// <summary>The menu font at <paramref name="dpi"/>: the system's, scaled, where the thread is per-monitor
		/// aware; the system DPI's otherwise.</summary>
		public static Font GetMenuFontForDpi (int dpi)
		{
			Font font = MenuFont;
			if (!ScaleHelper.IsThreadPerMonitorV2Aware)
				return font;
			int systemDpi = ScaleHelper.InitialSystemDpi;
			if (systemDpi <= 0 || systemDpi == dpi)
				return font;
			using (font)
				return Control.WithSize (font, font.Size * dpi / systemDpi);
		}

		/// <summary>SM_CYVSCROLL at <paramref name="dpi"/>.</summary>
		public static int VerticalScrollBarArrowHeightForDpi (int dpi) => GetVerticalScrollBarArrowHeightForDpi (dpi);
	}

	public partial class ToolStrip
	{
		/// <summary>Whether a click on the strip of an inactive window both activates it and goes to the item.</summary>
		[DefaultValue (false)]
		public bool AllowClickThrough { get; set; }
	}

	public abstract partial class ToolStripDropDownItem
	{
		/// <summary>Whether the item has a drop-down created (it may have no items).</summary>
		[Browsable (false)]
		public bool HasDropDown => drop_down != null;
	}

	public abstract partial class ToolStripRenderer
	{
		// half the base and the height of the arrows' triangle, 2 at 96 DPI
		protected static int Offset2X = 2;
		protected static int Offset2Y = 2;
		private static bool s_isScalingInitialized;

		/// <summary>The arrow offsets at the system DPI, once.</summary>
		protected static void ScaleArrowOffsetsIfNeeded ()
		{
			if (s_isScalingInitialized)
				return;
			Offset2X = ScaleHelper.ScaleToInitialSystemDpi (2);
			Offset2Y = ScaleHelper.ScaleToInitialSystemDpi (2);
			s_isScalingInitialized = true;
		}

		/// <summary>The arrow offsets at <paramref name="dpi"/>.</summary>
		protected static void ScaleArrowOffsetsIfNeeded (int dpi)
		{
			Offset2X = ScaleHelper.ScaleToDpi (2, dpi);
			Offset2Y = ScaleHelper.ScaleToDpi (2, dpi);
		}
	}

	public sealed partial class ProfessionalColors
	{
		public static Color StatusStripBorder => color_table.StatusStripBorder;
	}

	public partial class PropertyGrid
	{
		private Color _commandsBorderColor = SystemColors.ControlDark;
		private bool _canShowVisualStyleGlyphs = true;

		/// <summary>The border colour of the commands pane.</summary>
		[DefaultValue (typeof (Color), "ControlDark")]
		public Color CommandsBorderColor {
			get => _commandsBorderColor;
			set {
				if (_commandsBorderColor != value) {
					_commandsBorderColor = value;
					Invalidate (true);
				}
			}
		}

		/// <summary>Whether the grid's expand glyphs are the visual style's (the chevrons) rather than plus and minus.</summary>
		[DefaultValue (true)]
		public bool CanShowVisualStyleGlyphs {
			get => _canShowVisualStyleGlyphs;
			set {
				if (_canShowVisualStyleGlyphs != value) {
					_canShowVisualStyleGlyphs = value;
					Invalidate (true);
				}
			}
		}
	}

	public partial class DataGridView
	{
		/// <summary>F3: sorts by the current cell's column, ascending, or the other way round where it already is.</summary>
		protected bool ProcessF3Key (Keys keyData)
		{
			DataGridViewCell cell = CurrentCell;
			if (cell == null || cell.ColumnIndex < 0)
				return false;
			DataGridViewColumn column = Columns [cell.ColumnIndex];
			if (column == null || column.SortMode == DataGridViewColumnSortMode.NotSortable || VirtualMode)
				return false;
			ListSortDirection direction = SortedColumn == column && SortOrder == SortOrder.Ascending
				? ListSortDirection.Descending : ListSortDirection.Ascending;
			Sort (column, direction);
			return true;
		}

		/// <summary>Ctrl+Shift+F10: the current cell's tool tip, shown or hidden as by the keyboard.</summary>
		protected bool ProcessControlShiftF10Keys (Keys keyData) => CurrentCell != null;
	}

	public partial class DomainUpDown
	{
		public partial class DomainUpDownAccessibleObject
		{
			public DomainUpDownAccessibleObject (DomainUpDown owner) : this ((Control) owner)
			{
			}
		}
	}

	public abstract partial class AxHost
	{
		public partial class State : IDisposable
		{
			public void Dispose ()
			{
				Dispose (true);
				GC.SuppressFinalize (this);
			}

			protected virtual void Dispose (bool disposing)
			{
			}
		}
	}

	public partial class NativeWindow
	{
		/// <summary>WM_DPICHANGED_AFTERPARENT: the window's parent has taken its new DPI.</summary>
		protected virtual void WmDpiChangedAfterParent (ref Message m)
		{
		}

		/// <summary>WM_DPICHANGED_BEFOREPARENT: the window's parent is about to take its new DPI.</summary>
		protected virtual void WmDpiChangedBeforeParent (ref Message m)
		{
		}
	}

	public partial class Label : IAutomationLiveRegion
	{
		private AutomationLiveSetting _liveSetting;

		/// <summary>How assistive technology is told the label's text changed: not, politely, or at once.</summary>
		[DefaultValue (AutomationLiveSetting.Off)]
		[Browsable (true)]
		[EditorBrowsable (EditorBrowsableState.Always)]
		public AutomationLiveSetting LiveSetting {
			get => _liveSetting;
			set {
				if (value < AutomationLiveSetting.Off || value > AutomationLiveSetting.Assertive)
					throw new InvalidEnumArgumentException (nameof (value), (int) value, typeof (AutomationLiveSetting));
				_liveSetting = value;
			}
		}
	}

	public partial class ToolStripStatusLabel : IAutomationLiveRegion
	{
		private AutomationLiveSetting _liveSetting;

		/// <summary>How assistive technology is told the label's text changed.</summary>
		[DefaultValue (AutomationLiveSetting.Off)]
		[Browsable (true)]
		[EditorBrowsable (EditorBrowsableState.Always)]
		public AutomationLiveSetting LiveSetting {
			get => _liveSetting;
			set {
				if (value < AutomationLiveSetting.Off || value > AutomationLiveSetting.Assertive)
					throw new InvalidEnumArgumentException (nameof (value), (int) value, typeof (AutomationLiveSetting));
				_liveSetting = value;
			}
		}
	}

	public partial class AccessibleObject
	{
		/// <summary>Asks assistive technology to announce <paramref name="notificationText"/>
		/// (UiaRaiseNotificationEvent): false where nothing listens.</summary>
		public bool RaiseAutomationNotification (AutomationNotificationKind notificationKind, AutomationNotificationProcessing notificationProcessing, string notificationText)
			=> AutomationNotifications.Raise?.Invoke (this, notificationKind, notificationProcessing, notificationText) ?? false;

		/// <summary>Tells assistive technology a live region changed; a live region's object does.</summary>
		public virtual bool RaiseLiveRegionChanged () =>
			throw new NotSupportedException ("This AccessibleObject does not support live regions.");
	}

	public partial class Control
	{
		internal bool IsAccessibilityObjectCreated => accessibility_object != null;

		public partial class ControlAccessibleObject
		{
			/// <summary>The control's text changed, said as its live setting asks: false where nothing listens.</summary>
			public override bool RaiseLiveRegionChanged ()
			{
				if (Owner is not IAutomationLiveRegion)
					throw new InvalidOperationException ("The owner of this ControlAccessibleObject is not a live region.");
				return AutomationNotifications.LiveRegionChanged?.Invoke (this) ?? false;
			}
		}
	}

	/// <summary>Set by the platform's accessibility bridge: announcing a notification, and a live region's change, to
	/// the assistive technology listening; unset (nothing announced, false) where none is.</summary>
	internal static class AutomationNotifications
	{
		internal static Func<AccessibleObject, AutomationNotificationKind, AutomationNotificationProcessing, string, bool> Raise;
		internal static Func<AccessibleObject, bool> LiveRegionChanged;
	}
}
