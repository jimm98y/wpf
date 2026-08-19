// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// The process main thread, and how a test reaches it.
//
// Only macOS cares, and it cares absolutely: AppKit raises an Objective-C exception if a window is
// built anywhere else, and that exception unwinds through managed frames into std::terminate rather
// than into a catch block. xunit runs every test on a worker thread, so for a long time the tests
// that need a window simply skipped on macOS -- 17 of them, the entire windowing head.
//
// They no longer have to. Program.cs keeps the main thread for the UI and runs the test runner
// beside it (the arrangement every macOS UI host uses, and the one Wpf.Input.Tests already uses),
// so there IS a main thread to marshal onto. This is the marshalling: InvokeOnMain runs a delegate
// there and re-throws whatever it threw, with the stack it threw it with.
//
// Everything AppKit belongs on that thread, not just Create: reading a window's backingScaleFactor
// or destroying it off-thread is undefined in the same way, and was the latent half of this suite.
// MainThreadWindow does that part, so the tests themselves rarely name this class.
//

using System;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Windows.Threading;

namespace Wpf.Platform.Tests
{
    internal static class PlatformThread
    {
        /// <summary>True on the thread the process started on (always true off macOS, which has no
        /// such requirement to check).</summary>
        public static bool IsMainThread => !OperatingSystem.IsMacOS() || pthread_main_np() != 0;

        /// <summary>
        /// The dispatcher running on the process main thread, installed by Program before the runner
        /// starts. Null when the host did not set one up -- a plain `dotnet run` of the assembly with
        /// the generated entry point, for instance -- which is why the tests ask rather than assume.
        /// </summary>
        internal static Dispatcher? MainDispatcher { get; set; }

        /// <summary>True when <see cref="InvokeOnMain(Action)"/> can actually reach the main thread.</summary>
        public static bool CanReachMainThread =>
            !OperatingSystem.IsMacOS() || IsMainThread || MainDispatcher is not null;

        /// <summary>Run <paramref name="action"/> on the process main thread and wait for it.</summary>
        public static void InvokeOnMain(Action action)
            => InvokeOnMain<object?>(() => { action(); return null; });

        /// <summary>Run <paramref name="func"/> on the process main thread and return its result.</summary>
        public static T InvokeOnMain<T>(Func<T> func)
        {
            // Off macOS there is nothing to marshal, and on the main thread we are already there --
            // going through the dispatcher from there would deadlock a nested call.
            if (!OperatingSystem.IsMacOS() || IsMainThread)
            {
                return func();
            }

            Dispatcher? main = MainDispatcher
                ?? throw new InvalidOperationException(
                    "No main-thread dispatcher was installed. Wpf.Platform.Tests must run through its " +
                    "own entry point (Program.cs); see the comment there.");

            // Invoke rather than InvokeAsync + Wait: Invoke marshals the exception back to this thread
            // with its original stack, so a failing assertion inside the delegate reads as though it
            // ran here. Send is the priority the dispatcher uses for a synchronous call anyway.
            T result = default!;
            ExceptionDispatchInfo? failure = null;
            main.Invoke(() =>
            {
                try { result = func(); }
                catch (Exception e) { failure = ExceptionDispatchInfo.Capture(e); }
            }, DispatcherPriority.Send);

            failure?.Throw();
            return result;
        }

        [DllImport("/usr/lib/libSystem.dylib")]
        private static extern int pthread_main_np();
    }
}
