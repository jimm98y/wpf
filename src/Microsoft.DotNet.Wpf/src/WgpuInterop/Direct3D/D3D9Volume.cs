// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// IDirect3DVolumeTexture9 and IDirect3DVolume9: a 3D texture and its levels. Each level keeps its
// texels in memory -- a volume is never drawn to in D3D9, so what the application last wrote is what
// the texture holds -- and a level unlocked after a write goes to the GPU texture (a managed or default
// pool texture); one in a CPU pool (SYSTEMMEM, SCRATCH) has only the memory, and UpdateTexture copies
// it into a GPU texture.
//

using System;
using System.Runtime.InteropServices;
using static Microsoft.Wpf.Interop.WebGpu.Wgpu;

namespace Microsoft.Wpf.Interop.WebGpu.Direct3D
{
    [StructLayout(LayoutKind.Sequential)]
    internal struct D3DVOLUME_DESC { public int Format, Type; public uint Usage; public int Pool; public uint Width, Height, Depth; }

    [StructLayout(LayoutKind.Sequential)]
    internal unsafe struct D3DLOCKED_BOX { public int RowPitch, SlicePitch; public void* pBits; }

    [StructLayout(LayoutKind.Sequential)]
    internal struct D3DBOX { public uint Left, Top, Right, Bottom, Front, Back; }

    internal sealed unsafe class Direct3DVolumeTexture9 : ComObject
    {
        public const int D3DRTYPE_VOLUME = 2, D3DRTYPE_VOLUMETEXTURE = 4;
        private static readonly Vtable s_vt = Build();
        public readonly Direct3DDevice9 Device;
        /// <summary>The 3D GPU texture (null in a CPU pool).</summary>
        public readonly D3DTexture2D? Gpu;
        public readonly int Format, Pool;
        public readonly uint Usage, Width, Height, Depth, Levels;
        private readonly Direct3DVolume9[] _levels;
        public readonly PrivateData Data = new();
        private uint _priority, _lod;

        public Direct3DVolumeTexture9(Direct3DDevice9 device, uint w, uint h, uint d, uint levels, int format, int pool, uint usage) : base(s_vt)
        {
            Device = device; device.InternalAddRef();
            Format = format; Pool = pool; Usage = usage; Width = w; Height = h; Depth = d;
            uint max = 1 + (uint)Math.Floor(Math.Log2(Math.Max(w, Math.Max(h, d))));
            Levels = levels == 0 ? max : Math.Min(levels, max);
            DXGI_FORMAT f = D3D9Const.ToDxgi(format);
            if (f == 0) throw new NotSupportedException($"D3D9 format {format}");
            if (pool is not (D3D9Const.D3DPOOL_SYSTEMMEM or D3D9Const.D3DPOOL_SCRATCH))
                Gpu = D3DTexture2D.Create3D(device.Device11, new D3D11_TEXTURE3D_DESC
                {
                    Width = w, Height = h, Depth = d, MipLevels = Levels, Format = f, Usage = D3D11_USAGE.DEFAULT, BindFlags = D3D11_BIND.SHADER_RESOURCE,
                }, null);
            uint bpp = Formats.TexelBytes(f);
            _levels = new Direct3DVolume9[Levels];
            for (int i = 0; i < Levels; i++)
            {
                var v = new Direct3DVolume9(this, (uint)i, Math.Max(1, w >> i), Math.Max(1, h >> i), Math.Max(1, d >> i), bpp);
                v.MakeInternal();
                _levels[i] = v;
            }
        }

        public Direct3DVolume9 Level(int i) => _levels[i];

        protected override void OnFinalRelease()
        {
            foreach (var v in _levels) v.ReleaseInternal();
            Gpu?.Release();
            Data.ReleaseAll();
            Device.InternalRelease();
        }

        private static Direct3DVolumeTexture9? Self(IntPtr p) => From<Direct3DVolumeTexture9>(p);

