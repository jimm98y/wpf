// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// The smallest WPF app that exercises the Linux/Wayland head end to end, used for gates G4-G8.
//
// Deliberately code-only (no XAML, no markup compiler) and deliberately small: when something is
// wrong in the platform layer, a failure here points at one thing, whereas the same failure inside
// the full gallery is buried under thirty controls. Each control below is here because it proves a
// specific part of the head:
//
//   TextBlock  -> the managed font stack found a font and glyph rendering works
//   Button     -> mouse enter/leave/press/release and WPF's capture path
//   TextBox    -> keyboard focus, keysym->virtual-key mapping, text input, the I-beam cursor
//   ComboBox   -> a POPUP: its own surface, positioned through xdg_positioner
//   ContextMenu-> a popup opened at the pointer, exercising the coordinate write-back
//   ListBox    -> wheel scrolling (axis_value120)
//   the dialog -> a nested dispatcher frame, which only works because Linux keeps the blocking loop
//
//   dotnet run --project samples/wpf-linux-smoke
//

using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

internal static class Program
{
    [STAThread]
    private static int Main()
    {
        var app = new Application();
        Window window = BuildWindow();
        return app.Run(window);
    }

    private static Window BuildWindow()
    {
        var status = new TextBlock
        {
            Text = "ready",
            Margin = new Thickness(0, 8, 0, 0),
            Foreground = Brushes.DimGray,
        };

        var heading = new TextBlock
        {
            Text = "WPF on WebGPU — Linux/Wayland",
            FontSize = 20,
            FontWeight = FontWeights.SemiBold,
        };

        var button = new Button { Content = "Click me", Width = 120, HorizontalAlignment = HorizontalAlignment.Left };
        int clicks = 0;
        button.Click += (_, _) => status.Text = $"button clicked {++clicks}x";

        var textBox = new TextBox { Width = 260, HorizontalAlignment = HorizontalAlignment.Left };
        textBox.TextChanged += (_, _) => status.Text = $"typed: \"{textBox.Text}\"";

        var combo = new ComboBox { Width = 180, HorizontalAlignment = HorizontalAlignment.Left, SelectedIndex = 0 };
        foreach (string item in new[] { "Popup item one", "Popup item two", "Popup item three" })
            combo.Items.Add(item);
        combo.SelectionChanged += (_, _) => status.Text = $"combo: {combo.SelectedItem}";

        var list = new ListBox { Height = 90, Width = 260, HorizontalAlignment = HorizontalAlignment.Left };
        for (int i = 1; i <= 30; i++) list.Items.Add($"Scrollable row {i}");

        var dialogButton = new Button { Content = "Modal dialog", Width = 120, HorizontalAlignment = HorizontalAlignment.Left };
        dialogButton.Click += (_, _) =>
        {
            // A nested dispatcher frame. Works on Linux (and macOS) because the blocking run loop is
            // kept, unlike the iOS/Android/browser heads where ShowDialog throws.
            var dialog = new Window
            {
                Title = "Nested frame",
                Width = 320,
                Height = 160,
                Owner = Application.Current.MainWindow,
                Content = new StackPanel
                {
                    Margin = new Thickness(16),
                    Children =
                    {
                        new TextBlock { Text = "This is a modal dialog on a nested dispatcher frame.", TextWrapping = TextWrapping.Wrap },
                    },
                },
            };
            dialog.ShowDialog();
            status.Text = "modal dialog closed";
        };

        var panel = new StackPanel { Margin = new Thickness(20) };
        panel.Children.Add(heading);
        panel.Children.Add(new TextBlock
        {
            Text = "Right-click anywhere for a context menu.",
            Margin = new Thickness(0, 4, 0, 12),
            Foreground = Brushes.DimGray,
        });
        foreach (UIElement child in new UIElement[] { button, textBox, combo, list, dialogButton })
        {
            panel.Children.Add(child);
            ((FrameworkElement)child).Margin = new Thickness(0, 0, 0, 10);
        }
        panel.Children.Add(status);

        var menu = new ContextMenu();
        foreach (string item in new[] { "Context item A", "Context item B" })
        {
            var entry = new MenuItem { Header = item };
            string captured = item;
            entry.Click += (_, _) => status.Text = $"context menu: {captured}";
            menu.Items.Add(entry);
        }

        var window = new Window
        {
            Title = "WPF Linux smoke test",
            Width = 520,
            Height = 560,
            Content = panel,
            ContextMenu = menu,
        };

        window.SourceInitialized += (_, _) =>
        {
            // Setting Title after SourceInitialized used to throw DllNotFoundException off Windows
            // (SetWindowText P/Invoked user32 unguarded), so this doubles as a regression check.
            window.Title = "WPF Linux smoke test — initialized";
        };

        return window;
    }
}
