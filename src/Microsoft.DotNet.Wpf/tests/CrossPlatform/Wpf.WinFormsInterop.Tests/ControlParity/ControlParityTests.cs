// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// Our controls against Windows' controls, subtracted.
//
// Each specimen is drawn twice from ONE piece of source (Specimens.cs): once here, by this fork's
// System.Windows.Forms, and once by StockRenderer, which is the same code bound to the real
// Microsoft.WindowsDesktop.App. Both go through Control.DrawToBitmap into a bitmap of the same size
// on the same ground colour, so the two images are directly subtractable and any difference is the
// drawing.
//
// WHY NOT A SCREEN CAPTURE, which is how this was done before: a capture carries the window frame,
// the desktop composition, whatever was behind the window, and ClearType, and every one of those had
// to be argued away before the numbers meant anything. DrawToBitmap has none of it, needs no window
// on the screen, and gives the same answer on a build machine with nobody logged in.
//
// WHAT IS ASSERTED. Two numbers per specimen, because they fail differently:
//
//   * INK, the count of pixels where one side has drawn something and the other has left the ground
//     showing. That is a shape or a position being wrong, and it is what matters.
//   * SHADE, the count of pixels where both drew but the colours differ by more than a hair. That
//     catches a wrong system colour or a gradient going the other way.
//
// Both are written down per specimen in Allowed. A number that goes UP means something that used to
// match has stopped, and the test says so rather than quietly passing a larger difference.
//
// WHAT THIS INSTRUMENT CANNOT SEE, and both blind spots cost time before they were understood:
//
//  * NON-CLIENT drawing, on our side. Our text inputs frame themselves on WM_NCPAINT, and
//    DrawToBitmap renders the client area only, so our border is simply not in the picture. Windows'
//    native edit control prints its own frame into the bitmap, so its border IS. That is not a
//    missing border in the product -- put the two applications on the screen and both have one.
//  * CHILD controls of a container, on Windows' side. A NumericUpDown is an edit and two buttons in
//    a panel, and WM_PRINT does not reach them, so the reference comes back as an empty box while
//    ours draws the value and the arrows. There the reference is the incomplete one.
//
// So a big number here is a QUESTION, not a verdict, and the failure message prints both renders as
// character maps because looking is how the question gets answered. Three of the four faults this
// found were real and are fixed; the two entries above are the instrument.
//

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.Versioning;
using WinFormsControlParity;
using Xunit;

