// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using System.Runtime.InteropServices;
using MS.Utility;
using MS.Internal.Interop;
using MS.Win32;

namespace System.Windows.Interop
{
    internal sealed class HwndKeyboardInputProvider : DispatcherObject, IKeyboardInputProvider, IDisposable
    {
        internal HwndKeyboardInputProvider(HwndSource source)
        {
            _site = InputManager.Current.RegisterInputProvider(this);
            _source = source;

            // Off-Windows there is no Win32 message loop feeding key input to FilterMessage; keys
            // arrive as translated Cocoa events. Subscribe and forward them into the InputManager.
            if (OperatingSystem.IsBrowser())
            {
                MS.Internal.Interop.BrowserWindow.KeyInput += OnBrowserKeyInput;
            }
            else if (OperatingSystem.IsLinux())
            {
                MS.Internal.Interop.Wayland.WaylandInput.KeyInput += OnLinuxKeyInput;
            }
            else if (!OperatingSystem.IsWindows())
            {
                MS.Internal.Interop.CocoaWindow.KeyInput += OnCocoaKeyInput;
            }
        }

        public void Dispose()
        {
            if (OperatingSystem.IsBrowser())
            {
                MS.Internal.Interop.BrowserWindow.KeyInput -= OnBrowserKeyInput;
            }
            else if (OperatingSystem.IsLinux())
            {
                MS.Internal.Interop.Wayland.WaylandInput.KeyInput -= OnLinuxKeyInput;
            }
            else if (!OperatingSystem.IsWindows())
            {
                MS.Internal.Interop.CocoaWindow.KeyInput -= OnCocoaKeyInput;
            }

            _site?.Dispose();
            _site = null;
            _source = null;
        }

        public void OnRootChanged(Visual oldRoot, Visual newRoot)
        {
            if(_active && newRoot != null)
            {
                Keyboard.Focus(null); // internally we will set the focus to the root.
            }
        }

        bool IInputProvider.ProvidesInputForRootVisual(Visual v)
        {
            return _source.RootVisual == v;
        }

        void IInputProvider.NotifyDeactivate()
        {
            _active        = false;
            _partialActive = false;
        }

        bool IKeyboardInputProvider.AcquireFocus(bool checkOnly)
        {
            bool result = false;

            Debug.Assert( null != _source );

            // Off-Windows there is no Win32 focus (GetFocus/SetFocus). Our single Cocoa key window
            // always holds OS focus; WPF's own keyboard-focus tracking is managed, so just report
            // success and let WPF route keyboard input to the focused element.
            if (!OperatingSystem.IsWindows())
            {
                if (!checkOnly)
                {
                    _acquiringFocusOurselves = true;
                    _restoreFocusWindow = IntPtr.Zero;
                    _restoreFocus = null;
                }
                return true;
            }

            try
            {
                // Acquiring focus into this window should clear any pending focus restoration.
                if(!checkOnly)
                {
                    _acquiringFocusOurselves = true;
                    _restoreFocusWindow = IntPtr.Zero;
                    _restoreFocus = null;
                }

                HandleRef thisWindow = new HandleRef(this, _source.Handle);
                IntPtr focus = UnsafeNativeMethods.GetFocus();

                int windowStyle = UnsafeNativeMethods.GetWindowLong(thisWindow, NativeMethods.GWL_EXSTYLE);
                if ((windowStyle & NativeMethods.WS_EX_NOACTIVATE) == NativeMethods.WS_EX_NOACTIVATE || _source.IsInExclusiveMenuMode)
                {
                    // If this window has the WS_EX_NOACTIVATE style, then we
                    // do not set Win32 keyboard focus to this window because
                    // that would actually activate the window. This is
                    // typically for the menu Popup.
                    //
                    // If this window is in "menu mode", then we do not set
                    // Win32 focus to this window because we don't want to
                    // move Win32 focus from where it is.  This is typically
                    // for the main window.
                    //
                    // In either case, the window must be enabled.
                    if(SafeNativeMethods.IsWindowEnabled(thisWindow))
                    {

                        // In fully-trusted AppDomains, the only hard requirement
                        // is that Win32 keyboard focus be on some window owned
                        // by a thread that is attached to our Win32 queue.  This
                        // presumes that the thread's message pump will cooperate
                        // by calling ComponentDispatcher.RaiseThreadMessage.
                        // If so, WPF will be able to route the keyboard events to the
                        // element with WPF keyboard focus, regardless of which
                        // window has Win32 keyboard focus.
                        //
                        // Menus/ComboBoxes use this feature.
                        //
                        // Dev11 is moving more towards cross-process designer
                        // support.  They make sure to call AttachThreadInput so
                        // the the two threads share the same Win32 queue.  In
                        // addition, they repost the keyboard messages to the
                        // main UI process/thread for handling.
                        //
                        // We rely on the behavior of GetFocus to only return a
                        // window handle for windows attached to the calling
                        // thread's queue.
                        //
                        result = focus != IntPtr.Zero;
                    }
                }
                else
                {
                    // This is the normal case.  We want to keep WPF keyboard
                    // focus and Win32 keyboard focus in sync.
                    if(!checkOnly)
                    {
                        // Due to IsInExclusiveMenuMode, it is possible that an
                        // HWND keeps Win32 focus even though WPF has moved
                        // element focus somewhere else.  When the element focus
                        // moves somewhere else, this input provider will get
                        // deactivated.  If element focus is set back to an
                        // element within this provider, the HWND already has
                        // Win32 focus and so will not receive another
                        // WM_SETFOCUS, causing the provider to remain
                        // deactivated.  Now we detect that we already have
                        // Win32 focus but are not activated and treat it the
                        // same as getting focus.
                        if (!_active && focus == _source.Handle)
                        {
                            OnSetFocus(focus);
                        }
                        else
                        {
                            UnsafeNativeMethods.TrySetFocus(thisWindow);

                            // Fetch the HWND with Win32 focus again, to double
                            // check we got it.
                            focus = UnsafeNativeMethods.GetFocus();
                        }
                    }

                    result = (focus == _source.Handle);
                }
            }
            catch(System.ComponentModel.Win32Exception)
            {
                System.Diagnostics.Debug.WriteLine("HwndMouseInputProvider: AcquireFocus failed!");
            }
            finally
            {
                _acquiringFocusOurselves = false;
            }

            return result;
        }

