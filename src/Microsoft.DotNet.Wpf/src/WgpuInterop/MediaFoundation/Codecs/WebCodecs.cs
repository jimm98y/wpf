// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// The browser's decoders (Documentation/media-foundation-shim.md, "The system's decoders"): WebCodecs' VideoDecoder and
// AudioDecoder through Browser/browser-codecs.js, on the page's thread. The shim's code runs on a worker of the
// multithreaded runtime, so it waits for what the page's decoder makes -- each JS call a round trip to the page,
// between which the page runs its decoders' callbacks. A decoder's codec string is made of the stream: H.264's and
// HEVC's of their first SPS, VP9's and AV1's of their first frame; Annex B goes in as it is (no description), each
// access unit a chunk, the parameter sets that come alone held for the next. Key frames are found in the chunks;
// none is decoded before the first. Times go over in microseconds and come back in 100 ns as they went in.
//

#if WGPU_BROWSER
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices.JavaScript;
using System.Threading;

namespace Microsoft.Wpf.Interop.WebGpu.MediaFoundation.Codecs
{
    internal static partial class WebCodecsJs
    {
        private const string M = "wpfBrowserCodecs";
        [JSImport("supportsVideo", M)] internal static partial int SupportsVideo(string codec);
        [JSImport("supportsAudio", M)] internal static partial int SupportsAudio(string codec, int sampleRate, int channels);
        [JSImport("createVideoDecoder", M)] internal static partial int CreateVideoDecoder(string codec, int width, int height, bool lowLatency);
        [JSImport("decodeVideo", M)] internal static partial int DecodeVideo(int handle, [JSMarshalAs<JSType.MemoryView>] Span<byte> bytes, double timestamp, bool key);
        [JSImport("videoInfo", M)] internal static partial int VideoInfo(int handle, [JSMarshalAs<JSType.MemoryView>] Span<double> info);
        [JSImport("takeVideo", M)] internal static partial int TakeVideo(int handle, [JSMarshalAs<JSType.MemoryView>] Span<byte> buffer);
        [JSImport("createAudioDecoder", M)] internal static partial int CreateAudioDecoder(string codec, int sampleRate, int channels, [JSMarshalAs<JSType.MemoryView>] Span<byte> description, int kind);
        [JSImport("decodeAudio", M)] internal static partial int DecodeAudio(int handle, [JSMarshalAs<JSType.MemoryView>] Span<byte> bytes, double timestamp);
        [JSImport("audioInfo", M)] internal static partial int AudioInfo(int handle, [JSMarshalAs<JSType.MemoryView>] Span<double> info);
        [JSImport("takeAudio", M)] internal static partial int TakeAudio(int handle, [JSMarshalAs<JSType.MemoryView>] Span<byte> buffer);
        [JSImport("supportsVideoEncoder", M)] internal static partial int SupportsVideoEncoder(string codec, int width, int height);
        [JSImport("supportsAudioEncoder", M)] internal static partial int SupportsAudioEncoder(string codec, int sampleRate, int channels);
        [JSImport("createVideoEncoder", M)] internal static partial int CreateVideoEncoder(string codec, int width, int height, double bitrate, double framerate, string mode);
        [JSImport("setEncoderBitrate", M)] internal static partial int SetEncoderBitrate(int handle, double bitrate);
        [JSImport("encodeVideo", M)] internal static partial int EncodeVideo(int handle, [JSMarshalAs<JSType.MemoryView>] Span<byte> nv12, int width, int height, double timestamp, bool key);
        [JSImport("createAudioEncoder", M)] internal static partial int CreateAudioEncoder(string codec, int sampleRate, int channels, double bitrate);
        [JSImport("encodeAudio", M)] internal static partial int EncodeAudio(int handle, [JSMarshalAs<JSType.MemoryView>] Span<byte> pcm, int frames, double timestamp);
        [JSImport("encoderConfig", M)] internal static partial int EncoderConfig(int handle, [JSMarshalAs<JSType.MemoryView>] Span<byte> buffer);
        [JSImport("flush", M)] internal static partial void Flush(int handle);
        [JSImport("reset", M)] internal static partial void Reset(int handle);
        [JSImport("lastError", M)] internal static partial string LastError(int handle);
        [JSImport("close", M)] internal static partial void Close(int handle);
    }

