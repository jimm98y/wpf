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
