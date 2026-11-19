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

        [Fact]
        public void Probe()
        {
            Assert.SkipWhen(Environment.GetEnvironmentVariable("MF_PROBE_ENC") is not { Length: > 0 }, "MF_PROBE_ENC=<file> writes the report");
            System.IO.File.WriteAllLines(Environment.GetEnvironmentVariable("MF_PROBE_ENC")!, Report());
        }
    }
}
