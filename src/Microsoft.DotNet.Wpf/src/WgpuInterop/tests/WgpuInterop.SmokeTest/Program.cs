// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// Phase-0 headless smoke test for the cross-platform WebGPU seam.
//
// It exercises the whole device path with no window: create instance/adapter/
// device, make a small RGBA8 texture, clear it to a known colour in a render
// pass, copy the texture to a mappable buffer, map it and read back the top-left
// pixel. If the pixel matches the clear colour, the wgpu-native binding (Wgpu.cs)
// and the GPU device path are sound -- this is the foundation the wpfgpu
// composition engine renders on.
//

using System;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Wpf.Interop.WebGpu;
using static Microsoft.Wpf.Interop.WebGpu.Wgpu;

internal static unsafe class Program
{
    private const int Width = 4;
    private const int Height = 4;

    // 256-byte row alignment is required by CopyTextureToBuffer.
    private const int BytesPerRow = 256;
    private const ulong BufferSize = (ulong)BytesPerRow * Height;

    private static int Main()
    {
        // Clear colour. RGBA8Unorm is linear, so byte = round(v * 255).
        var clear = new WGPUColor { r = 0.25, g = 0.50, b = 0.75, a = 1.0 };
        byte[] expected = { 64, 128, 191, 255 };

        IntPtr instance = wgpuCreateInstance(null);
        if (instance == IntPtr.Zero) return Fail("wgpuCreateInstance returned null");
        Console.WriteLine($"instance = 0x{instance:x}");

        IntPtr adapter = RequestAdapter(instance);
        if (adapter == IntPtr.Zero) return Fail("could not acquire adapter");
        Console.WriteLine($"adapter  = 0x{adapter:x}");

        IntPtr device = RequestDevice(adapter);
        if (device == IntPtr.Zero) return Fail("could not acquire device");
        Console.WriteLine($"device   = 0x{device:x}");

        IntPtr queue = wgpuDeviceGetQueue(device);
        if (queue == IntPtr.Zero) return Fail("wgpuDeviceGetQueue returned null");

        // ---- texture ----
        var texDesc = new WGPUTextureDescriptor
        {
            usage = WGPUTextureUsage.RenderAttachment | WGPUTextureUsage.CopySrc,
            dimension = WGPUTextureDimension._2D,
            size = new WGPUExtent3D { width = Width, height = Height, depthOrArrayLayers = 1 },
            format = WGPUTextureFormat.RGBA8Unorm,
            mipLevelCount = 1,
            sampleCount = 1,
        };
        IntPtr texture = wgpuDeviceCreateTexture(device, &texDesc);
        IntPtr view = wgpuTextureCreateView(texture, IntPtr.Zero);

        // ---- readback buffer ----
        var bufDesc = new WGPUBufferDescriptor
        {
            usage = WGPUBufferUsage.CopyDst | WGPUBufferUsage.MapRead,
            size = BufferSize,
        };
        IntPtr buffer = wgpuDeviceCreateBuffer(device, &bufDesc);

        // ---- encode: clear pass + copy to buffer ----
        IntPtr encoder = wgpuDeviceCreateCommandEncoder(device, IntPtr.Zero);

        var colorAttachment = new WGPURenderPassColorAttachment
        {
            view = view,
            depthSlice = WGPU_DEPTH_SLICE_UNDEFINED,
            loadOp = WGPULoadOp.Clear,
            storeOp = WGPUStoreOp.Store,
            clearValue = clear,
        };
        var passDesc = new WGPURenderPassDescriptor
        {
            colorAttachmentCount = 1,
            colorAttachments = &colorAttachment,
        };
        IntPtr pass = wgpuCommandEncoderBeginRenderPass(encoder, &passDesc);
        wgpuRenderPassEncoderEnd(pass);

        var copySrc = new WGPUTexelCopyTextureInfo
        {
            texture = texture,
            aspect = WGPUTextureAspect.All,
        };
        var copyDst = new WGPUTexelCopyBufferInfo
        {
            layout = new WGPUTexelCopyBufferLayout { offset = 0, bytesPerRow = BytesPerRow, rowsPerImage = Height },
            buffer = buffer,
        };
        var copyExtent = new WGPUExtent3D { width = Width, height = Height, depthOrArrayLayers = 1 };
        wgpuCommandEncoderCopyTextureToBuffer(encoder, &copySrc, &copyDst, &copyExtent);

        IntPtr commandBuffer = wgpuCommandEncoderFinish(encoder, IntPtr.Zero);
        IntPtr* cmds = stackalloc IntPtr[1];
        cmds[0] = commandBuffer;
        wgpuQueueSubmit(queue, 1, cmds);

        // ---- map + readback ----
        byte[] pixel = MapAndReadFirstPixel(device, buffer);
        if (pixel.Length == 0) return Fail("buffer map failed");

        Console.WriteLine($"expected RGBA = [{string.Join(", ", expected)}]");
        Console.WriteLine($"readback RGBA = [{pixel[0]}, {pixel[1]}, {pixel[2]}, {pixel[3]}]");

        for (int i = 0; i < 4; i++)
        {
            if (Math.Abs(pixel[i] - expected[i]) > 1)
                return Fail($"channel {i}: got {pixel[i]}, expected ~{expected[i]}");
        }

        Console.WriteLine("SMOKE TEST PASSED: WebGPU device path renders and reads back correctly.");
        return 0;
    }

