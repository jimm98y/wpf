// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// winmm's wave samples (Winmm/WaveFormat.cs): each format's bytes to float and back, G.711's of its steps, the
// resampler's frames of the rates' ratio and its ramp unbroken across calls.
//

using System;
using System.Collections.Generic;
using Microsoft.Wpf.Interop.WebGpu.Winmm;
using Xunit;

namespace WgpuInterop.Tests.MediaFoundation
{
    public sealed class WaveFormatTests
    {
        private static WaveFormat F(SampleKind k, int bits) => new() { Kind = k, Bits = bits, Container = bits, Channels = 1, Rate = 8000, BlockAlign = bits / 8 };

        [Fact]
        public void EachFormat_RoundTrips()
        {
            foreach (var (kind, bits, tolerance) in new (SampleKind, int, double)[]
            {
                (SampleKind.Pcm, 8, 1 / 128.0),
                (SampleKind.Pcm, 16, 1 / 32768.0),
                (SampleKind.Pcm, 24, 1 / 8388608.0),
                (SampleKind.Pcm, 32, 1e-7),
                (SampleKind.Float, 16, 1e-3),
                (SampleKind.Float, 32, 0),
                (SampleKind.Float, 64, 1e-7),
                (SampleKind.ALaw, 8, 0.032),
                (SampleKind.MuLaw, 8, 0.032)
            })
                RoundTrip(kind, bits, tolerance);
        }

        private static void RoundTrip(SampleKind kind, int bits, double tolerance)
        {
            var f = F(kind, bits);
            var s = new float[] { 0f, 0.5f, -0.5f, 0.25f, -0.999f, 0.999f, 0.01f, -0.01f };
            var bytes = new byte[s.Length * bits / 8];
            f.Encode(s, bytes);
            var back = new float[s.Length];
            f.Decode(bytes, back);
            for (int i = 0; i < s.Length; i++)
                Assert.True(Math.Abs(back[i] - s[i]) <= tolerance + Math.Abs(s[i]) * (kind is SampleKind.ALaw or SampleKind.MuLaw ? 0.07 : 0), $"{kind} {bits}: {s[i]} -> {back[i]}");
        }

        [Fact]
        public void G711_GivesTheStandardsCodes()
        {
            var a = F(SampleKind.ALaw, 8);
            var u = F(SampleKind.MuLaw, 8);
            var b = new byte[1];
            a.Encode(new[] { 0f }, b); Assert.Equal(0xD5, b[0]);           // A-law's zero
            u.Encode(new[] { 0f }, b); Assert.Equal(0xFF, b[0]);           // mu-law's zero
            var v = new float[1];
            u.Decode(new byte[] { 0x80 }, v); Assert.Equal(32124 / 32768f, v[0], 4);    // mu-law's largest
            a.Decode(new byte[] { 0xAA }, v); Assert.Equal(32256 / 32768f, v[0], 4);    // A-law's largest
        }

        [Fact]
        public void TheResampler_GivesTheRatiosFrames_ContinuousAcrossCalls()
        {
            var r = new LinearResampler(1);
            var out1 = new List<float>();
            // a ramp of 0..4409 at 44100 into 48000, in calls of 441 frames
            for (int c = 0; c < 10; c++)
            {
                var input = new float[441];
                for (int i = 0; i < 441; i++) input[i] = c * 441 + i;
                r.Process(input, 44100 / 48000.0, out1);
            }
            Assert.InRange(out1.Count, 4799, 4801);
            for (int i = 1; i < out1.Count; i++) Assert.Equal(44100 / 48000.0, out1[i] - out1[i - 1], 2);          // (of float32 values to 4410)
        }
    }
}
