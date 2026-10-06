// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// Android drag-and-drop, both directions, over android.view.DragEvent and View.startDragAndDrop.
//
// The WPF side is reached through MS.Internal.Interop.PlatformDragDrop, exactly as the Wayland and
// Cocoa backends reach it: this file speaks Android's vocabulary and knows nothing about visual
// trees, and PresentationCore's target does the hit-testing and raises the routed events.
//
// Why a host seam instead of JNI from here: android.view.View, ClipData and DragEvent need the
// Mono.Android bindings, which WindowsBase cannot reference (see the header of AndroidWindow.cs).
// So the listener lives in the head payload and calls the Notify* methods below, and starting a drag
// goes out through IAndroidDragDropHost -- the same shape as AndroidClipboard and AndroidPrint.
//
// Three things about Android's model that shape everything here:
//
//   1. THE DATA IS ONLY READABLE AT THE DROP. A DragEvent carries its ClipDescription (the MIME
//      types) throughout, but its ClipData is null for every action except ACTION_DROP -- deliberate,
//      so an app cannot read what is merely passing over it. So DragEnter/DragOver advertise the
//      types and answer every read with null, and only Drop has bytes. GetDataPresent works the
//      whole time (it consults the advertised types); GetData before the drop does not, and cannot.
//   2. THERE ARE NO EFFECTS. Android has no copy/move/link distinction on a drag: a drop is consumed
//      or it is not. WPF's chosen effect still drives its own DragOver feedback, and on the source
//      side a consumed drop is reported as Copy where the drag allowed it -- duplicating rather than
//      moving is the safe direction to be wrong in.
//   3. THE SOURCE CANNOT BLOCK. startDragAndDrop returns immediately and the outcome arrives later
//      as ACTION_DRAG_ENDED. This head has no nested dispatcher frame to wait on it with (see
//      ManagedDragLoop), so DoDragDrop returns None and a source that deletes what it dragged on a
//      Move result keeps the original instead.
//
// What the ClipData can carry is text and URIs. Arbitrary bytes would need a ContentProvider to
// serve them, which an app that merely hosts WPF has not got -- the same limit AndroidClipboard
// documents. An in-process drag is unaffected: it rides the marker MIME type and the drop target
// answers it with the original data object, never touching the wire.
//

using System;
using System.Collections.Generic;
using System.Text;

namespace MS.Internal.Interop
{
    /// <summary>
    ///  The drag operations the Android head payload supplies. Implemented by AndroidHost.
    /// </summary>
    public interface IAndroidDragDropHost
    {
        /// <summary>
        ///  Begin a system drag from the view standing in for <paramref name="windowHandle"/>.
        /// </summary>
        /// <param name="mimeTypes">
        ///  Everything the drag can offer, for the ClipDescription. Types with no item behind them
        ///  are still advertised: a receiver decides what it wants from this list, and our own
        ///  windows recognise the in-process marker in it.
        /// </param>
        /// <param name="text">The drag's plain text, or null when it has none.</param>
        /// <param name="uris">The drag's URIs (file drops), or null when it has none.</param>
        /// <returns>
        ///  False when Android would not begin the drag -- no view, no touch to hang it off, a
        ///  refused request. The caller then runs the drag inside the application instead, which is
        ///  what keeps a drag working when this returns false rather than doing nothing at all.
        /// </returns>
        bool StartDrag(IntPtr windowHandle, string[] mimeTypes, string text, string[] uris);
    }

    /// <summary>
    /// The drag-and-drop backend for Android. Public for the head payload; everything crossing the
    /// seam is a primitive, so the payload never has to see a WindowsBase-internal type.
    /// </summary>
    // No [SupportedOSPlatform], matching AndroidWindow, AndroidClipboard and AndroidPrint next door:
    // the public WindowsBase surface carries no platform attributes, and adding one fails ApiCompat
    // against the hand-written reference assembly.
    public static class AndroidDragDrop
    {
        // WPF's DragDropEffects. Duplicated as constants rather than referenced, because this
        // assembly is below PresentationCore where that enum lives.
        private const int EffectNone = 0;
        private const int EffectCopy = 1;
        private const int EffectMove = 2;
        private const int EffectLink = 4;

