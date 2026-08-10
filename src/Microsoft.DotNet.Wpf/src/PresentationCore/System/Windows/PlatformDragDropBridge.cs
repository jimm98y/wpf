// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// The WPF half of drag-and-drop on every head that is not Windows: turns a platform drag into WPF's
// DragEnter/DragOver/DragLeave/Drop routed events, and a drag offer into an IDataObject.
//
// The platform halves are MS.Internal.Interop.Wayland.WaylandDragDrop and
// MS.Internal.Interop.CocoaDragDrop, which live in WindowsBase and cannot see this assembly; the two
// sides meet at MS.Internal.Interop.PlatformDragDrop, into which this installs itself. See that file
// for why the dependency runs one way.
//
// One WPF-side implementation serves all of them because the backends agree on a vocabulary: MIME
// type strings. Wayland's are native, and the Cocoa backend translates its UTIs on the way through,
// which is a table -- against a second copy of every format decision, which is not.
//
// The hit-testing and event plumbing is NOT reimplemented here. OleDropTarget already does all of it
// -- resolving the element under the point, honouring AllowDrop, synthesising the DragLeave/DragEnter
// pair when the target changes mid-drag, and running the tunnel/bubble pass -- and despite the name it
// is ordinary managed code behind a plain interface. Only the transport is OLE, and that is what this
// file replaces. What it can NOT be given is a System.Windows.DataObject: that type's constructor
// registers in the COM Global Interface Table and P/Invokes OLE32.dll, so it cannot even be
// constructed off Windows. Hence DragDataObject below, and the IDataObject arm added to
// DragDrop.GetDataObject.
//

#nullable enable

using System.Collections.Generic;
using System.Runtime.Versioning;
using System.Text;
using System.Windows.Input;
using System.Windows.Interop;
using MS.Internal.Interop;
using MS.Win32;

namespace System.Windows
{
    /// <summary>
    ///  <see cref="IDataObject"/> over a platform drag offer. Formats are read lazily -- a drag may
    ///  advertise types nobody asks for, and each read is a pipe round trip to the source process.
    /// </summary>
    internal sealed class DragDataObject : IDataObject
    {
        // WPF format <-> MIME. Order matters within a format: the first type the drag actually offers
        // is the one requested.
        private static readonly (string Format, string[] Mimes)[] s_map =
        {
            (DataFormats.FileDrop,     new[] { "text/uri-list" }),
            (DataFormats.UnicodeText,  new[] { "text/plain;charset=utf-8", "UTF8_STRING", "text/plain", "STRING", "TEXT" }),
            (DataFormats.Text,         new[] { "text/plain;charset=utf-8", "UTF8_STRING", "text/plain", "STRING", "TEXT" }),
            (DataFormats.StringFormat, new[] { "text/plain;charset=utf-8", "UTF8_STRING", "text/plain", "STRING", "TEXT" }),
            (DataFormats.Rtf,          new[] { "text/rtf", "application/rtf" }),
            (DataFormats.Html,         new[] { "text/html" }),
            ("PNG",                    new[] { "image/png" }),
            (DataFormats.Bitmap,       new[] { "image/png" }),
        };

        private readonly string[] _mimeTypes;
        private readonly Func<string, byte[]?> _read;
        private readonly Dictionary<string, object?> _cache = new(StringComparer.Ordinal);

        internal DragDataObject(string[] mimeTypes, Func<string, byte[]?> read)
        {
            _mimeTypes = mimeTypes ?? Array.Empty<string>();
            _read = read;
        }

        /// <summary>The MIME type this drag offers for a WPF format, or null if it offers none.</summary>
        private string? MimeFor(string format)
        {
            foreach ((string mapped, string[] mimes) in s_map)
            {
                if (!string.Equals(mapped, format, StringComparison.Ordinal)) continue;
                foreach (string mime in mimes)
                {
                    foreach (string offered in _mimeTypes)
                    {
                        if (string.Equals(mime, offered, StringComparison.Ordinal)) return mime;
                    }
                }
            }

            // An unmapped format may still be offered verbatim (an app dragging its own private type).
            foreach (string offered in _mimeTypes)
            {
                if (string.Equals(format, offered, StringComparison.Ordinal)) return format;
            }
            return null;
        }

        public object? GetData(string format) => GetData(format, autoConvert: true);

        public object? GetData(Type format) => GetData(format.FullName!, autoConvert: true);

