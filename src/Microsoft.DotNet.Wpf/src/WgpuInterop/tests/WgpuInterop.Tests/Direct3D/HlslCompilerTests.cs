// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// The shim's HLSL compiler against fxc: every vertex and pixel shader of the DXBC corpus
// (Corpus/manifest.txt) compiled through D3DCompile (the export, the application's path), its
// container checked as the runtime checks it, then drawn on the shim next to fxc's blob of the same
// source -- the same inputs (DxbcPixelParity: reflection-filled buffers and textures), the pixels
// compared. fxc's code and ours differ instruction by instruction; what they draw must not.
//

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Wpf.Interop.WebGpu.Direct3D;
using Microsoft.Wpf.Interop.WebGpu.Direct3D.Dxbc;
using Microsoft.Wpf.Interop.WebGpu.Direct3D.Hlsl;
using WgpuInterop.Tests.Harness;
using Xunit;

namespace WgpuInterop.Tests.Direct3D
{
    public sealed unsafe class HlslCompilerTests
    {
        private static string Here([System.Runtime.CompilerServices.CallerFilePath] string here = "") => Path.Combine(Path.GetDirectoryName(here)!, "Corpus");

        internal sealed record Entry(string File, string Name, string Profile, string[] Defines)
        {
            public string Blob => $"{Path.GetFileNameWithoutExtension(File)}.{Name}.{Profile}.dxbc";
            public override string ToString() => Blob;
        }

        /// <summary>The manifest's entries, each profile its own (with build.ps1's defines).</summary>
        internal static IEnumerable<Entry> Entries()
        {
            foreach (string line in File.ReadAllLines(Path.Combine(Here(), "manifest.txt")))
            {
                if (line.TrimStart().StartsWith('#') || line.Trim().Length == 0) continue;
                var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                foreach (string spec in parts.Skip(2))
                {
                    var pieces = spec.Split(':');
                    string prof = pieces[0];
                    var defines = new List<string>();
                    if (prof.EndsWith("_4_1", StringComparison.Ordinal)) defines.Add("SM41");
                    if (prof.Contains("_5_", StringComparison.Ordinal)) defines.Add("SM5");
                    if (pieces.Length > 1) defines.Add(pieces[1]);
                    yield return new Entry(parts[0], parts[1], prof, defines.ToArray());
                }
            }
        }

        /// <summary>D3DCompile through the export the application reaches; the blob, or null and the
        /// error text.</summary>
        internal static byte[]? Compile(string source, string file, string entry, string profile, string[] defines, out string errors)
        {
            errors = "";
            var src = Encoding.UTF8.GetBytes(source);
            var strings = new List<IntPtr>();
            IntPtr Str(string s) { var p = Marshal.StringToCoTaskMemUTF8(s); strings.Add(p); return p; }
            var macros = new IntPtr[defines.Length * 2 + 2];
            for (int i = 0; i < defines.Length; i++) { macros[2 * i] = Str(defines[i]); macros[2 * i + 1] = Str("1"); }
            try
            {
                IntPtr code = IntPtr.Zero, err = IntPtr.Zero;
                int hr;
                fixed (byte* s = src)
                fixed (IntPtr* m = macros)
                    hr = Exports.CompilePointer(s, (nuint)src.Length, (byte*)Str(file), m, null, (byte*)Str(entry), (byte*)Str(profile), 0, 0, &code, &err);
                if (err != IntPtr.Zero)
                {
                    errors = Marshal.PtrToStringUTF8(((delegate* unmanaged<IntPtr, IntPtr>)DxbcPixelParity.Fn(err, 3))(err)) ?? "";
                    DxbcPixelParity.Release(err);
                }
                if (hr < 0 || code == IntPtr.Zero) { if (errors.Length == 0) errors = $"0x{hr:x8}"; return null; }
                var ptr = ((delegate* unmanaged<IntPtr, IntPtr>)DxbcPixelParity.Fn(code, 3))(code);
                var size = ((delegate* unmanaged<IntPtr, nuint>)DxbcPixelParity.Fn(code, 4))(code);
                var blob = new byte[(int)size];
                Marshal.Copy(ptr, blob, 0, blob.Length);
                DxbcPixelParity.Release(code);
                return blob;
            }
            finally { foreach (var p in strings) Marshal.FreeCoTaskMem(p); }
        }

