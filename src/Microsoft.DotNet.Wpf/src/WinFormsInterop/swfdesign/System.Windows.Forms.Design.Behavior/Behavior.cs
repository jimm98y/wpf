// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
//
// Ported from dotnet/winforms (src/System.Windows.Forms.Design/src/System/Windows/Forms/Design/Behavior/Behavior.cs).
// Every virtual defers to the next behavior on the BehaviorService's stack (callParentBehavior) or to the behavior
// of the glyph it was given, and otherwise does nothing. The Mono stub threw from every one of them.

using System.ComponentModel.Design;
using System.Drawing;

namespace System.Windows.Forms.Design.Behavior
{
	public abstract class Behavior
	{
		private readonly bool _callParentBehavior;
		private readonly BehaviorService _behaviorService;

		protected Behavior ()
		{
		}

		protected Behavior (bool callParentBehavior, BehaviorService behaviorService)
		{
			if (callParentBehavior && behaviorService == null)
				throw new ArgumentException (null, nameof (behaviorService));
			_callParentBehavior = callParentBehavior;
			_behaviorService = behaviorService;
		}

		private Behavior Next => _callParentBehavior ? _behaviorService?.GetNextBehavior (this) : null;

		public virtual Cursor Cursor => Cursors.Default;

		public virtual bool DisableAllCommands => Next is Behavior next && next.DisableAllCommands;

		public virtual MenuCommand FindCommand (CommandID commandId)
		{
			try {
				return Next?.FindCommand (commandId);
			} catch {
				// As .NET: any exception means "no command".
				return null;
			}
		}

		private Behavior GetGlyphBehavior (Glyph g) => g?.Behavior != null && g.Behavior != this ? g.Behavior : null;

		public virtual void OnLoseCapture (Glyph g, EventArgs e)
		{
			if (Next is Behavior next) next.OnLoseCapture (g, e);
			else GetGlyphBehavior (g)?.OnLoseCapture (g, e);
		}

		public virtual bool OnMouseDoubleClick (Glyph g, MouseButtons button, Point mouseLoc)
		{
			if (Next is Behavior next) return next.OnMouseDoubleClick (g, button, mouseLoc);
			return GetGlyphBehavior (g)?.OnMouseDoubleClick (g, button, mouseLoc) ?? false;
		}

		public virtual bool OnMouseDown (Glyph g, MouseButtons button, Point mouseLoc)
		{
			if (Next is Behavior next) return next.OnMouseDown (g, button, mouseLoc);
			return GetGlyphBehavior (g)?.OnMouseDown (g, button, mouseLoc) ?? false;
		}

		public virtual bool OnMouseEnter (Glyph g)
		{
			if (Next is Behavior next) return next.OnMouseEnter (g);
			return GetGlyphBehavior (g)?.OnMouseEnter (g) ?? false;
		}

		public virtual bool OnMouseHover (Glyph g, Point mouseLoc)
		{
			if (Next is Behavior next) return next.OnMouseHover (g, mouseLoc);
			return GetGlyphBehavior (g)?.OnMouseHover (g, mouseLoc) ?? false;
		}

		public virtual bool OnMouseLeave (Glyph g)
		{
			if (Next is Behavior next) return next.OnMouseLeave (g);
			return GetGlyphBehavior (g)?.OnMouseLeave (g) ?? false;
		}

		public virtual bool OnMouseMove (Glyph g, MouseButtons button, Point mouseLoc)
		{
			if (Next is Behavior next) return next.OnMouseMove (g, button, mouseLoc);
			return GetGlyphBehavior (g)?.OnMouseMove (g, button, mouseLoc) ?? false;
		}

		public virtual bool OnMouseUp (Glyph g, MouseButtons button)
		{
			if (Next is Behavior next) return next.OnMouseUp (g, button);
			return GetGlyphBehavior (g)?.OnMouseUp (g, button) ?? false;
		}

		public virtual void OnDragDrop (Glyph g, DragEventArgs e)
		{
			if (Next is Behavior next) next.OnDragDrop (g, e);
			else GetGlyphBehavior (g)?.OnDragDrop (g, e);
		}

		public virtual void OnDragEnter (Glyph g, DragEventArgs e)
		{
			if (Next is Behavior next) next.OnDragEnter (g, e);
			else GetGlyphBehavior (g)?.OnDragEnter (g, e);
		}

		public virtual void OnDragLeave (Glyph g, EventArgs e)
		{
			if (Next is Behavior next) next.OnDragLeave (g, e);
			else GetGlyphBehavior (g)?.OnDragLeave (g, e);
		}

		public virtual void OnDragOver (Glyph g, DragEventArgs e)
		{
			if (Next is Behavior next) next.OnDragOver (g, e);
			else if (GetGlyphBehavior (g) is Behavior behavior) behavior.OnDragOver (g, e);
			else if (e.Effect != DragDropEffects.None)
				e.Effect = Control.ModifierKeys == Keys.Control ? DragDropEffects.Copy : DragDropEffects.Move;
		}

		public virtual void OnGiveFeedback (Glyph g, GiveFeedbackEventArgs e)
		{
			if (Next is Behavior next) next.OnGiveFeedback (g, e);
			else GetGlyphBehavior (g)?.OnGiveFeedback (g, e);
		}

		public virtual void OnQueryContinueDrag (Glyph g, QueryContinueDragEventArgs e)
		{
			if (Next is Behavior next) next.OnQueryContinueDrag (g, e);
			else GetGlyphBehavior (g)?.OnQueryContinueDrag (g, e);
		}
	}
}
