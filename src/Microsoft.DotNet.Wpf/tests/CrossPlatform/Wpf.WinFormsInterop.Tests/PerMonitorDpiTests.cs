// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// Per-monitor DPI, held to stock .NET 10 WinForms. A designer-style form (the shape InitializeComponent
// writes: AutoScaleDimensions, AutoScaleMode, ResumeLayout(false), PerformLayout) is created in a
// PerMonitorV2 application on a 200% display, then told it moved to 96, 240 and back to 192 dpi the
// way Windows tells it -- WM_GETDPISCALEDSIZE, WM_DPICHANGED_BEFOREPARENT to every child, WM_DPICHANGED
// with the suggested rectangle, WM_DPICHANGED_AFTERPARENT. Every bound, font, DeviceDpi and system
// metric, and the order the DPI events arrive in, is compared with what stock printed for the same
// form and the same messages (Fixtures/Dpi/stock-*.txt, recorded with the scratch probe "dpitab").
//
// The display is simulated (ScaleHelper.OverrideForTests), so the test does not depend on the
// machine's monitors. Lines that cannot agree are listed in KnownDifferences with the reason; the
// list may shrink and must not grow.
//

using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using SWF = System.Windows.Forms;
using Xunit;

namespace Wpf.WinFormsInterop.Tests
{
    public class PerMonitorDpiTests
    {
        // What a process loads once, at the DPI it first runs at (the system icons, as stock caches
        // them too), is loaded before the simulated display: filled under the override, the rest of
        // the suite -- a 96-dpi application -- would get 200% icons.
        static PerMonitorDpiTests()
        {
            _ = SystemIcons.Application;
        }

        // Lines (by their first words) whose values cannot match, and why.
        private static readonly string[] KnownDifferences =
        {
            // comctl32's tab control and track bar take their DPI from their real window, which the
            // stock recording's simulated move never changes: stock reports them still at 192 dpi.
            "tabs ", "track ", "TabPage ",
            // The splitter distance rounds a pixel differently (91 against 92), and the two panels
            // are listed in the other order.
            "split ", "SplitterPanel ",
            // The combo box a tool strip hosts is scaled by the font its native window gets; ours
            // keeps the strip's.
            "ToolStripComboBoxControl ", "tool ",
        };

        // After a simulated move only: uxtheme sizes a check or radio glyph for the HWND's DPI, which
        // the stock recording's messages never changed -- stock keeps the 192-dpi glyph at 96 and 240.
        private static readonly string[] KnownAfterMoves = { "check ", "radio " };

        public static IEnumerable<object[]> Modes()
        {
            yield return new object[] { SWF.AutoScaleMode.Font };
            yield return new object[] { SWF.AutoScaleMode.Dpi };
            yield return new object[] { SWF.AutoScaleMode.None };
        }

        [Theory]
        [MemberData(nameof(Modes))]
        public void ADesignedForm_ScalesAsStockDoes(SWF.AutoScaleMode mode)
        {
            Assert.SkipUnless(OperatingSystem.IsWindows(), "the frame sizes are Windows' (AdjustWindowRectExForDpi)");
            string stockPath = Path.Combine(AppContext.BaseDirectory, "Fixtures", "Dpi", $"stock-PerMonitorV2-{mode}-192.txt");
            string[] stock = File.ReadAllLines(stockPath);

            string ours;
            using (SWF.ScaleHelper.OverrideForTests(SWF.HighDpiMode.PerMonitorV2, 192))
                ours = Run(mode);

            var mismatches = new List<string>();
            string[] mine = ours.Replace("\r\n", "\n").Split('\n');
            bool moved = false;
            for (int i = 0, j = 0; i < stock.Length; i++, j++)
            {
                string s = Normalize(stock[i]);
                string o = j < mine.Length ? Normalize(mine[j]) : "<missing>";
                if (s.StartsWith("[dpi", StringComparison.Ordinal)) moved = true;
                if (s == o || IsKnown(s) || moved && StartsWithAny(s, KnownAfterMoves)) continue;
                mismatches.Add($"stock: {s}\n ours: {o}");
            }
            Assert.True(mismatches.Count == 0, $"{mismatches.Count} lines differ from stock:\n" + string.Join("\n", mismatches));
        }

