// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// The component editor's dialog, as .NET's: a selector of the pages down the left, the active page beside
// it under a grey strip, and OK, Cancel, Apply and Help below. Each page sits in a site that tells the form
// when the page has changes; Apply (or OK) saves the changed pages inside a component change, so a designer
// sees it as one edit. Mono had only the shell of the class. The selector is .NET's: items drawn with their
// icon and caption, the selected one on a dithered ground, the hot one with a raised edge.

using System.ComponentModel;
using System.ComponentModel.Design;
using System.Drawing;
using System.Reflection;
using System.Runtime.InteropServices;

namespace System.Windows.Forms.Design {
	[ToolboxItem (false)]
	[ClassInterfaceAttribute (ClassInterfaceType.AutoDispatch)]
	[ComVisible (true)]
	public class ComponentEditorForm : Form {
		readonly IComponent component;
		readonly Type [] pageTypes;
		ComponentEditorPageSite [] pageSites;
		Size maxSize = Size.Empty;
		int initialActivePage;
		int activePage;
		bool dirty;
		bool firstActivate;

		readonly Panel pageHost = new Panel ();
		PageSelector selector;
		ImageList selectorImageList;
		Button okButton;
		Button cancelButton;
		Button applyButton;
		Button helpButton;

		const int BUTTON_WIDTH = 80;
		const int BUTTON_HEIGHT = 23;
		const int BUTTON_PAD = 6;
		const int MIN_SELECTOR_WIDTH = 90;
		const int SELECTOR_PADDING = 10;
		const int STRIP_HEIGHT = 4;

		public ComponentEditorForm (object component, Type [] pageTypes) : base ()
		{
			if (!(component is IComponent))
				throw new ArgumentException ("Parameter must be of type IComponent.", nameof (component));
			this.component = (IComponent) component;
			this.pageTypes = pageTypes;
			dirty = false;
			firstActivate = true;
			activePage = -1;
			initialActivePage = 0;

			FormBorderStyle = FormBorderStyle.FixedDialog;
			MinimizeBox = false;
			MaximizeBox = false;
			ShowInTaskbar = false;
			Icon = null;
			StartPosition = FormStartPosition.CenterParent;

			OnNewObjects ();
			OnConfigureUI ();
		}

		#region Public Properties
		[Browsable (false)]
		[EditorBrowsable (EditorBrowsableState.Never)]
		[DesignerSerializationVisibility (DesignerSerializationVisibility.Hidden)]
		new public virtual bool AutoSize {
			get { return base.AutoSize; }
			set { base.AutoSize = value; }
		}
		#endregion

		#region Public Events
		[Browsable (false)]
		[EditorBrowsable (EditorBrowsableState.Never)]
		public new event EventHandler AutoSizeChanged {
			add { base.AutoSizeChanged += value; }
			remove { base.AutoSizeChanged -= value; }
		}
		#endregion

		// the changed pages saved, inside one component change
		void ApplyChanges (bool lastApply)
		{
			if (!dirty)
				return;
			IComponentChangeService changeService = component.Site?.GetService (typeof (IComponentChangeService)) as IComponentChangeService;
			if (changeService != null) {
				try {
					changeService.OnComponentChanging (component, null);
				} catch (CheckoutException e) when (e == CheckoutException.Canceled) {
					return;
				}
			}
			for (int n = 0; n < pageSites.Length; n++) {
				if (pageSites [n].Dirty) {
					pageSites [n].GetPageControl ().ApplyChanges ();
					pageSites [n].Dirty = false;
				}
			}
			changeService?.OnComponentChanged (component, null, null, null);
			applyButton.Enabled = false;
			cancelButton.Text = "Close";
			dirty = false;
			if (!lastApply) {
				for (int n = 0; n < pageSites.Length; n++)
					pageSites [n].GetPageControl ().OnApplyComplete ();
			}
		}

		void OnButtonClick (object sender, EventArgs e)
		{
			if (sender == okButton) {
				ApplyChanges (true);
				DialogResult = DialogResult.OK;
			} else if (sender == cancelButton) {
				DialogResult = DialogResult.Cancel;
			} else if (sender == applyButton) {
				ApplyChanges (false);
			} else if (sender == helpButton) {
				ShowPageHelp ();
			}
		}

