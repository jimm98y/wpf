// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// The rest of oleacc's type library, as tlbimp imports it into the framework's Accessibility assembly:
// Dynamic Annotation (IAccPropServices and its coclass CAccPropServices, IAccPropServer, AnnoScope), the
// identity interface (IAccIdentity), the handler marker (IAccessibleHandler) and the remotable handle
// structures their HWND and HMENU parameters are declared with.
//
// As with IAccessible (IAccessible.cs), these are COM declarations of oleacc's own objects: the coclass is
// created with CoCreateInstance and every call goes through the object's vtable, so DECLARATION ORDER IS THE
// ABI -- the members are in the type library's order.
//

using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

// the type library's own names and unsigned parameters, as tlbimp keeps them
#pragma warning disable CS3001, CS3008

namespace Accessibility
{
    /// <summary>Whether an annotation applies to the element only or to its children as well.</summary>
    public enum AnnoScope
    {
        ANNO_THIS = 0,
        ANNO_CONTAINER = 1,
    }

    /// <summary>The union inside a remotable handle: an in-process handle or a remote one.</summary>
    [StructLayout (LayoutKind.Explicit, Size = 4, Pack = 4)]
    public struct __MIDL_IWinTypes_0009
    {
        [FieldOffset (0)]
        public int hInproc;

        [FieldOffset (0)]
        public int hRemote;
    }

    /// <summary>wireHWND / wireHMENU: a handle as MIDL marshals it (the context, then the handle).</summary>
    [StructLayout (LayoutKind.Sequential, Pack = 4)]
    public struct _RemotableHandle
    {
        public int fContext;
        public __MIDL_IWinTypes_0009 u;
    }

    /// <summary>A callback that supplies annotated property values on demand.</summary>
    [ComImport]
    [Guid ("76C0DBBB-15E0-4E7B-B61B-20EEEA2001E0")]
    [InterfaceType (ComInterfaceType.InterfaceIsIUnknown)]
    public interface IAccPropServer
    {
        [MethodImpl (MethodImplOptions.InternalCall, MethodCodeType = MethodCodeType.Runtime)]
        void GetPropValue ([In] ref byte pIDString, [In] uint dwIDStringLen, [In] Guid idProp, [MarshalAs (UnmanagedType.Struct)] out object pvarValue, out int pfHasProp);
    }

    /// <summary>Dynamic Annotation: properties set on, or served for, an accessible element.</summary>
    [ComImport]
    [Guid ("6E26E776-04F0-495D-80E4-3330352E3169")]
    [InterfaceType (ComInterfaceType.InterfaceIsIUnknown)]
    [ComConversionLoss]
    public interface IAccPropServices
    {
        [MethodImpl (MethodImplOptions.InternalCall, MethodCodeType = MethodCodeType.Runtime)]
        void SetPropValue ([In] ref byte pIDString, [In] uint dwIDStringLen, [In] Guid idProp, [In, MarshalAs (UnmanagedType.Struct)] object var);

        [MethodImpl (MethodImplOptions.InternalCall, MethodCodeType = MethodCodeType.Runtime)]
        void SetPropServer ([In] ref byte pIDString, [In] uint dwIDStringLen, [In] ref Guid paProps, [In] int cProps, [In, MarshalAs (UnmanagedType.Interface)] IAccPropServer pServer, [In] AnnoScope AnnoScope);

        [MethodImpl (MethodImplOptions.InternalCall, MethodCodeType = MethodCodeType.Runtime)]
        void ClearProps ([In] ref byte pIDString, [In] uint dwIDStringLen, [In] ref Guid paProps, [In] int cProps);

        [MethodImpl (MethodImplOptions.InternalCall, MethodCodeType = MethodCodeType.Runtime)]
        void SetHwndProp ([In, ComAliasName ("Accessibility.wireHWND")] ref _RemotableHandle hwnd, [In] uint idObject, [In] uint idChild, [In] Guid idProp, [In, MarshalAs (UnmanagedType.Struct)] object var);

