// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
//
// Ported from dotnet/winforms (Design/Behavior/BehaviorService.cs and BehaviorService.AdornerWindow.cs).
//
// The behavior stack, the capture behavior, the adorners and the glyph hit test/paint/mouse routing are .NET's.
// What differs is how the adorner window sits over the design surface: .NET pushes it through the DesignerFrame's
// IOverlayService and steals the designed controls' mouse with a thread WH_MOUSE hook. Here it is a transparent
// child laid over the root designer's view (the same place, the same coordinates), which answers WM_NCHITTEST with
// HTTRANSPARENT wherever no glyph wants the pointer -- the mouse then reaches the designed control under it, as it
// does in .NET when the hook lets a message through -- and takes the mouse itself over a glyph that wants it.
// The Mono stub threw from every member, so ControlDesigner.BehaviorService and everything built on it threw too.

using System.Collections.Generic;
using System.ComponentModel.Design;
using System.Drawing;
using System.Drawing.Design;

namespace System.Windows.Forms.Design.Behavior
{
	public sealed class BehaviorService : IDisposable
	{
		private readonly IServiceProvider _serviceProvider;
		private readonly AdornerWindow _adornerWindow;
		private readonly List<Behavior> _behaviorStack = new List<Behavior> ();
		private Behavior _captureBehavior;
		private Glyph _hitTestedGlyph;
		private IToolboxService _toolboxSvc;
		private DragEventArgs _validDragArgs;
		private readonly HashSet<Glyph> _dragEnterReplies = new HashSet<Glyph> ();

		private BehaviorDragDropEventHandler _beginDragHandler;
		private BehaviorDragDropEventHandler _endDragHandler;
		private EventHandler _synchronizeEventHandler;

		internal const string ToolboxFormat = ".NET Toolbox Item";

		/// <summary>A behavior service whose adorner window covers <paramref name="frame"/>: the control the
		/// root designer shows the design surface in.</summary>
		internal BehaviorService (IServiceProvider serviceProvider, Control frame)
		{
			_serviceProvider = serviceProvider;
			_adornerWindow = new AdornerWindow (this, frame);
			Adorners = new BehaviorServiceAdornerCollection (this);
			if (frame != null) {
				frame.Controls.Add (_adornerWindow);
				_adornerWindow.BringToFront ();
			}
		}

		public BehaviorServiceAdornerCollection Adorners { get; }

		internal Control AdornerWindowControl => _adornerWindow;

		internal bool HasCapture => _captureBehavior != null;

		internal bool Dragging { get; private set; }

		internal bool CancelDrag { get; set; }

		public Graphics AdornerWindowGraphics {
			get {
				Graphics result = _adornerWindow.CreateGraphics ();
				result.Clip = new Region (_adornerWindow.DesignerFrameDisplayRectangle);
				return result;
			}
		}

		public Behavior CurrentBehavior => _behaviorStack.Count > 0 ? _behaviorStack [0] : null;

		public void Dispose ()
		{
			_adornerWindow.Dispose ();
		}

		public Point AdornerWindowPointToScreen (Point p) => _adornerWindow.PointToScreen (p);

		public Point AdornerWindowToScreen () => AdornerWindowPointToScreen (new Point (0, 0));

		public Point ControlToAdornerWindow (Control c)
		{
			if (c.Parent == null)
				return Point.Empty;
			Point pt = _adornerWindow.PointToClient (c.Parent.PointToScreen (new Point (c.Left, c.Top)));
			if (c.Parent.IsMirrored)
				pt.X -= c.Width;
			return pt;
		}

		public Point MapAdornerWindowPoint (IntPtr handle, Point pt)
		{
			Control from = Control.FromHandle (handle);
			if (from == null)
				return pt;
			return _adornerWindow.PointToClient (from.PointToScreen (pt));
		}

		public Rectangle ControlRectInAdornerWindow (Control c)
		{
			if (c.Parent == null)
				return Rectangle.Empty;
			return new Rectangle (ControlToAdornerWindow (c), c.Size);
		}

		public event BehaviorDragDropEventHandler BeginDrag {
			add => _beginDragHandler += value;
			remove => _beginDragHandler -= value;
		}

		public event BehaviorDragDropEventHandler EndDrag {
			add => _endDragHandler += value;
			remove => _endDragHandler -= value;
		}

