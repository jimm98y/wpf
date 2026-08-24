// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// A CDP client for the tests: ClientWebSocket plus id correlation.
//
// Deliberately uses the framework's WebSocket rather than anything shared with
// the server. The server's framing is hand-written (there is no server-side
// WebSocket that works off Windows), so having the tests drive it through an
// INDEPENDENT implementation is most of the point -- if the two ever disagree
// about masking or an extended length, that has to fail here.
//

using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;

namespace Wpf.DevTools.Tests
{
    internal sealed class CdpClient : IDisposable
    {
        private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

        private readonly ClientWebSocket _socket = new ClientWebSocket();
        private readonly List<JsonDocument> _events = new List<JsonDocument>();
        private int _nextId;

        internal CdpClient(int port, string targetId = "wpf")
        {
            Port = port;
            using var cts = new CancellationTokenSource(Timeout);
            _socket.ConnectAsync(new Uri($"ws://127.0.0.1:{port}/devtools/page/{targetId}"), cts.Token)
                   .GetAwaiter().GetResult();
        }

        internal int Port { get; }

        internal static string HttpGet(int port, string path)
        {
            using var client = new HttpClient { Timeout = Timeout };
            return client.GetStringAsync($"http://127.0.0.1:{port}{path}").GetAwaiter().GetResult();
        }

        /// <summary>Send a command and return its result object.</summary>
        internal JsonElement Call(string method, object? parameters = null)
        {
            JsonDocument response = CallRaw(method, parameters);
            if (response.RootElement.TryGetProperty("error", out JsonElement error))
                throw new InvalidOperationException($"{method} failed: {error}");

            return response.RootElement.GetProperty("result");
        }

        /// <summary>Send a command and return the whole reply, error and all.</summary>
        internal JsonDocument CallRaw(string method, object? parameters = null)
        {
            int id = ++_nextId;
            var payload = new StringBuilder();
            payload.Append("{\"id\":").Append(id).Append(",\"method\":\"").Append(method).Append('"');
            if (parameters != null)
                payload.Append(",\"params\":").Append(JsonSerializer.Serialize(parameters));
            payload.Append('}');

            Send(payload.ToString());

            // Events interleave with responses; hold them for WaitForEvent.
            while (true)
            {
                JsonDocument message = Receive();
                if (message.RootElement.TryGetProperty("id", out JsonElement gotId) && gotId.GetInt32() == id)
                    return message;

                _events.Add(message);
            }
        }

        /// <summary>The next event with this method, including any already buffered.</summary>
        internal JsonElement WaitForEvent(string method)
        {
            for (int i = 0; i < _events.Count; i++)
            {
                if (MethodOf(_events[i]) == method)
                {
                    JsonElement found = _events[i].RootElement.GetProperty("params");
                    _events.RemoveAt(i);
                    return found;
                }
            }

            var deadline = DateTime.UtcNow + Timeout;
            while (DateTime.UtcNow < deadline)
            {
                JsonDocument message = Receive();
                if (MethodOf(message) == method)
                    return message.RootElement.GetProperty("params");

                _events.Add(message);
            }

            throw new TimeoutException($"no {method} event within {Timeout.TotalSeconds}s");
        }

        private static string? MethodOf(JsonDocument document)
            => document.RootElement.TryGetProperty("method", out JsonElement m) ? m.GetString() : null;

        private void Send(string json)
        {
            using var cts = new CancellationTokenSource(Timeout);
            _socket.SendAsync(Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text, true, cts.Token)
                   .GetAwaiter().GetResult();
        }

        private JsonDocument Receive()
        {
            using var cts = new CancellationTokenSource(Timeout);
            var buffer = new byte[64 * 1024];
            var message = new MemoryStreamLite();

            while (true)
            {
                WebSocketReceiveResult result =
                    _socket.ReceiveAsync(new ArraySegment<byte>(buffer), cts.Token).GetAwaiter().GetResult();

                if (result.MessageType == WebSocketMessageType.Close)
                    throw new InvalidOperationException("the endpoint closed the connection");

                message.Write(buffer, result.Count);

                if (result.EndOfMessage)
                    return JsonDocument.Parse(message.ToArray());
            }
        }

        public void Dispose()
        {
            foreach (JsonDocument document in _events)
                document.Dispose();

            try { _socket.Abort(); } catch { }
            _socket.Dispose();
        }

        /// <summary>Grows a byte buffer across WebSocket fragments.</summary>
        private sealed class MemoryStreamLite
        {
            private byte[] _buffer = new byte[64 * 1024];
            private int _length;

            internal void Write(byte[] source, int count)
            {
                if (_length + count > _buffer.Length)
                    Array.Resize(ref _buffer, Math.Max(_buffer.Length * 2, _length + count));

                Buffer.BlockCopy(source, 0, _buffer, _length, count);
                _length += count;
            }

            internal byte[] ToArray()
            {
                var result = new byte[_length];
                Buffer.BlockCopy(_buffer, 0, result, 0, _length);
                return result;
            }
        }
    }
}
