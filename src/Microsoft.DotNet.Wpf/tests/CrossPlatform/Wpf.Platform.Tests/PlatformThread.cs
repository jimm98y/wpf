// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// Whether the calling thread is the process's main thread.
//
// Only macOS cares, and it cares absolutely: AppKit raises an Objective-C exception if a window is
// built anywhere else, and that exception unwinds through managed frames into std::terminate rather
// than into a catch block. A test host has no main thread to hand out -- xunit runs every test on
// the thread pool -- so the tests that need one have to ask before they try.
//

using System;
using System.Runtime.InteropServices;

namespace Wpf.Platform.Tests
{
    internal static class PlatformThread
    {
        /// <summary>True on the thread the process started on (always true off macOS, which has no
        /// such requirement to check).</summary>
        public static bool IsMainThread => !OperatingSystem.IsMacOS() || pthread_main_np() != 0;

        [DllImport("/usr/lib/libSystem.dylib")]
        private static extern int pthread_main_np();
    }
}