        private static Vtable Build()
        {
            var vt = new Vtable("IDirect3DVolumeTexture9", Vtbl.IDirect3DVolumeTexture9.SlotCount, Vtbl.IDirect3DVolumeTexture9.IID, Vtbl.IDirect3DBaseTexture9.IID, Vtbl.IDirect3DResource9.IID);
            vt.Set(Vtbl.IDirect3DVolumeTexture9.GetDevice, (delegate* unmanaged<IntPtr, IntPtr*, int>)&GetDevice);
            vt.Set(Vtbl.IDirect3DVolumeTexture9.SetPrivateData, (delegate* unmanaged<IntPtr, Guid*, void*, uint, uint, int>)&SetPrivateData);
            vt.Set(Vtbl.IDirect3DVolumeTexture9.GetPrivateData, (delegate* unmanaged<IntPtr, Guid*, void*, uint*, int>)&GetPrivateData);
            vt.Set(Vtbl.IDirect3DVolumeTexture9.FreePrivateData, (delegate* unmanaged<IntPtr, Guid*, int>)&FreePrivateData);
            vt.Set(Vtbl.IDirect3DVolumeTexture9.SetPriority, (delegate* unmanaged<IntPtr, uint, uint>)&SetPriority);
            vt.Set(Vtbl.IDirect3DVolumeTexture9.GetPriority, (delegate* unmanaged<IntPtr, uint>)&GetPriority);
            vt.Set(Vtbl.IDirect3DVolumeTexture9.PreLoad, (delegate* unmanaged<IntPtr, void>)&PreLoad);
            vt.Set(Vtbl.IDirect3DVolumeTexture9.GetType, (delegate* unmanaged<IntPtr, int>)&GetType);
            vt.Set(Vtbl.IDirect3DVolumeTexture9.SetLOD, (delegate* unmanaged<IntPtr, uint, uint>)&SetLOD);
            vt.Set(Vtbl.IDirect3DVolumeTexture9.GetLOD, (delegate* unmanaged<IntPtr, uint>)&GetLOD);
            vt.Set(Vtbl.IDirect3DVolumeTexture9.GetLevelCount, (delegate* unmanaged<IntPtr, uint>)&GetLevelCount);
            vt.Set(Vtbl.IDirect3DVolumeTexture9.SetAutoGenFilterType, (delegate* unmanaged<IntPtr, int, int>)&SetAutoGenFilterType);
            vt.Set(Vtbl.IDirect3DVolumeTexture9.GetAutoGenFilterType, (delegate* unmanaged<IntPtr, int>)&GetAutoGenFilterType);
            vt.Set(Vtbl.IDirect3DVolumeTexture9.GenerateMipSubLevels, (delegate* unmanaged<IntPtr, void>)&PreLoad);
            vt.Set(Vtbl.IDirect3DVolumeTexture9.GetLevelDesc, (delegate* unmanaged<IntPtr, uint, D3DVOLUME_DESC*, int>)&GetLevelDesc);
            vt.Set(Vtbl.IDirect3DVolumeTexture9.GetVolumeLevel, (delegate* unmanaged<IntPtr, uint, IntPtr*, int>)&GetVolumeLevel);
            vt.Set(Vtbl.IDirect3DVolumeTexture9.LockBox, (delegate* unmanaged<IntPtr, uint, D3DLOCKED_BOX*, D3DBOX*, uint, int>)&LockBox);
            vt.Set(Vtbl.IDirect3DVolumeTexture9.UnlockBox, (delegate* unmanaged<IntPtr, uint, int>)&UnlockBox);
            vt.Set(Vtbl.IDirect3DVolumeTexture9.AddDirtyBox, (delegate* unmanaged<IntPtr, D3DBOX*, int>)&AddDirtyBox);
            return vt;
        }

        [UnmanagedCallersOnly]
        private static int GetDevice(IntPtr self, IntPtr* device)
        {
            var t = Self(self);
            if (t == null || device == null) return D3D9Const.D3DERR_INVALIDCALL;
            *device = t.Device.AddRefPointer();
            return HResult.S_OK;
        }

