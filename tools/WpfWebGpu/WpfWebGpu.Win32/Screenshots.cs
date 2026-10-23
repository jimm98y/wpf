// In-process screenshots for `wpfrun run --screenshots <dir>`, taken from the stack's own rendering
// rather than from the window server: macOS hands a process that has no screen-recording permission
// a blank image even of its own windows. Every few seconds each WPF window (RenderTargetBitmap of its
// root) and each top-level WinForms form (the frame its host presents) is saved as a PNG named
// <seconds>-<kind>-<handle>.png, so a run leaves its timeline behind.

using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Forms = System.Windows.Forms;

namespace WpfWebGpu.Win32;

public static class Screenshots
{
    private static Timer? s_timer;
    private static readonly Stopwatch s_clock = Stopwatch.StartNew();

    public static void Start(string directory, int intervalSeconds)
    {
        Directory.CreateDirectory(directory);
        int ms = Math.Max(1, intervalSeconds) * 1000;
        s_timer = new Timer(_ => CaptureAll(directory), null, ms, ms);
        StartClickDiagnostic();
        StartWheelDiagnostic();
        StartDragDiagnostic();

        // WPFRUN_DEBUG_ZOOM=<seconds> (macOS): zoom (maximize) the key window then, as its green button
        // would, so the screenshots and window dumps that follow show how the app lays itself out.
        if (OperatingSystem.IsMacOS() && double.TryParse(Environment.GetEnvironmentVariable("WPFRUN_DEBUG_ZOOM"), out double zoomAt))
        {
            s_zoomTimer = new Timer(_ =>
            {
                Forms.Form? form = Forms.Application.OpenForms.Cast<Forms.Form>().FirstOrDefault(f => f.IsHandleCreated && f.Visible);
                form?.BeginInvoke(() =>
                {
                    // By title: an app started in the background has no main or key window.
                    nint window = MacWindows().FirstOrDefault(w => w.Title == form.Text).Window;
                    if (window != 0) MacNative.SendVoidPtr(window, MacNative.Sel("zoom:"), 0);
                    Exports.LogOnce("debug-zoom", $"debug: zoomed window 0x{window:x}");
                });
            }, null, (int)(zoomAt * 1000), Timeout.Infinite);
        }
    }

    private static Timer? s_zoomTimer;
    private static readonly List<Timer> s_clickTimers = new();

    // WPFRUN_DEBUG_WHEEL=x,y,notches,seconds: turn the wheel at form point (x,y), through the
    // WinForms driver (positive scrolls up, as on Windows).
    private static void StartWheelDiagnostic()
    {
        string? spec = Environment.GetEnvironmentVariable("WPFRUN_DEBUG_WHEEL");
        if (string.IsNullOrEmpty(spec)) return;
        double[] v = spec.Split(',').Select(t => double.TryParse(t, out double d) ? d : double.NaN).ToArray();
        if (v.Length < 4 || v.Any(double.IsNaN)) return;
        s_clickTimers.Add(new Timer(_ =>
        {
            Forms.Form? form = Forms.Application.OpenForms.Cast<Forms.Form>().FirstOrDefault(f => f.IsHandleCreated && f.Visible && f.TopLevel);
            form?.BeginInvoke(() =>
            {
                object? driver = typeof(Forms.Control).Assembly.GetType("System.Windows.Forms.XplatUI")?.GetField("driver", BindingFlags.NonPublic | BindingFlags.Static)?.GetValue(null);
                MethodInfo? wheel = driver?.GetType().GetMethod("InjectWheel", BindingFlags.NonPublic | BindingFlags.Instance);
                wheel?.Invoke(driver, new object[] { form.Left + (int)v[0], form.Top + (int)v[1], (int)v[2] * 120 });
                Exports.Log($"debug wheel: {(int)v[2]} notches at {v[0]},{v[1]}");
            });
        }, null, (int)(v[3] * 1000), Timeout.Infinite));
    }
    private static bool s_clickLogger;

