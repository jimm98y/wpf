// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// Runtime: just enough for the frontend to consider itself attached, plus $0.
//
// There is no script engine behind this and there is not going to be one, so
// evaluate reports a real exception for anything it cannot answer rather than
// returning a plausible-looking undefined. The one expression that does work is
// $0 -- the node selected in the Elements panel -- and getProperties expands it
// into its dependency properties, which makes the console a usable second view
// on the selected element.
//

using System;
using System.Globalization;
using System.Text.Json;
using System.Windows;
using Microsoft.Wpf.DevTools.Json;

namespace Microsoft.Wpf.DevTools.Domains
{
    internal sealed class RuntimeDomain : ICdpDomain
    {
        private const int ExecutionContextId = 1;

        private readonly CdpSession _session;

        internal RuntimeDomain(CdpSession session)
        {
            _session = session;
        }

        public bool TryHandle(string method, JsonElement p, Utf8JsonWriter w)
        {
            switch (method)
            {
                case "Runtime.enable":
                    // After the response: the frontend treats a context that arrives
                    // before the enable it asked for as belonging to a previous session.
                    _session.AfterResponse(() => _session.SendEvent("Runtime.executionContextCreated", ev =>
                    {
                        ev.WriteStartObject("context");
                        ev.WriteNumber("id", ExecutionContextId);
                        ev.WriteString("origin", "wpf://app");
                        ev.WriteString("name", "WPF visual tree");
                        ev.WriteString("uniqueId", ExecutionContextId.ToString(CultureInfo.InvariantCulture));
                        ev.WriteEndObject();
                    }));
                    return true;

                case "Runtime.disable":
                case "Runtime.releaseObject":
                case "Runtime.releaseObjectGroup":
                case "Runtime.runIfWaitingForDebugger":
                case "Runtime.discardConsoleEntries":
                    return true;

                case "Runtime.evaluate":
                    Evaluate(p, w);
                    return true;

                case "Runtime.getProperties":
                    GetProperties(p, w);
                    return true;

                default:
                    return false;
            }
        }

        private void Evaluate(JsonElement p, Utf8JsonWriter w)
        {
            string expression = (CdpJson.GetString(p, "expression") ?? string.Empty).Trim();

            if (expression == "$0")
            {
                object? node = _session.Dom.ResolveNode(_session.Dom.InspectedNodeId);
                if (node != null)
                {
                    WriteNodeObject(w, node, _session.Dom.InspectedNodeId);
                    return;
                }
            }

            w.WriteStartObject("result");
            w.WriteString("type", "undefined");
            w.WriteEndObject();

            w.WriteStartObject("exceptionDetails");
            w.WriteNumber("exceptionId", 1);
            w.WriteString("text", "Uncaught");
            w.WriteNumber("lineNumber", 0);
            w.WriteNumber("columnNumber", 0);
            w.WriteStartObject("exception");
            w.WriteString("type", "object");
            w.WriteString("subtype", "error");
            w.WriteString("className", "Error");
            w.WriteString("description",
                expression == "$0"
                    ? "$0 is not set: select an element in the Elements panel first."
                    : "This inspector evaluates only $0 (the selected element). There is no script engine behind a WPF visual tree.");
            w.WriteEndObject();
            w.WriteEndObject();
        }

        private void WriteNodeObject(Utf8JsonWriter w, object node, int nodeId)
        {
            w.WriteStartObject("result");
            w.WriteString("type", "object");
            w.WriteString("className", node.GetType().Name);
            w.WriteString("description", DomDomain.DescriptionOf(node));
            w.WriteString("objectId", nodeId.ToString(CultureInfo.InvariantCulture));
            w.WriteEndObject();
        }

        private void GetProperties(JsonElement p, Utf8JsonWriter w)
        {
            w.WriteStartArray("result");

            string? objectId = CdpJson.GetString(p, "objectId");
            if (objectId != null &&
                int.TryParse(objectId, NumberStyles.Integer, CultureInfo.InvariantCulture, out int nodeId))
            {
                object? node = _session.Dom.ResolveNode(nodeId);
                if (node != null)
                {
                    string typeName = node.GetType().Name;
                    foreach (PropertyEntry entry in PropertyReader.ReadAll(node))
                    {
                        w.WriteStartObject();
                        w.WriteString("name", entry.DisplayName(typeName));
                        w.WriteBoolean("configurable", false);
                        w.WriteBoolean("enumerable", true);
                        w.WriteBoolean("isOwn", true);
                        w.WriteStartObject("value");
                        w.WriteString("type", "string");
                        w.WriteString("value", entry.Value);
                        w.WriteString("description", entry.Value + "  [" + entry.Source + "]");
                        w.WriteEndObject();
                        w.WriteEndObject();
                    }
                }
            }

            w.WriteEndArray();
        }
    }
}