        [UnmanagedCallersOnly] private static int SetPrivateData(IntPtr self, Guid* g, void* d, uint n, uint flags) => Self(self)?.Data.Set(g, n, d) ?? D3D9Const.D3DERR_INVALIDCALL;
        [UnmanagedCallersOnly] private static int GetPrivateData(IntPtr self, Guid* g, void* d, uint* n) => Self(self)?.Data.Get(g, n, d) ?? D3D9Const.D3DERR_INVALIDCALL;
        [UnmanagedCallersOnly] private static int FreePrivateData(IntPtr self, Guid* g) => Self(self)?.Data.Set(g, 0, null) ?? D3D9Const.D3DERR_INVALIDCALL;
        [UnmanagedCallersOnly] private static uint SetPriority(IntPtr self, uint p) { var t = Self(self); if (t == null) return 0; uint o = t._priority; t._priority = p; return o; }
        [UnmanagedCallersOnly] private static uint GetPriority(IntPtr self) => Self(self)?._priority ?? 0;
        [UnmanagedCallersOnly] private static void PreLoad(IntPtr self) { }
        [UnmanagedCallersOnly] private static int GetType(IntPtr self) => D3DRTYPE_VOLUMETEXTURE;
        [UnmanagedCallersOnly] private static uint SetLOD(IntPtr self, uint lod) { var t = Self(self); if (t == null) return 0; uint o = t._lod; t._lod = lod; return o; }
        [UnmanagedCallersOnly] private static uint GetLOD(IntPtr self) => Self(self)?._lod ?? 0;
        [UnmanagedCallersOnly] private static uint GetLevelCount(IntPtr self) => Self(self)?.Levels ?? 0;
        [UnmanagedCallersOnly] private static int SetAutoGenFilterType(IntPtr self, int f) => HResult.S_OK;
        [UnmanagedCallersOnly] private static int GetAutoGenFilterType(IntPtr self) => 2;   // D3DTEXF_LINEAR

        [UnmanagedCallersOnly]
        private static int GetLevelDesc(IntPtr self, uint level, D3DVOLUME_DESC* desc)
        {
            var t = Self(self);
            if (t == null || desc == null || level >= t.Levels) return D3D9Const.D3DERR_INVALIDCALL;
            *desc = t._levels[level].Desc;
            return HResult.S_OK;
        }

        [UnmanagedCallersOnly]
        private static int GetVolumeLevel(IntPtr self, uint level, IntPtr* volume)
        {
            var t = Self(self);
            if (t == null || volume == null || level >= t.Levels) return D3D9Const.D3DERR_INVALIDCALL;
            *volume = t._levels[level].AddRefPointer();
            return HResult.S_OK;
        }

        [UnmanagedCallersOnly]
        private static int LockBox(IntPtr self, uint level, D3DLOCKED_BOX* locked, D3DBOX* box, uint flags)
        {
            var t = Self(self);
            if (t == null || level >= t.Levels) return D3D9Const.D3DERR_INVALIDCALL;
            return t._levels[level].Lock(locked, box, flags);
        }

        [UnmanagedCallersOnly]
        private static int UnlockBox(IntPtr self, uint level)
        {
            var t = Self(self);
            if (t == null || level >= t.Levels) return D3D9Const.D3DERR_INVALIDCALL;
            return t._levels[level].Unlock();
        }

        [UnmanagedCallersOnly] private static int AddDirtyBox(IntPtr self, D3DBOX* box) => HResult.S_OK;

        /// <summary>UpdateTexture: a CPU-pool volume texture's levels into this GPU one.</summary>
        public int UpdateFrom(Direct3DVolumeTexture9 src)
        {
            if (Gpu == null || src.Gpu != null || src.Width != Width || src.Height != Height || src.Depth != Depth) return D3D9Const.D3DERR_INVALIDCALL;
            int n = (int)Math.Min(src.Levels, Levels);
            for (int i = 0; i < n; i++) _levels[i].CopyFrom(src._levels[i]);
            return HResult.S_OK;
        }
    }

    internal sealed unsafe class Direct3DVolume9 : ComObject
    {
        private static readonly Vtable s_vt = Build();
        public readonly Direct3DVolumeTexture9 Container;
        public readonly uint Level, Width, Height, Depth, Bpp;
        public readonly D3DVOLUME_DESC Desc;
        public readonly PrivateData Data = new();
        private readonly byte* _cpu;
        private readonly int _rowPitch, _slicePitch;
        private bool _locked, _lockReadOnly;
        private D3DBOX _lockBox;

