// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// The system's audio output, for the audio renderer (MFAudioRenderer.cs): interleaved 32-bit float at the renderer's
// mix format, written into a ring the device plays from (silence where it runs dry), paused, flushed. AudioToolbox's
// AudioQueue on macOS and iOS (its buffers refilled on its own thread), PulseAudio's simple API on Linux (the system's
// libpulse-simple: written on a thread of the output's), AAudio on Android (likewise), WebAudio in the browser
// (Browser/browser-codecs.js: the chunks scheduled back to back). None where the system has no output.
//

using System;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Wpf.Interop.WebGpu.Direct3D;

namespace Microsoft.Wpf.Interop.WebGpu.MediaFoundation.Session
{
    internal abstract class AudioOutput : IDisposable
    {
        public readonly int Rate, Channels;
        private readonly float[] _ring;
        private int _read, _count;
        protected readonly object Lock = new();
        private bool _disposed;

        protected AudioOutput(int rate, int channels, double seconds = 0.5)
        {
            Rate = rate; Channels = channels;
            _ring = new float[(int)(rate * seconds) * channels];
        }

        /// <summary>The system's output at the format; null where there is none.</summary>
        public static AudioOutput? Open(int rate, int channels)
        {
            try
            {
#if WGPU_BROWSER
                if (OperatingSystem.IsBrowser()) return WebAudioOutput.Open(rate, channels);
#endif
                if (OperatingSystem.IsMacOS() || OperatingSystem.IsIOS() || OperatingSystem.IsMacCatalyst()) return AudioQueueOutput.Open(rate, channels);
                if (OperatingSystem.IsAndroid()) return AAudioOutput.Open(rate, channels);
                if (OperatingSystem.IsLinux()) return PulseOutput.Open(rate, channels);
            }
            catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
            {
                D3DLog.Once("audio-output", "Media Foundation: no audio output here: " + e.Message);
            }
            return null;
        }

        /// <summary>The samples written into the ring, waiting (up to the time) for room; the frames written.</summary>
        public int Write(ReadOnlySpan<float> samples, int timeoutMs = 2000)
        {
            int written = 0, deadline = Environment.TickCount + timeoutMs;
            lock (Lock)
            {
                while (written < samples.Length && !_disposed)
                {
                    int room = _ring.Length - _count;
                    if (room == 0)
                    {
                        int left = deadline - Environment.TickCount;
                        if (left <= 0) break;
                        Monitor.Wait(Lock, Math.Min(left, 20));
                        continue;
                    }
                    int n = Math.Min(room, samples.Length - written);
                    for (int i = 0; i < n; i++) _ring[(_read + _count + i) % _ring.Length] = samples[written + i];
                    _count += n; written += n;
                }
            }
            return written / Channels;
        }

        /// <summary>What the device takes: the ring's samples into the span, silence for the rest.</summary>
        protected void Fill(Span<float> into)
        {
            lock (Lock)
            {
                int n = Math.Min(_count, into.Length);
                for (int i = 0; i < n; i++) into[i] = _ring[(_read + i) % _ring.Length];
                into.Slice(n).Clear();
                _read = (_read + n) % _ring.Length; _count -= n;
                Monitor.PulseAll(Lock);
            }
        }

        /// <summary>The samples in the ring now.</summary>
        protected int Buffered { get { lock (Lock) return _count; } }

        public void Flush()
        {
            lock (Lock) { _count = 0; Monitor.PulseAll(Lock); }
            OnFlush();
        }

        public abstract void Start();
        public abstract void Pause();
        protected virtual void OnFlush() { }
        protected abstract void Close();

        public void Dispose()
        {
            lock (Lock) { if (_disposed) return; _disposed = true; Monitor.PulseAll(Lock); }
            Close();
        }

        protected bool Disposed { get { lock (Lock) return _disposed; } }
    }

    /// <summary>AudioToolbox's AudioQueue: three buffers of 20 ms, refilled from the ring on the queue's thread.</summary>
    internal sealed unsafe class AudioQueueOutput : AudioOutput
    {
        private const string AT = "/System/Library/Frameworks/AudioToolbox.framework/AudioToolbox";
        [DllImport(AT)] private static extern int AudioQueueNewOutput(byte* format, IntPtr callback, IntPtr user, IntPtr runLoop, IntPtr mode, uint flags, IntPtr* queue);
        [DllImport(AT)] private static extern int AudioQueueAllocateBuffer(IntPtr queue, uint size, IntPtr* buffer);
        [DllImport(AT)] private static extern int AudioQueueEnqueueBuffer(IntPtr queue, IntPtr buffer, uint packets, IntPtr descriptions);
        [DllImport(AT)] private static extern int AudioQueueStart(IntPtr queue, IntPtr time);
        [DllImport(AT)] private static extern int AudioQueuePause(IntPtr queue);
        [DllImport(AT)] private static extern int AudioQueueStop(IntPtr queue, byte immediate);
        [DllImport(AT)] private static extern int AudioQueueDispose(IntPtr queue, byte immediate);

