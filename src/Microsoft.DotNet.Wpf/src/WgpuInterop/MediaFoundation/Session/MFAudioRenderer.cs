// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// The streaming audio renderer (MFCreateAudioRendererActivate; Documentation/media-foundation-shim.md, "The media
// session"), as Windows' behaves (MFMediaSessionProbe's "renderer of MFMediaType_Audio"): a media sink of one stream
// whose handler takes the device's shared mix format (32-bit float, 48 kHz, stereo: the loader puts the resampler
// before it), its node of the renderer's CLSID; as the source starts, the session's MEExtendedType of the device's
// endpoint; its samples played on the system's output (AudioOutput.cs) a tenth of a second ahead of their times, at the
// volume the application sets (IMFGetService: MR_POLICY_VOLUME_SERVICE's IMFSimpleAudioVolume,
// MR_STREAM_VOLUME_SERVICE's IMFAudioStreamVolume).
//

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Microsoft.Wpf.Interop.WebGpu.Direct3D;
using Microsoft.Wpf.Interop.WebGpu.MediaFoundation.Sink;

namespace Microsoft.Wpf.Interop.WebGpu.MediaFoundation.Session
{
    /// <summary>A sink of the shim's that is a system renderer: its CLSID, set on its output node by the loader.</summary>
    internal interface IRendererClsid { Guid Clsid { get; } }

    internal sealed unsafe class MFAudioRenderer : ComObject, IStreamOwner, IRendererClsid, IRendererEvents
    {
        public static readonly Guid RendererClsid = new("d23e6476-b104-4707-81cb-e1ca19a07016");
        public const int MF_E_NO_AUDIO_PLAYBACK_DEVICE = unchecked((int)0xC00D36FA);
        private static readonly Guid IID_IMFGetService = new("fa993888-4383-415a-a930-dd472a8cf6f7"), IID_IMFSimpleAudioVolume = new("089edf13-cf71-4338-8d13-9e569dbdc319");
        private static readonly Guid IID_IMFAudioStreamVolume = new("76b1bbdb-4ec8-4f36-b106-70a9316df593"), MR_POLICY_VOLUME_SERVICE = new("1abaa2ac-9d3b-47c6-ab48-c59506de784d");
        private static readonly Guid MR_STREAM_VOLUME_SERVICE = new("f8b5fa2f-32ef-46f5-b172-1321212fb2c4");
        private static readonly Guid IID_IUnknown = new("00000000-0000-0000-c000-000000000046");
        private static readonly Guid MF_EVENT_ENDPOINT = new("02e7187d-0087-437e-a27f-cf5adccd3112");
        public const int Rate = 48000, Channels = 2;
        private static readonly Vtable s_vt = Build(), s_clockVt = BuildClock(), s_serviceVt = BuildService(), s_simpleVt = BuildSimple(), s_streamVt = BuildStreamVolume();

        private readonly AudioOutput _output;
        internal readonly MFGrabberStream Stream;
        private MFPresentationClock? _clock;
        private IntPtr _foreignClock;
        private bool _shutdown;
        private float _master = 1;
        private bool _mute;
        private readonly float[] _channel = { 1, 1 };
        private readonly object _lock = new();

        public Guid Clsid => RendererClsid;
        public bool IgnoreClock => false;
        public long Lead => 1_000_000;
        public ComObject Sink => this;

