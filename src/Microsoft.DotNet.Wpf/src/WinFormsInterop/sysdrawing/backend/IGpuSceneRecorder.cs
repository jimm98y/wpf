// The GPU-rasterization seam inside System.Drawing. When a Graphics has a recorder attached, its
// core drawing verbs (the ~20 the WinForms themes actually use) append primitives to a WebGPU scene
// instead of calling libgdiplus — so real control drawing is rasterized by WGSL. The interface lives
// in namespace System.Drawing (so Graphics.cs sees it with no extra dependency); the implementation
// (backend/SceneRecorder.cs) references WgpuInterop. Colors are passed as ARGB ints and text as a
// pixel em-size so this interface stays free of GDI+ / wgpu types.

namespace System.Drawing
{
    internal enum GradientShape { Rect, Ellipse, Polygon }

    // A resolved gradient: multi-stop, linear or radial. For linear, (Sx,Sy)->(Ex,Ey) are the
    // endpoints; for radial, (Sx,Sy) is the centre and (Ex,Ey) the radii. Offsets/Argb are parallel.
    internal readonly struct GradientDesc
    {
        public readonly bool Radial;
        public readonly float Sx, Sy, Ex, Ey;
        public readonly float[] Offsets;
        public readonly int[] Argb;
        public GradientDesc(bool radial, float sx, float sy, float ex, float ey, float[] offsets, int[] argb)
        { Radial = radial; Sx = sx; Sy = sy; Ex = ex; Ey = ey; Offsets = offsets; Argb = argb; }
    }

    internal interface IGpuSceneRecorder
    {
        void FillRect(float x, float y, float w, float h, int argb);
        // Gradient fill of a shape (rect/ellipse: x,y,w,h; polygon: polyXY flattened).
        void FillGradient(GradientShape shape, float x, float y, float w, float h, float[] polyXY, GradientDesc g);
        void FillEllipse(float x, float y, float w, float h, int argb);
        // Hatch fill of a shape: a small RGBA pattern tile (tileW x tileH) tiled every tileSize points.
        void FillHatch(GradientShape shape, float x, float y, float w, float h, float[] polyXY,
                       byte[] tileRgba, int tileW, int tileH, float tileSize);
        void FillPolygon(float[] xy, int argb);   // flattened x0,y0,x1,y1,…
        void DrawLine(float x1, float y1, float x2, float y2, int argb);

        /// <summary>A line stroked with a dash pattern. <paramref name="dashPattern"/> is in
        /// GDI+ units -- multiples of the pen width -- alternating on/off.</summary>
        void DrawDashedLine(float x1, float y1, float x2, float y2, int argb, float width, float[] dashPattern);
        void DrawArc(float x, float y, float w, float h, float startDeg, float sweepDeg, int argb, float thickness);
        /// <summary>simulations: 1 = bold, 2 = italic, as WPF's StyleSimulations counts them.</summary>
        void DrawText(string text, float x, float y, float emPx, int argb, int simulations, string fontFamily);
        void DrawImage(byte[] rgba, int pw, int ph, float dx, float dy, float dw, float dh);
        // Clip subsequent primitives to (or, if exclude, out of) a rect until ClearClip. Exclude is
        // how ThemeWin32Classic gaps the GroupBox border around its title.
        // Graphics.CompositingMode. SourceCopy replaces the destination (colour AND alpha)
        // instead of alpha-blending over it -- the documented way to build an off-screen
        // bitmap whose shapes do not blend with each other but still blend with whatever it
        // is later drawn onto.
        void SetCompositingMode(bool sourceCopy);
        void SetClipRect(float x, float y, float w, float h, bool exclude);
        void ClearClip();

        // Offset subsequent primitives by (dx,dy) until ResetTransform. Graphics.TranslateTransform
        // is how WinForms draws a composite control's parts: ToolStrip translates to each item's
        // bounds, draws it at the origin, and resets.
        void PushTranslate(float dx, float dy);
        void ResetTransform();
    }
}
