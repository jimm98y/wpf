// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// The designer's behavior/glyph/adorner layer and designer actions, ported from dotnet/winforms: every member of
// Mono's BehaviorService, Behavior, Adorner, Glyph (whose constructor threw), SnapLine, DesignerActionService and
// ControlDesigner's InitializeNewComponent / GetGlyphs / SnapLines / InheritanceAttribute threw
// NotImplementedException. Expectations are .NET 10's.
//

using System;
using System.Collections;
using System.ComponentModel;
using System.ComponentModel.Design;
using System.Drawing;
using System.Linq;
using System.Windows.Forms.Design;
using System.Windows.Forms.Design.Behavior;
using Xunit;
using SWF = System.Windows.Forms;

namespace Wpf.WinFormsInterop.Tests
{
    public class DesignerBehaviorTests
    {
        private sealed class RecordingBehavior : Behavior
        {
            public int LostCapture;
            public RecordingBehavior() { }
            public RecordingBehavior(BehaviorService svc) : base(true, svc) { }
            public override void OnLoseCapture(Glyph g, EventArgs e) => LostCapture++;
        }

        private sealed class DownBehavior : Behavior
        {
            public int Downs;
            public override bool OnMouseDown(Glyph g, SWF.MouseButtons button, Point mouseLoc) { Downs++; return true; }
        }

        private static (DesignSurface Surface, IDesignerHost Host, BehaviorService Behaviors) Surface()
        {
            var surface = new DesignSurface();
            surface.BeginLoad(typeof(SWF.Form));
            var host = (IDesignerHost)surface.GetService(typeof(IDesignerHost))!;
            var behaviors = (BehaviorService)surface.GetService(typeof(BehaviorService))!;
            return (surface, host, behaviors);
        }

        [Fact]
        public void TheRootDesigner_OffersABehaviorService()
        {
            (DesignSurface surface, IDesignerHost host, BehaviorService behaviors) = Surface();
            using (surface)
            {
                Assert.NotNull(behaviors);
                Assert.Null(behaviors.CurrentBehavior);
                Assert.Empty(behaviors.Adorners);
            }
        }

        [Fact]
        public void TheBehaviorStack_PushesPopsAndChains_AsDotNet()
        {
            (DesignSurface surface, _, BehaviorService svc) = Surface();
            using (surface)
            {
                var bottom = new RecordingBehavior();
                var top = new RecordingBehavior(svc);
                svc.PushBehavior(bottom);
                svc.PushBehavior(top);
                Assert.Same(top, svc.CurrentBehavior);
                Assert.Same(bottom, svc.GetNextBehavior(top));
                Assert.Null(svc.GetNextBehavior(bottom));
                Assert.Same(top, svc.PopBehavior(top));
                Assert.Same(bottom, svc.CurrentBehavior);
                Assert.Same(bottom, svc.PopBehavior(bottom));
                Assert.Throws<InvalidOperationException>(() => svc.PopBehavior(bottom));
                Assert.Throws<ArgumentNullException>(() => svc.PushBehavior(null!));
            }
        }

        [Fact]
        public void PushingOverACaptureBehavior_TellsItItLostTheCapture()
        {
            (DesignSurface surface, _, BehaviorService svc) = Surface();
            using (surface)
            {
                var capture = new RecordingBehavior();
                svc.PushCaptureBehavior(capture);
                svc.PushBehavior(new RecordingBehavior());
                Assert.Equal(1, capture.LostCapture);
            }
        }

        [Fact]
        public void ABehaviorWithCallParent_RequiresAService_AndDefersToTheNextOne()
        {
            Assert.Throws<ArgumentException>(() => new RecordingBehavior(null!));
            (DesignSurface surface, _, BehaviorService svc) = Surface();
            using (surface)
            {
                var next = new DownBehavior();
                var top = new RecordingBehavior(svc);
                svc.PushBehavior(next);
                svc.PushBehavior(top);
                Assert.True(top.OnMouseDown(null!, SWF.MouseButtons.Left, Point.Empty));
                Assert.Equal(1, next.Downs);
                // Without a parent to call, a behavior defers to its glyph's behavior.
                var glyphBehavior = new DownBehavior();
                var glyph = new ControlBodyGlyph(new Rectangle(0, 0, 10, 10), SWF.Cursors.Hand, null!, glyphBehavior);
                Assert.True(new RecordingBehavior().OnMouseDown(glyph, SWF.MouseButtons.Left, Point.Empty));
                Assert.Equal(1, glyphBehavior.Downs);
                Assert.Same(SWF.Cursors.Default, new RecordingBehavior().Cursor);
            }
        }

