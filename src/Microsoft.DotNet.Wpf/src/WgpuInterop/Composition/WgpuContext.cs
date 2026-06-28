// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// Owns a WebGPU instance/adapter/device/queue and provides the small set of
// buffer + readback helpers the renderer needs. This is the cross-platform
// analog of milcore's CD3DDeviceManager: device acquisition and resource
// creation, just over wgpu-native instead of Direct3D.
//

using System;
using System.Runtime.InteropServices;
using System.Threading;
using static Microsoft.Wpf.Interop.WebGpu.Wgpu;

namespace Microsoft.Wpf.Interop.WebGpu.Composition
{
    internal sealed unsafe class WgpuContext : IDisposable
    {
        public IntPtr Instance { get; private set; }
        public IntPtr Adapter { get; private set; }
        public string? AdapterDescription { get; private set; }

        /// <summary>Optional sink for wgpu-native's own log messages (backend selection diagnostics).</summary>
        public static Action<string>? LogSink;
        private static WGPULogCallback? s_logCallback;   // kept alive against GC
        public IntPtr Device { get; private set; }
        public IntPtr Queue { get; private set; }

        // Roots for delegates handed to native as function pointers, so the GC
        // does not collect them while wgpu may still invoke them.
        private readonly WGPURequestAdapterCallback _adapterCb;
        private readonly WGPURequestDeviceCallback _deviceCb;

        private WgpuContext(WGPURequestAdapterCallback adapterCb, WGPURequestDeviceCallback deviceCb)
        {
            _adapterCb = adapterCb;
            _deviceCb = deviceCb;
        }

        public static WgpuContext Create()
        {
            if (LogSink != null)
            {
                s_logCallback = (level, msg, ud) =>
                {
                    string m = msg.data == null ? "" : System.Text.Encoding.UTF8.GetString(msg.data, (int)msg.length);
                    LogSink?.Invoke($"[wgpu {level}] {m}");
                };
                wgpuSetLogCallback(Marshal.GetFunctionPointerForDelegate(s_logCallback), IntPtr.Zero);
                // Warn by default (errors/warnings); set WPF_WEBGPU_WGPU_LOG=debug for backend-selection traces.
                wgpuSetLogLevel(Environment.GetEnvironmentVariable("WPF_WEBGPU_WGPU_LOG") == "debug" ? WGPULogLevel.Debug : WGPULogLevel.Warn);
            }

            IntPtr instance = wgpuCreateInstance(null);
            if (instance == IntPtr.Zero)
                throw new InvalidOperationException("wgpuCreateInstance failed.");

            IntPtr adapterResult = IntPtr.Zero;
            bool adapterDone = false;
            WGPURequestAdapterCallback adapterCb = (status, adapter, message, u1, u2) =>
            {
                if (status == WGPURequestAdapterStatus.Success) adapterResult = adapter;
                adapterDone = true;
            };

            IntPtr deviceResult = IntPtr.Zero;
            bool deviceDone = false;
            WGPURequestDeviceCallback deviceCb = (status, device, message, u1, u2) =>
            {
                if (status == WGPURequestDeviceStatus.Success) deviceResult = device;
                deviceDone = true;
            };

            var ctx = new WgpuContext(adapterCb, deviceCb) { Instance = instance };

            var adapterInfo = new WGPURequestAdapterCallbackInfo
            {
                mode = WGPUCallbackMode.AllowProcessEvents,
                callback = Marshal.GetFunctionPointerForDelegate(adapterCb),
            };
            var options = new WGPURequestAdapterOptions { powerPreference = WGPUPowerPreference.HighPerformance };
            wgpuInstanceRequestAdapter(instance, &options, adapterInfo);
            for (int i = 0; i < 1000 && !adapterDone; i++) { wgpuInstanceProcessEvents(instance); Thread.Sleep(1); }
            if (adapterResult == IntPtr.Zero)
                throw new InvalidOperationException("Could not acquire a WebGPU adapter.");
            ctx.Adapter = adapterResult;

            // Report which backend/adapter wgpu selected (wgpu has no D3D11 backend, so on a box
            // with only D3D11 it may fall back to a CPU/software adapter -> very slow).
            var info = new WGPUAdapterInfo();
            if (wgpuAdapterGetInfo(adapterResult, &info) == WGPUStatus.Success)
            {
                static string S(WGPUStringView v) => v.data == null ? "" : System.Text.Encoding.UTF8.GetString(v.data, (int)v.length);
                ctx.AdapterDescription = $"backend={info.backendType} type={info.adapterType} device='{S(info.device)}' desc='{S(info.description)}'";
            }

            var deviceInfo = new WGPURequestDeviceCallbackInfo
            {
                mode = WGPUCallbackMode.AllowSpontaneous,
                callback = Marshal.GetFunctionPointerForDelegate(deviceCb),
            };
            wgpuAdapterRequestDevice(adapterResult, IntPtr.Zero, deviceInfo);
            for (int i = 0; i < 1000 && !deviceDone; i++) Thread.Sleep(1);
            if (deviceResult == IntPtr.Zero)
                throw new InvalidOperationException("Could not acquire a WebGPU device.");
            ctx.Device = deviceResult;
            ctx.Queue = wgpuDeviceGetQueue(deviceResult);

            return ctx;
        }

        public IntPtr CreateBuffer(ulong size, WGPUBufferUsage usage)
        {
            var desc = new WGPUBufferDescriptor { usage = usage, size = size };
            return wgpuDeviceCreateBuffer(Device, &desc);
        }

        public void WriteBuffer(IntPtr buffer, ReadOnlySpan<byte> data)
        {
            fixed (byte* p = data)
            {
                wgpuQueueWriteBuffer(Queue, buffer, 0, p, (nuint)data.Length);
            }
        }

        /// <summary>Maps a MapRead buffer (driving the device queue) and returns its bytes.</summary>
        public byte[] MapRead(IntPtr buffer, ulong size)
        {
            bool done = false, ok = false;
            WGPUBufferMapCallback cb = (status, message, u1, u2) =>
            {
                ok = status == WGPUMapAsyncStatus.Success;
                done = true;
            };
            var info = new WGPUBufferMapCallbackInfo
            {
                mode = WGPUCallbackMode.AllowProcessEvents,
                callback = Marshal.GetFunctionPointerForDelegate(cb),
            };
            wgpuBufferMapAsync(buffer, WGPUMapMode.Read, 0, (nuint)size, info);
            for (int i = 0; i < 2000 && !done; i++) wgpuDevicePoll(Device, WGPU_TRUE, null);
            GC.KeepAlive(cb);

            if (!ok) throw new InvalidOperationException("Buffer map failed.");

            void* mapped = wgpuBufferGetConstMappedRange(buffer, 0, (nuint)size);
            if (mapped == null) throw new InvalidOperationException("GetConstMappedRange returned null.");

            var bytes = new byte[size];
            Marshal.Copy((IntPtr)mapped, bytes, 0, (int)size);
            wgpuBufferUnmap(buffer);
            return bytes;
        }

        public void Dispose()
        {
            if (Queue != IntPtr.Zero) wgpuQueueRelease(Queue);
            if (Device != IntPtr.Zero) wgpuDeviceRelease(Device);
            if (Adapter != IntPtr.Zero) wgpuAdapterRelease(Adapter);
            if (Instance != IntPtr.Zero) wgpuInstanceRelease(Instance);
            Queue = Device = Adapter = Instance = IntPtr.Zero;
            GC.KeepAlive(_adapterCb);
            GC.KeepAlive(_deviceCb);
        }
    }
}
