// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// Browser/WebAssembly implementation of the wgpu-native C-ABI surface declared
// across Wgpu*.cs. Compiled ONLY by Browser/WgpuInterop.Browser.csproj in place
// of Wgpu.Native.cs (DllImport) + Wgpu.Lifecycle.cs (release externs): identical
// signatures, managed bodies. Descriptor structs are decoded here in C# (they
// are already C# structs — no JS-side memory parsing) and forwarded to
// Browser/wgpu-interop.js as primitives / JSON, where a handle table maps the
// integer ids we surface as IntPtr onto live WebGPU objects.
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

        // ---- Enum -> WebGPU JS string maps -------------------------------------

        private static string FormatName(WGPUTextureFormat f) => f switch
        {
            WGPUTextureFormat.R8Unorm => "r8unorm",
            WGPUTextureFormat.RGBA8Unorm => "rgba8unorm",
            WGPUTextureFormat.RGBA8UnormSrgb => "rgba8unorm-srgb",
            WGPUTextureFormat.BGRA8Unorm => "bgra8unorm",
            WGPUTextureFormat.BGRA8UnormSrgb => "bgra8unorm-srgb",
            WGPUTextureFormat.Depth24Plus => "depth24plus",
            _ => throw new NotSupportedException($"Texture format {f} not mapped for the browser backend."),
        };

        private static string LoadOpName(WGPULoadOp op) => op switch
        {
            WGPULoadOp.Load => "load",
            WGPULoadOp.Clear => "clear",
            _ => "",
        };

        private static string StoreOpName(WGPUStoreOp op) => op switch
        {
            WGPUStoreOp.Store => "store",
            WGPUStoreOp.Discard => "discard",
            _ => "",
        };

        private static string TopologyName(WGPUPrimitiveTopology t) => t switch
        {
            WGPUPrimitiveTopology.PointList => "point-list",
            WGPUPrimitiveTopology.LineList => "line-list",
            WGPUPrimitiveTopology.LineStrip => "line-strip",
            WGPUPrimitiveTopology.TriangleStrip => "triangle-strip",
            _ => "triangle-list",
        };

        private static string IndexFormatName(WGPUIndexFormat f)
            => f == WGPUIndexFormat.Uint16 ? "uint16" : "uint32";

        private static string VertexFormatName(WGPUVertexFormat f) => f switch
        {
            WGPUVertexFormat.Unorm8x4 => "unorm8x4",
            WGPUVertexFormat.Float32x2 => "float32x2",
            WGPUVertexFormat.Float32x3 => "float32x3",
            WGPUVertexFormat.Float32x4 => "float32x4",
            _ => throw new NotSupportedException($"Vertex format {f} not mapped."),
        };

        private static string BlendOpName(WGPUBlendOperation op) => op switch
        {
            WGPUBlendOperation.Subtract => "subtract",
            WGPUBlendOperation.ReverseSubtract => "reverse-subtract",
            WGPUBlendOperation.Min => "min",
            WGPUBlendOperation.Max => "max",
            _ => "add",
        };

        private static string BlendFactorName(WGPUBlendFactor f) => f switch
        {
            WGPUBlendFactor.Zero => "zero",
            WGPUBlendFactor.One => "one",
            WGPUBlendFactor.Src => "src",
            WGPUBlendFactor.OneMinusSrc => "one-minus-src",
            WGPUBlendFactor.SrcAlpha => "src-alpha",
            WGPUBlendFactor.OneMinusSrcAlpha => "one-minus-src-alpha",
            WGPUBlendFactor.Dst => "dst",
            WGPUBlendFactor.OneMinusDst => "one-minus-dst",
            WGPUBlendFactor.DstAlpha => "dst-alpha",
            WGPUBlendFactor.OneMinusDstAlpha => "one-minus-dst-alpha",
            WGPUBlendFactor.SrcAlphaSaturated => "src-alpha-saturated",
            _ => "one",
        };

        private static string AddressModeName(WGPUAddressMode m) => m switch
        {
            WGPUAddressMode.Repeat => "repeat",
            WGPUAddressMode.MirrorRepeat => "mirror-repeat",
            _ => "clamp-to-edge",
        };

        private static string FilterName(WGPUFilterMode f)
            => f == WGPUFilterMode.Linear ? "linear" : "nearest";

        private static string MipFilterName(WGPUMipmapFilterMode f)
            => f == WGPUMipmapFilterMode.Linear ? "linear" : "nearest";

        private static string? CompareName(WGPUCompareFunction f) => f switch
        {
            WGPUCompareFunction.Less => "less",
            WGPUCompareFunction.LessEqual => "less-equal",
            WGPUCompareFunction.Always => "always",
            _ => null,
        };

        // ---- Diagnostics --------------------------------------------------------

        internal static void wgpuSetLogCallback(IntPtr callback, IntPtr userdata) { }
        internal static void wgpuSetLogLevel(WGPULogLevel level) { }

        // ---- Instance / adapter / device ---------------------------------------

        internal static IntPtr wgpuCreateInstance(WGPUInstanceDescriptor* descriptor)
        {
            WgpuBrowser.EnsureInitialized();
            return s_instanceSentinel;
        }

        internal static void wgpuInstanceProcessEvents(IntPtr instance) { }

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

        // ---- Resources ----------------------------------------------------------

        internal static IntPtr wgpuDeviceCreateTexture(IntPtr device, WGPUTextureDescriptor* descriptor)
        {
            return (IntPtr)WgpuBrowserJs.CreateTexture(
                (int)device,
                (int)descriptor->size.width, (int)descriptor->size.height,
                FormatName(descriptor->format),
                (double)(ulong)descriptor->usage,
                (int)descriptor->mipLevelCount, (int)descriptor->sampleCount);
        }

        internal static IntPtr wgpuDeviceCreateBuffer(IntPtr device, WGPUBufferDescriptor* descriptor)
        {
            return (IntPtr)WgpuBrowserJs.CreateBuffer(
                (int)device, descriptor->size, (double)(ulong)descriptor->usage,
                descriptor->mappedAtCreation != 0);
        }

        internal static IntPtr wgpuDeviceCreateCommandEncoder(IntPtr device, IntPtr descriptor)
            => (IntPtr)WgpuBrowserJs.CreateCommandEncoder((int)device);

        internal static IntPtr wgpuTextureCreateView(IntPtr texture, IntPtr descriptor)
            => (IntPtr)WgpuBrowserJs.CreateTextureView((int)texture);

        internal static IntPtr wgpuDeviceCreateSampler(IntPtr device, WGPUSamplerDescriptor* descriptor)
        {
            Utf8JsonWriter w = NewJsonWriter(out var buffer);
            w.WriteStartObject();
            w.WriteString("addressModeU", AddressModeName(descriptor->addressModeU));
            w.WriteString("addressModeV", AddressModeName(descriptor->addressModeV));
            w.WriteString("addressModeW", AddressModeName(descriptor->addressModeW));
            w.WriteString("magFilter", FilterName(descriptor->magFilter));
            w.WriteString("minFilter", FilterName(descriptor->minFilter));
            w.WriteString("mipmapFilter", MipFilterName(descriptor->mipmapFilter));
            w.WriteNumber("lodMinClamp", descriptor->lodMinClamp);
            w.WriteNumber("lodMaxClamp", descriptor->lodMaxClamp);
            w.WriteNumber("maxAnisotropy", descriptor->maxAnisotropy);
            string? cmp = CompareName(descriptor->compare);
            if (cmp != null) w.WriteString("compare", cmp);
            w.WriteEndObject();
            return (IntPtr)WgpuBrowserJs.CreateSampler((int)device, FinishJson(w, buffer));
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
                    w.WriteNumber("size", e->size);
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
            // The only chained source the engine uses is WGSL.
            var chain = (WGPUChainedStruct*)descriptor->nextInChain;
            if (chain == null || chain->sType != WGPUSType_ShaderSourceWGSL)
                throw new NotSupportedException("Browser backend expects a WGSL shader source chain.");
            var wgsl = (WGPUShaderSourceWGSL*)chain;
            return (IntPtr)WgpuBrowserJs.CreateShaderModule((int)device, S(wgsl->code));
        }

        internal static IntPtr wgpuDeviceCreateRenderPipeline(IntPtr device, WGPURenderPipelineDescriptor* descriptor)
        {
            Utf8JsonWriter w = NewJsonWriter(out var buffer);
            w.WriteStartObject();

            w.WriteStartObject("vertex");
            w.WriteNumber("module", (int)descriptor->vertex.module);
            w.WriteString("entryPoint", S(descriptor->vertex.entryPoint));
            w.WriteStartArray("buffers");
            for (nuint i = 0; i < descriptor->vertex.bufferCount; i++)
            {
                WGPUVertexBufferLayout* bl = descriptor->vertex.buffers + i;
                w.WriteStartObject();
                w.WriteNumber("arrayStride", bl->arrayStride);
                w.WriteString("stepMode", bl->stepMode == WGPUVertexStepMode.Instance ? "instance" : "vertex");
                w.WriteStartArray("attributes");
                for (nuint j = 0; j < bl->attributeCount; j++)
                {
                    WGPUVertexAttribute* at = bl->attributes + j;
                    w.WriteStartObject();
                    w.WriteString("format", VertexFormatName(at->format));
                    w.WriteNumber("offset", at->offset);
                    w.WriteNumber("shaderLocation", at->shaderLocation);
                    w.WriteEndObject();
                }
                w.WriteEndArray();
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteEndObject();

            w.WriteStartObject("primitive");
            w.WriteString("topology", TopologyName(descriptor->primitive.topology));
            w.WriteString("frontFace", descriptor->primitive.frontFace == WGPUFrontFace.CW ? "cw" : "ccw");
            w.WriteString("cullMode", descriptor->primitive.cullMode switch
            {
                WGPUCullMode.Front => "front",
                WGPUCullMode.Back => "back",
                _ => "none",
            });
            w.WriteEndObject();

            w.WriteStartObject("multisample");
            w.WriteNumber("count", descriptor->multisample.count == 0 ? 1u : descriptor->multisample.count);
            w.WriteEndObject();

            if (descriptor->depthStencil != IntPtr.Zero)
            {
                var ds = (WGPUDepthStencilState*)descriptor->depthStencil;
                w.WriteStartObject("depthStencil");
                w.WriteString("format", FormatName(ds->format));
                w.WriteBoolean("depthWriteEnabled", ds->depthWriteEnabled == WGPUOptionalBool.True);
                w.WriteString("depthCompare", CompareName(ds->depthCompare) ?? "always");
                w.WriteEndObject();
            }

            if (descriptor->fragment != null)
            {
                w.WriteStartObject("fragment");
                w.WriteNumber("module", (int)descriptor->fragment->module);
                w.WriteString("entryPoint", S(descriptor->fragment->entryPoint));
                w.WriteStartArray("targets");
                for (nuint i = 0; i < descriptor->fragment->targetCount; i++)
                {
                    WGPUColorTargetState* t = descriptor->fragment->targets + i;
                    w.WriteStartObject();
                    w.WriteString("format", FormatName(t->format));
                    w.WriteNumber("writeMask", t->writeMask);
                    if (t->blend != null)
                    {
                        w.WriteStartObject("blend");
                        WriteBlend(w, "color", t->blend->color);
                        WriteBlend(w, "alpha", t->blend->alpha);
                        w.WriteEndObject();
                    }
                    w.WriteEndObject();
                }
                w.WriteEndArray();
                w.WriteEndObject();
            }

            w.WriteEndObject();
            return (IntPtr)WgpuBrowserJs.CreateRenderPipeline((int)device, FinishJson(w, buffer));

            static void WriteBlend(Utf8JsonWriter w, string name, WGPUBlendComponent c)
            {
                w.WriteStartObject(name);
                w.WriteString("operation", BlendOpName(c.operation));
                w.WriteString("srcFactor", BlendFactorName(c.srcFactor));
                w.WriteString("dstFactor", BlendFactorName(c.dstFactor));
                w.WriteEndObject();
            }
        }

        // ---- Uploads --------------------------------------------------------------

        internal static void wgpuQueueWriteBuffer(IntPtr queue, IntPtr buffer, ulong bufferOffset, void* data, nuint size)
            => WgpuBrowserJs.WriteBuffer((int)queue, (int)buffer, bufferOffset, new Span<byte>(data, (int)size));

        internal static void wgpuQueueWriteTexture(
            IntPtr queue, WGPUTexelCopyTextureInfo* destination, void* data, nuint dataSize,
            WGPUTexelCopyBufferLayout* dataLayout, WGPUExtent3D* writeSize)
        {
            if (dataLayout->offset != 0)
                throw new NotSupportedException("writeTexture with a non-zero data offset is not used by the engine.");
            WgpuBrowserJs.WriteTexture(
                (int)queue, (int)destination->texture, (int)destination->mipLevel,
                (int)destination->origin.x, (int)destination->origin.y,
                new Span<byte>(data, (int)dataSize),
                (int)dataLayout->bytesPerRow, (int)dataLayout->rowsPerImage,
                (int)writeSize->width, (int)writeSize->height);
        }

        // ---- Command encoding -------------------------------------------------------

        internal static IntPtr wgpuCommandEncoderBeginRenderPass(IntPtr commandEncoder, WGPURenderPassDescriptor* descriptor)
        {
            if (descriptor->colorAttachmentCount != 1)
                throw new NotSupportedException("Browser backend supports exactly one color attachment.");
            WGPURenderPassColorAttachment* c = descriptor->colorAttachments;

            int depthView = 0;
            string depthLoad = "", depthStore = "";
            double depthClear = 1.0;
            if (descriptor->depthStencilAttachment != IntPtr.Zero)
            {
                var dsa = (WGPURenderPassDepthStencilAttachment*)descriptor->depthStencilAttachment;
                depthView = (int)dsa->view;
                depthLoad = LoadOpName(dsa->depthLoadOp);
                depthStore = StoreOpName(dsa->depthStoreOp);
                depthClear = dsa->depthClearValue;
            }

            return (IntPtr)WgpuBrowserJs.BeginRenderPass(
                (int)commandEncoder, (int)c->view, (int)c->resolveTarget,
                LoadOpName(c->loadOp), StoreOpName(c->storeOp),
                c->clearValue.r, c->clearValue.g, c->clearValue.b, c->clearValue.a,
                depthView, depthLoad, depthStore, depthClear);
        }

        internal static void wgpuRenderPassEncoderEnd(IntPtr renderPassEncoder)
            => WgpuBrowserJs.EndPass((int)renderPassEncoder);

        internal static void wgpuRenderPassEncoderSetPipeline(IntPtr renderPassEncoder, IntPtr pipeline)
            => WgpuBrowserJs.SetPipeline((int)renderPassEncoder, (int)pipeline);

        internal static void wgpuRenderPassEncoderSetBindGroup(
            IntPtr renderPassEncoder, uint groupIndex, IntPtr group, nuint dynamicOffsetCount, uint* dynamicOffsets)
        {
            if (dynamicOffsetCount != 0)
                throw new NotSupportedException("Dynamic bind-group offsets are not used by the engine.");
            WgpuBrowserJs.SetBindGroup((int)renderPassEncoder, (int)groupIndex, (int)group);
        }

        internal static void wgpuRenderPassEncoderSetVertexBuffer(IntPtr renderPassEncoder, uint slot, IntPtr buffer, ulong offset, ulong size)
            => WgpuBrowserJs.SetVertexBuffer((int)renderPassEncoder, (int)slot, (int)buffer, offset,
                size == ulong.MaxValue ? -1d : size);

        internal static void wgpuRenderPassEncoderSetIndexBuffer(IntPtr renderPassEncoder, IntPtr buffer, WGPUIndexFormat format, ulong offset, ulong size)
            => WgpuBrowserJs.SetIndexBuffer((int)renderPassEncoder, (int)buffer, IndexFormatName(format), offset,
                size == ulong.MaxValue ? -1d : size);

        internal static void wgpuRenderPassEncoderDrawIndexed(IntPtr renderPassEncoder, uint indexCount, uint instanceCount, uint firstIndex, int baseVertex, uint firstInstance)
            => WgpuBrowserJs.DrawIndexed((int)renderPassEncoder, (int)indexCount, (int)instanceCount, (int)firstIndex, baseVertex, (int)firstInstance);

        internal static void wgpuRenderPassEncoderSetScissorRect(IntPtr renderPassEncoder, uint x, uint y, uint width, uint height)
            => WgpuBrowserJs.SetScissorRect((int)renderPassEncoder, (int)x, (int)y, (int)width, (int)height);

        internal static void wgpuCommandEncoderCopyTextureToBuffer(
            IntPtr commandEncoder, WGPUTexelCopyTextureInfo* source, WGPUTexelCopyBufferInfo* destination, WGPUExtent3D* copySize)
            => throw new NotSupportedException("GPU->CPU readback is not wired on the browser backend yet.");

        // The browser CreateTexture path uploads via queue.writeTexture (no command-buffer leak there),
        // so FlushPendingTexUploads is always a no-op on the browser and this is never invoked.
        internal static void wgpuCommandEncoderCopyBufferToTexture(
            IntPtr commandEncoder, WGPUTexelCopyBufferInfo* source, WGPUTexelCopyTextureInfo* destination, WGPUExtent3D* copySize)
            => throw new NotSupportedException("Buffer->texture copy is not wired on the browser backend (uploads use queue.writeTexture).");



        internal static IntPtr wgpuCommandEncoderFinish(IntPtr commandEncoder, IntPtr descriptor)
            => (IntPtr)WgpuBrowserJs.FinishEncoder((int)commandEncoder);

        internal static void wgpuQueueSubmit(IntPtr queue, nuint commandCount, IntPtr* commands)
        {
            if (commandCount != 1)
                throw new NotSupportedException("Browser backend expects single-command-buffer submits.");
            WgpuBrowserJs.Submit((int)queue, (int)commands[0]);
        }


        // ---- Buffer readback (async-only in the browser; not wired yet) -------------

        internal static WGPUFuture wgpuBufferMapAsync(
            IntPtr buffer, WGPUMapMode mode, nuint offset, nuint size, WGPUBufferMapCallbackInfo callbackInfo)
            => throw new NotSupportedException("Blocking buffer map is impossible on the browser main thread.");

        internal static void* wgpuBufferGetConstMappedRange(IntPtr buffer, nuint offset, nuint size)
            => throw new NotSupportedException("Blocking buffer map is impossible on the browser main thread.");

        internal static void wgpuBufferUnmap(IntPtr buffer) { }

        internal static uint wgpuDevicePoll(IntPtr device, uint wait, ulong* submissionIndex) => 0;

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
            // the current JS task returns to the event loop.
            return WGPUStatus.Success;
        }

        // ---- Releases (Wgpu.Lifecycle.cs equivalents) ---------------------------------

        internal static void wgpuTextureRelease(IntPtr texture) => WgpuBrowserJs.Release((int)texture);
        internal static void wgpuTextureViewRelease(IntPtr textureView) => WgpuBrowserJs.Release((int)textureView);
        internal static void wgpuBufferRelease(IntPtr buffer) => WgpuBrowserJs.Release((int)buffer);
        internal static void wgpuBindGroupRelease(IntPtr bindGroup) => WgpuBrowserJs.Release((int)bindGroup);
        internal static void wgpuSamplerRelease(IntPtr sampler) => WgpuBrowserJs.Release((int)sampler);
        internal static void wgpuCommandEncoderRelease(IntPtr commandEncoder) => WgpuBrowserJs.Release((int)commandEncoder);
        internal static void wgpuCommandBufferRelease(IntPtr commandBuffer) => WgpuBrowserJs.Release((int)commandBuffer);
        internal static void wgpuRenderPassEncoderRelease(IntPtr renderPassEncoder) => WgpuBrowserJs.Release((int)renderPassEncoder);
        internal static void wgpuRenderPipelineRelease(IntPtr renderPipeline) => WgpuBrowserJs.Release((int)renderPipeline);
        internal static void wgpuShaderModuleRelease(IntPtr shaderModule) => WgpuBrowserJs.Release((int)shaderModule);
        internal static void wgpuQueueRelease(IntPtr queue) => WgpuBrowserJs.Release((int)queue);
        internal static void wgpuDeviceRelease(IntPtr device) { /* device outlives contexts on the browser */ }
        internal static void wgpuAdapterRelease(IntPtr adapter) { }
        internal static void wgpuInstanceRelease(IntPtr instance) { }
        internal static void wgpuSurfaceRelease(IntPtr surface) => WgpuBrowserJs.Release((int)surface);
    }
}
