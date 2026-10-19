// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// What an application asks before it picks a hardware path, on the managed backend:
// - RenderOptions.ProcessRenderMode asked milcore and threw DllNotFoundException (wpfgfx_cor3.dll),
//   reading it and setting it.
// - RenderCapability.Tier read 0, "no acceleration": the caps come from milcore's render thread,
//   which is gone. Stock reports tier 2 on a WDDM GPU, and so does the GPU compositor.
//

using System.Windows.Interop;
using System.Windows.Media;
using Xunit;

namespace Wpf.Window.Tests
{
    public sealed class RenderCapabilityTests
    {
        [Fact]
        public void TheProcessRenderMode_ReadsAndKeepsWhatWasSet()
        {
            RenderMode was = RenderOptions.ProcessRenderMode;
            Assert.Equal(RenderMode.Default, was);
            try
            {
                RenderOptions.ProcessRenderMode = RenderMode.SoftwareOnly;
                Assert.Equal(RenderMode.SoftwareOnly, RenderOptions.ProcessRenderMode);
            }
            finally { RenderOptions.ProcessRenderMode = was; }
            Assert.Equal(RenderMode.Default, RenderOptions.ProcessRenderMode);
        }

        [Fact]
        public void TheRenderTier_IsHardware_WithPixelShaders()
        {
            var (tier, ps3, ps2) = UiThread.Invoke(() => (RenderCapability.Tier >> 16,
                RenderCapability.IsPixelShaderVersionSupported(3, 0), RenderCapability.IsPixelShaderVersionSupported(2, 0)));
            Assert.Equal(2, tier);
            Assert.True(ps3, "pixel shader 3.0");
            Assert.True(ps2, "pixel shader 2.0");
        }
    }
}
