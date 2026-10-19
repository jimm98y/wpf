// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// ID3D11DeviceContext's vtable: each slot reads its arguments into managed objects and calls the
// context (which runs it, or records it on a deferred context).
//

using System;
using System.Runtime.InteropServices;
using Microsoft.Wpf.Interop.WebGpu.Direct3D.Dxbc;
using static Microsoft.Wpf.Interop.WebGpu.Wgpu;

namespace Microsoft.Wpf.Interop.WebGpu.Direct3D
{
    internal sealed unsafe partial class D3DContext
    {
        private static Vtable Build()
        {
            var vt = new Vtable("ID3D11DeviceContext", Vtbl.ID3D11DeviceContext.SlotCount, Vtbl.ID3D11DeviceContext.IID, Vtbl.ID3D11DeviceChild.IID);
            FillDeviceChild(vt);
            const int S = 0;
            vt.Set(Vtbl.ID3D11DeviceContext.VSSetConstantBuffers + S, (delegate* unmanaged<IntPtr, uint, uint, IntPtr*, void>)&VSSetConstantBuffers);
            vt.Set(Vtbl.ID3D11DeviceContext.PSSetConstantBuffers, (delegate* unmanaged<IntPtr, uint, uint, IntPtr*, void>)&PSSetConstantBuffers);
            vt.Set(Vtbl.ID3D11DeviceContext.GSSetConstantBuffers, (delegate* unmanaged<IntPtr, uint, uint, IntPtr*, void>)&GSSetConstantBuffers);
            vt.Set(Vtbl.ID3D11DeviceContext.VSSetShaderResources, (delegate* unmanaged<IntPtr, uint, uint, IntPtr*, void>)&VSSetShaderResources);
            vt.Set(Vtbl.ID3D11DeviceContext.PSSetShaderResources, (delegate* unmanaged<IntPtr, uint, uint, IntPtr*, void>)&PSSetShaderResources);
            vt.Set(Vtbl.ID3D11DeviceContext.GSSetShaderResources, (delegate* unmanaged<IntPtr, uint, uint, IntPtr*, void>)&GSSetShaderResources);
            vt.Set(Vtbl.ID3D11DeviceContext.VSSetSamplers, (delegate* unmanaged<IntPtr, uint, uint, IntPtr*, void>)&VSSetSamplers);
            vt.Set(Vtbl.ID3D11DeviceContext.PSSetSamplers, (delegate* unmanaged<IntPtr, uint, uint, IntPtr*, void>)&PSSetSamplers);
            vt.Set(Vtbl.ID3D11DeviceContext.GSSetSamplers, (delegate* unmanaged<IntPtr, uint, uint, IntPtr*, void>)&GSSetSamplers);
            vt.Set(Vtbl.ID3D11DeviceContext.VSSetShader, (delegate* unmanaged<IntPtr, IntPtr, IntPtr*, uint, void>)&VSSetShader);
            vt.Set(Vtbl.ID3D11DeviceContext.PSSetShader, (delegate* unmanaged<IntPtr, IntPtr, IntPtr*, uint, void>)&PSSetShader);
            vt.Set(Vtbl.ID3D11DeviceContext.GSSetShader, (delegate* unmanaged<IntPtr, IntPtr, IntPtr*, uint, void>)&GSSetShader);
            vt.Set(Vtbl.ID3D11DeviceContext.CSSetShader, (delegate* unmanaged<IntPtr, IntPtr, IntPtr*, uint, void>)&CSSetShader);
            vt.Set(Vtbl.ID3D11DeviceContext.HSSetShader, (delegate* unmanaged<IntPtr, IntPtr, IntPtr*, uint, void>)&HSSetShader);
            vt.Set(Vtbl.ID3D11DeviceContext.DSSetShader, (delegate* unmanaged<IntPtr, IntPtr, IntPtr*, uint, void>)&DSSetShader);
            vt.Set(Vtbl.ID3D11DeviceContext.CSSetConstantBuffers, (delegate* unmanaged<IntPtr, uint, uint, IntPtr*, void>)&CSSetConstantBuffers);
            vt.Set(Vtbl.ID3D11DeviceContext.HSSetConstantBuffers, (delegate* unmanaged<IntPtr, uint, uint, IntPtr*, void>)&HSSetConstantBuffers);
            vt.Set(Vtbl.ID3D11DeviceContext.DSSetConstantBuffers, (delegate* unmanaged<IntPtr, uint, uint, IntPtr*, void>)&DSSetConstantBuffers);
            vt.Set(Vtbl.ID3D11DeviceContext.CSSetShaderResources, (delegate* unmanaged<IntPtr, uint, uint, IntPtr*, void>)&CSSetShaderResources);
            vt.Set(Vtbl.ID3D11DeviceContext.HSSetShaderResources, (delegate* unmanaged<IntPtr, uint, uint, IntPtr*, void>)&HSSetShaderResources);
            vt.Set(Vtbl.ID3D11DeviceContext.DSSetShaderResources, (delegate* unmanaged<IntPtr, uint, uint, IntPtr*, void>)&DSSetShaderResources);
            vt.Set(Vtbl.ID3D11DeviceContext.CSSetSamplers, (delegate* unmanaged<IntPtr, uint, uint, IntPtr*, void>)&CSSetSamplers);
            vt.Set(Vtbl.ID3D11DeviceContext.HSSetSamplers, (delegate* unmanaged<IntPtr, uint, uint, IntPtr*, void>)&HSSetSamplers);
            vt.Set(Vtbl.ID3D11DeviceContext.DSSetSamplers, (delegate* unmanaged<IntPtr, uint, uint, IntPtr*, void>)&DSSetSamplers);
            vt.Set(Vtbl.ID3D11DeviceContext.CSSetUnorderedAccessViews, (delegate* unmanaged<IntPtr, uint, uint, IntPtr*, uint*, void>)&CSSetUnorderedAccessViews);
            vt.Set(Vtbl.ID3D11DeviceContext.OMSetRenderTargetsAndUnorderedAccessViews, (delegate* unmanaged<IntPtr, uint, IntPtr*, IntPtr, uint, uint, IntPtr*, uint*, void>)&OMSetRenderTargetsAndUnorderedAccessViews);
            vt.Set(Vtbl.ID3D11DeviceContext.Dispatch, (delegate* unmanaged<IntPtr, uint, uint, uint, void>)&DispatchSlot);
            vt.Set(Vtbl.ID3D11DeviceContext.DispatchIndirect, (delegate* unmanaged<IntPtr, IntPtr, uint, void>)&DispatchIndirectSlot);
            vt.Set(Vtbl.ID3D11DeviceContext.ClearUnorderedAccessViewUint, (delegate* unmanaged<IntPtr, IntPtr, uint*, void>)&ClearUavUint);
            vt.Set(Vtbl.ID3D11DeviceContext.ClearUnorderedAccessViewFloat, (delegate* unmanaged<IntPtr, IntPtr, float*, void>)&ClearUavFloat);
            vt.Set(Vtbl.ID3D11DeviceContext.CopyStructureCount, (delegate* unmanaged<IntPtr, IntPtr, uint, IntPtr, void>)&CopyStructureCountSlot);
            vt.Set(Vtbl.ID3D11DeviceContext.DrawIndexed, (delegate* unmanaged<IntPtr, uint, uint, int, void>)&DrawIndexed);
            vt.Set(Vtbl.ID3D11DeviceContext.Draw, (delegate* unmanaged<IntPtr, uint, uint, void>)&DrawSlot);
            vt.Set(Vtbl.ID3D11DeviceContext.Begin, (delegate* unmanaged<IntPtr, IntPtr, void>)&BeginSlot);
            vt.Set(Vtbl.ID3D11DeviceContext.End, (delegate* unmanaged<IntPtr, IntPtr, void>)&EndSlot);
            vt.Set(Vtbl.ID3D11DeviceContext.GetData, (delegate* unmanaged<IntPtr, IntPtr, void*, uint, uint, int>)&GetDataSlot);
            vt.Set(Vtbl.ID3D11DeviceContext.DrawIndexedInstanced, (delegate* unmanaged<IntPtr, uint, uint, uint, int, uint, void>)&DrawIndexedInstanced);
            vt.Set(Vtbl.ID3D11DeviceContext.DrawInstanced, (delegate* unmanaged<IntPtr, uint, uint, uint, uint, void>)&DrawInstanced);
            vt.Set(Vtbl.ID3D11DeviceContext.DrawIndexedInstancedIndirect, (delegate* unmanaged<IntPtr, IntPtr, uint, void>)&DrawIndexedInstancedIndirect);
            vt.Set(Vtbl.ID3D11DeviceContext.DrawInstancedIndirect, (delegate* unmanaged<IntPtr, IntPtr, uint, void>)&DrawInstancedIndirect);
            vt.Set(Vtbl.ID3D11DeviceContext.DrawAuto, (delegate* unmanaged<IntPtr, void>)&DrawAutoSlot);
            vt.Set(Vtbl.ID3D11DeviceContext.SOSetTargets, (delegate* unmanaged<IntPtr, uint, IntPtr*, uint*, void>)&SOSetTargets);
            vt.Set(Vtbl.ID3D11DeviceContext.SOGetTargets, (delegate* unmanaged<IntPtr, uint, IntPtr*, void>)&SOGetTargets);
            vt.Set(Vtbl.ID3D11DeviceContext.Map, (delegate* unmanaged<IntPtr, IntPtr, uint, D3D11_MAP, uint, D3D11_MAPPED_SUBRESOURCE*, int>)&MapSlot);
            vt.Set(Vtbl.ID3D11DeviceContext.Unmap, (delegate* unmanaged<IntPtr, IntPtr, uint, void>)&UnmapSlot);
            vt.Set(Vtbl.ID3D11DeviceContext.IASetInputLayout, (delegate* unmanaged<IntPtr, IntPtr, void>)&IASetInputLayout);
            vt.Set(Vtbl.ID3D11DeviceContext.IASetVertexBuffers, (delegate* unmanaged<IntPtr, uint, uint, IntPtr*, uint*, uint*, void>)&IASetVertexBuffers);
            vt.Set(Vtbl.ID3D11DeviceContext.IASetIndexBuffer, (delegate* unmanaged<IntPtr, IntPtr, DXGI_FORMAT, uint, void>)&IASetIndexBuffer);
            vt.Set(Vtbl.ID3D11DeviceContext.IASetPrimitiveTopology, (delegate* unmanaged<IntPtr, D3D_PRIMITIVE_TOPOLOGY, void>)&IASetPrimitiveTopology);
            vt.Set(Vtbl.ID3D11DeviceContext.OMSetRenderTargets, (delegate* unmanaged<IntPtr, uint, IntPtr*, IntPtr, void>)&OMSetRenderTargets);
            vt.Set(Vtbl.ID3D11DeviceContext.OMSetBlendState, (delegate* unmanaged<IntPtr, IntPtr, float*, uint, void>)&OMSetBlendState);
            vt.Set(Vtbl.ID3D11DeviceContext.OMSetDepthStencilState, (delegate* unmanaged<IntPtr, IntPtr, uint, void>)&OMSetDepthStencilState);
            vt.Set(Vtbl.ID3D11DeviceContext.RSSetState, (delegate* unmanaged<IntPtr, IntPtr, void>)&RSSetState);
            vt.Set(Vtbl.ID3D11DeviceContext.RSSetViewports, (delegate* unmanaged<IntPtr, uint, D3D11_VIEWPORT*, void>)&RSSetViewports);
            vt.Set(Vtbl.ID3D11DeviceContext.RSSetScissorRects, (delegate* unmanaged<IntPtr, uint, D3D11_RECT*, void>)&RSSetScissorRects);
            vt.Set(Vtbl.ID3D11DeviceContext.CopySubresourceRegion, (delegate* unmanaged<IntPtr, IntPtr, uint, uint, uint, uint, IntPtr, uint, D3D11_BOX*, void>)&CopySubresourceRegionSlot);
            vt.Set(Vtbl.ID3D11DeviceContext.CopyResource, (delegate* unmanaged<IntPtr, IntPtr, IntPtr, void>)&CopyResourceSlot);
            vt.Set(Vtbl.ID3D11DeviceContext.UpdateSubresource, (delegate* unmanaged<IntPtr, IntPtr, uint, D3D11_BOX*, void*, uint, uint, void>)&UpdateSubresourceSlot);
            vt.Set(Vtbl.ID3D11DeviceContext.ClearRenderTargetView, (delegate* unmanaged<IntPtr, IntPtr, float*, void>)&ClearRenderTargetView);
            vt.Set(Vtbl.ID3D11DeviceContext.ClearDepthStencilView, (delegate* unmanaged<IntPtr, IntPtr, uint, float, byte, void>)&ClearDepthStencilView);
            vt.Set(Vtbl.ID3D11DeviceContext.ResolveSubresource, (delegate* unmanaged<IntPtr, IntPtr, uint, IntPtr, uint, DXGI_FORMAT, void>)&ResolveSubresourceSlot);
            vt.Set(Vtbl.ID3D11DeviceContext.ExecuteCommandList, (delegate* unmanaged<IntPtr, IntPtr, int, void>)&ExecuteCommandList);
            vt.Set(Vtbl.ID3D11DeviceContext.ClearState, (delegate* unmanaged<IntPtr, void>)&ClearStateSlot);
            vt.Set(Vtbl.ID3D11DeviceContext.Flush, (delegate* unmanaged<IntPtr, void>)&FlushSlot);
            vt.Set(Vtbl.ID3D11DeviceContext.GetType, (delegate* unmanaged<IntPtr, uint>)&GetTypeSlot);
            vt.Set(Vtbl.ID3D11DeviceContext.GetContextFlags, (delegate* unmanaged<IntPtr, uint>)&GetContextFlags);
            vt.Set(Vtbl.ID3D11DeviceContext.FinishCommandList, (delegate* unmanaged<IntPtr, int, IntPtr*, int>)&FinishCommandList);
            vt.Set(Vtbl.ID3D11DeviceContext.OMGetRenderTargets, (delegate* unmanaged<IntPtr, uint, IntPtr*, IntPtr*, void>)&OMGetRenderTargets);
            vt.Set(Vtbl.ID3D11DeviceContext.RSGetViewports, (delegate* unmanaged<IntPtr, uint*, D3D11_VIEWPORT*, void>)&RSGetViewports);
            vt.Set(Vtbl.ID3D11DeviceContext.IAGetPrimitiveTopology, (delegate* unmanaged<IntPtr, D3D_PRIMITIVE_TOPOLOGY*, void>)&IAGetPrimitiveTopology);
            vt.Set(Vtbl.ID3D11DeviceContext.SetPredication, (delegate* unmanaged<IntPtr, IntPtr, int, void>)&Nothing2);
            return vt;
        }

