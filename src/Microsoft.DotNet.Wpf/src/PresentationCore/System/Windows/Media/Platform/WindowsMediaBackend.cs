// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// WindowsMediaBackend -- the Windows IMediaBackend, backed by Media Foundation + waveOut.
//
// This is what took MediaElement off native milcore on Windows. Previously the Windows path built a
// native IMILMedia inside wpfgfx_cor3.dll: it decoded fine, but its frames were only ever composited
// by the native compositor, so under the managed WebGPU compositor DUCE.Channel.SendCommandMedia
// dropped them and video rendered nothing. Windows now goes through the same IMediaBackend seam as
// macOS/Linux/browser, so frames reach the compositor via SendVideoFrame and wpfgfx is not needed.
//
// Decode is the Media Foundation Source Reader (MFCreateSourceReaderFromURL), configured with
// MF_SOURCE_READER_ENABLE_VIDEO_PROCESSING so it hands back RGB32 -- which is BGRA32 in memory, the
// byte order the SendVideoFrame seam already expects -- no matter what the file actually contains.
// Audio is decoded to PCM by the same reader and rendered with waveOut.
//
// NO COM INTEROP. Media Foundation objects are COM objects, but nothing here uses [ComImport],
// CoCreateInstance, Marshal.GetObjectForIUnknown or the runtime's COM marshaller. Every object is
// produced by a FLAT exported factory function (MFCreateSourceReaderFromURL / MFCreateAttributes /
// MFCreateMediaType) and every method is invoked by indexing its vtable through
// delegate* unmanaged[Stdcall], i.e. plain function-pointer P/Invoke over a documented ABI. That
// keeps the fork's no-COM rule (which exists so the cross-platform engine stays portable and
// AOT-safe) intact, and this file is Windows-only anyway -- exactly like MacMediaBackend is
// Objective-C-only and LinuxMediaBackend is GStreamer-only. Slot numbers and GUIDs below were taken
// from the Windows SDK headers (mfreadwrite.h / mfobjects.h / mfapi.h / mfidl.h), not from memory.
//
// Threading. Unlike AVPlayer and GStreamer, the Source Reader does not own a decode thread: its
// ReadSample BLOCKS. So a decode thread pulls samples, converts video into pooled frame buffers and
// feeds PCM to waveOut, while a DispatcherTimer on the media dispatcher does everything the rest of
// WPF can see -- presenting frames whose timestamp is due, and raising Opened/Ended/Failed. Every
// IMediaBackend event therefore arrives on the dispatcher thread, matching the other backends.
//
// The clock. Audio is the master when it exists and the rate is 1x: waveOutGetPosition reports what
// the device has actually played, which is the only honest measure of "now" and keeps video locked
// to it for free. Without audio (or off-speed) a Stopwatch scaled by the rate takes over. Crossing
// between those two clock sources re-seeks, which resynchronises reader, audio device and clock in
// one step rather than trying to splice two timelines together.
//

using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading;
using System.Windows.Threading;

namespace System.Windows.Media
{
    [SupportedOSPlatform("windows")]
    internal sealed unsafe class WindowsMediaBackend : IMediaBackend
    {
        private readonly Dispatcher _dispatcher;
        private DispatcherTimer _timer;

        // ---- Media Foundation ----
        private IntPtr _reader;                 // IMFSourceReader*
        private int _videoStream = -1;          // real stream indices, not the FIRST_* sentinels: reading
        private int _audioStream = -1;          // ANY_STREAM reports the actual index it produced
        private int _width, _height;
        private int _stride;                    // may be negative == bottom-up
        private long _lengthTicks;

        // ---- decode thread ----
        private Thread _thread;
        private volatile bool _stop;
        private volatile bool _decodeEnded;     // reader hit end of stream; set by the decode thread
        private volatile Exception _failure;    // first decode-thread error, drained on the dispatcher

        // ---- video frames ----
        private const int MaxPendingFrames = 8;
        private readonly object _lock = new object();
        private readonly Queue<Frame> _pending = new Queue<Frame>();
        private readonly Stack<Frame> _free = new Stack<Frame>();
        private Frame _current;
        private bool _frameLocked;

        // ---- audio ----
        private const int AudioBufferCount = 8;
        private IntPtr _waveOut;
        private WaveFormatEx _waveFormat;
        private IntPtr[] _headers;              // WAVEHDR blocks (unmanaged, stable addresses)
        private IntPtr[] _headerData;
        private int _audioBufferBytes;
        private int _fillIndex = -1;            // header currently being filled
        private int _fillUsed;

        // ---- clock / transport ----
        private readonly System.Diagnostics.Stopwatch _sw = new System.Diagnostics.Stopwatch();
        private long _swBaseTicks;              // media time the stopwatch counts up from
        private long _audioBaseTicks;           // media time waveOut's sample counter counts up from
        private long _pausedTicks;
        private double _rate;                   // 0 == paused
        private double _volume = 0.5;
        private double _balance;

        // ---- observable state (dispatcher thread) ----
        private bool _openPending;
        private bool _opened, _failed, _ended;

        private int _framesDecoded;
        private int _framesPresented;
        private int _audioSyncs;

        private static readonly bool s_log = Environment.GetEnvironmentVariable("WPF_MEDIA_LOG") == "1";
        private static void Log(string m) { if (s_log) Console.WriteLine("MEDIA " + m); }

        internal WindowsMediaBackend(MediaPlayer player)
        {
            _dispatcher = player.Dispatcher;
        }

        private sealed class Frame
        {
            public long Ticks;
            public IntPtr Pixels;
            public int Capacity;
            public int Width, Height, RowBytes;
        }

        // ---------------------------------------------------------------- transport ----

        public void Open(string url)
        {
            Log($"Open {url}");
            Close();

            try
            {
                EnsureStarted();
                CreateReader(url);
                ConfigureStreams();
                OpenAudioDevice();
            }
            catch (Exception e)
            {
                Log("open failed: " + e.Message);
                Close();
                // Deferred like everything else observable: MediaFailed must not fire underneath the
                // caller that is still assigning MediaElement.Source.
                _failure = e;
                EnsureTimer();
                return;
            }

            _swBaseTicks = 0;
            _audioBaseTicks = 0;
            _pausedTicks = 0;
            _sw.Restart();

            _stop = false;
            _thread = new Thread(DecodeLoop)
            {
                IsBackground = true,
                Name = "WPF Media Decode",
            };
            _thread.Start();

            _openPending = true;
            EnsureTimer();
        }

