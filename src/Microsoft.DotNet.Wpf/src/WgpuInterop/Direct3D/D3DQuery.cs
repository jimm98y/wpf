// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// ID3D11Query and ID3D11Predicate (Documentation/d3d11-webgpu-shim.md, section 2): Begin, End and
// GetData on the context.
//
// - An EVENT query is done when the GPU has finished the work submitted before its End. End submits
//   the context's recorded work; GetData asks wgpu (without blocking) whether the queue has drained.
// - TIMESTAMP_DISJOINT answers disjoint, so an app ignores the timestamps, which are the CPU's clock in
//   ns.
// - OCCLUSION and OCCLUSION_PREDICATE count the samples that passed, over WebGPU's occlusion queries
//   (OcclusionQueries.cs); GetData submits what holds them unless told not to, and reads them back.
// - PIPELINE_STATISTICS and the stream-output statistics answer zeros, also logged once.
//

using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using static Microsoft.Wpf.Interop.WebGpu.Wgpu;

namespace Microsoft.Wpf.Interop.WebGpu.Direct3D
{
    [StructLayout(LayoutKind.Sequential)]
    internal struct D3D11_QUERY_DESC { public uint Query, MiscFlags; }

    internal sealed unsafe class D3DQuery : D3DChild
    {
        public const uint Event = 0, Occlusion = 1, Timestamp = 2, TimestampDisjoint = 3, PipelineStatistics = 4, OcclusionPredicate = 5,
            SoStatistics = 6, SoOverflowPredicate = 7;

        private static readonly Vtable s_query = Build("ID3D11Query", Vtbl.ID3D11Query.IID);
        private static readonly Vtable s_predicate = Build("ID3D11Predicate", new Guid("9eb576dd-9f77-4d86-81aa-8bab5fe490e2"), Vtbl.ID3D11Query.IID);

        public readonly D3D11_QUERY_DESC Desc;
        /// <summary>End was called (and not Begin since).</summary>
        public bool Ended;
        /// <summary>An occlusion query's segments.</summary>
        public readonly OcclusionQuery Samples = new();
        public bool IsOcclusion => Desc.Query is Occlusion or OcclusionPredicate;

        public D3DQuery(D3DDevice device, D3D11_QUERY_DESC desc, bool predicate) : base(device, predicate ? s_predicate : s_query) { Desc = desc; }

        private static Vtable Build(string name, params Guid[] iids)
        {
            var all = new Guid[iids.Length + 2];
            iids.CopyTo(all, 0);
            all[^2] = new Guid("4b35d0cd-1e15-4258-9c98-1b1333f6dd3b");   // ID3D11Asynchronous
            all[^1] = Vtbl.ID3D11DeviceChild.IID;
            var vt = new Vtable(name, Vtbl.ID3D11Query.SlotCount, all);
            FillDeviceChild(vt);
            vt.Set(Vtbl.ID3D11Query.GetDataSize, (delegate* unmanaged<IntPtr, uint>)&GetDataSizeSlot);
            vt.Set(Vtbl.ID3D11Query.GetDesc, (delegate* unmanaged<IntPtr, D3D11_QUERY_DESC*, void>)&GetDescSlot);
            return vt;
        }

        public static bool Valid(uint kind) => kind <= 15;       // up to D3D11_QUERY_SO_OVERFLOW_PREDICATE_STREAM3

        /// <summary>The bytes GetData writes for a query of this kind.</summary>
        public static uint DataSize(uint kind) => kind switch
        {
            Event or OcclusionPredicate or SoOverflowPredicate or 11 or 13 or 15 => 4,
            Occlusion or Timestamp => 8,
            TimestampDisjoint => 16,
            PipelineStatistics => 88,
            _ => 16,                                                // the stream-output statistics: two UINT64
        };

        [UnmanagedCallersOnly]
        private static uint GetDataSizeSlot(IntPtr self) => From<D3DQuery>(self) is { } q ? DataSize(q.Desc.Query) : 0;

        [UnmanagedCallersOnly]
        private static void GetDescSlot(IntPtr self, D3D11_QUERY_DESC* d) { if (d != null && From<D3DQuery>(self) is { } q) *d = q.Desc; }
    }

    internal sealed unsafe partial class D3DContext
    {
        public void Begin(D3DQuery q) => Run(c =>
        {
            q.Ended = false;
            // a pass begun before the context had a query set cannot hold the query: the next one will
            if (q.IsOcclusion && c.OcclusionOrNew.Begin(q.Samples)) c.EndPass();
        }, q);

        public void End(D3DQuery q) => Run(c =>
        {
            q.Ended = true;
            if (q.IsOcclusion && q.Samples.Open) c.OcclusionOrNew.End(q.Samples);
            // the work before it, on its way to the GPU: the event is done when it is
            if (q.Desc.Query == D3DQuery.Event) c.FlushToGpu(wait: false);
        }, q);

        /// <summary>GetData: S_OK and the data when the query is done, S_FALSE when not yet.</summary>
        public int GetData(D3DQuery q, void* data, uint size, uint flags)
        {
            if (Deferred) return HResult.DXGI_ERROR_INVALID_CALL;
            uint need = D3DQuery.DataSize(q.Desc.Query);
            if (data != null && size != 0 && size != need) return HResult.E_INVALIDARG;
            lock (Device.Sync)
            {
                if (!q.Ended) return HResult.DXGI_ERROR_INVALID_CALL;
                if (q.Desc.Query == D3DQuery.Event)
                {
                    if ((flags & 1) == 0) FlushToGpu(wait: false);     // D3D11_ASYNC_GETDATA_DONOTFLUSH not given
                    // non-blocking: true once every submission so far has finished
                    if (wgpuDevicePoll(Device.Gpu.Device, 0, null) == 0) return HResult.S_FALSE;
                    if (data != null) *(int*)data = 1;
                    return HResult.S_OK;
                }
                if (q.IsOcclusion)
                {
                    // its samples: in work still recording, submitted first (unless the caller said not to)
                    if (Occlusion != null && q.Samples.InCurrentBatch(Occlusion))
                    {
                        if ((flags & 1) != 0) return HResult.S_FALSE;
                        FlushToGpu(wait: false);
                    }
                    ulong samples = Occlusion?.Result(q.Samples) ?? 0;
                    if (data == null) return HResult.S_OK;
                    if (q.Desc.Query == D3DQuery.Occlusion) *(ulong*)data = samples;
                    else *(int*)data = samples != 0 ? 1 : 0;
                    return HResult.S_OK;
                }
                if (data == null) return HResult.S_OK;
                new Span<byte>(data, (int)need).Clear();
                switch (q.Desc.Query)
                {
                    case D3DQuery.Timestamp:
                        *(ulong*)data = (ulong)(Stopwatch.GetTimestamp() * (1e9 / Stopwatch.Frequency));
                        break;
                    case D3DQuery.TimestampDisjoint:
                        *(ulong*)data = 1_000_000_000;               // the timestamps' frequency
                        *(int*)((byte*)data + 8) = 1;                // disjoint: the app disregards them
                        break;
                    default:
                        D3DLog.Once("qstat:" + q.Desc.Query, $"query {q.Desc.Query} (statistics) answers zeros");
                        break;
                }
                return HResult.S_OK;
            }
        }
    }
}