		public event EventHandler Synchronize {
			add => _synchronizeEventHandler += value;
			remove => _synchronizeEventHandler -= value;
		}

		internal void OnBeginDrag (BehaviorDragDropEventArgs e)
		{
			Dragging = true;
			CancelDrag = false;
			_dragEnterReplies.Clear ();
			_beginDragHandler?.Invoke (this, e);
		}

		internal void OnEndDrag (BehaviorDragDropEventArgs e)
		{
			_adornerWindow.ProcessingDrag = false;
			_validDragArgs = null;
			Dragging = false;
			CancelDrag = false;
			_endDragHandler?.Invoke (this, e);
		}

		public Behavior GetNextBehavior (Behavior behavior)
		{
			if (_behaviorStack.Count > 0) {
				int index = _behaviorStack.IndexOf (behavior);
				if (index != -1 && index < _behaviorStack.Count - 1)
					return _behaviorStack [index + 1];
			}
			return null;
		}

		internal void EnableAllAdorners (bool enabled)
		{
			foreach (Adorner adorner in Adorners)
				adorner.EnabledInternal = enabled;
			Invalidate ();
		}

		public void Invalidate () => _adornerWindow.InvalidateAdornerWindow ();

		public void Invalidate (Rectangle rect) => _adornerWindow.InvalidateAdornerWindow (rect);

		public void Invalidate (Region r) => _adornerWindow.InvalidateAdornerWindow (r);

		public void SyncSelection () => _synchronizeEventHandler?.Invoke (this, EventArgs.Empty);

		public Behavior PopBehavior (Behavior behavior)
		{
			if (_behaviorStack.Count == 0)
				throw new InvalidOperationException ();
			int index = _behaviorStack.IndexOf (behavior);
			if (index == -1)
				return null;
			_behaviorStack.RemoveAt (index);
			if (behavior == _captureBehavior) {
				_adornerWindow.Capture = false;
				// Defensive, as .NET: the window should have heard WM_CAPTURECHANGED.
				if (_captureBehavior != null)
					OnLoseCapture ();
			}
			return behavior;
		}

		public void PushBehavior (Behavior behavior)
		{
			if (behavior == null)
				throw new ArgumentNullException (nameof (behavior));
			_behaviorStack.Insert (0, behavior);
			// A capture behavior that is not this one no longer has the capture.
			if (_captureBehavior != null && _captureBehavior != behavior)
				OnLoseCapture ();
		}

		public void PushCaptureBehavior (Behavior behavior)
		{
			PushBehavior (behavior);
			_captureBehavior = behavior;
			_adornerWindow.Capture = true;
		}

		public Point ScreenToAdornerWindow (Point p) => _adornerWindow.PointToClient (p);

		internal void OnLoseCapture ()
		{
			if (_captureBehavior == null)
				return;
			Behavior b = _captureBehavior;
			_captureBehavior = null;
			try {
				b.OnLoseCapture (_hitTestedGlyph, EventArgs.Empty);
			} catch {
			}
		}

		/// <summary>Hit tests every enabled adorner's glyphs, topmost adorner first, fires the glyph enter/leave
		/// events and sets the cursor. True when the point should go through to the window underneath: no glyph
		/// there, or a glyph of a transparent behavior (a control's body).</summary>
		internal bool PropagateHitTest (Point pt)
		{
			for (int i = Adorners.Count - 1; i >= 0; i--) {
				if (!Adorners [i].Enabled)
					continue;
				for (int j = 0; j < Adorners [i].Glyphs.Count; j++) {
					Cursor hitTestCursor = Adorners [i].Glyphs [j].GetHitTest (pt);
					if (hitTestCursor != null) {
						// Entering a glyph may change the selection and with it the glyphs: remember this one first.
						Glyph newGlyph = Adorners [i].Glyphs [j];
						InvokeMouseEnterLeave (_hitTestedGlyph, newGlyph);
						if (_validDragArgs == null)
							SetAppropriateCursor (hitTestCursor);
						_hitTestedGlyph = newGlyph;
						return _hitTestedGlyph.Behavior is ControlDesigner.TransparentBehavior;
					}
				}
			}

			InvokeMouseEnterLeave (_hitTestedGlyph, null);
			if (_validDragArgs == null) {
				Cursor cursor = Cursors.Default;
				if (_behaviorStack.Count > 0)
					cursor = _behaviorStack [0].Cursor;
				SetAppropriateCursor (cursor);
			}
			_hitTestedGlyph = null;
			return true;
		}

