// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
//
// Ported from dotnet/winforms (System.ComponentModel.Design.InheritanceService). Every member of the Mono stub
// threw NotImplementedException.
//
// The component fields a designed type inherits from its base classes are found by reflection and added to the
// container under their field (or [AccessedThroughProperty]) names: a public or protected one as Inherited, a
// private or internal one only recorded, as InheritedReadOnly. Components the inherited ones add while being
// sited are inherited too (read-only, or at their owner's level in its nested container).

using System.Collections.Generic;
using System.ComponentModel.Design.Serialization;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace System.ComponentModel.Design
{
	public class InheritanceService : IInheritanceService, IDisposable
	{
		private Dictionary<IComponent, InheritanceAttribute> _inheritedComponents = new Dictionary<IComponent, InheritanceAttribute> ();
		// The inherited component being added, and its attribute, while it is sited.
		private IComponent _addingComponent;
		private InheritanceAttribute _addingAttribute;

		public InheritanceService ()
		{
		}

		public void Dispose ()
		{
			Dispose (true);
		}

		protected virtual void Dispose (bool disposing)
		{
			if (disposing && _inheritedComponents != null) {
				_inheritedComponents.Clear ();
				_inheritedComponents = null;
			}
		}

		public void AddInheritedComponents (IComponent component, IContainer container)
		{
			AddInheritedComponents (component.GetType (), component, container);
		}

		protected virtual void AddInheritedComponents (Type type, IComponent component, IContainer container)
		{
			// Only down to the component level.
			if (type == null || !typeof (IComponent).IsAssignableFrom (type))
				return;

			ISite site = component.Site;
			IComponentChangeService cs = null;
			INameCreationService ncs = null;
			if (site != null) {
				ncs = site.GetService (typeof (INameCreationService)) as INameCreationService;
				cs = site.GetService (typeof (IComponentChangeService)) as IComponentChangeService;
				if (cs != null)
					cs.ComponentAdding += OnComponentAdding;
			}

			try {
				while (type != typeof (object)) {
					Type reflect = TypeDescriptor.GetReflectionType (type);
					FieldInfo [] fields = reflect.GetFields (BindingFlags.Instance | BindingFlags.DeclaredOnly | BindingFlags.Public | BindingFlags.NonPublic);
					foreach (FieldInfo field in fields) {
						string name = field.Name;
						if (!typeof (IComponent).IsAssignableFrom (field.FieldType))
							continue;
						// A field never assigned by the base class is of no use.
						object value = field.GetValue (component);
						if (value == null)
							continue;

						// [AccessedThroughProperty]: the property's name and visibility stand for the field's.
						MemberInfo member = field;
						object [] fieldAttrs = field.GetCustomAttributes (typeof (AccessedThroughPropertyAttribute), false);
						if (fieldAttrs != null && fieldAttrs.Length > 0) {
							var propAttr = (AccessedThroughPropertyAttribute) fieldAttrs [0];
							PropertyInfo fieldProp = reflect.GetProperty (propAttr.PropertyName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
							if (fieldProp != null && fieldProp.PropertyType == field.FieldType) {
								if (!fieldProp.CanRead)
									continue;
								member = fieldProp.GetGetMethod (true);
								name = propAttr.PropertyName;
							}
						}

						// The user hook; by default private and internal members are ignored.
						bool ignoreMember = IgnoreInheritedMember (member, component);
						bool privateInherited;
						if (ignoreMember)
							privateInherited = true;   // recorded only, in case something else adds it to the container
						else if (member is FieldInfo fi)
							privateInherited = fi.IsPrivate | fi.IsAssembly;
						else if (member is MethodInfo mi)
							privateInherited = mi.IsPrivate | mi.IsAssembly;
						else
							privateInherited = false;
						InheritanceAttribute attr = privateInherited ? InheritanceAttribute.InheritedReadOnly : InheritanceAttribute.Inherited;

						if (value is IComponent compValue) {
							bool notPresent = !_inheritedComponents.ContainsKey (compValue);
							_inheritedComponents [compValue] = attr;
							if (!ignoreMember && notPresent) {
								try {
									_addingComponent = compValue;
									_addingAttribute = attr;
									if (ncs == null || ncs.IsValidName (name)) {
										try {
											container.Add (compValue, name);
										} catch {
											// A rogue base component that refuses to be added is skipped, as .NET skips it.
										}
									}
								} finally {
									_addingComponent = null;
									_addingAttribute = null;
								}
							}
						}
					}
					type = type.BaseType;
				}
			} finally {
				if (cs != null)
					cs.ComponentAdding -= OnComponentAdding;
			}
		}

		/// <summary>Whether an inherited member stays out of the designer: by default every private or internal one.</summary>
		protected virtual bool IgnoreInheritedMember (MemberInfo member, IComponent component)
		{
			if (member is FieldInfo field)
				return field.IsPrivate || field.IsAssembly;
			if (member is MethodInfo method)
				return method.IsPrivate || method.IsAssembly;
			return true;
		}

		public InheritanceAttribute GetInheritanceAttribute (IComponent component)
			=> _inheritedComponents != null && _inheritedComponents.TryGetValue (component, out InheritanceAttribute attr)
				? attr
				: InheritanceAttribute.Default;

		private void OnComponentAdding (object sender, ComponentEventArgs ce)
		{
			if (_addingComponent != null && _addingComponent != ce.Component) {
				_inheritedComponents [ce.Component] = InheritanceAttribute.InheritedReadOnly;
				// Added to a nested container of the inherited component: its level.
				if (sender is INestedContainer nested && nested.Owner == _addingComponent)
					_inheritedComponents [ce.Component] = _addingAttribute;
			}
		}
	}
}
