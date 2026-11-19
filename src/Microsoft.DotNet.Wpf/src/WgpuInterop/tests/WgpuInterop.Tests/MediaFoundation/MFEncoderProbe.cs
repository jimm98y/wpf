// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// What the encoder MFTs do (MF_PROBE_ENC=<file> writes the report): MFTEnumEx's H.264 and AAC encoders, their
// types before and after the other side is set, and what three frames (or 0.1 s of PCM) encode to.
//

using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using Microsoft.Wpf.Interop.WebGpu.MediaFoundation;
using Xunit;

namespace WgpuInterop.Tests.MediaFoundation
{
    public sealed unsafe class MFEncoderProbe
    {
        [DllImport("mfplat.dll", EntryPoint = "MFTEnumEx")] private static extern int WinMFTEnumEx(Guid category, uint flags, MFT_REGISTER_TYPE_INFO* input, MFT_REGISTER_TYPE_INFO* output, IntPtr** activates, uint* count);
        private static readonly Guid MAJOR = new("48eba18e-f8c9-4687-bf11-0a74c9f96a8f"), SUB = new("f7e34c9a-42e8-4714-b74b-cb29d72c35e5");
        private static string Hr(int hr) => $"0x{hr:x8}";

        internal static IntPtr Encoder(Guid category, Guid major, Guid input, Guid output)
        {
            MFReader.Startup();
            var i = new MFT_REGISTER_TYPE_INFO { guidMajorType = major, guidSubtype = input };
            var o = new MFT_REGISTER_TYPE_INFO { guidMajorType = major, guidSubtype = output };
            IntPtr* acts; uint n;
            int hr = OperatingSystem.IsWindows() ? WinMFTEnumEx(category, 1 | 0x20 | 0x40, &i, &o, &acts, &n) : MFExports.MFTEnumEx(category, 1 | 0x20 | 0x40, &i, &o, &acts, &n);
            if (hr < 0 || n == 0) return IntPtr.Zero;
            IntPtr a = acts[0];
            for (uint k = 1; k < n; k++) MFReader.Release(acts[k]);
            Marshal.FreeCoTaskMem((IntPtr)acts);
            IntPtr t; var iid = new Guid("bf94c121-5b05-4e6f-8000-ba598961414d");
            hr = ((delegate* unmanaged<IntPtr, Guid*, IntPtr*, int>)MFReader.Fn(a, 33))(a, &iid, &t);
            MFReader.Release(a);
            return hr >= 0 ? t : IntPtr.Zero;
        }

        private static List<string> Types(IntPtr t, int slot, string what)
        {
            var log = new List<string>();
            for (uint n = 0; n < 200; n++)
            {
                IntPtr type;
                int hr = ((delegate* unmanaged<IntPtr, uint, uint, IntPtr*, int>)MFReader.Fn(t, slot))(t, 0, n, &type);
                if (hr < 0) { log.Add($"  {what} {n}: {Hr(hr)}"); break; }
                if (n >= 8 && n % 8 != 0) { MFReader.Release(type); continue; }
                if (n < 3) { log.Add($"  {what} {n}:"); log.AddRange(MFReader.Dump(type).Select(l => "    " + l)); }
                else log.Add($"  {what} {n}: {MFReader.Name(MFReader.G(type, SUB))} {string.Join(" ", MFReader.Dump(type).Where(l => l.Contains("SAMPLES_PER_SECOND") || l.Contains("NUM_CHANNELS") || l.Contains("AVG_BYTES")).Select(l => l.Split(' ').Last()))}");
                MFReader.Release(type);
            }
            return log;
        }

        private static (int Hr, uint Status, IntPtr Sample) Output(IntPtr t, uint size)
        {
            var info = stackalloc uint[3];
            ((delegate* unmanaged<IntPtr, uint, uint*, int>)MFReader.Fn(t, 7))(t, 0, info);
            IntPtr mine = IntPtr.Zero;
            if ((info[0] & 0x100) == 0) mine = MFWriter.Sample(new byte[Math.Max(size, info[1])], 0, 0);
            var odb = stackalloc byte[32];
            new Span<byte>(odb, 32).Clear();
            *(IntPtr*)(odb + 8) = mine;
            uint status;
            int hr = ((delegate* unmanaged<IntPtr, uint, uint, byte*, uint*, int>)MFReader.Fn(t, 25))(t, 0, 1, odb, &status);
            IntPtr got = *(IntPtr*)(odb + 8);
            if (hr < 0) { MFReader.Release(mine); return (hr, status, IntPtr.Zero); }
            return (hr, *(uint*)(odb + 16), got);
        }

        private static void Drain(IntPtr t, List<string> log, uint size, bool video)
        {
            for (int n = 0; n < 60; n++)
            {
                var (hr, st, s) = Output(t, size);
                if (hr < 0) { log.Add($"  output {Hr(hr)}"); break; }
                if (s == IntPtr.Zero) { log.Add($"  output: status 0x{st:x} no sample"); continue; }
                var b = MFReader.SampleBytes(s, out _);
                long time; ((delegate* unmanaged<IntPtr, long*, int>)MFReader.Fn(s, 35))(s, &time);
                string nals = video ? string.Join(",", Nals(b)) : "";
                log.Add($"  output: status 0x{st:x} time {time} duration {MFReader.Duration(s)} bytes {b.Length} {MFReader.HexOf(b, 8)} {nals}");
                if (n == 0) log.AddRange(MFReader.Dump(s).Select(l => "      " + l));
                MFReader.Release(s);
            }
        }

