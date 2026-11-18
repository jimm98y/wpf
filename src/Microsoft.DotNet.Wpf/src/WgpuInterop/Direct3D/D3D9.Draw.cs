// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// The Direct3D 9 device's drawing: DrawPrimitive, DrawIndexedPrimitive and their UP forms, and Clear,
// on WebGPU. Draws are recorded into one render pass per set of targets and submitted at EndScene,
// Present, or before anything reads or writes a surface (D3D9 orders them all). The draw's uniforms
// -- the shader constants and the device state the fixed-function pipeline and the epilogue read --
// and a UP draw's vertices go into per-submission arenas, written to the GPU once, at the submission.
//
// D3D9's rules that WebGPU has no state for are in the shaders: pixel centres on integer
// coordinates (the half-pixel offset), the alpha test, fog, pre-transformed vertices; triangle fans,
// wireframe and point fill become index lists.
//

using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using static Microsoft.Wpf.Interop.WebGpu.Wgpu;

namespace Microsoft.Wpf.Interop.WebGpu.Direct3D
{
    internal sealed unsafe partial class Direct3DDevice9
    {
        private static void BuildDraw(Vtable vt)
        {
            vt.Set(Vtbl.IDirect3DDevice9Ex.EndScene, (delegate* unmanaged<IntPtr, int>)&EndScene);
            vt.Set(Vtbl.IDirect3DDevice9Ex.Present, (delegate* unmanaged<IntPtr, void*, void*, IntPtr, void*, int>)&PresentFlushing);
            vt.Set(Vtbl.IDirect3DDevice9Ex.PresentEx, (delegate* unmanaged<IntPtr, void*, void*, IntPtr, void*, uint, int>)&PresentExFlushing);
            vt.Set(Vtbl.IDirect3DDevice9Ex.Clear, (delegate* unmanaged<IntPtr, uint, D3DRECT9*, uint, uint, float, uint, int>)&ClearTargets);
            vt.Set(Vtbl.IDirect3DDevice9Ex.DrawPrimitive, (delegate* unmanaged<IntPtr, int, uint, uint, int>)&DrawPrimitive);
            vt.Set(Vtbl.IDirect3DDevice9Ex.DrawIndexedPrimitive, (delegate* unmanaged<IntPtr, int, int, uint, uint, uint, uint, int>)&DrawIndexedPrimitive);
            vt.Set(Vtbl.IDirect3DDevice9Ex.DrawPrimitiveUP, (delegate* unmanaged<IntPtr, int, uint, void*, uint, int>)&DrawPrimitiveUP);
            vt.Set(Vtbl.IDirect3DDevice9Ex.DrawIndexedPrimitiveUP, NativeTwins.Register(
                (IntPtr)(delegate* unmanaged<IntPtr, int, uint, uint, uint, void*, int, void*, uint, int>)&DrawIndexedPrimitiveUP,
                (Func<IntPtr, int, uint, uint, uint, IntPtr, int, IntPtr, uint, int>)((p0, p1, p2, p3, p4, p5, p6, p7, p8) => DrawIndexedPrimitiveUPImpl(p0, p1, p2, p3, p4, (void*)p5, p6, (void*)p7, p8))));
        }

        /// <summary>Draws issued and skipped (a shader not translated, a pipeline WebGPU refused), for tests.</summary>
        public int DrawsIssued, DrawsSkipped;

        public readonly object DrawLock = new();

        [UnmanagedCallersOnly]
        private static int EndScene(IntPtr self)
        {
            Dev(self)?.FlushDraws();
            return HResult.S_OK;
        }

        [UnmanagedCallersOnly]
        private static int PresentFlushing(IntPtr self, void* src, void* dst, IntPtr wnd, void* dirty)
        {
            Dev(self)?.FlushDraws();
            return DeviceStatus(self);
        }

        [UnmanagedCallersOnly]
        private static int PresentExFlushing(IntPtr self, void* src, void* dst, IntPtr wnd, void* dirty, uint flags)
        {
            Dev(self)?.FlushDraws();
            return DeviceStatus(self);
        }

        // ---- the batch: one encoder, a pass per set of targets ------------------------------------

        private IntPtr _encoder, _pass;
        /// <summary>The open batch's number (buffers note the batch that read them).</summary>
        public long Batch = 1;
        private readonly IntPtr[] _passColor = new IntPtr[4];
        private IntPtr _passDepth;
        private uint _passW, _passH;
        private readonly List<IntPtr> _retired = new();
        private readonly List<IntPtr> _batchViews = new();
        private readonly Dictionary<(IntPtr, uint, int), IntPtr> _views = new();
        private readonly HashSet<D3DTexture2D> _drawnTextures = new();

        private const int ArenaSize = 4 << 20;
        private readonly byte[] _uniformCpu = new byte[ArenaSize], _vertexCpu = new byte[ArenaSize];
        private int _uniformAt, _vertexAt;
        private IntPtr _uniformGpu, _vertexGpu;
        private int _vsConstAt = -1, _psConstAt = -1, _miscAt = -1, _vsConstVer, _psConstVer, _miscVer;
        private (uint, uint) _miscTarget;

        public void Retire(IntPtr buffer) { lock (DrawLock) _retired.Add(buffer); }

        private IntPtr Encoder()
        {
            if (_encoder == IntPtr.Zero) _encoder = wgpuDeviceCreateCommandEncoder(Device11.Gpu.Device, IntPtr.Zero);
            return _encoder;
        }

        private void EndPass()
        {
            if (_pass == IntPtr.Zero) return;
            wgpuRenderPassEncoderEnd(_pass);
            wgpuRenderPassEncoderRelease(_pass);
            _pass = IntPtr.Zero;
            Array.Clear(_passColor);
            _passDepth = IntPtr.Zero;
        }

        /// <summary>Submits what the device recorded: the arenas' bytes, then the draws.</summary>
        public void FlushDraws()
        {
            lock (DrawLock)
            {
                EndPass();
                var gpu = Device11.Gpu;
                if (_encoder != IntPtr.Zero)
                {
                    if (_uniformAt > 0)
                        fixed (byte* p = _uniformCpu) wgpuQueueWriteBuffer(gpu.Queue, _uniformGpu, 0, p, (nuint)_uniformAt);
                    if (_vertexAt > 0)
                        fixed (byte* p = _vertexCpu) wgpuQueueWriteBuffer(gpu.Queue, _vertexGpu, 0, p, (nuint)((_vertexAt + 3) & ~3));
                    IntPtr cmd = wgpuCommandEncoderFinish(_encoder, IntPtr.Zero);
                    wgpuCommandEncoderRelease(_encoder);
                    _encoder = IntPtr.Zero;
                    wgpuQueueSubmit(gpu.Queue, 1, &cmd);
                    wgpuCommandBufferRelease(cmd);
                }
                foreach (IntPtr b in _retired) wgpuBufferRelease(b);
                _retired.Clear();
                foreach (IntPtr v in _batchViews) wgpuTextureViewRelease(v);
                _batchViews.Clear();
                _views.Clear();
                foreach (D3DTexture2D t in _drawnTextures) System.Threading.Interlocked.Increment(ref t.Version);
                _drawnTextures.Clear();
                _uniformAt = _vertexAt = 0;
                _vsConstAt = _psConstAt = _miscAt = -1;
                Batch++;
            }
        }

        private void ReleaseDrawResources()
        {
            try { FlushDraws(); } catch (Exception e) { D3DLog.Error("D3D9 final flush", e); }
            if (_uniformGpu != IntPtr.Zero) wgpuBufferRelease(_uniformGpu);
            if (_vertexGpu != IntPtr.Zero) wgpuBufferRelease(_vertexGpu);
            _uniformGpu = _vertexGpu = IntPtr.Zero;
            foreach (var p in _pipelines.Values) if (p != IntPtr.Zero) wgpuRenderPipelineRelease(p);
            foreach (var m in _modules.Values) if (m != IntPtr.Zero) wgpuShaderModuleRelease(m);
            foreach (var (bgl, pl) in _layouts.Values) { wgpuBindGroupLayoutRelease(bgl); wgpuPipelineLayoutRelease(pl); }
            foreach (var s in _samplers.Values) wgpuSamplerRelease(s);
            foreach (var v in _dummies.Values) wgpuTextureViewRelease(v);
            _pipelines.Clear(); _modules.Clear(); _layouts.Clear(); _samplers.Clear(); _dummies.Clear();
            foreach (var d in _fvfDecls.Values) d.InternalRelease();
            _fvfDecls.Clear();
            State.Reset();
            for (int i = 0; i < 4; i++) D3D9State.SetObject(ref Targets[i], null);
            D3D9State.SetObject(ref DepthStencil, null);
            ReleaseSwapChain();
        }

        /// <summary>A view of a surface's level (a render target or depth attachment), held for the batch.</summary>
        private IntPtr TargetView(D3DTexture2D t, uint level, uint layer)
        {
            if (_views.TryGetValue((t.Gpu, level | layer << 16, 1), out IntPtr v)) return v;
            v = D3DView.CreateTextureView(t.Gpu, t.Format, WGPUTextureViewDimension._2D, level, 1, layer, 1);
            _views[(t.Gpu, level | layer << 16, 1)] = v;
            _batchViews.Add(v);
            return v;
        }

        /// <summary>A view of all of a texture's levels, to sample.</summary>
        private IntPtr SampleView(D3DTexture2D t)
        {
            if (_views.TryGetValue((t.Gpu, 0, 2), out IntPtr v)) return v;
            bool cube = (t.Desc.MiscFlags & D3D11_RESOURCE_MISC.TEXTURECUBE) != 0;
            var dim = t.Kind == 3 ? WGPUTextureViewDimension._3D : cube ? WGPUTextureViewDimension.Cube : WGPUTextureViewDimension._2D;
            v = D3DView.CreateTextureView(t.Gpu, t.Format, dim, 0, t.MipLevels, 0, cube ? 6u : 1u);
            _views[(t.Gpu, 0, 2)] = v;
            _batchViews.Add(v);
            return v;
        }

        private int ArenaAlloc(ReadOnlySpan<byte> data)
        {
            if (_uniformGpu == IntPtr.Zero) _uniformGpu = Device11.Gpu.CreateBuffer(ArenaSize, WGPUBufferUsage.Uniform | WGPUBufferUsage.CopyDst);
            int at = _uniformAt;
            data.CopyTo(_uniformCpu.AsSpan(at));
            _uniformAt = (at + data.Length + 255) & ~255;
            return at;
        }

        private int VertexAlloc(ReadOnlySpan<byte> data)
        {
            if (_vertexGpu == IntPtr.Zero) _vertexGpu = Device11.Gpu.CreateBuffer(ArenaSize, WGPUBufferUsage.Vertex | WGPUBufferUsage.Index | WGPUBufferUsage.CopyDst);
            int at = _vertexAt;
            data.CopyTo(_vertexCpu.AsSpan(at));
            _vertexAt = (at + data.Length + 3) & ~3;
            return at;
        }

        /// <summary>Bytes in a buffer of their own, for one draw (more than an arena holds); gone at the submission.</summary>
        private IntPtr OwnBuffer(ReadOnlySpan<byte> data)
        {
            IntPtr b = Device11.Gpu.CreateBufferMapped(data, WGPUBufferUsage.Vertex | WGPUBufferUsage.Index | WGPUBufferUsage.CopyDst);
            _retired.Add(b);
            return b;
        }

        // ---- the targets --------------------------------------------------------------------------

        private readonly struct TargetSet
        {
            public readonly Direct3DSurface9?[] Color;
            public readonly Direct3DSurface9? Depth;
            public readonly uint Width, Height;
            public TargetSet(Direct3DSurface9?[] c, Direct3DSurface9? d, uint w, uint h) { Color = c; Depth = d; Width = w; Height = h; }
        }

