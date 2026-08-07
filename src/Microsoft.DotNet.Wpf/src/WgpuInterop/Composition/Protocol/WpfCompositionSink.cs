// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// WpfCompositionSink -- the bridge that turns PresentationCore's DUCE.Channel
// command stream into on-screen WebGPU frames. It is the concrete implementation
// of the IMilCompositionSink contract (defined in PresentationCore's exports.cs):
// the channel forwards every resource/command/commit here, this class feeds them
// to MilcoreEngine (which rebuilds the SceneVisual tree from the real MILCMD
// binary), and on Commit it presents each composition target to its HWND swap
// chain via the shared WgpuSceneRenderer -- the cross-platform analog of milcore
// presenting an HwndTarget through Direct3D.
//
// This type lives in the standalone WgpuInterop assembly and deliberately has NO
// reference to PresentationCore: its method *shapes* match IMilCompositionSink
// exactly (byte-oriented), so the full-WPF hookup is a trivial forwarding shim
// (PresentationCore implements IMilCompositionSink by delegating to an instance of
// this class, then calls DUCE.ManagedComposition.Register on it). Keeping the
// dependency one-way preserves WgpuInterop's independent build + test story.
//
// Threading: like milcore's render thread, a single sink instance is driven from
// one thread (the channel/UI thread for SameThread channels).
//

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Microsoft.Wpf.Interop.WebGpu;
using static Microsoft.Wpf.Interop.WebGpu.Wgpu;

namespace Microsoft.Wpf.Interop.WebGpu.Composition.Protocol
{
    internal sealed unsafe partial class WpfCompositionSink : IDisposable
    {
        private readonly MilcoreEngine _engine = new();
        private readonly Dictionary<uint, TargetSurface> _surfaces = new();
        private WgpuContext? _ctx;
        private WgpuSceneRenderer? _renderer;
        private uint _nextHandle;
        private string _targetsSig = "";
        private int _diagCount;
        private bool _loggedLayered;
        private int _layeredFrames;
        private long _perfRealizeTicks, _perfRenderTicks, _perfRenderOnlyTicks, _perfPresentTicks;
        private long _gcBytes0, _perfRealizeAlloc, _perfRenderAlloc;
        private int _gc0, _gc1, _gc2;
        private int _perfFrames;
        private bool _disposed;

        // Optional diagnostics: when WPF_WEBGPU_SINK_LOG names a file, the sink appends
        // lifecycle/present lines there. This is how a hosted WPF process proves its
        // frames were composited through WebGPU (rather than native milcore).
        private static readonly string? s_logPath =
            Environment.GetEnvironmentVariable("WPF_WEBGPU_SINK_LOG");

        // WPF_WEBGPU_SINK_DUMP names a PNG path for an offscreen dump of the composed frame.
        private static readonly string? s_dumpPath =
            Environment.GetEnvironmentVariable("WPF_WEBGPU_SINK_DUMP");

        // WF_SURF_DUMP names a PNG path for a readback of the REAL swapchain surface (the exact on-screen
        // pixels, unlike the offscreen VerifyOffscreen render). Adds CopySrc to the surface usage when set.
        private static readonly string? s_surfDump =
            Environment.GetEnvironmentVariable("WF_SURF_DUMP");
        private bool _surfDumped;

        // Diagnostics: also print the periodic PERF lines to the console (browser DevTools)
        // so live perf can be inspected without pulling the VFS log (?perf=1 in the wasm head).
        private static readonly bool s_perfToConsole =
            Environment.GetEnvironmentVariable("WPF_WEBGPU_PERF_CONSOLE") == "1";

        public WpfCompositionSink()
        {
            // Resolve WPF glyph runs to real fonts so text renders. The run carries a
            // managed font descriptor (file path + face index + style simulations from
            // GlyphTypeface), so this is fully cross-platform -- no COM / DirectWrite.
            // Set WPF_WEBGPU_TEXT=0 to disable text.
            if (Environment.GetEnvironmentVariable("WPF_WEBGPU_TEXT") != "0")
            {
                var fonts = new Text.ManagedFontResolver();
                _engine.ManagedFontResolver = fonts.Resolve;
            }
            Log($"WpfCompositionSink created (pid {Environment.ProcessId})");
        }

        /// <summary>Total swap-chain textures successfully acquired (diagnostics/tests).</summary>
        public int AcquiredFrames { get; private set; }

        /// <summary>Total frames successfully presented (diagnostics/tests).</summary>
        public int PresentedFrames { get; private set; }

        // Per-target (HWND) timestamp of the last present, to detect an isolated/idle frame that needs a
        // compositor flush vs. a frame inside a continuous animation burst (which composites on its own).
        private readonly System.Collections.Generic.Dictionary<ulong, long> _lastPresentTicks = new();
        private readonly System.Collections.Generic.Dictionary<ulong, int> _targetPresentCount = new();

        /// <summary>The decoded composition state (exposed for verification).</summary>
        public MilcoreEngine Engine => _engine;

        /// <summary>The GPU context, created on first use.</summary>
        public WgpuContext Context { get { EnsureGpu(); return _ctx!; } }

        /// <summary>The shared scene renderer, created on first use.</summary>
        public WgpuSceneRenderer Renderer { get { EnsureGpu(); return _renderer!; } }

        // ---- IMilCompositionSink-shaped surface --------------------------------------

        public void OpenChannel(int channelId, int referenceChannelId) { }

        public void CloseChannel(int channelId) { }

        public uint CreateOrAddRef(int channelId, uint handle, uint resourceType, out bool created)
        {
            created = handle == 0;
            if (handle == 0)
            {
                handle = ++_nextHandle;
            }
            else if (handle > _nextHandle)
            {
                // Keep the allocator ahead of any client-assigned handles.
                _nextHandle = handle;
            }
            _engine.CreateOrAddRef(handle, (MilResourceTypeId)resourceType);
            return handle;
        }

        public bool ReleaseOnChannel(int channelId, uint handle)
            // Return whether the resource actually LEFT the channel (refcount hit 0). DUCE.Resource only
            // then clears its cached handle; returning true unconditionally zeroed the handle of resources
            // still shared by other owners (broke PhotoFlipper's reused DiffuseMaterials).
            => _engine.Release(handle);

        public void SendCommand(int channelId, byte[] data, bool sendInSeparateBatch)
            => _engine.SubmitCommand(data);

