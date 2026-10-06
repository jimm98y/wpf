// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// DOM: the visual tree, shaped as a document.
//
//   #document (1)
//     Application (2)          synthetic; one child per PresentationSource
//       Window                 a real root visual
//         Border ...
//
// The Application element exists because the Elements panel wants exactly one
// document element and a WPF process routinely has several roots at once -- a
// Window, an open Popup, a tooltip are three PresentationSources, not one tree.
//
// Text is surfaced as a #text child rather than an attribute so the panel reads
// like markup. Those nodes have no DependencyObject behind them, so their ids
// come from VisualTreeModel.AllocateSyntheticId and map back to the owner.
//

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Threading;
using System.Windows.Threading;
using Microsoft.Wpf.DevTools.Json;

namespace Microsoft.Wpf.DevTools.Domains
{
    internal sealed class DomDomain : ICdpDomain, IDisposable
    {
        /// <summary>Attributes shown inline on a node in the Elements panel.</summary>
        private const int MaxAttributes = 12;
        private const int MaxAttributeValueLength = 40;

        /// <summary>Nodes visited when hashing the tree's shape. Bounds the poll's cost.</summary>
        private const int StructureHashNodeCap = 4000;

        /// <summary>How often the tree's shape is re-hashed, in milliseconds.</summary>
        private const int PollIntervalMs = 1000;

        /// <summary>
        /// Element levels always included in a DOM.getDocument reply: Application, the
        /// window, and enough of its chrome that the panel opens on something worth
        /// reading rather than on a single collapsed node. See DocumentDepth.
        /// </summary>
        private const int MinimumDocumentDepth = 4;

        /// <summary>
        /// Local values that are always present, never authored, and never what
        /// anyone is looking at. Filtering them is the difference between a node
        /// line that reads like its XAML and one buried in framework bookkeeping.
        /// </summary>
        private static readonly HashSet<string> AttributeDenyList = new HashSet<string>(StringComparer.Ordinal)
        {
            "NameScope", "IWindowService", "RootSource", "BaseUri", "Template", "Style",
            "FocusVisualStyle", "ItemTemplate", "DataContext", "ItemsSource", "Content",
            "XmlNamespaceMaps", "XmlnsDictionary", "WindowChrome", "WindowChromeWorker",
            "HasContent", "HasItems", "IsGrouping", "IsVisible", "ActualWidth", "ActualHeight",
        };

        /// <summary>See PushRootChildren. Off by default; it breaks deeper expansion.</summary>
        private static readonly bool s_pushRoots =
            Environment.GetEnvironmentVariable("WPF_DEVTOOLS_PUSH_ROOTS") is string v && v.Length > 0 && v != "0";

        private readonly CdpSession _session;

        // Text nodes, both directions.
        private readonly Dictionary<int, int> _textNodeByOwner = new Dictionary<int, int>();
        private readonly Dictionary<int, int> _ownerByTextNode = new Dictionary<int, int>();

        /// <summary>
        /// Nodes this frontend has actually been sent. A node id it has never seen is one it
        /// cannot select or reveal, however valid the id is -- which is why the picker has to
        /// deliver the path to a node before naming it.
        /// </summary>
        private readonly HashSet<int> _delivered = new HashSet<int>();

        private Timer? _structurePoll;
        private int _structureHash;

        private List<int>? _searchResults;
        private int _searchId;

        internal DomDomain(CdpSession session)
        {
            _session = session;
        }

        /// <summary>The node the frontend last selected, which Runtime exposes as $0.</summary>
        internal int InspectedNodeId { get; private set; }

        public bool TryHandle(string method, JsonElement p, Utf8JsonWriter w)
        {
            switch (method)
            {
                case "DOM.enable":
                    StartStructurePoll();
                    return true;

                case "DOM.disable":
                    StopStructurePoll();
                    return true;

                case "DOM.getDocument":
                    WriteDocument(w, DocumentDepth(p));
                    if (s_pushRoots)
                        PushRootChildren();
                    return true;

                case "DOM.requestChildNodes":
                    RequestChildNodes(p);
                    return true;

                case "DOM.getNodeForLocation":
                    GetNodeForLocation(p, w);
                    return true;

                case "DOM.describeNode":
                    DescribeNode(p, w);
                    return true;

                case "DOM.getBoxModel":
                    GetBoxModel(p, w);
                    return true;

                case "DOM.resolveNode":
                    ResolveNodeCommand(p, w);
                    return true;

                case "DOM.setAttributeValue":
                    SetAttribute(NodeIdFrom(p), CdpJson.GetString(p, "name"), CdpJson.GetString(p, "value"));
                    return true;

                case "DOM.setAttributesAsText":
                    SetAttributesAsText(p);
                    return true;

                case "DOM.removeAttribute":
                    RemoveAttribute(NodeIdFrom(p), CdpJson.GetString(p, "name"));
                    return true;

                case "DOM.setInspectedNode":
                    InspectedNodeId = CdpJson.GetInt(p, "nodeId");
                    return true;

                case "DOM.pushNodesByBackendIdsToFrontend":
                    PushNodesByBackendIds(p, w);
                    return true;

                case "DOM.performSearch":
                    PerformSearch(p, w);
                    return true;

                case "DOM.getSearchResults":
                    GetSearchResults(p, w);
                    return true;

                case "DOM.discardSearchResults":
                    _searchResults = null;
                    return true;

                default:
                    return false;
            }
        }

