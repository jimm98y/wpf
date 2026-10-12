// The compatibility log: what an unmodified app ran into on the WebGPU stack.
//
// Records the exception types that mean "this platform or this stack lacks something" the first time
// each distinct one is thrown, caught or not, with the call stack that reached it, and a count of
// repeats at exit. This is what decides which shim to write next, so it is deliberately quiet about
// everything else: apps throw and catch ordinary exceptions all the time.

using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Text;

namespace WpfMac.Bootstrap
{
    internal static class CompatLog
    {
        private static readonly object s_gate = new();
        private static readonly Dictionary<string, int> s_counts = new(StringComparer.Ordinal);
        private static StreamWriter? s_writer;
        [ThreadStatic] private static bool t_busy;

        public static void Start(string? path)
        {
            if (string.IsNullOrEmpty(path)) return;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
                s_writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true };
            }
            catch
            {
                return;                         // a log we cannot open costs the log, never the app
            }

            AppDomain.CurrentDomain.FirstChanceException += OnFirstChance;
            AppDomain.CurrentDomain.UnhandledException += (_, e) => Write($"UNHANDLED {e.ExceptionObject}");
            AppDomain.CurrentDomain.ProcessExit += (_, _) => Summary();
        }

        public static void Info(string message) => Write(message);

        /// <summary>Writes <paramref name="message"/> the first time <paramref name="key"/> is seen.</summary>
        public static void Once(string key, string message)
        {
            if (s_writer == null) return;
            lock (s_gate)
            {
                s_counts.TryGetValue(key, out int n);
                s_counts[key] = n + 1;
                if (n > 0) return;
            }
            Write(message);
        }

        private static void OnFirstChance(object? sender, FirstChanceExceptionEventArgs e)
        {
            if (t_busy || !IsCompatibilityFailure(e.Exception)) return;
            t_busy = true;
            try
            {
                Exception ex = e.Exception;
                // A DllNotFoundException lists every dlopen it tried; the first line names the library.
                string message = ex.Message;
                int eol = message.IndexOf('\n');
                if (eol > 0) message = message[..eol].TrimEnd();
                if (message.Length > 400) message = message[..400] + "...";
                string key = ex.GetType().FullName + ": " + message;
                bool first;
                lock (s_gate)
                {
                    s_counts.TryGetValue(key, out int n);
                    s_counts[key] = n + 1;
                    first = n == 0;
                }
                if (first)
                {
                    Write($"{key}\n{Frames(new StackTrace(2, false))}");
                }
            }
            catch
            {
            }
            finally
            {
                t_busy = false;
            }
        }

        private static bool IsCompatibilityFailure(Exception ex) => ex switch
        {
            PlatformNotSupportedException => true,
            DllNotFoundException => true,
            EntryPointNotFoundException => true,
            MissingMemberException => true,             // MissingMethod/MissingField
            TypeLoadException => true,
            BadImageFormatException => true,
            NotImplementedException => true,
            FileNotFoundException f => f.FileName?.Contains("Version=", StringComparison.Ordinal) == true,
            FileLoadException => true,
            _ => false,
        };

        private static string Frames(StackTrace trace)
        {
            var sb = new StringBuilder();
            int shown = 0;
            foreach (StackFrame frame in trace.GetFrames())
            {
                var m = frame.GetMethod();
                if (m == null) continue;
                sb.Append("    at ").Append(m.DeclaringType?.FullName).Append('.').Append(m.Name).Append('\n');
                if (++shown == 16) break;
            }
            return sb.ToString().TrimEnd();
        }

        private static void Summary()
        {
            if (s_writer == null) return;
            KeyValuePair<string, int>[] repeated;
            lock (s_gate)
            {
                repeated = s_counts.Where(kv => kv.Value > 1).OrderByDescending(kv => kv.Value).ToArray();
            }
            if (repeated.Length == 0) return;
            var sb = new StringBuilder("summary (repeat counts):\n");
            foreach (var kv in repeated)
            {
                sb.Append($"  {kv.Value,6}  {kv.Key}\n");
            }
            Write(sb.ToString().TrimEnd());
        }

        private static void Write(string message)
        {
            StreamWriter? w = s_writer;
            if (w == null) return;
            lock (s_gate)
            {
                try { w.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] {message}"); } catch { }
            }
        }
    }
}