        private static D3DContext? C(IntPtr p) => From<D3DContext>(p);

        [UnmanagedCallersOnly]
        private static void BeginSlot(IntPtr self, IntPtr query) { var c = C(self); var q = From<D3DQuery>(query); if (c != null && q != null) Guard("Begin", () => c.Begin(q)); }

        [UnmanagedCallersOnly]
        private static void EndSlot(IntPtr self, IntPtr query) { var c = C(self); var q = From<D3DQuery>(query); if (c != null && q != null) Guard("End", () => c.End(q)); }

        [UnmanagedCallersOnly]
        private static int GetDataSlot(IntPtr self, IntPtr query, void* data, uint size, uint flags)
        {
            var c = C(self);
            var q = From<D3DQuery>(query);
            if (c == null || q == null) return HResult.E_INVALIDARG;
            try { return c.GetData(q, data, size, flags); }
            catch (Exception e) { D3DLog.Error("GetData", e); return HResult.E_FAIL; }
        }

        private static void Guard(string where, Action a)
        {
            try { a(); }
            catch (NotSupportedException e) { D3DLog.Once("ns:" + where + e.Message, $"{where}: {e.Message}"); }
            catch (Exception e) { D3DLog.Error(where, e); }
        }

        private static T?[] Objects<T>(IntPtr* pp, uint n) where T : ComObject
        {
            var a = new T?[n];
            if (pp != null) for (int i = 0; i < n; i++) a[i] = From<T>(pp[i]);
            return a;
        }