        internal IntPtr FilterMessage(IntPtr hwnd, WindowMessage message, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            IntPtr result = IntPtr.Zero ;

            // It is possible to be re-entered during disposal.  Just return.
            if(_source is null)
            {
                return result;
            }

            _msgTime = 0;
            try
            {
                _msgTime = SafeNativeMethods.GetMessageTime();
            }
            catch(System.ComponentModel.Win32Exception)
            {
                System.Diagnostics.Debug.WriteLine("HwndKeyboardInputProvider: GetMessageTime failed!");
            }

            switch(message)
            {
                // WM_KEYDOWN is sent when a nonsystem key is pressed.
                // A nonsystem key is a key that is pressed when the ALT key
                // is not pressed.
                // WM_SYSKEYDOWN is sent when a system key is pressed.
                case WindowMessage.WM_SYSKEYDOWN:
                case WindowMessage.WM_KEYDOWN:
                {
                    // If we have a IKeyboardInputSite, then we should have already
                    // called ProcessKeyDown (from TranslateAccelerator)
                    // But there are several paths (our message pump / app's message
                    // pump) where we do (or don't) call through IKeyboardInputSink.
                    // So the best way is to just check here if we already did it.
                    if(_source.IsRepeatedKeyboardMessage(hwnd, (int)message, wParam, lParam))
                    {
                        break;
                    }

                    // We will use the current time before generating KeyDown events so we can filter
                    // the later posted WM_CHAR.
                    int currentTime = 0;
                    try
                    {
                        currentTime = SafeNativeMethods.GetTickCount();
                    }
                    catch(System.ComponentModel.Win32Exception)
                    {
                        System.Diagnostics.Debug.WriteLine("HwndMouseInputProvider: GetTickCount failed!");
                    }

                    // MITIGATION: HANDLED_KEYDOWN_STILL_GENERATES_CHARS
                    // In case a nested message pump is used before we return
                    // from processing this message, we disable processing the
                    // next WM_CHAR message because if the code pumps messages
                    // it should really mark the message as handled.
                    HwndSource._eatCharMessages = true;
                    DispatcherOperation restoreCharMessages = Dispatcher.BeginInvoke(DispatcherPriority.Normal, new DispatcherOperationCallback(HwndSource.RestoreCharMessages), null);

                    // Force the Dispatcher to post a new message to service any
                    // pending operations, so that the operation we just posted
                    // is guaranteed to get dispatched after any pending WM_CHAR
                    // messages are dispatched.
                    Dispatcher.CriticalRequestProcessing(true);

                    MSG msg = new MSG(hwnd, (int)message, wParam, lParam, _msgTime, 0, 0);
                    ProcessKeyAction(ref msg, ref handled);

                    if(!handled)
                    {
                        // MITIGATION: HANDLED_KEYDOWN_STILL_GENERATES_CHARS
                        // We did not handle the WM_KEYDOWN, so it is OK to process WM_CHAR messages.
                        // We can also abort the pending restore operation since we don't need it.
                        HwndSource._eatCharMessages = false;
                        restoreCharMessages.Abort();
                    }

                    // System.Console.WriteLine("KEYDOWN(message={0}, wParam={1})={2}", message, wParam, handled);
                }
                break;

                // WM_KEYUP is sent when a nonsystem key is released.
                // A nonsystem key is a key that is pressed when the ALT key
                // is not pressed.
                // WM_SYSKEYUP is sent when a system key is released.
                case WindowMessage.WM_SYSKEYUP:
                case WindowMessage.WM_KEYUP:
                {
                    if(_source.IsRepeatedKeyboardMessage(hwnd, (int)message, wParam, lParam))
                    {
                        break;
                    }

                    MSG msg = new MSG(hwnd, (int)message, wParam, lParam, _msgTime, 0, 0);
                    ProcessKeyAction(ref msg, ref handled);
                    // System.Console.WriteLine("KEYUP  (message={0}, wParam={1})={2}", message, wParam, handled);
                }
                break;

                // WM_UNICHAR (UTF-32) support needs to be implemented
                // case WindowMessage.WM_UNICHAR:
                case WindowMessage.WM_CHAR:
                case WindowMessage.WM_DEADCHAR:
                case WindowMessage.WM_SYSCHAR:
                case WindowMessage.WM_SYSDEADCHAR:
                {
                    if(_source.IsRepeatedKeyboardMessage(hwnd, (int)message, wParam, lParam))
                    {
                        break;
                    }

                    // MITIGATION: HANDLED_KEYDOWN_STILL_GENERATES_CHARS
                    if(HwndSource._eatCharMessages)
                    {
                        break;
                    }

                    ProcessTextInputAction(hwnd, message, wParam, lParam, ref handled);
                    // System.Console.WriteLine("CHAR(message={0}, wParam={1})={2}", message, wParam, handled);
                }
                break;

                case WindowMessage.WM_EXITMENULOOP:
                case WindowMessage.WM_EXITSIZEMOVE:
                {
                    // MITIGATION: KEYBOARD_STATE_OUT_OF_SYNC
                    //
                    // Avalon relies on keeping it's copy of the keyboard
                    // state.  This is for a number of reasons, including that
                    // we need to be able to give this state to worker threads.
                    //
                    // There are a number of cases where Win32 eats the
                    // keyboard messages, and this can cause our keyboard
                    // state to become stale.  Obviously this can happen when
                    // another app is in the foreground, but we handle that
                    // by re-synching our keyboard state when we get focus.
                    //
                    // Other times are when Win32 enters a nested loop.  While
                    // any one could enter a nested loop at any time for any
                    // reason, Win32 is nice enough to let us know when it is
                    // finished with the two common loops: menus and sizing.
                    // We re-sync our keyboard device in response to these.
                    //
                    if(_active)
                    {
                        _partialActive = true;

                        ReportInput(hwnd,
                                    InputMode.Foreground,
                                    _msgTime,
                                    RawKeyboardActions.Activate,
                                    0,
                                    false,
                                    false,
                                    0);
                    }
                }
                break;

                // WM_SETFOCUS is sent immediately after focus is granted.
                // This is our clue that the keyboard is active.
                case WindowMessage.WM_SETFOCUS:
                {
                    OnSetFocus(hwnd);

                    handled = true;
                }
                break;

                // WM_KILLFOCUS is sent immediately before focus is removed.
                // This is our clue that the keyboard is inactive.
                case WindowMessage.WM_KILLFOCUS:
                {
                    if(_active && wParam != _source.Handle )
                    {
                        // Console.WriteLine("WM_KILLFOCUS");

                        if(_source.RestoreFocusMode == RestoreFocusMode.Auto)
                        {
                            // when the window that's acquiring focus (wParam) is
                            // a descendant of our window, remember the immediate
                            // child so that we can restore focus to it.
                            _restoreFocusWindow = GetImmediateChildFor((IntPtr)wParam, _source.Handle);

                            _restoreFocus = null;

                            // If we aren't restoring focus to a child window,
                            // then restore focus to the element that currently
                            // has WPF keyboard focus if it is directly in this
                            // HwndSource.
                            if (_restoreFocusWindow == IntPtr.Zero)
                            {
                                DependencyObject focusedDO = Keyboard.FocusedElement as DependencyObject;
                                if (focusedDO != null)
                                {
                                    HwndSource hwndSource = PresentationSource.CriticalFromVisual(focusedDO) as HwndSource;
                                    if (hwndSource == _source)
                                    {
                                        _restoreFocus = focusedDO as IInputElement;
                                    }
                                }
}
                        }

                        PossiblyDeactivate((IntPtr)wParam);
}

                    handled = true;
                }
                break;

                // WM_UPDATEUISTATE is sent when the user presses ALT, expecting
                // the app to display accelerator keys.  We don't always hear the
                // keystroke - another message loop may handle it.  So report it
                // here.
                case WindowMessage.WM_UPDATEUISTATE:
                {
                    RawUIStateInputReport report =
                        new RawUIStateInputReport(_source,
                                                   InputMode.Foreground,
                                                   _msgTime,
                                                   (RawUIStateActions)NativeMethods.SignedLOWORD((int)wParam),
                                                   (RawUIStateTargets)NativeMethods.SignedHIWORD((int)wParam));

                    _site.ReportInput(report);

                    handled = true;
                }
                break;
            }

            if (handled && EventTrace.IsEnabled(EventTrace.Keyword.KeywordInput | EventTrace.Keyword.KeywordPerf, EventTrace.Level.Info))
            {
                EventTrace.EventProvider.TraceEvent(EventTrace.Event.WClientInputMessage,
                                                    EventTrace.Keyword.KeywordInput | EventTrace.Keyword.KeywordPerf, EventTrace.Level.Info,
                                                     Dispatcher.GetHashCode(),
                                                     hwnd.ToInt64(),
                                                     message,
                                                     (int)wParam,
                                                     (int)lParam);
            }

            return result;
        }

