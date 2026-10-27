// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// Browser/WebAssembly implementation of the wgpu-native C-ABI surface declared
// across Wgpu*.cs and the Direct3D shim's part of it (Direct3D/Wgpu.D3D.cs,
// D3DUnorderedAccess.cs). Compiled ONLY by Browser/WgpuInterop.Browser.csproj in
// place of the DllImports: identical signatures, managed bodies. Descriptor structs
// are read here in C# and handed to Browser/wgpu-interop.js as JSON of webgpu.h's
// own values (the JS side names them), where a handle table maps the integer ids
// we surface as IntPtr onto live WebGPU objects -- with webgpu.h's reference
// semantics: a view holds its texture, a bind group what it binds.
//
// What webgpu.h does synchronously and the browser only with a Promise -- a buffer
// map, an error scope's pop, the queue's progress -- is a future here: the page
// starts it, and wgpuDevicePoll / wgpuInstanceProcessEvents ask the page how far
// it has got and run the callbacks, as wgpu-native runs them from the same calls.
// A mapped range is a block of wasm memory: the page copies the mapped bytes into
// it, and a writable one back at unmap.
//
// Async boundary: browser WebGPU acquires adapter/device via Promises. The app
// must await WgpuBrowser.InitializeAsync() BEFORE the WPF stack boots; the
// callback-style wgpuInstanceRequestAdapter/wgpuAdapterRequestDevice then
// complete synchronously against the pre-acquired handles, which lets the
// unchanged WgpuContext.Create() spin loop finish on its first iteration.
//

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Wpf.Interop.WebGpu.Browser;

namespace Microsoft.Wpf.Interop.WebGpu
{
    /// <summary>
    /// Browser-side bootstrap for the WebGPU backend. The WASM host must await
    /// <see cref="InitializeAsync"/> (after the dotnet runtime starts, before the
    /// WPF Application runs) so adapter/device exist before the first render.
    /// </summary>
    public static class WgpuBrowser
    {
        internal static bool IsInitialized;
        internal static int AdapterHandle;
        internal static int DeviceHandle;

        /// <param name="moduleUrl">
        /// URL of wgpu-interop.js. Pass null when the host page already registered the
        /// module itself via <c>setModuleImports('wgpuInterop', module)</c> (recommended —
        /// a relative URL here resolves against _framework/, not the page).
        /// </param>
        public static async Task InitializeAsync(string? moduleUrl = null)
        {
            if (moduleUrl != null)
                await WgpuBrowserJs.ImportAsync(moduleUrl);
            if (!WgpuBrowserJs.IsSupported())
                throw new PlatformNotSupportedException("This browser does not expose WebGPU (navigator.gpu).");

            AdapterHandle = await WgpuBrowserJs.RequestAdapter("high-performance");
            if (AdapterHandle == 0)
                throw new InvalidOperationException("WebGPU requestAdapter() returned no adapter.");

            DeviceHandle = await WgpuBrowserJs.RequestDevice(AdapterHandle);
            if (DeviceHandle == 0)
                throw new InvalidOperationException("WebGPU requestDevice() failed.");

            IsInitialized = true;
        }

        internal static void EnsureInitialized()
        {
            if (!IsInitialized)
                throw new InvalidOperationException(
                    "WgpuBrowser.InitializeAsync() must be awaited before the WebGPU compositor starts.");
        }
    }

    internal static unsafe partial class Wgpu
    {
        /// <summary>The largest 2D texture a target may be: WebGPU's default limit, as the native backend's (Wgpu.Native.cs).</summary>
        internal const uint MaxTextureDimension2D = 8192;

        // The instance is a pure fiction on the browser (navigator.gpu is global);
        // use a sentinel outside the JS handle-id range (ids count up from 1).
        private static readonly IntPtr s_instanceSentinel = (IntPtr)int.MaxValue;

        private static string S(WGPUStringView v)
            => v.data == null ? "" : Encoding.UTF8.GetString(v.data, (int)v.length);

        // Reflection-based JsonSerializer is disabled on the wasm runtime by default;
        // descriptors are written with the reflection-free Utf8JsonWriter instead.
        private static Utf8JsonWriter NewJsonWriter(out System.Buffers.ArrayBufferWriter<byte> buffer)
        {
            buffer = new System.Buffers.ArrayBufferWriter<byte>(256);
            return new Utf8JsonWriter(buffer);
        }

        private static string FinishJson(Utf8JsonWriter w, System.Buffers.ArrayBufferWriter<byte> buffer)
        {
            w.Flush();
            w.Dispose();
            return Encoding.UTF8.GetString(buffer.WrittenSpan);
        }

        private static byte* AllocUtf8(string s)
            => (byte*)Marshal.StringToCoTaskMemUTF8(s);

        private static WGPUStringView SV(string s)
            => new WGPUStringView { data = AllocUtf8(s), length = (nuint)Encoding.UTF8.GetByteCount(s) };

        // webgpu.h's "undefined" sentinels go to the page as -1
        private static double U32(uint v) => v == uint.MaxValue ? -1 : v;
        private static double U64(ulong v) => v == ulong.MaxValue ? -1 : v;

        // WGPU_STRLEN (SIZE_MAX): a NUL-terminated string
        private static string Str(WGPUStringView v)
        {
            if (v.data == null) return "";
            if (v.length == nuint.MaxValue) return Marshal.PtrToStringUTF8((IntPtr)v.data) ?? "";
            return Encoding.UTF8.GetString(v.data, (int)v.length);
        }

        private static void WriteConstants(Utf8JsonWriter w, nuint count, IntPtr constants)
        {
            if (count == 0 || constants == IntPtr.Zero) return;
            var c = (Direct3D.WgpuCompute.WGPUConstantEntry*)constants;
            w.WriteStartArray("constants");
            for (nuint i = 0; i < count; i++)
            {
                w.WriteStartArray();
                w.WriteStringValue(Str(c[i].key));
                w.WriteNumberValue(c[i].value);
                w.WriteEndArray();
            }
            w.WriteEndArray();
        }

        // ---- Adapter features and limits (the Direct3D shim's) -------------------------------------

        /// <summary>The feature on the device: the browser's device is made at boot with every
        /// feature of the shim's list the adapter has (wgpu-interop.js requestDevice).</summary>
        internal static uint wgpuAdapterHasFeature(IntPtr adapter, uint feature)
            => WgpuBrowserJs.DeviceHasFeature(WgpuBrowser.DeviceHandle, (int)feature) ? 1u : 0u;

        /// <summary>The device already has the adapter's limits (requestDevice): nothing to ask for.</summary>
        internal static int wgpuAdapterGetLimits(IntPtr adapter, byte* limits) => (int)WGPUStatus.Error;

