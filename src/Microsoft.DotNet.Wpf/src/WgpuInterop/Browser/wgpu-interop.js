// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
//
// Browser WebGPU backend for the WPF WebGPU composition engine.
//
// This module is the JS half of Browser/Wgpu.Browser.cs: the managed side keeps
// the exact wgpu-native C-ABI surface (structs decoded in C#), and calls these
// exports with primitive arguments / JSON descriptor strings. All WebGPU objects
// live in a handle table here; C# only ever sees integer ids (mapped to IntPtr).
//
// Canvas registry: windowing code (BrowserWindow / the app bootstrap) registers
// HTMLCanvasElements into globalThis.__wpfCanvases (Map<int, HTMLCanvasElement>)
// under the same integer handle it hands to WPF as the "hwnd". createSurface()
// resolves the canvas from that registry.

const objs = new Map();
let nextId = 1;

function put(o) { const id = nextId++; objs.set(id, o); return id; }
function get(id) { return objs.get(id); }

function canvases() {
    if (!globalThis.__wpfCanvases) globalThis.__wpfCanvases = new Map();
    return globalThis.__wpfCanvases;
}

export function isSupported() {
    return !!navigator.gpu;
}

export async function requestAdapter(powerPreference) {
    if (!navigator.gpu) return 0;
    const adapter = await navigator.gpu.requestAdapter(
        powerPreference ? { powerPreference } : {});
    return adapter ? put(adapter) : 0;
}

export function getAdapterInfoJson(adapterId) {
    const a = get(adapterId);
    const info = a.info ?? {};
    return JSON.stringify({
        vendor: info.vendor ?? "",
        architecture: info.architecture ?? "",
        device: info.device ?? "",
        description: info.description ?? "",
    });
}

export async function requestDevice(adapterId) {
    const adapter = get(adapterId);
    const device = await adapter.requestDevice();
    // Diagnostics only — guarded so engine differences (e.g. Safari) can't break boot.
    try {
        device.addEventListener("uncapturederror", (e) => {
            console.error("[wgpu] uncaptured error:", e.error?.message ?? e.error);
        });
        device.lost.then((info) => {
            console.error("[wgpu] device lost:", info.reason, info.message);
        });
    } catch { /* optional */ }
    return put(device);
}

export function getQueue(deviceId) {
    return put(get(deviceId).queue);
}

export function getPreferredFormat() {
    return navigator.gpu ? navigator.gpu.getPreferredCanvasFormat() : "bgra8unorm";
}

// ---- Surface (canvas swap chain) ------------------------------------------

export function createSurface(canvasHandle) {
    const canvas = canvases().get(canvasHandle);
    if (!canvas) {
        console.error(`[wgpu] createSurface: no canvas registered for handle ${canvasHandle}`);
        return 0;
    }
    const context = canvas.getContext("webgpu");
    if (!context) {
        console.error("[wgpu] createSurface: canvas.getContext('webgpu') returned null");
        return 0;
    }
    return put({ __surface: true, canvas, context, viewFormat: "" });
}

// format: the base (non-sRGB) canvas format; viewFormat: "" or the sRGB view
// format render-target views should use (webgpu.h surfaces are configured with
// an sRGB format directly; browser canvases take it as a viewFormats entry).
export function configureSurface(surfaceId, deviceId, format, viewFormat, alphaMode, width, height) {
    const s = get(surfaceId);
    const device = get(deviceId);
    s.canvas.width = width;
    s.canvas.height = height;
    s.viewFormat = viewFormat;
    const cfg = {
        device,
        format,
        usage: GPUTextureUsage.RENDER_ATTACHMENT | GPUTextureUsage.COPY_SRC,
        alphaMode,
    };
    if (viewFormat) cfg.viewFormats = [viewFormat];
    s.context.configure(cfg);
}

export function surfaceGetCurrentTexture(surfaceId) {
    const s = get(surfaceId);
    try {
        const tex = s.context.getCurrentTexture();
        if (!tex) return 0;
        tex.__canvasBacked = true;
        if (s.viewFormat) tex.__viewFormat = s.viewFormat;
        return put(tex);
    } catch (e) {
        console.error("[wgpu] getCurrentTexture failed:", e);
        return 0;
    }
}

// ---- Resources --------------------------------------------------------------

export function createTexture(deviceId, width, height, format, usage, mipLevelCount, sampleCount) {
    const device = get(deviceId);
    const tex = device.createTexture({
        size: { width, height, depthOrArrayLayers: 1 },
        format,
        usage,
        mipLevelCount,
        sampleCount,
    });
    return put(tex);
}