        public object? GetData(string format, bool autoConvert)
        {
            if (format is null) return null;
            if (_cache.TryGetValue(format, out object? cached)) return cached;

            string? mime = MimeFor(format);
            object? value = null;
            if (mime is not null)
            {
                byte[]? bytes = _read(mime);
                if (bytes is not null) value = Decode(format, bytes);
            }

            _cache[format] = value;
            return value;
        }

        private static object? Decode(string format, byte[] bytes)
        {
            if (string.Equals(format, DataFormats.FileDrop, StringComparison.Ordinal))
            {
                return ParseUriList(bytes);
            }

            if (string.Equals(format, DataFormats.UnicodeText, StringComparison.Ordinal)
                || string.Equals(format, DataFormats.Text, StringComparison.Ordinal)
                || string.Equals(format, DataFormats.StringFormat, StringComparison.Ordinal)
                || string.Equals(format, DataFormats.Html, StringComparison.Ordinal))
            {
                return Encoding.UTF8.GetString(bytes);
            }

            if (string.Equals(format, DataFormats.Rtf, StringComparison.Ordinal))
            {
                // RTF is an ASCII wire format, and a stray high byte is likelier to be a mislabelled
                // Latin-1 file than UTF-8 -- decoding it as ASCII would replace it with '?'.
                return Encoding.Latin1.GetString(bytes);
            }

            // Anything else is handed over as bytes; the app knows what its own format means.
            return bytes;
        }

        /// <summary>
        /// text/uri-list (RFC 2483) to the local paths FileDrop promises. Lines starting with '#' are
        /// comments, and non-file schemes are dropped rather than handed over as unusable paths.
        /// </summary>
        private static string[] ParseUriList(byte[] bytes)
        {
            var paths = new List<string>();
            foreach (string raw in Encoding.UTF8.GetString(bytes).Split('\n'))
            {
                string line = raw.Trim('\r', ' ', '\t');
                if (line.Length == 0 || line[0] == '#') continue;
                if (!Uri.TryCreate(line, UriKind.Absolute, out Uri? uri) || !uri.IsFile) continue;
                paths.Add(uri.LocalPath);
            }
            return paths.ToArray();
        }

        public bool GetDataPresent(string format) => GetDataPresent(format, autoConvert: true);

        public bool GetDataPresent(Type format) => GetDataPresent(format.FullName!, autoConvert: true);

        public bool GetDataPresent(string format, bool autoConvert) => format is not null && MimeFor(format) is not null;

        public string[] GetFormats() => GetFormats(autoConvert: true);

        public string[] GetFormats(bool autoConvert)
        {
            var formats = new List<string>();
            foreach ((string format, _) in s_map)
            {
                if (MimeFor(format) is not null && !formats.Contains(format)) formats.Add(format);
            }
            // Surface the raw types too, so an app that set a private format can find it.
            foreach (string mime in _mimeTypes)
            {
                if (!formats.Contains(mime)) formats.Add(mime);
            }
            return formats.ToArray();
        }

        // A drag offer is read-only: it describes what the SOURCE process holds.
        public void SetData(object data) => throw new NotSupportedException();
        public void SetData(string format, object data) => throw new NotSupportedException();
        public void SetData(Type format, object data) => throw new NotSupportedException();
        public void SetData(string format, object data, bool autoConvert) => throw new NotSupportedException();
    }

    /// <summary>
    ///  The drag SOURCE half: hands the data to the platform and blocks until the drag ends.
    /// </summary>
    internal static class PlatformDragSource
    {
        // WPF format -> the MIME types to advertise for it, best first.
        private static readonly (string Format, string[] Mimes)[] s_map =
        {
            (DataFormats.FileDrop,    new[] { "text/uri-list" }),
            (DataFormats.UnicodeText, new[] { "text/plain;charset=utf-8", "text/plain", "UTF8_STRING", "STRING", "TEXT" }),
            (DataFormats.Rtf,         new[] { "text/rtf", "application/rtf" }),
            (DataFormats.Html,        new[] { "text/html" }),
            ("PNG",                   new[] { "image/png" }),
        };

