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
        public IntPtr Device { get; private set; }
        public IntPtr Queue { get; private set; }

        //
        // Native -> managed callbacks are STATIC [UnmanagedCallersOnly] methods rather than lambdas.
        // A lambda that captures locals compiles to a closure instance, and calling one from native
        // needs a reverse (native-to-managed) wrapper generated at runtime -- impossible where there
        // is no JIT. On iOS that throws outright:
        //     ExecutionEngineException: Attempting to JIT compile method
        //     '(wrapper native-to-managed) WgpuContext/<>c__DisplayClass..:<Create>b__1'
        //     while running in aot-only mode
        // (the same constraint that made the browser flavour avoid native callbacks entirely).
        // Static function pointers are AOT-safe on every platform, so results come back through
        // static fields instead of captured locals. Create() is serialised by s_requestLock.
        //
        private static readonly object s_requestLock = new object();
        private static IntPtr s_adapterResult;
        private static bool s_adapterDone;
        private static IntPtr s_deviceResult;
        private static bool s_deviceDone;

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvCdecl) })]
        private static void OnAdapterReady(WGPURequestAdapterStatus status, IntPtr adapter, WGPUStringView message, IntPtr u1, IntPtr u2)
        {
            if (status == WGPURequestAdapterStatus.Success) s_adapterResult = adapter;
            s_adapterDone = true;
        }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvCdecl) })]
        private static void OnDeviceReady(WGPURequestDeviceStatus status, IntPtr device, WGPUStringView message, IntPtr u1, IntPtr u2)
        {
            if (status == WGPURequestDeviceStatus.Success) s_deviceResult = device;
            s_deviceDone = true;
        }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvCdecl) })]
        private static void OnWgpuLog(WGPULogLevel level, WGPUStringView msg, IntPtr userdata)
        {
            string m = msg.data == null ? "" : System.Text.Encoding.UTF8.GetString(msg.data, (int)msg.length);
            LogSink?.Invoke($"[wgpu {level}] {m}");
        }

        private WgpuContext()
        {
        }

        public static WgpuContext Create()
        {
#if WGPU_BROWSER
            // Browser: the adapter/device were pre-acquired by WgpuBrowser.InitializeAsync
            // (WebGPU is Promise-based; the blocking callback dance below cannot run on the
            // browser main thread, and Marshal.GetFunctionPointerForDelegate has no
            // interpreter thunk on mono-wasm). Same object shape, no native callbacks.
            {
                IntPtr instance = wgpuCreateInstance(null);
                var ctx = new WgpuContext { Instance = instance };
                ctx.Adapter = (IntPtr)WgpuBrowser.AdapterHandle;
                var info = new WGPUAdapterInfo();
                if (wgpuAdapterGetInfo(ctx.Adapter, &info) == WGPUStatus.Success)
                {
                    static string SVs(WGPUStringView v) => v.data == null ? "" : System.Text.Encoding.UTF8.GetString(v.data, (int)v.length);
                    ctx.AdapterDescription = $"backend={info.backendType} type={info.adapterType} device='{SVs(info.device)}' desc='{SVs(info.description)}'";
                }
                ctx.Device = (IntPtr)WgpuBrowser.DeviceHandle;
                ctx.Queue = wgpuDeviceGetQueue(ctx.Device);
                return ctx;
            }
#else
            if (LogSink != null)
            {
                wgpuSetLogCallback(
                    (IntPtr)(delegate* unmanaged[Cdecl]<WGPULogLevel, WGPUStringView, IntPtr, void>)&OnWgpuLog,
                    IntPtr.Zero);
                // Warn by default (errors/warnings); set WPF_WEBGPU_WGPU_LOG=debug for backend-selection traces.
                wgpuSetLogLevel(Environment.GetEnvironmentVariable("WPF_WEBGPU_WGPU_LOG") == "debug" ? WGPULogLevel.Debug : WGPULogLevel.Warn);
            }

            IntPtr instance = wgpuCreateInstance(null);
            if (instance == IntPtr.Zero)
                throw new InvalidOperationException("wgpuCreateInstance failed.");

            IntPtr adapterResult, deviceResult;
            var ctx = new WgpuContext { Instance = instance };

            lock (s_requestLock)
            {
            s_adapterResult = IntPtr.Zero;
            s_adapterDone = false;
            s_deviceResult = IntPtr.Zero;
            s_deviceDone = false;

            var adapterInfo = new WGPURequestAdapterCallbackInfo
            {
                mode = WGPUCallbackMode.AllowProcessEvents,
                callback = (IntPtr)(delegate* unmanaged[Cdecl]<WGPURequestAdapterStatus, IntPtr, WGPUStringView, IntPtr, IntPtr, void>)&OnAdapterReady,
            };
            var options = new WGPURequestAdapterOptions { powerPreference = WGPUPowerPreference.HighPerformance };
            wgpuInstanceRequestAdapter(instance, &options, adapterInfo);
            for (int i = 0; i < 1000 && !s_adapterDone; i++) { wgpuInstanceProcessEvents(instance); Thread.Sleep(1); }
            adapterResult = s_adapterResult;
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
                callback = (IntPtr)(delegate* unmanaged[Cdecl]<WGPURequestDeviceStatus, IntPtr, WGPUStringView, IntPtr, IntPtr, void>)&OnDeviceReady,
            };
            wgpuAdapterRequestDevice(adapterResult, IntPtr.Zero, deviceInfo);
            for (int i = 0; i < 1000 && !s_deviceDone; i++) Thread.Sleep(1);
            deviceResult = s_deviceResult;
            } // s_requestLock

            if (deviceResult == IntPtr.Zero)
                throw new InvalidOperationException("Could not acquire a WebGPU device.");
            ctx.Device = deviceResult;
            ctx.Queue = wgpuDeviceGetQueue(deviceResult);

            return ctx;
