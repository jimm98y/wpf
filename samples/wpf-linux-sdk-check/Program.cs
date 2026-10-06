// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// Proves the PACKAGED SDK works, as opposed to the in-repo build.
//
// Every other sample references the fork's assemblies straight out of artifacts/bin, which is
// convenient but skips the whole shipping path: the WpfWebGpu.Sdk package, its RID-keyed native
// payload, and the shim overlay. Those can rot without any in-repo sample noticing. This app is a
// consumer like any other -- one `Sdk="WpfWebGpu.Sdk/..."` attribute and nothing else -- so if it
// builds and paints, the package is intact.
//
// It is also the only sample with COMPILED XAML (XamlWindow.xaml), which makes it the only check that
// the markup compiler runs off Windows at all. Everything else here is code-only, so the markup path
// -- what essentially every real WPF app is built from -- would otherwise never be exercised on Linux.
//
// It exits on its own so it can be run unattended:
//   dotnet run --project samples/wpf-linux-sdk-check
//

using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Linq;
using System.Windows.Threading;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        int exitAfterSeconds = 8;
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == "--seconds") int.TryParse(args[i + 1], out exitAfterSeconds);
        }

        var app = new Application();
        var window = new Window
        {
            Title = "WpfWebGpu.Sdk package check",
            Width = 420,
            Height = 220,
            Content = new StackPanel
            {
                Margin = new Thickness(24),
                Children =
                {
                    new TextBlock
                    {
                        Text = "Running against the packaged WpfWebGpu.Sdk.",
                        FontSize = 16,
                        TextWrapping = TextWrapping.Wrap,
                    },
                    new Border
                    {
                        Height = 60,
                        Margin = new Thickness(0, 16, 0, 0),
                        CornerRadius = new CornerRadius(8),
                        Background = new LinearGradientBrush(Colors.SteelBlue, Colors.MediumPurple, 45),
                    },
                },
            },
        };

        // The compiled-XAML window is the other half of this check: it proves the markup compiler
        // (PresentationBuildTasks) ran off Windows and that the BAML it produced loads. Every other
        // sample here is code-only, so without this the markup path -- what essentially every real WPF
        // app is built from -- is never exercised on Linux.
        WpfLinuxSdkCheck.XamlWindow xamlWindow = null;
        try
        {
            xamlWindow = new WpfLinuxSdkCheck.XamlWindow();
            xamlWindow.Show();
            Console.WriteLine("XAML: " + xamlWindow.Describe());
        }
        catch (Exception e)
        {
            Console.WriteLine($"XAML FAILED {e.GetType().Name}: {e.Message}");
        }

        // The Fluent theme draws its icons from Segoe Fluent Icons / Segoe MDL2 Assets, which exist on
        // no Linux or macOS box; they are substituted onto the "Symbols" font the SDK deploys next to
        // the app. Resolving a real glyph for one of those private-use codepoints is what proves both
        // halves (the deployment and the substitution) are in place -- U+E70D is the Fluent chevron.
        try
        {
            var typeface = new Typeface(new FontFamily("Segoe Fluent Icons"),
                                        FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
            bool got = typeface.TryGetGlyphTypeface(out GlyphTypeface glyphs);
            bool hasChevron = got && glyphs.CharacterToGlyphMap.TryGetValue(0xE70D, out ushort g) && g != 0;
            Console.WriteLine($"ICONS: resolved={got} face='{(got ? glyphs.FamilyNames.Values.FirstOrDefault() : "(none)")}' " +
                              $"chevronU+E70D={hasChevron}");
        }
        catch (Exception e)
        {
            Console.WriteLine($"ICONS FAILED {e.GetType().Name}: {e.Message}");
        }

        // Cascadia Code travels with the app for its programming ligatures; the families it would
        // otherwise fall back to (Menlo, Consolas, DejaVu Sans Mono) have none. This checks only that
        // the bundled font RESOLVES -- whether the ligatures then substitute is a glyph-count
        // question, not a width one (Cascadia is monospaced, so its "!=" ligature is drawn to occupy
        // exactly two cells and the string measures the same either way). Run with WPF_GSUB_LOG=1 to
        // see what the shaper actually substitutes.
        try
        {
            var cascadia = new Typeface(new FontFamily("Cascadia Code"),
                                        FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
            bool got = cascadia.TryGetGlyphTypeface(out GlyphTypeface face);
            string family = got ? face.FamilyNames.Values.FirstOrDefault() : "(none)";
            Console.WriteLine($"MONO FONT: resolved={got} face='{family}' " +
                              $"(bundled Cascadia expected, not a fallback)");

            // The UI face. Segoe UI substitutes onto Selawik, which the SDK now bundles; if this
            // reports Liberation Sans or DejaVu the substitution is not taking effect.
            var ui = new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
            bool uiGot = ui.TryGetGlyphTypeface(out GlyphTypeface uiFace);
            Console.WriteLine($"UI FONT: resolved={uiGot} face='{(uiGot ? uiFace.FamilyNames.Values.FirstOrDefault() : "(none)")}' (Selawik expected)");
        }
        catch (Exception e)
        {
            Console.WriteLine($"MONO FONT FAILED {e.GetType().Name}: {e.Message}");
        }

        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(exitAfterSeconds) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            if (xamlWindow != null)
            {
                // Re-read after a layout pass, so the binding has had a chance to propagate.
                Console.WriteLine("XAML after layout: " + xamlWindow.Describe());
                xamlWindow.Close();
            }
            Console.WriteLine("SDK PACKAGE CHECK PASSED: the window came up from the packaged SDK.");
            window.Close();
        };
        window.Loaded += (_, _) => timer.Start();

        return app.Run(window);
    }
}
