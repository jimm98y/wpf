// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// CSS: dependency properties, grouped by where their value came from.
//
// This is the domain that earns the whole exercise. The Computed pane becomes a
// filterable list of every DP on the node, and the Styles pane becomes something
// the Live Visual Tree does not have: one "rule" per BaseValueSource, ordered by
// WPF's real precedence, so a Background that looks wrong shows you at a glance
// whether it came from the element, its Style, a template, a trigger, or was
// inherited -- with the losers struck through, because that is what the panel
// does with overridden declarations.
//
// The mapping is a lie in exactly one direction and it is worth being clear
// about it: CSS specificity and WPF's dependency-property precedence are not the
// same system. What makes it work is that the panel renders a LIST of rules in
// the order it is given and strikes through any property a higher rule already
// set. Handing it groups in WPF precedence order therefore renders WPF's
// answer, not CSS's.
//

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Windows;
using Microsoft.Wpf.DevTools.Json;

namespace Microsoft.Wpf.DevTools.Domains
{
    internal sealed class CssDomain : ICdpDomain
    {
        /// <summary>
        /// BaseValueSource names in WPF's precedence order, strongest first. Taken from
        /// the dependency-property value precedence list; animation and coercion are
        /// modifiers on top of these rather than sources of their own, so they are
        /// reported per-property instead.
        /// </summary>
        private static readonly string[] SourceOrder =
        {
            nameof(BaseValueSource.Local),
            nameof(BaseValueSource.ParentTemplateTrigger),
            nameof(BaseValueSource.ParentTemplate),
            nameof(BaseValueSource.StyleTrigger),
            nameof(BaseValueSource.TemplateTrigger),
            nameof(BaseValueSource.Style),
            nameof(BaseValueSource.ImplicitStyleReference),
            nameof(BaseValueSource.DefaultStyleTrigger),
            nameof(BaseValueSource.DefaultStyle),
            nameof(BaseValueSource.Inherited),
            nameof(BaseValueSource.Default),
            "Unknown",
        };

        private readonly CdpSession _session;

        internal CssDomain(CdpSession session)
        {
            _session = session;
        }

        public bool TryHandle(string method, JsonElement p, Utf8JsonWriter w)
        {
            switch (method)
            {
                case "CSS.enable":
                case "CSS.disable":
                case "CSS.trackComputedStyleUpdates":
                    return true;

                case "CSS.takeComputedStyleUpdates":
                    // The frontend polls this; it wants the array, not an empty object.
                    // Nothing is tracked here, so the answer is always none.
                    w.WriteStartArray("nodeIds");
                    w.WriteEndArray();
                    return true;

                case "CSS.getComputedStyleForNode":
                    GetComputedStyle(p, w);
                    return true;

                case "CSS.getInlineStylesForNode":
                    GetInlineStyles(p, w);
                    return true;

                case "CSS.getMatchedStylesForNode":
                    GetMatchedStyles(p, w);
                    return true;

                case "CSS.setStyleTexts":
                    SetStyleTexts(p, w);
                    return true;

                case "CSS.getStyleSheetText":
                    // There is no stylesheet document behind a dependency property, so
                    // the panel gets an empty one. Answering rather than falling through
                    // keeps it from treating the rule as broken.
                    w.WriteString("text", string.Empty);
                    return true;

                default:
                    return false;
            }
        }

        private object? Resolve(JsonElement p)
            => _session.Dom.ResolveNode(_session.Dom.NodeIdFrom(p));

        /// <summary>Every dependency property on the node, flat. The Computed pane filters it.</summary>
        private void GetComputedStyle(JsonElement p, Utf8JsonWriter w)
        {
            // The composition root is synthetic, so there is no object to read properties
            // from -- and it is the natural place for the numbers that describe the whole
            // decode rather than one node. Selecting it and reading Computed gives the MILCMD
            // op histogram: every command, record and resource type the decoder has seen,
            // with counts. That is what distinguishes "WPF never sent it" from "the decoder
            // ignored it", which no per-node view can answer.
            if (_session.Kind == TargetKind.Composition &&
                _session.Dom.NodeIdFrom(p) == VisualTreeModel.ApplicationNodeId)
            {
                WriteOpHistogram(w);
                return;
            }

            object? node = Resolve(p);

            w.WriteStartArray("computedStyle");
            if (node != null)
            {
                string typeName = node.GetType().Name;

                // First, deliberately: what the COMPOSITOR has for this element. When
                // something is laid out correctly and still not on screen, this is the
                // line that says which half of the port to go and look at.
                if (node is System.Windows.Media.Visual visual &&
                    CompositionCorrelator.TryDescribe(visual, out CompositionInfo composition))
                {
                    w.WriteStartObject();
                    w.WriteString("name", "Composition.Scene");
                    w.WriteString("value", composition.Summary());
                    w.WriteEndObject();
                }

                // ...and the reverse, on the composition document: which element this scene
                // node came from, and where that element actually is. A SceneVisual has
                // transforms and clips but no laid-out box, so without this the node cannot
                // say where on screen it belongs.
                if (CompositionModel.Owns(node) && VisualTreeModel.AsVisual(node) is System.Windows.Media.Visual element)
                {
                    w.WriteStartObject();
                    w.WriteString("name", "Element");
                    w.WriteString("value", Domains.DomDomain.DescriptionOf(element));
                    w.WriteEndObject();

                    if (VisualTreeModel.TryGetBounds(element, out System.Windows.Rect bounds))
                    {
                        w.WriteStartObject();
                        w.WriteString("name", "Element.Bounds");
                        w.WriteString("value", string.Format(
                            System.Globalization.CultureInfo.InvariantCulture,
                            "{0:0.##},{1:0.##} {2:0.##} x {3:0.##}",
                            bounds.X, bounds.Y, bounds.Width, bounds.Height));
                        w.WriteEndObject();
                    }
                }

                foreach (PropertyEntry entry in PropertyReader.ReadAll(node))
                {
                    w.WriteStartObject();
                    w.WriteString("name", entry.DisplayName(typeName));
                    w.WriteString("value", Annotate(entry));
                    w.WriteEndObject();
                }
            }
            w.WriteEndArray();
        }

