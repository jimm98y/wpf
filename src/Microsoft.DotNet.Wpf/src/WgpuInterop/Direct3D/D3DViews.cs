// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// Render-target, depth-stencil and shader-resource views over WebGPU texture views.
//

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using static Microsoft.Wpf.Interop.WebGpu.Wgpu;

namespace Microsoft.Wpf.Interop.WebGpu.Direct3D
{
    internal abstract unsafe class D3DView : D3DChild
    {
        public readonly D3DResource Resource;

        protected D3DView(D3DDevice device, D3DResource resource, params Vtable[] vt) : base(device, vt)
        {
            Resource = resource;
            resource.InternalAddRef();
        }

        public static void FillView(Vtable vt)
        {
            FillDeviceChild(vt);
            vt.Set(7, (delegate* unmanaged<IntPtr, IntPtr*, void>)&GetResource);
        }

        [UnmanagedCallersOnly]
        private static void GetResource(IntPtr self, IntPtr* resource)
        {
            if (resource == null) return;
            var v = From<D3DView>(self);
            *resource = v == null ? IntPtr.Zero : v.Resource.AddRefPointer();
        }

        protected override void OnFinalRelease()
        {
            Resource.InternalRelease();
            base.OnFinalRelease();
        }

        internal static IntPtr CreateTextureView(IntPtr texture, WGPUTextureFormat format, WGPUTextureViewDimension dim, uint mip, uint mips, uint layer, uint layers, WGPUTextureAspect aspect = WGPUTextureAspect.All)
        {
            var d = new WGPUTextureViewDescriptor
            {
                format = format, dimension = dim, baseMipLevel = mip, mipLevelCount = mips, baseArrayLayer = layer, arrayLayerCount = layers, aspect = aspect,
            };
            return wgpuTextureCreateView(texture, (IntPtr)(&d));
        }
    }

    internal sealed unsafe class D3DRenderTargetView : D3DView
    {
        public readonly D3D11_VIEW_DESC Desc;
        public readonly D3DTexture2D Texture;
        public readonly uint Mip, Layer;
        public IntPtr View;

        private static readonly Vtable s_vt = Build();

        private static Vtable Build()
        {
            var vt = new Vtable("ID3D11RenderTargetView", Vtbl.ID3D11RenderTargetView1.SlotCount,
                Vtbl.ID3D11RenderTargetView.IID, Vtbl.ID3D11RenderTargetView1.IID, Vtbl.ID3D11View.IID, Vtbl.ID3D11DeviceChild.IID);
            FillView(vt);
            vt.Set(Vtbl.ID3D11RenderTargetView.GetDesc, (delegate* unmanaged<IntPtr, D3D11_VIEW_DESC*, void>)&GetDesc);
            return vt;
        }

        public D3DRenderTargetView(D3DDevice device, D3DTexture2D texture, D3D11_VIEW_DESC? desc) : base(device, texture, s_vt)
        {
            Texture = texture;
            if (texture.IsStaging) throw new ArgumentException("render target over a staging texture");
            var d = desc ?? new D3D11_VIEW_DESC
            {
                Format = texture.Desc.Format,
                ViewDimension = texture.SampleCount > 1 ? (texture.Desc.ArraySize > 1 ? 7u : 6u) : (texture.Desc.ArraySize > 1 ? 5u : 4u),
                U0 = 0, U1 = 0, U2 = texture.Desc.ArraySize,
            };
            if (d.Format == DXGI_FORMAT.UNKNOWN) d.Format = texture.Desc.Format;
            Desc = d;
            switch (d.ViewDimension)
            {
                case 4: Mip = d.U0; break;
                case 5: Mip = d.U0; Layer = d.U1; Layers = Math.Max(1, d.U2); break;
                case 6: break;
                case 7: Layer = d.U0; Layers = Math.Max(1, d.U1); break;
                default: throw new NotSupportedException($"render target view dimension {d.ViewDimension}");
            }
            Layers = Math.Max(1, Math.Min(Layers, texture.Desc.ArraySize - Math.Min(Layer, texture.Desc.ArraySize - 1)));
            var fmt = Formats.Texture(d.Format);
            View = CreateTextureView(texture.Gpu, fmt, WGPUTextureViewDimension._2D, Mip, 1, Layer, 1);
        }

        /// <summary>The slices an array view covers (1 otherwise); a geometry shader's
        /// SV_RenderTargetArrayIndex picks one.</summary>
        public readonly uint Layers = 1;
        private IntPtr[]? _layerViews;

        /// <summary>The view of slice <paramref name="l"/> of the view's range (0: <see cref="View"/>).</summary>
        public IntPtr ViewForLayer(uint l)
        {
            if (l == 0 || l >= Layers) return View;
            _layerViews ??= new IntPtr[Layers];
            if (_layerViews[l] == IntPtr.Zero) _layerViews[l] = CreateTextureView(Texture.Gpu, Formats.Texture(Desc.Format), WGPUTextureViewDimension._2D, Mip, 1, Layer + l, 1);
            return _layerViews[l];
        }

