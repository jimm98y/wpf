// macOS's own colour chooser for WinForms' ColorDialog: the shared NSColorPanel, run modally through
// CocoaDialogs.ShowColorPanel, for a standalone WinForms app on the Cocoa head and for one hosted in
// WPF there (WPF has no colour dialog of its own to route through).
//
// NSColorPanel has no OK and no Cancel -- a Mac user picks a colour and closes the panel -- so a
// colour that came back changed is OK and an unchanged one is Cancel, which leaves the dialog's
// Color as it was either way. The panel keeps its own swatches (colour lists), so WinForms'
// sixteen custom colours pass through unchanged.

using System;
using System.Drawing;

namespace System.Windows.Forms
{
	internal sealed class MacColorDialogBridge : IColorDialogBridge
	{
		internal static readonly IColorDialogBridge Default =
			OperatingSystem.IsMacOS () ? new MacColorDialogBridge () : null;

		public bool? Show (ColorDialogRequest request)
		{
			byte r = request.Color.R, g = request.Color.G, b = request.Color.B;
			try {
				MS.Internal.Interop.CocoaDialogs.ShowColorPanel (ref r, ref g, ref b, showAlpha: false);
			} catch (Exception) {
				return null;   // AppKit unavailable: the managed dialog
			}
			return Accept (request, r, g, b);
		}

		/// <summary>The panel's answer: OK when the colour changed (and it becomes the dialog's),
		/// Cancel when the panel was closed on the colour it opened with.</summary>
		internal static bool Accept (ColorDialogRequest request, byte r, byte g, byte b)
		{
			Color start = request.Color;
			if (start.R == r && start.G == g && start.B == b)
				return false;
			request.Color = Color.FromArgb (r, g, b);
			return true;
		}
	}
}