        [Theory]
        [InlineData("<dpiAwareness>PerMonitorV2, PerMonitor</dpiAwareness>", SWF.HighDpiMode.PerMonitorV2)]
        [InlineData("<dpiAwareness>PerMonitor</dpiAwareness>", SWF.HighDpiMode.PerMonitor)]
        [InlineData("<dpiAwareness>bogus, System</dpiAwareness>", SWF.HighDpiMode.SystemAware)]
        [InlineData("<dpiAware>true</dpiAware>", SWF.HighDpiMode.SystemAware)]
        [InlineData("<dpiAware>true/PM</dpiAware>", SWF.HighDpiMode.PerMonitor)]
        [InlineData("<dpiAware>false</dpiAware>", SWF.HighDpiMode.DpiUnaware)]
        [InlineData("<dpiAware>true</dpiAware><dpiAwareness>PerMonitorV2</dpiAwareness>", SWF.HighDpiMode.PerMonitorV2)]
        public void TheManifestsAwareness_IsTheMode(string settings, SWF.HighDpiMode expected)
        {
            // dpiAwareness wins over dpiAware, and its first value Windows knows is the one taken.
            string manifest = "garbage\0<assembly xmlns=\"urn:schemas-microsoft-com:asm.v1\"><application><windowsSettings>"
                + settings + "</windowsSettings></application></assembly>more";
            Assert.Equal(expected, SWF.ScaleHelper.ManifestModeOf(Encoding.UTF8.GetBytes(manifest)));
        }

        [Fact]
        public void AManifestWithoutAwareness_SaysNothing()
        {
            Assert.Null(SWF.ScaleHelper.ManifestModeOf(Encoding.UTF8.GetBytes("<assembly><trustInfo/></assembly>")));
        }

        [Fact]
        public void TheModeIsFixedByTheFirstControl()
        {
            // As Windows fixes a process's awareness at its first window: too late after that.
            _ = new SWF.Button();
            Assert.False(SWF.Application.SetHighDpiMode(SWF.HighDpiMode.PerMonitorV2));
        }

        private static bool StartsWithAny(string line, string[] keys)
        {
            foreach (string k in keys)
                if (line.StartsWith(k, StringComparison.Ordinal)) return true;
            return false;
        }

        private static bool IsKnown(string line)
        {
            string t = line.TrimStart();
            foreach (string k in KnownDifferences)
                if (line.StartsWith(k, StringComparison.Ordinal) || t.StartsWith(k, StringComparison.Ordinal)) return true;
            return false;
        }

        // The window rectangle is the real window's in stock; this form is never shown.
        private static string Normalize(string line)
        {
            int h = line.IndexOf(" hwnd=", StringComparison.Ordinal);
            return h > 0 ? line.Substring(0, h) : line;
        }

        private static readonly StringBuilder Log = new();

        private static string Run(SWF.AutoScaleMode mode)
        {
            Log.Clear();
            var sb = new StringBuilder();
            sb.Append($"HighDpiMode={SWF.Application.HighDpiMode} AutoScaleMode={mode}\n");
            using var f = new TableForm(mode) { StartPosition = SWF.FormStartPosition.Manual, Location = new Point(8000, 100), ShowInTaskbar = false };
            Create(f);
            Dump(f, sb, "shown");
            foreach (int dpi in new[] { 96, 240, 192 })
            {
                Log.Append($"--- move to {dpi}\n");
                Synthetic(f, dpi);
                Dump(f, sb, "dpi" + dpi);
            }
            sb.Append("events:\n").Append(Log);
            return sb.ToString();
        }

        private static void Create(SWF.Control c)
        {
            _ = c.Handle;
            foreach (SWF.Control child in c.Controls) Create(child);
        }

