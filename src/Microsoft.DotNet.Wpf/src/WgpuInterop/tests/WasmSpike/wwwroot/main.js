// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

import { dotnet } from './_framework/dotnet.js'
import * as wgpuInterop from './wgpu-interop.js'

// Register the page canvas under handle 1 — the C# side passes this handle
// through NativePlatform.CreateWindowSurface as the "hwnd".
globalThis.__wpfCanvases = new Map([[1, document.getElementById('wpf-canvas')]]);

const status = document.getElementById('status');
status.innerText = 'starting dotnet…';

try {
    const { setModuleImports, runMain } = await dotnet.create();
    setModuleImports('wgpuInterop', wgpuInterop);
    status.innerText = 'running spike…';
    const exitCode = await runMain();
    status.innerText = `spike exited with code ${exitCode}`;
} catch (e) {
    status.innerText = `boot failed: ${e}`;
    console.error(e);
}
