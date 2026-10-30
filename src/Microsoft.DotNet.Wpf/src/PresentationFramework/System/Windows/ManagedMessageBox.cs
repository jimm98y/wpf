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
// What it replaces is not another dialog -- it is silence. MessageBox.Show on any non-Windows,
// non-macOS platform previously returned the caller's default result WITHOUT DISPLAYING ANYTHING,
// so an app asking "Save changes before closing?" simply proceeded as though the user had answered.
//
// The same prompt is offered two ways, because the heads differ in one respect only: whether the
// caller can be made to wait. Show pushes a nested dispatcher frame, which Windows, macOS and Linux
// allow. ShowAsync awaits instead, which is the only shape available on iOS, Android and the browser,
// where the host owns the run loop and a nested frame would deadlock it. Everything between those
// two -- the window, the buttons, the icon, the keyboard handling, what a dismissal answers -- is
// built once, by Build, so the two cannot drift apart.
//
// Verified on the iPad simulator: ShowAsync puts up a centred card over a scrim, with working Yes
// and No buttons, and the app behind it is untouched.
//
// That is worth writing down because it looked broken for a long time, and the reason was how it
// was being LOOKED AT. An iOS app renders nothing for several seconds after launch, so a screenshot
// taken on a fixed delay is solid black and indistinguishable from a rendering fault. Sampling one
// relaunch every few seconds: 188 distinct colours, then 24, 15, 20, 20, then 636, 636, 636, with
// nothing changing but the clock. Poll until the screen settles; never trust one early frame.
//
// One real find from that hunt, for whoever needs a screen size on this head:
// SystemParameters.PrimaryScreenWidth/Height are a desktop stub, reporting 1920x1080 on an iPhone
// whose screen is 402x874. The owner window is the honest ruler, which is what Build uses.
//

using System.Collections.Generic;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace System.Windows
{
    internal static class ManagedMessageBox
    {
        /// <summary>
        /// Blocking: pushes a nested frame, and so is only available on the heads whose dispatcher
        /// loop can be re-entered. <see cref="ShowAsync"/> is the same prompt for the others.
        /// </summary>
        internal static MessageBoxResult Show(string messageBoxText, string caption,
            (string Label, MessageBoxResult Result)[] buttons, MessageBoxImage icon,
            MessageBoxResult defaultResult)
        {
            Prompt prompt = Build(messageBoxText, caption, buttons, icon, defaultResult);
            prompt.Window.ShowDialog();
            return prompt.Answer;
        }

        /// <summary>
        /// The same prompt, awaited instead of blocked on -- the only shape available where the host
        /// owns the run loop and a nested frame would deadlock it (iOS, Android, browser). The window
        /// is a real modal: every other window on the thread is disabled while it is up. What differs
        /// is purely how the caller waits.
        /// </summary>
        internal static async System.Threading.Tasks.Task<MessageBoxResult> ShowAsync(
            string messageBoxText, string caption,
            (string Label, MessageBoxResult Result)[] buttons, MessageBoxImage icon,
            MessageBoxResult defaultResult)
        {
            Prompt prompt = Build(messageBoxText, caption, buttons, icon, defaultResult);
            await prompt.Window.ShowDialogAsync().ConfigureAwait(true);
            return prompt.Answer;
        }

        /// <summary>A built, not-yet-shown prompt and the answer it will produce.</summary>
        private sealed class Prompt
        {
            internal Window Window;
            internal MessageBoxResult Result;
            internal MessageBoxResult Dismissed;
            internal bool Answered;

            /// <summary>
            /// Closed by the title-bar button rather than by an answer counts as a dismissal, the
            /// same as a native message box treats it.
            /// </summary>
            internal MessageBoxResult Answer => Answered ? Result : Dismissed;
        }

        private static Prompt Build(string messageBoxText, string caption,
            (string Label, MessageBoxResult Result)[] buttons, MessageBoxImage icon,
            MessageBoxResult defaultResult)
        {
            var prompt = new Prompt
            {
                Result = FallbackResult(buttons, defaultResult),
                Dismissed = EscapeResult(buttons, defaultResult),
            };

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

            Window owner = SafeOwner();

            // A window is a rectangle the app asks for on every head -- on iOS and Android too, where a
            // second window floats over the app's own (UIKitWindow, AndroidWindow) as it does in the
            // browser -- so SizeToContent plus CenterOwner gives the small centred box everyone expects.
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
            if (owner != null && !ReferenceEquals(owner, window))
            {
                window.Owner = owner;
            }

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
                    prompt.Result = captured;
                    prompt.Answered = true;
                    window.Close();
                };
                buttonRow.Children.Add(b);
            }

            // Escape answers the way a native message box does: Cancel/No where the button set
            // offers one, otherwise the declared default.
            window.KeyDown += (_, e) =>
            {
                if (e.Key != Key.Escape) return;
                prompt.Result = prompt.Dismissed;
                prompt.Answered = true;
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

            prompt.Window = window;
            return prompt;
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
