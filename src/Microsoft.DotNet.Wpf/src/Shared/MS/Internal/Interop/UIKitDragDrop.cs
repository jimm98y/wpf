// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// iOS drag-and-drop, both directions, over UIDropInteraction and UIDragInteraction.
//
// The WPF side is reached through MS.Internal.Interop.PlatformDragDrop, exactly as the Wayland,
// Cocoa and Android backends reach it: this file speaks UIKit and knows nothing about visual trees,
// and PresentationCore's target does the hit-testing and raises the routed events.
//
// The two directions are NOT symmetric on this platform, and the asymmetry is UIKit's, not ours:
//
//   DROPPING IN is straightforward and complete. A UIDropInteraction on the window's view is asked
//   whether it can handle a session, told where the session is, and finally handed the items. It
//   works between apps on iPadOS wherever the system allows a drag at all.
//
//   DRAGGING OUT cannot be started programmatically. There is no "begin a drag now" on
//   UIDragInteraction: a session begins when UIKit's own lift gesture recognises on the view, and
//   the delegate is then ASKED what to drag. DoDragDrop, which is a call and not a gesture, cannot
//   make that happen. So what DoDragDrop does here is ARM the interaction -- publish what would be
//   dragged -- and report that it did not start a drag, which sends the caller to ManagedDragLoop
//   and gives the user a working in-application drag either way. If UIKit then does recognise a lift
//   on the same touch, the armed payload is what leaves the app. A drag out therefore happens when
//   the user makes the system's own drag gesture, and not otherwise.
//
// Reading a dropped item is asynchronous and has no synchronous form: NSItemProvider offers
// loadDataRepresentationForTypeIdentifier:completionHandler: and nothing else. So performDrop: does
// not raise WPF's Drop -- it starts a load for every type it can map, and raises Drop from the
// dispatcher once they have all answered, with the bytes already in hand. Loading every mapped type
// rather than the one the handler wants is deliberate: which format a drop handler will ask for is
// not knowable before it runs, and the alternative is a synchronous read that does not exist. The
// blocks this needs are built by ObjCBlock; see that file for why they are the global kind.
//

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows.Threading;

namespace MS.Internal.Interop
{
    internal static unsafe class UIKitDragDrop
    {
        // Uniform Type Identifiers <-> the MIME vocabulary the WPF side maps to DataFormats, the same
        // table CocoaDragDrop carries. First match wins in both directions, so the preferred spelling
        // of each concept leads.
        private static readonly (string Uti, string Mime)[] s_types =
        {
            ("public.file-url",        "text/uri-list"),
            ("public.url",             "text/uri-list"),
            ("public.utf8-plain-text", "text/plain;charset=utf-8"),
            ("public.plain-text",      "text/plain"),
            ("public.rtf",             "text/rtf"),
            ("public.html",            "text/html"),
            ("public.png",             "image/png"),
            ("public.tiff",            "image/tiff"),
        };

        // UIDropOperation.
        private const nint DropOperationCancel = 0;
        private const nint DropOperationForbidden = 1;
        private const nint DropOperationCopy = 2;
        private const nint DropOperationMove = 3;

        // DragDropEffects, duplicated as constants rather than referenced: this assembly sits below
        // PresentationCore, where that enum lives.
        private const int EffectNone = 0;
        private const int EffectCopy = 1;
        private const int EffectMove = 2;
        private const int EffectLink = 4;

        private static Dispatcher s_dispatcher;
        private static IntPtr s_delegate;          // one shared delegate object for every view
        private static IntPtr s_delegateClass;

        // ---- target state (a drag over one of our views) ----
        private static IntPtr s_inside;            // the view the drag is currently in, or Zero
        private static string[] s_offeredMimes = Array.Empty<string>();
        private static Dictionary<string, byte[]> s_dropped;   // set for the length of one Drop

        // ---- source state (a drag we armed) ----
        private static string s_pendingText;
        private static string[] s_pendingUris;
        private static bool s_systemDragActive;

        /// <summary>
        ///  True while UIKit is carrying a drag out of the app that this process armed. The managed
        ///  in-application drag stands down when it is: both would drive the same drop target, and
        ///  the system one is the better drag when it exists.
        /// </summary>
        internal static bool IsSystemDragActive => s_systemDragActive;

