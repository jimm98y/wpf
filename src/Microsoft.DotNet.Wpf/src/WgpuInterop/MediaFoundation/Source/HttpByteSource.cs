// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// A file over HTTP(S), for the source reader (Documentation/media-foundation-shim.md, "The source reader"):
// what Windows' network byte stream does for an http: or https: URL (probed) -- a GET of bytes=0- first,
// then the parts asked for by range, in blocks kept; a server that takes no ranges is read through once
// into a file of its own. What the reader makes of it is what it makes of the same file on disk.
//

using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;

namespace Microsoft.Wpf.Interop.WebGpu.MediaFoundation.Source
{
    internal sealed class HttpByteSource : IByteSource
    {
        public const int NS_E_FILE_NOT_FOUND = unchecked((int)0xC00D001A);
        private static readonly HttpClient s_client = new(new SocketsHttpHandler { AutomaticDecompression = DecompressionMethods.None }) { Timeout = TimeSpan.FromSeconds(30) };
        private const int Block = 256 * 1024;

        private readonly Uri _url;
        private readonly Dictionary<long, byte[]> _blocks = new();
        private readonly LinkedList<long> _recent = new();
        private readonly FileStream? _whole;            // a server without ranges: the body, read through once
        private readonly object _lock = new();

        public long Length { get; }
        public string? ContentType { get; }

        private HttpByteSource(Uri url, long length, bool ranges, FileStream? whole, string? type)
        {
            _url = url; Length = length; _whole = whole; ContentType = type;
        }

        /// <summary>The file at the URL, or the HRESULT Windows answers with (NS_E_FILE_NOT_FOUND of a 404).</summary>
        public static int Open(Uri url, out HttpByteSource? source)
        {
            source = null;
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                req.Headers.Range = new RangeHeaderValue(0, null);
                using var resp = s_client.Send(req, HttpCompletionOption.ResponseHeadersRead);
                if (resp.StatusCode == HttpStatusCode.NotFound || resp.StatusCode == HttpStatusCode.Gone) return NS_E_FILE_NOT_FOUND;
                if (resp.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden) return unchecked((int)0x80070005);
                if (!resp.IsSuccessStatusCode) return NS_E_FILE_NOT_FOUND;
                string? type = resp.Content.Headers.ContentType?.MediaType;
                if (resp.StatusCode == HttpStatusCode.PartialContent && resp.Content.Headers.ContentRange?.Length is long total)
                {
                    var s = new HttpByteSource(url, total, true, null, type);
                    // the first block, of the response in hand
                    using var body = resp.Content.ReadAsStream();
                    var first = new byte[(int)Math.Min(Block, total)];
                    int got = ReadFully(body, first);
                    if (got == first.Length) s.Keep(0, first);
                    source = s;
                    return MF.S_OK;
                }
                // no ranges: the whole of it, into a file of its own (deleted when closed)
                var temp = new FileStream(Path.Combine(Path.GetTempPath(), "wpfmf-http-" + Guid.NewGuid().ToString("N")), FileMode.Create, FileAccess.ReadWrite, FileShare.None, 65536, FileOptions.DeleteOnClose);
                resp.Content.ReadAsStream().CopyTo(temp);
                source = new HttpByteSource(url, temp.Length, false, temp, type);
                return MF.S_OK;
            }
            catch (HttpRequestException e) { Codecs.Log.Warn($"HTTP {url.Host}", e); return e.HttpRequestError == HttpRequestError.NameResolutionError ? unchecked((int)0x80072EE7) : unchecked((int)0x80072EFD); }
            catch (System.Threading.Tasks.TaskCanceledException) { return unchecked((int)0x80072EE2); }                 // ERROR_INTERNET_TIMEOUT
        }

        private static int ReadFully(Stream s, Span<byte> into)
        {
            int total = 0;
            while (total < into.Length) { int n = s.Read(into.Slice(total)); if (n <= 0) break; total += n; }
            return total;
        }

        private void Keep(long index, byte[] data)
        {
            _blocks[index] = data;
            _recent.AddFirst(index);
            while (_recent.Count > 64) { _blocks.Remove(_recent.Last!.Value); _recent.RemoveLast(); }
        }

        private byte[] BlockAt(long index)
        {
            if (_blocks.TryGetValue(index, out var b)) return b;
            long start = index * Block, end = Math.Min(Length, start + Block) - 1;
            using var req = new HttpRequestMessage(HttpMethod.Get, _url);
            req.Headers.Range = new RangeHeaderValue(start, end);
            using var resp = s_client.Send(req);
            resp.EnsureSuccessStatusCode();
            using var body = resp.Content.ReadAsStream();
            var data = new byte[end - start + 1];
            ReadFully(body, data);
            Keep(index, data);
            return data;
        }

        public int Read(long offset, Span<byte> into)
        {
            lock (_lock)
            {
                if (offset >= Length) return 0;
                int want = (int)Math.Min(into.Length, Length - offset);
                if (_whole != null)
                {
                    _whole.Position = offset;
                    return ReadFully(_whole, into.Slice(0, want));
                }
                int done = 0;
                try
                {
                    while (done < want)
                    {
                        long at = offset + done, index = at / Block;
                        var b = BlockAt(index);
                        int from = (int)(at - index * Block), n = Math.Min(want - done, b.Length - from);
                        if (n <= 0) break;
                        b.AsSpan(from, n).CopyTo(into.Slice(done));
                        done += n;
                    }
                }
                catch (Exception e) when (e is HttpRequestException or IOException or System.Threading.Tasks.TaskCanceledException) { Codecs.Log.Warn($"HTTP {_url.Host}", e); }
                return done;
            }
        }

        public void Dispose() => _whole?.Dispose();
    }
}
