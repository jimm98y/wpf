// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// The hosted-layout scenario HostedDpiTests compares, shared word for word with the stock recorder
// (it is compiled into a net10.0-windows probe against Microsoft.WindowsDesktop.App to record
// Fixtures/Dpi/stock-hosted-<dpi>.txt): a WindowsFormsHost in a per-monitor WPF window, and an
// ElementHost in a per-monitor form, each dumped as the bounds, fonts and DPIs of what they host.
// Only public API, so both builds run the same code.
//

using System;
using System.Drawing;
using System.Text;
using SWF = System.Windows.Forms;
using WPF = System.Windows;
using WPFC = System.Windows.Controls;

namespace Wpf.WinFormsInterop.Tests
{
    internal static class HostedDpiScenario
    {
        /// <summary>A WPF window with a WindowsFormsHost holding a small WinForms panel, off screen.</summary>
        internal static (WPF.Window Window, SWF.Integration.WindowsFormsHost Host) BuildWpfWindow()
        {
            var panel = new SWF.Panel { Name = "panel" };
            panel.Controls.Add(new SWF.Button { Name = "button", Text = "Button", Location = new Point(10, 10), AutoSize = true, UseCompatibleTextRendering = false });
            panel.Controls.Add(new SWF.Label { Name = "label", Text = "Label text", Location = new Point(10, 50), AutoSize = true, UseCompatibleTextRendering = false });
            panel.Controls.Add(new SWF.TextBox { Name = "textbox", Text = "Text", Location = new Point(10, 80), Width = 150 });
            panel.Controls.Add(new SWF.CheckBox { Name = "check", Text = "Check", Location = new Point(170, 10), AutoSize = true, UseCompatibleTextRendering = false });
            var host = new SWF.Integration.WindowsFormsHost { Width = 300, Height = 150, Child = panel };
            var canvas = new WPFC.Canvas();
            WPFC.Canvas.SetLeft(host, 10);
            WPFC.Canvas.SetTop(host, 10);
            canvas.Children.Add(host);
            var window = new WPF.Window
            {
                Title = "hosted dpi wfh",
                Width = 500, Height = 300,
                Left = 8000, Top = 80,
                WindowStartupLocation = WPF.WindowStartupLocation.Manual,
                ShowActivated = false, ShowInTaskbar = false,
                Content = canvas,
            };
            return (window, host);
        }

        /// <summary>A form with an ElementHost holding a small WPF panel, off screen.</summary>
        internal static (SWF.Form Form, SWF.Integration.ElementHost Host, WPFC.StackPanel Root) BuildForm()
        {
            var root = new WPFC.StackPanel();
            root.Children.Add(new WPFC.TextBlock { Text = "Hello WPF" });
            root.Children.Add(new WPFC.Button { Content = "WPF Button" });
            var host = new SWF.Integration.ElementHost { Location = new Point(10, 10), Size = new Size(300, 150), Child = root };
            var form = new SWF.Form
            {
                Text = "hosted dpi eh",
                StartPosition = SWF.FormStartPosition.Manual,
                Location = new Point(8000, 80),
                ClientSize = new Size(400, 300),
                ShowInTaskbar = false,
            };
            form.Controls.Add(host);
            return (form, host, root);
        }

        internal static string DumpWpfWindow(SWF.Integration.WindowsFormsHost host)
        {
            var sb = new StringBuilder();
            WPF.DpiScale d = WPF.Media.VisualTreeHelper.GetDpi(host);
            sb.Append($"wfh actual={host.ActualWidth:0.##}x{host.ActualHeight:0.##} dpi={d.PixelsPerInchX:0}\n");
            if (host.Child != null) Walk(sb, host.Child, 0);
            return sb.ToString();
        }

        internal static string DumpForm(SWF.Integration.ElementHost host, WPFC.StackPanel root)
        {
            var sb = new StringBuilder();
            sb.Append($"elementhost bounds={host.Bounds} dpi={host.DeviceDpi}\n");
            WPF.DpiScale d = WPF.Media.VisualTreeHelper.GetDpi(root);
            sb.Append($"root actual={root.ActualWidth:0.##}x{root.ActualHeight:0.##} wpfdpi={d.PixelsPerInchX:0}\n");
            foreach (WPF.FrameworkElement e in root.Children)
                sb.Append($"  {e.GetType().Name} actual={e.ActualWidth:0.##}x{e.ActualHeight:0.##}\n");
            return sb.ToString();
        }

        private static void Walk(StringBuilder sb, SWF.Control c, int depth)
        {
            sb.Append(new string(' ', depth * 2)).Append(c.Name.Length > 0 ? c.Name : c.GetType().Name)
              .Append($" bounds={c.Bounds} font={c.Font.SizeInPoints:0.##}pt/{c.Font.Height} dpi={c.DeviceDpi}\n");
            // The panel's own controls, not the parts a control keeps inside itself (which the two
            // builds make differently).
            if (depth == 0)
                foreach (SWF.Control k in c.Controls) Walk(sb, k, depth + 1);
        }
    }
}
