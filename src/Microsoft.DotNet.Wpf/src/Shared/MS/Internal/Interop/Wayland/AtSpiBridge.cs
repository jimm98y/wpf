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
// The root object is the APPLICATION, as AT-SPI requires and GTK and Qt export it: its children are
// the process's toplevel windows, each the root of its own WPF (or WinForms) tree, so a second window
// -- a dialog, a tool window -- is as reachable as the first, and Orca hears which one is active.
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
        private const string IfaceText = "org.a11y.atspi.Text";
        private const string IfaceEditableText = "org.a11y.atspi.EditableText";
        private const string IfaceSelection = "org.a11y.atspi.Selection";
        private const string IfaceTable = "org.a11y.atspi.Table";
        private const string IfaceTableCell = "org.a11y.atspi.TableCell";
        private const string IfaceProperties = "org.freedesktop.DBus.Properties";
        private const string IfaceIntrospectable = "org.freedesktop.DBus.Introspectable";

        private static IntPtr s_bus;
        private static string s_busName = string.Empty;

        // The toplevels exported under the application, in the order they appeared, and the active one.
        private static readonly System.Collections.Generic.List<IntPtr> s_windows = new();
        private static IntPtr s_active;

        /// <summary>The application object's node: below every node id the tree hands out, and not
        /// <see cref="IAutomationTreeSource.InvalidNode"/>.</summary>
        private const int AppNode = -2;

        internal static bool IsAttached => s_bus != IntPtr.Zero;

        private static IAutomationTreeSource Tree => AutomationTree.Current;

        /// <summary>
        /// WPF_A11Y_LOG=1: trace the attach handshake and every message answered.
        ///
        /// Worth a switch of its own rather than a debugger session, because the failure mode of an
        /// AT-SPI service is silence -- a client times out and reports nothing, so from the outside
        /// "never attached", "attached but never dispatched" and "answered with the wrong shape" all
        /// look identical.
        /// </summary>
        private static readonly bool s_log = Environment.GetEnvironmentVariable("WPF_A11Y_LOG") == "1";

        private static void Trace(string message)
        {
            if (s_log) Console.Error.WriteLine($"[a11y] {message}");
        }

        /// <summary>
        /// Connects to the accessibility bus and exports the tree, if a bus exists. No bus means no
        /// assistive technology is running, which is the common case and costs one method call to
        /// discover.
        /// </summary>
        internal static bool TryAttach(IntPtr window)
        {
            if (IsAttached) { AddWindow(window); return true; }
            if (s_tried) return false;
            s_tried = true;

            if (!DBusLite.IsAvailable) { Trace("no session bus"); return false; }
            if (!IsAccessibilityEnabled()) { Trace("accessibility is off"); return false; }

            string address = GetAccessibilityBusAddress();
            if (string.IsNullOrEmpty(address)) { Trace("no a11y bus address"); return false; }

            IntPtr bus = DBusLite.OpenPrivate(address);
            if (bus == IntPtr.Zero) { Trace($"could not open {address}"); return false; }

            s_bus = bus;
            s_busName = DBusLite.UniqueNameOf(bus) ?? string.Empty;

            s_messageHandler = HandleMessage;
            if (!DBusLite.RegisterFallback(bus, AccessiblePrefix,
                                           Marshal.GetFunctionPointerForDelegate(s_messageHandler)))
            {
                Trace("register_fallback failed");
                s_bus = IntPtr.Zero;
                return false;
            }

            Trace($"attached as {s_busName} on {address} fd={Fd} window=0x{window.ToInt64():x}");

            AutomationTree.RequestActivation();
            AutomationTree.Changed += OnTreeChanged;

            Embed();
            AddWindow(window);
            return true;
        }

        // ---- the application's windows ---------------------------------------------

        private static void AddWindow(IntPtr window)
        {
            if (window == IntPtr.Zero || s_windows.Contains(window)) return;
            s_windows.Add(window);
            int root = Tree?.GetRootId(window) ?? IAutomationTreeSource.InvalidNode;
            if (root >= 0) EmitChildrenChanged(RootPath, "add", s_windows.Count - 1, PathForNode(root));
            DBusLite.Flush(s_bus);
        }

        /// <summary>A toplevel going away: the application's child removed, before its surface is.</summary>
        internal static void Detach(IntPtr window)
        {
            int index = s_windows.IndexOf(window);
            if (index < 0) return;
            int root = Tree?.GetRootId(window) ?? IAutomationTreeSource.InvalidNode;
            s_windows.RemoveAt(index);
            if (s_active == window) s_active = IntPtr.Zero;
            if (s_bus == IntPtr.Zero) return;
            if (root >= 0) EmitChildrenChanged(RootPath, "remove", index, PathForNode(root));
            DBusLite.Flush(s_bus);
        }

        /// <summary>
        /// The compositor made a toplevel the active window, or took that away: AT-SPI's window
        /// Activate/Deactivate and the frame's ACTIVE state, which is how Orca knows to announce the
        /// window the user switched to.
        /// </summary>
        internal static void WindowActivated(IntPtr window, bool active)
        {
            if (active) s_active = window;
            else if (s_active == window) s_active = IntPtr.Zero;
            if (s_bus == IntPtr.Zero || !s_windows.Contains(window)) return;
            int root = Tree?.GetRootId(window) ?? IAutomationTreeSource.InvalidNode;
            if (root < 0) return;
            string path = PathForNode(root);
            EmitStateChanged(path, "active", active ? 1 : 0);
            Trace($"event Window.{(active ? "Activate" : "Deactivate")} on {path}");
            DBusLite.EmitAtSpiEvent(s_bus, s_busName, path, IfaceEventWindow, active ? "Activate" : "Deactivate",
                                    string.Empty, 0, 0, Tree.GetName(root));
            DBusLite.Flush(s_bus);
        }

        private const string IfaceEventWindow = "org.a11y.atspi.Event.Window";

        /// <summary>The windows' tree roots, the application's children.</summary>
        private static int[] WindowRoots()
        {
            IAutomationTreeSource tree = Tree;
            if (tree == null) return Array.Empty<int>();
            var roots = new System.Collections.Generic.List<int>(s_windows.Count);
            foreach (IntPtr window in s_windows)
            {
                int root = tree.GetRootId(window);
                if (root >= 0) roots.Add(root);
            }
            return roots.ToArray();
        }

        /// <summary>The toplevel a node is in: the window whose root its parent chain ends at.</summary>
        private static IntPtr WindowOf(IAutomationTreeSource tree, int node)
        {
            for (int depth = 0; node >= 0 && depth < 256; depth++)
            {
                int parent = tree.GetParentId(node);
                if (parent < 0) break;
                node = parent;
            }
            foreach (IntPtr window in s_windows)
                if (tree.GetRootId(window) == node) return window;
            return s_active != IntPtr.Zero ? s_active : s_windows.Count > 0 ? s_windows[0] : IntPtr.Zero;
        }

        private static string ApplicationName =>
            System.Reflection.Assembly.GetEntryAssembly()?.GetName().Name ?? AppDomain.CurrentDomain.FriendlyName;

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
            if (s_bus != IntPtr.Zero) DBusLite.DrainDispatch(s_bus);
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
                    IfaceIntrospectable => HandleIntrospect(message, node),
                    IfaceProperties => HandleProperties(message, member, node),
                    IfaceAccessible => HandleAccessible(message, member, node),
                    IfaceComponent => HandleComponent(message, member, node),
                    IfaceAction => HandleAction(message, member, node),
                    IfaceValue => HandleValue(message, member, node),
                    IfaceApplication => HandleApplication(message, member),
                    IfaceText => HandleText(message, member, node),
                    IfaceEditableText => HandleEditableText(message, member, node),
                    IfaceSelection => HandleSelection(message, member, node),
                    IfaceTable => HandleTable(message, member, node),
                    IfaceTableCell => HandleTableCell(message, member, node),
                    _ => IntPtr.Zero,
                };

                if (reply == IntPtr.Zero)
                {
                    Trace($"unhandled {iface}.{member} on {path}");
                    return DBusLite.HANDLER_RESULT_NOT_YET_HANDLED;
                }

                Trace($"{iface}.{member} node={node}");
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
            if (path == RootPath) return AppNode;

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
            if (node == AppNode) { AppendApplicationProperty(iter, property); return; }
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
                case "NActions":
                    DBusLite.AppendInt32Variant(iter, AvailableActions(tree, node).Length);
                    break;

                // Text
                case "CharacterCount":
                    DBusLite.AppendInt32Variant(iter, AtSpiText.CharacterCount(TextOf(tree, node)));
                    break;
                case "CaretOffset":
                {
                    string text = TextOf(tree, node);
                    DBusLite.AppendInt32Variant(iter, text == null ? 0 : AtSpiText.ToCodePoints(text, SelectionOf(tree, node, text).Caret));
                    break;
                }

                // Selection
                case "NSelectedChildren":
                    DBusLite.AppendInt32Variant(iter, tree.GetSelectedIds(node).Length);
                    break;

                // Table
                case "NRows":
                    DBusLite.AppendInt32Variant(iter, tree.TryGetGrid(node, out int rows, out _) ? rows : 0);
                    break;
                case "NColumns":
                    DBusLite.AppendInt32Variant(iter, tree.TryGetGrid(node, out _, out int columns) ? columns : 0);
                    break;
                case "NSelectedRows":
                    DBusLite.AppendInt32Variant(iter, AtSpiPatterns.SelectedRows(tree, node).Length);
                    break;
                case "NSelectedColumns":
                    DBusLite.AppendInt32Variant(iter, 0);   // UIA selects rows and cells, never columns
                    break;
                case "Caption":
                case "Summary":
                    DBusLite.AppendObjectRefVariant(iter, s_busName, PathForNode(IAutomationTreeSource.InvalidNode));
                    break;

                // TableCell
                case "RowSpan":
                case "ColumnSpan":
                {
                    tree.TryGetGridItem(node, out _, out _, out int rowSpan, out int columnSpan, out _);
                    DBusLite.AppendInt32Variant(iter, Math.Max(property == "RowSpan" ? rowSpan : columnSpan, 1));
                    break;
                }
                case "Position":
                {
                    bool placed = tree.TryGetGridItem(node, out int row, out int column, out _, out _, out _);
                    AppendPairVariant(iter, placed ? row : -1, placed ? column : -1);
                    break;
                }
                case "Table":
                {
                    int grid = tree.TryGetGridItem(node, out _, out _, out _, out _, out int g) ? g : IAutomationTreeSource.InvalidNode;
                    DBusLite.AppendObjectRefVariant(iter, s_busName, PathForNode(grid));
                    break;
                }

                default:
                    DBusLite.AppendStringVariant(iter, string.Empty);
                    break;
            }
        }

        /// <summary>The application object's properties: its name, its windows, the desktop above it.</summary>
        private static void AppendApplicationProperty(byte* iter, string property)
        {
            switch (property)
            {
                case "Name": DBusLite.AppendStringVariant(iter, ApplicationName); break;
                case "Parent": DBusLite.AppendObjectRefVariant(iter, RegistryBusName, RootPath); break;
                case "ChildCount": DBusLite.AppendInt32Variant(iter, WindowRoots().Length); break;
                case "Locale": DBusLite.AppendStringVariant(iter, System.Globalization.CultureInfo.CurrentUICulture.Name); break;
                case "ToolkitName": DBusLite.AppendStringVariant(iter, "WPF"); break;
                case "Version": DBusLite.AppendStringVariant(iter, "10.0"); break;
                case "AtspiVersion": DBusLite.AppendStringVariant(iter, "2.1"); break;
                default: DBusLite.AppendStringVariant(iter, string.Empty); break;
            }
        }

        // the registry daemon, whose root is the desktop every application is embedded in
        private const string RegistryBusName = "org.a11y.atspi.Registry";

        private static string[] PropertiesOf(string iface) => iface switch
        {
            IfaceValue => new[] { "MinimumValue", "MaximumValue", "MinimumIncrement", "CurrentValue" },
            IfaceApplication => new[] { "ToolkitName", "Version", "AtspiVersion" },
            IfaceAction => new[] { "NActions" },
            IfaceText => new[] { "CharacterCount", "CaretOffset" },
            IfaceSelection => new[] { "NSelectedChildren" },
            IfaceTable => new[] { "NRows", "NColumns", "Caption", "Summary", "NSelectedRows", "NSelectedColumns" },
            IfaceTableCell => new[] { "ColumnSpan", "Position", "RowSpan", "Table" },
            IfaceComponent or IfaceEditableText => Array.Empty<string>(),
            _ => new[] { "Name", "Description", "Parent", "ChildCount", "Locale", "AccessibleId" },
        };

        /// <summary>
        /// Minimal introspection. Nothing in the AT-SPI protocol needs it, but every debugging tool
        /// does -- busctl tree, d-feet and Accerciser's inspector all walk Introspect, and a service
        /// that does not answer it is opaque exactly when something has gone wrong.
        /// </summary>
        private static IntPtr HandleIntrospect(IntPtr message, int node)
        {
            IAutomationTreeSource tree = Tree;
            string[] interfaces = node == AppNode ? ApplicationInterfaces
                : tree == null ? new[] { IfaceAccessible }
                : InterfacesOf(tree, node);

            IntPtr reply = DBusLite.NewMethodReturn(message);
            byte* iter = stackalloc byte[DBusLite.IterSize];
            DBusLite.IterInitAppend(reply, iter);
            DBusLite.AppendString(iter, DBusLite.DBUS_TYPE_STRING, IntrospectXmlFor(interfaces));
            return reply;
        }

        /// <summary>
        /// The introspection document for a node with these interfaces: the same set GetInterfaces
        /// reports, so a debugging tool shows exactly what a client would be offered.
        /// </summary>
        internal static string IntrospectXmlFor(string[] interfaces)
        {
            var xml = new System.Text.StringBuilder(IntrospectHeader);
            foreach (string iface in interfaces)
            {
                if (s_introspection.TryGetValue(iface, out string body))
                    xml.Append("  <interface name=\"").Append(iface).Append("\">\n").Append(body).Append("  </interface>\n");
            }
            return xml.Append("</node>\n").ToString();
        }

        private const string IntrospectHeader =
            "<!DOCTYPE node PUBLIC \"-//freedesktop//DTD D-BUS Object Introspection 1.0//EN\" " +
            "\"http://www.freedesktop.org/standards/dbus/1.0/introspect.dtd\">\n" +
            "<node>\n" +
            "  <interface name=\"org.freedesktop.DBus.Properties\">\n" +
            "    <method name=\"Get\"><arg type=\"s\" direction=\"in\"/><arg type=\"s\" direction=\"in\"/><arg type=\"v\" direction=\"out\"/></method>\n" +
            "    <method name=\"GetAll\"><arg type=\"s\" direction=\"in\"/><arg type=\"a{sv}\" direction=\"out\"/></method>\n" +
            "    <method name=\"Set\"><arg type=\"s\" direction=\"in\"/><arg type=\"s\" direction=\"in\"/><arg type=\"v\" direction=\"in\"/></method>\n" +
            "  </interface>\n";

        // A method, from its argument signatures: "i,u>s" is (in i, in u) -> (out s).
        private static string M(string name, string signature)
        {
            var line = new System.Text.StringBuilder("    <method name=\"").Append(name).Append("\">");
            string[] halves = signature.Split('>');
            foreach (string arg in halves[0].Split(',', StringSplitOptions.RemoveEmptyEntries))
                line.Append("<arg type=\"").Append(arg).Append("\" direction=\"in\"/>");
            if (halves.Length > 1)
                foreach (string arg in halves[1].Split(',', StringSplitOptions.RemoveEmptyEntries))
                    line.Append("<arg type=\"").Append(arg).Append("\" direction=\"out\"/>");
            return line.Append("</method>\n").ToString();
        }

        private static string P(string name, string type, string access = "read")
            => "    <property name=\"" + name + "\" type=\"" + type + "\" access=\"" + access + "\"/>\n";

        // Each interface's members, as at-spi2-core's xml/*.xml declares them.
        private static readonly System.Collections.Generic.Dictionary<string, string> s_introspection = new()
        {
            [IfaceAccessible] =
                P("Name", "s") + P("Description", "s") + P("Parent", "(so)") + P("ChildCount", "i") +
                P("Locale", "s") + P("AccessibleId", "s") +
                M("GetRole", ">u") + M("GetRoleName", ">s") + M("GetLocalizedRoleName", ">s") + M("GetState", ">au") +
                M("GetChildAtIndex", "i>(so)") + M("GetChildren", ">a(so)") + M("GetIndexInParent", ">i") +
                M("GetApplication", ">(so)") + M("GetAttributes", ">a{ss}") + M("GetRelationSet", ">a(ua(so))") +
                M("GetInterfaces", ">as"),
            [IfaceComponent] =
                M("GetExtents", "u>(iiii)") + M("GetPosition", "u>i,i") + M("GetSize", ">i,i") + M("GetLayer", ">u") +
                M("GetAccessibleAtPoint", "i,i,u>(so)") + M("GrabFocus", ">b") +
                M("ScrollTo", "u>b") + M("ScrollToPoint", "u,i,i>b"),
            [IfaceAction] =
                P("NActions", "i") +
                M("GetNActions", ">i") + M("GetName", "i>s") + M("GetDescription", "i>s") + M("DoAction", "i>b"),
            [IfaceValue] =
                P("MinimumValue", "d") + P("MaximumValue", "d") + P("MinimumIncrement", "d") + P("CurrentValue", "d", "readwrite"),
            [IfaceApplication] =
                P("ToolkitName", "s") + P("Version", "s") + P("AtspiVersion", "s") + M("GetLocale", "u>s"),
            [IfaceText] =
                P("CharacterCount", "i") + P("CaretOffset", "i") +
                M("GetStringAtOffset", "i,u>s,i,i") + M("GetText", "i,i>s") + M("SetCaretOffset", "i>b") +
                M("GetTextBeforeOffset", "i,u>s,i,i") + M("GetTextAtOffset", "i,u>s,i,i") + M("GetTextAfterOffset", "i,u>s,i,i") +
                M("GetCharacterAtOffset", "i>i") + M("GetAttributeValue", "i,s>s") + M("GetAttributes", "i>a{ss},i,i") +
                M("GetDefaultAttributes", ">a{ss}") + M("GetCharacterExtents", "i,u>i,i,i,i") + M("GetOffsetAtPoint", "i,i,u>i") +
                M("GetNSelections", ">i") + M("GetSelection", "i>i,i") + M("AddSelection", "i,i>b") + M("RemoveSelection", "i>b") +
                M("SetSelection", "i,i,i>b") + M("GetRangeExtents", "i,i,u>i,i,i,i") + M("GetBoundedRanges", "i,i,i,i,u,u,u>a(iisv)") +
                M("GetAttributeRun", "i,b>a{ss},i,i") + M("GetDefaultAttributeSet", ">a{ss}") +
                M("ScrollSubstringTo", "i,i,u>b") + M("ScrollSubstringToPoint", "i,i,u,i,i>b"),
            [IfaceEditableText] =
                M("SetTextContents", "s>b") + M("InsertText", "i,s,i>b") + M("CopyText", "i,i>") + M("CutText", "i,i>b") +
                M("DeleteText", "i,i>b") + M("PasteText", "i>b"),
            [IfaceSelection] =
                P("NSelectedChildren", "i") +
                M("GetSelectedChild", "i>(so)") + M("SelectChild", "i>b") + M("DeselectSelectedChild", "i>b") +
                M("IsChildSelected", "i>b") + M("SelectAll", ">b") + M("ClearSelection", ">b") + M("DeselectChild", "i>b"),
            [IfaceTable] =
                P("NRows", "i") + P("NColumns", "i") + P("Caption", "(so)") + P("Summary", "(so)") +
                P("NSelectedRows", "i") + P("NSelectedColumns", "i") +
                M("GetAccessibleAt", "i,i>(so)") + M("GetIndexAt", "i,i>i") + M("GetRowAtIndex", "i>i") + M("GetColumnAtIndex", "i>i") +
                M("GetRowDescription", "i>s") + M("GetColumnDescription", "i>s") + M("GetRowExtentAt", "i,i>i") +
                M("GetColumnExtentAt", "i,i>i") + M("GetRowHeader", "i>(so)") + M("GetColumnHeader", "i>(so)") +
                M("GetSelectedRows", ">ai") + M("GetSelectedColumns", ">ai") + M("IsRowSelected", "i>b") +
                M("IsColumnSelected", "i>b") + M("IsSelected", "i,i>b") + M("AddRowSelection", "i>b") +
                M("AddColumnSelection", "i>b") + M("RemoveRowSelection", "i>b") + M("RemoveColumnSelection", "i>b") +
                M("GetRowColumnExtentsAtIndex", "i>b,i,i,i,i,b"),
            [IfaceTableCell] =
                P("ColumnSpan", "i") + P("Position", "(ii)") + P("RowSpan", "i") + P("Table", "(so)") +
                M("GetRowHeaderCells", ">a(so)") + M("GetColumnHeaderCells", ">a(so)") + M("GetRowColumnSpan", ">i,i,i,i"),
        };

        // ---- which interfaces a node has --------------------------------------------

        private static readonly string[] ApplicationInterfaces = { IfaceAccessible, IfaceApplication };

        private static string[] InterfacesOf(IAutomationTreeSource tree, int node)
            => InterfacesFor(tree.GetState(node), AvailableActions(tree, node).Length > 0);

        /// <summary>
        /// The interfaces a node implements, as GetInterfaces reports them. This is what a client
        /// reads before it asks anything else: libatspi caches it, Orca checks it before every
        /// query, and an interface missing here is one the node is never asked about however well
        /// it would have answered. A node offers what its patterns support and nothing more, so
        /// Orca does not try to read text out of a button.
        ///
        /// A password box's text is not offered at all: its characters are the one thing a screen
        /// reader must not read, and the role (PASSWORD_TEXT) already says what it is.
        /// </summary>
        internal static string[] InterfacesFor(AccessibleState state, bool hasActions)
        {
            var interfaces = new System.Collections.Generic.List<string>(6) { IfaceAccessible, IfaceComponent };
            if (hasActions) interfaces.Add(IfaceAction);
            if ((state & AccessibleState.HasRange) != 0) interfaces.Add(IfaceValue);
            if (HasTextInterface(state))
            {
                interfaces.Add(IfaceText);
                if (IsEditableText(state)) interfaces.Add(IfaceEditableText);
            }
            if ((state & AccessibleState.SelectionContainer) != 0) interfaces.Add(IfaceSelection);
            if ((state & AccessibleState.Grid) != 0) interfaces.Add(IfaceTable);
            if ((state & AccessibleState.GridItem) != 0) interfaces.Add(IfaceTableCell);
            return interfaces.ToArray();
        }

        internal static bool HasTextInterface(AccessibleState state)
            => (state & AccessibleState.HasText) != 0 && (state & AccessibleState.Password) == 0;

        /// <summary>Text that can be typed into: EditableText is built on the Value pattern's SetValue,
        /// so a text without one, or a read-only one, cannot offer it.</summary>
        internal static bool IsEditableText(AccessibleState state)
            => HasTextInterface(state) && (state & AccessibleState.HasValue) != 0 && (state & AccessibleState.ReadOnly) == 0;

        // ---- org.a11y.atspi.Accessible ---------------------------------------------

        private static IntPtr HandleAccessible(IntPtr message, string member, int node)
        {
            IAutomationTreeSource tree = Tree;
            if (tree == null) return IntPtr.Zero;
            if (node == AppNode) return HandleApplicationAccessible(message, member);

            IntPtr reply;
            byte* iter = stackalloc byte[DBusLite.IterSize];

            switch (member)
            {
                case "GetRole":
                    reply = DBusLite.NewMethodReturn(message);
                    DBusLite.IterInitAppend(reply, iter);
                    DBusLite.AppendUInt32(iter, AtSpiRoles.RoleFor(tree.GetRole(node), tree.GetState(node)));
                    return reply;

                case "GetRoleName":
                case "GetLocalizedRoleName":
                    reply = DBusLite.NewMethodReturn(message);
                    DBusLite.IterInitAppend(reply, iter);
                    DBusLite.AppendString(iter, DBusLite.DBUS_TYPE_STRING,
                                          AtSpiRoles.RoleNameFor(tree.GetRole(node), tree.GetState(node)));
                    return reply;

                case "GetState":
                {
                    AccessibleState state = tree.GetState(node);
                    int visualState = 0;
                    if ((state & AccessibleState.Window) != 0) tree.TryGetWindow(node, out visualState, out _, out _, out _);
                    ulong states = AtSpiRoles.StatesFor(state, visualState);
                    // the frame of the window the compositor has active
                    if (s_active != IntPtr.Zero && tree.GetRootId(s_active) == node) states |= 1UL << StateActive;
                    reply = DBusLite.NewMethodReturn(message);
                    DBusLite.IterInitAppend(reply, iter);
                    DBusLite.AppendStateSet(iter, states);
                    return reply;
                }

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
                    // a window's root is a child of the application
                    if (parent < 0) index = Array.IndexOf(WindowRoots(), node);
                    else
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

                case "GetInterfaces":
                    return ReplyStrings(message, InterfacesOf(tree, node));

                default:
                    return IntPtr.Zero;
            }
        }

        private const int StateActive = 1;   // ATSPI_STATE_ACTIVE

        /// <summary>The application object as an accessible: role application, its windows as children.</summary>
        private static IntPtr HandleApplicationAccessible(IntPtr message, string member)
        {
            byte* iter = stackalloc byte[DBusLite.IterSize];
            IntPtr reply = DBusLite.NewMethodReturn(message);
            DBusLite.IterInitAppend(reply, iter);
            int[] windows = WindowRoots();
            switch (member)
            {
                case "GetRole": DBusLite.AppendUInt32(iter, 75); break;   // ATSPI_ROLE_APPLICATION
                case "GetRoleName":
                case "GetLocalizedRoleName": DBusLite.AppendString(iter, DBusLite.DBUS_TYPE_STRING, "application"); break;
                case "GetState": DBusLite.AppendStateSet(iter, 0); break;
                case "GetChildAtIndex":
                {
                    byte* args = stackalloc byte[DBusLite.IterSize];
                    int index = DBusLite.IterInit(message, args) ? DBusLite.ReadInt32Or(args, -1) : -1;
                    DBusLite.AppendObjectRef(iter, s_busName, index >= 0 && index < windows.Length ? PathForNode(windows[index]) : PathForNode(-1));
                    break;
                }
                case "GetChildren":
                {
                    byte* array = stackalloc byte[DBusLite.IterSize];
                    DBusLite.OpenArray(iter, "(so)", array);
                    foreach (int root in windows) DBusLite.AppendObjectRef(array, s_busName, PathForNode(root));
                    DBusLite.CloseContainer(iter, array);
                    break;
                }
                case "GetIndexInParent": DBusLite.AppendInt32(iter, -1); break;
                case "GetApplication": DBusLite.AppendObjectRef(iter, s_busName, RootPath); break;
                case "GetAttributes": DBusLite.OpenAndCloseEmptyArray(iter, "{ss}"); break;
                case "GetRelationSet": DBusLite.OpenAndCloseEmptyArray(iter, "(ua(so))"); break;
                case "GetInterfaces":
                {
                    byte* array = stackalloc byte[DBusLite.IterSize];
                    DBusLite.OpenArray(iter, "s", array);
                    foreach (string iface in ApplicationInterfaces) DBusLite.AppendString(array, DBusLite.DBUS_TYPE_STRING, iface);
                    DBusLite.CloseContainer(iter, array);
                    break;
                }
                default:
                    DBusLite.Unref(reply);
                    return IntPtr.Zero;
            }
            return reply;
        }

        // ---- org.a11y.atspi.Component ----------------------------------------------

        private static IntPtr HandleComponent(IntPtr message, string member, int node)
        {
            IAutomationTreeSource tree = Tree;
            if (tree == null || node == AppNode) return IntPtr.Zero;   // the application has no extent

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

                    int hit = tree.HitTest(WindowOf(tree, node), px, py);

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

                case "ScrollTo":
                case "ScrollToPoint":
                    // Orca's "move to" for an object scrolled out of view. Both come down to the
                    // ScrollItem pattern, which knows one place to scroll to -- into view -- so the
                    // edge (ScrollTo's type) or point (ScrollToPoint's) asked for is approximated
                    // by it, as GTK approximates them by grabbing focus.
                    return ReplyBool(message, (tree.GetState(node) & AccessibleState.ScrollItem) != 0 && tree.ScrollIntoView(node));

                default:
                    return IntPtr.Zero;
            }
        }

        // ---- org.a11y.atspi.Action -------------------------------------------------

        private static IntPtr HandleAction(IntPtr message, string member, int node)
        {
            IAutomationTreeSource tree = Tree;
            if (tree == null || node == AppNode) return IntPtr.Zero;

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

        // ---- arguments and replies --------------------------------------------------

        /// <summary>
        /// A call's arguments, read once and in order: int32, uint32 and boolean as ints, strings and
        /// object paths as strings, anything else as null. The patterns below take a handful of
        /// scalars each, and a client that sends the wrong type -- or none -- gets the fallback for
        /// that argument rather than a garbled read; a double is never read through an int, which
        /// would write eight bytes into four.
        /// </summary>
        private static object[] ReadArgs(IntPtr message)
        {
            byte* args = stackalloc byte[DBusLite.IterSize];
            if (!DBusLite.IterInit(message, args)) return Array.Empty<object>();
            var values = new System.Collections.Generic.List<object>(4);
            do
            {
                int type = DBusLite.IterArgType(args);
                if (type == DBusLite.DBUS_TYPE_INVALID) break;
                values.Add(type switch
                {
                    DBusLite.DBUS_TYPE_INT32 or DBusLite.DBUS_TYPE_UINT32 or DBusLite.DBUS_TYPE_BOOLEAN => (object)DBusLite.ReadInt32(args),
                    DBusLite.DBUS_TYPE_STRING or DBusLite.DBUS_TYPE_OBJECT_PATH => DBusLite.ReadString(args),
                    _ => null,
                });
            } while (DBusLite.IterNext(args));
            return values.ToArray();
        }

        private static int Arg(object[] args, int index, int fallback)
            => index < args.Length && args[index] is int value ? value : fallback;

        private static string StringArg(object[] args, int index)
            => index < args.Length && args[index] is string value ? value : string.Empty;

        private delegate void ReplyBody(byte* iter);

        /// <summary>A method return with a body. A body that throws drops the half-built reply, so
        /// the dispatcher's guard sees the exception and not a leaked message.</summary>
        private static IntPtr Reply(IntPtr message, ReplyBody body)
        {
            IntPtr reply = DBusLite.NewMethodReturn(message);
            if (reply == IntPtr.Zero) return IntPtr.Zero;
            try
            {
                byte* iter = stackalloc byte[DBusLite.IterSize];
                DBusLite.IterInitAppend(reply, iter);
                body(iter);
                return reply;
            }
            catch
            {
                DBusLite.Unref(reply);
                throw;
            }
        }

        private static IntPtr ReplyEmpty(IntPtr message) => DBusLite.NewMethodReturn(message);
        private static IntPtr ReplyBool(IntPtr message, bool value) => Reply(message, iter => DBusLite.AppendBool(iter, value));
        private static IntPtr ReplyString(IntPtr message, string value) => Reply(message, iter => DBusLite.AppendString(iter, DBusLite.DBUS_TYPE_STRING, value));
        private static IntPtr ReplyRef(IntPtr message, int node) => Reply(message, iter => DBusLite.AppendObjectRef(iter, s_busName, PathForNode(node)));

        private static IntPtr ReplyInts(IntPtr message, params int[] values) => Reply(message, iter =>
        {
            foreach (int value in values) DBusLite.AppendInt32(iter, value);
        });

        private static IntPtr ReplyIntArray(IntPtr message, int[] values) => Reply(message, iter =>
        {
            byte* array = stackalloc byte[DBusLite.IterSize];
            DBusLite.OpenArray(iter, "i", array);
            foreach (int value in values) DBusLite.AppendInt32(array, value);
            DBusLite.CloseContainer(iter, array);
        });

        private static IntPtr ReplyRefs(IntPtr message, int[] nodes) => Reply(message, iter =>
        {
            byte* array = stackalloc byte[DBusLite.IterSize];
            DBusLite.OpenArray(iter, "(so)", array);
            foreach (int node in nodes) DBusLite.AppendObjectRef(array, s_busName, PathForNode(node));
            DBusLite.CloseContainer(iter, array);
        });

        private static IntPtr ReplyStrings(IntPtr message, string[] values) => Reply(message, iter =>
        {
            byte* array = stackalloc byte[DBusLite.IterSize];
            DBusLite.OpenArray(iter, "s", array);
            foreach (string value in values) DBusLite.AppendString(array, DBusLite.DBUS_TYPE_STRING, value);
            DBusLite.CloseContainer(iter, array);
        });

        /// <summary>The (s, i, i) the GetText*Offset family answers: the text and where it starts and ends.</summary>
        private static IntPtr ReplyTextRange(IntPtr message, (string Text, int Start, int End) range) => Reply(message, iter =>
        {
            DBusLite.AppendString(iter, DBusLite.DBUS_TYPE_STRING, range.Text);
            DBusLite.AppendInt32(iter, range.Start);
            DBusLite.AppendInt32(iter, range.End);
        });

        /// <summary>
        /// An attribute run: (a{ss}, i, i), the attributes and the run they hold over. Always empty
        /// over the whole text. WPF's text pattern does carry fonts and colours, but Orca reads none
        /// of them unless asked, and an attribute it misreads is worse than none -- the same choice
        /// the macOS backend makes.
        /// </summary>
        private static IntPtr ReplyAttributeRun(IntPtr message, int end, bool withRun) => Reply(message, iter =>
        {
            DBusLite.OpenAndCloseEmptyArray(iter, "{ss}");
            if (withRun)
            {
                DBusLite.AppendInt32(iter, 0);
                DBusLite.AppendInt32(iter, end);
            }
        });

        [DllImport("libdbus-1.so.3")]
        private static extern int dbus_message_iter_open_container(byte* iter, int type, [MarshalAs(UnmanagedType.LPUTF8Str)] string signature, byte* sub);

        /// <summary>A variant holding (ii) -- TableCell's Position, the one property here that is a pair.</summary>
        private static void AppendPairVariant(byte* iter, int first, int second)
        {
            byte* variant = stackalloc byte[DBusLite.IterSize];
            byte* pair = stackalloc byte[DBusLite.IterSize];
            dbus_message_iter_open_container(iter, DBusLite.DBUS_TYPE_VARIANT, "(ii)", variant);
            DBusLite.OpenStruct(variant, pair);
            DBusLite.AppendInt32(pair, first);
            DBusLite.AppendInt32(pair, second);
            DBusLite.CloseContainer(variant, pair);
            DBusLite.CloseContainer(iter, variant);
        }

        // ---- org.a11y.atspi.Text ---------------------------------------------------

        /// <summary>
        /// A text node's text, or null for a node without the interface. The first read of a node's
        /// text is also the baseline its first text-changed signal is diffed against (see
        /// <see cref="EmitTextChanged"/>); later reads leave the baseline alone, because a client can
        /// read the new text before the change notification that describes it has been sent.
        /// </summary>
        private static string TextOf(IAutomationTreeSource tree, int node)
        {
            string text = CurrentText(tree, node);
            if (text != null && !s_texts.ContainsKey(node)) Remember(s_texts, node, text);
            return text;
        }

        private static string CurrentText(IAutomationTreeSource tree, int node)
            => HasTextInterface(tree.GetState(node)) && tree.TryGetText(node, out string text) ? text ?? string.Empty : null;

        /// <summary>The selection, clipped to the text, with the caret on the end the user moved last.</summary>
        private static AtSpiText.Selection SelectionOf(IAutomationTreeSource tree, int node, string text)
        {
            if (!tree.TryGetTextSelection(node, out int start, out int length)) return default;
            start = Math.Clamp(start, 0, text.Length);
            length = Math.Clamp(length, 0, text.Length - start);
            if (s_selections.TryGetValue(node, out AtSpiText.Selection known) && known.Start == start && known.Length == length)
                return known;
            return new AtSpiText.Selection(start, length, caretAtEnd: false);
        }

        /// <summary>A character range as the tree's UTF-16 one, ordered and clipped. -1 is the end.</summary>
        private static (int Start, int Length) Utf16Range(string text, int start, int end)
        {
            int s = AtSpiText.ToUtf16(text, Math.Max(start, 0));
            int e = AtSpiText.ToUtf16(text, end);
            if (e < s) (s, e) = (e, s);
            return (s, e - s);
        }

        private static AtSpiText.LineSource LinesOf(IAutomationTreeSource tree, int node)
            => (int offset, out int start, out int length) => tree.TryGetTextLine(node, offset, out start, out length, out _);

        private static IntPtr ReplyExtents(IntPtr message, IAutomationTreeSource tree, int node, int start, int length)
        {
            // The coordinate type is ignored, as Component's is: under Wayland screen and window
            // coordinates are the same synthesised space (the header explains why).
            if (length <= 0 || !tree.TryGetTextRangeBounds(node, start, length, out double x, out double y, out double w, out double h))
                return ReplyInts(message, 0, 0, 0, 0);
            return ReplyInts(message, (int)x, (int)y, (int)w, (int)h);
        }

        private static IntPtr HandleText(IntPtr message, string member, int node)
        {
            IAutomationTreeSource tree = Tree;
            if (tree == null || node == AppNode) return IntPtr.Zero;

            object[] args = ReadArgs(message);
            string text = TextOf(tree, node) ?? string.Empty;
            int count = AtSpiText.CharacterCount(text);

            switch (member)
            {
                case "GetText":
                    return ReplyString(message, AtSpiText.Substring(text, Arg(args, 0, 0), Arg(args, 1, -1)));

                case "SetCaretOffset":
                {
                    int caret = AtSpiText.ToUtf16(text, Math.Clamp(Arg(args, 0, 0), 0, count));
                    return ReplyBool(message, tree.SetTextSelection(node, caret, 0));
                }

                case "GetTextAtOffset":
                case "GetTextBeforeOffset":
                case "GetTextAfterOffset":
                {
                    if (!AtSpiText.TryUnitForBoundary((uint)Arg(args, 1, 0), out AtSpiText.Unit unit))
                        return ReplyTextRange(message, (string.Empty, 0, 0));
                    int direction = member == "GetTextBeforeOffset" ? -1 : member == "GetTextAfterOffset" ? 1 : 0;
                    return ReplyTextRange(message, AtSpiText.TextAt(text, Arg(args, 0, 0), unit, direction, LinesOf(tree, node)));
                }

                case "GetStringAtOffset":
                {
                    if (!AtSpiText.TryUnitForGranularity((uint)Arg(args, 1, 0), out AtSpiText.Unit unit))
                        return ReplyTextRange(message, (string.Empty, 0, 0));
                    return ReplyTextRange(message, AtSpiText.TextAt(text, Arg(args, 0, 0), unit, 0, LinesOf(tree, node)));
                }

                case "GetCharacterAtOffset":
                    return ReplyInts(message, AtSpiText.CharacterAt(text, Arg(args, 0, -1)));

                case "GetCharacterExtents":
                {
                    (int start, int length) = AtSpiText.CharacterRange(text, Arg(args, 0, 0));
                    return ReplyExtents(message, tree, node, start, length);
                }

                case "GetRangeExtents":
                {
                    (int start, int length) = Utf16Range(text, Arg(args, 0, 0), Arg(args, 1, 0));
                    return ReplyExtents(message, tree, node, start, length);
                }

                case "GetOffsetAtPoint":
                {
                    int offset = tree.GetTextOffsetAtPoint(node, Arg(args, 0, 0), Arg(args, 1, 0));
                    return ReplyInts(message, offset < 0 ? -1 : AtSpiText.ToCodePoints(text, offset));
                }

                // One selection at most: WPF's text selection is a single range. A caret is not a
                // selection, so a text with nothing selected has none.
                case "GetNSelections":
                    return ReplyInts(message, SelectionOf(tree, node, text).Length > 0 ? 1 : 0);

                case "GetSelection":
                {
                    AtSpiText.Selection selection = SelectionOf(tree, node, text);
                    if (Arg(args, 0, -1) != 0 || selection.Length == 0) return ReplyInts(message, 0, 0);
                    return ReplyInts(message, AtSpiText.ToCodePoints(text, selection.Start),
                                     AtSpiText.ToCodePoints(text, selection.Start + selection.Length));
                }

                case "AddSelection":
                {
                    // Adding a second range is not possible, so only a text without one can.
                    if (SelectionOf(tree, node, text).Length > 0) return ReplyBool(message, false);
                    (int start, int length) = Utf16Range(text, Arg(args, 0, 0), Arg(args, 1, 0));
                    return ReplyBool(message, tree.SetTextSelection(node, start, length));
                }

                case "SetSelection":
                {
                    if (Arg(args, 0, -1) != 0) return ReplyBool(message, false);
                    (int start, int length) = Utf16Range(text, Arg(args, 1, 0), Arg(args, 2, 0));
                    return ReplyBool(message, tree.SetTextSelection(node, start, length));
                }

                case "RemoveSelection":
                {
                    // Removing the selection leaves the caret where it was, as GTK's does.
                    AtSpiText.Selection selection = SelectionOf(tree, node, text);
                    if (Arg(args, 0, -1) != 0 || selection.Length == 0) return ReplyBool(message, false);
                    return ReplyBool(message, tree.SetTextSelection(node, selection.Caret, 0));
                }

                case "GetAttributes":
                case "GetAttributeRun":
                    return ReplyAttributeRun(message, count, withRun: true);
                case "GetDefaultAttributes":
                case "GetDefaultAttributeSet":
                    return ReplyAttributeRun(message, count, withRun: false);
                case "GetAttributeValue":
                    return ReplyString(message, string.Empty);

                case "GetBoundedRanges":
                    return Reply(message, iter => DBusLite.OpenAndCloseEmptyArray(iter, "(iisv)"));

                case "ScrollSubstringTo":
                case "ScrollSubstringToPoint":
                    // The tree can scroll a node into view but not a range of its text.
                    return ReplyBool(message, false);

                default:
                    return IntPtr.Zero;
            }
        }

        // ---- org.a11y.atspi.EditableText -------------------------------------------

        /// <summary>
        /// Editing, built on the one write the tree offers for text: the Value pattern's SetValue,
        /// with the whole new text. Insert and delete are computed here from the current text, so
        /// they are exactly as good as SetValue is -- the caret is the control's business afterwards.
        /// Copy, cut and paste need the clipboard, which is the application's and not the
        /// accessibility tree's, so they are declined rather than half done.
        /// </summary>
        private static IntPtr HandleEditableText(IntPtr message, string member, int node)
        {
            IAutomationTreeSource tree = Tree;
            if (tree == null || node == AppNode) return IntPtr.Zero;

            object[] args = ReadArgs(message);
            bool editable = IsEditableText(tree.GetState(node));
            string text = TextOf(tree, node) ?? string.Empty;

            switch (member)
            {
                case "SetTextContents":
                    return ReplyBool(message, editable && tree.SetValue(node, StringArg(args, 0)));

                case "InsertText":
                {
                    if (!editable) return ReplyBool(message, false);
                    int at = AtSpiText.ToUtf16(text, Math.Max(Arg(args, 0, 0), 0));
                    string inserted = StringArg(args, 1);
                    int length = Arg(args, 2, -1);
                    // the length is in characters, and a negative or overlong one means all of it
                    if (length >= 0 && length < AtSpiText.CharacterCount(inserted)) inserted = AtSpiText.Substring(inserted, 0, length);
                    return ReplyBool(message, tree.SetValue(node, text.Insert(at, inserted)));
                }

                case "DeleteText":
                {
                    if (!editable) return ReplyBool(message, false);
                    (int start, int length) = Utf16Range(text, Arg(args, 0, 0), Arg(args, 1, 0));
                    return ReplyBool(message, length == 0 || tree.SetValue(node, text.Remove(start, length)));
                }

                case "CopyText":
                    return ReplyEmpty(message);
                case "CutText":
                case "PasteText":
                    return ReplyBool(message, false);

                default:
                    return IntPtr.Zero;
            }
        }

        // ---- org.a11y.atspi.Selection ----------------------------------------------

        private static IntPtr HandleSelection(IntPtr message, string member, int node)
        {
            IAutomationTreeSource tree = Tree;
            if (tree == null || node == AppNode) return IntPtr.Zero;

            object[] args = ReadArgs(message);
            int index = Arg(args, 0, -1);

            return member switch
            {
                "GetSelectedChild" => ReplyRef(message, AtSpiPatterns.SelectedChildAt(tree, node, index)),
                "SelectChild" => ReplyBool(message, AtSpiPatterns.SelectChild(tree, node, index)),
                "DeselectSelectedChild" => ReplyBool(message, AtSpiPatterns.DeselectSelectedChild(tree, node, index)),
                "DeselectChild" => ReplyBool(message, AtSpiPatterns.DeselectChild(tree, node, index)),
                "IsChildSelected" => ReplyBool(message, AtSpiPatterns.IsChildSelected(tree, node, index)),
                "SelectAll" => ReplyBool(message, AtSpiPatterns.SelectAll(tree, node)),
                "ClearSelection" => ReplyBool(message, AtSpiPatterns.ClearSelection(tree, node)),
                _ => IntPtr.Zero,
            };
        }

        // ---- org.a11y.atspi.Table / TableCell --------------------------------------

        private static IntPtr HandleTable(IntPtr message, string member, int node)
        {
            IAutomationTreeSource tree = Tree;
            if (tree == null || node == AppNode) return IntPtr.Zero;

            object[] args = ReadArgs(message);
            int a = Arg(args, 0, -1), b = Arg(args, 1, -1);
            tree.TryGetGrid(node, out int rows, out int columns);

            switch (member)
            {
                case "GetAccessibleAt":
                    return ReplyRef(message, AtSpiPatterns.CellIndex(rows, columns, a, b) < 0 ? IAutomationTreeSource.InvalidNode : tree.GetGridCell(node, a, b));
                case "GetIndexAt":
                    return ReplyInts(message, AtSpiPatterns.CellIndex(rows, columns, a, b));
                case "GetRowAtIndex":
                    return ReplyInts(message, AtSpiPatterns.RowAndColumnOf(rows, columns, a).Row);
                case "GetColumnAtIndex":
                    return ReplyInts(message, AtSpiPatterns.RowAndColumnOf(rows, columns, a).Column);

                case "GetRowExtentAt":
                    return ReplyInts(message, AtSpiPatterns.SpanAt(tree, node, a, b).RowSpan);
                case "GetColumnExtentAt":
                    return ReplyInts(message, AtSpiPatterns.SpanAt(tree, node, a, b).ColumnSpan);

                case "GetRowHeader":
                    return ReplyRef(message, AtSpiPatterns.RowHeader(tree, node, a));
                case "GetColumnHeader":
                    return ReplyRef(message, AtSpiPatterns.ColumnHeader(tree, node, a));
                case "GetRowDescription":
                {
                    int header = AtSpiPatterns.RowHeader(tree, node, a);
                    return ReplyString(message, header >= 0 ? tree.GetName(header) : string.Empty);
                }
                case "GetColumnDescription":
                {
                    int header = AtSpiPatterns.ColumnHeader(tree, node, a);
                    return ReplyString(message, header >= 0 ? tree.GetName(header) : string.Empty);
                }

                case "GetSelectedRows":
                    return ReplyIntArray(message, AtSpiPatterns.SelectedRows(tree, node));
                case "GetSelectedColumns":
                    return ReplyIntArray(message, Array.Empty<int>());
                case "IsRowSelected":
                    return ReplyBool(message, Array.IndexOf(AtSpiPatterns.SelectedRows(tree, node), a) >= 0);
                case "IsColumnSelected":
                    return ReplyBool(message, false);
                case "IsSelected":
                    return ReplyBool(message, AtSpiPatterns.IsCellSelected(tree, node, a, b));

                // Selecting whole rows and columns is the item's business (its own Selection
                // pattern, through the container's Selection interface), not the grid's.
                case "AddRowSelection":
                case "AddColumnSelection":
                case "RemoveRowSelection":
                case "RemoveColumnSelection":
                    return ReplyBool(message, false);

                case "GetRowColumnExtentsAtIndex":
                {
                    (int row, int column) = AtSpiPatterns.RowAndColumnOf(rows, columns, a);
                    bool ok = row >= 0;
                    (int rowSpan, int columnSpan) = ok ? AtSpiPatterns.SpanAt(tree, node, row, column) : (0, 0);
                    bool selected = ok && AtSpiPatterns.IsCellSelected(tree, node, row, column);
                    return Reply(message, iter =>
                    {
                        DBusLite.AppendBool(iter, ok);
                        DBusLite.AppendInt32(iter, row);
                        DBusLite.AppendInt32(iter, column);
                        DBusLite.AppendInt32(iter, rowSpan);
                        DBusLite.AppendInt32(iter, columnSpan);
                        DBusLite.AppendBool(iter, selected);
                    });
                }

                default:
                    return IntPtr.Zero;
            }
        }

        private static IntPtr HandleTableCell(IntPtr message, string member, int node)
        {
            IAutomationTreeSource tree = Tree;
            if (tree == null || node == AppNode) return IntPtr.Zero;

            switch (member)
            {
                case "GetRowHeaderCells":
                    return ReplyRefs(message, AtSpiPatterns.HeaderCellsOf(tree, node, columns: false));
                case "GetColumnHeaderCells":
                    return ReplyRefs(message, AtSpiPatterns.HeaderCellsOf(tree, node, columns: true));
                case "GetRowColumnSpan":
                {
                    bool placed = tree.TryGetGridItem(node, out int row, out int column, out int rowSpan, out int columnSpan, out _);
                    return placed
                        ? ReplyInts(message, row, column, Math.Max(rowSpan, 1), Math.Max(columnSpan, 1))
                        : ReplyInts(message, -1, -1, 0, 0);
                }
                default:
                    return IntPtr.Zero;
            }
        }

        // ---- events out ------------------------------------------------------------

        /// <summary>
        /// The node that last reported gaining focus, so the one LOSING it can be told so. AT-SPI
        /// state changes are edges, not a snapshot: without the trailing 0 a client is left believing
        /// two objects are focused at once, and only one of them is.
        /// </summary>
        private static int s_focused = IAutomationTreeSource.InvalidNode;

        private static void OnTreeChanged(AutomationChange change)
        {
            if (s_bus == IntPtr.Zero) return;

            try
            {
                IAutomationTreeSource tree = Tree;
                string path = PathForNode(change.NodeId);
                string detail = AtSpiRoles.SignalFor(change.Kind).Detail;

                switch (change.Kind)
                {
                    case AutomationChangeKind.FocusChanged:
                        if (s_focused != change.NodeId && s_focused >= 0)
                        {
                            EmitStateChanged(PathForNode(s_focused), detail, 0);
                        }
                        EmitStateChanged(path, detail, 1);
                        s_focused = change.NodeId;
                        SeedText(tree, change.NodeId);
                        break;

                    // Each of these is a state that can go BOTH ways, so the current value is read
                    // back rather than assumed to be "on" -- a collapse and an expand are the same
                    // notification, and announcing both as "expanded" is worse than silence.
                    case AutomationChangeKind.SelectionChanged:
                        EmitStateChanged(path, detail, Has(tree, change.NodeId, AccessibleState.Selected));
                        break;
                    case AutomationChangeKind.ExpandedChanged:
                        EmitStateChanged(path, detail, Has(tree, change.NodeId, AccessibleState.Expanded));
                        break;
                    case AutomationChangeKind.CheckedChanged:
                        EmitStateChanged(path, detail, Has(tree, change.NodeId, AccessibleState.Checked));
                        break;
                    case AutomationChangeKind.EnabledChanged:
                    {
                        int on = Has(tree, change.NodeId, AccessibleState.Enabled);
                        EmitStateChanged(path, detail, on);
                        EmitStateChanged(path, "sensitive", on);   // AT-SPI splits these; WPF does not
                        break;
                    }
                    case AutomationChangeKind.OffscreenChanged:
                        // SHOWING, not VISIBLE: a row scrolled out of a list still exists and is
                        // still worth navigating to, which is the distinction StatesFor encodes.
                        EmitStateChanged(path, detail,
                                         Has(tree, change.NodeId, AccessibleState.Offscreen) == 1 ? 0 : 1);
                        break;

                    case AutomationChangeKind.ValueChanged:
                        DBusLite.EmitAtSpiEvent(s_bus, s_busName, path, IfaceEventObject, "PropertyChange",
                                                detail, 0, 0,
                                                tree != null && tree.TryGetRange(change.NodeId, out double v, out _, out _) ? v : 0);
                        break;
                    case AutomationChangeKind.NameChanged:
                        DBusLite.EmitAtSpiEvent(s_bus, s_busName, path, IfaceEventObject, "PropertyChange",
                                                detail, 0, 0, tree?.GetName(change.NodeId) ?? string.Empty);
                        break;
                    case AutomationChangeKind.DescriptionChanged:
                        DBusLite.EmitAtSpiEvent(s_bus, s_busName, path, IfaceEventObject, "PropertyChange",
                                                detail, 0, 0, tree?.GetHelpText(change.NodeId) ?? string.Empty);
                        break;

                    case AutomationChangeKind.ChildAdded:
                    case AutomationChangeKind.ChildRemoved:
                        EmitChildrenChanged(path, detail, change.Index, PathForNode(change.ChildId));
                        break;

                    case AutomationChangeKind.ChildrenChanged:
                        // WPF's BULK notification: more children changed at once than it was willing
                        // to enumerate, so neither the direction nor the index is known. AT-SPI has
                        // no "invalidated" detail, so this is reported as an add at index -1 naming
                        // the parent itself -- which is what a client needs to re-read the subtree,
                        // and the only shape the protocol offers for saying so.
                        EmitChildrenChanged(path, detail, -1, path);
                        break;

                    case AutomationChangeKind.PropertyChanged:
                        DBusLite.EmitAtSpiEvent(s_bus, s_busName, path, IfaceEventObject, "PropertyChange",
                                                detail, 0, 0, tree?.GetName(change.NodeId) ?? string.Empty);
                        break;

                    case AutomationChangeKind.TextChanged:
                        EmitTextChanged(tree, change.NodeId, path);
                        break;
                    case AutomationChangeKind.TextSelectionChanged:
                        EmitTextSelectionChanged(tree, change.NodeId, path);
                        break;

                    // object:selection-changed on the container, and object:visible-data-changed on
                    // the pane that scrolled: neither carries anything, the client re-reads.
                    case AutomationChangeKind.SelectionContainerChanged:
                    case AutomationChangeKind.ScrollChanged:
                    {
                        (string member, string none) = AtSpiRoles.SignalFor(change.Kind);
                        Trace($"event {member} on {path}");
                        DBusLite.EmitAtSpiEvent(s_bus, s_busName, path, IfaceEventObject, member, none, 0, 0);
                        break;
                    }
                }

                DBusLite.Flush(s_bus);
            }
            catch (Exception e)
            {
                Console.WriteLine($"WPF AT-SPI notify failed: {e}");
            }
        }

        private const string IfaceEventObject = "org.a11y.atspi.Event.Object";

        // ---- text signals ----------------------------------------------------------

        /// <summary>
        /// The last text each text node was seen with, and its selection: what a text-changed is
        /// diffed against and a caret move compared with. Bounded, because node ids are not recycled
        /// and a long session visits many fields; dropping the lot costs at most one unannounced edit
        /// in a field nobody has looked at since.
        /// </summary>
        private static readonly System.Collections.Generic.Dictionary<int, string> s_texts = new();
        private static readonly System.Collections.Generic.Dictionary<int, AtSpiText.Selection> s_selections = new();
        private const int CacheLimit = 256;

        private static void Remember<T>(System.Collections.Generic.Dictionary<int, T> cache, int node, T value)
        {
            if (cache.Count >= CacheLimit && !cache.ContainsKey(node)) cache.Clear();
            cache[node] = value;
        }

        /// <summary>A field taking focus is where typing is about to happen: its text and selection
        /// now are the baseline for the first edit and the first caret move.</summary>
        private static void SeedText(IAutomationTreeSource tree, int node)
        {
            if (tree == null) return;
            string text = CurrentText(tree, node);
            if (text == null) return;
            Remember(s_texts, node, text);
            if (tree.TryGetTextSelection(node, out int start, out int length))
                Remember(s_selections, node, new AtSpiText.Selection(Math.Clamp(start, 0, text.Length),
                                                                    Math.Clamp(length, 0, text.Length - Math.Clamp(start, 0, text.Length)), false));
        }

        /// <summary>
        /// object:text-changed:delete and :insert, with the offset, the length and the characters
        /// (AtSpiText.Diff). A node whose text was never seen -- not focused, never read -- has no
        /// baseline, and gets none of either: an insert of its whole text would be spoken as though
        /// the user had typed it, and no client is holding a copy that would need correcting.
        /// </summary>
        private static void EmitTextChanged(IAutomationTreeSource tree, int node, string path)
        {
            if (tree == null) return;
            string now = CurrentText(tree, node);
            if (now == null) { s_texts.Remove(node); return; }

            bool known = s_texts.TryGetValue(node, out string before);
            Remember(s_texts, node, now);
            if (!known) return;

            foreach ((string detail, int offset, int length, string text) in AtSpiText.Diff(before, now))
            {
                Trace($"event TextChanged:{detail} offset={offset} length={length} on {path}");
                DBusLite.EmitAtSpiEvent(s_bus, s_busName, path, IfaceEventObject, "TextChanged", detail, offset, length, text);
            }
        }

        /// <summary>
        /// object:text-caret-moved with the caret's character offset, and object:text-selection-changed
        /// when a range is or was selected (AtSpiText.SelectionSignals). Orca speaks the character or
        /// word the caret moved over from the first, and "selected" / "unselected" from the second.
        /// </summary>
        private static void EmitTextSelectionChanged(IAutomationTreeSource tree, int node, string path)
        {
            if (tree == null) return;
            string text = CurrentText(tree, node);
            if (text == null || !tree.TryGetTextSelection(node, out int start, out int length)) return;
            start = Math.Clamp(start, 0, text.Length);
            length = Math.Clamp(length, 0, text.Length - start);

            AtSpiText.Selection? before = s_selections.TryGetValue(node, out AtSpiText.Selection known) ? known : null;
            (AtSpiText.Selection now, bool caretMoved, bool rangeChanged) = AtSpiText.SelectionSignals(before, start, length);
            Remember(s_selections, node, now);

            if (caretMoved)
            {
                int caret = AtSpiText.ToCodePoints(text, now.Caret);
                Trace($"event TextCaretMoved {caret} on {path}");
                DBusLite.EmitAtSpiEvent(s_bus, s_busName, path, IfaceEventObject, "TextCaretMoved", string.Empty, caret, 0);
            }
            if (rangeChanged)
            {
                Trace($"event TextSelectionChanged on {path}");
                DBusLite.EmitAtSpiEvent(s_bus, s_busName, path, IfaceEventObject, "TextSelectionChanged", string.Empty, 0, 0);
            }
        }

        private static int Has(IAutomationTreeSource tree, int node, AccessibleState state)
            => tree != null && (tree.GetState(node) & state) != 0 ? 1 : 0;

        private static void EmitStateChanged(string path, string state, int value)
        {
            Trace($"event StateChanged {state}={value} on {path}");
            DBusLite.EmitAtSpiEvent(s_bus, s_busName, path, IfaceEventObject, "StateChanged", state, value, 0);
        }

        private static void EmitChildrenChanged(string path, string what, int index, string childPath)
        {
            Trace($"event ChildrenChanged {what} index={index} child={childPath} on {path}");
            DBusLite.EmitAtSpiEventForObject(s_bus, s_busName, path, IfaceEventObject, "ChildrenChanged",
                                             what, index, 0, childPath);
        }
    }
}