        /// <summary>The bound render targets (GPU surfaces; the depth surface only when it is their size).</summary>
        private TargetSet? Bound()
        {
            var rt0 = Targets[0];
            if (rt0?.Gpu == null) return null;
            var color = new Direct3DSurface9?[4];
            for (int i = 0; i < 4; i++)
                if (Targets[i] is { Gpu: not null } t && t.Width == rt0.Width && t.Height == rt0.Height) color[i] = t;
            var ds = DepthStencil;
            if (ds?.Gpu == null) ds = null;
            else if (ds.Width != rt0.Width || ds.Height != rt0.Height)
            {
                D3DLog.Once("d3d9dssize", $"a {ds.Width}x{ds.Height} depth surface with a {rt0.Width}x{rt0.Height} render target is not drawn with (WebGPU attachments are one size)");
                ds = null;
            }
            return new TargetSet(color, ds, rt0.Width, rt0.Height);
        }

        /// <summary>The pass for the targets, beginning one when they changed (or to clear on load).</summary>
        private bool EnsurePass(TargetSet ts, WGPUColor? clearColor = null, float? clearDepth = null, uint? clearStencil = null)
        {
            var views = new IntPtr[4];
            for (int i = 0; i < 4; i++) if (ts.Color[i] is { } s) views[i] = TargetView(s.Gpu!, s.Level, s.Layer);
            IntPtr dv = ts.Depth != null ? TargetView(ts.Depth.Gpu!, ts.Depth.Level, ts.Depth.Layer) : IntPtr.Zero;
            bool clearing = clearColor != null || clearDepth != null || clearStencil != null;
            if (_pass != IntPtr.Zero && !clearing && dv == _passDepth && views.AsSpan().SequenceEqual(_passColor)) return true;
            EndPass();
            int count = 0;
            for (int i = 0; i < 4; i++) if (views[i] != IntPtr.Zero) count = i + 1;
            var atts = stackalloc WGPURenderPassColorAttachment[4];
            for (int i = 0; i < count; i++)
            {
                atts[i] = new WGPURenderPassColorAttachment { view = views[i], depthSlice = 0xFFFFFFFF, storeOp = views[i] != IntPtr.Zero ? WGPUStoreOp.Store : 0 };
                if (views[i] == IntPtr.Zero) continue;
                if (clearColor is { } cc) { atts[i].loadOp = WGPULoadOp.Clear; atts[i].clearValue = cc; }
                else atts[i].loadOp = WGPULoadOp.Load;
                _drawnTextures.Add(ts.Color[i]!.Gpu!);
            }
            var ds = new WGPURenderPassDepthStencilAttachment { view = dv };
            if (ts.Depth != null)
            {
                ds.depthLoadOp = clearDepth != null ? WGPULoadOp.Clear : WGPULoadOp.Load;
                ds.depthStoreOp = WGPUStoreOp.Store;
                ds.depthClearValue = clearDepth ?? 1f;
                if (Formats.HasStencil(ts.Depth.Gpu!.Format))
                {
                    ds.stencilLoadOp = clearStencil != null ? WGPULoadOp.Clear : WGPULoadOp.Load;
                    ds.stencilStoreOp = WGPUStoreOp.Store;
                    ds.stencilClearValue = clearStencil ?? 0;
                }
                _drawnTextures.Add(ts.Depth.Gpu!);
            }
            var desc = new WGPURenderPassDescriptor
            {
                colorAttachmentCount = (nuint)count, colorAttachments = atts,
                depthStencilAttachment = ts.Depth != null ? (IntPtr)(&ds) : IntPtr.Zero,
            };
            _pass = wgpuCommandEncoderBeginRenderPass(Encoder(), &desc);
            views.AsSpan().CopyTo(_passColor);
            _passDepth = dv;
            _passW = ts.Width; _passH = ts.Height;
            return _pass != IntPtr.Zero;
        }

        /// <summary>SetRenderTarget: the target, and for target 0 the viewport and scissor rectangle reset to
        /// its size (as D3D9 does).</summary>
        public int SetTarget(uint index, Direct3DSurface9? s)
        {
            if (index >= 4) return Invalid;
            if (index == 0 && s == null) return Invalid;
            if (s != null && s.Gpu == null) return Invalid;
            D3D9State.SetObject(ref Targets[index], s);
            if (index == 0 && s != null)
            {
                State.Viewport = new D3DVIEWPORT9 { Width = s.Width, Height = s.Height, MaxZ = 1 };
                State.Scissor = new D3DRECT9 { right = (int)s.Width, bottom = (int)s.Height };
                State.MiscVersion++;
            }
            return HResult.S_OK;
        }

        // ---- Clear --------------------------------------------------------------------------------

        [UnmanagedCallersOnly]
        private static int ClearTargets(IntPtr self, uint count, D3DRECT9* rects, uint flags, uint color, float z, uint stencil)
        {
            var d = Dev(self);
            if (d == null) return Invalid;
            var list = new List<D3DRECT9>();
            if (count > 0 && rects != null) for (uint i = 0; i < count; i++) list.Add(rects[i]);
            return Guard("Clear", () => d.Clear(list, flags, color, z, stencil));
        }

        private static WGPUColor ColorOf(uint argb) => new()
        {
            r = ((argb >> 16) & 0xFF) / 255.0, g = ((argb >> 8) & 0xFF) / 255.0, b = (argb & 0xFF) / 255.0, a = (argb >> 24) / 255.0,
        };

        /// <summary>Clear: D3DCLEAR_TARGET (1), ZBUFFER (2), STENCIL (4) of the bound targets, in the viewport,
        /// the rectangles given, and the scissor rectangle when the scissor test is on.</summary>
        public int Clear(List<D3DRECT9> rects, uint flags, uint color, float z, uint stencil)
        {
            lock (DrawLock)
            {
                if (Bound() is not { } ts) return HResult.S_OK;
                bool target = (flags & 1) != 0, depth = (flags & 2) != 0 && ts.Depth != null, sten = (flags & 4) != 0 && ts.Depth != null && Formats.HasStencil(ts.Depth.Gpu!.Format);
                if (!target && !depth && !sten) return HResult.S_OK;
                var vp = State.Viewport;
                var area = new D3DRECT9 { left = (int)vp.X, top = (int)vp.Y, right = (int)(vp.X + vp.Width), bottom = (int)(vp.Y + vp.Height) };
                area = Intersect(area, new D3DRECT9 { right = (int)ts.Width, bottom = (int)ts.Height });
                if (State.RS[D3DRS.SCISSORTESTENABLE] != 0) area = Intersect(area, State.Scissor);
                var parts = rects.Count == 0 ? new List<D3DRECT9> { area } : rects.Select(r => Intersect(r, area)).ToList();
                parts.RemoveAll(r => r.right <= r.left || r.bottom <= r.top);
                if (parts.Count == 0) return HResult.S_OK;
                bool whole = parts.Count == 1 && parts[0].left == 0 && parts[0].top == 0 && parts[0].right == ts.Width && parts[0].bottom == ts.Height;
                if (whole)
                {
                    // the whole of the targets: the pass clears on load what is cleared, and loads the rest
                    EnsurePass(ts, target ? ColorOf(color) : null, depth ? z : null, sten ? stencil : null);
                    return HResult.S_OK;
                }
                // a part of the targets: a triangle over each rectangle that writes what is cleared
                IntPtr pipeline = ClearPipeline(ts, target, depth, sten);
                if (pipeline == IntPtr.Zero) return HResult.S_OK;
                var cc = ColorOf(color);
                var data = new float[8] { (float)cc.r, (float)cc.g, (float)cc.b, (float)cc.a, Math.Clamp(z, 0f, 1f), 0, 0, 0 };
                int at = ArenaAlloc(MemoryMarshal.AsBytes(data.AsSpan()));
                IntPtr group = UniformGroup(_clearLayout.Bgl, at, 32);
                if (!EnsurePass(ts)) { wgpuBindGroupRelease(group); return HResult.S_OK; }
                wgpuRenderPassEncoderSetPipeline(_pass, pipeline);
                wgpuRenderPassEncoderSetBindGroup(_pass, 0, group, 0, null);
                wgpuBindGroupRelease(group);
                wgpuRenderPassEncoderSetViewport(_pass, 0, 0, ts.Width, ts.Height, 0, 1);
                wgpuRenderPassEncoderSetStencilReference(_pass, stencil & 0xFF);
                foreach (var r in parts)
                {
                    wgpuRenderPassEncoderSetScissorRect(_pass, (uint)r.left, (uint)r.top, (uint)(r.right - r.left), (uint)(r.bottom - r.top));
                    wgpuRenderPassEncoderDraw(_pass, 3, 1, 0, 0);
                }
                return HResult.S_OK;
            }
        }

        private static D3DRECT9 Intersect(D3DRECT9 a, D3DRECT9 b) => new()
        {
            left = Math.Max(a.left, b.left), top = Math.Max(a.top, b.top), right = Math.Min(a.right, b.right), bottom = Math.Min(a.bottom, b.bottom),
        };

        private (IntPtr Bgl, IntPtr Pl) _clearLayout;

        private IntPtr UniformGroup(IntPtr bgl, int offset, int size)
        {
            var e = new WGPUBindGroupEntry { binding = 0, buffer = _uniformGpu, offset = (ulong)offset, size = (ulong)size };
            var desc = new WGPUBindGroupDescriptor { layout = bgl, entryCount = 1, entries = &e };
            return wgpuDeviceCreateBindGroup(Device11.Gpu.Device, &desc);
        }

