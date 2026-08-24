#!/usr/bin/env python3
"""Bridge the browser head's CDP message port to a WebSocket a DevTools frontend can attach to.

Every other head runs a TCP server inside the app, so chrome://inspect can reach it directly. A
page cannot accept a connection, so the browser head exposes the same protocol as a message port
(`__wpfDevTools.send` / `.onmessage`, see devtools-bridge.js) and something outside has to put a
socket in front of it. That is all this is.

Both sides connect TO here:

    the app page          ws://127.0.0.1:<port>/app
    the DevTools frontend ws://127.0.0.1:<port>/devtools/page/wpf

and the /json endpoints advertise the second one so the target shows up in chrome://inspect.

    python3 eng/devtools-relay.py                 # then open the app with ?devtools=1&relay=9223

Loopback only, and no authentication: the far end hands out and can modify the whole UI of the
app it is attached to. Same rule as the in-process transport it stands in for.
"""

import argparse
import base64
import hashlib
import json
import socket
import struct
import sys
import threading

WS_GUID = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11"
APP_PATH = "/app"


class WebSocket:
    """Server side of one RFC 6455 connection. Text frames only, which is all CDP uses."""

    def __init__(self, conn):
        self.conn = conn
        self._send_lock = threading.Lock()
        self._buf = b""

    # -- framing ---------------------------------------------------------
    def send(self, text):
        payload = text.encode("utf-8")
        header = bytearray([0x81])  # FIN + text
        n = len(payload)
        if n < 126:
            header.append(n)
        elif n < (1 << 16):
            header.append(126)
            header += struct.pack(">H", n)
        else:
            header.append(127)
            header += struct.pack(">Q", n)
        with self._send_lock:
            self.conn.sendall(bytes(header) + payload)

    def _read(self, n):
        while len(self._buf) < n:
            chunk = self.conn.recv(65536)
            if not chunk:
                raise ConnectionError("peer closed")
            self._buf += chunk
        out, self._buf = self._buf[:n], self._buf[n:]
        return out

    def recv(self):
        """Next text message, or None on close. Continuation frames are reassembled."""
        parts = []
        while True:
            b0, b1 = self._read(2)
            opcode = b0 & 0x0F
            fin = b0 & 0x80
            masked = b1 & 0x80
            length = b1 & 0x7F
            if length == 126:
                length = struct.unpack(">H", self._read(2))[0]
            elif length == 127:
                length = struct.unpack(">Q", self._read(8))[0]
            mask = self._read(4) if masked else None
            data = self._read(length) if length else b""
            if mask:
                data = bytes(c ^ mask[i % 4] for i, c in enumerate(data))

            if opcode == 0x8:                      # close
                return None
            if opcode == 0x9:                      # ping -> pong
                with self._send_lock:
                    self.conn.sendall(bytes([0x8A, len(data)]) + data)
                continue
            if opcode == 0xA:                      # pong
                continue
            parts.append(data)
            if fin:
                return b"".join(parts).decode("utf-8", "replace")

    def close(self):
        try:
            self.conn.close()
        except OSError:
            pass


