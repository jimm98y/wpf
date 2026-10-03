// The message box Windows itself shows: user32's MessageBoxW, called as .NET's MessageBox.ShowCore
// calls it -- the buttons, icon, default button and options ORed into one style word, MB_HELP for
// the help button, owned by the active window, inside the comctl32 v6 activation context. A plain,
// platform-gated DllImport; nothing outside Windows reaches it.

using System;
using System.Runtime.InteropServices;

namespace System.Windows.Forms
{
    internal sealed class Win32MessageBoxBridge : IMessageBoxBridge
    {
        internal static readonly IMessageBoxBridge Default =
            OperatingSystem.IsWindows () ? new Win32MessageBoxBridge () : null;

        private const int MB_HELP = 0x00004000;

        [DllImport ("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern int MessageBoxW (IntPtr owner, string text, string caption, int type);

        [DllImport ("user32.dll")]
        private static extern IntPtr GetActiveWindow ();

        public bool TryShow (MessageBoxRequest request, out DialogResult result)
        {
            int style = (int) request.Buttons | (int) request.Icon | (int) request.DefaultButton | (int) request.Options;
            if (request.ShowHelp)
                style |= MB_HELP;
            // ServiceNotification and DefaultDesktopOnly boxes have no owner, as in .NET.
            IntPtr owner = (request.Options & (MessageBoxOptions.ServiceNotification | MessageBoxOptions.DefaultDesktopOnly)) != 0
                ? IntPtr.Zero : GetActiveWindow ();
            int answer;
            IntPtr cookie = Win32ThemingScope.Enter ();
            try {
                answer = MessageBoxW (owner, request.Text, request.Caption, style);
            } finally {
                Win32ThemingScope.Leave (cookie);
            }
            // IDOK 1 .. IDNO 7, IDTRYAGAIN 10, IDCONTINUE 11: the DialogResult values are the same numbers.
            result = answer == 0 ? DialogResult.Cancel : (DialogResult) answer;
            return true;
        }
    }

    /// <summary>NSAlert, through the shared CocoaDialogs: macOS's own message box, for a standalone
    /// WinForms app on the Cocoa head. Its buttons are listed trailing to leading, the first the
    /// default, as WPF's MessageBox lists them there.</summary>
    internal sealed class MacMessageBoxBridge : IMessageBoxBridge
    {
        internal static readonly IMessageBoxBridge Default =
            OperatingSystem.IsMacOS () ? new MacMessageBoxBridge () : null;

        public bool TryShow (MessageBoxRequest request, out DialogResult result)
        {
            (string label, DialogResult result) [] buttons = Buttons (request.Buttons);
            // The default button comes first: NSAlert makes its first button the default.
            int first = request.DefaultButton switch {
                MessageBoxDefaultButton.Button2 => 1,
                MessageBoxDefaultButton.Button3 => 2,
                _ => 0,
            };
            if (first >= buttons.Length)
                first = 0;
            var ordered = new (string label, DialogResult result) [buttons.Length];
            ordered [0] = buttons [first];
            for (int i = 0, k = 1; i < buttons.Length; i++)
                if (i != first)
                    ordered [k++] = buttons [i];
            // NSAlertStyle: critical for Error/Stop/Hand, warning for Warning/Exclamation, else informational.
            int style = request.Icon switch {
                MessageBoxIcon.Error => 2,
                MessageBoxIcon.Warning => 0,
                _ => 1,
            };
            string [] labels = Array.ConvertAll (ordered, b => b.label);
            int clicked = MS.Internal.Interop.CocoaDialogs.ShowAlert (request.Text ?? "", request.Caption ?? "", labels, style);
            result = ordered [Math.Clamp (clicked, 0, ordered.Length - 1)].result;
            return true;
        }

        internal static (string, DialogResult) [] Buttons (MessageBoxButtons buttons) => buttons switch {
            MessageBoxButtons.OKCancel => new [] { ("OK", DialogResult.OK), ("Cancel", DialogResult.Cancel) },
            MessageBoxButtons.AbortRetryIgnore => new [] { ("Abort", DialogResult.Abort), ("Retry", DialogResult.Retry), ("Ignore", DialogResult.Ignore) },
            MessageBoxButtons.YesNoCancel => new [] { ("Yes", DialogResult.Yes), ("No", DialogResult.No), ("Cancel", DialogResult.Cancel) },
            MessageBoxButtons.YesNo => new [] { ("Yes", DialogResult.Yes), ("No", DialogResult.No) },
            MessageBoxButtons.RetryCancel => new [] { ("Retry", DialogResult.Retry), ("Cancel", DialogResult.Cancel) },
            MessageBoxButtons.CancelTryContinue => new [] { ("Cancel", DialogResult.Cancel), ("Try Again", DialogResult.TryAgain), ("Continue", DialogResult.Continue) },
            _ => new [] { ("OK", DialogResult.OK) },
        };
    }
}