        private void OnSetFocus(IntPtr hwnd)
        {
            // Normally we get WM_SETFOCUS only when _active is false.
            //  We have observed FatalExecutionEngineError when running stress when _active is true:
            //  1. Window contains a WindowsFormsHost, which contains a WF.TextBox that has focus
            //  2. User types Alt-Tab to switch to another app
            //  3. User types Alt-Tab again to return to this window
            // The ALT key sets _active to true, as we are processing keyboard input,
            // even though focus is in another window (the WF.TextBox).  But Alt-Tab
            // sends focus to another app, and we don't get any messages (the WF.TextBox
            // gets WM_KILLFOCUS, but doesn't tell us about it).   Thus when focus
            // returns after the second Alt-Tab, _active is still true.
            //
            // We need to run the focus restoration logic in this case. To make that
            // happen, we set _active to false here.  This leaves _active in the
            // state we want, even if the code herein encounters errors/exceptions.
            // There may be other cases where _active is true here (we don't know of
            // any, but we cannot rule them out), but we believe that the code won't
            // do any harm.
            _active = false;

            if (!_active)
            {
                // There is a chance that external code called during the focus
                // changes below will dispose our window, causing _source to get
                // cleared.  We actually saw this in XDesProc (the Blend XAML
                // designer process) in 4.5 Beta, but never tracked down the culprit.
                // To be safe, we cache the member variable in a local variable
                // for use within this method.
                HwndSource thisSource = _source;

                // Console.WriteLine("WM_SETFOCUS");

                ReportInput(hwnd,
                            InputMode.Foreground,
                            _msgTime,
                            RawKeyboardActions.Activate,
                            0,
                            false,
                            false,
                            0);

                // MITIGATION: KEYBOARD_STATE_OUT_OF_SYNC
                //
                // This is how we deal with the fact that Win32 sometimes sends
                // us a WM_SETFOCUS message BEFORE it has updated it's internal
                // internal keyboard state information.  When we get the
                // WM_SETFOCUS message, we activate the keyboard with the
                // keyboard state (even though it could be wrong).  Then when
                // we get the first "real" keyboard input event, we activate
                // the keyboard again, since Win32 will have updated the
                // keyboard state correctly by then.
                //
                _partialActive = true;

                if (!_acquiringFocusOurselves && thisSource.RestoreFocusMode == RestoreFocusMode.Auto)
                {
                    // Restore the keyboard focus to the child window or element that had
                    // the focus before we last lost Win32 focus.  If nothing
                    // had focus before, set it to null.
                    if (_restoreFocusWindow != IntPtr.Zero)
                    {
                        IntPtr hwndRestoreFocus = _restoreFocusWindow;
                        _restoreFocusWindow = IntPtr.Zero;

                        UnsafeNativeMethods.TrySetFocus(new HandleRef(this, hwndRestoreFocus), ref hwndRestoreFocus);
                    }
                    else
                    {
                        DependencyObject restoreFocusDO = _restoreFocus as DependencyObject;
                        _restoreFocus = null;

                        if (restoreFocusDO != null)
                        {
                            // Only restore focus to an element if that
                            // element still belongs to this HWND.
                            HwndSource hwndSource = PresentationSource.CriticalFromVisual(restoreFocusDO) as HwndSource;
                            if (hwndSource != thisSource)
                            {
                                restoreFocusDO = null;
                            }
                        }

                        // Try to restore focus to the last element that had focus.  Note
                        // that if restoreFocusDO is null, we will internally set focus
                        // to the root element.
                        Keyboard.Focus(restoreFocusDO as IInputElement);

                        // Lots of things can happen when setting focus to an element,
                        // including that element may set focus somewhere else, possibly
                        // even into another HWND.  However, if Win32 focus remains on
                        // this window, we do not allow the focused element to be in
                        // a different window.
                        IntPtr focus = UnsafeNativeMethods.GetFocus();
                        if (focus == thisSource.Handle)
                        {
                            restoreFocusDO = (DependencyObject)Keyboard.FocusedElement;
                            if (restoreFocusDO != null)
                            {
                                HwndSource hwndSource = PresentationSource.CriticalFromVisual(restoreFocusDO) as HwndSource;
                                if (hwndSource != thisSource)
                                {
                                    Keyboard.ClearFocus();
                                }
                            }
                        }
                    }
                }
            }
        }

