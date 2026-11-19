// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// The managed ALAC decoder (Codecs/AlacDecoder.cs), of systems without one of their own (the browser's): what it
// decodes of each file, to the bit what the system's own decoder decodes (AudioToolbox, GStreamer) -- mono and
// stereo 16-bit, stereo 24-bit (afconvert's, of the same tones).
//

using System;
using System.IO;
using System.Linq;
using Microsoft.Wpf.Interop.WebGpu.MediaFoundation.Codecs;
using Microsoft.Wpf.Interop.WebGpu.MediaFoundation.Source;
using Xunit;

namespace WgpuInterop.Tests.MediaFoundation
{
    public sealed class AlacDecoderTests
    {
        [Theory]
        [InlineData("tone-alac.m4a")]
        [InlineData("tone-alac-stereo.m4a")]
        [InlineData("tone-alac-24.m4a")]
        public void TheManagedDecoder_DecodesWhatTheSystemsDecoderDecodes(string name)
        {
            Assert.SkipWhen(OperatingSystem.IsWindows() || !(OperatingSystem.IsMacOS() || OperatingSystem.IsLinux()), "the system's decoder is the reference: AudioToolbox's, GStreamer's");
            using var c = MediaContainer.Open(new FileByteSource(MFSourceReaderProbe.AudioPath(name)))!;
            var t = c.Tracks.Single(x => x.Audio);
            var options = new AudioDecoderOptions { Channels = (uint)t.Channels, SampleRate = (uint)t.SampleRate, Config = t.Config };
            var managed = new AlacDecoder(t.Config);
            using var system = PlatformAudioDecoder.Create(AudioCodec.Alac, options);
            Assert.SkipWhen(system == null, "no ALAC decoder of the system's here");
            var a = new MemoryStream(); var b = new MemoryStream();
            byte[] buf = new byte[1 << 16];
            foreach (var s in t.Samples)
            {
                var packet = c.Read(t, s, out _);
                managed.Push(packet);
                while (managed.TryTake(ref buf, out uint n)) a.Write(buf, 0, (int)n);
                system!.Push(packet, 0);
                while (system.TryTake(ref buf, out uint n)) b.Write(buf, 0, (int)n);
            }
            system!.BeginDrain();
            while (system.TryTake(ref buf, out uint n)) b.Write(buf, 0, (int)n);
            system.EndDrain();
            Assert.Equal(system.BitsPerSample, managed.BitsPerSample);
            Assert.Equal(system.Channels, managed.Channels);
            var x = a.ToArray(); var y = b.ToArray();
            Assert.True(x.Length > 10000, $"{x.Length} bytes decoded");
            int diff = Enumerable.Range(0, Math.Min(x.Length, y.Length)).FirstOrDefault(i => x[i] != y[i], -1);
            Assert.True(x.Length == y.Length && diff < 0, $"managed {x.Length} bytes, the system's {y.Length}; first difference at {diff}");
        }
    }
}