        private static IEnumerable<int> Nals(byte[] b)
        {
            for (int i = 0; i + 4 < b.Length; i++)
                if (b[i] == 0 && b[i + 1] == 0 && (b[i + 2] == 1 || b[i + 2] == 0 && b[i + 3] == 1)) { int at = b[i + 2] == 1 ? i + 3 : i + 4; yield return b[at] & 0x1F; i = at; }
        }

        internal static List<string> Report()
        {
            var log = new List<string>();
            var video = new Guid("73646976-0000-0010-8000-00aa00389b71"); var audio = new Guid("73647561-0000-0010-8000-00aa00389b71");
            var nv12 = new Guid("3231564e-0000-0010-8000-00aa00389b71"); var h264 = new Guid("34363248-0000-0010-8000-00aa00389b71");
            var pcm = new Guid("00000001-0000-0010-8000-00aa00389b71"); var aac = new Guid("00001610-0000-0010-8000-00aa00389b71");
            IntPtr t = Encoder(new Guid("f79eac7d-e545-4387-bdee-d647d7bde42a"), video, nv12, h264);
            log.Add($"==== H.264 encoder {(t != IntPtr.Zero ? "found" : "none")}");
            if (t != IntPtr.Zero)
            {
                log.AddRange(Types(t, 14, "output available"));
                log.AddRange(Types(t, 13, "input available, before"));
                var o = MFReader.MediaType();
                MFReader.SetGuid(o, MAJOR, video); MFReader.SetGuid(o, SUB, h264);
                MFReader.SetU64(o, new Guid("1652c33d-d6b2-4012-b834-72030849a37d"), MF.Pack(128, 96));
                MFReader.SetU64(o, new Guid("c459a2e8-3d2c-4e44-b132-fee5156c7bb0"), MF.Pack(25, 1));
                MFReader.SetU32(o, new Guid("20332624-fb0d-4d9e-bd0d-cbf6786c102e"), 500_000);
                MFReader.SetU32(o, new Guid("e2724bb8-e676-4806-b4b2-a8d6efb44ccd"), 2);
                log.Add($"  set output {Hr(((delegate* unmanaged<IntPtr, uint, IntPtr, uint, int>)MFReader.Fn(t, 16))(t, 0, o, 0))}");
                MFReader.Release(o);
                log.AddRange(Types(t, 13, "input available"));
                IntPtr it = IntPtr.Zero; ((delegate* unmanaged<IntPtr, uint, uint, IntPtr*, int>)MFReader.Fn(t, 13))(t, 0, 2, &it);
                log.Add($"  set input (available 2) {Hr(((delegate* unmanaged<IntPtr, uint, IntPtr, uint, int>)MFReader.Fn(t, 15))(t, 0, it, 0))}");
                MFReader.Release(it);
                var inInfo = stackalloc uint[6];
                log.Add($"  input info {Hr(((delegate* unmanaged<IntPtr, uint, uint*, int>)MFReader.Fn(t, 6))(t, 0, inInfo))} flags 0x{inInfo[2]:x} size {inInfo[3]}");
                var outInfo = stackalloc uint[3];
                log.Add($"  output info {Hr(((delegate* unmanaged<IntPtr, uint, uint*, int>)MFReader.Fn(t, 7))(t, 0, outInfo))} flags 0x{outInfo[0]:x} size {outInfo[1]}");
                ((delegate* unmanaged<IntPtr, uint, nuint, int>)MFReader.Fn(t, 23))(t, 0x10000000, 0);
                ((delegate* unmanaged<IntPtr, uint, nuint, int>)MFReader.Fn(t, 23))(t, 0x10000003, 0);
                for (int k = 0; k < 3; k++)
                {
                    var s = MFWriter.Sample(MFDecoderTests.Picture(128, 96, k), k * 400_000L, 400_000);
                    int hr = ((delegate* unmanaged<IntPtr, uint, IntPtr, uint, int>)MFReader.Fn(t, 24))(t, 0, s, 0);
                    MFReader.Release(s);
                    log.Add($"  input {k} {Hr(hr)}");
                    Drain(t, log, 18432, true);
                }
                log.Add($"  drain {Hr(((delegate* unmanaged<IntPtr, uint, nuint, int>)MFReader.Fn(t, 23))(t, 1, 0))}");
                Drain(t, log, 18432, true);
                IntPtr ct = IntPtr.Zero; ((delegate* unmanaged<IntPtr, uint, IntPtr*, int>)MFReader.Fn(t, 18))(t, 0, &ct);
                log.Add("  output current:"); log.AddRange(MFReader.Dump(ct).Select(l => "    " + l)); MFReader.Release(ct);
                MFReader.Release(t);
            }
            t = Encoder(new Guid("91c64bd0-f91e-4d8c-9276-db248279d975"), audio, pcm, aac);
            log.Add($"==== AAC encoder {(t != IntPtr.Zero ? "found" : "none")}");
            if (t != IntPtr.Zero)
            {
                log.AddRange(Types(t, 14, "output available, before"));
                var i = MFReader.MediaType();
                MFReader.SetGuid(i, MAJOR, audio); MFReader.SetGuid(i, SUB, pcm);
                MFReader.SetU32(i, new Guid("37e48bf5-645e-4c5b-89de-ada9e29b696a"), 2); MFReader.SetU32(i, new Guid("5faeeae7-0290-4c31-9e8a-c534f68d9dba"), 44100);
                MFReader.SetU32(i, new Guid("f2deb57f-40fa-4764-aa33-ed4f2d1ff669"), 16); MFReader.SetU32(i, new Guid("322de230-9eeb-43bd-ab7a-ff412251541d"), 4);
                MFReader.SetU32(i, new Guid("1aab75c8-cfef-451c-ab95-ac034b8e1731"), 176400);
                log.Add($"  set input {Hr(((delegate* unmanaged<IntPtr, uint, IntPtr, uint, int>)MFReader.Fn(t, 15))(t, 0, i, 0))}");
                MFReader.Release(i);
                log.AddRange(Types(t, 14, "output available"));
                // the stereo 44.1 kHz type of 16000 bytes a second
                for (uint n = 0; n < 200; n++)
                {
                    IntPtr ot = IntPtr.Zero;
                    if (((delegate* unmanaged<IntPtr, uint, uint, IntPtr*, int>)MFReader.Fn(t, 14))(t, 0, n, &ot) < 0) { log.Add($"  output types {n}"); break; }
                    if (MFReader.U32(ot, new Guid("37e48bf5-645e-4c5b-89de-ada9e29b696a")) == 2 && MFReader.U32(ot, new Guid("5faeeae7-0290-4c31-9e8a-c534f68d9dba")) == 44100 && MFReader.U32(ot, new Guid("1aab75c8-cfef-451c-ab95-ac034b8e1731")) == 16000)
                    {
                        log.Add($"  set output (available {n}) {Hr(((delegate* unmanaged<IntPtr, uint, IntPtr, uint, int>)MFReader.Fn(t, 16))(t, 0, ot, 0))}");
                        MFReader.Release(ot);
                        break;
                    }
                    MFReader.Release(ot);
                }
                var outInfo = stackalloc uint[3];
                log.Add($"  output info {Hr(((delegate* unmanaged<IntPtr, uint, uint*, int>)MFReader.Fn(t, 7))(t, 0, outInfo))} flags 0x{outInfo[0]:x} size {outInfo[1]}");
                var tone = System.IO.File.ReadAllBytes(MFSourceReaderProbe.AudioPath("tone.wav")).AsSpan(44, 4410 * 4).ToArray();
                var s = MFWriter.Sample(tone, 0, 1_000_000);
                log.Add($"  input {Hr(((delegate* unmanaged<IntPtr, uint, IntPtr, uint, int>)MFReader.Fn(t, 24))(t, 0, s, 0))}");
                MFReader.Release(s);
                Drain(t, log, 4096, false);
                log.Add($"  drain {Hr(((delegate* unmanaged<IntPtr, uint, nuint, int>)MFReader.Fn(t, 23))(t, 1, 0))}");
                Drain(t, log, 4096, false);
                IntPtr ct = IntPtr.Zero; ((delegate* unmanaged<IntPtr, uint, IntPtr*, int>)MFReader.Fn(t, 18))(t, 0, &ct);
                log.Add("  output current:"); log.AddRange(MFReader.Dump(ct).Select(l => "    " + l)); MFReader.Release(ct);
                MFReader.Release(t);
            }
            return log;
        }