        public void Close()
        {
            StopTimer();
            StopDecodeThread();
            CloseAudioDevice();

            if (_reader != IntPtr.Zero) { Release(_reader); _reader = IntPtr.Zero; }

            lock (_lock)
            {
                UnlockFrame();
                while (_pending.Count > 0) FreeFrame(_pending.Dequeue());
                while (_free.Count > 0) FreeFrame(_free.Pop());
                if (_current != null) { FreeFrame(_current); _current = null; }
                _newestQueuedTicks = -1;
            }

            _videoStream = _audioStream = -1;
            _width = _height = _stride = 0;
            _lengthTicks = 0;
            _rate = 0;
            _opened = _failed = _ended = _openPending = false;
            _decodeEnded = false;
            _failure = null;
            _sw.Reset();
        }

        public void SetRate(double rate)
        {
            Log($"SetRate {rate}");
            if (_reader == IntPtr.Zero) { _rate = rate; return; }

            double old = _rate;
            if (rate == 0)
            {
                _pausedTicks = NowTicks();
                _rate = 0;
                if (_waveOut != IntPtr.Zero) waveOutPause(_waveOut);
                return;
            }

            // Whether audio can be the clock depends on the rate being exactly 1x, so a change that
            // crosses that boundary changes clock source. Re-seek to the current position instead of
            // trying to splice the two timelines: it re-bases reader, audio device and clock together.
            bool wasAudioMaster = AudioIsMaster(old);
            _rate = rate;
            if (AudioIsMaster(rate) != wasAudioMaster && old != 0)
            {
                SetPositionTicks(_pausedTicks != 0 && old == 0 ? _pausedTicks : NowTicksFor(old));
                if (_waveOut != IntPtr.Zero && !AudioIsMaster(rate)) waveOutReset(_waveOut);
                return;
            }

            if (old == 0)
            {
                // Resuming: the stopwatch is the clock and it did not run while paused, so re-base it
                // onto the position we paused at. SyncClock pulls it onto the audio device afterwards.
                _swBaseTicks = _pausedTicks;
                _sw.Restart();
                if (_waveOut != IntPtr.Zero) waveOutRestart(_waveOut);
            }
        }

        public long GetPositionTicks()
        {
            long t = NowTicks();
            if (_lengthTicks > 0 && t > _lengthTicks) t = _lengthTicks;
            return t < 0 ? 0 : t;
        }

        public void SetPositionTicks(long ticks)
        {
            Log($"SetPosition {(double)ticks / TimeSpan.TicksPerSecond:F3}s");
            if (_reader == IntPtr.Zero) return;
            if (ticks < 0) ticks = 0;

            // The decode thread must not be inside ReadSample while the reader is repositioned.
            bool wasRunning = _thread != null;
            StopDecodeThread();

            Flush(_reader, unchecked((uint)MF_SOURCE_READER_ALL_STREAMS));
            SetPosition(_reader, ticks);

            if (_waveOut != IntPtr.Zero)
            {
                waveOutReset(_waveOut);          // drops queued audio AND zeroes the sample counter
                ResetAudioHeaders();
                if (_rate == 0) waveOutPause(_waveOut);
            }

            lock (_lock)
            {
                UnlockFrame();
                while (_pending.Count > 0) _free.Push(_pending.Dequeue());
                _newestQueuedTicks = -1;
            }

            _audioBaseTicks = ticks;
            _swBaseTicks = ticks;
            _pausedTicks = ticks;
            _sw.Restart();
            _decodeEnded = false;
            _ended = false;

            if (wasRunning)
            {
                _stop = false;
                _thread = new Thread(DecodeLoop) { IsBackground = true, Name = "WPF Media Decode" };
                _thread.Start();
            }
        }

        // ------------------------------------------------------- audio / scrubbing ----

        public void SetVolume(double volume) { _volume = volume; ApplyVolume(); }
        public void SetBalance(double balance) { _balance = balance; ApplyVolume(); }
        public void SetScrubbingEnabled(bool enabled) { /* frames are presented by the timer regardless. */ }
        public void NeedUIFrameUpdate() { /* frames are driven by the timer, not a per-pass reserve. */ }

        private void ApplyVolume()
        {
            if (_waveOut == IntPtr.Zero) return;
            // waveOut carries balance in the same word pair as volume: low half left, high half right.
            double v = Math.Clamp(_volume, 0.0, 1.0);
            double b = Math.Clamp(_balance, -1.0, 1.0);
            double left = v * (b > 0 ? 1.0 - b : 1.0);
            double right = v * (b < 0 ? 1.0 + b : 1.0);
            uint l = (uint)Math.Round(left * 0xFFFF);
            uint r = (uint)Math.Round(right * 0xFFFF);
            waveOutSetVolume(_waveOut, (r << 16) | l);
        }

        // ------------------------------------------------------------ state getters ----

        public bool IsBuffering => false;
        public bool CanPause => true;
        public double DownloadProgress => 1.0;
        public double BufferingProgress => 1.0;
        public int NaturalVideoWidth => _width;
        public int NaturalVideoHeight => _height;
        public bool HasAudio => _audioStream >= 0;
        public bool HasVideo => _videoStream >= 0 && _width > 0;
        public long MediaLengthTicks => _lengthTicks;

        public event Action FrameAvailable;
        public event Action Opened;
        public event Action Ended;
        public event Action<Exception> Failed;
        public event Action BufferingStarted;
        public event Action BufferingEnded;

        // --------------------------------------------------------------- frame pull ----

        public bool TryLockFrame(out IntPtr baseAddress, out int width, out int height, out int rowBytes)
        {
            baseAddress = IntPtr.Zero; width = 0; height = 0; rowBytes = 0;

            Frame f = _current;
            if (f == null || f.Pixels == IntPtr.Zero) return false;

            _frameLocked = true;
            baseAddress = f.Pixels;
            width = f.Width;
            height = f.Height;
            rowBytes = f.RowBytes;
            return true;
        }

        public void UnlockFrame() => _frameLocked = false;

        // ------------------------------------------------------------------- clock ----

        private bool AudioIsMaster(double rate) => _waveOut != IntPtr.Zero && rate == 1.0;

        /// <summary>
        /// The presentation clock, and it is deliberately the STOPWATCH rather than the audio device --
        /// with the audio position used only to correct it (see <see cref="SyncClock"/>).
        /// <para>
        /// Reading the clock straight off waveOutGetPosition is more accurate and deadlocks: the clock
        /// then stops dead whenever the audio device runs dry, and the decoder is thereby waiting on a
        /// clock that only its own submissions can restart. A free-running stopwatch cannot stall, so
        /// frames always come due, the queue always drains and the reader always wakes up again.
        /// </para>
        /// Pure and thread-safe: the decode thread reads it, only the dispatcher writes the base.
        /// </summary>
        private long NowTicks() => NowTicksFor(_rate);

        private long NowTicksFor(double rate)
        {
            if (rate == 0) return _pausedTicks;
            return _swBaseTicks + (long)(_sw.Elapsed.Ticks * rate);
        }

        /// <summary>Drift threshold before the stopwatch is pulled back onto the audio device's position.</summary>
        private static readonly long ResyncTicks = TimeSpan.TicksPerMillisecond * 80;