        private static string Source(Entry e) => File.ReadAllText(Path.Combine(Here(), "hlsl", e.File));
        private static byte[] Fxc(Entry e) => File.ReadAllBytes(Path.Combine(Here(), "dxbc", e.Blob));

        /// <summary>What the compiler does not do yet, with why (the tests expect these to fail).</summary>
        private static readonly Dictionary<string, string> Gaps = new()
        {
            ["stages.ps_interface.ps_5_0.dxbc"] = "class linkage (interfaces and classes): not compiled yet; the translator does not run it either",
        };

        private static bool Drawable(Entry e) => e.Profile.StartsWith("ps_", StringComparison.Ordinal) || e.Profile.StartsWith("vs_", StringComparison.Ordinal);
        private static bool Compiled(Entry e) => Drawable(e) || e.Profile.StartsWith("cs_", StringComparison.Ordinal) || e.Profile.StartsWith("gs_", StringComparison.Ordinal)
            || e.Profile.StartsWith("hs_", StringComparison.Ordinal) || e.Profile.StartsWith("ds_", StringComparison.Ordinal);

        /// <summary>Compute shaders whose UAVs depend on the order threads run in (DxbcCoverageTests):
        /// run, not compared.</summary>
        private static readonly HashSet<string> OrderDependent = new(StringComparer.OrdinalIgnoreCase)
        {
            "compute.cs_atomics.cs_5_0.dxbc", "compute.cs_counter_down.cs_5_0.dxbc", "compute.cs_uav.cs_5_0.dxbc",
        };

        [Fact]
        public void TheChecksum_IsTheOneFxcWrites()
        {
            foreach (string f in Directory.GetFiles(Path.Combine(Here(), "dxbc"), "*.dxbc"))
            {
                byte[] b = File.ReadAllBytes(f);
                Assert.True(b.AsSpan(4, 16).SequenceEqual(DxbcChecksum.Compute(b)), Path.GetFileName(f));
            }
        }

        [Fact]
        public void EveryVertexAndPixelShaderOfTheCorpus_Compiles()
        {
            var failures = new List<string>();
            int ok = 0;
            foreach (var e in Entries().Where(Compiled))
            {
                if (Gaps.ContainsKey(e.Blob)) continue;
                var blob = Compile(Source(e), e.File, e.Name, e.Profile, e.Defines, out string errors);
                if (blob == null) { failures.Add($"{e}: {errors.Trim()}"); continue; }
                try
                {
                    Assert.True(blob.AsSpan(4, 16).SequenceEqual(DxbcChecksum.Compute(blob)), "checksum");
                    var s = DxbcShader.Parse(blob);
                    var fxc = DxbcShader.Parse(Fxc(e));
                    Assert.Equal(fxc.Stage, s.Stage);
                    if (Environment.GetEnvironmentVariable("HLSL_SIGS") != null) Console.Error.WriteLine($"{e}\n  fxc in  {Sig(fxc.Inputs)}\n  ours in {Sig(s.Inputs)}\n  fxc out  {Sig(fxc.Outputs)}\n  ours out {Sig(s.Outputs)}");
                    // the signatures are the interface between stages: the same as fxc's
                    Assert.Equal(Sig(fxc.Inputs), Sig(s.Inputs));
                    Assert.Equal(Sig(fxc.Outputs), Sig(s.Outputs));
                    // translated as fxc's is (a shader this device cannot run, e.g. without the
                    // primitive-index feature, fails for both alike)
                    string? Translates(DxbcShader x)
                    {
                        try { DxbcTranslator.Translate(x, x.Stage == ShaderStage.Vertex ? 0 : 1, DxbcTranslator.LinkageOf(x.Stage == ShaderStage.Pixel ? x : null)); return null; }
                        catch (Exception t) { return t.Message; }
                    }
                    Assert.Equal(Translates(fxc), Translates(s));
                    var refl = DxbcReflection.Read(DxbcPixelParity.Chunk(blob, "RDEF")!);
                    var fxcRefl = DxbcReflection.Read(DxbcPixelParity.Chunk(Fxc(e), "RDEF")!);
                    Assert.Equal(Bindings(fxcRefl), Bindings(refl));
                    Assert.Equal(Layout(fxcRefl), Layout(refl));
                    ok++;
                }
                catch (Exception x) { failures.Add($"{e}: {x.Message.Replace(Environment.NewLine, " | ")}"); }
            }
            Assert.True(ok > 0 && failures.Count == 0, $"{ok} compiled as fxc's do, {failures.Count} did not:{Environment.NewLine}{string.Join(Environment.NewLine, failures)}");
        }

