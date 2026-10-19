// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// ID3D11Device over a WebGPU device (Documentation/d3d11-webgpu-shim.md, sections 3 and 4).
//

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Wpf.Interop.WebGpu.Composition;
using static Microsoft.Wpf.Interop.WebGpu.Wgpu;

namespace Microsoft.Wpf.Interop.WebGpu.Direct3D
{
    /// <summary>Private data (SetPrivateData / GetPrivateData) and IUnknown private data, as every
    /// D3D11 and DXGI object keeps them. SharpDX names every object it makes through this.</summary>
    internal sealed unsafe class PrivateData
    {
        private readonly Dictionary<Guid, byte[]> _data = new();
        private readonly Dictionary<Guid, IntPtr> _unknowns = new();

        public int Get(Guid* guid, uint* size, void* data)
        {
            if (guid == null || size == null) return HResult.E_INVALIDARG;
            lock (_data)
            {
                if (_unknowns.TryGetValue(*guid, out IntPtr u))
                {
                    if (data == null) { *size = (uint)sizeof(IntPtr); return HResult.S_OK; }
                    if (*size < sizeof(IntPtr)) return HResult.DXGI_ERROR_INVALID_CALL;
                    if (u != IntPtr.Zero) Marshal.AddRef(u);
                    *(IntPtr*)data = u;
                    *size = (uint)sizeof(IntPtr);
                    return HResult.S_OK;
                }
                if (!_data.TryGetValue(*guid, out byte[]? b)) { *size = 0; return HResult.DXGI_ERROR_NOT_FOUND; }
                if (data == null) { *size = (uint)b.Length; return HResult.S_OK; }
                if (*size < b.Length) { *size = (uint)b.Length; return HResult.DXGI_ERROR_INVALID_CALL; }
                fixed (byte* p = b) Buffer.MemoryCopy(p, data, b.Length, b.Length);
                *size = (uint)b.Length;
                return HResult.S_OK;
            }
        }

        public int Set(Guid* guid, uint size, void* data)
        {
            if (guid == null) return HResult.E_INVALIDARG;
            lock (_data)
            {
                if (data == null || size == 0) { _data.Remove(*guid); return HResult.S_OK; }
                var b = new byte[size];
                fixed (byte* p = b) Buffer.MemoryCopy(data, p, size, size);
                _data[*guid] = b;
                return HResult.S_OK;
            }
        }

        public int SetInterface(Guid* guid, IntPtr unknown)
        {
            if (guid == null) return HResult.E_INVALIDARG;
            lock (_data)
            {
                if (_unknowns.TryGetValue(*guid, out IntPtr old) && old != IntPtr.Zero) Marshal.Release(old);
                if (unknown == IntPtr.Zero) { _unknowns.Remove(*guid); return HResult.S_OK; }
                Marshal.AddRef(unknown);
                _unknowns[*guid] = unknown;
                return HResult.S_OK;
            }
        }

        /// <summary>The debug name (WKPDID_D3DDebugObjectName), for logs.</summary>
        public string? Name
        {
            get
            {
                lock (_data) return _data.TryGetValue(DebugObjectName, out byte[]? b) ? Encoding.ASCII.GetString(b).TrimEnd('\0') : null;
            }
        }

        public static readonly Guid DebugObjectName = new("429b8c22-9188-4b0c-8742-acb0bf85c200");

        public void ReleaseAll()
        {
            lock (_data)
            {
                foreach (IntPtr u in _unknowns.Values) if (u != IntPtr.Zero) Marshal.Release(u);
                _unknowns.Clear();
            }
        }
    }

    /// <summary>An ID3D11DeviceChild: knows its device and keeps private data.</summary>
    internal abstract unsafe class D3DChild : ComObject
    {
        public readonly D3DDevice Device;
        public readonly PrivateData Data = new();

        protected D3DChild(D3DDevice device, params Vtable[] vt) : base(vt)
        {
            Device = device;
            device.InternalAddRef();
        }

        protected override void OnFinalRelease()
        {
            Data.ReleaseAll();
            Device.InternalRelease();
        }

        /// <summary>Slots 3..6 of every ID3D11DeviceChild-derived interface.</summary>
        public static void FillDeviceChild(Vtable vt)
        {
            vt.Set(3, (delegate* unmanaged<IntPtr, IntPtr*, void>)&GetDevice);
            vt.Set(4, (delegate* unmanaged<IntPtr, Guid*, uint*, void*, int>)&GetPrivateData);
            vt.Set(5, (delegate* unmanaged<IntPtr, Guid*, uint, void*, int>)&SetPrivateData);
            vt.Set(6, (delegate* unmanaged<IntPtr, Guid*, IntPtr, int>)&SetPrivateDataInterface);
        }

        [UnmanagedCallersOnly]
        private static void GetDevice(IntPtr self, IntPtr* device)
        {
            if (device == null) return;
            var c = From<D3DChild>(self);
            *device = c == null ? IntPtr.Zero : c.Device.AddRefPointer();
        }

        [UnmanagedCallersOnly]
        private static int GetPrivateData(IntPtr self, Guid* guid, uint* size, void* data) => From<D3DChild>(self)?.Data.Get(guid, size, data) ?? HResult.E_FAIL;

        [UnmanagedCallersOnly]
        private static int SetPrivateData(IntPtr self, Guid* guid, uint size, void* data) => From<D3DChild>(self)?.Data.Set(guid, size, data) ?? HResult.E_FAIL;

        [UnmanagedCallersOnly]
        private static int SetPrivateDataInterface(IntPtr self, Guid* guid, IntPtr unknown) => From<D3DChild>(self)?.Data.SetInterface(guid, unknown) ?? HResult.E_FAIL;
    }