        [MethodImpl (MethodImplOptions.InternalCall, MethodCodeType = MethodCodeType.Runtime)]
        void SetHwndPropStr ([In, ComAliasName ("Accessibility.wireHWND")] ref _RemotableHandle hwnd, [In] uint idObject, [In] uint idChild, [In] Guid idProp, [In, MarshalAs (UnmanagedType.LPWStr)] string str);

        [MethodImpl (MethodImplOptions.InternalCall, MethodCodeType = MethodCodeType.Runtime)]
        void SetHwndPropServer ([In, ComAliasName ("Accessibility.wireHWND")] ref _RemotableHandle hwnd, [In] uint idObject, [In] uint idChild, [In] ref Guid paProps, [In] int cProps, [In, MarshalAs (UnmanagedType.Interface)] IAccPropServer pServer, [In] AnnoScope AnnoScope);

        [MethodImpl (MethodImplOptions.InternalCall, MethodCodeType = MethodCodeType.Runtime)]
        void ClearHwndProps ([In, ComAliasName ("Accessibility.wireHWND")] ref _RemotableHandle hwnd, [In] uint idObject, [In] uint idChild, [In] ref Guid paProps, [In] int cProps);

        [MethodImpl (MethodImplOptions.InternalCall, MethodCodeType = MethodCodeType.Runtime)]
        void ComposeHwndIdentityString ([In, ComAliasName ("Accessibility.wireHWND")] ref _RemotableHandle hwnd, [In] uint idObject, [In] uint idChild, [Out] IntPtr ppIDString, out uint pdwIDStringLen);

        [MethodImpl (MethodImplOptions.InternalCall, MethodCodeType = MethodCodeType.Runtime)]
        void DecomposeHwndIdentityString ([In] ref byte pIDString, [In] uint dwIDStringLen, [Out, ComAliasName ("Accessibility.wireHWND")] IntPtr phwnd, out uint pidObject, out uint pidChild);

        [MethodImpl (MethodImplOptions.InternalCall, MethodCodeType = MethodCodeType.Runtime)]
        void SetHmenuProp ([In, ComAliasName ("Accessibility.wireHMENU")] ref _RemotableHandle hmenu, [In] uint idChild, [In] Guid idProp, [In, MarshalAs (UnmanagedType.Struct)] object var);

        [MethodImpl (MethodImplOptions.InternalCall, MethodCodeType = MethodCodeType.Runtime)]
        void SetHmenuPropStr ([In, ComAliasName ("Accessibility.wireHMENU")] ref _RemotableHandle hmenu, [In] uint idChild, [In] Guid idProp, [In, MarshalAs (UnmanagedType.LPWStr)] string str);

        [MethodImpl (MethodImplOptions.InternalCall, MethodCodeType = MethodCodeType.Runtime)]
        void SetHmenuPropServer ([In, ComAliasName ("Accessibility.wireHMENU")] ref _RemotableHandle hmenu, [In] uint idChild, [In] ref Guid paProps, [In] int cProps, [In, MarshalAs (UnmanagedType.Interface)] IAccPropServer pServer, [In] AnnoScope AnnoScope);

        [MethodImpl (MethodImplOptions.InternalCall, MethodCodeType = MethodCodeType.Runtime)]
        void ClearHmenuProps ([In, ComAliasName ("Accessibility.wireHMENU")] ref _RemotableHandle hmenu, [In] uint idChild, [In] ref Guid paProps, [In] int cProps);

        [MethodImpl (MethodImplOptions.InternalCall, MethodCodeType = MethodCodeType.Runtime)]
        void ComposeHmenuIdentityString ([In, ComAliasName ("Accessibility.wireHMENU")] ref _RemotableHandle hmenu, [In] uint idChild, [Out] IntPtr ppIDString, out uint pdwIDStringLen);

        [MethodImpl (MethodImplOptions.InternalCall, MethodCodeType = MethodCodeType.Runtime)]
        void DecomposeHmenuIdentityString ([In] ref byte pIDString, [In] uint dwIDStringLen, [Out, ComAliasName ("Accessibility.wireHMENU")] IntPtr phmenu, out uint pidChild);
    }

