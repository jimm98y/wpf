// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// What GDI+ records and draws for TEXT is a property of Windows' own font files and of the system's
// ANSI code page as much as of GDI+: the glyph indices and extents are the installed Arial's (macOS's
// 5.01 is another file), the LOGFONT's charset the code page's (the references were recorded on a
// Windows whose code page is 1250). Those references hold only where those files are; elsewhere the cases are left out, as
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

        /// <summary>Why a width GDI takes from Segoe UI's hinted advances ('hdmx': a tab's width) cannot be
        /// held to stock's here, or null where Windows' Segoe UI is. Its stand-in Selawik is unhinted and
        /// carries none; its line heights it does answer (StandInVdmx).</summary>
        public static string? NoSegoeHdmx => Present ? null : "a GDI width of Segoe UI's hinted advances ('hdmx'), which its stand-in Selawik does not carry";

        /// <summary>The reason to skip a text case here, or null.</summary>
        public static string? Missing(string name)
            => !Present && DrawsText(name) ? "GDI+ text is Windows' font files' and code page's" : null;
    }
}
