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
//	Jordi Mas i Hernandez, jordi@ximian.com
//

using System;
using System.ComponentModel;
using System.ComponentModel.Design;
using System.Drawing;

namespace System.Windows.Forms
{
	[DefaultEvent("Popup")]
	public class ContextMenu : Menu
	{
		private RightToLeft right_to_left;
		private Control	src_control;

		#region Events
		static object CollapseEvent = new object ();
		static object PopupEvent = new object ();

		public event EventHandler Collapse {
			add { Events.AddHandler (CollapseEvent, value); }
			remove { Events.RemoveHandler (CollapseEvent, value); }
		}

		public event EventHandler Popup {
			add { Events.AddHandler (PopupEvent, value); }
			remove { Events.RemoveHandler (PopupEvent, value); }
		}
		
		#endregion Events

		public ContextMenu () : base (null)
		{
			tracker = new MenuTracker (this);
			right_to_left = RightToLeft.Inherit;
		}

		public ContextMenu (MenuItem [] menuItems) : base (menuItems)
		{
			tracker = new MenuTracker (this);
			right_to_left = RightToLeft.Inherit;
		}
		
		#region Public Properties
		
		[Localizable(true)]
		[DefaultValue (RightToLeft.No)]
		public virtual RightToLeft RightToLeft {
			get { return right_to_left; }
			set { right_to_left = value; }
		}

		[Browsable(false)]
		[DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
		public Control SourceControl {
			get { return src_control; }
			private set
			{
				if (src_control == value)
					return;
				src_control = value;
				OnSetSourceControlDone (new SetSourceControlDoneArgs (value));
			}
		}

		#endregion Public Properties

		#region Public Methods

		protected internal virtual bool ProcessCmdKey (ref Message msg, Keys keyData, Control control)
		{
			SourceControl = control;
			return ProcessCmdKey (ref msg, keyData);
		}

		protected internal virtual void OnCollapse (EventArgs e)
		{
			EventHandler eh = (EventHandler) (Events [CollapseEvent]);
			if (eh != null)
				eh (this, e);
		}

		protected internal virtual void OnPopup (EventArgs e)
		{
			EventHandler eh = (EventHandler) (Events [PopupEvent]);
			if (eh != null)
				eh (this, e);
		}
		
		public void Show (Control control, Point pos)
		{
			if (control == null)
				throw new ArgumentException ();

			SourceControl = control;
			OnPopup (EventArgs.Empty);

			// Shown through the strip machinery rather than the old tracker.
			//
			// The old one puts up a window of its own and then sits in a message loop pumping
			// for the click that will dismiss it. On this driver that loop waits for ever:
			// mouse input is DISPATCHED to the window under the pointer, not posted to a queue,
			// so nothing a nested loop is waiting for ever arrives. The menu came up, took no
			// notice of the pointer, could not be dismissed, and the next right click stacked
			// another on top of it -- five of them, in the calendar this was first seen in.
			//
			// The strips do not need a loop of their own: they are dismissed by the driver's
			// own account of where a press landed. So an old-style menu is mirrored into one and
			// shown, which also gets it the hover, the placement, and the place in the
			// automation tree that the strips have. Unlike the old call this one returns at
			// once, as ContextMenuStrip.Show does.
			ShowAsStrip (control, pos);
		}

		private ToolStripDropDownMenu strip;

		private void ShowAsStrip (Control control, Point pos)
		{
			if (strip != null) {
				strip.Close ();
				strip.Dispose ();
			}
			strip = new ToolStripDropDownMenu ();
			// A menu of this vintage is drawn by Windows itself, and Windows draws a plain one:
			// no strip down the left for icons, because an old-style entry has none to put there.
			// The strip menus keep theirs -- an application that uses them puts icons in it.
			strip.ShowImageMargin = false;
			ToolStripRenderer renderer = ThemeEngine.Current.CreateSystemMenuRenderer ();
			if (renderer != null)
				strip.Renderer = renderer;
			strip.ShowCheckMargin = AnyChecked (MenuItems);
			// The entries are read afresh every time: an old-style menu is built and rebuilt by
			// the application, often in the Popup handler that has just run.
			Mirror (MenuItems, strip.Items);
			strip.Closed += delegate {
				SourceControl = null;
				OnCollapse (EventArgs.Empty);
			};
			if (strip.Items.Count > 0)
				strip.Show (control, pos);
		}

		/// <summary>Copy a run of old-style entries into a strip's, submenus and all. The entry
		/// itself is what runs when the copy is clicked, so an application's handlers, its
		/// Select and its Popup events all fire as they always did.</summary>
		private static void Mirror (Menu.MenuItemCollection from, ToolStripItemCollection into)
		{
			foreach (MenuItem item in from) {
				if (!item.Visible)
					continue;
				if (item.Text == "-") {
					into.Add (new ToolStripSeparator ());
					continue;
				}

				var copy = new ToolStripMenuItem (item.Text);
				// Windows leaves more air around an entry in one of its own menus than a strip menu
				// leaves around one of its: measured against a stock "Go to today", the box is about
				// twenty pixels wider than the caption on each side.
				copy.Padding = new Padding (11, 1, 11, 1);
				copy.Enabled = item.Enabled;
				copy.Checked = item.Checked;
				copy.ShortcutKeys = ShortcutKeysOf (item);
				copy.ShowShortcutKeys = item.ShowShortcut;
				MenuItem clicked = item;
				copy.Click += delegate { clicked.PerformClick (); };
				if (item.MenuItems.Count > 0)
					Mirror (item.MenuItems, copy.DropDownItems);
				into.Add (copy);
			}
		}

		/// <summary>Whether anything in the menu is ticked. Only then is the room for a tick
		/// worth taking: Windows leaves none in a menu that has nothing to show there.</summary>
		private static bool AnyChecked (Menu.MenuItemCollection items)
		{
			foreach (MenuItem item in items)
				if (item.Checked)
					return true;
			return false;
		}

		private static Keys ShortcutKeysOf (MenuItem item)
		{
			try {
				return item.Shortcut == Shortcut.None ? Keys.None : (Keys) item.Shortcut;
			} catch (Exception) {
				return Keys.None;
			}
		}

		public void Show (Control control, Point pos, LeftRightAlignment alignment)
		{
			Point point;
			
			if (alignment == LeftRightAlignment.Left)
				point = new Point ((pos.X - control.Width), pos.Y);
			else
				point = pos;

			Show (control, point);
		}
		#endregion Public Methods

		internal void Hide ()
		{
			if (strip != null)
				strip.Close ();
			tracker.Deactivate ();
			SourceControl = null;
		}

		#region Internal Events

		internal delegate void SetSourceControlDoneHandler (object sender, SetSourceControlDoneArgs e);
		
		// Is used by UIA API.
		[Browsable (false)]
		internal static event SetSourceControlDoneHandler SetSourceControlDone; 

		private void OnSetSourceControlDone (SetSourceControlDoneArgs e)
		{
			if (SetSourceControlDone != null)
				SetSourceControlDone (this, e);
		}

		internal class SetSourceControlDoneArgs : EventArgs
		{
			public readonly Control NewOwner;

			public SetSourceControlDoneArgs (Control newOwner)
			{
				NewOwner = newOwner;
			}
		}

		#endregion
	}
}
