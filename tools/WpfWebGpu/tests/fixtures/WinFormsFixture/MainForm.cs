using System.Drawing;
using System.Drawing.Drawing2D;

namespace WinFormsFixture;

// Exercises the type identities a .NET-compiled WinForms library binds to: Color/Point/Size/Rectangle
// (System.Drawing.Primitives), Font/Graphics/Bitmap (System.Drawing.Common), Message/Padding
// (System.Windows.Forms.Primitives). Any of them unresolved is a MissingMethodException long before
// the window shows, so reaching OnShown at all is most of the test.
internal sealed class MainForm : Form
{
    private readonly string[] _args;
    private readonly Label _label;
    private readonly Swatch _swatch;
    public int ExitCode { get; private set; } = 1;

    public MainForm(string[] args)
    {
        _args = args;
        Text = "WinFormsFixture";
        ClientSize = new Size(480, 320);
        BackColor = Color.WhiteSmoke;
        Padding = new Padding(12);

        _label = new Label { Text = "Hello from an unmodified WinForms app", AutoSize = true, Location = new Point(16, 16), Font = new Font(Font.FontFamily, 14f) };
        var button = new Button { Text = "A button", Location = new Point(16, 60), Size = new Size(120, 32) };
        var box = new TextBox { Text = "Some text", Location = new Point(16, 104), Width = 240 };
        _swatch = new Swatch { Location = new Point(280, 60), Size = new Size(160, 120) };
        Controls.AddRange(new Control[] { _label, button, box, _swatch });
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        var failures = new List<string>();
        string dir = Path.GetDirectoryName(typeof(MainForm).Assembly.Location)!;
        Console.WriteLine($"fixture: WinForms {typeof(Form).Assembly.FullName} from {typeof(Form).Assembly.Location}");
        Console.WriteLine($"fixture: Color {typeof(Color).Assembly.GetName().Name}, Font {typeof(Font).Assembly.GetName().Name}, Message {typeof(Message).Assembly.GetName().Name}");
        Console.WriteLine($"fixture: BaseDirectory {AppContext.BaseDirectory}");

        if (Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory) != dir)
            failures.Add($"BaseDirectory is {AppContext.BaseDirectory}, expected {dir}");
        if (_label.Width <= 0) failures.Add("the label was not laid out");
        if (BackColor.ToArgb() != Color.WhiteSmoke.ToArgb()) failures.Add($"BackColor round-trip gave {BackColor}");

        using (var bmp = new Bitmap(32, 32))
        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(Color.Red);
            if (bmp.GetPixel(4, 4).ToArgb() != Color.Red.ToArgb()) failures.Add($"Bitmap pixel is {bmp.GetPixel(4, 4)}");
        }

        foreach (string f in failures) Console.WriteLine($"fixture: FAIL {f}");
        Console.WriteLine(failures.Count == 0 ? "fixture: PASS" : "fixture: FAILED");
        ExitCode = failures.Count == 0 ? 0 : 1;

        int i = Array.IndexOf(_args, "--seconds");
        if (i >= 0 && i + 1 < _args.Length && double.TryParse(_args[i + 1], out double seconds))
        {
            var timer = new System.Windows.Forms.Timer { Interval = (int)(seconds * 1000) };
            timer.Tick += (_, _) => { timer.Stop(); Close(); };
            timer.Start();
        }
    }

    // A custom control the way third-party libraries write them: owner-drawn, overriding WndProc.
    private sealed class Swatch : Control
    {
        public int Messages { get; private set; }

        public Swatch() => SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer, true);

        protected override void WndProc(ref Message m)
        {
            Messages++;
            base.WndProc(ref m);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var r = ClientRectangle;
            using var brush = new LinearGradientBrush(r, Color.SteelBlue, Color.LightSkyBlue, LinearGradientMode.Vertical);
            e.Graphics.FillRectangle(brush, r);
            e.Graphics.DrawString("owner-drawn", Font, Brushes.White, new PointF(8, 8));
        }
    }
}
