// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// The video capture devices of MFEnumDeviceSources and MFCreateDeviceSource (Documentation/media-foundation-shim.md,
// "Capture"), as Windows' device source behaves (probed: MFCaptureProbe, the report in Capture.windows.txt):
//
//   - the activate's attributes: the device's friendly name, its symbolic link, its category (KSCATEGORY_VIDEO_CAMERA),
//     the source type (VIDCAP), the major type and subtypes the device gives, MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE_VIDCAP_HW_SOURCE;
//   - a live source (MFMEDIASOURCE_IS_LIVE | CAN_PAUSE) of one video stream, selected: the device's formats each a
//     type of the stream's handler (NV12, of the frame-server attributes Windows' types carry), the first current;
//   - Start opens the camera at the handler's current type: MENewStream, MESourceStarted, MEStreamStarted and a
//     MEStreamTick, each of the system time (MFGetSystemTime's) then; then each frame a RequestSample asked for (or
//     asks for: the latest few kept until then) as MEMediaSample, of the system time of its capture and the frame
//     server's attributes; Pause holds them, Stop closes the camera; after Shutdown the device is gone.
//

using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using Microsoft.Wpf.Interop.WebGpu.Direct3D;
using Microsoft.Wpf.Interop.WebGpu.MediaFoundation.Source;

namespace Microsoft.Wpf.Interop.WebGpu.MediaFoundation.Capture
{
    internal static class CaptureIds
    {
        public static readonly Guid MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE = new("c60ac5fe-252a-478f-a0ef-bc8fa5f7cad3");
        public static readonly Guid VIDCAP = new("8ac3587a-4ae7-42d8-99e0-0a6013eef90f"), AUDCAP = new("14dd9a1c-7cff-41be-b1b9-ba1ac6ecb571");
        public static readonly Guid MF_DEVSOURCE_ATTRIBUTE_FRIENDLY_NAME = new("60d0e559-52f8-4fa2-bbce-acdb34a8ec01");
        public static readonly Guid MF_DEVSOURCE_ATTRIBUTE_MEDIA_TYPE = new("56a819ca-0c78-4de4-a0a7-3ddaba0f24d4");
        public static readonly Guid VIDCAP_SYMBOLIC_LINK = new("58f0aad8-22bf-4f8a-bb3d-d2c4978c6e2f");
        public static readonly Guid VIDCAP_CATEGORY = new("77f0ae69-c3bd-4509-941d-467e4d24899e");
        public static readonly Guid VIDCAP_HW_SOURCE = new("de7046ba-54d6-4487-a2a4-ec7c0d1bd163");
        public static readonly Guid KSCATEGORY_VIDEO_CAMERA = new("e5323777-f976-4f5b-9b55-b94699c46e44");
        public const int E_ACCESSDENIED = unchecked((int)0x80070005);
        public const int MF_E_NO_CAPTURE_DEVICES_AVAILABLE = unchecked((int)0xC00D3E85), E_PATH_NOT_FOUND = unchecked((int)0x80070003);
        // the stream descriptor's: MF_DEVICESTREAM_ATTRIBUTE_FRAMESOURCE_TYPES (color), MF_DEVICESTREAM_STREAM_CATEGORY (PINNAME_VIDEO_CAPTURE)
        public static readonly Guid MF_DEVICESTREAM_ATTRIBUTE_FRAMESOURCE_TYPES = new("17145fd1-1b2b-423c-8001-2b6833ed3588"), MF_DEVICESTREAM_STREAM_CATEGORY = new("2939e7b8-a62e-4579-b674-d4073dfabbba");
        public static readonly Guid PINNAME_VIDEO_CAPTURE = new("fb6c4281-0353-11d1-905f-0000c0cc16ba");

        /// <summary>A device's symbolic link, of the shape of Windows' (\\?\bus#device#instance#{category}\global).</summary>
        public static string Link(CaptureDevice d)
        {
            var id = new System.Text.StringBuilder();
            foreach (char c in d.Id.ToLowerInvariant()) id.Append(char.IsLetterOrDigit(c) ? c : '_');
            return @"\\?\avcapture#" + id + "#0#{" + KSCATEGORY_VIDEO_CAMERA + @"}\global";
        }