        private static void SetCbs(IntPtr self, ShaderStage s, uint start, uint n, IntPtr* pp) { var c = C(self); if (c != null) Guard("SetConstantBuffers", () => c.SetConstantBuffers(s, (int)start, Objects<D3DBuffer>(pp, n))); }
        private static void SetSrvs(IntPtr self, ShaderStage s, uint start, uint n, IntPtr* pp) { var c = C(self); if (c != null) Guard("SetShaderResources", () => c.SetResources(s, (int)start, Objects<D3DShaderResourceView>(pp, n))); }
        private static void SetSmps(IntPtr self, ShaderStage s, uint start, uint n, IntPtr* pp) { var c = C(self); if (c != null) Guard("SetSamplers", () => c.SetSamplers(s, (int)start, Objects<D3DSamplerState>(pp, n))); }
        private static void SetSh(IntPtr self, ShaderStage s, IntPtr shader) { var c = C(self); if (c != null) Guard("SetShader", () => c.SetShader(s, From<D3DShader>(shader))); }

        [UnmanagedCallersOnly] private static void CSSetShader(IntPtr self, IntPtr sh, IntPtr* ci, uint nci) => SetSh(self, ShaderStage.Compute, sh);
        [UnmanagedCallersOnly] private static void HSSetShader(IntPtr self, IntPtr sh, IntPtr* ci, uint nci) => SetSh(self, ShaderStage.Hull, sh);
        [UnmanagedCallersOnly] private static void DSSetShader(IntPtr self, IntPtr sh, IntPtr* ci, uint nci) => SetSh(self, ShaderStage.Domain, sh);
        [UnmanagedCallersOnly] private static void CSSetConstantBuffers(IntPtr self, uint start, uint n, IntPtr* pp) => SetCbs(self, ShaderStage.Compute, start, n, pp);
        [UnmanagedCallersOnly] private static void HSSetConstantBuffers(IntPtr self, uint start, uint n, IntPtr* pp) => SetCbs(self, ShaderStage.Hull, start, n, pp);
        [UnmanagedCallersOnly] private static void DSSetConstantBuffers(IntPtr self, uint start, uint n, IntPtr* pp) => SetCbs(self, ShaderStage.Domain, start, n, pp);
        [UnmanagedCallersOnly] private static void CSSetShaderResources(IntPtr self, uint start, uint n, IntPtr* pp) => SetSrvs(self, ShaderStage.Compute, start, n, pp);
        [UnmanagedCallersOnly] private static void HSSetShaderResources(IntPtr self, uint start, uint n, IntPtr* pp) => SetSrvs(self, ShaderStage.Hull, start, n, pp);
        [UnmanagedCallersOnly] private static void DSSetShaderResources(IntPtr self, uint start, uint n, IntPtr* pp) => SetSrvs(self, ShaderStage.Domain, start, n, pp);
        [UnmanagedCallersOnly] private static void CSSetSamplers(IntPtr self, uint start, uint n, IntPtr* pp) => SetSmps(self, ShaderStage.Compute, start, n, pp);
        [UnmanagedCallersOnly] private static void HSSetSamplers(IntPtr self, uint start, uint n, IntPtr* pp) => SetSmps(self, ShaderStage.Hull, start, n, pp);
        [UnmanagedCallersOnly] private static void DSSetSamplers(IntPtr self, uint start, uint n, IntPtr* pp) => SetSmps(self, ShaderStage.Domain, start, n, pp);

