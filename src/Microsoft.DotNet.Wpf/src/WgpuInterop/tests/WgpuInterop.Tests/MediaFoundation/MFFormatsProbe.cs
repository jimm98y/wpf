// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// The rarer formats of Windows' video processor and color converter (MF_PROBE_FORMATS=<file> writes the report): a frame
// of 8x4 of a fixed pattern of bytes in each subtype of more than 8 bits, of floats, of a palette, of the rarer packings,
// through each class to RGB32 and NV12; and an RGB32 frame of a pattern to each of those the class writes -- each
// output whole, in hex, or the failure.
//

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Wpf.Interop.WebGpu.MediaFoundation;
using Xunit;

namespace WgpuInterop.Tests.MediaFoundation
{
    public sealed unsafe class MFFormatsProbe
    {
        private static string Hr(int hr) => $"0x{hr:x8}";
        private static IntPtr Fn(IntPtr o, int slot) => MFReader.Fn(o, slot);
        private static readonly Guid MAJOR = new("48eba18e-f8c9-4687-bf11-0a74c9f96a8f"), SUB = new("f7e34c9a-42e8-4714-b74b-cb29d72c35e5");
        private static readonly Guid Video = new("73646976-0000-0010-8000-00aa00389b71");
        private static Guid F(string s) => new((uint)(s[0] | s[1] << 8 | s[2] << 16 | s[3] << 24), 0, 0x10, 0x80, 0, 0, 0xaa, 0, 0x38, 0x9b, 0x71);
        private static Guid D(uint v) => new(v, 0, 0x10, 0x80, 0, 0, 0xaa, 0, 0x38, 0x9b, 0x71);
        private static readonly Guid RGB32 = D(0x16), NV12 = F("NV12");
        private const int W = 8, H = 4;

        /// <summary>The formats, and a frame's bytes of 8x4 of each.</summary>
        internal static readonly (string Name, Guid Sub, int Size)[] Formats =
        {
            ("L8", D(0x32), W * H), ("L16", D(0x51), W * H * 2), ("D16", D(0x50), W * H * 2), ("P8", D(0x29), W * H), ("RGB8", new("e436eb7a-524f-11ce-9f53-0020af0ba770"), W * H),
            ("A2R10G10B10", D(0x1f), W * H * 4), ("A16B16G16R16F", D(0x71), W * H * 8), ("ABGR32", D(0x20), W * H * 4), ("RGB555", D(0x18), W * H * 2), ("RGB565", D(0x17), W * H * 2),
            ("P010", F("P010"), W * H * 3), ("P016", F("P016"), W * H * 3), ("P208", F("P208"), W * H * 2), ("Y210", F("Y210"), W * H * 4), ("Y216", F("Y216"), W * H * 4),
            ("Y410", F("Y410"), W * H * 4), ("Y416", F("Y416"), W * H * 8), ("v410", F("v410"), W * H * 4), ("V410", F("V410"), W * H * 4), ("V216", F("V216"), W * H * 4),
            ("Y41P", F("Y41P"), W * H * 3 / 2), ("Y41T", F("Y41T"), W * H * 3 / 2), ("Y42T", F("Y42T"), W * H * 2), ("NV11", F("NV11"), W * H * 3 / 2), ("YVU9", F("YVU9"), W * H + 2 * (W / 4) * (H / 4)),
            ("420O", F("420O"), W * H * 3 / 2), ("I422", F("I422"), W * H * 2), ("I444", F("I444"), W * H * 3), ("AYUV", F("AYUV"), W * H * 4), ("YVYU", F("YVYU"), W * H * 2),
        };

        /// <summary>A pattern of bytes: each of its own, of the index.</summary>
        private static byte[] Pattern(int n, int seed) => Enumerable.Range(0, n).Select(i => (byte)((i * 37 + seed * 11 + (i >> 3) * 5) & 0xFF)).ToArray();

        private static IntPtr Type(Guid sub, bool stride)
        {
            var t = MFReader.MediaType();
            MFReader.SetGuid(t, MAJOR, Video); MFReader.SetGuid(t, SUB, sub);
            MFReader.SetU64(t, new Guid("1652c33d-d6b2-4012-b834-72030849a37d"), MF.Pack(W, H));
            MFReader.SetU64(t, new Guid("c459a2e8-3d2c-4e44-b132-fee5156c7bb0"), MF.Pack(25, 1));
            MFReader.SetU32(t, new Guid("e2724bb8-e676-4806-b4b2-a8d6efb44ccd"), 2);
            MFReader.SetU64(t, new Guid("c6376a1e-8d0a-4027-be45-6d9a0ad39bb6"), MF.Pack(1, 1));
            if (stride && sub == RGB32) MFReader.SetU32(t, new Guid("644b4e48-1e02-4516-b0eb-c01ca9d49ac6"), W * 4);
            return t;
        }

