// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// macOS drag-and-drop, both directions, over AppKit's NSDragging protocols.
//
// The WPF side is reached through MS.Internal.Interop.PlatformDragDrop, exactly as the Wayland
// backend reaches it: this file speaks AppKit and knows nothing about visual trees, and
// PresentationCore's target does the hit-testing and raises the routed events.
//
// Types cross that seam as MIME strings rather than as macOS UTIs. The seam lets a backend use its
// own vocabulary, but there is nothing to gain from a second one -- the WPF-side mapping is already
// written against MIME for Wayland, and translating here (public.utf8-plain-text ->
// text/plain;charset=utf-8) is a table, whereas a second vocabulary would be a second copy of the
// format logic. UTIs that have no MIME equivalent travel through untranslated.
//
// Two AppKit facts shape the rest:
//
//   1. THE DESTINATION IS THE VIEW. Dragging is not a delegate protocol here; the NSView itself
//      answers draggingEntered:/draggingUpdated:/draggingExited:/performDragOperation:. They are
//      added to the synthesised WpfContentView class alongside the accessibility methods, and a
//      view only receives them after registerForDraggedTypes:.
//   2. THE SOURCE DOES NOT BLOCK. beginDraggingSessionWithItems: returns at once and the drag is
//      tracked on the run loop, with the outcome arriving at draggingSession:endedAtPoint:operation:.
//      DoDragDrop must not return before then, so it pumps a nested dispatcher frame -- the same
//      shape the Wayland source uses, and for the same reason.
//
// Cancelling a drag from the source has no AppKit equivalent and needs none: AppKit itself ends a
// drag on Escape, which is the behaviour the Windows source gets from its own QueryContinueDrag.
//

#nullable enable

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows.Threading;

namespace MS.Internal.Interop
{
    internal static class CocoaDragDrop
    {
        // Uniform Type Identifiers <-> the MIME vocabulary the WPF side maps to DataFormats. First
        // match wins in both directions, so the preferred spelling of each concept leads.
        private static readonly (string Uti, string Mime)[] s_types =
        {
            ("public.file-url",        "text/uri-list"),
            ("public.utf8-plain-text", "text/plain;charset=utf-8"),
            ("public.plain-text",      "text/plain"),
            ("NSStringPboardType",     "text/plain"),
            ("public.rtf",             "text/rtf"),
            ("public.html",            "text/html"),
            ("public.png",             "image/png"),
            ("public.tiff",            "image/tiff"),
            // The in-process marker. Its MIME spelling is not a type identifier, and AppKit refused it
            // ("'application/x-wpf-dragdrop' is not a valid UTI string"): a drag of a plain CLR object
            // between two of the application's own controls carried nothing at all. Under a reverse-DNS
            // identifier of its own it travels; dynamic identifiers need no declaration.
            ("org.dotnet.wpf.inprocess-drag", PlatformDragDrop.InProcessMime),
        };

        private static string MimeForUti(string uti)
        {
            foreach ((string u, string mime) in s_types)
            {
                if (string.Equals(u, uti, StringComparison.Ordinal)) return mime;
            }
            return uti;
        }

        private static string UtiForMime(string mime)
        {
            foreach ((string uti, string m) in s_types)
            {
                if (string.Equals(m, mime, StringComparison.Ordinal)) return uti;
            }
            return mime;
        }

        // NSDragOperation. Not a bit index each -- these ARE the flag values.
        private const ulong NSDragOperationNone = 0;
        private const ulong NSDragOperationCopy = 1;
        private const ulong NSDragOperationLink = 2;
        private const ulong NSDragOperationGeneric = 4;
        private const ulong NSDragOperationMove = 16;

        // DragDropEffects, duplicated as constants rather than referenced: this assembly sits below
        // PresentationCore, where that enum lives.
        private const int EffectNone = 0;
        private const int EffectCopy = 1;
        private const int EffectMove = 2;
        private const int EffectLink = 4;

        private static int ToEffects(ulong operations)
        {
            int effects = EffectNone;
            if ((operations & (NSDragOperationCopy | NSDragOperationGeneric)) != 0) effects |= EffectCopy;
            if ((operations & NSDragOperationMove) != 0) effects |= EffectMove;
            if ((operations & NSDragOperationLink) != 0) effects |= EffectLink;
            return effects;
        }