    internal static class WebCodecs
    {
        /// <summary>A moment's wait on the worker, for the page to run its decoders.</summary>
        internal static void Pause()
        {
            try { Thread.Sleep(1); }
            catch (Exception e) when (e is PlatformNotSupportedException or InvalidOperationException) { Thread.SpinWait(20_000); }
        }

        private static readonly Dictionary<string, bool> s_supported = new();

        /// <summary>The browser's answer (isConfigSupported), waited for (up to 3 s) the first time it is asked.</summary>
        internal static bool Ask(string key, Func<int> ask)
        {
            lock (s_supported)
            {
                if (s_supported.TryGetValue(key, out bool known)) return known;
                int r = 2;
                try
                {
                    var sw = Stopwatch.StartNew();
                    while ((r = ask()) == 0 && sw.ElapsedMilliseconds < 3000) Pause();
                }
                catch (Exception e) when (e is JSException or InvalidOperationException)
                {
                    // a page whose main.js registers no 'wpfBrowserCodecs' (one scaffolded before it): no decoders
                    Log.Warn("WebCodecs: the page has no wpfBrowserCodecs module (main.js's setModuleImports)", e);
                    r = 2;
                }
                s_supported[key] = r == 1;
                return r == 1;
            }
        }

        /// <summary>A codec string of each codec for asking whether the browser decodes it at all.</summary>
        internal static string? Probe(VideoCodec c) => c switch
        {
            VideoCodec.H264 => "avc1.42E01E", VideoCodec.H265 => "hvc1.1.6.L93.B0", VideoCodec.VP9 => "vp09.00.10.08", VideoCodec.AV1 => "av01.0.04M.08", _ => null,
        };

        internal static bool Supports(VideoCodec c) => OperatingSystem.IsBrowser() && Probe(c) is { } s && Ask("v:" + s, () => WebCodecsJs.SupportsVideo(s));

        internal static string? AudioCodecString(AudioCodec c, byte[]? config) => c switch
        {
            AudioCodec.AAC => "mp4a.40." + (config is { Length: > 0 } ? Math.Max(1, config[0] >> 3) : 2),
            AudioCodec.Mp3 => "mp3", AudioCodec.Flac => "flac", AudioCodec.Opus => "opus", AudioCodec.Alac => "alac", _ => null,
        };

        internal static bool Supports(AudioCodec c) => OperatingSystem.IsBrowser() && AudioCodecString(c, null) is { } s && Ask($"a:{s}", () => WebCodecsJs.SupportsAudio(s, 48000, 2));

        // ---- the streams ---------------------------------------------------------------------------------

        /// <summary>The NAL units of Annex B: each one's offset (past its start code) and length.</summary>
        internal static List<(int Offset, int Length)> Nals(ReadOnlySpan<byte> b)
        {
            var starts = new List<int>();
            for (int i = 0; i + 3 <= b.Length; i++)
                if (b[i] == 0 && b[i + 1] == 0 && b[i + 2] == 1) { starts.Add(i + 3); i += 2; }
            var r = new List<(int, int)>();
            for (int k = 0; k < starts.Count; k++)
            {
                int end = k + 1 < starts.Count ? starts[k + 1] - 3 : b.Length;
                while (k + 1 < starts.Count && end > starts[k] && b[end - 1] == 0) end--;
                r.Add((starts[k], end - starts[k]));
            }
            return r;
        }

        /// <summary>The bytes of a NAL unit without its emulation prevention (00 00 03).</summary>
        private static byte[] Rbsp(ReadOnlySpan<byte> nal, int max)
        {
            var r = new List<byte>();
            int zeros = 0;
            for (int i = 0; i < nal.Length && r.Count < max; i++)
            {
                if (zeros >= 2 && nal[i] == 3) { zeros = 0; continue; }
                zeros = nal[i] == 0 ? zeros + 1 : 0;
                r.Add(nal[i]);
            }
            return r.ToArray();
        }

