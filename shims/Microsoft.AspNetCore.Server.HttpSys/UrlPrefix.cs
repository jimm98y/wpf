// A URL prefix as http.sys registers one: scheme://host:port/path/ -- the host + (every name, before all else),
// * (every name no other prefix claims) or a name or address, the path ending in a slash -- parsed as ASP.NET Core
// parses it.

using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Microsoft.AspNetCore.Server.HttpSys
{
    public class UrlPrefix
    {
        private UrlPrefix(bool isHttps, string scheme, string host, string port, int portValue, string path)
        {
            IsHttps = isHttps;
            Scheme = scheme;
            Host = host;
            Port = port;
            PortValue = portValue;
            Path = path;
            PathWithoutTrailingSlash = Path.Length > 1 ? Path[..^1] : string.Empty;
            FullPrefix = string.Format(CultureInfo.InvariantCulture, "{0}://{1}:{2}{3}", Scheme, Host, Port, Path);
        }

        public static UrlPrefix Create(string scheme, string host, string port, string path)
        {
            int? portValue = null;
            if (!string.IsNullOrWhiteSpace(port)) portValue = int.Parse(port, NumberStyles.None, CultureInfo.InvariantCulture);
            return Create(scheme, host, portValue, path);
        }

        public static UrlPrefix Create(string scheme, string host, int? portValue, string path)
        {
            bool isHttps;
            if (string.Equals("http", scheme, StringComparison.OrdinalIgnoreCase)) { scheme = "http"; isHttps = false; }
            else if (string.Equals("https", scheme, StringComparison.OrdinalIgnoreCase)) { scheme = "https"; isHttps = true; }
            else throw new ArgumentOutOfRangeException(nameof(scheme), scheme, "Unsupported scheme: " + scheme);

            if (string.IsNullOrWhiteSpace(host)) throw new ArgumentNullException(nameof(host));

            string port;
            if (!portValue.HasValue)
            {
                port = isHttps ? "443" : "80";
                portValue = isHttps ? 443 : 80;
            }
            else
            {
                port = portValue.Value.ToString(CultureInfo.InvariantCulture);
            }

            // http.sys requires the path to end in a slash
            if (string.IsNullOrWhiteSpace(path)) path = "/";
            else if (!path.EndsWith('/')) path += "/";

            return new UrlPrefix(isHttps, scheme, host, port, portValue.Value, path);
        }

        public static UrlPrefix Create(string prefix)
        {
            string whole = prefix ?? string.Empty;
            int schemeDelimiterEnd = whole.IndexOf("://", StringComparison.Ordinal);
            if (schemeDelimiterEnd < 0) throw new FormatException("Invalid prefix, missing scheme separator: " + prefix);
            int hostDelimiterStart = schemeDelimiterEnd + "://".Length;

            int pathDelimiterStart = whole.IndexOf('/', hostDelimiterStart);
            if (pathDelimiterStart < 0) pathDelimiterStart = whole.Length;
            int hostDelimiterEnd = whole.LastIndexOf(':', pathDelimiterStart - 1, pathDelimiterStart - hostDelimiterStart);
            if (hostDelimiterEnd < 0) hostDelimiterEnd = pathDelimiterStart;

            string scheme = whole[..schemeDelimiterEnd];
            string portString = whole[hostDelimiterEnd..pathDelimiterStart];       // the leading ':' included
            string host;
            int? portValue = null;
            if (!string.IsNullOrEmpty(portString)
                && int.TryParse(portString.AsSpan(1), NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed))
            {
                host = whole[hostDelimiterStart..hostDelimiterEnd];
                portValue = parsed;
            }
            else
            {
                host = whole[hostDelimiterStart..pathDelimiterStart];             // an IPv6 address's last colon, not a port's
            }
            string path = whole[pathDelimiterStart..];
            return Create(scheme, host, portValue, path);
        }

        public bool IsHttps { get; }
        public string Scheme { get; }
        public string Host { get; }
        public string Port { get; }
        internal string HostAndPort => Host + ":" + Port;
        public int PortValue { get; }
        public string Path { get; }
        internal string PathWithoutTrailingSlash { get; }
        public string FullPrefix { get; }

        public override bool Equals(object? obj) => string.Equals(FullPrefix, Convert.ToString(obj, CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase);
        public override int GetHashCode() => StringComparer.OrdinalIgnoreCase.GetHashCode(FullPrefix);
        public override string ToString() => FullPrefix;
    }

    public class UrlPrefixCollection : ICollection<UrlPrefix>
    {
        private readonly List<UrlPrefix> _prefixes = new();

        internal UrlPrefixCollection() { }

        public int Count { get { lock (_prefixes) return _prefixes.Count; } }
        public bool IsReadOnly => false;

        public void Add(string prefix) => Add(UrlPrefix.Create(prefix));
        public void Add(UrlPrefix item) { lock (_prefixes) _prefixes.Add(item); }
        public void Clear() { lock (_prefixes) _prefixes.Clear(); }
        public bool Contains(UrlPrefix item) { lock (_prefixes) return _prefixes.Contains(item); }
        public void CopyTo(UrlPrefix[] array, int arrayIndex) { lock (_prefixes) _prefixes.CopyTo(array, arrayIndex); }
        public bool Remove(string prefix) => Remove(UrlPrefix.Create(prefix));
        public bool Remove(UrlPrefix item) { lock (_prefixes) return _prefixes.Remove(item); }
        public IEnumerator<UrlPrefix> GetEnumerator() { lock (_prefixes) return _prefixes.ToList().GetEnumerator(); }
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        internal UrlPrefix[] Snapshot() { lock (_prefixes) return _prefixes.ToArray(); }
    }
}
