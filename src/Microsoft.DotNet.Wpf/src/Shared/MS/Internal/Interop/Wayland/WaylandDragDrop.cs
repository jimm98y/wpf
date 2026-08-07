// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// Wayland drag-and-drop, both directions, over the wl_data_device this process already opened for the
// clipboard. The selection and drag halves of wl_data_device share one listener, so the DnD events land
// in WaylandClipboard and are forwarded straight here.
//
// The WPF side is reached through MS.Internal.Interop.PlatformDragDrop: this file speaks protocol and
// knows nothing about visual trees, and PresentationCore's target does the hit-testing and raises the
// routed events. See PlatformDragDrop.cs for why the dependency runs that way.
//
// Three protocol obligations that are easy to miss, and fatal to get wrong:
//
//   1. ACCEPT AND SET_ACTIONS ARE NOT OPTIONAL. The source decides what its cursor shows, and it is
//      told by wl_data_offer.accept + set_actions. Skipping them leaves the drag showing "no drop"
//      even over a target that would accept it, and some sources refuse to send data at all.
//   2. FINISH BEFORE DESTROY, AND ONLY AFTER A SUCCESSFUL DROP. wl_data_offer.finish on an offer that
//      was not dropped is a protocol error, which kills the whole connection -- not just the drag.
//   3. VERSION-GATE finish/set_actions. They arrived in wl_data_device_manager v3. Sending them to an
//      older object is, again, a fatal protocol error rather than a no-op.
//
// The source half additionally needs a serial from a real button press (WaylandInput.LastInputSerial).
// The compositor validates it, so a drag cannot be started out of nowhere -- which is correct, and is
// why a programmatic DoDragDrop with no button held returns None rather than starting anything.
//

#nullable enable

using System;
using System.Collections.Generic;
using System.Windows.Threading;
using static MS.Internal.Interop.Wayland.WlProtocols;

namespace MS.Internal.Interop.Wayland
{
    internal static unsafe class WaylandDragDrop
    {
        // ---- target (a drag over our window) ----
        private static IntPtr s_offer;
        private static string[] s_offerTypes = Array.Empty<string>();
        private static IntPtr s_surface;
        private static uint s_enterSerial;
        private static double s_x, s_y;
        private static uint s_sourceActions;
        private static int s_lastEffect;

        // ---- source (a drag we started) ----
        private static IntPtr s_dragSource;
        private static Func<string, byte[]?>? s_dragWriter;
        private static string[] s_dragTypes = Array.Empty<string>();
        private static bool s_dragFinished;
        private static uint s_dragAction;

        internal static bool IsDragSourceActive => s_dragSource != IntPtr.Zero;

        // WPF's DragDropEffects. Duplicated as constants rather than referenced, because this assembly
        // is below PresentationCore where that enum lives.
        private const int EffectNone = 0;
        private const int EffectCopy = 1;
        private const int EffectMove = 2;
        private const int EffectLink = 4;

        private static void Log(string m) => WaylandDisplay.LogSink?.Invoke("dnd: " + m);

        // ------------------------------------------------------------------ target ----

        internal static void Enter(uint serial, IntPtr surface, int x, int y, IntPtr offer, string[] types)
        {
            DestroyOffer();

            s_offer = offer;
            s_offerTypes = types;
            s_surface = surface;
            s_enterSerial = serial;
            s_x = x / 256.0;
            s_y = y / 256.0;
            s_sourceActions = 0;
            s_lastEffect = EffectNone;

            if (offer == IntPtr.Zero) return;
            Log($"enter surface=0x{surface:x} at {s_x:0},{s_y:0} types=[{string.Join(",", types)}]");

            IPlatformDropTarget? target = PlatformDragDrop.Target;
            if (target is null) { Accept(EffectNone); return; }

            ToScreen(out int sx, out int sy);
            s_lastEffect = Guard(() => target.DragEnter(surface, sx, sy, s_offerTypes, ReadType, AllowedEffects()));
            Accept(s_lastEffect);
        }

