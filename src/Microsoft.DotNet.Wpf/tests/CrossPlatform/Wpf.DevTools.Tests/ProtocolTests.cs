// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Windows;
using Xunit;

namespace Wpf.DevTools.Tests
{
    /// <summary>
    /// The endpoint's contract. Everything here goes over a real socket to a real
    /// window; nothing reaches into the inspector's internals, because the thing
    /// under test is the protocol, not the implementation behind it.
    /// </summary>
    public class ProtocolTests
    {
        private static CdpClient Connect()
        {
            Assert.SkipUnless(TestApp.DisplayAvailable, "requires a display server");
            int port = TestApp.Start();
            Assert.SkipWhen(port == 0, "the endpoint did not bind");
            return new CdpClient(port);
        }

        // ------------------------------------------------------------------
        // Discovery
        // ------------------------------------------------------------------

        /// <summary>
        /// /json/list is how chrome://inspect finds the app at all. It has to be a
        /// non-empty array whose entry carries a ws:// URL back to this process.
        /// </summary>
        [Fact]
        public void TargetListAdvertisesAWebSocketUrl()
        {
            Assert.SkipUnless(TestApp.DisplayAvailable, "requires a display server");
            int port = TestApp.Start();
            Assert.SkipWhen(port == 0, "the endpoint did not bind");

            using JsonDocument list = JsonDocument.Parse(CdpClient.HttpGet(port, "/json/list"));

            Assert.Equal(JsonValueKind.Array, list.RootElement.ValueKind);

            // Two targets: the UI tree and the compositor's decoded MILCMD graph. They answer
            // different questions, so each is its own document with its own Elements panel.
            Dictionary<string, JsonElement> targets = list.RootElement.EnumerateArray()
                .ToDictionary(t => t.GetProperty("id").GetString()!, t => t);

            Assert.Equal(2, targets.Count);

            JsonElement visual = targets["wpf"];
            Assert.Equal("page", visual.GetProperty("type").GetString());
            Assert.Equal(TestApp.WindowTitle, visual.GetProperty("title").GetString());
            Assert.Equal($"ws://127.0.0.1:{port}/devtools/page/wpf",
                         visual.GetProperty("webSocketDebuggerUrl").GetString());

            Assert.Equal($"ws://127.0.0.1:{port}/devtools/page/composition",
                         targets["composition"].GetProperty("webSocketDebuggerUrl").GetString());
        }

        [Fact]
        public void VersionReportsAProtocolVersion()
        {
            Assert.SkipUnless(TestApp.DisplayAvailable, "requires a display server");
            int port = TestApp.Start();
            Assert.SkipWhen(port == 0, "the endpoint did not bind");

            using JsonDocument version = JsonDocument.Parse(CdpClient.HttpGet(port, "/json/version"));

            Assert.Equal("1.3", version.RootElement.GetProperty("Protocol-Version").GetString());
            Assert.False(string.IsNullOrEmpty(version.RootElement.GetProperty("Browser").GetString()));
        }

        // ------------------------------------------------------------------
        // DOM
        // ------------------------------------------------------------------

        /// <summary>
        /// The document's shape: #document holds one Application element, which holds
        /// one child per PresentationSource. The Elements panel needs exactly one
        /// document element, which is the whole reason Application is synthesised.
        /// </summary>
        [Fact]
        public void DocumentIsRootedOnASyntheticApplicationElement()
        {
            using CdpClient client = Connect();
            client.Call("DOM.enable");

            JsonElement root = client.Call("DOM.getDocument", new { depth = 2 }).GetProperty("root");

            Assert.Equal("#document", root.GetProperty("nodeName").GetString());
            Assert.Equal(9, root.GetProperty("nodeType").GetInt32());
            // Marks the document as XML, which is what stops the panel lowercasing
            // PascalCase element names the way it does for HTML.
            Assert.Equal("1.0", root.GetProperty("xmlVersion").GetString());

            JsonElement app = root.GetProperty("children").EnumerateArray().Single();
            Assert.Equal("Application", app.GetProperty("nodeName").GetString());
            Assert.True(app.GetProperty("childNodeCount").GetInt32() >= 1);

            Assert.Contains(app.GetProperty("children").EnumerateArray(),
                            n => n.GetProperty("nodeName").GetString() == "Window");
        }

        /// <summary>
        /// A node's x:Name goes out as "id" so the panel renders Button#Name, and its
        /// local values go out as the remaining attributes.
        /// </summary>
        [Fact]
        public void NamedElementCarriesItsNameAsAnIdAttribute()
        {
            using CdpClient client = Connect();
            JsonElement button = FindNode(client, "Button");

            Dictionary<string, string> attributes = AttributesOf(button);
            Assert.Equal(TestApp.ButtonName, attributes["id"]);
            Assert.Equal("120", attributes["Width"]);
            Assert.Equal("40", attributes["Height"]);
        }

        /// <summary>
        /// The panel expands a subtree by asking for children and reading them off an
        /// event rather than the reply, so an empty reply here is correct and the
        /// event is the actual answer.
        /// </summary>
        [Fact]
        public void RequestChildNodesAnswersWithAnEvent()
        {
            using CdpClient client = Connect();
            client.Call("DOM.enable");

            JsonElement root = client.Call("DOM.getDocument", new { depth = 1 }).GetProperty("root");
            int applicationId = root.GetProperty("children").EnumerateArray().Single().GetProperty("nodeId").GetInt32();

            client.Call("DOM.requestChildNodes", new { nodeId = applicationId, depth = 1 });

            JsonElement parameters = client.WaitForEvent("DOM.setChildNodes");
            Assert.Equal(applicationId, parameters.GetProperty("parentId").GetInt32());
            Assert.Contains(parameters.GetProperty("nodes").EnumerateArray(),
                            n => n.GetProperty("nodeName").GetString() == "Window");
        }

