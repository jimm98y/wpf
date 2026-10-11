// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// BrowserMediaBackend -- the WebAssembly IMediaBackend, backed by an HTML5 <video> element.
//
// Parallel to the macOS AVFoundation backend: the browser decodes audio+video, outputs audio, and keeps
// A/V in sync; a DispatcherTimer on the media dispatcher polls readiness (-> Opened/Failed), pulls the
// current frame's pixels each tick (drawImage the <video> to an offscreen canvas -> getImageData, done in
// browser-media.js) into a pinned managed buffer, and raises FrameAvailable. MediaPlayer.UpdateResource
// then ships those BGRA pixels to the managed WebGPU compositor via the same SendVideoFrame seam macOS uses.
// The JS side is the 'wpfBrowserMedia' module (browser-media.js), registered in the app bootstrap.
//

using System.Runtime.InteropServices;
using System.Runtime.InteropServices.JavaScript;
using System.Runtime.Versioning;
using System.Windows.Threading;

namespace System.Windows.Media
{
    [SupportedOSPlatform("browser")]
    internal sealed partial class BrowserMediaBackend : IMediaBackend
    {
        private static int s_nextHandle = 1;

        private readonly Dispatcher _dispatcher;
        private readonly int _handle;
        private DispatcherTimer _timer;

        private byte[] _frameBuffer;    // POH-pinned BGRA frame (stable address for TryLockFrame)
        private IntPtr _frameAddr;
        private int _width, _height;
        private bool _hasFrame;

        private bool _opened, _failed, _ended, _hasAudio;
        private long _lengthTicks;
        private double _rate, _volume = 0.5;

        internal BrowserMediaBackend(MediaPlayer player)
        {
            _dispatcher = player.Dispatcher;
            _handle = s_nextHandle++;
            Js.CreateVideo(_handle);
        }

        // ---------------------------------------------------------------- transport ----

        public void Open(string url)
        {
            _opened = _failed = _ended = _hasAudio = _hasFrame = false;
            _width = _height = 0;
            _lengthTicks = 0;
            Js.Open(_handle, url);
            ApplyVolume();
            StartTimer();
        }

        public void Close()
        {
            StopTimer();
            // Pause + clear the source but keep the element for reuse on a new Open.
            Js.SetRate(_handle, 0);
            _opened = _failed = _ended = _hasAudio = _hasFrame = false;
            _width = _height = 0;
            _lengthTicks = 0;
        }

        public void SetRate(double rate) { _rate = rate; Js.SetRate(_handle, rate); }
        public long GetPositionTicks() => (long)(Js.GetPosition(_handle) * TimeSpan.TicksPerSecond);
        public void SetPositionTicks(long ticks)
        {
            double secs = (double)ticks / TimeSpan.TicksPerSecond;
            if (ticks < _lengthTicks - TimeSpan.TicksPerSecond / 4) _ended = false;
            Js.Seek(_handle, secs);
        }

        // ---------------------------------------------------------------- audio ----

        public void SetVolume(double volume) { _volume = volume; ApplyVolume(); }
        public void SetBalance(double balance) { /* HTML5 <video> has no balance; would need a WebAudio graph. */ }
        public void SetScrubbingEnabled(bool enabled) { /* seeking a paused video shows a frame; the timer pulls it. */ }
        public void NeedUIFrameUpdate() { /* frames are driven by the timer, not a per-pass reserve. */ }
        private void ApplyVolume() => Js.SetVolume(_handle, _volume);

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
            baseAddress = _frameAddr; width = _width; height = _height; rowBytes = _width * 4;
            return _hasFrame && _frameAddr != IntPtr.Zero && _width > 0 && _height > 0;
        }

        public void UnlockFrame() { /* managed pinned buffer; nothing to unlock. */ }

        // ---------------------------------------------------------------- clock ----

        private void StartTimer()
        {
            StopTimer();
            _timer = new DispatcherTimer(DispatcherPriority.Render, _dispatcher)
            {
                Interval = TimeSpan.FromMilliseconds(15)   // ~60 Hz
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
            if (!_opened && !_failed)
            {
                int status = Js.GetStatus(_handle);   // 0 loading, 1 ready, 2 error
                if (status == 2)
                {
                    _failed = true;
                    Failed?.Invoke(new System.NotSupportedException("The browser could not load the media (unsupported format?)."));
                    return;
                }
                if (status == 1)
                {
                    _width = Js.GetWidth(_handle);
                    _height = Js.GetHeight(_handle);
                    double durSecs = Js.GetDuration(_handle);
                    _lengthTicks = durSecs > 0 ? (long)(durSecs * TimeSpan.TicksPerSecond) : 0;
                    _hasAudio = Js.HasAudio(_handle);
                    if (_width > 0 && _height > 0)
                    {
                        _frameBuffer = GC.AllocateArray<byte>(_width * _height * 4, pinned: true);
                        _frameAddr = Marshal.UnsafeAddrOfPinnedArrayElement(_frameBuffer, 0);
                    }
                    _opened = true;
                    Opened?.Invoke();
                }
            }

            if (_opened)
            {
                if (_frameBuffer != null && Js.GetFrame(_handle, _frameBuffer) != 0)
                {
                    _hasFrame = true;
                    FrameAvailable?.Invoke();
                }

                if (!_ended && Js.IsEnded(_handle))
                {
                    _ended = true;
                    Ended?.Invoke();
                }
            }
        }

        // ---------------------------------------------------------------- events / dispose ----

        public event Action FrameAvailable;
        public event Action Opened;
        public event Action Ended;
        public event Action<Exception> Failed;
        public event Action BufferingStarted;
        public event Action BufferingEnded;

        public void Dispose()
        {
            StopTimer();
            Js.Destroy(_handle);
            _frameBuffer = null;
            _frameAddr = IntPtr.Zero;
        }

        // ================================================================ JS interop ====

        private static partial class Js
        {
            private const string Module = "wpfBrowserMedia";

            [JSImport("createVideo", Module)] internal static partial void CreateVideo(int handle);
            [JSImport("open", Module)] internal static partial void Open(int handle, string url);
            [JSImport("setRate", Module)] internal static partial void SetRate(int handle, double rate);
            [JSImport("seek", Module)] internal static partial void Seek(int handle, double seconds);
            [JSImport("setVolume", Module)] internal static partial void SetVolume(int handle, double volume);
            [JSImport("getStatus", Module)] internal static partial int GetStatus(int handle);
            [JSImport("getWidth", Module)] internal static partial int GetWidth(int handle);
            [JSImport("getHeight", Module)] internal static partial int GetHeight(int handle);
            [JSImport("getDuration", Module)] internal static partial double GetDuration(int handle);
            [JSImport("getPosition", Module)] internal static partial double GetPosition(int handle);
            [JSImport("isEnded", Module)] internal static partial bool IsEnded(int handle);
            [JSImport("hasAudio", Module)] internal static partial bool HasAudio(int handle);
            [JSImport("getFrame", Module)] internal static partial int GetFrame(int handle, [JSMarshalAs<JSType.MemoryView>] Span<byte> buffer);
            [JSImport("destroy", Module)] internal static partial void Destroy(int handle);
        }
    }
}