        internal void ProcessKeyAction(ref MSG msg, ref bool handled)
        {
            // Remember the last message
            MSG previousMSG = ComponentDispatcher.UnsecureCurrentKeyboardMessage;
            ComponentDispatcher.UnsecureCurrentKeyboardMessage = msg;

            try
            {
                int virtualKey = GetVirtualKey(msg.wParam, msg.lParam);
                int scanCode = GetScanCode(msg.wParam, msg.lParam);
                bool isExtendedKey = IsExtendedKey(msg.lParam);
                bool isSystemKey = (((WindowMessage)msg.message == WindowMessage.WM_SYSKEYDOWN) || ((WindowMessage)msg.message == WindowMessage.WM_SYSKEYUP));
                RawKeyboardActions action = GetKeyUpKeyDown((WindowMessage)msg.message);

                // Console.WriteLine("WM_KEYDOWN: " + virtualKey + "," + scanCode);
                handled = ReportInput(msg.hwnd,
                                      InputMode.Foreground,
                                      _msgTime,
                                      action,
                                      scanCode,
                                      isExtendedKey,
                                      isSystemKey,
                                      virtualKey);
            }
            finally
            {
                // Restore the last message
                ComponentDispatcher.UnsecureCurrentKeyboardMessage = previousMSG;
            }
        }

        internal void ProcessTextInputAction(IntPtr hwnd, WindowMessage msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            char charcode = (char)wParam;
            bool isDeadChar = ((msg == WindowMessage.WM_DEADCHAR) || (msg == WindowMessage.WM_SYSDEADCHAR));
            bool isSystemChar = ((msg == WindowMessage.WM_SYSCHAR) || (msg == WindowMessage.WM_SYSDEADCHAR));
            bool isControlChar = false;

            // If the control is pressed but Alt is not, the char is control char.
            try
            {
                if (((UnsafeNativeMethods.GetKeyState(NativeMethods.VK_CONTROL) & 0x8000) != 0) &&
                    ((UnsafeNativeMethods.GetKeyState(NativeMethods.VK_MENU) & 0x8000) == 0) &&
                    Char.IsControl(charcode))
                {
                    isControlChar = true;
                }
            }
            catch(System.ComponentModel.Win32Exception)
            {
                System.Diagnostics.Debug.WriteLine("HwndMouseInputProvider: GetKeyState failed!");
            }

            RawTextInputReport report = new RawTextInputReport(_source,
                                                               InputMode.Foreground,
                                                               _msgTime,
                                                               isDeadChar,
                                                               isSystemChar,
                                                               isControlChar,
                                                               charcode);

            handled = _site.ReportInput(report);
        }

        internal static int GetVirtualKey(IntPtr wParam, IntPtr lParam)
        {
            int virtualKey = NativeMethods.IntPtrToInt32( wParam);
            int scanCode = 0;
            int keyData = NativeMethods.IntPtrToInt32(lParam);

            // Find the left/right instance SHIFT keys.
            if(virtualKey == NativeMethods.VK_SHIFT)
            {
                scanCode = (keyData & 0xFF0000) >> 16;
                try
                {
                    virtualKey = SafeNativeMethods.MapVirtualKey(scanCode, 3);
                    if(virtualKey == 0)
                    {
                        virtualKey = NativeMethods.VK_LSHIFT;
                    }
                }
                catch(System.ComponentModel.Win32Exception)
                {
                    System.Diagnostics.Debug.WriteLine("HwndMouseInputProvider: MapVirtualKey failed!");

                    virtualKey = NativeMethods.VK_LSHIFT;
                }
            }

            // Find the left/right instance ALT keys.
            if(virtualKey == NativeMethods.VK_MENU)
            {
                bool right = ((keyData & 0x1000000) >> 24) != 0;

                if(right)
                {
                    virtualKey = NativeMethods.VK_RMENU;
                }
                else
                {
                    virtualKey = NativeMethods.VK_LMENU;
                }
            }

            // Find the left/right instance CONTROL keys.
            if(virtualKey == NativeMethods.VK_CONTROL)
            {
                bool right = ((keyData & 0x1000000) >> 24) != 0;

                if(right)
                {
                    virtualKey = NativeMethods.VK_RCONTROL;
                }
                else
                {
                    virtualKey = NativeMethods.VK_LCONTROL;
                }
            }

            return virtualKey;
        }

        internal static int GetScanCode(IntPtr wParam, IntPtr lParam)
        {
            int keyData = NativeMethods.IntPtrToInt32(lParam);

            int scanCode = (keyData & 0xFF0000) >> 16;
            if(scanCode == 0)
            {
                try
                {
                    int virtualKey = GetVirtualKey(wParam, lParam);
                    scanCode = SafeNativeMethods.MapVirtualKey(virtualKey, 0);
                }
                catch(System.ComponentModel.Win32Exception)
                {
                    System.Diagnostics.Debug.WriteLine("HwndMouseInputProvider: MapVirtualKey failed!");
                }
            }

            return scanCode;
        }