        /// <summary>
        ///  The one effect a drag's outcome is, as Windows' DoDragDrop returns it: AppKit can end a session
        ///  with several (TextEdit took a dropped text as copy and move together, 17), and a source told
        ///  Copy|Move treats it as a move -- an application that removes what was moved would have removed
        ///  what was only copied. Copy wins, as the safe reading; then move, then link.
        /// </summary>
        private static int Performed(int effects)
        {
            if ((effects & EffectCopy) != 0) return EffectCopy;
            if ((effects & EffectMove) != 0) return EffectMove;
            if ((effects & EffectLink) != 0) return EffectLink;
            return EffectNone;
        }

        private static ulong ToOperation(int effects)
        {
            // One operation, not a set: this is the answer to "what would happen on drop", and the
            // order matches the Windows drop target's own preference.
            if ((effects & EffectMove) != 0) return NSDragOperationMove;
            if ((effects & EffectCopy) != 0) return NSDragOperationCopy;
            if ((effects & EffectLink) != 0) return NSDragOperationLink;
            return NSDragOperationNone;
        }

        private static void Log(string what, Exception e)
            => Console.WriteLine($"WPF macOS drag-and-drop {what} failed: {e}");

        // ------------------------------------------------------------- destination ----

        // The delegates are held in static fields for the process lifetime. A function pointer
        // installed in an Objective-C class outlives any managed scope, so letting one be collected
        // would leave AppKit calling into freed memory.
        private static DraggingOpDelegate? s_entered;
        private static DraggingOpDelegate? s_updated;
        private static DraggingVoidDelegate? s_exited;
        private static DraggingBoolDelegate? s_prepare;
        private static DraggingBoolDelegate? s_perform;

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate ulong DraggingOpDelegate(IntPtr self, IntPtr sel, IntPtr sender);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void DraggingVoidDelegate(IntPtr self, IntPtr sel, IntPtr sender);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        [return: MarshalAs(UnmanagedType.I1)]
        private delegate bool DraggingBoolDelegate(IntPtr self, IntPtr sel, IntPtr sender);

        /// <summary>
        ///  Adds the NSDraggingDestination methods to the content-view class. Called while the class
        ///  is being built, before objc_registerClassPair.
        /// </summary>
        internal static void AddViewDragging(IntPtr viewClass)
        {
            s_entered = EnteredImp;
            s_updated = UpdatedImp;
            s_exited = ExitedImp;
            s_prepare = PrepareImp;
            s_perform = PerformImp;

            AddMethod(viewClass, "draggingEntered:", s_entered, "L@:@");
            AddMethod(viewClass, "draggingUpdated:", s_updated, "L@:@");
            AddMethod(viewClass, "draggingExited:", s_exited, "v@:@");
            AddMethod(viewClass, "prepareForDragOperation:", s_prepare, "B@:@");
            AddMethod(viewClass, "performDragOperation:", s_perform, "B@:@");
        }

        /// <summary>
        ///  Tells AppKit this view accepts drags. Without it the methods above are never called, no
        ///  matter that the class implements them.
        /// </summary>
        /// <remarks>
        ///  Every type WPF can map is registered rather than only the ones some element wants: which
        ///  element is under the pointer is not known until the drag arrives, and AllowDrop is
        ///  decided by the WPF-side target. A registration that is too narrow shows the user a
        ///  "no drop" cursor over a window that would in fact have taken the data.
        /// </remarks>
        internal static void RegisterDraggedTypes(IntPtr view)
        {
            if (view == IntPtr.Zero) return;

            try
            {
                IntPtr array = Send(objc_getClass("NSMutableArray"), Sel("array"));
                if (array == IntPtr.Zero) return;

                foreach ((string uti, _) in s_types)
                {
                    SendVoidPtr(array, Sel("addObject:"), NSStr(uti));
                }

                // (The in-process marker is in the table: without it AppKit would never deliver a drag
                // carrying ONLY that type, which is precisely what dragging a plain CLR object between
                // two of the application's own controls produces -- the commonest drag there is.)

                SendVoidPtr(view, Sel("registerForDraggedTypes:"), array);
            }
            catch (Exception e)
            {
                Log("type registration", e);
            }
        }

        /// <summary>The types on a dragging session's pasteboard, as MIME.</summary>
        private static string[] TypesOf(IntPtr pasteboard)
        {
            IntPtr types = Send(pasteboard, Sel("types"));
            if (types == IntPtr.Zero) return Array.Empty<string>();

            ulong count = SendNUInt(types, Sel("count"));
            var mimes = new List<string>((int)count);
            for (ulong i = 0; i < count; i++)
            {
                string uti = ReadString(SendPtrNUInt(types, Sel("objectAtIndex:"), (nuint)i));
                if (uti.Length == 0) continue;

                string mime = MimeForUti(uti);
                if (!mimes.Contains(mime)) mimes.Add(mime);
            }
            return mimes.ToArray();
        }

