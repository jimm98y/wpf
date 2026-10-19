// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// ID3D11DeviceContext over WebGPU command encoders (Documentation/d3d11-webgpu-shim.md, section 5).
//
// The context keeps D3D11's state as D3D11 has it. A draw turns the state WebGPU bakes into a
// pipeline into a key and looks the pipeline up; everything else is set on the open render pass.
// One pass stays open while the bound targets do not change. Clears are remembered and become the
// next pass's load operations (or a pass of their own when nothing draws there first).
//
// A deferred context records each call (with the managed objects it names) and replays them on the
// immediate context in ExecuteCommandList, starting from the default state as D3D11 does.
//

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Microsoft.Wpf.Interop.WebGpu.Direct3D.Dxbc;
using static Microsoft.Wpf.Interop.WebGpu.Wgpu;

namespace Microsoft.Wpf.Interop.WebGpu.Direct3D
{
    internal sealed unsafe partial class D3DContext : D3DChild
    {
        public const int MaxVertexBuffers = 16, MaxConstantBuffers = 14, MaxResources = 64, MaxSamplers = 16, MaxTargets = 8, MaxViewports = 16;

        public readonly bool Deferred;

        // ---- state -------------------------------------------------------------------------------

        private sealed class StageState
        {
            public D3DShader? Shader;
            public readonly D3DBuffer?[] ConstantBuffers = new D3DBuffer?[MaxConstantBuffers];
            public readonly D3DShaderResourceView?[] Resources = new D3DShaderResourceView?[MaxResources];
            public readonly D3DSamplerState?[] Samplers = new D3DSamplerState?[MaxSamplers];
        }

        private readonly StageState _vs = new(), _ps = new(), _gs = new();
        private D3DInputLayout? _layout;
        private D3D_PRIMITIVE_TOPOLOGY _topology;
        private readonly D3DBuffer?[] _vb = new D3DBuffer?[MaxVertexBuffers];
        private readonly uint[] _vbStride = new uint[MaxVertexBuffers], _vbOffset = new uint[MaxVertexBuffers];
        private D3DBuffer? _ib;
        private DXGI_FORMAT _ibFormat;
        private uint _ibOffset;
        private D3DRasterizerState? _raster;
        private readonly D3D11_VIEWPORT[] _viewports = new D3D11_VIEWPORT[MaxViewports];
        private int _viewportCount;
        private readonly D3D11_RECT[] _scissors = new D3D11_RECT[MaxViewports];
        private int _scissorCount;
        private readonly D3DRenderTargetView?[] _rtv = new D3DRenderTargetView?[MaxTargets];
        private D3DDepthStencilView? _dsv;
        private D3DBlendState? _blend;
        private WGPUColor _blendFactor = new() { r = 1, g = 1, b = 1, a = 1 };
        private uint _sampleMask = 0xFFFFFFFF;
        private D3DDepthStencilState? _depth;
        private uint _stencilRef;

        private static readonly Vtable s_vt = Build();

        public D3DContext(D3DDevice device, bool deferred) : base(device, s_vt)
        {
            Deferred = deferred;
            if (!deferred)
            {
                // the immediate context is part of the device, not a child that keeps it alive
                device.InternalRelease();
            }
        }

        private bool _shutdown;

        /// <summary>The device is going: drop everything the immediate context holds.</summary>
        public void FinalShutdown()
        {
            if (_shutdown) return;
            _shutdown = true;
            try { FlushToGpu(wait: true); } catch (Exception e) { D3DLog.Error("final flush", e); }
            ClearStateImpl();
            ReleaseGpuCaches();
        }

        protected override void OnFinalRelease()
        {
            if (Deferred)
            {
                ClearStateImpl();
                foreach (var o in _recordedRefs) o.InternalRelease();
                _recordedRefs.Clear();
                _recorded.Clear();
                base.OnFinalRelease();
            }
            // the immediate context goes with its device (FinalShutdown)
        }

        // ---- binding helpers (internal references, as D3D11's bound objects keep theirs) -------------

        private static void Bind<T>(ref T? slot, T? value) where T : ComObject
        {
            if (ReferenceEquals(slot, value)) return;
            value?.InternalAddRef();
            slot?.InternalRelease();
            slot = value;
        }

