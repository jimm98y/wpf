// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// The other kind of tree: WinForms controls.
//
// Reached entirely by REFLECTION, and not out of squeamishness. This assembly is
// one AnyCPU build shared by every head, and most of those heads never deploy a
// System.Windows.Forms at all; a compile-time reference would make the inspector
// fail to load on a plain WPF app, which is the case it exists for. The same
// reasoning already applies to CompositionCorrelator and to how PresentationCore
// loads the renderer. Cost is irrelevant here -- a control tree is hundreds of
// nodes and this is a diagnostic, not a frame path.
//
// What WinForms gives, and does not:
//
//   * children      Control.Controls, ordinary and complete.
//   * bounds        Left/Top/Width/Height summed up to the owning Form. Client
//                   offsets (a Form's border and caption) are not subtracted, so
//                   these are client-area coordinates, which is the space the
//                   designer works in and the one that matches Control.Location.
//   * properties    ordinary CLR properties, read through TypeDescriptor -- which
//                   also answers the question WinForms has instead of WPF's value
//                   precedence: ShouldSerializeValue says whether a property was
//                   SET on this control or is still the default. Ambient
//                   properties (Font, BackColor, ForeColor, Cursor) fall back to
//                   the parent when unset, so those get a third origin.
//
// There is no equivalent of WPF's eleven-deep value precedence, and pretending
// otherwise would be worse than the honest three.
//

using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Windows;

namespace Microsoft.Wpf.DevTools
{
    internal static class WinFormsTree
    {
        /// <summary>
        /// Properties that inherit from the parent control when they were never set.
        /// WinForms calls these ambient; they are the closest thing it has to WPF's
        /// Inherited value source.
        /// </summary>
        private static readonly HashSet<string> AmbientProperties = new HashSet<string>(StringComparer.Ordinal)
        {
            "Font", "BackColor", "ForeColor", "Cursor", "RightToLeft",
        };

        /// <summary>
        /// Structural members that every control has, that nobody set, and that say
        /// nothing about how this one looks. Parent and Controls are the tree, which
        /// the panel is already showing; Bounds/Location/Left/Top duplicate the box
        /// model; the rest are plumbing.
        /// </summary>
        private static readonly HashSet<string> StructuralProperties = new HashSet<string>(StringComparer.Ordinal)
        {
            "Parent", "Controls", "DataBindings", "BindingContext", "Site", "Container",
            "Bounds", "Location", "Left", "Top", "ClientSize", "ClientRectangle",
            "DisplayRectangle", "Region", "Capture", "IsAccessible",
        };

        private static bool s_probed;
        private static Type? s_controlType;
        private static PropertyInfo? s_openForms;
        private static PropertyInfo? s_controls;
        private static PropertyInfo? s_parent;
        private static PropertyInfo? s_name;
        private static PropertyInfo? s_text;
        private static PropertyInfo? s_visible;
        private static PropertyInfo? s_topLevel;
        private static PropertyInfo? s_left, s_top, s_width, s_height;

        /// <summary>True when a System.Windows.Forms is loaded in this process.</summary>
        internal static bool Available
        {
            get
            {
                Probe();
                return s_controlType != null;
            }
        }

        internal static bool IsControl(object node)
        {
            Probe();
            return s_controlType != null && s_controlType.IsInstanceOfType(node);
        }

        /// <summary>Every visible top-level Form, which is WinForms' answer to PresentationSource.CurrentSources.</summary>
        internal static List<object> Roots()
        {
            var roots = new List<object>();
            Probe();

            if (s_openForms == null)
                return roots;

            try
            {
                if (s_openForms.GetValue(null) is not IEnumerable forms)
                    return roots;

                foreach (object? form in forms)
                {
                    if (form == null)
                        continue;

                    // A Form with a parent is not a top-level window; the designer
                    // parents the form under design into its own surface.
                    if (Bool(s_visible, form) && Bool(s_topLevel, form, fallback: true) &&
                        s_parent?.GetValue(form) == null)
                    {
                        roots.Add(form);
                    }
                }
            }
            catch
            {
                // A collection mutating under enumeration; report what we have.
            }

            return roots;
        }

        internal static List<object> Children(object node)
        {
            var children = new List<object>();
            Probe();

            try
            {
                if (s_controls?.GetValue(node) is IEnumerable controls)
                {
                    foreach (object? child in controls)
                    {
                        if (child != null)
                            children.Add(child);
                    }
                }
            }
            catch
            {
            }

            return children;
        }

        internal static object? ParentOf(object node)
        {
            try
            {
                return s_parent?.GetValue(node);
            }
            catch
            {
                return null;
            }
        }

        internal static string? NameOf(object node)
        {
            string? name = s_name?.GetValue(node) as string;
            return string.IsNullOrEmpty(name) ? null : name;
        }

        internal static string? NodeText(object node)
        {
            string? text = s_text?.GetValue(node) as string;
            return string.IsNullOrEmpty(text) ? null : text;
        }

