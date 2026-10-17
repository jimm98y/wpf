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
// Copyright (c) 2006 Novell, Inc. (http://www.novell.com)
//
// Authors:
//	Peter Dennis Bartok (pbartok@novell.com)
// 	Ivan N. Zlatev (contact i-nz.net)
//
//

// NOT COMPLETE
using System.ComponentModel;
using System.Windows.Forms;

namespace System.Windows.Forms.Design 
{
	internal class ControlBindingsConverter : TypeConverter 
	{
		public ControlBindingsConverter() 
		{
		}

		// TODO: Should create some sort of a special PropertyDescriptor and handle the data binding
		//
		[MonoTODO]
		public override PropertyDescriptorCollection GetProperties (ITypeDescriptorContext context, 
									    object value, Attribute[] attributes) 
		{
			PropertyDescriptorCollection properties = new PropertyDescriptorCollection (new PropertyDescriptor[0]);
			ControlBindingsCollection collection = value as ControlBindingsCollection;
			object bindableComponent = collection?.BindableComponent;
			if (collection != null && bindableComponent != null) {
				foreach (PropertyDescriptor property in
					 TypeDescriptor.GetProperties (bindableComponent, attributes)) {
					if (property.Attributes[typeof (BindableAttribute)] is BindableAttribute bindable && bindable.Bindable)
						properties.Add (new DataBindingPropertyDescriptor (property, attributes, true));
				}
			}
			return properties;
		}

		public override bool GetPropertiesSupported (ITypeDescriptorContext context) 
		{
			return true;
		}

		public override bool CanConvertTo (ITypeDescriptorContext context, Type destinationType)
		{
			if (destinationType == typeof (string))
				return true;
			return base.CanConvertTo (context, destinationType);
		}

		public override object ConvertTo (ITypeDescriptorContext context, System.Globalization.CultureInfo culture, 
						  object value, Type destinationType)
		{
			if (destinationType == typeof (string))
			    return String.Empty;
			return base.ConvertTo (context, culture, value, destinationType);
		}


		// One bindable property of the control in the property grid's (DataBindings) node: its value is the Binding
		// the control has for it, setting one replaces it, resetting removes it -- as the .NET Framework's designer
		// shows them (.NET itself no longer has this converter). Mono returned nothing, ignored a set, and threw
		// from ResetValue.
		private class DataBindingPropertyDescriptor : PropertyDescriptor
		{
			bool _readOnly;

			public DataBindingPropertyDescriptor (PropertyDescriptor property, Attribute [] attrs, bool readOnly)
				: base (property.Name, attrs)
			{
				_readOnly = readOnly;
			}

			private Binding BindingOf (object component)
				=> (component as ControlBindingsCollection)?[Name];

			public override object GetValue (object component)
			{
				return BindingOf (component);
			}

			public override void SetValue (object component, object value)
			{
				if (!(component is ControlBindingsCollection collection))
					return;
				Binding existing = collection [Name];
				if (existing == value)
					return;
				if (existing != null)
					collection.Remove (existing);
				if (value is Binding binding)
					collection.Add (binding);
				OnValueChanged (component, EventArgs.Empty);
			}

			public override void ResetValue (object component)
			{
				SetValue (component, null);
			}

			public override bool CanResetValue (object component)
			{
				return BindingOf (component) != null;
			}

			public override bool ShouldSerializeValue (object component)
			{
				return false;
			}

			public override Type PropertyType {
				get { return typeof (Binding); }
			}

			public override TypeConverter Converter {
				get { return TypeDescriptor.GetConverter (typeof (Binding)); }
			}

			public override Type ComponentType {
				get { return typeof (ControlBindingsCollection); }
			}

			public override bool IsReadOnly {
				get { return _readOnly; }
			}
		}
	}
}
