// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// The shim's Media Foundation (off Windows), and Windows' own on the shim's device: the H.264 decoder MFT, found with MFTEnumEx and driven
// through its vtables as an application drives Windows' -- input type, the first frame's stream change,
// the output type set again, ProcessInput / ProcessOutput, the drain -- over the system's decoder.
//
// The stream is made here: an SPS, a PPS and IDR pictures whose macroblocks are all I_PCM (raw samples),
// so what a decoder makes of them is known to the byte. Decoded into the application's samples, and
// into the decoder's own NV12 textures of a device manager's Direct3D 11 device.
//

using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Wpf.Interop.WebGpu.Direct3D;
using Microsoft.Wpf.Interop.WebGpu.MediaFoundation;
using Xunit;

namespace WgpuInterop.Tests.MediaFoundation
{
    public sealed unsafe class MFDecoderTests
    {
        private static readonly Guid MFT_CATEGORY_VIDEO_DECODER = new("d6c02d4b-6833-45b4-971a-05a4b04bab91");
        private static readonly Guid MFMediaType_Video = new("73646976-0000-0010-8000-00aa00389b71");
        private static readonly Guid MFVideoFormat_H264 = new("34363248-0000-0010-8000-00aa00389b71");
        private static readonly Guid MFVideoFormat_NV12 = new("3231564e-0000-0010-8000-00aa00389b71");
        private static readonly Guid MF_MT_MAJOR_TYPE = new("48eba18e-f8c9-4687-bf11-0a74c9f96a8f"), MF_MT_SUBTYPE = new("f7e34c9a-42e8-4714-b74b-cb29d72c35e5");
        private static readonly Guid MF_MT_FRAME_SIZE = new("1652c33d-d6b2-4012-b834-72030849a37d"), MF_MT_MINIMUM_DISPLAY_APERTURE = new("d7388766-18fe-48c6-a177-ee894867c8c4");
        private static readonly Guid MFT_FRIENDLY_NAME_Attribute = new("314ffbae-5b41-4c95-9c19-4e7d586face3");
        private static readonly Guid IID_IMFTransform = new("bf94c121-5b05-4e6f-8000-ba598961414d"), IID_IMFDXGIBuffer = new("e7174cfa-1c9e-48b1-8866-626226bfc258");
        private static readonly Guid IID_ID3D11Texture2D = new("6f15aaf2-d208-4e89-9ab4-489535d34f9c");
        private const int MF_E_TRANSFORM_NEED_MORE_INPUT = unchecked((int)0xC00D6D72), MF_E_TRANSFORM_STREAM_CHANGE = unchecked((int)0xC00D6D61);

        private static IntPtr Fn(IntPtr o, int slot) => (*(IntPtr**)o)[slot];
        private static void Release(IntPtr o) { if (o != IntPtr.Zero) ((delegate* unmanaged<IntPtr, uint>)Fn(o, 2))(o); }
        private static void Ok(int hr, string what) => Assert.True(hr >= 0, $"{what}: 0x{hr:x8}");

        // ---- the stream ---------------------------------------------------------------------------------

        private sealed class Bits
        {
            private readonly List<byte> _b = new();
            private int _bit;
            private uint _acc;
            public void U(int n, uint v) { for (int i = n - 1; i >= 0; i--) Bit((v >> i) & 1); }
            public void Bit(uint b) { _acc = (_acc << 1) | b; if (++_bit == 8) { _b.Add((byte)_acc); _acc = 0; _bit = 0; } }
            public void Ue(uint v) { uint x = v + 1; int n = 0; while ((x >> n) > 1) n++; U(n, 0); U(n + 1, x); }
            public void Se(int v) => Ue(v <= 0 ? (uint)(-2 * v) : (uint)(2 * v - 1));
            public void Align0() { while (_bit != 0) Bit(0); }
            public void Trailing() { Bit(1); Align0(); }
            public void Bytes(IEnumerable<byte> b) { foreach (var x in b) U(8, x); }
            public byte[] Done() => _b.ToArray();
        }