        private void ClearStateImpl()
        {
            foreach (var s in new[] { _vs, _ps, _gs })
            {
                Bind(ref s.Shader, null);
                for (int i = 0; i < s.ConstantBuffers.Length; i++) Bind(ref s.ConstantBuffers[i], null);
                for (int i = 0; i < s.Resources.Length; i++) Bind(ref s.Resources[i], null);
                for (int i = 0; i < s.Samplers.Length; i++) Bind(ref s.Samplers[i], null);
            }
            Bind(ref _layout, null);
            for (int i = 0; i < MaxVertexBuffers; i++) { Bind(ref _vb[i], null); _vbStride[i] = _vbOffset[i] = 0; }
            Bind(ref _ib, null);
            _ibFormat = DXGI_FORMAT.UNKNOWN; _ibOffset = 0;
            _topology = D3D_PRIMITIVE_TOPOLOGY.UNDEFINED;
            Bind(ref _raster, null);
            _viewportCount = 0; _scissorCount = 0;
            for (int i = 0; i < MaxTargets; i++) Bind(ref _rtv[i], null);
            Bind(ref _dsv, null);
            Bind(ref _blend, null);
            _blendFactor = new WGPUColor { r = 1, g = 1, b = 1, a = 1 };
            _sampleMask = 0xFFFFFFFF;
            Bind(ref _depth, null);
            _stencilRef = 0;
            ClearComputeState();
        }

        private StageState Stage(ShaderStage s) => s switch
        {
            ShaderStage.Vertex => _vs, ShaderStage.Pixel => _ps, ShaderStage.Geometry => _gs,
            ShaderStage.Compute => _cs, ShaderStage.Hull => _hs, ShaderStage.Domain => _ds,
            _ => throw new NotSupportedException($"{s} stage"),
        };

        // ---- deferred recording ----------------------------------------------------------------------

        private readonly List<Action<D3DContext>> _recorded = new();
        private readonly List<ComObject> _recordedRefs = new();

        private void Run(Action<D3DContext> a, params ComObject?[] refs)
        {
            if (!Deferred) { a(this); return; }
            foreach (var r in refs) if (r != null) { r.InternalAddRef(); _recordedRefs.Add(r); }
            _recorded.Add(a);
        }

        private void RunMany(Action<D3DContext> a, ComObject?[] refs) => Run(a, refs);

        public D3DCommandList Finish()
        {
            var list = new D3DCommandList(Device, _recorded.ToArray(), _recordedRefs.ToArray());
            _recorded.Clear();
            _recordedRefs.Clear();          // ownership moves to the list
            ClearStateImpl();
            return list;
        }

        public void Execute(D3DCommandList list, bool restore)
        {
            // D3D11 starts a command list from the default state and leaves the default state (or
            // the saved one) behind.
            var saved = restore ? SaveState() : null;
            ClearStateImpl();
            foreach (var a in list.Commands) a(this);
            ClearStateImpl();
            if (saved != null) RestoreState(saved);
        }

        private sealed class SavedState
        {
            public required Action<D3DContext> Restore;
            public required ComObject[] Refs;
        }

