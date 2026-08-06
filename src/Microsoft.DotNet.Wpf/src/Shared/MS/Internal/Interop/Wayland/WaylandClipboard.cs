// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// The Wayland clipboard (wl_data_device selection), in the same shape as MacClipboard so
// PlatformClipboard can dispatch to either.
//
// Wayland's clipboard is peer-to-peer rather than a store. There is no "put text on the clipboard"
// call: an application ADVERTISES a wl_data_source with a list of MIME types, and when some other
// client pastes, the compositor hands the owner a file descriptor to write the bytes into. Reading
// is the mirror image -- the compositor pushes a wl_data_offer describing what is available, and
// the content is fetched through a pipe.
//
// Two consequences are behaviour differences worth stating rather than hiding:
//
//   1. SET REQUIRES A RECENT INPUT SERIAL. wl_data_device.set_selection is validated against a
//      serial from real user input, so a Clipboard.SetText called at startup -- before the user has
//      touched the window -- is silently ignored by the compositor. Here it is deferred until the
//      first input event rather than dropped.
//
//   2. CLIPBOARD CONTENT DIES WITH THE PROCESS. Because the owner serves the bytes on demand, there
//      is nothing left once it exits. GNOME ships no clipboard manager for wl_data_device, so
//      copying and then quitting loses the content -- unlike Windows and macOS, where the system
//      owns it. This is Wayland's design, not a gap in this implementation.
//

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using static MS.Internal.Interop.Wayland.WlProtocols;

namespace MS.Internal.Interop.Wayland
{
    [SupportedOSPlatform("linux")]
    internal static unsafe class WaylandClipboard
    {
        internal const string TypeString = "text/plain;charset=utf-8";
        internal const string TypePng = "image/png";

        // Offered alongside the canonical type, because older clients ask for these spellings.
        private static readonly string[] TextTypes =
        {
            "text/plain;charset=utf-8",
            "text/plain",
            "UTF8_STRING",
            "STRING",
            "TEXT",
        };

        private static IntPtr s_dataDevice;
        private static IntPtr* s_dataDeviceListener;
        private static IntPtr* s_dataOfferListener;
        private static IntPtr* s_dataSourceListener;

        // The offer the compositor most recently announced as the selection, and what it can supply.
        private static IntPtr s_currentOffer;
        private static readonly HashSet<string> s_currentTypes = new(StringComparer.Ordinal);

        // What we are currently offering, kept alive because the compositor calls back into it.
        private static IntPtr s_source;
        private static byte[]? s_sourceBytes;
        private static string[]? s_sourceTypes;
        private static byte[]? s_deferredBytes;
        private static string[]? s_deferredTypes;

        internal static bool IsAvailable => WaylandDisplay.IsActive && WaylandDisplay.DataDeviceManager != IntPtr.Zero;

        private static void EnsureDataDevice()
        {
            if (s_dataDevice != IntPtr.Zero || !IsAvailable || WaylandDisplay.Seat == IntPtr.Zero) return;

            s_dataDeviceListener = Wl.Vtable(
                (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, void>)&OnDataOffer,
                (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, uint, int, int, IntPtr, void>)&OnEnter,
                (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, void>)&OnLeave,
                (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, uint, int, int, void>)&OnMotion,
                (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, void>)&OnDrop,
                (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, void>)&OnSelection);

            s_dataOfferListener = Wl.Vtable(
                (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, void>)&OnOfferMimeType,
                (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, uint, void>)&OnOfferSourceActions,
                (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, uint, void>)&OnOfferAction);

            s_dataSourceListener = Wl.Vtable(
                (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, void>)&OnSourceTarget,
                (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, int, void>)&OnSourceSend,
                (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, void>)&OnSourceCancelled,
                (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, void>)&OnSourceDndDropPerformed,
                (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, void>)&OnSourceDndFinished,
                (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, uint, void>)&OnSourceAction);

            IntPtr manager = WaylandDisplay.DataDeviceManager;
            s_dataDevice = Wl.Construct(manager, WL_DATA_DEVICE_MANAGER_GET_DATA_DEVICE, "wl_data_device",
                Wl.wl_proxy_get_version(manager), WlArgument.NewId(), WlArgument.Ptr(WaylandDisplay.Seat));
            Wl.wl_proxy_add_listener(s_dataDevice, s_dataDeviceListener, IntPtr.Zero);
            WaylandDisplay.Flush();
        }

