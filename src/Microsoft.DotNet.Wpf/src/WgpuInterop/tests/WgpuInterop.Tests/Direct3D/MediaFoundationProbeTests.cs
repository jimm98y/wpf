// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// Media Foundation's decoders on the shim's device (Documentation/d3d11-webgpu-shim.md, section 8):
// what an app that hands its D3D11 device to MF through IMFDXGIDeviceManager gets -- the case of
// SharpMediaFoundationInterop's GpuMediaControl. Native MF calls the shim's vtables itself.
//
// The probe (MF_PROBE=<file>) reads a file's compressed video with a source reader, gives the system's
// decoder MFT the shim's device, and reports what it took and what its frames are made of.
//

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Microsoft.Wpf.Interop.WebGpu.Direct3D;
using Xunit;

namespace WgpuInterop.Tests.Direct3D
{
    public sealed unsafe class MediaFoundationProbeTests
    {
        [DllImport("mfplat.dll")] private static extern int MFStartup(uint version, uint flags);
        [DllImport("mfplat.dll")] private static extern int MFCreateDXGIDeviceManager(uint* token, IntPtr* manager);
        [DllImport("mfplat.dll")] private static extern int MFCreateAttributes(IntPtr* attributes, uint size);
        [DllImport("mfplat.dll")] private static extern int MFCreateMediaType(IntPtr* type);
        [DllImport("mfreadwrite.dll")] private static extern int MFCreateSourceReaderFromURL([MarshalAs(UnmanagedType.LPWStr)] string url, IntPtr attributes, IntPtr* reader);
        [DllImport("ole32.dll")] private static extern int CoInitializeEx(IntPtr reserved, uint flags);
        [DllImport("ole32.dll")] private static extern int CoCreateInstance(Guid* clsid, IntPtr outer, uint context, Guid* iid, IntPtr* ppv);

        internal static IntPtr Fn(IntPtr o, int slot) => (*(IntPtr**)o)[slot];
        internal static uint Release(IntPtr o) => o == IntPtr.Zero ? 0 : ((delegate* unmanaged<IntPtr, uint>)Fn(o, 2))(o);
        internal static int Qi(IntPtr o, Guid iid, out IntPtr p)
        {
            IntPtr q;
            int hr = ((delegate* unmanaged<IntPtr, Guid*, IntPtr*, int>)Fn(o, 0))(o, &iid, &q);
            p = q;
            return hr;
        }

        internal static readonly Guid MF_MT_MAJOR_TYPE = new("48eba18e-f8c9-4687-bf11-0a74c9f96a8f"), MF_MT_SUBTYPE = new("f7e34c9a-42e8-4714-b74b-cb29d72c35e5");
        internal static readonly Guid MFMediaType_Video = new("73646976-0000-0010-8000-00AA00389B71");
        internal static readonly Guid MFVideoFormat_NV12 = new("3231564E-0000-0010-8000-00AA00389B71");
        internal static readonly Guid MFVideoFormat_H264 = new("34363248-0000-0010-8000-00AA00389B71");
        internal static readonly Guid MFVideoFormat_HEVC = new("43564548-0000-0010-8000-00AA00389B71");
        internal static readonly Guid CLSID_MSH264DecoderMFT = new("62CE7E72-4C71-4d20-B15D-452831A87D9D");
        internal static readonly Guid CLSID_MSH265DecoderMFT = new("420A51A3-D605-430C-B4FC-45274FA6C562");
        internal static readonly Guid IID_IMFTransform = new("bf94c121-5b05-4e6f-8000-ba598961414d");
        internal static readonly Guid IID_IMFDXGIBuffer = new("e7174cfa-1c9e-48b1-8866-626226bfc258");
        internal static readonly Guid IID_ID3D11Texture2D = new("6f15aaf2-d208-4e89-9ab4-489535d34f9c");
        internal static readonly Guid MF_SA_D3D11_AWARE = new("206b4fc8-fcf9-4c51-afe3-9764369e33a0");

