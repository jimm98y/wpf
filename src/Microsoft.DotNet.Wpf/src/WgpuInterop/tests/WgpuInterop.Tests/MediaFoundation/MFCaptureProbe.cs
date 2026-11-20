// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// What Media Foundation says of the capture devices (MF_PROBE_CAPTURE=<file> writes it): MFEnumDeviceSources'
// devices and their attributes, the device source's presentation descriptor, and a source reader over it -- its
// streams and native types, and a few frames of the first video device (switched on briefly; nothing is kept).
//

using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using Microsoft.Wpf.Interop.WebGpu.MediaFoundation;
using Xunit;

namespace WgpuInterop.Tests.MediaFoundation
{
    public sealed unsafe partial class MFCaptureProbe
    {
        [DllImport("mf.dll", EntryPoint = "MFEnumDeviceSources")] private static extern int WinMFEnumDeviceSources(IntPtr attributes, IntPtr** activates, uint* count);
        [DllImport("mf.dll", EntryPoint = "MFCreateDeviceSource")] private static extern int WinMFCreateDeviceSource(IntPtr attributes, IntPtr* source);
        [DllImport("mfreadwrite.dll", EntryPoint = "MFCreateSourceReaderFromMediaSource")] private static extern int WinMFCreateSourceReaderFromMediaSource(IntPtr source, IntPtr attributes, IntPtr* reader);

        internal static readonly Guid MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE = new("c60ac5fe-252a-478f-a0ef-bc8fa5f7cad3");
        internal static readonly Guid VIDCAP_GUID = new("8ac3587a-4ae7-42d8-99e0-0a6013eef90f"), AUDCAP_GUID = new("14dd9a1c-7cff-41be-b1b9-ba1ac6ecb571");
        internal static readonly Guid IID_IMFMediaSource = new("279a808d-aec7-40c8-9c6b-a6b492c78a66");

        internal static int Enum(IntPtr attributes, out IntPtr[] devices)
        {
            IntPtr* a; uint n;
            int hr = OperatingSystem.IsWindows() ? WinMFEnumDeviceSources(attributes, &a, &n) : MFExports.MFEnumDeviceSources(attributes, &a, &n);
            devices = new IntPtr[hr >= 0 ? (int)n : 0];
            for (int i = 0; i < devices.Length; i++) devices[i] = a[i];
            if (hr >= 0 && n > 0) Marshal.FreeCoTaskMem((IntPtr)a);
            return hr;
        }

        internal static int ReaderOf(IntPtr source, out IntPtr reader)
        {
            IntPtr r;
            int hr = OperatingSystem.IsWindows() ? WinMFCreateSourceReaderFromMediaSource(source, IntPtr.Zero, &r) : MFExports.MFCreateSourceReaderFromMediaSource(source, IntPtr.Zero, &r);
            reader = hr >= 0 ? r : IntPtr.Zero;
            return hr;
        }

