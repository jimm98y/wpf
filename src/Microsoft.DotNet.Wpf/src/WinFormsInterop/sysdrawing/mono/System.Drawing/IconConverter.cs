// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// System.Drawing.Common's IconConverter: an icon from its file's bytes; to a string, bytes or a bitmap.

using System.ComponentModel;
using System.Globalization;
using System.IO;

namespace System.Drawing
{
	public class IconConverter : ExpandableObjectConverter
	{
		public override bool CanConvertFrom (ITypeDescriptorContext context, Type sourceType)
			=> sourceType == typeof (byte []);

		public override bool CanConvertTo (ITypeDescriptorContext context, Type destinationType)
			=> destinationType == typeof (byte []) || destinationType == typeof (string)
				|| destinationType == typeof (Image) || destinationType == typeof (Bitmap);

		public override object ConvertFrom (ITypeDescriptorContext context, CultureInfo culture, object value)
		{
			if (value is byte [] buffer)
				return new Icon (new MemoryStream (buffer));
			return base.ConvertFrom (context, culture, value);
		}

		public override object ConvertTo (ITypeDescriptorContext context, CultureInfo culture, object value, Type destinationType)
		{
			if (destinationType == typeof (string)) {
				if (value == null)
					return SR.toStringNone;
				if (value is Icon)
					return value.ToString ();
			} else if (destinationType == typeof (byte [])) {
				if (value is Icon icon) {
					using (var ms = new MemoryStream ()) {
						icon.Save (ms);
						return ms.ToArray ();
					}
				}
			} else if ((destinationType == typeof (Image) || destinationType == typeof (Bitmap)) && value is Icon icon2) {
				return icon2.ToBitmap ();
			}
			throw GetConvertFromException (value);
		}
	}
}