        private static uint[]? Counts(uint* counts, uint n)
        {
            if (counts == null) return null;
            var r = new uint[n];
            for (int i = 0; i < n; i++) r[i] = counts[i];
            return r;
        }

        [UnmanagedCallersOnly]
        private static void CSSetUnorderedAccessViews(IntPtr self, uint start, uint n, IntPtr* uavs, uint* counts)
        {
            var c = C(self);
            if (c == null) return;
            var v = Objects<D3DUnorderedAccessView>(uavs, Math.Min(n, (uint)MaxUavs));
            var k = Counts(counts, (uint)v.Length);
            Guard("CSSetUnorderedAccessViews", () => c.SetUavs(true, (int)start, v, k));
        }

        [UnmanagedCallersOnly]
        private static void OMSetRenderTargetsAndUnorderedAccessViews(IntPtr self, uint nRtv, IntPtr* rtvs, IntPtr dsv, uint uavStart, uint nUav, IntPtr* uavs, uint* counts)
        {
            var c = C(self);
            if (c == null) return;
            if (nRtv != 0xFFFFFFFF)                    // D3D11_KEEP_RENDER_TARGETS_AND_DEPTH_STENCIL
            {
                var v = Objects<D3DRenderTargetView>(rtvs, Math.Min(nRtv, (uint)MaxTargets));
                Guard("OMSetRenderTargetsAndUnorderedAccessViews", () => c.SetTargets(v, From<D3DDepthStencilView>(dsv)));
            }
            if (nUav != 0xFFFFFFFF)                    // D3D11_KEEP_UNORDERED_ACCESS_VIEWS
            {
                var u = Objects<D3DUnorderedAccessView>(uavs, Math.Min(nUav, (uint)MaxUavs));
                var k = Counts(counts, (uint)u.Length);
                Guard("OMSetRenderTargetsAndUnorderedAccessViews", () => c.SetUavs(false, (int)uavStart, u, k));
            }
        }