        private static Vtable Build()
        {
            var vt = new Vtable("IMFMediaSink", 12, SinkIds.IID_IMFMediaSink);
            vt.Set(3, (delegate* unmanaged<IntPtr, uint*, int>)&GetCharacteristics);
            vt.Set(4, (delegate* unmanaged<IntPtr, uint, IntPtr, IntPtr*, int>)&AddStreamSink);
            vt.Set(5, (delegate* unmanaged<IntPtr, uint, int>)&RemoveStreamSink);
            vt.Set(6, (delegate* unmanaged<IntPtr, uint*, int>)&GetStreamSinkCount);
            vt.Set(7, (delegate* unmanaged<IntPtr, uint, IntPtr*, int>)&GetStreamSinkByIndex);
            vt.Set(8, (delegate* unmanaged<IntPtr, uint, IntPtr*, int>)&GetStreamSinkById);
            vt.Set(9, (delegate* unmanaged<IntPtr, IntPtr, int>)&SetPresentationClock);
            vt.Set(10, (delegate* unmanaged<IntPtr, IntPtr*, int>)&GetPresentationClock);
            vt.Set(11, (delegate* unmanaged<IntPtr, int>)&ShutdownSlot);
            return vt;
        }

        private static Vtable BuildClock()
        {
            var vt = new Vtable("IMFClockStateSink", 8, MFPresentationClock.IID_IMFClockStateSink);
            vt.Set(3, (delegate* unmanaged<IntPtr, long, long, int>)&OnClockStart);
            vt.Set(4, (delegate* unmanaged<IntPtr, long, int>)&OnClockStop);
            vt.Set(5, (delegate* unmanaged<IntPtr, long, int>)&OnClockPause);
            vt.Set(6, (delegate* unmanaged<IntPtr, long, int>)&OnClockRestart);
            vt.Set(7, (delegate* unmanaged<IntPtr, long, float, int>)&OnClockSetRate);
            return vt;
        }

        private MFAudioRenderer(AudioOutput output) : base(s_vt, s_clockVt, s_serviceVt, s_simpleVt, s_streamVt)
        {
            _output = output;
            Stream = new MFGrabberStream(this, new MixFormatHandler());
        }

        /// <summary>The renderer of the system's output; MF_E_NO_AUDIO_PLAYBACK_DEVICE where it has none.</summary>
        public static int Create(out MFAudioRenderer? r)
        {
            r = null;
            var o = AudioOutput.Open(Rate, Channels);
            if (o == null) return MF_E_NO_AUDIO_PLAYBACK_DEVICE;
            r = new MFAudioRenderer(o);
            return MF.S_OK;
        }

        protected override void OnFinalRelease() { Shutdown(); Stream.Release(); }

        public void Shutdown()
        {
            lock (_lock) { if (_shutdown) return; _shutdown = true; }
            Stream.Shut();
            Detach();
            _output.Dispose();
        }

        private void Detach()
        {
            IntPtr me = PointerFor(s_clockVt);
            if (_clock != null) { _clock.RemoveSink(me); _clock.Release(); _clock = null; }
            if (_foreignClock != IntPtr.Zero) { ((delegate* unmanaged<IntPtr, IntPtr, int>)Com.Fn(_foreignClock, 12))(_foreignClock, me); Com.Release(_foreignClock); _foreignClock = IntPtr.Zero; }
        }

        public long? Now
        {
            get
            {
                if (_clock != null) return _clock.Time;
                if (_foreignClock == IntPtr.Zero) return null;
                long t;
                return ((delegate* unmanaged<IntPtr, long*, int>)Com.Fn(_foreignClock, 10))(_foreignClock, &t) >= 0 ? t : null;
            }
        }

        /// <summary>The sample's float frames, at the volume set, to the output.</summary>
        public void Deliver(IntPtr sample, Guid major, long time, long duration)
        {
            byte[] bytes = MFSample.ForeignSampleBytes(sample);
            var f = MemoryMarshal.Cast<byte, float>(bytes.AsSpan(0, bytes.Length & ~3)).ToArray();
            float m, l, r;
            lock (_lock) { m = _mute ? 0 : _master; l = _channel[0] * m; r = _channel[1] * m; }
            if (l != 1 || r != 1) for (int i = 0; i + 1 < f.Length; i += 2) { f[i] *= l; f[i + 1] *= r; }
            _output.Write(f);
        }

