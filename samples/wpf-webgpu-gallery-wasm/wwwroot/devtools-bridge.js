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

/** The most recent good frame, base64 PNG, and whether one is already being taken. */
let snapshot = null;
let snapshotPending = false;

/** The app's canvas: main.js registers them, querySelector covers a host page that does not. */
function appCanvas() {
    return globalThis.__wpfCanvases?.values().next().value
        ?? document.querySelector('canvas');
}

/**
 * Is the canvas mid-redraw?
 *
 * The timer usually lands outside the app's animation-frame callback, but not always: about 7% of
 * captures came back as a blank sheet, which the panel showed as a flicker. A 64x64 downsample is
 * cheap enough to check every time and answers it exactly -- a cleared buffer is one uniform
 * colour. Checking first also SAVES time on those frames, since the encode is skipped entirely.
 */
function isBlank(canvas) {
    try {
        const probe = document.createElement('canvas');
        probe.width = 64;
        probe.height = 64;
        const ctx = probe.getContext('2d');
        ctx.drawImage(canvas, 0, 0, 64, 64);
        const pixels = ctx.getImageData(0, 0, 64, 64).data;
        // Two distinct byte values is a solid colour plus its alpha; anything drawn beats that.
        const seen = new Set();
        for (let i = 0; i < pixels.length; i++) {
            seen.add(pixels[i]);
            if (seen.size > 2) {
                return false;
            }
        }
        return true;
    } catch (e) {
        // Unreadable is not the same as blank; let the caller try to encode it.
        return false;
    }
}

function takeSnapshot(maxWidth, maxHeight, format, quality) {
    // JPEG is what a frontend asks for by default, and it matters here: a full PNG encode of a
    // 2800x1626 canvas on the main thread dropped the app itself to about 1 fps, because this
    // runs on the same thread the WPF pump does. Encoding what was actually requested, at the
    // requested quality, is most of that cost back.
    const mime = format === 'jpeg' ? 'image/jpeg' : 'image/png';
    const q = Math.min(1, Math.max(0.01, (quality || 80) / 100));
    const canvas = appCanvas();
    if (!canvas || !canvas.width || !canvas.height || isBlank(canvas)) {
        return null;
    }

    const fit = Math.min(1, maxWidth / canvas.width, maxHeight / canvas.height);
    if (fit >= 1) {
        return canvas.toDataURL(mime, q).split(',')[1];
    }

    // Scaled rather than sent whole: the frontend asks for a bounding box, and on a 2x display
    // the raw canvas is four times the pixels it will actually show.
    const scaled = document.createElement('canvas');
    scaled.width = Math.max(1, Math.round(canvas.width * fit));
    scaled.height = Math.max(1, Math.round(canvas.height * fit));
    scaled.getContext('2d').drawImage(canvas, 0, 0, scaled.width, scaled.height);
    return scaled.toDataURL(mime, q).split(',')[1];
}

/**
 * One frame of the app, as base64 PNG, for Page.screencastFrame.
 *
 * The desktop heads read the renderer's composed frame back off the GPU. That is not available
 * here: the readback blocks on a buffer map and WebGPU only maps asynchronously, so the composed
 * path returns nothing and the RenderTargetBitmap fallback (which needs the same readback) gave a
 * correctly sized, entirely blank white image -- a screencast pane that looked switched on and
 * showed nothing.
 *
 * The browser has the frame already, on the canvas. The catch is WHEN it can be read. Measured on
 * the gallery, counting distinct colours in a 64x64 downsample:
 *
 *     synchronously, between frames  240      requestAnimationFrame           240
 *     setTimeout(0)                  240      rAF inside rAF (next frame)       1
 *
 * That last one is blank because it lands at the top of the app's own animation-frame callback,
 * after the drawing buffer has been cleared for the new frame -- which is exactly where the
 * managed side asks, since CompositionTarget.Rendering fires there. Reading on demand therefore
 * captured nothing, reliably and silently.
 *
 * So the snapshot is taken on a timer, outside that window, and this hands back the most recent
 * one. It costs a frame of latency in the panel and is the difference between a picture and a
 * white rectangle. Nothing is scheduled until someone asks, so an inspector with no screencast
 * running encodes nothing.
 */
export function captureCanvas(maxWidth, maxHeight, format, quality) {
    if (!snapshotPending) {
        snapshotPending = true;
        setTimeout(() => {
            snapshotPending = false;
            try {
                // Keep the last good frame when this one could not be taken: a stale frame in the
                // panel reads as "nothing moved", a blank one reads as a flicker.
                const taken = takeSnapshot(maxWidth, maxHeight, format, quality);
                if (taken) {
                    snapshot = taken;
                }
            } catch (e) {
                console.error('[wpf-devtools] canvas capture failed:', e);
            }
        }, 0);
    }

    // Null on the very first call, before any snapshot exists. The managed side treats that as
    // "no frame this tick" and asks again, so the panel starts one frame late rather than never.
    return snapshot;
}