        [UnmanagedCallersOnly] private static void DispatchSlot(IntPtr self, uint x, uint y, uint z) { var c = C(self); if (c != null) Guard("Dispatch", () => c.Dispatch(x, y, z)); }

        [UnmanagedCallersOnly]
        private static void DispatchIndirectSlot(IntPtr self, IntPtr args, uint offset)
        {
            var c = C(self);
            var b = From<D3DBuffer>(args);
            if (c != null && b != null) Guard("DispatchIndirect", () => c.DispatchIndirect(b, offset));
        }

        [UnmanagedCallersOnly]
        private static void ClearUavUint(IntPtr self, IntPtr view, uint* values)
        {
            var c = C(self);
            var v = From<D3DUnorderedAccessView>(view);
            if (c == null || v == null || values == null) return;
            var copy = new[] { values[0], values[1], values[2], values[3] };
            Guard("ClearUnorderedAccessViewUint", () => c.ClearUav(v, copy, false));
        }

        [UnmanagedCallersOnly]
        private static void ClearUavFloat(IntPtr self, IntPtr view, float* values)
        {
            var c = C(self);
            var v = From<D3DUnorderedAccessView>(view);
            if (c == null || v == null || values == null) return;
            var copy = new[] { BitConverter.SingleToUInt32Bits(values[0]), BitConverter.SingleToUInt32Bits(values[1]), BitConverter.SingleToUInt32Bits(values[2]), BitConverter.SingleToUInt32Bits(values[3]) };
            Guard("ClearUnorderedAccessViewFloat", () => c.ClearUav(v, copy, true));
        }

        [UnmanagedCallersOnly]
        private static void CopyStructureCountSlot(IntPtr self, IntPtr dst, uint offset, IntPtr src)
        {
            var c = C(self);
            var b = From<D3DBuffer>(dst);
            var v = From<D3DUnorderedAccessView>(src);
            if (c != null && b != null && v != null) Guard("CopyStructureCount", () => c.CopyStructureCount(b, offset, v));
        }

