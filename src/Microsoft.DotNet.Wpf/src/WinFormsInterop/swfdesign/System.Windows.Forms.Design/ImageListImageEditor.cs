// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// The editor an ImageList's Images collection uses to add pictures, as .NET's: a multi-select open dialog over the
// bitmap formats (metafiles left out), each file read into memory so it is not left locked, an .ico read as an
// icon. Mono's was a stub behind a symbol never defined.

using System.Collections;
using System.ComponentModel;
using System.Drawing;
using System.Drawing.Design;
using System.IO;
using System.Reflection;

namespace System.Windows.Forms.Design
{
	[CLSCompliant (false)]
	public class ImageListImageEditor : ImageEditor
	{
		internal static Type [] s_imageExtenders = new Type [] { typeof (BitmapEditor) };
		OpenFileDialog fileDialog;

		public ImageListImageEditor ()
		{
		}

		protected override Type [] GetImageExtenders () => s_imageExtenders;

		public override object EditValue (ITypeDescriptorContext context, IServiceProvider provider, object value)
		{
			if (provider == null)
				return value;
			var images = new ArrayList ();
			if (provider.GetService (typeof (IWindowsFormsEditorService)) == null)
				return images;
			if (fileDialog == null) {
				fileDialog = new OpenFileDialog { Multiselect = true };
				string filter = CreateFilterEntry (this);
				foreach (Type extender in GetImageExtenders ()) {
					Type mine = GetType ();
					var editor = (ImageEditor) Activator.CreateInstance (extender,
						BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.CreateInstance, null, null, null);
					if (editor != null && !mine.Equals (editor.GetType ()) && mine.IsInstanceOfType (editor))
						filter += "|" + CreateFilterEntry (editor);
				}
				fileDialog.Filter = filter;
			}
			if (fileDialog.ShowDialog () == DialogResult.OK) {
				foreach (string name in fileDialog.FileNames) {
					// read into memory: the image owns its stream, and the file is not left locked
					var stream = new MemoryStream (File.ReadAllBytes (name));
					Image image = name.EndsWith (".ico", StringComparison.Ordinal)
						? new Icon (stream).ToBitmap ()
						: Image.FromStream (stream);
					image.Tag = Path.GetFileName (name);
					images.Add (image);
				}
			}
			return images;
		}

		protected override string GetFileDialogDescription () => "All image files";

		public override bool GetPaintValueSupported (ITypeDescriptorContext context) => true;

		public override void PaintValue (PaintValueEventArgs e)
		{
			base.PaintValue (e);
		}
	}
}
