// The drop-down the colour editor opens: three tabs, the palette, the named web colours and the
// system colours, exactly as Windows arranges them.

using System.Collections.Generic;
using System.Reflection;
using System.Windows.Forms;
using System.Windows.Forms.Design;

namespace System.Drawing.Design
{
	public partial class ColorEditor
	{
		private sealed class ColorUI : Control
		{
			private readonly ColorEditor editor;
			private IWindowsFormsEditorService service;
			private object value;

			private TabControl tabs;
			private TabPage palette_page, web_page, system_page;
			private ListBox web_list, system_list;
			private ColorPalette palette;
			private Color[] colour_values, system_colour_values, custom_colours;

			internal ColorUI (ColorEditor owner)
			{
				editor = owner;
				Build ();
			}

			internal object Value {
				get { return value; }
			}

			private Color[] ColorValues {
				get {
					if (colour_values == null) {
						colour_values = ConstantsOf (typeof (Color));
						Array.Sort (colour_values, new StandardColorComparer ());
					}
					return colour_values;
				}
			}

			private Color[] SystemColorValues {
				get {
					if (system_colour_values == null) {
						system_colour_values = ConstantsOf (typeof (SystemColors));
						Array.Sort (system_colour_values, new SystemColorComparer ());
					}
					return system_colour_values;
				}
			}

			private Color[] CustomColors {
				get {
					if (custom_colours == null) {
						custom_colours = new Color[ColorPalette.CellsCustom];
						for (int i = 0; i < custom_colours.Length; i++)
							custom_colours[i] = Color.White;
					}
					return custom_colours;
				}
			}

			/// <summary>Every public static Color property a type declares, which is how the two
			/// lists of named colours are built.</summary>
			private static Color[] ConstantsOf (Type type)
			{
				var found = new List<Color> ();
				foreach (PropertyInfo property in type.GetProperties ()) {
					if (property.PropertyType != typeof (Color))
						continue;
					MethodInfo getter = property.GetGetMethod ();
					if (getter == null || !getter.IsPublic || !getter.IsStatic)
						continue;
					object read = property.GetValue (null, null);
					if (read is Color)
						found.Add ((Color) read);
				}
				return found.ToArray ();
			}

			private void Build ()
			{
				palette_page = new TabPage ("Custom");
				web_page = new TabPage ("Web");
				system_page = new TabPage ("System");

				tabs = new TabControl { Dock = DockStyle.Fill, TabStop = false };
				tabs.TabPages.Add (palette_page);
				tabs.TabPages.Add (web_page);
				tabs.TabPages.Add (system_page);
				tabs.SelectedIndexChanged += OnTabChanged;

				web_list = MakeList ();
				system_list = MakeList ();

				foreach (Color colour in ColorValues)
					web_list.Items.Add (colour);
				foreach (Color colour in SystemColorValues)
					system_list.Items.Add (colour);

				palette = new ColorPalette (CustomColors);
				palette.Picked += OnPalettePicked;
				// The palette sizes itself to its cells; read that before docking it, because docking
				// hands its size over to the page it is on -- which is nothing at all until the drop-down
				// has been given a size, so asking afterwards sized the whole picker to a few pixels and
				// it appeared not to open.
				Size natural = palette.Size;
				palette.Dock = DockStyle.Fill;

				palette_page.Controls.Add (palette);
				web_page.Controls.Add (web_list);
				system_page.Controls.Add (system_list);
				Controls.Add (tabs);

				// Room for the row of tabs above the page. The control has no handle yet, so the tab
				// control cannot say how tall its own row is; the font it will use can.
				int strip = Font.Height + 12;
				Size = new Size (natural.Width + 8, natural.Height + strip + 8);
			}

			private ListBox MakeList ()
			{
				ListBox list = new ListBox {
					DrawMode = DrawMode.OwnerDrawFixed,
					BorderStyle = BorderStyle.FixedSingle,
					IntegralHeight = false,
					Sorted = false,
					Dock = DockStyle.Fill,
				};
				list.ItemHeight = Font.Height + 2;
				list.Click += OnListClicked;
				list.DrawItem += OnListDrawItem;
				list.KeyDown += OnListKeyDown;
				return list;
			}

			private void OnTabChanged (object sender, EventArgs e)
			{
				TabPage page = tabs.SelectedTab;
				if (page != null && page.Controls.Count > 0)
					page.Controls[0].Focus ();
			}

			private void OnListClicked (object sender, EventArgs e)
			{
				ListBox list = sender as ListBox;
				if (list != null && list.SelectedItem is Color)
					value = list.SelectedItem;
				Close ();
			}

