// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// Opcode coverage of the DXBC translator (Documentation/d3d11-webgpu-shim.md, section 9).
//
// Every shader of the translator's own corpus (Direct3D/Corpus: HLSL written per instruction family
// and stage, compiled with fxc for each profile, checked in) -- and of any corpus DXBC_CORPUS names --
// is parsed, translated and compiled as WGSL on this device. The report counts every opcode against
// the shaders that use it.
//
// The test is a ratchet. Corpus/known-gaps.txt names the shaders that do not translate yet, each with
// the start of the reason. A failure that is not listed fails the test, and so does a listed shader
// that now translates (take it off the list). DXBC_COVERAGE_REPORT writes the full report.
//

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.Wpf.Interop.WebGpu.Direct3D;
using Microsoft.Wpf.Interop.WebGpu.Direct3D.Dxbc;
using WgpuInterop.Tests.Harness;
using Xunit;

namespace WgpuInterop.Tests.Direct3D
{
    public sealed class DxbcCoverageTests
    {
        private static string CorpusDir => Path.Combine(AppContext.BaseDirectory, "DxbcCorpus");

        private static IEnumerable<string> Shaders()
        {
            foreach (string f in Directory.GetFiles(CorpusDir, "*.dxbc").OrderBy(f => f)) yield return f;
            if (Environment.GetEnvironmentVariable("DXBC_CORPUS") is { Length: > 0 } more)
                foreach (string d in more.Split(';', StringSplitOptions.RemoveEmptyEntries).Where(Directory.Exists))
                    foreach (string f in Directory.GetFiles(d).Where(f => f.EndsWith(".vs") || f.EndsWith(".ps") || f.EndsWith(".gs") || f.EndsWith(".hs") || f.EndsWith(".ds") || f.EndsWith(".cs") || f.EndsWith(".dxbc")).OrderBy(f => f))
                        yield return f;
        }

        /// <summary>The known gaps: shader file name, then the start of the reason it fails today.</summary>
        private static Dictionary<string, string> KnownGaps()
        {
            var gaps = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            string path = Path.Combine(AppContext.BaseDirectory, "DxbcCorpus", "known-gaps.txt");
            if (!File.Exists(path)) path = Path.Combine(FindSource(), "known-gaps.txt");
            if (!File.Exists(path)) return gaps;
            foreach (string line in File.ReadAllLines(path))
            {
                if (line.Length == 0 || line.StartsWith('#')) continue;
                int c = line.IndexOf(": ", StringComparison.Ordinal);
                if (c > 0) gaps[line[..c].Trim()] = line[(c + 2)..].Trim();
            }
            return gaps;
        }

        private static string FindSource([System.Runtime.CompilerServices.CallerFilePath] string here = "") => Path.Combine(Path.GetDirectoryName(here)!, "Corpus");

        [System.Runtime.InteropServices.DllImport("d3d11.dll", EntryPoint = "D3D11CreateDevice")]
        private static extern unsafe int RealCreateDevice(IntPtr adapter, uint driverType, IntPtr software, uint flags, uint* levels, uint count, uint sdk, IntPtr* device, uint* level, IntPtr* context);

        /// <summary>Corpus compute shaders whose UAVs depend on the order threads run in (an atomic's
        /// returned value stored per thread, a race on one element, a counter's handed-out indices):
        /// they are dispatched on both, but not compared.</summary>
        private static readonly HashSet<string> s_orderDependent = new(StringComparer.OrdinalIgnoreCase)
        {
            "compute.cs_atomics.cs_5_0.dxbc", "compute.cs_counter_down.cs_5_0.dxbc", "compute.cs_uav.cs_5_0.dxbc",
        };

        /// <summary>Differences that are the driver's, not the translator's: shader, the adapter it
        /// shows on, and the one word that differs there. Parallels' GL (over Metal) folds an atomic
        /// add of a value an earlier atomic returned into one add per wave of the first lane's value
        /// (the sum of 64 returned values comes out 64*base+1024, not 64*base+2016); the same add of
        /// (old &amp; 63) is exact, and the returned values themselves are all distinct.</summary>
        private static readonly (string Shader, string Adapter, string Word)[] s_driverDifferences =
        {
            ("compute_exec.cs_atomic_totals.cs_5_0.dxbc", "Parallels", "rawOut[9]:"),
        };