        private static readonly System.Collections.Generic.Dictionary<IntPtr, AudioQueueOutput> s_outputs = new();
        private static long s_next;
        private readonly IntPtr _key = (IntPtr)Interlocked.Increment(ref s_next);
        private IntPtr _queue;
        private readonly uint _bufferBytes;

        private AudioQueueOutput(int rate, int channels) : base(rate, channels) { _bufferBytes = (uint)(rate / 50 * channels * 4); }

        public static AudioOutput? Open(int rate, int channels)
        {
            var o = new AudioQueueOutput(rate, channels);
            var f = stackalloc byte[40];
            new Span<byte>(f, 40).Clear();
            *(double*)f = rate;
            *(uint*)(f + 8) = 0x6C70636D;                                          // 'lpcm'
            *(uint*)(f + 12) = 1 | 8;                                              // float, packed
            *(uint*)(f + 16) = (uint)(4 * channels);                               // bytes a packet
            *(uint*)(f + 20) = 1;                                                  // frames a packet
            *(uint*)(f + 24) = (uint)(4 * channels);                               // bytes a frame
            *(uint*)(f + 28) = (uint)channels;
            *(uint*)(f + 32) = 32;
            lock (s_outputs) s_outputs[o._key] = o;
            IntPtr q;
            int st = AudioQueueNewOutput(f, (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, IntPtr, void>)&Refill, o._key, IntPtr.Zero, IntPtr.Zero, 0, &q);
            if (st != 0) { lock (s_outputs) s_outputs.Remove(o._key); D3DLog.Once("audioqueue", $"Media Foundation: AudioQueueNewOutput: {st}"); return null; }
            o._queue = q;
            for (int i = 0; i < 3; i++)
            {
                IntPtr b;
                if (AudioQueueAllocateBuffer(q, o._bufferBytes, &b) != 0) continue;
                o.FillBuffer(b);
                AudioQueueEnqueueBuffer(q, b, 0, IntPtr.Zero);
            }
            return o;
        }

        /// <summary>A buffer (AudioQueueBuffer: capacity, data, byte size) filled from the ring.</summary>
        private void FillBuffer(IntPtr b)
        {
            float* data = *(float**)(b + 8);
            Fill(new Span<float>(data, (int)(_bufferBytes / 4)));
            *(uint*)(b + 16) = _bufferBytes;
        }

        [UnmanagedCallersOnly]
        private static void Refill(IntPtr user, IntPtr queue, IntPtr buffer)
        {
            AudioQueueOutput? o;
            lock (s_outputs) s_outputs.TryGetValue(user, out o);
            if (o == null || o.Disposed) return;
            o.FillBuffer(buffer);
            AudioQueueEnqueueBuffer(queue, buffer, 0, IntPtr.Zero);
        }

        public override void Start() => AudioQueueStart(_queue, IntPtr.Zero);
        public override void Pause() => AudioQueuePause(_queue);

        protected override void Close()
        {
            AudioQueueStop(_queue, 1);
            AudioQueueDispose(_queue, 1);
            lock (s_outputs) s_outputs.Remove(_key);
        }
    }

    /// <summary>An output written on a thread of its own: the ring's samples to the device, 10 ms at a time, while it runs.</summary>
    internal abstract class WriterOutput : AudioOutput
    {
        private Thread? _thread;
        private volatile bool _running;

        protected WriterOutput(int rate, int channels) : base(rate, channels) { }

        /// <summary>The frames to the device, blocking as it takes them.</summary>
        protected abstract void DeviceWrite(float[] samples, int frames);

        public override void Start()
        {
            if (_running) return;
            _running = true;
            _thread ??= new Thread(Loop) { IsBackground = true, Name = "Audio output" };
            if (!_thread.IsAlive) _thread.Start();
        }

        public override void Pause() => _running = false;

        private void Loop()
        {
            int frames = Rate / 100;
            var chunk = new float[frames * Channels];
            while (!Disposed)
            {
                if (!_running || Buffered == 0) { Thread.Sleep(5); continue; }
                Fill(chunk);
                try { DeviceWrite(chunk, frames); }
                catch (Exception e) { D3DLog.Error("the audio output", e); Thread.Sleep(10); }
            }
        }

        protected override void Close() { _thread?.Join(500); }
    }

