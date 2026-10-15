// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// The order in which a designer form's controls see SetBoundsCore / OnLayout / OnResize /
// OnFontChanged / OnHandleCreated, recorded from stock .NET 10 WinForms with the same form.
//
// DotNetBar's ribbon sizes its bars from these passes: ours gave an anchored child a SetBoundsCore
// on every parent layout, skipped the one Dock gives, named no AffectedControl, and created no
// handle for a control shown under a parent that had none -- so the ribbon's bars were laid out at
// different widths than in stock and its "Style and Colors" group collapsed.
//

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using Xunit;

namespace Wpf.WinFormsInterop.Tests
{
    public class LayoutSequenceTests
    {
        private sealed class Spy : UserControl
        {
            private readonly List<string> _log;
            public Spy(List<string> log) => _log = log;

            protected override void SetBoundsCore(int x, int y, int w, int h, BoundsSpecified s)
            {
                _log.Add($"{Name}.SetBoundsCore {x},{y},{w},{h} {s} handle={IsHandleCreated}");
                base.SetBoundsCore(x, y, w, h, s);
            }

            protected override void OnLayout(LayoutEventArgs e)
            {
                _log.Add($"{Name}.OnLayout by={e.AffectedProperty} affected={e.AffectedControl?.Name} size={Size} handle={IsHandleCreated}");
                base.OnLayout(e);
            }

            protected override void OnResize(EventArgs e)
            {
                _log.Add($"{Name}.OnResize {Size} handle={IsHandleCreated}");
                base.OnResize(e);
            }

            protected override void OnFontChanged(EventArgs e)
            {
                _log.Add($"{Name}.OnFontChanged {Font.SizeInPoints} handle={IsHandleCreated}");
                base.OnFontChanged(e);
            }

            protected override void OnHandleCreated(EventArgs e)
            {
                _log.Add($"{Name}.OnHandleCreated {Size}");
                base.OnHandleCreated(e);
            }
        }

        /// <summary>InitializeComponent as the designer writes it, plus a control shown under a
        /// panel that is in no form yet (DotNetBar's RibbonTabItem shows its panel that way).</summary>
        private static List<string> Record(out Form form)
        {
            var log = new List<string>();
            var f = new Form();
            var outer = new Spy(log) { Name = "outer" };
            var inner = new Spy(log) { Name = "inner" };
            var docked = new Spy(log) { Name = "docked" };
            var righty = new Spy(log) { Name = "righty" };
            var late = new Spy(log) { Name = "late", Visible = false };
            var panel = new Panel { Name = "panel" };
            f.SuspendLayout(); panel.SuspendLayout(); outer.SuspendLayout();
            inner.Location = new Point(3, 0); inner.Size = new Size(258, 90); inner.Anchor = AnchorStyles.Top | AnchorStyles.Left;
            outer.Controls.Add(inner);
            righty.Location = new Point(700, 10); righty.Size = new Size(150, 60); righty.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            outer.Controls.Add(righty);
            late.Location = new Point(10, 10); late.Size = new Size(50, 50); panel.Controls.Add(late);
            outer.Dock = DockStyle.Top; outer.Location = new Point(0, 0); outer.Size = new Size(900, 114); outer.Padding = new Padding(3, 0, 3, 0);
            docked.Dock = DockStyle.Fill; docked.Size = new Size(100, 100);
            panel.Controls.Add(docked); panel.Dock = DockStyle.Fill;
            f.Controls.Add(panel); f.Controls.Add(outer);
            f.AutoScaleDimensions = new SizeF(6F, 13F); f.AutoScaleMode = AutoScaleMode.None;
            f.ClientSize = new Size(900, 600); f.Font = new Font("Microsoft Sans Serif", 8.25F); f.Name = "SpyForm";
            outer.ResumeLayout(false); panel.ResumeLayout(false); f.ResumeLayout(false);
            log.Add("--- show late");
            late.Visible = true;
            var free = new Panel { Name = "free" };
            var lone = new Spy(log) { Name = "lone", Visible = false, Size = new Size(40, 40) };
            free.Controls.Add(lone);
            log.Add("--- show lone");
            lone.Visible = true;
            log.Add($"lone handle={lone.IsHandleCreated} free handle={free.IsHandleCreated} lone created={lone.Created}");
            f.Controls.Add(free); free.Location = new Point(0, 300);
            form = f;
            return log;
        }