        /// <summary>Reads one MIME type off the drag pasteboard.</summary>
        private static byte[]? Read(IntPtr pasteboard, string mime)
        {
            if (pasteboard == IntPtr.Zero) return null;

            IntPtr data = SendPtrPtr(pasteboard, Sel("dataForType:"), NSStr(UtiForMime(mime)));
            if (data == IntPtr.Zero) return null;

            nint length = SendNInt(data, Sel("length"));
            if (length <= 0) return Array.Empty<byte>();

            IntPtr bytes = Send(data, Sel("bytes"));
            if (bytes == IntPtr.Zero) return null;

            var managed = new byte[length];
            Marshal.Copy(bytes, managed, 0, (int)length);
            return managed;
        }

        /// <summary>
        ///  The drag location in the screen device pixels the seam takes. draggingLocation is in the
        ///  window's base coordinates, so it goes through the window (bottom-left origin, points) and
        ///  then through the same flip every other coordinate on this backend uses.
        /// </summary>
        private static void LocationOf(IntPtr view, IntPtr sender, out int screenX, out int screenY)
        {
            NSPoint onScreen = ScreenPointsOf(view, sender);
            CocoaWindow.ConvertCocoaPointsToScreenPixels(onScreen.x, onScreen.y, out double px, out double py);
            screenX = (int)Math.Round(px);
            screenY = (int)Math.Round(py);
        }

        /// <summary>The drag location on the screen, in AppKit's points (bottom-left origin).</summary>
        private static NSPoint ScreenPointsOf(IntPtr view, IntPtr sender)
        {
            NSPoint inWindow = SendPoint(sender, Sel("draggingLocation"));
            IntPtr window = Send(view, Sel("window"));
            if (window == IntPtr.Zero) return inWindow;
            // convertRectToScreen: rather than convertPointToScreen:, which arrived later; the
            // origin of a zero-sized rect is the converted point.
            var rect = new NSRect { origin = inWindow, size = default };
            return SendRectRect(window, Sel("convertRectToScreen:"), rect).origin;
        }

        // ---- views that are not WPF's (a WinForms form's) -----------------------------------
        //
        // A view whose class has the destination methods but is not a WPF window answers with a target
        // of its own, given the drag where AppKit gives it: on the screen in points. The WinForms host
        // is one: a form shown with no WPF around it is a drop target and a drag source of its own.

        private static readonly Dictionary<IntPtr, ForeignDropTarget> s_foreignTargets = new();

        internal static void RegisterForeign(IntPtr view, ForeignDropTarget target)
        {
            if (view == IntPtr.Zero) return;
            lock (s_foreignTargets) s_foreignTargets[view] = target;
            RegisterDraggedTypes(view);
        }

        internal static void UnregisterForeign(IntPtr view)
        {
            lock (s_foreignTargets) s_foreignTargets.Remove(view);
        }

        private static ForeignDropTarget? ForeignTarget(IntPtr view)
        {
            lock (s_foreignTargets) return s_foreignTargets.TryGetValue(view, out ForeignDropTarget? t) ? t : null;
        }

        private static ulong EnteredImp(IntPtr self, IntPtr sel, IntPtr sender)
        {
            try
            {
                if (ForeignTarget(self) is { } foreign)
                {
                    IntPtr fpb = Send(sender, Sel("draggingPasteboard"));
                    NSPoint fp = ScreenPointsOf(self, sender);
                    return ToOperation(foreign.DragEnter(fp.x, fp.y, TypesOf(fpb), mime => Read(fpb, mime),
                        ToEffects(SendNUInt(sender, Sel("draggingSourceOperationMask")))));
                }

                IPlatformDropTarget? target = PlatformDragDrop.Target;
                if (target is null) return NSDragOperationNone;

                IntPtr pasteboard = Send(sender, Sel("draggingPasteboard"));
                LocationOf(self, sender, out int x, out int y);
                int allowed = ToEffects(SendNUInt(sender, Sel("draggingSourceOperationMask")));

                int effect = target.DragEnter(CocoaWindow.HandleOf(self), x, y, TypesOf(pasteboard),
                                              mime => Read(pasteboard, mime), allowed);
                return ToOperation(effect);
            }
            catch (Exception e)
            {
                // An exception must not cross back into AppKit; a broken handler costs the drop.
                Log("dragging entered", e);
                return NSDragOperationNone;
            }
        }