        public IEnumerable<(Guid Extended, MFValue? Value)> StartEvents()
        {
            yield return (MF_EVENT_ENDPOINT, MFValue.Of("{0.0.0.00000000}.{" + EndpointId + "}"));
        }

        /// <summary>The output's endpoint identifier: the process's one (the system's default output).</summary>
        private static readonly Guid EndpointId = Guid.NewGuid();

        private static MFAudioRenderer? S(IntPtr self) => From<MFAudioRenderer>(self);

        [UnmanagedCallersOnly] private static int GetCharacteristics(IntPtr self, uint* c) { if (S(self) is not { } s || c == null) return MF.E_POINTER; if (s._shutdown) return MF.MF_E_SHUTDOWN; *c = SinkIds.MEDIASINK_FIXED_STREAMS; return MF.S_OK; }
        [UnmanagedCallersOnly] private static int AddStreamSink(IntPtr self, uint id, IntPtr type, IntPtr* stream) { if (stream != null) *stream = IntPtr.Zero; return unchecked((int)0xC00D4A3A); }
        [UnmanagedCallersOnly] private static int RemoveStreamSink(IntPtr self, uint id) => unchecked((int)0xC00D4A3A);
        [UnmanagedCallersOnly] private static int GetStreamSinkCount(IntPtr self, uint* n) { if (S(self) is not { } s || n == null) return MF.E_POINTER; if (s._shutdown) return MF.MF_E_SHUTDOWN; *n = 1; return MF.S_OK; }

        [UnmanagedCallersOnly]
        private static int GetStreamSinkByIndex(IntPtr self, uint i, IntPtr* stream)
        {
            if (S(self) is not { } s || stream == null) return MF.E_POINTER;
            *stream = IntPtr.Zero;
            if (s._shutdown) return MF.MF_E_SHUTDOWN;
            if (i != 0) return MF.E_INVALIDARG;
            *stream = s.Stream.AddRefPointer();
            return MF.S_OK;
        }

        [UnmanagedCallersOnly]
        private static int GetStreamSinkById(IntPtr self, uint id, IntPtr* stream)
        {
            if (S(self) is not { } s || stream == null) return MF.E_POINTER;
            *stream = IntPtr.Zero;
            if (s._shutdown) return MF.MF_E_SHUTDOWN;
            if (id != 0) return unchecked((int)0xC00D4A3B);
            *stream = s.Stream.AddRefPointer();
            return MF.S_OK;
        }

        [UnmanagedCallersOnly]
        private static int SetPresentationClock(IntPtr self, IntPtr clock)
        {
            if (S(self) is not { } s) return MF.E_POINTER;
            if (s._shutdown) return MF.MF_E_SHUTDOWN;
            s.Detach();
            IntPtr me = s.PointerFor(s_clockVt);
            if (From<MFPresentationClock>(clock) is { } ours) { ours.AddRef(); s._clock = ours; ours.AddSink(me); }
            else if (clock != IntPtr.Zero) { Com.AddRef(clock); s._foreignClock = clock; ((delegate* unmanaged<IntPtr, IntPtr, int>)Com.Fn(clock, 11))(clock, me); }
            return MF.S_OK;
        }

        [UnmanagedCallersOnly]
        private static int GetPresentationClock(IntPtr self, IntPtr* clock)
        {
            if (S(self) is not { } s || clock == null) return MF.E_POINTER;
            *clock = IntPtr.Zero;
            IntPtr c = s._clock?.Pointer ?? s._foreignClock;
            if (c == IntPtr.Zero) return SinkIds.MF_E_NO_CLOCK;
            Com.AddRef(c); *clock = c;
            return MF.S_OK;
        }

        [UnmanagedCallersOnly] private static int ShutdownSlot(IntPtr self) { if (S(self) is not { } s) return MF.E_POINTER; if (s._shutdown) return MF.MF_E_SHUTDOWN; s.Shutdown(); return MF.S_OK; }

