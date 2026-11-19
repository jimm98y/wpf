// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// A source reader of a media source of the application's (MFCreateSourceReaderFromMediaSource;
// Documentation/media-foundation-shim.md, "Media sources"), driven as Windows' reader drives one: its
// presentation descriptor's streams in order, selected as it selects them, each of its type handler's current
// type; Start (GUID_NULL, VT_EMPTY) at the first read, the source's events taken to MESourceStarted with the
// streams of MENewStream/MEUpdatedStream; a sample a RequestSample and its MEMediaSample, the end MEEndOfStream;
// a seek Start at the position; a change of selection Start again where it is; Shutdown with the reader unless
// MF_SOURCE_READER_DISCONNECT_MEDIASOURCE_ON_SHUTDOWN. A stream's MEStreamTick is handed out before the sample still
// asked for, its MEStreamFormatChanged with the sample after it (ReaderStream: MF_SOURCE_READER_FLAG's flags).
//

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using Microsoft.Wpf.Interop.WebGpu.Direct3D;

namespace Microsoft.Wpf.Interop.WebGpu.MediaFoundation.Source
{
    internal sealed unsafe class ForeignSource : ReaderSource
    {
        private const int Wait = 30_000;           // ms for an event of the source's before the reader gives up
        private readonly IntPtr _src, _pd;          // IMFMediaSource, IMFPresentationDescriptor: AddRef'd
        private readonly bool _disconnect;
        private readonly List<(ForeignFeed Feed, MFMediaType Native, bool Selected)> _streams = new();
        private readonly List<IntPtr> _handlers = new();     // IMFMediaTypeHandler of each stream, AddRef'd
        private bool _started, _restart;
        private long? _startAt;

        private ForeignSource(IntPtr src, IntPtr pd, bool disconnect) { _src = src; _pd = pd; _disconnect = disconnect; }

        public static int Create(IntPtr source, bool disconnect, out ForeignSource? fs)
        {
            fs = null;
            int hr = Com.QueryInterface(source, MS.IID_IMFMediaSource, out var src);
            if (hr < 0) return hr;
            IntPtr pd;
            hr = ((delegate* unmanaged<IntPtr, IntPtr*, int>)Com.Fn(src, 8))(src, &pd);
            if (hr < 0) { Com.Release(src); return hr; }
            var s = new ForeignSource(src, pd, disconnect);
            uint n;
            ((delegate* unmanaged<IntPtr, uint*, int>)Com.Fn(pd, 33))(pd, &n);
            for (uint i = 0; i < n; i++)
            {
                int sel; IntPtr sd;
                if (((delegate* unmanaged<IntPtr, uint, int*, IntPtr*, int>)Com.Fn(pd, 34))(pd, i, &sel, &sd) < 0) continue;
                try
                {
                    uint id;
                    ((delegate* unmanaged<IntPtr, uint*, int>)Com.Fn(sd, 33))(sd, &id);
                    IntPtr h;
                    if (((delegate* unmanaged<IntPtr, IntPtr*, int>)Com.Fn(sd, 34))(sd, &h) < 0) continue;
                    IntPtr t;
                    // the handler's current type, or its first
                    if (((delegate* unmanaged<IntPtr, IntPtr*, int>)Com.Fn(h, 7))(h, &t) < 0 && ((delegate* unmanaged<IntPtr, uint, IntPtr*, int>)Com.Fn(h, 5))(h, 0, &t) < 0) { Com.Release(h); continue; }
                    s._handlers.Add(h);
                    var native = Mp4Types.Read(t);
                    Com.Release(t);
                    var major = native.Attributes.Guid(MF.MF_MT_MAJOR_TYPE);
                    s._streams.Add((new ForeignFeed(s, id, major == SR.MFMediaType_Audio, major == MF.MFMediaType_Video), native, sel != 0));
                }
                finally { Com.Release(sd); }
            }
            fs = s;
            return MF.S_OK;
        }

        public override IEnumerable<(SampleFeed, MFMediaType, bool)> Streams()
        {
            foreach (var (f, t, sel) in _streams) yield return (f, t, sel);
        }

        public override uint Characteristics { get { uint c = 0; ((delegate* unmanaged<IntPtr, uint*, int>)Com.Fn(_src, 7))(_src, &c); return c; } }

