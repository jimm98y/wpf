// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
//
// Ported from dotnet/winforms (Design/Behavior/ControlBodyGlyph.cs).

using System.ComponentModel;
using System.Drawing;

namespace System.Windows.Forms.Design.Behavior
{
	public class ControlBodyGlyph : ComponentGlyph
	{
		private readonly Rectangle _bounds;          // bounds of the related control
		private readonly Cursor _hitTestCursor;      // cursor used to hit test
		private readonly IComponent _component;

		public ControlBodyGlyph (Rectangle bounds, Cursor cursor, IComponent relatedComponent, ControlDesigner designer)
			: base (relatedComponent, new ControlDesigner.TransparentBehavior (designer))
		{
			_bounds = bounds;
			_hitTestCursor = cursor;
			_component = relatedComponent;
		}

		public ControlBodyGlyph (Rectangle bounds, Cursor cursor, IComponent relatedComponent, Behavior behavior)
			: base (relatedComponent, behavior)
		{
			_bounds = bounds;
			_hitTestCursor = cursor;
			_component = relatedComponent;
		}

		public override Rectangle Bounds => _bounds;

		public override Cursor GetHitTest (Point p)
		{
			// Non-controls are always visible here.
			bool isVisible = !(_component is Control control) || control.Visible;
			return isVisible && _bounds.Contains (p) ? _hitTestCursor : null;
		}
	}
}
