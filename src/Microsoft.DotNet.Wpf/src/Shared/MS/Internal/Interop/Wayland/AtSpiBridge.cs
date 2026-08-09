// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// The Linux accessibility backend: WPF's automation tree, exported over AT-SPI2.
//
// This is the odd one out among the six. Every other platform hands the app a protocol to IMPLEMENT
// -- answer these selectors, fill in this node info. Linux hands it a D-Bus service to RUN: the
// application becomes a server, on a bus of its own, exporting an object per accessible node, and
// Orca calls in.
//
// Three consequences shape the whole file:
//
//   * A SECOND BUS. The accessibility bus is not the session bus; its address comes from asking
//     org.a11y.Bus.GetAddress on the session bus. Objects exported on the wrong one are invisible.
//   * ONE object-path HANDLER, not one object per node. A tree of a few thousand nodes would mean a
//     few thousand registered paths; instead a fallback handler owns the whole subtree and reads the
//     node id off the end of the path (/org/a11y/atspi/accessible/42).
//   * PROPERTIES, not just methods. AT-SPI clients read Name, Description, Parent and ChildCount
//     through org.freedesktop.DBus.Properties. An implementation that only offers methods looks like
//     an empty tree, which is a confusing way to fail.
//
// What is deliberately approximate: under Wayland a toplevel never learns its position on screen
// (WaylandWindow says so at length), so screen coordinates here are the head's synthesised virtual
// space. Reading order, focus tracking and navigation are all correct; flat-review against physical
// screen coordinates is not. GTK4 on Wayland has the same limitation.
//

