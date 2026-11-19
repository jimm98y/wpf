// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
//
// From SharpMediaFoundationInterop (MIT, by the same author), adapted for the shim's Media Foundation.
#nullable disable
namespace Microsoft.Wpf.Interop.WebGpu.MediaFoundation.Codecs
{
    /// <summary>The audio formats a codec of <see cref="MediaCodecs"/> can be asked for.</summary>
    internal enum AudioCodec
    {
        /// <summary>AAC, raw - not ADTS: as MP4 carries it. 16 bit PCM on the other side.</summary>
        AAC,

        /// <summary>Opus, of 20 ms frames. 32 bit float on the other side.</summary>
        Opus,

        /// <summary>MP3: MPEG-1 and MPEG-2 Layer III. 16 bit PCM on the other side.</summary>
        Mp3,

        /// <summary>FLAC, of the stream's bits: 16 or 24 bit PCM on the other side.</summary>
        Flac,

        /// <summary>Apple Lossless, of the stream's bits: 16 or 24 bit PCM on the other side.</summary>
        Alac
    }

    /// <summary>What a decoder of <see cref="MediaCodecs.CreateAudioDecoder"/> is made for.</summary>
    internal sealed class AudioDecoderOptions
    {
        public uint Channels { get; set; } = 2;

        public uint SampleRate { get; set; } = 48000;

        /// <summary>
        /// The codec's configuration, as the container carries it, which the channels, rate and bits of a lossless stream
        /// are read of: of AAC the AudioSpecificConfig; of FLAC the metadata blocks, each with its header, STREAMINFO first -
        /// what the 'dfLa' box holds; of ALAC the ALACSpecificConfig. Opus and MP3 need none.
        /// </summary>
        public byte[] Config { get; set; }

        /// <summary>
        /// Samples, of each channel, at the start of what the decoder hands out that are not of the sound: Opus's pre-skip,
        /// which the decoder takes to settle in. Dropped again after a flush - a seek - as the decoder settles in anew.
        /// </summary>
        public uint SkipSamples { get; set; }
    }
}