        /// <summary>A NAL unit with its start code, emulation prevention bytes inserted.</summary>
        private static byte[] Nal(byte header, byte[] rbsp)
        {
            var r = new List<byte> { 0, 0, 0, 1, header };
            int zeros = 0;
            foreach (var b in rbsp)
            {
                if (zeros >= 2 && b <= 3) { r.Add(3); zeros = 0; }
                r.Add(b);
                zeros = b == 0 ? zeros + 1 : 0;
            }
            return r.ToArray();
        }

        internal static byte[] Sps(int mbW, int mbH, int cropRight, int cropBottom)
        {
            var b = new Bits();
            b.U(8, 66); b.U(8, 0xC0); b.U(8, 30);       // baseline, constraint sets 0 and 1, level 3
            b.Ue(0);                                     // seq_parameter_set_id
            b.Ue(0);                                     // log2_max_frame_num_minus4
            b.Ue(2);                                     // pic_order_cnt_type 2: output order is decode order
            b.Ue(1); b.U(1, 0);                          // max_num_ref_frames, gaps
            b.Ue((uint)mbW - 1); b.Ue((uint)mbH - 1);
            b.U(1, 1); b.U(1, 1);                        // frame_mbs_only, direct_8x8_inference
            if (cropRight > 0 || cropBottom > 0) { b.U(1, 1); b.Ue(0); b.Ue((uint)cropRight / 2); b.Ue(0); b.Ue((uint)cropBottom / 2); }
            else b.U(1, 0);
            b.U(1, 0);                                   // no VUI
            b.Trailing();
            return Nal(0x67, b.Done());
        }

        internal static byte[] Pps()
        {
            var b = new Bits();
            b.Ue(0); b.Ue(0);                            // pps id, sps id
            b.U(1, 0); b.U(1, 0);                        // CAVLC, no bottom-field POC
            b.Ue(0); b.Ue(0); b.Ue(0);                   // one slice group, one reference each
            b.U(1, 0); b.U(2, 0);                        // no weighted prediction
            b.Se(0); b.Se(0); b.Se(0);                   // QP, QS, chroma QP offset
            b.U(1, 1); b.U(1, 0); b.U(1, 0);             // deblocking control present, no constrained intra, no redundant pictures
            b.Trailing();
            return Nal(0x68, b.Done());
        }

        /// <summary>An IDR picture of I_PCM macroblocks holding the NV12 frame (w x h, multiples of 16).</summary>
        internal static byte[] Idr(byte[] nv12, int w, int h, int idrId)
        {
            var b = new Bits();
            b.Ue(0); b.Ue(7); b.Ue(0);                   // first MB, all I slices, pps
            b.U(4, 0);                                   // frame_num
            b.Ue((uint)idrId);
            b.U(1, 0); b.U(1, 0);                        // dec_ref_pic_marking
            b.Se(0);                                     // slice_qp_delta
            b.Ue(1);                                     // disable_deblocking_filter_idc
            for (int my = 0; my < h / 16; my++)
                for (int mx = 0; mx < w / 16; mx++)
                {
                    b.Ue(25);                            // I_PCM
                    b.Align0();
                    for (int y = 0; y < 16; y++) for (int x = 0; x < 16; x++) b.U(8, nv12[(my * 16 + y) * w + mx * 16 + x]);
                    for (int c = 0; c < 2; c++)
                        for (int y = 0; y < 8; y++) for (int x = 0; x < 8; x++) b.U(8, nv12[w * h + (my * 8 + y) * w + (mx * 8 + x) * 2 + c]);
                }
            b.Trailing();
            return Nal(0x65, b.Done());
        }

        /// <summary>A frame of gradients, every value in 16..235.</summary>
        internal static byte[] Picture(int w, int h, int k)
        {
            var f = new byte[w * h * 3 / 2];
            for (int y = 0; y < h; y++) for (int x = 0; x < w; x++) f[y * w + x] = (byte)(16 + (x * 3 + y * 5 + k * 17) % 200);
            for (int y = 0; y < h / 2; y++)
                for (int x = 0; x < w / 2; x++)
                {
                    f[w * h + y * w + x * 2] = (byte)(40 + (x * 7 + k * 11) % 160);
                    f[w * h + y * w + x * 2 + 1] = (byte)(60 + (y * 9 + k * 13) % 150);
                }
            return f;
        }

