//
// System.Drawing.Imaging.MetafileHeader.cs
//
// Author: Everaldo Canuto
// eMail: everaldo.canuto@bol.com.br
// Dennis Hayes (dennish@raytek.com)
//
// (C) 2002 Ximian, Inc.  http://www.ximian.com
// Copyright (C) 2004, 2006 Novell, Inc (http://www.novell.com)
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
//
// GDI+'s MetafileHeader, managed: every field is what gdiplus.dll derives (GetEmfHeader /
// GetWmfHeader, ported in backend/gdip/GpMetafileFormat.cs), not what a marshalled copy of it
// happened to read -- .NET Framework's copy mis-marshalled EmfPlusHeaderSize and LogicalDpiX/Y; these
// are GDI+'s own values.
//

using System.Drawing.WebGpuBackend.Gdip;

namespace System.Drawing.Imaging {

	public sealed class MetafileHeader {

		internal readonly GpMetafileHeader header;

		internal MetafileHeader (GpMetafileHeader header)
		{
			this.header = header.Clone ();
		}

		// GDI+'s MetafileHeader::IsDisplay: an EMF+ file recorded against a display.
		public bool IsDisplay ()
		{
			return IsEmfPlus () && (header.EmfPlusFlags & 1) != 0;
		}

		public bool IsEmf ()
		{
			return (Type == MetafileType.Emf);
		}

		public bool IsEmfOrEmfPlus ()
		{
			return (Type >= MetafileType.Emf);
		}

		public bool IsEmfPlus ()
		{
			return (Type >= MetafileType.EmfPlusOnly);
		}

		public bool IsEmfPlusDual ()
		{
			return (Type == MetafileType.EmfPlusDual);
		}

		public bool IsEmfPlusOnly ()
		{
			return (Type == MetafileType.EmfPlusOnly);
		}

		public bool IsWmf ()
		{
			return (Type == MetafileType.Wmf || Type == MetafileType.WmfPlaceable);
		}

		public bool IsWmfPlaceable ()
		{
			return (Type == MetafileType.WmfPlaceable);
		}

		// properties

		public Rectangle Bounds {
			get { return new Rectangle (header.X, header.Y, header.Width, header.Height); }
		}

		public float DpiX {
			get { return header.DpiX; }
		}

		public float DpiY {
			get { return header.DpiY; }
		}

		public int EmfPlusHeaderSize {
			get { return header.EmfPlusHeaderSize; }
		}

		public int LogicalDpiX {
			get { return header.LogicalDpiX; }
		}

		public int LogicalDpiY {
			get { return header.LogicalDpiY; }
		}

		public int MetafileSize {
			get { return header.Size; }
		}

		public MetafileType Type {
			get { return header.Type; }
		}

		public int Version {
			get { return header.Version; }
		}

		internal int EmfPlusFlags {
			get { return header.EmfPlusFlags; }
		}

		// The EMF's ENHMETAHEADER3 as GDI+ keeps it in the header (the EmfHeader of System.Drawing's
		// MetafileHeaderEmf).
		internal byte[] EmfHeaderBytes {
			get { return IsEmfOrEmfPlus () ? (byte[]) header.Raw.Clone () : null; }
		}

		// note: this always returns a new instance (where we can change
		// properties even if they don't seems to affect anything)
		public MetaHeader WmfHeader {
			get {
				if (!IsWmf ())
					throw new ArgumentException ("Parameter is not valid.");
				byte [] r = header.Raw;
				var w = new WmfMetaHeader ();
				w.file_type = Le.I16 (r, 0);
				w.header_size = Le.I16 (r, 2);
				w.version = Le.I16 (r, 4);
				w.file_size_low = Le.U16 (r, 6);
				w.file_size_high = Le.U16 (r, 8);
				w.num_of_objects = Le.I16 (r, 10);
				w.max_record_size = Le.I32 (r, 12);
				w.num_of_params = Le.I16 (r, 16);
				return new MetaHeader (w);
			}
		}
	}
}