        [Fact]
        public void AnAdornerAddedToTheService_BelongsToIt()
        {
            (DesignSurface surface, _, BehaviorService svc) = Surface();
            using (surface)
            {
                var adorner = new Adorner();
                Assert.True(adorner.Enabled);
                Assert.Null(adorner.BehaviorService);
                svc.Adorners.Add(adorner);
                Assert.Same(svc, adorner.BehaviorService);
                adorner.Glyphs.Add(new ControlBodyGlyph(new Rectangle(1, 2, 3, 4), SWF.Cursors.Hand, null!, (Behavior)null!));
                adorner.Enabled = false;
                adorner.Invalidate();
                Assert.False(adorner.Enabled);
                Assert.Single(adorner.Glyphs);
            }
        }

        [Fact]
        public void Glyphs_HitTestTheirBounds()
        {
            var body = new ControlBodyGlyph(new Rectangle(10, 10, 20, 20), SWF.Cursors.SizeAll, null!, (Behavior)null!);
            Assert.Same(SWF.Cursors.SizeAll, body.GetHitTest(new Point(15, 15)));
            Assert.Null(body.GetHitTest(new Point(5, 5)));
            Assert.Equal(new Rectangle(10, 10, 20, 20), body.Bounds);
            // A hidden control's body is not there.
            using var hidden = new SWF.Button { Visible = false };
            var hiddenBody = new ControlBodyGlyph(new Rectangle(0, 0, 50, 50), SWF.Cursors.Hand, hidden, (Behavior)null!);
            Assert.Null(hiddenBody.GetHitTest(new Point(5, 5)));
            var component = new ComponentGlyph(null!);
            Assert.Null(component.GetHitTest(Point.Empty));
            Assert.Null(component.Behavior);
            Assert.Equal(Rectangle.Empty, component.Bounds);
        }

        [Fact]
        public void SnapLines_FollowDotNetsRules()
        {
            var line = new SnapLine(SnapLineType.Top, 5);
            Assert.Equal(SnapLinePriority.Low, line.Priority);
            Assert.True(line.IsHorizontal);
            line.AdjustOffset(3);
            Assert.Equal(8, line.Offset);
            Assert.Equal("SnapLine: {type = Top, offset = 8, priority = Low, filter = <null>}", line.ToString());
            Assert.True(SnapLine.ShouldSnap(new SnapLine(SnapLineType.Left, 0), new SnapLine(SnapLineType.Left, 9)));
            Assert.False(SnapLine.ShouldSnap(new SnapLine(SnapLineType.Left, 0), new SnapLine(SnapLineType.Right, 0)));
            // Margins attract their opposites and the like padding; paddings attract the like margin.
            Assert.True(SnapLine.ShouldSnap(new SnapLine(SnapLineType.Vertical, 0, "Margin.Right"), new SnapLine(SnapLineType.Vertical, 0, "Margin.Left")));
            Assert.True(SnapLine.ShouldSnap(new SnapLine(SnapLineType.Vertical, 0, "Margin.Right"), new SnapLine(SnapLineType.Vertical, 0, "Padding.Right")));
            Assert.False(SnapLine.ShouldSnap(new SnapLine(SnapLineType.Vertical, 0, "Margin.Right"), new SnapLine(SnapLineType.Vertical, 0, "Margin.Right")));
            Assert.True(SnapLine.ShouldSnap(new SnapLine(SnapLineType.Vertical, 0, "Padding.Left"), new SnapLine(SnapLineType.Vertical, 0, "Margin.Left")));
            Assert.False(SnapLine.ShouldSnap(new SnapLine(SnapLineType.Vertical, 0, "x"), new SnapLine(SnapLineType.Vertical, 0, null!)));
            Assert.True(SnapLine.ShouldSnap(new SnapLine(SnapLineType.Vertical, 0, "x"), new SnapLine(SnapLineType.Vertical, 0, "x")));
        }

