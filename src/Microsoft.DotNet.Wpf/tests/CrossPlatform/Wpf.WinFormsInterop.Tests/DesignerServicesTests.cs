// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// InheritanceService, EventHandlerService and ComponentTray's bookkeeping, ported from dotnet/winforms (each threw
// NotImplementedException in Mono). Expectations are .NET 10's.
//

using System;
using System.ComponentModel;
using System.ComponentModel.Design;
using System.Drawing;
using System.Linq;
using System.Windows.Forms.Design;
using Xunit;
using SWF = System.Windows.Forms;

namespace Wpf.WinFormsInterop.Tests
{
    public class DesignerServicesTests
    {
        public class BaseForm : SWF.Form
        {
            protected SWF.Button visibleButton = new SWF.Button();
            private SWF.Button hiddenButton = new SWF.Button();
            public SWF.Button Hidden => hiddenButton;
            public SWF.Button Visible1 => visibleButton;
        }

        public class DerivedForm : BaseForm { }

        [Fact]
        public void InheritanceService_AddsProtectedBaseComponents_AndRecordsPrivateOnesReadOnly()
        {
            var form = new DerivedForm();
            using var container = new Container();
            using var svc = new InheritanceService();
            svc.AddInheritedComponents(form, container);
            Assert.Equal(InheritanceAttribute.Inherited, svc.GetInheritanceAttribute(form.Visible1));
            Assert.Equal(InheritanceAttribute.InheritedReadOnly, svc.GetInheritanceAttribute(form.Hidden));
            Assert.Equal(InheritanceAttribute.Default, svc.GetInheritanceAttribute(form));
            // Only the visible one joins the container, under its field name.
            Assert.Contains(form.Visible1, container.Components.Cast<IComponent>());
            Assert.DoesNotContain(form.Hidden, container.Components.Cast<IComponent>());
            Assert.Equal("visibleButton", form.Visible1.Site?.Name);
        }

        [Fact]
        public void EventHandlerService_AnswersTheNewestHandlerOfAType()
        {
            var svc = new EventHandlerService(null);
            int changes = 0;
            svc.EventHandlerChanged += (s, e) => changes++;
            Assert.Null(svc.GetHandler(typeof(string)));
            svc.PushHandler("first");
            svc.PushHandler(42);
            svc.PushHandler("second");
            Assert.Equal("second", svc.GetHandler(typeof(string)));
            Assert.Equal(42, svc.GetHandler(typeof(int)));
            svc.PopHandler("second");
            // .NET forgets its cached answer on a pop and then answers null until the next push: kept as it is.
            Assert.Null(svc.GetHandler(typeof(string)));
            svc.PushHandler(7);
            Assert.Equal("first", svc.GetHandler(typeof(string)));
            Assert.Equal(5, changes);
            Assert.Throws<ArgumentNullException>(() => svc.PushHandler(null!));
            Assert.Throws<ArgumentNullException>(() => svc.GetHandler(null!));
        }

        private sealed class TestMask : MaskDescriptor
        {
            private readonly string _mask, _sample; private readonly Type? _type;
            public TestMask(string mask, string sample, Type? type = null) { _mask = mask; _sample = sample; _type = type; }
            public override string Mask => _mask;
            public override string Name => "test";
            public override string Sample => _sample;
            public override Type ValidatingType => _type!;
        }

        [Fact]
        public void MaskDescriptor_ValidatesItsSampleAgainstItsMask()
        {
            Assert.True(MaskDescriptor.IsValidMaskDescriptor(new TestMask("000-0000", "555-1234")));
            Assert.False(MaskDescriptor.IsValidMaskDescriptor(new TestMask("000-0000", "abc"), out string why));
            Assert.StartsWith("Error at position 0: Digit expected", why);
            Assert.False(MaskDescriptor.IsValidMaskDescriptor(null, out why));
            Assert.Equal("The provided MaskDescriptor is null.", why);
            Assert.Equal(new TestMask("00", "12"), new TestMask("00", "34"));
            Assert.NotEqual(new TestMask("00", "12"), new TestMask("000", "123"));
            Assert.Equal(System.Threading.Thread.CurrentThread.CurrentCulture, new TestMask("0", "1").Culture);
        }

        [Fact]
        public void ExceptionCollection_HoldsCopiesOfItsExceptions()
        {
            var inner = new InvalidOperationException("boom");
            var errors = new ExceptionCollection(new System.Collections.ArrayList { inner });
            Assert.Single(errors.Exceptions!);
            Assert.NotSame(errors.Exceptions, errors.Exceptions);   // a copy each time
            Assert.Null(new ExceptionCollection(null!).Exceptions);
            Assert.Throws<ArgumentException>(() => new ExceptionCollection(new System.Collections.ArrayList { "not an exception" }));
        }

        private sealed class ShowingTray : ComponentTray
        {
            public ShowingTray(IDesigner d, IServiceProvider sp) : base(d, sp) { }
            protected override bool CanDisplayComponent(IComponent component) => true;
        }

        [Fact]
        public void ComponentTray_OrdersItsComponents_AndKeepsTheirLocations()
        {
            using var surface = new DesignSurface();
            surface.BeginLoad(typeof(SWF.Form));
            var host = (IDesignerHost)surface.GetService(typeof(IDesignerHost))!;
            using var tray = new ShowingTray(host.GetDesigner(host.RootComponent)!, surface);
            var a = new Component(); var b = new Component(); var c = new Component();
            tray.AddComponent(a); tray.AddComponent(b); tray.AddComponent(b);
            Assert.Equal(2, tray.ComponentCount);
            Assert.True(tray.IsTrayComponent(a));
            Assert.False(tray.IsTrayComponent(c));
            Assert.Same(b, tray.GetNextComponent(a, true));
            Assert.Null(tray.GetNextComponent(b, true));
            Assert.Null(tray.GetNextComponent(a, false));
            Assert.Same(a, tray.GetNextComponent(c, true));     // not in the tray: the first
            Assert.Same(b, tray.GetNextComponent(c, false));    // ...or the last
            tray.SetTrayLocation(a, new Point(12, 34));
            Assert.Equal(new Point(12, 34), tray.GetTrayLocation(a));
            tray.RemoveComponent(a);
            Assert.Equal(1, tray.ComponentCount);
        }
    }
}