        internal static bool IsExtendedKey(IntPtr lParam)
        {
            int keyData = NativeMethods.IntPtrToInt32(lParam);
            return ((keyData & 0x01000000) != 0) ? true : false;
        }

        ///<summary>
        ///     Returns the set of modifier keys currently pressed as determined by calling to Win32
        ///</summary>
        internal static ModifierKeys GetSystemModifierKeys()
        {
            ModifierKeys modifierKeys = ModifierKeys.None;

            short keyState = UnsafeNativeMethods.GetKeyState(NativeMethods.VK_SHIFT);
            if((keyState & 0x8000) == 0x8000)
            {
                modifierKeys |= ModifierKeys.Shift;
            }

            keyState = UnsafeNativeMethods.GetKeyState(NativeMethods.VK_CONTROL);
            if((keyState & 0x8000) == 0x8000)
            {
                modifierKeys |= ModifierKeys.Control;
            }

            keyState = UnsafeNativeMethods.GetKeyState(NativeMethods.VK_MENU);
            if((keyState & 0x8000) == 0x8000)
            {
                modifierKeys |= ModifierKeys.Alt;
            }

            return modifierKeys;
        }

        private RawKeyboardActions GetKeyUpKeyDown(WindowMessage msg)
        {
            if(  msg == WindowMessage.WM_KEYDOWN || msg == WindowMessage.WM_SYSKEYDOWN )
                return RawKeyboardActions.KeyDown;
            if(  msg == WindowMessage.WM_KEYUP || msg == WindowMessage.WM_SYSKEYUP )
                return RawKeyboardActions.KeyUp;
            throw new ArgumentException(SR.OnlyAcceptsKeyMessages);
        }

        private void PossiblyDeactivate(IntPtr hwndFocus)
        {
            Debug.Assert( null != _source );

            // We are now longer active ourselves, but it is possible that the
            // window the keyboard is going to intereact with is in the same
            // Dispatcher as ourselves.  If so, we don't want to deactivate the
            // keyboard input stream because the other window hasn't activated
            // it yet, and it may result in the input stream "flickering" between
            // active/inactive/active.  This is ugly, so we try to supress the
            // uneccesary transitions.
            //
            bool deactivate = !IsOurWindow(hwndFocus);

            // This window itself should not be active anymore.
            _active = false;

            // Only deactivate the keyboard input stream if needed.
            if(deactivate)
            {
                ReportInput(_source.Handle,
                            InputMode.Foreground,
                            _msgTime,
                            RawKeyboardActions.Deactivate,
                            0,
                            false,
                            false,
                            0);
            }
        }

        private bool IsOurWindow(IntPtr hwnd)
        {
            bool isOurWindow = false;

            Debug.Assert( null != _source );

            if(hwnd != IntPtr.Zero)
            {
                HwndSource hwndSource = HwndSource.CriticalFromHwnd(hwnd);
                if(hwndSource != null)
                {
                    if(hwndSource.Dispatcher == _source.Dispatcher)
                    {
                        // The window has the same dispatcher, must be ours.
                        isOurWindow = true;
                    }
                    else
                    {
                        // The window has a different dispatcher, must not be ours.
                        isOurWindow = false;
                    }
                }
                else
                {
                    // The window is non-Avalon.
                    // Such windows are never ours.
                    isOurWindow = false;
                }
            }
            else
            {
                // This is not even a window.
                isOurWindow = false;
            }

            return isOurWindow;
        }

        // return the immediate child (if any) of hwndRoot that governs the
        // given hwnd.  If hwnd is not a descendant of hwndRoot, return 0.
        private IntPtr GetImmediateChildFor(IntPtr hwnd, IntPtr hwndRoot)
        {
            while (hwnd != IntPtr.Zero)
            {
                // We only care to restore focus to child windows. Notice that WS_POPUP
                // windows also have parents but we do not want to track those here.

                int windowStyle = UnsafeNativeMethods.GetWindowLong(new HandleRef(this,hwnd), NativeMethods.GWL_STYLE);
                if((windowStyle & NativeMethods.WS_CHILD) == 0)
                {
                    break;
                }

                IntPtr hwndParent = UnsafeNativeMethods.GetParent(new HandleRef(this, hwnd));

                if (hwndParent == hwndRoot)
                {
                    return hwnd;
                }

                hwnd = hwndParent;
            }

            return IntPtr.Zero;
        }

        // Off-Windows key path: map a translated Cocoa key event to a Win32 virtual key, report the
        // key-down/up to the InputManager, and (on key-down) deliver typed text as a text-input report.
        // Browser (WebAssembly): DOM keyboard events drained by the dispatcher pump. The
        // physical key (KeyboardEvent.code) maps to a virtual key for WPF key events; the
        // logical key (KeyboardEvent.key) supplies typed text, so layouts/dead keys are
        // whatever the browser already resolved.
        private void OnBrowserKeyInput(MS.Internal.Interop.BrowserWindow.BrowserKeyMessage msg)
        {
            if (_source == null || _site == null || _source.IsDisposed) return;
            if (msg.Window != _source.Handle) return;

            int virtualKey = MapDomCodeToVirtualKey(msg.Code);
            if (virtualKey != 0)
            {
                ReportMacKey(msg.IsDown ? RawKeyboardActions.KeyDown : RawKeyboardActions.KeyUp, virtualKey, msg.TimestampMs);
            }

            // Printable text: KeyboardEvent.key is the composed character ("a", "A", "é"), or a
            // multi-char name ("Enter", "Shift") for non-printable keys. Suppress under Ctrl/Meta
            // shortcuts, mirroring the Cocoa Command/Control rule.
            if (msg.IsDown && !msg.Ctrl && !msg.Meta && msg.Key != null && msg.Key.Length == 1)
            {
                char c = msg.Key[0];
                if (c >= ' ' && c != '\x7f')
                {
                    ReportMacText(c, msg.TimestampMs);
                }
            }
        }

