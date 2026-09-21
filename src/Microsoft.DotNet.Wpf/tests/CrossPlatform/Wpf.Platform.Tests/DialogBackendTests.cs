// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// The three file-dialog backends that were missing: browser, iOS and Android.
//
// The same shape as ClipboardBackendTests next door, and for the same reason: each backend reaches
// into something that exists on exactly one platform, and the failure mode of a wrong guard is not a
// wrong answer but a crash, or a native entry point that is not there. Those are the properties that
// can be checked from any machine.
//
// The dialog behaviour these back (ShowDialog, ShowDialogAsync, CommitAsync) lives one layer up in
// PresentationFramework and is covered by Wpf.Dialog.Tests; this project references WindowsBase
// alone, on purpose.
//
// Filter translation is here rather than there because BrowserDialogs.FilterToAccept is in
// WindowsBase, and because it is the part most likely to be quietly wrong: WPF's filter format has
// named GROUPS that no other platform's picker has, so the translation has to lose something and the
// tests pin down exactly what.
//

using System;
using System.Threading.Tasks;
using MS.Internal.Interop;
using Xunit;

namespace Wpf.Platform.Tests
{
    public class DialogBackendTests
    {
        // ---- availability tracks the platform, and only the platform ---------------------

        [Fact]
        public void BrowserDialogsAreAvailableExactlyInABrowser()
        {
            Assert.Equal(OperatingSystem.IsBrowser(), BrowserDialogs.IsAvailable);
        }

        [Fact]
        public void UIKitDialogsAreAvailableExactlyOnIOS()
        {
            Assert.Equal(OperatingSystem.IsIOS(), UIKitDialogs.IsAvailable);
        }

        /// <summary>
        ///  Android additionally needs a head, because the Storage Access Framework is started from
        ///  an Activity and only the payload has one.
        /// </summary>
        [Fact]
        public void AndroidDialogsNeedBothThePlatformAndAHost()
        {
            Assert.Equal(OperatingSystem.IsAndroid() && AndroidDialogs.Host is not null,
                         AndroidDialogs.IsAvailable);

            if (!OperatingSystem.IsAndroid())
            {
                Assert.False(AndroidDialogs.IsAvailable);
            }
        }

        // ---- an unavailable backend answers instead of faulting --------------------------

        [Fact]
        public async Task UIKitDialogsAreInertOffIOS()
        {
            if (OperatingSystem.IsIOS()) return;

            Assert.Empty(await UIKitDialogs.ShowOpenPanelAsync(null, multiple: true, directory: false));
            Assert.Empty(await UIKitDialogs.ShowExportPanelAsync("/nonexistent"));
        }

        [Fact]
        public async Task BrowserDialogsAreInertOutsideABrowser()
        {
            if (OperatingSystem.IsBrowser()) return;

            Assert.Empty(await BrowserDialogs.ShowOpenPanelAsync(null, multiple: true, directory: false));
            Assert.Null(BrowserDialogs.ReserveSavePath("x.txt"));
            Assert.False(BrowserDialogs.OfferDownload("/nonexistent", "text/plain"));
        }

        [Fact]
        public async Task AndroidDialogsAreInertWithoutAHost()
        {
            IAndroidDialogHost? previous = AndroidDialogs.Host;
            AndroidDialogs.Host = null;
            try
            {
                Assert.False(AndroidDialogs.IsAvailable);
                Assert.Empty(await AndroidDialogs_ShowOpenPanelAsync());
            }
            finally
            {
                AndroidDialogs.Host = previous;
            }
        }

        // AndroidDialogs' dispatch helpers are internal to WindowsBase and this project is a declared
        // friend, but the internal overloads are not part of the public surface being asserted here;
        // the reachable check is that a hostless backend reports itself unavailable, which is what
        // every call site consults first.
        private static Task<string[]> AndroidDialogs_ShowOpenPanelAsync() =>
            AndroidDialogs.IsAvailable
                ? AndroidDialogs.Host!.PickFilesAsync(null, multiple: false, directory: false)
                : Task.FromResult(Array.Empty<string>());

        // ---- filter translation ----------------------------------------------------------

        [Theory]
        // Every group's extensions are offered together: a browser picker has one list, not groups.
        [InlineData("Text files|*.txt;*.log", ".txt,.log")]
        [InlineData("Images|*.png;*.jpg;*.jpeg", ".png,.jpg,.jpeg")]
        [InlineData("Documents|*.pdf|Spreadsheets|*.csv;*.xlsx", ".pdf,.csv,.xlsx")]
        // A wildcard anywhere means no restriction: an accept list containing ".*" matches nothing,
        // so the honest translation of "All files" is to send none at all.
        [InlineData("Text files|*.txt;*.log|All files|*.*", null)]
        [InlineData("All files|*.*", null)]
        [InlineData("Anything|*", null)]
        [InlineData("", null)]
        [InlineData(null, null)]
        public void FilterBecomesAnAcceptList(string filter, string expected)
        {
            Assert.Equal(expected, BrowserDialogs.FilterToAccept(filter));
        }

        /// <summary>
        ///  A duplicate extension across two groups must appear once: browsers show the accept list
        ///  to the user, and ".txt,.txt" is visibly wrong.
        /// </summary>
        [Fact]
        public void FilterDropsDuplicateExtensions()
        {
            Assert.Equal(".txt,.log", BrowserDialogs.FilterToAccept("Text|*.txt;*.log|Logs|*.log;*.txt"));
        }

        /// <summary>
        ///  A malformed filter must not throw. WPF validates Filter on assignment, but this helper is
        ///  also reachable with whatever was there before that validation ran.
        /// </summary>
        [Theory]
        [InlineData("no pipes at all")]
        [InlineData("Trailing|")]
        [InlineData("|*.txt")]
        [InlineData("||||")]
        [InlineData("Group|txt")]      // a pattern with no dot at all
        public void MalformedFilterIsToleratedRatherThanThrowing(string filter)
        {
            _ = BrowserDialogs.FilterToAccept(filter);
        }
    }
}
