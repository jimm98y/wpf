// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// A form's part in per-monitor DPI, ported from .NET's Form: WM_DPICHANGED raises DpiChanged and
// rescales the form and its children (ContainerControl.ScaleContainerForDpi), and WM_GETDPISCALEDSIZE
// lets a font-scaled form ask for a size that is not the linear one.

using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;

namespace System.Windows.Forms
{
	public partial class Form
	{
		static readonly object DpiChangedEvent = new object ();

		// The form's size at each DPI it has been at, while only DPI changes resize it: going back
		// to a DPI gives back the size it had there (AutoScaleMode.Font only).
		private Dictionary<int, Size> _dpiFormSizes;
		private bool _processingDpiChanged;

		/// <summary>Occurs when the DPI of the display the form is on changes: it was moved to
		/// another, or the display's scale was changed.</summary>
		public event DpiChangedEventHandler DpiChanged {
			add { Events.AddHandler (DpiChangedEvent, value); }
			remove { Events.RemoveHandler (DpiChangedEvent, value); }
		}

		[Browsable (true)]
		[EditorBrowsable (EditorBrowsableState.Always)]
		protected virtual void OnDpiChanged (DpiChangedEventArgs e)
		{
			if (e.DeviceDpiNew == e.DeviceDpiOld)
				return;
			OriginalDeviceDpiInternal = e.DeviceDpiOld;

			((DpiChangedEventHandler) Events [DpiChangedEvent])?.Invoke (this, e);
			if (e.Cancel)
				return;

			try {
				// Cache the size for the current and the new DPI, so going back restores it.
				if (AutoScaleMode == AutoScaleMode.Font) {
					_dpiFormSizes ??= new Dictionary<int, Size> ();
					if (!_dpiFormSizes.ContainsKey (e.DeviceDpiNew))
						_dpiFormSizes.Add (e.DeviceDpiNew, new Size (e.SuggestedRectangle.Width, e.SuggestedRectangle.Height));
					if (!_dpiFormSizes.ContainsKey (e.DeviceDpiOld))
						_dpiFormSizes.Add (e.DeviceDpiOld, Size);
					_processingDpiChanged = true;
				}
				ScaleContainerForDpi (e.DeviceDpiNew, e.DeviceDpiOld, e.SuggestedRectangle);
			} finally {
				_processingDpiChanged = false;
			}
		}

		/// <summary>WM_DPICHANGED.</summary>
		internal void WmDpiChanged (ref Message m)
		{
			var e = new DpiChangedEventArgs (DeviceDpiInternal, m);
			DeviceDpiInternal = e.DeviceDpiNew;
			OnDpiChanged (e);
		}

		/// <summary>Lets the form say which size it wants at another DPI (WM_GETDPISCALEDSIZE):
		/// the font-scaled one for AutoScaleMode.Font, the linear one otherwise.</summary>
		[Browsable (true)]
		[EditorBrowsable (EditorBrowsableState.Advanced)]
		protected virtual bool OnGetDpiScaledSize (int deviceDpiOld, int deviceDpiNew, ref Size desiredSize)
		{
			// .NET: only a Dpi-scaled form takes Windows' linear size; any other asks for its size
			// scaled by its auto-scale factor -- unchanged when it has no dimensions to scale by.
			if (AutoScaleMode == AutoScaleMode.Dpi)
				return false;
			Font fontForDpi = GetScaledFont (Font, deviceDpiNew, deviceDpiOld);
			SizeF current = GetCurrentAutoScaleDimensions (fontForDpi);
			SizeF factor = GetCurrentAutoScaleFactor (current, AutoScaleDimensions);
			desiredSize.Width = (int) (Size.Width * factor.Width);
			desiredSize.Height = (int) (Size.Height * factor.Height);
			return true;
		}

		/// <summary>The size the form asks for at <paramref name="dpi"/> (WM_GETDPISCALEDSIZE), or
		/// false for the linear one.</summary>
		internal bool GetDpiScaledSize (int dpi, out Size size)
		{
			size = default;
			if (_dpiFormSizes != null && _dpiFormSizes.TryGetValue (dpi, out size))
				return true;
			return OnGetDpiScaledSize (DeviceDpiInternal, dpi, ref size);
		}

		/// <summary>WM_GETDPISCALEDSIZE as the host asks it.</summary>
		internal bool GetDpiScaledSizeForHost (int dpi, out Size size) => GetDpiScaledSize (dpi, out size);

		/// <summary>A resize that is not a DPI change forgets the sizes kept per DPI.</summary>
		internal void ClearDpiFormSizes ()
		{
			if (!_processingDpiChanged)
				_dpiFormSizes?.Clear ();
		}
	}
}
