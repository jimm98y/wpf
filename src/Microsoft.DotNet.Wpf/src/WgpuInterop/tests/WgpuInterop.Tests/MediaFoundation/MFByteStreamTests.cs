// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// Byte streams of the application's: MFCreateMFByteStreamOnStream over a COM IStream (here of memory), written by
// the sink writer and read back by the source reader; MFCreateTempFile. On Windows the tests drive Windows' own.
//

using System;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Wpf.Interop.WebGpu.Direct3D;
using Microsoft.Wpf.Interop.WebGpu.MediaFoundation;
using Xunit;

namespace WgpuInterop.Tests.MediaFoundation
{
    public sealed unsafe class MFByteStreamTests
    {
        [DllImport("mfplat.dll", EntryPoint = "MFCreateMFByteStreamOnStream")] private static extern int WinOnStream(IntPtr stream, IntPtr* bs);
        [DllImport("mfplat.dll", EntryPoint = "MFCreateTempFile")] private static extern int WinTempFile(uint access, uint open, uint flags, IntPtr* bs);
        [DllImport("mfreadwrite.dll", EntryPoint = "MFCreateSinkWriterFromURL")] private static extern int WinSink([MarshalAs(UnmanagedType.LPWStr)] string? url, IntPtr stream, IntPtr attributes, IntPtr* writer);
        [DllImport("mfreadwrite.dll", EntryPoint = "MFCreateSourceReaderFromByteStream")] private static extern int WinReader(IntPtr stream, IntPtr attributes, IntPtr* reader);

        private static readonly Guid MAJOR = new("48eba18e-f8c9-4687-bf11-0a74c9f96a8f"), SUB = new("f7e34c9a-42e8-4714-b74b-cb29d72c35e5");
        private static readonly Guid VIDEO = new("73646976-0000-0010-8000-00aa00389b71"), H264 = new("34363248-0000-0010-8000-00aa00389b71"), NV12 = new("3231564e-0000-0010-8000-00aa00389b71");
        private static readonly Guid FRAME_SIZE = new("1652c33d-d6b2-4012-b834-72030849a37d"), FRAME_RATE = new("c459a2e8-3d2c-4e44-b132-fee5156c7bb0");


        private static int OnStream(IntPtr s, out IntPtr bs) { IntPtr b; int hr = OperatingSystem.IsWindows() ? WinOnStream(s, &b) : MFExports.MFCreateMFByteStreamOnStream(s, &b); bs = hr >= 0 ? b : IntPtr.Zero; return hr; }

