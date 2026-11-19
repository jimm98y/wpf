// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// The cameras of Linux, through Video4Linux2 (CaptureDevices.cs): each /dev/video* that captures video by streaming
// (VIDIOC_QUERYCAP), its card's name, its formats (VIDIOC_ENUM_FMT, _FRAMESIZES, _FRAMEINTERVALS) as NV12: of the
// device's MJPG where GStreamer decodes it (as Windows' frame server gives a camera of MJPG: a USB camera streams the
// larger sizes compressed only), else of its NV12 or YUYV; a session of one at a format: its buffers mapped
// (VIDIOC_REQBUFS, mmap), each frame dequeued on the session's thread and made tight NV12 (decoded by jpegdec; YUYV's
// chroma of each two rows averaged), of the time of its capture (the buffer's CLOCK_MONOTONIC timestamp, the system
// clock's).
//

using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Wpf.Interop.WebGpu.Direct3D;
using Microsoft.Wpf.Interop.WebGpu.MediaFoundation.Codecs;

namespace Microsoft.Wpf.Interop.WebGpu.MediaFoundation.Capture
{
    internal static unsafe class V4l2Capture
    {
        private const string Libc = "libc";
        [DllImport(Libc, SetLastError = true)] private static extern int open(string path, int flags);
        [DllImport(Libc)] private static extern int close(int fd);
        [DllImport(Libc, SetLastError = true)] private static extern int ioctl(int fd, nuint request, void* arg);
        [DllImport(Libc, SetLastError = true)] private static extern IntPtr mmap(IntPtr addr, nuint length, int prot, int flags, int fd, nint offset);
        [DllImport(Libc)] private static extern int munmap(IntPtr addr, nuint length);
        [DllImport(Libc)] private static extern int poll(PollFd* fds, nuint n, int timeoutMs);

        [StructLayout(LayoutKind.Sequential)] private struct PollFd { public int Fd; public short Events, REvents; }

        private const int O_RDWR = 2, O_NONBLOCK = 0x800;
        private const uint CAP_VIDEO_CAPTURE = 1, CAP_STREAMING = 0x04000000, CAP_DEVICE_CAPS = 0x80000000;
        private const uint BUF_TYPE_VIDEO_CAPTURE = 1, MEMORY_MMAP = 1, FIELD_NONE = 1, FRMSIZE_DISCRETE = 1, FRMIVAL_DISCRETE = 1;
        private const uint BUF_FLAG_TIMESTAMP_MONOTONIC = 0x2000, BUF_FLAG_TIMESTAMP_MASK = 0xe000;
        // the requests (_IOC: of the same numbers on the 64-bit architectures)
        private const nuint VIDIOC_QUERYCAP = 0x80685600, VIDIOC_ENUM_FMT = 0xC0405602, VIDIOC_S_FMT = 0xC0D05605, VIDIOC_REQBUFS = 0xC0145608;
        private const nuint VIDIOC_QUERYBUF = 0xC0585609, VIDIOC_QBUF = 0xC058560F, VIDIOC_DQBUF = 0xC0585611, VIDIOC_STREAMON = 0x40045612;
        private const nuint VIDIOC_STREAMOFF = 0x40045613, VIDIOC_S_PARM = 0xC0CC5616, VIDIOC_ENUM_FRAMESIZES = 0xC02C564A, VIDIOC_ENUM_FRAMEINTERVALS = 0xC034564B;
        private static readonly uint YUYV = Fourcc("YUYV"), FNV12 = Fourcc("NV12"), MJPG = Fourcc("MJPG");
        private static readonly Guid NV12 = new("3231564e-0000-0010-8000-00aa00389b71");

        private static uint Fourcc(string s) => (uint)(s[0] | s[1] << 8 | s[2] << 16 | s[3] << 24);

        /// <summary>The capture devices, in the order of their nodes; each its formats (a size and rate once), largest first.</summary>
        public static List<CaptureDevice> Devices()
        {
            var list = new List<CaptureDevice>();
            string[] nodes;
            try { nodes = Directory.GetFiles("/dev", "video*"); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return list; }
            Array.Sort(nodes, (a, b) => Num(a).CompareTo(Num(b)));
            foreach (var path in nodes)
            {
                int fd = open(path, O_RDWR | O_NONBLOCK);
                if (fd < 0) continue;
                try
                {
                    var cap = stackalloc byte[104];
                    if (ioctl(fd, VIDIOC_QUERYCAP, cap) < 0) continue;
                    uint caps = *(uint*)(cap + 84), dcaps = *(uint*)(cap + 88);
                    uint c = (caps & CAP_DEVICE_CAPS) != 0 ? dcaps : caps;
                    if ((c & CAP_VIDEO_CAPTURE) == 0 || (c & CAP_STREAMING) == 0) continue;
                    var dev = new CaptureDevice { Name = Str(cap + 16, 32), Id = path };
                    lock (s_pixel) Formats(fd, dev);
                    if (dev.Formats.Count > 0) list.Add(dev);
                }
                finally { close(fd); }
            }
            return list;
        }