        private static void Log(string what, Exception e)
            => Console.WriteLine($"WPF iOS drag-and-drop {what} failed: {e}");

        // -------------------------------------------------------------- attachment ----

        /// <summary>
        ///  Gives a window's view the drop and drag interactions. Called once per view, after it is
        ///  created and on the UI thread.
        /// </summary>
        /// <remarks>
        ///  Every window gets both, whether or not anything in it has AllowDrop set or ever calls
        ///  DoDragDrop: which element is under the pointer is not known until a drag arrives, and WPF
        ///  decides. An interaction that is installed too narrowly shows the user a drag passing over
        ///  a window that would in fact have taken the data.
        /// </remarks>
        internal static void AttachInteractions(IntPtr view)
        {
            if (view == IntPtr.Zero || !OperatingSystem.IsIOS()) return;

            try
            {
                s_dispatcher ??= Dispatcher.CurrentDispatcher;

                IntPtr target = DelegateObject();
                if (target == IntPtr.Zero) return;

                Add(view, "UIDropInteraction", target);
                Add(view, "UIDragInteraction", target);
            }
            catch (Exception e) { Log("attaching the interactions", e); }

            static void Add(IntPtr host, string className, IntPtr target)
            {
                IntPtr cls = objc_getClass(className);
                if (cls == IntPtr.Zero) return;   // iOS 10 or earlier; there is nothing to attach to

                IntPtr interaction = SendPtrPtr(Send(cls, Sel("alloc")), Sel("initWithDelegate:"), target);
                if (interaction == IntPtr.Zero) return;

                SendVoidPtr(host, Sel("addInteraction:"), interaction);
                Send(interaction, Sel("release"));   // the view retains it
            }
        }

        /// <summary>The shared delegate instance, created on first use and never released.</summary>
        private static IntPtr DelegateObject()
        {
            if (s_delegate != IntPtr.Zero) return s_delegate;

            IntPtr cls = DelegateClass();
            if (cls == IntPtr.Zero) return IntPtr.Zero;
            return s_delegate = Send(Send(cls, Sel("alloc")), Sel("init"));
        }

        /// <summary>
        ///  Builds the class implementing both delegate protocols. One class serves both because the
        ///  selectors do not collide and the state they act on is the same.
        /// </summary>
        private static IntPtr DelegateClass()
        {
            if (s_delegateClass != IntPtr.Zero) return s_delegateClass;

            // Re-registering an existing class pair aborts the process, so look it up first.
            IntPtr existing = objc_getClass("WpfDragDropDelegate");
            if (existing != IntPtr.Zero) return s_delegateClass = existing;

            IntPtr nsObject = objc_getClass("NSObject");
            if (nsObject == IntPtr.Zero) return IntPtr.Zero;

            IntPtr cls = objc_allocateClassPair(nsObject, "WpfDragDropDelegate", UIntPtr.Zero);
            if (cls == IntPtr.Zero) return IntPtr.Zero;

            // The IMPs must be static [UnmanagedCallersOnly] function pointers: iOS is AOT-only and
            // cannot build a native->managed wrapper for a delegate at run time.

            // UIDropInteractionDelegate.
            class_addMethod(cls, Sel("dropInteraction:canHandleSession:"),
                (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, IntPtr, byte>)&CanHandleImp, "B@:@@");
            class_addMethod(cls, Sel("dropInteraction:sessionDidUpdate:"),
                (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, IntPtr, IntPtr>)&SessionDidUpdateImp, "@@:@@");
            class_addMethod(cls, Sel("dropInteraction:sessionDidExit:"),
                (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, IntPtr, void>)&SessionDidExitImp, "v@:@@");
            class_addMethod(cls, Sel("dropInteraction:performDrop:"),
                (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, IntPtr, void>)&PerformDropImp, "v@:@@");
            class_addMethod(cls, Sel("dropInteraction:sessionDidEnd:"),
                (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, IntPtr, void>)&SessionDidEndImp, "v@:@@");

            // UIDragInteractionDelegate. itemsForBeginningSession: is the protocol's only REQUIRED
            // method, and returning an empty array from it is how "there is nothing to drag here"
            // is spelled.
            class_addMethod(cls, Sel("dragInteraction:itemsForBeginningSession:"),
                (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, IntPtr, IntPtr>)&ItemsForBeginningImp, "@@:@@");
            class_addMethod(cls, Sel("dragInteraction:sessionDidEnd:withOperation:"),
                (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, IntPtr, nint, void>)&DragSessionDidEndImp, "v@:@@q");

            // Declaring conformance as well as implementing it. UIKit reaches these through
            // respondsToSelector: either way, but initWithDelegate: takes an id<...Delegate> and a
            // class that does not claim the protocol trips the conformance assertions in a debug
            // UIKit before any of the above is ever called.
            AddProtocol(cls, "UIDropInteractionDelegate");
            AddProtocol(cls, "UIDragInteractionDelegate");

            objc_registerClassPair(cls);
            return s_delegateClass = cls;

            static void AddProtocol(IntPtr cls, string name)
            {
                IntPtr protocol = objc_getProtocol(name);
                if (protocol != IntPtr.Zero) class_addProtocol(cls, protocol);
            }
        }

