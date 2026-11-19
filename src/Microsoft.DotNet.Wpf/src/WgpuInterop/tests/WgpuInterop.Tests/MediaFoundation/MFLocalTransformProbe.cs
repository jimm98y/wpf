// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// Transforms of the application's, registered in the process (MF_PROBE_LOCAL=<file> writes the report): MFTRegisterLocal
// of a class factory and MFTRegisterLocalByCLSID, as MFTEnumEx lists them of each set of flags, their activates'
// attributes, what activating one makes; unregistering; and whether the sink writer and the source reader take a local
// encoder and decoder before the system's (SpyTransform over the system's own), with every call they make of it.
// Windows' Media Foundation on Windows, the shim's elsewhere.
//

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using Microsoft.Wpf.Interop.WebGpu.MediaFoundation;
using Xunit;

namespace WgpuInterop.Tests.MediaFoundation
{
#pragma warning disable SYSLIB1045 // the patterns of a test, made once
    public sealed unsafe class MFLocalTransformProbe
    {
        [DllImport("mfplat.dll", EntryPoint = "MFTRegisterLocal")] private static extern int WinRegister(IntPtr factory, Guid* category, [MarshalAs(UnmanagedType.LPWStr)] string? name, uint flags, uint cin, MFT_REGISTER_TYPE_INFO* inTypes, uint cout, MFT_REGISTER_TYPE_INFO* outTypes);
        [DllImport("mfplat.dll", EntryPoint = "MFTUnregisterLocal")] private static extern int WinUnregister(IntPtr factory);
        [DllImport("mfplat.dll", EntryPoint = "MFTRegisterLocalByCLSID")] private static extern int WinRegisterClsid(Guid* clsid, Guid* category, [MarshalAs(UnmanagedType.LPWStr)] string? name, uint flags, uint cin, MFT_REGISTER_TYPE_INFO* inTypes, uint cout, MFT_REGISTER_TYPE_INFO* outTypes);
        [DllImport("mfplat.dll", EntryPoint = "MFTUnregisterLocalByCLSID")] private static extern int WinUnregisterClsid(Guid clsid);
        [DllImport("ole32.dll")] private static extern int CoInitializeEx(IntPtr reserved, uint model);
        [DllImport("mfplat.dll", EntryPoint = "MFTEnumEx")] private static extern int WinEnum(Guid category, uint flags, MFT_REGISTER_TYPE_INFO* input, MFT_REGISTER_TYPE_INFO* output, IntPtr** activates, uint* count);

        private static string Hr(int hr) => $"0x{hr:x8}";
        private static readonly Guid Video = new("73646976-0000-0010-8000-00aa00389b71");
        private static readonly Guid H264 = new("34363248-0000-0010-8000-00aa00389b71"), NV12 = new("3231564e-0000-0010-8000-00aa00389b71");
        private static readonly Guid VENC = new("f79eac7d-e545-4387-bdee-d647d7bde42a"), VDEC = new("d6c02d4b-6833-45b4-971a-05a4b04bab91");
        private static readonly Guid H264EncoderClsid = new("6ca50344-051a-4ded-9779-a43305165e35"), ColorConverterClsid = new("98230571-0087-4204-b020-3282538e57d3");
        private static readonly Guid IID_IMFTransform = new("bf94c121-5b05-4e6f-8000-ba598961414d");
        private static readonly Guid MFT_FRIENDLY_NAME = new("314ffbae-5b41-4c95-9c19-4e7d586face3");

        private static int Register(IntPtr factory, Guid category, string? name, Guid input, Guid output, uint flags = 0)
        {
            var i = new MFT_REGISTER_TYPE_INFO { guidMajorType = Video, guidSubtype = input };
            var o = new MFT_REGISTER_TYPE_INFO { guidMajorType = Video, guidSubtype = output };
            if (OperatingSystem.IsWindows()) return WinRegister(factory, &category, name, flags, 1, &i, 1, &o);
            fixed (char* p = name) return MFExports.MFTRegisterLocal(factory, &category, name == null ? null : p, flags, 1, &i, 1, &o);
        }

        private static int RegisterClsid(Guid clsid, Guid category, string? name, Guid input, Guid output)
        {
            var i = new MFT_REGISTER_TYPE_INFO { guidMajorType = Video, guidSubtype = input };
            var o = new MFT_REGISTER_TYPE_INFO { guidMajorType = Video, guidSubtype = output };
            if (OperatingSystem.IsWindows()) return WinRegisterClsid(&clsid, &category, name, 0, 1, &i, 1, &o);
            fixed (char* p = name) return MFExports.MFTRegisterLocalByCLSID(&clsid, &category, name == null ? null : p, 0, 1, &i, 1, &o);
        }

