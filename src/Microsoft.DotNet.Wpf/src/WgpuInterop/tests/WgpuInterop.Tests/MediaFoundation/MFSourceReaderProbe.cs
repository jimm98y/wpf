// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// What a source reader does with an MP4 file, as a report line by line: its streams and their native
// types (every attribute), the presentation attributes, the samples (times, flags, attributes, bytes),
// the end of the stream, a decoder's output types, seeking. Windows' reader on Windows, the shim's
// elsewhere: the reports are compared (MF_PROBE_OUT=<file> writes one).
//

using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Wpf.Interop.WebGpu.MediaFoundation;
using Xunit;

namespace WgpuInterop.Tests.MediaFoundation
{
    internal static unsafe class MFReader
    {
        [DllImport("mfplat.dll", EntryPoint = "MFStartup")] private static extern int WinMFStartup(uint version, uint flags);
        [DllImport("mfplat.dll", EntryPoint = "MFCreateAttributes")] private static extern int WinMFCreateAttributes(IntPtr* a, uint n);
        [DllImport("mfplat.dll", EntryPoint = "MFCreateMediaType")] private static extern int WinMFCreateMediaType(IntPtr* t);
        [DllImport("mfreadwrite.dll", EntryPoint = "MFCreateSourceReaderFromURL")] private static extern int WinMFCreateSourceReaderFromURL([MarshalAs(UnmanagedType.LPWStr)] string url, IntPtr attributes, IntPtr* reader);
        [DllImport("ole32.dll")] private static extern int CoInitializeEx(IntPtr reserved, uint flags);

        public const uint FIRST_VIDEO = 0xFFFFFFFC, FIRST_AUDIO = 0xFFFFFFFD, ANY = 0xFFFFFFFE, MEDIASOURCE = 0xFFFFFFFF;

        public static IntPtr Fn(IntPtr o, int slot) => (*(IntPtr**)o)[slot];
        public static void Release(IntPtr o) { if (o != IntPtr.Zero) ((delegate* unmanaged<IntPtr, uint>)Fn(o, 2))(o); }

        public static IntPtr Attributes()
        {
            IntPtr a;
            int hr = OperatingSystem.IsWindows() ? WinMFCreateAttributes(&a, 4) : MFExports.MFCreateAttributes(&a, 4);
            Assert.True(hr >= 0);
            return a;
        }

        public static IntPtr MediaType()
        {
            IntPtr t;
            int hr = OperatingSystem.IsWindows() ? WinMFCreateMediaType(&t) : MFExports.MFCreateMediaType(&t);
            Assert.True(hr >= 0);
            return t;
        }

        /// <summary>Windows' Media Foundation started (COM, MFStartup); nothing elsewhere.</summary>
        public static void Startup()
        {
            if (!OperatingSystem.IsWindows()) return;
            CoInitializeEx(IntPtr.Zero, 0);
            WinMFStartup(0x20070, 0);
        }

        public static int Create(string path, IntPtr attributes, out IntPtr reader)
        {
            IntPtr r;
            int hr;
            if (OperatingSystem.IsWindows())
            {
                CoInitializeEx(IntPtr.Zero, 0);
                WinMFStartup(0x20070, 0);
                hr = WinMFCreateSourceReaderFromURL(path, attributes, &r);
            }
            else
            {
                fixed (char* p = path) hr = MFExports.MFCreateSourceReaderFromURL(p, attributes, &r);
            }
            reader = hr >= 0 ? r : IntPtr.Zero;
            return hr;
        }

        public static void SetU32(IntPtr a, Guid k, uint v) => ((delegate* unmanaged<IntPtr, Guid*, uint, int>)Fn(a, 21))(a, &k, v);
        public static void SetU64(IntPtr a, Guid k, ulong v) => ((delegate* unmanaged<IntPtr, Guid*, ulong, int>)Fn(a, 22))(a, &k, v);
        public static void SetGuid(IntPtr a, Guid k, Guid v) => ((delegate* unmanaged<IntPtr, Guid*, Guid*, int>)Fn(a, 24))(a, &k, &v);
        public static void SetUnknown(IntPtr a, Guid k, IntPtr v) => ((delegate* unmanaged<IntPtr, Guid*, IntPtr, int>)Fn(a, 27))(a, &k, v);
        public static uint U32(IntPtr a, Guid k, uint dflt = 0) { uint v; return ((delegate* unmanaged<IntPtr, Guid*, uint*, int>)Fn(a, 7))(a, &k, &v) >= 0 ? v : dflt; }
        public static ulong U64(IntPtr a, Guid k) { ulong v; return ((delegate* unmanaged<IntPtr, Guid*, ulong*, int>)Fn(a, 8))(a, &k, &v) >= 0 ? v : 0; }
        public static Guid G(IntPtr a, Guid k) { Guid v; return ((delegate* unmanaged<IntPtr, Guid*, Guid*, int>)Fn(a, 10))(a, &k, &v) >= 0 ? v : Guid.Empty; }

        // IMFSourceReader
        public static int GetStreamSelection(IntPtr r, uint i, out bool sel) { int s; int hr = ((delegate* unmanaged<IntPtr, uint, int*, int>)Fn(r, 3))(r, i, &s); sel = s != 0; return hr; }
        public static int SetStreamSelection(IntPtr r, uint i, bool sel) => ((delegate* unmanaged<IntPtr, uint, int, int>)Fn(r, 4))(r, i, sel ? 1 : 0);
        public static int GetNativeMediaType(IntPtr r, uint i, uint n, out IntPtr t) { IntPtr p; int hr = ((delegate* unmanaged<IntPtr, uint, uint, IntPtr*, int>)Fn(r, 5))(r, i, n, &p); t = hr >= 0 ? p : IntPtr.Zero; return hr; }
        public static int GetCurrentMediaType(IntPtr r, uint i, out IntPtr t) { IntPtr p; int hr = ((delegate* unmanaged<IntPtr, uint, IntPtr*, int>)Fn(r, 6))(r, i, &p); t = hr >= 0 ? p : IntPtr.Zero; return hr; }
        public static int SetCurrentMediaType(IntPtr r, uint i, IntPtr t) => ((delegate* unmanaged<IntPtr, uint, uint*, IntPtr, int>)Fn(r, 7))(r, i, null, t);

