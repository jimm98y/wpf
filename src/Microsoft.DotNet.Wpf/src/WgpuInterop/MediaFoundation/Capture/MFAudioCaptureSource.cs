// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// The audio capture devices of MFEnumDeviceSources (Documentation/media-foundation-shim.md, "Capture devices"): an
// activate each microphone, of the attributes Windows' have (MFCaptureProbe: the endpoint's identifier, the friendly name,
// the SWD#MMDEVAPI symbolic link, the source type); a live source of one audio stream of the shared format (32-bit float
// at the device's rate and channels, as Windows' capture source gives the mix format); Start opens the microphone,
// MENewStream, MESourceStarted, MEStreamStarted and a MEStreamTick of the system time; each block of 10 ms (Windows' are of
// its engine's period, 40 to 70 ms in the VM) a RequestSample's MEMediaSample of the system time of its capture and that
// as the device's timestamp, the first discontinuous (half a second kept for the requests to come); Pause holds them,
// Stop closes it.
//

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Wpf.Interop.WebGpu.Direct3D;
using Microsoft.Wpf.Interop.WebGpu.MediaFoundation.Session;
using Microsoft.Wpf.Interop.WebGpu.MediaFoundation.Source;

namespace Microsoft.Wpf.Interop.WebGpu.MediaFoundation.Capture
{
    internal static class AudioCaptureIds
    {
        public static readonly Guid AUDCAP_ENDPOINT_ID = new("30da9258-feb9-47a7-a453-763a7a8e1c5f"), AUDCAP_SYMBOLIC_LINK = new("98d24b5e-5930-4614-b5a1-f600f9355a78");
        public static readonly Guid KSCATEGORY_AUDIO_INTERFACE = new("2eef81be-33fa-4800-9670-1cd474972c3f");

        /// <summary>The endpoint's identifier, of the device's (a capture endpoint's shape: {0.0.1.00000000}.{guid}).</summary>
        public static string Endpoint(AudioCaptureDevice d)
        {
            var h = SHA256.HashData(Encoding.UTF8.GetBytes("audio:" + d.Id));
            return "{0.0.1.00000000}.{" + new Guid(h.AsSpan(0, 16)) + "}";
        }

        public static string Link(AudioCaptureDevice d) => @"\\?\SWD#MMDEVAPI#" + Endpoint(d) + "#{" + KSCATEGORY_AUDIO_INTERFACE + "}";

        public static MFActivate Activate(AudioCaptureDevice d)
        {
            var a = new MFActivate(() => AudioCapture.Allowed ? (MF.S_OK, new MFAudioCaptureSource(d).Pointer) : (CaptureIds.E_ACCESSDENIED, IntPtr.Zero), MF.S_OK);
            a.Attributes.Set(AUDCAP_ENDPOINT_ID, MFValue.Of(Endpoint(d)));
            a.Attributes.Set(CaptureIds.MF_DEVSOURCE_ATTRIBUTE_FRIENDLY_NAME, MFValue.Of(d.Name));
            a.Attributes.Set(AUDCAP_SYMBOLIC_LINK, MFValue.Of(Link(d)));
            a.Attributes.Set(CaptureIds.MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE, MFValue.Of(CaptureIds.AUDCAP));
            return a;
        }
    }

    /// <summary>IMFMediaSource of a microphone.</summary>
    internal sealed unsafe class MFAudioCaptureSource : ComObject, ICaptureOwner
    {
        private static readonly Vtable s_vt = Build();
        private static readonly Guid MF_MT_AUDIO_CHANNEL_MASK = new("55fb5765-644a-4caf-8479-938983bb1588"), MF_MT_AUDIO_PREFER_WAVEFORMATEX = new("a901aaba-e037-458a-bdf6-545be2074042");
        private readonly AudioCaptureDevice _device;
        private readonly MFEventQueue _queue = new();
        private readonly MFPresentationDescriptor _pd = new();
        private readonly MFStreamDescriptor _sd;
        internal readonly MFCaptureStream Stream;
        public object Lock { get; } = new();
        public ComObject Source => this;
        private enum State { Stopped, Started, Paused, Shutdown }
        private State _state;
        private ICaptureSession? _session;
        private int _generation;

