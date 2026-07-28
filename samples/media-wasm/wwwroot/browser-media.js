// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// browser-media.js -- the HTML5 <video> backend for WPF MediaElement on the WebAssembly head.
// Registered as the 'wpfBrowserMedia' module import (main.js: setModuleImports('wpfBrowserMedia', ...)),
// driven by BrowserMediaBackend (PresentationCore) which mirrors the macOS AVFoundation backend. The
// browser decodes audio+video, plays audio, and keeps A/V in sync; C# pulls each video frame's pixels
// (drawImage the <video> onto an offscreen canvas -> getImageData) into a managed buffer and ships them
// to the managed WebGPU compositor via the same SendVideoFrame seam used on macOS.

function videos() { return (globalThis.__wpfVideos ??= new Map()); }

export function createVideo(handle) {
    const v = document.createElement('video');
    v.style.display = 'none';
    v.crossOrigin = 'anonymous';
    v.playsInline = true;
    v.preload = 'auto';
    const entry = { v, canvas: document.createElement('canvas'), ctx: null, ended: false, failed: false };
    v.addEventListener('ended', () => { entry.ended = true; });
    v.addEventListener('error', () => { entry.failed = true; });
    document.body.appendChild(v);
    videos().set(handle, entry);
}

export function open(handle, url) {
    const e = videos().get(handle); if (!e) return;
    e.ended = false; e.failed = false;
    // WPF may hand us a relative or file: URL; resolve to something the browser can fetch (page-relative).
    let src = url;
    try {
        if (url.startsWith("file:")) src = new URL(url.replace(/^file:\/*/, ''), document.baseURI).href;
        else src = new URL(url, document.baseURI).href;
    } catch { /* keep url as-is */ }
    console.log("wpfBrowserMedia open: " + url + " -> " + src);
    e.v.src = src;
    e.v.load();
}

export function setRate(handle, rate) {
    const e = videos().get(handle); if (!e) return;
    if (rate <= 0) { e.v.pause(); }
    else { e.v.playbackRate = rate; e.v.play().catch(() => { /* autoplay may be blocked until a user gesture */ }); }
}

export function seek(handle, seconds) { const e = videos().get(handle); if (e) e.v.currentTime = seconds; }

export function setVolume(handle, vol) { const e = videos().get(handle); if (e) e.v.volume = Math.max(0, Math.min(1, vol)); }

// 0 = loading/unknown, 1 = ready (has current frame), 2 = error.
export function getStatus(handle) {
    const e = videos().get(handle); if (!e) return 2;
    if (e.failed || e.v.error) return 2;
    return e.v.readyState >= 2 /* HAVE_CURRENT_DATA */ ? 1 : 0;
}

export function getWidth(handle) { const e = videos().get(handle); return e ? (e.v.videoWidth | 0) : 0; }
export function getHeight(handle) { const e = videos().get(handle); return e ? (e.v.videoHeight | 0) : 0; }
export function getDuration(handle) { const e = videos().get(handle); const d = e ? e.v.duration : 0; return (isFinite(d) && d > 0) ? d : 0; }
export function getPosition(handle) { const e = videos().get(handle); return e ? e.v.currentTime : 0; }
export function isEnded(handle) {
    const e = videos().get(handle); if (!e) return false;
    return e.ended || (isFinite(e.v.duration) && e.v.duration > 0 && e.v.currentTime >= e.v.duration - 0.05 && e.v.paused);
}
export function hasAudio(handle) {
    const e = videos().get(handle); if (!e) return false;
    const v = e.v;
    if (v.audioTracks && v.audioTracks.length) return true;
    if (v.mozHasAudio) return true;
    if (typeof v.webkitAudioDecodedByteCount === 'number') return v.webkitAudioDecodedByteCount > 0;
    return false;
}

// Fill `buffer` (a MemoryView over a managed byte[]) with the current frame as top-down BGRA32
// (the SendVideoFrame seam expects BGRA and swizzles to RGBA, matching the macOS CVPixelBuffer path).
// Returns 1 if a frame was written, else 0.
export function getFrame(handle, buffer) {
    const e = videos().get(handle); if (!e) return 0;
    const w = e.v.videoWidth | 0, h = e.v.videoHeight | 0;
    if (w === 0 || h === 0) return 0;
    const n = w * h * 4;
    if (buffer.byteLength < n) return 0;
    if (e.canvas.width !== w || e.canvas.height !== h) {
        e.canvas.width = w; e.canvas.height = h;
        e.ctx = e.canvas.getContext('2d', { willReadFrequently: true });
    }
    e.ctx.drawImage(e.v, 0, 0, w, h);
    const rgba = e.ctx.getImageData(0, 0, w, h).data; // RGBA
    const bgra = new Uint8Array(n);
    for (let i = 0; i < n; i += 4) {
        bgra[i] = rgba[i + 2];     // B
        bgra[i + 1] = rgba[i + 1]; // G
        bgra[i + 2] = rgba[i];     // R
        bgra[i + 3] = 255;         // opaque
    }
    buffer.set(bgra);
    return 1;
}

export function destroy(handle) {
    const e = videos().get(handle); if (!e) return;
    try { e.v.pause(); e.v.removeAttribute('src'); e.v.load(); e.v.remove(); } catch { /* ignore */ }
    videos().delete(handle);
}