        internal static void Motion(int x, int y)
        {
            if (s_offer == IntPtr.Zero) return;
            s_x = x / 256.0;
            s_y = y / 256.0;

            IPlatformDropTarget? target = PlatformDragDrop.Target;
            if (target is null) return;

            ToScreen(out int sx, out int sy);
            s_lastEffect = Guard(() => target.DragOver(s_surface, sx, sy, AllowedEffects()));
            Accept(s_lastEffect);
        }

        internal static void Leave()
        {
            if (s_offer == IntPtr.Zero) return;
            Log("leave");
            IPlatformDropTarget? target = PlatformDragDrop.Target;
            if (target is not null) Guard(() => { target.DragLeave(s_surface); return 0; });
            DestroyOffer();
        }

        internal static void Drop()
        {
            if (s_offer == IntPtr.Zero) return;
            Log("drop");

            IPlatformDropTarget? target = PlatformDragDrop.Target;
            int effect = EffectNone;
            if (target is not null)
            {
                ToScreen(out int sx, out int sy);
                effect = Guard(() => target.Drop(s_surface, sx, sy, AllowedEffects()));
            }

            // finish is only legal after a drop the target actually accepted, and only from v3.
            if (effect != EffectNone && Wl.wl_proxy_get_version(s_offer) >= 3)
            {
                Wl.Request(s_offer, WL_DATA_OFFER_FINISH);
            }
            WaylandDisplay.Flush();
            DestroyOffer();
        }

        internal static void SourceActions(uint actions)
        {
            s_sourceActions = actions;
            // The allowed set changed, so the effect the target picked may no longer be valid.
            if (s_offer != IntPtr.Zero) Accept(s_lastEffect);
        }

        /// <summary>
        /// Tell the source what would happen on drop. This is what drives ITS cursor, so it has to be
        /// re-sent whenever the answer changes, not just once on enter.
        /// </summary>
        private static void Accept(int effect)
        {
            if (s_offer == IntPtr.Zero) return;

            string? mime = effect == EffectNone ? null : PreferredType();
            Wl.Request(s_offer, WL_DATA_OFFER_ACCEPT,
                       WlArgument.UInt(s_enterSerial),
                       WlArgument.Ptr(mime is null ? IntPtr.Zero : Wl.Utf8(mime)));

            if (Wl.wl_proxy_get_version(s_offer) >= 3)
            {
                uint action = ToWaylandAction(effect);
                Wl.Request(s_offer, WL_DATA_OFFER_SET_ACTIONS,
                           WlArgument.UInt(action), WlArgument.UInt(action));
            }
            WaylandDisplay.Flush();
        }

        private static string? PreferredType() => s_offerTypes.Length > 0 ? s_offerTypes[0] : null;

        private static byte[]? ReadType(string mime) =>
            s_offer == IntPtr.Zero ? null : WaylandClipboard.ReceiveFrom(s_offer, mime);

        /// <summary>What the SOURCE permits, which bounds whatever the target may choose.</summary>
        private static int AllowedEffects()
        {
            if (s_sourceActions == 0) return EffectCopy | EffectMove | EffectLink;
            int effects = EffectNone;
            if ((s_sourceActions & WL_DND_ACTION_COPY) != 0) effects |= EffectCopy | EffectLink;
            if ((s_sourceActions & WL_DND_ACTION_MOVE) != 0) effects |= EffectMove;
            return effects;
        }

        private static uint ToWaylandAction(int effect)
        {
            // Wayland has no "link", so it rides on copy -- the data is duplicated either way, and the
            // alternative is telling the source "none" and getting no drag at all.
            if ((effect & EffectMove) != 0) return WL_DND_ACTION_MOVE;
            if ((effect & (EffectCopy | EffectLink)) != 0) return WL_DND_ACTION_COPY;
            return WL_DND_ACTION_NONE;
        }

