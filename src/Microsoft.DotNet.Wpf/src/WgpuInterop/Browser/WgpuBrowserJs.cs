// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// [JSImport] bindings into Browser/wgpu-interop.js — the JS half of the browser
// WebGPU backend. Only compiled by Browser/WgpuInterop.Browser.csproj (browser
// flavor of Microsoft.Wpf.Interop.WebGpu). See Wgpu.Browser.cs for how these
// implement the wgpu-native C-ABI surface.
//

using System;
using System.Runtime.InteropServices.JavaScript;
using System.Threading.Tasks;

namespace Microsoft.Wpf.Interop.WebGpu.Browser
{
    internal static partial class WgpuBrowserJs
    {
        internal const string ModuleName = "wgpuInterop";

        /// <summary>
        /// Loads the JS module. Must complete before any Wgpu.* call on the browser;
        /// WgpuBrowser.InitializeAsync does this plus adapter/device acquisition.
        /// </summary>
        internal static Task ImportAsync(string moduleUrl)
            => JSHost.ImportAsync(ModuleName, moduleUrl);

        [JSImport("isSupported", ModuleName)]
        internal static partial bool IsSupported();

        [JSImport("requestAdapter", ModuleName)]
        internal static partial Task<int> RequestAdapter(string powerPreference);

        [JSImport("getAdapterInfoJson", ModuleName)]
        internal static partial string GetAdapterInfoJson(int adapter);

        [JSImport("requestDevice", ModuleName)]
        internal static partial Task<int> RequestDevice(int adapter);

        [JSImport("getQueue", ModuleName)]
        internal static partial int GetQueue(int device);

        [JSImport("getPreferredFormat", ModuleName)]
        internal static partial string GetPreferredFormat();

        [JSImport("createSurface", ModuleName)]
        internal static partial int CreateSurface(int canvasHandle);

        [JSImport("configureSurface", ModuleName)]
        internal static partial void ConfigureSurface(int surface, int device, string format, string viewFormat, string alphaMode, int width, int height);

        [JSImport("surfaceGetCurrentTexture", ModuleName)]
        internal static partial int SurfaceGetCurrentTexture(int surface);

        [JSImport("surfacePresent", ModuleName)]
        internal static partial void SurfacePresent(int surface);

        [JSImport("deviceHasFeature", ModuleName)]
        internal static partial bool DeviceHasFeature(int device, int feature);

        [JSImport("createTexture", ModuleName)]
        internal static partial int CreateTexture(int device, string descJson);

        [JSImport("createTextureView", ModuleName)]
        internal static partial int CreateTextureView(int texture, string descJson);

        [JSImport("createBuffer", ModuleName)]
        internal static partial int CreateBuffer(int device, double size, double usage, bool mappedAtCreation);

        [JSImport("writeBuffer", ModuleName)]
        internal static partial void WriteBuffer(int queue, int buffer, double bufferOffset, [JSMarshalAs<JSType.MemoryView>] Span<byte> data);

        [JSImport("writeTexture", ModuleName)]
        internal static partial void WriteTexture(int queue, string descJson, [JSMarshalAs<JSType.MemoryView>] Span<byte> data);

        [JSImport("createShaderModule", ModuleName)]
        internal static partial int CreateShaderModule(int device, string code);

        [JSImport("createRenderPipeline", ModuleName)]
        internal static partial int CreateRenderPipeline(int device, string descJson);

        [JSImport("createComputePipeline", ModuleName)]
        internal static partial int CreateComputePipeline(int device, string descJson);

        [JSImport("createBindGroupLayout", ModuleName)]
        internal static partial int CreateBindGroupLayout(int device, string entriesJson);

        [JSImport("createPipelineLayout", ModuleName)]
        internal static partial int CreatePipelineLayout(int device, string layoutsJson);

        [JSImport("getBindGroupLayout", ModuleName)]
        internal static partial int GetBindGroupLayout(int pipeline, int groupIndex);

        [JSImport("createBindGroup", ModuleName)]
        internal static partial int CreateBindGroup(int device, int layout, string entriesJson);

        [JSImport("createSampler", ModuleName)]
        internal static partial int CreateSampler(int device, string descJson);