        public void Dispose() => StopStructurePoll();

        // ------------------------------------------------------------------
        // Node identity
        // ------------------------------------------------------------------

        /// <summary>
        /// The object a frontend node id refers to. A #text id resolves to the element
        /// that owns the text, so selecting text in the panel shows that element's
        /// properties rather than nothing.
        /// </summary>
        internal object? ResolveNode(int nodeId)
        {
            if (_ownerByTextNode.TryGetValue(nodeId, out int owner))
                nodeId = owner;

            return _session.Model.Resolve(nodeId);
        }

        /// <summary>Read whichever of nodeId / backendNodeId / objectId the caller sent.</summary>
        internal int NodeIdFrom(JsonElement p)
        {
            int nodeId = CdpJson.GetInt(p, "nodeId");
            if (nodeId != 0)
                return nodeId;

            nodeId = CdpJson.GetInt(p, "backendNodeId");
            if (nodeId != 0)
                return nodeId;

            // Runtime hands back the objectId minted by DOM.resolveNode.
            string? objectId = CdpJson.GetString(p, "objectId");
            if (objectId != null && int.TryParse(objectId, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed))
                return parsed;

            return 0;
        }

        // ------------------------------------------------------------------
        // Serialisation
        // ------------------------------------------------------------------

        private static int DepthOf(JsonElement p)
        {
            int depth = CdpJson.GetInt(p, "depth", 1);
            // CDP spells "the whole subtree" as -1. Cap it so a pathological tree
            // cannot produce a message the frontend chokes on.
            return depth < 0 ? 64 : depth;
        }

        /// <summary>
        /// The depth the DOCUMENT is served at, which is not quite the depth asked for.
        ///
        /// A frontend asks for depth 1 and gets #document plus Application, whose children
        /// are then a separate round trip -- and a panel that does not expand on its own
        /// shows one empty node, which is indistinguishable from a broken inspector. The
        /// obvious fix, pushing the children unasked, is worse: the frontend asks for them
        /// too, and the second delivery rebuilds the subtree under fresh node objects while
        /// the tree being drawn still holds the first set, so every later expansion lands on
        /// nodes nothing is showing.
        ///
        /// Sending them in the RESPONSE has neither problem. It is one delivery, it is the
        /// same payload shape a depth of -1 produces, and the frontend has no reason to ask
        /// again for children it already has.
        /// </summary>
        private static int DocumentDepth(JsonElement p)
            => Math.Max(DepthOf(p), MinimumDocumentDepth);

        private void WriteDocument(Utf8JsonWriter w, int depth)
        {
            w.WriteStartObject("root");
            w.WriteNumber("nodeId", VisualTreeModel.DocumentNodeId);
            w.WriteNumber("backendNodeId", VisualTreeModel.DocumentNodeId);
            w.WriteNumber("nodeType", 9);           // DOCUMENT_NODE
            w.WriteString("nodeName", "#document");
            w.WriteString("localName", string.Empty);
            w.WriteString("nodeValue", string.Empty);
            w.WriteString("documentURL", "wpf://app/");
            w.WriteString("baseURL", "wpf://app/");
            // Marks the document as XML so the panel keeps PascalCase element names
            // instead of lowercasing them the way it does for HTML.
            w.WriteString("xmlVersion", "1.0");
            // Binds the document to the frame Page.getFrameTree reports. Real CDP sends
            // this and the frontend's ResourceTreeModel looks for it.
            w.WriteString("frameId", PageDomain.FrameId);
            w.WriteNumber("childNodeCount", 1);

            w.WriteStartArray("children");
            WriteApplicationNode(w, depth - 1);
            w.WriteEndArray();

            w.WriteEndObject();
        }