        /// <summary>
        ///  Advertised on every drag we start, and the reason an in-process drag works at all.
        /// </summary>
        /// <remarks>
        ///  The commonest WPF drag carries neither text nor files but a plain CLR object -- reordering
        ///  items between two ListBoxes in one app. There is no MIME type for "a reference to an object
        ///  in my heap", and inventing a serialization would be both lossy and wrong (the drop handler
        ///  expects the very instance it dragged). So the wire type is a marker with no useful bytes,
        ///  and <see cref="PlatformDropTarget"/> recognises it coming back and hands the original
        ///  <see cref="IDataObject"/> straight to the drop target. Another application sees a type it
        ///  does not understand and refuses the drop, which is the correct outcome.
        /// </remarks>
        internal const string InProcessMime = PlatformDragDrop.InProcessMime;

        /// <summary>The data of the drag this process started, while it is in flight.</summary>
        internal static IDataObject? CurrentData { get; private set; }

        internal static DragDropEffects DoDragDrop(DependencyObject dragSource, object data, DragDropEffects allowedEffects)
        {
            IDataObject dataObject = DataObjectFactory.Create(data);

            IntPtr surface = IntPtr.Zero;
            if (PresentationSource.FromDependencyObject(dragSource) is HwndSource source)
            {
                surface = source.Handle;
            }
            if (surface == IntPtr.Zero) return DragDropEffects.None;

            // Advertise every MIME type the data can actually produce, so the receiving app can pick,
            // plus the in-process marker so our own windows get the data unflattened.
            var mimes = new List<string> { InProcessMime };
            foreach ((string format, string[] candidates) in s_map)
            {
                if (!dataObject.GetDataPresent(format)) continue;
                foreach (string mime in candidates)
                {
                    if (!mimes.Contains(mime)) mimes.Add(mime);
                }
            }

            // The source-side events come from the very same OleDragSource Windows uses. Like
            // OleDropTarget it is plain managed code behind a plain interface -- only its CALLER was
            // OLE -- so reusing it gets QueryContinueDrag, GiveFeedback, and both default handlers
            // (Esc cancels; UseDefaultCursors) without a second implementation to keep in step.
            var eventSource = (UnsafeNativeMethods.IOleDropSource)new OleDragSource(dragSource);

            CurrentData = dataObject;
            try
            {
                // Only the transport differs per head; everything above this point, and the whole
                // drop side, is shared.
                int performed;
                bool started;
                if (ManagedDragLoop.IsRequired)
                {
                    // No platform transport on this head at all, so the drag runs in managed code and
                    // stays inside the application. See ManagedDragLoop for why that is most of a drag.
                    return ManagedDragLoop.Run(dragSource, allowedEffects, eventSource);
                }
                else if (OperatingSystem.IsMacOS())
                {
                    // AppKit ends a drag on Escape itself, so the source gets no query-continue hook
                    // and needs none -- see CocoaDragDrop.
                    performed = MS.Internal.Interop.CocoaDragDrop.StartDrag(
                        surface, mimes.ToArray(), mime => Encode(dataObject, mime), (int)allowedEffects,
                        out started, effect => eventSource.OleGiveFeedback(effect));
                }
                else
                {
                    performed = MS.Internal.Interop.Wayland.WaylandDragDrop.StartDrag(
                        surface, mimes.ToArray(), mime => Encode(dataObject, mime), (int)allowedEffects,
                        out started,
                        () => QueryContinue(eventSource), effect => eventSource.OleGiveFeedback(effect));
                }

                // The platform would not begin the drag. The commonest reason is that the gesture is
                // a TOUCH one: both transports need a held pointer button to hang the drag off, and a
                // finger provides none. Rather than let a drag on a touchscreen do nothing at all,
                // run it inside the application, where the finger is all that is needed. A drag with
                // no gesture behind it -- DoDragDrop called from nowhere -- is refused there in turn.
                if (!started)
                {
                    return ManagedDragLoop.Run(dragSource, allowedEffects, eventSource);
                }

                return (DragDropEffects)performed;
            }
            finally
            {
                CurrentData = null;
            }
        }

        /// <summary>
        ///  Asks the drag source whether to keep going. Wayland has no IDropSource for the compositor
        ///  to call, so the pump asks on every tick instead.
        /// </summary>
        /// <returns>False to cancel the drag.</returns>
        private static bool QueryContinue(UnsafeNativeMethods.IOleDropSource eventSource)
        {
            // The left button is reported held throughout: a Wayland drag exists only while one is,
            // and the release IS the drop, which the compositor tells us about separately. Claiming
            // otherwise would make the default handler ask for a drop we cannot perform.
            DragDropKeyStates states = DragDropKeyStates.LeftMouseButton;
            ModifierKeys modifiers = Keyboard.Modifiers;
            if ((modifiers & ModifierKeys.Control) != 0) states |= DragDropKeyStates.ControlKey;
            if ((modifiers & ModifierKeys.Shift) != 0) states |= DragDropKeyStates.ShiftKey;
            if ((modifiers & ModifierKeys.Alt) != 0) states |= DragDropKeyStates.AltKey;

            int hr = eventSource.OleQueryContinueDrag(
                Keyboard.IsKeyDown(Key.Escape) ? 1 : 0, (int)states);

            return hr != NativeMethods.DRAGDROP_S_CANCEL;
        }

