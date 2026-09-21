// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// AndroidMediaBackend -- the Android IMediaBackend, backed by the NDK media APIs and AAudio.
//
// Android is "Linux" to OperatingSystem, but it has no GStreamer, so LinuxMediaBackend cannot serve it.
// It gets its own engine, and that engine is shaped like WindowsMediaBackend rather than like the
// AVFoundation ones: nothing on this platform owns a decode thread or an A/V clock for us, so this file
// owns both.
//
//   demux    AMediaExtractor              (libmediandk.so)
//   decode   AMediaCodec, one per track   (libmediandk.so) -- hardware where the device has it
//   frames   AImageReader, YUV_420_888    (libmediandk.so)
//   audio    AAudio, PCM16 output stream  (libaaudio.so)
//
// NO JNI. Every call here is a plain C entry point in an NDK shared library, so the backend lives in
// PresentationCore beside its siblings instead of in the Android head payload the way AndroidHost.cs has
// to (that one needs the Mono.Android bindings; this one needs nothing but P/Invoke). That also keeps it
// AOT-safe and keeps MediaElement working in any Android head, not only the ones that opt in.
//
// Why the video path goes through an AImageReader. A decoder configured with no output surface hands
// back raw buffers whose layout is described only by the "color-format" integer, and the software
// decoders report COLOR_FormatYUV420Flexible for it -- which says the layout is flexible, not what it
// actually is. The NDK has no equivalent of Java's MediaCodec.getOutputImage() to ask. An AImageReader
// surface does: every acquired AImage reports its own per-plane row stride, pixel stride and crop
// rectangle, so the YUV->BGRA conversion below reads a described buffer rather than a guessed one. It
// costs one gralloc round trip and is the difference between "works on this device" and "works".
//
// Threading and the clock follow WindowsMediaBackend exactly, for the same reasons its header gives:
// a decode thread does the blocking work and fills a bounded, time-paced frame queue, a DispatcherTimer
// presents whatever is due and raises every observable event, and the presentation clock is a Stopwatch
// that is CORRECTED toward the audio device rather than read from it -- a clock that can stall is a
// clock the decoder can deadlock against. AAudio's frame counters play the role waveOutGetPosition does
// there.
//
// Volume and balance are applied in software, on the PCM16 samples on their way to the stream. AAudio
// has no volume control of its own (that is android.media.AudioTrack's, i.e. JNI), and doing the gain
// during the copy that has to happen anyway costs one multiply per sample.
//

using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading;
using System.Windows.Threading;

namespace System.Windows.Media
{
    [SupportedOSPlatform("android")]
    internal sealed unsafe class AndroidMediaBackend : IMediaBackend
    {
        private readonly Dispatcher _dispatcher;
        private DispatcherTimer _timer;

        // ---- demux / decode ----
        private IntPtr _extractor;
        private FileStream _file;               // keeps the fd handed to setDataSourceFd alive
        private IntPtr _videoCodec, _audioCodec;
        private int _videoTrack = -1, _audioTrack = -1;
        private int _width, _height;
        private long _lengthTicks;

        // ---- frame tap ----
        private IntPtr _reader;                 // AImageReader*
        private IntPtr _readerWindow;           // ANativeWindow* owned by the reader
        private long _lastReleasedTicks;        // pts of the most recently rendered output buffer

        // ---- decode thread ----
        private Thread _thread;
        private volatile bool _stop;
        private volatile bool _decodeEnded;
        private volatile Exception _failure;
        private bool _extractorEnded, _videoEnded, _audioEnded;
        private bool _videoEosQueued, _audioEosQueued;

        // ---- video frames ----
        private const int MaxPendingFrames = 8;
        private const int ReaderImages = 4;     // gralloc buffers the codec may run ahead into
        private readonly object _lock = new object();
        private readonly Queue<Frame> _pending = new Queue<Frame>();
        private readonly Stack<Frame> _free = new Stack<Frame>();
        private Frame _current;
        private bool _frameLocked;
        private long _newestQueuedTicks = -1;

        // ---- audio ----
        private IntPtr _aaudio;                 // AAudioStream*
        private int _sampleRate, _channels;
        private int _frameBytes;                // channels * sizeof(int16)
        private IntPtr _audioScratch;           // gain is applied into this, then written to the stream
        private int _audioScratchBytes;
        private int _audioPendingOffset, _audioPendingBytes;
        private long _framesReadBase;           // AAudio's counter at the last flush; see AudioPlayedTicks

        // ---- clock / transport ----
        private readonly System.Diagnostics.Stopwatch _sw = new System.Diagnostics.Stopwatch();
        private long _swBaseTicks;
        private long _audioBaseTicks;
        private long _pausedTicks;
        private double _rate;                   // 0 == paused
        private double _volume = 0.5;
        private double _balance;

        // ---- observable state (dispatcher thread) ----
        private bool _openPending;
        private bool _opened, _failed, _ended;

        private int _framesDecoded, _framesPresented, _audioSyncs, _pulls;

        private static readonly bool s_log = Environment.GetEnvironmentVariable("WPF_MEDIA_LOG") == "1";
        private static void Log(string m) { if (s_log) Console.WriteLine("MEDIA " + m); }

        internal AndroidMediaBackend(MediaPlayer player)
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
                CreateExtractor(url);
                SelectTracks();
                StartVideoCodec();
                StartAudioCodec();

                if (_videoTrack < 0 && _audioTrack < 0)
                {
                    throw new NotSupportedException("The media has no track this device can decode.");
                }
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

            if (_videoCodec != IntPtr.Zero) { AMediaCodec_stop(_videoCodec); AMediaCodec_delete(_videoCodec); _videoCodec = IntPtr.Zero; }
            if (_audioCodec != IntPtr.Zero) { AMediaCodec_stop(_audioCodec); AMediaCodec_delete(_audioCodec); _audioCodec = IntPtr.Zero; }

            // The reader owns its ANativeWindow; deleting it releases both. It must outlive the codec
            // that was rendering into it, which is why this comes second.
            if (_reader != IntPtr.Zero) { AImageReader_delete(_reader); _reader = IntPtr.Zero; }
            _readerWindow = IntPtr.Zero;

            if (_extractor != IntPtr.Zero) { AMediaExtractor_delete(_extractor); _extractor = IntPtr.Zero; }
            if (_file != null) { _file.Dispose(); _file = null; }