        private static readonly MethodInfo WndProc = typeof(SWF.Control).GetMethod("WndProc", BindingFlags.NonPublic | BindingFlags.Instance)!;

        private static IntPtr Send(SWF.Control c, int msg, IntPtr w, IntPtr l)
        {
            object[] args = { SWF.Message.Create(c.Handle, msg, w, l) };
            WndProc.Invoke(c, args);
            return ((SWF.Message)args[0]).Result;
        }

        private static void Descendants(SWF.Control c, List<SWF.Control> into)
        {
            foreach (SWF.Control d in c.Controls) { if (!d.IsHandleCreated) continue; into.Add(d); Descendants(d, into); }
        }

        [StructLayout(LayoutKind.Sequential)] private struct RECT { public int L, T, R, B; }
        [DllImport("user32")] private static extern bool AdjustWindowRectExForDpi(ref RECT r, int style, bool menu, int ex, uint dpi);

        // As the stock recording delivered it (dpitab's Synthetic).
        private static void Synthetic(SWF.Form f, int dpi)
        {
            int old = f.DeviceDpi;
            var size = new Size(0, 0);
            IntPtr sp = Marshal.AllocHGlobal(8);
            Marshal.WriteInt32(sp, 0, 0); Marshal.WriteInt32(sp, 4, 0);
            bool asked = Send(f, 0x02E4, (IntPtr)dpi, sp) != IntPtr.Zero;
            if (asked) size = new Size(Marshal.ReadInt32(sp, 0), Marshal.ReadInt32(sp, 4));
            Marshal.FreeHGlobal(sp);
            if (!asked)
            {
                var fr = new RECT();
                AdjustWindowRectExForDpi(ref fr, unchecked((int)0x00CF0000), false, 0, (uint)dpi);
                size = new Size((int)Math.Round(f.ClientSize.Width * dpi / (double)old) + (fr.R - fr.L),
                                (int)Math.Round(f.ClientSize.Height * dpi / (double)old) + (fr.B - fr.T));
            }
            Log.Append($"suggested size {size} (asked={asked})\n");
            var kids = new List<SWF.Control>();
            Descendants(f, kids);
            foreach (SWF.Control c in kids) Send(c, 0x02E2, (IntPtr)dpi, IntPtr.Zero);
            IntPtr rp = Marshal.AllocHGlobal(16);
            Marshal.WriteInt32(rp, 0, f.Left); Marshal.WriteInt32(rp, 4, f.Top);
            Marshal.WriteInt32(rp, 8, f.Left + size.Width); Marshal.WriteInt32(rp, 12, f.Top + size.Height);
            Send(f, 0x02E0, (IntPtr)((dpi << 16) | dpi), rp);
            Marshal.FreeHGlobal(rp);
            for (int i = kids.Count - 1; i >= 0; i--) Send(kids[i], 0x02E3, IntPtr.Zero, IntPtr.Zero);
        }

        private static string F(Font fn) => $"{fn.Name} {fn.SizeInPoints:0.##}pt/{fn.Height}";

