// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// ShowDialog, ShowDialogAsync and CommitAsync: the contract, on whichever head the suite is running.
//
// A picker needs a user, so what is asserted here is not that a dialog appears. It is the set of
// properties that made the previous behaviour a bug rather than a limitation:
//
//   * On browser, iOS and Android, ShowDialog returned FALSE. Nothing was shown and the application
//     was told the user had cancelled. Nobody could notice, because a cancellation is exactly what
//     that looks like -- which is why it survived. It now refuses, and names the way out.
//
//   * On every other head ShowDialog must be untouched. A CannotBlock predicate written one clause
//     too wide would break the desktop heads, and no test that only ran on mobile would catch it.
//
//   * ShowDialogAsync exists on the whole CommonDialog family, not just on file dialogs, because it
//     is declared on the base -- PrintDialog gets it too.
//
//   * CommitAsync is a no-op returning true on desktop, so one piece of save code works everywhere.
//     If it returned false there, every caller would have to branch and nobody would use it.
//

using System;
using System.Threading.Tasks;
using Microsoft.Win32;
using Xunit;

namespace Wpf.Dialog.Tests
{
    public class ShowDialogTests
    {
        /// <summary>The heads whose run loop cannot be re-entered; see Dispatcher.PushFrameImpl.</summary>
        private static bool CannotBlock =>
            OperatingSystem.IsBrowser() || OperatingSystem.IsIOS() || OperatingSystem.IsAndroid();

        [Fact]
        public void ShowDialogRefusesOnHeadsThatCannotBlock()
        {
            Assert.SkipUnless(CannotBlock, "this head can show a modal dialog");

            var dialog = new OpenFileDialog();
            NotSupportedException error = Assert.Throws<NotSupportedException>(() => dialog.ShowDialog());

            // Naming the alternative is the point: without it this is a different dead end.
            Assert.Contains("ShowDialogAsync", error.Message, StringComparison.Ordinal);
        }

        /// <summary>
        ///  The guard against the refusal being written too broadly. Linux and macOS both satisfy
        ///  OperatingSystem.IsLinux()/IsMacOS() while Android also satisfies IsLinux(), so the
        ///  predicate is easy to get wrong in the direction that breaks working heads.
        /// </summary>
        [Fact]
        public void DesktopHeadsAreNotClassifiedAsUnableToBlock()
        {
            Assert.SkipWhen(CannotBlock, "this head genuinely cannot block");

            Assert.False(OperatingSystem.IsBrowser());
            Assert.False(OperatingSystem.IsIOS());
            Assert.False(OperatingSystem.IsAndroid());
        }

        [Fact]
        public void ShowDialogAsyncExistsOnTheWholeDialogFamily()
        {
            // Declared on CommonDialog, so every dialog inherits it, PrintDialog included.
            Assert.NotNull(typeof(CommonDialog).GetMethod(nameof(CommonDialog.ShowDialogAsync), Type.EmptyTypes));
            Assert.NotNull(typeof(OpenFileDialog).GetMethod(nameof(CommonDialog.ShowDialogAsync), Type.EmptyTypes));
            Assert.NotNull(typeof(SaveFileDialog).GetMethod(nameof(CommonDialog.ShowDialogAsync), Type.EmptyTypes));
            Assert.NotNull(typeof(OpenFolderDialog).GetMethod(nameof(CommonDialog.ShowDialogAsync), Type.EmptyTypes));
        }

        [Fact]
        public void ShowDialogAsyncHasAnOwnerOverloadToo()
        {
            Assert.NotNull(typeof(CommonDialog).GetMethod(
                nameof(CommonDialog.ShowDialogAsync), new[] { typeof(System.Windows.Window) }));
        }

        [Fact]
        public async Task CommitAsyncSucceedsTriviallyOnDesktop()
        {
            Assert.SkipWhen(CannotBlock, "this head needs a real export step");

            string path = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                                                 "wpf-commit-" + Guid.NewGuid().ToString("N") + ".txt");
            System.IO.File.WriteAllText(path, "x");
            try
            {
                var dialog = new SaveFileDialog { FileName = path };
                Assert.True(await dialog.CommitAsync());
            }
            finally
            {
                System.IO.File.Delete(path);
            }
        }

        [Fact]
        public async Task CommitAsyncRefusesAnEmptyFileName()
        {
            var dialog = new SaveFileDialog();
            Assert.False(await dialog.CommitAsync());
        }

        /// <summary>
        ///  ShowDialogAsync(owner) still validates its argument. The three asynchronous heads ignore
        ///  the owner (their pickers are presented over the whole screen), but ignoring it is not the
        ///  same as accepting null.
        /// </summary>
        [Fact]
        public async Task ShowDialogAsyncRejectsANullOwner()
        {
            Assert.SkipUnless(CannotBlock, "the desktop path reaches a different null check");

            var dialog = new OpenFileDialog();
            await Assert.ThrowsAsync<ArgumentNullException>(() => dialog.ShowDialogAsync(null!));
        }
    }
}
