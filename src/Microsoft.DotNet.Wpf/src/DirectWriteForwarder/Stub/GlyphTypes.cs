// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// Compile-only stub. See DirectWriteForwarderStub.csproj for details.
// Mirrors GlyphMetrics.h, FontMetrics.h, GlyphOffset.h, DWriteFontFeature.h.

using System.Runtime.InteropServices;

namespace MS.Internal.Text.TextInterface
{
    [StructLayout(LayoutKind.Explicit)]
    public struct GlyphMetrics
    {
        [FieldOffset(0)] public int LeftSideBearing;
        [FieldOffset(4)] public uint AdvanceWidth;
        [FieldOffset(8)] public int RightSideBearing;
        [FieldOffset(12)] public int TopSideBearing;
        [FieldOffset(16)] public uint AdvanceHeight;
        [FieldOffset(20)] public int BottomSideBearing;
        [FieldOffset(24)] public int VerticalOriginY;
    }

    public sealed class FontMetrics
    {
        public ushort DesignUnitsPerEm;
        public ushort Ascent;
        public ushort Descent;
        public short LineGap;
        public ushort CapHeight;
        public ushort XHeight;
        public short UnderlinePosition;
        public ushort UnderlineThickness;
        public short StrikethroughPosition;
        public ushort StrikethroughThickness;

        public double Baseline
        {
            get { throw new System.PlatformNotSupportedException("DirectWrite text/font shaping is not available on this platform."); }
        }

        public double LineSpacing
        {
            get { throw new System.PlatformNotSupportedException("DirectWrite text/font shaping is not available on this platform."); }
        }
    }

    public struct GlyphOffset
    {
        public int du;
        public int dv;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct DWriteFontFeature
    {
        public DWriteFontFeatureTag nameTag;
        public uint parameter;

        public DWriteFontFeature(DWriteFontFeatureTag dwriteNameTag, uint dwriteParameter)
        {
            nameTag = dwriteNameTag;
            parameter = dwriteParameter;
        }
    }
}