        private static ulong UpdatedImp(IntPtr self, IntPtr sel, IntPtr sender)
        {
            try
            {
                if (ForeignTarget(self) is { } foreign)
                {
                    NSPoint fp = ScreenPointsOf(self, sender);
                    return ToOperation(foreign.DragOver(fp.x, fp.y, ToEffects(SendNUInt(sender, Sel("draggingSourceOperationMask")))));
                }

                IPlatformDropTarget? target = PlatformDragDrop.Target;
                if (target is null) return NSDragOperationNone;

                LocationOf(self, sender, out int x, out int y);
                int allowed = ToEffects(SendNUInt(sender, Sel("draggingSourceOperationMask")));
                return ToOperation(target.DragOver(CocoaWindow.HandleOf(self), x, y, allowed));
            }
            catch (Exception e)
            {
                Log("dragging updated", e);
                return NSDragOperationNone;
            }
        }

        private static void ExitedImp(IntPtr self, IntPtr sel, IntPtr sender)
        {
            try
            {
                if (ForeignTarget(self) is { } foreign) foreign.DragLeave();
                else PlatformDragDrop.Target?.DragLeave(CocoaWindow.HandleOf(self));
            }
            catch (Exception e) { Log("dragging exited", e); }
        }

        private static bool PrepareImp(IntPtr self, IntPtr sel, IntPtr sender) => true;

        private static bool PerformImp(IntPtr self, IntPtr sel, IntPtr sender)
        {
            try
            {
                if (ForeignTarget(self) is { } foreign)
                {
                    NSPoint fp = ScreenPointsOf(self, sender);
                    return foreign.Drop(fp.x, fp.y, ToEffects(SendNUInt(sender, Sel("draggingSourceOperationMask")))) != EffectNone;
                }

                IPlatformDropTarget? target = PlatformDragDrop.Target;
                if (target is null) return false;

                LocationOf(self, sender, out int x, out int y);
                int allowed = ToEffects(SendNUInt(sender, Sel("draggingSourceOperationMask")));
                return target.Drop(CocoaWindow.HandleOf(self), x, y, allowed) != EffectNone;
            }
            catch (Exception e)
            {
                Log("perform drop", e);
                return false;
            }
        }

        // ------------------------------------------------------------------ source ----

        private static IntPtr s_sourceClass;
        private static IntPtr s_sourceObject;
        private static bool s_dragEnded;
        private static ulong s_dragOperation;
        private static Action<int>? s_giveFeedback;

        private static SourceMaskDelegate? s_sourceMask;
        private static SourceEndedDelegate? s_sourceEnded;
        private static SourceMovedDelegate? s_sourceMoved;

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate ulong SourceMaskDelegate(IntPtr self, IntPtr sel, IntPtr session, nint context);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void SourceEndedDelegate(IntPtr self, IntPtr sel, IntPtr session, NSPoint point, ulong operation);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void SourceMovedDelegate(IntPtr self, IntPtr sel, IntPtr session, NSPoint point);

        // What the drag we started permits. Read by the mask callback, which AppKit may ask more
        // than once and from either dragging context.
        private static ulong s_allowedOperations;

        private static IntPtr EnsureSourceObject()
        {
            if (s_sourceObject != IntPtr.Zero) return s_sourceObject;

            if (s_sourceClass == IntPtr.Zero)
            {
                // Re-registering an existing class pair aborts the process, so look it up first.
                s_sourceClass = objc_getClass("WpfDragSource");
                if (s_sourceClass == IntPtr.Zero)
                {
                    IntPtr nsObject = objc_getClass("NSObject");
                    if (nsObject == IntPtr.Zero) return IntPtr.Zero;

                    IntPtr cls = objc_allocateClassPair(nsObject, "WpfDragSource", UIntPtr.Zero);
                    if (cls == IntPtr.Zero) return IntPtr.Zero;

                    s_sourceMask = SourceMaskImp;
                    s_sourceEnded = SourceEndedImp;
                    s_sourceMoved = SourceMovedImp;

                    AddMethod(cls, "draggingSession:sourceOperationMaskForDraggingContext:", s_sourceMask, "L@:@q");
                    AddMethod(cls, "draggingSession:endedAtPoint:operation:", s_sourceEnded, "v@:@{CGPoint=dd}L");
                    AddMethod(cls, "draggingSession:movedToPoint:", s_sourceMoved, "v@:@{CGPoint=dd}");

                    objc_registerClassPair(cls);
                    s_sourceClass = cls;
                }
            }

            return s_sourceObject = Send(Send(s_sourceClass, Sel("alloc")), Sel("init"));
        }

