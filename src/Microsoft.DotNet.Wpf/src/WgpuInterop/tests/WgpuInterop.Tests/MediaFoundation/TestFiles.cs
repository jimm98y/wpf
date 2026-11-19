// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// Where the tests' files are (the Windows fixtures under MediaFoundation/, the media under MediaFoundation/Media/):
// beside the tests, or where MF_TEST_ROOT says -- the folder a mobile head copies its assets to.
//

using System;

namespace WgpuInterop.Tests.MediaFoundation
{
    internal static class TestFiles
    {
        public static string Root => Environment.GetEnvironmentVariable("MF_TEST_ROOT") is { Length: > 0 } r ? r : AppContext.BaseDirectory;
    }
}

namespace WgpuInterop.Tests.MediaFoundation
{
    /// <summary>A probe's report lines, each also appended to the file the variable names plus ".progress" as it comes (to see how far a crash got).</summary>
    internal sealed class ProgressLog : System.Collections.Generic.List<string>
    {
        private readonly string? _file;
        public ProgressLog(string variable) { _file = Environment.GetEnvironmentVariable(variable) is { Length: > 0 } f ? f + ".progress" : null; }
        public new void Add(string line) { base.Add(line); if (_file != null) System.IO.File.AppendAllText(_file, line + "\n"); }
        public new void AddRange(System.Collections.Generic.IEnumerable<string> lines) { foreach (var l in lines) Add(l); }
    }
}
