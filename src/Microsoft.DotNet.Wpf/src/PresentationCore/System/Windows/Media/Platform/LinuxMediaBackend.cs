// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// LinuxMediaBackend -- the Linux IMediaBackend, backed by GStreamer.
//
// GStreamer is the media stack on Linux: it is what GNOME Videos, Firefox's fallback path and most of
// the desktop use, it is present on any normal install, and it brings hardware decode, audio output
// and seeking with it. The alternative (libav/ffmpeg directly) would mean owning demuxing, audio
// output and the A/V clock ourselves.
//
// The pipeline is `playbin`, which handles URI resolution, demux, decode and audio for us, with its
// video-sink replaced by
//
//     videoconvert ! video/x-raw,format=BGRA ! appsink
//
// so decoded frames arrive as BGRA32 -- the byte order WPF's Bgra32 already uses, and the one
// MediaPlayer.UpdateResource ships to the WebGPU compositor. videoconvert absorbs whatever the
// decoder produces (I420, NV12, ...) so this file never has to know a colour space.
//
// Like MacMediaBackend, everything runs on the media dispatcher thread: a DispatcherTimer polls the
// bus for EOS/error/buffering and pulls the newest sample with a ZERO timeout, so nothing blocks and
// no frame crosses a thread boundary.
//
// Interop notes, because GLib is hostile to P/Invoke in two specific ways:
//   * g_object_set is VARIADIC. Variadic P/Invoke is fragile (aarch64 assigns variadic arguments
//     differently from fixed ones), so properties are set through the non-variadic
//     g_object_set_property + GValue instead, and everything that CAN be expressed in the pipeline
//     description string is, via gst_parse_bin_from_description.
//   * GstMessage's type field sits behind GstMiniObject, whose layout is not worth binding. Messages
//     are therefore popped with a TYPE MASK -- a non-null return from a mask of exactly one type is
//     itself the answer -- so no struct field is ever read.
//

using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Windows.Threading;

namespace System.Windows.Media
{
    [SupportedOSPlatform("linux")]
    internal sealed class LinuxMediaBackend : IMediaBackend
    {
        private readonly Dispatcher _dispatcher;
        private DispatcherTimer _timer;

        private IntPtr _playbin;     // GstElement*
        private IntPtr _sink;        // GstElement* (appsink inside the video-sink bin)
        private IntPtr _bus;         // GstBus*

        private IntPtr _sample;      // GstSample* currently held (owned)
        private GstMapInfo _map;     // valid only while _frameLocked
        private bool _frameLocked;

        private bool _opened;
        private bool _failed;
        private bool _ended;
        private int _width, _height;
        private long _lengthTicks;
        private double _rate;        // last requested rate (0 = paused)
        private double _volume = 0.5;
        private bool _buffering;

        private static readonly bool s_log = Environment.GetEnvironmentVariable("WPF_MEDIA_LOG") == "1";
        private static void Log(string m) { if (s_log) Console.WriteLine("MEDIA " + m); }

        internal LinuxMediaBackend(MediaPlayer player)
        {
            _dispatcher = player.Dispatcher;
        }

        // ---------------------------------------------------------------- transport ----

