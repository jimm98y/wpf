// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// winmm's wave functions off Windows (Documentation/winmm-wave.md), as Windows' behave (WaveProbe, Wave.windows.txt):
//
//   - the devices: the system's output as waveOut device 0, its microphones as the waveIn devices, the mapper
//     (WAVE_MAPPER) over the first; the capabilities Windows gives (the mapper's "Microsoft Sound Mapper"); a mixer
//     of each device;
//   - an open of a format WaveFormat takes (WAVE_FORMAT_QUERY: only the answer); its callback (a function, a
//     window's or a thread's message, an event, none) told of the open and the close on the caller's thread, of each
//     buffer done from the device's;
//   - waveOut: a prepared header written plays after those before it (the stack's output, AudioOutput: the samples
//     converted to its 48 kHz stereo float, of the handle's volume and playback rate), done (WHDR_DONE) when the
//     device has taken its last sample; pause holds it, restart goes on, reset returns every header at once and
//     unpauses; the position (bytes or samples, any other asked as bytes) of what the device has taken; loops of
//     WHDR_BEGINLOOP .. WHDR_ENDLOOP;
//   - waveIn: the headers added filled in turn from the microphone (AudioCapture: its blocks converted to the
//     format) once started, each done when full; stop returns the one begun (on the caller's thread), reset every one;
//     the position of the bytes recorded into them;
//   - the error texts, Windows' own.
//
// On Windows the real winmm is used (no Win32ExportOnWindows).
//

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Wpf.Interop.WebGpu.Direct3D;
using Microsoft.Wpf.Interop.WebGpu.MediaFoundation.Capture;
using Microsoft.Wpf.Interop.WebGpu.MediaFoundation.Session;

namespace Microsoft.Wpf.Interop.WebGpu.Winmm
{
    public static unsafe class WaveExports
    {
        internal const uint MMSYSERR_NOERROR = 0, MMSYSERR_ERROR = 1, MMSYSERR_BADDEVICEID = 2, MMSYSERR_INVALHANDLE = 5, MMSYSERR_NODRIVER = 6,
            MMSYSERR_NOMEM = 7, MMSYSERR_NOTSUPPORTED = 8, MMSYSERR_BADERRNUM = 9, MMSYSERR_INVALFLAG = 10, MMSYSERR_INVALPARAM = 11,
            WAVERR_BADFORMAT = 32, WAVERR_STILLPLAYING = 33, WAVERR_UNPREPARED = 34;
        internal const uint WAVE_MAPPER = 0xFFFFFFFF, WAVE_FORMAT_QUERY = 1, WAVE_MAPPED = 4, WAVE_FORMAT_DIRECT = 8;
        internal const uint CALLBACK_TYPEMASK = 0x70000, CALLBACK_WINDOW = 0x10000, CALLBACK_THREAD = 0x20000, CALLBACK_FUNCTION = 0x30000, CALLBACK_EVENT = 0x50000;
        internal const uint WHDR_DONE = 1, WHDR_PREPARED = 2, WHDR_BEGINLOOP = 4, WHDR_ENDLOOP = 8, WHDR_INQUEUE = 0x10;
        internal const uint WOM_OPEN = 0x3BB, WOM_CLOSE = 0x3BC, WOM_DONE = 0x3BD, WIM_OPEN = 0x3BE, WIM_CLOSE = 0x3BF, WIM_DATA = 0x3C0;
        private const uint TIME_SAMPLES = 2, TIME_BYTES = 4;
        private const uint DRVM_MAPPER_PREFERRED_GET = 0x2015, DRVM_MAPPER_CONSOLEVOICECOM_GET = 0x2017, DRV_QUERYDEVICEINTERFACESIZE = 0x80C, DRV_QUERYDEVICEINTERFACE = 0x80D;
        private const string Mapper = "Microsoft Sound Mapper";

        [StructLayout(LayoutKind.Sequential)]
        internal struct WAVEHDR { public nint Data; public uint Length, Recorded; public nint User; public uint Flags, Loops; public nint Next, Reserved; }

        // ---- the devices ----

        private static int s_outputs = -1;

        /// <summary>The system's output: one device where it has one.</summary>
        private static int Outputs()
        {
            if (s_outputs >= 0) return s_outputs;
            var o = AudioOutput.Open(48000, 2);
            o?.Dispose();
            return s_outputs = o != null ? 1 : 0;
        }

        private static List<AudioCaptureDevice> Inputs()
        {
            try { return AudioCapture.Devices(); }
            catch (Exception e) { D3DLog.Error("waveIn's devices", e); return new List<AudioCaptureDevice>(); }
        }

        private static string OutputName() => OperatingSystem.IsMacOS() || OperatingSystem.IsIOS() ? "Speakers" : "Speakers (Default)";