        // ---- Resources ----------------------------------------------------------

        // usage: webgpu.h's flags are WebGPU's (GPUTextureUsage / GPUBufferUsage) bit for bit
        internal static IntPtr wgpuDeviceCreateTexture(IntPtr device, WGPUTextureDescriptor* d)
        {
            Utf8JsonWriter w = NewJsonWriter(out var buffer);
            w.WriteStartObject();
            w.WriteNumber("usage", (ulong)d->usage);
            w.WriteNumber("dimension", (int)d->dimension);
            w.WriteNumber("w", d->size.width);
            w.WriteNumber("h", d->size.height);
            w.WriteNumber("d", d->size.depthOrArrayLayers == 0 ? 1 : d->size.depthOrArrayLayers);
            w.WriteNumber("format", (int)d->format);
            w.WriteNumber("mips", d->mipLevelCount == 0 ? 1 : d->mipLevelCount);
            w.WriteNumber("samples", d->sampleCount == 0 ? 1 : d->sampleCount);
            if (d->viewFormatCount != 0)
            {
                w.WriteStartArray("viewFormats");
                for (nuint i = 0; i < d->viewFormatCount; i++) w.WriteNumberValue((int)d->viewFormats[i]);
                w.WriteEndArray();
            }
            w.WriteEndObject();
            return (IntPtr)WgpuBrowserJs.CreateTexture((int)device, FinishJson(w, buffer));
        }

        // Buffer sizes and mapped ranges, for the mapping below.
        private sealed class MapRecord
        {
            public ulong Offset;
            public bool Writable;
            public readonly List<(IntPtr Ptr, ulong Offset, int Size, bool Write)> Ranges = new();
        }

        private static readonly Dictionary<IntPtr, MapRecord> s_maps = new();

        internal static IntPtr wgpuDeviceCreateBuffer(IntPtr device, WGPUBufferDescriptor* descriptor)
        {
            var b = (IntPtr)WgpuBrowserJs.CreateBuffer(
                (int)device, descriptor->size, (double)(ulong)descriptor->usage,
                descriptor->mappedAtCreation != 0);
            if (descriptor->mappedAtCreation != 0)
                lock (s_maps) s_maps[b] = new MapRecord { Offset = 0, Writable = true };
            return b;
        }

        internal static IntPtr wgpuDeviceCreateCommandEncoder(IntPtr device, IntPtr descriptor)
            => (IntPtr)WgpuBrowserJs.CreateCommandEncoder((int)device);

        internal static IntPtr wgpuTextureCreateView(IntPtr texture, IntPtr descriptor)
        {
            if (descriptor == IntPtr.Zero) return (IntPtr)WgpuBrowserJs.CreateTextureView((int)texture, "");
            var d = (WGPUTextureViewDescriptor*)descriptor;
            Utf8JsonWriter w = NewJsonWriter(out var buffer);
            w.WriteStartObject();
            w.WriteNumber("format", (int)d->format);
            w.WriteNumber("dimension", (int)d->dimension);
            w.WriteNumber("baseMip", d->baseMipLevel);
            w.WriteNumber("mips", U32(d->mipLevelCount));
            w.WriteNumber("baseLayer", d->baseArrayLayer);
            w.WriteNumber("layers", U32(d->arrayLayerCount));
            w.WriteNumber("aspect", (int)d->aspect);
            w.WriteNumber("usage", (ulong)d->usage);
            w.WriteEndObject();
            return (IntPtr)WgpuBrowserJs.CreateTextureView((int)texture, FinishJson(w, buffer));
        }

        internal static IntPtr wgpuDeviceCreateSampler(IntPtr device, WGPUSamplerDescriptor* d)
        {
            Utf8JsonWriter w = NewJsonWriter(out var buffer);
            w.WriteStartObject();
            w.WriteStartArray("address");
            w.WriteNumberValue((int)d->addressModeU);
            w.WriteNumberValue((int)d->addressModeV);
            w.WriteNumberValue((int)d->addressModeW);
            w.WriteEndArray();
            w.WriteNumber("mag", (int)d->magFilter);
            w.WriteNumber("min", (int)d->minFilter);
            w.WriteNumber("mip", (int)d->mipmapFilter);
            w.WriteNumber("lodMin", d->lodMinClamp);
            w.WriteNumber("lodMax", d->lodMaxClamp);
            w.WriteNumber("compare", (int)d->compare);
            w.WriteNumber("anisotropy", d->maxAnisotropy);
            w.WriteEndObject();
            return (IntPtr)WgpuBrowserJs.CreateSampler((int)device, FinishJson(w, buffer));
        }

        internal static IntPtr wgpuDeviceCreateBindGroupLayout(IntPtr device, WGPUBindGroupLayoutDescriptor* d)
        {
            Utf8JsonWriter w = NewJsonWriter(out var buffer);
            w.WriteStartArray();
            for (nuint i = 0; i < d->entryCount; i++)
            {
                WGPUBindGroupLayoutEntry* e = d->entries + i;
                w.WriteStartObject();
                w.WriteNumber("binding", e->binding);
                w.WriteNumber("visibility", e->visibility);
                if (e->buffer.type != WGPUBufferBindingType.BindingNotUsed)
                {
                    w.WriteStartArray("buffer");
                    w.WriteNumberValue((int)e->buffer.type);
                    w.WriteNumberValue(e->buffer.hasDynamicOffset);
                    w.WriteNumberValue(e->buffer.minBindingSize);
                    w.WriteEndArray();
                }
                else if (e->sampler.type != WGPUSamplerBindingType.BindingNotUsed)
                    w.WriteNumber("sampler", (int)e->sampler.type);
                else if (e->texture.sampleType != WGPUTextureSampleType.BindingNotUsed)
                {
                    w.WriteStartArray("texture");
                    w.WriteNumberValue((int)e->texture.sampleType);
                    w.WriteNumberValue((int)e->texture.viewDimension);
                    w.WriteNumberValue(e->texture.multisampled);
                    w.WriteEndArray();
                }
                else if (e->storageTexture.access != 0)
                {
                    w.WriteStartArray("storage");
                    w.WriteNumberValue(e->storageTexture.access);
                    w.WriteNumberValue((int)e->storageTexture.format);
                    w.WriteNumberValue((int)e->storageTexture.viewDimension);
                    w.WriteEndArray();
                }
                w.WriteEndObject();
            }
            w.WriteEndArray();
            return (IntPtr)WgpuBrowserJs.CreateBindGroupLayout((int)device, FinishJson(w, buffer));
        }

