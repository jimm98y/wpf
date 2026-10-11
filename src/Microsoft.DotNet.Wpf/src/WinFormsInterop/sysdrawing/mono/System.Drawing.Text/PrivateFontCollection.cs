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

			// this ensure the filename is valid (or throw the correct exception)
			string fname = Path.GetFullPath (filename);

			if (!File.Exists (fname))
				throw new FileNotFoundException ();

			Add (fname);
		}

		void Add (string path)
		{
			bool known (string n) => System.Drawing.WebGpuBackend.Gdip.GpFontFamily.Canonical (n) != null;
			var before = new HashSet<string> (FontFiles.FamilyNames (), StringComparer.OrdinalIgnoreCase);
			IReadOnlyList<string> names = FontFiles.RegisterPrivateFile (path);
			// note: MS throw the same exception FileNotFoundException if the file exists but isn't a valid font file
			if (names.Count == 0)
				throw new FileNotFoundException ();
			foreach (string n in names) {
				if (!before.Contains (n))
					lock (s_privateOnly) s_privateOnly.Add (n);
				if (!Contains (n, out _)) _families.Add (n);
			}
			_families.Sort (StringComparer.OrdinalIgnoreCase);
		}

		public void AddMemoryFont (IntPtr memory, int length) 
		{
			if (memory == IntPtr.Zero)
				throw new ArgumentException ("Parameter is not valid.");
			// note: MS throw FileNotFoundException if something is bad with the data (except for a null pointer)
			if (length <= 0)
				throw new FileNotFoundException ();
			var data = new byte [length];
			Marshal.Copy (memory, data, 0, length);
			string dir = Path.Combine (Path.GetTempPath (), "System.Drawing.MemoryFonts");
			Directory.CreateDirectory (dir);
			string path = Path.Combine (dir, Convert.ToHexString (System.Security.Cryptography.SHA1.HashData (data)) + ".ttf");
			if (!File.Exists (path))
				File.WriteAllBytes (path, data);
			Add (path);
		}

		protected override void Dispose (bool disposing)
		{
			base.Dispose (disposing);
		}		
	}
}
