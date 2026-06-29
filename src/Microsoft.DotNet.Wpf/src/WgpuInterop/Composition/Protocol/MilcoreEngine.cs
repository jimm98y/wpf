// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// MilcoreEngine -- the managed composition backend that consumes WPF's *real*
// milcore command protocol (the exact binary that PresentationCore's DUCE.Channel
// already emits to native milcore), instead of the WgpuInterop mini-protocol that
// CompositionEngine decodes.
//
// Where CompositionEngine speaks a private, convenient wire format, this class is
// the seam that lets the genuine WPF MediaContext drive WebGPU: it mirrors the
// channel's resource/command surface (CreateOrAddRef / Release / SubmitCommand /
// Commit), maintains a handle -> UCE-resource table (visuals, render-data blobs,
// solid brushes) exactly like milcore's slave resource tree, and rebuilds a
// SceneVisual tree for WgpuSceneRenderer.
//
// Wire format reference (all little-endian, from src/Common/Graphics):
//   * Each fixed command is a MILCMD_* struct sent via Channel.SendCommand. The
//     first 4 bytes are the MILCMD id (see Mil enum below); offset 4 is the
//     ResourceHandle (uint) the command addresses.
//   * Resources are NOT created by a command -- they come in through
//     CreateOrAddRefOnChannel(handle, ResourceType), modelled by CreateOrAddRef.
//   * Render data (visual content) is a TYPE_RENDERDATA resource whose bytes are a
//     sub-stream of records, each framed by RecordHeader { int Size; MILCMD Id; }
//     (Size includes the 8-byte header, QWORD aligned) followed by a MILCMD_DRAW_*
//     payload.
//
// Scope: the structural visual commands + solid-colour rectangle fills -- the core
// path that proves the real protocol drives the renderer. Other resource types and
// draw records are parsed-and-skipped so the stream stays in sync.
//

using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text;

namespace Microsoft.Wpf.Interop.WebGpu.Composition.Protocol
{
    /// <summary>Resource type ids from src/Common/Graphics/wgx_core_types.cs.</summary>
    internal enum MilResourceTypeId : uint
    {
        Null = 0,
        Visual = 39,                // TYPE_VISUAL
        Viewport3DVisual = 40,      // TYPE_VIEWPORT3DVISUAL (2D node hosting a 3D scene)
        Visual3D = 41,              // TYPE_VISUAL3D
        RenderData = 43,            // TYPE_RENDERDATA
        SolidColorBrush = 75,       // TYPE_SOLIDCOLORBRUSH
    }

    /// <summary>MILCMD ids from src/Common/Graphics/wgx_core_types.cs.</summary>
    internal enum Mil : uint
    {
        RenderData = 0x18,
        VisualSetOffset = 0x1b,
        VisualSetTransform = 0x1c,
        VisualSetClip = 0x1f,
        VisualSetEffect = 0x1d,
        VisualSetAlpha = 0x20,
        VisualSetContent = 0x22,
        VisualSetOpacityMask = 0x23,
        VisualRemoveAllChildren = 0x24,
        VisualRemoveChild = 0x25,
        VisualInsertChildAt = 0x26,
        // 3D (Viewport3D / Visual3D / models / cameras / lights / 3D transforms).
        Viewport3DVisualSetCamera = 0x29,
        Viewport3DVisualSetViewport = 0x2a,
        Viewport3DVisualSet3DChild = 0x2b,
        Visual3DSetContent = 0x2c,
        Visual3DSetTransform = 0x2d,
        Visual3DRemoveAllChildren = 0x2e,
        Visual3DRemoveChild = 0x2f,
        Visual3DInsertChildAt = 0x30,
        AxisAngleRotation3D = 0x57,
        PerspectiveCamera = 0x59,
        Model3DGroup = 0x5c,
        AmbientLight = 0x5d,
        DirectionalLight = 0x5e,
        GeometryModel3D = 0x61,
        MeshGeometry3D = 0x62,
        DiffuseMaterial = 0x64,
        Transform3DGroup = 0x67,
        TranslateTransform3D = 0x68,
        ScaleTransform3D = 0x69,
        RotateTransform3D = 0x6a,
        MatrixTransform3D = 0x6b,
        HwndTargetCreate = 0x31,
        TargetUpdateWindowSettings = 0x33,
        GlyphRunCreate = 0x3a,
        TargetSetRoot = 0x35,
        TargetSetClearColor = 0x36,
        // Render-data draw records (inside a TYPE_RENDERDATA stream).
        DrawLine = 0x3e,
        DrawRectangle = 0x40,
        DrawRoundedRectangle = 0x42,
        DrawEllipse = 0x44,
        DrawGeometry = 0x46,
        DrawImage = 0x47,
        DrawGlyphRun = 0x49,
        PushClip = 0x4d,
        PushOpacity = 0x4f,
        PushTransform = 0x51,
        Pop = 0x56,
        // Resource update commands.
        LineGeometry = 0x78,
        RectangleGeometry = 0x79,
        EllipseGeometry = 0x7a,
        PathGeometry = 0x7d,
        SolidColorBrush = 0x7e,
        LinearGradientBrush = 0x7f,
        RadialGradientBrush = 0x80,
        ImageBrush = 0x81,
        DrawingBrush = 0x82,
        VisualBrush = 0x83,
        GeometryDrawing = 0x87,
        DrawingGroup = 0x8b,
        MatrixTransform = 0x77,
        TransformGroup = 0x72,
        TranslateTransform = 0x73,
        ScaleTransform = 0x74,
        SkewTransform = 0x75,
        RotateTransform = 0x76,
        DashStyle = 0x85,
        Pen = 0x86,
        BlurEffect = 0x6e,
        DropShadowEffect = 0x6f,
    }

    /// <summary>
    /// A composition target: the milcore analog of an HwndTarget. Carries the window
    /// handle + size + clear colour (from MILCMD_HWNDTARGET_CREATE) and the visual the
    /// target is rooted at (from MILCMD_TARGET_SETROOT). The sink presents each target
    /// to its HWND swap chain.
    /// </summary>
    internal sealed class MilTarget
    {
        public ulong Hwnd;
        public int Width;
        public int Height;
        public RgbaColor ClearColor = new(1, 1, 1, 1);
        public uint RootHandle;

        // From MILCMD_TARGET_UPDATEWINDOWSETTINGS: the live window rect (popups are created
        // at 1x1 then resized via this command) and transparency (non-zero => layered window,
        // e.g. ComboBox/Menu/ToolTip popups, which need per-pixel-alpha presentation).
        public uint Transparency;
        public bool IsLayered => Transparency != 0;
    }

    /// <summary>
    /// Decodes the genuine WPF milcore command stream into a SceneVisual tree.
    /// State is retained across batches, like a milcore partition.
    /// </summary>
    internal sealed partial class MilcoreEngine
    {
        private readonly Dictionary<uint, SceneVisual> _visuals = new();
        private readonly Dictionary<uint, byte[]> _renderData = new();
        private readonly Dictionary<uint, RgbaColor> _solidBrushes = new();
        private readonly Dictionary<uint, MilTarget> _targets = new();
        private readonly Dictionary<uint, Matrix3x2> _transforms = new();
        private readonly Dictionary<uint, uint> _visualTransform = new();   // visual handle -> transform handle
        private readonly Dictionary<uint, MilPen> _pens = new();
        private readonly Dictionary<uint, (double Offset, double[] Dashes)> _dashStyles = new();  // thickness-relative
        private readonly Dictionary<uint, Geometry> _geometries = new();
        private readonly Dictionary<uint, MilGradient> _gradients = new();
        private readonly Dictionary<uint, MilGlyphRun> _glyphRuns = new();
        private readonly Dictionary<uint, Effect> _effects = new();
        private readonly Dictionary<uint, MilBitmap> _bitmaps = new();
        private readonly Dictionary<uint, MilImageBrush> _imageBrushes = new();

        // DUCE resources are reference-counted: a single frozen resource (e.g. a theme
        // brush/pen shared by every CheckBox border) is AddRef'd once per referrer and
        // must survive until the LAST release. Tracking this is essential -- otherwise the
        // first release of a shared resource deletes it for all other referrers.
        private readonly Dictionary<uint, int> _refCounts = new();

        /// <summary>Decoded image pixels (straight RGBA, row-major), keyed by image-source handle.</summary>
        private readonly struct MilBitmap
        {
            public readonly byte[] Rgba;
            public readonly int Width, Height;
            public MilBitmap(byte[] rgba, int w, int h) { Rgba = rgba; Width = w; Height = h; }
        }

        private readonly struct MilImageBrush
        {
            public readonly uint ImageHandle;
            public readonly TileMode Tile;
            public readonly uint Stretch;        // WPF Stretch: None=0, Fill=1, Uniform=2, UniformToFill=3
            public readonly Rect Viewport;       // destination tile rect
            public readonly uint ViewportUnits;  // BrushMappingMode: Absolute=0, RelativeToBoundingBox=1
            public readonly Rect Viewbox;        // source sub-region
            public readonly uint ViewboxUnits;
            public MilImageBrush(uint imageHandle, TileMode tile, uint stretch,
                Rect viewport, uint viewportUnits, Rect viewbox, uint viewboxUnits)
            {
                ImageHandle = imageHandle; Tile = tile; Stretch = stretch;
                Viewport = viewport; ViewportUnits = viewportUnits; Viewbox = viewbox; ViewboxUnits = viewboxUnits;
            }
        }

        /// <summary>
        /// Install decoded image pixels for an image-source handle (called by the host after
        /// reading them from the native IWICBitmapSource; or directly in tests).
        /// </summary>
        public void SetBitmap(uint handle, byte[] rgba, int width, int height)
            => _bitmaps[handle] = new MilBitmap(rgba, width, height);
        private readonly Dictionary<uint, uint> _visualContent = new();      // visual handle -> render-data handle
        private readonly Dictionary<uint, byte[]> _parsedDataRef = new();     // visual handle -> render-data byte[] last parsed (ref-equality change check)
        private readonly HashSet<uint> _contentBrushConsumers = new();        // visual handles that paint a VisualBrush/DrawingBrush (must re-parse every frame)
        private bool _parseTouchedContentBrush;                               // set during ParseRenderData when a content-brush fill is resolved
        private readonly Dictionary<uint, uint> _visualOpacityMask = new();  // visual handle -> mask brush handle
        private uint _rootHandle;

        /// <summary>
        /// Resolves a native IDWriteFont pointer (from a glyph run) to a font that can
        /// produce glyph outlines. Injected by the host (DWrite-backed for a live WPF
        /// process; a fixed font in tests). When null, text is skipped.
        /// </summary>
        public Func<ulong, Text.IGlyphOutlineFont?>? FontResolver;

        // Rasterizes a SceneVisual subtree to straight-RGBA pixels (wired by the host to the GPU
        // renderer). Used to turn a VisualBrush's visual / a DrawingBrush's drawing into a tileable
        // bitmap, which then flows through the existing ImageBrush tiling/viewbox/stretch path.
        public Func<SceneVisual, int, int, byte[]?>? VisualRasterizer;

