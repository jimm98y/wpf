// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// IMediaBackend -- the cross-platform seam for MediaElement/MediaPlayer playback OFF Windows.
//
// On Windows, MediaPlayerState drives a native milcore IMILMedia object (wpfgfx_cor3.dll) that decodes,
// plays audio, and composites video frames natively. That whole pipeline is Windows-only. Off Windows the
// MILMedia.* P/Invokes are no-op'd (see Common/Graphics/wgx_exports.cs) and MediaPlayerState instead owns an
// IMediaBackend: a platform decode+audio+transport engine whose leaf operations mirror the MILMedia calls
// 1:1, so wiring in MediaPlayerState is mechanical. Decoded video frames are pulled by MediaPlayer each
// composition pass and sent to the managed WebGPU compositor via the byte-oriented SendVideoFrame seam.
//
// Implementations: MacMediaBackend (AVFoundation). A browser/WASM backend (HTML5 <video> via JS interop)
// can slot in later behind the same interface. The factory returns null where no backend exists yet
// (Linux, browser today) -- MediaElement then stays blank without crashing, as before.
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
            if (OperatingSystem.IsMacOS())
            {
                return new MacMediaBackend(player);
            }

            if (OperatingSystem.IsBrowser())
            {
                return new BrowserMediaBackend(player);
            }

            // No backend on this platform yet (Linux): MediaElement stays blank without crashing, exactly
            // as the pre-backend stub behaved.
            return null;
        }
    }
}