using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace MS.Internal.Interop.Wayland
{
    [SupportedOSPlatform("linux")]
    internal static unsafe class AtSpiBridge
    {
        private const string AccessiblePrefix = "/org/a11y/atspi/accessible";
        private const string RootPath = AccessiblePrefix + "/root";

        private const string IfaceAccessible = "org.a11y.atspi.Accessible";
        private const string IfaceComponent = "org.a11y.atspi.Component";
        private const string IfaceAction = "org.a11y.atspi.Action";
        private const string IfaceValue = "org.a11y.atspi.Value";
        private const string IfaceApplication = "org.a11y.atspi.Application";
        private const string IfaceProperties = "org.freedesktop.DBus.Properties";
        private const string IfaceIntrospectable = "org.freedesktop.DBus.Introspectable";

        private static IntPtr s_bus;
        private static string s_busName = string.Empty;
        private static IntPtr s_window;

        internal static bool IsAttached => s_bus != IntPtr.Zero;

        private static IAutomationTreeSource Tree => AutomationTree.Current;

        /// <summary>
        /// Connects to the accessibility bus and exports the tree, if a bus exists. No bus means no
        /// assistive technology is running, which is the common case and costs one method call to
        /// discover.
        /// </summary>
        internal static bool TryAttach(IntPtr window)
        {
            if (IsAttached) return true;
            if (s_tried) return false;
            s_tried = true;

            if (!DBusLite.IsAvailable) return false;
            if (!IsAccessibilityEnabled()) return false;

            string address = GetAccessibilityBusAddress();
            if (string.IsNullOrEmpty(address)) return false;

            IntPtr bus = DBusLite.OpenPrivate(address);
            if (bus == IntPtr.Zero) return false;

            s_bus = bus;
            s_window = window;
            s_busName = DBusLite.UniqueNameOf(bus) ?? string.Empty;

            s_messageHandler = HandleMessage;
            if (!DBusLite.RegisterFallback(bus, AccessiblePrefix,
                                           Marshal.GetFunctionPointerForDelegate(s_messageHandler)))
            {
                s_bus = IntPtr.Zero;
                return false;
            }

            AutomationTree.RequestActivation();
            AutomationTree.Changed += OnTreeChanged;

            Embed();
            return true;
        }

        // Held in a static field: libdbus keeps only the raw function pointer.
        private static MessageHandler s_messageHandler;

        // The probe is one-shot. A desktop does not turn accessibility on halfway through a process's
        // life without also restarting the a11y bus, and retrying per window would put a blocking
        // session-bus round trip on every window creation for the overwhelmingly common case of no
        // screen reader at all.
        private static bool s_tried;

        /// <summary>
        /// Whether the desktop has accessibility switched on. This is the gate GTK uses, and it
        /// matters: on GNOME the a11y bus is always running, so "a bus exists" would mean building
        /// and maintaining the peer tree for every user, screen reader or not.
        /// </summary>
        private static bool IsAccessibilityEnabled()
        {
            if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("AT_SPI_BUS_ADDRESS"))) return true;

            bool? enabled = DBusLite.CallReadBoolProperty("org.a11y.Bus", "/org/a11y/bus", "org.a11y.Status", "IsEnabled");
            if (enabled == true) return true;

            // Older stacks expose only the screen-reader flag.
            return DBusLite.CallReadBoolProperty("org.a11y.Bus", "/org/a11y/bus", "org.a11y.Status", "ScreenReaderEnabled") == true;
        }

        private delegate int MessageHandler(IntPtr connection, IntPtr message, IntPtr userData);

        /// <summary>
        /// Asks the session bus where the accessibility bus lives. Returns empty when there is no
        /// a11y bus, which is how "no screen reader" presents itself.
        /// </summary>
        private static string GetAccessibilityBusAddress()
        {
            // An explicit address wins, as it does for every other AT-SPI client.
            string fromEnv = Environment.GetEnvironmentVariable("AT_SPI_BUS_ADDRESS");
            if (!string.IsNullOrEmpty(fromEnv)) return fromEnv;

            return DBusLite.CallReadString("org.a11y.Bus", "/org/a11y/bus", "org.a11y.Bus", "GetAddress")
                   ?? string.Empty;
        }

        /// <summary>
        /// Announces this application to the AT-SPI registry, which answers with the desktop's own
        /// reference. Until Embed succeeds the tree exists but nothing is looking at it.
        /// </summary>
        private static void Embed()
        {
            DBusLite.CallEmbed(s_bus, s_busName, RootPath);
            DBusLite.Flush(s_bus);
        }

        /// <summary>
        /// Runs the bus. Called from the Wayland run loop each tick, beside the session-bus pump --
        /// an idle application still has to answer Orca, and an accessibility bus that is never
        /// dispatched makes the screen reader appear to hang on this app.
        /// </summary>
        internal static void Pump()
        {
            if (s_bus != IntPtr.Zero) DBusLite.Dispatch(s_bus, 0);
        }

        /// <summary>The bus file descriptor, so the run loop can poll it alongside Wayland's.</summary>
        internal static int Fd => s_bus == IntPtr.Zero ? -1 : DBusLite.FdOf(s_bus);

        // ---- message dispatch ------------------------------------------------------

        private static int HandleMessage(IntPtr connection, IntPtr message, IntPtr userData)
        {
            try
            {
                string path = DBusLite.PathOf(message) ?? string.Empty;
                string iface = DBusLite.InterfaceOf(message) ?? string.Empty;
                string member = DBusLite.MemberOf(message) ?? string.Empty;

                int node = NodeFromPath(path);

                IntPtr reply = iface switch
                {
                    IfaceIntrospectable => HandleIntrospect(message),
                    IfaceProperties => HandleProperties(message, member, node),
                    IfaceAccessible => HandleAccessible(message, member, node),
                    IfaceComponent => HandleComponent(message, member, node),
                    IfaceAction => HandleAction(message, member, node),
                    IfaceValue => HandleValue(message, member, node),
                    IfaceApplication => HandleApplication(message, member),
                    _ => IntPtr.Zero,
                };

                if (reply == IntPtr.Zero) return DBusLite.HANDLER_RESULT_NOT_YET_HANDLED;

                DBusLite.SendAndUnref(connection, reply);
                DBusLite.Flush(connection);
                return DBusLite.HANDLER_RESULT_HANDLED;
            }
            catch (Exception e)
            {
                // Never let a managed exception unwind into libdbus's dispatcher.
                Console.WriteLine($"WPF AT-SPI dispatch failed: {e}");
                return DBusLite.HANDLER_RESULT_NOT_YET_HANDLED;
            }
        }

        /// <summary>
        /// The node id encoded in an object path. The root path maps to the tree's root; anything
        /// else is the integer after the last slash. An unparseable path is not an error -- AT-SPI
        /// clients probe paths -- so it answers "no node".
        /// </summary>
        internal static int NodeFromPath(string path)
        {
            if (string.IsNullOrEmpty(path)) return IAutomationTreeSource.InvalidNode;
            if (path == RootPath) return Tree?.GetRootId(s_window) ?? IAutomationTreeSource.InvalidNode;

            int slash = path.LastIndexOf('/');
            if (slash < 0 || slash == path.Length - 1) return IAutomationTreeSource.InvalidNode;

            return int.TryParse(path.AsSpan(slash + 1), out int id) ? id : IAutomationTreeSource.InvalidNode;
        }

        /// <summary>The object path for a node. The inverse of <see cref="NodeFromPath"/>.</summary>
        internal static string PathForNode(int nodeId)
            => nodeId < 0 ? AccessiblePrefix + "/null" : AccessiblePrefix + "/" + nodeId.ToString();

        // ---- org.freedesktop.DBus.Properties ---------------------------------------

        private static IntPtr HandleProperties(IntPtr message, string member, int node)
        {
            IAutomationTreeSource tree = Tree;
            if (tree == null) return IntPtr.Zero;

            byte* args = stackalloc byte[DBusLite.IterSize];
            if (!DBusLite.IterInit(message, args)) return IntPtr.Zero;
            string iface = DBusLite.ReadString(args);

            switch (member)
            {
                case "Get":
                {
                    if (!DBusLite.IterNext(args)) return IntPtr.Zero;
                    string property = DBusLite.ReadString(args);

                    IntPtr reply = DBusLite.NewMethodReturn(message);
                    byte* iter = stackalloc byte[DBusLite.IterSize];
                    DBusLite.IterInitAppend(reply, iter);
                    AppendProperty(iter, tree, node, property);
                    return reply;
                }

                case "GetAll":
                {
                    // Accerciser and at-spi2's own caching read whole interfaces at once. Answering
                    // Get but not GetAll is the classic way to look like an empty tree.
                    IntPtr reply = DBusLite.NewMethodReturn(message);
                    byte* iter = stackalloc byte[DBusLite.IterSize];
                    DBusLite.IterInitAppend(reply, iter);

                    byte* dict = stackalloc byte[DBusLite.IterSize];
                    byte* entry = stackalloc byte[DBusLite.IterSize];   // reused per entry, not per iteration
                    DBusLite.OpenArray(iter, "{sv}", dict);
                    foreach (string property in PropertiesOf(iface))
                    {
                        DBusLite.OpenDictEntry(dict, entry);
                        DBusLite.AppendString(entry, DBusLite.DBUS_TYPE_STRING, property);
                        AppendProperty(entry, tree, node, property);
                        DBusLite.CloseContainer(dict, entry);
                    }
                    DBusLite.CloseContainer(iter, dict);
                    return reply;
                }

                case "Set":
                {
                    // The one writable property in scope: a slider or scrollbar's value. AT-SPI has
                    // no SetCurrentValue method -- setting the property IS the action.
                    if (!DBusLite.IterNext(args)) return IntPtr.Zero;
                    string property = DBusLite.ReadString(args);

                    if (property == "CurrentValue" && DBusLite.IterNext(args) &&
                        DBusLite.TryReadDoubleVariant(args, out double target))
                    {
                        tree.SetValue(node, target.ToString(System.Globalization.CultureInfo.InvariantCulture));
                    }

                    return DBusLite.NewMethodReturn(message);   // Set replies with an empty body
                }

                default:
                    return IntPtr.Zero;
            }
        }

        /// <summary>Writes one property's value, as a variant.</summary>
        private static void AppendProperty(byte* iter, IAutomationTreeSource tree, int node, string property)
        {
            switch (property)
            {
                case "Name":
                    DBusLite.AppendStringVariant(iter, tree.GetName(node));
                    break;
                case "Description":
                    DBusLite.AppendStringVariant(iter, tree.GetHelpText(node));
                    break;
                case "Parent":
                    int parent = tree.GetParentId(node);
                    DBusLite.AppendObjectRefVariant(iter, s_busName,
                        parent < 0 ? RootPath : PathForNode(parent));
                    break;
                case "ChildCount":
                    DBusLite.AppendInt32Variant(iter, tree.GetChildIds(node).Length);
                    break;
                case "Locale":
                    DBusLite.AppendStringVariant(iter, System.Globalization.CultureInfo.CurrentUICulture.Name);
                    break;
                case "AccessibleId":
                    DBusLite.AppendStringVariant(iter, tree.GetAutomationId(node));
                    break;
                case "ToolkitName":
                    DBusLite.AppendStringVariant(iter, "WPF");
                    break;
                case "Version":
                    DBusLite.AppendStringVariant(iter, "10.0");
                    break;
                case "AtspiVersion":
                    DBusLite.AppendStringVariant(iter, "2.1");
                    break;
                case "CurrentValue":
                    DBusLite.AppendDoubleVariant(iter, tree.TryGetRange(node, out double v, out _, out _) ? v : 0);
                    break;
                case "MinimumValue":
                    DBusLite.AppendDoubleVariant(iter, tree.TryGetRange(node, out _, out double lo, out _) ? lo : 0);
                    break;
                case "MaximumValue":
                    DBusLite.AppendDoubleVariant(iter, tree.TryGetRange(node, out _, out _, out double hi) ? hi : 0);
                    break;
                case "MinimumIncrement":
                    DBusLite.AppendDoubleVariant(iter, 0);
                    break;
                default:
                    DBusLite.AppendStringVariant(iter, string.Empty);
                    break;
            }
        }

        private static string[] PropertiesOf(string iface) => iface switch
        {
            IfaceValue => new[] { "MinimumValue", "MaximumValue", "MinimumIncrement", "CurrentValue" },
            IfaceApplication => new[] { "ToolkitName", "Version", "AtspiVersion" },
            _ => new[] { "Name", "Description", "Parent", "ChildCount", "Locale", "AccessibleId" },
        };

        /// <summary>
        /// Minimal introspection. Nothing in the AT-SPI protocol needs it, but every debugging tool
        /// does -- busctl tree, d-feet and Accerciser's inspector all walk Introspect, and a service
        /// that does not answer it is opaque exactly when something has gone wrong.
        /// </summary>
        private static IntPtr HandleIntrospect(IntPtr message)
        {
            IntPtr reply = DBusLite.NewMethodReturn(message);
            byte* iter = stackalloc byte[DBusLite.IterSize];
            DBusLite.IterInitAppend(reply, iter);
            DBusLite.AppendString(iter, DBusLite.DBUS_TYPE_STRING, IntrospectXml);
            return reply;
        }

        private const string IntrospectXml =
            "<!DOCTYPE node PUBLIC \"-//freedesktop//DTD D-BUS Object Introspection 1.0//EN\" " +
            "\"http://www.freedesktop.org/standards/dbus/1.0/introspect.dtd\">\n" +
            "<node>\n" +
            "  <interface name=\"org.freedesktop.DBus.Properties\">\n" +
            "    <method name=\"Get\"><arg type=\"s\" direction=\"in\"/><arg type=\"s\" direction=\"in\"/><arg type=\"v\" direction=\"out\"/></method>\n" +
            "    <method name=\"GetAll\"><arg type=\"s\" direction=\"in\"/><arg type=\"a{sv}\" direction=\"out\"/></method>\n" +
            "    <method name=\"Set\"><arg type=\"s\" direction=\"in\"/><arg type=\"s\" direction=\"in\"/><arg type=\"v\" direction=\"in\"/></method>\n" +
            "  </interface>\n" +
            "  <interface name=\"org.a11y.atspi.Accessible\">\n" +
            "    <property name=\"Name\" type=\"s\" access=\"read\"/>\n" +
            "    <property name=\"Description\" type=\"s\" access=\"read\"/>\n" +
            "    <property name=\"Parent\" type=\"(so)\" access=\"read\"/>\n" +
            "    <property name=\"ChildCount\" type=\"i\" access=\"read\"/>\n" +
            "    <method name=\"GetRole\"><arg type=\"u\" direction=\"out\"/></method>\n" +
            "    <method name=\"GetRoleName\"><arg type=\"s\" direction=\"out\"/></method>\n" +
            "    <method name=\"GetLocalizedRoleName\"><arg type=\"s\" direction=\"out\"/></method>\n" +
            "    <method name=\"GetState\"><arg type=\"au\" direction=\"out\"/></method>\n" +
            "    <method name=\"GetChildAtIndex\"><arg type=\"i\" direction=\"in\"/><arg type=\"(so)\" direction=\"out\"/></method>\n" +
            "    <method name=\"GetChildren\"><arg type=\"a(so)\" direction=\"out\"/></method>\n" +
            "    <method name=\"GetIndexInParent\"><arg type=\"i\" direction=\"out\"/></method>\n" +
            "    <method name=\"GetApplication\"><arg type=\"(so)\" direction=\"out\"/></method>\n" +
            "    <method name=\"GetAttributes\"><arg type=\"a{ss}\" direction=\"out\"/></method>\n" +
            "    <method name=\"GetRelationSet\"><arg type=\"a(ua(so))\" direction=\"out\"/></method>\n" +
            "  </interface>\n" +
            "  <interface name=\"org.a11y.atspi.Component\">\n" +
            "    <method name=\"GetExtents\"><arg type=\"u\" direction=\"in\"/><arg type=\"(iiii)\" direction=\"out\"/></method>\n" +
            "    <method name=\"GetPosition\"><arg type=\"u\" direction=\"in\"/><arg type=\"i\" direction=\"out\"/><arg type=\"i\" direction=\"out\"/></method>\n" +
            "    <method name=\"GetSize\"><arg type=\"i\" direction=\"out\"/><arg type=\"i\" direction=\"out\"/></method>\n" +
            "    <method name=\"GetLayer\"><arg type=\"u\" direction=\"out\"/></method>\n" +
            "    <method name=\"GetAccessibleAtPoint\"><arg type=\"i\" direction=\"in\"/><arg type=\"i\" direction=\"in\"/><arg type=\"u\" direction=\"in\"/><arg type=\"(so)\" direction=\"out\"/></method>\n" +
            "    <method name=\"GrabFocus\"><arg type=\"b\" direction=\"out\"/></method>\n" +
            "  </interface>\n" +
            "  <interface name=\"org.a11y.atspi.Action\">\n" +
            "    <method name=\"GetNActions\"><arg type=\"i\" direction=\"out\"/></method>\n" +
            "    <method name=\"GetName\"><arg type=\"i\" direction=\"in\"/><arg type=\"s\" direction=\"out\"/></method>\n" +
            "    <method name=\"GetDescription\"><arg type=\"i\" direction=\"in\"/><arg type=\"s\" direction=\"out\"/></method>\n" +
            "    <method name=\"DoAction\"><arg type=\"i\" direction=\"in\"/><arg type=\"b\" direction=\"out\"/></method>\n" +
            "  </interface>\n" +
            "  <interface name=\"org.a11y.atspi.Value\">\n" +
            "    <property name=\"MinimumValue\" type=\"d\" access=\"read\"/>\n" +
            "    <property name=\"MaximumValue\" type=\"d\" access=\"read\"/>\n" +
            "    <property name=\"CurrentValue\" type=\"d\" access=\"readwrite\"/>\n" +
            "  </interface>\n" +
            "</node>\n";

        // ---- org.a11y.atspi.Accessible ---------------------------------------------

        private static IntPtr HandleAccessible(IntPtr message, string member, int node)
        {
            IAutomationTreeSource tree = Tree;
            if (tree == null) return IntPtr.Zero;

            IntPtr reply;
            byte* iter = stackalloc byte[DBusLite.IterSize];

            switch (member)
            {
                case "GetRole":
                    reply = DBusLite.NewMethodReturn(message);
                    DBusLite.IterInitAppend(reply, iter);
                    DBusLite.AppendUInt32(iter, AtSpiRoles.RoleFor(tree.GetRole(node)));
                    return reply;

                case "GetRoleName":
                case "GetLocalizedRoleName":
                    reply = DBusLite.NewMethodReturn(message);
                    DBusLite.IterInitAppend(reply, iter);
                    DBusLite.AppendString(iter, DBusLite.DBUS_TYPE_STRING,
                                          AtSpiRoles.RoleNameFor(tree.GetRole(node)));
                    return reply;

                case "GetState":
                    reply = DBusLite.NewMethodReturn(message);
                    DBusLite.IterInitAppend(reply, iter);
                    DBusLite.AppendStateSet(iter, AtSpiRoles.StatesFor(tree.GetState(node)));
                    return reply;

                case "GetChildAtIndex":
                {
                    byte* args = stackalloc byte[DBusLite.IterSize];
                    int index = DBusLite.IterInit(message, args) ? DBusLite.ReadInt32Or(args, -1) : -1;
                    int[] children = tree.GetChildIds(node);

                    reply = DBusLite.NewMethodReturn(message);
                    DBusLite.IterInitAppend(reply, iter);
                    DBusLite.AppendObjectRef(iter, s_busName,
                        index >= 0 && index < children.Length ? PathForNode(children[index]) : PathForNode(-1));
                    return reply;
                }

                case "GetChildren":
                {
                    reply = DBusLite.NewMethodReturn(message);
                    DBusLite.IterInitAppend(reply, iter);

                    byte* array = stackalloc byte[DBusLite.IterSize];
                    DBusLite.OpenArray(iter, "(so)", array);
                    foreach (int child in tree.GetChildIds(node))
                    {
                        DBusLite.AppendObjectRef(array, s_busName, PathForNode(child));
                    }
                    DBusLite.CloseContainer(iter, array);
                    return reply;
                }

                case "GetIndexInParent":
                {
                    int parent = tree.GetParentId(node);
                    int index = -1;
                    if (parent >= 0)
                    {
                        int[] siblings = tree.GetChildIds(parent);
                        for (int i = 0; i < siblings.Length; i++)
                        {
                            if (siblings[i] == node) { index = i; break; }
                        }
                    }

                    reply = DBusLite.NewMethodReturn(message);
                    DBusLite.IterInitAppend(reply, iter);
                    DBusLite.AppendInt32(iter, index);
                    return reply;
                }

                case "GetApplication":
                    reply = DBusLite.NewMethodReturn(message);
                    DBusLite.IterInitAppend(reply, iter);
                    DBusLite.AppendObjectRef(iter, s_busName, RootPath);
                    return reply;

                case "GetAttributes":
                    reply = DBusLite.NewMethodReturn(message);
                    DBusLite.IterInitAppend(reply, iter);
                    DBusLite.OpenAndCloseEmptyArray(iter, "{ss}");
                    return reply;

                case "GetRelationSet":
                    reply = DBusLite.NewMethodReturn(message);
                    DBusLite.IterInitAppend(reply, iter);
                    DBusLite.OpenAndCloseEmptyArray(iter, "(ua(so))");
                    return reply;

                default:
                    return IntPtr.Zero;
            }
        }

        // ---- org.a11y.atspi.Component ----------------------------------------------

        private static IntPtr HandleComponent(IntPtr message, string member, int node)
        {
            IAutomationTreeSource tree = Tree;
            if (tree == null) return IntPtr.Zero;

            byte* iter = stackalloc byte[DBusLite.IterSize];
            tree.TryGetBounds(node, out double x, out double y, out double w, out double h);

            switch (member)
            {
                case "GetExtents":
                {
                    IntPtr reply = DBusLite.NewMethodReturn(message);
                    DBusLite.IterInitAppend(reply, iter);

                    byte* rect = stackalloc byte[DBusLite.IterSize];
                    DBusLite.OpenStruct(iter, rect);
                    DBusLite.AppendInt32(rect, (int)x);
                    DBusLite.AppendInt32(rect, (int)y);
                    DBusLite.AppendInt32(rect, (int)w);
                    DBusLite.AppendInt32(rect, (int)h);
                    DBusLite.CloseContainer(iter, rect);
                    return reply;
                }

                case "GetPosition":
                {
                    IntPtr reply = DBusLite.NewMethodReturn(message);
                    DBusLite.IterInitAppend(reply, iter);
                    DBusLite.AppendInt32(iter, (int)x);
                    DBusLite.AppendInt32(iter, (int)y);
                    return reply;
                }

                case "GetSize":
                {
                    IntPtr reply = DBusLite.NewMethodReturn(message);
                    DBusLite.IterInitAppend(reply, iter);
                    DBusLite.AppendInt32(iter, (int)w);
                    DBusLite.AppendInt32(iter, (int)h);
                    return reply;
                }

                case "GetLayer":
                {
                    IntPtr reply = DBusLite.NewMethodReturn(message);
                    DBusLite.IterInitAppend(reply, iter);
                    DBusLite.AppendUInt32(iter, 3);   // ATSPI_LAYER_WIDGET
                    return reply;
                }

                case "GetAccessibleAtPoint":
                {
                    // What flat review and a mouse-over cursor both go through: a point, and the
                    // deepest node under it. The trailing coordinate type is ignored -- under
                    // Wayland window-relative and screen coordinates are the same synthesised
                    // space, for the reason this file's header gives.
                    byte* args = stackalloc byte[DBusLite.IterSize];
                    int px = 0, py = 0;
                    if (DBusLite.IterInit(message, args))
                    {
                        px = DBusLite.ReadInt32Or(args, 0);
                        if (DBusLite.IterNext(args)) py = DBusLite.ReadInt32Or(args, 0);
                    }

                    int hit = tree.HitTest(s_window, px, py);

                    IntPtr reply = DBusLite.NewMethodReturn(message);
                    DBusLite.IterInitAppend(reply, iter);
                    DBusLite.AppendObjectRef(iter, s_busName, PathForNode(hit));
                    return reply;
                }

                case "GrabFocus":
                {
                    bool ok = tree.SetFocus(node);
                    IntPtr reply = DBusLite.NewMethodReturn(message);
                    DBusLite.IterInitAppend(reply, iter);
                    DBusLite.AppendBool(iter, ok);
                    return reply;
                }

                default:
                    return IntPtr.Zero;
            }
        }

        // ---- org.a11y.atspi.Action -------------------------------------------------

        private static IntPtr HandleAction(IntPtr message, string member, int node)
        {
            IAutomationTreeSource tree = Tree;
            if (tree == null) return IntPtr.Zero;

            byte* iter = stackalloc byte[DBusLite.IterSize];
            AccessibleAction[] available = AvailableActions(tree, node);

            switch (member)
            {
                case "GetNActions":
                {
                    IntPtr reply = DBusLite.NewMethodReturn(message);
                    DBusLite.IterInitAppend(reply, iter);
                    DBusLite.AppendInt32(iter, available.Length);
                    return reply;
                }

                case "GetName":
                case "GetDescription":
                {
                    byte* args = stackalloc byte[DBusLite.IterSize];
                    int index = DBusLite.IterInit(message, args) ? DBusLite.ReadInt32Or(args, -1) : -1;

                    IntPtr reply = DBusLite.NewMethodReturn(message);
                    DBusLite.IterInitAppend(reply, iter);
                    DBusLite.AppendString(iter, DBusLite.DBUS_TYPE_STRING,
                        index >= 0 && index < available.Length ? NameOf(available[index]) : string.Empty);
                    return reply;
                }

                case "DoAction":
                {
                    byte* args = stackalloc byte[DBusLite.IterSize];
                    int index = DBusLite.IterInit(message, args) ? DBusLite.ReadInt32Or(args, -1) : -1;
                    bool ok = index >= 0 && index < available.Length && tree.DoAction(node, available[index]);

                    IntPtr reply = DBusLite.NewMethodReturn(message);
                    DBusLite.IterInitAppend(reply, iter);
                    DBusLite.AppendBool(iter, ok);
                    return reply;
                }

                default:
                    return IntPtr.Zero;
            }
        }

        /// <summary>
        /// The actions a node offers, in a stable order -- AT-SPI addresses them BY INDEX, so the
        /// order has to be a function of the node's capabilities and nothing else.
        /// </summary>
        internal static AccessibleAction[] AvailableActions(IAutomationTreeSource tree, int node)
        {
            Span<AccessibleAction> candidates = stackalloc AccessibleAction[]
            {
                AccessibleAction.Invoke, AccessibleAction.Toggle,
                AccessibleAction.Expand, AccessibleAction.Collapse, AccessibleAction.Select,
            };

            int count = 0;
            Span<AccessibleAction> kept = stackalloc AccessibleAction[5];
            foreach (AccessibleAction candidate in candidates)
            {
                if (tree.SupportsAction(node, candidate)) kept[count++] = candidate;
            }

            var result = new AccessibleAction[count];
            for (int i = 0; i < count; i++) result[i] = kept[i];
            return result;
        }

        internal static string NameOf(AccessibleAction action) => action switch
        {
            AccessibleAction.Invoke => "click",
            AccessibleAction.Toggle => "toggle",
            AccessibleAction.Expand => "expand",
            AccessibleAction.Collapse => "collapse",
            AccessibleAction.Select => "select",
            _ => string.Empty,
        };

        // ---- org.a11y.atspi.Value / Application ------------------------------------

        private static IntPtr HandleValue(IntPtr message, string member, int node)
        {
            _ = member;
            _ = node;
            // The Value interface is read and written entirely through Properties, which
            // HandleProperties already answers (CurrentValue / MinimumValue / MaximumValue).
            return IntPtr.Zero;
        }

        private static IntPtr HandleApplication(IntPtr message, string member)
        {
            byte* iter = stackalloc byte[DBusLite.IterSize];

            switch (member)
            {
                case "GetLocale":
                {
                    IntPtr reply = DBusLite.NewMethodReturn(message);
                    DBusLite.IterInitAppend(reply, iter);
                    DBusLite.AppendString(iter, DBusLite.DBUS_TYPE_STRING,
                                          System.Globalization.CultureInfo.CurrentUICulture.Name);
                    return reply;
                }
                default:
                    return IntPtr.Zero;
            }
        }

        // ---- events out ------------------------------------------------------------

        private static void OnTreeChanged(AutomationChange change)
        {
            if (s_bus == IntPtr.Zero) return;

            try
            {
                string path = PathForNode(change.NodeId);

                switch (change.Kind)
                {
                    case AutomationChangeKind.FocusChanged:
                        EmitStateChanged(path, "focused", 1);
                        break;
                    case AutomationChangeKind.SelectionChanged:
                        EmitStateChanged(path, "selected", 1);
                        break;
                    case AutomationChangeKind.ValueChanged:
                        EmitPropertyChange(path, "accessible-value");
                        break;
                    case AutomationChangeKind.PropertyChanged:
                        EmitPropertyChange(path, "accessible-name");
                        break;
                    case AutomationChangeKind.ChildrenChanged:
                        EmitPropertyChange(path, "children-changed");
                        break;
                }

                DBusLite.Flush(s_bus);
            }
            catch (Exception e)
            {
                Console.WriteLine($"WPF AT-SPI notify failed: {e}");
            }
        }

        private static void EmitStateChanged(string path, string state, int enabled)
            => DBusLite.EmitAtSpiEvent(s_bus, s_busName, path, "org.a11y.atspi.Event.Object",
                                       "StateChanged", state, enabled, 0);

        private static void EmitPropertyChange(string path, string what)
            => DBusLite.EmitAtSpiEvent(s_bus, s_busName, path, "org.a11y.atspi.Event.Object",
                                       "PropertyChange", what, 0, 0);
    }
}