        private readonly Dictionary<uint, (uint Source, bool IsDrawing)> _contentBrushes = new();  // Visual/DrawingBrush -> source
        private readonly Dictionary<uint, (uint Brush, uint Pen, uint Geometry)> _geometryDrawings = new();
        private readonly Dictionary<uint, (List<uint> Children, uint Transform, double Opacity)> _drawingGroups = new();

        /// <summary>A decoded WPF glyph run: already-shaped glyph indices + advances.</summary>
        private sealed class MilGlyphRun
        {
            public ulong FontPtr;
            public Vector2 Origin;      // baseline origin
            public float EmSize;
            public Rect Bounds;
            public ushort[] Indices = Array.Empty<ushort>();
            public float[] Advances = Array.Empty<float>();
            public float[]? Offsets;    // 2 per glyph (x,y), or null
        }

        /// <summary>A decoded WPF gradient brush (linear or radial), pre-bounds-mapping.</summary>
        private sealed class MilGradient
        {
            public bool Radial;
            public Vector2 Start;      // linear start / radial centre
            public Vector2 End;        // linear end
            public float RadiusX, RadiusY;
            public bool Relative;      // BrushMappingMode.RelativeToBoundingBox
            public GradientSpreadMethod Spread;
            public GradientStop[] Stops = Array.Empty<GradientStop>();
        }

        /// <summary>A decoded WPF Pen resource (stroke parameters + brush handle).</summary>
        private readonly struct MilPen
        {
            public readonly StrokeStyle Style;
            public readonly uint BrushHandle;
            public readonly uint DashHandle;   // hDashStyle; resolved lazily in ResolvePen
            public MilPen(StrokeStyle style, uint brushHandle, uint dashHandle)
            { Style = style; BrushHandle = brushHandle; DashHandle = dashHandle; }
        }

        // Accumulator for a variable-length command opened via BeginCommand and
        // completed with EndCommand (how the channel marshals render data).
        private readonly List<byte> _openCommand = new();
        private bool _commandOpen;

        // Coverage diagnostics: a histogram of every command/draw-record/resource seen,
        // so we can tell what a real WPF tree emits vs. what the decoder handles.
        private readonly Dictionary<string, int> _ops = new();
        private void Note(string op) => _ops[op] = _ops.TryGetValue(op, out int c) ? c + 1 : 1;

        /// <summary>Resource-table counts (diagnostics).</summary>
        public string DumpState() =>
            $"state: visuals={_visuals.Count} renderData={_renderData.Count} solidBrushes={_solidBrushes.Count} " +
            $"transforms={_transforms.Count} geometries={_geometries.Count} pens={_pens.Count} " +
            $"visualContent={_visualContent.Count} root=0x{_rootHandle:x}";

        /// <summary>A formatted histogram of all commands/records/resources observed.</summary>
        public string DumpOps()
        {
            var sb = new StringBuilder("MilcoreEngine op histogram:");
            foreach (KeyValuePair<string, int> kv in _ops.OrderBy(k => k.Key))
                sb.Append($"\n  {kv.Key} x{kv.Value}");
            return sb.ToString();
        }

        /// <summary>The current composition root, or null if none has been set.</summary>
        public SceneVisual? Root =>
            _visuals.TryGetValue(_rootHandle, out SceneVisual? v) ? v : null;

        /// <summary>Composition targets (HwndTargets) keyed by resource handle.</summary>
        public IReadOnlyDictionary<uint, MilTarget> Targets => _targets;

        /// <summary>Look up a visual by handle (e.g. a target's root).</summary>
        public SceneVisual? VisualByHandle(uint handle) =>
            _visuals.TryGetValue(handle, out SceneVisual? v) ? v : null;

        /// <summary>
        /// Models DUCE.Channel.CreateOrAddRefOnChannel: allocate the slave resource for
        /// <paramref name="handle"/> if it does not exist yet.
        /// </summary>
        public void CreateOrAddRef(uint handle, MilResourceTypeId type)
        {
            Note($"res:{(uint)type}");
            _refCounts[handle] = _refCounts.TryGetValue(handle, out int rc) ? rc + 1 : 1;
            switch (type)
            {
                case MilResourceTypeId.Visual:
                case MilResourceTypeId.Viewport3DVisual:   // a 2D node hosting a 3D scene
                    if (!_visuals.ContainsKey(handle)) _visuals[handle] = new SceneVisual();
                    break;
                case MilResourceTypeId.RenderData:
                    if (!_renderData.ContainsKey(handle)) _renderData[handle] = Array.Empty<byte>();
                    break;
                case MilResourceTypeId.SolidColorBrush:
                    if (!_solidBrushes.ContainsKey(handle))
                        _solidBrushes[handle] = new RgbaColor(0, 0, 0, 0);
                    break;
                // Other resource types are not modelled yet; their commands are skipped.
            }
        }

        /// <summary>Models DUCE.Channel.ReleaseOnChannel: decrement the refcount and only
        /// destroy the resource when the last reference goes away.</summary>
        public void Release(uint handle)
        {
            if (_refCounts.TryGetValue(handle, out int rc) && rc > 1)
            {
                _refCounts[handle] = rc - 1;   // still referenced elsewhere; keep it alive
                return;
            }
            _refCounts.Remove(handle);
            _visuals.Remove(handle);
            _renderData.Remove(handle);
            _solidBrushes.Remove(handle);
            _transforms.Remove(handle);
            _visualTransform.Remove(handle);
            _pens.Remove(handle);
            _dashStyles.Remove(handle);
            _contentBrushes.Remove(handle);
            _geometryDrawings.Remove(handle);
            _drawingGroups.Remove(handle);
            _geometries.Remove(handle);
            _gradients.Remove(handle);
            _glyphRuns.Remove(handle);
            _effects.Remove(handle);
            _bitmaps.Remove(handle);
            _imageBrushes.Remove(handle);
        }