        private void WriteApplicationNode(Utf8JsonWriter w, int depth)
        {
            List<object> roots = _session.Roots();

            w.WriteStartObject();
            w.WriteNumber("nodeId", VisualTreeModel.ApplicationNodeId);
            w.WriteNumber("backendNodeId", VisualTreeModel.ApplicationNodeId);
            w.WriteNumber("parentId", VisualTreeModel.DocumentNodeId);
            w.WriteNumber("nodeType", 1);
            bool composition = _session.Kind == TargetKind.Composition;
            string rootName = composition ? "Composition" : "Application";

            w.WriteString("nodeName", rootName);
            w.WriteString("localName", rootName);
            w.WriteString("nodeValue", string.Empty);
            w.WriteNumber("childNodeCount", roots.Count);

            w.WriteStartArray("attributes");
            w.WriteStringValue(composition ? "targets" : "sources");
            w.WriteStringValue(roots.Count.ToString(CultureInfo.InvariantCulture));
            if (composition)
            {
                // The resource-table counts, right on the root, so "what does the compositor
                // actually hold" is answered before you expand anything.
                foreach (string pair in CompositionModel.State().Split(' ', StringSplitOptions.RemoveEmptyEntries))
                {
                    int equals = pair.IndexOf('=');
                    if (equals <= 0)
                        continue;

                    w.WriteStringValue(pair.AsSpan(0, equals).ToString());
                    w.WriteStringValue(pair.AsSpan(equals + 1).ToString());
                }
            }
            w.WriteEndArray();

            if (depth != 0)
            {
                w.WriteStartArray("children");
                foreach (object root in roots)
                    WriteNode(w, root, VisualTreeModel.ApplicationNodeId, depth - 1);
                w.WriteEndArray();
            }

            w.WriteEndObject();
        }

        private void WriteNode(Utf8JsonWriter w, object node, int parentId, int depth)
        {
            int nodeId = _session.Model.IdOf(node);
            List<object> children = VisualTreeModel.Children(node);
            string? text = VisualTreeModel.NodeText(node);

            // A node's own text counts as a child, so the panel shows a disclosure
            // triangle for a TextBlock exactly as it would for a <span>.
            int childCount = children.Count + (text != null ? 1 : 0);

            _delivered.Add(nodeId);

            w.WriteStartObject();
            w.WriteNumber("nodeId", nodeId);
            w.WriteNumber("backendNodeId", nodeId);
            w.WriteNumber("parentId", parentId);
            w.WriteNumber("nodeType", 1);           // ELEMENT_NODE
            string name = VisualTreeModel.NodeName(node);
            w.WriteString("nodeName", name);
            w.WriteString("localName", name);
            w.WriteString("nodeValue", string.Empty);
            w.WriteNumber("childNodeCount", childCount);

            WriteAttributes(w, node);

            if (depth != 0 && childCount > 0)
            {
                w.WriteStartArray("children");
                if (text != null)
                    WriteTextNode(w, nodeId, text);
                foreach (object child in children)
                    WriteNode(w, child, nodeId, depth - 1);
                w.WriteEndArray();
            }

            w.WriteEndObject();
        }

        private void WriteTextNode(Utf8JsonWriter w, int ownerId, string text)
        {
            if (!_textNodeByOwner.TryGetValue(ownerId, out int textId))
            {
                textId = _session.Model.AllocateSyntheticId();
                _textNodeByOwner[ownerId] = textId;
                _ownerByTextNode[textId] = ownerId;
            }

            _delivered.Add(textId);

            w.WriteStartObject();
            w.WriteNumber("nodeId", textId);
            w.WriteNumber("backendNodeId", textId);
            w.WriteNumber("parentId", ownerId);
            w.WriteNumber("nodeType", 3);           // TEXT_NODE
            w.WriteString("nodeName", "#text");
            w.WriteString("localName", string.Empty);
            w.WriteString("nodeValue", text);
            w.WriteNumber("childNodeCount", 0);
            w.WriteEndObject();
        }

