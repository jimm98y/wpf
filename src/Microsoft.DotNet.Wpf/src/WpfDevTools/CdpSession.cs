// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// One attached frontend: JSON-RPC dispatch, and the thread hand-off.
//
// Commands arrive on the connection's thread and every one of them reads live
// WPF objects, so each is run through Dispatcher.Invoke. That also serialises
// them, which is what we want -- two commands walking the tree concurrently
// would be reading a tree that the other might be mid-way through changing.
//
// UNKNOWN METHODS RETURN AN EMPTY RESULT, not an error. A DevTools frontend
// probes dozens of domains the moment it attaches (Emulation, Network, Log,
// Audits, ...) and treats an error where it expected a result as a reason to
// give up on the panel. Answering {} to everything we do not implement is the
// difference between a working Elements panel and a blank one.
//

using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Windows.Threading;
using Microsoft.Wpf.DevTools.Json;

namespace Microsoft.Wpf.DevTools
{
    /// <summary>A group of CDP methods. TryHandle always runs on the dispatcher thread.</summary>
    internal interface ICdpDomain
    {
        /// <summary>
        /// Write the members of the result object and return true, or return false to
        /// let the next domain try.
        /// </summary>
        bool TryHandle(string method, JsonElement parameters, Utf8JsonWriter result);
    }

    internal sealed class CdpSession
    {
        /// <summary>
        /// How long a command may wait for the UI thread. The macOS dispatcher wakes
        /// roughly every 8ms even at idle, so exceeding this means the app is wedged
        /// or in a modal loop -- and a frontend that gets a timeout error keeps working,
        /// whereas one whose socket thread is blocked forever does not.
        /// </summary>
        private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(5);

        /// <summary>
        /// WPF_DEVTOOLS_TRACE=1 logs every command in and every reply out. The DevTools
        /// frontend is not a stable API and each build probes a different set of domains
        /// on attach, so when a panel comes up empty the only thing that actually
        /// answers "why" is the transcript of what it asked for.
        /// </summary>
        private static readonly bool s_trace =
            Environment.GetEnvironmentVariable("WPF_DEVTOOLS_TRACE") is string t &&
            t.Length > 0 && t != "0";

        private readonly ICdpConnection _connection;
        private readonly UiMarshaller _ui;
        private readonly List<ICdpDomain> _domains = new List<ICdpDomain>();
        private readonly List<Action> _afterResponse = new List<Action>();

        /// <summary>Whether the last command found a domain. Trace only; commands are serialised.</summary>
        private bool _lastHandled;

        internal CdpSession(ICdpConnection connection, Dispatcher dispatcher, VisualTreeModel model)
        {
            _connection = connection;
            _ui = new UiMarshaller(dispatcher, CommandTimeout);
            Model = model;
            Kind = connection.TargetId == WebSocketTransport.CompositionTargetId
                ? TargetKind.Composition
                : TargetKind.VisualTree;

            Dom = new Domains.DomDomain(this);
            _domains.Add(Dom);
            _domains.Add(new Domains.CssDomain(this));
            Overlay = new Domains.OverlayDomain(this);
            _domains.Add(Overlay);
            _domains.Add(new Domains.PageDomain(this));
            _domains.Add(new Domains.InputDomain(this));
            _domains.Add(new Domains.RuntimeDomain(this));

            connection.MessageReceived += OnMessage;
            connection.Closed += OnClosed;
        }

        internal VisualTreeModel Model { get; }

        /// <summary>Which document this session is showing. See TargetKind.</summary>
        internal TargetKind Kind { get; }

        /// <summary>The roots of this session's document.</summary>
        internal List<object> Roots() => VisualTreeModel.RootsFor(Kind);

        internal Domains.DomDomain Dom { get; }

        internal Domains.OverlayDomain Overlay { get; }

        /// <summary>
        /// Runs work on whichever thread owns the UI here -- the WPF dispatcher, or a
        /// WinForms form when nothing is pumping it. See UiMarshaller.
        /// </summary>
        internal UiMarshaller Ui => _ui;

        internal Dispatcher Dispatcher => _ui.Dispatcher;

        /// <summary>Send a protocol event. Safe from any thread.</summary>
        internal void SendEvent(string method, Action<Utf8JsonWriter>? writeParams)
        {
            string message = CdpJson.Event(method, null, writeParams);
            if (s_trace)
                DevToolsServer.Log($"<== {method} {message.Length} bytes");
            _connection.Send(message);
        }