export function createTextureView(textureId) {
    const tex = get(textureId);
    const view = tex.__viewFormat ? tex.createView({ format: tex.__viewFormat }) : tex.createView();
    return put(view);
}

export function createBuffer(deviceId, size, usage, mappedAtCreation) {
    const device = get(deviceId);
    return put(device.createBuffer({ size, usage, mappedAtCreation }));
}

export function writeBuffer(queueId, bufferId, bufferOffset, data) {
    // `data` is a marshaled Span<byte> (MemoryView over wasm memory); slice()
    // copies it out, which is required anyway because writeBuffer is async-safe.
    get(queueId).writeBuffer(get(bufferId), bufferOffset, data.slice());
}

export function writeTexture(queueId, textureId, mipLevel, originX, originY, data, bytesPerRow, rowsPerImage, width, height) {
    get(queueId).writeTexture(
        { texture: get(textureId), mipLevel, origin: { x: originX, y: originY, z: 0 } },
        data.slice(),
        { offset: 0, bytesPerRow, rowsPerImage },
        { width, height, depthOrArrayLayers: 1 });
}

export function createShaderModule(deviceId, code) {
    const device = get(deviceId);
    const module = device.createShaderModule({ code });
    module.getCompilationInfo?.().then((info) => {
        for (const m of info.messages) {
            if (m.type === "error")
                console.error(`[wgsl] ${m.lineNum}:${m.linePos} ${m.message}`);
        }
    });
    return put(module);
}

// desc: JSON from C# — handle-valued fields carry integer ids and are resolved here.
export function createRenderPipeline(deviceId, descJson) {
    const device = get(deviceId);
    const d = JSON.parse(descJson);
    const desc = {
        layout: "auto",
        vertex: {
            module: get(d.vertex.module),
            entryPoint: d.vertex.entryPoint,
            buffers: d.vertex.buffers,
        },
        primitive: d.primitive,
        multisample: d.multisample,
    };
    if (d.depthStencil) desc.depthStencil = d.depthStencil;
    if (d.fragment) {
        desc.fragment = {
            module: get(d.fragment.module),
            entryPoint: d.fragment.entryPoint,
            targets: d.fragment.targets,
        };
    }
    return put(device.createRenderPipeline(desc));
}

export function getBindGroupLayout(pipelineId, groupIndex) {
    return put(get(pipelineId).getBindGroupLayout(groupIndex));
}

// entriesJson: [{binding, buffer?, offset?, size?, sampler?, textureView?}] with ids.
export function createBindGroup(deviceId, layoutId, entriesJson) {
    const device = get(deviceId);
    const entries = JSON.parse(entriesJson).map((e) => {
        let resource;
        if (e.buffer) {
            resource = { buffer: get(e.buffer), offset: e.offset ?? 0 };
            if (e.size) resource.size = e.size;
        } else if (e.sampler) {
            resource = get(e.sampler);
        } else {
            resource = get(e.textureView);
        }
        return { binding: e.binding, resource };
    });
    return put(device.createBindGroup({ layout: get(layoutId), entries }));
}

export function createSampler(deviceId, descJson) {
    return put(get(deviceId).createSampler(JSON.parse(descJson)));
}

// ---- Command encoding --------------------------------------------------------

export function createCommandEncoder(deviceId) {
    return put(get(deviceId).createCommandEncoder());
}

export function beginRenderPass(encoderId, colorViewId, resolveViewId, loadOp, storeOp,
    r, g, b, a, depthViewId, depthLoadOp, depthStoreOp, depthClearValue) {
    const colorAttachment = {
        view: get(colorViewId),
        loadOp,
        storeOp,
        clearValue: { r, g, b, a },
    };
    if (resolveViewId) colorAttachment.resolveTarget = get(resolveViewId);
    const desc = { colorAttachments: [colorAttachment] };
    if (depthViewId) {
        desc.depthStencilAttachment = {
            view: get(depthViewId),
            depthLoadOp,
            depthStoreOp,
            depthClearValue,
        };
    }
    return put(get(encoderId).beginRenderPass(desc));
}

export function setPipeline(passId, pipelineId) {
    get(passId).setPipeline(get(pipelineId));
}

export function setBindGroup(passId, groupIndex, groupId) {
    get(passId).setBindGroup(groupIndex, get(groupId));
}