        private static void WriteOpHistogram(Utf8JsonWriter w)
        {
            w.WriteStartArray("computedStyle");

            foreach (string line in CompositionModel.OpHistogram()
                         .Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                string entry = line.Trim();
                // Lines read "MilCmdVisualSetOffset x412"; the count is the value.
                int split = entry.LastIndexOf(" x", StringComparison.Ordinal);
                if (split <= 0)
                    continue;

                w.WriteStartObject();
                w.WriteString("name", entry.AsSpan(0, split).ToString());
                w.WriteString("value", entry.AsSpan(split + 2).ToString());
                w.WriteEndObject();
            }

            w.WriteEndArray();
        }

        private void GetInlineStyles(JsonElement p, Utf8JsonWriter w)
        {
            object? node = Resolve(p);
            if (node == null)
                return;

            List<PropertyEntry> local = PropertyReader.ReadLocal(node);
            w.WritePropertyName("inlineStyle");
            WriteStyle(w, node, local, "local");
        }

        /// <summary>
        /// One synthetic rule per value source, strongest first. Everything is reported
        /// as an ordinary rule rather than using CDP's `inherited` bucket, which wants
        /// the ANCESTOR's rules: a WPF inherited value has no ancestor rule to point at,
        /// it is the same property resolved further up, and showing it in order is both
        /// simpler and closer to the truth.
        /// </summary>
        private void GetMatchedStyles(JsonElement p, Utf8JsonWriter w)
        {
            object? node = Resolve(p);
            if (node == null)
                return;

            List<PropertyEntry> all = PropertyReader.ReadAll(node);

            var bySource = new Dictionary<string, List<PropertyEntry>>(StringComparer.Ordinal);
            foreach (PropertyEntry entry in all)
            {
                // Defaults are every property that was never touched. They belong in
                // the Computed pane, not as a rule the size of the framework.
                if (string.Equals(entry.Source, nameof(BaseValueSource.Default), StringComparison.Ordinal))
                    continue;

                if (!bySource.TryGetValue(entry.Source, out List<PropertyEntry>? list))
                    bySource[entry.Source] = list = new List<PropertyEntry>();

                list.Add(entry);
            }

            // The local values also go out as the inline style, which is where the
            // panel puts element-level declarations.
            if (bySource.TryGetValue(nameof(BaseValueSource.Local), out List<PropertyEntry>? locals))
            {
                w.WritePropertyName("inlineStyle");
                WriteStyle(w, node, locals, "local");
            }

            w.WriteStartArray("matchedCSSRules");
            foreach (string source in SourceOrder)
            {
                if (source == nameof(BaseValueSource.Local))
                    continue;   // already reported as the inline style

                if (!bySource.TryGetValue(source, out List<PropertyEntry>? entries))
                    continue;

                w.WriteStartObject();
                w.WriteStartObject("rule");
                w.WriteString("styleSheetId", source);
                w.WriteStartObject("selectorList");
                w.WriteStartArray("selectors");
                w.WriteStartObject();
                w.WriteString("text", SelectorFor(source, node));
                w.WriteEndObject();
                w.WriteEndArray();
                w.WriteString("text", SelectorFor(source, node));
                w.WriteEndObject();
                // "regular" is the only origin the panel renders as an editable,
                // ordinary rule; "user-agent" would grey the whole group out.
                w.WriteString("origin", "regular");
                w.WritePropertyName("style");
                WriteStyle(w, node, entries, source);
                w.WriteEndObject();

                w.WriteStartArray("matchingSelectors");
                w.WriteNumberValue(0);
                w.WriteEndArray();
                w.WriteEndObject();
            }
            w.WriteEndArray();

            w.WriteStartArray("inherited");
            w.WriteEndArray();
            w.WriteStartArray("pseudoElements");
            w.WriteEndArray();
        }

