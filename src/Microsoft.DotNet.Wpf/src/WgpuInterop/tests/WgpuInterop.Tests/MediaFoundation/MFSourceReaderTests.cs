// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// The source reader (Documentation/media-foundation-shim.md, "The source reader"): what it does with MP4
// files against what Windows' does (SourceReader.windows.txt, MFSourceReaderProbe's report on Windows),
// and the frames it decodes, synchronously and through a callback, into memory and into textures. On
// Windows the tests drive Windows' own reader: the same expectations hold of it.
//

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using Microsoft.Wpf.Interop.WebGpu.Direct3D;
using Microsoft.Wpf.Interop.WebGpu.MediaFoundation;
using Xunit;

namespace WgpuInterop.Tests.MediaFoundation
{
    public sealed unsafe class MFSourceReaderTests
    {
        private static readonly Guid MF_MT_MAJOR_TYPE = new("48eba18e-f8c9-4687-bf11-0a74c9f96a8f"), MF_MT_SUBTYPE = new("f7e34c9a-42e8-4714-b74b-cb29d72c35e5");
        private static readonly Guid MFMediaType_Video = new("73646976-0000-0010-8000-00aa00389b71");
        private static readonly Guid NV12 = new("3231564e-0000-0010-8000-00aa00389b71"), RGB32 = new("00000016-0000-0010-8000-00aa00389b71");
        private static readonly Guid MF_SOURCE_READER_ENABLE_VIDEO_PROCESSING = new("fb394f3d-ccf1-42ee-bbb3-f9b845d5681d");
        private static readonly Guid MF_SOURCE_READER_ASYNC_CALLBACK = new("1e3dbeac-bb43-4c35-b507-cd644464c965"), MF_SOURCE_READER_D3D_MANAGER = new("ec822da2-e1e9-4b29-a0d8-563c719f5269");
        private static readonly Guid IID_IMFDXGIBuffer = new("e7174cfa-1c9e-48b1-8866-626226bfc258"), IID_ID3D11Texture2D = new("6f15aaf2-d208-4e89-9ab4-489535d34f9c");

        [DllImport("mfplat.dll", EntryPoint = "MFCreateFile")] private static extern int WinMFCreateFile(uint access, uint open, uint flags, [MarshalAs(UnmanagedType.LPWStr)] string url, IntPtr* stream);
        [DllImport("mfreadwrite.dll", EntryPoint = "MFCreateSourceReaderFromByteStream")] private static extern int WinMFCreateSourceReaderFromByteStream(IntPtr stream, IntPtr attributes, IntPtr* reader);

        private static void Ok(int hr, string what) => Assert.True(hr >= 0, $"{what}: 0x{hr:x8}");

        // ---- against Windows --------------------------------------------------------------------------

        /// <summary>Of Windows' own attributes of its samples, those of its pipeline's insides (tokens, decoder statistics).</summary>
        private static readonly string[] s_internal = { "8294da66-f328", "424c754c-97c8", "79ea74df-a740", "b1d5830a-deb8", "b4dd4a8c-0beb", "19124e7c-ad4b-465f-bb18-20186287b6af = BLOB[0]" };

