// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// D3D11 buffers and textures over WebGPU buffers and textures (Documentation/d3d11-webgpu-shim.md,
// section 4).
//
// BUFFERS.
//   - A constant buffer has no GPU buffer of its own: it is the bytes the app last wrote, and each
//     draw uploads the bytes it sees into the context's constant arena. D3D11 orders an update between
//     two draws; a queue write would not (it lands before the whole submission).
//   - A DYNAMIC buffer keeps a CPU copy. Every Unmap gives it a fresh GPU buffer holding that copy
//     ("renaming"), so draws recorded earlier keep the old one -- exactly D3D11's DISCARD, and correct
//     (if wasteful) for NO_OVERWRITE.
//   - A DEFAULT/IMMUTABLE buffer is a GPU buffer; UpdateSubresource submits what is pending first and
//     then writes.
//   - A STAGING buffer is a mappable GPU buffer.
//
// TEXTURES. A texture is a WebGPU texture, except a STAGING one: that is a mappable buffer laid out
// subresource after subresource with 256-byte row pitch, which Map hands out with that pitch.
//

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using static Microsoft.Wpf.Interop.WebGpu.Wgpu;

namespace Microsoft.Wpf.Interop.WebGpu.Direct3D
{
    internal abstract unsafe class D3DResource : D3DChild
    {
        public uint EvictionPriority;
        /// <summary>Bumped on every change of the content seen from outside (tests, the D3DImage path).</summary>
        public int Version;

        protected D3DResource(D3DDevice device, params Vtable[] vt) : base(device, vt) { }

        public abstract D3D11_RESOURCE_DIMENSION Dimension { get; }

        public static void FillResource(Vtable vt)
        {
            FillDeviceChild(vt);
            vt.Set(7, (delegate* unmanaged<IntPtr, uint*, void>)&GetType);
            vt.Set(8, (delegate* unmanaged<IntPtr, uint, void>)&SetEvictionPriority);
            vt.Set(9, (delegate* unmanaged<IntPtr, uint>)&GetEvictionPriority);
        }

        [UnmanagedCallersOnly]
        private static void GetType(IntPtr self, uint* dim)
        {
            if (dim != null) *dim = (uint)(From<D3DResource>(self)?.Dimension ?? D3D11_RESOURCE_DIMENSION.UNKNOWN);
        }

        [UnmanagedCallersOnly]
        private static void SetEvictionPriority(IntPtr self, uint p) { var r = From<D3DResource>(self); if (r != null) r.EvictionPriority = p; }

        [UnmanagedCallersOnly]
        private static uint GetEvictionPriority(IntPtr self) => From<D3DResource>(self)?.EvictionPriority ?? 0;

        /// <summary>Map / Unmap through the context.</summary>
        public abstract int Map(D3DContext ctx, uint subresource, D3D11_MAP type, uint flags, D3D11_MAPPED_SUBRESOURCE* mapped);
        public abstract void Unmap(D3DContext ctx, uint subresource);
    }

    internal sealed unsafe class D3DBuffer : D3DResource
    {
        public readonly D3D11_BUFFER_DESC Desc;
        /// <summary>The GPU buffer (null for a constant buffer).</summary>
        public IntPtr Gpu;
        /// <summary>The CPU copy: a constant buffer's content, a dynamic buffer's mirror.</summary>
        public byte* Shadow;
        public readonly uint Size;
        private bool _mapped;

        private static readonly Vtable s_vt = Build();

        private static Vtable Build()
        {
            var vt = new Vtable("ID3D11Buffer", Vtbl.ID3D11Buffer.SlotCount, Vtbl.ID3D11Buffer.IID, Vtbl.ID3D11Resource.IID, Vtbl.ID3D11DeviceChild.IID);
            FillResource(vt);
            vt.Set(Vtbl.ID3D11Buffer.GetDesc, (delegate* unmanaged<IntPtr, D3D11_BUFFER_DESC*, void>)&GetDesc);
            return vt;
        }

        public bool IsConstant => (Desc.BindFlags & D3D11_BIND.CONSTANT_BUFFER) != 0;
        public bool IsStaging => Desc.Usage == D3D11_USAGE.STAGING;

        public override D3D11_RESOURCE_DIMENSION Dimension => D3D11_RESOURCE_DIMENSION.BUFFER;