namespace Wpf.WinFormsInterop.Tests
{
    [SupportedOSPlatform("windows")]
    public sealed class ControlParityTests
    {
        /// <summary>What still differs, per specimen: (pixels of ink in one and not the other,
        /// pixels both drew but in different colours).
        /// <para>Lower a number when a change earns it, and NEVER raise one: a number going up means
        /// something that used to be drawn as Windows draws it has stopped.</para>
        /// <para>Two of these are the harness rather than the drawing, and are marked. Before
        /// spending time on a number here, look at the pictures -- the failure message prints both
        /// renders and the difference as character maps.</para></summary>
        private static readonly Dictionary<string, (int Ink, int Shade)> Allowed = new()
        {
            // Ours draws the value and the spin arrows; Windows' DrawToBitmap draws neither, because
            // they live in CHILD controls that WM_PRINT does not reach. The reference is the thin one
            // here, so this number cannot go to zero and going up would be us drawing LESS.
            ["numericupdown"] = (2232, 282),

            // Our text-input frames are drawn on WM_NCPAINT, and DrawToBitmap renders the CLIENT area
            // only, so our border is absent from every one of these. Windows' native edit control
            // prints its own frame into the bitmap, so its border is present. Visible only where the
            // field is the control colour: a white field makes the border ink on both sides and the
            // difference lands in shade instead.
            ["textbox-readonly"] = (839, 153),

            ["button"] = (132, 368),
            // RAISED, and only ever on the live window's authority. Windows 11 does not emboss disabled
            // text -- one flat #A0A0A0 pass, where the base drew a light copy at (1,1) and the real one
            // on top. Against the live stock window that took this button's text from 1.26 times
            // Windows' ink to 0.94, its darkest pixel from 132 to Windows' exact 160, and the whole
            // Buttons region from 111,038 to 88,642. The double draw was also MASKING a rounding
            // difference in this reference's own geometry: with one pass it shows, and on an unshown
            // form the caption lands a pixel over from stock's. The live window has it on the same
            // rows as Windows (87..95, both), so this number is the reference disagreeing, not us.
            ["button-disabled"] = (352, 261),
            // INK DOWN AND SHADE UP, on both this and the group box, from the group box caption
            // moving to where the LIVE stock window draws it and from the string-format margin it
            // used to carry. This reference is rendered on a form that was never shown, and where
            // the two disagree the live window is the authority -- see the disabled button above,
            // which has the same note for the same reason. Ink 148 -> 106 here and 266 -> 168 there
            // says the shapes agree better than they did; the shade is a few edge pixels either way.
            ["button-flat"] = (106, 398),
            // The check GLYPH moved up a row to sit where the live window beside ours puts it, so these
            // three moved with it: the ink is the same or better, the shade a few pixels worse
            // against a reference drawn on a form that was never shown. The live window is the
            // authority for where the box goes; see the note on tabcontrol.
            ["checkbox"] = (309, 45),
            ["checkbox-clear"] = (327, 46),
            ["checkbox-disabled"] = (318, 135),
            ["checkedlistbox"] = (20, 1085),
            // The chevron grew to the size Windows draws it and the editable field's text moved up a
            // row, both measured against the live window -- where these two went from 93k of
            // difference to 55k. This reference, drawn on a form that was never shown, disagrees by a
            // couple of pixels either way; see the note on tabcontrol.
            ["combobox-editable"] = (16, 625),
            ["combobox-list"] = (19, 225),
            // Windows leaves the control's last row clear -- its frame's bottom edge sits a row above
            // ours did -- and the etched hairline is #DCDCDC, not #DFDFDF. Both measured on the live
            // window, where the group box went from 212,532 to 181,740.
            ["groupbox"] = (168, 189),
            // A horizontal scroll bar's thumb was placed without the leading arrow's width at startup
            // (see ScrollBar.OnHandleCreated), which put it eighteen pixels left of Windows'. Fixing
            // that, the missing white leading edge and the thumb's extra pixel took both bars to zero
            // position error against the live window.
            ["hscrollbar"] = (21, 1),
            ["label"] = (110, 102),
            ["label-disabled"] = (120, 116),
            ["linklabel"] = (331, 100),
            // The row caption moved a pixel left and the selection band grew two to the right, both
            // measured against the live window -- where this control went from 276k of difference to
            // 77k, with its text now landing on Windows' exact columns. This reference, drawn on a
            // form that was never shown, disagrees by 158 pixels of shade; see the note on tabcontrol.
            ["listbox"] = (0, 1344),
            ["listview"] = (0, 1837),
            // Windows animates a progress bar, so its shades differ between two runs a second
            // apart while its ink does not. The shade numbers here are a ceiling with room for that.
            ["progressbar"] = (0, 2100),
            ["progressbar-full"] = (0, 4000),
            ["radio"] = (204, 42),
            ["radio-clear"] = (286, 38),
            ["statusstrip"] = (188, 200),
            // HARNESS, not drawing. The same .NET TabControl paints its page 249,249,249 when it
            // is drawn on a form that has never been shown, and 240,240,240 once the form is up --
            // with UseVisualStyleBackColor False and BackColor Control in both readings. Specimens
            // are rendered on an unshown form, so the reference here asks for a page colour that
            // never reaches a screen; ours draws the 240 the live window beside it draws, and the
            // whole page area lands in this number. Measured directly, both ways, before believing
            // it. The live-window comparison is the authority for this one.
            // The INK figure here is that page fill and nothing else, so it moves whenever the tab
            // geometry does and is not a quality signal; the SHADE figure is the one to read.
            ["tabcontrol"] = (12024, 295),
            ["textbox"] = (0, 764),
            // The remaining shade is the dots being antialiased differently, not drawn differently:
            // Windows renders text with ClearType and we render it grey. See the glyph parity suite.
            ["textbox-password"] = (0, 602),
            // The slider is nineteen rows and the channel one row up, both measured on the live window,
            // and the ticks now snap to a column instead of being spread over two by a fractional x.
            ["trackbar"] = (4, 10),
            // The dotted connector now hangs from the centre of its expander box and reaches the
            // label, as Windows draws it (TreeView.DrawNodeLines). Two pixels changed shade in
            // trade; against the live window that was 2,628 pixels of position recovered.
            ["treeview"] = (9, 1062),
            ["vscrollbar"] = (21, 1),
        };

        private static (int Ink, int Shade) AllowanceFor(string name)
            => Allowed.TryGetValue(name, out (int Ink, int Shade) a) ? a : (0, 0);

        public static TheoryData<string> SpecimenNames()
        {
            var data = new TheoryData<string>();
            foreach (Specimen s in Specimens.All()) data.Add(s.Name);
            return data;
        }

