// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Runtime.InteropServices;
using System.Text;
using System.ComponentModel;
using MS.Win32;

namespace System.Windows.Automation.Peers
{
    /// 
    public class WindowAutomationPeer : FrameworkElementAutomationPeer
    {
        ///
        public WindowAutomationPeer(Window owner): base(owner)
        {}
    
        ///
        protected override string GetClassNameCore()
        {
            return "Window";
        }

        ///
        protected override string GetNameCore()
        {
            string name = base.GetNameCore();

            if(name.Length == 0)
            {
                Window window = (Window)Owner;

                if(!window.IsSourceWindowNull)
                {
                    // Off Windows the title lives only in the managed Window -- there is no window
                    // manager to ask, and GetWindowText would be a DllNotFoundException rather than
                    // the Win32Exception this catches. That distinction matters here: this runs from
                    // AutomationPeer.UpdateSubtree during layout, where an unhandled exception takes
                    // the application down the moment an assistive technology builds the tree.
                    if (!OperatingSystem.IsWindows())
                    {
                        name = window.Title;
                    }
                    else
                    {
                        try
                        {
                            StringBuilder sb = new StringBuilder(512);
                            UnsafeNativeMethods.GetWindowText(new HandleRef(null, window.Handle), sb, sb.Capacity);
                            name = sb.ToString();
                        }
                        catch (Win32Exception)
                        {
                            name = window.Title;
                        }
                    }

                    name ??= "";
                }
            }

            return name;
        }

        ///
        protected override AutomationControlType GetAutomationControlTypeCore()
        {
            return AutomationControlType.Window;
        }



        ///
        protected override Rect GetBoundingRectangleCore()
        {
            Window window = (Window)Owner;
            Rect bounds = new Rect(0,0,0,0);

            // GetWindowRect reports the whole frame, decorations included. Off Windows there is no
            // such call, so fall back to the managed route every other peer already uses: the
            // element's rect mapped through the PresentationSource. That is the CLIENT area, which
            // on a Wayland head is the only rectangle a client can know about at all.
            if (!OperatingSystem.IsWindows())
            {
                return window.IsSourceWindowNull ? bounds : base.GetBoundingRectangleCore();
            }

            if(!window.IsSourceWindowNull)
            {
                NativeMethods.RECT rc = new NativeMethods.RECT(0,0,0,0);
                IntPtr windowHandle = window.Handle;
                if(windowHandle != IntPtr.Zero) //it is Zero on a window that was just closed
                {
                    try { SafeNativeMethods.GetWindowRect(new HandleRef(null, windowHandle), ref rc); }
                    catch(Win32Exception) {}
                }        
                bounds = new Rect(rc.left, rc.top, rc.right - rc.left, rc.bottom - rc.top);
            }

            return bounds;
        }

        /// <summary>
        /// The Window pattern, from the Window itself. On Windows UI Automation also reads it from the
        /// window handle; the platforms' accessibility bridges have only the peer to ask.
        /// </summary>
        public override object GetPattern(PatternInterface patternInterface)
        {
            if (patternInterface == PatternInterface.Window) return _windowProvider ??= new WindowPatternProvider((Window)Owner);
            return base.GetPattern(patternInterface);
        }

        private WindowPatternProvider _windowProvider;

        private sealed class WindowPatternProvider : System.Windows.Automation.Provider.IWindowProvider
        {
            private readonly Window _window;
            public WindowPatternProvider(Window window) => _window = window;

            public void SetVisualState(WindowVisualState state)
            {
                if ((state == WindowVisualState.Maximized && !Maximizable) || (state == WindowVisualState.Minimized && !Minimizable))
                    throw new InvalidOperationException();      // as UI Automation answers a state the window does not allow
                _window.WindowState = state switch
                {
                    WindowVisualState.Maximized => WindowState.Maximized,
                    WindowVisualState.Minimized => WindowState.Minimized,
                    _ => WindowState.Normal,
                };
            }

            public void Close() => _window.Close();
            public bool WaitForInputIdle(int milliseconds) => true;
            public bool Maximizable => _window.ResizeMode is ResizeMode.CanResize or ResizeMode.CanResizeWithGrip && _window.WindowStyle != WindowStyle.ToolWindow;
            public bool Minimizable => _window.ResizeMode != ResizeMode.NoResize && _window.WindowStyle != WindowStyle.ToolWindow;
            public bool IsModal => _window.IsShowingAsDialog;
            public WindowVisualState VisualState => _window.WindowState switch
            {
                WindowState.Maximized => WindowVisualState.Maximized,
                WindowState.Minimized => WindowVisualState.Minimized,
                _ => WindowVisualState.Normal,
            };
            public WindowInteractionState InteractionState => _window.IsEnabled ? WindowInteractionState.ReadyForUserInteraction : WindowInteractionState.BlockedByModalWindow;
            public bool IsTopmost => _window.Topmost;
        }

        protected override bool IsDialogCore()
        {
            Window window = (Window)Owner;
            if (MS.Internal.Helper.IsDefaultValue(AutomationProperties.IsDialogProperty, window))
            {
                return window.IsShowingAsDialog;
            }
            else
            {
                return AutomationProperties.GetIsDialog(window);
            }
        }
    }
}