		internal MenuCommand FindCommand (CommandID commandID, IMenuCommandService menuService)
		{
			Behavior behavior = GetAppropriateBehavior (_hitTestedGlyph);
			if (behavior != null) {
				if (behavior.DisableAllCommands) {
					MenuCommand menuCommand = menuService.FindCommand (commandID);
					if (menuCommand != null)
						menuCommand.Enabled = false;
					return menuCommand;
				}
				MenuCommand interrupted = behavior.FindCommand (commandID);
				if (interrupted != null)
					return interrupted;
			}
			return menuService.FindCommand (commandID);
		}

		private Behavior GetAppropriateBehavior (Glyph g) => _behaviorStack.Count > 0 ? _behaviorStack [0] : g?.Behavior;

		private void SetAppropriateCursor (Cursor cursor)
		{
			// The default cursor lets the toolbox choose one, as in .NET.
			if (cursor == Cursors.Default) {
				_toolboxSvc ??= _serviceProvider?.GetService (typeof (IToolboxService)) as IToolboxService;
				if (_toolboxSvc != null && _toolboxSvc.SetCursor ())
					return;
			}
			_adornerWindow.Cursor = cursor;
		}

		private void InvokeMouseEnterLeave (Glyph leaveGlyph, Glyph enterGlyph)
		{
			if (leaveGlyph != null) {
				if (enterGlyph != null && leaveGlyph.Equals (enterGlyph))
					return;
				if (_validDragArgs != null)
					OnDragLeave (leaveGlyph, EventArgs.Empty);
				else
					OnMouseLeave (leaveGlyph);
			}
			if (enterGlyph != null) {
				if (_validDragArgs != null)
					OnDragEnter (enterGlyph, _validDragArgs);
				else
					OnMouseEnter (enterGlyph);
			}
		}

		private void OnDragEnter (Glyph g, DragEventArgs e)
		{
			g ??= _hitTestedGlyph;
			Behavior behavior = GetAppropriateBehavior (g);
			if (behavior == null)
				return;
			behavior.OnDragEnter (g, e);
			if (g is ControlBodyGlyph && e.Effect == DragDropEffects.None)
				_dragEnterReplies.Add (g);
		}

		private void OnDragLeave (Glyph g, EventArgs e)
		{
			_dragEnterReplies.Clear ();
			g ??= _hitTestedGlyph;
			GetAppropriateBehavior (g)?.OnDragLeave (g, e);
		}

		internal bool OnMouseDoubleClick (MouseButtons button, Point mouseLoc)
			=> GetAppropriateBehavior (_hitTestedGlyph)?.OnMouseDoubleClick (_hitTestedGlyph, button, mouseLoc) ?? false;

		internal bool OnMouseDown (MouseButtons button, Point mouseLoc)
			=> GetAppropriateBehavior (_hitTestedGlyph)?.OnMouseDown (_hitTestedGlyph, button, mouseLoc) ?? false;

		private bool OnMouseEnter (Glyph g) => GetAppropriateBehavior (g)?.OnMouseEnter (g) ?? false;

		internal bool OnMouseHover (Point mouseLoc)
			=> GetAppropriateBehavior (_hitTestedGlyph)?.OnMouseHover (_hitTestedGlyph, mouseLoc) ?? false;

		private bool OnMouseLeave (Glyph g) => GetAppropriateBehavior (g)?.OnMouseLeave (g) ?? false;

		internal bool OnMouseMove (MouseButtons button, Point mouseLoc)
			=> GetAppropriateBehavior (_hitTestedGlyph)?.OnMouseMove (_hitTestedGlyph, button, mouseLoc) ?? false;

		internal bool OnMouseUp (MouseButtons button)
		{
			_dragEnterReplies.Clear ();
			_validDragArgs = null;
			return GetAppropriateBehavior (_hitTestedGlyph)?.OnMouseUp (_hitTestedGlyph, button) ?? false;
		}

