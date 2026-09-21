// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// Gate for the Linux input-method path (zwp_text_input_v3).
//
//   dotnet run --project samples/wpf-linux-smoke -- --ime
//
// Opens a window whose TextBox takes focus immediately, then closes itself. That is enough to
// exercise the whole client half of the protocol -- enter, enable, set_content_type,
// set_cursor_rectangle, set_surrounding_text, commit -- and a malformed request in any of them is
// not a soft failure: the compositor answers a protocol error and drops the connection, which shows
// up here as the process dying instead of reporting "ok".
//
// Typing itself cannot be driven from a flag (only a real input method can produce a preedit), so
// run it with WPF_WAYLAND_LOG=1 and an IME engaged, and type into the box to watch the composition
// arrive.
//

using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

internal static class ImeProbe
{
    public static int Run(bool interactive)
    {
        var app = new Application();

        var box = new TextBox
        {
            FontSize = 20,
            AcceptsReturn = true,
            Margin = new Thickness(12),
            Height = 120,
        };

        var status = new TextBlock { Margin = new Thickness(12, 0, 12, 12) };

        var panel = new StackPanel();
        panel.Children.Add(new TextBlock
        {
            Margin = new Thickness(12, 12, 12, 0),
            Text = "Type here with an IME engaged (fcitx5/ibus). The composition should appear inline.",
            TextWrapping = TextWrapping.Wrap,
        });
        panel.Children.Add(box);
        panel.Children.Add(status);

        var window = new Window
        {
            Title = "WPF Linux IME probe",
            Width = 520,
            Height = 260,
            Content = panel,
        };

        window.Loaded += (_, _) =>
        {
            box.Focus();

            // Whether the channel came up is the platform layer's business to report: run with
            // WPF_WAYLAND_LOG=1 and it logs "zwp_text_input_v3 bound" and "text-input enabled",
            // or stays silent on a compositor that offers no input method.
            status.Text = "focused; run with WPF_WAYLAND_LOG=1 to see the text-input handshake";
            Console.WriteLine("[ime] text field focused");

            if (!interactive)
            {
                // Give the compositor a moment to answer; a protocol error arrives asynchronously
                // and would tear the connection down before this fires.
                var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
                timer.Tick += (_, _) =>
                {
                    timer.Stop();
                    Console.WriteLine("[ime] ok: survived enable/commit with no protocol error");
                    window.Close();
                };
                timer.Start();
            }
        };

        app.Run(window);
        return 0;
    }
}
