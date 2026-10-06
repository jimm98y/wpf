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
// Copyright (c) 2005 Novell, Inc. (http://www.novell.com)
//
// Author:
//	Pedro Martínez Juliá <pedromj@gmail.com>
//


using System.ComponentModel;
using System.Collections;
using System.Collections.Generic;

namespace System.Windows.Forms
{
	[ListBindable (false)]
	public class DataGridViewColumnCollection : BaseCollection, IList, ICollection, IEnumerable
	{
		private DataGridView dataGridView;
		private List<DataGridViewColumn> display_index_sorted;
		
		public DataGridViewColumnCollection (DataGridView dataGridView)
		{
			this.dataGridView = dataGridView;
			RegenerateSortedList ();
		}

		bool IList.IsFixedSize {
			get { return base.List.IsFixedSize; }
		}

		object IList.this [int index] {
			get { return this [index]; }
			set { throw new NotSupportedException(); }
		}

		public DataGridViewColumn this [int index] {
			get { return (DataGridViewColumn) base.List[index]; }
		}

		public DataGridViewColumn this [string columnName] {
			get {
				foreach (DataGridViewColumn col in base.List) {
					if (col.Name == columnName) {
						return col;
					}
				}
				return null;
			}
		}

		public event CollectionChangeEventHandler CollectionChanged;

		int IList.Add (object value)
		{
			return Add(value as DataGridViewColumn);
		}

		public virtual int Add (DataGridViewColumn dataGridViewColumn)
		{
			int result = base.List.Add(dataGridViewColumn);
			if (dataGridViewColumn.DisplayIndex == -1)
				dataGridViewColumn.DisplayIndexInternal = result;
			dataGridViewColumn.SetIndex(result);
			dataGridViewColumn.SetDataGridView(dataGridView);
			OnCollectionChanged(new CollectionChangeEventArgs(CollectionChangeAction.Add, dataGridViewColumn));
			return result;
		}

		[DesignerSerializationVisibility (DesignerSerializationVisibility.Hidden)]
		public virtual int Add (string columnName, string headerText)
		{
			DataGridViewColumn col = new DataGridViewTextBoxColumn ();
			col.Name = columnName;
			col.HeaderText = headerText;
			return Add (col);
		}

		public virtual void AddRange (params DataGridViewColumn[] dataGridViewColumns)
		{
			foreach (DataGridViewColumn col in dataGridViewColumns)
				Add (col);
		}

		public virtual void Clear ()
		{
			base.List.Clear ();
			
			// When we clear the column collection, all rows get deleted
			dataGridView.Rows.Clear ();
			dataGridView.RemoveEditingRow ();
			
			RegenerateSortedList ();

			OnCollectionChanged (new CollectionChangeEventArgs (CollectionChangeAction.Refresh, null));
		}

		bool IList.Contains (object value)
		{
			return Contains (value as DataGridViewColumn);
		}

		public virtual bool Contains (DataGridViewColumn dataGridViewColumn)
		{
			return base.List.Contains (dataGridViewColumn);
		}

		public virtual bool Contains (string columnName)
		{
			foreach (DataGridViewColumn col in base.List)
				if (col.Name == columnName)
					return true;
			return false;
		}

		public void CopyTo (DataGridViewColumn [] array, int index)
		{
			base.List.CopyTo (array, index);
		}

		// The filtered queries, as .NET answers them: a column matches when it has every state in the
		// include filter and none in the exclude one, and "first", "next" and so on follow the
		// columns' DISPLAY order. These were stubs answering 0 and null, which a caller asking for
		// the first visible column read as "there is none".

		private static DataGridViewElementStates StateOf (DataGridViewColumn column)
		{
			// The properties are the truth for these five; the stored state can carry a stale bit
			// (a column hidden after it was added still reported Visible).
			const DataGridViewElementStates fromProperties = DataGridViewElementStates.Visible | DataGridViewElementStates.Frozen
				| DataGridViewElementStates.ReadOnly | DataGridViewElementStates.Displayed | DataGridViewElementStates.Selected;
			DataGridViewElementStates state = column.State & ~fromProperties;
			if (column.Visible) state |= DataGridViewElementStates.Visible;
			if (column.Frozen) state |= DataGridViewElementStates.Frozen;
			if (column.ReadOnly) state |= DataGridViewElementStates.ReadOnly;
			if (column.Displayed) state |= DataGridViewElementStates.Displayed;
			if (column.Selected) state |= DataGridViewElementStates.Selected;
			return state;
		}

		private static bool Matches (DataGridViewColumn column, DataGridViewElementStates include, DataGridViewElementStates exclude)
		{
			DataGridViewElementStates state = StateOf (column);
			return (state & include) == include && (state & exclude) == 0;
		}

		public int GetColumnCount (DataGridViewElementStates includeFilter)
		{
			int count = 0;
			foreach (DataGridViewColumn column in base.List)
				if (Matches (column, includeFilter, DataGridViewElementStates.None))
					count++;
			return count;
		}

		public int GetColumnsWidth (DataGridViewElementStates includeFilter)
		{
			int width = 0;
			foreach (DataGridViewColumn column in base.List)
				if (Matches (column, includeFilter, DataGridViewElementStates.None))
					width += column.Width;
			return width;
		}

		public DataGridViewColumn GetFirstColumn (DataGridViewElementStates includeFilter)
		{
			return GetFirstColumn (includeFilter, DataGridViewElementStates.None);
		}

