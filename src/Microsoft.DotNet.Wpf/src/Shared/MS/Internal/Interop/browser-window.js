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


// ---- Accessibility mirror ---------------------------------------------------
//
// A WPF window is a <canvas>, and a canvas has no accessible content -- to a screen reader the whole
// app is one blank graphic. So the managed side pushes the automation tree over here and this builds
// a parallel DOM of transparent elements, one per accessible node, positioned exactly over the
// pixels the compositor drew. Screen readers, tab order, browser zoom and axe all then work on real
// elements without knowing the pixels came from WebGPU.
//
// The mirror is inert: pointer-events are off so it never intercepts a click meant for the canvas,
// and the elements carry no text, only ARIA attributes. Keyboard activation IS routed back, because
// that is the only way a keyboard-only user can operate the app at all.

let a11yHost = null;
const a11yNodes = new Map();   // id -> element

function a11yEnsureHost() {
    if (a11yHost) return a11yHost;
    a11yHost = document.createElement("div");
    a11yHost.id = "wpf-a11y";
    // Covers the host but never eats input; individual nodes re-enable pointer events only if they
    // are focusable, so a screen reader's "activate" still lands.
    a11yHost.style.cssText = "position:absolute;left:0;top:0;width:0;height:0;pointer-events:none;";
    host().appendChild(a11yHost);
    return a11yHost;
}

/// Whether the page wants a mirror at all. There is no way to detect a screen reader on the web, so
/// the mirror is built for everyone unless the page opts out.
export function a11yIsEnabled() {
    const meta = document.querySelector('meta[name="wpf-a11y"]');
    return !(meta && meta.getAttribute("content") === "off");
}

export function a11ySync(json) {
    const nodes = JSON.parse(json);
    const host = a11yEnsureHost();
    const seen = new Set();
    const dpr = window.devicePixelRatio || 1;

    for (const n of nodes) {
        seen.add(n.id);
        let el = a11yNodes.get(n.id);
        if (!el) {
            el = document.createElement("div");
            el.dataset.wpfNode = String(n.id);
            a11yNodes.set(n.id, el);
            host.appendChild(el);

            el.addEventListener("click", () => pushA11y(n.id, 0));
            el.addEventListener("keydown", (e) => {
                if (e.key === "Enter" || e.key === " ") { pushA11y(n.id, 0); e.preventDefault(); }
            });
            el.addEventListener("focus", () => pushA11y(n.id, 1));
        }

        if (n.r) {
            el.setAttribute("role", n.r);
            if (n.n) el.setAttribute("aria-label", n.n); else el.removeAttribute("aria-label");
            if (el.firstChild) el.textContent = "";
        } else {
            // No ARIA role means a generic element, and a generic element's aria-label is IGNORED
            // by every browser -- name-from-author is prohibited for it. Static text has to become
            // actual text, or a label in a WPF app is simply absent from the accessibility tree.
            el.removeAttribute("role");
            el.removeAttribute("aria-label");
            const text = n.n || "";
            if (el.textContent !== text) el.textContent = text;
        }

        setAttr(el, "aria-disabled", n.dis ? "true" : null);
        setAttr(el, "aria-checked", n.chk === 2 ? "mixed" : n.chk === 1 ? "true" : (n.r === "checkbox" || n.r === "radio") ? "false" : null);
        setAttr(el, "aria-expanded", n.exp === 1 ? "true" : n.exp === 0 ? "false" : null);
        setAttr(el, "aria-selected", n.sel ? "true" : null);
        setAttr(el, "aria-valuenow", n.v !== undefined ? String(n.v) : null);
        setAttr(el, "aria-valuemin", n.vmin !== undefined ? String(n.vmin) : null);
        setAttr(el, "aria-valuemax", n.vmax !== undefined ? String(n.vmax) : null);
        setAttr(el, "aria-valuetext", n.vt ?? null);

        // Focusable and clickable only where WPF says there is something to do; everything else
        // stays out of the tab order so keyboard navigation matches what the app actually offers.
        if (n.foc || n.act) { el.setAttribute("tabindex", "0"); el.style.pointerEvents = "auto"; }
        else { el.removeAttribute("tabindex"); el.style.pointerEvents = "none"; }

        // Bounds arrive in device pixels; CSS wants points.
        el.style.cssText += "";
        el.style.position = "fixed";
        el.style.left = `${n.x / dpr}px`;
        el.style.top = `${n.y / dpr}px`;
        el.style.width = `${n.w / dpr}px`;
        el.style.height = `${n.h / dpr}px`;
        el.style.opacity = "0";
        el.style.overflow = "hidden";
    }

    for (const [id, el] of a11yNodes) {
        if (!seen.has(id)) { el.remove(); a11yNodes.delete(id); }
    }
}