        internal static readonly Guid VideoEncoderCategory = new("f79eac7d-e545-4387-bdee-d647d7bde42a");
        private static readonly Guid VIDEO = new("73646976-0000-0010-8000-00aa00389b71"), AUDIO = new("73647561-0000-0010-8000-00aa00389b71");
        private static readonly Guid NV12 = new("3231564e-0000-0010-8000-00aa00389b71"), H264 = new("34363248-0000-0010-8000-00aa00389b71");
        private static readonly Guid PCM = new("00000001-0000-0010-8000-00aa00389b71"), AAC = new("00001610-0000-0010-8000-00aa00389b71"), FLOAT = new("00000003-0000-0010-8000-00aa00389b71");
        private static readonly Guid FRAME_SIZE = new("1652c33d-d6b2-4012-b834-72030849a37d"), FRAME_RATE = new("c459a2e8-3d2c-4e44-b132-fee5156c7bb0");
        private static readonly Guid AVG_BITRATE = new("20332624-fb0d-4d9e-bd0d-cbf6786c102e"), INTERLACE = new("e2724bb8-e676-4806-b4b2-a8d6efb44ccd");
        private static readonly Guid CHANNELS = new("37e48bf5-645e-4c5b-89de-ada9e29b696a"), RATE = new("5faeeae7-0290-4c31-9e8a-c534f68d9dba"), BITS = new("f2deb57f-40fa-4764-aa33-ed4f2d1ff669");
        private static readonly Guid BLOCK = new("322de230-9eeb-43bd-ab7a-ff412251541d"), BYTES = new("1aab75c8-cfef-451c-ab95-ac034b8e1731"), PROFILE_LEVEL = new("7632f0e6-9538-4d61-acda-ea29c8c14456");
        private static readonly Guid PAYLOAD = new("bfbabe79-7434-4d1c-94f0-72a3b9e17188"), MPEG2_PROFILE = new("ad76a80b-2d5c-4e0b-b375-64e520137036");

        /// <summary>A type in a line: its subtype and its audio attributes (or its size), and how many attributes besides.</summary>
        private static string Compact(IntPtr t)
        {
            var d = MFReader.Dump(t);
            string sub = MFReader.Name(MFReader.G(t, SUB));
            if (MFReader.G(t, MAJOR) == AUDIO)
                return $"{sub} ch {MFReader.U32(t, CHANNELS)} rate {MFReader.U32(t, RATE)} bits {MFReader.U32(t, BITS)} bytes {MFReader.U32(t, BYTES)} block {MFReader.U32(t, BLOCK)} pl {MFReader.U32(t, PROFILE_LEVEL, 999)} payload {MFReader.U32(t, PAYLOAD, 999)} ({d.Count} attributes)";
            var (w, h) = MF.Unpack(MFReader.U64(t, FRAME_SIZE));
            return $"{sub} {w}x{h} ({d.Count} attributes)";
        }

