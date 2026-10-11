// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// IAccessible: the Microsoft Active Accessibility (MSAA) interface, as a managed declaration.
//
// This is a from-source replacement for the Accessibility assembly that ships in the WindowsDesktop
// framework, which is a tlbimp of oleacc's type library. The port must not take framework
// assemblies from WindowsDesktop, and this is the only type anything here needs from that one.
//
// WHY THIS IS ComImport AND NOT THE FLAT VTABLE PATTERN USED ELSEWHERE
//
// The port's rule is to avoid COM interop and call vtables directly through
// delegate* unmanaged[Stdcall], as WindowsMediaBackend does. That works when WE own both ends. Here
// we do not: these objects come from OTHER processes' accessibility implementations -- comctl32,
// Trident, Media Player, WinForms -- reached through oleacc, whose whole contract is COM. The two
// consumers (UIAutomationClient and UIAutomationClientSideProviders) rely on the marshaller for
// VARIANT and BSTR conversion, for cross-apartment calls, and for turning failure HRESULTs into the
// exceptions their error handling is built around. Reproducing that by hand would be a rewrite of
// roughly sixty call sites whose only real test is driving a legacy Win32 control.
//
// The important property is preserved either way: this assembly is referenced ONLY by the two
// client-side automation assemblies, which are Windows-only by nature (they exist to talk to
// oleacc), and it is not in the closure of PresentationFramework, PresentationCore or WindowsBase.
// No cross-platform head can reach it. What changes is where the assembly comes from: this repo,
// rather than the WindowsDesktop framework.
//
// DECLARATION ORDER IS THE ABI. The members below are in the exact order of the oleacc type
// library, including the two property setters at the end, because a dual interface is called
// through its vtable and the runtime lays the slots out in declaration order. Reordering anything
// here, or inserting a member, silently calls the wrong function on every accessible object in the
// system.
//

using System.Runtime.InteropServices;

namespace Accessibility
{
    /// <summary>
    /// The MSAA interface implemented by accessible objects. Obtained from oleacc
    /// (AccessibleObjectFromWindow, ObjectFromLresult, AccessibleObjectFromEvent), never constructed.
    /// </summary>
    /// <remarks>
    /// The <c>varChild</c> parameter of most members selects either the object itself
    /// (CHILDID_SELF, 0) or one of its simple children, which have no interface of their own.
    /// </remarks>
    [ComImport]
    [Guid("618736E0-3C3D-11CF-810C-00AA00389B71")]
    [InterfaceType(ComInterfaceType.InterfaceIsDual)]
    [TypeLibType(TypeLibTypeFlags.FDual | TypeLibTypeFlags.FOleAutomation | TypeLibTypeFlags.FDispatchable | TypeLibTypeFlags.FHidden)]
    public interface IAccessible
    {
        /// <summary>The parent accessible object, as an IDispatch.</summary>
        [DispId(-5000)]
        object accParent
        {
            [DispId(-5000)]
            [return: MarshalAs(UnmanagedType.IDispatch)]
            get;
        }

        /// <summary>How many children the object has, counting both full objects and simple children.</summary>
        [DispId(-5001)]
        int accChildCount
        {
            [DispId(-5001)]
            get;
        }

        /// <summary>The child's own accessible object, or null when the child is a simple one.</summary>
        [DispId(-5002)]
        [return: MarshalAs(UnmanagedType.IDispatch)]
        object get_accChild([In, MarshalAs(UnmanagedType.Struct)] object varChild);

        [DispId(-5003)]
        [return: MarshalAs(UnmanagedType.BStr)]
        string get_accName([In, Optional, MarshalAs(UnmanagedType.Struct)] object varChild);

        [DispId(-5004)]
        [return: MarshalAs(UnmanagedType.BStr)]
        string get_accValue([In, Optional, MarshalAs(UnmanagedType.Struct)] object varChild);

        [DispId(-5005)]
        [return: MarshalAs(UnmanagedType.BStr)]
        string get_accDescription([In, Optional, MarshalAs(UnmanagedType.Struct)] object varChild);

        /// <summary>The object's role, as one of the ROLE_SYSTEM_* values boxed in a VARIANT.</summary>
        [DispId(-5006)]
        [return: MarshalAs(UnmanagedType.Struct)]
        object get_accRole([In, Optional, MarshalAs(UnmanagedType.Struct)] object varChild);

        /// <summary>The object's state, as a combination of STATE_SYSTEM_* flags boxed in a VARIANT.</summary>
        [DispId(-5007)]
        [return: MarshalAs(UnmanagedType.Struct)]
        object get_accState([In, Optional, MarshalAs(UnmanagedType.Struct)] object varChild);

        [DispId(-5008)]
        [return: MarshalAs(UnmanagedType.BStr)]
        string get_accHelp([In, Optional, MarshalAs(UnmanagedType.Struct)] object varChild);

        [DispId(-5009)]
        int get_accHelpTopic([MarshalAs(UnmanagedType.BStr)] out string pszHelpFile,
                             [In, Optional, MarshalAs(UnmanagedType.Struct)] object varChild);

        [DispId(-5010)]
        [return: MarshalAs(UnmanagedType.BStr)]
        string get_accKeyboardShortcut([In, Optional, MarshalAs(UnmanagedType.Struct)] object varChild);

        /// <summary>The focused child: an IDispatch, a child id, or null.</summary>
        [DispId(-5011)]
        object accFocus
        {
            [DispId(-5011)]
            [return: MarshalAs(UnmanagedType.Struct)]
            get;
        }

        /// <summary>The selected child or children: an IDispatch, a child id, an IEnumVARIANT, or null.</summary>
        [DispId(-5012)]
        object accSelection
        {
            [DispId(-5012)]
            [return: MarshalAs(UnmanagedType.Struct)]
            get;
        }

        [DispId(-5013)]
        [return: MarshalAs(UnmanagedType.BStr)]
        string get_accDefaultAction([In, Optional, MarshalAs(UnmanagedType.Struct)] object varChild);

        [DispId(-5014)]
        void accSelect([In] int flagsSelect, [In, Optional, MarshalAs(UnmanagedType.Struct)] object varChild);

        /// <summary>The object's screen rectangle, as left/top/width/height.</summary>
        [DispId(-5015)]
        void accLocation(out int pxLeft, out int pyTop, out int pcxWidth, out int pcyHeight,
                         [In, Optional, MarshalAs(UnmanagedType.Struct)] object varChild);

        [DispId(-5016)]
        [return: MarshalAs(UnmanagedType.Struct)]
        object accNavigate([In] int navDir, [In, Optional, MarshalAs(UnmanagedType.Struct)] object varStart);

        /// <summary>The child at a screen point: an IDispatch, a child id, or null.</summary>
        [DispId(-5017)]
        [return: MarshalAs(UnmanagedType.Struct)]
        object accHitTest([In] int xLeft, [In] int yTop);

        [DispId(-5018)]
        void accDoDefaultAction([In, Optional, MarshalAs(UnmanagedType.Struct)] object varChild);

        // The two setters close the vtable. They share DispIds with their getters above, which is
        // correct for a dual interface: the property is one DispId with two accessors, and the
        // vtable slot order is what actually matters here.

        [DispId(-5003)]
        void set_accName([In, Optional, MarshalAs(UnmanagedType.Struct)] object varChild,
                         [In, MarshalAs(UnmanagedType.BStr)] string pszName);

        [DispId(-5004)]
        void set_accValue([In, Optional, MarshalAs(UnmanagedType.Struct)] object varChild,
                          [In, MarshalAs(UnmanagedType.BStr)] string pszValue);
    }
}