    private static IntPtr RequestAdapter(IntPtr instance)
    {
        IntPtr result = IntPtr.Zero;
        bool done = false;

        WGPURequestAdapterCallback cb = (status, adapter, message, u1, u2) =>
        {
            if (status == WGPURequestAdapterStatus.Success) result = adapter;
            else Console.Error.WriteLine($"requestAdapter status={status} msg={ReadString(message)}");
            done = true;
        };

        var info = new WGPURequestAdapterCallbackInfo
        {
            mode = WGPUCallbackMode.AllowProcessEvents,
            callback = Marshal.GetFunctionPointerForDelegate(cb),
        };
        var options = new WGPURequestAdapterOptions
        {
            powerPreference = WGPUPowerPreference.HighPerformance,
        };

        wgpuInstanceRequestAdapter(instance, &options, info);
        PumpUntil(ref done, () => wgpuInstanceProcessEvents(instance));
        GC.KeepAlive(cb);
        return result;
    }

    private static IntPtr RequestDevice(IntPtr adapter)
    {
        // wgpu-native fires the device callback spontaneously during the request.
        IntPtr result = IntPtr.Zero;
        bool done = false;

        WGPURequestDeviceCallback cb = (status, device, message, u1, u2) =>
        {
            if (status == WGPURequestDeviceStatus.Success) result = device;
            else Console.Error.WriteLine($"requestDevice status={status} msg={ReadString(message)}");
            done = true;
        };

        var info = new WGPURequestDeviceCallbackInfo
        {
            mode = WGPUCallbackMode.AllowSpontaneous,
            callback = Marshal.GetFunctionPointerForDelegate(cb),
        };

        wgpuAdapterRequestDevice(adapter, IntPtr.Zero, info);
        // The callback is spontaneous; give it a brief chance if not already done.
        for (int i = 0; i < 1000 && !done; i++) Thread.Sleep(1);
        GC.KeepAlive(cb);
        return result;
    }

    private static byte[] MapAndReadFirstPixel(IntPtr device, IntPtr buffer)
    {
        bool done = false;
        bool ok = false;

        WGPUBufferMapCallback cb = (status, message, u1, u2) =>
        {
            ok = status == WGPUMapAsyncStatus.Success;
            if (!ok) Console.Error.WriteLine($"mapAsync status={status} msg={ReadString(message)}");
            done = true;
        };

        var info = new WGPUBufferMapCallbackInfo
        {
            mode = WGPUCallbackMode.AllowProcessEvents,
            callback = Marshal.GetFunctionPointerForDelegate(cb),
        };

        wgpuBufferMapAsync(buffer, WGPUMapMode.Read, 0, (nuint)BufferSize, info);

        // Poll the device (wait=true) to drive submitted work + map completion.
        for (int i = 0; i < 1000 && !done; i++)
        {
            wgpuDevicePoll(device, WGPU_TRUE, null);
        }
        GC.KeepAlive(cb);

        if (!ok) return Array.Empty<byte>();

        void* mapped = wgpuBufferGetConstMappedRange(buffer, 0, (nuint)BufferSize);
        if (mapped == null) return Array.Empty<byte>();

        var pixel = new byte[4];
        Marshal.Copy((IntPtr)mapped, pixel, 0, 4);
        wgpuBufferUnmap(buffer);
        return pixel;
    }

    private static void PumpUntil(ref bool done, Action pump)
    {
        for (int i = 0; i < 1000 && !done; i++)
        {
            pump();
            Thread.Sleep(1);
        }
    }

    private static string ReadString(WGPUStringView v)
    {
        if (v.data == null || v.length == 0) return string.Empty;
        return Marshal.PtrToStringUTF8((IntPtr)v.data, (int)v.length) ?? string.Empty;
    }

    private static int Fail(string message)
    {
        Console.Error.WriteLine($"SMOKE TEST FAILED: {message}");
        return 1;
    }
}
