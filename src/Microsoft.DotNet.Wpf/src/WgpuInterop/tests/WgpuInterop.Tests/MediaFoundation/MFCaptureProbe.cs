// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// What Media Foundation says of the capture devices (MF_PROBE_CAPTURE=<file> writes it): MFEnumDeviceSources'
// devices and their attributes, the device source's presentation descriptor, and a source reader over it -- its
// streams and native types. Nothing is read from a device: no camera is switched on.
//

using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using Microsoft.Wpf.Interop.WebGpu.MediaFoundation;
using Xunit;

namespace WgpuInterop.Tests.MediaFoundation
{
    public sealed unsafe class MFCaptureProbe
    {
        [DllImport("mf.dll", EntryPoint = "MFEnumDeviceSources")] private static extern int WinMFEnumDeviceSources(IntPtr attributes, IntPtr** activates, uint* count);
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
                                foreach (var k in MFReader.Names.Keys.Where(k => MFReader.Names[k].StartsWith("MF_PD_") || MFReader.Names[k].StartsWith("MF_SOURCE_READER_MEDIASOURCE")))
                                    log.Add($"  presentation {MFReader.Name(k)} {(MFReader.PresentationAttribute(r, MFReader.MEDIASOURCE, k, out var v) is var ph && ph >= 0 ? "= " + v : Hr(ph))}");
                                MFReader.Release(r);
                            }
                            ((delegate* unmanaged<IntPtr, int>)MFReader.Fn(src, 12))(src);                  // IMFMediaSource::Shutdown
                            MFReader.Release(src);
                        }
                    }
                    MFReader.Release(devs[d]);
                }
            }
            return log;
        }

        private static string Hr(int hr) => $"0x{hr:x8}";

        [Fact]
        public void Probe()
        {
            Assert.SkipWhen(Environment.GetEnvironmentVariable("MF_PROBE_CAPTURE") is not { Length: > 0 }, "MF_PROBE_CAPTURE=<file> writes the report");
            System.IO.File.WriteAllLines(Environment.GetEnvironmentVariable("MF_PROBE_CAPTURE")!, Report());
        }
    }
}
