// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Wpf.WinFormsInterop.Tests
{
    /// <summary>WindowsBase's PlatformClipboard file-list encoding, for the tests.</summary>
    internal static class MSInternalInteropAccess
    {
        internal static byte[] Encode(string[] paths) => MS.Internal.Interop.PlatformClipboard.EncodeFileList(paths);
        internal static string[] Decode(byte[] list) => MS.Internal.Interop.PlatformClipboard.DecodeFileList(list);
    }
}