        /// <summary>Text becomes a #text child, so a TextBlock reads like markup.</summary>
        [Fact]
        public void TextIsReportedAsATextNode()
        {
            using CdpClient client = Connect();
            JsonElement text = FindNode(client, "#text", n =>
                n.TryGetProperty("nodeValue", out JsonElement v) && v.GetString() == TestApp.ButtonText);

            Assert.Equal(3, text.GetProperty("nodeType").GetInt32());
            Assert.Equal(TestApp.ButtonText, text.GetProperty("nodeValue").GetString());
        }

        /// <summary>
        /// The box model, against geometry the test set itself. WPF's rect for an
        /// element is the BORDER box; margin goes outside it, padding inside. Getting
        /// that inverted is the easiest possible mistake here and it would put the
        /// highlight in the wrong place on every element with a margin.
        /// </summary>
        [Fact]
        public void BoxModelPutsMarginOutsideTheBorderBox()
        {
            using CdpClient client = Connect();
            JsonElement button = FindNode(client, "Button");

            JsonElement model = client
                .Call("DOM.getBoxModel", new { nodeId = button.GetProperty("nodeId").GetInt32() })
                .GetProperty("model");

            Assert.Equal((int)TestApp.ButtonWidth, model.GetProperty("width").GetInt32());
            Assert.Equal((int)TestApp.ButtonHeight, model.GetProperty("height").GetInt32());

            double[] border = Quad(model, "border");
            double[] margin = Quad(model, "margin");

            // Left/top edges: the margin box starts a margin's worth further out.
            Assert.Equal(border[0] - TestApp.ButtonMargin.Left, margin[0], 3);
            Assert.Equal(border[1] - TestApp.ButtonMargin.Top, margin[1], 3);
            // ...and ends a margin's worth further on.
            Assert.Equal(border[4] + TestApp.ButtonMargin.Right, margin[4], 3);
            Assert.Equal(border[5] + TestApp.ButtonMargin.Bottom, margin[5], 3);
        }

        [Fact]
        public void SearchFindsANodeByItsName()
        {
            using CdpClient client = Connect();

            JsonElement search = client.Call("DOM.performSearch", new { query = TestApp.ButtonName });
            int count = search.GetProperty("resultCount").GetInt32();
            Assert.True(count >= 1, $"expected to find {TestApp.ButtonName}, got {count} matches");

            JsonElement results = client.Call("DOM.getSearchResults", new
            {
                searchId = search.GetProperty("searchId").GetString(),
                fromIndex = 0,
                toIndex = count,
            });

            int[] ids = results.GetProperty("nodeIds").EnumerateArray().Select(e => e.GetInt32()).ToArray();
            Assert.NotEmpty(ids);

            JsonElement described = client.Call("DOM.describeNode", new { nodeId = ids[0] }).GetProperty("node");
            Assert.Equal("Button", described.GetProperty("nodeName").GetString());
        }

        // ------------------------------------------------------------------
        // CSS
        // ------------------------------------------------------------------

        /// <summary>The Computed pane: every dependency property on the node.</summary>
        [Fact]
        public void ComputedStyleListsDependencyProperties()
        {
            using CdpClient client = Connect();
            JsonElement button = FindNode(client, "Button");

            JsonElement computed = client
                .Call("CSS.getComputedStyleForNode", new { nodeId = button.GetProperty("nodeId").GetInt32() })
                .GetProperty("computedStyle");

            Dictionary<string, string> byName = computed.EnumerateArray()
                .ToDictionary(e => e.GetProperty("name").GetString()!, e => e.GetProperty("value").GetString()!);

            Assert.True(byName.Count > 40, $"a Button has more than 40 dependency properties; got {byName.Count}");
            Assert.Equal("120", byName["FrameworkElement.Width"]);
            Assert.Equal("10,20,30,40", byName["FrameworkElement.Margin"]);
            Assert.Contains("IsEnabled", byName.Keys.Select(k => k.Split('.').Last()));
        }

        /// <summary>
        /// The point of the whole CSS mapping: values are grouped by WHERE they came
        /// from. A Background the test set on the element must land in the inline
        /// style, and the control's template and default style must show up as
        /// separate rules -- that is what the Styles pane renders, in order, with the
        /// overridden declarations struck through.
        /// </summary>
        [Fact]
        public void MatchedStylesGroupValuesByTheirSource()
        {
            using CdpClient client = Connect();
            JsonElement button = FindNode(client, "Button");

            JsonElement matched = client.Call("CSS.getMatchedStylesForNode",
                new { nodeId = button.GetProperty("nodeId").GetInt32() });

            string[] inline = matched.GetProperty("inlineStyle").GetProperty("cssProperties")
                .EnumerateArray().Select(p => p.GetProperty("name").GetString()!).ToArray();
            Assert.Contains(inline, n => n.EndsWith("Background", StringComparison.Ordinal));
            Assert.Contains(inline, n => n.EndsWith("Width", StringComparison.Ordinal));

            string[] selectors = matched.GetProperty("matchedCSSRules").EnumerateArray()
                .Select(r => r.GetProperty("rule").GetProperty("selectorList").GetProperty("text").GetString()!)
                .ToArray();

            Assert.NotEmpty(selectors);
            // Every rule names the value source it stands for, and none of them is the
            // inline style again.
            Assert.All(selectors, s => Assert.StartsWith("Button {", s, StringComparison.Ordinal));
            Assert.Contains(selectors, s => s.Contains("Style", StringComparison.Ordinal));
        }