        private static string Sig(IEnumerable<SignatureElement> els) =>
            string.Join("; ", els.OrderBy(x => x.Register).ThenBy(x => x.SemanticName).Select(x => $"{x.SemanticName.ToUpperInvariant()}{x.SemanticIndex} r{x.Register} m{x.Mask:x} t{x.ComponentType} sv{x.SystemValue}"));

        private static string Bindings(DxbcReflection r) =>
            string.Join("; ", r.Bindings.OrderBy(b => b.Kind).ThenBy(b => b.Register).Select(b => $"{b.Name} k{b.Kind} r{b.Register} n{b.Count} d{b.Dimension} rt{b.ReturnType}"));

        private static string Layout(DxbcReflection r) =>
            string.Join("; ", r.ConstantBuffers.OrderBy(c => c.Name).Select(c => $"{c.Name}[{c.Size}]: " + string.Join(", ", c.Variables.Select(v => $"{v.Name}@{v.Offset}+{v.Size}"))));

        /// <summary>Each pixel shader drawn with fxc's blob and with ours, each vertex shader the same.</summary>
        [Fact]
        public void EveryVertexAndPixelShaderOfTheCorpus_DrawsAsFxcsDraws()
        {
            Assert.SkipWhen(Requirements.GpuUnavailable is not null, Requirements.GpuUnavailable ?? "");
            IntPtr dev, ctx;
            uint level;
            Assert.True(Exports.CreateDevicePointer(IntPtr.Zero, 1, IntPtr.Zero, 0, null, 0, 7, &dev, &level, &ctx) >= 0, "shim");
            var lines = new List<string>();
            int same = 0, differ = 0;
            try
            {
                string? only = Environment.GetEnvironmentVariable("HLSL_ONLY");
                foreach (var e in Entries().Where(Drawable))
                {
                    if (Gaps.ContainsKey(e.Blob)) continue;
                    if (only != null && !e.Blob.Contains(only, StringComparison.OrdinalIgnoreCase)) continue;
                    if (Environment.GetEnvironmentVariable("HLSL_SKIP") is { Length: > 0 } skip && skip.Split(',').Any(x => e.Blob.Contains(x, StringComparison.OrdinalIgnoreCase))) continue;
                    Console.Error.WriteLine("hlsl: " + e);
                    byte[] fxcBlob = Fxc(e);
                    var ours = Compile(Source(e), e.File, e.Name, e.Profile, e.Defines, out string errors);
                    if (ours == null) { differ++; lines.Add($"FAIL {e}: {errors.Trim()}"); continue; }
                    if (Environment.GetEnvironmentVariable("HLSL_DUMP") is { Length: > 0 } dump)
                    {
                        Directory.CreateDirectory(dump);
                        File.WriteAllLines(Path.Combine(dump, e.Blob + ".ours.txt"), DxbcShader.Parse(ours).Instructions.Select(i => i.ToString()));
                        File.WriteAllLines(Path.Combine(dump, e.Blob + ".fxc.txt"), DxbcShader.Parse(fxcBlob).Instructions.Select(i => i.ToString()));
                    }
                    try
                    {
                        var fxc = DxbcShader.Parse(fxcBlob);
                        List<float[]> a, b;
                        if (fxc.Stage == ShaderStage.Pixel)
                        {
                            string vsHlsl = DxbcPixelParity.VertexShaderFor(fxc) ?? throw new InvalidOperationException("no vertex shader for its inputs");
                            var vs = Compile(vsHlsl, "gen.hlsl", "main", fxc.Major >= 5 ? "vs_5_0" : "vs_4_0", Array.Empty<string>(), out string verr) ?? throw new InvalidOperationException("vertex shader: " + verr);
                            a = DxbcPixelParity.Draw(dev, ctx, vs, fxcBlob, fxc, DxbcPixelParity.Reflect(fxcBlob), 1);
                            b = DxbcPixelParity.Draw(dev, ctx, vs, ours, DxbcShader.Parse(ours), DxbcPixelParity.Reflect(ours), 1);
                        }
                        else
                        {
                            string psHlsl = DxbcPixelParity.PixelShaderFor(fxc, out int targets);
                            if (targets == 0) { same++; lines.Add($"OK   {e} (no outputs but the position)"); continue; }
                            var ps = Compile(psHlsl, "gen.hlsl", "main", fxc.Major >= 5 ? "ps_5_0" : "ps_4_0", Array.Empty<string>(), out string perr) ?? throw new InvalidOperationException("pixel shader: " + perr);
                            var psShader = DxbcShader.Parse(ps);
                            var psRefl = DxbcPixelParity.Reflect(ps);
                            a = DxbcPixelParity.Draw(dev, ctx, fxcBlob, ps, psShader, psRefl, DxbcPixelParity.Reflect(fxcBlob), fxc, 1);
                            b = DxbcPixelParity.Draw(dev, ctx, ours, ps, psShader, psRefl, DxbcPixelParity.Reflect(ours), DxbcShader.Parse(ours), 1);
                        }
                        var (count, first) = DxbcPixelParity.Compare(a, b);
                        if (count == 0 && a.Count == b.Count) { same++; lines.Add($"OK   {e}"); }
                        else { differ++; lines.Add($"FAIL {e}: {count} components differ; {first}"); }
                    }
                    catch (Exception x) { differ++; lines.Add($"FAIL {e}: {x.GetType().Name}: {x.Message.Split('\n')[0]}"); }
                }
            }
            finally
            {
                foreach (IntPtr p in new[] { ctx, dev }) DxbcPixelParity.Release(p);
            }
            string summary = $"{same} draw as fxc's draw, {differ} differ";
            Assert.True(differ == 0, summary + Environment.NewLine + string.Join(Environment.NewLine, lines.Where(l => l.StartsWith("FAIL"))));
        }

