// Drag and drop for the hosted-WinForms driver.
//
// WPF's own drag and drop works on every head (PlatformDragDrop and the per-platform backends).
// WinForms controls hosted in a WindowsFormsHost did not take part in it: XplatUIWebGpu never
// overrode SetAllowDrop or StartDrag, so both fell through to XplatUIDriver's base implementations,
// which write "Drag and Drop is currently not supported on this platform" to stderr and do nothing.
// A WinForms control with AllowDrop set could not be dropped on, and Control.DoDragDrop returned
// None without starting anything.
//
// The window belongs to WPF, so there is no second platform drag stack to build here. The host
// element is already a WPF drop target sitting exactly where the WinForms control is drawn, and it
// already translates WPF mouse and keyboard into driver injections. Drag and drop follows that
// shape: WindowsFormsHost receives the WPF drag events and calls the injectors below, which route
// to the WinForms control under the point the same way InjectMouse routes a click.
//
// Starting a drag goes the other way. StartDrag cannot call WPF directly without the driver knowing
// about the host, so it raises a request the host installs a handler for; the host calls
// DragDrop.DoDragDrop with itself as the source, which is what puts the drag on the platform.
//

using System.Collections.Generic;
using System.Drawing;

namespace System.Windows.Forms
{
    internal partial class XplatUIWebGpu : XplatUIDriver
    {
        private readonly HashSet<IntPtr> _dropTargets = new HashSet<IntPtr>();

        /// <summary>The control the drag is currently inside, for enter/leave bookkeeping.</summary>
        private Control _dragOverControl;

        /// <summary>
        /// Raised by <see cref="StartDrag"/>. WindowsFormsHost installs this and answers it by
        /// calling WPF's DragDrop.DoDragDrop, which is what the platform actually understands. Null
        /// where nothing is hosting, in which case a drag cannot start and None is the honest answer.
        /// </summary>
        internal Func<object, DragDropEffects, DragDropEffects> StartDragRequested;

        /// <summary>True once any hosted control has asked to be a drop target.</summary>
        internal bool HasDropTargets => _dropTargets.Count > 0;

        internal override void SetAllowDrop(IntPtr handle, bool value)
        {
            if (value)
            {
                _dropTargets.Add(handle);
            }
            else
            {
                _dropTargets.Remove(handle);
            }
        }

        internal override DragDropEffects StartDrag(IntPtr handle, object data, DragDropEffects allowedEffects)
        {
            Func<object, DragDropEffects, DragDropEffects> start = StartDragRequested;
            return start == null ? DragDropEffects.None : start(data, allowedEffects);
        }

        // ---- the drop side, driven by WindowsFormsHost ----------------------------------------

        internal DragDropEffects InjectDragEnter(int screenX, int screenY, IDataObject data,
            DragDropEffects allowed, int keyState)
            => DeliverDrag(screenX, screenY, data, allowed, keyState, DragPhase.Over);

        internal DragDropEffects InjectDragOver(int screenX, int screenY, IDataObject data,
            DragDropEffects allowed, int keyState)
            => DeliverDrag(screenX, screenY, data, allowed, keyState, DragPhase.Over);

        internal DragDropEffects InjectDragDrop(int screenX, int screenY, IDataObject data,
            DragDropEffects allowed, int keyState)
            => DeliverDrag(screenX, screenY, data, allowed, keyState, DragPhase.Drop);

        internal void InjectDragLeave()
        {
            Control was = _dragOverControl;
            _dragOverControl = null;
            was?.DndLeave(EventArgs.Empty);
        }

        private enum DragPhase { Over, Drop }

        /// <summary>
        /// Routes one drag event to the control under the point, raising DragEnter first whenever
        /// the control has changed and DragLeave on the one being left. WinForms controls expect
        /// that pairing: a control that never saw an enter will not have set up whatever its
        /// DragOver handler reads.
        /// </summary>
        private DragDropEffects DeliverDrag(int screenX, int screenY, IDataObject data,
            DragDropEffects allowed, int keyState, DragPhase phase)
        {
            Control target = DropTargetAt(screenX, screenY);

            if (!ReferenceEquals(target, _dragOverControl))
            {
                _dragOverControl?.DndLeave(EventArgs.Empty);
                _dragOverControl = target;
                if (target != null)
                {
                    var entering = new DragEventArgs(data, keyState, screenX, screenY, allowed, DragDropEffects.None);
                    target.DndEnter(entering);
                }
            }

            if (target == null)
            {
                return DragDropEffects.None;
            }

            var e = new DragEventArgs(data, keyState, screenX, screenY, allowed, DragDropEffects.None);
            if (phase == DragPhase.Drop)
            {
                target.DndDrop(e);
                _dragOverControl = null;
            }
            else
            {
                target.DndOver(e);
            }
            return e.Effect;
        }

        /// <summary>
        /// The drop-registered control at a screen point. The window under the point may be a child
        /// that never called AllowDrop while an ancestor did -- a Panel that accepts drops holding
        /// labels that do not -- so this walks up until it finds one that did, which is what Win32
        /// drop targeting does.
        /// </summary>
        private Control DropTargetAt(int screenX, int screenY)
        {
            IntPtr handle = WindowAtPoint(screenX, screenY);
            for (Control c = handle == IntPtr.Zero ? null : Control.FromHandle(handle); c != null; c = c.Parent)
            {
                if (c.IsHandleCreated && _dropTargets.Contains(c.Handle))
                {
                    return c;
                }
            }
            return null;
        }
    }
}