        // Receive an image-source's pixels as straight BGRA32 (top-down, stride bytes/row),
        // marshalled on the managed PresentationCore side -- no COM. Convert to the engine's
        // straight RGBA and register it for image brushes / DrawImage.
        public void SendBitmap(int channelId, uint handle, int width, int height, int stride, byte[] pixels)
        {
            if (width <= 0 || height <= 0 || pixels == null || stride < width * 4 ||
                (long)stride * height > pixels.Length)
            {
                Log($"bitmap 0x{handle:x}: unsupported/failed");
                return;
            }

            var rgba = new byte[width * height * 4];
            for (int y = 0; y < height; y++)
            {
                int s = y * stride, d = y * width * 4;
                for (int x = 0; x < width; x++)
                {
                    int si = s + x * 4, di = d + x * 4;
                    rgba[di]     = pixels[si + 2];   // R <- B-G-R-A source
                    rgba[di + 1] = pixels[si + 1];   // G
                    rgba[di + 2] = pixels[si];       // B
                    rgba[di + 3] = pixels[si + 3];   // A (straight)
                }
            }
            _engine.SetBitmap(handle, rgba, width, height);
            Log($"bitmap 0x{handle:x}: {width}x{height}");
        }

        // Receive the current decoded video frame for a media-player resource (managed backend, e.g. AVFoundation
        // on macOS). Straight BGRA32, top-down, rowBytes/row (decoders align rows, so rowBytes may exceed
        // width*4). Convert to the engine's straight RGBA (opaque -- video has no alpha) and store keyed by the
        // media-player handle; the MilDrawVideo record referencing that handle samples it. A fresh array each
        // call makes the frame texture re-upload every frame.
        public void SendVideoFrame(int channelId, uint mediaHandle, int width, int height, int rowBytes, byte[] pixels)
        {
            if (width <= 0 || height <= 0 || pixels == null || rowBytes < width * 4 ||
                (long)rowBytes * height > pixels.Length)
            {
                Log($"video 0x{mediaHandle:x}: unsupported/failed ({width}x{height} rb={rowBytes})");
                return;
            }

            var rgba = new byte[width * height * 4];
            for (int y = 0; y < height; y++)
            {
                int s = y * rowBytes, d = y * width * 4;
                for (int x = 0; x < width; x++)
                {
                    int si = s + x * 4, di = d + x * 4;
                    rgba[di]     = pixels[si + 2];   // R <- B-G-R-A source
                    rgba[di + 1] = pixels[si + 1];   // G
                    rgba[di + 2] = pixels[si];       // B
                    rgba[di + 3] = 255;              // opaque
                }
            }
            _engine.SetVideoFrame(mediaHandle, rgba, width, height);
        }

        public void BeginCommand(int channelId, byte[] data, int extraSize)
            => _engine.BeginCommand(data);

        public void AppendCommandData(int channelId, byte[] data)
            => _engine.AppendCommandData(data);

        public void EndCommand(int channelId)
            => _engine.EndCommand();

        public void CloseBatch(int channelId) { }

        public void Commit(int channelId) => RenderTargets();

        public void SyncFlush(int channelId) => RenderTargets();

        /// <summary>
        /// Renders a bitmap composition target (RenderTargetBitmap via the sync channel) and
        /// returns its pixels as premultiplied BGRA32, top-down, width*4 stride. Null when the
        /// target has no renderable root or this platform has no synchronous GPU readback
        /// (browser -- its wgpu readback is Promise-only).
        /// </summary>
        public byte[]? ReadbackTarget(int channelId, uint targetHandle)
        {
            try
            {
                _engine.Realize();
                if (!_engine.Targets.TryGetValue(targetHandle, out MilTarget? t)
                    || t.RootHandle == 0 || t.Width <= 0 || t.Height <= 0)
                    return null;
                SceneVisual? root = _engine.VisualByHandle(t.RootHandle);
                if (root is null) return null;

                EnsureGpu();
                byte[] px = _renderer!.RenderToRgba(root, t.Width, t.Height, t.ClearColor, srgbOutput: true);
                for (int i = 0; i < px.Length; i += 4)
                    (px[i], px[i + 2]) = (px[i + 2], px[i]);   // RGBA -> BGRA
                Log($"readback target 0x{targetHandle:x}: {t.Width}x{t.Height} root={t.RootHandle}");
                return px;
            }
            catch (Exception ex)
            {
                Log($"readback target 0x{targetHandle:x} FAILED: {ex.GetType().Name}: {ex.Message}");
                return null;
            }
        }

        // ---- presentation ------------------------------------------------------------

