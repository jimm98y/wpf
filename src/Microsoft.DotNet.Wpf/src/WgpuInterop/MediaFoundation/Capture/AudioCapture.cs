// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// The system's microphones, under MFEnumDeviceSources' audio capture devices (MFAudioCaptureSource.cs): each one's name and
// identifier, its shared format (32-bit float at the device's rate and channels: Windows' capture source gives the mix
// format); opened, its samples handed to a callback in blocks of 10 ms as they come, of the system's time. AudioQueue's
// input on macOS and iOS (the device by its UID: kAudioQueueProperty_CurrentDevice), PulseAudio's simple API on Linux
// (its default source), AAudio's input on Android (RECORD_AUDIO); the browser's through getUserMedia (WebCapture.cs).
//

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Wpf.Interop.WebGpu.Direct3D;
using Microsoft.Wpf.Interop.WebGpu.MediaFoundation.Codecs;

namespace Microsoft.Wpf.Interop.WebGpu.MediaFoundation.Capture
{
    internal sealed class AudioCaptureDevice
    {
        public string Name = "", Id = "";
        public int Rate = 48000, Channels = 2;
    }

    /// <summary>A block of interleaved float samples, of the system's time (100 ns) of its first.</summary>
    internal delegate void AudioBlock(float[] samples, long time);

    internal static class AudioCapture
    {
        private static bool Apple => OperatingSystem.IsMacOS() || OperatingSystem.IsIOS() || OperatingSystem.IsMacCatalyst();

        /// <summary>The microphones, as the system lists them.</summary>
        public static List<AudioCaptureDevice> Devices()
        {
            try
            {
#if WGPU_BROWSER
                if (OperatingSystem.IsBrowser()) return WebCapture.Microphones();
#endif
                if (Apple) return AvCapture.Microphones();
                if (OperatingSystem.IsAndroid()) return new List<AudioCaptureDevice> { new() { Name = "Microphone", Id = "default", Rate = 48000, Channels = 1 } };
                if (OperatingSystem.IsLinux()) return PulseInput.Available() ? new List<AudioCaptureDevice> { new() { Name = "Microphone", Id = "default", Rate = 48000, Channels = 2 } } : new();
            }
            catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException or TypeInitializationException) { }
            return new List<AudioCaptureDevice>();
        }

