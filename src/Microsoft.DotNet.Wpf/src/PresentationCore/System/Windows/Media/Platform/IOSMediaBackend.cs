// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// IOSMediaBackend -- the iOS/iPadOS IMediaBackend, backed by AVFoundation.
//
// Structurally the same engine as MacMediaBackend: AVPlayer decodes audio+video and owns the master A/V
// clock (hardware decode via VideoToolbox), an AVPlayerItemVideoOutput requesting 32BGRA is the frame tap,
// and a DispatcherTimer on the media dispatcher polls item status (-> Opened/Failed) and pulls the current
// CVPixelBuffer, raising FrameAvailable so MediaPlayer.UpdateResource can ship its BGRA pixels to the
// WebGPU compositor through the SendVideoFrame seam. It is a sibling rather than a shared base class, the
// same way UIKitWindow is a sibling of CocoaWindow: the two frameworks agree today and diverge exactly
// where iOS needs it, and each backend stays readable and debuggable on its own.
//
// Three things really are iOS-only, and they are why this file exists at all:
//
//   * AVAudioSession. Every macOS AVPlayer just plays. On iOS the process starts in the default
//     SoloAmbient category, which is silenced by the ring/silent switch and by screen lock -- video
//     would run with no audio at all and nothing would report an error. The session is moved to
//     Playback and activated on the first Open.
//   * libobjc is /usr/lib/libobjc.dylib here, not the macOS /usr/lib/libobjc.A.dylib (see UIKitWindow).
//   * AVAudioSession lives in AVFAudio.framework, which AVFoundation re-exports; both are dlopen'd so
//     the class is registered whichever way the runtime has laid them out.
//
// Interop follows the fork's convention (WgpuInterop MacInterop.cs, UIKitWindow.cs): dlopen full framework
// paths, one typed objc_msgSend alias per call shape. CMTime is a 24-byte by-value struct; on arm64 the
// plain objc_msgSend entry handles large struct returns and arguments, so these aliases are correct for
// both the device and the Apple Silicon simulator.
//

using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Windows.Threading;

namespace System.Windows.Media
{
    [SupportedOSPlatform("ios")]
    internal sealed class IOSMediaBackend : IMediaBackend
    {
        private readonly Dispatcher _dispatcher;
        private DispatcherTimer _timer;

        private IntPtr _player;          // AVPlayer*
        private IntPtr _item;            // AVPlayerItem*
        private IntPtr _output;          // AVPlayerItemVideoOutput*

        private IntPtr _frame;           // current CVPixelBufferRef (owned; released on replace/dispose)
        private bool _frameLocked;

        private bool _opened;
        private bool _failed;
        private bool _ended;
        private bool _hasAudio;
        private int _width, _height;
        private long _lengthTicks;
        private double _rate;            // last requested rate (0 = paused)
        private double _volume = 0.5;    // mute is applied by MediaPlayerState as SetVolume(0), so no separate flag

        private static readonly bool s_log = Environment.GetEnvironmentVariable("WPF_MEDIA_LOG") == "1";
        private static void Log(string m) { if (s_log) Console.WriteLine("MEDIA " + m); }

        internal IOSMediaBackend(MediaPlayer player)
        {
            _dispatcher = player.Dispatcher;
        }

        // ---------------------------------------------------------------- transport ----

        public void Open(string url)
        {
            Log($"Open {url}");
            EnsureLoaded();
            Close();   // tear down any previous item

            // Before the first item: without this the app is still in the default SoloAmbient category and
            // the ring/silent switch mutes playback with no error anywhere.
            EnsurePlaybackAudioSession();

            IntPtr nsUrl = MakeUrl(url);
            if (nsUrl == IntPtr.Zero) { RaiseFailed(new System.IO.FileNotFoundException(url)); return; }

            _item = Retain(SendPtr(Cls("AVPlayerItem"), Sel("playerItemWithURL:"), nsUrl));

            // 32BGRA frame tap.
            IntPtr attrs = MakePixelFormatAttrs(kCVPixelFormatType_32BGRA);
            IntPtr output = Send(Cls("AVPlayerItemVideoOutput"), Sel("alloc"));
            _output = SendPtr(output, Sel("initWithPixelBufferAttributes:"), attrs);
            SendVoidPtr(_item, Sel("addOutput:"), _output);

            _player = Retain(SendPtr(Cls("AVPlayer"), Sel("playerWithPlayerItem:"), _item));
            // Play immediately for local files instead of waiting to minimize stalling (which defers a
            // setRate: issued before the item is ready).
            SendVoidBool(_player, Sel("setAutomaticallyWaitsToMinimizeStalling:"), false);
            ApplyVolume();

            StartTimer();
        }