        public void Open(string url)
        {
            Log($"Open {url}");
            if (!EnsureGst()) { RaiseFailed(new NotSupportedException("GStreamer is not available.")); return; }
            Close();

            _playbin = gst_element_factory_make("playbin", "wpfplaybin");
            if (_playbin == IntPtr.Zero) { RaiseFailed(new NotSupportedException("GStreamer playbin is unavailable (install gstreamer1.0-plugins-base).")); return; }

            // playbin takes a URI, so a bare path has to become one. Uri handles the escaping that a
            // hand-rolled "file://" + path would get wrong on the first filename with a space.
            string uri = url;
            if (!Uri.TryCreate(url, UriKind.Absolute, out Uri parsed) || parsed.IsFile)
            {
                try { uri = new Uri(Path.GetFullPath(url)).AbsoluteUri; }
                catch { /* leave as-is and let playbin report the failure */ }
            }
            SetStringProperty(_playbin, "uri", uri);

            IntPtr error = IntPtr.Zero;
            IntPtr videoSink = gst_parse_bin_from_description(
                "videoconvert ! video/x-raw,format=BGRA ! appsink name=wpfsink sync=true max-buffers=2 drop=true",
                true, ref error);
            if (videoSink == IntPtr.Zero)
            {
                RaiseFailed(new NotSupportedException("Could not build the GStreamer video sink (install gstreamer1.0-plugins-base)."));
                return;
            }
            _sink = gst_bin_get_by_name(videoSink, "wpfsink");
            SetObjectProperty(_playbin, "video-sink", videoSink);

            ApplyVolume();

            _bus = gst_element_get_bus(_playbin);

            // PAUSED pre-rolls: it decodes the first frame and settles duration/track counts without
            // starting playback, which is what lets Opened report real dimensions.
            gst_element_set_state(_playbin, GST_STATE_PAUSED);

            EnsureTimer();
        }

        public void Close()
        {
            StopTimer();
            ReleaseSample();

            if (_playbin != IntPtr.Zero)
            {
                gst_element_set_state(_playbin, GST_STATE_NULL);
                if (_bus != IntPtr.Zero) { gst_object_unref(_bus); _bus = IntPtr.Zero; }
                if (_sink != IntPtr.Zero) { gst_object_unref(_sink); _sink = IntPtr.Zero; }
                gst_object_unref(_playbin);
                _playbin = IntPtr.Zero;
            }

            _opened = false;
            _failed = false;
            _ended = false;
            _width = _height = 0;
            _lengthTicks = 0;
            _buffering = false;
        }

        public void SetRate(double rate)
        {
            _rate = rate;
            if (_playbin == IntPtr.Zero) return;

            if (rate == 0)
            {
                gst_element_set_state(_playbin, GST_STATE_PAUSED);
                return;
            }

            gst_element_set_state(_playbin, GST_STATE_PLAYING);

            // Anything other than 1x needs a seek: GStreamer carries the playback rate in the seek
            // event, not in a property.
            if (rate != 1.0)
            {
                long positionNs = QueryPositionNs();
                gst_element_seek(_playbin, rate, GST_FORMAT_TIME, GST_SEEK_FLAG_FLUSH | GST_SEEK_FLAG_ACCURATE,
                                 GST_SEEK_TYPE_SET, positionNs, GST_SEEK_TYPE_NONE, -1);
            }
        }

        public long GetPositionTicks() => QueryPositionNs() / 100;

        public void SetPositionTicks(long ticks)
        {
            if (_playbin == IntPtr.Zero) return;
            // Seeking away from the end re-arms Ended, so MediaElement's Repeat/Manual replay reports the
            // next end of stream instead of going quiet after the first one.
            _ended = false;
            gst_element_seek_simple(_playbin, GST_FORMAT_TIME,
                                    GST_SEEK_FLAG_FLUSH | GST_SEEK_FLAG_KEY_UNIT, ticks * 100);
        }

        // ------------------------------------------------------- audio / scrubbing ----

        public void SetVolume(double volume)
        {
            _volume = volume;
            ApplyVolume();
        }

        /// <summary>
        /// No-op. playbin exposes no balance knob; doing it properly would mean splicing an audiopanorama
        /// into the audio path, which is more machinery than the property is worth.
        /// </summary>
        public void SetBalance(double balance) { }

        public void SetScrubbingEnabled(bool enabled) { }

        private void ApplyVolume()
        {
            if (_playbin == IntPtr.Zero) return;
            // The value read here is the one the PREVIOUS set settled on, which is the useful number.
            // Reading straight back after a set is not: playbin propagates volume to its playsink
            // asynchronously, so an immediate get often still returns the old value even though the new
            // one did take.
            double previous = s_log ? GetDoubleProperty(_playbin, "volume") : 0;
            SetDoubleProperty(_playbin, "volume", Math.Clamp(_volume, 0.0, 1.0));
            if (s_log) Log($"volume set={_volume:F2} (pipeline was {previous:F2})");
        }

