//
// System.Drawing.FontConverter.cs
//
// Authors:
//	Dennis Hayes (dennish@Raytek.com)
//	Gonzalo Paniagua Javier (gonzalo@ximian.com)
//	Ravindra (rkumar@novell.com)
//
// Copyright (C) 2002,2003 Ximian, Inc.  http://www.ximian.com
//
// Copyright (C) 2004-2006 Novell, Inc (http://www.novell.com)
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
using System.Text;
using System.Collections;
using System.ComponentModel;
using System.Globalization;
using System.Drawing.Text;
using System.ComponentModel.Design.Serialization;
using System.Reflection;

namespace System.Drawing
{
	public class FontConverter : TypeConverter
	{
		public FontConverter ()
		{
		}
		~FontConverter ()
		{
			// required to match API definition
		}	
		public override bool CanConvertFrom (ITypeDescriptorContext context, Type sourceType)
		{
			if (sourceType == typeof (string))
				return true;

			return base.CanConvertFrom (context, sourceType);
		}

		public override bool CanConvertTo (ITypeDescriptorContext context, Type destinationType)
		{
			if (destinationType == typeof (String))
				return true;

			if (destinationType == typeof (InstanceDescriptor))
				return true;

			return base.CanConvertTo (context, destinationType);
		}

		public override object ConvertTo (ITypeDescriptorContext context,
			CultureInfo culture,
			object value,
			Type destinationType)
		{
			if ((destinationType == typeof (string)) && (value is Font)) {
				Font font = (Font) value;
				StringBuilder sb = new StringBuilder ();
				sb.Append (font.Name).Append (culture.TextInfo.ListSeparator[0] + " ");
				sb.Append (font.Size);

				switch (font.Unit) {
					// MS throws ArgumentException, if unit is set 
					// to GraphicsUnit.Display
					// Don't know what to append for GraphicsUnit.Display
				case GraphicsUnit.Display:
					sb.Append ("display"); break;

				case GraphicsUnit.Document:
					sb.Append ("doc"); break;

				case GraphicsUnit.Point:
					sb.Append ("pt"); break;

				case GraphicsUnit.Inch:
					sb.Append ("in"); break;

				case GraphicsUnit.Millimeter:
					sb.Append ("mm"); break;

				case GraphicsUnit.Pixel:
					sb.Append ("px"); break;

				case GraphicsUnit.World:
					sb.Append ("world"); break;
				}

				if (font.Style != FontStyle.Regular)
					sb.Append (culture.TextInfo.ListSeparator[0] + " style=").Append (font.Style);

				return sb.ToString ();
			}

			if ((destinationType == typeof (InstanceDescriptor)) && (value is Font)) {
				Font font = (Font) value;
				ConstructorInfo met = typeof(Font).GetTypeInfo ().GetConstructor (new Type[] {typeof(string), typeof(float), typeof(FontStyle), typeof(GraphicsUnit)});
				object[] args = new object[4];
				args [0] = font.Name;
				args [1] = font.Size;
				args [2] = font.Style;
				args [3] = font.Unit;
				return new InstanceDescriptor (met, args);
			}
			
			return base.ConvertTo (context, culture, value, destinationType);
		}

		// System.Drawing.Common's parse: "name[, size[units][, style=style1[, style2...]]]" in the
		// culture's list separator, 8.25pt Regular when only a name is given.
		public override object ConvertFrom (ITypeDescriptorContext context, CultureInfo culture, object value)
		{
			if (!(value is string s))
				return base.ConvertFrom (context, culture, value);
			string text = s.Trim ();
			if (text.Length == 0)
				return null;
			culture ??= CultureInfo.CurrentCulture;
			char sep = culture.TextInfo.ListSeparator [0];
			string familyName = text;
			string style = null;
			float emSize = 8.25f;
			FontStyle fontStyle = FontStyle.Regular;
			GraphicsUnit unit = GraphicsUnit.Point;
			int comma = text.IndexOf (sep);
			if (comma < 0)
				return new Font (familyName, emSize, fontStyle, unit);
			familyName = text.Substring (0, comma);
			if (comma < text.Length - 1) {
				int styleAt = culture.CompareInfo.IndexOf (text, "style=", CompareOptions.IgnoreCase);
				string size;
				if (styleAt != -1) {
					style = text.Substring (styleAt);
					size = text.Substring (comma + 1, styleAt - comma - 1);
				} else {
					size = text.Substring (comma + 1);
				}
				(string sizeText, string unitText) = ParseSizeTokens (size, sep);
				if (sizeText != null) {
					try {
						emSize = (float) TypeDescriptor.GetConverter (typeof (float)).ConvertFromString (context, culture, sizeText);
					} catch {
						throw new ArgumentException (SR.Format (SR.TextParseFailedFormat, text, $"name{sep} size[units[{sep} style=style1[{sep} style2{sep} ...]]]"), "value");
					}
				}
				if (unitText != null)
					unit = ParseGraphicsUnits (unitText);
				if (style != null) {
					foreach (string part in style.Substring (6).Split (sep)) {
						fontStyle |= Enum.Parse<FontStyle> (part.Trim (), ignoreCase: true);
						const FontStyle all = FontStyle.Bold | FontStyle.Italic | FontStyle.Underline | FontStyle.Strikeout;
						if ((fontStyle | all) != all)
							throw new InvalidEnumArgumentException ("style", (int) fontStyle, typeof (FontStyle));
					}
				}
			}
			return new Font (familyName, emSize, fontStyle, unit);
		}

