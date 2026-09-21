// Image and icon properties: a file dialog to choose one, and a thumbnail in the value column.
//
// Stock keeps these in its designer assembly along with the colour editor; the same reasoning
// applies -- an ordinary application never loads that assembly, so a bitmap property showed the
// type name and nothing else.

using System.ComponentModel;
using System.Drawing.Imaging;
using System.IO;
using System.Windows.Forms;
using System.Windows.Forms.Design;

namespace System.Drawing.Design
{
	public class ImageEditor : UITypeEditor
	{
		public override object EditValue (ITypeDescriptorContext context, IServiceProvider provider, object value)
		{
			if (provider == null || provider.GetService (typeof (IWindowsFormsEditorService)) == null)
				return value;

			using (OpenFileDialog dialog = new OpenFileDialog ()) {
				dialog.Title = "Open";
				dialog.Filter = FileFilter;
				if (dialog.ShowDialog () != DialogResult.OK)
					return value;
				try {
					using (FileStream file = new FileStream (dialog.FileName, FileMode.Open,
										 FileAccess.Read, FileShare.Read))
						return LoadFrom (file);
				} catch {
					// An unreadable or unrecognised file leaves the property as it was.
					return value;
				}
			}
		}

		protected virtual string FileFilter {
			get {
				return "All image files|*.bmp;*.gif;*.jpg;*.jpeg;*.png;*.ico;*.emf;*.wmf"
					+ "|Bitmap files|*.bmp;*.gif;*.jpg;*.jpeg;*.png"
					+ "|Icon files|*.ico"
					+ "|Metafiles|*.emf;*.wmf"
					+ "|All files|*.*";
			}
		}

		protected virtual object LoadFrom (Stream stream)
		{
			return Image.FromStream (stream);
		}

		public override UITypeEditorEditStyle GetEditStyle (ITypeDescriptorContext context)
		{
			return UITypeEditorEditStyle.Modal;
		}

		public override bool GetPaintValueSupported (ITypeDescriptorContext context)
		{
			return true;
		}

		public override void PaintValue (PaintValueEventArgs e)
		{
			Image image = e.Value as Image;
			if (image == null)
				return;
			// Fitted to the cell, keeping its shape, which is what a thumbnail in a property grid
			// has always done.
			Rectangle bounds = e.Bounds;
			float scale = Math.Min ((float) bounds.Width / image.Width,
						(float) bounds.Height / image.Height);
			int width = Math.Max (1, (int) (image.Width * scale));
			int height = Math.Max (1, (int) (image.Height * scale));
			e.Graphics.DrawImage (image,
					      new Rectangle (bounds.X + (bounds.Width - width) / 2,
							     bounds.Y + (bounds.Height - height) / 2, width, height));
		}
	}

	public class IconEditor : ImageEditor
	{
		protected override string FileFilter {
			get { return "Icon files|*.ico|All files|*.*"; }
		}

		protected override object LoadFrom (Stream stream)
		{
			return new Icon (stream);
		}

		public override void PaintValue (PaintValueEventArgs e)
		{
			Icon icon = e.Value as Icon;
			if (icon == null) {
				base.PaintValue (e);
				return;
			}
			using (Bitmap bitmap = icon.ToBitmap ())
				base.PaintValue (new PaintValueEventArgs (null, bitmap, e.Graphics, e.Bounds));
		}
	}
}
