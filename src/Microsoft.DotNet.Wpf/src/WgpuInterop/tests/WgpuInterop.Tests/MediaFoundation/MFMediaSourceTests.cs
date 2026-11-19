// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// The media sources of files and the source resolver (Documentation/media-foundation-shim.md, "Media sources"):
// what they do against what Windows' do (MediaSource.windows.txt, MFMediaSourceProbe's report on Windows). On
// Windows the test drives Windows' own: the same expectations hold of it.
//

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;

namespace WgpuInterop.Tests.MediaFoundation
{
    public sealed class MFMediaSourceTests
    {
        /// <summary>
        /// Every line of the report as Windows' -- but the files' times of last writing (the files' copies' own), and the
        /// estimates the source reader's comparison leaves out as well (an esds of Apple's tools' AAC, ADTS's duration and
        /// bitrate, Matroska's FLAC bytes per second).
        /// </summary>
        [Fact]
        public void TheSources_DoWithEachFileWhatWindowsSourcesDo()
        {
            var windows = File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "MediaFoundation", "MediaSource.windows.txt"));
            var ours = MFMediaSourceProbe.All();
            static List<string> Clean(IEnumerable<string> lines)
            {
                var r = new List<string>();
                string file = "";
                foreach (var l in lines)
                {
                    if (l.StartsWith("==== ")) file = l;
                    string t = l.TrimStart();
                    if (t.StartsWith("pd 6c990d38-")) continue;
                    if (file is "==== tone-aac.m4a" or "==== clip.mkv" && (t.StartsWith("MF_MT_AUDIO_AVG_BYTES_PER_SECOND") || t.StartsWith("MF_MT_AVG_BITRATE"))) continue;
                    if (file == "==== tone.aac" && (t.StartsWith("pd MF_PD_DURATION") || t.StartsWith("pd MF_PD_AUDIO_ENCODING_BITRATE") || t.StartsWith("MF_MT_AUDIO_AVG_BYTES_PER_SECOND"))) continue;
                    r.Add(l);
                }
                return r;
            }
            var w = Clean(windows); var o = Clean(ours);
            for (int i = 0; i < Math.Max(w.Count, o.Count); i++)
                Assert.True(i < w.Count && i < o.Count && w[i] == o[i], $"line {i}: Windows '{(i < w.Count ? w[i] : "(none)")}', ours '{(i < o.Count ? o[i] : "(none)")}'");
        }
    }
}