        internal static IntPtr wgpuDeviceCreatePipelineLayout(IntPtr device, WGPUPipelineLayoutDescriptor* d)
        {
            Utf8JsonWriter w = NewJsonWriter(out var buffer);
            w.WriteStartArray();
            for (nuint i = 0; i < d->bindGroupLayoutCount; i++) w.WriteNumberValue((int)d->bindGroupLayouts[i]);
            w.WriteEndArray();
            return (IntPtr)WgpuBrowserJs.CreatePipelineLayout((int)device, FinishJson(w, buffer));
        }

        internal static IntPtr wgpuDeviceCreateBindGroup(IntPtr device, WGPUBindGroupDescriptor* descriptor)
        {
            Utf8JsonWriter w = NewJsonWriter(out var buffer);
            w.WriteStartArray();
            for (nuint i = 0; i < descriptor->entryCount; i++)
            {
                WGPUBindGroupEntry* e = descriptor->entries + i;
                w.WriteStartObject();
                w.WriteNumber("binding", e->binding);
                if (e->buffer != IntPtr.Zero)
                {
                    w.WriteNumber("buffer", (int)e->buffer);
                    w.WriteNumber("offset", e->offset);
                    w.WriteNumber("size", U64(e->size));
                }
                else if (e->sampler != IntPtr.Zero)
                {
                    w.WriteNumber("sampler", (int)e->sampler);
                }
                else
                {
                    w.WriteNumber("textureView", (int)e->textureView);
                }
                w.WriteEndObject();
            }
            w.WriteEndArray();
            return (IntPtr)WgpuBrowserJs.CreateBindGroup((int)device, (int)descriptor->layout, FinishJson(w, buffer));
        }

        internal static IntPtr wgpuRenderPipelineGetBindGroupLayout(IntPtr renderPipeline, uint groupIndex)
            => (IntPtr)WgpuBrowserJs.GetBindGroupLayout((int)renderPipeline, (int)groupIndex);

        internal static IntPtr wgpuDeviceCreateShaderModule(IntPtr device, WGPUShaderModuleDescriptor* descriptor)
        {
            // WGSL is the only source either the engine or the Direct3D shim's translator makes.
            var chain = (WGPUChainedStruct*)descriptor->nextInChain;
            if (chain == null || chain->sType != WGPUSType_ShaderSourceWGSL)
                throw new NotSupportedException("Browser backend expects a WGSL shader source chain.");
            var wgsl = (WGPUShaderSourceWGSL*)chain;
            return (IntPtr)WgpuBrowserJs.CreateShaderModule((int)device, Str(wgsl->code));
        }