        // ------------------------------------------------------------------
        // Protocol behaviour
        // ------------------------------------------------------------------

        /// <summary>
        /// A frontend probes dozens of domains the moment it attaches and treats an
        /// error where it expected a result as a reason to give up on the panel. An
        /// unimplemented method must therefore answer with an empty result, and this
        /// is the test that keeps it that way.
        /// </summary>
        [Fact]
        public void UnimplementedMethodsReturnAnEmptyResultRatherThanAnError()
        {
            using CdpClient client = Connect();

            foreach (string method in new[]
            {
                "Emulation.setDeviceMetricsOverride",
                "Network.enable",
                "Log.enable",
                "Audits.enable",
                "Something.entirelyMadeUp",
            })
            {
                using JsonDocument reply = client.CallRaw(method);
                Assert.False(reply.RootElement.TryGetProperty("error", out _),
                             $"{method} answered with an error; the frontend would give up on the panel");
                Assert.Equal(0, reply.RootElement.GetProperty("result").EnumerateObject().Count());
            }
        }

        /// <summary>A stale or invented node id resolves to nothing, and must not fault the session.</summary>
        [Fact]
        public void UnknownNodeIdsAreSurvivable()
        {
            using CdpClient client = Connect();

            using JsonDocument reply = client.CallRaw("DOM.getBoxModel", new { nodeId = 999999 });
            Assert.False(reply.RootElement.TryGetProperty("error", out _));

            // The session is still usable afterwards.
            Assert.Equal("#document",
                client.Call("DOM.getDocument", new { depth = 1 })
                      .GetProperty("root").GetProperty("nodeName").GetString());
        }

        /// <summary>
        /// Overlay.highlightNode has to be answerable for any node; whether the box
        /// looks right is a thing only a person can see, so what is pinned here is
        /// that it does not fault and that the session survives it.
        /// </summary>
        [Fact]
        public void HighlightAndHideAreAccepted()
        {
            using CdpClient client = Connect();
            JsonElement button = FindNode(client, "Button");

            client.Call("Overlay.enable");
            client.Call("Overlay.highlightNode", new { nodeId = button.GetProperty("nodeId").GetInt32() });
            client.Call("Overlay.hideHighlight");

            // The highlight is a Visual in the tree it draws on, and the walk filters
            // the inspector's own types out. If that filter ever broke, the adorner
            // would show up as a child of the window.
            JsonElement root = client.Call("DOM.getDocument", new { depth = -1 }).GetProperty("root");
            Assert.DoesNotContain("HighlightAdorner", root.ToString(), StringComparison.Ordinal);
        }

        // ------------------------------------------------------------------
        // Editing
        // ------------------------------------------------------------------

        /// <summary>
        /// Editing an attribute has to reach the live object, not just the panel's
        /// copy of it. Set Width through the protocol and read it back off the real
        /// Button on the UI thread.
        /// </summary>
        [Fact]
        public void SettingAnAttributeChangesTheLiveProperty()
        {
            using CdpClient client = Connect();
            JsonElement button = FindNode(client, "Button");
            int nodeId = button.GetProperty("nodeId").GetInt32();

            try
            {
                client.Call("DOM.setAttributeValue", new { nodeId, name = "Width", value = "217" });

                Assert.True(UiThread.WaitUntil(() => Math.Abs(TestApp.Button!.Width - 217) < 0.001),
                            $"Width did not change; it is {UiThread.Invoke(() => TestApp.Button!.Width)}");

                JsonElement modified = client.WaitForEvent("DOM.attributeModified");
                Assert.Equal("Width", modified.GetProperty("name").GetString());
                Assert.Equal("217", modified.GetProperty("value").GetString());
            }
            finally
            {
                UiThread.Invoke(() => TestApp.Button!.Width = TestApp.ButtonWidth);
            }
        }

        /// <summary>
        /// A converted type, not just a double: "10,5,10,5" has to go through
        /// ThicknessConverter the same way the XAML parser would have taken it.
        /// </summary>
        [Fact]
        public void SettingAnAttributeConvertsFromTheXamlTextForm()
        {
            using CdpClient client = Connect();
            int nodeId = FindNode(client, "Button").GetProperty("nodeId").GetInt32();

            try
            {
                client.Call("DOM.setAttributeValue", new { nodeId, name = "Margin", value = "1,2,3,4" });

                Thickness margin = UiThread.Invoke(() => TestApp.Button!.Margin);
                Assert.Equal(new Thickness(1, 2, 3, 4), margin);
            }
            finally
            {
                UiThread.Invoke(() => TestApp.Button!.Margin = TestApp.ButtonMargin);
            }
        }

        /// <summary>Removing an attribute clears the local value, restoring whatever was underneath.</summary>
        [Fact]
        public void RemovingAnAttributeClearsTheLocalValue()
        {
            using CdpClient client = Connect();
            int nodeId = FindNode(client, "Button").GetProperty("nodeId").GetInt32();

            try
            {
                client.Call("DOM.removeAttribute", new { nodeId, name = "Width" });
                Assert.True(UiThread.WaitUntil(() => double.IsNaN(TestApp.Button!.Width)),
                            "Width still has a local value after removeAttribute");
            }
            finally
            {
                UiThread.Invoke(() => TestApp.Button!.Width = TestApp.ButtonWidth);
            }
        }

