// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// Ported from WgpuInterop.ManagedFontTest.
//
// The COM-free font-resolution path that replaced DWriteFontResolver. A glyph run carries a 'WFNT'
// trailer -- font file path, face index, style simulations, exactly what WPF's managed GlyphTypeface
// (FontUri/FaceIndex/StyleSimulations) supplies -- and the engine resolves the face itself through
// ManagedFontResolver with no DirectWrite anywhere.
//
// Only the MANAGED resolver is installed on the engine. Setting the legacy pointer resolver as well
// would let a test pass through the old path and prove nothing about the new one, which is the whole
// subject here.
//

using Microsoft.Wpf.Interop.WebGpu.Composition;
using Microsoft.Wpf.Interop.WebGpu.Composition.Protocol;
using Microsoft.Wpf.Interop.WebGpu.Composition.Text;
using WgpuInterop.Tests.Harness;
using Xunit;

namespace WgpuInterop.Tests.Protocol
{
    public sealed class ManagedFontResolutionTests : RendererTestBase
    {
        public ManagedFontResolutionTests(GpuFixture gpu) : base(gpu) { }

        private const int W = 160, H = 48;
        private const uint HRoot = 2, HBlack = 3, HRun = 20, HContent = 6;
        private const float EmSize = 32f;
        private const float OriginX = 6f, OriginY = 34f;
        private const string Text = "HELLO";

        /// <summary>
        /// Render one run whose font is described ONLY by the managed descriptor, and return the ink
        /// count in the text band. Also asserts the left margin is clear, so a run that failed to
        /// resolve and painted nothing cannot be mistaken for one that resolved and rendered.
        /// </summary>
        private int RenderManaged(int simulations)
        {
            string fontPath = TestFonts.Require();

            // The local reader is ONLY for shaping (glyph ids + advances). The engine resolves the
            // face itself from the trailer -- that is the thing under test.
            var shaper = new TrueTypeFont(System.IO.File.ReadAllBytes(fontPath));
            float advScale = EmSize / shaper.PixelsPerEm;
            var indices = new ushort[Text.Length];
            var advances = new float[Text.Length];
            for (int i = 0; i < Text.Length; i++)
            {
                int gid = shaper.GlyphIndex(Text[i]);
                indices[i] = (ushort)gid;
                advances[i] = shaper.Advance(gid) * advScale;
            }

            var resolver = new ManagedFontResolver();
            var engine = new MilcoreEngine { ManagedFontResolver = resolver.Resolve };
            engine.CreateOrAddRef(HRoot, MilResourceTypeId.Visual);
            engine.SubmitCommand(MilCmd.SolidColorBrush(HBlack, 0, 0, 0, 1));

            engine.CreateOrAddRef(HRun, MilResourceTypeId.Null);
            engine.BeginCommand(MilCmd.GlyphRunWithManagedFont(HRun, OriginX, OriginY, EmSize,
                indices, advances, fontPath, faceIndex: 0, simulations: simulations));
            engine.EndCommand();

            byte[] img = RenderContent(engine, MilCmd.DrawGlyphRunRecord(HBlack, HRun), W, H,
                                       hVisual: HRoot, hContent: HContent);

            float total = 0;
            foreach (float a in advances) total += a;
            int textEndX = (int)(OriginX + total);

            Assert.True(CountInk(img, 0, 0, (int)OriginX - 1, H) == 0,
                "ink appeared before the run origin, so the run is not positioned by its descriptor");

            return CountInk(img, (int)OriginX, 8, textEndX, 36);
        }

        private static int CountInk(byte[] px, int x0, int y0, int x1, int y1)
        {
            int n = 0;
            for (int y = y0; y < y1; y++)
                for (int x = x0; x < x1; x++)
                    if (px[(y * W + x) * 4] < 128) n++;
            return n;
        }

        [Fact]
        public void GlyphRun_ResolvesItsFontFromAManagedDescriptor_NoCom()
        {
            int ink = RenderManaged(simulations: 0);
            Assert.True(ink > 80,
                $"the managed resolver produced only {ink} ink pixels; the 'WFNT' descriptor did not resolve a usable face");
        }

        /// <summary>
        /// The simulation BITS in the descriptor must reach the reader. Bold is the observable one:
        /// the same glyph ids and advances, thickened. Without this, a resolver that ignored the
        /// simulations field entirely would pass the test above.
        /// </summary>
        [Fact]
        public void BoldSimulationFlag_FromTheDescriptor_ThickensTheRun()
        {
            int regular = RenderManaged(simulations: 0);
            int bold = RenderManaged(simulations: 1);   // 1 = Bold

            Assert.True(bold > regular,
                $"the bold simulation flag did not thicken the run: bold={bold} ink pixels, regular={regular}");
        }
    }
}
