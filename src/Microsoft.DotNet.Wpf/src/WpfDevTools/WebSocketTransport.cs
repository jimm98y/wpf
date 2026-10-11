// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// The socket transport: HTTP target discovery plus a hand-rolled RFC 6455 endpoint.
//
// Hand-rolled because there is no server-side WebSocket in the box that works
// here. HttpListener's AcceptWebSocketAsync is Windows-only (it is a thin cover
// over the HTTP.SYS implementation) and throws PlatformNotSupportedException on
// macOS and Linux, which is every head this inspector exists for. The framing
// is about two hundred lines and has no dependencies, so it also carries to the
// mobile heads unchanged.
//
// ALWAYS BOUND TO LOOPBACK. The endpoint hands out the full contents of a
// running application's UI, and later phases let a client change it. It is opt
// in through an environment variable and it must not be reachable from off the
// machine; there is no auth here to make that safe.
//

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace Microsoft.Wpf.DevTools
{
    internal sealed class WebSocketTransport : ICdpTransport
    {
        /// <summary>The RFC 6455 handshake GUID.</summary>
        private const string WebSocketGuid = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11";

        /// <summary>The UI tree: WPF visuals, and WinForms controls where there are any.</summary>
        internal const string VisualTreeTargetId = "wpf";

        /// <summary>The renderer's decoded MILCMD scene graph.</summary>
        internal const string CompositionTargetId = "composition";

        private readonly int _port;
        private readonly Func<string> _titleProvider;
        private TcpListener? _listener;
        private volatile bool _stopped;

        public event Action<ICdpConnection>? ConnectionAccepted;

        internal WebSocketTransport(int port, Func<string> titleProvider)
        {
            _port = port;
            _titleProvider = titleProvider;
        }

        /// <summary>The port actually bound, which differs from the requested one only on failure (0).</summary>
        internal int Port { get; private set; }

        public void Start()
        {
            _listener = new TcpListener(IPAddress.Loopback, _port);
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;

            var thread = new Thread(AcceptLoop)
            {
                IsBackground = true,
                Name = "wpf-devtools-accept",
            };
            thread.Start();
        }

        public void Dispose()
        {
            _stopped = true;
            try { _listener?.Stop(); } catch { }
        }

        private void AcceptLoop()
        {
            while (!_stopped)
            {
                TcpClient client;
                try
                {
                    client = _listener!.AcceptTcpClient();
                }
                catch
                {
                    // Stop() races the blocking accept; that is the normal way out.
                    return;
                }

                var thread = new Thread(() => ServeClient(client))
                {
                    IsBackground = true,
                    Name = "wpf-devtools-conn",
                };
                thread.Start();
            }
        }

        private void ServeClient(TcpClient client)
        {
            try
            {
                client.NoDelay = true;
                using (client)
                using (NetworkStream stream = client.GetStream())
                {
                    if (!TryReadRequest(stream, out string path, out Dictionary<string, string> headers))
                        return;

                    if (headers.TryGetValue("sec-websocket-key", out string? key) &&
                        IsWebSocketPath(path))
                    {
                        Handshake(stream, key);

                        var connection = new WebSocketConnection(stream, TargetIdOf(path));
                        ConnectionAccepted?.Invoke(connection);
                        connection.ReadLoop();
                        return;
                    }

                    ServeDiscovery(stream, path);
                }
            }
            catch (Exception ex)
            {
                DevToolsServer.Log($"connection ended: {ex.GetType().Name}: {ex.Message}");
            }
        }

        private static bool IsWebSocketPath(string path)
            => path.StartsWith("/devtools/", StringComparison.Ordinal);

        /// <summary>
        /// The target from the WebSocket path (/devtools/page/&lt;id&gt;). Anything unrecognised
        /// is the UI tree: a frontend that guessed a URL should get the document it almost
        /// certainly wanted rather than an error.
        /// </summary>
        private static string TargetIdOf(string path)
            => path.EndsWith("/" + CompositionTargetId, StringComparison.Ordinal)
                ? CompositionTargetId
                : VisualTreeTargetId;

        // ------------------------------------------------------------------
        // HTTP
        // ------------------------------------------------------------------

        private static bool TryReadRequest(Stream stream, out string path, out Dictionary<string, string> headers)
        {
            path = string.Empty;
            headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            string? requestLine = ReadLine(stream);
            if (string.IsNullOrEmpty(requestLine))
                return false;

            string[] parts = requestLine.Split(' ');
            if (parts.Length < 2)
                return false;

            path = parts[1];

            // Headers, to the blank line. Bounded so a client that never sends one
            // cannot hold a thread and unbounded memory.
            for (int i = 0; i < 100; i++)
            {
                string? line = ReadLine(stream);
                if (string.IsNullOrEmpty(line))
                    break;

                int colon = line.IndexOf(':');
                if (colon > 0)
                    headers[line.Substring(0, colon).Trim()] = line.Substring(colon + 1).Trim();
            }

            return true;
        }

        /// <summary>Read one CRLF-terminated line, byte at a time -- headers are tiny and this keeps the stream position exact for the frames that follow.</summary>
        private static string? ReadLine(Stream stream)
        {
            var sb = new StringBuilder(128);
            while (true)
            {
                int b = stream.ReadByte();
                if (b < 0)
                    return sb.Length == 0 ? null : sb.ToString();
                if (b == '\n')
                    return sb.ToString().TrimEnd('\r');
                if (sb.Length > 8192)
                    return sb.ToString();
                sb.Append((char)b);
            }
        }

        private static void Handshake(Stream stream, string key)
        {
            string accept = Convert.ToBase64String(
                SHA1.HashData(Encoding.ASCII.GetBytes(key + WebSocketGuid)));

            string response =
                "HTTP/1.1 101 Switching Protocols\r\n" +
                "Upgrade: websocket\r\n" +
                "Connection: Upgrade\r\n" +
                "Sec-WebSocket-Accept: " + accept + "\r\n\r\n";

            byte[] bytes = Encoding.ASCII.GetBytes(response);
            stream.Write(bytes, 0, bytes.Length);
            stream.Flush();
        }

        /// <summary>
        /// The /json endpoints chrome://inspect reads to find a target. Serving these
        /// is the whole difference between "paste a ws:// URL by hand" and the target
        /// simply appearing in the browser's device list.
        /// </summary>
        private void ServeDiscovery(Stream stream, string path)
        {
            string body;
            int slash = path.IndexOf('?');
            if (slash >= 0)
                path = path.Substring(0, slash);

            switch (path)
            {
                case "/json":
                case "/json/list":
                    body = TargetListJson();
                    break;

                case "/json/version":
                    body = VersionJson();
                    break;

                default:
                    WriteHttp(stream, "404 Not Found", "text/plain", "no such endpoint\n");
                    return;
            }

            WriteHttp(stream, "200 OK", "application/json; charset=UTF-8", body);
        }

        private string TargetListJson()
        {
            // The endpoint returns a bare ARRAY of targets; CdpJson.Build frames one object,
            // so the brackets go on here.
            //
            // TWO targets, because there are two trees and they answer different questions.
            // "wpf" is what the app built; "composition" is what the compositor actually
            // received, decoded from the MILCMD stream. A frontend attaches to whichever it
            // wants and gets an ordinary Elements panel over it.
            string visual = Target(VisualTreeTargetId, _titleProvider(), "WPF visual tree");
            string composition = Target(CompositionTargetId, "MILCMD scene graph",
                                        "What the compositor decoded from the MILCMD stream");

            return "[" + visual + "," + composition + "]";
        }

        private string Target(string id, string title, string description)
        {
            string ws = $"127.0.0.1:{Port.ToString(CultureInfo.InvariantCulture)}/devtools/page/{id}";
            return Json.CdpJson.Build(w =>
            {
                w.WriteString("description", description);
                w.WriteString("devtoolsFrontendUrl", "devtools://devtools/bundled/inspector.html?ws=" + ws);
                w.WriteString("id", id);
                w.WriteString("title", title);
                w.WriteString("type", "page");
                w.WriteString("url", "wpf://app/" + id);
                w.WriteString("webSocketDebuggerUrl", "ws://" + ws);
            });
        }

        private string VersionJson()
        {
            // The frontend does not gate on this, but chrome://inspect has been known
            // to, and which build it is varies. Overridable so a mismatch is one
            // environment variable rather than a rebuild.
            string? configured = Environment.GetEnvironmentVariable("WPF_DEVTOOLS_BROWSER_ID");
            string browser = string.IsNullOrWhiteSpace(configured) ? "WPF-on-WebGPU/1.0" : configured;

            return Json.CdpJson.Build(w =>
            {
                w.WriteString("Browser", browser);
                w.WriteString("Protocol-Version", "1.3");
                w.WriteString("User-Agent", "WPF-on-WebGPU visual tree inspector");
                w.WriteString("V8-Version", "0.0.0.0");
                w.WriteString("WebKit-Version", "0.0.0.0");
                w.WriteString("webSocketDebuggerUrl",
                    $"ws://127.0.0.1:{Port.ToString(CultureInfo.InvariantCulture)}/devtools/browser/{VisualTreeTargetId}");
            });
        }

        private static void WriteHttp(Stream stream, string status, string contentType, string body)
        {
            byte[] payload = Encoding.UTF8.GetBytes(body);
            string head =
                "HTTP/1.1 " + status + "\r\n" +
                "Content-Type: " + contentType + "\r\n" +
                "Content-Length: " + payload.Length.ToString(CultureInfo.InvariantCulture) + "\r\n" +
                // chrome://inspect and a DevTools frontend served from devtools:// are
                // both cross-origin to this endpoint.
                "Access-Control-Allow-Origin: *\r\n" +
                "Connection: close\r\n\r\n";

            byte[] headBytes = Encoding.ASCII.GetBytes(head);
            stream.Write(headBytes, 0, headBytes.Length);
            stream.Write(payload, 0, payload.Length);
            stream.Flush();
        }
    }
}
