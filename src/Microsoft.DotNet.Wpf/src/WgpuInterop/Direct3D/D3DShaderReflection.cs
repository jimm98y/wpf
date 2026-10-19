// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// D3DReflect, managed (Documentation/d3d11-webgpu-shim.md, section 9): ID3D11ShaderReflection over the
// DXBC container's own chunks -- RDEF (constant buffers, variables, types, bindings), the signatures,
// STAT (the compiler's instruction counts), SFI0 (required features) and the code's declarations.
// d3dcompiler_47 answers on Windows; this one answers where there is none, and the tests hold it to
// the native one field for field over the corpus (D3DReflectTests).
//
// The constant-buffer, variable and type interfaces are not IUnknowns: their vtables start at
// GetDesc, and they live as long as the reflection that handed them out. A lookup that finds nothing
// returns an object whose GetDesc fails, never null, as d3dcompiler does.
//

using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using Microsoft.Wpf.Interop.WebGpu.Direct3D.Dxbc;

namespace Microsoft.Wpf.Interop.WebGpu.Direct3D
{
    [StructLayout(LayoutKind.Sequential)]
    internal unsafe struct D3D11_SHADER_DESC
    {
        public uint Version;
        public byte* Creator;
        public uint Flags, ConstantBuffers, BoundResources, InputParameters, OutputParameters;
        public uint InstructionCount, TempRegisterCount, TempArrayCount, DefCount, DclCount;
        public uint TextureNormalInstructions, TextureLoadInstructions, TextureCompInstructions, TextureBiasInstructions, TextureGradientInstructions;
        public uint FloatInstructionCount, IntInstructionCount, UintInstructionCount, StaticFlowControlCount, DynamicFlowControlCount;
        public uint MacroInstructionCount, ArrayInstructionCount, CutInstructionCount, EmitInstructionCount;
        public uint GSOutputTopology, GSMaxOutputVertexCount, InputPrimitive, PatchConstantParameters, cGSInstanceCount, cControlPoints;
        public uint HSOutputPrimitive, HSPartitioning, TessellatorDomain, cBarrierInstructions, cInterlockedInstructions, cTextureStoreInstructions;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal unsafe struct D3D11_SIGNATURE_PARAMETER_DESC
    {
        public byte* SemanticName;
        public uint SemanticIndex, Register, SystemValueType, ComponentType;
        public byte Mask, ReadWriteMask;
        public uint Stream, MinPrecision;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal unsafe struct D3D11_SHADER_BUFFER_DESC
    {
        public byte* Name;
        public uint Type, Variables, Size, uFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal unsafe struct D3D11_SHADER_VARIABLE_DESC
    {
        public byte* Name;
        public uint StartOffset, Size, uFlags;
        public void* DefaultValue;
        public uint StartTexture, TextureSize, StartSampler, SamplerSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal unsafe struct D3D11_SHADER_TYPE_DESC
    {
        public uint Class, Type, Rows, Columns, Elements, Members, Offset;
        public byte* Name;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal unsafe struct D3D11_SHADER_INPUT_BIND_DESC
    {
        public byte* Name;
        public uint Type, BindPoint, BindCount, uFlags, ReturnType, Dimension, NumSamples;
    }

    internal sealed unsafe class D3DShaderReflection : ComObject
    {
        /// <summary>ID3D11ShaderReflection of d3dcompiler_47, and of _43 and earlier.</summary>
        public static readonly Guid IID_ID3D11ShaderReflection = new("8d536ca1-0cca-4956-a837-786963755584");
        public static readonly Guid IID_ID3D11ShaderReflection43 = new("0a233719-3960-4578-9d7c-203b8b1d9cc1");

        private static readonly Vtable s_vt = BuildVtable();
        private static readonly IntPtr* s_cbVt = BuildCbVtable(), s_varVt = BuildVarVtable(), s_typeVt = BuildTypeVtable();

        private readonly DxbcShader _s;
        private readonly DxbcReflection _r;
        // the native blocks of the parts ([vtable*][GCHandle of the part]) and the strings and defaults
        // handed out: freed with the reflection
        private readonly List<IntPtr> _native = new();
        private readonly List<GCHandle> _handles = new();
        private readonly Dictionary<string, IntPtr> _strings = new();
        private readonly List<Part> _cbs = new();
        private readonly Part _nullCb, _nullVar, _nullType;
        private readonly Dictionary<(DxbcReflection.Type, int), Part> _types = new(new TypeKey());

        /// <summary>A constant buffer, variable or type: what its native block points back to.</summary>
        private sealed class Part
        {
            public IntPtr Block;
            public D3DShaderReflection Owner = null!;
            public DxbcReflection.ConstantBuffer? Cb;
            public DxbcReflection.Variable? Var;
            public Part? Buffer;                    // a variable's constant buffer
            public List<Part> Vars = new();         // a constant buffer's variables
            public DxbcReflection.Type? Type;
            public int Offset;                      // a member type's offset in its parent
            public IntPtr Default;
        }

        private sealed class TypeKey : IEqualityComparer<(DxbcReflection.Type, int)>
        {
            public bool Equals((DxbcReflection.Type, int) a, (DxbcReflection.Type, int) b) => ReferenceEquals(a.Item1, b.Item1) && a.Item2 == b.Item2;
            public int GetHashCode((DxbcReflection.Type, int) k) => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(k.Item1) ^ k.Item2;
        }

        private D3DShaderReflection(DxbcShader s) : base(s_vt)
        {
            _s = s;
            _r = s.Reflection ?? new DxbcReflection();
            _nullCb = NewPart(s_cbVt, new Part());
            _nullVar = NewPart(s_varVt, new Part { Buffer = _nullCb });
            _nullType = NewPart(s_typeVt, new Part());
            foreach (var cb in _r.ConstantBuffers)
            {
                var p = NewPart(s_cbVt, new Part { Cb = cb });
                foreach (var v in cb.Variables)
                {
                    var vp = NewPart(s_varVt, new Part { Var = v, Buffer = p });
                    if (v.Default != null)
                    {
                        vp.Default = (IntPtr)NativeMemory.Alloc((nuint)Math.Max(1, v.Default.Length));
                        Marshal.Copy(v.Default, 0, vp.Default, v.Default.Length);
                        _native.Add(vp.Default);
                    }
                    p.Vars.Add(vp);
                }
                _cbs.Add(p);
            }
        }

        /// <summary>D3DReflect: the reflection of a DXBC blob as <paramref name="iid"/>.</summary>
        public static int Reflect(void* src, nuint size, Guid* iid, IntPtr* reflector)
        {
            if (reflector == null) return HResult.E_POINTER;
            *reflector = IntPtr.Zero;
            if (src == null || iid == null) return HResult.E_INVALIDARG;
            if (*iid != IID_ID3D11ShaderReflection && *iid != IID_ID3D11ShaderReflection43)
            {
                D3DLog.Once("reflect-iid:" + *iid, $"D3DReflect as {*iid}: only ID3D11ShaderReflection is reflected");
                return HResult.E_NOINTERFACE;
            }
            DxbcShader s;
            try { s = DxbcShader.Parse(new ReadOnlySpan<byte>(src, checked((int)size))); }
            catch (Exception e) when (e is FormatException or ArgumentException or IndexOutOfRangeException or OverflowException)
            {
                D3DLog.Once("reflect-bad", $"D3DReflect of a blob that is not DXBC: {e.Message}");
                return HResult.E_INVALIDARG;
            }
            var r = new D3DShaderReflection(s);
            *reflector = r.Pointer;
            return HResult.S_OK;
        }

        private Part NewPart(IntPtr* vt, Part p)
        {
            p.Owner = this;
            var h = GCHandle.Alloc(p, GCHandleType.Normal);
            _handles.Add(h);
            IntPtr* b = (IntPtr*)NativeMemory.AllocZeroed((nuint)(2 * sizeof(IntPtr)));
            b[0] = (IntPtr)vt;
            b[1] = GCHandle.ToIntPtr(h);
            p.Block = (IntPtr)b;
            _native.Add(p.Block);
            return p;
        }

        private static Part? PartOf(IntPtr self) => self == IntPtr.Zero ? null : GCHandle.FromIntPtr(((IntPtr*)self)[1]).Target as Part;

        private byte* Str(string? s)
        {
            if (s == null) return null;
            if (!_strings.TryGetValue(s, out IntPtr p))
            {
                p = Marshal.StringToHGlobalAnsi(s);
                _strings[s] = p;
            }
            return (byte*)p;
        }

        private Part TypePart(DxbcReflection.Type t, int offset)
        {
            if (!_types.TryGetValue((t, offset), out var p)) _types[(t, offset)] = p = NewPart(s_typeVt, new Part { Type = t, Offset = offset });
            return p;
        }

        protected override void OnFinalRelease()
        {
            foreach (IntPtr p in _native) NativeMemory.Free((void*)p);
            foreach (var h in _handles) if (h.IsAllocated) h.Free();
            foreach (IntPtr p in _strings.Values) Marshal.FreeHGlobal(p);
            _native.Clear(); _handles.Clear(); _strings.Clear();
        }

        // ---- ID3D11ShaderReflection --------------------------------------------------------------------

        private static Vtable BuildVtable()
        {
            var vt = new Vtable("ID3D11ShaderReflection", 22, IID_ID3D11ShaderReflection, IID_ID3D11ShaderReflection43);
            vt.Set(3, (delegate* unmanaged<IntPtr, D3D11_SHADER_DESC*, int>)&GetDesc);
            vt.Set(4, (delegate* unmanaged<IntPtr, uint, IntPtr>)&GetConstantBufferByIndex);
            vt.Set(5, (delegate* unmanaged<IntPtr, byte*, IntPtr>)&GetConstantBufferByName);
            vt.Set(6, (delegate* unmanaged<IntPtr, uint, D3D11_SHADER_INPUT_BIND_DESC*, int>)&GetResourceBindingDesc);
            vt.Set(7, (delegate* unmanaged<IntPtr, uint, D3D11_SIGNATURE_PARAMETER_DESC*, int>)&GetInputParameterDesc);
            vt.Set(8, (delegate* unmanaged<IntPtr, uint, D3D11_SIGNATURE_PARAMETER_DESC*, int>)&GetOutputParameterDesc);
            vt.Set(9, (delegate* unmanaged<IntPtr, uint, D3D11_SIGNATURE_PARAMETER_DESC*, int>)&GetPatchConstantParameterDesc);
            vt.Set(10, (delegate* unmanaged<IntPtr, byte*, IntPtr>)&GetVariableByName);
            vt.Set(11, (delegate* unmanaged<IntPtr, byte*, D3D11_SHADER_INPUT_BIND_DESC*, int>)&GetResourceBindingDescByName);
            vt.Set(12, (delegate* unmanaged<IntPtr, uint>)&GetMovInstructionCount);
            vt.Set(13, (delegate* unmanaged<IntPtr, uint>)&GetMovcInstructionCount);
            vt.Set(14, (delegate* unmanaged<IntPtr, uint>)&GetConversionInstructionCount);
            vt.Set(15, (delegate* unmanaged<IntPtr, uint>)&GetBitwiseInstructionCount);
            vt.Set(16, (delegate* unmanaged<IntPtr, uint>)&GetGSInputPrimitive);
            vt.Set(17, (delegate* unmanaged<IntPtr, int>)&IsSampleFrequencyShader);
            vt.Set(18, (delegate* unmanaged<IntPtr, uint>)&GetNumInterfaceSlots);
            vt.Set(19, (delegate* unmanaged<IntPtr, uint*, int>)&GetMinFeatureLevel);
            vt.Set(20, (delegate* unmanaged<IntPtr, uint*, uint*, uint*, uint>)&GetThreadGroupSize);
            vt.Set(21, (delegate* unmanaged<IntPtr, ulong>)&GetRequiresFlags);
            return vt;
        }

        private static D3DShaderReflection? R(IntPtr self) => From<D3DShaderReflection>(self);

        /// <summary>A STAT word (the compiler's counts; 0 when the chunk was stripped).</summary>
        private uint Stat(int i) => i < _s.Statistics.Length ? _s.Statistics[i] : 0;

        // STAT, as d3dcompiler_47 reads it (measured over the corpus, D3DReflectTests): instructions,
        // temps, defs, dcls, float, int, uint, static flow, dynamic flow, macro, temp arrays, array
        // instructions, cut, emit, texture normal/load/comp/bias/gradient, mov, movc, conversion,
        // bitwise, input primitive, GS output topology, GS max vertices, 2 gather counts, sample
        // frequency, GS instances, then from 5.0: control points, HS output primitive, partitioning,
        // domain, barriers, interlocked, texture stores
        [UnmanagedCallersOnly]
        private static int GetDesc(IntPtr self, D3D11_SHADER_DESC* d)
        {
            var r = R(self);
            if (r == null || d == null) return HResult.E_INVALIDARG;
            var s = r._s;
            *d = default;
            d->Version = ((uint)s.Stage << 16) | ((uint)s.Major << 4) | (uint)s.Minor;
            d->Creator = s.Reflection == null ? null : r.Str(r._r.Creator);
            d->Flags = (uint)r._r.Flags;
            d->ConstantBuffers = (uint)r._r.ConstantBuffers.Count;
            d->BoundResources = (uint)r._r.Bindings.Count;
            d->InputParameters = (uint)s.Inputs.Count;
            d->OutputParameters = (uint)s.Outputs.Count;
            d->PatchConstantParameters = (uint)s.PatchConstants.Count;
            d->InstructionCount = r.Stat(0); d->TempRegisterCount = r.Stat(1); d->DefCount = r.Stat(2); d->DclCount = r.Stat(3);
            d->FloatInstructionCount = r.Stat(4); d->IntInstructionCount = r.Stat(5); d->UintInstructionCount = r.Stat(6);
            d->StaticFlowControlCount = r.Stat(7); d->DynamicFlowControlCount = r.Stat(8); d->MacroInstructionCount = r.Stat(9);
            d->TempArrayCount = r.Stat(10); d->ArrayInstructionCount = r.Stat(11); d->CutInstructionCount = r.Stat(12); d->EmitInstructionCount = r.Stat(13);
            d->TextureNormalInstructions = r.Stat(14); d->TextureLoadInstructions = r.Stat(15); d->TextureCompInstructions = r.Stat(16);
            d->TextureBiasInstructions = r.Stat(17); d->TextureGradientInstructions = r.Stat(18);
            d->InputPrimitive = r.Stat(23); d->GSOutputTopology = r.Stat(24); d->GSMaxOutputVertexCount = r.Stat(25);
            d->cGSInstanceCount = r.Stat(29);
            d->cControlPoints = r.Stat(30); d->HSOutputPrimitive = r.Stat(31); d->HSPartitioning = r.Stat(32); d->TessellatorDomain = r.Stat(33);
            d->cBarrierInstructions = r.Stat(34); d->cInterlockedInstructions = r.Stat(35); d->cTextureStoreInstructions = r.Stat(36);
            return HResult.S_OK;
        }

        [UnmanagedCallersOnly] private static uint GetMovInstructionCount(IntPtr self) => R(self)?.Stat(19) ?? 0;
        [UnmanagedCallersOnly] private static uint GetMovcInstructionCount(IntPtr self) => R(self)?.Stat(20) ?? 0;
        [UnmanagedCallersOnly] private static uint GetConversionInstructionCount(IntPtr self) => R(self)?.Stat(21) ?? 0;
        [UnmanagedCallersOnly] private static uint GetBitwiseInstructionCount(IntPtr self) => R(self)?.Stat(22) ?? 0;
        [UnmanagedCallersOnly] private static uint GetGSInputPrimitive(IntPtr self) => R(self)?.Stat(23) ?? 0;
        [UnmanagedCallersOnly] private static int IsSampleFrequencyShader(IntPtr self) => R(self)?.Stat(28) != 0 ? 1 : 0;
        [UnmanagedCallersOnly] private static ulong GetRequiresFlags(IntPtr self) => R(self)?._s.RequiredFeatures ?? 0;

        [UnmanagedCallersOnly]
        private static uint GetNumInterfaceSlots(IntPtr self)
        {
            var r = R(self);
            // IFCE: the interface slot count is its third word (class instances, class types, slots)
            return r == null || r._s.Interfaces.Length < 12 ? 0 : BitConverter.ToUInt32(r._s.Interfaces, 8);
        }

        [UnmanagedCallersOnly]
        private static int GetMinFeatureLevel(IntPtr self, uint* level)
        {
            var r = R(self);
            if (r == null || level == null) return HResult.E_INVALIDARG;
            *level = r.MinFeatureLevel();
            return HResult.S_OK;
        }

        private uint MinFeatureLevel()
        {
            if (_s.Major >= 5) return 0xb000;
            return _s.Minor >= 1 ? 0xa100u : 0xa000u;
        }

        [UnmanagedCallersOnly]
        private static uint GetThreadGroupSize(IntPtr self, uint* x, uint* y, uint* z)
        {
            var r = R(self);
            uint a = 0, b = 0, c = 0;
            var ins = r?._s.Instructions.FirstOrDefault(i => i.Op == Opcode.DclThreadGroup);
            if (ins != null && ins.Extra.Length >= 3) { a = ins.Extra[0]; b = ins.Extra[1]; c = ins.Extra[2]; }
            if (x != null) *x = a;
            if (y != null) *y = b;
            if (z != null) *z = c;
            return a * b * c;
        }

        [UnmanagedCallersOnly]
        private static IntPtr GetConstantBufferByIndex(IntPtr self, uint index)
        {
            var r = R(self);
            if (r == null) return IntPtr.Zero;
            return index < (uint)r._cbs.Count ? r._cbs[(int)index].Block : r._nullCb.Block;
        }

        [UnmanagedCallersOnly]
        private static IntPtr GetConstantBufferByName(IntPtr self, byte* name)
        {
            var r = R(self);
            if (r == null) return IntPtr.Zero;
            string? n = name == null ? null : Marshal.PtrToStringAnsi((IntPtr)name);
            return r._cbs.FirstOrDefault(c => c.Cb!.Name == n)?.Block ?? r._nullCb.Block;
        }

        [UnmanagedCallersOnly]
        private static IntPtr GetVariableByName(IntPtr self, byte* name)
        {
            var r = R(self);
            if (r == null) return IntPtr.Zero;
            string? n = name == null ? null : Marshal.PtrToStringAnsi((IntPtr)name);
            foreach (var cb in r._cbs)
                foreach (var v in cb.Vars)
                    if (v.Var!.Name == n) return v.Block;
            return r._nullVar.Block;
        }

        private int BindingDesc(DxbcReflection.Binding b, D3D11_SHADER_INPUT_BIND_DESC* d)
        {
            d->Name = Str(b.Name);
            d->Type = (uint)b.Kind; d->BindPoint = (uint)b.Register; d->BindCount = (uint)b.Count; d->uFlags = (uint)b.Flags;
            d->ReturnType = (uint)b.ReturnType; d->Dimension = (uint)b.Dimension; d->NumSamples = (uint)b.Samples;
            return HResult.S_OK;
        }

        [UnmanagedCallersOnly]
        private static int GetResourceBindingDesc(IntPtr self, uint index, D3D11_SHADER_INPUT_BIND_DESC* d)
        {
            var r = R(self);
            if (r == null || d == null || index >= (uint)r._r.Bindings.Count) return HResult.E_INVALIDARG;
            return r.BindingDesc(r._r.Bindings[(int)index], d);
        }

        [UnmanagedCallersOnly]
        private static int GetResourceBindingDescByName(IntPtr self, byte* name, D3D11_SHADER_INPUT_BIND_DESC* d)
        {
            var r = R(self);
            if (r == null || d == null || name == null) return HResult.E_INVALIDARG;
            string n = Marshal.PtrToStringAnsi((IntPtr)name) ?? "";
            var b = r._r.Bindings.FirstOrDefault(x => x.Name == n);
            return b == null ? HResult.E_INVALIDARG : r.BindingDesc(b, d);
        }

        private int Parameter(List<SignatureElement> list, uint index, D3D11_SIGNATURE_PARAMETER_DESC* d)
        {
            if (d == null || index >= (uint)list.Count) return HResult.E_INVALIDARG;
            var e = list[(int)index];
            d->SemanticName = Str(e.SemanticName);
            d->SemanticIndex = (uint)e.SemanticIndex; d->Register = (uint)e.Register; d->SystemValueType = (uint)SystemValue(list, e);
            d->ComponentType = (uint)e.ComponentType; d->Mask = (byte)e.Mask; d->ReadWriteMask = (byte)e.ReadWriteMask;
            d->Stream = (uint)e.Stream; d->MinPrecision = (uint)e.MinPrecision;
            return HResult.S_OK;
        }

        /// <summary>A pixel shader's outputs carry no system value in the signature: d3dcompiler names
        /// them from the semantic (D3D_NAME_TARGET 64, DEPTH 65, COVERAGE 66, ...).</summary>
        private int SystemValue(List<SignatureElement> list, SignatureElement e)
        {
            if (e.SystemValue != 0 || _s.Stage != ShaderStage.Pixel || !ReferenceEquals(list, _s.Outputs)) return e.SystemValue;
            return e.SemanticName.ToUpperInvariant() switch
            {
                "SV_TARGET" => 64, "SV_DEPTH" => 65, "SV_COVERAGE" => 66, "SV_DEPTHGREATEREQUAL" => 67,
                "SV_DEPTHLESSEQUAL" => 68, "SV_STENCILREF" => 69, "SV_INNERCOVERAGE" => 70, _ => 0,
            };
        }

        [UnmanagedCallersOnly]
        private static int GetInputParameterDesc(IntPtr self, uint index, D3D11_SIGNATURE_PARAMETER_DESC* d)
            => R(self) is { } r ? r.Parameter(r._s.Inputs, index, d) : HResult.E_INVALIDARG;

        [UnmanagedCallersOnly]
        private static int GetOutputParameterDesc(IntPtr self, uint index, D3D11_SIGNATURE_PARAMETER_DESC* d)
            => R(self) is { } r ? r.Parameter(r._s.Outputs, index, d) : HResult.E_INVALIDARG;

        [UnmanagedCallersOnly]
        private static int GetPatchConstantParameterDesc(IntPtr self, uint index, D3D11_SIGNATURE_PARAMETER_DESC* d)
            => R(self) is { } r ? r.Parameter(r._s.PatchConstants, index, d) : HResult.E_INVALIDARG;

        // ---- ID3D11ShaderReflectionConstantBuffer ----------------------------------------------------

        private static IntPtr* BuildCbVtable()
        {
            var t = (IntPtr*)NativeMemory.AllocZeroed((nuint)(3 * sizeof(IntPtr)));
            t[0] = (IntPtr)(delegate* unmanaged<IntPtr, D3D11_SHADER_BUFFER_DESC*, int>)&CbGetDesc;
            t[1] = (IntPtr)(delegate* unmanaged<IntPtr, uint, IntPtr>)&CbGetVariableByIndex;
            t[2] = (IntPtr)(delegate* unmanaged<IntPtr, byte*, IntPtr>)&CbGetVariableByName;
            return t;
        }

        [UnmanagedCallersOnly]
        private static int CbGetDesc(IntPtr self, D3D11_SHADER_BUFFER_DESC* d)
        {
            var p = PartOf(self);
            if (p?.Cb == null || d == null) return HResult.E_FAIL;
            d->Name = p.Owner.Str(p.Cb.Name);
            d->Type = (uint)p.Cb.Kind; d->Variables = (uint)p.Cb.Variables.Count; d->Size = (uint)p.Cb.Size; d->uFlags = (uint)p.Cb.Flags;
            return HResult.S_OK;
        }

        [UnmanagedCallersOnly]
        private static IntPtr CbGetVariableByIndex(IntPtr self, uint index)
        {
            var p = PartOf(self);
            if (p == null) return IntPtr.Zero;
            return index < (uint)p.Vars.Count ? p.Vars[(int)index].Block : p.Owner._nullVar.Block;
        }

        [UnmanagedCallersOnly]
        private static IntPtr CbGetVariableByName(IntPtr self, byte* name)
        {
            var p = PartOf(self);
            if (p == null) return IntPtr.Zero;
            string? n = name == null ? null : Marshal.PtrToStringAnsi((IntPtr)name);
            return p.Vars.FirstOrDefault(v => v.Var!.Name == n)?.Block ?? p.Owner._nullVar.Block;
        }

        // ---- ID3D11ShaderReflectionVariable ----------------------------------------------------------

        private static IntPtr* BuildVarVtable()
        {
            var t = (IntPtr*)NativeMemory.AllocZeroed((nuint)(4 * sizeof(IntPtr)));
            t[0] = (IntPtr)(delegate* unmanaged<IntPtr, D3D11_SHADER_VARIABLE_DESC*, int>)&VarGetDesc;
            t[1] = (IntPtr)(delegate* unmanaged<IntPtr, IntPtr>)&VarGetType;
            t[2] = (IntPtr)(delegate* unmanaged<IntPtr, IntPtr>)&VarGetBuffer;
            t[3] = (IntPtr)(delegate* unmanaged<IntPtr, uint, uint>)&VarGetInterfaceSlot;
            return t;
        }

        [UnmanagedCallersOnly]
        private static int VarGetDesc(IntPtr self, D3D11_SHADER_VARIABLE_DESC* d)
        {
            var p = PartOf(self);
            if (p?.Var == null || d == null) return HResult.E_FAIL;
            var v = p.Var;
            d->Name = p.Owner.Str(v.Name);
            d->StartOffset = (uint)v.Offset; d->Size = (uint)v.Size; d->uFlags = (uint)v.Flags;
            d->DefaultValue = (void*)p.Default;
            d->StartTexture = (uint)v.StartTexture; d->TextureSize = (uint)v.TextureSize;
            d->StartSampler = (uint)v.StartSampler; d->SamplerSize = (uint)v.SamplerSize;
            return HResult.S_OK;
        }

        [UnmanagedCallersOnly]
        private static IntPtr VarGetType(IntPtr self)
        {
            var p = PartOf(self);
            if (p == null) return IntPtr.Zero;
            return p.Var == null ? p.Owner._nullType.Block : p.Owner.TypePart(p.Var.Type, 0).Block;
        }

        [UnmanagedCallersOnly]
        private static IntPtr VarGetBuffer(IntPtr self) => PartOf(self)?.Buffer?.Block ?? IntPtr.Zero;

        [UnmanagedCallersOnly]
        private static uint VarGetInterfaceSlot(IntPtr self, uint index) => 0xFFFFFFFF;

        // ---- ID3D11ShaderReflectionType --------------------------------------------------------------

        private static IntPtr* BuildTypeVtable()
        {
            var t = (IntPtr*)NativeMemory.AllocZeroed((nuint)(11 * sizeof(IntPtr)));
            t[0] = (IntPtr)(delegate* unmanaged<IntPtr, D3D11_SHADER_TYPE_DESC*, int>)&TypeGetDesc;
            t[1] = (IntPtr)(delegate* unmanaged<IntPtr, uint, IntPtr>)&TypeGetMemberTypeByIndex;
            t[2] = (IntPtr)(delegate* unmanaged<IntPtr, byte*, IntPtr>)&TypeGetMemberTypeByName;
            t[3] = (IntPtr)(delegate* unmanaged<IntPtr, uint, byte*>)&TypeGetMemberTypeName;
            t[4] = (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, int>)&TypeIsEqual;
            t[5] = (IntPtr)(delegate* unmanaged<IntPtr, IntPtr>)&TypeGetSubType;
            t[6] = (IntPtr)(delegate* unmanaged<IntPtr, IntPtr>)&TypeGetBaseClass;
            t[7] = (IntPtr)(delegate* unmanaged<IntPtr, uint>)&TypeGetNumInterfaces;
            t[8] = (IntPtr)(delegate* unmanaged<IntPtr, uint, IntPtr>)&TypeGetInterfaceByIndex;
            t[9] = (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, int>)&TypeIsOfType;
            t[10] = (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, int>)&TypeImplementsInterface;
            return t;
        }

        [UnmanagedCallersOnly]
        private static int TypeGetDesc(IntPtr self, D3D11_SHADER_TYPE_DESC* d)
        {
            var p = PartOf(self);
            if (p?.Type == null || d == null) return HResult.E_FAIL;
            var t = p.Type;
            d->Class = (uint)t.Class; d->Type = (uint)t.Kind; d->Rows = (uint)t.Rows; d->Columns = (uint)t.Columns;
            d->Elements = (uint)t.Elements; d->Members = (uint)t.Members.Count; d->Offset = (uint)p.Offset;
            d->Name = t.Name.Length == 0 ? null : p.Owner.Str(t.Name);
            return HResult.S_OK;
        }

        [UnmanagedCallersOnly]
        private static IntPtr TypeGetMemberTypeByIndex(IntPtr self, uint index)
        {
            var p = PartOf(self);
            if (p == null) return IntPtr.Zero;
            if (p.Type == null || index >= (uint)p.Type.Members.Count) return p.Owner._nullType.Block;
            var m = p.Type.Members[(int)index];
            return p.Owner.TypePart(m.Type, m.Offset).Block;
        }

        [UnmanagedCallersOnly]
        private static IntPtr TypeGetMemberTypeByName(IntPtr self, byte* name)
        {
            var p = PartOf(self);
            if (p == null) return IntPtr.Zero;
            string? n = name == null ? null : Marshal.PtrToStringAnsi((IntPtr)name);
            if (p.Type != null)
                foreach (var m in p.Type.Members)
                    if (m.Name == n) return p.Owner.TypePart(m.Type, m.Offset).Block;
            return p.Owner._nullType.Block;
        }

        [UnmanagedCallersOnly]
        private static byte* TypeGetMemberTypeName(IntPtr self, uint index)
        {
            var p = PartOf(self);
            if (p?.Type == null || index >= (uint)p.Type.Members.Count) return null;
            return p.Owner.Str(p.Type.Members[(int)index].Name);
        }

        [UnmanagedCallersOnly]
        private static int TypeIsEqual(IntPtr self, IntPtr other)
        {
            var a = PartOf(self); var b = PartOf(other);
            if (a?.Type == null || b?.Type == null) return HResult.E_INVALIDARG;
            return ReferenceEquals(a.Type, b.Type) ? HResult.S_OK : HResult.S_FALSE;
        }

        [UnmanagedCallersOnly]
        private static IntPtr TypeGetSubType(IntPtr self) => PartOf(self) is { Type.SubType: { } t } p ? p.Owner.TypePart(t, 0).Block : IntPtr.Zero;

        [UnmanagedCallersOnly]
        private static IntPtr TypeGetBaseClass(IntPtr self) => PartOf(self) is { Type.BaseClass: { } t } p ? p.Owner.TypePart(t, 0).Block : IntPtr.Zero;

        [UnmanagedCallersOnly]
        private static uint TypeGetNumInterfaces(IntPtr self) => (uint)(PartOf(self)?.Type?.Interfaces.Count ?? 0);

        [UnmanagedCallersOnly]
        private static IntPtr TypeGetInterfaceByIndex(IntPtr self, uint index)
        {
            var p = PartOf(self);
            if (p?.Type == null || index >= (uint)p.Type.Interfaces.Count) return IntPtr.Zero;
            return p.Owner.TypePart(p.Type.Interfaces[(int)index], 0).Block;
        }

        [UnmanagedCallersOnly]
        private static int TypeIsOfType(IntPtr self, IntPtr other)
        {
            var a = PartOf(self); var b = PartOf(other);
            if (a?.Type == null || b?.Type == null) return HResult.E_INVALIDARG;
            return ReferenceEquals(a.Type, b.Type) ? HResult.S_OK : HResult.S_FALSE;
        }

        [UnmanagedCallersOnly]
        private static int TypeImplementsInterface(IntPtr self, IntPtr other) => HResult.S_FALSE;
    }
}
