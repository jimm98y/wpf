// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// The encoder MFTs (MFEncoders.cs) against Windows' (Encoder.windows.txt: MFEncoderProbe's Details and Dumps on
// Windows): what MFTEnumEx lists, every type each way, what each type set is answered with, ICodecAPI, the stream
// infos, and when outputs come and with what times and attributes. What the encoders make is each system's own:
// the bytes, the parameter sets, the order of pictures (Windows' B pictures) and AAC's priming are not compared.
// On Windows the test drives Windows' own encoders.
//

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;

namespace WgpuInterop.Tests.MediaFoundation
{
    public sealed class MFEncoderTests
    {
        private static bool IsOutput(string l) => l.StartsWith("    output: ");

        /// <summary>An output line as compared: its duration and its attributes' names (video), or its time and duration too (audio).</summary>
        private static string Shape(string l, bool video)
        {
            var parts = l.Trim().Split(' ');
            int at = l.IndexOf('[');
            // (not MFSampleExtension_VideoEncodeQP, b2efe478-...: the quantiser the encoder chose, its own)
            var names = at < 0 ? "" : string.Join(" ", l[(at + 1)..].TrimEnd(']').Split(' ').Select(a => a.Split('=')[0]).Where(n => !n.StartsWith("b2efe478-")));
            return video ? $"output: duration {parts[4]} [{names}]" : $"output: time {parts[2]} duration {parts[4]} [{names}]";
        }

        private static readonly Guid MAJOR = new("48eba18e-f8c9-4687-bf11-0a74c9f96a8f"), SUB = new("f7e34c9a-42e8-4714-b74b-cb29d72c35e5");
        private static readonly Guid VIDEO = new("73646976-0000-0010-8000-00aa00389b71"), NV12 = new("3231564e-0000-0010-8000-00aa00389b71"), H264 = new("34363248-0000-0010-8000-00aa00389b71");
        private static readonly Guid FRAME_SIZE = new("1652c33d-d6b2-4012-b834-72030849a37d"), FRAME_RATE = new("c459a2e8-3d2c-4e44-b132-fee5156c7bb0");
        private static unsafe int Call(IntPtr t, int slot, IntPtr type) => ((delegate* unmanaged<IntPtr, uint, IntPtr, uint, int>)MFReader.Fn(t, slot))(t, 0, type, 0);

        /// <summary>Of an MFT, the next output into a sample of the size given: the HRESULT and the sample (null without one).</summary>
        private static unsafe (int Hr, IntPtr Sample) Out(IntPtr t, int size)
        {
            // a transform that provides its samples is given none
            var info = stackalloc uint[3];
            ((delegate* unmanaged<IntPtr, uint, uint*, int>)MFReader.Fn(t, 7))(t, 0, info);
            var s = (info[0] & 0x100) != 0 ? IntPtr.Zero : MFWriter.Sample(new byte[Math.Max(size, (int)info[1])], 0, 0);
            var odb = stackalloc byte[32];
            new Span<byte>(odb, 32).Clear();
            *(IntPtr*)(odb + IntPtr.Size) = s;
            uint status;
            int hr = ((delegate* unmanaged<IntPtr, uint, uint, byte*, uint*, int>)MFReader.Fn(t, 25))(t, 0, 1, odb, &status);
            if (hr < 0) { MFReader.Release(s); return (hr, IntPtr.Zero); }
            return (hr, *(IntPtr*)(odb + IntPtr.Size));
        }

