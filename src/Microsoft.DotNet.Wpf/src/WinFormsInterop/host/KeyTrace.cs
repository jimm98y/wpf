// WF_TRACE_KEYS=1: the route every key message of the thread takes, the windows of hosted WPF content
// included -- which window it reached, what the application's message filters said, the WinForms
// pre-processing chain (PreProcessControlMessage, ProcessCmdKey, IsInputKey, ProcessDialogKey) and
// what WPF made of it (the input manager's key events, ComponentDispatcher, the focused element).
// Enough to tell where a key such as Delete stopped. The file is WF_TRACE_KEYS_LOG, or
// %LOCALAPPDATA%\WpfWebGpu\logs\keys-<pid>.log. Costs one static bool test when the variable is not set.

using System;
using System.Diagnostics;
using System.IO;

namespace System.Windows.Forms
{
    internal static class KeyTrace
    {
        internal static readonly bool On = Environment.GetEnvironmentVariable("WF_TRACE_KEYS") == "1";

        private static readonly Stopwatch s_clock = Stopwatch.StartNew();
        private static readonly object s_lock = new object();
        private static StreamWriter s_out;
        private static bool s_failed;

        /// <summary>Whether <paramref name="msg"/> is one of the key messages (WM_KEYFIRST..WM_KEYLAST).</summary>
        internal static bool IsKey(int msg) => msg >= 0x0100 && msg <= 0x0109;

        internal static string Name(int msg) => msg switch
        {
            0x0100 => "WM_KEYDOWN", 0x0101 => "WM_KEYUP", 0x0102 => "WM_CHAR", 0x0103 => "WM_DEADCHAR",
            0x0104 => "WM_SYSKEYDOWN", 0x0105 => "WM_SYSKEYUP", 0x0106 => "WM_SYSCHAR", 0x0107 => "WM_SYSDEADCHAR",
            _ => $"0x{msg:x4}",
        };

        /// <summary>One message, for the log: its name, key and window.</summary>
        internal static string Describe(int msg, IntPtr wParam, IntPtr hwnd)
        {
            long w = (long)wParam;
            string key = msg == 0x0102 || msg == 0x0106 ? $"char 0x{w:x}" : $"vk 0x{w:x} ({(Keys)(int)w})";
            return $"{Name(msg)} {key} hwnd 0x{hwnd.ToInt64():x} {InputTrace.Describe(hwnd)}";
        }

        internal static void Log(string what)
        {
            if (!On) return;
            lock (s_lock)
            {
                if (s_out == null && !s_failed)
                {
                    try
                    {
                        string path = Environment.GetEnvironmentVariable("WF_TRACE_KEYS_LOG");
                        if (string.IsNullOrEmpty(path))
                        {
                            string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WpfWebGpu", "logs");
                            Directory.CreateDirectory(dir);
                            path = Path.Combine(dir, $"keys-{Environment.ProcessId}.log");
                        }
                        s_out = new StreamWriter(new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite)) { AutoFlush = true };
                        s_out.WriteLine($"# WF_TRACE_KEYS {DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} pid {Environment.ProcessId}; times are ms since start");
                    }
                    catch (Exception ex)
                    {
                        s_failed = true;
                        Console.Error.WriteLine("WF_TRACE_KEYS: cannot open the log: " + ex.Message);
                    }
                }
                s_out?.WriteLine($"{s_clock.Elapsed.TotalMilliseconds,10:0.00} [t{Environment.CurrentManagedThreadId}] {what}");
            }
        }
    }
}
