// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
//
// JS half of MS.Internal.Interop.BrowserWindow (WindowsBase): each WPF window is a
// canvas element; DOM input is queued here and drained by the dispatcher pump via
// drainEvents(). Canvases are also registered in globalThis.__wpfCanvases so the
// WebGPU backend (wgpu-interop.js) can create surfaces from the same handles.
//
// Coordinate conventions match the Win32-guard expectations: content size in CSS
// points, pixel size / screen origins in top-left device pixels.

const windows = new Map();   // handle -> { canvas, borderless }
const queue = [];
let zTop = 10;
let listenersInstalled = false;

function canvases() {
    if (!globalThis.__wpfCanvases) globalThis.__wpfCanvases = new Map();
    return globalThis.__wpfCanvases;
}

function dpr() { return window.devicePixelRatio || 1; }

function host() { return document.getElementById("wpf-host") ?? document.body; }

export function createWindow(handle, title, x, y, width, height, borderless) {
    const canvas = document.createElement("canvas");
    canvas.dataset.wpfHandle = String(handle);
    canvas.style.display = "block";
    canvas.style.position = borderless ? "fixed" : "relative";
    // Popups are created AT their target position (CreateWindowEx semantics): WPF may
    // not issue a follow-up move when the creation coordinates already match.
    canvas.style.left = borderless ? `${x / dpr()}px` : "0px";
    canvas.style.top = borderless ? `${y / dpr()}px` : "0px";
    canvas.style.zIndex = String(borderless ? ++zTop : 1);
    host().appendChild(canvas);
    windows.set(handle, { canvas, borderless });
    canvases().set(handle, canvas);
    setContentSize(handle, width, height);
    if (!borderless && title) document.title = title;
    installListeners();
}

export function destroyWindow(handle) {
    const w = windows.get(handle);
    if (!w) return;
    w.canvas.remove();
    windows.delete(handle);
    canvases().delete(handle);
}

export function setContentSize(handle, width, height) {
    const w = windows.get(handle);
    if (!w) return;
    // Identical size is a strict no-op: macOS fires window-resize bursts with
    // unchanged dimensions, a same-value canvas.width write still blanks the
    // canvas, and the queued synthetic WM_SIZE would re-layout and invalidate
    // the renderer's entire layer cache (~65ms per spurious event).
    const dw = Math.max(1, Math.round(width * dpr()));
    const dh = Math.max(1, Math.round(height * dpr()));
    if (w.canvas.width === dw && w.canvas.height === dh && w.canvas.style.width === `${width}px`)
        return;
    w.canvas.style.width = `${width}px`;
    w.canvas.style.height = `${height}px`;
    // The backing-store (attribute) size is owned by the WebGPU surface configure;
    // set it here too so an unconfigured canvas still lays out correctly.
    w.canvas.width = Math.max(1, Math.round(width * dpr()));
    w.canvas.height = Math.max(1, Math.round(height * dpr()));
    // Windows raises WM_SIZE after SetWindowPos (and CocoaWindow polls for resizes);
    // queue the equivalent so HwndTarget re-layouts and the surface reconfigures.
    queue.push({
        t: "r", h: handle,
        x: Math.max(1, Math.round(width * dpr())),
        y: Math.max(1, Math.round(height * dpr())),
        ts: Date.now() & 0x7FFFFFFF,
    });
}

export function setFrameOrigin(handle, xPixels, yPixels) {
    const w = windows.get(handle);
    if (!w) return;
    w.canvas.style.position = "fixed";
    w.canvas.style.left = `${xPixels / dpr()}px`;
    w.canvas.style.top = `${yPixels / dpr()}px`;
}

export function getWidth(handle, devicePixels) {
    const w = windows.get(handle);
    if (!w) return 0;
    const r = w.canvas.getBoundingClientRect();
    return Math.round(devicePixels ? r.width * dpr() : r.width);
}

export function getHeight(handle, devicePixels) {
    const w = windows.get(handle);
    if (!w) return 0;
    const r = w.canvas.getBoundingClientRect();
    return Math.round(devicePixels ? r.height * dpr() : r.height);
}

export function getScreenOriginX(handle) {
    const w = windows.get(handle);
    return w ? Math.round(w.canvas.getBoundingClientRect().left * dpr()) : 0;
}