        public static int SetCurrentPosition(IntPtr r, long time)
        {
            var pv = stackalloc byte[24];
            new Span<byte>(pv, 24).Clear();
            *(ushort*)pv = 20;                                // VT_I8
            *(long*)(pv + 8) = time;
            Guid g = Guid.Empty;
            return ((delegate* unmanaged<IntPtr, Guid*, void*, int>)Fn(r, 8))(r, &g, pv);
        }

        public static int ReadSample(IntPtr r, uint i, out uint actual, out uint flags, out long time, out IntPtr sample)
        {
            uint a, f; long t; IntPtr s;
            int hr = ((delegate* unmanaged<IntPtr, uint, uint, uint*, uint*, long*, IntPtr*, int>)Fn(r, 9))(r, i, 0, &a, &f, &t, &s);
            actual = a; flags = f; time = t; sample = hr >= 0 ? s : IntPtr.Zero;
            return hr;
        }

        public static int Flush(IntPtr r, uint i) => ((delegate* unmanaged<IntPtr, uint, int>)Fn(r, 10))(r, i);

        /// <summary>A presentation attribute, as text.</summary>
        public static int PresentationAttribute(IntPtr r, uint i, Guid k, out string value)
        {
            var pv = stackalloc byte[24];
            new Span<byte>(pv, 24).Clear();
            int hr = ((delegate* unmanaged<IntPtr, uint, Guid*, void*, int>)Fn(r, 12))(r, i, &k, pv);
            value = hr >= 0 ? PropVariant(pv) : "";
            return hr;
        }

        // ---- attributes as text -------------------------------------------------------------------------

        public static readonly Dictionary<Guid, string> Names = new()
        {
            [new("48eba18e-f8c9-4687-bf11-0a74c9f96a8f")] = "MF_MT_MAJOR_TYPE",
            [new("f7e34c9a-42e8-4714-b74b-cb29d72c35e5")] = "MF_MT_SUBTYPE",
            [new("1652c33d-d6b2-4012-b834-72030849a37d")] = "MF_MT_FRAME_SIZE",
            [new("c459a2e8-3d2c-4e44-b132-fee5156c7bb0")] = "MF_MT_FRAME_RATE",
            [new("c6376a1e-8d0a-4027-be45-6d9a0ad39bb6")] = "MF_MT_PIXEL_ASPECT_RATIO",
            [new("e2724bb8-e676-4806-b4b2-a8d6efb44ccd")] = "MF_MT_INTERLACE_MODE",
            [new("644b4e48-1e02-4516-b0eb-c01ca9d49ac6")] = "MF_MT_DEFAULT_STRIDE",
            [new("c9173739-5e56-461c-b713-46fb995cb95f")] = "MF_MT_ALL_SAMPLES_INDEPENDENT",
            [new("b8ebefaf-b718-4e04-b0a9-116775e3321b")] = "MF_MT_FIXED_SIZE_SAMPLES",
            [new("dad3ab78-1990-408b-bce2-eba673dacc10")] = "MF_MT_SAMPLE_SIZE",
            [new("3afd0cee-18f2-4ba5-a110-8bea502e1f92")] = "MF_MT_COMPRESSED",
            [new("d7388766-18fe-48c6-a177-ee894867c8c4")] = "MF_MT_MINIMUM_DISPLAY_APERTURE",
            [new("66758743-7e5f-400d-980a-aa8596c85696")] = "MF_MT_GEOMETRIC_APERTURE",
            [new("79614dde-9187-48fb-b8c7-4d52689de649")] = "MF_MT_PAN_SCAN_APERTURE",
            [new("c21b8ee5-b956-4071-8daf-325edf5cab11")] = "MF_MT_VIDEO_NOMINAL_RANGE",
            [new("3e23d450-2c75-4d25-a00e-b91670d12327")] = "MF_MT_YUV_MATRIX",
            [new("dbfbe4d7-0740-4ee0-8192-850ab0e21935")] = "MF_MT_VIDEO_PRIMARIES",
            [new("5fb0fce9-be5c-4935-a811-ec838f8eed93")] = "MF_MT_TRANSFER_FUNCTION",
            [new("65df2370-c773-4c33-aa64-843e068efb0c")] = "MF_MT_VIDEO_CHROMA_SITING",
            [new("3c036de7-3ad0-4c9e-9216-ee6d6ac21cb3")] = "MF_MT_MPEG_SEQUENCE_HEADER",
            [new("b6bc765f-4c3b-40a4-bd51-2535b66fe09d")] = "MF_MT_USER_DATA",
            [new("ad76a80b-2d5c-4e0b-b375-64e520137036")] = "MF_MT_MPEG2_PROFILE",
            [new("96f66574-11c5-4015-8666-bff516436da7")] = "MF_MT_MPEG2_LEVEL",
            [new("20332624-fb0d-4d9e-bd0d-cbf6786c102e")] = "MF_MT_AVG_BITRATE",
            [new("c380465d-2271-428c-9b83-ecea3b4a85c1")] = "MF_MT_VIDEO_ROTATION",
            [new("d7be3fe0-2bc7-492d-b843-61a1919b70c3")] = "MF_MT_ORIGINAL_4CC",
            [new("261e9d83-9529-4b8f-a111-8b9c950a81a9")] = "MF_MT_MPEG4_SAMPLE_DESCRIPTION",
            [new("9aa7e155-b64a-4c1d-a500-455d600b6560")] = "MF_MT_MPEG4_CURRENT_SAMPLE_ENTRY",
            [new("37e48bf5-645e-4c5b-89de-ada9e29b696a")] = "MF_MT_AUDIO_NUM_CHANNELS",
            [new("5faeeae7-0290-4c31-9e8a-c534f68d9dba")] = "MF_MT_AUDIO_SAMPLES_PER_SECOND",
            [new("1aab75c8-cfef-451c-ab95-ac034b8e1731")] = "MF_MT_AUDIO_AVG_BYTES_PER_SECOND",
            [new("322de230-9eeb-43bd-ab7a-ff412251541d")] = "MF_MT_AUDIO_BLOCK_ALIGNMENT",
            [new("f2deb57f-40fa-4764-aa33-ed4f2d1ff669")] = "MF_MT_AUDIO_BITS_PER_SAMPLE",
            [new("55fb5765-644a-4caf-8479-938983bb1588")] = "MF_MT_AUDIO_CHANNEL_MASK",
            [new("d9bf8d6a-9530-4b7c-9ddf-ff6fd58bbd06")] = "MF_MT_AUDIO_VALID_BITS_PER_SAMPLE",
            [new("aab15aac-e13a-4995-9222-501ea15c6877")] = "MF_MT_AUDIO_SAMPLES_PER_BLOCK",
            [new("fb3b724a-cfb5-4319-aefe-6e42b2406132")] = "MF_MT_AUDIO_FLOAT_SAMPLES_PER_SECOND",
            [new("a901aaba-e037-458a-bdf6-545be2074042")] = "MF_MT_AUDIO_PREFER_WAVEFORMATEX",
            [new("bfbabe79-7434-4d1c-94f0-72a3b9e17188")] = "MF_MT_AAC_PAYLOAD_TYPE",
            [new("7632f0e6-9538-4d61-acda-ea29c8c14456")] = "MF_MT_AAC_AUDIO_PROFILE_LEVEL_INDICATION",
            [new("9cdf01d8-a0f0-43ba-b077-eaa06cbd728a")] = "MFSampleExtension_CleanPoint",
            [new("9cdf01d9-a0f0-43ba-b077-eaa06cbd728a")] = "MFSampleExtension_Discontinuity",
            [new("6c990d33-bb8e-477a-8598-0d5d96fcd88a")] = "MF_PD_DURATION",
            [new("6c990d34-bb8e-477a-8598-0d5d96fcd88a")] = "MF_PD_TOTAL_FILE_SIZE",
            [new("6c990d37-bb8e-477a-8598-0d5d96fcd88a")] = "MF_PD_MIME_TYPE",
            [new("6c990d35-bb8e-477a-8598-0d5d96fcd88a")] = "MF_PD_AUDIO_ENCODING_BITRATE",
            [new("6c990d36-bb8e-477a-8598-0d5d96fcd88a")] = "MF_PD_VIDEO_ENCODING_BITRATE",
            [new("6c990d3a-bb8e-477a-8598-0d5d96fcd88a")] = "MF_PD_LAST_MODIFIED_TIME",
            [new("6d23f5c8-c5d7-4a9b-9971-5d11f8bca880")] = "MF_SOURCE_READER_MEDIASOURCE_CHARACTERISTICS",
            [new("00af2180-bdc2-423c-abca-f503593bc121")] = "MF_SD_LANGUAGE",
            [new("4f1b099d-d314-41e5-a781-7fefaa4c501f")] = "MF_SD_STREAM_NAME",
            [new("73646976-0000-0010-8000-00aa00389b71")] = "MFMediaType_Video",
            [new("73647561-0000-0010-8000-00aa00389b71")] = "MFMediaType_Audio",
            [new("34363248-0000-0010-8000-00aa00389b71")] = "MFVideoFormat_H264",
            [new("3231564e-0000-0010-8000-00aa00389b71")] = "MFVideoFormat_NV12",
            [new("00000016-0000-0010-8000-00aa00389b71")] = "MFVideoFormat_RGB32",
            [new("00000015-0000-0010-8000-00aa00389b71")] = "MFVideoFormat_ARGB32",
            [new("00001610-0000-0010-8000-00aa00389b71")] = "MFAudioFormat_AAC",
            [new("00000001-0000-0010-8000-00aa00389b71")] = "MFAudioFormat_PCM",
            [new("00000003-0000-0010-8000-00aa00389b71")] = "MFAudioFormat_Float",
        };