        private SavedState SaveState()
        {
            // every bound object, re-bound through the same setters on restore
            var refs = new List<ComObject>();
            void Hold(ComObject? o) { if (o != null) { o.InternalAddRef(); refs.Add(o); } }
            var stages = new[] { _vs, _ps, _gs };
            var copies = new (D3DShader? sh, D3DBuffer?[] cb, D3DShaderResourceView?[] srv, D3DSamplerState?[] smp)[3];
            for (int i = 0; i < 3; i++)
            {
                var s = stages[i];
                copies[i] = (s.Shader, (D3DBuffer?[])s.ConstantBuffers.Clone(), (D3DShaderResourceView?[])s.Resources.Clone(), (D3DSamplerState?[])s.Samplers.Clone());
                Hold(s.Shader); foreach (var o in s.ConstantBuffers) Hold(o); foreach (var o in s.Resources) Hold(o); foreach (var o in s.Samplers) Hold(o);
            }
            var layout = _layout; Hold(layout);
            var topology = _topology;
            var vb = (D3DBuffer?[])_vb.Clone(); foreach (var o in vb) Hold(o);
            var stride = (uint[])_vbStride.Clone(); var off = (uint[])_vbOffset.Clone();
            var ib = _ib; Hold(ib); var ibf = _ibFormat; var ibo = _ibOffset;
            var raster = _raster; Hold(raster);
            var vps = (D3D11_VIEWPORT[])_viewports.Clone(); int vpn = _viewportCount;
            var scs = (D3D11_RECT[])_scissors.Clone(); int scn = _scissorCount;
            var rtv = (D3DRenderTargetView?[])_rtv.Clone(); foreach (var o in rtv) Hold(o);
            var dsv = _dsv; Hold(dsv);
            var blend = _blend; Hold(blend); var bf = _blendFactor; var sm = _sampleMask;
            var depth = _depth; Hold(depth); var sr = _stencilRef;
            return new SavedState
            {
                Refs = refs.ToArray(),
                Restore = c =>
                {
                    var st = new[] { c._vs, c._ps, c._gs };
                    for (int i = 0; i < 3; i++)
                    {
                        Bind(ref st[i].Shader, copies[i].sh);
                        for (int k = 0; k < MaxConstantBuffers; k++) Bind(ref st[i].ConstantBuffers[k], copies[i].cb[k]);
                        for (int k = 0; k < MaxResources; k++) Bind(ref st[i].Resources[k], copies[i].srv[k]);
                        for (int k = 0; k < MaxSamplers; k++) Bind(ref st[i].Samplers[k], copies[i].smp[k]);
                    }
                    Bind(ref c._layout, layout); c._topology = topology;
                    for (int k = 0; k < MaxVertexBuffers; k++) { Bind(ref c._vb[k], vb[k]); c._vbStride[k] = stride[k]; c._vbOffset[k] = off[k]; }
                    Bind(ref c._ib, ib); c._ibFormat = ibf; c._ibOffset = ibo;
                    Bind(ref c._raster, raster);
                    Array.Copy(vps, c._viewports, vps.Length); c._viewportCount = vpn;
                    Array.Copy(scs, c._scissors, scs.Length); c._scissorCount = scn;
                    for (int k = 0; k < MaxTargets; k++) Bind(ref c._rtv[k], rtv[k]);
                    Bind(ref c._dsv, dsv);
                    Bind(ref c._blend, blend); c._blendFactor = bf; c._sampleMask = sm;
                    Bind(ref c._depth, depth); c._stencilRef = sr;
                },
            };
        }

        private void RestoreState(SavedState s)
        {
            s.Restore(this);
            foreach (var o in s.Refs) o.InternalRelease();
        }

        // ---- state setters -------------------------------------------------------------------------

        public void SetShader(ShaderStage stage, D3DShader? shader) => Run(c => Bind(ref c.Stage(stage).Shader, shader), shader);

        public void SetConstantBuffers(ShaderStage stage, int start, D3DBuffer?[] buffers)
            => Run(c => { var s = c.Stage(stage); for (int i = 0; i < buffers.Length && start + i < MaxConstantBuffers; i++) Bind(ref s.ConstantBuffers[start + i], buffers[i]); }, buffers);

        public void SetResources(ShaderStage stage, int start, D3DShaderResourceView?[] views)
            => Run(c => { var s = c.Stage(stage); for (int i = 0; i < views.Length && start + i < MaxResources; i++) Bind(ref s.Resources[start + i], views[i]); }, views);

        public void SetSamplers(ShaderStage stage, int start, D3DSamplerState?[] samplers)
            => Run(c => { var s = c.Stage(stage); for (int i = 0; i < samplers.Length && start + i < MaxSamplers; i++) Bind(ref s.Samplers[start + i], samplers[i]); }, samplers);

        public void SetInputLayout(D3DInputLayout? layout) => Run(c => Bind(ref c._layout, layout), layout);

        public void SetTopology(D3D_PRIMITIVE_TOPOLOGY t) => Run(c => c._topology = t);

        public void SetVertexBuffers(int start, D3DBuffer?[] buffers, uint[] strides, uint[] offsets)
            => Run(c =>
            {
                for (int i = 0; i < buffers.Length && start + i < MaxVertexBuffers; i++)
                {
                    Bind(ref c._vb[start + i], buffers[i]);
                    c._vbStride[start + i] = strides[i];
                    c._vbOffset[start + i] = offsets[i];
                }
            }, buffers);