        internal static List<string> Report()
        {
            var log = new List<string>();
            MFReader.Startup();          // (Windows' audio capture source is of the started platform's: MF_E_SHUTDOWN without)
            foreach (var (kind, guid) in new[] { ("video", VIDCAP_GUID), ("audio", AUDCAP_GUID) })
            {
                IntPtr attrs = MFReader.Attributes();
                MFReader.SetGuid(attrs, MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE, guid);
                int hr = Enum(attrs, out var devs);
                MFReader.Release(attrs);
                log.Add($"==== {kind} devices {Hr(hr)} count {devs.Length}");
                for (int d = 0; d < devs.Length; d++)
                {
                    log.Add($"device {d}:");
                    log.AddRange(MFReader.Dump(devs[d]).Select(l => "  " + l));
                    if (d == 0)
                    {
                        IntPtr src; var iid = IID_IMFMediaSource;
                        hr = ((delegate* unmanaged<IntPtr, Guid*, IntPtr*, int>)MFReader.Fn(devs[d], 33))(devs[d], &iid, &src);
                        log.Add($"  activate {Hr(hr)}");
                        if (hr >= 0)
                        {
                            uint ch;
                            ((delegate* unmanaged<IntPtr, uint*, int>)MFReader.Fn(src, 7))(src, &ch);      // IMFMediaSource::GetCharacteristics
                            log.Add($"  characteristics 0x{ch:x}");
                            hr = ReaderOf(src, out var r);
                            log.Add($"  reader {Hr(hr)}");
                            if (hr >= 0)
                            {
                                for (uint i = 0; ; i++)
                                {
                                    if (MFReader.GetStreamSelection(r, i, out bool sel) < 0) { log.Add($"  streams {i}"); break; }
                                    log.Add($"  stream {i}: selected {sel}");
                                    for (uint n = 0; n < 400; n++)
                                    {
                                        if (MFReader.GetNativeMediaType(r, i, n, out var t) < 0) { log.Add($"    native types {n}"); break; }
                                        if (n < 3) { log.Add($"    native {n}:"); log.AddRange(MFReader.Dump(t).Select(l => "      " + l)); }
                                        else log.Add($"    native {n}: {MFReader.Name(MFReader.G(t, new Guid("f7e34c9a-42e8-4714-b74b-cb29d72c35e5")))} {MFReader.U64(t, new Guid("1652c33d-d6b2-4012-b834-72030849a37d")) >> 32}x{(uint)MFReader.U64(t, new Guid("1652c33d-d6b2-4012-b834-72030849a37d"))} rate {MFReader.U64(t, new Guid("c459a2e8-3d2c-4e44-b132-fee5156c7bb0")) >> 32}:{(uint)MFReader.U64(t, new Guid("c459a2e8-3d2c-4e44-b132-fee5156c7bb0"))}");
                                        MFReader.Release(t);
                                    }
                                    MFReader.GetCurrentMediaType(r, i, out var c);
                                    log.Add("    current:");
                                    log.AddRange(MFReader.Dump(c).Select(l => "      " + l));
                                    MFReader.Release(c);
                                }
                                // a few frames of the first stream (the camera on, briefly; nothing is kept): flags, sizes, the times between
                                {
                                    long prev = -1;
                                    for (int f = 0; f < 8; f++)
                                    {
                                        int rh = MFReader.ReadSample(r, 0, out _, out uint fl, out long ts, out var smp);
                                        string what = smp == IntPtr.Zero ? "no sample" : $"{MFReader.SampleBytes(smp, out uint bufs).Length} bytes in {bufs} [{string.Join(" ", MFReader.Dump(smp).Select(l => l.Split(' ')[0]))}]";
                                        log.Add($"  read {Hr(rh)} flags 0x{fl:x} {(f == 0 ? $"first t={ts}" : $"after {(ts - prev) / 10_000} ms")} {what}");
                                        if (f >= 1 && smp != IntPtr.Zero) log.Add("    " + MFMediaSourceProbe.SampleLine(smp));
                                        prev = ts;
                                        MFReader.Release(smp);
                                        if (rh < 0) break;
                                    }
                                }
                                if (kind == "video") log.AddRange(SetNative(r));
                                foreach (var k in MFReader.Names.Keys.Where(k => MFReader.Names[k].StartsWith("MF_PD_") || MFReader.Names[k].StartsWith("MF_SOURCE_READER_MEDIASOURCE")))
                                    log.Add($"  presentation {MFReader.Name(k)} {(MFReader.PresentationAttribute(r, MFReader.MEDIASOURCE, k, out var v) is var ph && ph >= 0 ? "= " + v : Hr(ph))}");
                                MFReader.Release(r);
                            }
                            ((delegate* unmanaged<IntPtr, int>)MFReader.Fn(src, 12))(src);                  // IMFMediaSource::Shutdown
                            MFReader.Release(src);
                        }
                    }
                    if (d == 0 && kind == "video") log.AddRange(ByLink(devs[d]));
                    MFReader.Release(devs[d]);
                }
                if (kind == "video" && devs.Length > 0) log.AddRange(SourceEvents());
            }
            return log;
        }

        private static readonly Guid IID_IMFSourceReaderEx = new("7b981cf0-560e-4116-9875-b099895f23d7");
        private static readonly Guid FRAME_SIZE = new("1652c33d-d6b2-4012-b834-72030849a37d");