        private static int Unregister(IntPtr factory) => OperatingSystem.IsWindows() ? WinUnregister(factory) : MFExports.MFTUnregisterLocal(factory);
        private static int UnregisterClsid(Guid clsid) => OperatingSystem.IsWindows() ? WinUnregisterClsid(clsid) : MFExports.MFTUnregisterLocalByCLSID(clsid);

        /// <summary>The activates MFTEnumEx gives (AddRef'd, the caller releases them).</summary>
        private static List<IntPtr> Enum(Guid category, uint flags, Guid input, Guid output, out int hr)
        {
            var i = new MFT_REGISTER_TYPE_INFO { guidMajorType = Video, guidSubtype = input };
            var o = new MFT_REGISTER_TYPE_INFO { guidMajorType = Video, guidSubtype = output };
            IntPtr* acts; uint n;
            hr = OperatingSystem.IsWindows() ? WinEnum(category, flags, &i, &o, &acts, &n) : MFExports.MFTEnumEx(category, flags, &i, &o, &acts, &n);
            var r = new List<IntPtr>();
            if (hr < 0) return r;
            for (uint k = 0; k < n; k++) r.Add(acts[k]);
            if (n > 0) Marshal.FreeCoTaskMem((IntPtr)acts);
            return r;
        }

        private static string Name(IntPtr a)
        {
            var k = MFT_FRIENDLY_NAME;
            uint len;
            if (((delegate* unmanaged<IntPtr, Guid*, uint*, int>)MFReader.Fn(a, 11))(a, &k, &len) < 0) return "(no name)";       // GetStringLength
            var buf = new char[len + 1];
            fixed (char* p = buf) ((delegate* unmanaged<IntPtr, Guid*, char*, uint, uint*, int>)MFReader.Fn(a, 12))(a, &k, p, len + 1, &len);     // GetString
            return new string(buf, 0, (int)len);
        }

        /// <summary>The names MFTEnumEx lists, in order, of each set of flags; the local ones' attributes once.</summary>
        private static List<string> Listing(string when, bool dumpLocal)
        {
            var log = new LiveLog();
            foreach (var (flags, what) in new (uint, string)[] { (0, "0"), (0x41, "SYNCMFT|SORTANDFILTER"), (0x51, "SYNCMFT|LOCALMFT|SORTANDFILTER"), (0x11, "SYNCMFT|LOCALMFT"), (0x3F, "ALL"), (0x10, "LOCALMFT") })
            {
                var acts = Enum(VENC, flags, NV12, H264, out int hr);
                var names = acts.Select(Name).ToList();
                log.Add($"  {when}, {what}: {Hr(hr)} [{string.Join(" | ", names)}]");
                if (dumpLocal && flags == 0x51)
                    for (int k = 0; k < acts.Count; k++)
                        if (Name(acts[k]) != "H264 Encoder MFT")
                        {
                            uint count;
                            ((delegate* unmanaged<IntPtr, uint*, int>)MFReader.Fn(acts[k], 30))(acts[k], &count);
                            log.Add($"    local {k}: {count} attributes");
                            foreach (var l in MFReader.Dump(acts[k])) log.Add($"      {l}");
                        }
                foreach (var a in acts) MFReader.Release(a);
            }
            return log;
        }

        private static IntPtr SystemEncoder() => MFEncoderProbe.Encoder(VENC, Video, NV12, H264);
        private static IntPtr SystemDecoder() => MFEncoderProbe.Encoder(VDEC, Video, H264, NV12);

        /// <summary>The calls, runs of the same one as one with its count.</summary>
        private static string Collapse(List<string> calls)
        {
            var r = new List<string>();
            for (int i = 0; i < calls.Count;)
            {
                int j = i;
                while (j < calls.Count && calls[j] == calls[i]) j++;
                r.Add(j - i > 1 ? $"{calls[i]} x{j - i}" : calls[i]);
                i = j;
            }
            return string.Join("\n    ", r);
        }

