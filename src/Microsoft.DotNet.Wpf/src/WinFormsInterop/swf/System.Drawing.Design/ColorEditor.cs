// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
//
// A colour editor for the property grid, ported from .NET's System.Windows.Forms.Design
// (System/Drawing/Design/ColorEditor*.cs) so the drop-down is stock's own: the same tab control,
// owner-drawn lists, palette geometry and key handling.
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
	/// <summary>
	///  Provides an editor for visually picking a color.
	/// </summary>
	public partial class ColorEditor : UITypeEditor
	{
		/// <summary>
		///  Edits the given object value using the editor style provided by ColorEditor.GetEditStyle.
		/// </summary>
		public override object EditValue (ITypeDescriptorContext context, IServiceProvider provider, object value)
		{
			IWindowsFormsEditorService editorService = provider == null
				? null
				: provider.GetService (typeof (IWindowsFormsEditorService)) as IWindowsFormsEditorService;
			if (editorService == null)
				return value;

			using (ColorUI colorUI = new ColorUI (this)) {
				colorUI.Start (editorService, value);
				editorService.DropDownControl (colorUI);

				if (colorUI.Value is Color colorValue && colorValue != Color.Empty)
					value = colorValue;

				colorUI.End ();
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
			if (e.Value is Color color) {
				using (SolidBrush brush = new SolidBrush (color))
					e.Graphics.FillRectangle (brush, e.Bounds);
			}
		}
	}
}
