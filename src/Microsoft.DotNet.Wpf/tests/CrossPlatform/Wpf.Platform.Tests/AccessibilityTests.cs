// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// The parts of the six accessibility backends that can be tested without an assistive technology.
//
// Every backend answers the same questions -- what role is this, what state, what can I do to it --
// so the interesting failures are in the translation tables, and those are pure functions. A fake
// IAutomationTreeSource stands in for the peer tree, which keeps these tests free of a Dispatcher,
// a window, and any platform at all: they run identically on all three operating systems, and they
// are the only coverage Linux gets on a developer machine, since AT-SPI needs a session bus, an
// accessibility bus and Orca to exercise for real.
//
// What is deliberately NOT here: the native transports. Driving NSAccessibility or libdbus belongs
// in the per-platform ABI tests (CocoaTextInputTests is the model), and D-Bus has no equivalent
// because there is no bus on this machine to talk to.
//

using System;
using System.Collections.Generic;
using MS.Internal.Interop;
using MS.Internal.Interop.Wayland;
using Xunit;

namespace Wpf.Platform.Tests
{
    public class AccessibilityTests
    {
        // The 39 AutomationControlType values. Spelled out rather than derived from the enum, which
        // lives in PresentationCore and is not referenced here -- and so that adding a control type
        // upstream shows up as a test that needs updating rather than silently mapping to "unknown".
        private static IEnumerable<int> AllControlTypes()
        {
            for (int i = 0; i <= 38; i++) yield return i;
        }

        [Fact]
        public void AtSpi_MapsEveryControlTypeToARole()
        {
            foreach (int type in AllControlTypes())
            {
                uint role = AtSpiRoles.RoleFor(type);
                Assert.NotEqual(0u, role);          // 0 is ATSPI_ROLE_INVALID
            }
        }

        [Fact]
        public void AtSpi_RoleNamesAreTheSpecSpelling()
        {
            // Orca speaks these verbatim, so a typo is audible. Sampling the ones a user meets first.
            Assert.Equal("push button", AtSpiRoles.RoleNameFor(0));
            Assert.Equal("check box", AtSpiRoles.RoleNameFor(2));
            Assert.Equal("combo box", AtSpiRoles.RoleNameFor(3));
            Assert.Equal("slider", AtSpiRoles.RoleNameFor(15));
            Assert.Equal("radio button", AtSpiRoles.RoleNameFor(13));
        }

        [Fact]
        public void AtSpi_StatesAreABitSetNotAnEnum()
        {
            const int Enabled = 8, Focusable = 12, Focused = 13, Showing = 21, Visible = 22, Checked = 4;

            ulong states = AtSpiRoles.StatesFor(
                AccessibleState.Enabled | AccessibleState.Focusable | AccessibleState.Focused);

            Assert.True((states & (1UL << Enabled)) != 0);
            Assert.True((states & (1UL << Focusable)) != 0);
            Assert.True((states & (1UL << Focused)) != 0);
            Assert.True((states & (1UL << Checked)) == 0);

            // VISIBLE and SHOWING are different questions: an item scrolled out of view still exists.
            Assert.True((states & (1UL << Visible)) != 0);
            Assert.True((states & (1UL << Showing)) != 0);

            ulong offscreen = AtSpiRoles.StatesFor(AccessibleState.Enabled | AccessibleState.Offscreen);
            Assert.True((offscreen & (1UL << Visible)) != 0);
            Assert.True((offscreen & (1UL << Showing)) == 0);
        }

        [Fact]
        public void AtSpi_CheckedImpliesCheckable()
        {
            const int Checked = 4, Checkable = 34, Indeterminate = 15;

            ulong on = AtSpiRoles.StatesFor(AccessibleState.Checked);
            Assert.True((on & (1UL << Checked)) != 0);
            Assert.True((on & (1UL << Checkable)) != 0);

            // A three-state box that is in its third state is checkable but not checked; a client
            // that saw neither bit would announce it as a plain label.
            ulong mixed = AtSpiRoles.StatesFor(AccessibleState.Indeterminate);
            Assert.True((mixed & (1UL << Indeterminate)) != 0);
            Assert.True((mixed & (1UL << Checkable)) != 0);
            Assert.True((mixed & (1UL << Checked)) == 0);
        }

        [Fact]
        public void AtSpi_ObjectPathsRoundTrip()
        {
            Assert.Equal("/org/a11y/atspi/accessible/42", AtSpiBridge.PathForNode(42));
            Assert.Equal(42, AtSpiBridge.NodeFromPath("/org/a11y/atspi/accessible/42"));

            // Clients probe paths that do not exist. Every one of these has to answer "no node"
            // rather than throw, because the answer is computed inside libdbus's dispatcher.
            Assert.Equal(-1, AtSpiBridge.NodeFromPath("/org/a11y/atspi/accessible/null"));
            Assert.Equal(-1, AtSpiBridge.NodeFromPath("/org/a11y/atspi/accessible/"));
            Assert.Equal(-1, AtSpiBridge.NodeFromPath("/some/other/path"));
            Assert.Equal(-1, AtSpiBridge.NodeFromPath(string.Empty));
            Assert.Equal(-1, AtSpiBridge.NodeFromPath("/org/a11y/atspi/accessible/notanumber"));
        }

