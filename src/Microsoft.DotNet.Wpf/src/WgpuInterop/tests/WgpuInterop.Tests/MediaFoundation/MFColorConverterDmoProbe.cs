// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// Windows' color converter DSP as a DMO (IMediaObject; MF_PROBE_DMO=<file> writes the report): its streams, the types it
// offers in and out (DMO_MEDIA_TYPE: the GUIDs, sizes, the format block whole), what it takes, what IMFTransform sees of
// the types set through it, the sizes, and a picture through ProcessInput / ProcessOutput (of IMediaBuffers of the probe's).
//

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using Microsoft.Wpf.Interop.WebGpu.Direct3D;
using Microsoft.Wpf.Interop.WebGpu.MediaFoundation;
using Xunit;

namespace WgpuInterop.Tests.MediaFoundation
{
    /// <summary>An IMediaBuffer over managed memory.</summary>
    internal sealed unsafe class ProbeMediaBuffer : ComObject
    {
        private static readonly Vtable s_vt = Build();
        public readonly IntPtr Memory;
        public readonly uint Max;
        public uint Length;

        public ProbeMediaBuffer(uint max, byte[]? data = null) : base(s_vt)
        {
            Max = max; Memory = Marshal.AllocHGlobal((int)Math.Max(1, max));
            new Span<byte>((void*)Memory, (int)max).Fill(0x5a);
            if (data != null) { data.CopyTo(new Span<byte>((void*)Memory, data.Length)); Length = (uint)data.Length; }
        }

        protected override void OnFinalRelease() => Marshal.FreeHGlobal(Memory);
        public byte[] Bytes() => new Span<byte>((void*)Memory, (int)Length).ToArray();

        private static Vtable Build()
        {
            var vt = new Vtable("IMediaBuffer", 6, new Guid("59eff8b9-938c-4a26-82f2-95cb84cdc837"));
            vt.Set(3, (delegate* unmanaged<IntPtr, uint, int>)&SetLength);
            vt.Set(4, (delegate* unmanaged<IntPtr, uint*, int>)&GetMaxLength);
            vt.Set(5, (delegate* unmanaged<IntPtr, byte**, uint*, int>)&GetBufferAndLength);
            return vt;
        }

        [UnmanagedCallersOnly] private static int SetLength(IntPtr self, uint n) { var b = From<ProbeMediaBuffer>(self)!; if (n > b.Max) return unchecked((int)0x80070057); b.Length = n; return 0; }
        [UnmanagedCallersOnly] private static int GetMaxLength(IntPtr self, uint* n) { *n = From<ProbeMediaBuffer>(self)!.Max; return 0; }
        [UnmanagedCallersOnly] private static int GetBufferAndLength(IntPtr self, byte** p, uint* n) { var b = From<ProbeMediaBuffer>(self)!; if (p != null) *p = (byte*)b.Memory; if (n != null) *n = b.Length; return 0; }
    }

    public sealed unsafe class MFColorConverterDmoProbe
    {
        private static string Hr(int hr) => $"0x{hr:x8}";
        private static IntPtr Fn(IntPtr o, int slot) => MFReader.Fn(o, slot);
        private static readonly Guid IID_IMediaObject = new("d8ad0f58-5494-4102-97c5-ec798e59bcf4");
        private static readonly Guid Video = new("73646976-0000-0010-8000-00aa00389b71"), FORMAT_VideoInfo = new("05589f80-c356-11ce-bf01-00aa0055595a");
        private static Guid Fourcc(string s) => new((uint)(s[0] | s[1] << 8 | s[2] << 16 | s[3] << 24), 0, 0x10, 0x80, 0, 0, 0xaa, 0, 0x38, 0x9b, 0x71);
        private static readonly Guid NV12 = Fourcc("NV12"), RGB32 = new("e436eb7e-524f-11ce-9f53-0020af0ba770");   // MEDIASUBTYPE_RGB32 (DirectShow's)
        private static readonly Guid RGB32MF = new("00000016-0000-0010-8000-00aa00389b71");