        /// <summary>
        /// x:Name goes out as "id", which makes the panel render Button#BackButton the
        /// way it renders a DOM id. The rest are the node's LOCAL values -- what the
        /// XAML author actually wrote -- minus framework bookkeeping and anything whose
        /// value is an object dump rather than a scalar.
        /// </summary>
        private static void WriteAttributes(Utf8JsonWriter w, object node)
        {
            w.WriteStartArray("attributes");

            string? name = VisualTreeModel.NameOf(node);
            if (name != null)
            {
                w.WriteStringValue("id");
                w.WriteStringValue(name);
            }

            // On a scene node, say which element it was decoded from. The handle is the join,
            // but "0x1A" tells you nothing on its own; "Button#BackButton" is the answer to
            // the question you actually had.
            if (CompositionModel.Owns(node) && VisualTreeModel.AsVisual(node) is Visual element)
            {
                w.WriteStringValue("element");
                w.WriteStringValue(DescriptionOf(element));
            }

            int written = 0;
            foreach (PropertyEntry entry in PropertyReader.ReadLocal(node))
            {
                if (written >= MaxAttributes)
                    break;

                if (entry.Name == "Name" || AttributeDenyList.Contains(entry.Name))
                    continue;

                if (!IsScalar(entry.Value))
                    continue;

                w.WriteStringValue(entry.Name);
                w.WriteStringValue(entry.Value);
                written++;
            }

            w.WriteEndArray();
        }

        private static bool IsScalar(string value)
        {
            if (value.Length == 0 || value.Length > MaxAttributeValueLength)
                return false;

            // A ToString that fell back to the type name is noise on a node line; it
            // is still available in the Styles and Computed panes.
            return !value.StartsWith("System.", StringComparison.Ordinal)
                && !value.StartsWith("Microsoft.", StringComparison.Ordinal)
                && value.IndexOf('`') < 0
                && value.IndexOf('\n') < 0;
        }

        // ------------------------------------------------------------------
        // Commands
        // ------------------------------------------------------------------

        /// <summary>
        /// Hand the frontend the roots under Application without being asked.
        ///
        /// OFF BY DEFAULT, because it turned out to cause a worse bug than the one it fixed.
        /// A frontend asks for those same children itself a moment later, and delivering them
        /// twice makes it rebuild the subtree: the second delivery creates fresh DOMNode
        /// objects and rebinds the ids, while the tree it is DRAWING still holds the first
        /// set. Every later setChildNodes then lands on nodes nothing is showing, so deeper
        /// expansion silently does nothing -- which looks like the tree simply stops.
        ///
        /// Kept behind WPF_DEVTOOLS_PUSH_ROOTS=1 for a frontend that genuinely never asks.
        /// </summary>
        private void PushRootChildren()
        {
            _session.AfterResponse(() => _session.Ui.Invoke(() =>
            {
                _session.SendEvent("DOM.setChildNodes", ev =>
                {
                    ev.WriteNumber("parentId", VisualTreeModel.ApplicationNodeId);
                    ev.WriteStartArray("nodes");
                    foreach (object root in _session.Roots())
                        WriteNode(ev, root, VisualTreeModel.ApplicationNodeId, depth: 1);
                    ev.WriteEndArray();
                });
            }));
        }

        private void RequestChildNodes(JsonElement p)
        {
            int parentId = NodeIdFrom(p);
            int depth = DepthOf(p);

            // The nodes arrive as an event and the reply itself is empty -- but the ORDER
            // matters, and it is the opposite of what it looks like.
            //
            // The frontend does not wait for the event. It invokes requestChildNodes and, in
            // the RESPONSE callback, reads the children it expects to already have:
            //
            //     invoke_requestChildNodes({nodeId}).then(() => callback(this.children()))
            //
            // So setChildNodes has to be on the wire BEFORE the response, which is what real
            // Chrome does. Sending it afterwards -- the obvious reading of "reply, then push"
            // -- leaves that callback looking at an empty child list, and the node expands to
            // nothing while the protocol log shows both messages sent and no error anywhere.
            // Handlers run before the response is written, so emitting here is the fix.
            {
                {
                    _session.SendEvent("DOM.setChildNodes", ev =>
                    {
                        ev.WriteNumber("parentId", parentId);
                        ev.WriteStartArray("nodes");

                        if (parentId == VisualTreeModel.ApplicationNodeId)
                        {
                            foreach (object root in _session.Roots())
                                WriteNode(ev, root, parentId, depth - 1);
                        }
                        else
                        {
                            object? parent = ResolveNode(parentId);
                            if (parent != null)
                            {
                                string? text = VisualTreeModel.NodeText(parent);
                                if (text != null)
                                    WriteTextNode(ev, parentId, text);

                                foreach (object child in VisualTreeModel.Children(parent))
                                    WriteNode(ev, child, parentId, depth - 1);
                            }
                        }

                        ev.WriteEndArray();
                    });
                }
            }
        }

