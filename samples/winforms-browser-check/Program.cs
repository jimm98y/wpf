// A WinForms app as WinForms apps are written: Application.Run(mainForm), a button handler, a modal
// dialog and a message box. A timer clicks through it once it is up, so a run with nobody at the page
// still shows each part working; every step is reported on the console ("winforms:" lines).

using System;
using System.Drawing;
using System.Windows.Forms;

public static class Program
{
    private static int s_clicks;

    [STAThread]
    public static void Main()
    {
        Application.EnableVisualStyles();
        var form = new Form { Text = "WinForms browser check", ClientSize = new Size(480, 260) };
        var label = new Label { Text = "Not clicked yet", Location = new Point(20, 20), AutoSize = true };
        var count = new Button { Text = "Click me", Location = new Point(20, 60), Size = new Size(120, 32) };
        var dialog = new Button { Text = "Dialog...", Location = new Point(160, 60), Size = new Size(120, 32) };
        var text = new TextBox { Location = new Point(20, 110), Width = 260, Text = "Type here" };
        count.Click += (_, _) => { s_clicks++; label.Text = $"Clicked {s_clicks} time(s)"; Log($"button clicked ({s_clicks})"); };
        dialog.Click += (_, _) => ShowDialogBox(form);
        form.Controls.AddRange(new Control[] { label, count, dialog, text });
        form.Shown += (_, _) =>
        {
            Log($"main form shown at {form.Bounds}, client {form.ClientSize}");
            var t = new Timer { Interval = 500 };
            int step = 0;
            t.Tick += (_, _) =>
            {
                switch (step++)
                {
                    case 0: count.PerformClick(); break;
                    case 1: count.PerformClick(); break;
                    case 2: dialog.PerformClick(); break;
                    case 3: Log("message box: " + MessageBox.Show("Hello from WinForms in the browser", "Message")); break;
                    default: t.Stop(); Log($"done; label says \"{label.Text}\""); break;
                }
            };
            t.Start();
        };
        Application.Run(form);
        Log("Application.Run returned");
    }

    private static void ShowDialogBox(Form owner)
    {
        var dlg = new Form { Text = "A dialog", ClientSize = new Size(300, 120), StartPosition = FormStartPosition.CenterParent };
        var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, Location = new Point(110, 70) };
        dlg.Controls.Add(ok);
        dlg.AcceptButton = ok;
        dlg.FormClosed += (_, _) => Log($"dialog closed with {dlg.DialogResult}");
        dlg.Shown += (_, _) => { Log($"dialog shown at {dlg.Bounds}"); var t = new Timer { Interval = 300 }; t.Tick += (_, _) => { t.Stop(); ok.PerformClick(); }; t.Start(); };
        DialogResult r = dlg.ShowDialog(owner);
        Log($"ShowDialog returned {r}");
    }

    private static void Log(string s) => Console.WriteLine("winforms: " + s);
}