        [UnmanagedCallersOnly] private static void VSSetConstantBuffers(IntPtr self, uint start, uint n, IntPtr* pp) => SetCbs(self, ShaderStage.Vertex, start, n, pp);
        [UnmanagedCallersOnly] private static void PSSetConstantBuffers(IntPtr self, uint start, uint n, IntPtr* pp) => SetCbs(self, ShaderStage.Pixel, start, n, pp);
        [UnmanagedCallersOnly] private static void GSSetConstantBuffers(IntPtr self, uint start, uint n, IntPtr* pp) => SetCbs(self, ShaderStage.Geometry, start, n, pp);
        [UnmanagedCallersOnly] private static void VSSetShaderResources(IntPtr self, uint start, uint n, IntPtr* pp) => SetSrvs(self, ShaderStage.Vertex, start, n, pp);
        [UnmanagedCallersOnly] private static void PSSetShaderResources(IntPtr self, uint start, uint n, IntPtr* pp) => SetSrvs(self, ShaderStage.Pixel, start, n, pp);
        [UnmanagedCallersOnly] private static void GSSetShaderResources(IntPtr self, uint start, uint n, IntPtr* pp) => SetSrvs(self, ShaderStage.Geometry, start, n, pp);
        [UnmanagedCallersOnly] private static void VSSetSamplers(IntPtr self, uint start, uint n, IntPtr* pp) => SetSmps(self, ShaderStage.Vertex, start, n, pp);
        [UnmanagedCallersOnly] private static void PSSetSamplers(IntPtr self, uint start, uint n, IntPtr* pp) => SetSmps(self, ShaderStage.Pixel, start, n, pp);
        [UnmanagedCallersOnly] private static void GSSetSamplers(IntPtr self, uint start, uint n, IntPtr* pp) => SetSmps(self, ShaderStage.Geometry, start, n, pp);
        [UnmanagedCallersOnly] private static void VSSetShader(IntPtr self, IntPtr sh, IntPtr* ci, uint nci) => SetSh(self, ShaderStage.Vertex, sh);
        [UnmanagedCallersOnly] private static void PSSetShader(IntPtr self, IntPtr sh, IntPtr* ci, uint nci) => SetSh(self, ShaderStage.Pixel, sh);
        [UnmanagedCallersOnly] private static void GSSetShader(IntPtr self, IntPtr sh, IntPtr* ci, uint nci) => SetSh(self, ShaderStage.Geometry, sh);

        private static void DoDraw(IntPtr self, DrawArgs a) { var c = C(self); if (c != null) Guard("Draw", () => c.Draw(a)); }

        [UnmanagedCallersOnly] private static void DrawSlot(IntPtr self, uint count, uint start) => DoDraw(self, new DrawArgs { Count = count, Instances = 1, First = start });
        [UnmanagedCallersOnly] private static void DrawIndexed(IntPtr self, uint count, uint start, int baseVertex) => DoDraw(self, new DrawArgs { Indexed = true, Count = count, Instances = 1, First = start, BaseVertex = baseVertex });
        [UnmanagedCallersOnly] private static void DrawInstanced(IntPtr self, uint count, uint instances, uint start, uint startInstance) => DoDraw(self, new DrawArgs { Count = count, Instances = instances, First = start, FirstInstance = startInstance });
        [UnmanagedCallersOnly] private static void DrawIndexedInstancedIndirect(IntPtr self, IntPtr args, uint offset)
        {
            var b = From<D3DBuffer>(args);
            if (b != null) DoDraw(self, new DrawArgs { Indexed = true, Indirect = b, IndirectOffset = offset, Instances = 1 });
        }

        [UnmanagedCallersOnly] private static void DrawInstancedIndirect(IntPtr self, IntPtr args, uint offset)
        {
            var b = From<D3DBuffer>(args);
            if (b != null) DoDraw(self, new DrawArgs { Indirect = b, IndirectOffset = offset, Instances = 1 });
        }

        [UnmanagedCallersOnly] private static void DrawAutoSlot(IntPtr self) { var c = C(self); if (c != null) Guard("DrawAuto", () => c.DrawAuto()); }

        [UnmanagedCallersOnly]
        private static void SOSetTargets(IntPtr self, uint n, IntPtr* buffers, uint* offsets)
        {
            var c = C(self);
            if (c == null) return;
            var b = Objects<D3DBuffer>(buffers, Math.Min(n, 4u));
            var o = new uint[b.Length];
            for (int i = 0; i < o.Length; i++) o[i] = offsets != null ? offsets[i] : 0;
            Guard("SOSetTargets", () => c.SetSoTargets(b, o));
        }

        [UnmanagedCallersOnly]
        private static void SOGetTargets(IntPtr self, uint n, IntPtr* buffers)
        {
            var c = C(self);
            if (c == null || buffers == null) return;
            for (int i = 0; i < n; i++) buffers[i] = i < 4 && c._so[i] != null ? c._so[i]!.AddRefPointer() : IntPtr.Zero;
        }

        [UnmanagedCallersOnly] private static void DrawIndexedInstanced(IntPtr self, uint count, uint instances, uint start, int baseVertex, uint startInstance) => DoDraw(self, new DrawArgs { Indexed = true, Count = count, Instances = instances, First = start, BaseVertex = baseVertex, FirstInstance = startInstance });

        [UnmanagedCallersOnly]
        private static int MapSlot(IntPtr self, IntPtr resource, uint sub, D3D11_MAP type, uint flags, D3D11_MAPPED_SUBRESOURCE* mapped)
        {
            var c = C(self);
            var r = From<D3DResource>(resource);
            if (c == null || r == null) return HResult.E_INVALIDARG;
            try { c.Map(r, sub, type, flags, mapped, out int hr); return hr; }
            catch (Exception e) { D3DLog.Error("Map", e); return HResult.E_FAIL; }
        }