        /// <summary>Render and present every target that has a window and a root visual.</summary>
        public void RenderTargets()
        {
            EnsureGpu();         // ensure the renderer (and VisualRasterizer) exist before Realize
            _renderer!.BeginFrame();
            WgpuSceneRenderer.PerfReset();
            long ra0 = GC.GetAllocatedBytesForCurrentThread();
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            _engine.Realize();   // re-parse content with the current resource state
            _perfRealizeTicks += System.Diagnostics.Stopwatch.GetTimestamp() - t0;
            long ra1 = GC.GetAllocatedBytesForCurrentThread();
            _perfRealizeAlloc += ra1 - ra0;
            string sig = "";
            foreach (KeyValuePair<uint, MilTarget> tk in _engine.Targets)
                sig += $"0x{tk.Key:x}:{tk.Value.Width}x{tk.Value.Height}:{tk.Value.Transparency};";
            if (sig != _targetsSig)
            {
                _targetsSig = sig;
                foreach (KeyValuePair<uint, MilTarget> tk in _engine.Targets)
                    Log($"target 0x{tk.Key:x}: hwnd=0x{tk.Value.Hwnd:x} root={tk.Value.RootHandle} {tk.Value.Width}x{tk.Value.Height} layered={tk.Value.IsLayered} (transp=0x{tk.Value.Transparency:x})");
            }
            // Where a popup cannot own a presentable transparent surface (Android on the GLES
            // backend), its scene is drawn INTO the window it belongs to instead. Collect those
            // first, translated to where the popup sits, so the owner's single render pass paints
            // them on top of its own content -- see NativePlatform.PopupsShareOwnerSurface.
            EnsureGpu();
            List<SceneVisual>? popupOverlays = CollectPopupOverlays();

            foreach (KeyValuePair<uint, MilTarget> kv in _engine.Targets)
            {
                MilTarget t = kv.Value;
                if (t.Hwnd == 0 || t.RootHandle == 0 || t.Width <= 0 || t.Height <= 0) continue;
                SceneVisual? root = _engine.VisualByHandle(t.RootHandle);
                if (root is null) continue;

                // Already drawn into its owner above; it has no surface of its own to present to.
                if (popupOverlays != null && t.IsLayered) continue;

                if (s_logPath != null && (_diagCount < 5 || _diagCount % 30 == 0) && _diagCount < 200)
                {
                    _diagCount++;
                    int drawables = CountDrawables(root);
                    Log($"DIAG frame#{_diagCount}: root=0x{t.RootHandle:x} drawables={drawables}");
                }

                if (t.IsLayered && Platform.NativePlatform.SupportsLayeredWindows)
                {
                    // ComboBox/Menu/ToolTip popups live in WS_EX_LAYERED per-pixel-alpha windows;
                    // present those via UpdateLayeredWindow from a premultiplied off-screen render.
                    // Where the OS has no layered-window path (macOS), fall through and present the
                    // popup through its own swap-chain surface instead (opaque, but its content shows).
                    PresentLayered(root, t);
                    continue;
                }
                TargetSurface ts = EnsureSurface(kv.Key, t);
                // Android delivers the native Surface asynchronously (surfaceCreated), so a window's
                // first frame or two legitimately have nothing to present into; EnsureSurface builds
                // the wgpu surface as soon as one exists. No other platform can be here.
                if (ts.Surface == IntPtr.Zero) continue;
                long t1 = System.Diagnostics.Stopwatch.GetTimestamp();
                Present(ts, popupOverlays != null ? Overlay(root, popupOverlays) : root, t);
                _perfRenderTicks += System.Diagnostics.Stopwatch.GetTimestamp() - t1;
            }
            _perfRenderAlloc += GC.GetAllocatedBytesForCurrentThread() - ra1;

            _renderer!.EndFrame();

            if (++_perfFrames >= 60 && (s_logPath != null || s_perfToConsole))
            {
                double ms(long ticks) => ticks * 1000.0 / System.Diagnostics.Stopwatch.Frequency / _perfFrames;
                double msr(long ticks) => ticks * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
                void Emit(string m) { Log(m); if (s_perfToConsole) Console.WriteLine(m); }
                Emit($"PERF: parse={msr(_engine.PerfParseTicks):0.0}ms ({_engine.PerfParsed} visuals) brushes={msr(_engine.PerfBrushTicks):0.0}ms | collect={msr(WgpuSceneRenderer.PerfCollectTicks):0.0}ms (layerhash={msr(WgpuSceneRenderer.PerfHashTicks):0.0}ms hits={WgpuSceneRenderer.PerfLayerHits} miss={WgpuSceneRenderer.PerfLayerMiss}) encode={msr(WgpuSceneRenderer.PerfEncodeTicks):0.0}ms submit={msr(WgpuSceneRenderer.PerfSubmitTicks):0.0}ms (last frame)");
                Emit($"PERF/frame: realize={ms(_perfRealizeTicks):0.0}ms render={ms(_perfRenderOnlyTicks):0.0}ms present={ms(_perfPresentTicks):0.0}ms | " +
                    $"rasterized={WgpuSceneRenderer.PerfCoverage} (localcache={WgpuSceneRenderer.PerfLocalCoverage}) textures={WgpuSceneRenderer.PerfTextures} bindgroups={WgpuSceneRenderer.PerfBindGroups} layers={WgpuSceneRenderer.PerfLayers} readbacks={WgpuSceneRenderer.PerfReadbacks}");
                long allocNow = GC.GetTotalAllocatedBytes();
                int g0 = GC.CollectionCount(0), g1 = GC.CollectionCount(1), g2 = GC.CollectionCount(2);
                if (_gcBytes0 != 0)
                    Emit($"PERF/gc: alloc={(allocNow - _gcBytes0) / 1024.0 / _perfFrames:0.0}KB/frame (realize={_perfRealizeAlloc / 1024.0 / _perfFrames:0.0} render={_perfRenderAlloc / 1024.0 / _perfFrames:0.0} [collect={WgpuSceneRenderer.PerfCollectAlloc / 1024.0 / _perfFrames:0.0} exec={WgpuSceneRenderer.PerfExecAlloc / 1024.0 / _perfFrames:0.0}]) gen0={g0 - _gc0} gen1={g1 - _gc1} gen2={g2 - _gc2} (over {_perfFrames} frames)");
                _gcBytes0 = allocNow; _gc0 = g0; _gc1 = g1; _gc2 = g2; _perfRealizeAlloc = 0; _perfRenderAlloc = 0;
                WgpuSceneRenderer.PerfCollectAlloc = 0; WgpuSceneRenderer.PerfExecAlloc = 0;
                _perfFrames = 0; _perfRealizeTicks = 0; _perfRenderTicks = 0; _perfRenderOnlyTicks = 0; _perfPresentTicks = 0;
            }
        }

        /// <summary>
        /// The popup scenes to draw into their owner's surface this frame, each already translated to
        /// the popup's position, or null where popups present themselves (every platform but Android
        /// on GLES). Returns null rather than an empty list when there is nothing to composite, so the
        /// owner's scene is passed through untouched and no wrapper visual is allocated.
        /// </summary>
        private List<SceneVisual>? CollectPopupOverlays()
        {
            if (!Platform.NativePlatform.PopupsShareOwnerSurface)
                return null;

            List<SceneVisual>? overlays = null;
            foreach (KeyValuePair<uint, MilTarget> kv in _engine.Targets)
            {
                MilTarget t = kv.Value;
                if (!t.IsLayered || t.IsBitmap || t.Hwnd == 0 || t.RootHandle == 0) continue;
                if (t.Width <= 0 || t.Height <= 0) continue;

                SceneVisual? root = _engine.VisualByHandle(t.RootHandle);
                if (root is null) continue;

                // The popup's scene is in ITS window's coordinates; move it to where that window sits
                // inside the activity. Both are device pixels, so this is a plain translation.
                Platform.NativePlatform.GetWindowOrigin((IntPtr)t.Hwnd, out int x, out int y);
                var placed = new SceneVisual { Offset = new System.Numerics.Vector2(x, y) };
                placed.Children.Add(root);
                (overlays ??= new List<SceneVisual>()).Add(placed);
            }
            return overlays;
        }

        /// <summary>Wraps the owner's scene and the popup overlays in one parent, so a single render
        /// pass draws the window and then the popups above it (children draw after their parent).</summary>
        private static SceneVisual Overlay(SceneVisual root, List<SceneVisual> overlays)
        {
            var composite = new SceneVisual();
            composite.Children.Add(root);
            foreach (SceneVisual o in overlays) composite.Children.Add(o);
            return composite;
        }