        private static int FromWaylandAction(uint action)
        {
            if ((action & WL_DND_ACTION_MOVE) != 0) return EffectMove;
            if ((action & WL_DND_ACTION_COPY) != 0) return EffectCopy;
            return EffectNone;
        }

        /// <summary>
        /// Surface-local logical coordinates to the screen device pixels PointUtil.ScreenToClient
        /// expects. Off Windows that subtracts the window's client screen origin, so adding the same
        /// origin here makes the round trip exact -- and identical to the mouse path's basis.
        /// </summary>
        private static void ToScreen(out int screenX, out int screenY)
        {
            double scale = WaylandDisplay.ScaleForSurface(s_surface);
            int clientX = (int)Math.Round(s_x * scale);
            int clientY = (int)Math.Round(s_y * scale);

            screenX = clientX;
            screenY = clientY;
            IPlatformWindow? window = PlatformWindow.FromHandle(s_surface);
            if (window is null) return;
            window.GetClientScreenOriginPixels(out int originX, out int originY);
            screenX += originX;
            screenY += originY;
        }

        private static void DestroyOffer()
        {
            if (s_offer == IntPtr.Zero) return;
            try { Wl.Destroy(s_offer, WL_DATA_OFFER_DESTROY); } catch { }
            s_offer = IntPtr.Zero;
            s_offerTypes = Array.Empty<string>();
            s_surface = IntPtr.Zero;
            s_lastEffect = EffectNone;
        }

        /// <summary>
        /// Run WPF's side of a drag event without letting it escape into libwayland. An exception
        /// crossing back into a C callback is undefined behaviour, so a broken drop handler must cost
        /// the drop and nothing more.
        /// </summary>
        private static int Guard(Func<int> body)
        {
            try { return body(); }
            catch (Exception e) { Log("target threw: " + e.Message); return EffectNone; }
        }

        // ------------------------------------------------------------------ source ----

        /// <summary>
        /// Start a drag from <paramref name="originSurface"/> and run it to completion, returning the
        /// effect that was performed. Blocks on a nested dispatcher frame, which is how DoDragDrop
        /// behaves on Windows too -- and works here only because Linux keeps the blocking PushFrame.
        /// </summary>
        internal static int StartDrag(IntPtr originSurface, string[] mimeTypes,
                                      Func<string, byte[]?> writer, int allowedEffects)
        {
            if (!WaylandClipboard.IsAvailable || originSurface == IntPtr.Zero) return EffectNone;
            if (mimeTypes is null || mimeTypes.Length == 0) return EffectNone;

            IntPtr device = WaylandClipboard.EnsureDevice();
            if (device == IntPtr.Zero) return EffectNone;

            // Must be the serial of a button that is STILL HELD: start_drag needs an implicit grab, and
            // mutter drops a request carrying any other serial without saying so -- which would leave
            // the nested frame below waiting for a drag that was never going to begin. A programmatic
            // DoDragDrop with no button down lands here and returns None instead of hanging.
            uint serial = WaylandInput.LastPointerButtonSerial;
            if (serial == 0) { Log("no held pointer button; not starting a drag"); return EffectNone; }

            IntPtr manager = WaylandDisplay.DataDeviceManager;
            IntPtr source = Wl.Construct(manager, WL_DATA_DEVICE_MANAGER_CREATE_DATA_SOURCE, "wl_data_source",
                                         Wl.wl_proxy_get_version(manager), WlArgument.NewId());
            if (source == IntPtr.Zero) return EffectNone;

            WaylandClipboard.AttachSourceListener(source);
            foreach (string t in mimeTypes)
            {
                Wl.Request(source, WL_DATA_SOURCE_OFFER, WlArgument.Ptr(Wl.Utf8(t)));
            }
            if (Wl.wl_proxy_get_version(source) >= 3)
            {
                Wl.Request(source, WL_DATA_SOURCE_SET_ACTIONS, WlArgument.UInt(ToWaylandActions(allowedEffects)));
            }

            s_dragSource = source;
            s_dragWriter = writer;
            s_dragTypes = mimeTypes;
            s_dragFinished = false;
            s_dragAction = 0;

            Log($"start_drag types=[{string.Join(",", mimeTypes)}] serial={serial}");
            Wl.Request(device, WL_DATA_DEVICE_START_DRAG,
                       WlArgument.Ptr(source), WlArgument.Ptr(originSurface),
                       WlArgument.Ptr(IntPtr.Zero),      // no drag icon: the compositor draws a default
                       WlArgument.UInt(serial));
            WaylandDisplay.Flush();

            PumpUntilDragFinished();

            int performed = FromWaylandAction(s_dragAction);
            Log($"drag finished action={s_dragAction} effect={performed}");
            EndDrag();
            return performed;
        }