        public D3DBuffer(D3DDevice device, D3D11_BUFFER_DESC desc, D3D11_SUBRESOURCE_DATA* init) : base(device, s_vt)
        {
            if (desc.ByteWidth == 0) throw new ArgumentException("ByteWidth 0");
            Desc = desc;
            Size = desc.ByteWidth;
            if (IsConstant || desc.Usage == D3D11_USAGE.DYNAMIC)
            {
                Shadow = (byte*)NativeMemory.AllocZeroed(Align4(Size));
                if (init != null && init->pSysMem != null) Buffer.MemoryCopy(init->pSysMem, Shadow, Size, Size);
            }
            if (!IsConstant)
            {
                var src = init != null && init->pSysMem != null ? new ReadOnlySpan<byte>(init->pSysMem, (int)Size) : default;
                Gpu = Create(src);
            }
        }

        private static nuint Align4(uint n) => (nuint)((n + 3) & ~3u);

        private WGPUBufferUsage Usage()
        {
            if (IsStaging)
                return (Desc.CPUAccessFlags & D3D11_CPU_ACCESS.READ) != 0 ? WGPUBufferUsage.MapRead | WGPUBufferUsage.CopyDst : WGPUBufferUsage.MapWrite | WGPUBufferUsage.CopySrc;
            var u = WGPUBufferUsage.CopyDst | WGPUBufferUsage.CopySrc;
            if ((Desc.BindFlags & D3D11_BIND.VERTEX_BUFFER) != 0) u |= WGPUBufferUsage.Vertex;
            if ((Desc.BindFlags & D3D11_BIND.INDEX_BUFFER) != 0) u |= WGPUBufferUsage.Index;
            if ((Desc.BindFlags & (D3D11_BIND.SHADER_RESOURCE | D3D11_BIND.UNORDERED_ACCESS | D3D11_BIND.STREAM_OUTPUT)) != 0) u |= WGPUBufferUsage.Storage;
            if ((Desc.MiscFlags & D3D11_RESOURCE_MISC.DRAWINDIRECT_ARGS) != 0) u |= WGPUBufferUsage.Indirect;
            return u;
        }

        private IntPtr Create(ReadOnlySpan<byte> data)
        {
            var usage = Usage();
            if (IsStaging || data.IsEmpty)
            {
                var desc = new WGPUBufferDescriptor { usage = usage, size = (Size + 3UL) & ~3UL };
                return wgpuDeviceCreateBuffer(Device.Gpu.Device, &desc);
            }
            return Device.Gpu.CreateBufferMapped(data, usage);
        }

        /// <summary>A dynamic buffer's Unmap: a new GPU buffer with the CPU copy (see the header).</summary>
        public void Rename()
        {
            IntPtr old = Gpu;
            Gpu = Device.Gpu.CreateBufferMapped(new ReadOnlySpan<byte>(Shadow, (int)Size), Usage());
            if (old != IntPtr.Zero) wgpuBufferRelease(old);
            Interlocked.Increment(ref Version);
        }

        public override int Map(D3DContext ctx, uint subresource, D3D11_MAP type, uint flags, D3D11_MAPPED_SUBRESOURCE* mapped)
        {
            if (mapped == null || subresource != 0) return HResult.E_INVALIDARG;
            if (Shadow != null && !IsStaging)
            {
                if (type is not (D3D11_MAP.WRITE_DISCARD or D3D11_MAP.WRITE_NO_OVERWRITE or D3D11_MAP.WRITE)) return HResult.E_INVALIDARG;
                mapped->pData = Shadow;
                mapped->RowPitch = Size;
                mapped->DepthPitch = Size;
                _mapped = true;
                return HResult.S_OK;
            }
            if (IsStaging)
            {
                ctx.FlushToGpu(wait: false);
                void* p = ctx.MapGpuBuffer(Gpu, Size, (Desc.CPUAccessFlags & D3D11_CPU_ACCESS.READ) != 0, (flags & 0x100000) != 0, out int hr);
                if (p == null) return hr;
                mapped->pData = p;
                mapped->RowPitch = Size;
                mapped->DepthPitch = Size;
                _mapped = true;
                return HResult.S_OK;
            }
            return HResult.E_INVALIDARG;
        }

        public override void Unmap(D3DContext ctx, uint subresource)
        {
            if (!_mapped) return;
            _mapped = false;
            if (IsStaging) { wgpuBufferUnmap(Gpu); return; }
            if (IsConstant) { Interlocked.Increment(ref Version); return; }
            Rename();
        }