        /// <summary>Whether the application may use the microphones (where the system asks, it asks when one is opened).</summary>
        public static bool Allowed
        {
            get
            {
                try { return !Apple || AvCapture.MicrophoneAllowed; }
                catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException or TypeInitializationException) { return false; }
            }
        }

        /// <summary>The microphone opened, its blocks to the callback once started; null (and the HRESULT) where it cannot be.</summary>
        public static ICaptureSession? Open(AudioCaptureDevice device, AudioBlock blocks, out int hr)
        {
            hr = MF.S_OK;
            ICaptureSession? s = null;
            try
            {
#if WGPU_BROWSER
                if (OperatingSystem.IsBrowser()) return WebCapture.OpenMicrophone(device, blocks, out hr);
#endif
                if (Apple) s = AudioQueueInput.Open(device, blocks);
                else if (OperatingSystem.IsAndroid()) s = AAudioInput.Open(device, blocks, out hr);
                else if (OperatingSystem.IsLinux()) s = PulseInput.Open(device, blocks);
            }
            catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException or TypeInitializationException) { }
            if (s == null && hr >= 0) hr = CaptureIds.MF_E_NO_CAPTURE_DEVICES_AVAILABLE;
            return s;
        }
    }

    /// <summary>AudioToolbox's input queue: buffers of 10 ms, their samples to the callback on the queue's thread.</summary>
    internal sealed unsafe class AudioQueueInput : ICaptureSession
    {
        private const string AT = "/System/Library/Frameworks/AudioToolbox.framework/AudioToolbox";
        [DllImport(AT)] private static extern int AudioQueueNewInput(byte* format, IntPtr callback, IntPtr user, IntPtr runLoop, IntPtr mode, uint flags, IntPtr* queue);
        [DllImport(AT)] private static extern int AudioQueueAllocateBuffer(IntPtr queue, uint size, IntPtr* buffer);
        [DllImport(AT)] private static extern int AudioQueueEnqueueBuffer(IntPtr queue, IntPtr buffer, uint packets, IntPtr descriptions);
        [DllImport(AT)] private static extern int AudioQueueStart(IntPtr queue, IntPtr time);
        [DllImport(AT)] private static extern int AudioQueueStop(IntPtr queue, byte immediate);
        [DllImport(AT)] private static extern int AudioQueuePause(IntPtr queue);
        [DllImport(AT)] private static extern int AudioQueueDispose(IntPtr queue, byte immediate);
        [DllImport(AT)] private static extern int AudioQueueSetProperty(IntPtr queue, uint id, void* data, uint size);

        private static readonly Dictionary<IntPtr, AudioQueueInput> s_inputs = new();
        private static long s_next;
        private readonly IntPtr _key = (IntPtr)Interlocked.Increment(ref s_next);
        private readonly AudioBlock _blocks;
        private readonly int _channels, _rate;
        private IntPtr _queue;
        private volatile bool _running;
        private int _disposed;

        private AudioQueueInput(AudioCaptureDevice d, AudioBlock blocks) { _blocks = blocks; _channels = d.Channels; _rate = d.Rate; }

        public static ICaptureSession? Open(AudioCaptureDevice d, AudioBlock blocks)
        {
            var q = new AudioQueueInput(d, blocks);
            var f = stackalloc byte[40];
            new Span<byte>(f, 40).Clear();
            *(double*)f = d.Rate;
            *(uint*)(f + 8) = 0x6C70636D;                      // 'lpcm'
            *(uint*)(f + 12) = 1 | 8;                          // float, packed
            *(uint*)(f + 16) = (uint)(4 * d.Channels);
            *(uint*)(f + 20) = 1;
            *(uint*)(f + 24) = (uint)(4 * d.Channels);
            *(uint*)(f + 28) = (uint)d.Channels;
            *(uint*)(f + 32) = 32;
            lock (s_inputs) s_inputs[q._key] = q;
            IntPtr aq;
            int st = AudioQueueNewInput(f, (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, IntPtr, IntPtr, uint, IntPtr, void>)&Filled, q._key, IntPtr.Zero, IntPtr.Zero, 0, &aq);
            if (st != 0) { lock (s_inputs) s_inputs.Remove(q._key); D3DLog.Once("aq-input", $"Media Foundation: AudioQueueNewInput: {st}"); return null; }
            q._queue = aq;
            // the device of the identifier (its UID), where it is not the default
            if (d.Id.Length > 0 && d.Id != "default")
            {
                IntPtr uid = AppleNative.CreateString(d.Id);
                AudioQueueSetProperty(aq, 0x61716364, &uid, (uint)IntPtr.Size);       // 'aqcd' kAudioQueueProperty_CurrentDevice
                AppleNative.CFRelease(uid);
            }
            uint bytes = (uint)(d.Rate / 100 * d.Channels * 4);
            for (int i = 0; i < 4; i++)
            {
                IntPtr b;
                if (AudioQueueAllocateBuffer(aq, bytes, &b) == 0) AudioQueueEnqueueBuffer(aq, b, 0, IntPtr.Zero);
            }
            return q;
        }

        [UnmanagedCallersOnly]
        private static void Filled(IntPtr user, IntPtr queue, IntPtr buffer, IntPtr startTime, uint packets, IntPtr descriptions)
        {
            AudioQueueInput? q;
            lock (s_inputs) s_inputs.TryGetValue(user, out q);
            if (q == null || q._disposed != 0) return;
            try
            {
                uint size = *(uint*)(buffer + 16);
                float* data = *(float**)(buffer + 8);
                if (q._running && size > 0)
                {
                    var samples = new float[size / 4];
                    new ReadOnlySpan<float>(data, samples.Length).CopyTo(samples);
                    long frames = samples.Length / q._channels;
                    q._blocks(samples, CaptureDevices.Now() - frames * 10_000_000 / q._rate);
                }
            }
            catch (Exception e) { D3DLog.Error("the microphone's samples", e); }
            AudioQueueEnqueueBuffer(queue, buffer, 0, IntPtr.Zero);
        }

        public void Start() { if (_disposed != 0) return; _running = true; AudioQueueStart(_queue, IntPtr.Zero); }
        public void Stop() { _running = false; AudioQueuePause(_queue); }        // (its buffers kept for a start again: a stop would drop them)

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
            _running = false;
            AudioQueueStop(_queue, 1);
            AudioQueueDispose(_queue, 1);
            lock (s_inputs) s_inputs.Remove(_key);
        }
    }

    /// <summary>A microphone read on a thread of its own, 10 ms a read, while it runs.</summary>
    internal abstract class ReaderInput : ICaptureSession
    {
        protected readonly int Rate, Channels;
        private readonly AudioBlock _blocks;
        private Thread? _thread;
        private volatile bool _running, _disposed;

        protected ReaderInput(AudioCaptureDevice d, AudioBlock blocks) { Rate = d.Rate; Channels = d.Channels; _blocks = blocks; }

        /// <summary>The frames read into the block (blocking); false where the device failed.</summary>
        protected abstract bool Read(float[] block, int frames);

        public virtual void Start()
        {
            if (_running || _disposed) return;
            _running = true;
            if (_thread != null) return;
            _thread = new Thread(Loop) { IsBackground = true, Name = "Microphone" };
            _thread.Start();
        }

        /// <summary>Stopped: the device still read, what it gives let go -- else the system's buffer of it, given all
        /// at once on the next start, would be a stopped device's recording.</summary>
        public virtual void Stop() => _running = false;

        private void Loop()
        {
            int frames = Rate / 100;
            while (!_disposed)
            {
                var block = new float[frames * Channels];
                if (!Read(block, frames)) { Thread.Sleep(10); continue; }
                if (_running) _blocks(block, CaptureDevices.Now() - 100_000);
            }
        }

        protected abstract void Close();

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Stop();
            _thread?.Join(500);
            Close();
        }
    }

    /// <summary>PulseAudio's simple API, recording from the default source.</summary>
    internal sealed unsafe class PulseInput : ReaderInput
    {
        private const string Lib = "libpulse-simple.so.0";
        [DllImport(Lib)] private static extern IntPtr pa_simple_new(byte* server, byte* name, int dir, byte* dev, byte* stream, int* spec, IntPtr map, IntPtr attr, int* error);
        [DllImport(Lib)] private static extern int pa_simple_read(IntPtr s, float* data, nuint bytes, int* error);
        [DllImport(Lib)] private static extern void pa_simple_free(IntPtr s);

        private IntPtr _s;
        private PulseInput(AudioCaptureDevice d, AudioBlock b) : base(d, b) { }

        /// <summary>Whether there is a PulseAudio server (its simple API loads and a recording stream opens).</summary>
        public static bool Available()
        {
            try { return NativeLibrary.TryLoad(Lib, out _); }
            catch (Exception) { return false; }
        }

        public static ICaptureSession? Open(AudioCaptureDevice d, AudioBlock blocks)
        {
            var spec = stackalloc int[3];
            spec[0] = 5; spec[1] = d.Rate; spec[2] = d.Channels;            // PA_SAMPLE_FLOAT32LE
            int err;
            fixed (byte* name = "Media Foundation\0"u8)
            fixed (byte* stream = "Audio capture\0"u8)
            {
                IntPtr s = pa_simple_new(null, name, 2, null, stream, spec, IntPtr.Zero, IntPtr.Zero, &err);     // PA_STREAM_RECORD
                if (s == IntPtr.Zero) { D3DLog.Once("pulse-in", $"Media Foundation: no PulseAudio source ({err})"); return null; }
                return new PulseInput(d, blocks) { _s = s };
            }
        }

        protected override bool Read(float[] block, int frames)
        {
            int err;
            fixed (float* p = block) return pa_simple_read(_s, p, (nuint)(frames * Channels * 4), &err) >= 0;
        }

        protected override void Close() => pa_simple_free(_s);
    }

    /// <summary>Android's AAudio input, read blocking (RECORD_AUDIO: without it, E_ACCESSDENIED).</summary>
    internal sealed unsafe class AAudioInput : ReaderInput
    {
        private const string Lib = "libaaudio.so";
        [DllImport(Lib)] private static extern int AAudio_createStreamBuilder(IntPtr* builder);
        [DllImport(Lib)] private static extern void AAudioStreamBuilder_setDirection(IntPtr b, int direction);
        [DllImport(Lib)] private static extern void AAudioStreamBuilder_setFormat(IntPtr b, int format);
        [DllImport(Lib)] private static extern void AAudioStreamBuilder_setChannelCount(IntPtr b, int channels);
        [DllImport(Lib)] private static extern void AAudioStreamBuilder_setSampleRate(IntPtr b, int rate);
        [DllImport(Lib)] private static extern int AAudioStreamBuilder_openStream(IntPtr b, IntPtr* stream);
        [DllImport(Lib)] private static extern int AAudioStreamBuilder_delete(IntPtr b);
        [DllImport(Lib)] private static extern int AAudioStream_requestStart(IntPtr s);
        [DllImport(Lib)] private static extern int AAudioStream_requestStop(IntPtr s);
        [DllImport(Lib)] private static extern int AAudioStream_read(IntPtr s, float* buffer, int frames, long timeoutNanos);
        [DllImport(Lib)] private static extern int AAudioStream_close(IntPtr s);
        private const int AAUDIO_ERROR_UNAVAILABLE = -889;

        private IntPtr _s;
        private AAudioInput(AudioCaptureDevice d, AudioBlock b) : base(d, b) { }

        public static ICaptureSession? Open(AudioCaptureDevice d, AudioBlock blocks, out int hr)
        {
            hr = MF.S_OK;
            IntPtr b, s;
            if (AAudio_createStreamBuilder(&b) != 0) return null;
            AAudioStreamBuilder_setDirection(b, 1);                 // AAUDIO_DIRECTION_INPUT
            AAudioStreamBuilder_setFormat(b, 2);                    // AAUDIO_FORMAT_PCM_FLOAT
            AAudioStreamBuilder_setChannelCount(b, d.Channels);
            AAudioStreamBuilder_setSampleRate(b, d.Rate);
            int r = AAudioStreamBuilder_openStream(b, &s);
            AAudioStreamBuilder_delete(b);
            if (r != 0) { if (r == AAUDIO_ERROR_UNAVAILABLE) hr = CaptureIds.E_ACCESSDENIED; D3DLog.Once("aaudio-in", $"Media Foundation: no AAudio input ({r})"); return null; }
            return new AAudioInput(d, blocks) { _s = s };
        }

        public override void Start() { AAudioStream_requestStart(_s); base.Start(); }
        public override void Stop() { base.Stop(); AAudioStream_requestStop(_s); }

        protected override bool Read(float[] block, int frames)
        {
            fixed (float* p = block) return AAudioStream_read(_s, p, frames, 200_000_000) > 0;
        }

        protected override void Close() => AAudioStream_close(_s);
    }
}