        /// <summary>The activate of a camera: its attributes, as Windows' have them.</summary>
        public static MFActivate Activate(CaptureDevice d)
        {
            // the source made at the first ActivateObject: the camera refused where the application may not use it
            var a = new MFActivate(() => CaptureDevices.Allowed ? (MF.S_OK, new MFCaptureSource(d).Pointer) : (E_ACCESSDENIED, IntPtr.Zero), MF.S_OK);
            var blob = new List<byte>();
            foreach (var s in d.Formats.Select(f => f.Subtype).Distinct()) { blob.AddRange(MF.MFMediaType_Video.ToByteArray()); blob.AddRange(s.ToByteArray()); }
            a.Attributes.Set(MF_DEVSOURCE_ATTRIBUTE_MEDIA_TYPE, MFValue.Of(blob.ToArray()));
            a.Attributes.Set(VIDCAP_SYMBOLIC_LINK, MFValue.Of(Link(d)));
            a.Attributes.Set(MF_DEVSOURCE_ATTRIBUTE_FRIENDLY_NAME, MFValue.Of(d.Name));
            a.Attributes.Set(VIDCAP_CATEGORY, MFValue.Of(KSCATEGORY_VIDEO_CAMERA));
            a.Attributes.Set(MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE, MFValue.Of(VIDCAP));
            a.Attributes.Set(VIDCAP_HW_SOURCE, 4u);
            return a;
        }
    }

    /// <summary>IMFMediaSource of a camera.</summary>
    internal sealed unsafe class MFCaptureSource : ComObject
    {
        private static readonly Vtable s_vt = Build();
        private static readonly Guid MF_MT_AM_FORMAT_TYPE = new("73d1072d-1870-4174-a063-29ff4ff6c11e"), FORMAT_VideoInfo2 = new("f72a76a0-eb0a-11d0-ace4-0000c0cc16ba");
        private static readonly Guid MF_MT_VIDEO_CHROMA_SITING = new("65df2370-c773-4c33-aa64-843e068efb0c"), MF_MT_VIDEO_NOMINAL_RANGE = new("c21b8ee5-b956-4071-8daf-325edf5cab11");
        private static readonly Guid MF_MT_VIDEO_PRIMARIES = new("dbfbe4d7-0740-4ee0-8192-850ab0e21935"), MF_MT_YUV_MATRIX = new("3e23d450-2c75-4d25-a00e-b91670d12327");
        private static readonly Guid MF_MT_FRAME_RATE_RANGE_MAX = new("d2e7558c-dc1f-403f-9a72-d28bb1eb3b5e"), MF_MT_FRAME_RATE_RANGE_MIN = new("e3371d41-b4cf-4a05-bd4e-20b88bb2c4d6");
        // the frame server's: of each type (3), of the NV12 ones (1)
        private static readonly Guid FrameServerType = new("53a0529c-890b-4216-8bf9-599367ad6d20"), FrameServerNv12 = new("ea031a62-8bbb-43c5-b5c4-572d2d231c18");

        private readonly CaptureDevice _device;
        private readonly MFEventQueue _queue = new();
        private readonly MFPresentationDescriptor _pd = new();
        private readonly MFStreamDescriptor _sd;
        internal readonly MFCaptureStream Stream;
        internal readonly object Lock = new();
        private enum State { Stopped, Started, Paused, Shutdown }
        private State _state;
        private ICaptureSession? _session;
        private MFMediaType? _openAt;
        private int _generation;                    // of the session open: a frame of one closed since is dropped