        private const int MT = 88;           // sizeof(DMO_MEDIA_TYPE) of 64 bits; 72 of 32
        private static int Size => IntPtr.Size == 8 ? 88 : 72;
        private static int PUnk => IntPtr.Size == 8 ? 64 : 60;
        private static int CbFormat => IntPtr.Size == 8 ? 72 : 64;
        private static int PbFormat => IntPtr.Size == 8 ? 80 : 68;

        private static string Describe(byte* mt)
        {
            var major = *(Guid*)mt; var sub = *(Guid*)(mt + 16);
            uint fixedSize = *(uint*)(mt + 32), temporal = *(uint*)(mt + 36), sampleSize = *(uint*)(mt + 40);
            var format = *(Guid*)(mt + 44);
            uint cb = *(uint*)(mt + CbFormat); byte* pb = *(byte**)(mt + PbFormat);
            string f = cb > 0 && pb != null ? Convert.ToHexString(new ReadOnlySpan<byte>(pb, (int)cb)).ToLowerInvariant() : "";
            return $"{MFReader.Name(major)} {MFReader.Name(sub)} fixed {fixedSize} temporal {temporal} size {sampleSize} format {format} unk {(*(IntPtr*)(mt + PUnk) != IntPtr.Zero)} cb {cb} {f}";
        }

        private static void Free(byte* mt)
        {
            byte* pb = *(byte**)(mt + PbFormat);
            if (pb != null) Marshal.FreeCoTaskMem((IntPtr)pb);
            var unk = *(IntPtr*)(mt + PUnk);
            if (unk != IntPtr.Zero) MFReader.Release(unk);
            new Span<byte>(mt, Size).Clear();
        }

        /// <summary>A DMO_MEDIA_TYPE of a VIDEOINFOHEADER of the size, of the bit count and compression of the subtype.</summary>
        private static byte* Type(Guid sub, int w, int h, ushort bits, uint compression, uint sampleSize)
        {
            byte* mt = (byte*)Marshal.AllocHGlobal(Size); new Span<byte>(mt, Size).Clear();
            *(Guid*)mt = Video; *(Guid*)(mt + 16) = sub;
            *(uint*)(mt + 32) = 1; *(uint*)(mt + 40) = sampleSize;
            *(Guid*)(mt + 44) = FORMAT_VideoInfo;
            byte* vih = (byte*)Marshal.AllocCoTaskMem(88); new Span<byte>(vih, 88).Clear();
            *(long*)(vih + 40) = 400_000;                                    // AvgTimePerFrame
            byte* bih = vih + 48;
            *(uint*)bih = 40; *(int*)(bih + 4) = w; *(int*)(bih + 8) = h; *(ushort*)(bih + 12) = 1; *(ushort*)(bih + 14) = bits;
            *(uint*)(bih + 16) = compression; *(uint*)(bih + 20) = sampleSize;
            *(uint*)(mt + CbFormat) = 88; *(byte**)(mt + PbFormat) = vih;
            return mt;
        }

        private static void Drop(byte* mt) { Marshal.FreeCoTaskMem((IntPtr)(*(byte**)(mt + PbFormat))); Marshal.FreeHGlobal((IntPtr)mt); }

