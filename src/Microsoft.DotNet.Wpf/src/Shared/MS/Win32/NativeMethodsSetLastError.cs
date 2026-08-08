// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Text;
using System.Runtime.InteropServices;
using MS.Win32;

// Description: P/Invokes for methods that need to call SetLastError(0)

// The NativeMethodsSetLastError class differs between assemblies and could not actually be
//  shared, so it is duplicated across namespaces to prevent name collision.
#if WINDOWS_BASE
namespace MS.Internal.WindowsBase
#elif UIAUTOMATIONCLIENT
namespace MS.Internal.UIAutomationClient
#elif UIAUTOMATIONCLIENTSIDEPROVIDERS
namespace MS.Internal.UIAutomationClientSideProviders
#elif WINDOWSFORMSINTEGRATION
namespace MS.Internal.WinFormsIntegration
#elif UIAUTOMATIONTYPES
namespace MS.Internal.UIAutomationTypes
#elif DRT
namespace MS.Internal.Drt
#else
#error Class is being used from an unknown assembly.
#endif
{
    internal static class NativeMethodsSetLastError
    {
        private const string PresentationNativeDll = "PresentationNative_cor3.dll";

#if WINDOWSFORMSINTEGRATION     // WinFormsIntegration

        [DllImport(PresentationNativeDll, EntryPoint="EnableWindowWrapper", SetLastError = true, ExactSpelling = true, CharSet = System.Runtime.InteropServices.CharSet.Auto)]
        public static extern bool EnableWindow(IntPtr hWnd, bool enable);

#elif UIAUTOMATIONCLIENT || UIAUTOMATIONCLIENTSIDEPROVIDERS   // UIAutomation

        [DllImport(PresentationNativeDll, EntryPoint="GetWindowLongWrapper", CharSet=CharSet.Auto, SetLastError=true)]
        public static extern Int32 GetWindowLong(IntPtr hWnd, int nIndex );

        [DllImport(PresentationNativeDll, EntryPoint="GetWindowLongPtrWrapper", CharSet=CharSet.Auto, SetLastError=true)]
        public static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex );

        [DllImport(PresentationNativeDll, EntryPoint="GlobalDeleteAtomWrapper", ExactSpelling = true, SetLastError = true)]
        public static extern short GlobalDeleteAtom(short atom);

#if UIAUTOMATIONCLIENT  // UIAutomationClient

        [DllImport(PresentationNativeDll, EntryPoint="GetMenuBarInfoWrapper", SetLastError = true)]
        public static extern bool GetMenuBarInfo (IntPtr hwnd, int idObject, uint idItem, ref UnsafeNativeMethods.MENUBARINFO mbi);

        [DllImport(PresentationNativeDll, EntryPoint="GetWindowWrapper", ExactSpelling = true, SetLastError = true)]
        public static extern NativeMethods.HWND GetWindow(NativeMethods.HWND hWnd, int uCmd);

        [DllImport(PresentationNativeDll, EntryPoint="MapWindowPointsWrapper", SetLastError = true, ExactSpelling=true, CharSet=CharSet.Auto)]
        public static extern int MapWindowPoints(NativeMethods.HWND hWndFrom, NativeMethods.HWND hWndTo, [In, Out] ref NativeMethods.RECT rect, int cPoints);

        [DllImport(PresentationNativeDll, EntryPoint="MapWindowPointsWrapper", SetLastError = true, ExactSpelling=true, CharSet=CharSet.Auto)]
        public static extern int MapWindowPoints(NativeMethods.HWND hWndFrom, NativeMethods.HWND hWndTo, ref NativeMethods.POINT pt, int cPoints);

#elif UIAUTOMATIONCLIENTSIDEPROVIDERS   // UIAutomationClientSideProviders

        [DllImport(PresentationNativeDll, EntryPoint="GetAncestorWrapper", CharSet = CharSet.Auto)]
        public static extern IntPtr GetAncestor(IntPtr hwnd, int gaFlags);

        [DllImport(PresentationNativeDll, EntryPoint="FindWindowExWrapper", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern IntPtr FindWindowEx(IntPtr hwndParent, IntPtr hwndChildAfter, string className, string wndName);

        [DllImport(PresentationNativeDll, EntryPoint="GetMenuBarInfoWrapper", SetLastError = true)]
        public static extern bool GetMenuBarInfo (IntPtr hwnd, int idObject, uint idItem, ref NativeMethods.MENUBARINFO mbi);

        [DllImport(PresentationNativeDll, EntryPoint="GetTextExtentPoint32Wrapper", SetLastError = true)]
        public static extern int GetTextExtentPoint32(IntPtr hdc, [MarshalAs(UnmanagedType.LPWStr)]string lpString, int cbString, out NativeMethods.SIZE lpSize);

        [DllImport(PresentationNativeDll, EntryPoint="GetWindowWrapper", ExactSpelling = true, SetLastError = true)]
        public static extern IntPtr GetWindow(IntPtr hWnd, int uCmd);

        [DllImport(PresentationNativeDll, EntryPoint = "GetWindowTextWrapper", CharSet=CharSet.Auto, BestFitMapping = false, SetLastError = true)]
        public static extern int GetWindowText(IntPtr hWnd, [Out] StringBuilder lpString, int nMaxCount);

        [DllImport(PresentationNativeDll, EntryPoint="MapWindowPointsWrapper", ExactSpelling = true, SetLastError = true)]
        public static extern int MapWindowPoints(IntPtr hWndFrom, IntPtr hWndTo, [In, Out] ref NativeMethods.Win32Rect rect, int cPoints);

        [DllImport(PresentationNativeDll, EntryPoint="MapWindowPointsWrapper", ExactSpelling = true, SetLastError = true)]
        public static extern int MapWindowPoints(IntPtr hWndFrom, IntPtr hWndTo, [In, Out] ref NativeMethods.Win32Point pt, int cPoints);

        [DllImport(PresentationNativeDll, EntryPoint="SetScrollPosWrapper", SetLastError = true)]
        public static extern int SetScrollPos(IntPtr hWnd, int nBar, int nPos, bool bRedraw);

#endif
#else       // Base/Core/FW + DRT

        //
        // These used to bind to the *Wrapper exports in PresentationNative_cor3.dll. They are plain
        // user32 P/Invokes now, because the port must not depend on WPF's shipped native DLLs on any
        // platform -- Windows included.
        //
        // The wrappers existed for one reason: several of these APIs legitimately return 0/NULL on
        // success, so a caller cannot tell "returned zero" from "failed" without clearing the last
        // error FIRST. The native shim did SetLastError(0) and then called through.
        // Marshal.SetLastSystemError(0) is that same clear, so ClearLastError() before each call
        // preserves the semantics the callers were written against.
        //
        // GetWindowLongPtr/SetWindowLongPtr need the size split: on 64-bit they are real user32
        // exports, but on 32-bit Windows they are macros for the non-Ptr versions and no export of
        // that name exists, so binding to it would fail at first call.
        //

        private const string User32 = "user32.dll";

        private static void ClearLastError() => Marshal.SetLastSystemError(0);

        public static bool EnableWindow(HandleRef hWnd, bool enable)
        {
            ClearLastError();
            bool result = EnableWindowImpl(hWnd.Handle, enable);
            GC.KeepAlive(hWnd.Wrapper);
            return result;
        }

        public static IntPtr GetAncestor(IntPtr hwnd, int gaFlags)
        {
            ClearLastError();
            return GetAncestorImpl(hwnd, gaFlags);
        }

        public static int GetKeyboardLayoutList(int size, [Out] IntPtr[] hkls)
        {
            ClearLastError();
            return GetKeyboardLayoutListImpl(size, hkls);
        }

        public static IntPtr GetParent(HandleRef hWnd)
        {
            ClearLastError();
            IntPtr result = GetParentImpl(hWnd.Handle);
            GC.KeepAlive(hWnd.Wrapper);
            return result;
        }

        public static IntPtr GetWindow(IntPtr hWnd, int uCmd)
        {
            ClearLastError();
            return GetWindowImpl(hWnd, uCmd);
        }

        public static int GetWindowLong(HandleRef hWnd, int nIndex)
        {
            int result = GetWindowLong(hWnd.Handle, nIndex);
            GC.KeepAlive(hWnd.Wrapper);
            return result;
        }

        public static int GetWindowLong(IntPtr hWnd, int nIndex)
        {
            ClearLastError();
            return GetWindowLongImpl(hWnd, nIndex);
        }

        public static NativeMethods.WndProc GetWindowLongWndProc(HandleRef hWnd, int nIndex)
        {
            ClearLastError();
            NativeMethods.WndProc result = GetWindowLongWndProcImpl(hWnd.Handle, nIndex);
            GC.KeepAlive(hWnd.Wrapper);
            return result;
        }

        public static IntPtr GetWindowLongPtr(HandleRef hWnd, int nIndex)
        {
            IntPtr result = GetWindowLongPtr(hWnd.Handle, nIndex);
            GC.KeepAlive(hWnd.Wrapper);
            return result;
        }

        public static IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex)
        {
            ClearLastError();
            return IntPtr.Size == 8
                ? GetWindowLongPtrImpl(hWnd, nIndex)
                : (IntPtr)GetWindowLongImpl(hWnd, nIndex);
        }

        public static NativeMethods.WndProc GetWindowLongPtrWndProc(HandleRef hWnd, int nIndex)
        {
            ClearLastError();
            NativeMethods.WndProc result = IntPtr.Size == 8
                ? GetWindowLongPtrWndProcImpl(hWnd.Handle, nIndex)
                : GetWindowLongWndProcImpl(hWnd.Handle, nIndex);
            GC.KeepAlive(hWnd.Wrapper);
            return result;
        }

        public static int GetWindowText(HandleRef hWnd, [Out] StringBuilder lpString, int nMaxCount)
        {
            ClearLastError();
            int result = GetWindowTextImpl(hWnd.Handle, lpString, nMaxCount);
            GC.KeepAlive(hWnd.Wrapper);
            return result;
        }

        public static int GetWindowTextLength(HandleRef hWnd)
        {
            ClearLastError();
            int result = GetWindowTextLengthImpl(hWnd.Handle);
            GC.KeepAlive(hWnd.Wrapper);
            return result;
        }

        public static int MapWindowPoints(HandleRef hWndFrom, HandleRef hWndTo, [In, Out] ref NativeMethods.RECT rect, int cPoints)
        {
            ClearLastError();
            int result = MapWindowPointsImpl(hWndFrom.Handle, hWndTo.Handle, ref rect, cPoints);
            GC.KeepAlive(hWndFrom.Wrapper);
            GC.KeepAlive(hWndTo.Wrapper);
            return result;
        }

        public static IntPtr SetFocus(HandleRef hWnd)
        {
            ClearLastError();
            IntPtr result = SetFocusImpl(hWnd.Handle);
            GC.KeepAlive(hWnd.Wrapper);
            return result;
        }

        public static int SetWindowLong(HandleRef hWnd, int nIndex, int dwNewLong)
        {
            int result = SetWindowLong(hWnd.Handle, nIndex, dwNewLong);
            GC.KeepAlive(hWnd.Wrapper);
            return result;
        }

        public static int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong)
        {
            ClearLastError();
            return SetWindowLongImpl(hWnd, nIndex, dwNewLong);
        }

        public static int SetWindowLongWndProc(HandleRef hWnd, int nIndex, NativeMethods.WndProc dwNewLong)
        {
            ClearLastError();
            int result = SetWindowLongWndProcImpl(hWnd.Handle, nIndex, dwNewLong);
            GC.KeepAlive(hWnd.Wrapper);
            return result;
        }

        public static IntPtr SetWindowLongPtr(HandleRef hWnd, int nIndex, IntPtr dwNewLong)
        {
            IntPtr result = SetWindowLongPtr(hWnd.Handle, nIndex, dwNewLong);
            GC.KeepAlive(hWnd.Wrapper);
            return result;
        }

        public static IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong)
        {
            ClearLastError();
            return IntPtr.Size == 8
                ? SetWindowLongPtrImpl(hWnd, nIndex, dwNewLong)
                : (IntPtr)SetWindowLongImpl(hWnd, nIndex, (int)dwNewLong);
        }

        public static IntPtr SetWindowLongPtrWndProc(HandleRef hWnd, int nIndex, NativeMethods.WndProc dwNewLong)
        {
            ClearLastError();
            IntPtr result = IntPtr.Size == 8
                ? SetWindowLongPtrWndProcImpl(hWnd.Handle, nIndex, dwNewLong)
                : (IntPtr)SetWindowLongWndProcImpl(hWnd.Handle, nIndex, dwNewLong);
            GC.KeepAlive(hWnd.Wrapper);
            return result;
        }

        [DllImport(User32, EntryPoint = "EnableWindow", SetLastError = true, ExactSpelling = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool EnableWindowImpl(IntPtr hWnd, bool enable);

        [DllImport(User32, EntryPoint = "GetAncestor", SetLastError = true, ExactSpelling = true)]
        private static extern IntPtr GetAncestorImpl(IntPtr hwnd, int gaFlags);

        [DllImport(User32, EntryPoint = "GetKeyboardLayoutList", SetLastError = true, ExactSpelling = true)]
        private static extern int GetKeyboardLayoutListImpl(int size, [Out, MarshalAs(UnmanagedType.LPArray)] IntPtr[] hkls);

        [DllImport(User32, EntryPoint = "GetParent", SetLastError = true, ExactSpelling = true)]
        private static extern IntPtr GetParentImpl(IntPtr hWnd);

        [DllImport(User32, EntryPoint = "GetWindow", SetLastError = true, ExactSpelling = true)]
        private static extern IntPtr GetWindowImpl(IntPtr hWnd, int uCmd);

        [DllImport(User32, EntryPoint = "GetWindowLongW", SetLastError = true, ExactSpelling = true)]
        private static extern int GetWindowLongImpl(IntPtr hWnd, int nIndex);

        [DllImport(User32, EntryPoint = "GetWindowLongW", SetLastError = true, ExactSpelling = true)]
        private static extern NativeMethods.WndProc GetWindowLongWndProcImpl(IntPtr hWnd, int nIndex);

        [DllImport(User32, EntryPoint = "GetWindowLongPtrW", SetLastError = true, ExactSpelling = true)]
        private static extern IntPtr GetWindowLongPtrImpl(IntPtr hWnd, int nIndex);

        [DllImport(User32, EntryPoint = "GetWindowLongPtrW", SetLastError = true, ExactSpelling = true)]
        private static extern NativeMethods.WndProc GetWindowLongPtrWndProcImpl(IntPtr hWnd, int nIndex);

        [DllImport(User32, EntryPoint = "GetWindowTextW", SetLastError = true, ExactSpelling = true, CharSet = CharSet.Unicode)]
        private static extern int GetWindowTextImpl(IntPtr hWnd, [Out] StringBuilder lpString, int nMaxCount);

        [DllImport(User32, EntryPoint = "GetWindowTextLengthW", SetLastError = true, ExactSpelling = true, CharSet = CharSet.Unicode)]
        private static extern int GetWindowTextLengthImpl(IntPtr hWnd);

        [DllImport(User32, EntryPoint = "MapWindowPoints", SetLastError = true, ExactSpelling = true)]
        private static extern int MapWindowPointsImpl(IntPtr hWndFrom, IntPtr hWndTo, [In, Out] ref NativeMethods.RECT rect, int cPoints);

        [DllImport(User32, EntryPoint = "SetFocus", SetLastError = true, ExactSpelling = true)]
        private static extern IntPtr SetFocusImpl(IntPtr hWnd);

        [DllImport(User32, EntryPoint = "SetWindowLongW", SetLastError = true, ExactSpelling = true)]
        private static extern int SetWindowLongImpl(IntPtr hWnd, int nIndex, int dwNewLong);

        [DllImport(User32, EntryPoint = "SetWindowLongW", SetLastError = true, ExactSpelling = true)]
        private static extern int SetWindowLongWndProcImpl(IntPtr hWnd, int nIndex, NativeMethods.WndProc dwNewLong);

        [DllImport(User32, EntryPoint = "SetWindowLongPtrW", SetLastError = true, ExactSpelling = true)]
        private static extern IntPtr SetWindowLongPtrImpl(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

        [DllImport(User32, EntryPoint = "SetWindowLongPtrW", SetLastError = true, ExactSpelling = true)]
        private static extern IntPtr SetWindowLongPtrWndProcImpl(IntPtr hWnd, int nIndex, NativeMethods.WndProc dwNewLong);

#endif

        /// <summary>
        /// Once a global inside the native Line Services engine. The managed engine
        /// (ManagedLineServices) does not form these ligatures in the first place, so there is
        /// nothing to disable and this is a no-op rather than a load of PresentationNative.
        /// </summary>
        public static void LsDisableSpecialCharacterLigature(bool fDisable)
        {
        }
    }
}