        private static List<string> AllTypes(IntPtr t, int slot, string what)
        {
            var log = new List<string>();
            for (uint n = 0; n < 400; n++)
            {
                IntPtr type;
                int hr = ((delegate* unmanaged<IntPtr, uint, uint, IntPtr*, int>)MFReader.Fn(t, slot))(t, 0, n, &type);
                if (hr < 0) { log.Add($"  {what} {n}: {Hr(hr)}"); break; }
                log.Add($"  {what} {n}: {Compact(type)}");
                MFReader.Release(type);
            }
            return log;
        }

        private static int SetIn(IntPtr t, IntPtr type, uint flags = 0) => ((delegate* unmanaged<IntPtr, uint, IntPtr, uint, int>)MFReader.Fn(t, 15))(t, 0, type, flags);
        private static int SetOut(IntPtr t, IntPtr type, uint flags = 0) => ((delegate* unmanaged<IntPtr, uint, IntPtr, uint, int>)MFReader.Fn(t, 16))(t, 0, type, flags);
        private static int Message(IntPtr t, uint m, nuint p = 0) => ((delegate* unmanaged<IntPtr, uint, nuint, int>)MFReader.Fn(t, 23))(t, m, p);

        private static IntPtr Pcm(uint ch, uint rate, uint bits = 16, Guid? sub = null)
        {
            var i = MFReader.MediaType();
            MFReader.SetGuid(i, MAJOR, AUDIO); MFReader.SetGuid(i, SUB, sub ?? PCM);
            MFReader.SetU32(i, CHANNELS, ch); MFReader.SetU32(i, RATE, rate); MFReader.SetU32(i, BITS, bits);
            MFReader.SetU32(i, BLOCK, ch * bits / 8); MFReader.SetU32(i, BYTES, rate * ch * bits / 8);
            return i;
        }

        private static IntPtr H264Out(uint w, uint h, uint? profile = null, bool size = true, bool rate = true, bool bitrate = true, bool interlace = true)
        {
            var o = MFReader.MediaType();
            MFReader.SetGuid(o, MAJOR, VIDEO); MFReader.SetGuid(o, SUB, H264);
            if (size) MFReader.SetU64(o, FRAME_SIZE, MF.Pack(w, h));
            if (rate) MFReader.SetU64(o, FRAME_RATE, MF.Pack(30, 1));
            if (bitrate) MFReader.SetU32(o, AVG_BITRATE, 1_000_000);
            if (interlace) MFReader.SetU32(o, INTERLACE, 2);
            if (profile is uint p) MFReader.SetU32(o, MPEG2_PROFILE, p);
            return o;
        }

        /// <summary>The ICodecAPI properties the encoder says it supports, of those apps set most.</summary>
        private static List<string> CodecApi(IntPtr t)
        {
            var log = new List<string>();
            var iid = new Guid("901db4c7-31ce-41a2-85dc-8fa0bf41b8da");
            IntPtr api;
            int hr = ((delegate* unmanaged<IntPtr, Guid*, IntPtr*, int>)MFReader.Fn(t, 0))(t, &iid, &api);
            log.Add($"  ICodecAPI {Hr(hr)}");
            if (hr < 0) return log;
            var props = new (string, Guid)[]
            {
                ("CODECAPI_AVEncCommonRateControlMode", new("1c0608e9-370c-4710-8a58-cb6181c42423")), ("CODECAPI_AVEncCommonMeanBitRate", new("f7222374-2144-4815-b550-a37f8e12ee52")),
                ("CODECAPI_AVEncCommonQuality", new("fcbf57a3-7ea5-4b0c-9644-69b40c39c391")), ("CODECAPI_AVEncCommonQualityVsSpeed", new("98332df8-03cd-476b-89fa-3f9e442dec9f")),
                ("CODECAPI_AVEncMPVGOPSize", new("95f31b26-95a4-41aa-9303-246a7fc6eef1")), ("CODECAPI_AVEncMPVDefaultBPictureCount", new("8d390aac-dc5c-4200-b57f-814d04babab2")),
                ("CODECAPI_AVEncH264CABACEnable", new("ee6cad62-d305-4248-a50e-e1b255f7caf8")), ("CODECAPI_AVLowLatencyMode", new("9c27891a-ed7a-40e1-88e8-b22727a024ee")),
                ("CODECAPI_AVEncVideoForceKeyFrame", new("398c1b98-8353-475a-9ef2-8f265d260345")), ("CODECAPI_AVEncCommonBufferSize", new("0db96574-b6a4-4c8b-8106-3773de0310cd")),
                ("CODECAPI_AVEncCommonMaxBitRate", new("9651eae4-39b9-4ebf-85ef-d7f444ec7465")), ("CODECAPI_AVEncVideoEncodeQP", new("2cb5696b-23fb-4ce1-a0f9-ef5b90fd55ca")),
                ("CODECAPI_AVEncVideoMinQP", new("0ee22c6a-a37c-4568-b5f1-9d4c2b3ab886")), ("CODECAPI_AVEncVideoMaxQP", new("3daf6f66-a6a7-45e0-a8e5-f2743f46a3a2")),
                ("CODECAPI_AVEncNumWorkerThreads", new("b0c8bf60-16f7-4951-a30b-1db1609293d6")), ("CODECAPI_AVEncVideoTemporalLayerCount", new("19caebff-b74d-4cfd-8c27-c2f9d97d5f52")),
                ("CODECAPI_AVEncMPVProfile", new("dabb534a-1d99-4284-975a-d90e2239baa1")), ("CODECAPI_AVEncAdaptiveMode", new("4419b185-da1f-4f53-bc76-097d0c1efb1e")),
                ("CODECAPI_AVEncSliceControlMode", new("e9e782ef-5f18-44c9-a90b-e9c3c2c17b0b")), ("CODECAPI_AVEncVideoLTRBufferControl", new("a4a0e93d-4cbc-444c-89f4-826d310e92a7")),
            };
            var pv = stackalloc byte[24];
            foreach (var (name, g0) in props)
            {
                var g = g0;
                int sup = ((delegate* unmanaged<IntPtr, Guid*, int>)MFReader.Fn(api, 3))(api, &g);                           // IsSupported
                new Span<byte>(pv, 24).Clear();
                int get = ((delegate* unmanaged<IntPtr, Guid*, byte*, int>)MFReader.Fn(api, 8))(api, &g, pv);                  // GetValue
                string v = get >= 0 ? MFReader.PropVariant(pv) : "";
                log.Add($"  {name}: supported {Hr(sup)} get {Hr(get)} {v}");
            }
            MFReader.Release(api);
            return log;
        }