        public void Close()
        {
            StopTimer();
            UnlockFrame();
            if (_frame != IntPtr.Zero) { CVBufferRelease(_frame); _frame = IntPtr.Zero; }
            if (_player != IntPtr.Zero) { SendVoidFloat(_player, Sel("setRate:"), 0f); Release(_player); _player = IntPtr.Zero; }
            if (_output != IntPtr.Zero) { Release(_output); _output = IntPtr.Zero; }
            if (_item != IntPtr.Zero) { Release(_item); _item = IntPtr.Zero; }
            _opened = _failed = _ended = _hasAudio = false;
            _width = _height = 0;
            _lengthTicks = 0;
        }

        public void SetRate(double rate)
        {
            Log($"SetRate {rate}");
            _rate = rate;
            if (_player != IntPtr.Zero)
            {
                SendVoidFloat(_player, Sel("setRate:"), (float)rate);   // AVPlayer.rate is a float
            }
        }

        public long GetPositionTicks()
        {
            if (_item == IntPtr.Zero) return 0;
            double secs = CMTimeGetSeconds(SendCMTime(_item, Sel("currentTime")));
            if (double.IsNaN(secs) || secs < 0) return 0;
            return (long)(secs * TimeSpan.TicksPerSecond);
        }

        public void SetPositionTicks(long ticks)
        {
            Log($"SetPosition {(double)ticks / TimeSpan.TicksPerSecond:F3}s");
            if (_player == IntPtr.Zero) return;
            if (ticks < _lengthTicks - TimeSpan.TicksPerSecond / 4) _ended = false;   // seeking back re-arms Ended
            CMTime t = CMTimeMakeWithSeconds((double)ticks / TimeSpan.TicksPerSecond, 600);
            SendVoidCMTime(_player, Sel("seekToTime:"), t);
        }

        // ---------------------------------------------------------------- audio ----

        public void SetVolume(double volume) { Log($"SetVolume {volume}"); _volume = volume; ApplyVolume(); }
        public void SetBalance(double balance) { /* AVPlayer has no balance knob; no-op (would need an AVAudioMix). */ }
        public void SetScrubbingEnabled(bool enabled) { /* one-shot frame after seek handled by the timer. */ }
        public void NeedUIFrameUpdate() { /* frames are driven by the timer, not a per-pass reserve. */ }

        private void ApplyVolume()
        {
            if (_player == IntPtr.Zero) return;
            SendVoidFloat(_player, Sel("setVolume:"), (float)_volume);
        }

        /// <summary>
        /// Moves the process audio session to Playback and activates it, once. iOS starts every app in
        /// SoloAmbient, where the hardware mute switch silences playback and nothing reports a failure --
        /// video plays perfectly and silently. Failures here are logged and ignored: a session the OS
        /// refuses (a call in progress, say) must not turn into MediaFailed for the video.
        /// </summary>
        private static void EnsurePlaybackAudioSession()
        {
            if (s_audioSessionConfigured) return;
            s_audioSessionConfigured = true;

            IntPtr cls = Cls("AVAudioSession");
            if (cls == IntPtr.Zero) { Log("AVAudioSession unavailable"); return; }

            IntPtr session = Send(cls, Sel("sharedInstance"));
            if (session == IntPtr.Zero) return;

            IntPtr category = s_categoryPlayback != IntPtr.Zero
                ? s_categoryPlayback
                // The exported constant's value is its own name; if dlsym could not find the symbol the
                // literal is still the documented category identifier.
                : SendPtrUtf8(Cls("NSString"), Sel("stringWithUTF8String:"), "AVAudioSessionCategoryPlayback");

            bool ok = SendBoolPtrPtr(session, Sel("setCategory:error:"), category, IntPtr.Zero);
            bool active = SendBoolBoolPtr(session, Sel("setActive:error:"), true, IntPtr.Zero);
            Log($"AVAudioSession playback category={ok} active={active}");
        }

        // ---------------------------------------------------------------- state ----