        // ---- Reading -------------------------------------------------------------------------

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static void OnDataOffer(IntPtr data, IntPtr device, IntPtr offer)
        {
            try
            {
                // A brand-new offer: attach a listener so its MIME types are collected before the
                // selection event names it.
                Wl.wl_proxy_add_listener(offer, s_dataOfferListener, IntPtr.Zero);
            }
            catch { }
        }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static void OnOfferMimeType(IntPtr data, IntPtr offer, IntPtr mimeType)
        {
            try
            {
                // Types arrive before the selection event, so accumulate against the pending offer.
                s_pendingTypes.Add(Wl.FromUtf8(mimeType));
            }
            catch { }
        }
        private static readonly HashSet<string> s_pendingTypes = new(StringComparer.Ordinal);

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static void OnOfferSourceActions(IntPtr data, IntPtr offer, uint actions) { }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static void OnOfferAction(IntPtr data, IntPtr offer, uint action) { }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static void OnSelection(IntPtr data, IntPtr device, IntPtr offer)
        {
            try
            {
                if (s_currentOffer != IntPtr.Zero && s_currentOffer != offer)
                {
                    Wl.Destroy(s_currentOffer, WL_DATA_OFFER_DESTROY);
                }
                s_currentOffer = offer;
                s_currentTypes.Clear();
                foreach (string t in s_pendingTypes) s_currentTypes.Add(t);
                s_pendingTypes.Clear();
            }
            catch { }
        }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static void OnEnter(IntPtr data, IntPtr device, uint serial, int x, int y, IntPtr offer) { }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static void OnLeave(IntPtr data, IntPtr device) { }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static void OnMotion(IntPtr data, IntPtr device, uint time, int x, int y) { }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static void OnDrop(IntPtr data, IntPtr device) { }

        [DllImport("libc", SetLastError = true)] private static extern int pipe2(int* fds, int flags);
        [DllImport("libc", SetLastError = true)] private static extern nint read(int fd, byte* buf, nuint count);
        [DllImport("libc", SetLastError = true)] private static extern nint write(int fd, byte* buf, nuint count);
        [DllImport("libc", SetLastError = true)] private static extern int close(int fd);

        private const int O_CLOEXEC = 0x80000;

        /// <summary>
        /// Fetch the current selection as bytes for the first matching MIME type.
        ///
        /// The read loop PUMPS THE DISPLAY between attempts, which is not an optimisation: when this
        /// process is itself the clipboard owner, the compositor asks us to write the data, and that
        /// request only arrives if we are dispatching. A plain blocking read would deadlock the app
        /// against itself on every self-paste.
        /// </summary>
        private static byte[]? Receive(string[] mimeTypes)
        {
            EnsureDataDevice();
            if (s_currentOffer == IntPtr.Zero) return null;

            string? chosen = null;
            foreach (string t in mimeTypes)
            {
                if (s_currentTypes.Contains(t)) { chosen = t; break; }
            }
            if (chosen is null) return null;

            int* fds = stackalloc int[2];
            if (pipe2(fds, O_CLOEXEC) != 0) return null;
            int readFd = fds[0], writeFd = fds[1];

            try
            {
                IntPtr mime = Wl.Utf8(chosen);
                Wl.Request(s_currentOffer, WL_DATA_OFFER_RECEIVE, WlArgument.Ptr(mime), WlArgument.Int(writeFd));
                WaylandDisplay.Flush();

                // The writer end must be closed HERE, or the read below never sees EOF: this process
                // would still hold it open and wait for itself.
                close(writeFd);
                writeFd = -1;

                var buffer = new List<byte>();
                byte* chunk = stackalloc byte[4096];
                DateTime deadline = DateTime.UtcNow.AddSeconds(2);
                while (DateTime.UtcNow < deadline)
                {
                    nint n = read(readFd, chunk, 4096);
                    if (n > 0)
                    {
                        for (nint i = 0; i < n; i++) buffer.Add(chunk[i]);
                        continue;
                    }
                    if (n == 0) break;                                  // EOF: complete
                    if (Marshal.GetLastPInvokeError() is 11 or 4)       // EAGAIN / EINTR
                    {
                        WaylandDisplay.ReadEvents(8);                   // let the owner (maybe us) write
                        continue;
                    }
                    break;
                }
                return buffer.ToArray();
            }
            finally
            {
                if (writeFd >= 0) close(writeFd);
                close(readFd);
            }
        }

