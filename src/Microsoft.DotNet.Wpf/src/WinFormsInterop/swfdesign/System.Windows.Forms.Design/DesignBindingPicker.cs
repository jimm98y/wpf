// Picking a data source, or a field within one.
//
// Windows opens a tree of every data source the project knows about -- the components on the form,
// the project's own data sources, and the members of each -- with a wizard for adding new ones. This
// is the same tree over what the designer's container actually holds, which is what a form's own
// bindings are made from; there is no project system to ask on the other platforms this has to run
// on, and a designer that has one can add to the container before opening the picker.

using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.Design;
using System.Drawing;

namespace System.Windows.Forms.Design
{
	/// <summary>A data source and, optionally, the member of it that was chosen.</summary>
	internal class DesignBinding
	{
		internal static readonly DesignBinding Null = new DesignBinding (null, null);

		internal DesignBinding (object dataSource, string dataMember)
		{
			DataSource = dataSource;
			DataMember = dataMember;
		}

		internal object DataSource { get; private set; }
		internal string DataMember { get; private set; }

		internal bool IsNull {
			get { return DataSource == null; }
		}

		/// <summary>The last leg of the member path -- the field itself, without the list it is
		/// part of.</summary>
		internal string DataField {
			get {
				if (string.IsNullOrEmpty (DataMember))
					return string.Empty;
				int dot = DataMember.LastIndexOf ('.');
				return dot < 0 ? DataMember : DataMember.Substring (dot + 1);
			}
		}
	}

	internal class DesignBindingPicker : UserControl
	{
		private readonly TreeView tree = new TreeView ();
		private IWindowsFormsEditorService service;
		private DesignBinding picked;

		internal DesignBindingPicker ()
		{
			tree.Dock = DockStyle.Fill;
			tree.HideSelection = false;
			tree.FullRowSelect = true;
			tree.ShowLines = true;
			tree.NodeMouseClick += OnNodeClicked;
			tree.KeyDown += OnKeyDown;

			BorderStyle = BorderStyle.FixedSingle;
			BackColor = SystemColors.Window;
			Size = new Size (260, 220);
			Controls.Add (tree);
		}

		/// <summary>Open the tree and return what was chosen, or null when nothing was.</summary>
		/// <param name="showDataSources">Whether whole sources may be picked.</param>
		/// <param name="showDataMembers">Whether the members of a source may be picked.</param>
		/// <param name="selectListMembers">Whether a member has to be a list, as a grid's does, or
		/// may be a single field, as a text box's is.</param>
		internal DesignBinding Pick (ITypeDescriptorContext context, IServiceProvider provider,
					     bool showDataSources, bool showDataMembers, bool selectListMembers,
					     object dataSource, string dataMember, DesignBinding current)
		{
			service = provider == null
				? null
				: provider.GetService (typeof (IWindowsFormsEditorService)) as IWindowsFormsEditorService;
			if (service == null)
				return null;

			picked = null;
			Build (provider, showDataSources, showDataMembers, selectListMembers, dataSource, dataMember,
			       current);
			service.DropDownControl (this);
			service = null;
			return picked;
		}

		private void Build (IServiceProvider provider, bool showDataSources, bool showDataMembers,
				    bool selectListMembers, object dataSource, string dataMember,
				    DesignBinding current)
		{
			tree.BeginUpdate ();
			try {
				tree.Nodes.Clear ();
				tree.Nodes.Add (new TreeNode ("(none)") { Tag = DesignBinding.Null });

				if (showDataMembers && dataSource != null) {
					// A field inside the source the control is already bound to.
					AddMembers (tree.Nodes, dataSource, dataMember, selectListMembers);
				} else if (showDataSources) {
					foreach (IComponent component in DataSources (provider)) {
						var node = new TreeNode (NameOf (component)) {
							Tag = new DesignBinding (component, string.Empty),
						};
						tree.Nodes.Add (node);
						if (showDataMembers)
							AddMembers (node.Nodes, component, string.Empty, selectListMembers);
					}
				}

				tree.ExpandAll ();
				Select (current);
			} finally {
				tree.EndUpdate ();
			}
		}

		/// <summary>Every component on the form that can act as a data source, which is what the
		/// designer's container holds.</summary>
		private static IEnumerable<IComponent> DataSources (IServiceProvider provider)
		{
			var found = new List<IComponent> ();
			IDesignerHost host = provider == null
				? null
				: provider.GetService (typeof (IDesignerHost)) as IDesignerHost;
			if (host == null || host.Container == null)
				return found;

			foreach (IComponent component in host.Container.Components) {
				if (component is IListSource || component is IList || component is ITypedList
				    || component is BindingSource)
					found.Add (component);
			}
			return found;
		}

		private static string NameOf (IComponent component)
		{
			if (component.Site != null && !string.IsNullOrEmpty (component.Site.Name))
				return component.Site.Name;
			return component.GetType ().Name;
		}

		/// <summary>The members of a source: its lists, and -- when a single field will do -- the
		/// properties of each list's item type.</summary>
		private void AddMembers (TreeNodeCollection into, object dataSource, string prefix,
					 bool selectListMembers)
		{
			PropertyDescriptorCollection properties;
			try {
				properties = ListBindingHelper.GetListItemProperties (dataSource, prefix, null);
			} catch (Exception) {
				return;
			}
			if (properties == null)
				return;

			foreach (PropertyDescriptor property in properties) {
				bool isList = typeof (IList).IsAssignableFrom (property.PropertyType)
					|| typeof (IListSource).IsAssignableFrom (property.PropertyType);
				if (selectListMembers && !isList)
					continue;

				string member = string.IsNullOrEmpty (prefix)
					? property.Name
					: prefix + "." + property.Name;
				into.Add (new TreeNode (property.Name) {
					Tag = new DesignBinding (dataSource, member),
				});
			}
		}

		private void Select (DesignBinding current)
		{
			if (current == null)
				return;
			foreach (TreeNode node in AllNodes (tree.Nodes)) {
				DesignBinding binding = node.Tag as DesignBinding;
				if (binding != null && binding.DataSource == current.DataSource
				    && binding.DataMember == current.DataMember) {
					tree.SelectedNode = node;
					return;
				}
			}
		}

		private static IEnumerable<TreeNode> AllNodes (TreeNodeCollection nodes)
		{
			foreach (TreeNode node in nodes) {
				yield return node;
				foreach (TreeNode child in AllNodes (node.Nodes))
					yield return child;
			}
		}

		private void OnNodeClicked (object sender, TreeNodeMouseClickEventArgs e)
		{
			Commit (e.Node);
		}

		private void OnKeyDown (object sender, KeyEventArgs e)
		{
			if (e.KeyCode == Keys.Return)
				Commit (tree.SelectedNode);
		}

		private void Commit (TreeNode node)
		{
			if (node == null)
				return;
			picked = node.Tag as DesignBinding;
			if (service != null)
				service.CloseDropDown ();
		}
	}
}
