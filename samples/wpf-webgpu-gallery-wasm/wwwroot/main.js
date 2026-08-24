// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

import { dotnet } from './_framework/dotnet.js'
import * as wgpuInterop from './wgpu-interop.js'
import * as wpfBrowserWindow from './browser-window.js'
import * as wpfBrowserMedia from './browser-media.js'
import * as wpfDevTools from './devtools-bridge.js'

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

try {
    // Gallery modes (e.g. ?args=states to auto-open the combo popup) pass through
    // to Program.Main via the query string.
    const params = new URLSearchParams(location.search);
    const args = params.get('args');
    let builder = dotnet
        .withEnvironmentVariable('WPF_USE_WEBGPU_COMPOSITION', '1')
        .withEnvironmentVariable('WPF_WEBGPU_SINK_LOG', '/sink.log');
    if (params.has('perf')) builder = builder.withEnvironmentVariable('WPF_WEBGPU_PERF_CONSOLE', '1');
    // ?devtools[=port] turns the CDP visual-tree inspector on. There is no socket in a browser,
    // so it is a message port -- see devtools-bridge.js for how to drive it.
    if (params.has('devtools'))
        builder = builder.withEnvironmentVariable('WPF_DEVTOOLS', params.get('devtools') || '1');
    // AOT-profile collection: only meaningful in a -p:CollectAotProfile=true build (the mono AOT
    // profiler is linked in there). Point its write-at-method at our AotProfiling.Stop trigger.
    if (params.has('aotprofile'))
        // NOTE: the runtime's default sendTo ('Interop/Runtime::DumpAotProfileData') is stale in
        // .NET 10 — the method lives in JavaScriptExports now. Spell it out or the dump can't resolve.
        builder = builder.withConfig({ aotProfilerOptions: { writeAt: 'AotProfiling::WpfAotProfileFlush', sendTo: 'System.Runtime.InteropServices.JavaScript.JavaScriptExports::DumpAotProfileData' } });
    if (args) builder = builder.withApplicationArguments(...args.split(','));
    const runtime = await builder.create();
    globalThis.__dotnetRuntime = runtime;
    // Driver hook: invoke the profiler write-at trigger, then hand back the dumped bytes.
    globalThis.__dumpAotProfile = async () => {
        const ex = await runtime.getAssemblyExports('WpfWebGpuGalleryWasm');
        ex.AotProfiling.WpfAotProfileFlush();
        for (let i = 0; i < 50; i++) {
            const d = runtime.INTERNAL && runtime.INTERNAL.aotProfileData;
            if (d && d.length) return Array.from(d);
            await new Promise(r => setTimeout(r, 100));
        }
        return { error: 'no aotProfileData', internalKeys: Object.keys(runtime.INTERNAL || {}) };
    };
    const { setModuleImports, runMain, Module } = runtime;
    // Diagnostics: lets the host/driver read files the app wrote into the wasm VFS
    // (e.g. the compositor's /sink.log).
    globalThis.__wpfFS = Module.FS;

    setModuleImports('wgpuInterop', wgpuInterop);
    setModuleImports('wpfBrowserWindow', wpfBrowserWindow);
    setModuleImports('wpfBrowserMedia', wpfBrowserMedia);
    setModuleImports('wpfDevTools', wpfDevTools);

    // The bridge cannot reach the managed exports by itself, and they have to be in place
    // BEFORE runMain: the inspector starts when the app creates its first window, which is
    // inside Main, and its first act is to call back into this module.
    if (params.has('devtools')) {
        try {
            globalThis.__wpfDevToolsExports = await runtime.getAssemblyExports('Microsoft.Wpf.DevTools');
        } catch (e) {
            console.error('devtools exports unavailable (is the assembly trimmed away?):', e);
        }
    }

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
    console.error(e);
}