        private IntPtr ClearPipeline(TargetSet ts, bool color, bool depth, bool stencil)
        {
            var key = new StringBuilder("clear:").Append(color).Append(depth).Append(stencil);
            for (int i = 0; i < 4; i++) key.Append('|').Append(ts.Color[i] == null ? 0 : (int)ts.Color[i]!.Gpu!.Format);
            key.Append("|d").Append(ts.Depth == null ? 0 : (int)ts.Depth.Gpu!.Format);
            string k = key.ToString();
            if (_pipelines.TryGetValue(k, out IntPtr p)) return p;
            if (_clearLayout.Bgl == IntPtr.Zero)
            {
                var e = new WGPUBindGroupLayoutEntry { binding = 0, visibility = WGPUShaderStage_Vertex | WGPUShaderStage_Fragment, buffer = new WGPUBufferBindingLayout { type = WGPUBufferBindingType.Uniform, minBindingSize = 32 } };
                var d = new WGPUBindGroupLayoutDescriptor { entryCount = 1, entries = &e };
                IntPtr bgl = wgpuDeviceCreateBindGroupLayout(Device11.Gpu.Device, &d);
                var pld = new WGPUPipelineLayoutDescriptor { bindGroupLayoutCount = 1, bindGroupLayouts = &bgl };
                _clearLayout = (bgl, wgpuDeviceCreatePipelineLayout(Device11.Gpu.Device, &pld));
            }
            int count = 0;
            for (int i = 0; i < 4; i++) if (ts.Color[i] != null) count = i + 1;
            var wgsl = new StringBuilder("struct C { color : vec4<f32>, depth : vec4<f32> };\n@group(0) @binding(0) var<uniform> c : C;\n");
            wgsl.Append("@vertex fn vs(@builtin(vertex_index) i : u32) -> @builtin(position) vec4<f32> {\n")
                .Append("    var p = array<vec2<f32>, 3>(vec2<f32>(-1.0, -1.0), vec2<f32>(3.0, -1.0), vec2<f32>(-1.0, 3.0));\n")
                .Append("    return vec4<f32>(p[i], c.depth.x, 1.0);\n}\n");
            wgsl.Append("struct FOut {\n");
            for (int i = 0; i < Math.Max(1, count); i++) wgsl.Append("    @location(").Append(i).Append(") c").Append(i).Append(" : vec4<f32>,\n");
            wgsl.Append("};\n@fragment fn fs() -> FOut {\n    var f : FOut;\n");
            for (int i = 0; i < Math.Max(1, count); i++) wgsl.Append("    f.c").Append(i).Append(" = c.color;\n");
            wgsl.Append("    return f;\n}\n");
            IntPtr module = Module(k, wgsl.ToString());
            var targets = stackalloc WGPUColorTargetState[4];
            for (int i = 0; i < count; i++)
                targets[i] = ts.Color[i] == null ? new WGPUColorTargetState { format = WGPUTextureFormat.Undefined }
                    : new WGPUColorTargetState { format = ts.Color[i]!.Gpu!.Format, writeMask = color ? 0xFu : 0u };
            byte* vsName = stackalloc byte[2] { (byte)'v', (byte)'s' };
            byte* fsName = stackalloc byte[2] { (byte)'f', (byte)'s' };
            var frag = new WGPUFragmentState { module = module, entryPoint = new WGPUStringView { data = fsName, length = 2 }, targetCount = (nuint)count, targets = targets };
            var ds = new WGPUDepthStencilState();
            if (ts.Depth != null)
            {
                var keep = new WGPUStencilFaceState { compare = WGPUCompareFunction.Always, failOp = WGPUStencilOperation.Keep, depthFailOp = WGPUStencilOperation.Keep, passOp = stencil ? (WGPUStencilOperation)3 : WGPUStencilOperation.Keep };
                ds.format = ts.Depth.Gpu!.Format;
                ds.depthWriteEnabled = depth ? WGPUOptionalBool.True : WGPUOptionalBool.False;
                ds.depthCompare = WGPUCompareFunction.Always;
                ds.stencilFront = ds.stencilBack = keep;
                ds.stencilReadMask = 0xFF;
                ds.stencilWriteMask = stencil ? 0xFFu : 0u;
            }
            var desc = new WGPURenderPipelineDescriptor
            {
                layout = _clearLayout.Pl,
                vertex = new WGPUVertexState { module = module, entryPoint = new WGPUStringView { data = vsName, length = 2 } },
                primitive = new WGPUPrimitiveState { topology = WGPUPrimitiveTopology.TriangleList, frontFace = WGPUFrontFace.CW, cullMode = WGPUCullMode.None },
                depthStencil = ts.Depth != null ? (IntPtr)(&ds) : IntPtr.Zero,
                multisample = new WGPUMultisampleState { count = 1, mask = 0xFFFFFFFF },
                fragment = &frag,
            };
            p = CreatePipeline(&desc);
            _pipelines[k] = p;
            return p;
        }

        // ---- draws --------------------------------------------------------------------------------

        private struct DrawCall
        {
            public int Type;
            public uint PrimCount;
            public bool Indexed;
            public uint StartVertex;
            public int BaseVertex;
            public uint MinIndex, NumVertices, StartIndex;
            public byte* UpVertices;
            public uint UpStride, UpVertexBytes;
            public byte* UpIndices;
            public bool UpIndex32;
        }

        private static uint VerticesOf(int type, uint prims) => type switch
        {
            1 => prims, 2 => prims * 2, 3 => prims + 1, 4 => prims * 3, 5 or 6 => prims + 2, _ => 0,
        };

        [UnmanagedCallersOnly]
        private static int DrawPrimitive(IntPtr self, int type, uint start, uint prims)
        {
            var d = Dev(self);
            if (d == null) return Invalid;
            return Guard("DrawPrimitive", () => d.Draw(new DrawCall { Type = type, PrimCount = prims, StartVertex = start }));
        }

        [UnmanagedCallersOnly]
        private static int DrawIndexedPrimitive(IntPtr self, int type, int baseVertex, uint minIndex, uint numVertices, uint startIndex, uint prims)
        {
            var d = Dev(self);
            if (d == null) return Invalid;
            return Guard("DrawIndexedPrimitive", () => d.Draw(new DrawCall
            {
                Type = type, PrimCount = prims, Indexed = true, BaseVertex = baseVertex, MinIndex = minIndex, NumVertices = numVertices, StartIndex = startIndex,
            }));
        }

        [UnmanagedCallersOnly]
        private static int DrawPrimitiveUP(IntPtr self, int type, uint prims, void* data, uint stride)
        {
            var d = Dev(self);
            if (d == null || data == null || stride == 0) return Invalid;
            uint n = VerticesOf(type, prims);
            return Guard("DrawPrimitiveUP", () => d.DrawUp(new DrawCall { Type = type, PrimCount = prims, UpVertices = (byte*)data, UpStride = stride, UpVertexBytes = n * stride }));
        }

        [UnmanagedCallersOnly]
        private static int DrawIndexedPrimitiveUP(IntPtr self, int type, uint minIndex, uint numVertices, uint prims, void* indices, int indexFormat, void* vertices, uint stride)
            => DrawIndexedPrimitiveUPImpl(self, type, minIndex, numVertices, prims, indices, indexFormat, vertices, stride);

        private static int DrawIndexedPrimitiveUPImpl(IntPtr self, int type, uint minIndex, uint numVertices, uint prims, void* indices, int indexFormat, void* vertices, uint stride)
        {
            var d = Dev(self);
            if (d == null || indices == null || vertices == null || stride == 0) return Invalid;
            return Guard("DrawIndexedPrimitiveUP", () => d.DrawUp(new DrawCall
            {
                Type = type, PrimCount = prims, Indexed = true, MinIndex = minIndex, NumVertices = numVertices,
                UpVertices = (byte*)vertices, UpStride = stride, UpVertexBytes = (minIndex + numVertices) * stride,
                UpIndices = (byte*)indices, UpIndex32 = indexFormat == Direct3DBuffer9.D3DFMT_INDEX32,
            }));
        }

        /// <summary>A UP draw: then stream 0 and (indexed) the indices are unset, as D3D9 leaves them.</summary>
        private int DrawUp(DrawCall c)
        {
            int hr = Draw(c);
            D3D9State.SetObject(ref State.Streams[0], null);
            State.StreamOffset[0] = State.StreamStride[0] = 0;
            if (c.Indexed) D3D9State.SetObject(ref State.Indices, null);
            return hr;
        }

        /// <summary>The vertex elements a draw reads, by location, with the stream each comes from.</summary>
        private sealed class InputMap
        {
            public readonly SortedDictionary<int, D3DVERTEXELEMENT9> ByLocation = new();
        }