        private static ulong SourceMaskImp(IntPtr self, IntPtr sel, IntPtr session, nint context)
            => s_allowedOperations;

        private static void SourceEndedImp(IntPtr self, IntPtr sel, IntPtr session, NSPoint point, ulong operation)
        {
            TraceDrag($"session 0x{session:x} ended at {point.x},{point.y} operation {operation} (moves seen: {s_moves})");
            s_dragOperation = operation;
            s_dragEnded = true;
        }

        private static int s_moves;
        private static readonly bool s_traceDrag = Environment.GetEnvironmentVariable("WPF_TRACE_DRAG") == "1";

        internal static void TraceDrag(string what)
        {
            if (s_traceDrag) Console.Error.WriteLine($"[drag {DateTime.Now:HH:mm:ss.fff}] {what}");
        }

        private static void SourceMovedImp(IntPtr self, IntPtr sel, IntPtr session, NSPoint point)
        {
            if (s_moves++ % 20 == 0) TraceDrag($"session 0x{session:x} moved to {point.x},{point.y}");
            // Deliberately empty. AppKit never tells the SOURCE what the destination under the
            // pointer would do -- that answer exists only inside the destination, and reaches the
            // source once, at the end. So there is nothing to report per move that would not be a
            // guess; GiveFeedback is raised from RaiseInitialFeedback instead, with what the drag
            // permits. The method still has to exist, because it is where a fix would go if a later
            // macOS ever exposes the proposal.
        }

        /// <summary>
        ///  Raises GiveFeedback once, as the drag begins, with the effects the drag allows.
        /// </summary>
        /// <remarks>
        ///  Not what Windows does -- there OLE re-asks on every mouse move with the destination's
        ///  current answer -- but it is everything this platform can support, and it means a handler
        ///  that sets UseDefaultCursors or does its own bookkeeping still runs.
        /// </remarks>
        private static void RaiseInitialFeedback(int allowedEffects)
        {
            try { s_giveFeedback?.Invoke(allowedEffects); }
            catch (Exception e) { Log("give feedback", e); }
        }

