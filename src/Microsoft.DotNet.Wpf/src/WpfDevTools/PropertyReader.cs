// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// Reads dependency properties off a live node, with the ORIGIN of each value.
//
// The origin is the point of this file. A flat list of current values is what
// every ad-hoc tree dumper already gives you; what you actually need when a
// control looks wrong is whether Background came from the element, its Style, a
// template trigger, or inheritance. DependencyPropertyHelper.GetValueSource
// answers that, and it maps directly onto DevTools' Styles pane, which groups
// declarations by origin and strikes through the ones that lost.
//
// Two discovery paths, because one of them is not always available:
//
//   * the full set, by reflecting over the public static DependencyProperty
//     fields on the type and its bases. Complete, and what you want on desktop.
//   * local values only, via GetLocalValueEnumerator(). Reflection-free, so it
//     survives trimming on the iOS and WASM heads, and it is also the only way
//     to see an ATTACHED property, which is declared on some other type
//     (Grid.Row lives on Grid, not on the Button that carries it).
//
// The two are merged: reflection for breadth, the enumerator for attached
// properties and as the fallback when the trimmer has removed the fields.
//

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Windows;

namespace Microsoft.Wpf.DevTools
{
    internal readonly struct PropertyEntry
    {
        internal PropertyEntry(string name, string ownerType, string value, string source, string? modifiers, bool isLocal)
        {
            Name = name;
            OwnerType = ownerType;
            Value = value;
            Source = source;
            Modifiers = modifiers;
            IsLocal = isLocal;
        }

        /// <summary>Property name without the "Property" suffix, e.g. "Background".</summary>
        internal string Name { get; }

        /// <summary>Declaring type, so attached properties read as "Grid.Row".</summary>
        internal string OwnerType { get; }

        internal string Value { get; }

        /// <summary>BaseValueSource name: Local, Style, TemplateTrigger, Inherited, Default...</summary>
        internal string Source { get; }

        /// <summary>Comma-joined "animated", "coerced", "expression", "current" -- null when none apply.</summary>
        internal string? Modifiers { get; }

        internal bool IsLocal { get; }

        /// <summary>Qualified display name: "Grid.Row" for attached, "Background" otherwise.</summary>
        internal string DisplayName(string nodeTypeName)
            => OwnerType.Length == 0 || string.Equals(OwnerType, nodeTypeName, StringComparison.Ordinal)
                ? Name
                : OwnerType + "." + Name;
    }

    internal static class PropertyReader
    {
        private const int MaxValueLength = 240;

        private static readonly Dictionary<Type, DependencyProperty[]> s_byType = new Dictionary<Type, DependencyProperty[]>();

        /// <summary>
        /// Every dependency property visible on this node, with current value and
        /// origin, sorted by display name.
        /// </summary>
        internal static List<PropertyEntry> ReadAll(object node)
        {
            if (CompositionModel.Owns(node))
                return CompositionModel.Properties(node);

            if (node is not DependencyObject d)
                return WinFormsTree.IsControl(node) ? WinFormsTree.ReadProperties(node, localOnly: false) : new List<PropertyEntry>();

            var seen = new HashSet<DependencyProperty>();
            var entries = new List<PropertyEntry>();

            foreach (DependencyProperty dp in DeclaredProperties(d.GetType()))
            {
                if (seen.Add(dp) && TryRead(d, dp, out PropertyEntry entry))
                    entries.Add(entry);
            }

            // Attached properties, and everything else the reflection pass could not
            // see. Also the whole answer when the fields have been trimmed away.
            foreach (DependencyProperty dp in LocalProperties(d))
            {
                if (seen.Add(dp) && TryRead(d, dp, out PropertyEntry entry))
                    entries.Add(entry);
            }

            string typeName = d.GetType().Name;
            entries.Sort((a, b) => string.CompareOrdinal(a.DisplayName(typeName), b.DisplayName(typeName)));
            return entries;
        }

        /// <summary>Only the properties with a local value -- the "inline style" of a node.</summary>
        internal static List<PropertyEntry> ReadLocal(object node)
        {
            // For a composition node "local" means SET: the ones left at their default are
            // still in the full list, but a node line reading "Clip=null ClipGeometry=null
            // Effect=null GuidelinesX=null" buries the two members that were actually set.
            if (CompositionModel.Owns(node))
                return CompositionModel.Properties(node).FindAll(e => e.IsLocal);

            if (node is not DependencyObject d)
                return WinFormsTree.IsControl(node) ? WinFormsTree.ReadProperties(node, localOnly: true) : new List<PropertyEntry>();

            var entries = new List<PropertyEntry>();
            foreach (DependencyProperty dp in LocalProperties(d))
            {
                if (TryRead(d, dp, out PropertyEntry entry))
                    entries.Add(entry);
            }

            string typeName = d.GetType().Name;
            entries.Sort((a, b) => string.CompareOrdinal(a.DisplayName(typeName), b.DisplayName(typeName)));
            return entries;
        }