        private static Vtable Build()
        {
            var vt = new Vtable("IMFMediaSource", 13, MS.IID_IMFMediaSource, MS.IID_IMFMediaEventGenerator);
            vt.Set(3, (delegate* unmanaged<IntPtr, uint, IntPtr*, int>)&GetEvent);
            vt.Set(4, (delegate* unmanaged<IntPtr, IntPtr, IntPtr, int>)&BeginGetEvent);
            vt.Set(5, (delegate* unmanaged<IntPtr, IntPtr, IntPtr*, int>)&EndGetEvent);
            vt.Set(6, (delegate* unmanaged<IntPtr, uint, Guid*, int, byte*, int>)&QueueEvent);
            vt.Set(7, (delegate* unmanaged<IntPtr, uint*, int>)&GetCharacteristics);
            vt.Set(8, (delegate* unmanaged<IntPtr, IntPtr*, int>)&CreatePresentationDescriptor);
            vt.Set(9, (delegate* unmanaged<IntPtr, IntPtr, Guid*, byte*, int>)&Start);
            vt.Set(10, (delegate* unmanaged<IntPtr, int>)&Stop);
            vt.Set(11, (delegate* unmanaged<IntPtr, int>)&Pause);
            vt.Set(12, (delegate* unmanaged<IntPtr, int>)&ShutdownSlot);
            return vt;
        }

        public MFCaptureSource(CaptureDevice device) : base(s_vt)
        {
            _device = device;
            _sd = new MFStreamDescriptor(0, device.Formats.Select(TypeOf));
            _sd.Attributes.Set(CaptureIds.MF_DEVICESTREAM_ATTRIBUTE_FRAMESOURCE_TYPES, 1u);
            _sd.Attributes.Set(CaptureIds.MF_DEVICESTREAM_STREAM_CATEGORY, MFValue.Of(CaptureIds.PINNAME_VIDEO_CAPTURE));
            if (_sd.Handler.Types.Count > 0) { _sd.Handler.Types[0].AddRef(); _sd.Handler.Current = _sd.Handler.Types[0]; }
            _pd.Add(_sd.Pointer, true);
            Stream = new MFCaptureStream(this, _sd);
            _sd.Handler.CurrentChanged = Switch;
        }

        /// <summary>A format of the device as the stream's type.</summary>
        internal static MFMediaType TypeOf(CaptureFormat f)
        {
            var t = new MFMediaType();
            var a = t.Attributes;
            var (n, d) = CaptureDevices.Rate(f.MaxFps);
            var (mn, md) = CaptureDevices.Rate(f.MinFps);
            a.Set(FrameServerType, 3u);
            a.Set(MF_MT_AM_FORMAT_TYPE, FORMAT_VideoInfo2);
            a.Set(MF.MF_MT_ALL_SAMPLES_INDEPENDENT, 1u);
            a.Set(MF.MF_MT_FIXED_SIZE_SAMPLES, 1u);
            a.Set(MF.MF_MT_FRAME_RATE, MF.Pack((uint)n, (uint)d));
            a.Set(MF.MF_MT_FRAME_SIZE, MF.Pack((uint)f.Width, (uint)f.Height));
            a.Set(MF.MF_MT_INTERLACE_MODE, 2u);
            a.Set(MF.MF_MT_MAJOR_TYPE, MF.MFMediaType_Video);
            a.Set(MF.MF_MT_PIXEL_ASPECT_RATIO, MF.Pack(1, 1));
            a.Set(MF.MF_MT_SUBTYPE, f.Subtype);
            a.Set(MF_MT_VIDEO_CHROMA_SITING, 1u);
            a.Set(MF_MT_VIDEO_NOMINAL_RANGE, f.FullRange ? 1u : 2u);
            a.Set(MF_MT_VIDEO_PRIMARIES, 2u);
            a.Set(MF_MT_YUV_MATRIX, 1u);
            a.Set(MF_MT_FRAME_RATE_RANGE_MAX, MF.Pack((uint)n, (uint)d));
            a.Set(MF_MT_FRAME_RATE_RANGE_MIN, MF.Pack((uint)mn, (uint)md));
            a.Set(FrameServerNv12, 1u);
            return t;
        }

        protected override void OnFinalRelease()
        {
            Shutdown();
            _pd.Release();
            _sd.Release();
            Stream.Release();
        }

