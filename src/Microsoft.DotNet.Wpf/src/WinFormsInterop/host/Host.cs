using System;
using System.Drawing;
using System.Threading;
using System.Windows.Forms;

internal static class Host
{
    private static int Main(string[] args)
    {
        int seconds = args.Length > 0 && int.TryParse(args[0], out int s) ? s : 0;

        var f = new Form { Text = "WinForms on macOS", Width = 460, Height = 340, BackColor = Color.FromArgb(0xEC, 0xEC, 0xE4) };
        var gb = new GroupBox { Text = "Options", Left = 12, Top = 8, Width = 210, Height = 120 };
        var r1 = new RadioButton { Text = "Fast", Left = 14, Top = 24, Width = 120, Checked = true };
        var r2 = new RadioButton { Text = "Accurate", Left = 14, Top = 50, Width = 140 };
        var cb = new CheckBox { Text = "Verbose", Left = 14, Top = 80, Width = 140, Checked = true };
        gb.Controls.Add(r1); gb.Controls.Add(r2); gb.Controls.Add(cb);
        var combo = new ComboBox { Left = 236, Top = 12, Width = 200, DropDownStyle = ComboBoxStyle.DropDownList };
        combo.Items.AddRange(new object[] { "Direct3D 12", "Vulkan", "Metal", "OpenGL" }); combo.SelectedIndex = 2;
        var list = new ListBox { Left = 236, Top = 48, Width = 200, Height = 96 };
        list.Items.AddRange(new object[] { "alpha", "bravo", "charlie", "delta", "echo" }); list.SelectedIndex = 1;
        var lbl = new Label { Text = "Name:", Left = 12, Top = 150 };
        var tb = new TextBox { Left = 70, Top = 147, Width = 150, Text = "Ada Lovelace" };
        var prog = new ProgressBar { Left = 12, Top = 182, Width = 210, Height = 20, Value = 40 };
        var btn = new Button { Text = "Click me", Left = 12, Top = 220, Width = 120, Height = 32 };
        var counter = new Label { Text = "Clicks: 0", Left = 148, Top = 226, Width = 140 };
        f.Controls.Add(gb); f.Controls.Add(combo); f.Controls.Add(list); f.Controls.Add(lbl);
        f.Controls.Add(tb); f.Controls.Add(prog); f.Controls.Add(btn); f.Controls.Add(counter);

        int clicks = 0;
        btn.Click += (s2, e) => { clicks++; counter.Text = $"Clicks: {clicks}"; counter.Invalidate(); prog.Value = Math.Min(100, prog.Value + 10); prog.Invalidate(); Console.WriteLine($"Button.Click -> {clicks}"); };
        cb.CheckedChanged += (s2, e) => Console.WriteLine($"CheckBox -> {cb.Checked}");
        list.SelectedIndexChanged += (s2, e) => Console.WriteLine($"List -> {list.SelectedItem}");

        // Force initial paint of the whole tree, then host on screen.
        f.CreateControl();
        f.Show();
        foreach (Control c in Flatten(f)) c.Invalidate(true);
        Application.DoEvents();

        var host = new CocoaHost(f);
        host.Show();
        Console.WriteLine("WinForms window shown on macOS. Interact with it, or wait for timeout.");

        // Self-test (arg "selftest"): inject clicks through the driver like real NSEvent clicks do,
        // proving the on-screen window is interactive (click -> handler -> label update -> re-present).
        bool selftest = Array.IndexOf(args, "selftest") >= 0;

        var sw = System.Diagnostics.Stopwatch.StartNew();
        int frame = 0;
        while (true)
        {
            if (selftest && frame == 10) { host.InjectClickScreen(btn.Left + btn.Width/2, btn.Top + btn.Height/2); }
            if (selftest && frame == 20) { host.InjectClickScreen(btn.Left + btn.Width/2, btn.Top + btn.Height/2); }
            if (selftest && frame == 30) { var p = cb.PointToScreen(new Point(8, cb.Height/2)); host.InjectClickScreen(p.X, p.Y); }
            Application.DoEvents();     // drain any WinForms-side messages (repaints from Invalidate)
            host.Present();             // reflect current state on screen
            if (!host.Pump()) break;    // route OS input; false when the window closes
            if (selftest && frame == 40) { host.SaveFrame("/private/tmp/claude-501/-Users-lukasvolf-Documents-GitHub/7c297698-2b54-41c1-8a9a-246a02fe657a/scratchpad/wf-onscreen.png"); Console.WriteLine("saved on-screen frame"); break; }
            if (seconds > 0 && sw.Elapsed.TotalSeconds > seconds) break;
            frame++;
            Thread.Sleep(16);
        }
        Console.WriteLine($"done. total clicks={clicks} checkbox={cb.Checked}");
        return selftest ? (clicks == 2 && !cb.Checked ? 0 : 5) : 0;
    }

    private static System.Collections.Generic.IEnumerable<Control> Flatten(Control c)
    { yield return c; foreach (Control ch in c.Controls) foreach (var g in Flatten(ch)) yield return g; }
}