        /// <summary>
        /// Corrects the stopwatch against what the audio device has really played, so long playback does
        /// not drift out of lip-sync. Dispatcher thread only -- it is the sole writer of the clock base.
        /// </summary>
        private void SyncClock()
        {
            if (_rate == 0 || !AudioIsMaster(_rate)) return;

            long played = AudioPlayedTicks();
            if (played <= 0) return;                       // nothing has actually come out of the device yet

            if (s_log && _audioSyncs++ % 120 == 0)
            {
                Log($"audio played={played / (double)TimeSpan.TicksPerSecond:F2}s queued={BusyAudioHeaderCount()}/{AudioBufferCount} buffers");
            }

            long audio = _audioBaseTicks + played;
            if (Math.Abs(audio - NowTicks()) < ResyncTicks) return;

            _swBaseTicks = audio;
            _sw.Restart();
        }

        private long AudioPlayedTicks()
        {
            if (_waveOut == IntPtr.Zero || _waveFormat.nSamplesPerSec == 0) return 0;

            MMTIME t = default;
            t.wType = TIME_SAMPLES;
            if (waveOutGetPosition(_waveOut, ref t, (uint)sizeof(MMTIME)) != 0) return 0;

            // Drivers are allowed to answer in a different unit than the one asked for.
            long samples;
            if (t.wType == TIME_SAMPLES) samples = t.u0;
            else if (t.wType == TIME_BYTES) samples = _waveFormat.nBlockAlign > 0 ? t.u0 / _waveFormat.nBlockAlign : 0;
            else if (t.wType == TIME_MS) return (long)t.u0 * TimeSpan.TicksPerMillisecond;
            else return 0;

            return samples * TimeSpan.TicksPerSecond / _waveFormat.nSamplesPerSec;
        }

        // -------------------------------------------------------------- dispatcher ----

        private void EnsureTimer()
        {
            if (_timer != null) return;
            _timer = new DispatcherTimer(DispatcherPriority.Render, _dispatcher)
            {
                Interval = TimeSpan.FromMilliseconds(15),   // ~60Hz; a tick with nothing due is nearly free
            };
            _timer.Tick += OnTick;
            _timer.Start();
        }

        private void StopTimer()
        {
            if (_timer == null) return;
            _timer.Stop();
            _timer.Tick -= OnTick;
            _timer = null;
        }

        private void OnTick(object sender, EventArgs e)
        {
            Exception failure = _failure;
            if (failure != null && !_failed)
            {
                _failed = true;
                _failure = null;
                Failed?.Invoke(failure);
                return;
            }
            if (_failed) return;

            if (_openPending)
            {
                _openPending = false;
                _opened = true;
                Log($"Opened {_width}x{_height} audio={HasAudio} length={_lengthTicks}");
                Opened?.Invoke();
            }

            SyncClock();
            PresentDueFrame();

            // Ended once the reader is out of samples AND everything already decoded has been shown
            // and heard -- otherwise MediaEnded would fire while the last second is still playing.
            if (_opened && !_ended && _decodeEnded && _rate != 0)
            {
                bool framesDrained;
                lock (_lock) { framesDrained = _pending.Count == 0; }
                if (framesDrained && AudioDrained())
                {
                    _ended = true;
                    Log("Ended");
                    Ended?.Invoke();
                }
            }
        }

        private void PresentDueFrame()
        {
            long now = NowTicks();
            Frame due = null;

            lock (_lock)
            {
                while (_pending.Count > 0 && _pending.Peek().Ticks <= now)
                {
                    if (due != null) _free.Push(due);   // we are behind; skip straight to the newest due frame
                    due = _pending.Dequeue();
                }

                // Nothing due yet and nothing on screen: show the first frame anyway, so a MediaElement
                // that was opened but not played displays its poster frame instead of staying blank.
                if (due == null && _current == null && _pending.Count > 0) due = _pending.Dequeue();

                if (due != null)
                {
                    if (_current != null && !_frameLocked) _free.Push(_current);
                    _current = due;
                }
            }

            if (due != null)
            {
                if (s_log && _framesPresented++ % 60 == 0) Log($"present #{_framesPresented - 1} pts={due.Ticks / (double)TimeSpan.TicksPerSecond:F2}s now={now / (double)TimeSpan.TicksPerSecond:F2}s");
                FrameAvailable?.Invoke();
            }
        }

        // ------------------------------------------------------------ decode thread ----

        private void StopDecodeThread()
        {
            Thread t = _thread;
            if (t == null) return;
            _stop = true;
            _thread = null;
            if (!t.Join(2000)) Log("decode thread did not stop in time");
        }

        private void DecodeLoop()
        {
            try
            {
                while (!_stop)
                {
                    RecycleAudioHeaders();

                    if (_decodeEnded) { Thread.Sleep(15); continue; }

                    if (!WantMoreData()) { Thread.Sleep(3); continue; }

                    int hr = ReadSample(_reader, unchecked((uint)MF_SOURCE_READER_ANY_STREAM), 0,
                                        out uint actualStream, out uint flags, out long timestamp, out IntPtr sample);
                    if (hr < 0) throw MediaError("Media Foundation could not read the next sample.", hr);

                    try
                    {
                        if ((flags & MF_SOURCE_READERF_ERROR) != 0)
                        {
                            throw new InvalidOperationException("Media Foundation reported a stream error.");
                        }

                        if ((flags & MF_SOURCE_READERF_ENDOFSTREAM) != 0)
                        {
                            FlushPartialAudio();
                            _decodeEnded = true;
                            continue;
                        }

                        if ((flags & MF_SOURCE_READERF_CURRENTMEDIATYPECHANGED) != 0 &&
                            actualStream == (uint)_videoStream)
                        {
                            ReadVideoFormat();
                        }

                        // A stream tick (a gap) carries no sample; so does a stream we did not select.
                        if (sample == IntPtr.Zero) continue;

                        if (actualStream == (uint)_videoStream) TakeVideoSample(sample, timestamp);
                        else if (actualStream == (uint)_audioStream) TakeAudioSample(sample);
                    }
                    finally
                    {
                        if (sample != IntPtr.Zero) Release(sample);
                    }
                }
            }
            catch (Exception e)
            {
                Log("decode thread failed: " + e.Message);
                _failure = e;
            }
        }

        /// <summary>
        /// Back-pressure: how far the decoder is allowed to run ahead of the presentation clock.
        /// <para>
        /// This is the pacing rule, and it has to be a TIME budget rather than a frame count. Gating on
        /// the queue being full and dropping the oldest frame to make room looks equivalent and is not:
        /// video decodes far faster than audio plays, so the queue instantly became a sliding window of
        /// frames from the future, none of which was ever due, and playback froze on frame one while the
        /// clock and the audio ran on happily.
        /// </para>
        /// <para>
        /// The lead must also stay comfortably under how much audio the device holds (~1s, see
        /// OpenAudioDevice), so that pausing the reader on this gate can never starve the audio device.
        /// Starving it would stop the audio clock, and a stopped clock means no frame ever becomes due
        /// -- the deadlock this ordering exists to prevent.
        /// </para>
        /// </summary>
        private static readonly long LeadTicks = TimeSpan.TicksPerMillisecond * 400;

