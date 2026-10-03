// Message boxes, handed to the platform.
//
// Same shape as the file dialog bridge next door. Windows has user32's MessageBox, macOS NSAlert,
// and a WPF host brings WPF's MessageBox (native where its head has one, its own managed box where
// it has none -- Linux, whose desktop portal offers no message box). Only with no bridge at all --
// the headless tests -- does Mono's managed MessageBoxForm run.

using System.Threading.Tasks;

namespace System.Windows.Forms
{
    /// <summary>What a message box is asking, in MessageBox.Show's terms.</summary>
    internal sealed class MessageBoxRequest
    {
        public string Text;
        public string Caption;
        public MessageBoxButtons Buttons;
        public MessageBoxIcon Icon;
        public MessageBoxDefaultButton DefaultButton;
        public MessageBoxOptions Options;
        public bool ShowHelp;
    }

    internal interface IMessageBoxBridge
    {
        /// <summary>Shows the box and waits for the answer; false if this platform cannot show it
        /// synchronously, and then nothing was shown.</summary>
        bool TryShow(MessageBoxRequest request, out DialogResult result);

        /// <summary>The box without waiting: the only form the browser, iOS and Android can serve.
        /// A bridge whose platform can block inherits this, which simply runs the call.</summary>
        Task<DialogResult?> ShowAsync(MessageBoxRequest request)
            => Task.FromResult(TryShow(request, out DialogResult r) ? r : (DialogResult?)null);
    }

    internal partial class XplatUIWebGpu : XplatUIDriver
    {
        /// <summary>The platform's message box, or null when there is none to call. A host that
        /// brings its own still wins, as for the file dialogs.</summary>
        internal static IMessageBoxBridge MessageBoxBridge
        {
            get => s_messageBoxBridge ?? Win32MessageBoxBridge.Default ?? MacMessageBoxBridge.Default;
            set => s_messageBoxBridge = value;
        }

        private static IMessageBoxBridge s_messageBoxBridge;
    }
}