    // WPFRUN_DEBUG_CLICK=x,y,seconds (macOS): post a real left click (NSEvent mouse down + up) at
    // window point (x,y) of the app's main WinForms window -- top-left origin, points -- and log
    // which control each mouse message reaches, so "clicks do nothing" can be traced without a user.
    private static void StartClickDiagnostic()
    {
        string? spec = Environment.GetEnvironmentVariable("WPFRUN_DEBUG_CLICK");
        if (!OperatingSystem.IsMacOS() || string.IsNullOrEmpty(spec)) return;
        // Several clicks, ';'-separated: open a menu, then pick from it. Each is x,y,seconds, and
        // optionally a part of the title of the window to click in; without one, the key window,
        // else the newest WinForms form (a dialog, when one is open), else the WPF main window.
        foreach (string one in spec.Split(';'))
        {
            string[] p = one.Split(',');
            if (p.Length < 3 || !double.TryParse(p[0], out double x) || !double.TryParse(p[1], out double y) || !double.TryParse(p[2], out double at)) continue;
            string? titlePart = p.Length > 3 ? string.Join(",", p.Skip(3)) : null;
            s_clickTimers.Add(new Timer(_ =>
            {
                Forms.Form? form = Forms.Application.OpenForms.Cast<Forms.Form>().LastOrDefault(f => f.IsHandleCreated && f.Visible && f.TopLevel);
                Window? main = null;
                Action click = () =>
                {
                    if (form != null && !s_clickLogger) { s_clickLogger = true; Forms.Application.AddMessageFilter(new ClickLogger()); }
                    // Visible windows only: a hidden one may carry the same title.
                    var windows = MacWindows().Where(w => MacNative.SendBool(w.Window, MacNative.Sel("isVisible"))).ToList();
                    nint key = MacNative.Send(MacNative.Send(MacNative.Class("NSApplication"), MacNative.Sel("sharedApplication")), MacNative.Sel("keyWindow"));
                    nint window = titlePart != null ? windows.FirstOrDefault(w => w.Title.Contains(titlePart, StringComparison.OrdinalIgnoreCase)).Window
                        : key != 0 ? key
                        : form != null ? windows.FirstOrDefault(w => w.Title == form.Text).Window
                        : windows.FirstOrDefault(w => main != null && w.Title == main.Title).Window;
                    if (window == 0) { Exports.Log($"debug click: no window{(titlePart != null ? " titled like '" + titlePart + "'" : "")}"); return; }
                    nint content = MacNative.Send(window, MacNative.Sel("contentView"));
                    double height = MacNative.SendRect(content, MacNative.Sel("frame")).Height;
                    var point = new CGPoint { X = x, Y = height - y };
                    nint number = MacNative.Send(window, MacNative.Sel("windowNumber"));
                    // Moved, down, up: a pointer arrives before it presses, and controls that track the
                    // hot item (DotNetBar's ribbon) act only on the one under it.
                    MacNative.PostEvent(MacNative.MouseEvent(5, new CGPoint { X = point.X - 3, Y = point.Y }, number));
                    foreach (nuint type in new nuint[] { 5, 1, 2 })
                    {
                        nint evt = MacNative.MouseEvent(type, point, number);
                        MacNative.PostEvent(evt);
                    }
                    Exports.Log($"debug click: posted at {x},{y} to window 0x{window:x}");
                };
                if (form != null && titlePart == null) form.BeginInvoke(click);
                else if (Application.Current is Application app)
                    app.Dispatcher.BeginInvoke(() => { main = app.MainWindow; click(); });
                else form?.BeginInvoke(click);
            }, null, (int)(at * 1000), Timeout.Infinite));
        }
    }

    private static Timer? s_dragTimer;

