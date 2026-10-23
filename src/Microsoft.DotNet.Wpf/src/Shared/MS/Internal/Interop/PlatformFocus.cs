// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// The process's keyboard focus window, as Win32 keeps one (GetFocus/SetFocus): one implementation for
// every head and for both window systems of this stack -- WPF's windows (HwndWrapper) and the WinForms
// port's (its driver's handles). Win32 tells a window it lost the focus (WM_KILLFOCUS, wParam the window
// that gets it) and then the window that got it (WM_SETFOCUS, wParam the one that lost it); this does
// the same for any pair of windows of either kind, so focus moving between a WinForms control and an
// ElementHost's WPF tree is one change, as it is on Windows.
//
// user32's GetFocus is not asked, on Windows either: a window of this stack is not necessarily the one
// Windows gives the keyboard to (a WinForms control is a window of the port's driver, drawn into its
// form's host window; an ElementHost's WPF tree may be composited into that frame). What Windows knows
// is kept in step with this, not the other way round: a WPF window taking the focus is given the native
// keyboard by its head (user32's SetFocus for a real HWND, the key window on macOS, the page's keyboard
// canvas in the browser), and what a head sees the OS do on its own -- a window activated, a child
// window pressed -- it reports here (FocusMovedByPlatform).

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace MS.Internal.Interop
{
    public static class PlatformFocus
    {
        /// <summary>A kind of window the focus can be on: says which handles are its own, delivers a
        /// focus message to one of them, and names the native window its keyboard comes through.</summary>
        public interface IWindowSystem
        {
            /// <summary>Whether <paramref name="handle"/> is one of this system's windows.</summary>
            bool Owns(IntPtr handle);

            /// <summary>Sends <paramref name="msg"/> to the window, as SendMessage does.</summary>
            void Deliver(IntPtr handle, int msg, IntPtr wParam, IntPtr lParam);

            /// <summary>Whether the OS itself sends this window its focus messages (a real HWND on
            /// Windows, which gets them from user32 when its native focus moves): then they are not
            /// sent again from here.</summary>
            bool GetsNativeFocusMessages(IntPtr handle);

            /// <summary>Gives the window the platform's keyboard (its head's native focus), if it has
            /// a native window that takes it; nothing otherwise.</summary>
            void TakeNativeKeyboard(IntPtr handle);
        }

        private const int WM_SETFOCUS = 0x0007, WM_KILLFOCUS = 0x0008;

        private static readonly object s_lock = new object();
        private static readonly List<IWindowSystem> s_systems = new List<IWindowSystem>();
        private static IntPtr s_focus;
        // A focus change under way: the messages it sends make windows ask for the focus again
        // (WinForms moves it to a child from its WM_SETFOCUS), and those come after it, as on Windows.
        [ThreadStatic] private static int t_moving;

        public static void Register(IWindowSystem system)
        {
            if (system == null) return;
            lock (s_lock)
                if (!s_systems.Contains(system)) s_systems.Add(system);
        }

        /// <summary>GetFocus: the window with the keyboard, or zero.</summary>
        public static IntPtr GetFocus() => s_focus;

        /// <summary>SetFocus: <paramref name="handle"/> takes the keyboard (zero: no window has it), the
        /// window that had it is told first; the previous focus window.</summary>
        public static IntPtr SetFocus(IntPtr handle)
        {
            IntPtr previous = s_focus;
            if (handle == previous) return previous;
            IWindowSystem target = handle == IntPtr.Zero ? null : SystemOf(handle);
            Move(previous, handle);
            // The native keyboard after the record (a head that reports the move it causes,
            // FocusMovedByPlatform, finds it already made), and only if the window still has the
            // focus: one that passed it on from its WM_SETFOCUS (an ElementHost to its WPF tree) has
            // given the keyboard to that window already, and taking it here took it back.
            if (s_focus == handle) target?.TakeNativeKeyboard(handle);
            return previous;
        }

        /// <summary>A head saw the OS move the keyboard to <paramref name="handle"/> (a window made
        /// key, a child window pressed); zero when it left the process's windows. The windows are told
        /// as SetFocus tells them, except where the OS has told them itself.</summary>
        public static void FocusMovedByPlatform(IntPtr handle)
        {
            if (handle == s_focus) return;
            Move(s_focus, handle);
        }

        /// <summary>A window's procedure got a focus message from the OS (a real HWND on Windows): the
        /// record follows it. WM_SETFOCUS: the window has the focus; WM_KILLFOCUS: the window named
        /// in wParam has it, if this one still did.</summary>
        public static void NoteFocusMessage(IntPtr handle, int msg, IntPtr wParam)
        {
            if (t_moving > 0) return;   // one this sends
            if (msg == WM_SETFOCUS && s_focus != handle)
            {
                // The OS gave a real window the focus and told it, and the window that had it only if
                // that was a real one too: a driver window (a WinForms control) is told here.
                IntPtr previous = s_focus;
                s_focus = handle;
                if (previous != IntPtr.Zero && SystemOf(previous) is { } lost && !lost.GetsNativeFocusMessages(previous))
                {
                    t_moving++;
                    try { lost.Deliver(previous, WM_KILLFOCUS, handle, IntPtr.Zero); }
                    finally { t_moving--; }
                }
            }
            else if (msg == WM_KILLFOCUS && s_focus == handle) s_focus = wParam;
        }

        // WPF_TRACE_FOCUS=1 (diagnostics): every move of the focus, and who made it.
        private static readonly bool s_trace = Environment.GetEnvironmentVariable("WPF_TRACE_FOCUS") == "1";

        private static void Trace(string what, IntPtr from, IntPtr to)
        {
            if (!s_trace) return;
            string caller = new System.Diagnostics.StackTrace(2, false).GetFrame(0)?.GetMethod() is { } m ? m.DeclaringType?.Name + "." + m.Name : "?";
            Console.WriteLine($"[focus] {what} 0x{(long)from:x} -> 0x{(long)to:x} by {caller}");
        }

        private static void Move(IntPtr previous, IntPtr handle)
        {
            Trace("move", previous, handle);
            s_focus = handle;
            t_moving++;
            try
            {
                if (previous != IntPtr.Zero && SystemOf(previous) is { } lost && !lost.GetsNativeFocusMessages(previous))
                    lost.Deliver(previous, WM_KILLFOCUS, handle, IntPtr.Zero);
                // a window the loser's WM_KILLFOCUS gave the focus to instead has it now
                if (s_focus != handle) return;
                if (handle != IntPtr.Zero && SystemOf(handle) is { } got && !got.GetsNativeFocusMessages(handle))
                    got.Deliver(handle, WM_SETFOCUS, previous, IntPtr.Zero);
            }
            finally { t_moving--; }
        }

        private static IWindowSystem SystemOf(IntPtr handle)
        {
            lock (s_lock)
                foreach (IWindowSystem s in s_systems)
                    if (s.Owns(handle)) return s;
            return null;
        }

        // ---- Windows: user32's SetFocus for a real HWND, the one native call this makes ----

        /// <summary>Gives a real HWND the native keyboard (Windows).</summary>
        public static void SetNativeFocus(IntPtr hwnd)
        {
            if (OperatingSystem.IsWindows() && hwnd != IntPtr.Zero) NativeSetFocus(hwnd);
        }

        [DllImport("user32.dll", EntryPoint = "SetFocus")]
        private static extern IntPtr NativeSetFocus(IntPtr hWnd);
    }
}