export function setVertexBuffer(passId, slot, bufferId, offset, size) {
    // size < 0 encodes webgpu.h WGPU_WHOLE_SIZE.
    if (size < 0) get(passId).setVertexBuffer(slot, get(bufferId), offset);
    else get(passId).setVertexBuffer(slot, get(bufferId), offset, size);
}

export function setIndexBuffer(passId, bufferId, format, offset, size) {
    if (size < 0) get(passId).setIndexBuffer(get(bufferId), format, offset);
    else get(passId).setIndexBuffer(get(bufferId), format, offset, size);
}

export function drawIndexed(passId, indexCount, instanceCount, firstIndex, baseVertex, firstInstance) {
    get(passId).drawIndexed(indexCount, instanceCount, firstIndex, baseVertex, firstInstance);
}

export function setScissorRect(passId, x, y, width, height) {
    get(passId).setScissorRect(x, y, width, height);
}

export function endPass(passId) {
    get(passId).end();
}

export function finishEncoder(encoderId) {
    return put(get(encoderId).finish());
}

export function submit(queueId, commandBufferId) {
    // Frame counter for the on-screen FPS readout (see main.js). Counted HERE, at the point work is
    // handed to the GPU, so it reports frames the app actually drew -- not requestAnimationFrame
    // callbacks, which keep ticking at display rate whether or not anything was rendered.
    globalThis.__wpfFrameCount = (globalThis.__wpfFrameCount || 0) + 1;
    get(queueId).submit([get(commandBufferId)]);
}

// ---- Readback ------------------------------------------------------------------

// Async GPU->CPU readback of a whole texture (RGBA8). Returns tightly-packed rows.
export async function readbackTexture(deviceId, textureId, width, height) {
    const device = get(deviceId);
    const bytesPerRow = Math.ceil((width * 4) / 256) * 256;
    const buf = device.createBuffer({
        size: bytesPerRow * height,
        usage: GPUBufferUsage.COPY_DST | GPUBufferUsage.MAP_READ,
    });
    const enc = device.createCommandEncoder();
    enc.copyTextureToBuffer(
        { texture: get(textureId) },
        { buffer: buf, bytesPerRow, rowsPerImage: height },
        { width, height, depthOrArrayLayers: 1 });
    device.queue.submit([enc.finish()]);
    await buf.mapAsync(GPUMapMode.READ);
    const src = new Uint8Array(buf.getMappedRange());
    const out = new Uint8Array(width * 4 * height);
    for (let y = 0; y < height; y++)
        out.set(src.subarray(y * bytesPerRow, y * bytesPerRow + width * 4), y * width * 4);
    buf.unmap();
    buf.destroy();
    // Task<byte[]> is not marshalable; park the bytes and let C# take them synchronously.
    return put(out);
}

export function takeBytes(id) {
    const o = get(id);
    objs.delete(id);
    return o ?? new Uint8Array(0);
}

// Async 1-texel readback for GPU hit testing: returns the packed visual id at (x,y)
// (r | g<<8 | b<<16), or 0 when alpha is 0 (no visual). Reads a single texel rather
// than the whole id buffer.
export async function readbackTexel(deviceId, textureId, x, y) {
    const device = get(deviceId);
    const buf = device.createBuffer({
        size: 256,
        usage: GPUBufferUsage.COPY_DST | GPUBufferUsage.MAP_READ,
    });
    const enc = device.createCommandEncoder();
    enc.copyTextureToBuffer(
        { texture: get(textureId), origin: { x, y, z: 0 } },
        { buffer: buf, bytesPerRow: 256, rowsPerImage: 1 },
        { width: 1, height: 1, depthOrArrayLayers: 1 });
    device.queue.submit([enc.finish()]);
    await buf.mapAsync(GPUMapMode.READ);
    const p = new Uint8Array(buf.getMappedRange());
    const id = p[3] === 0 ? 0 : (p[0] | (p[1] << 8) | (p[2] << 16));
    buf.unmap();
    buf.destroy();
    return id;
}

// ---- Lifetime ----------------------------------------------------------------

// Drops the handle. GPU-resource-owning objects are destroyed eagerly where the
// API allows it (buffers, non-canvas textures); everything else is left to GC.
// Canvas-backed textures must never be destroyed — the context owns them.
export function release(id) {
    const o = objs.get(id);
    if (o !== undefined) {
        if (o instanceof GPUBuffer) o.destroy();
        else if (typeof GPUTexture !== "undefined" && o instanceof GPUTexture && !o.__canvasBacked) o.destroy();
        objs.delete(id);
    }
}

export function handleCount() {
    return objs.size;
}
