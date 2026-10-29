// wpfrun serve: a page of the browser head, served as it needs to be. A page whose app uses threads runs
// on .NET's multithreaded WebAssembly runtime (wpfrun bundle builds one), which needs SharedArrayBuffer,
// which a browser gives only to a cross-origin isolated page: one served with COOP and COEP headers. A
// plain static server (python -m http.server) sends neither, and the page never starts. This serves a
// folder on the loopback with both, the content types the runtime checks (.wasm, .mjs), and no caching
// (a rebuilt bundle is picked up by a reload).
//
// It also proxies other sites for the page's web views: a web site that forbids being framed by other
// origins (X-Frame-Options, CSP frame-ancestors -- the vendor's KB) cannot appear in the browser head's
// <iframe>, where on Windows WebView2 shows it as a browser does. Through here it is fetched for the
// frame without the headers that forbid framing, at http://<site host>.localhost:<port>/<its own path>
// (browsers resolve every *.localhost name to the loopback): its paths stay its own, which a single-page
// app routes by, and its cookies stay scoped to it. Its URLs of itself and its redirects are rewritten
// to come back through here. Only loopback clients are answered.

using System.Net;
using System.Net.Http;
using System.Text;

namespace WpfWebGpu;

internal static class Serve
{
    private static readonly Dictionary<string, string> s_types = new(StringComparer.OrdinalIgnoreCase)
    {
        [".html"] = "text/html; charset=utf-8", [".js"] = "text/javascript", [".mjs"] = "text/javascript",
        [".json"] = "application/json", [".wasm"] = "application/wasm", [".css"] = "text/css",
        [".png"] = "image/png", [".jpg"] = "image/jpeg", [".svg"] = "image/svg+xml", [".ico"] = "image/x-icon",
        [".ttf"] = "font/ttf", [".ttc"] = "font/collection", [".otf"] = "font/otf", [".woff2"] = "font/woff2",
        [".dat"] = "application/octet-stream", [".pdb"] = "application/octet-stream", [".txt"] = "text/plain; charset=utf-8",
        [".xml"] = "application/xml", [".config"] = "application/xml",
    };