        /// <summary>Set by the head alongside AndroidWindow.Host.</summary>
        public static IAndroidDragDropHost Host { get; set; }

        /// <summary>True once a head is attached that can start system drags.</summary>
        public static bool IsAvailable => OperatingSystem.IsAndroid() && Host is not null;

        // ---- target (a drag over one of our views) ----

        // The window the drag is currently inside, so a stray DragLocation or Drop for a view we
        // never entered cannot reach WPF, and so an exception on the way in can be unwound.
        private static IntPtr s_inside;

        // The types the current drag advertises, from the ClipDescription. Kept because the read
        // callback is handed out once at enter and outlives the call that made it.
        private static string[] s_types = Array.Empty<string>();

        // The dropped payload, populated for the length of one NotifyDrop call and cleared after.
        // Null everywhere else, which is exactly Android's own rule about when data may be read.
        private static string[] s_dropText;
        private static string[] s_dropUris;

        // ---- source (a drag we started) ----

        private static bool s_dragging;

        /// <summary>True while a drag this process started is still in flight.</summary>
        public static bool IsDragSourceActive => s_dragging;

        /// <summary>
        ///  A drag began somewhere in the app (ACTION_DRAG_STARTED). Returns whether this view wants
        ///  to hear about it -- and answering false is not a detail: Android sends a view NO further
        ///  events for a drag whose start it declined, so a listener that returns false here never
        ///  sees the drop either.
        /// </summary>
        /// <param name="mimeTypes">The drag's ClipDescription types.</param>
        public static bool NotifyDragStarted(IntPtr windowHandle, string[] mimeTypes)
        {
            if (PlatformDragDrop.Target is null) return false;
            s_types = mimeTypes ?? Array.Empty<string>();
            return true;
        }

        /// <summary>
        ///  The drag is over this view at a position (ACTION_DRAG_LOCATION), which is also how it is
        ///  first learnt to BE over it.
        /// </summary>
        /// <remarks>
        ///  ACTION_DRAG_ENTERED is deliberately not the thing that raises WPF's DragEnter: it carries
        ///  no valid position, and DragEnter needs one to find the element under the pointer. The
        ///  first ACTION_DRAG_LOCATION follows immediately and does carry one, so the enter is raised
        ///  from here on the first location inside a view instead.
        /// </remarks>
        /// <param name="xPixels">Position within the view, device pixels (DragEvent.getX/getY).</param>
        public static void NotifyDragLocation(IntPtr windowHandle, int xPixels, int yPixels, string[] mimeTypes)
        {
            IPlatformDropTarget target = PlatformDragDrop.Target;
            if (target is null) return;

            ToScreen(windowHandle, xPixels, yPixels, out int sx, out int sy);

            if (s_inside != windowHandle)
            {
                LeaveCurrent(target);
                s_types = mimeTypes ?? s_types;
                s_inside = windowHandle;
                Guard(() => target.DragEnter(windowHandle, sx, sy, s_types, Read, AllowedEffects()));
                return;
            }

            Guard(() => target.DragOver(windowHandle, sx, sy, AllowedEffects()));
        }

        /// <summary>The drag left a view (ACTION_DRAG_EXITED).</summary>
        public static void NotifyDragExited(IntPtr windowHandle)
        {
            IPlatformDropTarget target = PlatformDragDrop.Target;
            if (target is null || s_inside != windowHandle) return;
            LeaveCurrent(target);
        }