        private int Draw(DrawCall c)
        {
            if (c.PrimCount == 0) return HResult.S_OK;
            if (c.Type is < 1 or > 6) return Invalid;
            lock (DrawLock)
            {
                if (Bound() is not { } ts) return Invalid;
                var decl = State.Decl;
                if (decl == null) return Invalid;
                bool transformed = decl.Elements.Any(e => e.Usage == D3D9Usage.PositionT);
                D3D9Shader? vs = transformed ? null : State.Vs?.Code;
                D3D9Shader? ps = State.Ps?.Code;
                if (vs?.Error != null || ps?.Error != null) { DrawsSkipped++; return HResult.S_OK; }

                // the epilogue and the linkage
                var tail = new D3D9Programs.PixelTail
                {
                    AlphaFunc = State.RS[D3DRS.ALPHATESTENABLE] != 0 ? (int)State.RS[D3DRS.ALPHAFUNC] : 8,
                    Fog = State.RS[D3DRS.FOGENABLE] == 0 ? 0 : State.RS[D3DRS.FOGTABLEMODE] is uint t and >= 1 and <= 3 ? (int)t + 1 : 1,
                    WFog = !transformed && State.Transform(3)[11] != 0,
                    ClipMask = transformed ? 0 : (int)(State.RS[D3DRS.CLIPPLANEENABLE] & 0x3F),
                };
                bool flat = State.RS[D3DRS.SHADEMODE] == 1;
                D3D9Programs.FixedPixel? ffp = ps == null ? FixedPixelKey() : null;
                var link = ps != null ? D3D9Programs.LinkageOf(ps, tail, flat) : D3D9Programs.FixedLinkage(ffp!, tail, flat);
                int targets = 0;
                for (int i = 0; i < 4; i++) if (ts.Color[i] != null) targets = i + 1;

                // textures: what each sampler the stages read has bound
                var psTex = new SortedDictionary<int, (D3DTexture2D? Tex, int Fixup, int Type)>();
                var vsTex = new SortedDictionary<int, (D3DTexture2D? Tex, int Fixup, int Type)>();
                if (ps != null) foreach (var (s, st) in ps.Samplers) psTex[s] = Sampled(s, st, shader: true, ts);
                else for (int i = 0; i < ffp!.Stages.Count; i++)
                        if (D3D9Programs.FixedPixel.ReadsTexture(ffp.Stages[i]) && ffp.Stages[i].Fixup != 8) psTex[i] = Sampled(i, ffp.Stages[i].TexType, shader: false, ts);
                if (vs != null) foreach (var (s, st) in vs.Samplers) vsTex[s] = Sampled(16 + s, st, shader: true, ts);

                // the vertex inputs
                var inputs = new InputMap();
                D3D9Programs.FixedVertex? ffv = null;
                if (vs != null)
                {
                    foreach (var io in vs.Inputs)
                    {
                        int at = Array.FindIndex(decl.Elements, e => e.Usage == io.Usage && e.UsageIndex == io.Index);
                        if (at >= 0) inputs.ByLocation[io.Register] = decl.Elements[at];
                    }
                }
                else
                {
                    foreach (var e in decl.Elements)
                    {
                        int loc = D3D9Programs.FixedLocation(e.Usage, e.UsageIndex);
                        if (loc >= 0 && !inputs.ByLocation.ContainsKey(loc)) inputs.ByLocation[loc] = e;
                    }
                    if (!inputs.ByLocation.ContainsKey(0)) { DrawsSkipped++; D3DLog.Once("d3d9nopos", "a fixed-function draw without a position"); return HResult.S_OK; }
                    ffv = FixedVertexKey(inputs, transformed, link);
                }

                // points with a size (D3DRS_POINTSIZE, the vertex's PSIZE, scaling) or sprites: squares
                int pointMode = 0;
                if (c.Type == 1)
                {
                    float F(int rs) => BitConverter.UInt32BitsToSingle(State.RS[rs]);
                    bool vertexSize = vs != null ? vs.Outputs.Any(o => o.Usage == D3D9Usage.PSize) : inputs.ByLocation.ContainsKey(2);
                    bool scale = vs == null && !transformed && State.RS[D3DRS.POINTSCALEENABLE] != 0;
                    float size = Math.Clamp(F(D3DRS.POINTSIZE), F(D3DRS.POINTSIZE_MIN), Math.Min(F(D3DRS.POINTSIZE_MAX), MaxPointSize));
                    if (State.RS[D3DRS.POINTSPRITEENABLE] != 0) pointMode = 2;
                    else if (vertexSize || scale || size != 1f) pointMode = 1;
                    if (ffv != null)
                    {
                        ffv.PointMode = pointMode;
                        ffv.PSize = inputs.ByLocation.ContainsKey(2);
                        ffv.PointScale = scale;
                    }
                }
                var pts = new D3D9Programs.Points { Mode = pointMode };

                // the modules
                var psFix = psTex.ToDictionary(kv => kv.Key, kv => kv.Value.Fixup);
                var vsFix = vsTex.ToDictionary(kv => kv.Key, kv => kv.Value.Fixup);
                string vsId, psId;
                IntPtr vsModule, psModule;
                if (vs != null)
                {
                    var types = inputs.ByLocation.ToDictionary(kv => kv.Key, kv => kv.Value.Type);
                    vsId = $"vs{vs.Hash:x16}|{string.Join(",", types.Select(kv => $"{kv.Key}:{kv.Value}"))}|{string.Join(",", vsFix.Select(kv => $"{kv.Key}:{kv.Value}"))}|{link.Id}{pts.Id}";
                    vsModule = Module(vsId, () => D3D9Programs.VertexShaderModule(vs, link, types, vsFix, pts));
                }
                else
                {
                    vsId = ffv!.Id + "|" + link.Id;
                    vsModule = Module(vsId, () => D3D9Programs.FixedVertexModule(ffv, link));
                }
                if (ps != null)
                {
                    psId = $"ps{ps.Hash:x16}|{string.Join(",", psFix.Select(kv => $"{kv.Key}:{kv.Value}"))}|{link.Id}|{tail.Id}|{targets}";
                    psModule = Module(psId, () => D3D9Programs.PixelShaderModule(ps, link, tail, targets, psFix));
                }
                else
                {
                    psId = ffp!.Id + "|" + link.Id + "|" + tail.Id + "|" + targets;
                    psModule = Module(psId, () => D3D9Programs.FixedPixelModule(ffp, link, tail, targets));
                }
                if (vsModule == IntPtr.Zero || psModule == IntPtr.Zero) { DrawsSkipped++; return HResult.S_OK; }

                // the primitive and its indices
                int type = c.Type;
                uint fill = State.RS[D3DRS.FILLMODE];
                bool triangles = type >= 4;
                bool regenerate = type == 6 || (triangles && fill is 1 or 2);
                uint vertexCount = VerticesOf(type, c.PrimCount);
                WGPUPrimitiveTopology topology = type switch
                {
                    1 => WGPUPrimitiveTopology.PointList, 2 => WGPUPrimitiveTopology.LineList, 3 => WGPUPrimitiveTopology.LineStrip,
                    5 => WGPUPrimitiveTopology.TriangleStrip, _ => WGPUPrimitiveTopology.TriangleList,
                };
                if (pointMode != 0) topology = WGPUPrimitiveTopology.TriangleList;
                if (regenerate) topology = triangles && fill == 1 ? WGPUPrimitiveTopology.PointList : triangles && fill == 2 ? WGPUPrimitiveTopology.LineList : WGPUPrimitiveTopology.TriangleList;
                bool index32 = c.UpIndices != null ? c.UpIndex32 : State.Indices?.Index32 == true;
                var stripFormat = !regenerate && pointMode == 0 && c.Indexed && topology is WGPUPrimitiveTopology.TriangleStrip or WGPUPrimitiveTopology.LineStrip
                    ? (index32 ? WGPUIndexFormat.Uint32 : WGPUIndexFormat.Uint16) : WGPUIndexFormat.Undefined;
                if (c.Indexed && c.UpIndices == null && State.Indices == null) return Invalid;

                // the vertex buffers: the streams the inputs read
                var streams = new SortedDictionary<int, List<(int Loc, D3DVERTEXELEMENT9 E)>>();
                foreach (var (loc, e) in inputs.ByLocation)
                {
                    if (!streams.TryGetValue(e.Stream, out var list)) streams[e.Stream] = list = new();
                    list.Add((loc, e));
                }
                bool up = c.UpVertices != null;
                uint instances = 1;
                if (c.Indexed && pointMode == 0 && (State.StreamFreq[0] & 0x40000000) != 0) instances = Math.Max(1, State.StreamFreq[0] & 0x3FFFFFFF);
                var layoutKey = new StringBuilder();
                var strides = new Dictionary<int, uint>();
                foreach (var (stream, list) in streams)
                {
                    uint stride = up ? c.UpStride : State.StreamStride[stream];
                    if (up && stream != 0) { D3DLog.Once("d3d9upstream", "a UP draw reads a stream other than 0"); DrawsSkipped++; return HResult.S_OK; }
                    if (!up && State.Streams[stream] == null) { D3DLog.Once("d3d9nostream", $"a draw reads stream {stream}, which has no buffer"); DrawsSkipped++; return HResult.S_OK; }
                    if ((stride & 3) != 0 || list.Any(x => stride != 0 && x.E.Offset + Direct3DVertexDeclaration9.TypeBytes(x.E.Type) > stride) || list.Any(x => (x.E.Offset & 3) != 0))
                    {
                        D3DLog.Once("d3d9stride", $"a vertex stride of {stride} (or an element past it, or unaligned) is not drawn");
                        DrawsSkipped++;
                        return HResult.S_OK;
                    }
                    strides[stream] = stride;
                    bool perInstance = pointMode != 0 || (instances > 1 && (State.StreamFreq[stream] & 0x80000000) != 0);
                    layoutKey.Append(stream).Append(':').Append(stride).Append(perInstance ? 'i' : 'v');
                    foreach (var (loc, e) in list) layoutKey.Append(',').Append(loc).Append('.').Append(e.Type).Append('.').Append(e.Offset);
                    layoutKey.Append(';');
                }

                IntPtr pipeline = DrawPipeline(vsId, psId, vsModule, psModule, ts, topology, stripFormat, layoutKey.ToString(), streams, strides, instances, vs, ps, psTex, vsTex, pointMode != 0);
                if (pipeline == IntPtr.Zero) { DrawsSkipped++; return HResult.S_OK; }

                // the UP and regenerated data, before the uniforms (a full arena submits first)
                uint[]? generated = null;
                if (regenerate)
                {
                    generated = Regenerate(c, type, fill, index32);
                    if (generated == null) { DrawsSkipped++; return HResult.S_OK; }
                }
                // an indexed point list as squares: each stream's vertices in index order (a point is an instance)
                List<byte[]>? gathered = null;
                if (pointMode != 0 && c.Indexed)
                {
                    gathered = GatherPoints(c, streams, strides, index32);
                    if (gathered == null) { D3DLog.Once("d3d9ptrange", "an indexed point list past its buffers is not drawn"); DrawsSkipped++; return HResult.S_OK; }
                }
                int needVertex = (gathered?.Sum(g => g.Length + 4) ?? 0) + (int)(up ? c.UpVertexBytes : 0) + 4 + (c.UpIndices != null ? (int)(vertexCount * (c.UpIndex32 ? 4 : 2)) + 4 : 0) + (generated != null ? generated.Length * 4 + 4 : 0);
                if (_uniformAt + 3 * (D3D9Programs.ConstsSize + 256) + D3D9Programs.MiscSize + 512 > ArenaSize || (needVertex <= ArenaSize && _vertexAt + needVertex > ArenaSize))
                    FlushDraws();
                (IntPtr Buf, ulong Off) upVb = default, upIb = default, genIb = default;
                var gatheredVb = gathered?.Select(g => Place(g)).ToList();
                if (up && gathered == null) upVb = Place(new ReadOnlySpan<byte>(c.UpVertices, (int)c.UpVertexBytes));
                if (c.UpIndices != null && gathered == null) upIb = Place(new ReadOnlySpan<byte>(c.UpIndices, (int)(vertexCount * (c.UpIndex32 ? 4 : 2))));
                if (generated != null) genIb = Place(MemoryMarshal.AsBytes(generated.AsSpan()));

                // the uniforms
                if (vs != null && (_vsConstAt < 0 || _vsConstVer != State.VsConstVersion)) { _vsConstAt = ArenaAlloc(Consts(State.VsF, State.VsI, State.VsB)); _vsConstVer = State.VsConstVersion; }
                if (ps != null && (_psConstAt < 0 || _psConstVer != State.PsConstVersion)) { _psConstAt = ArenaAlloc(Consts(State.PsF, State.PsI, State.PsB)); _psConstVer = State.PsConstVersion; }
                if (_miscAt < 0 || _miscVer != State.MiscVersion || _miscTarget != (ts.Width, ts.Height))
                {
                    _miscAt = ArenaAlloc(MiscBytes(ts, out _));
                    _miscVer = State.MiscVersion;
                    _miscTarget = (ts.Width, ts.Height);
                }
                Viewport(ts, out var clamped);
                if (clamped.Width <= 0 || clamped.Height <= 0) return HResult.S_OK;

                IntPtr group = DrawGroup(vs, ps, psTex, vsTex);
                if (group == IntPtr.Zero) { DrawsSkipped++; return HResult.S_OK; }

                // vertex buffers' GPU copies, before the pass records them
                var vbs = new List<(IntPtr Buf, ulong Off, ulong Size)>();
                int si = 0;
                foreach (var (stream, _) in streams)
                {
                    if (gatheredVb != null) { vbs.Add((gatheredVb[si].Buf, gatheredVb[si].Off, (ulong)gathered![si].Length)); si++; continue; }
                    if (up) { vbs.Add((upVb.Buf, upVb.Off, c.UpVertexBytes)); continue; }
                    var b = State.Streams[stream]!;
                    IntPtr g = b.EnsureGpu(Batch);
                    ulong off = State.StreamOffset[stream];
                    vbs.Add((g, off, b.Size > off ? b.Size - off : 0));
                }
                IntPtr ibBuf = IntPtr.Zero;
                ulong ibOff = 0, ibSize = 0;
                if (generated != null) { ibBuf = genIb.Buf; ibOff = genIb.Off; ibSize = (ulong)generated.Length * 4; }
                else if (c.UpIndices != null) { ibBuf = upIb.Buf; ibOff = upIb.Off; ibSize = vertexCount * (c.UpIndex32 ? 4u : 2u); }
                else if (c.Indexed && gathered == null)
                {
                    var ib = State.Indices!;
                    ulong bytes = index32 ? 4u : 2u;
                    if ((c.StartIndex + (ulong)vertexCount) * bytes > ib.Size) { D3DLog.Once("d3d9ibrange", "an indexed draw past its index buffer is not drawn"); wgpuBindGroupRelease(group); DrawsSkipped++; return HResult.S_OK; }
                    ibBuf = ib.EnsureGpu(Batch);
                    ibSize = ib.Size;
                }
                if (!c.Indexed && !up && generated == null)
                    foreach (var (stream, list) in streams)
                    {
                        var b = State.Streams[stream]!;
                        uint stride = strides[stream];
                        ulong end = State.StreamOffset[stream] + (ulong)(c.StartVertex + vertexCount - 1) * stride + list.Max(x => x.E.Offset + Direct3DVertexDeclaration9.TypeBytes(x.E.Type));
                        if (end > b.Size) { D3DLog.Once("d3d9vbrange", "a draw past its vertex buffer is not drawn"); wgpuBindGroupRelease(group); DrawsSkipped++; return HResult.S_OK; }
                    }

                if (!EnsurePass(ts)) { wgpuBindGroupRelease(group); DrawsSkipped++; return HResult.S_OK; }
                wgpuRenderPassEncoderSetPipeline(_pass, pipeline);
                wgpuRenderPassEncoderSetBindGroup(_pass, 0, group, 0, null);
                wgpuBindGroupRelease(group);
                for (int i = 0; i < vbs.Count; i++) wgpuRenderPassEncoderSetVertexBuffer(_pass, (uint)i, vbs[i].Buf, vbs[i].Off, vbs[i].Size);
                wgpuRenderPassEncoderSetViewport(_pass, clamped.X, clamped.Y, clamped.Width, clamped.Height, Math.Clamp(State.Viewport.MinZ, 0, 1), Math.Clamp(State.Viewport.MaxZ, 0, 1));
                var sc = new D3DRECT9 { right = (int)ts.Width, bottom = (int)ts.Height };
                if (State.RS[D3DRS.SCISSORTESTENABLE] != 0) sc = Intersect(sc, State.Scissor);
                if (sc.right <= sc.left || sc.bottom <= sc.top) { DrawsIssued++; return HResult.S_OK; }
                wgpuRenderPassEncoderSetScissorRect(_pass, (uint)sc.left, (uint)sc.top, (uint)(sc.right - sc.left), (uint)(sc.bottom - sc.top));
                var bf = ColorOf(State.RS[D3DRS.BLENDFACTOR]);
                wgpuRenderPassEncoderSetBlendConstant(_pass, &bf);
                wgpuRenderPassEncoderSetStencilReference(_pass, State.RS[D3DRS.STENCILREF] & 0xFF);

                if (pointMode != 0)
                    wgpuRenderPassEncoderDraw(_pass, 6, vertexCount, 0, gathered != null || up ? 0 : c.StartVertex);
                else if (generated != null)
                {
                    wgpuRenderPassEncoderSetIndexBuffer(_pass, ibBuf, WGPUIndexFormat.Uint32, ibOff, ibSize);
                    wgpuRenderPassEncoderDrawIndexed(_pass, (uint)generated.Length, instances, 0, c.Indexed ? c.BaseVertex : (int)c.StartVertex, 0);
                }
                else if (c.Indexed)
                {
                    var f = index32 ? WGPUIndexFormat.Uint32 : WGPUIndexFormat.Uint16;
                    wgpuRenderPassEncoderSetIndexBuffer(_pass, ibBuf, f, ibOff, ibSize);
                    wgpuRenderPassEncoderDrawIndexed(_pass, vertexCount, instances, c.UpIndices != null ? 0 : c.StartIndex, c.BaseVertex, 0);
                }
                else wgpuRenderPassEncoderDraw(_pass, vertexCount, 1, up ? 0 : c.StartVertex, 0);
                DrawsIssued++;
                return HResult.S_OK;
            }
        }