        private static int Num(string path) => int.TryParse(Path.GetFileName(path).AsSpan(5), out int n) ? n : int.MaxValue;

        private static string Str(byte* p, int max)
        {
            int n = 0;
            while (n < max && p[n] != 0) n++;
            return System.Text.Encoding.UTF8.GetString(p, n);
        }

        private static void Formats(int fd, CaptureDevice dev)
        {
            var found = new List<(uint Pixel, int W, int H, double Min, double Max)>();
            var fmt = stackalloc byte[64];
            for (uint i = 0; ; i++)
            {
                new Span<byte>(fmt, 64).Clear();
                *(uint*)fmt = i; *(uint*)(fmt + 4) = BUF_TYPE_VIDEO_CAPTURE;
                if (ioctl(fd, VIDIOC_ENUM_FMT, fmt) < 0) break;
                uint pixel = *(uint*)(fmt + 44);
                if (pixel != YUYV && pixel != FNV12 && !(pixel == MJPG && Jpeg)) continue;
                var size = stackalloc byte[44];
                for (uint k = 0; ; k++)
                {
                    new Span<byte>(size, 44).Clear();
                    *(uint*)size = k; *(uint*)(size + 4) = pixel;
                    if (ioctl(fd, VIDIOC_ENUM_FRAMESIZES, size) < 0 || *(uint*)(size + 8) != FRMSIZE_DISCRETE) break;
                    int w = *(int*)(size + 12), h = *(int*)(size + 16);
                    double min = double.MaxValue, max = 0;
                    var iv = stackalloc byte[52];
                    for (uint j = 0; ; j++)
                    {
                        new Span<byte>(iv, 52).Clear();
                        *(uint*)iv = j; *(uint*)(iv + 4) = pixel; *(int*)(iv + 8) = w; *(int*)(iv + 12) = h;
                        if (ioctl(fd, VIDIOC_ENUM_FRAMEINTERVALS, iv) < 0) break;
                        uint num = *(uint*)(iv + 20), den = *(uint*)(iv + 24);
                        if (num == 0) continue;
                        double fps = (double)den / num;
                        if (*(uint*)(iv + 16) != FRMIVAL_DISCRETE)
                        {
                            // stepwise: from the longest interval to the shortest
                            uint mxn = *(uint*)(iv + 28), mxd = *(uint*)(iv + 32);
                            min = Math.Min(min, mxn == 0 ? fps : (double)mxd / mxn); max = Math.Max(max, fps);
                            break;
                        }
                        min = Math.Min(min, fps); max = Math.Max(max, fps);
                    }
                    if (max <= 0) continue;
                    found.Add((pixel, w, h, min, max));
                }
            }
            // a size and rate once (of MJPG before NV12 before YUYV where it has more than one), the largest first
            static int Rank(uint p) => p == MJPG ? 0 : p == FNV12 ? 1 : 2;
            found.Sort((a, b) => (b.W * b.H).CompareTo(a.W * a.H) is var c && c != 0 ? c : b.Max.CompareTo(a.Max) is var r && r != 0 ? r : Rank(a.Pixel).CompareTo(Rank(b.Pixel)));
            foreach (var f in found)
            {
                if (dev.Formats.Exists(x => x.Width == f.W && x.Height == f.H && x.MaxFps == f.Max)) continue;
                s_pixel[(dev.Id, f.W, f.H, f.Max)] = f.Pixel;
                dev.Formats.Add(new CaptureFormat { Subtype = NV12, Width = f.W, Height = f.H, MinFps = f.Min, MaxFps = f.Max, FullRange = true });
            }
        }

        /// <summary>Whether GStreamer decodes JPEG here (jpegdec, of gstreamer1.0-plugins-good).</summary>
        private static bool Jpeg
        {
            get
            {
                try { return OperatingSystem.IsLinux() && Gst.IsAvailable && Gst.HasElement("jpegdec"); }
                catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException or TypeInitializationException) { return false; }
            }
        }

        // the device's own pixel format of each format listed
        private static readonly Dictionary<(string, int, int, double), uint> s_pixel = new();

