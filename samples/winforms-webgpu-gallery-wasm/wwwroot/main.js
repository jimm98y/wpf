import { dotnet } from './_framework/dotnet.js'
import * as wgpuInterop from './wgpu-interop.js'
import * as winformsInterop from './winforms-interop.js'

const status = document.getElementById('wf-status');
const FONTS = ['LiberationSans-Regular.ttf'];

try {
    const runtime = await dotnet
        .withEnvironmentVariable('WF_WEBGPU', '1')
        .withEnvironmentVariable('WF_GPU_RASTER', '1')
        .create();
    const { setModuleImports, runMain, Module } = runtime;
    globalThis.__wpfFS = Module.FS;

    setModuleImports('wgpuInterop', wgpuInterop);
    setModuleImports('winformsInterop', winformsInterop);

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
