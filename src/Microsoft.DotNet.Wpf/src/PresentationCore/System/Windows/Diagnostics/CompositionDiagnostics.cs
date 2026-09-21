// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// Description:
//      The one thing the visual-tree inspector cannot reach through public API: the DUCE
//      resource handle a Visual was published under.
//
//      That handle is the join between the two trees this port has. WPF's managed Visual tree is
//      what an app wrote; the renderer's SceneVisual graph (MilcoreEngine) is what the compositor
//      actually received, decoded from the MILCMD stream. They are addressed by the same handle,
//      so with it the inspector can answer the question that otherwise takes an afternoon of
//      log-reading: is this element missing from the screen because WPF never sent it, or because
//      the compositor got it and drew it wrong?
//
//      Everything needed is internal and spread across three types (MediaContext's channel,
//      Visual's proxy, VisualProxy's per-channel handle map), so this is one call rather than
//      three grants -- and it means the inspector's dependency on PresentationCore's internals is
//      exactly this file's signature and nothing else.

using System.Windows.Media;
using System.Windows.Media.Composition;

namespace System.Windows.Diagnostics
{
    internal static class CompositionDiagnostics
    {
        /// <summary>
        ///     The DUCE resource handle this visual is published under on the media context's
        ///     channel, or 0 if it is not on a channel at all.
        /// </summary>
        /// <remarks>
        ///     Zero is an ordinary answer, not a failure: a visual that has never been rendered,
        ///     one whose window is closing, and one in an app running without managed composition
        ///     all legitimately have no handle. Callers report it as "the compositor does not have
        ///     this element", which is exactly what it means.
        /// </remarks>
        internal static uint GetCompositionHandle(Visual visual)
        {
            if (visual == null)
            {
                return 0;
            }

            try
            {
                MediaContext mediaContext = MediaContext.From(visual.Dispatcher);
                DUCE.Channel channel = mediaContext?.Channel;
                if (channel == null)
                {
                    return 0;
                }

                return (uint)visual._proxy.GetHandle(channel);
            }
            catch
            {
                // Diagnostics only. A handle that cannot be read is reported as absent rather
                // than taken as a reason to fail the command that asked for it.
                return 0;
            }
        }
    }
}