        /// <summary>
        /// Models DUCE.Channel.SendCommand / a coalesced BeginCommand+AppendCommandData+EndCommand:
        /// applies one MILCMD_* record (its trailing variable-length payload, if any, follows the
        /// fixed struct in the same buffer).
        /// </summary>
        public void SubmitCommand(byte[] command)
        {
            var r = new MilReader(command);
            var id = (Mil)r.U32();
            Note($"cmd:0x{(uint)id:x2}");
            switch (id)
            {
                case Mil.VisualSetOffset:
                {
                    SceneVisual v = Visual(r.U32());
                    v.Offset = new Vector2((float)r.F64(), (float)r.F64());
                    break;
                }
                case Mil.VisualSetAlpha:
                {
                    SceneVisual v = Visual(r.U32());
                    v.Opacity = r.F64();
                    break;
                }
                case Mil.VisualSetTransform:
                {
                    uint vh = r.U32();
                    uint th = r.U32();
                    _visualTransform[vh] = th;
                    if (_visuals.TryGetValue(vh, out SceneVisual? v))
                        v.Transform = (th != 0 && _transforms.TryGetValue(th, out Matrix3x2 m)) ? m : Matrix3x2.Identity;
                    break;
                }
                case Mil.MatrixTransform:
                {
                    // MILCMD_MATRIXTRANSFORM: Handle@4, MilMatrix3x2D Matrix@8 (S11,S12,S21,S22,DX,DY).
                    uint th = r.U32();
                    SetTransform(th, new Matrix3x2(
                        (float)r.F64(), (float)r.F64(), (float)r.F64(),
                        (float)r.F64(), (float)r.F64(), (float)r.F64()));
                    break;
                }
                case Mil.TranslateTransform:
                {
                    // Handle@4, X@8, Y@16.
                    uint th = r.U32();
                    SetTransform(th, Matrix3x2.CreateTranslation((float)r.F64(), (float)r.F64()));
                    break;
                }
                case Mil.ScaleTransform:
                {
                    // Handle@4, ScaleX@8, ScaleY@16, CenterX@24, CenterY@32.
                    uint th = r.U32();
                    float sx = (float)r.F64(), sy = (float)r.F64();
                    var center = new Vector2((float)r.F64(), (float)r.F64());
                    SetTransform(th, Matrix3x2.CreateScale(sx, sy, center));
                    break;
                }
                case Mil.RotateTransform:
                {
                    // Handle@4, Angle@8 (degrees), CenterX@16, CenterY@24.
                    uint th = r.U32();
                    float angle = (float)(r.F64() * Math.PI / 180.0);
                    var center = new Vector2((float)r.F64(), (float)r.F64());
                    SetTransform(th, Matrix3x2.CreateRotation(angle, center));
                    break;
                }
                case Mil.SkewTransform:
                {
                    // Handle@4, AngleX@8, AngleY@16, CenterX@24, CenterY@32.
                    uint th = r.U32();
                    float tax = (float)Math.Tan(r.F64() * Math.PI / 180.0);
                    float tay = (float)Math.Tan(r.F64() * Math.PI / 180.0);
                    var c = new Vector2((float)r.F64(), (float)r.F64());
                    var skew = new Matrix3x2(1, tay, tax, 1, 0, 0);
                    SetTransform(th, Matrix3x2.CreateTranslation(-c) * skew * Matrix3x2.CreateTranslation(c));
                    break;
                }
                case Mil.TransformGroup:
                {
                    // Handle@4, ChildrenSize@8, then ChildrenSize/4 child transform handles.
                    uint th = r.U32();
                    int childrenSize = (int)r.U32();
                    var m = Matrix3x2.Identity;
                    for (int i = 0; i < childrenSize / 4; i++)
                    {
                        uint child = r.U32();
                        if (_transforms.TryGetValue(child, out Matrix3x2 cm)) m *= cm;
                    }
                    SetTransform(th, m);
                    break;
                }
                case Mil.TargetSetClearColor:
                {
                    uint targetHandle = r.U32();
                    var c = new RgbaColor(r.F32(), r.F32(), r.F32(), r.F32());
                    if (_targets.TryGetValue(targetHandle, out MilTarget? t)) t.ClearColor = c;
                    break;
                }
                case Mil.GlyphRunCreate:
                {
                    // MILCMD_GLYPHRUN_CREATE (Pack=1, explicit offsets; gaps between fields):
                    // Handle@4, pIDWriteFont@8, GlyphRunFlags@16, Origin@20, MuSize@28,
                    // ManagedBounds@32, GlyphCount@64, then ushort[count] indices + float[count]
                    // advances + optional float[2*count] offsets (76-byte struct).
                    uint handle = r.U32();
                    r.Position = 8; ulong fontPtr = r.U64();
                    r.Position = 20; var origin = new Vector2(r.F32(), r.F32());
                    r.Position = 28; float emSize = r.F32();
                    r.Position = 32; var bounds = new Rect((float)r.F64(), (float)r.F64(), (float)r.F64(), (float)r.F64());
                    r.Position = 64; int count = r.U16();

                    r.Position = 76;
                    var indices = new ushort[count];
                    for (int i = 0; i < count; i++) indices[i] = r.U16();
                    var advances = new float[count];
                    for (int i = 0; i < count; i++) advances[i] = r.F32();
                    // Offsets are present iff the buffer carries the extra 8 bytes/glyph.
                    float[]? offsets = null;
                    if (command.Length - 76 >= count * 14)
                    {
                        offsets = new float[2 * count];
                        for (int i = 0; i < 2 * count; i++) offsets[i] = r.F32();
                    }
                    _glyphRuns[handle] = new MilGlyphRun
                    {
                        FontPtr = fontPtr, Origin = origin, EmSize = emSize, Bounds = bounds,
                        Indices = indices, Advances = advances, Offsets = offsets,
                    };
                    break;
                }
                case Mil.Pen:
                {
                    // MILCMD_PEN: Handle@4, Thickness@8, MiterLimit@16, hBrush@24, hThicknessAnim@28,
                    // StartLineCap@32, EndLineCap@36, DashCap@40, LineJoin@44, hDashStyle@48.
                    uint ph = r.U32();
                    double thickness = r.F64();
                    double miter = r.F64();
                    uint hBrush = r.U32();
                    _ = r.U32();                       // hThicknessAnimations
                    var cap = MapCap(r.U32());
                    _ = r.U32();                       // EndLineCap (use start for both)
                    _ = r.U32();                       // DashCap
                    var join = (LineJoin)Math.Min(r.U32(), 2u);
                    uint hDash = r.U32();              // hDashStyle@48
                    _pens[ph] = new MilPen(new StrokeStyle(thickness, cap, join, miter), hBrush, hDash);
                    break;
                }
                case Mil.DashStyle:
                {
                    // MILCMD_DASHSTYLE: Handle@4, Offset@8 (double), hOffsetAnim@16, DashesSize@20
                    // (bytes), then the dash doubles. Lengths are in pen-thickness multiples.
                    uint dh = r.U32();
                    double offset = r.F64();
                    _ = r.U32();                       // hOffsetAnimations
                    uint dashesSize = r.U32();
                    var dashes = new double[dashesSize / 8];
                    for (int i = 0; i < dashes.Length; i++) dashes[i] = r.F64();
                    _dashStyles[dh] = (offset, dashes);
                    break;
                }
                case Mil.RectangleGeometry:
                {
                    // Handle@4, RadiusX@8, RadiusY@16, Rect@24.
                    uint gh = r.U32();
                    double rx = r.F64(), ry = r.F64();
                    var rect = new Rect((float)r.F64(), (float)r.F64(), (float)r.F64(), (float)r.F64());
                    _geometries[gh] = (rx > 0 || ry > 0)
                        ? new RoundedRectangleGeometry(rect, (float)rx, (float)ry)
                        : new RectangleGeometry(rect);
                    break;
                }
                case Mil.EllipseGeometry:
                {
                    // Handle@4, RadiusX@8, RadiusY@16, Center@24.
                    uint gh = r.U32();
                    double rx = r.F64(), ry = r.F64();
                    var center = new Vector2((float)r.F64(), (float)r.F64());
                    _geometries[gh] = new EllipseGeometry(center, (float)rx, (float)ry);
                    break;
                }
                case Mil.LineGeometry:
                {
                    // Handle@4, StartPoint@8, EndPoint@24.
                    uint gh = r.U32();
                    var start = new Vector2((float)r.F64(), (float)r.F64());
                    var end = new Vector2((float)r.F64(), (float)r.F64());
                    _geometries[gh] = MakeLine(start, end);
                    break;
                }
                case Mil.PathGeometry:
                {
                    // MILCMD_PATHGEOMETRY: Handle@4, hTransform@8, FillRule@12, FiguresSize@16,
                    // then the serialized path blob (MIL_PATHGEOMETRY + MIL_PATHFIGURE/segments).
                    uint gh = r.U32();
                    _ = r.U32();                       // hTransform
                    var fillRule = (FillRule)r.U32();
                    uint figuresSize = r.U32();
                    List<PathFigure> figures;
                    try { figures = ParsePathBlob(command, 20, figuresSize); }
                    catch { figures = new List<PathFigure>(); }
                    _geometries[gh] = new PathGeometry(fillRule, figures);
                    break;
                }
                case Mil.VisualSetContent:
                {
                    // Record the link only; the render data is parsed lazily in Realize() so
                    // brushes/pens/geometries updated after this command are resolved correctly.
                    uint vh = r.U32();
                    _visualContent[vh] = r.U32();
                    break;
                }
                case Mil.VisualInsertChildAt:
                {
                    SceneVisual parent = Visual(r.U32());
                    SceneVisual child = Visual(r.U32());
                    int index = (int)r.U32();
                    if (index < 0 || index > parent.Children.Count) index = parent.Children.Count;
                    parent.Children.Insert(index, child);
                    break;
                }
                case Mil.VisualRemoveChild:
                {
                    SceneVisual parent = Visual(r.U32());
                    if (_visuals.TryGetValue(r.U32(), out SceneVisual? child))
                        parent.Children.Remove(child);
                    break;
                }
                case Mil.VisualRemoveAllChildren:
                {
                    Visual(r.U32()).Children.Clear();
                    break;
                }
                case Mil.RenderData:
                {
                    uint handle = r.U32();
                    int cbData = (int)r.U32();
                    _renderData[handle] = r.Bytes(cbData);
                    break;
                }
                case Mil.SolidColorBrush:
                {
                    uint handle = r.U32();
                    double opacity = r.F64();
                    // MilColorF: r,g,b,a floats. WPF brush Opacity scales the alpha.
                    float cr = r.F32(), cg = r.F32(), cb = r.F32(), ca = r.F32();
                    _solidBrushes[handle] = new RgbaColor(cr, cg, cb, (float)(ca * opacity));
                    break;
                }
                case Mil.LinearGradientBrush:
                {
                    // Handle@4, Opacity@8, StartPoint@16, EndPoint@32, ...anim/transform...,
                    // ColorInterp@60, MappingMode@64, SpreadMethod@68, GradientStopsSize@72,
                    // ...anim@76,80 -> 84-byte struct, then MIL_GRADIENTSTOP[] blob.
                    uint h = r.U32();
                    double opacity = r.F64();
                    var start = new Vector2((float)r.F64(), (float)r.F64());
                    var end = new Vector2((float)r.F64(), (float)r.F64());
                    r.U32(); r.U32(); r.U32();          // hOpacityAnim, hTransform, hRelativeTransform
                    r.U32();                            // ColorInterpolationMode
                    uint mapping = r.U32();             // BrushMappingMode (1 = RelativeToBoundingBox)
                    var spread = (GradientSpreadMethod)Math.Min(r.U32(), 2u);
                    uint stopsSize = r.U32();
                    r.U32(); r.U32();                   // hStartPointAnim, hEndPointAnim
                    _gradients[h] = new MilGradient
                    {
                        Radial = false, Start = start, End = end,
                        Relative = mapping == 1, Spread = spread,
                        Stops = ReadGradientStops(ref r, stopsSize, opacity),
                    };
                    break;
                }
                case Mil.RadialGradientBrush:
                {
                    // Handle@4, Opacity@8, Center@16, RadiusX@32, RadiusY@40, GradientOrigin@48,
                    // ...anim/transform..., MappingMode@80, SpreadMethod@84, GradientStopsSize@88,
                    // ...anim -> 108-byte struct, then MIL_GRADIENTSTOP[] blob.
                    uint h = r.U32();
                    double opacity = r.F64();
                    var center = new Vector2((float)r.F64(), (float)r.F64());
                    float rx = (float)r.F64(), ry = (float)r.F64();
                    r.F64(); r.F64();                   // GradientOrigin (focus) -- not modelled
                    r.U32(); r.U32(); r.U32();          // hOpacityAnim, hTransform, hRelativeTransform
                    r.U32();                            // ColorInterpolationMode
                    uint mapping = r.U32();
                    var spread = (GradientSpreadMethod)Math.Min(r.U32(), 2u);
                    uint stopsSize = r.U32();
                    r.U32(); r.U32(); r.U32(); r.U32(); // hCenter/RadiusX/RadiusY/GradientOrigin anim
                    _gradients[h] = new MilGradient
                    {
                        Radial = true, Start = center, RadiusX = rx, RadiusY = ry,
                        Relative = mapping == 1, Spread = spread,
                        Stops = ReadGradientStops(ref r, stopsSize, opacity),
                    };
                    break;
                }
                case Mil.HwndTargetCreate:
                {
                    // MILCMD_HWNDTARGET_CREATE: Handle@4, hwnd(u64)@8, hSection@16,
                    // masterDevice@24, width(u32)@32, height(u32)@36, clearColor(MilColorF)@40.
                    uint handle = r.U32();
                    ulong hwnd = r.U64();
                    _ = r.U64();           // hSection
                    _ = r.U64();           // masterDevice
                    uint w = r.U32();
                    uint h = r.U32();
                    float cr = r.F32(), cg = r.F32(), cb = r.F32(), ca = r.F32();
                    if (!_targets.TryGetValue(handle, out MilTarget? t))
                        _targets[handle] = t = new MilTarget();
                    t.Hwnd = hwnd;
                    t.Width = (int)w;
                    t.Height = (int)h;
                    t.ClearColor = new RgbaColor(cr, cg, cb, ca);
                    break;
                }
                case Mil.TargetUpdateWindowSettings:
                {
                    // MILCMD_TARGET_UPDATEWINDOWSETTINGS: Handle@4, windowRect(RECT)@8,
                    // windowLayerType@24, transparencyMode@28, ... Popups are created at 1x1
                    // and get their real size here; transparencyMode!=0 means a layered window.
                    uint targetHandle = r.U32();
                    int left = (int)r.U32(), top = (int)r.U32(), right = (int)r.U32(), bottom = (int)r.U32();
                    _ = r.U32();                 // windowLayerType
                    uint transparency = r.U32(); // MILTransparencyFlags
                    if (_targets.TryGetValue(targetHandle, out MilTarget? t))
                    {
                        int w = right - left, h = bottom - top;
                        if (w > 0 && h > 0) { t.Width = w; t.Height = h; }
                        t.Transparency = transparency;
                    }
                    break;
                }
                case Mil.TargetSetRoot:
                {
                    uint targetHandle = r.U32();
                    uint hRoot = r.U32();
                    _rootHandle = hRoot;
                    if (_targets.TryGetValue(targetHandle, out MilTarget? t)) t.RootHandle = hRoot;
                    break;
                }
                case Mil.Viewport3DVisualSetCamera:
                case Mil.Viewport3DVisualSetViewport:
                case Mil.Viewport3DVisualSet3DChild:
                case Mil.Visual3DSetContent:
                case Mil.Visual3DSetTransform:
                case Mil.Visual3DRemoveAllChildren:
                case Mil.Visual3DRemoveChild:
                case Mil.Visual3DInsertChildAt:
                case Mil.AxisAngleRotation3D:
                case Mil.PerspectiveCamera:
                case Mil.Model3DGroup:
                case Mil.AmbientLight:
                case Mil.DirectionalLight:
                case Mil.GeometryModel3D:
                case Mil.MeshGeometry3D:
                case Mil.DiffuseMaterial:
                case Mil.Transform3DGroup:
                case Mil.TranslateTransform3D:
                case Mil.ScaleTransform3D:
                case Mil.RotateTransform3D:
                case Mil.MatrixTransform3D:
                    Decode3D(id, r);
                    break;
                case Mil.ImageBrush:
                {
                    // MILCMD_IMAGEBRUSH: Handle@4, Opacity@8, Viewport@16, Viewbox@48,
                    // ViewportUnits@108, ViewboxUnits@112, Stretch@124, TileMode@128, hImageSource@144.
                    uint h = r.U32();
                    r.Position = 16; var viewport = new Rect((float)r.F64(), (float)r.F64(), (float)r.F64(), (float)r.F64());
                    r.Position = 48; var viewbox = new Rect((float)r.F64(), (float)r.F64(), (float)r.F64(), (float)r.F64());
                    r.Position = 108; uint viewportUnits = r.U32();
                    r.Position = 112; uint viewboxUnits = r.U32();
                    r.Position = 124; uint stretch = r.U32();
                    r.Position = 128; var tile = (TileMode)r.U32();
                    r.Position = 144; uint hImg = r.U32();
                    _imageBrushes[h] = new MilImageBrush(hImg, tile, stretch, viewport, viewportUnits, viewbox, viewboxUnits);
                    break;
                }
                case Mil.VisualBrush:
                case Mil.DrawingBrush:
                {
                    // Same TileBrush layout as ImageBrush; the source (Visual or Drawing) is at @144.
                    // We rasterize that source to a bitmap in Realize, then reuse the ImageBrush path.
                    uint h = r.U32();
                    r.Position = 16; var viewport = new Rect((float)r.F64(), (float)r.F64(), (float)r.F64(), (float)r.F64());
                    r.Position = 48; var viewbox = new Rect((float)r.F64(), (float)r.F64(), (float)r.F64(), (float)r.F64());
                    r.Position = 108; uint viewportUnits = r.U32();
                    r.Position = 112; uint viewboxUnits = r.U32();
                    r.Position = 124; uint stretch = r.U32();
                    r.Position = 128; var tile = (TileMode)r.U32();
                    r.Position = 144; uint hSource = r.U32();
                    _imageBrushes[h] = new MilImageBrush(hSource, tile, stretch, viewport, viewportUnits, viewbox, viewboxUnits);
                    _contentBrushes[h] = (hSource, id == Mil.DrawingBrush);
                    break;
                }
                case Mil.GeometryDrawing:
                {
                    // MILCMD_GEOMETRYDRAWING: Handle@4, hBrush@8, hPen@12, hGeometry@16.
                    uint h = r.U32();
                    uint hBrush = r.U32(), hPen = r.U32(), hGeom = r.U32();
                    _geometryDrawings[h] = (hBrush, hPen, hGeom);
                    break;
                }
                case Mil.DrawingGroup:
                {
                    // MILCMD_DRAWINGGROUP: Handle@4, Opacity@8, ChildrenSize@16, hTransform@32,
                    // ... fixed struct ends at ClearTypeHint@48 (52 bytes); child handles follow.
                    uint h = r.U32();
                    double opacity = r.F64();
                    r.Position = 16; uint childrenSize = r.U32();
                    r.Position = 32; uint hTransform = r.U32();
                    r.Position = 52;
                    var children = new List<uint>();
                    for (uint i = 0; i + 4 <= childrenSize && r.Remaining >= 4; i += 4) children.Add(r.U32());
                    _drawingGroups[h] = (children, hTransform, opacity);
                    break;
                }
                case Mil.BlurEffect:
                {
                    // MILCMD_BLUREFFECT: Handle@4, Radius@8 (double).
                    uint h = r.U32();
                    _effects[h] = new BlurEffect(r.F64());
                    break;
                }
                case Mil.DropShadowEffect:
                {
                    // MILCMD_DROPSHADOWEFFECT: Handle@4, ShadowDepth@8, Color@16 (MilColorF),
                    // Direction@32 (degrees), Opacity@40, BlurRadius@48.
                    uint h = r.U32();
                    double depth = r.F64();
                    float cr = r.F32(), cg = r.F32(), cb = r.F32(), ca = r.F32();
                    double direction = r.F64();
                    double opacity = r.F64();
                    double blur = r.F64();
                    double rad = direction * Math.PI / 180.0;
                    double ox = depth * Math.Cos(rad);
                    double oy = -depth * Math.Sin(rad);   // screen y is down; WPF direction is CCW from +x
                    _effects[h] = new DropShadowEffect(new RgbaColor(cr, cg, cb, (float)(ca * opacity)), blur, ox, oy);
                    break;
                }
                case Mil.VisualSetEffect:
                {
                    // MILCMD_VISUAL_SETEFFECT: Handle@4, hEffect@8.
                    uint vh = r.U32();
                    uint hEffect = r.U32();
                    if (_visuals.TryGetValue(vh, out SceneVisual? v))
                        v.Effect = hEffect != 0 && _effects.TryGetValue(hEffect, out Effect? e) ? e : null;
                    break;
                }
                case Mil.VisualSetOpacityMask:
                {
                    // MILCMD_VISUAL_SETALPHAMASK: Handle@4, hAlphaMask@8 (a brush resource).
                    // Resolved lazily in Realize() once the content bounds are known.
                    uint vh = r.U32();
                    _visualOpacityMask[vh] = r.U32();
                    break;
                }
                case Mil.VisualSetClip:
                {
                    // MILCMD_VISUAL_SETCLIP: Handle@4, hClip@8 (a geometry resource).
                    uint vh = r.U32();
                    uint hClip = r.U32();
                    if (_visuals.TryGetValue(vh, out SceneVisual? v))
                    {
                        v.Clip = null;
                        v.ClipGeometry = null;
                        if (hClip != 0 && _geometries.TryGetValue(hClip, out Geometry? clip))
                        {
                            if (clip is RectangleGeometry rg) v.Clip = rg.Rect;          // fast axis-aligned scissor
                            else v.ClipGeometry = ToPathGeometry(clip);                  // arbitrary mask
                        }
                    }
                    break;
                }
            }
        }