        [Fact]
        public void AtSpi_ActionIndicesAreStable()
        {
            // AT-SPI addresses actions BY INDEX: DoAction(1) has to mean the same thing as the
            // GetName(1) the client read a moment earlier, so the order must depend only on which
            // actions the node supports.
            var tree = new FakeTree();
            tree.Actions[7] = new[] { AccessibleAction.Toggle, AccessibleAction.Invoke };

            AccessibleAction[] first = AtSpiBridge.AvailableActions(tree, 7);
            AccessibleAction[] second = AtSpiBridge.AvailableActions(tree, 7);

            Assert.Equal(first, second);
            Assert.Equal(new[] { AccessibleAction.Invoke, AccessibleAction.Toggle }, first);
            Assert.Equal("click", AtSpiBridge.NameOf(first[0]));
            Assert.Equal("toggle", AtSpiBridge.NameOf(first[1]));

            Assert.Empty(AtSpiBridge.AvailableActions(tree, 99));
        }

        [Fact]
        public void Android_EveryControlTypeHasAClassName()
        {
            // On Android the class name IS the role: TalkBack keys its announcements off it, and an
            // empty or malformed one is silently ignored.
            foreach (int type in AllControlTypes())
            {
                string name = AndroidClassNames.For(type);
                Assert.False(string.IsNullOrEmpty(name));
                Assert.StartsWith("android.", name, StringComparison.Ordinal);
            }
        }

        [Fact]
        public void Android_NodeJsonCarriesWhatTalkBackReads()
        {
            var tree = new FakeTree();
            tree.Roles[3] = 0;                                  // Button
            tree.Names[3] = "Save";
            tree.Bounds[3] = (10, 20, 100, 40);
            tree.States[3] = AccessibleState.Enabled | AccessibleState.Focusable;
            tree.Actions[3] = new[] { AccessibleAction.Invoke };

            AutomationTree.Current = tree;
            try
            {
                string json = AndroidAccessibility.GetNodeJson(3);

                Assert.Contains("\"cls\":\"android.widget.Button\"", json);
                Assert.Contains("\"name\":\"Save\"", json);
                Assert.Contains("\"x\":10", json);
                Assert.Contains("\"w\":100", json);
                Assert.Contains("\"en\":1", json);
                Assert.Contains("\"fo\":1", json);
                Assert.Contains("\"act\":1", json);              // click

                // A node that has gone answers with an empty string, which the head turns into a
                // null AccessibilityNodeInfo -- Android's way of being told a virtual node is gone.
                Assert.Equal(string.Empty, AndroidAccessibility.GetNodeJson(404));
            }
            finally { AutomationTree.Current = null; }
        }

        [Fact]
        public void Android_JsonEscapesNamesThatWouldBreakIt()
        {
            var tree = new FakeTree();
            tree.Roles[1] = 20;                                  // Text
            tree.Names[1] = "He said \"hi\"\\\n";

            AutomationTree.Current = tree;
            try
            {
                string json = AndroidAccessibility.GetNodeJson(1);
                Assert.Contains("\\\"hi\\\"", json);
                Assert.Contains("\\\\", json);
                Assert.Contains("\\n", json);
                Assert.DoesNotContain('\n', json);
            }
            finally { AutomationTree.Current = null; }
        }

        [Fact]
        public void Android_CheckableIsSeparateFromChecked()
        {
            var tree = new FakeTree();
            tree.Roles[5] = 2;                                   // CheckBox
            tree.Actions[5] = new[] { AccessibleAction.Toggle };

            AutomationTree.Current = tree;
            try
            {
                // TalkBack says "tick box" and "tick box, ticked" from these two bits, so a node
                // that can be toggled must claim checkable even while it is unchecked.
                string unchecked_ = AndroidAccessibility.GetNodeJson(5);
                Assert.Contains("\"ck\":1", unchecked_);
                Assert.Contains("\"chk\":0", unchecked_);

                tree.States[5] = AccessibleState.Checked;
                string checked_ = AndroidAccessibility.GetNodeJson(5);
                Assert.Contains("\"ck\":1", checked_);
                Assert.Contains("\"chk\":1", checked_);
            }
            finally { AutomationTree.Current = null; }
        }

        [Fact]
        public void Android_ClickPrefersToggleOverInvoke()
        {
            var tree = new FakeTree();
            tree.Actions[8] = new[] { AccessibleAction.Invoke, AccessibleAction.Toggle };

            AutomationTree.Current = tree;
            try
            {
                // A CheckBox offers both. Clicking it must toggle, not invoke: invoking a toggle
                // control is a no-op in some peers and would leave TalkBack announcing no change.
                Assert.True(AndroidAccessibility.PerformAction(8, 0, null));
                Assert.Equal(AccessibleAction.Toggle, tree.LastAction);
            }
            finally { AutomationTree.Current = null; }
        }

