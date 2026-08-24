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
        [DllImport(Lib)] private static extern int dbus_connection_dispatch(IntPtr connection);
        [DllImport(Lib)] private static extern int dbus_connection_get_dispatch_status(IntPtr connection);
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
        internal const int IterSize = 128;

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
        // ====================================================================================
        // The server half.
        //
        // Everything above talks TO a service. AT-SPI needs the opposite: the application IS a
        // service that an assistive technology calls into, on a SEPARATE bus, exporting an object
        // per accessible node. That needs three things libdbus has and the client half never used --
        // a private connection, an object-path handler, and the ability to construct replies and
        // signals rather than only method calls.
        // ====================================================================================

        [DllImport(Lib)] private static extern IntPtr dbus_connection_open_private([MarshalAs(UnmanagedType.LPUTF8Str)] string address, DBusError* error);
        [DllImport(Lib)] private static extern int dbus_bus_register(IntPtr connection, DBusError* error);
        [DllImport(Lib)] private static extern void dbus_connection_set_exit_on_disconnect(IntPtr connection, int exit);
        [DllImport(Lib)] private static extern int dbus_bus_request_name(IntPtr connection, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, uint flags, DBusError* error);
        [DllImport(Lib)] private static extern int dbus_connection_register_fallback(IntPtr connection, [MarshalAs(UnmanagedType.LPUTF8Str)] string path, DBusObjectPathVTable* vtable, IntPtr userData);
        [DllImport(Lib)] private static extern IntPtr dbus_message_new_method_return(IntPtr methodCall);
        [DllImport(Lib)] private static extern IntPtr dbus_message_new_error(IntPtr replyTo, [MarshalAs(UnmanagedType.LPUTF8Str)] string errorName, [MarshalAs(UnmanagedType.LPUTF8Str)] string? message);
        [DllImport(Lib)] private static extern IntPtr dbus_message_new_signal([MarshalAs(UnmanagedType.LPUTF8Str)] string path, [MarshalAs(UnmanagedType.LPUTF8Str)] string iface, [MarshalAs(UnmanagedType.LPUTF8Str)] string name);
        [DllImport(Lib)] private static extern IntPtr dbus_message_get_sender(IntPtr message);

        /// <summary>libdbus's DBusObjectPathVTable. Only the two function pointers matter.</summary>
        [StructLayout(LayoutKind.Sequential)]
        internal struct DBusObjectPathVTable
        {
            public IntPtr unregister_function;
            public IntPtr message_function;
            public IntPtr pad1, pad2, pad3, pad4;
        }

        /// <summary>DBusHandlerResult.</summary>
        internal const int HANDLER_RESULT_HANDLED = 0;
        internal const int HANDLER_RESULT_NOT_YET_HANDLED = 1;

        /// <summary>
        /// Opens a second, PRIVATE connection to an arbitrary bus address. The accessibility bus is
        /// not the session bus: its address comes from org.a11y.Bus.GetAddress on the session bus,
        /// and mixing the two on one connection would put the app's a11y objects on the session bus
        /// where nothing looks for them.
        ///
        /// exit_on_disconnect is turned OFF deliberately: an assistive technology stopping must not
        /// take the application down with it.
        /// </summary>
        internal static IntPtr OpenPrivate(string address)
        {
            DBusError err;
            dbus_error_init(&err);
            try
            {
                IntPtr connection = dbus_connection_open_private(address, &err);
                if (connection == IntPtr.Zero) return IntPtr.Zero;

                dbus_connection_set_exit_on_disconnect(connection, 0);

                if (dbus_bus_register(connection, &err) == 0)
                {
                    return IntPtr.Zero;
                }
                return connection;
            }
            finally { dbus_error_free(&err); }
        }

        /// <summary>
        /// Registers ONE handler for a whole object-path subtree.
        ///
        /// A fallback rather than an object per node, which matters at this scale: a tree of a few
        /// thousand accessible nodes would otherwise mean a few thousand registered object paths,
        /// each with its own allocation and lookup. Instead every path under the prefix arrives at
        /// one handler which reads the node id off the end of the path.
        /// </summary>
        internal static bool RegisterFallback(IntPtr connection, string pathPrefix, IntPtr messageFunction)
        {
            // The vtable is allocated natively and never freed, deliberately. libdbus keeps the
            // registration for the life of the connection, and whether it copies the struct or keeps
            // the pointer is not something its documentation commits to -- a managed local (or
            // anything the GC can move) would be a use-after-free that only ever reproduces on the
            // one platform this code runs on. One allocation, once, sidesteps the question.
            if (s_vtable == null)
            {
                s_vtable = (DBusObjectPathVTable*)Marshal.AllocHGlobal(sizeof(DBusObjectPathVTable));
                *s_vtable = default;
            }

            s_vtable->message_function = messageFunction;
            return dbus_connection_register_fallback(connection, pathPrefix, s_vtable, IntPtr.Zero) != 0;
        }

        private static DBusObjectPathVTable* s_vtable;

        private static string? Utf8(IntPtr p) => p == IntPtr.Zero ? null : Marshal.PtrToStringUTF8(p);

        internal static IntPtr NewMethodReturn(IntPtr call) => dbus_message_new_method_return(call);

        internal static IntPtr NewError(IntPtr call, string name, string message) => dbus_message_new_error(call, name, message);

        internal static IntPtr NewSignal(string path, string iface, string name) => dbus_message_new_signal(path, iface, name);

        internal static string? Sender(IntPtr message) => Utf8(dbus_message_get_sender(message));

        /// <summary>Sends a message and drops our reference to it.</summary>
        internal static void SendAndUnref(IntPtr connection, IntPtr message)
        {
            if (message == IntPtr.Zero) return;
            uint serial;
            dbus_connection_send(connection, message, &serial);
            dbus_message_unref(message);
        }

        // ---- writing the shapes AT-SPI speaks in ----

        /// <summary>
        /// An AT-SPI object reference: (so) -- our bus name plus the object path of the node. This
        /// is the currency of the whole protocol; parents, children and event sources are all one of
        /// these.
        /// </summary>
        internal static void AppendObjectRef(byte* iter, string busName, string path)
        {
            byte* sub = stackalloc byte[IterSize];
            dbus_message_iter_open_container(iter, DBUS_TYPE_STRUCT, null, sub);
            AppendString(sub, DBUS_TYPE_STRING, busName);
            AppendString(sub, DBUS_TYPE_OBJECT_PATH, path);
            dbus_message_iter_close_container(iter, sub);
        }

        /// <summary>A variant wrapping a single object reference, for the Properties interface.</summary>
        internal static void AppendObjectRefVariant(byte* iter, string busName, string path)
        {
            byte* variant = stackalloc byte[IterSize];
            dbus_message_iter_open_container(iter, DBUS_TYPE_VARIANT, "(so)", variant);
            AppendObjectRef(variant, busName, path);
            dbus_message_iter_close_container(iter, variant);
        }

        internal static void AppendStringVariant(byte* iter, string value)
        {
            byte* variant = stackalloc byte[IterSize];
            dbus_message_iter_open_container(iter, DBUS_TYPE_VARIANT, "s", variant);
            AppendString(variant, DBUS_TYPE_STRING, value);
            dbus_message_iter_close_container(iter, variant);
        }

        internal static void AppendInt32Variant(byte* iter, int value)
        {
            byte* variant = stackalloc byte[IterSize];
            dbus_message_iter_open_container(iter, DBUS_TYPE_VARIANT, "i", variant);
            dbus_message_iter_append_basic(variant, DBUS_TYPE_INT32, &value);
            dbus_message_iter_close_container(iter, variant);
        }

        internal static void AppendDoubleVariant(byte* iter, double value)
        {
            byte* variant = stackalloc byte[IterSize];
            dbus_message_iter_open_container(iter, DBUS_TYPE_VARIANT, "d", variant);
            dbus_message_iter_append_basic(variant, DBUS_TYPE_DOUBLE, &value);
            dbus_message_iter_close_container(iter, variant);
        }

        /// <summary>
        /// The AT-SPI state set: an array of exactly two uint32s, a 64-bit bitmask split in half.
        /// Low word first.
        /// </summary>
        internal static void AppendStateSet(byte* iter, ulong states)
        {
            byte* array = stackalloc byte[IterSize];
            dbus_message_iter_open_container(iter, DBUS_TYPE_ARRAY, "u", array);
            uint low = (uint)(states & 0xFFFFFFFF);
            uint high = (uint)(states >> 32);
            dbus_message_iter_append_basic(array, DBUS_TYPE_UINT32, &low);
            dbus_message_iter_append_basic(array, DBUS_TYPE_UINT32, &high);
            dbus_message_iter_close_container(iter, array);
        }

        internal static void AppendString(byte* iter, int type, string value)
        {
            IntPtr utf8 = Marshal.StringToCoTaskMemUTF8(value ?? string.Empty);
            try { dbus_message_iter_append_basic(iter, type, &utf8); }
            finally { Marshal.FreeCoTaskMem(utf8); }
        }

        internal static void AppendInt32(byte* iter, int value) => dbus_message_iter_append_basic(iter, DBUS_TYPE_INT32, &value);

        internal static void AppendUInt32(byte* iter, uint value) => dbus_message_iter_append_basic(iter, DBUS_TYPE_UINT32, &value);

        internal static void AppendDouble(byte* iter, double value) => dbus_message_iter_append_basic(iter, DBUS_TYPE_DOUBLE, &value);

        internal const int DBUS_TYPE_DOUBLE = (int)'d';

        internal static void IterInitAppend(IntPtr message, byte* iter) => dbus_message_iter_init_append(message, iter);

        internal static bool IterInit(IntPtr message, byte* iter) => dbus_message_iter_init(message, iter) != 0;

        internal static int IterArgType(byte* iter) => dbus_message_iter_get_arg_type(iter);

        internal static bool IterNext(byte* iter) => dbus_message_iter_next(iter) != 0;

        internal static int ReadInt32(byte* iter)
        {
            int value = 0;
            dbus_message_iter_get_basic(iter, &value);
            return value;
        }

        internal static string ReadString(byte* iter)
        {
            IntPtr ptr = IntPtr.Zero;
            dbus_message_iter_get_basic(iter, &ptr);
            return Utf8(ptr) ?? string.Empty;
        }

        internal static void Flush(IntPtr connection) => dbus_connection_flush(connection);

        internal static int Dispatch(IntPtr connection, int timeoutMs) => dbus_connection_read_write_dispatch(connection, timeoutMs);

        private const int DBUS_DISPATCH_DATA_REMAINS = 0;

        /// <summary>
        /// Read whatever has arrived and run the registered object handlers for ALL of it.
        ///
        /// The obvious call, dbus_connection_read_write_dispatch, dispatches exactly ONE message per
        /// invocation -- libdbus documents this and it is easy to miss, because for a client that
        /// only makes calls the difference never shows. For a connection that SERVES an object path
        /// it is fatal: a caller's request is answered one wake-up late, and since answering it is
        /// itself what the caller was waiting for, nothing ever wakes the loop again and every
        /// request times out. Drain to DISPATCH_COMPLETE instead.
        ///
        /// The read is separated from the dispatch for the same reason. Data already sitting in
        /// libdbus's incoming queue -- put there as a side effect of an earlier flush, say -- leaves
        /// the socket quiet, so a loop that only dispatches when poll() reports POLLIN never sees it.
        /// </summary>
        internal static void DrainDispatch(IntPtr connection)
        {
            if (connection == IntPtr.Zero) return;

            dbus_connection_read_write(connection, 0);
            while (dbus_connection_get_dispatch_status(connection) == DBUS_DISPATCH_DATA_REMAINS)
            {
                dbus_connection_dispatch(connection);
            }
        }

        internal static int FdOf(IntPtr connection)
        {
            int fd = -1;
            dbus_connection_get_unix_fd(connection, &fd);
            return fd;
        }

        internal static void OpenArray(byte* iter, string signature, byte* sub)
            => dbus_message_iter_open_container(iter, DBUS_TYPE_ARRAY, signature, sub);

        internal static void OpenStruct(byte* iter, byte* sub)
            => dbus_message_iter_open_container(iter, DBUS_TYPE_STRUCT, null, sub);

        internal static void CloseContainer(byte* iter, byte* sub)
            => dbus_message_iter_close_container(iter, sub);

        internal static void OpenDictEntry(byte* array, byte* entry)
            => dbus_message_iter_open_container(array, DBUS_TYPE_DICT_ENTRY, null, entry);

        /// <summary>
        /// Reads a double out of a variant, tolerating the integer a client may send instead --
        /// a caller setting a slider to 5 has no reason to know the property is typed double.
        /// </summary>
        internal static bool TryReadDoubleVariant(byte* iter, out double value)
        {
            value = 0;
            if (dbus_message_iter_get_arg_type(iter) != DBUS_TYPE_VARIANT) return false;

            byte* variant = stackalloc byte[IterSize];
            dbus_message_iter_recurse(iter, variant);

            switch (dbus_message_iter_get_arg_type(variant))
            {
                case DBUS_TYPE_DOUBLE:
                    double d = 0;
                    dbus_message_iter_get_basic(variant, &d);
                    value = d;
                    return true;
                case DBUS_TYPE_INT32:
                    int i = 0;
                    dbus_message_iter_get_basic(variant, &i);
                    value = i;
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>Reads an int32 argument, or <paramref name="fallback"/> if this is not one.</summary>
        internal static int ReadInt32Or(byte* iter, int fallback)
            => dbus_message_iter_get_arg_type(iter) == DBUS_TYPE_INT32 ? ReadInt32(iter) : fallback;

        internal static void OpenAndCloseEmptyArray(byte* iter, string signature)
        {
            byte* sub = stackalloc byte[IterSize];
            dbus_message_iter_open_container(iter, DBUS_TYPE_ARRAY, signature, sub);
            dbus_message_iter_close_container(iter, sub);
        }

        internal static void AppendBool(byte* iter, bool value)
        {
            int v = value ? 1 : 0;   // D-Bus booleans are 32-bit on the wire
            dbus_message_iter_append_basic(iter, DBUS_TYPE_BOOLEAN, &v);
        }

        internal static string? PathOf(IntPtr message) => Utf8(dbus_message_get_path(message));
        internal static string? InterfaceOf(IntPtr message) => Utf8(dbus_message_get_interface(message));
        internal static string? MemberOf(IntPtr message) => Utf8(dbus_message_get_member(message));
        internal static string? UniqueNameOf(IntPtr connection) => Utf8(dbus_bus_get_unique_name(connection));

        /// <summary>
        /// One AT-SPI event signal. The body signature is (siiva{sv}): the detail string, two ints
        /// whose meaning depends on the event, a variant, and an attribute dictionary. Almost every
        /// event leaves the last two empty, but they are not optional -- a client reading a shorter
        /// body treats the signal as malformed and drops it.
        /// </summary>
        internal static void EmitAtSpiEvent(IntPtr connection, string busName, string path,
                                            string iface, string member, string detail,
                                            int detail1, int detail2)
            => EmitEvent(connection, busName, path, iface, member, detail, detail1, detail2,
                         AnyKind.Int, null, 0, null);

        /// <summary>An event whose <c>any_data</c> is a string -- a new name or description.</summary>
        internal static void EmitAtSpiEvent(IntPtr connection, string busName, string path,
                                            string iface, string member, string detail,
                                            int detail1, int detail2, string any)
            => EmitEvent(connection, busName, path, iface, member, detail, detail1, detail2,
                         AnyKind.String, any ?? string.Empty, 0, null);

        /// <summary>An event whose <c>any_data</c> is a number -- a slider's new value.</summary>
        internal static void EmitAtSpiEvent(IntPtr connection, string busName, string path,
                                            string iface, string member, string detail,
                                            int detail1, int detail2, double any)
            => EmitEvent(connection, busName, path, iface, member, detail, detail1, detail2,
                         AnyKind.Double, null, any, null);

        /// <summary>
        /// An event whose <c>any_data</c> is another accessible -- the child in a ChildrenChanged.
        /// A client that is told a child was added and not WHICH child has to re-read the whole
        /// subtree to find out, which for a virtualised list is the thing the event was meant to
        /// avoid.
        /// </summary>
        internal static void EmitAtSpiEventForObject(IntPtr connection, string busName, string path,
                                                     string iface, string member, string detail,
                                                     int detail1, int detail2, string objectPath)
            => EmitEvent(connection, busName, path, iface, member, detail, detail1, detail2,
                         AnyKind.ObjectRef, null, 0, objectPath);

        private enum AnyKind { Int, String, Double, ObjectRef }

        /// <summary>
        /// The AT-SPI event body: <c>(s i i v a{sv})</c> -- detail, two integers, the "any" payload
        /// whose type depends on the event, and a property bag.
        /// </summary>
        private static void EmitEvent(IntPtr connection, string busName, string path,
                                      string iface, string member, string detail,
                                      int detail1, int detail2,
                                      AnyKind kind, string text, double number, string objectPath)
        {
            IntPtr signal = dbus_message_new_signal(path, iface, member);
            if (signal == IntPtr.Zero) return;

            byte* iter = stackalloc byte[IterSize];
            dbus_message_iter_init_append(signal, iter);

            AppendString(iter, DBUS_TYPE_STRING, detail);
            AppendInt32(iter, detail1);
            AppendInt32(iter, detail2);

            byte* variant = stackalloc byte[IterSize];
            switch (kind)
            {
                case AnyKind.String:
                    dbus_message_iter_open_container(iter, DBUS_TYPE_VARIANT, "s", variant);
                    AppendString(variant, DBUS_TYPE_STRING, text);
                    break;
                case AnyKind.Double:
                    dbus_message_iter_open_container(iter, DBUS_TYPE_VARIANT, "d", variant);
                    AppendDouble(variant, number);
                    break;
                case AnyKind.ObjectRef:
                {
                    dbus_message_iter_open_container(iter, DBUS_TYPE_VARIANT, "(so)", variant);
                    byte* reference = stackalloc byte[IterSize];
                    dbus_message_iter_open_container(variant, DBUS_TYPE_STRUCT, null, reference);
                    AppendString(reference, DBUS_TYPE_STRING, busName);
                    AppendString(reference, DBUS_TYPE_OBJECT_PATH, objectPath);
                    dbus_message_iter_close_container(variant, reference);
                    break;
                }
                default:
                    dbus_message_iter_open_container(iter, DBUS_TYPE_VARIANT, "i", variant);
                    AppendInt32(variant, 0);
                    break;
            }
            dbus_message_iter_close_container(iter, variant);

            // The trailing a{sv} is the event's property bag. Left empty: clients resolve the source
            // from the signal's own sender and object path, which is information D-Bus already
            // carries, and every field that could go here is one more thing to keep in step.
            byte* dict = stackalloc byte[IterSize];
            dbus_message_iter_open_container(iter, DBUS_TYPE_ARRAY, "{sv}", dict);
            dbus_message_iter_close_container(iter, dict);

            SendAndUnref(connection, signal);
        }

        /// <summary>
        /// Reads one boolean property over org.freedesktop.DBus.Properties, on the session bus.
        /// Returns null when the property (or its owner) does not exist, which is different from
        /// reading false: "no such service" and "switched off" want different answers.
        /// </summary>
        internal static bool? CallReadBoolProperty(string destination, string path, string iface, string property)
        {
            if (!IsAvailable) return null;

            IntPtr msg = dbus_message_new_method_call(destination, path, "org.freedesktop.DBus.Properties", "Get");
            if (msg == IntPtr.Zero) return null;

            DBusError err;
            dbus_error_init(&err);
            try
            {
                byte* args = stackalloc byte[IterSize];
                dbus_message_iter_init_append(msg, args);
                AppendString(args, DBUS_TYPE_STRING, iface);
                AppendString(args, DBUS_TYPE_STRING, property);

                IntPtr reply = dbus_connection_send_with_reply_and_block(Connection, msg, 5000, &err);
                if (reply == IntPtr.Zero) return null;

                try
                {
                    byte* iter = stackalloc byte[IterSize];
                    if (dbus_message_iter_init(reply, iter) == 0) return null;
                    if (dbus_message_iter_get_arg_type(iter) != DBUS_TYPE_VARIANT) return null;

                    byte* variant = stackalloc byte[IterSize];
                    dbus_message_iter_recurse(iter, variant);
                    if (dbus_message_iter_get_arg_type(variant) != DBUS_TYPE_BOOLEAN) return null;

                    int value = 0;
                    dbus_message_iter_get_basic(variant, &value);
                    return value != 0;
                }
                finally { dbus_message_unref(reply); }
            }
            finally
            {
                dbus_message_unref(msg);
                dbus_error_free(&err);
            }
        }

        /// <summary>Calls a method that returns one string, on the session bus.</summary>
        internal static string? CallReadString(string destination, string path, string iface, string method)
        {
            if (!IsAvailable) return null;

            IntPtr msg = dbus_message_new_method_call(destination, path, iface, method);
            if (msg == IntPtr.Zero) return null;

            DBusError err;
            dbus_error_init(&err);
            try
            {
                IntPtr reply = dbus_connection_send_with_reply_and_block(Connection, msg, 5000, &err);
                if (reply == IntPtr.Zero) return null;

                try
                {
                    byte* iter = stackalloc byte[IterSize];
                    if (dbus_message_iter_init(reply, iter) == 0) return null;
                    if (dbus_message_iter_get_arg_type(iter) != DBUS_TYPE_STRING) return null;
                    return ReadString(iter);
                }
                finally { dbus_message_unref(reply); }
            }
            finally
            {
                dbus_message_unref(msg);
                dbus_error_free(&err);
            }
        }

        /// <summary>
        /// Registers this application with the AT-SPI registry. Until this succeeds the tree is
        /// exported but nothing knows to look at it.
        /// </summary>
        internal static void CallEmbed(IntPtr bus, string busName, string rootPath)
        {
            IntPtr msg = dbus_message_new_method_call("org.a11y.atspi.Registry",
                                                      "/org/a11y/atspi/accessible/root",
                                                      "org.a11y.atspi.Socket", "Embed");
            if (msg == IntPtr.Zero) return;

            byte* iter = stackalloc byte[IterSize];
            dbus_message_iter_init_append(msg, iter);
            AppendObjectRef(iter, busName, rootPath);

            uint serial;
            dbus_connection_send(bus, msg, &serial);
            dbus_message_unref(msg);
        }

    }
}