        public bool IsShutdown { get { lock (Lock) return _state == State.Shutdown; } }
        internal bool Started { get { lock (Lock) return _state == State.Started; } }
        internal int RequestState => _state switch { State.Shutdown => MF.MF_E_SHUTDOWN, State.Stopped => MS.MF_E_MEDIA_SOURCE_WRONGSTATE, State.Paused => 1, _ => 0 };

        public void Shutdown()
        {
            ICaptureSession? s;
            lock (Lock)
            {
                if (_state == State.Shutdown) return;
                _state = State.Shutdown;
                _queue.Shutdown();
                Stream.Shut();
                s = _session; _session = null; _generation++;
                _openAt?.Release(); _openAt = null;
            }
            s?.Dispose();
        }

        /// <summary>The frames of a session whose frames are taken (the browser's), taken now.</summary>
        internal void Pump()
        {
            ICaptureSession? s;
            lock (Lock) s = _state == State.Started ? _session : null;
            // (under the callers' vtable slots: nothing thrown out of them)
            try { s?.Pump(); }
            catch (Exception e) { D3DLog.Error("the camera's frames", e); }
        }

        private void Queue(uint type, MFValue? value = null) => _queue.QueueEvent(type, Guid.Empty, MF.S_OK, value);

        /// <summary>A frame of the camera, of the system time of its capture.</summary>
        private void OnFrame(byte[] bytes, long clock, int generation)
        {
            lock (Lock)
            {
                if (_state is State.Stopped or State.Shutdown || generation != _generation) return;
                Stream.Frame(bytes, clock);
            }
        }

        private static MFCaptureSource? S(IntPtr self) => From<MFCaptureSource>(self);

        // ---- IMFMediaEventGenerator ------------------------------------------------------------------------

        [UnmanagedCallersOnly]
        private static int GetEvent(IntPtr self, uint flags, IntPtr* e)
        {
            if (S(self) is not { } s || e == null) return MF.E_POINTER;
            s.Pump();
            int hr = s._queue.GetEvent((flags & 1) != 0, out var ev);
            *e = ev;
            return hr;
        }

        [UnmanagedCallersOnly] private static int BeginGetEvent(IntPtr self, IntPtr callback, IntPtr state) => S(self)?._queue.BeginGetEvent(callback, state) ?? MF.E_POINTER;

        [UnmanagedCallersOnly]
        private static int EndGetEvent(IntPtr self, IntPtr result, IntPtr* e)
        {
            if (S(self) is not { } s || e == null) return MF.E_POINTER;
            int hr = s._queue.EndGetEvent(result, out var ev);
            *e = ev;
            return hr;
        }

        [UnmanagedCallersOnly]
        private static int QueueEvent(IntPtr self, uint type, Guid* ext, int status, byte* value)
            => S(self)?._queue.QueueEvent(type, ext == null ? Guid.Empty : *ext, status, MFMediaEvent.ValueOf(value)) ?? MF.E_POINTER;

        // ---- IMFMediaSource --------------------------------------------------------------------------------

        [UnmanagedCallersOnly]
        private static int GetCharacteristics(IntPtr self, uint* c)
        {
            if (S(self) is not { } s || c == null) return MF.E_POINTER;
            if (s.IsShutdown) return CaptureIds.MF_E_NO_CAPTURE_DEVICES_AVAILABLE;         // (Windows': the device gone)
            *c = 5;                     // MFMEDIASOURCE_IS_LIVE | MFMEDIASOURCE_CAN_PAUSE
            return MF.S_OK;
        }

        [UnmanagedCallersOnly]
        private static int CreatePresentationDescriptor(IntPtr self, IntPtr* pd)
        {
            if (S(self) is not { } s || pd == null) return MF.E_POINTER;
            *pd = IntPtr.Zero;
            if (s.IsShutdown) return MF.MF_E_SHUTDOWN;
            *pd = s._pd.CloneOf().Pointer;
            return MF.S_OK;
        }

