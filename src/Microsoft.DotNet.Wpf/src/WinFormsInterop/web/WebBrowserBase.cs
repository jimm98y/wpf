// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// .NET's WebBrowserBase and WebBrowserSiteBase: the base of a control that hosts a browser (WebBrowser's, the
// ActiveX host's in .NET) and its site. Ported for their surface and rules: the control's own appearance and input
// events are the browser's, not the control's, so their properties are hidden and adding a handler to their events
// throws, as for an ActiveX control in .NET; the sink and interface hooks are the virtuals a derived browser overrides.

using System.ComponentModel;
using System.Drawing;

namespace System.Windows.Forms
{
	public class WebBrowserBase : Control
	{
		private const string InvalidEvent = "The '{0}' event is not valid on an ActiveX control.";

		internal WebBrowserBase ()
		{
		}

		/// <summary>The object the browser's events are sunk on (the ActiveX instance in .NET).</summary>
		[Browsable (false)]
		[EditorBrowsable (EditorBrowsableState.Advanced)]
		[DesignerSerializationVisibility (DesignerSerializationVisibility.Hidden)]
		public object ActiveXInstance => ActiveXInstanceCore;

		internal virtual object ActiveXInstanceCore => null;

		/// <summary>The site the browser is given (.NET's IOleClientSite and its kin).</summary>
		protected virtual WebBrowserSiteBase CreateWebBrowserSiteBase () => new WebBrowserSiteBase (this);

		/// <summary>The browser's object was created: a derived control takes its interfaces from it.</summary>
		protected virtual void AttachInterfaces (object nativeActiveXObject)
		{
		}

		/// <summary>The browser's object is going: a derived control lets go of its interfaces.</summary>
		protected virtual void DetachInterfaces ()
		{
		}

		/// <summary>Called once the browser is ready for an event sink to be attached.</summary>
		protected virtual void CreateSink ()
		{
		}

		/// <summary>Called when the browser is going away and an event sink should be released.</summary>
		protected virtual void DetachSink ()
		{
		}

		[EditorBrowsable (EditorBrowsableState.Never)]
		public new void DrawToBitmap (Bitmap bitmap, Rectangle targetBounds) => base.DrawToBitmap (bitmap, targetBounds);

		public override bool PreProcessMessage (ref Message msg) => base.PreProcessMessage (ref msg);

		public override ISite Site {
			set => base.Site = value;
		}

		[Browsable (false)]
		[EditorBrowsable (EditorBrowsableState.Never)]
		[DesignerSerializationVisibility (DesignerSerializationVisibility.Hidden)]
		public override string Text {
			get => string.Empty;
			set => throw new NotSupportedException ("WebBrowser does not support the Text property.");
		}

		[Browsable (false)]
		[EditorBrowsable (EditorBrowsableState.Never)]
		[DesignerSerializationVisibility (DesignerSerializationVisibility.Hidden)]
		public new bool Enabled {
			get => base.Enabled;
			set => base.Enabled = value;
		}

		[Browsable (false)]
		[EditorBrowsable (EditorBrowsableState.Never)]
		[DesignerSerializationVisibility (DesignerSerializationVisibility.Hidden)]
		public new ImeMode ImeMode {
			get => base.ImeMode;
			set => base.ImeMode = value;
		}

		[Browsable (false)]
		[EditorBrowsable (EditorBrowsableState.Never)]
		[DesignerSerializationVisibility (DesignerSerializationVisibility.Hidden)]
		public new bool UseWaitCursor {
			get => base.UseWaitCursor;
			set => base.UseWaitCursor = value;
		}

		[Browsable (false)]
		[EditorBrowsable (EditorBrowsableState.Never)]
		[DesignerSerializationVisibility (DesignerSerializationVisibility.Hidden)]
		public override bool AllowDrop {
			get => base.AllowDrop;
			set => base.AllowDrop = value;
		}

		[Browsable (false)]
		[EditorBrowsable (EditorBrowsableState.Never)]
		[DesignerSerializationVisibility (DesignerSerializationVisibility.Hidden)]
		public override Color BackColor {
			get => base.BackColor;
			set => base.BackColor = value;
		}