        /// <summary>Convert any decoded geometry to a PathGeometry (for clip masks).</summary>
        private static PathGeometry ToPathGeometry(Geometry g)
        {
            switch (g)
            {
                case PathGeometry p:
                    return p;
                case RectangleGeometry r:
                    return RectPath(r.Rect);
                case PolygonGeometry poly:
                {
                    var fig = new PathFigure(poly.Points.Length > 0 ? poly.Points[0] : Vector2.Zero) { Closed = true };
                    for (int i = 1; i < poly.Points.Length; i++) fig.Segments.Add(new LineSegment(poly.Points[i]));
                    return new PathGeometry(FillRule.NonZero, new List<PathFigure> { fig });
                }
                case EllipseGeometry e:
                    return EllipsePath(e.Center, e.RadiusX, e.RadiusY);
                case RoundedRectangleGeometry rr:
                    return RoundedRectPath(rr.Rect, rr.RadiusX, rr.RadiusY);
                default:
                    return RectPath(GeometryBounds(g));
            }
        }

        private const float Kappa = 0.5522847498f;   // 4/3 * (sqrt(2) - 1): circle arc as a cubic Bézier

        private static PathGeometry RectPath(Rect r)
        {
            var f = new PathFigure(new Vector2(r.X, r.Y)) { Closed = true };
            f.Segments.Add(new LineSegment(new Vector2(r.X + r.Width, r.Y)));
            f.Segments.Add(new LineSegment(new Vector2(r.X + r.Width, r.Y + r.Height)));
            f.Segments.Add(new LineSegment(new Vector2(r.X, r.Y + r.Height)));
            return new PathGeometry(FillRule.NonZero, new List<PathFigure> { f });
        }

        private static PathGeometry EllipsePath(Vector2 c, float rx, float ry)
        {
            float ox = rx * Kappa, oy = ry * Kappa;
            var f = new PathFigure(new Vector2(c.X, c.Y - ry)) { Closed = true };
            f.Segments.Add(new CubicBezierSegment(new Vector2(c.X + ox, c.Y - ry), new Vector2(c.X + rx, c.Y - oy), new Vector2(c.X + rx, c.Y)));
            f.Segments.Add(new CubicBezierSegment(new Vector2(c.X + rx, c.Y + oy), new Vector2(c.X + ox, c.Y + ry), new Vector2(c.X, c.Y + ry)));
            f.Segments.Add(new CubicBezierSegment(new Vector2(c.X - ox, c.Y + ry), new Vector2(c.X - rx, c.Y + oy), new Vector2(c.X - rx, c.Y)));
            f.Segments.Add(new CubicBezierSegment(new Vector2(c.X - rx, c.Y - oy), new Vector2(c.X - ox, c.Y - ry), new Vector2(c.X, c.Y - ry)));
            return new PathGeometry(FillRule.NonZero, new List<PathFigure> { f });
        }

        private static PathGeometry RoundedRectPath(Rect rect, float rx, float ry)
        {
            rx = MathF.Min(rx, rect.Width / 2); ry = MathF.Min(ry, rect.Height / 2);
            float l = rect.X, t = rect.Y, rr = rect.X + rect.Width, b = rect.Y + rect.Height;
            float ox = rx * Kappa, oy = ry * Kappa;
            var f = new PathFigure(new Vector2(l + rx, t)) { Closed = true };
            f.Segments.Add(new LineSegment(new Vector2(rr - rx, t)));
            f.Segments.Add(new CubicBezierSegment(new Vector2(rr - rx + ox, t), new Vector2(rr, t + ry - oy), new Vector2(rr, t + ry)));
            f.Segments.Add(new LineSegment(new Vector2(rr, b - ry)));
            f.Segments.Add(new CubicBezierSegment(new Vector2(rr, b - ry + oy), new Vector2(rr - rx + ox, b), new Vector2(rr - rx, b)));
            f.Segments.Add(new LineSegment(new Vector2(l + rx, b)));
            f.Segments.Add(new CubicBezierSegment(new Vector2(l + rx - ox, b), new Vector2(l, b - ry + oy), new Vector2(l, b - ry)));
            f.Segments.Add(new LineSegment(new Vector2(l, t + ry)));
            f.Segments.Add(new CubicBezierSegment(new Vector2(l, t + ry - oy), new Vector2(l + rx - ox, t), new Vector2(l + rx, t)));
            return new PathGeometry(FillRule.NonZero, new List<PathFigure> { f });
        }

        /// <summary>
        /// Models DUCE.Channel.BeginCommand: opens a variable-length command. The
        /// channel sends the fixed struct here and the payload via AppendCommandData.
        /// </summary>
        public void BeginCommand(byte[] header)
        {
            _openCommand.Clear();
            _openCommand.AddRange(header);
            _commandOpen = true;
        }

        /// <summary>Models DUCE.Channel.AppendCommandData: appends to the open command.</summary>
        public void AppendCommandData(byte[] data)
        {
            if (_commandOpen) _openCommand.AddRange(data);
        }