        /// <summary>
        /// The report of each file, as Windows' (in SourceReader.windows.txt) without its pipeline's own attributes: every line the
        /// same, and the samples each stream gives in the same order -- the order of a read of any stream between them aside.
        /// </summary>
        [Fact]
        public void TheReader_DoesWithEachFileWhatWindowsReaderDoes()
        {
            var windows = File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "MediaFoundation", "SourceReader.windows.txt"));
            var ours = new List<string>();
            foreach (var (name, f) in MFSourceReaderProbe.Files())
            {
                ours.Add($"==== {name}");
                ours.AddRange(MFSourceReaderProbe.Report(f, name));
            }
            foreach (var m in MFSourceReaderProbe.AudioFiles)
            {
                ours.Add($"==== {m}");
                ours.AddRange(MFSourceReaderProbe.AudioReport(MFSourceReaderProbe.AudioPath(m)));
            }
            foreach (var m in MFSourceReaderProbe.VideoFiles)
            {
                ours.Add($"==== {m}");
                ours.AddRange(MFSourceReaderProbe.VideoReport(MFSourceReaderProbe.AudioPath(m)));
            }
            foreach (var m in MFSourceReaderProbe.AvFiles)
            {
                ours.Add($"==== {m}");
                ours.AddRange(MFSourceReaderProbe.VideoReport(MFSourceReaderProbe.AudioPath(m)));
                ours.Add("---- audio");
                ours.AddRange(MFSourceReaderProbe.AudioReport(MFSourceReaderProbe.AudioPath(m)));
            }
            static List<string> Clean(IEnumerable<string> lines)
            {
                var r = new List<string>();
                string file = "";
                bool resampled = false;
                foreach (var l in lines)
                {
                    if (l.StartsWith("==== ")) file = l;
                    if (s_internal.Any(l.Contains)) continue;
                    // resampled, or of a file whose frames the decoder finds itself: the packets are the decoder's and the
                    // resampler's, each system's own (the signal is compared, below)
                    if (l.StartsWith("decode to ")) resampled = l.Contains(" Hz") || file is "==== tone.aac" or "==== tone.flac" or "==== tone.mp3" or "==== tone-notag.mp3";
                    if (resampled && l.StartsWith("  read: flags 0x0 ")) continue;
                    // Matroska's FLAC: Windows' bytes per second are of a rule the file does not show; Opus: Windows' decoder hands
                    // the sound out 274 frames later than the pre-skip puts it (the frames the same): the level and onset not compared
                    if (file == "==== clip.mkv" && l.StartsWith("  MF_MT_AUDIO_AVG_BYTES_PER_SECOND")) continue;
                    if (file == "==== clip.webm" && l.StartsWith("  channel ")) { r.Add(string.Join(' ', l.Split(' ').Take(6))); continue; }
                    // ADTS has no length of its own: Windows estimates its duration and bitrate from its start, the shim from all of it
                    if (file == "==== tone.aac" && (l.StartsWith("presentation MF_PD_DURATION") || l.StartsWith("presentation MF_PD_AUDIO_ENCODING_BITRATE") || l.StartsWith("  MF_MT_AUDIO_AVG_BYTES_PER_SECOND"))) continue;
                    // Windows takes no bitrate of an esds whose descriptors' lengths are of four bytes (as Apple's tools write
                    // them), and makes one of its own the shim does not: the esds' is the shim's
                    if (file == "==== tone-aac.m4a" && (l.StartsWith("  MF_MT_AUDIO_AVG_BYTES_PER_SECOND") || l.StartsWith("  MF_MT_AVG_BITRATE"))) continue;
                    r.Add(l);
                }
                return r;
            }
            var w = Clean(windows); var o = Clean(ours);