        /// <summary>WAVEOUTCAPS / WAVEINCAPS / MIXERCAPS, W (names of 32 WCHARs) or A (of 32 bytes).</summary>
        private static uint Caps(void* caps, uint cb, bool wide, string name, ushort pid, uint version, uint? support, int tail, uint? mixer = null)
        {
            int nameBytes = wide ? 64 : 32, size = 8 + nameBytes + tail;
            if (caps == null || cb < size) return MMSYSERR_INVALPARAM;
            byte* c = (byte*)caps;
            new Span<byte>(c, size).Clear();
            *(ushort*)c = 1;                        // MM_MICROSOFT
            *(ushort*)(c + 2) = pid;
            *(uint*)(c + 4) = version;
            string n = name.Length > 31 ? name.Substring(0, 31) : name;
            if (wide) for (int i = 0; i < n.Length; i++) ((char*)(c + 8))[i] = n[i];
            else for (int i = 0; i < n.Length; i++) c[8 + i] = n[i] < 0x80 ? (byte)n[i] : (byte)'?';
            byte* t = c + 8 + nameBytes;
            if (mixer is { } destinations) { *(uint*)t = 0; *(uint*)(t + 4) = destinations; return MMSYSERR_NOERROR; }
            *(uint*)t = 0xFFFFF;                    // every WAVE_FORMAT_ of the 1M..96S
            *(ushort*)(t + 4) = 2;
            if (support is { } s) *(uint*)(t + 8) = s;
            return MMSYSERR_NOERROR;
        }

        private static uint OutCaps(nuint id, void* caps, uint cb, bool wide)
        {
            if (Device.Of((nint)id) is OutDevice d) id = d.Id;
            if (id == WAVE_MAPPER || id == unchecked((nuint)(nint)(int)WAVE_MAPPER))
                return Outputs() == 0 ? MMSYSERR_BADDEVICEID : Caps(caps, cb, wide, Mapper, 2, 0x500, 0x2E, 12);
            if (id >= (nuint)Outputs()) return MMSYSERR_BADDEVICEID;
            return Caps(caps, cb, wide, OutputName(), 0xFFFF, 0, 0x2E, 12);       // WAVECAPS_PLAYBACKRATE | VOLUME | LRVOLUME | SAMPLEACCURATE
        }

        private static uint InCaps(nuint id, void* caps, uint cb, bool wide)
        {
            if (Device.Of((nint)id) is InDevice d) id = d.Id;
            var ins = Inputs();
            if (id == WAVE_MAPPER || id == unchecked((nuint)(nint)(int)WAVE_MAPPER))
                return ins.Count == 0 ? MMSYSERR_BADDEVICEID : Caps(caps, cb, wide, Mapper, 2, 0x500, null, 8);
            if (id >= (nuint)ins.Count) return MMSYSERR_BADDEVICEID;
            return Caps(caps, cb, wide, ins[(int)id].Name, 0xFFFF, 0, null, 8);
        }

        [Win32Export("waveOutGetNumDevs")] public static uint waveOutGetNumDevs() => (uint)Outputs();
        [Win32Export("waveInGetNumDevs")] public static uint waveInGetNumDevs() => (uint)Inputs().Count;
        [Win32Export("waveOutGetDevCapsW", "waveOutGetDevCaps")] public static uint waveOutGetDevCapsW(nuint id, void* caps, uint cb) => OutCaps(id, caps, cb, true);
        [Win32Export("waveOutGetDevCapsA")] public static uint waveOutGetDevCapsA(nuint id, void* caps, uint cb) => OutCaps(id, caps, cb, false);
        [Win32Export("waveInGetDevCapsW", "waveInGetDevCaps")] public static uint waveInGetDevCapsW(nuint id, void* caps, uint cb) => InCaps(id, caps, cb, true);
        [Win32Export("waveInGetDevCapsA")] public static uint waveInGetDevCapsA(nuint id, void* caps, uint cb) => InCaps(id, caps, cb, false);

        /// <summary>The mixers: one of each output, then of each input.</summary>
        [Win32Export("mixerGetNumDevs")] public static uint mixerGetNumDevs() => (uint)(Outputs() + Inputs().Count);

        private static uint MixerCaps(nuint id, void* caps, uint cb, bool wide)
        {
            int outs = Outputs();
            var ins = Inputs();
            if (id >= (nuint)(outs + ins.Count)) return MMSYSERR_BADDEVICEID;
            string name = id < (nuint)outs ? OutputName() : ins[(int)id - outs].Name;
            return Caps(caps, cb, wide, name, 0xFFFF, 0, null, 8, 1);
        }

        [Win32Export("mixerGetDevCapsW", "mixerGetDevCaps")] public static uint mixerGetDevCapsW(nuint id, void* caps, uint cb) => MixerCaps(id, caps, cb, true);
        [Win32Export("mixerGetDevCapsA")] public static uint mixerGetDevCapsA(nuint id, void* caps, uint cb) => MixerCaps(id, caps, cb, false);

        // ---- the error texts (Windows' own) ----

        private static string? ErrorText(uint e) => e switch
        {
            0 => "The specified command was carried out.",
            1 => "Undefined external error.",
            2 => "A device ID has been used that is out of range for your system.",
            3 => "The driver was not enabled.",
            4 => "The specified device is already in use.  Wait until it is free, and then try again.",
            5 => "The specified device handle is invalid.",
            6 => "There is no driver installed on your system.",
            7 => "There is not enough memory available for this task.  Quit one or more applications to increase available memory, and then try again.",
            8 => "This function is not supported.  Use the Capabilities function to determine which functions and messages the driver supports.",
            9 => "An error number was specified that is not defined in the system.",
            10 => "An invalid flag was passed to a system function.",
            11 => "An invalid parameter was passed to a system function.",
            12 => "Handle being used simultaneously on another thread (eg callback).",
            13 => "Specified alias not found in WIN.INI.",
            20 => "The driver did not generate a valid OPEN callback.",
            32 => "The specified format is not supported or cannot be translated.  Use the Capabilities function to determine the supported formats.",
            33 => "Cannot perform this operation while media data is still playing.  Reset the device, or wait until the data is finished playing.",
            34 => "The wave header was not prepared.  Use the Prepare function to prepare the header, and then try again.",
            35 => "Cannot open the device without using the WAVE_ALLOWSYNC flag.  Use the flag, and then try again.",
            _ => null,
        };

