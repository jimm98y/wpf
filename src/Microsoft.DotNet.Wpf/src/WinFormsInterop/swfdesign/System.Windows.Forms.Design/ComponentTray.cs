//
// System.Windows.Forms.Design.ComponentTray
//
// Authors:
//	  Ivan N. Zlatev (contact i-nZ.net)
//
// (C) 2006-2007 Ivan N. Zlatev

//
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

// STUBS ONLY!!!
//
//

using System;
using System.ComponentModel;
using System.ComponentModel.Design;
using System.Windows.Forms;
using System.Drawing;
using System.Drawing.Design;
using System.Collections;

namespace System.Windows.Forms.Design
{
	[DesignTimeVisible (false)]
	[ToolboxItem (false)]
	[ProvideProperty ("Location", typeof (IComponent))]
	public class ComponentTray : ScrollableControl, IExtenderProvider
	{

		private IServiceProvider _serviceProvider;
		private IDesigner _mainDesigner = null;
		private bool _showLargeIcons = false;
		private bool _autoArrange = false;

		public ComponentTray (IDesigner mainDesigner, IServiceProvider serviceProvider)
		{
			if (mainDesigner == null) {
				throw new ArgumentNullException ("mainDesigner");
			}
			if (serviceProvider == null) {
				throw new ArgumentNullException ("serviceProvider");
			}

			_mainDesigner = mainDesigner;
			_serviceProvider = serviceProvider;
		}

		public bool AutoArrange {
			get { return _autoArrange; }
			set { _autoArrange = value; }
		}

		// The tray's components, in the order they were added, and where each sits in the tray (dotnet/winforms keeps a
		// TrayControl per component; this tray draws none, so it keeps the bookkeeping the public API answers from).
		private readonly ArrayList _trayComponents = new ArrayList ();
		private readonly Hashtable _trayLocations = new Hashtable ();

		public int ComponentCount {
			get { return _trayComponents.Count; }
		}

		public bool ShowLargeIcons {
			get { return _showLargeIcons; }
			set { _showLargeIcons = value; }
		}


		/// <summary>Takes a component the tray can display (CanDisplayComponent), once.</summary>
		public virtual void AddComponent (IComponent component)
		{
			if (component == null || !CanDisplayComponent (component) || _trayComponents.Contains (component))
				return;
			_trayComponents.Add (component);
		}

		protected virtual bool CanCreateComponentFromTool (ToolboxItem tool)
		{
			return true;
		}

		protected virtual bool CanDisplayComponent (IComponent component)
		{
			// This tray draws no component views (dotnet/winforms answers DesignTimeVisible here and shows each
			// one): it displays nothing, so it takes nothing, and the designer shows no empty tray.
			return false;
		}

		[MonoTODO]
		public void CreateComponentFromTool (ToolboxItem tool)
		{
		}

		[MonoTODO]
		protected void DisplayError (Exception e)
		{
		}

		protected override void Dispose (bool disposing)
		{
		}

		[Browsable (false)]
		[Category ("Layout")]
		[DesignerSerializationVisibility (DesignerSerializationVisibility.Hidden)]
		[DesignOnly (true)]
		[Localizable (false)]
		[MonoTODO]
		public Point GetLocation (IComponent receiver)
		{
			return new Point (0,0);
		}

		[MonoTODO]
		public void SetLocation (IComponent receiver, Point location)
		{
		}

		/// <summary>The tray component after (or before) <paramref name="component"/>, null at either end; for a
		/// component not in the tray, the first (or last) one -- .NET's order.</summary>
		public IComponent GetNextComponent (IComponent component, bool forward)
		{
			int i = _trayComponents.IndexOf (component);
			if (i >= 0) {
				int target = forward ? i + 1 : i - 1;
				return target >= 0 && target < _trayComponents.Count ? (IComponent) _trayComponents [target] : null;
			}
			if (_trayComponents.Count > 0)
				return (IComponent) _trayComponents [forward ? 0 : _trayComponents.Count - 1];
			return null;
		}

		[Browsable (false)]
		[Category ("Layout")]
		[DesignOnly (true)]
		[Localizable (false)]
		public Point GetTrayLocation (IComponent receiver)
		{
			// Relative to the tray's scrolled origin, as .NET answers its tray control's location.
			Point loc = _trayLocations [receiver] is Point p ? p : Point.Empty;
			Point scroll = AutoScrollPosition;
			return new Point (loc.X - scroll.X, loc.Y - scroll.Y);
		}

		public bool IsTrayComponent (IComponent comp)
		{
			return comp != null && _trayComponents.Contains (comp);
		}

		public void SetTrayLocation (IComponent receiver, Point location)
		{
			if (receiver == null || !_trayComponents.Contains (receiver))
				return;
			Point scroll = AutoScrollPosition;
			_trayLocations [receiver] = new Point (location.X + scroll.X, location.Y + scroll.Y);
		}

		[MonoTODO]
		protected override void OnMouseDoubleClick (MouseEventArgs e)
		{
		}

		[MonoTODO]
		protected override void OnDragDrop (DragEventArgs de)
		{
		}

		[MonoTODO]
		protected override void OnDragEnter (DragEventArgs de)
		{
		}

		[MonoTODO]
		protected override void OnDragLeave (EventArgs e)
		{
		}

		[MonoTODO]
		protected override void OnDragOver (DragEventArgs de)
		{
		}

		[MonoTODO]
		protected override void OnGiveFeedback (GiveFeedbackEventArgs gfevent)
		{
		}

		[MonoTODO]
		protected override void OnLayout (LayoutEventArgs levent)
		{
		}

		[MonoTODO]
		protected virtual void OnLostCapture ()
		{
		}

		[MonoTODO]
		protected override void OnMouseDown (MouseEventArgs e)
		{
		}

		[MonoTODO]
		protected override void OnMouseMove (MouseEventArgs e)
		{
		}

		[MonoTODO]
		protected override void OnMouseUp (MouseEventArgs e)
		{
		}

		[MonoTODO]
		protected override void OnPaint (PaintEventArgs pe)
		{
		}

		[MonoTODO]
		protected virtual void OnSetCursor ()
		{
		}

		public virtual void RemoveComponent (IComponent component)
		{
			_trayComponents.Remove (component);
			_trayLocations.Remove (component);
		}

		[MonoTODO]
		protected override void WndProc (ref Message m)
		{
			base.WndProc (ref m);
		}

		bool IExtenderProvider.CanExtend (object component)
		{
			return false;
		}

		protected override object GetService (Type serviceType)
		{
			if (_serviceProvider != null) {
				return _serviceProvider.GetService (serviceType);
			}
			return null;
		}

	}
}