        internal static IntPtr wgpuDeviceCreateRenderPipeline(IntPtr device, WGPURenderPipelineDescriptor* descriptor)
        {
            Utf8JsonWriter w = NewJsonWriter(out var buffer);
            w.WriteStartObject();
            w.WriteNumber("layout", (int)descriptor->layout);

            w.WriteStartObject("vertex");
            w.WriteNumber("module", (int)descriptor->vertex.module);
            w.WriteString("entryPoint", Str(descriptor->vertex.entryPoint));
            WriteConstants(w, descriptor->vertex.constantCount, descriptor->vertex.constants);
            w.WriteStartArray("buffers");
            for (nuint i = 0; i < descriptor->vertex.bufferCount; i++)
            {
                WGPUVertexBufferLayout* bl = descriptor->vertex.buffers + i;
                // webgpu.h's unused slot: no attributes and no step mode
                if (bl->attributeCount == 0 && bl->stepMode == WGPUVertexStepMode.Undefined) { w.WriteNullValue(); continue; }
                w.WriteStartObject();
                w.WriteNumber("stride", bl->arrayStride);
                w.WriteNumber("step", (int)bl->stepMode);
                w.WriteStartArray("attributes");
                for (nuint j = 0; j < bl->attributeCount; j++)
                {
                    WGPUVertexAttribute* at = bl->attributes + j;
                    w.WriteStartArray();
                    w.WriteNumberValue((int)at->format);
                    w.WriteNumberValue(at->offset);
                    w.WriteNumberValue(at->shaderLocation);
                    w.WriteEndArray();
                }
                w.WriteEndArray();
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteEndObject();

            w.WriteStartObject("primitive");
            w.WriteNumber("topology", (int)descriptor->primitive.topology);
            w.WriteNumber("stripIndexFormat", (int)descriptor->primitive.stripIndexFormat);
            w.WriteNumber("frontFace", (int)descriptor->primitive.frontFace);
            w.WriteNumber("cullMode", (int)descriptor->primitive.cullMode);
            w.WriteBoolean("unclippedDepth", descriptor->primitive.unclippedDepth != 0);
            w.WriteEndObject();

            w.WriteStartObject("multisample");
            w.WriteNumber("count", descriptor->multisample.count == 0 ? 1u : descriptor->multisample.count);
            w.WriteNumber("mask", descriptor->multisample.mask);
            w.WriteBoolean("alphaToCoverage", descriptor->multisample.alphaToCoverageEnabled != 0);
            w.WriteEndObject();

            if (descriptor->depthStencil != IntPtr.Zero)
            {
                var ds = (WGPUDepthStencilState*)descriptor->depthStencil;
                w.WriteStartObject("depthStencil");
                w.WriteNumber("format", (int)ds->format);
                w.WriteNumber("depthWrite", (int)ds->depthWriteEnabled);
                w.WriteNumber("depthCompare", (int)ds->depthCompare);
                WriteFace(w, "front", ds->stencilFront);
                WriteFace(w, "back", ds->stencilBack);
                w.WriteNumber("readMask", ds->stencilReadMask);
                w.WriteNumber("writeMask", ds->stencilWriteMask);
                w.WriteNumber("bias", ds->depthBias);
                w.WriteNumber("slope", ds->depthBiasSlopeScale);
                w.WriteNumber("clamp", ds->depthBiasClamp);
                w.WriteEndObject();
            }

            if (descriptor->fragment != null)
            {
                w.WriteStartObject("fragment");
                w.WriteNumber("module", (int)descriptor->fragment->module);
                w.WriteString("entryPoint", Str(descriptor->fragment->entryPoint));
                WriteConstants(w, descriptor->fragment->constantCount, descriptor->fragment->constants);
                w.WriteStartArray("targets");
                for (nuint i = 0; i < descriptor->fragment->targetCount; i++)
                {
                    WGPUColorTargetState* t = descriptor->fragment->targets + i;
                    if (t->format == WGPUTextureFormat.Undefined) { w.WriteNullValue(); continue; }
                    w.WriteStartObject();
                    w.WriteNumber("format", (int)t->format);
                    w.WriteNumber("writeMask", t->writeMask);
                    if (t->blend != null)
                    {
                        w.WriteStartArray("blend");
                        WriteBlend(w, t->blend->color);
                        WriteBlend(w, t->blend->alpha);
                        w.WriteEndArray();
                    }
                    w.WriteEndObject();
                }
                w.WriteEndArray();
                w.WriteEndObject();
            }

            w.WriteEndObject();
            return (IntPtr)WgpuBrowserJs.CreateRenderPipeline((int)device, FinishJson(w, buffer));

            static void WriteBlend(Utf8JsonWriter w, WGPUBlendComponent c)
            {
                w.WriteStartArray();
                w.WriteNumberValue((int)c.operation);
                w.WriteNumberValue((int)c.srcFactor);
                w.WriteNumberValue((int)c.dstFactor);
                w.WriteEndArray();
            }

            static void WriteFace(Utf8JsonWriter w, string name, WGPUStencilFaceState f)
            {
                w.WriteStartArray(name);
                w.WriteNumberValue((int)f.compare);
                w.WriteNumberValue((int)f.failOp);
                w.WriteNumberValue((int)f.depthFailOp);
                w.WriteNumberValue((int)f.passOp);
                w.WriteEndArray();
            }
        }

        internal static IntPtr ComputePipeline(IntPtr device, Direct3D.WgpuCompute.WGPUComputePipelineDescriptor* d)
        {
            Utf8JsonWriter w = NewJsonWriter(out var buffer);
            w.WriteStartObject();
            w.WriteNumber("layout", (int)d->layout);
            w.WriteNumber("module", (int)d->compute.module);
            w.WriteString("entryPoint", Str(d->compute.entryPoint));
            WriteConstants(w, d->compute.constantCount, (IntPtr)d->compute.constants);
            w.WriteEndObject();
            return (IntPtr)WgpuBrowserJs.CreateComputePipeline((int)device, FinishJson(w, buffer));
        }

        // ---- Uploads --------------------------------------------------------------

        internal static void wgpuQueueWriteBuffer(IntPtr queue, IntPtr buffer, ulong bufferOffset, void* data, nuint size)
            => WgpuBrowserJs.WriteBuffer((int)queue, (int)buffer, bufferOffset, new Span<byte>(data, (int)size));

        internal static void wgpuQueueWriteTexture(
            IntPtr queue, WGPUTexelCopyTextureInfo* destination, void* data, nuint dataSize,
            WGPUTexelCopyBufferLayout* dataLayout, WGPUExtent3D* writeSize)
        {
            Utf8JsonWriter w = NewJsonWriter(out var buffer);
            w.WriteStartObject();
            w.WriteNumber("texture", (int)destination->texture);
            w.WriteNumber("mip", destination->mipLevel);
            w.WriteNumber("x", destination->origin.x);
            w.WriteNumber("y", destination->origin.y);
            w.WriteNumber("z", destination->origin.z);
            w.WriteNumber("aspect", (int)destination->aspect);
            w.WriteNumber("offset", dataLayout->offset);
            w.WriteNumber("bytesPerRow", U32(dataLayout->bytesPerRow));
            w.WriteNumber("rowsPerImage", U32(dataLayout->rowsPerImage));
            w.WriteNumber("w", writeSize->width);
            w.WriteNumber("h", writeSize->height);
            w.WriteNumber("d", writeSize->depthOrArrayLayers == 0 ? 1 : writeSize->depthOrArrayLayers);
            w.WriteEndObject();
            WgpuBrowserJs.WriteTexture((int)queue, FinishJson(w, buffer), new Span<byte>(data, (int)dataSize));
        }

        // ---- Command encoding -------------------------------------------------------

        internal static IntPtr wgpuCommandEncoderBeginRenderPass(IntPtr commandEncoder, WGPURenderPassDescriptor* descriptor)
        {
            Utf8JsonWriter w = NewJsonWriter(out var buffer);
            w.WriteStartObject();
            w.WriteStartArray("color");
            for (nuint i = 0; i < descriptor->colorAttachmentCount; i++)
            {
                WGPURenderPassColorAttachment* c = descriptor->colorAttachments + i;
                if (c->view == IntPtr.Zero) { w.WriteNullValue(); continue; }
                w.WriteStartObject();
                w.WriteNumber("view", (int)c->view);
                w.WriteNumber("depthSlice", U32(c->depthSlice));
                w.WriteNumber("resolve", (int)c->resolveTarget);
                w.WriteNumber("load", (int)c->loadOp);
                w.WriteNumber("store", (int)c->storeOp);
                w.WriteStartArray("clear");
                w.WriteNumberValue(c->clearValue.r);
                w.WriteNumberValue(c->clearValue.g);
                w.WriteNumberValue(c->clearValue.b);
                w.WriteNumberValue(c->clearValue.a);
                w.WriteEndArray();
                w.WriteEndObject();
            }
            w.WriteEndArray();
            if (descriptor->depthStencilAttachment != IntPtr.Zero)
            {
                var dsa = (WGPURenderPassDepthStencilAttachment*)descriptor->depthStencilAttachment;
                w.WriteStartObject("depth");
                w.WriteNumber("view", (int)dsa->view);
                w.WriteNumber("depthLoad", (int)dsa->depthLoadOp);
                w.WriteNumber("depthStore", (int)dsa->depthStoreOp);
                w.WriteNumber("depthClear", float.IsNaN(dsa->depthClearValue) ? 1f : dsa->depthClearValue);
                w.WriteBoolean("depthReadOnly", dsa->depthReadOnly != 0);
                w.WriteNumber("stencilLoad", (int)dsa->stencilLoadOp);
                w.WriteNumber("stencilStore", (int)dsa->stencilStoreOp);
                w.WriteNumber("stencilClear", dsa->stencilClearValue);
                w.WriteBoolean("stencilReadOnly", dsa->stencilReadOnly != 0);
                w.WriteEndObject();
            }
            w.WriteEndObject();
            FlushPassCommands();
            return (IntPtr)WgpuBrowserJs.BeginRenderPass((int)commandEncoder, FinishJson(w, buffer));
        }

        internal static IntPtr BeginComputePass(IntPtr commandEncoder)
        {
            FlushPassCommands();
            return (IntPtr)WgpuBrowserJs.BeginComputePass((int)commandEncoder);
        }

        // ---- Encoder and pass commands, recorded and replayed in one call --------------------------
        //
        // The app's UI thread is a worker and WebGPU lives on the page's thread, so every [JSImport] is
        // a blocking round trip between the two. A pass is a few calls per draw, a display with
        // app's alarm, trend and grid controls in it some two hundred draws, and the round trips
        // alone took the frame to half a second (2 fps dragging them). Commands that return nothing and
        // name only objects that already exist -- a pass's, the encoder's copies, releases -- are
        // recorded here, [op, argument count, arguments...], and the page replays them in one call
        // (wgpu-interop.js runPassCommands) before anything that must see them: a pass begun or the
        // encoder finished, a submit, a map, an error scope, presenting, and when the record is full.

        private const int OpSetPipeline = 1, OpSetBindGroup = 2, OpSetVertexBuffer = 3, OpSetIndexBuffer = 4,
                          OpDrawIndexed = 5, OpSetScissorRect = 6, OpSetViewport = 7, OpEnd = 8, OpRelease = 9,
                          OpDraw = 10, OpDrawIndirect = 11, OpDrawIndexedIndirect = 12, OpBlendConstant = 13,
                          OpStencilReference = 14, OpDynamicOffsets = 15, OpDispatch = 16, OpDispatchIndirect = 17,
                          OpCopyBufferToBuffer = 18, OpCopyTextureToTexture = 19, OpCopyBufferToTexture = 20,
                          OpCopyTextureToBuffer = 21, OpClearBuffer = 22;
        [ThreadStatic] private static double[]? t_passCommands;
        [ThreadStatic] private static int t_passCount;

        /// <summary>Starts a record of <paramref name="count"/> arguments; Arg adds each.</summary>
        private static void Op(int op, int count)
        {
            double[] cmds = t_passCommands ??= new double[8192];
            if (t_passCount + count + 2 > cmds.Length) FlushPassCommands();
            cmds[t_passCount++] = op;
            cmds[t_passCount++] = count;
        }

        private static void Arg(double v) => t_passCommands![t_passCount++] = v;

        private static void Record(int op, double a, double b = 0, double c = 0, double d = 0, double e = 0, double f = 0, double g = 0, int count = -1)
        {
            if (count < 0) count = 7;
            Op(op, count);
            Span<double> args = stackalloc double[] { a, b, c, d, e, f, g };
            for (int i = 0; i < count; i++) Arg(args[i]);
        }

        private static void FlushPassCommands()
        {
            if (t_passCount == 0) return;
            int n = t_passCount;
            t_passCount = 0;
            WgpuBrowserJs.RunPassCommands(new Span<double>(t_passCommands, 0, n));
        }

        internal static void wgpuRenderPassEncoderEnd(IntPtr renderPassEncoder) => Record(OpEnd, (int)renderPassEncoder, count: 1);

        internal static void wgpuRenderPassEncoderSetPipeline(IntPtr renderPassEncoder, IntPtr pipeline)
            => Record(OpSetPipeline, (int)renderPassEncoder, (int)pipeline, count: 2);

        internal static void SetBindGroup(IntPtr pass, uint groupIndex, IntPtr group, nuint dynamicOffsetCount, uint* dynamicOffsets)
        {
            if (dynamicOffsetCount != 0)
            {
                Op(OpDynamicOffsets, (int)dynamicOffsetCount);
                for (nuint i = 0; i < dynamicOffsetCount; i++) Arg(dynamicOffsets[i]);
            }
            Record(OpSetBindGroup, (int)pass, (int)groupIndex, (int)group, count: 3);
        }

        internal static void wgpuRenderPassEncoderSetBindGroup(
            IntPtr renderPassEncoder, uint groupIndex, IntPtr group, nuint dynamicOffsetCount, uint* dynamicOffsets)
            => SetBindGroup(renderPassEncoder, groupIndex, group, dynamicOffsetCount, dynamicOffsets);

        internal static void wgpuRenderPassEncoderSetVertexBuffer(IntPtr renderPassEncoder, uint slot, IntPtr buffer, ulong offset, ulong size)
            => Record(OpSetVertexBuffer, (int)renderPassEncoder, (int)slot, (int)buffer, offset, U64(size), count: 5);

        internal static void wgpuRenderPassEncoderSetIndexBuffer(IntPtr renderPassEncoder, IntPtr buffer, WGPUIndexFormat format, ulong offset, ulong size)
            => Record(OpSetIndexBuffer, (int)renderPassEncoder, (int)buffer, (int)format, offset, U64(size), count: 5);

        internal static void wgpuRenderPassEncoderDrawIndexed(IntPtr renderPassEncoder, uint indexCount, uint instanceCount, uint firstIndex, int baseVertex, uint firstInstance)
            => Record(OpDrawIndexed, (int)renderPassEncoder, indexCount, instanceCount, firstIndex, baseVertex, firstInstance, count: 6);

        internal static void wgpuRenderPassEncoderDraw(IntPtr pass, uint vertexCount, uint instanceCount, uint firstVertex, uint firstInstance)
            => Record(OpDraw, (int)pass, vertexCount, instanceCount, firstVertex, firstInstance, count: 5);

        internal static void DrawIndirect(IntPtr pass, IntPtr buffer, ulong offset)
            => Record(OpDrawIndirect, (int)pass, (int)buffer, offset, count: 3);

        internal static void DrawIndexedIndirect(IntPtr pass, IntPtr buffer, ulong offset)
            => Record(OpDrawIndexedIndirect, (int)pass, (int)buffer, offset, count: 3);

        internal static void wgpuRenderPassEncoderSetBlendConstant(IntPtr pass, WGPUColor* color)
            => Record(OpBlendConstant, (int)pass, color->r, color->g, color->b, color->a, count: 5);

        internal static void wgpuRenderPassEncoderSetStencilReference(IntPtr pass, uint reference)
            => Record(OpStencilReference, (int)pass, reference, count: 2);

        internal static void wgpuRenderPassEncoderSetScissorRect(IntPtr renderPassEncoder, uint x, uint y, uint width, uint height)
            => Record(OpSetScissorRect, (int)renderPassEncoder, x, y, width, height, count: 5);

        // A pass drawing a frame into the top-left of a larger persistent target (the partial-redraw
        // target is kept at its high-water size) maps its NDC to the frame, not to the whole texture.
        // Without it the browser drew every partial frame stretched over the target: a WinForms window
        // 1000x557 on a 1024x768 target had each control 38% lower than its own clip.
        internal static void wgpuRenderPassEncoderSetViewport(IntPtr renderPassEncoder, float x, float y, float width, float height, float minDepth, float maxDepth)
            => Record(OpSetViewport, (int)renderPassEncoder, x, y, width, height, minDepth, maxDepth);

        internal static void Dispatch(IntPtr pass, uint x, uint y, uint z) => Record(OpDispatch, (int)pass, x, y, z, count: 4);

        internal static void DispatchIndirect(IntPtr pass, IntPtr buffer, ulong offset) => Record(OpDispatchIndirect, (int)pass, (int)buffer, offset, count: 3);

        internal static void wgpuCommandEncoderClearBuffer(IntPtr encoder, IntPtr buffer, ulong offset, ulong size)
            => Record(OpClearBuffer, (int)encoder, (int)buffer, offset, U64(size), count: 4);

        internal static void wgpuCommandEncoderCopyBufferToBuffer(IntPtr encoder, IntPtr source, ulong sourceOffset, IntPtr destination, ulong destinationOffset, ulong size)
            => Record(OpCopyBufferToBuffer, (int)encoder, (int)source, sourceOffset, (int)destination, destinationOffset, size, count: 6);

        private static void ArgTexture(WGPUTexelCopyTextureInfo* t)
        {
            Arg((int)t->texture); Arg(t->mipLevel); Arg(t->origin.x); Arg(t->origin.y); Arg(t->origin.z); Arg((int)t->aspect);
        }

        private static void ArgBuffer(WGPUTexelCopyBufferInfo* b)
        {
            Arg((int)b->buffer); Arg(b->layout.offset); Arg(U32(b->layout.bytesPerRow)); Arg(U32(b->layout.rowsPerImage));
        }

        private static void ArgExtent(WGPUExtent3D* e)
        {
            Arg(e->width); Arg(e->height); Arg(e->depthOrArrayLayers == 0 ? 1 : e->depthOrArrayLayers);
        }

        internal static void wgpuCommandEncoderCopyTextureToTexture(IntPtr encoder, WGPUTexelCopyTextureInfo* source, WGPUTexelCopyTextureInfo* destination, WGPUExtent3D* copySize)
        {
            Op(OpCopyTextureToTexture, 16);
            Arg((int)encoder); ArgTexture(source); ArgTexture(destination); ArgExtent(copySize);
        }

        internal static void wgpuCommandEncoderCopyBufferToTexture(
            IntPtr commandEncoder, WGPUTexelCopyBufferInfo* source, WGPUTexelCopyTextureInfo* destination, WGPUExtent3D* copySize)
        {
            Op(OpCopyBufferToTexture, 14);
            Arg((int)commandEncoder); ArgBuffer(source); ArgTexture(destination); ArgExtent(copySize);
        }

        internal static void wgpuCommandEncoderCopyTextureToBuffer(
            IntPtr commandEncoder, WGPUTexelCopyTextureInfo* source, WGPUTexelCopyBufferInfo* destination, WGPUExtent3D* copySize)
        {
            Op(OpCopyTextureToBuffer, 14);
            Arg((int)commandEncoder); ArgTexture(source); ArgBuffer(destination); ArgExtent(copySize);
        }

        internal static IntPtr wgpuCommandEncoderFinish(IntPtr commandEncoder, IntPtr descriptor)
        {
            FlushPassCommands();
            return (IntPtr)WgpuBrowserJs.FinishEncoder((int)commandEncoder);
        }

        internal static void wgpuQueueSubmit(IntPtr queue, nuint commandCount, IntPtr* commands)
        {
            FlushPassCommands();
            Span<int> ids = commandCount <= 16 ? stackalloc int[(int)commandCount] : new int[(int)commandCount];
            for (int i = 0; i < ids.Length; i++) ids[i] = (int)commands[i];
            WgpuBrowserJs.Submit((int)queue, ids);
        }

        // ---- Futures: buffer maps and error scopes --------------------------------------------------

        private enum FutureKind { Map, Scope }

        private sealed class Future
        {
            public ulong Id;
            public FutureKind Kind;
            public IntPtr Buffer;
            public int Scope;
            public IntPtr Callback, User1, User2;
        }

        private static readonly List<Future> s_futures = new();
        private static ulong s_nextFuture = 1;

        private static T? Twin<T>(IntPtr callback) where T : Delegate
        {
            T? twin = TwinOf<T>(callback);
            if (twin == null) Console.Error.WriteLine($"[wgpu] a callback without a managed twin (0x{callback:x}) was not called: Mono cannot call it (Wgpu.Callbacks.cs)");
            return twin;
        }

        private static WGPUFuture Pend(Future f)
        {
            lock (s_futures)
            {
                f.Id = s_nextFuture++;
                s_futures.Add(f);
            }
            return new WGPUFuture { id = f.Id };
        }

        /// <summary>Runs the callbacks of what the page has finished (wgpu-native runs them from
        /// wgpuInstanceProcessEvents and wgpuDevicePoll).</summary>
        private static void ProcessFutures()
        {
            Future[] pending;
            lock (s_futures)
            {
                if (s_futures.Count == 0) return;
                pending = s_futures.ToArray();
            }
            foreach (Future f in pending)
            {
                if (f.Kind == FutureKind.Map)
                {
                    int state = WgpuBrowserJs.MapState((int)f.Buffer);
                    if (state == 0) continue;
                    lock (s_futures) s_futures.Remove(f);
                    if (state != 1) lock (s_maps) s_maps.Remove(f.Buffer);
                    // the callback's managed twin, never the pointer (Wgpu.Callbacks.cs)
                    if (f.Callback != IntPtr.Zero)
                        Twin<MapTwin>(f.Callback)?.Invoke(state == 1 ? WGPUMapAsyncStatus.Success : WGPUMapAsyncStatus.Error, default, f.User1, f.User2);
                }
                else
                {
                    int state = WgpuBrowserJs.ErrorScopeState(f.Scope);
                    if (state == 0) continue;
                    lock (s_futures) s_futures.Remove(f);
                    string message = WgpuBrowserJs.TakeErrorScopeMessage(f.Scope);
                    if (f.Callback == IntPtr.Zero) continue;
                    byte[] text = Encoding.UTF8.GetBytes(message);
                    fixed (byte* p = text)
                    {
                        var view = new WGPUStringView { data = p, length = (nuint)text.Length };
                        Twin<ScopeTwin>(f.Callback)?.Invoke(
                            state == 5 ? WGPUPopErrorScopeStatus.Error : WGPUPopErrorScopeStatus.Success,
                            state == 5 ? WGPUErrorType.Unknown : (WGPUErrorType)state, view, f.User1, f.User2);
                    }
                }
            }
        }

        internal static WGPUFuture wgpuBufferMapAsync(
            IntPtr buffer, WGPUMapMode mode, nuint offset, nuint size, WGPUBufferMapCallbackInfo callbackInfo)
        {
            FlushPassCommands();
            lock (s_maps) s_maps[buffer] = new MapRecord { Offset = offset, Writable = (mode & WGPUMapMode.Write) != 0 };
            WgpuBrowserJs.MapAsync((int)buffer, (int)(ulong)mode, offset, size == nuint.MaxValue ? -1 : size);
            return Pend(new Future { Kind = FutureKind.Map, Buffer = buffer, Callback = callbackInfo.callback, User1 = callbackInfo.userdata1, User2 = callbackInfo.userdata2 });
        }

        /// <summary>A copy of the mapped bytes in wasm memory, valid until unmap; a writable one goes
        /// back to the buffer then.</summary>
        private static void* MappedRange(IntPtr buffer, nuint offset, nuint size, bool write)
        {
            MapRecord? rec;
            lock (s_maps) s_maps.TryGetValue(buffer, out rec);
            if (rec == null) return null;
            if (size == nuint.MaxValue)
                size = (nuint)Math.Max(0L, (long)WgpuBrowserJs.MappedSize((int)buffer) - (long)(offset - rec.Offset));
            void* p = NativeMemory.Alloc(Math.Max((nuint)1, size));
            if (size != 0) WgpuBrowserJs.ReadMapped((int)buffer, offset, new Span<byte>(p, (int)size));
            lock (s_maps) rec.Ranges.Add(((IntPtr)p, offset, (int)size, write && rec.Writable));
            return p;
        }

        internal static void* wgpuBufferGetMappedRange(IntPtr buffer, nuint offset, nuint size) => MappedRange(buffer, offset, size, write: true);

        internal static void* wgpuBufferGetConstMappedRange(IntPtr buffer, nuint offset, nuint size) => MappedRange(buffer, offset, size, write: false);

        internal static void wgpuBufferUnmap(IntPtr buffer)
        {
            MapRecord? rec;
            lock (s_maps) { s_maps.TryGetValue(buffer, out rec); s_maps.Remove(buffer); }
            if (rec != null)
                foreach (var (ptr, offset, size, write) in rec.Ranges)
                {
                    if (write && size != 0) WgpuBrowserJs.WriteMapped((int)buffer, offset, new Span<byte>((void*)ptr, size));
                    NativeMemory.Free((void*)ptr);
                }
            WgpuBrowserJs.Unmap((int)buffer);
        }

        internal static void wgpuDevicePushErrorScope(IntPtr device, WGPUErrorFilter filter)
        {
            FlushPassCommands();
            WgpuBrowserJs.PushErrorScope((int)device, (int)filter);
        }

        internal static WGPUFuture wgpuDevicePopErrorScope(IntPtr device, WGPUPopErrorScopeCallbackInfo callbackInfo)
        {
            FlushPassCommands();
            int scope = WgpuBrowserJs.PopErrorScope((int)device);
            return Pend(new Future { Kind = FutureKind.Scope, Scope = scope, Callback = callbackInfo.callback, User1 = callbackInfo.userdata1, User2 = callbackInfo.userdata2 });
        }

        internal static void wgpuInstanceProcessEvents(IntPtr instance)
        {
            FlushPassCommands();
            ProcessFutures();
        }

        /// <summary>wgpu-native's poll: with <paramref name="wait"/>, until the queue has run what was
        /// submitted; the callbacks of whatever finished; whether the queue is empty.</summary>
        internal static uint wgpuDevicePoll(IntPtr device, uint wait, ulong* submissionIndex)
        {
            FlushPassCommands();
            if (wait != 0)
            {
                long until = Environment.TickCount64 + 10_000;
                while (WgpuBrowserJs.OutstandingWork() != 0 && Environment.TickCount64 < until) { }
            }
            ProcessFutures();
            return WgpuBrowserJs.OutstandingWork() == 0 ? 1u : 0u;
        }

        // ---- Diagnostics --------------------------------------------------------

        internal static void wgpuSetLogCallback(IntPtr callback, IntPtr userdata) { }
        internal static void wgpuSetLogLevel(WGPULogLevel level) { }

        // ---- Instance / adapter / device ---------------------------------------

        internal static IntPtr wgpuCreateInstance(WGPUInstanceDescriptor* descriptor)
        {
            WgpuBrowser.EnsureInitialized();
            return s_instanceSentinel;
        }

        // The callback-style request entry points are never used on the browser:
        // WgpuContext.Create has a WGPU_BROWSER branch that reads the pre-acquired
        // handles directly (function-pointer round trips have no interpreter thunks
        // on mono-wasm and trap with "memory access out of bounds").
        internal static WGPUFuture wgpuInstanceRequestAdapter(
            IntPtr instance, WGPURequestAdapterOptions* options, WGPURequestAdapterCallbackInfo callbackInfo)
            => throw new NotSupportedException("Use WgpuBrowser.InitializeAsync + WgpuContext.Create on the browser.");

        internal static WGPUFuture wgpuAdapterRequestDevice(
            IntPtr adapter, IntPtr descriptor, WGPURequestDeviceCallbackInfo callbackInfo)
            => throw new NotSupportedException("Use WgpuBrowser.InitializeAsync + WgpuContext.Create on the browser.");

        internal static WGPUStatus wgpuAdapterGetInfo(IntPtr adapter, WGPUAdapterInfo* info)
        {
            string json = WgpuBrowserJs.GetAdapterInfoJson((int)adapter);
            using JsonDocument doc = JsonDocument.Parse(json);
            JsonElement root = doc.RootElement;
            info->vendor = SV(root.GetProperty("vendor").GetString() ?? "");
            info->architecture = SV(root.GetProperty("architecture").GetString() ?? "");
            info->device = SV(root.GetProperty("device").GetString() ?? "");
            info->description = SV(root.GetProperty("description").GetString() ?? "");
            info->backendType = WGPUBackendType.WebGPU;
            info->adapterType = WGPUAdapterType.Unknown;
            return WGPUStatus.Success;
        }

        internal static IntPtr wgpuDeviceGetQueue(IntPtr device)
            => (IntPtr)WgpuBrowserJs.GetQueue((int)device);

        // ---- Surface -------------------------------------------------------------------

        internal static IntPtr wgpuInstanceCreateSurface(IntPtr instance, WGPUSurfaceDescriptor* descriptor)
            => throw new NotSupportedException(
                "Browser surfaces are created from canvas handles via BrowserInterop.CreateSurface.");

        internal static void wgpuSurfaceConfigure(IntPtr surface, WGPUSurfaceConfiguration* config)
        {
            (string baseFormat, string viewFormat) = config->format switch
            {
                WGPUTextureFormat.RGBA8UnormSrgb => ("rgba8unorm", "rgba8unorm-srgb"),
                WGPUTextureFormat.BGRA8UnormSrgb => ("bgra8unorm", "bgra8unorm-srgb"),
                WGPUTextureFormat.RGBA8Unorm => ("rgba8unorm", ""),
                WGPUTextureFormat.BGRA8Unorm => ("bgra8unorm", ""),
                _ => throw new NotSupportedException($"Surface format {config->format} not supported on canvas."),
            };
            string alphaMode = config->alphaMode == WGPUCompositeAlphaMode.Premultiplied ? "premultiplied" : "opaque";
            WgpuBrowserJs.ConfigureSurface(
                (int)surface, (int)config->device, baseFormat, viewFormat, alphaMode,
                (int)config->width, (int)config->height);
        }

        internal static WGPUStatus wgpuSurfaceGetCapabilities(IntPtr surface, IntPtr adapter, WGPUSurfaceCapabilities* capabilities)
        {
            // Canvas contexts accept rgba8unorm/bgra8unorm bases; sRGB variants are
            // exposed here as directly-configurable (mapped to viewFormats in Configure).
            var formats = (WGPUTextureFormat*)Marshal.AllocHGlobal(sizeof(WGPUTextureFormat) * 4);
            formats[0] = WGPUTextureFormat.RGBA8UnormSrgb;
            formats[1] = WGPUTextureFormat.RGBA8Unorm;
            formats[2] = WGPUTextureFormat.BGRA8UnormSrgb;
            formats[3] = WGPUTextureFormat.BGRA8Unorm;

            var presentModes = (WGPUPresentMode*)Marshal.AllocHGlobal(sizeof(WGPUPresentMode));
            presentModes[0] = WGPUPresentMode.Fifo;

            var alphaModes = (WGPUCompositeAlphaMode*)Marshal.AllocHGlobal(sizeof(WGPUCompositeAlphaMode) * 2);
            alphaModes[0] = WGPUCompositeAlphaMode.Opaque;
            alphaModes[1] = WGPUCompositeAlphaMode.Premultiplied;

            capabilities->usages = WGPUTextureUsage.RenderAttachment | WGPUTextureUsage.CopySrc;
            capabilities->formatCount = 4;
            capabilities->formats = formats;
            capabilities->presentModeCount = 1;
            capabilities->presentModes = presentModes;
            capabilities->alphaModeCount = 2;
            capabilities->alphaModes = alphaModes;
            return WGPUStatus.Success;
        }

        internal static void wgpuSurfaceCapabilitiesFreeMembers(WGPUSurfaceCapabilities capabilities)
        {
            if (capabilities.formats != null) Marshal.FreeHGlobal((IntPtr)capabilities.formats);
            if (capabilities.presentModes != null) Marshal.FreeHGlobal((IntPtr)capabilities.presentModes);
            if (capabilities.alphaModes != null) Marshal.FreeHGlobal((IntPtr)capabilities.alphaModes);
        }

        internal static void wgpuSurfaceGetCurrentTexture(IntPtr surface, WGPUSurfaceTexture* surfaceTexture)
        {
            int tex = WgpuBrowserJs.SurfaceGetCurrentTexture((int)surface);
            surfaceTexture->texture = (IntPtr)tex;
            surfaceTexture->status = tex != 0
                ? WGPUSurfaceGetCurrentTextureStatus.SuccessOptimal
                : WGPUSurfaceGetCurrentTextureStatus.Error;
        }

        internal static WGPUStatus wgpuSurfacePresent(IntPtr surface)
        {
            // The browser presents the canvas's current texture automatically when
            // the current JS task returns to the event loop; a surface drawn from a worker
            // (the multithreaded runtime) is copied onto it here (wgpu-interop.js surfacePresent).
            FlushPassCommands();
            WgpuBrowserJs.SurfacePresent((int)surface);
            return WGPUStatus.Success;
        }

        // ---- Releases (Wgpu.Lifecycle.cs equivalents) ---------------------------------

        // A release goes in the record too, after the commands that name the object: in order, and no
        // round trip of its own (a frame frees its bind groups and buffers by the dozen). Ids are never
        // reused, so nothing made after it can be mistaken for it. Only on a thread that records passes,
        // which flushes every frame: elsewhere (a finalizer) it goes now, as it did.
        private static void Release(int id)
        {
            if (t_passCommands != null) Record(OpRelease, id, count: 1);
            else WgpuBrowserJs.Release(id);
        }

        internal static void wgpuTextureRelease(IntPtr texture) => Release((int)texture);
        internal static void wgpuTextureViewRelease(IntPtr textureView) => Release((int)textureView);
        internal static void wgpuBufferRelease(IntPtr buffer)
        {
            // a buffer let go of while mapped: its ranges go with it
            MapRecord? rec;
            lock (s_maps) { s_maps.TryGetValue(buffer, out rec); s_maps.Remove(buffer); }
            if (rec != null) foreach (var r in rec.Ranges) NativeMemory.Free((void*)r.Ptr);
            Release((int)buffer);
        }
        internal static void wgpuBindGroupRelease(IntPtr bindGroup) => Release((int)bindGroup);
        // The 3D path releases layouts as well as bind groups; without this the browser flavor does
        // not compile at all, so the whole head silently kept running last-known-good binaries.
        internal static void wgpuBindGroupLayoutRelease(IntPtr bindGroupLayout) => Release((int)bindGroupLayout);
        internal static void wgpuPipelineLayoutRelease(IntPtr pipelineLayout) => Release((int)pipelineLayout);
        internal static void wgpuSamplerRelease(IntPtr sampler) => Release((int)sampler);
        internal static void wgpuCommandEncoderRelease(IntPtr commandEncoder) => Release((int)commandEncoder);
        internal static void wgpuCommandBufferRelease(IntPtr commandBuffer) => Release((int)commandBuffer);
        internal static void wgpuRenderPassEncoderRelease(IntPtr renderPassEncoder) => Release((int)renderPassEncoder);
        internal static void wgpuRenderPipelineRelease(IntPtr renderPipeline) => Release((int)renderPipeline);
        internal static void ComputeRelease(IntPtr computeObject) => Release((int)computeObject);
        internal static void wgpuShaderModuleRelease(IntPtr shaderModule) => Release((int)shaderModule);
        internal static void wgpuQueueRelease(IntPtr queue) => Release((int)queue);
        internal static void wgpuDeviceRelease(IntPtr device) { /* device outlives contexts on the browser */ }
        internal static void wgpuAdapterRelease(IntPtr adapter) { }
        internal static void wgpuInstanceRelease(IntPtr instance) { }
        internal static void wgpuSurfaceRelease(IntPtr surface) => Release((int)surface);
    }
}
