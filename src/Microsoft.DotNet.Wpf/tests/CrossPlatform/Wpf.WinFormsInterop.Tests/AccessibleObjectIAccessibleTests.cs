// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// AccessibleObject's MSAA face (Accessibility.IAccessible), as dotnet/winforms answers it for a client object:
// child id 0 is the object itself, n is GetChild(n - 1), and an object handed back is CHILDID_SELF when it is this
// one. Every member threw NotImplementedException in Mono.
//

using System.Drawing;
using System;
using System.Reflection;
using Xunit;
using SWF = System.Windows.Forms;

namespace Wpf.WinFormsInterop.Tests
{
    public class AccessibleObjectIAccessibleTests
    {
        private sealed class Leaf : SWF.AccessibleObject
        {
            private readonly string _name;
            public int Actions;
            public Leaf(string name) => _name = name;
            public override string Name { get => _name; set { } }
            public override SWF.AccessibleRole Role => SWF.AccessibleRole.PushButton;
            public override Rectangle Bounds => new Rectangle(1, 2, 3, 4);
            public override void DoDefaultAction() => Actions++;
        }

        private sealed class Parent : SWF.AccessibleObject
        {
            public readonly Leaf A = new("a"), B = new("b");
            public string? SetName;
            public override string Name { get => "parent"; set => SetName = value; }
            public override SWF.AccessibleRole Role => SWF.AccessibleRole.Grouping;
            public override SWF.AccessibleStates State => SWF.AccessibleStates.Focused;
            public override int GetChildCount() => 2;
            public override SWF.AccessibleObject? GetChild(int index) => index == 0 ? A : index == 1 ? B : null;
            public override SWF.AccessibleObject? HitTest(int x, int y) => x < 10 ? this : B;
        }

        /// <summary>The object's Accessibility.IAccessible members, called through the interface (no reference to
        /// Accessibility.dll needed).</summary>
        private sealed class Acc
        {
            private readonly object _o;
            private readonly Type _i;
            public Acc(object o) { _o = o; _i = o.GetType().GetInterface("IAccessible")!; }
            private object? Call(string name, params object?[] args) => _i.GetMethod(name)!.Invoke(_o, args);
            public object? get_accName(object id) => Call("get_accName", id);
            public object? get_accChild(object id) => Call("get_accChild", id);
            public object? get_accRole(object id) => Call("get_accRole", id);
            public object? get_accState(object id) => Call("get_accState", id);
            public int accChildCount => (int)Call("get_accChildCount")!;
            public void accDoDefaultAction(object id) => Call("accDoDefaultAction", id);
            public object? accHitTest(int x, int y) => Call("accHitTest", x, y);
            public void set_accName(object id, string name) => Call("set_accName", id, name);
            public void accLocation(out int x, out int y, out int w, out int h, object id)
            {
                object?[] a = { 0, 0, 0, 0, id };
                Call("accLocation", a);
                x = (int)a[0]!; y = (int)a[1]!; w = (int)a[2]!; h = (int)a[3]!;
            }
        }

        [Fact]
        public void ChildIds_NameTheObjectAndItsChildren()
        {
            var parent = new Parent();
            var acc = new Acc(parent);
            Assert.Equal("parent", acc.get_accName(0));
            Assert.Equal("a", acc.get_accName(1));
            Assert.Equal("b", acc.get_accName(2));
            Assert.Null(acc.get_accName(3));
            Assert.Equal(2, acc.accChildCount);
            Assert.Same(parent.B, acc.get_accChild(2));
            Assert.Same(parent, acc.get_accChild(0));
            Assert.Equal((int)SWF.AccessibleRole.Grouping, acc.get_accRole(0));
            Assert.Equal((int)SWF.AccessibleRole.PushButton, acc.get_accRole(1));
            Assert.Equal((int)SWF.AccessibleStates.Focused, acc.get_accState(0));

            acc.accLocation(out int x, out int y, out int w, out int h, 1);
            Assert.Equal((1, 2, 3, 4), (x, y, w, h));

            acc.accDoDefaultAction(2);
            Assert.Equal(1, parent.B.Actions);

            // An object handed back is CHILDID_SELF when it is this one.
            Assert.Equal(0, acc.accHitTest(5, 5));
            Assert.Same(parent.B, acc.accHitTest(50, 5));

            acc.set_accName(0, "renamed");
            Assert.Equal("renamed", parent.SetName);
        }
    }
}