		private void OnDragDrop (DragEventArgs e)
		{
			_validDragArgs = null;
			GetAppropriateBehavior (_hitTestedGlyph)?.OnDragDrop (_hitTestedGlyph, e);
		}

		private void OnDragOver (DragEventArgs e)
		{
			_validDragArgs = e;
			Behavior behavior = GetAppropriateBehavior (_hitTestedGlyph);
			if (behavior == null) {
				e.Effect = DragDropEffects.None;
				return;
			}
			if (_hitTestedGlyph == null || !_dragEnterReplies.Contains (_hitTestedGlyph))
				behavior.OnDragOver (_hitTestedGlyph, e);
			else
				e.Effect = DragDropEffects.None;
		}

		private void OnGiveFeedback (GiveFeedbackEventArgs e)
			=> GetAppropriateBehavior (_hitTestedGlyph)?.OnGiveFeedback (_hitTestedGlyph, e);

		private void OnQueryContinueDrag (QueryContinueDragEventArgs e)
			=> GetAppropriateBehavior (_hitTestedGlyph)?.OnQueryContinueDrag (_hitTestedGlyph, e);

		/// <summary>Paints every enabled adorner's glyphs: adorners in order, each one's glyphs last to first.</summary>
		internal void PropagatePaint (PaintEventArgs pe)
		{
			for (int i = 0; i < Adorners.Count; i++) {
				if (!Adorners [i].Enabled)
					continue;
				for (int j = Adorners [i].Glyphs.Count - 1; j >= 0; j--)
					Adorners [i].Glyphs [j].Paint (pe);
			}
		}

		/// <summary>The transparent window every glyph is drawn on and hit tested in, over the design surface.</summary>
		private sealed class AdornerWindow : Control
		{
			private const int WM_NCHITTEST = 0x0084, WM_CAPTURECHANGED = 0x0215, WM_MOUSEHOVER = 0x02A1;
			private const int HTTRANSPARENT = -1, HTCLIENT = 1;

			private readonly BehaviorService _behaviorService;

			internal AdornerWindow (BehaviorService behaviorService, Control designerFrame)
			{
				_behaviorService = behaviorService;
				DesignerFrame = designerFrame;
				Dock = DockStyle.Fill;
				AllowDrop = true;
				Text = "AdornerWindow";
				// Nothing of its own is drawn: the design surface shows through wherever there is no glyph.
				SetStyle (ControlStyles.Opaque, true);
				SetStyle (ControlStyles.Selectable, false);
				TabStop = false;
			}

			protected override CreateParams CreateParams {
				get {
					CreateParams cp = base.CreateParams;
					cp.Style &= ~(0x02000000 | 0x04000000);   // WS_CLIPCHILDREN | WS_CLIPSIBLINGS
					cp.ExStyle |= 0x00000020;                 // WS_EX_TRANSPARENT
					return cp;
				}
			}

			internal bool ProcessingDrag { get; set; }

			internal Control DesignerFrame { get; private set; }

			internal Rectangle DesignerFrameDisplayRectangle => DesignerFrameValid ? DesignerFrame.DisplayRectangle : Rectangle.Empty;

			internal bool DesignerFrameValid => DesignerFrame != null && !DesignerFrame.IsDisposed && DesignerFrame.IsHandleCreated;

			protected override void Dispose (bool disposing)
			{
				if (disposing)
					DesignerFrame = null;
				base.Dispose (disposing);
			}

			internal void InvalidateAdornerWindow ()
			{
				if (DesignerFrameValid) {
					DesignerFrame.Invalidate (true);
					DesignerFrame.Update ();
				}
			}

			internal void InvalidateAdornerWindow (Region region)
			{
				if (DesignerFrameValid) {
					Point scrollPosition = DesignerFrame is ScrollableControl sc ? sc.AutoScrollPosition : Point.Empty;
					region.Translate (scrollPosition.X, scrollPosition.Y);
					DesignerFrame.Invalidate (region, true);
					DesignerFrame.Update ();
				}
			}

			internal void InvalidateAdornerWindow (Rectangle rectangle)
			{
				if (DesignerFrameValid) {
					Point scrollPosition = DesignerFrame is ScrollableControl sc ? sc.AutoScrollPosition : Point.Empty;
					rectangle.Offset (scrollPosition.X, scrollPosition.Y);
					DesignerFrame.Invalidate (rectangle, true);
					DesignerFrame.Update ();
				}
			}