        // ------------------------------------------------------------------ target ----

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static byte CanHandleImp(IntPtr self, IntPtr sel, IntPtr interaction, IntPtr session)
        {
            // Never let a managed exception unwind into UIKit.
            try
            {
                // Anything whose type we can name is worth offering to WPF; whether the element
                // under the pointer wants it is decided later, by the element.
                return OfferedMimes(session).Length > 0 ? (byte)1 : (byte)0;
            }
            catch (Exception e) { Log("canHandleSession", e); return 0; }
        }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static IntPtr SessionDidUpdateImp(IntPtr self, IntPtr sel, IntPtr interaction, IntPtr session)
        {
            nint operation = DropOperationCancel;
            try { operation = ToOperation(UpdateAndAsk(interaction, session)); }
            catch (Exception e) { Log("sessionDidUpdate", e); }

            // A proposal object is required, not optional -- returning nil from this is a crash. The
            // method's name does not begin with alloc/new/copy, so by Cocoa's rules the caller does
            // not own what comes back and it has to be autoreleased.
            IntPtr cls = objc_getClass("UIDropProposal");
            if (cls == IntPtr.Zero) return IntPtr.Zero;
            IntPtr proposal = SendPtrNInt(Send(cls, Sel("alloc")), Sel("initWithDropOperation:"), operation);
            return Send(proposal, Sel("autorelease"));
        }

        /// <summary>Raises DragEnter or DragOver for the session's current position, and returns the
        /// effect WPF chose.</summary>
        private static int UpdateAndAsk(IntPtr interaction, IntPtr session)
        {
            IPlatformDropTarget target = PlatformDragDrop.Target;
            if (target is null) return EffectNone;

            IntPtr view = Send(interaction, Sel("view"));
            if (view == IntPtr.Zero) return EffectNone;

            ToScreen(view, session, out int sx, out int sy);

            if (s_inside != view)
            {
                LeaveCurrent(target);
                s_offeredMimes = OfferedMimes(session);
                s_inside = view;
                return target.DragEnter(view, sx, sy, s_offeredMimes, Read, AllowedEffects());
            }

            return target.DragOver(view, sx, sy, AllowedEffects());
        }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static void SessionDidExitImp(IntPtr self, IntPtr sel, IntPtr interaction, IntPtr session)
        {
            try { LeaveCurrent(PlatformDragDrop.Target); }
            catch (Exception e) { Log("sessionDidExit", e); }
        }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static void SessionDidEndImp(IntPtr self, IntPtr sel, IntPtr interaction, IntPtr session)
        {
            // Ends after a drop as well as after a cancel, so leaving is conditional on still being
            // inside -- the drop path clears that itself.
            try { LeaveCurrent(PlatformDragDrop.Target); }
            catch (Exception e) { Log("sessionDidEnd", e); }
        }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static void PerformDropImp(IntPtr self, IntPtr sel, IntPtr interaction, IntPtr session)
        {
            try
            {
                IntPtr view = Send(interaction, Sel("view"));
                if (view == IntPtr.Zero) return;

                ToScreen(view, session, out int sx, out int sy);
                BeginLoads(session, view, sx, sy);
            }
            catch (Exception e) { Log("performDrop", e); }
        }

