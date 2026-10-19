// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// The managed D3DReflect (D3DShaderReflection) against d3dcompiler_47's, over every shader of the
// corpus (and DXBC_CORPUS): both reflections are walked through their vtables -- the shader's
// description, every constant buffer, variable (with its default value) and type tree, every
// binding, signature parameter, lookup by name and by a name or index that is not there, and the
// instruction counts -- into lines, and the lines must be the same.
//

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using Microsoft.Wpf.Interop.WebGpu.Direct3D;
using Xunit;

namespace WgpuInterop.Tests.Direct3D
{
    public sealed unsafe class D3DReflectTests
    {
        [DllImport("d3dcompiler_47.dll", EntryPoint = "D3DReflect")]
        private static extern int NativeReflect(void* src, nuint size, Guid* iid, IntPtr* reflector);

        private static IntPtr Fn(IntPtr o, int slot) => (*(IntPtr**)o)[slot];
        private static string S(byte* p) => p == null ? "(null)" : Marshal.PtrToStringAnsi((IntPtr)p) ?? "";

        [Fact]
        public void ManagedReflection_IsTheNativeOne()
        {
            Assert.SkipUnless(OperatingSystem.IsWindows(), "d3dcompiler_47 exists on Windows only");
            var failures = new List<string>();
            int shaders = 0;
            foreach (string f in DxbcCoverageTests.Shaders())
            {
                byte[] blob = File.ReadAllBytes(f);
                var native = Walk(blob, managed: false);
                var managed = Walk(blob, managed: true);
                shaders++;
                for (int i = 0; i < Math.Max(native.Count, managed.Count); i++)
                {
                    string a = i < native.Count ? native[i] : "(end)", b = i < managed.Count ? managed[i] : "(end)";
                    if (a != b) { failures.Add($"{Path.GetFileName(f)}: line {i}: native '{a}', managed '{b}'"); break; }
                }
            }
            Assert.True(failures.Count == 0, $"{failures.Count} of {shaders} shaders differ:\n" + string.Join("\n", failures.Take(30)));
        }

        /// <summary>A blob that is not DXBC, and an interface that is not ID3D11ShaderReflection.</summary>
        [Fact]
        public void ManagedReflection_RefusesWhatTheNativeOneRefuses()
        {
            byte[] junk = new byte[64];
            Guid iid = D3DShaderReflection.IID_ID3D11ShaderReflection, other = Guid.NewGuid();
            IntPtr r = (IntPtr)1;
            fixed (byte* b = junk)
            {
                Assert.True(Exports.ReflectPointer(b, (nuint)junk.Length, &iid, &r) < 0);
                Assert.Equal(IntPtr.Zero, r);
            }
            byte[] blob = File.ReadAllBytes(DxbcCoverageTests.Shaders().First());
            fixed (byte* b = blob)
            {
                Assert.Equal(HResult.E_NOINTERFACE, Exports.ReflectPointer(b, (nuint)blob.Length, &other, &r));
                Assert.True(Exports.ReflectPointer(b, (nuint)blob.Length, &iid, &r) >= 0);
            }
            Guid unk = new("00000000-0000-0000-C000-000000000046");
            IntPtr q;
            Assert.Equal(0, ((delegate* unmanaged<IntPtr, Guid*, IntPtr*, int>)Fn(r, 0))(r, &unk, &q));
            ((delegate* unmanaged<IntPtr, uint>)Fn(q, 2))(q);
            Assert.Equal(0u, ((delegate* unmanaged<IntPtr, uint>)Fn(r, 2))(r));
        }