        // --------------------------------------------------------------- frame pull ----

        public void NeedUIFrameUpdate() => PullFrame();

        public bool TryLockFrame(out IntPtr baseAddress, out int width, out int height, out int rowBytes)
        {
            baseAddress = IntPtr.Zero;
            width = _width;
            height = _height;
            rowBytes = 0;

            if (_sample == IntPtr.Zero || _width <= 0 || _height <= 0) return false;

            IntPtr buffer = gst_sample_get_buffer(_sample);
            if (buffer == IntPtr.Zero) return false;

            if (!_frameLocked)
            {
                if (!gst_buffer_map(buffer, out _map, GST_MAP_READ)) return false;
                _frameLocked = true;
            }

            baseAddress = _map.data;
            // Stride is derived rather than assumed: videoconvert may pad rows for alignment, and
            // width*4 would then shear the image. For a single-plane BGRA buffer the mapped size is
            // exactly height*stride, so this recovers the real value.
            rowBytes = _height > 0 ? (int)((long)_map.size / _height) : 0;
            return rowBytes > 0;
        }

        public void UnlockFrame()
        {
            if (!_frameLocked) return;
            _frameLocked = false;
            if (_sample == IntPtr.Zero) return;
            IntPtr buffer = gst_sample_get_buffer(_sample);
            if (buffer != IntPtr.Zero) gst_buffer_unmap(buffer, ref _map);
        }

        // ------------------------------------------------------------ state getters ----

        public bool IsBuffering => _buffering;
        public bool CanPause => true;
        public double DownloadProgress => 1.0;
        public double BufferingProgress { get; private set; } = 1.0;
        public int NaturalVideoWidth => _width;
        public int NaturalVideoHeight => _height;
        public bool HasAudio => GetIntProperty(_playbin, "n-audio") > 0;
        public bool HasVideo => _width > 0 && _height > 0;
        public long MediaLengthTicks => _lengthTicks;

        public event Action FrameAvailable;
        public event Action Opened;
        public event Action Ended;
        public event Action<Exception> Failed;
        public event Action BufferingStarted;
        public event Action BufferingEnded;

        // ------------------------------------------------------------------- pump ----

        private void EnsureTimer()
        {
            if (_timer != null) return;
            // ~60Hz. The appsink is pulled with a zero timeout, so a tick that finds nothing costs a
            // failed try-pull and nothing else.
            _timer = new DispatcherTimer(DispatcherPriority.Normal, _dispatcher)
            {
                Interval = TimeSpan.FromMilliseconds(16),
            };
            _timer.Tick += (_, _) => Tick();
            _timer.Start();
        }

        private void StopTimer()
        {
            if (_timer == null) return;
            _timer.Stop();
            _timer = null;
        }

        private void Tick()
        {
            // This runs on the dispatcher, so an escaping exception is an app crash rather than a broken
            // video. libgstapp is a separate .so from libgstreamer, so a partial GStreamer install throws
            // here rather than at Open. Report it as a media failure once and stop the pump.
            try
            {
                PumpBus();
                PullFrame();
            }
            catch (Exception e)
            {
                StopTimer();
                RaiseFailed(e);
            }
        }