        /// <summary>Each compute shader dispatched with fxc's blob and with ours, every UAV compared.</summary>
        [Fact]
        public void EveryComputeShaderOfTheCorpus_ComputesAsFxcsComputes()
        {
            Assert.SkipWhen(Requirements.GpuUnavailable is not null, Requirements.GpuUnavailable ?? "");
            IntPtr dev, ctx;
            uint level;
            Assert.True(Exports.CreateDevicePointer(IntPtr.Zero, 1, IntPtr.Zero, 0, null, 0, 7, &dev, &level, &ctx) >= 0, "shim");
            var lines = new List<string>();
            int same = 0, differ = 0;
            try
            {
                string? only = Environment.GetEnvironmentVariable("HLSL_ONLY");
                foreach (var e in Entries().Where(x => x.Profile.StartsWith("cs_", StringComparison.Ordinal)))
                {
                    if (Gaps.ContainsKey(e.Blob)) continue;
                    if (only != null && !e.Blob.Contains(only, StringComparison.OrdinalIgnoreCase)) continue;
                    byte[] fxcBlob = Fxc(e);
                    var ours = Compile(Source(e), e.File, e.Name, e.Profile, e.Defines, out string errors);
                    if (ours == null) { differ++; lines.Add($"FAIL {e}: {errors.Trim()}"); continue; }
                    if (Environment.GetEnvironmentVariable("HLSL_DUMP") is { Length: > 0 } dump)
                    {
                        Directory.CreateDirectory(dump);
                        File.WriteAllLines(Path.Combine(dump, e.Blob + ".ours.txt"), DxbcShader.Parse(ours).Instructions.Select(i => i.ToString()));
                        File.WriteAllLines(Path.Combine(dump, e.Blob + ".fxc.txt"), DxbcShader.Parse(fxcBlob).Instructions.Select(i => i.ToString()));
                    }
                    try
                    {
                        var a = DxbcComputeParity.Run(dev, ctx, fxcBlob, DxbcPixelParity.Reflect(fxcBlob), 1);
                        var b = DxbcComputeParity.Run(dev, ctx, ours, DxbcPixelParity.Reflect(ours), 1);
                        if (OrderDependent.Contains(e.Blob)) { same++; lines.Add($"OK   {e} (run, not compared: the order threads run in shows)"); continue; }
                        var (count, first) = DxbcComputeParity.Compare(a, b);
                        if (count == 0 && a.Count == b.Count) { same++; lines.Add($"OK   {e}"); }
                        else { differ++; lines.Add($"FAIL {e}: {count} words differ; {first}"); }
                    }
                    catch (Exception x) { differ++; lines.Add($"FAIL {e}: {x.GetType().Name}: {x.Message.Split('\n')[0]}"); }
                }
            }
            finally
            {
                foreach (IntPtr p in new[] { ctx, dev }) DxbcPixelParity.Release(p);
            }
            string summary = $"{same} compute as fxc's compute, {differ} differ";
            Assert.True(differ == 0, summary + Environment.NewLine + string.Join(Environment.NewLine, lines.Where(l => l.StartsWith("FAIL"))));
        }