		[Browsable (false)]
		[EditorBrowsable (EditorBrowsableState.Never)]
		[DesignerSerializationVisibility (DesignerSerializationVisibility.Hidden)]
		public override Image BackgroundImage {
			get => base.BackgroundImage;
			set => base.BackgroundImage = value;
		}

		[Browsable (false)]
		[EditorBrowsable (EditorBrowsableState.Never)]
		[DesignerSerializationVisibility (DesignerSerializationVisibility.Hidden)]
		public override ImageLayout BackgroundImageLayout {
			get => base.BackgroundImageLayout;
			set => base.BackgroundImageLayout = value;
		}

		[Browsable (false)]
		[EditorBrowsable (EditorBrowsableState.Never)]
		[DesignerSerializationVisibility (DesignerSerializationVisibility.Hidden)]
		public override Cursor Cursor {
			get => base.Cursor;
			set => base.Cursor = value;
		}

		[Browsable (false)]
		[EditorBrowsable (EditorBrowsableState.Never)]
		[DesignerSerializationVisibility (DesignerSerializationVisibility.Hidden)]
		public override Font Font {
			get => base.Font;
			set => base.Font = value;
		}

		[Browsable (false)]
		[EditorBrowsable (EditorBrowsableState.Never)]
		[DesignerSerializationVisibility (DesignerSerializationVisibility.Hidden)]
		public override Color ForeColor {
			get => base.ForeColor;
			set => base.ForeColor = value;
		}

		[Browsable (false)]
		[EditorBrowsable (EditorBrowsableState.Never)]
		[DesignerSerializationVisibility (DesignerSerializationVisibility.Hidden)]
		public override RightToLeft RightToLeft {
			get => base.RightToLeft;
			set => base.RightToLeft = value;
		}

		[Browsable (false)]
		[EditorBrowsable (EditorBrowsableState.Never)]
		public new event EventHandler BackColorChanged {
			add => throw new NotSupportedException (string.Format (InvalidEvent, "BackColorChanged"));
			remove { }
		}

		[Browsable (false)]
		[EditorBrowsable (EditorBrowsableState.Never)]
		public new event EventHandler BackgroundImageChanged {
			add => throw new NotSupportedException (string.Format (InvalidEvent, "BackgroundImageChanged"));
			remove { }
		}

		[Browsable (false)]
		[EditorBrowsable (EditorBrowsableState.Never)]
		public new event EventHandler BackgroundImageLayoutChanged {
			add => throw new NotSupportedException (string.Format (InvalidEvent, "BackgroundImageLayoutChanged"));
			remove { }
		}

		[Browsable (false)]
		[EditorBrowsable (EditorBrowsableState.Never)]
		public new event EventHandler BindingContextChanged {
			add => throw new NotSupportedException (string.Format (InvalidEvent, "BindingContextChanged"));
			remove { }
		}

		[Browsable (false)]
		[EditorBrowsable (EditorBrowsableState.Never)]
		public new event UICuesEventHandler ChangeUICues {
			add => throw new NotSupportedException (string.Format (InvalidEvent, "ChangeUICues"));
			remove { }
		}

		[Browsable (false)]
		[EditorBrowsable (EditorBrowsableState.Never)]
		public new event EventHandler Click {
			add => throw new NotSupportedException (string.Format (InvalidEvent, "Click"));
			remove { }
		}

		[Browsable (false)]
		[EditorBrowsable (EditorBrowsableState.Never)]
		public new event EventHandler CursorChanged {
			add => throw new NotSupportedException (string.Format (InvalidEvent, "CursorChanged"));
			remove { }
		}

		[Browsable (false)]
		[EditorBrowsable (EditorBrowsableState.Never)]
		public new event EventHandler DoubleClick {
			add => throw new NotSupportedException (string.Format (InvalidEvent, "DoubleClick"));
			remove { }
		}

		[Browsable (false)]
		[EditorBrowsable (EditorBrowsableState.Never)]
		public new event DragEventHandler DragDrop {
			add => throw new NotSupportedException (string.Format (InvalidEvent, "DragDrop"));
			remove { }
		}

		[Browsable (false)]
		[EditorBrowsable (EditorBrowsableState.Never)]
		public new event DragEventHandler DragEnter {
			add => throw new NotSupportedException (string.Format (InvalidEvent, "DragEnter"));
			remove { }
		}