        /// <summary>avc1.PPCCLL of the access unit's SPS.</summary>
        internal static string? AvcCodec(ReadOnlySpan<byte> au)
        {
            foreach (var (o, n) in Nals(au))
                if (n >= 4 && (au[o] & 0x1F) == 7) return $"avc1.{au[o + 1]:X2}{au[o + 2]:X2}{au[o + 3]:X2}";
            return null;
        }

        internal static (int W, int H)? AvcCodedSize(ReadOnlySpan<byte> au) => H264Sps.CodedSize(au);

        /// <summary>hvc1.profile.compat.Llevel.constraints of the access unit's SPS (ISO/IEC 14496-15 E.3).</summary>
        internal static string? HevcCodec(ReadOnlySpan<byte> au)
        {
            foreach (var (o, n) in Nals(au))
            {
                if (n < 16 || ((au[o] >> 1) & 0x3F) != 33) continue;
                var r = Rbsp(au.Slice(o + 2, n - 2), 13);                // past the NAL header; sps_video_parameter_set_id etc. in r[0]
                if (r.Length < 13) return null;
                int space = r[1] >> 6, tier = (r[1] >> 5) & 1, profile = r[1] & 0x1F;
                uint compat = (uint)(r[2] << 24 | r[3] << 16 | r[4] << 8 | r[5]);
                uint rev = 0;
                for (int i = 0; i < 32; i++) if ((compat & (1u << i)) != 0) rev |= 1u << (31 - i);
                int level = r[12];
                var cons = new List<string>();
                int last = 5;
                while (last >= 0 && r[6 + last] == 0) last--;
                for (int i = 0; i <= last; i++) cons.Add(r[6 + i].ToString("X2"));
                string sp = space switch { 1 => "A", 2 => "B", 3 => "C", _ => "" };
                return $"hvc1.{sp}{profile}.{rev:X}.{(tier == 1 ? "H" : "L")}{level}" + (cons.Count > 0 ? "." + string.Join(".", cons) : ".B0");
            }
            return null;
        }

        /// <summary>vp09.PP.10.DD of the first frame's profile (its bit depth 8, or 10 of profiles 2 and 3).</summary>
        internal static string Vp9Codec(ReadOnlySpan<byte> f)
        {
            int profile = f.Length > 0 ? ((f[0] >> 5) & 1) | (((f[0] >> 4) & 1) << 1) : 0;
            return $"vp09.0{profile}.10.{(profile >= 2 ? "10" : "08")}";
        }

        /// <summary>av01.P.04M.DD of the sequence header's profile.</summary>
        internal static string Av1Codec(ReadOnlySpan<byte> t)
        {
            for (int i = 0; i + 2 < t.Length;)
            {
                int type = (t[i] >> 3) & 0xF;
                bool ext = (t[i] & 4) != 0, hasSize = (t[i] & 2) != 0;
                int at = i + 1 + (ext ? 1 : 0);
                long size = t.Length - at;
                if (hasSize) { size = 0; int shift = 0; while (at < t.Length) { byte b = t[at++]; size |= (long)(b & 0x7F) << shift; shift += 7; if ((b & 0x80) == 0) break; } }
                if (type == 1 && at < t.Length) { int profile = t[at] >> 5; return $"av01.{profile}.04M.08"; }
                if (!hasSize) break;
                i = at + (int)size;
            }
            return "av01.0.04M.08";
        }

