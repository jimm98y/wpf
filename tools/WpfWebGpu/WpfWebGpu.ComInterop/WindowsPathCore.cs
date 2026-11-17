// A path read as Windows reads it: '\' a separator as well as '/', and a name found whatever its case on a
// file system that minds case (the browser's, Linux's). WindowsPaths' stand-ins use it for the paths an
// app hands the System.IO members, and the shims that open files for an app compile it in too (the
// System.Net.Requests shim's file: requests), so a path names the same file whichever way it is opened.

#nullable disable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace WpfWebGpu.Paths
{
    internal static class WindowsPathCore
    {
        private static readonly bool s_windows = OperatingSystem.IsWindows();

        /// <summary>The path with Windows' separators made this system's -- and a Windows path that names a
        /// place by its drive or its server (C:\..., \\server\share\...) the place it is here (Locate).</summary>
        public static string Native(string path)
        {
            if (path == null || s_windows) return path;
            if (path.Length >= 2 && (path[1] == ':' || (path[0] is '\\' or '/' && path[1] is '\\' or '/'))) path = Locate(path);
            return path.IndexOf('\\') < 0 ? path : path.Replace('\\', '/');
        }

        // ---- where a Windows path is here ------------------------------------------------------
        //
        // An application names places as Windows has them: C:\ProgramData\Vendor\..., C:\Program Files\Vendor\...,
        // \\fileserver\share\... -- in its own code, its configuration files, its registry. Off Windows such a path
        // was a relative path named "C:" (or "//fileserver/share"), and nothing it named was found. Now:
        //
        //   * the machine's mapping table (<state>/paths.json, [{"from": "C:\\Program Files\\Vendor", "to":
        //     "/opt/Vendor/app"}], written by whatever installed the application) says where its folders are, the
        //     longest matching entry first;
        //   * Windows' own folders are the stack's: ProgramData and Users\Public in the machine's state (as
        //     Environment.GetFolderPath has them), the user's profile (C:\Users\<name>, its AppData) the user's
        //     folders, Windows\Temp the temporary folder;
        //   * anything else on a drive is in a folder for that drive in the machine's state (Drives/C/...), so a
        //     path the application makes for itself (C:\Logs\app.log) is somewhere it can write;
        //   * a server's share is where the system has it mounted (macOS's /Volumes, a CIFS mount, GNOME's gvfs);
        //     one that is not mounted stays as it was.

        private static string Locate(string path)
        {
            string p = path.Replace('/', '\\');
            if (p.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)) p = @"\\" + p.Substring(8);
            else if (p.StartsWith(@"\\?\", StringComparison.Ordinal) || p.StartsWith(@"\\.\", StringComparison.Ordinal)) p = p.Substring(4);
            if (p.StartsWith(@"\\", StringComparison.Ordinal)) return Share(p) ?? path;
            if (p.Length < 2 || p[1] != ':' || !char.IsAsciiLetter(p[0])) return path;
            if (p.Length > 2 && p[2] != '\\') return path;               // "C:name": relative to a drive's own current folder
            string rest = p.Length > 3 ? p.Substring(3).TrimEnd('\\') : "";

            if (FromTable(p) is { } mapped) return mapped;
            if (KnownFolder(rest) is { } known) return known;
            string state = StateDirectory();
            if (state == null) return path;
            string drive = Path.Combine(state, "Drives", char.ToUpperInvariant(p[0]).ToString());
            try { Directory.CreateDirectory(drive); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            return rest.Length == 0 ? drive : Path.Combine(drive, rest.Replace('\\', '/'));
        }

        /// <summary>The machine's state folder: the launcher's (WpfWebGpu.StateDirectory) or a bundle's.</summary>
        internal static string StateDirectory()
        {
            if (StateOverride != null) return StateOverride;
            string state = AppContext.GetData("WpfWebGpu.StateDirectory") as string;
            if (string.IsNullOrEmpty(state)) state = AppContext.GetData("WPFWEBGPU_STATE_DIR") as string;
            if (string.IsNullOrEmpty(state)) state = Environment.GetEnvironmentVariable("WPFRUN_STATE");
            return string.IsNullOrEmpty(state) ? null : state;
        }

        /// <summary>For tests: the state folder, instead of the process's.</summary>
        internal static string StateOverride;

        // the mapping table, read again when its file changes (looked at every two seconds at most)
        private static (string Path, DateTime Stamp, (string From, string To)[] Rules) s_table;
        private static DateTime s_tableChecked;

        private static string FromTable(string windowsPath)
        {
            string state = StateDirectory();
            if (state == null) return null;
            string file = Path.Combine(state, "paths.json");
            (string From, string To)[] rules;
            if (s_table.Path == file && DateTime.UtcNow - s_tableChecked < TimeSpan.FromSeconds(2) && StateOverride == null)
                return Match(windowsPath, s_table.Rules);
            s_tableChecked = DateTime.UtcNow;
            try
            {
                DateTime stamp = File.Exists(file) ? File.GetLastWriteTimeUtc(file) : default;
                if (stamp == default) s_table = (file, default, Array.Empty<(string, string)>());
                if (stamp == default) return null;
                if (s_table.Path != file || s_table.Stamp != stamp)
                {
                    using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(file));
                    rules = doc.RootElement.EnumerateArray()
                        .Select(e => (From: (e.GetProperty("from").GetString() ?? "").Replace('/', '\\').TrimEnd('\\'), To: (e.GetProperty("to").GetString() ?? "").Replace('\\', '/').TrimEnd('/')))
                        .Where(r => r.From.Length > 0 && r.To.Length > 0)
                        .OrderByDescending(r => r.From.Length).ToArray();
                    s_table = (file, stamp, rules);
                }
                rules = s_table.Rules;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidOperationException or KeyNotFoundException)
            {
                return null;
            }
            return Match(windowsPath, rules);
        }

        private static string Match(string windowsPath, (string From, string To)[] rules)
        {
            if (rules == null) return null;
            foreach ((string from, string to) in rules)
            {
                if (!windowsPath.StartsWith(from, StringComparison.OrdinalIgnoreCase)) continue;
                if (windowsPath.Length > from.Length && windowsPath[from.Length] != '\\') continue;   // a whole segment
                string tail = windowsPath.Substring(from.Length).TrimStart('\\').TrimEnd('\\');
                return tail.Length == 0 ? to : Path.Combine(to, tail.Replace('\\', '/'));
            }
            return null;
        }

        private static string KnownFolder(string rest)
        {
            string Under(string root, int skip)
            {
                string tail = string.Join('/', rest.Split('\\').Skip(skip));
                return tail.Length == 0 ? root : Path.Combine(root, tail);
            }
            string[] parts = rest.Split('\\', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0) return null;
            string state = StateDirectory();
            bool Is(int i, string name) => parts.Length > i && parts[i].Equals(name, StringComparison.OrdinalIgnoreCase);
            if (Is(0, "ProgramData") && state != null) return Under(Path.Combine(state, "ProgramData"), 1);
            if (Is(0, "Users") && Is(1, "Public") && state != null) return Under(Path.Combine(state, "Public"), 2);
            if (Is(0, "Users") && parts.Length > 1 && (parts[1].Equals(Environment.UserName, StringComparison.OrdinalIgnoreCase) || parts[1].Equals("Default", StringComparison.OrdinalIgnoreCase)))
            {
                if (Is(2, "AppData") && Is(3, "Roaming")) return Under(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData, Environment.SpecialFolderOption.DoNotVerify), 4);
                if (Is(2, "AppData") && Is(3, "Local")) return Under(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.DoNotVerify), 4);
                return Under(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), 2);
            }
            if (Is(0, "Windows") && Is(1, "Temp")) return Under(Path.GetTempPath().TrimEnd('/'), 2);
            return null;
        }

        // ---- network shares ----

        /// <summary>For tests: the mounted shares, instead of the system's.</summary>
        internal static Func<IEnumerable<(string Server, string Share, string Root)>> MountsOverride;

        private static (DateTime At, (string Server, string Share, string Root)[] List) s_mounts;

        private static string Share(string unc)
        {
            string[] parts = unc.Substring(2).Split('\\', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2) return null;
            string server = parts[0], share = parts[1];
            foreach ((string s, string sh, string root) in Mounts())
            {
                if (!SameServer(s, server) || !share.Equals(sh, StringComparison.OrdinalIgnoreCase)) continue;
                string tail = string.Join('/', parts.Skip(2));
                return tail.Length == 0 ? root : Path.Combine(root, tail);
            }
            return null;
        }

        private static bool SameServer(string a, string b)
        {
            static string Bare(string n) => n.EndsWith(".local", StringComparison.OrdinalIgnoreCase) ? n.Substring(0, n.Length - 6) : n;
            return Bare(a).Equals(Bare(b), StringComparison.OrdinalIgnoreCase)
                || Bare(a).Split('.')[0].Equals(Bare(b).Split('.')[0], StringComparison.OrdinalIgnoreCase) && !char.IsDigit(a[0]) && !char.IsDigit(b[0]);
        }

        private static (string Server, string Share, string Root)[] Mounts()
        {
            if (MountsOverride != null) return MountsOverride().ToArray();
            if (s_mounts.List != null && DateTime.UtcNow - s_mounts.At < TimeSpan.FromSeconds(5)) return s_mounts.List;
            var list = new List<(string, string, string)>();
            try
            {
                if (OperatingSystem.IsMacOS()) list.AddRange(MacMounts());
                else if (OperatingSystem.IsLinux() && !OperatingSystem.IsAndroid()) list.AddRange(LinuxMounts());
            }
            catch (Exception) { }
            s_mounts = (DateTime.UtcNow, list.ToArray());
            return s_mounts.List;
        }

        // "//user@server/share on /Volumes/share (smbfs, nodev, ...)" -- mount(8)'s lines for SMB shares
        internal static IEnumerable<(string, string, string)> ParseMacMounts(string mountOutput)
        {
            foreach (string line in mountOutput.Split('\n'))
            {
                int on = line.IndexOf(" on ", StringComparison.Ordinal), paren = line.LastIndexOf(" (", StringComparison.Ordinal);
                if (!line.StartsWith("//", StringComparison.Ordinal) || on < 0 || paren < on || line.IndexOf("smbfs", paren, StringComparison.Ordinal) < 0) continue;
                string source = line.Substring(2, on - 2), root = line.Substring(on + 4, paren - on - 4);
                int at = source.IndexOf('@'), slash = source.IndexOf('/');
                if (slash < 0) continue;
                string server = source.Substring(at + 1, slash - at - 1), share = Uri.UnescapeDataString(source.Substring(slash + 1).TrimEnd('/'));
                yield return (server, share, root);
            }
        }

        private static IEnumerable<(string, string, string)> MacMounts()
        {
            var psi = new System.Diagnostics.ProcessStartInfo("/sbin/mount") { RedirectStandardOutput = true, UseShellExecute = false };
            using var p = System.Diagnostics.Process.Start(psi);
            string output = p?.StandardOutput.ReadToEnd() ?? "";
            p?.WaitForExit(2000);
            return ParseMacMounts(output);
        }

        // /proc/mounts' CIFS lines ("//server/share /mnt/point cifs ...", spaces as \040), and GNOME's gvfs folders
        // ($XDG_RUNTIME_DIR/gvfs/smb-share:server=host,share=name[,user=...])
        internal static IEnumerable<(string, string, string)> ParseLinuxMounts(string procMounts, IEnumerable<string> gvfsEntries)
        {
            foreach (string line in procMounts.Split('\n'))
            {
                string[] f = line.Split(' ');
                if (f.Length < 3 || (f[2] != "cifs" && f[2] != "smb3") || !f[0].StartsWith("//", StringComparison.Ordinal)) continue;
                string source = f[0].Substring(2).Replace("\\040", " "), root = f[1].Replace("\\040", " ");
                int slash = source.IndexOf('/');
                if (slash > 0) yield return (source.Substring(0, slash), source.Substring(slash + 1).TrimEnd('/'), root);
            }
            foreach (string entry in gvfsEntries)
            {
                string name = Path.GetFileName(entry);
                if (!name.StartsWith("smb-share:", StringComparison.Ordinal)) continue;
                string server = null, share = null;
                foreach (string kv in name.Substring(10).Split(','))
                {
                    int eq = kv.IndexOf('=');
                    if (eq < 0) continue;
                    string k = kv.Substring(0, eq), v = Uri.UnescapeDataString(kv.Substring(eq + 1));
                    if (k == "server") server = v;
                    else if (k == "share") share = v;
                }
                if (server != null && share != null) yield return (server, share, entry);
            }
        }

        private static IEnumerable<(string, string, string)> LinuxMounts()
        {
            string mounts = File.Exists("/proc/mounts") ? File.ReadAllText("/proc/mounts") : "";
            string runtime = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
            string gvfs = string.IsNullOrEmpty(runtime) ? null : Path.Combine(runtime, "gvfs");
            IEnumerable<string> entries = gvfs != null && Directory.Exists(gvfs) ? Directory.EnumerateDirectories(gvfs).ToArray() : Array.Empty<string>();
            return ParseLinuxMounts(mounts, entries);
        }

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