        public Direct3DVolume9(Direct3DVolumeTexture9 container, uint level, uint w, uint h, uint d, uint bpp) : base(s_vt)
        {
            Container = container;
            Level = level; Width = w; Height = h; Depth = d; Bpp = bpp;
            Desc = new D3DVOLUME_DESC { Format = container.Format, Type = Direct3DVolumeTexture9.D3DRTYPE_VOLUME, Usage = container.Usage, Pool = container.Pool, Width = w, Height = h, Depth = d };
            _rowPitch = (int)((w * bpp + 3) & ~3u);
            _slicePitch = _rowPitch * (int)h;
            _cpu = (byte*)NativeMemory.AllocZeroed((nuint)((long)_slicePitch * d));
        }

        /// <summary>Owned by its texture; while the application holds the level, the level holds the texture.</summary>
        public void MakeInternal() { InternalAddRef(); Release(); }
        public void ReleaseInternal() => InternalRelease();
        private bool _holdsContainer;

        protected override void OnReferenced()
        {
            if (!_holdsContainer) { Container.AddRef(); _holdsContainer = true; }
        }

        protected override bool OnNoReferences()
        {
            if (_holdsContainer) { _holdsContainer = false; Container.Release(); }
            return false;
        }

        protected override void OnFinalRelease()
        {
            Data.ReleaseAll();
            NativeMemory.Free(_cpu);
        }

        private D3DBOX Whole => new() { Right = Width, Bottom = Height, Back = Depth };

        public int Lock(D3DLOCKED_BOX* locked, D3DBOX* box, uint flags)
        {
            if (locked == null) return D3D9Const.D3DERR_INVALIDCALL;
            lock (this)
            {
                if (_locked) return D3D9Const.D3DERR_INVALIDCALL;
                D3DBOX b = box != null ? *box : Whole;
                if (b.Left >= b.Right || b.Top >= b.Bottom || b.Front >= b.Back || b.Right > Width || b.Bottom > Height || b.Back > Depth) return D3D9Const.D3DERR_INVALIDCALL;
                _locked = true;
                _lockReadOnly = (flags & D3D9Const.D3DLOCK_READONLY) != 0;
                _lockBox = b;
                locked->RowPitch = _rowPitch;
                locked->SlicePitch = _slicePitch;
                locked->pBits = _cpu + (long)b.Front * _slicePitch + (long)b.Top * _rowPitch + b.Left * Bpp;
                return HResult.S_OK;
            }
        }

        public int Unlock()
        {
            lock (this)
            {
                if (!_locked) return D3D9Const.D3DERR_INVALIDCALL;
                _locked = false;
                if (!_lockReadOnly) WriteGpu(_lockBox);
                return HResult.S_OK;
            }
        }

        /// <summary>The box of the level's memory into the GPU texture, after the draws recorded before it.</summary>
        private void WriteGpu(D3DBOX b)
        {
            D3DTexture2D? gpu = Container.Gpu;
            if (gpu == null) return;
            Container.Device.FlushDraws();
            uint w = b.Right - b.Left, h = b.Bottom - b.Top, d = b.Back - b.Front;
            byte* first = _cpu + (long)b.Front * _slicePitch + (long)b.Top * _rowPitch + b.Left * Bpp;
            D3DTexture2D.Upload(gpu.Device.Gpu, gpu.Gpu, Level, new WGPUOrigin3D { x = b.Left, y = b.Top, z = b.Front }, first, (uint)_rowPitch, h, w * Bpp, (uint)_slicePitch,
                                new WGPUExtent3D { width = w, height = h, depthOrArrayLayers = d });
            System.Threading.Interlocked.Increment(ref gpu.Version);
        }

        public void CopyFrom(Direct3DVolume9 src)
        {
            lock (this)
            {
                if (src.Bpp != Bpp || src.Width != Width || src.Height != Height || src.Depth != Depth) return;
                Buffer.MemoryCopy(src._cpu, _cpu, (long)_slicePitch * Depth, (long)_slicePitch * Depth);
                WriteGpu(Whole);
            }
        }

