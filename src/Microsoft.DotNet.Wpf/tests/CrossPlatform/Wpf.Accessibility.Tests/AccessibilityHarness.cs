// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// Building a live element and asking what it exposes to a screen reader.
//
// Two constraints shape everything here.
//
// The element must have been through LAYOUT: GetChildren walks the visual tree, so an element that
// was never measured or arranged reports no children at all, and a test that skipped that step
// would see an empty tree and call it a regression.
//
// And it must be built, laid out AND read on ONE STA thread. Constructing a Control brings up the
// InputManager, which refuses to exist off an STA thread, and every DispatcherObject afterwards
// belongs to that thread -- so a helper that returned a live AutomationPeer for the caller to
// inspect would throw "a different thread owns it" on the first property read. Probe therefore
// takes the question as well as the element, and hands back a plain value.
//

using System;
using System.Collections.Generic;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Documents;

namespace Wpf.Accessibility.Tests
{
    internal static class AccessibilityHarness
    {
        /// <summary>
        /// Builds an element, lays it out, and answers <paramref name="read"/> about its automation
        /// peer. The result must be a plain value: anything live belongs to the thread it was made on.
        /// </summary>
        public static T Probe<T>(Func<UIElement> build, Func<AutomationPeer, T> read)
            => Sta(() =>
            {
                UIElement element = build();
                Layout(element);

                AutomationPeer? peer = UIElementAutomationPeer.CreatePeerForElement(element);
                if (peer is null) throw new InvalidOperationException("no automation peer was created for the element");
                return read(peer);
            });

        /// <summary>The names of an element's automation children, in tree order.</summary>
        /// <param name="items">
        /// The children to expose. They go into an ItemsControl because a peer is needed to ask, and
        /// most elements do not have one: WPF creates a peer only where a control overrides
        /// OnCreateAutomationPeer, so a StackPanel returns null (it is a layout container, not a
        /// control) and so does a plain ContentControl. A peer-less element is not absent from the
        /// tree -- its children are surfaced through the nearest ancestor that HAS a peer -- which is
        /// exactly the relationship being tested here.
        /// </param>
        /// The children are built by a FACTORY rather than passed in: they have to be constructed on
        /// the STA thread too, and a caller writing `ChildNames(new Button(), ...)` would construct
        /// them on xunit's own MTA thread and throw before this method was even entered.
        public static List<string> ChildNames(Func<UIElement[]> items)
            => Probe(() =>
                     {
                         var host = new ItemsControl();
                         foreach (UIElement item in items()) host.Items.Add(item);
                         return host;
                     },
                     peer =>
            {
                var names = new List<string>();
                foreach (AutomationPeer c in peer.GetChildren() ?? new List<AutomationPeer>())
                {
                    names.Add(c.GetName());
                }
                return names;
            });

        /// <summary>
        /// The text a FlowDocument exposes: through the viewer's peer and the Text pattern, which is
        /// the route a screen reader takes.
        /// </summary>
        public static string DocumentText(Func<FlowDocument> build)
            => Probe(() => new FlowDocumentScrollViewer
                     {
                         Document = build(),
                         Width = 400,
                         Height = 300,
                         // Scrollbars left ON deliberately. They are drawn with Shape/StreamGeometry,
                         // so they are what drags Geometry.Bounds into this test -- and that used to
                         // throw DllNotFoundException for wpfgfx_cor3.dll here, which is how the
                         // native bounds dependency was found in the first place. Bounds are computed
                         // managed on every platform now, and leaving the chrome on keeps that
                         // covered rather than stepping around it.
                     },
                     peer =>
                     {
                         // The viewer delegates the document itself to a child peer, so look there too.
                         string text = TextOf(peer);
                         if (text.Length > 0) return text;

                         foreach (AutomationPeer child in peer.GetChildren() ?? new List<AutomationPeer>())
                         {
                             text = TextOf(child);
                             if (text.Length > 0) return text;
                         }
                         return string.Empty;
                     });

        private static string TextOf(AutomationPeer peer)
        {
            if (peer.GetPattern(PatternInterface.Text) is not ITextProvider text) return string.Empty;
            ITextRangeProvider? range = text.DocumentRange;
            return range?.GetText(-1) ?? string.Empty;
        }

        // Big enough that nothing is clipped out of the tree.
        private static void Layout(UIElement element)
        {
            element.Measure(new Size(1000, 1000));
            element.Arrange(new Rect(0, 0, 1000, 1000));
            element.UpdateLayout();
        }

        /// <summary>Runs a body on a dedicated STA thread and rethrows anything it threw.</summary>
        public static T Sta<T>(Func<T> body)
        {
            T result = default!;
            Exception? failure = null;

            var thread = new Thread(() =>
            {
                try { result = body(); }
                catch (Exception e) { failure = e; }
            });
            // STA is a COM concept, and COM exists only on Windows: SetApartmentState throws
            // PlatformNotSupportedException("COM Interop is not supported on this platform")
            // everywhere else, which failed every test in this suite before it ran a line of the
            // code under test. WPF itself does not need an STA off Windows -- the Dispatcher's
            // thread affinity is its own, not the apartment's -- so the thread is simply left
            // in the default apartment there.
            if (OperatingSystem.IsWindows())
            {
                thread.SetApartmentState(ApartmentState.STA);
            }

            thread.IsBackground = true;
            thread.Start();
            thread.Join();

            if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
            return result;
        }
    }
}
