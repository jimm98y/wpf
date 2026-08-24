// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// Page: the frame tree the frontend insists on, and screencasting.
//
// The frame tree is not optional decoration. A DevTools frontend asks for it
// while attaching and a page with no frame is a page it will not populate
// panels for, so this domain answers with a single synthetic frame standing for
// the application.
//
// Screencasting turns the endpoint into a remote view of the window. Frames come
// from RenderTargetBitmap over the root visual -- the same path the gallery's
// WPF_GALLERY_RTB smoke test already proves works on the macOS head -- so this
// needs nothing from the renderer and behaves the same on every head.
//
// Two things keep it from swamping the connection: it will not send a frame
// while one is unacknowledged (the frontend acks each one it draws), and it will
// not send faster than MinFrameInterval regardless. Rendering the tree to a
// bitmap is not free, and this is a diagnostic; it must not become the reason
// the app is slow.
//

using System;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Wpf.DevTools.Json;

namespace Microsoft.Wpf.DevTools.Domains
{
    internal sealed class PageDomain : ICdpDomain, IDisposable
    {
        /// <summary>The one synthetic frame this target reports. DOM stamps it on the document.</summary>
        internal const string FrameId = "wpf-frame";

        /// <summary>Roughly 10fps. A visual tree inspector does not need 60.</summary>
        private static readonly TimeSpan MinFrameInterval = TimeSpan.FromMilliseconds(100);

        private readonly CdpSession _session;

        private bool _screencasting;
        private bool _awaitingAck;
        private DateTime _lastFrameUtc;
        private string _format = "png";
        private int _quality = 80;
        private int _maxWidth = 1600;
        private int _maxHeight = 1200;
        private int _frameSessionId;

        internal PageDomain(CdpSession session)
        {
            _session = session;
        }

        public bool TryHandle(string method, JsonElement p, Utf8JsonWriter w)
        {
            switch (method)
            {
                case "Page.enable":
                case "Page.disable":
                    if (method == "Page.disable")
                        StopScreencast();
                    return true;

                case "Page.getFrameTree":
                    w.WriteStartObject("frameTree");
                    WriteFrame(w);
                    w.WriteStartArray("childFrames");
                    w.WriteEndArray();
                    w.WriteEndObject();
                    return true;

                case "Page.getResourceTree":
                    w.WriteStartObject("frameTree");
                    WriteFrame(w);
                    w.WriteStartArray("resources");
                    w.WriteEndArray();
                    w.WriteStartArray("childFrames");
                    w.WriteEndArray();
                    w.WriteEndObject();
                    return true;

                case "Page.getNavigationHistory":
                    w.WriteNumber("currentIndex", 0);
                    w.WriteStartArray("entries");
                    w.WriteStartObject();
                    w.WriteNumber("id", 1);
                    w.WriteString("url", "wpf://app/");
                    w.WriteString("userTypedURL", "wpf://app/");
                    w.WriteString("title", "WPF");
                    w.WriteString("transitionType", "typed");
                    w.WriteEndObject();
                    w.WriteEndArray();
                    return true;

                case "Page.getLayoutMetrics":
                    WriteLayoutMetrics(w);
                    return true;

                case "Page.startScreencast":
                    StartScreencast(p);
                    return true;

                case "Page.stopScreencast":
                    StopScreencast();
                    return true;

                case "Page.screencastFrameAck":
                    _awaitingAck = false;
                    return true;

                case "Page.setLifecycleEventsEnabled":
                case "Page.setAdBlockingEnabled":
                    return true;

                default:
                    return false;
            }
        }

        public void Dispose() => StopScreencast();

        private static void WriteFrame(Utf8JsonWriter w)
        {
            w.WriteStartObject("frame");
            w.WriteString("id", FrameId);
            w.WriteString("loaderId", FrameId);
            w.WriteString("url", "wpf://app/");
            w.WriteString("domainAndRegistry", string.Empty);
            w.WriteString("securityOrigin", "wpf://app");
            w.WriteString("mimeType", "text/html");
            w.WriteString("secureContextType", "Secure");
            w.WriteString("crossOriginIsolatedContextType", "NotIsolated");
            w.WriteStartArray("gatedAPIFeatures");
            w.WriteEndArray();
            w.WriteEndObject();
        }

