// MaskedTextBox.Mask and MaskedTextBox.Text, which the control already names as their editors.

using System.ComponentModel;
using System.ComponentModel.Design;
using System.Drawing;
using System.Drawing.Design;

namespace System.Windows.Forms.Design
{
	internal class MaskPropertyEditor : UITypeEditor
	{
		internal static string EditMask (ITypeDiscoveryService discovery, IUIService ui, MaskedTextBox instance)
		{
			using (MaskDesignerDialog dialog = new MaskDesignerDialog (instance)) {
				dialog.DiscoverMaskDescriptors (discovery);
				DialogResult result = ui != null ? ui.ShowDialog (dialog) : dialog.ShowDialog ();
				if (result != DialogResult.OK)
					return null;

				// The validating type is not a browsable property, so there is no designer
				// transaction to put it through; set it on the control directly, as Windows does.
				if (instance != null && dialog.ValidatingType != instance.ValidatingType)
					instance.ValidatingType = dialog.ValidatingType;
				return dialog.Mask;
			}
		}

		public override object EditValue (ITypeDescriptorContext context, IServiceProvider provider, object value)
		{
			if (context == null || provider == null)
				return value;
			MaskedTextBox instance = context.Instance as MaskedTextBox;
			if (instance == null)
				return value;

			string mask = EditMask (provider.GetService (typeof (ITypeDiscoveryService)) as ITypeDiscoveryService,
						provider.GetService (typeof (IUIService)) as IUIService,
						instance);
			return mask ?? value;
		}

		public override bool GetPaintValueSupported (ITypeDescriptorContext context)
		{
			return false;
		}

		public override UITypeEditorEditStyle GetEditStyle (ITypeDescriptorContext context)
		{
			return UITypeEditorEditStyle.Modal;
		}
	}

	/// <summary>MaskedTextBox.Text, edited through a copy of the control itself so the mask is
	/// applied while it is typed rather than rejected afterwards.</summary>
	internal class MaskedTextBoxTextEditor : UITypeEditor
	{
		public override bool IsDropDownResizable {
			get { return false; }
		}

		public override object EditValue (ITypeDescriptorContext context, IServiceProvider provider, object value)
		{
			IWindowsFormsEditorService service = provider == null
				? null
				: provider.GetService (typeof (IWindowsFormsEditorService)) as IWindowsFormsEditorService;
			if (context == null || context.Instance == null || service == null)
				return value;

			MaskedTextBox instance = context.Instance as MaskedTextBox;
			if (instance == null)
				instance = new MaskedTextBox { Text = value as string };

			using (MaskedTextBoxTextEditorDropDown dropDown = new MaskedTextBoxTextEditorDropDown (instance)) {
				service.DropDownControl (dropDown);
				if (dropDown.Value != null)
					value = dropDown.Value;
			}
			return value;
		}

		public override UITypeEditorEditStyle GetEditStyle (ITypeDescriptorContext context)
		{
			return context != null && context.Instance != null
				? UITypeEditorEditStyle.DropDown
				: base.GetEditStyle (context);
		}

		public override bool GetPaintValueSupported (ITypeDescriptorContext context)
		{
			return context != null && context.Instance != null ? false : base.GetPaintValueSupported (context);
		}
	}

	internal class MaskedTextBoxTextEditorDropDown : UserControl
	{
		private readonly MaskedTextBox clone;
		private readonly Label error = new Label ();
		private bool cancelled;

		internal MaskedTextBoxTextEditorDropDown (MaskedTextBox source)
		{
			clone = new MaskedTextBox {
				Mask = source.Mask,
				Culture = source.Culture,
				PromptChar = source.PromptChar,
				PasswordChar = source.PasswordChar,
				AllowPromptAsInput = source.AllowPromptAsInput,
				HidePromptOnLeave = false,
				RightToLeft = source.RightToLeft,
				Text = source.Text,
				// Always include the prompt and the literals, so the text can be read back with each
				// character at the position the mask puts it -- even when the ones before it are not
				// filled in.
				TextMaskFormat = MaskFormat.IncludePromptAndLiterals,
				ResetOnPrompt = true,
				SkipLiterals = true,
				ResetOnSpace = true,
				Dock = DockStyle.Top,
			};
			clone.MaskInputRejected += OnRejected;
			clone.KeyDown += delegate { error.Text = string.Empty; };

			error.Dock = DockStyle.Bottom;
			error.ForeColor = Color.Firebrick;
			error.Height = 30;

			BackColor = SystemColors.Control;
			BorderStyle = BorderStyle.FixedSingle;
			Padding = new Padding (8);
			Size = new Size (220, 76);
			Controls.Add (error);
			Controls.Add (clone);
		}

		internal string Value {
			get { return cancelled ? null : clone.Text; }
		}

		protected override bool ProcessDialogKey (Keys keyData)
		{
			if (keyData == Keys.Escape)
				cancelled = true;
			return base.ProcessDialogKey (keyData);
		}

		private void OnRejected (object sender, MaskInputRejectedEventArgs e)
		{
			error.Text = "The character typed does not fit the mask at that position.";
		}
	}
}