        private static uint Text(uint e, void* text, uint cch, bool wide)
        {
            string? s = ErrorText(e);
            if (s == null) return MMSYSERR_BADERRNUM;
            if (text == null || cch == 0) return MMSYSERR_NOERROR;
            int n = (int)Math.Min((uint)s.Length, cch - 1);
            if (wide) { for (int i = 0; i < n; i++) ((char*)text)[i] = s[i]; ((char*)text)[n] = '\0'; }
            else { for (int i = 0; i < n; i++) ((byte*)text)[i] = (byte)s[i]; ((byte*)text)[n] = 0; }
            return MMSYSERR_NOERROR;
        }

        [Win32Export("waveOutGetErrorTextW", "waveOutGetErrorText")] public static uint waveOutGetErrorTextW(uint e, char* text, uint cch) => Text(e, text, cch, true);
        [Win32Export("waveOutGetErrorTextA")] public static uint waveOutGetErrorTextA(uint e, byte* text, uint cch) => Text(e, text, cch, false);
        [Win32Export("waveInGetErrorTextW", "waveInGetErrorText")] public static uint waveInGetErrorTextW(uint e, char* text, uint cch) => Text(e, text, cch, true);
        [Win32Export("waveInGetErrorTextA")] public static uint waveInGetErrorTextA(uint e, byte* text, uint cch) => Text(e, text, cch, false);

        // ---- an open device: its handle, its callback ----

        internal abstract class Device
        {
            private static readonly Dictionary<nint, Device> s_open = new();
            private static nint s_next = 0x5A000;

            public readonly nint Handle;
            public readonly uint Id;                 // the id opened (WAVE_MAPPER of the mapper)
            public readonly WaveFormat Format;
            private readonly nint _callback, _instance;
            private readonly uint _type;
            public readonly object Lock = new();
            public bool Closed;

            protected Device(uint id, WaveFormat f, nint callback, nint instance, uint flags)
            {
                Id = id; Format = f; _callback = callback; _instance = instance; _type = flags & CALLBACK_TYPEMASK;
                lock (s_open) { Handle = s_next += 0x10; s_open[Handle] = this; }
            }

            public static Device? Of(nint h) { lock (s_open) return s_open.TryGetValue(h, out var d) ? d : null; }
            public void Forget() { lock (s_open) s_open.Remove(Handle); }

            /// <summary>The callback told (on the calling thread).</summary>
            public void Notify(uint msg, nint header)
            {
                try
                {
                    switch (_type)
                    {
                        case CALLBACK_FUNCTION:
                            if (_callback != 0) ((delegate* unmanaged<nint, uint, nint, nint, nint, void>)_callback)(Handle, msg, _instance, header, 0);
                            break;
                        case CALLBACK_WINDOW:
                            (s_post ??= AppContext.GetData("WpfWebGpu.PostMessage") as Func<IntPtr, uint, IntPtr, IntPtr, bool>)?.Invoke(_callback, msg, Handle, header);
                            break;
                        case CALLBACK_THREAD:
                            (s_postThread ??= AppContext.GetData("WpfWebGpu.PostThreadMessage") as Func<uint, uint, IntPtr, IntPtr, bool>)?.Invoke((uint)_callback, msg, Handle, header);
                            break;
                        case CALLBACK_EVENT:
                            Win32Events.Set(_callback);
                            break;
                    }
                }
                catch (Exception e) { D3DLog.Error("a wave callback", e); }
            }

            private static Func<IntPtr, uint, IntPtr, IntPtr, bool>? s_post;
            private static Func<uint, uint, IntPtr, IntPtr, bool>? s_postThread;
        }

        private static uint Opened(nint* handle, uint id, void* format, nint callback, nint instance, uint flags, int devices, Func<uint, WaveFormat, Device?> open)
        {
            if (format == null) return MMSYSERR_INVALPARAM;
            if (id != WAVE_MAPPER && id >= (uint)devices) return MMSYSERR_BADDEVICEID;
            if (devices == 0) return MMSYSERR_BADDEVICEID;
            bool mapped = (id == WAVE_MAPPER || (flags & WAVE_MAPPED) != 0) && (flags & WAVE_FORMAT_DIRECT) == 0;
            uint r = WaveFormat.Parse((byte*)format, mapped, out var f);
            if (r != 0 || (flags & WAVE_FORMAT_QUERY) != 0) return r;
            if (handle == null) return MMSYSERR_INVALPARAM;
            Device? d;
            try { d = open(id, f!); }
            catch (Exception e) { D3DLog.Error("a wave device's open", e); d = null; }
            if (d == null) return MMSYSERR_NODRIVER;
            *handle = d.Handle;
            return MMSYSERR_NOERROR;
        }

        private static uint Prepare(WAVEHDR* h, uint cb)
        {
            if (h == null || cb < sizeof(WAVEHDR)) return MMSYSERR_INVALPARAM;
            h->Flags |= WHDR_PREPARED;
            return MMSYSERR_NOERROR;
        }

