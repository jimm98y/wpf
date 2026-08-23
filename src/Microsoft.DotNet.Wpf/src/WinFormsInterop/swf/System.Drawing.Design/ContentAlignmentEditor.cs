// Content alignment as a three-by-three grid of cells, the way Windows offers it, rather than as a
// list of nine enumeration names.

using System.ComponentModel;
using System.Windows.Forms;
using System.Windows.Forms.Design;

namespace System.Drawing.Design
{
	public partial class ContentAlignmentEditor : UITypeEditor
	{
		private ContentUI ui;

		public override object EditValue (ITypeDescriptorContext context, IServiceProvider provider, object value)
		{
			IWindowsFormsEditorService service = provider == null
				? null
				: provider.GetService (typeof (IWindowsFormsEditorService)) as IWindowsFormsEditorService;
			if (service == null)
				return value;

			if (ui == null)
				ui = new ContentUI ();
			ui.Start (service, value);
			service.DropDownControl (ui);
			value = ui.Value;
			ui.End ();
			return value;
		}

		public override UITypeEditorEditStyle GetEditStyle (ITypeDescriptorContext context)
		{
			return UITypeEditorEditStyle.DropDown;
		}

		private sealed class ContentUI : Control
		{
			// Row-major, top to bottom and left to right, which is the order the cells are drawn in.
			private static readonly ContentAlignment[] Cells = new ContentAlignment[] {
				ContentAlignment.TopLeft, ContentAlignment.TopCenter, ContentAlignment.TopRight,
				ContentAlignment.MiddleLeft, ContentAlignment.MiddleCenter, ContentAlignment.MiddleRight,
				ContentAlignment.BottomLeft, ContentAlignment.BottomCenter, ContentAlignment.BottomRight,
			};

			private const int CellSize = 24;
			private const int Margin = 4;

			private IWindowsFormsEditorService service;
			private ContentAlignment current = ContentAlignment.MiddleCenter;
			private int focused;

			internal ContentUI ()
			{
				SetStyle (ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint
					  | ControlStyles.Selectable, true);
				BackColor = SystemColors.Window;
				Size = new Size (3 * CellSize + 4 * Margin, 3 * CellSize + 4 * Margin);
			}

			internal object Value {
				get { return current; }
			}

			internal void Start (IWindowsFormsEditorService editorService, object value)
			{
				service = editorService;
				if (value is ContentAlignment) {
					current = (ContentAlignment) value;
					focused = Array.IndexOf (Cells, current);
					if (focused < 0)
						focused = 0;
				}
				Invalidate ();
			}

			internal void End ()
			{
				service = null;
			}

			private static Rectangle CellBounds (int index)
			{
				int across = index % 3, down = index / 3;
				return new Rectangle (Margin + across * (CellSize + Margin),
						      Margin + down * (CellSize + Margin), CellSize, CellSize);
			}

			private int CellAt (Point point)
			{
				for (int i = 0; i < Cells.Length; i++)
					if (CellBounds (i).Contains (point))
						return i;
				return -1;
			}

			protected override void OnMouseDown (MouseEventArgs e)
			{
				base.OnMouseDown (e);
				int cell = CellAt (new Point (e.X, e.Y));
				if (cell < 0)
					return;
				current = Cells[cell];
				focused = cell;
				Invalidate ();
				if (service != null)
					service.CloseDropDown ();
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
				int across = focused % 3, down = focused / 3;
				switch (e.KeyCode) {
				case Keys.Left: across--; break;
				case Keys.Right: across++; break;
				case Keys.Up: down--; break;
				case Keys.Down: down++; break;
				case Keys.Return:
				case Keys.Space:
					current = Cells[focused];
					if (service != null)
						service.CloseDropDown ();
					return;
				default:
					return;
				}
				if (across < 0 || down < 0 || across > 2 || down > 2)
					return;
				focused = across + down * 3;
				current = Cells[focused];
				Invalidate ();
			}

			protected override void OnPaint (PaintEventArgs e)
			{
				e.Graphics.FillRectangle (SystemBrushes.Window, ClientRectangle);
				for (int i = 0; i < Cells.Length; i++) {
					Rectangle bounds = CellBounds (i);
					bool picked = Cells[i] == current;
					e.Graphics.FillRectangle (
						picked ? SystemBrushes.Highlight : SystemBrushes.Control, bounds);
					e.Graphics.DrawRectangle (SystemPens.ControlDark, bounds.X, bounds.Y,
								  bounds.Width - 1, bounds.Height - 1);

					// A short bar drawn where the content would sit, so the cell says what it
					// means without a caption.
					var bar = new Rectangle (bounds.X + 4, bounds.Y + 4, bounds.Width - 8, 4);
					int across = i % 3, down = i / 3;
					bar.Y = bounds.Y + (down == 0 ? 5 : down == 1 ? bounds.Height / 2 - 2
									 : bounds.Height - 9);
					bar.Width = bounds.Width - 10;
					bar.X = bounds.X + (across == 0 ? 5 : across == 1 ? 5 : 5);
					if (across == 1)
						bar = new Rectangle (bounds.X + 7, bar.Y, bounds.Width - 14, 4);
					else if (across == 2)
						bar = new Rectangle (bounds.X + 9, bar.Y, bounds.Width - 14, 4);
					else
						bar = new Rectangle (bounds.X + 5, bar.Y, bounds.Width - 14, 4);
					e.Graphics.FillRectangle (
						picked ? SystemBrushes.HighlightText : SystemBrushes.ControlText, bar);

					if (Focused && i == focused)
						ControlPaint.DrawFocusRectangle (e.Graphics,
										 Rectangle.Inflate (bounds, -2, -2));
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