        private long _newestQueuedTicks = -1;

        private bool WantMoreData()
        {
            // Audio device about to run dry: keep reading no matter how far ahead the video is. Silence
            // is the one outcome worse than a dropped frame, and the video enqueue drops its oldest to
            // make room -- a frame the clock has almost certainly passed already.
            if (_audioStream >= 0 && BusyAudioHeaderCount() <= 1) return true;

            // Audio sink full: nowhere to put the next audio sample.
            if (_audioStream >= 0 && FreeAudioHeaderCount() == 0) return false;

            lock (_lock)
            {
                if (_pending.Count >= MaxPendingFrames) return false;
            }

            // Far enough ahead of the clock; let presentation catch up.
            if (_videoStream >= 0 && _newestQueuedTicks >= 0 && _newestQueuedTicks - NowTicks() > LeadTicks)
            {
                return false;
            }

            return true;
        }

        private void TakeVideoSample(IntPtr sample, long timestamp)
        {
            if (_width <= 0 || _height <= 0) return;

            int hr = ConvertToContiguousBuffer(sample, out IntPtr buffer);
            if (hr < 0 || buffer == IntPtr.Zero) return;

            try
            {
                hr = BufferLock(buffer, out byte* src, out _, out uint length);
                if (hr < 0 || src == null) return;

                try
                {
                    int abs = Math.Abs(_stride);
                    int rowBytes = _width * 4;
                    if (abs < rowBytes || (long)abs * _height > length) return;   // not the frame we asked for

                    Frame f = RentFrame(rowBytes * _height);
                    f.Ticks = timestamp;
                    f.Width = _width;
                    f.Height = _height;
                    f.RowBytes = rowBytes;

                    // A negative default stride means the buffer is bottom-up: its first byte is the
                    // BOTTOM row, and the top row lives at the far end. WPF wants top-down, so walk the
                    // source backwards. Row-at-a-time either way, because abs may exceed rowBytes.
                    //
                    // The alpha byte is forced opaque as we go. MFVideoFormat_RGB32 is D3DFMT_X8R8G8B8:
                    // B,G,R,X in memory, where X is UNDEFINED and in practice zero. Copied verbatim into
                    // a Bgra32 frame that reads as fully transparent, and the compositor drew the video
                    // rectangle as a black box -- right geometry, no picture. Video is opaque, so OR the
                    // alpha in during the same pass rather than copying twice.
                    byte* s = src + (_stride < 0 ? (long)(_height - 1) * abs : 0);
                    long step = _stride < 0 ? -abs : abs;
                    byte* d = (byte*)f.Pixels;
                    for (int y = 0; y < _height; y++)
                    {
                        uint* srcRow = (uint*)s;
                        uint* dstRow = (uint*)d;
                        for (int x = 0; x < _width; x++)
                        {
                            dstRow[x] = srcRow[x] | 0xFF000000u;
                        }

                        s += step;
                        d += rowBytes;
                    }

                    if (s_log && _framesDecoded++ % 60 == 0)
                    {
                        uint* px = (uint*)f.Pixels;
                        long sum = 0;
                        int n = _width * _height;
                        for (int i = 0; i < n; i += 97) { sum += (px[i] & 0xFF) + ((px[i] >> 8) & 0xFF) + ((px[i] >> 16) & 0xFF); }
                        Log($"frame #{_framesDecoded - 1} pts={timestamp / (double)TimeSpan.TicksPerSecond:F2}s " +
                            $"px0=0x{px[0]:X8} pxMid=0x{px[n / 2]:X8} avgRGB={sum / (double)(n / 97 + 1) / 3:F1}");
                    }

                    lock (_lock)
                    {
                        // Safety valve only -- WantMoreData normally stops us long before this. Dropping
                        // the OLDEST is right here: it is the one the clock has most likely already
                        // passed, so it was never going to be shown anyway.
                        if (_pending.Count >= MaxPendingFrames) _free.Push(_pending.Dequeue());
                        _pending.Enqueue(f);
                        _newestQueuedTicks = timestamp;
                    }
                }
                finally
                {
                    BufferUnlock(buffer);
                }
            }
            finally
            {
                Release(buffer);
            }
        }

        private Frame RentFrame(int bytes)
        {
            lock (_lock)
            {
                while (_free.Count > 0)
                {
                    Frame f = _free.Pop();
                    if (f.Capacity >= bytes) return f;
                    FreeFrame(f);            // stale size (the stream changed resolution)
                }
            }

            return new Frame
            {
                Pixels = Marshal.AllocHGlobal(bytes),
                Capacity = bytes,
            };
        }

        private static void FreeFrame(Frame f)
        {
            if (f == null || f.Pixels == IntPtr.Zero) return;
            Marshal.FreeHGlobal(f.Pixels);
            f.Pixels = IntPtr.Zero;
        }

        // ------------------------------------------------------------------- audio ----

        private void TakeAudioSample(IntPtr sample)
        {
            if (_waveOut == IntPtr.Zero) return;

            // Off-speed playback would need resampling to stay in tune, which is more machinery than
            // it is worth here; the video clock takes over and audio simply goes quiet until 1x.
            if (_rate != 0 && _rate != 1.0) return;

            int hr = ConvertToContiguousBuffer(sample, out IntPtr buffer);
            if (hr < 0 || buffer == IntPtr.Zero) return;

            try
            {
                hr = BufferLock(buffer, out byte* src, out _, out uint length);
                if (hr < 0 || src == null) return;

                try
                {
                    int offset = 0;
                    while (offset < (int)length && !_stop)
                    {
                        if (_fillIndex < 0)
                        {
                            _fillIndex = TakeFreeAudioHeader();
                            if (_fillIndex < 0) { Thread.Sleep(2); continue; }
                            _fillUsed = 0;
                        }

                        int room = _audioBufferBytes - _fillUsed;
                        int take = Math.Min(room, (int)length - offset);
                        Buffer.MemoryCopy(src + offset, (byte*)_headerData[_fillIndex] + _fillUsed, room, take);
                        _fillUsed += take;
                        offset += take;

                        // Normally submit only full buffers, but hand over a partial one rather than let
                        // the device fall silent -- notably at startup, where waiting to fill 100ms
                        // before the first submission leaves the audio clock reading zero.
                        if (_fillUsed == _audioBufferBytes || BusyAudioHeaderCount() == 0) SubmitFillBuffer();
                    }
                }
                finally
                {
                    BufferUnlock(buffer);
                }
            }
            finally
            {
                Release(buffer);
            }
        }

