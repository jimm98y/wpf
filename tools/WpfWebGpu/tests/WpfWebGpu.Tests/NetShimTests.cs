// The browser's System.Net.Requests and System.Net.WebClient (shims/), which this test process runs in place
// of the framework's, against a local HttpListener. What each test expects is what .NET's own assemblies
// answer to the same calls on macOS (a probe of both, side by side, agreed on every one of these).

#pragma warning disable SYSLIB0014
using System.Collections.Specialized;
using System.ComponentModel;
using System.Net;
using System.Text;
using Xunit;

namespace WpfWebGpu.Tests;

public sealed class NetShimServer : IDisposable
{
    private readonly HttpListener _listener = new();
    public readonly int Port;

    public NetShimServer()
    {
        for (Port = 8810; ; Port++)
        {
            try
            {
                _listener = new HttpListener();
                _listener.Prefixes.Add($"http://127.0.0.1:{Port}/");
                _listener.Start();
                break;
            }
            catch (HttpListenerException) when (Port < 8900) { }
        }
        Task.Run(async () =>
        {
            while (_listener.IsListening)
            {
                HttpListenerContext c;
                try { c = await _listener.GetContextAsync(); }
                catch { return; }
                _ = Task.Run(() => Handle(c));
            }
        });
    }

    public string U(string path) => $"http://127.0.0.1:{Port}{path}";

    private static void Handle(HttpListenerContext c)
    {
        HttpListenerRequest q = c.Request;
        HttpListenerResponse r = c.Response;
        void Text(string s) { r.ContentType = "text/plain"; byte[] b = Encoding.UTF8.GetBytes(s); r.OutputStream.Write(b); r.Close(); }
        switch (q.Url!.AbsolutePath)
        {
            case "/latin1": r.ContentType = "text/plain; charset=iso-8859-1"; r.OutputStream.Write(new byte[] { 0x63, 0x61, 0x66, 0xE9 }); r.Close(); return;
            case "/404": r.StatusCode = 404; Text("missing"); return;
            case "/redir": r.StatusCode = 302; r.RedirectLocation = "/echo?from=redir"; r.Close(); return;
            case "/slow": Thread.Sleep(1500); Text("slow"); return;
            case "/setcookie": r.AppendHeader("Set-Cookie", "k=v; Path=/"); Text("set"); return;
            case "/auth":
                if (q.Headers["Authorization"] == null) { r.StatusCode = 401; r.AppendHeader("WWW-Authenticate", "Basic realm=\"x\""); r.Close(); return; }
                Text("auth " + q.Headers["Authorization"]);
                return;
        }
        string body = new StreamReader(q.InputStream, Encoding.UTF8).ReadToEnd();
        Text($"{q.HttpMethod}|ct={q.ContentType}|body={body}|ua={q.UserAgent}|x={q.Headers["X-Custom"]}|auth={q.Headers["Authorization"]}|range={q.Headers["Range"]}|cookie={q.Headers["Cookie"]}|query={q.Url.Query}|ref={q.Headers["Referer"]}");
    }

    public void Dispose() => _listener.Stop();
}

public sealed class NetShimTests : IClassFixture<NetShimServer>
{
    private readonly NetShimServer _s;

    public NetShimTests(NetShimServer server) => _s = server;

    private static string Field(string echo, string name) => echo.Split('|').First(f => f.StartsWith(name + "=", StringComparison.Ordinal))[(name.Length + 1)..];

    [Fact]
    public void TheShimsAreTheOnesRunning()
    {
        Assert.Equal(new Version(10, 0, 9999, 0), typeof(WebClient).Assembly.GetName().Version);
        Assert.Equal(new Version(10, 0, 9999, 0), typeof(WebRequest).Assembly.GetName().Version);
    }

    [Fact]
    public void DownloadString_DecodesWithTheClientsEncoding_AndKeepsTheResponseHeaders()
    {
        var wc = new WebClient();
        Assert.Equal("caf�", wc.DownloadString(_s.U("/latin1")));              // the response's charset is not read
        Assert.Equal("text/plain; charset=iso-8859-1", wc.ResponseHeaders!["Content-Type"]);
    }

    [Fact]
    public void AnErrorStatus_IsAWebException_CarryingTheResponse()
    {
        var e = Assert.Throws<WebException>(() => new WebClient().DownloadString(_s.U("/404")));
        Assert.Equal("The remote server returned an error: (404) Not Found.", e.Message);
        Assert.Equal(WebExceptionStatus.ProtocolError, e.Status);
        var response = Assert.IsType<HttpWebResponse>(e.Response);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("missing", new StreamReader(response.GetResponseStream()).ReadToEnd());
    }