        /// <summary>Produce the bytes for one requested MIME type, or null if the data has none.</summary>
        private static byte[]? Encode(IDataObject data, string mime)
        {
            if (string.Equals(mime, InProcessMime, StringComparison.Ordinal))
            {
                // A marker, not a payload: whoever can use it reads CurrentData instead. It still has
                // to answer with SOMETHING, because a source that writes nothing for a type it
                // advertised leaves the receiver blocking on the pipe.
                return Array.Empty<byte>();
            }

            if (string.Equals(mime, "text/uri-list", StringComparison.Ordinal))
            {
                if (data.GetData(DataFormats.FileDrop) is not string[] files) return null;
                var builder = new StringBuilder();
                foreach (string path in files)
                {
                    // text/uri-list wants URIs and CRLF line endings (RFC 2483).
                    builder.Append(new Uri(path).AbsoluteUri).Append("\r\n");
                }
                return Encoding.UTF8.GetBytes(builder.ToString());
            }

            if (mime.StartsWith("text/plain", StringComparison.Ordinal)
                || string.Equals(mime, "UTF8_STRING", StringComparison.Ordinal)
                || string.Equals(mime, "STRING", StringComparison.Ordinal)
                || string.Equals(mime, "TEXT", StringComparison.Ordinal))
            {
                return data.GetData(DataFormats.UnicodeText) is string text ? Encoding.UTF8.GetBytes(text) : null;
            }

            if (string.Equals(mime, "text/rtf", StringComparison.Ordinal)
                || string.Equals(mime, "application/rtf", StringComparison.Ordinal))
            {
                // RTF is ASCII on the wire by definition; the escapes in it carry anything else.
                return data.GetData(DataFormats.Rtf) is string rtf ? Encoding.ASCII.GetBytes(rtf) : null;
            }

            if (string.Equals(mime, "text/html", StringComparison.Ordinal))
            {
                // WPF's Html format is the CF_HTML wire format on Windows -- a header, then the
                // markup. Other toolkits want the markup alone, so hand over just that part.
                return data.GetData(DataFormats.Html) is string html
                    ? Encoding.UTF8.GetBytes(StripCfHtmlHeader(html))
                    : null;
            }

            if (string.Equals(mime, "image/png", StringComparison.Ordinal))
            {
                return data.GetData("PNG") as byte[];
            }

            return data.GetData(mime) as byte[];
        }

        /// <summary>
        ///  Drops the CF_HTML preamble, if there is one. The header is a fixed set of "Key:value"
        ///  lines ending at StartHTML's byte offset; anything without it is already bare markup.
        /// </summary>
        internal static string StripCfHtmlHeader(string html)
        {
            const string Marker = "StartHTML:";
            int marker = html.IndexOf(Marker, StringComparison.OrdinalIgnoreCase);
            if (marker < 0) return html;

            int digits = marker + Marker.Length;
            int end = digits;
            while (end < html.Length && char.IsDigit(html[end])) end++;

            // The offset counts BYTES from the start of the CF_HTML block. The header is ASCII, so
            // for it byte offset and character index agree -- and a mismatch would only mean a
            // slightly wrong cut, so refuse anything out of range rather than trusting it blindly.
            if (end == digits
                || !int.TryParse(html.AsSpan(digits, end - digits), out int start)
                || start <= 0 || start >= html.Length)
            {
                return html;
            }

            return html.Substring(start);
        }
    }

    /// <summary>
    ///  Drives <see cref="OleDropTarget"/> from platform drag events. One target per window, created
    ///  on first use and kept, mirroring the per-hwnd registration Windows does up front.
    /// </summary>
    /// <remarks>
    ///  Shared by every non-Windows backend rather than written once per head: the backends agree on
    ///  a MIME vocabulary at the seam (the Cocoa one translates its UTIs on the way through), and
    ///  everything past that point -- format mapping, hit-testing, the routed events -- is the same
    ///  work on all of them.
    /// </remarks>
    internal sealed class PlatformDropTarget : IPlatformDropTarget
    {
        private static PlatformDropTarget? s_instance;