        [UnmanagedCallersOnly]
        private static void UnmapSlot(IntPtr self, IntPtr resource, uint sub)
        {
            var c = C(self);
            var r = From<D3DResource>(resource);
            if (c != null && r != null) Guard("Unmap", () => c.Unmap(r, sub));
        }

        [UnmanagedCallersOnly] private static void IASetInputLayout(IntPtr self, IntPtr layout) { var c = C(self); if (c != null) Guard("IASetInputLayout", () => c.SetInputLayout(From<D3DInputLayout>(layout))); }

        [UnmanagedCallersOnly]
        private static void IASetVertexBuffers(IntPtr self, uint start, uint n, IntPtr* pp, uint* strides, uint* offsets)
        {
            var c = C(self);
            if (c == null) return;
            var s = new uint[n]; var o = new uint[n];
            for (int i = 0; i < n; i++) { s[i] = strides != null ? strides[i] : 0; o[i] = offsets != null ? offsets[i] : 0; }
            var b = Objects<D3DBuffer>(pp, n);
            Guard("IASetVertexBuffers", () => c.SetVertexBuffers((int)start, b, s, o));
        }

        [UnmanagedCallersOnly] private static void IASetIndexBuffer(IntPtr self, IntPtr buffer, DXGI_FORMAT format, uint offset) { var c = C(self); if (c != null) Guard("IASetIndexBuffer", () => c.SetIndexBuffer(From<D3DBuffer>(buffer), format, offset)); }
        [UnmanagedCallersOnly] private static void IASetPrimitiveTopology(IntPtr self, D3D_PRIMITIVE_TOPOLOGY t) { var c = C(self); if (c != null) Guard("IASetPrimitiveTopology", () => c.SetTopology(t)); }

        [UnmanagedCallersOnly]
        private static void OMSetRenderTargets(IntPtr self, uint n, IntPtr* rtvs, IntPtr dsv)
        {
            var c = C(self);
            if (c == null) return;
            var v = Objects<D3DRenderTargetView>(rtvs, Math.Min(n, (uint)MaxTargets));
            Guard("OMSetRenderTargets", () => c.SetTargets(v, From<D3DDepthStencilView>(dsv)));
        }

        [UnmanagedCallersOnly]
        private static void OMSetBlendState(IntPtr self, IntPtr state, float* factor, uint mask)
        {
            var c = C(self);
            if (c == null) return;
            var f = factor == null ? new WGPUColor { r = 1, g = 1, b = 1, a = 1 } : new WGPUColor { r = factor[0], g = factor[1], b = factor[2], a = factor[3] };
            Guard("OMSetBlendState", () => c.SetBlend(From<D3DBlendState>(state), f, mask));
        }

        [UnmanagedCallersOnly] private static void OMSetDepthStencilState(IntPtr self, IntPtr state, uint stencilRef) { var c = C(self); if (c != null) Guard("OMSetDepthStencilState", () => c.SetDepthStencil(From<D3DDepthStencilState>(state), stencilRef)); }
        [UnmanagedCallersOnly] private static void RSSetState(IntPtr self, IntPtr state) { var c = C(self); if (c != null) Guard("RSSetState", () => c.SetRasterizer(From<D3DRasterizerState>(state))); }

        [UnmanagedCallersOnly]
        private static void RSSetViewports(IntPtr self, uint n, D3D11_VIEWPORT* v)
        {
            var c = C(self);
            if (c == null) return;
            var a = new D3D11_VIEWPORT[v == null ? 0 : n];
            for (int i = 0; i < a.Length; i++) a[i] = v[i];
            Guard("RSSetViewports", () => c.SetViewports(a));
        }

        [UnmanagedCallersOnly]
        private static void RSSetScissorRects(IntPtr self, uint n, D3D11_RECT* r)
        {
            var c = C(self);
            if (c == null) return;
            var a = new D3D11_RECT[r == null ? 0 : n];
            for (int i = 0; i < a.Length; i++) a[i] = r[i];
            Guard("RSSetScissorRects", () => c.SetScissors(a));
        }

        [UnmanagedCallersOnly]
        private static void CopySubresourceRegionSlot(IntPtr self, IntPtr dst, uint dstSub, uint x, uint y, uint z, IntPtr src, uint srcSub, D3D11_BOX* box)
        {
            var c = C(self);
            var d = From<D3DResource>(dst);
            var s = From<D3DResource>(src);
            if (c == null || d == null || s == null) return;
            D3D11_BOX? b = box == null ? null : *box;
            Guard("CopySubresourceRegion", () => c.CopySubresourceRegion(d, dstSub, x, y, z, s, srcSub, b));
        }