    // WPFRUN_DEBUG_DRAG=x1,y1,x2,y2,seconds (macOS): a real left-button drag from one window point to
    // another (down, a dozen dragged moves, up), as a user resizing a splitter would, then a click
    // where it ended.
    private static void StartDragDiagnostic()
    {
        string? spec = Environment.GetEnvironmentVariable("WPFRUN_DEBUG_DRAG");
        if (!OperatingSystem.IsMacOS() || string.IsNullOrEmpty(spec)) return;
        double[] v = spec.Split(',').Select(t => double.TryParse(t, out double d) ? d : double.NaN).ToArray();
        if (v.Length < 5 || v.Any(double.IsNaN)) return;
        s_dragTimer = new Timer(_ =>
        {
            Forms.Form? form = Forms.Application.OpenForms.Cast<Forms.Form>().FirstOrDefault(f => f.IsHandleCreated && f.Visible && f.TopLevel);
            form?.BeginInvoke(() =>
            {
                nint window = MacWindows().FirstOrDefault(w => w.Title == form.Text).Window;
                if (window == 0) return;
                int changes = 0;
                void Who(object? _, EventArgs __)
                {
                    if (changes++ < 6) Exports.Log($"debug drag: form bounds now {form.Bounds}\n{string.Join("\n", Environment.StackTrace.Split('\n').Skip(2).Take(25))}");
                }
                form.LocationChanged += Who;
                form.SizeChanged += Who;
                // The drag, then a click (press and release) where it ended.
                double height = MacNative.SendRect(MacNative.Send(window, MacNative.Sel("contentView")), MacNative.Sel("frame")).Height;
                nint number = MacNative.Send(window, MacNative.Sel("windowNumber"));
                void Drag(double x1, double y1, double x2, double y2)
                {
                    CGPoint At(double t) => new CGPoint { X = x1 + (x2 - x1) * t, Y = height - (y1 + (y2 - y1) * t) };
                    MacNative.PostEvent(MacNative.MouseEvent(1, At(0), number));
                    for (int i = 1; i <= 12; i++) MacNative.PostEvent(MacNative.MouseEvent(6, At(i / 12.0), number));   // left mouse dragged
                    MacNative.PostEvent(MacNative.MouseEvent(2, At(1), number));
                }
                // WPFRUN_DEBUG_MOVE=dx,dy: move the window first (the frame change a title-bar drag ends in).
                double[] move = (Environment.GetEnvironmentVariable("WPFRUN_DEBUG_MOVE") ?? "").Split(',').Select(t => double.TryParse(t, out double d) ? d : double.NaN).ToArray();
                bool moving = move.Length == 2 && !move.Any(double.IsNaN);
                if (moving)
                {
                    var f = MacNative.SendRect(window, MacNative.Sel("frame"));
                    MacNative.SendPoint(window, MacNative.Sel("setFrameOrigin:"), new CGPoint { X = f.X + move[0], Y = f.Y - move[1] });
                    Exports.Log($"debug drag: moved the window by {move[0]},{move[1]}");
                }
                Task.Delay(moving ? 3000 : 0).ContinueWith(_ => form.BeginInvoke(() =>
                {
                    Drag(v[0], v[1], v[2], v[3]);
                    Exports.Log($"debug drag: posted {v[0]},{v[1]} -> {v[2]},{v[3]}");
                    Task.Delay(3000).ContinueWith(_ => form.BeginInvoke(() =>
                    {
                        Drag(v[2], v[3], v[2], v[3]);
                        Exports.Log($"debug drag: posted click at {v[2]},{v[3]}");
                    }));
                }));
            });
        }, null, (int)(v[4] * 1000), Timeout.Infinite);
    }

    private sealed class ClickLogger : Forms.IMessageFilter
    {
        private int _count;

        public bool PreFilterMessage(ref Forms.Message m)
        {
            if (m.Msg is 0x0201 or 0x0202 or 0x0021 && _count++ < 20)   // WM_LBUTTONDOWN/UP, WM_MOUSEACTIVATE
            {
                var c = Forms.Control.FromHandle(m.HWnd);
                Exports.Log($"debug click: msg 0x{m.Msg:x4} to {c?.GetType().FullName} '{c?.Name}' enabled={c?.Enabled} lparam=0x{m.LParam:x}");
            }
            return false;
        }
    }

    /// <summary>Every NSWindow of the process (main thread only).</summary>
    private static List<(nint Window, string Title)> MacWindows()
    {
        var result = new List<(nint, string)>();
        nint app = MacNative.Send(MacNative.Class("NSApplication"), MacNative.Sel("sharedApplication"));
        nint windows = MacNative.Send(app, MacNative.Sel("windows"));
        nint count = MacNative.Send(windows, MacNative.Sel("count"));
        for (nint i = 0; i < count; i++)
        {
            nint w = MacNative.SendPtrNInt(windows, MacNative.Sel("objectAtIndex:"), i);
            nint t = MacNative.Send(w, MacNative.Sel("title"));
            string title = t == 0 ? "" : System.Runtime.InteropServices.Marshal.PtrToStringUTF8(MacNative.Send(t, MacNative.Sel("UTF8String"))) ?? "";
            result.Add((w, title));
        }
        return result;
    }

