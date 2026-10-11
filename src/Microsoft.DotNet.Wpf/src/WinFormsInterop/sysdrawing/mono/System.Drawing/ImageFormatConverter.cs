//
// System.Drawing.ImageFormatConverter.cs
//
// Author:
//   Dennis Hayes (dennish@Raytek.com)
//   Sanjay Gupta (gsanjay@novell.com)
//
// (C) 2002 Ximian, Inc
//

//
// Copyright (C) 2004 Novell, Inc (http://www.novell.com)
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
using System.Globalization;
using System.Drawing.Imaging;
using System.ComponentModel.Design.Serialization;
using System.Reflection;

namespace System.Drawing
{
	/// <summary>
	/// Summary description for ImageFormatConverter.
	/// </summary>
	public class ImageFormatConverter : TypeConverter
	{
		public ImageFormatConverter ()
		{
		}

		public override bool CanConvertFrom (ITypeDescriptorContext context, Type sourceType)
		{
			if (sourceType == typeof (string))
				return true;
				
			return base.CanConvertFrom (context, sourceType);
		}

		public override bool CanConvertTo (ITypeDescriptorContext context, Type destinationType)
		{
			if (destinationType == typeof (string))
				return true;
				
			if (destinationType == typeof (InstanceDescriptor))
				return true;

			return base.CanConvertTo (context, destinationType);
		}
		
		// System.Drawing.Common's: "[ImageFormat: <guid>]", or a format's name in any case; anything
		// else is a FormatException.
		public override object ConvertFrom (ITypeDescriptorContext context, CultureInfo culture, object value)
		{
			if (!(value is string text))
				return base.ConvertFrom (context, culture, value);
			if (text [0] == '[' && text.Length >= 50 && Guid.TryParse (text.AsSpan (14, 36), out Guid guid))
				return new ImageFormat (guid);
			foreach (ImageFormat f in new [] { ImageFormat.Bmp, ImageFormat.Emf, ImageFormat.Exif, ImageFormat.Gif, ImageFormat.Icon,
			                                   ImageFormat.Jpeg, ImageFormat.MemoryBmp, ImageFormat.Png, ImageFormat.Tiff, ImageFormat.Wmf,
			                                   ImageFormat.Heif, ImageFormat.Webp }) {
				string name = f.Guid == ImageFormat.MemoryBmp.Guid ? "MemoryBmp" : f.ToString ();
				if (text.Equals (name, StringComparison.OrdinalIgnoreCase))
					return f;
			}
			throw new FormatException (SR.Format (SR.ConvertInvalidPrimitive, text, "ImageFormat"));
		}

		public override object ConvertTo (ITypeDescriptorContext context, CultureInfo culture, object value, Type destinationType )
		{
			if (value is ImageFormat) {
				ImageFormat c = (ImageFormat) value;
				string prop = null;
				if (c.Guid.Equals (ImageFormat.Bmp.Guid))
					prop = "Bmp";
				else if (c.Guid.Equals (ImageFormat.Emf.Guid))
					prop = "Emf";
				else if (c.Guid.Equals (ImageFormat.Exif.Guid))
					prop = "Exif";
				else if (c.Guid.Equals (ImageFormat.Gif.Guid))
					prop = "Gif";
				else if (c.Guid.Equals (ImageFormat.Icon.Guid))
					prop = "Icon";
				else if (c.Guid.Equals (ImageFormat.Jpeg.Guid))
					prop = "Jpeg";
				else if (c.Guid.Equals (ImageFormat.MemoryBmp.Guid))
					prop = "MemoryBmp";
				else if (c.Guid.Equals (ImageFormat.Png.Guid))
					prop = "Png";
				else if (c.Guid.Equals (ImageFormat.Tiff.Guid))
					prop = "Tiff";
				else if (c.Guid.Equals (ImageFormat.Wmf.Guid))
					prop = "Wmf";

				if (destinationType == typeof (string)) {
					return prop != null ? prop : c.ToString ();
				} else if (destinationType == typeof (InstanceDescriptor)) {
					if (prop != null){
						return new InstanceDescriptor (typeof (ImageFormat).GetTypeInfo ().GetProperty (prop), null);
					} else {
						ConstructorInfo ctor = typeof(ImageFormat).GetTypeInfo ().GetConstructor (new Type[] {typeof(Guid)} );
						return new InstanceDescriptor (ctor, new object[] {c.Guid});
					}
				}
			}
			
			return base.ConvertTo (context, culture, value, destinationType);
		}

		public override StandardValuesCollection GetStandardValues (ITypeDescriptorContext context)
		{
			ImageFormat[] list = new ImageFormat [10];
			list [0] = ImageFormat.MemoryBmp;
			list [1] = ImageFormat.Bmp;
			list [2] = ImageFormat.Emf;
			list [3] = ImageFormat.Wmf;
			list [4] = ImageFormat.Gif;
			list [5] = ImageFormat.Jpeg;
			list [6] = ImageFormat.Png;
			list [7] = ImageFormat.Tiff;
			list [8] = ImageFormat.Exif;
			list [9] = ImageFormat.Icon;
			return new TypeConverter.StandardValuesCollection (list);
		}

		public override bool GetStandardValuesSupported (ITypeDescriptorContext context)
		{
			return true;
		}
	}
}
