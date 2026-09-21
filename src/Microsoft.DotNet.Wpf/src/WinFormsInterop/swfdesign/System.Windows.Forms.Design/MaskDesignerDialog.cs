// The dialog behind MaskedTextBox.Mask: a list of the standard masks, the mask itself, and a box to
// try it in before committing to it.

using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.Design;
using System.Drawing;
using System.Globalization;

namespace System.Windows.Forms.Design
{
	internal class MaskDesignerDialog : Form
	{
		private readonly ListView list = new ListView ();
		private readonly TextBox mask_box = new TextBox ();
		private readonly MaskedTextBox preview = new MaskedTextBox ();
		private readonly Label error = new Label ();
		private readonly CheckBox use_validating = new CheckBox ();
		private readonly Button ok = new Button ();
		private readonly Button cancel = new Button ();

		private readonly List<MaskDescriptor> descriptors = new List<MaskDescriptor> ();
		private readonly CultureInfo culture;
		private Type validating_type;

		internal MaskDesignerDialog (MaskedTextBox instance)
		{
			culture = instance == null ? CultureInfo.CurrentCulture : instance.Culture;
			validating_type = instance == null ? null : instance.ValidatingType;

			Text = "Input Mask";
			FormBorderStyle = FormBorderStyle.FixedDialog;
			MinimizeBox = false;
			MaximizeBox = false;
			ShowInTaskbar = false;
			StartPosition = FormStartPosition.CenterParent;
			ClientSize = new Size (480, 380);

			var heading = new Label {
				Text = "Select a predefined mask, or write one of your own.",
				Bounds = new Rectangle (12, 10, 456, 18),
			};

			list.Bounds = new Rectangle (12, 32, 456, 210);
			list.View = View.Details;
			list.FullRowSelect = true;
			list.MultiSelect = false;
			list.HideSelection = false;
			list.Columns.Add ("Mask Description", 190);
			list.Columns.Add ("Data Format", 130);
			list.Columns.Add ("Validating Type", 130);
			list.SelectedIndexChanged += OnPicked;

			var maskLabel = new Label { Text = "Mask:", Bounds = new Rectangle (12, 252, 50, 18) };
			mask_box.Bounds = new Rectangle (66, 249, 402, 23);
			mask_box.TextChanged += OnMaskChanged;

			var tryLabel = new Label { Text = "Preview:", Bounds = new Rectangle (12, 284, 50, 18) };
			preview.Bounds = new Rectangle (66, 281, 402, 23);
			preview.TextMaskFormat = MaskFormat.IncludePromptAndLiterals;
			preview.ResetOnPrompt = true;
			preview.SkipLiterals = true;
			preview.ResetOnSpace = true;
			preview.MaskInputRejected += OnRejected;
			preview.KeyDown += delegate { error.Text = string.Empty; };

			error.Bounds = new Rectangle (66, 306, 402, 18);
			error.ForeColor = Color.Firebrick;

			use_validating.Text = "Use the mask's validating type";
			use_validating.Bounds = new Rectangle (12, 328, 260, 20);
			use_validating.Checked = true;

			ok.Text = "OK";
			ok.DialogResult = DialogResult.OK;
			ok.Bounds = new Rectangle (306, 348, 75, 25);

			cancel.Text = "Cancel";
			cancel.DialogResult = DialogResult.Cancel;
			cancel.Bounds = new Rectangle (393, 348, 75, 25);

			Controls.AddRange (new Control[] {
				heading, list, maskLabel, mask_box, tryLabel, preview, error, use_validating, ok, cancel,
			});
			AcceptButton = ok;
			CancelButton = cancel;

			AddStandardDescriptors ();
			Mask = instance == null ? string.Empty : instance.Mask;
		}

		/// <summary>The mask the dialog was left on.</summary>
		internal string Mask {
			get { return mask_box.Text; }
			private set {
				mask_box.Text = value ?? string.Empty;
				SelectMatching (mask_box.Text);
			}
		}

		/// <summary>The type the mask validates against, or null when the designer asked for none.
		/// Not a browsable property of the control, so the editor sets it directly.</summary>
		internal Type ValidatingType {
			get { return use_validating.Checked ? validating_type : null; }
		}

		private void AddStandardDescriptors ()
		{
			descriptors.Clear ();
			list.Items.Clear ();

			// The custom entry first, as Windows has it: picking it leaves the mask alone and hands
			// the designer the text box.
			descriptors.Add (null);
			var custom = new ListViewItem (new string[] { "<Custom>", string.Empty, string.Empty });
			list.Items.Add (custom);

			foreach (MaskDescriptor descriptor in MaskDescriptorTemplate.Standard (culture)) {
				descriptors.Add (descriptor);
				list.Items.Add (new ListViewItem (new string[] {
					descriptor.Name,
					descriptor.Mask,
					descriptor.ValidatingType == null ? "(none)" : descriptor.ValidatingType.Name,
				}));
			}
		}

		/// <summary>Take in whatever else the project offers. Windows finds these through the type
		/// discovery service; without one the standard set is all there is, which is what an
		/// ordinary project has anyway.</summary>
		internal void DiscoverMaskDescriptors (ITypeDiscoveryService discovery)
		{
			if (discovery == null)
				return;
			try {
				foreach (Type type in discovery.GetTypes (typeof (MaskDescriptor), false)) {
					if (type.IsAbstract || !type.IsPublic)
						continue;
					MaskDescriptor descriptor;
					try {
						descriptor = Activator.CreateInstance (type) as MaskDescriptor;
					} catch (Exception) {
						continue;
					}
					if (descriptor == null || !MaskDescriptor.IsValidMaskDescriptor (descriptor))
						continue;
					descriptors.Add (descriptor);
					list.Items.Add (new ListViewItem (new string[] {
						descriptor.Name,
						descriptor.Mask,
						descriptor.ValidatingType == null ? "(none)" : descriptor.ValidatingType.Name,
					}));
				}
			} catch (Exception) {
				// A discovery service that cannot enumerate simply contributes nothing.
			}
		}

		private void SelectMatching (string mask)
		{
			for (int i = 1; i < descriptors.Count; i++)
				if (descriptors[i] != null && descriptors[i].Mask == mask) {
					list.Items[i].Selected = true;
					return;
				}
			if (list.Items.Count > 0)
				list.Items[0].Selected = true;
		}

		private void OnPicked (object sender, EventArgs e)
		{
			if (list.SelectedIndices.Count == 0)
				return;
			MaskDescriptor descriptor = descriptors[list.SelectedIndices[0]];
			if (descriptor == null)
				return;                             // <Custom>: leave the mask to the designer
			validating_type = descriptor.ValidatingType;
			mask_box.Text = descriptor.Mask;
		}

		private void OnMaskChanged (object sender, EventArgs e)
		{
			error.Text = string.Empty;
			try {
				preview.Mask = mask_box.Text;
				ok.Enabled = true;
			} catch (Exception ex) {
				error.Text = ex.Message;
				ok.Enabled = false;
			}
		}

		private void OnRejected (object sender, MaskInputRejectedEventArgs e)
		{
			error.Text = "The character typed does not fit the mask at that position.";
		}
	}
}