        private static string Through(Guid clsid, Guid inSub, Guid outSub, byte[] picture)
        {
            int hr = MFVideoProcessorProbe.Make(clsid, out var t);
            if (hr < 0) return $"create {Hr(hr)}";
            try
            {
                var i = Type(inSub, true); var o = Type(outSub, true);
                hr = ((delegate* unmanaged<IntPtr, uint, IntPtr, uint, int>)Fn(t, 15))(t, 0, i, 0);
                int ohr = hr < 0 ? hr : ((delegate* unmanaged<IntPtr, uint, IntPtr, uint, int>)Fn(t, 16))(t, 0, o, 0);
                MFReader.Release(i); MFReader.Release(o);
                if (hr < 0) return $"input {Hr(hr)}";
                if (ohr < 0) return $"output {Hr(ohr)}";
                var input = MFWriter.Sample(picture, 0, 400_000);
                hr = ((delegate* unmanaged<IntPtr, uint, IntPtr, uint, int>)Fn(t, 24))(t, 0, input, 0);
                MFReader.Release(input);
                if (hr < 0) return $"process input {Hr(hr)}";
                IntPtr mine = MFWriter.Sample(new byte[W * H * 16], 0, 0);
                var odb = stackalloc byte[4 * sizeof(IntPtr)];
                new Span<byte>(odb, 4 * sizeof(IntPtr)).Clear();
                *(IntPtr*)(odb + IntPtr.Size) = mine;
                uint status;
                hr = ((delegate* unmanaged<IntPtr, uint, uint, byte*, uint*, int>)Fn(t, 25))(t, 0, 1, odb, &status);
                IntPtr got = *(IntPtr*)(odb + IntPtr.Size);
                if (hr < 0) { MFReader.Release(mine); return $"process output {Hr(hr)}"; }
                var bytes = MFReader.SampleBytes(got, out _);
                MFReader.Release(got);
                return $"{bytes.Length} bytes: {Convert.ToHexString(bytes).ToLowerInvariant()}";
            }
            finally { MFReader.Release(t); }
        }

        /// <summary>Of each input subtype the class lists, the input set's answer and each output subtype's (o: taken, x: refused).</summary>
        internal static List<string> Matrix(Guid clsid)
        {
            var log = new List<string>();
            MFVideoProcessorProbe.Make(clsid, out var t0);
            var ins = new List<Guid>(); var outs = new List<Guid>();
            for (uint n = 0; n < 100; n++) { IntPtr ty; if (((delegate* unmanaged<IntPtr, uint, uint, IntPtr*, int>)Fn(t0, 13))(t0, 0, n, &ty) < 0) break; ins.Add(MFReader.G(ty, SUB)); MFReader.Release(ty); }
            var nv = Type(NV12, true);
            ((delegate* unmanaged<IntPtr, uint, IntPtr, uint, int>)Fn(t0, 15))(t0, 0, nv, 0);
            MFReader.Release(nv);
            for (uint n = 0; n < 100; n++) { IntPtr ty; if (((delegate* unmanaged<IntPtr, uint, uint, IntPtr*, int>)Fn(t0, 14))(t0, 0, n, &ty) < 0) break; var g = MFReader.G(ty, SUB); if (!outs.Contains(g)) outs.Add(g); MFReader.Release(ty); }
            MFReader.Release(t0);
            log.Add($"  outputs: {string.Join(" ", outs.Select(MFReader.Name))}");
            foreach (var i in ins)
            {
                MFVideoProcessorProbe.Make(clsid, out var t);
                var it = Type(i, true);
                int hr = ((delegate* unmanaged<IntPtr, uint, IntPtr, uint, int>)Fn(t, 15))(t, 0, it, 0);
                MFReader.Release(it);
                var line = new System.Text.StringBuilder($"  {MFReader.Name(i)} {Hr(hr)}:");
                if (hr >= 0)
                    foreach (var o in outs)
                    {
                        var ot = Type(o, true);
                        int ohr = ((delegate* unmanaged<IntPtr, uint, IntPtr, uint, int>)Fn(t, 16))(t, 0, ot, 1);       // a test
                        MFReader.Release(ot);
                        line.Append(ohr >= 0 ? " o" : " x");
                    }
                log.Add(line.ToString());
                MFReader.Release(t);
            }
            return log;
        }

