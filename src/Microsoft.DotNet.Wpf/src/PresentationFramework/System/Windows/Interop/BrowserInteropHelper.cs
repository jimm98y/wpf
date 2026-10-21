// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// Description: Implements Avalon BrowserInteropHelper class, which helps
//              interop with the browser. Deprecated as XBAP is not supported.
//

namespace System.Windows.Interop
{
    /// <summary>
    /// Implements Avalon BrowserInteropHelper, which helps interop with the browser
    /// </summary>
    public static class BrowserInteropHelper
    {
        /// <summary>
        /// Returns the IOleClientSite interface
        /// </summary>
        /// <remarks>An OLE interface of the browser that hosted an XBAP: no host has one now.</remarks>
        public static object ClientSite => null;

        /// <summary>
        /// Gets a script object that provides access to the HTML window object,
        /// custom script functions, and global variables for the HTML page, if the XAML browser application (XBAP)
        /// is hosted in a frame.
        /// </summary>
        /// <remarks>
        /// On the browser head, where the application runs in a page, the page's window object
        /// (MS.Internal.Interop.BrowserHostScript): the application's script calls reach the page as an
        /// XBAP's did. Everywhere else <code>null</code>, as for any application that is not browser hosted.
        /// </remarks>
        public static dynamic HostScript => OperatingSystem.IsBrowser() ? MS.Internal.Interop.BrowserHostScript.Window : null;

        /// <summary>
        /// Returns true if the app is a browser hosted app.
        /// </summary>
        /// <remarks>True on the browser head: the application runs in a page, which is what an app asks
        /// this to know (an XBAP's code takes its browser paths).</remarks>
        public static bool IsBrowserHosted => OperatingSystem.IsBrowser();

        /// <summary>
        /// Returns the Uri used to launch the application.
        /// </summary>
        /// <remarks>On the browser head, the address of the page.</remarks>
        public static Uri Source => OperatingSystem.IsBrowser() ? MS.Internal.Interop.BrowserHostScript.PageUri : null;

    }
}
