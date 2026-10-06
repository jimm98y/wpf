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
//	Jonathan Chambers (jonathan.chambers@ansys.com)
//

// COMPLETE

using System;
using System.Collections;
using System.Windows.Forms.PropertyGridInternal;

namespace System.Windows.Forms
{
	public class GridItemCollection : IEnumerable, ICollection
	{
		#region	Local Variables
		private System.Collections.SortedList list;
		// In the order given, where the grid is not sorting alphabetically: .NET's collection is a
		// plain list and the GRID sorts it, by display name only when PropertySort says Alphabetical,
		// so a Categorized grid shows each category's properties as the type declares them. The
		// sorted list put every grid in alphabetical order whatever it was asked for.
		private System.Collections.Generic.List<GridItem> ordered;
		#endregion	// Local Variables

		#region Public Static Fields
		public static GridItemCollection Empty = new GridItemCollection();
		#endregion	// Public Static Fields

		#region	Constructors
		internal GridItemCollection()
		{
			list = new SortedList();
		}
		#endregion	// Constructors

		#region Internal Properties and Methods
		/// <summary>Keep the items in the order they are added (set while the collection is empty).</summary>
		internal bool KeepOrder {
			get { return ordered != null; }
			set {
				if (value == KeepOrder)
					return;
				var items = new System.Collections.Generic.List<GridItem> ();
				foreach (GridItem item in this)
					items.Add (item);
				list.Clear ();
				ordered = value ? new System.Collections.Generic.List<GridItem> () : null;
				foreach (GridItem item in items)
					Add (item);
			}
		}

		internal void Add (GridItem grid_item)
		{
			if (ordered != null) {
				ordered.Add (grid_item);
				return;
			}
			string key = grid_item.Label;
			while (list.ContainsKey (key))
				key += "_";
			list.Add (key, grid_item);
		}

		internal void AddRange (GridItemCollection items)
		{
			foreach (GridItem item in items)
				Add (item);
		}

		internal int IndexOf (GridItem grid_item)
		{
			if (ordered != null)
				return ordered.IndexOf (grid_item);
			return list.IndexOfValue (grid_item);
		}
		#endregion	// Internal Properties and Methods

		#region	Public Instance Properties
		public int Count {
			get {
				return ordered != null ? ordered.Count : list.Count;
			}
		}

		public GridItem this [int index] {
			get {
				if (index>=Count) {
					throw new ArgumentOutOfRangeException("index");
				}
				if (ordered != null)
					return ordered [index];
				return (GridItem)list.GetByIndex(index);
			}
		}

		public GridItem this [string label] {
			get {
				if (ordered != null)
					return ordered.Find (i => i.Label == label);
				return (GridItem)list[label];
			}
		}
		#endregion	// Public Instance Properties

		#region IEnumerable Members
		public IEnumerator GetEnumerator()
		{
			return new GridItemEnumerator (this);
		}
		#endregion

		#region Enumerator Class
		internal class GridItemEnumerator : IEnumerator{
			int nIndex;
			GridItemCollection collection;

			public GridItemEnumerator(GridItemCollection coll)
			{
				collection = coll;
				nIndex = -1;
			}

			public bool MoveNext ()
			{
				nIndex++;
				return (nIndex < collection.Count);
			}

			public void Reset ()
			{
				nIndex = -1;
			}

			object System.Collections.IEnumerator.Current {
				get {
					return collection [nIndex];
				}
			}
		}
		#endregion

		#region ICollection Members

		bool ICollection.IsSynchronized {
			get {
				return list.IsSynchronized;
			}
		}

		void ICollection.CopyTo(Array dest, int index)
		{
			if (ordered != null) {
				foreach (GridItem item in ordered)
					dest.SetValue (item, index++);
				return;
			}
			list.CopyTo (dest, index);
		}

		object ICollection.SyncRoot {
			get {
				return list.SyncRoot;
			}
		}

		#endregion

		internal void Clear ()
		{
			list.Clear ();
			ordered?.Clear ();
		}
	}
}
