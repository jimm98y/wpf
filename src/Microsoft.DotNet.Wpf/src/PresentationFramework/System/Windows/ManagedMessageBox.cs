// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// A message box drawn by WPF itself, for platforms with no native one to call.
//
// Windows has user32 MessageBox and macOS has NSAlert, but Linux has neither: there is no message
// box in Wayland, and xdg-desktop-portal deliberately does not provide one (it exposes file
// choosers, settings and screenshots, not general dialogs). Shelling out to zenity or kdialog would
// mean a child process that may not be installed, with no parent-window association and no
// theming.
//
// So this draws one. It is the better answer regardless: it follows the app's own theme, it is
// faithful to MessageBoxButton/MessageBoxImage/DefaultResult, and it needs nothing installed.
//
// It works because the Linux and macOS heads keep the BLOCKING dispatcher loop, so ShowDialog
// genuinely pushes a nested frame (unlike the iOS/Android/browser heads, where nested frames throw
// and this approach would not be available).
//
// What it replaces is not another dialog -- it is silence. MessageBox.Show on any non-Windows,
// non-macOS platform previously returned the caller's default result WITHOUT DISPLAYING ANYTHING,
// so an app asking "Save changes before closing?" simply proceeded as though the user had answered.
//

using System.Collections.Generic;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace System.Windows
{
    internal static class ManagedMessageBox
    {
        internal static MessageBoxResult Show(string messageBoxText, string caption,
            (string Label, MessageBoxResult Result)[] buttons, MessageBoxImage icon,
            MessageBoxResult defaultResult)
        {
            MessageBoxResult result = FallbackResult(buttons, defaultResult);

            var panel = new StackPanel { Margin = new Thickness(24, 20, 24, 16) };

            var header = new StackPanel { Orientation = Orientation.Horizontal };
            if (TryGetIcon(icon, out string glyph, out Brush brush))
            {
                header.Children.Add(new TextBlock
                {
                    Text = glyph,
                    FontSize = 28,
                    Foreground = brush,
                    Margin = new Thickness(0, 0, 14, 0),
                    VerticalAlignment = VerticalAlignment.Top,
                });
            }
            header.Children.Add(new TextBlock
            {
                Text = messageBoxText ?? string.Empty,
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 420,
                VerticalAlignment = VerticalAlignment.Center,
            });
            panel.Children.Add(header);

            var buttonRow = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 22, 0, 0),
            };
            panel.Children.Add(buttonRow);

            var window = new Window
            {
                Title = caption ?? string.Empty,
                Content = panel,
                SizeToContent = SizeToContent.WidthAndHeight,
                ResizeMode = ResizeMode.NoResize,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                ShowInTaskbar = false,
            };

            // The owner keeps the dialog above the window it belongs to and lets the compositor
            // treat it as a child. Guard against adopting the dialog as its own owner.
            Window owner = SafeOwner();
            if (owner != null && !ReferenceEquals(owner, window))
            {
                window.Owner = owner;
            }

            bool closedByButton = false;
            foreach ((string label, MessageBoxResult buttonResult) in buttons)
            {
                MessageBoxResult captured = buttonResult;
                var b = new Button
                {
                    Content = label,
                    MinWidth = 88,
                    Margin = new Thickness(8, 0, 0, 0),
                    Padding = new Thickness(12, 4, 12, 4),
                    IsDefault = buttonResult == defaultResult,
                };
                b.Click += (_, _) =>
                {
                    result = captured;
                    closedByButton = true;
                    window.Close();
                };
                buttonRow.Children.Add(b);
            }

            // Escape answers the way a native message box does: Cancel/No where the button set
            // offers one, otherwise the declared default.
            window.KeyDown += (_, e) =>
            {
                if (e.Key != Key.Escape) return;
                result = EscapeResult(buttons, defaultResult);
                closedByButton = true;
                window.Close();
            };

            // Focus the default button so Enter and Space work immediately.
            window.Loaded += (_, _) =>
            {
                foreach (object child in buttonRow.Children)
                {
                    if (child is Button candidate && candidate.IsDefault)
                    {
                        candidate.Focus();
                        return;
                    }
                }
                if (buttonRow.Children.Count > 0 && buttonRow.Children[0] is Button first)
                {
                    first.Focus();
                }
            };

            window.ShowDialog();

            // Closed by the title-bar button rather than an answer: same as dismissing a native one.
            return closedByButton ? result : EscapeResult(buttons, defaultResult);
        }

        private static Window SafeOwner()
        {
            try
            {
                Application app = Application.Current;
                Window main = app?.MainWindow;
                // An owner that has not been shown yet cannot adopt a child.
                if (main != null && main.IsLoaded)
                {
                    return main;
                }
            }
            catch (InvalidOperationException)
            {
                // Application.Current belongs to another thread.
            }
            return null;
        }

        private static bool TryGetIcon(MessageBoxImage icon, out string glyph, out Brush brush)
        {
            // Text glyphs rather than bitmaps: no image assets to carry, and they scale with DPI.
            switch (icon)
            {
                case MessageBoxImage.Error:          // == Stop == Hand
                    glyph = "✖";                // heavy multiplication x
                    brush = Brushes.IndianRed;
                    return true;
                case MessageBoxImage.Warning:        // == Exclamation
                    glyph = "⚠";                // warning sign
                    brush = Brushes.Goldenrod;
                    return true;
                case MessageBoxImage.Question:
                    glyph = "?";
                    brush = Brushes.SteelBlue;
                    return true;
                case MessageBoxImage.Information:    // == Asterisk
                    glyph = "ℹ";                // information source
                    brush = Brushes.SteelBlue;
                    return true;
                default:
                    glyph = null;
                    brush = null;
                    return false;
            }
        }

        private static MessageBoxResult EscapeResult(
            (string Label, MessageBoxResult Result)[] buttons, MessageBoxResult defaultResult)
        {
            foreach ((string _, MessageBoxResult r) in buttons)
            {
                if (r == MessageBoxResult.Cancel) return r;
            }
            foreach ((string _, MessageBoxResult r) in buttons)
            {
                if (r == MessageBoxResult.No) return r;
            }
            return FallbackResult(buttons, defaultResult);
        }

        private static MessageBoxResult FallbackResult(
            (string Label, MessageBoxResult Result)[] buttons, MessageBoxResult defaultResult)
        {
            foreach ((string _, MessageBoxResult r) in buttons)
            {
                if (r == defaultResult) return defaultResult;
            }
            return buttons.Length > 0 ? buttons[0].Result : MessageBoxResult.None;
        }
    }
}