        /// <summary>UpdateSubresource of a buffer.</summary>
        public void Update(D3DContext ctx, D3D11_BOX* box, void* data)
        {
            uint off = box == null ? 0 : box->left;
            uint end = box == null ? Size : Math.Min(Size, box->right);
            if (end <= off || data == null) return;
            if (Shadow != null) Buffer.MemoryCopy(data, Shadow + off, Size - off, end - off);
            if (IsConstant) { Interlocked.Increment(ref Version); return; }
            if (Desc.Usage == D3D11_USAGE.DYNAMIC) { Rename(); return; }
            ctx.WriteBufferOrdered(Gpu, off, new ReadOnlySpan<byte>(data, (int)(end - off)));
            Interlocked.Increment(ref Version);
        }

        [UnmanagedCallersOnly]
        private static void GetDesc(IntPtr self, D3D11_BUFFER_DESC* desc)
        {
            var b = From<D3DBuffer>(self);
            if (b != null && desc != null) *desc = b.Desc;
        }

        protected override void OnFinalRelease()
        {
            if (Gpu != IntPtr.Zero) wgpuBufferRelease(Gpu);
            Gpu = IntPtr.Zero;
            if (Shadow != null) NativeMemory.Free(Shadow);
            Shadow = null;
            base.OnFinalRelease();
        }
    }

    internal sealed unsafe class D3DTexture2D : D3DResource
    {
        public readonly D3D11_TEXTURE2D_DESC Desc;
        public readonly WGPUTextureFormat Format;
        public readonly uint MipLevels;
        public IntPtr Gpu;                   // WGPUTexture (not for staging)
        public IntPtr Staging;               // WGPUBuffer (staging only)
        public ulong StagingSize;
        public readonly ulong[] SubresourceOffset;
        public readonly uint[] SubresourceRowPitch;
        public readonly uint[] SubresourceRows;
        public IntPtr SharedHandle;
        private bool _mapped;

        private static readonly Vtable s_vt = Build();
        private static readonly Vtable s_dxgiResource = BuildDxgiResource();
        private static readonly Vtable s_dxgiSurface = BuildDxgiSurface();

        private static Vtable Build()
        {
            var vt = new Vtable("ID3D11Texture2D", Vtbl.ID3D11Texture2D1.SlotCount,
                Vtbl.ID3D11Texture2D.IID, Vtbl.ID3D11Texture2D1.IID, Vtbl.ID3D11Resource.IID, Vtbl.ID3D11DeviceChild.IID);
            FillResource(vt);
            vt.Set(Vtbl.ID3D11Texture2D.GetDesc, (delegate* unmanaged<IntPtr, D3D11_TEXTURE2D_DESC*, void>)&GetDesc);
            return vt;
        }

        public override D3D11_RESOURCE_DIMENSION Dimension => D3D11_RESOURCE_DIMENSION.TEXTURE2D;
        public bool IsStaging => Desc.Usage == D3D11_USAGE.STAGING;
        public uint SampleCount => Math.Max(1, Desc.SampleDesc.Count);

