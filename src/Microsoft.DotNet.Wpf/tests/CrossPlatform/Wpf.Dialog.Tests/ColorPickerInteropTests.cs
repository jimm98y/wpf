// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// The colour pickers WPF's interop layer opens for WinForms' ColorDialog where WinForms cannot reach
// the platform by itself: UIColorPickerViewController on iOS and <input type=color> in a browser
// (WindowsFormsHost routes ColorDialog there; WPF has no colour dialog of its own).
//
// Neither can be shown from a test, so what is asserted is the seam's contract on the heads where
// it is NOT available: it must say so ("no picker here": -1 or null) rather than answer as though a
// user had dismissed it, because that answer is what sends ColorDialog to its managed dialog -- the
// same mistake ShowDialogTests describes, where "nothing shown" read as "cancelled". And the browser
// binding must name the JS export the picker is, so a rename on one side is caught here.
//

using System;
using System.Reflection;
using System.Runtime.InteropServices.JavaScript;
using System.Threading.Tasks;
using Xunit;

namespace Wpf.Dialog.Tests
{
    public class ColorPickerInteropTests
    {
        private static readonly Assembly WindowsBase = typeof(System.Windows.DependencyObject).Assembly;

        [Fact]
        public async Task UIKitPicker_OffIOS_SaysThereIsNone()
        {
            Assert.SkipWhen(OperatingSystem.IsIOS(), "iOS has the picker");
            Type picker = WindowsBase.GetType("MS.Internal.Interop.UIKitColorPicker", throwOnError: true)!;
            Assert.False((bool)picker.GetProperty("IsAvailable", BindingFlags.Public | BindingFlags.Static)!.GetValue(null)!);

            var pending = (Task<int>)picker.GetMethod("PickColorAsync", BindingFlags.Public | BindingFlags.Static)!
                .Invoke(null, new object[] { 0xFF8040, "Color" })!;
            Assert.Equal(-1, await pending);
        }

        [Fact]
        public async Task BrowserPicker_OffTheBrowser_SaysThereIsNone()
        {
            Assert.SkipWhen(OperatingSystem.IsBrowser(), "the browser has the picker");
            Type dialogs = WindowsBase.GetType("MS.Internal.Interop.BrowserDialogs", throwOnError: true)!;
            var pending = (Task<string>)dialogs.GetMethod("PickColorAsync", BindingFlags.NonPublic | BindingFlags.Static)!
                .Invoke(null, new object[] { "#ff8040" })!;
            // null is "no picker", which is not "" ("dismissed without a choice").
            Assert.Null(await pending);
        }

        [Fact]
        public void BrowserPicker_IsBoundToTheJsExport()
        {
            Type js = WindowsBase.GetType("MS.Internal.Interop.BrowserDialogs+Js", throwOnError: true)!;
            MethodInfo pick = js.GetMethod("PickColor", BindingFlags.NonPublic | BindingFlags.Static)!;
            // Read as metadata: constructing a JSImportAttribute off the browser throws.
            CustomAttributeData import = Assert.Single(pick.GetCustomAttributesData(),
                a => a.AttributeType == typeof(JSImportAttribute));
            Assert.Equal("pickColorAsync", import.ConstructorArguments[0].Value);
            Assert.Equal("wpfBrowserWindow", import.ConstructorArguments[1].Value);
            Assert.Equal(typeof(Task<string>), pick.ReturnType);
        }
    }
}
