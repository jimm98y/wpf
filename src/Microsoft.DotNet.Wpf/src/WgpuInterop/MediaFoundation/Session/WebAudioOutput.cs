// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// The browser's audio output (AudioOutput.cs): WebAudio through Browser/browser-codecs.js on the page's thread, each
// 10 ms chunk an AudioBuffer scheduled after the last; written while less than a tenth of a second is queued.
//

#if WGPU_BROWSER
using System;
using System.Runtime.InteropServices.JavaScript;
using System.Threading;

namespace Microsoft.Wpf.Interop.WebGpu.MediaFoundation.Session
{
    internal static partial class WebAudioJs
    {
        private const string M = "wpfBrowserCodecs";
        [JSImport("audioOpen", M)] internal static partial int Open(int rate, int channels);
        [JSImport("audioWrite", M)] internal static partial double Write(int handle, [JSMarshalAs<JSType.MemoryView>] Span<byte> samples, int frames);
        [JSImport("audioQueued", M)] internal static partial double Queued(int handle);
        [JSImport("audioPause", M)] internal static partial void Pause(int handle, bool pause);
        [JSImport("audioFlush", M)] internal static partial void Flush(int handle);
        [JSImport("audioClose", M)] internal static partial void Close(int handle);
    }

    internal sealed class WebAudioOutput : WriterOutput
    {
        private readonly int _handle;
        private WebAudioOutput(int rate, int channels, int handle) : base(rate, channels) { _handle = handle; }

        public static AudioOutput? Open(int rate, int channels)
        {
            try
            {
                int h = WebAudioJs.Open(rate, channels);
                return h > 0 ? new WebAudioOutput(rate, channels, h) : null;
            }
            catch (Exception e) when (e is JSException or InvalidOperationException) { return null; }
        }

        protected override void DeviceWrite(float[] samples, int frames)
        {
            while (!Disposed && WebAudioJs.Queued(_handle) > 0.1) Thread.Sleep(5);
            WebAudioJs.Write(_handle, System.Runtime.InteropServices.MemoryMarshal.AsBytes(samples.AsSpan(0, frames * Channels)), frames);
        }

        public override void Start() { WebAudioJs.Pause(_handle, false); base.Start(); }
        public override void Pause() { base.Pause(); WebAudioJs.Pause(_handle, true); }
        protected override void OnFlush() => WebAudioJs.Flush(_handle);
        protected override void Close() { base.Close(); WebAudioJs.Close(_handle); }
    }
}
#endif
