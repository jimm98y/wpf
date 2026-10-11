// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// System.Drawing.Common's MarginsConverter: "left, right, top, bottom" in the culture's list separator.

using System.Collections;
using System.ComponentModel;
using System.ComponentModel.Design.Serialization;
using System.Globalization;
using System.Reflection;

namespace System.Drawing.Printing
{
	public class MarginsConverter : ExpandableObjectConverter
	{
		public override bool CanConvertFrom (ITypeDescriptorContext context, Type sourceType)
			=> sourceType == typeof (string) || base.CanConvertFrom (context, sourceType);

		public override bool CanConvertTo (ITypeDescriptorContext context, Type destinationType)
			=> destinationType == typeof (InstanceDescriptor) || base.CanConvertTo (context, destinationType);

		public override object ConvertFrom (ITypeDescriptorContext context, CultureInfo culture, object value)
		{
			if (!(value is string s))
				return base.ConvertFrom (context, culture, value);
			string text = s.Trim ();
			if (text.Length == 0)
				return null;
			culture ??= CultureInfo.CurrentCulture;
			char separator = culture.TextInfo.ListSeparator [0];
			string [] tokens = text.Split (separator);
			var values = new int [tokens.Length];
			TypeConverter intConverter = TypeDescriptor.GetConverter (typeof (int));
			for (int i = 0; i < values.Length; i++)
				values [i] = (int) intConverter.ConvertFromString (context, culture, tokens [i]);
			if (values.Length != 4)
				throw new ArgumentException (SR.Format (SR.TextParseFailedFormat, text, "left, right, top, bottom"));
			return new Margins (values [0], values [1], values [2], values [3]);
		}

		public override object ConvertTo (ITypeDescriptorContext context, CultureInfo culture, object value, Type destinationType)
		{
			ArgumentNullException.ThrowIfNull (destinationType);
			if (value is Margins margins) {
				if (destinationType == typeof (string)) {
					culture ??= CultureInfo.CurrentCulture;
					string separator = culture.TextInfo.ListSeparator + " ";
					TypeConverter intConverter = TypeDescriptor.GetConverter (typeof (int));
					return string.Join (separator, new [] {
						intConverter.ConvertToString (context, culture, margins.Left),
						intConverter.ConvertToString (context, culture, margins.Right),
						intConverter.ConvertToString (context, culture, margins.Top),
						intConverter.ConvertToString (context, culture, margins.Bottom),
					});
				}
				if (destinationType == typeof (InstanceDescriptor)) {
					ConstructorInfo ctor = typeof (Margins).GetConstructor (new [] { typeof (int), typeof (int), typeof (int), typeof (int) });
					if (ctor != null)
						return new InstanceDescriptor (ctor, new object [] { margins.Left, margins.Right, margins.Top, margins.Bottom });
				}
			}
			return base.ConvertTo (context, culture, value, destinationType);
		}

		public override bool GetCreateInstanceSupported (ITypeDescriptorContext context) => true;

		public override object CreateInstance (ITypeDescriptorContext context, IDictionary propertyValues)
		{
			ArgumentNullException.ThrowIfNull (propertyValues);
			object left = propertyValues ["Left"], right = propertyValues ["Right"], top = propertyValues ["Top"], bottom = propertyValues ["Bottom"];
			if (left is int l && right is int r && bottom is int b && top is int t)
				return new Margins (l, r, t, b);
			throw new ArgumentException (SR.PropertyValueInvalidEntry);
		}
	}
}
