// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// MessageBox.Show and MessageBox.ShowAsync: the same contract ShowDialogTests states for dialogs,
// applied to the prompt people actually reach for.
//
// MessageBox had the identical defect ShowDialog was fixed for, and kept it: on browser, iOS and
// Android nothing was displayed and Show returned an answer anyway. Worse than the dialog case,
// because of which answer. Every overload but one leaves defaultResult at None, no button carries
// None, so the fallback took the FIRST button -- Yes, for a YesNo prompt. "Delete this permanently?"
// was answered yes, by nobody, on three heads.
//
// A message box needs a user, so what is asserted here is not that one appears:
//
//   * On those three heads Show must refuse, and must name ShowAsync -- a refusal that does not say
//     what to do instead is a different dead end.
//
//   * On every other head Show must be untouched. The predicate is easy to write one clause too
//     wide (Android is also Linux, iOS is also macOS), and a test that only ran on mobile would
//     never catch the desktop heads breaking.
//
//   * ShowAsync must cover EVERY Show overload. A caller whose shape is missing goes back to Show,
//     which is the call that cannot work -- so parity is the property, not the presence of "an"
//     async method.
//
//   * Argument validation must happen before the platform branch, so a bad enum is rejected the
//     same way everywhere rather than only where a prompt can be drawn.
//

using System;
using System.ComponentModel;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows;
using Xunit;

namespace Wpf.Dialog.Tests
{
    public class MessageBoxTests
    {
        /// <summary>The heads whose run loop cannot be re-entered; see Dispatcher.PushFrameImpl.</summary>
        private static bool CannotBlock =>
            OperatingSystem.IsBrowser() || OperatingSystem.IsIOS() || OperatingSystem.IsAndroid();

        [Fact]
        public void ShowRefusesOnHeadsThatCannotBlock()
        {
            Assert.SkipUnless(CannotBlock, "this head can show a message box synchronously");

            NotSupportedException error = Assert.Throws<NotSupportedException>(
                () => MessageBox.Show("Delete this permanently?", "Confirm", MessageBoxButton.YesNo));

            Assert.Contains("ShowAsync", error.Message, StringComparison.Ordinal);
        }

        /// <summary>
        /// Every Show overload has a ShowAsync taking exactly the same parameters. Written as a
        /// comparison of signatures rather than a list, so an overload added to Show later cannot
        /// quietly go without one.
        /// </summary>
        [Fact]
        public void EveryShowOverloadHasAnAsyncCounterpart()
        {
            string[] Signature(MethodInfo m) =>
                m.GetParameters().Select(p => p.ParameterType.FullName).ToArray();

            MethodInfo[] all = typeof(MessageBox).GetMethods(BindingFlags.Public | BindingFlags.Static);
            var asyncSignatures = all.Where(m => m.Name == nameof(MessageBox.ShowAsync))
                                     .Select(m => string.Join(",", Signature(m)))
                                     .ToHashSet(StringComparer.Ordinal);

            MethodInfo[] sync = all.Where(m => m.Name == nameof(MessageBox.Show)).ToArray();
            Assert.NotEmpty(sync);

            foreach (MethodInfo m in sync)
            {
                string signature = string.Join(",", Signature(m));
                Assert.True(asyncSignatures.Contains(signature),
                    $"MessageBox.Show({signature}) has no ShowAsync counterpart");
            }
        }

        [Fact]
        public void ShowAsyncReturnsATaskOfTheSameResultType()
        {
            MethodInfo m = typeof(MessageBox).GetMethod(
                nameof(MessageBox.ShowAsync), new[] { typeof(string) });

            Assert.NotNull(m);
            Assert.Equal(typeof(Task<MessageBoxResult>), m.ReturnType);
        }

        /// <summary>
        /// The owner overloads reject null on every head. The three asynchronous ones ignore the
        /// owner -- their prompt covers the screen -- but ignoring a value is not accepting anything.
        /// </summary>
        [Fact]
        public async Task ShowAsyncRejectsANullOwner()
        {
            // Cast because ShowAsync(string, string) and ShowAsync(Window, string) are both
            // applicable to a bare null -- the same ambiguity Show has, inherited deliberately.
            await Assert.ThrowsAsync<ArgumentNullException>(
                () => MessageBox.ShowAsync((Window)null!, "text"));
        }

        /// <summary>
        /// Validation precedes the platform branch, so this runs on every head without a prompt ever
        /// being built -- which is also what makes it safe to assert here rather than only on mobile.
        /// </summary>
        [Fact]
        public async Task ShowAsyncValidatesItsEnumsBeforeShowingAnything()
        {
            await Assert.ThrowsAsync<InvalidEnumArgumentException>(
                () => MessageBox.ShowAsync("text", "caption", (MessageBoxButton)999));

            await Assert.ThrowsAsync<InvalidEnumArgumentException>(
                () => MessageBox.ShowAsync("text", "caption", MessageBoxButton.OK, (MessageBoxImage)998));
        }
    }
}