        private void FlushPartialAudio()
        {
            if (_fillIndex >= 0 && _fillUsed > 0) SubmitFillBuffer();
        }

        private void SubmitFillBuffer()
        {
            int i = _fillIndex;
            if (i < 0) return;
            _fillIndex = -1;

            IntPtr hdr = _headers[i];
            Marshal.WriteInt32(hdr, WaveHdrBufferLengthOffset, _fillUsed);
            Marshal.WriteInt32(hdr, WaveHdrFlagsOffset, 0);

            if (waveOutPrepareHeader(_waveOut, hdr, (uint)WaveHdrSize) != 0) return;
            if (waveOutWrite(_waveOut, hdr, (uint)WaveHdrSize) != 0)
            {
                waveOutUnprepareHeader(_waveOut, hdr, (uint)WaveHdrSize);
                return;
            }
            _headerBusy[i] = true;
        }

        private bool[] _headerBusy;

        /// <summary>
        /// Returns finished buffers to the pool. waveOut is opened with CALLBACK_NULL -- no callback
        /// thread, no reentrancy -- so completion is observed by polling WHDR_DONE from the one thread
        /// that submits.
        /// </summary>
        private void RecycleAudioHeaders()
        {
            if (_waveOut == IntPtr.Zero || _headers == null) return;
            for (int i = 0; i < _headers.Length; i++)
            {
                if (!_headerBusy[i]) continue;
                int flags = Marshal.ReadInt32(_headers[i], WaveHdrFlagsOffset);
                if ((flags & WHDR_DONE) == 0) continue;
                waveOutUnprepareHeader(_waveOut, _headers[i], (uint)WaveHdrSize);
                _headerBusy[i] = false;
            }
        }

        private int TakeFreeAudioHeader()
        {
            RecycleAudioHeaders();
            for (int i = 0; i < _headers.Length; i++)
            {
                if (!_headerBusy[i]) return i;
            }
            return -1;
        }

        /// <summary>How many buffers the device still has to play. Zero means it is about to fall silent.</summary>
        private int BusyAudioHeaderCount()
        {
            if (_headers == null) return 0;
            RecycleAudioHeaders();
            int n = 0;
            for (int i = 0; i < _headers.Length; i++)
            {
                if (_headerBusy[i]) n++;
            }
            return n;
        }

        private int FreeAudioHeaderCount()
        {
            if (_headers == null) return 0;
            RecycleAudioHeaders();
            int n = 0;
            for (int i = 0; i < _headers.Length; i++)
            {
                if (!_headerBusy[i]) n++;
            }
            return n;
        }

        private bool AudioDrained()
        {
            if (_waveOut == IntPtr.Zero || _headers == null) return true;
            for (int i = 0; i < _headers.Length; i++)
            {
                if (_headerBusy[i] && (Marshal.ReadInt32(_headers[i], WaveHdrFlagsOffset) & WHDR_DONE) == 0)
                {
                    return false;
                }
            }
            return true;
        }

        private void ResetAudioHeaders()
        {
            if (_headers == null) return;
            for (int i = 0; i < _headers.Length; i++)
            {
                if (!_headerBusy[i]) continue;
                waveOutUnprepareHeader(_waveOut, _headers[i], (uint)WaveHdrSize);
                _headerBusy[i] = false;
            }
            _fillIndex = -1;
            _fillUsed = 0;
        }

        private void OpenAudioDevice()
        {
            if (_audioStream < 0) return;

            _audioBufferBytes = Math.Max(4096, (int)_waveFormat.nAvgBytesPerSec / 10);   // ~100ms per buffer
            _audioBufferBytes -= _audioBufferBytes % Math.Max(1, (int)_waveFormat.nBlockAlign);

            WaveFormatEx fmt = _waveFormat;
            int mmr = waveOutOpen(out IntPtr hwo, WAVE_MAPPER, ref fmt, IntPtr.Zero, IntPtr.Zero, CALLBACK_NULL);
            if (mmr != 0)
            {
                // No usable output device is not a reason to fail the media: video still plays.
                Log($"waveOutOpen failed ({mmr}); continuing without audio");
                _audioStream = -1;
                return;
            }

            _waveOut = hwo;
            _headers = new IntPtr[AudioBufferCount];
            _headerData = new IntPtr[AudioBufferCount];
            _headerBusy = new bool[AudioBufferCount];
            for (int i = 0; i < AudioBufferCount; i++)
            {
                _headers[i] = Marshal.AllocHGlobal(WaveHdrSize);
                _headerData[i] = Marshal.AllocHGlobal(_audioBufferBytes);
                for (int b = 0; b < WaveHdrSize; b += 4) Marshal.WriteInt32(_headers[i], b, 0);
                Marshal.WriteIntPtr(_headers[i], WaveHdrDataOffset, _headerData[i]);
            }

            // waveOut starts running; the decode thread has not queued anything yet, and playback is
            // paused until SetRate anyway.
            waveOutPause(_waveOut);
            ApplyVolume();
        }

        private void CloseAudioDevice()
        {
            if (_waveOut != IntPtr.Zero)
            {
                waveOutReset(_waveOut);
                ResetAudioHeaders();
                waveOutClose(_waveOut);
                _waveOut = IntPtr.Zero;
            }

            if (_headers != null)
            {
                for (int i = 0; i < _headers.Length; i++)
                {
                    if (_headers[i] != IntPtr.Zero) Marshal.FreeHGlobal(_headers[i]);
                    if (_headerData[i] != IntPtr.Zero) Marshal.FreeHGlobal(_headerData[i]);
                }
                _headers = null;
                _headerData = null;
                _headerBusy = null;
            }

            _fillIndex = -1;
            _fillUsed = 0;
        }

        public void Dispose() => Close();

        // =============================================================== MF set-up ====

        private static bool s_started;
        private static readonly object s_startLock = new object();

        private static void EnsureStarted()
        {
            if (s_started) return;
            lock (s_startLock)
            {
                if (s_started) return;
                int hr = MFStartup(MF_VERSION, MFSTARTUP_LITE);
                if (hr < 0) throw MediaError("Media Foundation is not available on this system.", hr);
                s_started = true;
            }
        }

        private void CreateReader(string url)
        {
            IntPtr attributes = IntPtr.Zero;
            try
            {
                int hr = MFCreateAttributes(out attributes, 2);
                if (hr < 0) throw MediaError("Could not create Media Foundation attributes.", hr);

                // Lets SetCurrentMediaType ask for RGB32 on a stream the decoder emits as NV12/YUY2/... :
                // the reader splices in a converter rather than refusing the type.
                Guid g = MF_SOURCE_READER_ENABLE_VIDEO_PROCESSING;
                SetUINT32(attributes, ref g, 1);
                // Software output. DXVA would hand back a D3D surface that this path cannot map.
                g = MF_SOURCE_READER_DISABLE_DXVA;
                SetUINT32(attributes, ref g, 1);

                hr = MFCreateSourceReaderFromURL(url, attributes, out _reader);
                if (hr < 0) throw MediaError($"Media Foundation could not open '{url}'.", hr);
            }
            finally
            {
                if (attributes != IntPtr.Zero) Release(attributes);
            }
        }

