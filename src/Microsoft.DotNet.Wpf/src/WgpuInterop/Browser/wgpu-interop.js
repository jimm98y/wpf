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

// The handle table. A handle is one reference, as a webgpu.h object pointer is: an object made of
// others holds them too (a view its texture, a bind group its buffers and views), so releasing a
// handle the native API keeps alive behind it -- the Direct3D shim lets go of a texture and keeps
// drawing through its view -- does not destroy what is still in use. A buffer or texture whose
// last reference goes is destroyed once the GPU has finished the work submitted before then:
// destroyed at once, a command buffer still to run that names it failed validation.
const objs = new Map();
let nextId = 1;

function put(o) { const id = nextId++; objs.set(id, o); o.__refs = (o.__refs || 0) + 1; return id; }
function get(id) { return objs.get(id); }
function hold(o) { if (o) o.__refs = (o.__refs || 0) + 1; return o; }

let theQueue = null;
let pendingDestroy = [];
let sweepTimer = 0;
let outstanding = 0;

function unref(o) {
    if (!o || --o.__refs > 0) return;
    if (o.__deps) { for (const d of o.__deps) unref(d); o.__deps = null; }
    const destroyable = o instanceof GPUBuffer
        || (typeof GPUTexture !== "undefined" && o instanceof GPUTexture && !o.__canvasBacked);
    if (!destroyable) return;
    pendingDestroy.push(o);
    // Nothing submitted for a while: sweep after whatever is on the GPU now.
    if (!sweepTimer) sweepTimer = setTimeout(() => {
        sweepTimer = 0;
        if (!theQueue || pendingDestroy.length === 0) return;
        const batch = pendingDestroy; pendingDestroy = [];
        theQueue.onSubmittedWorkDone().then(() => { for (const x of batch) x.destroy(); });
    }, 500);
}

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

// The features the Direct3D shim's translator can use where the adapter has them, and the adapter's
// own limits (Direct3D/D3DDevice.cs CreateGpu asks wgpu-native for the same): D3D11 takes eight
// render targets, 128 shader resources and 16 samplers per stage for granted, which WebGPU's
// default limits refuse.
const WANTED_FEATURES = ["depth32float-stencil8", "texture-compression-bc", "indirect-first-instance",
    "float32-filterable", "depth-clip-control", "rg11b10ufloat-renderable", "float32-blendable",
    "clip-distances", "dual-source-blending", "primitive-index", "bgra8unorm-storage",
    "texture-formats-tier1", "texture-formats-tier2"];

function adapterLimits(adapter) {
    const limits = {};
    for (const key in adapter.limits) {
        const v = adapter.limits[key];
        if (typeof v === "number") limits[key] = v;
    }
    return limits;
}

export async function requestDevice(adapterId) {
    const adapter = get(adapterId);
    const requiredFeatures = WANTED_FEATURES.filter((f) => adapter.features.has(f));
    let device;
    try { device = await adapter.requestDevice({ requiredFeatures, requiredLimits: adapterLimits(adapter) }); }
    catch (e) {
        console.warn("[wgpu] requestDevice with the adapter's limits failed, with the defaults instead:", e);
        device = await adapter.requestDevice({ requiredFeatures });
    }
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

// webgpu.h's WGPUFeatureName (D3DDevice.cs s_wantedFeatures): what the device was made with.
const FEATURES = { 0x1: "core-features-and-limits", 0x2: "depth-clip-control", 0x3: "depth32float-stencil8",
    0x4: "texture-compression-bc", 0x5: "texture-compression-bc-sliced-3d", 0x6: "texture-compression-etc2",
    0x7: "texture-compression-astc", 0x8: "texture-compression-astc-sliced-3d", 0x9: "timestamp-query",
    0xA: "indirect-first-instance", 0xB: "shader-f16", 0xC: "rg11b10ufloat-renderable", 0xD: "bgra8unorm-storage",
    0xE: "float32-filterable", 0xF: "float32-blendable", 0x10: "clip-distances", 0x11: "dual-source-blending",
    0x12: "subgroups", 0x13: "texture-formats-tier1", 0x14: "texture-formats-tier2", 0x15: "primitive-index" };

export function deviceHasFeature(deviceId, feature) {
    const name = FEATURES[feature];
    return !!name && get(deviceId).features.has(name);
}

export function getQueue(deviceId) {
    theQueue = get(deviceId).queue;
    return put(theQueue);
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
        // COPY_DST: what a back buffer is presented with (surfacePresent)
        usage: GPUTextureUsage.RENDER_ATTACHMENT | GPUTextureUsage.COPY_SRC | GPUTextureUsage.COPY_DST,
        alphaMode,
    };
    if (viewFormat) cfg.viewFormats = [viewFormat];
    s.context.configure(cfg);
    s.device = device;
    s.format = format;
    s.alphaMode = alphaMode;
    s.canvas.__wgpuSurface = s;
    if (s.back) { s.back.destroy(); s.back = null; }
    if (relayedRuntime()) {
        s.back = device.createTexture({
            size: { width, height, depthOrArrayLayers: 1 },
            format,
            usage: GPUTextureUsage.RENDER_ATTACHMENT | GPUTextureUsage.COPY_SRC | GPUTextureUsage.TEXTURE_BINDING,
            viewFormats: viewFormat ? [viewFormat] : [],
        });
        s.back.__canvasBacked = true;
        if (viewFormat) s.back.__viewFormat = viewFormat;
    }
}