        public D3DTexture2D(D3DDevice device, D3D11_TEXTURE2D_DESC desc, D3D11_SUBRESOURCE_DATA* init)
            : base(device, s_vt, s_dxgiResource, s_dxgiSurface)
        {
            Format = Formats.Texture(desc.Format);
            if (Format == WGPUTextureFormat.Undefined) throw new NotSupportedException($"texture format {desc.Format}");
            if (desc.Width == 0 || desc.Height == 0) throw new ArgumentException("zero-sized texture");
            uint maxMips = 1 + (uint)Math.Floor(Math.Log2(Math.Max(desc.Width, desc.Height)));
            MipLevels = desc.MipLevels == 0 ? maxMips : Math.Min(desc.MipLevels, maxMips);
            desc.MipLevels = MipLevels;
            desc.ArraySize = Math.Max(1, desc.ArraySize);
            if (desc.SampleDesc.Count == 0) desc.SampleDesc.Count = 1;
            Desc = desc;
            int subs = (int)(MipLevels * desc.ArraySize);
            SubresourceOffset = new ulong[subs];
            SubresourceRowPitch = new uint[subs];
            SubresourceRows = new uint[subs];
            uint bpe = Formats.TexelBytes(desc.Format);
            bool bc = Formats.IsBlockCompressed(desc.Format);
            ulong offset = 0;
            for (uint a = 0; a < desc.ArraySize; a++)
                for (uint m = 0; m < MipLevels; m++)
                {
                    int i = (int)(a * MipLevels + m);
                    uint w = Math.Max(1, desc.Width >> (int)m), h = Math.Max(1, desc.Height >> (int)m);
                    if (bc) { w = (w + 3) / 4; h = (h + 3) / 4; }
                    uint pitch = (w * bpe + 255) & ~255u;
                    SubresourceOffset[i] = offset;
                    SubresourceRowPitch[i] = pitch;
                    SubresourceRows[i] = h;
                    offset += (ulong)pitch * h;
                }
            StagingSize = offset;

            if (IsStaging)
            {
                bool read = (desc.CPUAccessFlags & D3D11_CPU_ACCESS.READ) != 0;
                var bdesc = new WGPUBufferDescriptor
                {
                    usage = read ? WGPUBufferUsage.MapRead | WGPUBufferUsage.CopyDst : WGPUBufferUsage.MapWrite | WGPUBufferUsage.CopySrc,
                    size = Math.Max(4, offset),
                };
                Staging = wgpuDeviceCreateBuffer(Device.Gpu.Device, &bdesc);
                return;
            }

            var usage = WGPUTextureUsage.CopySrc | WGPUTextureUsage.CopyDst | WGPUTextureUsage.TextureBinding;
            if (!bc && ((desc.BindFlags & (D3D11_BIND.RENDER_TARGET | D3D11_BIND.DEPTH_STENCIL)) != 0 || (desc.MiscFlags & D3D11_RESOURCE_MISC.GENERATE_MIPS) != 0))
                usage |= WGPUTextureUsage.RenderAttachment;
            if (Formats.IsDepth(Format))
            {
                // depth formats: never written by copies, copied from only when 16- or 32-bit float
                usage = WGPUTextureUsage.RenderAttachment | WGPUTextureUsage.TextureBinding;
                if (Format is TexFmt.Depth32Float or TexFmt.Depth16Unorm) usage |= WGPUTextureUsage.CopySrc;
            }
            if (SampleCount > 1) usage = WGPUTextureUsage.RenderAttachment | WGPUTextureUsage.TextureBinding;   // no copies of multisampled textures
            var tdesc = new WGPUTextureDescriptor
            {
                usage = usage,
                dimension = WGPUTextureDimension._2D,
                size = new WGPUExtent3D { width = desc.Width, height = desc.Height, depthOrArrayLayers = desc.ArraySize },
                format = Format,
                mipLevelCount = MipLevels,
                sampleCount = SampleCount,
            };
            Gpu = wgpuDeviceCreateTexture(Device.Gpu.Device, &tdesc);
            if (Gpu == IntPtr.Zero) throw new InvalidOperationException("wgpuDeviceCreateTexture failed");

            if (init != null)
            {
                for (int i = 0; i < subs; i++)
                {
                    if (init[i].pSysMem == null) continue;
                    WriteSubresource(i, init[i].pSysMem, init[i].SysMemPitch, null);
                }
            }
        }

        public (uint Mip, uint Layer) Split(uint subresource) => (subresource % MipLevels, subresource / MipLevels);

        /// <summary>queue.writeTexture of one subresource (or a box of it) from app memory with the
        /// app's own row pitch.</summary>
        public void WriteSubresource(int sub, void* data, uint rowPitch, D3D11_BOX* box)
        {
            var (mip, layer) = Split((uint)sub);
            uint w = Math.Max(1, Desc.Width >> (int)mip), h = Math.Max(1, Desc.Height >> (int)mip);
            uint x = 0, y = 0;
            if (box != null) { x = box->left; y = box->top; w = box->right - box->left; h = box->bottom - box->top; }
            if (w == 0 || h == 0) return;
            bool bc = Formats.IsBlockCompressed(Desc.Format);
            uint rows = bc ? (h + 3) / 4 : h;
            uint bpe = Formats.TexelBytes(Desc.Format);
            uint tight = (bc ? (w + 3) / 4 : w) * bpe;
            if (rowPitch == 0) rowPitch = tight;
            var dst = new WGPUTexelCopyTextureInfo { texture = Gpu, mipLevel = mip, origin = new WGPUOrigin3D { x = x, y = y, z = layer }, aspect = WGPUTextureAspect.All };
            var layout = new WGPUTexelCopyBufferLayout { offset = 0, bytesPerRow = rowPitch, rowsPerImage = rows };
            var size = new WGPUExtent3D { width = w, height = h, depthOrArrayLayers = 1 };
            wgpuQueueWriteTexture(Device.Gpu.Queue, &dst, data, (nuint)((ulong)rowPitch * (rows - 1) + tight), &layout, &size);
            Interlocked.Increment(ref Version);
        }