		[Browsable (false)]
		[EditorBrowsable (EditorBrowsableState.Never)]
		public new event EventHandler DragLeave {
			add => throw new NotSupportedException (string.Format (InvalidEvent, "DragLeave"));
			remove { }
		}

		[Browsable (false)]
		[EditorBrowsable (EditorBrowsableState.Never)]
		public new event DragEventHandler DragOver {
			add => throw new NotSupportedException (string.Format (InvalidEvent, "DragOver"));
			remove { }
		}

		[Browsable (false)]
		[EditorBrowsable (EditorBrowsableState.Never)]
		public new event EventHandler EnabledChanged {
			add => throw new NotSupportedException (string.Format (InvalidEvent, "EnabledChanged"));
			remove { }
		}

		[Browsable (false)]
		[EditorBrowsable (EditorBrowsableState.Never)]
		public new event EventHandler Enter {
			add => throw new NotSupportedException (string.Format (InvalidEvent, "Enter"));
			remove { }
		}

		[Browsable (false)]
		[EditorBrowsable (EditorBrowsableState.Never)]
		public new event EventHandler FontChanged {
			add => throw new NotSupportedException (string.Format (InvalidEvent, "FontChanged"));
			remove { }
		}

		[Browsable (false)]
		[EditorBrowsable (EditorBrowsableState.Never)]
		public new event EventHandler ForeColorChanged {
			add => throw new NotSupportedException (string.Format (InvalidEvent, "ForeColorChanged"));
			remove { }
		}

		[Browsable (false)]
		[EditorBrowsable (EditorBrowsableState.Never)]
		public new event GiveFeedbackEventHandler GiveFeedback {
			add => throw new NotSupportedException (string.Format (InvalidEvent, "GiveFeedback"));
			remove { }
		}

		[Browsable (false)]
		[EditorBrowsable (EditorBrowsableState.Never)]
		public new event HelpEventHandler HelpRequested {
			add => throw new NotSupportedException (string.Format (InvalidEvent, "HelpRequested"));
			remove { }
		}

		[Browsable (false)]
		[EditorBrowsable (EditorBrowsableState.Never)]
		public new event EventHandler ImeModeChanged {
			add => throw new NotSupportedException (string.Format (InvalidEvent, "ImeModeChanged"));
			remove { }
		}

		[Browsable (false)]
		[EditorBrowsable (EditorBrowsableState.Never)]
		public new event KeyEventHandler KeyDown {
			add => throw new NotSupportedException (string.Format (InvalidEvent, "KeyDown"));
			remove { }
		}

		[Browsable (false)]
		[EditorBrowsable (EditorBrowsableState.Never)]
		public new event KeyPressEventHandler KeyPress {
			add => throw new NotSupportedException (string.Format (InvalidEvent, "KeyPress"));
			remove { }
		}

		[Browsable (false)]
		[EditorBrowsable (EditorBrowsableState.Never)]
		public new event KeyEventHandler KeyUp {
			add => throw new NotSupportedException (string.Format (InvalidEvent, "KeyUp"));
			remove { }
		}

		[Browsable (false)]
		[EditorBrowsable (EditorBrowsableState.Never)]
		public new event LayoutEventHandler Layout {
			add => throw new NotSupportedException (string.Format (InvalidEvent, "Layout"));
			remove { }
		}

		[Browsable (false)]
		[EditorBrowsable (EditorBrowsableState.Never)]
		public new event EventHandler Leave {
			add => throw new NotSupportedException (string.Format (InvalidEvent, "Leave"));
			remove { }
		}

		[Browsable (false)]
		[EditorBrowsable (EditorBrowsableState.Never)]
		public new event EventHandler MouseCaptureChanged {
			add => throw new NotSupportedException (string.Format (InvalidEvent, "MouseCaptureChanged"));
			remove { }
		}

		[Browsable (false)]
		[EditorBrowsable (EditorBrowsableState.Never)]
		public new event MouseEventHandler MouseClick {
			add => throw new NotSupportedException (string.Format (InvalidEvent, "MouseClick"));
			remove { }
		}

		[Browsable (false)]
		[EditorBrowsable (EditorBrowsableState.Never)]
		public new event MouseEventHandler MouseDoubleClick {
			add => throw new NotSupportedException (string.Format (InvalidEvent, "MouseDoubleClick"));
			remove { }
		}