        private static readonly string[] s_stock =
        {
            "inner.SetBoundsCore 3,0,150,150 Location handle=False",
            "inner.SetBoundsCore 3,0,258,90 Size handle=False",
            "inner.OnResize {Width=258, Height=90} handle=False",
            "inner.OnLayout by=Bounds affected=inner size={Width=258, Height=90} handle=False",
            "righty.SetBoundsCore 700,10,150,150 Location handle=False",
            "righty.SetBoundsCore 700,10,150,60 Size handle=False",
            "righty.OnResize {Width=150, Height=60} handle=False",
            "righty.OnLayout by=Bounds affected=righty size={Width=150, Height=60} handle=False",
            "late.SetBoundsCore 10,10,150,150 Location handle=False",
            "late.SetBoundsCore 10,10,50,50 Size handle=False",
            "late.OnResize {Width=50, Height=50} handle=False",
            "late.OnLayout by=Bounds affected=late size={Width=50, Height=50} handle=False",
            "outer.SetBoundsCore 0,0,150,150 All handle=False",
            "outer.SetBoundsCore 0,0,900,114 Size handle=False",
            "outer.OnResize {Width=900, Height=114} handle=False",
            "docked.SetBoundsCore 0,0,150,150 All handle=False",
            "docked.SetBoundsCore 0,0,100,100 Size handle=False",
            "docked.OnResize {Width=100, Height=100} handle=False",
            "docked.OnLayout by=Bounds affected=docked size={Width=100, Height=100} handle=False",
            "late.OnFontChanged 8.25 handle=False",
            "late.OnLayout by=Font affected=late size={Width=50, Height=50} handle=False",
            "docked.OnFontChanged 8.25 handle=False",
            "docked.OnLayout by=Font affected=docked size={Width=100, Height=100} handle=False",
            "outer.OnFontChanged 8.25 handle=False",
            "inner.OnFontChanged 8.25 handle=False",
            "inner.OnLayout by=Font affected=inner size={Width=258, Height=90} handle=False",
            "righty.OnFontChanged 8.25 handle=False",
            "righty.OnLayout by=Font affected=righty size={Width=150, Height=60} handle=False",
            "--- show late",
            "docked.SetBoundsCore 0,0,200,100 None handle=False",
            "docked.OnResize {Width=200, Height=100} handle=False",
            "docked.OnLayout by=Bounds affected=docked size={Width=200, Height=100} handle=False",
            "lone.SetBoundsCore 0,0,40,40 Size handle=False",
            "lone.OnResize {Width=40, Height=40} handle=False",
            "lone.OnLayout by=Bounds affected=lone size={Width=40, Height=40} handle=False",
            "--- show lone",
            "lone.OnLayout by=Visible affected=lone size={Width=40, Height=40} handle=False",
            "lone.OnHandleCreated {Width=40, Height=40}",
            "lone handle=True free handle=False lone created=True",
            "lone.OnFontChanged 8.25 handle=True",
            "lone.OnLayout by=Font affected=lone size={Width=40, Height=40} handle=True",
            "outer.SetBoundsCore 0,0,900,114 None handle=False",
            "docked.SetBoundsCore 0,0,900,486 None handle=False",
            "docked.OnResize {Width=900, Height=486} handle=False",
            "docked.OnLayout by=Bounds affected=docked size={Width=900, Height=486} handle=False",
            "outer.SetBoundsCore 0,0,900,114 None handle=False"
        };

        [Fact]
        public void ADesignerForm_SeesStocksLayoutPasses_InStocksOrder()
        {
            List<string> log = Record(out Form f);
            using (f)
                Assert.Equal(string.Join(" | ", s_stock), string.Join(" | ", log));
        }

        [Fact]
        public void AnAnchoredChild_WhoseBoundsDoNotChange_GetsNoSetBoundsCore()
        {
            List<string> log = Record(out Form f);
            using (f)
                Assert.DoesNotContain(log, l => l.StartsWith("inner.SetBoundsCore", StringComparison.Ordinal) && l.Contains(" None "));
        }

        [Fact]
        public void AControlShownUnderAHandlelessParent_IsCreatedAtOnce()
        {
            List<string> log = Record(out Form f);
            using (f)
                Assert.Contains("lone handle=True free handle=False lone created=True", log);
        }
    }
}
