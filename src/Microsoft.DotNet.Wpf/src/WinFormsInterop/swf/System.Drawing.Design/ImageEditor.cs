// Image and icon properties: .NET's ImageEditor, BitmapEditor, MetafileEditor and IconEditor -- a file
// dialog to choose one, and a framed thumbnail in the value column.
//
// .NET keeps these in its designer assembly (System.Windows.Forms.Design), and the System.Drawing.Design
// identity forwards to them. Ours do the same: the facade forwards here. The copies in our
// System.Drawing had .NET's protected API but no EditValue -- that assembly cannot open a file dialog --
// so the [Editor] attribute on Image resolved to an editor that did nothing, and a Picture property
// never opened anything; these here had an EditValue of their own design and not .NET's API.

using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing.Imaging;
using System.IO;
using System.Reflection;
using System.Text;
using System.Windows.Forms;
using System.Windows.Forms.Design;

namespace System.Drawing.Design
{
	public class ImageEditor : UITypeEditor
	{
		private static readonly Type [] s_imageExtenders = { typeof (BitmapEditor), typeof (MetafileEditor) };
		private FileDialog _fileDialog;

		// Accessor needed into the static field so that derived classes can implement a different list
		// of supported image types.
		protected virtual Type [] GetImageExtenders () => s_imageExtenders;

		protected static string CreateExtensionsString (string [] extensions, string sep)
		{
			if (extensions == null || extensions.Length == 0)
				return null;
			var text = new StringBuilder ();
			for (int i = 0; i < extensions.Length; i++) {
				if (string.IsNullOrEmpty (extensions [i]))
					continue;
				text.Append ("*.");
				text.Append (extensions [i]);
				if (i != extensions.Length - 1)
					text.Append (sep);
			}
			return text.ToString ();
		}

		protected static string CreateFilterEntry (ImageEditor e)
		{
			if (e == null)
				throw new ArgumentNullException (nameof (e));
			string [] extenders = e.GetExtensions ();
			string description = e.GetFileDialogDescription ();
			string extensions = CreateExtensionsString (extenders, ",");
			string extensionsWithSemicolons = CreateExtensionsString (extenders, ";");
			return $"{description}({extensions})|{extensionsWithSemicolons}";
		}

		private ImageEditor CreateExtender (Type extender)
			=> (ImageEditor) Activator.CreateInstance (extender,
				BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.CreateInstance,
				null, null, null);

		public override object EditValue (ITypeDescriptorContext context, IServiceProvider provider, object value)
		{
			if (provider == null || provider.GetService (typeof (IWindowsFormsEditorService)) == null)
				return value;

			if (_fileDialog == null) {
				_fileDialog = new OpenFileDialog ();
				string filter = CreateFilterEntry (this);
				foreach (Type extender in GetImageExtenders ()) {
					if (extender == null || !typeof (ImageEditor).IsAssignableFrom (extender))
						continue;
					Type myClass = GetType ();
					ImageEditor editor = CreateExtender (extender);
					if (editor != null && !myClass.Equals (editor.GetType ()) && myClass.IsInstanceOfType (editor))
						filter += $"|{CreateFilterEntry (editor)}";
				}
				_fileDialog.Filter = filter;
			}

			IntPtr focus = XplatUI.GetFocus ();
			try {
				if (_fileDialog.ShowDialog () == DialogResult.OK) {
					using (var stream = new FileStream (_fileDialog.FileName, FileMode.Open, FileAccess.Read, FileShare.Read))
						value = LoadFromStream (stream);
				}
			} finally {
				if (focus != IntPtr.Zero)
					XplatUI.SetFocus (focus);
			}
			return value;
		}

		public override UITypeEditorEditStyle GetEditStyle (ITypeDescriptorContext context) => UITypeEditorEditStyle.Modal;

		protected virtual string GetFileDialogDescription () => "All image files";

		protected virtual string [] GetExtensions ()
		{
			var list = new List<string> ();
			foreach (Type extender in GetImageExtenders ()) {
				if (extender == null || !typeof (ImageEditor).IsAssignableFrom (extender))
					continue;
				ImageEditor editor = CreateExtender (extender);
				if (editor != null && editor.GetType () != typeof (ImageEditor)) {
					string [] extensions = editor.GetExtensions ();
					if (extensions != null)
						list.AddRange (extensions);
				}
			}
			return list.ToArray ();
		}