        public static string Name(Guid g) => Names.TryGetValue(g, out var n) ? n : g.ToString();

        private static string Hex(byte* p, int n, int max = 64)
        {
            var sb = new StringBuilder();
            for (int i = 0; i < Math.Min(n, max); i++) sb.Append(p[i].ToString("x2"));
            if (n > max) sb.Append("...");
            return sb.ToString();
        }

        /// <summary>A PROPVARIANT as text; what it points to is freed.</summary>
        public static string PropVariant(byte* pv)
        {
            ushort vt = *(ushort*)pv;
            switch (vt)
            {
                case 19: return $"UI4 {*(uint*)(pv + 8)}";
                case 21: { ulong v = *(ulong*)(pv + 8); return $"UI8 {v} ({v >> 32}:{(uint)v})"; }
                case 20: return $"I8 {*(long*)(pv + 8)}";
                case 5: return $"R8 {*(double*)(pv + 8)}";
                case 72: { var g = **(Guid**)(pv + 8); Marshal.FreeCoTaskMem(*(IntPtr*)(pv + 8)); return $"GUID {Name(g)}"; }
                case 31: { var s = Marshal.PtrToStringUni(*(IntPtr*)(pv + 8)); Marshal.FreeCoTaskMem(*(IntPtr*)(pv + 8)); return $"STR \"{s}\""; }
                case 0x1011: { uint n = *(uint*)(pv + 8); byte* d = *(byte**)(pv + 16); var h = $"BLOB[{n}] {Hex(d, (int)n)}"; Marshal.FreeCoTaskMem((IntPtr)d); return h; }
                case 13: { IntPtr u = *(IntPtr*)(pv + 8); Release(u); return "UNKNOWN"; }
                default: return $"VT {vt}";
            }
        }