        public static ICaptureSession? Open(CaptureDevice device, CaptureFormat format, CaptureFrame frames)
        {
            uint pixel;
            lock (s_pixel) if (!s_pixel.TryGetValue((device.Id, format.Width, format.Height, format.MaxFps), out pixel)) pixel = YUYV;
            int fd = open(device.Id, O_RDWR | O_NONBLOCK);
            if (fd < 0) { D3DLog.Once("v4l2-open:" + device.Id, $"Media Foundation: the camera {device.Id} is not opened (errno {Marshal.GetLastPInvokeError()})"); return null; }
            try { return new Session(fd, pixel, format, frames); }
            catch (Exception e) { close(fd); D3DLog.Once("v4l2-session:" + device.Id, "Media Foundation: the camera is not started: " + e.Message); return null; }
        }

        private sealed class Session : ICaptureSession
        {
            private readonly int _fd;
            private readonly uint _pixel;
            private readonly CaptureFormat _format;
            private readonly CaptureFrame _frames;
            private readonly List<(IntPtr Addr, nuint Length)> _buffers = new();
            private int _stride;
            private GstAppPipeline? _jpeg;                  // MJPG's decoder
            private Thread? _thread;
            private volatile bool _running;
            private int _disposed;

            public Session(int fd, uint pixel, CaptureFormat f, CaptureFrame frames)
            {
                _fd = fd; _pixel = pixel; _format = f; _frames = frames;
                var fmt = stackalloc byte[208];
                *(uint*)fmt = BUF_TYPE_VIDEO_CAPTURE;
                *(uint*)(fmt + 8) = (uint)f.Width; *(uint*)(fmt + 12) = (uint)f.Height; *(uint*)(fmt + 16) = pixel; *(uint*)(fmt + 20) = FIELD_NONE;
                if (ioctl(fd, VIDIOC_S_FMT, fmt) < 0) throw new IOException($"VIDIOC_S_FMT: errno {Marshal.GetLastPInvokeError()}");
                if (*(uint*)(fmt + 8) != f.Width || *(uint*)(fmt + 12) != f.Height || *(uint*)(fmt + 16) != pixel) throw new IOException("the device took another format");
                _stride = (int)*(uint*)(fmt + 24);
                if (_stride == 0) _stride = pixel == YUYV ? f.Width * 2 : f.Width;
                if (pixel == MJPG && OperatingSystem.IsLinux())
                {
                    _jpeg = new GstAppPipeline($"appsrc name=src format=time is-live=true do-timestamp=false caps=\"image/jpeg,width={f.Width},height={f.Height}\" ! " +
                        "jpegdec ! videoconvert ! video/x-raw,format=NV12 ! appsink name=sink sync=false max-buffers=2 drop=true");
                    _jpeg.Play();
                }
                // the rate
                var parm = stackalloc byte[204];
                *(uint*)parm = BUF_TYPE_VIDEO_CAPTURE;
                var (n, d) = CaptureDevices.Rate(f.MaxFps);
                *(uint*)(parm + 12) = (uint)d; *(uint*)(parm + 16) = (uint)n;
                ioctl(fd, VIDIOC_S_PARM, parm);
                // four buffers, mapped
                var req = stackalloc byte[20];
                *(uint*)req = 4; *(uint*)(req + 4) = BUF_TYPE_VIDEO_CAPTURE; *(uint*)(req + 8) = MEMORY_MMAP;
                if (ioctl(fd, VIDIOC_REQBUFS, req) < 0) throw new IOException($"VIDIOC_REQBUFS: errno {Marshal.GetLastPInvokeError()}");
                uint count = *(uint*)req;
                for (uint i = 0; i < count; i++)
                {
                    var b = Buffer(i);
                    if (ioctl(fd, VIDIOC_QUERYBUF, b) < 0) throw new IOException("VIDIOC_QUERYBUF");
                    uint offset = *(uint*)(b + 64), length = *(uint*)(b + 72);
                    IntPtr p = mmap(IntPtr.Zero, length, 3, 1, fd, (nint)offset);          // PROT_READ | PROT_WRITE, MAP_SHARED
                    if (p == new IntPtr(-1)) throw new IOException("mmap");
                    _buffers.Add((p, length));
                }
            }

            private static byte* Buffer(uint index)
            {
                var b = (byte*)NativeMemory.AllocZeroed(88);
                *(uint*)b = index; *(uint*)(b + 4) = BUF_TYPE_VIDEO_CAPTURE; *(uint*)(b + 60) = MEMORY_MMAP;
                return b;
            }

            public void Start()
            {
                if (_running || _disposed != 0) return;
                for (uint i = 0; i < _buffers.Count; i++) { var b = Buffer(i); ioctl(_fd, VIDIOC_QBUF, b); NativeMemory.Free(b); }
                uint type = BUF_TYPE_VIDEO_CAPTURE;
                if (ioctl(_fd, VIDIOC_STREAMON, &type) < 0) { D3DLog.Once("v4l2-streamon", $"Media Foundation: VIDIOC_STREAMON: errno {Marshal.GetLastPInvokeError()}"); return; }
                _running = true;
                _thread = new Thread(Loop) { IsBackground = true, Name = "V4L2 capture" };
                _thread.Start();
            }