        private static void PumpUntilDragFinished()
        {
            var frame = new DispatcherFrame();

            // Two bounds, because this frame blocks the whole app and a drag that never ends would read
            // as a hang. The hard cap catches a compositor that answers nothing at all; the second is
            // the real one -- a drag cannot outlive the button holding it, so once that is released the
            // source events (dnd_drop_performed, dnd_finished, or cancelled) are due immediately.
            DateTime hardDeadline = DateTime.UtcNow.AddMinutes(2);
            DateTime? afterRelease = null;

            var timer = new DispatcherTimer(DispatcherPriority.Send, Dispatcher.CurrentDispatcher)
            {
                Interval = TimeSpan.FromMilliseconds(16),
            };
            timer.Tick += (_, _) =>
            {
                if (WaylandInput.LastPointerButtonSerial == 0)
                {
                    afterRelease ??= DateTime.UtcNow.AddSeconds(5);
                }

                if (s_dragFinished
                    || DateTime.UtcNow > hardDeadline
                    || (afterRelease is DateTime due && DateTime.UtcNow > due))
                {
                    frame.Continue = false;
                }
            };
            timer.Start();
            try { Dispatcher.PushFrame(frame); }
            finally { timer.Stop(); }
        }

        private static uint ToWaylandActions(int effects)
        {
            uint actions = 0;
            if ((effects & (EffectCopy | EffectLink)) != 0) actions |= WL_DND_ACTION_COPY;
            if ((effects & EffectMove) != 0) actions |= WL_DND_ACTION_MOVE;
            return actions;
        }

        private static void EndDrag()
        {
            if (s_dragSource != IntPtr.Zero)
            {
                try { Wl.Destroy(s_dragSource, WL_DATA_SOURCE_DESTROY); } catch { }
                s_dragSource = IntPtr.Zero;
            }
            s_dragWriter = null;
            s_dragTypes = Array.Empty<string>();
            s_dragFinished = true;
        }

        /// <summary>The compositor is asking us to write the dragged data. Returns null if not ours.</summary>
        internal static byte[]? WriteForSource(IntPtr source, string mime)
        {
            if (source != s_dragSource || s_dragWriter is null) return null;
            try { return s_dragWriter(mime); }
            catch (Exception e) { Log("writer threw: " + e.Message); return null; }
        }

        internal static bool IsOurDragSource(IntPtr source) => source != IntPtr.Zero && source == s_dragSource;

        internal static void SourceCancelled(IntPtr source)
        {
            if (source != s_dragSource) return;
            Log("source cancelled");
            s_dragAction = 0;
            s_dragFinished = true;
        }

        internal static void SourceDropPerformed(IntPtr source)
        {
            if (source != s_dragSource) return;
            Log("drop performed");
        }

        internal static void SourceFinished(IntPtr source)
        {
            if (source != s_dragSource) return;
            s_dragFinished = true;
        }

        internal static void SourceAction(IntPtr source, uint action)
        {
            if (source != s_dragSource) return;
            s_dragAction = action;
        }
    }
}