        /// <summary>
        /// Bounds in the owning form's client space, by summing Left/Top up the parent
        /// chain. A root form reports at the origin: its own Location is a screen
        /// position, which is not the space its children are expressed in.
        /// </summary>
        internal static bool TryGetBounds(object node, out Rect bounds)
        {
            bounds = Rect.Empty;
            Probe();

            if (s_width == null || s_height == null)
                return false;

            try
            {
                int width = Int(s_width, node);
                int height = Int(s_height, node);
                if (width <= 0 || height <= 0)
                    return false;

                int x = 0, y = 0;
                object? current = node;
                while (current != null && s_parent?.GetValue(current) is object parent)
                {
                    x += Int(s_left, current);
                    y += Int(s_top, current);
                    current = parent;
                }

                bounds = new Rect(x, y, width, height);
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// The control's properties, with WinForms' three origins in place of WPF's
        /// value sources. Read-only and indexer properties are skipped: an indexer
        /// has no value to show and a getter with side effects is not worth the risk
        /// in a tool that reads every property of every node.
        /// </summary>
        [UnconditionalSuppressMessage("Trimming", "IL2026",
            Justification = "Diagnostics-only. If TypeDescriptor has been trimmed the property list " +
                            "comes back empty, which degrades the pane rather than failing the command.")]
        internal static List<PropertyEntry> ReadProperties(object node, bool localOnly)
        {
            var entries = new List<PropertyEntry>();
            string typeName = node.GetType().Name;

            PropertyDescriptorCollection properties;
            try
            {
                properties = TypeDescriptor.GetProperties(node);
            }
            catch
            {
                return entries;
            }

            foreach (PropertyDescriptor descriptor in properties)
            {
                if (descriptor.PropertyType == null)
                    continue;

                // [Browsable(false)] is WinForms' own statement that a property is not
                // for a human to look at, which is exactly the question being asked here.
                if (!descriptor.IsBrowsable || StructuralProperties.Contains(descriptor.Name))
                    continue;

                bool isSet;
                try
                {
                    isSet = descriptor.ShouldSerializeValue(node);
                }
                catch
                {
                    isSet = false;
                }

                if (localOnly && !isSet)
                    continue;

                object? value;
                try
                {
                    value = descriptor.GetValue(node);
                }
                catch
                {
                    // A property whose getter faults is not worth failing the node over.
                    continue;
                }

                // WinForms' three origins, in place of WPF's eleven. ShouldSerializeValue
                // is the designer's notion of "set on this control"; the ambient ones fall
                // back to the parent; everything else is the property's own default. A
                // read-only property was never set by anyone, whatever ShouldSerializeValue
                // says about it.
                string source = descriptor.IsReadOnly
                    ? "Default"
                    : isSet
                        ? "Local"
                        : AmbientProperties.Contains(descriptor.Name) && HasParent(node)
                            ? "Inherited"
                            : "Default";

                entries.Add(new PropertyEntry(
                    name: descriptor.Name,
                    ownerType: descriptor.ComponentType?.Name ?? typeName,
                    value: PropertyReader.FormatValue(value),
                    source: source,
                    modifiers: descriptor.IsReadOnly ? "read-only" : null,
                    isLocal: isSet));
            }

            entries.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
            return entries;
        }

        private static bool HasParent(object node)
        {
            try
            {
                return s_parent?.GetValue(node) != null;
            }
            catch
            {
                return false;
            }
        }

        private static bool Bool(PropertyInfo? property, object target, bool fallback = false)
        {
            try
            {
                return property?.GetValue(target) is bool b ? b : fallback;
            }
            catch
            {
                return fallback;
            }
        }

        private static int Int(PropertyInfo? property, object target)
        {
            try
            {
                return property?.GetValue(target) is int i ? i : 0;
            }
            catch
            {
                return 0;
            }
        }

        [UnconditionalSuppressMessage("Trimming", "IL2057",
            Justification = "Diagnostics-only. If System.Windows.Forms is absent or trimmed, Available " +
                            "stays false and the WinForms half of the inspector simply does not appear.")]
        private static void Probe()
        {
            if (s_probed)
                return;

            s_probed = true;

            try
            {
                // Type.GetType, not Assembly.Load: no WinForms in the process means no
                // control tree to inspect, so loading one would be a side effect with
                // nothing to show for it.
                Type? control = Type.GetType("System.Windows.Forms.Control, System.Windows.Forms", throwOnError: false);
                Type? application = Type.GetType("System.Windows.Forms.Application, System.Windows.Forms", throwOnError: false);
                if (control == null || application == null)
                    return;

                const BindingFlags Instance = BindingFlags.Public | BindingFlags.Instance;

                s_openForms = application.GetProperty("OpenForms", BindingFlags.Public | BindingFlags.Static);
                s_controls = control.GetProperty("Controls", Instance);
                s_parent = control.GetProperty("Parent", Instance);
                s_name = control.GetProperty("Name", Instance);
                s_text = control.GetProperty("Text", Instance);
                s_visible = control.GetProperty("Visible", Instance);
                s_left = control.GetProperty("Left", Instance);
                s_top = control.GetProperty("Top", Instance);
                s_width = control.GetProperty("Width", Instance);
                s_height = control.GetProperty("Height", Instance);

                Type? form = Type.GetType("System.Windows.Forms.Form, System.Windows.Forms", throwOnError: false);
                s_topLevel = form?.GetProperty("TopLevel", Instance);

                if (s_openForms == null || s_controls == null)
                    return;

                s_controlType = control;
            }
            catch
            {
                s_controlType = null;
            }
        }
    }
}