        private static List<string> Enumerated(Guid category, Guid major, Guid? input, Guid? output)
        {
            var log = new List<string>();
            var i = new MFT_REGISTER_TYPE_INFO { guidMajorType = major, guidSubtype = input ?? Guid.Empty };
            var o = new MFT_REGISTER_TYPE_INFO { guidMajorType = major, guidSubtype = output ?? Guid.Empty };
            foreach (uint flags in new uint[] { 0x3F, 1 | 0x20 | 0x40, 0x1 })
            {
                IntPtr* acts; uint n;
                int hr = OperatingSystem.IsWindows() ? WinMFTEnumEx(category, flags, input != null ? &i : null, output != null ? &o : null, &acts, &n) : MFExports.MFTEnumEx(category, flags, input != null ? &i : null, output != null ? &o : null, &acts, &n);
                log.Add($"  MFTEnumEx flags 0x{flags:x} in {(input is { } a ? MFReader.Name(a) : "-")} out {(output is { } b ? MFReader.Name(b) : "-")}: {Hr(hr)} count {(hr >= 0 ? n : 0)}");
                if (hr < 0) continue;
                for (uint k = 0; k < n; k++)
                {
                    var names = MFReader.Dump(acts[k]).Where(l => l.Contains("314ffbae") || l.Contains("6821c42b") || l.Contains("9d1e88d3") || l.Contains("f81a699a")).Select(l => l.Split(" = ").Last());
                    log.Add($"    {k}: {string.Join(" | ", names)}");
                    MFReader.Release(acts[k]);
                }
                if (n > 0) Marshal.FreeCoTaskMem((IntPtr)acts);
            }
            return log;
        }