        private static Vtable Build()
        {
            var vt = new Vtable("IMFMediaSource", 13, MS.IID_IMFMediaSource, new Guid("2cd0bd52-bcd5-4b89-b62c-eadc0c031e7d"));
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

        public MFAudioCaptureSource(AudioCaptureDevice device) : base(s_vt)
        {
            _device = device;
            // the shared format as Windows' source gives it (Capture.windows.txt): no channel mask, the WAVEFORMATEX preferred
            var t = MFResampler.TypeOf(true, 32, (uint)device.Channels, (uint)device.Rate);
            t.Attributes.Delete(MF_MT_AUDIO_CHANNEL_MASK);
            t.Attributes.Set(MF_MT_AUDIO_PREFER_WAVEFORMATEX, 1u);
            _sd = new MFStreamDescriptor(0, new[] { t });
            t.AddRef(); _sd.Handler.Current = t;
            _pd.Add(_sd.Pointer, true);
            Stream = new MFCaptureStream(this, _sd, audio: true);
        }

        protected override void OnFinalRelease() { Shutdown(); _pd.Release(); _sd.Release(); Stream.Release(); }

        public bool IsShutdown { get { lock (Lock) return _state == State.Shutdown; } }
        public bool Started { get { lock (Lock) return _state == State.Started; } }
        public int RequestState => _state switch { State.Shutdown => MF.MF_E_SHUTDOWN, State.Stopped => MS.MF_E_MEDIA_SOURCE_WRONGSTATE, State.Paused => 1, _ => 0 };

        public void Pump()
        {
            ICaptureSession? s;
            lock (Lock) s = _state == State.Started ? _session : null;
            try { s?.Pump(); }
            catch (Exception e) { D3DLog.Error("the microphone's samples", e); }
        }

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
            }
            s?.Dispose();
        }

        private void Queue(uint type, MFValue? value = null) => _queue.QueueEvent(type, Guid.Empty, MF.S_OK, value);

        private void OnBlock(float[] samples, long time, int generation)
        {
            lock (Lock)
            {
                if (_state is State.Stopped or State.Shutdown || generation != _generation) return;
                var bytes = MemoryMarshal.AsBytes(samples.AsSpan()).ToArray();
                Stream.Frame(bytes, time);
            }
        }

        private static MFAudioCaptureSource? S(IntPtr self) => From<MFAudioCaptureSource>(self);

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

        [UnmanagedCallersOnly]
        private static int GetCharacteristics(IntPtr self, uint* c)
        {
            if (S(self) is not { } s || c == null) return MF.E_POINTER;
            if (s.IsShutdown) return CaptureIds.MF_E_NO_CAPTURE_DEVICES_AVAILABLE;
            *c = 5;                         // MFMEDIASOURCE_IS_LIVE | CAN_PAUSE
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
            try
            {
                ICaptureSession? open;
                lock (s.Lock)
                {
                    if (s._state == State.Shutdown) return MF.MF_E_SHUTDOWN;
                    if (pd == IntPtr.Zero) return MF.E_INVALIDARG;
                    if (format != null && *format != Guid.Empty) return MF.E_INVALIDARG;
                    bool resume = s._state == State.Paused;
                    if (s._session == null)
                    {
                        int gen = ++s._generation;
                        s._session = AudioCapture.Open(s._device, (b, t) => s.OnBlock(b, t, gen), out int hr);
                        if (s._session == null) return hr;
                        s.Stream.OpenedAudio(s._device.Rate, 4 * s._device.Channels);
                        resume = false;
                    }
                    open = s._session;
                    bool was = s.Stream.Created;
                    s.Stream.Active = s.Stream.Created = true;
                    Com.AddRef(s.Stream.Pointer);
                    s.Queue(was ? MS.MEUpdatedStream : MS.MENewStream, new MFValue { Type = MF.VT_UNKNOWN, Unknown = s.Stream.Pointer });
                    ulong now = (ulong)CaptureDevices.Now();
                    s._state = State.Started;
                    s.Queue(MS.MESourceStarted, new MFValue { Type = 20, U = now });
                    s.Stream.Queue(MS.MEStreamStarted, new MFValue { Type = 20, U = now });
                    if (!resume) s.Stream.Tick((long)now);
                    s.Stream.Resume();
                }
                open?.Start();
                return MF.S_OK;
            }
            catch (Exception e) { D3DLog.Error("IMFMediaSource.Start (audio capture)", e); return MF.E_FAIL; }
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
                s.Queue(MS.MESourceStopped);
                if (s.Stream.Active) s.Stream.Stopped();
            }
            try { close?.Dispose(); }
            catch (Exception e) { D3DLog.Error("IMFMediaSource.Stop (audio capture)", e); }
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
                s.Stream.Queue(MS.MEStreamPaused);
            }
            return MF.S_OK;
        }

        [UnmanagedCallersOnly]
        private static int ShutdownSlot(IntPtr self)
        {
            if (S(self) is not { } s) return MF.E_POINTER;
            try { s.Shutdown(); }
            catch (Exception e) { D3DLog.Error("IMFMediaSource.Shutdown (audio capture)", e); }
            return MF.S_OK;
        }
    }
}
