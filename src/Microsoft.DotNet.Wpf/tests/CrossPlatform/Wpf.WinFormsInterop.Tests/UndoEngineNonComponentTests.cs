// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// A change raised for an object that is not a component -- ICONICS raises ComponentChanged for a WPF
// control as its configurator closes -- is no business of undo, as in .NET: the UndoEngine records
// nothing for it. Mono's cast it to IComponent and threw InvalidCastException out of the form's
// closing.
//

using System;
using System.ComponentModel;
using System.ComponentModel.Design;
using Xunit;

namespace Wpf.WinFormsInterop.Tests
{
    public class UndoEngineNonComponentTests
    {
        private sealed class Undo : UndoEngine
        {
            public int Added;
            public Undo(IServiceProvider provider) : base(provider) { }
            protected override void AddUndoUnit(UndoUnit unit) => Added++;
        }

        [Fact]
        public void AChangeOfANonComponent_IsIgnoredByUndo()
        {
            using var surface = new DesignSurface();
            surface.BeginLoad(typeof(Component));
            var container = (IServiceContainer)surface.GetService(typeof(IServiceContainer))!;
            var undo = new Undo(surface);
            container.AddService(typeof(UndoEngine), undo);
            var changes = (IComponentChangeService)surface.GetService(typeof(IComponentChangeService))!;

            object notAComponent = new object();
            changes.OnComponentChanging(notAComponent, null);
            changes.OnComponentChanged(notAComponent, null, null, null);
            // ... and a real change still makes its unit.
            var host = (IDesignerHost)surface.GetService(typeof(IDesignerHost))!;
            changes.OnComponentChanging(host.RootComponent, null);
            changes.OnComponentChanged(host.RootComponent, null, null, null);
            Assert.True(undo.Added >= 1);
        }
    }
}