        public void SetIndexBuffer(D3DBuffer? buffer, DXGI_FORMAT format, uint offset)
            => Run(c => { Bind(ref c._ib, buffer); c._ibFormat = format; c._ibOffset = offset; }, buffer);

        public void SetRasterizer(D3DRasterizerState? s) => Run(c => Bind(ref c._raster, s), s);

        public void SetViewports(D3D11_VIEWPORT[] v) => Run(c => { Array.Copy(v, c._viewports, Math.Min(v.Length, MaxViewports)); c._viewportCount = Math.Min(v.Length, MaxViewports); });

        public void SetScissors(D3D11_RECT[] r) => Run(c => { Array.Copy(r, c._scissors, Math.Min(r.Length, MaxViewports)); c._scissorCount = Math.Min(r.Length, MaxViewports); });

        public void SetTargets(D3DRenderTargetView?[] rtvs, D3DDepthStencilView? dsv)
        {
            var refs = new ComObject?[rtvs.Length + 1];
            Array.Copy(rtvs, refs, rtvs.Length);
            refs[^1] = dsv;
            Run(c =>
            {
                for (int i = 0; i < MaxTargets; i++) Bind(ref c._rtv[i], i < rtvs.Length ? rtvs[i] : null);
                Bind(ref c._dsv, dsv);
            }, refs);
        }

        public void SetBlend(D3DBlendState? s, WGPUColor factor, uint mask) => Run(c => { Bind(ref c._blend, s); c._blendFactor = factor; c._sampleMask = mask; }, s);

        public void SetDepthStencil(D3DDepthStencilState? s, uint stencilRef) => Run(c => { Bind(ref c._depth, s); c._stencilRef = stencilRef; }, s);

        public void ClearState() => Run(c => c.ClearStateImpl());

        // ---- GPU work ------------------------------------------------------------------------------

        private IntPtr _encoder;
        private IntPtr _pass;
        private readonly IntPtr[] _passColor = new IntPtr[MaxTargets];
        private IntPtr _passDepth;
        private readonly Dictionary<IntPtr, WGPUColor> _pendingColorClears = new();
        private readonly Dictionary<IntPtr, (float? Depth, uint? Stencil, bool HasStencil)> _pendingDepthClears = new();
        private int _submissions;