        // ---- Windows' Media Foundation on Windows, the shim's elsewhere ------------------------------------

        [System.Runtime.InteropServices.DllImport("mfplat.dll", EntryPoint = "MFStartup")] private static extern int WinMFStartup(uint version, uint flags);
        [System.Runtime.InteropServices.DllImport("mfplat.dll", EntryPoint = "MFCreateMediaType")] private static extern int WinMFCreateMediaType(IntPtr* t);
        [System.Runtime.InteropServices.DllImport("mfplat.dll", EntryPoint = "MFCreateMemoryBuffer")] private static extern int WinMFCreateMemoryBuffer(uint size, IntPtr* b);
        [System.Runtime.InteropServices.DllImport("mfplat.dll", EntryPoint = "MFCreateSample")] private static extern int WinMFCreateSample(IntPtr* s);
        [System.Runtime.InteropServices.DllImport("mfplat.dll", EntryPoint = "MFCreateDXGIDeviceManager")] private static extern int WinMFCreateDXGIDeviceManager(uint* token, IntPtr* m);
        [System.Runtime.InteropServices.DllImport("ole32.dll")] private static extern int CoInitializeEx(IntPtr reserved, uint flags);
        [System.Runtime.InteropServices.DllImport("ole32.dll")] private static extern int CoCreateInstance(Guid* clsid, IntPtr outer, uint context, Guid* iid, IntPtr* ppv);
        private static readonly Guid CLSID_MSH264DecoderMFT = new("62CE7E72-4C71-4d20-B15D-452831A87D9D");

        private static int CreateMediaType(IntPtr* t) => OperatingSystem.IsWindows() ? WinMFCreateMediaType(t) : MFExports.MFCreateMediaType(t);
        private static int CreateMemoryBuffer(uint size, IntPtr* b) => OperatingSystem.IsWindows() ? WinMFCreateMemoryBuffer(size, b) : MFExports.MFCreateMemoryBuffer(size, b);
        private static int CreateSample(IntPtr* s) => OperatingSystem.IsWindows() ? WinMFCreateSample(s) : MFExports.MFCreateSample(s);
        private static int CreateDXGIDeviceManager(uint* token, IntPtr* m) => OperatingSystem.IsWindows() ? WinMFCreateDXGIDeviceManager(token, m) : MFExports.MFCreateDXGIDeviceManager(token, m);

        // ---- driving the decoder ----------------------------------------------------------------------

        internal static IntPtr Decoder()
        {
            if (OperatingSystem.IsWindows())
            {
                CoInitializeEx(IntPtr.Zero, 0);
                Ok(WinMFStartup(0x20070, 0), "MFStartup");
                var clsid = CLSID_MSH264DecoderMFT; var tid = IID_IMFTransform; IntPtr w;
                Ok(CoCreateInstance(&clsid, IntPtr.Zero, 1, &tid, &w), "CoCreateInstance(the H.264 decoder)");
                return w;
            }
            var input = new MFT_REGISTER_TYPE_INFO { guidMajorType = MFMediaType_Video, guidSubtype = MFVideoFormat_H264 };
            IntPtr* activates; uint count;
            Ok(MFExports.MFTEnumEx(MFT_CATEGORY_VIDEO_DECODER, 1 | 4, &input, null, &activates, &count), "MFTEnumEx");
            Assert.SkipWhen(count == 0, "this system has no H.264 decoder");
            IntPtr a = activates[0];
            for (uint i = 1; i < count; i++) Release(activates[i]);
            System.Runtime.InteropServices.Marshal.FreeCoTaskMem((IntPtr)activates);
            IntPtr name; uint len;
            var key = MFT_FRIENDLY_NAME_Attribute;
            Ok(((delegate* unmanaged<IntPtr, Guid*, IntPtr*, uint*, int>)Fn(a, 13))(a, &key, &name, &len), "GetAllocatedString");
            Assert.Equal("Microsoft H264 Video Decoder MFT", System.Runtime.InteropServices.Marshal.PtrToStringUni(name));
            System.Runtime.InteropServices.Marshal.FreeCoTaskMem(name);
            IntPtr t;
            var iid = IID_IMFTransform;
            Ok(((delegate* unmanaged<IntPtr, Guid*, IntPtr*, int>)Fn(a, 33))(a, &iid, &t), "ActivateObject");
            Release(a);
            return t;
        }

