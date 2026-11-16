// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// Drag and drop between a WinForms form and other applications on Linux (Wayland).
//
// As on macOS (CocoaHost.DragDrop.cs): a form shown with no WPF around it took no part in the system's
// drag and drop -- only a WindowsFormsHost connected WinForms to it. The form's toplevel is a
// WaylandWindow, whose drags WindowsBase's WaylandDragDrop receives; a target of the form's own answers
// them with the driver's drag injections at the point under the drag. A drag the form starts is a
// wl_data_device drag from its surface, offering what SystemDragData makes of the data object, waited for
// in the Wayland loop the form's messages run in.
//

extern alias WindowsBase;

using System.Collections.Generic;
using System.Drawing;
using ForeignDragDrop = WindowsBase::MS.Internal.Interop.ForeignDragDrop;
using ForeignDropTarget = WindowsBase::MS.Internal.Interop.ForeignDropTarget;
using WaylandWindow = WindowsBase::MS.Internal.Interop.Wayland.WaylandWindow;

namespace System.Windows.Forms
{
	internal sealed partial class WaylandFormHost
	{
		private static bool s_systemDragActive;
		private FormDropTarget _dropTarget;
		private IntPtr _dropWindow;

		/// <summary>Makes the form's toplevel a drop target and its drags system drags.</summary>
		private void EnableDragDrop ()
		{
			try {
				_dropTarget ??= new FormDropTarget (this);
				_dropWindow = _window.Handle;
				ForeignDragDrop.Register (_dropWindow, _dropTarget);
				if (_form.IsHandleCreated)
					_driver.RegisterDragSource (_form.Handle, StartSystemDrag);
			} catch (Exception e) {
				Console.Error.WriteLine ("wayland form: drag and drop unavailable: " + e.Message);
			}
		}

		private void DisableDragDrop ()
		{
			try {
				if (_dropWindow != IntPtr.Zero)
					ForeignDragDrop.Unregister (_dropWindow);
				_dropWindow = IntPtr.Zero;
				if (_form.IsHandleCreated)
					_driver.UnregisterDragSource (_form.Handle);
			} catch { }
		}

		/// <summary>DragEventArgs.KeyState: the left button and the modifiers held.</summary>
		private static int KeyState ()
		{
			Keys m = Control.ModifierKeys;
			int state = 1;
			if ((m & Keys.Shift) != 0) state |= 4;
			if ((m & Keys.Control) != 0) state |= 8;
			if ((m & Keys.Alt) != 0) state |= 32;
			return state;
		}

		private sealed class FormDropTarget : ForeignDropTarget
		{
			private readonly WaylandFormHost _host;
			private IDataObject _data;

			public FormDropTarget (WaylandFormHost host) => _host = host;

			public override int DragEnter (double screenX, double screenY, string[] mimeTypes, Func<string, byte[]> read, int allowedEffects)
			{
				_data = SystemDragData.ToDataObject (mimeTypes, read);
				return Deliver (screenX, screenY, allowedEffects, 1);
			}

			public override int DragOver (double screenX, double screenY, int allowedEffects)
				=> Deliver (screenX, screenY, allowedEffects, 2);

			public override void DragLeave ()
			{
				_data = null;
				try { OnFormThread (() => { _host._driver.InjectDragLeave (); return 0; }); } catch { }
			}

			public override int Drop (double screenX, double screenY, int allowedEffects)
			{
				int effect = Deliver (screenX, screenY, allowedEffects, 3);
				_data = null;
				return effect;
			}

			private int Deliver (double screenX, double screenY, int allowedEffects, int phase)
			{
				IDataObject data = _data;
				if (data == null || _host._closed || !_host._form.IsHandleCreated)
					return 0;
				try {
					return OnFormThread (() => {
						// the screen pixels the pointer path uses, at the driver's scale (OnMouse)
						int x = (int)Math.Floor (screenX / _host._scale), y = (int)Math.Floor (screenY / _host._scale);
						var allowed = (DragDropEffects)allowedEffects;
						IntPtr root = _host._form.Handle;
						DragDropEffects effect = phase switch {
							1 => _host._driver.InjectDragEnterIn (root, x, y, data, allowed, KeyState ()),
							2 => _host._driver.InjectDragOverIn (root, x, y, data, allowed, KeyState ()),
							_ => _host._driver.InjectDragDropIn (root, x, y, data, allowed, KeyState ()),
						};
						_host.Present ();
						return (int)(effect & allowed);
					});
				} catch (Exception e) {
					Console.Error.WriteLine ("wayland form: drop target threw: " + e);
					return 0;
				}
			}

			private int OnFormThread (Func<int> body)
				=> _host._form.InvokeRequired ? (int)_host._form.Invoke (body) : body ();
		}

		/// <summary>Control.DoDragDrop from one of this form's controls: a wl_data_device drag.</summary>
		private DragDropEffects StartSystemDrag (object data, DragDropEffects allowed)
		{
			if (_closed || s_systemDragActive)
				return DragDropEffects.None;
			IDataObject dataObject = data as IDataObject ?? new DataObject (data);

			// Where WPF can start it (a WindowsFormsHost lent the driver its drag), WPF does (CocoaHost's
			// reasons); the form's own drop target takes the object back by the in-process marker.
			if (_driver.StartDragRequested is { } wpf) {
				SystemDragData.Current = dataObject;
				try { return wpf (data, allowed); } finally { SystemDragData.Current = null; }
			}

			Dictionary<string, byte[]> payload = SystemDragData.Payload (dataObject);
			var mimes = new string [payload.Count];
			payload.Keys.CopyTo (mimes, 0);
			SystemDragData.Current = dataObject;
			s_systemDragActive = true;
			try {
				ForeignDragDrop.Trace ($"winforms drag from wayland window 0x{_window.Handle:x}: {string.Join (", ", mimes)}");
				int effect = ForeignDragDrop.StartDrag (_window.Handle, mimes,
					mime => payload.TryGetValue (mime, out byte[] bytes) ? bytes : null,
					(int)allowed, WaitForDrag, out bool started);
				ForeignDragDrop.Trace ($"winforms drag returned: started {started}, effect {effect}");
				return started ? (DragDropEffects)effect & allowed : DragDropEffects.None;
			} catch (Exception e) {
				Console.Error.WriteLine ("wayland form: the drag threw: " + e);
				return DragDropEffects.None;
			} finally {
				s_systemDragActive = false;
				SystemDragData.Current = null;
			}
		}

		/// <summary>DoDragDrop returns with the drag's outcome, as on Windows: the compositor's events are
		/// read (the drag source's among them) and the application's messages go on meanwhile.</summary>
		private static void WaitForDrag (Func<bool> ended)
		{
			while (!ended ()) {
				WaylandWindow.RunLoop (16);
				Application.DoEvents ();
			}
		}
	}
}
