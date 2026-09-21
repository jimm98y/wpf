// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// The entry point PresentationCore's DevToolsBootstrap reflects onto.
//
// The signature of Start(int) is a CONTRACT with that bootstrap: it is bound by
// name through reflection, so renaming either half silently turns the inspector
// off rather than failing the build. There is a test that pins it.
//
// Start is called once per HwndSource, because that is the one place every head
// funnels through when a window appears; everything past the first call is a
// no-op.
//

using System;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;

namespace Microsoft.Wpf.DevTools
{
    /// <summary>
    /// Hosts the inspector inside a running WPF application. Loaded reflectively;
    /// see DevToolsBootstrap in PresentationCore.
    /// </summary>
    public static class DevToolsServer
    {
        private const string DumpVariable = "WPF_DEVTOOLS_DUMP";
        private const string DumpPropsVariable = "WPF_DEVTOOLS_DUMP_PROPS";
        private const string DumpDelayVariable = "WPF_DEVTOOLS_DUMP_DELAY";

        private const int DefaultDumpDelayMs = 1500;
        private const int DefaultPort = 9222;

        private static int s_started;
        private static VisualTreeModel? s_model;
        private static Dispatcher? s_dispatcher;
        private static WebSocketTransport? s_transport;
        private static UiMarshaller? s_ui;
        private static Timer? s_dumpTimer;

        /// <summary>
        /// The port the endpoint is listening on, or 0 if it is not. Differs from the
        /// port passed to Start only when that was 0, which asks the OS to pick one --
        /// how a test gets an endpoint without racing another test for a fixed port.
        /// </summary>
        public static int ListeningPort => s_transport?.Port ?? 0;

        /// <summary>
        /// Read WPF_DEVTOOLS and start accordingly. Both bootstraps -- PresentationCore's,
        /// triggered by HwndSource, and the WinForms one, triggered by the message loop --
        /// go through here so the two heads cannot drift on how the variable is spelled.
        /// </summary>
        /// <returns>True if the inspector started.</returns>
        public static bool StartFromEnvironment()
        {
            string? setting = Environment.GetEnvironmentVariable("WPF_DEVTOOLS");
            if (string.IsNullOrEmpty(setting) || IsOff(setting))
                return false;

            // WPF_DEVTOOLS=9333 names a port; anything else that got this far means "on,
            // default port". "1" is handled as an on-switch BEFORE it is handled as a
            // number: it parses as a perfectly valid port, so leaving it to TryParse would
            // turn the ordinary way of enabling a feature into a request to bind port 1.
            int port = DefaultPort;
            if (int.TryParse(setting, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed) &&
                parsed > 1 && parsed <= 65535)
            {
                port = parsed;
            }

            Start(port);
            return true;
        }