        /// <summary>The interesting parameters of a command, short enough for one log line.</summary>
        private static string Summarize(JsonElement parameters)
        {
            if (parameters.ValueKind != JsonValueKind.Object)
                return string.Empty;

            var parts = new List<string>();
            foreach (JsonProperty property in parameters.EnumerateObject())
            {
                if (parts.Count == 4)
                {
                    parts.Add("...");
                    break;
                }

                string value = property.Value.ValueKind switch
                {
                    JsonValueKind.Object or JsonValueKind.Array => property.Value.ValueKind.ToString().ToLowerInvariant(),
                    _ => property.Value.ToString(),
                };

                if (value.Length > 40)
                    value = string.Concat(value.AsSpan(0, 40), "...");

                parts.Add(property.Name + "=" + value);
            }

            return parts.Count == 0 ? string.Empty : "{" + string.Join(", ", parts) + "}";
        }

        /// <summary>
        /// Run something once the current command's response is on the wire. Used for
        /// events that a frontend expects to follow their trigger rather than precede
        /// it, such as Runtime.executionContextCreated after Runtime.enable.
        /// </summary>
        internal void AfterResponse(Action action) => _afterResponse.Add(action);

        private void OnClosed()
        {
            foreach (ICdpDomain domain in _domains)
            {
                if (domain is IDisposable disposable)
                {
                    try { _ui.Invoke(disposable.Dispose); }
                    catch { }
                }
            }
        }

        private void OnMessage(string json)
        {
            int id = 0;
            try
            {
                using JsonDocument document = JsonDocument.Parse(json);
                JsonElement root = document.RootElement;

                id = CdpJson.GetInt(root, "id", -1);
                string? method = CdpJson.GetString(root, "method");

                if (id < 0 || string.IsNullOrEmpty(method))
                {
                    _connection.Send(CdpJson.Error(id, null, CdpJson.ErrorInvalidRequest, "expected id and method"));
                    return;
                }

                JsonElement parameters = CdpJson.GetObject(root, "params");

                // Synchronous, so `document` outlives every JsonElement the handlers
                // read out of it. Handlers must not retain them past the call.
                if (s_trace)
                    DevToolsServer.Log($"--> #{id} {method} {Summarize(parameters)}");

                string response = InvokeOnDispatcher(id, method!, parameters);
                _connection.Send(response);

                if (s_trace)
                {
                    DevToolsServer.Log($"<-- #{id} {response.Length} bytes"
                                       + (_lastHandled ? string.Empty : "  [not implemented, answered {}]")
                                       + (response.Contains("\"error\"", StringComparison.Ordinal) ? "  ERROR: " + response : string.Empty));
                }

                if (_afterResponse.Count > 0)
                {
                    Action[] pending = _afterResponse.ToArray();
                    _afterResponse.Clear();
                    foreach (Action action in pending)
                    {
                        try { action(); } catch { }
                    }
                }
            }
            catch (JsonException ex)
            {
                _connection.Send(CdpJson.Error(id, null, CdpJson.ErrorInvalidRequest, "malformed JSON: " + ex.Message));
            }
            catch (Exception ex)
            {
                _connection.Send(CdpJson.Error(id, null, CdpJson.ErrorInternal, ex.GetType().Name + ": " + ex.Message));
            }
        }

        private string InvokeOnDispatcher(int id, string method, JsonElement parameters)
        {
            try
            {
                return _ui.Invoke(() => Handle(id, method, parameters));
            }
            catch (TimeoutException)
            {
                return CdpJson.Error(id, null, CdpJson.ErrorServer,
                    $"'{method}' timed out waiting for the UI thread ({CommandTimeout.TotalSeconds}s)");
            }
        }

        private string Handle(int id, string method, JsonElement parameters)
        {
            try
            {
                return CdpJson.Response(id, null, w =>
                {
                    foreach (ICdpDomain domain in _domains)
                    {
                        if (domain.TryHandle(method, parameters, w))
                        {
                            _lastHandled = true;
                            return;
                        }
                    }

                    // Not implemented. See the file header: {} on purpose.
                    _lastHandled = false;
                });
            }
            catch (Exception ex)
            {
                return CdpJson.Error(id, null, CdpJson.ErrorServer, $"'{method}' failed: {ex.GetType().Name}: {ex.Message}");
            }
        }
    }
}