        private static void Dump(SWF.Form f, StringBuilder sb, string tag)
        {
            sb.Append($"[{tag}] DeviceDpi={f.DeviceDpi} hwnd=\n");
            sb.Append($"Form Size={f.Size.Width}x{f.Size.Height} ClientSize={f.ClientSize.Width}x{f.ClientSize.Height} Font={F(f.Font)} ASD={f.AutoScaleDimensions} CASD={f.CurrentAutoScaleDimensions} MinSize={f.MinimumSize}\n");
            sb.Append($"SysInfo VSB={SWF.SystemInformation.VerticalScrollBarWidth} HSB={SWF.SystemInformation.HorizontalScrollBarHeight} VSBdpi={SWF.SystemInformation.GetVerticalScrollBarWidthForDpi(f.DeviceDpi)} Menu={SWF.SystemInformation.MenuHeight} Border3D={SWF.SystemInformation.Border3DSize} DefaultFont={F(SWF.Control.DefaultFont)}\n");
            foreach (SWF.Control c in All(f))
            {
                sb.Append($"{c.Name,-10} {c.GetType().Name,-14} B={c.Left},{c.Top},{c.Width}x{c.Height} C={c.ClientSize.Width}x{c.ClientSize.Height} dpi={c.DeviceDpi} font={F(c.Font)}");
                switch (c)
                {
                    case SWF.ToolStrip ts:
                        sb.Append($" ISS={ts.ImageScalingSize.Width}x{ts.ImageScalingSize.Height} pad={ts.Padding}");
                        foreach (SWF.ToolStripItem it in ts.Items) sb.Append($" [{it.Text}:{it.Bounds.X},{it.Bounds.Y},{it.Bounds.Width}x{it.Bounds.Height} m={it.Margin} p={it.Padding}]");
                        break;
                    case SWF.ListView lv:
                        foreach (SWF.ColumnHeader h in lv.Columns) sb.Append($" col={h.Width}");
                        break;
                    case SWF.DataGridView g:
                        sb.Append($" rowH={g.RowTemplate.Height} hdrW={g.RowHeadersWidth} colHdrH={g.ColumnHeadersHeight}");
                        foreach (SWF.DataGridViewColumn col in g.Columns) sb.Append($" col={col.Width}");
                        break;
                    case SWF.TreeView tv: sb.Append($" itemH={tv.ItemHeight} indent={tv.Indent}"); break;
                    case SWF.ListBox lb: sb.Append($" itemH={lb.ItemHeight}"); break;
                    case SWF.ComboBox cb: sb.Append($" itemH={cb.ItemHeight} ddW={cb.DropDownWidth}"); break;
                    case SWF.TabControl tc: sb.Append($" itemSize={tc.ItemSize} disp={tc.DisplayRectangle}"); break;
                    case SWF.SplitContainer sc: sb.Append($" splitter={sc.SplitterDistance}/{sc.SplitterWidth}"); break;
                }
                sb.Append('\n');
            }
        }

        private static IEnumerable<SWF.Control> All(SWF.Control root)
        {
            foreach (SWF.Control c in root.Controls)
            {
                yield return c;
                if (c is SWF.UserControl || c is SWF.DataGridView || c is SWF.NumericUpDown || c is SWF.ComboBox) continue;
                foreach (SWF.Control d in All(c)) yield return d;
            }
        }