    public static int Run(string[] args)
    {
        int port = 8080;
        string? dir = null;
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] is "--port" or "-p") port = int.Parse(args[++i], System.Globalization.CultureInfo.InvariantCulture);
            else if (args[i].StartsWith('-')) throw new UserError($"unknown option '{args[i]}'.");
            else dir = args[i];
        }
        if (dir == null) throw new UserError("which page? Pass the folder that holds its index.html (a bundle's wwwroot).");
        string root = Path.GetFullPath(Directory.Exists(Path.Combine(dir, "wwwroot")) ? Path.Combine(dir, "wwwroot") : dir);
        if (!File.Exists(Path.Combine(root, "index.html"))) throw new UserError($"{root} has no index.html.");

        using var listener = new HttpListener();
        // Every host name, for the proxied sites' *.localhost ones (HttpListener matches the Host header
        // against its prefixes); Answer refuses any client not on the loopback.
        listener.Prefixes.Add($"http://*:{port}/");
        s_port = port;
        listener.Start();
        Console.WriteLine($"wpfrun serve: http://127.0.0.1:{port}/ -> {root} (cross-origin isolated); Ctrl+C to stop");
        while (listener.IsListening)
        {
            HttpListenerContext ctx;
            try { ctx = listener.GetContext(); }
            catch (HttpListenerException) { break; }
            _ = Task.Run(() => Answer(ctx, root));
        }
        return 0;
    }

    private static void Answer(HttpListenerContext ctx, string root)
    {
        HttpListenerResponse res = ctx.Response;
        try
        {
            string rel = Uri.UnescapeDataString(ctx.Request.Url!.AbsolutePath).TrimStart('/');
            if (rel.Length == 0) rel = "index.html";
            // The probe a page makes to learn that this server proxies (browser-webview.js).
            if (rel is "__proxy" or "__proxy/")
            {
                res.StatusCode = 204;
                return;
            }
            if (!ctx.Request.RemoteEndPoint.Address.Equals(IPAddress.Loopback) && !ctx.Request.RemoteEndPoint.Address.Equals(IPAddress.IPv6Loopback)
                && !(ctx.Request.RemoteEndPoint.Address.IsIPv4MappedToIPv6 && ctx.Request.RemoteEndPoint.Address.MapToIPv4().Equals(IPAddress.Loopback)))
            {
                res.StatusCode = 403;
                return;
            }
            if (SiteOf(ctx.Request.Url) is Uri target)
            {
                Proxy(ctx, target);
                return;
            }
            string path = Path.GetFullPath(Path.Combine(root, rel));
            res.Headers["Cross-Origin-Opener-Policy"] = "same-origin";
            res.Headers["Cross-Origin-Embedder-Policy"] = "require-corp";
            res.Headers["Cross-Origin-Resource-Policy"] = "same-origin";
            res.Headers["Cache-Control"] = "no-store";
            if (!path.StartsWith(root, StringComparison.Ordinal) || !File.Exists(path))
            {
                res.StatusCode = 404;
                return;
            }
            res.ContentType = s_types.TryGetValue(Path.GetExtension(path), out string? type) ? type : "application/octet-stream";
            using var file = File.OpenRead(path);
            res.ContentLength64 = file.Length;
            file.CopyTo(res.OutputStream);
        }
        catch (Exception e) when (e is IOException or HttpListenerException or UnauthorizedAccessException or HttpRequestException)
        {
            try { res.StatusCode = 500; } catch (InvalidOperationException) { }
        }
        finally
        {
            try { res.Close(); } catch (Exception) { }
        }
    }

    // ---- the framing proxy ----------------------------------------------------------------------

    // A site at https://host is proxied at http://host.localhost:port, one at http://host at
    // http://host.insecure.localhost:port.
    private const string ProxySuffix = ".localhost", InsecureSuffix = ".insecure.localhost";
    private static int s_port;

    private static readonly HttpClient s_http = new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        AutomaticDecompression = DecompressionMethods.All,
        UseCookies = false,   // the page's browser keeps them, as it would for the site itself
    });

    /// <summary>The site URL a proxy URL stands for; null for the bundle's own.</summary>
    private static Uri? SiteOf(Uri? url)
    {
        if (url == null || url.Port != s_port || !url.Host.EndsWith(ProxySuffix, StringComparison.OrdinalIgnoreCase)) return null;
        bool insecure = url.Host.EndsWith(InsecureSuffix, StringComparison.OrdinalIgnoreCase);
        string host = url.Host.Substring(0, url.Host.Length - (insecure ? InsecureSuffix : ProxySuffix).Length);
        if (host.Length == 0) return null;
        return Uri.TryCreate($"{(insecure ? "http" : "https")}://{host}{url.PathAndQuery}", UriKind.Absolute, out Uri? u) ? u : null;
    }

    private static string ProxyOrigin(Uri site) => $"http://{site.Host}{(site.Scheme == "http" ? InsecureSuffix : ProxySuffix)}:{s_port}";

    /// <summary>A site URL as the proxy's; one it cannot stand for (another port, another scheme) unchanged.</summary>
    private static string ToProxy(Uri u) =>
        u.Scheme is "http" or "https" && u.IsDefaultPort ? ProxyOrigin(u) + u.PathAndQuery + u.Fragment : u.ToString();

    private static readonly HashSet<string> s_hopHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Host", "Connection", "Keep-Alive", "Transfer-Encoding", "Upgrade", "Proxy-Connection", "TE", "Trailer",
        "Accept-Encoding", "Content-Length", "Content-Type", "Origin", "Referer",
    };

    // What would forbid the page in a frame, or tie it to its real origin, is not passed on.
    private static readonly HashSet<string> s_droppedResponseHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "X-Frame-Options", "Content-Security-Policy", "Content-Security-Policy-Report-Only", "Strict-Transport-Security",
        "Cross-Origin-Opener-Policy", "Cross-Origin-Embedder-Policy", "Cross-Origin-Resource-Policy",
        "Content-Length", "Content-Encoding", "Transfer-Encoding", "Connection", "Keep-Alive", "Set-Cookie", "Location",
        "Content-Type", "Alt-Svc", "Report-To", "NEL",
    };

    private static void Proxy(HttpListenerContext ctx, Uri target)
    {
        HttpListenerRequest req = ctx.Request;
        HttpListenerResponse res = ctx.Response;
        using var msg = new HttpRequestMessage(new HttpMethod(req.HttpMethod), target);
        foreach (string? name in req.Headers.AllKeys)
            if (name != null && !s_hopHeaders.Contains(name))
                msg.Headers.TryAddWithoutValidation(name, req.Headers.GetValues(name) ?? Array.Empty<string>());
        if (req.Headers["Origin"] is { Length: > 0 } origin)
            msg.Headers.TryAddWithoutValidation("Origin", Uri.TryCreate(origin, UriKind.Absolute, out Uri? o) && SiteOf(new Uri(o, "/")) is Uri os
                ? os.GetLeftPart(UriPartial.Authority) : origin);
        if (req.UrlReferrer is { } referer) msg.Headers.Referrer = SiteOf(referer) ?? referer;
        if (req.HasEntityBody)
        {
            var sent = new MemoryStream();
            req.InputStream.CopyTo(sent);
            msg.Content = new ByteArrayContent(sent.ToArray());
            if (req.ContentType is { Length: > 0 } ct) msg.Content.Headers.TryAddWithoutValidation("Content-Type", ct);
        }

        using HttpResponseMessage up = s_http.Send(msg, HttpCompletionOption.ResponseHeadersRead);
        res.StatusCode = (int)up.StatusCode;
        foreach (var h in up.Headers.Concat(up.Content.Headers))
            if (!s_droppedResponseHeaders.Contains(h.Key))
                foreach (string v in h.Value) res.AppendHeader(h.Key, v);
        // Framed in the app page, which is cross-origin isolated: embeddable there, and loading the
        // site's resources from its other origins without credentials, as a credentialless frame would.
        res.Headers["Cross-Origin-Embedder-Policy"] = "credentialless";
        res.Headers["Cross-Origin-Resource-Policy"] = "cross-origin";
        if (up.Headers.Location is { } location)
            res.AppendHeader("Location", ToProxy(location.IsAbsoluteUri ? location : new Uri(target, location)));
        if (up.Headers.TryGetValues("Set-Cookie", out var cookies))
            foreach (string c in cookies) res.AppendHeader("Set-Cookie", RewriteCookie(c));

        string? type = up.Content.Headers.ContentType?.ToString();
        if (type != null) res.ContentType = type;
        using Stream body = up.Content.ReadAsStream();
        if (type != null && IsText(type))
        {
            string? charset = up.Content.Headers.ContentType?.CharSet;
            Encoding enc;
            try { enc = string.IsNullOrEmpty(charset) ? Encoding.UTF8 : Encoding.GetEncoding(charset.Trim('"')); }
            catch (ArgumentException) { enc = Encoding.UTF8; }
            string text = new StreamReader(body, enc).ReadToEnd();
            byte[] bytes = enc.GetBytes(RewriteText(text, target));
            res.ContentLength64 = bytes.Length;
            res.OutputStream.Write(bytes);
        }
        else body.CopyTo(res.OutputStream);
    }

    private static bool IsText(string type) =>
        type.Contains("html", StringComparison.OrdinalIgnoreCase) || type.Contains("javascript", StringComparison.OrdinalIgnoreCase)
        || type.Contains("css", StringComparison.OrdinalIgnoreCase) || type.Contains("json", StringComparison.OrdinalIgnoreCase);

    /// <summary>The site's absolute URLs of itself -- plain, JSON-escaped and protocol-relative -- made to
    /// come back through here; its relative ones already do, its paths being its own.</summary>
    private static string RewriteText(string text, Uri site)
    {
        string origin = site.GetLeftPart(UriPartial.Authority), via = ProxyOrigin(site);
        string viaHost = via.Substring("http:".Length);
        return text.Replace(origin, via, StringComparison.OrdinalIgnoreCase)
                   .Replace(origin.Replace("/", "\\/"), via.Replace("/", "\\/"), StringComparison.OrdinalIgnoreCase)
                   .Replace("\"//" + site.Authority, "\"" + viaHost, StringComparison.OrdinalIgnoreCase)
                   .Replace("'//" + site.Authority, "'" + viaHost, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>A cookie of the site's for its proxy host: without its Domain, which names the site, and
    /// which the browser would refuse there. (Secure and SameSite=None stay: a *.localhost page is a
    /// secure context.)</summary>
    private static string RewriteCookie(string cookie) =>
        string.Join("; ", cookie.Split(';').Select(p => p.Trim())
            .Where(p => !p.StartsWith("Domain=", StringComparison.OrdinalIgnoreCase)));
}
