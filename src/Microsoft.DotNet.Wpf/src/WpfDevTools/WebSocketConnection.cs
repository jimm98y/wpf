// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// RFC 6455 framing over an already-upgraded stream.
//
// Only what a DevTools frontend actually uses: text messages, fragmentation,
// ping/pong and close. Binary frames are read and dropped -- CDP is JSON, and
// a binary frame here means something other than a frontend is connected.
//

using System;
using System.IO;
using System.Text;

namespace Microsoft.Wpf.DevTools
{
    internal sealed class WebSocketConnection : ICdpConnection
    {
        private const byte OpContinuation = 0x0;
        private const byte OpText = 0x1;
        private const byte OpBinary = 0x2;
        private const byte OpClose = 0x8;
        private const byte OpPing = 0x9;
        private const byte OpPong = 0xA;

        /// <summary>
        /// Cap on a reassembled inbound message. A frontend's commands are a few
        /// hundred bytes; anything approaching this is a bug or a probe, and the
        /// alternative to a cap is letting a peer name its own allocation size.
        /// </summary>
        private const int MaxInboundMessageBytes = 8 * 1024 * 1024;

        private readonly Stream _stream;
        private readonly object _writeLock = new object();
        private volatile bool _closed;

        public event Action<string>? MessageReceived;
        public event Action? Closed;

        internal WebSocketConnection(Stream stream, string targetId)
        {
            _stream = stream;
            TargetId = targetId;
        }

        public string TargetId { get; }

        /// <summary>
        /// Pump frames until the peer goes away. Runs on the connection's own thread
        /// and does not return until the connection is finished.
        /// </summary>
        internal void ReadLoop()
        {
            var message = new MemoryStream();
            byte messageOpcode = 0;

            try
            {
                while (!_closed)
                {
                    if (!TryReadFrame(out bool fin, out byte opcode, out byte[] payload))
                        break;

                    switch (opcode)
                    {
                        case OpClose:
                            SendFrame(OpClose, Array.Empty<byte>());
                            return;

                        case OpPing:
                            SendFrame(OpPong, payload);
                            continue;

                        case OpPong:
                            continue;

                        case OpText:
                        case OpBinary:
                            message.SetLength(0);
                            messageOpcode = opcode;
                            break;

                        case OpContinuation:
                            break;

                        default:
                            // Reserved opcode: RFC 6455 says fail the connection.
                            return;
                    }

                    if (message.Length + payload.Length > MaxInboundMessageBytes)
                        return;

                    message.Write(payload, 0, payload.Length);

                    if (!fin)
                        continue;

                    if (messageOpcode == OpText)
                    {
                        string json = Encoding.UTF8.GetString(message.GetBuffer(), 0, (int)message.Length);
                        try
                        {
                            MessageReceived?.Invoke(json);
                        }
                        catch (Exception ex)
                        {
                            // A handler that throws must not kill the connection: the
                            // frontend would reconnect and hit the same command again.
                            DevToolsServer.Log($"handler threw: {ex.GetType().Name}: {ex.Message}");
                        }
                    }

                    message.SetLength(0);
                }
            }
            catch (Exception ex)
            {
                DevToolsServer.Log($"read loop ended: {ex.GetType().Name}: {ex.Message}");
            }
            finally
            {
                _closed = true;
                message.Dispose();
                try { Closed?.Invoke(); } catch { }
            }
        }

        private bool TryReadFrame(out bool fin, out byte opcode, out byte[] payload)
        {
            fin = false;
            opcode = 0;
            payload = Array.Empty<byte>();

            int b0 = _stream.ReadByte();
            if (b0 < 0)
                return false;

            int b1 = _stream.ReadByte();
            if (b1 < 0)
                return false;

            fin = (b0 & 0x80) != 0;
            opcode = (byte)(b0 & 0x0F);

            bool masked = (b1 & 0x80) != 0;
            long length = b1 & 0x7F;

            if (length == 126)
            {
                byte[] ext = ReadExactly(2);
                length = (ext[0] << 8) | ext[1];
            }
            else if (length == 127)
            {
                byte[] ext = ReadExactly(8);
                length = 0;
                for (int i = 0; i < 8; i++)
                    length = (length << 8) | ext[i];
            }

            if (length < 0 || length > MaxInboundMessageBytes)
                return false;

            byte[] mask = masked ? ReadExactly(4) : Array.Empty<byte>();
            payload = ReadExactly((int)length);

            if (masked)
            {
                for (int i = 0; i < payload.Length; i++)
                    payload[i] ^= mask[i & 3];
            }

            return true;
        }

        private byte[] ReadExactly(int count)
        {
            var buffer = new byte[count];
            int read = 0;
            while (read < count)
            {
                int n = _stream.Read(buffer, read, count - read);
                if (n <= 0)
                    throw new EndOfStreamException("peer closed mid-frame");
                read += n;
            }
            return buffer;
        }

        public void Send(string json)
        {
            if (_closed)
                return;

            try
            {
                SendFrame(OpText, Encoding.UTF8.GetBytes(json));
            }
            catch (Exception ex)
            {
                _closed = true;
                DevToolsServer.Log($"send failed: {ex.GetType().Name}: {ex.Message}");
            }
        }

        private void SendFrame(byte opcode, byte[] payload)
        {
            // Events are raised from the UI thread while a command response is being
            // written from a connection thread, so the two must not interleave on the
            // wire.
            lock (_writeLock)
            {
                var header = new byte[10];
                int n = 0;
                header[n++] = (byte)(0x80 | opcode);   // FIN, never fragmented outbound

                // Server-to-client frames are never masked.
                if (payload.Length < 126)
                {
                    header[n++] = (byte)payload.Length;
                }
                else if (payload.Length <= ushort.MaxValue)
                {
                    header[n++] = 126;
                    header[n++] = (byte)(payload.Length >> 8);
                    header[n++] = (byte)payload.Length;
                }
                else
                {
                    header[n++] = 127;
                    long len = payload.Length;
                    for (int i = 7; i >= 0; i--)
                        header[n++] = (byte)(len >> (i * 8));
                }

                _stream.Write(header, 0, n);
                _stream.Write(payload, 0, payload.Length);
                _stream.Flush();
            }
        }

        public void Close()
        {
            if (_closed)
                return;

            _closed = true;
            try { SendFrame(OpClose, Array.Empty<byte>()); } catch { }
            try { _stream.Dispose(); } catch { }
        }
    }
}
