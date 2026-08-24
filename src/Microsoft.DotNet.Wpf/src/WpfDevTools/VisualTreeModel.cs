// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// Stable integer identity for live visual-tree nodes, and the tree walk itself.
//
// Every CDP domain addresses nodes by an integer that has to stay valid across
// commands but must not keep a dead UI alive, so identity is a
// ConditionalWeakTable in one direction and a table of weak references in the
// other. Ids are never reused: a stale id from a frontend that has not noticed a
// tree change resolves to null rather than to some unrelated element.
//
// Everything here touches WPF objects and therefore runs on the dispatcher
// thread. The transport marshals; this file assumes it already happened.
//

using System;
using System.Collections;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Media3D;

namespace Microsoft.Wpf.DevTools
{
    internal sealed class VisualTreeModel
    {
        /// <summary>CDP reserves 0 for "no node"; the synthetic document is 1.</summary>
        internal const int DocumentNodeId = 1;

        /// <summary>
        /// The synthetic element under the document that holds one child per
        /// PresentationSource. The Elements panel wants a single document element,
        /// and a WPF process routinely has several roots at once (a Window, an open
        /// Popup, a tooltip), so they hang off this rather than fighting over the slot.
        /// </summary>
        internal const int ApplicationNodeId = 2;

        private sealed class IdBox { internal int Id; }

        private readonly ConditionalWeakTable<object, IdBox> _ids = new ConditionalWeakTable<object, IdBox>();
        private readonly Dictionary<int, WeakReference<object>> _byId = new Dictionary<int, WeakReference<object>>();
        private int _nextId = ApplicationNodeId + 1;

        /// <summary>Id for a node, allocating one the first time it is seen.</summary>
        internal int IdOf(object? d)
        {
            if (d == null)
                return 0;

            if (_ids.TryGetValue(d, out IdBox? box))
                return box.Id;

            box = new IdBox { Id = _nextId++ };
            _ids.Add(d, box);
            _byId[box.Id] = new WeakReference<object>(d);
            return box.Id;
        }

        /// <summary>
        /// An id for something that is not a DependencyObject -- today only the #text
        /// nodes the DOM domain synthesises. Drawn from the same sequence as real nodes
        /// so the two can never collide.
        /// </summary>
        internal int AllocateSyntheticId() => _nextId++;

        /// <summary>Resolve an id handed back by the frontend. Null for stale or unknown ids.</summary>
        internal object? Resolve(int id)
        {
            if (!_byId.TryGetValue(id, out WeakReference<object>? weak))
                return null;

            if (weak.TryGetTarget(out object? d))
                return d;

            // Collected. Drop the entry so the table does not grow without bound
            // across a long session with churning popups/items.
            _byId.Remove(id);
            return null;
        }

        /// <summary>Drop entries whose target has been collected.</summary>
        internal void Prune()
        {
            List<int>? dead = null;
            foreach (KeyValuePair<int, WeakReference<object>> kv in _byId)
            {
                if (!kv.Value.TryGetTarget(out _))
                    (dead ??= new List<int>()).Add(kv.Key);
            }

            if (dead != null)
            {
                foreach (int id in dead)
                    _byId.Remove(id);
            }
        }

        // ------------------------------------------------------------------
        // Roots
        // ------------------------------------------------------------------

        /// <summary>
        /// The root visual of every PresentationSource on the calling (dispatcher)
        /// thread. This is how a window, a Popup and a tooltip each show up as a
        /// separate top-level node -- they are separate sources, not one tree.
        /// </summary>
        internal static List<object> Roots()
        {
            var roots = new List<object>();

            foreach (object o in (IEnumerable)PresentationSource.CurrentSources)
            {
                if (o is PresentationSource source && !source.IsDisposed && source.RootVisual is Visual v)
                    roots.Add(v);
            }

            // ...and every top-level WinForms Form, when this process has any. A WPF
            // island inside a WinForms app already appears above -- ElementHost gives it
            // a real HwndSource -- so a mixed app shows both trees, each rooted where it
            // actually is rather than one pretending to contain the other.
            if (WinFormsTree.Available)
                roots.AddRange(WinFormsTree.Roots());

            return roots;
        }

        /// <summary>The WPF root visuals only, for the paths that are WPF-specific (screencast, hit testing).</summary>
        internal static List<Visual> VisualRoots()
        {
            var roots = new List<Visual>();
            foreach (object root in Roots())
            {
                if (root is Visual visual)
                    roots.Add(visual);
            }
            return roots;
        }