        /// <summary>Five frames written through an IStream of memory; the file there read back through another: the five samples.</summary>
        [Fact]
        public void AnIStream_IsWrittenByTheSinkWriter_AndReadByTheSourceReader()
        {
            Assert.SkipWhen(!OperatingSystem.IsWindows() && !Microsoft.Wpf.Interop.WebGpu.MediaFoundation.Codecs.PlatformVideoEncoder.Supports(Microsoft.Wpf.Interop.WebGpu.MediaFoundation.Codecs.VideoCodec.H264), "no H.264 encoder here");
            MFReader.Startup();
            var mem = new MemoryIStream();
            Assert.Equal(0, OnStream(mem.Pointer, out var bs));
            var attrs = MFReader.Attributes();
            MFReader.SetGuid(attrs, new Guid("150ff23f-4abc-478b-ac4f-e1916fba1cca"), new Guid("dc6cd05d-b9d0-40ef-bd35-fa622c1ab28a"));       // MF_TRANSCODE_CONTAINERTYPE: MPEG-4
            IntPtr w;
            int hr = OperatingSystem.IsWindows() ? WinSink(null, bs, attrs, &w) : MFExports.MFCreateSinkWriterFromURL(null, bs, attrs, &w);
            MFReader.Release(attrs);
            Assert.Equal(0, hr);
            var o = MFReader.MediaType();
            MFReader.SetGuid(o, MAJOR, VIDEO); MFReader.SetGuid(o, SUB, H264);
            MFReader.SetU64(o, FRAME_SIZE, MF.Pack(128, 96)); MFReader.SetU64(o, FRAME_RATE, MF.Pack(25, 1));
            MFReader.SetU32(o, new Guid("20332624-fb0d-4d9e-bd0d-cbf6786c102e"), 500_000); MFReader.SetU32(o, new Guid("e2724bb8-e676-4806-b4b2-a8d6efb44ccd"), 2);
            Assert.Equal(0, MFWriter.AddStream(w, o, out uint vi));
            MFReader.Release(o);
            var i = MFReader.MediaType();
            MFReader.SetGuid(i, MAJOR, VIDEO); MFReader.SetGuid(i, SUB, NV12);
            MFReader.SetU64(i, FRAME_SIZE, MF.Pack(128, 96)); MFReader.SetU64(i, FRAME_RATE, MF.Pack(25, 1)); MFReader.SetU32(i, new Guid("e2724bb8-e676-4806-b4b2-a8d6efb44ccd"), 2);
            Assert.Equal(0, MFWriter.SetInputMediaType(w, vi, i));
            MFReader.Release(i);
            Assert.Equal(0, MFWriter.BeginWriting(w));
            for (int k = 0; k < 5; k++)
            {
                var s = MFWriter.Sample(MFDecoderTests.Picture(128, 96, k), k * 400_000L, 400_000);
                Assert.Equal(0, MFWriter.WriteSample(w, vi, s));
                MFReader.Release(s);
            }
            Assert.Equal(0, MFWriter.Finalize(w));
            MFReader.Release(w);
            MFReader.Release(bs);
            var file = mem.Data.ToArray();
            Assert.True(file.Length > 1000 && file.AsSpan(4, 4).SequenceEqual("ftyp"u8), $"{file.Length} bytes");

            // read back, through a byte stream over another IStream of the bytes
            var back = new MemoryIStream();
            back.Data.Write(file);
            back.Data.Position = 0;
            Assert.Equal(0, OnStream(back.Pointer, out var rbs));
            IntPtr r;
            hr = OperatingSystem.IsWindows() ? WinReader(rbs, IntPtr.Zero, &r) : MFExports.MFCreateSourceReaderFromByteStream(rbs, IntPtr.Zero, &r);
            Assert.Equal(0, hr);
            int n = 0;
            for (int k = 0; k < 20; k++)
            {
                Assert.Equal(0, MFReader.ReadSample(r, 0, out _, out uint flags, out _, out var smp));
                if (smp != IntPtr.Zero) { n++; MFReader.Release(smp); }
                if ((flags & 2) != 0) break;
            }
            MFReader.Release(r);
            MFReader.Release(rbs);
            Assert.Equal(5, n);
            mem.Release(); back.Release();
        }

        /// <summary>A temporary file's byte stream: readable, writable, seekable; what is written read back.</summary>
        [Fact]
        public void ATempFile_IsAByteStreamOfItsOwn()
        {
            MFReader.Startup();
            IntPtr bs;
            Assert.Equal(0, OperatingSystem.IsWindows() ? WinTempFile(3, 4, 0, &bs) : MFExports.MFCreateTempFile(3, 4, 0, &bs));
            uint caps;
            Assert.Equal(0, ((delegate* unmanaged<IntPtr, uint*, int>)MFReader.Fn(bs, 3))(bs, &caps));
            Assert.Equal(7u, caps & 7);
            var data = new byte[] { 1, 2, 3, 4, 5 };
            uint n;
            fixed (byte* p = data) Assert.Equal(0, ((delegate* unmanaged<IntPtr, byte*, uint, uint*, int>)MFReader.Fn(bs, 12))(bs, p, 5, &n));
            Assert.Equal(0, ((delegate* unmanaged<IntPtr, ulong, int>)MFReader.Fn(bs, 7))(bs, 1));
            var got = new byte[4];
            fixed (byte* p = got) Assert.Equal(0, ((delegate* unmanaged<IntPtr, byte*, uint, uint*, int>)MFReader.Fn(bs, 9))(bs, p, 4, &n));
            Assert.Equal(new byte[] { 2, 3, 4, 5 }, got);
            MFReader.Release(bs);
        }
    }