        private static void WriteLayoutMetrics(Utf8JsonWriter w)
        {
            Rect bounds = RootBounds();

            w.WriteStartObject("layoutViewport");
            w.WriteNumber("pageX", 0);
            w.WriteNumber("pageY", 0);
            w.WriteNumber("clientWidth", (int)bounds.Width);
            w.WriteNumber("clientHeight", (int)bounds.Height);
            w.WriteEndObject();

            w.WriteStartObject("visualViewport");
            w.WriteNumber("offsetX", 0);
            w.WriteNumber("offsetY", 0);
            w.WriteNumber("pageX", 0);
            w.WriteNumber("pageY", 0);
            w.WriteNumber("clientWidth", (int)bounds.Width);
            w.WriteNumber("clientHeight", (int)bounds.Height);
            w.WriteNumber("scale", 1);
            w.WriteEndObject();

            w.WriteStartObject("contentSize");
            w.WriteNumber("x", 0);
            w.WriteNumber("y", 0);
            w.WriteNumber("width", (int)bounds.Width);
            w.WriteNumber("height", (int)bounds.Height);
            w.WriteEndObject();
        }

        private static Rect RootBounds()
        {
            foreach (Visual root in VisualTreeModel.VisualRoots())
            {
                if (VisualTreeModel.TryGetBounds(root, out Rect bounds) && bounds.Width >= 1 && bounds.Height >= 1)
                    return bounds;
            }
            return new Rect(0, 0, 1, 1);
        }

        // ------------------------------------------------------------------
        // Screencast
        // ------------------------------------------------------------------

        private void StartScreencast(JsonElement p)
        {
            _format = CdpJson.GetString(p, "format") ?? "png";
            _quality = Math.Clamp(CdpJson.GetInt(p, "quality", 80), 1, 100);
            _maxWidth = Math.Max(1, CdpJson.GetInt(p, "maxWidth", 1600));
            _maxHeight = Math.Max(1, CdpJson.GetInt(p, "maxHeight", 1200));
            _awaitingAck = false;

            if (_screencasting)
                return;

            _screencasting = true;
            CompositionTarget.Rendering += OnRendering;
        }

        private void StopScreencast()
        {
            if (!_screencasting)
                return;

            _screencasting = false;
            CompositionTarget.Rendering -= OnRendering;
        }

        private void OnRendering(object? sender, EventArgs e)
        {
            if (!_screencasting || _awaitingAck)
                return;

            DateTime now = DateTime.UtcNow;
            if (now - _lastFrameUtc < MinFrameInterval)
                return;

            _lastFrameUtc = now;

            try
            {
                if (!TryCapture(out byte[] data, out double pageWidth, out double pageHeight))
                    return;

                _awaitingAck = true;
                int sessionId = ++_frameSessionId;

                _session.SendEvent("Page.screencastFrame", ev =>
                {
                    ev.WriteBase64String("data", data);
                    ev.WriteStartObject("metadata");
                    ev.WriteNumber("offsetTop", 0);
                    ev.WriteNumber("pageScaleFactor", 1);
                    // The PAGE's size, not the image's.
                    //
                    // The frame is scaled down to fit the panel, but the frontend uses these
                    // two numbers to map a click on the screencast back into page coordinates.
                    // Reporting the scaled image size here makes every click land short by
                    // exactly the scale factor -- high and to the left -- while the picture
                    // itself still looks perfectly correct.
                    ev.WriteNumber("deviceWidth", pageWidth);
                    ev.WriteNumber("deviceHeight", pageHeight);
                    ev.WriteNumber("scrollOffsetX", 0);
                    ev.WriteNumber("scrollOffsetY", 0);
                    ev.WriteNumber("timestamp",
                        (now - DateTime.UnixEpoch).TotalSeconds);
                    ev.WriteEndObject();
                    ev.WriteNumber("sessionId", sessionId);
                });
            }
            catch (Exception ex)
            {
                // One bad frame is not a reason to stop casting, but a stream of them
                // would be, so say so and let the next tick try again.
                DevToolsServer.Log($"screencast frame failed: {ex.GetType().Name}: {ex.Message}");
                _awaitingAck = false;
            }
        }