        // Render a layered popup off-screen with a transparent clear (so its shadow/rounded
        // corners keep real alpha) and hand the premultiplied bitmap to the OS compositor.
        private void PresentLayered(SceneVisual root, MilTarget t)
        {
            byte[] rgba = _renderer!.RenderToRgba(root, t.Width, t.Height, new RgbaColor(0, 0, 0, 0), srgbOutput: true);
            Platform.NativePlatform.TryPresentLayered((IntPtr)t.Hwnd, rgba, t.Width, t.Height);
            PresentedFrames++;
            if (t.Width > 4 && t.Height > 4 && CountDrawables(root) > 0) _layeredFrames++;
            if (!_loggedLayered && _layeredFrames == 25)
            {
                _loggedLayered = true;
                Log($"layered popup presented to HWND 0x{t.Hwnd:x} ({t.Width}x{t.Height}) drawables={CountDrawables(root)}");
                string? dump = Environment.GetEnvironmentVariable("WPF_WEBGPU_SINK_DUMP");
                if (dump != null)
                {
                    string p = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(dump) ?? ".",
                        System.IO.Path.GetFileNameWithoutExtension(dump) + "_popup" + System.IO.Path.GetExtension(dump));
                    PngWriter.Write(p, rgba, t.Width, t.Height, maxWidth: 4000);
                    Log($"wrote popup {p}");
                }
            }
        }

        // A surface can get STUCK returning no drawable when it was configured while its window
        // wasn't visible yet (e.g. created behind a fullscreen Space, or before the first
        // order-front) — the Occluded/null state then persists even after the window shows.
        // Only Outdated/Timeout normally trigger a reconfigure, so force one after a run of
        // drawable-less acquires to let the swapchain re-attach to the now-visible layer.
        private void NoDrawable(TargetSurface ts)
        {
            if (++ts.NullAcquires % 30 != 0) return;
            Log($"no drawable {ts.NullAcquires} frames in a row; reconfiguring surface");
            Configure(ts);
        }

        private void Present(TargetSurface ts, SceneVisual root, MilTarget t)
        {
            // Nothing to present to. Asking for the next texture of a Fifo swap chain whose surface
            // the compositor has stopped scheduling BLOCKS -- on the UI thread, until the window is
            // visible again -- so this check has to come before the acquire, not after.
            if (!Platform.NativePlatform.IsWindowVisible((IntPtr)t.Hwnd))
            {
                return;
            }

            WGPUSurfaceTexture surfaceTexture;
            wgpuSurfaceGetCurrentTexture(ts.Surface, &surfaceTexture);

            if (surfaceTexture.status == WGPUSurfaceGetCurrentTextureStatus.Outdated ||
                surfaceTexture.status == WGPUSurfaceGetCurrentTextureStatus.Timeout)
            {
                Configure(ts);                 // window resized/lost; reconfigure and skip this frame
                return;
            }
            if (surfaceTexture.status != WGPUSurfaceGetCurrentTextureStatus.SuccessOptimal &&
                surfaceTexture.status != WGPUSurfaceGetCurrentTextureStatus.SuccessSuboptimal &&
                surfaceTexture.status != WGPUSurfaceGetCurrentTextureStatus.Occluded)
            {
                NoDrawable(ts);
                return;
            }
            // A valid status can still hand back a null texture (e.g. an occluded/off-screen
            // drawable). Rendering to it would panic inside wgpu-native, so skip this frame.
            if (surfaceTexture.texture == IntPtr.Zero)
            {
                NoDrawable(ts);
                return;
            }

            ts.NullAcquires = 0;
            AcquiredFrames++;

            // Render through a view in RenderFormat. Usually identical to the swapchain format (default
            // view), but on the OpenGL gamma path it's the UNORM view over the sRGB swapchain so
            // pre-encoded gamma bytes store verbatim.
            IntPtr view;
            if (ts.RenderFormat != ts.Format)
            {
                var vdesc = new WGPUTextureViewDescriptor
                {
                    format = ts.RenderFormat,
                    dimension = WGPUTextureViewDimension._2D,
                    baseMipLevel = 0, mipLevelCount = 1,
                    baseArrayLayer = 0, arrayLayerCount = 1,
                    aspect = WGPUTextureAspect.All,
                    usage = WGPUTextureUsage.RenderAttachment,
                };
                view = wgpuTextureCreateView(surfaceTexture.texture, (IntPtr)(&vdesc));
            }
            else
            {
                view = wgpuTextureCreateView(surfaceTexture.texture, IntPtr.Zero);
            }
            long ta = System.Diagnostics.Stopwatch.GetTimestamp();
            // A layered popup composites over what's behind its window, so clear fully transparent
            // (premultiplied 0,0,0,0) rather than the target's opaque clear colour. WPF sends a popup
            // clear of (1,1,1,0) — white RGB, alpha 0 — which an opaque surface would show as a white
            // rectangle; on a premultiplied surface it must be zeroed or it tints the transparent areas.
            RgbaColor clear = ts.Transparent ? new RgbaColor(0, 0, 0, 0) : t.ClearColor;
            // Composite any hosted (WindowsFormsHost) scenes on top of the WPF scene — same SceneVisual
            // type + same renderer, so no bitmap/readback.
            _renderer!.RenderSceneToView(EmbeddedContent.Compose(root), view, ts.RenderFormat, t.Width, t.Height, clear, ts.Transparent);
            _perfRenderOnlyTicks += System.Diagnostics.Stopwatch.GetTimestamp() - ta;

            // Definitive on-screen capture: read back the REAL swapchain texture (not a separate
            // offscreen render), so what we inspect is exactly what's presented. Once, after settle.
            if (s_surfDump != null && !_surfDumped && AcquiredFrames == 40)
            {
                _surfDumped = true;
                DumpSurface(surfaceTexture.texture, ts, s_surfDump);
            }

            // Offscreen PNG dump keyed to ACQUIRED (rendered) frames, so it fires even when the window
            // is occluded/off-screen (present never succeeds in a detached/headless run). Env-gated by
            // WPF_WEBGPU_SINK_DUMP; fires once around frame 90 so animation has settled.
            if (s_dumpPath != null && AcquiredFrames == 40)
                VerifyOffscreen(EmbeddedContent.Compose(root), t, AcquiredFrames);

            long tp = System.Diagnostics.Stopwatch.GetTimestamp();
            WGPUStatus pres = wgpuSurfacePresent(ts.Surface);
            _perfPresentTicks += System.Diagnostics.Stopwatch.GetTimestamp() - tp;
            if (pres == WGPUStatus.Success)
            {
                PresentedFrames++;
                // Commit the compositor transaction so a frame shows immediately even if the window then goes
                // idle (an autoresizing CAMetalLayer sublayer's contents otherwise stay uncommitted until an
                // unrelated relayout — e.g. a resize — so a static window is blank until you resize it).
                // CATransaction.flush is a synchronous window-server commit (~one vsync), too costly to run on
                // every animation frame, so gate it: flush the FIRST FEW frames of each window (guarantees the
                // startup/settle frames reach the glass regardless of their timing) AND any later ISOLATED frame
                // (>100ms since this target's last present = an idle one-shot render). Frames inside a
                // continuous animation burst are close together and composite on their own, so they skip it.
                long nowTicks = System.Diagnostics.Stopwatch.GetTimestamp();
                _lastPresentTicks.TryGetValue((ulong)t.Hwnd, out long lastTicks);
                double msSincePresent = lastTicks == 0 ? double.MaxValue
                    : (nowTicks - lastTicks) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
                _targetPresentCount.TryGetValue((ulong)t.Hwnd, out int tpc);
                _targetPresentCount[(ulong)t.Hwnd] = tpc + 1;
                if (tpc < 8 || msSincePresent > 100.0)
                {
                    // Wait for the GPU to FINISH presenting this drawable before committing the compositor
                    // transaction. wgpuSurfacePresent schedules the present asynchronously, so an immediate
                    // CATransaction.flush races it: sometimes the drawable is ready (frame shows), sometimes
                    // not (window stays blank until an unrelated relayout) — a random "empty until you resize"
                    // on static windows. Polling to GPU-idle first makes the composite deterministic. Only
                    // these rare isolated/startup frames pay the sync; animation frames skip the flush entirely.
                    unsafe { wgpuDevicePoll(_ctx.Device, WGPU_TRUE, null); }
                    Platform.NativePlatform.CommitPresent();
                }
                _lastPresentTicks[(ulong)t.Hwnd] = nowTicks;
                if (PresentedFrames == 1 || PresentedFrames % 60 == 0)
                {
                    Log($"presented frame {PresentedFrames} to HWND 0x{t.Hwnd:x} ({t.Width}x{t.Height})");
                    Log(_engine.DumpOps());
                }
                if (s_logPath != null && (PresentedFrames == 30 || PresentedFrames == 90 || PresentedFrames == 150))
                    VerifyOffscreen(EmbeddedContent.Compose(root), t, PresentedFrames);
            }

            wgpuTextureViewRelease(view);
            wgpuTextureRelease(surfaceTexture.texture);
        }