        /// <summary>
        /// The WPF visual this node IS, or the one it was decoded from. A composition node
        /// has no box of its own -- it carries transforms and clips -- so everything spatial
        /// about it (bounds, highlight, box model) goes through the element it came from,
        /// found by the DUCE handle the two trees share.
        /// </summary>
        internal static Visual? AsVisual(object node)
        {
            if (node is Visual visual)
                return visual;

            if (CompositionModel.RawHandleOf(node) is uint handle &&
                CompositionCorrelator.TryFindElement(handle, out Visual? element))
            {
                return element;
            }

            return null;
        }

        /// <summary>The node's parent in whichever tree it belongs to, or null at a root.</summary>
        internal static object? ParentOf(object node)
        {
            try
            {
                // The decoded graph holds no back-pointers, so a composition node has no
                // ancestry to walk. Nothing needs one: that path exists for the element
                // picker, which only ever runs against the UI tree.
                if (CompositionModel.Owns(node))
                    return CompositionModel.ParentOf(node);

                if (node is DependencyObject d)
                    return VisualTreeHelper.GetParent(d);

                return WinFormsTree.IsControl(node) ? WinFormsTree.ParentOf(node) : null;
            }
            catch
            {
                return null;
            }
        }

        internal static Visual RootOf(Visual v)
        {
            Visual current = v;
            while (true)
            {
                DependencyObject? parent = VisualTreeHelper.GetParent(current);
                if (parent is Visual p)
                    current = p;
                else
                    return current;
            }
        }

        // ------------------------------------------------------------------
        // Walk
        // ------------------------------------------------------------------

        /// <summary>
        /// Visual children, minus the inspector's own adorners. The highlight
        /// adorner is a Visual in the tree it is highlighting, so without this
        /// filter turning on the overlay would change the tree being inspected.
        /// </summary>
        internal static List<object> Children(object d)
        {
            if (CompositionModel.Owns(d))
                return CompositionModel.Children(d);

            if (d is not DependencyObject dependencyObject)
                return WinFormsTree.IsControl(d) ? WinFormsTree.Children(d) : new List<object>();

            return VisualChildren(dependencyObject);
        }

        /// <summary>The roots of whichever document this target is showing.</summary>
        internal static List<object> RootsFor(TargetKind kind)
            => kind == TargetKind.Composition ? CompositionModel.Roots() : Roots();

        private static List<object> VisualChildren(DependencyObject d)
        {
            var children = new List<object>();
            int count;
            try
            {
                count = VisualTreeHelper.GetChildrenCount(d);
            }
            catch
            {
                // GetChildrenCount throws for a Visual3D parent in some states, and
                // for a node mid-teardown. An unreadable subtree is reported as a
                // leaf rather than taking down the session.
                return children;
            }

            for (int i = 0; i < count; i++)
            {
                DependencyObject? child;
                try
                {
                    child = VisualTreeHelper.GetChild(d, i);
                }
                catch
                {
                    continue;
                }

                if (child != null && !IsInspectorOwned(child))
                    children.Add(child);
            }

            return children;
        }

        internal static bool IsInspectorOwned(object d)
        {
            string? ns = d.GetType().Namespace;
            return ns != null && ns.StartsWith("Microsoft.Wpf.DevTools", StringComparison.Ordinal);
        }

        // ------------------------------------------------------------------
        // Node shape
        // ------------------------------------------------------------------

        /// <summary>The CLR type name, which is what the Elements panel shows as the tag.</summary>
        internal static string NodeName(object d) => d.GetType().Name;

        /// <summary>
        /// The text a node carries directly, if any -- surfaced as a #text child so
        /// the Elements panel reads like markup instead of like a type list.
        /// Deliberately narrow: only properties that are literally the node's own
        /// text, never a ToString() of arbitrary content.
        /// </summary>
        internal static string? NodeText(object node)
        {
            if (CompositionModel.Owns(node))
                return null;

            if (node is not DependencyObject d)
                return WinFormsTree.IsControl(node) ? WinFormsTree.NodeText(node) : null;

            try
            {
                switch (d)
                {
                    case TextBlock tb:
                        return Nullify(tb.Text);
                    case Run run:
                        return Nullify(run.Text);
                    case TextBox box:
                        return Nullify(box.Text);
                    case ContentControl cc when cc.Content is string s:
                        return Nullify(s);
                    default:
                        return null;
                }
            }
            catch
            {
                return null;
            }

            static string? Nullify(string? s) => string.IsNullOrEmpty(s) ? null : s;
        }