        public bool IsBuffering => false;
        public bool CanPause => true;
        public double DownloadProgress => _opened ? 1.0 : 0.0;
        public double BufferingProgress => _opened ? 1.0 : 0.0;
        public int NaturalVideoWidth => _width;
        public int NaturalVideoHeight => _height;
        public bool HasAudio => _hasAudio;
        public bool HasVideo => _width > 0;
        public long MediaLengthTicks => _lengthTicks;

        // ---------------------------------------------------------------- frame pull ----

        public bool TryLockFrame(out IntPtr baseAddress, out int width, out int height, out int rowBytes)
        {
            baseAddress = IntPtr.Zero; width = 0; height = 0; rowBytes = 0;
            if (_frame == IntPtr.Zero) return false;

            const ulong kReadOnly = 1; // kCVPixelBufferLock_ReadOnly
            if (CVPixelBufferLockBaseAddress(_frame, kReadOnly) != 0) return false;
            _frameLocked = true;
            baseAddress = CVPixelBufferGetBaseAddress(_frame);
            width = (int)CVPixelBufferGetWidth(_frame);
            height = (int)CVPixelBufferGetHeight(_frame);
            rowBytes = (int)CVPixelBufferGetBytesPerRow(_frame);
            return baseAddress != IntPtr.Zero;
        }

        public void UnlockFrame()
        {
            if (_frameLocked && _frame != IntPtr.Zero)
            {
                const ulong kReadOnly = 1;
                CVPixelBufferUnlockBaseAddress(_frame, kReadOnly);
                _frameLocked = false;
            }
        }

        // ---------------------------------------------------------------- clock ----

        private void StartTimer()
        {
            StopTimer();
            _timer = new DispatcherTimer(DispatcherPriority.Render, _dispatcher)
            {
                Interval = TimeSpan.FromMilliseconds(15)   // ~60 Hz; frame pull is cheap when no new frame
            };
            _timer.Tick += OnTick;
            _timer.Start();
        }

        private void StopTimer()
        {
            if (_timer != null) { _timer.Stop(); _timer.Tick -= OnTick; _timer = null; }
        }

        private void OnTick(object sender, EventArgs e)
        {
            if (_item == IntPtr.Zero) return;

            if (!_opened && !_failed)
            {
                nint status = SendNInt(_item, Sel("status"));   // 0 Unknown, 1 ReadyToPlay, 2 Failed
                if (status == 2)
                {
                    _failed = true;
                    RaiseFailed(new System.NotSupportedException("AVFoundation could not open the media (unsupported format?)."));
                    return;
                }
                if (status == 1)
                {
                    CGSize sz = SendSize(_item, Sel("presentationSize"));
                    _width = (int)Math.Round(sz.width);
                    _height = (int)Math.Round(sz.height);
                    double durSecs = CMTimeGetSeconds(SendCMTime(_item, Sel("duration")));
                    _lengthTicks = (double.IsNaN(durSecs) || durSecs <= 0) ? 0 : (long)(durSecs * TimeSpan.TicksPerSecond);
                    // HasAudio: does the asset carry any audio track?
                    if (s_avMediaTypeAudio != IntPtr.Zero)
                    {
                        IntPtr asset = Send(_item, Sel("asset"));
                        IntPtr audio = asset != IntPtr.Zero ? SendPtr(asset, Sel("tracksWithMediaType:"), s_avMediaTypeAudio) : IntPtr.Zero;
                        _hasAudio = audio != IntPtr.Zero && SendNInt(audio, Sel("count")) > 0;
                    }
                    _opened = true;
                    Log($"Opened {_width}x{_height} dur={durSecs:F1}s rate={_rate}");
                    // Re-apply the requested rate now that the item is ready (an earlier setRate: before
                    // ReadyToPlay may not have started playback).
                    if (_rate != 0) SendVoidFloat(_player, Sel("setRate:"), (float)_rate);
                    Opened?.Invoke();
                }
            }

            if (_opened)
            {
                PullFrame();

                // End-of-media: AVPlayer stops at duration (rate -> 0). Raise Ended once when we reach it.
                if (!_ended && _lengthTicks > 0 &&
                    GetPositionTicks() >= _lengthTicks - TimeSpan.TicksPerSecond / 4 &&
                    SendNInt(_player, Sel("timeControlStatus")) == 0)
                {
                    _ended = true;
                    Log("Ended");
                    Ended?.Invoke();
                }
            }
        }