        private (IntPtr Buf, ulong Off) Place(ReadOnlySpan<byte> data)
        {
            if (data.Length + 4 > ArenaSize) return (OwnBuffer(data), 0);
            int at = VertexAlloc(data);
            return (_vertexGpu, (ulong)at);
        }

        /// <summary>D3DCAPS9.MaxPointSize.</summary>
        internal const float MaxPointSize = 256f;
        /// <summary>The world matrices indexed vertex blending reaches (D3DCAPS9.MaxVertexBlendMatrixIndex + 1).</summary>
        internal const uint BlendMatrixIndices = D3D9Programs.BlendMatrices;

        /// <summary>The vertices an indexed point list reads, per stream in index order (null: past a buffer).</summary>
        private List<byte[]>? GatherPoints(DrawCall c, SortedDictionary<int, List<(int Loc, D3DVERTEXELEMENT9 E)>> streams, Dictionary<int, uint> strides, bool index32)
        {
            uint n = c.PrimCount;
            byte* ip;
            if (c.UpIndices != null) ip = c.UpIndices;
            else
            {
                var ib = State.Indices!;
                ulong bytes = index32 ? 4u : 2u;
                if ((c.StartIndex + (ulong)n) * bytes > ib.Size) return null;
                ip = ib.Cpu + c.StartIndex * bytes;
            }
            var result = new List<byte[]>();
            foreach (var (stream, list) in streams)
            {
                uint stride = strides[stream];
                uint used = (uint)list.Max(x => x.E.Offset + Direct3DVertexDeclaration9.TypeBytes(x.E.Type));
                byte* src;
                ulong size;
                if (c.UpVertices != null) { src = c.UpVertices; size = c.UpVertexBytes; }
                else
                {
                    var b = State.Streams[stream]!;
                    ulong off = State.StreamOffset[stream];
                    if (off > b.Size) return null;
                    src = b.Cpu + off;
                    size = b.Size - off;
                }
                var dst = new byte[n * stride];
                for (uint i = 0; i < n; i++)
                {
                    long v = (long)(index32 ? ((uint*)ip)[i] : ((ushort*)ip)[i]) + (c.UpVertices != null ? 0 : c.BaseVertex);
                    if (v < 0 || (ulong)v * stride + used > size) return null;
                    new ReadOnlySpan<byte>(src + v * stride, (int)Math.Min(stride, size - (ulong)v * stride)).CopyTo(dst.AsSpan((int)(i * stride)));
                }
                result.Add(dst);
            }
            return result;
        }

        /// <summary>The indices of a fan as a list, or of triangles as their edges (wireframe) or corners
        /// (point fill), relative to the base vertex.</summary>
        private uint[]? Regenerate(DrawCall c, int type, uint fill, bool index32)
        {
            uint n = VerticesOf(type, c.PrimCount);
            uint[] src = new uint[n];
            if (c.Indexed)
            {
                byte* ip;
                if (c.UpIndices != null) ip = c.UpIndices;
                else
                {
                    var ib = State.Indices!;
                    ulong bytes = index32 ? 4u : 2u;
                    if ((c.StartIndex + (ulong)n) * bytes > ib.Size) return null;
                    ip = ib.Cpu + c.StartIndex * bytes;
                }
                for (int i = 0; i < n; i++) src[i] = index32 ? ((uint*)ip)[i] : ((ushort*)ip)[i];
            }
            else for (uint i = 0; i < n; i++) src[i] = i;
            var tris = new List<uint>();
            for (uint p = 0; p < c.PrimCount; p++)
            {
                switch (type)
                {
                    case 4: tris.Add(src[p * 3]); tris.Add(src[p * 3 + 1]); tris.Add(src[p * 3 + 2]); break;
                    case 5:
                        if ((p & 1) == 0) { tris.Add(src[p]); tris.Add(src[p + 1]); tris.Add(src[p + 2]); }
                        else { tris.Add(src[p + 1]); tris.Add(src[p]); tris.Add(src[p + 2]); }
                        break;
                    case 6: tris.Add(src[p + 1]); tris.Add(src[p + 2]); tris.Add(src[0]); break;
                }
            }
            if (fill == 3 || type < 4) return tris.ToArray();
            if (fill == 1) return tris.Distinct().ToArray();
            var lines = new List<uint>(tris.Count * 2);
            for (int i = 0; i + 2 < tris.Count; i += 3)
            {
                lines.Add(tris[i]); lines.Add(tris[i + 1]);
                lines.Add(tris[i + 1]); lines.Add(tris[i + 2]);
                lines.Add(tris[i + 2]); lines.Add(tris[i]);
            }
            return lines.ToArray();
        }

        /// <summary>A sampler's texture for a draw: the GPU texture, its fixup and texture type; none for one
        /// not bound, in a CPU pool, bound as a target too, or a depth texture.</summary>
        private (D3DTexture2D? Tex, int Fixup, int Type) Sampled(int sampler, int type, bool shader, TargetSet ts)
        {
            int none = shader ? 7 : 8;
            if (State.Tex[sampler] is Direct3DVolumeTexture9 vt)
            {
                if (vt.Gpu == null) return (null, none, type);
                if (!shader) type = 4;
                if (type != 4) { D3DLog.Once("d3d9textype", $"a sampler declared as type {type} has a texture of type 4 bound: it reads nothing"); return (null, none, type); }
                return (vt.Gpu, D3D9Programs.FixupOf(vt.Format), 4);
            }
            if (State.Tex[sampler] is not Direct3DTexture9 t || t.Gpu == null) return (null, none, type);
            if (ts.Color.Any(s => s?.Gpu == t.Gpu) || ts.Depth?.Gpu == t.Gpu)
            {
                D3DLog.Once("d3d9feedback", "a texture bound as a render target and sampled in one draw reads nothing");
                return (null, none, type);
            }
            if (Formats.IsDepth(t.Gpu.Format)) { D3DLog.Once("d3d9depthtex", "sampling a depth texture is not supported"); return (null, none, type); }
            // the texture's type, which must be the one the shader declares (a fixed-function stage takes the texture's)
            int has = t.IsCube ? 3 : 2;
            if (!shader) type = has;
            if (type != has) { D3DLog.Once("d3d9textype", $"a sampler declared as type {type} has a texture of type {has} bound: it reads nothing"); return (null, none, type); }
            return (t.Gpu, D3D9Programs.FixupOf(t.Format), type);
        }

        private static bool Unfilterable(D3DTexture2D? t) => t != null && t.Format is TexFmt.R32Float or TexFmt.RG32Float or TexFmt.RGBA32Float;

        // ---- the fixed-function keys -----------------------------------------------------------------

        private D3D9Programs.FixedPixel FixedPixelKey()
        {
            var k = new D3D9Programs.FixedPixel { SpecularEnable = State.RS[D3DRS.SPECULARENABLE] != 0 };
            for (int i = 0; i < 8; i++)
            {
                uint op = State.Tss[i, 1];
                if (op is 1 or 0) break;
                var s = new D3D9Programs.FixedPixel.Stage
                {
                    ColorOp = op, ColorArg1 = State.Tss[i, 2], ColorArg2 = State.Tss[i, 3], ColorArg0 = State.Tss[i, 26],
                    AlphaOp = State.Tss[i, 4], AlphaArg1 = State.Tss[i, 5], AlphaArg2 = State.Tss[i, 6], AlphaArg0 = State.Tss[i, 27],
                    ResultArg = State.Tss[i, 28], Transform = State.Tss[i, 24],
                    TexType = State.Tex[i] switch { Direct3DTexture9 { IsCube: true } => 3, Direct3DVolumeTexture9 => 4, _ => 2 },
                };
                s.Fixup = State.Tex[i] switch
                {
                    Direct3DTexture9 { Gpu: not null } t when !Formats.IsDepth(t.Gpu.Format) => D3D9Programs.FixupOf(t.Format),
                    Direct3DVolumeTexture9 { Gpu: not null } v => D3D9Programs.FixupOf(v.Format),
                    _ => 8,
                };
                k.Stages.Add(s);
            }
            return k;
        }

        private D3D9Programs.FixedVertex FixedVertexKey(InputMap inputs, bool transformed, D3D9Programs.Linkage link)
        {
            var k = new D3D9Programs.FixedVertex { Transformed = transformed };
            k.PosType = inputs.ByLocation[0].Type;
            if (inputs.ByLocation.TryGetValue(1, out var n)) { k.Normal = true; k.NormalType = n.Type; }
            if (inputs.ByLocation.TryGetValue(3, out var dfs)) { k.Diffuse = true; k.DiffuseType = dfs.Type; }
            if (inputs.ByLocation.TryGetValue(4, out var spc)) { k.Specular = true; k.SpecularType = spc.Type; }
            for (int i = 0; i < 8; i++)
                if (inputs.ByLocation.TryGetValue(5 + i, out var tc))
                {
                    k.TexTypes[i] = tc.Type;
                    k.TexDims[i] = tc.Type switch
                    {
                        Direct3DVertexDeclaration9.FLOAT1 => 1, Direct3DVertexDeclaration9.FLOAT2 or Direct3DVertexDeclaration9.SHORT2 or Direct3DVertexDeclaration9.SHORT2N
                            or Direct3DVertexDeclaration9.USHORT2N or Direct3DVertexDeclaration9.FLOAT16_2 => 2,
                        Direct3DVertexDeclaration9.FLOAT3 or Direct3DVertexDeclaration9.UDEC3 or Direct3DVertexDeclaration9.DEC3N => 3,
                        _ => 4,
                    };
                }
            uint blend = State.RS[D3DRS.VERTEXBLEND];
            if (!transformed && blend is >= 1 and <= 3 or 256)
            {
                k.BlendWeights = blend == 256 ? 0 : (int)blend;
                k.BlendIndexed = State.RS[D3DRS.INDEXEDVERTEXBLENDENABLE] != 0;
                // (0WEIGHTS, not indexed: WORLD alone, as without blending)
                if (inputs.ByLocation.TryGetValue(13, out var bw)) k.WeightType = bw.Type;
                if (k.BlendIndexed && inputs.ByLocation.TryGetValue(14, out var bi)) k.IndicesType = bi.Type;
            }
            k.Tween = !transformed && blend == 255 && inputs.ByLocation.ContainsKey(15);
            k.Lighting = State.RS[D3DRS.LIGHTING] != 0 && !transformed;
            k.LocalViewer = State.RS[D3DRS.LOCALVIEWER] != 0;
            k.NormalizeNormals = State.RS[D3DRS.NORMALIZENORMALS] != 0;
            k.SpecularEnable = State.RS[D3DRS.SPECULARENABLE] != 0;
            bool colorVertex = State.RS[D3DRS.COLORVERTEX] != 0;
            int Src(int rs) { int s = (int)State.RS[rs]; return !colorVertex || (s == 1 && !k.Diffuse) || (s == 2 && !k.Specular) ? 0 : s; }
            k.DiffuseSrc = Src(D3DRS.DIFFUSEMATERIALSOURCE);
            k.SpecularSrc = Src(D3DRS.SPECULARMATERIALSOURCE);
            k.AmbientSrc = Src(D3DRS.AMBIENTMATERIALSOURCE);
            k.EmissiveSrc = Src(D3DRS.EMISSIVEMATERIALSOURCE);
            if (k.Lighting) foreach (uint l in State.LightsOn.Take(8)) k.Lights.Add(State.Lights.TryGetValue(l, out var light) ? light.Type : 3);
            k.FogMode = State.RS[D3DRS.FOGTABLEMODE] == 0 ? (int)State.RS[D3DRS.FOGVERTEXMODE] : 0;
            k.RangeFog = State.RS[D3DRS.RANGEFOGENABLE] != 0;
            for (int i = 0; i < 8; i++)
            {
                if (!link.Keys.Contains(D3D9Usage.Key(D3D9Usage.TexCoord, i))) continue;
                k.TexIndex[i] = State.Tss[i, 11];
                k.TexTransform[i] = State.Tss[i, 24];
            }
            return k;
        }