        /// <summary>Whether the chunk is a key frame: H.264's IDR, HEVC's IRAP, VP9's key frame, AV1's sequence header.</summary>
        internal static bool IsKey(VideoCodec c, ReadOnlySpan<byte> d)
        {
            switch (c)
            {
                case VideoCodec.H264: foreach (var (o, n) in Nals(d)) if (n > 0 && (d[o] & 0x1F) == 5) return true; return false;
                case VideoCodec.H265: foreach (var (o, n) in Nals(d)) if (n > 0 && ((d[o] >> 1) & 0x3F) is >= 16 and <= 23) return true; return false;
                case VideoCodec.VP9:
                {
                    if (d.Length < 1) return false;
                    int profile = ((d[0] >> 5) & 1) | (((d[0] >> 4) & 1) << 1);
                    int bit = profile == 3 ? 2 : 3;                         // past frame_marker, the profile (and a reserved bit of 3)
                    bool showExisting = ((d[0] >> bit) & 1) != 0;
                    return !showExisting && ((d[0] >> (bit - 1)) & 1) == 0;
                }
                case VideoCodec.AV1:
                    return d.Length > 0 && ((d[0] >> 3) & 0xF) is 1 or 2 && Av1HasSequenceHeader(d);
                default: return true;
            }
        }

        private static bool Av1HasSequenceHeader(ReadOnlySpan<byte> t)
        {
            for (int i = 0; i < t.Length;)
            {
                int type = (t[i] >> 3) & 0xF;
                if (type == 1) return true;
                bool ext = (t[i] & 4) != 0, hasSize = (t[i] & 2) != 0;
                if (!hasSize) return false;
                int at = i + 1 + (ext ? 1 : 0);
                long size = 0; int shift = 0;
                while (at < t.Length) { byte b = t[at++]; size |= (long)(b & 0x7F) << shift; shift += 7; if ((b & 0x80) == 0) break; }
                i = at + (int)size;
            }
            return false;
        }

        /// <summary>Whether Annex B holds a picture's slice (else it is parameter sets alone).</summary>
        internal static bool HasPicture(VideoCodec c, ReadOnlySpan<byte> d)
        {
            foreach (var (o, n) in Nals(d))
            {
                if (n == 0) continue;
                if (c == VideoCodec.H264 && (d[o] & 0x1F) is >= 1 and <= 5) return true;
                if (c == VideoCodec.H265 && ((d[o] >> 1) & 0x3F) <= 31) return true;
            }
            return false;
        }
    }

    /// <summary>WebCodecs' VideoDecoder, as a platform decoder: NV12 frames out, in the order they are shown.</summary>
    internal sealed class WebCodecsVideoDecoder : PlatformVideoDecoder
    {
        private readonly VideoCodec _codec;
        private readonly bool _lowLatency;
        private readonly uint _width, _height;
        private int _h;                                    // the decoder's handle, 0 until the first key frame configures it
        private byte[]? _held;                             // parameter sets that came alone, for the next chunk
        private bool _keySeen;
        private readonly Dictionary<long, Queue<long>> _times = new();     // microseconds -> the 100 ns times sent as them
        private readonly double[] _info = new double[9];

        public WebCodecsVideoDecoder(VideoCodec codec, bool lowLatency, uint width, uint height) { _codec = codec; _lowLatency = lowLatency; _width = width; _height = height; }

        public override void Push(ReadOnlySpan<byte> data, long time)
        {
            byte[] chunk = data.ToArray();
            if (_codec is VideoCodec.H264 or VideoCodec.H265)
            {
                if (!WebCodecs.HasPicture(_codec, chunk)) { _held = _held == null ? chunk : Concat(_held, chunk); return; }
                if (_held != null) { chunk = Concat(_held, chunk); _held = null; }
            }
            bool key = WebCodecs.IsKey(_codec, chunk);
            if (!_keySeen && !key) return;                      // WebCodecs begins at a key frame
            if (_h == 0)
            {
                string? codec = _codec switch
                {
                    VideoCodec.H264 => WebCodecs.AvcCodec(chunk), VideoCodec.H265 => WebCodecs.HevcCodec(chunk),
                    VideoCodec.VP9 => WebCodecs.Vp9Codec(chunk), VideoCodec.AV1 => WebCodecs.Av1Codec(chunk), _ => null,
                };
                if (codec == null) return;
                // the coded size where the type has none: of the SPS (Chrome's decoder fails a cropped stream without it)
                var (w, h) = _width > 0 && _height > 0 ? ((int)_width, (int)_height) : _codec == VideoCodec.H264 && WebCodecs.AvcCodedSize(chunk) is { } s ? s : (0, 0);
                _h = WebCodecsJs.CreateVideoDecoder(codec, w, h, _lowLatency);
                if (_h == 0) return;
            }
            _keySeen = true;
            long us = time / 10;
            if (!_times.TryGetValue(us, out var q)) _times[us] = q = new Queue<long>();
            q.Enqueue(time);
            int hr = WebCodecsJs.DecodeVideo(_h, chunk, us, key);
            if (hr != 0) Log.Warn("WebCodecs' VideoDecoder: " + WebCodecsJs.LastError(_h));
        }