		public override bool GetPaintValueSupported (ITypeDescriptorContext context) => true;

		protected virtual Image LoadFromStream (Stream stream)
		{
			if (stream == null)
				throw new ArgumentNullException (nameof (stream));
			// Copied first so the file is not held open: the image takes over the memory stream.
			var memoryStream = new MemoryStream ();
			stream.CopyTo (memoryStream);
			return Image.FromStream (memoryStream);
		}

		public override void PaintValue (PaintValueEventArgs e)
		{
			if (e?.Value is Image image) {
				Rectangle r = e.Bounds;
				r.Width--;
				r.Height--;
				e.Graphics.DrawRectangle (SystemPens.WindowFrame, r);
				e.Graphics.DrawImage (image, e.Bounds);
			}
		}
	}

	public class BitmapEditor : ImageEditor
	{
		protected static List<string> BitmapExtensions = new List<string> { "bmp", "gif", "jpg", "jpeg", "png", "ico" };

		protected override string GetFileDialogDescription () => "Bitmap files";

		protected override string [] GetExtensions () => BitmapExtensions.ToArray ();

		protected override Image LoadFromStream (Stream stream) => new Bitmap (stream);
	}

	public class MetafileEditor : ImageEditor
	{
		protected override string GetFileDialogDescription () => "Metafiles";

		protected override string [] GetExtensions () => new [] { "emf", "wmf" };

		protected override Image LoadFromStream (Stream stream) => new Metafile (stream);
	}

	public class IconEditor : UITypeEditor
	{
		private static readonly List<string> s_iconExtensions = new List<string> { "ico" };
		private FileDialog _fileDialog;

		protected static string CreateExtensionsString (string [] extensions, string sep)
		{
			if (extensions == null || extensions.Length == 0)
				return null;
			string text = string.Empty;
			for (int i = 0; i < extensions.Length; i++) {
				if (string.IsNullOrWhiteSpace (extensions [i]))
					continue;
				text = $"{text}*.{extensions [i]}";
				if (i < extensions.Length - 1)
					text += sep;
			}
			return text;
		}

		protected static string CreateFilterEntry (IconEditor editor)
		{
			string description = editor.GetFileDialogDescription ();
			string extensions = CreateExtensionsString (editor.GetExtensions (), ",");
			string extensionsWithSemicolons = CreateExtensionsString (editor.GetExtensions (), ";");
			return $"{description}({extensions})|{extensionsWithSemicolons}";
		}

		public override object EditValue (ITypeDescriptorContext context, IServiceProvider provider, object value)
		{
			if (provider == null || provider.GetService (typeof (IWindowsFormsEditorService)) == null)
				return value;
			if (_fileDialog == null) {
				_fileDialog = new OpenFileDialog ();
				_fileDialog.Filter = CreateFilterEntry (this);
			}
			IntPtr focus = XplatUI.GetFocus ();
			try {
				if (_fileDialog.ShowDialog () == DialogResult.OK) {
					using (var stream = new FileStream (_fileDialog.FileName, FileMode.Open, FileAccess.Read, FileShare.Read))
						value = LoadFromStream (stream);
				}
			} finally {
				if (focus != IntPtr.Zero)
					XplatUI.SetFocus (focus);
			}
			return value;
		}

		public override UITypeEditorEditStyle GetEditStyle (ITypeDescriptorContext context) => UITypeEditorEditStyle.Modal;

		protected virtual string GetFileDialogDescription () => "Icon files";

		protected virtual string [] GetExtensions () => s_iconExtensions.ToArray ();

		public override bool GetPaintValueSupported (ITypeDescriptorContext context) => true;

		protected virtual Icon LoadFromStream (Stream stream) => new Icon (stream);

		public override void PaintValue (PaintValueEventArgs e)
		{
			if (!(e?.Value is Icon icon))
				return;
			// An icon smaller than the cell is centred in it unscaled.
			Rectangle rectangle = e.Bounds;
			if (icon.Width < rectangle.Width) {
				rectangle.X += (rectangle.Width - icon.Width) / 2;
				rectangle.Width = icon.Width;
			}
			if (icon.Height < rectangle.Height) {
				rectangle.Y += (rectangle.Height - icon.Height) / 2;
				rectangle.Height = icon.Height;
			}
			e.Graphics.DrawIcon (icon, rectangle);
		}
	}
}
