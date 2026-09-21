// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// Everything a control needs to own an engine, behind one type that speaks only in primitives.
//
// The two control assemblies used to create the backend and the host window themselves, which meant
// naming IWebViewBackend and WebViewHostWindow. That works for the WPF control and DOES NOT work for
// the WinForms one: the seam is link-compiled into System.Windows.Forms as well (its own WebBrowser
// needs it), and that assembly grants this one friend access, so both copies of IWebViewBackend are
// visible at once and every mention of it is CS0433-ambiguous.
//
// Consolidating here fixes that by construction -- the controls exchange an IntPtr, a Task and a
// CoreWebView2, none of which is duplicated -- and it is the better shape anyway: host-window and
// engine lifetime are one thing, they are created together and must be torn down together, and both
// controls were otherwise going to spell that out identically.
//

using System;
using System.Threading.Tasks;
using MS.Internal.Interop.WebView;

namespace Microsoft.Web.WebView2.Core
{
    /// <summary>An engine and the native window it fills, owned together.</summary>
    internal sealed class CoreWebView2Host : IDisposable
    {
        private IWebViewBackend _backend;
        private IntPtr _hostWindow;

        private CoreWebView2Host(IWebViewBackend backend, IntPtr hostWindow)
        {
            _backend = backend;
            _hostWindow = hostWindow;
            CoreWebView2 = new CoreWebView2(backend);
            Ready = backend.AttachAsync(hostWindow);
        }

        /// <summary>
        /// Create an engine parented to <paramref name="parentWindow"/>, or null where this platform
        /// has no engine or no way to host one. A null result is the caller's cue to report that
        /// plainly -- never to show an empty rectangle.
        /// </summary>
        internal static CoreWebView2Host Create(IntPtr parentWindow, int width, int height)
        {
            IWebViewBackend backend = WebViewBackendFactory.Create();

            if (backend is null)
            {
                return null;
            }

            IntPtr host = WebViewHostWindow.Create(parentWindow, width, height);

            if (host == IntPtr.Zero)
            {
                backend.Dispose();
                return null;
            }

            return new CoreWebView2Host(backend, host);
        }

        /// <summary>The engine's object model. Valid immediately; usable once <see cref="Ready"/> completes.</summary>
        internal CoreWebView2 CoreWebView2 { get; }

        /// <summary>Completes when the engine can be navigated.</summary>
        internal Task Ready { get; }

        internal bool IsAttached => _backend is not null && _backend.IsAttached;

        internal IntPtr HostWindow => _hostWindow;

        /// <summary>Move the host window within its parent, in device pixels.</summary>
        internal void Move(int x, int y, int width, int height)
        {
            WebViewHostWindow.Move(_hostWindow, x, y, width, height);
            _backend?.SetBounds(0, 0, Math.Max(1, width), Math.Max(1, height), 1.0);
        }

        /// <summary>Size the engine inside a host window someone else positions (the WPF path).</summary>
        internal void SetBounds(int x, int y, int width, int height, double scale) =>
            _backend?.SetBounds(x, y, width, height, scale);

        internal void SetVisible(bool visible) => _backend?.SetVisible(visible);

        internal double ZoomFactor
        {
            get => _backend?.ZoomFactor ?? 1.0;
            set { if (_backend is not null) { _backend.ZoomFactor = value; } }
        }

        public void Dispose()
        {
            _backend?.Dispose();
            _backend = null;

            if (_hostWindow != IntPtr.Zero)
            {
                WebViewHostWindow.Destroy(_hostWindow);
                _hostWindow = IntPtr.Zero;
            }
        }
    }
}
