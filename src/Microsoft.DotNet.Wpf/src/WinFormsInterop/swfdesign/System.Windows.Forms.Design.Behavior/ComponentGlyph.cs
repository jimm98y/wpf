// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
//
// Ported from dotnet/winforms (Design/Behavior/ComponentGlyph.cs).

using System.ComponentModel;
using System.Drawing;

namespace System.Windows.Forms.Design.Behavior
{
	public class ComponentGlyph : Glyph
	{
		private readonly IComponent _relatedComponent;

		public ComponentGlyph (IComponent relatedComponent, Behavior behavior)
			: base (behavior)
		{
			_relatedComponent = relatedComponent;
		}

		public ComponentGlyph (IComponent relatedComponent)
			: base (null)
		{
			_relatedComponent = relatedComponent;
		}

		public IComponent RelatedComponent => _relatedComponent;

		public override Cursor GetHitTest (Point p) => null;

		public override void Paint (PaintEventArgs pe)
		{
		}
	}
}
