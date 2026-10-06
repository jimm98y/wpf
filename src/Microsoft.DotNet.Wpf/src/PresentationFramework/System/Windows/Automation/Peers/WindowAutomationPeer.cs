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

