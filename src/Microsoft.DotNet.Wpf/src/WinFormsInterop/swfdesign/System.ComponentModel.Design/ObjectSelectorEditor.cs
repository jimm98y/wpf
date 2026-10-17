// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
//
// Ported from dotnet/winforms (ObjectSelectorEditor, its Selector tree and SelectorNode): a drop-down tree of the
// values a property can take, filled by FillTreeWithData; a click or Enter on a node commits it, Esc restores the
// value the edit started with. Mono threw from EditValue, FillTreeWithData and every Selector member.

using System.Drawing.Design;
using System.Windows.Forms;
using System.Windows.Forms.Design;

namespace System.ComponentModel.Design
{
	public abstract class ObjectSelectorEditor : UITypeEditor
	{
		public bool SubObjectSelector;
		protected object prevValue;
		protected object currValue;
		private Selector _selector;

		public ObjectSelectorEditor ()
		{
		}

		public ObjectSelectorEditor (bool subObjectSelector)
		{
			SubObjectSelector = subObjectSelector;
		}

		public override object EditValue (ITypeDescriptorContext context, IServiceProvider provider, object value)
		{
			if (!(provider?.GetService (typeof (IWindowsFormsEditorService)) is IWindowsFormsEditorService editorService))
				return value;
			if (_selector == null) {
				_selector = new Selector (this);
				ApplyTreeViewThemeStyles (_selector);
			}
			prevValue = value;
			currValue = value;
			FillTreeWithData (_selector, context, provider);
			_selector.Start (editorService, value);
			editorService.DropDownControl (_selector);
			_selector.Stop ();
			if (prevValue != currValue)
				value = currValue;
			return value;
		}

		/// <summary>The Explorer look .NET gives the tree: hot tracking and no lines. (.NET also turns on the
		/// native tree view's double buffering and fading expandos; this tree draws itself.)</summary>
		public static void ApplyTreeViewThemeStyles (TreeView treeView)
		{
			if (treeView == null)
				throw new ArgumentNullException (nameof (treeView));
			treeView.HotTracking = true;
			treeView.ShowLines = false;
		}

		public bool EqualsToValue (object value)
		{
			return value == currValue;
		}

		protected virtual void FillTreeWithData (Selector selector, ITypeDescriptorContext context, IServiceProvider provider)
		{
			selector.Clear ();
		}

		public override UITypeEditorEditStyle GetEditStyle (ITypeDescriptorContext context)
		{
			return UITypeEditorEditStyle.DropDown;
		}

		public virtual void SetValue (object value)
		{
			currValue = value;
		}

		public class Selector : TreeView
		{
			private readonly ObjectSelectorEditor _editor;
			private IWindowsFormsEditorService _editorService;
			public bool clickSeen;

			public Selector (ObjectSelectorEditor editor)
			{
				CreateHandle ();
				_editor = editor;
				BorderStyle = BorderStyle.None;
				FullRowSelect = !editor.SubObjectSelector;
				Scrollable = true;
				CheckBoxes = false;
				ShowPlusMinus = editor.SubObjectSelector;
				ShowLines = editor.SubObjectSelector;
				ShowRootLines = editor.SubObjectSelector;
				AfterSelect += OnAfterSelect;
			}

			public SelectorNode AddNode (string label, object value, SelectorNode parent)
			{
				var newNode = new SelectorNode (label, value);
				if (parent != null)
					parent.Nodes.Add (newNode);
				else
					Nodes.Add (newNode);
				return newNode;
			}

			/// <summary>Commits the selected node's value, and closes the drop-down when the editor took it.</summary>
			private bool ChooseSelectedNodeIfEqual ()
			{
				if (_editor != null && _editorService != null && SelectedNode is SelectorNode node) {
					_editor.SetValue (node.value);
					if (_editor.EqualsToValue (node.value)) {
						_editorService.CloseDropDown ();
						return true;
					}
				}
				return false;
			}

			public void Clear ()
			{
				clickSeen = false;
				Nodes.Clear ();
			}

			protected void OnAfterSelect (object sender, TreeViewEventArgs e)
			{
				if (clickSeen) {
					ChooseSelectedNodeIfEqual ();
					clickSeen = false;
				}
			}

			protected override void OnKeyDown (KeyEventArgs e)
			{
				switch (e.KeyCode) {
				case Keys.Return:
					if (ChooseSelectedNodeIfEqual ())
						e.Handled = true;
					break;
				case Keys.Escape:
					_editor.SetValue (_editor.prevValue);
					e.Handled = true;
					_editorService?.CloseDropDown ();
					break;
				}
				base.OnKeyDown (e);
			}

			protected override void OnKeyPress (KeyPressEventArgs e)
			{
				if (e.KeyChar == '\r')
					e.Handled = true;
				base.OnKeyPress (e);
			}

			// .NET sees the click as NM_CLICK, which arrives before the selection changes; a press is the same moment
			// here, so a selection the press makes is committed.
			protected override void OnMouseDown (MouseEventArgs e)
			{
				clickSeen = true;
				base.OnMouseDown (e);
			}

			protected override void OnNodeMouseClick (TreeNodeMouseClickEventArgs e)
			{
				// No AfterSelect for a node that is already selected.
				if (e.Node == SelectedNode)
					ChooseSelectedNodeIfEqual ();
				base.OnNodeMouseClick (e);
			}

			public bool SetSelection (object value, TreeNodeCollection nodes)
			{
				nodes ??= Nodes;
				int length = nodes.Count;
				if (length == 0)
					return false;
				TreeNode [] treeNodes = new TreeNode [length];
				nodes.CopyTo (treeNodes, 0);
				for (int i = 0; i < length; i++) {
					if (((SelectorNode) treeNodes [i]).value == value) {
						SelectedNode = treeNodes [i];
						return true;
					}
					if (treeNodes [i].Nodes != null && treeNodes [i].Nodes.Count != 0) {
						treeNodes [i].Expand ();
						if (SetSelection (value, treeNodes [i].Nodes))
							return true;
						treeNodes [i].Collapse ();
					}
				}
				return false;
			}

			public void Start (IWindowsFormsEditorService edSvc, object value)
			{
				_editorService = edSvc;
				clickSeen = false;
				SetSelection (value, Nodes);
			}

			public void Stop ()
			{
				_editorService = null;
			}

			protected override void WndProc (ref Message m)
			{
				switch (m.Msg) {
				case 0x0087:   // WM_GETDLGCODE: every key is ours (Enter, Esc)
					m.Result = (IntPtr) ((long) m.Result | 0x0004);
					return;
				case 0x0200:   // WM_MOUSEMOVE
					clickSeen = false;
					break;
				}
				base.WndProc (ref m);
			}
		}

		public class SelectorNode : TreeNode
		{
			public object value;

			public SelectorNode (string label, object value) : base (label)
			{
				this.value = value;
			}
		}
	}
}
