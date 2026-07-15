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
        void FillPolygon(float[] xy, int argb);   // flattened x0,y0,x1,y1,…
        void DrawLine(float x1, float y1, float x2, float y2, int argb);
        void DrawArc(float x, float y, float w, float h, float startDeg, float sweepDeg, int argb, float thickness);
        void DrawText(string text, float x, float y, float emPx, int argb);
        void DrawImage(byte[] rgba, int pw, int ph, float dx, float dy, float dw, float dh);
    }
}