    private static void CaptureAll(string directory)
    {
        string stamp = ((int)s_clock.Elapsed.TotalSeconds).ToString("D4") + "s";
        try
        {
            foreach (PresentationSource ps in PresentationSource.CurrentSources)
            {
                if (ps is not HwndSource source || source.IsDisposed) continue;
                string path = Path.Combine(directory, $"{stamp}-wpf-{source.Handle:x}.png");
                source.Dispatcher.BeginInvoke(() => SaveWpf(source, path));
            }
        }
        catch (Exception e)
        {
            Exports.LogOnce("shots:wpf", "screenshots (WPF): " + e.Message);
        }

        if (Environment.GetEnvironmentVariable("WPFRUN_DUMP_WINDOWS") == "1" && Application.Current?.Dispatcher is System.Windows.Threading.Dispatcher wpfMain)
            wpfMain.BeginInvoke(() => DumpNsWindows(stamp));   // LogOnce per stamp: once, WPF or WinForms

        if (Environment.GetEnvironmentVariable("WPFRUN_DUMP_WINDOWS") == "1")
        {
            // Every native window WPF made, presentation source or not: whose hooks it carries says
            // whose window it is (an HwndSource, a Window, a Popup, a hidden helper).
            try
            {
                Type? wrapperType = typeof(System.Windows.Threading.Dispatcher).Assembly.GetType("MS.Win32.HwndWrapper");
                var bf = BindingFlags.NonPublic | BindingFlags.Static;
                if (wrapperType?.GetField("s_byHandle", bf)?.GetValue(null) is System.Collections.IDictionary byHandle)
                {
                    var lines = new System.Text.StringBuilder($"wpf native windows at {stamp}:\n");
                    object gate = wrapperType.GetField("s_byHandleLock", bf)?.GetValue(null) ?? byHandle;
                    lock (gate)
                    {
                        foreach (System.Collections.DictionaryEntry e in byHandle)
                        {
                            object w = e.Value!;
                            var hooks = wrapperType.GetField("_hooks", BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(w) as System.Collections.IEnumerable;
                            string owners = hooks == null ? "" : string.Join(",", hooks.Cast<object>().Select(h => h is Delegate d ? d.Target?.GetType().Name + "." + d.Method.Name : h?.GetType().Name));
                            object? platform = wrapperType.GetField("_platformWindow", BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(w);
                            nint ns = platform?.GetType().GetField("_window", BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(platform) is IntPtr p ? p : 0;
                            lines.Append($"  0x{(IntPtr)e.Key:x} nswindow=0x{ns:x} hooks=[{owners}]\n");
                        }
                    }
                    Exports.LogOnce("wpfnative:" + stamp, lines.ToString().TrimEnd());
                }
            }
            catch (Exception e)
            {
                Exports.LogOnce("wpfnative-fail", "wpf native window dump: " + e.Message);
            }

            try
            {
                foreach (PresentationSource ps in PresentationSource.CurrentSources)
                {
                    if (ps is HwndSource source && !source.IsDisposed) source.Dispatcher.BeginInvoke(() => DumpWpf(source, stamp));
                }
            }
            catch (Exception e)
            {
                Exports.LogOnce("dump-wpf-fail", "wpf window dump: " + e.Message);
            }
        }

        try
        {
            Type? host = typeof(Forms.Form).Assembly.GetType("System.Windows.Forms.PresentationHost");
            if (host?.GetField("s_forms", BindingFlags.NonPublic | BindingFlags.Static)?.GetValue(null) is System.Collections.IDictionary forms)
            {
                object? gate = host.GetField("s_lock", BindingFlags.NonPublic | BindingFlags.Static)?.GetValue(null);
                List<(object Host, Forms.Form Form)> snapshot = new();
                lock (gate ?? forms)
                {
                    foreach (System.Collections.DictionaryEntry e in forms)
                    {
                        if (e.Value is Forms.Form f && !f.IsDisposed) snapshot.Add((e.Key, f));
                    }
                }
                if (Environment.GetEnvironmentVariable("WPFRUN_DUMP_WINDOWS") == "1")
                {
                    var lines = new System.Text.StringBuilder($"winforms hosts at {stamp}:\n");
                    lock (gate ?? forms)
                    {
                        foreach (System.Collections.DictionaryEntry e in forms)
                        {
                            object hostObj = e.Key;
                            nint ns = hostObj.GetType().GetField("_window", BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(hostObj) is IntPtr p ? p : 0;
                            var f = e.Value as Forms.Form;
                            lines.Append($"  {hostObj.GetType().Name} nswindow=0x{ns:x} form={f?.GetType().FullName} '{f?.Text}' disposed={f?.IsDisposed} visible={(f is { IsDisposed: false } ? f.Visible : false)}\n");
                        }
                    }
                    Exports.LogOnce("hosts:" + stamp, lines.ToString().TrimEnd());
                }
                foreach (var (h, form) in snapshot)
                {
                    if (Environment.GetEnvironmentVariable("WPFRUN_DUMP_WINDOWS") == "1" && form.IsHandleCreated)
                    {
                        form.BeginInvoke(() => DumpWindows(form, stamp));
                    }
                    string path = Path.Combine(directory, $"{stamp}-winforms-{RuntimeHelpersId(form)}.png");
                    MethodInfo? save = h.GetType().GetMethod("SaveFrame", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    if (save == null || !form.IsHandleCreated) continue;
                    form.BeginInvoke(() => { try { save.Invoke(h, new object[] { path }); } catch { } });
                }
            }
        }
        catch (Exception e)
        {
            Exports.LogOnce("shots:winforms", "screenshots (WinForms): " + e.Message);
        }
    }
    /// <summary>A member path: a.b.c, each a property or field of any visibility (inherited too), or
    /// [i] of a list.</summary>
    static object? MemberPathResolve(object? o, string path)
    {
        const BindingFlags bf = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
        foreach (string part0 in path.Split('.'))
        {
            if (o == null) return null;
            string part = part0; int index = -1;
            int br = part.IndexOf('[');
            if (br >= 0) { index = int.Parse(part[(br + 1)..^1]); part = part[..br]; }
            if (part.Length > 0)
            {
                object? next = "?";
                for (Type? t = o.GetType(); t != null; t = t.BaseType)
                {
                    if (t.GetProperty(part, bf) is PropertyInfo p && p.GetIndexParameters().Length == 0) { next = p.GetValue(o); break; }
                    if (t.GetField(part, bf) is FieldInfo f) { next = f.GetValue(o); break; }
                }
                o = next;
            }
            if (index >= 0) o = o is System.Collections.IList l && index < l.Count ? l[index] : null;
        }
        return o;
    }
    static object? Member(object o, string path) => MemberPathResolve(o, path);


    /// <summary>WPFRUN_DUMP_PROPS="Type:Member,Member;Type2:Member": those members (properties or fields, any
    /// visibility, inherited too) of every control in the dump that is a Type, by simple name.</summary>
    private static void AppendProps(System.Text.StringBuilder lines, Forms.Control c, int depth)
    {
        string? spec = Environment.GetEnvironmentVariable("WPFRUN_DUMP_PROPS");
        if (string.IsNullOrEmpty(spec)) return;
        foreach (string part in spec.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            string[] tm = part.Split(':');
            if (tm.Length != 2) continue;
            bool match = false;
            for (Type? t = c.GetType(); t != null; t = t.BaseType) if (t.Name == tm[0]) { match = true; break; }
            if (!match) continue;
            var vals = new List<string>();
            foreach (string name in tm[1].Split(','))
            {
                object? v;
                try { v = Member(c, name); }
                catch (Exception e) { v = "!" + e.GetBaseException().GetType().Name; }
                // An image member is also written out, to compare its pixels with stock's.
                if (v is System.Drawing.Image img && Environment.GetEnvironmentVariable("WPFRUN_DUMP_IMAGES") is { Length: > 0 } dir)
                {
                    try { img.Save(Path.Combine(dir, $"{c.Name}-{string.Concat(name.Split(Path.GetInvalidFileNameChars()))}.png"), System.Drawing.Imaging.ImageFormat.Png); }
                    catch (Exception e) { v = $"{v} (!save {e.GetType().Name})"; }
                }
                vals.Add($"{name}={v}");
            }
            lines.Append(' ', depth * 2 + 4).Append(string.Join(" ", vals)).Append('\n');
        }
    }

    private static string ScreenRectOrEmpty(Forms.Control c)
    {
        try
        {
            var r = c.Parent != null ? c.Parent.RectangleToScreen(c.Bounds) : c.Bounds;
            return $"{r.X},{r.Y} {r.Width}x{r.Height}";
        }
        catch
        {
            return "-";
        }
    }

    /// <summary>WPFRUN_DUMP_WINDOWS=1: what the WinForms host composites for a form, to the compat log.</summary>
    private static void DumpWindows(Forms.Form form, string stamp)
    {
        DumpWindowsCore(form, stamp);
        DumpNsWindows(stamp);
        try
        {
            var forms = new System.Text.StringBuilder($"open forms at {stamp}:\n");
            foreach (Forms.Form f in Forms.Application.OpenForms.Cast<Forms.Form>().ToArray())
            {
                forms.Append($"  {f.GetType().FullName} '{f.Text}' {f.Bounds} visible={f.Visible} enabled={f.Enabled} modal={f.Modal} border={f.FormBorderStyle} handle={f.IsHandleCreated} invokeRequired={f.InvokeRequired} topLevel={f.TopLevel} owner={f.Owner?.Name}\n");
            }
            Exports.LogOnce("forms:" + stamp, forms.ToString().TrimEnd());
        }
        catch (Exception e)
        {
            Exports.LogOnce("forms-fail", "open forms dump: " + e.Message);
        }
        // The control tree too: what is laid out where, hidden or not, which the composited list omits.
        try
        {
            var lines = new System.Text.StringBuilder($"controls of '{form.Text}' {form.Bounds} client {form.ClientSize} at {stamp}:\n");
            void Walk(Forms.Control c, int depth)
            {
                if (depth > 12) return;
                lines.Append(' ', depth * 2).Append($"{c.GetType().Name} '{c.Name}' {c.Bounds} dock={c.Dock} anchor={c.Anchor} visible={c.Visible} screen={ScreenRectOrEmpty(c)}{(c.IsHandleCreated ? $" handle=0x{c.Handle:x}" : "")}\n");
                AppendProps(lines, c, depth);
                foreach (Forms.Control child in c.Controls) Walk(child, depth + 1);
            }
            Walk(form, 0);
            // WPFRUN_DUMP_STATICS="Type, Assembly:Member;...": static members, for state an app keeps there.
            foreach (string st in (Environment.GetEnvironmentVariable("WPFRUN_DUMP_STATICS") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                int colon = st.LastIndexOf(':');
                if (colon < 0) continue;
                const BindingFlags sf = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
                Type? ty = Type.GetType(st[..colon]);
                object? v = ty?.GetField(st[(colon + 1)..], sf) is FieldInfo fi ? fi.GetValue(null) : ty?.GetProperty(st[(colon + 1)..], sf)?.GetValue(null);
                lines.Append($"[static] {st} = {v}\n");
            }
            // Top-level controls that are not forms: popups (a menu's container) are windows of
            // their own, outside every form's tree.
            if (typeof(Forms.Control).Assembly.GetType("System.Windows.Forms.Hwnd")?.GetField("windows", BindingFlags.NonPublic | BindingFlags.Static)?.GetValue(null) is System.Collections.Hashtable all)
            {
                object[] handles;
                lock (all) { handles = all.Keys.Cast<object>().ToArray(); }
                foreach (object h in handles)
                {
                    if (h is IntPtr handle && Forms.Control.FromHandle(handle) is Forms.Control c && c.Parent == null && c is not Forms.Form && c.Visible)
                    {
                        lines.Append($"top-level {c.GetType().FullName} at {c.Bounds}:\n");
                        Walk(c, 1);
                    }
                }
            }
            Exports.LogOnce("tree:" + stamp + form.GetHashCode(), lines.ToString().TrimEnd());
        }
        catch (Exception e)
        {
            Exports.LogOnce("tree-fail", "control tree dump: " + e.Message);
        }
    }

    /// <summary>WPFRUN_DUMP_WINDOWS=1: every NSWindow, and child windows back to front (main thread).</summary>
    private static void DumpNsWindows(string stamp)
    {
        if (OperatingSystem.IsMacOS())
        {
            var lines = new System.Text.StringBuilder($"NSWindows at {stamp}:\n");
            foreach (var (w, title) in MacWindows())
            {
                var f = MacNative.SendRect(w, MacNative.Sel("frame"));
                bool visible = MacNative.SendBool(w, MacNative.Sel("isVisible"));
                nint parent = MacNative.Send(w, MacNative.Sel("parentWindow"));
                lines.Append($"  0x{w:x} '{title}' visible={visible} frame=({f.X},{f.Y} {f.Width}x{f.Height}) parent=0x{parent:x} level={MacNative.Send(w, MacNative.Sel("level"))} style=0x{MacNative.Send(w, MacNative.Sel("styleMask")):x} opaque={MacNative.SendBool(w, MacNative.Sel("isOpaque"))}\n");
            }
            // Child windows back to front: what is drawn over what.
            foreach (var (w, title) in MacWindows())
            {
                nint children = MacNative.Send(w, MacNative.Sel("childWindows"));
                nint n = children == 0 ? 0 : MacNative.Send(children, MacNative.Sel("count"));
                if (n == 0) continue;
                lines.Append($"  children of 0x{w:x}:");
                for (nint i = 0; i < n; i++) lines.Append($" 0x{MacNative.SendPtrNInt(children, MacNative.Sel("objectAtIndex:"), i):x}");
                lines.Append('\n');
            }
            Exports.LogOnce("nswindows:" + stamp, lines.ToString().TrimEnd());
        }
    }

    private static void DumpWindowsCore(Forms.Form form, string stamp)
    {
        try
        {
            object driver = typeof(Forms.Control).Assembly.GetType("System.Windows.Forms.XplatUI")!
                .GetField("driver", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
            var present = (long[])driver.GetType().GetMethod("GetPresentWindows", BindingFlags.NonPublic | BindingFlags.Instance)!
                .Invoke(driver, new object[] { form.Handle })!;
            var lines = new System.Text.StringBuilder($"windows of '{form.Text}' at {stamp}:\n");
            for (int i = 0; i + 2 < present.Length; i += 3)
            {
                var c = Forms.Control.FromHandle((IntPtr)present[i]);
                lines.Append($"  0x{present[i]:x} at {present[i + 1]},{present[i + 2]} {c?.GetType().FullName ?? "?"} {c?.Bounds} visible={c?.Visible} parent={c?.Parent?.GetType().Name}\n");
            }
            Exports.LogOnce("dump:" + stamp + form.GetHashCode(), lines.ToString().TrimEnd());
        }
        catch (Exception e)
        {
            Exports.LogOnce("dump-fail", "window dump: " + e.Message);
        }
    }

    /// <summary>WPFRUN_DUMP_WINDOWS=1: a WPF window's place on screen and what it belongs to, to the compat log.</summary>
    private static void DumpWpf(HwndSource source, string stamp)
    {
        try
        {
            string where = source.RootVisual is FrameworkElement root && root.IsVisible
                ? $"{root.PointToScreen(new Point(0, 0))} {root.ActualWidth}x{root.ActualHeight} {root.GetType().FullName}"
                : $"(not visible) {source.RootVisual?.GetType().FullName}";
            object? wrapper = typeof(HwndSource).GetField("_hwndWrapper", BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(source);
            object? platform = wrapper?.GetType().GetField("_platformWindow", BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(wrapper);
            nint owner = platform?.GetType().GetField("_ownerHandle", BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(platform) is IntPtr o ? o : 0;
            nint nsWindow = platform?.GetType().GetField("_window", BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(platform) is IntPtr w ? w : 0;
            if (nsWindow != 0 && OperatingSystem.IsMacOS())
            {
                bool visible = MacNative.SendBool(nsWindow, MacNative.Sel("isVisible"));
                nint parentWindow = MacNative.Send(nsWindow, MacNative.Sel("parentWindow"));
                var frame = MacNative.SendRect(nsWindow, MacNative.Sel("frame"));
                where += $"; NSWindow visible={visible} frame=({frame.X},{frame.Y} {frame.Width}x{frame.Height}) parentWindow=0x{parentWindow:x}";
            }
            string ownerText = owner == 0 ? "none" : $"0x{owner:x}";
            if (owner != 0 && Forms.Control.FromHandle(owner) is Forms.Control c)
                ownerText += $" WinForms {c.GetType().FullName} '{c.Name}' at {c.PointToScreen(System.Drawing.Point.Empty)} {c.Size} in form '{c.FindForm()?.Text}' at {c.FindForm()?.Location}";
            else if (owner != 0 && HwndSource.FromHwnd(owner) is HwndSource os)
                ownerText += $" WPF {os.RootVisual?.GetType().FullName}";
            Exports.LogOnce($"dumpwpf:{stamp}:{source.Handle}", $"wpf window 0x{source.Handle:x} at {stamp}: {where}; platform {platform?.GetType().Name}; owner {ownerText}");
        }
        catch (Exception e)
        {
            Exports.LogOnce("dump-wpf-fail2", "wpf window dump: " + e.Message);
        }
    }

    private static int RuntimeHelpersId(object o) => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(o);

    // WPFRUN_DUMP_ADORNERS=1 (diagnostics): every adorner layer under the window's root and what is in
    // it -- type, size, visibility, opacity, where it is relative to the root -- with each capture.
    private static void DumpAdorners(HwndSource source, string stamp)
    {
        if (source.RootVisual is not Visual root) return;
        var lines = new System.Text.StringBuilder($"adorners of wpf 0x{source.Handle:x} at {stamp} (root {root.GetType().Name} dpi {VisualTreeHelper.GetDpi(root).DpiScaleX}):\n");
        var stack = new Stack<DependencyObject>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            DependencyObject d = stack.Pop();
            if (d is System.Windows.Documents.AdornerLayer layer)
            {
                lines.Append($"  layer {layer.RenderSize} visible={layer.IsVisible} children={VisualTreeHelper.GetChildrenCount(layer)}\n");
                for (int i = 0; i < VisualTreeHelper.GetChildrenCount(layer); i++)
                    if (VisualTreeHelper.GetChild(layer, i) is UIElement a)
                    {
                        string at = "?";
                        try { at = a.TransformToAncestor(root).Transform(new Point()).ToString(); } catch (Exception e) { at = e.GetType().Name; }
                        lines.Append($"    {a.GetType().FullName} size={a.RenderSize} visible={a.IsVisible} opacity={a.Opacity} at={at}\n");
                    }
            }
            if (d.GetType().Name == "GwxRuntimeViewControl")
            {
                // app's licence state, which decides its licence notices (diagnostics only)
                const BindingFlags f = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
                Type t = d.GetType();
                object Field(string name) { try { return t.GetField(name, f)?.GetValue(d); } catch (Exception e) { return e.GetType().Name; } }
                string mainForm;
                try { mainForm = t.GetMethod("IsMainForm", f)?.Invoke(d, null)?.ToString() ?? "?"; } catch (Exception e) { mainForm = (e.InnerException ?? e).GetType().Name; }
                object model = Field("licenseModel");
                string show = "?";
                try { show = model?.GetType().GetProperty("ShowLicenseWarningMessage")?.GetValue(model)?.ToString() + " '" + model?.GetType().GetProperty("LicenseWarningMessage")?.GetValue(model) + "'"; } catch (Exception e) { show = e.GetType().Name; }
                string Prop(string name) { try { return model?.GetType().GetProperty(name, f)?.GetValue(model)?.ToString() ?? "null"; } catch (Exception e) { return (e.InnerException ?? e).GetType().Name; } }
                lines.Append($"  licence model: IsDemoMode={Prop("IsDemoMode")} CreationDate={Prop("CreationDate")} ExpirationDate={Prop("ExpirationDate")} ShutDownDate={Prop("ShutDownDate")} now={DateTime.Now} IsConnected={Prop("IsConnected")} Edition={Prop("Edition")}\n");
                try
                {
                    if (model?.GetType().GetProperty("LicenseTags", f)?.GetValue(model) is System.Collections.IEnumerable tags)
                        foreach (object tag in tags)
                        {
                            Type tt = tag.GetType();
                            object point = tt.GetProperty("LicensePoint", f)?.GetValue(tag);
                            object ctx = tt.GetProperty("Context", f)?.GetValue(tag);
                            lines.Append($"    point {tt.GetProperty("LicenseTag", f)?.GetValue(tag)}: point={(point == null ? "null" : point.GetType().Name)} last={tt.GetProperty("LastValue", f)?.GetValue(tag) ?? "null"} context={ctx?.GetType().Name}\n");
                            if (point != null && lines.Length < 20000)
                                foreach (PropertyInfo pi in point.GetType().GetProperties(BindingFlags.Instance | BindingFlags.Public))
                                {
                                    if (pi.GetIndexParameters().Length > 0) continue;
                                    object v; try { v = pi.GetValue(point); } catch (Exception e) { v = (e.InnerException ?? e).GetType().Name; }
                                    lines.Append($"      {pi.Name}={v}\n");
                                }
                        }
                }
                catch (Exception e) { lines.Append("    points: " + e.GetType().Name + "\n"); }
                lines.Append($"  GwxRuntimeViewControl: IsMainForm={mainForm} licenseManager={(Field("licenseManager") == null ? "null" : "set")} licenseModel={(model == null ? "null" : "set")} warning={show} failureAdorner={(Field("licenseFailureAdorner") == null ? "null" : "set")} noticeAdorner={(Field("licenseNotificationAdorner") == null ? "null" : "set")}\n");
            }
            int n = d is Visual || d is System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetChildrenCount(d) : 0;
            for (int i = 0; i < n; i++) stack.Push(VisualTreeHelper.GetChild(d, i));
        }
        Console.Error.Write(lines.ToString());
    }

    private static void SaveWpf(HwndSource source, string path)
    {
        if (Environment.GetEnvironmentVariable("WPFRUN_DUMP_ADORNERS") == "1")
            try { DumpAdorners(source, Path.GetFileName(path)); } catch (Exception e) { Console.Error.WriteLine("adorner dump: " + e.Message); }
        if (source.IsDisposed || source.RootVisual is not FrameworkElement root || root.ActualWidth < 1 || root.ActualHeight < 1) return;
        // At the window's own DPI, so the picture is the window's physical pixels (a 200% display
        // got a half-size picture of a window that is drawn 1:1).
        DpiScale dpi = VisualTreeHelper.GetDpi(root);
        var rtb = new RenderTargetBitmap((int)Math.Ceiling(root.ActualWidth * dpi.DpiScaleX), (int)Math.Ceiling(root.ActualHeight * dpi.DpiScaleY),
            dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        rtb.Render(root);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(rtb));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }
}