        [UnmanagedCallersOnly]
        private static int Start(IntPtr self, IntPtr pd, Guid* format, byte* position)
        {
            if (S(self) is not { } s) return MF.E_POINTER;
            try { return s.StartAt(pd, format, position); }
            catch (Exception e) { D3DLog.Error("IMFMediaSource.Start (capture)", e); return MF.E_FAIL; }
        }

        private int StartAt(IntPtr pd, Guid* format, byte* position)
        {
            // a camera open at another type closed first (a device opens once at a time: V4L2's EBUSY)
            ICaptureSession? open, stale = null;
            lock (Lock)
            {
                var want = _sd.Handler.Current ?? _sd.Handler.Types.FirstOrDefault();
                if (_session != null && !ReferenceEquals(_openAt, want)) { stale = _session; _session = null; _openAt?.Release(); _openAt = null; _generation++; }
            }
            stale?.Dispose();
            lock (Lock)
            {
                if (_state == State.Shutdown) return MF.MF_E_SHUTDOWN;
                if (pd == IntPtr.Zero) return MF.E_INVALIDARG;
                if (format != null && *format != Guid.Empty) return MF.E_INVALIDARG;
                ushort vt = position == null ? (ushort)0 : *(ushort*)position;
                if (vt != 0 && vt != 20) return MF.MF_E_INVALIDTYPE;
                // the stream selected or not
                int sel = 0;
                uint n;
                ((delegate* unmanaged<IntPtr, uint*, int>)Com.Fn(pd, 33))(pd, &n);
                if (n > 0) { IntPtr sd; if (((delegate* unmanaged<IntPtr, uint, int*, IntPtr*, int>)Com.Fn(pd, 34))(pd, 0, &sel, &sd) >= 0) Com.Release(sd); }
                bool resume = _state == State.Paused;
                // the camera at the handler's current type
                var current = _sd.Handler.Current ?? _sd.Handler.Types.FirstOrDefault();
                if (current == null) return MF.MF_E_INVALIDMEDIATYPE;
                if (_session == null && sel != 0)
                {
                    var f = FormatOf(current);
                    if (f == null) return MF.MF_E_INVALIDMEDIATYPE;
                    int gen = ++_generation;
                    _session = CaptureDevices.Open(_device, f, (b, t) => OnFrame(b, t, gen), out int ohr);
                    if (_session == null) return ohr;
                    current.AddRef(); _openAt = current;
                    Stream.Opened(f);
                    resume = false;
                }
                open = _session;
                if (sel != 0)
                {
                    bool was = Stream.Created;
                    Stream.Active = Stream.Created = true;
                    Com.AddRef(Stream.Pointer);
                    Queue(was ? MS.MEUpdatedStream : MS.MENewStream, new MFValue { Type = MF.VT_UNKNOWN, Unknown = Stream.Pointer });
                }
                else Stream.Active = false;
                ulong now = (ulong)CaptureDevices.Now();
                _state = State.Started;
                Queue(MS.MESourceStarted, new MFValue { Type = 20, U = now });
                if (Stream.Active)
                {
                    Stream.Queue(MS.MEStreamStarted, new MFValue { Type = 20, U = now });
                    if (!resume) Stream.Tick((long)now);
                    Stream.Resume();
                }
            }
            open?.Start();
            return MF.S_OK;
        }

        /// <summary>The handler's current type changed: a camera open goes on at it (no event: the frames after of its size).</summary>
        private void Switch(MFMediaType t)
        {
            ICaptureSession? open = null, close;
            CaptureFormat? f;
            lock (Lock)
            {
                if (_session == null || ReferenceEquals(_openAt, t) || (f = FormatOf(t)) == null) return;
                close = _session; _session = null; _openAt?.Release(); _openAt = null; _generation++;
            }
            // the camera closed before it is opened again (a device opens once at a time)
            close.Dispose();
            lock (Lock)
            {
                if (_state == State.Shutdown || _session != null) return;
                int gen = ++_generation;
                _session = CaptureDevices.Open(_device, f, (b, tm) => OnFrame(b, tm, gen), out _);
                if (_session != null) { t.AddRef(); _openAt = t; Stream.Opened(f); open = _session; }
            }
            if (open != null && Started) open.Start();
        }

