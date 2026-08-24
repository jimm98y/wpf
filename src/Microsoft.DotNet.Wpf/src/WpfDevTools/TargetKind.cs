// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Microsoft.Wpf.DevTools
{
    /// <summary>
    /// Which document a session is inspecting. The endpoint advertises one target per kind,
    /// so a frontend attaches to the tree it wants and gets an ordinary Elements panel over it.
    /// </summary>
    internal enum TargetKind
    {
        /// <summary>The WPF Visual tree (and WinForms controls) -- what the app built.</summary>
        VisualTree,

        /// <summary>The renderer's decoded MILCMD scene graph -- what the compositor received.</summary>
        Composition,
    }
}
