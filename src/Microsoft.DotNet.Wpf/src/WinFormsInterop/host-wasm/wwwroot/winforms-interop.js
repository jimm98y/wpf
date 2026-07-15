// JS half of the WinForms-on-WebGPU browser host. Creates the single presentation <canvas>
// (registered in globalThis.__wpfCanvases so the C# BrowserInterop.CreateSurface finds it) and
// queues DOM input, drained each pump tick by BrowserHost and fed to the XplatUIWebGpu driver.
// Mirrors the WPF browser-window.js pattern but for one Form filling the canvas.

const queue = [];
let theCanvas = null;
const HANDLE = 1;

function canvases() {
    if (!globalThis.__wpfCanvases) globalThis.__wpfCanvases = new Map();
    return globalThis.__wpfCanvases;
}

export function dpr() { return window.devicePixelRatio || 1; }

// Mouse position in CSS px relative to the canvas top-left == WinForms point space (the Form
// fills the canvas at the origin, so canvas-relative == the driver's screen coords).
function pos(e) {
    const r = theCanvas.getBoundingClientRect();
    return { x: Math.round(e.clientX - r.left), y: Math.round(e.clientY - r.top) };
}

function pushMouse(kind, e) {
    const p = pos(e);
    queue.push({ t: "m", k: kind, x: p.x, y: p.y, b: e.button ?? 0, l: (e.buttons & 1) ? 1 : 0 });
}

function pushKey(isDown, e) {
    queue.push({ t: "k", d: isDown ? 1 : 0, c: e.code, key: e.key, r: e.repeat ? 1 : 0 });
    // Keep the browser from scrolling/навigating on keys the app consumes.
    if (isDown && !e.metaKey && e.key !== "F5" && e.key !== "F12") e.preventDefault();
}

export function createCanvas(widthPoints, heightPoints) {
    const d = dpr();
    const canvas = document.createElement("canvas");
    canvas.width = Math.round(widthPoints * d);
    canvas.height = Math.round(heightPoints * d);
    canvas.style.width = widthPoints + "px";
    canvas.style.height = heightPoints + "px";
    canvas.style.display = "block";
    canvas.style.outline = "none";
    canvas.tabIndex = 0;                 // so the canvas can take keyboard focus
    const host = document.getElementById("wpf-host") ?? document.body;
    host.appendChild(canvas);
    document.getElementById("wf-status")?.remove();   // window is up; the async pump keeps running
    theCanvas = canvas;
    canvases().set(HANDLE, canvas);

    canvas.addEventListener("mousemove", (e) => pushMouse(0, e));
    canvas.addEventListener("mousedown", (e) => { canvas.focus(); pushMouse(1, e); });
    window.addEventListener("mouseup", (e) => pushMouse(2, e));   // window: catch release outside canvas
    canvas.addEventListener("contextmenu", (e) => e.preventDefault());
    canvas.addEventListener("wheel", (e) => {
        e.preventDefault();
        // DOM deltaY>0 = scroll down; Win32 WM_MOUSEWHEEL delta is +120 per notch scrolling UP.
        const p = pos(e);
        queue.push({ t: "w", x: p.x, y: p.y, d: e.deltaY > 0 ? -120 : 120 });
    }, { passive: false });
    canvas.addEventListener("keydown", (e) => pushKey(true, e));
    canvas.addEventListener("keyup", (e) => pushKey(false, e));
    canvas.focus();
    return HANDLE;
}

export function drainEvents() {
    if (queue.length === 0) return "";
    return JSON.stringify(queue.splice(0));
}