        /// <summary>More of the encoders: what is enumerated, every type, the errors before the types are set, a longer encode.</summary>
        internal static List<string> Details()
        {
            var log = new List<string>();
            var vcat = new Guid("f79eac7d-e545-4387-bdee-d647d7bde42a"); var acat = new Guid("91c64bd0-f91e-4d8c-9276-db248279d975");
            log.Add("==== enumerated");
            log.AddRange(Enumerated(vcat, VIDEO, NV12, H264));
            log.AddRange(Enumerated(vcat, VIDEO, null, H264));
            log.AddRange(Enumerated(acat, AUDIO, PCM, AAC));
            log.AddRange(Enumerated(acat, AUDIO, FLOAT, AAC));

            // ---- H.264 ----
            IntPtr t = Encoder(vcat, VIDEO, NV12, H264);
            log.Add($"==== H.264 encoder {(t != IntPtr.Zero ? "found" : "none")}");
            if (t != IntPtr.Zero)
            {
                IntPtr a; ((delegate* unmanaged<IntPtr, IntPtr*, int>)MFReader.Fn(t, 8))(t, &a);
                log.Add("  attributes:"); log.AddRange(MFReader.Dump(a).Select(l => "    " + l)); MFReader.Release(a);
                uint* ids = stackalloc uint[4];
                log.Add($"  stream limits {Hr(((delegate* unmanaged<IntPtr, uint*, uint*, uint*, uint*, int>)MFReader.Fn(t, 3))(t, ids, ids + 1, ids + 2, ids + 3))} {ids[0]} {ids[1]} {ids[2]} {ids[3]}");
                IntPtr cur;
                log.Add($"  input current, none set {Hr(((delegate* unmanaged<IntPtr, uint, IntPtr*, int>)MFReader.Fn(t, 17))(t, 0, &cur))}");
                log.Add($"  output current, none set {Hr(((delegate* unmanaged<IntPtr, uint, IntPtr*, int>)MFReader.Fn(t, 18))(t, 0, &cur))}");
                var info = stackalloc uint[6];
                log.Add($"  output info, none set {Hr(((delegate* unmanaged<IntPtr, uint, uint*, int>)MFReader.Fn(t, 7))(t, 0, info))} flags 0x{info[0]:x} size {info[1]}");
                log.AddRange(CodecApi(t));
                foreach (var (what, o) in new[] { ("no size", H264Out(320, 240, size: false)), ("no rate", H264Out(320, 240, rate: false)), ("no bitrate", H264Out(320, 240, bitrate: false)),
                    ("no interlace", H264Out(320, 240, interlace: false)), ("odd size", H264Out(321, 241)), ("Baseline", H264Out(320, 240, 66)), ("High", H264Out(320, 240, 100)), ("Main", H264Out(320, 240, 77)), ("plain", H264Out(320, 240)) })
                {
                    log.Add($"  set output, {what}: test {Hr(SetOut(t, o, 1))} set {Hr(SetOut(t, o))}");
                    MFReader.Release(o);
                }
                log.Add("  output current:");
                if (((delegate* unmanaged<IntPtr, uint, IntPtr*, int>)MFReader.Fn(t, 18))(t, 0, &cur) >= 0) { log.AddRange(MFReader.Dump(cur).Select(l => "    " + l)); MFReader.Release(cur); }
                log.AddRange(AllTypes(t, 13, "input available"));
                log.AddRange(AllTypes(t, 14, "output available"));
                log.Add($"  output info, output set {Hr(((delegate* unmanaged<IntPtr, uint, uint*, int>)MFReader.Fn(t, 7))(t, 0, info))} flags 0x{info[0]:x} size {info[1]}");
                // an input of another size, of another rate
                foreach (var (what, w, h, r) in new[] { ("other size", 640u, 480u, 30u), ("other rate", 320u, 240u, 25u), ("same", 320u, 240u, 30u) })
                {
                    var i = MFReader.MediaType();
                    MFReader.SetGuid(i, MAJOR, VIDEO); MFReader.SetGuid(i, SUB, NV12);
                    MFReader.SetU64(i, FRAME_SIZE, MF.Pack(w, h)); MFReader.SetU64(i, FRAME_RATE, MF.Pack(r, 1)); MFReader.SetU32(i, INTERLACE, 2);
                    log.Add($"  set input, {what}: {Hr(SetIn(t, i))}");
                    MFReader.Release(i);
                }
                log.Add("  input current:");
                if (((delegate* unmanaged<IntPtr, uint, IntPtr*, int>)MFReader.Fn(t, 17))(t, 0, &cur) >= 0) { log.AddRange(MFReader.Dump(cur).Select(l => "    " + l)); MFReader.Release(cur); }
                log.Add($"  input info {Hr(((delegate* unmanaged<IntPtr, uint, uint*, int>)MFReader.Fn(t, 6))(t, 0, info))} latency {info[0]}{info[1]} flags 0x{info[2]:x} size {info[3]} lookahead {info[4]} align {info[5]}");
                log.Add($"  output info {Hr(((delegate* unmanaged<IntPtr, uint, uint*, int>)MFReader.Fn(t, 7))(t, 0, info))} flags 0x{info[0]:x} size {info[1]} align {info[2]}");
                var (ohr, _, _) = Output(t, 65536);
                log.Add($"  output before input {Hr(ohr)}");
                log.Add($"  begin streaming {Hr(Message(t, 0x10000000))}, start of stream {Hr(Message(t, 0x10000003))}");
                // ten frames: when each output comes
                for (int k = 0; k < 10; k++)
                {
                    var s = MFWriter.Sample(MFDecoderTests.Picture(320, 240, k), k * 333_333L, 333_333);
                    int hr = ((delegate* unmanaged<IntPtr, uint, IntPtr, uint, int>)MFReader.Fn(t, 24))(t, 0, s, 0);
                    MFReader.Release(s);
                    log.Add($"  input {k} {Hr(hr)}");
                    DrainBrief(t, log, 65536, true);
                }
                log.Add($"  drain {Hr(Message(t, 1))}");
                DrainBrief(t, log, 65536, true);
                log.Add($"  flush {Hr(Message(t, 0))}");
                MFReader.Release(t);
            }

            // ---- AAC ----
            t = Encoder(acat, AUDIO, PCM, AAC);
            log.Add($"==== AAC encoder {(t != IntPtr.Zero ? "found" : "none")}");
            if (t != IntPtr.Zero)
            {
                IntPtr a; ((delegate* unmanaged<IntPtr, IntPtr*, int>)MFReader.Fn(t, 8))(t, &a);
                log.Add("  attributes:"); log.AddRange(MFReader.Dump(a).Select(l => "    " + l)); MFReader.Release(a);
                log.AddRange(CodecApi(t));
                log.AddRange(AllTypes(t, 13, "input available, none set"));
                log.AddRange(AllTypes(t, 14, "output available, none set"));
                foreach (var (what, i) in new[] { ("mono 44100", Pcm(1, 44100)), ("6 ch 48000", Pcm(6, 48000)), ("22050", Pcm(2, 22050)), ("8000", Pcm(2, 8000)), ("96000", Pcm(2, 96000)),
                    ("24 bits", Pcm(2, 44100, 24)), ("float", Pcm(2, 44100, 32, FLOAT)), ("3 ch", Pcm(3, 44100)), ("stereo 48000", Pcm(2, 48000)) })
                {
                    log.Add($"  set input, {what}: test {Hr(SetIn(t, i, 1))} set {Hr(SetIn(t, i))}");
                    MFReader.Release(i);
                }
                log.AddRange(AllTypes(t, 14, "output available, input 48000 stereo"));
                IntPtr cur;
                log.Add($"  output current, none set {Hr(((delegate* unmanaged<IntPtr, uint, IntPtr*, int>)MFReader.Fn(t, 18))(t, 0, &cur))}");
                var o = MFReader.MediaType();
                MFReader.SetGuid(o, MAJOR, AUDIO); MFReader.SetGuid(o, SUB, AAC);
                MFReader.SetU32(o, CHANNELS, 2); MFReader.SetU32(o, RATE, 48000); MFReader.SetU32(o, BITS, 16); MFReader.SetU32(o, BYTES, 24000);
                log.Add($"  set output, plain: {Hr(SetOut(t, o))}");
                MFReader.SetU32(o, PAYLOAD, 1);
                log.Add($"  set output, ADTS: {Hr(SetOut(t, o))}");
                MFReader.SetU32(o, BYTES, 23000);
                log.Add($"  set output, 23000 bytes: {Hr(SetOut(t, o))}");
                MFReader.Release(o);
                log.Add("  output current:");
                if (((delegate* unmanaged<IntPtr, uint, IntPtr*, int>)MFReader.Fn(t, 18))(t, 0, &cur) >= 0) { log.AddRange(MFReader.Dump(cur).Select(l => "    " + l)); MFReader.Release(cur); }
                log.AddRange(AllTypes(t, 13, "input available, both set"));
                var info = stackalloc uint[6];
                log.Add($"  input info {Hr(((delegate* unmanaged<IntPtr, uint, uint*, int>)MFReader.Fn(t, 6))(t, 0, info))} latency {info[0]}{info[1]} flags 0x{info[2]:x} size {info[3]} lookahead {info[4]} align {info[5]}");
                log.Add($"  output info {Hr(((delegate* unmanaged<IntPtr, uint, uint*, int>)MFReader.Fn(t, 7))(t, 0, info))} flags 0x{info[0]:x} size {info[1]} align {info[2]}");
                // 0.1 s of 48 kHz stereo, in pieces of 1000 frames
                var pcm = new byte[4800 * 4];
                for (int k = 0; k < 4800; k++) { short v = (short)(8000 * Math.Sin(2 * Math.PI * 440 * k / 48000)); BitConverter.GetBytes(v).CopyTo(pcm, k * 4); BitConverter.GetBytes(v).CopyTo(pcm, k * 4 + 2); }
                for (int k = 0; k < 5; k++)
                {
                    var piece = pcm.AsSpan(k * 4000 * (k < 4 ? 1 : 1), Math.Min(4000, pcm.Length - k * 4000)).ToArray();
                    var s = MFWriter.Sample(piece, k * 1000 * 10_000_000L / 48000, piece.Length / 4 * 10_000_000L / 48000);
                    log.Add($"  input {k} ({piece.Length / 4} frames) {Hr(((delegate* unmanaged<IntPtr, uint, IntPtr, uint, int>)MFReader.Fn(t, 24))(t, 0, s, 0))}");
                    MFReader.Release(s);
                    DrainBrief(t, log, 8192, false);
                }
                log.Add($"  drain {Hr(Message(t, 1))}");
                DrainBrief(t, log, 8192, false);
                MFReader.Release(t);
            }
            return log;
        }

