// The palette page of the colour picker: eight by eight cells, the first forty-eight fixed and the
// last sixteen left for colours the user mixes.

using System.Windows.Forms;

namespace System.Drawing.Design
{
	public partial class ColorEditor
	{
		internal class ColorPalette : Control
		{
			internal const int CellsAcross = 8;
			internal const int CellsDown = 8;
			internal const int CellsCustom = 16;            // the last row and the one above it
			internal const int TotalCells = CellsAcross * CellsDown;
			internal const int CellSize = 16;
			internal const int MarginWidth = 8;

			// Read off the stock palette, in OLE order (0x00bbggrr).
			private static readonly int[] StaticCells = new int[] {
				0x00ffffff, 0x00c0c0ff, 0x00c0e0ff, 0x00c0ffff,
				0x00c0ffc0, 0x00ffffc0, 0x00ffc0c0, 0x00ffc0ff,

				0x00e0e0e0, 0x008080ff, 0x0080c0ff, 0x0080ffff,
				0x0080ff80, 0x00ffff80, 0x00ff8080, 0x00ff80ff,

				0x00c0c0c0, 0x000000ff, 0x000080ff, 0x0000ffff,
				0x0000ff00, 0x00ffff00, 0x00ff0000, 0x00ff00ff,

				0x00808080, 0x000000c0, 0x000040c0, 0x0000c0c0,
				0x0000c000, 0x00c0c000, 0x00c00000, 0x00c000c0,

				0x00404040, 0x00000080, 0x00004080, 0x00008080,
				0x00008000, 0x00808000, 0x00800000, 0x00800080,

				0x00000000, 0x00000040, 0x00404080, 0x00004040,
				0x00004000, 0x00404000, 0x00400000, 0x00400040
			};

			private readonly Color[] static_colours;
			private readonly Color[] custom_colours;
			private Color selected_colour;
			private Point focus_cell;

			internal event EventHandler Picked;

			internal ColorPalette (Color[] customColours)
			{
				static_colours = new Color[TotalCells - CellsCustom];
				for (int i = 0; i < StaticCells.Length; i++)
					static_colours[i] = ColorTranslator.FromOle (StaticCells[i]);
				custom_colours = customColours;

				SetStyle (ControlStyles.Opaque | ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint
					  | ControlStyles.Selectable, true);
				BackColor = SystemColors.Control;
				Size = new Size (CellsAcross * (CellSize + MarginWidth) + MarginWidth + 2,
						 CellsDown * (CellSize + MarginWidth) + MarginWidth + 2);
			}

			internal Color SelectedColor {
				get { return selected_colour; }
				set {
					if (value == selected_colour)
						return;
					selected_colour = value;
					focus_cell = CellOf (value);
					Invalidate ();
				}
			}

			internal Color ColorAt (int cell)
			{
				if (cell < 0 || cell >= TotalCells)
					return Color.Empty;
				return cell < TotalCells - CellsCustom
					? static_colours[cell]
					: custom_colours[cell - (TotalCells - CellsCustom)];
			}

			private Point CellOf (Color colour)
			{
				for (int i = 0; i < TotalCells; i++)
					if (ColorAt (i).ToArgb () == colour.ToArgb ())
						return new Point (i % CellsAcross, i / CellsAcross);
				return new Point (-1, -1);
			}

			private static Rectangle CellBounds (int across, int down)
			{
				return new Rectangle (MarginWidth + across * (CellSize + MarginWidth),
						      MarginWidth + down * (CellSize + MarginWidth),
						      CellSize, CellSize);
			}

			private int CellAt (Point point)
			{
				for (int down = 0; down < CellsDown; down++)
					for (int across = 0; across < CellsAcross; across++) {
						// The margin around a cell belongs to it, so the whole grid is live
						// rather than only the coloured squares.
						Rectangle live = Rectangle.Inflate (CellBounds (across, down),
										    MarginWidth / 2, MarginWidth / 2);
						if (live.Contains (point))
							return across + down * CellsAcross;
					}
				return -1;
			}

			protected override void OnMouseDown (MouseEventArgs e)
			{
				base.OnMouseDown (e);
				int cell = CellAt (new Point (e.X, e.Y));
				if (cell < 0)
					return;
				Focus ();
				focus_cell = new Point (cell % CellsAcross, cell / CellsAcross);
				selected_colour = ColorAt (cell);
				Invalidate ();
				OnPicked ();
			}

			protected override bool IsInputKey (Keys keyData)
			{
				switch (keyData & Keys.KeyCode) {
				case Keys.Left:
				case Keys.Right:
				case Keys.Up:
				case Keys.Down:
				case Keys.Return:
				case Keys.Space:
					return true;
				}
				return base.IsInputKey (keyData);
			}

			protected override void OnKeyDown (KeyEventArgs e)
			{
				base.OnKeyDown (e);
				int x = focus_cell.X < 0 ? 0 : focus_cell.X;
				int y = focus_cell.Y < 0 ? 0 : focus_cell.Y;
				switch (e.KeyCode) {
				case Keys.Left: x--; break;
				case Keys.Right: x++; break;
				case Keys.Up: y--; break;
				case Keys.Down: y++; break;
				case Keys.Return:
				case Keys.Space:
					selected_colour = ColorAt (x + y * CellsAcross);
					OnPicked ();
					return;
				default:
					return;
				}
				if (x < 0 || y < 0 || x >= CellsAcross || y >= CellsDown)
					return;
				focus_cell = new Point (x, y);
				selected_colour = ColorAt (x + y * CellsAcross);
				Invalidate ();
			}

			private void OnPicked ()
			{
				EventHandler handler = Picked;
				if (handler != null)
					handler (this, EventArgs.Empty);
			}

			protected override void OnPaint (PaintEventArgs e)
			{
				e.Graphics.FillRectangle (new SolidBrush (BackColor), ClientRectangle);
				for (int down = 0; down < CellsDown; down++)
					for (int across = 0; across < CellsAcross; across++) {
						int cell = across + down * CellsAcross;
						Rectangle bounds = CellBounds (across, down);
						using (SolidBrush brush = new SolidBrush (ColorAt (cell)))
							e.Graphics.FillRectangle (brush, bounds);
						e.Graphics.DrawRectangle (SystemPens.ControlText, bounds.X, bounds.Y,
									  bounds.Width - 1, bounds.Height - 1);

						// The cell that is picked is boxed, the one with the keyboard is
						// dotted -- the same two marks Windows uses.
						if (ColorAt (cell).ToArgb () == selected_colour.ToArgb ()) {
							Rectangle box = Rectangle.Inflate (bounds, 2, 2);
							e.Graphics.DrawRectangle (SystemPens.ControlText, box.X, box.Y,
										  box.Width - 1, box.Height - 1);
						}
						if (Focused && focus_cell.X == across && focus_cell.Y == down)
							ControlPaint.DrawFocusRectangle (e.Graphics,
											 Rectangle.Inflate (bounds, 3, 3));
					}
			}

			protected override void OnGotFocus (EventArgs e)
			{
				base.OnGotFocus (e);
				Invalidate ();
			}

			protected override void OnLostFocus (EventArgs e)
			{
				base.OnLostFocus (e);
				Invalidate ();
			}
		}
	}
}