        /// <summary>Models DUCE.Channel.EndCommand: dispatches the accumulated command.</summary>
        public void EndCommand()
        {
            if (!_commandOpen) return;
            _commandOpen = false;
            SubmitCommand(_openCommand.ToArray());
        }

        /// <summary>
        /// Re-parse every visual's render data into its Content list using the current
        /// resource tables. Called before each frame so commands that arrive in any order
        /// (brush/pen/geometry updates after SetContent) are resolved. Idempotent.
        /// </summary>
        internal long PerfParseTicks, PerfBrushTicks;
        internal int PerfParsed;

        public void Realize()
        {
            long p0 = System.Diagnostics.Stopwatch.GetTimestamp();
            PerfParsed = 0;
            foreach (KeyValuePair<uint, uint> kv in _visualContent)
            {
                if (!_visuals.TryGetValue(kv.Key, out SceneVisual? v)) continue;
                if (kv.Value != 0 && _renderData.TryGetValue(kv.Value, out byte[]? data))
                {
                    // Skip re-parsing visuals whose render-data buffer is unchanged since last frame.
                    // SetContent replaces the byte[] wholesale, so reference-equality detects real
                    // changes; this avoids rebuilding the whole primitive/geometry/brush object graph
                    // for ~all (static) visuals every frame -- the dominant managed allocation / GC churn.
                    // EXCEPTION: visuals that paint a VisualBrush/DrawingBrush must re-parse every frame
                    // -- the brush bitmap is rasterized AFTER this parse loop (RealizeContentBrushes), so
                    // the fill only resolves on the *next* frame's parse; skipping them leaves them blank.
                    if (_parsedDataRef.TryGetValue(kv.Key, out byte[]? prev) && ReferenceEquals(prev, data)
                        && !_contentBrushConsumers.Contains(kv.Key))
                        continue;
                    v.Content.Clear();
                    _parseTouchedContentBrush = false;
                    ParseRenderData(data, v.Content);
                    if (_parseTouchedContentBrush) _contentBrushConsumers.Add(kv.Key); else _contentBrushConsumers.Remove(kv.Key);
                    _parsedDataRef[kv.Key] = data;
                    PerfParsed++;
                }
                else
                {
                    v.Content.Clear();
                    _parsedDataRef.Remove(kv.Key);
                    _contentBrushConsumers.Remove(kv.Key);
                }
            }
            PerfParseTicks = System.Diagnostics.Stopwatch.GetTimestamp() - p0;

            Realize3D();   // flatten any Viewport3D scene graphs into Viewport3DDraw content
            long b0 = System.Diagnostics.Stopwatch.GetTimestamp();
            RealizeContentBrushes();   // rasterize VisualBrush/DrawingBrush sources to bitmaps
            PerfBrushTicks = System.Diagnostics.Stopwatch.GetTimestamp() - b0;

            // Opacity masks resolve after content so a relative gradient maps to the bounds.
            foreach (KeyValuePair<uint, uint> kv in _visualOpacityMask)
            {
                if (!_visuals.TryGetValue(kv.Key, out SceneVisual? v)) continue;
                v.OpacityMask = kv.Value != 0 ? ResolveBrush(kv.Value, ContentBounds(v.Content)) : null;
            }
        }

        /// <summary>Union of the content primitives' geometry bounds (for opacity-mask mapping).</summary>
        private static Rect ContentBounds(List<DrawingPrimitive> content)
        {
            float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
            bool any = false;
            foreach (DrawingPrimitive p in content)
            {
                Geometry? g = p switch
                {
                    GeometryFill f => f.Geometry,
                    GeometryStroke s => s.Geometry,
                    GeometryDrawing d => d.Geometry,
                    _ => null,
                };
                if (g is null) continue;
                Rect b = GeometryBounds(g);
                minX = Math.Min(minX, b.X); minY = Math.Min(minY, b.Y);
                maxX = Math.Max(maxX, b.X + b.Width); maxY = Math.Max(maxY, b.Y + b.Height);
                any = true;
            }
            return any ? new Rect(minX, minY, maxX - minX, maxY - minY) : new Rect(0, 0, 0, 0);
        }

        // Rasterize each VisualBrush/DrawingBrush source into a bitmap so the existing ImageBrush
        // tiling/viewbox/stretch path can paint it. Re-run every frame so VisualBrushes stay live.
        private void RealizeContentBrushes()
        {
            if (VisualRasterizer is null || _contentBrushes.Count == 0) return;
            foreach (KeyValuePair<uint, (uint Source, bool IsDrawing)> kv in _contentBrushes)
            {
                SceneVisual? source = kv.Value.IsDrawing
                    ? BuildDrawingVisual(kv.Value.Source)
                    : (_visuals.TryGetValue(kv.Value.Source, out SceneVisual? v) ? v : null);
                if (source is null) continue;

                // Measure the source in its own LocalToParent space (so a live tree element's layout
                // offset / a DrawingGroup transform is included), then map THAT space onto the bitmap.
                // Rendering applies source.LocalToParent too, so the two stay consistent; measuring with
                // Identity instead left the source positioned outside the bitmap (empty brush).
                Rect b = VisualSubtreeBounds(source, source.LocalToParent);
                if (b.Width <= 0.01f || b.Height <= 0.01f) continue;

                const int supersample = 3;   // motif bitmap density; with bilinear sampling keeps tiled/scaled brushes crisp
                int pw = Math.Clamp((int)MathF.Ceiling(b.Width * supersample), 1, 1024);
                int ph = Math.Clamp((int)MathF.Ceiling(b.Height * supersample), 1, 1024);

                // Rasterizing a brush source costs a GPU render + a blocking readback (very expensive
                // on the GL backend). Skip it when the source content + size are unchanged from the
                // previous frame: the already-rasterized bitmap in _bitmaps is still valid.
                long hash = HashBrushSource(source) ^ ((long)pw << 21) ^ ph;
                if (_brushHash.TryGetValue(kv.Key, out long prev) && prev == hash && _bitmaps.ContainsKey(kv.Value.Source))
                    continue;

                // Map the source's content bounds onto the bitmap [0,pw]x[0,ph].
                var wrapper = new SceneVisual
                {
                    Transform = Matrix3x2.CreateTranslation(-b.X, -b.Y) * Matrix3x2.CreateScale(pw / b.Width, ph / b.Height),
                };
                wrapper.Children.Add(source);
                byte[]? px = VisualRasterizer(wrapper, pw, ph);
                if (px is not null) { _bitmaps[kv.Value.Source] = new MilBitmap(px, pw, ph); _brushHash[kv.Key] = hash; }
            }
        }

        private readonly Dictionary<uint, long> _brushHash = new();
        private long _bh;
        private void BMix(long x) => _bh = (_bh ^ x) * 1099511628211L;
        private void BMixF(float f) => BMix(BitConverter.SingleToInt32Bits(f));

        // Content hash of a brush source subtree (geometry/brush/transform) -> detects when a
        // VisualBrush/DrawingBrush actually changed and must be re-rasterized.
        private long HashBrushSource(SceneVisual root)
        {
            _bh = unchecked((long)1469598103934665603UL);
            HashBSrc(root, Matrix3x2.Identity);
            return _bh;
        }

        private void HashBSrc(SceneVisual n, Matrix3x2 w)
        {
            BMixF(w.M11); BMixF(w.M12); BMixF(w.M21); BMixF(w.M22); BMixF(w.M31); BMixF(w.M32);
            BMix(BitConverter.DoubleToInt64Bits(n.Opacity));
            foreach (DrawingPrimitive p in n.Content)
            {
                switch (p)
                {
                    case GeometryFill f: BMix(1); HashBGeo(f.Geometry); HashBBrush(f.Brush); break;
                    case GeometryStroke s: BMix(2); HashBGeo(s.Geometry); HashBBrush(s.Brush); BMixF((float)s.Style.Thickness); break;
                    case GeometryDrawing d: BMix(3); HashBGeo(d.Geometry); break;
                    case GlyphRunDraw g: BMix(4); BMix(g.Text.GetHashCode()); BMixF(g.Origin.X); BMixF(g.Origin.Y); BMixF(g.EmSize); break;
                    default: BMix(9); break;
                }
            }
            foreach (SceneVisual c in n.Children) HashBSrc(c, c.LocalToParent * w);
        }

        private void HashBGeo(Geometry g)
        {
            switch (g)
            {
                case RectangleGeometry r: BMix(21); BMixF(r.Rect.X); BMixF(r.Rect.Y); BMixF(r.Rect.Width); BMixF(r.Rect.Height); break;
                case RoundedRectangleGeometry rr: BMix(22); BMixF(rr.Rect.X); BMixF(rr.Rect.Y); BMixF(rr.Rect.Width); BMixF(rr.Rect.Height); BMixF(rr.RadiusX); break;
                case EllipseGeometry e: BMix(23); BMixF(e.Center.X); BMixF(e.Center.Y); BMixF(e.RadiusX); BMixF(e.RadiusY); break;
                case PathGeometry pg: BMix(24); foreach (PathFigure fig in pg.Figures) { BMixF(fig.Start.X); BMixF(fig.Start.Y); BMix(fig.Segments.Count); } break;
                case CombinedGeometry cg: BMix(25); HashBGeo(cg.Geometry1); HashBGeo(cg.Geometry2); break;
                default: BMix(20); break;
            }
        }

        private void HashBBrush(Brush b)
        {
            switch (b)
            {
                case SolidColorBrush s: BMix(11); BMixF(s.Color.R); BMixF(s.Color.G); BMixF(s.Color.B); BMixF(s.Color.A); break;
                case LinearGradientBrush lg: BMix(12); BMixF(lg.Start.X); BMixF(lg.End.X); foreach (GradientStop st in lg.Stops) { BMixF(st.Offset); BMixF(st.Color.R); BMixF(st.Color.G); BMixF(st.Color.B); } break;
                case RadialGradientBrush rg: BMix(13); BMixF(rg.RadiusX); foreach (GradientStop st in rg.Stops) { BMixF(st.Offset); BMixF(st.Color.R); BMixF(st.Color.G); BMixF(st.Color.B); } break;
                default: BMix(10); break;
            }
        }

        // Build a SceneVisual from a decoded Drawing resource (GeometryDrawing / DrawingGroup).
        private SceneVisual BuildDrawingVisual(uint handle)
        {
            var v = new SceneVisual();
            if (_geometryDrawings.TryGetValue(handle, out (uint Brush, uint Pen, uint Geometry) gd))
            {
                if (_geometries.TryGetValue(gd.Geometry, out Geometry? geom))
                    EmitDrawing(v.Content, geom, gd.Brush, gd.Pen, RenderState.Default);
            }
            else if (_drawingGroups.TryGetValue(handle, out (List<uint> Children, uint Transform, double Opacity) dg))
            {
                v.Opacity = dg.Opacity;
                if (dg.Transform != 0 && _transforms.TryGetValue(dg.Transform, out Matrix3x2 m)) v.Transform = m;
                foreach (uint child in dg.Children) v.Children.Add(BuildDrawingVisual(child));
            }
            return v;
        }