        /// <summary>
        ///  Start a drag from <paramref name="view"/> and run it to completion, returning the effect
        ///  that was performed.
        /// </summary>
        /// <param name="writer">Produces the bytes for one MIME type.</param>
        /// <param name="started">
        ///  False when the session never began -- no AppKit, no originating mouse event, nothing to
        ///  put on the pasteboard. The caller can then run the drag inside the application instead,
        ///  which is what makes a drag by touch work. Distinct from a drag that ran and was refused,
        ///  which reports true with an effect of none.
        /// </param>
        /// <param name="giveFeedback">Told the current effect whenever it changes.</param>
        internal static int StartDrag(IntPtr view, string[] mimeTypes, Func<string, byte[]?> writer,
                                      int allowedEffects, out bool started,
                                      Action<int>? giveFeedback = null, Action<Func<bool>>? waitUntil = null)
        {
            started = false;
            view = CocoaWindow.ViewOf(view);   // the drag source's HWND: AppKit wants its view

            if (view == IntPtr.Zero || mimeTypes is null || mimeTypes.Length == 0) return EffectNone;

            try
            {
                // The drag must be attached to the mouse event that started it. AppKit refuses a
                // session begun from anything else, which is what stops a drag appearing from
                // nowhere -- and is why a programmatic DoDragDrop with no button held does nothing.
                IntPtr app = Send(objc_getClass("NSApplication"), Sel("sharedApplication"));
                IntPtr theEvent = app == IntPtr.Zero ? IntPtr.Zero : Send(app, Sel("currentEvent"));
                if (theEvent == IntPtr.Zero) return EffectNone;

                IntPtr source = EnsureSourceObject();
                if (source == IntPtr.Zero) return EffectNone;

                IntPtr item = Send(Send(objc_getClass("NSPasteboardItem"), Sel("alloc")), Sel("init"));
                if (item == IntPtr.Zero) return EffectNone;

                bool anyData = false;
                string[]? fileUrls = null;
                foreach (string mime in mimeTypes)
                {
                    byte[]? bytes = writer(mime);
                    if (bytes is null) continue;

                    // A file list is one file URL per pasteboard item, as Finder puts several files there:
                    // the first file on this item, the others on items of their own below. A uri-list's
                    // lines under public.file-url were no URL at all, and nothing could take them.
                    if (mime == "text/uri-list")
                    {
                        fileUrls = FileUrls(bytes);
                        if (fileUrls.Length == 0) continue;
                        bytes = System.Text.Encoding.UTF8.GetBytes(fileUrls[0]);
                    }

                    // Only what the pasteboard took: setData:forType: refuses a type it cannot hold (an
                    // app's private format whose name makes no valid type identifier), and an item with
                    // none of its data is no item -- AppKit then throws an Objective-C exception from
                    // beginDraggingSession ("0 items on the pasteboard, but 1 drag images") that nothing
                    // managed can catch, and the process ended: a grid's column drag in a trace viewer.
                    if (SendBoolPtrPtr(item, Sel("setData:forType:"), NSData(bytes), NSStr(UtiForMime(mime))))
                        anyData = true;
                }
                // Nothing the system could carry: the drag stays inside the application (started=false).
                if (!anyData) return EffectNone;

                // A frame at the pointer with no image: AppKit needs somewhere to anchor the drag,
                // and WPF has no drag visual to give it. The pointer still shows the drag cursor.
                NSPoint at = SendPoint(theEvent, Sel("locationInWindow"));
                var frame = new NSRect { origin = at, size = new NSSize { width = 1, height = 1 } };

                IntPtr items = Send(objc_getClass("NSMutableArray"), Sel("array"));
                IntPtr dragItem = SendPtrPtr(
                    Send(objc_getClass("NSDraggingItem"), Sel("alloc")),
                    Sel("initWithPasteboardWriter:"), item);
                if (dragItem == IntPtr.Zero || items == IntPtr.Zero) return EffectNone;
                SendVoidRectPtr(dragItem, Sel("setDraggingFrame:contents:"), frame, IntPtr.Zero);
                SendVoidPtr(items, Sel("addObject:"), dragItem);
                for (int f = 1; fileUrls != null && f < fileUrls.Length; f++)
                {
                    IntPtr fileItem = Send(Send(objc_getClass("NSPasteboardItem"), Sel("alloc")), Sel("init"));
                    if (fileItem == IntPtr.Zero
                        || !SendBoolPtrPtr(fileItem, Sel("setData:forType:"), NSData(System.Text.Encoding.UTF8.GetBytes(fileUrls[f])), NSStr("public.file-url")))
                        continue;
                    IntPtr extra = SendPtrPtr(Send(objc_getClass("NSDraggingItem"), Sel("alloc")), Sel("initWithPasteboardWriter:"), fileItem);
                    if (extra == IntPtr.Zero) continue;
                    SendVoidRectPtr(extra, Sel("setDraggingFrame:contents:"), frame, IntPtr.Zero);
                    SendVoidPtr(items, Sel("addObject:"), extra);
                }

                s_allowedOperations = 0;
                if ((allowedEffects & EffectCopy) != 0) s_allowedOperations |= NSDragOperationCopy;
                if ((allowedEffects & EffectMove) != 0) s_allowedOperations |= NSDragOperationMove;
                if ((allowedEffects & EffectLink) != 0) s_allowedOperations |= NSDragOperationLink;

                s_dragEnded = false;
                s_dragOperation = NSDragOperationNone;
                s_giveFeedback = giveFeedback;

                IntPtr session = SendPtrPtrPtrPtr(
                    view, Sel("beginDraggingSessionWithItems:event:source:"), items, theEvent, source);
                if (session == IntPtr.Zero) return EffectNone;

                // Past the point of no return: whatever happens now is a real drag's outcome.
                started = true;
                s_moves = 0;
                TraceDrag($"session 0x{session:x} begun from event type {(nint)Send(theEvent, Sel("type"))} in view 0x{view:x}, {mimeTypes.Length} types");

                RaiseInitialFeedback(allowedEffects);
                // A host with a loop of its own (a WinForms form with no WPF) waits in it; WPF's in a
                // dispatcher frame.
                if (waitUntil != null) waitUntil(() => s_dragEnded);
                else PumpUntilDragEnded();
                return Performed(ToEffects(s_dragOperation));
            }
            catch (Exception e)
            {
                Log("start drag", e);
                return EffectNone;
            }
            finally
            {
                s_giveFeedback = null;
            }
        }

