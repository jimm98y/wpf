// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// System.Drawing.Common's ImageConverter: an image from bytes (an OLE "PBrush" embedding's bitmap, or
// any file's) or from an icon; to a string or the image's file bytes.

using System.ComponentModel;
using System.Globalization;
using System.IO;

namespace System.Drawing
{
	public class ImageConverter : TypeConverter
	{
		public override bool CanConvertFrom (ITypeDescriptorContext context, Type sourceType)
			=> sourceType == typeof (byte []) || sourceType == typeof (Icon);

		public override bool CanConvertTo (ITypeDescriptorContext context, Type destinationType)
			=> destinationType == typeof (byte []) || destinationType == typeof (string);

		public override object ConvertFrom (ITypeDescriptorContext context, CultureInfo culture, object value)
		{
			if (value is Icon icon)
				return icon.ToBitmap ();
			if (value is byte [] bytes)
				return Image.FromStream (GetBitmapStream (bytes) ?? new MemoryStream (bytes));
			return base.ConvertFrom (context, culture, value);
		}

		public override object ConvertTo (ITypeDescriptorContext context, CultureInfo culture, object value, Type destinationType)
		{
			if (destinationType == typeof (string)) {
				if (value == null)
					return SR.toStringNone;
				if (value is Image)
					return value.ToString ();
			} else if (destinationType == typeof (byte [])) {
				if (value == null)
					return Array.Empty<byte> ();
				if (value is Image image) {
					using (var ms = new MemoryStream ()) {
						image.SaveWithRawFormat (ms);
						return ms.ToArray ();
					}
				}
			}
			throw GetConvertFromException (value);
		}

		public override PropertyDescriptorCollection GetProperties (ITypeDescriptorContext context, object value, Attribute [] attributes)
			=> TypeDescriptor.GetProperties (typeof (Image), attributes);

		public override bool GetPropertiesSupported (ITypeDescriptorContext context) => true;

		// An OLE 1 object header (OLEOBJHEADER) wrapping a "PBrush" embedding: the bitmap file inside.
		static MemoryStream GetBitmapStream (byte [] d)
		{
			const int HeaderSize = 20;
			if (d.Length < HeaderSize)
				return null;
			int typ = BitConverter.ToUInt16 (d, 0), cbHdr = BitConverter.ToUInt16 (d, 2);
			uint oot = BitConverter.ToUInt32 (d, 4);
			int cchName = BitConverter.ToUInt16 (d, 8), cchClass = BitConverter.ToUInt16 (d, 10);
			int ibName = BitConverter.ToUInt16 (d, 12), ibClass = BitConverter.ToUInt16 (d, 14);
			short ptx = BitConverter.ToInt16 (d, 16), pty = BitConverter.ToInt16 (d, 18);
			if (typ != 7189 || oot != 2 || ibName != HeaderSize || ibClass != HeaderSize + cchName
			    || cbHdr != HeaderSize + cchClass + cchName || ptx != -1 || pty != -1)
				return null;
			int pos = HeaderSize + cchName + cchClass;
			if (!Read (d, ref pos, out int _) || !Read (d, ref pos, out int fmtid) || fmtid != 2 || !Read (d, ref pos, out int nameLen)
			    || nameLen != 7 || pos + nameLen > d.Length || System.Text.Encoding.ASCII.GetString (d, pos, 7) != "PBrush\0")
				return null;
			pos += nameLen;
			if (!Read (d, ref pos, out int z1) || z1 != 0 || !Read (d, ref pos, out int z2) || z2 != 0 || !Read (d, ref pos, out int size)
			    || size < 0 || pos + size > d.Length)
				return null;
			return new MemoryStream (d, pos, size);
		}

		static bool Read (byte [] d, ref int pos, out int v)
		{
			v = 0;
			if (pos + 4 > d.Length)
				return false;
			v = BitConverter.ToInt32 (d, pos);
			pos += 4;
			return true;
		}
	}
}