        private static byte[] Concat(byte[] a, byte[] b) { var r = new byte[a.Length + b.Length]; a.CopyTo(r, 0); b.CopyTo(r, a.Length); return r; }

        public override bool TryTake(out DecodedFrame? frame)
        {
            frame = null;
            if (_h == 0) return false;
            while (true)
            {
                WebCodecsJs.VideoInfo(_h, _info);
                if (_info[0] == 0) return false;
                var bytes = new byte[(int)_info[4]];
                int n = WebCodecsJs.TakeVideo(_h, bytes);
                if (n <= 0) continue;                           // a frame its copy failed of: the next
                long us = (long)_info[3];
                long time = _times.TryGetValue(us, out var q) && q.Count > 0 ? q.Dequeue() : us * 10;
                if (q != null && q.Count == 0) _times.Remove(us);
                frame = new BytesFrame(bytes, (int)_info[1], (int)_info[2], time);
                return true;
            }
        }

        /// <summary>The decoder flushed, and every frame of it copied out, before the drain's outputs are taken.</summary>
        public override void BeginDrain()
        {
            if (_h == 0) return;
            WebCodecsJs.Flush(_h);
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < 20_000)
            {
                WebCodecsJs.VideoInfo(_h, _info);
                if (_info[7] >= 2 && _info[6] == 0) break;
                WebCodecs.Pause();
            }
        }

        public override void EndDrain() { }

        public override void Flush()
        {
            if (_h != 0) WebCodecsJs.Reset(_h);
            _keySeen = false;
            _held = null;
            _times.Clear();
        }

