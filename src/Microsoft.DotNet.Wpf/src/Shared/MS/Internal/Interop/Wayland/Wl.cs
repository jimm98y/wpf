// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// The libwayland-client binding: enough of the C API to speak the Wayland wire protocol from
// managed code, with no code generator and no protocol XML on the target machine.
//
// Wayland is unusual among the platform APIs this repo binds. There is no fixed entry point per
// request -- libwayland exposes ONE generic marshaller, wl_proxy_marshal_array_flags, driven by a
// table of `struct wl_interface` descriptors that a C client normally gets by running
// wayland-scanner over the protocol XML at build time. We have neither the scanner nor the XML at
// runtime, so this file provides:
//
//   1. The three native layouts (wl_interface, wl_message, wl_argument), verified against the
//      installed libwayland: sizeof(wl_interface) == 40 with offsets 0/8/12/16/24/32, and
//      sizeof(wl_message) == 24. C#'s LayoutKind.Sequential reproduces both exactly.
//   2. Resolution of the ~22 CORE interface descriptors (wl_compositor, wl_surface, wl_seat, ...)
//      by SYMBOL from libwayland-client.so.0, which exports them as data. Those we never author --
//      taking the library's own tables removes an entire class of transcription error, and keeps
//      us automatically in step with whatever libwayland version is installed.
//   3. A builder that emits native wl_interface tables for the protocols libwayland does NOT
//      export (xdg-shell and friends; see WlProtocols.cs), from a declarative managed description.
//
// Why wl_proxy_marshal_array_flags and never wl_proxy_marshal_flags: the latter is VARIADIC.
// On aarch64 the calling convention for variadic arguments differs from fixed ones (Apple's ABI
// puts them on the stack; the AAPCS rules for register assignment differ), and a P/Invoke
// declaration cannot express "variadic from argument N". The array form takes a union
// wl_argument* and is a normal fixed-arity function -- correct on every architecture.
//

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace MS.Internal.Interop.Wayland
{
    /// <summary>A wl_argument: the 8-byte union libwayland uses for every request/event argument.</summary>
    [StructLayout(LayoutKind.Explicit, Size = 8)]
    internal struct WlArgument
    {
        [FieldOffset(0)] public int i;      // int32_t, and wl_fixed_t
        [FieldOffset(0)] public uint u;     // uint32_t, and new_id
        [FieldOffset(0)] public IntPtr p;   // const char* (s), wl_object* (o), wl_array* (a)

        public static WlArgument Int(int v) => new WlArgument { p = IntPtr.Zero, i = v };
        public static WlArgument UInt(uint v) => new WlArgument { p = IntPtr.Zero, u = v };
        public static WlArgument Ptr(IntPtr v) => new WlArgument { p = v };
        /// <summary>A new_id slot. The VALUE is ignored -- libwayland fills in the id -- but the
        /// slot must exist or the argument array is short and the marshaller reads past its end.</summary>
        public static WlArgument NewId() => new WlArgument { p = IntPtr.Zero };
    }

    /// <summary>struct wl_message { const char *name; const char *signature; const wl_interface **types; }</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct WlMessage
    {
        public IntPtr name;
        public IntPtr signature;
        public IntPtr types;
    }

    /// <summary>
    /// struct wl_interface. Verified layout: size 40, name@0, version@8, method_count@12,
    /// methods@16, event_count@24, events@32.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct WlInterface
    {
        public IntPtr name;
        public int version;
        public int method_count;
        public IntPtr methods;
        public int event_count;
        public IntPtr events;
    }

    /// <summary>A request or event in the declarative protocol description (see WlProtocols).</summary>
    internal sealed class WlMsgDef
    {
        public readonly string Name;
        public readonly string Signature;
        /// <summary>One entry per ARGUMENT, null where the argument is not an object/new_id.
        /// Entries name another interface ("wl_surface", "xdg_popup", ...).</summary>
        public readonly string?[] Types;

        public WlMsgDef(string name, string signature, params string?[] types)
        {
            Name = name;
            Signature = signature;
            Types = types ?? Array.Empty<string?>();
        }
    }

    /// <summary>A protocol interface in the declarative description.</summary>
    internal sealed class WlInterfaceDef
    {
        public readonly string Name;
        public readonly int Version;
        public readonly WlMsgDef[] Requests;
        public readonly WlMsgDef[] Events;

        public WlInterfaceDef(string name, int version, WlMsgDef[] requests, WlMsgDef[] events)
        {
            Name = name;
            Version = version;
            Requests = requests;
            Events = events;
        }
    }

    [SupportedOSPlatform("linux")]
    internal static unsafe class Wl
    {
        internal const string LibWaylandClient = "libwayland-client.so.0";

        // wl_proxy_marshal_array_flags flags
        internal const uint MARSHAL_FLAG_DESTROY = 1;

        // ---- libwayland-client -------------------------------------------------------------

        [DllImport(LibWaylandClient)] internal static extern IntPtr wl_display_connect(string? name);
        [DllImport(LibWaylandClient)] internal static extern void wl_display_disconnect(IntPtr display);
        [DllImport(LibWaylandClient)] internal static extern int wl_display_get_fd(IntPtr display);
        [DllImport(LibWaylandClient)] internal static extern int wl_display_dispatch(IntPtr display);
        [DllImport(LibWaylandClient)] internal static extern int wl_display_dispatch_pending(IntPtr display);
        [DllImport(LibWaylandClient)] internal static extern int wl_display_flush(IntPtr display);
        [DllImport(LibWaylandClient)] internal static extern int wl_display_roundtrip(IntPtr display);
        [DllImport(LibWaylandClient)] internal static extern int wl_display_prepare_read(IntPtr display);
        [DllImport(LibWaylandClient)] internal static extern int wl_display_read_events(IntPtr display);
        [DllImport(LibWaylandClient)] internal static extern void wl_display_cancel_read(IntPtr display);
        [DllImport(LibWaylandClient)] internal static extern int wl_display_get_error(IntPtr display);

        [DllImport(LibWaylandClient)]
        internal static extern IntPtr wl_proxy_marshal_array_flags(
            IntPtr proxy, uint opcode, IntPtr iface, uint version, uint flags, WlArgument* args);

        [DllImport(LibWaylandClient)]
        internal static extern int wl_proxy_add_listener(IntPtr proxy, IntPtr* implementation, IntPtr data);

        [DllImport(LibWaylandClient)] internal static extern void wl_proxy_destroy(IntPtr proxy);
        [DllImport(LibWaylandClient)] internal static extern uint wl_proxy_get_version(IntPtr proxy);

        // ---- Core interface descriptors, resolved by symbol --------------------------------

        private static IntPtr s_lib;
        private static readonly Dictionary<string, IntPtr> s_coreInterfaces = new(StringComparer.Ordinal);
        private static readonly object s_lock = new object();

        /// <summary>
        /// The address of a wl_interface libwayland-client exports as data (wl_registry_interface,
        /// wl_surface_interface, ...). Returns Zero for a name it does not export -- every protocol
        /// beyond the core one, which WlProtocols authors instead.
        /// </summary>
        internal static IntPtr CoreInterface(string interfaceName)
        {
            lock (s_lock)
            {
                if (s_coreInterfaces.TryGetValue(interfaceName, out IntPtr cached))
                    return cached;

                if (s_lib == IntPtr.Zero && !NativeLibrary.TryLoad(LibWaylandClient, out s_lib))
                {
                    s_coreInterfaces[interfaceName] = IntPtr.Zero;
                    return IntPtr.Zero;
                }

                NativeLibrary.TryGetExport(s_lib, interfaceName + "_interface", out IntPtr addr);
                s_coreInterfaces[interfaceName] = addr;
                return addr;
            }
        }

        // ---- Building wl_interface tables for non-core protocols ---------------------------

        private static readonly Dictionary<string, IntPtr> s_builtInterfaces = new(StringComparer.Ordinal);

        /// <summary>
        /// The address of the wl_interface for <paramref name="name"/>: libwayland's own where it
        /// exports one, otherwise the table built from <see cref="WlProtocols"/>. This is the single
        /// lookup every request and every types[] entry goes through, so a core and an authored
        /// interface are indistinguishable to callers.
        /// </summary>
        internal static IntPtr Interface(string name)
        {
            IntPtr core = CoreInterface(name);
            if (core != IntPtr.Zero) return core;

            lock (s_lock)
            {
                EnsureTablesBuiltLocked();
                // A name with no table -- a protocol we deliberately do not bind, reachable only
                // through a request we never send -- resolves to NULL, which is exactly what
                // wayland-scanner emits for a type it was not given. Note that this is safe only
                // for REQUESTS: an EVENT whose argument is a new_id has libwayland construct the
                // proxy during demarshalling, and a null interface there is fatal.
                return s_builtInterfaces.TryGetValue(name, out IntPtr built) ? built : IntPtr.Zero;
            }
        }

        private static bool s_tablesBuilt;

        /// <summary>
        /// Emits native wl_interface tables for every authored protocol at once. They reference each
        /// other (xdg_surface.get_popup names xdg_positioner and xdg_popup), so every address must
        /// exist before any types[] array is filled -- hence a two-pass build: allocate all the
        /// wl_interface structs first, then populate their message tables. Caller holds s_lock.
        ///
        /// The flag is set BEFORE building, not after. Populating a types[] array calls back into
        /// Interface(), so a name with no table would otherwise re-enter here, find the interface
        /// currently being populated still looking empty, and rebuild it -- recursing until the
        /// stack is gone.
        /// </summary>
        private static void EnsureTablesBuiltLocked()
        {
            if (s_tablesBuilt) return;
            s_tablesBuilt = true;

            WlInterfaceDef[] defs = WlProtocols.All;

            // Pass 1: allocate an (empty) wl_interface for each, so cross-references can resolve.
            foreach (WlInterfaceDef d in defs)
            {
                var p = (WlInterface*)NativeMemory.AllocZeroed((nuint)sizeof(WlInterface));
                p->name = Utf8(d.Name);
                p->version = d.Version;
                s_builtInterfaces[d.Name] = (IntPtr)p;
            }

            // Pass 2: fill in the message tables.
            foreach (WlInterfaceDef d in defs)
            {
                var p = (WlInterface*)s_builtInterfaces[d.Name];
                p->method_count = d.Requests.Length;
                p->methods = BuildMessages(d.Name, d.Requests);
                p->event_count = d.Events.Length;
                p->events = BuildMessages(d.Name, d.Events);
            }
        }

        private static IntPtr BuildMessages(string owner, WlMsgDef[] defs)
        {
            if (defs.Length == 0) return IntPtr.Zero;

            var msgs = (WlMessage*)NativeMemory.AllocZeroed((nuint)(sizeof(WlMessage) * defs.Length));
            for (int i = 0; i < defs.Length; i++)
            {
                WlMsgDef d = defs[i];
                int argCount = CountArguments(d.Signature);

                // Fail LOUDLY at startup rather than handing the marshaller a short types[] array,
                // which it indexes by argument position -- a mismatch there is an out-of-bounds read
                // inside libwayland and shows up as an unrelated protocol error much later.
                if (d.Types.Length != argCount)
                {
                    throw new InvalidOperationException(
                        $"Wayland protocol table for {owner}.{d.Name}: signature '{d.Signature}' has " +
                        $"{argCount} argument(s) but {d.Types.Length} type slot(s) were declared.");
                }

                msgs[i].name = Utf8(d.Name);
                msgs[i].signature = Utf8(d.Signature);

                var types = (IntPtr*)NativeMemory.AllocZeroed((nuint)(IntPtr.Size * Math.Max(1, argCount)));
                for (int a = 0; a < argCount; a++)
                    types[a] = d.Types[a] is null ? IntPtr.Zero : Interface(d.Types[a]!);
                msgs[i].types = (IntPtr)types;
            }
            return (IntPtr)msgs;
        }

        /// <summary>
        /// The number of ARGUMENTS a wl_message signature declares. The grammar has two things that
        /// are not arguments and are easy to miscount: a leading run of digits is the "since" version
        /// (wl_surface.set_buffer_scale is "3i" -- since 3, one int), and '?' marks the FOLLOWING
        /// argument nullable (wl_surface.attach is "?oii" -- three arguments).
        /// </summary>
        internal static int CountArguments(string signature)
        {
            int n = 0;
            foreach (char c in signature)
            {
                if (c is >= '0' and <= '9') continue;   // since-version prefix
                if (c == '?') continue;                 // nullable marker
                n++;
            }
            return n;
        }

        /// <summary>A NUL-terminated UTF-8 copy in native memory, never freed (process lifetime).</summary>
        internal static IntPtr Utf8(string s)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(s);
            var p = (byte*)NativeMemory.Alloc((nuint)(bytes.Length + 1));
            for (int i = 0; i < bytes.Length; i++) p[i] = bytes[i];
            p[bytes.Length] = 0;
            return (IntPtr)p;
        }

        internal static string FromUtf8(IntPtr p) => p == IntPtr.Zero ? string.Empty : (Marshal.PtrToStringUTF8(p) ?? string.Empty);

        /// <summary>Builds a native vtable of C function pointers for wl_proxy_add_listener. The order
        /// MUST match the interface's event declaration order.</summary>
        internal static IntPtr* Vtable(params IntPtr[] handlers)
        {
            var v = (IntPtr*)NativeMemory.AllocZeroed((nuint)(IntPtr.Size * handlers.Length));
            for (int i = 0; i < handlers.Length; i++) v[i] = handlers[i];
            return v;
        }

        /// <summary>
        /// The same, checked against the interface the listener will be attached to.
        /// </summary>
        /// <remarks>
        /// libwayland indexes this array by EVENT OPCODE and is never told how long it is: one
        /// handler short of the interface's event count and an event past the end reads whatever
        /// follows the allocation and calls it. That is the same class of mistake BuildMessages
        /// catches for types[], and it deserves the same loud failure at startup.
        ///
        /// Only interfaces from <see cref="WlProtocols"/> are checked. A CORE interface's event count
        /// comes from whatever libwayland is installed, and a newer one may well declare an event
        /// this code predates -- refusing to start over that would be a worse bug than the one being
        /// guarded against, since the compositor cannot send an event above the version we bound.
        /// </remarks>
        internal static IntPtr* Vtable(string interfaceName, params IntPtr[] handlers)
        {
            lock (s_lock)
            {
                EnsureTablesBuiltLocked();
                if (s_builtInterfaces.TryGetValue(interfaceName, out IntPtr authored) && authored != IntPtr.Zero)
                {
                    int declared = ((WlInterface*)authored)->event_count;
                    if (declared != handlers.Length)
                    {
                        throw new InvalidOperationException(
                            $"Wayland listener for {interfaceName}: {handlers.Length} handler(s) for " +
                            $"{declared} declared event(s). The vtable is indexed by event opcode.");
                    }
                }
            }

            return Vtable(handlers);
        }

        // ---- Request helpers ---------------------------------------------------------------

        /// <summary>Send a request that returns nothing.</summary>
        internal static void Request(IntPtr proxy, uint opcode, params WlArgument[] args)
        {
            fixed (WlArgument* a = args)
                wl_proxy_marshal_array_flags(proxy, opcode, IntPtr.Zero, wl_proxy_get_version(proxy), 0, a);
        }

        /// <summary>Send a request with no arguments.</summary>
        internal static void Request(IntPtr proxy, uint opcode)
        {
            WlArgument none = default;
            wl_proxy_marshal_array_flags(proxy, opcode, IntPtr.Zero, wl_proxy_get_version(proxy), 0, &none);
        }

        /// <summary>Send a destructor request (.destroy()), which also frees the proxy.</summary>
        internal static void Destroy(IntPtr proxy, uint opcode)
        {
            WlArgument none = default;
            wl_proxy_marshal_array_flags(proxy, opcode, IntPtr.Zero, wl_proxy_get_version(proxy), MARSHAL_FLAG_DESTROY, &none);
        }

        /// <summary>
        /// Send a constructor request and return the new proxy. <paramref name="args"/> must already
        /// contain the (ignored-value) new_id slot in its protocol position.
        /// </summary>
        internal static IntPtr Construct(IntPtr proxy, uint opcode, string newInterface, uint version, params WlArgument[] args)
        {
            fixed (WlArgument* a = args)
                return wl_proxy_marshal_array_flags(proxy, opcode, Interface(newInterface), version, 0, a);
        }
    }
}
