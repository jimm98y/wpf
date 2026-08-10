// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// A drag-and-drop operation run entirely in managed code, for the heads whose backend has no drag
// transport of its own: Android, iOS and the browser.
//
// The observation that makes this worth having is that a drag WITHIN one application needs no
// platform involvement at all. Everything the operation consists of -- which element is under the
// pointer, whether it has AllowDrop set, the DragEnter/DragOver/DragLeave/Drop routed events, the
// effect the target chose, and the QueryContinueDrag that lets Escape call the whole thing off --
// is WPF's own code, already written and already used by every other head. The only thing a
// compositor or AppKit contributes is carrying the data to ANOTHER application.
//
// So rather than three heads on which dragging does nothing, this drives the same OleDropTarget the
// platform backends drive, from the mouse position WPF already tracks. Data crossing the process
// boundary is what those heads still lack -- and that is a smaller and more honest gap than "drag
// does nothing at all".
//
// The pointer is polled on a dispatcher timer rather than hooked into the input stream. A drag is
// a modal state (it runs inside a nested frame, as it does on Windows), the tick is cheap, and
// polling reads the same MouseDevice a handler would have been told about -- while a temporary
// input filter would have to be unwound correctly on every exit path, including a handler throwing.
//

#nullable enable

using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using MS.Internal;
using MS.Internal.Interop;
using MS.Win32;

namespace System.Windows
{
    internal static class ManagedDragLoop
    {
        /// <summary>
        ///  True on the heads that have no platform drag transport, and therefore want this.
        /// </summary>
        internal static bool IsRequired =>
            OperatingSystem.IsAndroid() || OperatingSystem.IsIOS() || OperatingSystem.IsBrowser();

        /// <summary>
        ///  Runs a drag to completion and returns the effect that was performed.
        /// </summary>
        /// <param name="eventSource">
        ///  The drag source's event side -- the same <see cref="OleDragSource"/> Windows uses, asked
        ///  the same questions.
        /// </param>
        internal static DragDropEffects Run(DependencyObject dragSource,
                                            DragDropEffects allowedEffects,
                                            UnsafeNativeMethods.IOleDropSource eventSource)
        {
            // A drag from an element that is in no window has nowhere to happen.
            if (PresentationSource.FromDependencyObject(dragSource) is not HwndSource)
            {
                return DragDropEffects.None;
            }

            // There must be a gesture underway to drag WITH. Without this, a DoDragDrop called from
            // nowhere -- a timer, a startup path -- would find the button already up on the first
            // tick and instantly "drop" wherever the pointer happened to be resting. A touch drag
            // does satisfy this: WPF promotes the primary contact to the mouse.
            if (Mouse.PrimaryDevice.LeftButton != MouseButtonState.Pressed)
            {
                return DragDropEffects.None;
            }

            IPlatformDropTarget target = PlatformDropTarget.Instance;

            // The data never goes near a wire, so the only type advertised is the marker that tells
            // PlatformDropTarget to hand the drop the original object. See PlatformDragSource.
            string[] types = { PlatformDragSource.InProcessMime };
            Func<string, byte[]?> read = _ => Array.Empty<byte>();

            var state = new DragState(target, types, read, (int)allowedEffects, eventSource);
            var frame = new DispatcherFrame();

            var timer = new DispatcherTimer(DispatcherPriority.Send, Dispatcher.CurrentDispatcher)
            {
                Interval = TimeSpan.FromMilliseconds(16),
            };
            timer.Tick += (_, _) =>
            {
                // A handler that throws must not leave the application stuck in a nested frame with
                // a drag half-entered, so the loop ends the operation instead of propagating.
                try
                {
                    if (state.Tick()) frame.Continue = false;
                }
                catch
                {
                    state.Cancel();
                    frame.Continue = false;
                    throw;
                }
            };

            timer.Start();
            try { Dispatcher.PushFrame(frame); }
            finally { timer.Stop(); }

            return (DragDropEffects)state.Performed;
        }

        /// <summary>
        ///  One in-flight drag. Split out so the timer body reads as the state machine it is.
        /// </summary>
        private sealed class DragState
        {
            private readonly IPlatformDropTarget _target;
            private readonly string[] _types;
            private readonly Func<string, byte[]?> _read;
            private readonly int _allowedEffects;
            private readonly UnsafeNativeMethods.IOleDropSource _eventSource;