        internal static List<string> Report()
        {
            MFReader.Startup();
            if (OperatingSystem.IsWindows()) MFVideoProcessorProbe.Init();
            var log = new List<string>();
            int hr = MFVideoProcessorProbe.Make(MFVideoProcessorProbe.ColorConverterClsid, out var t);
            log.Add($"create {Hr(hr)}");
            if (hr < 0) return log;
            var iid = IID_IMediaObject; IntPtr dmo;
            hr = ((delegate* unmanaged<IntPtr, Guid*, IntPtr*, int>)Fn(t, 0))(t, &iid, &dmo);
            log.Add($"IMediaObject {Hr(hr)}");
            if (hr < 0) { MFReader.Release(t); return log; }
            uint ni, no;
            log.Add($"streams {Hr(((delegate* unmanaged<IntPtr, uint*, uint*, int>)Fn(dmo, 3))(dmo, &ni, &no))} {ni} {no}");
            uint fl;
            log.Add($"input stream info {Hr(((delegate* unmanaged<IntPtr, uint, uint*, int>)Fn(dmo, 4))(dmo, 0, &fl))} 0x{fl:x}");
            log.Add($"output stream info {Hr(((delegate* unmanaged<IntPtr, uint, uint*, int>)Fn(dmo, 5))(dmo, 0, &fl))} 0x{fl:x}");
            log.Add($"input stream info of 1 {Hr(((delegate* unmanaged<IntPtr, uint, uint*, int>)Fn(dmo, 4))(dmo, 1, &fl))}");
            byte* mt = stackalloc byte[Size];
            new Span<byte>(mt, Size).Clear();
            for (uint k = 0; k < 40; k++)
            {
                int thr = ((delegate* unmanaged<IntPtr, uint, uint, byte*, int>)Fn(dmo, 6))(dmo, 0, k, mt);
                if (thr < 0) { log.Add($"  input type {k}: {Hr(thr)}"); break; }
                log.Add($"  input type {k}: {Describe(mt)}");
                Free(mt);
            }
            log.Add($"  input type, null: {Hr(((delegate* unmanaged<IntPtr, uint, uint, byte*, int>)Fn(dmo, 6))(dmo, 0, 0, null))}");
            for (uint k = 0; k < 40; k++)
            {
                int thr = ((delegate* unmanaged<IntPtr, uint, uint, byte*, int>)Fn(dmo, 7))(dmo, 0, k, mt);
                if (thr < 0) { log.Add($"  output type {k}, no input: {Hr(thr)}"); break; }
                log.Add($"  output type {k}, no input: {Describe(mt)}");
                Free(mt);
            }
            log.Add($"  input current, none: {Hr(((delegate* unmanaged<IntPtr, uint, byte*, int>)Fn(dmo, 10))(dmo, 0, mt))}");
            uint size, look, align;
            log.Add($"  input size info, none: {Hr(((delegate* unmanaged<IntPtr, uint, uint*, uint*, uint*, int>)Fn(dmo, 12))(dmo, 0, &size, &look, &align))}");

            // NV12 64x48 in: test (DMO_SET_TYPEF_TEST_ONLY 1), set
            var nv = Type(NV12, 64, 48, 12, 0x3231564e, 64 * 48 * 3 / 2);
            log.Add($"set input NV12: test {Hr(((delegate* unmanaged<IntPtr, uint, byte*, uint, int>)Fn(dmo, 8))(dmo, 0, nv, 1))} set {Hr(((delegate* unmanaged<IntPtr, uint, byte*, uint, int>)Fn(dmo, 8))(dmo, 0, nv, 0))}");
            Drop(nv);
            if (((delegate* unmanaged<IntPtr, uint, byte*, int>)Fn(dmo, 10))(dmo, 0, mt) is var chr && chr >= 0) { log.Add($"  input current: {Describe(mt)}"); Free(mt); } else log.Add($"  input current: {Hr(chr)}");
            log.Add($"  input size info {Hr(((delegate* unmanaged<IntPtr, uint, uint*, uint*, uint*, int>)Fn(dmo, 12))(dmo, 0, &size, &look, &align))} {size} {look} {align}");
            IntPtr cur;
            int mhr = ((delegate* unmanaged<IntPtr, uint, IntPtr*, int>)Fn(t, 17))(t, 0, &cur);
            log.Add($"  the transform's input current {Hr(mhr)}");
            if (mhr >= 0) { log.AddRange(MFReader.Dump(cur).Select(l => "    " + l)); MFReader.Release(cur); }
            for (uint k = 0; k < 40; k++)
            {
                int thr = ((delegate* unmanaged<IntPtr, uint, uint, byte*, int>)Fn(dmo, 7))(dmo, 0, k, mt);
                if (thr < 0) { log.Add($"  output type {k}: {Hr(thr)}"); break; }
                log.Add($"  output type {k}: {Describe(mt)}");
                Free(mt);
            }
            // RGB32 out, top-down (a negative height) and bottom-up
            foreach (var (name, h) in new[] { ("RGB32 bottom-up", 48), ("RGB32 top-down", -48) })
            {
                var rgb = Type(RGB32, 64, h, 32, 0, 64 * 48 * 4);
                log.Add($"set output {name}: test {Hr(((delegate* unmanaged<IntPtr, uint, byte*, uint, int>)Fn(dmo, 9))(dmo, 0, rgb, 1))} set {Hr(((delegate* unmanaged<IntPtr, uint, byte*, uint, int>)Fn(dmo, 9))(dmo, 0, rgb, 0))}");
                Drop(rgb);
                if (((delegate* unmanaged<IntPtr, uint, byte*, int>)Fn(dmo, 11))(dmo, 0, mt) is var ohr && ohr >= 0) { log.Add($"  output current: {Describe(mt)}"); Free(mt); } else log.Add($"  output current: {Hr(ohr)}");
                log.Add($"  output size info {Hr(((delegate* unmanaged<IntPtr, uint, uint*, uint*, int>)Fn(dmo, 13))(dmo, 0, &size, &align))} {size} {align}");
                mhr = ((delegate* unmanaged<IntPtr, uint, IntPtr*, int>)Fn(t, 18))(t, 0, &cur);
                log.Add($"  the transform's output current {Hr(mhr)}");
                if (mhr >= 0) { log.AddRange(MFReader.Dump(cur).Select(l => "    " + l)); MFReader.Release(cur); }
                long lat;
                log.Add($"  max latency {Hr(((delegate* unmanaged<IntPtr, uint, long*, int>)Fn(dmo, 14))(dmo, 0, &lat))}");
                log.Add($"  allocate {Hr(((delegate* unmanaged<IntPtr, int>)Fn(dmo, 18))(dmo))}");
                uint st;
                log.Add($"  input status {Hr(((delegate* unmanaged<IntPtr, uint, uint*, int>)Fn(dmo, 20))(dmo, 0, &st))} 0x{st:x}");
                var input = new ProbeMediaBuffer(64 * 48 * 3 / 2, MFDecoderTests.Picture(64, 48, 1));
                // DMO_INPUT_DATA_BUFFERF_SYNCPOINT 1, _TIME 2, _TIMELENGTH 4
                log.Add($"  process input {Hr(((delegate* unmanaged<IntPtr, uint, IntPtr, uint, long, long, int>)Fn(dmo, 21))(dmo, 0, input.Pointer, 7, 400_000, 400_000))}");
                log.Add($"  input status {Hr(((delegate* unmanaged<IntPtr, uint, uint*, int>)Fn(dmo, 20))(dmo, 0, &st))} 0x{st:x}");
                log.Add($"  process input again {Hr(((delegate* unmanaged<IntPtr, uint, IntPtr, uint, long, long, int>)Fn(dmo, 21))(dmo, 0, input.Pointer, 7, 800_000, 400_000))}");
                input.Release();
                var output = new ProbeMediaBuffer(64 * 48 * 4 + 64);
                int odbSize = IntPtr.Size == 8 ? 32 : 24;
                byte* odb = stackalloc byte[odbSize]; new Span<byte>(odb, odbSize).Clear();
                *(IntPtr*)odb = output.Pointer;
                int at = IntPtr.Size, tsAt = IntPtr.Size == 8 ? 16 : 8;
                uint status;
                int phr = ((delegate* unmanaged<IntPtr, uint, uint, byte*, uint*, int>)Fn(dmo, 22))(dmo, 0, 1, odb, &status);
                var bytes = output.Bytes();
                long sum = 0; foreach (var b in bytes) sum += b;
                log.Add($"  process output {Hr(phr)} status {status} flags 0x{*(uint*)(odb + at):x} time {*(long*)(odb + tsAt)} length {*(long*)(odb + tsAt + 8)} bytes {bytes.Length} sum {sum} {Convert.ToHexString(bytes, 0, Math.Min(16, bytes.Length)).ToLowerInvariant()}");
                new Span<byte>(odb, odbSize).Clear(); *(IntPtr*)odb = output.Pointer; output.Length = 0;
                phr = ((delegate* unmanaged<IntPtr, uint, uint, byte*, uint*, int>)Fn(dmo, 22))(dmo, 0, 1, odb, &status);
                log.Add($"  process output again {Hr(phr)} flags 0x{*(uint*)(odb + at):x} bytes {output.Length}");
                output.Release();
                log.Add($"  flush {Hr(((delegate* unmanaged<IntPtr, int>)Fn(dmo, 16))(dmo))} discontinuity {Hr(((delegate* unmanaged<IntPtr, uint, int>)Fn(dmo, 17))(dmo, 0))} free {Hr(((delegate* unmanaged<IntPtr, int>)Fn(dmo, 19))(dmo))} lock {Hr(((delegate* unmanaged<IntPtr, int, int>)Fn(dmo, 23))(dmo, 1))} {Hr(((delegate* unmanaged<IntPtr, int, int>)Fn(dmo, 23))(dmo, 0))}");
            }
            // clearing the types (DMO_SET_TYPEF_CLEAR 2)
            log.Add($"clear output {Hr(((delegate* unmanaged<IntPtr, uint, byte*, uint, int>)Fn(dmo, 9))(dmo, 0, null, 2))} clear input {Hr(((delegate* unmanaged<IntPtr, uint, byte*, uint, int>)Fn(dmo, 8))(dmo, 0, null, 2))}");
            log.Add($"  input current: {Hr(((delegate* unmanaged<IntPtr, uint, byte*, int>)Fn(dmo, 10))(dmo, 0, mt))}");
            // the transform's types, seen through the DMO
            var mfin = MFReader.MediaType();
            MFReader.SetGuid(mfin, new Guid("48eba18e-f8c9-4687-bf11-0a74c9f96a8f"), Video); MFReader.SetGuid(mfin, new Guid("f7e34c9a-42e8-4714-b74b-cb29d72c35e5"), NV12);
            MFReader.SetU64(mfin, new Guid("1652c33d-d6b2-4012-b834-72030849a37d"), MF.Pack(64, 48));
            log.Add($"the transform's input NV12 {Hr(((delegate* unmanaged<IntPtr, uint, IntPtr, uint, int>)Fn(t, 15))(t, 0, mfin, 0))}");
            MFReader.Release(mfin);
            if (((delegate* unmanaged<IntPtr, uint, byte*, int>)Fn(dmo, 10))(dmo, 0, mt) is var dhr && dhr >= 0) { log.Add($"  the DMO's input current: {Describe(mt)}"); Free(mt); } else log.Add($"  the DMO's input current: {Hr(dhr)}");
            MFReader.Release(dmo);
            MFReader.Release(t);
            return log;
        }

