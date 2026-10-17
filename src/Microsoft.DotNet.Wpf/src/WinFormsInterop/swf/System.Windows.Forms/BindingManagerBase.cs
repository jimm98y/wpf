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
// Copyright (c) 2004-2005 Novell, Inc.
//
// Authors:
//	Peter Bartok	pbartok@novell.com
//	Jackson Harper	jackson@ximian.com
//


// NOT COMPLETE

using System.ComponentModel;
using System.Collections;

namespace System.Windows.Forms
{
	public abstract class BindingManagerBase
	{
		private BindingsCollection	bindings;
		internal bool transfering_data; /* true if we're pushing or pulling data */

		#region Public Constructors
		public BindingManagerBase()
		{
		}
		#endregion	// Public Constructors

		#region Protected Instance Fields
		protected EventHandler onCurrentChangedHandler;
		protected EventHandler onPositionChangedHandler;
		internal EventHandler onCurrentItemChangedHandler;
		#endregion	// Protected Instance Fields

		#region Public Instance Properties
		public BindingsCollection Bindings {
			get {
				if (bindings == null) {
					bindings = new BindingsCollection ();
				}
				return bindings;
			}
		}

		public abstract int Count {
			get;
		}

		public abstract object Current {
			get;
		}

		public bool IsBindingSuspended {
			get {
				return IsSuspended;
			}
		}

		public abstract int Position {
			get; set;
		}
		#endregion	// Public Instance Properties

		#region Public Instance Methods
		public abstract void AddNew();

		public abstract void CancelCurrentEdit();

		public abstract void EndCurrentEdit();

		public virtual PropertyDescriptorCollection GetItemProperties()
		{
			return GetItemPropertiesInternal ();
		}
		
		/// <summary>The type the manager binds to (.NET's BindType): a list's item type, a single object's own.</summary>
		internal virtual Type BindTypeInternal => null;

		/// <summary>The manager's data source, for the item properties of an untyped list (.NET's DataSource).</summary>
		internal virtual object DataSourceInternal => null;

		internal virtual PropertyDescriptorCollection GetItemPropertiesInternal ()
		{
			Type bindType = BindTypeInternal;
			return bindType == null ? new PropertyDescriptorCollection (null) : TypeDescriptor.GetProperties (bindType);
		}

		public abstract void RemoveAt(int index);

		public abstract void ResumeBinding();

		public abstract void SuspendBinding();
		#endregion	// Public Instance Methods

		internal virtual bool IsSuspended {
			get {
				return false;
			}
		}

		#region Protected Instance Methods
		// Ported from dotnet/winforms BindingManagerBase (the Mono stubs threw): the item properties at the end of a
		// chain of list accessors -- an ITypedList answers them itself; otherwise each accessor is followed through
		// the bound type's properties, an IList by its strongly typed indexer or else its first item.
		protected internal virtual PropertyDescriptorCollection GetItemProperties (ArrayList dataSources, ArrayList listAccessors)
		{
			IList list = (this as CurrencyManager)?.List;
			if (list is ITypedList typedList) {
				PropertyDescriptor [] properties = new PropertyDescriptor [listAccessors.Count];
				listAccessors.CopyTo (properties, 0);
				return typedList.GetItemProperties (properties);
			}
			return GetItemProperties (BindTypeInternal, 0, dataSources, listAccessors);
		}

		protected virtual PropertyDescriptorCollection GetItemProperties (Type listType, int offset, ArrayList dataSources, ArrayList listAccessors)
		{
			if (listType == null || listAccessors.Count < offset)
				return null;

			if (listAccessors.Count == offset) {
				if (!typeof (IList).IsAssignableFrom (listType))
					return TypeDescriptor.GetProperties (listType);
				foreach (System.Reflection.PropertyInfo property in listType.GetProperties ()) {
					if (property.Name == "Item" && property.PropertyType != typeof (object))
						return TypeDescriptor.GetProperties (property.PropertyType, new Attribute [] { new BrowsableAttribute (true) });
				}
				// The properties of the type of the list's first element.
				if (offset > 0 && dataSources [offset - 1] is IList first && first.Count > 0)
					return TypeDescriptor.GetProperties (first [0]);
				return null;
			}

			if (typeof (IList).IsAssignableFrom (listType)) {
				PropertyDescriptorCollection itemProps = null;
				foreach (System.Reflection.PropertyInfo property in listType.GetProperties ()) {
					if (property.Name == "Item" && property.PropertyType != typeof (object))
						itemProps = TypeDescriptor.GetProperties (property.PropertyType, new Attribute [] { new BrowsableAttribute (true) });
				}
				if (itemProps == null) {
					// No strongly typed indexer: the first element's properties. At offset 0 the list is the
					// manager's own data source.
					IList items = offset == 0 ? DataSourceInternal as IList : dataSources [offset - 1] as IList;
					if (items != null && items.Count > 0)
						itemProps = TypeDescriptor.GetProperties (items [0]);
				}
				if (itemProps != null) {
					for (int j = 0; j < itemProps.Count; j++) {
						if (itemProps [j].Equals (listAccessors [offset]))
							return GetItemProperties (itemProps [j].PropertyType, offset + 1, dataSources, listAccessors);
					}
				}
			} else {
				foreach (System.Reflection.PropertyInfo property in listType.GetProperties ()) {
					if (property.Name.Equals (((PropertyDescriptor) listAccessors [offset]).Name))
						return GetItemProperties (property.PropertyType, offset + 1, dataSources, listAccessors);
				}
			}
			return null;
		}

		protected internal abstract string GetListName (ArrayList listAccessors);

		protected internal abstract void OnCurrentChanged (EventArgs e);

		protected void PullData()
		{
			try {
				if (!transfering_data) {
					transfering_data = true;
					UpdateIsBinding ();
				}
				foreach (Binding binding in Bindings) {
					binding.PullData ();
				}
			} finally {
				transfering_data = false;
			}
		}

		protected void PushData()
		{
			try {
				if (!transfering_data) {
					transfering_data = true;
					UpdateIsBinding ();
				}
				foreach (Binding binding in Bindings) {
					binding.PushData ();
				}
			} finally {
				transfering_data = false;
			}
		}


		protected void OnBindingComplete (BindingCompleteEventArgs args)
		{
			if (BindingComplete != null)
				BindingComplete (this, args);
		}

		protected abstract void OnCurrentItemChanged (EventArgs e);

		protected void OnDataError (Exception e)
		{
			if (DataError != null)
				DataError (this, new BindingManagerDataErrorEventArgs (e));
		}

		protected abstract void UpdateIsBinding();
		#endregion	// Protected Instance Methods

		internal void AddBinding (Binding binding)
		{
			if (Bindings.Contains (binding))
				return;
			Bindings.Add (binding);
		}

		#region Events
		public event EventHandler CurrentChanged {
			add { onCurrentChangedHandler += value; }
			remove { onCurrentChangedHandler -= value; }
		}

		public event EventHandler PositionChanged {
			add { onPositionChangedHandler += value; }
			remove { onPositionChangedHandler -= value; }
		}

		public event EventHandler CurrentItemChanged {
			add { onCurrentItemChangedHandler += value; }
			remove { onCurrentItemChangedHandler -= value; }
		}

		public event BindingCompleteEventHandler BindingComplete;
		public event BindingManagerDataErrorEventHandler DataError;
		#endregion	// Events
	}
}
