// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// The frames of an audio byte stream, as Windows' decoders find them in what their sources hand them
// (Source/Containers.cs): ADTS (its headers taken off: raw AAC), MPEG audio frames, FLAC frames (the
// stream's "fLaC" and metadata skipped). Bytes come in any pieces; whole frames go out.
//

using System;
using System.Collections.Generic;

namespace Microsoft.Wpf.Interop.WebGpu.MediaFoundation.Codecs
{
    internal sealed class Packetizer
    {
        public enum Kind { Adts, Mpeg, Flac }

        private readonly Kind _kind;
        private readonly List<byte> _buf = new();
        private bool _flacHeaderDone;

        public Packetizer(Kind kind) { _kind = kind; }

        public void Reset() { _buf.Clear(); }

        /// <summary>The whole frames the bytes complete; <paramref name="end"/>: the stream's end, the rest a frame too (FLAC).</summary>
        public List<byte[]> Push(ReadOnlySpan<byte> data, bool end = false)
        {
            _buf.AddRange(data.ToArray());
            var outs = new List<byte[]>();
            var b = _buf.ToArray();
            int at = 0;
            if (_kind == Kind.Flac && !_flacHeaderDone)
            {
                if (b.Length < 4) return outs;
                if (b[0] == (byte)'f' && b[1] == (byte)'L' && b[2] == (byte)'a' && b[3] == (byte)'C')
                {
                    int m = 4;
                    while (true)
                    {
                        if (m + 4 > b.Length) return outs;
                        int len = b[m + 1] << 16 | b[m + 2] << 8 | b[m + 3];
                        bool last = (b[m] & 0x80) != 0;
                        m += 4 + len;
                        if (last) break;
                    }
                    if (m > b.Length) return outs;
                    at = m;
                }
                _flacHeaderDone = true;
            }
            while (true)
            {
                if (_kind == Kind.Adts)
                {
                    if (at + 7 > b.Length) break;
                    if (b[at] != 0xFF || (b[at + 1] & 0xF6) != 0xF0) { at++; continue; }
                    int len = ((b[at + 3] & 3) << 11) | (b[at + 4] << 3) | (b[at + 5] >> 5);
                    int header = (b[at + 1] & 1) != 0 ? 7 : 9;               // protection_absent: no CRC
                    if (len < header) { at++; continue; }
                    if (at + len > b.Length) break;
                    outs.Add(b.AsSpan(at + header, len - header).ToArray());
                    at += len;
                }
                else if (_kind == Kind.Mpeg)
                {
                    if (at + 4 > b.Length) break;
                    if (!Source.Mp3Container.Header(b.AsSpan(at, 4), out int len, out _, out _, out _, out _)) { at++; continue; }
                    if (at + len > b.Length) break;
                    outs.Add(b.AsSpan(at, len).ToArray());
                    at += len;
                }
                else
                {
                    // a frame runs to the next frame header (or the end)
                    if (at + 16 > b.Length && !end) break;
                    if (!FlacHeader(b, at)) { if (at + 16 > b.Length) break; at++; continue; }
                    int next = -1;
                    for (int i = at + 2; i + 16 <= b.Length; i++)
                        if (b[i] == 0xFF && (b[i + 1] & 0xFE) == 0xF8 && FlacHeader(b, i)) { next = i; break; }
                    if (next < 0)
                    {
                        if (!end) break;
                        next = b.Length;
                    }
                    outs.Add(b.AsSpan(at, next - at).ToArray());
                    at = next;
                    if (at >= b.Length) break;
                }
            }
            _buf.RemoveRange(0, Math.Min(at, _buf.Count));
            return outs;
        }

        /// <summary>A FLAC frame header at the offset: its sync, its fields, its CRC-8 right.</summary>
        private static bool FlacHeader(byte[] b, int at)
        {
            if (at + 6 > b.Length || b[at] != 0xFF || (b[at + 1] & 0xFE) != 0xF8) return false;
            int bs = b[at + 2] >> 4, sr = b[at + 2] & 0xF, ch = b[at + 3] >> 4, ss = (b[at + 3] >> 1) & 7;
            if (bs == 0 || sr == 15 || ch > 10 || ss == 3 || ss == 7 || (b[at + 3] & 1) != 0) return false;
            int i = at + 4;
            // the frame or sample number, UTF-8 coded
            int first = b[i++];
            int more = first < 0x80 ? 0 : first >= 0xFE ? 6 : first >= 0xFC ? 5 : first >= 0xF8 ? 4 : first >= 0xF0 ? 3 : first >= 0xE0 ? 2 : first >= 0xC0 ? 1 : -1;
            if (more < 0) return false;
            for (int k = 0; k < more; k++) { if (i >= b.Length || (b[i] & 0xC0) != 0x80) return false; i++; }
            if (bs == 6) i += 1; else if (bs == 7) i += 2;
            if (sr == 12) i += 1; else if (sr is 13 or 14) i += 2;
            if (i >= b.Length) return false;
            byte crc = 0;
            for (int k = at; k < i; k++)
            {
                crc ^= b[k];
                for (int j = 0; j < 8; j++) crc = (byte)((crc & 0x80) != 0 ? (crc << 1) ^ 0x07 : crc << 1);
            }
            return crc == b[i];
        }
    }
}