        private static uint Unprepare(WAVEHDR* h, uint cb)
        {
            if (h == null || cb < sizeof(WAVEHDR)) return MMSYSERR_INVALPARAM;
            if ((h->Flags & WHDR_INQUEUE) != 0) return WAVERR_STILLPLAYING;
            h->Flags &= ~WHDR_PREPARED;
            return MMSYSERR_NOERROR;
        }

        /// <summary>A header to queue: prepared, not queued already.</summary>
        private static uint Queueable(WAVEHDR* h, uint cb)
        {
            if (h == null || cb < sizeof(WAVEHDR)) return MMSYSERR_INVALPARAM;
            if ((h->Flags & WHDR_PREPARED) == 0) return WAVERR_UNPREPARED;
            if ((h->Flags & WHDR_INQUEUE) != 0) return WAVERR_STILLPLAYING;
            return MMSYSERR_NOERROR;
        }

        private static uint Position(void* mmtime, uint cb, long bytes, int blockAlign)
        {
            if (mmtime == null || cb < 12) return MMSYSERR_ERROR;
            uint* t = (uint*)mmtime;
            if (t[0] == TIME_SAMPLES) t[1] = (uint)(bytes / blockAlign);
            else { t[0] = TIME_BYTES; t[1] = (uint)bytes; }
            return MMSYSERR_NOERROR;
        }

        /// <summary>The messages of the device's buffers, on another thread (as Windows' reset tells them).</summary>
        private static void Later(Device d, uint msg, List<nint> headers)
        {
            if (headers.Count == 0) return;
            ThreadPool.UnsafeQueueUserWorkItem(_ => { foreach (var h in headers) d.Notify(msg, h); }, null);
        }

        private static uint Interface(nuint p1, nuint p2, bool output)
        {
            string path = @"\\?\SWD#MMDEVAPI#{0.0." + (output ? "0" : "1") + ".00000000}.{" + (output ? "b3f8fa53-0004-438e-9003-51a46e139bfc" : "3c5a7e3f-2a5f-4b9e-9e1c-1b2d4a6f8c10") + "}#{e6327cad-dcec-4949-ae8a-991e976a79d2}";
            if (p1 == 0) return MMSYSERR_INVALPARAM;
            uint size = (uint)(path.Length + 1) * 2;
            if (p2 == 0) { *(uint*)p1 = size; return MMSYSERR_NOERROR; }
            if ((uint)p2 < size) return MMSYSERR_INVALPARAM;
            char* s = (char*)p1;
            for (int i = 0; i < path.Length; i++) s[i] = path[i];
            s[path.Length] = '\0';
            return MMSYSERR_NOERROR;
        }

        private static uint Message(nint h, uint msg, nuint p1, nuint p2, bool output)
        {
            switch (msg)
            {
                case DRVM_MAPPER_PREFERRED_GET or DRVM_MAPPER_CONSOLEVOICECOM_GET:
                    if (p1 == 0 || p2 == 0) return MMSYSERR_INVALPARAM;
                    *(uint*)p1 = 0; *(uint*)p2 = 0;
                    return MMSYSERR_NOERROR;
                case DRV_QUERYDEVICEINTERFACESIZE:
                    return Interface(p1, 0, output);
                case DRV_QUERYDEVICEINTERFACE:
                    return Interface(p1, p2, output);
                default:
                    return MMSYSERR_NOTSUPPORTED;
            }
        }

        // ---- waveOut ----

        internal sealed class OutDevice : Device
        {
            private AudioOutput? _output;
            private readonly LinearResampler _resampler = new(2);
            private readonly List<nint> _queue = new();             // the headers written, in order, not yet done
            private sealed class Played { public long OutEnd = -1; public long SrcStart; public bool Final; }
            private readonly Dictionary<nint, Played> _played = new();
            private readonly List<(long outStart, long outEnd, long srcStart, long srcEnd)> _segments = new();
            private int _feed, _feedOffset, _loopStart = -1;
            private uint _loopsLeft;
            private bool _breakLoop;
            private long _srcWritten, _trimmed;        // the source's bytes written; those of the segments played and let go
            private int _generation;
            public uint Volume = 0xFFFFFFFF, Rate = 0x10000;
            public bool Paused;
            private Thread? _feeder, _notifier;

            public OutDevice(uint id, WaveFormat f, nint cb, nint inst, uint flags) : base(id, f, cb, inst, flags) { }

            public bool Start()
            {
                _output = AudioOutput.Open(48000, 2);
                if (_output == null) return false;
                _output.Start();
                _feeder = new Thread(Feed) { IsBackground = true, Name = "waveOut" };
                _notifier = new Thread(Watch) { IsBackground = true, Name = "waveOut done" };
                _feeder.Start(); _notifier.Start();
                return true;
            }

            public int Queued { get { lock (Lock) return _queue.Count; } }

            public void Write(WAVEHDR* h)
            {
                lock (Lock)
                {
                    h->Flags = (h->Flags & ~WHDR_DONE) | WHDR_INQUEUE;
                    _queue.Add((nint)h);
                    Monitor.PulseAll(Lock);
                }
            }