        private void ConfigureStreams()
        {
            // Which stream is which. Reading ANY_STREAM reports the REAL index, so the FIRST_VIDEO /
            // FIRST_AUDIO sentinels cannot be used to recognise samples later -- find the indices now.
            for (uint i = 0; i < 64; i++)
            {
                int hr = GetNativeMediaType(_reader, i, 0, out IntPtr native);
                if (hr == MF_E_INVALIDSTREAMNUMBER) break;
                if (hr < 0 || native == IntPtr.Zero) continue;

                try
                {
                    Guid key = MF_MT_MAJOR_TYPE;
                    if (GetGUID(native, ref key, out Guid major) < 0) continue;
                    if (major == MFMediaType_Video && _videoStream < 0) _videoStream = (int)i;
                    else if (major == MFMediaType_Audio && _audioStream < 0) _audioStream = (int)i;
                }
                finally
                {
                    Release(native);
                }
            }

            if (_videoStream < 0 && _audioStream < 0)
            {
                throw new NotSupportedException("The media contains no playable audio or video stream.");
            }

            SetStreamSelection(_reader, unchecked((uint)MF_SOURCE_READER_ALL_STREAMS), 0);
            if (_videoStream >= 0) SetStreamSelection(_reader, (uint)_videoStream, 1);
            if (_audioStream >= 0) SetStreamSelection(_reader, (uint)_audioStream, 1);

            if (_videoStream >= 0 && !RequestVideoFormat()) _videoStream = -1;
            if (_audioStream >= 0 && !RequestAudioFormat()) _audioStream = -1;

            _lengthTicks = ReadDuration();
        }

        private bool RequestVideoFormat()
        {
            IntPtr type = IntPtr.Zero;
            try
            {
                if (MFCreateMediaType(out type) < 0) return false;
                Guid k = MF_MT_MAJOR_TYPE, v = MFMediaType_Video;
                if (SetGUID(type, ref k, ref v) < 0) return false;
                k = MF_MT_SUBTYPE; v = MFVideoFormat_RGB32;
                if (SetGUID(type, ref k, ref v) < 0) return false;

                if (SetCurrentMediaType(_reader, (uint)_videoStream, IntPtr.Zero, type) < 0)
                {
                    Log("the video stream could not be converted to RGB32");
                    return false;
                }
            }
            finally
            {
                if (type != IntPtr.Zero) Release(type);
            }

            return ReadVideoFormat();
        }

        private bool ReadVideoFormat()
        {
            if (GetCurrentMediaType(_reader, (uint)_videoStream, out IntPtr type) < 0 || type == IntPtr.Zero)
            {
                return false;
            }

            try
            {
                Guid k = MF_MT_FRAME_SIZE;
                if (GetUINT64(type, ref k, out ulong packed) < 0) return false;
                _width = (int)(packed >> 32);
                _height = (int)(packed & 0xFFFFFFFF);
                if (_width <= 0 || _height <= 0) return false;

                // MF_MT_DEFAULT_STRIDE is a signed value carried in a UINT32 slot; negative means the
                // buffer is bottom-up. Absent, ask the platform what the stride for this format is.
                k = MF_MT_DEFAULT_STRIDE;
                if (GetUINT32(type, ref k, out uint raw) >= 0) _stride = unchecked((int)raw);
                else if (MFGetStrideForBitmapInfoHeader(D3DFMT_X8R8G8B8, (uint)_width, out int s) >= 0) _stride = s;
                else _stride = _width * 4;

                Log($"video {_width}x{_height} stride={_stride}");
                return true;
            }
            finally
            {
                Release(type);
            }
        }

        private bool RequestAudioFormat()
        {
            IntPtr type = IntPtr.Zero;
            try
            {
                if (MFCreateMediaType(out type) < 0) return false;
                Guid k = MF_MT_MAJOR_TYPE, v = MFMediaType_Audio;
                if (SetGUID(type, ref k, ref v) < 0) return false;
                k = MF_MT_SUBTYPE; v = MFAudioFormat_PCM;
                if (SetGUID(type, ref k, ref v) < 0) return false;
                // 16-bit is what waveOut is universally happy with; left to itself the reader may pick a
                // width the device would reject.
                k = MF_MT_AUDIO_BITS_PER_SAMPLE;
                if (SetUINT32(type, ref k, 16) < 0) return false;

                if (SetCurrentMediaType(_reader, (uint)_audioStream, IntPtr.Zero, type) < 0)
                {
                    Log("the audio stream could not be converted to 16-bit PCM");
                    return false;
                }
            }
            finally
            {
                if (type != IntPtr.Zero) Release(type);
            }

            if (GetCurrentMediaType(_reader, (uint)_audioStream, out IntPtr actual) < 0 || actual == IntPtr.Zero)
            {
                return false;
            }

            try
            {
                if (MFCreateWaveFormatExFromMFMediaType(actual, out IntPtr pwfx, out _, 0) < 0 || pwfx == IntPtr.Zero)
                {
                    return false;
                }

                try
                {
                    _waveFormat = *(WaveFormatEx*)pwfx;
                    _waveFormat.cbSize = 0;   // we pass the 18-byte form; no extra bytes travel with it
                }
                finally
                {
                    CoTaskMemFree(pwfx);
                }
            }
            finally
            {
                Release(actual);
            }

            Log($"audio {_waveFormat.nChannels}ch {_waveFormat.nSamplesPerSec}Hz {_waveFormat.wBitsPerSample}bit");
            return _waveFormat.nSamplesPerSec > 0 && _waveFormat.nBlockAlign > 0;
        }

        private long ReadDuration()
        {
            byte* pv = stackalloc byte[PropVariantSize];
            for (int i = 0; i < PropVariantSize; i++) pv[i] = 0;

            Guid key = MF_PD_DURATION;
            int hr = GetPresentationAttribute(_reader, unchecked((uint)MF_SOURCE_READER_MEDIASOURCE), ref key, pv);
            if (hr < 0) return 0;

            try
            {
                // VT_UI8: the value sits after vt (2 bytes) plus 6 reserved bytes. Duration is already
                // in 100ns units, the same unit as TimeSpan ticks.
                ushort vt = *(ushort*)pv;
                if (vt != VT_UI8 && vt != VT_I8) return 0;
                return (long)*(ulong*)(pv + 8);
            }
            finally
            {
                PropVariantClear(pv);
            }
        }

