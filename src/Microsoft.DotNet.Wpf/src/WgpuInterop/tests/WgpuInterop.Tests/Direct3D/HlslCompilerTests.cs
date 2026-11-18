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
            foreach (var e in Entries().Where(Drawable))
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
            Assert.True(failures.Count == 0, $"{ok} compiled as fxc's do, {failures.Count} did not:{Environment.NewLine}{string.Join(Environment.NewLine, failures)}");
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

        [Fact]
        public void Errors_AreFxcsText()
        {
            var blob = Compile("float4 main() : SV_Target { return undefined_thing; }", "bad.hlsl", "main", "ps_4_0", Array.Empty<string>(), out string errors);
            Assert.Null(blob);
            Assert.Matches(@"^bad\.hlsl\(1,\d+\): error X3004: ", errors);
        }
    }
}
