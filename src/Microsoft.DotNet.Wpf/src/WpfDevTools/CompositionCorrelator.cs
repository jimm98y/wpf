// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// The other tree: what the compositor actually received for this element.
//
// This port has two trees and only one of them is the one apps talk about. WPF
// builds a managed Visual tree; MediaContext turns it into a MILCMD byte stream;
// MilcoreEngine decodes that back into a SceneVisual graph, and THAT is what
// gets drawn. When an element is on screen but invisible, the question that
// actually splits the search space is which of the two trees is wrong -- and
// until now answering it meant reading WPF_WEBGPU_SINK_LOG by eye.
//
// Both trees address a visual by the same DUCE resource handle, so the join is
// exact rather than heuristic.
//
// The renderer is reached by REFLECTION on purpose. PresentationCore loads it
// reflectively too (DUCE.ManagedComposition), precisely so that an app which
// ships no renderer, or runs with WPF_USE_WEBGPU_COMPOSITION=0, still works.
// A compile-time reference here would quietly undo that: the inspector would
// fail to load on exactly the configurations where you would most want it.
//

using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Windows;
using System.Windows.Diagnostics;
using System.Windows.Media;

namespace Microsoft.Wpf.DevTools
{
    /// <summary>What the compositor holds for one element.</summary>
    internal readonly struct CompositionInfo
    {
        internal CompositionInfo(uint handle, bool found, int children, int primitives, double opacity)
        {
            Handle = handle;
            Found = found;
            Children = children;
            Primitives = primitives;
            Opacity = opacity;
        }

        /// <summary>The DUCE resource handle, or 0 when the visual is not on a channel.</summary>
        internal uint Handle { get; }

        /// <summary>Whether the renderer's decoded graph actually has a node under that handle.</summary>
        internal bool Found { get; }

        internal int Children { get; }

        /// <summary>Drawing primitives recorded on the node -- 0 means it draws nothing itself.</summary>
        internal int Primitives { get; }

        internal double Opacity { get; }

        /// <summary>
        /// A one-line verdict, phrased as the answer to the question that was asked:
        /// did this element reach the compositor, and did it bring anything to draw?
        /// </summary>
        internal string Summary()
        {
            if (Handle == 0)
                return "not published to the compositor (no DUCE handle on this channel)";

            string handle = "handle 0x" + Handle.ToString("X", CultureInfo.InvariantCulture);

            if (!Found)
                return handle + " -- published, but the renderer's scene graph has no node for it";

            return string.Format(
                CultureInfo.InvariantCulture,
                "{0}, {1} child visual(s), {2} drawing primitive(s), opacity {3:0.###}",
                handle, Children, Primitives, Opacity);
        }
    }

    internal static class CompositionCorrelator
    {
        private const string SinkTypeName =
            "Microsoft.Wpf.Interop.WebGpu.Composition.Protocol.WpfCompositionSink, Microsoft.Wpf.Interop.WebGpu";

        private static bool s_probed;
        private static PropertyInfo? s_current;
        private static PropertyInfo? s_engine;
        private static MethodInfo? s_visualByHandle;
        private static PropertyInfo? s_children;
        private static PropertyInfo? s_content;
        private static PropertyInfo? s_opacity;

        /// <summary>True when a renderer is loaded and its scene graph can be read.</summary>
        internal static bool Available
        {
            get
            {
                Probe();
                return s_visualByHandle != null;
            }
        }

        /// <summary>
        /// Correlate a visual against the compositor's graph. Returns false only when
        /// there is no renderer to ask; a visual the renderer has never heard of is a
        /// successful answer with Found == false, because that IS the finding.
        /// </summary>
        internal static bool TryDescribe(Visual visual, out CompositionInfo info)
        {
            info = default;
            Probe();

            if (s_visualByHandle == null)
                return false;

            uint handle;
            try
            {
                handle = CompositionDiagnostics.GetCompositionHandle(visual);
            }
            catch
            {
                return false;
            }

            if (handle == 0)
            {
                info = new CompositionInfo(0, found: false, 0, 0, 0);
                return true;
            }

            try
            {
                object? sink = s_current!.GetValue(null);
                object? engine = sink == null ? null : s_engine!.GetValue(sink);
                object? node = engine == null ? null : s_visualByHandle.Invoke(engine, new object[] { handle });

                if (node == null)
                {
                    info = new CompositionInfo(handle, found: false, 0, 0, 0);
                    return true;
                }

                info = new CompositionInfo(
                    handle,
                    found: true,
                    children: Count(s_children?.GetValue(node)),
                    primitives: Count(s_content?.GetValue(node)),
                    opacity: s_opacity?.GetValue(node) is double d ? d : 1.0);
                return true;
            }
            catch
            {
                return false;
            }
        }

