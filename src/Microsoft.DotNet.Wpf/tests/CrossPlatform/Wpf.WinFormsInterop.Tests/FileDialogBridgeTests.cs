// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// Which file browser a WinForms application gets, and what it hands back.
//
// Mono's managed dialog is a Windows-2000 dialog with a places bar and a "Look in:" combo. Measured
// against the real thing on Windows it is 555x385 where Windows shows an 839x497 Explorer window
// with a navigation pane, a breadcrumb bar and a details view -- a different program's window, not a
// layout to be adjusted into shape. So on Windows the platform's own is called instead, through
// comdlg32 (which forwards to the same modern picker on Vista and later) rather than through
// IFileDialog, so that no COM interface has to be declared to get it.
//
// Two things are asserted, and the second is where the bugs live:
//
//   * WHICH bridge is used. A host that installs its own still wins, because a mixed WPF/WinForms
//     application must show one browser rather than two. And a bridge that does files and NOT
//     folders must say so: "the user cancelled" and "this bridge has no folder browser" are both
//     false out of ShowFolder, and conflating them made every FolderBrowserDialog return Cancel
//     without showing anything. SupportsFolder was added after exactly that mistake.
//
//   * WHAT COMES BACK. A multiselect does not arrive as a list of paths: it is the DIRECTORY
//     followed by each bare name, every one NUL-terminated, the lot closed by an empty one -- and a
//     single selection arrives in the one-name shape even when multiselect was allowed. Driving that
//     with real keystrokes was tried and abandoned: whether the key lands depends on which window
//     has the focus, so the same run passed and failed by luck. The buffer can just be handed over.
//

using System;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Xunit;

namespace Wpf.WinFormsInterop.Tests
{
    public class FileDialogBridgeTests
    {
        private static readonly Assembly Swf = typeof(Form).Assembly;
        private static readonly Type BridgeType = Swf.GetType("System.Windows.Forms.IFileDialogBridge")!;
        private static readonly Type Driver = Swf.GetType("System.Windows.Forms.XplatUIWebGpu")!;
        private static Type Win32Bridge => Swf.GetType("System.Windows.Forms.Win32FileDialogBridge")!;

        private static PropertyInfo BridgeProperty =>
            Driver.GetProperty("FileDialogBridge", BindingFlags.Static | BindingFlags.NonPublic)!;

        private static object CurrentBridge() => BridgeProperty.GetValue(null);

        // ---- which browser ------------------------------------------------------------------------

        [Fact]
        public void OnWindows_ThePlatformsOwnBrowserIsUsedWhenNothingElseIsInstalled()
        {
            Assert.SkipUnless(OperatingSystem.IsWindows(), "only Windows has a common dialog to call");

            object bridge = CurrentBridge();
            Assert.NotNull(bridge);
            Assert.Equal("Win32FileDialogBridge", bridge!.GetType().Name);
        }

        [Fact]
        public void AnInstalledBridgeIsReturnedAsItIs_NotReplacedByThePlatformDefault()
        {
            object saved = BridgeProperty.GetValue(null);
            Assert.SkipWhen(saved is null, "no platform bridge on this head to stand in");
            try
            {
                BridgeProperty.SetValue(null, saved);
                Assert.Same(saved, CurrentBridge());
            }
            finally
            {
                // null, so the getter goes back to deciding for itself.
                BridgeProperty.SetValue(null, null);
            }
        }

        [Fact]
        public void ABridgeThatDoesNotDoFolders_SaysSoRatherThanReturningCancelled()
        {
            Assert.SkipUnless(OperatingSystem.IsWindows(), "only Windows installs one by default");

            object bridge = CurrentBridge();
            bool supports = (bool)BridgeType.GetProperty("SupportsFolder")!.GetValue(bridge)!;
            Assert.False(supports,
                "comdlg32 has no folder picker, so this bridge must decline folders -- otherwise "
                + "FolderBrowserDialog reports Cancel and never shows the managed tree");

            object[] args = { "pick a folder", null, null };
            bool shown = (bool)BridgeType.GetMethod("ShowFolder")!.Invoke(bridge, args)!;
            Assert.False(shown);
            Assert.Null(args[2]);
        }

        // ---- what comes back ----------------------------------------------------------------------

        /// <summary>Lay the parts out the way comdlg32 does -- each NUL-terminated, an empty one at
        /// the end -- and read them back through the bridge's own parser.</summary>
        private static string[] ReadNames(string[] parts, bool multiselect)
        {
            const int MaxPath = 32768;
            var chars = new char[MaxPath];
            int at = 0;
            foreach (string part in parts)
            {
                part.CopyTo(0, chars, at, part.Length);
                at += part.Length;
                chars[at++] = '\0';
            }
            chars[at] = '\0';

            IntPtr buffer = Marshal.AllocHGlobal(MaxPath * sizeof(char));
            try
            {
                Marshal.Copy(chars, 0, buffer, MaxPath);
                MethodInfo m = Win32Bridge.GetMethod("ReadNames", BindingFlags.Static | BindingFlags.NonPublic)!;
                return (string[])m.Invoke(null, new object[] { buffer, multiselect })!;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        [Fact]
        public void OneChosenFile_ComesBackAsItsOwnFullPath()
        {
            Assert.SkipUnless(OperatingSystem.IsWindows(), "the Win32 bridge is Windows only");
            Assert.Equal(new[] { @"C:\work\report.txt" },
                         ReadNames(new[] { @"C:\work\report.txt" }, multiselect: false));
        }

        [Fact]
        public void SeveralChosenFiles_AreJoinedToTheDirectoryThatPrecedesThem()
        {
            Assert.SkipUnless(OperatingSystem.IsWindows(), "the Win32 bridge is Windows only");
            Assert.Equal(new[] { @"C:\work\a.txt", @"C:\work\b.txt", @"C:\work\c.txt" },
                         ReadNames(new[] { @"C:\work", "a.txt", "b.txt", "c.txt" }, multiselect: true));
        }

        [Fact]
        public void OneFileChosenWhereManyWereAllowed_IsStillOneFullPath()
        {
            Assert.SkipUnless(OperatingSystem.IsWindows(), "the Win32 bridge is Windows only");

            // The shape that catches a reader trusting the FLAG instead of the count: with one
            // selection the dialog writes the whole path and no directory line, so treating the first
            // entry as a directory would hand back nothing at all.
            Assert.Equal(new[] { @"C:\work\only.txt" },
                         ReadNames(new[] { @"C:\work\only.txt" }, multiselect: true));
        }

        [Fact]
        public void NothingChosen_IsNoFilesRatherThanOneEmptyName()
        {
            Assert.SkipUnless(OperatingSystem.IsWindows(), "the Win32 bridge is Windows only");
            Assert.Empty(ReadNames(Array.Empty<string>(), multiselect: true));
        }

        [Fact]
        public void TheFilterIsHandedOverInTheShapeComdlg32Wants()
        {
            Assert.SkipUnless(OperatingSystem.IsWindows(), "the Win32 bridge is Windows only");

            MethodInfo m = Win32Bridge.GetMethod("ToNativeFilter", BindingFlags.Static | BindingFlags.NonPublic)!;
            string got = (string)m.Invoke(null, new object[] { "Text|*.txt|All files|*.*" })!;
            Assert.Equal("Text\0*.txt\0All files\0*.*\0\0", got);
            Assert.Null(m.Invoke(null, new object[] { null }));
        }
    }
}
