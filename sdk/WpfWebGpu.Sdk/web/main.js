// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

import { dotnet } from './_framework/dotnet.js'
import * as wgpuInterop from './wgpu-interop.js'
import * as wpfBrowserWindow from './browser-window.js'
import * as wpfBrowserMedia from './browser-media.js'
import * as wpfBrowserWebView from './browser-webview.js'

const status = document.getElementById('wpf-status');

// Which fonts to mount into the wasm filesystem. The build writes fonts/index.json listing exactly
// what it staged (see WpfWebGpuScaffoldWeb in Sdk.targets), because a browser cannot list a directory
// over HTTP and this file otherwise has to guess. It used to guess, in a hand-written array, and the
// guess drifted: Selawik -- the first substitute the font stack asks for in place of Segoe UI -- was
// missing here for as long as it has been bundled, so all UI text quietly fell through to Liberation.
//
// FALLBACK is only for a wwwroot staged by an older SDK, which has no manifest to read. It is
// deliberately the minimum that keeps an app legible rather than a second copy of the bundle: text,
// the Fluent icon glyphs, and CJK.
const FALLBACK_FONTS = [
    'LiberationSans-Regular.ttf', 'LiberationSans-Bold.ttf', 'LiberationSans-Italic.ttf', 'LiberationSans-BoldItalic.ttf',
    'Selawik-Regular.ttf', 'Selawik-Bold.ttf', 'Symbols.ttf', 'NotoSansCJK-Regular.ttc',
];

async function fontList() {
    try {
        const resp = await fetch('./fonts/index.json');
        if (resp.ok) {
            const names = await resp.json();
            if (Array.isArray(names) && names.length) return names;
        }
    } catch { /* no manifest: an older wwwroot, handled below */ }
    console.warn('fonts/index.json missing or empty; falling back to a minimal font set. Rebuild to regenerate it.');
    return FALLBACK_FONTS;
}

// The data protection keys (the ProtectedData shim's DPAPI master keys, one per scope): 32 random bytes
// each, made on the first visit and kept in the origin's IndexedDB, so that what an app protects -- a
// saved password, MSAL's token cache -- can still be read after a reload. The wasm file system is memory
// only; the keys are put into it before the app starts (WPFWEBGPU_DPAPI_DIR). Under a Web Lock, so two
// tabs opening the app for the first time cannot each make a key and keep the other's. Null when the
// page cannot store anything (a private window may refuse): the shim then makes keys that last the page.
const DPAPI_DIR = '/.wpfwebgpu/dpapi';
const DPAPI_KEYS = ['user.key', 'machine.key'];

function idb(request) {
    return new Promise((resolve, reject) => { request.onsuccess = () => resolve(request.result); request.onerror = () => reject(request.error); });
}

async function dpapiKeys() {
    try {
        const open = indexedDB.open('wpfwebgpu', 1);
        open.onupgradeneeded = () => open.result.createObjectStore('dpapi');
        const db = await idb(open);
        const load = async () => {
            const keys = {};
            for (const name of DPAPI_KEYS) {
                let key = await idb(db.transaction('dpapi').objectStore('dpapi').get(name));
                if (key === undefined) {
                    key = crypto.getRandomValues(new Uint8Array(32));
                    await idb(db.transaction('dpapi', 'readwrite').objectStore('dpapi').add(key, name));
                }
                if (!(key instanceof Uint8Array) || key.length !== 32) throw new Error(`stored key ${name} is damaged`);
                keys[name] = key;
            }
            return keys;
        };
        const keys = navigator.locks ? await navigator.locks.request('wpfwebgpu-dpapi', load) : await load();
        db.close();
        // ask the browser not to evict them under storage pressure (it may decline; they stay until then)
        navigator.storage?.persist?.().catch(() => { });
        return keys;
    } catch (e) {
        console.warn('WpfWebGpu: data protection keys cannot be stored; protected data will not survive a reload:', e);
        return null;
    }
}