// A canvas's current texture lives only until the JS task that took it returns to the event loop:
// the browser presents it then and destroys it. On .NET's multithreaded runtime the app's code runs
// on a worker, and each call it makes to this module reaches the page as a task of its own, so the
// texture it acquired is gone by the time it submits what it drew into it. There the surface is a
// texture of the bridge's own (s.back), which the frame is drawn into, and present copies it onto the
// canvas's current texture in one call -- one task -- as a swap chain's present would.
function relayedRuntime() {
    try { return !!globalThis.getDotnetRuntime?.(0)?.runtimeBuildInfo?.wasmEnableThreads; }
    catch { return false; }
}

export function surfacePresent(surfaceId) {
    const s = get(surfaceId);
    if (!s?.back) return;     // the canvas's own texture, presented when this task ends
    const tex = s.context.getCurrentTexture();
    const enc = s.device.createCommandEncoder();
    enc.copyTextureToTexture({ texture: s.back }, { texture: tex },
        { width: s.back.width, height: s.back.height, depthOrArrayLayers: 1 });
    s.device.queue.submit([enc.finish()]);
}

// What a canvas shows, read back -- the screen's pixels an eye dropper samples (ScreenPixels, through
// browser-window.js capturePageAsync). A WebGPU canvas's own texture is gone once it is presented, and
// drawImage and getImageData see the canvas as transparent; the surface's back buffer still holds the
// frame it shows, and is read instead. The rectangle is in the canvas's pixels and inside it; the
// answer is RGBA with straight alpha, top row first -- ImageData's -- or null for a canvas with no
// back buffer to read.
export async function readCanvasPixels(canvas, x, y, w, h) {
    const s = canvas?.__wgpuSurface;
    const src = s?.back;
    if (!src || w <= 0 || h <= 0 || x < 0 || y < 0 || x + w > src.width || y + h > src.height) return null;
    const stride = Math.ceil(w * 4 / 256) * 256;     // copyTextureToBuffer's row alignment
    const buffer = s.device.createBuffer({ size: stride * h, usage: GPUBufferUsage.COPY_DST | GPUBufferUsage.MAP_READ });
    try {
        const enc = s.device.createCommandEncoder();
        enc.copyTextureToBuffer({ texture: src, origin: { x, y, z: 0 } }, { buffer, bytesPerRow: stride },
            { width: w, height: h, depthOrArrayLayers: 1 });
        s.device.queue.submit([enc.finish()]);
        await buffer.mapAsync(GPUMapMode.READ);
        const bytes = new Uint8Array(buffer.getMappedRange());
        const out = new Uint8ClampedArray(w * h * 4);
        const bgra = s.format.startsWith("bgra");
        const premultiplied = s.alphaMode === "premultiplied";
        for (let row = 0; row < h; row++) {
            for (let i = 0; i < w; i++) {
                const from = row * stride + i * 4, to = (row * w + i) * 4;
                let r = bytes[from + (bgra ? 2 : 0)], g = bytes[from + 1], b = bytes[from + (bgra ? 0 : 2)];
                const a = premultiplied ? bytes[from + 3] : 255;
                if (premultiplied && a > 0 && a < 255) {
                    r = Math.round(r * 255 / a); g = Math.round(g * 255 / a); b = Math.round(b * 255 / a);
                }
                out[to] = r; out[to + 1] = g; out[to + 2] = b; out[to + 3] = a;
            }
        }
        buffer.unmap();
        return out;
    } finally {
        buffer.destroy();
    }
}