        /// <summary>
        /// Apply an edited declaration block. Best effort by design: the Styles pane
        /// only offers an editor when a rule has a source RANGE, which these synthetic
        /// rules do not have, so most frontends will never send this. It is implemented
        /// because a scripted client reasonably might, and because it costs a dozen
        /// lines on top of the attribute editing that DOM already does.
        /// </summary>
        private void SetStyleTexts(JsonElement p, Utf8JsonWriter w)
        {
            w.WriteStartArray("styles");

            if (p.ValueKind == JsonValueKind.Object &&
                p.TryGetProperty("edits", out JsonElement edits) &&
                edits.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement edit in edits.EnumerateArray())
                {
                    int nodeId = CdpJson.GetInt(edit, "nodeId");
                    object resolved = nodeId != 0
                        ? _session.Dom.RequireNode(nodeId)
                        : _session.Dom.RequireNode(_session.Dom.InspectedNodeId);

                    if (resolved is not DependencyObject node)
                        throw new InvalidOperationException("editing is dependency properties only.");

                    string text = CdpJson.GetString(edit, "text") ?? string.Empty;
                    var applied = new List<PropertyEntry>();

                    foreach (KeyValuePair<string, string> declaration in PropertyWriter.ParseDeclarations(text))
                    {
                        if (!PropertyWriter.TryFind(node, declaration.Key, out DependencyProperty? property) || property == null)
                            throw new InvalidOperationException($"{node.GetType().Name} has no dependency property '{declaration.Key}'.");

                        if (!PropertyWriter.TrySet(node, property, declaration.Value, out string error))
                            throw new InvalidOperationException(error);
                    }

                    applied.AddRange(PropertyReader.ReadLocal(node));
                    WriteStyle(w, node, applied, "local");
                }
            }

            w.WriteEndArray();
        }

        /// <summary>
        /// A human-readable "selector" for a value source. Not a real selector -- there
        /// is nothing to select with -- but it is what the panel prints as the rule's
        /// heading, so it should say where the value came from.
        /// </summary>
        private static string SelectorFor(string source, object node)
        {
            string type = node.GetType().Name;
            return source switch
            {
                nameof(BaseValueSource.Style) => type + " { Style }",
                nameof(BaseValueSource.ImplicitStyleReference) => type + " { implicit Style }",
                nameof(BaseValueSource.DefaultStyle) => type + " { default Style }",
                nameof(BaseValueSource.StyleTrigger) => type + " { Style trigger }",
                nameof(BaseValueSource.DefaultStyleTrigger) => type + " { default Style trigger }",
                nameof(BaseValueSource.ParentTemplate) => type + " { parent ControlTemplate }",
                nameof(BaseValueSource.ParentTemplateTrigger) => type + " { parent template trigger }",
                nameof(BaseValueSource.TemplateTrigger) => type + " { template trigger }",
                nameof(BaseValueSource.Inherited) => type + " { inherited }",
                _ => type + " { " + source + " }",
            };
        }

        private static void WriteStyle(Utf8JsonWriter w, object node, List<PropertyEntry> entries, string styleSheetId)
        {
            string typeName = node.GetType().Name;
            var cssText = new StringBuilder();

            w.WriteStartObject();
            w.WriteString("styleSheetId", styleSheetId);
            w.WriteStartArray("cssProperties");

            foreach (PropertyEntry entry in entries)
            {
                string name = entry.DisplayName(typeName);
                string value = Annotate(entry);
                string declaration = name + ": " + value + ";";

                w.WriteStartObject();
                w.WriteString("name", name);
                w.WriteString("value", value);
                w.WriteString("text", declaration);
                // Nothing here came from a stylesheet the frontend can edit, so the
                // declaration is reported as implicit and without a source range. That
                // is also what stops the panel offering an edit box for it, which
                // matters until property editing actually exists.
                w.WriteBoolean("implicit", true);
                w.WriteBoolean("disabled", false);
                w.WriteEndObject();

                cssText.Append(declaration).Append('\n');
            }

            w.WriteEndArray();
            w.WriteStartArray("shorthandEntries");
            w.WriteEndArray();
            w.WriteString("cssText", cssText.ToString());
            w.WriteEndObject();
        }

        /// <summary>
        /// Values carry their modifiers inline, because an animated or coerced value is
        /// the single most confusing thing to look at without being told: the property
        /// reads as one thing and was set to another.
        /// </summary>
        private static string Annotate(PropertyEntry entry)
            => entry.Modifiers == null ? entry.Value : entry.Value + "  /* " + entry.Modifiers + " */";
    }
}