        private void PullFrame()
        {
            CMTime itemTime = SendCMTime(_item, Sel("currentTime"));
            if (!SendBoolCMTime(_output, Sel("hasNewPixelBufferForItemTime:"), itemTime)) return;

            IntPtr pb = SendPtrCMTimePtr(_output, Sel("copyPixelBufferForItemTime:itemTimeForDisplay:"), itemTime, IntPtr.Zero);
            if (pb == IntPtr.Zero) return;

            // Replace the current frame (copyPixelBuffer returns a +1 retain we own).
            if (_frame != IntPtr.Zero) { UnlockFrame(); CVBufferRelease(_frame); }
            _frame = pb;
            FrameAvailable?.Invoke();
        }

        // ---------------------------------------------------------------- events ----

        public event Action FrameAvailable;
        public event Action Opened;
        public event Action Ended;
        public event Action<Exception> Failed;
        public event Action BufferingStarted;
        public event Action BufferingEnded;

        private void RaiseFailed(Exception ex) => Failed?.Invoke(ex);

        public void Dispose() => Close();

        // ================================================================ interop ====

        // iOS ships libobjc under its unsuffixed name; the macOS backend's /usr/lib/libobjc.A.dylib does
        // not exist here (see UIKitWindow.cs, which made the same distinction).
        private const string ObjC = "/usr/lib/libobjc.dylib";
        private const string CoreMedia = "/System/Library/Frameworks/CoreMedia.framework/CoreMedia";
        private const string CoreVideo = "/System/Library/Frameworks/CoreVideo.framework/CoreVideo";
        private const int RTLD_NOW = 2;
        private const uint kCVPixelFormatType_32BGRA = 0x42475241; // 'BGRA'

        private static bool s_loaded;
        private static bool s_audioSessionConfigured;
        private static IntPtr s_pixelFormatKey;    // kCVPixelBufferPixelFormatTypeKey (CFStringRef)
        private static IntPtr s_avMediaTypeAudio;  // AVMediaTypeAudio (NSString*)
        private static IntPtr s_categoryPlayback;  // AVAudioSessionCategoryPlayback (NSString*)

        private static void EnsureLoaded()
        {
            if (s_loaded) return;
            dlopen("/System/Library/Frameworks/Foundation.framework/Foundation", RTLD_NOW);
            IntPtr av = dlopen("/System/Library/Frameworks/AVFoundation.framework/AVFoundation", RTLD_NOW);
            // AVAudioSession is AVFAudio's, re-exported by AVFoundation. Loading it explicitly costs
            // nothing and keeps the session code working whichever framework owns the class.
            IntPtr avf = dlopen("/System/Library/Frameworks/AVFAudio.framework/AVFAudio", RTLD_NOW);
            IntPtr cv = dlopen(CoreVideo, RTLD_NOW);
            dlopen(CoreMedia, RTLD_NOW);
            // These framework constants are exported CFStringRef/NSString* variables; read their values.
            IntPtr keyAddr = cv != IntPtr.Zero ? dlsym(cv, "kCVPixelBufferPixelFormatTypeKey") : IntPtr.Zero;
            s_pixelFormatKey = keyAddr != IntPtr.Zero ? Marshal.ReadIntPtr(keyAddr) : IntPtr.Zero;
            IntPtr audAddr = av != IntPtr.Zero ? dlsym(av, "AVMediaTypeAudio") : IntPtr.Zero;
            s_avMediaTypeAudio = audAddr != IntPtr.Zero ? Marshal.ReadIntPtr(audAddr) : IntPtr.Zero;
            IntPtr catAddr = avf != IntPtr.Zero ? dlsym(avf, "AVAudioSessionCategoryPlayback") : IntPtr.Zero;
            if (catAddr == IntPtr.Zero && av != IntPtr.Zero) catAddr = dlsym(av, "AVAudioSessionCategoryPlayback");
            s_categoryPlayback = catAddr != IntPtr.Zero ? Marshal.ReadIntPtr(catAddr) : IntPtr.Zero;
            s_loaded = true;
        }

        private static IntPtr MakeUrl(string url)
        {
            IntPtr nsStr = SendPtrUtf8(Cls("NSString"), Sel("stringWithUTF8String:"), url);
            if (nsStr == IntPtr.Zero) return IntPtr.Zero;
            bool isHttp = url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                       || url.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
            return SendPtr(Cls("NSURL"), Sel(isHttp ? "URLWithString:" : "fileURLWithPath:"), nsStr);
        }

