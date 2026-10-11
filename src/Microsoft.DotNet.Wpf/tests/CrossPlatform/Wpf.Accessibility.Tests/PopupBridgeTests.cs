// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// The popup's MSAA-to-UIA bridge.
//
// A Popup -- a ContextMenu, a ComboBox drop-down, a ToolTip -- lives in its own HWND, separate from
// the window that owns it. UIAutomationCore cannot work out on its own that the two belong
// together, so WPF forces the connection by asking oleacc for the popup's accessible object the
// moment it opens (Popup.ForceMsaaToUiaBridge). Without it a screen reader can see the menu but
// cannot place it in the application, and menus announce as though they came from nowhere.
//
// This is worth its own test for two reasons. It is the only accessibility code in the product that
// runs on a CONDITION -- it does nothing unless a WinEvent hook is installed, i.e. unless something
// like a screen reader is actually listening -- so it is invisible to any test that does not
// install one. And it is the last thing in WPF's app-facing assemblies that referenced the
// Accessibility interop assembly, which this port does not build; the reference is gone now, and
// this is what proves the behaviour it was there for did not go with it.
//

using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;
using Xunit;

namespace Wpf.Accessibility.Tests
{
    public class PopupBridgeTests
    {
        private const int WM_GETOBJECT = 0x003D;
        private const int OBJID_CLIENT = unchecked((int)0xFFFFFFFC);

        private const uint EVENT_OBJECT_FOCUS = 0x8005;
        private const uint EVENT_OBJECT_STATECHANGE = 0x800A;
        private const uint WINEVENT_OUTOFCONTEXT = 0x0000;

        // Held in a static so the callback is not collected while the hook is installed.
        private static readonly WinEventProc s_winEventProc = (hook, ev, hwnd, idObject, idChild, thread, time) => { };

        /// <summary>
        /// With a listener attached, an opened ContextMenu exposes a provider on its own HWND.
        ///
        /// The WinEvent hook is what arms the code under test: Popup checks
        /// IsWinEventHookInstalled before doing anything, so without one this test would pass
        /// while executing none of the path it is named after.
        /// </summary>
        [Fact]
        public void AnOpenedPopupExposesAProviderToAListeningClient()
        {
            Assert.SkipUnless(OperatingSystem.IsWindows(), "the MSAA to UIA bridge is Windows accessibility infrastructure");

            IntPtr result = AccessibilityHarness.Sta(() =>
            {
                IntPtr hook = SetWinEventHook(EVENT_OBJECT_FOCUS, EVENT_OBJECT_STATECHANGE,
                                              IntPtr.Zero, s_winEventProc, 0, 0, WINEVENT_OUTOFCONTEXT);
                Assert.True(hook != IntPtr.Zero, "could not install a WinEvent hook, so the bridge would never be attempted");

                Window? window = null;
                try
                {
                    var menu = new ContextMenu();
                    menu.Items.Add(new MenuItem { Header = "an item" });

                    var content = new Border
                    {
                        Background = System.Windows.Media.Brushes.White,
                        ContextMenu = menu,
                    };

                    window = new Window
                    {
                        Title = "popup bridge probe",
                        Width = 240,
                        Height = 180,
                        Content = content,
                        // Off-screen: this must not flash a menu in front of whoever is at the machine.
                        WindowStartupLocation = WindowStartupLocation.Manual,
                        Left = -32000,
                        Top = -32000,
                        ShowInTaskbar = false,
                        ShowActivated = false,
                    };
                    window.Show();
                    Pump();

                    menu.PlacementTarget = content;
                    menu.IsOpen = true;
                    Pump();

                    // The menu is in a window of its own; that HWND is the one the bridge exists for.
                    var popupSource = PresentationSource.FromVisual(menu) as HwndSource;
                    Assert.True(popupSource is not null, "the opened ContextMenu has no HWND of its own");

                    IntPtr answer = SendMessage(popupSource!.Handle, WM_GETOBJECT, IntPtr.Zero, (IntPtr)OBJID_CLIENT);

                    menu.IsOpen = false;
                    Pump();
                    return answer;
                }
                finally
                {
                    UnhookWinEvent(hook);
                    window?.Close();
                }
            });

            Assert.True(result != IntPtr.Zero,
                "the opened popup returned no provider for WM_GETOBJECT, so a screen reader cannot read the menu");
        }

        // Drains the dispatcher so the window and the popup are really up before they are asked.
        private static void Pump()
        {
            for (int i = 0; i < 4; i++)
            {
                var frame = new DispatcherFrame();
                Dispatcher.CurrentDispatcher.BeginInvoke(
                    DispatcherPriority.Background, new Action(() => frame.Continue = false));
                Dispatcher.PushFrame(frame);
            }
        }

        private delegate void WinEventProc(IntPtr hook, uint ev, IntPtr hwnd, int idObject, int idChild, uint thread, uint time);

        [DllImport("user32.dll")]
        private static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr module, WinEventProc callback,
                                                     uint process, uint thread, uint flags);

        [DllImport("user32.dll")]
        private static extern bool UnhookWinEvent(IntPtr hook);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
    }
}