        // ------------------------------------------------------------------
        // Editing
        //
        // Failures THROW. The session turns an exception into a CDP error, which is
        // what puts the reason in front of whoever typed the value -- returning an
        // empty result would silently discard the edit and leave the panel showing
        // the new text over an unchanged property.
        // ------------------------------------------------------------------

        private void SetAttribute(int nodeId, string? name, string? value)
        {
            object node = RequireNode(nodeId);

            if (string.Equals(name, "id", StringComparison.Ordinal))
                throw new InvalidOperationException("x:Name is fixed once an element is in a name scope.");

            if (node is not DependencyObject target)
                throw new InvalidOperationException($"{VisualTreeModel.NodeName(node)} is not a WPF element; editing is dependency properties only.");

            if (!PropertyWriter.TryFind(target, name ?? string.Empty, out DependencyProperty? property) || property == null)
                throw new InvalidOperationException($"{VisualTreeModel.NodeName(node)} has no dependency property '{name}'.");

            if (!PropertyWriter.TrySet(target, property, value, out string error))
                throw new InvalidOperationException(error);

            NotifyAttributeModified(nodeId, name!, value ?? string.Empty);
        }

        /// <summary>
        /// The panel sends the whole attribute text when an attribute is edited in
        /// place, and names the one that was replaced so it can be removed first.
        /// </summary>
        private void SetAttributesAsText(JsonElement p)
        {
            int nodeId = NodeIdFrom(p);
            string text = CdpJson.GetString(p, "text") ?? string.Empty;
            string? replaced = CdpJson.GetString(p, "name");

            foreach (KeyValuePair<string, string> pair in ParseAttributeText(text))
                SetAttribute(nodeId, pair.Key, pair.Value);

            // An edit that renames an attribute away leaves the old one to clear.
            if (!string.IsNullOrEmpty(replaced) &&
                !ParseAttributeText(text).Exists(a => string.Equals(a.Key, replaced, StringComparison.Ordinal)))
            {
                RemoveAttribute(nodeId, replaced);
            }
        }

        private void RemoveAttribute(int nodeId, string? name)
        {
            object node = RequireNode(nodeId);

            if (node is not DependencyObject target)
                throw new InvalidOperationException($"{VisualTreeModel.NodeName(node)} is not a WPF element; editing is dependency properties only.");

            if (!PropertyWriter.TryFind(target, name ?? string.Empty, out DependencyProperty? property) || property == null)
                throw new InvalidOperationException($"{VisualTreeModel.NodeName(node)} has no dependency property '{name}'.");

            if (!PropertyWriter.TryClear(target, property, out string error))
                throw new InvalidOperationException(error);

            _session.AfterResponse(() => _session.SendEvent("DOM.attributeRemoved", ev =>
            {
                ev.WriteNumber("nodeId", nodeId);
                ev.WriteString("name", name!);
            }));
        }

        private void NotifyAttributeModified(int nodeId, string name, string value)
        {
            _session.AfterResponse(() => _session.SendEvent("DOM.attributeModified", ev =>
            {
                ev.WriteNumber("nodeId", nodeId);
                ev.WriteString("name", name);
                ev.WriteString("value", value);
            }));
        }

        internal object RequireNode(int nodeId)
            => ResolveNode(nodeId)
               ?? throw new InvalidOperationException($"node {nodeId.ToString(CultureInfo.InvariantCulture)} is gone.");

        /// <summary>Split `Width="120" Background="Red"` into pairs.</summary>
        private static List<KeyValuePair<string, string>> ParseAttributeText(string text)
        {
            var result = new List<KeyValuePair<string, string>>();
            int i = 0;

            while (i < text.Length)
            {
                while (i < text.Length && char.IsWhiteSpace(text[i])) i++;

                int nameStart = i;
                while (i < text.Length && text[i] != '=' && !char.IsWhiteSpace(text[i])) i++;
                if (i >= text.Length || nameStart == i) break;

                string name = text.Substring(nameStart, i - nameStart);
                while (i < text.Length && (char.IsWhiteSpace(text[i]) || text[i] == '=')) i++;
                if (i >= text.Length) break;

                char quote = text[i];
                if (quote is '"' or '\'')
                {
                    i++;
                    int valueStart = i;
                    while (i < text.Length && text[i] != quote) i++;
                    result.Add(new KeyValuePair<string, string>(name, text.Substring(valueStart, i - valueStart)));
                    i++;
                }
                else
                {
                    int valueStart = i;
                    while (i < text.Length && !char.IsWhiteSpace(text[i])) i++;
                    result.Add(new KeyValuePair<string, string>(name, text.Substring(valueStart, i - valueStart)));
                }
            }

            return result;
        }