export function a11ySetFocus(nodeId) {
    const el = a11yNodes.get(nodeId);
    if (el && document.activeElement !== el) el.focus({ preventScroll: true });
}

function setAttr(el, name, value) {
    if (value === null || value === undefined) el.removeAttribute(name);
    else el.setAttribute(name, value);
}

function pushA11y(nodeId, kind) {
    queue.push({ t: "a", id: nodeId, k: kind });
}

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

// ---- drag and drop (drop target only) ------------------------------------
//
// The browser can RECEIVE a drag -- text or a URL dragged in from another tab, another
// application, or the desktop -- but it cannot start one on WPF's behalf: an HTML5 drag
// begins from a dragstart event on a draggable element, and DoDragDrop is called from
// managed code in the middle of a mouse gesture, which is not that. Drags that stay
// inside the application therefore run in ManagedDragLoop instead, and this half is
// purely about drags arriving from outside.
//
// TWO BROWSER RULES SHAPE EVERYTHING BELOW:
//
//   1. A drop only happens where dragover called preventDefault(), and that decision is
//      SYNCHRONOUS. WPF's answer is not: the hit-test runs when the dispatcher next
//      drains this queue. So the last effect WPF reported is cached and used to answer
//      the next dragover -- accurate within one frame, which is imperceptible while a
//      pointer is moving, and self-correcting because dragover fires continuously.
//   2. getData() only returns anything during the drop event itself. The strings are
//      therefore read out here, at drop, and travel with the queued event; before that,
//      only the TYPE list is knowable, which is exactly what WPF needs to answer
//      DragEnter/DragOver anyway.
//
// Dropped FILES are deliberately not mapped. WPF's FileDrop format promises filesystem
// paths and a browser never exposes them, so an app asking for FileDrop gets nothing
// rather than something that looks like a path and is not one.

let dragEffect = 0;          // last effect WPF reported: 0 none, 1 copy, 2 move, 4 link
let dragInside = 0;          // handle the drag is currently over, 0 when outside

export function setDragEffect(effect) { dragEffect = effect | 0; }

function dropEffectName(effect) {
    if (effect & 2) return "move";
    if (effect & 1) return "copy";
    if (effect & 4) return "link";
    return "none";
}

// What the SOURCE permits, as WPF effects. effectAllowed is a fixed vocabulary.
function allowedEffects(transfer) {
    switch (transfer?.effectAllowed) {
        case "copy": return 1;
        case "move": return 2;
        case "link": return 4;
        case "copyMove": return 3;
        case "copyLink": return 5;
        case "linkMove": return 6;
        case "none": return 0;
        default: return 7;   // "all", "uninitialized", or absent
    }
}

function pushDrag(kind, e, data) {
    let handle = topmostWindowAt(e.clientX, e.clientY);
    if (!handle) return 0;

    const r = windows.get(handle).canvas.getBoundingClientRect();
    queue.push({
        t: "d", k: kind, h: handle,
        x: Math.round((e.clientX - r.left) * dpr()),
        y: Math.round((e.clientY - r.top) * dpr()),
        a: allowedEffects(e.dataTransfer),
        // Types the drag offers. Browsers already speak MIME here, which is the
        // vocabulary the WPF side maps from, so these travel unchanged.
        m: e.dataTransfer ? Array.from(e.dataTransfer.types) : [],
        v: data ?? null,
    });
    return handle;
}