        // Read back the REAL swapchain texture (the exact presented pixels) and write a PNG. Unlike
        // VerifyOffscreen (a separate offscreen render), this proves what the surface actually shows —
        // including any drawable-size / scale mismatch between t.Width and the CAMetalLayer.
        private void DumpSurface(IntPtr texture, TargetSurface ts, string path)
        {
            try
            {
                int w = ts.Width, h = ts.Height;
                int bytesPerRow = (w * 4 + 255) & ~255;
                ulong size = (ulong)bytesPerRow * (ulong)h;
                IntPtr buf = _ctx!.CreateBuffer(size, WGPUBufferUsage.CopyDst | WGPUBufferUsage.MapRead);
                IntPtr enc = wgpuDeviceCreateCommandEncoder(_ctx.Device, IntPtr.Zero);
                var src = new WGPUTexelCopyTextureInfo { texture = texture, aspect = WGPUTextureAspect.All };
                var dst = new WGPUTexelCopyBufferInfo
                {
                    layout = new WGPUTexelCopyBufferLayout { offset = 0, bytesPerRow = (uint)bytesPerRow, rowsPerImage = (uint)h },
                    buffer = buf,
                };
                var ext = new WGPUExtent3D { width = (uint)w, height = (uint)h, depthOrArrayLayers = 1 };
                wgpuCommandEncoderCopyTextureToBuffer(enc, &src, &dst, &ext);
                IntPtr cmd = wgpuCommandEncoderFinish(enc, IntPtr.Zero);
                IntPtr* cmds = stackalloc IntPtr[1]; cmds[0] = cmd;
                wgpuQueueSubmit(_ctx.Queue, 1, cmds);

                byte[] padded = _ctx.MapRead(buf, size);
                var px = new byte[w * h * 4];
                for (int row = 0; row < h; row++)
                    Buffer.BlockCopy(padded, row * bytesPerRow, px, row * w * 4, w * 4);

                // Surface is typically BGRA; PngWriter expects RGBA — swap R/B when needed.
                bool bgra = ts.Format is WGPUTextureFormat.BGRA8Unorm or WGPUTextureFormat.BGRA8UnormSrgb;
                if (bgra)
                    for (int i = 0; i < px.Length; i += 4) { byte b0 = px[i]; px[i] = px[i + 2]; px[i + 2] = b0; }

                PngWriter.Write(path, px, w, h, maxWidth: 4000);
                Log($"WF_SURF_DUMP wrote real surface {w}x{h} (format={ts.Format}) to {path}");
            }
            catch (Exception ex) { Log($"WF_SURF_DUMP failed: {ex.Message}"); }
        }