			protected override void OnPaintBackground (PaintEventArgs pevent)
			{
				// Opaque, and see-through: no background at all.
			}

			protected override void OnPaint (PaintEventArgs e)
			{
				_behaviorService.PropagatePaint (e);
			}

			private static bool IsLocalDrag (DragEventArgs e)
			{
				string [] formats = e.Data?.GetFormats () ?? new string [0];
				foreach (string f in formats)
					if (string.Equals (ToolboxFormat, f))
						return true;
				return false;
			}

			protected override void OnDragDrop (DragEventArgs e)
			{
				try {
					_behaviorService.OnDragDrop (e);
				} finally {
					ProcessingDrag = false;
				}
			}

			protected override void OnDragEnter (DragEventArgs e)
			{
				ProcessingDrag = true;
				if (!IsLocalDrag (e)) {
					_behaviorService._validDragArgs = e;
					_behaviorService.PropagateHitTest (PointToClient (new Point (e.X, e.Y)));
				}
				_behaviorService.OnDragEnter (null, e);
			}

			protected override void OnDragLeave (EventArgs e)
			{
				_behaviorService._validDragArgs = null;
				try {
					_behaviorService.OnDragLeave (null, e);
				} finally {
					ProcessingDrag = false;
				}
			}

			protected override void OnDragOver (DragEventArgs e)
			{
				ProcessingDrag = true;
				if (!IsLocalDrag (e)) {
					_behaviorService._validDragArgs = e;
					_behaviorService.PropagateHitTest (PointToClient (new Point (e.X, e.Y)));
				}
				_behaviorService.OnDragOver (e);
			}

			protected override void OnGiveFeedback (GiveFeedbackEventArgs e) => _behaviorService.OnGiveFeedback (e);

			protected override void OnQueryContinueDrag (QueryContinueDragEventArgs e) => _behaviorService.OnQueryContinueDrag (e);

			protected override void WndProc (ref Message m)
			{
				switch (m.Msg) {
				case WM_NCHITTEST: {
					long lp = (long) m.LParam;
					Point pt = PointToClient (new Point ((short) (lp & 0xFFFF), (short) ((lp >> 16) & 0xFFFF)));
					m.Result = _behaviorService.PropagateHitTest (pt) && !ProcessingDrag ? (IntPtr) HTTRANSPARENT : (IntPtr) HTCLIENT;
					return;
				}
				case WM_CAPTURECHANGED:
					base.WndProc (ref m);
					_behaviorService.OnLoseCapture ();
					return;
				}
				// A glyph that wants the pointer gets its mouse here (.NET reaches the same place through its
				// mouse hook's WndProcProxy); a behavior that handles a message keeps it from the control.
				if (m.Msg >= 0x0200 && m.Msg <= 0x0209 || m.Msg == WM_MOUSEHOVER) {
					long lp = (long) m.LParam;
					if (!Proxy (m.Msg, new Point ((short) (lp & 0xFFFF), (short) ((lp >> 16) & 0xFFFF))))
						return;
				}
				base.WndProc (ref m);
			}

			/// <summary>False when a behavior handled the message.</summary>
			private bool Proxy (int msg, Point mouseLoc)
			{
				_behaviorService.PropagateHitTest (mouseLoc);
				switch (msg) {
				case 0x0201: return !_behaviorService.OnMouseDown (MouseButtons.Left, mouseLoc);
				case 0x0204: return !_behaviorService.OnMouseDown (MouseButtons.Right, mouseLoc);
				case 0x0200: return !_behaviorService.OnMouseMove (Control.MouseButtons, mouseLoc);
				case 0x0202: return !_behaviorService.OnMouseUp (MouseButtons.Left);
				case 0x0205: return !_behaviorService.OnMouseUp (MouseButtons.Right);
				case WM_MOUSEHOVER: return !_behaviorService.OnMouseHover (mouseLoc);
				case 0x0203: return !_behaviorService.OnMouseDoubleClick (MouseButtons.Left, mouseLoc);
				case 0x0206: return !_behaviorService.OnMouseDoubleClick (MouseButtons.Right, mouseLoc);
				}
				return true;
			}
		}
	}
}