        // ---- uniforms -------------------------------------------------------------------------------

        private static byte[] Consts(float[] f, int[] i, int[] b)
        {
            var bytes = new byte[D3D9Programs.ConstsSize];
            MemoryMarshal.AsBytes(f.AsSpan()).CopyTo(bytes);
            MemoryMarshal.AsBytes(i.AsSpan()).CopyTo(bytes.AsSpan(256 * 16));
            for (int k = 0; k < 16; k++) BitConverter.TryWriteBytes(bytes.AsSpan(256 * 16 + 16 * 16 + k * 4), b[k] != 0 ? 1u : 0u);
            return bytes;
        }

        private struct ViewRect { public float X, Y, Width, Height; }

        /// <summary>The viewport WebGPU draws with: D3D9's, within the targets.</summary>
        private void Viewport(TargetSet ts, out ViewRect clamped)
        {
            var v = State.Viewport;
            float x0 = Math.Max(0, v.X), y0 = Math.Max(0, v.Y);
            float x1 = Math.Min(ts.Width, (float)v.X + v.Width), y1 = Math.Min(ts.Height, (float)v.Y + v.Height);
            clamped = new ViewRect { X = x0, Y = y0, Width = x1 - x0, Height = y1 - y0 };
        }

        private static void Put(byte[] b, int at, float x, float y, float z, float w)
        {
            BitConverter.TryWriteBytes(b.AsSpan(at), x);
            BitConverter.TryWriteBytes(b.AsSpan(at + 4), y);
            BitConverter.TryWriteBytes(b.AsSpan(at + 8), z);
            BitConverter.TryWriteBytes(b.AsSpan(at + 12), w);
        }

        private static void Put(byte[] b, int at, D3DCOLORVALUE c) => Put(b, at, c.r, c.g, c.b, c.a);

        private static void PutColor(byte[] b, int at, uint argb) => Put(b, at, ((argb >> 16) & 0xFF) / 255f, ((argb >> 8) & 0xFF) / 255f, (argb & 0xFF) / 255f, (argb >> 24) / 255f);

        private static void Put(byte[] b, int at, float[] m) => MemoryMarshal.AsBytes(m.AsSpan()).CopyTo(b.AsSpan(at));

        /// <summary>The Misc block: viewport, alpha reference, fog, colours, material, transforms, the
        /// texture stages' constants, lights (in view space).</summary>
        private byte[] MiscBytes(TargetSet ts, out ViewRect clamped)
        {
            var b = new byte[D3D9Programs.MiscSize];
            var v = State.Viewport;
            float vw = Math.Max(1, v.Width), vh = Math.Max(1, v.Height);
            Put(b, D3D9Programs.OffPx, 1f / vw, 1f / vh, v.X, v.Y);
            Put(b, D3D9Programs.OffVp, vw, vh, v.MinZ, v.MaxZ);
            Put(b, D3D9Programs.OffAlpha, State.RS[D3DRS.ALPHAREF] & 0xFF, 0, 0, 0);
            float F(int rs) => BitConverter.UInt32BitsToSingle(State.RS[rs]);
            Put(b, D3D9Programs.OffFog, F(D3DRS.FOGSTART), F(D3DRS.FOGEND), F(D3DRS.FOGDENSITY), 0);
            PutColor(b, D3D9Programs.OffFogColor, State.RS[D3DRS.FOGCOLOR]);
            PutColor(b, D3D9Programs.OffTFactor, State.RS[D3DRS.TEXTUREFACTOR]);
            PutColor(b, D3D9Programs.OffAmbient, State.RS[D3DRS.AMBIENT]);
            var m = State.Material;
            Put(b, D3D9Programs.OffMatDiffuse, m.Diffuse);
            Put(b, D3D9Programs.OffMatAmbient, m.Ambient);
            Put(b, D3D9Programs.OffMatSpecular, m.Specular);
            Put(b, D3D9Programs.OffMatEmissive, m.Emissive);
            Put(b, D3D9Programs.OffMatPower, m.Power, 0, 0, 0);
            float[] view = State.Transform(2), proj = State.Transform(3), world = State.Transform(256);
            float[] wv = D3D9Math.Mul(world, view);
            Put(b, D3D9Programs.OffWv, wv);
            Put(b, D3D9Programs.OffProj, proj);
            Put(b, D3D9Programs.OffWvp, D3D9Math.Mul(wv, proj));
            Put(b, D3D9Programs.OffNrm, D3D9Math.Transpose(D3D9Math.Inverse(wv)));
            for (int i = 0; i < 8; i++)
            {
                Put(b, D3D9Programs.OffTex + i * 64, State.Transform(16 + i));
                PutColor(b, D3D9Programs.OffStageConst + i * 16, State.Tss[i, 32]);
                float T(int t) => BitConverter.UInt32BitsToSingle(State.Tss[i, t]);
                Put(b, D3D9Programs.OffBump + i * 16, T(7), T(8), T(9), T(10));
                Put(b, D3D9Programs.OffBumpL + i * 16, T(22), T(23), 0, 0);
            }
            int li = 0;
            foreach (uint l in State.LightsOn)
            {
                if (li >= 8) break;
                var L = State.Lights.TryGetValue(l, out var x) ? x : DefaultLight;
                int at = D3D9Programs.OffLights + li * D3D9Programs.LightStride;
                Put(b, at, L.Diffuse);
                Put(b, at + 16, L.Specular);
                Put(b, at + 32, L.Ambient);
                // position and direction into view space
                float px = L.PosX * view[0] + L.PosY * view[4] + L.PosZ * view[8] + view[12];
                float py = L.PosX * view[1] + L.PosY * view[5] + L.PosZ * view[9] + view[13];
                float pz = L.PosX * view[2] + L.PosY * view[6] + L.PosZ * view[10] + view[14];
                float dx = L.DirX * view[0] + L.DirY * view[4] + L.DirZ * view[8];
                float dy = L.DirX * view[1] + L.DirY * view[5] + L.DirZ * view[9];
                float dz = L.DirX * view[2] + L.DirY * view[6] + L.DirZ * view[10];
                float len = MathF.Sqrt(dx * dx + dy * dy + dz * dz);
                if (len > 0) { dx /= len; dy /= len; dz /= len; }
                Put(b, at + 48, px, py, pz, L.Type == 3 ? float.MaxValue : L.Range);
                Put(b, at + 64, dx, dy, dz, L.Falloff);
                Put(b, at + 80, L.Attenuation0, L.Attenuation1, L.Attenuation2, L.Type);
                Put(b, at + 96, MathF.Cos(L.Theta / 2), MathF.Cos(L.Phi / 2), 0, 0);
                li++;
            }
            // the user clip planes: with a vertex shader in clip space, as given; without, in world space,
            // here in object space (the plane through the world matrix: dist = (p * World) . plane)
            bool shaderVs = State.Vs != null;
            for (int i = 0; i < 6; i++)
            {
                float a = State.ClipPlanes[i * 4], bb = State.ClipPlanes[i * 4 + 1], c = State.ClipPlanes[i * 4 + 2], dd = State.ClipPlanes[i * 4 + 3];
                if (!shaderVs)
                {
                    float[] wm = world;
                    (a, bb, c, dd) = (wm[0] * a + wm[1] * bb + wm[2] * c + wm[3] * dd, wm[4] * a + wm[5] * bb + wm[6] * c + wm[7] * dd,
                                      wm[8] * a + wm[9] * bb + wm[10] * c + wm[11] * dd, wm[12] * a + wm[13] * bb + wm[14] * c + wm[15] * dd);
                }
                Put(b, D3D9Programs.OffClip + i * 16, a, bb, c, dd);
            }
            // points: the size and its clamp, the scale factors
            float Fr(int rs) => BitConverter.UInt32BitsToSingle(State.RS[rs]);
            Put(b, D3D9Programs.OffPoint, Fr(D3DRS.POINTSIZE), Math.Max(0, Fr(D3DRS.POINTSIZE_MIN)), Math.Min(Fr(D3DRS.POINTSIZE_MAX), MaxPointSize), Fr(D3DRS.TWEENFACTOR));
            // vertex blending: each world matrix (D3DTS_WORLDMATRIX(n)) with the view, and for normals
            if (State.RS[D3DRS.VERTEXBLEND] != 0 && !shaderVs)
                for (int i = 0; i < D3D9Programs.BlendMatrices; i++)
                {
                    float[] bwv = D3D9Math.Mul(State.Transform(256 + i), view);
                    Put(b, D3D9Programs.OffBlendWv + i * 64, bwv);
                    Put(b, D3D9Programs.OffBlendNrm + i * 64, D3D9Math.Transpose(D3D9Math.Inverse(bwv)));
                }
            Put(b, D3D9Programs.OffPointScale, Fr(D3DRS.POINTSCALE_A), Fr(D3DRS.POINTSCALE_B), Fr(D3DRS.POINTSCALE_C), 0);
            // the viewport adjustment: D3D9's viewport mapped into the part of it on the targets
            Viewport(ts, out clamped);
            float cw = Math.Max(clamped.Width, 1e-6f), ch = Math.Max(clamped.Height, 1e-6f);
            Put(b, D3D9Programs.OffAdj, vw / cw, vh / ch, (vw + 2 * (v.X - clamped.X)) / cw - 1, 1 - (2 * (v.Y - clamped.Y) + vh) / ch);
            return b;
        }

        // ---- modules, layouts, samplers, pipelines --------------------------------------------------

        private readonly Dictionary<string, IntPtr> _modules = new(), _pipelines = new();
        private readonly Dictionary<string, (IntPtr Bgl, IntPtr Pl)> _layouts = new();
        private readonly Dictionary<string, IntPtr> _samplers = new();
        private readonly Dictionary<int, IntPtr> _dummies = new();
        /// <summary>WPF_D3D9_DUMP_SHADERS: a folder the programs WebGPU rejects are written to (WPF_D3D9_DUMP_ALL=1: every program).</summary>
        private static readonly string? s_dumpDir = Environment.GetEnvironmentVariable("WPF_D3D9_DUMP_SHADERS");
        private static readonly bool s_dumpAll = Environment.GetEnvironmentVariable("WPF_D3D9_DUMP_ALL") == "1";

        private IntPtr Module(string id, string wgsl) => Module(id, () => wgsl);