        // Maps a DOM KeyboardEvent.code (physical key) to a Win32 virtual-key code.
        private static int MapDomCodeToVirtualKey(string code)
        {
            if (string.IsNullOrEmpty(code)) return 0;

            // KeyA..KeyZ / Digit0..Digit9 / F1..F12 handled positionally.
            if (code.Length == 4 && code.StartsWith("Key", StringComparison.Ordinal))
                return code[3];
            if (code.Length == 6 && code.StartsWith("Digit", StringComparison.Ordinal))
                return code[5];
            if (code.Length >= 2 && code[0] == 'F' && int.TryParse(code.AsSpan(1), out int fn) && fn >= 1 && fn <= 12)
                return 0x70 + (fn - 1); // VK_F1..
            if (code.StartsWith("Numpad", StringComparison.Ordinal) && code.Length == 7 && char.IsDigit(code[6]))
                return 0x60 + (code[6] - '0'); // VK_NUMPAD0..

            switch (code)
            {
                case "Enter": case "NumpadEnter": return 0x0D;
                case "Tab": return 0x09;
                case "Space": return 0x20;
                case "Backspace": return 0x08;
                case "Escape": return 0x1B;
                case "Delete": return 0x2E;
                case "Insert": return 0x2D;
                case "Home": return 0x24;
                case "End": return 0x23;
                case "PageUp": return 0x21;
                case "PageDown": return 0x22;
                case "ArrowLeft": return 0x25;
                case "ArrowRight": return 0x27;
                case "ArrowUp": return 0x26;
                case "ArrowDown": return 0x28;
                case "ShiftLeft": case "ShiftRight": return 0x10;
                case "ControlLeft": case "ControlRight": return 0x11;
                case "AltLeft": case "AltRight": return 0x12;
                case "MetaLeft": return 0x5B;
                case "MetaRight": return 0x5C;
                case "CapsLock": return 0x14;
                case "ContextMenu": return 0x5D;
                // OEM punctuation (US layout positions)
                case "Minus": return 0xBD;
                case "Equal": return 0xBB;
                case "BracketLeft": return 0xDB;
                case "BracketRight": return 0xDD;
                case "Backslash": return 0xDC;
                case "Semicolon": return 0xBA;
                case "Quote": return 0xDE;
                case "Comma": return 0xBC;
                case "Period": return 0xBE;
                case "Slash": return 0xBF;
                case "Backquote": return 0xC0;
                case "NumpadAdd": return 0x6B;
                case "NumpadSubtract": return 0x6D;
                case "NumpadMultiply": return 0x6A;
                case "NumpadDivide": return 0x6F;
                case "NumpadDecimal": return 0x6E;
                default: return 0;
            }
        }

        private void OnCocoaKeyInput(MS.Internal.Interop.CocoaWindow.CocoaKeyMessage msg)
        {
            if (_source == null || _site == null || _source.IsDisposed) return;
            if (msg.View != _source.Handle) return;

            int virtualKey = MapMacKeyCodeToVirtualKey(msg.KeyCode);
            if (virtualKey != 0)
            {
                ReportMacKey(msg.IsDown ? RawKeyboardActions.KeyDown : RawKeyboardActions.KeyUp, virtualKey, msg.TimestampMs);
            }

            // Deliver typed characters on key-down, unless a Command/Control shortcut is active or the
            // character is non-printable (control chars, or the private-use codepoints AppKit uses for
            // arrows/function keys). Enter/Tab/Backspace are handled as key events, not text.
            const ulong CommandOrControl = 0x100000 | 0x40000;
            if (msg.IsDown && !string.IsNullOrEmpty(msg.Characters) && (msg.ModifierFlags & CommandOrControl) == 0)
            {
                foreach (char c in msg.Characters)
                {
                    if (c >= ' ' && c != '\x7f' && !(c >= '\uF700' && c <= '\uF8FF'))
                    {
                        ReportMacText(c, msg.TimestampMs);
                    }
                }
            }
        }

        private void OnLinuxKeyInput(MS.Internal.Interop.Wayland.WaylandKeyMessage msg)
        {
            if (_source == null || _site == null || _source.IsDisposed) return;
            if (msg.Surface != _source.Handle) return;

            int virtualKey = MapKeysymToVirtualKey(msg.Keysym);
            if (virtualKey != 0)
            {
                ReportMacKey(msg.IsDown ? RawKeyboardActions.KeyDown : RawKeyboardActions.KeyUp, virtualKey, msg.TimestampMs);
            }

            // Text comes from libxkbcommon, already run through the compose state, so a dead key
            // produces nothing here and the following key produces the composed character (see
            // WaylandInput.ComposeText). Suppress it while Ctrl or Alt is held, where the keystroke
            // is a shortcut rather than typing -- the same rule the Cocoa path applies to Command.
            const MS.Internal.Interop.Wayland.WaylandModifiers shortcutModifiers =
                MS.Internal.Interop.Wayland.WaylandModifiers.Control | MS.Internal.Interop.Wayland.WaylandModifiers.Alt;

            // While an input method is composing, its keystrokes are its own: some forward the keys
            // they are consuming to the client as well, and inserting those alongside what the IME
            // eventually commits types every Japanese word twice.
            bool composing = MS.Internal.Interop.Wayland.WaylandTextInput.IsComposing;

            if (msg.IsDown && !composing && !string.IsNullOrEmpty(msg.Characters) && (msg.Modifiers & shortcutModifiers) == 0)
            {
                foreach (char c in msg.Characters)
                {
                    if (c >= ' ' && c != '\x7f')
                    {
                        ReportMacText(c, msg.TimestampMs);
                    }
                }
            }
        }