        // ---- the asynchronous read ----
        //
        // One PendingDrop per drop, holding the loads still outstanding and what has come back. The
        // completion handlers arrive on whatever queue NSItemProvider felt like, so every touch of
        // one is under its own lock and the finish hops to the dispatcher.

        private sealed class PendingDrop
        {
            internal IntPtr View;
            internal int ScreenX, ScreenY;
            internal int Outstanding;
            internal readonly Dictionary<string, byte[]> Data = new(StringComparer.Ordinal);
        }

        private static readonly object s_pendingLock = new object();
        private static readonly Dictionary<nint, (PendingDrop Drop, string Mime, IntPtr Block)> s_loads = new();
        private static nint s_nextLoadId = 1;

        private static void BeginLoads(IntPtr session, IntPtr view, int screenX, int screenY)
        {
            var pending = new PendingDrop { View = view, ScreenX = screenX, ScreenY = screenY };

            // (provider, uti, mime) for every type we can name, first provider that offers it.
            var work = new List<(IntPtr Provider, string Uti, string Mime)>();
            var seen = new HashSet<string>(StringComparer.Ordinal);

            IntPtr items = Send(session, Sel("items"));
            nint count = items == IntPtr.Zero ? 0 : SendNInt(items, Sel("count"));
            for (nint i = 0; i < count; i++)
            {
                IntPtr item = SendPtrNInt(items, Sel("objectAtIndex:"), i);
                IntPtr provider = item == IntPtr.Zero ? IntPtr.Zero : Send(item, Sel("itemProvider"));
                if (provider == IntPtr.Zero) continue;

                foreach (string uti in RegisteredTypes(provider))
                {
                    string mime = MimeForUti(uti);
                    if (mime is null || !seen.Add(mime)) continue;
                    work.Add((provider, uti, mime));
                }
            }

            if (work.Count == 0)
            {
                Finish(pending);
                return;
            }

            pending.Outstanding = work.Count;
            foreach ((IntPtr provider, string uti, string mime) in work)
            {
                nint id;
                IntPtr block;
                lock (s_pendingLock)
                {
                    id = s_nextLoadId++;
                    block = ObjCBlock.Create(
                        (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, void>)&LoadCompletedImp,
                        (IntPtr)id);
                    s_loads[id] = (pending, mime, block);
                }

                if (block == IntPtr.Zero)
                {
                    // No blocks means no asynchronous read at all. Count the load as answered with
                    // nothing rather than leaving the drop waiting for ever.
                    LoadFinished(id, IntPtr.Zero);
                    continue;
                }

                SendVoidPtrPtr(provider, Sel("loadDataRepresentationForTypeIdentifier:completionHandler:"),
                               NSStr(uti), block);
            }
        }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static void LoadCompletedImp(IntPtr block, IntPtr data, IntPtr error)
        {
            try
            {
                nint id = (nint)ObjCBlock.ContextOf(block);
                LoadFinished(id, data);
            }
            catch (Exception e) { Log("a drop load completion", e); }
        }

        private static void LoadFinished(nint id, IntPtr data)
        {
            PendingDrop pending;
            string mime;
            lock (s_pendingLock)
            {
                if (!s_loads.Remove(id, out (PendingDrop Drop, string Mime, IntPtr Block) load)) return;
                ObjCBlock.Release(load.Block);
                pending = load.Drop;
                mime = load.Mime;
            }

            byte[] bytes = ToBytes(data);

            bool done;
            lock (pending)
            {
                if (bytes is not null) pending.Data[mime] = bytes;
                done = --pending.Outstanding <= 0;
            }

            if (done) Finish(pending);
        }

