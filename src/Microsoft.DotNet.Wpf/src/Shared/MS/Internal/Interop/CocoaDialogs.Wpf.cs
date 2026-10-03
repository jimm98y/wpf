// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace MS.Internal.Interop
{
    /// <summary>Modal AppKit dialogs, callable from any WPF assembly on macOS.</summary>
    public static partial class CocoaDialogs
    {
        private static partial void EnsureApplication() => CocoaWindow.EnsureApplication();
    }
}