        /// <summary>What happened, line by line.</summary>
        internal static List<string> Probe(string file, IntPtr device, int frames)
        {
            var log = new LiveLog();
            CoInitializeEx(IntPtr.Zero, 0);                 // COINIT_MULTITHREADED
            MFStartup(0x20070, 0);
            uint token;
            IntPtr manager;
            int hr = MFCreateDXGIDeviceManager(&token, &manager);
            log.Add($"MFCreateDXGIDeviceManager 0x{hr:x8}");
            hr = ((delegate* unmanaged<IntPtr, IntPtr, uint, int>)Fn(manager, 7))(manager, device, token);   // ResetDevice
            log.Add($"ResetDevice 0x{hr:x8}");

            // the compressed stream, from a reader with no device
            IntPtr reader;
            hr = MFCreateSourceReaderFromURL(file, IntPtr.Zero, &reader);
            log.Add($"MFCreateSourceReaderFromURL 0x{hr:x8}");
            if (hr < 0) return log;
            const uint FirstVideo = 0xFFFFFFFC;
            IntPtr native;
            hr = ((delegate* unmanaged<IntPtr, uint, uint, IntPtr*, int>)Fn(reader, 5))(reader, FirstVideo, 0, &native);   // GetNativeMediaType
            Guid sub;
            ((delegate* unmanaged<IntPtr, Guid*, Guid*, int>)Fn(native, 10))(native, (Guid*)Unsafe(MF_MT_SUBTYPE), &sub);  // GetGUID
            log.Add($"native type 0x{hr:x8} {sub}");

            Guid clsid = sub == MFVideoFormat_HEVC ? CLSID_MSH265DecoderMFT : CLSID_MSH264DecoderMFT, iid = IID_IMFTransform;
            IntPtr mft;
            hr = CoCreateInstance(&clsid, IntPtr.Zero, 1, &iid, &mft);
            log.Add($"decoder 0x{hr:x8}");
            if (hr < 0) return log;
            IntPtr attrs;
            ((delegate* unmanaged<IntPtr, IntPtr*, int>)Fn(mft, 8))(mft, &attrs);                                       // GetAttributes
            uint aware = 0;
            ((delegate* unmanaged<IntPtr, Guid*, uint*, int>)Fn(attrs, 7))(attrs, (Guid*)Unsafe(MF_SA_D3D11_AWARE), &aware); // GetUINT32
            log.Add($"D3D11 aware {aware}");
            hr = ((delegate* unmanaged<IntPtr, int, nuint, int>)Fn(mft, 23))(mft, 2, (nuint)manager);                  // MFT_MESSAGE_SET_D3D_MANAGER
            log.Add($"SET_D3D_MANAGER 0x{hr:x8}");
            hr = ((delegate* unmanaged<IntPtr, uint, IntPtr, uint, int>)Fn(mft, 15))(mft, 0, native, 0);                // SetInputType
            log.Add($"SetInputType 0x{hr:x8}");
            if (hr < 0) return log;
            // the first NV12 output type it offers
            IntPtr outType = IntPtr.Zero;
            for (uint i = 0; ; i++)
            {
                IntPtr t;
                if (((delegate* unmanaged<IntPtr, uint, uint, IntPtr*, int>)Fn(mft, 14))(mft, 0, i, &t) < 0) break;     // GetOutputAvailableType
                Guid s2;
                ((delegate* unmanaged<IntPtr, Guid*, Guid*, int>)Fn(t, 10))(t, (Guid*)Unsafe(MF_MT_SUBTYPE), &s2);
                if (s2 == MFVideoFormat_NV12) { outType = t; break; }
                Release(t);
            }
            hr = outType == IntPtr.Zero ? -1 : ((delegate* unmanaged<IntPtr, uint, IntPtr, uint, int>)Fn(mft, 16))(mft, 0, outType, 0);
            log.Add($"SetOutputType NV12 0x{hr:x8}");
            uint* info = stackalloc uint[3];
            ((delegate* unmanaged<IntPtr, uint, uint*, int>)Fn(mft, 7))(mft, 0, info);                                 // GetOutputStreamInfo
            log.Add($"output stream flags 0x{info[0]:x} (0x100 provides samples)");
            ((delegate* unmanaged<IntPtr, int, nuint, int>)Fn(mft, 23))(mft, 0x10000000, 0);                           // NOTIFY_BEGIN_STREAMING
            ((delegate* unmanaged<IntPtr, int, nuint, int>)Fn(mft, 23))(mft, 0x10000003, 0);                           // NOTIFY_START_OF_STREAM

            int got = 0;
            for (int n = 0; n < 400 && got < frames; n++)
            {
                uint stream, flags; long ts; IntPtr sample;
                hr = ((delegate* unmanaged<IntPtr, uint, uint, uint*, uint*, long*, IntPtr*, int>)Fn(reader, 9))(reader, FirstVideo, 0, &stream, &flags, &ts, &sample);  // ReadSample
                if (hr < 0 || sample == IntPtr.Zero) { log.Add($"ReadSample 0x{hr:x8} flags 0x{flags:x}"); break; }
                hr = ((delegate* unmanaged<IntPtr, uint, IntPtr, uint, int>)Fn(mft, 24))(mft, 0, sample, 0);           // ProcessInput
                Release(sample);
                while (true)
                {
                    // MFT_OUTPUT_DATA_BUFFER: stream id, sample, status, events
                    var odb = stackalloc IntPtr[4];
                    odb[0] = IntPtr.Zero; odb[1] = IntPtr.Zero; odb[2] = IntPtr.Zero; odb[3] = IntPtr.Zero;
                    uint status;
                    int ohr = ((delegate* unmanaged<IntPtr, uint, uint, IntPtr*, uint*, int>)Fn(mft, 25))(mft, 0, 1, odb, &status);  // ProcessOutput
                    if (ohr == unchecked((int)0xC00D6D72)) break;                                                          // NEED_MORE_INPUT
                    if (ohr == unchecked((int)0xC00D6D61))                                                                 // STREAM_CHANGE
                    {
                        IntPtr t;
                        for (uint i = 0; ((delegate* unmanaged<IntPtr, uint, uint, IntPtr*, int>)Fn(mft, 14))(mft, 0, i, &t) >= 0; i++)
                        {
                            Guid s2;
                            ((delegate* unmanaged<IntPtr, Guid*, Guid*, int>)Fn(t, 10))(t, (Guid*)Unsafe(MF_MT_SUBTYPE), &s2);
                            if (s2 == MFVideoFormat_NV12) { ((delegate* unmanaged<IntPtr, uint, IntPtr, uint, int>)Fn(mft, 16))(mft, 0, t, 0); Release(t); break; }
                            Release(t);
                        }
                        log.Add("stream change: NV12 again");
                        continue;
                    }
                    if (ohr < 0) { log.Add($"ProcessOutput 0x{ohr:x8}"); n = int.MaxValue - 1; break; }
                    IntPtr outSample = odb[1];
                    if (outSample == IntPtr.Zero) { log.Add("ProcessOutput gave no sample (the MFT does not provide samples)"); n = int.MaxValue - 1; break; }
                    IntPtr buffer;
                    ((delegate* unmanaged<IntPtr, uint, IntPtr*, int>)Fn(outSample, 40))(outSample, 0, &buffer);       // IMFSample::GetBufferByIndex
                    if (Qi(buffer, IID_IMFDXGIBuffer, out IntPtr dxgi) >= 0)
                    {
                        Guid tiid = IID_ID3D11Texture2D;
                        IntPtr tex;
                        int thr = ((delegate* unmanaged<IntPtr, Guid*, IntPtr*, int>)Fn(dxgi, 3))(dxgi, &tiid, &tex);   // GetResource
                        var shim = ComObject.From(tex);
                        log.Add($"frame {got}: a D3D11 texture 0x{thr:x8}, the shim's: {shim?.GetType().Name ?? "no"}");
                        Release(tex);
                        Release(dxgi);
                    }
                    else log.Add($"frame {got}: system memory");
                    Release(buffer);
                    Release(outSample);
                    got++;
                }
            }
            Release(mft); Release(reader); Release(manager);
            return log;
        }

        internal sealed class LiveLog : List<string>
        {
            public new void Add(string s) { base.Add(s); Console.WriteLine("MF " + s); Console.Out.Flush(); }
        }

        private static IntPtr Unsafe(Guid g)
        {
            var p = (Guid*)NativeMemory.Alloc((nuint)sizeof(Guid));
            *p = g;
            return (IntPtr)p;         // a few bytes a probe; never freed
        }

        [Fact]
        public void Probe_MediaFoundationOnTheShimsDevice()
        {
            string? file = Environment.GetEnvironmentVariable("MF_PROBE");
            Assert.SkipWhen(file == null, "a probe: MF_PROBE=<video file>");
            IntPtr dev, ctx;
            uint level;
            Assert.True(Exports.CreateDevicePointer(IntPtr.Zero, 1, IntPtr.Zero, 0x820, null, 0, 7, &dev, &level, &ctx) >= 0, "shim");
            Probe(file!, dev, 5);
        }
    }
}