            lock (_lock)
            {
                UnlockFrame();
                while (_pending.Count > 0) FreeFrame(_pending.Dequeue());
                while (_free.Count > 0) FreeFrame(_free.Pop());
                if (_current != null) { FreeFrame(_current); _current = null; }
                _newestQueuedTicks = -1;
            }

            _videoTrack = _audioTrack = -1;
            _width = _height = 0;
            _lengthTicks = 0;
            _rate = 0;
            _opened = _failed = _ended = _openPending = false;
            _decodeEnded = _extractorEnded = _videoEnded = _audioEnded = false;
            _videoEosQueued = _audioEosQueued = false;
            _failure = null;
            _lastReleasedTicks = 0;
            _sw.Reset();
        }

        public void SetRate(double rate)
        {
            Log($"SetRate {rate}");
            if (_extractor == IntPtr.Zero) { _rate = rate; return; }

            double old = _rate;
            if (rate == 0)
            {
                _pausedTicks = NowTicks();
                _rate = 0;
                if (_aaudio != IntPtr.Zero) AAudioStream_requestPause(_aaudio);
                return;
            }

            // Whether audio can be the clock depends on the rate being exactly 1x, so a change that
            // crosses that boundary changes clock source. Re-seek instead of splicing two timelines:
            // it re-bases extractor, codecs, audio device and clock in one step.
            bool wasAudioMaster = AudioIsMaster(old);
            _rate = rate;
            if (AudioIsMaster(rate) != wasAudioMaster && old != 0)
            {
                SetPositionTicks(_pausedTicks != 0 && old == 0 ? _pausedTicks : NowTicksFor(old));
                return;
            }

            if (old == 0)
            {
                // Resuming: the stopwatch is the clock and it did not run while paused, so re-base it
                // onto the position we paused at. SyncClock pulls it onto the audio device afterwards.
                _swBaseTicks = _pausedTicks;
                _sw.Restart();
                if (_aaudio != IntPtr.Zero) AAudioStream_requestStart(_aaudio);
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
            if (_extractor == IntPtr.Zero) return;
            if (ticks < 0) ticks = 0;

            // The decode thread must not be inside the extractor or a codec while they are repositioned.
            bool wasRunning = _thread != null;
            StopDecodeThread();

            // CLOSEST_SYNC rather than PREVIOUS_SYNC: WPF's Position is a request to be AT that time,
            // and the decoder cannot start anywhere but a sync sample, so land on the nearest one
            // instead of always undershooting by up to a GOP.
            AMediaExtractor_seekTo(_extractor, ticks / 10, AMEDIAEXTRACTOR_SEEK_CLOSEST_SYNC);

            if (_videoCodec != IntPtr.Zero) AMediaCodec_flush(_videoCodec);
            if (_audioCodec != IntPtr.Zero) AMediaCodec_flush(_audioCodec);
            FlushAudioDevice();
            DrainReader();

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
            _extractorEnded = _videoEnded = _audioEnded = false;
            _videoEosQueued = _audioEosQueued = false;
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

        public void SetVolume(double volume) { _volume = volume; }
        public void SetBalance(double balance) { _balance = balance; }
        public void SetScrubbingEnabled(bool enabled) { /* frames are presented by the timer regardless. */ }
        public void NeedUIFrameUpdate() { /* frames are driven by the timer, not a per-pass reserve. */ }

        // ------------------------------------------------------------ state getters ----

        public bool IsBuffering => false;
        public bool CanPause => true;
        public double DownloadProgress => 1.0;
        public double BufferingProgress => 1.0;
        public int NaturalVideoWidth => _width;
        public int NaturalVideoHeight => _height;
        public bool HasAudio => _audioTrack >= 0;
        public bool HasVideo => _videoTrack >= 0 && _width > 0;
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

            // How often the compositor actually PULLS, and how old what it gets is. This is the one
            // measurement that separates "the backend is behind" from "the frame is current and the
            // screen is behind"; the two look identical on a phone and have nothing in common.
            if (s_log && _pulls++ % 60 == 0)
            {
                Log($"pull #{_pulls - 1} pts={f.Ticks / (double)TimeSpan.TicksPerSecond:F2}s now={NowTicks() / (double)TimeSpan.TicksPerSecond:F2}s");
            }

            _frameLocked = true;
            baseAddress = f.Pixels;
            width = f.Width;
            height = f.Height;
            rowBytes = f.RowBytes;
            return true;
        }

        public void UnlockFrame() => _frameLocked = false;

        // ------------------------------------------------------------------- clock ----

        private bool AudioIsMaster(double rate) => _aaudio != IntPtr.Zero && rate == 1.0;

        /// <summary>
        /// The presentation clock: a free-running stopwatch, corrected toward the audio device by
        /// <see cref="SyncClock"/> rather than read from it. Reading the device directly is more
        /// accurate and deadlocks -- the clock stops whenever audio runs dry, and only the decoder can
        /// restart it, while the decoder is waiting on that same clock for a frame to come due.
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
        /// Corrects the stopwatch against what AAudio has really played, so long playback does not drift
        /// out of lip-sync. Dispatcher thread only -- it is the sole writer of the clock base.
        /// </summary>
        private void SyncClock()
        {
            if (_rate == 0 || !AudioIsMaster(_rate)) return;

            long played = AudioPlayedTicks();
            if (played <= 0) return;

            if (s_log && _audioSyncs++ % 120 == 0)
            {
                Log($"audio played={played / (double)TimeSpan.TicksPerSecond:F2}s");
            }

            long audio = _audioBaseTicks + played;
            if (Math.Abs(audio - NowTicks()) < ResyncTicks) return;

            _swBaseTicks = audio;
            _sw.Restart();
        }

        /// <summary>
        /// What the device has actually consumed, in media ticks. AAudio's counters run from stream
        /// creation and are NOT documented to reset on flush, so a base is captured at every flush and
        /// subtracted -- the same value <see cref="_audioBaseTicks"/> is expressed relative to.
        /// </summary>
        private long AudioPlayedTicks()
        {
            if (_aaudio == IntPtr.Zero || _sampleRate <= 0) return 0;
            long frames = AAudioStream_getFramesRead(_aaudio) - _framesReadBase;
            if (frames <= 0) return 0;
            return frames * TimeSpan.TicksPerSecond / _sampleRate;
        }

        private bool AudioDrained()
        {
            if (_aaudio == IntPtr.Zero) return true;
            return _audioPendingBytes == 0 && AAudioStream_getFramesRead(_aaudio) >= AAudioStream_getFramesWritten(_aaudio);
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

            // Ended once the pipeline is out of samples AND everything already decoded has been shown
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
                if (s_log && _framesPresented++ % 60 == 0)
                {
                    Log($"present #{_framesPresented - 1} pts={due.Ticks / (double)TimeSpan.TicksPerSecond:F2}s now={now / (double)TimeSpan.TicksPerSecond:F2}s");
                }

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
                    bool progressed = false;

                    // Audio first and unconditionally: silence is the one outcome worse than a late
                    // frame, and the whole clock hangs off this stream staying fed.
                    progressed |= DrainAudioOutput();

                    // Unconditional once the extractor is done: the EOS handshake must not be gated on
                    // the pacing budget, or a full frame queue could hold MediaEnded back forever.
                    if (_extractorEnded) progressed |= PumpEndOfStream();
                    else if (WantMoreData()) progressed |= FeedInput();
                    if (WantMoreVideo()) progressed |= DrainVideoOutput();
                    progressed |= AcquireImages();

                    if (!_decodeEnded && _videoEnded && _audioEnded) _decodeEnded = true;

                    if (!progressed) Thread.Sleep(_decodeEnded ? 15 : 2);
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
        /// It has to be a TIME budget rather than a frame count. Gating on a full queue and dropping the
        /// oldest frame to make room looks equivalent and is not: video decodes far faster than audio
        /// plays, so the queue becomes a sliding window of frames from the future, none of which is ever
        /// due, and playback freezes on frame one while the clock runs on.
        /// </para>
        /// <para>
        /// The lead must also stay under how much audio AAudio holds, so that pausing the reader on this
        /// gate can never starve the device: a starved device stops the audio clock, and a stopped clock
        /// means no frame ever comes due.
        /// </para>
        /// </summary>
        private static readonly long LeadTicks = TimeSpan.TicksPerMillisecond * 400;

        private bool WantMoreData()
        {
            // Anything still owed to the audio device outranks the video lead.
            if (_audioPendingBytes > 0) return true;

            lock (_lock)
            {
                if (_pending.Count >= MaxPendingFrames) return false;
            }

            return !TooFarAhead();
        }

        private bool WantMoreVideo()
        {
            if (_videoCodec == IntPtr.Zero) return false;

            lock (_lock)
            {
                if (_pending.Count >= MaxPendingFrames) return false;
            }

            return !TooFarAhead();
        }

        private bool TooFarAhead()
            => _videoTrack >= 0 && _newestQueuedTicks >= 0 && _newestQueuedTicks - NowTicks() > LeadTicks;

        /// <summary>
        /// Moves one demuxed sample into whichever codec owns its track. The extractor is only advanced
        /// once the sample is actually queued, so a codec whose input is momentarily full simply defers
        /// the read instead of losing the sample.
        /// </summary>
        private bool FeedInput()
        {
            int track = AMediaExtractor_getSampleTrackIndex(_extractor);
            if (track < 0)
            {
                // End of stream. The EOS markers themselves are PumpEndOfStream's job, not this one's:
                // every started codec needs its own, and a codec whose input queue happens to be full
                // right now has to be asked again.
                _extractorEnded = true;
                return true;
            }

            IntPtr codec = track == _videoTrack ? _videoCodec : track == _audioTrack ? _audioCodec : IntPtr.Zero;
            if (codec == IntPtr.Zero)
            {
                AMediaExtractor_advance(_extractor);   // a track we did not select
                return true;
            }

            nint index = AMediaCodec_dequeueInputBuffer(codec, 0);
            if (index < 0) return false;

            nuint capacity = 0;
            byte* buffer = AMediaCodec_getInputBuffer(codec, (nuint)index, &capacity);
            if (buffer == null) return false;

            nint read = AMediaExtractor_readSampleData(_extractor, buffer, capacity);
            if (read < 0)
            {
                // The buffer is dequeued and must go back, so this one carries the marker; the rest is
                // again PumpEndOfStream's.
                AMediaCodec_queueInputBuffer(codec, (nuint)index, 0, 0, 0, AMEDIACODEC_BUFFER_FLAG_END_OF_STREAM);
                if (codec == _videoCodec) _videoEosQueued = true; else _audioEosQueued = true;
                _extractorEnded = true;
                return true;
            }

            long timeUs = AMediaExtractor_getSampleTime(_extractor);
            AMediaCodec_queueInputBuffer(codec, (nuint)index, 0, (nuint)read, timeUs < 0 ? 0UL : (ulong)timeUs, 0);
            AMediaExtractor_advance(_extractor);
            return true;
        }

        /// <summary>
        /// Hands each started codec its end-of-stream marker, retrying until both have taken one.
        /// <para>
        /// This runs as its own loop step rather than as a branch of <see cref="FeedInput"/>, because a
        /// codec can refuse the marker: dequeueInputBuffer returns nothing when its input queue is
        /// momentarily full. Queued from inside FeedInput on the pass that discovered the end of the
        /// stream, that single refusal was permanent -- FeedInput is not called again once the
        /// extractor is done, so that codec never emitted its output EOS flag, MediaEnded never fired,
        /// and the failure depended on buffer timing, so it reproduced only sometimes.
        /// </para>
        /// </summary>
        private bool PumpEndOfStream()
        {
            bool progressed = false;
            progressed |= QueueEndOfStream(_videoCodec, ref _videoEosQueued, ref _videoEnded);
            progressed |= QueueEndOfStream(_audioCodec, ref _audioEosQueued, ref _audioEnded);
            return progressed;
        }

        private bool QueueEndOfStream(IntPtr codec, ref bool queued, ref bool ended)
        {
            // A track this media does not have, or a decoder that never started, is already drained.
            if (codec == IntPtr.Zero) { queued = true; ended = true; return false; }
            if (queued) return false;

            nint index = AMediaCodec_dequeueInputBuffer(codec, 0);
            if (index < 0) return false;   // input full; asked again on the next pass

            AMediaCodec_queueInputBuffer(codec, (nuint)index, 0, 0, 0, AMEDIACODEC_BUFFER_FLAG_END_OF_STREAM);
            queued = true;
            return true;
        }

        /// <summary>
        /// Releases one decoded output buffer INTO the AImageReader surface. Rendering is the only way to
        /// get at the pixels here, so the pacing decision was already made by the caller.
        /// </summary>
        private bool DrainVideoOutput()
        {
            AMediaCodecBufferInfo info = default;
            nint index = AMediaCodec_dequeueOutputBuffer(_videoCodec, &info, 0);

            if (index == AMEDIACODEC_INFO_OUTPUT_FORMAT_CHANGED)
            {
                IntPtr fmt = AMediaCodec_getOutputFormat(_videoCodec);
                if (fmt != IntPtr.Zero)
                {
                    if (AMediaFormat_getInt32(fmt, "width", out int w) && w > 0) _width = w;
                    if (AMediaFormat_getInt32(fmt, "height", out int h) && h > 0) _height = h;
                    AMediaFormat_delete(fmt);
                    Log($"video output format {_width}x{_height}");
                }
                return true;
            }

            if (index < 0) return false;

            bool render = info.size > 0;
            if (render) _lastReleasedTicks = info.presentationTimeUs * 10;
            AMediaCodec_releaseOutputBuffer(_videoCodec, (nuint)index, render);
            if ((info.flags & AMEDIACODEC_BUFFER_FLAG_END_OF_STREAM) != 0) _videoEnded = true;
            return true;
        }

        /// <summary>
        /// Converts every image the reader has ready into a pooled BGRA frame. acquireNextImage rather
        /// than acquireLatestImage: the queue is already bounded by <see cref="ReaderImages"/> and the
        /// pacing gate above, and dropping frames is the presentation timer's job, not the demuxer's.
        /// </summary>
        private bool AcquireImages()
        {
            if (_reader == IntPtr.Zero) return false;

            bool any = false;
            while (true)
            {
                if (AImageReader_acquireNextImage(_reader, out IntPtr image) != AMEDIA_OK || image == IntPtr.Zero) break;

                try { ConvertImage(image); }
                finally { AImage_delete(image); }
                any = true;
            }

            return any;
        }

        private void DrainReader()
        {
            if (_reader == IntPtr.Zero) return;
            while (AImageReader_acquireNextImage(_reader, out IntPtr image) == AMEDIA_OK && image != IntPtr.Zero)
            {
                AImage_delete(image);
            }
        }

        private void ConvertImage(IntPtr image)
        {
            AImageCropRect crop = default;
            int w, h;
            if (AImage_getCropRect(image, &crop) == AMEDIA_OK && crop.right > crop.left && crop.bottom > crop.top)
            {
                w = crop.right - crop.left;
                h = crop.bottom - crop.top;
            }
            else
            {
                crop = default;
                if (AImage_getWidth(image, out w) != AMEDIA_OK || AImage_getHeight(image, out h) != AMEDIA_OK) return;
            }

            if (w <= 0 || h <= 0) return;

            byte* y = null; int yLen = 0; int yRow, yPix;
            byte* u = null; int uLen = 0; int uRow, uPix;
            byte* v = null; int vLen = 0; int vRow, vPix;
            if (AImage_getPlaneData(image, 0, &y, &yLen) != AMEDIA_OK ||
                AImage_getPlaneData(image, 1, &u, &uLen) != AMEDIA_OK ||
                AImage_getPlaneData(image, 2, &v, &vLen) != AMEDIA_OK)
            {
                return;
            }

            if (AImage_getPlaneRowStride(image, 0, out yRow) != AMEDIA_OK ||
                AImage_getPlaneRowStride(image, 1, out uRow) != AMEDIA_OK ||
                AImage_getPlaneRowStride(image, 2, out vRow) != AMEDIA_OK)
            {
                return;
            }

            // Pixel stride is what separates NV12/NV21 (2, chroma interleaved) from fully planar I420
            // (1). Asking beats assuming: both layouts are legal YUV_420_888 and both are shipped.
            if (AImage_getPlanePixelStride(image, 0, out yPix) != AMEDIA_OK) yPix = 1;
            if (AImage_getPlanePixelStride(image, 1, out uPix) != AMEDIA_OK) uPix = 1;
            if (AImage_getPlanePixelStride(image, 2, out vPix) != AMEDIA_OK) vPix = 1;

            long ticks;
            if (AImage_getTimestamp(image, out long ns) == AMEDIA_OK && ns > 0) ticks = ns / 100;
            else ticks = _lastReleasedTicks;

            int rowBytes = w * 4;
            Frame f = RentFrame(rowBytes * h);
            f.Ticks = ticks;
            f.Width = w;
            f.Height = h;
            f.RowBytes = rowBytes;

            YuvToBgra(y, yRow, yPix, u, uRow, uPix, v, vRow, vPix, crop.left, crop.top, w, h, (byte*)f.Pixels, rowBytes);

            if (_width <= 0) _width = w;
            if (_height <= 0) _height = h;

            if (s_log && _framesDecoded++ % 60 == 0)
            {
                uint* px = (uint*)f.Pixels;
                Log($"frame #{_framesDecoded - 1} {w}x{h} pts={ticks / (double)TimeSpan.TicksPerSecond:F2}s " +
                    $"px0=0x{px[0]:X8} pxMid=0x{px[w * h / 2]:X8} yRow={yRow} uPix={uPix}");
            }

            lock (_lock)
            {
                // Safety valve only -- the pacing gate normally stops us long before this. Dropping the
                // OLDEST is right: it is the one the clock has most likely already passed.
                if (_pending.Count >= MaxPendingFrames) _free.Push(_pending.Dequeue());
                _pending.Enqueue(f);
                _newestQueuedTicks = f.Ticks;
            }
        }

        /// <summary>
        /// YUV_420_888 -> BGRA32, limited range, honouring both strides and the crop origin. BT.709 is
        /// used from 720 lines up and BT.601 below, which is the convention the content itself follows.
        /// </summary>
        private static void YuvToBgra(
            byte* y, int yRow, int yPix,
            byte* u, int uRow, int uPix,
            byte* v, int vRow, int vPix,
            int cropX, int cropY, int width, int height,
            byte* dst, int dstRow)
        {
            // Integer coefficients, 8-bit fixed point, from the limited-range matrices.
            bool hd = height >= 720;
            int cr = hd ? 459 : 409;   // R from V
            int cg1 = hd ? 55 : 100;   // G from U
            int cg2 = hd ? 136 : 208;  // G from V
            int cb = hd ? 541 : 516;   // B from U

            for (int row = 0; row < height; row++)
            {
                int sy = cropY + row;
                byte* yLine = y + (long)sy * yRow;
                byte* uLine = u + (long)(sy >> 1) * uRow;
                byte* vLine = v + (long)(sy >> 1) * vRow;
                uint* dstLine = (uint*)(dst + (long)row * dstRow);

                for (int col = 0; col < width; col++)
                {
                    int sx = cropX + col;
                    int c = yLine[sx * yPix] - 16;
                    int d = uLine[(sx >> 1) * uPix] - 128;
                    int e = vLine[(sx >> 1) * vPix] - 128;

                    int r = (298 * c + cr * e + 128) >> 8;
                    int g = (298 * c - cg1 * d - cg2 * e + 128) >> 8;
                    int b = (298 * c + cb * d + 128) >> 8;

                    if ((uint)r > 255) r = r < 0 ? 0 : 255;
                    if ((uint)g > 255) g = g < 0 ? 0 : 255;
                    if ((uint)b > 255) b = b < 0 ? 0 : 255;

                    // Little-endian B,G,R,A -- the byte order the SendVideoFrame seam wants. Video is
                    // opaque, so alpha is forced here rather than in a second pass.
                    dstLine[col] = 0xFF000000u | ((uint)r << 16) | ((uint)g << 8) | (uint)b;
                }
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

        private bool DrainAudioOutput()
        {
            if (_audioCodec == IntPtr.Zero) return false;

            // Anything left over from the previous write goes first: AAudio takes whole frames and will
            // accept a partial buffer, so the remainder has to be carried rather than dropped.
            if (_audioPendingBytes > 0 && !PushPendingAudio()) return false;

            AMediaCodecBufferInfo info;
            nint index = AMediaCodec_dequeueOutputBuffer(_audioCodec, &info, 0);

            if (index == AMEDIACODEC_INFO_OUTPUT_FORMAT_CHANGED)
            {
                IntPtr fmt = AMediaCodec_getOutputFormat(_audioCodec);
                if (fmt != IntPtr.Zero)
                {
                    if (AMediaFormat_getInt32(fmt, "sample-rate", out int rate) && rate > 0) _sampleRate = rate;
                    if (AMediaFormat_getInt32(fmt, "channel-count", out int ch) && ch > 0) _channels = ch;
                    AMediaFormat_delete(fmt);
                }

                OpenAudioDevice();
                return true;
            }

            if (index < 0) return false;

            if (info.size > 0)
            {
                nuint size = 0;
                byte* src = AMediaCodec_getOutputBuffer(_audioCodec, (nuint)index, &size);
                if (src != null)
                {
                    if (_aaudio == IntPtr.Zero) OpenAudioDevice();

                    // Off-speed playback would need resampling to stay in tune, which is more machinery
                    // than it is worth; the video clock takes over and audio goes quiet until 1x.
                    if (_aaudio != IntPtr.Zero && (_rate == 0 || _rate == 1.0))
                    {
                        StageAudio(src + info.offset, info.size);
                        PushPendingAudio();
                    }
                }
            }

            if ((info.flags & AMEDIACODEC_BUFFER_FLAG_END_OF_STREAM) != 0) _audioEnded = true;
            AMediaCodec_releaseOutputBuffer(_audioCodec, (nuint)index, false);
            return true;
        }

        /// <summary>
        /// Copies decoded PCM16 into our own buffer, applying volume and balance on the way. This copy
        /// has to happen anyway -- the codec buffer is returned to the codec long before AAudio has
        /// consumed what we handed it -- so the gain rides along for one multiply per sample.
        /// </summary>
        private void StageAudio(byte* src, int bytes)
        {
            if (bytes > _audioScratchBytes)
            {
                if (_audioScratch != IntPtr.Zero) Marshal.FreeHGlobal(_audioScratch);
                _audioScratch = Marshal.AllocHGlobal(bytes);
                _audioScratchBytes = bytes;
            }

            double v = Math.Clamp(_volume, 0.0, 1.0);
            double b = Math.Clamp(_balance, -1.0, 1.0);
            int left = (int)Math.Round(v * (b > 0 ? 1.0 - b : 1.0) * 4096);
            int right = (int)Math.Round(v * (b < 0 ? 1.0 + b : 1.0) * 4096);

            short* s = (short*)src;
            short* d = (short*)_audioScratch;
            int samples = bytes / sizeof(short);
            int channels = _channels > 0 ? _channels : 1;

            for (int i = 0; i < samples; i++)
            {
                // Balance only means anything for stereo; anything else takes the plain volume.
                int gain = channels == 2 ? ((i & 1) == 0 ? left : right) : left;
                int scaled = (s[i] * gain) >> 12;
                if (scaled > short.MaxValue) scaled = short.MaxValue;
                else if (scaled < short.MinValue) scaled = short.MinValue;
                d[i] = (short)scaled;
            }

            _audioPendingOffset = 0;
            _audioPendingBytes = bytes;
        }

        /// <summary>Non-blocking write; returns true once the staged block is fully handed over.</summary>
        private bool PushPendingAudio()
        {
            if (_aaudio == IntPtr.Zero || _frameBytes <= 0) { _audioPendingBytes = 0; return true; }

            while (_audioPendingBytes >= _frameBytes)
            {
                int frames = _audioPendingBytes / _frameBytes;
                int written = AAudioStream_write(_aaudio, (byte*)_audioScratch + _audioPendingOffset, frames, 0);
                if (written <= 0) return false;      // device full (or an error); try again next pass

                int consumed = written * _frameBytes;
                _audioPendingOffset += consumed;
                _audioPendingBytes -= consumed;
            }

            _audioPendingBytes = 0;
            return true;
        }

        private void OpenAudioDevice()
        {
            if (_aaudio != IntPtr.Zero || _sampleRate <= 0 || _channels <= 0) return;
            if (!s_aaudioAvailable) return;

            if (AAudio_createStreamBuilder(out IntPtr builder) != AAUDIO_OK || builder == IntPtr.Zero)
            {
                Log("AAudio builder unavailable; playing without sound");
                return;
            }

            try
            {
                AAudioStreamBuilder_setDirection(builder, AAUDIO_DIRECTION_OUTPUT);
                AAudioStreamBuilder_setFormat(builder, AAUDIO_FORMAT_PCM_I16);
                AAudioStreamBuilder_setSampleRate(builder, _sampleRate);
                AAudioStreamBuilder_setChannelCount(builder, _channels);
                // NONE, not LOW_LATENCY: this stream is fed from a decode thread against a 400 ms video
                // lead, so a deep buffer is exactly what it wants. A low-latency stream would underrun
                // on every scheduling hiccup, and an underrun stalls the clock.
                AAudioStreamBuilder_setPerformanceMode(builder, AAUDIO_PERFORMANCE_MODE_NONE);

                int status = AAudioStreamBuilder_openStream(builder, out IntPtr stream);
                if (status != AAUDIO_OK || stream == IntPtr.Zero)
                {
                    Log($"AAudio open failed ({status}); playing without sound");
                    return;
                }

                _aaudio = stream;
                _frameBytes = _channels * sizeof(short);
                _framesReadBase = 0;

                // Ask for roughly half a second of slack, capped by what the device actually has.
                int capacity = AAudioStream_getBufferCapacityInFrames(_aaudio);
                int want = _sampleRate / 2;
                AAudioStream_setBufferSizeInFrames(_aaudio, capacity > 0 && want > capacity ? capacity : want);

                if (_rate != 0) AAudioStream_requestStart(_aaudio);
                Log($"AAudio {_sampleRate}Hz x{_channels} capacity={capacity}");
            }
            finally
            {
                AAudioStreamBuilder_delete(builder);
            }
        }

        private void FlushAudioDevice()
        {
            if (_aaudio == IntPtr.Zero) return;

            _audioPendingBytes = 0;
            _audioPendingOffset = 0;
            AAudioStream_requestPause(_aaudio);
            AAudioStream_requestFlush(_aaudio);
            // The counters are not documented to reset here, so re-base rather than assume either way.
            _framesReadBase = AAudioStream_getFramesRead(_aaudio);
            if (_rate != 0) AAudioStream_requestStart(_aaudio);
        }

        private void CloseAudioDevice()
        {
            if (_aaudio != IntPtr.Zero)
            {
                AAudioStream_requestStop(_aaudio);
                AAudioStream_close(_aaudio);
                _aaudio = IntPtr.Zero;
            }

            if (_audioScratch != IntPtr.Zero) { Marshal.FreeHGlobal(_audioScratch); _audioScratch = IntPtr.Zero; }
            _audioScratchBytes = _audioPendingBytes = _audioPendingOffset = 0;
            _sampleRate = _channels = _frameBytes = 0;
            _framesReadBase = 0;
        }

        // ------------------------------------------------------------------ set-up ----

        private void CreateExtractor(string url)
        {
            _extractor = AMediaExtractor_new();
            if (_extractor == IntPtr.Zero) throw new NotSupportedException("The Android media extractor could not be created.");

            bool remote = url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                       || url.StartsWith("https://", StringComparison.OrdinalIgnoreCase);

            int status;
            if (remote)
            {
                status = AMediaExtractor_setDataSource(_extractor, url);
            }
            else
            {
                // A file descriptor rather than a path: the NDK extractor takes a URI, and handing it a
                // bare path (or hand-building a file:// URI out of one) is where filenames with spaces
                // and app-private storage paths go wrong. Opening it ourselves also produces the honest
                // FileNotFoundException instead of a generic media status.
                _file = new FileStream(url, FileMode.Open, FileAccess.Read);
                status = AMediaExtractor_setDataSourceFd(_extractor, (int)_file.SafeFileHandle.DangerousGetHandle(), 0, _file.Length);
            }

            if (status != AMEDIA_OK)
            {
                throw new NotSupportedException($"Android could not open the media ({url}): status {status}.");
            }
        }

        private void SelectTracks()
        {
            nuint count = AMediaExtractor_getTrackCount(_extractor);
            for (nuint i = 0; i < count; i++)
            {
                IntPtr fmt = AMediaExtractor_getTrackFormat(_extractor, i);
                if (fmt == IntPtr.Zero) continue;

                try
                {
                    if (!AMediaFormat_getString(fmt, "mime", out IntPtr mimePtr) || mimePtr == IntPtr.Zero) continue;
                    string mime = Marshal.PtrToStringUTF8(mimePtr);
                    if (mime == null) continue;

                    if (_videoTrack < 0 && mime.StartsWith("video/", StringComparison.Ordinal))
                    {
                        _videoTrack = (int)i;
                        _videoMime = mime;
                        if (AMediaFormat_getInt32(fmt, "width", out int w)) _width = w;
                        if (AMediaFormat_getInt32(fmt, "height", out int h)) _height = h;
                        TakeDuration(fmt);
                        AMediaExtractor_selectTrack(_extractor, i);
                    }
                    else if (_audioTrack < 0 && mime.StartsWith("audio/", StringComparison.Ordinal))
                    {
                        _audioTrack = (int)i;
                        _audioMime = mime;
                        if (AMediaFormat_getInt32(fmt, "sample-rate", out int rate)) _sampleRate = rate;
                        if (AMediaFormat_getInt32(fmt, "channel-count", out int ch)) _channels = ch;
                        TakeDuration(fmt);
                        AMediaExtractor_selectTrack(_extractor, i);
                    }
                }
                finally
                {
                    AMediaFormat_delete(fmt);
                }
            }

            Log($"tracks video={_videoTrack} ({_videoMime}) {_width}x{_height} audio={_audioTrack} ({_audioMime}) {_sampleRate}Hz x{_channels}");
        }

        private string _videoMime, _audioMime;

        private void TakeDuration(IntPtr fmt)
        {
            // The longest track wins: an audio track that outlasts the video still has to play.
            if (AMediaFormat_getInt64(fmt, "durationUs", out long us) && us > 0)
            {
                long ticks = us * 10;
                if (ticks > _lengthTicks) _lengthTicks = ticks;
            }
        }

        private void StartVideoCodec()
        {
            if (_videoTrack < 0) return;
            if (_width <= 0 || _height <= 0) throw new NotSupportedException("The video track does not declare its size.");

            int status = AImageReader_new(_width, _height, AIMAGE_FORMAT_YUV_420_888, ReaderImages, out _reader);
            if (status != AMEDIA_OK || _reader == IntPtr.Zero)
            {
                throw new NotSupportedException($"An image reader for {_width}x{_height} YUV_420_888 could not be created (status {status}).");
            }

            status = AImageReader_getWindow(_reader, out _readerWindow);
            if (status != AMEDIA_OK || _readerWindow == IntPtr.Zero)
            {
                throw new NotSupportedException($"The image reader produced no surface (status {status}).");
            }

            _videoCodec = AMediaCodec_createDecoderByType(_videoMime);
            if (_videoCodec == IntPtr.Zero) throw new NotSupportedException($"No decoder for {_videoMime}.");

            IntPtr fmt = AMediaExtractor_getTrackFormat(_extractor, (nuint)_videoTrack);
            try
            {
                status = AMediaCodec_configure(_videoCodec, fmt, _readerWindow, IntPtr.Zero, 0);
                if (status != AMEDIA_OK) throw new NotSupportedException($"The {_videoMime} decoder rejected the stream (status {status}).");
                status = AMediaCodec_start(_videoCodec);
                if (status != AMEDIA_OK) throw new NotSupportedException($"The {_videoMime} decoder did not start (status {status}).");
            }
            finally
            {
                if (fmt != IntPtr.Zero) AMediaFormat_delete(fmt);
            }
        }

        private void StartAudioCodec()
        {
            if (_audioTrack < 0) return;

            _audioCodec = AMediaCodec_createDecoderByType(_audioMime);
            if (_audioCodec == IntPtr.Zero)
            {
                // A missing audio decoder is not a failure of the media: play the picture.
                Log($"no decoder for {_audioMime}; playing without sound");
                _audioTrack = -1;
                return;
            }

            IntPtr fmt = AMediaExtractor_getTrackFormat(_extractor, (nuint)_audioTrack);
            try
            {
                if (AMediaCodec_configure(_audioCodec, fmt, IntPtr.Zero, IntPtr.Zero, 0) != AMEDIA_OK ||
                    AMediaCodec_start(_audioCodec) != AMEDIA_OK)
                {
                    Log($"the {_audioMime} decoder did not start; playing without sound");
                    AMediaCodec_delete(_audioCodec);
                    _audioCodec = IntPtr.Zero;
                    _audioTrack = -1;
                }
            }
            finally
            {
                if (fmt != IntPtr.Zero) AMediaFormat_delete(fmt);
            }
        }

        public void Dispose() => Close();

        // ================================================================ interop ====

        private const string MediaNdk = "mediandk";     // libmediandk.so
        private const string AAudioLib = "aaudio";      // libaaudio.so, API 26+

        private const int AMEDIA_OK = 0;
        private const int AAUDIO_OK = 0;

        private const nint AMEDIACODEC_INFO_TRY_AGAIN_LATER = -1;
        private const nint AMEDIACODEC_INFO_OUTPUT_FORMAT_CHANGED = -2;
        private const nint AMEDIACODEC_INFO_OUTPUT_BUFFERS_CHANGED = -3;
        private const uint AMEDIACODEC_BUFFER_FLAG_END_OF_STREAM = 4;

        private const int AMEDIAEXTRACTOR_SEEK_PREVIOUS_SYNC = 0;
        private const int AMEDIAEXTRACTOR_SEEK_NEXT_SYNC = 1;
        private const int AMEDIAEXTRACTOR_SEEK_CLOSEST_SYNC = 2;

        private const int AIMAGE_FORMAT_YUV_420_888 = 0x23;

        private const int AAUDIO_DIRECTION_OUTPUT = 0;
        private const int AAUDIO_FORMAT_PCM_I16 = 1;
        private const int AAUDIO_PERFORMANCE_MODE_NONE = 10;

        /// <summary>
        /// AAudio arrived in API 26 and the heads do not pin a minimum above that, so its absence is a
        /// silent-video case rather than a crash. libmediandk has been there since API 21 and its
        /// absence really is a failure to open the media.
        /// </summary>
        private static readonly bool s_aaudioAvailable = NativeLibrary.TryLoad("libaaudio.so", out _);

        [StructLayout(LayoutKind.Sequential)]
        private struct AMediaCodecBufferInfo
        {
            public int offset;
            public int size;
            public long presentationTimeUs;
            public uint flags;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct AImageCropRect
        {
            public int left;
            public int top;
            public int right;
            public int bottom;
        }

        // ---- AMediaExtractor ----
        [DllImport(MediaNdk)] private static extern IntPtr AMediaExtractor_new();
        [DllImport(MediaNdk)] private static extern int AMediaExtractor_delete(IntPtr ex);
        [DllImport(MediaNdk)] private static extern int AMediaExtractor_setDataSource(IntPtr ex, [MarshalAs(UnmanagedType.LPUTF8Str)] string location);
        [DllImport(MediaNdk)] private static extern int AMediaExtractor_setDataSourceFd(IntPtr ex, int fd, long offset, long length);
        [DllImport(MediaNdk)] private static extern nuint AMediaExtractor_getTrackCount(IntPtr ex);
        [DllImport(MediaNdk)] private static extern IntPtr AMediaExtractor_getTrackFormat(IntPtr ex, nuint idx);
        [DllImport(MediaNdk)] private static extern int AMediaExtractor_selectTrack(IntPtr ex, nuint idx);
        [DllImport(MediaNdk)] private static extern nint AMediaExtractor_readSampleData(IntPtr ex, byte* buffer, nuint capacity);
        [DllImport(MediaNdk)] private static extern long AMediaExtractor_getSampleTime(IntPtr ex);
        [DllImport(MediaNdk)] private static extern int AMediaExtractor_getSampleTrackIndex(IntPtr ex);
        [DllImport(MediaNdk)] [return: MarshalAs(UnmanagedType.I1)] private static extern bool AMediaExtractor_advance(IntPtr ex);
        [DllImport(MediaNdk)] private static extern int AMediaExtractor_seekTo(IntPtr ex, long seekPosUs, int mode);

        // ---- AMediaFormat ----
        [DllImport(MediaNdk)] private static extern int AMediaFormat_delete(IntPtr fmt);
        [DllImport(MediaNdk)] [return: MarshalAs(UnmanagedType.I1)] private static extern bool AMediaFormat_getInt32(IntPtr fmt, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, out int value);
        [DllImport(MediaNdk)] [return: MarshalAs(UnmanagedType.I1)] private static extern bool AMediaFormat_getInt64(IntPtr fmt, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, out long value);
        [DllImport(MediaNdk)] [return: MarshalAs(UnmanagedType.I1)] private static extern bool AMediaFormat_getString(IntPtr fmt, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, out IntPtr value);

        // ---- AMediaCodec ----
        [DllImport(MediaNdk)] private static extern IntPtr AMediaCodec_createDecoderByType([MarshalAs(UnmanagedType.LPUTF8Str)] string mime);
        [DllImport(MediaNdk)] private static extern int AMediaCodec_delete(IntPtr codec);
        [DllImport(MediaNdk)] private static extern int AMediaCodec_configure(IntPtr codec, IntPtr format, IntPtr surface, IntPtr crypto, uint flags);
        [DllImport(MediaNdk)] private static extern int AMediaCodec_start(IntPtr codec);
        [DllImport(MediaNdk)] private static extern int AMediaCodec_stop(IntPtr codec);
        [DllImport(MediaNdk)] private static extern int AMediaCodec_flush(IntPtr codec);
        [DllImport(MediaNdk)] private static extern nint AMediaCodec_dequeueInputBuffer(IntPtr codec, long timeoutUs);
        [DllImport(MediaNdk)] private static extern byte* AMediaCodec_getInputBuffer(IntPtr codec, nuint idx, nuint* outSize);
        [DllImport(MediaNdk)] private static extern int AMediaCodec_queueInputBuffer(IntPtr codec, nuint idx, long offset, nuint size, ulong time, uint flags);
        [DllImport(MediaNdk)] private static extern nint AMediaCodec_dequeueOutputBuffer(IntPtr codec, AMediaCodecBufferInfo* info, long timeoutUs);
        [DllImport(MediaNdk)] private static extern byte* AMediaCodec_getOutputBuffer(IntPtr codec, nuint idx, nuint* outSize);
        [DllImport(MediaNdk)] private static extern int AMediaCodec_releaseOutputBuffer(IntPtr codec, nuint idx, [MarshalAs(UnmanagedType.I1)] bool render);
        [DllImport(MediaNdk)] private static extern IntPtr AMediaCodec_getOutputFormat(IntPtr codec);

        // ---- AImageReader / AImage ----
        [DllImport(MediaNdk)] private static extern int AImageReader_new(int width, int height, int format, int maxImages, out IntPtr reader);
        [DllImport(MediaNdk)] private static extern void AImageReader_delete(IntPtr reader);
        [DllImport(MediaNdk)] private static extern int AImageReader_getWindow(IntPtr reader, out IntPtr window);
        [DllImport(MediaNdk)] private static extern int AImageReader_acquireNextImage(IntPtr reader, out IntPtr image);
        [DllImport(MediaNdk)] private static extern void AImage_delete(IntPtr image);
        [DllImport(MediaNdk)] private static extern int AImage_getWidth(IntPtr image, out int width);
        [DllImport(MediaNdk)] private static extern int AImage_getHeight(IntPtr image, out int height);
        [DllImport(MediaNdk)] private static extern int AImage_getTimestamp(IntPtr image, out long timestampNs);
        [DllImport(MediaNdk)] private static extern int AImage_getCropRect(IntPtr image, AImageCropRect* rect);
        [DllImport(MediaNdk)] private static extern int AImage_getPlaneData(IntPtr image, int planeIdx, byte** data, int* dataLength);
        [DllImport(MediaNdk)] private static extern int AImage_getPlaneRowStride(IntPtr image, int planeIdx, out int rowStride);
        [DllImport(MediaNdk)] private static extern int AImage_getPlanePixelStride(IntPtr image, int planeIdx, out int pixelStride);

        // ---- AAudio ----
        [DllImport(AAudioLib)] private static extern int AAudio_createStreamBuilder(out IntPtr builder);
        [DllImport(AAudioLib)] private static extern int AAudioStreamBuilder_delete(IntPtr builder);
        [DllImport(AAudioLib)] private static extern void AAudioStreamBuilder_setDirection(IntPtr builder, int direction);
        [DllImport(AAudioLib)] private static extern void AAudioStreamBuilder_setFormat(IntPtr builder, int format);
        [DllImport(AAudioLib)] private static extern void AAudioStreamBuilder_setSampleRate(IntPtr builder, int sampleRate);
        [DllImport(AAudioLib)] private static extern void AAudioStreamBuilder_setChannelCount(IntPtr builder, int channelCount);
        [DllImport(AAudioLib)] private static extern void AAudioStreamBuilder_setPerformanceMode(IntPtr builder, int mode);
        [DllImport(AAudioLib)] private static extern int AAudioStreamBuilder_openStream(IntPtr builder, out IntPtr stream);
        [DllImport(AAudioLib)] private static extern int AAudioStream_close(IntPtr stream);
        [DllImport(AAudioLib)] private static extern int AAudioStream_requestStart(IntPtr stream);
        [DllImport(AAudioLib)] private static extern int AAudioStream_requestPause(IntPtr stream);
        [DllImport(AAudioLib)] private static extern int AAudioStream_requestFlush(IntPtr stream);
        [DllImport(AAudioLib)] private static extern int AAudioStream_requestStop(IntPtr stream);
        [DllImport(AAudioLib)] private static extern int AAudioStream_write(IntPtr stream, void* buffer, int numFrames, long timeoutNanoseconds);
        [DllImport(AAudioLib)] private static extern long AAudioStream_getFramesRead(IntPtr stream);
        [DllImport(AAudioLib)] private static extern long AAudioStream_getFramesWritten(IntPtr stream);
        [DllImport(AAudioLib)] private static extern int AAudioStream_getBufferCapacityInFrames(IntPtr stream);
        [DllImport(AAudioLib)] private static extern int AAudioStream_setBufferSizeInFrames(IntPtr stream, int numFrames);
    }
}
