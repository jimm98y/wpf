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
    /// WindowsFormsHost's per-thread tick pumps WinForms once a frame and publishes one set into
    /// EmbeddedContent; anything else of this assembly's that wants to be composited - an
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
