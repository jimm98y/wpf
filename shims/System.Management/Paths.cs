// ManagementPath and ManagementScope: WMI object paths and the namespace a query runs in.
//
//   \\server\root\cimv2:Win32_LogicalDisk.DeviceID="C:"     full path
//   root\cimv2                                               a namespace
//   Win32_LogicalDisk                                        a class
//   Win32_LogicalDisk.DeviceID="C:",Other=5                  an instance (relative path)
//   Win32_OperatingSystem=@                                  a singleton

using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace System.Management
{
    public partial class ManagementPath : ICloneable
    {
        private static ManagementPath s_defaultPath = new ManagementPath(@"\\.\root\cimv2");

        private string _server = "";
        private string _namespace = "";
        private string _className = "";
        private bool _singleton;
        // Key name to value as written in the path: a string (quotes removed) or a number's text.
        private List<KeyValuePair<string, string>> _keys = new List<KeyValuePair<string, string>>();

        public ManagementPath() { }

        public ManagementPath(string path)
        {
            if (!string.IsNullOrEmpty(path)) Parse(path);
        }

        [RefreshProperties(RefreshProperties.All)]
        public string ClassName
        {
            get => _className;
            set { _className = value ?? ""; _keys.Clear(); _singleton = false; }
        }

        public static ManagementPath DefaultPath
        {
            get => s_defaultPath;
            set => s_defaultPath = value ?? throw new ArgumentNullException(nameof(value));
        }

        public bool IsClass => _className.Length > 0 && _keys.Count == 0 && !_singleton;

        public bool IsInstance => _className.Length > 0 && (_keys.Count > 0 || _singleton);

        public bool IsSingleton => _singleton;

        [RefreshProperties(RefreshProperties.All)]
        public string NamespacePath
        {
            get => _namespace;
            set
            {
                string v = (value ?? "").Replace('/', '\\');
                if (v.StartsWith(@"\\", StringComparison.Ordinal))
                {
                    int slash = v.IndexOf('\\', 2);
                    _server = slash < 0 ? v.Substring(2) : v.Substring(2, slash - 2);
                    v = slash < 0 ? "" : v.Substring(slash + 1);
                }
                _namespace = v.TrimStart('\\');
            }
        }

        [RefreshProperties(RefreshProperties.All)]
        public string Path
        {
            get
            {
                var sb = new StringBuilder();
                if (_server.Length > 0) sb.Append(@"\\").Append(_server).Append('\\');
                sb.Append(_namespace);
                string rel = RelativePath;
                if (rel.Length > 0)
                {
                    if (sb.Length > 0) sb.Append(':');
                    sb.Append(rel);
                }
                return sb.ToString();
            }
            set
            {
                _server = ""; _namespace = ""; _className = ""; _singleton = false; _keys.Clear();
                if (!string.IsNullOrEmpty(value)) Parse(value);
            }
        }

        [RefreshProperties(RefreshProperties.All)]
        public string RelativePath
        {
            get
            {
                if (_className.Length == 0) return "";
                if (_singleton) return _className + "=@";
                if (_keys.Count == 0) return _className;
                var sb = new StringBuilder(_className).Append('.');
                for (int i = 0; i < _keys.Count; i++)
                {
                    if (i > 0) sb.Append(',');
                    sb.Append(_keys[i].Key).Append('=').Append(FormatKeyValue(_keys[i].Value));
                }
                return sb.ToString();
            }
            set
            {
                _className = ""; _singleton = false; _keys.Clear();
                if (!string.IsNullOrEmpty(value)) ParseRelative(value);
            }
        }

        [RefreshProperties(RefreshProperties.All)]
        public string Server
        {
            get => _server;
            set => _server = value ?? "";
        }

        internal IReadOnlyList<KeyValuePair<string, string>> Keys => _keys;

        public ManagementPath Clone()
        {
            var p = (ManagementPath)MemberwiseClone();
            p._keys = new List<KeyValuePair<string, string>>(_keys);
            return p;
        }

        public void SetAsClass()
        {
            _keys.Clear();
            _singleton = false;
        }

        public void SetAsSingleton()
        {
            _keys.Clear();
            _singleton = true;
        }

        object ICloneable.Clone() => Clone();

        public override string ToString() => Path;

        private void Parse(string path)
        {
            // '/' separates server and namespace like '\' does -- there only, never in a key value.
            string p = path.Trim();
            int ns = IndexOutsideQuotes(p, ':');
            p = ns < 0 && IndexOutsideQuotes(p, '=') >= 0 ? p : (ns < 0 ? p.Replace('/', '\\') : p.Substring(0, ns).Replace('/', '\\') + p.Substring(ns));
            string server = "";
            if (p.StartsWith(@"\\", StringComparison.Ordinal))
            {
                int slash = p.IndexOf('\\', 2);
                server = slash < 0 ? p.Substring(2) : p.Substring(2, slash - 2);
                p = slash < 0 ? "" : p.Substring(slash + 1);
                _server = server;
            }

            // The namespace ends at the first ':' outside a quoted key value.
            int colon = IndexOutsideQuotes(p, ':');
            if (colon >= 0)
            {
                _namespace = p.Substring(0, colon);
                ParseRelative(p.Substring(colon + 1));
            }
            else if (server.Length > 0 || (p.IndexOf('\\') >= 0 && IndexOutsideQuotes(p, '.') < 0 && p.IndexOf('=') < 0))
            {
                _namespace = p;                 // \\server\root\cimv2, root\cimv2
            }
            else
            {
                ParseRelative(p);
            }
        }

        private void ParseRelative(string rel)
        {
            rel = rel.Trim();
            int dot = IndexOutsideQuotes(rel, '.');
            int eq = IndexOutsideQuotes(rel, '=');
            if (dot < 0 && eq < 0)
            {
                _className = rel;
                return;
            }
            if (dot < 0 || (eq >= 0 && eq < dot))
            {
                // Class=@ (singleton) or Class="value" (the class's single, unnamed key).
                _className = rel.Substring(0, eq);
                string v = rel.Substring(eq + 1).Trim();
                if (v == "@") _singleton = true;
                else _keys.Add(new KeyValuePair<string, string>("", Unquote(v)));
                return;
            }
            _className = rel.Substring(0, dot);
            foreach (string pair in SplitOutsideQuotes(rel.Substring(dot + 1), ','))
            {
                int e = IndexOutsideQuotes(pair, '=');
                if (e < 0) throw new ArgumentOutOfRangeException("path");
                _keys.Add(new KeyValuePair<string, string>(pair.Substring(0, e).Trim(), Unquote(pair.Substring(e + 1).Trim())));
            }
        }

        private static string FormatKeyValue(string v)
        {
            // Numbers stay bare; everything else is a quoted string with \ and " escaped.
            if (v.Length > 0 && (char.IsDigit(v[0]) || v[0] == '-') && double.TryParse(v, Globalization.NumberStyles.Float, Globalization.CultureInfo.InvariantCulture, out _))
                return v;
            return "\"" + v.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
        }

        private static string Unquote(string v)
        {
            if (v.Length >= 2 && (v[0] == '"' || v[0] == '\'') && v[v.Length - 1] == v[0])
            {
                var sb = new StringBuilder();
                for (int i = 1; i < v.Length - 1; i++)
                {
                    if (v[i] == '\\' && i + 1 < v.Length - 1) i++;
                    sb.Append(v[i]);
                }
                return sb.ToString();
            }
            return v;
        }

        private static int IndexOutsideQuotes(string s, char c)
        {
            char quote = '\0';
            for (int i = 0; i < s.Length; i++)
            {
                char ch = s[i];
                if (quote != '\0')
                {
                    if (ch == '\\') i++;
                    else if (ch == quote) quote = '\0';
                }
                else if (ch == '"' || ch == '\'') quote = ch;
                else if (ch == c) return i;
            }
            return -1;
        }

        private static IEnumerable<string> SplitOutsideQuotes(string s, char c)
        {
            while (true)
            {
                int i = IndexOutsideQuotes(s, c);
                if (i < 0) { yield return s; yield break; }
                yield return s.Substring(0, i);
                s = s.Substring(i + 1);
            }
        }
    }

    public partial class ManagementScope : ICloneable
    {
        private ManagementPath _path;
        private ConnectionOptions _options;
        private bool _connected;

        public ManagementScope() : this((ManagementPath)null, null) { }

        public ManagementScope(ManagementPath path) : this(path, null) { }

        public ManagementScope(ManagementPath path, ConnectionOptions options)
        {
            _path = path?.Clone();
            _options = options;
        }

        public ManagementScope(string path) : this(new ManagementPath(path), null) { }

        public ManagementScope(string path, ConnectionOptions options) : this(new ManagementPath(path), options) { }

        public bool IsConnected => _connected;

        public ConnectionOptions Options
        {
            get => _options ??= new ConnectionOptions();
            set { _options = value ?? throw new ArgumentNullException(nameof(value)); _connected = false; }
        }

        public ManagementPath Path
        {
            get => _path ??= ManagementPath.DefaultPath.Clone();
            set { _path = value ?? throw new ArgumentNullException(nameof(value)); _connected = false; }
        }

        public ManagementScope Clone()
        {
            var s = new ManagementScope(_path, (ConnectionOptions)_options?.Clone());
            s._connected = _connected;
            return s;
        }

        object ICloneable.Clone() => Clone();

        public void Connect()
        {
            if (_connected) return;
            string server = Path.Server;
            if (!Cimv2.IsLocalServer(server))
            {
                // What DCOM says about a machine it cannot reach.
                throw new COMException("The RPC server is unavailable. (0x800706BA)", unchecked((int)0x800706BA));
            }
            if (!Cimv2.IsKnownNamespace(EffectiveNamespace))
            {
                throw new ManagementException(ManagementStatus.InvalidNamespace);
            }
            _connected = true;
        }

        /// <summary>The namespace queries run in: the scope's, or root\cimv2 when it names none.</summary>
        internal string EffectiveNamespace => Path.NamespacePath.Length > 0 ? Path.NamespacePath : ManagementPath.DefaultPath.NamespacePath;

        internal string EffectiveServer => Path.Server.Length > 0 ? Path.Server : ".";
    }
}