        /// <summary>
        /// The node at a point. This is how "Select element" actually works over a
        /// screencast: the frontend does the picking itself, asking for the node under the
        /// cursor on every mouse move and highlighting the answer -- it never turns on the
        /// backend's own inspect mode. Leaving this unimplemented makes the picker look
        /// completely dead while the protocol log fills with hundreds of these a minute.
        /// </summary>
        /// <summary>
        /// Make a set of nodes addressable by the frontend, then name them.
        ///
        /// The ids are already valid -- ours are stable and backendNodeId equals nodeId -- so
        /// it is tempting to just echo them back, which is what this did at first. It does not
        /// work: a frontend can only SELECT a node it has actually been sent, and it has only
        /// been sent the few levels the document reply carried. Echoing an id for a node it
        /// has never seen leaves it to fall back on the deepest ancestor it does have, so the
        /// element picker appeared to pick a parent no matter what you pointed at.
        ///
        /// So walk each node's ancestry and deliver the levels it is missing, top down, before
        /// answering. Top down because a frontend cannot attach children to a parent it has
        /// not got yet.
        /// </summary>
        private void PushNodesByBackendIds(JsonElement p, Utf8JsonWriter w)
        {
            var ids = new List<int>();
            if (p.ValueKind == JsonValueKind.Object &&
                p.TryGetProperty("backendNodeIds", out JsonElement requested) &&
                requested.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement id in requested.EnumerateArray())
                {
                    if (id.TryGetInt32(out int value))
                        ids.Add(value);
                }
            }

            foreach (int id in ids)
                PushPathTo(id);

            w.WriteStartArray("nodeIds");
            foreach (int id in ids)
                w.WriteNumberValue(id);
            w.WriteEndArray();
        }

        /// <summary>
        /// Deliver every level between the root and this node that the frontend is missing.
        /// Emitted during the command, so it is on the wire before the reply -- the frontend
        /// reads its model in the response callback.
        /// </summary>
        private void PushPathTo(int nodeId)
        {
            object? node = ResolveNode(nodeId);
            if (node == null)
                return;

            // Root-first chain of ancestors, excluding the node itself: those are the parents
            // whose children have to be delivered for the node to exist in the frontend.
            var chain = new List<object>();
            for (object? current = VisualTreeModel.ParentOf(node); current != null; current = VisualTreeModel.ParentOf(current))
                chain.Add(current);
            chain.Reverse();

            // The roots hang off the synthetic Application element, so that level comes first.
            if (chain.Count > 0 && !_delivered.Contains(_session.Model.IdOf(chain[0])))
                SendChildren(VisualTreeModel.ApplicationNodeId, _session.Roots());

            foreach (object ancestor in chain)
            {
                List<object> children = VisualTreeModel.Children(ancestor);
                string? text = VisualTreeModel.NodeText(ancestor);

                bool missing = children.Exists(c => !_delivered.Contains(_session.Model.IdOf(c)));
                if (!missing)
                    continue;

                SendChildren(_session.Model.IdOf(ancestor), children, text);
            }
        }

        private void SendChildren(int parentId, List<object> children, string? text = null)
        {
            _session.SendEvent("DOM.setChildNodes", ev =>
            {
                ev.WriteNumber("parentId", parentId);
                ev.WriteStartArray("nodes");
                if (text != null)
                    WriteTextNode(ev, parentId, text);
                foreach (object child in children)
                    WriteNode(ev, child, parentId, depth: 0);
                ev.WriteEndArray();
            });
        }

