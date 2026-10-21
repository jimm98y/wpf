// macOS: an app that aborts ends quietly instead of as a crash the system reports with a dialog.
//
// On macOS abort() leaves a crash report and a "quit unexpectedly" dialog, one per crash. Under wpfrun the
// cause is already in the compatibility log (unhandled exceptions are written there before the runtime
// aborts), so the process ends without either: SIGABRT's handler is libc's _exit itself, which the kernel
// calls with the signal number -- the process exits with status 6 (SIGABRT), nothing of ours runs in the
// signal handler, and no native code of ours is needed. The launcher, which waits for the app, reports the
// abort (Runner: the note on stderr and in the log, status 134 as a shell shows an abort).

using System.Runtime.InteropServices;

namespace WpfWebGpu.Bootstrap.Win32
{
    internal static unsafe class QuietAborts
    {
        private const int SIGABRT = 6;

        public static void Install()
        {
            if (!OperatingSystem.IsMacOS()) return;
            IntPtr libSystem = NativeLibrary.Load("/usr/lib/libSystem.B.dylib");
            var signal = (delegate* unmanaged<int, IntPtr, IntPtr>)NativeLibrary.GetExport(libSystem, "signal");
            IntPtr exit = NativeLibrary.GetExport(libSystem, "_exit");
            if (signal(SIGABRT, exit) == -1) CompatLog.Info("quiet aborts: signal(SIGABRT) failed");

            // The runtime restores the default SIGABRT action before it aborts over an unhandled
            // exception, so that case ends here instead, once the log has the exception (its handler
            // was added first and runs first).
            AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            {
                if (!e.IsTerminating) return;
                Console.Error.WriteLine("Unhandled exception. " + e.ExceptionObject);
                Console.Error.WriteLine("wpfrun: exiting with status 134 instead of crashing. See the compatibility log.");
                CompatLog.Info("[exit] unhandled exception: exited with status 134 instead of crashing");
                ((delegate* unmanaged<int, void>)exit)(134);
            };
            // what tells the launcher that a status 6 is the app's own (Environment.Exit(6)), not an abort
            AppDomain.CurrentDomain.ProcessExit += (_, _) => CompatLog.Info($"[exit] the app exited with code {Environment.ExitCode}");
        }
    }
}
