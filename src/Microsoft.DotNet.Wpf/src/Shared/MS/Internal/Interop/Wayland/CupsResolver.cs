// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// Finding libcups.
//
// CupsPrint's imports name the library "libcups", and on a stock Linux machine that does not
// resolve. The default probe tries "libcups", "libcups.so" and the lib-prefixed forms of both; what
// is actually installed is "libcups.so.2", because a shared library ships under its SONAME and only
// the -dev package adds the unversioned symlink. So printing worked on a developer's machine with
// libcups2-dev on it and nowhere else -- and it failed as a DllNotFoundException, which CupsPrint
// catches and reports as "no printers", which is indistinguishable from a machine with none. A
// resolver that names the SONAME is the fix.
//
// libcups.so.3 is deliberately NOT a candidate. CUPS 3 is a different ABI -- cups_dest_t changed
// shape and the cupsGetDests taken here was removed -- so loading it would bind the struct layout in
// CupsPrint to a library that does not match it. Refusing to load is the honest outcome: "no
// printers" beats reading a printer list through the wrong layout. A CUPS 3 backend is its own
// piece of work.
//

using System;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace MS.Internal.Interop.Wayland
{
    [SupportedOSPlatform("linux")]
    internal static class CupsResolver
    {
        private static bool s_done;

        /// <summary>
        /// Registers the resolver, once. Called from CupsPrint's static constructor rather than a
        /// [ModuleInitializer] so that a machine that never prints never pays for it -- and so that
        /// the cost lands on the first CUPS call rather than on loading WindowsBase.
        ///
        /// NOTE for anyone adding a second native dependency to this assembly:
        /// SetDllImportResolver throws if it is called twice for the same assembly, so there is one
        /// resolver for WindowsBase and it lives here. Add candidates to it; do not register another.
        /// Names this does not recognise fall through to the default probe untouched.
        /// </summary>
        internal static void Init()
        {
            if (s_done) return;
            s_done = true;

            NativeLibrary.SetDllImportResolver(typeof(CupsResolver).Assembly, (name, assembly, searchPath) =>
            {
                if (!string.Equals(name, "libcups", StringComparison.Ordinal)) return IntPtr.Zero;

                foreach (string candidate in Candidates)
                {
                    if (NativeLibrary.TryLoad(candidate, out IntPtr handle)) return handle;
                }

                // Nothing found. Returning zero lets the default probe have its turn and, when that
                // fails too, produces the DllNotFoundException CupsPrint already handles.
                return IntPtr.Zero;
            });
        }

        private static readonly string[] Candidates =
        {
            "libcups.so.2",   // the SONAME: what libcups2 actually installs
            "libcups.so",     // the -dev symlink, for a machine that has one
        };
    }
}