        private static IntPtr MakePixelFormatAttrs(uint fourcc)
        {
            if (s_pixelFormatKey == IntPtr.Zero) return IntPtr.Zero;
            IntPtr num = SendPtrInt(Cls("NSNumber"), Sel("numberWithUnsignedInt:"), fourcc);
            return SendPtrPtrPtr(Cls("NSDictionary"), Sel("dictionaryWithObject:forKey:"), num, s_pixelFormatKey);
        }

        private static IntPtr Cls(string name) => objc_getClass(name);
        private static IntPtr Sel(string name) => sel_registerName(name);
        private static IntPtr Retain(IntPtr o) { if (o != IntPtr.Zero) Send(o, Sel("retain")); return o; }
        private static void Release(IntPtr o) { if (o != IntPtr.Zero) Send(o, Sel("release")); }

        [DllImport(ObjC)] private static extern IntPtr objc_getClass(string name);
        [DllImport(ObjC)] private static extern IntPtr sel_registerName(string name);
        [DllImport("/usr/lib/libSystem.dylib")] private static extern IntPtr dlopen(string path, int mode);
        [DllImport("/usr/lib/libSystem.dylib")] private static extern IntPtr dlsym(IntPtr handle, string symbol);

        // objc_msgSend typed aliases (one per call shape).
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr Send(IntPtr r, IntPtr s);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr SendPtr(IntPtr r, IntPtr s, IntPtr a);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr SendPtrPtrPtr(IntPtr r, IntPtr s, IntPtr a, IntPtr b);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr SendPtrInt(IntPtr r, IntPtr s, uint a);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr SendPtrUtf8(IntPtr r, IntPtr s, [MarshalAs(UnmanagedType.LPUTF8Str)] string a);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern void SendVoidPtr(IntPtr r, IntPtr s, IntPtr a);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern void SendVoidFloat(IntPtr r, IntPtr s, float a);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern void SendVoidBool(IntPtr r, IntPtr s, [MarshalAs(UnmanagedType.I1)] bool a);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] [return: MarshalAs(UnmanagedType.I1)] private static extern bool SendBoolPtrPtr(IntPtr r, IntPtr s, IntPtr a, IntPtr b);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] [return: MarshalAs(UnmanagedType.I1)] private static extern bool SendBoolBoolPtr(IntPtr r, IntPtr s, [MarshalAs(UnmanagedType.I1)] bool a, IntPtr b);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern nint SendNInt(IntPtr r, IntPtr s);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern CGSize SendSize(IntPtr r, IntPtr s);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern CMTime SendCMTime(IntPtr r, IntPtr s);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern void SendVoidCMTime(IntPtr r, IntPtr s, CMTime a);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] [return: MarshalAs(UnmanagedType.I1)] private static extern bool SendBoolCMTime(IntPtr r, IntPtr s, CMTime a);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr SendPtrCMTimePtr(IntPtr r, IntPtr s, CMTime a, IntPtr b);

        // CoreMedia (C, by-value CMTime).
        [DllImport(CoreMedia)] private static extern double CMTimeGetSeconds(CMTime time);
        [DllImport(CoreMedia)] private static extern CMTime CMTimeMakeWithSeconds(double seconds, int preferredTimescale);

        // CoreVideo (C).
        [DllImport(CoreVideo)] private static extern int CVPixelBufferLockBaseAddress(IntPtr pb, ulong flags);
        [DllImport(CoreVideo)] private static extern int CVPixelBufferUnlockBaseAddress(IntPtr pb, ulong flags);
        [DllImport(CoreVideo)] private static extern IntPtr CVPixelBufferGetBaseAddress(IntPtr pb);
        [DllImport(CoreVideo)] private static extern nuint CVPixelBufferGetBytesPerRow(IntPtr pb);
        [DllImport(CoreVideo)] private static extern nuint CVPixelBufferGetWidth(IntPtr pb);
        [DllImport(CoreVideo)] private static extern nuint CVPixelBufferGetHeight(IntPtr pb);
        [DllImport(CoreVideo)] private static extern void CVBufferRelease(IntPtr buffer);

        [StructLayout(LayoutKind.Sequential)]
        private struct CMTime
        {
            public long value;
            public int timescale;
            public uint flags;     // bit0 = Valid
            public long epoch;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct CGSize
        {
            public double width;
            public double height;
        }
    }
}