        /// <summary>
        ///  The drag was released over a view. Returns true when WPF consumed it, which the listener
        ///  returns for ACTION_DROP -- and which is what Android reports back to the drag's source.
        /// </summary>
        /// <param name="textItems">The ClipData's text items, or null.</param>
        /// <param name="uriItems">The ClipData's URI items, as strings, or null.</param>
        public static bool NotifyDrop(IntPtr windowHandle, int xPixels, int yPixels,
                                      string[] mimeTypes, string[] textItems, string[] uriItems)
        {
            IPlatformDropTarget target = PlatformDragDrop.Target;
            if (target is null) return false;

            // A drop without a preceding location is possible: a drag released the instant it
            // crosses into a view gets ACTION_DROP with no ACTION_DRAG_LOCATION before it. Enter the
            // view now rather than dropping onto nothing.
            if (s_inside != windowHandle) NotifyDragLocation(windowHandle, xPixels, yPixels, mimeTypes);
            if (s_inside != windowHandle) return false;

            s_dropText = textItems;
            s_dropUris = uriItems;
            try
            {
                ToScreen(windowHandle, xPixels, yPixels, out int sx, out int sy);
                int effect = Guard(() => target.Drop(windowHandle, sx, sy, AllowedEffects()));
                s_inside = IntPtr.Zero;
                s_types = Array.Empty<string>();
                return effect != EffectNone;
            }
            finally
            {
                s_dropText = null;
                s_dropUris = null;
            }
        }

        /// <summary>
        ///  The whole drag finished, wherever it landed. Sent to every view that took part, so it is
        ///  also how a view learns that a drag it was inside went away without dropping.
        /// </summary>
        public static void NotifyDragEnded(IntPtr windowHandle)
        {
            IPlatformDropTarget target = PlatformDragDrop.Target;
            if (target is not null && s_inside == windowHandle) LeaveCurrent(target);

            if (!s_dragging) return;
            s_dragging = false;

            // Our own drag is over, so the data it was carrying can go. DoDragDrop could not release
            // it: this head has no nested frame, so that call returned while the drag was still in
            // the user's hand.
            try { PlatformDragDrop.DragSourceFinished?.Invoke(); }
            catch (Exception e) { Console.WriteLine($"WPF Android: drag-finished handler threw: {e}"); }
        }

        /// <summary>
        ///  Reads one of the advertised MIME types. Answers only during a drop: see the file header
        ///  for why there is nothing to read before one.
        /// </summary>
        private static byte[] Read(string mime)
        {
            if (mime is null) return null;

            if (string.Equals(mime, PlatformDragDrop.InProcessMime, StringComparison.Ordinal))
            {
                // A marker with no payload. It never reaches here in practice -- the drop target
                // recognises it and answers with the original object -- but a type that is
                // advertised has to be answerable.
                return Array.Empty<byte>();
            }

            if (string.Equals(mime, "text/uri-list", StringComparison.Ordinal))
            {
                if (s_dropUris is null || s_dropUris.Length == 0) return null;
                var builder = new StringBuilder();
                foreach (string uri in s_dropUris)
                {
                    if (!string.IsNullOrEmpty(uri)) builder.Append(uri).Append("\r\n");   // RFC 2483
                }
                return Encoding.UTF8.GetBytes(builder.ToString());
            }

            if (IsTextMime(mime))
            {
                if (s_dropText is null || s_dropText.Length == 0) return null;
                return Encoding.UTF8.GetBytes(string.Join("\n", s_dropText));
            }

            // Anything else was advertised by the sender but cannot be carried without a content
            // provider on this side of the drop. Null, rather than empty bytes, so the format reads
            // as absent instead of present-and-empty.
            return null;
        }

        private static bool IsTextMime(string mime)
            => mime.StartsWith("text/plain", StringComparison.Ordinal)
               || string.Equals(mime, "text/html", StringComparison.Ordinal)
               || string.Equals(mime, "UTF8_STRING", StringComparison.Ordinal)
               || string.Equals(mime, "STRING", StringComparison.Ordinal)
               || string.Equals(mime, "TEXT", StringComparison.Ordinal);

        /// <summary>
        ///  What the source permits. Android says nothing about this, so everything WPF understands
        ///  is allowed and the target's own handlers decide -- which is what they would do on
        ///  Windows against a source that allowed all three.
        /// </summary>
        private static int AllowedEffects() => EffectCopy | EffectMove | EffectLink;