        /// <summary>A probe: HLSL_DUMP_FXC=dir writes each corpus blob's instructions (with their
        /// opcode tokens' control bits), the reference the back end is written against.</summary>
        [Fact]
        public void DumpFxc()
        {
            string? dir = Environment.GetEnvironmentVariable("HLSL_DUMP_FXC");
            Assert.SkipWhen(string.IsNullOrEmpty(dir), "a probe: HLSL_DUMP_FXC=<directory>");
            Directory.CreateDirectory(dir!);
            foreach (var e in Entries())
            {
                var s = DxbcShader.Parse(Fxc(e));
                File.WriteAllLines(Path.Combine(dir!, e.Blob + ".txt"), s.Instructions.Select(i => $"{i} ; ctl 0x{(i.Token >> 11) & 0x1FFF:x}"));
            }
        }

        /// <summary>Each geometry shader drawn between a generated vertex and pixel shader (our
        /// compiler's), with fxc's blob and with ours, the targets compared.</summary>
        [Fact]
        public void EveryGeometryShaderOfTheCorpus_DrawsAsFxcsDraws()
        {
            Assert.SkipWhen(Requirements.GpuUnavailable is not null, Requirements.GpuUnavailable ?? "");
            IntPtr dev, ctx;
            uint level;
            Assert.True(Exports.CreateDevicePointer(IntPtr.Zero, 1, IntPtr.Zero, 0, null, 0, 7, &dev, &level, &ctx) >= 0, "shim");
            var lines = new List<string>();
            int same = 0, differ = 0;
            try
            {
                foreach (var e in Entries().Where(x => x.Profile.StartsWith("gs_", StringComparison.Ordinal)))
                {
                    byte[] fxcBlob = Fxc(e);
                    var ours = Compile(Source(e), e.File, e.Name, e.Profile, e.Defines, out string errors);
                    if (ours == null) { differ++; lines.Add($"FAIL {e}: {errors.Trim()}"); continue; }
                    if (Environment.GetEnvironmentVariable("HLSL_DUMP") is { Length: > 0 } dump)
                    {
                        Directory.CreateDirectory(dump);
                        File.WriteAllLines(Path.Combine(dump, e.Blob + ".ours.txt"), DxbcShader.Parse(ours).Instructions.Select(i => $"{i} ; ctl 0x{(i.Token >> 11) & 0x1FFF:x}"));
                    }
                    try
                    {
                        var gs = DxbcShader.Parse(fxcBlob);
                        var topo = DxbcGeometryParity.TopologyFor(GeometryInfo.Of(gs)) ?? throw new InvalidOperationException("an input primitive not drawn here");
                        string profile = gs.Major >= 5 ? "_5_0" : "_4_0";
                        var vs = Compile(DxbcGeometryParity.VertexShaderFor(gs), "gen.hlsl", "main", "vs" + profile, Array.Empty<string>(), out string verr) ?? throw new InvalidOperationException("vertex shader: " + verr);
                        var ps = Compile(DxbcGeometryParity.PixelShaderFor(gs), "gen.hlsl", "main", "ps" + profile, Array.Empty<string>(), out string perr) ?? throw new InvalidOperationException("pixel shader: " + perr);
                        var psShader = DxbcShader.Parse(ps);
                        float[] positions = DxbcGeometryParity.Positions(topo.Vertices);
                        var a = DxbcGeometryParity.Draw(dev, ctx, vs, fxcBlob, ps, psShader, DxbcPixelParity.Reflect(fxcBlob), topo.Topology, positions, 5);
                        var b = DxbcGeometryParity.Draw(dev, ctx, vs, ours, ps, psShader, DxbcPixelParity.Reflect(ours), topo.Topology, positions, 5);
                        var (count, first) = DxbcPixelParity.Compare(a, b);
                        // a draw that covers nothing compares nothing
                        if (DxbcPixelParity.Covered(a) == 0) { differ++; lines.Add($"FAIL {e}: fxc's blob covered nothing"); }
                        else if (count == 0 && a.Count == b.Count) { same++; lines.Add($"OK   {e} ({DxbcPixelParity.Covered(a)} covered)"); }
                        else { differ++; lines.Add($"FAIL {e}: {count} components differ; {first}"); }
                    }
                    catch (Exception x) { differ++; lines.Add($"FAIL {e}: {x.GetType().Name}: {x.Message.Split('\n')[0]}"); }
                }
            }
            finally
            {
                foreach (IntPtr p in new[] { ctx, dev }) DxbcPixelParity.Release(p);
            }
            string summary = $"{same} draw as fxc's draw, {differ} differ";
            Console.Error.WriteLine(summary + Environment.NewLine + string.Join(Environment.NewLine, lines));
            Assert.True(differ == 0, summary + Environment.NewLine + string.Join(Environment.NewLine, lines.Where(l => l.StartsWith("FAIL"))));
        }

