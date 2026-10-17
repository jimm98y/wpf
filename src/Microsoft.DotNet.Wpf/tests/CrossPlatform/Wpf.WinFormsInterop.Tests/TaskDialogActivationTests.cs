// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// An app's own [DllImport("Comctl32.dll")] TaskDialogIndirect (WindowsAPICodePack's TaskDialog, which
// app shows on the way to Runtime) threw EntryPointNotFoundException: it bound to comctl32 v5,
// which has no task dialogs. Under .NET, Application.EnableVisualStyles makes an activation context
// naming Common-Controls 6.0 and WinForms activates it around the work it does (ThemingScope), so a
// comctl32 import made from there binds to v6 -- and the runtime then keeps that binding for the
// name. An app started by dotnet.exe, or by an apphost whose manifest names no Common-Controls, has
// no other way to get v6. Here the message loop and handle creation run under that context.
//

using System;
using System.Runtime.InteropServices;
using System.Threading;
using Xunit;
using SWF = System.Windows.Forms;

namespace Wpf.WinFormsInterop.Tests
{
    public class TaskDialogActivationTests
    {
        // The spelling WindowsAPICodePack uses; the runtime keeps one binding per spelling.
        [DllImport("Comctl32.dll", EntryPoint = "TaskDialogIndirect")]
        private static extern int TaskDialogIndirectNull(IntPtr config, IntPtr button, IntPtr radio, IntPtr verify);

        [DllImport("comctl32.dll", EntryPoint = "TaskDialogIndirect")]
        private static extern int TaskDialogIndirect(ref TASKDIALOGCONFIG config, out int button, IntPtr radio, IntPtr verify);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr GetModuleHandleW(string name);

        [DllImport("kernel32.dll", CharSet = CharSet.Ansi)]
        private static extern IntPtr GetProcAddress(IntPtr module, string name);

        [DllImport("user32.dll")]
        private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);

