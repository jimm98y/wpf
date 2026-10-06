// Adding a column: its name, the header it shows, and which kind of column it is.

using System.Collections.Generic;
using System.ComponentModel.Design;
using System.Drawing;

namespace System.Windows.Forms.Design
{
	internal class DataGridViewAddColumnDialog : Form
	{
		private readonly TextBox name = new TextBox ();
		private readonly TextBox header = new TextBox ();
		private readonly ComboBox type = new ComboBox ();
		private readonly Button ok = new Button ();
		private readonly Button cancel = new Button ();

		private readonly DataGridView grid;

		internal DataGridViewAddColumnDialog (DataGridView view, IDesignerHost host)
		{
			grid = view;

			Text = "Add Column";
			FormBorderStyle = FormBorderStyle.FixedDialog;
			MinimizeBox = false;
			MaximizeBox = false;
			ShowInTaskbar = false;
			StartPosition = FormStartPosition.CenterParent;
			ClientSize = new Size (360, 180);

			var nameLabel = new Label { Text = "Name:", Bounds = new Rectangle (12, 16, 90, 18) };
			name.Bounds = new Rectangle (108, 13, 240, 23);
			name.Text = NextName ();

			var typeLabel = new Label { Text = "Type:", Bounds = new Rectangle (12, 50, 90, 18) };
			type.Bounds = new Rectangle (108, 47, 240, 23);
			type.DropDownStyle = ComboBoxStyle.DropDownList;
			foreach (ColumnKind kind in Kinds (host))
				type.Items.Add (kind);
			if (type.Items.Count > 0)
				type.SelectedIndex = 0;

			var headerLabel = new Label { Text = "Header text:", Bounds = new Rectangle (12, 84, 90, 18) };
			header.Bounds = new Rectangle (108, 81, 240, 23);
			header.Text = name.Text;

			ok.Text = "Add";
			ok.DialogResult = DialogResult.OK;
			ok.Bounds = new Rectangle (186, 140, 75, 25);
			ok.Click += OnAdd;

			cancel.Text = "Cancel";
			cancel.DialogResult = DialogResult.Cancel;
			cancel.Bounds = new Rectangle (273, 140, 75, 25);

			Controls.AddRange (new Control[] {
				nameLabel, name, typeLabel, type, headerLabel, header, ok, cancel,
			});
			AcceptButton = ok;
			CancelButton = cancel;
		}

		/// <summary>The column the dialog built, or null when it was cancelled.</summary>
		internal DataGridViewColumn Column { get; private set; }

		private string NextName ()
		{
			int n = grid == null ? 1 : grid.Columns.Count + 1;
			return "Column" + n;
		}

		private void OnAdd (object sender, EventArgs e)
		{
			ColumnKind kind = type.SelectedItem as ColumnKind;
			if (kind == null)
				return;
			try {
				Column = (DataGridViewColumn) Activator.CreateInstance (kind.Type);
				Column.Name = name.Text;
				Column.HeaderText = header.Text;
			} catch (Exception) {
				Column = null;
				DialogResult = DialogResult.Cancel;
			}
		}

		/// <summary>The kinds of column on offer. The built-in ones always, plus anything else the
		/// project defines when the designer can enumerate its types.</summary>
		private static IEnumerable<ColumnKind> Kinds (IDesignerHost host)
		{
			var kinds = new List<ColumnKind> {
				new ColumnKind ("TextBox", typeof (DataGridViewTextBoxColumn)),
				new ColumnKind ("CheckBox", typeof (DataGridViewCheckBoxColumn)),
				new ColumnKind ("ComboBox", typeof (DataGridViewComboBoxColumn)),
				new ColumnKind ("Button", typeof (DataGridViewButtonColumn)),
				new ColumnKind ("Image", typeof (DataGridViewImageColumn)),
				new ColumnKind ("Link", typeof (DataGridViewLinkColumn)),
			};

			ITypeDiscoveryService discovery = host == null
				? null
				: host.GetService (typeof (ITypeDiscoveryService)) as ITypeDiscoveryService;
			if (discovery == null)
				return kinds;

			try {
				foreach (Type type in discovery.GetTypes (typeof (DataGridViewColumn), false)) {
					if (type.IsAbstract || !type.IsPublic || kinds.Exists (k => k.Type == type))
						continue;
					kinds.Add (new ColumnKind (type.Name, type));
				}
			} catch (Exception) {
				// A discovery service that cannot enumerate contributes nothing.
			}
			return kinds;
		}

		private sealed class ColumnKind
		{
			internal readonly string Name;
			internal readonly Type Type;

			internal ColumnKind (string name, Type type)
			{
				Name = name;
				Type = type;
			}

			public override string ToString ()
			{
				return Name;
			}
		}
	}
}