        /// <summary>The device's format of a type of the handler's.</summary>
        private CaptureFormat? FormatOf(MFMediaType t)
        {
            ulong size = t.Attributes.U64(MF.MF_MT_FRAME_SIZE), rate = t.Attributes.U64(MF.MF_MT_FRAME_RATE);
            foreach (var f in _device.Formats)
            {
                var (n, d) = CaptureDevices.Rate(f.MaxFps);
                if (f.Subtype == t.Subtype && MF.Pack((uint)f.Width, (uint)f.Height) == size && MF.Pack((uint)n, (uint)d) == rate) return f;
            }
            return null;
        }

        [UnmanagedCallersOnly]
        private static int Stop(IntPtr self)
        {
            if (S(self) is not { } s) return MF.E_POINTER;
            ICaptureSession? close;
            lock (s.Lock)
            {
                if (s._state == State.Shutdown) return MF.MF_E_SHUTDOWN;
                s._state = State.Stopped;
                close = s._session; s._session = null; s._generation++;
                s._openAt?.Release(); s._openAt = null;
                s.Queue(MS.MESourceStopped);
                if (s.Stream.Active) s.Stream.Stopped();
            }
            try { close?.Dispose(); }
            catch (Exception e) { D3DLog.Error("IMFMediaSource.Stop (capture)", e); }
            return MF.S_OK;
        }

        [UnmanagedCallersOnly]
        private static int Pause(IntPtr self)
        {
            if (S(self) is not { } s) return MF.E_POINTER;
            lock (s.Lock)
            {
                if (s._state == State.Shutdown) return MF.MF_E_SHUTDOWN;
                if (s._state != State.Started) return MS.MF_E_MEDIA_SOURCE_WRONGSTATE;
                s._state = State.Paused;
                s.Queue(MS.MESourcePaused);
                if (s.Stream.Active) s.Stream.Queue(MS.MEStreamPaused);
            }
            return MF.S_OK;
        }

        [UnmanagedCallersOnly]
        private static int ShutdownSlot(IntPtr self)
        {
            if (S(self) is not { } s) return MF.E_POINTER;
            try { s.Shutdown(); }
            catch (Exception e) { D3DLog.Error("IMFMediaSource.Shutdown (capture)", e); }
            return MF.S_OK;
        }
    }

    /// <summary>IMFMediaStream of a camera: its frames, a RequestSample each.</summary>
    internal sealed unsafe class MFCaptureStream : ComObject
    {
        private static readonly Vtable s_vt = Build();
        private const int Kept = 3;                                 // frames kept for the requests to come; older ones dropped
        private readonly MFCaptureSource _source;
        private readonly MFEventQueue _queue = new();
        private readonly MFStreamDescriptor _sd;
        private readonly Queue<IntPtr> _requests = new();           // tokens of the requests not answered (AddRef'd; zero of none)
        private readonly Queue<(byte[] Bytes, long Time)> _frames = new();
        private bool _shut, _discontinuity = true;
        private CaptureFormat? _format;
        private static readonly Guid FrameServerFormat = new("03ffdcb2-c32c-4b92-98ff-87c0333db253"), FrameServerA = new("137e6b95-4cad-4cb9-9b7f-65cef2068a41");
        private static readonly Guid FrameServerB = new("3680913a-0e20-4b48-9364-be67fbf1a5e6"), MFSampleExtension_DeviceTimestamp = new("8f3e35e7-2dcd-4887-8622-2a58baa652b0");
        private static readonly Guid FrameServerC = new("941ce0a3-6ae3-4dda-9a08-a64298340617"), FrameServerD = new("9bec3fd1-d3fd-4816-8943-dbfeb5b43372");
        private static readonly Guid MFSampleExtension_Interlaced = new("b1d5830a-deb8-40e3-90fa-389943716461"), FrameServerE = new("c4139297-2cec-47c6-9cdf-6db62ee6df72");
        private static readonly Guid FrameServerDelivered = new("d06f8a46-60a0-4c20-bc33-ae96bf4fb070");
        public bool Created, Active;

