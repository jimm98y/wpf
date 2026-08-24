// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// The compositor's own tree, as a second inspectable document.
//
// WPF's Visual tree is what the app wrote. MediaContext turns it into a MILCMD
// byte stream, MilcoreEngine decodes that back into a SceneVisual graph, and
// THAT graph is what gets drawn. The two are addressed by the same DUCE handle,
// which is what lets a node here be matched to the element it came from.
//
// Everything is reached by REFLECTION, for the same reason CompositionCorrelator
// is: PresentationCore loads the renderer reflectively so that an app shipping
// no renderer, or running with WPF_USE_WEBGPU_COMPOSITION=0, still works. A
// compile-time reference here would undo that. It also sidesteps the fact that
// SceneVisual and DrawingPrimitive are internal to the renderer -- their members
// are public, which is all reflection needs.
//
// Properties are enumerated GENERICALLY rather than per type. A GlyphRunDraw and
// a GeometryStroke have nothing in common but their base class, and the renderer
// grows new primitive kinds; reflecting whatever public members a node actually
// has means this keeps working without being taught about each one.
//

using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace Microsoft.Wpf.DevTools
{
    internal static class CompositionModel
    {
        private const string SinkTypeName =
            "Microsoft.Wpf.Interop.WebGpu.Composition.Protocol.WpfCompositionSink, Microsoft.Wpf.Interop.WebGpu";

        /// <summary>Shown as the node's children instead of as properties.</summary>
        private static readonly HashSet<string> ChildCollections = new HashSet<string>(StringComparer.Ordinal)
        {
            "Children", "Content",
        };

        private static bool s_probed;
        private static PropertyInfo? s_current;
        private static PropertyInfo? s_engine;
        private static PropertyInfo? s_root;
        private static PropertyInfo? s_targets;
        private static PropertyInfo? s_visuals;

        // Object -> DUCE handle, rebuilt from the engine's handle table. Conditional so a
        // graph that churns cannot pin decoded visuals alive through the inspector.
        private static readonly ConditionalWeakTable<object, HandleBox> s_handles =
            new ConditionalWeakTable<object, HandleBox>();
        private static int s_handleGeneration = -1;

        // Child -> parent, built by walking the roots. The decoded graph holds no back
        // pointers, and the element picker needs ancestry to make a node selectable.
        private static readonly ConditionalWeakTable<object, object> s_parents =
            new ConditionalWeakTable<object, object>();
        private static int s_parentGeneration = -1;

        private sealed class HandleBox { internal uint Value; }
        private static MethodInfo? s_visualByHandle;
        private static MethodInfo? s_dumpOps;
        private static MethodInfo? s_dumpState;
        private static Type? s_sceneVisualType;
        private static Type? s_primitiveType;
        private static PropertyInfo? s_sceneChildren;
        private static PropertyInfo? s_sceneContent;
        private static PropertyInfo? s_sceneId;

        /// <summary>True when a renderer is loaded and its decoded graph can be read.</summary>
        internal static bool Available
        {
            get
            {
                Probe();
                return s_sceneVisualType != null && Engine() != null;
            }
        }

        internal static bool IsSceneVisual(object node)
        {
            Probe();
            return s_sceneVisualType != null && s_sceneVisualType.IsInstanceOfType(node);
        }

        internal static bool IsPrimitive(object node)
        {
            Probe();
            return s_primitiveType != null && s_primitiveType.IsInstanceOfType(node);
        }

        /// <summary>Whether this node belongs to the composition tree rather than a UI tree.</summary>
        internal static bool Owns(object node) => IsSceneVisual(node) || IsPrimitive(node);

        /// <summary>
        /// One root per composition target -- a window, a popup, or a RenderTargetBitmap.
        /// Falls back to the engine's single root when the target table is empty, which is
        /// what a headless or bitmap-only run looks like.
        /// </summary>
        internal static List<object> Roots()
        {
            var roots = new List<object>();
            object? engine = Engine();
            if (engine == null)
                return roots;

            try
            {
                if (s_targets?.GetValue(engine) is IEnumerable targets && s_visualByHandle != null)
                {
                    foreach (object? entry in targets)
                    {
                        if (entry == null)
                            continue;

                        // KeyValuePair<uint, MilTarget>; the target carries the root handle.
                        object? target = entry.GetType().GetProperty("Value")?.GetValue(entry);
                        object? handle = target?.GetType().GetField("RootHandle")?.GetValue(target);
                        if (handle is not uint rootHandle || rootHandle == 0)
                            continue;

                        if (s_visualByHandle.Invoke(engine, new object[] { rootHandle }) is object visual &&
                            !roots.Contains(visual))
                        {
                            roots.Add(visual);
                        }
                    }
                }

                if (roots.Count == 0 && s_root?.GetValue(engine) is object only)
                    roots.Add(only);
            }
            catch
            {
                // The engine mutates on the render thread; a partial answer beats none.
            }

            return roots;
        }

        /// <summary>
        /// Child visuals and the node's own drawing primitives, in that order. Showing
        /// primitives as children is the point: "what does the compositor actually draw for
        /// this element" is the question, and a count in a property pane does not answer it.
        /// </summary>
        internal static List<object> Children(object node)
        {
            var children = new List<object>();

            if (!IsSceneVisual(node))
                return children;

            Add(s_sceneChildren);
            Add(s_sceneContent);
            return children;

            void Add(PropertyInfo? property)
            {
                try
                {
                    if (property?.GetValue(node) is IEnumerable items)
                    {
                        foreach (object? item in items)
                        {
                            if (item != null)
                                children.Add(item);
                        }
                    }
                }
                catch
                {
                }
            }
        }

        internal static string NodeName(object node) => node.GetType().Name;

        /// <summary>
        /// A number that moves whenever the decoded graph changes: the size of the engine's
        /// handle table. Cheap to read and good enough to invalidate caches built over the
        /// graph -- exact change tracking would mean diffing it, which costs more than the
        /// work being avoided.
        /// </summary>
        internal static int GraphGeneration
        {
            get
            {
                RefreshHandles();
                return s_handleGeneration;
            }
        }

        /// <summary>The DUCE handle as a number, or null when this node has none.</summary>
        internal static uint? RawHandleOf(object node)
        {
            if (!IsSceneVisual(node))
                return null;

            RefreshHandles();
            return s_handles.TryGetValue(node, out HandleBox? box) ? box.Value : null;
        }

        /// <summary>The scene node published under a handle, or null.</summary>
        internal static object? FindByHandle(uint handle)
        {
            object? engine = Engine();
            if (engine == null || s_visualByHandle == null || handle == 0)
                return null;

            try
            {
                return s_visualByHandle.Invoke(engine, new object[] { handle });
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// This node's parent in the decoded graph. The graph has no back pointers, so the
        /// relation is recovered by walking the roots and remembered until the graph changes.
        /// Needed by the element picker: a frontend can only select a node whose ancestry it
        /// has been given.
        /// </summary>
        internal static object? ParentOf(object node)
        {
            if (!Owns(node))
                return null;

            RefreshParents();
            return s_parents.TryGetValue(node, out object? parent) ? parent : null;
        }

        private static void RefreshParents()
        {
            RefreshHandles();
            if (s_parentGeneration == s_handleGeneration)
                return;

            s_parentGeneration = s_handleGeneration;

            try
            {
                foreach (object root in Roots())
                    Walk(root, 0);
            }
            catch
            {
                // Mutating under the walk; a partial map still helps.
            }

            static void Walk(object node, int depth)
            {
                // The graph is a tree, but it is decoded from a byte stream and a malformed
                // batch could make it not one. Bound the descent rather than trust it.
                if (depth > 256)
                    return;

                foreach (object child in Children(node))
                {
                    s_parents.AddOrUpdate(child, node);
                    Walk(child, depth + 1);
                }
            }
        }

        /// <summary>
        /// The DUCE handle this visual was published under -- the join back to the WPF
        /// element that produced it.
        ///
        /// Taken from the engine's handle table, NOT from SceneVisual.Id. Id is the hit-test
        /// id: it is set only for visuals that take part in hit testing and is zero almost
        /// everywhere, which made every node in this tree anonymous.
        /// </summary>
        internal static string? HandleOf(object node)
        {
            if (!IsSceneVisual(node))
                return null;

            RefreshHandles();

            return s_handles.TryGetValue(node, out HandleBox? box)
                ? "0x" + box.Value.ToString("X", CultureInfo.InvariantCulture)
                : null;
        }

        /// <summary>
        /// Rebuild the object-to-handle map when the graph has changed. Keyed off the visual
        /// count, which is cheap and moves whenever the table does; an exact answer would mean
        /// rebuilding on every node of every walk.
        /// </summary>
        private static void RefreshHandles()
        {
            object? engine = Engine();
            if (engine == null || s_visuals == null)
                return;

            try
            {
                if (s_visuals.GetValue(engine) is not IEnumerable visuals)
                    return;

                int generation = (visuals as ICollection)?.Count ?? -1;
                if (generation == s_handleGeneration)
                    return;

                s_handleGeneration = generation;

                foreach (object? entry in visuals)
                {
                    if (entry == null)
                        continue;

                    Type type = entry.GetType();
                    if (type.GetProperty("Key")?.GetValue(entry) is not uint handle)
                        continue;

                    if (type.GetProperty("Value")?.GetValue(entry) is not object visual)
                        continue;

                    s_handles.AddOrUpdate(visual, new HandleBox { Value = handle });
                }
            }
            catch
            {
                // The engine mutates on the render thread; a stale map beats no map.
            }
        }

        /// <summary>
        /// Every public member the node actually has. Generic on purpose -- see the file
        /// header. Child collections are excluded because they are shown as child nodes.
        /// </summary>
        [UnconditionalSuppressMessage("Trimming", "IL2070",
            Justification = "Diagnostics-only, and the renderer this reflects into is loaded " +
                            "reflectively as well; when it is absent the whole target disappears.")]
        internal static List<PropertyEntry> Properties(object node)
        {
            var entries = new List<PropertyEntry>();
            Type type = node.GetType();

            try
            {
                foreach (PropertyInfo property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
                {
                    if (property.GetIndexParameters().Length > 0 || ChildCollections.Contains(property.Name))
                        continue;

                    Read(property.Name, () => property.GetValue(node));
                }

                foreach (FieldInfo field in type.GetFields(BindingFlags.Public | BindingFlags.Instance))
                {
                    if (ChildCollections.Contains(field.Name))
                        continue;

                    Read(field.Name, () => field.GetValue(node));
                }
            }
            catch
            {
            }

            entries.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
            return entries;

            void Read(string name, Func<object?> get)
            {
                object? value;
                try
                {
                    value = get();
                }
                catch
                {
                    return;
                }

                string formatted = PropertyReader.FormatValue(value);

                // "Clip=null Effect=null GuidelinesX=null" on every node buries the two or
                // three members that were actually set. An unset value is not information here.
                bool unset = formatted is "null" or "False" or "";
                entries.Add(new PropertyEntry(
                    name: name,
                    ownerType: type.Name,
                    value: formatted,
                    source: unset ? "Default" : "Local",
                    modifiers: null,
                    isLocal: !unset));
            }
        }

        /// <summary>
        /// The MILCMD op histogram: every command, record and resource type the decoder has
        /// seen, with counts. This is the closest thing to a view of the command stream
        /// itself, and it is what says whether an element is missing because WPF never sent
        /// the command or because the decoder ignored it.
        /// </summary>
        internal static string OpHistogram() => Invoke(s_dumpOps);

        /// <summary>Resource-table counts: visuals, geometries, brushes, pens, transforms.</summary>
        internal static string State() => Invoke(s_dumpState);

        private static string Invoke(MethodInfo? method)
        {
            object? engine = Engine();
            if (engine == null || method == null)
                return string.Empty;

            try
            {
                return method.Invoke(engine, null) as string ?? string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }

        private static object? Engine()
        {
            Probe();

            try
            {
                object? sink = s_current?.GetValue(null);
                return sink == null ? null : s_engine?.GetValue(sink);
            }
            catch
            {
                return null;
            }
        }

        [UnconditionalSuppressMessage("Trimming", "IL2057",
            Justification = "Diagnostics-only. Without the renderer this stays unavailable and the " +
                            "composition target is simply not advertised.")]
        private static void Probe()
        {
            if (s_probed)
                return;

            s_probed = true;

            try
            {
                // Type.GetType rather than Assembly.Load: no renderer in the process means no
                // decoded graph to look at, so loading one would be a side effect with nothing
                // to show for it.
                Type? sinkType = Type.GetType(SinkTypeName, throwOnError: false);
                if (sinkType == null)
                    return;

                s_current = sinkType.GetProperty("Current", BindingFlags.Public | BindingFlags.Static);
                s_engine = sinkType.GetProperty("Engine", BindingFlags.Public | BindingFlags.Instance);
                if (s_current == null || s_engine == null)
                    return;

                Type engineType = s_engine.PropertyType;
                const BindingFlags Instance = BindingFlags.Public | BindingFlags.Instance;

                s_root = engineType.GetProperty("Root", Instance);
                s_targets = engineType.GetProperty("Targets", Instance);
                s_visuals = engineType.GetProperty("Visuals", Instance);
                s_dumpOps = engineType.GetMethod("DumpOps", Instance, null, Type.EmptyTypes, null);
                s_dumpState = engineType.GetMethod("DumpState", Instance, null, Type.EmptyTypes, null);
                s_visualByHandle = engineType.GetMethod("VisualByHandle", Instance, null, new[] { typeof(uint) }, null);

                s_sceneVisualType = s_visualByHandle?.ReturnType ?? s_root?.PropertyType;
                if (s_sceneVisualType == null)
                    return;

                s_sceneChildren = s_sceneVisualType.GetProperty("Children", Instance);
                s_sceneContent = s_sceneVisualType.GetProperty("Content", Instance);
                s_sceneId = s_sceneVisualType.GetProperty("Id", Instance);

                // The primitive base type, taken from the element type of Content rather than
                // by name, so a rename in the renderer cannot silently disable this.
                Type? contentType = s_sceneContent?.PropertyType;
                if (contentType is { IsGenericType: true })
                    s_primitiveType = contentType.GetGenericArguments()[0];
            }
            catch
            {
                s_sceneVisualType = null;
            }
        }
    }
}