        /// <summary>The file URLs of a text/uri-list (comments and blank lines skipped).</summary>
        private static string[] FileUrls(byte[] uriList)
        {
            var urls = new List<string>();
            foreach (string raw in System.Text.Encoding.UTF8.GetString(uriList).Split('\n'))
            {
                string line = raw.Trim();
                if (line.Length > 0 && line[0] != '#') urls.Add(line);
            }
            return urls.ToArray();
        }

        /// <summary>
        ///  Blocks until the session reports back, which is what makes DoDragDrop synchronous as it
        ///  is on Windows. AppKit tracks the drag on the run loop the dispatcher is already pumping.
        /// </summary>
        private static void PumpUntilDragEnded()
        {
            var frame = new DispatcherFrame();

            // A bound, because this frame blocks the whole application: a session that somehow never
            // reports would otherwise read as a hang rather than as a drag that did nothing.
            DateTime deadline = DateTime.UtcNow.AddMinutes(2);

            var timer = new DispatcherTimer(DispatcherPriority.Send, Dispatcher.CurrentDispatcher)
            {
                Interval = TimeSpan.FromMilliseconds(16),
            };
            timer.Tick += (_, _) =>
            {
                if (s_dragEnded || DateTime.UtcNow > deadline) frame.Continue = false;
            };
            timer.Start();
            try { Dispatcher.PushFrame(frame); }
            finally { timer.Stop(); }
        }

        // ---- Objective-C runtime ----------------------------------------------------

        private const string ObjC = "/usr/lib/libobjc.A.dylib";

        private static IntPtr Sel(string name) => sel_registerName(name);

        private static void AddMethod(IntPtr cls, string selector, Delegate impl, string types)
            => class_addMethod(cls, Sel(selector), Marshal.GetFunctionPointerForDelegate(impl), types);

        private static IntPtr NSStr(string value)
        {
            IntPtr bytes = Marshal.StringToCoTaskMemUTF8(value ?? string.Empty);
            try { return SendPtrPtr(objc_getClass("NSString"), Sel("stringWithUTF8String:"), bytes); }
            finally { Marshal.FreeCoTaskMem(bytes); }
        }

        private static IntPtr NSData(byte[] data)
        {
            if (data.Length == 0) return Send(objc_getClass("NSData"), Sel("data"));
            return SendDataWithBytes(objc_getClass("NSData"), Sel("dataWithBytes:length:"), data, (nuint)data.Length);
        }

        private static string ReadString(IntPtr nsString)
        {
            if (nsString == IntPtr.Zero) return string.Empty;
            IntPtr utf8 = Send(nsString, Sel("UTF8String"));
            return utf8 == IntPtr.Zero ? string.Empty : (Marshal.PtrToStringUTF8(utf8) ?? string.Empty);
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct NSPoint { public double x; public double y; }

        [StructLayout(LayoutKind.Sequential)]
        private struct NSSize { public double width; public double height; }

        [StructLayout(LayoutKind.Sequential)]
        private struct NSRect { public NSPoint origin; public NSSize size; }

        [DllImport(ObjC)] private static extern IntPtr objc_getClass(string name);
        [DllImport(ObjC)] private static extern IntPtr sel_registerName(string name);
        [DllImport(ObjC)] private static extern IntPtr objc_allocateClassPair(IntPtr super, string name, UIntPtr extra);
        [DllImport(ObjC)] private static extern void objc_registerClassPair(IntPtr cls);
        [DllImport(ObjC)] [return: MarshalAs(UnmanagedType.I1)]
        private static extern bool class_addMethod(IntPtr cls, IntPtr sel, IntPtr imp, string types);

        // objc_msgSend is variadic in C; declare one typed alias per call shape used here.
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr Send(IntPtr receiver, IntPtr selector);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern void SendVoidPtr(IntPtr receiver, IntPtr selector, IntPtr arg);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr SendPtrPtr(IntPtr receiver, IntPtr selector, IntPtr arg);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr SendPtrNUInt(IntPtr receiver, IntPtr selector, nuint arg);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern ulong SendNUInt(IntPtr receiver, IntPtr selector);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern nint SendNInt(IntPtr receiver, IntPtr selector);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern NSPoint SendPoint(IntPtr receiver, IntPtr selector);
        // a struct of more than 16 bytes comes back through objc_msgSend_stret on x86_64 (an Intel Mac); arm64 has
        // only objc_msgSend, which returns any struct
        private static NSRect SendRectRect(IntPtr receiver, IntPtr selector, NSRect arg) => System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture == System.Runtime.InteropServices.Architecture.X64
            ? SendRectRectStret(receiver, selector, arg) : SendRectRectArm64(receiver, selector, arg);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern NSRect SendRectRectArm64(IntPtr receiver, IntPtr selector, NSRect arg);
        [DllImport(ObjC, EntryPoint = "objc_msgSend_stret")] private static extern NSRect SendRectRectStret(IntPtr receiver, IntPtr selector, NSRect arg);

        [DllImport(ObjC, EntryPoint = "objc_msgSend")] [return: MarshalAs(UnmanagedType.I1)]
        private static extern bool SendBoolPtrPtr(IntPtr receiver, IntPtr selector, IntPtr a, IntPtr b);

        [DllImport(ObjC, EntryPoint = "objc_msgSend")]
        private static extern void SendVoidRectPtr(IntPtr receiver, IntPtr selector, NSRect rect, IntPtr arg);

        [DllImport(ObjC, EntryPoint = "objc_msgSend")]
        private static extern IntPtr SendPtrPtrPtrPtr(IntPtr receiver, IntPtr selector, IntPtr a, IntPtr b, IntPtr c);

        [DllImport(ObjC, EntryPoint = "objc_msgSend")]
        private static extern IntPtr SendDataWithBytes(IntPtr receiver, IntPtr selector, byte[] bytes, nuint length);
    }