        /// <summary>Every attribute, a line each, sorted by name.</summary>
        public static List<string> Dump(IntPtr a)
        {
            var lines = new List<string>();
            uint n;
            ((delegate* unmanaged<IntPtr, uint*, int>)Fn(a, 30))(a, &n);
            for (uint i = 0; i < n; i++)
            {
                Guid k;
                var pv = stackalloc byte[24];
                new Span<byte>(pv, 24).Clear();
                if (((delegate* unmanaged<IntPtr, uint, Guid*, void*, int>)Fn(a, 31))(a, i, &k, pv) < 0) continue;
                lines.Add($"{Name(k)} = {PropVariant(pv)}");
            }
            lines.Sort(StringComparer.Ordinal);
            return lines;
        }

        public static byte[] SampleBytes(IntPtr s, out uint buffers)
        {
            uint n;
            ((delegate* unmanaged<IntPtr, uint*, int>)Fn(s, 39))(s, &n);
            buffers = n;
            IntPtr buf;
            if (((delegate* unmanaged<IntPtr, IntPtr*, int>)Fn(s, 41))(s, &buf) < 0) return Array.Empty<byte>();
            byte* p; uint max, cur;
            ((delegate* unmanaged<IntPtr, byte**, uint*, uint*, int>)Fn(buf, 3))(buf, &p, &max, &cur);
            var r = new ReadOnlySpan<byte>(p, (int)cur).ToArray();
            ((delegate* unmanaged<IntPtr, int>)Fn(buf, 4))(buf);
            Release(buf);
            return r;
        }

        public static long Duration(IntPtr s) { long d; return ((delegate* unmanaged<IntPtr, long*, int>)Fn(s, 37))(s, &d) >= 0 ? d : -1; }

        public static string HexOf(byte[] b, int max = 64) { fixed (byte* p = b) return Hex(p, b.Length, max); }
    }

    public sealed unsafe class MFSourceReaderProbe
    {
        private static readonly Guid MF_SOURCE_READER_ENABLE_VIDEO_PROCESSING = new("fb394f3d-ccf1-42ee-bbb3-f9b845d5681d");
        private static readonly Guid MF_PD_DURATION = new("6c990d33-bb8e-477a-8598-0d5d96fcd88a");

        private static string Hr(int hr) => $"0x{hr:x8}";

        /// <summary>The report of one file.</summary>
        internal static List<string> Report(Mp4TestFile f, string name)
        {
            var log = new List<string>();
            string path = f.WriteTemp(name);
            try
            {
                int hr = MFReader.Create(path, IntPtr.Zero, out var r);
                log.Add($"create {Hr(hr)}");
                if (hr < 0) return log;
                try
                {
                    foreach (var k in MFReader.Names.Keys.Where(k => MFReader.Names[k].StartsWith("MF_PD_") || MFReader.Names[k].StartsWith("MF_SOURCE_READER_MEDIASOURCE")))
                        if (MFReader.PresentationAttribute(r, MFReader.MEDIASOURCE, k, out var v) is var h && h >= 0) log.Add($"presentation {MFReader.Name(k)} = {v}");
                        else log.Add($"presentation {MFReader.Name(k)} {Hr(h)}");
                    for (uint i = 0; ; i++)
                    {
                        hr = MFReader.GetStreamSelection(r, i, out bool sel);
                        if (hr < 0) { log.Add($"stream {i}: {Hr(hr)}"); break; }
                        log.Add($"stream {i}: selected {sel}");
                        foreach (var k in new[] { new Guid("00af2180-bdc2-423c-abca-f503593bc121"), new Guid("4f1b099d-d314-41e5-a781-7fefaa4c501f") })
                            if (MFReader.PresentationAttribute(r, i, k, out var v) is var h && h >= 0) log.Add($"  sd {MFReader.Name(k)} = {v}");
                            else log.Add($"  sd {MFReader.Name(k)} {Hr(h)}");
                        for (uint n = 0; ; n++)
                        {
                            hr = MFReader.GetNativeMediaType(r, i, n, out var t);
                            if (hr < 0) { log.Add($"  native {n}: {Hr(hr)}"); break; }
                            log.Add($"  native {n}:");
                            log.AddRange(MFReader.Dump(t).Select(l => "    " + l));
                            MFReader.Release(t);
                        }
                        hr = MFReader.GetCurrentMediaType(r, i, out var c);
                        log.Add($"  current {Hr(hr)}{(hr >= 0 && MFReader.Dump(c).SequenceEqual(Native(r, i)) ? " (the native type)" : "")}");
                        MFReader.Release(c);
                    }
                    // samples, any stream, natively
                    MFReader.SetStreamSelection(r, MFReader.ANY, true);
                    for (int n = 0; n < 60; n++)
                    {
                        hr = MFReader.ReadSample(r, MFReader.ANY, out uint a, out uint fl, out long ts, out var s);
                        if (hr < 0) { log.Add($"read {Hr(hr)}"); break; }
                        if (s == IntPtr.Zero) { log.Add($"read: stream {a} flags 0x{fl:x} time {ts} no sample"); if ((fl & 2) != 0 && AllEnded(r, log)) break; continue; }
                        var bytes = MFReader.SampleBytes(s, out uint nb);
                        log.Add($"read: stream {a} flags 0x{fl:x} time {ts} duration {MFReader.Duration(s)} buffers {nb} bytes {bytes.Length} {MFReader.HexOf(bytes, 24)}");
                        log.AddRange(MFReader.Dump(s).Select(l => "    " + l));
                        MFReader.Release(s);
                    }
                    // the first video stream again, after a seek to the middle
                    hr = MFReader.SetCurrentPosition(r, 1_200_000);
                    log.Add($"seek 0.12 s {Hr(hr)}");
                    for (int n = 0; n < 3 && hr >= 0; n++)
                    {
                        int h = MFReader.ReadSample(r, MFReader.FIRST_VIDEO, out uint a, out uint fl, out long ts, out var s);
                        log.Add($"  read video {Hr(h)}: stream {a} flags 0x{fl:x} time {ts}{(s == IntPtr.Zero ? " no sample" : "")}");
                        MFReader.Release(s);
                    }
                    // a deselected stream
                    MFReader.SetStreamSelection(r, 1, false);
                    log.Add($"read deselected {Hr(MFReader.ReadSample(r, 1, out _, out _, out _, out var ds))}");
                    MFReader.Release(ds);
                    log.Add($"read out of range {Hr(MFReader.ReadSample(r, 7, out _, out _, out _, out var os))}");
                    MFReader.Release(os);
                }
                finally { MFReader.Release(r); }

                // decoded: NV12, then RGB32 without and with video processing
                foreach (var (sub, vp) in new[] { (new Guid("3231564e-0000-0010-8000-00aa00389b71"), false), (new Guid("00000016-0000-0010-8000-00aa00389b71"), false), (new Guid("00000016-0000-0010-8000-00aa00389b71"), true) })
                {
                    IntPtr attrs = MFReader.Attributes();
                    if (vp) MFReader.SetU32(attrs, MF_SOURCE_READER_ENABLE_VIDEO_PROCESSING, 1);
                    MFReader.Create(path, attrs, out r);
                    MFReader.Release(attrs);
                    var t = MFReader.MediaType();
                    MFReader.SetGuid(t, new Guid("48eba18e-f8c9-4687-bf11-0a74c9f96a8f"), new Guid("73646976-0000-0010-8000-00aa00389b71"));
                    MFReader.SetGuid(t, new Guid("f7e34c9a-42e8-4714-b74b-cb29d72c35e5"), sub);
                    hr = MFReader.SetCurrentMediaType(r, MFReader.FIRST_VIDEO, t);
                    MFReader.Release(t);
                    log.Add($"decode to {MFReader.Name(sub)}{(vp ? " with video processing" : "")}: {Hr(hr)}");
                    if (hr >= 0)
                    {
                        MFReader.GetCurrentMediaType(r, MFReader.FIRST_VIDEO, out var c);
                        log.Add("  current:");
                        log.AddRange(MFReader.Dump(c).Select(l => "    " + l));
                        MFReader.Release(c);
                        for (int n = 0; n < 10; n++)
                        {
                            int h = MFReader.ReadSample(r, MFReader.FIRST_VIDEO, out uint a, out uint fl, out long ts, out var s);
                            if (h < 0) { log.Add($"  read {Hr(h)}"); break; }
                            if (s == IntPtr.Zero) { log.Add($"  read: flags 0x{fl:x} time {ts} no sample"); if ((fl & 2) != 0) break; continue; }
                            var bytes = MFReader.SampleBytes(s, out uint nb);
                            log.Add($"  read: flags 0x{fl:x} time {ts} duration {MFReader.Duration(s)} buffers {nb} bytes {bytes.Length} {MFReader.HexOf(bytes, 16)}");
                            if (n == 0) log.AddRange(MFReader.Dump(s).Select(l => "      " + l));
                            if ((fl & 0x20) != 0)
                            {
                                MFReader.GetCurrentMediaType(r, MFReader.FIRST_VIDEO, out c);
                                log.Add("  current now:");
                                log.AddRange(MFReader.Dump(c).Select(l => "    " + l));
                                MFReader.Release(c);
                            }
                            MFReader.Release(s);
                        }
                    }
                    MFReader.Release(r);
                }
            }
            finally { try { System.IO.File.Delete(path); } catch { } }
            return log;
        }