    /// <summary>The coclass interface tlbimp makes for CAccPropServices: <c>new CAccPropServices()</c> creates
    /// oleacc's object.</summary>
    [ComImport]
    [Guid ("6E26E776-04F0-495D-80E4-3330352E3169")]
    [CoClass (typeof (CAccPropServicesClass))]
    public interface CAccPropServices : IAccPropServices
    {
    }

    /// <summary>oleacc's Dynamic Annotation object.</summary>
    [ComImport]
    [Guid ("B5F8350B-0548-48B1-A6EE-88BD00B4A5E7")]
    [ClassInterface (ClassInterfaceType.None)]
    [ComConversionLoss]
    [TypeLibType (TypeLibTypeFlags.FCanCreate)]
    public class CAccPropServicesClass : CAccPropServices, IAccPropServices
    {
        // its constructor is the runtime's (CoCreateInstance of the class), as a ComImport class's always is

        [MethodImpl (MethodImplOptions.InternalCall, MethodCodeType = MethodCodeType.Runtime)]
        public extern virtual void SetPropValue ([In] ref byte pIDString, [In] uint dwIDStringLen, [In] Guid idProp, [In, MarshalAs (UnmanagedType.Struct)] object var);

        [MethodImpl (MethodImplOptions.InternalCall, MethodCodeType = MethodCodeType.Runtime)]
        public extern virtual void SetPropServer ([In] ref byte pIDString, [In] uint dwIDStringLen, [In] ref Guid paProps, [In] int cProps, [In, MarshalAs (UnmanagedType.Interface)] IAccPropServer pServer, [In] AnnoScope AnnoScope);

        [MethodImpl (MethodImplOptions.InternalCall, MethodCodeType = MethodCodeType.Runtime)]
        public extern virtual void ClearProps ([In] ref byte pIDString, [In] uint dwIDStringLen, [In] ref Guid paProps, [In] int cProps);

        [MethodImpl (MethodImplOptions.InternalCall, MethodCodeType = MethodCodeType.Runtime)]
        public extern virtual void SetHwndProp ([In, ComAliasName ("Accessibility.wireHWND")] ref _RemotableHandle hwnd, [In] uint idObject, [In] uint idChild, [In] Guid idProp, [In, MarshalAs (UnmanagedType.Struct)] object var);

        [MethodImpl (MethodImplOptions.InternalCall, MethodCodeType = MethodCodeType.Runtime)]
        public extern virtual void SetHwndPropStr ([In, ComAliasName ("Accessibility.wireHWND")] ref _RemotableHandle hwnd, [In] uint idObject, [In] uint idChild, [In] Guid idProp, [In, MarshalAs (UnmanagedType.LPWStr)] string str);

        [MethodImpl (MethodImplOptions.InternalCall, MethodCodeType = MethodCodeType.Runtime)]
        public extern virtual void SetHwndPropServer ([In, ComAliasName ("Accessibility.wireHWND")] ref _RemotableHandle hwnd, [In] uint idObject, [In] uint idChild, [In] ref Guid paProps, [In] int cProps, [In, MarshalAs (UnmanagedType.Interface)] IAccPropServer pServer, [In] AnnoScope AnnoScope);

        [MethodImpl (MethodImplOptions.InternalCall, MethodCodeType = MethodCodeType.Runtime)]
        public extern virtual void ClearHwndProps ([In, ComAliasName ("Accessibility.wireHWND")] ref _RemotableHandle hwnd, [In] uint idObject, [In] uint idChild, [In] ref Guid paProps, [In] int cProps);

        [MethodImpl (MethodImplOptions.InternalCall, MethodCodeType = MethodCodeType.Runtime)]
        public extern virtual void ComposeHwndIdentityString ([In, ComAliasName ("Accessibility.wireHWND")] ref _RemotableHandle hwnd, [In] uint idObject, [In] uint idChild, [Out] IntPtr ppIDString, out uint pdwIDStringLen);