function installDragListeners() {
    window.addEventListener("dragenter", (e) => {
        e.preventDefault();
        const handle = pushDrag(0, e);
        if (handle) dragInside = handle;
    });

    window.addEventListener("dragover", (e) => {
        const handle = pushDrag(1, e);
        if (!handle) return;

        // Rule 1: this is what makes the drop possible at all, and what the browser
        // draws its cursor from.
        if (dragEffect !== 0) {
            e.preventDefault();
            e.dataTransfer.dropEffect = dropEffectName(dragEffect);
        }
    });

    window.addEventListener("dragleave", (e) => {
        // dragleave also fires when moving between elements INSIDE the canvas, where the
        // drag has not really left. Only a leave that lands outside every window counts.
        if (topmostWindowAt(e.clientX, e.clientY)) return;
        if (!dragInside) return;

        queue.push({ t: "d", k: 2, h: dragInside, x: 0, y: 0, a: 0, m: [], v: null });
        dragInside = 0;
        dragEffect = 0;
    });

    window.addEventListener("drop", (e) => {
        e.preventDefault();

        // Rule 2: read everything now, while the data is still readable.
        const data = {};
        if (e.dataTransfer) {
            for (const type of e.dataTransfer.types) {
                if (type === "Files") continue;      // no paths exist to hand over
                data[type] = e.dataTransfer.getData(type);
            }
        }

        pushDrag(3, e, data);
        dragInside = 0;
        dragEffect = 0;
    });
}

// A touch/pen CONTACT, as distinct from the mouse the browser also synthesizes for the first
// finger. Pointer events carry an id and a pointerType, so several can be alive at once -- which is
// the whole thing the mouse cannot express.
//
// The synthesized mouse is deliberately left alone: a TouchDevice raises the Touch events and drives
// Manipulation but does not promote itself to the mouse, so suppressing the browser's compatibility
// mouse events would gain pinch and lose Button.Click.
function pushContact(kind, e) {
    let handle = topmostWindowAt(e.clientX, e.clientY);
    if (!handle) {
        for (const [h, w] of windows) { if (!w.borderless) { handle = h; break; } }
        if (!handle) return;
    }
    const r = windows.get(handle).canvas.getBoundingClientRect();
    queue.push({
        t: "tc", k: kind, h: handle, id: e.pointerId | 0,
        x: Math.round((e.clientX - r.left) * dpr()),
        y: Math.round((e.clientY - r.top) * dpr()),
        // A pen reports real pressure; a finger reports a constant 0.5 or 1 that measures nothing,
        // and the mouse reports 0.5 while a button is down. Only the pen's is passed on, so
        // StylusPoint.PressureFactor does not carry a number nobody measured.
        p: e.pointerType === "pen" ? e.pressure : -1,
        // tiltX/tiltY are degrees from vertical and are what the seam takes directly. Zero from a
        // finger means "flat", not "measured as flat", so only a pen's are sent.
        tx: e.pointerType === "pen" ? (e.tiltX ?? 0) : undefined,
        ty: e.pointerType === "pen" ? (e.tiltY ?? 0) : undefined,
        // The inverted (eraser) end. Pointer Events has no flag for it: it arrives as a fifth
        // BUTTON, bit 5 of buttons, in place of the tip's bit 0. Sent only when set, so the
        // ordinary pen and finger payloads are unchanged.
        inv: e.pointerType === "pen" && (e.buttons & 32) !== 0 ? true : undefined,
        ts: Math.round(e.timeStamp),
    });
}