            public void Stop()
            {
                if (!_running) return;
                _running = false;
                _thread?.Join();
                _thread = null;
                uint type = BUF_TYPE_VIDEO_CAPTURE;
                ioctl(_fd, VIDIOC_STREAMOFF, &type);
            }

            public void Dispose()
            {
                if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
                Stop();
                foreach (var (a, l) in _buffers) munmap(a, l);
                if (OperatingSystem.IsLinux()) { _jpeg?.Stop(); _jpeg?.Dispose(); }
                _buffers.Clear();
                var req = stackalloc byte[20];
                *(uint*)(req + 4) = BUF_TYPE_VIDEO_CAPTURE; *(uint*)(req + 8) = MEMORY_MMAP;
                ioctl(_fd, VIDIOC_REQBUFS, req);
                close(_fd);
            }

            private void Loop()
            {
                var b = Buffer(0);
                try
                {
                    while (_running)
                    {
                        var pfd = new PollFd { Fd = _fd, Events = 1 };            // POLLIN
                        if (poll(&pfd, 1, 100) <= 0) continue;
                        new Span<byte>(b, 88).Clear();
                        *(uint*)(b + 4) = BUF_TYPE_VIDEO_CAPTURE; *(uint*)(b + 60) = MEMORY_MMAP;
                        if (ioctl(_fd, VIDIOC_DQBUF, b) < 0) continue;
                        uint index = *(uint*)b, used = *(uint*)(b + 8), flags = *(uint*)(b + 12);
                        long time = (flags & BUF_FLAG_TIMESTAMP_MASK) == BUF_FLAG_TIMESTAMP_MONOTONIC
                            ? *(long*)(b + 24) * 10_000_000 + *(long*)(b + 32) * 10 : CaptureDevices.Now();
                        byte[]? bytes = null;
                        if (index < _buffers.Count) bytes = Copy((byte*)_buffers[(int)index].Addr, (int)Math.Min(used, (uint)_buffers[(int)index].Length));
                        ioctl(_fd, VIDIOC_QBUF, b);
                        if (bytes != null)
                        {
                            try { _frames(bytes, time); }
                            catch (Exception e) { D3DLog.Error("V4L2 capture frame", e); }
                        }
                    }
                }
                finally { NativeMemory.Free(b); }
            }

            /// <summary>The frame as tight NV12: NV12's planes of the device's stride; YUYV's luma, its chroma of each two rows averaged.</summary>
            private byte[]? Copy(byte* src, int used)
            {
                int w = _format.Width, h = _format.Height, cw = (w + 1) / 2 * 2, ch = (h + 1) / 2;
                var o = new byte[w * h + cw * ch];
                if (_pixel == MJPG)
                {
                    if (_jpeg == null || !OperatingSystem.IsLinux()) return null;
                    if (!_jpeg.Push(default, new ReadOnlySpan<byte>(src, used), null)) return null;
                    IntPtr sample = _jpeg.Pull(TimeSpan.FromMilliseconds(500));
                    if (sample == IntPtr.Zero) return null;
                    try { return GstFrames.CopyNV12(sample, o, (uint)w, (uint)h, out _, out _) ? o : null; }
                    finally { Gst.gst_mini_object_unref(sample); }
                }
                if (_pixel == FNV12)
                {
                    if (used < _stride * h + _stride * ch) return null;
                    for (int y = 0; y < h; y++) new ReadOnlySpan<byte>(src + y * _stride, w).CopyTo(o.AsSpan(y * w));
                    for (int y = 0; y < ch; y++) new ReadOnlySpan<byte>(src + _stride * h + y * _stride, cw).CopyTo(o.AsSpan(w * h + y * cw));
                    return o;
                }
                if (used < _stride * h) return null;
                for (int y = 0; y < h; y++)
                {
                    byte* row = src + y * _stride;
                    for (int x = 0; x < w; x++) o[y * w + x] = row[x * 2];
                }
                for (int y = 0; y < ch; y++)
                {
                    byte* r0 = src + 2 * y * _stride, r1 = src + Math.Min(2 * y + 1, h - 1) * _stride;
                    for (int x = 0; x < cw / 2; x++)
                    {
                        int at = w * h + y * cw + x * 2;
                        o[at] = (byte)((r0[x * 4 + 1] + r1[x * 4 + 1] + 1) >> 1);
                        o[at + 1] = (byte)((r0[x * 4 + 3] + r1[x * 4 + 3] + 1) >> 1);
                    }
                }
                return o;
            }
        }
    }
}