        private void PumpBus()
        {
            if (_bus == IntPtr.Zero) return;

            if (MessageTypeOffset < 0) { PumpBusWithoutMessageType(); return; }

            // ONE pop, with every interesting type in the mask. gst_bus_pop_filtered does not merely
            // skip non-matching messages -- it DISCARDS them. Popping per type therefore destroys the
            // other types: a first pop for ERROR silently threw away the EOS sitting behind the
            // STATE_CHANGED traffic, and playback never reported that it had finished.
            const uint mask = GST_MESSAGE_ERROR | GST_MESSAGE_EOS | GST_MESSAGE_BUFFERING;

            // Drain rather than handling one per tick, so a burst cannot lag behind the 16ms timer.
            // Bounded because the bus is refilled by other threads and an unbounded loop could spin.
            for (int i = 0; i < 32; i++)
            {
                IntPtr message = gst_bus_pop_filtered(_bus, mask);
                if (message == IntPtr.Zero) break;

                uint type = GetMessageType(message);
                if (type == GST_MESSAGE_ERROR)
                {
                    gst_message_parse_error(message, out IntPtr gerror, out IntPtr debug);
                    string text = DescribeGError(gerror);
                    if (gerror != IntPtr.Zero) g_error_free(gerror);
                    if (debug != IntPtr.Zero) g_free(debug);
                    gst_message_unref(message);
                    RaiseFailed(new InvalidOperationException(text));
                    return;
                }

                if (type == GST_MESSAGE_BUFFERING)
                {
                    gst_message_parse_buffering(message, out int percent);
                    gst_message_unref(message);
                    BufferingProgress = percent / 100.0;
                    bool nowBuffering = percent < 100;
                    if (nowBuffering != _buffering)
                    {
                        _buffering = nowBuffering;
                        if (nowBuffering) BufferingStarted?.Invoke(); else BufferingEnded?.Invoke();
                    }
                    continue;
                }

                gst_message_unref(message);
                if (type == GST_MESSAGE_EOS) { RaiseEnded(); return; }
            }

            // appsink deliberately does NOT chain EOS up to the bus (gstappsink.c: "no need to chain up").
            // playbin still posts bus EOS once every sink is done, so the pop above is the primary path;
            // this is the backstop for a sink that goes EOS without the bin aggregating it.
            if (_opened && !_ended && SinkIsEos())
            {
                RaiseEnded();
            }
        }

        // GstMessage.type sits behind GstMiniObject, whose size is an ABI detail worth neither hardcoding
        // nor guessing. Instead the offset is measured once, at runtime, from a message built with a known
        // type -- two distinct probes must agree, so a coincidental match against refcount or flags cannot
        // pass. -1 means unresolved, and the caller falls back to trusting the popped order.
        private static int s_messageTypeOffset = int.MinValue;

        private static int MessageTypeOffset
        {
            get
            {
                if (s_messageTypeOffset == int.MinValue) s_messageTypeOffset = ResolveMessageTypeOffset();
                return s_messageTypeOffset;
            }
        }

        private static uint GetMessageType(IntPtr message) => (uint)Marshal.ReadInt32(message, MessageTypeOffset);

        /// <summary>
        /// Degraded pump for the case where the message type could not be located, which would take a
        /// GstMessage layout change to trigger. Errors win the single pop because they are terminal and a
        /// silent failure is the worst outcome; end-of-stream then rides on <see cref="SinkIsEos"/>, which
        /// covers everything with video but not audio-only media.
        /// </summary>
        private void PumpBusWithoutMessageType()
        {
            IntPtr message = gst_bus_pop_filtered(_bus, GST_MESSAGE_ERROR);
            if (message != IntPtr.Zero)
            {
                gst_message_parse_error(message, out IntPtr gerror, out IntPtr debug);
                string text = DescribeGError(gerror);
                if (gerror != IntPtr.Zero) g_error_free(gerror);
                if (debug != IntPtr.Zero) g_free(debug);
                gst_message_unref(message);
                RaiseFailed(new InvalidOperationException(text));
                return;
            }

            if (_opened && !_ended && SinkIsEos()) RaiseEnded();
        }