		void OnConfigureUI ()
		{
			Font uiFont = DefaultFont;
			if (component.Site?.GetService (typeof (IUIService)) is IUIService uiService && uiService.Styles ["DialogFont"] is Font dialogFont)
				uiFont = dialogFont;
			Font = uiFont;

			okButton = new Button ();
			cancelButton = new Button ();
			applyButton = new Button ();
			helpButton = new Button ();

			selectorImageList = new ImageList { ImageSize = new Size (16, 16) };
			selector = new PageSelector { ImageList = selectorImageList };
			selector.AfterSelect += new TreeViewEventHandler (OnSelChangeSelector);

			var grayStrip = new Label { BackColor = SystemColors.ControlDark };

			int selectorWidth = MIN_SELECTOR_WIDTH;
			if (pageSites != null && pageSites.Length != 0) {
				using Graphics graphics = CreateGraphics ();
				for (int n = 0; n < pageSites.Length; n++) {
					ComponentEditorPage page = pageSites [n].GetPageControl ();
					string title = page.Title;
					int titleWidth = (int) graphics.MeasureString (title, Font).Width;
					selectorImageList.Images.Add (page.Icon.ToBitmap ());
					selector.Nodes.Add (new TreeNode (title, n, n));
					if (titleWidth > selectorWidth)
						selectorWidth = titleWidth;
				}
			}
			selectorWidth += SELECTOR_PADDING;

			ISite site = component.Site;
			Text = site != null ? string.Format ("{0} Properties", site.Name) : "Properties";

			var pageHostBounds = new Rectangle (2 * BUTTON_PAD + selectorWidth, 2 * BUTTON_PAD + STRIP_HEIGHT, maxSize.Width, maxSize.Height);
			pageHost.Bounds = pageHostBounds;
			grayStrip.Bounds = new Rectangle (pageHostBounds.X, BUTTON_PAD, pageHostBounds.Width, STRIP_HEIGHT);

			if (pageSites != null) {
				var pageBounds = new Rectangle (0, 0, pageHostBounds.Width, pageHostBounds.Height);
				for (int n = 0; n < pageSites.Length; n++)
					pageSites [n].GetPageControl ().GetControl ().Bounds = pageBounds;
			}

			int xFrame = SystemInformation.FixedFrameBorderSize.Width;
			Rectangle bounds = pageHostBounds;
			Size = new Size (bounds.Width + 3 * (BUTTON_PAD + xFrame) + selectorWidth,
				bounds.Height + STRIP_HEIGHT + 4 * BUTTON_PAD + BUTTON_HEIGHT + 2 * xFrame + SystemInformation.CaptionHeight);

			selector.Bounds = new Rectangle (BUTTON_PAD, BUTTON_PAD, selectorWidth, bounds.Height + STRIP_HEIGHT + 2 * BUTTON_PAD + BUTTON_HEIGHT);

			bounds.X = bounds.Width + bounds.X - BUTTON_WIDTH;
			bounds.Y = bounds.Height + bounds.Y + BUTTON_PAD;
			bounds.Width = BUTTON_WIDTH;
			bounds.Height = BUTTON_HEIGHT;

			helpButton.Bounds = bounds;
			helpButton.Text = "Help";
			helpButton.Click += new EventHandler (OnButtonClick);
			helpButton.Enabled = false;
			helpButton.FlatStyle = FlatStyle.System;

			bounds.X -= BUTTON_WIDTH + BUTTON_PAD;
			applyButton.Bounds = bounds;
			applyButton.Text = "&Apply";
			applyButton.Click += new EventHandler (OnButtonClick);
			applyButton.Enabled = false;
			applyButton.FlatStyle = FlatStyle.System;

			bounds.X -= BUTTON_WIDTH + BUTTON_PAD;
			cancelButton.Bounds = bounds;
			cancelButton.Text = "Cancel";
			cancelButton.Click += new EventHandler (OnButtonClick);
			cancelButton.FlatStyle = FlatStyle.System;
			CancelButton = cancelButton;

			bounds.X -= BUTTON_WIDTH + BUTTON_PAD;
			okButton.Bounds = bounds;
			okButton.Text = "OK";
			okButton.Click += new EventHandler (OnButtonClick);
			okButton.FlatStyle = FlatStyle.System;
			AcceptButton = okButton;

			Controls.Clear ();
			Controls.AddRange (new Control [] { selector, grayStrip, pageHost, okButton, cancelButton, applyButton, helpButton });

			// .NET keeps the old autoscale base size here, at its non-standard height
#pragma warning disable CS0618
			AutoScaleBaseSize = new Size (5, 14);
			ApplyAutoScaling ();
#pragma warning restore CS0618
		}

