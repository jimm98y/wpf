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

        // A drag can only be started by the WPF element that is showing the control, and an app can
        // have several such elements: a WindowsFormsHost here, a claimed foreign HwndHost there.
        // Register one per hosted root and pick the one whose subtree the dragging control belongs
        // to, so the drag starts from the element the user is actually dragging in.
        private readonly Dictionary<IntPtr, Func<object, DragDropEffects, DragDropEffects>> _dragSources
            = new Dictionary<IntPtr, Func<object, DragDropEffects, DragDropEffects>>();

        internal void RegisterDragSource(IntPtr root, Func<object, DragDropEffects, DragDropEffects> start)
        {
            if (root != IntPtr.Zero && start != null) _dragSources[root] = start;
        }

        internal void UnregisterDragSource(IntPtr root)
        {
            if (root != IntPtr.Zero) _dragSources.Remove(root);
        }

        private Func<object, DragDropEffects, DragDropEffects> FindDragSource(IntPtr handle)
        {
            if (_dragSources.Count == 0) return null;
            for (Hwnd h = Hwnd.ObjectFromHandle(handle); h != null; h = h.parent)
                if (_dragSources.TryGetValue(h.Handle, out Func<object, DragDropEffects, DragDropEffects> start))
                    return start;
            return null;
        }

        internal override DragDropEffects StartDrag(IntPtr handle, object data, DragDropEffects allowedEffects)
        {
            // The control that starts a drag grabbed the mouse on button-down and will never see the
            // button-up that would release it: the drag consumes the rest of the gesture. Left set,
            // that capture silently swallows every later click, because a captured window owns all
            // mouse input -- after dragging a button on the design surface, nothing on it could be
            // selected again.
            ReleaseCaptureForDrag();

            Func<object, DragDropEffects, DragDropEffects> start = FindDragSource(handle) ?? StartDragRequested;
            DragDropEffects result = start == null ? DragDropEffects.None : start(data, allowedEffects);

            ReleaseCaptureForDrag();     // and again: the drag may have taken it back
            return result;
        }

        private void ReleaseCaptureForDrag()
        {
            IntPtr grabbed = _grabHandle;
            if (grabbed == IntPtr.Zero) return;

            // Through the control where there is one, so the managed Capture flag agrees with the
            // driver's; Control.InternalCapture ungrabs on its way.
            Control c = Control.FromHandle(grabbed);
            if (c != null) c.InternalCapture = false;
            else UngrabWindow(grabbed);
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
        // Subtree-scoped drag delivery, for a host that owns one hosted subtree rather than the whole
        // WinForms world. Every hosted container sits at driver (0,0), so a GLOBAL hit test picks
        // whichever host comes first at that point -- the same trap that once made clicks in one pad
        // land in another, and it would put a dragged control into the wrong pad entirely.

        internal DragDropEffects InjectDragEnterIn(IntPtr root, int screenX, int screenY,
            IDataObject data, DragDropEffects allowed, int keyState)
            => DeliverDragTo(DropTargetIn(root, screenX, screenY), screenX, screenY, data, allowed,
                keyState, DragPhase.Over);

        internal DragDropEffects InjectDragOverIn(IntPtr root, int screenX, int screenY,
            IDataObject data, DragDropEffects allowed, int keyState)
            => DeliverDragTo(DropTargetIn(root, screenX, screenY), screenX, screenY, data, allowed,
                keyState, DragPhase.Over);

        internal DragDropEffects InjectDragDropIn(IntPtr root, int screenX, int screenY,
            IDataObject data, DragDropEffects allowed, int keyState)
            => DeliverDragTo(DropTargetIn(root, screenX, screenY), screenX, screenY, data, allowed,
                keyState, DragPhase.Drop);

        /// <summary>The drop-registered control at a point, searching only <paramref name="root"/>'s
        /// subtree. See <see cref="DropTargetAt"/> for why it walks up to an ancestor.</summary>
        private Control DropTargetIn(IntPtr root, int screenX, int screenY)
        {
            IntPtr handle = WindowAtPointIn(root, screenX, screenY);
            for (Control c = handle == IntPtr.Zero ? null : Control.FromHandle(handle); c != null; c = c.Parent)
            {
                if (c.IsHandleCreated && _dropTargets.Contains(c.Handle))
                {
                    return c;
                }
            }
            return null;
        }

        private DragDropEffects DeliverDrag(int screenX, int screenY, IDataObject data,
            DragDropEffects allowed, int keyState, DragPhase phase)
            => DeliverDragTo(DropTargetAt(screenX, screenY), screenX, screenY, data, allowed, keyState, phase);

        private DragDropEffects DeliverDragTo(Control target, int screenX, int screenY, IDataObject data,
            DragDropEffects allowed, int keyState, DragPhase phase)
        {
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