        private static int ResolveMessageTypeOffset()
        {
            const uint probeA = 1u << 22;   // GST_MESSAGE_REQUEST_STATE
            const uint probeB = 1u << 19;   // GST_MESSAGE_LATENCY

            IntPtr a = gst_message_new_custom(probeA, IntPtr.Zero, IntPtr.Zero);
            IntPtr b = gst_message_new_custom(probeB, IntPtr.Zero, IntPtr.Zero);
            int found = -1;
            if (a != IntPtr.Zero && b != IntPtr.Zero)
            {
                for (int offset = 8; offset + 4 <= 256; offset += 4)
                {
                    if ((uint)Marshal.ReadInt32(a, offset) == probeA &&
                        (uint)Marshal.ReadInt32(b, offset) == probeB)
                    {
                        found = offset;
                        break;
                    }
                }
            }
            if (a != IntPtr.Zero) gst_message_unref(a);
            if (b != IntPtr.Zero) gst_message_unref(b);

            Log($"GstMessage.type offset = {found}");
            return found;
        }

        /// <summary>
        /// Whether the video appsink has really seen end-of-stream.
        /// <para>
        /// Both guards are load-bearing. gst_app_sink_is_eos also returns TRUE for a sink that is not in
        /// PAUSED or PLAYING, and for audio-only media playbin never links the video sink at all -- so the
        /// bare call reports EOS from the first tick and playback "ends" a second in. No frame ever having
        /// arrived (_width == 0) is what distinguishes that case; the state check covers the transient
        /// while the sink is still coming up. Audio-only correctly falls through to the bus EOS above,
        /// which does fire because the audio sink posts it normally.
        /// </para>
        /// </summary>
        private bool SinkIsEos()
        {
            if (_sink == IntPtr.Zero || _width <= 0) return false;
            gst_element_get_state(_sink, out int state, out _, 0);
            if (state != GST_STATE_PAUSED && state != GST_STATE_PLAYING) return false;
            return gst_app_sink_is_eos(_sink);
        }

        private void PullFrame()
        {
            if (_sink == IntPtr.Zero) return;

            // Zero timeout: never block the UI thread waiting for a decoder.
            IntPtr sample = gst_app_sink_try_pull_sample(_sink, 0);
            if (sample == IntPtr.Zero)
            {
                // No buffers flow until the pipeline is PLAYING -- while it is merely pre-rolled, the only
                // thing the sink holds is the preroll sample, and try_pull_sample does not return it.
                // Pulling it explicitly is what gives MediaOpened real dimensions instead of 0x0, and what
                // paints the first frame of a MediaElement that was opened but never played.
                if (_width <= 0) sample = gst_app_sink_try_pull_preroll(_sink, 0);
                if (sample == IntPtr.Zero)
                {
                    if (!_opened) TryCompleteOpen();
                    return;
                }
            }

            ReleaseSample();
            _sample = sample;
            ReadDimensions(sample);

            if (!_opened) TryCompleteOpen();
            FrameAvailable?.Invoke();
        }

        private void ReadDimensions(IntPtr sample)
        {
            IntPtr caps = gst_sample_get_caps(sample);
            if (caps == IntPtr.Zero) return;
            IntPtr structure = gst_caps_get_structure(caps, 0);
            if (structure == IntPtr.Zero) return;
            if (gst_structure_get_int(structure, "width", out int w) && w > 0) _width = w;
            if (gst_structure_get_int(structure, "height", out int h) && h > 0) _height = h;
        }

        private void TryCompleteOpen()
        {
            if (_opened || _failed || _playbin == IntPtr.Zero) return;

            // Nothing the pipeline reports is trustworthy until it has finished pre-rolling: before that,
            // n-video reads 0 even for a file that plainly has video.
            gst_element_get_state(_playbin, out int state, out int pending, 0);
            if (pending != GST_STATE_VOID_PENDING) return;
            if (state != GST_STATE_PAUSED && state != GST_STATE_PLAYING) return;

            // With video, hold Opened until real dimensions exist. Firing on duration alone raced the
            // first frame and reported MediaOpened as 0x0 with HasVideo false -- wrong for any app that
            // sizes itself from NaturalVideoWidth. Audio-only has no frame to wait for and falls through.
            if (GetIntProperty(_playbin, "n-video") > 0 && _width <= 0) return;

            long durationNs = QueryDurationNs();
            bool haveFrame = _width > 0 && _height > 0;
            if (durationNs <= 0 && !haveFrame) return;

            _lengthTicks = durationNs > 0 ? durationNs / 100 : 0;
            _opened = true;
            Log($"Opened {_width}x{_height} length={_lengthTicks}");
            Opened?.Invoke();

            // A rate requested before the pipeline was ready has to be applied now.
            if (_rate != 0) SetRate(_rate);
        }