		protected override void OnActivated (EventArgs e)
		{
			base.OnActivated (e);
			if (firstActivate) {
				firstActivate = false;
				if (pageSites.Length == 0)
					return;
				selector.SelectedNode = selector.Nodes [initialActivePage];
				pageSites [initialActivePage].Active = true;
				activePage = initialActivePage;
				helpButton.Enabled = pageSites [activePage].GetPageControl ().SupportsHelp ();
			}
		}

		protected override void OnHelpRequested (HelpEventArgs e)
		{
			base.OnHelpRequested (e);
			ShowPageHelp ();
		}

		// a site and a page for each page type, every page the size of the largest
		void OnNewObjects ()
		{
			maxSize = new Size (3 * (BUTTON_WIDTH + BUTTON_PAD), 24 * pageTypes.Length);
			pageSites = new ComponentEditorPageSite [pageTypes.Length];
			for (int n = 0; n < pageTypes.Length; n++) {
				pageSites [n] = new ComponentEditorPageSite (pageHost, pageTypes [n], component, this);
				Size pageSize = pageSites [n].GetPageControl ().Size;
				if (pageSize.Width > maxSize.Width)
					maxSize.Width = pageSize.Width;
				if (pageSize.Height > maxSize.Height)
					maxSize.Height = pageSize.Height;
			}
			for (int n = 0; n < pageSites.Length; n++)
				pageSites [n].GetPageControl ().Size = maxSize;
		}

		protected virtual void OnSelChangeSelector (object source, TreeViewEventArgs e)
		{
			// the tree selects before the form is first activated
			if (firstActivate)
				return;
			int newPage = selector.SelectedNode.Index;
			if (newPage == activePage)
				return;
			if (activePage != -1) {
				if (pageSites [activePage].AutoCommit)
					ApplyChanges (false);
				pageSites [activePage].Active = false;
			}
			activePage = newPage;
			pageSites [activePage].Active = true;
			helpButton.Enabled = pageSites [activePage].GetPageControl ().SupportsHelp ();
		}

		public override bool PreProcessMessage (ref Message msg)
		{
			if (pageSites != null && activePage >= 0 && pageSites [activePage].GetPageControl ().IsPageMessage (ref msg))
				return true;
			return base.PreProcessMessage (ref msg);
		}

		void SetDirty ()
		{
			dirty = true;
			applyButton.Enabled = true;
			cancelButton.Text = "Cancel";
		}

		public virtual DialogResult ShowForm ()
		{
			return ShowForm (null, 0);
		}

		public virtual DialogResult ShowForm (int page)
		{
			return ShowForm (null, page);
		}

		public virtual DialogResult ShowForm (IWin32Window owner)
		{
			return ShowForm (owner, 0);
		}

		public virtual DialogResult ShowForm (IWin32Window owner, int page)
		{
			initialActivePage = page;
			ShowDialog (owner);
			return DialogResult;
		}

		void ShowPageHelp ()
		{
			if (activePage >= 0 && pageSites [activePage].GetPageControl ().SupportsHelp ())
				pageSites [activePage].GetPageControl ().ShowHelp ();
		}

		/// <summary>Where a page lives: its parent in the form, its component, and whether it has changes.</summary>
		sealed class ComponentEditorPageSite : IComponentEditorPageSite
		{
			readonly Control parent;
			readonly ComponentEditorPage pageControl;
			readonly ComponentEditorForm form;
			bool isActive;

			internal ComponentEditorPageSite (Control parent, Type pageClass, IComponent component, ComponentEditorForm form)
			{
				this.parent = parent;
				this.form = form ?? throw new ArgumentNullException (nameof (form));
				try {
					pageControl = (ComponentEditorPage) Activator.CreateInstance (pageClass);
				} catch (TargetInvocationException e) {
					throw new TargetInvocationException (string.Format ("ComponentEditorControl: {0} cannot be created.", e), e.InnerException);
				}
				pageControl.SetSite (this);
				pageControl.SetComponent (component);
			}