    // (at the namespace's level: the browser's runtime finds no callback of a nested class)
    /// <summary>A COM IStream of memory.</summary>
    internal sealed unsafe class MemoryIStream : ComObject
    {
        private static readonly Vtable s_vt = Build();
        public readonly MemoryStream Data = new();
        public MemoryIStream() : base(s_vt) { }
        private static Vtable Build()
        {
            var vt = new Vtable("IStream", 14, new Guid("0000000c-0000-0000-C000-000000000046"), new Guid("0c733a30-2a1c-11ce-ade5-00aa0044773d"));
            vt.Set(3, (delegate* unmanaged<IntPtr, byte*, uint, uint*, int>)&Read);
            vt.Set(4, (delegate* unmanaged<IntPtr, byte*, uint, uint*, int>)&Write);
            vt.Set(5, (delegate* unmanaged<IntPtr, long, uint, ulong*, int>)&Seek);
            vt.Set(6, (delegate* unmanaged<IntPtr, ulong, int>)&SetSize);
            vt.Set(7, (delegate* unmanaged<IntPtr, IntPtr, ulong, ulong*, ulong*, int>)&CopyTo);
            vt.Set(8, (delegate* unmanaged<IntPtr, uint, int>)&Commit);
            vt.Set(9, (delegate* unmanaged<IntPtr, int>)&Revert);
            vt.Set(10, (delegate* unmanaged<IntPtr, ulong, ulong, uint, int>)&Region);
            vt.Set(11, (delegate* unmanaged<IntPtr, ulong, ulong, uint, int>)&Region);
            vt.Set(12, (delegate* unmanaged<IntPtr, byte*, uint, int>)&Stat);
            vt.Set(13, (delegate* unmanaged<IntPtr, IntPtr*, int>)&Clone);
            return vt;
        }
        private static MemoryIStream S(IntPtr self) => From<MemoryIStream>(self)!;
        [UnmanagedCallersOnly] private static int Read(IntPtr self, byte* p, uint n, uint* got) { int k = S(self).Data.Read(new Span<byte>(p, (int)n)); if (got != null) *got = (uint)k; return k < n ? 1 : 0; }
        [UnmanagedCallersOnly] private static int Write(IntPtr self, byte* p, uint n, uint* wrote) { S(self).Data.Write(new ReadOnlySpan<byte>(p, (int)n)); if (wrote != null) *wrote = n; return 0; }
        [UnmanagedCallersOnly] private static int Seek(IntPtr self, long off, uint origin, ulong* at) { long p = S(self).Data.Seek(off, (SeekOrigin)origin); if (at != null) *at = (ulong)p; return 0; }
        [UnmanagedCallersOnly] private static int SetSize(IntPtr self, ulong n) { S(self).Data.SetLength((long)n); return 0; }
        [UnmanagedCallersOnly] private static int CopyTo(IntPtr self, IntPtr o, ulong n, ulong* r, ulong* w) => unchecked((int)0x80004001);
        [UnmanagedCallersOnly] private static int Commit(IntPtr self, uint f) => 0;
        [UnmanagedCallersOnly] private static int Revert(IntPtr self) => 0;
        [UnmanagedCallersOnly] private static int Region(IntPtr self, ulong a, ulong b, uint t) => unchecked((int)0x80030001);   // STG_E_INVALIDFUNCTION
        [UnmanagedCallersOnly] private static int Stat(IntPtr self, byte* st, uint flags) { new Span<byte>(st, 80).Clear(); *(uint*)(st + 8) = 2; *(long*)(st + 16) = S(self).Data.Length; *(uint*)(st + 48) = 2; return 0; }   // STGTY_STREAM, STGM_READWRITE
        [UnmanagedCallersOnly] private static int Clone(IntPtr self, IntPtr* o) { *o = IntPtr.Zero; return unchecked((int)0x80004001); }
    }
}
