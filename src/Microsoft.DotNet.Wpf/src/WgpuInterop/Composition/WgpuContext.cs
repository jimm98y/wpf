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

        /// <summary>
        /// True when wgpu selected the OpenGL/GLES backend (Android without a usable Vulkan, ANGLE,
        /// older Linux). Some upload paths differ there -- see WgpuSceneRenderer.CreateTexture.
        /// </summary>
        public bool IsOpenGL => AdapterDescription?.Contains("backend=OpenGL") == true;

        /// <summary>
        /// True when the GL adapter is VirGL -- Mesa's virtio-gpu driver, which forwards GL from a
        /// guest VM to the host (Parallels, QEMU, GNOME Boxes). It re-translates the driver's GLSL
        /// for the host, and that extra translation step miscompiles the coverage rasterizer; see
        /// WgpuSceneRenderer's constructor for the evidence and the fallback it triggers.
        /// </summary>
        public bool IsVirgl => AdapterDescription?.Contains("virgl", StringComparison.OrdinalIgnoreCase) == true;

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

        // ---- Which backends the instance enables -------------------------------------
        //
        // Everywhere but Android: all of them (the historical behaviour). wgpu then picks the best
        // adapter, and creating a surface for the also-enabled-but-unused backends is harmless.
        //
        // On Android it is NOT harmless. wgpu-core creates a native surface for EVERY enabled backend
        // when you create a WGPUSurface, and vkCreateAndroidSurfaceKHR connects the ANativeWindow to
        // the EGL producer API and keeps it connected for the surface's lifetime. So on a device that
        // enables both Vulkan and GL, the GL backend's own eglCreateWindowSurface finds the window
        // taken and fails:
        //     BufferQueueProducer: connect: already connected (cur=1 req=1)
        //     libEGL: eglCreateWindowSurface: native_window_api_connect ... EGL_BAD_ALLOC
        // which wgpu-native reports through handle_error_fatal -- a Rust panic that aborts the
        // process inside wgpuSurfaceConfigure. Enabling exactly one backend is what avoids it.
        //
        // Vulkan first (every Android device made this decade has it, and it is much faster), GL as
        // the fallback for devices -- and emulators -- whose Vulkan wgpu will not accept.
        //
        // Linux desktop gets the same one-backend-at-a-time treatment as Android, for the same
        // reason (a wgpu surface is created for EVERY enabled backend, so the GL backend would
        // build a wl_egl_window on the very wl_surface the Vulkan backend just took), plus a
        // second reason Android does not have: on Linux, which backend reaches the GPU is not
        // knowable up front. Under a VM whose host exposes VirGL (OpenGL passthrough) but not
        // Venus (Vulkan passthrough), Vulkan enumerates only lavapipe -- a CPU rasterizer --
        // while GL reaches the real GPU. Taking "Vulkan first" on faith there costs an order of
        // magnitude. So Linux ORDERS its candidates and lets the adapter type decide: see
        // SelectBackends, which rejects a software adapter while a hardware one is still on the
        // table. WPF_WEBGPU_BACKEND=vulkan|gl|all pins it for triage.
        //
        private static ulong PreferredBackends =>
            OperatingSystem.IsAndroid() ? WGPUInstanceBackend_Vulkan : WGPUInstanceBackend_All;

        private static ulong FallbackBackends =>
            OperatingSystem.IsAndroid() ? WGPUInstanceBackend_GL : 0;

        /// <summary>True on a Linux desktop (Android is Linux to OperatingSystem, and is handled
        /// by the Preferred/Fallback pair above).</summary>
        private static bool IsLinuxDesktop =>
            OperatingSystem.IsLinux() && !OperatingSystem.IsAndroid();

        /// <summary>The backend masks Linux tries, in order. Vulkan first because on real hardware
        /// it is the better backend; GL second because it is the one that works under VirGL.</summary>
        private static ulong[] LinuxBackendCandidates()
        {
            switch (Environment.GetEnvironmentVariable("WPF_WEBGPU_BACKEND"))
            {
                case "vulkan": return new[] { WGPUInstanceBackend_Vulkan };
                case "gl": return new[] { WGPUInstanceBackend_GL };
                case "all": return new[] { WGPUInstanceBackend_All };
                default: return new[] { WGPUInstanceBackend_Vulkan, WGPUInstanceBackend_GL };
            }
        }

        /// <summary>Create a wgpu instance limited to <paramref name="backends"/> (0 = all).</summary>
        private static IntPtr CreateInstance(ulong backends)
        {
            IntPtr waylandDisplay = LinuxPlatform.WaylandDisplay;
            if (backends == WGPUInstanceBackend_All && waylandDisplay == IntPtr.Zero)
                return wgpuCreateInstance(null);

            var extras = new WGPUInstanceExtras
            {
                chain = new WGPUChainedStruct { next = null, sType = WGPUSType_InstanceExtras },
                backends = backends,
            };
            // The GLES backend on Wayland cannot discover the display on its own -- there is no
            // wl_proxy_get_display -- so it has to be handed the one connection the process owns
            // (WaylandWindow's). Without it, eglGetPlatformDisplay opens a SECOND connection and
            // the wl_egl_window it builds belongs to a display our surfaces do not live on.
            if (waylandDisplay != IntPtr.Zero)
            {
                extras.displayHandle.type = WGPUNativeDisplayHandleType_Wayland;
                extras.displayHandle.data.display = (void*)waylandDisplay;
            }
            var desc = new WGPUInstanceDescriptor { nextInChain = (WGPUChainedStruct*)&extras };
            LogSink?.Invoke($"CreateInstance backends=0x{backends:x} waylandDisplay=0x{waylandDisplay.ToInt64():x} " +
                            $"displayHandleType={extras.displayHandle.type}");
            return wgpuCreateInstance(&desc);
        }

        /// <summary>
        /// Create an instance for <paramref name="backends"/> and ask it for an adapter. Returns
        /// the adapter (or Zero) and, on success, its description and whether it is a software
        /// rasterizer. The instance is returned so an unwanted candidate can be released.
        /// Must be called under <see cref="s_requestLock"/>.
        /// </summary>
        private static IntPtr TryAcquireAdapter(ulong backends, out IntPtr instance, out string? description, out bool isSoftware)
        {
            description = null;
            isSoftware = false;

            instance = CreateInstance(backends);
            if (instance == IntPtr.Zero)
                return IntPtr.Zero;

            return RequestAdapter(instance, out description, out isSoftware);
        }

        /// <summary>
        /// Ask an existing instance for an adapter, reporting its description and whether it is a
        /// software rasterizer. Must be called under <see cref="s_requestLock"/>.
        /// </summary>
        private static IntPtr RequestAdapter(IntPtr instance, out string? description, out bool isSoftware)
        {
            description = null;
            isSoftware = false;

            s_adapterResult = IntPtr.Zero;
            s_adapterDone = false;

            var callbackInfo = new WGPURequestAdapterCallbackInfo
            {
                mode = WGPUCallbackMode.AllowProcessEvents,
                callback = (IntPtr)(delegate* unmanaged[Cdecl]<WGPURequestAdapterStatus, IntPtr, WGPUStringView, IntPtr, IntPtr, void>)&OnAdapterReady,
            };
            var options = new WGPURequestAdapterOptions { powerPreference = WGPUPowerPreference.HighPerformance };
            wgpuInstanceRequestAdapter(instance, &options, callbackInfo);
            for (int i = 0; i < 1000 && !s_adapterDone; i++) { wgpuInstanceProcessEvents(instance); Thread.Sleep(1); }

            IntPtr adapter = s_adapterResult;
            if (adapter == IntPtr.Zero)
                return IntPtr.Zero;

            var info = new WGPUAdapterInfo();
            if (wgpuAdapterGetInfo(adapter, &info) == WGPUStatus.Success)
            {
                static string S(WGPUStringView v) => v.data == null ? "" : System.Text.Encoding.UTF8.GetString(v.data, (int)v.length);
                description = $"backend={info.backendType} type={info.adapterType} device='{S(info.device)}' desc='{S(info.description)}'";
                isSoftware = info.adapterType == WGPUAdapterType.CPU;
            }
            return adapter;
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

            IntPtr instance;
            IntPtr adapterResult, deviceResult;
            var ctx = new WgpuContext();

            lock (s_requestLock)
            {
            s_deviceResult = IntPtr.Zero;
            s_deviceDone = false;

            if (IsLinuxDesktop)
            {
                // Try the candidates in order and keep the first HARDWARE adapter. A software
                // adapter is remembered but not accepted while another candidate is untried, so a
                // box where Vulkan is lavapipe but GL is the real GPU (VirGL) lands on GL.
                IntPtr softInstance = IntPtr.Zero, softAdapter = IntPtr.Zero;
                string? softDescription = null;
                instance = IntPtr.Zero;
                adapterResult = IntPtr.Zero;

                foreach (ulong backends in LinuxBackendCandidates())
                {
                    IntPtr candidateAdapter = TryAcquireAdapter(backends, out IntPtr candidateInstance, out string? description, out bool isSoftware);
                    if (candidateAdapter == IntPtr.Zero)
                    {
                        LogSink?.Invoke($"no adapter for backends 0x{backends:x}");
                        if (candidateInstance != IntPtr.Zero) wgpuInstanceRelease(candidateInstance);
                        continue;
                    }

                    if (!isSoftware)
                    {
                        instance = candidateInstance;
                        adapterResult = candidateAdapter;
                        ctx.AdapterDescription = description;
                        break;
                    }

                    LogSink?.Invoke($"backends 0x{backends:x} offered only a software adapter ({description})");
                    if (softAdapter == IntPtr.Zero)
                    {
                        softInstance = candidateInstance;
                        softAdapter = candidateAdapter;
                        softDescription = description;
                    }
                    else
                    {
                        wgpuInstanceRelease(candidateInstance);
                    }
                }

                if (adapterResult == IntPtr.Zero)
                {
                    // Every candidate was software (or absent). Software still renders, so take it.
                    instance = softInstance;
                    adapterResult = softAdapter;
                    ctx.AdapterDescription = softDescription;
                }
                else if (softInstance != IntPtr.Zero)
                {
                    wgpuInstanceRelease(softInstance);
                }

                if (instance == IntPtr.Zero)
                    throw new InvalidOperationException("wgpuCreateInstance failed.");
                ctx.Instance = instance;
            }
            else
            {
                instance = CreateInstance(PreferredBackends);
                if (instance == IntPtr.Zero)
                    throw new InvalidOperationException("wgpuCreateInstance failed.");
                ctx.Instance = instance;

                adapterResult = RequestAdapter(instance, out string? description, out _);
                ctx.AdapterDescription = description;

                // The instance may be pinned to a single backend (see PreferredBackends). If that one has
                // no adapter -- an emulator whose Vulkan wgpu rejects as non-compliant, a device with no
                // Vulkan driver -- fall back to the next one rather than failing outright.
                if (adapterResult == IntPtr.Zero && FallbackBackends != 0)
                {
                    LogSink?.Invoke($"no adapter for backends 0x{PreferredBackends:x}; retrying with 0x{FallbackBackends:x}");
                    wgpuInstanceRelease(instance);
                    instance = CreateInstance(FallbackBackends);
                    ctx.Instance = instance;

                    adapterResult = RequestAdapter(instance, out description, out _);
                    ctx.AdapterDescription = description;
                }
            }

            if (adapterResult == IntPtr.Zero)
                throw new InvalidOperationException("Could not acquire a WebGPU adapter.");
            ctx.Adapter = adapterResult;

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

        public void WriteBuffer(IntPtr buffer, ReadOnlySpan<byte> data) => WriteBuffer(buffer, 0, data);

        public void WriteBuffer(IntPtr buffer, ulong offset, ReadOnlySpan<byte> data)
        {
            fixed (byte* p = data)
            {
                wgpuQueueWriteBuffer(Queue, buffer, offset, p, (nuint)data.Length);
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
