// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// Which colour dialog a WinForms application gets, what it is asked, and what it hands back.
//
// The rule is the platform's own wherever there is one: ChooseColor on Windows, NSColorPanel on
// macOS, UIColorPickerViewController on iOS and <input type=color> in a browser (the last two
// through the WPF host, and asynchronously), the managed dialog -- laid out as ChooseColor -- only
// on Linux and Android, which have no colour chooser to call.
//
// Nothing here opens a dialog: a modal one would block the run. What is asserted instead is
//
//   * WHICH bridge is chosen, and that an installed one is used and its answer becomes the result,
//     with the managed form never shown after it;
//
//   * WHAT the platform is asked: ChooseColor's flag word and COLORREFs are built from the
//     properties exactly as .NET's ColorDialog builds them (CHOOSECOLORW is filled and inspected,
//     not passed);
//
//   * the managed dialog's arithmetic and layout against numbers read off Windows' own dialog.
//

using System;
using System.Drawing;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows.Forms;
using Xunit;

namespace Wpf.WinFormsInterop.Tests
{
    public class ColorDialogBridgeTests
    {
        private sealed class FakeBridge : IColorDialogBridge
        {
            public ColorDialogRequest Asked;
            public bool? Answer;
            public Color Chosen;
            public int[] ChosenCustom;
            public TaskCompletionSource<bool?> Later;

            public bool? Show(ColorDialogRequest request)
            {
                Asked = request;
                if (Answer == true)
                {
                    request.Color = Chosen;
                    if (ChosenCustom != null) request.CustomColors = ChosenCustom;
                }
                return Answer;
            }

            public Task<bool?> ShowAsync(ColorDialogRequest request)
            {
                Asked = request;
                if (Later == null) return Task.FromResult(Show(request));
                return Later.Task.ContinueWith(t =>
                {
                    if (t.Result == true) request.Color = Chosen;
                    return t.Result;
                }, TaskScheduler.Default);
            }
        }

        private static void WithBridge(IColorDialogBridge bridge, Action body)
        {
            ColorDialog.PlatformBridge = bridge;
            try { body(); }
            finally { ColorDialog.PlatformBridge = null; }
        }