        [JSImport("createCommandEncoder", ModuleName)]
        internal static partial int CreateCommandEncoder(int device);

        [JSImport("beginRenderPass", ModuleName)]
        internal static partial int BeginRenderPass(int encoder, string descJson);

        [JSImport("beginComputePass", ModuleName)]
        internal static partial int BeginComputePass(int encoder);

        /// <summary>Recorded encoder and pass commands, replayed in order (Wgpu.Browser.cs Op).</summary>
        [JSImport("runPassCommands", ModuleName)]
        internal static partial void RunPassCommands([JSMarshalAs<JSType.MemoryView>] Span<double> commands);

        [JSImport("finishEncoder", ModuleName)]
        internal static partial int FinishEncoder(int encoder);

        [JSImport("submit", ModuleName)]
        internal static partial void Submit(int queue, [JSMarshalAs<JSType.MemoryView>] Span<int> commandBuffers);

        [JSImport("outstandingWork", ModuleName)]
        internal static partial int OutstandingWork();

        // ---- buffer maps (Wgpu.Browser.cs: a mapped range is a copy in wasm memory) ----

        [JSImport("mapAsync", ModuleName)]
        internal static partial void MapAsync(int buffer, int mode, double offset, double size);

        [JSImport("mapState", ModuleName)]
        internal static partial int MapState(int buffer);

        [JSImport("mappedSize", ModuleName)]
        internal static partial double MappedSize(int buffer);

        [JSImport("readMapped", ModuleName)]
        internal static partial void ReadMapped(int buffer, double offset, [JSMarshalAs<JSType.MemoryView>] Span<byte> destination);

        [JSImport("writeMapped", ModuleName)]
        internal static partial void WriteMapped(int buffer, double offset, [JSMarshalAs<JSType.MemoryView>] Span<byte> source);

        [JSImport("unmap", ModuleName)]
        internal static partial void Unmap(int buffer);

        // ---- error scopes ----

        [JSImport("pushErrorScope", ModuleName)]
        internal static partial void PushErrorScope(int device, int filter);

        [JSImport("popErrorScope", ModuleName)]
        internal static partial int PopErrorScope(int device);

        [JSImport("errorScopeState", ModuleName)]
        internal static partial int ErrorScopeState(int scope);

        [JSImport("takeErrorScopeMessage", ModuleName)]
        internal static partial string TakeErrorScopeMessage(int scope);

        // Task<byte[]> is not supported by the JSImport generator: the async half
        // parks the bytes JS-side and returns a handle; TakeBytes fetches them.
        [JSImport("readbackTexture", ModuleName)]
        internal static partial Task<int> ReadbackTexture(int device, int texture, int width, int height);

        [JSImport("bytesLength", ModuleName)]
        internal static partial int BytesLength(int id);

        [JSImport("copyBytes", ModuleName)]
        internal static partial void CopyBytes(int id, [JSMarshalAs<JSType.MemoryView>] Span<byte> destination);

        /// <summary>Takes the bytes readbackTexture parked. Copied straight into a managed array
        /// through a memory view: returning byte[] as JSType.Array&lt;Number&gt; marshals a whole
        /// frame element by element, and those arrays were never collected (a WGPU_DAMAGE_VERIFY run
        /// taking two per frame reached 1.7 GB of heap and died with "memory access out of bounds").</summary>
        internal static byte[] TakeBytes(int id)
        {
            var bytes = new byte[BytesLength(id)];
            CopyBytes(id, bytes);
            return bytes;
        }

        // WGPU_DAMAGE_VERIFY: compare two textures in JS, return a text summary (see compareTextures).
        [JSImport("compareTextures", ModuleName)]
        internal static partial Task<string> CompareTextures(int device, int textureA, int textureB, int width, int height, int maxSamples);

        // Async GPU hit-test readback: the packed visual id at a device point.
        [JSImport("readbackTexel", ModuleName)]
        internal static partial Task<int> ReadbackTexel(int device, int texture, int x, int y);

        [JSImport("release", ModuleName)]
        internal static partial void Release(int id);

        [JSImport("handleCount", ModuleName)]
        internal static partial int HandleCount();
    }
}