        private static void DrainBrief(IntPtr t, List<string> log, uint size, bool video)
        {
            for (int n = 0; n < 60; n++)
            {
                var (hr, st, s) = Output(t, size);
                if (hr < 0) { log.Add($"    output {Hr(hr)}"); break; }
                if (s == IntPtr.Zero) { log.Add($"    output: status 0x{st:x} no sample"); continue; }
                var b = MFReader.SampleBytes(s, out _);
                long time; ((delegate* unmanaged<IntPtr, long*, int>)MFReader.Fn(s, 35))(s, &time);
                var attrs = MFReader.Dump(s).Select(l => l.Split(' ')[0] + "=" + l.Split(' ').Last());
                log.Add($"    output: time {time} duration {MFReader.Duration(s)} bytes {b.Length} {(video ? string.Join(",", Nals(b)) : MFReader.HexOf(b, 4))} [{string.Join(" ", attrs)}]");
                MFReader.Release(s);
            }
        }

        private static void DumpType(IntPtr t, int slot, uint n, List<string> log, string what)
        {
            IntPtr type;
            int hr = ((delegate* unmanaged<IntPtr, uint, uint, IntPtr*, int>)MFReader.Fn(t, slot))(t, 0, n, &type);
            log.Add($"  {what} {n}: {Hr(hr)}");
            if (hr >= 0) { log.AddRange(MFReader.Dump(type).Select(l => "    " + l)); MFReader.Release(type); }
        }