        /// <summary>
        /// Ten frames through the H.264 encoder MFT (Main profile, as by default) and back through the H.264 decoder MFT: ten
        /// frames out, in order, each the picture that went in (luma within 30 dB of it).
        /// </summary>
        [Fact]
        public unsafe void TheH264Encoder_EncodesWhatTheDecoderDecodesBack()
        {
            const int W = 320, H = 240, N = 10;
            var enc = MFEncoderProbe.Encoder(MFEncoderProbe.VideoEncoderCategory, VIDEO, NV12, H264);
            Assert.SkipWhen(enc == IntPtr.Zero, "no H.264 encoder here");
            var units = new List<(byte[] Bytes, long Time)>();
            try
            {
                var o = MFReader.MediaType();
                MFReader.SetGuid(o, MAJOR, VIDEO); MFReader.SetGuid(o, SUB, H264);
                MFReader.SetU64(o, FRAME_SIZE, Microsoft.Wpf.Interop.WebGpu.MediaFoundation.MF.Pack(W, H)); MFReader.SetU64(o, FRAME_RATE, Microsoft.Wpf.Interop.WebGpu.MediaFoundation.MF.Pack(30, 1));
                MFReader.SetU32(o, new Guid("20332624-fb0d-4d9e-bd0d-cbf6786c102e"), 2_000_000); MFReader.SetU32(o, new Guid("e2724bb8-e676-4806-b4b2-a8d6efb44ccd"), 2);
                Assert.Equal(0, Call(enc, 16, o));
                MFReader.Release(o);
                IntPtr it; Assert.Equal(0, ((delegate* unmanaged<IntPtr, uint, uint, IntPtr*, int>)MFReader.Fn(enc, 13))(enc, 0, 2, &it));
                Assert.Equal(0, Call(enc, 15, it));
                MFReader.Release(it);
                ((delegate* unmanaged<IntPtr, uint, nuint, int>)MFReader.Fn(enc, 23))(enc, 0x10000000, 0);         // MFT_MESSAGE_NOTIFY_BEGIN_STREAMING
                ((delegate* unmanaged<IntPtr, uint, nuint, int>)MFReader.Fn(enc, 23))(enc, 0x10000003, 0);         // MFT_MESSAGE_NOTIFY_START_OF_STREAM
                void Drain()
                {
                    while (true)
                    {
                        var (hr, s) = Out(enc, 200_000);
                        if (hr < 0) { Assert.Equal(unchecked((int)0xC00D6D72), hr); break; }
                        long t; ((delegate* unmanaged<IntPtr, long*, int>)MFReader.Fn(s, 35))(s, &t);
                        units.Add((MFReader.SampleBytes(s, out _), t));
                        MFReader.Release(s);
                    }
                }
                for (int k = 0; k < N; k++)
                {
                    var s = MFWriter.Sample(MFDecoderTests.Picture(W, H, k), k * 333_333L, 333_333);
                    Assert.Equal(0, ((delegate* unmanaged<IntPtr, uint, IntPtr, uint, int>)MFReader.Fn(enc, 24))(enc, 0, s, 0));
                    MFReader.Release(s);
                    Drain();
                }
                ((delegate* unmanaged<IntPtr, uint, nuint, int>)MFReader.Fn(enc, 23))(enc, 1, 0);
                Drain();
            }
            finally { MFReader.Release(enc); }
            Assert.True(units.Count == N, $"encoded {units.Count} of {N}");
            // (on Windows the encoding is Windows' own; its H.264 decoder MFT, made on its own, decodes nothing in the test VM --
            // every input taken, E_FAIL at the drain -- though its source reader decodes: the decoding is checked off Windows)
            if (OperatingSystem.IsWindows()) return;

            var dec = MFDecoderTests.Decoder();
            var frames = new List<(byte[] Nv12, long Time)>();
            var trace = new System.Text.StringBuilder();
            try
            {
                var i = MFReader.MediaType();
                MFReader.SetGuid(i, MAJOR, VIDEO); MFReader.SetGuid(i, SUB, H264);
                MFReader.SetU64(i, FRAME_SIZE, Microsoft.Wpf.Interop.WebGpu.MediaFoundation.MF.Pack(W, H)); MFReader.SetU64(i, FRAME_RATE, Microsoft.Wpf.Interop.WebGpu.MediaFoundation.MF.Pack(30, 1));
                MFReader.SetU32(i, new Guid("e2724bb8-e676-4806-b4b2-a8d6efb44ccd"), 2); MFReader.SetU64(i, new Guid("c6376a1e-8d0a-4027-be45-6d9a0ad39bb6"), Microsoft.Wpf.Interop.WebGpu.MediaFoundation.MF.Pack(1, 1));
                Assert.Equal(0, Call(dec, 15, i));
                MFReader.Release(i);
                void SetNv12()
                {
                    for (uint n = 0; ; n++)
                    {
                        IntPtr t; Assert.Equal(0, ((delegate* unmanaged<IntPtr, uint, uint, IntPtr*, int>)MFReader.Fn(dec, 14))(dec, 0, n, &t));
                        bool nv12 = MFReader.G(t, SUB) == NV12;
                        if (nv12) Assert.Equal(0, Call(dec, 16, t));
                        MFReader.Release(t);
                        if (nv12) return;
                    }
                }
                SetNv12();
                ((delegate* unmanaged<IntPtr, uint, nuint, int>)MFReader.Fn(dec, 23))(dec, 0x10000000, 0);
                ((delegate* unmanaged<IntPtr, uint, nuint, int>)MFReader.Fn(dec, 23))(dec, 0x10000003, 0);
                void Take()
                {
                    while (true)
                    {
                        var (hr, s) = Out(dec, W * H * 3 / 2 + 65536);
                        if (hr == unchecked((int)0xC00D6D61)) { SetNv12(); continue; }             // MF_E_TRANSFORM_STREAM_CHANGE
                        var oi = stackalloc uint[3];
                        ((delegate* unmanaged<IntPtr, uint, uint*, int>)MFReader.Fn(dec, 7))(dec, 0, oi);
                        trace.Append($" out 0x{hr:x8} (info 0x{oi[0]:x} {oi[1]})");
                        if (hr < 0) { Assert.True(hr == unchecked((int)0xC00D6D72), $"the decoder's ProcessOutput: 0x{hr:x8};{trace}"); break; }
                        long t; ((delegate* unmanaged<IntPtr, long*, int>)MFReader.Fn(s, 35))(s, &t);
                        frames.Add((MFReader.SampleBytes(s, out _), t));
                        MFReader.Release(s);
                    }
                }
                foreach (var (bytes, time) in units)
                {
                    var s = MFWriter.Sample(bytes, time, 333_333);
                    int hr;
                    while ((hr = ((delegate* unmanaged<IntPtr, uint, IntPtr, uint, int>)MFReader.Fn(dec, 24))(dec, 0, s, 0)) == unchecked((int)0xC00D36B5)) Take();
                    trace.Append($" in {bytes.Length} 0x{hr:x8}");
                    Assert.Equal(0, hr);
                    MFReader.Release(s);
                    Take();
                }
                ((delegate* unmanaged<IntPtr, uint, nuint, int>)MFReader.Fn(dec, 23))(dec, 1, 0);
                Take();
            }
            finally { MFReader.Release(dec); }
            Assert.True(frames.Count == N, $"decoded {frames.Count} of {N}");
            var ordered = frames.OrderBy(f => f.Time).ToList();
            for (int k = 0; k < N; k++)
            {
                var want = MFDecoderTests.Picture(W, H, k);
                double se = 0;
                for (int p = 0; p < W * H; p++) { double d = ordered[k].Nv12[p] - want[p]; se += d * d; }
                double psnr = 10 * Math.Log10(255.0 * 255 / Math.Max(1e-9, se / (W * H)));
                Assert.True(psnr > 30, $"frame {k}: luma PSNR {psnr:F1} dB");
            }
        }