		static (string, string) ParseSizeTokens (string text, char separator)
		{
			string size = null, units = null;
			text = text.Trim ();
			int length = text.Length;
			if (length > 0) {
				int i = 0;
				while (i < length && !char.IsLetter (text [i]))
					i++;
				char [] trim = { separator, ' ' };
				if (i > 0)
					size = text.Substring (0, i).Trim (trim);
				if (i < length)
					units = text.Substring (i).TrimEnd (trim);
			}
			return (size, units);
		}

		static GraphicsUnit ParseGraphicsUnits (string units) => units switch {
			"display" => GraphicsUnit.Display,
			"doc" => GraphicsUnit.Document,
			"pt" => GraphicsUnit.Point,
			"in" => GraphicsUnit.Inch,
			"mm" => GraphicsUnit.Millimeter,
			"px" => GraphicsUnit.Pixel,
			"world" => GraphicsUnit.World,
			_ => throw new ArgumentException (SR.Format (SR.InvalidArgumentValueFontConverter, units), "units"),
		};

		public override object CreateInstance (ITypeDescriptorContext context, IDictionary propertyValues)
		{
			ArgumentNullException.ThrowIfNull (propertyValues);
			Object value;
			byte charSet = 1;
			float size = 8;
			String name = null;
			bool vertical = false;
			FontStyle style = FontStyle.Regular;
			FontFamily fontFamily = null;
			GraphicsUnit unit = GraphicsUnit.Point;

			if ((value = propertyValues ["GdiCharSet"]) != null)
				charSet = (byte) value;

			if ((value = propertyValues ["Size"]) != null)
				size = (float) value;

			if ((value = propertyValues ["Unit"]) != null)
				unit = (GraphicsUnit) value;

			if ((value = propertyValues ["Name"]) != null)
				name = (String) value;

			if ((value = propertyValues ["GdiVerticalFont"]) != null)
				vertical = (bool) value;

			if ((value = propertyValues ["Bold"]) != null) {
				bool bold = (bool) value;
				if (bold == true)
					style |= FontStyle.Bold;
			}

			if ((value = propertyValues ["Italic"]) != null) {
				bool italic = (bool) value;
				if (italic == true)
					style |= FontStyle.Italic;
			}

			if ((value = propertyValues ["Strikeout"]) != null) {
				bool strike = (bool) value;
				if (strike == true)
					style |= FontStyle.Strikeout;
			}

			if ((value = propertyValues ["Underline"]) != null) {
				bool underline = (bool) value;
				if (underline == true)
					style |= FontStyle.Underline;
			}

			/* ?? Should default font be culture dependent ?? */
			if (name == null)
				fontFamily = new FontFamily ("Tahoma");
			else {
				name = name.ToLower ();
				FontCollection collection = new InstalledFontCollection ();
				FontFamily [] installedFontList = collection.Families;
				foreach (FontFamily font in installedFontList) {
					if (name == font.Name.ToLower ()) {
						fontFamily = font;
						break;
					}
				}

				// font family not found in installed fonts
				if (fontFamily == null) {
					collection = new PrivateFontCollection ();
					FontFamily [] privateFontList = collection.Families;
					foreach (FontFamily font in privateFontList) {
						if (name == font.Name.ToLower ()) {
							fontFamily = font;
							break;
						}
					}
				}

				// font family not found in private fonts also
				if (fontFamily == null)
					fontFamily = FontFamily.GenericSansSerif;
			}

			return new Font (fontFamily, size, style, unit, charSet, vertical);
		}

		public override bool GetCreateInstanceSupported (ITypeDescriptorContext context)
		{
			return true;
		}

		public override PropertyDescriptorCollection GetProperties
			(ITypeDescriptorContext context,
			object value, Attribute [] attributes)
		{
			if (value is Font)
				return TypeDescriptor.GetProperties (value, attributes);

			return base.GetProperties (context, value, attributes);
		}

		public override bool GetPropertiesSupported (ITypeDescriptorContext context)
		{
			return true;
		}

		public sealed class FontNameConverter : TypeConverter
		, IDisposable		
		{
			FontFamily [] fonts;
			
			public FontNameConverter ()
			{
				fonts = FontFamily.Families;
			}	
			void IDisposable.Dispose ()
			{
			}
			public override bool CanConvertFrom (ITypeDescriptorContext context, Type sourceType)
			{
				if (sourceType == typeof (string))
					return true;

				return base.CanConvertFrom (context, sourceType);
			}

			public override object ConvertFrom (ITypeDescriptorContext context, CultureInfo culture, object value)
			{
				if (value is string)
					return value;
				return base.ConvertFrom (context, culture, value);
			}

			public override StandardValuesCollection GetStandardValues (ITypeDescriptorContext context)
			{
				string [] values = new string [fonts.Length];
				for (int i = fonts.Length; i > 0;){
					i--;
					values [i] = fonts [i].Name;
				}
				
				return new TypeConverter.StandardValuesCollection (values);
			}

			public override bool GetStandardValuesExclusive (ITypeDescriptorContext context)
			{
				// We allow other values other than those in the font list.
				return false;
			}

			public override bool GetStandardValuesSupported (ITypeDescriptorContext context)
			{
				// Yes, we support picking an element from the list. 
				return true;
			}
		}

		public class FontUnitConverter : EnumConverter
		{
			public FontUnitConverter () : base (typeof (GraphicsUnit)) {}
			
			public override StandardValuesCollection GetStandardValues (ITypeDescriptorContext context)
			{
				return base.GetStandardValues (context);
			}
				
		}
	}
}
