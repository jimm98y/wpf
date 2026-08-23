// A font property opens the font dialog.

using System.ComponentModel;
using System.Windows.Forms;
using System.Windows.Forms.Design;

namespace System.Drawing.Design
{
	public class FontEditor : UITypeEditor
	{
		private FontDialog dialog;

		public override object EditValue (ITypeDescriptorContext context, IServiceProvider provider, object value)
		{
			// The dialog does the work rather than the editor service, but Windows still asks for
			// the service first and declines when there is none -- an editor with nowhere to put a
			// drop-down has no business opening a modal dialog either.
			if (provider == null || provider.GetService (typeof (IWindowsFormsEditorService)) == null)
				return value;

			if (dialog == null)
				dialog = new FontDialog {
					ShowApply = false,
					ShowColor = false,
					AllowVerticalFonts = false,
				};

			if (value is Font)
				dialog.Font = (Font) value;

			return dialog.ShowDialog () == DialogResult.OK ? dialog.Font : value;
		}

		public override UITypeEditorEditStyle GetEditStyle (ITypeDescriptorContext context)
		{
			return UITypeEditorEditStyle.Modal;
		}
	}

	/// <summary>The name of a font, drawn in that font. Used for the string properties that name
	/// one -- a font combo box's selection, for instance.</summary>
	public class FontNameEditor : UITypeEditor
	{
		// Not every family has a plain face; try the four in turn and settle for the first that
		// exists.
		private static readonly FontStyle[] Faces = new FontStyle[] {
			FontStyle.Regular, FontStyle.Italic, FontStyle.Bold, FontStyle.Bold | FontStyle.Italic,
		};

		public override bool GetPaintValueSupported (ITypeDescriptorContext context)
		{
			return true;
		}

		public override void PaintValue (PaintValueEventArgs e)
		{
			string name = e.Value as string;
			if (string.IsNullOrEmpty (name) || name.Trim ().Length == 0)
				return;
			try {
				using (FontFamily family = new FontFamily (name)) {
					e.Graphics.FillRectangle (SystemBrushes.ActiveCaption, e.Bounds);
					foreach (FontStyle face in Faces) {
						try {
							DrawSample (e, family, face);
							break;
						} catch {
							// This family has no such face; try the next.
						}
					}
				}
			} catch {
				// An unknown or invalid family name simply gets no preview.
			}
		}

		private static void DrawSample (PaintValueEventArgs e, FontFamily family, FontStyle face)
		{
			const float ScaleFactor = 1.3f;
			using (Font font = new Font (family, e.Bounds.Height / ScaleFactor, face, GraphicsUnit.Pixel)) {
				StringFormat format = new StringFormat (
					StringFormatFlags.NoWrap | StringFormatFlags.NoFontFallback) {
					LineAlignment = StringAlignment.Far,
				};
				e.Graphics.DrawString ("abcd", font, SystemBrushes.ActiveCaptionText, e.Bounds, format);
			}
		}
	}
}
