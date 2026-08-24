// The link area of a LinkLabel, picked by selecting the words in a sample of the label's own text
// rather than typed as a start and a length.
//
// LinkLabel.LinkArea already names this editor; it simply was not here, so the property fell back to
// the expandable Start/Length pair and a designer had to count characters.

using System.ComponentModel;
using System.Drawing;
using System.Drawing.Design;

namespace System.Windows.Forms.Design
{
	internal partial class LinkAreaEditor : UITypeEditor
	{
		private LinkAreaUI ui;

		public override object EditValue (ITypeDescriptorContext context, IServiceProvider provider, object value)
		{
			IWindowsFormsEditorService service = provider == null
				? null
				: provider.GetService (typeof (IWindowsFormsEditorService)) as IWindowsFormsEditorService;
			if (service == null)
				return value;

			if (ui == null)
				ui = new LinkAreaUI ();

			// The sample is the label's own text, and a change to it in the dialog is a change to the
			// label: selecting a link is nearly always done while writing the words it sits in.
			string text = string.Empty;
			PropertyDescriptor textProperty = null;
			if (context != null && context.Instance != null) {
				textProperty = TypeDescriptor.GetProperties (context.Instance)["Text"];
				if (textProperty != null && textProperty.PropertyType == typeof (string))
					text = (string) textProperty.GetValue (context.Instance);
			}

			string original = text;
			ui.SampleText = text;
			ui.Start (value);

			if (service.ShowDialog (ui) == DialogResult.OK) {
				value = ui.Value;
				text = ui.SampleText;
				if (!string.Equals (text, original) && textProperty != null
				    && textProperty.PropertyType == typeof (string) && !textProperty.IsReadOnly)
					textProperty.SetValue (context.Instance, text);
			}

			ui.End ();
			return value;
		}

		public override UITypeEditorEditStyle GetEditStyle (ITypeDescriptorContext context)
		{
			return UITypeEditorEditStyle.Modal;
		}

		internal class LinkAreaUI : Form
		{
			private readonly Label caption = new Label ();
			private readonly TextBox sample = new TextBox ();
			private readonly Button ok = new Button ();
			private readonly Button cancel = new Button ();

			internal LinkAreaUI ()
			{
				Text = "Edit LinkArea";
				FormBorderStyle = FormBorderStyle.FixedDialog;
				MinimizeBox = false;
				MaximizeBox = false;
				ShowInTaskbar = false;
				StartPosition = FormStartPosition.CenterParent;
				ClientSize = new Size (350, 170);

				caption.Text = "Select the text you want to make into a link.";
				caption.Bounds = new Rectangle (12, 12, 326, 32);

				sample.Bounds = new Rectangle (12, 48, 326, 72);
				sample.Multiline = true;
				sample.ScrollBars = ScrollBars.Vertical;
				// Without this the selection vanishes the moment the buttons take the focus, and the
				// dialog is entirely about that selection.
				sample.HideSelection = false;

				ok.Text = "OK";
				ok.DialogResult = DialogResult.OK;
				ok.Bounds = new Rectangle (176, 132, 75, 25);
				ok.Click += OnOk;

				cancel.Text = "Cancel";
				cancel.DialogResult = DialogResult.Cancel;
				cancel.Bounds = new Rectangle (263, 132, 75, 25);

				Controls.AddRange (new Control[] { caption, sample, ok, cancel });
				AcceptButton = ok;
				CancelButton = cancel;
			}

			internal string SampleText {
				get { return sample.Text; }
				set {
					sample.Text = value;
					ShowSelection ();
				}
			}

			internal object Value { get; private set; }

			internal void Start (object value)
			{
				Value = value;
				ShowSelection ();
				ActiveControl = sample;
			}

			internal void End ()
			{
				Value = null;
			}

			private void OnOk (object sender, EventArgs e)
			{
				Value = new LinkArea (sample.SelectionStart, sample.SelectionLength);
			}

			private void ShowSelection ()
			{
				if (!(Value is LinkArea))
					return;
				LinkArea area = (LinkArea) Value;
				try {
					sample.SelectionStart = area.Start;
					sample.SelectionLength = area.Length;
				} catch (Exception) {
					// A link area left over from longer text simply does not apply to this sample.
				}
			}
		}
	}
}