        // handle -> element, rebuilt when the decoded graph changes. Weak values: a
        // diagnostic must not be the reason a torn-down window stays alive.
        private static readonly Dictionary<uint, WeakReference<Visual>> s_elements =
            new Dictionary<uint, WeakReference<Visual>>();
        private static int s_elementsGeneration = -1;
        private static int s_missGeneration = -1;

        /// <summary>
        /// The WPF element published under a handle -- the reverse of
        /// CompositionDiagnostics.GetCompositionHandle.
        ///
        /// There is no reverse lookup to ask, so the map is built by walking the visual tree
        /// and reading each element's handle. That walk is CACHED, because the callers make it
        /// quadratic otherwise: every node of the composition document asks for its element
        /// while being written, and the highlight asks again on every hover.
        /// </summary>
        internal static bool TryFindElement(uint handle, out Visual? element)
        {
            element = null;
            if (handle == 0)
                return false;

            int generation = CompositionModel.GraphGeneration;
            if (generation != s_elementsGeneration)
                Rebuild(generation);

            if (TryLookup(handle, out element))
                return true;

            // A miss is not proof the handle is unknown: the generation is the handle-table
            // size, so a visual republished under a new handle without changing the count
            // leaves a stale map. Rebuild once more and retry -- but only once per generation,
            // or a genuinely unknown handle would walk the tree on every call.
            if (s_missGeneration == generation)
                return false;

            s_missGeneration = generation;
            Rebuild(generation);
            return TryLookup(handle, out element);
        }

        private static bool TryLookup(uint handle, out Visual? element)
        {
            element = null;

            if (!s_elements.TryGetValue(handle, out WeakReference<Visual>? weak))
                return false;

            if (weak.TryGetTarget(out Visual? target))
            {
                element = target;
                return true;
            }

            s_elements.Remove(handle);
            return false;
        }

        private static void Rebuild(int generation)
        {
            s_elements.Clear();
            s_elementsGeneration = generation;

            foreach (Visual root in VisualTreeModel.VisualRoots())
                Index(root);

            static void Index(DependencyObject node)
            {
                if (node is Visual visual)
                {
                    uint handle;
                    try
                    {
                        handle = CompositionDiagnostics.GetCompositionHandle(visual);
                    }
                    catch
                    {
                        handle = 0;
                    }

                    if (handle != 0)
                        s_elements[handle] = new WeakReference<Visual>(visual);
                }

                foreach (object child in VisualTreeModel.Children(node))
                {
                    if (child is DependencyObject d)
                        Index(d);
                }
            }
        }

        private static int Count(object? value)
            => value is ICollection collection ? collection.Count : 0;

        private static void Probe()
        {
            if (s_probed)
                return;

            s_probed = true;

            try
            {
                // Type.GetType, not Assembly.Load: if the renderer is not already loaded
                // then managed composition is not running and there is nothing to correlate
                // against, so loading it here would be both pointless and a side effect.
                Type? sinkType = Type.GetType(SinkTypeName, throwOnError: false);
                if (sinkType == null)
                    return;

                s_current = sinkType.GetProperty("Current", BindingFlags.Public | BindingFlags.Static);
                s_engine = sinkType.GetProperty("Engine", BindingFlags.Public | BindingFlags.Instance);
                if (s_current == null || s_engine == null)
                    return;

                Type? engineType = s_engine.PropertyType;
                s_visualByHandle = engineType.GetMethod("VisualByHandle", BindingFlags.Public | BindingFlags.Instance,
                                                        binder: null, types: new[] { typeof(uint) }, modifiers: null);
                if (s_visualByHandle == null)
                    return;

                // SceneVisual is internal to the renderer, so its members are read off the
                // returned instance rather than through a typed reference.
                Type? sceneVisual = s_visualByHandle.ReturnType;
                s_children = sceneVisual.GetProperty("Children", BindingFlags.Public | BindingFlags.Instance);
                s_content = sceneVisual.GetProperty("Content", BindingFlags.Public | BindingFlags.Instance);
                s_opacity = sceneVisual.GetProperty("Opacity", BindingFlags.Public | BindingFlags.Instance);
            }
            catch
            {
                s_visualByHandle = null;
            }
        }
    }
}