        // Bounds of a visual subtree in the root's local space (content + transformed children).
        private static Rect VisualSubtreeBounds(SceneVisual v, Matrix3x2 acc)
        {
            float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
            bool any = false;
            void Union(Rect r)
            {
                if (r.Width < 0 || r.Height < 0) return;
                minX = Math.Min(minX, r.X); minY = Math.Min(minY, r.Y);
                maxX = Math.Max(maxX, r.X + r.Width); maxY = Math.Max(maxY, r.Y + r.Height);
                any = true;
            }
            foreach (DrawingPrimitive p in v.Content)
            {
                Geometry? g = p switch
                {
                    GeometryFill f => f.Geometry,
                    GeometryStroke s => s.Geometry,
                    GeometryDrawing d => d.Geometry,
                    _ => null,
                };
                if (g is not null) Union(TransformRect(GeometryBounds(g), acc));
            }
            foreach (SceneVisual c in v.Children)
            {
                Rect cb = VisualSubtreeBounds(c, c.LocalToParent * acc);
                if (cb.Width > 0 && cb.Height > 0) Union(cb);
            }
            return any ? new Rect(minX, minY, maxX - minX, maxY - minY) : new Rect(0, 0, 0, 0);
        }

        private SceneVisual Visual(uint handle) => _visuals[handle];

        /// <summary>Store a transform resource and propagate it to any visual already linked to it.</summary>
        private void SetTransform(uint handle, Matrix3x2 m)
        {
            _transforms[handle] = m;
            foreach (KeyValuePair<uint, uint> kv in _visualTransform)
                if (kv.Value == handle && _visuals.TryGetValue(kv.Key, out SceneVisual? v))
                    v.Transform = m;
        }

        /// <summary>
        /// Parses a TYPE_RENDERDATA byte stream: a sequence of records framed by
        /// RecordHeader { int Size; MILCMD Id; } followed by a MILCMD_DRAW_* payload.
        /// </summary>
        private void ParseRenderData(byte[] data, List<DrawingPrimitive> output)
        {
            var r = new MilReader(data);
            // Render-data Push/Pop state: WPF brackets draw ops with PushClip/PushOpacity/
            // PushTransform ... Pop. We bake the active transform into emitted geometry,
            // intersect with the active clip, and fold opacity into the brush.
            var stack = new Stack<RenderState>();
            var state = RenderState.Default;

            while (r.Remaining >= 8)
            {
                int recordStart = r.Position;
                int size = (int)r.U32();        // total record size incl. 8-byte header
                var op = (Mil)r.U32();
                if (size < 8 || recordStart + size > data.Length) break;
                Note($"draw:0x{(uint)op:x2}");

                switch (op)
                {
                    case Mil.DrawRectangle:
                    {
                        // Rect@0, hBrush@32, hPen@36.
                        var rect = new Rect((float)r.F64(), (float)r.F64(), (float)r.F64(), (float)r.F64());
                        EmitDrawing(output, new RectangleGeometry(rect), r.U32(), r.U32(), state);
                        break;
                    }
                    case Mil.DrawRoundedRectangle:
                    {
                        // Rect@0, radiusX@32, radiusY@40, hBrush@48, hPen@52.
                        var rect = new Rect((float)r.F64(), (float)r.F64(), (float)r.F64(), (float)r.F64());
                        double rx = r.F64(), ry = r.F64();
                        Geometry g = (rx > 0 || ry > 0)
                            ? new RoundedRectangleGeometry(rect, (float)rx, (float)ry)
                            : new RectangleGeometry(rect);
                        EmitDrawing(output, g, r.U32(), r.U32(), state);
                        break;
                    }
                    case Mil.DrawEllipse:
                    {
                        // center@0, radiusX@16, radiusY@24, hBrush@32, hPen@36.
                        var center = new Vector2((float)r.F64(), (float)r.F64());
                        double rx = r.F64(), ry = r.F64();
                        EmitDrawing(output, new EllipseGeometry(center, (float)rx, (float)ry), r.U32(), r.U32(), state);
                        break;
                    }
                    case Mil.DrawLine:
                    {
                        // point0@0, point1@16, hPen@32 (stroke only).
                        var p0 = new Vector2((float)r.F64(), (float)r.F64());
                        var p1 = new Vector2((float)r.F64(), (float)r.F64());
                        EmitDrawing(output, MakeLine(p0, p1), 0, r.U32(), state);
                        break;
                    }
                    case Mil.DrawGeometry:
                    {
                        // hBrush@0, hPen@4, hGeometry@8.
                        uint hBrush = r.U32(), hPen = r.U32(), hGeom = r.U32();
                        if (_geometries.TryGetValue(hGeom, out Geometry? g))
                            EmitDrawing(output, g, hBrush, hPen, state);
                        break;
                    }
                    case Mil.DrawImage:
                    {
                        // MILCMD_DRAW_IMAGE: rectangle@0 (4 doubles), hImageSource@32.
                        var rect = new Rect((float)r.F64(), (float)r.F64(), (float)r.F64(), (float)r.F64());
                        uint hImg = r.U32();
                        if (_bitmaps.TryGetValue(hImg, out MilBitmap bmp))
                            EmitFill(output, new RectangleGeometry(rect), new ImageBrush(bmp.Rgba, bmp.Width, bmp.Height), state);
                        break;
                    }
                    case Mil.DrawGlyphRun:
                    {
                        // MILCMD_DRAW_GLYPH_RUN: hForegroundBrush@0, hGlyphRun@4.
                        uint hBrush = r.U32(), hRun = r.U32();
                        if (_glyphRuns.TryGetValue(hRun, out MilGlyphRun? run))
                            EmitGlyphRun(output, run, hBrush, state);
                        break;
                    }
                    case Mil.PushClip:
                    {
                        stack.Push(state);
                        uint hClip = r.U32();
                        if (_geometries.TryGetValue(hClip, out Geometry? clip))
                        {
                            Geometry clipLocal = TransformGeometry(clip, state.Transform);
                            state.Clip = state.Clip is null
                                ? clipLocal
                                : new CombinedGeometry(GeometryCombineMode.Intersect, state.Clip, clipLocal);
                        }
                        break;
                    }
                    case Mil.PushOpacity:
                        stack.Push(state);
                        state.Opacity *= (float)r.F64();
                        break;
                    case Mil.PushTransform:
                    {
                        stack.Push(state);
                        uint hTransform = r.U32();
                        if (_transforms.TryGetValue(hTransform, out Matrix3x2 m))
                            state.Transform = m * state.Transform;
                        break;
                    }
                    case Mil.Pop:
                        if (stack.Count > 0) state = stack.Pop();
                        break;
                    default:
                        // Other Push* records (opacity-mask/effect/guidelines, 0x4d..0x55) we do
                        // not model still bracket a Pop -- save state so the stack stays balanced.
                        if ((uint)op >= 0x4d && (uint)op <= 0x55) stack.Push(state);
                        break;
                }

                r.Position = recordStart + size;
            }
        }

        /// <summary>Active render-data push state (clip/opacity/transform), baked into draws.</summary>
        private struct RenderState
        {
            public float Opacity;
            public Geometry? Clip;        // in the visual's local space
            public Matrix3x2 Transform;   // current-space -> local space
            public static RenderState Default => new() { Opacity = 1f, Clip = null, Transform = Matrix3x2.Identity };
        }

        /// <summary>
        /// Emit a glyph run: resolve the font (via FontResolver), then lay out each
        /// already-shaped glyph as a filled outline at the run's baseline, advancing the
        /// pen by the supplied advance widths. Text thus reuses the standard AA fill path.
        /// </summary>
        private void EmitGlyphRun(List<DrawingPrimitive> output, MilGlyphRun run, uint hBrush, RenderState state)
        {
            Text.IGlyphOutlineFont? font = FontResolver?.Invoke(run.FontPtr);
            if (font is null) { Note("text:fontnull"); return; }
            if (run.Indices.Length == 0) { Note("text:noglyphs"); return; }

            Brush? brush = ApplyOpacity(ResolveBrush(hBrush, run.Bounds), state.Opacity);
            if (brush is null) { Note("text:brushnull"); return; }
            Note($"text:emit{run.Indices.Length}");

            float scale = run.EmSize / font.PixelsPerEm;
            float penX = run.Origin.X;
            for (int i = 0; i < run.Indices.Length; i++)
            {
                if (font.TryGetGlyphOutline(run.Indices[i], out List<PathFigure> figures) && figures.Count > 0)
                {
                    float gx = penX + (run.Offsets != null ? run.Offsets[2 * i] : 0f);
                    float gy = run.Origin.Y - (run.Offsets != null ? run.Offsets[2 * i + 1] : 0f);
                    Geometry glyph = new PathGeometry(FillRule.NonZero, ScaleFigures(figures, scale, gx, gy));
                    glyph = TransformGeometry(glyph, state.Transform);
                    if (state.Clip is not null)
                        glyph = new CombinedGeometry(GeometryCombineMode.Intersect, glyph, state.Clip);
                    output.Add(new GeometryFill(glyph, brush));
                }
                penX += i < run.Advances.Length ? run.Advances[i] : 0f;
            }
        }

        // Scale glyph-outline figures (in font px, baseline y=0) by s and translate to (tx, ty).
        private static List<PathFigure> ScaleFigures(List<PathFigure> figures, float s, float tx, float ty)
        {
            Vector2 M(Vector2 p) => new(p.X * s + tx, p.Y * s + ty);
            var outF = new List<PathFigure>(figures.Count);
            foreach (PathFigure f in figures)
            {
                var nf = new PathFigure(M(f.Start)) { Closed = f.Closed };
                foreach (PathSegment seg in f.Segments)
                    nf.Segments.Add(seg switch
                    {
                        LineSegment l => new LineSegment(M(l.Point)),
                        QuadraticBezierSegment q => new QuadraticBezierSegment(M(q.Control), M(q.Point)),
                        CubicBezierSegment c => new CubicBezierSegment(M(c.Control1), M(c.Control2), M(c.Point)),
                        _ => seg,
                    });
                outF.Add(nf);
            }
            return outF;
        }

        /// <summary>Emit a fill and/or stroke for a geometry, applying the push state.</summary>
        private void EmitDrawing(List<DrawingPrimitive> output, Geometry geometry, uint hBrush, uint hPen, RenderState state)
        {
            Geometry tg = TransformGeometry(geometry, state.Transform);
            Rect bounds = GeometryBounds(tg);                 // brush bounds = transformed geometry
            Geometry g = state.Clip is null
                ? tg
                : new CombinedGeometry(GeometryCombineMode.Intersect, tg, state.Clip);

            Brush? fill;
            // ImageBrush (Viewbox crop, Viewport tile, Stretch) is geometry-aware, so it is
            // resolved here rather than in ResolveBrush.
            if (TryImageBrushFill(hBrush, bounds, ref g, out Brush? imgFill))
                fill = ApplyOpacity(imgFill, state.Opacity);
            else
                fill = ApplyOpacity(ResolveBrush(hBrush, bounds), state.Opacity);

            (Brush? strokeBrush, StrokeStyle strokeStyle) = ResolvePen(hPen, bounds);
            strokeBrush = ApplyOpacity(strokeBrush, state.Opacity);
            if (fill != null || strokeBrush != null)
                output.Add(new GeometryDrawing(g, fill, strokeBrush, strokeStyle));
        }