        /// <summary>The types in full, and what the AAC encoder says to outputs of another rate or before an input.</summary>
        internal static List<string> Dumps()
        {
            var log = new List<string>();
            var vcat = new Guid("f79eac7d-e545-4387-bdee-d647d7bde42a"); var acat = new Guid("91c64bd0-f91e-4d8c-9276-db248279d975");
            IntPtr t = Encoder(acat, AUDIO, PCM, AAC);
            log.Add("==== AAC");
            DumpType(t, 13, 0, log, "input"); DumpType(t, 13, 1, log, "input"); DumpType(t, 14, 0, log, "output"); DumpType(t, 14, 278, log, "output");
            var o = MFReader.MediaType();
            MFReader.SetGuid(o, MAJOR, AUDIO); MFReader.SetGuid(o, SUB, AAC);
            MFReader.SetU32(o, CHANNELS, 2); MFReader.SetU32(o, RATE, 44100); MFReader.SetU32(o, BITS, 16); MFReader.SetU32(o, BYTES, 16000);
            log.Add($"  set output before input: test {Hr(SetOut(t, o, 1))} set {Hr(SetOut(t, o))}");
            DumpType(t, 13, 0, log, "input, output set");
            DumpType(t, 13, 1, log, "input, output set");
            log.Add($"  input, output set 2+: {string.Join(" ", AllTypes(t, 13, "i").Skip(2).Select(l => l.Trim()))}");
            var i = Pcm(2, 48000);
            log.Add($"  set input of another rate: {Hr(SetIn(t, i))}");
            MFReader.Release(i);
            i = Pcm(2, 44100);
            log.Add($"  set input of the rate: {Hr(SetIn(t, i))}");
            IntPtr cur;
            if (((delegate* unmanaged<IntPtr, uint, IntPtr*, int>)MFReader.Fn(t, 17))(t, 0, &cur) >= 0) { log.Add("  input current:"); log.AddRange(MFReader.Dump(cur).Select(l => "    " + l)); MFReader.Release(cur); }
            if (((delegate* unmanaged<IntPtr, uint, IntPtr*, int>)MFReader.Fn(t, 18))(t, 0, &cur) >= 0) { log.Add($"  output current same object {cur == o}:"); log.AddRange(MFReader.Dump(cur).Select(l => "    " + l)); MFReader.Release(cur); }
            MFReader.Release(i);
            var o2 = MFReader.MediaType();
            MFReader.SetGuid(o2, MAJOR, AUDIO); MFReader.SetGuid(o2, SUB, AAC);
            MFReader.SetU32(o2, CHANNELS, 1); MFReader.SetU32(o2, RATE, 44100); MFReader.SetU32(o2, BITS, 16); MFReader.SetU32(o2, BYTES, 16000);
            log.Add($"  set output of other channels: {Hr(SetOut(t, o2))}");
            MFReader.SetU32(o2, CHANNELS, 2); MFReader.SetU32(o2, BYTES, 17000);
            log.Add($"  set output of 17000 bytes: {Hr(SetOut(t, o2))}");
            MFReader.SetU32(o2, BYTES, 16000); MFReader.SetU32(o2, PROFILE_LEVEL, 0x2A);
            log.Add($"  set output of level 0x2a: {Hr(SetOut(t, o2))}");
            MFReader.SetU32(o2, PROFILE_LEVEL, 0x29); MFReader.SetU32(o2, PAYLOAD, 3);
            log.Add($"  set output of payload 3: {Hr(SetOut(t, o2))}");
            MFReader.SetU32(o2, PAYLOAD, 0); MFReader.SetU32(o2, BITS, 24);
            log.Add($"  set output of 24 bits: {Hr(SetOut(t, o2))}");
            MFReader.Release(o2); MFReader.Release(o);
            MFReader.Release(t);
            t = Encoder(vcat, VIDEO, NV12, H264);
            log.Add("==== H.264");
            DumpType(t, 14, 0, log, "output");
            var h = H264Out(320, 240);
            SetOut(t, h);
            DumpType(t, 13, 0, log, "input"); DumpType(t, 13, 2, log, "input");
            MFReader.SetU32(h, MPEG2_PROFILE, 66);
            log.Add($"  set output again, Baseline: {Hr(SetOut(t, h))}");
            if (((delegate* unmanaged<IntPtr, uint, IntPtr*, int>)MFReader.Fn(t, 18))(t, 0, &cur) >= 0) { log.Add($"  output current same object {cur == h}:"); log.AddRange(MFReader.Dump(cur).Select(l => "    " + l)); MFReader.Release(cur); }
            MFReader.Release(h);
            var hi = MFReader.MediaType();
            MFReader.SetGuid(hi, MAJOR, VIDEO); MFReader.SetGuid(hi, SUB, NV12);
            MFReader.SetU64(hi, FRAME_SIZE, MF.Pack(320, 240)); MFReader.SetU64(hi, FRAME_RATE, MF.Pack(30, 1));
            log.Add($"  set input without interlace: {Hr(SetIn(t, hi))}");
            MFReader.SetGuid(hi, SUB, new Guid("00000016-0000-0010-8000-00aa00389b71"));
            log.Add($"  set input RGB32: {Hr(SetIn(t, hi))}");
            MFReader.Release(hi);
            var o3 = H264Out(320, 240);
            MFReader.SetU32(o3, MPEG2_PROFILE, 66);
            SetOut(t, o3);
            hi = MFReader.MediaType();
            MFReader.SetGuid(hi, MAJOR, VIDEO); MFReader.SetGuid(hi, SUB, NV12);
            MFReader.SetU64(hi, FRAME_SIZE, MF.Pack(320, 240)); MFReader.SetU64(hi, FRAME_RATE, MF.Pack(30, 1)); MFReader.SetU32(hi, INTERLACE, 2);
            SetIn(t, hi); MFReader.Release(hi); MFReader.Release(o3);
            log.Add($"  Baseline, ten frames: begin {Hr(Message(t, 0x10000000))}");
            for (int k = 0; k < 10; k++)
            {
                var s = MFWriter.Sample(MFDecoderTests.Picture(320, 240, k), k * 333_333L, 333_333);
                int hr = ((delegate* unmanaged<IntPtr, uint, IntPtr, uint, int>)MFReader.Fn(t, 24))(t, 0, s, 0);
                MFReader.Release(s);
                log.Add($"  input {k} {Hr(hr)}");
                DrainBrief(t, log, 200000, true);
            }
            log.Add($"  drain {Hr(Message(t, 1))}");
            DrainBrief(t, log, 200000, true);
            MFReader.Release(t);
            return log;
        }

        [Fact]
        public void ProbeDumps()
        {
            Assert.SkipWhen(Environment.GetEnvironmentVariable("MF_PROBE_ENC3") is not { Length: > 0 }, "MF_PROBE_ENC3=<file> writes the report");
            System.IO.File.WriteAllLines(Environment.GetEnvironmentVariable("MF_PROBE_ENC3")!, Dumps());
        }

        [Fact]
        public void ProbeDetails()
        {
            Assert.SkipWhen(Environment.GetEnvironmentVariable("MF_PROBE_ENC2") is not { Length: > 0 }, "MF_PROBE_ENC2=<file> writes the report");
            System.IO.File.WriteAllLines(Environment.GetEnvironmentVariable("MF_PROBE_ENC2")!, Details());
        }

        [Fact]
        public void Probe()
        {
            Assert.SkipWhen(Environment.GetEnvironmentVariable("MF_PROBE_ENC") is not { Length: > 0 }, "MF_PROBE_ENC=<file> writes the report");
            System.IO.File.WriteAllLines(Environment.GetEnvironmentVariable("MF_PROBE_ENC")!, Report());
        }
    }
}