        [DllImport("user32.dll")]
        private static extern bool PostMessageW(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

        private const int E_INVALIDARG = unchecked((int)0x80070057);

        private delegate int TaskDialogCallback(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam, IntPtr refData);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode, Pack = 4)]
        private struct TASKDIALOGCONFIG
        {
            public uint cbSize;
            public IntPtr hwndParent;
            public IntPtr hInstance;
            public uint dwFlags;
            public uint dwCommonButtons;
            public IntPtr pszWindowTitle;
            public IntPtr hMainIcon;
            public IntPtr pszMainInstruction;
            public IntPtr pszContent;
            public uint cButtons;
            public IntPtr pButtons;
            public int nDefaultButton;
            public uint cRadioButtons;
            public IntPtr pRadioButtons;
            public int nDefaultRadioButton;
            public IntPtr pszVerificationText;
            public IntPtr pszExpandedInformation;
            public IntPtr pszExpandedControlText;
            public IntPtr pszCollapsedControlText;
            public IntPtr hFooterIcon;
            public IntPtr pszFooter;
            public IntPtr pfCallback;
            public IntPtr lpCallbackData;
            public uint cxWidth;
        }

        private sealed class QuietForm : SWF.Form
        {
            protected override bool ShowWithoutActivation => true;
        }

        /// <summary>Runs <paramref name="body"/> from inside Application.Run on an STA thread, with a form
        /// off every monitor (the loop ends when its last window does).</summary>
        private static void InLoop(Action body)
        {
            Exception? failure = null;
            bool ran = false;
            var t = new Thread(() =>
            {
                try
                {
                    SWF.Application.EnableVisualStyles();
                    using var form = new QuietForm
                    {
                        Text = "TaskDialogActivationTest", StartPosition = SWF.FormStartPosition.Manual,
                        Location = new System.Drawing.Point(8000, 80), Size = new System.Drawing.Size(200, 120),
                        ShowInTaskbar = false, FormBorderStyle = SWF.FormBorderStyle.None,
                    };
                    form.Shown += (s, e) => form.BeginInvoke(new Action(() =>
                    {
                        ran = true;
                        try { body(); }
                        catch (Exception ex) { failure = ex; }
                        finally { form.Close(); }
                    }));
                    SWF.Application.Run(form);
                }
                catch (Exception ex) { failure ??= ex; }
            });
            t.SetApartmentState(ApartmentState.STA);
            t.Start();
            Assert.True(t.Join(TimeSpan.FromSeconds(60)), "the message loop did not end");
            if (failure != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
            Assert.True(ran, "the loop never ran the body");
        }

        [Fact]
        public void AfterEnableVisualStyles_TheMessageLoopBindsComctl32Version6()
        {
            if (!OperatingSystem.IsWindows()) return;
            InLoop(() =>
            {
                // The import binds (no EntryPointNotFoundException) and rejects a null config. It is called FIRST:
                // nothing in the loop has to have loaded comctl32 already (a plain form creates no common control),
                // so checking the module before the call depended on another test having loaded it.
                Assert.Equal(E_INVALIDARG, TaskDialogIndirectNull(IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero));
                // ...and what the loop's context resolves comctl32 to is v6: v5 has no task dialogs.
                IntPtr comctl = GetModuleHandleW("comctl32.dll");
                Assert.NotEqual(IntPtr.Zero, comctl);
                Assert.NotEqual(IntPtr.Zero, GetProcAddress(comctl, "TaskDialogIndirect"));
            });
        }

        [Fact]
        public void ATaskDialogShownFromTheMessageLoop_Succeeds()
        {
            if (!OperatingSystem.IsWindows()) return;
            const uint TDN_CREATED = 0, TDN_DIALOG_CONSTRUCTED = 7;
            const uint TDM_CLICK_BUTTON = 0x0400 + 102;
            const uint TDCBF_CANCEL_BUTTON = 0x0008, TDF_ALLOW_DIALOG_CANCELLATION = 0x0008;
            const int IDCANCEL = 2;
            const uint SWP_NOSIZE = 0x1, SWP_NOZORDER = 0x4, SWP_NOACTIVATE = 0x10;
            int created = 0, hrSeen = -1, buttonSeen = -1;
            var seen = new System.Collections.Generic.List<uint>();
            // Moved off every monitor before it is shown, and closed as soon as it exists: nothing
            // reaches the screen and nothing is activated.
            TaskDialogCallback callback = (hwnd, msg, wParam, lParam, data) =>
            {
                seen.Add(msg);
                if (msg == TDN_DIALOG_CONSTRUCTED)
                    SetWindowPos(hwnd, IntPtr.Zero, -32000, -32000, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
                else if (msg == TDN_CREATED)
                {
                    created++;
                    PostMessageW(hwnd, TDM_CLICK_BUTTON, (IntPtr)IDCANCEL, IntPtr.Zero);
                }
                return 0;
            };
            InLoop(() =>
            {
                IntPtr title = Marshal.StringToHGlobalUni("TaskDialogActivationTest");
                IntPtr text = Marshal.StringToHGlobalUni("closes itself");
                try
                {
                    var config = new TASKDIALOGCONFIG
                    {
                        cbSize = (uint)Marshal.SizeOf<TASKDIALOGCONFIG>(),
                        dwFlags = TDF_ALLOW_DIALOG_CANCELLATION,
                        dwCommonButtons = TDCBF_CANCEL_BUTTON,
                        pszWindowTitle = title,
                        pszMainInstruction = text,
                        pfCallback = Marshal.GetFunctionPointerForDelegate(callback),
                    };
                    int hr = TaskDialogIndirect(ref config, out int button, IntPtr.Zero, IntPtr.Zero);
                    hrSeen = hr; buttonSeen = button;
                    Assert.Equal(0, hr);
                    Assert.Equal(IDCANCEL, button);
                }
                finally
                {
                    Marshal.FreeHGlobal(title);
                    Marshal.FreeHGlobal(text);
                }
            });
            GC.KeepAlive(callback);
            Assert.True(created == 1, $"hr {hrSeen:x} button {buttonSeen} notifications: " + string.Join(",", seen));
        }
    }
}