        private static void LeaveCurrent(IPlatformDropTarget target)
        {
            if (s_inside == IntPtr.Zero) return;
            IntPtr was = s_inside;
            s_inside = IntPtr.Zero;
            s_types = Array.Empty<string>();
            Guard(() => { target.DragLeave(was); return EffectNone; });
        }

        /// <summary>
        /// View-local device pixels to the screen device pixels the seam takes. Off Windows
        /// PointUtil.ScreenToClient subtracts the window's client screen origin, so adding the same
        /// origin here makes the round trip exact -- and identical to the touch path's basis.
        /// </summary>
        private static void ToScreen(IntPtr windowHandle, int xPixels, int yPixels, out int screenX, out int screenY)
        {
            screenX = xPixels;
            screenY = yPixels;

            IPlatformWindow window = PlatformWindow.FromHandle(windowHandle);
            if (window is null) return;
            window.GetClientScreenOriginPixels(out int originX, out int originY);
            screenX += originX;
            screenY += originY;
        }

        /// <summary>
        /// Run WPF's side of a drag event without letting it escape into the Java listener. An
        /// exception crossing back through JNI is not something the runtime can carry, so a broken
        /// drop handler must cost the drop and nothing more.
        /// </summary>
        private static int Guard(Func<int> body)
        {
            try { return body(); }
            catch (Exception e)
            {
                Console.WriteLine($"WPF Android: drag target threw: {e}");
                return EffectNone;
            }
        }

        // ------------------------------------------------------------------ source ----

        /// <summary>
        ///  Start a system drag from <paramref name="windowHandle"/>.
        /// </summary>
        /// <param name="writer">Produces the bytes for one MIME type.</param>
        /// <param name="started">
        ///  False when Android would not begin the drag. The caller can then run it inside the
        ///  application instead. Distinct from a drag that ran and was refused, which reports true.
        /// </param>
        /// <returns>
        ///  Always <c>None</c>: the outcome is not known when this returns. See the file header.
        /// </returns>
        internal static int StartDrag(IntPtr windowHandle, string[] mimeTypes,
                                      Func<string, byte[]> writer, int allowedEffects, out bool started)
        {
            started = false;
            if (!IsAvailable || windowHandle == IntPtr.Zero) return EffectNone;
            if (mimeTypes is null || mimeTypes.Length == 0) return EffectNone;

            string text = null;
            string[] uris = null;
            foreach (string mime in mimeTypes)
            {
                if (text is null && IsTextMime(mime)) text = ReadString(writer, mime);
                else if (uris is null && string.Equals(mime, "text/uri-list", StringComparison.Ordinal))
                {
                    uris = ParseUriList(ReadString(writer, mime));
                }
            }

            try
            {
                started = Host.StartDrag(windowHandle, mimeTypes, text, uris);
            }
            catch (Exception e)
            {
                Console.WriteLine($"WPF Android: could not start a drag: {e}");
                started = false;
            }

            s_dragging = started;
            return EffectNone;
        }

        private static string ReadString(Func<string, byte[]> writer, string mime)
        {
            try
            {
                byte[] bytes = writer(mime);
                return bytes is null || bytes.Length == 0 ? null : Encoding.UTF8.GetString(bytes);
            }
            catch (Exception e)
            {
                Console.WriteLine($"WPF Android: drag writer threw for '{mime}': {e}");
                return null;
            }
        }

        private static string[] ParseUriList(string list)
        {
            if (string.IsNullOrEmpty(list)) return null;

            var uris = new List<string>();
            foreach (string line in list.Split('\n'))
            {
                string trimmed = line.Trim();
                // RFC 2483: '#' introduces a comment line, and blank lines are allowed.
                if (trimmed.Length == 0 || trimmed[0] == '#') continue;
                uris.Add(trimmed);
            }
            return uris.Count > 0 ? uris.ToArray() : null;
        }
    }
}