export function getScreenOriginY(handle) {
    const w = windows.get(handle);
    return w ? Math.round(w.canvas.getBoundingClientRect().top * dpr()) : 0;
}

export function getDevicePixelRatio() { return dpr(); }

export function getViewportWidthPixels() { return Math.round(window.innerWidth * dpr()); }

export function getViewportHeightPixels() { return Math.round(window.innerHeight * dpr()); }

// x/y in top-left device pixels ("screen" == page viewport). Topmost first.
export function hitTest(x, y) {
    let best = 0, bestZ = -1;
    for (const [handle, w] of windows) {
        const r = w.canvas.getBoundingClientRect();
        const z = parseInt(w.canvas.style.zIndex || "0", 10);
        if (x >= r.left * dpr() && x < r.right * dpr() &&
            y >= r.top * dpr() && y < r.bottom * dpr() && z >= bestZ) {
            best = handle;
            bestZ = z;
        }
    }
    return best;
}

export function setCursor(cssCursor) {
    document.body.style.cursor = cssCursor || "default";
}

// ---- Input method (IME) ----------------------------------------------------
//
// Typing Japanese, Chinese or Korean in a browser is not a sequence of keystrokes: the IME takes the
// keys, shows a candidate list, and hands back the finished text through composition events. Those
// events only exist for an EDITABLE element, and a WPF window here is a <canvas>, which is not one.
// So there is a real editable element, kept invisible and empty, that holds focus while a WPF text
// field does. It exists to be composed into and for the browser to anchor the candidate list to;
// its content is never read as text, only the composition events it emits are.
//
// It cannot be display:none or visibility:hidden - either one stops the IME from engaging at all -
// so it is transparent, one pixel, and parked at the caret.

let imeElement = null;
let imeComposing = false;

function ensureImeElement() {
    if (imeElement) return imeElement;

    const el = document.createElement("div");
    el.contentEditable = "true";
    el.setAttribute("aria-hidden", "true");
    el.style.cssText =
        "position:fixed;width:1px;height:1px;padding:0;border:0;outline:0;" +
        "opacity:0;color:transparent;background:transparent;caret-color:transparent;" +
        "overflow:hidden;z-index:2147483647;white-space:pre;";

    el.addEventListener("compositionstart", () => {
        imeComposing = true;
        queue.push({ t: "i", k: 0, s: "" });
    });

    el.addEventListener("compositionupdate", (e) => {
        // The in-progress composition. The DOM exposes no clause selection (unlike IMM32 and
        // zwp_text_input_v3), so the caret is reported at the end of the preedit and the whole run
        // draws with the same underline.
        queue.push({ t: "i", k: 1, s: e.data ?? "" });
    });

    el.addEventListener("compositionend", (e) => {
        imeComposing = false;
        queue.push({ t: "i", k: 2, s: e.data ?? "" });
        // The element is a scratchpad, not a document: anything left behind would become the
        // starting context of the next composition.
        el.textContent = "";
    });

    // Keystrokes that are NOT part of a composition still land here while it holds focus (it is the
    // focused element). WPF has already handled them as key events, so drop the text.
    el.addEventListener("input", () => { if (!imeComposing) el.textContent = ""; });

    host().appendChild(el);
    imeElement = el;
    return el;
}

/// A WPF text field took focus: give the element focus so an IME can engage.
export function enableTextInput() {
    const el = ensureImeElement();
    el.style.display = "block";
    if (document.activeElement !== el) el.focus({ preventScroll: true });
}

/// Focus left the text field. Blur so the IME detaches and no candidate window lingers.
export function disableTextInput() {
    if (!imeElement) return;
    imeComposing = false;
    imeElement.textContent = "";
    if (document.activeElement === imeElement) imeElement.blur();
    imeElement.style.display = "none";
}

/// Where the caret is, in top-left DEVICE pixels. The browser anchors the candidate window to the
/// focused element, so moving the element to the caret is what puts the candidates under the text.
export function setImeCaretRect(x, y, width, height) {
    const el = ensureImeElement();
    const s = dpr();
    el.style.left = `${x / s}px`;
    el.style.top = `${y / s}px`;
    el.style.width = `${Math.max(1, width / s)}px`;
    el.style.height = `${Math.max(1, height / s)}px`;
}

export function isComposing() { return imeComposing; }

