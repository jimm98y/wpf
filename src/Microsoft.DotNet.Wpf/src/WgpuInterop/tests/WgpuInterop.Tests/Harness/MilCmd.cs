// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// MILCMD command and record assembly, shared by every test that drives MilcoreEngine.
//
// This is the single copy of what used to be duplicated across twenty standalone test apps, each
// with its own private `Buf` and its own hand-rolled `SolidColorBrush` / `RenderDataHeader` /
// `DrawRectangleRecord`. Duplication is worse here than usual: these byte layouts mirror generated
// native struct offsets, so a copy that drifts does not fail to compile -- it silently decodes as a
// different command and the test still "passes" while exercising nothing.
//
// Field offsets are recorded in the comments because they are the whole content of these methods and
// cannot be inferred from the C#: the marshalling is positional, and several commands order their
// fields counter-intuitively (DrawRectangle puts its handles AFTER the rect; DrawGeometry puts them
// first).
//

using System;
using System.Collections.Generic;

namespace WgpuInterop.Tests.Harness
{
    /// <summary>Little-endian byte accumulator for MILCMD payloads.</summary>
    internal sealed class Buf
    {
        private readonly List<byte> _b = new();
        public int Length => _b.Count;
        public void U8(byte v) => _b.Add(v);
        public void U16(ushort v) => _b.AddRange(BitConverter.GetBytes(v));
        public void U32(uint v) => _b.AddRange(BitConverter.GetBytes(v));
        public void U64(ulong v) => _b.AddRange(BitConverter.GetBytes(v));
        public void F32(float v) => _b.AddRange(BitConverter.GetBytes(v));
        public void F64(double v) => _b.AddRange(BitConverter.GetBytes(v));
        public void Raw(byte[] v) => _b.AddRange(v);
        public byte[] ToArray() => _b.ToArray();
    }

    internal static class MilCmd
    {
        // ---- resources ---------------------------------------------------------------------

        public static byte[] SolidColorBrush(uint h, float r, float g, float b, float a, double opacity = 1.0)
        {
            var q = new Buf(); q.U32(0x7e); q.U32(h); q.F64(opacity); q.F32(r); q.F32(g); q.F32(b); q.F32(a);
            q.U32(0); q.U32(0); q.U32(0); q.U32(0); return q.ToArray();
        }

        /// <summary>MILCMD_RECTANGLEGEOMETRY: Handle@4, RadiusX@8, RadiusY@16, Rect@24, hTransform@56.</summary>
        public static byte[] RectangleGeometry(uint h, double x, double y, double w, double hh,
            double radiusX = 0, double radiusY = 0)
        {
            var q = new Buf(); q.U32(0x79); q.U32(h); q.F64(radiusX); q.F64(radiusY);
            q.F64(x); q.F64(y); q.F64(w); q.F64(hh); q.U32(0); return q.ToArray();
        }

        /// <summary>
        /// MILCMD_MESHGEOMETRY3D: Handle@4, PositionsSize@8, NormalsSize@12, TextureCoordinatesSize@16,
        /// TriangleIndicesSize@20 (all BYTES), then positions/normals as MilPoint3F (3 floats each),
        /// texture coordinates as MilPoint2D (2 DOUBLES each), then UInt32 indices.
        /// </summary>
        public static byte[] MeshGeometry3D(uint h, float[] positionsXyz, float[] normalsXyz, uint[] indices)
        {
            var q = new Buf(); q.U32(0x62); q.U32(h);
            q.U32((uint)(positionsXyz.Length * 4)); q.U32((uint)(normalsXyz.Length * 4));
            q.U32(0); q.U32((uint)(indices.Length * 4));
            foreach (float f in positionsXyz) q.F32(f);
            foreach (float f in normalsXyz) q.F32(f);
            foreach (uint i in indices) q.U32(i);
            return q.ToArray();
        }

        /// <summary>MILCMD_GEOMETRYGROUP: Handle@4, hTransform@8, FillRule@12, ChildrenSize@16 (BYTES), children.</summary>
        public static byte[] GeometryGroup(uint h, uint fillRule, params uint[] children)
        {
            var q = new Buf(); q.U32(0x7b); q.U32(h); q.U32(0); q.U32(fillRule); q.U32((uint)(children.Length * 4));
            foreach (uint c in children) q.U32(c);
            return q.ToArray();
        }