        private static Direct3DVolume9? Self(IntPtr p) => From<Direct3DVolume9>(p);

        private static Vtable Build()
        {
            var vt = new Vtable("IDirect3DVolume9", Vtbl.IDirect3DVolume9.SlotCount, Vtbl.IDirect3DVolume9.IID);
            vt.Set(Vtbl.IDirect3DVolume9.GetDevice, (delegate* unmanaged<IntPtr, IntPtr*, int>)&GetDevice);
            vt.Set(Vtbl.IDirect3DVolume9.SetPrivateData, (delegate* unmanaged<IntPtr, Guid*, void*, uint, uint, int>)&SetPrivateData);
            vt.Set(Vtbl.IDirect3DVolume9.GetPrivateData, (delegate* unmanaged<IntPtr, Guid*, void*, uint*, int>)&GetPrivateData);
            vt.Set(Vtbl.IDirect3DVolume9.FreePrivateData, (delegate* unmanaged<IntPtr, Guid*, int>)&FreePrivateData);
            vt.Set(Vtbl.IDirect3DVolume9.GetContainer, (delegate* unmanaged<IntPtr, Guid*, IntPtr*, int>)&GetContainer);
            vt.Set(Vtbl.IDirect3DVolume9.GetDesc, (delegate* unmanaged<IntPtr, D3DVOLUME_DESC*, int>)&GetDesc);
            vt.Set(Vtbl.IDirect3DVolume9.LockBox, (delegate* unmanaged<IntPtr, D3DLOCKED_BOX*, D3DBOX*, uint, int>)&LockBoxV);
            vt.Set(Vtbl.IDirect3DVolume9.UnlockBox, (delegate* unmanaged<IntPtr, int>)&UnlockBoxV);
            return vt;
        }

        [UnmanagedCallersOnly]
        private static int GetDevice(IntPtr self, IntPtr* device)
        {
            var v = Self(self);
            if (v == null || device == null) return D3D9Const.D3DERR_INVALIDCALL;
            *device = v.Container.Device.AddRefPointer();
            return HResult.S_OK;
        }

        [UnmanagedCallersOnly] private static int SetPrivateData(IntPtr self, Guid* g, void* d, uint n, uint flags) => Self(self)?.Data.Set(g, n, d) ?? D3D9Const.D3DERR_INVALIDCALL;
        [UnmanagedCallersOnly] private static int GetPrivateData(IntPtr self, Guid* g, void* d, uint* n) => Self(self)?.Data.Get(g, n, d) ?? D3D9Const.D3DERR_INVALIDCALL;
        [UnmanagedCallersOnly] private static int FreePrivateData(IntPtr self, Guid* g) => Self(self)?.Data.Set(g, 0, null) ?? D3D9Const.D3DERR_INVALIDCALL;

        [UnmanagedCallersOnly]
        private static int GetContainer(IntPtr self, Guid* iid, IntPtr* container)
        {
            var v = Self(self);
            if (v == null || iid == null || container == null) return D3D9Const.D3DERR_INVALIDCALL;
            return D3DDevice.QueryInterfaceOf(v.Container, *iid, container);
        }

        [UnmanagedCallersOnly]
        private static int GetDesc(IntPtr self, D3DVOLUME_DESC* desc)
        {
            var v = Self(self);
            if (v == null || desc == null) return D3D9Const.D3DERR_INVALIDCALL;
            *desc = v.Desc;
            return HResult.S_OK;
        }

        [UnmanagedCallersOnly] private static int LockBoxV(IntPtr self, D3DLOCKED_BOX* locked, D3DBOX* box, uint flags) => Self(self)?.Lock(locked, box, flags) ?? D3D9Const.D3DERR_INVALIDCALL;
        [UnmanagedCallersOnly] private static int UnlockBoxV(IntPtr self) => Self(self)?.Unlock() ?? D3D9Const.D3DERR_INVALIDCALL;
    }
}