        private static List<string> Walk(byte[] blob, bool managed)
        {
            var lines = new List<string>();
            Guid iid = D3DShaderReflection.IID_ID3D11ShaderReflection;
            IntPtr r0;
            int hr;
            fixed (byte* b = blob) hr = managed ? Exports.ReflectPointer(b, (nuint)blob.Length, &iid, &r0) : NativeReflect(b, (nuint)blob.Length, &iid, &r0);
            lines.Add($"D3DReflect 0x{hr:x8}");
            if (hr < 0) return lines;
            IntPtr r = r0;
            try
            {
                D3D11_SHADER_DESC d;
                hr = ((delegate* unmanaged<IntPtr, D3D11_SHADER_DESC*, int>)Fn(r, 3))(r, &d);
                lines.Add($"desc 0x{hr:x8} v{d.Version:x} '{S(d.Creator)}' flags {d.Flags} cb {d.ConstantBuffers} res {d.BoundResources} in {d.InputParameters} out {d.OutputParameters} pc {d.PatchConstantParameters}");
                lines.Add($"  counts {d.InstructionCount} {d.TempRegisterCount} {d.TempArrayCount} {d.DefCount} {d.DclCount} tex {d.TextureNormalInstructions} {d.TextureLoadInstructions} {d.TextureCompInstructions} {d.TextureBiasInstructions} {d.TextureGradientInstructions}"
                    + $" alu {d.FloatInstructionCount} {d.IntInstructionCount} {d.UintInstructionCount} flow {d.StaticFlowControlCount} {d.DynamicFlowControlCount} {d.MacroInstructionCount} {d.ArrayInstructionCount} gs {d.CutInstructionCount} {d.EmitInstructionCount}"
                    + $" {d.GSOutputTopology} {d.GSMaxOutputVertexCount} {d.InputPrimitive} {d.cGSInstanceCount} hs {d.cControlPoints} {d.HSOutputPrimitive} {d.HSPartitioning} {d.TessellatorDomain} cs {d.cBarrierInstructions} {d.cInterlockedInstructions} {d.cTextureStoreInstructions}");
                uint U(int slot) => ((delegate* unmanaged<IntPtr, uint>)Fn(r, slot))(r);
                uint fl;
                int flhr = ((delegate* unmanaged<IntPtr, uint*, int>)Fn(r, 19))(r, &fl);
                uint x, y, z;
                uint total = ((delegate* unmanaged<IntPtr, uint*, uint*, uint*, uint>)Fn(r, 20))(r, &x, &y, &z);
                lines.Add($"  mov {U(12)} movc {U(13)} conv {U(14)} bit {U(15)} gsin {U(16)} sf {((delegate* unmanaged<IntPtr, int>)Fn(r, 17))(r)} slots {U(18)}"
                    + $" fl 0x{flhr:x8} {fl:x} group {total} {x} {y} {z} req 0x{((delegate* unmanaged<IntPtr, ulong>)Fn(r, 21))(r):x}");

                for (uint i = 0; i <= d.ConstantBuffers; i++)          // one past the end: the null buffer
                {
                    IntPtr cb = ((delegate* unmanaged<IntPtr, uint, IntPtr>)Fn(r, 4))(r, i);
                    D3D11_SHADER_BUFFER_DESC bd;
                    hr = ((delegate* unmanaged<IntPtr, D3D11_SHADER_BUFFER_DESC*, int>)Fn(cb, 0))(cb, &bd);
                    if (hr < 0) { lines.Add($"cb {i}: 0x{hr:x8}"); continue; }
                    string name = S(bd.Name);
                    lines.Add($"cb {i} '{name}' type {bd.Type} vars {bd.Variables} size {bd.Size} flags {bd.uFlags}");
                    fixed (byte* n = System.Text.Encoding.ASCII.GetBytes(name + "\0"))
                        lines.Add($"  by name is by index: {((delegate* unmanaged<IntPtr, byte*, IntPtr>)Fn(r, 5))(r, n) == cb}");
                    for (uint v = 0; v <= bd.Variables; v++)
                    {
                        IntPtr va = ((delegate* unmanaged<IntPtr, uint, IntPtr>)Fn(cb, 1))(cb, v);
                        D3D11_SHADER_VARIABLE_DESC vd;
                        hr = ((delegate* unmanaged<IntPtr, D3D11_SHADER_VARIABLE_DESC*, int>)Fn(va, 0))(va, &vd);
                        if (hr < 0) { lines.Add($"  var {v}: 0x{hr:x8}"); continue; }
                        string def = vd.DefaultValue == null ? "none" : Convert.ToHexString(new ReadOnlySpan<byte>(vd.DefaultValue, (int)vd.Size));
                        lines.Add($"  var {v} '{S(vd.Name)}' at {vd.StartOffset} size {vd.Size} flags {vd.uFlags} tex {(int)vd.StartTexture} {vd.TextureSize} smp {(int)vd.StartSampler} {vd.SamplerSize} default {def}");
                        lines.Add($"    buffer is its own: {((delegate* unmanaged<IntPtr, IntPtr>)Fn(va, 2))(va) == cb}");
                        fixed (byte* n = System.Text.Encoding.ASCII.GetBytes(S(vd.Name) + "\0"))
                        {
                            lines.Add($"    by name in buffer: {((delegate* unmanaged<IntPtr, byte*, IntPtr>)Fn(cb, 2))(cb, n) == va}");
                            IntPtr g = ((delegate* unmanaged<IntPtr, byte*, IntPtr>)Fn(r, 10))(r, n);
                            D3D11_SHADER_VARIABLE_DESC gd;
                            int ghr = ((delegate* unmanaged<IntPtr, D3D11_SHADER_VARIABLE_DESC*, int>)Fn(g, 0))(g, &gd);
                            lines.Add($"    by name in shader: 0x{ghr:x8} {(ghr >= 0 ? S(gd.Name) + " " + gd.StartOffset : "")}");
                        }
                        WalkType(((delegate* unmanaged<IntPtr, IntPtr>)Fn(va, 1))(va), lines, "    ", 0);
                    }
                }
                fixed (byte* n = "no such thing\0"u8)
                {
                    IntPtr cb = ((delegate* unmanaged<IntPtr, byte*, IntPtr>)Fn(r, 5))(r, n);
                    D3D11_SHADER_BUFFER_DESC bd;
                    lines.Add($"cb by a name not there: 0x{((delegate* unmanaged<IntPtr, D3D11_SHADER_BUFFER_DESC*, int>)Fn(cb, 0))(cb, &bd):x8}");
                    IntPtr va = ((delegate* unmanaged<IntPtr, byte*, IntPtr>)Fn(r, 10))(r, n);
                    D3D11_SHADER_VARIABLE_DESC vd;
                    lines.Add($"var by a name not there: 0x{((delegate* unmanaged<IntPtr, D3D11_SHADER_VARIABLE_DESC*, int>)Fn(va, 0))(va, &vd):x8}");
                    D3D11_SHADER_INPUT_BIND_DESC bind;
                    lines.Add($"binding by a name not there: 0x{((delegate* unmanaged<IntPtr, byte*, D3D11_SHADER_INPUT_BIND_DESC*, int>)Fn(r, 11))(r, n, &bind):x8}");
                }

                for (uint i = 0; i <= d.BoundResources; i++)
                {
                    D3D11_SHADER_INPUT_BIND_DESC bd;
                    hr = ((delegate* unmanaged<IntPtr, uint, D3D11_SHADER_INPUT_BIND_DESC*, int>)Fn(r, 6))(r, i, &bd);
                    if (hr < 0) { lines.Add($"binding {i}: 0x{hr:x8}"); continue; }
                    lines.Add($"binding {i} '{S(bd.Name)}' type {bd.Type} at {bd.BindPoint} x{bd.BindCount} flags {bd.uFlags} ret {bd.ReturnType} dim {bd.Dimension} samples {(int)bd.NumSamples}");
                    D3D11_SHADER_INPUT_BIND_DESC bn;
                    fixed (byte* n = System.Text.Encoding.ASCII.GetBytes(S(bd.Name) + "\0"))
                        hr = ((delegate* unmanaged<IntPtr, byte*, D3D11_SHADER_INPUT_BIND_DESC*, int>)Fn(r, 11))(r, n, &bn);
                    lines.Add($"  by name: 0x{hr:x8} at {bn.BindPoint} type {bn.Type}");
                }

                foreach (var (slot, what, count) in new[] { (7, "in", d.InputParameters), (8, "out", d.OutputParameters), (9, "pc", d.PatchConstantParameters) })
                    for (uint i = 0; i <= count; i++)
                    {
                        D3D11_SIGNATURE_PARAMETER_DESC p;
                        hr = ((delegate* unmanaged<IntPtr, uint, D3D11_SIGNATURE_PARAMETER_DESC*, int>)Fn(r, slot))(r, i, &p);
                        lines.Add(hr < 0 ? $"{what} {i}: 0x{hr:x8}"
                            : $"{what} {i} '{S(p.SemanticName)}'{p.SemanticIndex} r{p.Register} sv {p.SystemValueType} ct {p.ComponentType} mask {p.Mask} rw {p.ReadWriteMask} stream {p.Stream} mp {p.MinPrecision}");
                    }
            }
            finally { ((delegate* unmanaged<IntPtr, uint>)Fn(r, 2))(r); }
            return lines;
        }