        /// <summary>MILCMD_COMBINEDGEOMETRY: Handle@4, hTransform@8, Mode@12, hGeometry1@16, hGeometry2@20.</summary>
        public static byte[] CombinedGeometry(uint h, uint mode, uint g1, uint g2)
        {
            var q = new Buf(); q.U32(0x7c); q.U32(h); q.U32(0); q.U32(mode); q.U32(g1); q.U32(g2); return q.ToArray();
        }

        /// <summary>MILCMD_GUIDELINESET: Handle@4, XSize@8, YSize@12 (BYTES), IsDynamic@16, then DOUBLES.</summary>
        public static byte[] GuidelineSet(uint h, double[] xs, double[] ys)
        {
            var q = new Buf(); q.U32(0x8c); q.U32(h); q.U32((uint)(xs.Length * 8)); q.U32((uint)(ys.Length * 8)); q.U32(0);
            foreach (double d in xs) q.F64(d);
            foreach (double d in ys) q.F64(d);
            return q.ToArray();
        }

        /// <summary>MILCMD_IMAGEDRAWING: Handle@4, Rect@8 (4 doubles), hImageSource@40.</summary>
        public static byte[] ImageDrawing(uint h, double x, double y, double w, double ht, uint hImg)
        {
            var b = new Buf(); b.U32(0x89); b.U32(h);
            b.F64(x); b.F64(y); b.F64(w); b.F64(ht); b.U32(hImg); b.U32(0);
            return b.ToArray();
        }

        /// <summary>MILCMD_GLYPHRUNDRAWING: Handle@4, hGlyphRun@8, hForegroundBrush@12.</summary>
        public static byte[] GlyphRunDrawing(uint h, uint hRun, uint hBrush)
        { var b = new Buf(); b.U32(0x88); b.U32(h); b.U32(hRun); b.U32(hBrush); return b.ToArray(); }

        /// <summary>MILCMD_DRAWINGIMAGE: a Drawing used as an ImageSource (vector icons).</summary>
        public static byte[] DrawingImage(uint h, uint hDrawing)
        { var b = new Buf(); b.U32(0x71); b.U32(h); b.U32(hDrawing); return b.ToArray(); }

        /// <summary>MILCMD_GEOMETRYDRAWING: Handle@4, hBrush@8, hPen@12, hGeometry@16.</summary>
        public static byte[] GeometryDrawing(uint h, uint hBrush, uint hPen, uint hGeometry)
        {
            var q = new Buf(); q.U32(0x87); q.U32(h); q.U32(hBrush); q.U32(hPen); q.U32(hGeometry); return q.ToArray();
        }

        /// <summary>
        /// MILCMD_DRAWINGGROUP (0x8b): Handle@4, Opacity@8, ChildrenSize@16, hTransform@32; the fixed
        /// struct runs to 52 (ClearTypeHint is the last field) and children follow.
        /// </summary>
        public static byte[] DrawingGroup(uint h, double opacity, uint[] children)
        {
            var q = new Buf(); q.U32(0x8b); q.U32(h); q.F64(opacity); q.U32((uint)(children.Length * 4));
            while (q.Length < 52) q.U32(0);
            foreach (uint c in children) q.U32(c);
            return q.ToArray();
        }

        /// <summary>MILCMD_RECTRESOURCE: Handle@4, Value@8 (four doubles).</summary>
        public static byte[] RectResource(uint h, double x, double y, double w, double hh)
        {
            var q = new Buf(); q.U32(0x11); q.U32(h); q.F64(x); q.F64(y); q.F64(w); q.F64(hh); return q.ToArray();
        }

        /// <summary>
        /// MILCMD_BITMAPCACHEBRUSH: Handle@4, Opacity@8, hOpacityAnimations@16, hTransform@20,
        /// hRelativeTransform@24, hBitmapCache@28, hInternalTarget@32.
        /// </summary>
        public static byte[] BitmapCacheBrush(uint h, double opacity, uint hInternalTarget)
        {
            var q = new Buf(); q.U32(0x84); q.U32(h); q.F64(opacity);
            q.U32(0); q.U32(0); q.U32(0); q.U32(0); q.U32(hInternalTarget);
            return q.ToArray();
        }

