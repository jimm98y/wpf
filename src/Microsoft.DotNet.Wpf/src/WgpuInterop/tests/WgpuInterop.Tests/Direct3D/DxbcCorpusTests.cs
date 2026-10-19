// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// The DXBC translator over a corpus of compiled shaders (an application's, e.g. DXEngine's 170): every
// vertex and pixel shader must parse, translate and compile as WGSL on this device. The corpus is not
// part of the repository (it is the application's); DXBC_CORPUS names a directory of shader blobs
// (*.vs, *.ps, *.gs, or *.dxbc), and DXBC_REPORT, when set, receives one line per shader.
// Without DXBC_CORPUS the test is skipped.
//

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Wpf.Interop.WebGpu.Direct3D;
using Microsoft.Wpf.Interop.WebGpu.Direct3D.Dxbc;
using WgpuInterop.Tests.Harness;
using Xunit;

namespace WgpuInterop.Tests.Direct3D
{
    public sealed class DxbcCorpusTests
    {
        [Fact]
        public void EveryShaderOfTheCorpus_TranslatesAndCompiles()
        {
            string? dir = Environment.GetEnvironmentVariable("DXBC_CORPUS");
            Assert.SkipWhen(string.IsNullOrEmpty(dir) || !Directory.Exists(dir), "DXBC_CORPUS names no directory of shaders");
            Assert.SkipWhen(Requirements.GpuUnavailable is not null, Requirements.GpuUnavailable ?? "");

            var device = new D3DDevice(D3DDevice.SharedGpu, ownsGpu: false, D3D_FEATURE_LEVEL.L11_0, 0, null);
            try
            {
                var files = Directory.GetFiles(dir!).Where(f => f.EndsWith(".vs") || f.EndsWith(".ps") || f.EndsWith(".gs") || f.EndsWith(".dxbc")).OrderBy(f => f).ToList();
                var parsed = new Dictionary<string, DxbcShader>();
                var lines = new List<string>();
                int ok = 0, failed = 0, geometry = 0;
                foreach (string f in files)
                {
                    try { parsed[f] = DxbcShader.Parse(File.ReadAllBytes(f)); }
                    catch (Exception e) { failed++; lines.Add($"FAIL parse {Path.GetFileName(f)}: {e.Message}"); }
                }
                // a vertex shader links against the pixel shader of the same name, else none
                foreach (var (f, s) in parsed)
                {
                    string name = Path.GetFileName(f);
                    if (s.Stage == ShaderStage.Geometry) { geometry++; lines.Add($"GS   {name}"); continue; }
                    try
                    {
                        DxbcShader? ps = s.Stage == ShaderStage.Vertex
                            ? parsed.FirstOrDefault(kv => kv.Value.Stage == ShaderStage.Pixel && Path.GetFileNameWithoutExtension(kv.Key) == Path.GetFileNameWithoutExtension(f)).Value
                            : s;
                        var link = DxbcTranslator.LinkageOf(ps);
                        var t = DxbcTranslator.Translate(s, s.Stage == ShaderStage.Vertex ? 0 : 1, link);
                        if (Environment.GetEnvironmentVariable("DXBC_DUMP") is { Length: > 0 } dump) { Directory.CreateDirectory(dump); File.WriteAllText(Path.Combine(dump, name + ".wgsl"), t.Wgsl); }
                        IntPtr module = device.Immediate.CreateModule(t.Wgsl, name, out string? error);
                        if (module == IntPtr.Zero)
                        {
                            failed++;
                            lines.Add($"FAIL wgsl  {name}: {error?.Split('\n').FirstOrDefault(l => l.Contains("error"))?.Trim() ?? error}");
                            if (Environment.GetEnvironmentVariable("DXBC_REPORT") is { Length: > 0 } r)
                                File.WriteAllText(Path.Combine(Path.GetDirectoryName(r)!, name + ".wgsl"), t.Wgsl + "\n/*\n" + error + "\n*/\n");
                        }
                        else
                        {
                            ok++;
                            lines.Add($"OK   {name}");
                            Microsoft.Wpf.Interop.WebGpu.Wgpu.wgpuShaderModuleRelease(module);
                        }
                    }
                    catch (Exception e)
                    {
                        failed++;
                        lines.Add($"FAIL translate {name}: {e.GetType().Name}: {e.Message}");
                    }
                }
                string summary = $"{ok} compiled, {failed} failed, {geometry} geometry shaders (the compute path) of {files.Count}";
                lines.Insert(0, summary);
                if (Environment.GetEnvironmentVariable("DXBC_REPORT") is { Length: > 0 } report) File.WriteAllLines(report, lines);
                Assert.True(failed == 0, summary + Environment.NewLine + string.Join(Environment.NewLine, lines.Where(l => l.StartsWith("FAIL")).Take(20)));
            }
            finally { device.Release(); }
        }