        private readonly Dictionary<IntPtr, OleDropTarget> _targets = new();
        private IDataObject? _data;

        internal static void Install()
        {
            if (s_instance is not null) return;
            s_instance = new PlatformDropTarget();
            PlatformDragDrop.Target = s_instance;
        }

        /// <summary>The installed target, created if a window has not brought it up yet.</summary>
        internal static IPlatformDropTarget Instance
        {
            get
            {
                Install();
                return s_instance!;
            }
        }

        /// <summary>
        ///  The data object to hand the drop target. A drag this process started is recognised by the
        ///  marker type it advertises, and is answered with the ORIGINAL data object rather than a
        ///  view over the wire: an app dragging one of its own objects between two of its controls
        ///  must get that instance back, and no MIME encoding could carry it. Everything else is a
        ///  real cross-process drag and reads through the offer.
        /// </summary>
        private static IDataObject DataFor(string[] mimeTypes, Func<string, byte[]?> read)
        {
            if (PlatformDragSource.CurrentData is IDataObject own)
            {
                foreach (string mime in mimeTypes)
                {
                    if (string.Equals(mime, PlatformDragSource.InProcessMime, StringComparison.Ordinal))
                    {
                        return own;
                    }
                }
            }

            return new DragDataObject(mimeTypes, read);
        }

        private OleDropTarget TargetFor(IntPtr windowHandle)
        {
            if (!_targets.TryGetValue(windowHandle, out OleDropTarget? target))
            {
                target = new OleDropTarget(windowHandle);
                _targets[windowHandle] = target;
            }
            return target;
        }

        /// <summary>
        /// Screen point packed the way IOleDropTarget takes it: x in the low 32 bits, y in the high.
        /// The masking matters -- a negative coordinate (a window straddling the origin) would
        /// otherwise sign-extend over y.
        /// </summary>
        private static long Pack(int x, int y) => ((long)(uint)y << 32) | (uint)x;

        /// <summary>
        /// The button and modifier state WPF reports on the event. The left button is implicit: a
        /// Wayland drag only exists while one is held. Modifiers come from the keyboard because the
        /// drag events carry none, and Ctrl/Shift are how a user picks copy over move.
        /// </summary>
        private static int KeyStates()
        {
            var states = DragDropKeyStates.LeftMouseButton;
            ModifierKeys modifiers = Keyboard.Modifiers;
            if ((modifiers & ModifierKeys.Control) != 0) states |= DragDropKeyStates.ControlKey;
            if ((modifiers & ModifierKeys.Shift) != 0) states |= DragDropKeyStates.ShiftKey;
            if ((modifiers & ModifierKeys.Alt) != 0) states |= DragDropKeyStates.AltKey;
            return (int)states;
        }

        public int DragEnter(IntPtr windowHandle, int screenX, int screenY, string[] mimeTypes,
                             Func<string, byte[]?> read, int allowedEffects)
        {
            _data = DataFor(mimeTypes, read);
            int effects = allowedEffects;
            ((UnsafeNativeMethods.IOleDropTarget)TargetFor(windowHandle))
                .OleDragEnter(_data, KeyStates(), Pack(screenX, screenY), ref effects);
            return effects;
        }

        public int DragOver(IntPtr windowHandle, int screenX, int screenY, int allowedEffects)
        {
            if (_data is null) return 0;
            int effects = allowedEffects;
            ((UnsafeNativeMethods.IOleDropTarget)TargetFor(windowHandle))
                .OleDragOver(KeyStates(), Pack(screenX, screenY), ref effects);
            return effects;
        }

        public void DragLeave(IntPtr windowHandle)
        {
            if (_data is null) return;
            ((UnsafeNativeMethods.IOleDropTarget)TargetFor(windowHandle)).OleDragLeave();
            _data = null;
        }

        public int Drop(IntPtr windowHandle, int screenX, int screenY, int allowedEffects)
        {
            if (_data is null) return 0;
            int effects = allowedEffects;
            ((UnsafeNativeMethods.IOleDropTarget)TargetFor(windowHandle))
                .OleDrop(_data, KeyStates(), Pack(screenX, screenY), ref effects);
            _data = null;
            return effects;
        }
    }
}