        /// <summary>Every load has answered, so the drop can be raised -- on the dispatcher, because
        /// the completions did not arrive on it.</summary>
        private static void Finish(PendingDrop pending)
        {
            Dispatcher dispatcher = s_dispatcher;
            if (dispatcher is null) return;

            dispatcher.BeginInvoke(DispatcherPriority.Send, new Action(() =>
            {
                IPlatformDropTarget target = PlatformDragDrop.Target;
                if (target is null) return;

                s_dropped = pending.Data;
                try
                {
                    if (s_inside != pending.View)
                    {
                        s_offeredMimes = ToArray(pending.Data.Keys);
                        s_inside = pending.View;
                        target.DragEnter(pending.View, pending.ScreenX, pending.ScreenY,
                                         s_offeredMimes, Read, AllowedEffects());
                    }

                    target.Drop(pending.View, pending.ScreenX, pending.ScreenY, AllowedEffects());
                }
                catch (Exception e) { Log("the drop", e); }
                finally
                {
                    s_dropped = null;
                    s_inside = IntPtr.Zero;
                    s_offeredMimes = Array.Empty<string>();
                }
            }));
        }

        /// <summary>
        ///  Reads one of the advertised MIME types. Answers only during a drop: before one there is
        ///  nothing loaded, because loading is asynchronous and DragOver is not.
        /// </summary>
        private static byte[] Read(string mime)
        {
            if (mime is null || s_dropped is null) return null;
            return s_dropped.TryGetValue(mime, out byte[] bytes) ? bytes : null;
        }

        /// <summary>What the source permits. UIKit does not say, so everything WPF understands is
        /// allowed and the target's own handlers decide.</summary>
        private static int AllowedEffects() => EffectCopy | EffectMove | EffectLink;

        private static void LeaveCurrent(IPlatformDropTarget target)
        {
            if (s_inside == IntPtr.Zero) return;
            IntPtr was = s_inside;
            s_inside = IntPtr.Zero;
            s_offeredMimes = Array.Empty<string>();
            target?.DragLeave(was);
        }

        /// <summary>
        /// The session's position in the screen device pixels the seam takes. locationInView: gives
        /// POINTS relative to the view, so it goes through the screen scale (as the touch path does)
        /// and then through the window's client screen origin -- which is what PointUtil.ScreenToClient
        /// subtracts again on the way in, making the round trip exact.
        /// </summary>
        private static void ToScreen(IntPtr view, IntPtr session, out int screenX, out int screenY)
        {
            CGPoint p = SendPointPtr(session, Sel("locationInView:"), view);
            double scale = ScreenScale();
            screenX = (int)Math.Round(p.x * scale);
            screenY = (int)Math.Round(p.y * scale);

            IPlatformWindow window = PlatformWindow.FromHandle(view);
            if (window is null) return;
            window.GetClientScreenOriginPixels(out int originX, out int originY);
            screenX += originX;
            screenY += originY;
        }

        // ------------------------------------------------------------------ source ----

        /// <summary>
        ///  Publishes what a drag would carry, so that a lift gesture on the same touch takes it out
        ///  of the app.
        /// </summary>
        /// <param name="started">
        ///  Always false: see the file header. The caller runs the drag inside the application
        ///  instead, which is what makes a drag work here at all.
        /// </param>
        /// <returns>Always <c>None</c>, for the same reason.</returns>
        internal static int StartDrag(IntPtr view, string[] mimeTypes, Func<string, byte[]> writer,
                                      int allowedEffects, out bool started)
        {
            started = false;
            s_pendingText = null;
            s_pendingUris = null;

            if (view == IntPtr.Zero || mimeTypes is null || !OperatingSystem.IsIOS()) return EffectNone;

            foreach (string mime in mimeTypes)
            {
                if (s_pendingText is null && IsTextMime(mime)) s_pendingText = ReadString(writer, mime);
                else if (s_pendingUris is null && string.Equals(mime, "text/uri-list", StringComparison.Ordinal))
                {
                    s_pendingUris = ParseUriList(ReadString(writer, mime));
                }
            }

            return EffectNone;
        }

