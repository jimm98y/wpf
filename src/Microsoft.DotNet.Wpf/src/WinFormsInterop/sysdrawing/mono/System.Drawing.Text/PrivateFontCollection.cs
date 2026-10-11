//
// System.Drawing.Text.PrivateFontCollection.cs
//
// (C) 2002 Ximian, Inc.  http://www.ximian.com
// Author: Everaldo Canuto everaldo.canuto@bol.com.br
//		Sanjay Gupta (gsanjay@novell.com)
//		Peter Dennis Bartok (pbartok@novell.com)
//
//
// Copyright (C) 2004 - 2006 Novell, Inc (http://www.novell.com)
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

using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Wpf.Interop.WebGpu.Composition.Text;

namespace System.Drawing.Text {

	// Fonts the application brings: each file registered with the managed font stack (so text drawn
	// in one of its families finds the file), its families listed here. A memory font is written to
	// a file of its own first, since the font stack reads faces from files.
	public sealed class PrivateFontCollection : FontCollection {

		static readonly HashSet<string> s_privateOnly = new HashSet<string> (StringComparer.OrdinalIgnoreCase);

		internal static bool IsPrivateOnly (string name)
		{
			lock (s_privateOnly) return s_privateOnly.Contains (name);
		}

		public PrivateFontCollection ()
		{
		}

		public void AddFontFile (string filename) 
		{
			if (filename == null)
				throw new ArgumentNullException ("filename");
			// System.Drawing's own check, then GdipPrivateAddFontFile's.
			if (!File.Exists (filename))
				throw new FileNotFoundException ();
			Live ();
			Add (Path.GetFullPath (filename), false);
		}

		void Add (string path, bool memory)
		{
			bool known (string n) => System.Drawing.WebGpuBackend.Gdip.GpFontFamily.Canonical (n) != null;
			var before = new HashSet<string> (FontFiles.FamilyNames (), StringComparer.OrdinalIgnoreCase);
			IReadOnlyList<string> names = FontFiles.RegisterPrivateFile (path);
			// GDI+ adds a file that holds no font without complaint (and nothing to the families); a
			// block of memory that is not one is FileNotFound.
			if (names.Count == 0) {
				if (memory)
					throw new FileNotFoundException ();
				return;
			}
			foreach (string n in names) {
				if (!before.Contains (n))
					lock (s_privateOnly) s_privateOnly.Add (n);
				if (!Contains (n, out _)) _families.Add (n);
			}
			_families.Sort (StringComparer.OrdinalIgnoreCase);
		}

		public void AddMemoryFont (IntPtr memory, int length) 
		{
			// GdipPrivateAddMemoryFont: no memory, no length or a disposed collection is InvalidParameter;
			// data that is not a font is FileNotFound.
			if (memory == IntPtr.Zero || length <= 0)
				throw new ArgumentException ("Parameter is not valid.");
			Live ();
			var data = new byte [length];
			Marshal.Copy (memory, data, 0, length);
			string dir = Path.Combine (Path.GetTempPath (), "System.Drawing.MemoryFonts");
			Directory.CreateDirectory (dir);
			string path = Path.Combine (dir, Convert.ToHexString (System.Security.Cryptography.SHA1.HashData (data)) + ".ttf");
			if (!File.Exists (path))
				File.WriteAllBytes (path, data);
			Add (path, true);
		}

		bool _disposed;

		internal override void Live ()
		{
			if (_disposed)
				throw new ArgumentException ("Parameter is not valid.");
		}

		protected override void Dispose (bool disposing)
		{
			_disposed = true;
			base.Dispose (disposing);
		}		
	}
}
