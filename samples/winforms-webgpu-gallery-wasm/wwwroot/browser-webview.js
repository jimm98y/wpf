// The browser head's web view: an <iframe> positioned over the WPF canvas.
//
// This is the one head where "overlay" is not a choice we made but the only thing that exists. An
// iframe's pixels are unreachable -- there is no way to read them back into a canvas at any price,
// cross-origin or not -- which is the concrete reason the whole IWebViewBackend seam is an overlay
// rather than a composited texture. See the remarks on that interface.
//
// It is also the head with the least authority over its own content. Everything below is either
// something the browser lets an embedder do to a frame (position it, navigate it, postMessage to it)
// or something it deliberately does not (read its title, walk its history, run script in it when it
// is cross-origin). The managed side turns the second group into explicit failures rather than
// pretending, because "the browser forbids this" is a fact an application needs told plainly.
//
// Frames are keyed by the same synthetic handle WebViewHostWindow hands the control, so the managed
// side never holds a DOM object.

const frames = new Map();      // handle -> { el, ready, lastMessage, pendingLoad }

function host() {
    return document.getElementById('wpf-host') ?? document.body;
}

// window.chrome.webview for the page, over the frame's own postMessage channel -- the vocabulary a
// WebView2 page expects, and the one every other head's shim provides (MacWebViewBackend and its
// siblings). WebBrowser.ObjectForScripting's window.external bridge is built on it too, so without
// it the bridge script threw on its first line and window.external never existed on this head.
// Page -> host is window.parent.postMessage, which the inbox below collects; host -> page arrives as
// a 'message' event from the parent and is handed to the shim's listeners.
const CHROME_WEBVIEW_SHIM = `(function () {
    if (window.chrome && window.chrome.webview) { return; }
    var listeners = [];
    window.chrome = window.chrome || {};
    window.chrome.webview = {
        postMessage: function (m) { window.parent.postMessage(typeof m === 'string' ? m : JSON.stringify(m), '*'); },
        addEventListener: function (t, f) { if (t === 'message') { listeners.push(f); } },
        removeEventListener: function (t, f) {
            if (t !== 'message') { return; }
            var i = listeners.indexOf(f); if (i >= 0) { listeners.splice(i, 1); }
        }
    };
    window.addEventListener('message', function (e) {
        if (e.source !== window.parent) { return; }
        var ev = { data: e.data, source: e.source };
        for (var i = 0; i < listeners.length; i++) { listeners[i](ev); }
    });
})();`;

function installShim(f) {
    try { f.el.contentWindow.eval(CHROME_WEBVIEW_SHIM); } catch (e) { /* cross-origin: not ours to script */ }
}

// A literal for a <script> element's body: '</script' inside the text would end it early.
function inlineScript(source) {
    return '<script>' + source.replace(/<\/script/gi, '<\\/script') + '</script>';
}

export function createFrame(handle, parentWindow) {
    if (frames.has(handle)) { return; }

    const el = document.createElement('iframe');
    el.dataset.wpfWebView = String(handle);

    // Fixed-positioned over the WPF window's canvas (browser-window.js registers each window's canvas
    // in __wpfCanvases under the window's handle), at the control's rectangle within it: see setBounds.
    el.style.position = 'fixed';
    el.style.border = '0';
    el.style.margin = '0';
    el.style.padding = '0';
    el.style.zIndex = '10';
    el.style.display = 'block';

    // Same defaults a desktop engine gives an embedded page. Scripts and same-origin are what make
    // ExecuteScript and the message bridge work at all for srcdoc content.
    el.setAttribute('sandbox',
        'allow-scripts allow-same-origin allow-forms allow-popups allow-modals allow-downloads');

    // Registered before it is inserted: an iframe with no src fires its first load DURING appendChild.
    frames.set(handle, { el: el, ready: false, lastMessage: null, parent: parentWindow });
    host().appendChild(el);

    el.addEventListener('load', () => {
        const f = frames.get(handle);
        if (f) {
            // Before the document scripts (whose load listener is registered after this one): the
            // window.external bridge among them is built on the shim.
            installShim(f);
            f.ready = true;
        }
    });
}

export function destroyFrame(handle) {
    const f = frames.get(handle);
    if (!f) { return; }
    f.el.remove();
    frames.delete(handle);
}

export function setBounds(handle, x, y, width, height, scale) {
    const f = frames.get(handle);
    if (!f) { return; }

    // The caller works in device pixels; CSS wants logical ones.
    const s = scale > 0 ? scale : (window.devicePixelRatio || 1);
    // x, y are within the WPF window; the window is a canvas somewhere in the page.
    const canvas = globalThis.__wpfCanvases?.get(f.parent);
    const origin = canvas ? canvas.getBoundingClientRect() : { left: 0, top: 0 };
    f.el.style.left = (origin.left + x / s) + 'px';
    f.el.style.top = (origin.top + y / s) + 'px';
    f.el.style.width = (width / s) + 'px';
    f.el.style.height = (height / s) + 'px';
}

export function setVisible(handle, visible) {
    const f = frames.get(handle);
    if (f) { f.el.style.display = visible ? 'block' : 'none'; }
}

