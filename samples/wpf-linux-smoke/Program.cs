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
    private static int Main(string[] args)
    {
        var app = new Application();
        Window window = BuildWindow();

        // --auto-popup opens the ComboBox dropdown a moment after the window maps, so popup
        // PLACEMENT can be checked from a log without anyone clicking. Placement is the part of the
        // Wayland head with no absolute coordinates to fall back on (see WaylandWindow's header),
        // so it is the piece most worth being able to test unattended.
        if (Array.IndexOf(args, "--auto-popup") >= 0)
        {
            var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                if (window.Content is Panel root)
                {
                    foreach (object child in root.Children)
                    {
                        if (child is not ComboBox cb) continue;

                        // WPF's own answer for where the dropdown should sit: the ComboBox's
                        // bottom-left in screen coordinates. Printing it lets the placement the
                        // backend computes be checked against the placement WPF asked for, instead
                        // of judging by eye.
                        Point topLeft = cb.PointToScreen(new Point(0, 0));
                        Point bottomLeft = cb.PointToScreen(new Point(0, cb.ActualHeight));
                        Console.Error.WriteLine(
                            $"[probe] combo topLeft(screen)={topLeft.X:0},{topLeft.Y:0} " +
                            $"bottomLeft(screen)={bottomLeft.X:0},{bottomLeft.Y:0} " +
                            $"actualHeight={cb.ActualHeight:0.#}");

                        cb.IsDropDownOpen = true;
                        break;
                    }
                }
            };
            timer.Start();
        }

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

        // --- desktop integration (gate G8) ---------------------------------------------------

        var clipboardBox = new TextBox { Width = 260, HorizontalAlignment = HorizontalAlignment.Left, Text = "copy me" };

        var copyButton = new Button { Content = "Copy", Width = 90 };
        copyButton.Click += (_, _) =>
        {
            Clipboard.SetText(clipboardBox.Text);
            status.Text = $"copied \"{clipboardBox.Text}\" — paste it into another app";
        };

        var pasteButton = new Button { Content = "Paste", Width = 90 };
        pasteButton.Click += (_, _) =>
        {
            string text = Clipboard.GetText();
            status.Text = string.IsNullOrEmpty(text) ? "clipboard is empty" : $"pasted: \"{text}\"";
        };

        var messageBoxButton = new Button { Content = "MessageBox", Width = 110 };
        messageBoxButton.Click += (_, _) =>
        {
            MessageBoxResult answer = MessageBox.Show(
                "This message box is drawn by WPF itself — Linux has no native one, and the portal does not provide one.",
                "MessageBox", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
            status.Text = $"message box answered: {answer}";
        };

        var openFileButton = new Button { Content = "Open file…", Width = 110 };
        openFileButton.Click += (_, _) =>
        {
            var dialog = new Microsoft.Win32.OpenFileDialog { Title = "Pick a file" };
            status.Text = dialog.ShowDialog() == true
                ? $"chose: {dialog.FileName}"
                : "file dialog cancelled";
        };

        var themeButton = new Button { Content = "Read theme", Width = 110 };
        themeButton.Click += (_, _) =>
            status.Text = $"desktop colour scheme: {(IsDarkTheme() ? "dark" : "light")}";

        var integrationRow1 = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (UIElement b in new UIElement[] { copyButton, pasteButton })
        {
            ((FrameworkElement)b).Margin = new Thickness(0, 0, 8, 0);
            integrationRow1.Children.Add(b);
        }

        var integrationRow2 = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (UIElement b in new UIElement[] { messageBoxButton, openFileButton, themeButton })
        {
            ((FrameworkElement)b).Margin = new Thickness(0, 0, 8, 0);
            integrationRow2.Children.Add(b);
        }

        var panel = new StackPanel { Margin = new Thickness(20) };
        panel.Children.Add(heading);
        panel.Children.Add(new TextBlock
        {
            Text = "Right-click anywhere for a context menu.",
            Margin = new Thickness(0, 4, 0, 12),
            Foreground = Brushes.DimGray,
        });
        foreach (UIElement child in new UIElement[]
                 { button, textBox, combo, list, dialogButton, clipboardBox, integrationRow1, integrationRow2 })
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

    /// <summary>
    /// WPF's own view of the desktop theme. SystemParameters has no cross-platform colour-scheme
    /// property, so this reads what ThemeManager reads: the Fluent theme's active dictionary follows
    /// the same signal (xdg-desktop-portal's org.freedesktop.appearance color-scheme).
    /// </summary>
    private static bool IsDarkTheme()
    {
        Color background = SystemColors.WindowColor;
        return (background.R + background.G + background.B) / 3 < 128;
    }
}