        private static void WalkType(IntPtr t, List<string> lines, string indent, int depth)
        {
            D3D11_SHADER_TYPE_DESC td;
            int hr = ((delegate* unmanaged<IntPtr, D3D11_SHADER_TYPE_DESC*, int>)Fn(t, 0))(t, &td);
            if (hr < 0) { lines.Add($"{indent}type: 0x{hr:x8}"); return; }
            lines.Add($"{indent}type '{S(td.Name)}' class {td.Class} type {td.Type} {td.Rows}x{td.Columns} [{td.Elements}] members {td.Members} at {td.Offset}"
                + $" interfaces {((delegate* unmanaged<IntPtr, uint>)Fn(t, 7))(t)} sub {((delegate* unmanaged<IntPtr, IntPtr>)Fn(t, 5))(t) != IntPtr.Zero} base {((delegate* unmanaged<IntPtr, IntPtr>)Fn(t, 6))(t) != IntPtr.Zero}"
                + $" equal {((delegate* unmanaged<IntPtr, IntPtr, int>)Fn(t, 4))(t, t)}");
            if (depth > 8) return;
            IntPtr sub = ((delegate* unmanaged<IntPtr, IntPtr>)Fn(t, 5))(t), bas = ((delegate* unmanaged<IntPtr, IntPtr>)Fn(t, 6))(t);
            if (sub != IntPtr.Zero) { lines.Add($"{indent}  subtype"); WalkType(sub, lines, indent + "  ", depth + 1); }
            if (bas != IntPtr.Zero) { lines.Add($"{indent}  base class"); WalkType(bas, lines, indent + "  ", depth + 1); }
            uint ifaces = ((delegate* unmanaged<IntPtr, uint>)Fn(t, 7))(t);
            for (uint k = 0; k < ifaces; k++) { lines.Add($"{indent}  interface {k}"); WalkType(((delegate* unmanaged<IntPtr, uint, IntPtr>)Fn(t, 8))(t, k), lines, indent + "  ", depth + 1); }
            for (uint m = 0; m <= td.Members; m++)
            {
                string name = S(((delegate* unmanaged<IntPtr, uint, byte*>)Fn(t, 3))(t, m));
                IntPtr mt = ((delegate* unmanaged<IntPtr, uint, IntPtr>)Fn(t, 1))(t, m);
                lines.Add($"{indent}  member {m} '{name}'");
                if (m < td.Members)
                    fixed (byte* n = System.Text.Encoding.ASCII.GetBytes(name + "\0"))
                    {
                        IntPtr byName = ((delegate* unmanaged<IntPtr, byte*, IntPtr>)Fn(t, 2))(t, n);
                        D3D11_SHADER_TYPE_DESC a, b;
                        ((delegate* unmanaged<IntPtr, D3D11_SHADER_TYPE_DESC*, int>)Fn(byName, 0))(byName, &a);
                        ((delegate* unmanaged<IntPtr, D3D11_SHADER_TYPE_DESC*, int>)Fn(mt, 0))(mt, &b);
                        lines.Add($"{indent}  by name: at {a.Offset} (by index {b.Offset})");
                    }
                WalkType(mt, lines, indent + "  ", depth + 1);
            }
        }
    }
}
