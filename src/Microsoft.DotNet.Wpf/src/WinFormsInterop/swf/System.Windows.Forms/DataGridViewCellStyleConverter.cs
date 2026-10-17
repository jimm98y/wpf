// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
//
// Ported from dotnet/winforms (DataGridViewCellStyleConverter): a cell style serializes as a call to its
// parameterless constructor (an InstanceDescriptor), which the designer's code generator writes as
// `new DataGridViewCellStyle()`. Mono threw from both members.

using System.ComponentModel;
using System.ComponentModel.Design.Serialization;
using System.Globalization;
using System.Reflection;

namespace System.Windows.Forms {

	public class DataGridViewCellStyleConverter : TypeConverter {

		public DataGridViewCellStyleConverter () {
		}

		public override bool CanConvertTo (ITypeDescriptorContext context, Type destinationType) {
			if (destinationType == typeof (InstanceDescriptor))
				return true;
			return base.CanConvertTo (context, destinationType);
		}

		public override object ConvertTo (ITypeDescriptorContext context, CultureInfo culture, object value, Type destinationType) {
			if (destinationType == null)
				throw new ArgumentNullException (nameof (destinationType));
			if (destinationType == typeof (InstanceDescriptor) && value is DataGridViewCellStyle) {
				ConstructorInfo ctor = value.GetType ().GetConstructor (Type.EmptyTypes);
				return new InstanceDescriptor (ctor, Array.Empty<object> (), false);
			}
			return base.ConvertTo (context, culture, value, destinationType);
		}
	}
}