        private long QueryPositionNs()
        {
            if (_playbin == IntPtr.Zero) return 0;
            return gst_element_query_position(_playbin, GST_FORMAT_TIME, out long ns) && ns > 0 ? ns : 0;
        }

        private long QueryDurationNs()
        {
            if (_playbin == IntPtr.Zero) return 0;
            return gst_element_query_duration(_playbin, GST_FORMAT_TIME, out long ns) && ns > 0 ? ns : 0;
        }

        private void RaiseEnded()
        {
            if (_ended) return;
            _ended = true;
            Log("EOS");
            Ended?.Invoke();
        }

        private void RaiseFailed(Exception e)
        {
            if (_failed) return;
            _failed = true;
            Log("FAILED " + e.Message);
            Failed?.Invoke(e);
        }

        private void ReleaseSample()
        {
            UnlockFrame();
            if (_sample == IntPtr.Zero) return;
            gst_sample_unref(_sample);
            _sample = IntPtr.Zero;
        }

        public void Dispose() => Close();

        // ------------------------------------------------------------------ interop ----

        private const string LibGst = "libgstreamer-1.0.so.0";
        private const string LibGstApp = "libgstapp-1.0.so.0";
        private const string LibGObject = "libgobject-2.0.so.0";
        private const string LibGLib = "libglib-2.0.so.0";

        private const int GST_STATE_VOID_PENDING = 0;
        private const int GST_STATE_NULL = 1;
        private const int GST_STATE_PAUSED = 3;
        private const int GST_STATE_PLAYING = 4;
        private const int GST_FORMAT_TIME = 3;

        private const int GST_SEEK_FLAG_FLUSH = 1 << 0;
        private const int GST_SEEK_FLAG_ACCURATE = 1 << 1;
        private const int GST_SEEK_FLAG_KEY_UNIT = 1 << 2;
        private const int GST_SEEK_TYPE_NONE = 0;
        private const int GST_SEEK_TYPE_SET = 1;

        private const uint GST_MESSAGE_EOS = 1 << 0;
        private const uint GST_MESSAGE_ERROR = 1 << 1;
        private const uint GST_MESSAGE_BUFFERING = 1 << 5;

        private const int GST_MAP_READ = 1;

        // Fundamental GType ids are (n << 2); see glib's gtype.h.
        private const nuint G_TYPE_INT = 6 << 2;
        private const nuint G_TYPE_DOUBLE = 15 << 2;
        private const nuint G_TYPE_STRING = 16 << 2;
        private const nuint G_TYPE_OBJECT = 20 << 2;

        /// <summary>GstMapInfo. Larger than the fields used: it carries four padding pointers that
        /// gst_buffer_map writes, so a short struct would be a buffer overrun.</summary>
        [StructLayout(LayoutKind.Sequential)]
        private struct GstMapInfo
        {
            public IntPtr memory;
            public int flags;
            private int _pad;
            public IntPtr data;
            public nuint size;
            public nuint maxsize;
            public IntPtr user0, user1, user2, user3;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct GValue
        {
            public nuint gType;
            public long data0, data1;
        }

        private static bool s_gstReady;
        private static bool s_gstUnavailable;

