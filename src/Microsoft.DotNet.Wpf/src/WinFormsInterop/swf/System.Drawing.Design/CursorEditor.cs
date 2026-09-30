// A cursor property drops down the list of the standard cursors, each drawn beside its name.

using System.Collections.Generic;
using System.ComponentModel;
using System.Reflection;
using System.Windows.Forms;
using System.Windows.Forms.Design;

namespace System.Drawing.Design
{
	public partial class CursorEditor : UITypeEditor
	{
		private CursorUI ui;

		public override bool IsDropDownResizable {
			get { return true; }
		}

		public override object EditValue (ITypeDescriptorContext context, IServiceProvider provider, object value)
		{
			IWindowsFormsEditorService service = provider == null
				? null
				: provider.GetService (typeof (IWindowsFormsEditorService)) as IWindowsFormsEditorService;
			if (service == null)
				return value;

			if (ui == null)
				ui = new CursorUI ();
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

		/// <summary>.NET's CursorEditor.CursorUI: an owner-drawn list of the converter's standard
		/// cursors, 310 tall (IntegralHeight left on, so whole rows of it), each row a small-icon
		/// square on the control colour with the cursor stretched into it and its name beside it.
		/// Ours was a list of its own design -- 21-pixel rows, eight of them, sorted by name.</summary>
		private sealed class CursorUI : ListBox
		{
			private IWindowsFormsEditorService service;
			private object value;
			private readonly TypeConverter converter = TypeDescriptor.GetConverter (typeof (Cursor));

			internal CursorUI ()
			{
				Height = 310;
				ItemHeight = Math.Max (4 + Cursors.Default.Size.Height, Font.Height);
				DrawMode = DrawMode.OwnerDrawFixed;
				BorderStyle = BorderStyle.None;
				if (converter.GetStandardValuesSupported ())
					foreach (object cursor in converter.GetStandardValues ())
						Items.Add (cursor);
			}

			internal object Value {
				get { return value; }
			}

			internal void Start (IWindowsFormsEditorService editorService, object current)
			{
				service = editorService;
				value = current;
				if (current == null)
					return;
				for (int i = 0; i < Items.Count; i++)
					if (Items [i] == current) {
						SelectedIndex = i;
						break;
					}
			}

			internal void End ()
			{
				service = null;
				value = null;
			}

			protected override void OnClick (EventArgs e)
			{
				base.OnClick (e);
				value = SelectedItem;
				if (service != null)
					service.CloseDropDown ();
			}

			protected override bool ProcessDialogKey (Keys keyData)
			{
				if ((keyData & Keys.KeyCode) == Keys.Return && (keyData & (Keys.Alt | Keys.Control)) == 0) {
					OnClick (EventArgs.Empty);
					return true;
				}
				return base.ProcessDialogKey (keyData);
			}

			protected override void OnDrawItem (DrawItemEventArgs e)
			{
				base.OnDrawItem (e);
				if (e.Index == -1)
					return;
				Cursor cursor = (Cursor) Items [e.Index];
				string text = converter.ConvertToString (cursor);
				Font font = e.Font;
				// The small-icon size: .NET scales the cursor's icon to it (ScaleSmallIconToDpi).
				int width = SystemInformation.SmallIconSize.Width;
				e.DrawBackground ();
				var square = new Rectangle (e.Bounds.X + 2, e.Bounds.Y + 2, width, e.Bounds.Height - 4);
				e.Graphics.FillRectangle (SystemBrushes.Control, square);
				e.Graphics.DrawRectangle (SystemPens.WindowText, new Rectangle (square.X, square.Y, width - 1, square.Height - 1));
				try {
					cursor.DrawStretched (e.Graphics, square);
				} catch {
					// a cursor without an image leaves its square empty
				}
				using (SolidBrush ink = new SolidBrush (e.ForeColor))
					e.Graphics.DrawString (text, font, ink, e.Bounds.X + width + 4, e.Bounds.Y + (e.Bounds.Height - font.Height) / 2);
			}
		}
	}
}