        // ---- the clock: the stream and the output started, stopped, paused ----

        [UnmanagedCallersOnly]
        private static int OnClockStart(IntPtr self, long system, long offset)
        {
            if (S(self) is not { } s) return MF.E_POINTER;
            if (offset != MFPresentationClock.PRESENTATION_CURRENT_POSITION) s._output.Flush();
            s.Stream.ClockStarted(offset);
            s._output.Start();
            return MF.S_OK;
        }

        [UnmanagedCallersOnly] private static int OnClockStop(IntPtr self, long system) { if (S(self) is not { } s) return MF.E_POINTER; s._output.Pause(); s._output.Flush(); s.Stream.ClockStopped(); return MF.S_OK; }
        [UnmanagedCallersOnly] private static int OnClockPause(IntPtr self, long system) { if (S(self) is not { } s) return MF.E_POINTER; s._output.Pause(); s.Stream.ClockPaused(); return MF.S_OK; }
        [UnmanagedCallersOnly] private static int OnClockRestart(IntPtr self, long system) { if (S(self) is not { } s) return MF.E_POINTER; s.Stream.ClockRestarted(); s._output.Start(); return MF.S_OK; }
        [UnmanagedCallersOnly] private static int OnClockSetRate(IntPtr self, long system, float rate) => MF.S_OK;

        // ---- IMFGetService: the volumes ----

        private static Vtable BuildService()
        {
            var vt = new Vtable("IMFGetService", 4, IID_IMFGetService);
            vt.Set(3, (delegate* unmanaged<IntPtr, Guid*, Guid*, IntPtr*, int>)&GetService);
            return vt;
        }

        [UnmanagedCallersOnly]
        private static int GetService(IntPtr self, Guid* service, Guid* iid, IntPtr* ppv)
        {
            if (S(self) is not { } s || service == null || iid == null || ppv == null) return MF.E_POINTER;
            *ppv = IntPtr.Zero;
            if (*service == MR_POLICY_VOLUME_SERVICE && (*iid == IID_IMFSimpleAudioVolume || *iid == IID_IUnknown)) { *ppv = s.AddRefPointer(s_simpleVt); return MF.S_OK; }
            if (*service == MR_STREAM_VOLUME_SERVICE && (*iid == IID_IMFAudioStreamVolume || *iid == IID_IUnknown)) { *ppv = s.AddRefPointer(s_streamVt); return MF.S_OK; }
            if (*service == MR_POLICY_VOLUME_SERVICE || *service == MR_STREAM_VOLUME_SERVICE) return MF.E_NOINTERFACE;
            return unchecked((int)0xC00D36BA);                  // MF_E_UNSUPPORTED_SERVICE
        }

        private static Vtable BuildSimple()
        {
            var vt = new Vtable("IMFSimpleAudioVolume", 7, IID_IMFSimpleAudioVolume);
            vt.Set(3, (delegate* unmanaged<IntPtr, float, int>)&SetMasterVolume);
            vt.Set(4, (delegate* unmanaged<IntPtr, float*, int>)&GetMasterVolume);
            vt.Set(5, (delegate* unmanaged<IntPtr, int, int>)&SetMute);
            vt.Set(6, (delegate* unmanaged<IntPtr, int*, int>)&GetMute);
            return vt;
        }

        [UnmanagedCallersOnly] private static int SetMasterVolume(IntPtr self, float v) { if (S(self) is not { } s) return MF.E_POINTER; if (v < 0 || v > 1) return MF.E_INVALIDARG; lock (s._lock) s._master = v; return MF.S_OK; }
        [UnmanagedCallersOnly] private static int GetMasterVolume(IntPtr self, float* v) { if (S(self) is not { } s || v == null) return MF.E_POINTER; lock (s._lock) *v = s._master; return MF.S_OK; }
        [UnmanagedCallersOnly] private static int SetMute(IntPtr self, int m) { if (S(self) is not { } s) return MF.E_POINTER; lock (s._lock) s._mute = m != 0; return MF.S_OK; }
        [UnmanagedCallersOnly] private static int GetMute(IntPtr self, int* m) { if (S(self) is not { } s || m == null) return MF.E_POINTER; lock (s._lock) *m = s._mute ? 1 : 0; return MF.S_OK; }

