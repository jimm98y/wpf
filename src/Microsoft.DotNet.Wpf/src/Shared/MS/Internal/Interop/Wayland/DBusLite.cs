// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// Just enough D-Bus to talk to xdg-desktop-portal: method calls with basic argument types, signal
// match rules, and a pump that can be driven from the WPF dispatcher.
//
// Why libdbus-1 directly rather than the alternatives:
//   * shelling out to `gdbus`/`busctl` means a child process per call, shell quoting hazards, no
//     cancellation, and no way to associate a dialog with the parent window;
//   * a NuGet D-Bus client is not an option -- WindowsBase and PresentationFramework carry no
//     third-party references, by policy;
//   * libdbus-1 is present on every desktop Linux (it is how the session bus works at all), its ABI
//     has been frozen for well over a decade, and dbus_connection_read_write_dispatch is a
//     self-contained pump with no GLib main-loop entanglement -- which matters, because WPF owns
//     the loop and will only lend it a slice.
//
// Everything here is deliberately minimal: portals need method calls returning basic types plus one
// signal subscription. There is no marshalling framework, no proxy generation and no async model.
//

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace MS.Internal.Interop.Wayland
{
    [SupportedOSPlatform("linux")]
    internal static unsafe class DBusLite
    {
        private const string Lib = "libdbus-1.so.3";

        // DBusBusType
        private const int DBUS_BUS_SESSION = 0;

        // DBusMessageType
        private const int DBUS_MESSAGE_TYPE_METHOD_CALL = 1;
        private const int DBUS_MESSAGE_TYPE_SIGNAL = 4;

        // Argument type codes, which are literally the ASCII characters of the D-Bus signature.
        internal const int DBUS_TYPE_INVALID = 0;
        internal const int DBUS_TYPE_BYTE = 'y';
        internal const int DBUS_TYPE_BOOLEAN = 'b';
        internal const int DBUS_TYPE_INT32 = 'i';
        internal const int DBUS_TYPE_UINT32 = 'u';
        internal const int DBUS_TYPE_STRING = 's';
        internal const int DBUS_TYPE_OBJECT_PATH = 'o';
        internal const int DBUS_TYPE_VARIANT = 'v';
        internal const int DBUS_TYPE_ARRAY = 'a';
        internal const int DBUS_TYPE_DICT_ENTRY = 'e';
        internal const int DBUS_TYPE_STRUCT = 'r';

        [StructLayout(LayoutKind.Sequential)]
        private struct DBusError
        {
            public IntPtr name;
            public IntPtr message;
            public uint dummies;      // bitfield + padding; the struct is opaque past this point
            public IntPtr padding1;
        }

        [DllImport(Lib)] private static extern void dbus_error_init(DBusError* error);
        [DllImport(Lib)] private static extern void dbus_error_free(DBusError* error);
        [DllImport(Lib)] private static extern int dbus_error_is_set(DBusError* error);
        [DllImport(Lib)] private static extern IntPtr dbus_bus_get(int type, DBusError* error);
        [DllImport(Lib)] private static extern IntPtr dbus_bus_get_unique_name(IntPtr connection);
        [DllImport(Lib)] private static extern void dbus_bus_add_match(IntPtr connection, [MarshalAs(UnmanagedType.LPUTF8Str)] string rule, DBusError* error);
        [DllImport(Lib)] private static extern void dbus_connection_flush(IntPtr connection);
        [DllImport(Lib)] private static extern int dbus_connection_read_write_dispatch(IntPtr connection, int timeoutMs);
        [DllImport(Lib)] private static extern int dbus_connection_read_write(IntPtr connection, int timeoutMs);
        [DllImport(Lib)] private static extern IntPtr dbus_connection_pop_message(IntPtr connection);
        [DllImport(Lib)] private static extern int dbus_connection_get_unix_fd(IntPtr connection, int* fd);
        [DllImport(Lib)] private static extern int dbus_connection_send(IntPtr connection, IntPtr message, uint* serial);

        [DllImport(Lib)]
        private static extern IntPtr dbus_connection_send_with_reply_and_block(IntPtr connection, IntPtr message, int timeoutMs, DBusError* error);

        [DllImport(Lib)]
        private static extern IntPtr dbus_message_new_method_call(
            [MarshalAs(UnmanagedType.LPUTF8Str)] string destination,
            [MarshalAs(UnmanagedType.LPUTF8Str)] string path,
            [MarshalAs(UnmanagedType.LPUTF8Str)] string iface,
            [MarshalAs(UnmanagedType.LPUTF8Str)] string method);

        [DllImport(Lib)] private static extern void dbus_message_unref(IntPtr message);
        [DllImport(Lib)] private static extern int dbus_message_get_type(IntPtr message);
        [DllImport(Lib)] private static extern IntPtr dbus_message_get_path(IntPtr message);
        [DllImport(Lib)] private static extern IntPtr dbus_message_get_interface(IntPtr message);
        [DllImport(Lib)] private static extern IntPtr dbus_message_get_member(IntPtr message);

        [DllImport(Lib)] private static extern void dbus_message_iter_init_append(IntPtr message, byte* iter);
        [DllImport(Lib)] private static extern int dbus_message_iter_append_basic(byte* iter, int type, void* value);
        [DllImport(Lib)] private static extern int dbus_message_iter_open_container(byte* iter, int type, [MarshalAs(UnmanagedType.LPUTF8Str)] string? containedSignature, byte* sub);
        [DllImport(Lib)] private static extern int dbus_message_iter_close_container(byte* iter, byte* sub);

        [DllImport(Lib)] private static extern int dbus_message_iter_init(IntPtr message, byte* iter);
        [DllImport(Lib)] private static extern int dbus_message_iter_next(byte* iter);
        [DllImport(Lib)] private static extern int dbus_message_iter_get_arg_type(byte* iter);
        [DllImport(Lib)] private static extern void dbus_message_iter_get_basic(byte* iter, void* value);
        [DllImport(Lib)] private static extern void dbus_message_iter_recurse(byte* iter, byte* sub);

        // DBusMessageIter is opaque and stack-allocated by callers; the real struct is well under
        // this, and libdbus only ever writes through the pointer we hand it.
        private const int IterSize = 128;

        private static IntPtr s_connection;
        private static bool s_tried;
        private static readonly object s_lock = new object();

        /// <summary>The session bus, or Zero when there is none (a headless or sandboxed process).</summary>
        internal static IntPtr Connection
        {
            get
            {
                lock (s_lock)
                {
                    if (s_tried) return s_connection;
                    s_tried = true;
                    try
                    {
                        DBusError err;
                        dbus_error_init(&err);
                        s_connection = dbus_bus_get(DBUS_BUS_SESSION, &err);
                        if (dbus_error_is_set(&err) != 0)
                        {
                            WaylandDisplay.LogSink?.Invoke("session bus unavailable: " + Wl.FromUtf8(err.message));
                            s_connection = IntPtr.Zero;
                        }
                        dbus_error_free(&err);
                    }
                    catch (DllNotFoundException)
                    {
                        WaylandDisplay.LogSink?.Invoke("libdbus-1 not found; portal integration is unavailable.");
                        s_connection = IntPtr.Zero;
                    }
                    return s_connection;
                }
            }
        }

        internal static bool IsAvailable => Connection != IntPtr.Zero;

        /// <summary>This connection's unique bus name (":1.42"), used to predict portal request paths.</summary>
        internal static string UniqueName
            => Connection == IntPtr.Zero ? string.Empty : Wl.FromUtf8(dbus_bus_get_unique_name(Connection));

        /// <summary>The connection's file descriptor, so the run loop can poll it alongside Wayland's.</summary>
        internal static int Fd
        {
            get
            {
                if (Connection == IntPtr.Zero) return -1;
                int fd = -1;
                return dbus_connection_get_unix_fd(Connection, &fd) != 0 ? fd : -1;
            }
        }

        internal static void AddMatch(string rule)
        {
            if (Connection == IntPtr.Zero) return;
            DBusError err;
            dbus_error_init(&err);
            dbus_bus_add_match(Connection, rule, &err);
            if (dbus_error_is_set(&err) != 0)
                WaylandDisplay.LogSink?.Invoke($"dbus_bus_add_match('{rule}') failed: " + Wl.FromUtf8(err.message));
            dbus_error_free(&err);
            dbus_connection_flush(Connection);
        }

        /// <summary>Dispatch whatever is pending without blocking. Called from the WPF pump.</summary>
        internal static void DispatchPending()
        {
            if (Connection == IntPtr.Zero) return;
            dbus_connection_read_write_dispatch(Connection, 0);
        }

        // ---- Calls -----------------------------------------------------------------------

        /// <summary>
        /// A blocking method call whose reply is a single variant wrapping a uint -- the shape
        /// org.freedesktop.portal.Settings.ReadOne returns. Returns false when the call fails or the
        /// reply is not that shape.
        /// </summary>
        internal static bool CallReadUInt32Variant(string destination, string path, string iface, string method,
                                                   string[] args, out uint value)
        {
            value = 0;
            if (Connection == IntPtr.Zero) return false;

            IntPtr msg = dbus_message_new_method_call(destination, path, iface, method);
            if (msg == IntPtr.Zero) return false;

            try
            {
                byte* iter = stackalloc byte[IterSize];
                dbus_message_iter_init_append(msg, iter);
                foreach (string a in args)
                {
                    IntPtr str = Wl.Utf8(a);
                    dbus_message_iter_append_basic(iter, DBUS_TYPE_STRING, &str);
                }

                DBusError err;
                dbus_error_init(&err);
                IntPtr reply = dbus_connection_send_with_reply_and_block(Connection, msg, 5000, &err);
                bool failed = dbus_error_is_set(&err) != 0;
                if (failed)
                    WaylandDisplay.LogSink?.Invoke($"{iface}.{method} failed: " + Wl.FromUtf8(err.message));
                dbus_error_free(&err);
                if (reply == IntPtr.Zero) return false;

                try
                {
                    byte* r = stackalloc byte[IterSize];
                    if (dbus_message_iter_init(reply, r) == 0) return false;
                    return ReadUInt32Recursive(r, out value);
                }
                finally
                {
                    dbus_message_unref(reply);
                }
            }
            finally
            {
                dbus_message_unref(msg);
            }
        }

        /// <summary>
        /// Descend through however many variant layers wrap a uint and read it. Portals are
        /// inconsistent here by design: Settings.ReadOne returns `v` wrapping the value while the
        /// older Read returns `v` wrapping `v` wrapping it, so the depth is not fixed.
        /// </summary>
        private static bool ReadUInt32Recursive(byte* iter, out uint value)
        {
            value = 0;
            for (int depth = 0; depth < 8; depth++)
            {
                int type = dbus_message_iter_get_arg_type(iter);
                if (type == DBUS_TYPE_UINT32 || type == DBUS_TYPE_INT32)
                {
                    uint v;
                    dbus_message_iter_get_basic(iter, &v);
                    value = v;
                    return true;
                }
                if (type != DBUS_TYPE_VARIANT) return false;

                byte* sub = stackalloc byte[IterSize];
                dbus_message_iter_recurse(iter, sub);
                // Continue at the inner level. Copying the iterator forward is not possible with an
                // opaque struct, so recurse explicitly.
                return ReadUInt32Recursive(sub, out value);
            }
            return false;
        }

        /// <summary>
        /// Drain incoming messages, invoking <paramref name="onSignal"/> for each signal. Returns the
        /// number of messages handled.
        /// </summary>
        internal static int PumpSignals(Action<string, string, IntPtr> onSignal)
        {
            if (Connection == IntPtr.Zero) return 0;

            int handled = 0;
            dbus_connection_read_write_dispatch(Connection, 0);
            IntPtr msg;
            while ((msg = dbus_connection_pop_message(Connection)) != IntPtr.Zero)
            {
                try
                {
                    if (dbus_message_get_type(msg) == DBUS_MESSAGE_TYPE_SIGNAL)
                    {
                        onSignal(Wl.FromUtf8(dbus_message_get_interface(msg)),
                                 Wl.FromUtf8(dbus_message_get_member(msg)),
                                 msg);
                        handled++;
                    }
                }
                catch { }
                finally
                {
                    dbus_message_unref(msg);
                }
            }
            return handled;
        }

        /// <summary>Read the string arguments of a message, in order.</summary>
        internal static List<string> GetStringArgs(IntPtr message)
        {
            var result = new List<string>();
            byte* iter = stackalloc byte[IterSize];
            if (dbus_message_iter_init(message, iter) == 0) return result;
            do
            {
                if (dbus_message_iter_get_arg_type(iter) != DBUS_TYPE_STRING) continue;
                IntPtr p;
                dbus_message_iter_get_basic(iter, &p);
                result.Add(Wl.FromUtf8(p));
            }
            while (dbus_message_iter_next(iter) != 0);
            return result;
        }

        /// <summary>
        /// Call a portal method taking (s parent_window, s title, a{sv} options) and return the
        /// object path of the Request it created. Portals are asynchronous by design: the method
        /// returns immediately with a handle, and the ANSWER arrives later as a Response signal on
        /// that path (see PortalDialogs).
        /// </summary>
        internal static bool CallPortalWithOptions(string destination, string path, string iface, string method,
                                                   string parentWindow, string title,
                                                   (string Key, object Value)[] options, out string requestPath)
        {
            requestPath = string.Empty;
            if (Connection == IntPtr.Zero) return false;

            IntPtr msg = dbus_message_new_method_call(destination, path, iface, method);
            if (msg == IntPtr.Zero) return false;

            try
            {
                byte* iter = stackalloc byte[IterSize];
                dbus_message_iter_init_append(msg, iter);

                IntPtr parent = Wl.Utf8(parentWindow);
                dbus_message_iter_append_basic(iter, DBUS_TYPE_STRING, &parent);
                IntPtr titlePtr = Wl.Utf8(title);
                dbus_message_iter_append_basic(iter, DBUS_TYPE_STRING, &titlePtr);

                // a{sv}
                byte* array = stackalloc byte[IterSize];
                dbus_message_iter_open_container(iter, DBUS_TYPE_ARRAY, "{sv}", array);
                foreach ((string key, object value) in options)
                {
                    byte* entry = stackalloc byte[IterSize];
                    dbus_message_iter_open_container(array, DBUS_TYPE_DICT_ENTRY, null, entry);

                    IntPtr keyPtr = Wl.Utf8(key);
                    dbus_message_iter_append_basic(entry, DBUS_TYPE_STRING, &keyPtr);

                    byte* variant = stackalloc byte[IterSize];
                    switch (value)
                    {
                        case bool b:
                            {
                                dbus_message_iter_open_container(entry, DBUS_TYPE_VARIANT, "b", variant);
                                int v = b ? 1 : 0;   // D-Bus booleans are 32-bit on the wire
                                dbus_message_iter_append_basic(variant, DBUS_TYPE_BOOLEAN, &v);
                                break;
                            }
                        case uint u:
                            {
                                dbus_message_iter_open_container(entry, DBUS_TYPE_VARIANT, "u", variant);
                                dbus_message_iter_append_basic(variant, DBUS_TYPE_UINT32, &u);
                                break;
                            }
                        default:
                            {
                                dbus_message_iter_open_container(entry, DBUS_TYPE_VARIANT, "s", variant);
                                IntPtr s = Wl.Utf8(value?.ToString() ?? string.Empty);
                                dbus_message_iter_append_basic(variant, DBUS_TYPE_STRING, &s);
                                break;
                            }
                    }
                    dbus_message_iter_close_container(entry, variant);
                    dbus_message_iter_close_container(array, entry);
                }
                dbus_message_iter_close_container(iter, array);

                DBusError err;
                dbus_error_init(&err);
                IntPtr reply = dbus_connection_send_with_reply_and_block(Connection, msg, 10000, &err);
                if (dbus_error_is_set(&err) != 0)
                    WaylandDisplay.LogSink?.Invoke($"{iface}.{method} failed: " + Wl.FromUtf8(err.message));
                dbus_error_free(&err);
                if (reply == IntPtr.Zero) return false;

                try
                {
                    byte* r = stackalloc byte[IterSize];
                    if (dbus_message_iter_init(reply, r) == 0) return false;
                    int type = dbus_message_iter_get_arg_type(r);
                    if (type != DBUS_TYPE_OBJECT_PATH && type != DBUS_TYPE_STRING) return false;
                    IntPtr p;
                    dbus_message_iter_get_basic(r, &p);
                    requestPath = Wl.FromUtf8(p);
                    return true;
                }
                finally
                {
                    dbus_message_unref(reply);
                }
            }
            finally
            {
                dbus_message_unref(msg);
            }
        }

        /// <summary>The object path a signal was emitted on.</summary>
        internal static string GetPath(IntPtr message) => Wl.FromUtf8(dbus_message_get_path(message));

        /// <summary>
        /// Parse a portal Response signal: (u response, a{sv} results), pulling out the response
        /// code and the string array stored under <paramref name="arrayKey"/> (portals return the
        /// chosen files as "uris").
        /// </summary>
        internal static bool TryParsePortalResponse(IntPtr message, string arrayKey, out uint response, out List<string> values)
        {
            response = 2;   // "other" -- treated as cancelled
            values = new List<string>();

            byte* iter = stackalloc byte[IterSize];
            if (dbus_message_iter_init(message, iter) == 0) return false;
            if (dbus_message_iter_get_arg_type(iter) != DBUS_TYPE_UINT32) return false;
            uint code;
            dbus_message_iter_get_basic(iter, &code);
            response = code;

            if (dbus_message_iter_next(iter) == 0) return true;
            if (dbus_message_iter_get_arg_type(iter) != DBUS_TYPE_ARRAY) return true;

            byte* dict = stackalloc byte[IterSize];
            dbus_message_iter_recurse(iter, dict);
            while (dbus_message_iter_get_arg_type(dict) == DBUS_TYPE_DICT_ENTRY)
            {
                byte* entry = stackalloc byte[IterSize];
                dbus_message_iter_recurse(dict, entry);

                if (dbus_message_iter_get_arg_type(entry) == DBUS_TYPE_STRING)
                {
                    IntPtr keyPtr;
                    dbus_message_iter_get_basic(entry, &keyPtr);
                    string key = Wl.FromUtf8(keyPtr);

                    if (string.Equals(key, arrayKey, StringComparison.Ordinal) && dbus_message_iter_next(entry) != 0)
                    {
                        byte* variant = stackalloc byte[IterSize];
                        dbus_message_iter_recurse(entry, variant);
                        if (dbus_message_iter_get_arg_type(variant) == DBUS_TYPE_ARRAY)
                        {
                            byte* strings = stackalloc byte[IterSize];
                            dbus_message_iter_recurse(variant, strings);
                            while (dbus_message_iter_get_arg_type(strings) == DBUS_TYPE_STRING)
                            {
                                IntPtr sp;
                                dbus_message_iter_get_basic(strings, &sp);
                                values.Add(Wl.FromUtf8(sp));
                                if (dbus_message_iter_next(strings) == 0) break;
                            }
                        }
                    }
                }

                if (dbus_message_iter_next(dict) == 0) break;
            }
            return true;
        }

        /// <summary>Read a trailing uint (possibly variant-wrapped) from a signal's arguments.</summary>
        internal static bool TryGetUInt32Arg(IntPtr message, int index, out uint value)
        {
            value = 0;
            byte* iter = stackalloc byte[IterSize];
            if (dbus_message_iter_init(message, iter) == 0) return false;
            for (int i = 0; i < index; i++)
            {
                if (dbus_message_iter_next(iter) == 0) return false;
            }
            return ReadUInt32Recursive(iter, out value);
        }
    }
}
