// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

import { dotnet } from './_framework/dotnet.js'
import * as wgpuInterop from './wgpu-interop.js'
import * as wpfBrowserWindow from './browser-window.js'

const status = document.getElementById('wpf-status');

const FONTS = [
    'LiberationSans-Regular.ttf', 'LiberationSans-Bold.ttf', 'LiberationSans-Italic.ttf', 'LiberationSans-BoldItalic.ttf',
    'DejaVuSans.ttf', 'DejaVuSans-Bold.ttf', 'DejaVuSans-Oblique.ttf', 'DejaVuSans-BoldOblique.ttf',
    'DejaVuSansMono.ttf', 'DejaVuSansMono-Bold.ttf', 'DejaVuSerif.ttf', 'DejaVuSerif-Bold.ttf',
];

try {
    // Gallery modes (e.g. ?args=states to auto-open the combo popup) pass through
    // to Program.Main via the query string.
    const params = new URLSearchParams(location.search);
    const args = params.get('args');
    let builder = dotnet
        .withEnvironmentVariable('WPF_USE_WEBGPU_COMPOSITION', '1')
        .withEnvironmentVariable('WPF_WEBGPU_SINK_LOG', '/sink.log');
    if (params.has('perf')) builder = builder.withEnvironmentVariable('WPF_WEBGPU_PERF_CONSOLE', '1');
    if (args) builder = builder.withApplicationArguments(...args.split(','));
    const runtime = await builder.create();
    const { setModuleImports, runMain, Module } = runtime;
    // Diagnostics: lets the host/driver read files the app wrote into the wasm VFS
    // (e.g. the compositor's /sink.log).
    globalThis.__wpfFS = Module.FS;

    setModuleImports('wgpuInterop', wgpuInterop);
    setModuleImports('wpfBrowserWindow', wpfBrowserWindow);

    // Mount the bundled fonts into the wasm VFS where WPF's managed font catalog
    // scans on the browser (SystemFontCatalog: /fonts).
    status.innerText = 'loading fonts…';
    Module.FS.mkdirTree('/fonts');
    await Promise.all(FONTS.map(async (name) => {
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
