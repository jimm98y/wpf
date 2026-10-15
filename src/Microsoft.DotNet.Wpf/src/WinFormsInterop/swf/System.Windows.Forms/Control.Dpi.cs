// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// A control's part in per-monitor DPI, ported from .NET's Control: the font it shows at each DPI
// (ScaledControlFont, a cache per DPI), the DPI its window takes when it is created
// (HandleHighDpi) and the messages a DPI change brings a child window
// (WM_DPICHANGED_BEFOREPARENT / AFTERPARENT).

using System.Collections.Generic;
using System.Drawing;

namespace System.Windows.Forms
{
	public partial class Control
	{
		// The font scaled for a DPI change, kept apart from one the application sets.
		private Font _scaledControlFont;
		// The fonts of this control at each DPI it has been at (PerMonitorV2).
		private Dictionary<int, Font> _dpiFonts;

		internal Font ScaledControlFont {
			get => _scaledControlFont;
			set => _scaledControlFont = value;
		}

		/// <summary>Whether the application set this control's font (it is not inherited).</summary>
		internal bool IsFontSet () => font != null;

		/// <summary>The font at <paramref name="newDpi"/>: <paramref name="font"/> scaled by the
		/// change, kept per DPI so moving back gives back the same font.</summary>
		internal Font GetScaledFont (Font font, int newDpi, int oldDpi)
		{
			_dpiFonts ??= new Dictionary<int, Font> { { oldDpi, WithSize (font, font.Size) } };
			if (_dpiFonts.TryGetValue (newDpi, out Font scaled))
				return scaled;
			float factor = (float) newDpi / oldDpi;
			scaled = WithSize (font, font.Size * factor);
			_dpiFonts.Add (newDpi, scaled);
			return scaled;
		}

		internal static Font WithSize (Font font, float size)
			=> new Font (font.FontFamily, size, font.Style, font.Unit, font.GdiCharSet, font.GdiVerticalFont);

		/// <summary>Forget the fonts kept per DPI. They are not disposed: the one in use may be
		/// held by what was laid out with it (a text box's document).</summary>
		internal void ClearDpiFonts ()
		{
			_dpiFonts = null;
		}

		/// <summary>Replace the control's own font with its scaled one (a DPI change), and say so
		/// unless told not to.</summary>
		internal void SetScaledFont (Font scaledFont, bool raiseOnFontChangedEvent = true)
		{
			font = scaledFont;
			if (!raiseOnFontChangedEvent)
				return;
			SuspendLayout ();
			try {
				OnFontChanged (EventArgs.Empty);
			} finally {
				ResumeLayout (false);
			}
			parent?.PerformLayout (this, "Font");
		}

		/// <summary>The font, and the DPI it is for: the scaled one, the control's own, or the
		/// parent's.</summary>
		internal Font GetCurrentFontAndDpi (out int fontDpi)
		{
			fontDpi = DeviceDpiInternal;
			if (_scaledControlFont != null)
				return _scaledControlFont;
			if (font != null)
				return font;
			if (parent != null) {
				Font f = parent.GetCurrentFontAndDpi (out fontDpi);
				if (f != null)
					return f;
			}
			return DefaultFont;
		}

		/// <summary>
		/// The window exists: it takes the DPI of the display it is on, and a control created at a DPI
		/// other than the one its font is for takes the font at that DPI (.NET's HandleHighDpi).
		/// </summary>
		internal void HandleHighDpi ()
		{
			if (!ScaleHelper.IsThreadPerMonitorV2Aware || !IsHandleCreated)
				return;
			int old = DeviceDpiInternal;
			Font localFont = GetCurrentFontAndDpi (out int fontDpi);
			DeviceDpiInternal = XplatUIWebGpu.GetInstance ().GetDpiForWindow (Handle);
			if (old == DeviceDpiInternal)
				return;
			if (fontDpi != DeviceDpiInternal) {
				Font fontForDpi = GetScaledFont (localFont, DeviceDpiInternal, fontDpi);
				ScaledControlFont = fontForDpi;
				if (IsFontSet ())
					SetScaledFont (fontForDpi);
			}
			RescaleConstantsForDpi (old, DeviceDpiInternal);
		}

		/// <summary>WM_DPICHANGED_BEFOREPARENT: the window's DPI changed, before its top-level
		/// window's WM_DPICHANGED.</summary>
		internal void WmDpiChangedBeforeParent (ref Message m)
		{
			OriginalDeviceDpiInternal = DeviceDpiInternal;

			int newDeviceDpi = (short) ((long) m.WParam & 0xFFFF);
			if (newDeviceDpi == 0)
				newDeviceDpi = XplatUIWebGpu.GetInstance ().GetDpiForWindow (Handle);

			if (OriginalDeviceDpiInternal == newDeviceDpi) {
				OnDpiChangedBeforeParent (EventArgs.Empty);
				return;
			}

			Font localFont = GetCurrentFontAndDpi (out int fontDpi);
			DeviceDpiInternal = newDeviceDpi;

			if (fontDpi == DeviceDpiInternal) {
				OnDpiChangedBeforeParent (EventArgs.Empty);
				return;
			}

			// A container that inherits its font and is scaled by its parent only takes the font:
			// the parent's font change scales it. Anything else scales now.
			ContainerControl container = this as ContainerControl;
			bool isLocalFontSet = IsFontSet ();
			ScaledControlFont = GetScaledFont (localFont, DeviceDpiInternal, fontDpi);

			if (isLocalFontSet || container == null || !IsScaledByParent (this)) {
				if (isLocalFontSet) {
					// An inheriting container must not autoscale by its parent's old factor here.
					bool raiseOnFontChangedEvent = container == null || container.AutoScaleMode != AutoScaleMode.Inherit;
					SetScaledFont (ScaledControlFont, raiseOnFontChangedEvent);
				}
				if (container != null)
					container.IsDpiChangeScalingRequired = true;
				RescaleConstantsForDpi (OriginalDeviceDpiInternal, DeviceDpiInternal);
			}

			OnDpiChangedBeforeParent (EventArgs.Empty);
		}

		/// <summary>WM_DPICHANGED_AFTERPARENT.</summary>
		internal void WmDpiChangedAfterParent (ref Message m)
		{
			OnDpiChangedAfterParent (EventArgs.Empty);
		}
	}
}