        private void GetNodeForLocation(JsonElement p, Utf8JsonWriter w)
        {
            object? hit = VisualTreeModel.HitTest(new Point(CdpJson.GetDouble(p, "x"), CdpJson.GetDouble(p, "y")));
            if (hit == null)
                return;

            // On the composition document the picker still points at the real window -- there
            // is nothing else to point at -- so the hit is a WPF element, and what the user
            // wants selected is the scene node decoded from it. Hop across on the handle.
            if (_session.Kind == TargetKind.Composition)
            {
                hit = hit is Visual visual &&
                      System.Windows.Diagnostics.CompositionDiagnostics.GetCompositionHandle(visual) is uint handle &&
                      CompositionModel.FindByHandle(handle) is object scene
                    ? scene
                    : null;

                if (hit == null)
                    return;
            }

            int nodeId = _session.Model.IdOf(hit);

            // Deliver the node's ancestry FIRST, then name it.
            //
            // Both halves are required and neither is sufficient. The frontend does:
            //
            //     if (!response.nodeId) return null;
            //     return this.nodeForId(response.nodeId);
            //
            // so omitting nodeId makes it give up before it starts -- 568 hovers produced not
            // one Overlay.highlightNode. But nodeForId only resolves against nodes it has
            // already been SENT, so naming a node it has never seen fails just as quietly, and
            // the picker settles for the deepest ancestor it happens to hold. Pushing the path
            // here satisfies both: the node exists in its model by the time it reads the id.
            //
            // Cheap in practice despite running on every hover: _delivered means each level is
            // sent once, so after the first few moves this is a no-op.
            PushPathTo(nodeId);

            w.WriteNumber("backendNodeId", nodeId);
            w.WriteString("frameId", PageDomain.FrameId);
            w.WriteNumber("nodeId", nodeId);
        }

        private void DescribeNode(JsonElement p, Utf8JsonWriter w)
        {
            int nodeId = NodeIdFrom(p);
            object? node = ResolveNode(nodeId);
            if (node == null)
                return;

            w.WriteStartObject("node");
            w.WriteNumber("nodeId", nodeId);
            w.WriteNumber("backendNodeId", nodeId);
            w.WriteNumber("nodeType", 1);
            string name = VisualTreeModel.NodeName(node);
            w.WriteString("nodeName", name);
            w.WriteString("localName", name);
            w.WriteString("nodeValue", string.Empty);
            w.WriteNumber("childNodeCount", VisualTreeModel.Children(node).Count);
            WriteAttributes(w, node);
            w.WriteEndObject();
        }

        private void GetBoxModel(JsonElement p, Utf8JsonWriter w)
        {
            object? node = ResolveNode(NodeIdFrom(p));
            if (node == null || !VisualTreeModel.TryGetBounds(node, out Rect content))
                return;

            Thickness margin = VisualTreeModel.MarginOf(node);
            Thickness border = VisualTreeModel.BorderOf(node);
            Thickness padding = VisualTreeModel.PaddingOf(node);

            // WPF's Rect for an element is its BORDER box: the margin sits outside it,
            // and border then padding come off the inside to reach the content box.
            Rect borderBox = content;
            Rect marginBox = Expand(borderBox, margin);
            Rect paddingBox = Shrink(borderBox, border);
            Rect contentBox = Shrink(paddingBox, padding);

            w.WriteStartObject("model");
            WriteQuad(w, "content", contentBox);
            WriteQuad(w, "padding", paddingBox);
            WriteQuad(w, "border", borderBox);
            WriteQuad(w, "margin", marginBox);
            w.WriteNumber("width", (int)Math.Round(borderBox.Width));
            w.WriteNumber("height", (int)Math.Round(borderBox.Height));
            w.WriteEndObject();
        }

        private static Rect Expand(Rect r, Thickness t)
            => new Rect(r.X - t.Left, r.Y - t.Top,
                        Math.Max(0, r.Width + t.Left + t.Right),
                        Math.Max(0, r.Height + t.Top + t.Bottom));

        private static Rect Shrink(Rect r, Thickness t)
            => new Rect(r.X + t.Left, r.Y + t.Top,
                        Math.Max(0, r.Width - t.Left - t.Right),
                        Math.Max(0, r.Height - t.Top - t.Bottom));

        /// <summary>A CDP quad: four corners, clockwise from top-left, flattened.</summary>
        private static void WriteQuad(Utf8JsonWriter w, string name, Rect r)
        {
            w.WriteStartArray(name);
            w.WriteNumberValue(r.Left);  w.WriteNumberValue(r.Top);
            w.WriteNumberValue(r.Right); w.WriteNumberValue(r.Top);
            w.WriteNumberValue(r.Right); w.WriteNumberValue(r.Bottom);
            w.WriteNumberValue(r.Left);  w.WriteNumberValue(r.Bottom);
            w.WriteEndArray();
        }

        private void ResolveNodeCommand(JsonElement p, Utf8JsonWriter w)
        {
            int nodeId = NodeIdFrom(p);
            object? node = ResolveNode(nodeId);
            if (node == null)
                return;

            w.WriteStartObject("object");
            w.WriteString("type", "object");
            w.WriteString("className", node.GetType().Name);
            w.WriteString("description", DescriptionOf(node));
            w.WriteString("objectId", nodeId.ToString(CultureInfo.InvariantCulture));
            w.WriteEndObject();
        }

