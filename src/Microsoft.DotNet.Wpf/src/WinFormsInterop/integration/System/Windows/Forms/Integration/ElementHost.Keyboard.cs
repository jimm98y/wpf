// The keys the hosted WPF tree does not handle go on to WinForms, as with the Windows-only ElementHost.
//
// There the application's message loop pre-processes every key message, the hosted window's included:
// ElementHost.ProcessCmdKey first offers the key to WPF (IKeyboardInputSink.TranslateAccelerator, which
// raises PreviewKeyDown/KeyDown), and only a key WPF leaves unhandled goes on to base.ProcessCmdKey --
// up the parent chain to the form. A WM_CHAR that WPF's text input leaves unhandled becomes
// ProcessDialogChar (mnemonics typed without Alt).
//
// Here the OS delivers the key straight to the hosted window and WPF handles it there; nothing went on
// to WinForms. app deletes the selected canvas element from its configurator control's
// ProcessCmdKey (Keys.Delete, and Ctrl+C / Ctrl+V / Ctrl+X, F2 ... the same way), so Delete did
// nothing. The same order is kept: WPF first, then, for a key WPF leaves unhandled, WinForms'
// ProcessCmdKey from this control up.

using System.Windows.Interop;
using SWI = System.Windows.Input;

namespace System.Windows.Forms.Integration
{
    public partial class ElementHost
    {
        private HwndSource _keysFor;
        private bool _translating;

