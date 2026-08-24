// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// How CDP messages get in and out.
//
// This seam exists because of the browser head. Every other head listens on a
// TCP socket, which WASM has no way to do; there the page opens a WebSocket
// outward through the JS bridge that browser-window.js already maintains.
// Keeping the protocol layer behind an interface means that difference costs
// one class rather than a second implementation of the domains.
//

using System;

namespace Microsoft.Wpf.DevTools
{
    /// <summary>Accepts frontend connections. Implementations run off the UI thread.</summary>
    internal interface ICdpTransport : IDisposable
    {
        /// <summary>
        /// Raised on a background thread when a frontend attaches. Handlers must not
        /// assume the UI thread.
        /// </summary>
        event Action<ICdpConnection>? ConnectionAccepted;

        void Start();
    }

    /// <summary>One attached frontend.</summary>
    internal interface ICdpConnection
    {
        /// <summary>
        /// Which advertised target this frontend asked for, taken from the WebSocket path.
        /// The endpoint offers one per document -- the UI tree and the compositor's graph --
        /// and the session builds its model from this.
        /// </summary>
        string TargetId { get; }

        /// <summary>A complete JSON message from the frontend, raised on a background thread.</summary>
        event Action<string>? MessageReceived;

        event Action? Closed;

        /// <summary>Send one JSON message. Safe to call from any thread.</summary>
        void Send(string json);

        void Close();
    }
}