    [Fact]
    public void Uploads_AreSentAsDotNetSendsThem()
    {
        var wc = new WebClient();
        Assert.Equal("POST|ct=|body=hello", string.Join('|', wc.UploadString(_s.U("/echo"), "hello").Split('|').Take(3)));
        Assert.StartsWith("PUT|", wc.UploadString(_s.U("/echo"), "PUT", "x"));
        string values = Encoding.UTF8.GetString(wc.UploadValues(_s.U("/echo"), new NameValueCollection { { "a", "1 2" }, { "b", "x&y" } }));
        Assert.Equal("application/x-www-form-urlencoded", Field(values, "ct"));
        Assert.Equal("a=1+2&b=x%26y", Field(values, "body"));
        Assert.Null(wc.Headers["Content-Type"]);                                      // the request's, not left on the client
        var typed = new WebClient();
        typed.Headers["Content-Type"] = "text/plain";
        Assert.Equal("The Content-Type header cannot be changed from its default value for this request.",
            Assert.Throws<WebException>(() => typed.UploadValues(_s.U("/echo"), new NameValueCollection { { "a", "1" } })).Message);
    }

    [Fact]
    public void QueryString_IsAddedOnlyToAnAddressWithoutAQuery()
    {
        var wc = new WebClient();
        wc.QueryString.Add("q", "1");
        Assert.Equal("?q=1", Field(wc.DownloadString(_s.U("/echo")), "query"));
        Assert.Equal("?old=1", Field(wc.DownloadString(_s.U("/echo?old=1")), "query"));
    }

    [Fact]
    public void Paths_AreFileRequests_ReadAsWindowsReadsThem()
    {
        string dir = TestEnvironment.NewDirectory("webclient");
        File.WriteAllText(Path.Combine(dir, "Hello.txt"), "file text");
        var wc = new WebClient();
        Assert.Equal("file text", wc.DownloadString(Path.Combine(dir, "Hello.txt")));
        Assert.Equal("file text", new StreamReader(wc.OpenRead(dir.Replace('/', '\\') + "\\hello.TXT")).ReadToEnd());
        Assert.Equal("9", wc.ResponseHeaders!["Content-Length"]);
        Assert.Equal("application/octet-stream", wc.ResponseHeaders["Content-Type"]);
        var e = Assert.Throws<WebException>(() => wc.DownloadString(Path.Combine(dir, "nope.txt")));
        Assert.IsType<WebException>(e.InnerException);
        wc.UploadString(new Uri(Path.Combine(dir, "up.txt")).AbsoluteUri, "uploaded");
        Assert.Equal("uploaded", File.ReadAllText(Path.Combine(dir, "up.txt")));
    }

    [Fact]
    public void AsyncMembers_RaiseTheirEvents_WithProgressAndTheToken()
    {
        var wc = new WebClient();
        var done = new ManualResetEventSlim();
        string? got = null;
        int progress = 0;
        wc.DownloadProgressChanged += (_, e) => Interlocked.Increment(ref progress);
        wc.DownloadStringCompleted += (_, e) => { got = $"{e.Result.Split('|')[0]} {e.UserState} {e.Cancelled} {wc.IsBusy}"; done.Set(); };
        wc.DownloadStringAsync(new Uri(_s.U("/echo")), "tok");
        Assert.True(wc.IsBusy);
        Assert.True(done.Wait(5000));
        Assert.Equal("GET tok False False", got);
        Assert.True(progress > 0);
    }

    [Fact]
    public void TaskMembers_CompleteFromTheEvent_WhichOthersSeeToo()
    {
        var wc = new WebClient();
        int seen = 0;
        wc.DownloadStringCompleted += (_, _) => Interlocked.Increment(ref seen);
        Assert.StartsWith("GET|", wc.DownloadStringTaskAsync(_s.U("/echo")).Result);
        SpinWait.SpinUntil(() => seen == 1, 2000);
        Assert.Equal(1, seen);
        var e = Assert.Throws<AggregateException>(() => new WebClient().DownloadDataTaskAsync(_s.U("/404")).Wait());
        Assert.Equal(WebExceptionStatus.ProtocolError, Assert.IsType<WebException>(e.InnerException).Status);
    }

    [Fact]
    public void OneOperationAtATime_AndCancelAsyncCancels()
    {
        var wc = new WebClient();
        var done = new ManualResetEventSlim();
        AsyncCompletedEventArgs? args = null;
        wc.DownloadStringCompleted += (_, e) => { args = e; done.Set(); };
        wc.DownloadStringAsync(new Uri(_s.U("/slow")));
        Assert.Equal("WebClient does not support concurrent I/O operations.", Assert.Throws<NotSupportedException>(() => wc.DownloadString(_s.U("/echo"))).Message);
        wc.CancelAsync();
        Assert.True(done.Wait(5000));
        Assert.True(args!.Cancelled);
        Assert.Equal(WebExceptionStatus.RequestCanceled, Assert.IsType<WebException>(args.Error).Status);
    }

    private sealed class PreAuthClient : WebClient
    {
        public readonly List<string> Seen = new();

        protected override WebRequest GetWebRequest(Uri address)
        {
            WebRequest r = base.GetWebRequest(address);
            Seen.Add(r.GetType().Name + " " + r.Method);
            r.PreAuthenticate = true;
            r.Credentials = new NetworkCredential("u", "p");
            if (r is HttpWebRequest h) h.Referer = "http://ref/";
            return r;
        }

        protected override WebResponse GetWebResponse(WebRequest request)
        {
            Seen.Add("response");
            return base.GetWebResponse(request);
        }
    }