        [Fact]
        public void ANewControl_TakesItsSiteName_JoinsItsParent_AndHasDotNetsGlyphsAndSnapLines()
        {
            (DesignSurface surface, IDesignerHost host, BehaviorService svc) = Surface();
            using (surface)
            {
                var form = (SWF.Form)host.RootComponent;
                var button = (SWF.Button)host.CreateComponent(typeof(SWF.Button), "button1");
                var designer = (ControlDesigner)host.GetDesigner(button)!;
                designer.InitializeNewComponent(new Hashtable { ["Parent"] = form, ["Size"] = new Size(75, 23) });
                Assert.Equal("button1", button.Text);
                Assert.Same(form, button.Parent);
                Assert.Equal(new Size(75, 23), button.Size);

                Assert.Empty(designer.GetGlyphs(GlyphSelectionType.NotSelected));
                GlyphCollection glyphs = designer.GetGlyphs(GlyphSelectionType.SelectedPrimary);
                // A fully sizeable control: eight grab handles and four borders.
                Assert.Equal(12, glyphs.Count);

                IList lines = designer.SnapLines;
                Assert.Equal(8, lines.Count);
                Assert.Contains(lines.Cast<SnapLine>(), l => l.SnapLineType == SnapLineType.Bottom && l.Offset == button.Height - 1);
                Assert.Contains(lines.Cast<SnapLine>(), l => l.Filter == "Margin.Right" && l.Offset == button.Width + button.Margin.Right);
                Assert.True(designer.ParticipatesWithSnapLines);
                Assert.False(designer.AutoResizeHandles);
                designer.AutoResizeHandles = true;
                Assert.True(designer.AutoResizeHandles);
                Assert.NotNull(designer.AccessibilityObject);
                Assert.Equal("button1", designer.AccessibilityObject.Name);
            }
        }

        public sealed class VerbDesigner : ComponentDesigner
        {
            public override DesignerVerbCollection Verbs => new DesignerVerbCollection { new DesignerVerb("Do it", (s, e) => { }) };
        }

        [Designer(typeof(VerbDesigner))]
        public sealed class VerbComponent : Component { }

        private sealed class OneItemList : DesignerActionList
        {
            public OneItemList(IComponent c) : base(c) { }
            public override DesignerActionItemCollection GetSortedActionItems() => new DesignerActionItemCollection { new DesignerActionHeaderItem("Header") };
        }

        [Fact]
        public void TheDesignerActionService_PushesLists_AndUpgradesVerbs()
        {
            (DesignSurface surface, IDesignerHost host, _) = Surface();
            using (surface)
            {
                var actions = (DesignerActionService)surface.GetService(typeof(DesignerActionService))!;
                Assert.NotNull(actions);
                var component = (IComponent)host.CreateComponent(typeof(VerbComponent), "verbs1");

                // No action lists of its own: its verbs become one list.
                DesignerActionListCollection lists = actions.GetComponentActions(component);
                Assert.Single(lists);
                DesignerActionItemCollection items = lists[0]!.GetSortedActionItems();
                Assert.Single(items);
                Assert.Equal("Do it", items[0].DisplayName);
                Assert.Equal("Verbs", items[0].Category);

                int changes = 0;
                actions.DesignerActionListsChanged += (s, e) => changes++;
                var pushed = new OneItemList(component);
                actions.Add(component, pushed);
                Assert.True(actions.Contains(component));
                Assert.Equal(2, actions.GetComponentActions(component).Count);
                Assert.Single(actions.GetComponentActions(component, ComponentActionsType.Service));
                actions.Remove(pushed);
                Assert.False(actions.Contains(component));
                actions.Add(component, pushed);
                actions.Clear();
                Assert.False(actions.Contains(component));
                Assert.Equal(4, changes);
                Assert.Throws<ArgumentNullException>(() => actions.Contains(null!));

                var ui = (DesignerActionUIService)surface.GetService(typeof(DesignerActionUIService))!;
                Assert.NotNull(ui);
                DesignerActionUIStateChangeType? seen = null;
                ui.DesignerActionUIStateChange += (s, e) => seen = e.ChangeType;
                ui.ShowUI(component);
                Assert.Equal(DesignerActionUIStateChangeType.Show, seen);
                ui.Refresh(component);
                Assert.Equal(DesignerActionUIStateChangeType.Refresh, seen);
                ui.HideUI(component);
                Assert.Equal(DesignerActionUIStateChangeType.Hide, seen);
                Assert.False(ui.ShouldAutoShow(component));   // verb lists never auto-show
            }
        }

        [Fact]
        public void ADesignerCommandSet_AnswersWhatGetCommandsAnswers()
        {
            var set = new DesignerCommandSet();
            Assert.Null(set.Verbs);
            Assert.Null(set.ActionLists);
            Assert.Null(set.GetCommands("Verbs"));
        }
    }
}
