// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// The browser head's transport: a JS-callable message port instead of a socket.
//
// WASM has no TcpListener, and more fundamentally a page cannot ACCEPT a
// connection -- a DevTools frontend dials a ws:// URL, and nothing in a browser
// tab can be dialled. Every CDP-in-the-browser arrangement therefore needs a
// relay: the page dials out, the frontend dials in, and something in the middle
// pairs them.
//
// So this does not pretend to be a socket. It exposes the protocol as a pair of
// functions on the JS side:
//
//     globalThis.__wpfDevTools.send(json)   -> deliver a command to the inspector
//     wpfDevTools.receive(json)             -> the inspector's replies and events
//
// which is enough to drive the whole protocol from the browser console, from a
// Playwright script, or from a fifteen-line relay that forwards both directions
// to a WebSocket. That last one is the piece deliberately NOT shipped here: it
// is deployment, it differs per setup, and inventing one would add a moving part
// to a diagnostic whose value is that it has none.
//
// This is verified now: the browser head serves a 599-node document and a working screencast,
// driven either from the console or through eng/devtools-relay.py, which is the relay described
// above and which IS shipped -- driving the port by hand turned out to be no substitute for an
// Elements panel the moment anyone wanted to walk the tree.
//

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices.JavaScript;
using System.Runtime.Versioning;

namespace Microsoft.Wpf.DevTools
{
    [SupportedOSPlatform("browser")]
    internal sealed partial class BrowserTransport : ICdpTransport, ICdpConnection
    {
        /// <summary>The module the host page registers, mirroring 'wpfBrowserWindow'.</summary>
        private const string Module = "wpfDevTools";

        private static BrowserTransport? s_instance;

        public event Action<ICdpConnection>? ConnectionAccepted;

        /// <summary>The browser bridge is a single port, so it serves the UI tree.</summary>
        public string TargetId => WebSocketTransport.VisualTreeTargetId;
        public event Action<string>? MessageReceived;
        public event Action? Closed;

        public void Start()
        {
            s_instance = this;

            try
            {
                Js.Ready();
            }
            catch (Exception ex)
            {
                // The host page did not register the module. That is a deployment
                // detail, not a crash: the app keeps running without an inspector.
                DevToolsServer.Log($"browser bridge unavailable: {ex.GetType().Name}: {ex.Message}");
                return;
            }

            // There is one implicit "connection" -- the page itself -- and it is open
            // from the moment the bridge is up.
            ConnectionAccepted?.Invoke(this);
        }

        public void Send(string json)
        {
            try
            {
                Js.Receive(json);
            }
            catch (Exception ex)
            {
                DevToolsServer.Log($"browser send failed: {ex.GetType().Name}: {ex.Message}");
            }
        }

        public void Close() => Closed?.Invoke();

        public void Dispose()
        {
            s_instance = null;
            Closed?.Invoke();
        }

        /// <summary>
        /// Called from JS with one CDP command. Runs on the dispatcher thread by
        /// construction -- the browser head is single-threaded and JS calls in on the
        /// same thread the pump runs on -- so the session's Dispatcher.Invoke is a
        /// pass-through rather than a marshal.
        /// </summary>
        [JSExport]
        internal static void Dispatch(string json)
        {
            BrowserTransport? transport = s_instance;
            if (transport == null)
                return;

            try
            {
                transport.MessageReceived?.Invoke(json);
            }
            catch (Exception ex)
            {
                DevToolsServer.Log($"browser dispatch failed: {ex.GetType().Name}: {ex.Message}");
            }
        }

        /// <summary>
        /// One frame of the app as an encoded PNG, read off the page's canvas.
        ///
        /// The screencast's usual sources both need a synchronous GPU readback, which a browser
        /// does not offer, so on this head they yield a blank frame. The canvas already holds the
        /// composed image, hosted content included, so it is both the only source available here
        /// and the most faithful one.
        /// </summary>
        internal static bool TryCaptureCanvas(int maxWidth, int maxHeight, string format, int quality, out byte[] png)
        {
            png = Array.Empty<byte>();

            try
            {
                string? base64 = Js.CaptureCanvas(maxWidth, maxHeight, format, quality);
                if (string.IsNullOrEmpty(base64))
                    return false;

                png = Convert.FromBase64String(base64);
                return png.Length > 0;
            }
            catch (Exception ex)
            {
                DevToolsServer.Log($"browser capture failed: {ex.GetType().Name}: {ex.Message}");
                return false;
            }
        }

        internal static partial class Js
        {
            /// <summary>Tells the page the inspector is up and its exports are callable.</summary>
            [JSImport("ready", Module)]
            internal static partial void Ready();

            /// <summary>One reply or event, as JSON.</summary>
            [JSImport("receive", Module)]
            internal static partial void Receive(string json);

            /// <summary>The canvas contents, encoded and scaled to fit, or null.</summary>
            [JSImport("captureCanvas", Module)]
            internal static partial string? CaptureCanvas(int maxWidth, int maxHeight, string format, int quality);
        }
    }
}