        private IntPtr Module(string id, Func<string> make)
        {
            if (_modules.TryGetValue(id, out IntPtr m)) return m;
            string wgsl = make();
            byte[] code = Encoding.UTF8.GetBytes(wgsl);
            fixed (byte* p = code)
            {
                var src = new WGPUShaderSourceWGSL
                {
                    chain = new WGPUChainedStruct { next = null, sType = WGPUSType_ShaderSourceWGSL },
                    code = new WGPUStringView { data = p, length = (nuint)code.Length },
                };
                var desc = new WGPUShaderModuleDescriptor { nextInChain = (WGPUChainedStruct*)&src };
                wgpuDevicePushErrorScope(Device11.Gpu.Device, WGPUErrorFilter.Validation);
                IntPtr module = wgpuDeviceCreateShaderModule(Device11.Gpu.Device, &desc);
                m = D3DContext.PopScope(Device11.Gpu, module, out string? error);
                if (error != null) D3DLog.Once("d3d9mod:" + id, $"D3D9 program rejected: {error}");
                if (!string.IsNullOrEmpty(s_dumpDir) && (error != null || s_dumpAll))
                {
                        try { System.IO.Directory.CreateDirectory(s_dumpDir); System.IO.File.WriteAllText(System.IO.Path.Combine(s_dumpDir, $"{(uint)id.GetHashCode():x8}.wgsl"), $"/* {id} */\n" + wgsl + (error != null ? "\n/* " + error + " */\n" : "")); } catch { }
                }
            }
            _modules[id] = m;
            return m;
        }

        private IntPtr CreatePipeline(WGPURenderPipelineDescriptor* desc)
        {
            wgpuDevicePushErrorScope(Device11.Gpu.Device, WGPUErrorFilter.Validation);
            IntPtr created = wgpuDeviceCreateRenderPipeline(Device11.Gpu.Device, desc);
            return D3DContext.PopScope(Device11.Gpu, created, out _);
        }

        private string LayoutKey(D3D9Shader? vs, D3D9Shader? ps, SortedDictionary<int, (D3DTexture2D? Tex, int Fixup, int Type)> psTex, SortedDictionary<int, (D3DTexture2D? Tex, int Fixup, int Type)> vsTex)
        {
            var k = new StringBuilder();
            k.Append(vs != null ? 'V' : 'v').Append(ps != null ? 'P' : 'p');
            foreach (var (s, t) in psTex) k.Append('|').Append(s).Append(t.Type).Append(Unfilterable(t.Tex) ? 'u' : 'f');
            k.Append('/');
            foreach (var (s, t) in vsTex) k.Append('|').Append(s).Append(t.Type).Append(Unfilterable(t.Tex) ? 'u' : 'f');
            return k.ToString();
        }

        private (IntPtr Bgl, IntPtr Pl) Layout(D3D9Shader? vs, D3D9Shader? ps, SortedDictionary<int, (D3DTexture2D? Tex, int Fixup, int Type)> psTex, SortedDictionary<int, (D3DTexture2D? Tex, int Fixup, int Type)> vsTex)
        {
            string key = LayoutKey(vs, ps, psTex, vsTex);
            if (_layouts.TryGetValue(key, out var l)) return l;
            var entries = new List<WGPUBindGroupLayoutEntry>();
            if (vs != null) entries.Add(new WGPUBindGroupLayoutEntry { binding = 0, visibility = WGPUShaderStage_Vertex, buffer = new WGPUBufferBindingLayout { type = WGPUBufferBindingType.Uniform, minBindingSize = D3D9Programs.ConstsSize } });
            if (ps != null) entries.Add(new WGPUBindGroupLayoutEntry { binding = 1, visibility = WGPUShaderStage_Fragment, buffer = new WGPUBufferBindingLayout { type = WGPUBufferBindingType.Uniform, minBindingSize = D3D9Programs.ConstsSize } });
            entries.Add(new WGPUBindGroupLayoutEntry { binding = D3D9Programs.MiscBinding, visibility = WGPUShaderStage_Vertex | WGPUShaderStage_Fragment, buffer = new WGPUBufferBindingLayout { type = WGPUBufferBindingType.Uniform, minBindingSize = D3D9Programs.MiscSize } });
            void Tex(int baseBinding, int s, (D3DTexture2D? Tex, int Fixup, int Type) t, ulong stage)
            {
                bool unf = Unfilterable(t.Tex);
                entries.Add(new WGPUBindGroupLayoutEntry
                {
                    binding = (uint)(baseBinding + 2 * s), visibility = stage,
                    texture = new WGPUTextureBindingLayout { sampleType = unf ? WGPUTextureSampleType.UnfilterableFloat : WGPUTextureSampleType.Float, viewDimension = ViewDim(t.Type) },
                });
                entries.Add(new WGPUBindGroupLayoutEntry
                {
                    binding = (uint)(baseBinding + 2 * s + 1), visibility = stage,
                    sampler = new WGPUSamplerBindingLayout { type = unf ? WGPUSamplerBindingType.NonFiltering : WGPUSamplerBindingType.Filtering },
                });
            }
            foreach (var (s, t) in psTex) Tex(D3D9Programs.PsTextureBase, s, t, WGPUShaderStage_Fragment);
            foreach (var (s, t) in vsTex) Tex(D3D9Programs.VsTextureBase, s, t, WGPUShaderStage_Vertex);
            var arr = entries.ToArray();
            fixed (WGPUBindGroupLayoutEntry* p = arr)
            {
                var d = new WGPUBindGroupLayoutDescriptor { entryCount = (nuint)arr.Length, entries = p };
                IntPtr bgl = wgpuDeviceCreateBindGroupLayout(Device11.Gpu.Device, &d);
                var pld = new WGPUPipelineLayoutDescriptor { bindGroupLayoutCount = 1, bindGroupLayouts = &bgl };
                l = (bgl, wgpuDeviceCreatePipelineLayout(Device11.Gpu.Device, &pld));
            }
            _layouts[key] = l;
            return l;
        }

        private static WGPUTextureViewDimension ViewDim(int d3dType) => d3dType switch { 3 => WGPUTextureViewDimension.Cube, 4 => WGPUTextureViewDimension._3D, _ => WGPUTextureViewDimension._2D };

        private IntPtr Dummy(int d3dType)
        {
            if (_dummies.TryGetValue(d3dType, out IntPtr v)) return v;
            uint layers = d3dType == 3 ? 6u : 1u;
            var d = new WGPUTextureDescriptor
            {
                usage = WGPUTextureUsage.TextureBinding, dimension = d3dType == 4 ? WGPUTextureDimension._3D : WGPUTextureDimension._2D,
                size = new WGPUExtent3D { width = 1, height = 1, depthOrArrayLayers = layers }, format = TexFmt.RGBA8Unorm, mipLevelCount = 1, sampleCount = 1,
            };
            IntPtr tex = wgpuDeviceCreateTexture(Device11.Gpu.Device, &d);
            v = D3DView.CreateTextureView(tex, TexFmt.RGBA8Unorm, ViewDim(d3dType), 0, 1, 0, layers);
            _dummies[d3dType] = v;
            return v;
        }

        /// <summary>The WebGPU sampler of a D3D9 sampler's states.</summary>
        private IntPtr Sampler(int index, bool unfilterable)
        {
            uint U = State.Samp[index, 1], V = State.Samp[index, 2], W = State.Samp[index, 3];
            uint mag = State.Samp[index, 5], min = State.Samp[index, 6], mip = State.Samp[index, 7], maxMip = State.Samp[index, 9], aniso = State.Samp[index, 10];
            if (unfilterable) { mag = min = 1; if (mip != 0) mip = 1; }
            string key = $"{U}.{V}.{W}.{mag}.{min}.{mip}.{maxMip}.{aniso}";
            if (_samplers.TryGetValue(key, out IntPtr s)) return s;
            static WGPUAddressMode A(uint a)
            {
                if (a == 4) D3DLog.Once("d3d9border", "D3DTADDRESS_BORDER is drawn as clamp");
                if (a == 5) D3DLog.Once("d3d9mirroronce", "D3DTADDRESS_MIRRORONCE is drawn as mirror");
                return a switch { 2 or 5 => WGPUAddressMode.MirrorRepeat, 3 or 4 => WGPUAddressMode.ClampToEdge, _ => WGPUAddressMode.Repeat };
            }
            bool anisotropic = mag == 3 || min == 3;
            var d = new WGPUSamplerDescriptor
            {
                addressModeU = A(U), addressModeV = A(V), addressModeW = A(W),
                magFilter = mag == 1 ? WGPUFilterMode.Nearest : WGPUFilterMode.Linear,
                minFilter = min == 1 ? WGPUFilterMode.Nearest : WGPUFilterMode.Linear,
                mipmapFilter = mip == 2 ? WGPUMipmapFilterMode.Linear : WGPUMipmapFilterMode.Nearest,
                lodMinClamp = maxMip,
                lodMaxClamp = mip == 0 ? maxMip : 32f,
                maxAnisotropy = 1,
            };
            if (anisotropic && mip != 1)
            {
                d.magFilter = d.minFilter = WGPUFilterMode.Linear;
                d.mipmapFilter = WGPUMipmapFilterMode.Linear;
                d.maxAnisotropy = (ushort)Math.Clamp(aniso, 1u, 16u);
            }
            s = wgpuDeviceCreateSampler(Device11.Gpu.Device, &d);
            _samplers[key] = s;
            return s;
        }

        private IntPtr DrawGroup(D3D9Shader? vs, D3D9Shader? ps, SortedDictionary<int, (D3DTexture2D? Tex, int Fixup, int Type)> psTex, SortedDictionary<int, (D3DTexture2D? Tex, int Fixup, int Type)> vsTex)
        {
            var (bgl, _) = Layout(vs, ps, psTex, vsTex);
            var e = new List<WGPUBindGroupEntry>();
            if (vs != null) e.Add(new WGPUBindGroupEntry { binding = 0, buffer = _uniformGpu, offset = (ulong)_vsConstAt, size = D3D9Programs.ConstsSize });
            if (ps != null) e.Add(new WGPUBindGroupEntry { binding = 1, buffer = _uniformGpu, offset = (ulong)_psConstAt, size = D3D9Programs.ConstsSize });
            e.Add(new WGPUBindGroupEntry { binding = D3D9Programs.MiscBinding, buffer = _uniformGpu, offset = (ulong)_miscAt, size = D3D9Programs.MiscSize });
            void Tex(int baseBinding, int s, int samplerIndex, (D3DTexture2D? Tex, int Fixup, int Type) t)
            {
                e.Add(new WGPUBindGroupEntry { binding = (uint)(baseBinding + 2 * s), textureView = t.Tex != null ? SampleView(t.Tex) : Dummy(t.Type) });
                e.Add(new WGPUBindGroupEntry { binding = (uint)(baseBinding + 2 * s + 1), sampler = Sampler(samplerIndex, Unfilterable(t.Tex)) });
            }
            foreach (var (s, t) in psTex) Tex(D3D9Programs.PsTextureBase, s, s, t);
            foreach (var (s, t) in vsTex) Tex(D3D9Programs.VsTextureBase, s, 16 + s, t);
            var arr = e.ToArray();
            fixed (WGPUBindGroupEntry* p = arr)
            {
                var d = new WGPUBindGroupDescriptor { layout = bgl, entryCount = (nuint)arr.Length, entries = p };
                return wgpuDeviceCreateBindGroup(Device11.Gpu.Device, &d);
            }
        }

