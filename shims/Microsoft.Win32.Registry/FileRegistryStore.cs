// The NON-WINDOWS half of the registry: a per-user file, one per hive.
//
// The in-memory tree this replaces was already enough to keep apps running -- reads answered with
// the caller's default and writes went somewhere -- but it lost everything at exit, so an app that
// stores its window placement, its recent files or its user's preferences in the registry started
// from scratch every launch. On Windows those settings persist; off Windows they now do too.
//
// FORMAT: a line-based text file, close enough to a .reg to read at a glance and small enough to
// parse without a serializer. Deliberately not JSON: this assembly is loaded by the wasm and AOT
// heads, where pulling in a reflection-based serializer is exactly the kind of thing that has
// already cost this repo an AOT build, and hand-parsing four line shapes is cheaper than the risk.
//
//     # HKEY_CURRENT_USER
//     [Software\Contoso\App]
//     "WindowTitle"=str:Hello%20world
//     "Left"=dword:0000012c
//     "Recent"=multi:one%00two
//     "Blob"=bin:AQIDBA==
//
// WHERE: $WPFWEBGPU_REGISTRY_DIR if set, else the platform's per-user config location
// (XDG_CONFIG_HOME or ~/.config on Linux, ~/Library/Application Support on macOS). The store is
// shared by every app built on this stack, which is what the real registry is: a machine-wide store
// apps read each other's corners of, not an app-private settings file.
//
// Every failure degrades to the in-memory behaviour that came before: a read-only home directory, a
// sandbox, or a browser with no persistent filesystem costs persistence, never a crash.

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;

namespace Microsoft.Win32
{
    internal sealed class FileRegistryStore
    {
        private readonly string _hiveName;
        private string _path;
        private bool _resolved;
        private readonly object _gate = new object();
        private bool _broken;           // one failed write is enough; stop trying and stay in memory

        // ---- shared by every process, as the registry is ----------------------------------------
        //
        // The file is the hive of every process of the machine (its services, its apps), as Windows' registry is one
        // store: what one process writes another reads. So a process is told when the file changes (a watcher: FSEvents
        // on macOS, inotify on Linux -- a read costs nothing until then) and reads it again, where its time and size are
        // not what this process last read or wrote; where no watcher can be had (the browser, a sandbox) it looks at
        // the file at most every 50 ms instead. A write is made under a lock every process takes (a file beside the
        // hive, flock'd), over the file as it is then -- looked at, not told -- never over what this process read
        // earlier, which would undo what the others wrote since.
        private (long Time, long Length) _stamp;
        private long _checkedAt;
        private FileSystemWatcher _watcher;
        private volatile bool _told;        // the watcher said the file changed
        private readonly object _writeGate = new object();
        private int _lockDepth;
        private FileStream _lockFile;

        private (long, long) CurrentStamp()
        {
            try
            {
                var info = new FileInfo(_path);
                return info.Exists ? (info.LastWriteTimeUtc.Ticks, info.Length) : (0, 0);
            }
            catch { return _stamp; }
        }

        /// <summary>Whether the file changed since this process last read or wrote it; looked at at most every 50 ms
        /// unless <paramref name="now"/>.</summary>
        internal bool ChangedOnDisk(bool now = false)
        {
            if (!Available) return false;
            if (!now && _watcher != null)
            {
                if (!_told) return false;
                _told = false;              // this process's own saves are told too: their stamp is the one it keeps
                return CurrentStamp() != _stamp;
            }
            long tick = Environment.TickCount64;
            if (!now && tick - Volatile.Read(ref _checkedAt) < 50) return false;
            Volatile.Write(ref _checkedAt, tick);
            return CurrentStamp() != _stamp;
        }

        // told when the file changes, from the first read on; where none can be had, looked at instead (ChangedOnDisk)
        private void Watch()
        {
            if (_watcher != null) return;
            try
            {
                var w = new FileSystemWatcher(Path.GetDirectoryName(_path), Path.GetFileName(_path))
                {
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
                };
                FileSystemEventHandler told = (_, _) => _told = true;
                w.Changed += told;
                w.Created += told;
                w.Deleted += told;
                w.Renamed += (_, _) => _told = true;     // the temp file moved over the hive: how every save lands
                w.Error += (_, _) => _told = true;
                w.EnableRaisingEvents = true;
                _watcher = w;
            }
            catch { _watcher = null; }
        }

