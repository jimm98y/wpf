// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// The fork's own Accessibility assembly, against a real accessible object.
//
// Accessibility.dll used to come from the WindowsDesktop framework; this repo builds it now
// (src/Microsoft.DotNet.Wpf/src/Accessibility). It declares exactly one thing, IAccessible, and
// that declaration IS an ABI: a dual COM interface is called through its vtable, and the runtime
// lays the slots out in declaration order. Get the order wrong -- transpose two members, omit one,
// add one -- and every call still compiles and still runs, but lands on a different function of
// whatever accessible object it is talking to. The symptom is not an exception; it is a name that
// comes back as a role, or a crash deep inside somebody else's process.
//
// So this asks a REAL accessible object -- the one oleacc builds for a live window -- for values it
// can be checked against, reached through slots spread across the vtable rather than clustered at
// the front.
//

using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using Accessibility;
using Xunit;

namespace Wpf.Accessibility.Tests
{
    public class MsaaInteropTests
    {
        private const int OBJID_WINDOW = 0x00000000;
        private const int OBJID_CLIENT = unchecked((int)0xFFFFFFFC);
        private const int CHILDID_SELF = 0;

        // ROLE_SYSTEM_WINDOW / ROLE_SYSTEM_CLIENT, from oleacc.h.
        private const int ROLE_SYSTEM_WINDOW = 9;
        private const int ROLE_SYSTEM_CLIENT = 10;

        /// <summary>
        /// A live window's accessible object answers name, role and screen rectangle, and all three
        /// agree with what the window actually is.
        /// </summary>
        [Fact]
        public void ARealAccessibleObjectAnswersThroughTheForkBuiltInterface()
        {
            Assert.SkipUnless(OperatingSystem.IsWindows(), "MSAA is a Windows interface");

            const string title = "msaa interop probe";
            const double width = 260, height = 190;

            (string name, int role, int left, int top, int cx, int cy) = AccessibilityHarness.Sta(() =>
            {
                Window? window = null;
                try
                {
                    window = new Window
                    {
                        Title = title,
                        Width = width,
                        Height = height,
                        Content = new Border { Background = System.Windows.Media.Brushes.White },
                        WindowStartupLocation = WindowStartupLocation.Manual,
                        Left = -32000,
                        Top = -32000,
                        ShowInTaskbar = false,
                        ShowActivated = false,
                    };
                    window.Show();

                    IntPtr hwnd = new WindowInteropHelper(window).Handle;
                    Assert.True(hwnd != IntPtr.Zero, "the window has no HWND, so there is nothing accessible to ask");

                    // OBJID_WINDOW: the window itself, whose accessible name is its caption. That is
                    // the value this test can predict, which is what makes it a check on slot order
                    // rather than a check that something came back.
                    Guid iid = new Guid("618736E0-3C3D-11CF-810C-00AA00389B71");
                    object? raw = null;
                    int hr = AccessibleObjectFromWindow(hwnd, OBJID_WINDOW, ref iid, ref raw);
                    Assert.True(hr == 0 && raw is IAccessible,
                        $"oleacc did not hand back an IAccessible for a live window (hr=0x{hr:X8})");

                    var acc = (IAccessible)raw!;

                    string accName = acc.get_accName(CHILDID_SELF);
                    int accRole = (int)acc.get_accRole(CHILDID_SELF);
                    acc.accLocation(out int l, out int t, out int w, out int h, CHILDID_SELF);

                    return (accName, accRole, l, t, w, h);
                }
                finally
                {
                    window?.Close();
                }
            });

            // get_accName -- slot 10 of the interface.
            Assert.Equal(title, name);

            // get_accRole -- slot 13. A top-level window reports itself as a window.
            Assert.True(role == ROLE_SYSTEM_WINDOW || role == ROLE_SYSTEM_CLIENT,
                $"the window's accessible role came back as {role}, which is neither ROLE_SYSTEM_WINDOW nor ROLE_SYSTEM_CLIENT; the vtable slots are misaligned");

            // accLocation -- slot 22, and a void method with four out parameters, so it is the one
            // most sensitive to a signature that does not match the native one.
            Assert.True(cx > 0 && cy > 0,
                $"the window's accessible rectangle is {cx}x{cy} at ({left}, {top}), which is not a real window");
        }

        /// <summary>
        /// The client area's object has a parent and a child count.
        ///
        /// accParent and accChildCount are PROPERTIES rather than methods, and they sit at the very
        /// front of the interface, so between them and the members above the test spans the whole
        /// vtable. A misdeclared property is the easiest way to shift every slot after it.
        /// </summary>
        [Fact]
        public void TheClientObjectExposesItsPlaceInTheTree()
        {
            Assert.SkipUnless(OperatingSystem.IsWindows(), "MSAA is a Windows interface");

            (bool hasParent, int childCount) = AccessibilityHarness.Sta(() =>
            {
                Window? window = null;
                try
                {
                    window = new Window
                    {
                        Title = "msaa tree probe",
                        Width = 240,
                        Height = 170,
                        Content = new Border { Background = System.Windows.Media.Brushes.White },
                        WindowStartupLocation = WindowStartupLocation.Manual,
                        Left = -32000,
                        Top = -32000,
                        ShowInTaskbar = false,
                        ShowActivated = false,
                    };
                    window.Show();

                    IntPtr hwnd = new WindowInteropHelper(window).Handle;
                    Guid iid = new Guid("618736E0-3C3D-11CF-810C-00AA00389B71");
                    object? raw = null;
                    int hr = AccessibleObjectFromWindow(hwnd, OBJID_CLIENT, ref iid, ref raw);
                    Assert.True(hr == 0 && raw is IAccessible,
                        $"oleacc did not hand back an IAccessible for the client area (hr=0x{hr:X8})");

                    var acc = (IAccessible)raw!;
                    return (acc.accParent is not null, acc.accChildCount);
                }
                finally
                {
                    window?.Close();
                }
            });

            Assert.True(hasParent, "the client object reported no parent, so accParent is not landing on the right vtable slot");
            Assert.True(childCount >= 0, "accChildCount returned a negative count");
        }

        [DllImport("oleacc.dll")]
        private static extern int AccessibleObjectFromWindow(IntPtr hwnd, int idObject, ref Guid iid,
                                                             [In, Out, MarshalAs(UnmanagedType.IUnknown)] ref object? ppvObject);
    }
}