        // Maps an XKB keysym to a Win32 virtual-key code, which WPF's KeyInterop turns into a Key.
        // Keysyms rather than scancodes because the keysym already accounts for the user's layout,
        // so AZERTY and Dvorak report the letter the user actually pressed. Returns 0 for keysyms
        // we do not translate.
        private static int MapKeysymToVirtualKey(uint keysym)
        {
            // Latin letters and digits: the keysym IS the ASCII code, and VK_A..VK_Z / VK_0..VK_9
            // are the uppercase ASCII values.
            if (keysym >= 'a' && keysym <= 'z') return (int)(keysym - 'a' + 'A');
            if (keysym >= 'A' && keysym <= 'Z') return (int)keysym;
            if (keysym >= '0' && keysym <= '9') return (int)keysym;

            // Keypad digits (XK_KP_0 = 0xffb0) -> VK_NUMPAD0 = 0x60.
            if (keysym >= 0xffb0 && keysym <= 0xffb9) return (int)(keysym - 0xffb0 + 0x60);
            // Function keys (XK_F1 = 0xffbe) -> VK_F1 = 0x70.
            if (keysym >= 0xffbe && keysym <= 0xffe0) return (int)(keysym - 0xffbe + 0x70);

            switch (keysym)
            {
                case 0xff08: return 0x08;   // BackSpace   -> VK_BACK
                case 0xff09: return 0x09;   // Tab         -> VK_TAB
                case 0xff0d: return 0x0D;   // Return      -> VK_RETURN
                case 0xff8d: return 0x0D;   // KP_Enter    -> VK_RETURN
                case 0xff1b: return 0x1B;   // Escape      -> VK_ESCAPE
                case 0xff13: return 0x13;   // Pause       -> VK_PAUSE
                case 0xff14: return 0x91;   // Scroll_Lock -> VK_SCROLL
                case 0xff50: return 0x24;   // Home        -> VK_HOME
                case 0xff51: return 0x25;   // Left        -> VK_LEFT
                case 0xff52: return 0x26;   // Up          -> VK_UP
                case 0xff53: return 0x27;   // Right       -> VK_RIGHT
                case 0xff54: return 0x28;   // Down        -> VK_DOWN
                case 0xff55: return 0x21;   // Prior/PgUp  -> VK_PRIOR
                case 0xff56: return 0x22;   // Next/PgDn   -> VK_NEXT
                case 0xff57: return 0x23;   // End         -> VK_END
                case 0xff63: return 0x2D;   // Insert      -> VK_INSERT
                case 0xffff: return 0x2E;   // Delete      -> VK_DELETE
                case 0xff67: return 0x5D;   // Menu        -> VK_APPS
                case 0xff7f: return 0x90;   // Num_Lock    -> VK_NUMLOCK
                case 0xffe1: return 0xA0;   // Shift_L     -> VK_LSHIFT
                case 0xffe2: return 0xA1;   // Shift_R     -> VK_RSHIFT
                case 0xffe3: return 0xA2;   // Control_L   -> VK_LCONTROL
                case 0xffe4: return 0xA3;   // Control_R   -> VK_RCONTROL
                case 0xffe9: return 0xA4;   // Alt_L       -> VK_LMENU
                case 0xffea: return 0xA5;   // Alt_R       -> VK_RMENU
                case 0xffe5: return 0x14;   // Caps_Lock   -> VK_CAPITAL
                case 0xffeb: return 0x5B;   // Super_L     -> VK_LWIN
                case 0xffec: return 0x5C;   // Super_R     -> VK_RWIN
                case 0x0020: return 0x20;   // space       -> VK_SPACE
                case 0xffaa: return 0x6A;   // KP_Multiply -> VK_MULTIPLY
                case 0xffab: return 0x6B;   // KP_Add      -> VK_ADD
                case 0xffad: return 0x6D;   // KP_Subtract -> VK_SUBTRACT
                case 0xffae: return 0x6E;   // KP_Decimal  -> VK_DECIMAL
                case 0xffaf: return 0x6F;   // KP_Divide   -> VK_DIVIDE

                // OEM punctuation, at their US-layout virtual keys (which is what WPF's Key enum
                // names them after, e.g. Key.OemComma, regardless of the actual layout).
                case ';': case ':': return 0xBA;   // VK_OEM_1
                case '=': case '+': return 0xBB;   // VK_OEM_PLUS
                case ',': case '<': return 0xBC;   // VK_OEM_COMMA
                case '-': case '_': return 0xBD;   // VK_OEM_MINUS
                case '.': case '>': return 0xBE;   // VK_OEM_PERIOD
                case '/': case '?': return 0xBF;   // VK_OEM_2
                case '`': case '~': return 0xC0;   // VK_OEM_3
                case '[': case '{': return 0xDB;   // VK_OEM_4
                case '\\': case '|': return 0xDC;  // VK_OEM_5
                case ']': case '}': return 0xDD;   // VK_OEM_6
                case '\'': case '"': return 0xDE;  // VK_OEM_7

                default: return 0;
            }
        }

        private void ReportMacKey(RawKeyboardActions action, int virtualKey, int timestamp)
        {
            if (_source == null || _site == null || _source.IsDisposed) return;

            if (!_active || _partialActive)
            {
                action |= RawKeyboardActions.Activate;
                _active = true;
                _partialActive = false;
            }

            System.Windows.Input.Win32KeyboardDevice.TrackMacKey(virtualKey, (action & RawKeyboardActions.KeyDown) != 0);

            RawKeyboardInputReport report = new RawKeyboardInputReport(
                _source, InputMode.Foreground, timestamp, action,
                /*scanCode*/ 0, /*isExtendedKey*/ false, /*isSystemKey*/ false, virtualKey, IntPtr.Zero);

            _site.ReportInput(report);
        }

        private void ReportMacText(char c, int timestamp)
        {
            if (_source == null || _site == null || _source.IsDisposed) return;

            RawTextInputReport report = new RawTextInputReport(
                _source, InputMode.Foreground, timestamp,
                /*isDeadCharacter*/ false, /*isSystemCharacter*/ false, /*isControlCharacter*/ false, c);

            _site.ReportInput(report);
        }