        [MethodImpl (MethodImplOptions.InternalCall, MethodCodeType = MethodCodeType.Runtime)]
        public extern virtual void DecomposeHwndIdentityString ([In] ref byte pIDString, [In] uint dwIDStringLen, [Out, ComAliasName ("Accessibility.wireHWND")] IntPtr phwnd, out uint pidObject, out uint pidChild);

        [MethodImpl (MethodImplOptions.InternalCall, MethodCodeType = MethodCodeType.Runtime)]
        public extern virtual void SetHmenuProp ([In, ComAliasName ("Accessibility.wireHMENU")] ref _RemotableHandle hmenu, [In] uint idChild, [In] Guid idProp, [In, MarshalAs (UnmanagedType.Struct)] object var);

        [MethodImpl (MethodImplOptions.InternalCall, MethodCodeType = MethodCodeType.Runtime)]
        public extern virtual void SetHmenuPropStr ([In, ComAliasName ("Accessibility.wireHMENU")] ref _RemotableHandle hmenu, [In] uint idChild, [In] Guid idProp, [In, MarshalAs (UnmanagedType.LPWStr)] string str);

        [MethodImpl (MethodImplOptions.InternalCall, MethodCodeType = MethodCodeType.Runtime)]
        public extern virtual void SetHmenuPropServer ([In, ComAliasName ("Accessibility.wireHMENU")] ref _RemotableHandle hmenu, [In] uint idChild, [In] ref Guid paProps, [In] int cProps, [In, MarshalAs (UnmanagedType.Interface)] IAccPropServer pServer, [In] AnnoScope AnnoScope);

        [MethodImpl (MethodImplOptions.InternalCall, MethodCodeType = MethodCodeType.Runtime)]
        public extern virtual void ClearHmenuProps ([In, ComAliasName ("Accessibility.wireHMENU")] ref _RemotableHandle hmenu, [In] uint idChild, [In] ref Guid paProps, [In] int cProps);

        [MethodImpl (MethodImplOptions.InternalCall, MethodCodeType = MethodCodeType.Runtime)]
        public extern virtual void ComposeHmenuIdentityString ([In, ComAliasName ("Accessibility.wireHMENU")] ref _RemotableHandle hmenu, [In] uint idChild, [Out] IntPtr ppIDString, out uint pdwIDStringLen);

        [MethodImpl (MethodImplOptions.InternalCall, MethodCodeType = MethodCodeType.Runtime)]
        public extern virtual void DecomposeHmenuIdentityString ([In] ref byte pIDString, [In] uint dwIDStringLen, [Out, ComAliasName ("Accessibility.wireHMENU")] IntPtr phmenu, out uint pidChild);
    }

    /// <summary>An accessible object's identity string, for annotation.</summary>
    [ComImport]
    [Guid ("7852B78D-1CFD-41C1-A615-9C0C85960B5F")]
    [InterfaceType (ComInterfaceType.InterfaceIsIUnknown)]
    [ComConversionLoss]
    public interface IAccIdentity
    {
        [MethodImpl (MethodImplOptions.InternalCall, MethodCodeType = MethodCodeType.Runtime)]
        void GetIdentityString ([In] uint dwIDChild, [Out] IntPtr ppIDString, out uint pdwIDStringLen);
    }

    /// <summary>Marks an object that hands out IAccessible for a window (AccessibleObjectFromID).</summary>
    [ComImport]
    [Guid ("03022430-ABC4-11D0-BDE2-00AA001A1953")]
    [InterfaceType (ComInterfaceType.InterfaceIsIUnknown)]
    [TypeLibType (TypeLibTypeFlags.FHidden | TypeLibTypeFlags.FOleAutomation)]
    public interface IAccessibleHandler
    {
        [MethodImpl (MethodImplOptions.InternalCall, MethodCodeType = MethodCodeType.Runtime)]
        void AccessibleObjectFromID ([In] int hwnd, [In] int lObjectID, [MarshalAs (UnmanagedType.Interface)] out IAccessible pIAccessible);
    }
}