try {
    const dpapi = await dpapiKeys();
    // Gallery modes (e.g. ?args=states to auto-open the combo popup) pass through
    // to Program.Main via the query string.
    const params = new URLSearchParams(location.search);
    const args = params.get('args');
    let builder = dotnet
        .withEnvironmentVariable('WPF_USE_WEBGPU_COMPOSITION', '1')
        .withEnvironmentVariable('WPFWEBGPU_DPAPI_DIR', DPAPI_DIR)
        .withEnvironmentVariable('WPF_WEBGPU_SINK_LOG', '/sink.log');
    if (params.has('perf')) builder = builder.withEnvironmentVariable('WPF_WEBGPU_PERF_CONSOLE', '1');
    if (params.has('firstchance')) builder = builder.withEnvironmentVariable('WPF_WEBGPU_LOG_FIRST_CHANCE', params.get('firstchance') === 'stack' ? 'stack' : '1');
    // A page has no environment block, so the renderer's knobs come from the URL instead:
    //   ?damage=0        every frame in full (WGPU_DAMAGE=0) -- the A/B for partial redraw
    //   ?damageverify    render each frame partial AND full and log every differing pixel
    //   ?damagetrace     log each frame's damage rectangles
    //   ?env=NAME=V,...  any other variable
    if (params.get('damage') === '0') builder = builder.withEnvironmentVariable('WGPU_DAMAGE', '0');
    if (params.has('damageverify')) builder = builder.withEnvironmentVariable('WGPU_DAMAGE_VERIFY', '1');
    if (params.has('damagetrace')) builder = builder.withEnvironmentVariable('WGPU_DAMAGE_TRACE', '1');
    for (const kv of (params.get('env') || '').split(',')) {
        const eq = kv.indexOf('=');
        if (eq > 0) builder = builder.withEnvironmentVariable(kv.slice(0, eq), kv.slice(eq + 1));
    }
    if (args) builder = builder.withApplicationArguments(...args.split(','));
    const runtime = await builder.create();
    const { setModuleImports, runMain, Module } = runtime;
    // Diagnostics: lets the host/driver read files the app wrote into the wasm VFS
    // (e.g. the compositor's /sink.log).
    globalThis.__wpfFS = Module.FS;

    setModuleImports('wgpuInterop', wgpuInterop);
    setModuleImports('wpfBrowserWindow', wpfBrowserWindow);
    setModuleImports('wpfBrowserMedia', wpfBrowserMedia);
    setModuleImports('wpfBrowserWebView', wpfBrowserWebView);

    Module.FS.mkdirTree(DPAPI_DIR);
    if (dpapi) for (const name of DPAPI_KEYS) Module.FS.writeFile(`${DPAPI_DIR}/${name}`, dpapi[name]);

    // Mount the bundled fonts into the wasm VFS where WPF's managed font catalog
    // scans on the browser (SystemFontCatalog: /fonts).
    status.innerText = 'loading fonts…';
    Module.FS.mkdirTree('/fonts');
    const fonts = await fontList();
    await Promise.all(fonts.map(async (name) => {
        const resp = await fetch(`./fonts/${name}`);
        if (!resp.ok) { console.error(`font fetch failed: ${name}`); return; }
        Module.FS.writeFile(`/fonts/${name}`, new Uint8Array(await resp.arrayBuffer()));
    }));

    // Mount the app's own payload folders (its plugin drop folder, for one) into the wasm VFS at the
    // SAME relative path they occupy next to the .exe on the desktop. AppContext.BaseDirectory is "/"
    // here, so an app doing Path.Combine(AppContext.BaseDirectory, "Plugins") and loading assemblies
    // from it works unchanged. The build stages these (see _WpfWebGpuBrowserDropNestedPayloads) and
    // writes payload/index.json, because a directory cannot be listed over HTTP.
    try {
        const manifest = await fetch('./payload/index.json');
        if (manifest.ok) {
            const entries = await manifest.json();
            if (entries.length) {
                status.innerText = 'loading app payload…';
                await Promise.all(entries.map(async (rel) => {
                    const resp = await fetch(`./payload/${rel}`);
                    if (!resp.ok) { console.error(`payload fetch failed: ${rel}`); return; }
                    const path = `/${rel}`;
                    Module.FS.mkdirTree(path.slice(0, path.lastIndexOf('/')));
                    Module.FS.writeFile(path, new Uint8Array(await resp.arrayBuffer()));
                }));
                console.log(`WpfWebGpu: mounted ${entries.length} app payload files`);
            }
        }
    } catch (e) {
        // An app with no payload folder is the normal case; never let this stop the boot.
        console.warn('app payload mount skipped:', e);
    }

    // ?fps=1 -- a live readout in the corner of the page.
    //
    // Two numbers, because they answer different questions and only one of them is "the frame rate":
    // DRAW is how often the app actually submitted a frame to the GPU, RAF is how often the browser
    // offered one. RAF near the display rate with a low DRAW means the app is idle, not slow;
    // both low together means the frame loop is genuinely struggling.
    if (params.has('fps')) {
        const el = document.createElement('div');
        el.style.cssText = 'position:fixed;right:6px;top:6px;z-index:2147483647;padding:3px 7px;' +
            'font:12px/1.4 ui-monospace,SFMono-Regular,Menlo,monospace;color:#0f0;background:rgba(0,0,0,.72);' +
            'border-radius:4px;pointer-events:none;white-space:pre';
        document.body.appendChild(el);
        let raf = 0, lastDraw = 0, t0 = performance.now();
        const tick = () => { raf++; requestAnimationFrame(tick); };
        requestAnimationFrame(tick);
        setInterval(() => {
            const now = performance.now();
            const secs = (now - t0) / 1000;
            const draws = (globalThis.__wpfFrameCount || 0) - lastDraw;
            el.textContent = `draw ${(draws / secs).toFixed(0)} fps\nraf  ${(raf / secs).toFixed(0)} fps`;
            lastDraw = globalThis.__wpfFrameCount || 0;
            raf = 0; t0 = now;
        }, 500);
    }

    status.innerText = 'starting WPF…';
    // Environment telemetry: canvas/viewport/scale, printed at boot and on resize
    // (perf reports are meaningless without knowing the rendered pixel count).
    const envLine = () => {
        const c = globalThis.__wpfCanvases?.values().next().value;
        console.log(`ENV: dpr=${window.devicePixelRatio} viewport=${window.innerWidth}x${window.innerHeight}` +
            (c ? ` canvas=${c.width}x${c.height}dev (${c.style.width} css)` : " canvas=none"));
    };
    setTimeout(envLine, 3000);
    window.addEventListener('resize', () => setTimeout(envLine, 500));
    const exitCode = await runMain();
    if (exitCode === 0) {
        // Main returns immediately on the browser; the dispatcher pump keeps running.
        status.remove();
    } else {
        status.innerText = `boot failed (rc=${exitCode}) — see console`;
    }
} catch (e) {
    status.innerText = `boot failed: ${e}`;
    // An app built for the multithreaded runtime (WasmEnableThreads) shares its memory with workers,
    // which a browser allows only on a cross-origin isolated page. Not isolated, it fails here with
    // "memory import must be a WebAssembly.Memory object", which says nothing of why: say it.
    if (!globalThis.crossOriginIsolated)
        status.innerText += ' -- this page is not cross-origin isolated, which an app that uses threads needs: '
            + (window.top !== window
                ? 'it is shown inside a frame (an editor\'s preview?); open it in a browser tab of its own'
                : 'serve it with `wpfrun serve` (COOP/COEP headers) and open the address it prints');
    console.error(e);
}
