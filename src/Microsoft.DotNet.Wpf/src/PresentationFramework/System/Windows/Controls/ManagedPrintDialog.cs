// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// A print dialog drawn by WPF itself, for the one platform that needs one.
//
// Of the six heads, five need no dialog of ours. Windows has PrintDlgEx, and it is already wired up.
// macOS, iOS, Android and the browser all show their print UI at SUBMISSION, with the document
// already in it and a live preview -- which is better than a chooser beforehand, and is what native
// applications on those platforms do.
//
// Linux is the exception, and only because this port talks to CUPS directly rather than through the
// XDG print portal (CupsPrint says why). CUPS is a spooler, not a user interface; the desktop's own
// print dialog belongs to the portal. So this draws one.
//
// It works on Linux for the same reason ManagedMessageBox does: that head keeps the BLOCKING
// dispatcher loop, so ShowDialog genuinely pushes a nested frame. On iOS, Android and the browser
// nested frames throw outright, which is why this is never reached there.
//
// Deliberately small: a printer, a copy count, a page range. Everything else a print dialog usually
// offers -- duplex, trays, quality -- is a property of a driver, and this port has no driver model
// to populate it from. Offering controls that do nothing would be worse than not offering them.
//

using System.Collections.Generic;
using System.Windows.Media;
using MS.Internal.Interop;

namespace System.Windows.Controls
{
    internal static class ManagedPrintDialog
    {
        /// <summary>
        /// Shows the dialog and reports what the user chose. False means they cancelled, which the
        /// caller must treat as "do not print" rather than as "print with defaults".
        /// </summary>
        internal static bool Show(IList<PrinterInfo> printers, ref PrinterInfo chosen,
                                  ref int copies, ref int firstPage, ref int lastPage,
                                  int minPage, int maxPage, bool pageRangeEnabled)
        {
            if (printers == null || printers.Count == 0) return false;

            var list = new ComboBox
            {
                Margin = new Thickness(0, 4, 0, 12),
                MinWidth = 320,
            };

            foreach (PrinterInfo printer in printers)
            {
                list.Items.Add(printer.DisplayName ?? printer.Name);
            }

            int selected = printers.IndexOf(chosen);
            list.SelectedIndex = selected >= 0 ? selected : DefaultIndex(printers);

            var copiesBox = new TextBox
            {
                Text = copies.ToString(Globalization.CultureInfo.CurrentCulture),
                Width = 60,
                HorizontalAlignment = HorizontalAlignment.Left,
                Margin = new Thickness(0, 4, 0, 12),
            };

            var fromBox = new TextBox { Width = 60, Margin = new Thickness(0, 0, 8, 0) };
            var toBox = new TextBox { Width = 60 };

            var allPages = new RadioButton { Content = "All pages", IsChecked = true, GroupName = "range" };
            var rangePages = new RadioButton { Content = "Pages", GroupName = "range" };

            if (firstPage > 0 && lastPage > 0)
            {
                fromBox.Text = firstPage.ToString(Globalization.CultureInfo.CurrentCulture);
                toBox.Text = lastPage.ToString(Globalization.CultureInfo.CurrentCulture);
            }

            var rangeRow = new StackPanel { Orientation = Orientation.Horizontal };
            rangeRow.Children.Add(rangePages);
            rangeRow.Children.Add(new TextBlock { Text = "  from ", VerticalAlignment = VerticalAlignment.Center });
            rangeRow.Children.Add(fromBox);
            rangeRow.Children.Add(new TextBlock { Text = "to ", VerticalAlignment = VerticalAlignment.Center });
            rangeRow.Children.Add(toBox);

            var content = new StackPanel { Margin = new Thickness(20) };
            content.Children.Add(new TextBlock { Text = "Printer", FontWeight = FontWeights.SemiBold });
            content.Children.Add(list);
            content.Children.Add(new TextBlock { Text = "Copies", FontWeight = FontWeights.SemiBold });
            content.Children.Add(copiesBox);

            if (pageRangeEnabled)
            {
                content.Children.Add(new TextBlock { Text = "Range", FontWeight = FontWeights.SemiBold });
                content.Children.Add(allPages);
                content.Children.Add(rangeRow);
            }

            bool accepted = false;

            var print = new Button { Content = "Print", IsDefault = true, MinWidth = 88, Margin = new Thickness(0, 16, 8, 0) };
            var cancel = new Button { Content = "Cancel", IsCancel = true, MinWidth = 88, Margin = new Thickness(0, 16, 0, 0) };

            var buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
            };
            buttons.Children.Add(print);
            buttons.Children.Add(cancel);
            content.Children.Add(buttons);

            var window = new Window
            {
                Title = "Print",
                Content = content,
                SizeToContent = SizeToContent.WidthAndHeight,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                ResizeMode = ResizeMode.NoResize,
                ShowInTaskbar = false,
            };

            print.Click += (_, _) => { accepted = true; window.Close(); };
            cancel.Click += (_, _) => window.Close();

            if (Application.Current?.MainWindow != null && Application.Current.MainWindow != window)
            {
                window.Owner = Application.Current.MainWindow;
            }

            window.ShowDialog();

            if (!accepted) return false;

            int index = list.SelectedIndex;
            chosen = index >= 0 && index < printers.Count ? printers[index] : printers[0];

            copies = Parse(copiesBox.Text, 1, 1, 999);

            if (pageRangeEnabled && rangePages.IsChecked == true)
            {
                firstPage = Parse(fromBox.Text, minPage, minPage, maxPage);
                lastPage = Parse(toBox.Text, maxPage, firstPage, maxPage);
            }
            else
            {
                // Zero means "everything", which is not the same as page zero.
                firstPage = 0;
                lastPage = 0;
            }

            return true;
        }

        private static int DefaultIndex(IList<PrinterInfo> printers)
        {
            for (int i = 0; i < printers.Count; i++)
            {
                if (printers[i].IsDefault) return i;
            }
            return 0;
        }

        /// <summary>
        /// A number the user typed, clamped. Text boxes accept anything, and a print dialog is not
        /// the place to argue about it: an unreadable copy count means one copy, not an exception on
        /// the way to the printer.
        /// </summary>
        private static int Parse(string text, int fallback, int minimum, int maximum)
        {
            if (!int.TryParse(text, Globalization.NumberStyles.Integer,
                              Globalization.CultureInfo.CurrentCulture, out int value))
            {
                value = fallback;
            }

            if (value < minimum) value = minimum;
            if (maximum >= minimum && value > maximum) value = maximum;
            return value;
        }
    }
}