export function drainEvents() {
    if (queue.length === 0) return "";
    return JSON.stringify(queue.splice(0));
}

// Resolves on the next animation frame (display-aligned; what the dispatcher pump
// awaits between ticks). The 250ms timeout keeps the app ticking slowly when the
// tab is hidden and rAF stops firing.
export function nextFrame() {
    return new Promise((resolve) => {
        const t = setTimeout(() => resolve(1), 250);
        requestAnimationFrame(() => { clearTimeout(t); resolve(0); });
    });
}

// ---- DOM event capture -------------------------------------------------------

function topmostWindowAt(clientX, clientY) {
    let best = 0, bestZ = -1;
    for (const [handle, w] of windows) {
        const r = w.canvas.getBoundingClientRect();
        const z = parseInt(w.canvas.style.zIndex || "0", 10);
        if (clientX >= r.left && clientX < r.right && clientY >= r.top && clientY < r.bottom && z >= bestZ) {
            best = handle;
            bestZ = z;
        }
    }
    return best;
}

function pushMouse(kind, e, wheel = 0) {
    // Route to the window under the pointer (popups float above the main canvas);
    // WPF's capture logic handles routing during drags.
    let handle = topmostWindowAt(e.clientX, e.clientY);
    if (!handle) {
        // Outside every canvas (e.g. drag past the edge): report against the main window.
        for (const [h, w] of windows) { if (!w.borderless) { handle = h; break; } }
        if (!handle) return;
    }
    const r = windows.get(handle).canvas.getBoundingClientRect();
    queue.push({
        t: "m", k: kind, h: handle, b: e.button ?? 0,
        x: Math.round((e.clientX - r.left) * dpr()),
        y: Math.round((e.clientY - r.top) * dpr()),
        w: wheel,
        ts: Math.round(e.timeStamp),
    });
}

function installListeners() {
    if (listenersInstalled) return;
    listenersInstalled = true;

    window.addEventListener("mousemove", (e) => pushMouse(0, e));
    window.addEventListener("mousedown", (e) => pushMouse(1, e));
    window.addEventListener("mouseup", (e) => pushMouse(2, e));
    window.addEventListener("wheel", (e) => {
        // deltaMode 0 = pixels; convert to Win32 notches (~100px per 120 units).
        const notches = Math.max(-960, Math.min(960, Math.round(-e.deltaY * 120 / 100)));
        if (notches !== 0) pushMouse(3, e, notches);
        e.preventDefault();
    }, { passive: false });
    window.addEventListener("contextmenu", (e) => e.preventDefault());

    window.addEventListener("keydown", (e) => pushKey(true, e));
    window.addEventListener("keyup", (e) => pushKey(false, e));

    window.addEventListener("resize", () => {
        for (const [handle, w] of windows) {
            if (w.borderless) continue;
            // Mimic the OS resizing a maximized window: the main canvas tracks the
            // viewport; setContentSize queues the synthetic WM_SIZE for WPF.
            setContentSize(handle, window.innerWidth, window.innerHeight);
        }
    });
}

function pushKey(isDown, e) {
    let handle = 0;
    for (const [h, w] of windows) { if (!w.borderless) { handle = h; break; } }
    if (!handle) return;

    // While an IME is composing, the keystrokes are ITS input, not the app's. Two things have to
    // happen and both matter: the key must not reach WPF (it would move the caret out from under a
    // composition the user is still editing, and the committed text arrives separately), and it must
    // NOT be preventDefault()ed below - swallowing the key stops the IME from ever seeing it, which
    // is the difference between a candidate window and nothing happening at all.
    // keyCode 229 is the long-standing signal for "this key went to the IME"; isComposing is the
    // modern one, and neither is universal on its own.
    if (e.isComposing || e.keyCode === 229) return;

    queue.push({
        t: "k", h: handle, d: isDown, c: e.code, key: e.key, r: e.repeat,
        ctl: e.ctrlKey, sh: e.shiftKey, alt: e.altKey, meta: e.metaKey,
        ts: Math.round(e.timeStamp),
    });
    // Keep browser shortcuts like reload/devtools; swallow everything else so
    // Tab/Space/arrows/Backspace reach WPF instead of scrolling/navigating.
    if (isDown && !e.metaKey && e.key !== "F5" && e.key !== "F12")
        e.preventDefault();
}