        /// <summary>Each hull and domain shader pair of the corpus tessellating two patches, fxc's pair and
        /// ours, with the corpus's constants (a scaling transform, the factors): the targets compared.</summary>
        [Fact]
        public void EveryHullAndDomainShaderOfTheCorpus_TessellatesAsFxcsDo()
        {
            Assert.SkipWhen(Requirements.GpuUnavailable is not null, Requirements.GpuUnavailable ?? "");
            IntPtr dev, ctx;
            uint level;
            Assert.True(Exports.CreateDevicePointer(IntPtr.Zero, 1, IntPtr.Zero, 0, null, 0, 7, &dev, &level, &ctx) >= 0, "shim");
            const string vsHlsl = "struct Cp { float3 position : POSITION; float2 uv : TEXCOORD0; };\nCp main(float3 p : POSITION) { Cp c; c.position = p; c.uv = p.xy * 0.5 + 0.5; return c; }\n";
            const string psHlsl = "float4 main(float4 pos : SV_Position) : SV_Target { return float4(frac(pos.xy / 7), pos.z, 1); }\n";
            var vs = Compile(vsHlsl, "gen.hlsl", "main", "vs_5_0", Array.Empty<string>(), out string verr) ?? throw new InvalidOperationException(verr);
            var ps = Compile(psHlsl, "gen.hlsl", "main", "ps_5_0", Array.Empty<string>(), out string perr) ?? throw new InvalidOperationException(perr);
            // Params: transform (a scale that keeps ds_tri's offset patches -- it adds a control point
            // and the primitive id -- inside the target and the depth range), tess (the factors), color
            var constants = new float[24];
            constants[0] = constants[5] = 0.4f; constants[10] = 0.2f; constants[15] = 1;
            constants[16] = 3; constants[17] = 2; constants[18] = 4; constants[19] = 2.5f;
            var cb = new byte[constants.Length * 4];
            Buffer.BlockCopy(constants, 0, cb, 0, cb.Length);
            var lines = new List<string>();
            int same = 0, differ = 0;
            try
            {
                foreach (var (hsName, dsName, points) in new[] { ("hs_tri", "ds_tri", 3), ("hs_quad", "ds_quad", 4), ("hs_line", "ds_line", 2) })
                {
                    var hsE = Entries().First(x => x.Name == hsName);
                    var dsE = Entries().First(x => x.Name == dsName);
                    var hs = Compile(Source(hsE), hsE.File, hsE.Name, hsE.Profile, hsE.Defines, out string herr);
                    var ds = Compile(Source(dsE), dsE.File, dsE.Name, dsE.Profile, dsE.Defines, out string derr);
                    if (hs == null || ds == null) { differ++; lines.Add($"FAIL {hsName}/{dsName}: {herr.Trim()} {derr.Trim()}"); continue; }
                    if (Environment.GetEnvironmentVariable("HLSL_DUMP") is { Length: > 0 } dump)
                    {
                        Directory.CreateDirectory(dump);
                        File.WriteAllLines(Path.Combine(dump, hsE.Blob + ".ours.txt"), DxbcShader.Parse(hs).Instructions.Select(i => $"{i} ; ctl 0x{(i.Token >> 11) & 0x1FFF:x}"));
                        File.WriteAllLines(Path.Combine(dump, dsE.Blob + ".ours.txt"), DxbcShader.Parse(ds).Instructions.Select(i => $"{i} ; ctl 0x{(i.Token >> 11) & 0x1FFF:x}"));
                    }
                    try
                    {
                        var factors = new[] { 3f, 2f, 4f, 2.5f, 2f, 3f };
                        var a = DxbcTessellationTests.DrawPatches(dev, ctx, vs, Fxc(hsE), Fxc(dsE), ps, points, factors, constants: cb);
                        var b = DxbcTessellationTests.DrawPatches(dev, ctx, vs, hs, ds, ps, points, factors, constants: cb);
                        var (count, first) = DxbcPixelParity.Compare(a, b);
                        if (DxbcPixelParity.Covered(a) == 0) { differ++; lines.Add($"FAIL {hsName}/{dsName}: fxc's pair covered nothing"); }
                        else if (count == 0) { same++; lines.Add($"OK   {hsName}/{dsName} ({DxbcPixelParity.Covered(a)} covered)"); }
                        else { differ++; lines.Add($"FAIL {hsName}/{dsName}: {count} components differ; {first}"); }
                    }
                    catch (Exception x) { differ++; lines.Add($"FAIL {hsName}/{dsName}: {x.GetType().Name}: {x.Message.Split('\n')[0]}"); }
                }
            }
            finally
            {
                foreach (IntPtr p in new[] { ctx, dev }) DxbcPixelParity.Release(p);
            }
            string summary = $"{same} tessellate as fxc's do, {differ} differ";
            Console.Error.WriteLine(summary + Environment.NewLine + string.Join(Environment.NewLine, lines));
            Assert.True(differ == 0, summary + Environment.NewLine + string.Join(Environment.NewLine, lines.Where(l => l.StartsWith("FAIL"))));
        }

        [Fact]
        public void Errors_AreFxcsText()
        {
            var blob = Compile("float4 main() : SV_Target { return undefined_thing; }", "bad.hlsl", "main", "ps_4_0", Array.Empty<string>(), out string errors);
            Assert.Null(blob);
            Assert.Matches(@"^bad\.hlsl\(1,\d+\): error X3004: ", errors);
        }
    }
}