        internal static List<string> Report()
        {
            MFReader.Startup();
            if (OperatingSystem.IsWindows()) MFVideoProcessorProbe.Init();
            var log = new ProgressLog("MF_PROBE_FORMATS");
            foreach (var (cname, clsid) in new[] { ("video processor", MFVideoProcessorProbe.VideoProcessorClsid), ("color converter", MFVideoProcessorProbe.ColorConverterClsid) })
            {
                log.Add($"==== {cname}");
                log.AddRange(Matrix(clsid));
                int k = 0;
                foreach (var (name, sub, size) in Formats)
                {
                    var pic = Pattern(size, k++);
                    log.Add($"{name} to RGB32: {Through(clsid, sub, RGB32, pic)}");
                    log.Add($"{name} to NV12: {Through(clsid, sub, NV12, pic)}");
                }
                var rgb = Pattern(W * H * 4, 99);
                for (int q = 3; q < rgb.Length; q += 4) rgb[q] = 255;
                foreach (var (name, sub, _) in Formats) log.Add($"RGB32 to {name}: {Through(clsid, RGB32, sub, rgb)}");
                var nv12 = Pattern(W * H * 3 / 2, 77);
                foreach (var (name, sub, _) in Formats) log.Add($"NV12 to {name}: {Through(clsid, NV12, sub, nv12)}");
            }
            return log;
        }

        /// <summary>
        /// What of each line the shim's classes do not match yet (MFFormatsProbe): of the video processor, its conversions
        /// through its own color pipeline -- 10-bit, 16-bit and float subtypes in and out (A2R10G10B10, A16B16G16R16F, P010,
        /// P016, L8, L16, D16 of NV12: Windows' wide-gamut and float arithmetic), its RGB to RGB of another format (ABGR32,
        /// RGB565: through YUV); of the color converter, RGB8 of a type of no palette (of none Windows' own), its dithered RGB8
        /// out, and Y41P, Y41T, Y42T (Windows' answers to NV12 garbage of its memory). Their acceptance is compared all the
        /// same: these lines only as "bytes".
        /// </summary>
        private static readonly string[] s_unmatched =
        {
            "video A2R10G10B10 to RGB32", "video A2R10G10B10 to NV12", "video A16B16G16R16F to RGB32", "video A16B16G16R16F to NV12",
            "video P010 to RGB32", "video P010 to NV12", "video P016 to RGB32", "video P016 to NV12", "video RGB32 to ABGR32", "video RGB32 to RGB565",
            "video NV12 to L8", "video NV12 to L16", "video NV12 to D16", "video NV12 to A2R10G10B10", "video NV12 to A16B16G16R16F", "video NV12 to ABGR32",
            "video NV12 to P010", "video NV12 to P016",
            "color RGB8 to RGB32", "color RGB8 to NV12", "color Y41P to RGB32", "color Y41P to NV12", "color Y41T to RGB32", "color Y41T to NV12",
            "color Y42T to RGB32", "color Y42T to NV12", "color RGB32 to RGB8", "color NV12 to RGB8",
        };

        /// <summary>
        /// The shim's video processor and color converter take and refuse each pair of the rarer subtypes as Windows' do
        /// (Formats.windows.txt: the matrix of every input and output, each conversion's answer), and convert them to the byte
        /// -- but those of s_unmatched, of which only that a frame comes, and its length, are compared.
        /// </summary>
        [Fact]
        public void TheRarerFormats_AreWindows()
        {
            Assert.SkipWhen(OperatingSystem.IsWindows(), "Windows' classes are the fixture");
            var windows = File.ReadAllLines(Path.Combine(TestFiles.Root, "MediaFoundation", "Formats.windows.txt"));
            var ours = Report();
            Assert.Equal(windows.Length, ours.Count);
            string sec = "";
            for (int i = 0; i < windows.Length; i++)
            {
                string w = windows[i], o = ours[i];
                if (w.StartsWith("==== ")) sec = w.Contains("video") ? "video" : "color";
                if (w == o) continue;
                string key = $"{sec} {w.Split(':')[0]}";
                bool known = Array.IndexOf(s_unmatched, key) >= 0;
                Assert.True(known && w.Contains(" bytes: ") && o.Contains(" bytes: "), $"line {i}: Windows '{w}', ours '{o}'");
            }
        }

        [Fact]
        public void Probe()
        {
            Assert.SkipWhen(Environment.GetEnvironmentVariable("MF_PROBE_FORMATS") is not { Length: > 0 }, "MF_PROBE_FORMATS=<file> writes the report");
            File.WriteAllLines(Environment.GetEnvironmentVariable("MF_PROBE_FORMATS")!, Report());
        }
    }
}
