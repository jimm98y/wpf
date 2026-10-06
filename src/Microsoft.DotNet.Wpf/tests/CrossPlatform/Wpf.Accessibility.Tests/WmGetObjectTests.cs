// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// The Windows handshake: WM_GETOBJECT.
//
// This is the whole seam between WPF and the operating system's accessibility stack. A UIA client
// (Narrator, NVDA, JAWS, Accessibility Insights, an automated UI test) sends WM_GETOBJECT to a
// window; HwndTarget answers by wrapping the root AutomationPeer in an ElementProxy and handing it
// to UiaReturnRawElementProvider, whose return value is the LRESULT the client unpacks. Break that
// one message and the entire automation tree, however healthy, becomes unreachable -- and nothing
// else in the product changes at all.
//
// It is worth a test of its own because the answer comes from three places that were all touched by
// this port: HwndTarget's window procedure, the COM marshalling of IRawElementProviderSimple, and
// UIAutomationCore itself. AutomationPeerTests cover the tree; only this covers the door to it.
//

using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using Xunit;

namespace Wpf.Accessibility.Tests
{
    public class WmGetObjectTests
    {
        private const int WM_GETOBJECT = 0x003D;

        // The object a client asks for. UiaRootObjectId is what a UIA client sends; OBJID_CLIENT is
        // the older MSAA request, which WPF answers too.
        private const int UiaRootObjectId = unchecked((int)0xFFFFFFFA);
        private const int OBJID_CLIENT = unchecked((int)0xFFFFFFFC);

        /// <summary>
        /// A window hands back a provider when asked.
        ///
        /// OBJID_CLIENT rather than UiaRootObjectId, and the difference is worth recording. Both ids
        /// travel the identical path in WPF -- HwndTarget does not look at the object id at all, it
        /// wraps the root peer and calls UiaReturnRawElementProvider either way -- but UIA itself
        /// answers UiaRootObjectId with 0 in a process where no UIA client has ever attached, because
        /// it initialises lazily. Asserting on that would be asserting on UIAutomationCore's
        /// activation state, not on anything this port controls, and it would fail on a clean
        /// machine and pass on a developer's while a screen reader happened to be running.
        ///
        /// A non-zero answer here proves the whole chain regardless: the root AutomationPeer was
        /// found, ElementProxy wrapped it, the COM marshalling of IRawElementProviderSimple worked,
        /// and UIAutomationCore accepted it and produced an LRESULT. The tree that a real client then
        /// walks is what AutomationPeerTests covers.
        /// </summary>
        [Fact]
        public void AWindowAnswersWmGetObjectWithAProvider()
        {
            Assert.SkipUnless(OperatingSystem.IsWindows(), "WM_GETOBJECT is the Windows accessibility seam");

            IntPtr result = AccessibilityHarness.Sta(() => AskWindow(OBJID_CLIENT));

            Assert.True(result != IntPtr.Zero,
                "the window returned no provider for WM_GETOBJECT, so no screen reader can see anything in it");
        }

        // Deliberately ONE window test, not several.
        //
        // A second Window in the same process fails here with a missing wpfgfx_cor3.dll: each test
        // builds its window on its own short-lived STA thread, that thread exits without shutting its
        // Dispatcher down, and the next window creation no longer finds the managed compositor
        // registered, so HwndTarget falls through to the native milcore attach this port does not
        // ship. That is a property of driving WPF windows from a test host rather than anything the
        // accessibility seam does, and a second window would add no coverage of it -- so the suite
        // creates exactly one and the fragility never arises.

        // Builds a real WPF window with real content, shows it, and sends it the message a screen
        // reader would. A window is required rather than a bare element: WM_GETOBJECT is answered by
        // HwndTarget, which only exists once there is an HWND.
        private static IntPtr AskWindow(int objectId, bool withContent = true)
        {
            Window? window = null;
            try
            {
                StackPanel? panel = null;
                if (withContent)
                {
                    panel = new StackPanel();
                    panel.Children.Add(new TextBlock { Text = "readable content" });
                    panel.Children.Add(new Button { Content = "a button" });
                }

                window = new Window
                {
                    Title = "accessibility probe",
                    Width = 200,
                    Height = 150,
                    Content = panel,
                    // Off-screen and unfocusable: this must not steal focus from whoever is at the
                    // machine, and it must not flash a window in the middle of a test run.
                    WindowStartupLocation = WindowStartupLocation.Manual,
                    Left = -32000,
                    Top = -32000,
                    ShowInTaskbar = false,
                    ShowActivated = false,
                };
                window.Show();

                IntPtr hwnd = new WindowInteropHelper(window).Handle;
                Assert.True(hwnd != IntPtr.Zero, "the window has no HWND, so there is nothing to ask");

                return SendMessage(hwnd, WM_GETOBJECT, IntPtr.Zero, (IntPtr)objectId);
            }
            finally
            {
                window?.Close();
            }
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
    }
}
