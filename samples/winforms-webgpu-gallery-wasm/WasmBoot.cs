// WebAssembly entry point for the WinForms-on-WebGPU port. Pre-acquires the WebGPU adapter/device
// (Promise-based, must finish before the compositor starts), builds the same demo Form the desktop
// host uses, then runs a self-scheduling ASYNC pump (DoEvents + present + drain DOM input). Blocking
// Application.Run would freeze the browser tab, so the message loop is an awaited Task.Delay loop.

using System;
using System.Drawing;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Wpf.Interop.WebGpu;

internal static class WasmBoot
{
    private static async Task<int> Main(string[] args)
    {
        try
        {
            Console.WriteLine("WinFormsWasm: initializing WebGPU...");
            await WgpuBrowser.InitializeAsync();
            Console.WriteLine("WinFormsWasm: WebGPU ready, building form...");

            // Surface per-message exceptions as console text instead of Mono's default
            // ThreadExceptionDialog (which itself fails on the browser loading Mono.ico).
            Application.ThreadException += (s, e) => Console.WriteLine("THREADEX: " + e.Exception);
            Application.SetCompatibleTextRenderingDefault(false);   // driver forces GPU-raster text back on

            var f = new Form { Text = "WinForms on WebGPU", Width = 460, Height = 340, BackColor = Color.FromArgb(0xEC, 0xEC, 0xE4) };
            var gb = new GroupBox { Text = "Options", Left = 12, Top = 8, Width = 210, Height = 120 };
            var r1 = new RadioButton { Text = "Fast", Left = 14, Top = 24, Width = 120, Checked = true };
            var r2 = new RadioButton { Text = "Accurate", Left = 14, Top = 50, Width = 140 };
            var cb = new CheckBox { Text = "Verbose", Left = 14, Top = 80, Width = 140, Checked = true };
            gb.Controls.Add(r1); gb.Controls.Add(r2); gb.Controls.Add(cb);
            var combo = new ComboBox { Left = 236, Top = 12, Width = 200, DropDownStyle = ComboBoxStyle.DropDownList };
            combo.Items.AddRange(new object[] { "Direct3D 12", "Vulkan", "Metal", "OpenGL" }); combo.SelectedIndex = 2;
            var list = new ListBox { Left = 236, Top = 48, Width = 200, Height = 96 };
            list.Items.AddRange(new object[] { "alpha", "bravo", "charlie", "delta", "echo", "foxtrot",
                "golf", "hotel", "india", "juliet", "kilo", "lima", "mike", "november", "oscar" });
            list.SelectedIndex = 1;
            var lbl = new Label { Text = "Name:", Left = 12, Top = 150 };
            var tb = new TextBox { Left = 70, Top = 147, Width = 150, Text = "Ada Lovelace" };
            var prog = new ProgressBar { Left = 12, Top = 182, Width = 210, Height = 20, Value = 40 };
            var btn = new Button { Text = "Click me", Left = 12, Top = 220, Width = 120, Height = 32 };
            var counter = new Label { Text = "Clicks: 0", Left = 148, Top = 226, Width = 140 };
            // Near the form's bottom edge: its dropdown overflows the form, exercising the canvas-grow path.
            var combo2 = new ComboBox { Left = 236, Top = 300, Width = 200, DropDownStyle = ComboBoxStyle.DropDownList };
            combo2.Items.AddRange(new object[] { "Low", "Medium", "High", "Ultra", "Cinematic" }); combo2.SelectedIndex = 1;
            f.Controls.Add(gb); f.Controls.Add(combo); f.Controls.Add(list); f.Controls.Add(lbl);
            f.Controls.Add(tb); f.Controls.Add(prog); f.Controls.Add(btn); f.Controls.Add(counter); f.Controls.Add(combo2);

            int clicks = 0;
            btn.Click += (s, e) => { clicks++; counter.Text = $"Clicks: {clicks}"; counter.Invalidate(); prog.Value = Math.Min(100, prog.Value + 10); prog.Invalidate(); Console.WriteLine($"Button.Click -> {clicks}"); };
            cb.CheckedChanged += (s, e) => Console.WriteLine($"CheckBox -> {cb.Checked}");
            list.SelectedIndexChanged += (s, e) => Console.WriteLine($"List -> {list.SelectedItem}");

            f.CreateControl();
            f.Show();
            foreach (Control c in Flatten(f)) c.Invalidate(true);
            Application.DoEvents();

            IWinFormsHost host = new BrowserHost(f);
            host.Show();
            Console.WriteLine("WinFormsWasm: window shown; entering async pump.");

            // Self-scheduling message loop: never blocks the browser thread.
            while (true)
            {
                Application.DoEvents();
                host.Present();
                host.Pump();
                await Task.Delay(16).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"WinFormsWasm BOOT FAILED: {ex}");
            return 1;
        }
    }

    private static System.Collections.Generic.IEnumerable<Control> Flatten(Control c)
    { yield return c; foreach (Control ch in c.Controls) foreach (var g in Flatten(ch)) yield return g; }
}