			private void OnListKeyDown (object sender, KeyEventArgs e)
			{
				if (e.KeyCode == Keys.Return)
					OnListClicked (sender, EventArgs.Empty);
			}

			private void OnListDrawItem (object sender, DrawItemEventArgs e)
			{
				ListBox list = sender as ListBox;
				if (list == null || e.Index < 0 || e.Index >= list.Items.Count)
					return;
				Color colour = (Color) list.Items[e.Index];
				e.DrawBackground ();

				var swatch = new Rectangle (e.Bounds.X + 2, e.Bounds.Y + 2, 22, e.Bounds.Height - 4);
				editor.PaintValue (colour, e.Graphics, swatch);
				e.Graphics.DrawRectangle (SystemPens.WindowText, swatch.X, swatch.Y,
							  swatch.Width - 1, swatch.Height - 1);
				using (SolidBrush ink = new SolidBrush (e.ForeColor))
					e.Graphics.DrawString (colour.Name, Font, ink, e.Bounds.X + 26, e.Bounds.Y);
			}

			private void OnPalettePicked (object sender, EventArgs e)
			{
				ColorPalette source = sender as ColorPalette;
				if (source != null)
					value = BestMatch (source.SelectedColor);
				Close ();
			}

			/// <summary>A colour picked out of the palette comes back as a bare RGB value; if one of
			/// the named colours is the same colour, hand that back instead so the grid shows a name
			/// rather than three numbers.</summary>
			private Color BestMatch (Color colour)
			{
				int rgb = colour.ToArgb ();
				foreach (Color known in ColorValues)
					if (known.ToArgb () == rgb)
						return known;
				return colour;
			}

			private void Close ()
			{
				if (service != null)
					service.CloseDropDown ();
			}

			protected override void OnGotFocus (EventArgs e)
			{
				base.OnGotFocus (e);
				OnTabChanged (this, EventArgs.Empty);
			}

			protected override bool ProcessDialogKey (Keys keyData)
			{
				// Tab moves between the three pages. There is nothing else for it to do in a
				// drop-down, and ctrl-tab is awkward to reach from here.
				if ((keyData & Keys.Alt) == 0 && (keyData & Keys.Control) == 0
				    && (keyData & Keys.KeyCode) == Keys.Tab) {
					int selected = tabs.SelectedIndex;
					if (selected != -1) {
						bool forward = (keyData & Keys.Shift) == 0;
						int count = tabs.TabPages.Count;
						tabs.SelectedIndex = forward
							? (selected + 1) % count
							: (selected + count - 1) % count;
						return true;
					}
				}
				return base.ProcessDialogKey (keyData);
			}

			/// <summary>Open on whichever page holds the colour the property already has.</summary>
			internal void Start (IWindowsFormsEditorService editorService, object current)
			{
				service = editorService;
				value = current;
				if (!(current is Color))
					return;

				Color colour = (Color) current;
				TabPage page = palette_page;
				foreach (Color known in ColorValues)
					if (known.Equals (colour)) {
						web_list.SelectedItem = colour;
						page = web_page;
						break;
					}
				if (page == palette_page)
					foreach (Color known in SystemColorValues)
						if (known.Equals (colour)) {
							system_list.SelectedItem = colour;
							page = system_page;
							break;
						}
				if (page == palette_page)
					palette.SelectedColor = colour;
				tabs.SelectedTab = page;
			}

			internal void End ()
			{
				service = null;
				value = null;
			}
		}

		/// <summary>Greys first and then by hue, which is the order the web list reads in.</summary>
		private sealed class StandardColorComparer : System.Collections.IComparer
		{
			public int Compare (object first, object second)
			{
				Color left = (Color) first, right = (Color) second;
				if (left.A < right.A) return -1;
				if (left.A > right.A) return 1;

				float leftHue = left.GetHue (), rightHue = right.GetHue ();
				if (left.GetSaturation () == 0f) leftHue = -1f;
				if (right.GetSaturation () == 0f) rightHue = -1f;

				float difference = leftHue - rightHue;
				if (difference == 0f) {
					difference = left.GetBrightness () - right.GetBrightness ();
					if (difference == 0f)
						return string.Compare (left.Name, right.Name, StringComparison.Ordinal);
				}
				return difference < 0f ? -1 : 1;
			}
		}

		/// <summary>By name, which is all the system list needs.</summary>
		private sealed class SystemColorComparer : System.Collections.IComparer
		{
			public int Compare (object first, object second)
			{
				return string.Compare (((Color) first).Name, ((Color) second).Name,
						       StringComparison.OrdinalIgnoreCase);
			}
		}
	}
}
