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
    internal sealed unsafe class WpfCompositionSink : IDisposable
    {
        private readonly MilcoreEngine _engine = new();
        private readonly Dictionary<uint, TargetSurface> _surfaces = new();
        private WgpuContext? _ctx;
        private WgpuSceneRenderer? _renderer;
        private uint _nextHandle;
        private string _targetsSig = "";
        private bool _loggedLayered;
        private int _layeredFrames;
        private long _perfRealizeTicks, _perfRenderTicks, _perfRenderOnlyTicks, _perfPresentTicks;
        private int _perfFrames;
        private bool _disposed;

        // Optional diagnostics: when WPF_WEBGPU_SINK_LOG names a file, the sink appends
        // lifecycle/present lines there. This is how a hosted WPF process proves its
        // frames were composited through WebGPU (rather than native milcore).
        private static readonly string? s_logPath =
            Environment.GetEnvironmentVariable("WPF_WEBGPU_SINK_LOG");

        public WpfCompositionSink()
        {
            // Resolve WPF glyph runs' native IDWriteFont pointers to real fonts so text renders
            // (via DirectWrite -> local font file -> TrueTypeFont). Set WPF_WEBGPU_TEXT=0 to disable.
            if (Environment.GetEnvironmentVariable("WPF_WEBGPU_TEXT") != "0")
            {
                var fonts = new Text.DWriteFontResolver();
                _engine.FontResolver = fonts.Resolve;
            }
            Log($"WpfCompositionSink created (pid {Environment.ProcessId})");
        }

        /// <summary>Total swap-chain textures successfully acquired (diagnostics/tests).</summary>
        public int AcquiredFrames { get; private set; }

        /// <summary>Total frames successfully presented (diagnostics/tests).</summary>
        public int PresentedFrames { get; private set; }

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
        {
            _engine.Release(handle);
            return true;
        }

        public void SendCommand(int channelId, byte[] data, bool sendInSeparateBatch)
            => _engine.SubmitCommand(data);

        public void SendBitmap(int channelId, uint handle, IntPtr bitmapSource)
        {
            if (BitmapReader.TryRead(bitmapSource, out byte[] rgba, out int w, out int h))
            {
                _engine.SetBitmap(handle, rgba, w, h);
                Log($"bitmap 0x{handle:x}: {w}x{h}");
            }
            else
            {
                Log($"bitmap 0x{handle:x}: unsupported/failed");
            }
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

        // ---- presentation ------------------------------------------------------------

        /// <summary>Render and present every target that has a window and a root visual.</summary>
        public void RenderTargets()
        {
            EnsureGpu();         // ensure the renderer (and VisualRasterizer) exist before Realize
            _renderer!.BeginFrame();
            WgpuSceneRenderer.PerfReset();
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            _engine.Realize();   // re-parse content with the current resource state
            _perfRealizeTicks += System.Diagnostics.Stopwatch.GetTimestamp() - t0;
            string sig = "";
            foreach (KeyValuePair<uint, MilTarget> tk in _engine.Targets)
                sig += $"0x{tk.Key:x}:{tk.Value.Width}x{tk.Value.Height}:{tk.Value.Transparency};";
            if (sig != _targetsSig)
            {
                _targetsSig = sig;
                foreach (KeyValuePair<uint, MilTarget> tk in _engine.Targets)
                    Log($"target 0x{tk.Key:x}: hwnd=0x{tk.Value.Hwnd:x} root={tk.Value.RootHandle} {tk.Value.Width}x{tk.Value.Height} layered={tk.Value.IsLayered} (transp=0x{tk.Value.Transparency:x})");
            }
            foreach (KeyValuePair<uint, MilTarget> kv in _engine.Targets)
            {
                MilTarget t = kv.Value;
                if (t.Hwnd == 0 || t.RootHandle == 0 || t.Width <= 0 || t.Height <= 0) continue;
                SceneVisual? root = _engine.VisualByHandle(t.RootHandle);
                if (root is null) continue;

                EnsureGpu();
                if (t.IsLayered)
                {
                    // ComboBox/Menu/ToolTip popups live in WS_EX_LAYERED per-pixel-alpha windows;
                    // present those via UpdateLayeredWindow from a premultiplied off-screen render.
                    PresentLayered(root, t);
                    continue;
                }
                TargetSurface ts = EnsureSurface(kv.Key, t);
                long t1 = System.Diagnostics.Stopwatch.GetTimestamp();
                Present(ts, root, t);
                _perfRenderTicks += System.Diagnostics.Stopwatch.GetTimestamp() - t1;
            }

            _renderer!.EndFrame();

            if (++_perfFrames >= 60 && s_logPath != null)
            {
                double ms(long ticks) => ticks * 1000.0 / System.Diagnostics.Stopwatch.Frequency / _perfFrames;
                double msr(long ticks) => ticks * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
                Log($"PERF: collect={msr(WgpuSceneRenderer.PerfCollectTicks):0.0}ms encode={msr(WgpuSceneRenderer.PerfEncodeTicks):0.0}ms submit={msr(WgpuSceneRenderer.PerfSubmitTicks):0.0}ms (last frame)");
                Log($"PERF/frame: realize={ms(_perfRealizeTicks):0.0}ms render={ms(_perfRenderOnlyTicks):0.0}ms present={ms(_perfPresentTicks):0.0}ms | " +
                    $"rasterized={WgpuSceneRenderer.PerfCoverage} textures={WgpuSceneRenderer.PerfTextures} bindgroups={WgpuSceneRenderer.PerfBindGroups} layers={WgpuSceneRenderer.PerfLayers} readbacks={WgpuSceneRenderer.PerfReadbacks}");
                _perfFrames = 0; _perfRealizeTicks = 0; _perfRenderTicks = 0; _perfRenderOnlyTicks = 0; _perfPresentTicks = 0;
            }
        }

        // Render a layered popup off-screen with a transparent clear (so its shadow/rounded
        // corners keep real alpha) and hand the premultiplied bitmap to the OS compositor.
        private void PresentLayered(SceneVisual root, MilTarget t)
        {
            byte[] rgba = _renderer!.RenderToRgba(root, t.Width, t.Height, new RgbaColor(0, 0, 0, 0), srgbOutput: true);
            LayeredWindow.Update((IntPtr)t.Hwnd, rgba, t.Width, t.Height);
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

        private void Present(TargetSurface ts, SceneVisual root, MilTarget t)
        {
            WGPUSurfaceTexture surfaceTexture;
            wgpuSurfaceGetCurrentTexture(ts.Surface, &surfaceTexture);

            if (surfaceTexture.status == WGPUSurfaceGetCurrentTextureStatus.Outdated ||
                surfaceTexture.status == WGPUSurfaceGetCurrentTextureStatus.Timeout)
            {
                Configure(ts);                 // window resized/lost; reconfigure and skip this frame
                return;
            }
            if (surfaceTexture.status != WGPUSurfaceGetCurrentTextureStatus.SuccessOptimal &&
                surfaceTexture.status != WGPUSurfaceGetCurrentTextureStatus.SuccessSuboptimal)
            {
                return;
            }
            AcquiredFrames++;

            IntPtr view = wgpuTextureCreateView(surfaceTexture.texture, IntPtr.Zero);
            long ta = System.Diagnostics.Stopwatch.GetTimestamp();
            _renderer!.RenderSceneToView(root, view, ts.Format, t.Width, t.Height, t.ClearColor);
            _perfRenderOnlyTicks += System.Diagnostics.Stopwatch.GetTimestamp() - ta;

            long tp = System.Diagnostics.Stopwatch.GetTimestamp();
            WGPUStatus pres = wgpuSurfacePresent(ts.Surface);
            _perfPresentTicks += System.Diagnostics.Stopwatch.GetTimestamp() - tp;
            if (pres == WGPUStatus.Success)
            {
                PresentedFrames++;
                if (PresentedFrames == 1 || PresentedFrames % 60 == 0)
                {
                    Log($"presented frame {PresentedFrames} to HWND 0x{t.Hwnd:x} ({t.Width}x{t.Height})");
                    Log(_engine.DumpOps());
                }
                if (s_logPath != null && (PresentedFrames == 30 || PresentedFrames == 90 || PresentedFrames == 150))
                    VerifyOffscreen(root, t, PresentedFrames);
            }

            wgpuTextureViewRelease(view);
            wgpuTextureRelease(surfaceTexture.texture);
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

        private void EnsureGpu()
        {
            if (_ctx is null)
            {
                _ctx = WgpuContext.Create();
                _renderer = new WgpuSceneRenderer(_ctx);
                if (s_logPath != null) WgpuSceneRenderer.DebugLog = Log;
                // Let the engine rasterize VisualBrush/DrawingBrush sources to straight-RGBA bitmaps
                // (rendered sRGB for display, then un-premultiplied since the image path re-premultiplies).
                _engine.VisualRasterizer = (visual, w, h) =>
                {
                    byte[] px = _renderer!.RenderToRgba(visual, w, h, new RgbaColor(0, 0, 0, 0), srgbOutput: true);
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
                    return px;
                };
                Log($"WebGPU device created (0x{_ctx.Device:x}) {_ctx.AdapterDescription}");
            }
        }

        private TargetSurface EnsureSurface(uint targetHandle, MilTarget t)
        {
            if (!_surfaces.TryGetValue(targetHandle, out TargetSurface? ts))
            {
                IntPtr surface = CreateHwndSurface((IntPtr)t.Hwnd);
                WGPUTextureFormat format = ChooseFormat(surface, _ctx!.Adapter);
                ts = new TargetSurface { Surface = surface, Format = format, Width = t.Width, Height = t.Height };
                _surfaces[targetHandle] = ts;
                Configure(ts);
            }
            else if (ts.Width != t.Width || ts.Height != t.Height)
            {
                ts.Width = t.Width;
                ts.Height = t.Height;
                Configure(ts);
            }
            return ts;
        }

        private IntPtr CreateHwndSurface(IntPtr hwnd)
        {
            var hwndSource = new WGPUSurfaceSourceWindowsHWND
            {
                chain = new WGPUChainedStruct { next = null, sType = WGPUSType_SurfaceSourceWindowsHWND },
                hinstance = (void*)GetModuleHandleW(null),
                hwnd = (void*)hwnd,
            };
            var desc = new WGPUSurfaceDescriptor { nextInChain = (WGPUChainedStruct*)&hwndSource };
            return wgpuInstanceCreateSurface(_ctx!.Instance, &desc);
        }

        private static WGPUTextureFormat ChooseFormat(IntPtr surface, IntPtr adapter)
        {
            WGPUSurfaceCapabilities caps;
            if (wgpuSurfaceGetCapabilities(surface, adapter, &caps) != WGPUStatus.Success || caps.formatCount == 0)
                return WGPUTextureFormat.BGRA8Unorm;

            // The renderer outputs RGBA-order, linear scRGB colours. Prefer an RGBA *sRGB* surface
            // so the linear->sRGB gamma encode happens once on the display write (a plain UNORM
            // surface would show WPF's linear colours too dark). Avoid BGRA formats (channel swap).
            WGPUTextureFormat chosen = caps.formats[0];
            bool haveSrgb = false, haveRgba = false;
            for (nuint i = 0; i < caps.formatCount; i++)
            {
                if (caps.formats[i] == WGPUTextureFormat.RGBA8UnormSrgb) haveSrgb = true;
                if (caps.formats[i] == WGPUTextureFormat.RGBA8Unorm) haveRgba = true;
            }
            if (haveSrgb) chosen = WGPUTextureFormat.RGBA8UnormSrgb;
            else if (haveRgba) chosen = WGPUTextureFormat.RGBA8Unorm;
            wgpuSurfaceCapabilitiesFreeMembers(caps);
            return chosen;
        }

        private void Configure(TargetSurface ts)
        {
            var config = new WGPUSurfaceConfiguration
            {
                device = _ctx!.Device,
                format = ts.Format,
                usage = WGPUTextureUsage.RenderAttachment,
                width = (uint)ts.Width,
                height = (uint)ts.Height,
                alphaMode = WGPUCompositeAlphaMode.Auto,
                presentMode = WGPUPresentMode.Fifo,
            };
            wgpuSurfaceConfigure(ts.Surface, &config);
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
            public WGPUTextureFormat Format;
            public int Width;
            public int Height;
        }

        [DllImport("kernel32.dll", EntryPoint = "GetModuleHandleW", CharSet = CharSet.Unicode)]
        private static extern IntPtr GetModuleHandleW(string? lpModuleName);
    }
}