class Relay:
    def __init__(self, port, verbose):
        self.port = port
        self.verbose = verbose
        self.app = None          # the WPF page
        self.frontend = None     # the DevTools frontend
        self.lock = threading.Lock()

    def log(self, *a):
        if self.verbose:
            print("[relay]", *a, file=sys.stderr, flush=True)

    # -- HTTP ------------------------------------------------------------
    def targets(self):
        ws = f"127.0.0.1:{self.port}/devtools/page/wpf"
        return [{
            "description": "WPF visual tree (browser head, via relay)",
            "devtoolsFrontendUrl": f"devtools://devtools/bundled/inspector.html?ws={ws}",
            "id": "wpf",
            "title": "WPF on WebGPU",
            "type": "page",
            "url": "wpf://app/wpf",
            "webSocketDebuggerUrl": f"ws://{ws}",
        }]

    def http(self, conn, path):
        if path in ("/json", "/json/list"):
            body = json.dumps(self.targets())
        elif path == "/json/version":
            body = json.dumps({
                "Browser": "WPF-on-WebGPU/1.0",
                "Protocol-Version": "1.3",
                "User-Agent": "WPF-on-WebGPU visual tree inspector (browser head relay)",
                "V8-Version": "0.0.0.0",
                "WebKit-Version": "0.0.0.0",
            })
        else:
            conn.sendall(b"HTTP/1.1 404 Not Found\r\nContent-Length: 0\r\n"
                         b"Connection: close\r\n\r\n")
            return
        payload = body.encode()
        conn.sendall(
            b"HTTP/1.1 200 OK\r\n"
            b"Content-Type: application/json; charset=UTF-8\r\n"
            + f"Content-Length: {len(payload)}\r\n".encode()
            + b"Access-Control-Allow-Origin: *\r\nConnection: close\r\n\r\n" + payload)

    # -- pumping ---------------------------------------------------------
    def pump(self, src, name):
        """Forward everything from `src` to whichever peer is on the other side."""
        try:
            while True:
                msg = src.recv()
                if msg is None:
                    break
                with self.lock:
                    dst = self.frontend if name == "app" else self.app
                if dst is None:
                    self.log(f"{name}: dropped a message, no peer attached yet")
                    continue
                try:
                    dst.send(msg)
                except OSError:
                    break
        except (ConnectionError, OSError):
            pass
        finally:
            with self.lock:
                if name == "app" and self.app is src:
                    self.app = None
                elif name == "frontend" and self.frontend is src:
                    self.frontend = None
            src.close()
            self.log(f"{name} disconnected")

    def serve_client(self, conn):
        conn.settimeout(None)
        data = b""
        while b"\r\n\r\n" not in data:
            chunk = conn.recv(4096)
            if not chunk:
                conn.close()
                return
            data += chunk
        head = data.split(b"\r\n\r\n", 1)[0].decode("latin-1")
        lines = head.split("\r\n")
        path = lines[0].split(" ")[1] if len(lines[0].split(" ")) > 1 else "/"
        headers = {}
        for line in lines[1:]:
            if ":" in line:
                k, v = line.split(":", 1)
                headers[k.strip().lower()] = v.strip()

        key = headers.get("sec-websocket-key")
        if not key:
            self.http(conn, path.split("?")[0])
            conn.close()
            return

        accept = base64.b64encode(
            hashlib.sha1((key + WS_GUID).encode()).digest()).decode()
        conn.sendall(
            b"HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\n"
            b"Connection: Upgrade\r\n"
            + f"Sec-WebSocket-Accept: {accept}\r\n\r\n".encode())

        ws = WebSocket(conn)
        role = "app" if path.startswith(APP_PATH) else "frontend"
        with self.lock:
            old = self.app if role == "app" else self.frontend
            # One of each. A reload reconnects, and the stale socket must go or the
            # relay forwards into a socket nobody is reading.
            if old is not None:
                old.close()
            if role == "app":
                self.app = ws
            else:
                self.frontend = ws
        self.log(f"{role} attached ({path})")
        self.pump(ws, role)

    def run(self):
        srv = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
        srv.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
        srv.bind(("127.0.0.1", self.port))
        srv.listen(16)
        print(f"relay on 127.0.0.1:{self.port}\n"
              f"  app page      -> ws://127.0.0.1:{self.port}{APP_PATH}\n"
              f"  frontend      -> ws://127.0.0.1:{self.port}/devtools/page/wpf\n"
              f"  chrome://inspect: add localhost:{self.port} under Configure",
              file=sys.stderr, flush=True)
        while True:
            conn, _ = srv.accept()
            conn.setsockopt(socket.IPPROTO_TCP, socket.TCP_NODELAY, 1)
            threading.Thread(target=self.serve_client, args=(conn,), daemon=True).start()


def main():
    ap = argparse.ArgumentParser(description=__doc__,
                                 formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("-p", "--port", type=int, default=9223)
    ap.add_argument("-q", "--quiet", action="store_true")
    args = ap.parse_args()
    try:
        Relay(args.port, not args.quiet).run()
    except KeyboardInterrupt:
        pass


if __name__ == "__main__":
    main()