        private static bool EnsureGst()
        {
            if (s_gstReady) return true;
            if (s_gstUnavailable) return false;
            try
            {
                gst_init(IntPtr.Zero, IntPtr.Zero);
                s_gstReady = true;
            }
            catch (DllNotFoundException)
            {
                Log("libgstreamer-1.0 not found");
                s_gstUnavailable = true;
            }
            return s_gstReady;
        }

        private static string DescribeGError(IntPtr gerror)
        {
            if (gerror == IntPtr.Zero) return "GStreamer reported an error.";
            // GError is { GQuark domain; gint code; gchar *message; } -- the message pointer sits
            // after two 4-byte fields, so at offset 8 on every platform this builds for.
            IntPtr message = Marshal.ReadIntPtr(gerror, 8);
            string text = message != IntPtr.Zero ? Marshal.PtrToStringUTF8(message) : null;
            return string.IsNullOrEmpty(text) ? "GStreamer reported an error." : text;
        }

        private static void SetStringProperty(IntPtr obj, string name, string value)
        {
            var v = new GValue { gType = 0 };
            g_value_init(ref v, G_TYPE_STRING);
            IntPtr utf8 = Marshal.StringToCoTaskMemUTF8(value);
            try
            {
                g_value_set_string(ref v, utf8);
                g_object_set_property(obj, name, ref v);
            }
            finally
            {
                g_value_unset(ref v);
                Marshal.FreeCoTaskMem(utf8);
            }
        }

        private static void SetDoubleProperty(IntPtr obj, string name, double value)
        {
            var v = new GValue { gType = 0 };
            g_value_init(ref v, G_TYPE_DOUBLE);
            g_value_set_double(ref v, value);
            g_object_set_property(obj, name, ref v);
            g_value_unset(ref v);
        }

        private static void SetObjectProperty(IntPtr obj, string name, IntPtr value)
        {
            var v = new GValue { gType = 0 };
            g_value_init(ref v, G_TYPE_OBJECT);
            g_value_set_object(ref v, value);
            g_object_set_property(obj, name, ref v);
            g_value_unset(ref v);
        }

        private static double GetDoubleProperty(IntPtr obj, string name)
        {
            if (obj == IntPtr.Zero) return 0;
            var v = new GValue { gType = 0 };
            g_value_init(ref v, G_TYPE_DOUBLE);
            g_object_get_property(obj, name, ref v);
            double result = g_value_get_double(ref v);
            g_value_unset(ref v);
            return result;
        }

        private static int GetIntProperty(IntPtr obj, string name)
        {
            if (obj == IntPtr.Zero) return 0;
            var v = new GValue { gType = 0 };
            g_value_init(ref v, G_TYPE_INT);
            g_object_get_property(obj, name, ref v);
            int result = g_value_get_int(ref v);
            g_value_unset(ref v);
            return result;
        }

        [DllImport(LibGst)] private static extern void gst_init(IntPtr argc, IntPtr argv);
        [DllImport(LibGst)] private static extern IntPtr gst_element_factory_make([MarshalAs(UnmanagedType.LPUTF8Str)] string factory, [MarshalAs(UnmanagedType.LPUTF8Str)] string name);
        [DllImport(LibGst)] private static extern IntPtr gst_parse_bin_from_description([MarshalAs(UnmanagedType.LPUTF8Str)] string description, [MarshalAs(UnmanagedType.I1)] bool ghostUnlinkedPads, ref IntPtr error);
        [DllImport(LibGst)] private static extern IntPtr gst_bin_get_by_name(IntPtr bin, [MarshalAs(UnmanagedType.LPUTF8Str)] string name);
        [DllImport(LibGst)] private static extern void gst_object_unref(IntPtr obj);
        [DllImport(LibGst)] private static extern int gst_element_set_state(IntPtr element, int state);
        [DllImport(LibGst)] private static extern IntPtr gst_element_get_bus(IntPtr element);
        [DllImport(LibGst)] private static extern int gst_element_get_state(IntPtr element, out int state, out int pending, ulong timeoutNs);