		[Browsable (false)]
		[EditorBrowsable (EditorBrowsableState.Never)]
		public new event MouseEventHandler MouseDown {
			add => throw new NotSupportedException (string.Format (InvalidEvent, "MouseDown"));
			remove { }
		}

		[Browsable (false)]
		[EditorBrowsable (EditorBrowsableState.Never)]
		public new event EventHandler MouseEnter {
			add => throw new NotSupportedException (string.Format (InvalidEvent, "MouseEnter"));
			remove { }
		}

		[Browsable (false)]
		[EditorBrowsable (EditorBrowsableState.Never)]
		public new event EventHandler MouseHover {
			add => throw new NotSupportedException (string.Format (InvalidEvent, "MouseHover"));
			remove { }
		}

		[Browsable (false)]
		[EditorBrowsable (EditorBrowsableState.Never)]
		public new event EventHandler MouseLeave {
			add => throw new NotSupportedException (string.Format (InvalidEvent, "MouseLeave"));
			remove { }
		}

		[Browsable (false)]
		[EditorBrowsable (EditorBrowsableState.Never)]
		public new event MouseEventHandler MouseMove {
			add => throw new NotSupportedException (string.Format (InvalidEvent, "MouseMove"));
			remove { }
		}

		[Browsable (false)]
		[EditorBrowsable (EditorBrowsableState.Never)]
		public new event MouseEventHandler MouseUp {
			add => throw new NotSupportedException (string.Format (InvalidEvent, "MouseUp"));
			remove { }
		}

		[Browsable (false)]
		[EditorBrowsable (EditorBrowsableState.Never)]
		public new event MouseEventHandler MouseWheel {
			add => throw new NotSupportedException (string.Format (InvalidEvent, "MouseWheel"));
			remove { }
		}

		[Browsable (false)]
		[EditorBrowsable (EditorBrowsableState.Never)]
		public new event PaintEventHandler Paint {
			add => throw new NotSupportedException (string.Format (InvalidEvent, "Paint"));
			remove { }
		}

		[Browsable (false)]
		[EditorBrowsable (EditorBrowsableState.Never)]
		public new event QueryAccessibilityHelpEventHandler QueryAccessibilityHelp {
			add => throw new NotSupportedException (string.Format (InvalidEvent, "QueryAccessibilityHelp"));
			remove { }
		}

		[Browsable (false)]
		[EditorBrowsable (EditorBrowsableState.Never)]
		public new event QueryContinueDragEventHandler QueryContinueDrag {
			add => throw new NotSupportedException (string.Format (InvalidEvent, "QueryContinueDrag"));
			remove { }
		}

		[Browsable (false)]
		[EditorBrowsable (EditorBrowsableState.Never)]
		public new event EventHandler RightToLeftChanged {
			add => throw new NotSupportedException (string.Format (InvalidEvent, "RightToLeftChanged"));
			remove { }
		}

		[Browsable (false)]
		[EditorBrowsable (EditorBrowsableState.Never)]
		public new event EventHandler StyleChanged {
			add => throw new NotSupportedException (string.Format (InvalidEvent, "StyleChanged"));
			remove { }
		}

		[Browsable (false)]
		[EditorBrowsable (EditorBrowsableState.Never)]
		public new event EventHandler TextChanged {
			add => throw new NotSupportedException (string.Format (InvalidEvent, "TextChanged"));
			remove { }
		}

	}

	/// <summary>The site a browser control gives its browser.</summary>
	public class WebBrowserSiteBase : IDisposable
	{
		internal WebBrowserSiteBase (WebBrowserBase host) => Host = host;

		internal WebBrowserBase Host { get; }

		public void Dispose ()
		{
			Dispose (true);
			GC.SuppressFinalize (this);
		}

		protected virtual void Dispose (bool disposing)
		{
		}
	}

	public partial class WebBrowser
	{
		/// <summary>WebBrowser's site (.NET's: the DocHostUIHandler that maps window.external).</summary>
		protected class WebBrowserSite : WebBrowserSiteBase
		{
			public WebBrowserSite (WebBrowser host) : base (host)
			{
			}
		}
	}
}