        private static readonly Guid MFMediaType_Audio = new("73647561-0000-0010-8000-00aa00389b71");
        private static readonly Guid PCM = new("00000001-0000-0010-8000-00aa00389b71"), Float = new("00000003-0000-0010-8000-00aa00389b71");
        private static readonly Guid MFT_CATEGORY_AUDIO_DECODER = new("9ea73fb4-ef7a-4559-8d5d-719d8f0426c7");

        [DllImport("mfplat.dll", EntryPoint = "MFTEnumEx")] private static extern int WinMFTEnumEx(Guid category, uint flags, MFT_REGISTER_TYPE_INFO* input, MFT_REGISTER_TYPE_INFO* output, IntPtr** activates, uint* count);

        /// <summary>The audio of a file: decoded to PCM and to float, the samples' times and sizes, the signal per channel.</summary>
        internal static List<string> AudioReport(string path)
        {
            var log = new List<string>();
            int hr = MFReader.Create(path, IntPtr.Zero, out var r);
            log.Add($"create {Hr(hr)}");
            if (hr < 0) return log;
            foreach (var k in MFReader.Names.Keys.Where(k => MFReader.Names[k].StartsWith("MF_PD_") || MFReader.Names[k].StartsWith("MF_SOURCE_READER_MEDIASOURCE")))
                if (MFReader.PresentationAttribute(r, MFReader.MEDIASOURCE, k, out var pv) is var ph && ph >= 0) log.Add($"presentation {MFReader.Name(k)} = {pv}");
                else log.Add($"presentation {MFReader.Name(k)} {Hr(ph)}");
            uint streams = 0;
            while (MFReader.GetStreamSelection(r, streams, out _) >= 0) streams++;
            log.Add($"streams {streams}");
            MFReader.GetNativeMediaType(r, MFReader.FIRST_AUDIO, 0, out var native);
            log.Add("native:");
            log.AddRange(MFReader.Dump(native).Select(l => "  " + l));
            // the samples as the source cuts them
            int nsamples = 0; long total = 0;
            for (int n = 0; n < 2000; n++)
            {
                int h = MFReader.ReadSample(r, MFReader.FIRST_AUDIO, out _, out uint f, out long ts, out var smp);
                if (h < 0) { log.Add($"  native read {Hr(h)}"); break; }
                if (smp == IntPtr.Zero) { log.Add($"  native read: flags 0x{f:x} time {ts} no sample"); if ((f & 2) != 0) break; continue; }
                var b = MFReader.SampleBytes(smp, out uint nb);
                if (n < 3) { log.Add($"  native read: flags 0x{f:x} time {ts} duration {MFReader.Duration(smp)} buffers {nb} bytes {b.Length} {MFReader.HexOf(b, 8)}"); log.AddRange(MFReader.Dump(smp).Select(l => "      " + l)); }
                nsamples++; total += b.Length;
                MFReader.Release(smp);
            }
            log.Add($"  native samples {nsamples} bytes {total}");
            MFReader.Release(r);

            // the decoders, by themselves
            var input = new MFT_REGISTER_TYPE_INFO { guidMajorType = MFMediaType_Audio, guidSubtype = MFReader.G(native, new Guid("f7e34c9a-42e8-4714-b74b-cb29d72c35e5")) };
            IntPtr* acts; uint count;
            hr = OperatingSystem.IsWindows() ? WinMFTEnumEx(MFT_CATEGORY_AUDIO_DECODER, 1 | 4 | 0x20 | 0x40, &input, null, &acts, &count) : MFExports.MFTEnumEx(MFT_CATEGORY_AUDIO_DECODER, 1 | 4 | 0x20 | 0x40, &input, null, &acts, &count);
            log.Add($"MFTEnumEx {Hr(hr)} count {(hr >= 0 ? count : 0)}");
            for (uint i = 0; hr >= 0 && i < count; i++)
            {
                IntPtr a = acts[i];
                log.Add($"  decoder {i}:");
                log.AddRange(MFReader.Dump(a).Where(l => l.Contains("314ffbae") || l.Contains("6821c42b") || l.Contains("4276c9b1") || l.Contains("8eae8cf3") || l.Contains("FRIENDLY")).Select(l => "    " + l));
                if (i == 0)
                {
                    IntPtr t; var iid = new Guid("bf94c121-5b05-4e6f-8000-ba598961414d");
                    int h = ((delegate* unmanaged<IntPtr, Guid*, IntPtr*, int>)MFReader.Fn(a, 33))(a, &iid, &t);
                    log.Add($"    activate {Hr(h)}");
                    if (h >= 0)
                    {
                        h = ((delegate* unmanaged<IntPtr, uint, IntPtr, uint, int>)MFReader.Fn(t, 15))(t, 0, native, 0);
                        log.Add($"    SetInputType {Hr(h)}");
                        for (uint n = 0; n < 12; n++)
                        {
                            IntPtr ot;
                            h = ((delegate* unmanaged<IntPtr, uint, uint, IntPtr*, int>)MFReader.Fn(t, 14))(t, 0, n, &ot);
                            if (h < 0) { log.Add($"    output {n}: {Hr(h)}"); break; }
                            log.Add($"    output {n}:");
                            log.AddRange(MFReader.Dump(ot).Select(l => "      " + l));
                            MFReader.Release(ot);
                        }
                        var info = stackalloc uint[6];
                        h = ((delegate* unmanaged<IntPtr, uint, uint*, int>)MFReader.Fn(t, 6))(t, 0, info);
                        log.Add($"    input stream info {Hr(h)}: latency {*(long*)info} flags 0x{info[2]:x} size {info[3]} lookahead {info[4]} alignment {info[5]}");
                        MFReader.Release(t);
                    }
                }
                MFReader.Release(a);
            }
            if (hr >= 0 && count > 0) Marshal.FreeCoTaskMem((IntPtr)acts);
            MFReader.Release(native);

            foreach (var (sub, bits, rate, chans) in new[] { (PCM, 0u, 0u, 0u), (PCM, 16u, 0u, 0u), (Float, 0u, 0u, 0u), (PCM, 24u, 0u, 0u), (PCM, 16u, 48000u, 0u), (PCM, 16u, 0u, 1u), (Float, 0u, 22050u, 1u), (PCM, 16u, 0u, 2u), (PCM, 16u, 8000u, 6u) })
            {
                MFReader.Create(path, IntPtr.Zero, out r);
                var t = MFReader.MediaType();
                MFReader.SetGuid(t, new Guid("48eba18e-f8c9-4687-bf11-0a74c9f96a8f"), MFMediaType_Audio);
                MFReader.SetGuid(t, new Guid("f7e34c9a-42e8-4714-b74b-cb29d72c35e5"), sub);
                if (bits > 0) MFReader.SetU32(t, new Guid("f2deb57f-40fa-4764-aa33-ed4f2d1ff669"), bits);
                if (rate > 0) MFReader.SetU32(t, new Guid("5faeeae7-0290-4c31-9e8a-c534f68d9dba"), rate);
                if (chans > 0) MFReader.SetU32(t, new Guid("37e48bf5-645e-4c5b-89de-ada9e29b696a"), chans);
                hr = MFReader.SetCurrentMediaType(r, MFReader.FIRST_AUDIO, t);
                MFReader.Release(t);
                log.Add($"decode to {MFReader.Name(sub)}{(bits > 0 ? $" {bits} bits" : "")}{(rate > 0 ? $" {rate} Hz" : "")}{(chans > 0 ? $" {chans} channels" : "")}: {Hr(hr)}");
                if (Environment.GetEnvironmentVariable("MF_PROBE_OUT") is { Length: > 0 }) Progress("  " + log[^1]);
                if (hr >= 0)
                {
                    MFReader.GetCurrentMediaType(r, MFReader.FIRST_AUDIO, out var c);
                    log.Add("  current:");
                    log.AddRange(MFReader.Dump(c).Select(l => "    " + l));
                    uint ch = MFReader.U32(c, new Guid("37e48bf5-645e-4c5b-89de-ada9e29b696a"), 1), bps = MFReader.U32(c, new Guid("f2deb57f-40fa-4764-aa33-ed4f2d1ff669"), 16);
                    bool fl = MFReader.G(c, new Guid("f7e34c9a-42e8-4714-b74b-cb29d72c35e5")) == Float;
                    MFReader.Release(c);
                    var all = new List<byte>();
                    for (int n = 0; n < 400; n++)
                    {
                        int h = MFReader.ReadSample(r, MFReader.FIRST_AUDIO, out _, out uint f, out long ts, out var smp);
                        if (h < 0) { log.Add($"  read {Hr(h)}"); break; }
                        if (smp == IntPtr.Zero) { log.Add($"  read: flags 0x{f:x} time {ts} no sample"); if ((f & 2) != 0) break; continue; }
                        var b = MFReader.SampleBytes(smp, out uint nb);
                        if (n < 4 || (f & 0x20) != 0) log.Add($"  read: flags 0x{f:x} time {ts} duration {MFReader.Duration(smp)} buffers {nb} bytes {b.Length}");
                        if (n == 0) log.AddRange(MFReader.Dump(smp).Select(l => "      " + l));
                        all.AddRange(b);
                        MFReader.Release(smp);
                    }
                    // the signal: per channel, the frames, the first loud one, the RMS
                    int bytesPer = (int)bps / 8, frame = bytesPer * (int)ch;
                    int frames = all.Count / Math.Max(1, frame);
                    var arr = all.ToArray();
                    for (int k = 0; k < ch; k++)
                    {
                        double sum = 0; int first = -1;
                        for (int i = 0; i < frames; i++)
                        {
                            int o = i * frame + k * bytesPer;
                            double v = fl ? BitConverter.ToSingle(arr, o) : bytesPer == 2 ? BitConverter.ToInt16(arr, o) / 32768.0 : bytesPer == 3 ? ((arr[o] | arr[o + 1] << 8 | (sbyte)arr[o + 2] << 16) / 8388608.0) : BitConverter.ToInt32(arr, o) / 2147483648.0;
                            sum += v * v;
                            if (first < 0 && Math.Abs(v) > 0.03) first = i;
                        }
                        log.Add(FormattableString.Invariant($"  channel {k}: frames {frames} first loud {first} rms {Math.Sqrt(sum / Math.Max(1, frames)):F4}"));
                    }
                }
                MFReader.Release(r);
            }
            return log;
        }

