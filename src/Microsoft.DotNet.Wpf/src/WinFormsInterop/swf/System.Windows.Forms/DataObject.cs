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
// Copyright (c) 2005 Novell, Inc. (http://www.novell.com)
//
// Authors:
//	Peter Bartok	(pbartok@novell.com)
//
//

// COMPLETE

using System;
using System.IO;
using System.Drawing;
using System.Collections;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace System.Windows.Forms {
	[ClassInterface(ClassInterfaceType.None)]
	public class DataObject : IDataObject, System.Runtime.InteropServices.ComTypes.IDataObject
	 {
		#region DataObject.Entry Class
		private class Entry {
			#region Local Variables
			private string	type;
			private object	data;
			private bool	autoconvert;
			internal Entry	next;
			#endregion	// Local Variables

			#region Constructors
			internal Entry(string type, object data, bool autoconvert) {
				this.type = type;
				this.data = data;
				this.autoconvert = autoconvert;
			}
			#endregion	// Constructors

			#region Properties
			public object Data {
				get {
					return data;
				}

				set {
					data = value;
				}
			}
			public bool AutoConvert {
				get { 
					return autoconvert;
				}
				set {
					autoconvert = value;
				}
			}
			#endregion	// Properties

			#region Methods
			public static int Count(Entry entries) {
				int	result;

				result = 0;

				while (entries != null) {
					result++;
					entries = entries.next;
				}

				return result;
			}

			public static Entry Find (Entry entries, string type) {
				return Find (entries, type, false);
			}

			public static Entry Find(Entry entries, string type, bool only_convertible) {
				while (entries != null) {
					bool available = true;
					if (only_convertible && !entries.autoconvert)
						available = false;
					if (available && String.Compare (entries.type, type, true) == 0) {
						return entries;
					}
					entries = entries.next;
				}

				return null;
			}

			public static Entry FindConvertible(Entry entries, string type) {
				Entry e;

				e = Find(entries, type);
				if (e != null) {
					return e;
				}

				// map to *any* other text format if needed
				if (type == DataFormats.StringFormat || type == DataFormats.Text || type == DataFormats.UnicodeText) {
					e = entries;
					while (e != null) {
						if (e.type == DataFormats.StringFormat || e.type == DataFormats.Text || e.type == DataFormats.UnicodeText)
							return e;

						e = e.next;
					}
				}

				return null;
			}

			public static string[] Entries(Entry entries, bool convertible) {
				Entry		e;
				ArrayList	list;
				string[]	result;

				// Initially store into something that we can grow easily
				list = new ArrayList(Entry.Count(entries));
				e = entries;

				if (convertible) {
					// Add the convertibles
					Entry text_entry = Entry.Find (entries, DataFormats.Text);
					Entry utext_entry = Entry.Find (entries, DataFormats.UnicodeText);
					Entry string_entry = Entry.Find (entries, DataFormats.StringFormat);
					bool text_convertible = text_entry != null && text_entry.AutoConvert;
					bool utext_convertible = utext_entry != null && utext_entry.AutoConvert;
					bool string_convertible = string_entry != null && string_entry.AutoConvert;

					if (text_convertible || utext_convertible || string_convertible) {
						list.Add (DataFormats.StringFormat);
						list.Add (DataFormats.UnicodeText);
						list.Add (DataFormats.Text);
					}
				}

				while (e != null) {
					if (!list.Contains (e.type))
						list.Add (e.type);
					e = e.next;
				}

				// Copy the results into a string array
				result = new string[list.Count];
				for (int i = 0; i < list.Count; i++) {
					result[i] = (string)list[i];
				}

				return result;
			}
			#endregion	// Methods
		}
		#endregion	// DataObject.Entry class

		#region Local Variables
		private Entry	entries;
		#endregion	// Local Variables

		#region Public Constructors
		public DataObject() {
			entries = null;
		}

		public DataObject(object data) {
			SetData(data);
		}

		public DataObject(string format, object data) {
			SetData(format, data);
		}
		#endregion	// Public Constructors

		#region Public Instance Methods
		public virtual bool ContainsAudio ()
		{
			return GetDataPresent (DataFormats.WaveAudio, true);
		}
		
		public virtual bool ContainsFileDropList ()
		{
			return GetDataPresent (DataFormats.FileDrop, true);
		}
		
		public virtual bool ContainsImage ()
		{
			return GetDataPresent (DataFormats.Bitmap, true);
		}
		
		public virtual bool ContainsText ()
		{
			return GetDataPresent (DataFormats.UnicodeText, true);
		}
		
		public virtual bool ContainsText (TextDataFormat format)
		{
			if (!Enum.IsDefined (typeof (TextDataFormat), format))
				throw new InvalidEnumArgumentException (string.Format ("Enum argument value '{0}' is not valid for TextDataFormat", format));

			return GetDataPresent (TextFormatToDataFormat (format), true);
		}
		
		public virtual Stream GetAudioStream ()
		{
			return (Stream)GetData (DataFormats.WaveAudio, true);
		}

		public virtual object GetData(string format) {
			return GetData(format, true);
		}

		public virtual object GetData(string format, bool autoConvert) {
			Entry e;
			if (autoConvert) {
				e = Entry.FindConvertible(entries, format);
			} else {
				e = Entry.Find(entries, format);
			}
			if (e == null)
				return null;
			return e.Data;
		}

		public virtual object GetData(Type format) {
			return GetData(format.FullName, true);
		}

		public virtual bool GetDataPresent(string format) {
			return GetDataPresent(format, true);
		}

		public virtual bool GetDataPresent(string format, bool autoConvert) {
			if (autoConvert) {
				return Entry.FindConvertible(entries, format) != null;
			} else {
				return Entry.Find(entries, format) != null;
			}
		}

		public virtual bool GetDataPresent(Type format) {
			return GetDataPresent(format.FullName, true);
		}

		public virtual StringCollection GetFileDropList ()
		{
			return (StringCollection)GetData (DataFormats.FileDrop, true);
		}
		public virtual string[] GetFormats() {
			return GetFormats(true);
		}

		public virtual string[] GetFormats(bool autoConvert) {
			return Entry.Entries(entries, autoConvert);
		}

		public virtual Image GetImage ()
		{
			return (Image)GetData (DataFormats.Bitmap, true);
		}

		public virtual string GetText ()
		{
			return (string)GetData (DataFormats.UnicodeText, true);
		}

		public virtual string GetText (TextDataFormat format)
		{
			if (!Enum.IsDefined (typeof (TextDataFormat), format))
				throw new InvalidEnumArgumentException (string.Format ("Enum argument value '{0}' is not valid for TextDataFormat", format));

			return (string)GetData (TextFormatToDataFormat (format), false);
		}

		public virtual void SetAudio (byte[] audioBytes)
		{
			if (audioBytes == null)
				throw new ArgumentNullException ("audioBytes");

			MemoryStream ms = new MemoryStream (audioBytes);

			SetAudio (ms);
		}

		public virtual void SetAudio (Stream audioStream)
		{
			if (audioStream == null)
				throw new ArgumentNullException ("audioStream");

			SetData (DataFormats.WaveAudio, audioStream);
		}

		public virtual void SetData(object data) {
			SetData(data.GetType(), data); 
		}

		public virtual void SetData(string format, bool autoConvert, object data) {
			Entry	entry;
			Entry	e;

			entry = Entry.Find(entries, format);

			if (entry == null) {
				entry = new DataObject.Entry(format, data, autoConvert);
			} else {
				entry.Data = data;
				return;
			}

			lock (this) {
				if (entries == null) {
					entries = entry;
				} else {
					// Insert into the list of known/defined formats
					e = entries;

					while (e.next != null) {
						e = e.next;
					}
					e.next = entry;
				}
			}
		}

		public virtual void SetData(string format, object data) {
			SetData(format, true, data);
		}

		public virtual void SetData(Type format, object data) {
			SetData(EnsureFormat(format), true, data);
		}
		
		[MonoInternalNote ("Needs additional checks for valid paths, see MSDN")]
		public virtual void SetFileDropList (StringCollection filePaths)
		{
			if (filePaths == null)
				throw new ArgumentNullException ("filePaths");

			SetData (DataFormats.FileDrop, filePaths);
		}

		public virtual void SetImage (Image image)
		{
			if (image == null)
				throw new ArgumentNullException ("image");

			SetData (DataFormats.Bitmap, image);
		}

		public virtual void SetText (string textData)
		{
			if (string.IsNullOrEmpty (textData))
				throw new ArgumentNullException ("text");

			SetData (DataFormats.UnicodeText, textData);
		}

		public virtual void SetText (string textData, TextDataFormat format)
		{
			if (string.IsNullOrEmpty (textData))
				throw new ArgumentNullException ("text");
			if (!Enum.IsDefined (typeof (TextDataFormat), format))
				throw new InvalidEnumArgumentException (string.Format ("Enum argument value '{0}' is not valid for TextDataFormat", format));

			switch (format) {
				case TextDataFormat.Text:
					SetData (DataFormats.Text, textData);
					break;
				case TextDataFormat.UnicodeText:
					SetData (DataFormats.UnicodeText, textData);
					break;
				case TextDataFormat.Rtf:
					SetData (DataFormats.Rtf, textData);
					break;
				case TextDataFormat.Html:
					SetData (DataFormats.Html, textData);
					break;
				case TextDataFormat.CommaSeparatedValue:
					SetData (DataFormats.CommaSeparatedValue, textData);
					break;
			}
		}
		#endregion	// Public Instance Methods

		#region Private Methods
		internal string EnsureFormat(string name) {
			DataFormats.Format f;

			f = DataFormats.Format.Find(name);
			if (f == null) {
				// Register the format
				f = DataFormats.Format.Add(name);
			}

			return f.Name;
		}

		internal string EnsureFormat(Type type) {
			return EnsureFormat(type.FullName);
		}

		private string TextFormatToDataFormat (TextDataFormat format)
		{
			switch (format) {
				case TextDataFormat.Text:
				default:
					return DataFormats.Text;
				case TextDataFormat.UnicodeText:
					return DataFormats.UnicodeText;
				case TextDataFormat.Rtf:
					return DataFormats.Rtf;
				case TextDataFormat.Html:
					return DataFormats.Html;
				case TextDataFormat.CommaSeparatedValue:
					return DataFormats.CommaSeparatedValue;
			}
		}
		#endregion	// Private Methods

		#region IDataObject Members
		// System.Runtime.InteropServices.ComTypes.IDataObject over this object's data, as dotnet/winforms' DataObject
		// answers it (its Composition's managed-to-native adapter, seen through the runtime's ComTypes facade: a
		// failing HRESULT of a void method arrives as a COMException). Every member of the Mono stub threw
		// NotImplementedException. Data goes out in an HGLOBAL: text as ANSI or UTF-16 with its terminator, a file
		// list as DROPFILES, a stream or byte array as its bytes, a string of any other format as UTF-16.

		private const int E_NOTIMPL = unchecked ((int) 0x80004001);
		private const int E_POINTER = unchecked ((int) 0x80004003);
		private const int DV_E_FORMATETC = unchecked ((int) 0x80040064);
		private const int DV_E_TYMED = unchecked ((int) 0x80040069);
		private const int DV_E_DVASPECT = unchecked ((int) 0x8004006B);
		private const int OLE_E_ADVISENOTSUPPORTED = unchecked ((int) 0x80040003);
		private const int DATA_S_SAMEFORMATETC = 0x00040130;

		private static readonly string [] s_standardFormats = {
			null, DataFormats.Text, DataFormats.Bitmap, DataFormats.MetafilePict, DataFormats.SymbolicLink, DataFormats.Dif,
			DataFormats.Tiff, DataFormats.OemText, DataFormats.Dib, DataFormats.Palette, DataFormats.PenData, DataFormats.Riff,
			DataFormats.WaveAudio, DataFormats.UnicodeText, DataFormats.EnhancedMetafile, DataFormats.FileDrop, DataFormats.Locale,
		};

		[DllImport ("user32", EntryPoint = "RegisterClipboardFormatW", CharSet = CharSet.Unicode)]
		private static extern int RegisterClipboardFormatW (string format);

		[DllImport ("user32", EntryPoint = "GetClipboardFormatNameW", CharSet = CharSet.Unicode)]
		private static extern int GetClipboardFormatNameW (int format, System.Text.StringBuilder name, int max);

		/// <summary>The clipboard format id of a format name: the Win32 constants for the standard ones, the
		/// registered id for the others (on Windows), and DataFormats' own id elsewhere.</summary>
		internal static short FormatId (string format)
		{
			int i = Array.IndexOf (s_standardFormats, format);
			if (i > 0)
				return (short) i;
			if (OperatingSystem.IsWindows ())
				return (short) RegisterClipboardFormatW (format);
			return (short) DataFormats.GetFormat (format).Id;
		}

		private static string FormatName (short id)
		{
			if (id > 0 && id < s_standardFormats.Length)
				return s_standardFormats [id];
			if (OperatingSystem.IsWindows ()) {
				var sb = new System.Text.StringBuilder (256);
				if (GetClipboardFormatNameW (id & 0xffff, sb, sb.Capacity) > 0)
					return sb.ToString ();
			}
			return DataFormats.GetFormat (id & 0xffff).Name;
		}

		private static bool TymedUsable (System.Runtime.InteropServices.ComTypes.TYMED tymed)
			=> (tymed & System.Runtime.InteropServices.ComTypes.TYMED.TYMED_HGLOBAL) != 0;

		int System.Runtime.InteropServices.ComTypes.IDataObject.DAdvise (ref System.Runtime.InteropServices.ComTypes.FORMATETC pFormatetc, System.Runtime.InteropServices.ComTypes.ADVF advf, System.Runtime.InteropServices.ComTypes.IAdviseSink adviseSink, out int connection)
		{
			connection = 0;
			return E_NOTIMPL;
		}

		void System.Runtime.InteropServices.ComTypes.IDataObject.DUnadvise (int connection)
		{
			Marshal.ThrowExceptionForHR (E_NOTIMPL);
		}

		int System.Runtime.InteropServices.ComTypes.IDataObject.EnumDAdvise (out System.Runtime.InteropServices.ComTypes.IEnumSTATDATA enumAdvise)
		{
			enumAdvise = null;
			return OLE_E_ADVISENOTSUPPORTED;
		}

		System.Runtime.InteropServices.ComTypes.IEnumFORMATETC System.Runtime.InteropServices.ComTypes.IDataObject.EnumFormatEtc (System.Runtime.InteropServices.ComTypes.DATADIR direction)
		{
			if (direction != System.Runtime.InteropServices.ComTypes.DATADIR.DATADIR_GET)
				Marshal.ThrowExceptionForHR (E_NOTIMPL);
			string [] formats = GetFormats ();
			var list = new System.Runtime.InteropServices.ComTypes.FORMATETC [formats.Length];
			for (int i = 0; i < formats.Length; i++) {
				list [i] = new System.Runtime.InteropServices.ComTypes.FORMATETC {
					cfFormat = FormatId (formats [i]),
					dwAspect = System.Runtime.InteropServices.ComTypes.DVASPECT.DVASPECT_CONTENT,
					lindex = -1,
					tymed = formats [i] == DataFormats.Bitmap ? System.Runtime.InteropServices.ComTypes.TYMED.TYMED_GDI : System.Runtime.InteropServices.ComTypes.TYMED.TYMED_HGLOBAL,
				};
			}
			return new FormatEnumerator (list, 0);
		}

		int System.Runtime.InteropServices.ComTypes.IDataObject.GetCanonicalFormatEtc (ref System.Runtime.InteropServices.ComTypes.FORMATETC formatIn, out System.Runtime.InteropServices.ComTypes.FORMATETC formatOut)
		{
			formatOut = default;
			return DATA_S_SAMEFORMATETC;
		}

		void System.Runtime.InteropServices.ComTypes.IDataObject.GetData (ref System.Runtime.InteropServices.ComTypes.FORMATETC format, out System.Runtime.InteropServices.ComTypes.STGMEDIUM medium)
		{
			medium = default;
			if (!TymedUsable (format.tymed))
				Marshal.ThrowExceptionForHR (DV_E_TYMED);
			byte [] bytes = GetHGlobalBytes (format);
			IntPtr hglobal = Marshal.AllocHGlobal (Math.Max (1, bytes.Length));
			Marshal.Copy (bytes, 0, hglobal, bytes.Length);
			if (bytes.Length == 0)
				Marshal.WriteByte (hglobal, 0);
			medium.tymed = System.Runtime.InteropServices.ComTypes.TYMED.TYMED_HGLOBAL;
			medium.unionmember = hglobal;
		}

		void System.Runtime.InteropServices.ComTypes.IDataObject.GetDataHere (ref System.Runtime.InteropServices.ComTypes.FORMATETC format, ref System.Runtime.InteropServices.ComTypes.STGMEDIUM medium)
		{
			if (!TymedUsable (format.tymed) || medium.tymed != System.Runtime.InteropServices.ComTypes.TYMED.TYMED_HGLOBAL)
				Marshal.ThrowExceptionForHR (DV_E_TYMED);
			if (medium.unionmember == IntPtr.Zero)
				Marshal.ThrowExceptionForHR (E_POINTER);
			byte [] bytes = GetHGlobalBytes (format);
			// The caller's HGLOBAL, grown to the data (.NET's SaveDataToHGLOBAL reallocates the handle it is given).
			medium.unionmember = Marshal.ReAllocHGlobal (medium.unionmember, (IntPtr) Math.Max (1, bytes.Length));
			Marshal.Copy (bytes, 0, medium.unionmember, bytes.Length);
		}

		int System.Runtime.InteropServices.ComTypes.IDataObject.QueryGetData (ref System.Runtime.InteropServices.ComTypes.FORMATETC format)
		{
			if (format.dwAspect != System.Runtime.InteropServices.ComTypes.DVASPECT.DVASPECT_CONTENT)
				return DV_E_DVASPECT;
			if (!TymedUsable (format.tymed))
				return DV_E_TYMED;
			if (format.cfFormat == 0)
				return 1;   // S_FALSE: no format asked
			string name = FormatName (format.cfFormat);
			return name != null && GetDataPresent (name) ? 0 : DV_E_FORMATETC;
		}

		void System.Runtime.InteropServices.ComTypes.IDataObject.SetData (ref System.Runtime.InteropServices.ComTypes.FORMATETC formatIn, ref System.Runtime.InteropServices.ComTypes.STGMEDIUM medium, bool release)
		{
			// .NET accepts only the drag-loop formats of the shell's drag images; anything else is E_NOTIMPL.
			Marshal.ThrowExceptionForHR (E_NOTIMPL);
		}

		/// <summary>The bytes the HGLOBAL of a format holds; DV_E_FORMATETC when this object has no such data.</summary>
		private byte [] GetHGlobalBytes (System.Runtime.InteropServices.ComTypes.FORMATETC format)
		{
			string name = FormatName (format.cfFormat);
			object data = name == null ? null : GetData (name);
			if (data == null)
				Marshal.ThrowExceptionForHR (DV_E_FORMATETC);
			if (name == DataFormats.FileDrop && data is string [] files)
				return DropFiles (files);
			if (data is Stream stream) {
				using (var ms = new MemoryStream ()) {
					long at = stream.CanSeek ? stream.Position : 0;
					if (stream.CanSeek) stream.Position = 0;
					stream.CopyTo (ms);
					if (stream.CanSeek) stream.Position = at;
					return ms.ToArray ();
				}
			}
			if (data is byte [] raw)
				return raw;
			string text = data as string ?? data.ToString ();
			if (name == DataFormats.Text || name == DataFormats.OemText || name == DataFormats.Rtf || name == DataFormats.Html || name == DataFormats.CommaSeparatedValue) {
				// ANSI (UTF-8 for HTML, as .NET writes it), NUL-terminated.
				System.Text.Encoding enc = name == DataFormats.Html ? System.Text.Encoding.UTF8 : System.Text.Encoding.Default;
				byte [] b = enc.GetBytes (text);
				Array.Resize (ref b, b.Length + 1);
				return b;
			}
			byte [] u = System.Text.Encoding.Unicode.GetBytes (text);
			Array.Resize (ref u, u.Length + 2);
			return u;
		}

		/// <summary>CF_HDROP: a DROPFILES header (wide names), the names each NUL-terminated, then one more NUL.</summary>
		private static byte [] DropFiles (string [] files)
		{
			const int header = 20;   // DWORD pFiles, POINT pt, BOOL fNC, BOOL fWide
			var names = new System.Text.StringBuilder ();
			foreach (string f in files)
				names.Append (f).Append ('\0');
			names.Append ('\0');
			byte [] text = System.Text.Encoding.Unicode.GetBytes (names.ToString ());
			byte [] result = new byte [header + text.Length];
			BitConverter.GetBytes (header).CopyTo (result, 0);
			BitConverter.GetBytes (1).CopyTo (result, 16);   // fWide
			text.CopyTo (result, header);
			return result;
		}

		/// <summary>IEnumFORMATETC over a fixed list (.NET's FormatEnumerator).</summary>
		private sealed class FormatEnumerator : System.Runtime.InteropServices.ComTypes.IEnumFORMATETC
		{
			private readonly System.Runtime.InteropServices.ComTypes.FORMATETC [] _formats;
			private int _current;

			internal FormatEnumerator (System.Runtime.InteropServices.ComTypes.FORMATETC [] formats, int current)
			{
				_formats = formats;
				_current = current;
			}

			public int Next (int celt, System.Runtime.InteropServices.ComTypes.FORMATETC [] rgelt, int [] pceltFetched)
			{
				int fetched = 0;
				while (fetched < celt && _current < _formats.Length && rgelt != null && fetched < rgelt.Length)
					rgelt [fetched++] = _formats [_current++];
				if (pceltFetched != null && pceltFetched.Length > 0)
					pceltFetched [0] = fetched;
				return fetched == celt ? 0 : 1;   // S_OK : S_FALSE
			}

			public int Skip (int celt)
			{
				if (_current + celt >= _formats.Length) {
					_current = _formats.Length;
					return 1;
				}
				_current += celt;
				return 0;
			}

			public int Reset ()
			{
				_current = 0;
				return 0;
			}

			public void Clone (out System.Runtime.InteropServices.ComTypes.IEnumFORMATETC newEnum)
			{
				newEnum = new FormatEnumerator (_formats, _current);
			}
		}
		#endregion
	 }
}