            // The window the drag is currently inside, or Zero. Kept so a drag moving between two of
            // the application's own windows leaves one target before entering the next -- the
            // sequence a drop handler is entitled to see.
            private IntPtr _inside;

            internal int Performed { get; private set; }

            internal DragState(IPlatformDropTarget target, string[] types,
                               Func<string, byte[]?> read, int allowedEffects,
                               UnsafeNativeMethods.IOleDropSource eventSource)
            {
                _target = target;
                _types = types;
                _read = read;
                _allowedEffects = allowedEffects;
                _eventSource = eventSource;
            }

            /// <summary>Advances the drag. Returns true when it is over.</summary>
            internal bool Tick()
            {
                MouseDevice mouse = Mouse.PrimaryDevice;
                bool released = mouse.LeftButton != MouseButtonState.Pressed;

                // Ask the source first, exactly as OLE does: a handler may cancel (or on release,
                // complete) the drag before the target ever sees this position.
                int hr = _eventSource.OleQueryContinueDrag(
                    Keyboard.IsKeyDown(Key.Escape) ? 1 : 0, (int)KeyStates(released));

                if (hr == NativeMethods.DRAGDROP_S_CANCEL)
                {
                    Cancel();
                    return true;
                }

                bool drop = hr == NativeMethods.DRAGDROP_S_DROP || released;

                if (!TryLocate(mouse, out HwndSource? over, out int screenX, out int screenY))
                {
                    // Off every window of ours. A drop here goes nowhere, and a drag here is outside
                    // any target, so leave whichever one we were in.
                    Leave();
                    if (!drop) return false;

                    Performed = 0;
                    return true;
                }

                IntPtr handle = over!.Handle;
                if (handle != _inside)
                {
                    Leave();
                    _inside = handle;
                    _target.DragEnter(handle, screenX, screenY, _types, _read, _allowedEffects);
                }

                if (drop)
                {
                    Performed = _target.Drop(handle, screenX, screenY, _allowedEffects);
                    _inside = IntPtr.Zero;
                    return true;
                }

                int effect = _target.DragOver(handle, screenX, screenY, _allowedEffects);
                _eventSource.OleGiveFeedback(effect);
                return false;
            }

            internal void Cancel()
            {
                Leave();
                Performed = 0;
            }

            private void Leave()
            {
                if (_inside == IntPtr.Zero) return;
                _target.DragLeave(_inside);
                _inside = IntPtr.Zero;
            }

            /// <summary>
            ///  The window under the pointer and the pointer's position in the screen device pixels
            ///  the seam takes.
            /// </summary>
            /// <remarks>
            ///  The point makes the same trip a real platform drag's does, only backwards: WPF's
            ///  logical coordinates to Win32 client pixels to screen pixels, so that the drop
            ///  target's own ScreenToClient lands back exactly where the mouse is.
            /// </remarks>
            private bool TryLocate(MouseDevice mouse, out HwndSource? over, out int screenX, out int screenY)
            {
                over = null;
                screenX = screenY = 0;

                // Mouse.DirectlyOver names the window the pointer is really in, which is what makes a
                // drag from one of the application's windows into another one work. It is null when
                // the pointer is outside them all -- including over another application.
                HwndSource? source = mouse.DirectlyOver is DependencyObject element
                    ? PresentationSource.FromDependencyObject(element) as HwndSource
                    : null;

                if (source is null || source.RootVisual is null) return false;

                Point logical = mouse.GetPosition(source.RootVisual as IInputElement);
                Point client = PointUtil.RootToClient(logical, source);
                Point screen = PointUtil.ClientToScreen(client, source);

                over = source;
                screenX = (int)Math.Round(screen.X);
                screenY = (int)Math.Round(screen.Y);
                return true;
            }

            private static DragDropKeyStates KeyStates(bool released)
            {
                DragDropKeyStates states = released
                    ? DragDropKeyStates.None
                    : DragDropKeyStates.LeftMouseButton;

                ModifierKeys modifiers = Keyboard.Modifiers;
                if ((modifiers & ModifierKeys.Control) != 0) states |= DragDropKeyStates.ControlKey;
                if ((modifiers & ModifierKeys.Shift) != 0) states |= DragDropKeyStates.ShiftKey;
                if ((modifiers & ModifierKeys.Alt) != 0) states |= DragDropKeyStates.AltKey;
                return states;
            }
        }
    }
}
