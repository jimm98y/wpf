// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// What GDI+ records and draws for TEXT is a property of Windows' own font files and of the system's
// ANSI code page as much as of GDI+: the glyph indices and extents are the installed Arial's (macOS's
// 5.01 is another file), the LOGFONT's charset the code page's (the references were recorded on a
// Windows whose code page is 1250), a line's height Segoe UI's VDMX (which Selawik, its stand-in, does
// not carry). Those references hold only where those files are; elsewhere the cases are left out, as
// the batteries already leave out the ones that need Windows' colour profiles.
//

using System;

namespace Wpf.WinFormsInterop.Tests
{
    internal static class WindowsFontFiles
    {
        /// <summary>Whether this machine has the font files and code page GDI+'s references were
        /// recorded with: Windows'.</summary>
        public static bool Present => OperatingSystem.IsWindows();

        /// <summary>Whether case <paramref name="name"/> draws text ("text", "text_lines", ".../text/..."),
        /// whose expected bytes hold only with <see cref="Present"/>.</summary>
        public static bool DrawsText(string name)
        {
            foreach (string part in name.Split('/'))
                if (part == "text" || part.StartsWith("text_", StringComparison.Ordinal)) return true;
            return false;
        }

        /// <summary>Why a size GDI takes from Segoe UI's VDMX (a line's height: a combo box's field, a
        /// tab's height) cannot be held to stock's here, or null where Windows' Segoe UI is.</summary>
        public static string? NoSegoeVdmx => Present ? null : "a GDI line height of Segoe UI's own VDMX, which its stand-in Selawik does not carry";

        /// <summary>The reason to skip a text case here, or null.</summary>
        public static string? Missing(string name)
            => !Present && DrawsText(name) ? "GDI+ text is Windows' font files' and code page's" : null;
    }
}