        /// <summary>
        /// MILCMD_LINEARGRADIENTBRUSH. Stops are 24 bytes each: offset (double) then colour (4 floats).
        /// This overload builds white-opaque to white-transparent, which is the shape an opacity mask
        /// needs (only the alpha ramp matters).
        /// </summary>
        public static byte[] LinearGradientAlphaBrush(uint h, double x0, double y0, double x1, double y1)
        {
            var q = new Buf(); q.U32(0x7f); q.U32(h); q.F64(1.0);
            q.F64(x0); q.F64(y0); q.F64(x1); q.F64(y1);
            q.U32(0); q.U32(0); q.U32(0);       // hOpacityAnim, hTransform, hRelativeTransform
            q.U32(0);                           // ColorInterpolationMode
            q.U32(1);                           // MappingMode = RelativeToBoundingBox
            q.U32(0);                           // SpreadMethod = Pad
            q.U32(2 * 24);                      // GradientStopsSize (bytes)
            q.U32(0); q.U32(0);                 // hStartPointAnim, hEndPointAnim
            q.F64(0.0); q.F32(1); q.F32(1); q.F32(1); q.F32(1);
            q.F64(1.0); q.F32(1); q.F32(1); q.F32(1); q.F32(0);
            return q.ToArray();
        }

        /// <summary>
        /// MILCMD_GLYPHRUN plus a 'WFNT' TRAILER: the managed (COM-free) font descriptor.
        ///
        /// This is the cross-platform replacement for DWriteFontResolver. The trailer carries exactly
        /// what WPF's managed GlyphTypeface supplies -- font file path, face index, style simulations
        /// -- so the engine resolves the face itself with no DirectWrite and no COM. The legacy
        /// pIDWriteFont pointer field stays zero on this path.
        /// </summary>
        public static byte[] GlyphRunWithManagedFont(uint handle, float ox, float oy, float emSize,
            ushort[] indices, float[] advances, string fontPath, int faceIndex = 0, int simulations = 0)
        {
            var b = new Buf();
            b.U32(0x3a); b.U32(handle);
            b.U64(0);                                  // pIDWriteFont: unused on the managed path
            b.U16(0); b.U16(0); b.F32(ox); b.F32(oy); b.F32(emSize);
            b.F64(0); b.F64(0); b.F64(0); b.F64(0);
            b.U16((ushort)indices.Length);
            b.U16(0); b.U16(0); b.U16(0); b.U16(0); b.U16(0);
            foreach (ushort g in indices) b.U16(g);
            foreach (float a in advances) b.F32(a);

            byte[] path = System.Text.Encoding.UTF8.GetBytes(fontPath);
            b.U32(0x544E4657);                         // 'W','F','N','T', little-endian
            b.U32((uint)faceIndex);
            b.U32((uint)simulations);
            b.U32((uint)path.Length);
            b.Raw(path);
            return b.ToArray();
        }

        /// <summary>MILCMD_GLYPHRUN: the run's font, origin, em size and per-glyph indices/advances.</summary>
        public static byte[] GlyphRun(uint handle, ulong fontPtr, float ox, float oy, float emSize,
            ushort[] indices, float[] advances)
        {
            var b = new Buf();
            b.U32(0x3a); b.U32(handle); b.U64(fontPtr);
            b.U16(0); b.U16(0); b.F32(ox); b.F32(oy); b.F32(emSize);
            b.F64(0); b.F64(0); b.F64(0); b.F64(0);
            b.U16((ushort)indices.Length); b.U16(0); b.U16(0); b.U16(0); b.U16(0); b.U16(0);
            foreach (ushort g in indices) b.U16(g);
            foreach (float a in advances) b.F32(a);
            return b.ToArray();
        }

        // ---- render-data records -----------------------------------------------------------

        /// <summary>Wrap a payload as a render-data record: total size, opcode, payload.</summary>
        public static byte[] Record(uint id, byte[] payload)
        {
            var b = new Buf(); b.U32((uint)(payload.Length + 8)); b.U32(id); b.Raw(payload); return b.ToArray();
        }

        public static byte[] DrawGeometryRecord(uint hBrush, uint hPen, uint hGeometry)
        {
            var p = new Buf(); p.U32(hBrush); p.U32(hPen); p.U32(hGeometry); p.U32(0);
            return Record(0x46, p.ToArray());
        }