        public override int Map(D3DContext ctx, uint subresource, D3D11_MAP type, uint flags, D3D11_MAPPED_SUBRESOURCE* mapped)
        {
            if (mapped == null) return HResult.E_INVALIDARG;
            if (!IsStaging) { D3DLog.Once("map-default-tex", "Map of a non-staging texture"); return HResult.E_INVALIDARG; }
            if (subresource >= SubresourceOffset.Length) return HResult.E_INVALIDARG;
            ctx.FlushToGpu(wait: false);
            byte* p = (byte*)ctx.MapGpuBuffer(Staging, StagingSize, (Desc.CPUAccessFlags & D3D11_CPU_ACCESS.READ) != 0, (flags & 0x100000) != 0, out int hr);
            if (p == null) return hr;
            mapped->pData = p + SubresourceOffset[subresource];
            mapped->RowPitch = SubresourceRowPitch[subresource];
            mapped->DepthPitch = SubresourceRowPitch[subresource] * SubresourceRows[subresource];
            _mapped = true;
            return HResult.S_OK;
        }

        public override void Unmap(D3DContext ctx, uint subresource)
        {
            if (!_mapped) return;
            _mapped = false;
            wgpuBufferUnmap(Staging);
        }

        [UnmanagedCallersOnly]
        private static void GetDesc(IntPtr self, D3D11_TEXTURE2D_DESC* desc)
        {
            var t = From<D3DTexture2D>(self);
            if (t != null && desc != null) *desc = t.Desc;
        }

        protected override void OnFinalRelease()
        {
            if (SharedHandle != IntPtr.Zero) SharedHandles.Remove(SharedHandle);
            if (Gpu != IntPtr.Zero) wgpuTextureRelease(Gpu);
            if (Staging != IntPtr.Zero) wgpuBufferRelease(Staging);
            Gpu = Staging = IntPtr.Zero;
            base.OnFinalRelease();
        }

        // ---- IDXGIResource / IDXGISurface (the same object, as on a real device) -------------------

        private static Vtable BuildDxgiResource()
        {
            var vt = new Vtable("IDXGIResource", Vtbl.IDXGIResource1.SlotCount, Vtbl.IDXGIResource.IID, Vtbl.IDXGIResource1.IID, Vtbl.IDXGIDeviceSubObject.IID, Vtbl.IDXGIObject.IID);
            FillDxgiObject(vt);
            vt.Set(Vtbl.IDXGIResource.GetSharedHandle, (delegate* unmanaged<IntPtr, IntPtr*, int>)&GetSharedHandle);
            vt.Set(Vtbl.IDXGIResource.GetUsage, (delegate* unmanaged<IntPtr, uint*, int>)&GetUsage);
            vt.Set(Vtbl.IDXGIResource.SetEvictionPriority, (delegate* unmanaged<IntPtr, uint, int>)&DxgiSetEviction);
            vt.Set(Vtbl.IDXGIResource.GetEvictionPriority, (delegate* unmanaged<IntPtr, uint*, int>)&DxgiGetEviction);
            return vt;
        }

        private static Vtable BuildDxgiSurface()
        {
            var vt = new Vtable("IDXGISurface", Vtbl.IDXGISurface2.SlotCount, Vtbl.IDXGISurface.IID, Vtbl.IDXGISurface1.IID, Vtbl.IDXGISurface2.IID);
            FillDxgiObject(vt);
            vt.Set(Vtbl.IDXGISurface.GetDesc, (delegate* unmanaged<IntPtr, DXGI_SURFACE_DESC*, int>)&SurfaceGetDesc);
            return vt;
        }

        private static void FillDxgiObject(Vtable vt)
        {
            vt.Set(Vtbl.IDXGIObject.SetPrivateData, (delegate* unmanaged<IntPtr, Guid*, uint, void*, int>)&DxgiSetPrivateData);
            vt.Set(Vtbl.IDXGIObject.SetPrivateDataInterface, (delegate* unmanaged<IntPtr, Guid*, IntPtr, int>)&DxgiSetPrivateDataInterface);
            vt.Set(Vtbl.IDXGIObject.GetPrivateData, (delegate* unmanaged<IntPtr, Guid*, uint*, void*, int>)&DxgiGetPrivateData);
            vt.Set(Vtbl.IDXGIObject.GetParent, (delegate* unmanaged<IntPtr, Guid*, IntPtr*, int>)&DxgiGetParent);
            vt.Set(Vtbl.IDXGIDeviceSubObject.GetDevice, (delegate* unmanaged<IntPtr, Guid*, IntPtr*, int>)&DxgiGetDevice);
        }

