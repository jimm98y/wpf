// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
//
// Ported from dotnet/winforms (Design/Behavior/Adorner.cs).

using System.Drawing;

namespace System.Windows.Forms.Design.Behavior
{
	public sealed class Adorner
	{
		private readonly GlyphCollection _glyphs = new GlyphCollection ();

		public Adorner ()
		{
			EnabledInternal = true;
		}

		public BehaviorService BehaviorService { get; set; }

		public bool Enabled {
			get => EnabledInternal;
			set {
				if (value != EnabledInternal) {
					EnabledInternal = value;
					Invalidate ();
				}
			}
		}

		internal bool EnabledInternal { get; set; }

		public GlyphCollection Glyphs => _glyphs;

		public void Invalidate () => BehaviorService?.Invalidate ();

		public void Invalidate (Rectangle rectangle) => BehaviorService?.Invalidate (rectangle);

		public void Invalidate (Region region) => BehaviorService?.Invalidate (region);
	}
}