        // Resolve an ImageBrush fill: source crop (Viewbox) -> destination tile (Viewport) ->
        // Stretch (None/Fill/Uniform/UniformToFill) -> TileMode. WPF Stretch: None=0,Fill=1,Uniform=2,UniformToFill=3.
        private bool TryImageBrushFill(uint hBrush, Rect bounds, ref Geometry g, out Brush? fill)
        {
            fill = null;
            if (_contentBrushes.ContainsKey(hBrush)) _parseTouchedContentBrush = true;
            if (!_imageBrushes.TryGetValue(hBrush, out MilImageBrush ib) ||
                !_bitmaps.TryGetValue(ib.ImageHandle, out MilBitmap bmp))
                return false;
            float bw = bounds.Width, bh = bounds.Height;
            if (bw <= 0 || bh <= 0 || bmp.Width <= 0 || bmp.Height <= 0) return false;

            // 1) Viewbox: select the source sub-region (relative units, non-default).
            (byte[] pixels, int iw, int ih) = ViewboxCrop(ib, bmp);

            // 2) Viewport: the destination tile rect (relative -> bounds; absolute -> as-is; default = bounds).
            Rect vp = ViewportRect(ib, bounds);
            float vw = vp.Width > 0 ? vp.Width : bw, vh = vp.Height > 0 ? vp.Height : bh;

            // 3) TileMode != None: tile the (cropped) image every Viewport cell across the shape.
            if (ib.Tile != TileMode.None)
            {
                fill = new ImageBrush(pixels, iw, ih, ib.Tile, vw, vh);
                return true;
            }

            // None tile: stretch the image into the viewport.
            if (ib.Stretch == 3)                                 // UniformToFill: cover, crop overflow
            {
                float scale = MathF.Max(vw / iw, vh / ih);
                int cw = Math.Clamp((int)MathF.Round(vw / scale), 1, iw);
                int ch = Math.Clamp((int)MathF.Round(vh / scale), 1, ih);
                int cx = Math.Clamp((iw - cw) / 2, 0, iw - cw);
                int cy = Math.Clamp((ih - ch) / 2, 0, ih - ch);
                if (vp.Width != bw || vp.Height != bh || vp.X != bounds.X || vp.Y != bounds.Y)
                    g = new CombinedGeometry(GeometryCombineMode.Intersect, g, new RectangleGeometry(vp));
                fill = new ImageBrush(CropRgba(pixels, iw, cx, cy, cw, ch), cw, ch, TileMode.None, vw, vh);
                return true;
            }

            float dw, dh;
            if (ib.Stretch == 1)                                 // Fill: stretch to the viewport
            {
                dw = vw; dh = vh;
            }
            else if (ib.Stretch == 2)                            // Uniform: fit, preserve aspect
            {
                float scale = MathF.Min(vw / iw, vh / ih);
                dw = iw * scale; dh = ih * scale;
            }
            else                                                 // None: native size
            {
                dw = MathF.Min(iw, vw); dh = MathF.Min(ih, vh);
            }
            var dest = new Rect(vp.X + (vw - dw) / 2, vp.Y + (vh - dh) / 2, dw, dh);

            // Clip to the painted sub-rect unless it already covers the whole bounds (plain Fill).
            if (dest.X != bounds.X || dest.Y != bounds.Y || dest.Width != bw || dest.Height != bh)
                g = new CombinedGeometry(GeometryCombineMode.Intersect, g, new RectangleGeometry(dest));
            fill = new ImageBrush(pixels, iw, ih, TileMode.None, dest.Width, dest.Height);
            return true;
        }

        private static (byte[], int, int) ViewboxCrop(MilImageBrush ib, MilBitmap bmp)
        {
            // RelativeToBoundingBox viewbox selects a [0,1] sub-region of the source image.
            if (ib.ViewboxUnits == 1 && !IsUnitRect(ib.Viewbox))
            {
                int iw = bmp.Width, ih = bmp.Height;
                int cx = Math.Clamp((int)MathF.Round(ib.Viewbox.X * iw), 0, iw - 1);
                int cy = Math.Clamp((int)MathF.Round(ib.Viewbox.Y * ih), 0, ih - 1);
                int cw = Math.Clamp((int)MathF.Round(ib.Viewbox.Width * iw), 1, iw - cx);
                int ch = Math.Clamp((int)MathF.Round(ib.Viewbox.Height * ih), 1, ih - cy);
                return (CropRgba(bmp.Rgba, iw, cx, cy, cw, ch), cw, ch);
            }
            return (bmp.Rgba, bmp.Width, bmp.Height);
        }

        private static Rect ViewportRect(MilImageBrush ib, Rect bounds)
        {
            if (ib.ViewportUnits == 1)   // RelativeToBoundingBox
                return new Rect(bounds.X + ib.Viewport.X * bounds.Width, bounds.Y + ib.Viewport.Y * bounds.Height,
                                ib.Viewport.Width * bounds.Width, ib.Viewport.Height * bounds.Height);
            return ib.Viewport;          // Absolute
        }

        private static bool IsUnitRect(Rect r) => r.X == 0 && r.Y == 0 && r.Width == 1 && r.Height == 1;

        private static byte[] CropRgba(byte[] src, int srcWidth, int x, int y, int w, int h)
        {
            var dst = new byte[w * h * 4];
            for (int row = 0; row < h; row++)
                Array.Copy(src, ((y + row) * srcWidth + x) * 4, dst, row * w * 4, w * 4);
            return dst;
        }

        /// <summary>Multiply a brush's alpha by <paramref name="opacity"/> (null/opaque passthrough).</summary>
        private static Brush? ApplyOpacity(Brush? brush, float opacity)
        {
            if (brush is null || opacity >= 0.999f) return brush;
            switch (brush)
            {
                case SolidColorBrush s:
                    return new SolidColorBrush(Fade(s.Color, opacity));
                case LinearGradientBrush l:
                    return new LinearGradientBrush(l.Start, l.End, FadeStops(l.Stops, opacity), l.SpreadMethod);
                case RadialGradientBrush rg:
                    return new RadialGradientBrush(rg.Center, rg.RadiusX, rg.RadiusY, FadeStops(rg.Stops, opacity), rg.SpreadMethod);
                default:
                    return brush;
            }
        }

        private static RgbaColor Fade(RgbaColor c, float o) => new(c.R, c.G, c.B, c.A * o);
        private static GradientStop[] FadeStops(GradientStop[] stops, float o)
        {
            var outS = new GradientStop[stops.Length];
            for (int i = 0; i < stops.Length; i++) outS[i] = new GradientStop(stops[i].Offset, Fade(stops[i].Color, o));
            return outS;
        }

        /// <summary>
        /// Transform a geometry into local space by <paramref name="m"/>. Axis-aligned
        /// transforms keep the geometry type; rotation/skew converts to a polygon/path.
        /// </summary>
        private static Geometry TransformGeometry(Geometry geom, Matrix3x2 m)
        {
            if (m.IsIdentity) return geom;
            bool axisAligned = m.M12 == 0 && m.M21 == 0;
            Vector2 T(Vector2 p) => Vector2.Transform(p, m);

            switch (geom)
            {
                case RectangleGeometry r when axisAligned:
                    return new RectangleGeometry(TransformRect(r.Rect, m));
                case RoundedRectangleGeometry rr when axisAligned:
                    return new RoundedRectangleGeometry(TransformRect(rr.Rect, m),
                        rr.RadiusX * MathF.Abs(m.M11), rr.RadiusY * MathF.Abs(m.M22));
                case EllipseGeometry e when axisAligned:
                    return new EllipseGeometry(T(e.Center), e.RadiusX * MathF.Abs(m.M11), e.RadiusY * MathF.Abs(m.M22));
                case RectangleGeometry r:
                    return new PolygonGeometry(new[]
                    {
                        T(new Vector2(r.Rect.X, r.Rect.Y)), T(new Vector2(r.Rect.X + r.Rect.Width, r.Rect.Y)),
                        T(new Vector2(r.Rect.X + r.Rect.Width, r.Rect.Y + r.Rect.Height)), T(new Vector2(r.Rect.X, r.Rect.Y + r.Rect.Height)),
                    });
                case PolygonGeometry p:
                {
                    var pts = new Vector2[p.Points.Length];
                    for (int i = 0; i < pts.Length; i++) pts[i] = T(p.Points[i]);
                    return new PolygonGeometry(pts);
                }
                case PathGeometry path:
                    return new PathGeometry(path.FillRule, TransformFigures(path.Figures, m));
                case GeometryGroup grp:
                    return new GeometryGroup(grp.FillRule, grp.Children.ConvertAll(c => TransformGeometry(c, m)));
                case CombinedGeometry cg:
                    return new CombinedGeometry(cg.Mode, TransformGeometry(cg.Geometry1, m), TransformGeometry(cg.Geometry2, m));
                default:
                    // Rounded/ellipse under rotation: sample the transformed bounding box (approx).
                    Rect b = GeometryBounds(geom);
                    return new PolygonGeometry(new[]
                    {
                        T(new Vector2(b.X, b.Y)), T(new Vector2(b.X + b.Width, b.Y)),
                        T(new Vector2(b.X + b.Width, b.Y + b.Height)), T(new Vector2(b.X, b.Y + b.Height)),
                    });
            }
        }

        private static Rect TransformRect(Rect r, Matrix3x2 m)
        {
            Vector2 a = Vector2.Transform(new Vector2(r.X, r.Y), m);
            Vector2 c = Vector2.Transform(new Vector2(r.X + r.Width, r.Y + r.Height), m);
            float x = MathF.Min(a.X, c.X), y = MathF.Min(a.Y, c.Y);
            return new Rect(x, y, MathF.Abs(c.X - a.X), MathF.Abs(c.Y - a.Y));
        }

        private static List<PathFigure> TransformFigures(List<PathFigure> figures, Matrix3x2 m)
        {
            Vector2 T(Vector2 p) => Vector2.Transform(p, m);
            var outF = new List<PathFigure>(figures.Count);
            foreach (PathFigure f in figures)
            {
                var nf = new PathFigure(T(f.Start)) { Closed = f.Closed };
                foreach (PathSegment seg in f.Segments)
                    nf.Segments.Add(seg switch
                    {
                        LineSegment l => new LineSegment(T(l.Point)),
                        QuadraticBezierSegment q => new QuadraticBezierSegment(T(q.Control), T(q.Point)),
                        CubicBezierSegment c => new CubicBezierSegment(T(c.Control1), T(c.Control2), T(c.Point)),
                        _ => seg,
                    });
                outF.Add(nf);
            }
            return outF;
        }