        /// <summary>
        /// Every property that could be addressed on this node: the declared set plus
        /// whatever attached properties it actually carries. Shared with PropertyWriter
        /// so an edit can only name something a read could have shown.
        /// </summary>
        internal static IEnumerable<DependencyProperty> CandidatesFor(DependencyObject d)
        {
            var seen = new HashSet<DependencyProperty>();

            foreach (DependencyProperty dp in DeclaredProperties(d.GetType()))
            {
                if (seen.Add(dp))
                    yield return dp;
            }

            foreach (DependencyProperty dp in LocalProperties(d))
            {
                if (seen.Add(dp))
                    yield return dp;
            }
        }

        private static List<DependencyProperty> LocalProperties(DependencyObject d)
        {
            var list = new List<DependencyProperty>();
            try
            {
                LocalValueEnumerator e = d.GetLocalValueEnumerator();
                while (e.MoveNext())
                    list.Add(e.Current.Property);
            }
            catch
            {
                // A node being torn down can fault mid-enumeration. Report what we got.
            }
            return list;
        }

        private static bool TryRead(DependencyObject d, DependencyProperty dp, out PropertyEntry entry)
        {
            entry = default;

            object? value;
            try
            {
                value = d.GetValue(dp);
            }
            catch
            {
                // A property whose getter faults is not worth failing the whole node over.
                return false;
            }

            string source = "Unknown";
            string? modifiers = null;
            bool isLocal = false;

            try
            {
                ValueSource vs = DependencyPropertyHelper.GetValueSource(d, dp);
                source = vs.BaseValueSource.ToString();
                isLocal = vs.BaseValueSource == BaseValueSource.Local;

                List<string>? mods = null;
                if (vs.IsExpression) (mods ??= new List<string>()).Add("expression");
                if (vs.IsAnimated) (mods ??= new List<string>()).Add("animated");
                if (vs.IsCoerced) (mods ??= new List<string>()).Add("coerced");
                if (vs.IsCurrent) (mods ??= new List<string>()).Add("current");
                modifiers = mods == null ? null : string.Join(", ", mods);
            }
            catch
            {
                // GetValueSource rejects a property that is not valid for this type.
                // The value still is, so report it with an unknown origin.
            }

            entry = new PropertyEntry(
                name: dp.Name,
                ownerType: dp.OwnerType?.Name ?? string.Empty,
                value: FormatValue(value),
                source: source,
                modifiers: modifiers,
                isLocal: isLocal);
            return true;
        }

        internal static string FormatValue(object? value)
        {
            if (value == null)
                return "null";
            if (ReferenceEquals(value, DependencyProperty.UnsetValue))
                return "(unset)";

            string text;
            try
            {
                text = value as string ?? value.ToString() ?? string.Empty;
            }
            catch
            {
                // ToString on a half-constructed value can throw; the type name is
                // still more informative than dropping the property.
                return "<" + value.GetType().Name + ">";
            }

            if (text.Length > MaxValueLength)
                text = string.Concat(text.AsSpan(0, MaxValueLength), "...");

            return text;
        }

        /// <summary>
        /// The DependencyProperty fields declared on a type and its bases, cached.
        /// Returns empty when reflection finds nothing, which is the expected
        /// outcome on a trimmed head -- callers fall back to local values.
        /// </summary>
        [UnconditionalSuppressMessage("Trimming", "IL2070",
            Justification = "Diagnostics-only breadth. The type comes from a live object so it cannot be " +
                            "statically annotated; when the trimmer has removed these fields this returns " +
                            "empty and ReadAll degrades to GetLocalValueEnumerator, which needs no reflection.")]
        private static DependencyProperty[] DeclaredProperties(Type type)
        {
            if (s_byType.TryGetValue(type, out DependencyProperty[]? cached))
                return cached;

            DependencyProperty[] result;
            try
            {
                var list = new List<DependencyProperty>();
                // FlattenHierarchy walks base types for public statics, which is
                // exactly how WPF declares dependency properties.
                FieldInfo[] fields = type.GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy);
                foreach (FieldInfo field in fields)
                {
                    if (field.FieldType != typeof(DependencyProperty))
                        continue;

                    if (field.GetValue(null) is DependencyProperty dp)
                        list.Add(dp);
                }
                result = list.ToArray();
            }
            catch
            {
                result = Array.Empty<DependencyProperty>();
            }

            s_byType[type] = result;
            return result;
        }
    }
}