        private static IntPtr MediaType(Guid subtype, uint w, uint h)
        {
            IntPtr t;
            Ok(CreateMediaType(&t), "MFCreateMediaType");
            var k1 = MF_MT_MAJOR_TYPE; var v1 = MFMediaType_Video; var k2 = MF_MT_SUBTYPE; var v2 = subtype; var k3 = MF_MT_FRAME_SIZE;
            ((delegate* unmanaged<IntPtr, Guid*, Guid*, int>)Fn(t, 24))(t, &k1, &v1);
            ((delegate* unmanaged<IntPtr, Guid*, Guid*, int>)Fn(t, 24))(t, &k2, &v2);
            if (w > 0) ((delegate* unmanaged<IntPtr, Guid*, ulong, int>)Fn(t, 22))(t, &k3, (ulong)w << 32 | h);
            return t;
        }

        private static IntPtr Sample(byte[] data, long time)
        {
            IntPtr buf, s;
            Ok(CreateMemoryBuffer((uint)Math.Max(1, data.Length), &buf), "MFCreateMemoryBuffer");
            if (data.Length > 0)
            {
                byte* p; uint max, cur;
                Ok(((delegate* unmanaged<IntPtr, byte**, uint*, uint*, int>)Fn(buf, 3))(buf, &p, &max, &cur), "Lock");
                data.CopyTo(new Span<byte>(p, data.Length));
                ((delegate* unmanaged<IntPtr, int>)Fn(buf, 4))(buf);
                ((delegate* unmanaged<IntPtr, uint, int>)Fn(buf, 6))(buf, (uint)data.Length);
            }
            Ok(CreateSample(&s), "MFCreateSample");
            ((delegate* unmanaged<IntPtr, IntPtr, int>)Fn(s, 42))(s, buf);
            Release(buf);
            ((delegate* unmanaged<IntPtr, long, int>)Fn(s, 36))(s, time);
            return s;
        }

        private static (uint W, uint H) SetOutput(IntPtr t)
        {
            IntPtr type;
            Ok(((delegate* unmanaged<IntPtr, uint, uint, IntPtr*, int>)Fn(t, 14))(t, 0, 0, &type), "GetOutputAvailableType");
            var k = MF_MT_SUBTYPE; Guid sub;
            ((delegate* unmanaged<IntPtr, Guid*, Guid*, int>)Fn(type, 10))(type, &k, &sub);
            Assert.Equal(MFVideoFormat_NV12, sub);
            var fk = MF_MT_FRAME_SIZE; ulong fs = 0;
            ((delegate* unmanaged<IntPtr, Guid*, ulong*, int>)Fn(type, 8))(type, &fk, &fs);
            Ok(((delegate* unmanaged<IntPtr, uint, IntPtr, uint, int>)Fn(t, 16))(t, 0, type, 0), "SetOutputType");
            Release(type);
            return ((uint)(fs >> 32), (uint)fs);
        }