        // ---- Writing -------------------------------------------------------------------------

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static void OnSourceSend(IntPtr data, IntPtr source, IntPtr mimeType, int fd)
        {
            try
            {
                byte[]? bytes = s_sourceBytes;
                if (bytes is not null)
                {
                    fixed (byte* p = bytes)
                    {
                        int offset = 0;
                        while (offset < bytes.Length)
                        {
                            nint n = write(fd, p + offset, (nuint)(bytes.Length - offset));
                            if (n <= 0) break;      // the reader gave up
                            offset += (int)n;
                        }
                    }
                }
            }
            catch { }
            finally
            {
                // Always close: the reader waits for EOF, so leaking this hangs the pasting app.
                try { close(fd); } catch { }
            }
        }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static void OnSourceCancelled(IntPtr data, IntPtr source)
        {
            try
            {
                // Another client took the selection; our source is dead.
                if (s_source == source)
                {
                    s_source = IntPtr.Zero;
                    s_sourceBytes = null;
                    s_sourceTypes = null;
                }
                Wl.Destroy(source, WL_DATA_SOURCE_DESTROY);
            }
            catch { }
        }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static void OnSourceTarget(IntPtr data, IntPtr source, IntPtr mimeType) { }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static void OnSourceDndDropPerformed(IntPtr data, IntPtr source) { }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static void OnSourceDndFinished(IntPtr data, IntPtr source) { }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static void OnSourceAction(IntPtr data, IntPtr source, uint action) { }

        private static void Offer(byte[] bytes, string[] mimeTypes)
        {
            EnsureDataDevice();
            if (s_dataDevice == IntPtr.Zero) return;

            uint serial = WaylandInput.LastInputSerial;
            if (serial == 0)
            {
                // No input yet: the compositor would reject this. Remember it and publish on the
                // first real event rather than losing the caller's data silently.
                s_deferredBytes = bytes;
                s_deferredTypes = mimeTypes;
                WaylandDisplay.LogSink?.Invoke("clipboard set before any input; deferred until the first event.");
                return;
            }

            IntPtr manager = WaylandDisplay.DataDeviceManager;
            IntPtr source = Wl.Construct(manager, WL_DATA_DEVICE_MANAGER_CREATE_DATA_SOURCE, "wl_data_source",
                Wl.wl_proxy_get_version(manager), WlArgument.NewId());
            if (source == IntPtr.Zero) return;

            Wl.wl_proxy_add_listener(source, s_dataSourceListener, IntPtr.Zero);
            foreach (string t in mimeTypes)
            {
                Wl.Request(source, WL_DATA_SOURCE_OFFER, WlArgument.Ptr(Wl.Utf8(t)));
            }

            s_source = source;
            s_sourceBytes = bytes;
            s_sourceTypes = mimeTypes;

            Wl.Request(s_dataDevice, WL_DATA_DEVICE_SET_SELECTION, WlArgument.Ptr(source), WlArgument.UInt(serial));
            WaylandDisplay.Flush();
        }

        /// <summary>Publish anything that was set before the first input event (see Offer).</summary>
        internal static void FlushDeferred()
        {
            if (s_deferredBytes is null || WaylandInput.LastInputSerial == 0) return;
            byte[] bytes = s_deferredBytes;
            string[] types = s_deferredTypes ?? TextTypes;
            s_deferredBytes = null;
            s_deferredTypes = null;
            Offer(bytes, types);
        }

        // ---- The MacClipboard-shaped surface ---------------------------------------------------

        internal static void Clear()
        {
            EnsureDataDevice();
            if (s_dataDevice == IntPtr.Zero) return;
            uint serial = WaylandInput.LastInputSerial;
            if (serial == 0) return;
            // A null source clears the selection.
            Wl.Request(s_dataDevice, WL_DATA_DEVICE_SET_SELECTION, WlArgument.Ptr(IntPtr.Zero), WlArgument.UInt(serial));
            WaylandDisplay.Flush();
            s_source = IntPtr.Zero;
            s_sourceBytes = null;
        }

        internal static void SetString(string value) => Offer(Encoding.UTF8.GetBytes(value ?? string.Empty), TextTypes);

        internal static string? GetString()
        {
            byte[]? bytes = Receive(TextTypes);
            return bytes is null ? null : Encoding.UTF8.GetString(bytes);
        }

        /// <summary>
        /// Costs no round trip: the compositor already told us what the current selection offers, so
        /// this is a set lookup rather than a full fetch (which is what MacClipboard has to do).
        /// </summary>
        internal static bool ContainsString()
        {
            EnsureDataDevice();
            foreach (string t in TextTypes)
            {
                if (s_currentTypes.Contains(t)) return true;
            }
            return false;
        }

        internal static void SetData(string type, byte[] data) => Offer(data ?? Array.Empty<byte>(), new[] { type });

        internal static byte[]? GetData(string type) => Receive(new[] { type });

        internal static bool ContainsData(string type)
        {
            EnsureDataDevice();
            return s_currentTypes.Contains(type);
        }
    }
}