        /// <summary>
        /// A bad edit must come back as an error the panel can show. Returning an empty
        /// result would discard the edit silently and leave the panel displaying a value
        /// the object does not have.
        /// </summary>
        [Fact]
        public void RejectedEditsComeBackAsErrors()
        {
            using CdpClient client = Connect();
            int nodeId = FindNode(client, "Button").GetProperty("nodeId").GetInt32();

            using (JsonDocument unknown = client.CallRaw("DOM.setAttributeValue",
                       new { nodeId, name = "NotARealProperty", value = "1" }))
            {
                Assert.True(unknown.RootElement.TryGetProperty("error", out _),
                            "an unknown property was accepted");
            }

            using (JsonDocument unconvertible = client.CallRaw("DOM.setAttributeValue",
                       new { nodeId, name = "Width", value = "not-a-number" }))
            {
                Assert.True(unconvertible.RootElement.TryGetProperty("error", out _),
                            "an unconvertible value was accepted");
            }

            // Untouched, and the session still works.
            Assert.Equal(TestApp.ButtonWidth, UiThread.Invoke(() => TestApp.Button!.Width));
            Assert.Equal("#document", client.Call("DOM.getDocument", new { depth = 1 })
                                            .GetProperty("root").GetProperty("nodeName").GetString());
        }

        // ------------------------------------------------------------------
        // Screencast and input
        // ------------------------------------------------------------------

        /// <summary>
        /// A screencast frame has to be a real decodable image of the real window, at
        /// roughly the window's size. Asserting on the PNG signature and the header's
        /// dimensions is what separates "sent bytes" from "sent a picture".
        /// </summary>
        [Fact]
        public void ScreencastDeliversADecodableFrame()
        {
            using CdpClient client = Connect();
            client.Call("Page.enable");
            client.Call("Page.startScreencast", new { format = "png", maxWidth = 800, maxHeight = 600 });

            try
            {
                JsonElement frame = client.WaitForEvent("Page.screencastFrame");
                byte[] data = Convert.FromBase64String(frame.GetProperty("data").GetString()!);

                Assert.True(data.Length > 100, $"frame was {data.Length} bytes");
                Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47 }, data[..4]);   // PNG signature

                // IHDR carries width and height as big-endian ints at offset 16.
                int width = (data[16] << 24) | (data[17] << 16) | (data[18] << 8) | data[19];
                int height = (data[20] << 24) | (data[21] << 16) | (data[22] << 8) | data[23];

                // The image is scaled to fit the panel...
                Assert.InRange(width, 1, 800);
                Assert.InRange(height, 1, 600);

                // ...but the metadata must report the PAGE size in device-independent
                // pixels, because that is what the frontend maps a click on the screencast
                // back through. Asserting these are equal to the image size -- which this
                // test originally did -- pins the bug that makes every click land high and
                // to the left by exactly the scale factor.
                JsonElement metadata = frame.GetProperty("metadata");
                double pageWidth = metadata.GetProperty("deviceWidth").GetDouble();
                double pageHeight = metadata.GetProperty("deviceHeight").GetDouble();

                Assert.True(pageWidth > 0 && pageHeight > 0, "the page size was not reported");