    internal sealed unsafe partial class D3DDevice : ComObject
    {
        public readonly WgpuContext Gpu;
        public readonly D3D_FEATURE_LEVEL FeatureLevel;
        public readonly uint CreationFlags;
        public readonly PrivateData Data = new();
        public readonly D3DContext Immediate;
        public readonly DxgiAdapter Adapter;
        public readonly bool OwnsGpu;
        /// <summary>The device's lock: wgpu objects are thread-safe, but the shim's own caches are not.</summary>
        public readonly object Sync = new();
        public volatile int RemovedReason;

        private static readonly Vtable s_device = BuildDevice();
        private static readonly Vtable s_dxgiDevice = BuildDxgiDevice();
        private static readonly Vtable s_multithread = BuildMultithread();

        public D3DDevice(WgpuContext gpu, bool ownsGpu, D3D_FEATURE_LEVEL level, uint flags, DxgiAdapter? adapter)
            : base(s_device, s_dxgiDevice, s_multithread)
        {
            Gpu = gpu;
            OwnsGpu = ownsGpu;
            FeatureLevel = level;
            CreationFlags = flags;
            if (adapter != null) { Adapter = adapter; adapter.AddRef(); }
            else
            {
                var factory = DxgiFactory.Create();
                Adapter = new DxgiAdapter(factory, gpu);
                factory.Release();               // the adapter keeps it
            }
            Immediate = new D3DContext(this, deferred: false);
        }

        protected override void OnFinalRelease()
        {
            Immediate.FinalShutdown();
            Data.ReleaseAll();
            Adapter.Release();
            if (OwnsGpu) Gpu.Dispose();
        }

        // ---- creating the WebGPU device ------------------------------------------------------------

        private static readonly uint[] s_wantedFeatures =
        {
            0x3 /*Depth32FloatStencil8*/, 0x4 /*TextureCompressionBC*/, 0xA /*IndirectFirstInstance*/, 0xE /*Float32Filterable*/,
            0x9 /*TimestampQuery*/, 0x2 /*DepthClipControl*/, 0xC /*RG11B10UfloatRenderable*/, 0xF /*Float32Blendable*/,
            0x10 /*ClipDistances*/, 0x11 /*DualSourceBlending*/, 0x15 /*PrimitiveIndex*/, 0xD /*BGRA8UnormStorage*/,
            0x13 /*TextureFormatsTier1*/, 0x14 /*TextureFormatsTier2*/,
            0x3001D /*ShaderF64*/, 0x30026 /*ShaderInt64*/, 0x30027 /*ShaderFloat32Atomic*/, 0x30028 /*TextureAtomic*/, 0x30020 /*ShaderEarlyDepthTest*/,
        };

        /// <summary>The optional features the shared device was created with (the translator emits
        /// a feature's form where the device has it, and its fallback where not).</summary>
        public static IReadOnlyCollection<uint> Features => s_granted;

        private static readonly HashSet<uint> s_granted = new();

        public static bool HasFeature(uint feature) { _ = SharedGpu; return s_granted.Contains(feature); }

        public const uint FeatureClipDistances = 0x10, FeaturePrimitiveIndex = 0x15, FeatureShaderF64 = 0x3001D, FeatureTextureAtomic = 0x30028, FeatureDualSource = 0x11;

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        private static extern uint wgpuAdapterHasFeature(IntPtr adapter, uint feature);

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        private static extern int wgpuAdapterGetLimits(IntPtr adapter, byte* limits);

        [StructLayout(LayoutKind.Sequential)]
        private struct DeviceDescriptor
        {
            public IntPtr nextInChain;
            public WGPUStringView label;
            public nuint requiredFeatureCount;
            public uint* requiredFeatures;
            public IntPtr requiredLimits;
            public IntPtr queueNext; public WGPUStringView queueLabel;
            public IntPtr lostNext; public WGPUCallbackMode lostMode; public IntPtr lostCallback; public IntPtr lostUser1, lostUser2;
            public IntPtr errNext; public IntPtr errCallback; public IntPtr errUser1, errUser2;
        }

        private static DeviceDescriptor* s_desc;
        private static uint* s_features;
        private static byte* s_limits;      // a WGPULimits, filled by the adapter (its layout is never read here)

        /// <summary>A WebGPU device of its own for a D3D11 device: the features D3D11 at feature level
        /// 11 takes for granted where the adapter has them, and validation errors logged rather than
        /// fatal (wgpu-native aborts on an uncaptured error).</summary>
        /// <summary>The WebGPU device every shim device uses, as every Direct3D device on a machine
        /// uses its GPU: a texture shared between devices (a D3D9 surface of a D3D11 texture, a
        /// shared handle) is then one WebGPU texture. Never disposed.</summary>
        public static WgpuContext SharedGpu => s_sharedGpu.Value;

        private static readonly Lazy<WgpuContext> s_sharedGpu = new(CreateGpu, System.Threading.LazyThreadSafetyMode.ExecutionAndPublication);

        public static WgpuContext CreateGpu()
        {
            return WgpuContext.Create(adapter =>
            {
                if (s_desc == null)
                {
                    s_desc = (DeviceDescriptor*)NativeMemory.AllocZeroed((nuint)sizeof(DeviceDescriptor));
                    s_features = (uint*)NativeMemory.AllocZeroed((nuint)(s_wantedFeatures.Length * 4));
                }
                int n = 0;
                s_granted.Clear();
                foreach (uint f in s_wantedFeatures)
                    if (wgpuAdapterHasFeature(adapter, f) != 0) { s_features[n++] = f; s_granted.Add(f); }
                *s_desc = default;
                // the adapter's own limits, not WebGPU's defaults: D3D11 takes eight 128-bit render
                // targets, 128 shader resources and 16 samplers per stage for granted, and the
                // defaults (32 bytes of color attachments per sample, ...) refuse what an adapter
                // usually can do
                if (s_limits == null) s_limits = (byte*)NativeMemory.AllocZeroed(1024);
                new Span<byte>(s_limits, 1024).Clear();
                if (wgpuAdapterGetLimits(adapter, s_limits) == 1 /* WGPUStatus_Success */) s_desc->requiredLimits = (IntPtr)s_limits;
                s_desc->requiredFeatureCount = (nuint)n;
                s_desc->requiredFeatures = s_features;
                s_desc->lostMode = WGPUCallbackMode.AllowSpontaneous;
                s_desc->lostCallback = (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr*, int, WGPUStringView, IntPtr, IntPtr, void>)&OnLost;
                s_desc->errCallback = (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr*, WGPUErrorType, WGPUStringView, IntPtr, IntPtr, void>)&OnError;
                return (IntPtr)s_desc;
            });
        }