        /// <summary>
        /// MILCMD_DRAW_RECTANGLE: rectangle@0 (4 doubles), hBrush@32, hPen@36. The handles come AFTER
        /// the rect here, unlike DrawGeometry where they come first.
        /// </summary>
        public static byte[] DrawRectangleRecord(uint hBrush, uint hPen, double x, double y, double w, double h)
        {
            var p = new Buf(); p.F64(x); p.F64(y); p.F64(w); p.F64(h); p.U32(hBrush); p.U32(hPen);
            return Record(0x40, p.ToArray());
        }

        /// <summary>MILCMD_DRAW_RECTANGLE_ANIMATE: rectangle@0, hBrush@32, hPen@36, hRectangleAnimations@40, pad@44.</summary>
        public static byte[] DrawRectangleAnimateRecord(uint hBrush, uint hPen,
            double x, double y, double w, double h, uint hRectAnim)
        {
            var p = new Buf(); p.F64(x); p.F64(y); p.F64(w); p.F64(h);
            p.U32(hBrush); p.U32(hPen); p.U32(hRectAnim); p.U32(0);
            return Record(0x41, p.ToArray());
        }

        /// <summary>
        /// MILCMD_IMAGEBRUSH: Handle@4, Opacity@8, Viewport@16, Viewbox@48, ViewportUnits@108,
        /// ViewboxUnits@112, Stretch@124, TileMode@128, hImageSource@144.
        ///
        /// The gaps are padded explicitly rather than by writing consecutive fields: this struct has
        /// several reserved runs, and a builder that just concatenates the fields it cares about
        /// lands hImageSource dozens of bytes early and decodes as a null brush.
        /// </summary>
        public static byte[] ImageBrush(uint handle, uint hImageSource, uint stretch,
            double vbX = 0, double vbY = 0, double vbW = 1, double vbH = 1)
        {
            var b = new Buf();
            b.U32(0x81); b.U32(handle);
            b.F64(1.0);                                       // Opacity @8
            while (b.Length < 16) b.U8(0);
            b.F64(0); b.F64(0); b.F64(1); b.F64(1);           // Viewport @16 = (0,0,1,1)
            while (b.Length < 48) b.U8(0);
            b.F64(vbX); b.F64(vbY); b.F64(vbW); b.F64(vbH);   // Viewbox @48
            while (b.Length < 108) b.U8(0);
            b.U32(1);                                         // ViewportUnits @108 = RelativeToBoundingBox
            b.U32(1);                                         // ViewboxUnits  @112 = RelativeToBoundingBox
            while (b.Length < 124) b.U8(0);
            b.U32(stretch);                                   // Stretch @124
            while (b.Length < 144) b.U8(0);                    // TileMode @128 = None
            b.U32(hImageSource);                              // hImageSource @144
            return b.ToArray();
        }

        /// <summary>MILCMD_DRAW_IMAGE: rectangle@0 (4 doubles), hImageSource@32, pad@36.</summary>
        public static byte[] DrawImageRecord(double x, double y, double w, double h, uint hImg)
        {
            var p = new Buf(); p.F64(x); p.F64(y); p.F64(w); p.F64(h); p.U32(hImg); p.U32(0);
            return Record(0x47, p.ToArray());
        }

        /// <summary>MILCMD_DRAW_ROUNDED_RECTANGLE: rect@0, radiusX@32, radiusY@40, hBrush@48, hPen@52.</summary>
        public static byte[] DrawRoundedRectangleRecord(uint hBrush, double x, double y, double w, double h,
            double radiusX = 0, double radiusY = 0)
        {
            var p = new Buf();
            p.F64(x); p.F64(y); p.F64(w); p.F64(h); p.F64(radiusX); p.F64(radiusY); p.U32(hBrush); p.U32(0);
            return Record(0x42, p.ToArray());
        }

