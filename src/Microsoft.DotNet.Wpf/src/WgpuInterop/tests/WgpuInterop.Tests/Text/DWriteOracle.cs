// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// DirectWrite as a TEST ORACLE: the alpha texture IDWriteGlyphRunAnalysis makes for a glyph run,
// asked exactly the way wpfgfx asks it (CGlyphRunResource::CreateRealization: pixelsPerDip 1, a
// scale transform, baseline at 0,0, the natural measuring mode). Windows only, and only in tests --
// the port itself never calls DirectWrite. Raw vtable calls rather than a COM interop layer, so the
// oracle carries no dependency the product does not have.
//

using System;
using System.Runtime.InteropServices;

namespace WgpuInterop.Tests.Text
{
    internal static unsafe class DWriteOracle
    {
        [DllImport("dwrite.dll")] private static extern int DWriteCreateFactory(int type, in Guid iid, out IntPtr factory);

        private static readonly Guid IID_IDWriteFactory = new("b859ee5a-d838-4b5b-a2e8-1adc7d93db48");
        private static IntPtr s_factory;

        private static void** V(IntPtr p) => *(void***)p;

        private static void Check(int hr, string what)
        {
            if (hr < 0) throw new InvalidOperationException($"DirectWrite {what}: 0x{hr:X8}");
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct GlyphRun
        {
            public IntPtr FontFace; public float EmSize; public uint GlyphCount;
            public ushort* Indices; public float* Advances; public float* Offsets; public int IsSideways; public uint BidiLevel;
        }

        private static IntPtr Factory()
        {
            if (s_factory == IntPtr.Zero) Check(DWriteCreateFactory(1, IID_IDWriteFactory, out s_factory), "factory");
            return s_factory;
        }

        /// <summary>An IDWriteFontFace for a font file, or zero.</summary>
        internal static IntPtr FontFace(string path, int faceIndex = 0)
        {
            IntPtr factory = Factory(), file, face;
            fixed (char* pp = path)
                // IDWriteFactory::CreateFontFileReference = slot 7
                Check(((delegate* unmanaged[Stdcall]<IntPtr, char*, void*, IntPtr*, int>)V(factory)[7])(factory, pp, null, &file), "file");
            int supported, fileType, faceType, faces;
            // IDWriteFontFile::Analyze = slot 5
            Check(((delegate* unmanaged[Stdcall]<IntPtr, int*, int*, int*, int*, int>)V(file)[5])(file, &supported, &fileType, &faceType, &faces), "analyze");
            // IDWriteFactory::CreateFontFace = slot 9
            Check(((delegate* unmanaged[Stdcall]<IntPtr, int, uint, IntPtr*, uint, int, IntPtr*, int>)V(factory)[9])(
                factory, faceType, 1, &file, (uint)faceIndex, 0, &face), "face");
            Marshal.Release(file);
            return face;
        }

        /// <summary>DWRITE_RENDERING_MODE_NATURAL (4) or NATURAL_SYMMETRIC (5) -- or whatever DirectWrite
        /// recommends for this face and size under the default rendering params.</summary>
        internal static int RecommendedMode(IntPtr face, float emSize)
        {
            IntPtr factory = Factory(), prm;
            // IDWriteFactory::CreateRenderingParams = slot 10
            Check(((delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, int>)V(factory)[10])(factory, &prm), "params");
            int mode;
            // IDWriteFontFace::GetRecommendedRenderingMode = slot 15
            Check(((delegate* unmanaged[Stdcall]<IntPtr, float, float, int, IntPtr, int*, int>)V(face)[15])(face, emSize, 1f, 0, prm, &mode), "mode");
            Marshal.Release(prm);
            return mode;
        }

        /// <summary>The ClearType 3x1 alpha texture DirectWrite makes for the run, and its bounds in
        /// pixels relative to the baseline origin.</summary>
        internal static byte[] AlphaTexture(IntPtr face, float emSize, ushort[] glyphs, float[] advances,
            float[]? offsets, int mode, out int left, out int top, out int right, out int bottom, int gridFit = 0,
            int measuring = 0)
        {
            IntPtr factory = Factory(), analysis;
            int n = glyphs.Length;
            offsets ??= new float[2 * n];
            float* xf = stackalloc float[6] { 1, 0, 0, 1, 0, 0 };
            int* rc = stackalloc int[4];
            fixed (ushort* pi = glyphs) fixed (float* pa = advances) fixed (float* po = offsets)
            {
                var run = new GlyphRun { FontFace = face, EmSize = emSize, GlyphCount = (uint)n, Indices = pi, Advances = pa, Offsets = po };
                if (gridFit == 0)
                    // IDWriteFactory::CreateGlyphRunAnalysis = slot 23
                    Check(((delegate* unmanaged[Stdcall]<IntPtr, GlyphRun*, float, float*, int, int, float, float, IntPtr*, int>)V(factory)[23])(
                        factory, &run, 1f, xf, mode, measuring, 0f, 0f, &analysis), "analysis");
                else
                {
                    // IDWriteFactory2::CreateGlyphRunAnalysis = slot 30, which takes the grid-fit mode
                    // (1 disabled, 2 enabled) explicitly.
                    IntPtr f2;
                    Guid iid = new("0439fc60-ca44-4994-8dee-3a9af7b732ec");
                    Check(Marshal.QueryInterface(factory, in iid, out f2), "factory2");
                    try
                    {
                        Check(((delegate* unmanaged[Stdcall]<IntPtr, GlyphRun*, float*, int, int, int, int, float, float, IntPtr*, int>)V(f2)[30])(
                            f2, &run, null, mode, measuring, gridFit, 0, 0f, 0f, &analysis), "analysis2");
                    }
                    finally { Marshal.Release(f2); }
                }
            }
            try
            {
                // IDWriteGlyphRunAnalysis::GetAlphaTextureBounds = slot 3, CreateAlphaTexture = slot 4
                Check(((delegate* unmanaged[Stdcall]<IntPtr, int, int*, int>)V(analysis)[3])(analysis, 1, rc), "bounds");
                left = rc[0]; top = rc[1]; right = rc[2]; bottom = rc[3];
                int size = Math.Max(0, (right - left) * (bottom - top) * 3);
                var tex = new byte[size];
                if (size > 0)
                    fixed (byte* pt = tex)
                        Check(((delegate* unmanaged[Stdcall]<IntPtr, int, int*, byte*, int, int>)V(analysis)[4])(analysis, 1, rc, pt, size), "texture");
                return tex;
            }
            finally { Marshal.Release(analysis); }
        }
    }
}