            /// <summary>Converts the queued headers' bytes into the output, in chunks of 10 ms of the source.</summary>
            private void Feed()
            {
                var src = Array.Empty<float>();
                var stereo = new List<float>();
                var outBuf = new List<float>();
                while (true)
                {
                    WAVEHDR* h; int offset, count, gen; double step; uint volume;
                    lock (Lock)
                    {
                        while (!Closed && _feed >= _queue.Count) Monitor.Wait(Lock, 50);
                        if (Closed) return;
                        h = (WAVEHDR*)_queue[_feed];
                        if (_feedOffset == 0)
                        {
                            if ((h->Flags & WHDR_BEGINLOOP) != 0 && _loopStart < 0) { _loopStart = _feed; _loopsLeft = Math.Max(h->Loops, 1); _breakLoop = false; }
                            if (!_played.ContainsKey((nint)h)) _played[(nint)h] = new Played { SrcStart = _srcWritten };
                        }
                        offset = _feedOffset;
                        int chunk = Math.Max(Format.BlockAlign, Format.Rate / 100 * Format.BlockAlign);
                        count = (int)Math.Min((uint)chunk, h->Length - (uint)Math.Min((uint)offset, h->Length));
                        count -= count % Format.BlockAlign;
                        gen = _generation;
                        step = Format.Rate * (Rate / 65536.0) / 48000.0;
                        volume = Volume;
                    }
                    int frames = count / Format.BlockAlign;
                    if (frames > 0)
                    {
                        int samples = frames * Format.Channels;
                        if (src.Length < samples) src = new float[samples];
                        Format.Decode(new ReadOnlySpan<byte>((byte*)h->Data + offset, count), src);
                        stereo.Clear();
                        float l = (volume & 0xFFFF) / 65535f, r = (volume >> 16) / 65535f;
                        for (int i = 0; i < frames; i++)
                        {
                            var (a, b) = Mix(src.AsSpan(i * Format.Channels, Format.Channels));
                            stereo.Add(a * l); stereo.Add(b * r);
                        }
                        outBuf.Clear();
                        _resampler.Process(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(stereo), step, outBuf);
                        long before = _output!.Written;
                        int wrote = 0, want = outBuf.Count / 2;
                        while (wrote < want)
                        {
                            int n = _output.Write(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(outBuf).Slice(wrote * 2), 200);
                            wrote += n;
                            lock (Lock) if (Closed || gen != _generation) break;
                        }
                        lock (Lock)
                        {
                            if (gen != _generation) continue;
                            _segments.Add((before, before + wrote, _srcWritten, _srcWritten + count));
                            _srcWritten += count;
                        }
                    }
                    lock (Lock)
                    {
                        if (gen != _generation) continue;
                        _feedOffset = offset + count;
                        if (count == 0 || _feedOffset >= h->Length)
                        {
                            _feedOffset = 0;
                            bool end = (h->Flags & WHDR_ENDLOOP) != 0 && _loopStart >= 0;
                            if (end && --_loopsLeft > 0 && !_breakLoop)
                            {
                                _played[(nint)h].OutEnd = _output!.Written;
                                _feed = _loopStart;                    // the loop again
                            }
                            else
                            {
                                if (end || _loopStart < 0)
                                {
                                    // the last pass of the headers up to here: done when played
                                    for (int i = _loopStart < 0 ? _feed : _loopStart; i <= _feed; i++) if (_played.TryGetValue(_queue[i], out var p)) p.Final = true;
                                    _loopStart = -1;
                                }
                                _played[(nint)h].OutEnd = _output!.Written;
                                _feed++;
                            }
                        }
                    }
                }
            }

            private static (float, float) Mix(ReadOnlySpan<float> f)
            {
                if (f.Length == 1) return (f[0], f[0]);
                if (f.Length == 2) return (f[0], f[1]);
                // a multichannel frame of the usual order (FL FR FC LFE BL BR SL SR): its stereo
                float c = f[2] * 0.7071f, l = f[0] + c, r = f[1] + c;
                for (int i = 4; i < f.Length; i++) { if ((i & 1) == 0) l += f[i] * 0.7071f; else r += f[i] * 0.7071f; }
                return (l, r);
            }

            /// <summary>The headers whose last sample the device took: done, told.</summary>
            private void Watch()
            {
                var done = new List<nint>();
                while (true)
                {
                    lock (Lock)
                    {
                        if (Closed) return;
                        long consumed = _output!.Consumed;
                        while (_queue.Count > 0 && _feed > 0 && _played.TryGetValue(_queue[0], out var p) && p.Final && p.OutEnd >= 0 && consumed >= p.OutEnd)
                        {
                            var h = (WAVEHDR*)_queue[0];
                            h->Flags = (h->Flags & ~WHDR_INQUEUE) | WHDR_DONE;
                            _played.Remove(_queue[0]);
                            done.Add(_queue[0]);
                            _queue.RemoveAt(0); _feed--;
                            if (_loopStart >= 0) _loopStart--;
                        }
                        while (_segments.Count > 1 && _segments[0].outEnd <= consumed) { _trimmed = _segments[0].srcEnd; _segments.RemoveAt(0); }
                    }
                    foreach (var h in done) Notify(WOM_DONE, h);
                    done.Clear();
                    Thread.Sleep(5);
                }
            }

