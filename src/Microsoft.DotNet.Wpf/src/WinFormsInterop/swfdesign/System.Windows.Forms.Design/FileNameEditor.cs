//
// System.Windows.Forms.Design.ComponentEditorForm.cs
//
// Author:
//   Dennis Hayes (dennish@raytek.com)
// (C) 2002 Ximian, Inc.  http://www.ximian.com
//

//
// Permission is hereby granted, free of charge, to any person obtaining
// a copy of this software and associated documentation files (the
// "Software"), to deal in the Software without restriction, including
// without limitation the rights to use, copy, modify, merge, publish,
// distribute, sublicense, and/or sell copies of the Software, and to
// permit persons to whom the Software is furnished to do so, subject to
// the following conditions:
// 
// The above copyright notice and this permission notice shall be
// included in all copies or substantial portions of the Software.
// 
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND,
// EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF
// MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND
// NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE
// LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION
// OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION
// WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
//
using System;
using System.ComponentModel;
using System.Drawing.Design;

namespace System.Windows.Forms.Design
{
	public class FileNameEditor : UITypeEditor
	{
		#region Public Instance Constructors

		public FileNameEditor ()
		{
		}

		#endregion Public Instance Constructors

		#region Override implementation of UITypeEditor

		// Ported from dotnet/winforms (the Mono stubs threw): an Open File dialog starting at the current name,
		// made once and initialized by InitializeDialog; only with an editor service, as .NET.
		private OpenFileDialog _openFileDialog;

		public override object EditValue (ITypeDescriptorContext context, IServiceProvider provider, object value)
		{
			if (!(provider?.GetService (typeof (IWindowsFormsEditorService)) is IWindowsFormsEditorService))
				return value;
			if (_openFileDialog == null) {
				_openFileDialog = new OpenFileDialog ();
				InitializeDialog (_openFileDialog);
			}
			if (value is string stringValue)
				_openFileDialog.FileName = stringValue;
			if (_openFileDialog.ShowDialog () == DialogResult.OK)
				return _openFileDialog.FileName;
			return value;
		}

		public override UITypeEditorEditStyle GetEditStyle (ITypeDescriptorContext context)
		{
			return UITypeEditorEditStyle.Modal;
		}

		#endregion Override implementation of UITypeEditor

		#region Protected Instance Methods

		protected virtual void InitializeDialog (OpenFileDialog openFileDialog)
		{
			if (openFileDialog == null)
				throw new ArgumentNullException (nameof (openFileDialog));
			openFileDialog.Filter = "All Files(*.*)|*.*";
			openFileDialog.Title = "Open File";
		}

		#endregion Protected Instance Methods
	}
}
