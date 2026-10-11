// The editors the DataGridView and its columns name: the columns dialog, the cell-style builder,
// the column-type picker and the two that pick a data source or a field within one.

using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.Design;
using System.Drawing;
using System.Drawing.Design;

namespace System.Windows.Forms.Design
{
	internal class DataGridViewColumnCollectionEditor : UITypeEditor
	{
		private DataGridViewColumnCollectionDialog dialog;

		public override object EditValue (ITypeDescriptorContext context, IServiceProvider provider, object value)
		{
			IWindowsFormsEditorService service = provider == null
				? null
				: provider.GetService (typeof (IWindowsFormsEditorService)) as IWindowsFormsEditorService;
			DataGridView grid = context == null ? null : context.Instance as DataGridView;
			if (service == null || grid == null)
				return value;

			IDesignerHost host = provider.GetService (typeof (IDesignerHost)) as IDesignerHost;
			if (dialog == null)
				dialog = new DataGridViewColumnCollectionDialog ();
			dialog.SetLiveDataGridView (grid, host);

			// The whole session is one undo step, as it is in Windows: a designer that adds three
			// columns and renames two should not have to undo five times.
			DesignerTransaction transaction = host == null
				? null
				: host.CreateTransaction ("Edit Columns");
			try {
				if (service.ShowDialog (dialog) == DialogResult.OK) {
					if (transaction != null)
						transaction.Commit ();
				} else if (transaction != null) {
					transaction.Cancel ();
				}
				transaction = null;
			} finally {
				if (transaction != null)
					transaction.Cancel ();
			}
			return value;
		}

		public override UITypeEditorEditStyle GetEditStyle (ITypeDescriptorContext context)
		{
			return UITypeEditorEditStyle.Modal;
		}
	}

	internal class DataGridViewCellStyleEditor : UITypeEditor
	{
		private DataGridViewCellStyleBuilder builder;

		public override object EditValue (ITypeDescriptorContext context, IServiceProvider provider, object value)
		{
			IWindowsFormsEditorService service = provider == null
				? null
				: provider.GetService (typeof (IWindowsFormsEditorService)) as IWindowsFormsEditorService;
			if (service == null)
				return value;

			IComponent component = context == null ? null : context.Instance as IComponent;
			if (builder == null)
				builder = new DataGridViewCellStyleBuilder (provider, component);

			if (value is DataGridViewCellStyle)
				builder.CellStyle = (DataGridViewCellStyle) value;
			builder.Context = context;

			IUIService ui = provider.GetService (typeof (IUIService)) as IUIService;
			DialogResult result = ui != null ? ui.ShowDialog (builder) : builder.ShowDialog ();
			return result == DialogResult.OK ? builder.CellStyle : value;
		}

		public override UITypeEditorEditStyle GetEditStyle (ITypeDescriptorContext context)
		{
			return UITypeEditorEditStyle.Modal;
		}
	}

	/// <summary>Which kind of column a row of the columns dialog is. Only reachable from that
	/// dialog: it edits a property the column collection editor puts in front of it, not one the
	/// grid itself publishes.</summary>
	internal class DataGridViewColumnTypeEditor : UITypeEditor
	{
		private ColumnTypePicker picker;

		public override bool IsDropDownResizable {
			get { return true; }
		}

		public override object EditValue (ITypeDescriptorContext context, IServiceProvider provider, object value)
		{
			IWindowsFormsEditorService service = provider == null
				? null
				: provider.GetService (typeof (IWindowsFormsEditorService)) as IWindowsFormsEditorService;
			if (service == null || context == null || context.Instance == null)
				return value;

			IDesignerHost host = provider.GetService (typeof (IDesignerHost)) as IDesignerHost;
			ITypeDiscoveryService discovery = host == null
				? null
				: host.GetService (typeof (ITypeDiscoveryService)) as ITypeDiscoveryService;

			if (picker == null)
				picker = new ColumnTypePicker ();
			picker.Start (service, discovery, value as Type);
			service.DropDownControl (picker);
			return picker.SelectedType ?? value;
		}

		public override UITypeEditorEditStyle GetEditStyle (ITypeDescriptorContext context)
		{
			return UITypeEditorEditStyle.DropDown;
		}