        /// <summary>Emit a fill with an already-resolved brush, applying the push state.</summary>
        private void EmitFill(List<DrawingPrimitive> output, Geometry geometry, Brush brush, RenderState state)
        {
            Geometry tg = TransformGeometry(geometry, state.Transform);
            Geometry g = state.Clip is null
                ? tg
                : new CombinedGeometry(GeometryCombineMode.Intersect, tg, state.Clip);
            output.Add(new GeometryFill(g, ApplyOpacity(brush, state.Opacity) ?? brush));
        }

        /// <summary>Resolve a brush handle to a Scene brush (solid, gradient, or image).</summary>
        private Brush? ResolveBrush(uint handle, Rect bounds)
        {
            if (handle == 0) return null;
            if (_contentBrushes.ContainsKey(handle)) _parseTouchedContentBrush = true;
            if (_solidBrushes.TryGetValue(handle, out RgbaColor c)) return new SolidColorBrush(c);
            if (_gradients.TryGetValue(handle, out MilGradient? g)) return BuildGradient(g, bounds);
            if (_imageBrushes.TryGetValue(handle, out MilImageBrush ib) && _bitmaps.TryGetValue(ib.ImageHandle, out MilBitmap bmp))
                return new ImageBrush(bmp.Rgba, bmp.Width, bmp.Height, ib.Tile, bounds.Width, bounds.Height);
            return null;
        }

        /// <summary>Resolve a pen handle to its stroke brush + style.</summary>
        private (Brush?, StrokeStyle) ResolvePen(uint handle, Rect bounds)
        {
            if (handle != 0 && _pens.TryGetValue(handle, out MilPen pen))
            {
                StrokeStyle style = pen.Style;
                // WPF dash lengths are multiples of the pen thickness; scale to absolute pixels.
                if (pen.DashHandle != 0 && _dashStyles.TryGetValue(pen.DashHandle, out (double Offset, double[] Dashes) ds) && ds.Dashes.Length > 0)
                {
                    double t = pen.Style.Thickness;
                    var scaled = new double[ds.Dashes.Length];
                    for (int i = 0; i < scaled.Length; i++) scaled[i] = ds.Dashes[i] * t;
                    style = new StrokeStyle(pen.Style.Thickness, pen.Style.Cap, pen.Style.Join, pen.Style.MiterLimit, scaled, ds.Offset * t);
                }
                return (ResolveBrush(pen.BrushHandle, bounds), style);
            }
            return (null, default);
        }

        // WPF gradients default to RelativeToBoundingBox: endpoints/radii are in [0,1] of the
        // filled geometry's bounds. Our renderer evaluates in absolute local space, so map here.
        private static Brush BuildGradient(MilGradient g, Rect b)
        {
            if (g.Radial)
            {
                Vector2 center = g.Relative ? MapToBounds(g.Start, b) : g.Start;
                float rx = g.Relative ? g.RadiusX * b.Width : g.RadiusX;
                float ry = g.Relative ? g.RadiusY * b.Height : g.RadiusY;
                return new RadialGradientBrush(center, rx, ry, g.Stops, g.Spread);
            }
            Vector2 start = g.Relative ? MapToBounds(g.Start, b) : g.Start;
            Vector2 end = g.Relative ? MapToBounds(g.End, b) : g.End;
            return new LinearGradientBrush(start, end, g.Stops, g.Spread);
        }

        private static Vector2 MapToBounds(Vector2 rel, Rect b)
            => new(b.X + rel.X * b.Width, b.Y + rel.Y * b.Height);

        private static GradientStop[] ReadGradientStops(ref MilReader r, uint sizeBytes, double opacity)
        {
            int count = (int)(sizeBytes / 24);   // MIL_GRADIENTSTOP = double Position + MilColorF (24 bytes)
            var stops = new GradientStop[count];
            for (int i = 0; i < count; i++)
            {
                float pos = (float)r.F64();
                float cr = r.F32(), cg = r.F32(), cb = r.F32(), ca = r.F32();
                stops[i] = new GradientStop(pos, new RgbaColor(cr, cg, cb, (float)(ca * opacity)));
            }
            return stops;
        }

        // Parse a serialized WPF path blob (MIL_PATHGEOMETRY header + MIL_PATHFIGURE figures
        // with MIL_SEGMENT_* records) into PathFigures. Structs use natural alignment.
        private static List<PathFigure> ParsePathBlob(byte[] data, int blobOffset, uint figuresSize)
        {
            var figures = new List<PathFigure>();
            if (figuresSize < 48 || blobOffset + figuresSize > data.Length) return figures;

            var r = new MilReader(data);
            r.Position = blobOffset + 40;          // MIL_PATHGEOMETRY: Size@0,Flags@4,Bounds@8(32),FigureCount@40
            uint figureCount = r.U32();
            int figPos = blobOffset + 48;          // figures start after the 48-byte header

            for (uint f = 0; f < figureCount && figPos + 40 <= blobOffset + figuresSize; f++)
            {
                r.Position = figPos;
                _ = r.U32();                       // BackSize
                uint figFlags = r.U32();           // Flags (IsClosed = 0x4)
                uint segCount = r.U32();           // Count (segments)
                uint figSize = r.U32();            // Size (total figure bytes)
                var start = new Vector2((float)r.F64(), (float)r.F64()); // StartPoint@16
                var figure = new PathFigure(start) { Closed = (figFlags & 0x4) != 0 };

                int segPos = figPos + 40;          // segments after the 40-byte figure header
                for (uint s = 0; s < segCount && segPos + 12 <= blobOffset + figuresSize; s++)
                    segPos += ParsePathSegment(r, data, segPos, figure);

                figures.Add(figure);
                figPos += figSize > 40 ? (int)figSize : segPos - figPos;
            }
            return figures;
        }

        // MIL_SEGMENT_*: Type@0, Flags@4, BackSize@8. Non-poly have ForcePacking@12 then points@16;
        // poly have Count@12 then points@16. Returns the segment's byte size.
        private static int ParsePathSegment(MilReader r, byte[] data, int segPos, PathFigure figure)
        {
            r.Position = segPos;
            uint type = r.U32();
            Vector2 P(int off) { r.Position = segPos + off; return new Vector2((float)r.F64(), (float)r.F64()); }
            switch (type)
            {
                case 1: // Line
                    figure.Segments.Add(new LineSegment(P(16)));
                    return 32;
                case 2: // Cubic Bezier
                    figure.Segments.Add(new CubicBezierSegment(P(16), P(32), P(48)));
                    return 64;
                case 3: // Quadratic Bezier
                    figure.Segments.Add(new QuadraticBezierSegment(P(16), P(32)));
                    return 48;
                case 4: // Arc -- not converted; approximate with a line to the endpoint.
                    figure.Segments.Add(new LineSegment(P(16)));
                    return 64;
                case 5: // PolyLine: Count@12, then Count points
                case 6: // PolyBezier: points in triples
                case 7: // PolyQuadraticBezier: points in pairs
                {
                    r.Position = segPos + 12;
                    int count = (int)r.U32();
                    var pts = new Vector2[count];
                    for (int i = 0; i < count; i++) pts[i] = new Vector2((float)r.F64(), (float)r.F64());
                    if (type == 5) foreach (Vector2 p in pts) figure.Segments.Add(new LineSegment(p));
                    else if (type == 6) for (int i = 0; i + 2 < count; i += 3)
                        figure.Segments.Add(new CubicBezierSegment(pts[i], pts[i + 1], pts[i + 2]));
                    else for (int i = 0; i + 1 < count; i += 2)
                        figure.Segments.Add(new QuadraticBezierSegment(pts[i], pts[i + 1]));
                    return 16 + count * 16;
                }
                default:
                    return 16; // unknown -- skip the header and hope to resync
            }
        }

        private static Rect GeometryBounds(Geometry g) => g switch
        {
            RectangleGeometry r => r.Rect,
            RoundedRectangleGeometry rr => rr.Rect,
            EllipseGeometry e => new Rect(e.Center.X - e.RadiusX, e.Center.Y - e.RadiusY, 2 * e.RadiusX, 2 * e.RadiusY),
            PolygonGeometry p => PointsBounds(p.Points),
            PathGeometry path => PathBounds(path),
            _ => new Rect(0, 0, 0, 0),
        };

        private static Rect PathBounds(PathGeometry path)
        {
            var pts = new List<Vector2>();
            foreach (PathFigure f in path.Figures)
            {
                pts.Add(f.Start);
                foreach (PathSegment s in f.Segments)
                    switch (s)
                    {
                        case LineSegment l: pts.Add(l.Point); break;
                        case QuadraticBezierSegment q: pts.Add(q.Control); pts.Add(q.Point); break;
                        case CubicBezierSegment c: pts.Add(c.Control1); pts.Add(c.Control2); pts.Add(c.Point); break;
                    }
            }
            return PointsBounds(pts);
        }

        private static Rect PointsBounds(IReadOnlyList<Vector2> pts)
        {
            if (pts.Count == 0) return new Rect(0, 0, 0, 0);
            float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
            foreach (Vector2 p in pts)
            {
                minX = Math.Min(minX, p.X); minY = Math.Min(minY, p.Y);
                maxX = Math.Max(maxX, p.X); maxY = Math.Max(maxY, p.Y);
            }
            return new Rect(minX, minY, maxX - minX, maxY - minY);
        }

        private static PathGeometry MakeLine(Vector2 start, Vector2 end)
        {
            var fig = new PathFigure(start) { Closed = false };
            fig.Segments.Add(new LineSegment(end));
            return new PathGeometry(FillRule.NonZero, new List<PathFigure> { fig });
        }

        // WPF PenLineCap {Flat=0, Square=1, Round=2, Triangle=3} -> Scene LineCap {Butt,Round,Square}.
        private static LineCap MapCap(uint wpfCap) => wpfCap switch
        {
            0 => LineCap.Butt,
            1 => LineCap.Square,
            2 => LineCap.Round,
            _ => LineCap.Square,
        };

        /// <summary>Little-endian reader over a milcore command/record buffer.</summary>
        private struct MilReader
        {
            private readonly byte[] _b;
            public int Position;

            public MilReader(byte[] b) { _b = b; Position = 0; }

            public int Remaining => _b.Length - Position;

            public uint U32()
            {
                uint v = BitConverter.ToUInt32(_b, Position);
                Position += 4;
                return v;
            }

            public ushort U16()
            {
                ushort v = BitConverter.ToUInt16(_b, Position);
                Position += 2;
                return v;
            }

            public ulong U64()
            {
                ulong v = BitConverter.ToUInt64(_b, Position);
                Position += 8;
                return v;
            }

            public float F32()
            {
                float v = BitConverter.ToSingle(_b, Position);
                Position += 4;
                return v;
            }

            public double F64()
            {
                double v = BitConverter.ToDouble(_b, Position);
                Position += 8;
                return v;
            }

            public byte[] Bytes(int count)
            {
                var slice = new byte[count];
                Array.Copy(_b, Position, slice, 0, count);
                Position += count;
                return slice;
            }
        }
    }
}
