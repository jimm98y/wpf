// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// Drag and drop between a WinForms form and other applications on macOS.
//
// Only a WPF host connected WinForms to the system's drag and drop: a WindowsFormsHost drop target fed
// the driver's injections and started its drags through WPF. A form shown with no WPF around it --
// Application.Run(new MainForm()) -- was neither: a file dragged from the Finder onto a list that
// accepts drops showed "no drop", and DoDragDrop out of the form returned None at once.
//
// The form's view now answers AppKit's dragging-destination methods (WindowsBase's CocoaDragDrop, the
// one WPF's windows use, through a target of the form's own), and turns the drag into the driver's drag
// injections at the point it is over. A drag the form starts is a system drag session on the view: the
// data offered in the types other applications read (text, a file list as file URLs, HTML, RTF, a
// PNG), plus the in-process marker that carries the data object itself to a drop inside this process.
// While AppKit tracks it, the host's pump hands it the events, as it does for any window it does not
// handle.
//

extern alias WindowsBase;

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using ForeignDragDrop = WindowsBase::MS.Internal.Interop.ForeignDragDrop;
using ForeignDropTarget = WindowsBase::MS.Internal.Interop.ForeignDropTarget;

internal sealed partial class CocoaHost
{
    /// <summary>A system drag this process's forms started is running: the pump gives AppKit the events.</summary>
    private static bool s_systemDragActive;

    private FormDropTarget _dropTarget;

    /// <summary>Makes the form's view a drop target and its drags system drags. Called once the view exists.</summary>
    private void EnableDragDrop()
    {
        if (_imageView == IntPtr.Zero) return;
        try
        {
            _dropTarget ??= new FormDropTarget(this);
            ForeignDragDrop.Register(_imageView, _dropTarget);
            if (_form.IsHandleCreated) _driver.RegisterDragSource(_form.Handle, StartSystemDrag);
        }
        catch (Exception e) { Console.Error.WriteLine("cocoa host: drag and drop unavailable: " + e.Message); }
    }

    private void DisableDragDrop(IntPtr view)
    {
        try
        {
            if (view != IntPtr.Zero) ForeignDragDrop.Unregister(view);
            if (_form.IsHandleCreated) _driver.UnregisterDragSource(_form.Handle);
        }
        catch { }
    }

    /// <summary>An AppKit screen point (points, bottom-left origin) in the driver's screen space, where the
    /// form's controls hit-test -- as a mouse event at that point would be.</summary>
    private Point DriverPoint(double screenX, double screenY)
    {
        NSRect frame = SendRectRet(_window, Sel("frame"));
        double lx = (screenX - frame.x) * _k, ly = (screenY - frame.y) * _k;   // in the content, still bottom-up
        Rectangle client = ClientScreen;
        return new Point((int)lx + client.Left, (int)(client.Height - ly) + client.Top);
    }

    /// <summary>DragEventArgs.KeyState: the left button, and the modifiers as Windows names them (Option
    /// copies on a Mac as Ctrl does on Windows; Command moves as Shift does).</summary>
    private static int KeyState()
    {
        nuint flags = (nuint)(nint)Send(Cls("NSEvent"), Sel("modifierFlags"));
        int state = 1;                                         // MK_LBUTTON: a drag is a left-button drag
        if ((flags & (1 << 17)) != 0) state |= 4;              // Shift
        if ((flags & (1 << 19)) != 0) state |= 8;              // Option -> Ctrl (copy)
        if ((flags & (1 << 20)) != 0) state |= 4;              // Command -> Shift (move)
        if ((flags & (1 << 18)) != 0) state |= 32;             // Control -> Alt (link)
        return state;
    }

    // ---- the drop side -------------------------------------------------------------------

    private sealed class FormDropTarget : ForeignDropTarget
    {
        private readonly CocoaHost _host;
        private IDataObject _data;

        public FormDropTarget(CocoaHost host) => _host = host;

        public override int DragEnter(double screenX, double screenY, string[] mimeTypes, Func<string, byte[]> read, int allowedEffects)
        {
            _data = SystemDragData.ToDataObject(mimeTypes, read);
            return Deliver(screenX, screenY, allowedEffects, drop: false, enter: true);
        }

        public override int DragOver(double screenX, double screenY, int allowedEffects)
            => Deliver(screenX, screenY, allowedEffects, drop: false, enter: false);

        public override void DragLeave()
        {
            _data = null;
            try { _host._driver.InjectDragLeave(); } catch { }
        }

        public override int Drop(double screenX, double screenY, int allowedEffects)
        {
            int effect = Deliver(screenX, screenY, allowedEffects, drop: true, enter: false);
            _data = null;
            return effect;
        }

        private int Deliver(double screenX, double screenY, int allowedEffects, bool drop, bool enter)
        {
            if (_data == null || _host._closed || !_host._form.IsHandleCreated) return 0;
            try
            {
                Point p = _host.DriverPoint(screenX, screenY);
                var allowed = (DragDropEffects)allowedEffects;
                IntPtr root = _host._form.Handle;
                DragDropEffects effect = drop
                    ? _host._driver.InjectDragDropIn(root, p.X, p.Y, _data, allowed, KeyState())
                    : enter
                        ? _host._driver.InjectDragEnterIn(root, p.X, p.Y, _data, allowed, KeyState())
                        : _host._driver.InjectDragOverIn(root, p.X, p.Y, _data, allowed, KeyState());
                // the form repaints its drop feedback before AppKit takes the pointer again
                _host.Present();
                return (int)(effect & allowed);
            }
            catch (Exception e)
            {
                Console.Error.WriteLine("cocoa host: drop target threw: " + e);
                return 0;
            }
        }
    }

    // ---- the source side -----------------------------------------------------------------