        private static Vtable Build()
        {
            var vt = new Vtable("IMFMediaStream", 10, MS.IID_IMFMediaStream, MS.IID_IMFMediaEventGenerator);
            vt.Set(3, (delegate* unmanaged<IntPtr, uint, IntPtr*, int>)&GetEvent);
            vt.Set(4, (delegate* unmanaged<IntPtr, IntPtr, IntPtr, int>)&BeginGetEvent);
            vt.Set(5, (delegate* unmanaged<IntPtr, IntPtr, IntPtr*, int>)&EndGetEvent);
            vt.Set(6, (delegate* unmanaged<IntPtr, uint, Guid*, int, byte*, int>)&QueueEvent);
            vt.Set(7, (delegate* unmanaged<IntPtr, IntPtr*, int>)&GetMediaSource);
            vt.Set(8, (delegate* unmanaged<IntPtr, IntPtr*, int>)&GetStreamDescriptor);
            vt.Set(9, (delegate* unmanaged<IntPtr, IntPtr, int>)&RequestSample);
            return vt;
        }

        public MFCaptureStream(MFCaptureSource source, MFStreamDescriptor sd) : base(s_vt)
        {
            _source = source;
            sd.AddRef();
            _sd = sd;
        }

        protected override void OnFinalRelease()
        {
            Shut();
            _sd.Release();
        }

        internal void Shut()
        {
            if (_shut) return;
            _shut = true;
            _queue.Shutdown();
            Drop();
        }

        private void Drop()
        {
            while (_requests.Count > 0) Com.Release(_requests.Dequeue());
            _frames.Clear();
        }

        internal void Queue(uint type, MFValue? value = null) => _queue.QueueEvent(type, Guid.Empty, MF.S_OK, value);

        /// <summary>The camera opened at a format: its frames' durations and description.</summary>
        internal void Opened(CaptureFormat f) { _format = f; _frames.Clear(); }

        internal void Tick(long time) => Queue(MS.MEStreamTick, new MFValue { Type = 20, U = (ulong)time });

        internal void Stopped()
        {
            Queue(MS.MEStreamStopped);
            Drop();
            _discontinuity = true;
        }

        /// <summary>Started again: the frames kept go to the requests held.</summary>
        internal void Resume() { while (_requests.Count > 0 && _frames.Count > 0) Answer(); }

        /// <summary>A frame of the camera (the source's lock held): to a request waiting, else kept.</summary>
        internal void Frame(byte[] bytes, long time)
        {
            if (!Active || _shut) return;
            _frames.Enqueue((bytes, time));
            while (_frames.Count > Kept) { _frames.Dequeue(); _discontinuity = true; }       // (a frame dropped: the next one after a gap)
            if (_source.Started) Resume();
        }

        private void Answer()
        {
            IntPtr token = _requests.Dequeue();
            var (bytes, time) = _frames.Dequeue();
            var s = new MFSample();
            var buf = new MFMemoryBuffer((uint)bytes.Length);
            byte* d = buf.LockBytes(true);
            bytes.CopyTo(new Span<byte>(d, bytes.Length));
            buf.UnlockBytes();
            buf.CurrentLength = (uint)bytes.Length;
            s.Buffers.Add(buf.Pointer);
            s.Time = time;
            var a = s.Attributes;
            if (_format is { } f)
            {
                var (n, dn) = CaptureDevices.Rate(f.MaxFps);
                s.Duration = 10_000_000L * dn / n;
                // the frame server's description of the frame: 0, 2, 0, the subtype's FourCC, the subtype, the size
                var desc = new byte[40];
                BitConverter.TryWriteBytes(desc.AsSpan(4), 2u);
                f.Subtype.ToByteArray().AsSpan(0, 4).CopyTo(desc.AsSpan(12));
                f.Subtype.TryWriteBytes(desc.AsSpan(16));
                BitConverter.TryWriteBytes(desc.AsSpan(32), (uint)f.Width);
                BitConverter.TryWriteBytes(desc.AsSpan(36), (uint)f.Height);
                a.Set(FrameServerFormat, MFValue.Of(desc));
            }
            a.Set(FrameServerA, 0u);
            a.Set(FrameServerB, 0u);
            a.Set(MFSampleExtension_DeviceTimestamp, (ulong)time);
            a.Set(FrameServerC, 1u);
            var d24 = new byte[24]; d24[16] = 3;
            a.Set(FrameServerD, MFValue.Of(d24));
            a.Set(MF.MFSampleExtension_CleanPoint, 1u);
            a.Set(MF.MFSampleExtension_Discontinuity, _discontinuity ? 1u : 0u);
            _discontinuity = false;
            a.Set(MFSampleExtension_Interlaced, 0u);
            a.Set(FrameServerE, 10u);
            a.Set(FrameServerDelivered, (ulong)CaptureDevices.Now());
            a.Set(MS.MFSampleExtension_Token, new MFValue { Type = MF.VT_UNKNOWN, Unknown = token });    // the request's reference, the sample's now (empty of a request without one)
            Queue(MS.MEMediaSample, new MFValue { Type = MF.VT_UNKNOWN, Unknown = s.Pointer });
        }