        private IntPtr Encoder()
        {
            if (_encoder == IntPtr.Zero) _encoder = wgpuDeviceCreateCommandEncoder(Device.Gpu.Device, IntPtr.Zero);
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

        /// <summary>Ends the pass and applies the clears no draw has taken up (each a pass of its own).</summary>
        private void ResolvePendingClears()
        {
            EndPass();
            if (_pendingColorClears.Count == 0 && _pendingDepthClears.Count == 0) return;
            foreach (var (view, color) in _pendingColorClears)
            {
                var att = new WGPURenderPassColorAttachment { view = view, depthSlice = 0xFFFFFFFF, loadOp = WGPULoadOp.Clear, storeOp = WGPUStoreOp.Store, clearValue = color };
                var desc = new WGPURenderPassDescriptor { colorAttachmentCount = 1, colorAttachments = &att };
                IntPtr p = wgpuCommandEncoderBeginRenderPass(Encoder(), &desc);
                wgpuRenderPassEncoderEnd(p);
                wgpuRenderPassEncoderRelease(p);
            }
            foreach (var (view, c) in _pendingDepthClears)
            {
                var ds = DepthAttachment(view, c, readOnly: false);
                var desc = new WGPURenderPassDescriptor { depthStencilAttachment = (IntPtr)(&ds) };
                IntPtr p = wgpuCommandEncoderBeginRenderPass(Encoder(), &desc);
                wgpuRenderPassEncoderEnd(p);
                wgpuRenderPassEncoderRelease(p);
            }
            _pendingColorClears.Clear();
            _pendingDepthClears.Clear();
        }

        private static WGPURenderPassDepthStencilAttachment DepthAttachment(IntPtr view, (float? Depth, uint? Stencil, bool HasStencil) clear, bool readOnly)
        {
            var ds = new WGPURenderPassDepthStencilAttachment { view = view };
            ds.depthLoadOp = clear.Depth != null ? WGPULoadOp.Clear : WGPULoadOp.Load;
            ds.depthStoreOp = WGPUStoreOp.Store;
            ds.depthClearValue = clear.Depth ?? 1f;
            if (clear.HasStencil)
            {
                ds.stencilLoadOp = clear.Stencil != null ? WGPULoadOp.Clear : WGPULoadOp.Load;
                ds.stencilStoreOp = WGPUStoreOp.Store;
                ds.stencilClearValue = clear.Stencil ?? 0;
            }
            return ds;
        }

        /// <summary>Submits everything recorded. <paramref name="wait"/>: until the GPU is done.</summary>
        public void FlushToGpu(bool wait)
        {
            if (Deferred) return;
            ResolvePendingClears();
            if (_encoder != IntPtr.Zero)
            {
                IntPtr cmd = wgpuCommandEncoderFinish(_encoder, IntPtr.Zero);
                wgpuCommandEncoderRelease(_encoder);
                _encoder = IntPtr.Zero;
                wgpuQueueSubmit(Device.Gpu.Queue, 1, &cmd);
                wgpuCommandBufferRelease(cmd);
                _submissions++;
                _arenaOffset = 0;
                _arenaIndex = (_arenaIndex + 1) % _arenas.Length;
            }
            if (wait) wgpuDevicePoll(Device.Gpu.Device, WGPU_TRUE, null);
        }

        /// <summary>A write to a GPU buffer that must land after what was recorded before it.</summary>
        public void WriteBufferOrdered(IntPtr buffer, uint offset, ReadOnlySpan<byte> data)
        {
            if (_encoder != IntPtr.Zero || _pendingColorClears.Count > 0 || _pendingDepthClears.Count > 0) FlushToGpu(wait: false);
            int len = (data.Length + 3) & ~3;
            if (len == data.Length) { Device.Gpu.WriteBuffer(buffer, offset, data); return; }
            var tmp = new byte[len];
            data.CopyTo(tmp);
            Device.Gpu.WriteBuffer(buffer, offset, tmp);
        }

        // ---- mapping -------------------------------------------------------------------------------

        private sealed class MapState { public volatile bool Done; public volatile bool Ok; }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvCdecl) })]
        private static void OnMapped(WGPUMapAsyncStatus status, WGPUStringView message, IntPtr u1, IntPtr u2)
        {
            var s = (MapState?)GCHandle.FromIntPtr(u1).Target;
            if (s == null) return;
            s.Ok = status == WGPUMapAsyncStatus.Success;
            s.Done = true;
        }

        /// <summary>Maps a staging buffer, waiting for the GPU unless <paramref name="doNotWait"/>
        /// (then DXGI_ERROR_WAS_STILL_DRAWING while it is busy).</summary>
        public void* MapGpuBuffer(IntPtr buffer, ulong size, bool read, bool doNotWait, out int hr)
        {
            var state = new MapState();
            var h = GCHandle.Alloc(state);
            try
            {
                var info = new WGPUBufferMapCallbackInfo
                {
                    mode = WGPUCallbackMode.AllowProcessEvents,
                    callback = (IntPtr)(delegate* unmanaged[Cdecl]<WGPUMapAsyncStatus, WGPUStringView, IntPtr, IntPtr, void>)&OnMapped,
                    userdata1 = GCHandle.ToIntPtr(h),
                };
                ulong mapSize = (size + 3) & ~3UL;
                wgpuBufferMapAsync(buffer, read ? WGPUMapMode.Read : WGPUMapMode.Write, 0, (nuint)mapSize, info);
                for (int i = 0; i < 100000 && !state.Done; i++) wgpuDevicePoll(Device.Gpu.Device, doNotWait ? 0u : WGPU_TRUE, null);
                if (!state.Done || !state.Ok)
                {
                    hr = state.Done ? HResult.E_FAIL : HResult.DXGI_ERROR_WAS_STILL_DRAWING;
                    return null;
                }
                hr = HResult.S_OK;
                return read ? wgpuBufferGetConstMappedRange(buffer, 0, (nuint)mapSize) : wgpuBufferGetMappedRange(buffer, 0, (nuint)mapSize);
            }
            finally { h.Free(); }
        }

        // ---- clears and copies ------------------------------------------------------------------------

        public void ClearRtv(D3DRenderTargetView view, WGPUColor color) => Run(c =>
        {
            int i = Array.IndexOf(c._passColor, view.View);
            if (c._pass != IntPtr.Zero && i >= 0) c.EndPass();
            c._pendingColorClears[view.View] = color;
        }, view);

        public void ClearDsv(D3DDepthStencilView view, uint flags, float depth, byte stencil) => Run(c =>
        {
            if (c._pass != IntPtr.Zero && c._passDepth == view.View) c.EndPass();
            bool hasStencil = Formats.HasStencil(view.Texture.Format);
            c._pendingDepthClears.TryGetValue(view.View, out var old);
            float? d = (flags & 1) != 0 ? depth : old.Depth;
            uint? s = (flags & 2) != 0 && hasStencil ? stencil : old.Stencil;
            c._pendingDepthClears[view.View] = (d, s, hasStencil);
        }, view);

        public void CopyResource(D3DResource dst, D3DResource src) => Run(c => c.CopyImpl(dst, 0, 0, 0, 0, src, 0, null, whole: true), dst, src);

        public void CopySubresourceRegion(D3DResource dst, uint dstSub, uint x, uint y, uint z, D3DResource src, uint srcSub, D3D11_BOX? box)
            => Run(c => c.CopyImpl(dst, dstSub, x, y, z, src, srcSub, box, whole: false), dst, src);

        private void CopyImpl(D3DResource dst, uint dstSub, uint dx, uint dy, uint dz, D3DResource src, uint srcSub, D3D11_BOX? box, bool whole)
        {
            ResolvePendingClears();
            IntPtr enc = Encoder();
            if (dst is D3DBuffer db && src is D3DBuffer sb)
            {
                uint from = box?.left ?? 0, to = box?.right ?? sb.Size;
                ulong n = Math.Min(to - from, db.Size - dx);
                if (sb.IsConstant || db.IsConstant)
                {
                    // constant buffers live on the CPU
                    if (sb.Shadow != null && db.Shadow != null) Buffer.MemoryCopy(sb.Shadow + from, db.Shadow + dx, db.Size - dx, n);
                    else if (db.Shadow != null) D3DLog.Once("cb-copy", "copy from a GPU buffer into a constant buffer is not supported");
                    db.Version++;
                    return;
                }
                n &= ~3UL;
                if (n > 0) wgpuCommandEncoderCopyBufferToBuffer(enc, sb.Gpu, from, db.Gpu, dx, n);
                return;
            }
            if (dst is D3DTexture2D dt && src is D3DTexture2D st)
            {
                int subs = whole ? dt.SubresourceOffset.Length : 1;
                for (int i = 0; i < subs; i++)
                {
                    uint ds = whole ? (uint)i : dstSub, ss = whole ? (uint)i : srcSub;
                    var (dm, dl) = dt.Split(ds);
                    var (sm, sl) = st.Split(ss);
                    uint w = Math.Max(1, st.Desc.Width >> (int)sm), h = Math.Max(1, st.Desc.Height >> (int)sm);
                    uint sx = 0, sy = 0;
                    if (box is { } b) { sx = b.left; sy = b.top; w = b.right - b.left; h = b.bottom - b.top; }
                    if (Formats.IsBlockCompressed(st.Desc.Format)) { w = (w + 3) & ~3u; h = (h + 3) & ~3u; }
                    var size = new WGPUExtent3D { width = w, height = h, depthOrArrayLayers = 1 };
                    if (dt.IsStaging && !st.IsStaging)
                    {
                        if (st.SampleCount > 1) { D3DLog.Once("copy-ms", "copy from a multisampled texture"); return; }
                        var from = new WGPUTexelCopyTextureInfo { texture = st.Gpu, mipLevel = sm, origin = new WGPUOrigin3D { x = sx, y = sy, z = sl }, aspect = WGPUTextureAspect.All };
                        uint bpe = Formats.TexelBytes(dt.Desc.Format);
                        ulong rowOff = Formats.IsBlockCompressed(dt.Desc.Format) ? (ulong)(dy / 4) * dt.SubresourceRowPitch[ds] + dx / 4 * bpe : (ulong)dy * dt.SubresourceRowPitch[ds] + dx * bpe;
                        var to = new WGPUTexelCopyBufferInfo
                        {
                            buffer = dt.Staging,
                            layout = new WGPUTexelCopyBufferLayout { offset = dt.SubresourceOffset[ds] + rowOff, bytesPerRow = dt.SubresourceRowPitch[ds], rowsPerImage = dt.SubresourceRows[ds] },
                        };
                        wgpuCommandEncoderCopyTextureToBuffer(enc, &from, &to, &size);
                    }
                    else if (!dt.IsStaging && st.IsStaging)
                    {
                        uint bpe = Formats.TexelBytes(st.Desc.Format);
                        var from = new WGPUTexelCopyBufferInfo
                        {
                            buffer = st.Staging,
                            layout = new WGPUTexelCopyBufferLayout { offset = st.SubresourceOffset[ss] + (ulong)sy * st.SubresourceRowPitch[ss] + sx * bpe, bytesPerRow = st.SubresourceRowPitch[ss], rowsPerImage = st.SubresourceRows[ss] },
                        };
                        var to = new WGPUTexelCopyTextureInfo { texture = dt.Gpu, mipLevel = dm, origin = new WGPUOrigin3D { x = dx, y = dy, z = dl }, aspect = WGPUTextureAspect.All };
                        wgpuCommandEncoderCopyBufferToTexture(enc, &from, &to, &size);
                    }
                    else if (!dt.IsStaging && !st.IsStaging)
                    {
                        if (st.SampleCount > 1 || dt.SampleCount > 1) { D3DLog.Once("copy-ms", "copy of a multisampled texture"); return; }
                        var from = new WGPUTexelCopyTextureInfo { texture = st.Gpu, mipLevel = sm, origin = new WGPUOrigin3D { x = sx, y = sy, z = sl }, aspect = WGPUTextureAspect.All };
                        var to = new WGPUTexelCopyTextureInfo { texture = dt.Gpu, mipLevel = dm, origin = new WGPUOrigin3D { x = dx, y = dy, z = dl }, aspect = WGPUTextureAspect.All };
                        wgpuCommandEncoderCopyTextureToTexture(enc, &from, &to, &size);
                    }
                    else
                    {
                        // staging to staging: both are mapped buffers; copy the subresource rows
                        ulong n = Math.Min((ulong)st.SubresourceRowPitch[ss] * st.SubresourceRows[ss], (ulong)dt.SubresourceRowPitch[ds] * dt.SubresourceRows[ds]);
                        wgpuCommandEncoderCopyBufferToBuffer(enc, st.Staging, st.SubresourceOffset[ss], dt.Staging, dt.SubresourceOffset[ds], n & ~3UL);
                    }
                }
                dt.Version++;
                return;
            }
            D3DLog.Once("copy-kinds", $"copy from {src.GetType().Name} to {dst.GetType().Name}");
        }

        public void UpdateSubresource(D3DResource dst, uint sub, D3D11_BOX* box, void* data, uint rowPitch, uint depthPitch)
        {
            // the data must be read now, deferred or not
            if (dst is D3DBuffer b)
            {
                uint off = box == null ? 0 : box->left, end = box == null ? b.Size : Math.Min(b.Size, box->right);
                if (end <= off || data == null) return;
                var bytes = new ReadOnlySpan<byte>(data, (int)(end - off)).ToArray();
                D3D11_BOX? bx = box == null ? null : *box;
                Run(c =>
                {
                    fixed (byte* p = bytes)
                    {
                        var bb = bx ?? default;
                        b.Update(c, bx == null ? null : &bb, p);
                    }
                }, b);
                return;
            }
            if (dst is D3DTexture2D t)
            {
                var (mip, _) = t.Split(sub);
                uint h = box != null ? box->bottom - box->top : Math.Max(1, t.Desc.Height >> (int)mip);
                if (Formats.IsBlockCompressed(t.Desc.Format)) h = (h + 3) / 4;
                uint w = box != null ? box->right - box->left : Math.Max(1, t.Desc.Width >> (int)mip);
                uint tight = (Formats.IsBlockCompressed(t.Desc.Format) ? (w + 3) / 4 : w) * Formats.TexelBytes(t.Desc.Format);
                if (rowPitch == 0) rowPitch = tight;
                var bytes = new ReadOnlySpan<byte>(data, (int)(rowPitch * (h - 1) + tight)).ToArray();
                D3D11_BOX? bx = box == null ? null : *box;
                Run(c =>
                {
                    c.FlushToGpu(wait: false);       // the write lands before what is recorded next, after what was recorded
                    fixed (byte* p = bytes)
                    {
                        var bb = bx ?? default;
                        t.WriteSubresource((int)sub, p, rowPitch, bx == null ? null : &bb);
                    }
                }, t);
            }
        }

        public void ResolveSubresource(D3DTexture2D dst, uint dstSub, D3DTexture2D src, uint srcSub, DXGI_FORMAT format) => Run(c =>
        {
            c.ResolvePendingClears();
            var (sm, sl) = src.Split(srcSub);
            var (dm, dl) = dst.Split(dstSub);
            var fmt = Formats.Texture(format == DXGI_FORMAT.UNKNOWN ? src.Desc.Format : format);
            IntPtr sv = D3DView.CreateTextureView(src.Gpu, fmt, WGPUTextureViewDimension._2D, sm, 1, sl, 1);
            IntPtr dv = D3DView.CreateTextureView(dst.Gpu, fmt, WGPUTextureViewDimension._2D, dm, 1, dl, 1);
            var att = new WGPURenderPassColorAttachment { view = sv, depthSlice = 0xFFFFFFFF, resolveTarget = dv, loadOp = WGPULoadOp.Load, storeOp = WGPUStoreOp.Store };
            var desc = new WGPURenderPassDescriptor { colorAttachmentCount = 1, colorAttachments = &att };
            IntPtr p = wgpuCommandEncoderBeginRenderPass(c.Encoder(), &desc);
            wgpuRenderPassEncoderEnd(p);
            wgpuRenderPassEncoderRelease(p);
            wgpuTextureViewRelease(sv);
            wgpuTextureViewRelease(dv);
            dst.Version++;
        }, dst, src);

        public void Map(D3DResource r, uint sub, D3D11_MAP type, uint flags, D3D11_MAPPED_SUBRESOURCE* mapped, out int hr)
        {
            if (Deferred && type != D3D11_MAP.WRITE_DISCARD) { hr = HResult.E_INVALIDARG; return; }
            hr = r.Map(this, sub, type, flags, mapped);
        }

        public void Unmap(D3DResource r, uint sub) => r.Unmap(this, sub);

        private void ReleaseGpuCaches()
        {
            EndPass();
            if (_encoder != IntPtr.Zero) { wgpuCommandEncoderRelease(_encoder); _encoder = IntPtr.Zero; }
            foreach (var p in _pipelines.Values) if (p.Pipeline != IntPtr.Zero) wgpuRenderPipelineRelease(p.Pipeline);
            _pipelines.Clear();
            foreach (var t in _translations.Values) { if (t.Vs != IntPtr.Zero) wgpuShaderModuleRelease(t.Vs); if (t.Ps != IntPtr.Zero) wgpuShaderModuleRelease(t.Ps); }
            _translations.Clear();
            for (int i = 0; i < _arenas.Length; i++) if (_arenas[i] != IntPtr.Zero) { wgpuBufferRelease(_arenas[i]); _arenas[i] = IntPtr.Zero; }
            if (_defaultSampler != IntPtr.Zero) { wgpuSamplerRelease(_defaultSampler); _defaultSampler = IntPtr.Zero; }
        }
    }

    internal sealed unsafe class D3DCommandList : D3DChild
    {
        public readonly Action<D3DContext>[] Commands;
        private readonly ComObject[] _refs;
        private static readonly Vtable s_vt = Build();

        private static Vtable Build()
        {
            var vt = new Vtable("ID3D11CommandList", Vtbl.ID3D11CommandList.SlotCount, Vtbl.ID3D11CommandList.IID, Vtbl.ID3D11DeviceChild.IID);
            FillDeviceChild(vt);
            vt.Set(Vtbl.ID3D11CommandList.GetContextFlags, (delegate* unmanaged<IntPtr, uint>)&GetContextFlags);
            return vt;
        }

        public D3DCommandList(D3DDevice d, Action<D3DContext>[] commands, ComObject[] refs) : base(d, s_vt)
        {
            Commands = commands;
            _refs = refs;
        }

        [UnmanagedCallersOnly] private static uint GetContextFlags(IntPtr self) => 0;

        protected override void OnFinalRelease()
        {
            foreach (var r in _refs) r.InternalRelease();
            base.OnFinalRelease();
        }
    }
}