        /// <summary>
        /// The shim's color converter is Windows' as a DMO too (ColorConverterDmo.windows.txt), line for line: its streams,
        /// types in and out, their format blocks, what IMFTransform sees of the types set through it and the DMO of the
        /// transform's, the sizes, and its picture of a buffer in, of its time, length and sync point.
        /// </summary>
        [Fact]
        public void TheColorConvertersMediaObject_IsWindows()
        {
            Assert.SkipWhen(OperatingSystem.IsWindows(), "Windows' color converter is the fixture");
            var windows = File.ReadAllLines(Path.Combine(TestFiles.Root, "MediaFoundation", "ColorConverterDmo.windows.txt"));
            var ours = Report();
            Assert.Equal(windows.Length, ours.Count);
            for (int i = 0; i < windows.Length; i++) Assert.True(windows[i] == ours[i], $"line {i}: Windows '{windows[i]}', ours '{ours[i]}'");
        }

        [Fact]
        public void Probe()
        {
            Assert.SkipWhen(Environment.GetEnvironmentVariable("MF_PROBE_DMO") is not { Length: > 0 }, "MF_PROBE_DMO=<file> writes the report");
            File.WriteAllLines(Environment.GetEnvironmentVariable("MF_PROBE_DMO")!, Report());
        }
    }
}