        [Theory]
        [MemberData(nameof(SpecimenNames))]
        public void EachControl_IsDrawnAsWindowsDrawsIt(string name)
        {
            Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows draws the reference");
            string reference = StockRenders.Value;
            Assert.SkipWhen(reference is null, StockRenders.Failure ?? "no reference renders");

            string raw = Path.Combine(reference!, name + ".raw");
            string failed = Path.Combine(reference!, name + ".failed");
            Assert.False(File.Exists(failed),
                         $"Windows itself could not draw '{name}':\n" + SafeRead(failed));
            Assert.True(File.Exists(raw), $"the reference renderer produced no '{name}'");

            byte[] windows = ReadRaw(raw, out int w, out int h);

            Specimen spec = Specimens.All().Find(s => s.Name == name);
            Assert.NotNull(spec);
            byte[] ours = SpecimenRenderer.Render(spec!, out int ow, out int oh);

            // With WPF_PARITY_DUMP set, write BOTH sides out in the same raw format the reference
            // uses. Without it there is no way to look at what our side drew -- only at a number
            // saying how far it is from Windows, which cannot tell a wrong colour from a wrong
            // shape. The reference .raw is copied too, so the pair travels together.
            string? dump = Environment.GetEnvironmentVariable("WPF_PARITY_DUMP");
            if (!string.IsNullOrEmpty(dump))
            {
                Directory.CreateDirectory(dump!);
                File.Copy(raw, Path.Combine(dump!, name + "-windows.raw"), overwrite: true);
                using var fs = File.Create(Path.Combine(dump!, name + "-ours.raw"));
                fs.Write(BitConverter.GetBytes(ow));
                fs.Write(BitConverter.GetBytes(oh));
                fs.Write(ours);
            }
            Assert.True(ow == w && oh == h, $"'{name}': sizes differ, ours {ow}x{oh}, Windows {w}x{h}");

            Difference diff = Difference.Between(windows, ours, w, h);
            (int ink, int shade) = AllowanceFor(name);

            Assert.True(diff.Ink <= ink && diff.Shade <= shade,
                        diff.Describe(name, windows, ours, w, h));

            // Ink is ratcheted BOTH ways -- an improvement has to be written down, so the table stays
            // an honest record of what differs. Shade is only an upper bound, because it is not
            // reproducible to the pixel: Windows animates a ProgressBar, so two runs a second apart
            // disagree about a couple of thousand of its shades while its ink is identical. Holding
            // shade to an exact number made the suite fail on the passage of time.
            Assert.True(diff.Ink >= ink,
                        $"'{name}' now matches Windows in {ink - diff.Ink} more pixels of ink than "
                        + "Allowed says it does -- lower its entry, or drop it.");
        }

        private static string SafeRead(string path)
            => File.Exists(path) ? File.ReadAllText(path) : "";

        private static byte[] ReadRaw(string path, out int width, out int height)
        {
            using var br = new BinaryReader(File.OpenRead(path));
            width = br.ReadInt32();
            height = br.ReadInt32();
            return br.ReadBytes(width * height * 4);
        }

        // ---- the comparison ------------------------------------------------------------------