        [DllImport(LibGst)] [return: MarshalAs(UnmanagedType.I1)]
        private static extern bool gst_element_query_position(IntPtr element, int format, out long cur);

        [DllImport(LibGst)] [return: MarshalAs(UnmanagedType.I1)]
        private static extern bool gst_element_query_duration(IntPtr element, int format, out long duration);

        [DllImport(LibGst)] [return: MarshalAs(UnmanagedType.I1)]
        private static extern bool gst_element_seek_simple(IntPtr element, int format, int flags, long position);

        [DllImport(LibGst)] [return: MarshalAs(UnmanagedType.I1)]
        private static extern bool gst_element_seek(IntPtr element, double rate, int format, int flags,
                                                    int startType, long start, int stopType, long stop);

        [DllImport(LibGst)] private static extern IntPtr gst_bus_pop_filtered(IntPtr bus, uint types);
        [DllImport(LibGst)] private static extern void gst_message_unref(IntPtr message);
        [DllImport(LibGst)] private static extern IntPtr gst_message_new_custom(uint type, IntPtr src, IntPtr structure);
        [DllImport(LibGst)] private static extern void gst_message_parse_error(IntPtr message, out IntPtr error, out IntPtr debug);
        [DllImport(LibGst)] private static extern void gst_message_parse_buffering(IntPtr message, out int percent);

        [DllImport(LibGst)] private static extern IntPtr gst_sample_get_buffer(IntPtr sample);
        [DllImport(LibGst)] private static extern IntPtr gst_sample_get_caps(IntPtr sample);
        [DllImport(LibGst)] private static extern void gst_sample_unref(IntPtr sample);
        [DllImport(LibGst)] private static extern IntPtr gst_caps_get_structure(IntPtr caps, uint index);

        [DllImport(LibGst)] [return: MarshalAs(UnmanagedType.I1)]
        private static extern bool gst_structure_get_int(IntPtr structure, [MarshalAs(UnmanagedType.LPUTF8Str)] string field, out int value);

        [DllImport(LibGst)] [return: MarshalAs(UnmanagedType.I1)]
        private static extern bool gst_buffer_map(IntPtr buffer, out GstMapInfo info, int flags);

        [DllImport(LibGst)] private static extern void gst_buffer_unmap(IntPtr buffer, ref GstMapInfo info);

        [DllImport(LibGstApp)] private static extern IntPtr gst_app_sink_try_pull_sample(IntPtr appsink, ulong timeoutNs);
        [DllImport(LibGstApp)] private static extern IntPtr gst_app_sink_try_pull_preroll(IntPtr appsink, ulong timeoutNs);

        [DllImport(LibGstApp)] [return: MarshalAs(UnmanagedType.I1)]
        private static extern bool gst_app_sink_is_eos(IntPtr appsink);

        [DllImport(LibGObject)] private static extern IntPtr g_value_init(ref GValue value, nuint gType);
        [DllImport(LibGObject)] private static extern void g_value_unset(ref GValue value);
        [DllImport(LibGObject)] private static extern void g_value_set_double(ref GValue value, double v);
        [DllImport(LibGObject)] private static extern void g_value_set_string(ref GValue value, IntPtr utf8);
        [DllImport(LibGObject)] private static extern void g_value_set_object(ref GValue value, IntPtr obj);
        [DllImport(LibGObject)] private static extern int g_value_get_int(ref GValue value);
        [DllImport(LibGObject)] private static extern double g_value_get_double(ref GValue value);
        [DllImport(LibGObject)] private static extern void g_object_set_property(IntPtr obj, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, ref GValue value);
        [DllImport(LibGObject)] private static extern void g_object_get_property(IntPtr obj, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, ref GValue value);

        [DllImport(LibGLib)] private static extern void g_error_free(IntPtr error);
        [DllImport(LibGLib)] private static extern void g_free(IntPtr mem);
    }
}