        /// <summary>Clips made with ffmpeg (testsrc2, 128x96, 8 frames, a key frame every 4): their decoding is exact, VP9's, AV1's and HEVC's alike.</summary>
        internal static readonly string[] VideoFiles = { "clip-vp9.mp4", "clip-av1.mp4", "clip-hevc.mp4" };

        /// <summary>Clips of video and audio: WebM (VP9, Opus) and Matroska (HEVC, FLAC).</summary>
        internal static readonly string[] AvFiles = { "clip.webm", "clip.mkv" };

        /// <summary>A video file: its native type, its first samples, its frames decoded to NV12 (a hash of each).</summary>
        internal static List<string> VideoReport(string path)
        {
            var log = new List<string>();
            int hr = MFReader.Create(path, IntPtr.Zero, out var r);
            log.Add($"create {Hr(hr)}");
            if (hr < 0) return log;
            foreach (var k in MFReader.Names.Keys.Where(k => MFReader.Names[k].StartsWith("MF_PD_") || MFReader.Names[k].StartsWith("MF_SOURCE_READER_MEDIASOURCE")))
                if (MFReader.PresentationAttribute(r, MFReader.MEDIASOURCE, k, out var pv) is var ph && ph >= 0) log.Add($"presentation {MFReader.Name(k)} = {pv}");
                else log.Add($"presentation {MFReader.Name(k)} {Hr(ph)}");
            MFReader.GetNativeMediaType(r, MFReader.FIRST_VIDEO, 0, out var native);
            log.Add("native:");
            log.AddRange(MFReader.Dump(native).Select(l => "  " + l));
            MFReader.Release(native);
            for (int n = 0; n < 40; n++)
            {
                int h = MFReader.ReadSample(r, MFReader.FIRST_VIDEO, out _, out uint f, out long ts, out var smp);
                if (h < 0) { log.Add($"  native read {Hr(h)}"); break; }
                if (smp == IntPtr.Zero) { log.Add($"  native read: flags 0x{f:x} time {ts} no sample"); if ((f & 2) != 0) break; continue; }
                var b = MFReader.SampleBytes(smp, out uint nb);
                log.Add($"  native read: flags 0x{f:x} time {ts} duration {MFReader.Duration(smp)} bytes {b.Length} {MFReader.HexOf(b, 12)}");
                log.AddRange(MFReader.Dump(smp).Select(l => "      " + l));
                MFReader.Release(smp);
            }
            MFReader.Release(r);
            MFReader.Create(path, IntPtr.Zero, out r);
            var t = MFReader.MediaType();
            MFReader.SetGuid(t, new Guid("48eba18e-f8c9-4687-bf11-0a74c9f96a8f"), new Guid("73646976-0000-0010-8000-00aa00389b71"));
            MFReader.SetGuid(t, new Guid("f7e34c9a-42e8-4714-b74b-cb29d72c35e5"), new Guid("3231564e-0000-0010-8000-00aa00389b71"));
            hr = MFReader.SetCurrentMediaType(r, MFReader.FIRST_VIDEO, t);
            MFReader.Release(t);
            log.Add($"decode to NV12: {Hr(hr)}");
            if (hr >= 0)
            {
                MFReader.GetCurrentMediaType(r, MFReader.FIRST_VIDEO, out var c);
                log.Add("  current:");
                log.AddRange(MFReader.Dump(c).Select(l => "    " + l));
                MFReader.Release(c);
                for (int n = 0; n < 40; n++)
                {
                    int h = MFReader.ReadSample(r, MFReader.FIRST_VIDEO, out _, out uint f, out long ts, out var smp);
                    if (h < 0) { log.Add($"  read {Hr(h)}"); break; }
                    if (smp == IntPtr.Zero) { log.Add($"  read: flags 0x{f:x} time {ts} no sample"); if ((f & 2) != 0) break; continue; }
                    var b = MFReader.SampleBytes(smp, out _);
                    // the picture's 128x96 of the decoder's (maybe larger) frame
                    MFReader.GetCurrentMediaType(r, MFReader.FIRST_VIDEO, out c);
                    ulong fs = MFReader.U64(c, new Guid("1652c33d-d6b2-4012-b834-72030849a37d"));
                    MFReader.Release(c);
                    int w = (int)(fs >> 32), hh = (int)(uint)fs;
                    var pic = new byte[128 * 96 * 3 / 2];
                    if (w >= 128 && hh >= 96 && b.Length >= w * hh * 3 / 2)
                    {
                        for (int y = 0; y < 96; y++) Array.Copy(b, y * w, pic, y * 128, 128);
                        for (int y = 0; y < 48; y++) Array.Copy(b, w * hh + y * w, pic, 128 * 96 + y * 128, 128);
                    }
                    var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(pic)).ToLowerInvariant()[..16];
                    log.Add($"  read: flags 0x{f:x} time {ts} duration {MFReader.Duration(smp)} bytes {b.Length} frame {w}x{hh} picture {hash}");
                    if ((f & 0x20) != 0)
                    {
                        MFReader.GetCurrentMediaType(r, MFReader.FIRST_VIDEO, out c);
                        log.Add("  current now:");
                        log.AddRange(MFReader.Dump(c).Select(l => "    " + l));
                        MFReader.Release(c);
                    }
                    MFReader.Release(smp);
                }
            }
            MFReader.Release(r);
            return log;
        }