        private readonly struct Difference
        {
            /// <summary>Anything at least this far apart in any channel is a different colour, and
            /// anything closer is the two rasterizers rounding the same colour differently.</summary>
            private const int ShadeTolerance = 24;

            public readonly int Ink;
            public readonly int Shade;
            public readonly int Worst;

            private Difference(int ink, int shade, int worst)
            {
                Ink = ink; Shade = shade; Worst = worst;
            }

            public static Difference Between(byte[] a, byte[] b, int w, int h)
            {
                int ink = 0, shade = 0, worst = 0;
                for (int i = 0; i < w * h; i++)
                {
                    int o = i * 4;
                    bool aInk = IsInk(a, o), bInk = IsInk(b, o);
                    if (aInk != bInk) { ink++; continue; }
                    if (!aInk) continue;

                    int d = Math.Max(Math.Abs(a[o] - b[o]),
                            Math.Max(Math.Abs(a[o + 1] - b[o + 1]), Math.Abs(a[o + 2] - b[o + 2])));
                    if (d > worst) worst = d;
                    if (d > ShadeTolerance) shade++;
                }
                return new Difference(ink, shade, worst);
            }

            /// <summary>Whether this pixel has anything drawn on it, as opposed to the ground the
            /// bitmap was cleared to. Both sides are cleared to the same colour, so "differs from the
            /// ground" is the same question on both.</summary>
            private static bool IsInk(byte[] px, int o)
            {
                // SystemColors.Control, which both sides cleared to. Compared with a tolerance so a
                // control that fills its background with the very same colour does not count as ink
                // on one side and not the other over a rounding of one.
                const int GroundB = 240, GroundG = 240, GroundR = 240;
                return Math.Abs(px[o] - GroundB) > 6
                    || Math.Abs(px[o + 1] - GroundG) > 6
                    || Math.Abs(px[o + 2] - GroundR) > 6;
            }

            public string Describe(string name, byte[] windows, byte[] ours, int w, int h)
            {
                var report = new System.Text.StringBuilder();
                report.AppendLine($"'{name}': {Ink} pixels drawn by one and not the other; {Shade} "
                                  + $"drawn by both in different colours (worst channel {Worst}/255).");
                report.AppendLine("windows:");
                report.Append(Map(windows, w, h));
                report.AppendLine("ours:");
                report.Append(Map(ours, w, h));
                report.AppendLine("difference (# = only one side drew, : = both drew, differently):");
                report.Append(DiffMap(windows, ours, w, h));
                return report.ToString();
            }

            private static string Map(byte[] px, int w, int h)
            {
                const string Ramp = "#*:. ";
                var text = new System.Text.StringBuilder();
                for (int y = 0; y < h; y++)
                {
                    text.Append("  ");
                    for (int x = 0; x < w; x++)
                    {
                        int o = (y * w + x) * 4;
                        int lum = (px[o] * 29 + px[o + 1] * 150 + px[o + 2] * 77) >> 8;
                        text.Append(Ramp[Math.Min(4, lum * 5 / 256)]);
                    }
                    text.AppendLine();
                }
                return text.ToString();
            }

            private static string DiffMap(byte[] a, byte[] b, int w, int h)
            {
                var text = new System.Text.StringBuilder();
                for (int y = 0; y < h; y++)
                {
                    text.Append("  ");
                    for (int x = 0; x < w; x++)
                    {
                        int o = (y * w + x) * 4;
                        bool ai = IsInk(a, o), bi = IsInk(b, o);
                        if (ai != bi) { text.Append('#'); continue; }
                        if (!ai) { text.Append(' '); continue; }
                        int d = Math.Max(Math.Abs(a[o] - b[o]),
                                Math.Max(Math.Abs(a[o + 1] - b[o + 1]), Math.Abs(a[o + 2] - b[o + 2])));
                        text.Append(d > ShadeTolerance ? ':' : '.');
                    }
                    text.AppendLine();
                }
                return text.ToString();
            }
        }

        // ---- the reference renders -------------------------------------------------------------

        /// <summary>Runs StockRenderer once for the whole suite and keeps the directory it wrote.
        /// Null when it could not be run, with the reason in <see cref="Failure"/>.</summary>
        private static class StockRenders
        {
            private static readonly Lazy<string?> s_value = new(Produce);
            public static string? Value => s_value.Value;
            public static string? Failure { get; private set; }

            private static string? Produce()
            {
                string? exe = Locate();
                if (exe is null)
                {
                    Failure = "StockRenderer.exe was not built; build ControlParity/StockRenderer "
                            + "to enable the control comparison";
                    return null;
                }

                string dir = Path.Combine(Path.GetTempPath(), "wf-control-parity",
                                          Guid.NewGuid().ToString("n"));
                var psi = new ProcessStartInfo(exe, "\"" + dir + "\"")
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                };
                using Process? p = Process.Start(psi);
                if (p is null) { Failure = "could not start StockRenderer"; return null; }
                string err = p.StandardError.ReadToEnd();
                p.StandardOutput.ReadToEnd();
                if (!p.WaitForExit(120_000)) { Failure = "StockRenderer did not finish"; return null; }
                if (p.ExitCode != 0) { Failure = $"StockRenderer exited {p.ExitCode}: {err}"; return null; }
                return dir;
            }

            private static string? Locate()
            {
                // Next to the test binary first (the project reference copies it there), then the
                // renderer's own output, so the suite runs from either.
                string here = AppContext.BaseDirectory;
                var candidates = new List<string>
                {
                    Path.Combine(here, "StockRenderer.exe"),
                    Path.Combine(here, "StockRenderer", "StockRenderer.exe"),
                };
                string? root = FindSourceDirectory(here);
                if (root is not null)
                {
                    candidates.Add(Path.Combine(root, "ControlParity", "StockRenderer", "bin",
                                                "Release", "net10.0-windows", "StockRenderer.exe"));
                    candidates.Add(Path.Combine(root, "ControlParity", "StockRenderer", "bin",
                                                "Debug", "net10.0-windows", "StockRenderer.exe"));
                }
                foreach (string c in candidates)
                    if (File.Exists(c)) return c;
                return null;
            }

            /// <summary>Walk up from the test binary to the project directory.</summary>
            private static string? FindSourceDirectory(string start)
            {
                for (DirectoryInfo? d = new(start); d is not null; d = d.Parent)
                    if (File.Exists(Path.Combine(d.FullName, "Wpf.WinFormsInterop.Tests.csproj")))
                        return d.FullName;
                return null;
            }
        }
    }
}