        /// <summary>
        /// The element's x:Name / Name, which is the single most useful thing to see
        /// on a node and the thing a search is most likely to be for.
        /// </summary>
        internal static string? NameOf(object node)
        {
            // A scene visual has no name; its DUCE handle is the identity that matters,
            // and it is what matches the node to the WPF element it was decoded from.
            if (CompositionModel.Owns(node))
                return CompositionModel.HandleOf(node);

            if (node is not DependencyObject d)
                return WinFormsTree.IsControl(node) ? WinFormsTree.NameOf(node) : null;

            try
            {
                if (d is FrameworkElement fe)
                    return string.IsNullOrEmpty(fe.Name) ? null : fe.Name;
                if (d is FrameworkContentElement fce)
                    return string.IsNullOrEmpty(fce.Name) ? null : fce.Name;
            }
            catch
            {
            }
            return null;
        }

        // ------------------------------------------------------------------
        // Geometry
        // ------------------------------------------------------------------

        /// <summary>
        /// The node's bounds in the coordinate space of its root visual -- CDP's
        /// "page" space. False when the node has no meaningful box (a Visual3D, a
        /// node not yet laid out, or one detached mid-walk).
        /// </summary>
        internal static bool TryGetBounds(object node, out Rect bounds)
        {
            bounds = Rect.Empty;

            // A scene node reports the bounds of the element it was decoded from, which is
            // what makes highlighting and the box model work on the composition document.
            if (CompositionModel.Owns(node))
                return AsVisual(node) is Visual decoded && TryGetBounds(decoded, out bounds);

            if (node is not Visual v)
                return WinFormsTree.IsControl(node) && WinFormsTree.TryGetBounds(node, out bounds);

            DependencyObject d = v;

            try
            {
                Rect local = d is UIElement uie
                    ? new Rect(uie.RenderSize)
                    : VisualTreeHelper.GetDescendantBounds(v);

                if (local.IsEmpty)
                    return false;

                Visual root = RootOf(v);
                if (ReferenceEquals(root, v))
                {
                    bounds = local;
                    return true;
                }

                GeneralTransform transform = v.TransformToAncestor(root);
                bounds = transform.TransformBounds(local);
                return !bounds.IsEmpty;
            }
            catch
            {
                // TransformToAncestor throws when the two visuals stopped being
                // related between the walk and the transform, which happens
                // routinely while an app is animating items in and out.
                return false;
            }
        }

        /// <summary>
        /// The topmost application node at a point in the root's coordinate space, or null.
        /// Walks out of anything the inspector owns, so the highlight adorner can never be
        /// the thing you pick.
        /// </summary>
        internal static object? HitTest(Point point)
        {
            foreach (Visual root in VisualRoots())
            {
                try
                {
                    HitTestResult result = VisualTreeHelper.HitTest(root, point);
                    DependencyObject? hit = result?.VisualHit;

                    while (hit != null && IsInspectorOwned(hit))
                        hit = VisualTreeHelper.GetParent(hit);

                    if (hit != null)
                        return hit;
                }
                catch
                {
                    // A root mid-teardown; try the next one.
                }
            }

            return null;
        }

        /// <summary>Margin thickness, for the outer box of the CDP box model.</summary>
        internal static Thickness MarginOf(object d)
        {
            try
            {
                return d is FrameworkElement fe ? fe.Margin : default;
            }
            catch
            {
                return default;
            }
        }

        /// <summary>Border thickness, between the CDP border and padding boxes.</summary>
        internal static Thickness BorderOf(object d)
        {
            try
            {
                return d switch
                {
                    Border b => b.BorderThickness,
                    Control c => c.BorderThickness,
                    _ => default,
                };
            }
            catch
            {
                return default;
            }
        }

        /// <summary>Padding, between the CDP padding and content boxes.</summary>
        internal static Thickness PaddingOf(object d)
        {
            try
            {
                return d switch
                {
                    Border b => b.Padding,
                    Control c => c.Padding,
                    TextBlock t => t.Padding,
                    _ => default,
                };
            }
            catch
            {
                return default;
            }
        }
    }
}