        private static Vtable BuildStreamVolume()
        {
            var vt = new Vtable("IMFAudioStreamVolume", 8, IID_IMFAudioStreamVolume);
            vt.Set(3, (delegate* unmanaged<IntPtr, uint*, int>)&GetChannelCount);
            vt.Set(4, (delegate* unmanaged<IntPtr, uint, float, int>)&SetChannelVolume);
            vt.Set(5, (delegate* unmanaged<IntPtr, uint, float*, int>)&GetChannelVolume);
            vt.Set(6, (delegate* unmanaged<IntPtr, uint, float*, int>)&SetAllVolumes);
            vt.Set(7, (delegate* unmanaged<IntPtr, uint, float*, int>)&GetAllVolumes);
            return vt;
        }

        [UnmanagedCallersOnly] private static int GetChannelCount(IntPtr self, uint* n) { if (n == null) return MF.E_POINTER; *n = Channels; return MF.S_OK; }
        [UnmanagedCallersOnly] private static int SetChannelVolume(IntPtr self, uint i, float v) { if (S(self) is not { } s) return MF.E_POINTER; if (i >= Channels || v < 0 || v > 1) return MF.E_INVALIDARG; lock (s._lock) s._channel[i] = v; return MF.S_OK; }
        [UnmanagedCallersOnly] private static int GetChannelVolume(IntPtr self, uint i, float* v) { if (S(self) is not { } s || v == null) return MF.E_POINTER; if (i >= Channels) return MF.E_INVALIDARG; lock (s._lock) *v = s._channel[i]; return MF.S_OK; }

        [UnmanagedCallersOnly]
        private static int SetAllVolumes(IntPtr self, uint n, float* v)
        {
            if (S(self) is not { } s || v == null) return MF.E_POINTER;
            if (n != Channels) return MF.E_INVALIDARG;
            for (int i = 0; i < n; i++) if (v[i] < 0 || v[i] > 1) return MF.E_INVALIDARG;
            lock (s._lock) for (int i = 0; i < n; i++) s._channel[i] = v[i];
            return MF.S_OK;
        }

        [UnmanagedCallersOnly]
        private static int GetAllVolumes(IntPtr self, uint n, float* v)
        {
            if (S(self) is not { } s || v == null) return MF.E_POINTER;
            if (n != Channels) return MF.E_INVALIDARG;
            lock (s._lock) for (int i = 0; i < n; i++) v[i] = s._channel[i];
            return MF.S_OK;
        }
    }

    /// <summary>The audio renderer's handler: the mix format (float, 48 kHz, stereo) its one type, and the only one it takes.</summary>
    internal sealed unsafe class MixFormatHandler : ComObject
    {
        private static readonly Vtable s_vt = Build();
        private readonly MFMediaType _mix = MFResampler.TypeOf(true, 32, MFAudioRenderer.Channels, MFAudioRenderer.Rate);
        private MFMediaType? _current;
        private static readonly Guid MF_MT_AUDIO_NUM_CHANNELS = new("37e48bf5-645e-4c5b-89de-ada9e29b696a"), MF_MT_AUDIO_SAMPLES_PER_SECOND = new("5faeeae7-0290-4c31-9e8a-c534f68d9dba");
        private static readonly Guid MF_MT_AUDIO_BITS_PER_SAMPLE = new("f2deb57f-40fa-4764-aa33-ed4f2d1ff669");