        private static MFCaptureStream? M(IntPtr self) => From<MFCaptureStream>(self);

        [UnmanagedCallersOnly]
        private static int GetEvent(IntPtr self, uint flags, IntPtr* e)
        {
            if (M(self) is not { } m || e == null) return MF.E_POINTER;
            m._source.Pump();
            int hr = m._queue.GetEvent((flags & 1) != 0, out var ev);
            *e = ev;
            return hr;
        }

        [UnmanagedCallersOnly] private static int BeginGetEvent(IntPtr self, IntPtr callback, IntPtr state) => M(self)?._queue.BeginGetEvent(callback, state) ?? MF.E_POINTER;

        [UnmanagedCallersOnly]
        private static int EndGetEvent(IntPtr self, IntPtr result, IntPtr* e)
        {
            if (M(self) is not { } m || e == null) return MF.E_POINTER;
            int hr = m._queue.EndGetEvent(result, out var ev);
            *e = ev;
            return hr;
        }

        [UnmanagedCallersOnly]
        private static int QueueEvent(IntPtr self, uint type, Guid* ext, int status, byte* value)
            => M(self)?._queue.QueueEvent(type, ext == null ? Guid.Empty : *ext, status, MFMediaEvent.ValueOf(value)) ?? MF.E_POINTER;

        [UnmanagedCallersOnly]
        private static int GetMediaSource(IntPtr self, IntPtr* source)
        {
            if (M(self) is not { } m || source == null) return MF.E_POINTER;
            *source = IntPtr.Zero;
            if (m._source.IsShutdown) return MF.MF_E_SHUTDOWN;
            *source = m._source.AddRefPointer();
            return MF.S_OK;
        }

        [UnmanagedCallersOnly]
        private static int GetStreamDescriptor(IntPtr self, IntPtr* sd)
        {
            if (M(self) is not { } m || sd == null) return MF.E_POINTER;
            *sd = IntPtr.Zero;
            if (m._source.IsShutdown) return MF.MF_E_SHUTDOWN;
            *sd = m._sd.AddRefPointer();
            return MF.S_OK;
        }

        [UnmanagedCallersOnly]
        private static int RequestSample(IntPtr self, IntPtr token)
        {
            if (M(self) is not { } m) return MF.E_POINTER;
            try
            {
                m._source.Pump();
                lock (m._source.Lock)
                {
                    int state = m._source.RequestState;
                    if (state == MF.MF_E_SHUTDOWN) return state;
                    if (!m.Active) return MF.MF_E_SHUTDOWN;
                    if (state < 0) return state;
                    Com.AddRef(token);
                    m._requests.Enqueue(token);
                    if (state == 0) m.Resume();
                }
                return MF.S_OK;
            }
            catch (Exception e) { D3DLog.Error("IMFMediaStream.RequestSample (capture)", e); return MF.E_FAIL; }
        }
    }
}
