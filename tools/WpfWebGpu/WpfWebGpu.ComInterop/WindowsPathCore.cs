// A path read as Windows reads it: '\' a separator as well as '/', and a name found whatever its case on a
// file system that minds case (the browser's, Linux's). WindowsPaths' stand-ins use it for the paths an
// app hands the System.IO members, and the shims that open files for an app compile it in too (the
// System.Net.Requests shim's file: requests), so a path names the same file whichever way it is opened.

#nullable disable

using System;
using System.IO;
using System.Linq;

namespace WpfWebGpu.Paths
{
    internal static class WindowsPathCore
    {
        private static readonly bool s_windows = OperatingSystem.IsWindows();

        /// <summary>The path with Windows' separators made this system's.</summary>
        public static string Native(string path)
            => path == null || s_windows || path.IndexOf('\\') < 0 ? path : path.Replace('\\', '/');

        // Windows' file names are not case-sensitive: an app asks for "keyPad.xml" and gets KeyPad.xml. A
        // case-sensitive file system -- the browser's, Linux's as a rule -- found nothing, and app' keypad
        // reported its layout missing. Where the file system is one, a path that does not exist as written is
        // found as Windows would find it, a component at a time, the first entry that differs only in case
        // taken; what does not exist at all (a file about to be made) keeps the rest of its name as written.
        private static readonly bool s_caseSensitive = !s_windows && CaseSensitive();

        private static bool CaseSensitive()
        {
            try
            {
                string probe = AppContext.BaseDirectory.TrimEnd('/');
                string flipped = new string(probe.Select(c => char.IsUpper(c) ? char.ToLowerInvariant(c) : char.ToUpperInvariant(c)).ToArray());
                if (flipped == probe) return true;
                return !Directory.Exists(flipped);
            }
            catch (Exception) { return true; }
        }

        /// <summary>The path an app hands a member that opens, lists or checks something: Windows' separators
        /// made this system's, and found as Windows' case-insensitive names find it.</summary>
        public static string Disk(string path)
        {
            string native = Native(path);
            if (!s_caseSensitive || string.IsNullOrEmpty(native) || File.Exists(native) || Directory.Exists(native)) return native;
            try
            {
                string full = Path.GetFullPath(native);
                string root = Path.GetPathRoot(full) ?? "/";
                string[] parts = full.Substring(root.Length).Split('/', StringSplitOptions.RemoveEmptyEntries);
                string at = root;
                for (int i = 0; i < parts.Length; i++)
                {
                    string exact = Path.Combine(at, parts[i]);
                    if (File.Exists(exact) || Directory.Exists(exact)) { at = exact; continue; }
                    string match = Directory.Exists(at)
                        ? Directory.EnumerateFileSystemEntries(at).FirstOrDefault(e => string.Equals(Path.GetFileName(e), parts[i], StringComparison.OrdinalIgnoreCase))
                        : null;
                    if (match == null) return Path.Combine(new[] { at }.Concat(parts.Skip(i)).ToArray());
                    at = match;
                }
                return at;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                return native;
            }
        }
    }
}