        /// <summary>The hive's write lock, every process's (reentrant in this one): <paramref name="onLocked"/> runs once
        /// it is taken, to read the file again where another process changed it.</summary>
        internal IDisposable Lock(Action onLocked)
        {
            if (!Available) return null;
            Monitor.Enter(_writeGate);
            if (_lockDepth++ == 0)
            {
                var until = Environment.TickCount64 + 5000;
                while (_lockFile == null)
                {
                    try { _lockFile = new FileStream(_path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
                    catch (IOException) when (Environment.TickCount64 < until) { Thread.Sleep(5); }
                    catch { break; }        // no lock to be had (read-only): write as before
                }
                try { onLocked(); } catch { }
            }
            return new Unlock(this);
        }

        private sealed class Unlock : IDisposable
        {
            private FileRegistryStore _store;
            public Unlock(FileRegistryStore store) => _store = store;
            public void Dispose()
            {
                var s = Interlocked.Exchange(ref _store, null);
                if (s == null) return;
                if (--s._lockDepth == 0)
                {
                    s._lockFile?.Dispose();
                    s._lockFile = null;
                }
                Monitor.Exit(s._writeGate);
            }
        }

        internal FileRegistryStore(string hiveName)
        {
            _hiveName = hiveName;
        }

        // Where the hive is, decided when it is first read or written, not when the hives are made: they are
        // made by Registry's static initializer, on the first touch of anything registry-shaped -- which can be
        // before a host has said where the machine's registry is (WPFWEBGPU_REGISTRY_DIR), and a store that
        // decided then read an empty registry from the default place for the rest of the process.
        private void Resolve()
        {
            if (_resolved) return;
            _resolved = true;
            _tokens = Tokens();
            string dir = null;
            try
            {
                dir = Environment.GetEnvironmentVariable("WPFWEBGPU_REGISTRY_DIR");
                if (string.IsNullOrEmpty(dir))
                {
                    string config = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
                    if (string.IsNullOrEmpty(config))
                    {
                        // ApplicationData maps to ~/.config on Linux and ~/Library/Application Support
                        // on macOS, which is where each expects a per-user store to live.
                        config = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                    }
                    if (!string.IsNullOrEmpty(config)) dir = Path.Combine(config, "wpf-webgpu", "registry");
                }

                if (!string.IsNullOrEmpty(dir))
                {
                    Directory.CreateDirectory(dir);
                    _path = Path.Combine(dir, _hiveName + ".reg");
                }
                else
                {
                    _broken = true;
                }
            }
            catch
            {
                _broken = true;         // no writable home: memory only, exactly as before
            }
        }

        internal bool Available
        {
            get
            {
                lock (_gate) Resolve();
                return !_broken && _path != null;
            }
        }

        // ---- reading ------------------------------------------------------------------------

        /// <summary>
        /// Reads the hive into <paramref name="root"/>. A malformed or half-written file is skipped
        /// line by line rather than thrown from: losing one value beats refusing to start.
        /// </summary>
        internal void Load(RegistryKey root)
        {
            if (!Available) return;

            lock (_gate)
            {
                try
                {
                    Watch();
                    _stamp = CurrentStamp();
                    _checkedAt = Environment.TickCount64;
                    if (!File.Exists(_path)) return;

                    RegistryKey current = root;
                    foreach (string raw in File.ReadAllLines(_path))
                    {
                        string line = raw.Trim();
                        if (line.Length == 0 || line[0] == '#') continue;

                        if (line[0] == '[' && line[line.Length - 1] == ']')
                        {
                            string path = line.Substring(1, line.Length - 2);
                            current = string.IsNullOrEmpty(path) ? root : root.CreateSubKey(path);
                            continue;
                        }

                        if (current == null) continue;

                        int eq = line.IndexOf("\"=", StringComparison.Ordinal);
                        if (line[0] != '"' || eq < 0) continue;

                        string name = Unescape(line.Substring(1, eq - 1));
                        string rest = line.Substring(eq + 2);
                        int colon = rest.IndexOf(':');
                        if (colon < 0) continue;

                        string kind = rest.Substring(0, colon);
                        string payload = rest.Substring(colon + 1);
                        if (TryParse(kind, payload, out object value)) current.SetValueLoaded(name, value);
                    }
                }
                catch
                {
                    // Unreadable store: carry on with whatever loaded, in memory.
                }
            }
        }

        private bool TryParse(string kind, string payload, out object value)
        {
            value = null;
            try
            {
                switch (kind)
                {
                    case "str":
                    case "expand":
                        value = Expand(Unescape(payload));
                        return true;
                    case "dword":
                        value = unchecked((int)Convert.ToUInt32(payload, 16));
                        return true;
                    case "qword":
                        value = unchecked((long)Convert.ToUInt64(payload, 16));
                        return true;
                    case "multi":
                        value = Expand(Unescape(payload)).Split('\0', StringSplitOptions.RemoveEmptyEntries);
                        return true;
                    case "bin":
                        value = Convert.FromBase64String(payload);
                        return true;
                    default:
                        return false;
                }
            }
            catch
            {
                return false;
            }
        }

        // ---- writing ------------------------------------------------------------------------

        /// <summary>
        /// Rewrites the whole hive. Whole-file because these stores are small (kilobytes) and the
        /// alternative -- editing in place -- is where a settings file becomes corrupt.
        /// </summary>
        internal void Save(RegistryKey root)
        {
            if (!Available) return;

            lock (_gate)
            {
                try
                {
                    var sb = new StringBuilder();
                    sb.Append("# ").Append(root.Name).Append('\n');
                    sb.Append("# Written by the WPF-on-WebGPU Microsoft.Win32.Registry stand-in.\n");
                    Write(sb, root, string.Empty);

                    // Temp file then move: a process killed mid-write leaves the previous store
                    // intact rather than a truncated one, and a truncated store is indistinguishable
                    // from "the user had no settings".
                    string temp = _path + ".tmp";
                    File.WriteAllText(temp, sb.ToString());
                    File.Move(temp, _path, overwrite: true);
                    _stamp = CurrentStamp();
                }
                catch
                {
                    _broken = true;     // read-only or sandboxed: stop trying, keep serving from memory
                }
            }
        }

        private void Write(StringBuilder sb, RegistryKey key, string path)
        {
            string[] valueNames = key.LoadedValueNames();
            if (valueNames.Length > 0 || path.Length > 0)
            {
                sb.Append('[').Append(path).Append(']').Append('\n');
                foreach (string name in valueNames)
                {
                    object v = key.GetLoadedValue(name);
                    if (v == null) continue;
                    sb.Append('"').Append(Escape(name)).Append("\"=").Append(Format(v)).Append('\n');
                }
            }

            foreach (string sub in key.LoadedSubKeyNames())
            {
                RegistryKey child = key.GetLoadedSubKey(sub);
                if (child != null) Write(sb, child, path.Length == 0 ? sub : path + "\\" + sub);
            }
        }

        private string Format(object v) =>
            v switch
            {
                int i => "dword:" + unchecked((uint)i).ToString("x8"),
                long l => "qword:" + unchecked((ulong)l).ToString("x16"),
                byte[] b => "bin:" + Convert.ToBase64String(b),
                string[] m => "multi:" + Escape(Contract(string.Join("\0", m))),
                _ => "str:" + Escape(Contract(Convert.ToString(v))),
            };

        // A store several programs share whose paths differ for each: the apps of an installation that is one
        // bundle per app (an iPad's), each with the installation in its own bundle. The store keeps the
        // installation's paths as tokens (${WPFRUN_INSTALLDIR}\...), and each program reads them as its own
        // paths and writes its own paths back as the tokens (WPFWEBGPU_REGISTRY_TOKENS: "TOKEN=path" lines).
        private (string Token, string Path)[] _tokens = Array.Empty<(string, string)>();

        private static (string, string)[] Tokens()
        {
            string table = Environment.GetEnvironmentVariable("WPFWEBGPU_REGISTRY_TOKENS");
            if (string.IsNullOrEmpty(table)) return Array.Empty<(string, string)>();
            var tokens = new List<(string Token, string Path)>();
            foreach (string line in table.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                int eq = line.IndexOf('=');
                if (eq > 0 && eq < line.Length - 1) tokens.Add((line[..eq], line[(eq + 1)..]));
            }
            // the longest path first: one inside another is its own token's
            tokens.Sort((x, y) => y.Path.Length.CompareTo(x.Path.Length));
            return tokens.ToArray();
        }

        private string Expand(string s)
        {
            if (_tokens.Length == 0 || string.IsNullOrEmpty(s) || s.IndexOf("${", StringComparison.Ordinal) < 0) return s;
            foreach (var (token, path) in _tokens) s = s.Replace(token, path, StringComparison.Ordinal);
            return s;
        }

        private string Contract(string s)
        {
            if (_tokens.Length == 0 || string.IsNullOrEmpty(s)) return s;
            foreach (var (token, path) in _tokens) s = s.Replace(path, token, StringComparison.Ordinal);
            return s;
        }

        // Percent-escaping, so that a newline, a quote or a NUL inside a value cannot end the line
        // it lives on. Kept to the few characters that would break the format rather than escaping
        // everything, so the file stays readable.
        private static string Escape(string s)
        {
            if (string.IsNullOrEmpty(s)) return string.Empty;
            var sb = new StringBuilder(s.Length);
            foreach (char c in s)
            {
                if (c == '%' || c == '"' || c == '\n' || c == '\r' || c == '\0')
                    sb.Append('%').Append(((int)c).ToString("X2"));
                else
                    sb.Append(c);
            }
            return sb.ToString();
        }

        private static string Unescape(string s)
        {
            if (string.IsNullOrEmpty(s) || s.IndexOf('%') < 0) return s ?? string.Empty;
            var sb = new StringBuilder(s.Length);
            for (int i = 0; i < s.Length; i++)
            {
                if (s[i] == '%' && i + 2 < s.Length &&
                    int.TryParse(s.Substring(i + 1, 2), System.Globalization.NumberStyles.HexNumber, null, out int code))
                {
                    sb.Append((char)code);
                    i += 2;
                }
                else
                {
                    sb.Append(s[i]);
                }
            }
            return sb.ToString();
        }
    }
}