        /// <summary>
        /// MILCMD_DRAW_VIDEO (0x4b): rectangle (4 doubles), hPlayer, pad.
        ///
        /// It carries only a HANDLE. The pixels arrive out of band from the platform media backend
        /// through IMilRenderTargetSink.SendVideoFrame, and the decoder pairs the two up -- which is
        /// why a video visual's render data is byte-identical every frame.
        /// </summary>
        public static byte[] DrawVideoRecord(uint hPlayer, double x, double y, double w, double h)
        {
            var p = new Buf(); p.F64(x); p.F64(y); p.F64(w); p.F64(h); p.U32(hPlayer); p.U32(0);
            return Record(0x4b, p.ToArray());
        }

        public static byte[] DrawDrawingRecord(uint hDrawing)
        {
            var p = new Buf(); p.U32(hDrawing); p.U32(0); return Record(0x4a, p.ToArray());
        }

        public static byte[] DrawGlyphRunRecord(uint hBrush, uint hRun)
        {
            var p = new Buf(); p.U32(hBrush); p.U32(hRun); return Record(0x49, p.ToArray());
        }

        /// <summary>MILCMD_PUSH_OPACITY_MASK: boundingBoxCacheLocalSpace@0 (4 floats), hOpacityMask@16, pad@20.</summary>
        public static byte[] PushOpacityMaskRecord(uint hMask)
        {
            var p = new Buf(); p.F32(0); p.F32(0); p.F32(0); p.F32(0); p.U32(hMask); p.U32(0);
            return Record(0x4e, p.ToArray());
        }

        public static byte[] PushEffectRecord()
        { var b = new Buf(); b.U32(8); b.U32(0x55); return b.ToArray(); }

        public static byte[] PushClipRecord(uint hClip)
        { var b = new Buf(); b.U32(16); b.U32(0x4d); b.U32(hClip); b.U32(0); return b.ToArray(); }

        public static byte[] PushGuidelineSetRecord(uint h)
        { var p = new Buf(); p.U32(h); p.U32(0); return Record(0x52, p.ToArray()); }

        public static byte[] PushGuidelineY1Record(double y)
        { var p = new Buf(); p.F64(y); return Record(0x53, p.ToArray()); }

        public static byte[] PushGuidelineY2Record(double lead, double offset)
        { var p = new Buf(); p.F64(lead); p.F64(offset); return Record(0x54, p.ToArray()); }

        /// <summary>MILCMD_DRAW_ELLIPSE: centre@0, radiusX@16, radiusY@24, hBrush@32, hPen@36.</summary>
        public static byte[] DrawEllipseRecord(uint hBrush, double cx, double cy, double rx, double ry)
        {
            var p = new Buf(); p.F64(cx); p.F64(cy); p.F64(rx); p.F64(ry); p.U32(hBrush); p.U32(0);
            return Record(0x44, p.ToArray());
        }

        public static byte[] PushOpacityRecord(double opacity)
        { var b = new Buf(); b.U32(16); b.U32(0x4f); b.F64(opacity); return b.ToArray(); }

        public static byte[] PopRecord()
        { var b = new Buf(); b.U32(8); b.U32(0x56); return b.ToArray(); }

        // ---- visual tree -------------------------------------------------------------------

        public static byte[] RenderDataHeader(uint handle, int cbData)
        { var b = new Buf(); b.U32(0x18); b.U32(handle); b.U32((uint)cbData); return b.ToArray(); }

        /// <summary>
        /// These three use the renderer's own Mil opcode enum rather than a literal. The rest of this
        /// file hard-codes opcodes on purpose -- a decoder and a test that share a constant cannot
        /// disprove it -- but offset/alpha/target-root are the ones the ENGINE'S OWN table names, and
        /// a mismatch there is a build break rather than a silent misdecode.
        /// </summary>
        public static byte[] VisualSetOffset(uint handle, double x, double y)
        {
            var b = new Buf();
            b.U32((uint)Microsoft.Wpf.Interop.WebGpu.Composition.Protocol.Mil.VisualSetOffset);
            b.U32(handle); b.F64(x); b.F64(y);
            return b.ToArray();
        }

        public static byte[] VisualSetAlpha(uint handle, double alpha)
        {
            var b = new Buf();
            b.U32((uint)Microsoft.Wpf.Interop.WebGpu.Composition.Protocol.Mil.VisualSetAlpha);
            b.U32(handle); b.F64(alpha);
            return b.ToArray();
        }