        internal static List<string> Report()
        {
            MFReader.Startup();
            // as an application has it: COM on the thread (a local transform of a CLSID is made through it)
            if (OperatingSystem.IsWindows()) CoInitializeEx(IntPtr.Zero, 0);
            var log = new LiveLog { "==== registering" };
            var factory = new SpyFactory(SystemEncoder);
            log.AddRange(Listing("before", false));
            log.Add($"  register, no factory: {Hr(Register(IntPtr.Zero, VENC, "Local spy", NV12, H264))}");
            log.Add($"  register: {Hr(Register(factory.Pointer, VENC, "Local spy", NV12, H264))}");
            log.Add($"  register no name: {Hr(Register(factory.Pointer, VENC, null, NV12, H264))}");
            log.Add($"  register by CLSID: {Hr(RegisterClsid(H264EncoderClsid, VENC, "Local by CLSID", NV12, H264))}");
            log.Add($"  register the color converter by CLSID: {Hr(RegisterClsid(ColorConverterClsid, VENC, "Local converter", NV12, H264))}");
            log.AddRange(Listing("registered", true));
            log.Add($"  factory calls: {string.Join(", ", factory.Calls)}"); factory.Calls.Clear();

            // activating each local one
            var acts = Enum(VENC, 0x51, NV12, H264, out _);
            for (int ai = 0; ai < acts.Count; ai++)
            {
                var a = acts[ai];
                string n = $"{ai} {Name(a)}";
                log.Add($"  activating {n}");
                // (Windows' activate of the H.264 encoder's CLSID registered locally crashes the process: passed over)
                if (MFReader.G(a, new Guid("6821c42b-65a4-4e82-99bc-9a88205ecd0c")) == H264EncoderClsid) { log.Add("    passed over"); continue; }
                var iid = IID_IMFTransform; IntPtr t;
                int hr = ((delegate* unmanaged<IntPtr, Guid*, IntPtr*, int>)MFReader.Fn(a, 33))(a, &iid, &t);
                log.Add($"  activate {n}: {Hr(hr)}{(hr >= 0 && Microsoft.Wpf.Interop.WebGpu.Direct3D.ComObject.From<SpyTransform>(t) != null ? " (the spy)" : "")}");
                if (hr >= 0)
                {
                    IntPtr again;
                    int hr2 = ((delegate* unmanaged<IntPtr, Guid*, IntPtr*, int>)MFReader.Fn(a, 33))(a, &iid, &again);
                    log.Add($"    again: {Hr(hr2)} the same object {hr2 >= 0 && again == t}");
                    if (hr2 >= 0) MFReader.Release(again);
                    log.Add($"    shutdown {Hr(((delegate* unmanaged<IntPtr, int>)MFReader.Fn(a, 34))(a))}");
                    MFReader.Release(t);
                }
            }
            foreach (var a in acts) MFReader.Release(a);
            log.Add($"  factory calls: {string.Join(", ", factory.Calls)}"); factory.Calls.Clear();

            log.Add($"  unregister: {Hr(Unregister(factory.Pointer))}");
            log.AddRange(Listing("unregistered once", false));
            log.Add($"  unregister again: {Hr(Unregister(factory.Pointer))}");
            log.AddRange(Listing("unregistered twice", false));
            log.Add($"  unregister by CLSID: {Hr(UnregisterClsid(H264EncoderClsid))}");
            log.Add($"  unregister by CLSID again: {Hr(UnregisterClsid(H264EncoderClsid))}");
            log.Add($"  unregister the color converter by CLSID: {Hr(UnregisterClsid(ColorConverterClsid))}");
            log.Add($"  register, unregister all: {Hr(Register(factory.Pointer, VENC, "Local spy", NV12, H264))} {Hr(Unregister(IntPtr.Zero))}");
            log.AddRange(Listing("all unregistered", false));
            log.Add($"  unregister all again: {Hr(Unregister(IntPtr.Zero))}");
            factory.Calls.Clear();

            log.AddRange(Writer(factory));
            log.AddRange(Reader());
            factory.Release();
            return log;
        }

