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

        [JSImport("createTexture", ModuleName)]
        internal static partial int CreateTexture(int device, int width, int height, string format, double usage, int mipLevelCount, int sampleCount);

        [JSImport("createTextureView", ModuleName)]
        internal static partial int CreateTextureView(int texture);

        [JSImport("createBuffer", ModuleName)]
        internal static partial int CreateBuffer(int device, double size, double usage, bool mappedAtCreation);

        [JSImport("writeBuffer", ModuleName)]
        internal static partial void WriteBuffer(int queue, int buffer, double bufferOffset, [JSMarshalAs<JSType.MemoryView>] Span<byte> data);

        [JSImport("writeTexture", ModuleName)]
        internal static partial void WriteTexture(int queue, int texture, int mipLevel, int originX, int originY, [JSMarshalAs<JSType.MemoryView>] Span<byte> data, int bytesPerRow, int rowsPerImage, int width, int height);

        [JSImport("createShaderModule", ModuleName)]
        internal static partial int CreateShaderModule(int device, string code);

        [JSImport("createRenderPipeline", ModuleName)]
        internal static partial int CreateRenderPipeline(int device, string descJson);

        [JSImport("getBindGroupLayout", ModuleName)]
        internal static partial int GetBindGroupLayout(int pipeline, int groupIndex);

        [JSImport("createBindGroup", ModuleName)]
        internal static partial int CreateBindGroup(int device, int layout, string entriesJson);

        [JSImport("createSampler", ModuleName)]
        internal static partial int CreateSampler(int device, string descJson);

        [JSImport("createCommandEncoder", ModuleName)]
        internal static partial int CreateCommandEncoder(int device);

        [JSImport("beginRenderPass", ModuleName)]
        internal static partial int BeginRenderPass(int encoder, int colorView, int resolveView, string loadOp, string storeOp,
            double r, double g, double b, double a, int depthView, string depthLoadOp, string depthStoreOp, double depthClearValue);

        [JSImport("setPipeline", ModuleName)]
        internal static partial void SetPipeline(int pass, int pipeline);

        [JSImport("setBindGroup", ModuleName)]
        internal static partial void SetBindGroup(int pass, int groupIndex, int group);

        [JSImport("setVertexBuffer", ModuleName)]
        internal static partial void SetVertexBuffer(int pass, int slot, int buffer, double offset, double size);

        [JSImport("setIndexBuffer", ModuleName)]
        internal static partial void SetIndexBuffer(int pass, int buffer, string format, double offset, double size);

        [JSImport("drawIndexed", ModuleName)]
        internal static partial void DrawIndexed(int pass, int indexCount, int instanceCount, int firstIndex, int baseVertex, int firstInstance);

        [JSImport("setScissorRect", ModuleName)]
        internal static partial void SetScissorRect(int pass, int x, int y, int width, int height);

        [JSImport("endPass", ModuleName)]
        internal static partial void EndPass(int pass);

        [JSImport("finishEncoder", ModuleName)]
        internal static partial int FinishEncoder(int encoder);

        [JSImport("submit", ModuleName)]
        internal static partial void Submit(int queue, int commandBuffer);

        // Task<byte[]> is not supported by the JSImport generator: the async half
        // parks the bytes JS-side and returns a handle; TakeBytes fetches them.
        [JSImport("readbackTexture", ModuleName)]
        internal static partial Task<int> ReadbackTexture(int device, int texture, int width, int height);

        [JSImport("takeBytes", ModuleName)]
        [return: JSMarshalAs<JSType.Array<JSType.Number>>]
        internal static partial byte[] TakeBytes(int id);

        // Async GPU hit-test readback: the packed visual id at a device point.
        [JSImport("readbackTexel", ModuleName)]
        internal static partial Task<int> ReadbackTexel(int device, int texture, int x, int y);

        [JSImport("release", ModuleName)]
        internal static partial void Release(int id);

        [JSImport("handleCount", ModuleName)]
        internal static partial int HandleCount();
    }
}