        /// <summary>Drops the armed payload, once the drag it belonged to is over.</summary>
        internal static void ClearPending()
        {
            s_pendingText = null;
            s_pendingUris = null;
        }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static IntPtr ItemsForBeginningImp(IntPtr self, IntPtr sel, IntPtr interaction, IntPtr session)
        {
            try
            {
                IntPtr array = Send(objc_getClass("NSMutableArray"), Sel("array"));
                if (array == IntPtr.Zero) return IntPtr.Zero;

                // Text and URLs only. Anything else would need a data representation registered
                // through a block whose loadHandler outlives this call, and the drag would then be
                // carrying bytes no receiving app asked for; the same subset the Android head
                // carries, and for the same reason.
                if (s_pendingUris is not null)
                {
                    foreach (string uri in s_pendingUris) AddItem(array, NSUrl(uri));
                }
                if (s_pendingText is not null) AddItem(array, NSStr(s_pendingText));

                nint count = SendNInt(array, Sel("count"));
                if (count > 0) s_systemDragActive = true;
                return array;
            }
            catch (Exception e) { Log("itemsForBeginningSession", e); return IntPtr.Zero; }

            static void AddItem(IntPtr array, IntPtr obj)
            {
                if (obj == IntPtr.Zero) return;

                // initWithObject: registers the type identifiers the object itself declares -- NSString
                // registers the plain-text ones, NSURL the URL ones -- which is why no block is needed
                // on this side.
                IntPtr provider = SendPtrPtr(Send(objc_getClass("NSItemProvider"), Sel("alloc")),
                                             Sel("initWithObject:"), obj);
                if (provider == IntPtr.Zero) return;

                IntPtr item = SendPtrPtr(Send(objc_getClass("UIDragItem"), Sel("alloc")),
                                         Sel("initWithItemProvider:"), provider);
                Send(provider, Sel("release"));
                if (item == IntPtr.Zero) return;

                SendVoidPtr(array, Sel("addObject:"), item);
                Send(item, Sel("release"));
            }
        }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static void DragSessionDidEndImp(IntPtr self, IntPtr sel, IntPtr interaction, IntPtr session, nint operation)
        {
            s_systemDragActive = false;
            ClearPending();
        }

        // ------------------------------------------------------------------ types ----

        private static string MimeForUti(string uti)
        {
            foreach ((string u, string mime) in s_types)
            {
                if (string.Equals(u, uti, StringComparison.Ordinal)) return mime;
            }
            return null;   // a type we have no name for is not worth loading
        }

        private static bool IsTextMime(string mime)
            => mime.StartsWith("text/plain", StringComparison.Ordinal)
               || string.Equals(mime, "UTF8_STRING", StringComparison.Ordinal)
               || string.Equals(mime, "STRING", StringComparison.Ordinal)
               || string.Equals(mime, "TEXT", StringComparison.Ordinal);

        private static nint ToOperation(int effects)
        {
            // One operation, not a set: this is the answer to "what would happen on drop". Forbidden
            // rather than Cancel for a refusal, so the user sees the "no drop" badge instead of the
            // drag silently snapping back.
            if ((effects & EffectMove) != 0) return DropOperationMove;
            if ((effects & (EffectCopy | EffectLink)) != 0) return DropOperationCopy;
            return DropOperationForbidden;
        }

        /// <summary>The MIME types a session's items can produce, deduplicated.</summary>
        private static string[] OfferedMimes(IntPtr session)
        {
            IntPtr items = Send(session, Sel("items"));
            nint count = items == IntPtr.Zero ? 0 : SendNInt(items, Sel("count"));

            var mimes = new List<string>();
            for (nint i = 0; i < count; i++)
            {
                IntPtr item = SendPtrNInt(items, Sel("objectAtIndex:"), i);
                IntPtr provider = item == IntPtr.Zero ? IntPtr.Zero : Send(item, Sel("itemProvider"));
                if (provider == IntPtr.Zero) continue;

                foreach (string uti in RegisteredTypes(provider))
                {
                    string mime = MimeForUti(uti);
                    if (mime is not null && !mimes.Contains(mime)) mimes.Add(mime);
                }
            }
            return mimes.ToArray();
        }

        private static List<string> RegisteredTypes(IntPtr provider)
        {
            var utis = new List<string>();
            IntPtr array = Send(provider, Sel("registeredTypeIdentifiers"));
            nint count = array == IntPtr.Zero ? 0 : SendNInt(array, Sel("count"));
            for (nint i = 0; i < count; i++)
            {
                string uti = FromNSString(SendPtrNInt(array, Sel("objectAtIndex:"), i));
                if (uti is not null) utis.Add(uti);
            }
            return utis;
        }