        private static List<string> Writer(SpyFactory factory)
        {
            var log = new LiveLog { "==== the sink writer, a local encoder registered" };
            log.Add($"  register: {Hr(Register(factory.Pointer, VENC, "Local spy", NV12, H264))}");
            string path = Path.Combine(Path.GetTempPath(), $"wpfmf-local-{Environment.ProcessId}.mp4");
            try
            {
                int hr = MFWriter.Create(path, IntPtr.Zero, out var w);
                log.Add($"  create {Hr(hr)}");
                if (hr < 0) return log;
                var target = MFReader.MediaType();
                MFReader.SetGuid(target, new Guid("48eba18e-f8c9-4687-bf11-0a74c9f96a8f"), Video);
                MFReader.SetGuid(target, new Guid("f7e34c9a-42e8-4714-b74b-cb29d72c35e5"), H264);
                MFReader.SetU64(target, new Guid("1652c33d-d6b2-4012-b834-72030849a37d"), MF.Pack(128, 96));
                MFReader.SetU64(target, new Guid("c459a2e8-3d2c-4e44-b132-fee5156c7bb0"), MF.Pack(25, 1));
                MFReader.SetU32(target, new Guid("20332624-fb0d-4d9e-bd0d-cbf6786c102e"), 500_000);
                MFReader.SetU32(target, new Guid("e2724bb8-e676-4806-b4b2-a8d6efb44ccd"), 2);
                hr = MFWriter.AddStream(w, target, out uint i);
                var input = MFReader.MediaType();
                ((delegate* unmanaged<IntPtr, IntPtr, int>)MFReader.Fn(target, 32))(target, input);
                MFReader.SetGuid(input, new Guid("f7e34c9a-42e8-4714-b74b-cb29d72c35e5"), NV12);
                { var bk = new Guid("20332624-fb0d-4d9e-bd0d-cbf6786c102e"); ((delegate* unmanaged<IntPtr, Guid*, int>)MFReader.Fn(input, 19))(input, &bk); }      // DeleteItem
                MFReader.Release(target);
                int inHr = MFWriter.SetInputMediaType(w, i, input);
                MFReader.Release(input);
                log.Add($"  add {Hr(hr)}, input {Hr(inHr)}");
                Guid cat; IntPtr t;
                hr = ((delegate* unmanaged<IntPtr, uint, uint, Guid*, IntPtr*, int>)MFReader.Fn(w, 14))(w, i, 0, &cat, &t);
                log.Add($"  transform 0: {Hr(hr)}{(hr >= 0 && Microsoft.Wpf.Interop.WebGpu.Direct3D.ComObject.From<SpyTransform>(t) != null ? " the spy" : hr >= 0 ? " not the spy" : "")}");
                if (hr >= 0) MFReader.Release(t);
                log.Add($"  calls while set up:\n    {Collapse(factory.Calls)}"); factory.Calls.Clear();
                log.Add($"  begin {Hr(MFWriter.BeginWriting(w))}");
                for (int k = 0; k < 5; k++)
                {
                    var s = MFWriter.Sample(MFDecoderTests.Picture(128, 96, k), k * 400_000L, 400_000);
                    hr = MFWriter.WriteSample(w, i, s);
                    MFReader.Release(s);
                    if (hr < 0) { log.Add($"  write {k} {Hr(hr)}"); break; }
                }
                log.Add($"  finalize {Hr(MFWriter.Finalize(w))}");
                MFReader.Release(w);
                lock (factory.Calls) log.Add($"  calls while writing:\n    {Collapse(factory.Calls)}");
                factory.Calls.Clear();
                log.Add($"  file {(File.Exists(path) ? new FileInfo(path).Length > 1000 : false)}");
            }
            finally
            {
                log.Add($"  unregister: {Hr(Unregister(factory.Pointer))}");
                try { File.Delete(path); } catch { }
            }
            return log;
        }

        private static List<string> Reader()
        {
            var log = new LiveLog { "==== the source reader, a local decoder registered" };
            var factory = new SpyFactory(SystemDecoder);
            log.Add($"  register: {Hr(Register(factory.Pointer, VDEC, "Local spy decoder", H264, NV12))}");
            string path = new Mp4TestFile { Audio = false }.WriteTemp("local");
            try
            {
                int hr = MFReader.Create(path, IntPtr.Zero, out var r);
                log.Add($"  create {Hr(hr)}");
                if (hr < 0) return log;
                var nv12 = MFReader.MediaType();
                MFReader.SetGuid(nv12, new Guid("48eba18e-f8c9-4687-bf11-0a74c9f96a8f"), Video);
                MFReader.SetGuid(nv12, new Guid("f7e34c9a-42e8-4714-b74b-cb29d72c35e5"), NV12);
                log.Add($"  set NV12 {Hr(MFReader.SetCurrentMediaType(r, 0, nv12))}");
                MFReader.Release(nv12);
                Guid cat; IntPtr t;
                hr = ((delegate* unmanaged<IntPtr, uint, uint, Guid*, IntPtr*, int>)MFReader.Fn(r, 16))(r, 0, 0, &cat, &t);
                log.Add($"  transform 0: {Hr(hr)}{(hr >= 0 && Microsoft.Wpf.Interop.WebGpu.Direct3D.ComObject.From<SpyTransform>(t) != null ? " the spy" : hr >= 0 ? " not the spy" : "")}");
                if (hr >= 0) MFReader.Release(t);
                log.Add($"  calls while set up:\n    {Collapse(factory.Calls)}"); factory.Calls.Clear();
                int frames = 0;
                for (int k = 0; k < 20; k++)
                {
                    hr = MFReader.ReadSample(r, 0, out _, out uint flags, out _, out var s);
                    if (hr < 0) { log.Add($"  read {Hr(hr)}"); break; }
                    if (s != IntPtr.Zero) { frames++; MFReader.Release(s); }
                    if ((flags & 2) != 0) break;
                }
                log.Add($"  frames {frames}");
                MFReader.Release(r);
                lock (factory.Calls) log.Add($"  calls while reading:\n    {Collapse(factory.Calls)}");
            }
            finally
            {
                log.Add($"  unregister: {Hr(Unregister(factory.Pointer))}");
                factory.Release();
                try { File.Delete(path); } catch { }
            }
            return log;
        }

