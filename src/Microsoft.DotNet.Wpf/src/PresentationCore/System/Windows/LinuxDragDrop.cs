// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// The WPF half of Linux drag-and-drop: turns a platform drag into WPF's DragEnter/DragOver/DragLeave/
// Drop routed events, and a dragged wl_data_offer into an IDataObject.
//
// The protocol half is MS.Internal.Interop.Wayland.WaylandDragDrop, which lives in WindowsBase and
// cannot see this assembly; the two meet at MS.Internal.Interop.PlatformDragDrop, into which this
// installs itself. See that file for why the dependency runs one way.
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
    [SupportedOSPlatform("linux")]
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
                || string.Equals(format, DataFormats.StringFormat, StringComparison.Ordinal))
            {
                return Encoding.UTF8.GetString(bytes);
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
    ///  A plain in-memory <see cref="IDataObject"/>, for when an app hands DoDragDrop a bare value
    ///  (a string, say) rather than a data object. On Windows that becomes a DataObject; here it
    ///  cannot, for the OLE32 reason above.
    /// </summary>
    internal sealed class MemoryDataObject : IDataObject
    {
        private readonly Dictionary<string, object?> _values = new(StringComparer.Ordinal);

        internal MemoryDataObject(object data)
        {
            if (data is string text)
            {
                _values[DataFormats.UnicodeText] = text;
                _values[DataFormats.Text] = text;
                _values[DataFormats.StringFormat] = text;
            }
            else if (data is string[] files)
            {
                _values[DataFormats.FileDrop] = files;
            }
            else if (data is not null)
            {
                _values[data.GetType().FullName!] = data;
            }
        }

        public object? GetData(string format) => format is not null && _values.TryGetValue(format, out object? v) ? v : null;
        public object? GetData(Type format) => GetData(format.FullName!);
        public object? GetData(string format, bool autoConvert) => GetData(format);
        public bool GetDataPresent(string format) => format is not null && _values.ContainsKey(format);
        public bool GetDataPresent(Type format) => GetDataPresent(format.FullName!);
        public bool GetDataPresent(string format, bool autoConvert) => GetDataPresent(format);
        public string[] GetFormats() { var keys = new string[_values.Count]; _values.Keys.CopyTo(keys, 0); return keys; }
        public string[] GetFormats(bool autoConvert) => GetFormats();
        public void SetData(object data) => SetData(data?.GetType().FullName ?? string.Empty, data!);
        public void SetData(string format, object data) => _values[format] = data;
        public void SetData(Type format, object data) => _values[format.FullName!] = data;
        public void SetData(string format, object data, bool autoConvert) => _values[format] = data;
    }

    /// <summary>
    ///  The drag SOURCE half: hands the data off to the compositor and blocks until the drag ends.
    /// </summary>
    [SupportedOSPlatform("linux")]
    internal static class LinuxDragSource
    {
        // WPF format -> the MIME types to advertise for it, best first.
        private static readonly (string Format, string[] Mimes)[] s_map =
        {
            (DataFormats.FileDrop,    new[] { "text/uri-list" }),
            (DataFormats.UnicodeText, new[] { "text/plain;charset=utf-8", "text/plain", "UTF8_STRING", "STRING", "TEXT" }),
            ("PNG",                   new[] { "image/png" }),
        };

        internal static DragDropEffects DoDragDrop(DependencyObject dragSource, object data, DragDropEffects allowedEffects)
        {
            IDataObject dataObject = data as IDataObject ?? new MemoryDataObject(data);

            IntPtr surface = IntPtr.Zero;
            if (PresentationSource.FromDependencyObject(dragSource) is HwndSource source)
            {
                surface = source.Handle;
            }
            if (surface == IntPtr.Zero) return DragDropEffects.None;

            // Advertise every MIME type the data can actually produce, so the receiving app can pick.
            var mimes = new List<string>();
            foreach ((string format, string[] candidates) in s_map)
            {
                if (!dataObject.GetDataPresent(format)) continue;
                foreach (string mime in candidates)
                {
                    if (!mimes.Contains(mime)) mimes.Add(mime);
                }
            }
            if (mimes.Count == 0) return DragDropEffects.None;

            int performed = MS.Internal.Interop.Wayland.WaylandDragDrop.StartDrag(
                surface, mimes.ToArray(), mime => Encode(dataObject, mime), (int)allowedEffects);

            return (DragDropEffects)performed;
        }

        /// <summary>Produce the bytes for one requested MIME type, or null if the data has none.</summary>
        private static byte[]? Encode(IDataObject data, string mime)
        {
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

            if (string.Equals(mime, "image/png", StringComparison.Ordinal))
            {
                return data.GetData("PNG") as byte[];
            }

            return data.GetData(mime) as byte[];
        }
    }

    /// <summary>
    ///  Drives <see cref="OleDropTarget"/> from platform drag events. One target per window, created
    ///  on first use and kept, mirroring the per-hwnd registration Windows does up front.
    /// </summary>
    [SupportedOSPlatform("linux")]
    internal sealed class LinuxDropTarget : IPlatformDropTarget
    {
        private static LinuxDropTarget? s_instance;

        private readonly Dictionary<IntPtr, OleDropTarget> _targets = new();
        private DragDataObject? _data;

        internal static void Install()
        {
            if (s_instance is not null) return;
            s_instance = new LinuxDropTarget();
            PlatformDragDrop.Target = s_instance;
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
            _data = new DragDataObject(mimeTypes, read);
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