        /// <summary>The device's last type as the stream's native (SetNativeMediaType): the current type then, and the frames of it.</summary>
        private static List<string> SetNative(IntPtr r)
        {
            var log = new List<string>();
            uint n = 0;
            while (MFReader.GetNativeMediaType(r, 0, n, out var t) >= 0) { MFReader.Release(t); n++; }
            MFReader.GetNativeMediaType(r, 0, n - 1, out var last);
            ulong size = MFReader.U64(last, FRAME_SIZE);
            IntPtr rx = MFMediaSourceProbe.Qi(r, IID_IMFSourceReaderEx);
            uint flags = 0;
            int hr = ((delegate* unmanaged<IntPtr, uint, IntPtr, uint*, int>)MFReader.Fn(rx, 13))(rx, 0, last, &flags);
            log.Add($"  set native {Hr(hr)} flags 0x{flags:x}");
            MFReader.GetCurrentMediaType(r, 0, out var c);
            log.Add($"  current then: {MFReader.Name(MFReader.G(c, new Guid("f7e34c9a-42e8-4714-b74b-cb29d72c35e5")))} of the native's size {MFReader.U64(c, FRAME_SIZE) == size}");
            MFReader.Release(c);
            MFReader.GetNativeMediaType(r, 0, 0xFFFFFFFF, out var now);
            log.Add($"  native in use: of its size {(now != IntPtr.Zero && MFReader.U64(now, FRAME_SIZE) == size)}");
            MFReader.Release(now);
            long expect = (long)(size >> 32) * (uint)size * 3 / 2;
            for (int f = 0; f < 4; f++)
            {
                int rh = MFReader.ReadSample(r, 0, out _, out uint fl, out _, out var smp);
                log.Add($"  read {Hr(rh)} flags 0x{fl:x} {(smp == IntPtr.Zero ? "no sample" : $"of the type's size {MFReader.SampleBytes(smp, out _).Length == expect}")}");
                MFReader.Release(smp);
                if (rh < 0) break;
            }
            MFReader.Release(last);
            MFReader.Release(rx);
            return log;
        }

        internal static int CreateDeviceSource(IntPtr attributes, out IntPtr source)
        {
            IntPtr s;
            int hr = OperatingSystem.IsWindows() ? WinMFCreateDeviceSource(attributes, &s) : MFExports.MFCreateDeviceSource(attributes, &s);
            source = hr >= 0 ? s : IntPtr.Zero;
            return hr;
        }

        internal static readonly Guid SYMBOLIC_LINK = new("58f0aad8-22bf-4f8a-bb3d-d2c4978c6e2f");

        /// <summary>MFCreateDeviceSource of the device's symbolic link, of another, of none.</summary>
        private static List<string> ByLink(IntPtr activate)
        {
            var log = new List<string>();
            var k = SYMBOLIC_LINK;
            char* str; uint len;
            if (((delegate* unmanaged<IntPtr, Guid*, char**, uint*, int>)MFReader.Fn(activate, 13))(activate, &k, &str, &len) < 0) { log.Add("  no link"); return log; }
            string link = new string(str, 0, (int)len);
            Marshal.FreeCoTaskMem((IntPtr)str);
            foreach (var (what, value) in new[] { ("its link", link), ("its link upper-cased", link.ToUpperInvariant()), ("another link", @"\\?\usb#vid_0000&pid_0000#0#{e5323777-f976-4f5b-9b55-b94699c46e44}\global"), ("no link", (string?)null) })
            {
                IntPtr attrs = MFReader.Attributes();
                MFReader.SetGuid(attrs, MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE, VIDCAP_GUID);
                if (value != null) fixed (char* v = value) ((delegate* unmanaged<IntPtr, Guid*, char*, int>)MFReader.Fn(attrs, 25))(attrs, &k, v);
                int hr = CreateDeviceSource(attrs, out var src);
                log.Add($"  device source of {what} {Hr(hr)}");
                if (src != IntPtr.Zero)
                {
                    IntPtr pd = MFMediaSourceProbe.Pd(src, out int ph);
                    log.Add($"    pd {Hr(ph)}");
                    MFReader.Release(pd);
                    ((delegate* unmanaged<IntPtr, int>)MFReader.Fn(src, 12))(src);
                    MFReader.Release(src);
                }
                MFReader.Release(attrs);
            }
            IntPtr none = MFReader.Attributes();
            log.Add($"  device source of no type {Hr(CreateDeviceSource(none, out var s2))}");
            if (s2 != IntPtr.Zero) { ((delegate* unmanaged<IntPtr, int>)MFReader.Fn(s2, 12))(s2); MFReader.Release(s2); }
            MFReader.Release(none);
            return log;
        }

        /// <summary>The device source driven as a media session drives it: its descriptor, Start, its events, a few RequestSamples, Stop.</summary>
        private static List<string> SourceEvents()
        {
            var log = new List<string> { "source events of the last device:" };
            IntPtr attrs = MFReader.Attributes();
            MFReader.SetGuid(attrs, MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE, VIDCAP_GUID);
            Enum(attrs, out var devs);
            MFReader.Release(attrs);
            for (int i = 0; i < devs.Length - 1; i++) MFReader.Release(devs[i]);
            IntPtr activate = devs[^1];
            IntPtr src; var iid = IID_IMFMediaSource;
            int hr = ((delegate* unmanaged<IntPtr, Guid*, IntPtr*, int>)MFReader.Fn(activate, 33))(activate, &iid, &src);
            log.Add($"  activate {Hr(hr)}");
            if (hr < 0) { MFReader.Release(activate); return log; }
            IntPtr pd = MFMediaSourceProbe.Pd(src, out hr);
            log.Add($"  pd {Hr(hr)}");
            if (hr >= 0)
            {
                log.AddRange(MFMediaSourceProbe.PdReport(pd, out _).Where(l => !l.StartsWith("      ") || l.Contains("SUBTYPE") || l.Contains("FRAME_SIZE")).Take(60));
                log.Add($"  start {Hr(MFMediaSourceProbe.Start(src, pd, null))}");
                IntPtr stream = IntPtr.Zero;
                foreach (var e in MFMediaSourceProbe.Drain(src, 5000))
                {
                    log.Add("  source " + MFMediaSourceProbe.Describe(e));
                    if (e.Unk != IntPtr.Zero && (e.Type == 205 || e.Type == 206) && stream == IntPtr.Zero) stream = e.Unk;
                    else if (e.Unk != IntPtr.Zero) MFReader.Release(e.Unk);
                }
                if (stream != IntPtr.Zero)
                {
                    log.Add($"  stream id {MFMediaSourceProbe.StreamId(stream)}");
                    foreach (var e in MFMediaSourceProbe.Drain(stream, 1000)) { log.Add("  stream " + MFMediaSourceProbe.Describe(e)); MFReader.Release(e.Unk); }
                    for (int i = 0; i < 3; i++) log.Add($"  request {Hr(MFMediaSourceProbe.RequestSample(stream, IntPtr.Zero))}");
                    long prev = 0;
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    int samples = 0;
                    while (samples < 3 && sw.ElapsedMilliseconds < 8000 && MFMediaSourceProbe.Event(stream, 8000, out _) is { } e)
                    {
                        if (e.Type == 213 && e.Unk != IntPtr.Zero)
                        {
                            long t; ((delegate* unmanaged<IntPtr, long*, int>)MFReader.Fn(e.Unk, 35))(e.Unk, &t);
                            log.Add($"  stream {MFMediaSourceProbe.Describe(e)} {(samples == 0 ? "t=" + t : $"after {(t - prev) / 10_000} ms")} {MFReader.SampleBytes(e.Unk, out _).Length} bytes");
                            prev = t; samples++;
                        }
                        else log.Add("  stream " + MFMediaSourceProbe.Describe(e));
                        MFReader.Release(e.Unk);
                    }
                    log.Add($"  pause {Hr(((delegate* unmanaged<IntPtr, int>)MFReader.Fn(src, 11))(src))}");
                    foreach (var e in MFMediaSourceProbe.Drain(src, 1000)) { log.Add("  source " + MFMediaSourceProbe.Describe(e)); MFReader.Release(e.Unk); }
                    log.Add($"  stop {Hr(((delegate* unmanaged<IntPtr, int>)MFReader.Fn(src, 10))(src))}");
                    foreach (var e in MFMediaSourceProbe.Drain(src, 1000)) { log.Add("  source " + MFMediaSourceProbe.Describe(e)); MFReader.Release(e.Unk); }
                    foreach (var e in MFMediaSourceProbe.Drain(stream, 300)) { log.Add("  stream " + MFMediaSourceProbe.Describe(e)); MFReader.Release(e.Unk); }
                    log.Add($"  request when stopped {Hr(MFMediaSourceProbe.RequestSample(stream, IntPtr.Zero))}");
                    MFReader.Release(stream);
                }
                MFReader.Release(pd);
            }
            log.Add($"  shutdown {Hr(((delegate* unmanaged<IntPtr, int>)MFReader.Fn(src, 12))(src))}");
            uint ch; log.Add($"  characteristics after shutdown {Hr(((delegate* unmanaged<IntPtr, uint*, int>)MFReader.Fn(src, 7))(src, &ch))}");
            MFReader.Release(src);
            log.Add($"  activate again {Hr(((delegate* unmanaged<IntPtr, Guid*, IntPtr*, int>)MFReader.Fn(activate, 33))(activate, &iid, &src))}");
            if (src != IntPtr.Zero) { ((delegate* unmanaged<IntPtr, int>)MFReader.Fn(src, 12))(src); MFReader.Release(src); }
            log.Add($"  shutdown object {Hr(((delegate* unmanaged<IntPtr, int>)MFReader.Fn(activate, 34))(activate))}");
            MFReader.Release(activate);
            return log;
        }

        private static string Hr(int hr) => $"0x{hr:x8}";

        /// <summary>
        /// The report of what is of Media Foundation, not of the machine: the first camera's attributes but its name and
        /// link, its first type but its size and rate, the reads and events but their times and sizes; the other
        /// cameras and the microphones left out (a machine's own, and no device's name or link kept in the fixture).
        /// </summary>
        internal static List<string> Normalize(IEnumerable<string> report)
        {
            var o = new List<string>();
            bool skip = false, skipType = false, firstSample = true;
            foreach (var raw in report)
            {
                string l = raw;
                if (l.StartsWith("==== audio")) break;
                if (l.StartsWith("==== video")) { o.Add(Cut(l, " count ") + " count <n>"); continue; }
                if (l.StartsWith("device ")) { skip = l != "device 0:"; if (!skip) o.Add(l); continue; }
                if (l.StartsWith("source events")) skip = false;
                if (skip) continue;
                // a type of the device but the first (and the current): its own
                if (l.StartsWith("    native ") && !l.StartsWith("    native 0:")) { skipType = l.EndsWith(":"); continue; }
                if (l.StartsWith("    native 0:") || l.StartsWith("    current:")) skipType = false;
                if (l.StartsWith("    type ")) { skipType = true; continue; }
                if (l.StartsWith("      ") && skipType) continue;
                if (!l.StartsWith("      ")) skipType = false;
                if (l.Contains("60d0e559-52f8-4fa2-bbce-acdb34a8ec01 = STR")) l = Cut(l, "= STR") + "= STR <name>";
                if (l.Contains("58f0aad8-22bf-4f8a-bb3d-d2c4978c6e2f = STR")) l = Cut(l, "= STR") + "= STR <link>";
                if (l.Contains("56a819ca-0c78-4de4-a0a7-3ddaba0f24d4 = BLOB")) l = Cut(l, "= BLOB") + "= BLOB <types>";
                foreach (var k in new[] { "MF_MT_FRAME_SIZE = ", "MF_MT_FRAME_RATE = ", "d2e7558c-dc1f-403f-9a72-d28bb1eb3b5e = ", "e3371d41-b4cf-4a05-bd4e-20b88bb2c4d6 = " })
                    if (l.Contains(k)) l = Cut(l, k) + k + "<value>";
                if (l.Contains("handler major ")) l = Cut(l, " types ") + " types <n>";
                // (the sample's attribute of the camera's driver, 5f002af9, left out: Device)
                // a frame's discontinuity after the first: of a frame dropped before it (of the timing)
                if (l.StartsWith("    sample ") && !firstSample) l = l.Replace("MFSampleExtension_Discontinuity = UI4 1", "MFSampleExtension_Discontinuity = UI4 0");
                if (l.StartsWith("    sample ")) firstSample = false;
                o.Add(Numbers(Device(l)));
            }
            return o;
        }

        private static string Cut(string l, string at) => l[..l.IndexOf(at)];

        /// <summary>The camera driver's attribute out of a sample's list (its name alone, or with its value).</summary>
        private static string Device(string l)
        {
            const string k = "5f002af9-d8f9-41a3-b6c3-a2ad43f647ad";
            int at = l.IndexOf(k);
            if (at < 0) return l;
            int end = at + k.Length;
            if (l.AsSpan(end).StartsWith(" = ")) { end = l.IndexOf(';', end); if (end < 0) end = l.IndexOf(']', at); else end += 1; }
            while (end < l.Length && l[end] == ' ') end++;
            return l[..at] + l[end..];
        }

        /// <summary>Times, sizes and the bytes of a frame as placeholders.</summary>
        private static string Numbers(string l)
        {
            var o = new System.Text.StringBuilder();
            string[] marks = { "t=", "d=", "size=", "I8 ", "after ", "UI8 " };
            int i = 0;
            while (i < l.Length)
            {
                string? m = marks.FirstOrDefault(x => string.CompareOrdinal(l, i, x, 0, x.Length) == 0 && (i == 0 || !char.IsLetterOrDigit(l[i - 1])));
                if (m != null && i + m.Length < l.Length && (char.IsDigit(l[i + m.Length]) || l[i + m.Length] == '-'))
                {
                    o.Append(m).Append('#');
                    i += m.Length + 1;
                    while (i < l.Length && char.IsDigit(l[i])) i++;
                    if (i < l.Length && l[i] == ' ' && i + 1 < l.Length && l[i + 1] == '(') { int c = l.IndexOf(')', i); if (c > 0) i = c + 1; }        // a UI8's ratio
                    continue;
                }
                o.Append(l[i++]);
            }
            string r = o.ToString();
            // a frame's byte count and its first bytes
            int b = r.IndexOf(" bytes");
            if (b > 0) { int s0 = r.LastIndexOf(' ', b - 1); if (s0 >= 0 && long.TryParse(r.AsSpan(s0 + 1, b - s0 - 1), out _)) r = r[..(s0 + 1)] + "#" + r[b..]; }
            // the frame server's description of the frame: its size the device's
            int fd = r.IndexOf("03ffdcb2-c32c-4b92-98ff-87c0333db253 = BLOB[40] ");
            if (fd > 0) { int h = fd + "03ffdcb2-c32c-4b92-98ff-87c0333db253 = BLOB[40] ".Length; r = r[..(h + 64)] + "<size>" + r[(h + 80)..]; }
            int buf = r.IndexOf("buffers=1 ");
            if (buf > 0) { int e = r.IndexOf(' ', buf + 10); if (e > 0) r = r[..(buf + 10)] + "<bytes>" + r[e..]; }
            return r;
        }

        [System.Text.RegularExpressions.GeneratedRegex(@"first t=\d+")] private static partial System.Text.RegularExpressions.Regex Rx0();
        [System.Text.RegularExpressions.GeneratedRegex(@"after -?\d+ ms \d+ bytes")] private static partial System.Text.RegularExpressions.Regex Rx1();
        [System.Text.RegularExpressions.GeneratedRegex(@"t=\d+ d=\d+ size=\d+ buffers=1 \S+")] private static partial System.Text.RegularExpressions.Regex Rx2();
        [System.Text.RegularExpressions.GeneratedRegex(@"UI8 \d+ \(\d+:\d+\)")] private static partial System.Text.RegularExpressions.Regex Rx3();
        [System.Text.RegularExpressions.GeneratedRegex(@"t=(\d+) d=(\d+) size=(\d+).*8f3e35e7-2dcd-4887-8622-2a58baa652b0 = UI8 (\d+)")] private static partial System.Text.RegularExpressions.Regex Rx4();

        /// <summary>The report's microphones, of the first: of no device's name or endpoint, nor the blocks' times and sizes
        /// (Windows' are of its engine's period, ours 10 ms: a block's duration of its size either way).</summary>
        internal static List<string> NormalizeAudio(IEnumerable<string> report)
        {
            var o = new List<string>();
            bool audio = false, skip = false;
            foreach (var raw in report)
            {
                string l = raw;
                if (l.StartsWith("==== audio")) { audio = true; o.Add(Cut(l, " count ") + " count <n>"); continue; }
                if (!audio) continue;
                if (l.StartsWith("device ")) { skip = l != "device 0:"; if (!skip) o.Add(l); continue; }
                if (skip) continue;
                foreach (var k in new[] { "30da9258-feb9-47a7-a453-763a7a8e1c5f = STR", "60d0e559-52f8-4fa2-bbce-acdb34a8ec01 = STR", "98d24b5e-5930-4614-b5a1-f600f9355a78 = STR" })
                    if (l.Contains(k)) l = Cut(l, "= STR") + "= STR <device>";
                foreach (var k in new[] { "MF_MT_AUDIO_AVG_BYTES_PER_SECOND = ", "MF_MT_AUDIO_BLOCK_ALIGNMENT = ", "MF_MT_AUDIO_NUM_CHANNELS = ", "MF_MT_AUDIO_SAMPLES_PER_SECOND = " })
                    if (l.Contains(k)) l = Cut(l, k) + k + "<the device's>";            // (the device's mix format, as Windows' is)
                l = Rx0().Replace(l, "first");
                l = Rx1().Replace(l, "after # ms # bytes");
                l = Rx2().Replace(l, "t=# d=# size=# buffers=1 <bytes>");
                l = Rx3().Replace(l, "UI8 <time>");
                o.Add(l);
            }
            return o;
        }

        /// <summary>The first microphone, as Windows' first is (CaptureAudio.windows.txt): where there is one.</summary>
        [Fact]
        public void TheAudioCaptureSource_BehavesAsWindowsDoes()
        {
            Assert.SkipWhen(OperatingSystem.IsWindows(), "Windows' device source is the fixture");
            var report = Report();
            var ours = NormalizeAudio(report);
            Assert.SkipWhen(!ours.Contains("device 0:"), "no microphone here");
            var windows = System.IO.File.ReadAllLines(System.IO.Path.Combine(TestFiles.Root, "MediaFoundation", "CaptureAudio.windows.txt"));
            for (int i = 0; i < Math.Min(windows.Length, ours.Count); i++)
                Assert.True(windows[i] == ours[i], $"line {i}: Windows '{windows[i]}', ours '{ours[i]}'");
            Assert.Equal(windows.Length, ours.Count);
            // each block's duration of its size, its device timestamp its time
            var audio = report.SkipWhile(l => !l.StartsWith("==== audio")).ToList();
            long Of(string k) => long.Parse(audio.First(l => l.Contains(k)).Split(' ').Last());
            long rate = Of("MF_MT_AUDIO_SAMPLES_PER_SECOND = "), block = Of("MF_MT_AUDIO_BLOCK_ALIGNMENT = ");
            Assert.Equal(block * rate, Of("MF_MT_AUDIO_AVG_BYTES_PER_SECOND = "));
            foreach (var l in report.SkipWhile(l => !l.StartsWith("==== audio")).Where(l => l.StartsWith("    sample ")))
            {
                var m = Rx4().Match(l);
                Assert.True(m.Success, l);
                Assert.Equal(long.Parse(m.Groups[1].Value), long.Parse(m.Groups[4].Value));
                Assert.Equal(long.Parse(m.Groups[3].Value) / block * 10_000_000 / rate, long.Parse(m.Groups[2].Value));
            }
        }

        /// <summary>The first camera, as Windows' first is (Capture.windows.txt, of the parts of Media Foundation's): where there is one.</summary>
        [Fact]
        public void TheCaptureSource_BehavesAsWindowsDoes()
        {
            Assert.SkipWhen(OperatingSystem.IsWindows(), "Windows' device source is the fixture");
            var ours = Normalize(Report());
            Assert.SkipWhen(!ours.Contains("device 0:"), "no camera here");
            var windows = System.IO.File.ReadAllLines(System.IO.Path.Combine(TestFiles.Root, "MediaFoundation", "Capture.windows.txt"));
            for (int i = 0; i < Math.Min(windows.Length, ours.Count); i++)
                Assert.True(windows[i] == ours[i], $"line {i}: Windows '{windows[i]}', ours '{ours[i]}'");
            Assert.Equal(windows.Length, ours.Count);
        }

        [Fact]
        public void Probe()
        {
            Assert.SkipWhen(Environment.GetEnvironmentVariable("MF_PROBE_CAPTURE") is not { Length: > 0 }, "MF_PROBE_CAPTURE=<file> writes the report (of no device's name or link)");
            // (MF_PROBE_CAPTURE_RAW=1: the report as it is -- of the machine's device names: never a fixture)
            var report = Report();
            System.IO.File.WriteAllLines(Environment.GetEnvironmentVariable("MF_PROBE_CAPTURE")!, Environment.GetEnvironmentVariable("MF_PROBE_CAPTURE_RAW") == "1" ? report : Normalize(report));
            if (Environment.GetEnvironmentVariable("MF_PROBE_CAPTURE_RAW") != "1") System.IO.File.WriteAllLines(System.IO.Path.ChangeExtension(Environment.GetEnvironmentVariable("MF_PROBE_CAPTURE")!, ".audio.txt"), NormalizeAudio(report));
        }
    }
}
