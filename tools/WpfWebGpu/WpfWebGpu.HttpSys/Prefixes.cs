// A registered URL prefix, as the registry keeps it (http.sys's normalized form, scheme://host:port/path/), and
// http.sys's choice among prefixes for a request: the strong wildcard's (+) first, then a named host's, then the
// weak wildcard's (*); within each, the longest path.

using System.Globalization;
using System.Net;

namespace WpfWebGpu.HttpSys;

public sealed record SharedPrefix(bool Https, string Host, int Port, string Path)
{
    public string FullPrefix => $"{(Https ? "https" : "http")}://{Host}:{Port.ToString(CultureInfo.InvariantCulture)}{Path}";

    /// <summary>A normalized prefix (the form http.sys and UrlPrefix.FullPrefix give: the port explicit, the path
    /// ending in '/'); null for anything else.</summary>
    public static SharedPrefix? Parse(string full)
    {
        int scheme = full.IndexOf("://", StringComparison.Ordinal);
        if (scheme < 0) return null;
        int pathAt = full.IndexOf('/', scheme + 3);
        if (pathAt < 0) return null;
        int colon = full.LastIndexOf(':', pathAt - 1, pathAt - scheme - 3);
        if (colon < 0 || !int.TryParse(full.AsSpan(colon + 1, pathAt - colon - 1), NumberStyles.None, CultureInfo.InvariantCulture, out int port)) return null;
        string s = full[..scheme];
        bool https = s.Equals("https", StringComparison.OrdinalIgnoreCase);
        if (!https && !s.Equals("http", StringComparison.OrdinalIgnoreCase)) return null;
        string path = full[pathAt..];
        return new SharedPrefix(https, full[(scheme + 3)..colon], port, path.EndsWith('/') ? path : path + "/");
    }
}

public static class HttpSysRouting
{
    /// <summary>The prefix (of <paramref name="prefixes"/>, each with what it stands for) that claims the request,
    /// as http.sys chooses it; default where none does.</summary>
    public static T? Best<T>(IEnumerable<T> prefixes, Func<T, SharedPrefix> prefixOf, bool https, string host, IPAddress? localAddress, int port, string path)
    {
        string? local = localAddress?.ToString();
        T? match = default;
        SharedPrefix? best = null;
        int rank = int.MaxValue;
        foreach (var item in prefixes)
        {
            var p = prefixOf(item);
            if (p.Https != https || (p.Port != 0 && p.Port != port)) continue;
            int r = p.Host == "+" ? 0 : p.Host == "*" ? 2 : 1;
            if (r == 1)
            {
                string h = p.Host.StartsWith('[') && p.Host.EndsWith(']') ? p.Host[1..^1] : p.Host;
                if (!string.Equals(h, host.Trim('[', ']'), StringComparison.OrdinalIgnoreCase)
                    && !(local != null && IPAddress.TryParse(h, out var ip) && ip.ToString() == local)) continue;
            }
            if (!(path + "/").StartsWith(p.Path, StringComparison.OrdinalIgnoreCase)) continue;
            if (best == null || r < rank || (r == rank && p.Path.Length > best.Path.Length)) { match = item; best = p; rank = r; }
        }
        return match;
    }
}