        [System.Runtime.InteropServices.DllImport("d3d11.dll", EntryPoint = "D3D11CreateDevice")]
        private static extern unsafe int RealCreateDevice(IntPtr adapter, uint driverType, IntPtr software, uint flags, uint* levels, uint count, uint sdk, IntPtr* device, uint* level, IntPtr* context);

        /// <summary>Every pixel shader of the corpus draws on the shim what it draws on WARP, from the
        /// same inputs (DxbcPixelParity). DXBC_ONLY narrows the run to names containing it.</summary>
        [Fact]
        public unsafe void EveryPixelShaderOfTheCorpus_DrawsAsWarpDraws()
        {
            string? dir = Environment.GetEnvironmentVariable("DXBC_CORPUS");
            Assert.SkipWhen(string.IsNullOrEmpty(dir) || !Directory.Exists(dir), "DXBC_CORPUS names no directory of shaders");
            Assert.SkipUnless(OperatingSystem.IsWindows(), "real Direct3D 11 (WARP) exists on Windows only");
            Assert.SkipWhen(Requirements.GpuUnavailable is not null, Requirements.GpuUnavailable ?? "");
            string? only = Environment.GetEnvironmentVariable("DXBC_ONLY");

            IntPtr wDev, wCtx, sDev, sCtx;
            uint level, want = 0xb000;
            Assert.True(RealCreateDevice(IntPtr.Zero, 5, IntPtr.Zero, 0, &want, 1, 7, &wDev, &level, &wCtx) >= 0, "WARP");
            Assert.True(Exports.CreateDevicePointer(IntPtr.Zero, 1, IntPtr.Zero, 0, null, 0, 7, &sDev, &level, &sCtx) >= 0, "shim");
            var lines = new List<string>();
            int same = 0, differ = 0, skipped = 0;
            try
            {
                foreach (string f in Directory.GetFiles(dir!, "*.ps").OrderBy(f => f))
                {
                    string name = Path.GetFileName(f);
                    if (only != null && !name.Contains(only, StringComparison.OrdinalIgnoreCase)) continue;
                    byte[] blob = File.ReadAllBytes(f);
                    var ps = DxbcShader.Parse(blob);
                    var refl = DxbcPixelParity.Reflect(blob);
                    string vsHlsl = DxbcPixelParity.VertexShaderFor(ps)!;
                    byte[] vs = DxbcPixelParity.Compile(vsHlsl, ps.Major >= 5 ? "vs_5_0" : "vs_4_0", out string? cerr);
                    if (vs.Length == 0) { skipped++; lines.Add($"SKIP {name}: vertex shader: {cerr?.Trim()}"); continue; }
                    List<float[]> warp, shim;
                    try { warp = DxbcPixelParity.Draw(wDev, wCtx, vs, blob, ps, refl, 1); }
                    catch (Exception e) { skipped++; lines.Add($"SKIP {name}: WARP: {e.Message}"); continue; }
                    try { shim = DxbcPixelParity.Draw(sDev, sCtx, vs, blob, ps, refl, 1); }
                    catch (Exception e) { differ++; lines.Add($"FAIL {name}: shim: {e.Message}"); continue; }
                    var (count, first) = DxbcPixelParity.Compare(warp, shim);
                    if (count == 0 && warp.Count == shim.Count) { same++; lines.Add($"OK   {name}"); }
                    else { differ++; lines.Add($"FAIL {name}: {count} components differ; {first}"); }
                }
            }
            finally
            {
                foreach (IntPtr p in new[] { sCtx, sDev, wCtx, wDev })
                    ((delegate* unmanaged<IntPtr, uint>)(*(IntPtr**)p)[2])(p);
            }
            string summary = $"{same} draw as WARP draws, {differ} differ, {skipped} not drawn";
            lines.Insert(0, summary);
            if (Environment.GetEnvironmentVariable("DXBC_REPORT") is { Length: > 0 } report) File.WriteAllLines(Path.ChangeExtension(report, ".pixels.txt"), lines);
            Assert.True(differ == 0, summary + Environment.NewLine + string.Join(Environment.NewLine, lines.Where(l => l.StartsWith("FAIL")).Take(25)));
        }