        internal static string Text(WGPUStringView v) => v.data == null ? "" : Encoding.UTF8.GetString(v.data, (int)Math.Min(v.length, 1 << 20));

        /// <summary>The last uncaptured validation error, for tests and logs.</summary>
        internal static volatile string? LastGpuError;
        internal static int GpuErrorCount;

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvCdecl) })]
        private static void OnError(IntPtr* device, WGPUErrorType type, WGPUStringView message, IntPtr u1, IntPtr u2)
        {
            string m = Text(message);
            LastGpuError = m;
            System.Threading.Interlocked.Increment(ref GpuErrorCount);
            D3DLog.Once("gpu:" + (m.Length > 200 ? m[..200] : m), $"WebGPU {type} error: {m}");
        }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvCdecl) })]
        private static void OnLost(IntPtr* device, int reason, WGPUStringView message, IntPtr u1, IntPtr u2)
        {
            if (reason != 2 /* destroyed */) D3DLog.Once("lost:" + reason, $"WebGPU device lost ({reason}): {Text(message)}");
        }

        // ---- vtables ------------------------------------------------------------------------------

        private static Vtable BuildDevice()
        {
            var vt = new Vtable("ID3D11Device", Vtbl.ID3D11Device5.SlotCount,
                Vtbl.ID3D11Device.IID, Vtbl.ID3D11Device1.IID, Vtbl.ID3D11Device2.IID, Vtbl.ID3D11Device3.IID, Vtbl.ID3D11Device4.IID, Vtbl.ID3D11Device5.IID);
            vt.Set(Vtbl.ID3D11Device.CreateBuffer, (delegate* unmanaged<IntPtr, D3D11_BUFFER_DESC*, D3D11_SUBRESOURCE_DATA*, IntPtr*, int>)&CreateBuffer);
            vt.Set(Vtbl.ID3D11Device.CreateTexture2D, (delegate* unmanaged<IntPtr, D3D11_TEXTURE2D_DESC*, D3D11_SUBRESOURCE_DATA*, IntPtr*, int>)&CreateTexture2D);
            vt.Set(Vtbl.ID3D11Device.CreateTexture1D, (delegate* unmanaged<IntPtr, D3D11_TEXTURE1D_DESC*, D3D11_SUBRESOURCE_DATA*, IntPtr*, int>)&CreateTexture1D);
            vt.Set(Vtbl.ID3D11Device.CreateTexture3D, (delegate* unmanaged<IntPtr, D3D11_TEXTURE3D_DESC*, D3D11_SUBRESOURCE_DATA*, IntPtr*, int>)&CreateTexture3D);
            vt.Set(Vtbl.ID3D11Device.CreateUnorderedAccessView, (delegate* unmanaged<IntPtr, IntPtr, D3D11_VIEW_DESC*, IntPtr*, int>)&CreateUnorderedAccessView);
            vt.Set(Vtbl.ID3D11Device.CreateComputeShader, (delegate* unmanaged<IntPtr, byte*, nuint, IntPtr, IntPtr*, int>)&CreateComputeShader);
            vt.Set(Vtbl.ID3D11Device.CreateHullShader, (delegate* unmanaged<IntPtr, byte*, nuint, IntPtr, IntPtr*, int>)&CreateHullShader);
            vt.Set(Vtbl.ID3D11Device.CreateDomainShader, (delegate* unmanaged<IntPtr, byte*, nuint, IntPtr, IntPtr*, int>)&CreateDomainShader);
            vt.Set(Vtbl.ID3D11Device.OpenSharedResource, (delegate* unmanaged<IntPtr, IntPtr, Guid*, IntPtr*, int>)&OpenSharedResource);
            vt.Set(Vtbl.ID3D11Device1.OpenSharedResource1, (delegate* unmanaged<IntPtr, IntPtr, Guid*, IntPtr*, int>)&OpenSharedResource);
            vt.Set(Vtbl.ID3D11Device.CreateShaderResourceView, (delegate* unmanaged<IntPtr, IntPtr, D3D11_VIEW_DESC*, IntPtr*, int>)&CreateShaderResourceView);
            vt.Set(Vtbl.ID3D11Device.CreateRenderTargetView, (delegate* unmanaged<IntPtr, IntPtr, D3D11_VIEW_DESC*, IntPtr*, int>)&CreateRenderTargetView);
            vt.Set(Vtbl.ID3D11Device.CreateDepthStencilView, (delegate* unmanaged<IntPtr, IntPtr, D3D11_DEPTH_STENCIL_VIEW_DESC*, IntPtr*, int>)&CreateDepthStencilView);
            vt.Set(Vtbl.ID3D11Device.CreateInputLayout, (delegate* unmanaged<IntPtr, D3D11_INPUT_ELEMENT_DESC*, uint, byte*, nuint, IntPtr*, int>)&CreateInputLayout);
            vt.Set(Vtbl.ID3D11Device.CreateVertexShader, (delegate* unmanaged<IntPtr, byte*, nuint, IntPtr, IntPtr*, int>)&CreateVertexShader);
            vt.Set(Vtbl.ID3D11Device.CreatePixelShader, (delegate* unmanaged<IntPtr, byte*, nuint, IntPtr, IntPtr*, int>)&CreatePixelShader);
            vt.Set(Vtbl.ID3D11Device.CreateGeometryShader, (delegate* unmanaged<IntPtr, byte*, nuint, IntPtr, IntPtr*, int>)&CreateGeometryShader);
            vt.Set(Vtbl.ID3D11Device.CreateBlendState, (delegate* unmanaged<IntPtr, D3D11_BLEND_DESC*, IntPtr*, int>)&CreateBlendState);
            vt.Set(Vtbl.ID3D11Device.CreateDepthStencilState, (delegate* unmanaged<IntPtr, D3D11_DEPTH_STENCIL_DESC*, IntPtr*, int>)&CreateDepthStencilState);
            vt.Set(Vtbl.ID3D11Device.CreateRasterizerState, (delegate* unmanaged<IntPtr, D3D11_RASTERIZER_DESC*, IntPtr*, int>)&CreateRasterizerState);
            vt.Set(Vtbl.ID3D11Device.CreateSamplerState, (delegate* unmanaged<IntPtr, D3D11_SAMPLER_DESC*, IntPtr*, int>)&CreateSamplerState);
            vt.Set(Vtbl.ID3D11Device.CheckFormatSupport, (delegate* unmanaged<IntPtr, DXGI_FORMAT, uint*, int>)&CheckFormatSupport);
            vt.Set(Vtbl.ID3D11Device.CheckMultisampleQualityLevels, (delegate* unmanaged<IntPtr, DXGI_FORMAT, uint, uint*, int>)&CheckMultisampleQualityLevels);
            vt.Set(Vtbl.ID3D11Device.CheckFeatureSupport, (delegate* unmanaged<IntPtr, uint, void*, uint, int>)&CheckFeatureSupport);
            vt.Set(Vtbl.ID3D11Device.GetPrivateData, (delegate* unmanaged<IntPtr, Guid*, uint*, void*, int>)&GetPrivateData);
            vt.Set(Vtbl.ID3D11Device.SetPrivateData, (delegate* unmanaged<IntPtr, Guid*, uint, void*, int>)&SetPrivateData);
            vt.Set(Vtbl.ID3D11Device.SetPrivateDataInterface, (delegate* unmanaged<IntPtr, Guid*, IntPtr, int>)&SetPrivateDataInterface);
            vt.Set(Vtbl.ID3D11Device.GetFeatureLevel, (delegate* unmanaged<IntPtr, uint>)&GetFeatureLevel);
            vt.Set(Vtbl.ID3D11Device.GetCreationFlags, (delegate* unmanaged<IntPtr, uint>)&GetCreationFlags);
            vt.Set(Vtbl.ID3D11Device.GetDeviceRemovedReason, (delegate* unmanaged<IntPtr, int>)&GetDeviceRemovedReason);
            vt.Set(Vtbl.ID3D11Device.GetImmediateContext, (delegate* unmanaged<IntPtr, IntPtr*, void>)&GetImmediateContext);
            vt.Set(Vtbl.ID3D11Device.SetExceptionMode, (delegate* unmanaged<IntPtr, uint, int>)&SetExceptionMode);
            vt.Set(Vtbl.ID3D11Device.GetExceptionMode, (delegate* unmanaged<IntPtr, uint>)&GetExceptionMode);
            vt.Set(Vtbl.ID3D11Device.CreateDeferredContext, (delegate* unmanaged<IntPtr, uint, IntPtr*, int>)&CreateDeferredContext);
            vt.Set(Vtbl.ID3D11Device1.GetImmediateContext1, (delegate* unmanaged<IntPtr, IntPtr*, void>)&GetImmediateContext);
            return vt;
        }

        private static D3DDevice? Self(IntPtr p) => From<D3DDevice>(p);

        private static int Guard(string where, Func<int> body)
        {
            try { return body(); }
            catch (NotSupportedException e) { D3DLog.Once("ns:" + where + e.Message, $"{where}: {e.Message}"); return HResult.E_NOTIMPL; }
            catch (Exception e) { D3DLog.Error(where, e); return HResult.E_FAIL; }
        }

        [UnmanagedCallersOnly]
        private static int CreateBuffer(IntPtr self, D3D11_BUFFER_DESC* desc, D3D11_SUBRESOURCE_DATA* init, IntPtr* buffer)
        {
            var d = Self(self);
            if (d == null || desc == null) return HResult.E_INVALIDARG;
            if (buffer != null) *buffer = IntPtr.Zero;
            var copy = *desc;
            return Guard("CreateBuffer", () => ComObject.Out(buffer, new D3DBuffer(d, copy, init)));
        }

        [UnmanagedCallersOnly]
        private static int CreateTexture2D(IntPtr self, D3D11_TEXTURE2D_DESC* desc, D3D11_SUBRESOURCE_DATA* init, IntPtr* texture)
        {
            var d = Self(self);
            if (d == null || desc == null) return HResult.E_INVALIDARG;
            if (texture != null) *texture = IntPtr.Zero;
            var copy = *desc;
            return Guard("CreateTexture2D", () => ComObject.Out(texture, new D3DTexture2D(d, copy, init)));
        }

        /// <summary>OpenSharedResource(1): a shim handle names a texture (made shared by D3D11 or
        /// D3D9); every shim device shares one WebGPU device, so it is that same texture.</summary>
        [UnmanagedCallersOnly]
        private static int OpenSharedResource(IntPtr self, IntPtr handle, Guid* iid, IntPtr* resource)
        {
            if (resource == null || iid == null) return HResult.E_INVALIDARG;
            *resource = IntPtr.Zero;
            D3DTexture2D? t = SharedHandles.Find(handle);
            if (t == null) { D3DLog.Once("openshared:" + handle, $"OpenSharedResource: 0x{(long)handle:x} is not a shim handle"); return HResult.E_INVALIDARG; }
            return QueryInterfaceOf(t, *iid, resource);
        }

        [UnmanagedCallersOnly]
        private static int CreateShaderResourceView(IntPtr self, IntPtr resource, D3D11_VIEW_DESC* desc, IntPtr* view)
        {
            var d = Self(self);
            var r = From<D3DResource>(resource);
            if (d == null || r == null) return HResult.E_INVALIDARG;
            if (view != null) *view = IntPtr.Zero;
            D3D11_VIEW_DESC? copy = desc == null ? null : *desc;
            return Guard("CreateShaderResourceView", () => ComObject.Out(view, new D3DShaderResourceView(d, r, copy)));
        }

        [UnmanagedCallersOnly]
        private static int CreateRenderTargetView(IntPtr self, IntPtr resource, D3D11_VIEW_DESC* desc, IntPtr* view)
        {
            var d = Self(self);
            var r = From<D3DResource>(resource);
            if (d == null || r is not D3DTexture2D t) return HResult.E_INVALIDARG;
            if (view != null) *view = IntPtr.Zero;
            D3D11_VIEW_DESC? copy = desc == null ? null : *desc;
            return Guard("CreateRenderTargetView", () => ComObject.Out(view, new D3DRenderTargetView(d, t, copy)));
        }

        [UnmanagedCallersOnly]
        private static int CreateDepthStencilView(IntPtr self, IntPtr resource, D3D11_DEPTH_STENCIL_VIEW_DESC* desc, IntPtr* view)
        {
            var d = Self(self);
            var r = From<D3DResource>(resource);
            if (d == null || r is not D3DTexture2D t) return HResult.E_INVALIDARG;
            if (view != null) *view = IntPtr.Zero;
            D3D11_DEPTH_STENCIL_VIEW_DESC? copy = desc == null ? null : *desc;
            return Guard("CreateDepthStencilView", () => ComObject.Out(view, new D3DDepthStencilView(d, t, copy)));
        }

        [UnmanagedCallersOnly]
        private static int CreateInputLayout(IntPtr self, D3D11_INPUT_ELEMENT_DESC* elements, uint count, byte* bytecode, nuint length, IntPtr* layout)
        {
            var d = Self(self);
            if (d == null || (count > 0 && elements == null)) return HResult.E_INVALIDARG;
            if (layout != null) *layout = IntPtr.Zero;
            var list = new D3DInputLayout.Element[count];
            for (int i = 0; i < count; i++)
            {
                var e = elements[i];
                list[i] = new D3DInputLayout.Element
                {
                    Semantic = Marshal.PtrToStringAnsi((IntPtr)e.SemanticName) ?? "",
                    Index = (int)e.SemanticIndex, Format = e.Format, Slot = (int)e.InputSlot, Offset = e.AlignedByteOffset,
                    PerInstance = e.InputSlotClass == 1, StepRate = e.InstanceDataStepRate,
                };
            }
            return Guard("CreateInputLayout", () => ComObject.Out(layout, new D3DInputLayout(d, list)));
        }

        private static int CreateShader(IntPtr self, byte* bytecode, nuint length, IntPtr* shader, Dxbc.ShaderStage expected, string name)
        {
            var d = Self(self);
            if (d == null || bytecode == null || length < 32) return HResult.E_INVALIDARG;
            if (shader != null) *shader = IntPtr.Zero;
            var blob = new ReadOnlySpan<byte>(bytecode, (int)length).ToArray();
            return Guard(name, () =>
            {
                var parsed = Dxbc.DxbcShader.Parse(blob);
                if (parsed.Stage != expected) return HResult.E_INVALIDARG;
                return ComObject.Out(shader, new D3DShader(d, parsed));
            });
        }

        [UnmanagedCallersOnly]
        private static int CreateVertexShader(IntPtr self, byte* bytecode, nuint length, IntPtr linkage, IntPtr* shader)
            => CreateShader(self, bytecode, length, shader, Dxbc.ShaderStage.Vertex, "CreateVertexShader");

        [UnmanagedCallersOnly]
        private static int CreatePixelShader(IntPtr self, byte* bytecode, nuint length, IntPtr linkage, IntPtr* shader)
            => CreateShader(self, bytecode, length, shader, Dxbc.ShaderStage.Pixel, "CreatePixelShader");

        [UnmanagedCallersOnly]
        private static int CreateGeometryShader(IntPtr self, byte* bytecode, nuint length, IntPtr linkage, IntPtr* shader)
            => CreateShader(self, bytecode, length, shader, Dxbc.ShaderStage.Geometry, "CreateGeometryShader");

        [UnmanagedCallersOnly]
        private static int CreateComputeShader(IntPtr self, byte* bytecode, nuint length, IntPtr linkage, IntPtr* shader)
            => CreateShader(self, bytecode, length, shader, Dxbc.ShaderStage.Compute, "CreateComputeShader");

        [UnmanagedCallersOnly]
        private static int CreateHullShader(IntPtr self, byte* bytecode, nuint length, IntPtr linkage, IntPtr* shader)
            => CreateShader(self, bytecode, length, shader, Dxbc.ShaderStage.Hull, "CreateHullShader");

        [UnmanagedCallersOnly]
        private static int CreateDomainShader(IntPtr self, byte* bytecode, nuint length, IntPtr linkage, IntPtr* shader)
            => CreateShader(self, bytecode, length, shader, Dxbc.ShaderStage.Domain, "CreateDomainShader");

        [UnmanagedCallersOnly]
        private static int CreateTexture1D(IntPtr self, D3D11_TEXTURE1D_DESC* desc, D3D11_SUBRESOURCE_DATA* init, IntPtr* texture)
        {
            var d = Self(self);
            if (d == null || desc == null) return HResult.E_INVALIDARG;
            if (texture != null) *texture = IntPtr.Zero;
            var copy = *desc;
            return Guard("CreateTexture1D", () => ComObject.Out(texture, D3DTexture2D.Create1D(d, copy, init)));
        }

        [UnmanagedCallersOnly]
        private static int CreateTexture3D(IntPtr self, D3D11_TEXTURE3D_DESC* desc, D3D11_SUBRESOURCE_DATA* init, IntPtr* texture)
        {
            var d = Self(self);
            if (d == null || desc == null) return HResult.E_INVALIDARG;
            if (texture != null) *texture = IntPtr.Zero;
            var copy = *desc;
            return Guard("CreateTexture3D", () => ComObject.Out(texture, D3DTexture2D.Create3D(d, copy, init)));
        }

        [UnmanagedCallersOnly]
        private static int CreateUnorderedAccessView(IntPtr self, IntPtr resource, D3D11_VIEW_DESC* desc, IntPtr* view)
        {
            var d = Self(self);
            var r = From<D3DResource>(resource);
            if (d == null || r == null) return HResult.E_INVALIDARG;
            if (view != null) *view = IntPtr.Zero;
            D3D11_VIEW_DESC? copy = desc == null ? null : *desc;
            return Guard("CreateUnorderedAccessView", () => ComObject.Out(view, new D3DUnorderedAccessView(d, r, copy)));
        }

        [UnmanagedCallersOnly]
        private static int CreateBlendState(IntPtr self, D3D11_BLEND_DESC* desc, IntPtr* state)
        {
            var d = Self(self);
            if (d == null || desc == null) return HResult.E_INVALIDARG;
            var copy = *desc;
            return Guard("CreateBlendState", () => ComObject.Out(state, new D3DBlendState(d, copy)));
        }

        [UnmanagedCallersOnly]
        private static int CreateDepthStencilState(IntPtr self, D3D11_DEPTH_STENCIL_DESC* desc, IntPtr* state)
        {
            var d = Self(self);
            if (d == null || desc == null) return HResult.E_INVALIDARG;
            var copy = *desc;
            return Guard("CreateDepthStencilState", () => ComObject.Out(state, new D3DDepthStencilState(d, copy)));
        }

        [UnmanagedCallersOnly]
        private static int CreateRasterizerState(IntPtr self, D3D11_RASTERIZER_DESC* desc, IntPtr* state)
        {
            var d = Self(self);
            if (d == null || desc == null) return HResult.E_INVALIDARG;
            var copy = *desc;
            return Guard("CreateRasterizerState", () => ComObject.Out(state, new D3DRasterizerState(d, copy)));
        }

        [UnmanagedCallersOnly]
        private static int CreateSamplerState(IntPtr self, D3D11_SAMPLER_DESC* desc, IntPtr* state)
        {
            var d = Self(self);
            if (d == null || desc == null) return HResult.E_INVALIDARG;
            var copy = *desc;
            return Guard("CreateSamplerState", () => ComObject.Out(state, new D3DSamplerState(d, copy)));
        }

        [UnmanagedCallersOnly]
        private static int CreateDeferredContext(IntPtr self, uint flags, IntPtr* context)
        {
            var d = Self(self);
            if (d == null || context == null) return HResult.E_INVALIDARG;
            return Guard("CreateDeferredContext", () => ComObject.Out(context, new D3DContext(d, deferred: true)));
        }

        // D3D11_FORMAT_SUPPORT bits
        private const uint FS_BUFFER = 0x1, FS_IA_VERTEX = 0x2, FS_IA_INDEX = 0x4, FS_TEXTURE1D = 0x10, FS_TEXTURE2D = 0x20, FS_TEXTURE3D = 0x40,
            FS_TEXTURECUBE = 0x80, FS_SHADER_LOAD = 0x100, FS_SHADER_SAMPLE = 0x200, FS_MIP = 0x1000, FS_MIP_AUTOGEN = 0x2000, FS_RENDER_TARGET = 0x4000,
            FS_BLENDABLE = 0x8000, FS_DEPTH_STENCIL = 0x10000, FS_CPU_LOCKABLE = 0x20000, FS_MS_RESOLVE = 0x40000, FS_DISPLAY = 0x80000,
            FS_MS_RENDERTARGET = 0x200000, FS_MS_LOAD = 0x400000, FS_SHADER_GATHER = 0x800000, FS_SHADER_SAMPLE_COMPARISON = 0x80;

        [UnmanagedCallersOnly]
        private static int CheckFormatSupport(IntPtr self, DXGI_FORMAT format, uint* support)
        {
            if (support == null) return HResult.E_INVALIDARG;
            *support = FormatSupport(format);
            return *support == 0 ? HResult.E_FAIL : HResult.S_OK;
        }

        internal static uint FormatSupport(DXGI_FORMAT format)
        {
            var w = Formats.Texture(format);
            uint s = 0;
            if (Formats.Vertex(format) != 0) s |= FS_IA_VERTEX | FS_BUFFER;
            if (format is DXGI_FORMAT.R16_UINT or DXGI_FORMAT.R32_UINT) s |= FS_IA_INDEX | FS_BUFFER;
            if (w == WGPUTextureFormat.Undefined) return s;
            s |= FS_TEXTURE2D | FS_TEXTURECUBE | FS_MIP | FS_CPU_LOCKABLE | FS_SHADER_LOAD;
            if (Formats.IsDepth(w)) return s | FS_DEPTH_STENCIL | FS_SHADER_SAMPLE_COMPARISON | FS_MS_RENDERTARGET;
            s |= FS_TEXTURE1D | FS_TEXTURE3D | FS_SHADER_SAMPLE | FS_SHADER_GATHER;
            if (!Formats.IsBlockCompressed(format))
            {
                s |= FS_RENDER_TARGET | FS_MIP_AUTOGEN | FS_MS_RENDERTARGET | FS_MS_RESOLVE | FS_MS_LOAD;
                if (format is not (DXGI_FORMAT.R32G32B32A32_UINT or DXGI_FORMAT.R32G32B32A32_SINT or DXGI_FORMAT.R32_UINT or DXGI_FORMAT.R32_SINT or DXGI_FORMAT.R8G8B8A8_UINT or DXGI_FORMAT.R16G16_UINT))
                    s |= FS_BLENDABLE;
            }
            if (format is DXGI_FORMAT.B8G8R8A8_UNORM or DXGI_FORMAT.R8G8B8A8_UNORM or DXGI_FORMAT.B8G8R8A8_UNORM_SRGB or DXGI_FORMAT.R8G8B8A8_UNORM_SRGB or DXGI_FORMAT.R16G16B16A16_FLOAT or DXGI_FORMAT.R10G10B10A2_UNORM)
                s |= FS_DISPLAY;
            return s;
        }

        [UnmanagedCallersOnly]
        private static int CheckMultisampleQualityLevels(IntPtr self, DXGI_FORMAT format, uint samples, uint* levels)
        {
            if (levels == null) return HResult.E_INVALIDARG;
            // WebGPU offers 1 and 4 samples.
            *levels = (samples == 1 || samples == 4) && Formats.Texture(format) != WGPUTextureFormat.Undefined && !Formats.IsBlockCompressed(format) ? 1u : 0u;
            return HResult.S_OK;
        }

        [UnmanagedCallersOnly]
        private static int CheckFeatureSupport(IntPtr self, uint feature, void* data, uint size)
        {
            if (data == null) return HResult.E_INVALIDARG;
            NativeMemory.Clear(data, size);
            switch (feature)
            {
                case 0:   // D3D11_FEATURE_THREADING: concurrent creates yes, command lists emulated
                    if (size < 8) return HResult.E_INVALIDARG;
                    ((int*)data)[0] = 1; ((int*)data)[1] = 0;
                    return HResult.S_OK;
                case 1:   // DOUBLES
                case 2:   // FORMAT_SUPPORT (handled below)
                case 3:   // FORMAT_SUPPORT2
                case 4:   // D3D10_X_HARDWARE_OPTIONS: compute on 10.x
                case 5:   // D3D11_OPTIONS
                case 6:   // ARCHITECTURE_INFO
                case 7:   // D3D9_OPTIONS
                case 8:   // SHADER_MIN_PRECISION_SUPPORT
                case 9:   // D3D9_SHADOW_SUPPORT
                    if (feature == 2 && size >= 8) ((uint*)data)[1] = FormatSupport((DXGI_FORMAT)((uint*)data)[0]);
                    if (feature == 9 && size >= 4) ((int*)data)[0] = 1;
                    return HResult.S_OK;
                default:
                    return HResult.S_OK;     // every capability the shim does not know of reads as absent
            }
        }

        [UnmanagedCallersOnly]
        private static int GetPrivateData(IntPtr self, Guid* guid, uint* size, void* data) => Self(self)?.Data.Get(guid, size, data) ?? HResult.E_FAIL;

        [UnmanagedCallersOnly]
        private static int SetPrivateData(IntPtr self, Guid* guid, uint size, void* data) => Self(self)?.Data.Set(guid, size, data) ?? HResult.E_FAIL;

        [UnmanagedCallersOnly]
        private static int SetPrivateDataInterface(IntPtr self, Guid* guid, IntPtr unknown) => Self(self)?.Data.SetInterface(guid, unknown) ?? HResult.E_FAIL;

        [UnmanagedCallersOnly]
        private static uint GetFeatureLevel(IntPtr self) => (uint)(Self(self)?.FeatureLevel ?? D3D_FEATURE_LEVEL.L11_0);

        [UnmanagedCallersOnly]
        private static uint GetCreationFlags(IntPtr self) => Self(self)?.CreationFlags ?? 0;

        [UnmanagedCallersOnly]
        private static int GetDeviceRemovedReason(IntPtr self) => Self(self)?.RemovedReason ?? 0;

        [UnmanagedCallersOnly]
        private static void GetImmediateContext(IntPtr self, IntPtr* context)
        {
            if (context == null) return;
            var d = Self(self);
            *context = d == null ? IntPtr.Zero : d.Immediate.AddRefPointer();
        }

        private uint _exceptionMode;

        [UnmanagedCallersOnly]
        private static int SetExceptionMode(IntPtr self, uint flags)
        {
            var d = Self(self);
            if (d != null) d._exceptionMode = flags;
            return HResult.S_OK;
        }

        [UnmanagedCallersOnly]
        private static uint GetExceptionMode(IntPtr self) => Self(self)?._exceptionMode ?? 0;

        // ---- IDXGIDevice (the same object, as on a real device) --------------------------------------

        private static Vtable BuildDxgiDevice()
        {
            var vt = new Vtable("IDXGIDevice", Vtbl.IDXGIDevice4.SlotCount,
                Vtbl.IDXGIDevice.IID, Vtbl.IDXGIDevice1.IID, Vtbl.IDXGIDevice2.IID, Vtbl.IDXGIDevice3.IID, Vtbl.IDXGIDevice4.IID, Vtbl.IDXGIObject.IID);
            vt.Set(Vtbl.IDXGIObject.SetPrivateData, (delegate* unmanaged<IntPtr, Guid*, uint, void*, int>)&SetPrivateData);
            vt.Set(Vtbl.IDXGIObject.SetPrivateDataInterface, (delegate* unmanaged<IntPtr, Guid*, IntPtr, int>)&SetPrivateDataInterface);
            vt.Set(Vtbl.IDXGIObject.GetPrivateData, (delegate* unmanaged<IntPtr, Guid*, uint*, void*, int>)&GetPrivateData);
            vt.Set(Vtbl.IDXGIObject.GetParent, (delegate* unmanaged<IntPtr, Guid*, IntPtr*, int>)&DxgiGetParent);
            vt.Set(Vtbl.IDXGIDevice.GetAdapter, (delegate* unmanaged<IntPtr, IntPtr*, int>)&DxgiGetAdapter);
            vt.Set(Vtbl.IDXGIDevice.SetGPUThreadPriority, (delegate* unmanaged<IntPtr, int, int>)&DxgiSetPriority);
            vt.Set(Vtbl.IDXGIDevice.GetGPUThreadPriority, (delegate* unmanaged<IntPtr, int*, int>)&DxgiGetPriority);
            vt.Set(Vtbl.IDXGIDevice1.SetMaximumFrameLatency, (delegate* unmanaged<IntPtr, uint, int>)&DxgiSetLatency);
            vt.Set(Vtbl.IDXGIDevice1.GetMaximumFrameLatency, (delegate* unmanaged<IntPtr, uint*, int>)&DxgiGetLatency);
            vt.Set(Vtbl.IDXGIDevice2.OfferResources, (delegate* unmanaged<IntPtr, uint, IntPtr*, int, int>)&DxgiOffer);
            vt.Set(Vtbl.IDXGIDevice2.ReclaimResources, (delegate* unmanaged<IntPtr, uint, IntPtr*, int*, int>)&DxgiReclaim);
            vt.Set(Vtbl.IDXGIDevice2.EnqueueSetEvent, (delegate* unmanaged<IntPtr, IntPtr, int>)&DxgiEnqueueSetEvent);
            vt.Set(Vtbl.IDXGIDevice3.Trim, (delegate* unmanaged<IntPtr, void>)&DxgiTrim);
            return vt;
        }

        [UnmanagedCallersOnly]
        private static int DxgiGetParent(IntPtr self, Guid* iid, IntPtr* parent)
        {
            var d = Self(self);
            if (d == null || parent == null) return HResult.E_INVALIDARG;
            return d.Adapter.QueryInterfaceManaged(*iid, parent);
        }

        [UnmanagedCallersOnly]
        private static int DxgiGetAdapter(IntPtr self, IntPtr* adapter)
        {
            var d = Self(self);
            if (d == null || adapter == null) return HResult.E_INVALIDARG;
            *adapter = d.Adapter.AddRefPointer();
            return HResult.S_OK;
        }

        private int _priority;
        private uint _latency = 3;

        [UnmanagedCallersOnly] private static int DxgiSetPriority(IntPtr self, int p) { var d = Self(self); if (d != null) d._priority = p; return HResult.S_OK; }
        [UnmanagedCallersOnly] private static int DxgiGetPriority(IntPtr self, int* p) { if (p != null) *p = Self(self)?._priority ?? 0; return HResult.S_OK; }
        [UnmanagedCallersOnly] private static int DxgiSetLatency(IntPtr self, uint l) { var d = Self(self); if (d != null) d._latency = l; return HResult.S_OK; }
        [UnmanagedCallersOnly] private static int DxgiGetLatency(IntPtr self, uint* l) { if (l != null) *l = Self(self)?._latency ?? 3; return HResult.S_OK; }
        [UnmanagedCallersOnly] private static int DxgiOffer(IntPtr self, uint n, IntPtr* r, int p) => HResult.S_OK;
        [UnmanagedCallersOnly] private static int DxgiReclaim(IntPtr self, uint n, IntPtr* r, int* discarded) { if (discarded != null) for (int i = 0; i < n; i++) discarded[i] = 0; return HResult.S_OK; }
        [UnmanagedCallersOnly] private static void DxgiTrim(IntPtr self) { }

        /// <summary>IDXGIDevice2::EnqueueSetEvent: the event is set once the GPU has done the work
        /// submitted so far.</summary>
        [UnmanagedCallersOnly]
        private static int DxgiEnqueueSetEvent(IntPtr self, IntPtr hEvent)
        {
            var d = Self(self);
            if (d == null) return HResult.E_INVALIDARG;
            return Guard("EnqueueSetEvent", () =>
            {
                d.Immediate.FlushToGpu(wait: true);
                Win32Events.Set(hEvent);
                return HResult.S_OK;
            });
        }

        // ---- ID3D10Multithread / ID3D11Multithread: always protected -------------------------------

        private static readonly Guid IID_ID3D10Multithread = new("9b7e4e00-342c-4106-a19f-4f2704f689f0");
        private static readonly Guid IID_ID3D11Multithread = new("9b7e4e00-342c-4106-a19f-4f2704f689f0");

        private static Vtable BuildMultithread()
        {
            var vt = new Vtable("ID3D10Multithread", 7, IID_ID3D10Multithread);
            vt.Set(3, (delegate* unmanaged<IntPtr, void>)&MtEnter);
            vt.Set(4, (delegate* unmanaged<IntPtr, void>)&MtLeave);
            vt.Set(5, (delegate* unmanaged<IntPtr, int, int>)&MtSet);
            vt.Set(6, (delegate* unmanaged<IntPtr, int>)&MtGet);
            return vt;
        }

        [UnmanagedCallersOnly] private static void MtEnter(IntPtr self) { var d = Self(self); if (d != null) System.Threading.Monitor.Enter(d.Sync); }
        [UnmanagedCallersOnly] private static void MtLeave(IntPtr self) { var d = Self(self); if (d != null && System.Threading.Monitor.IsEntered(d.Sync)) System.Threading.Monitor.Exit(d.Sync); }
        [UnmanagedCallersOnly] private static int MtSet(IntPtr self, int on) => 1;
        [UnmanagedCallersOnly] private static int MtGet(IntPtr self) => 1;

        /// <summary>QueryInterface from the outside (managed callers), with a reference.</summary>
        public int QueryInterfaceManaged(Guid iid, IntPtr* ppv) => QueryInterfaceOf(this, iid, ppv);

        internal static int QueryInterfaceOf(ComObject o, Guid iid, IntPtr* ppv)
        {
            IntPtr p = o.Pointer;
            var qi = (delegate* unmanaged<IntPtr, Guid*, IntPtr*, int>)(*(IntPtr**)p)[0];
            return qi(p, &iid, ppv);
        }
    }
}