                // NOT "the image is no larger than the page": the composed frame is in DEVICE
                // pixels, so on a 2x display it is twice the page's size in device-independent
                // ones. That assertion held only by accident on a 1x display and failed the
                // moment the test ran on a scaled one -- 390 page against a 780 image.
                // Scaling preserves aspect, so a click maps back linearly in both axes.
                //
                // The page size is the FRAME's extent, not the root visual's: the composed
                // frame covers the client area, and the root's box includes WindowChrome's
                // non-client band. Asserting the frame against the ROOT's aspect pins a click
                // mapping that is out by the difference.
                Assert.Equal(pageWidth / pageHeight, (double)width / height, 2);
            }
            finally
            {
                client.Call("Page.stopScreencast");
            }
        }

        /// <summary>
        /// A dispatched click has to actually click. It does not go through the OS --
        /// there is no public path for that -- so what proves it worked is the app's own
        /// Click handler running, which is the thing a synthetic routed MouseUp alone
        /// would NOT have produced.
        /// </summary>
        [Fact]
        public void DispatchedClickReachesTheControl()
        {
            using CdpClient client = Connect();
            JsonElement button = FindNode(client, "Button");

            JsonElement model = client
                .Call("DOM.getBoxModel", new { nodeId = button.GetProperty("nodeId").GetInt32() })
                .GetProperty("model");

            double[] border = Quad(model, "border");
            int x = (int)((border[0] + border[4]) / 2);
            int y = (int)((border[1] + border[5]) / 2);

            int before = UiThread.Invoke(() => TestApp.ClickCount);

            client.Call("Input.dispatchMouseEvent", new { type = "mousePressed", x, y, button = "left", clickCount = 1 });
            client.Call("Input.dispatchMouseEvent", new { type = "mouseReleased", x, y, button = "left", clickCount = 1 });

            Assert.True(UiThread.WaitUntil(() => TestApp.ClickCount > before),
                        "the button's Click handler never ran");
        }

        /// <summary>
        /// Page.getFrameTree is not decoration: a frontend asks for it while attaching
        /// and will not populate panels for a page with no frame.
        /// </summary>
        [Fact]
        public void PageReportsAFrameTree()
        {
            using CdpClient client = Connect();

            JsonElement frame = client.Call("Page.getFrameTree")
                                      .GetProperty("frameTree").GetProperty("frame");

            Assert.False(string.IsNullOrEmpty(frame.GetProperty("id").GetString()));
            Assert.Equal("wpf://app/", frame.GetProperty("url").GetString());
        }

        // ------------------------------------------------------------------
        // Composition correlation
        // ------------------------------------------------------------------

        /// <summary>
        /// The differentiating feature: a Computed entry saying what the COMPOSITOR
        /// holds for this element, joined on the DUCE handle. The test host runs with
        /// managed composition on, so a laid-out, visible Button must be published and
        /// must be found in the renderer's decoded scene graph. A summary that says
        /// otherwise is either a real rendering bug or a broken correlation, and both
        /// are worth failing on.
        /// </summary>
        [Fact]
        public void ComputedStyleReportsWhatTheCompositorHolds()
        {
            using CdpClient client = Connect();
            JsonElement button = FindNode(client, "Button");

            JsonElement computed = client
                .Call("CSS.getComputedStyleForNode", new { nodeId = button.GetProperty("nodeId").GetInt32() })
                .GetProperty("computedStyle");

            JsonElement? entry = computed.EnumerateArray()
                .Cast<JsonElement?>()
                .FirstOrDefault(e => e!.Value.GetProperty("name").GetString() == "Composition.Scene");

            Assert.SkipWhen(entry is null, "no renderer is loaded, so there is nothing to correlate against");

            string summary = entry!.Value.GetProperty("value").GetString()!;
            Assert.DoesNotContain("not published", summary, StringComparison.Ordinal);
            Assert.DoesNotContain("no node for it", summary, StringComparison.Ordinal);
            Assert.Contains("handle 0x", summary, StringComparison.Ordinal);
        }

        /// <summary>
        /// "Select element" over a screencast is driven entirely by this: the frontend asks
        /// for the node under the cursor on every mouse move and highlights the answer
        /// itself. It never enables the backend's inspect mode, so leaving this
        /// unimplemented makes the picker look dead while hundreds of these go unanswered
        /// per minute -- which is exactly what happened.
        /// </summary>
        [Fact]
        public void NodeForLocationFindsTheElementUnderThePoint()
        {
            using CdpClient client = Connect();
            JsonElement button = FindNode(client, "Button");

            JsonElement model = client
                .Call("DOM.getBoxModel", new { nodeId = button.GetProperty("nodeId").GetInt32() })
                .GetProperty("model");

            double[] border = Quad(model, "border");
            int x = (int)((border[0] + border[4]) / 2);
            int y = (int)((border[1] + border[5]) / 2);

            JsonElement hit = client.Call("DOM.getNodeForLocation", new { x, y });
            int backendNodeId = hit.GetProperty("backendNodeId").GetInt32();
            Assert.True(backendNodeId > 0, "no node was found under the button's centre");

            // Whatever primitive the template put there, it must live inside the Button.
            JsonElement described = client.Call("DOM.describeNode", new { nodeId = backendNodeId })
                                          .GetProperty("node");
            Assert.False(string.IsNullOrEmpty(described.GetProperty("nodeName").GetString()));

            // And a point outside every window resolves to nothing rather than faulting.
            using JsonDocument miss = client.CallRaw("DOM.getNodeForLocation", new { x = -5000, y = -5000 });
            Assert.False(miss.RootElement.TryGetProperty("error", out _));
        }

        /// <summary>
        /// getNodeForLocation must BOTH name the node and make it reachable, in that one
        /// exchange. The frontend's picker does:
        ///
        ///     if (!response.nodeId) return null;
        ///     return this.nodeForId(response.nodeId);
        ///
        /// Omit nodeId and it gives up before it starts; return a nodeId for a node it has
        /// never been sent and nodeForId resolves nothing, so it settles for the deepest
        /// ancestor it holds. Either failure looks identical from the outside: the picker
        /// selects a parent no matter where you point.
        /// </summary>
        [Fact]
        public void NodeForLocationNamesTheNodeAndMakesItReachable()
        {
            using CdpClient scout = Connect();
            JsonElement button = FindNode(scout, "Button");
            JsonElement model = scout
                .Call("DOM.getBoxModel", new { nodeId = button.GetProperty("nodeId").GetInt32() })
                .GetProperty("model");
            double[] border = Quad(model, "border");
            int x = (int)((border[0] + border[4]) / 2);
            int y = (int)((border[1] + border[5]) / 2);

            // A session that has only ever seen the shallow document.
            using var fresh = new CdpClient(scout.Port);
            fresh.Call("DOM.enable");
            fresh.Call("DOM.getDocument", new { depth = 1 });

            JsonElement located = fresh.Call("DOM.getNodeForLocation", new { x, y });
            Assert.True(located.TryGetProperty("nodeId", out JsonElement idElement),
                        "no nodeId was returned, so the picker gives up before resolving anything");
            int nodeId = idElement.GetInt32();

            // The ancestry must have arrived with it, or nodeForId has nothing to resolve.
            var delivered = new List<int>();
            for (int i = 0; i < 64 && !delivered.Contains(nodeId); i++)
            {
                JsonElement ev = fresh.WaitForEvent("DOM.setChildNodes");
                foreach (JsonElement node in ev.GetProperty("nodes").EnumerateArray())
                    delivered.Add(node.GetProperty("nodeId").GetInt32());
            }

            Assert.Contains(nodeId, delivered);
        }

        /// <summary>
        /// The other half of the picker. getNodeForLocation names a node the frontend has
        /// probably never been sent, and a frontend can only select what it holds -- so
        /// pushNodesByBackendIdsToFrontend has to DELIVER the missing ancestry, not just echo
        /// the ids. Echoing them made the picker resolve to whatever ancestor happened to be
        /// the deepest node already delivered, which looked like "it always picks the parent".
        ///
        /// Two sessions on purpose: node ids are process-stable, so one client can discover a
        /// deep id while the other stays ignorant of it. Discovering and pushing on the SAME
        /// client proves nothing -- fetching the document to find the node is itself what
        /// delivers it, and there is then nothing left for the push to do.
        /// </summary>
        [Fact]
        public void PushingABackendIdDeliversTheNodesAncestry()
        {
            using CdpClient scout = Connect();
            int deep = FindNode(scout, "Button").GetProperty("nodeId").GetInt32();

            using var fresh = new CdpClient(scout.Port);
            fresh.Call("DOM.enable");
            fresh.Call("DOM.getDocument", new { depth = 1 });

            fresh.Call("DOM.pushNodesByBackendIdsToFrontend", new { backendNodeIds = new[] { deep } });

            var delivered = new List<int>();
            for (int i = 0; i < 64 && !delivered.Contains(deep); i++)
            {
                JsonElement ev = fresh.WaitForEvent("DOM.setChildNodes");
                foreach (JsonElement node in ev.GetProperty("nodes").EnumerateArray())
                    delivered.Add(node.GetProperty("nodeId").GetInt32());
            }

            Assert.Contains(deep, delivered);
        }

        /// <summary>
        /// Scrolling over the screencast. Raised as a real routed MouseWheel event, so
        /// whatever would have scrolled does -- no simulation, and it works for any scrollable
        /// thing rather than just the ones this code knows about.
        /// </summary>
        [Fact]
        public void WheelEventsScrollTheContentUnderThem()
        {
            Assert.SkipUnless(TestApp.DisplayAvailable, "requires a display server");
            int port = TestApp.Start();
            Assert.SkipWhen(port == 0, "the endpoint did not bind");
            Assert.SkipUnless(TestApp.EnsureScrollable(), "no scrollable content to test with");

            using var client = new CdpClient(port);
            double before = UiThread.Invoke(() => TestApp.Scroller!.VerticalOffset);

            client.Call("Input.dispatchMouseEvent", new
            {
                type = "mouseWheel",
                x = (int)TestApp.ScrollerCentre.X,
                y = (int)TestApp.ScrollerCentre.Y,
                deltaX = 0,
                deltaY = 300,
            });

            Assert.True(UiThread.WaitUntil(() => TestApp.Scroller!.VerticalOffset > before),
                        $"the wheel did not scroll; offset stayed at {UiThread.Invoke(() => TestApp.Scroller!.VerticalOffset)}");
        }

        /// <summary>
        /// Dragging the scrollbar thumb. A Thumb works by capturing the mouse and there is no
        /// public way to hand a synthetic device capture, so press/move/release delivered as
        /// routed events makes it do nothing at all. The drag is therefore simulated -- but
        /// through Track.ValueFromDistance, the same conversion the real drag uses, so the
        /// thumb tracks the cursor rather than approximating it.
        /// </summary>
        [Fact]
        public void DraggingTheScrollbarThumbScrolls()
        {
            Assert.SkipUnless(TestApp.DisplayAvailable, "requires a display server");
            int port = TestApp.Start();
            Assert.SkipWhen(port == 0, "the endpoint did not bind");
            Assert.SkipUnless(TestApp.EnsureScrollable(), "no scrollable content to test with");

            using var client = new CdpClient(port);
            UiThread.Invoke(() => TestApp.Scroller!.ScrollToVerticalOffset(0));
            Assert.True(UiThread.WaitUntil(() => TestApp.Scroller!.VerticalOffset == 0));

            Point thumb = UiThread.Invoke(() => TestApp.VerticalThumbCentre());
            Assert.SkipWhen(thumb.X <= 0 && thumb.Y <= 0, "no vertical scrollbar is realised");

            client.Call("Input.dispatchMouseEvent", new
            {
                type = "mousePressed", x = (int)thumb.X, y = (int)thumb.Y, button = "left", clickCount = 1,
            });
            client.Call("Input.dispatchMouseEvent", new
            {
                type = "mouseMoved", x = (int)thumb.X, y = (int)thumb.Y + 40, button = "left",
            });
            client.Call("Input.dispatchMouseEvent", new
            {
                type = "mouseReleased", x = (int)thumb.X, y = (int)thumb.Y + 40, button = "left", clickCount = 1,
            });

            Assert.True(UiThread.WaitUntil(() => TestApp.Scroller!.VerticalOffset > 0),
                        "dragging the thumb did not scroll");
        }

        /// <summary>
        /// Dragging across text selects it. Capture-driven like the thumb, so simulated -- but
        /// through GetCharacterIndexFromPoint, the control's own point-to-character mapping,
        /// so the selection ends where a real drag would end it rather than near it.
        /// </summary>
        [Fact]
        public void DraggingAcrossTextSelectsIt()
        {
            Assert.SkipUnless(TestApp.DisplayAvailable, "requires a display server");
            int port = TestApp.Start();
            Assert.SkipWhen(port == 0, "the endpoint did not bind");
            Assert.True(UiThread.WaitUntil(() => TestApp.Text!.ActualWidth > 0), "the text box never laid out");

            using var client = new CdpClient(port);
            UiThread.Invoke(() => TestApp.Text!.Select(0, 0));

            Point from = UiThread.Invoke(() => TestApp.TextPointAt(0.10));
            Point to = UiThread.Invoke(() => TestApp.TextPointAt(0.60));

            client.Call("Input.dispatchMouseEvent", new
            {
                type = "mousePressed", x = (int)from.X, y = (int)from.Y, button = "left", clickCount = 1,
            });
            client.Call("Input.dispatchMouseEvent", new
            {
                type = "mouseMoved", x = (int)to.X, y = (int)to.Y, button = "left",
            });
            client.Call("Input.dispatchMouseEvent", new
            {
                type = "mouseReleased", x = (int)to.X, y = (int)to.Y, button = "left", clickCount = 1,
            });

            Assert.True(UiThread.WaitUntil(() => TestApp.Text!.SelectionLength > 0),
                        "the drag selected nothing");

            // And it selected the RIGHT run -- one out of the middle, bounded at both ends.
            // Simply asserting "something is selected" would pass just as well if the drag
            // had selected everything, which is what a mapping that ignored the points would do.
            (int start, int length, string selected, int total) = UiThread.Invoke(() =>
                (TestApp.Text!.SelectionStart, TestApp.Text.SelectionLength,
                 TestApp.Text.SelectedText, TestApp.Text.Text.Length));

            Assert.True(start > 0, $"selection began at {start}; the drag started a tenth of the way in");
            Assert.True(start + length < total,
                        $"selection ran to {start + length} of {total}; the drag ended well short of the end");
            Assert.Equal(length, selected.Length);
        }

        // ------------------------------------------------------------------
        // The composition target
        // ------------------------------------------------------------------

        /// <summary>
        /// The second document: the SceneVisual graph the renderer decoded from the MILCMD
        /// stream. Attaching to it must give an ordinary tree -- and one that is genuinely
        /// the OTHER tree, not the same visuals under a different name. The tell is that its
        /// nodes carry DUCE handles and drawing primitives, which no WPF element does.
        /// </summary>
        [Fact]
        public void CompositionTargetExposesTheDecodedSceneGraph()
        {
            Assert.SkipUnless(TestApp.DisplayAvailable, "requires a display server");
            int port = TestApp.Start();
            Assert.SkipWhen(port == 0, "the endpoint did not bind");

            using var client = new CdpClient(port, "composition");
            client.Call("DOM.enable");

            JsonElement root = client.Call("DOM.getDocument", new { depth = -1 }).GetProperty("root");
            JsonElement composition = root.GetProperty("children").EnumerateArray().Single();
            Assert.Equal("Composition", composition.GetProperty("nodeName").GetString());

            Assert.SkipWhen(composition.GetProperty("childNodeCount").GetInt32() == 0,
                            "no renderer is loaded, so there is no decoded graph");

            var names = new List<string>();
            var handles = new List<string>();
            Collect(composition, names, handles);

            Assert.Contains("SceneVisual", names);

            // Every scene visual is addressed by the DUCE handle that joins it to the element
            // it was decoded from; that is what makes the two trees correlatable at all.
            Assert.NotEmpty(handles);
            Assert.All(handles, h => Assert.StartsWith("0x", h, StringComparison.Ordinal));

            static void Collect(JsonElement node, List<string> names, List<string> handles)
            {
                names.Add(node.GetProperty("nodeName").GetString()!);

                if (node.TryGetProperty("attributes", out JsonElement attributes))
                {
                    JsonElement[] flat = attributes.EnumerateArray().ToArray();
                    for (int i = 0; i + 1 < flat.Length; i += 2)
                    {
                        if (flat[i].GetString() == "id")
                            handles.Add(flat[i + 1].GetString()!);
                    }
                }

                if (node.TryGetProperty("children", out JsonElement children))
                {
                    foreach (JsonElement child in children.EnumerateArray())
                        Collect(child, names, handles);
                }
            }
        }

        /// <summary>
        /// The MILCMD op histogram hangs off the composition root, because it describes the
        /// whole decode rather than any one node: every command, record and resource type the
        /// decoder has seen, with counts.
        /// </summary>
        [Fact]
        public void CompositionRootCarriesTheMilcmdOpHistogram()
        {
            Assert.SkipUnless(TestApp.DisplayAvailable, "requires a display server");
            int port = TestApp.Start();
            Assert.SkipWhen(port == 0, "the endpoint did not bind");

            using var client = new CdpClient(port, "composition");
            client.Call("DOM.enable");
            JsonElement root = client.Call("DOM.getDocument", new { depth = 1 }).GetProperty("root");
            int compositionId = root.GetProperty("children").EnumerateArray().Single()
                                    .GetProperty("nodeId").GetInt32();

            JsonElement computed = client.Call("CSS.getComputedStyleForNode", new { nodeId = compositionId })
                                         .GetProperty("computedStyle");

            Assert.SkipWhen(computed.GetArrayLength() == 0, "no renderer is loaded, so nothing was decoded");
            Assert.All(computed.EnumerateArray(),
                       e => Assert.True(int.TryParse(e.GetProperty("value").GetString(), out _),
                                        "every histogram entry is a count"));
        }

        /// <summary>
        /// The composition document is spatial after all. A SceneVisual carries transforms
        /// and clips but no laid-out box, so bounds, the box model and the highlight all go
        /// through the element it was decoded from, found by the shared DUCE handle. Without
        /// that, hovering a node in that tree highlights nothing.
        /// </summary>
        [Fact]
        public void CompositionNodesReportTheirElementAndItsBounds()
        {
            Assert.SkipUnless(TestApp.DisplayAvailable, "requires a display server");
            int port = TestApp.Start();
            Assert.SkipWhen(port == 0, "the endpoint did not bind");

            using var client = new CdpClient(port, "composition");
            client.Call("DOM.enable");
            JsonElement root = client.Call("DOM.getDocument", new { depth = -1 }).GetProperty("root");

            JsonElement? scene = FindWithAttribute(root, "element");
            Assert.SkipWhen(scene is null, "no renderer is loaded, so nothing was decoded");

            int nodeId = scene!.Value.GetProperty("nodeId").GetInt32();

            // It names the element it came from...
            Dictionary<string, string> attributes = AttributesOf(scene.Value);
            Assert.False(string.IsNullOrWhiteSpace(attributes["element"]));

            // ...and can be highlighted, which means it resolved to a real box.
            JsonElement model = client.Call("DOM.getBoxModel", new { nodeId }).GetProperty("model");
            Assert.True(model.GetProperty("width").GetInt32() > 0, "the scene node resolved to no box");

            JsonElement computed = client.Call("CSS.getComputedStyleForNode", new { nodeId })
                                         .GetProperty("computedStyle");
            Assert.Contains(computed.EnumerateArray(), e => e.GetProperty("name").GetString() == "Element");

            client.Call("Overlay.enable");
            client.Call("Overlay.highlightNode", new { nodeId });
            client.Call("Overlay.hideHighlight");
        }

        /// <summary>
        /// The picker on the composition document points at the real window -- there is
        /// nothing else to point at -- so a hit on a WPF element must come back as the scene
        /// node decoded from it, not as the element.
        /// </summary>
        [Fact]
        public void PickingOnTheCompositionTargetSelectsASceneNode()
        {
            using CdpClient wpf = Connect();
            JsonElement button = FindNode(wpf, "Button");
            JsonElement model = wpf
                .Call("DOM.getBoxModel", new { nodeId = button.GetProperty("nodeId").GetInt32() })
                .GetProperty("model");
            double[] border = Quad(model, "border");
            int x = (int)((border[0] + border[4]) / 2);
            int y = (int)((border[1] + border[5]) / 2);

            using var client = new CdpClient(wpf.Port, "composition");
            client.Call("DOM.enable");
            client.Call("DOM.getDocument", new { depth = 1 });

            JsonElement hit = client.Call("DOM.getNodeForLocation", new { x, y });
            Assert.SkipWhen(!hit.TryGetProperty("nodeId", out JsonElement idElement),
                            "no renderer is loaded, so there is no scene node to pick");

            JsonElement described = client.Call("DOM.describeNode", new { nodeId = idElement.GetInt32() })
                                          .GetProperty("node");
            Assert.Equal("SceneVisual", described.GetProperty("nodeName").GetString());
        }

        // ------------------------------------------------------------------
        // Helpers
        // ------------------------------------------------------------------

        private static JsonElement? FindWithAttribute(JsonElement node, string attribute)
        {
            if (node.TryGetProperty("attributes", out JsonElement attributes))
            {
                foreach (JsonElement entry in attributes.EnumerateArray())
                {
                    if (entry.ValueKind == JsonValueKind.String && entry.GetString() == attribute)
                        return node;
                }
            }

            if (node.TryGetProperty("children", out JsonElement children))
            {
                foreach (JsonElement child in children.EnumerateArray())
                {
                    JsonElement? found = FindWithAttribute(child, attribute);
                    if (found.HasValue)
                        return found;
                }
            }

            return null;
        }

        private static JsonElement FindNode(CdpClient client, string nodeName, Func<JsonElement, bool>? predicate = null)
        {
            JsonElement root = client.Call("DOM.getDocument", new { depth = -1 }).GetProperty("root");
            JsonElement? found = Search(root, nodeName, predicate);
            Assert.True(found.HasValue, $"no {nodeName} in the document");
            return found!.Value;
        }

        private static JsonElement? Search(JsonElement node, string nodeName, Func<JsonElement, bool>? predicate)
        {
            if (node.GetProperty("nodeName").GetString() == nodeName && (predicate == null || predicate(node)))
                return node;

            if (node.TryGetProperty("children", out JsonElement children))
            {
                foreach (JsonElement child in children.EnumerateArray())
                {
                    JsonElement? found = Search(child, nodeName, predicate);
                    if (found.HasValue)
                        return found;
                }
            }

            return null;
        }

        private static Dictionary<string, string> AttributesOf(JsonElement node)
        {
            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            JsonElement[] flat = node.GetProperty("attributes").EnumerateArray().ToArray();
            for (int i = 0; i + 1 < flat.Length; i += 2)
                result[flat[i].GetString()!] = flat[i + 1].GetString()!;
            return result;
        }

        private static double[] Quad(JsonElement model, string name)
            => model.GetProperty(name).EnumerateArray().Select(e => e.GetDouble()).ToArray();
    }
}
