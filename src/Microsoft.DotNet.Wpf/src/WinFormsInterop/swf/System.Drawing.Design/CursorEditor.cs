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

		private sealed class CursorUI : ListBox
		{
			private IWindowsFormsEditorService service;
			private object value;

			internal CursorUI ()
			{
				DrawMode = DrawMode.OwnerDrawFixed;
				BorderStyle = BorderStyle.None;
				IntegralHeight = false;
				ItemHeight = 21;
				Height = 21 * 8;

				foreach (var entry in Standard ())
					Items.Add (entry);
			}

			internal object Value {
				get { return value; }
			}

			/// <summary>Every cursor Cursors declares, which is the same list Windows offers.</summary>
			private static IEnumerable<Entry> Standard ()
			{
				var found = new List<Entry> ();
				foreach (PropertyInfo property in typeof (Cursors).GetProperties ()) {
					if (property.PropertyType != typeof (Cursor))
						continue;
					MethodInfo getter = property.GetGetMethod ();
					if (getter == null || !getter.IsPublic || !getter.IsStatic)
						continue;
					Cursor cursor = property.GetValue (null, null) as Cursor;
					if (cursor != null)
						found.Add (new Entry (property.Name, cursor));
				}
				found.Sort ((a, b) => string.Compare (a.Name, b.Name, StringComparison.Ordinal));
				return found;
			}

			private sealed class Entry
			{
				internal readonly string Name;
				internal readonly Cursor Cursor;

				internal Entry (string name, Cursor cursor)
				{
					Name = name;
					Cursor = cursor;
				}

				public override string ToString ()
				{
					return Name;
				}
			}

			internal void Start (IWindowsFormsEditorService editorService, object current)
			{
				service = editorService;
				value = current;
				SelectedIndex = -1;
				Cursor cursor = current as Cursor;
				if (cursor == null)
					return;
				for (int i = 0; i < Items.Count; i++)
					if (((Entry) Items[i]).Cursor == cursor) {
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
				Entry picked = SelectedItem as Entry;
				if (picked != null)
					value = picked.Cursor;
				if (service != null)
					service.CloseDropDown ();
			}

			protected override void OnKeyDown (KeyEventArgs e)
			{
				base.OnKeyDown (e);
				if (e.KeyCode == Keys.Return)
					OnClick (EventArgs.Empty);
			}

			protected override void OnDrawItem (DrawItemEventArgs e)
			{
				if (e.Index < 0 || e.Index >= Items.Count)
					return;
				Entry entry = (Entry) Items[e.Index];
				e.DrawBackground ();

				var glyph = new Rectangle (e.Bounds.X + 2, e.Bounds.Y + 2,
							   e.Bounds.Height - 4, e.Bounds.Height - 4);
				try {
					entry.Cursor.Draw (e.Graphics, glyph);
				} catch {
					// A cursor with no drawable image just leaves the square empty.
				}
				using (SolidBrush ink = new SolidBrush (e.ForeColor))
					e.Graphics.DrawString (entry.Name, Font, ink,
							       e.Bounds.X + e.Bounds.Height + 2, e.Bounds.Y + 2);
				e.DrawFocusRectangle ();
			}
		}
	}
}
