// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Windows.Forms;

namespace System.Drawing.Design
{
	public partial class ColorEditor
	{
		private partial class ColorPalette
		{
			public partial class ColorPaletteAccessibleObject
			{
				public class ColorCellAccessibleObject : AccessibleObject
				{
					private readonly Color _color;
					private readonly ColorPaletteAccessibleObject _parent;
					private readonly int _cell;

					public ColorCellAccessibleObject (ColorPaletteAccessibleObject parent, Color color, int cell)
					{
						_color = color;
						_parent = parent;
						_cell = cell;
					}

					public override Rectangle Bounds {
						get {
							Point cellPt = Get2DFrom1D (_cell);
							Rectangle rect = default (Rectangle);
							FillRectWithCellBounds (cellPt.X, cellPt.Y, ref rect);

							// Translate rect to screen coordinates (.NET: ClientToScreen)
							Point pt = new Point (rect.X, rect.Y);

							ColorPalette palette = _parent.ColorPalette;
							if (palette != null && palette.IsHandleCreated)
								pt = palette.PointToScreen (pt);

							return new Rectangle (pt.X, pt.Y, rect.Width, rect.Height);
						}
					}

					public override string Name {
						get { return _color.ToString (); }
					}

					public override AccessibleObject Parent {
						get { return _parent; }
					}

					public override AccessibleRole Role {
						get { return AccessibleRole.Cell; }
					}

					public override AccessibleStates State {
						get {
							AccessibleStates state = base.State;
							ColorPalette palette = _parent.ColorPalette;
							if (palette != null && _cell == palette.FocusedCell)
								state |= AccessibleStates.Focused;

							return state;
						}
					}

					public override string Value {
						get { return _color.ToString (); }
					}
				}
			}
		}
	}
}