        public WGPUTextureFormat Format => Formats.Texture(Desc.Format);

        [UnmanagedCallersOnly]
        private static void GetDesc(IntPtr self, D3D11_VIEW_DESC* desc)
        {
            var v = From<D3DRenderTargetView>(self);
            if (v != null && desc != null) *desc = v.Desc;
        }

        protected override void OnFinalRelease()
        {
            if (View != IntPtr.Zero) wgpuTextureViewRelease(View);
            View = IntPtr.Zero;
            if (_layerViews != null) foreach (IntPtr v in _layerViews) if (v != IntPtr.Zero) wgpuTextureViewRelease(v);
            _layerViews = null;
            base.OnFinalRelease();
        }
    }

    internal sealed unsafe class D3DDepthStencilView : D3DView
    {
        public readonly D3D11_DEPTH_STENCIL_VIEW_DESC Desc;
        public readonly D3DTexture2D Texture;
        public readonly uint Mip, Layer;
        public IntPtr View;

        private static readonly Vtable s_vt = Build();

        private static Vtable Build()
        {
            var vt = new Vtable("ID3D11DepthStencilView", Vtbl.ID3D11DepthStencilView.SlotCount,
                Vtbl.ID3D11DepthStencilView.IID, Vtbl.ID3D11View.IID, Vtbl.ID3D11DeviceChild.IID);
            FillView(vt);
            vt.Set(Vtbl.ID3D11DepthStencilView.GetDesc, (delegate* unmanaged<IntPtr, D3D11_DEPTH_STENCIL_VIEW_DESC*, void>)&GetDesc);
            return vt;
        }

        public D3DDepthStencilView(D3DDevice device, D3DTexture2D texture, D3D11_DEPTH_STENCIL_VIEW_DESC? desc) : base(device, texture, s_vt)
        {
            Texture = texture;
            var d = desc ?? new D3D11_DEPTH_STENCIL_VIEW_DESC
            {
                Format = texture.Desc.Format,
                ViewDimension = texture.SampleCount > 1 ? (texture.Desc.ArraySize > 1 ? 6u : 5u) : (texture.Desc.ArraySize > 1 ? 4u : 3u),
                U2 = texture.Desc.ArraySize,
            };
            if (d.Format == DXGI_FORMAT.UNKNOWN) d.Format = texture.Desc.Format;
            Desc = d;
            switch (d.ViewDimension)
            {
                case 3: Mip = d.U0; break;
                case 4: Mip = d.U0; Layer = d.U1; Layers = Math.Max(1, d.U2); break;
                case 5: break;
                case 6: Layer = d.U0; Layers = Math.Max(1, d.U1); break;
                default: throw new NotSupportedException($"depth-stencil view dimension {d.ViewDimension}");
            }
            Layers = Math.Max(1, Math.Min(Layers, texture.Desc.ArraySize - Math.Min(Layer, texture.Desc.ArraySize - 1)));
            View = CreateTextureView(texture.Gpu, texture.Format, WGPUTextureViewDimension._2D, Mip, 1, Layer, 1);
        }

        public bool ReadOnlyDepth => (Desc.Flags & 1) != 0;
        public bool ReadOnlyStencil => (Desc.Flags & 2) != 0;

        /// <summary>The slices an array view covers (1 otherwise).</summary>
        public readonly uint Layers = 1;
        private IntPtr[]? _layerViews;

        public IntPtr ViewForLayer(uint l)
        {
            if (l == 0 || l >= Layers) return View;
            _layerViews ??= new IntPtr[Layers];
            if (_layerViews[l] == IntPtr.Zero) _layerViews[l] = CreateTextureView(Texture.Gpu, Texture.Format, WGPUTextureViewDimension._2D, Mip, 1, Layer + l, 1);
            return _layerViews[l];
        }

        [UnmanagedCallersOnly]
        private static void GetDesc(IntPtr self, D3D11_DEPTH_STENCIL_VIEW_DESC* desc)
        {
            var v = From<D3DDepthStencilView>(self);
            if (v != null && desc != null) *desc = v.Desc;
        }

        protected override void OnFinalRelease()
        {
            if (View != IntPtr.Zero) wgpuTextureViewRelease(View);
            View = IntPtr.Zero;
            if (_layerViews != null) foreach (IntPtr v in _layerViews) if (v != IntPtr.Zero) wgpuTextureViewRelease(v);
            _layerViews = null;
            base.OnFinalRelease();
        }
    }

    internal sealed unsafe class D3DShaderResourceView : D3DView
    {
        public readonly D3D11_VIEW_DESC Desc;
        private readonly Dictionary<int, IntPtr> _views = new();   // by translator resource dimension

        private static readonly Vtable s_vt = Build();