		public DataGridViewColumn GetFirstColumn (DataGridViewElementStates includeFilter, DataGridViewElementStates excludeFilter)
		{
			foreach (DataGridViewColumn column in ColumnDisplayIndexSortedArrayList)
				if (Matches (column, includeFilter, excludeFilter))
					return column;
			return null;
		}

		public DataGridViewColumn GetLastColumn (DataGridViewElementStates includeFilter, DataGridViewElementStates excludeFilter)
		{
			List<DataGridViewColumn> sorted = ColumnDisplayIndexSortedArrayList;
			for (int i = sorted.Count - 1; i >= 0; i--)
				if (Matches (sorted [i], includeFilter, excludeFilter))
					return sorted [i];
			return null;
		}

		public DataGridViewColumn GetNextColumn (DataGridViewColumn dataGridViewColumnStart, DataGridViewElementStates includeFilter, DataGridViewElementStates excludeFilter)
		{
			if (dataGridViewColumnStart == null)
				throw new ArgumentNullException ("dataGridViewColumnStart");
			List<DataGridViewColumn> sorted = ColumnDisplayIndexSortedArrayList;
			for (int i = sorted.IndexOf (dataGridViewColumnStart) + 1; i > 0 && i < sorted.Count; i++)
				if (Matches (sorted [i], includeFilter, excludeFilter))
					return sorted [i];
			return null;
		}

		public DataGridViewColumn GetPreviousColumn (DataGridViewColumn dataGridViewColumnStart, DataGridViewElementStates includeFilter, DataGridViewElementStates excludeFilter)
		{
			if (dataGridViewColumnStart == null)
				throw new ArgumentNullException ("dataGridViewColumnStart");
			List<DataGridViewColumn> sorted = ColumnDisplayIndexSortedArrayList;
			for (int i = sorted.IndexOf (dataGridViewColumnStart) - 1; i >= 0; i--)
				if (Matches (sorted [i], includeFilter, excludeFilter))
					return sorted [i];
			return null;
		}

		int IList.IndexOf (object value)
		{
			return IndexOf (value as DataGridViewColumn);
		}

		public int IndexOf (DataGridViewColumn dataGridViewColumn)
		{
			return base.List.IndexOf (dataGridViewColumn);
		}

		void IList.Insert (int index, object value)
		{
			Insert (index, value as DataGridViewColumn);
		}

		public virtual void Insert (int columnIndex, DataGridViewColumn dataGridViewColumn)
		{
			base.List.Insert (columnIndex, dataGridViewColumn);
			if (dataGridViewColumn.DisplayIndex == -1)
				dataGridViewColumn.DisplayIndexInternal = columnIndex;
			dataGridViewColumn.SetIndex (columnIndex);
			dataGridViewColumn.SetDataGridView (dataGridView);
			OnCollectionChanged (new CollectionChangeEventArgs (CollectionChangeAction.Add, dataGridViewColumn));
		}

		void IList.Remove (object value)
		{
			Remove (value as DataGridViewColumn);
		}

		public virtual void Remove (DataGridViewColumn dataGridViewColumn)
		{
			DataGridView.OnColumnPreRemovedInternal (new DataGridViewColumnEventArgs (dataGridViewColumn));
			base.List.Remove (dataGridViewColumn);
			OnCollectionChanged (new CollectionChangeEventArgs (CollectionChangeAction.Remove, dataGridViewColumn));
		}

		public virtual void Remove (string columnName) {
			foreach (DataGridViewColumn col in base.List) {
				if (col.Name == columnName) {
					Remove(col);
					return;
				}
			}
		}

		public virtual void RemoveAt (int index)
		{
			DataGridViewColumn col = this [index];
			Remove (col);
		}

		protected DataGridView DataGridView {
			get { return dataGridView; }
		}

		protected virtual void OnCollectionChanged (CollectionChangeEventArgs e)
		{
			RegenerateIndexes ();
			RegenerateSortedList ();

			if (CollectionChanged != null)
				CollectionChanged(this, e);
		}

		protected override ArrayList List {
			get { return base.List; }
		}

		internal List<DataGridViewColumn> ColumnDisplayIndexSortedArrayList {
			get { return display_index_sorted; }
		}

		private void RegenerateIndexes ()
		{
			for (int i = 0; i < Count; i++)
				this[i].SetIndex (i);
		}
		
		internal void RegenerateSortedList ()
		{
			DataGridViewColumn[] array = (DataGridViewColumn[])base.List.ToArray (typeof (DataGridViewColumn));
			List<DataGridViewColumn> result = new List<DataGridViewColumn> (array);

			result.Sort (new ColumnDisplayIndexComparator ());
			for (int i = 0; i < result.Count; i++)
				result[i].DisplayIndexInternal = i;
			
			display_index_sorted = result;
		}
		
		internal void ClearAutoGeneratedColumns ()
		{
			for (int i = list.Count - 1; i >= 0; i--)
				if ((list[i] as DataGridViewColumn).AutoGenerated)
					RemoveAt (i);
		}
		
		private class ColumnDisplayIndexComparator : IComparer<DataGridViewColumn>
		{
			public int Compare (DataGridViewColumn o1, DataGridViewColumn o2)
			{
				return o1.DisplayIndex.CompareTo (o2.DisplayIndex);
			}
		}
	}
}

