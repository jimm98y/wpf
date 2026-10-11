// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// What scale each layer of the stack believes it is drawing at.
//
//   dotnet run --project samples/wpf-linux-smoke -- --dpi
//
// Pixel snapping is only meaningful relative to a grid, and WPF derives that grid from the DPI it is
// told about: Border.OnRender rounds its thickness by DpiScaleX, and UseLayoutRounding rounds layout
// by the same. If the window renders at 1.5x while layout believes 1.0x, every rounding decision
// lands on a LOGICAL boundary rather than a device one, and every 1px line is soft no matter how
// much snapping the renderer does. This prints the three numbers so they can be compared.
//

using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

internal static class DpiProbe
{
    public static int Run()
    {
        var app = new Application();

        var text = new TextBlock { Margin = new Thickness(12), TextWrapping = TextWrapping.Wrap };
        var window = new Window { Title = "DPI probe", Width = 460, Height = 220, Content = text };

        window.Loaded += (_, _) =>
        {
            var report = new System.Text.StringBuilder();

            DpiScale dpi = VisualTreeHelper.GetDpi(window);
            report.AppendLine($"VisualTreeHelper.GetDpi:  x={dpi.DpiScaleX} y={dpi.DpiScaleY} ({dpi.PixelsPerInchX} dpi)");

            CompositionTarget? target = PresentationSource.FromVisual(window)?.CompositionTarget;
            if (target is not null)
            {
                Matrix toDevice = target.TransformToDevice;
                report.AppendLine($"CompositionTarget.TransformToDevice: M11={toDevice.M11} M22={toDevice.M22}");
            }
            else
            {
                report.AppendLine("CompositionTarget: (none)");
            }

            report.AppendLine($"SystemParameters: {SystemParameters.PrimaryScreenWidth}x{SystemParameters.PrimaryScreenHeight} logical");
            report.AppendLine($"Window.UseLayoutRounding: {window.UseLayoutRounding}");

            text.Text = report.ToString();
            Console.WriteLine(report.ToString());

            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            timer.Tick += (_, _) => { timer.Stop(); window.Close(); };
            timer.Start();
        };

        app.Run(window);
        return 0;
    }
}