        /// <summary>
        /// Transforms of the application's, as Windows keeps and uses them (LocalTransform.windows.txt): the registering, line by
        /// line; of the writer and the reader, that each takes the application's transform before the system's, and the calls
        /// that set its types and tell it of streaming, in order. Not compared: Windows' color converter DSP (of a CLSID, the
        /// shim has no class of: REGDB_E_CLASSNOTREG); the calls the transform's own work makes (it is the system's
        /// encoder and decoder inside), such as a decoder's type set again at its stream change.
        /// </summary>
        [Fact]
        public void TheApplicationsTransforms_AreKeptAndTakenAsWindowsDoes()
        {
            Assert.SkipWhen(OperatingSystem.IsWindows(), "Windows' Media Foundation is the fixture");
            Assert.SkipUnless(Microsoft.Wpf.Interop.WebGpu.MediaFoundation.Codecs.PlatformVideoEncoder.Supports(Microsoft.Wpf.Interop.WebGpu.MediaFoundation.Codecs.VideoCodec.H264), "this system has no H.264 encoder");
            var windows = Clean(File.ReadAllLines(Path.Combine(TestFiles.Root, "MediaFoundation", "LocalTransform.windows.txt")));
            var ours = Clean(Report());
            for (int i = 0; i < Math.Max(windows.Count, ours.Count); i++)
                Assert.True(i < windows.Count && i < ours.Count && windows[i] == ours[i], $"line {i}: Windows '{(i < windows.Count ? windows[i] : "(none)")}', ours '{(i < ours.Count ? ours[i] : "(none)")}'");
        }

        private static List<string> Clean(IEnumerable<string> lines)
        {
            var r = new List<string>();
            string section = "";
            bool converter = false, typed = false;
            foreach (var l in lines.SelectMany(x => x.Split('\n')))
            {
                if (l.StartsWith("==== ")) { section = l; typed = false; }
                if (l.StartsWith("  activating ")) converter = l.StartsWith("  activating 3 ");
                if (converter && !l.StartsWith("  activating ")) continue;
                if (section == "==== registering") { r.Add(l); continue; }
                if (!l.StartsWith("    ")) { r.Add(l); continue; }
                // the calls: the types set, the messages, the release
                var call = l.TrimStart();
                if (call.Contains("] SetOutputType M")) { if (typed && section.Contains("reader")) continue; if (section.Contains("reader")) typed = true; }
                if (call.Contains("] SetInputType M") || call.Contains("] SetOutputType M") || call.Contains("] ProcessMessage") || call.EndsWith("released") || call.StartsWith("CreateInstance"))
                {
                    call = System.Text.RegularExpressions.Regex.Replace(call, " x\\d+$", "");
                    if (r.Count == 0 || r[^1] != "    " + call) r.Add("    " + call);
                }
            }
            return r;
        }

        /// <summary>The report's lines, each also appended to MF_PROBE_LOCAL's ".progress" file as it comes (to see how far a crash got).</summary>
        private sealed class LiveLog : List<string>
        {
            public new void Add(string line)
            {
                base.Add(line);
                if (Environment.GetEnvironmentVariable("MF_PROBE_LOCAL") is { Length: > 0 } f) File.AppendAllText(f + ".progress", line + "\n");
            }
            public new void AddRange(IEnumerable<string> lines) { foreach (var l in lines) if (lines is not LiveLog) Add(l); else base.Add(l); }
        }

        [Fact]
        public void Probe()
        {
            Assert.SkipWhen(Environment.GetEnvironmentVariable("MF_PROBE_LOCAL") is not { Length: > 0 }, "MF_PROBE_LOCAL=<file> writes the report");
            File.WriteAllLines(Environment.GetEnvironmentVariable("MF_PROBE_LOCAL")!, Report());
        }
    }
}
