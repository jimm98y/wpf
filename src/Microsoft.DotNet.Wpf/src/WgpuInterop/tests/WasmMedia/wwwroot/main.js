// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

import { dotnet } from './_framework/dotnet.js'
import * as wgpuInterop from './wgpu-interop.js'
import * as wpfBrowserCodecs from './browser-codecs.js'

const media = ['tone-aac.m4a', 'tone-alac.m4a', 'tone-alac-stereo.m4a', 'tone-alac-24.m4a', 'tone.aac', 'tone.flac', 'tone.mp3', 'tone-notag.mp3', 'clip-vp9.mp4', 'clip-av1.mp4', 'clip-hevc.mp4', 'clip.webm', 'clip.mkv'];
const status = document.getElementById('status');
try {
    const { setModuleImports, runMain, Module } = await dotnet.create();
    setModuleImports('wgpuInterop', wgpuInterop);
    setModuleImports('wpfBrowserCodecs', wpfBrowserCodecs);
    Module.FS.mkdirTree('/MediaFoundation/Media');
    for (const m of media) {
        const r = await fetch('media/' + m);
        Module.FS.writeFile('/MediaFoundation/Media/' + m, new Uint8Array(await r.arrayBuffer()));
    }
    for (const f of ['SourceReader', 'SinkWriter', 'Encoder', 'MediaSource', 'ReaderTransform', 'SinkByteStream', 'SinkTransform', 'LocalTransform', 'VideoProcessor', 'VideoProcessorControl']) {
        const r = await fetch('fixtures/' + f + '.windows.txt');
        Module.FS.writeFile('/MediaFoundation/' + f + '.windows.txt', new Uint8Array(await r.arrayBuffer()));
    }
    status.innerText = 'running…';
    const filter = new URLSearchParams(location.search).get('filter');
    const code = await runMain(undefined, filter ? [filter] : []);
    status.innerText = 'exited ' + code;
} catch (e) {
    status.innerText = 'failed: ' + e;
    console.error(e);
}