        private static bool RunDialog(ColorDialog d)
            => (bool)typeof(ColorDialog).GetMethod("RunDialog", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(d, new object[] { IntPtr.Zero })!;

        private static Form FormOf(CommonDialog d)
            => (Form)typeof(CommonDialog).GetField("form", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(d)!;

        // ---- which dialog -------------------------------------------------------------------------

        [Fact]
        public void OnWindows_ChooseColorIsThePlatformDialog()
        {
            Assert.SkipUnless(OperatingSystem.IsWindows(), "only Windows has comdlg32");
            Assert.IsType<Win32ColorDialogBridge>(ColorDialog.PlatformBridge);
        }

        [Fact]
        public void OnMac_NSColorPanelIsThePlatformDialog()
        {
            Assert.SkipUnless(OperatingSystem.IsMacOS(), "only macOS has NSColorPanel");
            Assert.IsType<MacColorDialogBridge>(ColorDialog.PlatformBridge);
        }

        [Fact]
        public void OnLinuxAndAndroid_ThereIsNoPlatformDialog_SoTheManagedOneRuns()
        {
            Assert.SkipUnless(OperatingSystem.IsLinux() || OperatingSystem.IsAndroid(), "a head with no colour chooser");
            Assert.Null(ColorDialog.PlatformBridge);
        }

        [Fact]
        public void AnInstalledBridgeIsUsed_AndItsAnswerIsTheResult()
        {
            var fake = new FakeBridge { Answer = true, Chosen = Color.FromArgb(10, 20, 30), ChosenCustom = new int[16] };
            fake.ChosenCustom[3] = 0x00332211;
            WithBridge(fake, () =>
            {
                using var d = new ColorDialog { Color = Color.Red };
                // ShowDialog returns without the managed form: the platform's dialog has been and gone.
                Assert.Equal(DialogResult.OK, d.ShowDialog());
                Assert.NotNull(fake.Asked);
                Assert.Equal(Color.FromArgb(10, 20, 30).ToArgb(), d.Color.ToArgb());
                Assert.Equal(0x00332211, d.CustomColors[3]);
                Assert.False(FormOf(d).Visible);
            });
        }

        [Fact]
        public void ACancelledPlatformDialog_LeavesTheColourAndCustomColoursAlone()
        {
            var fake = new FakeBridge { Answer = false };
            WithBridge(fake, () =>
            {
                using var d = new ColorDialog { Color = Color.Teal, CustomColors = new[] { 0x123456 } };
                Assert.Equal(DialogResult.Cancel, d.ShowDialog());
                Assert.Equal(Color.Teal, d.Color);
                Assert.Equal(0x123456, d.CustomColors[0]);
            });
        }

        [Fact]
        public void ABridgeThatCannotShow_FallsBackToTheManagedDialog()
        {
            var fake = new FakeBridge { Answer = null };
            WithBridge(fake, () =>
            {
                using var d = new ColorDialog { Color = Color.FromArgb(255, 128, 64) };
                // RunDialog alone: it prepares the managed form (which ShowDialog would then show).
                Assert.True(RunDialog(d));
                Assert.NotNull(fake.Asked);
                Assert.False((bool)typeof(CommonDialog).GetField("ranOnPlatform", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(d)!);
                Assert.Equal(new Size(222, 299), FormOf(d).ClientSize);
            });
        }

        [Fact]
        public async Task ShowDialogAsync_WaitsForThePlatformsLaterAnswer()
        {
            var fake = new FakeBridge { Later = new TaskCompletionSource<bool?>(), Chosen = Color.FromArgb(1, 2, 3) };
            ColorDialog.PlatformBridge = fake;
            System.Threading.SynchronizationContext saved = System.Threading.SynchronizationContext.Current;
            try
            {
                using var d = new ColorDialog();
                // Making the dialog's form installs WinForms' context on this thread, and nothing here
                // pumps its messages: the answer is awaited without it.
                System.Threading.SynchronizationContext.SetSynchronizationContext(null);
                Task<DialogResult> pending = d.ShowDialogAsync();
                Assert.False(pending.IsCompleted);
                fake.Later.SetResult(true);
                Assert.Equal(DialogResult.OK, await pending);
                Assert.Equal(Color.FromArgb(1, 2, 3).ToArgb(), d.Color.ToArgb());
            }
            finally
            {
                System.Threading.SynchronizationContext.SetSynchronizationContext(saved);
                ColorDialog.PlatformBridge = null;
            }
        }

        // ---- what the platform is asked ------------------------------------------------------------

        [Fact]
        public void Properties_AreChooseColorsFlags_AsInDotNet()
        {
            using var d = new ColorDialog();
            Assert.True(d.AllowFullOpen);
            Assert.Equal(ColorDialogRequest.CC_RGBINIT, d.PlatformRequest().Flags);

            d.AllowFullOpen = false;
            d.AnyColor = true;
            d.FullOpen = true;
            d.ShowHelp = true;
            d.SolidColorOnly = true;
            int flags = d.PlatformRequest().Flags;
            Assert.Equal(ColorDialogRequest.CC_RGBINIT | ColorDialogRequest.CC_PREVENTFULLOPEN | ColorDialogRequest.CC_ANYCOLOR
                | ColorDialogRequest.CC_SHOWHELP | ColorDialogRequest.CC_SOLIDCOLOR, flags);   // FULLOPEN cleared: not allowed

            d.AllowFullOpen = true;
            Assert.Equal(ColorDialogRequest.CC_FULLOPEN, d.PlatformRequest().Flags & ColorDialogRequest.CC_FULLOPEN);

            d.Reset();
            Assert.Equal(ColorDialogRequest.CC_RGBINIT, d.PlatformRequest().Flags);
            Assert.Equal(Color.Black, d.Color);
        }

        [Fact]
        public void ChooseColorIsHandedCOLORREFs_AndItsHookFlag()
        {
            Assert.SkipUnless(OperatingSystem.IsWindows(), "the Win32 structure");
            using var d = new ColorDialog { Color = Color.FromArgb(0x11, 0x22, 0x33), FullOpen = true };
            ColorDialogRequest request = d.PlatformRequest();
            var owner = (IntPtr)0x1234;
            var custom = (IntPtr)0x5678;
            Win32ColorDialogBridge.CHOOSECOLORW cc = Win32ColorDialogBridge.Build(request, owner, custom);

            Assert.Equal(IntPtr.Size == 8 ? 72 : 36, cc.lStructSize);
            Assert.Equal(owner, cc.hwndOwner);
            Assert.Equal(custom, cc.lpCustColors);
            Assert.Equal(0x00332211, cc.rgbResult);
            Assert.Equal(ColorDialogRequest.CC_RGBINIT | ColorDialogRequest.CC_ENABLEHOOK | ColorDialogRequest.CC_FULLOPEN, cc.Flags);
            // hInstance only matters with a template, and none is asked for.
            Assert.Equal(IntPtr.Zero, cc.hInstance);
        }

        [Fact]
        public void CustomColors_AreSixteenCOLORREFs_CopiedInAndOut()
        {
            using var d = new ColorDialog();
            int[] all = d.CustomColors;
            Assert.Equal(16, all.Length);
            Assert.All(all, c => Assert.Equal(0x00FFFFFF, c));

            d.CustomColors = new[] { 0x000000FF, 0x0000FF00 };
            int[] got = d.CustomColors;
            Assert.Equal(0x000000FF, got[0]);
            Assert.Equal(0x0000FF00, got[1]);
            Assert.Equal(0x00FFFFFF, got[2]);          // padded with white past the end

            got[0] = 0;
            Assert.Equal(0x000000FF, d.CustomColors[0]);   // a copy came out

            // A COLORREF is 0x00BBGGRR: 0xFF is red.
            Assert.Equal(Color.FromArgb(255, 0, 0).ToArgb(), ColorDialogRequest.FromColorRef(0x000000FF).ToArgb());
            Assert.Equal(0x000000FF, ColorDialogRequest.ToColorRef(Color.Red));
        }

        [Fact]
        public void TheSameValueBack_KeepsANamedColourNamed()
        {
            using var d = new ColorDialog { Color = Color.Red };
            var request = d.PlatformRequest();
            request.Color = Color.FromArgb(255, 0, 0);
            d.AcceptPlatformAnswer(request);
            Assert.True(d.Color.IsNamedColor);

            request.Color = Color.FromArgb(254, 0, 0);
            d.AcceptPlatformAnswer(request);
            Assert.Equal(Color.FromArgb(254, 0, 0).ToArgb(), d.Color.ToArgb());
        }

        [Fact]
        public void MacPanel_AChangedColourIsOK_AnUnchangedOneIsCancel()
        {
            var request = new ColorDialogRequest { Color = Color.FromArgb(10, 20, 30) };
            Assert.False(MacColorDialogBridge.Accept(request, 10, 20, 30));
            Assert.Equal(Color.FromArgb(10, 20, 30).ToArgb(), request.Color.ToArgb());

            Assert.True(MacColorDialogBridge.Accept(request, 11, 20, 30));
            Assert.Equal(Color.FromArgb(11, 20, 30).ToArgb(), request.Color.ToArgb());
        }

        // ---- the WPF host ----------------------------------------------------------------------------

        [Fact]
        public void HostedOnWindows_TheHostLeavesChooseColorInPlace()
        {
            Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows' own bridge");
            System.Windows.Forms.Integration.WpfColorDialogBridge.Install();
            Assert.IsType<Win32ColorDialogBridge>(ColorDialog.PlatformBridge);
        }

        [Fact]
        public void HostedBridge_RefusesToBlock_AndNamesShowDialogAsync()
        {
            var bridge = new System.Windows.Forms.Integration.WpfColorDialogBridge();
            NotSupportedException error = Assert.Throws<NotSupportedException>(() => bridge.Show(new ColorDialogRequest()));
            Assert.Contains("ShowDialogAsync", error.Message, StringComparison.Ordinal);
        }

        [Fact]
        public async Task HostedBridge_OnAHeadWithoutAPicker_AnswersNoneSoTheManagedDialogRuns()
        {
            Assert.SkipWhen(OperatingSystem.IsIOS() || OperatingSystem.IsBrowser(), "this head has a picker");
            var bridge = new System.Windows.Forms.Integration.WpfColorDialogBridge();
            Assert.Null(await bridge.ShowAsync(new ColorDialogRequest { Color = Color.Red }));
        }

        [Fact]
        public void BrowserColours_AreHashHex()
        {
            Assert.Equal("#ff8040", System.Windows.Forms.Integration.WpfColorDialogBridge.ToHex(0xFF8040));
            Assert.Equal("#000000", System.Windows.Forms.Integration.WpfColorDialogBridge.ToHex(0));
            Assert.True(System.Windows.Forms.Integration.WpfColorDialogBridge.TryParseHex("#0a0B0c", out int rgb));
            Assert.Equal(0x0A0B0C, rgb);
            Assert.False(System.Windows.Forms.Integration.WpfColorDialogBridge.TryParseHex("", out _));
            Assert.False(System.Windows.Forms.Integration.WpfColorDialogBridge.TryParseHex("#12345", out _));
        }

        // ---- the managed dialog: Windows' numbers ------------------------------------------------------

        [Theory]
        // Read off ChooseColor: the six boxes for each colour.
        [InlineData(255, 128, 64, 13, 150, 240)]
        [InlineData(64, 128, 128, 120, 90, 80)]
        [InlineData(0, 0, 0, 160, 0, 0)]
        public void RgbToHls_IsComdlg32s(int r, int g, int b, int hue, int lum, int sat)
        {
            ColorDialog.RgbToHls(Color.FromArgb(r, g, b), out int h, out int l, out int s);
            Assert.Equal((hue, lum, sat), (h, l, s));
        }

        [Fact]
        public void HlsToRgb_IsComdlg32s()
        {
            // The rainbow's first block and its first block one saturation step down (FF0000, FB0404),
            // and a block of the teal luminance bar (lum 232 at hue 120 sat 80: F3FAFA).
            Assert.Equal(Color.FromArgb(255, 0, 0).ToArgb(), ColorDialog.HlsToRgb(0, 120, 240).ToArgb());
            Assert.Equal(Color.FromArgb(0xFB, 0x04, 0x04).ToArgb(), ColorDialog.HlsToRgb(0, 120, 232).ToArgb());
            Assert.Equal(Color.FromArgb(0xF3, 0xFA, 0xFA).ToArgb(), ColorDialog.HlsToRgb(120, 232, 80).ToArgb());
        }

        [Fact]
        public void LuminanceBlocks_AndArrow_AreWhereWindowsDrawsThem()
        {
            int[] windows = { 4, 10, 16, 22, 28, 35, 41, 47, 53, 60, 66, 72, 78, 85, 91, 97, 103, 109, 116, 122, 128, 134, 141, 147, 153, 159, 166, 172, 178, 183 };
            for (int k = 1; k <= windows.Length; k++)
                Assert.Equal(windows[k - 1], ColorDialog.LumScroll.Boundary(k, 187));
            // The arrow's tip, down from the gradient's top: 77 and 124 on screen, the gradient at 8.
            Assert.Equal(69, ColorDialog.LumScroll.TipOffset(150));
            Assert.Equal(116, ColorDialog.LumScroll.TipOffset(90));
        }

        [Fact]
        public void TheManagedDialog_IsLaidOutAsChooseColor()
        {
            using var d = new ColorDialog { Color = Color.FromArgb(255, 128, 64), FullOpen = true };
            d.PrepareForm();
            Form form = FormOf(d);
            Assert.Equal(new Size(447, 299), form.ClientSize);

            Rectangle Rect(string text)
            {
                foreach (Control c in form.Controls)
                    if (c.Text == text) return c.Bounds;
                throw new InvalidOperationException(text);
            }
            Assert.Equal(new Rectangle(6, 7, 210, 15), Rect("&Basic colors:"));
            Assert.Equal(new Rectangle(6, 172, 210, 15), Rect("&Custom colors:"));
            Assert.Equal(new Rectangle(6, 244, 207, 23), Rect("&Define Custom Colors >>"));
            Assert.Equal(new Rectangle(6, 270, 66, 23), Rect("OK"));
            Assert.Equal(new Rectangle(78, 270, 66, 23), Rect("Cancel"));
            Assert.Equal(new Rectangle(291, 205, 30, 15), Rect("Hu&e:"));
            Assert.Equal(new Rectangle(365, 250, 36, 15), Rect("Bl&ue:"));
            Assert.Equal(new Rectangle(228, 270, 213, 23), Rect("&Add to Custom Colors"));

            // The swatches: sunken 19x17 at a 26x22 pitch from the box's (4,3); on screen the first
            // is at (10,26) and the last basic one at (192,136).
            Assert.Equal(new Rectangle(4, 3, 19, 17), ColorDialog.SwatchGrid.SwatchRect(0, 8));
            Assert.Equal(new Rectangle(186, 113, 19, 17), ColorDialog.SwatchGrid.SwatchRect(47, 8));
        }

        [Fact]
        public void TheManagedDialog_StartsOnItsColour_AndOKTakesTheChosenOne()
        {
            using var d = new ColorDialog { Color = Color.FromArgb(255, 128, 64), CustomColors = new[] { 0x00123456 } };
            d.PrepareForm();
            Assert.Equal((13, 150, 240), (d.hue, d.lum, d.sat));

            ColorDialog.SwatchGrid Grid(ColorDialog dialog, string name)
                => (ColorDialog.SwatchGrid)typeof(ColorDialog).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(dialog)!;
            // Its basic box is ringed (row 2, column 1), and no custom one.
            Assert.Equal(17, Grid(d, "basicGrid").Selected);
            Assert.Equal(-1, Grid(d, "customGrid").Selected);

            // A colour that is in no box starts with the first basic box ringed, as Windows does.
            using var odd = new ColorDialog { Color = Color.FromArgb(0x56, 0x34, 0x12) };
            odd.PrepareForm();
            Assert.Equal(0, Grid(odd, "basicGrid").Selected);

            // A custom colour's own box is ringed instead.
            using var custom = new ColorDialog { Color = Color.FromArgb(0x56, 0x34, 0x12), CustomColors = new[] { 0, 0x00123456 } };
            custom.PrepareForm();
            Assert.Equal(-1, Grid(custom, "basicGrid").Selected);
            Assert.Equal(1, Grid(custom, "customGrid").Selected);

            // Choosing the basic red, then OK.
            d.SetFromRgb(Color.FromArgb(255, 0, 0), null);
            typeof(ColorDialog).GetMethod("BuildResult", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(d, null);
            Assert.Equal(Color.FromArgb(255, 0, 0).ToArgb(), d.Color.ToArgb());
            Assert.Equal(0x00123456, d.CustomColors[0]);
        }
    }
}