        /// <summary>As the Windows ElementHost: a key the loop pre-processes for the hosted window goes to
        /// WPF first (IKeyboardInputSink.TranslateAccelerator, which raises PreviewKeyDown/KeyDown and
        /// marks the message seen, so the window does not take it a second time), and on to WinForms
        /// only if WPF leaves it. Without this an open ContextMenu never had its arrow keys: the loop's
        /// message filters ran ProcessDialogKey, which took Up and Down for moving between controls.</summary>
        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (_source != null && !_source.IsDisposed && !_translating && msg.HWnd == _source.Handle && IsKeyMessage(msg.Msg))
            {
                var wpf = new System.Windows.Interop.MSG { hwnd = msg.HWnd, message = msg.Msg, wParam = msg.WParam, lParam = msg.LParam };
                bool handled;
                _translating = true;
                try { handled = ((IKeyboardInputSink)_source).TranslateAccelerator(ref wpf, ModifiersOf(keyData)); }
                finally { _translating = false; }
                if (KeyTrace.On) KeyTrace.Log($"  ElementHost.ProcessCmdKey({keyData}): WPF TranslateAccelerator -> {handled}");
                if (handled) return true;
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        private static bool IsKeyMessage(int m) => m is 0x0100 or 0x0101 or 0x0104 or 0x0105;

        private static SWI.ModifierKeys ModifiersOf(Keys keyData)
        {
            SWI.ModifierKeys m = SWI.ModifierKeys.None;
            if ((keyData & Keys.Shift) != 0) m |= SWI.ModifierKeys.Shift;
            if ((keyData & Keys.Control) != 0) m |= SWI.ModifierKeys.Control;
            if ((keyData & Keys.Alt) != 0) m |= SWI.ModifierKeys.Alt;
            return m;
        }

        /// <summary>Starts following the hosted window's keyboard input (see the file's comment).</summary>
        private void HookKeyboard()
        {
            if (_source == null || ReferenceEquals(_keysFor, _source)) return;
            UnhookKeyboard();
            _keysFor = _source;
            SWI.InputManager.Current.PostProcessInput += OnPostProcessInput;
            if (KeyTrace.On) TraceKeyboardOnce();
        }

        private void UnhookKeyboard()
        {
            if (_keysFor == null) return;
            _keysFor = null;
            try { SWI.InputManager.Current.PostProcessInput -= OnPostProcessInput; } catch (InvalidOperationException) { }
        }

        private void OnPostProcessInput(object sender, SWI.ProcessInputEventArgs e)
        {
            SWI.InputEventArgs input = e.StagingItem.Input;
            // Inside ProcessCmdKey's TranslateAccelerator, which goes on to WinForms itself.
            if (input.Handled || IsDisposed || _keysFor == null || _translating) return;
            try
            {
                if (input.RoutedEvent == SWI.Keyboard.KeyDownEvent && input is SWI.KeyEventArgs ke && ReferenceEquals(ke.InputSource, _keysFor))
                {
                    bool system = ke.Key == SWI.Key.System;
                    int vk = SWI.KeyInterop.VirtualKeyFromKey(system ? ke.SystemKey : ke.Key);
                    if (vk == 0) return;
                    Keys keyData = (Keys)vk | ModifiersNow();
                    var msg = Message.Create(_keysFor.Handle, system ? 0x0104 : 0x0100, (IntPtr)vk, IntPtr.Zero);
                    _translating = true;
                    bool taken;
                    try { taken = ProcessCmdKey(ref msg, keyData); }
                    finally { _translating = false; }
                    if (KeyTrace.On) KeyTrace.Log($"  ElementHost: WPF left {ke.Key} unhandled -> WinForms ProcessCmdKey({keyData}) from {GetType().Name} '{Name}' -> {taken}");
                    if (taken) input.Handled = true;
                }
                else if (input.RoutedEvent == SWI.TextCompositionManager.TextInputEvent && input is SWI.TextCompositionEventArgs te
                         && ReferenceEquals(SWI.Keyboard.PrimaryDevice.ActiveSource, _keysFor))
                {
                    string text = string.IsNullOrEmpty(te.Text) ? te.SystemText : te.Text;
                    if (string.IsNullOrEmpty(text)) return;
                    bool taken = ProcessDialogChar(text[0]);
                    if (KeyTrace.On) KeyTrace.Log($"  ElementHost: WPF left text '{text}' unhandled -> ProcessDialogChar -> {taken}");
                    if (taken) input.Handled = true;
                }
            }
            catch (Exception ex) { Console.Error.WriteLine("ElementHost: key forwarding failed: " + ex.Message); }
        }

        private static Keys ModifiersNow()
        {
            SWI.ModifierKeys m = SWI.Keyboard.Modifiers;
            Keys k = Keys.None;
            if ((m & SWI.ModifierKeys.Shift) != 0) k |= Keys.Shift;
            if ((m & SWI.ModifierKeys.Control) != 0) k |= Keys.Control;
            if ((m & SWI.ModifierKeys.Alt) != 0) k |= Keys.Alt;
            return k;
        }

        // ---- WF_TRACE_KEYS: what WPF does with each key on this thread --------------------------------

        [ThreadStatic] private static bool t_keysTraced;

        private static void TraceKeyboardOnce()
        {
            if (t_keysTraced) return;
            t_keysTraced = true;
            ComponentDispatcher.ThreadPreprocessMessage += (ref System.Windows.Interop.MSG msg, ref bool handled) =>
            {
                if (KeyTrace.IsKey(msg.message))
                    KeyTrace.Log($"wpf ComponentDispatcher.ThreadPreprocessMessage {KeyTrace.Describe(msg.message, msg.wParam, msg.hwnd)} handled={handled} focused={Describe(SWI.Keyboard.FocusedElement)}");
            };
            SWI.InputManager im = SWI.InputManager.Current;
            im.PreProcessInput += (s, e) => TraceInput("pre ", e.StagingItem.Input);
            im.PostProcessInput += (s, e) => TraceInput("post", e.StagingItem.Input);
        }

        private static void TraceInput(string stage, SWI.InputEventArgs input)
        {
            if (input.RoutedEvent == null) return;
            if (input is SWI.KeyEventArgs ke)
            {
                KeyTrace.Log($"wpf {stage} {input.RoutedEvent.Name} {ke.Key}{(ke.Key == SWI.Key.System ? "/" + ke.SystemKey : "")} handled={ke.Handled} source={ke.InputSource?.GetType().Name} root={(ke.InputSource as HwndSource)?.RootVisual?.GetType().Name} focused={Describe(SWI.Keyboard.FocusedElement)} original={Describe(ke.OriginalSource)}");
                return;
            }
            if (input is SWI.TextCompositionEventArgs te)
            {
                KeyTrace.Log($"wpf {stage} {input.RoutedEvent.Name} '{te.Text}{te.SystemText}' handled={te.Handled} focused={Describe(SWI.Keyboard.FocusedElement)}");
                return;
            }
            if (stage != "pre " || input.GetType().Name != "InputReportEventArgs") return;
            const Reflection.BindingFlags BF = Reflection.BindingFlags.Instance | Reflection.BindingFlags.Public | Reflection.BindingFlags.NonPublic;
            object report = input.GetType().GetProperty("Report", BF)?.GetValue(input);
            if (report?.GetType().Name != "RawKeyboardInputReport") return;
            Type rt = report.GetType();
            object Get(string n) => rt.GetProperty(n, BF)?.GetValue(report);
            KeyTrace.Log($"wpf raw keyboard report actions={Get("Actions")} vk={Get("VirtualKey")} system={Get("IsSystemKey")}");
        }

        private static string Describe(object o)
        {
            if (o == null) return "<none>";
            if (o is System.Windows.FrameworkElement fe && !string.IsNullOrEmpty(fe.Name)) return $"{fe.GetType().FullName} '{fe.Name}'";
            return o.GetType().FullName;
        }
    }
}
