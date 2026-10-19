// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// DXGI: the factory and the adapter, both describing the WebGPU adapter.
//

using System;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Wpf.Interop.WebGpu.Composition;
using static Microsoft.Wpf.Interop.WebGpu.Wgpu;

namespace Microsoft.Wpf.Interop.WebGpu.Direct3D
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal unsafe struct DXGI_ADAPTER_DESC1
    {
        public fixed char Description[128];
        public uint VendorId, DeviceId, SubSysId, Revision;
        public nuint DedicatedVideoMemory, DedicatedSystemMemory, SharedSystemMemory;
        public uint LuidLow;
        public int LuidHigh;
        public uint Flags;                    // DESC1 only
    }

    internal sealed unsafe class DxgiFactory : ComObject
    {
        public readonly PrivateData Data = new();
        private IntPtr _window;
        private DxgiAdapter? _adapter;
        private static readonly Vtable s_vt = Build();

        private static Vtable Build()
        {
            var vt = new Vtable("IDXGIFactory", Vtbl.IDXGIFactory7.SlotCount,
                Vtbl.IDXGIFactory.IID, Vtbl.IDXGIFactory1.IID, Vtbl.IDXGIFactory2.IID, Vtbl.IDXGIFactory3.IID, Vtbl.IDXGIFactory4.IID,
                Vtbl.IDXGIFactory5.IID, Vtbl.IDXGIFactory6.IID, Vtbl.IDXGIFactory7.IID, Vtbl.IDXGIObject.IID);
            vt.Set(Vtbl.IDXGIObject.SetPrivateData, (delegate* unmanaged<IntPtr, Guid*, uint, void*, int>)&SetPrivateData);
            vt.Set(Vtbl.IDXGIObject.SetPrivateDataInterface, (delegate* unmanaged<IntPtr, Guid*, IntPtr, int>)&SetPrivateDataInterface);
            vt.Set(Vtbl.IDXGIObject.GetPrivateData, (delegate* unmanaged<IntPtr, Guid*, uint*, void*, int>)&GetPrivateData);
            vt.Set(Vtbl.IDXGIFactory.EnumAdapters, (delegate* unmanaged<IntPtr, uint, IntPtr*, int>)&EnumAdapters);
            vt.Set(Vtbl.IDXGIFactory.MakeWindowAssociation, (delegate* unmanaged<IntPtr, IntPtr, uint, int>)&MakeWindowAssociation);
            vt.Set(Vtbl.IDXGIFactory.GetWindowAssociation, (delegate* unmanaged<IntPtr, IntPtr*, int>)&GetWindowAssociation);
            vt.Set(Vtbl.IDXGIFactory1.EnumAdapters1, (delegate* unmanaged<IntPtr, uint, IntPtr*, int>)&EnumAdapters);
            vt.Set(Vtbl.IDXGIFactory1.IsCurrent, (delegate* unmanaged<IntPtr, int>)&IsCurrent);
            vt.Set(Vtbl.IDXGIFactory2.IsWindowedStereoEnabled, (delegate* unmanaged<IntPtr, int>)&False);
            vt.Set(Vtbl.IDXGIFactory3.GetCreationFlags, (delegate* unmanaged<IntPtr, uint>)&CreationFlags);
            vt.Set(Vtbl.IDXGIFactory6.EnumAdapterByGpuPreference, (delegate* unmanaged<IntPtr, uint, uint, Guid*, IntPtr*, int>)&EnumByPreference);
            return vt;
        }

        private DxgiFactory() : base(s_vt) { }

        public static DxgiFactory Create() => new();

        /// <summary>The one adapter: the WebGPU adapter of a device made for it (made on first ask).</summary>
        public DxgiAdapter Adapter()
        {
            lock (Data)
            {
                if (_adapter == null) _adapter = new DxgiAdapter(this, null);
                return _adapter;
            }
        }

        protected override void OnFinalRelease()
        {
            Data.ReleaseAll();
            _adapter?.InternalRelease();
        }

        private static DxgiFactory? Self(IntPtr p) => From<DxgiFactory>(p);

        [UnmanagedCallersOnly] private static int SetPrivateData(IntPtr self, Guid* g, uint n, void* d) => Self(self)?.Data.Set(g, n, d) ?? HResult.E_FAIL;
        [UnmanagedCallersOnly] private static int SetPrivateDataInterface(IntPtr self, Guid* g, IntPtr u) => Self(self)?.Data.SetInterface(g, u) ?? HResult.E_FAIL;
        [UnmanagedCallersOnly] private static int GetPrivateData(IntPtr self, Guid* g, uint* n, void* d) => Self(self)?.Data.Get(g, n, d) ?? HResult.E_FAIL;

        [UnmanagedCallersOnly]
        private static int EnumAdapters(IntPtr self, uint index, IntPtr* adapter)
        {
            var f = Self(self);
            if (f == null || adapter == null) return HResult.E_INVALIDARG;
            *adapter = IntPtr.Zero;
            if (index > 0) return HResult.DXGI_ERROR_NOT_FOUND;
            try
            {
                var a = f.Adapter();
                *adapter = a.AddRefPointer();
                return HResult.S_OK;
            }
            catch (Exception e) { D3DLog.Error("EnumAdapters", e); return HResult.DXGI_ERROR_NOT_FOUND; }
        }

        [UnmanagedCallersOnly]
        private static int EnumByPreference(IntPtr self, uint index, uint pref, Guid* iid, IntPtr* adapter)
        {
            var f = Self(self);
            if (f == null || adapter == null || iid == null) return HResult.E_INVALIDARG;
            *adapter = IntPtr.Zero;
            if (index > 0) return HResult.DXGI_ERROR_NOT_FOUND;
            return D3DDevice.QueryInterfaceOf(f.Adapter(), *iid, adapter);
        }

        [UnmanagedCallersOnly] private static int MakeWindowAssociation(IntPtr self, IntPtr window, uint flags) { var f = Self(self); if (f != null) f._window = window; return HResult.S_OK; }
        [UnmanagedCallersOnly] private static int GetWindowAssociation(IntPtr self, IntPtr* window) { if (window != null) *window = Self(self)?._window ?? IntPtr.Zero; return HResult.S_OK; }
        [UnmanagedCallersOnly] private static int IsCurrent(IntPtr self) => 1;
        [UnmanagedCallersOnly] private static int False(IntPtr self) => 0;
        [UnmanagedCallersOnly] private static uint CreationFlags(IntPtr self) => 0;
    }

    internal sealed unsafe class DxgiAdapter : ComObject
    {
        public readonly DxgiFactory Factory;
        public readonly PrivateData Data = new();
        private string _description;
        private static readonly Vtable s_vt = Build();
        private static int s_luid = 0x5D3D;

        private static Vtable Build()
        {
            var vt = new Vtable("IDXGIAdapter", Vtbl.IDXGIAdapter4.SlotCount,
                Vtbl.IDXGIAdapter.IID, Vtbl.IDXGIAdapter1.IID, Vtbl.IDXGIAdapter2.IID, Vtbl.IDXGIAdapter3.IID, Vtbl.IDXGIAdapter4.IID, Vtbl.IDXGIObject.IID);
            vt.Set(Vtbl.IDXGIObject.SetPrivateData, (delegate* unmanaged<IntPtr, Guid*, uint, void*, int>)&SetPrivateData);
            vt.Set(Vtbl.IDXGIObject.SetPrivateDataInterface, (delegate* unmanaged<IntPtr, Guid*, IntPtr, int>)&SetPrivateDataInterface);
            vt.Set(Vtbl.IDXGIObject.GetPrivateData, (delegate* unmanaged<IntPtr, Guid*, uint*, void*, int>)&GetPrivateData);
            vt.Set(Vtbl.IDXGIObject.GetParent, (delegate* unmanaged<IntPtr, Guid*, IntPtr*, int>)&GetParent);
            vt.Set(Vtbl.IDXGIAdapter.EnumOutputs, (delegate* unmanaged<IntPtr, uint, IntPtr*, int>)&EnumOutputs);
            vt.Set(Vtbl.IDXGIAdapter.GetDesc, (delegate* unmanaged<IntPtr, DXGI_ADAPTER_DESC1*, int>)&GetDesc);
            vt.Set(Vtbl.IDXGIAdapter.CheckInterfaceSupport, (delegate* unmanaged<IntPtr, Guid*, long*, int>)&CheckInterfaceSupport);
            vt.Set(Vtbl.IDXGIAdapter1.GetDesc1, (delegate* unmanaged<IntPtr, DXGI_ADAPTER_DESC1*, int>)&GetDesc1);
            return vt;
        }

        public readonly uint LuidLow;

        public DxgiAdapter(DxgiFactory factory, WgpuContext? gpu) : base(s_vt)
        {
            Factory = factory;
            factory.InternalAddRef();
            _description = Describe(gpu?.AdapterDescription);
            LuidLow = (uint)Interlocked.Increment(ref s_luid);
        }

        private static string Describe(string? adapter)
        {
            // "backend=Vulkan type=DiscreteGPU device='NVIDIA ...' desc=..." -> "NVIDIA ... (WebGPU)"
            if (adapter != null)
            {
                int i = adapter.IndexOf("device='", StringComparison.Ordinal);
                if (i >= 0)
                {
                    int j = adapter.IndexOf('\'', i + 8);
                    if (j > i) return adapter[(i + 8)..j] + " (WebGPU)";
                }
            }
            return "WebGPU adapter";
        }

        protected override void OnFinalRelease()
        {
            Data.ReleaseAll();
            Factory.InternalRelease();
        }

        private static DxgiAdapter? Self(IntPtr p) => From<DxgiAdapter>(p);

        public int QueryInterfaceManaged(Guid iid, IntPtr* ppv) => D3DDevice.QueryInterfaceOf(this, iid, ppv);

        [UnmanagedCallersOnly] private static int SetPrivateData(IntPtr self, Guid* g, uint n, void* d) => Self(self)?.Data.Set(g, n, d) ?? HResult.E_FAIL;
        [UnmanagedCallersOnly] private static int SetPrivateDataInterface(IntPtr self, Guid* g, IntPtr u) => Self(self)?.Data.SetInterface(g, u) ?? HResult.E_FAIL;
        [UnmanagedCallersOnly] private static int GetPrivateData(IntPtr self, Guid* g, uint* n, void* d) => Self(self)?.Data.Get(g, n, d) ?? HResult.E_FAIL;

        [UnmanagedCallersOnly]
        private static int GetParent(IntPtr self, Guid* iid, IntPtr* parent)
        {
            var a = Self(self);
            if (a == null || iid == null || parent == null) return HResult.E_INVALIDARG;
            return D3DDevice.QueryInterfaceOf(a.Factory, *iid, parent);
        }

        [UnmanagedCallersOnly]
        private static int EnumOutputs(IntPtr self, uint index, IntPtr* output)
        {
            if (output != null) *output = IntPtr.Zero;
            return HResult.DXGI_ERROR_NOT_FOUND;
        }

        private void Fill(DXGI_ADAPTER_DESC1* d, bool desc1)
        {
            NativeMemory.Clear(d, desc1 ? (nuint)sizeof(DXGI_ADAPTER_DESC1) : (nuint)((byte*)&d->Flags - (byte*)d));
            int n = Math.Min(_description.Length, 127);
            for (int i = 0; i < n; i++) d->Description[i] = _description[i];
            d->VendorId = 0x1414;           // Microsoft (the WARP/basic render ids), not a hardware vendor
            d->DeviceId = 0x8C;
            d->DedicatedVideoMemory = (nuint)(512UL << 20);
            d->SharedSystemMemory = (nuint)(2048UL << 20);
            d->LuidLow = LuidLow;
            if (desc1) d->Flags = 0;
        }

        [UnmanagedCallersOnly]
        private static int GetDesc(IntPtr self, DXGI_ADAPTER_DESC1* desc)
        {
            var a = Self(self);
            if (a == null || desc == null) return HResult.E_INVALIDARG;
            a.Fill(desc, false);
            return HResult.S_OK;
        }

        [UnmanagedCallersOnly]
        private static int GetDesc1(IntPtr self, DXGI_ADAPTER_DESC1* desc)
        {
            var a = Self(self);
            if (a == null || desc == null) return HResult.E_INVALIDARG;
            a.Fill(desc, true);
            return HResult.S_OK;
        }

        [UnmanagedCallersOnly]
        private static int CheckInterfaceSupport(IntPtr self, Guid* iid, long* umd)
        {
            if (umd != null) *umd = 0x000B_0000_0000_0000;
            return iid != null && (*iid == Vtbl.ID3D11Device.IID || *iid == new Guid("9b7e4c0f-342c-4106-a19f-4f2704f689f0") /*ID3D10Device*/) ? HResult.S_OK : HResult.DXGI_ERROR_UNSUPPORTED;
        }
    }
}
