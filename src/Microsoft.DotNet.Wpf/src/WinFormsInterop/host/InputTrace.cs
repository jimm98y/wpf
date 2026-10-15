// WF_TRACE_INPUT=1: the life of each real input message, from the moment Windows posted it to the
// present that first showed its effect. One line per step, all on one clock:
//
//   in   #n      the host window received it (and how long it sat in the OS queue first)
//   disp #n      the driver delivered it, to which control
//   enter/leave  the hover changes it caused
//   inval        a control asking to be repainted
//   paint        a control painting (recording its scene), and for how long
//   present      a frame going out: which input it is the first to include, and its cost
//   capture      the driver's mouse capture changing hands
//   syscmd       a system move/size loop starting and ending
//   cursor       an application asking where the pointer is (Control.MousePosition / GetCursorPos)
//
// The file is WF_TRACE_INPUT_LOG, or %LOCALAPPDATA%\WpfMac\logs\input-<pid>.log. Nothing is written,
// and nothing costs more than one static bool test, when the variable is not set.

using System;
using System.Diagnostics;
using System.IO;

namespace System.Windows.Forms
{
    internal static class InputTrace
    {
        internal static readonly bool On = Environment.GetEnvironmentVariable("WF_TRACE_INPUT") == "1";

        private static readonly Stopwatch s_clock = Stopwatch.StartNew();
        private static readonly object s_lock = new object();
        private static StreamWriter s_out;
        private static bool s_failed;

        /// <summary>Number of the last input message received from the OS.</summary>
        internal static int Seq;

        /// <summary>The newest input already included in a present.</summary>
        internal static int Presented;

        /// <summary>Milliseconds on the trace's clock.</summary>
        internal static double Now => s_clock.Elapsed.TotalMilliseconds;

        internal static void Log(string what)
        {
            if (!On) return;
            lock (s_lock)
            {
                if (s_out == null && !s_failed)
                {
                    try
                    {
                        string path = Environment.GetEnvironmentVariable("WF_TRACE_INPUT_LOG");
                        if (string.IsNullOrEmpty(path))
                        {
                            string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WpfMac", "logs");
                            Directory.CreateDirectory(dir);
                            path = Path.Combine(dir, $"input-{Environment.ProcessId}.log");
                        }
                        s_out = new StreamWriter(new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite)) { AutoFlush = true };
                        s_out.WriteLine($"# WF_TRACE_INPUT {DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} pid {Environment.ProcessId}; times are ms since start");
                    }
                    catch (Exception ex)
                    {
                        s_failed = true;
                        Console.Error.WriteLine("WF_TRACE_INPUT: cannot open the log: " + ex.Message);
                    }
                }
                s_out?.WriteLine($"{Now,10:0.00} {what}");
            }
        }

        /// <summary>A control's type and name, for the log.</summary>
        internal static string Describe(IntPtr handle)
        {
            if (handle == IntPtr.Zero) return "<none>";
            Control c = Control.FromHandle(handle);
            if (c == null) return $"0x{handle.ToInt64():x}";
            return string.IsNullOrEmpty(c.Name) ? c.GetType().Name : $"{c.GetType().Name} '{c.Name}'";
        }
    }
}