        /// <summary>
        /// Render the root visual to an encoded image, reporting the PAGE size in
        /// device-independent pixels. The image may be smaller -- it is scaled to fit the
        /// panel -- and the two must not be confused; see where the metadata is written.
        /// </summary>
        /// <summary>
        /// Composite the frame onto opaque white.
        ///
        /// A WPF window does not paint its whole RenderSize: WindowChrome's non-client area,
        /// the resize border and glass frame, is left transparent. On the WPF Gallery that is
        /// a band roughly 8 device-independent pixels down the right edge and 33 along the
        /// bottom. The frontend composites a screencast frame onto black, so those bands
        /// arrive looking like a black border painted around the picture.
        ///
        /// Done by hand rather than by rendering a backdrop visual underneath, because
        /// RenderTargetBitmap.Render does not accumulate here: a second Render replaces the
        /// target rather than drawing over it, so the backdrop simply vanished. Measured, not
        /// assumed -- the transparent bands were still there afterwards.
        ///
        /// The source is PREMULTIPLIED (Pbgra32), so compositing over white is
        /// src + (1-alpha) per channel, with no division to get wrong.
        ///
        /// Flattening rather than cropping keeps the image's extent equal to the page's,
        /// which is what the click mapping depends on.
        /// </summary>
        /// <summary>
        /// Device pixels per device-independent pixel for this visual's window, so a
        /// composed frame's pixel extent can be reported as the page size the frontend maps
        /// clicks through.
        /// </summary>
        private static double DeviceScale(Visual root)
        {
            try
            {
                PresentationSource? source = PresentationSource.FromVisual(root);
                double scale = source?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;
                return scale > 0 ? scale : 1.0;
            }
            catch
            {
                return 1.0;
            }
        }

        /// <summary>
        /// Wrap straight RGBA as a bitmap. The renderer hands back R,G,B,A in that order;
        /// WPF's 32-bit formats are B,G,R,A, so the two outer channels swap.
        /// </summary>
        private static BitmapSource FromRgba(byte[] rgba, int width, int height)
        {
            int stride = width * 4;
            var pixels = new byte[stride * height];

            for (int i = 0; i < pixels.Length; i += 4)
            {
                pixels[i] = rgba[i + 2];
                pixels[i + 1] = rgba[i + 1];
                pixels[i + 2] = rgba[i];
                pixels[i + 3] = 255;      // the composed frame is opaque by construction
            }

            return BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, stride);
        }

        private static BitmapSource Flatten(RenderTargetBitmap bitmap, int width, int height)
        {
            int stride = width * 4;
            var pixels = new byte[stride * height];
            bitmap.CopyPixels(pixels, stride, 0);

            for (int i = 0; i < pixels.Length; i += 4)
            {
                int transparency = 255 - pixels[i + 3];
                if (transparency == 0)
                    continue;

                pixels[i] = (byte)Math.Min(255, pixels[i] + transparency);
                pixels[i + 1] = (byte)Math.Min(255, pixels[i + 1] + transparency);
                pixels[i + 2] = (byte)Math.Min(255, pixels[i + 2] + transparency);
                pixels[i + 3] = 255;
            }

            return BitmapSource.Create(width, height, 96, 96, PixelFormats.Pbgra32, null, pixels, stride);
        }