        public static byte[] TargetSetRoot(uint handle, uint hRoot)
        {
            var b = new Buf();
            b.U32((uint)Microsoft.Wpf.Interop.WebGpu.Composition.Protocol.Mil.TargetSetRoot);
            b.U32(handle); b.U32(hRoot);
            return b.ToArray();
        }

        /// <summary>A render-data header plus its payload, as one command.</summary>
        public static byte[] RenderData(uint handle, byte[] payload)
        {
            var b = new Buf();
            b.Raw(RenderDataHeader(handle, payload.Length));
            b.Raw(payload);
            return b.ToArray();
        }

        public static byte[] VisualInsertChildAt(uint parent, uint child, uint index)
        { var b = new Buf(); b.U32(0x26); b.U32(parent); b.U32(child); b.U32(index); return b.ToArray(); }

        public static byte[] VisualSetContent(uint handle, uint hContent)
        { var b = new Buf(); b.U32(0x22); b.U32(handle); b.U32(hContent); return b.ToArray(); }

        public static byte[] VisualSetTransform(uint handle, uint hTransform)
        { var b = new Buf(); b.U32(0x1c); b.U32(handle); b.U32(hTransform); return b.ToArray(); }

        public static byte[] VisualSetClip(uint handle, uint hClip)
        { var b = new Buf(); b.U32(0x1f); b.U32(handle); b.U32(hClip); return b.ToArray(); }

        public static byte[] VisualSetOpacityMask(uint handle, uint hMask)
        { var b = new Buf(); b.U32(0x23); b.U32(handle); b.U32(hMask); return b.ToArray(); }

        public static byte[] VisualSetEffect(uint handle, uint hEffect)
        { var b = new Buf(); b.U32(0x1d); b.U32(handle); b.U32(hEffect); return b.ToArray(); }

        /// <summary>
        /// MILCMD_PIXELSHADER: Handle@4, ShaderRenderMode@8, BytecodeSize@12,
        /// CompileSoftwareShader@16, then the bytecode blob.
        /// </summary>
        public static byte[] PixelShader(uint handle, byte[] code)
        {
            var b = new Buf();
            b.U32(0x6c); b.U32(handle);
            b.U32(0);                       // ShaderRenderMode
            b.U32((uint)code.Length);
            b.U32(1);                       // CompileSoftwareShader
            b.Raw(code);
            return b.ToArray();
        }

        /// <summary>
        /// MILCMD_SHADEREFFECT: Handle@4, four doubles of padding, hPixelShader@40,
        /// DdxUvDdyUvRegisterIndex@44, then EIGHT payload sizes, then the payloads themselves.
        ///
        /// The eight sizes are the trap. They describe float/int/bool/sampler register-index and
        /// value blobs, and every one has to be written even when empty -- a builder that emits only
        /// the non-empty pairs shifts the payloads and the effect decodes with no constants, which
        /// renders as no effect rather than as an error.
        /// </summary>
        public static byte[] ShaderEffect(uint handle, uint hShader, ushort register,
            float r, float g, float b2, float a)
        {
            var b = new Buf();
            b.U32(0x70); b.U32(handle);
            b.F64(0); b.F64(0); b.F64(0); b.F64(0);        // padding
            b.U32(hShader);
            b.U32(unchecked((uint)-1));                     // DdxUvDdyUvRegisterIndex
            b.U32(2);                                       // float register indices: 1 x Int16
            b.U32(16);                                      // float values: 1 x float4
            b.U32(0); b.U32(0);                             // int registers / values
            b.U32(0); b.U32(0);                             // bool registers / values
            b.U32(0); b.U32(0);                             // sampler info / values
            b.U16(register);
            b.F32(r); b.F32(g); b.F32(b2); b.F32(a);
            return b.ToArray();
        }

        /// <summary>
        /// MILCMD_BLUREFFECT: Handle@4, Radius@8, hRadiusAnimations@16, KernelType@20, RenderingBias@24.
        ///
        /// The FULL 28-byte struct, not an abbreviated prefix. The trailing fields are real wire
        /// content and KernelType (0 Gaussian, 1 Box) sits past the animation handle -- the decoder
        /// used to stop after Radius, so a Box blur silently rendered as a Gaussian one.
        /// </summary>
        public static byte[] BlurEffect(uint handle, double radius, uint kernelType = 0)
        {
            var b = new Buf();
            b.U32(0x6e); b.U32(handle);
            b.F64(radius);
            b.U32(0);              // hRadiusAnimations
            b.U32(kernelType);
            b.U32(0);              // RenderingBias = Performance
            return b.ToArray();
        }