        private void SetPosition(IntPtr reader, long ticks)
        {
            byte* pv = stackalloc byte[PropVariantSize];
            for (int i = 0; i < PropVariantSize; i++) pv[i] = 0;
            *(ushort*)pv = VT_I8;
            *(long*)(pv + 8) = ticks;

            Guid timeFormat = Guid.Empty;   // GUID_NULL == 100-nanosecond units
            int hr = SetCurrentPosition(reader, ref timeFormat, pv);
            if (hr < 0) Log($"seek failed 0x{hr:X8}");
        }

        private static Exception MediaError(string message, int hr) =>
            new InvalidOperationException($"{message} (HRESULT 0x{hr:X8})");

        // ================================================================ interop ====
        //
        // Vtable slots come from the CINTERFACE vtable structs in the Windows SDK headers:
        // IMFSourceReader (mfreadwrite.h), IMFAttributes/IMFMediaType/IMFSample/IMFMediaBuffer
        // (mfobjects.h). IMFMediaType and IMFSample both derive from IMFAttributes, so the attribute
        // accessors below work on any of them.

        private static void** Vtbl(IntPtr obj) => *(void***)obj;

        private static uint Release(IntPtr o) =>
            o == IntPtr.Zero ? 0 : ((delegate* unmanaged[Stdcall]<IntPtr, uint>)Vtbl(o)[2])(o);

        // ---- IMFAttributes (shared by IMFMediaType and IMFSample) ----
        private static int GetUINT32(IntPtr o, ref Guid key, out uint value)
        {
            fixed (Guid* k = &key)
            fixed (uint* v = &value)
            {
                return ((delegate* unmanaged[Stdcall]<IntPtr, Guid*, uint*, int>)Vtbl(o)[7])(o, k, v);
            }
        }

        private static int GetUINT64(IntPtr o, ref Guid key, out ulong value)
        {
            fixed (Guid* k = &key)
            fixed (ulong* v = &value)
            {
                return ((delegate* unmanaged[Stdcall]<IntPtr, Guid*, ulong*, int>)Vtbl(o)[8])(o, k, v);
            }
        }

        private static int GetGUID(IntPtr o, ref Guid key, out Guid value)
        {
            fixed (Guid* k = &key)
            fixed (Guid* v = &value)
            {
                return ((delegate* unmanaged[Stdcall]<IntPtr, Guid*, Guid*, int>)Vtbl(o)[10])(o, k, v);
            }
        }

        private static int SetUINT32(IntPtr o, ref Guid key, uint value)
        {
            fixed (Guid* k = &key)
            {
                return ((delegate* unmanaged[Stdcall]<IntPtr, Guid*, uint, int>)Vtbl(o)[21])(o, k, value);
            }
        }

        private static int SetGUID(IntPtr o, ref Guid key, ref Guid value)
        {
            fixed (Guid* k = &key)
            fixed (Guid* v = &value)
            {
                return ((delegate* unmanaged[Stdcall]<IntPtr, Guid*, Guid*, int>)Vtbl(o)[24])(o, k, v);
            }
        }

        // ---- IMFSourceReader ----
        private static int SetStreamSelection(IntPtr r, uint stream, int selected) =>
            ((delegate* unmanaged[Stdcall]<IntPtr, uint, int, int>)Vtbl(r)[4])(r, stream, selected);

        private static int GetNativeMediaType(IntPtr r, uint stream, uint index, out IntPtr type)
        {
            fixed (IntPtr* t = &type)
            {
                return ((delegate* unmanaged[Stdcall]<IntPtr, uint, uint, IntPtr*, int>)Vtbl(r)[5])(r, stream, index, t);
            }
        }

        private static int GetCurrentMediaType(IntPtr r, uint stream, out IntPtr type)
        {
            fixed (IntPtr* t = &type)
            {
                return ((delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr*, int>)Vtbl(r)[6])(r, stream, t);
            }
        }

        private static int SetCurrentMediaType(IntPtr r, uint stream, IntPtr reserved, IntPtr type) =>
            ((delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr, IntPtr, int>)Vtbl(r)[7])(r, stream, reserved, type);

        private static int SetCurrentPosition(IntPtr r, ref Guid timeFormat, byte* position)
        {
            fixed (Guid* g = &timeFormat)
            {
                return ((delegate* unmanaged[Stdcall]<IntPtr, Guid*, byte*, int>)Vtbl(r)[8])(r, g, position);
            }
        }

        private static int ReadSample(IntPtr r, uint stream, uint controlFlags,
                                      out uint actualStream, out uint streamFlags, out long timestamp, out IntPtr sample)
        {
            fixed (uint* a = &actualStream)
            fixed (uint* f = &streamFlags)
            fixed (long* t = &timestamp)
            fixed (IntPtr* s = &sample)
            {
                return ((delegate* unmanaged[Stdcall]<IntPtr, uint, uint, uint*, uint*, long*, IntPtr*, int>)Vtbl(r)[9])
                    (r, stream, controlFlags, a, f, t, s);
            }
        }

        private static int Flush(IntPtr r, uint stream) =>
            ((delegate* unmanaged[Stdcall]<IntPtr, uint, int>)Vtbl(r)[10])(r, stream);

        private static int GetPresentationAttribute(IntPtr r, uint stream, ref Guid key, byte* value)
        {
            fixed (Guid* k = &key)
            {
                return ((delegate* unmanaged[Stdcall]<IntPtr, uint, Guid*, byte*, int>)Vtbl(r)[12])(r, stream, k, value);
            }
        }

