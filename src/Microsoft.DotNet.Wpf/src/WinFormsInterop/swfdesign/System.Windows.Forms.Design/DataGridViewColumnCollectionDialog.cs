// The columns dialog: the grid's columns down the left, the selected column's properties on the
// right, and the buttons to add, remove and reorder them.

using System.ComponentModel;
using System.ComponentModel.Design;
using System.Drawing;

namespace System.Windows.Forms.Design
{
	internal class DataGridViewColumnCollectionDialog : Form
	{
		private readonly ListBox columns = new ListBox ();
		private readonly Button add = new Button ();
		private readonly Button remove = new Button ();
		private readonly Button up = new Button ();
		private readonly Button down = new Button ();
		private readonly PropertyGrid properties = new PropertyGrid ();
		private readonly Button ok = new Button ();
		private readonly Button cancel = new Button ();

		private DataGridView grid;
		private IDesignerHost host;

		internal DataGridViewColumnCollectionDialog ()
		{
			Text = "Edit Columns";
			FormBorderStyle = FormBorderStyle.Sizable;
			MinimizeBox = false;
			MaximizeBox = false;
			ShowInTaskbar = false;
			StartPosition = FormStartPosition.CenterParent;
			ClientSize = new Size (640, 440);
			MinimumSize = new Size (520, 340);

			var heading = new Label { Text = "Selected Columns:", Bounds = new Rectangle (12, 10, 200, 18) };
			columns.Bounds = new Rectangle (12, 32, 230, 340);
			columns.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Bottom;
			columns.SelectedIndexChanged += OnColumnPicked;

			up.Text = "Move Up";
			up.Bounds = new Rectangle (248, 32, 80, 25);
			up.Click += delegate { Move (-1); };

			down.Text = "Move Down";
			down.Bounds = new Rectangle (248, 63, 80, 25);
			down.Click += delegate { Move (1); };

			add.Text = "Add...";
			add.Bounds = new Rectangle (12, 380, 80, 25);
			add.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
			add.Click += OnAdd;

			remove.Text = "Remove";
			remove.Bounds = new Rectangle (100, 380, 80, 25);
			remove.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
			remove.Click += OnRemove;

			var propertyHeading = new Label {
				Text = "Column Properties:",
				Bounds = new Rectangle (336, 10, 200, 18),
				Anchor = AnchorStyles.Top | AnchorStyles.Left,
			};
			properties.Bounds = new Rectangle (336, 32, 292, 340);
			properties.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom;
			properties.PropertyValueChanged += delegate { RefreshNames (); };

			ok.Text = "OK";
			ok.DialogResult = DialogResult.OK;
			ok.Bounds = new Rectangle (466, 404, 75, 25);
			ok.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;

			cancel.Text = "Cancel";
			cancel.DialogResult = DialogResult.Cancel;
			cancel.Bounds = new Rectangle (553, 404, 75, 25);
			cancel.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;

			Controls.AddRange (new Control[] {
				heading, columns, up, down, add, remove, propertyHeading, properties, ok, cancel,
			});
			AcceptButton = ok;
			CancelButton = cancel;
		}

		internal void SetLiveDataGridView (DataGridView view)
		{
			SetLiveDataGridView (view, null);
		}

		internal void SetLiveDataGridView (DataGridView view, IDesignerHost designerHost)
		{
			grid = view;
			host = designerHost;
			Reload (0);
		}

		private void Reload (int select)
		{
			columns.Items.Clear ();
			if (grid == null)
				return;
			foreach (DataGridViewColumn column in grid.Columns)
				columns.Items.Add (new Entry (column));
			if (columns.Items.Count > 0)
				columns.SelectedIndex = Math.Max (0, Math.Min (select, columns.Items.Count - 1));
			UpdateButtons ();
		}

		private void RefreshNames ()
		{
			// A column's name or header text may have changed under us; the list shows both.
			for (int i = 0; i < columns.Items.Count; i++)
				columns.Items[i] = columns.Items[i];
			columns.Invalidate ();
		}

		private void UpdateButtons ()
		{
			int index = columns.SelectedIndex;
			remove.Enabled = index >= 0;
			up.Enabled = index > 0;
			down.Enabled = index >= 0 && index < columns.Items.Count - 1;
		}

		private void OnColumnPicked (object sender, EventArgs e)
		{
			Entry entry = columns.SelectedItem as Entry;
			properties.SelectedObject = entry == null ? null : entry.Column;
			UpdateButtons ();
		}

		private void OnAdd (object sender, EventArgs e)
		{
			if (grid == null)
				return;
			using (var dialog = new DataGridViewAddColumnDialog (grid, host)) {
				if (dialog.ShowDialog (this) != DialogResult.OK || dialog.Column == null)
					return;
				grid.Columns.Add (dialog.Column);
				Reload (grid.Columns.Count - 1);
			}
		}

		private void OnRemove (object sender, EventArgs e)
		{
			Entry entry = columns.SelectedItem as Entry;
			if (grid == null || entry == null)
				return;
			int index = columns.SelectedIndex;
			grid.Columns.Remove (entry.Column);
			Reload (index);
		}

		private void Move (int direction)
		{
			Entry entry = columns.SelectedItem as Entry;
			if (grid == null || entry == null)
				return;
			int index = entry.Column.DisplayIndex + direction;
			if (index < 0 || index >= grid.Columns.Count)
				return;
			entry.Column.DisplayIndex = index;
			Reload (index);
		}

		/// <summary>A row of the list: the column, shown by its header or its name.</summary>
		private sealed class Entry
		{
			internal readonly DataGridViewColumn Column;

			internal Entry (DataGridViewColumn column)
			{
				Column = column;
			}

			public override string ToString ()
			{
				string header = Column.HeaderText;
				return string.IsNullOrEmpty (header) ? Column.Name : header;
			}
		}
	}
}