            /// <summary>The bytes the device has taken since the open or the last reset.</summary>
            public long PositionBytes()
            {
                lock (Lock)
                {
                    long consumed = _output!.Consumed, bytes = _trimmed;
                    foreach (var s in _segments)
                    {
                        if (consumed >= s.outEnd) { bytes = s.srcEnd; continue; }
                        if (consumed > s.outStart && s.outEnd > s.outStart) bytes = s.srcStart + (s.srcEnd - s.srcStart) * (consumed - s.outStart) / (s.outEnd - s.outStart);
                        break;
                    }
                    bytes -= bytes % Format.BlockAlign;
                    return Math.Max(0, bytes);
                }
            }

            public void Pause() { lock (Lock) { if (!Paused) _output!.Pause(); Paused = true; } }
            public void Restart() { lock (Lock) { if (Paused) _output!.Start(); Paused = false; } }
            public void BreakLoop() { lock (Lock) _breakLoop = true; }

            /// <summary>Every header done at once (told later), the position back to 0, playing again.</summary>
            public List<nint> Reset()
            {
                lock (Lock)
                {
                    _generation++;
                    _output!.Flush();
                    var done = new List<nint>(_queue);
                    foreach (var p in done) { var h = (WAVEHDR*)p; h->Flags = (h->Flags & ~WHDR_INQUEUE) | WHDR_DONE; }
                    _queue.Clear(); _played.Clear(); _segments.Clear();
                    _feed = _feedOffset = 0; _loopStart = -1;
                    _resampler.Reset();
                    _srcWritten = _trimmed = 0;
                    if (Paused) { _output.Start(); Paused = false; }
                    Monitor.PulseAll(Lock);
                    return done;
                }
            }

            public void Close()
            {
                lock (Lock) { Closed = true; Monitor.PulseAll(Lock); }
                _feeder?.Join(500); _notifier?.Join(500);
                _output?.Dispose();
                Forget();
            }
        }

        private static OutDevice? Out(nint h) => Device.Of(h) as OutDevice is { Closed: false } d ? d : null;

        [Win32Export("waveOutOpen")]
        public static uint waveOutOpen(nint* handle, uint id, void* format, nint callback, nint instance, uint flags)
            => Opened(handle, id, format, callback, instance, flags, Outputs(), (i, f) =>
            {
                var d = new OutDevice(i, f, callback, instance, flags);
                if (!d.Start()) { d.Forget(); return null; }
                d.Notify(WOM_OPEN, 0);
                return d;
            });

        [Win32Export("waveOutClose")]
        public static uint waveOutClose(nint h)
        {
            if (Out(h) is not { } d) return MMSYSERR_INVALHANDLE;
            if (d.Queued > 0) return WAVERR_STILLPLAYING;
            d.Close();
            d.Notify(WOM_CLOSE, 0);
            return MMSYSERR_NOERROR;
        }

        [Win32Export("waveOutPrepareHeader")] public static uint waveOutPrepareHeader(nint h, void* hdr, uint cb) => Out(h) == null ? MMSYSERR_INVALHANDLE : Prepare((WAVEHDR*)hdr, cb);
        [Win32Export("waveOutUnprepareHeader")] public static uint waveOutUnprepareHeader(nint h, void* hdr, uint cb) => Out(h) == null ? MMSYSERR_INVALHANDLE : Unprepare((WAVEHDR*)hdr, cb);

        [Win32Export("waveOutWrite")]
        public static uint waveOutWrite(nint h, void* hdr, uint cb)
        {
            uint r = Queueable((WAVEHDR*)hdr, cb);
            if (r != 0) return r;
            if (Out(h) is not { } d) return MMSYSERR_INVALHANDLE;
            d.Write((WAVEHDR*)hdr);
            return MMSYSERR_NOERROR;
        }

        [Win32Export("waveOutPause")] public static uint waveOutPause(nint h) { if (Out(h) is not { } d) return MMSYSERR_INVALHANDLE; d.Pause(); return 0; }
        [Win32Export("waveOutRestart")] public static uint waveOutRestart(nint h) { if (Out(h) is not { } d) return MMSYSERR_INVALHANDLE; d.Restart(); return 0; }
        [Win32Export("waveOutBreakLoop")] public static uint waveOutBreakLoop(nint h) { if (Out(h) is not { } d) return MMSYSERR_INVALHANDLE; d.BreakLoop(); return 0; }

        [Win32Export("waveOutReset")]
        public static uint waveOutReset(nint h)
        {
            if (Out(h) is not { } d) return MMSYSERR_INVALHANDLE;
            Later(d, WOM_DONE, d.Reset());
            return MMSYSERR_NOERROR;
        }

        [Win32Export("waveOutGetPosition")]
        public static uint waveOutGetPosition(nint h, void* t, uint cb) => Out(h) is { } d ? Position(t, cb, d.PositionBytes(), d.Format.BlockAlign) : MMSYSERR_INVALHANDLE;

        private static uint s_deviceVolume = 0xFFFFFFFF;

        [Win32Export("waveOutGetVolume")]
        public static uint waveOutGetVolume(nint h, uint* v)
        {
            if (v == null) return MMSYSERR_INVALPARAM;
            if (Out(h) is { } d) { *v = d.Volume; return 0; }
            if ((nuint)h < (nuint)Outputs() || (uint)h == WAVE_MAPPER) { *v = s_deviceVolume; return 0; }
            return MMSYSERR_INVALHANDLE;
        }