export function surfaceGetCurrentTexture(surfaceId) {
    const s = get(surfaceId);
    if (s.back) return put(s.back);
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

// ---- webgpu.h enums -> WebGPU strings ----------------------------------------------------------
//
// Wgpu.Browser.cs passes webgpu.h's values as they are (descriptors as JSON of numbers); the
// tables are webgpu.h's numbering (wgpu-native v29, Direct3D/Wgpu.D3D.cs TexFmt / VtxFmt).

const TEXTURE_FORMATS = [undefined,
    "r8unorm", "r8snorm", "r8uint", "r8sint", "r16unorm", "r16snorm", "r16uint", "r16sint", "r16float",
    "rg8unorm", "rg8snorm", "rg8uint", "rg8sint", "r32float", "r32uint", "r32sint",
    "rg16unorm", "rg16snorm", "rg16uint", "rg16sint", "rg16float",
    "rgba8unorm", "rgba8unorm-srgb", "rgba8snorm", "rgba8uint", "rgba8sint", "bgra8unorm", "bgra8unorm-srgb",
    "rgb10a2uint", "rgb10a2unorm", "rg11b10ufloat", "rgb9e5ufloat",
    "rg32float", "rg32uint", "rg32sint",
    "rgba16unorm", "rgba16snorm", "rgba16uint", "rgba16sint", "rgba16float",
    "rgba32float", "rgba32uint", "rgba32sint",
    "stencil8", "depth16unorm", "depth24plus", "depth24plus-stencil8", "depth32float", "depth32float-stencil8",
    "bc1-rgba-unorm", "bc1-rgba-unorm-srgb", "bc2-rgba-unorm", "bc2-rgba-unorm-srgb", "bc3-rgba-unorm", "bc3-rgba-unorm-srgb",
    "bc4-r-unorm", "bc4-r-snorm", "bc5-rg-unorm", "bc5-rg-snorm", "bc6h-rgb-ufloat", "bc6h-rgb-float",
    "bc7-rgba-unorm", "bc7-rgba-unorm-srgb",
    "etc2-rgb8unorm", "etc2-rgb8unorm-srgb", "etc2-rgb8a1unorm", "etc2-rgb8a1unorm-srgb", "etc2-rgba8unorm", "etc2-rgba8unorm-srgb",
    "eac-r11unorm", "eac-r11snorm", "eac-rg11unorm", "eac-rg11snorm"];
const VERTEX_FORMATS = [undefined,
    "uint8", "uint8x2", "uint8x4", "sint8", "sint8x2", "sint8x4", "unorm8", "unorm8x2", "unorm8x4",
    "snorm8", "snorm8x2", "snorm8x4", "uint16", "uint16x2", "uint16x4", "sint16", "sint16x2", "sint16x4",
    "unorm16", "unorm16x2", "unorm16x4", "snorm16", "snorm16x2", "snorm16x4", "float16", "float16x2", "float16x4",
    "float32", "float32x2", "float32x3", "float32x4", "uint32", "uint32x2", "uint32x3", "uint32x4",
    "sint32", "sint32x2", "sint32x3", "sint32x4", "unorm10-10-10-2", "unorm8x4-bgra"];
const COMPARE = [undefined, "never", "less", "equal", "less-equal", "greater", "not-equal", "greater-equal", "always"];
const STENCIL_OPS = [undefined, "keep", "zero", "replace", "invert", "increment-clamp", "decrement-clamp", "increment-wrap", "decrement-wrap"];
const BLEND_FACTORS = [undefined, "zero", "one", "src", "one-minus-src", "src-alpha", "one-minus-src-alpha",
    "dst", "one-minus-dst", "dst-alpha", "one-minus-dst-alpha", "src-alpha-saturated", "constant", "one-minus-constant",
    "src1", "one-minus-src1", "src1-alpha", "one-minus-src1-alpha"];
const BLEND_OPS = [undefined, "add", "subtract", "reverse-subtract", "min", "max"];
const TOPOLOGIES = [undefined, "point-list", "line-list", "line-strip", "triangle-list", "triangle-strip"];
const INDEX_FORMATS = [undefined, "uint16", "uint32"];
const FRONT_FACES = [undefined, "ccw", "cw"];
const CULL_MODES = [undefined, "none", "front", "back"];
const ADDRESS_MODES = [undefined, "clamp-to-edge", "repeat", "mirror-repeat"];
const FILTERS = [undefined, "nearest", "linear"];
const TEXTURE_DIMENSIONS = [undefined, "1d", "2d", "3d"];
const VIEW_DIMENSIONS = [undefined, "1d", "2d", "2d-array", "cube", "cube-array", "3d"];
const ASPECTS = [undefined, "all", "stencil-only", "depth-only"];
const LOAD_OPS = [undefined, "load", "clear"];
const STORE_OPS = [undefined, "store", "discard"];
const BUFFER_BINDINGS = [undefined, "uniform", "uniform", "storage", "read-only-storage"];
const SAMPLER_BINDINGS = [undefined, "filtering", "filtering", "non-filtering", "comparison"];
const SAMPLE_TYPES = [undefined, "float", "float", "unfilterable-float", "depth", "sint", "uint"];
const STORAGE_ACCESS = [undefined, "write-only", "write-only", "read-only", "read-write"];
const ERROR_FILTERS = [undefined, "validation", "out-of-memory", "internal"];

// webgpu.h's "undefined" sentinels, which arrive as -1 (Wgpu.Browser.cs U32/U64)
const omit = (v) => v === undefined || v === null || v < 0;

function constants(list) {
    if (!list || list.length === 0) return undefined;
    const o = {};
    for (const [k, v] of list) o[k] = v;
    return o;
}

// ---- Resources --------------------------------------------------------------

// d: {usage, dimension, w, h, d, format, mips, samples, viewFormats}
export function createTexture(deviceId, json) {
    const d = JSON.parse(json);
    const desc = {
        size: { width: d.w, height: d.h, depthOrArrayLayers: d.d },
        format: TEXTURE_FORMATS[d.format],
        usage: d.usage,
        mipLevelCount: d.mips,
        sampleCount: d.samples,
        dimension: TEXTURE_DIMENSIONS[d.dimension] ?? "2d",
    };
    if (d.viewFormats?.length) desc.viewFormats = d.viewFormats.map((f) => TEXTURE_FORMATS[f]);
    return put(get(deviceId).createTexture(desc));
}

// json "": the texture's default view (a canvas-backed one in its sRGB view format, as configured)
export function createTextureView(textureId, json) {
    const tex = get(textureId);
    let view;
    if (!json) view = tex.__viewFormat ? tex.createView({ format: tex.__viewFormat }) : tex.createView();
    else {
        const d = JSON.parse(json);
        const desc = {};
        if (d.format) desc.format = TEXTURE_FORMATS[d.format];
        if (d.dimension) desc.dimension = VIEW_DIMENSIONS[d.dimension];
        if (d.aspect) desc.aspect = ASPECTS[d.aspect];
        desc.baseMipLevel = d.baseMip;
        if (!omit(d.mips)) desc.mipLevelCount = d.mips;
        desc.baseArrayLayer = d.baseLayer;
        if (!omit(d.layers)) desc.arrayLayerCount = d.layers;
        if (d.usage) desc.usage = d.usage;
        if (!desc.format && tex.__viewFormat) desc.format = tex.__viewFormat;
        view = tex.createView(desc);
    }
    view.__deps = [hold(tex)];
    return put(view);
}

export function createBuffer(deviceId, size, usage, mappedAtCreation) {
    const b = get(deviceId).createBuffer({ size, usage, mappedAtCreation });
    if (mappedAtCreation) { b.__mapped = b.getMappedRange(); b.__mapOffset = 0; b.__mapState = 1; }
    return put(b);
}

export function writeBuffer(queueId, bufferId, bufferOffset, data) {
    // `data` is a marshaled Span<byte> (MemoryView over wasm memory); slice()
    // copies it out, which is required anyway because writeBuffer is async-safe.
    get(queueId).writeBuffer(get(bufferId), bufferOffset, data.slice());
}

// d: {texture, mip, x, y, z, aspect, offset, bytesPerRow, rowsPerImage, w, h, d}
export function writeTexture(queueId, json, data) {
    const d = JSON.parse(json);
    const layout = { offset: d.offset };
    if (!omit(d.bytesPerRow)) layout.bytesPerRow = d.bytesPerRow;
    if (!omit(d.rowsPerImage)) layout.rowsPerImage = d.rowsPerImage;
    get(queueId).writeTexture(
        { texture: get(d.texture), mipLevel: d.mip, origin: { x: d.x, y: d.y, z: d.z }, aspect: ASPECTS[d.aspect] ?? "all" },
        data.slice(), layout, { width: d.w, height: d.h, depthOrArrayLayers: d.d });
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

function blendComponent(c) {
    return { operation: BLEND_OPS[c[0]] ?? "add", srcFactor: BLEND_FACTORS[c[1]] ?? "one", dstFactor: BLEND_FACTORS[c[2]] ?? "zero" };
}

function stencilFace(f) {
    return { compare: COMPARE[f[0]] ?? "always", failOp: STENCIL_OPS[f[1]] ?? "keep",
        depthFailOp: STENCIL_OPS[f[2]] ?? "keep", passOp: STENCIL_OPS[f[3]] ?? "keep" };
}

// the JSON Wgpu.Browser.cs writes for a WGPURenderPipelineDescriptor; handle fields carry ids
export function createRenderPipeline(deviceId, json) {
    const d = JSON.parse(json);
    const v = d.vertex;
    const desc = {
        layout: d.layout ? get(d.layout) : "auto",
        vertex: {
            module: get(v.module),
            buffers: v.buffers.map((b) => b === null ? null : {
                arrayStride: b.stride,
                stepMode: b.step === 2 ? "instance" : "vertex",
                attributes: b.attributes.map((a) => ({ format: VERTEX_FORMATS[a[0]], offset: a[1], shaderLocation: a[2] })),
            }),
        },
        primitive: {
            topology: TOPOLOGIES[d.primitive.topology] ?? "triangle-list",
            frontFace: FRONT_FACES[d.primitive.frontFace] ?? "ccw",
            cullMode: CULL_MODES[d.primitive.cullMode] ?? "none",
        },
        multisample: { count: d.multisample.count || 1, mask: d.multisample.mask >>> 0, alphaToCoverageEnabled: !!d.multisample.alphaToCoverage },
    };
    if (v.entryPoint) desc.vertex.entryPoint = v.entryPoint;
    if (v.constants) desc.vertex.constants = constants(v.constants);
    if (d.primitive.stripIndexFormat) desc.primitive.stripIndexFormat = INDEX_FORMATS[d.primitive.stripIndexFormat];
    if (d.primitive.unclippedDepth) desc.primitive.unclippedDepth = true;
    if (d.depthStencil) {
        const ds = d.depthStencil;
        desc.depthStencil = {
            format: TEXTURE_FORMATS[ds.format],
            stencilFront: stencilFace(ds.front),
            stencilBack: stencilFace(ds.back),
            stencilReadMask: ds.readMask >>> 0,
            stencilWriteMask: ds.writeMask >>> 0,
            depthBias: ds.bias,
            depthBiasSlopeScale: ds.slope,
            depthBiasClamp: ds.clamp,
        };
        // webgpu.h's Undefined (2) leaves them out: a stencil-only format takes neither
        if (ds.depthWrite !== 2) desc.depthStencil.depthWriteEnabled = ds.depthWrite === 1;
        if (ds.depthCompare) desc.depthStencil.depthCompare = COMPARE[ds.depthCompare];
    }
    if (d.fragment) {
        const f = d.fragment;
        desc.fragment = {
            module: get(f.module),
            targets: f.targets.map((t) => {
                if (t === null) return null;
                const target = { format: TEXTURE_FORMATS[t.format], writeMask: t.writeMask };
                if (t.blend) target.blend = { color: blendComponent(t.blend[0]), alpha: blendComponent(t.blend[1]) };
                return target;
            }),
        };
        if (f.entryPoint) desc.fragment.entryPoint = f.entryPoint;
        if (f.constants) desc.fragment.constants = constants(f.constants);
    }
    return put(get(deviceId).createRenderPipeline(desc));
}

// {layout, module, entryPoint, constants}
export function createComputePipeline(deviceId, json) {
    const d = JSON.parse(json);
    const desc = { layout: d.layout ? get(d.layout) : "auto", compute: { module: get(d.module) } };
    if (d.entryPoint) desc.compute.entryPoint = d.entryPoint;
    if (d.constants) desc.compute.constants = constants(d.constants);
    return put(get(deviceId).createComputePipeline(desc));
}

export function getBindGroupLayout(pipelineId, groupIndex) {
    return put(get(pipelineId).getBindGroupLayout(groupIndex));
}

// entries: [{binding, visibility, buffer?: [type, dynamic, minSize], sampler?: type,
//            texture?: [sampleType, viewDimension, multisampled], storage?: [access, format, viewDimension]}]
export function createBindGroupLayout(deviceId, json) {
    const entries = JSON.parse(json).map((e) => {
        const entry = { binding: e.binding, visibility: e.visibility };
        if (e.buffer) entry.buffer = { type: BUFFER_BINDINGS[e.buffer[0]], hasDynamicOffset: !!e.buffer[1], minBindingSize: e.buffer[2] };
        else if (e.sampler) entry.sampler = { type: SAMPLER_BINDINGS[e.sampler] };
        else if (e.texture) {
            entry.texture = { sampleType: SAMPLE_TYPES[e.texture[0]], multisampled: !!e.texture[2] };
            if (e.texture[1]) entry.texture.viewDimension = VIEW_DIMENSIONS[e.texture[1]];
        } else if (e.storage) {
            entry.storageTexture = { access: STORAGE_ACCESS[e.storage[0]], format: TEXTURE_FORMATS[e.storage[1]] };
            if (e.storage[2]) entry.storageTexture.viewDimension = VIEW_DIMENSIONS[e.storage[2]];
        }
        return entry;
    });
    return put(get(deviceId).createBindGroupLayout({ entries }));
}

// layouts: ids, 0 for an empty slot
export function createPipelineLayout(deviceId, json) {
    const layouts = JSON.parse(json).map((id) => id ? get(id) : null);
    return put(get(deviceId).createPipelineLayout({ bindGroupLayouts: layouts }));
}

// entries: [{binding, buffer?, offset?, size?, sampler?, textureView?}] with ids; size -1: the rest.
export function createBindGroup(deviceId, layoutId, entriesJson) {
    const deps = [];
    const entries = JSON.parse(entriesJson).map((e) => {
        let resource;
        if (e.buffer) {
            const b = get(e.buffer);
            resource = { buffer: b, offset: e.offset ?? 0 };
            if (!omit(e.size)) resource.size = e.size;
            deps.push(hold(b));
        } else if (e.sampler) {
            resource = get(e.sampler);
        } else {
            resource = get(e.textureView);
            deps.push(hold(resource));
        }
        return { binding: e.binding, resource };
    });
    const group = get(deviceId).createBindGroup({ layout: get(layoutId), entries });
    group.__deps = deps;
    return put(group);
}

// {address: [u, v, w], mag, min, mip, lodMin, lodMax, compare, anisotropy}
export function createSampler(deviceId, json) {
    const d = JSON.parse(json);
    const desc = {
        addressModeU: ADDRESS_MODES[d.address[0]] ?? "clamp-to-edge",
        addressModeV: ADDRESS_MODES[d.address[1]] ?? "clamp-to-edge",
        addressModeW: ADDRESS_MODES[d.address[2]] ?? "clamp-to-edge",
        magFilter: FILTERS[d.mag] ?? "nearest",
        minFilter: FILTERS[d.min] ?? "nearest",
        mipmapFilter: FILTERS[d.mip] ?? "nearest",
        lodMinClamp: d.lodMin,
        lodMaxClamp: d.lodMax,
        maxAnisotropy: Math.max(1, d.anisotropy),
    };
    if (d.compare) desc.compare = COMPARE[d.compare];
    return put(get(deviceId).createSampler(desc));
}

// ---- Command encoding --------------------------------------------------------

export function createCommandEncoder(deviceId) {
    return put(get(deviceId).createCommandEncoder());
}

// {color: [{view, depthSlice, resolve, load, store, clear: [r, g, b, a]} | null],
//  depth: {view, depthLoad, depthStore, depthClear, depthReadOnly, stencilLoad, stencilStore, stencilClear, stencilReadOnly}}
export function beginRenderPass(encoderId, json) {
    const d = JSON.parse(json);
    const desc = {
        colorAttachments: d.color.map((c) => {
            if (c === null) return null;
            const a = { view: get(c.view), loadOp: LOAD_OPS[c.load] ?? "load", storeOp: STORE_OPS[c.store] ?? "store",
                clearValue: { r: c.clear[0], g: c.clear[1], b: c.clear[2], a: c.clear[3] } };
            if (!omit(c.depthSlice)) a.depthSlice = c.depthSlice;
            if (c.resolve) a.resolveTarget = get(c.resolve);
            return a;
        }),
    };
    if (d.depth) {
        const s = d.depth;
        const a = { view: get(s.view) };
        if (s.depthReadOnly) a.depthReadOnly = true;
        else if (s.depthLoad) { a.depthLoadOp = LOAD_OPS[s.depthLoad]; a.depthStoreOp = STORE_OPS[s.depthStore] ?? "store"; a.depthClearValue = s.depthClear; }
        if (s.stencilReadOnly) a.stencilReadOnly = true;
        else if (s.stencilLoad) { a.stencilLoadOp = LOAD_OPS[s.stencilLoad]; a.stencilStoreOp = STORE_OPS[s.stencilStore] ?? "store"; a.stencilClearValue = s.stencilClear; }
        desc.depthStencilAttachment = a;
    }
    return put(get(encoderId).beginRenderPass(desc));
}

export function beginComputePass(encoderId) {
    return put(get(encoderId).beginComputePass());
}

function copyTexture(c, i) {
    return { texture: get(c[i]), mipLevel: c[i + 1], origin: { x: c[i + 2], y: c[i + 3], z: c[i + 4] }, aspect: ASPECTS[c[i + 5]] ?? "all" };
}

function copyBuffer(c, i) {
    const layout = { buffer: get(c[i]), offset: c[i + 1] };
    if (!omit(c[i + 2])) layout.bytesPerRow = c[i + 2];
    if (!omit(c[i + 3])) layout.rowsPerImage = c[i + 3];
    return layout;
}

const extent = (c, i) => ({ width: c[i], height: c[i + 1], depthOrArrayLayers: c[i + 2] });

// Every encoder and pass command as Wgpu.Browser.cs records them -- [op, argument count,
// arguments...] -- replayed in order: one call from the worker for what was a round trip each.
export function runPassCommands(view) {
    const c = view.slice();
    let offsets = [];
    for (let i = 0; i < c.length; i += 2 + c[i + 1]) {
        const op = c[i], a = i + 2;
        if (op === 9) { release(c[a]); continue; }
        if (op === 15) { for (let k = 0; k < c[i + 1]; k++) offsets.push(c[a + k]); continue; }
        const o = get(c[a]);
        switch (op) {
            case 1: o.setPipeline(get(c[a + 1])); break;
            case 2: o.setBindGroup(c[a + 1], c[a + 2] ? get(c[a + 2]) : null, offsets); offsets = []; break;
            case 3: if (c[a + 4] < 0) o.setVertexBuffer(c[a + 1], c[a + 2] ? get(c[a + 2]) : null, c[a + 3]);
                    else o.setVertexBuffer(c[a + 1], c[a + 2] ? get(c[a + 2]) : null, c[a + 3], c[a + 4]); break;
            case 4: if (c[a + 4] < 0) o.setIndexBuffer(get(c[a + 1]), INDEX_FORMATS[c[a + 2]], c[a + 3]);
                    else o.setIndexBuffer(get(c[a + 1]), INDEX_FORMATS[c[a + 2]], c[a + 3], c[a + 4]); break;
            case 5: o.drawIndexed(c[a + 1], c[a + 2], c[a + 3], c[a + 4], c[a + 5]); break;
            case 6: o.setScissorRect(c[a + 1], c[a + 2], c[a + 3], c[a + 4]); break;
            case 7: o.setViewport(c[a + 1], c[a + 2], c[a + 3], c[a + 4], c[a + 5], c[a + 6]); break;
            case 8: o.end(); break;
            case 10: o.draw(c[a + 1], c[a + 2], c[a + 3], c[a + 4]); break;
            case 11: o.drawIndirect(get(c[a + 1]), c[a + 2]); break;
            case 12: o.drawIndexedIndirect(get(c[a + 1]), c[a + 2]); break;
            case 13: o.setBlendConstant({ r: c[a + 1], g: c[a + 2], b: c[a + 3], a: c[a + 4] }); break;
            case 14: o.setStencilReference(c[a + 1]); break;
            case 16: o.dispatchWorkgroups(c[a + 1], c[a + 2], c[a + 3]); break;
            case 17: o.dispatchWorkgroupsIndirect(get(c[a + 1]), c[a + 2]); break;
            case 18: o.copyBufferToBuffer(get(c[a + 1]), c[a + 2], get(c[a + 3]), c[a + 4], c[a + 5]); break;
            case 19: o.copyTextureToTexture(copyTexture(c, a + 1), copyTexture(c, a + 7), extent(c, a + 13)); break;
            case 20: o.copyBufferToTexture(copyBuffer(c, a + 1), copyTexture(c, a + 5), extent(c, a + 11)); break;
            case 21: o.copyTextureToBuffer(copyTexture(c, a + 1), copyBuffer(c, a + 7), extent(c, a + 11)); break;
            case 22: if (c[a + 3] < 0) o.clearBuffer(get(c[a + 1]), c[a + 2]); else o.clearBuffer(get(c[a + 1]), c[a + 2], c[a + 3]); break;
        }
    }
}

export function finishEncoder(encoderId) {
    return put(get(encoderId).finish());
}

export function submit(queueId, commandBufferIds) {
    // Frame counter for the on-screen FPS readout (see main.js). Counted HERE, at the point work is
    // handed to the GPU, so it reports frames the app actually drew -- not requestAnimationFrame
    // callbacks, which keep ticking at display rate whether or not anything was rendered.
    globalThis.__wpfFrameCount = (globalThis.__wpfFrameCount || 0) + 1;
    const queue = get(queueId);
    queue.submit(Array.from(commandBufferIds.slice(), (id) => get(id)));
    // what was let go of before this submit is destroyed once it has run (see unref)
    const batch = pendingDestroy;
    pendingDestroy = [];
    outstanding++;
    queue.onSubmittedWorkDone().then(() => {
        outstanding--;
        for (const o of batch) o.destroy();
    });
}

// wgpuDevicePoll's "queue empty": no submitted work still running
export function outstandingWork() {
    return outstanding;
}

// ---- Buffer mapping (Wgpu.Browser.cs: a mapped range is a copy in wasm memory) --------------------

export function mapAsync(bufferId, mode, offset, size) {
    const b = get(bufferId);
    b.__mapState = 0;
    b.__mapOffset = offset;
    const p = size < 0 ? b.mapAsync(mode, offset) : b.mapAsync(mode, offset, size);
    p.then(() => {
        b.__mapped = size < 0 ? b.getMappedRange(offset) : b.getMappedRange(offset, size);
        b.__mapState = 1;
    }, (e) => {
        b.__mapState = 2;
        b.__mapError = String(e?.message ?? e);
    });
}

// 0 still mapping, 1 mapped, 2 failed
export function mapState(bufferId) {
    return get(bufferId)?.__mapState ?? 2;
}

export function mappedSize(bufferId) {
    return get(bufferId)?.__mapped?.byteLength ?? 0;
}

export function readMapped(bufferId, offset, destination) {
    const b = get(bufferId);
    destination.set(new Uint8Array(b.__mapped, offset - b.__mapOffset, destination.length), 0);
}

export function writeMapped(bufferId, offset, source) {
    const b = get(bufferId);
    new Uint8Array(b.__mapped, offset - b.__mapOffset, source.length).set(source.slice());
}

export function unmap(bufferId) {
    const b = get(bufferId);
    if (!b) return;
    b.__mapped = null;
    b.__mapState = undefined;
    b.unmap();
}

// ---- Error scopes ------------------------------------------------------------------------------

const scopes = new Map();
let nextScope = 1;

export function pushErrorScope(deviceId, filter) {
    get(deviceId).pushErrorScope(ERROR_FILTERS[filter] ?? "validation");
}

export function popErrorScope(deviceId) {
    const id = nextScope++;
    const s = { state: 0, message: "" };
    scopes.set(id, s);
    get(deviceId).popErrorScope().then((err) => {
        if (!err) { s.state = 1; return; }
        s.state = typeof GPUValidationError !== "undefined" && err instanceof GPUValidationError ? 2
            : typeof GPUOutOfMemoryError !== "undefined" && err instanceof GPUOutOfMemoryError ? 3 : 4;
        s.message = err.message ?? "";
    }, (e) => { s.state = 5; s.message = String(e?.message ?? e); });
    return id;
}

// 0 pending; else webgpu.h's WGPUErrorType (1 none, 2 validation, 3 out of memory, 4 internal), 5 the pop failed
export function errorScopeState(id) {
    return scopes.get(id)?.state ?? 5;
}

export function takeErrorScopeMessage(id) {
    const s = scopes.get(id);
    scopes.delete(id);
    return s?.message ?? "";
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

export function bytesLength(id) {
    const o = get(id);
    return o ? o.length : 0;
}

// Copies parked bytes into a managed array (a MemoryView over it) and drops them.
export function copyBytes(id, destination) {
    const o = get(id);
    objs.delete(id);
    if (o) destination.set(o, 0);
}

// WGPU_DAMAGE_VERIFY: compare two same-sized RGBA8 textures pixel for pixel without bringing
// either into managed memory. The copies are submitted before the first await, so they see the
// textures as they are when this is called. Returns "diff minX minY maxX maxY maxDelta" followed
// by "|x,y,r,g,b,a,R,G,B,A" for the first maxSamples differing pixels (lower case = texture A).
export async function compareTextures(deviceId, textureA, textureB, width, height, maxSamples) {
    const device = get(deviceId);
    const bytesPerRow = Math.ceil((width * 4) / 256) * 256;
    const mk = () => device.createBuffer({ size: bytesPerRow * height, usage: GPUBufferUsage.COPY_DST | GPUBufferUsage.MAP_READ });
    const ba = mk(), bb = mk();
    const enc = device.createCommandEncoder();
    for (const [tex, buf] of [[textureA, ba], [textureB, bb]])
        enc.copyTextureToBuffer({ texture: get(tex) }, { buffer: buf, bytesPerRow, rowsPerImage: height },
            { width, height, depthOrArrayLayers: 1 });
    device.queue.submit([enc.finish()]);
    try {
        await Promise.all([ba.mapAsync(GPUMapMode.READ), bb.mapAsync(GPUMapMode.READ)]);
        const a8 = new Uint8Array(ba.getMappedRange()), b8 = new Uint8Array(bb.getMappedRange());
        const a32 = new Uint32Array(a8.buffer, a8.byteOffset, a8.byteLength >> 2);
        const b32 = new Uint32Array(b8.buffer, b8.byteOffset, b8.byteLength >> 2);
        let diff = 0, minX = 2147483647, minY = 2147483647, maxX = -1, maxY = -1, maxDelta = 0;
        let samples = '';
        const wordsPerRow = bytesPerRow >> 2;
        for (let y = 0; y < height; y++) {
            const row = y * wordsPerRow;
            for (let x = 0; x < width; x++) {
                if (a32[row + x] === b32[row + x]) continue;
                diff++;
                if (x < minX) minX = x; if (x > maxX) maxX = x; if (y < minY) minY = y; if (y > maxY) maxY = y;
                const i = (row + x) << 2;
                for (let c = 0; c < 4; c++) maxDelta = Math.max(maxDelta, Math.abs(a8[i + c] - b8[i + c]));
                if (diff <= maxSamples)
                    samples += `|${x},${y},${a8[i]},${a8[i + 1]},${a8[i + 2]},${a8[i + 3]},${b8[i]},${b8[i + 1]},${b8[i + 2]},${b8[i + 3]}`;
            }
        }
        return `${diff} ${minX} ${minY} ${maxX} ${maxY} ${maxDelta}${samples}`;
    } finally {
        ba.destroy();
        bb.destroy();
    }
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

// Drops a handle (webgpu.h's *Release): see the handle table at the top.
export function release(id) {
    const o = objs.get(id);
    if (o === undefined) return;
    objs.delete(id);
    unref(o);
}

export function handleCount() {
    return objs.size;
}
