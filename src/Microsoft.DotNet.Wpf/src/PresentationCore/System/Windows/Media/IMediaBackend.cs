// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// IMediaBackend -- the cross-platform seam for MediaElement/MediaPlayer playback, on EVERY platform.
//
// WPF used to drive a native milcore IMILMedia object (wpfgfx_cor3.dll) that decoded, played audio and
// composited video natively. That pipeline was Windows-only, and it did not survive the move to the
// managed WebGPU compositor even there: its frames were composited inside milcore, so the managed
// compositor never received any and video rendered blank. Every platform now owns an IMediaBackend
// instead -- a platform decode+audio+transport engine whose leaf operations mirror the old MILMedia calls
// 1:1, so wiring in MediaPlayerState is mechanical. Decoded video frames are pulled by MediaPlayer each
// composition pass and sent to the compositor via the byte-oriented SendVideoFrame seam. The MILMedia.*
// entry points are now inert stubs (see Common/Graphics/wgx_exports.cs) and PresentationCore no longer
// loads wpfgfx_cor3.dll for media on any platform.
//
// Implementations: WindowsMediaBackend (Media Foundation + waveOut), MacMediaBackend (AVFoundation),
// IOSMediaBackend (AVFoundation + AVAudioSession), LinuxMediaBackend (GStreamer), AndroidMediaBackend
// (NDK AMediaExtractor/AMediaCodec/AImageReader + AAudio), BrowserMediaBackend (HTML5 <video> via JS
// interop). The factory returns null where no backend exists for the platform -- MediaElement then
// stays blank without crashing, exactly as the pre-backend stub behaved.
//

namespace System.Windows.Media
{
    /// <summary>
    /// Platform media decode + audio + transport engine used by <see cref="MediaPlayerState"/> off-Windows.
    /// Leaf operations mirror the native <c>MILMedia.*</c> calls so MediaPlayerState routes to it directly.
    /// </summary>
    internal interface IMediaBackend : IDisposable
    {
        // ---- transport (mirror MILMedia.Open/Close/SetRate/Set-GetPosition) ----
        void Open(string url);
        void Close();
        void SetRate(double rate);          // 0 = paused; otherwise play at this speed
        long GetPositionTicks();            // 100ns ticks
        void SetPositionTicks(long ticks);

        // ---- audio / scrubbing (mirror MILMedia.SetVolume/SetBalance/SetIsScrubbingEnabled) ----
        void SetVolume(double volume);      // 0..1
        void SetBalance(double balance);    // -1..1 (may be a no-op where the platform lacks a balance knob)
        void SetScrubbingEnabled(bool enabled);

        // ---- per-pass frame kick (mirror MILMedia.NeedUIFrameUpdate) ----
        void NeedUIFrameUpdate();

        // ---- state getters (mirror MILMedia.IsBuffering/CanPause/Get*/Has*) ----
        bool IsBuffering { get; }
        bool CanPause { get; }
        double DownloadProgress { get; }    // 0..1
        double BufferingProgress { get; }   // 0..1
        int NaturalVideoWidth { get; }
        int NaturalVideoHeight { get; }
        bool HasAudio { get; }
        bool HasVideo { get; }
        long MediaLengthTicks { get; }      // 0 => unknown/automatic

        // ---- video frame pull (Approach 2: MediaPlayer.UpdateResource pulls per pass, sends via channel) ----
        /// <summary>
        /// Locks the current decoded video frame and returns a pointer to its top-down BGRA32 pixels.
        /// Returns false if no frame is available yet. Call <see cref="UnlockFrame"/> when done copying.
        /// </summary>
        bool TryLockFrame(out IntPtr baseAddress, out int width, out int height, out int rowBytes);
        void UnlockFrame();

        // ---- events (backend raises; MediaPlayerState routes to MediaEventsHelper on the dispatcher) ----
        event Action FrameAvailable;
        event Action Opened;
        event Action Ended;
        event Action<Exception> Failed;
        event Action BufferingStarted;
        event Action BufferingEnded;
    }

    /// <summary>Selects the platform <see cref="IMediaBackend"/> (mirrors the fork's IPlatformWindow/NativePlatform OS dispatch).</summary>
    internal static class MediaBackendFactory
    {
        internal static IMediaBackend Create(MediaPlayer player)
        {
            if (OperatingSystem.IsWindows())
            {
                return new WindowsMediaBackend(player);
            }

            if (OperatingSystem.IsMacOS())
            {
                return new MacMediaBackend(player);
            }

            // Before the Linux test: iOS is not "Linux", but it is checked here beside its macOS sibling
            // because the two share AVFoundation and differ only in the ways IOSMediaBackend documents.
            if (OperatingSystem.IsIOS())
            {
                return new IOSMediaBackend(player);
            }

            if (OperatingSystem.IsBrowser())
            {
                return new BrowserMediaBackend(player);
            }

            // Android is also "Linux" to OperatingSystem, so it has to be answered first: it has no
            // GStreamer, and its backend is the NDK media stack instead.
            if (OperatingSystem.IsAndroid())
            {
                return new AndroidMediaBackend(player);
            }

            if (OperatingSystem.IsLinux())
            {
                return new LinuxMediaBackend(player);
            }

            // No backend on this platform: MediaElement stays blank without crashing, exactly as the
            // pre-backend stub behaved.
            return null;
        }
    }
}