#endif
        }

        public IntPtr CreateBuffer(ulong size, WGPUBufferUsage usage)
        {
            var desc = new WGPUBufferDescriptor { usage = usage, size = size };
            return wgpuDeviceCreateBuffer(Device, &desc);
        }

        // Uploads data by creating the buffer already-mapped and memcpy'ing CPU-side, then unmapping —
        // NO queue.write_buffer, so no per-call Metal blit command buffer. wgpu-native's Metal backend
        // commits (and never reclaims) a command buffer for every queue.write_buffer/write_texture; a
        // frame issues dozens, which pile up to Metal's hard 4096 in-flight limit ("N outstanding command
        // buffers exceeds the limit" → device lost → fatal). mappedAtCreation writes are pure CPU copies.

        public IntPtr CreateBufferMapped(ReadOnlySpan<byte> data, WGPUBufferUsage usage, ulong minSize = 0)
        {
            ulong size = ((ulong)data.Length + 3UL) & ~3UL;   // mappedAtCreation requires a size multiple of 4
            if (size < minSize) size = (minSize + 3UL) & ~3UL;
            if (size == 0) size = 4;
#if WGPU_BROWSER
            // The browser's WebGPU (JS) backend has no Metal command-buffer accounting problem, and raw
            // mapped-range pointers can't be marshaled to JS — so just create + queue.writeBuffer there.
            var bdesc = new WGPUBufferDescriptor { usage = usage | WGPUBufferUsage.CopyDst, size = size };
            IntPtr bbuf = wgpuDeviceCreateBuffer(Device, &bdesc);
            if (data.Length > 0) WriteBuffer(bbuf, data);
            return bbuf;
#else
            var desc = new WGPUBufferDescriptor { usage = usage, size = size, mappedAtCreation = 1 };
            IntPtr buf = wgpuDeviceCreateBuffer(Device, &desc);
            if (data.Length > 0)
            {
                void* range = wgpuBufferGetMappedRange(buf, 0, (nuint)size);
                fixed (byte* src = data)
                    System.Buffer.MemoryCopy(src, range, size, (ulong)data.Length);
            }
            wgpuBufferUnmap(buf);
            return buf;
#endif
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
            // No GC.KeepAlive needed any more: the adapter/device callbacks are static
            // [UnmanagedCallersOnly] methods, so there is no delegate object to keep alive.
        }
    }
}