        private sealed class TableForm : SWF.Form
        {
            public TableForm(SWF.AutoScaleMode mode)
            {
                DpiChanged += (s, e) => Log.Append($"Form.DpiChanged {e.DeviceDpiOld}->{e.DeviceDpiNew} suggested={e.SuggestedRectangle}\n");
                SuspendLayout();
                var button = new SWF.Button { Name = "button", Text = "OK", Location = new Point(12, 12), Size = new Size(75, 23) };
                var label = new SWF.Label { Name = "label", Text = "Label text", AutoSize = true, Location = new Point(100, 16) };
                var text = new SWF.TextBox { Name = "text", Text = "abc", Location = new Point(12, 44), Size = new Size(120, 23) };
                var combo = new SWF.ComboBox { Name = "combo", Location = new Point(140, 44), Size = new Size(121, 23) };
                combo.Items.AddRange(new object[] { "one", "two" });
                var check = new SWF.CheckBox { Name = "check", Text = "Check", AutoSize = true, Location = new Point(270, 46) };
                var list = new SWF.ListBox { Name = "list", Location = new Point(12, 76), Size = new Size(120, 94) };
                list.Items.AddRange(new object[] { "a", "b", "c" });
                var nud = new SWF.NumericUpDown { Name = "nud", Location = new Point(140, 76), Size = new Size(120, 23) };
                var panel = new SWF.Panel { Name = "panel", BorderStyle = SWF.BorderStyle.FixedSingle, Location = new Point(270, 76), Size = new Size(120, 94) };
                var inner = new SWF.Button { Name = "inner", Text = "In", Location = new Point(8, 8), Size = new Size(60, 23) };
                panel.Controls.Add(inner);
                var group = new SWF.GroupBox { Name = "group", Text = "Group", Location = new Point(12, 180), Size = new Size(180, 80) };
                var radio = new SWF.RadioButton { Name = "radio", Text = "Radio", AutoSize = true, Location = new Point(10, 22) };
                group.Controls.Add(radio);
                var tree = new SWF.TreeView { Name = "tree", Location = new Point(200, 180), Size = new Size(120, 80) };
                tree.Nodes.Add("root").Nodes.Add("child");
                var lv = new SWF.ListView { Name = "lv", View = SWF.View.Details, Location = new Point(330, 180), Size = new Size(160, 80) };
                lv.Columns.Add("Name", 80); lv.Columns.Add("Value", 60);
                var grid = new SWF.DataGridView { Name = "grid", Location = new Point(12, 270), Size = new Size(240, 100) };
                grid.Columns.Add("a", "A"); grid.Columns.Add("b", "B");
                var tabs = new SWF.TabControl { Name = "tabs", Location = new Point(260, 270), Size = new Size(230, 100) };
                tabs.TabPages.Add("one"); tabs.TabPages.Add("two");
                var menu = new SWF.MenuStrip { Name = "menu" };
                menu.Items.Add("File"); menu.Items.Add("Edit");
                var tool = new SWF.ToolStrip { Name = "tool" };
                tool.Items.Add(new SWF.ToolStripButton("Btn")); tool.Items.Add(new SWF.ToolStripSeparator()); tool.Items.Add(new SWF.ToolStripComboBox("tcb") { Size = new Size(80, 23) });
                var status = new SWF.StatusStrip { Name = "status" };
                status.Items.Add(new SWF.ToolStripStatusLabel("Ready"));
                var progress = new SWF.ProgressBar { Name = "progress", Location = new Point(12, 380), Size = new Size(200, 23), Value = 40 };
                var track = new SWF.TrackBar { Name = "track", Location = new Point(220, 380), Size = new Size(150, 45) };
                var split = new SWF.SplitContainer { Name = "split", Location = new Point(380, 380), Size = new Size(110, 60), SplitterDistance = 50 };
                foreach (SWF.Control c in new SWF.Control[] { button, label, text, combo, check, list, nud, panel, group, tree, lv, grid, tabs, progress, track, split, tool, menu, status })
                {
                    SWF.Control cc = c;
                    // As an application calling SetCompatibleTextRenderingDefault(false) has it.
                    if (c is SWF.Label l) l.UseCompatibleTextRendering = false;
                    if (c is SWF.ButtonBase b) b.UseCompatibleTextRendering = false;
                    foreach (SWF.Control k in c.Controls)
                        if (k is SWF.ButtonBase kb) kb.UseCompatibleTextRendering = false;
                    cc.DpiChangedBeforeParent += (s, e) => Log.Append($"{cc.Name}.DpiChangedBeforeParent dpi={cc.DeviceDpi}\n");
                    cc.DpiChangedAfterParent += (s, e) => Log.Append($"{cc.Name}.DpiChangedAfterParent dpi={cc.DeviceDpi}\n");
                    Controls.Add(c);
                }
                MainMenuStrip = menu;
                if (mode == SWF.AutoScaleMode.Font) AutoScaleDimensions = new SizeF(7F, 15F);
                else if (mode == SWF.AutoScaleMode.Dpi) AutoScaleDimensions = new SizeF(96F, 96F);
                AutoScaleMode = mode;
                ClientSize = new Size(500, 470);
                Text = "dpitab";
                ResumeLayout(false);
                PerformLayout();
            }

            protected override void OnDpiChanged(SWF.DpiChangedEventArgs e)
            {
                Log.Append($"Form.OnDpiChanged {e.DeviceDpiOld}->{e.DeviceDpiNew}\n");
                base.OnDpiChanged(e);
            }
        }
    }
}
