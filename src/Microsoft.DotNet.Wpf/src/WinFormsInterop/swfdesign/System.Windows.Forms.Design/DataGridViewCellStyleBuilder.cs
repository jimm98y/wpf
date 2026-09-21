// The cell-style builder: every property of a DataGridViewCellStyle on the left, and a live grid on
// the right showing what they look like.

using System.ComponentModel;
using System.Drawing;

namespace System.Windows.Forms.Design
{
	internal class DataGridViewCellStyleBuilder : Form
	{
		private readonly PropertyGrid properties = new PropertyGrid ();
		private readonly DataGridView sample = new DataGridView ();
		private readonly Button ok = new Button ();
		private readonly Button cancel = new Button ();
		private DataGridViewCellStyle style = new DataGridViewCellStyle ();

		internal DataGridViewCellStyleBuilder (IServiceProvider provider, IComponent component)
		{
			Text = "CellStyle Builder";
			FormBorderStyle = FormBorderStyle.Sizable;
			MinimizeBox = false;
			MaximizeBox = false;
			ShowInTaskbar = false;
			StartPosition = FormStartPosition.CenterParent;
			ClientSize = new Size (620, 420);
			MinimumSize = new Size (480, 320);

			var appearance = new Label {
				Text = "Appearance",
				Bounds = new Rectangle (12, 10, 200, 18),
			};
			properties.Bounds = new Rectangle (12, 32, 300, 340);
			properties.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Bottom;
			properties.ToolbarVisible = false;
			properties.PropertyValueChanged += OnPropertyChanged;

			var previewLabel = new Label {
				Text = "Preview",
				Bounds = new Rectangle (324, 10, 200, 18),
				Anchor = AnchorStyles.Top | AnchorStyles.Left,
			};
			sample.Bounds = new Rectangle (324, 32, 284, 160);
			sample.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
			sample.AllowUserToAddRows = false;
			sample.AllowUserToDeleteRows = false;
			sample.AllowUserToResizeRows = false;
			sample.ReadOnly = true;
			sample.Columns.Add ("preview", "Column");
			sample.Rows.Add ("Normal");
			sample.Rows.Add ("Selected");
			if (sample.Rows.Count > 1)
				sample.Rows[1].Selected = true;

			ok.Text = "OK";
			ok.DialogResult = DialogResult.OK;
			ok.Bounds = new Rectangle (446, 384, 75, 25);
			ok.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;

			cancel.Text = "Cancel";
			cancel.DialogResult = DialogResult.Cancel;
			cancel.Bounds = new Rectangle (533, 384, 75, 25);
			cancel.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;

			Controls.AddRange (new Control[] { appearance, properties, previewLabel, sample, ok, cancel });
			AcceptButton = ok;
			CancelButton = cancel;

			CellStyle = style;
		}

		/// <summary>The style being edited. A copy: the dialog must be cancellable, and the caller's
		/// style is shared with whatever is already using it.</summary>
		internal DataGridViewCellStyle CellStyle {
			get { return style; }
			set {
				style = value == null ? new DataGridViewCellStyle () : new DataGridViewCellStyle (value);
				properties.SelectedObject = style;
				ShowPreview ();
			}
		}

		internal ITypeDescriptorContext Context { get; set; }

		private void OnPropertyChanged (object sender, PropertyValueChangedEventArgs e)
		{
			ShowPreview ();
		}

		private void ShowPreview ()
		{
			try {
				sample.DefaultCellStyle = style;
				sample.Invalidate ();
			} catch (Exception) {
				// A style that the sample grid cannot apply -- a font it cannot create, say -- just
				// leaves the preview as it was.
			}
		}
	}
}