        /// <summary>
        /// MILCMD_DROPSHADOWEFFECT: Handle@4, ShadowDepth@8, Color@16, Direction@32, Opacity@40,
        /// BlurRadius@48. Depth plus direction (degrees) is what the decoder converts into an x/y
        /// offset -- WPF does not send the offset itself, so a decoder that ignored Direction would
        /// still produce a shadow, just in the wrong place.
        /// </summary>
        public static byte[] DropShadowEffect(uint handle, double depth, double directionDegrees,
            double opacity, double blurRadius, float r = 0, float g = 0, float b2 = 0)
        {
            var b = new Buf();
            b.U32(0x6f); b.U32(handle);
            b.F64(depth);
            b.F32(r); b.F32(g); b.F32(b2); b.F32(1f);   // Color; Opacity folds into alpha separately
            b.F64(directionDegrees); b.F64(opacity); b.F64(blurRadius);
            return b.ToArray();
        }

        /// <summary>
        /// MILCMD_LINEARGRADIENTBRUSH with arbitrary stops. Each stop is 24 bytes: offset (double)
        /// then colour (4 floats). GradientStopsSize is in BYTES, not stop count -- a builder that
        /// writes the count decodes as a truncated stop list.
        /// </summary>
        public static byte[] LinearGradientBrush(uint handle, double sx, double sy, double ex, double ey,
            uint mappingMode, (float Pos, float R, float G, float B, float A)[] stops, uint spreadMethod = 0)
        {
            var b = new Buf();
            b.U32(0x7f); b.U32(handle);
            b.F64(1.0);                        // Opacity
            b.F64(sx); b.F64(sy);              // StartPoint
            b.F64(ex); b.F64(ey);              // EndPoint
            b.U32(0); b.U32(0); b.U32(0);      // hOpacityAnim, hTransform, hRelativeTransform
            b.U32(0);                          // ColorInterpolationMode
            b.U32(mappingMode);                // BrushMappingMode (1 = RelativeToBoundingBox)
            b.U32(spreadMethod);
            b.U32((uint)(stops.Length * 24));  // GradientStopsSize, in BYTES
            b.U32(0); b.U32(0);                // hStartPointAnim, hEndPointAnim
            foreach ((float pos, float r, float g, float bl, float a) in stops)
            { b.F64(pos); b.F32(r); b.F32(g); b.F32(bl); b.F32(a); }
            return b.ToArray();
        }

        /// <summary>MILCMD_ELLIPSEGEOMETRY: Handle@4, RadiusX@8, RadiusY@16, Center@24.</summary>
        public static byte[] EllipseGeometry(uint handle, double rx, double ry, double cx, double cy)
        {
            var b = new Buf(); b.U32(0x7a); b.U32(handle);
            b.F64(rx); b.F64(ry); b.F64(cx); b.F64(cy); return b.ToArray();
        }

        /// <summary>
        /// MILCMD_VISUAL_SETRENDEROPTIONS (0x21): Handle@4, then MilRenderOptions@8 as SEVEN u32s.
        ///
        /// All seven are parameters rather than defaults on purpose. The two fields the renderer
        /// honours (EdgeMode, BitmapScalingMode) sit either side of ones it skips, so a test that
        /// left the others zero would pass even if the struct walk were off by a field -- it would
        /// read CompositingMode as EdgeMode and silently alias the two.
        /// </summary>
        public static byte[] SetRenderOptions(uint handle, uint flags, uint edgeMode, uint compositingMode,
            uint bitmapScalingMode, uint clearTypeHint, uint textRenderingMode, uint textHintingMode)
        {
            var b = new Buf();
            b.U32(0x21); b.U32(handle);
            b.U32(flags); b.U32(edgeMode); b.U32(compositingMode); b.U32(bitmapScalingMode);
            b.U32(clearTypeHint); b.U32(textRenderingMode); b.U32(textHintingMode);
            return b.ToArray();
        }