        [Win32Export("waveOutSetVolume")]
        public static uint waveOutSetVolume(nint h, uint v)
        {
            if (Out(h) is { } d) { d.Volume = v; return 0; }
            if ((nuint)h < (nuint)Outputs() || (uint)h == WAVE_MAPPER) { s_deviceVolume = v; return 0; }
            return MMSYSERR_INVALHANDLE;
        }

        [Win32Export("waveOutGetPitch")] public static uint waveOutGetPitch(nint h, uint* v) => Out(h) == null ? MMSYSERR_INVALHANDLE : MMSYSERR_NOTSUPPORTED;
        [Win32Export("waveOutSetPitch")] public static uint waveOutSetPitch(nint h, uint v) => Out(h) == null ? MMSYSERR_INVALHANDLE : MMSYSERR_NOTSUPPORTED;

        [Win32Export("waveOutGetPlaybackRate")]
        public static uint waveOutGetPlaybackRate(nint h, uint* v)
        {
            if (Out(h) is not { } d) return MMSYSERR_INVALHANDLE;
            if (v == null) return MMSYSERR_INVALPARAM;
            *v = d.Rate;
            return 0;
        }

        [Win32Export("waveOutSetPlaybackRate")]
        public static uint waveOutSetPlaybackRate(nint h, uint v)
        {
            if (Out(h) is not { } d) return MMSYSERR_INVALHANDLE;
            if (v == 0) return MMSYSERR_INVALPARAM;
            d.Rate = v;
            return 0;
        }

        [Win32Export("waveOutGetID")]
        public static uint waveOutGetID(nint h, uint* id)
        {
            if (id == null) return MMSYSERR_INVALPARAM;
            if (Out(h) is not { } d) return MMSYSERR_INVALHANDLE;
            *id = d.Id;
            return 0;
        }

        [Win32Export("waveOutMessage")]
        public static uint waveOutMessage(nint h, uint msg, nuint p1, nuint p2) => Message(h, msg, p1, p2, true);

        // ---- waveIn ----

        internal sealed class InDevice : Device
        {
            private readonly AudioCaptureDevice _device;
            private ICaptureSession? _session;
            private readonly List<nint> _queue = new();
            private LinearResampler? _resampler;
            private readonly List<float> _converted = new();
            private long _recorded;
            public bool Started;
            private Thread? _pump;

            public InDevice(uint id, WaveFormat f, nint cb, nint inst, uint flags, AudioCaptureDevice device) : base(id, f, cb, inst, flags) { _device = device; }

            public int Queued { get { lock (Lock) return _queue.Count; } }
            public long Recorded { get { lock (Lock) return _recorded; } }

            public void Add(WAVEHDR* h)
            {
                lock (Lock)
                {
                    h->Flags = (h->Flags & ~WHDR_DONE) | WHDR_INQUEUE;
                    h->Recorded = 0;
                    _queue.Add((nint)h);
                }
            }

            public uint Start()
            {
                lock (Lock)
                {
                    if (Started) return 0;
                    if (_session == null)
                    {
                        _session = AudioCapture.Open(_device, (b, t) => Block(b), out int hr);
                        if (_session == null) return hr == CaptureIds.E_ACCESSDENIED ? MMSYSERR_NOTSUPPORTED : MMSYSERR_NODRIVER;
                        _resampler = new LinearResampler(Format.Channels);
                    }
                    Started = true;
                }
                _session.Start();
                if (OperatingSystem.IsBrowser() && _pump == null)
                {
                    // the browser's microphone gives its blocks when asked
                    _pump = new Thread(() => { while (true) { ICaptureSession? s; lock (Lock) { if (Closed) return; s = _session; } try { s?.Pump(); } catch (Exception e) { D3DLog.Error("waveIn's pump", e); } Thread.Sleep(10); } }) { IsBackground = true, Name = "waveIn" };
                    _pump.Start();
                }
                return 0;
            }

            /// <summary>A block of the microphone's: into the headers, those filled done.</summary>
            private void Block(float[] samples)
            {
                var done = new List<nint>();
                lock (Lock)
                {
                    if (!Started || Closed) return;
                    int dc = _device.Channels, fc = Format.Channels, frames = samples.Length / dc;
                    var mapped = new float[frames * fc];
                    for (int i = 0; i < frames; i++)
                        for (int c = 0; c < fc; c++)
                            mapped[i * fc + c] = fc == 1 && dc > 1 ? (samples[i * dc] + samples[i * dc + 1]) * 0.5f : samples[i * dc + Math.Min(c, dc - 1)];
                    _resampler!.Process(mapped, (double)_device.Rate / Format.Rate, _converted);
                    int taken = 0;
                    while (_queue.Count > 0 && taken < _converted.Count)
                    {
                        var h = (WAVEHDR*)_queue[0];
                        int room = (int)(h->Length - h->Recorded) / Format.BlockAlign;
                        int n = Math.Min(room, (_converted.Count - taken) / fc);
                        Format.Encode(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_converted).Slice(taken, n * fc), new Span<byte>((byte*)h->Data + h->Recorded, n * Format.BlockAlign));
                        h->Recorded += (uint)(n * Format.BlockAlign);
                        _recorded += n * Format.BlockAlign;
                        taken += n * fc;
                        if ((int)(h->Length - h->Recorded) < Format.BlockAlign)
                        {
                            h->Flags = (h->Flags & ~WHDR_INQUEUE) | WHDR_DONE;
                            done.Add(_queue[0]);
                            _queue.RemoveAt(0);
                        }
                        if (n == 0 && (int)(h->Length - h->Recorded) >= Format.BlockAlign) break;
                    }
                    _converted.RemoveRange(0, _queue.Count > 0 ? taken : _converted.Count);    // (no header: the rest dropped)
                }
                foreach (var h in done) Notify(WIM_DATA, h);
            }

