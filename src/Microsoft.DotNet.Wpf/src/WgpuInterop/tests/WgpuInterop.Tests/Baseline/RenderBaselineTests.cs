// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// Ported from WgpuInterop.RenderBaselineTest.
//
// WHOLE-IMAGE render regression. The rest of the suite asserts SAMPLED pixels -- "(6,16) should be
// black" -- which pins down the specific thing each test was written for and nothing else. A change
// that shifts every glyph half a pixel, drops a join, or alters a gradient ramp passes all of them.
// This renders a catalogue of scenes and compares the ENTIRE frame against a committed baseline PNG,
// so any visible change has to be looked at and either accepted or fixed.
//
// Comparison is tolerance-based, not a hash. Rendering here measured bit-identical within and across
// processes, so an exact hash would work on this machine -- and would fail on every other GPU and
// driver for reasons that are not regressions. The tolerance is tight enough that a real change
// cannot hide under it: a one-pixel geometry shift moves far more than 24 pixels past a channel
// delta of 8.
//
// Baselines are PER RASTER MODE. The CPU scanline rasterizer and the GPU coverage shader do not
// produce identical anti-aliasing -- measured, the CPU path is the more accurate of the two against
// a supersampled reference -- so one set cannot serve both, and a shared set would fail whenever
// WPF_WEBGPU_CPU_RASTER was set. Splitting them means both rasterizers are guarded instead of
// neither.
//
// Maintenance (the CLI flags the standalone app had, as environment variables):
//
//   WPF_BASELINE_UPDATE=1        rewrite the baselines for the ACTIVE raster mode
//   WPF_BASELINE_WRITE_ACTUAL=1  also dump actual+diff PNGs beside the baselines
//
// WPF_BASELINE_UPDATE deliberately makes every scene report as skipped rather than passed: a run
// that rewrote the baselines has verified nothing, and reporting it green would be a lie.
//

using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Wpf.Interop.WebGpu.Composition;
using WgpuInterop.Tests.Harness;
using Xunit;

namespace WgpuInterop.Tests.Baseline
{
    public sealed class RenderBaselineTests : RendererTestBase
    {
        public RenderBaselineTests(GpuFixture gpu) : base(gpu) { }

        // A pixel counts as changed past this per-channel delta; a scene fails past this many
        // changed pixels. Non-zero on both because a driver or math change can shift an
        // anti-aliased edge by one quantisation step without anything being wrong.
        private const int ChannelTolerance = 8;
        private const int MaxChangedPixels = 24;

        public static TheoryData<string> SceneNames
        {
            get
            {
                var d = new TheoryData<string>();
                foreach ((string name, int _, int _, Func<SceneVisual> _) in BaselineScenes.Scenes()) d.Add(name);
                return d;
            }
        }

        [Theory]
        [MemberData(nameof(SceneNames))]
        public void Scene_MatchesItsBaseline(string sceneName)
        {
            (string name, int w, int h, Func<SceneVisual> build) = FindScene(sceneName);

            byte[] actual = Render(build(), w, h);
            string dir = BaselineDir();
            string path = Path.Combine(dir, name + ".png");

            bool update = Environment.GetEnvironmentVariable("WPF_BASELINE_UPDATE") == "1";
            bool writeActual = Environment.GetEnvironmentVariable("WPF_BASELINE_WRITE_ACTUAL") == "1";

            if (update)
            {
                Directory.CreateDirectory(dir);
                PngWriter.Write(path, actual, w, h, w);       // maxWidth = w: never downsample
                Assert.Skip($"WPF_BASELINE_UPDATE: rewrote {path}; this run verified nothing");
            }

            // A MISSING baseline is a failure, not a pass. Silently writing one would mean a brand
            // new scene is "guarded" by whatever it happened to render the first time, including a bug.
            Assert.True(File.Exists(path),
                $"no baseline at {path}. If this scene is new, run once with WPF_BASELINE_UPDATE=1 and " +
                "REVIEW the generated image before committing it.");

            byte[] expect = PngReader.Read(path, out int bw, out int bh);
            Assert.True(bw == w && bh == h, $"baseline is {bw}x{bh} but the render is {w}x{h}");

            int changed = 0, maxDelta = 0;
            byte[]? diff = writeActual ? new byte[w * h * 4] : null;
            for (int i = 0; i < w * h; i++)
            {
                int d = 0;
                for (int c = 0; c < 3; c++)
                    d = Math.Max(d, Math.Abs(actual[i * 4 + c] - expect[i * 4 + c]));
                maxDelta = Math.Max(maxDelta, d);
                if (d > ChannelTolerance) changed++;
                if (diff is not null)
                {
                    byte v = (byte)Math.Min(255, d * 8);
                    diff[i * 4] = v; diff[i * 4 + 1] = (byte)(255 - v); diff[i * 4 + 2] = (byte)(255 - v);
                    diff[i * 4 + 3] = 255;
                }
            }

            if (diff is not null)
            {
                PngWriter.Write(Path.Combine(dir, name + ".actual.png"), actual, w, h, w);
                PngWriter.Write(Path.Combine(dir, name + ".diff.png"), diff, w, h, w);
            }

            Assert.True(changed <= MaxChangedPixels,
                $"{name}: {changed} pixels differ by more than {ChannelTolerance} (max delta {maxDelta}). " +
                "Re-run with WPF_BASELINE_WRITE_ACTUAL=1 to dump actual/diff images; if the change is " +
                "intended, WPF_BASELINE_UPDATE=1 rewrites the baseline.");
        }

        private static (string, int, int, Func<SceneVisual>) FindScene(string name)
        {
            foreach ((string n, int w, int h, Func<SceneVisual> b) in BaselineScenes.Scenes())
                if (n == name) return (n, w, h, b);
            throw new InvalidOperationException($"unknown baseline scene '{name}'");
        }

        /// <summary>
        /// The baseline set for the EFFECTIVE raster mode, not the requested one.
        ///
        /// Constructing a renderer can turn the GPU rasterizer off for the adapter it got -- the
        /// virgl fallback, see WgpuSceneRenderer near IsVirgl. Comparing that CPU output against the
        /// GPU baselines would report the fallback as a pile of scene failures, so the mode is read
        /// back after a renderer exists rather than from the environment variable that usually causes
        /// it. (The guideline self-check learned the same lesson the hard way.)
        /// </summary>
        private string BaselineDir()
        {
            _ = NewRenderer();   // force the adapter-dependent raster decision
            return Path.Combine(SourceBaselineRoot(), WgpuSceneRenderer.s_gpuRaster ? "gpu" : "cpu");
        }

        /// <summary>
        /// Baselines live beside the SOURCE, not in bin/, so they are reviewable in a diff and a
        /// rewrite lands somewhere a human will see it.
        /// </summary>
        private static string SourceBaselineRoot()
        {
            var d = new DirectoryInfo(AppContext.BaseDirectory);
            for (int up = 0; up < 8 && d is not null; up++, d = d.Parent)
            {
                string candidate = Path.Combine(d.FullName, "Baseline", "baselines");
                if (Directory.Exists(candidate)) return candidate;
            }
            return Path.Combine(AppContext.BaseDirectory, "Baseline", "baselines");
        }
    }
}