        /// <summary>Every frame decoded, as NV12 of the output's size, with the stream changes counted.</summary>
        private static List<byte[]> Decode(IntPtr t, IEnumerable<byte[]> units, Func<IntPtr, byte[]>? readOwn, out (uint W, uint H) size, out int changes)
        {
            var frames = new List<byte[]>();
            size = (0, 0);
            changes = 0;
            long time = 0;
            var all = units.ToList();
            for (int i = 0; i <= all.Count; i++)
            {
                if (i < all.Count)
                {
                    IntPtr s = Sample(all[i], time += 400_000);
                    int hr = ((delegate* unmanaged<IntPtr, uint, IntPtr, uint, int>)Fn(t, 24))(t, 0, s, 0);
                    Release(s);
                    Ok(hr, "ProcessInput");
                }
                else Ok(((delegate* unmanaged<IntPtr, uint, nuint, int>)Fn(t, 23))(t, 1, 0), "drain");
                while (true)
                {
                    uint info0;
                    var info = stackalloc uint[3];
                    Ok(((delegate* unmanaged<IntPtr, uint, uint*, int>)Fn(t, 7))(t, 0, info), "GetOutputStreamInfo");
                    bool provides = (info[0] & 0x100) != 0;
                    info0 = info[1];
                    var odb = stackalloc byte[32];
                    new Span<byte>(odb, 32).Clear();
                    IntPtr own = provides ? IntPtr.Zero : Sample(new byte[Math.Max(1, (int)info0)], 0);
                    *(IntPtr*)(odb + 8) = own;
                    uint status;
                    int hr = ((delegate* unmanaged<IntPtr, uint, uint, byte*, uint*, int>)Fn(t, 25))(t, 0, 1, odb, &status);
                    IntPtr got = *(IntPtr*)(odb + 8);
                    if (hr == MF_E_TRANSFORM_STREAM_CHANGE) { changes++; size = SetOutput(t); Release(own); continue; }
                    if (hr == MF_E_TRANSFORM_NEED_MORE_INPUT) { Release(own); break; }
                    Ok(hr, "ProcessOutput");
                    frames.Add(provides ? readOwn!(got) : SampleBytes(got));
                    Release(got);
                }
            }
            return frames;
        }

        private static byte[] SampleBytes(IntPtr s)
        {
            IntPtr buf;
            Ok(((delegate* unmanaged<IntPtr, IntPtr*, int>)Fn(s, 41))(s, &buf), "ConvertToContiguousBuffer");
            byte* p; uint max, cur;
            Ok(((delegate* unmanaged<IntPtr, byte**, uint*, uint*, int>)Fn(buf, 3))(buf, &p, &max, &cur), "Lock");
            var r = new ReadOnlySpan<byte>(p, (int)cur).ToArray();
            ((delegate* unmanaged<IntPtr, int>)Fn(buf, 4))(buf);
            Release(buf);
            return r;
        }

        private static List<byte[]> Stream(int w, int h, int n, out List<byte[]> pictures, int cropRight = 0, int cropBottom = 0)
        {
            pictures = new List<byte[]>();
            var units = new List<byte[]>();
            for (int k = 0; k < n; k++)
            {
                var pic = Picture(w, h, k);
                pictures.Add(pic);
                var au = new List<byte>();
                if (k == 0) { au.AddRange(Sps(w / 16, h / 16, cropRight, cropBottom)); au.AddRange(Pps()); }
                au.AddRange(Idr(pic, w, h, k & 1));
                units.Add(au.ToArray());
            }
            return units;
        }

        [Fact]
        public void TheH264Decoder_DecodesIntoTheApplicationsSamples()
        {
            Assert.SkipWhen(OperatingSystem.IsWindows(), "Windows has its own Media Foundation");
            Ok(MFExports.MFStartup(0x20070, 0), "MFStartup");
            IntPtr t = Decoder();
            try
            {
                IntPtr inType = MediaType(MFVideoFormat_H264, 64, 48);
                Ok(((delegate* unmanaged<IntPtr, uint, IntPtr, uint, int>)Fn(t, 15))(t, 0, inType, 0), "SetInputType");
                Release(inType);
                SetOutput(t);
                var units = Stream(64, 48, 6, out var pictures);
                var frames = Decode(t, units, null, out var size, out int changes);
                Assert.Equal(1, changes);
                Assert.Equal((64u, 48u), size);
                Assert.Equal(pictures.Count, frames.Count);
                for (int k = 0; k < frames.Count; k++) Assert.True(pictures[k].AsSpan().SequenceEqual(frames[k].AsSpan(0, pictures[k].Length)), $"frame {k} differs");
            }
            finally { Release(t); }
        }

