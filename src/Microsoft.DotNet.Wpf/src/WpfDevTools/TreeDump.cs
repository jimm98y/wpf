// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// A plain-text rendering of the live visual tree.
//
// This is the smallest useful thing the inspector can do and it does not need a
// socket, a browser, or a frontend that might have changed since it was last
// tested: set WPF_DEVTOOLS_DUMP and you get a file. It is also the oracle the
// protocol tests compare against, so the tree walk is exercised independently
// of the transport that later carries it.
//

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Media;

namespace Microsoft.Wpf.DevTools
{
    /// <summary>How much per-node property detail a dump carries.</summary>
    internal enum DumpProperties
    {
        /// <summary>Structure only.</summary>
        None,
        /// <summary>Only properties with a local value.</summary>
        Local,
        /// <summary>Everything whose value did not come from the property default.</summary>
        Set,
        /// <summary>Every dependency property on the node.</summary>
        All,
    }

    internal static class TreeDump
    {
        /// <summary>Parse the WPF_DEVTOOLS_DUMP_PROPS value. Unrecognised means None.</summary>
        internal static DumpProperties ParseProperties(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return DumpProperties.None;

            switch (value.Trim().ToLowerInvariant())
            {
                case "local": return DumpProperties.Local;
                case "set": case "1": case "true": return DumpProperties.Set;
                case "all": return DumpProperties.All;
                default: return DumpProperties.None;
            }
        }

        /// <summary>
        /// Render every PresentationSource root on the calling thread. Must run on
        /// the dispatcher thread.
        /// </summary>
        internal static string Render(VisualTreeModel model, DumpProperties properties)
        {
            var sb = new StringBuilder();
            List<object> roots = VisualTreeModel.Roots();

            sb.Append("# WPF visual tree, ")
              .Append(roots.Count.ToString(CultureInfo.InvariantCulture))
              .AppendLine(roots.Count == 1 ? " root" : " roots");
            sb.AppendLine("# bounds are (x,y wxh) in the root visual's coordinate space");
            sb.AppendLine();

            if (roots.Count == 0)
                sb.AppendLine("(no PresentationSource on this thread)");

            foreach (object root in roots)
                RenderNode(sb, model, root, depth: 0, properties);

            return sb.ToString();
        }

        private static void RenderNode(StringBuilder sb, VisualTreeModel model, object node, int depth, DumpProperties properties)
        {
            sb.Append(' ', depth * 2);

            sb.Append('[').Append(model.IdOf(node).ToString(CultureInfo.InvariantCulture)).Append("] ");
            sb.Append(VisualTreeModel.NodeName(node));

            string? name = VisualTreeModel.NameOf(node);
            if (name != null)
                sb.Append(" #").Append(name);

            if (VisualTreeModel.TryGetBounds(node, out Rect bounds))
            {
                sb.Append("  (")
                  .Append(Round(bounds.X)).Append(',').Append(Round(bounds.Y))
                  .Append(' ').Append(Round(bounds.Width)).Append('x').Append(Round(bounds.Height))
                  .Append(')');
            }

            string? text = VisualTreeModel.NodeText(node);
            if (text != null)
                sb.Append("  \"").Append(Escape(text)).Append('"');

            sb.AppendLine();

            if (properties != DumpProperties.None)
            {
                if (node is Visual visual && CompositionCorrelator.TryDescribe(visual, out CompositionInfo composition))
                {
                    sb.Append(' ', (depth + 1) * 2)
                      .Append("~ composition: ").AppendLine(composition.Summary());
                }

                RenderProperties(sb, node, depth + 1, properties);
            }

            foreach (object child in VisualTreeModel.Children(node))
                RenderNode(sb, model, child, depth + 1, properties);
        }

        private static void RenderProperties(StringBuilder sb, object node, int depth, DumpProperties properties)
        {
            List<PropertyEntry> entries = properties == DumpProperties.Local
                ? PropertyReader.ReadLocal(node)
                : PropertyReader.ReadAll(node);

            string typeName = node.GetType().Name;

            foreach (PropertyEntry entry in entries)
            {
                if (properties == DumpProperties.Set &&
                    string.Equals(entry.Source, nameof(BaseValueSource.Default), StringComparison.Ordinal))
                {
                    continue;
                }

                sb.Append(' ', depth * 2)
                  .Append("- ").Append(entry.DisplayName(typeName))
                  .Append(" = ").Append(Escape(entry.Value))
                  .Append("  [").Append(entry.Source);

                if (entry.Modifiers != null)
                    sb.Append(", ").Append(entry.Modifiers);

                sb.AppendLine("]");
            }
        }

        private static string Round(double value)
            => Math.Round(value, 2).ToString(CultureInfo.InvariantCulture);

        /// <summary>Keep one node on one line -- the dump is diffed and grepped.</summary>
        private static string Escape(string s)
            => s.Replace("\\", "\\\\").Replace("\r", "\\r").Replace("\n", "\\n").Replace("\"", "\\\"");
    }
}