        // Maps a macOS hardware key code (kVK_*) to a Win32 virtual-key code (VK_*), which WPF's
        // KeyInterop turns into a Key. Returns 0 for key codes we don't translate.
        private static int MapMacKeyCodeToVirtualKey(int keyCode)
        {
            switch (keyCode)
            {
                // Letters -> 'A'..'Z' (VK == ASCII uppercase)
                case 0x00: return 'A'; case 0x0B: return 'B'; case 0x08: return 'C'; case 0x02: return 'D';
                case 0x0E: return 'E'; case 0x03: return 'F'; case 0x05: return 'G'; case 0x04: return 'H';
                case 0x22: return 'I'; case 0x26: return 'J'; case 0x28: return 'K'; case 0x25: return 'L';
                case 0x2E: return 'M'; case 0x2D: return 'N'; case 0x1F: return 'O'; case 0x23: return 'P';
                case 0x0C: return 'Q'; case 0x0F: return 'R'; case 0x01: return 'S'; case 0x11: return 'T';
                case 0x20: return 'U'; case 0x09: return 'V'; case 0x0D: return 'W'; case 0x07: return 'X';
                case 0x10: return 'Y'; case 0x06: return 'Z';
                // Top-row digits -> '0'..'9'
                case 0x1D: return '0'; case 0x12: return '1'; case 0x13: return '2'; case 0x14: return '3';
                case 0x15: return '4'; case 0x17: return '5'; case 0x16: return '6'; case 0x1A: return '7';
                case 0x1C: return '8'; case 0x19: return '9';
                // Editing / navigation
                case 0x24: return 0x0D; // Return  -> VK_RETURN
                case 0x30: return 0x09; // Tab     -> VK_TAB
                case 0x31: return 0x20; // Space   -> VK_SPACE
                case 0x33: return 0x08; // Delete  -> VK_BACK (backspace)
                case 0x35: return 0x1B; // Escape  -> VK_ESCAPE
                case 0x75: return 0x2E; // FwdDel  -> VK_DELETE
                case 0x73: return 0x24; // Home    -> VK_HOME
                case 0x77: return 0x23; // End     -> VK_END
                case 0x74: return 0x21; // PageUp  -> VK_PRIOR
                case 0x79: return 0x22; // PageDn  -> VK_NEXT
                case 0x7B: return 0x25; // Left    -> VK_LEFT
                case 0x7C: return 0x27; // Right   -> VK_RIGHT
                case 0x7E: return 0x26; // Up      -> VK_UP
                case 0x7D: return 0x28; // Down    -> VK_DOWN
                // Modifiers
                case 0x38: case 0x3C: return 0x10; // Shift        -> VK_SHIFT
                case 0x3B: case 0x3E: return 0x11; // Control      -> VK_CONTROL
                case 0x3A: case 0x3D: return 0x12; // Option       -> VK_MENU (Alt)
                case 0x37: return 0x5B;            // Command      -> VK_LWIN
                case 0x36: return 0x5C;            // RightCommand -> VK_RWIN
                case 0x39: return 0x14;            // CapsLock     -> VK_CAPITAL
                // Punctuation (OEM keys)
                case 0x2B: return 0xBC; // ,  VK_OEM_COMMA
                case 0x2F: return 0xBE; // .  VK_OEM_PERIOD
                case 0x2C: return 0xBF; // /  VK_OEM_2
                case 0x29: return 0xBA; // ;  VK_OEM_1
                case 0x27: return 0xDE; // '  VK_OEM_7
                case 0x2A: return 0xDC; // \  VK_OEM_5
                case 0x21: return 0xDB; // [  VK_OEM_4
                case 0x1E: return 0xDD; // ]  VK_OEM_6
                case 0x18: return 0xBB; // =  VK_OEM_PLUS
                case 0x1B: return 0xBD; // -  VK_OEM_MINUS
                case 0x32: return 0xC0; // `  VK_OEM_3
                // Function keys
                case 0x7A: return 0x70; case 0x78: return 0x71; case 0x63: return 0x72; case 0x76: return 0x73;
                case 0x60: return 0x74; case 0x61: return 0x75; case 0x62: return 0x76; case 0x64: return 0x77;
                case 0x65: return 0x78; case 0x6D: return 0x79; case 0x67: return 0x7A; case 0x6F: return 0x7B;
                default: return 0;
            }
        }

        private bool ReportInput(
            IntPtr hwnd,
            InputMode mode,
            int timestamp,
            RawKeyboardActions actions,
            int scanCode,
            bool isExtendedKey,
            bool isSystemKey,
            int virtualKey)
        {
            Debug.Assert( null != _source );

            // The first event should also activate the keyboard device.
            if((actions & RawKeyboardActions.Deactivate) == 0)
            {
                if(!_active || _partialActive)
                {
                    try
                    {
                        // Include the activation action.
                        actions |= RawKeyboardActions.Activate;

                        // Remember that we are active.
                        _active = true;
                        _partialActive = false;
                    }
                    catch(System.ComponentModel.Win32Exception)
                    {
                        System.Diagnostics.Debug.WriteLine("HwndMouseInputProvider: GetKeyboardState failed!");

                        // We'll go ahead and report the input, but we'll try to "activate" next time.
                    }
                }
            }

            // Get the extra information sent along with the message.
            IntPtr extraInformation = IntPtr.Zero;
            try
            {
                extraInformation = UnsafeNativeMethods.GetMessageExtraInfo();
            }
            catch(System.ComponentModel.Win32Exception)
            {
                System.Diagnostics.Debug.WriteLine("HwndMouseInputProvider: GetMessageExtraInfo failed!");
            }

            RawKeyboardInputReport report = new RawKeyboardInputReport(_source,
                                                                       mode,
                                                                       timestamp,
                                                                       actions,
                                                                       scanCode,
                                                                       isExtendedKey,
                                                                       isSystemKey,
                                                                       virtualKey,
                                                                       extraInformation);


            bool handled = _site.ReportInput(report);

            return handled;
        }

        private int  _msgTime;
        private HwndSource _source;
        private InputProviderSite _site;
        private IInputElement _restoreFocus;
        private IntPtr _restoreFocusWindow;
        private bool _active;
        private bool _partialActive;
        private bool _acquiringFocusOurselves;
    }
}