        private bool TryCapture(out byte[] data, out double pageWidth, out double pageHeight)
        {
            data = Array.Empty<byte>();
            pageWidth = pageHeight = 0;

            Visual? root = null;
            foreach (Visual candidate in VisualTreeModel.VisualRoots())
            {
                root = candidate;
                break;
            }

            if (root == null || !VisualTreeModel.TryGetBounds(root, out Rect bounds))
                return false;

            if (bounds.Width < 1 || bounds.Height < 1)
                return false;

            // Scale down rather than crop, so the frontend sees the whole window.
            pageWidth = bounds.Width;
            pageHeight = bounds.Height;

            double scale = Math.Min(1.0, Math.Min(_maxWidth / bounds.Width, _maxHeight / bounds.Height));
            int width = Math.Max(1, (int)Math.Round(bounds.Width * scale));
            int height = Math.Max(1, (int)Math.Round(bounds.Height * scale));

            // The browser has no synchronous GPU readback, so BOTH paths below come back empty
            // there -- the composed frame as null, and RenderTargetBitmap as a correctly sized
            // sheet of white. Its canvas already holds the composed image, so take it from there.
            //
            // pageWidth/pageHeight stay the ROOT's, deliberately: the canvas is in device pixels
            // and the frontend maps clicks through these two numbers, which have to stay in the
            // same units the hit test works in.
            if (OperatingSystem.IsBrowser() &&
                BrowserTransport.TryCaptureCanvas(_maxWidth, _maxHeight, _format, _quality, out byte[] canvasPng))
            {
                data = canvasPng;
                return true;
            }

            // The renderer's own composed frame first: it is what is actually on screen,
            // including any hosted (WindowsFormsHost) scene. RenderTargetBitmap re-renders the
            // WPF VISUAL TREE, and hosted content is not in it -- a WinForms card simply does
            // not appear, with nothing to say why.
            BitmapSource frame;
            byte[]? composed = CompositionModel.CaptureComposedFrame(out int pixelWidth, out int pixelHeight);
            if (composed != null && composed.Length >= pixelWidth * pixelHeight * 4)
            {
                frame = FromRgba(composed, pixelWidth, pixelHeight);

                // The composed frame covers the CLIENT area, which is not the root visual's
                // box: the root's RenderSize includes WindowChrome's non-client band (measured
                // 3850x1087 against a 3840x1049 surface). Same origin, smaller extent.
                //
                // So the page size reported has to be the FRAME's extent, not the root's.
                // Leaving it as the root's would put the frontend's click mapping out by the
                // difference -- about 3.5% vertically here, and silently.
                double deviceScale = DeviceScale(root);
                pageWidth = pixelWidth / deviceScale;
                pageHeight = pixelHeight / deviceScale;

                double fit = Math.Min(1.0, Math.Min((double)_maxWidth / pixelWidth, (double)_maxHeight / pixelHeight));
                if (fit < 1.0)
                    frame = new TransformedBitmap(frame, new ScaleTransform(fit, fit));
            }
            else
            {
                var bitmap = new RenderTargetBitmap(width, height, 96 * scale, 96 * scale, PixelFormats.Pbgra32);
                bitmap.Render(root);
                frame = Flatten(bitmap, width, height);
            }

            using var stream = new MemoryStream();
            Encoder(frame).Save(stream);
            data = stream.ToArray();
            return data.Length > 0;

            BitmapEncoder Encoder(BitmapSource frame)
            {
                BitmapEncoder encoder;
                try
                {
                    // JPEG is what a frontend asks for by default. PNG is the one this
                    // stack has always been able to write, so it is the fallback rather
                    // than a reason to send nothing.
                    encoder = string.Equals(_format, "jpeg", StringComparison.OrdinalIgnoreCase)
                        ? new JpegBitmapEncoder { QualityLevel = _quality }
                        : new PngBitmapEncoder();
                }
                catch
                {
                    encoder = new PngBitmapEncoder();
                }

                encoder.Frames.Add(BitmapFrame.Create(frame));
                return encoder;
            }
        }
    }
}