		private sealed class ColumnTypePicker : ListBox
		{
			private IWindowsFormsEditorService service;

			internal ColumnTypePicker ()
			{
				BorderStyle = BorderStyle.None;
				Height = 100;
			}

			internal Type SelectedType { get; private set; }

			internal void Start (IWindowsFormsEditorService editorService, ITypeDiscoveryService discovery,
					     Type current)
			{
				service = editorService;
				SelectedType = null;
				Items.Clear ();

				foreach (Type type in ColumnTypes (discovery))
					Items.Add (new Entry (type));
				for (int i = 0; i < Items.Count; i++)
					if (((Entry) Items[i]).Type == current) {
						SelectedIndex = i;
						break;
					}
			}

			private static IEnumerable<Type> ColumnTypes (ITypeDiscoveryService discovery)
			{
				var types = new List<Type> {
					typeof (DataGridViewTextBoxColumn), typeof (DataGridViewCheckBoxColumn),
					typeof (DataGridViewComboBoxColumn), typeof (DataGridViewButtonColumn),
					typeof (DataGridViewImageColumn), typeof (DataGridViewLinkColumn),
				};
				if (discovery == null)
					return types;
				try {
					foreach (Type type in discovery.GetTypes (typeof (DataGridViewColumn), false))
						if (!type.IsAbstract && type.IsPublic && !types.Contains (type))
							types.Add (type);
				} catch (Exception) {
					// Nothing more to offer than the built-in kinds.
				}
				return types;
			}

			protected override void OnClick (EventArgs e)
			{
				base.OnClick (e);
				Entry entry = SelectedItem as Entry;
				if (entry != null)
					SelectedType = entry.Type;
				if (service != null)
					service.CloseDropDown ();
			}

			private sealed class Entry
			{
				internal readonly Type Type;

				internal Entry (Type type)
				{
					Type = type;
				}

				public override string ToString ()
				{
					return Type.Name;
				}
			}
		}
	}

	/// <summary>A column's DataPropertyName: the fields of whatever the grid is bound to.</summary>
	internal class DataGridViewColumnDataPropertyNameEditor : UITypeEditor
	{
		private DesignBindingPicker picker;

		public override bool IsDropDownResizable {
			get { return true; }
		}

		public override object EditValue (ITypeDescriptorContext context, IServiceProvider provider, object value)
		{
			if (provider == null || context == null || context.Instance == null)
				return value;

			DataGridViewColumn column = context.Instance as DataGridViewColumn;
			DataGridView grid = column == null ? null : column.DataGridView;
			if (grid == null)
				return value;

			object dataSource = grid.DataSource;
			string dataMember = grid.DataMember;
			string field = value as string ?? string.Empty;
			string selected = dataSource == null ? field : dataMember + "." + field;
			if (dataSource == null)
				dataMember = string.Empty;

			if (picker == null)
				picker = new DesignBindingPicker ();

			DesignBinding chosen = picker.Pick (context, provider,
							    false,      // whole sources
							    true,       // members
							    false,      // a single field will do
							    dataSource, dataMember,
							    new DesignBinding (dataSource, selected));
			return dataSource != null && chosen != null ? chosen.DataField : value;
		}

		public override UITypeEditorEditStyle GetEditStyle (ITypeDescriptorContext context)
		{
			return UITypeEditorEditStyle.DropDown;
		}
	}

	/// <summary>A control's DataSource: the components on the form that can act as one.</summary>
	internal class DataSourceListEditor : UITypeEditor
	{
		private DesignBindingPicker picker;

		public override bool IsDropDownResizable {
			get { return true; }
		}

		public override object EditValue (ITypeDescriptorContext context, IServiceProvider provider, object value)
		{
			if (provider == null || context == null || context.Instance == null)
				return value;

			if (picker == null)
				picker = new DesignBindingPicker ();

			DesignBinding chosen = picker.Pick (context, provider,
							    true,       // whole sources
							    false,      // no members
							    false,
							    null, string.Empty,
							    new DesignBinding (value, string.Empty));
			return chosen != null ? chosen.DataSource : value;
		}

		public override UITypeEditorEditStyle GetEditStyle (ITypeDescriptorContext context)
		{
			return UITypeEditorEditStyle.DropDown;
		}
	}
}