        [UnmanagedCallersOnly] private static int DxgiSetPrivateData(IntPtr self, Guid* g, uint n, void* d) => From<D3DChild>(self)?.Data.Set(g, n, d) ?? HResult.E_FAIL;
        [UnmanagedCallersOnly] private static int DxgiSetPrivateDataInterface(IntPtr self, Guid* g, IntPtr u) => From<D3DChild>(self)?.Data.SetInterface(g, u) ?? HResult.E_FAIL;
        [UnmanagedCallersOnly] private static int DxgiGetPrivateData(IntPtr self, Guid* g, uint* n, void* d) => From<D3DChild>(self)?.Data.Get(g, n, d) ?? HResult.E_FAIL;

        [UnmanagedCallersOnly]
        private static int DxgiGetParent(IntPtr self, Guid* iid, IntPtr* parent) => DxgiGetDeviceCore(self, iid, parent);

        [UnmanagedCallersOnly]
        private static int DxgiGetDevice(IntPtr self, Guid* iid, IntPtr* device) => DxgiGetDeviceCore(self, iid, device);

        private static int DxgiGetDeviceCore(IntPtr self, Guid* iid, IntPtr* device)
        {
            var c = From<D3DChild>(self);
            if (c == null || device == null || iid == null) return HResult.E_INVALIDARG;
            return D3DDevice.QueryInterfaceOf(c.Device, *iid, device);
        }

        [UnmanagedCallersOnly]
        private static int GetSharedHandle(IntPtr self, IntPtr* handle)
        {
            var t = From<D3DTexture2D>(self);
            if (t == null || handle == null) return HResult.E_INVALIDARG;
            if (t.SharedHandle == IntPtr.Zero) t.SharedHandle = SharedHandles.Add(t);
            *handle = t.SharedHandle;
            return HResult.S_OK;
        }

        [UnmanagedCallersOnly]
        private static int GetUsage(IntPtr self, uint* usage)
        {
            if (usage != null) *usage = 0x20 /* DXGI_USAGE_RENDER_TARGET_OUTPUT */ | 0x10 /* SHADER_INPUT */;
            return HResult.S_OK;
        }

        [UnmanagedCallersOnly] private static int DxgiSetEviction(IntPtr self, uint p) { var r = From<D3DResource>(self); if (r != null) r.EvictionPriority = p; return HResult.S_OK; }
        [UnmanagedCallersOnly] private static int DxgiGetEviction(IntPtr self, uint* p) { if (p != null) *p = From<D3DResource>(self)?.EvictionPriority ?? 0; return HResult.S_OK; }

        [UnmanagedCallersOnly]
        private static int SurfaceGetDesc(IntPtr self, DXGI_SURFACE_DESC* desc)
        {
            var t = From<D3DTexture2D>(self);
            if (t == null || desc == null) return HResult.E_INVALIDARG;
            desc->Width = t.Desc.Width; desc->Height = t.Desc.Height; desc->Format = t.Desc.Format; desc->SampleDesc = t.Desc.SampleDesc;
            return HResult.S_OK;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct DXGI_SURFACE_DESC { public uint Width, Height; public DXGI_FORMAT Format; public DXGI_SAMPLE_DESC SampleDesc; }

    /// <summary>Shared handles (IDXGIResource::GetSharedHandle): process-wide tokens for textures,
    /// which the D3D9 shim and OpenSharedResource resolve back to the same texture.</summary>
    internal static class SharedHandles
    {
        private static readonly Dictionary<IntPtr, WeakReference<D3DTexture2D>> s_table = new();
        private static long s_next = 0x40000000;

        public static IntPtr Add(D3DTexture2D t)
        {
            lock (s_table)
            {
                var h = (IntPtr)Interlocked.Add(ref s_next, 4);
                s_table[h] = new WeakReference<D3DTexture2D>(t);
                return h;
            }
        }

        public static D3DTexture2D? Find(IntPtr h)
        {
            lock (s_table) return s_table.TryGetValue(h, out var w) && w.TryGetTarget(out var t) ? t : null;
        }

        public static void Remove(IntPtr h)
        {
            lock (s_table) s_table.Remove(h);
        }
    }
}