    /// <summary>Control.DoDragDrop from one of this form's controls: a system drag session.</summary>
    private DragDropEffects StartSystemDrag(object data, DragDropEffects allowed)
    {
        if (_imageView == IntPtr.Zero || s_systemDragActive) return DragDropEffects.None;
        IDataObject dataObject = data as IDataObject ?? new DataObject(data);

        // Where WPF can start it (a WindowsFormsHost lent the driver its drag), WPF does: its drag is a
        // system drag too, and a WPF window dropped on gets the data object as WPF hands it over. A form's
        // own drop target recognises the drag by its in-process marker and takes the object from here.
        if (_driver.StartDragRequested is { } wpf)
        {
            SystemDragData.Current = dataObject;
            try { return wpf(data, allowed); }
            finally { SystemDragData.Current = null; }
        }

        Dictionary<string, byte[]> payload = SystemDragData.Payload(dataObject);

        var mimes = new string[payload.Count];
        payload.Keys.CopyTo(mimes, 0);
        SystemDragData.Current = dataObject;
        s_systemDragActive = true;
        try
        {
            ForeignDragDrop.Trace($"winforms drag from 0x{_imageView:x}: {string.Join(", ", mimes)}, current event type {(nint)Send(Send(App(), Sel("currentEvent")), Sel("type"))}");
            int effect = ForeignDragDrop.StartDrag(_imageView, mimes,
                mime => payload.TryGetValue(mime, out byte[] bytes) ? bytes : null,
                (int)allowed, WaitForDrag, out bool started);
            ForeignDragDrop.Trace($"winforms drag returned: started {started}, effect {effect}");
            return started ? (DragDropEffects)effect & allowed : DragDropEffects.None;
        }
        catch (Exception e)
        {
            Console.Error.WriteLine("cocoa host: the drag threw: " + e);
            return DragDropEffects.None;
        }
        finally
        {
            s_systemDragActive = false;
            SystemDragData.Current = null;
        }
    }

    /// <summary>DoDragDrop returns with the drag's outcome, as on Windows: the application's messages
    /// go on meanwhile, and AppKit, given the events, tracks the drag.</summary>
    private static void WaitForDrag(Func<bool> ended)
    {
        DateTime deadline = DateTime.UtcNow.AddMinutes(2);
        IntPtr mode = NSStr("kCFRunLoopDefaultMode");
        DateTime? buttonUpSince = null;
        bool nudged = false;
        while (!ended() && DateTime.UtcNow < deadline)
        {
            // A safety net: the button is up and AppKit has not ended the drag. A mouse-up posted to
            // this application's own queue (no permission needed) gives its drag tracking the release;
            // if even that does not end it, stop waiting rather than hold the application.
            bool down = ((nuint)(nint)Send(Cls("NSEvent"), Sel("pressedMouseButtons")) & 1) != 0;
            if (down) buttonUpSince = null;
            else buttonUpSince ??= DateTime.UtcNow;
            if (buttonUpSince is { } since && DateTime.UtcNow - since > TimeSpan.FromSeconds(1))
            {
                if (!nudged)
                {
                    nudged = true;
                    ForeignDragDrop.Trace("button up for 1 s and the drag not ended: posting a mouse-up");
                    PostMouseUp();
                }
                else if (DateTime.UtcNow - since > TimeSpan.FromSeconds(3))
                {
                    ForeignDragDrop.Trace("still not ended 3 s after the release: no longer waiting");
                    return;
                }
            }

            // The events taken off the queue and routed (to AppKit, while s_systemDragActive): only
            // waiting for one left them where they were, and the drag, never seeing the button come up,
            // never ended -- the application spun.
            IntPtr until = SendPtrDouble(Cls("NSDate"), Sel("dateWithTimeIntervalSinceNow:"), 0.016);
            IntPtr evt = SendNextEvent(App(), Sel("nextEventMatchingMask:untilDate:inMode:dequeue:"),
                unchecked((nuint)ulong.MaxValue), until, mode, true);
            if (evt != IntPtr.Zero)
            {
                nint type = (nint)Send(evt, Sel("type"));
                if (type != 5 && type != 6) ForeignDragDrop.Trace($"waiting: event type {type} to AppKit");
                Route(evt);
            }
            Application.DoEvents();
        }
    }

    /// <summary>A left-mouse-up at the pointer, posted to this application's own event queue.</summary>
    private static void PostMouseUp()
    {
        try
        {
            NSPoint at = SendPointRet(Cls("NSEvent"), Sel("mouseLocation"));
            IntPtr key = Send(App(), Sel("keyWindow"));
            nint number = key == IntPtr.Zero ? 0 : (nint)Send(key, Sel("windowNumber"));
            NSPoint inWindow = at;
            if (key != IntPtr.Zero)
            {
                NSRect f = SendRectRet(key, Sel("frame"));
                inWindow = new NSPoint { x = at.x - f.x, y = at.y - f.y };
            }
            IntPtr up = SendMouseEvent(Cls("NSEvent"), Sel("mouseEventWithType:location:modifierFlags:timestamp:windowNumber:context:eventNumber:clickCount:pressure:"),
                2 /* LeftMouseUp */, inWindow, 0, 0, number, IntPtr.Zero, 0, 1, 0f);
            if (up != IntPtr.Zero) SendVoidPtrBool(App(), Sel("postEvent:atStart:"), up, true);
        }
        catch (Exception e) { ForeignDragDrop.Trace("posting the mouse-up threw: " + e.Message); }
    }

    [System.Runtime.InteropServices.DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
    private static extern IntPtr SendMouseEvent(IntPtr r, IntPtr s, nuint type, NSPoint location, nuint flags, double timestamp,
        nint windowNumber, IntPtr context, nint eventNumber, nint clickCount, float pressure);
}
