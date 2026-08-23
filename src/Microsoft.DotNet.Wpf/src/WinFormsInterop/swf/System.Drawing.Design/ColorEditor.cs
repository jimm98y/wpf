// A colour editor for the property grid.
//
// Windows shows a colour property as a swatch of the colour followed by its name, and opens a
// three-tab picker when the row's button is pressed: a palette of fixed colours, the named web
// colours, and the system colours. Without an editor registered for Color the grid falls back to
// the type converter's standard values, which is a plain list of names and no swatch at all.
//
// Stock keeps this in System.Windows.Forms.Design, an assembly an application never references; the
// grid finds it through a type-descriptor table installed by the designer. Ours lives in the
// forms assembly and the grid knows about it directly (see GridEntry.GetEditor), so it works in an
// ordinary application and on every platform -- nothing here reaches for a Win32 dialog.

using System.ComponentModel;
using System.Windows.Forms;
using System.Windows.Forms.Design;

namespace System.Drawing.Design
{
	public partial class ColorEditor : UITypeEditor
	{
		public override object EditValue (ITypeDescriptorContext context, IServiceProvider provider, object value)
		{
			IWindowsFormsEditorService service = provider == null
				? null
				: provider.GetService (typeof (IWindowsFormsEditorService)) as IWindowsFormsEditorService;
			if (service == null)
				return value;

			using (ColorUI ui = new ColorUI (this)) {
				ui.Start (service, value);
				service.DropDownControl (ui);
				if (ui.Value is Color && (Color) ui.Value != Color.Empty)
					value = ui.Value;
				ui.End ();
			}
			return value;
		}

		public override UITypeEditorEditStyle GetEditStyle (ITypeDescriptorContext context)
		{
			return UITypeEditorEditStyle.DropDown;
		}

		public override bool GetPaintValueSupported (ITypeDescriptorContext context)
		{
			return true;
		}

		public override void PaintValue (PaintValueEventArgs e)
		{
			if (!(e.Value is Color))
				return;
			using (SolidBrush brush = new SolidBrush ((Color) e.Value))
				e.Graphics.FillRectangle (brush, e.Bounds);
		}

		internal void PaintValue (Color colour, Graphics g, Rectangle bounds)
		{
			using (SolidBrush brush = new SolidBrush (colour))
				g.FillRectangle (brush, bounds);
		}
	}
}