        private static string ReadString(Func<string, byte[]> writer, string mime)
        {
            try
            {
                byte[] bytes = writer(mime);
                return bytes is null || bytes.Length == 0 ? null : System.Text.Encoding.UTF8.GetString(bytes);
            }
            catch (Exception e) { Log($"the drag writer for '{mime}'", e); return null; }
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

        private static string[] ToArray(Dictionary<string, byte[]>.KeyCollection keys)
        {
            var array = new string[keys.Count];
            keys.CopyTo(array, 0);
            return array;
        }

        // ---- Objective-C runtime --------------------------------------------------

        private const string ObjC = "/usr/lib/libobjc.dylib";

        [StructLayout(LayoutKind.Sequential)]
        private struct CGPoint { public double x, y; }

        private static IntPtr Sel(string name) => sel_registerName(name);

        private static IntPtr NSStr(string s)
            => s is null ? IntPtr.Zero : SendPtrUtf8(objc_getClass("NSString"), Sel("stringWithUTF8String:"), s);

        private static IntPtr NSUrl(string s)
        {
            if (string.IsNullOrEmpty(s)) return IntPtr.Zero;
            return SendPtrPtr(objc_getClass("NSURL"), Sel("URLWithString:"), NSStr(s));
        }

        private static string FromNSString(IntPtr nsString)
        {
            if (nsString == IntPtr.Zero) return null;
            IntPtr utf8 = Send(nsString, Sel("UTF8String"));
            return utf8 == IntPtr.Zero ? null : Marshal.PtrToStringUTF8(utf8);
        }

        private static byte[] ToBytes(IntPtr nsData)
        {
            if (nsData == IntPtr.Zero) return null;
            nint length = SendNInt(nsData, Sel("length"));
            if (length <= 0) return Array.Empty<byte>();

            IntPtr bytes = Send(nsData, Sel("bytes"));
            if (bytes == IntPtr.Zero) return null;

            var managed = new byte[length];
            Marshal.Copy(bytes, managed, 0, (int)length);
            return managed;
        }

        private static double ScreenScale()
        {
            IntPtr screen = Send(objc_getClass("UIScreen"), Sel("mainScreen"));
            if (screen == IntPtr.Zero) return 1.0;
            double s = SendDouble(screen, Sel("nativeScale"));
            return s > 0 ? s : 1.0;
        }

        [DllImport(ObjC)] private static extern IntPtr objc_getClass(string name);
        [DllImport(ObjC)] private static extern IntPtr sel_registerName(string name);
        [DllImport(ObjC)] private static extern IntPtr objc_allocateClassPair(IntPtr superclass, string name, UIntPtr extraBytes);
        [DllImport(ObjC)] private static extern void objc_registerClassPair(IntPtr cls);
        [DllImport(ObjC)] [return: MarshalAs(UnmanagedType.I1)] private static extern bool class_addMethod(IntPtr cls, IntPtr sel, IntPtr imp, string types);
        [DllImport(ObjC)] private static extern IntPtr objc_getProtocol(string name);
        [DllImport(ObjC)] [return: MarshalAs(UnmanagedType.I1)] private static extern bool class_addProtocol(IntPtr cls, IntPtr protocol);

        [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr Send(IntPtr receiver, IntPtr selector);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern void SendVoidPtr(IntPtr receiver, IntPtr selector, IntPtr arg);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr SendPtrPtr(IntPtr receiver, IntPtr selector, IntPtr arg);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern void SendVoidPtrPtr(IntPtr receiver, IntPtr selector, IntPtr a, IntPtr b);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr SendPtrUtf8(IntPtr receiver, IntPtr selector, [MarshalAs(UnmanagedType.LPUTF8Str)] string arg);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr SendPtrNInt(IntPtr receiver, IntPtr selector, nint arg);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern nint SendNInt(IntPtr receiver, IntPtr selector);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern double SendDouble(IntPtr receiver, IntPtr selector);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern CGPoint SendPointPtr(IntPtr receiver, IntPtr selector, IntPtr arg);
    }
}