        /// <summary>Every compute shader of the corpus that translates computes on the shim what it
        /// computes on WARP, from the same inputs (DxbcComputeParity).</summary>
        [Fact]
        public unsafe void EveryCorpusComputeShader_ComputesAsWarpComputes()
        {
            Assert.SkipUnless(OperatingSystem.IsWindows(), "real Direct3D 11 (WARP) exists on Windows only");
            Assert.SkipWhen(Requirements.GpuUnavailable is not null, Requirements.GpuUnavailable ?? "");
            var gaps = KnownGaps();
            IntPtr wDev, wCtx, sDev, sCtx;
            uint level, want = 0xb000;
            Assert.True(RealCreateDevice(IntPtr.Zero, 5, IntPtr.Zero, 0, &want, 1, 7, &wDev, &level, &wCtx) >= 0, "WARP");
            Assert.True(Exports.CreateDevicePointer(IntPtr.Zero, 1, IntPtr.Zero, 0, null, 0, 7, &sDev, &level, &sCtx) >= 0, "shim");
            var failures = new List<string>();
            int compared = 0, runOnly = 0;
            try
            {
                foreach (string f in Shaders().Where(f => Path.GetFileName(f).Contains(".cs_")))
                {
                    string name = Path.GetFileName(f);
                    if (gaps.ContainsKey(name)) continue;
                    byte[] blob = File.ReadAllBytes(f);
                    var refl = DxbcPixelParity.Reflect(blob);
                    List<DxbcComputeParity.Output> warp, shim;
                    try { warp = DxbcComputeParity.Run(wDev, wCtx, blob, refl, 3); }
                    catch (Exception e) { failures.Add($"{name}: WARP: {e.Message}"); continue; }
                    try { shim = DxbcComputeParity.Run(sDev, sCtx, blob, refl, 3); }
                    catch (Exception e) { failures.Add($"{name}: shim: {e.Message}"); continue; }
                    if (s_orderDependent.Contains(name)) { runOnly++; continue; }
                    var (count, first) = DxbcComputeParity.Compare(warp, shim);
                    compared++;
                    string adapter = D3DDevice.SharedGpu.AdapterDescription ?? "";
                    if (count == 1 && s_driverDifferences.Any(d => d.Shader == name && adapter.Contains(d.Adapter, StringComparison.Ordinal) && first.StartsWith(d.Word, StringComparison.Ordinal)))
                        Console.WriteLine($"  {name}: the known driver difference on this adapter: {first}");
                    else if (count > 0) failures.Add($"{name}: {count} words differ; {first}");
                }
            }
            finally
            {
                // a dispatch the shim skipped (no pipeline, an unbindable UAV) is a failure even unseen
                var shimContext = Microsoft.Wpf.Interop.WebGpu.Direct3D.ComObject.From<D3DContext>(sCtx);
                if (shimContext != null && shimContext.DispatchesSkipped > 0) failures.Add($"the shim skipped {shimContext.DispatchesSkipped} dispatch(es)");
                foreach (IntPtr p in new[] { sCtx, sDev, wCtx, wDev })
                    ((delegate* unmanaged<IntPtr, uint>)(*(IntPtr**)p)[2])(p);
            }
            Assert.True(failures.Count == 0, $"{compared} compared, {runOnly} run only; " + string.Join(Environment.NewLine, failures));
        }