        public override MFValue? Presentation(Guid key)
        {
            var pv = stackalloc byte[MF.PropVariantSize];
            new Span<byte>(pv, MF.PropVariantSize).Clear();
            if (((delegate* unmanaged<IntPtr, Guid*, byte*, int>)Com.Fn(_pd, 3))(_pd, &key, pv) < 0) return null;            // IMFAttributes::GetItem
            var v = MFAttributeStore.Read(pv);
            Ole.PropVariantClear(pv);
            return v;
        }

        public override IntPtr MediaSource => _src;

        /// <summary>The stream's handler's types, by index, as Windows' reader lists them (of a handler of more than one).</summary>
        public override MFMediaType? NativeType(int index, uint n)
        {
            if (index >= _handlers.Count) return null;
            IntPtr h = _handlers[index], t;
            uint count = 0;
            ((delegate* unmanaged<IntPtr, uint*, int>)Com.Fn(h, 4))(h, &count);
            if (count <= 1) return null;                    // (the one type: the stream's native, as of a file's)
            if (((delegate* unmanaged<IntPtr, uint, IntPtr*, int>)Com.Fn(h, 5))(h, n, &t) < 0) return null;
            var c = Mp4Types.Read(t);
            Com.Release(t);
            return c;
        }

        /// <summary>The handler's current type to the one given, where it takes it (the source goes on of it: not started again).</summary>
        public override int SetNative(int index, IntPtr type, out MFMediaType? native)
        {
            native = null;
            if (index >= _handlers.Count) return MF.MF_E_INVALIDSTREAMNUMBER;
            IntPtr h = _handlers[index];
            uint count = 0;
            ((delegate* unmanaged<IntPtr, uint*, int>)Com.Fn(h, 4))(h, &count);
            if (count <= 1) return MF.E_NOTIMPL;          // (the one type: as of a file's)
            IntPtr closest = IntPtr.Zero;
            int hr = ((delegate* unmanaged<IntPtr, IntPtr, IntPtr*, int>)Com.Fn(h, 3))(h, type, &closest);
            Com.Release(closest);
            if (hr < 0) return MF.MF_E_INVALIDMEDIATYPE;
            hr = ((delegate* unmanaged<IntPtr, IntPtr, int>)Com.Fn(h, 6))(h, type);
            if (hr < 0) return hr;
            native = Mp4Types.Read(type);
            var (f, old, sel) = _streams[index];
            old.Release();
            native.AddRef();
            _streams[index] = (f, native, sel);
            return MF.S_OK;
        }

        public override void Select(int index, bool on)
        {
            ((delegate* unmanaged<IntPtr, uint, int>)Com.Fn(_pd, on ? 35 : 36))(_pd, (uint)index);
            if (_started) _restart = true;
        }

        public override int Seek(long hns)
        {
            foreach (var (f, _, _) in _streams) f.Drop();
            if (!_started) { _startAt = hns; return MF.S_OK; }
            return Start(hns);
        }

        /// <summary>The source started (again, after a change of selection), before a stream's first request.</summary>
        internal void EnsureStarted()
        {
            if (_started && !_restart) return;
            int hr = Start(_started ? null : _startAt);
            if (hr < 0) throw new InvalidOperationException($"the media source's Start: 0x{hr:x8}");
        }

        private int Start(long? at)
        {
            var pv = stackalloc byte[MF.PropVariantSize];
            new Span<byte>(pv, MF.PropVariantSize).Clear();
            if (at is long a) { *(ushort*)pv = 20; *(long*)(pv + 8) = a; }
            Guid fmt = Guid.Empty;
            int hr = ((delegate* unmanaged<IntPtr, IntPtr, Guid*, byte*, int>)Com.Fn(_src, 9))(_src, _pd, &fmt, pv);
            if (hr < 0) return hr;
            _started = true; _restart = false; _startAt = null;
            // its events, to MESourceStarted (MESourceSeeked): the streams of it
            while (true)
            {
                var e = Event(_src, out hr);
                if (e == null) return hr < 0 ? hr : MF.E_FAIL;
                var (type, status, unk) = e.Value;
                try
                {
                    if (status < 0) return status;
                    if (type is MS.MENewStream or MS.MEUpdatedStream && unk != IntPtr.Zero && Com.QueryInterface(unk, MS.IID_IMFMediaStream, out var ms) >= 0)
                    {
                        IntPtr sd; uint id = uint.MaxValue;
                        if (((delegate* unmanaged<IntPtr, IntPtr*, int>)Com.Fn(ms, 8))(ms, &sd) >= 0) { ((delegate* unmanaged<IntPtr, uint*, int>)Com.Fn(sd, 33))(sd, &id); Com.Release(sd); }
                        foreach (var (f, _, _) in _streams) if (f.Id == id) { f.Attach(ms); break; }
                        Com.Release(ms);
                    }
                    else if (type is MS.MESourceStarted or MS.MESourceSeeked) return MF.S_OK;
                    else if (type == MS.MEError) return MF.E_FAIL;
                }
                finally { Com.Release(unk); }
            }
        }

