// WinForms' ColorDialog in a WPF process, where WinForms cannot reach the platform's colour chooser
// by itself. WPF has no colour dialog to route through, so this goes to the platform directly, by
// head:
//   * Windows (ChooseColor) and macOS (NSColorPanel) are WinForms' own bridges already, left alone;
//   * iOS gets UIColorPickerViewController and the browser an <input type=color>, both of which
//     answer only later (ColorDialog.ShowDialogAsync);
//   * Linux, whose desktop portal has a screen colour picker (Screenshot.PickColor, an eyedropper)
//     but no colour chooser, and Android, which has none, keep the managed dialog.
// A class of its own rather than nested in WindowsFormsHost, so nothing that touches it has to load
// WPF's element tree.

using System;
using SWF = System.Windows.Forms;

namespace System.Windows.Forms.Integration
{
    internal sealed class WpfColorDialogBridge : SWF.IColorDialogBridge
    {
        /// <summary>Installs this bridge on the heads that need it. Idempotent.</summary>
        internal static void Install()
        {
            if (OperatingSystem.IsIOS() || OperatingSystem.IsBrowser())
                SWF.ColorDialog.PlatformBridge ??= new WpfColorDialogBridge();
        }

        // Neither picker can be waited for: a synchronous ShowDialog says so, naming the call that
        // works, as WPF's own dialogs do on these heads.
        public bool? Show(SWF.ColorDialogRequest request)
            => throw new NotSupportedException(
                "This platform's colour picker answers asynchronously; call ColorDialog.ShowDialogAsync.");

        public async System.Threading.Tasks.Task<bool?> ShowAsync(SWF.ColorDialogRequest request)
        {
            System.Drawing.Color start = request.Color;
            int rgb = (start.R << 16) | (start.G << 8) | start.B;
            if (OperatingSystem.IsIOS())
            {
                if (!MS.Internal.Interop.UIKitColorPicker.IsAvailable) return null;
                int chosen = await MS.Internal.Interop.UIKitColorPicker.PickColorAsync(rgb, null);
                if (chosen < 0) return null;
                // The picker has no Cancel: dismissed on the colour it opened with is a cancellation.
                return SWF.MacColorDialogBridge.Accept(request, (byte)(chosen >> 16), (byte)(chosen >> 8), (byte)chosen);
            }
            if (OperatingSystem.IsBrowser())
            {
                string hex = await MS.Internal.Interop.BrowserDialogs.PickColorAsync(ToHex(rgb));
                if (hex == null) return null;
                if (!TryParseHex(hex, out int value)) return false;
                request.Color = System.Drawing.Color.FromArgb((value >> 16) & 0xFF, (value >> 8) & 0xFF, value & 0xFF);
                return true;
            }
            return null;
        }

        internal static string ToHex(int rgb) => "#" + (rgb & 0xFFFFFF).ToString("x6", System.Globalization.CultureInfo.InvariantCulture);

        /// <summary>"#rrggbb" as the browser reports it; anything else (the empty dismissal included)
        /// is no colour.</summary>
        internal static bool TryParseHex(string hex, out int rgb)
        {
            rgb = 0;
            return hex != null && hex.Length == 7 && hex[0] == '#'
                && int.TryParse(hex.AsSpan(1), System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out rgb);
        }
    }
}