        /// <summary>A picture of 56x40 in macroblocks of 64x48: the output of the coded size, the picture in the aperture.</summary>
        [Fact]
        public void TheH264Decoder_GivesTheCodedSize_ThePictureInTheAperture()
        {
            Assert.SkipWhen(OperatingSystem.IsWindows(), "Windows has its own Media Foundation");
            IntPtr t = Decoder();
            try
            {
                IntPtr inType = MediaType(MFVideoFormat_H264, 0, 0);
                Ok(((delegate* unmanaged<IntPtr, uint, IntPtr, uint, int>)Fn(t, 15))(t, 0, inType, 0), "SetInputType");
                Release(inType);
                var units = Stream(64, 48, 2, out var pictures, cropRight: 8, cropBottom: 8);
                // the first output before an output type is set: the stream change, as Windows' decoder gives it
                var frames = Decode(t, units, null, out var size, out int changes);
                Assert.Equal((64u, 48u), size);
                Assert.Equal(2, frames.Count);
                IntPtr cur;
                Ok(((delegate* unmanaged<IntPtr, uint, IntPtr*, int>)Fn(t, 18))(t, 0, &cur), "GetOutputCurrentType");
                var k = MF_MT_MINIMUM_DISPLAY_APERTURE; var area = stackalloc byte[16]; uint got;
                Ok(((delegate* unmanaged<IntPtr, Guid*, byte*, uint, uint*, int>)Fn(cur, 15))(cur, &k, area, 16, &got), "aperture");
                Release(cur);
                Assert.Equal((56u, 40u), (*(uint*)(area + 8), *(uint*)(area + 12)));
                // the picture's rows, the coded size's pitch
                for (int y = 0; y < 40; y++) Assert.True(pictures[1].AsSpan(y * 64, 56).SequenceEqual(frames[1].AsSpan(y * 64, 56)), $"row {y}");
            }
            finally { Release(t); }
        }

        /// <summary>Given a device manager: the decoder's own samples, NV12 textures of the device, as IMFDXGIBuffer.</summary>
        [Fact]
        public void TheH264Decoder_DecodesIntoTexturesOfTheDeviceManagersDevice()
        {
            Assert.SkipWhen(OperatingSystem.IsWindows(), "Windows has its own Media Foundation");
            DecodeIntoTextures(64, 48);
        }

        /// <summary>
        /// Windows' own H.264 decoder given the shim's device decodes into the shim's textures: with the GPU's
        /// decoder, through the shim's ID3D11VideoDecoder (D3DVideoDecoder.cs), where the GPU has one, and on the
        /// CPU where it has none.
        /// </summary>
        [Fact]
        public void WindowsH264Decoder_DecodesIntoTheShimsTextures()
        {
            Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows' Media Foundation");
            Assert.SkipWhen(WgpuInterop.Tests.Harness.Requirements.GpuUnavailable is not null, WgpuInterop.Tests.Harness.Requirements.GpuUnavailable ?? "");
            // Windows' own device first: where the GPU has no decoder, Windows' decoder refuses it, and the shim's alike
            if (VideoDecoderBackend.Available)
            {
                int real = SetManager(VideoDecoderBackend.Device);
                IntPtr dev, ctx; uint level;
                Ok(Exports.CreateDevicePointer(IntPtr.Zero, 1, IntPtr.Zero, 0x20, null, 0, 7, &dev, &level, &ctx), "D3D11CreateDevice");
                int shim = SetManager(dev);
                Release(ctx); Release(dev);
                Assert.Equal($"0x{real:x8}", $"0x{shim:x8}");
                Assert.SkipWhen(real < 0, $"this GPU has no video decoder ({VideoDecoderBackend.Profiles} profiles): Windows' H.264 decoder refuses its device (0x{real:x8}) as it does the shim's");
            }
            int before = D3DVideoDecoder.Created;
            DecodeIntoTextures(256, 144);
            if (VideoDecoderBackend.Profiles > 0) Assert.True(D3DVideoDecoder.Created > before, "Windows' decoder did not use the shim's ID3D11VideoDecoder");
        }