        private static List<string> Native(IntPtr r, uint i)
        {
            MFReader.GetNativeMediaType(r, i, 0, out var t);
            var d = MFReader.Dump(t);
            MFReader.Release(t);
            return d;
        }

        private static bool AllEnded(IntPtr r, List<string> log) => log.Count(l => l.StartsWith("read:") && l.Contains("no sample")) >= 2;

        /// <summary>Audio made with afconvert, macOS's: AAC of a stereo tone of 440 and 660 Hz, ALAC of a mono one.</summary>
        internal static readonly string[] AudioFiles = { "tone-aac.m4a", "tone-alac.m4a", "tone.aac", "tone.flac", "tone.mp3", "tone-notag.mp3", "tone.wav" };

        /// <summary>The audio file's path: of the media folder, or (the WAV) written here: the tone as 16-bit stereo PCM.</summary>
        internal static string AudioPath(string name)
        {
            if (name != "tone.wav") return System.IO.Path.Combine(AppContext.BaseDirectory, "MediaFoundation", "Media", name);
            string path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"wpfmf-tone-{Environment.ProcessId}.wav");
            const int rate = 44100, frames = rate / 2;
            using var w = new System.IO.BinaryWriter(System.IO.File.Create(path));
            w.Write("RIFF"u8); w.Write(36 + frames * 4); w.Write("WAVE"u8);
            w.Write("fmt "u8); w.Write(16); w.Write((short)1); w.Write((short)2); w.Write(rate); w.Write(rate * 4); w.Write((short)4); w.Write((short)16);
            w.Write("data"u8); w.Write(frames * 4);
            for (int i = 0; i < frames; i++) { w.Write((short)(12000 * Math.Sin(2 * Math.PI * 440 * i / rate))); w.Write((short)(12000 * Math.Sin(2 * Math.PI * 660 * i / rate))); }
            return path;
        }

        /// <summary>The files of the report (SourceReader.windows.txt is Windows' of them).</summary>
        internal static (string Name, Mp4TestFile File)[] Files() => new[]
        {
            ("plain", new Mp4TestFile()),
            ("moov-last", new Mp4TestFile { MoovFirst = false }),
            ("fragmented", new Mp4TestFile { Fragmented = true }),
            ("audio-trak-first", new Mp4TestFile { AudioTrakFirst = true }),
            ("audio-id-1", new Mp4TestFile { VideoTrackId = 2, AudioTrackId = 1 }),
            ("audio-first-id-1", new Mp4TestFile { AudioTrakFirst = true, VideoTrackId = 2, AudioTrackId = 1 }),
            ("cropped-rotated", new Mp4TestFile { Width = 64, Height = 48, CropRight = 8, CropBottom = 8, Rotation = 90, Audio = false }),
        };

        private static void Progress(string what) => System.IO.File.AppendAllText(Environment.GetEnvironmentVariable("MF_PROBE_OUT") + ".progress", what + "\n");

        [Fact]
        public void Probe()
        {
            Assert.SkipWhen(Environment.GetEnvironmentVariable("MF_PROBE_OUT") is not { Length: > 0 }, "MF_PROBE_OUT=<file> writes the report");
            var all = new List<string>();
            foreach (var (name, f) in Files())
            {
                all.Add($"==== {name}");
                try { all.AddRange(Report(f, name)); }
                catch (Exception e) { all.Add("failed: " + e); }
            }
            foreach (var m in AudioFiles)
            {
                all.Add($"==== {m}");
                Progress(m);
                try { all.AddRange(AudioReport(AudioPath(m))); }
                catch (Exception e) { all.Add("failed: " + e); }
            }
            foreach (var m in VideoFiles)
            {
                all.Add($"==== {m}");
                Progress(m);
                try { all.AddRange(VideoReport(AudioPath(m))); }
                catch (Exception e) { all.Add("failed: " + e); }
            }
            foreach (var m in AvFiles)
            {
                all.Add($"==== {m}");
                Progress(m);
                try { all.AddRange(VideoReport(AudioPath(m))); Progress(m + " audio"); all.AddRange(AudioReport(AudioPath(m))); }
                catch (Exception e) { all.Add("failed: " + e); }
            }
            // over HTTP (MF_PROBE_HTTP=<base URL> serving the media folder, with ranges)
            if (Environment.GetEnvironmentVariable("MF_PROBE_HTTP") is { Length: > 0 } http)
            {
                foreach (var m in new[] { "clip-vp9.mp4", "tone.mp3" })
                {
                    all.Add($"==== http {m}");
                    Progress("http " + m);
                    try { all.AddRange(m.EndsWith(".mp3") ? AudioReport(http.TrimEnd('/') + "/" + m) : VideoReport(http.TrimEnd('/') + "/" + m)); }
                    catch (Exception e) { all.Add("failed: " + e); }
                }
                all.Add("==== http missing.mp4");
                all.Add($"create {Hr(MFReader.Create(http.TrimEnd('/') + "/missing.mp4", IntPtr.Zero, out var mr))}");
                MFReader.Release(mr);
            }
            System.IO.File.WriteAllLines(Environment.GetEnvironmentVariable("MF_PROBE_OUT")!, all);
            System.IO.File.Delete(Environment.GetEnvironmentVariable("MF_PROBE_OUT") + ".progress");
        }
    }
}
