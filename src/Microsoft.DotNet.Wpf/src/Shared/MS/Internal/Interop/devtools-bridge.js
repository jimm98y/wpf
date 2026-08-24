// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// The browser head's half of the CDP visual-tree inspector.
//
// A page cannot accept a WebSocket connection, so unlike every other head this
// is not a server. It is a message port: the inspector's replies and events
// arrive at receive(), and commands go back through __wpfDevTools.send(). What
// you do with that is deliberately left open --
//
//   * drive it straight from the console:
//       __wpfDevTools.onmessage = m => console.log(JSON.parse(m));
//       __wpfDevTools.send({ id: 1, method: 'DOM.getDocument', params: { depth: -1 } });
//
//   * or forward both directions to a relay so a real DevTools frontend can
//     attach:
//       const ws = new WebSocket('ws://localhost:9223/page');
//       ws.onmessage = e => __wpfDevTools.send(e.data);
//       __wpfDevTools.onmessage = m => ws.send(m);
//
// A relay IS shipped now, because driving the port by hand is fine for a script and no way to
// walk a tree: eng/devtools-relay.py puts a socket in front of this port so chrome://inspect
// finds the target like it does on every other head. It stays opt in -- the host page sets
// globalThis.__wpfDevToolsRelay before runMain and connectRelay() does the rest.
//
// The host page registers this module the same way it registers the windowing
// one, and additionally hands over the managed exports, because a JS module
// cannot reach them by itself:
//
//   import * as wpfDevTools from './devtools-bridge.js';
//   setModuleImports('wpfDevTools', wpfDevTools);
//   globalThis.__wpfDevToolsExports =
//       await runtime.getAssemblyExports('Microsoft.Wpf.DevTools');
//
// Without that second line the port is receive-only: replies and events still
// arrive, but send() has nothing to call.
//

/** Queued replies, for a consumer that attaches after the inspector starts. */
const pending = [];

/** Set by whoever wants the messages. Draining `pending` is their first job. */
let onmessage = null;

/** Called by the managed side once its exports are callable. */
export function ready() {
    globalThis.__wpfDevTools = {
        /**
         * Send one CDP command. Accepts an object or a JSON string, because the
         * console wants the former and a relay already has the latter.
         */
        send(command) {
            const json = typeof command === 'string' ? command : JSON.stringify(command);
            const exports = globalThis.__wpfDevToolsExports;
            if (!exports) {
                throw new Error(
                    'globalThis.__wpfDevToolsExports is not set. The host page must assign it from ' +
                    "runtime.getAssemblyExports('Microsoft.Wpf.DevTools'); see devtools-bridge.js.");
            }
            exports.Microsoft.Wpf.DevTools.BrowserTransport.Dispatch(json);
        },

        /** Messages that arrived before anyone was listening. */
        get pending() {
            return pending;
        },

        set onmessage(handler) {
            onmessage = handler;
            // Hand over the backlog rather than dropping it: the interesting
            // messages are often the ones from before you opened the console.
            while (handler && pending.length) {
                handler(pending.shift());
            }
        },

        get onmessage() {
            return onmessage;
        },

        /** Forward this port to a relay so a real DevTools frontend can attach. */
        connectRelay,
    };

    // The host page sets this before runMain, because the port does not exist until now
    // and it has no other moment to hook. Nothing happens without it.
    if (globalThis.__wpfDevToolsRelay) {
        connectRelay(globalThis.__wpfDevToolsRelay);
    }
}

/**
 * Pump this port to and from a WebSocket relay (eng/devtools-relay.py), which puts a socket in
 * front of it so chrome://inspect can attach. Retries, because the usual order of events is that
 * the page is reloaded while the relay keeps running -- and, less often, the reverse.
 */
function connectRelay(url, attempt = 0) {
    let ws;
    try {
        ws = new WebSocket(url);
    } catch (e) {
        console.error('[wpf-devtools] relay URL rejected:', url, e);
        return;
    }

    ws.onopen = () => {
        console.log(`[wpf-devtools] relay connected: ${url}`);
        // Assigning onmessage hands over the backlog, so anything the inspector said
        // before the socket opened reaches the frontend rather than being dropped.
        globalThis.__wpfDevTools.onmessage = (m) => {
            if (ws.readyState === WebSocket.OPEN) {
                ws.send(m);
            }
        };
    };
    ws.onmessage = (e) => {
        try {
            globalThis.__wpfDevTools.send(e.data);
        } catch (err) {
            console.error('[wpf-devtools] command failed:', err);
        }
    };
    ws.onclose = () => {
        globalThis.__wpfDevTools.onmessage = null;
        // Back off to a second, then keep trying at that rate: a relay started after the
        // page should still pick it up, without a tight loop while it is absent.
        const delay = Math.min(1000, 100 * (attempt + 1));
        setTimeout(() => connectRelay(url, attempt + 1), delay);
    };
    ws.onerror = () => ws.close();
}

/** One reply or event from the inspector, as a JSON string. */
export function receive(json) {
    if (onmessage) {
        onmessage(json);
    } else {
        // Bounded: an inspector nobody is listening to must not grow without limit.
        if (pending.length >= 1000) {
            pending.shift();
        }
        pending.push(json);
    }
}