        // ---- transform resources -----------------------------------------------------------
        //
        // WPF's RenderTransform/LayoutTransform realize as these specific resource types rather than
        // as a general matrix, and MILCMD_VISUAL_SETTRANSFORM links one to a visual. Each carries its
        // own centre where it has one, so a rotate about a point is NOT decomposed into
        // translate-rotate-translate on the wire.

        public static byte[] TranslateTransform(uint h, double x, double y)
        { var b = new Buf(); b.U32(0x73); b.U32(h); b.F64(x); b.F64(y); return b.ToArray(); }

        public static byte[] ScaleTransform(uint h, double sx, double sy, double cx, double cy)
        { var b = new Buf(); b.U32(0x74); b.U32(h); b.F64(sx); b.F64(sy); b.F64(cx); b.F64(cy); return b.ToArray(); }

        public static byte[] RotateTransform(uint h, double angleDegrees, double cx, double cy)
        { var b = new Buf(); b.U32(0x76); b.U32(h); b.F64(angleDegrees); b.F64(cx); b.F64(cy); return b.ToArray(); }

        public static byte[] MatrixTransform(uint h, double m11, double m12, double m21, double m22,
            double dx, double dy)
        {
            var b = new Buf(); b.U32(0x77); b.U32(h);
            b.F64(m11); b.F64(m12); b.F64(m21); b.F64(m22); b.F64(dx); b.F64(dy);
            b.U32(0);   // hMatrixAnimations
            return b.ToArray();
        }

        /// <summary>
        /// MIL_PATHGEOMETRY carrying ONE closed triangle, encoded either as two MIL_SEGMENT_LINE
        /// records or as a single MIL_SEGMENT_POLY.
        ///
        /// Both encodings matter and are not interchangeable: StreamGeometry emits the POLY form,
        /// while a PathGeometry built from LineSegment objects emits the LINE form, so a decoder can
        /// support one and silently drop the other. The sizes are explicit because the blob is a
        /// self-describing tree -- blob size, figure size and segment count all have to agree or the
        /// walk desynchronises and the rest of the geometry is read as garbage.
        /// </summary>
        public static byte[] PathGeometryTriangle(uint handle,
            (double X, double Y) a, (double X, double Y) b, (double X, double Y) c, bool poly)
        {
            int segBytes = poly ? (16 + 2 * 16) : (32 + 32);
            int segCount = poly ? 1 : 2;
            int figureSize = 40 + segBytes;
            int blobSize = 48 + figureSize;

            var blob = new Buf();
            blob.U32((uint)blobSize); blob.U32(0);                 // MIL_PATHGEOMETRY: Size, Flags
            blob.F64(0); blob.F64(0); blob.F64(0); blob.F64(0);    // Bounds
            blob.U32(1); blob.U32(0);                              // FigureCount = 1, ForcePacking
            blob.U32(0); blob.U32(0x4);                            // MIL_PATHFIGURE: BackSize, Flags = IsClosed
            blob.U32((uint)segCount); blob.U32((uint)figureSize);
            blob.F64(a.X); blob.F64(a.Y);                          // StartPoint
            blob.U32(0); blob.U32(0);                              // OffsetToLastSegment, ForcePacking
            if (poly)
            {
                blob.U32(5); blob.U32(0); blob.U32(0); blob.U32(2);   // PolyLine: Type, Flags, BackSize, Count
                blob.F64(b.X); blob.F64(b.Y); blob.F64(c.X); blob.F64(c.Y);
            }
            else
            {
                blob.U32(1); blob.U32(0); blob.U32(0); blob.U32(0); blob.F64(b.X); blob.F64(b.Y);
                blob.U32(1); blob.U32(0); blob.U32(0); blob.U32(0); blob.F64(c.X); blob.F64(c.Y);
            }

            byte[] blobBytes = blob.ToArray();
            var cmd = new Buf();
            cmd.U32(0x7d); cmd.U32(handle); cmd.U32(0); cmd.U32(1); cmd.U32((uint)blobBytes.Length);
            cmd.Raw(blobBytes);
            return cmd.ToArray();
        }

        // ---- misc --------------------------------------------------------------------------

        public static byte[] Concat(params byte[][] parts)
        {
            var all = new List<byte>();
            foreach (byte[] p in parts) all.AddRange(p);
            return all.ToArray();
        }
    }
}