        /// <summary>Every vertex shader of the corpus: its outputs, written to targets by a generated
        /// pixel shader, as WARP's (DxbcPixelParity: a vertex buffer for its input signature, its
        /// constant buffers filled by type -- matrices near the identity keep the triangle on the target).</summary>
        [Fact]
        public unsafe void EveryVertexShaderOfTheCorpus_OutputsAsWarpOutputs()
        {
            string? dir = Environment.GetEnvironmentVariable("DXBC_CORPUS");
            Assert.SkipWhen(string.IsNullOrEmpty(dir) || !Directory.Exists(dir), "DXBC_CORPUS names no directory of shaders");
            Assert.SkipUnless(OperatingSystem.IsWindows(), "real Direct3D 11 (WARP) exists on Windows only");
            Assert.SkipWhen(Requirements.GpuUnavailable is not null, Requirements.GpuUnavailable ?? "");
            string? only = Environment.GetEnvironmentVariable("DXBC_ONLY");

            IntPtr wDev, wCtx, sDev, sCtx;
            uint level, want = 0xb000;
            Assert.True(RealCreateDevice(IntPtr.Zero, 5, IntPtr.Zero, 0, &want, 1, 7, &wDev, &level, &wCtx) >= 0, "WARP");
            Assert.True(Exports.CreateDevicePointer(IntPtr.Zero, 1, IntPtr.Zero, 0, null, 0, 7, &sDev, &level, &sCtx) >= 0, "shim");
            var lines = new List<string>();
            int same = 0, differ = 0, skipped = 0;
            try
            {
                foreach (string f in Directory.GetFiles(dir!, "*.vs").OrderBy(f => f))
                {
                    string name = Path.GetFileName(f);
                    if (only != null && !name.Contains(only, StringComparison.OrdinalIgnoreCase)) continue;
                    byte[] blob = File.ReadAllBytes(f);
                    var vs = DxbcShader.Parse(blob);
                    var refl = DxbcPixelParity.Reflect(blob);
                    string psHlsl = DxbcPixelParity.PixelShaderFor(vs, out int targets);
                    if (targets == 0) { skipped++; lines.Add($"SKIP {name}: no outputs but the position"); continue; }
                    byte[] psBlob = DxbcPixelParity.Compile(psHlsl, vs.Major >= 5 ? "ps_5_0" : "ps_4_0", out string? cerr);
                    if (psBlob.Length == 0) { skipped++; lines.Add($"SKIP {name}: pixel shader: {cerr?.Trim()}"); continue; }
                    var ps = DxbcShader.Parse(psBlob);
                    var psRefl = DxbcPixelParity.Reflect(psBlob);
                    List<float[]> warp, shim;
                    try { warp = DxbcPixelParity.Draw(wDev, wCtx, blob, psBlob, ps, psRefl, refl, vs, 1); }
                    catch (Exception e) { skipped++; lines.Add($"SKIP {name}: WARP: {e.Message}"); continue; }
                    try { shim = DxbcPixelParity.Draw(sDev, sCtx, blob, psBlob, ps, psRefl, refl, vs, 1); }
                    catch (Exception e) { differ++; lines.Add($"FAIL {name}: shim: {e.Message}"); continue; }
                    var (count, first) = DxbcPixelParity.Compare(warp, shim);
                    int wCovered = DxbcPixelParity.Covered(warp), sCovered = DxbcPixelParity.Covered(shim);
                    if (count == 0 && warp.Count == shim.Count) { same++; lines.Add($"OK   {name}{(wCovered > 0 ? "" : " (covered nothing on WARP either)")}"); }
                    // a pixel whose centre lies on the triangle's edge is covered by one GPU's tie-break
                    // rule and not the other's (GL has no top-left rule): coverage, not the translation
                    else if (DxbcPixelParity.OnlyCoverageDiffers(warp, shim, 2) is int edge && edge > 0) { same++; lines.Add($"OK   {name} ({edge} pixel(s) on an edge covered by one rasterizer only)"); }
                    else { differ++; lines.Add($"FAIL {name}: {count} components differ; {first}"); }
                }
            }
            finally
            {
                foreach (IntPtr p in new[] { sCtx, sDev, wCtx, wDev })
                    ((delegate* unmanaged<IntPtr, uint>)(*(IntPtr**)p)[2])(p);
            }
            string summary = $"{same} output as WARP outputs, {differ} differ, {skipped} not drawn";
            lines.Insert(0, summary);
            if (Environment.GetEnvironmentVariable("DXBC_REPORT") is { Length: > 0 } report) File.WriteAllLines(Path.ChangeExtension(report, ".vertices.txt"), lines);
            Assert.True(differ == 0, summary + Environment.NewLine + string.Join(Environment.NewLine, lines.Where(l => l.StartsWith("FAIL")).Take(25)));
        }
    }
}