        private static WGPUBlendFactor Factor(uint b, bool noDstAlpha) => b switch
        {
            1 => WGPUBlendFactor.Zero, 2 => WGPUBlendFactor.One, 3 => WGPUBlendFactor.Src, 4 => WGPUBlendFactor.OneMinusSrc,
            5 => WGPUBlendFactor.SrcAlpha, 6 => WGPUBlendFactor.OneMinusSrcAlpha,
            7 => noDstAlpha ? WGPUBlendFactor.One : WGPUBlendFactor.DstAlpha, 8 => noDstAlpha ? WGPUBlendFactor.Zero : WGPUBlendFactor.OneMinusDstAlpha,
            9 => WGPUBlendFactor.Dst, 10 => WGPUBlendFactor.OneMinusDst, 11 => WGPUBlendFactor.SrcAlphaSaturated,
            14 => (WGPUBlendFactor)0xC, 15 => (WGPUBlendFactor)0xD,
            _ => WGPUBlendFactor.One,
        };

        private static WGPUStencilOperation StencilOp(uint op) => (WGPUStencilOperation)(op switch { 1 => 1, 2 => 2, 3 => 3, 4 => 5, 5 => 6, 6 => 4, 7 => 7, 8 => 8, _ => 1 });

        private static WGPUCompareFunction Compare(uint f) => (WGPUCompareFunction)Math.Clamp(f, 1u, 8u);

        private IntPtr DrawPipeline(string vsId, string psId, IntPtr vsModule, IntPtr psModule, TargetSet ts, WGPUPrimitiveTopology topology, WGPUIndexFormat stripFormat,
            string layoutKey, SortedDictionary<int, List<(int Loc, D3DVERTEXELEMENT9 E)>> streams, Dictionary<int, uint> strides, uint instances,
            D3D9Shader? vs, D3D9Shader? ps, SortedDictionary<int, (D3DTexture2D? Tex, int Fixup, int Type)> psTex, SortedDictionary<int, (D3DTexture2D? Tex, int Fixup, int Type)> vsTex,
            bool points)
        {
            uint[] rs = State.RS;
            var key = new StringBuilder(512);
            key.Append(vsId).Append('#').Append(psId).Append('#').Append(layoutKey).Append('#').Append((int)topology).Append((int)stripFormat).Append('#').Append(LayoutKey(vs, ps, psTex, vsTex)).Append(points ? "#pt" : "");
            for (int i = 0; i < 4; i++) key.Append('|').Append(ts.Color[i] == null ? 0 : (int)ts.Color[i]!.Gpu!.Format).Append(ts.Color[i] != null && ts.Color[i]!.Desc.Format is D3D9Const.D3DFMT_X8R8G8B8 or D3D9Const.D3DFMT_X8B8G8R8 ? "x" : "");
            key.Append("|d").Append(ts.Depth == null ? 0 : (int)ts.Depth.Gpu!.Format);
            foreach (int s in new[] { D3DRS.CULLMODE, D3DRS.ALPHABLENDENABLE, D3DRS.SRCBLEND, D3DRS.DESTBLEND, D3DRS.BLENDOP, D3DRS.SEPARATEALPHABLENDENABLE, D3DRS.SRCBLENDALPHA, D3DRS.DESTBLENDALPHA, D3DRS.BLENDOPALPHA,
                D3DRS.COLORWRITEENABLE, D3DRS.COLORWRITEENABLE1, D3DRS.COLORWRITEENABLE2, D3DRS.COLORWRITEENABLE3 })
                key.Append(',').Append(rs[s]);
            if (ts.Depth != null)
                foreach (int s in new[] { D3DRS.ZENABLE, D3DRS.ZWRITEENABLE, D3DRS.ZFUNC, D3DRS.STENCILENABLE, D3DRS.STENCILFAIL, D3DRS.STENCILZFAIL, D3DRS.STENCILPASS, D3DRS.STENCILFUNC,
                    D3DRS.STENCILMASK, D3DRS.STENCILWRITEMASK, D3DRS.TWOSIDEDSTENCILMODE, D3DRS.CCW_STENCILFAIL, D3DRS.CCW_STENCILZFAIL, D3DRS.CCW_STENCILPASS, D3DRS.CCW_STENCILFUNC, D3DRS.DEPTHBIAS, D3DRS.SLOPESCALEDEPTHBIAS })
                    key.Append(',').Append(rs[s]);
            string k = key.ToString();
            if (_pipelines.TryGetValue(k, out IntPtr cached)) return cached;

            var (_, pl) = Layout(vs, ps, psTex, vsTex);
            var handles = new List<GCHandle>();
            try
            {
                var buffers = new List<WGPUVertexBufferLayout>();
                foreach (var (stream, list) in streams)
                {
                    var attrs = list.Select(x => new WGPUVertexAttribute { format = D3D9Programs.Attribute(x.E.Type, "").Format, offset = x.E.Offset, shaderLocation = (uint)x.Loc }).ToArray();
                    var h = GCHandle.Alloc(attrs, GCHandleType.Pinned);
                    handles.Add(h);
                    bool perInstance = points || (instances > 1 && (State.StreamFreq[stream] & 0x80000000) != 0);
                    buffers.Add(new WGPUVertexBufferLayout
                    {
                        stepMode = perInstance ? WGPUVertexStepMode.Instance : WGPUVertexStepMode.Vertex,
                        arrayStride = strides[stream], attributeCount = (nuint)attrs.Length, attributes = (WGPUVertexAttribute*)h.AddrOfPinnedObject(),
                    });
                }
                var bufArr = buffers.ToArray();
                int count = 0;
                for (int i = 0; i < 4; i++) if (ts.Color[i] != null) count = i + 1;
                var targets = stackalloc WGPUColorTargetState[4];
                var blends = stackalloc WGPUBlendState[4];
                bool blend = rs[D3DRS.ALPHABLENDENABLE] != 0;
                for (int i = 0; i < count; i++)
                {
                    var s = ts.Color[i];
                    if (s == null) { targets[i] = new WGPUColorTargetState { format = WGPUTextureFormat.Undefined }; continue; }
                    bool noDstAlpha = s.Desc.Format is D3D9Const.D3DFMT_X8R8G8B8 or D3D9Const.D3DFMT_X8B8G8R8;
                    uint mask = (i == 0 ? rs[D3DRS.COLORWRITEENABLE] : rs[D3DRS.COLORWRITEENABLE1 + i - 1]) & 0xF;
                    targets[i] = new WGPUColorTargetState { format = s.Gpu!.Format, writeMask = mask };
                    if (!blend) continue;
                    uint src = rs[D3DRS.SRCBLEND], dst = rs[D3DRS.DESTBLEND], op = rs[D3DRS.BLENDOP];
                    if (src == 12) { src = 5; dst = 6; } else if (src == 13) { src = 6; dst = 5; }      // BOTHSRCALPHA, BOTHINVSRCALPHA
                    uint srcA = src, dstA = dst, opA = op;
                    if (rs[D3DRS.SEPARATEALPHABLENDENABLE] != 0) { srcA = rs[D3DRS.SRCBLENDALPHA]; dstA = rs[D3DRS.DESTBLENDALPHA]; opA = rs[D3DRS.BLENDOPALPHA]; }
                    blends[i].color = new WGPUBlendComponent { operation = (WGPUBlendOperation)Math.Clamp(op, 1u, 5u), srcFactor = Factor(src, noDstAlpha), dstFactor = Factor(dst, noDstAlpha) };
                    blends[i].alpha = new WGPUBlendComponent { operation = (WGPUBlendOperation)Math.Clamp(opA, 1u, 5u), srcFactor = Factor(srcA, noDstAlpha), dstFactor = Factor(dstA, noDstAlpha) };
                    if (op is 4 or 5) blends[i].color.srcFactor = blends[i].color.dstFactor = WGPUBlendFactor.One;
                    if (opA is 4 or 5) blends[i].alpha.srcFactor = blends[i].alpha.dstFactor = WGPUBlendFactor.One;
                    targets[i].blend = &blends[i];
                }
                byte* main = stackalloc byte[4] { (byte)'m', (byte)'a', (byte)'i', (byte)'n' };
                var frag = new WGPUFragmentState { module = psModule, entryPoint = new WGPUStringView { data = main, length = 4 }, targetCount = (nuint)count, targets = targets };
                var ds = new WGPUDepthStencilState();
                if (ts.Depth != null)
                {
                    var f = ts.Depth.Gpu!.Format;
                    bool z = rs[D3DRS.ZENABLE] != 0;
                    ds.format = f;
                    ds.depthWriteEnabled = z && rs[D3DRS.ZWRITEENABLE] != 0 ? WGPUOptionalBool.True : WGPUOptionalBool.False;
                    ds.depthCompare = z ? Compare(rs[D3DRS.ZFUNC]) : WGPUCompareFunction.Always;
                    bool stencil = Formats.HasStencil(f) && rs[D3DRS.STENCILENABLE] != 0;
                    var keep = new WGPUStencilFaceState { compare = WGPUCompareFunction.Always, failOp = WGPUStencilOperation.Keep, depthFailOp = WGPUStencilOperation.Keep, passOp = WGPUStencilOperation.Keep };
                    var front = stencil ? new WGPUStencilFaceState { compare = Compare(rs[D3DRS.STENCILFUNC]), failOp = StencilOp(rs[D3DRS.STENCILFAIL]), depthFailOp = StencilOp(rs[D3DRS.STENCILZFAIL]), passOp = StencilOp(rs[D3DRS.STENCILPASS]) } : keep;
                    var back = stencil && rs[D3DRS.TWOSIDEDSTENCILMODE] != 0
                        ? new WGPUStencilFaceState { compare = Compare(rs[D3DRS.CCW_STENCILFUNC]), failOp = StencilOp(rs[D3DRS.CCW_STENCILFAIL]), depthFailOp = StencilOp(rs[D3DRS.CCW_STENCILZFAIL]), passOp = StencilOp(rs[D3DRS.CCW_STENCILPASS]) }
                        : front;
                    ds.stencilFront = front;
                    ds.stencilBack = back;
                    ds.stencilReadMask = stencil ? rs[D3DRS.STENCILMASK] & 0xFF : 0;
                    ds.stencilWriteMask = stencil ? rs[D3DRS.STENCILWRITEMASK] & 0xFF : 0;
                    // D3D9's depth bias is in depth units; WebGPU's in the format's smallest step
                    float bias = BitConverter.UInt32BitsToSingle(rs[D3DRS.DEPTHBIAS]);
                    float steps = f == TexFmt.Depth16Unorm ? 65535f : f == TexFmt.Depth32Float ? 8388608f : 16777215f;
                    ds.depthBias = (int)MathF.Round(bias * steps);
                    ds.depthBiasSlopeScale = BitConverter.UInt32BitsToSingle(rs[D3DRS.SLOPESCALEDEPTHBIAS]);
                }
                uint cull = rs[D3DRS.CULLMODE];
                fixed (WGPUVertexBufferLayout* vb = bufArr)
                {
                    var desc = new WGPURenderPipelineDescriptor
                    {
                        layout = pl,
                        vertex = new WGPUVertexState { module = vsModule, entryPoint = new WGPUStringView { data = main, length = 4 }, bufferCount = (nuint)bufArr.Length, buffers = vb },
                        primitive = new WGPUPrimitiveState
                        {
                            topology = topology, stripIndexFormat = stripFormat, frontFace = WGPUFrontFace.CW,
                            cullMode = !points && topology is WGPUPrimitiveTopology.TriangleList or WGPUPrimitiveTopology.TriangleStrip
                                ? cull switch { 2 => WGPUCullMode.Front, 3 => WGPUCullMode.Back, _ => WGPUCullMode.None } : WGPUCullMode.None,
                        },
                        depthStencil = ts.Depth != null ? (IntPtr)(&ds) : IntPtr.Zero,
                        multisample = new WGPUMultisampleState { count = 1, mask = 0xFFFFFFFF },
                        fragment = &frag,
                    };
                    IntPtr p = CreatePipeline(&desc);
                    _pipelines[k] = p;
                    return p;
                }
            }
            finally
            {
                foreach (var h in handles) h.Free();
            }
        }
    }
}
