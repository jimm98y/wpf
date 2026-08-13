// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

import { dotnet } from './_framework/dotnet.js'
import * as wgpuInterop from './wgpu-interop.js'
import * as wpfBrowserWindow from './browser-window.js'
import * as wpfBrowserMedia from './browser-media.js'

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
    if (params.has('firstchance')) builder = builder.withEnvironmentVariable('WPF_WEBGPU_LOG_FIRST_CHANCE', '1');
    if (args) builder = builder.withApplicationArguments(...args.split(','));
    const runtime = await builder.create();
    const { setModuleImports, runMain, Module } = runtime;
    // Diagnostics: lets the host/driver read files the app wrote into the wasm VFS
    // (e.g. the compositor's /sink.log).
    globalThis.__wpfFS = Module.FS;

    setModuleImports('wgpuInterop', wgpuInterop);
    setModuleImports('wpfBrowserWindow', wpfBrowserWindow);
    setModuleImports('wpfBrowserMedia', wpfBrowserMedia);

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