        [UnmanagedCallersOnly]
        private static void CopyResourceSlot(IntPtr self, IntPtr dst, IntPtr src)
        {
            var c = C(self);
            var d = From<D3DResource>(dst);
            var s = From<D3DResource>(src);
            if (c != null && d != null && s != null) Guard("CopyResource", () => c.CopyResource(d, s));
        }

        [UnmanagedCallersOnly]
        private static void UpdateSubresourceSlot(IntPtr self, IntPtr dst, uint sub, D3D11_BOX* box, void* data, uint rowPitch, uint depthPitch)
        {
            var c = C(self);
            var d = From<D3DResource>(dst);
            if (c == null || d == null || data == null) return;
            try { c.UpdateSubresource(d, sub, box, data, rowPitch, depthPitch); }
            catch (Exception e) { D3DLog.Error("UpdateSubresource", e); }
        }

        [UnmanagedCallersOnly]
        private static void ClearRenderTargetView(IntPtr self, IntPtr view, float* rgba)
        {
            var c = C(self);
            var v = From<D3DRenderTargetView>(view);
            if (c == null || v == null || rgba == null) return;
            var color = new WGPUColor { r = rgba[0], g = rgba[1], b = rgba[2], a = rgba[3] };
            Guard("ClearRenderTargetView", () => c.ClearRtv(v, color));
        }

        [UnmanagedCallersOnly]
        private static void ClearDepthStencilView(IntPtr self, IntPtr view, uint flags, float depth, byte stencil)
        {
            var c = C(self);
            var v = From<D3DDepthStencilView>(view);
            if (c != null && v != null) Guard("ClearDepthStencilView", () => c.ClearDsv(v, flags, depth, stencil));
        }

        [UnmanagedCallersOnly]
        private static void ResolveSubresourceSlot(IntPtr self, IntPtr dst, uint dstSub, IntPtr src, uint srcSub, DXGI_FORMAT format)
        {
            var c = C(self);
            if (c != null && From<D3DTexture2D>(dst) is { } d && From<D3DTexture2D>(src) is { } s)
                Guard("ResolveSubresource", () => c.ResolveSubresource(d, dstSub, s, srcSub, format));
        }

        [UnmanagedCallersOnly]
        private static void ExecuteCommandList(IntPtr self, IntPtr list, int restore)
        {
            var c = C(self);
            var l = From<D3DCommandList>(list);
            if (c != null && l != null) Guard("ExecuteCommandList", () => c.Execute(l, restore != 0));
        }

        [UnmanagedCallersOnly] private static void ClearStateSlot(IntPtr self) { var c = C(self); if (c != null) Guard("ClearState", () => c.ClearState()); }
        [UnmanagedCallersOnly] private static void FlushSlot(IntPtr self) { var c = C(self); if (c != null) Guard("Flush", () => c.FlushToGpu(wait: false)); }
        [UnmanagedCallersOnly] private static uint GetTypeSlot(IntPtr self) => C(self)?.Deferred == true ? 1u : 0u;
        [UnmanagedCallersOnly] private static uint GetContextFlags(IntPtr self) => 0;
        [UnmanagedCallersOnly] private static void Nothing2(IntPtr self, IntPtr a, int b) { }

        [UnmanagedCallersOnly]
        private static int FinishCommandList(IntPtr self, int restore, IntPtr* list)
        {
            var c = C(self);
            if (c == null || !c.Deferred) return HResult.DXGI_ERROR_INVALID_CALL;
            try
            {
                var l = c.Finish(restore != 0);
                if (list == null) { l.Release(); return HResult.S_OK; }
                *list = l.Pointer;
                return HResult.S_OK;
            }
            catch (Exception e) { D3DLog.Error("FinishCommandList", e); return HResult.E_FAIL; }
        }

        [UnmanagedCallersOnly]
        private static void OMGetRenderTargets(IntPtr self, uint n, IntPtr* rtvs, IntPtr* dsv)
        {
            var c = C(self);
            if (c == null) return;
            for (int i = 0; i < n && rtvs != null; i++) rtvs[i] = i < MaxTargets && c._rtv[i] != null ? c._rtv[i]!.AddRefPointer() : IntPtr.Zero;
            if (dsv != null) *dsv = c._dsv?.AddRefPointer() ?? IntPtr.Zero;
        }

        [UnmanagedCallersOnly]
        private static void RSGetViewports(IntPtr self, uint* n, D3D11_VIEWPORT* v)
        {
            var c = C(self);
            if (c == null || n == null) return;
            if (v == null) { *n = (uint)c._viewportCount; return; }
            uint k = Math.Min(*n, (uint)c._viewportCount);
            for (int i = 0; i < k; i++) v[i] = c._viewports[i];
            *n = k;
        }

        [UnmanagedCallersOnly]
        private static void IAGetPrimitiveTopology(IntPtr self, D3D_PRIMITIVE_TOPOLOGY* t)
        {
            if (t != null) *t = C(self)?._topology ?? D3D_PRIMITIVE_TOPOLOGY.UNDEFINED;
        }
    }
}
