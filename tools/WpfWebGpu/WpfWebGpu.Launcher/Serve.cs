// wpfrun serve: a page of the browser head, served as it needs to be. A page whose app uses threads runs
// on .NET's multithreaded WebAssembly runtime (wpfrun bundle builds one), which needs SharedArrayBuffer,
// which a browser gives only to a cross-origin isolated page: one served with COOP and COEP headers. A
// plain static server (python -m http.server) sends neither, and the page never starts. This serves a
// folder on the loopback with both, the content types the runtime checks (.wasm, .mjs), and no caching
// (a rebuilt bundle is picked up by a reload).

using System.Net;

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
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
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
        catch (Exception e) when (e is IOException or HttpListenerException or UnauthorizedAccessException)
        {
            try { res.StatusCode = 500; } catch (InvalidOperationException) { }
        }
        finally
        {
            try { res.Close(); } catch (Exception) { }
        }
    }
}
