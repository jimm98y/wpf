// The sample's self-test, driven by an ordinary WinForms Timer so the app under test stays an
// ordinary Application.Run app.
//
// It deliberately needs NO mouse, so it is safe to run on a machine somebody is using: driving the
// physical pointer loses races against a human (and against any other UI test), which is exactly how
// it produced misleading results while this sample was being written. Pass "mouse" to add the
// real-pointer click as well.
//
// The assertions are the two things that can silently come apart, and did:
//   * where a hosted WPF element is DRAWN vs. where it is CLICKABLE -- the scene is composited at the
//     ElementHost's position in the host's frame, while input arrives at the hosted window's own
//     client rect, and nothing ties those together automatically;
//   * the host's client area vs. the surface presented into it -- if they differ the compositor
//     rescales the frame and every hit-test drifts, worse the further from the origin, which shows up
//     as clicks landing on the control above the cursor.

using System;
using System.Reflection;
using SD = System.Drawing;
using SWF = System.Windows.Forms;
using System.Windows.Forms.Integration;

internal static class SelfTest
{
    internal static void Arm(SWF.Form form, ElementHost host, Program.WpfPanel wpf,
                             SWF.Button wfButton, SWF.TrackBar track, SWF.RadioButton amber, SWF.CheckBox shadow,
                             bool mouseTest, Func<int> wfClicks, Func<int> wpfClicks, Action<int> setExit)
    {
        int step = 0;
        var timer = new SWF.Timer { Interval = 120 };
        timer.Tick += (s, e) =>
        {
            step++;
            switch (step)
            {
                case 3:
                    InjectDriverClick(wfButton);          // WinForms hit-testing, no physical mouse
                    track.Value = 88;                      // WinForms -> WPF
                    break;
                case 5:
                    if (mouseTest) MoveRealCursorToWpfButton(host, wpf);
                    break;
                case 6: if (mouseTest) OsInput.PressLeft(); break;
                case 7: if (mouseTest) OsInput.ReleaseLeft(); break;
                case 9:
                    Save(SelftestPng);
                    amber.Checked = true;                  // WinForms -> WPF: a shared brush's colour
                    shadow.Checked = false;                // WinForms -> WPF: a WPF effect
                    break;
                case 12:
                    Save(SelftestPng.Replace(".png", "-after.png"));
                    setExit(Evaluate(form, host, wpf, mouseTest, wfClicks(), wpfClicks()));
                    timer.Stop();
                    form.Close();
                    break;
            }
        };
        timer.Start();
    }

    private static int Evaluate(SWF.Form form, ElementHost host, Program.WpfPanel wpf,
                                bool mouseTest, int wfClicks, int wpfClicks)
    {
        bool aligned = host.TryGetAlignment(wpf.Button, out SD.Point drawn, out SD.Point clickable);
        int dx = drawn.X - clickable.X, dy = drawn.Y - clickable.Y;
        bool onTarget = aligned && Math.Abs(dx) <= 1 && Math.Abs(dy) <= 1;

        bool sized = OsInput.TryGetClientSize(HostWindow, out int cw, out int ch);
        float scale = HostScale;
        int sw = (int)Math.Round(form.Width * scale), sh = (int)Math.Round(form.Height * scale);
        bool oneToOne = sized && cw == sw && ch == sh;

        Console.WriteLine($"selftest: winforms clicks={wfClicks} wpfLive={host.IsChildLive} " +
                          $"wpf button drawn at {drawn} clickable at {clickable} (delta {dx},{dy}) " +
                          $"client {cw}x{ch} vs surface {sw}x{sh}" + (mouseTest ? $" wpf clicks={wpfClicks}" : ""));

        bool ok = wfClicks >= 1 && host.IsChildLive && onTarget && oneToOne && (!mouseTest || wpfClicks >= 1);
        return ok ? 0 : 5;
    }

    private static string SelftestPng =>
        Environment.GetEnvironmentVariable("WF_WEBGPU_SAVE")
        ?? System.IO.Path.Combine(System.IO.Path.GetTempPath(), "wpf-in-winforms.png");

    // The host saves a frame when WF_WEBGPU_SAVE is set; re-arm it to capture a second one.
    private static void Save(string path)
    {
        Environment.SetEnvironmentVariable("WF_WEBGPU_SAVE", path);
        ResetSaveLatch();
    }

    // ---- the host/driver seam, reached by reflection ------------------------------------------
    //
    // These are internals of the WinForms assembly (the driver's input injection, the host window it
    // presents into). A test reaching for them is fine; making them public API would not be.

    private static readonly Assembly s_swf = typeof(SWF.Control).Assembly;

    private static object Driver =>
        s_swf.GetType("System.Windows.Forms.XplatUI")
             .GetField("driver", BindingFlags.NonPublic | BindingFlags.Static).GetValue(null);

    private static Type EmbeddedScenes => s_swf.GetType("System.Windows.Forms.EmbeddedScenes");

    internal static IntPtr HostWindow =>
        (IntPtr)EmbeddedScenes.GetField("HostWindow", BindingFlags.NonPublic | BindingFlags.Static).GetValue(null);

    internal static float HostScale =>
        (float)EmbeddedScenes.GetField("HostScale", BindingFlags.NonPublic | BindingFlags.Static).GetValue(null);

    // A click at the control's centre, through the driver's own hit-testing — the same entry real
    // window messages take, minus the physical mouse.
    private static void InjectDriverClick(SWF.Control c)
    {
        SD.Point p = c.PointToScreen(new SD.Point(c.Width / 2, c.Height / 2));
        object driver = Driver;
        driver.GetType().GetMethod("InjectClick", BindingFlags.NonPublic | BindingFlags.Instance)
              .Invoke(driver, new object[] { p.X, p.Y });
    }

    private static void ResetSaveLatch()
    {
        Type host = s_swf.GetType("System.Windows.Forms.PresentationHost");
        object current = host?.GetProperty("Current", BindingFlags.NonPublic | BindingFlags.Static)?.GetValue(null);
        current?.GetType().GetField("_savedGpu", BindingFlags.NonPublic | BindingFlags.Instance)?.SetValue(current, false);
    }

    private static void MoveRealCursorToWpfButton(ElementHost host, Program.WpfPanel wpf)
    {
        if (host.TryGetAlignment(wpf.Button, out SD.Point drawn, out _))
            OsInput.MoveCursorToClientPoint(HostWindow, drawn.X, drawn.Y);
    }
}
