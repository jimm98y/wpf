// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// Occlusion queries over WebGPU's: the samples that passed the depth and stencil tests between a
// query's Begin and End, as D3D11's OCCLUSION / OCCLUSION_PREDICATE and D3D9's D3DQUERYTYPE_OCCLUSION
// count them.
//
// A WebGPU occlusion query lives inside one render pass, and one is open at a time; a D3D query
// spans passes (a target change, a clear) and several may overlap. So the time the queries are open
// is cut into segments: one WebGPU query from a pass's start, or a change of which D3D queries are
// open, to the pass's end or the next change. A segment counts for every D3D query open during it, and
// a D3D query's result is the sum of its segments'. The segments of a submission are resolved into a
// buffer when it is submitted and read back when an application asks for the result.
//
// WebGPU promises only "nonzero if a sample passed": Vulkan, D3D12 and GL count samples, as D3D does,
// but Metal (wgpu's visibility results are boolean there) answers 1 per segment that drew. A hidden
// object is 0 either way, which is what culling and predication read.
//

using System;
using System.Collections.Generic;
using Microsoft.Wpf.Interop.WebGpu.Composition;
using static Microsoft.Wpf.Interop.WebGpu.Wgpu;

namespace Microsoft.Wpf.Interop.WebGpu.Direct3D
{
    /// <summary>One application occlusion query: the segments it was open in.</summary>
    internal sealed class OcclusionQuery
    {
        internal readonly List<(OcclusionTracker.Batch Batch, int Index)> Parts = new();
        /// <summary>Open longer than the tracker had queries for: counted as visible.</summary>
        internal bool Overflowed;
        internal bool Open;

        /// <summary>The batch still recording holds a part of this query (submit before reading).</summary>
        internal bool InCurrentBatch(OcclusionTracker t) => Parts.Exists(p => p.Batch == t.Current);
    }

    internal sealed unsafe class OcclusionTracker : IDisposable
    {
        /// <summary>Segments per submission.</summary>
        public const uint Capacity = 4096;

        internal sealed class Batch
        {
            /// <summary>Queries used; index 0 is never one (on Metal a query at 0 resolves as nothing passed).</summary>
            public int Used = 1;
            public IntPtr Resolve, Read;
            public ulong[]? Values;
        }

        private readonly WgpuContext _gpu;
        private IntPtr _set;
        private readonly List<OcclusionQuery> _open = new();
        private IntPtr _pass;
        private bool _segmentOpen;
        internal Batch Current = new();

        public OcclusionTracker(WgpuContext gpu) => _gpu = gpu;

        /// <summary>The query set for a pass's descriptor: only once a query has been begun (a pass with
        /// one costs nothing, but none at all is the common case).</summary>
        public IntPtr QuerySetForPass => _set;

        private void EnsureSet()
        {
            if (_set != IntPtr.Zero) return;
            var d = new WGPUQuerySetDescriptor { type = WGPUQueryType_Occlusion, count = Capacity };
            _set = wgpuDeviceCreateQuerySet(_gpu.Device, &d);
        }

        public void PassBegun(IntPtr pass)
        {
            _pass = pass;
            OpenSegment();
        }

        /// <summary>Before the pass ends.</summary>
        public void PassEnding()
        {
            CloseSegment();
            _pass = IntPtr.Zero;
        }

        /// <summary>Begin: the query restarts (D3D: a Begin on an open query starts it over).</summary>
        /// <returns>Whether the query set was made just now: a pass the caller has open was begun without
        /// it, and must end (the next one counts).</returns>
        public bool Begin(OcclusionQuery q)
        {
            bool needsPass = _set == IntPtr.Zero;
            EnsureSet();
            CloseSegment();
            q.Parts.Clear();
            q.Overflowed = false;
            if (!_open.Contains(q)) _open.Add(q);
            q.Open = true;
            if (!needsPass) OpenSegment();
            return needsPass;
        }

        public void End(OcclusionQuery q)
        {
            CloseSegment();
            _open.Remove(q);
            q.Open = false;
            OpenSegment();
        }

        private void OpenSegment()
        {
            if (_pass == IntPtr.Zero || _open.Count == 0 || _set == IntPtr.Zero) return;
            if (Current.Used >= Capacity)
            {
                foreach (var q in _open) q.Overflowed = true;
                return;
            }
            int index = Current.Used++;
            wgpuRenderPassEncoderBeginOcclusionQuery(_pass, (uint)index);
            _segmentOpen = true;
            foreach (var q in _open) q.Parts.Add((Current, index));
        }

        private void CloseSegment()
        {
            if (!_segmentOpen) return;
            wgpuRenderPassEncoderEndOcclusionQuery(_pass);
            _segmentOpen = false;
        }

        /// <summary>Before the encoder is finished (its passes ended): the segments into a buffer to read back.</summary>
        public void BeforeFinish(IntPtr encoder)
        {
            Batch b = Current;
            Current = new Batch();
            if (b.Used <= 1) return;
            ulong bytes = (ulong)b.Used * 8;
            b.Resolve = _gpu.CreateBuffer(bytes, WGPUBufferUsage.QueryResolve | WGPUBufferUsage.CopySrc);
            b.Read = _gpu.CreateBuffer(bytes, WGPUBufferUsage.MapRead | WGPUBufferUsage.CopyDst);
            wgpuCommandEncoderResolveQuerySet(encoder, _set, 0, (uint)b.Used, b.Resolve, 0);
            wgpuCommandEncoderCopyBufferToBuffer(encoder, b.Resolve, 0, b.Read, 0, bytes);
        }

        /// <summary>The samples that passed while the query was open (it has ended, and the work holding
        /// it was submitted). Reads its submissions back, waiting for them.</summary>
        public ulong Result(OcclusionQuery q)
        {
            if (q.Overflowed) return 1;
            ulong sum = 0;
            foreach (var (b, index) in q.Parts)
            {
                if (b.Values == null)
                {
                    if (b.Read == IntPtr.Zero) continue;
                    byte[] raw = _gpu.MapRead(b.Read, (ulong)b.Used * 8);
                    b.Values = new ulong[b.Used];
                    Buffer.BlockCopy(raw, 0, b.Values, 0, b.Used * 8);
                    wgpuBufferRelease(b.Read);
                    wgpuBufferRelease(b.Resolve);
                    b.Read = b.Resolve = IntPtr.Zero;
                }
                sum += b.Values[index];
            }
            return sum;
        }

        public void Dispose()
        {
            if (_set != IntPtr.Zero) wgpuQuerySetRelease(_set);
            _set = IntPtr.Zero;
        }
    }
}
