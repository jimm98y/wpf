// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// Notification-area icons on Linux: each one a StatusNotifierItem (the freedesktop/KDE protocol GNOME's AppIndicator
// extension, KDE Plasma and most panels show), served on a private session-bus connection of its own. A WinForms
// NotifyIcon is Shell_NotifyIcon on Windows; here the panel asks the item for its icon, tooltip and title over D-Bus,
// and tells it of clicks (Activate, ContextMenu, SecondaryActivate, Scroll), which the host turns into the callback
// messages the Windows shell sends the icon's window.
//
// Each icon has its own object path under this connection's unique name, registered with the watcher by path, so
// one connection serves any number of icons without owning a bus name per icon.
//

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace MS.Internal.Interop.Wayland
{
    [SupportedOSPlatform("linux")]
    internal static unsafe class StatusNotifierItem
    {
        private const string Lib = "libdbus-1.so.3";
        private const string PathPrefix = "/org/wpfwebgpu/StatusNotifierItem";
        private const string ItemInterface = "org.kde.StatusNotifierItem";
        private const int IterSize = 128;
        private const int DBUS_TYPE_STRING = 's', DBUS_TYPE_OBJECT_PATH = 'o', DBUS_TYPE_INT32 = 'i', DBUS_TYPE_BOOLEAN = 'b',
                          DBUS_TYPE_ARRAY = 'a', DBUS_TYPE_STRUCT = 'r', DBUS_TYPE_VARIANT = 'v', DBUS_TYPE_DICT_ENTRY = 'e', DBUS_TYPE_BYTE = 'y';
        private const int DBUS_MESSAGE_TYPE_METHOD_CALL = 1;
        private const int DBUS_HANDLER_RESULT_HANDLED = 0, DBUS_HANDLER_RESULT_NOT_YET_HANDLED = 1;

        [StructLayout(LayoutKind.Sequential)]
        private struct DBusError { public IntPtr name, message; public uint dummy; public IntPtr padding1; }

        [StructLayout(LayoutKind.Sequential)]
        private struct VTable { public IntPtr unregister_function, message_function, pad1, pad2, pad3, pad4; }

        [DllImport(Lib)] private static extern void dbus_error_init(DBusError* error);
        [DllImport(Lib)] private static extern void dbus_error_free(DBusError* error);
        [DllImport(Lib)] private static extern IntPtr dbus_bus_get_private(int type, DBusError* error);
        [DllImport(Lib)] private static extern IntPtr dbus_bus_get_unique_name(IntPtr connection);
        [DllImport(Lib)] private static extern void dbus_connection_set_exit_on_disconnect(IntPtr connection, int exit);
        [DllImport(Lib)] private static extern int dbus_connection_register_fallback(IntPtr connection, [MarshalAs(UnmanagedType.LPUTF8Str)] string path, VTable* vtable, IntPtr userData);
        [DllImport(Lib)] private static extern int dbus_connection_send(IntPtr connection, IntPtr message, uint* serial);
        [DllImport(Lib)] private static extern void dbus_connection_flush(IntPtr connection);
        [DllImport(Lib)] private static extern IntPtr dbus_connection_send_with_reply_and_block(IntPtr connection, IntPtr message, int timeoutMs, DBusError* error);
        [DllImport(Lib)] private static extern IntPtr dbus_message_new_method_call([MarshalAs(UnmanagedType.LPUTF8Str)] string destination, [MarshalAs(UnmanagedType.LPUTF8Str)] string path, [MarshalAs(UnmanagedType.LPUTF8Str)] string iface, [MarshalAs(UnmanagedType.LPUTF8Str)] string method);
        [DllImport(Lib)] private static extern IntPtr dbus_message_new_method_return(IntPtr call);
        [DllImport(Lib)] private static extern IntPtr dbus_message_new_error(IntPtr call, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, [MarshalAs(UnmanagedType.LPUTF8Str)] string message);
        [DllImport(Lib)] private static extern IntPtr dbus_message_new_signal([MarshalAs(UnmanagedType.LPUTF8Str)] string path, [MarshalAs(UnmanagedType.LPUTF8Str)] string iface, [MarshalAs(UnmanagedType.LPUTF8Str)] string name);
        [DllImport(Lib)] private static extern void dbus_message_unref(IntPtr message);
        [DllImport(Lib)] private static extern int dbus_message_get_type(IntPtr message);
        [DllImport(Lib)] private static extern IntPtr dbus_message_get_path(IntPtr message);
        [DllImport(Lib)] private static extern IntPtr dbus_message_get_interface(IntPtr message);
        [DllImport(Lib)] private static extern IntPtr dbus_message_get_member(IntPtr message);
        [DllImport(Lib)] private static extern void dbus_message_iter_init_append(IntPtr message, byte* iter);
        [DllImport(Lib)] private static extern int dbus_message_iter_init(IntPtr message, byte* iter);
        [DllImport(Lib)] private static extern int dbus_message_iter_next(byte* iter);
        [DllImport(Lib)] private static extern int dbus_message_iter_get_arg_type(byte* iter);
        [DllImport(Lib)] private static extern void dbus_message_iter_get_basic(byte* iter, void* value);
        [DllImport(Lib)] private static extern int dbus_message_iter_append_basic(byte* iter, int type, void* value);
        [DllImport(Lib)] private static extern int dbus_message_iter_append_fixed_array(byte* iter, int elementType, void* value, int count);
        [DllImport(Lib)] private static extern int dbus_message_iter_open_container(byte* iter, int type, [MarshalAs(UnmanagedType.LPUTF8Str)] string? signature, byte* sub);
        [DllImport(Lib)] private static extern int dbus_message_iter_close_container(byte* iter, byte* sub);
        [DllImport(Lib)] private static extern int dbus_connection_read_write(IntPtr connection, int timeoutMs);
        [DllImport(Lib)] private static extern int dbus_connection_dispatch(IntPtr connection);
        [DllImport(Lib)] private static extern int dbus_connection_get_unix_fd(IntPtr connection, int* fd);

        /// <summary>A click on an icon: its id, and what was done -- 1 activate (primary click), 2 context menu
        /// (secondary click), 3 secondary activate (middle click), 4 scroll -- with the pointer's screen position.</summary>
        internal static event Action<int, int, int, int>? Clicked;

        /// <summary>
        /// An icon's menu, asked for as the panel shows it (com.canonical.dbusmenu, which GNOME's AppIndicator panel
        /// opens on a secondary click): item i (id i + 1) has parent parents[i] (0 the menu itself), label labels[i]
        /// and flags[i] -- 1 enabled, 2 visible, 4 separator, 8 a check box, 16 checked. Null for no menu.
        /// </summary>
        internal static Func<int, bool, (int[] Parents, string[] Labels, int[] Flags)?>? MenuProvider;

        /// <summary>A menu item chosen: (icon id, item id).</summary>
        internal static event Action<int, int>? MenuItemClicked;

        private sealed class Item
        {
            public int Id;
            public string Title = "";
            public string Tip = "";
            public int Width, Height;
            public byte[] Argb = Array.Empty<byte>();   // ARGB32 in network byte order, as the protocol carries it
        }

        private static readonly Dictionary<int, Item> s_items = new();
        private static IntPtr s_bus;
        private static string s_name = "";
        private static bool s_tried;
        private static VTable* s_vtable;
        private static delegate* unmanaged<IntPtr, IntPtr, IntPtr, int> s_handler;
        private static readonly object s_lock = new();

        /// <summary>The bus file descriptor, for the run loop to poll beside Wayland's.</summary>
        internal static int Fd
        {
            get
            {
                if (s_bus == IntPtr.Zero) return -1;
                int fd = -1;
                return dbus_connection_get_unix_fd(s_bus, &fd) != 0 ? fd : -1;
            }
        }

        /// <summary>Answers what the panel asked. Called from the run loop each tick.</summary>
        internal static void Pump()
        {
            if (s_bus == IntPtr.Zero) return;
            dbus_connection_read_write(s_bus, 0);
            while (dbus_connection_dispatch(s_bus) == 0) { }   // DBUS_DISPATCH_DATA_REMAINS = 0
        }

        private static bool Attach()
        {
            if (s_bus != IntPtr.Zero) return true;
            if (s_tried) return false;
            s_tried = true;
            DBusError err;
            dbus_error_init(&err);
            try
            {
                IntPtr bus = dbus_bus_get_private(0 /* DBUS_BUS_SESSION */, &err);
                if (bus == IntPtr.Zero) { Trace("no session bus"); return false; }
                dbus_connection_set_exit_on_disconnect(bus, 0);
                s_vtable = (VTable*)Marshal.AllocHGlobal(sizeof(VTable));
                *s_vtable = default;
                s_handler = &HandleMessage;
                s_vtable->message_function = (IntPtr)s_handler;
                if (dbus_connection_register_fallback(bus, PathPrefix, s_vtable, IntPtr.Zero) == 0) { Trace("register_fallback failed"); return false; }
                s_bus = bus;
                s_name = Marshal.PtrToStringUTF8(dbus_bus_get_unique_name(bus)) ?? "";
                Trace($"attached as {s_name}");
                return true;
            }
            catch (DllNotFoundException) { return false; }
            finally { dbus_error_free(&err); }
        }

        /// <summary>Shows an icon (or updates it): its pixels as straight BGRA rows (System.Drawing's), its tooltip.</summary>
        internal static bool Show(int id, string title, string tip, byte[]? bgra, int width, int height)
        {
            lock (s_lock)
            {
                if (!Attach()) return false;
                bool added = !s_items.TryGetValue(id, out Item? item);
                item ??= new Item { Id = id };
                item.Title = title ?? "";
                item.Tip = tip ?? "";
                if (bgra != null && width > 0 && height > 0 && bgra.Length >= width * height * 4)
                {
                    var argb = new byte[width * height * 4];
                    for (int i = 0; i < width * height; i++)
                    {
                        argb[i * 4] = bgra[i * 4 + 3];       // A
                        argb[i * 4 + 1] = bgra[i * 4 + 2];   // R
                        argb[i * 4 + 2] = bgra[i * 4 + 1];   // G
                        argb[i * 4 + 3] = bgra[i * 4];       // B
                    }
                    item.Argb = argb; item.Width = width; item.Height = height;
                }
                s_items[id] = item;
                if (added) Register(item);
                else
                {
                    Signal(item, "NewIcon");
                    Signal(item, "NewToolTip");
                    Signal(item, "NewTitle");
                }
                dbus_connection_flush(s_bus);
                return true;
            }
        }

        /// <summary>Takes an icon away: its object is gone, which the watcher notices.</summary>
        internal static void Remove(int id)
        {
            lock (s_lock)
            {
                if (!s_items.Remove(id) || s_bus == IntPtr.Zero) return;
                // the watcher watches the item's bus name, not its path: an icon of a connection that stays is
                // taken away by its status (Passive hides it in every panel), and its calls answered with an error
                Signal(new Item { Id = id }, "NewStatus", "Passive");
                dbus_connection_flush(s_bus);
            }
        }

        private static string PathOf(int id) => PathPrefix + "/" + id;

        private static void Register(Item item)
        {
            IntPtr call = dbus_message_new_method_call("org.kde.StatusNotifierWatcher", "/StatusNotifierWatcher",
                                                       "org.kde.StatusNotifierWatcher", "RegisterStatusNotifierItem");
            if (call == IntPtr.Zero) return;
            byte* iter = stackalloc byte[IterSize];
            dbus_message_iter_init_append(call, iter);
            AppendString(iter, PathOf(item.Id));
            DBusError err;
            dbus_error_init(&err);
            IntPtr reply = dbus_connection_send_with_reply_and_block(s_bus, call, 2000, &err);
            dbus_message_unref(call);
            if (reply == IntPtr.Zero) Trace("no StatusNotifierWatcher: " + (err.message == IntPtr.Zero ? "?" : Marshal.PtrToStringUTF8(err.message)));
            else { Trace($"registered {PathOf(item.Id)}"); dbus_message_unref(reply); }
            dbus_error_free(&err);
        }

        private static void Signal(Item item, string name, string? status = null)
        {
            IntPtr signal = dbus_message_new_signal(PathOf(item.Id), ItemInterface, name);
            if (signal == IntPtr.Zero) return;
            if (status != null)
            {
                byte* iter = stackalloc byte[IterSize];
                dbus_message_iter_init_append(signal, iter);
                AppendString(iter, status);
            }
            uint serial;
            dbus_connection_send(s_bus, signal, &serial);
            dbus_message_unref(signal);
        }

        // ---- the item's object -----------------------------------------------------------------

        [UnmanagedCallersOnly]
        private static int HandleMessage(IntPtr connection, IntPtr message, IntPtr userData)
        {
            try
            {
                if (dbus_message_get_type(message) != DBUS_MESSAGE_TYPE_METHOD_CALL) return DBUS_HANDLER_RESULT_NOT_YET_HANDLED;
                string path = Marshal.PtrToStringUTF8(dbus_message_get_path(message)) ?? "";
                string iface = Marshal.PtrToStringUTF8(dbus_message_get_interface(message)) ?? "";
                string member = Marshal.PtrToStringUTF8(dbus_message_get_member(message)) ?? "";
                bool menu = path.EndsWith("/Menu", StringComparison.Ordinal);
                ReadOnlySpan<char> idText = path.AsSpan(Math.Min(path.Length, PathPrefix.Length + 1));
                if (menu) idText = idText[..Math.Max(0, idText.Length - 5)];
                if (!path.StartsWith(PathPrefix + "/", StringComparison.Ordinal) || !int.TryParse(idText, out int id))
                    return DBUS_HANDLER_RESULT_NOT_YET_HANDLED;
                Item? item;
                lock (s_lock) s_items.TryGetValue(id, out item);
                IntPtr reply;
                if (item == null)
                    reply = dbus_message_new_error(message, "org.freedesktop.DBus.Error.UnknownObject", "no such icon");
                else if (menu)
                    reply = HandleMenu(message, id, iface, member);
                else if (member == "Introspect")
                    reply = StringReply(message, Introspection);
                else if (iface == "org.freedesktop.DBus.Properties" && member == "GetAll")
                    reply = GetAll(message, item);
                else if (iface == "org.freedesktop.DBus.Properties" && member == "Get")
                    reply = Get(message, item);
                else if (member is "Activate" or "ContextMenu" or "SecondaryActivate" or "Scroll")
                {
                    int x = 0, y = 0;
                    byte* iter = stackalloc byte[IterSize];
                    if (dbus_message_iter_init(message, iter) != 0 && dbus_message_iter_get_arg_type(iter) == DBUS_TYPE_INT32)
                    {
                        dbus_message_iter_get_basic(iter, &x);
                        if (dbus_message_iter_next(iter) != 0 && dbus_message_iter_get_arg_type(iter) == DBUS_TYPE_INT32) dbus_message_iter_get_basic(iter, &y);
                    }
                    int what = member switch { "Activate" => 1, "ContextMenu" => 2, "SecondaryActivate" => 3, _ => 4 };
                    Trace($"{member} on {id} at ({x},{y})");
                    try { Clicked?.Invoke(id, what, x, y); } catch (Exception) { }
                    reply = dbus_message_new_method_return(message);
                }
                else
                    reply = dbus_message_new_error(message, "org.freedesktop.DBus.Error.UnknownMethod", member);
                if (reply != IntPtr.Zero)
                {
                    uint serial;
                    dbus_connection_send(connection, reply, &serial);
                    dbus_message_unref(reply);
                }
                return DBUS_HANDLER_RESULT_HANDLED;
            }
            catch (Exception) { return DBUS_HANDLER_RESULT_NOT_YET_HANDLED; }
        }

        private static readonly string[] s_properties = { "Category", "Id", "Title", "Status", "IconName", "IconPixmap", "ToolTip", "ItemIsMenu", "WindowId", "Menu" };

        private static IntPtr GetAll(IntPtr call, Item item)
        {
            IntPtr reply = dbus_message_new_method_return(call);
            byte* iter = stackalloc byte[IterSize], array = stackalloc byte[IterSize], entry = stackalloc byte[IterSize];
            dbus_message_iter_init_append(reply, iter);
            dbus_message_iter_open_container(iter, DBUS_TYPE_ARRAY, "{sv}", array);
            foreach (string p in s_properties)
            {
                dbus_message_iter_open_container(array, DBUS_TYPE_DICT_ENTRY, null, entry);
                AppendString(entry, p);
                AppendProperty(entry, item, p);
                dbus_message_iter_close_container(array, entry);
            }
            dbus_message_iter_close_container(iter, array);
            return reply;
        }

        private static IntPtr Get(IntPtr call, Item item)
        {
            byte* args = stackalloc byte[IterSize];
            string? name = null;
            if (dbus_message_iter_init(call, args) != 0 && dbus_message_iter_next(args) != 0 && dbus_message_iter_get_arg_type(args) == DBUS_TYPE_STRING)
            {
                IntPtr s;
                dbus_message_iter_get_basic(args, &s);
                name = Marshal.PtrToStringUTF8(s);
            }
            if (name == null || Array.IndexOf(s_properties, name) < 0)
                return dbus_message_new_error(call, "org.freedesktop.DBus.Error.UnknownProperty", name ?? "?");
            IntPtr reply = dbus_message_new_method_return(call);
            byte* iter = stackalloc byte[IterSize];
            dbus_message_iter_init_append(reply, iter);
            AppendProperty(iter, item, name);
            return reply;
        }

        /// <summary>One property as a variant.</summary>
        private static void AppendProperty(byte* iter, Item item, string name)
        {
            byte* v = stackalloc byte[IterSize];
            string sig = name switch
            {
                "IconPixmap" => "a(iiay)", "ItemIsMenu" => "b", "WindowId" => "i", "ToolTip" => "(sa(iiay)ss)", "Menu" => "o", _ => "s",
            };
            dbus_message_iter_open_container(iter, DBUS_TYPE_VARIANT, sig, v);
            switch (name)
            {
                case "Category": AppendString(v, "ApplicationStatus"); break;
                case "Id": AppendString(v, AppId); break;
                case "Title": AppendString(v, item.Title.Length > 0 ? item.Title : AppId); break;
                case "Status": AppendString(v, "Active"); break;
                case "IconName": AppendString(v, ""); break;
                case "Menu": AppendBasicString(v, DBUS_TYPE_OBJECT_PATH, PathOf(item.Id) + "/Menu"); break;
                case "IconPixmap": AppendPixmaps(v, item); break;
                case "ItemIsMenu": { int f = 0; dbus_message_iter_append_basic(v, DBUS_TYPE_BOOLEAN, &f); break; }
                case "WindowId": { int w = 0; dbus_message_iter_append_basic(v, DBUS_TYPE_INT32, &w); break; }
                case "ToolTip":
                {
                    byte* st = stackalloc byte[IterSize];
                    dbus_message_iter_open_container(v, DBUS_TYPE_STRUCT, null, st);
                    AppendString(st, "");
                    AppendPixmaps(st, null);
                    AppendString(st, item.Tip);
                    AppendString(st, "");
                    dbus_message_iter_close_container(v, st);
                    break;
                }
            }
            dbus_message_iter_close_container(iter, v);
        }

        /// <summary>a(iiay): the icon at its one size, or none.</summary>
        private static void AppendPixmaps(byte* iter, Item? item)
        {
            byte* array = stackalloc byte[IterSize], st = stackalloc byte[IterSize], bytes = stackalloc byte[IterSize];
            dbus_message_iter_open_container(iter, DBUS_TYPE_ARRAY, "(iiay)", array);
            if (item != null && item.Argb.Length > 0)
            {
                dbus_message_iter_open_container(array, DBUS_TYPE_STRUCT, null, st);
                int w = item.Width, h = item.Height;
                dbus_message_iter_append_basic(st, DBUS_TYPE_INT32, &w);
                dbus_message_iter_append_basic(st, DBUS_TYPE_INT32, &h);
                dbus_message_iter_open_container(st, DBUS_TYPE_ARRAY, "y", bytes);
                fixed (byte* p = item.Argb)
                {
                    byte* pp = p;
                    dbus_message_iter_append_fixed_array(bytes, DBUS_TYPE_BYTE, &pp, item.Argb.Length);
                }
                dbus_message_iter_close_container(st, bytes);
                dbus_message_iter_close_container(array, st);
            }
            dbus_message_iter_close_container(iter, array);
        }

        private static void AppendBasicString(byte* iter, int type, string value)
        {
            IntPtr p = Marshal.StringToCoTaskMemUTF8(value);
            try { dbus_message_iter_append_basic(iter, type, &p); }
            finally { Marshal.FreeCoTaskMem(p); }
        }

        // ---- the icon's menu: com.canonical.dbusmenu -------------------------------------------------

        private static readonly Dictionary<int, (uint Revision, string Key)> s_menuRevisions = new();

        /// <summary>The menu as it is; <paramref name="opening"/> when the user is opening it (the root's "opened"), which is
        /// when the application hears of it (ContextMenuStrip.Opening) -- not each time the panel reads the layout.</summary>
        private static (int[] Parents, string[] Labels, int[] Flags) MenuOf(int id, bool opening = false)
        {
            (int[] Parents, string[] Labels, int[] Flags)? m = null;
            try { m = MenuProvider?.Invoke(id, opening); } catch (Exception) { }
            return m ?? (Array.Empty<int>(), Array.Empty<string>(), Array.Empty<int>());
        }

        /// <summary>The menu's revision, moved on when what it holds changed since it was last given.</summary>
        private static uint Revise(int id, (int[] Parents, string[] Labels, int[] Flags) m, out bool changed)
        {
            var key = new StringBuilder();
            for (int i = 0; i < m.Parents.Length; i++) key.Append(m.Parents[i]).Append('|').Append(m.Labels[i]).Append('|').Append(m.Flags[i]).Append(';');
            string k = key.ToString();
            s_menuRevisions.TryGetValue(id, out (uint Revision, string Key) last);
            changed = last.Key != k;
            if (changed) s_menuRevisions[id] = last = (last.Revision + 1, k);
            return last.Revision;
        }

        private static IntPtr HandleMenu(IntPtr call, int id, string iface, string member)
        {
            switch (member)
            {
                case "Introspect":
                    return StringReply(call, MenuIntrospection);
                case "GetLayout":
                {
                    var m = MenuOf(id);
                    uint revision = Revise(id, m, out _);
                    IntPtr reply = dbus_message_new_method_return(call);
                    byte* iter = stackalloc byte[IterSize];
                    dbus_message_iter_init_append(reply, iter);
                    dbus_message_iter_append_basic(iter, 'u', &revision);
                    AppendMenuNode(iter, m, 0);
                    return reply;
                }
                case "GetGroupProperties":
                {
                    var m = MenuOf(id);
                    IntPtr reply = dbus_message_new_method_return(call);
                    byte* iter = stackalloc byte[IterSize], array = stackalloc byte[IterSize], st = stackalloc byte[IterSize];
                    dbus_message_iter_init_append(reply, iter);
                    dbus_message_iter_open_container(iter, DBUS_TYPE_ARRAY, "(ia{sv})", array);
                    for (int i = 0; i <= m.Parents.Length; i++)
                    {
                        dbus_message_iter_open_container(array, DBUS_TYPE_STRUCT, null, st);
                        int itemId = i;
                        dbus_message_iter_append_basic(st, DBUS_TYPE_INT32, &itemId);
                        AppendMenuProperties(st, m, i);
                        dbus_message_iter_close_container(array, st);
                    }
                    dbus_message_iter_close_container(iter, array);
                    return reply;
                }
                case "AboutToShow":
                {
                    var m = MenuOf(id);
                    Revise(id, m, out bool changed);
                    if (changed) LayoutUpdated(id);
                    IntPtr reply = dbus_message_new_method_return(call);
                    byte* iter = stackalloc byte[IterSize];
                    dbus_message_iter_init_append(reply, iter);
                    int b = changed ? 1 : 0;
                    dbus_message_iter_append_basic(iter, DBUS_TYPE_BOOLEAN, &b);
                    return reply;
                }
                case "AboutToShowGroup":
                {
                    IntPtr reply = dbus_message_new_method_return(call);
                    byte* iter = stackalloc byte[IterSize], a1 = stackalloc byte[IterSize], a2 = stackalloc byte[IterSize];
                    dbus_message_iter_init_append(reply, iter);
                    dbus_message_iter_open_container(iter, DBUS_TYPE_ARRAY, "i", a1); dbus_message_iter_close_container(iter, a1);
                    dbus_message_iter_open_container(iter, DBUS_TYPE_ARRAY, "i", a2); dbus_message_iter_close_container(iter, a2);
                    return reply;
                }
                case "Event":
                {
                    byte* args = stackalloc byte[IterSize];
                    int itemId = -1;
                    string? eventId = null;
                    if (dbus_message_iter_init(call, args) != 0 && dbus_message_iter_get_arg_type(args) == DBUS_TYPE_INT32)
                    {
                        dbus_message_iter_get_basic(args, &itemId);
                        if (dbus_message_iter_next(args) != 0 && dbus_message_iter_get_arg_type(args) == DBUS_TYPE_STRING)
                        {
                            IntPtr s;
                            dbus_message_iter_get_basic(args, &s);
                            eventId = Marshal.PtrToStringUTF8(s);
                        }
                    }
                    Trace($"menu event {eventId} on item {itemId} of {id}");
                    if (eventId == "clicked" && itemId > 0)
                        try { MenuItemClicked?.Invoke(id, itemId); } catch (Exception) { }
                    // the menu opened (GNOME's panel says so of the root; its AboutToShow comes once, as the menu is set up):
                    // the application hears of it, and what it changed in it shows
                    if (eventId == "opened" && itemId == 0)
                    {
                        var opened = MenuOf(id, opening: true);
                        Revise(id, opened, out bool changed);
                        if (changed) LayoutUpdated(id);
                    }
                    return dbus_message_new_method_return(call);
                }
                case "EventGroup":
                {
                    IntPtr reply = dbus_message_new_method_return(call);
                    byte* iter = stackalloc byte[IterSize], a = stackalloc byte[IterSize];
                    dbus_message_iter_init_append(reply, iter);
                    dbus_message_iter_open_container(iter, DBUS_TYPE_ARRAY, "i", a); dbus_message_iter_close_container(iter, a);
                    return reply;
                }
                case "Get" when iface == "org.freedesktop.DBus.Properties":
                case "GetAll" when iface == "org.freedesktop.DBus.Properties":
                    return MenuInterfaceProperties(call, member == "GetAll");
            }
            return dbus_message_new_error(call, "org.freedesktop.DBus.Error.UnknownMethod", member);
        }

        /// <summary>com.canonical.dbusmenu's own properties: Version 3, TextDirection, Status, IconThemePath.</summary>
        private static IntPtr MenuInterfaceProperties(IntPtr call, bool all)
        {
            string? only = null;
            if (!all)
            {
                byte* args = stackalloc byte[IterSize];
                if (dbus_message_iter_init(call, args) != 0 && dbus_message_iter_next(args) != 0 && dbus_message_iter_get_arg_type(args) == DBUS_TYPE_STRING)
                {
                    IntPtr s;
                    dbus_message_iter_get_basic(args, &s);
                    only = Marshal.PtrToStringUTF8(s);
                }
            }
            IntPtr reply = dbus_message_new_method_return(call);
            byte* iter = stackalloc byte[IterSize], array = stackalloc byte[IterSize], entry = stackalloc byte[IterSize], v = stackalloc byte[IterSize], a = stackalloc byte[IterSize];
            dbus_message_iter_init_append(reply, iter);
            void One(byte* into, string name)
            {
                string sig = name switch { "Version" => "u", "IconThemePath" => "as", _ => "s" };
                dbus_message_iter_open_container(into, DBUS_TYPE_VARIANT, sig, v);
                if (name == "Version") { uint ver = 3; dbus_message_iter_append_basic(v, 'u', &ver); }
                else if (name == "IconThemePath") { dbus_message_iter_open_container(v, DBUS_TYPE_ARRAY, "s", a); dbus_message_iter_close_container(v, a); }
                else AppendString(v, name == "TextDirection" ? "ltr" : "normal");
                dbus_message_iter_close_container(into, v);
            }
            if (!all) { One(iter, only ?? "Version"); return reply; }
            dbus_message_iter_open_container(iter, DBUS_TYPE_ARRAY, "{sv}", array);
            foreach (string name in new[] { "Version", "TextDirection", "Status", "IconThemePath" })
            {
                dbus_message_iter_open_container(array, DBUS_TYPE_DICT_ENTRY, null, entry);
                AppendString(entry, name);
                One(entry, name);
                dbus_message_iter_close_container(array, entry);
            }
            dbus_message_iter_close_container(iter, array);
            return reply;
        }

        /// <summary>(ia{sv}av): item <paramref name="node"/> (0 the menu) with its children, each a variant of the same.</summary>
        private static void AppendMenuNode(byte* iter, (int[] Parents, string[] Labels, int[] Flags) m, int node)
        {
            byte* st = stackalloc byte[IterSize], children = stackalloc byte[IterSize], v = stackalloc byte[IterSize];
            dbus_message_iter_open_container(iter, DBUS_TYPE_STRUCT, null, st);
            int id = node;
            dbus_message_iter_append_basic(st, DBUS_TYPE_INT32, &id);
            AppendMenuProperties(st, m, node);
            dbus_message_iter_open_container(st, DBUS_TYPE_ARRAY, "v", children);
            for (int i = 0; i < m.Parents.Length; i++)
            {
                if (m.Parents[i] != node) continue;
                dbus_message_iter_open_container(children, DBUS_TYPE_VARIANT, "(ia{sv}av)", v);
                AppendMenuNode(v, m, i + 1);
                dbus_message_iter_close_container(children, v);
            }
            dbus_message_iter_close_container(st, children);
            dbus_message_iter_close_container(iter, st);
        }

        private static void AppendMenuProperties(byte* iter, (int[] Parents, string[] Labels, int[] Flags) m, int node)
        {
            byte* array = stackalloc byte[IterSize], entry = stackalloc byte[IterSize], v = stackalloc byte[IterSize];
            dbus_message_iter_open_container(iter, DBUS_TYPE_ARRAY, "{sv}", array);
            void Entry(string key, string sig)
            {
                dbus_message_iter_open_container(array, DBUS_TYPE_DICT_ENTRY, null, entry);
                AppendString(entry, key);
                dbus_message_iter_open_container(entry, DBUS_TYPE_VARIANT, sig, v);
            }
            void End() { dbus_message_iter_close_container(entry, v); dbus_message_iter_close_container(array, entry); }
            bool hasChildren = Array.IndexOf(m.Parents, node) >= 0;
            if (node == 0 || hasChildren) { Entry("children-display", "s"); AppendString(v, "submenu"); End(); }
            if (node > 0)
            {
                int f = m.Flags[node - 1];
                if ((f & 4) != 0) { Entry("type", "s"); AppendString(v, "separator"); End(); }
                else
                {
                    // WinForms' mnemonic (&File) is GTK's (_File); a literal underscore is doubled, && a literal ampersand
                    string label = m.Labels[node - 1].Replace("_", "__").Replace("&&", "\u0001").Replace("&", "_").Replace("\u0001", "&");
                    Entry("label", "s"); AppendString(v, label); End();
                    if ((f & 8) != 0)
                    {
                        Entry("toggle-type", "s"); AppendString(v, "checkmark"); End();
                        Entry("toggle-state", "i"); int state = (f & 16) != 0 ? 1 : 0; dbus_message_iter_append_basic(v, DBUS_TYPE_INT32, &state); End();
                    }
                }
                if ((f & 1) == 0) { Entry("enabled", "b"); int no = 0; dbus_message_iter_append_basic(v, DBUS_TYPE_BOOLEAN, &no); End(); }
                if ((f & 2) == 0) { Entry("visible", "b"); int no = 0; dbus_message_iter_append_basic(v, DBUS_TYPE_BOOLEAN, &no); End(); }
            }
            dbus_message_iter_close_container(iter, array);
        }

        private static void LayoutUpdated(int id)
        {
            IntPtr signal = dbus_message_new_signal(PathOf(id) + "/Menu", "com.canonical.dbusmenu", "LayoutUpdated");
            if (signal == IntPtr.Zero) return;
            byte* iter = stackalloc byte[IterSize];
            dbus_message_iter_init_append(signal, iter);
            uint revision = s_menuRevisions.TryGetValue(id, out (uint Revision, string Key) r) ? r.Revision : 0;
            int parent = 0;
            dbus_message_iter_append_basic(iter, 'u', &revision);
            dbus_message_iter_append_basic(iter, DBUS_TYPE_INT32, &parent);
            uint serial;
            dbus_connection_send(s_bus, signal, &serial);
            dbus_message_unref(signal);
        }

        private const string MenuIntrospection =
            "<node><interface name=\"com.canonical.dbusmenu\">" +
            "<property name=\"Version\" type=\"u\" access=\"read\"/><property name=\"TextDirection\" type=\"s\" access=\"read\"/>" +
            "<property name=\"Status\" type=\"s\" access=\"read\"/><property name=\"IconThemePath\" type=\"as\" access=\"read\"/>" +
            "<method name=\"GetLayout\"><arg type=\"i\" name=\"parentId\" direction=\"in\"/><arg type=\"i\" name=\"recursionDepth\" direction=\"in\"/>" +
            "<arg type=\"as\" name=\"propertyNames\" direction=\"in\"/><arg type=\"u\" name=\"revision\" direction=\"out\"/><arg type=\"(ia{sv}av)\" name=\"layout\" direction=\"out\"/></method>" +
            "<method name=\"GetGroupProperties\"><arg type=\"ai\" name=\"ids\" direction=\"in\"/><arg type=\"as\" name=\"propertyNames\" direction=\"in\"/><arg type=\"a(ia{sv})\" name=\"properties\" direction=\"out\"/></method>" +
            "<method name=\"Event\"><arg type=\"i\" name=\"id\" direction=\"in\"/><arg type=\"s\" name=\"eventId\" direction=\"in\"/><arg type=\"v\" name=\"data\" direction=\"in\"/><arg type=\"u\" name=\"timestamp\" direction=\"in\"/></method>" +
            "<method name=\"EventGroup\"><arg type=\"a(isvu)\" name=\"events\" direction=\"in\"/><arg type=\"ai\" name=\"idErrors\" direction=\"out\"/></method>" +
            "<method name=\"AboutToShow\"><arg type=\"i\" name=\"id\" direction=\"in\"/><arg type=\"b\" name=\"needUpdate\" direction=\"out\"/></method>" +
            "<method name=\"AboutToShowGroup\"><arg type=\"ai\" name=\"ids\" direction=\"in\"/><arg type=\"ai\" name=\"updatesNeeded\" direction=\"out\"/><arg type=\"ai\" name=\"idErrors\" direction=\"out\"/></method>" +
            "<signal name=\"LayoutUpdated\"><arg type=\"u\" name=\"revision\"/><arg type=\"i\" name=\"parent\"/></signal>" +
            "<signal name=\"ItemsPropertiesUpdated\"><arg type=\"a(ia{sv})\" name=\"updatedProps\"/><arg type=\"a(ias)\" name=\"removedProps\"/></signal>" +
            "</interface></node>";

        private static void AppendString(byte* iter, string value)
        {
            IntPtr p = Marshal.StringToCoTaskMemUTF8(value);
            try { dbus_message_iter_append_basic(iter, DBUS_TYPE_STRING, &p); }
            finally { Marshal.FreeCoTaskMem(p); }
        }

        private static IntPtr StringReply(IntPtr call, string value)
        {
            IntPtr reply = dbus_message_new_method_return(call);
            byte* iter = stackalloc byte[IterSize];
            dbus_message_iter_init_append(reply, iter);
            AppendString(iter, value);
            return reply;
        }

        private static string AppId =>
            System.Reflection.Assembly.GetEntryAssembly()?.GetName().Name ?? AppDomain.CurrentDomain.FriendlyName;

        private const string Introspection =
            "<node><interface name=\"org.kde.StatusNotifierItem\">" +
            "<property name=\"Category\" type=\"s\" access=\"read\"/><property name=\"Id\" type=\"s\" access=\"read\"/>" +
            "<property name=\"Title\" type=\"s\" access=\"read\"/><property name=\"Status\" type=\"s\" access=\"read\"/>" +
            "<property name=\"WindowId\" type=\"i\" access=\"read\"/><property name=\"IconName\" type=\"s\" access=\"read\"/>" +
            "<property name=\"IconPixmap\" type=\"a(iiay)\" access=\"read\"/><property name=\"ToolTip\" type=\"(sa(iiay)ss)\" access=\"read\"/>" +
            "<property name=\"ItemIsMenu\" type=\"b\" access=\"read\"/><property name=\"Menu\" type=\"o\" access=\"read\"/>" +
            "<method name=\"Activate\"><arg name=\"x\" type=\"i\" direction=\"in\"/><arg name=\"y\" type=\"i\" direction=\"in\"/></method>" +
            "<method name=\"SecondaryActivate\"><arg name=\"x\" type=\"i\" direction=\"in\"/><arg name=\"y\" type=\"i\" direction=\"in\"/></method>" +
            "<method name=\"ContextMenu\"><arg name=\"x\" type=\"i\" direction=\"in\"/><arg name=\"y\" type=\"i\" direction=\"in\"/></method>" +
            "<method name=\"Scroll\"><arg name=\"delta\" type=\"i\" direction=\"in\"/><arg name=\"orientation\" type=\"s\" direction=\"in\"/></method>" +
            "<signal name=\"NewTitle\"/><signal name=\"NewIcon\"/><signal name=\"NewToolTip\"/>" +
            "<signal name=\"NewStatus\"><arg name=\"status\" type=\"s\"/></signal>" +
            "</interface><interface name=\"org.freedesktop.DBus.Properties\">" +
            "<method name=\"Get\"><arg type=\"s\" direction=\"in\"/><arg type=\"s\" direction=\"in\"/><arg type=\"v\" direction=\"out\"/></method>" +
            "<method name=\"GetAll\"><arg type=\"s\" direction=\"in\"/><arg type=\"a{sv}\" direction=\"out\"/></method>" +
            "</interface><interface name=\"org.freedesktop.DBus.Introspectable\">" +
            "<method name=\"Introspect\"><arg type=\"s\" direction=\"out\"/></method></interface></node>";

        private static void Trace(string s) => WaylandDisplay.LogSink?.Invoke("tray: " + s);
    }
}