        [Fact]
        public void Browser_ControlTypesMapToValidAriaRoles()
        {
            // ARIA has no role for plain text, a custom control or the window itself, and inventing
            // one is worse than none: an unrecognised token makes the browser drop the element from
            // the accessibility tree entirely. Those nodes are exposed as text content instead (see
            // the generic-element branch in browser-window.js a11ySync).
            var roleless = new HashSet<int> { 20, 25, 27, 32 };

            foreach (int type in AllControlTypes())
            {
                string role = BrowserRoles.RoleFor(type);

                if (roleless.Contains(type))
                {
                    Assert.Equal(string.Empty, role);
                    continue;
                }

                Assert.False(string.IsNullOrEmpty(role));
                // Role tokens are lowercase and unspaced; anything else is dropped by browsers.
                Assert.Equal(role.ToLowerInvariant(), role);
                Assert.DoesNotContain(' ', role);
            }
        }

        [Fact]
        public void Cocoa_EveryControlTypeMapsToAnAxRole()
        {
            foreach (int type in AllControlTypes())
            {
                string role = CocoaRoles.RoleFor(type);
                Assert.False(string.IsNullOrEmpty(role));
                Assert.StartsWith("AX", role, StringComparison.Ordinal);
            }
        }

        [Fact]
        public void InvalidNodesAnswerRatherThanThrow()
        {
            // Every backend is driven from a native callback -- a libdbus dispatcher, an Objective-C
            // IMP, a JNI call -- where a managed exception has nowhere to go. Asking about a node
            // that has been collected is routine, not exceptional.
            var tree = new FakeTree();

            Assert.Empty(AtSpiBridge.AvailableActions(tree, -1));
            Assert.Equal(0u, AtSpiRoles.RoleFor(-1) & 0u);
            Assert.Equal("unknown", AtSpiRoles.RoleNameFor(-1));
            Assert.Equal("android.view.View", AndroidClassNames.For(-1));
        }

        /// <summary>
        /// A peer tree that is just a set of dictionaries. Anything not populated answers the way a
        /// collected node does, which is what most of these tests are checking.
        /// </summary>
        private sealed class FakeTree : IAutomationTreeSource
        {
            internal readonly Dictionary<int, int> Roles = new();
            internal readonly Dictionary<int, string> Names = new();
            internal readonly Dictionary<int, AccessibleState> States = new();
            internal readonly Dictionary<int, AccessibleAction[]> Actions = new();
            internal readonly Dictionary<int, (double X, double Y, double W, double H)> Bounds = new();
            internal readonly Dictionary<int, int[]> Children = new();

            internal AccessibleAction LastAction = (AccessibleAction)(-1);

            public int GetRootId(IntPtr windowHandle) => 0;

            public int[] GetChildIds(int id) => Children.TryGetValue(id, out int[] c) ? c : Array.Empty<int>();

            public int GetParentId(int id) => IAutomationTreeSource.InvalidNode;

            public int GetRole(int id) => Roles.TryGetValue(id, out int r) ? r : (Names.ContainsKey(id) ? 20 : -1);

            public string GetName(int id) => Names.TryGetValue(id, out string n) ? n : string.Empty;

            public string GetAutomationId(int id) => string.Empty;

            public string GetHelpText(int id) => string.Empty;

            public string GetValue(int id) => string.Empty;

            public bool TryGetRange(int id, out double value, out double minimum, out double maximum)
            {
                value = minimum = maximum = 0;
                return false;
            }

            public AccessibleState GetState(int id) => States.TryGetValue(id, out AccessibleState s) ? s : default;

            public bool TryGetBounds(int id, out double x, out double y, out double width, out double height)
            {
                if (Bounds.TryGetValue(id, out (double X, double Y, double W, double H) b))
                {
                    (x, y, width, height) = (b.X, b.Y, b.W, b.H);
                    return true;
                }
                x = y = width = height = 0;
                return false;
            }

            public int HitTest(IntPtr windowHandle, double screenX, double screenY)
                => IAutomationTreeSource.InvalidNode;

            public int GetFocusedId(IntPtr windowHandle) => IAutomationTreeSource.InvalidNode;

            public bool SupportsAction(int id, AccessibleAction action)
                => Actions.TryGetValue(id, out AccessibleAction[] a) && Array.IndexOf(a, action) >= 0;

            public bool DoAction(int id, AccessibleAction action)
            {
                if (!SupportsAction(id, action)) return false;
                LastAction = action;
                return true;
            }

            public bool SetValue(int id, string value) => false;

            public bool SetFocus(int id) => false;
        }
    }
}