        [Fact]
        public void TheEncoders_DoWhatWindowsEncodersDo()
        {
            var windows = File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "MediaFoundation", "Encoder.windows.txt")).ToList();
            var ours = MFEncoderProbe.Details().Concat(MFEncoderProbe.Dumps()).ToList();
            // a system without an encoder of its own lists none, as Windows would without one
            Assert.SkipWhen(ours.Any(l => l.StartsWith("==== H.264 encoder none")) && !OperatingSystem.IsWindows(), "no H.264 encoder here");

            int w = 0, o = 0;
            string section = "";
            while (w < windows.Count || o < ours.Count)
            {
                string? a = w < windows.Count ? windows[w] : null, b = o < ours.Count ? ours[o] : null;
                if (a != null && a.StartsWith("==== ")) section = a;
                bool video = section.StartsWith("==== H.264");
                // a run of outputs: compared as a run
                if (a != null && IsOutput(a) || b != null && IsOutput(b))
                {
                    var wa = new List<string>(); var ob = new List<string>();
                    while (w < windows.Count && IsOutput(windows[w])) wa.Add(windows[w++]);
                    while (o < ours.Count && IsOutput(ours[o])) ob.Add(ours[o++]);
                    string after = w < windows.Count ? windows[w] : "";
                    bool drain = after.Contains("output 0xc00d6d72") && w >= 1 && windows.Take(w).LastOrDefault(l => !IsOutput(l))?.Contains("drain") == true;
                    if (video)
                    {
                        // the same count (the latency Windows' encoder has), each of the same duration and attributes
                        Assert.True(wa.Count == ob.Count, $"{section}: outputs before line {w}: Windows {wa.Count}, ours {ob.Count}\n{string.Join("\n", ob)}");
                        for (int i = 0; i < wa.Count; i++) Assert.Equal(Shape(wa[i], true), Shape(ob[i], true));
                    }
                    else
                    {
                        // AAC: the same packets at the same times; at the drain the system's priming may make up to two more
                        Assert.True(ob.Count >= wa.Count && ob.Count <= wa.Count + (drain ? 2 : 0), $"{section}: outputs before line {w}: Windows {wa.Count}, ours {ob.Count}");
                        for (int i = 0; i < wa.Count; i++) Assert.Equal(Shape(wa[i], false), Shape(ob[i], false));
                    }
                    continue;
                }
                Assert.True(a != null && b != null, $"line {w}/{o}: Windows '{a ?? "(none)"}', ours '{b ?? "(none)"}'");
                // the parameter sets are the encoder's own
                if (a!.Contains("MF_MT_MPEG_SEQUENCE_HEADER") && b!.Contains("MF_MT_MPEG_SEQUENCE_HEADER")) { w++; o++; continue; }
                Assert.True(a == b, $"line {w}: Windows '{a}', ours '{b}'");
                w++; o++;
            }
        }
    }
}