        public override void Dispose()
        {
            if (_h != 0) WebCodecsJs.Close(_h);
            _h = 0;
        }
    }

    /// <summary>WebCodecs' AudioDecoder: packets in, interleaved PCM out (16-bit of AAC and MP3, the stream's bits of FLAC, float of Opus).</summary>
    internal sealed class WebCodecsAudioDecoder : IDisposable
    {
        private readonly AudioCodec _codec;
        private readonly AudioDecoderOptions _options;
        private int _h;
        private readonly double[] _info = new double[9];
        private uint _skipBytes;
        private readonly uint _skipSamples;

        public uint Channels { get; }
        public uint SampleRate { get; }
        public uint BitsPerSample { get; }

        public WebCodecsAudioDecoder(AudioCodec codec, AudioDecoderOptions options)
        {
            _codec = codec; _options = options;
            Channels = options.Channels; SampleRate = options.SampleRate;
            BitsPerSample = codec == AudioCodec.Opus ? 32u : 16u;
            if (codec == AudioCodec.Flac && Streaminfo(options.Config) is { } si) { SampleRate = si.Rate; Channels = si.Channels; BitsPerSample = si.Bits; }
            _skipSamples = options.SkipSamples;
            _skipBytes = SkipBytes;
        }

        private uint SkipBytes => _skipSamples * Channels * (BitsPerSample / 8);

        /// <summary>FLAC's STREAMINFO (the first block, past its header): the rate, channels and bits.</summary>
        private static (uint Rate, uint Channels, uint Bits)? Streaminfo(byte[]? cfg)
        {
            if (cfg == null) return null;
            int at = cfg.Length >= 4 && cfg[0] == 'f' && cfg[1] == 'L' && cfg[2] == 'a' && cfg[3] == 'C' ? 4 : 0;
            if (cfg.Length < at + 4 + 18 || (cfg[at] & 0x7F) != 0) return null;
            int d = at + 4;
            uint rate = (uint)(cfg[d + 10] << 12 | cfg[d + 11] << 4 | cfg[d + 12] >> 4);
            uint ch = (uint)((cfg[d + 12] >> 1) & 7) + 1;
            uint bits = (uint)(((cfg[d + 12] & 1) << 4) | (cfg[d + 13] >> 4)) + 1;
            return (rate, ch, bits);
        }

        public void Initialize()
        {
            string codec = WebCodecs.AudioCodecString(_codec, _options.Config) ?? throw new NotSupportedException($"no WebCodecs codec of {_codec}");
            byte[] desc = _options.Config ?? Array.Empty<byte>();
            if (_codec == AudioCodec.Flac && desc.Length > 0 && !(desc[0] == 'f' && desc[1] == 'L' && desc[2] == 'a' && desc[3] == 'C'))
            {
                var d = new byte[desc.Length + 4];
                "fLaC"u8.CopyTo(d); desc.CopyTo(d, 4);
                desc = d;
            }
            int kind = BitsPerSample == 32 && _codec == AudioCodec.Opus ? 2 : BitsPerSample == 24 ? 1 : BitsPerSample == 32 ? 3 : 0;
            _h = WebCodecsJs.CreateAudioDecoder(codec, (int)SampleRate, (int)Channels, desc, kind);
            if (_h == 0) throw new NotSupportedException($"WebCodecs' AudioDecoder takes no {codec}");
        }

        public void ProcessInput(ReadOnlySpan<byte> data, long time)
        {
            if (_h == 0 || data.IsEmpty) return;
            int hr = WebCodecsJs.DecodeAudio(_h, data.ToArray(), time / 10);
            if (hr != 0) Log.Warn($"WebCodecs' AudioDecoder ({WebCodecs.AudioCodecString(_codec, _options.Config)}, config {Convert.ToHexString(_options.Config ?? Array.Empty<byte>())}): " + WebCodecsJs.LastError(_h));
        }

        public bool ProcessOutput(ref byte[] buffer, out uint length)
        {
            length = 0;
            if (_h == 0) return false;
            while (true)
            {
                WebCodecsJs.AudioInfo(_h, _info);
                if (_info[0] == 0) return false;
                int n = (int)_info[4];
                if (buffer == null || buffer.Length < n) buffer = new byte[Math.Max(n, 4096)];
                int got = WebCodecsJs.TakeAudio(_h, buffer.AsSpan(0, Math.Max(n, 0)));
                if (got <= 0) continue;
                // what Windows' decoder leaves out at the start (Opus's pre-skip)
                uint skip = Math.Min(_skipBytes, (uint)got);
                _skipBytes -= skip;
                if (skip == got) continue;
                if (skip > 0) Buffer.BlockCopy(buffer, (int)skip, buffer, 0, got - (int)skip);
                length = (uint)(got - (int)skip);
                return true;
            }
        }

        public void BeginDrain()
        {
            if (_h == 0) return;
            WebCodecsJs.Flush(_h);
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < 20_000)
            {
                WebCodecsJs.AudioInfo(_h, _info);
                if (_info[7] >= 2 && _info[6] == 0) break;
                WebCodecs.Pause();
            }
        }

        public void EndDrain() { }

        public void Flush()
        {
            if (_h != 0) WebCodecsJs.Reset(_h);
            _skipBytes = SkipBytes;
        }

        public void Dispose()
        {
            if (_h != 0) WebCodecsJs.Close(_h);
            _h = 0;
        }
    }

    /// <summary>The queue of a WebCodecs object's outputs, shared by the encoders: what is ready, taken; a flush waited for.</summary>
    internal sealed class WebCodecsQueue
    {
        private readonly int _h;
        public readonly double[] Info = new double[9];
        public WebCodecsQueue(int h) { _h = h; }

        /// <summary>The next output's bytes (its time and key flag in Info), or null.</summary>
        public byte[]? Take()
        {
            while (true)
            {
                WebCodecsJs.VideoInfo(_h, Info);
                if (Info[0] == 0) return null;
                var b = new byte[(int)Info[4]];
                int n = WebCodecsJs.TakeVideo(_h, b);
                if (n > 0) return b;
            }
        }

        public void Drain()
        {
            WebCodecsJs.Flush(_h);
            var sw = Stopwatch.StartNew();
            var info = new double[9];
            while (sw.ElapsedMilliseconds < 30_000)
            {
                WebCodecsJs.VideoInfo(_h, info);
                if (info[7] >= 2 && info[6] == 0) break;
                WebCodecs.Pause();
            }
        }
    }

    /// <summary>WebCodecs' VideoEncoder: NV12 in, Annex B access units out (the parameter sets before each key frame), each with its frame's time.</summary>
    internal sealed class WebCodecsVideoEncoder : IDisposable
    {
        private readonly VideoCodec _codec;
        private readonly VideoEncoderOptions _o;
        private int _h;
        private WebCodecsQueue? _q;
        private long _frames;
        private readonly Dictionary<long, Queue<long>> _times = new();

        public WebCodecsVideoEncoder(VideoCodec codec, VideoEncoderOptions options) { _codec = codec; _o = options; }

        /// <summary>The codec string of the options: H.264's profile (66, 77, 100) and a level of the picture's size; HEVC's Main.</summary>
        internal static string? Codec(VideoCodec c, VideoEncoderOptions o)
        {
            long area = (long)o.Width * o.Height;
            if (c == VideoCodec.H264)
            {
                string profile = o.H264Profile switch { 66 => "4200", 77 => "4D00", _ => "6400" };
                string level = area <= 414_720 ? "1E" : area <= 921_600 ? "1F" : area <= 2_228_224 ? "28" : area <= 8_912_896 ? "33" : "34";
                return "avc1." + profile + level;
            }
            if (c == VideoCodec.H265) return "hvc1.1.6.L" + (area <= 921_600 ? "93" : area <= 2_228_224 ? "120" : "153") + ".B0";
            return null;
        }

        public static bool Supports(VideoCodec c) => OperatingSystem.IsBrowser() && Codec(c, new VideoEncoderOptions { Width = 640, Height = 480, H264Profile = 66 }) is { } s && WebCodecs.Ask("ve:" + s, () => WebCodecsJs.SupportsVideoEncoder(s, 640, 480));

        public void Initialize()
        {
            string codec = Codec(_codec, _o) ?? throw new NotSupportedException($"no WebCodecs encoder of {_codec}");
            double fps = _o.FpsNom > 0 ? (double)_o.FpsNom / Math.Max(1, _o.FpsDenom) : 0;
            string mode = _o.RateControl switch { RateControlMode.ConstantBitrate => "constant", RateControlMode.VariableBitrate => "variable", _ => "" };
            _h = WebCodecsJs.CreateVideoEncoder(codec, (int)_o.Width, (int)_o.Height, _o.Bitrate, fps, mode);
            if (_h == 0) throw new NotSupportedException($"WebCodecs' VideoEncoder takes no {codec} of {_o.Width}x{_o.Height}");
            _q = new WebCodecsQueue(_h);
        }

        /// <summary>The bit rate changed while encoding: the encoder configured again of it.</summary>
        public void SetBitrate(uint bitrate)
        {
            _o.Bitrate = bitrate;
            if (_h != 0 && WebCodecsJs.SetEncoderBitrate(_h, bitrate) != 0) Log.Warn("WebCodecs' VideoEncoder: " + WebCodecsJs.LastError(_h));
        }

        public void ProcessInput(ReadOnlySpan<byte> nv12, long time)
        {
            long us = time / 10;
            if (!_times.TryGetValue(us, out var q)) _times[us] = q = new Queue<long>();
            q.Enqueue(time);
            bool key = _o.KeyFrameInterval > 0 && _frames % _o.KeyFrameInterval == 0;
            _frames++;
            if (WebCodecsJs.EncodeVideo(_h, nv12.ToArray(), (int)_o.Width, (int)_o.Height, us, key) != 0) Log.Warn("WebCodecs' VideoEncoder: " + WebCodecsJs.LastError(_h));
        }

        public bool ProcessOutput(ref byte[] buffer, out uint length, out long time)
        {
            length = 0; time = 0;
            if (_q?.Take() is not { } b) return false;
            long us = (long)_q.Info[3];
            time = _times.TryGetValue(us, out var q) && q.Count > 0 ? q.Dequeue() : us * 10;
            if (q != null && q.Count == 0) _times.Remove(us);
            if (buffer == null || buffer.Length < b.Length) buffer = new byte[b.Length];
            b.CopyTo(buffer, 0);
            length = (uint)b.Length;
            return true;
        }

        public void BeginDrain() => _q?.Drain();
        public void EndDrain() { }
        public void Dispose() { if (_h != 0) WebCodecsJs.Close(_h); _h = 0; }
    }

    /// <summary>WebCodecs' AudioEncoder of AAC: 16-bit PCM in, raw AAC packets out, the AudioSpecificConfig once it has one.</summary>
    internal sealed class WebCodecsAudioEncoder : IDisposable
    {
        private readonly AudioEncoderOptions _o;
        private int _h;
        private WebCodecsQueue? _q;
        private long _framesIn;
        private byte[]? _config;

        public WebCodecsAudioEncoder(AudioCodec codec, AudioEncoderOptions options)
        {
            if (codec != AudioCodec.AAC) throw new NotSupportedException($"no WebCodecs encoder of {codec} here");
            _o = options;
        }

        private static string Codec(uint objectType) => "mp4a.40." + (objectType is 5 or 29 ? objectType : 2);

        public static bool Supports(AudioCodec c) => OperatingSystem.IsBrowser() && c == AudioCodec.AAC && WebCodecs.Ask("ae:mp4a.40.2", () => WebCodecsJs.SupportsAudioEncoder("mp4a.40.2", 48000, 2));

        public uint Channels => _o.Channels;
        public uint SampleRate => _o.SampleRate;

        public void Initialize()
        {
            _h = WebCodecsJs.CreateAudioEncoder(Codec(_o.AacObjectType), (int)_o.SampleRate, (int)_o.Channels, _o.AacBitrate);
            if (_h == 0) throw new NotSupportedException($"WebCodecs' AudioEncoder takes no AAC of {_o.SampleRate} Hz, {_o.Channels} channels");
            _q = new WebCodecsQueue(_h);
        }

        public byte[]? Config
        {
            get
            {
                if (_config != null || _h == 0) return _config;
                var b = new byte[64];
                int n = WebCodecsJs.EncoderConfig(_h, b);
                if (n > 0 && n <= b.Length) _config = b.AsSpan(0, n).ToArray();
                return _config;
            }
        }

        public void ProcessInput(ReadOnlySpan<byte> pcm, long time)
        {
            int frames = pcm.Length / (int)(2 * Math.Max(1, _o.Channels));
            if (frames == 0) return;
            long us = _framesIn * 1_000_000 / Math.Max(1, _o.SampleRate);
            _framesIn += frames;
            if (WebCodecsJs.EncodeAudio(_h, pcm.Slice(0, frames * (int)(2 * _o.Channels)).ToArray(), frames, us) != 0) Log.Warn("WebCodecs' AudioEncoder: " + WebCodecsJs.LastError(_h));
        }

        public bool ProcessOutput(ref byte[] buffer, out uint length)
        {
            length = 0;
            if (_q?.Take() is not { } b) return false;
            if (buffer == null || buffer.Length < b.Length) buffer = new byte[b.Length];
            b.CopyTo(buffer, 0);
            length = (uint)b.Length;
            return true;
        }

        public void BeginDrain() => _q?.Drain();
        public void EndDrain() { }
        public void Dispose() { if (_h != 0) WebCodecsJs.Close(_h); _h = 0; }
    }
}
#endif