    [Fact]
    public void ASubclass_SeesAndChangesEachRequest_AndBasicCredentialsAnswerAChallenge()
    {
        var wc = new PreAuthClient();
        string echo = wc.DownloadString(_s.U("/echo"));
        Assert.Equal("", Field(echo, "auth"));                                       // nothing up front before a challenge
        Assert.Equal("http://ref/", Field(echo, "ref"));
        Assert.Equal(new[] { "HttpWebRequest GET", "response" }, wc.Seen);
        Assert.Equal("auth Basic dTpw", wc.DownloadString(_s.U("/auth")));
        Assert.Equal(HttpStatusCode.Unauthorized,
            ((HttpWebResponse)Assert.Throws<WebException>(() => new WebClient().DownloadString(_s.U("/auth"))).Response!).StatusCode);
    }

    [Fact]
    public void HttpWebRequest_PostsItsBody_FollowsRedirects_AndTimesOut()
    {
        var post = (HttpWebRequest)WebRequest.Create(_s.U("/echo"));
        post.Method = "POST";
        post.ContentType = "application/json";
        using (Stream s = post.GetRequestStream()) s.Write("{\"a\":1}"u8);
        using (var response = (HttpWebResponse)post.GetResponse())
        {
            string echo = new StreamReader(response.GetResponseStream()).ReadToEnd();
            Assert.Equal("application/json", Field(echo, "ct"));
            Assert.Equal("{\"a\":1}", Field(echo, "body"));
        }
        Assert.Same(post.GetResponse(), post.GetResponse());

        var redirected = WebRequest.Create(_s.U("/redir"));
        using (WebResponse r = redirected.GetResponse()) Assert.Equal("/echo?from=redir", r.ResponseUri.PathAndQuery);
        var manual = (HttpWebRequest)WebRequest.Create(_s.U("/redir"));
        manual.AllowAutoRedirect = false;
        using (var r = (HttpWebResponse)manual.GetResponse()) Assert.Equal(HttpStatusCode.Found, r.StatusCode);

        var slow = WebRequest.Create(_s.U("/slow"));
        slow.Timeout = 300;
        var e = Assert.Throws<WebException>(() => slow.GetResponse());
        Assert.Equal(("The operation has timed out.", WebExceptionStatus.Timeout), (e.Message, e.Status));
    }

    [Fact]
    public void HttpWebRequest_RangesAndCookies()
    {
        var ranged = (HttpWebRequest)WebRequest.Create(_s.U("/echo"));
        ranged.AddRange(5, 10);
        ranged.AddRange(20, 30);
        using (WebResponse r = ranged.GetResponse()) Assert.Equal("bytes=5-10,20-30", Field(new StreamReader(r.GetResponseStream()).ReadToEnd(), "range"));

        var jar = new CookieContainer();
        var set = (HttpWebRequest)WebRequest.Create(_s.U("/setcookie"));
        set.CookieContainer = jar;
        set.GetResponse().Close();
        var send = (HttpWebRequest)WebRequest.Create(_s.U("/echo"));
        send.CookieContainer = jar;
        using (var r = (HttpWebResponse)send.GetResponse())
        {
            Assert.Equal("k=v", Field(new StreamReader(r.GetResponseStream()).ReadToEnd(), "cookie"));
            Assert.Single(r.Cookies);
        }
        using (var r = (HttpWebResponse)WebRequest.Create(_s.U("/setcookie")).GetResponse()) Assert.Empty(r.Cookies);   // no container, no cookies
    }

    [Fact]
    public void HttpWebRequest_AfterTheResponse_RefusesWhatDotNetRefuses()
    {
        var r = (HttpWebRequest)WebRequest.Create(_s.U("/echo"));
        r.GetResponse().Close();
        r.UserAgent = "late";                                                          // header properties: taken
        r.Timeout = 5;
        r.Method = "PUT";
        Assert.Equal("This property cannot be set after writing has started.", Assert.Throws<InvalidOperationException>(() => r.ContentLength = 5).Message);
        Assert.Equal("This property cannot be set after writing has started.", Assert.Throws<InvalidOperationException>(() => r.Host = "h").Message);
        Assert.Equal("This operation cannot be performed after the request has been submitted.", Assert.Throws<InvalidOperationException>(() => r.Headers = new WebHeaderCollection()).Message);
        Assert.Equal("This operation cannot be performed after the request has been submitted.", Assert.Throws<InvalidOperationException>(() => r.GetRequestStream()).Message);

        var closed = (HttpWebResponse)WebRequest.Create(_s.U("/echo")).GetResponse();
        closed.Close();
        Assert.Throws<ObjectDisposedException>(() => closed.StatusCode);

        Assert.Equal("Cannot send a content-body with this verb-type.", Assert.Throws<ProtocolViolationException>(() => WebRequest.Create(_s.U("/echo")).GetRequestStream()).Message);
        Assert.Equal("The URI prefix is not recognized.", Assert.Throws<NotSupportedException>(() => WebRequest.Create("foo://x/")).Message);
        Assert.IsType<FileWebRequest>(WebRequest.Create("file:///tmp/x"));
    }
}
