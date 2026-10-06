// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Generic;
using Microsoft.Wpf.Interop.WebGpu.Composition;

namespace System.Windows.Forms.Integration
{
    /// <summary>
    /// Something whose WinForms window scenes are published into the WPF scene each frame.
    /// </summary>
    /// <remarks>
    /// EmbeddedContent.Set replaces the entire hosted set, so exactly one component may publish.
    /// WindowsFormsHost owns that tick; anything else that wants to be composited - an
    /// HwndHost-derived host claimed through HwndHostForeignContent, for instance - contributes
    /// through this interface instead of running a tick of its own.
    /// </remarks>
    internal interface IEmbeddedContentSource
    {
        /// <summary>
        /// Add this source's scenes, placed in device pixels. Returns true when the source moved
        /// since the previous frame, which forces a present even if no pixels changed.
        /// </summary>
        bool Collect(List<EmbeddedItem> into);

        /// <summary>Ask the owning element to repaint.</summary>
        void Invalidate();
    }
}