            /// <summary>Stopped: the header begun returned (its bytes recorded), on the caller's thread.</summary>
            public nint Stop()
            {
                ICaptureSession? s;
                nint begun = 0;
                lock (Lock)
                {
                    if (!Started) return 0;
                    Started = false;
                    s = _session;
                    if (_queue.Count > 0 && ((WAVEHDR*)_queue[0])->Recorded > 0)
                    {
                        var h = (WAVEHDR*)_queue[0];
                        h->Flags = (h->Flags & ~WHDR_INQUEUE) | WHDR_DONE;
                        begun = _queue[0];
                        _queue.RemoveAt(0);
                    }
                    _converted.Clear();
                }
                try { s?.Stop(); } catch (Exception e) { D3DLog.Error("waveInStop", e); }
                return begun;
            }

            /// <summary>Stopped, every header returned (told later), the position back to 0.</summary>
            public List<nint> Reset()
            {
                nint begun = Stop();
                lock (Lock)
                {
                    var done = new List<nint>(_queue);
                    if (begun != 0) done.Insert(0, begun);
                    foreach (var p in done) { var h = (WAVEHDR*)p; h->Flags = (h->Flags & ~WHDR_INQUEUE) | WHDR_DONE; }
                    _queue.Clear();
                    _recorded = 0;
                    _resampler?.Reset();
                    return done;
                }
            }

            public void Close()
            {
                ICaptureSession? s;
                lock (Lock) { Closed = true; s = _session; _session = null; }
                try { s?.Dispose(); } catch (Exception e) { D3DLog.Error("waveInClose", e); }
                _pump?.Join(200);
                Forget();
            }
        }

        private static InDevice? In(nint h) => Device.Of(h) as InDevice is { Closed: false } d ? d : null;

        [Win32Export("waveInOpen")]
        public static uint waveInOpen(nint* handle, uint id, void* format, nint callback, nint instance, uint flags)
        {
            var ins = Inputs();
            return Opened(handle, id, format, callback, instance, flags, ins.Count, (i, f) =>
            {
                var d = new InDevice(i, f, callback, instance, flags, ins[i == WAVE_MAPPER ? 0 : (int)i]);
                d.Notify(WIM_OPEN, 0);
                return d;
            });
        }

        [Win32Export("waveInClose")]
        public static uint waveInClose(nint h)
        {
            if (In(h) is not { } d) return MMSYSERR_INVALHANDLE;
            if (d.Queued > 0) return WAVERR_STILLPLAYING;
            d.Close();
            d.Notify(WIM_CLOSE, 0);
            return MMSYSERR_NOERROR;
        }

        [Win32Export("waveInPrepareHeader")] public static uint waveInPrepareHeader(nint h, void* hdr, uint cb) => In(h) == null ? MMSYSERR_INVALHANDLE : Prepare((WAVEHDR*)hdr, cb);
        [Win32Export("waveInUnprepareHeader")] public static uint waveInUnprepareHeader(nint h, void* hdr, uint cb) => In(h) == null ? MMSYSERR_INVALHANDLE : Unprepare((WAVEHDR*)hdr, cb);

        [Win32Export("waveInAddBuffer")]
        public static uint waveInAddBuffer(nint h, void* hdr, uint cb)
        {
            uint r = Queueable((WAVEHDR*)hdr, cb);
            if (r != 0) return r;
            if (In(h) is not { } d) return MMSYSERR_INVALHANDLE;
            d.Add((WAVEHDR*)hdr);
            return MMSYSERR_NOERROR;
        }

        [Win32Export("waveInStart")] public static uint waveInStart(nint h) => In(h) is { } d ? d.Start() : MMSYSERR_INVALHANDLE;

        [Win32Export("waveInStop")]
        public static uint waveInStop(nint h)
        {
            if (In(h) is not { } d) return MMSYSERR_INVALHANDLE;
            nint begun = d.Stop();
            if (begun != 0) d.Notify(WIM_DATA, begun);
            return MMSYSERR_NOERROR;
        }

        [Win32Export("waveInReset")]
        public static uint waveInReset(nint h)
        {
            if (In(h) is not { } d) return MMSYSERR_INVALHANDLE;
            Later(d, WIM_DATA, d.Reset());
            return MMSYSERR_NOERROR;
        }

        [Win32Export("waveInGetPosition")]
        public static uint waveInGetPosition(nint h, void* t, uint cb) => In(h) is { } d ? Position(t, cb, d.Recorded, d.Format.BlockAlign) : MMSYSERR_INVALHANDLE;

        [Win32Export("waveInGetID")]
        public static uint waveInGetID(nint h, uint* id)
        {
            if (id == null) return MMSYSERR_INVALPARAM;
            if (In(h) is not { } d) return MMSYSERR_INVALHANDLE;
            *id = d.Id;
            return 0;
        }

        [Win32Export("waveInMessage")]
        public static uint waveInMessage(nint h, uint msg, nuint p1, nuint p2) => Message(h, msg, p1, p2, false);
    }
}