        /// <summary>The next event of an event generator (type, status, the IUnknown of its value AddRef'd), or null when none came in time.</summary>
        internal static (uint Type, int Status, IntPtr Unknown)? Event(IntPtr gen, out int hr) => Event(gen, out hr, out _);

        /// <summary>The next event, and its value's VT_I8 (a stream tick's time) where it is one.</summary>
        internal static (uint Type, int Status, IntPtr Unknown)? Event(IntPtr gen, out int hr, out long i8)
        {
            var sw = Stopwatch.StartNew();
            i8 = 0;
            while (true)
            {
                IntPtr e;
                hr = ((delegate* unmanaged<IntPtr, uint, IntPtr*, int>)Com.Fn(gen, 3))(gen, 1, &e);           // MF_EVENT_FLAG_NO_WAIT
                if (hr >= 0)
                {
                    uint type; int status;
                    ((delegate* unmanaged<IntPtr, uint*, int>)Com.Fn(e, 33))(e, &type);
                    ((delegate* unmanaged<IntPtr, int*, int>)Com.Fn(e, 35))(e, &status);
                    var pv = stackalloc byte[MF.PropVariantSize];
                    new Span<byte>(pv, MF.PropVariantSize).Clear();
                    ((delegate* unmanaged<IntPtr, byte*, int>)Com.Fn(e, 36))(e, pv);
                    IntPtr unk = *(ushort*)pv == MF.VT_UNKNOWN ? *(IntPtr*)(pv + 8) : IntPtr.Zero;
                    if (*(ushort*)pv == 20) i8 = *(long*)(pv + 8);                                          // VT_I8
                    if (unk == IntPtr.Zero) Ole.PropVariantClear(pv);
                    Com.Release(e);
                    return (type, status, unk);
                }
                if (hr != MFE.MF_E_NO_EVENTS_AVAILABLE || sw.ElapsedMilliseconds > Wait) return null;
                Thread.Sleep(1);
            }
        }

        public override void Dispose()
        {
            foreach (var (f, t, _) in _streams) { f.Dispose(); t.Release(); }
            foreach (var h in _handlers) Com.Release(h);
            if (!_disconnect) ((delegate* unmanaged<IntPtr, int>)Com.Fn(_src, 12))(_src);           // Shutdown
            Com.Release(_pd);
            Com.Release(_src);
        }
    }

    /// <summary>A stream of an application's media source: a sample a request.</summary>
    internal sealed unsafe class ForeignFeed : SampleFeed
    {
        private readonly ForeignSource _owner;
        private readonly bool _audio, _video;
        public readonly uint Id;
        private IntPtr _stream;                     // IMFMediaStream, AddRef'd: of the source's MENewStream
        private MFSample? _peek;
        private bool _ended;
        private long? _tick;                        // MEStreamTick's time, until the reader hands it out
        private bool _requested;                    // a RequestSample still standing (a tick came before its sample)
        private MFMediaType? _format, _formatOfPeek;   // MEStreamFormatChanged's type: before the next sample, of the sample peeked

        public ForeignFeed(ForeignSource owner, uint id, bool audio, bool video) { _owner = owner; Id = id; _audio = audio; _video = video; }

        public override bool Audio => _audio;
        public override bool Video => _video;

        internal void Attach(IntPtr stream)
        {
            if (stream == _stream) return;
            Com.AddRef(stream);
            Com.Release(_stream);
            _stream = stream;
        }

        internal void Drop() { _peek?.Release(); _peek = null; _ended = false; _tick = null; _requested = false; _taken?.Release(); _taken = null; _format?.Release(); _format = null; _formatOfPeek?.Release(); _formatOfPeek = null; }

        public override long NextTime { get { Fill(); return _tick ?? _peek?.Time ?? long.MaxValue; } }
        public override bool AtEnd { get { Fill(); return _peek == null && _tick == null; } }

        public override bool TakeTick(out long time)
        {
            Fill();
            time = _tick ?? 0;
            if (_tick == null) return false;
            _tick = null;
            return true;
        }

        private MFMediaType? _taken;
        public override MFMediaType? TakeFormat() { var t = _taken; _taken = null; return t; }