        [Fact]
        public void EveryCorpusShader_TranslatesOrIsAKnownGap()
        {
            Assert.SkipWhen(Requirements.GpuUnavailable is not null, Requirements.GpuUnavailable ?? "");
            var device = new D3DDevice(D3DDevice.SharedGpu, ownsGpu: false, D3D_FEATURE_LEVEL.L11_0, 0, null);
            var gaps = KnownGaps();
            var uses = new Dictionary<Opcode, (int Shaders, int Translated)>();
            var results = new List<(string Name, string? Error)>();
            try
            {
                foreach (string f in Shaders())
                {
                    string name = Path.GetFileName(f);
                    DxbcShader s;
                    try { s = DxbcShader.Parse(File.ReadAllBytes(f)); }
                    catch (Exception e) { results.Add((name, "parse: " + e.Message)); continue; }
                    string? error = null;
                    try
                    {
                        var link = s.Stage == ShaderStage.Vertex ? DxbcTranslator.LinkageOf(null) : DxbcTranslator.LinkageOf(s.Stage == ShaderStage.Pixel ? s : null);
                        var t = DxbcTranslator.Translate(s, s.Stage == ShaderStage.Pixel ? 1 : 0, link);
                        IntPtr module = device.Immediate.CreateModule(t.Wgsl, name, out string? werr);
                        if (module == IntPtr.Zero) error = "wgsl: " + (werr?.Split('\n').FirstOrDefault(l => l.Contains("error"))?.Trim() ?? werr);
                        else Microsoft.Wpf.Interop.WebGpu.Wgpu.wgpuShaderModuleRelease(module);
                    }
                    catch (NotSupportedException e) { error = e.Message; }
                    catch (Exception e) { error = $"{e.GetType().Name}: {e.Message}"; }
                    results.Add((name, error));
                    foreach (Opcode op in s.Instructions.Select(i => i.Op).Distinct())
                    {
                        var u = uses.TryGetValue(op, out var v) ? v : (Shaders: 0, Translated: 0);
                        uses[op] = (u.Shaders + 1, u.Translated + (error == null ? 1 : 0));
                    }
                }
            }
            finally { device.Release(); }

            var unexpected = results.Where(r => r.Error != null && !(gaps.TryGetValue(r.Name, out var why) && r.Error.StartsWith(why, StringComparison.Ordinal))).ToList();
            var fixedNow = results.Where(r => r.Error == null && gaps.ContainsKey(r.Name)).Select(r => r.Name).ToList();
            var opsOnlyInFailures = uses.Where(kv => kv.Value.Translated == 0).Select(kv => DxbcOpcodes.Info(kv.Key).Name).OrderBy(n => n).ToList();
            var neverSeen = DxbcOpcodes.All.Where(i => !uses.ContainsKey(i.Op)).Select(i => i.Name).OrderBy(n => n).ToList();

            var report = new StringBuilder();
            report.AppendLine($"{results.Count(r => r.Error == null)} of {results.Count} shaders translate; {results.Count(r => r.Error != null)} do not ({unexpected.Count} unexpectedly)");
            report.AppendLine($"opcodes seen: {uses.Count}; seen only in shaders that fail: {opsOnlyInFailures.Count}; in the specification but in no corpus: {neverSeen.Count}");
            report.AppendLine("only in failing shaders: " + string.Join(" ", opsOnlyInFailures));
            report.AppendLine("in no corpus: " + string.Join(" ", neverSeen));
            report.AppendLine();
            foreach (var kv in uses.OrderBy(k => (int)k.Key)) report.AppendLine($"{DxbcOpcodes.Info(kv.Key).Name,-36} {kv.Value.Translated,4}/{kv.Value.Shaders,-4}");
            report.AppendLine();
            foreach (var r in results) report.AppendLine(r.Error == null ? $"OK   {r.Name}" : $"FAIL {r.Name}: {r.Error}");
            if (Environment.GetEnvironmentVariable("DXBC_COVERAGE_REPORT") is { Length: > 0 } path) File.WriteAllText(path, report.ToString());

            Assert.True(unexpected.Count == 0 && fixedNow.Count == 0,
                (unexpected.Count > 0 ? "not translated, and not a known gap:" + Environment.NewLine + string.Join(Environment.NewLine, unexpected.Take(30).Select(u => $"  {u.Name}: {u.Error}")) + Environment.NewLine : "")
                + (fixedNow.Count > 0 ? "translate now; take them off known-gaps.txt: " + string.Join(", ", fixedNow) + Environment.NewLine : "")
                + report.ToString().Split(Environment.NewLine).Take(4).Aggregate((a, b) => a + Environment.NewLine + b));
        }
    }
}
