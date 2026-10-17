// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
//
// Ported from dotnet/winforms (Design/Behavior/Glyph.cs). The Mono stub's constructor called a SetBehavior that
// threw, so no glyph of any kind could be made.

using System.Drawing;

namespace System.Windows.Forms.Design.Behavior
{
	public abstract class Glyph
	{
		private Behavior _behavior;

		protected Glyph (Behavior behavior)
		{
			_behavior = behavior;
		}

		public virtual Behavior Behavior => _behavior;

		public virtual Rectangle Bounds => Rectangle.Empty;

		public abstract Cursor GetHitTest (Point p);

		public abstract void Paint (PaintEventArgs pe);

		protected void SetBehavior (Behavior behavior)
		{
			_behavior = behavior;
		}
	}
}