			internal bool Active {
				set {
					if (value) {
						pageControl.CreateControl ();
						pageControl.Activate ();
					} else {
						pageControl.Deactivate ();
					}
					isActive = value;
				}
			}

			internal bool AutoCommit => pageControl.CommitOnDeactivate;

			internal bool Dirty { get; set; }

			public Control GetControl () => parent;

			internal ComponentEditorPage GetPageControl () => pageControl;

			public void SetDirty ()
			{
				if (isActive)
					Dirty = true;
				form.SetDirty ();
			}
		}

		/// <summary>The page list: flat, full-row, each item its icon and caption, the selected one on a dithered
		/// ground, the one under the mouse or with the focus edged as a raised button.</summary>
		sealed class PageSelector : TreeView
		{
			const int PADDING_VERT = 3;
			const int PADDING_HORZ = 4;
			const int SIZE_ICON_X = 16;
			const int SIZE_ICON_Y = 16;

			public PageSelector ()
			{
				HotTracking = true;
				HideSelection = false;
				BackColor = SystemColors.Control;
				Indent = 0;
				LabelEdit = false;
				Scrollable = false;
				ShowLines = false;
				ShowPlusMinus = false;
				ShowRootLines = false;
				BorderStyle = BorderStyle.None;
				FullRowSelect = true;
				DrawMode = TreeViewDrawMode.OwnerDrawAll;
			}

			protected override CreateParams CreateParams {
				get {
					CreateParams cp = base.CreateParams;
					cp.ExStyle |= (int) WindowExStyles.WS_EX_STATICEDGE;
					return cp;
				}
			}

			protected override void OnHandleCreated (EventArgs e)
			{
				base.OnHandleCreated (e);
				ItemHeight += 2 * PADDING_VERT;
			}

			protected override void OnDrawNode (DrawTreeNodeEventArgs e)
			{
				Graphics g = e.Graphics;
				Rectangle rc = new Rectangle (0, e.Bounds.Y, ClientSize.Width, e.Bounds.Height);
				bool selected = (e.State & TreeNodeStates.Selected) != 0;
				bool hot = (e.State & (TreeNodeStates.Hot | TreeNodeStates.Focused)) != 0;

				if (selected) {
					// the 50% pattern of ControlLightLight on Control
					using var dither = new System.Drawing.Drawing2D.HatchBrush (System.Drawing.Drawing2D.HatchStyle.Percent50, SystemColors.ControlLightLight, SystemColors.Control);
					g.FillRectangle (dither, rc);
				} else {
					using var back = new SolidBrush (SystemColors.Control);
					g.FillRectangle (back, rc);
				}

				// the dialog's font, so a tracked item is not underlined
				Font font = Parent?.Font ?? Font;
				var text = new Rectangle (rc.Left + SIZE_ICON_X + 2 * PADDING_HORZ, rc.Top, rc.Width - SIZE_ICON_X - 2 * PADDING_HORZ, rc.Height);
				TextRenderer.DrawText (g, e.Node.Text, font, text, SystemColors.ControlText,
					TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);

				if (ImageList != null && e.Node.ImageIndex >= 0 && e.Node.ImageIndex < ImageList.Images.Count)
					ImageList.Draw (g, PADDING_HORZ, rc.Top + ((rc.Height - SIZE_ICON_Y) >> 1), e.Node.ImageIndex);

				if (hot) {
					using var light = new Pen (SystemColors.ControlLightLight);
					using var dark = new Pen (SystemColors.ControlDark);
					g.DrawLine (light, rc.Left, rc.Top, rc.Right - 1, rc.Top);
					g.DrawLine (light, rc.Left, rc.Top, rc.Left, rc.Bottom - 1);
					g.DrawLine (dark, rc.Left, rc.Bottom - 1, rc.Right - 1, rc.Bottom - 1);
					g.DrawLine (dark, rc.Right - 1, rc.Top, rc.Right - 1, rc.Bottom - 1);
				}
			}
		}
	}
}