        /// <summary>What Windows' H.264 decoder answers MFT_MESSAGE_SET_D3D_MANAGER with, of a manager of the device.</summary>
        private static int SetManager(IntPtr device)
        {
            IntPtr manager; uint token;
            Ok(CreateDXGIDeviceManager(&token, &manager), "MFCreateDXGIDeviceManager");
            Ok(((delegate* unmanaged<IntPtr, IntPtr, uint, int>)Fn(manager, 7))(manager, device, token), "ResetDevice");
            IntPtr t = Decoder();
            int hr = ((delegate* unmanaged<IntPtr, uint, nuint, int>)Fn(t, 23))(t, 2, (nuint)manager);
            Release(t); Release(manager);
            return hr;
        }

        private static void DecodeIntoTextures(int w, int h)
        {
            Assert.SkipWhen(WgpuInterop.Tests.Harness.Requirements.GpuUnavailable is not null, WgpuInterop.Tests.Harness.Requirements.GpuUnavailable ?? "");
            IntPtr dev, ctx; uint level;
            Ok(Exports.CreateDevicePointer(IntPtr.Zero, 1, IntPtr.Zero, 0x20, null, 0, 7, &dev, &level, &ctx), "D3D11CreateDevice");
            IntPtr manager; uint token;
            Ok(CreateDXGIDeviceManager(&token, &manager), "MFCreateDXGIDeviceManager");
            Ok(((delegate* unmanaged<IntPtr, IntPtr, uint, int>)Fn(manager, 7))(manager, dev, token), "ResetDevice");
            IntPtr t = Decoder();
            try
            {
                Ok(((delegate* unmanaged<IntPtr, uint, nuint, int>)Fn(t, 23))(t, 2, (nuint)manager), "MFT_MESSAGE_SET_D3D_MANAGER");
                IntPtr inType = MediaType(MFVideoFormat_H264, (uint)w, (uint)h);
                Ok(((delegate* unmanaged<IntPtr, uint, IntPtr, uint, int>)Fn(t, 15))(t, 0, inType, 0), "SetInputType");
                Release(inType);
                SetOutput(t);
                var units = Stream(w, h, 4, out var pictures);
                int textures = 0;
                var frames = Decode(t, units, s =>
                {
                    // the texture behind the buffer, and the buffer's bytes (read back from it)
                    IntPtr buf, dxgi, tex;
                    Ok(((delegate* unmanaged<IntPtr, uint, IntPtr*, int>)Fn(s, 40))(s, 0, &buf), "GetBufferByIndex");
                    var iid = IID_IMFDXGIBuffer;
                    Ok(((delegate* unmanaged<IntPtr, Guid*, IntPtr*, int>)Fn(buf, 0))(buf, &iid, &dxgi), "IMFDXGIBuffer");
                    var tiid = IID_ID3D11Texture2D;
                    Ok(((delegate* unmanaged<IntPtr, Guid*, IntPtr*, int>)Fn(dxgi, 3))(dxgi, &tiid, &tex), "GetResource");
                    D3D11_TEXTURE2D_DESC d;
                    ((delegate* unmanaged<IntPtr, D3D11_TEXTURE2D_DESC*, void>)Fn(tex, Vtbl.ID3D11Texture2D.GetDesc))(tex, &d);
                    Assert.Equal((DXGI_FORMAT)103, d.Format);
                    textures++;
                    Release(tex); Release(dxgi); Release(buf);
                    return SampleBytes(s);
                }, out _, out _);
                Assert.Equal(4, textures);
                Assert.Equal(4, frames.Count);
                for (int k = 0; k < frames.Count; k++) Assert.True(pictures[k].AsSpan().SequenceEqual(frames[k].AsSpan(0, pictures[k].Length)), $"frame {k} differs");
            }
            finally
            {
                Release(t); Release(manager); Release(ctx); Release(dev);
            }
        }
    }
}