        // ---- IMFSample / IMFMediaBuffer ----
        private static int ConvertToContiguousBuffer(IntPtr sample, out IntPtr buffer)
        {
            fixed (IntPtr* b = &buffer)
            {
                return ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, int>)Vtbl(sample)[41])(sample, b);
            }
        }

        private static int BufferLock(IntPtr buffer, out byte* data, out uint maxLength, out uint currentLength)
        {
            fixed (byte** d = &data)
            fixed (uint* m = &maxLength)
            fixed (uint* c = &currentLength)
            {
                return ((delegate* unmanaged[Stdcall]<IntPtr, byte**, uint*, uint*, int>)Vtbl(buffer)[3])(buffer, d, m, c);
            }
        }

        private static int BufferUnlock(IntPtr buffer) =>
            ((delegate* unmanaged[Stdcall]<IntPtr, int>)Vtbl(buffer)[4])(buffer);

        // ---- flat exports ----
        private const string MfPlat = "mfplat.dll";
        private const string MfReadWrite = "mfreadwrite.dll";
        private const string Ole32 = "ole32.dll";
        private const string WinMM = "winmm.dll";

        [DllImport(MfPlat)] private static extern int MFStartup(uint version, uint flags);
        [DllImport(MfPlat)] private static extern int MFCreateAttributes(out IntPtr attributes, uint initialSize);
        [DllImport(MfPlat)] private static extern int MFCreateMediaType(out IntPtr type);
        [DllImport(MfPlat)] private static extern int MFCreateWaveFormatExFromMFMediaType(IntPtr type, out IntPtr wfx, out uint size, uint flags);
        [DllImport(MfPlat)] private static extern int MFGetStrideForBitmapInfoHeader(uint format, uint width, out int stride);

        [DllImport(MfReadWrite, CharSet = CharSet.Unicode)]
        private static extern int MFCreateSourceReaderFromURL(string url, IntPtr attributes, out IntPtr reader);

        [DllImport(Ole32)] private static extern void CoTaskMemFree(IntPtr p);
        [DllImport(Ole32)] private static extern int PropVariantClear(byte* pv);

        [DllImport(WinMM)] private static extern int waveOutOpen(out IntPtr hwo, uint device, ref WaveFormatEx format, IntPtr callback, IntPtr instance, uint flags);
        [DllImport(WinMM)] private static extern int waveOutClose(IntPtr hwo);
        [DllImport(WinMM)] private static extern int waveOutPrepareHeader(IntPtr hwo, IntPtr header, uint size);
        [DllImport(WinMM)] private static extern int waveOutUnprepareHeader(IntPtr hwo, IntPtr header, uint size);
        [DllImport(WinMM)] private static extern int waveOutWrite(IntPtr hwo, IntPtr header, uint size);
        [DllImport(WinMM)] private static extern int waveOutPause(IntPtr hwo);
        [DllImport(WinMM)] private static extern int waveOutRestart(IntPtr hwo);
        [DllImport(WinMM)] private static extern int waveOutReset(IntPtr hwo);
        [DllImport(WinMM)] private static extern int waveOutSetVolume(IntPtr hwo, uint volume);
        [DllImport(WinMM)] private static extern int waveOutGetPosition(IntPtr hwo, ref MMTIME time, uint size);

        // ---- constants ----
        private const uint MF_VERSION = 0x00020070;          // MF_SDK_VERSION 0x0002 << 16 | MF_API_VERSION 0x0070
        private const uint MFSTARTUP_LITE = 0x1;             // no sockets; we never play from a network source
        private const int MF_E_INVALIDSTREAMNUMBER = unchecked((int)0xC00D36B3);

        private const int MF_SOURCE_READER_ALL_STREAMS = unchecked((int)0xFFFFFFFE);
        private const int MF_SOURCE_READER_ANY_STREAM = unchecked((int)0xFFFFFFFE);
        private const int MF_SOURCE_READER_MEDIASOURCE = unchecked((int)0xFFFFFFFF);

        private const uint MF_SOURCE_READERF_ERROR = 0x1;
        private const uint MF_SOURCE_READERF_ENDOFSTREAM = 0x2;
        private const uint MF_SOURCE_READERF_CURRENTMEDIATYPECHANGED = 0x20;

        private const uint D3DFMT_X8R8G8B8 = 22;
        private const ushort VT_I8 = 20;
        private const ushort VT_UI8 = 21;
        private const int PropVariantSize = 24;

        private const uint WAVE_MAPPER = 0xFFFFFFFF;
        private const uint CALLBACK_NULL = 0;
        private const int WHDR_DONE = 0x1;
        private const uint TIME_MS = 0x1;
        private const uint TIME_SAMPLES = 0x2;
        private const uint TIME_BYTES = 0x4;

        // WAVEHDR on 64-bit: lpData(0) dwBufferLength(8) dwBytesRecorded(12) dwUser(16) dwFlags(24)
        // dwLoops(28) lpNext(32) reserved(40).
        private const int WaveHdrSize = 48;
        private const int WaveHdrDataOffset = 0;
        private const int WaveHdrBufferLengthOffset = 8;
        private const int WaveHdrFlagsOffset = 24;

        private static readonly Guid MFMediaType_Video = new Guid(0x73646976, 0x0000, 0x0010, 0x80, 0x00, 0x00, 0xAA, 0x00, 0x38, 0x9B, 0x71);
        private static readonly Guid MFMediaType_Audio = new Guid(0x73647561, 0x0000, 0x0010, 0x80, 0x00, 0x00, 0xAA, 0x00, 0x38, 0x9B, 0x71);
        private static readonly Guid MFVideoFormat_RGB32 = new Guid(0x00000016, 0x0000, 0x0010, 0x80, 0x00, 0x00, 0xAA, 0x00, 0x38, 0x9B, 0x71);
        private static readonly Guid MFAudioFormat_PCM = new Guid(0x00000001, 0x0000, 0x0010, 0x80, 0x00, 0x00, 0xAA, 0x00, 0x38, 0x9B, 0x71);

        private static readonly Guid MF_MT_MAJOR_TYPE = new Guid(0x48eba18e, 0xf8c9, 0x4687, 0xbf, 0x11, 0x0a, 0x74, 0xc9, 0xf9, 0x6a, 0x8f);
        private static readonly Guid MF_MT_SUBTYPE = new Guid(0xf7e34c9a, 0x42e8, 0x4714, 0xb7, 0x4b, 0xcb, 0x29, 0xd7, 0x2c, 0x35, 0xe5);
        private static readonly Guid MF_MT_FRAME_SIZE = new Guid(0x1652c33d, 0xd6b2, 0x4012, 0xb8, 0x34, 0x72, 0x03, 0x08, 0x49, 0xa3, 0x7d);
        private static readonly Guid MF_MT_DEFAULT_STRIDE = new Guid(0x644b4e48, 0x1e02, 0x4516, 0xb0, 0xeb, 0xc0, 0x1c, 0xa9, 0xd4, 0x9a, 0xc6);
        private static readonly Guid MF_MT_AUDIO_BITS_PER_SAMPLE = new Guid(0xf2deb57f, 0x40fa, 0x4764, 0xaa, 0x33, 0xed, 0x4f, 0x2d, 0x1f, 0xf6, 0x69);
        private static readonly Guid MF_PD_DURATION = new Guid(0x6c990d33, 0xbb8e, 0x477a, 0x85, 0x98, 0x0d, 0x5d, 0x96, 0xfc, 0xd8, 0x8a);
        private static readonly Guid MF_SOURCE_READER_ENABLE_VIDEO_PROCESSING = new Guid(0xfb394f3d, 0xccf1, 0x42ee, 0xbb, 0xb3, 0xf9, 0xb8, 0x45, 0xd5, 0x68, 0x1d);
        private static readonly Guid MF_SOURCE_READER_DISABLE_DXVA = new Guid(0xaa456cfd, 0x3943, 0x4a1e, 0xa7, 0x7d, 0x18, 0x38, 0xc0, 0xea, 0x2e, 0x35);

        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        private struct WaveFormatEx
        {
            public ushort wFormatTag;
            public ushort nChannels;
            public uint nSamplesPerSec;
            public uint nAvgBytesPerSec;
            public ushort nBlockAlign;
            public ushort wBitsPerSample;
            public ushort cbSize;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MMTIME
        {
            public uint wType;
            public uint u0;
            public uint u1;
        }
    }
}