        // One-time sanity render: composite the decoded tree off-screen and count how
        // many pixels differ from the clear colour, proving the broadened decode actually
        // draws content (vs. an empty frame when ops are skipped).
        private void VerifyOffscreen(SceneVisual root, MilTarget t, int frame)
        {
            try
            {
                Log(_engine.DumpState());
                Log($"root transform={(root.Transform.IsIdentity ? "I" : root.Transform.ToString())} drawables={CountDrawables(root)}");
                byte[] px = _renderer!.RenderToRgba(root, t.Width, t.Height, t.ClearColor, srgbOutput: true);
                byte cr = (byte)Math.Clamp(t.ClearColor.R * 255f, 0, 255);
                byte cg = (byte)Math.Clamp(t.ClearColor.G * 255f, 0, 255);
                byte cb = (byte)Math.Clamp(t.ClearColor.B * 255f, 0, 255);
                long nonBg = 0;
                for (int i = 0; i < px.Length; i += 4)
                    if (px[i] != cr || px[i + 1] != cg || px[i + 2] != cb) nonBg++;
                Log($"offscreen verify: {nonBg} of {(long)t.Width * t.Height} px differ from clear colour (content drew)");

                // Optional screenshot of the composed frame for demos/diagnostics.
                string? dump = Environment.GetEnvironmentVariable("WPF_WEBGPU_SINK_DUMP");
                if (dump != null)
                {
                    string numbered = System.IO.Path.Combine(
                        System.IO.Path.GetDirectoryName(dump) ?? ".",
                        System.IO.Path.GetFileNameWithoutExtension(dump) + "_" + frame.ToString("000") + System.IO.Path.GetExtension(dump));
                    PngWriter.Write(numbered, px, t.Width, t.Height);
                    Log($"wrote screenshot {numbered}");

                    // Also a 1:1 (un-downsampled) crop of a region so detail is visible.
                    int ox = Math.Clamp(int.TryParse(Environment.GetEnvironmentVariable("WPF_WEBGPU_CROP_X"), out int cx) ? cx : 0, 0, Math.Max(0, t.Width - 1));
                    int oy = Math.Clamp(int.TryParse(Environment.GetEnvironmentVariable("WPF_WEBGPU_CROP_Y"), out int cy) ? cy : 0, 0, Math.Max(0, t.Height - 1));
                    int cw = Math.Min(1000, t.Width - ox), ch = Math.Min(560, t.Height - oy);
                    var crop = new byte[cw * ch * 4];
                    for (int yy = 0; yy < ch; yy++)
                        Array.Copy(px, ((oy + yy) * t.Width + ox) * 4, crop, yy * cw * 4, cw * 4);
                    string cropPath = System.IO.Path.Combine(
                        System.IO.Path.GetDirectoryName(dump) ?? ".",
                        System.IO.Path.GetFileNameWithoutExtension(dump) + "_" + frame.ToString("000") + "_crop" + System.IO.Path.GetExtension(dump));
                    PngWriter.Write(cropPath, crop, cw, ch, maxWidth: 4000);
                    Log($"wrote crop {cropPath}");
                }
            }
            catch (Exception ex) { Log("offscreen verify failed: " + ex.Message); }
        }

        private static int CountDrawables(SceneVisual v)
        {
            int n = v.Content.Count;
            foreach (SceneVisual c in v.Children) n += CountDrawables(c);
            return n;
        }

        private static void Log(string message)
        {
            if (s_logPath is null) return;
            try { System.IO.File.AppendAllText(s_logPath, message + Environment.NewLine); }
            catch { /* diagnostics only */ }
        }

        private static void UnpremultiplyInPlace(byte[] px)
        {
            for (int i = 0; i < px.Length; i += 4)
            {
                byte a = px[i + 3];
                if (a > 0 && a < 255)
                {
                    px[i] = (byte)Math.Min(255, px[i] * 255 / a);
                    px[i + 1] = (byte)Math.Min(255, px[i + 1] * 255 / a);
                    px[i + 2] = (byte)Math.Min(255, px[i + 2] * 255 / a);
                }
            }
        }

// Browser async brush rasterization lives in WpfCompositionSink.Browser.cs (a
        // non-unsafe partial part: await is illegal inside this unsafe class declaration).

        // Default font for text-STRING glyph runs from embedded content (a real TrueType face so lowercase
        // renders; the built-in fallback is uppercase-only). Glyphs rasterize on the GPU at present time.
        private static Text.IFont LoadDefaultFont()
        {
            foreach (string p in new[] { "/System/Library/Fonts/Supplemental/Arial.ttf",
                                         "/System/Library/Fonts/HelveticaNeue.ttc", "/Library/Fonts/Arial.ttf",
                                         "/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf",
                                         // Browser (wasm): fonts live in the VFS at /fonts (main.js writes them
                                         // before Main). Without this the fallback is uppercase-only, so embedded
                                         // WinForms text renders as fragmented capitals.
                                         "/fonts/LiberationSans-Regular.ttf", "/fonts/DejaVuSans.ttf" })
                if (System.IO.File.Exists(p)) return new Text.TrueTypeFont(System.IO.File.ReadAllBytes(p));
            return new Text.BuiltinBitmapFont();
        }

        private void EnsureGpu()
        {
            if (_ctx is null)
            {
                if (s_logPath != null) WgpuContext.LogSink = Log;
                _ctx = WgpuContext.Create();

                // A real default font + shaper so text-STRING glyph runs (GlyphRunDraw with .Text) —
                // emitted by embedded non-WPF content like a WinForms control via EmbeddedContent —
                // shape and rasterize with actual glyphs. WPF's own text arrives as pre-shaped
                // glyph-INDEX runs with per-run fonts, so this default is only used for those string runs.
                _renderer = new WgpuSceneRenderer(_ctx, LoadDefaultFont(), new Text.SimpleTextShaper());
                if (s_logPath != null) WgpuSceneRenderer.DebugLog = Log;
                // Let the engine rasterize VisualBrush/DrawingBrush sources to straight-RGBA bitmaps
                // (rendered sRGB for display, then un-premultiplied since the image path re-premultiplies).
#if WGPU_BROWSER
                // Browser: the readback is Promise-based, so brush rasterization completes with
                // one frame of latency (kick the async render, retry delivers the bytes).
                _engine.VisualRasterizerKeyed = RasterizeBrushBrowser;
#else
                _engine.VisualRasterizer = (visual, w, h) =>
                {
                    byte[] px = _renderer!.RenderToRgba(visual, w, h, new RgbaColor(0, 0, 0, 0), srgbOutput: true);
                    UnpremultiplyInPlace(px);
                    return px;
                };
#endif
                Log($"WebGPU device created (0x{_ctx.Device:x}) {_ctx.AdapterDescription}");
            }
        }

