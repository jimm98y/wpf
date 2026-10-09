import { dotnet } from './_framework/dotnet.js'
import * as wgpuInterop from './wgpu-interop.js'
import * as winformsInterop from './winforms-interop.js'
import * as wpfBrowserWebView from './browser-webview.js'

const status = document.getElementById('wf-status');
const FONTS = ['LiberationSans-Regular.ttf'];

try {
    // A page has no environment block, so the renderer's knobs come from the URL instead:
    //   ?damage=0        every frame in full (WGPU_DAMAGE=0) -- the A/B for partial redraw
    //   ?damageverify    render each frame partial AND full and log every differing pixel
    //   ?damagetrace     log each frame's damage rectangles
    //   ?args=web        add a WebBrowser (an <iframe> over the canvas) to the demo form
    //   ?env=NAME=V,...  any other variable
    const params = new URLSearchParams(location.search);
    let builder = dotnet
        .withEnvironmentVariable('WF_WEBGPU', '1')
        .withEnvironmentVariable('WF_GPU_RASTER', '1');
    if (params.get('damage') === '0') builder = builder.withEnvironmentVariable('WGPU_DAMAGE', '0');
    if (params.has('damageverify')) builder = builder.withEnvironmentVariable('WGPU_DAMAGE_VERIFY', '1');
    if (params.has('damagetrace')) builder = builder.withEnvironmentVariable('WGPU_DAMAGE_TRACE', '1');
    for (const kv of (params.get('env') || '').split(',')) {
        const eq = kv.indexOf('=');
        if (eq > 0) builder = builder.withEnvironmentVariable(kv.slice(0, eq), kv.slice(eq + 1));
    }
    const args = params.get('args');
    if (args) builder = builder.withApplicationArguments(...args.split(','));
    const runtime = await builder.create();
    const { setModuleImports, runMain, Module } = runtime;
    globalThis.__wpfFS = Module.FS;

    setModuleImports('wgpuInterop', wgpuInterop);
    setModuleImports('winformsInterop', winformsInterop);
    setModuleImports('wpfBrowserWebView', wpfBrowserWebView);

    // Mount fonts into the wasm VFS where our TextMetrics / WgpuPresenter scan (/fonts).
    status.innerText = 'loading fonts…';
    Module.FS.mkdirTree('/fonts');
    await Promise.all(FONTS.map(async (name) => {
        const resp = await fetch(`./fonts/${name}`);
        if (!resp.ok) { console.error(`font fetch failed: ${name}`); return; }
        Module.FS.writeFile(`/fonts/${name}`, new Uint8Array(await resp.arrayBuffer()));
    }));

    status.innerText = 'starting WinForms…';
    const rc = await runMain();
    if (rc === 0) status.remove(); else status.innerText = `boot failed (rc=${rc}) — see console`;
} catch (e) {
    status.innerText = `boot failed: ${e}`;
    console.error(e);
}