            // a read of any stream: the samples (with their attributes) by stream, each in order
            static (List<string> Others, Dictionary<string, List<string>> Reads) Split(List<string> lines)
            {
                var rest = new List<string>(); var reads = new Dictionary<string, List<string>>();
                string file = "", current = "";
                List<string>? into = null;
                foreach (var l in lines)
                {
                    if (l.StartsWith("==== ")) file = l;
                    if (l.StartsWith("read: stream "))
                    {
                        current = file + " " + l.Split(' ')[2];
                        if (!reads.TryGetValue(current, out into)) reads[current] = into = new();
                        into.Add(l);
                        continue;
                    }
                    if (into != null && l.StartsWith("    ") && !l.StartsWith("     ")) { into.Add(l); continue; }
                    into = null;
                    rest.Add(l);
                }
                return (rest, reads);
            }
            var (wr, wreads) = Split(w);
            var (or, oreads) = Split(o);
            for (int i = 0; i < Math.Max(wr.Count, or.Count); i++)
                Assert.True(i < wr.Count && i < or.Count && (wr[i] == or[i] || SameSignal(wr[i], or[i])), $"line {i}: Windows '{(i < wr.Count ? wr[i] : "(none)")}', ours '{(i < or.Count ? or[i] : "(none)")}'");
            Assert.Equal(wreads.Keys.OrderBy(k => k), oreads.Keys.OrderBy(k => k));
            foreach (var k in wreads.Keys)
                Assert.True(wreads[k].SequenceEqual(oreads[k]), $"{k}: Windows\n{string.Join("\n", wreads[k])}\nours\n{string.Join("\n", oreads[k])}");
        }

        /// <summary>
        /// A decoded channel's line ("channel k: frames n first loud f rms r"): the frames within two (the same unless resampled),
        /// the sound beginning within two frames and its level within 0.002 -- each system's decoder and resampler its own.
        /// </summary>
        private static bool SameSignal(string a, string b)
        {
            var x = a.Trim().Split(' '); var y = b.Trim().Split(' ');
            if (x.Length != 9 || y.Length != 9 || x[0] != "channel" || y[0] != "channel" || x[1] != y[1]) return false;
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            return Math.Abs(int.Parse(x[3], inv) - int.Parse(y[3], inv)) <= 2 && Math.Abs(int.Parse(x[6], inv) - int.Parse(y[6], inv)) <= 2 && Math.Abs(double.Parse(x[8], inv) - double.Parse(y[8], inv)) <= 0.002;
        }

        // ---- decoding -----------------------------------------------------------------------------------

        private static IntPtr Open(Mp4TestFile f, string name, Action<IntPtr>? attributes, out string path)
        {
            path = f.WriteTemp(name);
            IntPtr a = IntPtr.Zero;
            if (attributes != null) { a = MFReader.Attributes(); attributes(a); }
            int hr = MFReader.Create(path, a, out var r);
            MFReader.Release(a);
            Ok(hr, "MFCreateSourceReaderFromURL");
            return r;
        }

        private static void SetVideoType(IntPtr r, Guid subtype)
        {
            var t = MFReader.MediaType();
            MFReader.SetGuid(t, MF_MT_MAJOR_TYPE, MFMediaType_Video);
            MFReader.SetGuid(t, MF_MT_SUBTYPE, subtype);
            int hr = MFReader.SetCurrentMediaType(r, MFReader.FIRST_VIDEO, t);
            MFReader.Release(t);
            Ok(hr, "SetCurrentMediaType");
        }

        /// <summary>Every frame of the first video stream, each sample handed to the function before it is released.</summary>
        private static List<T> ReadAll<T>(IntPtr r, Func<IntPtr, T> each)
        {
            var frames = new List<T>();
            for (int n = 0; n < 100; n++)
            {
                Ok(MFReader.ReadSample(r, MFReader.FIRST_VIDEO, out _, out uint flags, out _, out var s), "ReadSample");
                if (s != IntPtr.Zero) { frames.Add(each(s)); MFReader.Release(s); }
                if ((flags & 2) != 0) return frames;
            }
            throw new InvalidOperationException("no end of the stream");
        }

        [Fact]
        public void TheReader_DecodesTheFramesToNv12()
        {
            var f = new Mp4TestFile();
            IntPtr r = Open(f, "nv12", null, out var path);
            try
            {
                SetVideoType(r, NV12);
                var frames = ReadAll(r, s => MFReader.SampleBytes(s, out _));
                Assert.Equal(f.Frames, frames.Count);
                for (int k = 0; k < frames.Count; k++) Assert.True(f.Pictures[k].AsSpan().SequenceEqual(frames[k]), $"frame {k} differs");
            }
            finally { MFReader.Release(r); File.Delete(path); }
        }

        /// <summary>The RGB of a picture as Windows' video processor makes it (BT.601, limited range, 8-bit fixed point, chroma per 2x2).</summary>
        internal static byte[] Rgb32(byte[] nv12, int w, int h)
        {
            var o = new byte[w * h * 4];
            static byte C(int v) => (byte)Math.Clamp(v, 0, 255);
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    int c = 298 * (nv12[y * w + x] - 16), ci = w * h + y / 2 * w + x / 2 * 2, d = nv12[ci] - 128, e = nv12[ci + 1] - 128;
                    int i = (y * w + x) * 4;
                    o[i] = C((c + 516 * d + 128) >> 8); o[i + 1] = C((c - 100 * d - 208 * e + 128) >> 8); o[i + 2] = C((c + 409 * e + 128) >> 8); o[i + 3] = 0;
                }
            return o;
        }

        [Fact]
        public void TheReader_DecodesTheFramesToRgb32_WithVideoProcessing()
        {
            var f = new Mp4TestFile();
            IntPtr r = Open(f, "rgb", a => MFReader.SetU32(a, MF_SOURCE_READER_ENABLE_VIDEO_PROCESSING, 1), out var path);
            try
            {
                SetVideoType(r, RGB32);
                var frames = ReadAll(r, s => MFReader.SampleBytes(s, out _));
                Assert.Equal(f.Frames, frames.Count);
                for (int k = 0; k < frames.Count; k++)
                {
                    var want = Rgb32(f.Pictures[k], f.Width, f.Height);
                    int bad = Enumerable.Range(0, want.Length).Count(i => want[i] != frames[k][i]);
                    Assert.True(bad == 0, $"frame {k}: {bad} bytes differ");
                }
            }
            finally { MFReader.Release(r); File.Delete(path); }
        }

        // ---- asynchronously ----------------------------------------------------------------------------

        /// <summary>IMFSourceReaderCallback: what it is told, in order.</summary>
        private sealed class Callback : ComObject
        {
            private static readonly Vtable s_vt = Build();
            public readonly BlockingCollection<(string What, int Hr, uint Stream, uint Flags, long Time, byte[]? Bytes)> Told = new();

            private static Vtable Build()
            {
                var vt = new Vtable("IMFSourceReaderCallback", 6, new Guid("deec8d99-fa1d-4d82-84c2-2c8969944867"));
                vt.Set(3, (delegate* unmanaged<IntPtr, int, uint, uint, long, IntPtr, int>)&OnReadSample);
                vt.Set(4, (delegate* unmanaged<IntPtr, uint, int>)&OnFlush);
                vt.Set(5, (delegate* unmanaged<IntPtr, uint, IntPtr, int>)&OnEvent);
                return vt;
            }

            public Callback() : base(s_vt) { }

            [UnmanagedCallersOnly]
            private static int OnReadSample(IntPtr self, int hr, uint stream, uint flags, long time, IntPtr sample)
            {
                From<Callback>(self)?.Told.Add(("read", hr, stream, flags, time, sample == IntPtr.Zero ? null : MFReader.SampleBytes(sample, out _)));
                return 0;
            }

            [UnmanagedCallersOnly] private static int OnFlush(IntPtr self, uint stream) { From<Callback>(self)?.Told.Add(("flush", 0, stream, 0, 0, null)); return 0; }
            [UnmanagedCallersOnly] private static int OnEvent(IntPtr self, uint stream, IntPtr e) => 0;
        }

        [Fact]
        public void TheReader_ReadsThroughTheCallback()
        {
            var f = new Mp4TestFile();
            var cb = new Callback();
            IntPtr r = Open(f, "async", a => MFReader.SetUnknown(a, MF_SOURCE_READER_ASYNC_CALLBACK, cb.Pointer), out var path);
            try
            {
                SetVideoType(r, NV12);
                // asynchronous: the out parameters must be null
                uint dummy;
                Assert.Equal(unchecked((int)0x80070057), ((delegate* unmanaged<IntPtr, uint, uint, uint*, uint*, long*, IntPtr*, int>)MFReader.Fn(r, 9))(r, MFReader.FIRST_VIDEO, 0, &dummy, null, null, null));
                var frames = new List<byte[]>();
                var times = new List<long>();
                for (int n = 0; n < 20; n++)
                {
                    Ok(((delegate* unmanaged<IntPtr, uint, uint, uint*, uint*, long*, IntPtr*, int>)MFReader.Fn(r, 9))(r, MFReader.FIRST_VIDEO, 0, null, null, null, null), "ReadSample");
                    Assert.True(cb.Told.TryTake(out var t, 10_000), "no OnReadSample");
                    Assert.Equal("read", t.What);
                    Ok(t.Hr, "OnReadSample's status");
                    Assert.Equal(1u, t.Stream);
                    if (t.Bytes != null) { frames.Add(t.Bytes); times.Add(t.Time); }
                    if ((t.Flags & 2) != 0) break;
                }
                Assert.Equal(f.Frames, frames.Count);
                Assert.Equal(Enumerable.Range(0, f.Frames).Select(k => k * 400_000L), times);
                for (int k = 0; k < frames.Count; k++) Assert.True(f.Pictures[k].AsSpan().SequenceEqual(frames[k]), $"frame {k} differs");
                Ok(MFReader.Flush(r, MFReader.FIRST_VIDEO), "Flush");
                Assert.True(cb.Told.TryTake(out var fl, 10_000), "no OnFlush");
                Assert.Equal("flush", fl.What);
            }
            finally { MFReader.Release(r); cb.Release(); File.Delete(path); }
        }

        // ---- into textures ------------------------------------------------------------------------------

        [Fact]
        public void TheReader_DecodesIntoTexturesOfTheDeviceManagersDevice()
        {
            Assert.SkipWhen(OperatingSystem.IsWindows(), "the shim's reader (Windows' given the shim's device: MFDecoderTests)");
            Assert.SkipWhen(WgpuInterop.Tests.Harness.Requirements.GpuUnavailable is not null, WgpuInterop.Tests.Harness.Requirements.GpuUnavailable ?? "");
            IntPtr dev, ctx; uint level;
            Ok(Exports.CreateDevicePointer(IntPtr.Zero, 1, IntPtr.Zero, 0x20, null, 0, 7, &dev, &level, &ctx), "D3D11CreateDevice");
            IntPtr m; uint token;
            Ok(MFExports.MFCreateDXGIDeviceManager(&token, &m), "MFCreateDXGIDeviceManager");
            IntPtr manager = m;
            Ok(((delegate* unmanaged<IntPtr, IntPtr, uint, int>)MFReader.Fn(manager, 7))(manager, dev, token), "ResetDevice");
            try
            {
                foreach (var (sub, format) in new[] { (NV12, 103u), (RGB32, 88u) })
                {
                    var f = new Mp4TestFile();
                    IntPtr r = Open(f, "d3d", a => { MFReader.SetUnknown(a, MF_SOURCE_READER_D3D_MANAGER, manager); MFReader.SetU32(a, MF_SOURCE_READER_ENABLE_VIDEO_PROCESSING, 1); }, out var path);
                    try
                    {
                        SetVideoType(r, sub);
                        var frames = ReadAll(r, s =>
                        {
                            IntPtr buf, dxgi, tex;
                            Ok(((delegate* unmanaged<IntPtr, uint, IntPtr*, int>)MFReader.Fn(s, 40))(s, 0, &buf), "GetBufferByIndex");
                            var iid = IID_IMFDXGIBuffer;
                            Ok(((delegate* unmanaged<IntPtr, Guid*, IntPtr*, int>)MFReader.Fn(buf, 0))(buf, &iid, &dxgi), "IMFDXGIBuffer");
                            var tiid = IID_ID3D11Texture2D;
                            Ok(((delegate* unmanaged<IntPtr, Guid*, IntPtr*, int>)MFReader.Fn(dxgi, 3))(dxgi, &tiid, &tex), "GetResource");
                            D3D11_TEXTURE2D_DESC d;
                            ((delegate* unmanaged<IntPtr, D3D11_TEXTURE2D_DESC*, void>)MFReader.Fn(tex, Vtbl.ID3D11Texture2D.GetDesc))(tex, &d);
                            Assert.Equal((DXGI_FORMAT)format, d.Format);
                            MFReader.Release(tex); MFReader.Release(dxgi); MFReader.Release(buf);
                            return MFReader.SampleBytes(s, out _);
                        });
                        Assert.Equal(f.Frames, frames.Count);
                        for (int k = 0; k < frames.Count; k++)
                        {
                            var want = sub == NV12 ? f.Pictures[k] : Rgb32(f.Pictures[k], f.Width, f.Height);
                            Assert.True(want.AsSpan().SequenceEqual(frames[k].AsSpan(0, want.Length)), $"{(sub == NV12 ? "NV12" : "RGB32")} frame {k} differs");
                        }
                    }
                    finally { MFReader.Release(r); File.Delete(path); }
                }
            }
            finally { MFReader.Release(manager); MFReader.Release(ctx); MFReader.Release(dev); }
        }

        // ---- byte streams -------------------------------------------------------------------------------

        [Fact]
        public void TheReader_ReadsAFileByteStream()
        {
            var f = new Mp4TestFile();
            string path = f.WriteTemp("bytestream");
            IntPtr stream = IntPtr.Zero, r = IntPtr.Zero;
            try
            {
                int hr;
                fixed (char* p = path) hr = OperatingSystem.IsWindows() ? WinMFCreateFile(1, 0, 0, path, &stream) : MFExports.MFCreateFile(1, 0, 0, p, &stream);
                Ok(hr, "MFCreateFile");
                ulong len;
                Ok(((delegate* unmanaged<IntPtr, ulong*, int>)MFReader.Fn(stream, 4))(stream, &len), "GetLength");
                Assert.Equal((ulong)new FileInfo(path).Length, len);
                hr = OperatingSystem.IsWindows() ? WinMFCreateSourceReaderFromByteStream(stream, IntPtr.Zero, &r) : MFExports.MFCreateSourceReaderFromByteStream(stream, IntPtr.Zero, &r);
                Ok(hr, "MFCreateSourceReaderFromByteStream");
                SetVideoType(r, NV12);
                var frames = ReadAll(r, s => MFReader.SampleBytes(s, out _));
                Assert.Equal(f.Frames, frames.Count);
                Assert.True(f.Pictures[^1].AsSpan().SequenceEqual(frames[^1]));
            }
            finally { MFReader.Release(r); MFReader.Release(stream); File.Delete(path); }
        }

        /// <summary>A file over HTTP (with ranges, as servers give them): read as the same file on disk is; a 404 is Windows' NS_E_FILE_NOT_FOUND.</summary>
        [Fact]
        public void TheReader_ReadsAFileOverHttp()
        {
            var listener = new System.Net.HttpListener();
            int port = 0;
            for (int p = 18771; p < 18800 && port == 0; p++)
            {
                try { listener.Prefixes.Clear(); listener.Prefixes.Add($"http://127.0.0.1:{p}/"); listener.Start(); port = p; }
                catch (System.Net.HttpListenerException) { listener = new System.Net.HttpListener(); }
            }
            Assert.SkipWhen(port == 0, "no port to listen on");
            var serving = System.Threading.Tasks.Task.Run(() =>
            {
                while (listener.IsListening)
                {
                    System.Net.HttpListenerContext c;
                    try { c = listener.GetContext(); } catch { return; }
                    string file = MFSourceReaderProbe.AudioPath(c.Request.Url!.AbsolutePath.TrimStart('/'));
                    if (!File.Exists(file)) { c.Response.StatusCode = 404; c.Response.Close(); continue; }
                    var data = File.ReadAllBytes(file);
                    long a = 0, b = data.Length - 1;
                    if (c.Request.Headers["Range"] is { } range && range.StartsWith("bytes="))
                    {
                        var parts = range[6..].Split('-');
                        a = long.Parse(parts[0]);
                        if (parts[1].Length > 0) b = Math.Min(b, long.Parse(parts[1]));
                        c.Response.StatusCode = 206;
                        c.Response.AddHeader("Content-Range", $"bytes {a}-{b}/{data.Length}");
                    }
                    c.Response.AddHeader("Accept-Ranges", "bytes");
                    c.Response.ContentType = "video/mp4";
                    c.Response.ContentLength64 = b - a + 1;
                    try { c.Response.OutputStream.Write(data, (int)a, (int)(b - a + 1)); c.Response.Close(); } catch { }
                }
            });
            try
            {
                var web = MFSourceReaderProbe.VideoReport($"http://127.0.0.1:{port}/clip-vp9.mp4");
                var disk = MFSourceReaderProbe.VideoReport(MFSourceReaderProbe.AudioPath("clip-vp9.mp4"));
                Assert.Equal(string.Join("\n", disk), string.Join("\n", web));
                Assert.Equal(unchecked((int)0xC00D001A), MFReader.Create($"http://127.0.0.1:{port}/missing.mp4", IntPtr.Zero, out var r));
                MFReader.Release(r);
            }
            finally { listener.Stop(); }
        }

        [Fact]
        public void TheReader_RefusesWhatIsNoMediaFile()
        {
            string path = Path.Combine(Path.GetTempPath(), $"wpfmf-text-{Environment.ProcessId}.mp4");
            File.WriteAllText(path, "this is no media file, only text long enough to be sniffed at");
            try
            {
                int hr = MFReader.Create(path, IntPtr.Zero, out var r);
                MFReader.Release(r);
                Assert.Equal(unchecked((int)0xC00D36C4), hr);                  // MF_E_UNSUPPORTED_BYTESTREAM_TYPE
                hr = MFReader.Create(path + ".none", IntPtr.Zero, out r);
                Assert.Equal(unchecked((int)0x80070002), hr);                  // ERROR_FILE_NOT_FOUND
            }
            finally { File.Delete(path); }
        }
    }
}
