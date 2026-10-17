//
// System.ComponentModel.Design.BinaryEditor
//
// Authors:
//      Gert Driesen (drieseng@users.sourceforge.net)
//
// (C) 2004 Novell
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

using System.IO;
using System.Drawing;
using System.Windows.Forms;
using System.Windows.Forms.Design;
using System.Drawing.Design;

// Ported from dotnet/winforms (BinaryEditor and its BinaryUI); the Mono stub threw from EditValue. A byte[] or
// Stream value is shown in a ByteViewer in a modal dialog with Auto/ANSI/Unicode/Hexadecimal views and a Save
// button. The editor only shows the bytes (as .NET's), so the value comes back as it went in.
namespace System.ComponentModel.Design
{
	public sealed class BinaryEditor : UITypeEditor
	{
		private BinaryUI _binaryUI;

		public BinaryEditor ()
		{
		}

		public override object EditValue (ITypeDescriptorContext context, IServiceProvider provider, object value)
		{
			if (!(provider?.GetService (typeof (IWindowsFormsEditorService)) is IWindowsFormsEditorService editorService))
				return value;
			_binaryUI ??= new BinaryUI ();
			_binaryUI.Value = value;
			if (editorService.ShowDialog (_binaryUI) == DialogResult.OK)
				value = _binaryUI.Value;
			_binaryUI.Value = null;
			return value;
		}

		public override UITypeEditorEditStyle GetEditStyle (ITypeDescriptorContext context)
		{
			return UITypeEditorEditStyle.Modal;
		}

		/// <summary>The bytes a value shows: a byte[] itself, or what a Stream holds (read from its start and rewound).</summary>
		internal static byte [] ConvertToBytes (object value)
		{
			if (value is byte [] bytes)
				return bytes;
			if (value is Stream stream) {
				if (stream.CanSeek)
					stream.Position = 0;
				var copy = new MemoryStream ();
				stream.CopyTo (copy);
				if (stream.CanSeek)
					stream.Position = 0;
				return copy.ToArray ();
			}
			return null;
		}

		internal sealed class BinaryUI : Form
		{
			private readonly ByteViewer _byteViewer;
			private readonly RadioButton _automatic, _ansi, _unicode, _hex;
			private readonly Button _save, _ok;
			private object _value;

			public BinaryUI ()
			{
				Text = "Byte Array Property Editor";
				ClientSize = new Size (639, 487);
				MinimumSize = new Size (400, 300);
				FormBorderStyle = FormBorderStyle.Sizable;
				MaximizeBox = false;
				MinimizeBox = false;
				ShowInTaskbar = false;
				StartPosition = FormStartPosition.CenterParent;

				_byteViewer = new ByteViewer { Dock = DockStyle.Fill };
				var group = new GroupBox { Text = "Display", Dock = DockStyle.Fill };
				_automatic = new RadioButton { Text = "&Auto", AutoSize = true, Checked = true };
				_ansi = new RadioButton { Text = "A&NSI", AutoSize = true };
				_unicode = new RadioButton { Text = "&Unicode", AutoSize = true };
				_hex = new RadioButton { Text = "&Hexadecimal", AutoSize = true };
				var radios = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true };
				radios.Controls.AddRange (new Control [] { _automatic, _hex, _ansi, _unicode });
				group.Controls.Add (radios);
				foreach (RadioButton radio in new [] { _automatic, _ansi, _unicode, _hex })
					radio.CheckedChanged += RadioCheckedChanged;

				_save = new Button { Text = "&Save...", AutoSize = true };
				_save.Click += (s, e) => Save ();
				_ok = new Button { Text = "OK", AutoSize = true, DialogResult = DialogResult.OK };
				var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, FlowDirection = FlowDirection.RightToLeft };
				buttons.Controls.AddRange (new Control [] { _ok, _save });

				var top = new Panel { Dock = DockStyle.Top, Height = 60 };
				top.Controls.Add (group);
				Controls.Add (_byteViewer);
				Controls.Add (top);
				Controls.Add (buttons);
				AcceptButton = _ok;
				CancelButton = _ok;
			}

			internal ByteViewer Viewer => _byteViewer;
			internal RadioButton AnsiButton => _ansi;
			internal RadioButton UnicodeButton => _unicode;
			internal RadioButton HexButton => _hex;
			internal RadioButton AutoButton => _automatic;

			public object Value {
				get => _value;
				set {
					_value = value;
					byte [] bytes = ConvertToBytes (value);
					_byteViewer.SetBytes (bytes ?? Array.Empty<byte> ());
					_save.Enabled = bytes != null && bytes.Length > 0;
					// A new value starts in the mode the viewer picks for it.
					_automatic.Checked = true;
					_byteViewer.SetDisplayMode (DisplayMode.Auto);
				}
			}

			private void RadioCheckedChanged (object sender, EventArgs e)
			{
				if (!((RadioButton) sender).Checked)
					return;
				if (sender == _ansi)
					_byteViewer.SetDisplayMode (DisplayMode.Ansi);
				else if (sender == _unicode)
					_byteViewer.SetDisplayMode (DisplayMode.Unicode);
				else if (sender == _hex)
					_byteViewer.SetDisplayMode (DisplayMode.Hexdump);
				else
					_byteViewer.SetDisplayMode (DisplayMode.Auto);
			}

			private void Save ()
			{
				using (var dialog = new SaveFileDialog { FileName = "binarydata", Title = "Save Binary Data", Filter = "All Files (*.*)|*.*" }) {
					if (dialog.ShowDialog (this) == DialogResult.OK)
						_byteViewer.SaveToFile (dialog.FileName);
				}
			}
		}
	}
}