        private TargetSurface EnsureSurface(uint targetHandle, MilTarget t)
        {
            // Android replaces the native window under a stable WPF handle every time the activity
            // stops and starts again (and hands it over asynchronously, so the FIRST frames of a new
            // window legitimately have none at all). Drop a surface built on a window that is no
            // longer the live one -- or that we never managed to build -- and try again from scratch;
            // rendering into the old ANativeWindow would draw into a dead buffer queue. Everywhere
            // else GetNativeWindow returns the handle itself, so neither branch can fire.
            IntPtr liveWindow = Platform.NativePlatform.GetNativeWindow((IntPtr)t.Hwnd);
            if (_surfaces.TryGetValue(targetHandle, out TargetSurface? stale)
                && (stale.Surface == IntPtr.Zero || stale.NativeWindow != liveWindow))
            {
                if (stale.Surface != IntPtr.Zero) wgpuSurfaceRelease(stale.Surface);
                _surfaces.Remove(targetHandle);
            }

            if (!_surfaces.TryGetValue(targetHandle, out TargetSurface? ts))
            {
                IntPtr surface = Platform.NativePlatform.CreateWindowSurface(_ctx!.Instance, (IntPtr)t.Hwnd);
                if (surface == IntPtr.Zero)
                {
                    // No live native window (Android, before surfaceCreated). Cache a placeholder so
                    // the target is known, but do NOT query capabilities or configure a null surface --
                    // wgpu-native panics on both. The check at the top of this method throws the
                    // placeholder away and retries as soon as a window shows up.
                    ts = new TargetSurface { Surface = IntPtr.Zero, Hwnd = (IntPtr)t.Hwnd, NativeWindow = IntPtr.Zero, Width = t.Width, Height = t.Height };
                    _surfaces[targetHandle] = ts;
                    return ts;
                }
                bool gl = _ctx!.AdapterDescription?.Contains("backend=OpenGL") == true;
                WGPUTextureFormat format = ChooseFormat(surface, _ctx!.Adapter, gl);
                // We render straight into the swapchain view. (An sRGB-swapchain + UNORM-view scheme to
                // keep gamma-space bytes verbatim needs SURFACE_VIEW_FORMATS, which the ANGLE/GL device
                // does NOT support — it panics. So on GL we composite in linear space instead, see
                // EnsureContext; the swapchain is then sRGB and RenderFormat == Format.)
                WGPUTextureFormat renderFormat = format;
                // Layered popups (per-pixel alpha) OR a window made non-opaque for a translucent Mica
                // backdrop both present through a transparent surface so the material behind shows through.
                bool transparent = t.IsLayered || !Platform.NativePlatform.IsWindowOpaque((IntPtr)t.Hwnd);
                ts = new TargetSurface { Surface = surface, Hwnd = (IntPtr)t.Hwnd, NativeWindow = liveWindow, Format = format, RenderFormat = renderFormat, Width = t.Width, Height = t.Height, Transparent = transparent };
                _surfaces[targetHandle] = ts;
                Configure(ts);
            }
            else
            {
                // Re-poll opacity every frame: WPF's Fluent theme enables the DWM Mica backdrop AFTER the
                // window (and this surface) already exist, so a surface first created opaque must flip to a
                // transparent (premultiplied-alpha) configuration once the backdrop turns on — otherwise the
                // opaque swapchain keeps hiding the Mica. Reconfigure on either a size or a transparency change.
                bool wantTransparent = t.IsLayered || !Platform.NativePlatform.IsWindowOpaque((IntPtr)t.Hwnd);
                if (ts.Width != t.Width || ts.Height != t.Height || ts.Transparent != wantTransparent)
                {
                    ts.Width = t.Width;
                    ts.Height = t.Height;
                    ts.Transparent = wantTransparent;
                    Configure(ts);
                }
            }
            return ts;
        }

        // sRGB -> plain-UNORM counterpart (view-compatible); other formats pass through unchanged.
        private static WGPUTextureFormat UnormOf(WGPUTextureFormat f) => f switch
        {
            WGPUTextureFormat.RGBA8UnormSrgb => WGPUTextureFormat.RGBA8Unorm,
            WGPUTextureFormat.BGRA8UnormSrgb => WGPUTextureFormat.BGRA8Unorm,
            _ => f,
        };

        private static WGPUTextureFormat ChooseFormat(IntPtr surface, IntPtr adapter, bool gl)
        {
            WGPUSurfaceCapabilities caps;
            if (wgpuSurfaceGetCapabilities(surface, adapter, &caps) != WGPUStatus.Success || caps.formatCount == 0)
                return WGPUTextureFormat.BGRA8Unorm;

            // Gamma-space compositing (default): the renderer already sRGB-encodes every colour at its
            // source and blends in gamma space, so prefer a plain UNORM surface that stores those encoded
            // values verbatim (an sRGB surface would gamma-encode AGAIN -> washed out). Linear mode: prefer
            // an sRGB surface so the one linear->sRGB encode happens on the display write. Avoid BGRA (swap).
            // Metal (macOS) surfaces advertise BGRA, not RGBA — so we must consider BOTH channel orders,
            // or we fall through to formats[0] which on Metal is often the sRGB variant and, in gamma
            // mode, double-encodes (colours too bright / washed). Gamma mode: pick any UNORM (non-sRGB)
            // format so the pre-encoded values store verbatim. Linear mode: pick an sRGB format so the one
            // linear->sRGB encode happens on store. Prefer RGBA over BGRA when both exist (no channel swap).
            bool gamma = WgpuSceneRenderer.s_gammaComposite;
            bool haveRgbaU = false, haveBgraU = false, haveRgbaS = false, haveBgraS = false;
            for (nuint i = 0; i < caps.formatCount; i++)
            {
                switch (caps.formats[i])
                {
                    case WGPUTextureFormat.RGBA8Unorm: haveRgbaU = true; break;
                    case WGPUTextureFormat.BGRA8Unorm: haveBgraU = true; break;
                    case WGPUTextureFormat.RGBA8UnormSrgb: haveRgbaS = true; break;
                    case WGPUTextureFormat.BGRA8UnormSrgb: haveBgraS = true; break;
                }
            }
            // Gamma mode stores pre-encoded values verbatim, so on backends that scan out a UNORM
            // swapchain faithfully (Metal) pick a UNORM surface. On OpenGL/ANGLE a UNORM swapchain is
            // presented too dark, so pick an sRGB surface even in gamma mode and render into it through a
            // UNORM view (RenderFormat) so the store is still verbatim. Linear mode always wants sRGB.
            bool wantUnormSwapchain = gamma;
            WGPUTextureFormat chosen;
            if (wantUnormSwapchain)
                chosen = haveRgbaU ? WGPUTextureFormat.RGBA8Unorm
                       : haveBgraU ? WGPUTextureFormat.BGRA8Unorm
                       : haveRgbaS ? WGPUTextureFormat.RGBA8UnormSrgb
                       : haveBgraS ? WGPUTextureFormat.BGRA8UnormSrgb : caps.formats[0];
            else
                chosen = haveRgbaS ? WGPUTextureFormat.RGBA8UnormSrgb
                       : haveBgraS ? WGPUTextureFormat.BGRA8UnormSrgb
                       : haveRgbaU ? WGPUTextureFormat.RGBA8Unorm
                       : haveBgraU ? WGPUTextureFormat.BGRA8Unorm : caps.formats[0];
            Log($"ChooseFormat gamma={gamma} gl={gl} formats[0]={caps.formats[0]} chosen={chosen}");
            wgpuSurfaceCapabilitiesFreeMembers(caps);
            return chosen;
        }