        private static Vtable Build()
        {
            var vt = new Vtable("IMFMediaTypeHandler", 9, new Guid("e93dcf6c-4b07-4e1e-8123-aa16ed6eadf5"));
            vt.Set(3, (delegate* unmanaged<IntPtr, IntPtr, IntPtr*, int>)&IsMediaTypeSupported);
            vt.Set(4, (delegate* unmanaged<IntPtr, uint*, int>)&GetMediaTypeCount);
            vt.Set(5, (delegate* unmanaged<IntPtr, uint, IntPtr*, int>)&GetMediaTypeByIndex);
            vt.Set(6, (delegate* unmanaged<IntPtr, IntPtr, int>)&SetCurrentMediaType);
            vt.Set(7, (delegate* unmanaged<IntPtr, IntPtr*, int>)&GetCurrentMediaType);
            vt.Set(8, (delegate* unmanaged<IntPtr, Guid*, int>)&GetMajorType);
            return vt;
        }

        public MixFormatHandler() : base(s_vt) { }

        protected override void OnFinalRelease() { _mix.Release(); _current?.Release(); }

        private static bool Takes(IntPtr type)
        {
            var t = Source.Mp4Types.Read(type);
            try
            {
                var a = t.Attributes;
                return a.Guid(MF.MF_MT_MAJOR_TYPE) == new Guid("73647561-0000-0010-8000-00aa00389b71") && t.Subtype == Codecs.MediaFormats.Float
                    && a.U32(MF_MT_AUDIO_NUM_CHANNELS) == MFAudioRenderer.Channels && a.U32(MF_MT_AUDIO_SAMPLES_PER_SECOND) == MFAudioRenderer.Rate && a.U32(MF_MT_AUDIO_BITS_PER_SAMPLE, 32) == 32;
            }
            finally { t.Release(); }
        }

        private static MixFormatHandler? H(IntPtr self) => From<MixFormatHandler>(self);

        [UnmanagedCallersOnly]
        private static int IsMediaTypeSupported(IntPtr self, IntPtr type, IntPtr* closest)
        {
            if (H(self) is not { } h || type == IntPtr.Zero) return MF.E_POINTER;
            if (closest != null) *closest = IntPtr.Zero;
            return Takes(type) ? MF.S_OK : MF.MF_E_INVALIDMEDIATYPE;
        }

        [UnmanagedCallersOnly] private static int GetMediaTypeCount(IntPtr self, uint* n) { if (n == null) return MF.E_POINTER; *n = 1; return MF.S_OK; }

        [UnmanagedCallersOnly]
        private static int GetMediaTypeByIndex(IntPtr self, uint i, IntPtr* type)
        {
            if (H(self) is not { } h || type == null) return MF.E_POINTER;
            *type = IntPtr.Zero;
            if (i != 0) return MF.MF_E_NO_MORE_TYPES;
            *type = h._mix.Clone().Pointer;
            return MF.S_OK;
        }

        [UnmanagedCallersOnly]
        private static int SetCurrentMediaType(IntPtr self, IntPtr type)
        {
            if (H(self) is not { } h || type == IntPtr.Zero) return MF.E_POINTER;
            if (!Takes(type)) return MF.MF_E_INVALIDMEDIATYPE;
            h._current?.Release();
            h._current = Source.Mp4Types.Read(type);
            return MF.S_OK;
        }

        [UnmanagedCallersOnly]
        private static int GetCurrentMediaType(IntPtr self, IntPtr* type)
        {
            if (H(self) is not { } h || type == null) return MF.E_POINTER;
            *type = IntPtr.Zero;
            if (h._current == null) return MFE.MF_E_NOT_INITIALIZED;
            *type = h._current.AddRefPointer();
            return MF.S_OK;
        }

        [UnmanagedCallersOnly] private static int GetMajorType(IntPtr self, Guid* major) { if (major == null) return MF.E_POINTER; *major = new Guid("73647561-0000-0010-8000-00aa00389b71"); return MF.S_OK; }
    }
}