        internal static string DescriptionOf(object node)
        {
            string name = VisualTreeModel.NodeName(node);
            string? id = VisualTreeModel.NameOf(node);
            return id == null ? name : name + "#" + id;
        }

        private void PerformSearch(JsonElement p, Utf8JsonWriter w)
        {
            string query = CdpJson.GetString(p, "query") ?? string.Empty;
            var results = new List<int>();

            if (query.Length > 0)
            {
                foreach (object root in _session.Roots())
                    SearchInto(root, query, results);
            }

            _searchResults = results;
            _searchId++;

            w.WriteString("searchId", _searchId.ToString(CultureInfo.InvariantCulture));
            w.WriteNumber("resultCount", results.Count);
        }

        /// <summary>Matches a type name, an x:Name, or the node's text, case-insensitively.</summary>
        private void SearchInto(object node, string query, List<int> results)
        {
            if (Contains(VisualTreeModel.NodeName(node), query) ||
                Contains(VisualTreeModel.NameOf(node), query) ||
                Contains(VisualTreeModel.NodeText(node), query))
            {
                results.Add(_session.Model.IdOf(node));
            }

            foreach (object child in VisualTreeModel.Children(node))
                SearchInto(child, query, results);

            static bool Contains(string? haystack, string needle)
                => haystack != null && haystack.Contains(needle, StringComparison.OrdinalIgnoreCase);
        }

        private void GetSearchResults(JsonElement p, Utf8JsonWriter w)
        {
            w.WriteStartArray("nodeIds");

            if (_searchResults != null)
            {
                int from = Math.Max(0, CdpJson.GetInt(p, "fromIndex"));
                int to = Math.Min(_searchResults.Count, CdpJson.GetInt(p, "toIndex", _searchResults.Count));
                for (int i = from; i < to; i++)
                    w.WriteNumberValue(_searchResults[i]);
            }

            w.WriteEndArray();
        }

        // ------------------------------------------------------------------
        // Invalidation
        // ------------------------------------------------------------------

        //
        // Polled rather than driven by VisualDiagnostics.VisualTreeChanged, which is
        // the obvious source and the wrong default. Subscribing to it flips
        // s_HasVisualTreeChangedListeners inside PresentationCore, which arms
        // VerifyVisualTreeChange and makes any RE-ENTRANT tree mutation throw
        // InvalidOperationException -- in an app that was working fine until someone
        // opened the inspector. A diagnostic must not change the behaviour of the
        // thing it is diagnosing.
        //
        // Polling a structural hash also avoids the other trap: LayoutUpdated fires
        // continuously through any animation, and DOM.documentUpdated makes the
        // frontend re-fetch and collapse the whole tree. Hashing shape rather than
        // geometry means an animating app does not fight the panel.
        //
        private void StartStructurePoll()
        {
            if (_structurePoll != null)
                return;

            _structureHash = ComputeStructureHash();

            // A plain timer, not a DispatcherTimer: on a WinForms head nothing pumps the
            // WPF dispatcher queue, so a DispatcherTimer would never tick and the tree
            // would silently stop reporting changes. The callback marshals itself.
            _structurePoll = new Timer(_ => OnStructurePoll(), null, PollIntervalMs, PollIntervalMs);
        }

        private void StopStructurePoll()
        {
            if (_structurePoll == null)
                return;

            _structurePoll.Dispose();
            _structurePoll = null;
        }

        private void OnStructurePoll()
        {
            try
            {
                int hash = _session.Ui.Invoke(ComputeStructureHash);
                if (hash == _structureHash)
                    return;

                _structureHash = hash;
                _session.SendEvent("DOM.documentUpdated", null);
            }
            catch
            {
                // A UI thread that is busy or gone is not worth a message; the next
                // tick will either succeed or the session will be torn down.
            }
        }

        private int ComputeStructureHash()
        {
            var hash = new HashCode();
            int budget = StructureHashNodeCap;

            foreach (object root in _session.Roots())
                Walk(root, ref hash, ref budget);

            return hash.ToHashCode();

            static void Walk(object node, ref HashCode hash, ref int budget)
            {
                if (budget-- <= 0)
                    return;

                List<object> children = VisualTreeModel.Children(node);
                hash.Add(node.GetType().Name.Length);
                hash.Add(children.Count);

                foreach (object child in children)
                    Walk(child, ref hash, ref budget);
            }
        }
    }
}