function installListeners() {
    if (listenersInstalled) return;
    listenersInstalled = true;

    installDragListeners();
    installClipboardListeners();

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

    // Touch and pen only: a mouse already has its own path above, and routing it through both would
    // deliver every click twice.
    const isContact = (e) => e.pointerType === "touch" || e.pointerType === "pen";
    window.addEventListener("pointerdown", (e) => { if (isContact(e)) pushContact(1, e); });
    window.addEventListener("pointermove", (e) => { if (isContact(e)) pushContact(0, e); });
    window.addEventListener("pointerup", (e) => { if (isContact(e)) pushContact(2, e); });
    window.addEventListener("pointercancel", (e) => { if (isContact(e)) pushContact(3, e); });

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

// ---- clipboard --------------------------------------------------------------------------------
//
// WPF's Clipboard API is synchronous and the browser's is not, which is normally where a port gives
// up. It works here because of an accident of the existing design that is worth stating plainly:
// key events are QUEUED by the listener and drained by the dispatcher on the next animation frame,
// not handed to WPF inside the DOM handler. For a Ctrl+V that gives the order
//
//     keydown (queued)  ->  paste (fills the cache below)  ->  rAF  ->  WPF handles the keydown
//
// so by the time WPF asks Clipboard.GetText the cache is already warm. Nothing needed reordering.
//
// The cache is filled from exactly two places, and deliberately NOT from navigator.clipboard.read:
// reading the system clipboard needs a permission prompt, and firing one every time an application
// happens to call Clipboard.ContainsText would be intolerable. So:
//
//   * the paste event, which is a real user gesture and needs no permission -- this is how text
//     copied in ANOTHER application arrives;
//   * this module's own writes, which is how in-application copy/paste works.
//
// What that leaves genuinely unavailable is reading the system clipboard with no paste gesture at
// all. No browser permits it without a prompt, so no implementation of this API can offer it.
//
// Writing goes out through navigator.clipboard.writeText, which needs TRANSIENT USER ACTIVATION.
// The rAF hop above costs about 16ms of a five-second window, so a Ctrl+C is comfortably inside it.
// execCommand("copy") is kept as the fallback for browsers that refuse the async API.

let clipText = null;         // last text seen or written, null when nothing is known
let clipPng = null;          // ditto, as a Uint8Array

function installClipboardListeners() {
    document.addEventListener("paste", (e) => {
        const data = e.clipboardData;
        if (!data) return;

        const text = data.getData("text/plain");
        if (text) clipText = text;

        for (const item of data.items || []) {
            if (item.type !== "image/png") continue;
            const file = item.getAsFile();
            if (!file) continue;
            // Asynchronous, so this lands one frame later than the text. An image pasted twice is
            // therefore right the second time; there is no synchronous way to read a Blob.
            file.arrayBuffer()
                .then((buffer) => { clipPng = new Uint8Array(buffer); })
                .catch(() => {});
        }
    });
}

/// Last-resort copy for browsers that refuse navigator.clipboard: a hidden textarea plus the
/// deprecated execCommand, which is synchronous and still universally implemented.
function copyViaTextarea(value) {
    try {
        const area = document.createElement("textarea");
        area.value = value;
        area.style.cssText = "position:fixed;left:-10000px;top:0;opacity:0;";
        document.body.appendChild(area);
        area.select();
        document.execCommand("copy");
        area.remove();
    } catch (e) {
        console.warn("WPF clipboard write failed:", e);
    }
}

export function clipboardSetText(value) {
    clipText = value ?? "";
    try {
        const written = navigator.clipboard?.writeText(clipText);
        if (written) written.catch(() => copyViaTextarea(clipText));
        else copyViaTextarea(clipText);
    } catch {
        copyViaTextarea(clipText);
    }
}

export function clipboardGetText() {
    return clipText ?? "";
}

export function clipboardHasText() {
    return clipText !== null && clipText !== "";
}

export function clipboardClear() {
    clipText = null;
    clipPng = null;
    // Also empty the SYSTEM clipboard, or Clipboard.Clear() would only forget locally while another
    // application could still paste what this one put there.
    try { navigator.clipboard?.writeText("")?.catch(() => {}); } catch {}
}

export function clipboardSetPng(bytes) {
    clipPng = bytes instanceof Uint8Array ? bytes : new Uint8Array(bytes);
    try {
        if (!navigator.clipboard?.write || typeof ClipboardItem === "undefined") return;
        const blob = new Blob([clipPng], { type: "image/png" });
        navigator.clipboard.write([new ClipboardItem({ "image/png": blob })]).catch(() => {});
    } catch (e) {
        console.warn("WPF clipboard image write failed:", e);
    }
}

/// Base64 rather than a byte array: the return direction of the array marshaller is the fiddly one,
/// and an image on a clipboard is small enough that the encoding costs nothing worth measuring.
export function clipboardGetPng() {
    if (!clipPng || clipPng.length === 0) return "";
    let binary = "";
    for (let i = 0; i < clipPng.length; i++) binary += String.fromCharCode(clipPng[i]);
    return btoa(binary);
}

export function clipboardHasPng() {
    return clipPng !== null && clipPng.length > 0;
}

// ---- file dialogs -----------------------------------------------------------------------------
//
// A browser has no file SYSTEM to show a path from, so "open a file" and "save a file" mean
// something different here and the difference is not hidden:
//
//   open  an <input type=file> click. The user picks from their real machine; what comes back is a
//         NAME and the BYTES, never a path -- the page is never told where the file lives. The bytes
//         are written into the wasm virtual file system under /wpf-picked, and THAT path is what
//         FileName returns, so an application that does File.ReadAllBytes(dlg.FileName) works
//         unchanged. This is the same trick the head already uses to make bundled fonts visible.
//
//   save  a download. There is no way to write to a chosen location and no way to learn where the
//         browser put it, so SaveFileDialog hands back a path inside the same virtual file system;
//         the application writes there as usual and the head then offers the result as a download.
//         Calling code does not change; only the last step is a browser gesture rather than a write.
//
// showOpenFilePicker (the File System Access API) would give a real handle and a genuine save, but
// it is Chromium-only and needs a secure context, so the input element is what actually works
// everywhere. The picker is deliberately NOT reused between calls: a fresh element each time avoids
// a stale change listener firing an old promise.

let pickedSequence = 0;

/// Opens the file picker and resolves to a JSON envelope the managed side parses:
///   { "ok": bool, "names": [ "/wpf-picked/3/report.csv", ... ] }
/// Rejection is never propagated: a cancelled picker is an ordinary answer, not an error.
export function pickFilesAsync(accept, multiple, directory) {
    return new Promise((resolve) => {
        try {
            const input = document.createElement("input");
            input.type = "file";
            if (accept) input.accept = accept;
            if (multiple) input.multiple = true;
            // Directory selection is webkitdirectory everywhere that supports it at all.
            if (directory) { input.webkitdirectory = true; input.setAttribute("webkitdirectory", ""); }
            input.style.cssText = "position:fixed;left:-10000px;top:0;opacity:0;";

            let settled = false;
            // Set the instant a change event arrives, BEFORE the files are read. Reading is async
            // and a large file takes longer than the cancellation fallback's timeout, so without
            // this a slow read would be reported as a cancellation while it was still in progress.
            let changed = false;

            const finish = (value) => {
                if (settled) return;
                settled = true;
                input.remove();
                resolve(JSON.stringify(value));
            };

            input.addEventListener("change", async () => {
                changed = true;
                try {
                    const files = Array.from(input.files || []);
                    if (files.length === 0) { finish({ ok: false, names: [] }); return; }

                    const dir = `/wpf-picked/${++pickedSequence}`;
                    mkdirp(dir);

                    const names = [];
                    for (const file of files) {
                        const bytes = new Uint8Array(await file.arrayBuffer());
                        // webkitRelativePath is set only for a directory pick, and it is the only
                        // way to keep the tree's shape rather than flattening it.
                        const relative = file.webkitRelativePath || file.name;
                        const path = `${dir}/${relative}`;
                        mkdirp(path.slice(0, path.lastIndexOf("/")));
                        writeFile(path, bytes);
                        names.push(path);
                    }
                    finish({ ok: true, names });
                } catch (e) {
                    console.warn("WPF file picker failed:", e);
                    finish({ ok: false, names: [] });
                }
            });

            // "cancel" is the modern signal and is not universal; without it a cancelled picker
            // would leave the promise pending for ever and the awaiting dialog would never return.
            // The focus fallback fires when the window regains focus with no change event, which is
            // what a cancellation looks like in a browser that lacks the event.
            input.addEventListener("cancel", () => finish({ ok: false, names: [] }));
            window.addEventListener("focus", () => {
                setTimeout(() => { if (!changed) finish({ ok: false, names: [] }); }, 500);
            }, { once: true });

            document.body.appendChild(input);
            input.click();
        } catch (e) {
            console.warn("WPF file picker could not open:", e);
            resolve(JSON.stringify({ ok: false, names: [] }));
        }
    });
}

/// Reserves a path in the virtual file system for a save, and returns it. Nothing is written here:
/// the application writes to the path, then calls offerDownload.
export function reserveSavePath(suggestedName) {
    const dir = `/wpf-picked/${++pickedSequence}`;
    mkdirp(dir);
    return `${dir}/${suggestedName || "download"}`;
}

/// Hands a file the application has just written to the browser as a download.
export function offerDownload(path, mimeType) {
    try {
        const bytes = readFile(path);
        if (!bytes) return false;

        const blob = new Blob([bytes], { type: mimeType || "application/octet-stream" });
        const url = URL.createObjectURL(blob);

        const link = document.createElement("a");
        link.href = url;
        link.download = path.slice(path.lastIndexOf("/") + 1);
        link.style.cssText = "position:fixed;left:-10000px;top:0;opacity:0;";
        document.body.appendChild(link);
        link.click();
        link.remove();

        // Revoked on a timer rather than immediately: revoking before the browser has started the
        // download cancels it in Firefox.
        setTimeout(() => URL.revokeObjectURL(url), 10000);
        return true;
    } catch (e) {
        console.warn("WPF download failed:", e);
        return false;
    }
}

// The wasm virtual file system, reached through whichever handle this runtime exposes. Module.FS is
// the long-standing one; globalThis.FS appears when the runtime is built with FS exported.
function fs() {
    return globalThis.Module?.FS ?? globalThis.FS ?? null;
}

function mkdirp(path) {
    const f = fs();
    if (!f || !path) return;
    let built = "";
    for (const part of path.split("/")) {
        if (!part) continue;
        built += "/" + part;
        try { f.mkdir(built); } catch { /* already there */ }
    }
}

function writeFile(path, bytes) {
    const f = fs();
    if (!f) throw new Error("no wasm file system to write the picked file into");
    f.writeFile(path, bytes);
}

function readFile(path) {
    const f = fs();
    if (!f) return null;
    try { return f.readFile(path); } catch { return null; }
}

// ---- printing ---------------------------------------------------------------------------------

/// Prints a rendered PDF through the browser's own print preview.
///
/// The document goes into a HIDDEN IFRAME and that frame is printed, rather than calling
/// window.print() on the page. The difference is the whole point: this app's page is one canvas, so
/// printing the page would produce a screenshot of the window at screen resolution, clipped to the
/// viewport, with none of the pagination the application just did. Printing the frame prints the
/// PDF, which is what the pages actually are.
///
/// Returns whether the preview was OPENED. Whether the user then printed, saved to PDF or cancelled
/// is not observable: window.print() returns nothing and reports nothing.
export function printDocument(jobName, bytes) {
    try {
        const blob = new Blob([bytes], { type: "application/pdf" });
        const url = URL.createObjectURL(blob);

        // A previous job's frame is removed first. Leaving them accumulates an iframe and a blob URL
        // per print, and a blob URL pins its data in memory until it is revoked.
        const existing = document.getElementById("wpf-print-frame");
        if (existing) {
            if (existing.dataset.url) URL.revokeObjectURL(existing.dataset.url);
            existing.remove();
        }

        const frame = document.createElement("iframe");
        frame.id = "wpf-print-frame";
        frame.dataset.url = url;
        frame.title = jobName || "";

        // Not display:none. A frame that is not laid out is not rendered, and a frame that is not
        // rendered has nothing to print -- Safari and Firefox both produce a blank job. Off-screen
        // and zero-opacity keeps it laid out and invisible.
        frame.style.cssText =
            "position:fixed;left:-10000px;top:0;width:1px;height:1px;opacity:0;border:0;";

        frame.onload = () => {
            try {
                // focus() first: without it Chrome prints the PARENT document, because print() acts
                // on the focused frame rather than on the one it was called through.
                frame.contentWindow.focus();
                frame.contentWindow.print();
            } catch (e) {
                console.warn("WPF print failed:", e);
            }
        };

        frame.src = url;
        document.body.appendChild(frame);
        return true;
    } catch (e) {
        console.warn("WPF print could not start:", e);
        return false;
    }
}