        // Present mode: Fifo (default) is vsync-locked to the display refresh -- a frame that
        // narrowly misses a vsync deadline slips to the next interval (so a 100Hz display can
        // read ~88fps). WPF_WEBGPU_PRESENT=mailbox presents the newest frame without blocking on
        // vsync (no tearing), which recovers those missed intervals; =immediate uncaps entirely
        // (may tear). Backends that don't support the requested mode fall back to Fifo.
        private static readonly WGPUPresentMode s_presentMode =
            Environment.GetEnvironmentVariable("WPF_WEBGPU_PRESENT")?.ToLowerInvariant() switch
            {
                "mailbox" => WGPUPresentMode.Mailbox,
                "immediate" => WGPUPresentMode.Immediate,
                "fiforelaxed" => WGPUPresentMode.FifoRelaxed,
                _ => WGPUPresentMode.Fifo,
            };

        private void Configure(TargetSurface ts)
        {
            WGPUPresentMode mode = SurfaceSupportsPresentMode(ts.Surface, s_presentMode) ? s_presentMode : WGPUPresentMode.Fifo;
            // Layered popups composite over the content behind them. Pick any non-opaque alpha mode the
            // surface advertises so wgpu sets the CAMetalLayer non-opaque (an Opaque mode would force it
            // back to opaque and the transparent clear would show as black). wgpu-native's Metal backend
            // reports Opaque + Unpremultiplied (not Premultiplied), so prefer whichever transparent mode
            // is available; CoreAnimation composites the CAMetalLayer's drawable regardless of the label.
            WGPUCompositeAlphaMode alpha = WGPUCompositeAlphaMode.Auto;
            if (ts.Transparent)
            {
                if (SurfaceSupportsAlphaMode(ts.Surface, WGPUCompositeAlphaMode.Premultiplied)) alpha = WGPUCompositeAlphaMode.Premultiplied;
                else if (SurfaceSupportsAlphaMode(ts.Surface, WGPUCompositeAlphaMode.Unpremultiplied)) alpha = WGPUCompositeAlphaMode.Unpremultiplied;
                else if (SurfaceSupportsAlphaMode(ts.Surface, WGPUCompositeAlphaMode.Inherit)) alpha = WGPUCompositeAlphaMode.Inherit;
            }
            // When we render through a different-format view than the swapchain (OpenGL gamma path:
            // UNORM view over an sRGB swapchain), that view format must be declared in viewFormats.
            WGPUTextureFormat renderFmt = ts.RenderFormat;
            var config = new WGPUSurfaceConfiguration
            {
                device = _ctx!.Device,
                format = ts.Format,
                usage = WGPUTextureUsage.RenderAttachment | (s_surfDump != null ? WGPUTextureUsage.CopySrc : 0),
                width = (uint)ts.Width,
                height = (uint)ts.Height,
                alphaMode = alpha,
                presentMode = mode,
                viewFormatCount = renderFmt != ts.Format ? (nuint)1 : 0,
                viewFormats = renderFmt != ts.Format ? &renderFmt : null,
            };
            Log($"CONFIGURE surface {ts.Width}x{ts.Height} present={mode} transparent={ts.Transparent} alpha={alpha}");
            wgpuSurfaceConfigure(ts.Surface, &config);

            // Keep the native layer's contents/backing scale in step with the surface's device-pixel
            // size. Configure runs on creation and whenever the pixel size changes -- including when a
            // window is dragged to a different-DPI display (same points, new pixel size) -- so this is
            // where the CAMetalLayer contentsScale must be refreshed, else the new drawable would be
            // mapped onto the view at the old scale.
            Platform.NativePlatform.UpdateContentsScale(ts.Hwnd);
        }

        // Fifo is guaranteed by the spec; any other requested mode is honoured only if the surface
        // reports it in its capabilities (else we keep Fifo).
        private bool SurfaceSupportsPresentMode(IntPtr surface, WGPUPresentMode mode)
        {
            if (mode == WGPUPresentMode.Fifo) return true;
            var caps = new WGPUSurfaceCapabilities();
            wgpuSurfaceGetCapabilities(surface, _ctx!.Adapter, &caps);
            bool found = false;
            for (nuint i = 0; i < caps.presentModeCount; i++)
                if (caps.presentModes[i] == mode) { found = true; break; }
            wgpuSurfaceCapabilitiesFreeMembers(caps);
            return found;
        }

        private bool SurfaceSupportsAlphaMode(IntPtr surface, WGPUCompositeAlphaMode mode)
        {
            var caps = new WGPUSurfaceCapabilities();
            wgpuSurfaceGetCapabilities(surface, _ctx!.Adapter, &caps);
            bool found = false;
            for (nuint i = 0; i < caps.alphaModeCount; i++)
                if (caps.alphaModes[i] == mode) { found = true; break; }
            wgpuSurfaceCapabilitiesFreeMembers(caps);
            return found;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            foreach (TargetSurface ts in _surfaces.Values)
            {
                if (ts.Surface != IntPtr.Zero) wgpuSurfaceRelease(ts.Surface);
            }
            _surfaces.Clear();
            _renderer?.Dispose();
            _ctx?.Dispose();
        }

        private sealed class TargetSurface
        {
            public IntPtr Surface;
            /// <summary>The native window/view handle (NSView* on macOS) backing this surface, kept so
            /// the CAMetalLayer contentsScale can be re-synced when the window's DPI changes.</summary>
            public IntPtr Hwnd;
            /// <summary>The native window this surface was actually built on. Equals <see cref="Hwnd"/>
            /// everywhere except Android, where the ANativeWindow behind a WPF handle is destroyed and
            /// replaced across activity stop/start -- comparing the two is how EnsureSurface notices.</summary>
            public IntPtr NativeWindow;
            /// <summary>The swapchain format (what wgpu presents). May be sRGB even in gamma-space mode
            /// on the OpenGL/ANGLE backend, where a plain-UNORM swapchain scans out too dark.</summary>
            public WGPUTextureFormat Format;
            /// <summary>The format we RENDER through (the surface texture view + pipelines). Equals
            /// <see cref="Format"/> except on the OpenGL gamma-space path, where it is the UNORM
            /// counterpart of an sRGB swapchain so pre-encoded gamma bytes store verbatim (no re-encode).</summary>
            public WGPUTextureFormat RenderFormat;
            public int Width;
            public int Height;
            /// <summary>Consecutive acquires that produced no drawable (occluded/bad status).</summary>
            public int NullAcquires;
            /// <summary>Layered popup target: configure with premultiplied alpha + clear transparent so
            /// the popup's shadow/rounded corners composite over the content behind the window.</summary>
            public bool Transparent;
        }
    }
}