    /// <summary>A drop target that is not a WPF window (a WinForms form's). Points are on the screen as the
    /// head has it: on macOS AppKit's points with the origin at the bottom left; on Wayland the window
    /// layer's screen in device pixels. Effects are DragDropEffects values.</summary>
    public abstract class ForeignDropTarget
    {
        public abstract int DragEnter(double screenX, double screenY, string[] mimeTypes, Func<string, byte[]?> read, int allowedEffects);
        public abstract int DragOver(double screenX, double screenY, int allowedEffects);
        public abstract void DragLeave();
        public abstract int Drop(double screenX, double screenY, int allowedEffects);
    }

    /// <summary>The system's drag and drop for windows that are not WPF's: the WinForms hosts' forms.</summary>
    public static class ForeignDragDrop
    {
        /// <summary>macOS: adds the NSDraggingDestination methods to a view class being built (before
        /// objc_registerClassPair).</summary>
        public static void AddDestinationMethods(IntPtr viewClass) => CocoaDragDrop.AddViewDragging(viewClass);

        /// <summary>Makes a window a drop target answered by <paramref name="target"/>: on macOS its view (of a
        /// class given the methods above), on Wayland its WaylandWindow handle.</summary>
        public static void Register(IntPtr window, ForeignDropTarget target)
        {
            if (OperatingSystem.IsMacOS()) CocoaDragDrop.RegisterForeign(window, target);
            else if (OperatingSystem.IsLinux() && !OperatingSystem.IsAndroid()) Wayland.WaylandDragDrop.RegisterForeign(window, target);
        }

        public static void Unregister(IntPtr window)
        {
            if (OperatingSystem.IsMacOS()) CocoaDragDrop.UnregisterForeign(window);
            else if (OperatingSystem.IsLinux() && !OperatingSystem.IsAndroid()) Wayland.WaylandDragDrop.UnregisterForeign(window);
        }

        /// <summary>Starts a system drag from <paramref name="window"/> (with the pointer press it is part of),
        /// offering <paramref name="mimeTypes"/> as <paramref name="writer"/> writes them, and waits for its
        /// outcome in <paramref name="waitUntil"/> -- the host's own loop, run until the function it is given
        /// says the drag is over. <paramref name="started"/> is false when no drag could begin.</summary>
        public static int StartDrag(IntPtr window, string[] mimeTypes, Func<string, byte[]?> writer, int allowedEffects,
                                    Action<Func<bool>> waitUntil, out bool started)
        {
            if (OperatingSystem.IsMacOS())
                return CocoaDragDrop.StartDrag(window, mimeTypes, writer, allowedEffects, out started, null, waitUntil);
            if (OperatingSystem.IsLinux() && !OperatingSystem.IsAndroid())
                return Wayland.WaylandDragDrop.StartDrag(window, mimeTypes, writer, allowedEffects, out started, null, null, waitUntil);
            started = false;
            return 0;
        }

        /// <summary>Writes a line to the drag trace (WPF_TRACE_DRAG=1).</summary>
        public static void Trace(string what) => CocoaDragDrop.TraceDrag(what);
    }
}