        private static bool IsOff(string value)
            => string.Equals(value, "0", StringComparison.Ordinal)
            || string.Equals(value, "false", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "off", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "no", StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// Start the inspector on the calling thread's dispatcher. Idempotent, and
        /// never throws: a diagnostic that takes down the app it is diagnosing is
        /// worse than no diagnostic.
        /// </summary>
        /// <param name="port">TCP port for the DevTools endpoint.</param>
        public static void Start(int port)
        {
            if (Interlocked.Exchange(ref s_started, 1) != 0)
                return;

            try
            {
                s_dispatcher = Dispatcher.CurrentDispatcher;
                s_model = new VisualTreeModel();
                s_ui = new UiMarshaller(s_dispatcher, TimeSpan.FromSeconds(5));

                ScheduleDumpIfRequested();
                StartTransport(port);
            }
            catch (Exception ex)
            {
                Log($"failed to start: {ex.GetType().Name}: {ex.Message}");
            }
        }

        /// <summary>
        /// Render the tree right now. Exposed for tests and for the protocol layer;
        /// must be called on the dispatcher thread.
        /// </summary>
        internal static string RenderTree(DumpProperties properties)
            => TreeDump.Render(s_model ??= new VisualTreeModel(), properties);

        private static void StartTransport(int port)
        {
            // The browser head has no sockets and cannot be dialled, so there it is a
            // JS message port rather than a listener. Everything else listens on
            // loopback. See BrowserTransport for why that difference is not papered over.
            if (OperatingSystem.IsBrowser())
            {
                StartBrowserTransport();
                return;
            }

            var transport = new WebSocketTransport(port, CurrentTitle);
            transport.ConnectionAccepted += Attach;

            try
            {
                transport.Start();
            }
            catch (Exception ex)
            {
                // A port already in use is the common one, and it usually means a second
                // instance of the app is running with the inspector on. Say so and carry
                // on without an inspector rather than taking the app down.
                Log($"could not listen on 127.0.0.1:{port}: {ex.GetType().Name}: {ex.Message}");
                transport.Dispose();
                return;
            }

            s_transport = transport;
            Log($"listening on http://127.0.0.1:{transport.Port} " +
                $"(chrome://inspect -> Configure -> add localhost:{transport.Port})");
        }

        private static void StartBrowserTransport()
        {
            try
            {
                var browser = new BrowserTransport();
                browser.ConnectionAccepted += Attach;
                browser.Start();
                Log("browser bridge ready: globalThis.__wpfDevTools.send(json)");
            }
            catch (Exception ex)
            {
                Log($"browser bridge failed to start: {ex.GetType().Name}: {ex.Message}");
            }
        }

        private static void Attach(ICdpConnection connection)
        {
            Log($"frontend attached to '{connection.TargetId}'");
            // The session hooks the connection's events in its constructor; the
            // connection's own read loop drives it from here.
            _ = new CdpSession(connection, s_dispatcher!, s_model!);
        }

        /// <summary>
        /// The title the target advertises. Called from a connection thread while
        /// serving /json/list, so it has to hop to the UI thread to read a Window.
        /// </summary>
        private static string CurrentTitle()
        {
            UiMarshaller? ui = s_ui;
            if (ui == null)
                return "WPF";

            try
            {
                return ui.Invoke(() =>
                {
                    foreach (Visual root in VisualTreeModel.VisualRoots())
                    {
                        if (root is Window { Title.Length: > 0 } window)
                            return window.Title;
                    }

                    // A WinForms-only app has no Window, but its Form has a Text.
                    foreach (object root in VisualTreeModel.Roots())
                    {
                        string? text = VisualTreeModel.NodeText(root);
                        if (!string.IsNullOrEmpty(text))
                            return text;
                    }

                    return "WPF";
                });
            }
            catch
            {
                // A busy or wedged UI thread must not stop target discovery -- the
                // title is cosmetic and the endpoint still works without it.
                return "WPF";
            }
        }

        private static void ScheduleDumpIfRequested()
        {
            string? path = Environment.GetEnvironmentVariable(DumpVariable);
            if (string.IsNullOrWhiteSpace(path))
                return;

            DumpProperties properties = TreeDump.ParseProperties(Environment.GetEnvironmentVariable(DumpPropsVariable));
            int delayMs = ParseDelay(Environment.GetEnvironmentVariable(DumpDelayVariable));

            // The tree is worth nothing before the first layout pass, and a window
            // that is still measuring reports empty bounds for everything. Waiting a
            // beat gives a dump that reflects what is on screen.
            //
            // A plain timer rather than a DispatcherTimer: on a WinForms head nothing
            // pumps the WPF dispatcher, so a DispatcherTimer would never tick. The
            // callback marshals itself.
            s_dumpTimer = new Timer(_ =>
            {
                s_dumpTimer?.Dispose();
                s_dumpTimer = null;

                try
                {
                    s_ui!.Invoke(() => WriteDump(path!, properties));
                }
                catch (Exception ex)
                {
                    Log($"dump to '{path}' could not reach the UI thread: {ex.GetType().Name}: {ex.Message}");
                }
            }, null, delayMs, System.Threading.Timeout.Infinite);
        }

        private static void WriteDump(string path, DumpProperties properties)
        {
            try
            {
                string text = RenderTree(properties);
                File.WriteAllText(path, text);
                Log($"wrote visual tree dump to {path} ({text.Length} chars)");
            }
            catch (Exception ex)
            {
                Log($"dump to '{path}' failed: {ex.GetType().Name}: {ex.Message}");
            }
        }

        private static int ParseDelay(string? value)
        {
            if (!string.IsNullOrWhiteSpace(value) &&
                int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int ms) &&
                ms >= 0)
            {
                return ms;
            }

            return DefaultDumpDelayMs;
        }

        /// <summary>
        /// stderr, not stdout: the heads already print frame and perf lines to
        /// stdout and the run scripts parse them.
        /// </summary>
        internal static void Log(string message)
        {
            try
            {
                Console.Error.WriteLine("[wpf-devtools] " + message);
            }
            catch
            {
            }
        }
    }
}