        private static Vtable Build()
        {
            var vt = new Vtable("ID3D11ShaderResourceView", Vtbl.ID3D11ShaderResourceView1.SlotCount,
                Vtbl.ID3D11ShaderResourceView.IID, Vtbl.ID3D11ShaderResourceView1.IID, Vtbl.ID3D11View.IID, Vtbl.ID3D11DeviceChild.IID);
            FillView(vt);
            vt.Set(Vtbl.ID3D11ShaderResourceView.GetDesc, (delegate* unmanaged<IntPtr, D3D11_VIEW_DESC*, void>)&GetDesc);
            return vt;
        }

        public D3DShaderResourceView(D3DDevice device, D3DResource resource, D3D11_VIEW_DESC? desc) : base(device, resource, s_vt)
        {
            if (resource is D3DTexture2D t)
            {
                var d = desc ?? new D3D11_VIEW_DESC
                {
                    Format = t.Desc.Format,
                    ViewDimension = t.Kind == 3 ? 8u : t.Kind == 1 ? (t.Desc.ArraySize > 1 ? 3u : 2u)
                        : (t.Desc.MiscFlags & D3D11_RESOURCE_MISC.TEXTURECUBE) != 0 ? (t.Desc.ArraySize > 6 ? 10u : 9u)
                        : t.SampleCount > 1 ? (t.Desc.ArraySize > 1 ? 7u : 6u) : (t.Desc.ArraySize > 1 ? 5u : 4u),
                    U0 = 0, U1 = t.MipLevels, U2 = 0, U3 = t.Desc.ArraySize,
                };
                if (d.ViewDimension == 10 && desc == null) d.U3 = t.Desc.ArraySize / 6;   // a cube array counts cubes
                if (desc != null && d.ViewDimension == 9) d.U3 = 6;
                if (d.Format == DXGI_FORMAT.UNKNOWN) d.Format = t.Desc.Format;
                Desc = d;
            }
            else if (resource is D3DBuffer b)
            {
                // D3D11_SRV_DIMENSION_BUFFER (1) and BUFFEREX (11): U0 first element, U1 element count
                if (desc == null) throw new ArgumentException("a buffer's shader resource view needs a description");
                var d = desc.Value;
                if (d.ViewDimension is not (1 or 11)) throw new NotSupportedException($"buffer view dimension {d.ViewDimension}");
                if ((b.Desc.BindFlags & D3D11_BIND.SHADER_RESOURCE) == 0) throw new ArgumentException("the buffer has no SHADER_RESOURCE binding");
                Desc = d;
            }
            else throw new NotSupportedException($"shader resource view of {resource.GetType().Name}");
        }

        /// <summary>A texture view of the shape a shader declared (dcl_resource dimension) -- normally
        /// the view's own; a 2D view read as a 2D array (or the reverse) gets the view WGSL needs.</summary>
        public IntPtr ViewFor(int shaderDim)
        {
            lock (_views)
            {
                if (_views.TryGetValue(shaderDim, out IntPtr v)) return v;
                var t = (D3DTexture2D)Resource;
                uint mip = Desc.U0, mips = Desc.U1 == uint.MaxValue || Desc.U1 == 0 ? t.MipLevels - mip : Desc.U1;
                uint layer = 0, layers = 1;
                switch (Desc.ViewDimension)
                {
                    case 3: case 5: layer = Desc.U2; layers = Desc.U3; break;   // 1D and 2D arrays
                    case 6: mip = 0; mips = 1; break;
                    case 7: mip = 0; mips = 1; layer = Desc.U0; layers = Desc.U1; break;
                    case 9: layers = 6; break;
                    case 10: layer = Desc.U2 * 6; layers = Desc.U3 * 6; break;
                }
                var dim = shaderDim switch
                {
                    5 => WGPUTextureViewDimension._3D,
                    6 => WGPUTextureViewDimension.Cube,
                    10 => WGPUTextureViewDimension.CubeArray,
                    7 or 8 => WGPUTextureViewDimension._2DArray,
                    _ => WGPUTextureViewDimension._2D,
                };
                if (dim == WGPUTextureViewDimension._3D) { layer = 0; layers = 1; }
                if (dim == WGPUTextureViewDimension._2D) layers = 1;
                var fmt = Formats.Texture(Desc.Format);
                var aspect = Formats.IsDepth(t.Format) ? WGPUTextureAspect.DepthOnly : WGPUTextureAspect.All;
                if (Formats.IsDepth(t.Format)) fmt = t.Format;
                v = CreateTextureView(t.Gpu, fmt, dim, mip, Math.Max(1, mips), layer, Math.Max(1, layers), aspect);
                _views[shaderDim] = v;
                return v;
            }
        }

        [UnmanagedCallersOnly]
        private static void GetDesc(IntPtr self, D3D11_VIEW_DESC* desc)
        {
            var v = From<D3DShaderResourceView>(self);
            if (v != null && desc != null) *desc = v.Desc;
        }

        protected override void OnFinalRelease()
        {
            lock (_views)
            {
                foreach (IntPtr v in _views.Values) wgpuTextureViewRelease(v);
                _views.Clear();
            }
            base.OnFinalRelease();
        }
    }
}