        public override MFSample? Take(bool first)
        {
            Fill();
            var s = _peek;
            _peek = null;
            _taken?.Release(); _taken = _formatOfPeek; _formatOfPeek = null;
            return s;
        }

        public override void Seek(long hns) => Drop();

        /// <summary>The next sample, asked for and waited for.</summary>
        private void Fill()
        {
            if (_peek != null || _ended || _tick != null) return;
            _owner.EnsureStarted();
            if (_stream == IntPtr.Zero) { _ended = true; return; }
            int hr;
            // (a request still standing, of a tick before its sample: not asked again)
            if (!_requested)
            {
                hr = ((delegate* unmanaged<IntPtr, IntPtr, int>)Com.Fn(_stream, 9))(_stream, IntPtr.Zero);       // RequestSample
                if (hr == MS.MF_E_END_OF_STREAM) { _ended = true; return; }
                if (hr < 0) throw new InvalidOperationException($"the media stream's RequestSample: 0x{hr:x8}");
            }
            _requested = false;
            while (true)
            {
                var e = ForeignSource.Event(_stream, out hr, out long i8);
                if (e == null) throw new InvalidOperationException($"the media stream sent no sample: 0x{hr:x8}");
                var (type, status, unk) = e.Value;
                try
                {
                    if (status < 0) throw new InvalidOperationException($"the media stream's event {type}: 0x{status:x8}");
                    switch (type)
                    {
                        case MS.MEMediaSample:
                            if (unk == IntPtr.Zero) continue;
                            _peek = Import(unk);
                            _formatOfPeek = _format; _format = null;
                            return;
                        case MS.MEEndOfStream:
                            _ended = true;
                            return;
                        case MS.MEStreamTick:
                            // a gap: handed out before the next sample (the request still stands)
                            _tick = i8;
                            _requested = true;
                            return;
                        case MS.MEStreamFormatChanged:
                            if (unk != IntPtr.Zero) { _format?.Release(); _format = Mp4Types.Read(unk); }
                            break;
                        case MS.MEError:
                            throw new InvalidOperationException("the media stream's MEError");
                    }
                }
                finally { Com.Release(unk); }
            }
        }

        /// <summary>A sample of the application's as one of the shim's, of one buffer: the same object where it is one, else a copy.</summary>
        private static MFSample Import(IntPtr p)
        {
            if (ComObject.From<MFSample>(p) is { } ours && ours.Buffers.Count <= 1) { ours.AddRef(); return ours; }
            if (Com.QueryInterface(p, MF.IID_IMFSample, out var smp) < 0) throw new InvalidOperationException("MEMediaSample of no IMFSample");
            try
            {
                var s = new MFSample();
                long t, d; uint flags, n;
                if (((delegate* unmanaged<IntPtr, long*, int>)Com.Fn(smp, 35))(smp, &t) >= 0) s.Time = t;
                if (((delegate* unmanaged<IntPtr, long*, int>)Com.Fn(smp, 37))(smp, &d) >= 0) s.Duration = d;
                if (((delegate* unmanaged<IntPtr, uint*, int>)Com.Fn(smp, 33))(smp, &flags) >= 0) s.Flags = flags;
                ((delegate* unmanaged<IntPtr, uint*, int>)Com.Fn(smp, 30))(smp, &n);
                var pv = stackalloc byte[MF.PropVariantSize];
                for (uint i = 0; i < n; i++)
                {
                    new Span<byte>(pv, MF.PropVariantSize).Clear();
                    Guid k;
                    if (((delegate* unmanaged<IntPtr, uint, Guid*, byte*, int>)Com.Fn(smp, 31))(smp, i, &k, pv) < 0) continue;
                    if (MFAttributeStore.Read(pv) is { } v) s.Attributes.Set(k, v);
                    Ole.PropVariantClear(pv);
                }
                var bytes = MFSample.ForeignSampleBytes(smp);
                var buf = new MFMemoryBuffer((uint)Math.Max(1, bytes.Length));
                byte* dst = buf.LockBytes(true);
                bytes.CopyTo(new Span<byte>(dst, bytes.Length));
                buf.UnlockBytes();
                buf.CurrentLength = (uint)bytes.Length;
                s.Buffers.Add(buf.Pointer);
                return s;
            }
            finally { Com.Release(smp); }
        }

        public override void Dispose()
        {
            Drop();
            Com.Release(_stream);
            _stream = IntPtr.Zero;
        }
    }
}