    /// <summary>PulseAudio's simple API (the system's libpulse-simple).</summary>
    internal sealed unsafe class PulseOutput : WriterOutput
    {
        private const string Lib = "libpulse-simple.so.0";
        [DllImport(Lib)] private static extern IntPtr pa_simple_new(byte* server, byte* name, int dir, byte* dev, byte* stream, int* spec, IntPtr map, IntPtr attr, int* error);
        [DllImport(Lib)] private static extern int pa_simple_write(IntPtr s, float* data, nuint bytes, int* error);
        [DllImport(Lib)] private static extern int pa_simple_flush(IntPtr s, int* error);
        [DllImport(Lib)] private static extern void pa_simple_free(IntPtr s);

        private IntPtr _s;
        private PulseOutput(int rate, int channels) : base(rate, channels) { }

        public static AudioOutput? Open(int rate, int channels)
        {
            var spec = stackalloc int[3];
            spec[0] = 5;                                    // PA_SAMPLE_FLOAT32LE
            spec[1] = rate;
            spec[2] = channels;                             // (uint8_t channels: the low byte)
            int err;
            fixed (byte* name = "Media Foundation\0"u8)
            fixed (byte* stream = "Audio renderer\0"u8)
            {
                IntPtr s = pa_simple_new(null, name, 1, null, stream, spec, IntPtr.Zero, IntPtr.Zero, &err);       // PA_STREAM_PLAYBACK
                if (s == IntPtr.Zero) { D3DLog.Once("pulse", $"Media Foundation: no PulseAudio output ({err})"); return null; }
                return new PulseOutput(rate, channels) { _s = s };
            }
        }

        protected override void DeviceWrite(float[] samples, int frames)
        {
            int err;
            fixed (float* p = samples) pa_simple_write(_s, p, (nuint)(frames * Channels * 4), &err);
        }

        protected override void OnFlush() { int err; pa_simple_flush(_s, &err); }
        protected override void Close() { base.Close(); pa_simple_free(_s); }
    }

    /// <summary>Android's AAudio, written blocking.</summary>
    internal sealed unsafe class AAudioOutput : WriterOutput
    {
        private const string Lib = "libaaudio.so";
        [DllImport(Lib)] private static extern int AAudio_createStreamBuilder(IntPtr* builder);
        [DllImport(Lib)] private static extern void AAudioStreamBuilder_setFormat(IntPtr b, int format);
        [DllImport(Lib)] private static extern void AAudioStreamBuilder_setChannelCount(IntPtr b, int channels);
        [DllImport(Lib)] private static extern void AAudioStreamBuilder_setSampleRate(IntPtr b, int rate);
        [DllImport(Lib)] private static extern int AAudioStreamBuilder_openStream(IntPtr b, IntPtr* stream);
        [DllImport(Lib)] private static extern int AAudioStreamBuilder_delete(IntPtr b);
        [DllImport(Lib)] private static extern int AAudioStream_requestStart(IntPtr s);
        [DllImport(Lib)] private static extern int AAudioStream_requestPause(IntPtr s);
        [DllImport(Lib)] private static extern int AAudioStream_requestFlush(IntPtr s);
        [DllImport(Lib)] private static extern int AAudioStream_write(IntPtr s, float* buffer, int frames, long timeoutNanos);
        [DllImport(Lib)] private static extern int AAudioStream_close(IntPtr s);

        private IntPtr _s;
        private AAudioOutput(int rate, int channels) : base(rate, channels) { }

        public static AudioOutput? Open(int rate, int channels)
        {
            IntPtr b, s;
            if (AAudio_createStreamBuilder(&b) != 0) return null;
            AAudioStreamBuilder_setFormat(b, 2);                    // AAUDIO_FORMAT_PCM_FLOAT
            AAudioStreamBuilder_setChannelCount(b, channels);
            AAudioStreamBuilder_setSampleRate(b, rate);
            int r = AAudioStreamBuilder_openStream(b, &s);
            AAudioStreamBuilder_delete(b);
            if (r != 0) { D3DLog.Once("aaudio", $"Media Foundation: no AAudio output ({r})"); return null; }
            return new AAudioOutput(rate, channels) { _s = s };
        }

        public override void Start() { AAudioStream_requestStart(_s); base.Start(); }
        public override void Pause() { base.Pause(); AAudioStream_requestPause(_s); }

        protected override void DeviceWrite(float[] samples, int frames)
        {
            fixed (float* p = samples) AAudioStream_write(_s, p, frames, 100_000_000);
        }

        protected override void OnFlush() => AAudioStream_requestFlush(_s);
        protected override void Close() { base.Close(); AAudioStream_close(_s); }
    }
}