export function navigate(handle, uri) {
    const f = frames.get(handle);
    if (!f) { return; }
    f.ready = false;
    f.el.removeAttribute('srcdoc');
    f.el.src = uri;
}

export function navigateToString(handle, html) {
    const f = frames.get(handle);
    if (!f) { return; }
    f.ready = false;
    // srcdoc rather than a data: URL: a data: URL is an opaque origin, which would make the frame
    // cross-origin to us and cost us script and messaging on our OWN content.
    f.el.removeAttribute('src');
    // Content we were handed is the one case where a script CAN run at document start, ahead of the
    // page's own: put the shim and the document scripts at the top of it. (They run again on load,
    // as for any page, and are written to be harmless the second time.)
    const head = inlineScript(CHROME_WEBVIEW_SHIM) +
        (documentScripts.get(handle) || []).map(inlineScript).join('');
    f.el.srcdoc = head + html;
}

export function reload(handle) {
    const f = frames.get(handle);
    if (!f) { return; }

    try {
        f.ready = false;
        f.el.contentWindow.location.reload();
    } catch (e) {
        // Cross-origin: re-assigning src is the only reload an embedder is allowed.
        f.el.src = f.el.src;
    }
}

export function stop(handle) {
    const f = frames.get(handle);
    if (!f) { return; }

    try {
        f.el.contentWindow.stop();
    } catch (e) {
        // Cross-origin frames cannot be stopped from here; nothing else is available.
    }
}

export function isReady(handle) {
    const f = frames.get(handle);
    return !!(f && f.ready);
}

/// Whether the frame's document can be reached at all. Everything script-shaped depends on this,
/// and it is false for any cross-origin page no matter what the embedder asks for.
export function isSameOrigin(handle) {
    const f = frames.get(handle);
    if (!f) { return false; }

    try {
        return !!f.el.contentWindow.document;
    } catch (e) {
        return false;
    }
}

export function getSource(handle) {
    const f = frames.get(handle);
    if (!f) { return null; }

    try {
        return f.el.contentWindow.location.href;
    } catch (e) {
        // Cross-origin: only what we asked for, which is still the honest answer to "where is it".
        return f.el.src || null;
    }
}

export function getTitle(handle) {
    const f = frames.get(handle);
    if (!f) { return null; }

    try {
        return f.el.contentWindow.document.title;
    } catch (e) {
        return null;
    }
}

/// Evaluate in the frame and return the result as JSON. Returns the sentinel below when the browser
/// will not allow it, so the managed side can raise a specific error rather than a generic one.
export const CROSS_ORIGIN = ' cross-origin';

export function executeScript(handle, script) {
    const f = frames.get(handle);
    if (!f) { return 'null'; }

    let w;

    try {
        w = f.el.contentWindow;
        if (!w.document) { return CROSS_ORIGIN; }
    } catch (e) {
        return CROSS_ORIGIN;
    }

    try {
        // Indirect eval inside the frame, so the script sees the frame's globals and not ours.
        const value = w.eval(script);
        return value === undefined ? 'null' : JSON.stringify(value) ?? 'null';
    } catch (e) {
        throw new Error(String(e && e.message ? e.message : e));
    }
}

/// Install a script into every document the frame loads. There is no browser primitive for this --
/// an embedder cannot register a document-start script in a frame it does not control -- so it is
/// re-run on each load, which is as close as the platform allows.
const documentScripts = new Map();  // handle -> [script]

export function addDocumentScript(handle, script) {
    if (!documentScripts.has(handle)) {
        documentScripts.set(handle, []);

        const f = frames.get(handle);

        if (f) {
            f.el.addEventListener('load', () => {
                for (const s of documentScripts.get(handle) || []) {
                    try { f.el.contentWindow.eval(s); } catch (e) { /* cross-origin: not possible */ }
                }
            });
        }
    }

    documentScripts.get(handle).push(script);

    // Apply to the document that is already there, if any.
    try { frames.get(handle).el.contentWindow.eval(script); } catch (e) { /* not yet, or cross-origin */ }
}

export function postMessage(handle, json) {
    const f = frames.get(handle);
    if (!f || !f.el.contentWindow) { return; }

    // '*' because the frame may be any origin, and the payload is the application's own message.
    f.el.contentWindow.postMessage(JSON.parse(json), '*');
}

/// Messages from frames, drained by the managed side. Queued rather than pushed because the managed
/// callback would otherwise run on the browser's event turn rather than the dispatcher's. One queue
/// per frame, each drained only by that frame's own backend.
window.addEventListener('message', (e) => {
    for (const [, f] of frames) {
        if (e.source === f.el.contentWindow) {
            (f.inbox ??= []).push(typeof e.data === 'string' ? e.data : JSON.stringify(e.data));
            break;
        }
    }
});

export function takeMessage(handle) {
    const f = frames.get(handle);
    if (!f || !f.inbox || f.inbox.length === 0) { return null; }
    return f.inbox.shift();
}

export function clearData() {
    // An embedder cannot clear another origin's storage; the frames it owns are dropped instead,
    // which is the only part of "clear browsing data" this platform actually grants.
    for (const [, f] of frames) {
        f.el.removeAttribute('src');
        f.el.removeAttribute('srcdoc');
    }
}
