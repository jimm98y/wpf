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
        MediaPlayer = 1,            // TYPE_MEDIAPLAYER (managed video backend, e.g. AVFoundation on macOS)
        Visual = 39,                // TYPE_VISUAL
        Viewport3DVisual = 40,      // TYPE_VIEWPORT3DVISUAL (2D node hosting a 3D scene)
        Visual3D = 41,              // TYPE_VISUAL3D
        RenderData = 43,            // TYPE_RENDERDATA
        SolidColorBrush = 75,       // TYPE_SOLIDCOLORBRUSH
    }

    /// <summary>MILCMD ids from src/Common/Graphics/wgx_core_types.cs.</summary>
    internal enum Mil : uint
    {
        // Composite geometry resources. Both are Pack=1 with the child handles/operands
        // packed straight after the 20-byte fixed struct (Generated/wgx_commands.cs).
        GuidelineSet = 0x8c,
        // DrawingContext.DrawDrawing -- how a DrawingGroup/GeometryDrawing tree gets into a
        // visual's render data (and what DrawingBrush content walks through).
        DrawDrawing = 0x4a,
        VideoDrawing = 0x8a,
        BitmapCacheBrush = 0x84,
        PushOpacityMask = 0x4e,
        // Animation VALUE resources. AnimationClockResource re-sends these every tick with the
        // clock's CurrentValue, which is how a render-data *Animate record's animated property
        // actually moves. (Distinct from Animatable's independent-animation handles, which
        // GetAnimationResourceHandle deliberately suppresses in favour of resolved values.)
        DoubleResource = 0x0e,
        ColorResource = 0x0f,
        PointResource = 0x10,
        RectResource = 0x11,
        SizeResource = 0x12,
        MatrixResource = 0x13,
        // Animated drawing records: the static payload plus handles to the resources above.
        DrawLineAnimate = 0x3f,
        DrawRectangleAnimate = 0x41,
        DrawRoundedRectangleAnimate = 0x43,
        DrawEllipseAnimate = 0x45,
        DrawImageAnimate = 0x48,
        DrawVideoAnimate = 0x4c,
        PushOpacityAnimate = 0x50,
        // Render-data guideline pushes. SimpleTextLine.Draw emits PushGuidelineY1 for EVERY text
        // line and LineServicesCallbacks emits PushGuidelineY2 for underlines, so these are among
        // the most frequent records in a text-heavy tree -- they were being skipped wholesale.
        PushGuidelineSet = 0x52,
        PushGuidelineY1 = 0x53,
        PushGuidelineY2 = 0x54,
        GeometryGroup = 0x7b,
        CombinedGeometry = 0x7c,
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
        QuaternionRotation3D = 0x58,
        PerspectiveCamera = 0x59,
        OrthographicCamera = 0x5a,
        MatrixCamera = 0x5b,
        Model3DGroup = 0x5c,
        AmbientLight = 0x5d,
        DirectionalLight = 0x5e,
        PointLight = 0x5f,
        SpotLight = 0x60,
        GeometryModel3D = 0x61,
        MeshGeometry3D = 0x62,
        MaterialGroup = 0x63,
        DiffuseMaterial = 0x64,
        SpecularMaterial = 0x65,
        EmissiveMaterial = 0x66,
        Transform3DGroup = 0x67,
        TranslateTransform3D = 0x68,
        ScaleTransform3D = 0x69,
        RotateTransform3D = 0x6a,
        MatrixTransform3D = 0x6b,
        HwndTargetCreate = 0x31,
        TargetUpdateWindowSettings = 0x33,
        GenericTargetCreate = 0x34,
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
        DrawVideo = 0x4b,
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
        ImplicitInputBrush = 0x6d,
        PixelShader = 0x6c,
        VisualSetCacheMode = 0x1e,
        VisualSetRenderOptions = 0x21,
        BitmapCache = 0x8d,
        VisualSetGuidelineCollection = 0x27,
        DrawingImage = 0x71,
        GlyphRunDrawing = 0x88,
        ImageDrawing = 0x89,
        BlurEffect = 0x6e,
        DropShadowEffect = 0x6f,
        ShaderEffect = 0x70,
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

        // A bitmap target (MILCMD_GENERICTARGET_CREATE, e.g. RenderTargetBitmap): never
        // presented to a window; rendered on demand via the sink's ReadbackTarget.
        public bool IsBitmap;
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
        // GuidelineSet resources (MILCMD_GUIDELINESET), in the guideline set's own coordinates.
        private readonly Dictionary<uint, (float[] X, float[] Y)> _guidelineSets = new();
        // Live animated values, refreshed every tick by the MilCmd*Resource commands. An animate
        // record carries BOTH its static value and a handle in here; the handle wins when present,
        // which is what makes the property move.
        private readonly Dictionary<uint, (Rect Rect, uint Player, uint RectAnim)> _videoDrawings = new();
        private readonly Dictionary<uint, double> _animDouble = new();
        private readonly Dictionary<uint, Vector2> _animPoint = new();
        private readonly Dictionary<uint, Rect> _animRect = new();
        private readonly Dictionary<uint, Vector2> _animSize = new();
        private readonly Dictionary<uint, RgbaColor> _animColor = new();
        private readonly Dictionary<uint, Matrix3x2> _animMatrix = new();

        // An animate record's value: the resource if the clock has published one, else the
        // static value packed alongside it.
        private double Anim(uint h, double staticValue)
            => h != 0 && _animDouble.TryGetValue(h, out double v) ? v : staticValue;
        private Vector2 Anim(uint h, Vector2 staticValue)
            => h != 0 && _animPoint.TryGetValue(h, out Vector2 v) ? v : staticValue;
        private Rect Anim(uint h, Rect staticValue)
            => h != 0 && _animRect.TryGetValue(h, out Rect v) ? v : staticValue;
        // Guidelines supplied per-VISUAL by MILCMD_VISUAL_SETGUIDELINECOLLECTION. Kept apart from
        // the ones a render-data pass discovers so that re-parsing content cannot drop them.
        private readonly Dictionary<uint, (float[]? X, float[]? Y)> _visualGuides = new();
        // Guidelines accumulated from Push* records during the parse currently running.
        private readonly List<float> _parseGuidesX = new();
        private readonly List<float> _parseGuidesY = new();
        private readonly Dictionary<uint, MilGradient> _gradients = new();
        private readonly Dictionary<uint, MilGlyphRun> _glyphRuns = new();

        // 'WFNT' (W,F,N,T as little-endian bytes) marks the managed font descriptor
        // trailer appended to a glyph run when the managed WebGPU backend is active.
        private const uint FontTrailerMagic = 0x544E4657;
        private readonly Dictionary<uint, Effect> _effects = new();
        private readonly Dictionary<uint, byte[]> _pixelShaders = new();
        private readonly Dictionary<uint, int> _shaderIds = new();
        private static int s_nextShaderId = 1;

        private static void SkipBytes(ref MilReader r, int count)
        {
            if (count > 0) r.Position += Math.Min(count, r.Remaining);
        }

        /// <summary>
        /// Realizes a ShaderEffect: translates its D3D9 bytecode to WGSL and packs the float
        /// registers densely from c0. Returns null when the shader is outside the translator's
        /// subset or uses register classes this renderer does not bind -- the visual then renders
        /// unmodified, which is what milcore does with a shader it cannot compile, and is far
        /// better than dropping the content.
        /// </summary>
        private Effect? BuildShaderEffect(uint hShader, int[] regIndices, float[] values, bool usesIntOrBool,
            int[] samplerRegs, uint[] samplerHandles)
        {
            if (usesIntOrBool) return null;
            if (!_pixelShaders.TryGetValue(hShader, out byte[]? code) || code.Length == 0) return null;
            if (!D3D9ShaderTranslator.TryTranslate(code, out TranslatedShader tr, out _)) return null;

            int maxReg = -1;
            foreach (int i in regIndices) if (i > maxReg) maxReg = i;
            int needed = Math.Max(tr.FloatRegisterCount, maxReg + 1);
            var packed = new float[needed * 4];
            for (int k = 0; k < regIndices.Length; k++)
            {
                int dst = regIndices[k] * 4;
                if (dst + 3 < packed.Length && k * 4 + 3 < values.Length)
                    Array.Copy(values, k * 4, packed, dst, 4);
            }

            // One id per distinct PixelShader resource: the module and pipeline are cached on it.
            if (!_shaderIds.TryGetValue(hShader, out int id))
            {
                id = s_nextShaderId++;
                _shaderIds[hShader] = id;
            }
            // Bind each sampler the shader reads: null means WPF's ImplicitInputBrush (the
            // effect's own input), anything else resolves to a real brush.
            var brushes = new Brush?[tr.Samplers.Count];
            for (int i = 0; i < tr.Samplers.Count; i++)
            {
                int reg = tr.Samplers[i];
                int slot = Array.IndexOf(samplerRegs, reg);
                if (slot < 0 || slot >= samplerHandles.Length) continue;      // unbound -> implicit
                uint hb = samplerHandles[slot];
                if (hb == 0 || _implicitInputBrushes.Contains(hb)) continue;  // implicit input
                brushes[i] = ResolveBrush(hb, default);
            }
            return new ShaderEffectDef(id, tr.Wgsl, packed, tr.Samplers.ToArray(), brushes);
        }
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
            public readonly double Opacity;      // TileBrush.Opacity (base or animated); 1.0 = opaque
            public MilImageBrush(uint imageHandle, TileMode tile, uint stretch,
                Rect viewport, uint viewportUnits, Rect viewbox, uint viewboxUnits, double opacity)
            {
                ImageHandle = imageHandle; Tile = tile; Stretch = stretch;
                Viewport = viewport; ViewportUnits = viewportUnits; Viewbox = viewbox; ViewboxUnits = viewboxUnits;
                Opacity = opacity;
            }
        }

        /// <summary>
        /// Install decoded image pixels for an image-source handle (called by the host after
        /// reading them from the native IWICBitmapSource; or directly in tests).
        /// </summary>
        public void SetBitmap(uint handle, byte[] rgba, int width, int height)
            => _bitmaps[handle] = new MilBitmap(rgba, width, height);

        // Current decoded video frame for a MediaPlayer (TYPE_MEDIAPLAYER) resource, keyed by its handle. A
        // fresh RGBA array each frame (see WpfCompositionSink.SendVideoFrame) makes the texture re-upload.
        public void SetVideoFrame(uint mediaHandle, byte[] rgba, int width, int height)
            => _videoFrames[mediaHandle] = new MilBitmap(rgba, width, height);
        private readonly Dictionary<uint, MilBitmap> _videoFrames = new();
        private readonly Dictionary<uint, uint> _visualContent = new();      // visual handle -> render-data handle
        private readonly Dictionary<uint, byte[]> _parsedDataRef = new();     // visual handle -> render-data byte[] last parsed (ref-equality change check)
        private readonly HashSet<uint> _contentBrushConsumers = new();        // visual handles that paint a VisualBrush/DrawingBrush (must re-parse every frame)
        private readonly List<uint> _reparseScratch = new();                  // snapshot of consumers to re-parse after brush rasterization
        private bool _parseTouchedContentBrush;                               // set during ParseRenderData when a content-brush fill is resolved
        private readonly Dictionary<uint, uint> _visualOpacityMask = new();  // visual handle -> mask brush handle
        private uint _rootHandle;

        /// <summary>
        /// Resolves a managed font descriptor (file path + face index + style
        /// simulations, carried by a glyph run) to a font that can produce glyph
        /// outlines. This is the cross-platform, COM-free path used by a live WPF
        /// process. When null or unresolved, the engine falls back to
        /// <see cref="FontResolver"/>. When both miss, text is skipped.
        /// </summary>
        public Func<Text.FontDescriptor, Text.IGlyphOutlineFont?>? ManagedFontResolver;

        /// <summary>
        /// Legacy resolver keyed by the run's raw font pointer. Retained as a test
        /// injection seam (tests supply a fixed font); the live path uses the
        /// platform-neutral <see cref="ManagedFontResolver"/> instead.
        /// </summary>
        public Func<ulong, Text.IGlyphOutlineFont?>? FontResolver;

        // Rasterizes a SceneVisual subtree to straight-RGBA pixels (wired by the host to the GPU
        // renderer). Used to turn a VisualBrush's visual / a DrawingBrush's drawing into a tileable
        // bitmap, which then flows through the existing ImageBrush tiling/viewbox/stretch path.
        public Func<SceneVisual, int, int, byte[]?>? VisualRasterizer;

        // Keyed variant (browser): receives the brush handle so an async rasterizer can
        // correlate a kicked-off readback with the retry on a later frame. Preferred over
        // VisualRasterizer when set.
        public Func<uint, SceneVisual, int, int, byte[]?>? VisualRasterizerKeyed;

        private readonly Dictionary<uint, (uint Source, bool IsDrawing)> _contentBrushes = new();  // Visual/DrawingBrush -> source
        private readonly HashSet<uint> _contentBrushes2D = new();   // content brushes a 2D primitive resolved via the CPU pixel path (needs readback; accumulated)
        private readonly HashSet<uint> _brushes3DLive = new();      // content brushes a 3D material renders live per frame
        private readonly HashSet<uint> _brushesGpuLive = new();     // content brushes a 2D fill samples LIVE from a GPU texture (no readback; accumulated)

        // Route VisualBrush/DrawingBrush plain-Fill 2D fills through a GPU texture rendered from the source
        // each frame (no CPU render+readback+per-tile upload). Set WPF_GPU_VISUALBRUSH=0 to fall back to the
        // CPU pixel path (readback). See TryBuildGpuSourceBrush.
        private static readonly bool s_gpuVisualBrush = System.Environment.GetEnvironmentVariable("WPF_GPU_VISUALBRUSH") != "0";
        private readonly Dictionary<uint, (uint Brush, uint Pen, uint Geometry)> _geometryDrawings = new();
        private readonly Dictionary<uint, (List<uint> Children, uint Transform, double Opacity)> _drawingGroups = new();
        // The other leaf Drawing types. Without these a DrawingGroup or DrawingBrush containing
        // an image or a glyph run silently rendered those children as nothing.
        private readonly Dictionary<uint, (Rect Rect, uint ImageSource)> _imageDrawings = new();
        private readonly Dictionary<uint, (uint GlyphRun, uint Brush)> _glyphRunDrawings = new();
        // DrawingImage: a Drawing used as an ImageSource (vector icon assets).
        private readonly Dictionary<uint, uint> _drawingImages = new();
        private readonly HashSet<uint> _bitmapCaches = new();
        private readonly HashSet<uint> _implicitInputBrushes = new();

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

            // Managed font descriptor (cross-platform, COM-free). Present when the
            // run carried the 'WFNT' trailer; null for legacy/test streams that
            // only supply FontPtr.
            public string? FontPath;
            public int FaceIndex;
            public int Simulations;     // WPF StyleSimulations: 1=Bold, 2=Italic
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
        /// <summary>Drops one reference. Returns true iff the resource LEFT the channel (its last
        /// reference was released) -- the caller (DUCE.Resource.ReleaseOnChannel) only then resets the
        /// resource's cached handle to Null. Returning true while the resource is still referenced by
        /// another owner would zero a live handle (e.g. a DiffuseMaterial shared by several
        /// GeometryModel3Ds and reassigned -> the still-referencing models see handle 0 -> render nothing).</summary>
        public bool Release(uint handle)
        {
            if (_refCounts.TryGetValue(handle, out int rc) && rc > 1)
            {
                _refCounts[handle] = rc - 1;   // still referenced elsewhere; keep it alive
                return false;
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
            _contentBrushes2D.Remove(handle);
            _brushes3DLive.Remove(handle);
            _brushesGpuLive.Remove(handle);
            _geometryDrawings.Remove(handle);
            _drawingGroups.Remove(handle);
            _imageDrawings.Remove(handle);
            _glyphRunDrawings.Remove(handle);
            _drawingImages.Remove(handle);
            _bitmapCaches.Remove(handle);
            _implicitInputBrushes.Remove(handle);
            _geometries.Remove(handle);
            _gradients.Remove(handle);
            _glyphRuns.Remove(handle);
            _effects.Remove(handle);
            _bitmaps.Remove(handle);
            _imageBrushes.Remove(handle);
            return true;   // last reference gone -> the resource left the channel
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
                    var children = new uint[childrenSize / 4];
                    for (int i = 0; i < children.Length; i++) children[i] = r.U32();
                    _transformGroupChildren[th] = children;
                    RecomposeGroup(th);   // compose from children + store/propagate
                    break;
                }
                case Mil.TargetSetClearColor:
                {
                    uint targetHandle = r.U32();
                    var c = EncCol(r.F32(), r.F32(), r.F32(), r.F32());
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
                    r.Position = 16; ushort glyphFlags = r.U16();
                    r.Position = 20; var origin = new Vector2(r.F32(), r.F32());
                    r.Position = 28; float emSize = r.F32();
                    r.Position = 32; var bounds = new Rect((float)r.F64(), (float)r.F64(), (float)r.F64(), (float)r.F64());
                    r.Position = 64; int count = r.U16();

                    r.Position = 76;
                    var indices = new ushort[count];
                    for (int i = 0; i < count; i++) indices[i] = r.U16();
                    var advances = new float[count];
                    for (int i = 0; i < count; i++) advances[i] = r.F32();
                    // Offsets present iff MilGlyphRun.HasOffsets (0x10) is set. (A length
                    // sniff is unreliable now that an optional font trailer can follow.)
                    float[]? offsets = null;
                    if ((glyphFlags & 0x10) != 0)
                    {
                        offsets = new float[2 * count];
                        for (int i = 0; i < 2 * count; i++) offsets[i] = r.F32();
                    }

                    // Optional managed font descriptor trailer ('WFNT'): faceIndex(i32),
                    // simulations(i32), pathLen(i32), UTF-8 path. Appended by WPF only
                    // when the managed WebGPU backend is active (see GlyphRun.cs); lets
                    // us resolve the font with no COM / DirectWrite.
                    string? fontPath = null; int faceIndex = 0, simulations = 0;
                    if (r.Remaining >= 16 && r.U32() == FontTrailerMagic)
                    {
                        faceIndex = (int)r.U32();
                        simulations = (int)r.U32();
                        int pathLen = (int)r.U32();
                        if (pathLen > 0 && pathLen <= r.Remaining)
                            fontPath = System.Text.Encoding.UTF8.GetString(r.Bytes(pathLen));
                    }

                    _glyphRuns[handle] = new MilGlyphRun
                    {
                        FontPtr = fontPtr, Origin = origin, EmSize = emSize, Bounds = bounds,
                        Indices = indices, Advances = advances, Offsets = offsets,
                        FontPath = fontPath, FaceIndex = faceIndex, Simulations = simulations,
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
                case Mil.DoubleResource:
                {
                    uint h = r.U32(); _animDouble[h] = r.F64(); break;
                }
                case Mil.ColorResource:
                {
                    uint h = r.U32();
                    _animColor[h] = new RgbaColor(r.F32(), r.F32(), r.F32(), r.F32());
                    break;
                }
                case Mil.PointResource:
                {
                    uint h = r.U32();
                    _animPoint[h] = new Vector2((float)r.F64(), (float)r.F64());
                    break;
                }
                case Mil.SizeResource:
                {
                    uint h = r.U32();
                    _animSize[h] = new Vector2((float)r.F64(), (float)r.F64());
                    break;
                }
                case Mil.RectResource:
                {
                    uint h = r.U32();
                    _animRect[h] = new Rect((float)r.F64(), (float)r.F64(), (float)r.F64(), (float)r.F64());
                    break;
                }
                case Mil.MatrixResource:
                {
                    // MilMatrix3x2D: six doubles, WPF's 3x2 affine order.
                    uint h = r.U32();
                    _animMatrix[h] = new Matrix3x2((float)r.F64(), (float)r.F64(), (float)r.F64(),
                                                   (float)r.F64(), (float)r.F64(), (float)r.F64());
                    break;
                }
                case Mil.VideoDrawing:
                {
                    // MILCMD_VIDEODRAWING: Handle@4, Rect@8, hPlayer@40, hRectAnimations@44.
                    // The Drawing form of a video -- what a DrawingBrush or DrawingImage wrapping
                    // a MediaElement resolves to, as opposed to the DrawVideo render-data record.
                    uint h = r.U32();
                    var rect = new Rect((float)r.F64(), (float)r.F64(), (float)r.F64(), (float)r.F64());
                    uint hPlayer = r.U32();
                    uint hRectAnim = r.U32();
                    _videoDrawings[h] = (rect, hPlayer, hRectAnim);
                    break;
                }
                case Mil.GuidelineSet:
                {
                    // MILCMD_GUIDELINESET: Handle@4, GuidelinesXSize@8, GuidelinesYSize@12 (both
                    // in BYTES), IsDynamic@16, then the X then Y arrays. These are DOUBLES --
                    // unlike MILCMD_VISUAL_SETGUIDELINECOLLECTION, which packs floats.
                    uint h = r.U32();
                    uint xBytes = r.U32(), yBytes = r.U32();
                    _ = r.U32();                                  // IsDynamic: no dynamic-guideline model yet
                    var gx = new List<float>();
                    var gy = new List<float>();
                    for (uint i = 0; i + 8 <= xBytes && r.Remaining >= 8; i += 8) gx.Add((float)r.F64());
                    for (uint i = 0; i + 8 <= yBytes && r.Remaining >= 8; i += 8) gy.Add((float)r.F64());
                    _guidelineSets[h] = (gx.ToArray(), gy.ToArray());
                    break;
                }
                case Mil.GeometryGroup:
                {
                    // MILCMD_GEOMETRYGROUP: Handle@4, hTransform@8, FillRule@12, ChildrenSize@16
                    // (bytes, not count), then that many child resource handles. Emitted by any
                    // XAML <GeometryGroup>, and by Path data that combines figures.
                    uint gh = r.U32();
                    uint hTransform = r.U32();
                    var fillRule = (FillRule)r.U32();
                    uint childrenBytes = r.U32();
                    var children = new List<Geometry>();
                    for (uint i = 0; i + 4 <= childrenBytes && r.Remaining >= 4; i += 4)
                        if (_geometries.TryGetValue(r.U32(), out Geometry? child)) children.Add(child);
                    _geometries[gh] = ApplyGeometryTransform(new GeometryGroup(fillRule, children), hTransform);
                    break;
                }
                case Mil.CombinedGeometry:
                {
                    // MILCMD_COMBINEDGEOMETRY: Handle@4, hTransform@8, GeometryCombineMode@12,
                    // hGeometry1@16, hGeometry2@20. A missing operand is not an empty region --
                    // Union/Xor with nothing is the other operand, so fall back to whichever
                    // resolved rather than dropping the shape.
                    uint gh = r.U32();
                    uint hTransform = r.U32();
                    var mode = (GeometryCombineMode)r.U32();
                    _geometries.TryGetValue(r.U32(), out Geometry? g1);
                    _geometries.TryGetValue(r.U32(), out Geometry? g2);
                    Geometry? combined = (g1, g2) switch
                    {
                        (not null, not null) => new CombinedGeometry(mode, g1, g2),
                        (not null, null) => g1,
                        (null, not null) => g2,
                        _ => null,
                    };
                    if (combined is not null) _geometries[gh] = ApplyGeometryTransform(combined, hTransform);
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
                    // A child occurs at most once under a parent: milcore's InsertChildAt has move/replace
                    // semantics, so WPF may re-insert an already-present child (to reposition or re-realize
                    // it) WITHOUT a preceding RemoveChild. Implementing this as a plain List.Insert then
                    // DUPLICATES the child every time — e.g. an InkCanvas grew +1 subtree visual per frame,
                    // which churned VisualBrush hashes and starved presents. Remove any existing occurrence
                    // first so a re-insert just repositions.
                    parent.Children.Remove(child);
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
                    _solidBrushes[handle] = EncCol(cr, cg, cb, (float)(ca * opacity));
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
                    t.ClearColor = EncCol(cr, cg, cb, ca);
                    break;
                }
                case Mil.GenericTargetCreate:
                {
                    // MILCMD_GENERICTARGET_CREATE: Handle@4, hwnd(u64)@8, pRenderTarget(u64)@16,
                    // width(u32)@24, height(u32)@28. A bitmap render target (RenderTargetBitmap):
                    // no window; rendered on demand via ReadbackTarget, never presented.
                    uint handle = r.U32();
                    _ = r.U64();           // hwnd (0 for bitmap targets)
                    _ = r.U64();           // pRenderTarget (native pointer; unused managed)
                    uint w = r.U32();
                    uint h = r.U32();
                    if (!_targets.TryGetValue(handle, out MilTarget? t))
                        _targets[handle] = t = new MilTarget();
                    t.IsBitmap = true;
                    t.Width = (int)w;
                    t.Height = (int)h;
                    t.ClearColor = new RgbaColor(0, 0, 0, 0);   // transparent, like MIL's bitmap RT
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
                    bool known = _targets.TryGetValue(targetHandle, out MilTarget? t);
                    if (known) t!.RootHandle = hRoot;
                    // A bitmap target (RenderTargetBitmap sync render) must not hijack the
                    // window's composition root -- its root lives only on the target itself.
                    if (!known || !t!.IsBitmap) _rootHandle = hRoot;
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
                case Mil.QuaternionRotation3D:
                case Mil.PerspectiveCamera:
                case Mil.OrthographicCamera:
                // Decode3D has handled MatrixCamera all along, but this dispatch never routed
                // 0x5b to it, so a <MatrixCamera> silently left the Viewport3D with no camera
                // and the whole 3D scene dropped.
                case Mil.MatrixCamera:
                case Mil.Model3DGroup:
                case Mil.AmbientLight:
                case Mil.DirectionalLight:
                case Mil.PointLight:
                case Mil.SpotLight:
                case Mil.GeometryModel3D:
                case Mil.MeshGeometry3D:
                case Mil.MaterialGroup:
                case Mil.DiffuseMaterial:
                case Mil.SpecularMaterial:
                case Mil.EmissiveMaterial:
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
                    r.Position = 8; double imgOpacity = r.F64();
                    r.Position = 16; var viewport = new Rect((float)r.F64(), (float)r.F64(), (float)r.F64(), (float)r.F64());
                    r.Position = 48; var viewbox = new Rect((float)r.F64(), (float)r.F64(), (float)r.F64(), (float)r.F64());
                    r.Position = 108; uint viewportUnits = r.U32();
                    r.Position = 112; uint viewboxUnits = r.U32();
                    r.Position = 124; uint stretch = r.U32();
                    r.Position = 128; var tile = (TileMode)r.U32();
                    r.Position = 144; uint hImg = r.U32();
                    _imageBrushes[h] = new MilImageBrush(hImg, tile, stretch, viewport, viewportUnits, viewbox, viewboxUnits, imgOpacity);
                    break;
                }
                case Mil.VisualBrush:
                case Mil.DrawingBrush:
                {
                    // Same TileBrush layout as ImageBrush; the source (Visual or Drawing) is at @144.
                    // We rasterize that source to a bitmap in Realize, then reuse the ImageBrush path.
                    uint h = r.U32();
                    r.Position = 8; double srcOpacity = r.F64();
                    r.Position = 16; var viewport = new Rect((float)r.F64(), (float)r.F64(), (float)r.F64(), (float)r.F64());
                    r.Position = 48; var viewbox = new Rect((float)r.F64(), (float)r.F64(), (float)r.F64(), (float)r.F64());
                    r.Position = 108; uint viewportUnits = r.U32();
                    r.Position = 112; uint viewboxUnits = r.U32();
                    r.Position = 124; uint stretch = r.U32();
                    r.Position = 128; var tile = (TileMode)r.U32();
                    r.Position = 144; uint hSource = r.U32();
                    _imageBrushes[h] = new MilImageBrush(hSource, tile, stretch, viewport, viewportUnits, viewbox, viewboxUnits, srcOpacity);
                    _contentBrushes[h] = (hSource, id == Mil.DrawingBrush);
                    break;
                }
                case Mil.BitmapCacheBrush:
                {
                    // MILCMD_BITMAPCACHEBRUSH: Handle@4, Opacity@8, hOpacityAnimations@16,
                    // hTransform@20, hRelativeTransform@24, hBitmapCache@28, hInternalTarget@32.
                    //
                    // Unlike the TileBrushes this is NOT a viewport/viewbox brush: a
                    // BitmapCacheBrush paints its cached target visual across the whole fill
                    // area. So it maps onto the same content-brush machinery with a
                    // bounding-box-relative unit viewport/viewbox and Stretch=Fill.
                    //
                    // hBitmapCache selects the cache's RenderAtScale/ClearType hints, which the
                    // renderer has nothing to attach to yet (see the BitmapCache arm) -- the
                    // target is rasterized at screen scale either way, so ignoring it costs
                    // resolution tuning, not correctness.
                    uint h = r.U32();
                    double cacheOpacity = r.F64();
                    r.Position = 32; uint hTarget = r.U32();
                    if (hTarget != 0)
                    {
                        var unit = new Rect(0f, 0f, 1f, 1f);
                        _imageBrushes[h] = new MilImageBrush(hTarget, TileMode.None, stretch: 1 /* Fill */,
                            unit, viewportUnits: 1 /* RelativeToBoundingBox */,
                            unit, viewboxUnits: 1, cacheOpacity);
                        _contentBrushes[h] = (hTarget, false);
                    }
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
                case Mil.ImageDrawing:
                {
                    // MILCMD_IMAGEDRAWING: Handle@4, Rect@8 (4 doubles), hImageSource@40.
                    uint h = r.U32();
                    var rect = new Rect((float)r.F64(), (float)r.F64(), (float)r.F64(), (float)r.F64());
                    uint hImg = r.U32();
                    _imageDrawings[h] = (rect, hImg);
                    break;
                }
                case Mil.GlyphRunDrawing:
                {
                    // MILCMD_GLYPHRUNDRAWING: Handle@4, hGlyphRun@8, hForegroundBrush@12.
                    uint h = r.U32();
                    uint hRun = r.U32(), hBrush = r.U32();
                    _glyphRunDrawings[h] = (hRun, hBrush);
                    break;
                }
                case Mil.DrawingImage:
                {
                    // MILCMD_DRAWINGIMAGE: Handle@4, hDrawing@8.
                    uint h = r.U32();
                    _drawingImages[h] = r.U32();
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
                case Mil.BitmapCache:
                {
                    // MILCMD_BITMAPCACHE: Handle@4, RenderAtScale@8, hRenderAtScaleAnimations@16,
                    // SnapsToDevicePixels@20, EnableClearType@24. Only the handle's existence is
                    // used today -- the subtree is cached at screen scale, so RenderAtScale and
                    // the ClearType hint have nothing to attach to yet.
                    uint h = r.U32();
                    _bitmapCaches.Add(h);
                    break;
                }
                case Mil.VisualSetCacheMode:
                {
                    // MILCMD_VISUAL_SETCACHEMODE: Handle@4, hCacheMode@8 (0 clears it).
                    uint vh = r.U32();
                    uint hCache = r.U32();
                    if (_visuals.TryGetValue(vh, out SceneVisual? cv))
                        cv.BitmapCached = hCache != 0 && _bitmapCaches.Contains(hCache);
                    break;
                }
                case Mil.VisualSetRenderOptions:
                {
                    // MILCMD_VISUAL_SETRENDEROPTIONS: Handle@4, then MilRenderOptions@8 --
                    // Flags, EdgeMode, CompositingMode, BitmapScalingMode, ClearTypeHint,
                    // TextRenderingMode, TextHintingMode (7 x u32). Flags is a bitmask saying
                    // which of the rest were actually set by the app.
                    uint vh = r.U32();
                    uint flags = r.U32();
                    uint edgeMode = r.U32();
                    // CompositingMode is read past, not honoured, and that is not an omission:
                    // WPF has no public RenderOptions.CompositingMode, and Visual.cs never sets
                    // the field or its flag bit (the MilRenderOptions it sends is default-
                    // initialised, so this is always 0 = SourceOver). The value exists in the
                    // wire struct for milcore's own internal compositing passes. It still has to
                    // be READ, because it sits between EdgeMode and BitmapScalingMode.
                    r.U32();
                    uint bitmapScaling = r.U32();
                    // ClearTypeHint / TextRenderingMode / TextHintingMode are ClearType and
                    // hinting hints; this renderer does grayscale AA text, so they are read
                    // past rather than honoured.
                    if (_visuals.TryGetValue(vh, out SceneVisual? rv))
                    {
                        const uint FlagBitmapScalingMode = 0x1, FlagEdgeMode = 0x2;
                        if ((flags & FlagEdgeMode) != 0)
                            rv.AliasedEdges = edgeMode == 1;              // EdgeMode.Aliased
                        if ((flags & FlagBitmapScalingMode) != 0)
                            rv.NearestBitmapScaling = bitmapScaling == 3; // NearestNeighbor
                    }
                    break;
                }
                case Mil.VisualSetGuidelineCollection:
                {
                    // MILCMD_VISUAL_SETGUIDELINECOLLECTION: Handle@4, countX@8 (u16),
                    // countY@12 (u16), then countX+countY FLOATS (not doubles), each axis
                    // already sorted ascending. Local-space coordinates.
                    uint vh = r.U32();
                    int cx = r.U16(); r.U16();              // countX @8, padding to @12
                    int cy = r.U16(); r.U16();              // countY @12, padding
                    var gx = new float[cx];
                    var gy = new float[cy];
                    for (int i = 0; i < cx && r.Remaining >= 4; i++) gx[i] = r.F32();
                    for (int i = 0; i < cy && r.Remaining >= 4; i++) gy[i] = r.F32();
                    _visualGuides[vh] = (cx > 0 ? gx : null, cy > 0 ? gy : null);
                    if (_visuals.TryGetValue(vh, out SceneVisual? gv))
                    {
                        gv.GuidelinesX = cx > 0 ? gx : null;
                        gv.GuidelinesY = cy > 0 ? gy : null;
                    }
                    break;
                }
                case Mil.ImplicitInputBrush:
                {
                    // MILCMD_IMPLICITINPUTBRUSH: a marker resource standing for "the content this
                    // effect is applied to". Only its identity matters; the subtree's own layer is
                    // what gets bound wherever it appears as a sampler source.
                    _implicitInputBrushes.Add(r.U32());
                    break;
                }
                case Mil.PixelShader:
                {
                    // MILCMD_PIXELSHADER: Handle@4, ShaderRenderMode@8, BytecodeSize@12,
                    // CompileSoftwareShader@16, then the D3D9 bytecode blob.
                    uint h = r.U32();
                    r.U32();                                  // ShaderRenderMode
                    int size = (int)r.U32();
                    r.U32();                                  // CompileSoftwareShader
                    _pixelShaders[h] = size > 0 && size <= r.Remaining ? r.Bytes(size) : Array.Empty<byte>();
                    break;
                }
                case Mil.ShaderEffect:
                {
                    // MILCMD_SHADEREFFECT: Handle@4, four doubles of padding@8..40,
                    // hPixelShader@40, DdxUvDdyUvRegisterIndex@44, then eight payload sizes,
                    // then the payloads in that order.
                    uint h = r.U32();
                    r.F64(); r.F64(); r.F64(); r.F64();        // top/bottom/left/right padding
                    uint hShader = r.U32();
                    r.U32();                                  // DdxUvDdyUvRegisterIndex
                    int floatRegBytes = (int)r.U32();
                    int floatValBytes = (int)r.U32();
                    int intRegBytes = (int)r.U32();
                    int intValBytes = (int)r.U32();
                    int boolRegBytes = (int)r.U32();
                    int boolValBytes = (int)r.U32();
                    int samplerInfoBytes = (int)r.U32();
                    int samplerValBytes = (int)r.U32();

                    // Float registers are Int16 indices; the values that follow are one
                    // float4 each, in the same order.
                    int floatCount = floatRegBytes / 2;
                    var regIndices = new int[floatCount];
                    for (int i = 0; i < floatCount; i++) regIndices[i] = r.U16();
                    var values = new float[floatCount * 4];
                    for (int i = 0; i < floatCount * 4 && floatValBytes >= 4; i++) values[i] = r.F32();
                    // Int and bool register classes are unsupported; skip to the sampler payload.
                    SkipBytes(ref r, intRegBytes + intValBytes + boolRegBytes + boolValBytes);

                    // 7) sampler registration info: (registerIndex, samplingMode) per sampler.
                    int samplerCount = samplerInfoBytes / 8;
                    var samplerRegs = new int[samplerCount];
                    for (int i = 0; i < samplerCount; i++) { samplerRegs[i] = (int)r.U32(); r.U32(); }
                    // 8) one brush handle per sampler, same order.
                    var samplerHandles = new uint[samplerValBytes / 4];
                    for (int i = 0; i < samplerHandles.Length; i++) samplerHandles[i] = r.U32();

                    _effects[h] = BuildShaderEffect(hShader, regIndices, values,
                        intRegBytes + boolRegBytes > 0, samplerRegs, samplerHandles);
                    break;
                }
                case Mil.BlurEffect:
                {
                    // MILCMD_BLUREFFECT: Handle@4, Radius@8 (double), hRadiusAnimations@16,
                    // KernelType@20, RenderingBias@24. KernelType used to be dropped on the
                    // floor here, so a Box blur rendered as a Gaussian one.
                    uint h = r.U32();
                    double blurRadius = r.F64();
                    // Guarded: this decodes an IPC byte stream, so a short or truncated
                    // command must degrade to the default rather than index past the buffer
                    // and take down the compositor.
                    uint kernel = 0;
                    if (r.Remaining >= 8)
                    {
                        r.U32();                                 // hRadiusAnimations
                        kernel = r.U32();                        // 0 = Gaussian, 1 = Box
                    }
                    // RenderingBias@24 (Performance/Quality) is a hint about kernel accuracy;
                    // this renderer always takes the accurate path, so it is not read.
                    _effects[h] = new BlurEffect(blurRadius,
                        kernel == 1 ? BlurKernelType.Box : BlurKernelType.Gaussian);
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
                    _effects[h] = new DropShadowEffect(EncCol(cr, cg, cb, (float)(ca * opacity)), blur, ox, oy);
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
        /// <summary>
        /// Bakes a geometry resource's own hTransform into its points. Geometry.Transform is a
        /// real WPF property (&lt;GeometryGroup Transform="..."&gt;) that composes UNDER the
        /// visual/render-data transform, and the Scene layer has no per-geometry transform slot,
        /// so it is applied here where the handle is still resolvable.
        /// </summary>
        private Geometry ApplyGeometryTransform(Geometry g, uint hTransform)
            => hTransform != 0 && _transforms.TryGetValue(hTransform, out Matrix3x2 m) && !m.IsIdentity
                ? TransformGeometry(g, m)
                : g;

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
                        && !_contentBrushConsumers.Contains(kv.Key) && !TransformDepsChanged(kv.Key))
                        continue;
                    v.Content.Clear();
                    _parseTouchedContentBrush = false;
                    _parseTransformRefs.Clear();
                    _parseGuidesX.Clear(); _parseGuidesY.Clear();
                    ParseRenderData(data, v.Content);
                    ApplyParsedGuides(kv.Key, v);
                    if (_parseTouchedContentBrush) _contentBrushConsumers.Add(kv.Key); else _contentBrushConsumers.Remove(kv.Key);
                    RecordTransformDeps(kv.Key);
                    _parsedDataRef[kv.Key] = data;
                    PerfParsed++;
                }
                else
                {
                    v.Content.Clear();
                    _parsedDataRef.Remove(kv.Key);
                    _contentBrushConsumers.Remove(kv.Key);
                    _visualTransformDeps.Remove(kv.Key);
                }
            }
            PerfParseTicks = System.Diagnostics.Stopwatch.GetTimestamp() - p0;

            // Rasterize VisualBrush/DrawingBrush sources to bitmaps FIRST, so a 3D material's
            // VisualBrush (2D-in-3D) can resolve its texture from _bitmaps in the same frame.
            long b0 = System.Diagnostics.Stopwatch.GetTimestamp();
            RealizeContentBrushes();
            PerfBrushTicks = System.Diagnostics.Stopwatch.GetTimestamp() - b0;

            // A content-brush consumer resolves its fill's bitmap at PARSE time (ResolveBrush reads
            // _bitmaps), but the source bitmaps were only just rasterized above -- AFTER the parse loop.
            // So on the frame a brush first appears, the consumer parsed with no bitmap and drew nothing.
            // Re-parse the consumers now that the bitmaps exist so the fill resolves THIS frame; otherwise
            // it stays blank until some later frame re-parses, and with no per-frame invalidation there may
            // be no later frame -- e.g. a VisualBrush drop-shadow that never appears until you click/resize.
            if (_contentBrushConsumers.Count > 0)
            {
                _reparseScratch.Clear();
                _reparseScratch.AddRange(_contentBrushConsumers);   // snapshot: ParseRenderData may mutate the set
                foreach (uint consumer in _reparseScratch)
                {
                    if (_visuals.TryGetValue(consumer, out SceneVisual? cv)
                        && _visualContent.TryGetValue(consumer, out uint cdh) && cdh != 0
                        && _renderData.TryGetValue(cdh, out byte[]? cdata))
                    {
                        cv.Content.Clear();
                        _parseTransformRefs.Clear();
                        _parseGuidesX.Clear(); _parseGuidesY.Clear();
                        ParseRenderData(cdata, cv.Content);
                        ApplyParsedGuides(consumer, cv);
                    }
                }
            }

            Realize3D();   // flatten any Viewport3D scene graphs into Viewport3DDraw content

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
            if ((VisualRasterizer is null && VisualRasterizerKeyed is null) || _contentBrushes.Count == 0) return;
            foreach (KeyValuePair<uint, (uint Source, bool IsDrawing)> kv in _contentBrushes)
            {
                // A brush consumed ONLY by a 3D material (ResolveTextureVisual) or ONLY by the GPU 2D
                // plain-Fill path (TryBuildGpuSourceBrush) renders live on the GPU each frame; rasterizing
                // it here (GPU render + BLOCKING readback, re-done every frame for animated content) would
                // be pure waste. Skip unless SOME consumer took the CPU pixel path (_contentBrushes2D),
                // which still needs the readback bitmap.
                if ((_brushes3DLive.Contains(kv.Key) || _brushesGpuLive.Contains(kv.Key)) && !_contentBrushes2D.Contains(kv.Key)) continue;

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
                // Key the raster cache by SOURCE, not consumer: a puzzle chops one animated visual (the
                // spinning cube) into N tiles, each a separate VisualBrush over the SAME source with a
                // different Viewbox (applied later at paint time). The bitmap (_bitmaps) is already keyed
                // by source and the hash/size are source-only, so all N tiles produce the identical bitmap
                // -- rasterize it ONCE per frame and let the other N-1 tiles skip, instead of N redundant
                // GPU-render + blocking-readback passes (16x cost on a 4x4 board).
                long hash = HashBrushSource(source) ^ ((long)pw << 21) ^ ph;
                if (_brushHash.TryGetValue(kv.Value.Source, out long prev) && prev == hash && _bitmaps.ContainsKey(kv.Value.Source))
                    continue;

                // Map the source's content bounds onto the bitmap [0,pw]x[0,ph].
                var wrapper = new SceneVisual
                {
                    Transform = Matrix3x2.CreateTranslation(-b.X, -b.Y) * Matrix3x2.CreateScale(pw / b.Width, ph / b.Height),
                };
                wrapper.Children.Add(source);
                byte[]? px = VisualRasterizerKeyed is not null
                    ? VisualRasterizerKeyed(kv.Key, wrapper, pw, ph)
                    : VisualRasterizer!(wrapper, pw, ph);
                if (px is not null) { _bitmaps[kv.Value.Source] = new MilBitmap(px, pw, ph); _brushHash[kv.Value.Source] = hash; }
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
                    // A Viewport3D (e.g. an animated spinning cube) painted into a VisualBrush must
                    // re-rasterize as its camera/model transforms animate; without hashing them the brush
                    // source looks unchanged (default:9 is constant) and the tile texture freezes until an
                    // unrelated invalidation (mouse-over) forces a re-raster. Mirror HashPrimitive's key.
                    case Viewport3DDraw v3:
                        BMix(5);
                        BMixF(v3.Camera.Position.X); BMixF(v3.Camera.Position.Y); BMixF(v3.Camera.Position.Z);
                        BMixF(v3.Camera.LookDirection.X); BMixF(v3.Camera.LookDirection.Y); BMixF(v3.Camera.LookDirection.Z);
                        foreach (Model3D m3 in v3.Models)
                        {
                            System.Numerics.Matrix4x4 t = m3.Transform;
                            BMixF(t.M11); BMixF(t.M12); BMixF(t.M13); BMixF(t.M21); BMixF(t.M22); BMixF(t.M23);
                            BMixF(t.M31); BMixF(t.M32); BMixF(t.M33); BMixF(t.M41); BMixF(t.M42); BMixF(t.M43);
                            BMixF(m3.DiffuseColor.R); BMixF(m3.DiffuseColor.G); BMixF(m3.DiffuseColor.B);
                        }
                        break;
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

        /// <summary>
        /// Test hook: the content-brush registration for a brush handle -- the source visual (or
        /// Drawing) it paints, and how it maps onto the fill. Lets a decode test assert what a
        /// brush command RESOLVED TO without standing up the live compositor's content-texture
        /// plumbing, which a hand-built tree does not reproduce.
        /// </summary>
        internal bool TryGetContentBrushForTest(uint handle, out uint source, out bool isDrawing,
            out uint stretch, out TileMode tile)
        {
            source = 0; isDrawing = false; stretch = 0; tile = TileMode.None;
            if (!_contentBrushes.TryGetValue(handle, out (uint Source, bool IsDrawing) cb)) return false;
            source = cb.Source; isDrawing = cb.IsDrawing;
            if (_imageBrushes.TryGetValue(handle, out MilImageBrush ib)) { stretch = ib.Stretch; tile = ib.Tile; }
            return true;
        }

        /// <summary>Test hook: materialize a decoded Drawing resource into a visual.</summary>
        internal SceneVisual BuildDrawingVisualForTest(uint handle) => BuildDrawingVisual(handle);

        // Build a SceneVisual from a decoded Drawing resource (GeometryDrawing / DrawingGroup).
        private SceneVisual BuildDrawingVisual(uint handle)
        {
            var v = new SceneVisual();
            if (_geometryDrawings.TryGetValue(handle, out (uint Brush, uint Pen, uint Geometry) gd))
            {
                if (_geometries.TryGetValue(gd.Geometry, out Geometry? geom))
                    EmitDrawing(v.Content, geom, gd.Brush, gd.Pen, RenderState.Default);
            }
            else if (_imageDrawings.TryGetValue(handle, out (Rect Rect, uint ImageSource) idr))
            {
                if (_bitmaps.TryGetValue(idr.ImageSource, out MilBitmap ibmp))
                    EmitFill(v.Content, new RectangleGeometry(idr.Rect),
                        new ImageBrush(ibmp.Rgba, ibmp.Width, ibmp.Height), RenderState.Default);
            }
            else if (_glyphRunDrawings.TryGetValue(handle, out (uint GlyphRun, uint Brush) grd))
            {
                if (_glyphRuns.TryGetValue(grd.GlyphRun, out MilGlyphRun? gr))
                    EmitGlyphRun(v.Content, gr, grd.Brush, RenderState.Default);
            }
            else if (_videoDrawings.TryGetValue(handle, out (Rect Rect, uint Player, uint RectAnim) vd))
            {
                if (_videoFrames.TryGetValue(vd.Player, out MilBitmap vframe))
                    EmitFill(v.Content, new RectangleGeometry(Anim(vd.RectAnim, vd.Rect)),
                        new ImageBrush(vframe.Rgba, vframe.Width, vframe.Height), RenderState.Default);
            }
            else if (_drawingImages.TryGetValue(handle, out uint hInner))
            {
                // A DrawingImage just wraps another Drawing; render that.
                return BuildDrawingVisual(hInner);
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
            _transformVersion[handle] = _transformVersion.GetValueOrDefault(handle) + 1;   // for content that bakes it via PushTransform
            foreach (KeyValuePair<uint, uint> kv in _visualTransform)
                if (kv.Value == handle && _visuals.TryGetValue(kv.Key, out SceneVisual? v))
                    v.Transform = m;
            // A TransformGroup composes its children once at decode; if a child animates, recompose any
            // group that contains it (recursively -> nested groups + visuals linked to the group). Without
            // this, an animated RotateTransform nested in a group (WPF wraps a centred rotate in one) is
            // frozen at the angle captured when the group was first composed.
            if (_transformGroupChildren.Count > 0)
                foreach (KeyValuePair<uint, uint[]> g in _transformGroupChildren)
                    if (g.Key != handle && Array.IndexOf(g.Value, handle) >= 0)
                        RecomposeGroup(g.Key);
        }

        private readonly Dictionary<uint, uint[]> _transformGroupChildren = new();   // group handle -> ordered child handles

        private void RecomposeGroup(uint groupHandle)
        {
            if (!_transformGroupChildren.TryGetValue(groupHandle, out uint[]? children)) return;
            var m = Matrix3x2.Identity;
            foreach (uint c in children)
                if (_transforms.TryGetValue(c, out Matrix3x2 cm)) m *= cm;
            SetTransform(groupHandle, m);
        }

        // Transform resources are baked into geometry at parse time (PushTransform). When such a
        // transform animates, the consuming visual's render-data byte[] is unchanged (it holds the
        // handle, not the matrix), so parse-skip must re-parse it. Track each visual's referenced
        // transform handles + their version at parse, and the live version bumped on every SetTransform.
        private readonly Dictionary<uint, int> _transformVersion = new();          // transform handle -> bump count
        private readonly List<uint> _parseTransformRefs = new();                   // handles PushTransform'd during the current parse
        private readonly Dictionary<uint, (uint Handle, int Ver)[]> _visualTransformDeps = new(); // visual -> deps snapshot

        // True if any transform the visual's content baked in (via PushTransform) changed since last parse.
        private bool TransformDepsChanged(uint visual)
        {
            if (!_visualTransformDeps.TryGetValue(visual, out (uint Handle, int Ver)[]? deps)) return false;
            foreach ((uint Handle, int Ver) d in deps)
                if (_transformVersion.GetValueOrDefault(d.Handle) != d.Ver) return true;
            return false;
        }

        // Snapshot the (distinct) transform handles this parse baked in, with their current version.
        private void RecordTransformDeps(uint visual)
        {
            if (_parseTransformRefs.Count == 0) { _visualTransformDeps.Remove(visual); return; }
            var distinct = new List<(uint, int)>();
            foreach (uint h in _parseTransformRefs)
            {
                bool seen = false;
                foreach ((uint H, int _) in distinct) if (H == h) { seen = true; break; }
                if (!seen) distinct.Add((h, _transformVersion.GetValueOrDefault(h)));
            }
            _visualTransformDeps[visual] = distinct.ToArray();
        }

        /// <summary>
        /// Parses a TYPE_RENDERDATA byte stream: a sequence of records framed by
        /// RecordHeader { int Size; MILCMD Id; } followed by a MILCMD_DRAW_* payload.
        /// </summary>
        /// <summary>
        /// Flattens a Drawing resource (GeometryDrawing / ImageDrawing / GlyphRunDrawing /
        /// DrawingImage / DrawingGroup) into a render-data output list under <paramref name="state"/>.
        /// The sibling of <see cref="BuildDrawingVisual"/>, which produces a standalone visual for
        /// brush content; here the drawing composes into the surrounding record stream instead, so
        /// group transform/opacity fold into the caller's state rather than onto a new visual.
        /// </summary>
        private void EmitDrawingResource(List<DrawingPrimitive> output, uint handle, RenderState state, int depth = 0)
        {
            // A DrawingGroup cannot contain itself through the public API, but the handles arrive
            // off a wire we do not control, so a cycle must cost a bounded amount of work.
            if (depth > 32) return;

            if (_geometryDrawings.TryGetValue(handle, out (uint Brush, uint Pen, uint Geometry) gd))
            {
                if (_geometries.TryGetValue(gd.Geometry, out Geometry? geom))
                    EmitDrawing(output, geom, gd.Brush, gd.Pen, state);
            }
            else if (_imageDrawings.TryGetValue(handle, out (Rect Rect, uint ImageSource) idr))
            {
                if (_bitmaps.TryGetValue(idr.ImageSource, out MilBitmap ibmp))
                    EmitFill(output, new RectangleGeometry(idr.Rect),
                        new ImageBrush(ibmp.Rgba, ibmp.Width, ibmp.Height), state);
            }
            else if (_glyphRunDrawings.TryGetValue(handle, out (uint GlyphRun, uint Brush) grd))
            {
                if (_glyphRuns.TryGetValue(grd.GlyphRun, out MilGlyphRun? gr))
                    EmitGlyphRun(output, gr, grd.Brush, state);
            }
            else if (_videoDrawings.TryGetValue(handle, out (Rect Rect, uint Player, uint RectAnim) vd))
            {
                if (_videoFrames.TryGetValue(vd.Player, out MilBitmap vframe))
                    EmitFill(output, new RectangleGeometry(Anim(vd.RectAnim, vd.Rect)),
                        new ImageBrush(vframe.Rgba, vframe.Width, vframe.Height), state);
                _parseTouchedContentBrush = true;   // frames change without the render data changing
            }
            else if (_drawingImages.TryGetValue(handle, out uint hInner))
            {
                EmitDrawingResource(output, hInner, state, depth + 1);
            }
            else if (_drawingGroups.TryGetValue(handle, out (List<uint> Children, uint Transform, double Opacity) dg))
            {
                RenderState child = state;
                child.Opacity *= (float)dg.Opacity;
                if (dg.Transform != 0 && _transforms.TryGetValue(dg.Transform, out Matrix3x2 m))
                    child.Transform = m * state.Transform;
                foreach (uint c in dg.Children) EmitDrawingResource(output, c, child, depth + 1);
            }
        }

        // Record a guideline in the VISUAL's local space. Guidelines are pushed in the coordinate
        // space in effect at the push, so any enclosing PushTransform has to be undone first;
        // guidelines are meaningless under rotation anyway (ResolveGuides bails on it), so the
        // axis-aligned scale/translate of the current transform is the whole of it.
        private static void AddParseGuide(List<float> into, float coord, float scale, float translate)
        {
            float local = coord * scale + translate;
            if (!float.IsFinite(local) || into.Contains(local)) return;
            // 64 is far past the point of usefulness (Nearest() is a linear scan) and stops a
            // pathological page of text from growing an unbounded list.
            if (into.Count < 64) into.Add(local);
        }

        // Union the visual's own guideline collection with whatever its render data pushed, and
        // hand the result to the visual. Called after every parse: the parse-side list is rebuilt
        // each time, and the collection-supplied one must survive that.
        private void ApplyParsedGuides(uint visualHandle, SceneVisual v)
        {
            _visualGuides.TryGetValue(visualHandle, out (float[]? X, float[]? Y) own);
            v.GuidelinesX = MergeGuides(own.X, _parseGuidesX);
            v.GuidelinesY = MergeGuides(own.Y, _parseGuidesY);
        }

        private static float[]? MergeGuides(float[]? own, List<float> parsed)
        {
            if (parsed.Count == 0) return own;
            var all = new List<float>(parsed);
            if (own is not null) foreach (float f in own) if (!all.Contains(f)) all.Add(f);
            all.Sort();                       // Guides.Build assumes nothing, but sorted keeps it readable
            return all.ToArray();
        }

        private void ParseRenderData(byte[] data, List<DrawingPrimitive> output)
        {
            var r = new MilReader(data);
            // Render-data Push/Pop state: WPF brackets draw ops with PushClip/PushOpacity/
            // PushTransform ... Pop. We bake the active transform into emitted geometry,
            // intersect with the active clip, and fold opacity into the brush.
            var stack = new Stack<RenderState>();
            var state = RenderState.Default;
            // Open PushOpacityMask scopes. Each remembers the stack depth of the push that opened
            // it, so the matching Pop is identified without every other push site having to
            // participate. Primitives are redirected into the scope's visual until then.
            var scopes = new Stack<(int Depth, List<DrawingPrimitive> Parent, SceneVisual Nested, uint MaskBrush)>();

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
                    // ---- animated draw records -------------------------------------------
                    // Each carries its static value AND a handle into the animation-value tables,
                    // which AnimationClockResource refreshes every tick. Reading the static value
                    // alone would pin the drawing at its base; ignoring the record (what happened
                    // before) dropped the drawing entirely.
                    case Mil.DrawRectangleAnimate:
                    {
                        // rectangle@0, hBrush@32, hPen@36, hRectangleAnimations@40.
                        var rect = new Rect((float)r.F64(), (float)r.F64(), (float)r.F64(), (float)r.F64());
                        uint hBrush = r.U32(), hPen = r.U32(), hAnim = r.U32();
                        EmitDrawing(output, new RectangleGeometry(Anim(hAnim, rect)), hBrush, hPen, state);
                        break;
                    }
                    case Mil.DrawRoundedRectangleAnimate:
                    {
                        // rectangle@0, radiusX@32, radiusY@40, hBrush@48, hPen@52,
                        // hRectangleAnimations@56, hRadiusXAnimations@60, hRadiusYAnimations@64.
                        var rect = new Rect((float)r.F64(), (float)r.F64(), (float)r.F64(), (float)r.F64());
                        double rx = r.F64(), ry = r.F64();
                        uint hBrush = r.U32(), hPen = r.U32();
                        uint hRectAnim = r.U32(), hRxAnim = r.U32(), hRyAnim = r.U32();
                        EmitDrawing(output, new RoundedRectangleGeometry(Anim(hRectAnim, rect),
                            (float)Anim(hRxAnim, rx), (float)Anim(hRyAnim, ry)), hBrush, hPen, state);
                        break;
                    }
                    case Mil.DrawEllipseAnimate:
                    {
                        // center@0, radiusX@16, radiusY@24, hBrush@32, hPen@36,
                        // hCenterAnimations@40, hRadiusXAnimations@44, hRadiusYAnimations@48.
                        var center = new Vector2((float)r.F64(), (float)r.F64());
                        double rx = r.F64(), ry = r.F64();
                        uint hBrush = r.U32(), hPen = r.U32();
                        uint hCenterAnim = r.U32(), hRxAnim = r.U32(), hRyAnim = r.U32();
                        EmitDrawing(output, new EllipseGeometry(Anim(hCenterAnim, center),
                            (float)Anim(hRxAnim, rx), (float)Anim(hRyAnim, ry)), hBrush, hPen, state);
                        break;
                    }
                    case Mil.DrawLineAnimate:
                    {
                        // point0@0, point1@16, hPen@32, hPoint0Animations@36, hPoint1Animations@40.
                        var p0 = new Vector2((float)r.F64(), (float)r.F64());
                        var p1 = new Vector2((float)r.F64(), (float)r.F64());
                        uint hPen = r.U32(), hP0Anim = r.U32(), hP1Anim = r.U32();
                        EmitDrawing(output, MakeLine(Anim(hP0Anim, p0), Anim(hP1Anim, p1)), 0, hPen, state);
                        break;
                    }
                    case Mil.DrawImageAnimate:
                    {
                        // rectangle@0, hImageSource@32, hRectangleAnimations@36.
                        var rect = new Rect((float)r.F64(), (float)r.F64(), (float)r.F64(), (float)r.F64());
                        uint hImg = r.U32(), hAnim = r.U32();
                        if (_bitmaps.TryGetValue(hImg, out MilBitmap abmp))
                            EmitFill(output, new RectangleGeometry(Anim(hAnim, rect)),
                                new ImageBrush(abmp.Rgba, abmp.Width, abmp.Height), state);
                        break;
                    }
                    case Mil.DrawVideoAnimate:
                    {
                        // rectangle@0, hPlayer@32, hRectangleAnimations@36.
                        var rect = new Rect((float)r.F64(), (float)r.F64(), (float)r.F64(), (float)r.F64());
                        uint hPlayer = r.U32(), hAnim = r.U32();
                        _parseTouchedContentBrush = true;      // as MilDrawVideo: frames change out of band
                        if (_videoFrames.TryGetValue(hPlayer, out MilBitmap aframe))
                            EmitFill(output, new RectangleGeometry(Anim(hAnim, rect)),
                                new ImageBrush(aframe.Rgba, aframe.Width, aframe.Height), state);
                        break;
                    }
                    case Mil.PushOpacityAnimate:
                    {
                        // opacity@0, hOpacityAnimations@8.
                        stack.Push(state);
                        double opacity = r.F64();
                        state.Opacity *= (float)Anim(r.U32(), opacity);
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
                    case Mil.DrawVideo:
                    {
                        // MILCMD_DRAW_VIDEO: rectangle@0 (4 doubles), hPlayer@32 -- byte-identical to DrawImage.
                        // The MediaElement emits this every composition pass; the managed video backend keeps
                        // _videoFrames[hPlayer] current via SendVideoFrame, so we draw the latest frame stretched
                        // into the rect (Stretch/clip/DPI carried by the rect, exactly like DrawImage).
                        var rect = new Rect((float)r.F64(), (float)r.F64(), (float)r.F64(), (float)r.F64());
                        uint hPlayer = r.U32();
                        // Re-parse this visual every frame (like a VisualBrush consumer): the DrawVideo
                        // render-data byte[] never changes, but _videoFrames[hPlayer] does, so without this the
                        // static-skip optimization would keep sampling the first frame's texture.
                        _parseTouchedContentBrush = true;
                        if (_videoFrames.TryGetValue(hPlayer, out MilBitmap frame))
                            EmitFill(output, new RectangleGeometry(rect), new ImageBrush(frame.Rgba, frame.Width, frame.Height), state);
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
                        if (hTransform != 0) _parseTransformRefs.Add(hTransform);   // record for parse-skip invalidation
                        if (_transforms.TryGetValue(hTransform, out Matrix3x2 m))
                            state.Transform = m * state.Transform;
                        break;
                    }
                    case Mil.DrawDrawing:
                    {
                        // MILCMD_DRAW_DRAWING: hDrawing@0, pad@4. The Drawing resource types are
                        // already decoded for DrawingBrush/DrawingImage; this flattens one into the
                        // record stream under the state in effect here.
                        EmitDrawingResource(output, r.U32(), state);
                        break;
                    }
                    case Mil.PushGuidelineSet:
                    {
                        // MILCMD_PUSH_GUIDELINE_SET: hGuidelines@0 (payload), pad@4.
                        stack.Push(state);
                        if (_guidelineSets.TryGetValue(r.U32(), out (float[] X, float[] Y) set))
                        {
                            foreach (float x in set.X) AddParseGuide(_parseGuidesX, x, state.Transform.M11, state.Transform.M31);
                            foreach (float y in set.Y) AddParseGuide(_parseGuidesY, y, state.Transform.M22, state.Transform.M32);
                        }
                        break;
                    }
                    case Mil.PushGuidelineY1:
                    {
                        // MILCMD_PUSH_GUIDELINE_Y1: coordinate@0. One horizontal guideline -- a text
                        // line's baseline (SimpleTextLine.Draw, Glyphs, HighlightVisual).
                        stack.Push(state);
                        AddParseGuide(_parseGuidesY, (float)r.F64(), state.Transform.M22, state.Transform.M32);
                        break;
                    }
                    case Mil.PushGuidelineY2:
                    {
                        // MILCMD_PUSH_GUIDELINE_Y2: leadingCoordinate@0, offsetToDrivenCoordinate@8.
                        // The pair a text decoration needs: snap the baseline, and carry the
                        // underline/strikethrough along by the same correction so the rule stays
                        // the right distance from the text instead of drifting a pixel.
                        stack.Push(state);
                        float lead = (float)r.F64(), offset = (float)r.F64();
                        AddParseGuide(_parseGuidesY, lead, state.Transform.M22, state.Transform.M32);
                        AddParseGuide(_parseGuidesY, lead + offset, state.Transform.M22, state.Transform.M32);
                        break;
                    }
                    case Mil.PushOpacityMask:
                    {
                        // MILCMD_PUSH_OPACITY_MASK: boundingBoxCacheLocalSpace@0 (MilRectF, 4
                        // floats), hOpacityMask@16, pad@20. The bounding box is milcore's own
                        // cache hint; the mask brush is resolved against the scope's real content
                        // bounds at Pop, which is what the per-visual mask path does too.
                        stack.Push(state);
                        r.F32(); r.F32(); r.F32(); r.F32();          // boundingBoxCacheLocalSpace
                        uint hMask = r.U32();
                        var scopeVisual = new SceneVisual();
                        scopes.Push((stack.Count, output, scopeVisual, hMask));
                        output = scopeVisual.Content;               // enclosed records land here
                        break;
                    }
                    case Mil.Pop:
                        // Close an opacity-mask scope before unwinding the state it was pushed
                        // with, so the scope and its RenderState come off together.
                        if (scopes.Count > 0 && scopes.Peek().Depth == stack.Count)
                        {
                            (int _, List<DrawingPrimitive> parent, SceneVisual nested, uint hMask) = scopes.Pop();
                            output = parent;
                            if (nested.Content.Count > 0)
                            {
                                // An unresolvable mask must not black the content out: leaving the
                                // mask null draws the group unmasked, which is the safer failure.
                                nested.OpacityMask = hMask != 0 ? ResolveBrush(hMask, ContentBounds(nested.Content)) : null;
                                parent.Add(new NestedVisualDraw(nested));
                            }
                        }
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

            // A truncated or unbalanced stream can leave a scope open. Its content is already
            // collected and must still be drawn -- dropping it would make an opacity mask look
            // like it erased the group.
            while (scopes.Count > 0)
            {
                (int _, List<DrawingPrimitive> parent, SceneVisual nested, uint hMask) = scopes.Pop();
                output = parent;
                if (nested.Content.Count == 0) continue;
                nested.OpacityMask = hMask != 0 ? ResolveBrush(hMask, ContentBounds(nested.Content)) : null;
                parent.Add(new NestedVisualDraw(nested));
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
            // Prefer the cross-platform managed descriptor (path/face/simulations);
            // fall back to the legacy pointer-keyed resolver (used by tests).
            Text.IGlyphOutlineFont? font = null;
            if (run.FontPath != null && ManagedFontResolver != null)
                font = ManagedFontResolver(new Text.FontDescriptor(run.FontPath, run.FaceIndex, run.Simulations));
            font ??= FontResolver?.Invoke(run.FontPtr);
            if (font is null) { Note("text:fontnull"); return; }
            if (run.Indices.Length == 0) { Note("text:noglyphs"); return; }

            Brush? brush = ApplyOpacity(ResolveBrush(hBrush, run.Bounds), state.Opacity);
            if (brush is null) { Note("text:brushnull"); return; }
            Note($"text:emit{run.Indices.Length}");

            var colorFont = font as Text.IColorGlyphFont;
            float scale = run.EmSize / font.PixelsPerEm;
            float penX = run.Origin.X;
            for (int i = 0; i < run.Indices.Length; i++)
            {
                float gx = penX + (run.Offsets != null ? run.Offsets[2 * i] : 0f);
                float gy = run.Origin.Y - (run.Offsets != null ? run.Offsets[2 * i + 1] : 0f);

                // Color glyph (COLR/CPAL emoji): paint each layer outline in its palette
                // colour (or the foreground brush), back-to-front, in place of the
                // single monochrome outline.
                if (colorFont != null && colorFont.TryGetColorLayers(run.Indices[i], out var layers))
                    EmitColorGlyph(output, font, layers, scale, gx, gy, brush, state);
                else if (font.TryGetGlyphOutline(run.Indices[i], out List<PathFigure> figures) && figures.Count > 0)
                {
                    Geometry glyph = new PathGeometry(FillRule.NonZero, ScaleFigures(figures, scale, gx, gy));
                    glyph = TransformGeometry(glyph, state.Transform);
                    if (state.Clip is not null)
                        glyph = new CombinedGeometry(GeometryCombineMode.Intersect, glyph, state.Clip);
                    // Baseline anchor (glyph baseline point in the geometry's space) so the
                    // coverage cache snaps the whole run to one baseline instead of snapping
                    // each glyph by its own ink box (which sinks some letters ~1px).
                    Vector2 baseline = Vector2.Transform(new Vector2(gx, gy), state.Transform);
                    output.Add(new GeometryFill(glyph, brush, isGlyph: true, baselineAnchor: baseline));
                }
                penX += i < run.Advances.Length ? run.Advances[i] : 0f;
            }
        }

        // Emit a color glyph's layers (back-to-front): each layer is an outline glyph
        // filled with its palette colour, or the run's foreground brush when the layer
        // has no palette index. Layer fills blend linearly (no text gamma).
        private void EmitColorGlyph(List<DrawingPrimitive> output, Text.IGlyphOutlineFont font,
            IReadOnlyList<Text.ColorGlyphLayer> layers, float scale, float gx, float gy, Brush foreground, RenderState state)
        {
            foreach (Text.ColorGlyphLayer layer in layers)
            {
                if (!font.TryGetGlyphOutline(layer.GlyphId, out List<PathFigure> figures) || figures.Count == 0)
                    continue;
                Geometry glyph = new PathGeometry(FillRule.NonZero, ScaleFigures(figures, scale, gx, gy));
                glyph = TransformGeometry(glyph, state.Transform);
                if (state.Clip is not null)
                    glyph = new CombinedGeometry(GeometryCombineMode.Intersect, glyph, state.Clip);

                Brush layerBrush = layer.Color is RgbaColor c
                    ? ApplyOpacity(new SolidColorBrush(c), state.Opacity)!
                    : foreground;
                output.Add(new GeometryFill(glyph, layerBrush, isGlyph: false));
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
            bool isContent = _contentBrushes.ContainsKey(hBrush);
            // A content brush's painter must re-parse every frame so the live source stays current; but
            // DON'T flag it as CPU-2D here — that's decided per path below (GPU-live vs pixel readback).
            if (isContent) _parseTouchedContentBrush = true;
            if (!_imageBrushes.TryGetValue(hBrush, out MilImageBrush ib))
                return false;

            // GPU-live path: a VisualBrush/DrawingBrush painted as a plain axis-aligned Fill (TileMode.None,
            // Stretch=Fill, Viewport covering the shape) over a RectangleGeometry samples the source rendered
            // to a GPU texture -- no CPU render+readback, no per-tile crop+upload. This is the dominant case
            // (a puzzle chops one animated visual into rectangular tiles, each a Viewbox sub-rect).
            if (s_gpuVisualBrush && isContent && ib.Tile == TileMode.None && ib.Stretch == 1 /*Fill*/
                && g is RectangleGeometry
                && TryBuildGpuSourceBrush(hBrush, ib, bounds, (float)ib.Opacity, out fill))
            {
                _brushesGpuLive.Add(hBrush);
                return true;
            }

            // CPU pixel path (readback bitmap): flag as CPU-2D so RealizeContentBrushes keeps rasterizing it.
            if (isContent) _contentBrushes2D.Add(hBrush);
            if (!_bitmaps.TryGetValue(ib.ImageHandle, out MilBitmap bmp))
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
                fill = new ImageBrush(pixels, iw, ih, ib.Tile, vw, vh, (float)ib.Opacity);
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
                fill = new ImageBrush(CropRgba(pixels, iw, cx, cy, cw, ch), cw, ch, TileMode.None, vw, vh, (float)ib.Opacity);
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
            fill = new ImageBrush(pixels, iw, ih, TileMode.None, dest.Width, dest.Height, (float)ib.Opacity);
            return true;
        }

        // Build a GPU-live source brush for a content brush (see TryImageBrushFill): wrap the live source
        // visual so its content bounds map onto a [0,pw]x[0,ph] texture (the renderer draws it on the GPU
        // each frame, deduped across tiles), with the Viewbox carried as a UV sub-rect. Fails (-> CPU pixel
        // path) if the Viewport doesn't cover the shape (a sub-tile/offset needs the dest-clip path) or the
        // source has no bounds.
        private bool TryBuildGpuSourceBrush(uint hBrush, MilImageBrush ib, Rect bounds, float opacity, out Brush? fill)
        {
            fill = null;
            if (!_contentBrushes.TryGetValue(hBrush, out (uint Source, bool IsDrawing) cb)) return false;

            Rect vp = ViewportRect(ib, bounds);
            if (Math.Abs(vp.X - bounds.X) > 0.01 || Math.Abs(vp.Y - bounds.Y) > 0.01 ||
                Math.Abs(vp.Width - bounds.Width) > 0.01 || Math.Abs(vp.Height - bounds.Height) > 0.01)
                return false;

            SceneVisual? source = cb.IsDrawing
                ? BuildDrawingVisual(cb.Source)
                : (_visuals.TryGetValue(cb.Source, out SceneVisual? v) ? v : null);
            if (source is null) return false;
            Rect b = VisualSubtreeBounds(source, source.LocalToParent);
            if (b.Width <= 0.01f || b.Height <= 0.01f) return false;

            const int supersample = 2;
            int pw = Math.Clamp((int)MathF.Ceiling(b.Width * supersample), 1, 1024);
            int ph = Math.Clamp((int)MathF.Ceiling(b.Height * supersample), 1, 1024);
            var wrapper = new SceneVisual
            {
                Transform = Matrix3x2.CreateTranslation(-b.X, -b.Y) * Matrix3x2.CreateScale(pw / b.Width, ph / b.Height),
            };
            wrapper.Children.Add(source);

            float u0 = 0, v0 = 0, u1 = 1, v1 = 1;   // Viewbox (RelativeToBoundingBox) -> UV sub-rect.
            if (ib.ViewboxUnits == 1 && !IsUnitRect(ib.Viewbox))
            {
                u0 = (float)ib.Viewbox.X; v0 = (float)ib.Viewbox.Y;
                u1 = u0 + (float)ib.Viewbox.Width; v1 = v0 + (float)ib.Viewbox.Height;
            }

            fill = new ImageBrush(wrapper, cb.Source, pw, ph, u0, v0, u1, v1, 0f, 0f, opacity);
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
            if (_contentBrushes.ContainsKey(handle)) { _parseTouchedContentBrush = true; _contentBrushes2D.Add(handle); }
            if (_solidBrushes.TryGetValue(handle, out RgbaColor c)) return new SolidColorBrush(c);
            if (_gradients.TryGetValue(handle, out MilGradient? g)) return BuildGradient(g, bounds);
            if (_imageBrushes.TryGetValue(handle, out MilImageBrush ib) && _bitmaps.TryGetValue(ib.ImageHandle, out MilBitmap bmp))
                return new ImageBrush(bmp.Rgba, bmp.Width, bmp.Height, ib.Tile, bounds.Width, bounds.Height, (float)ib.Opacity);
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

        // Stable insertion sort of gradient stops by ascending Offset (keeps declaration order for
        // equal offsets so hard-edge stops render as WPF does). Stop counts are small.
        private static GradientStop[] StableSortByOffset(GradientStop[] stops)
        {
            for (int i = 1; i < stops.Length; i++)
            {
                GradientStop key = stops[i];
                int j = i - 1;
                while (j >= 0 && stops[j].Offset > key.Offset) { stops[j + 1] = stops[j]; j--; }
                stops[j + 1] = key;
            }
            return stops;
        }

        // sRGB (gamma) encode of one scRGB (linear) channel. Legacy WPF composites in gamma space, so
        // when gamma compositing is on we encode every colour AT ITS SOURCE (here) and the whole
        // downstream pipeline blends the encoded values against an UNORM target. Identity (passthrough)
        // when gamma compositing is off (physically-linear pipeline + sRGB target). Alpha is never encoded.
        internal static float EncCh(float c)
        {
            if (!WgpuSceneRenderer.s_gammaComposite) return c;
            c = Math.Clamp(c, 0f, 1f);
            return c <= 0.0031308f ? c * 12.92f : 1.055f * MathF.Pow(c, 1f / 2.4f) - 0.055f;
        }
        private static RgbaColor EncCol(float r, float g, float b, float a) => new(EncCh(r), EncCh(g), EncCh(b), a);

        private static GradientStop[] ReadGradientStops(ref MilReader r, uint sizeBytes, double opacity)
        {
            int count = (int)(sizeBytes / 24);   // MIL_GRADIENTSTOP = double Position + MilColorF (24 bytes)
            var stops = new GradientStop[count];
            for (int i = 0; i < count; i++)
            {
                float pos = (float)r.F64();
                float cr = r.F32(), cg = r.F32(), cb = r.F32(), ca = r.F32();
                stops[i] = new GradientStop(pos, EncCol(cr, cg, cb, (float)(ca * opacity)));
            }
            // WPF interpolates gradient stops by Offset, not declaration order (XAML may list them
            // in any order, e.g. descending). Our ramp/sampler assume ascending offsets, so sort
            // here. A stable sort preserves declaration order for coincident offsets (hard stops).
            return count > 1 ? StableSortByOffset(stops) : stops;
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
                case 4: // Arc (MIL_SEGMENT_ARC) -> cubic beziers. Point@16, Size(radii)@32,
                {       // XRotation(deg)@48, eSweepDirection@56 (0=CCW, 1=CW), fLargeArc@60.
                    Vector2 end = P(16);
                    Vector2 rad = P(32);
                    r.Position = segPos + 48; double xRotDeg = r.F64();
                    r.Position = segPos + 56; bool sweepClockwise = r.U32() != 0;
                    r.Position = segPos + 60; bool largeArc = r.U32() != 0;
                    AddArcAsBeziers(figure, CurrentPoint(figure), end, (float)rad.X, (float)rad.Y,
                        (float)xRotDeg, largeArc, sweepClockwise);
                    return 64;
                }
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

        // Current pen position of a figure = the last segment's endpoint (or the figure start).
        private static Vector2 CurrentPoint(PathFigure figure)
        {
            if (figure.Segments.Count == 0) return figure.Start;
            return figure.Segments[figure.Segments.Count - 1] switch
            {
                LineSegment l => l.Point,
                QuadraticBezierSegment q => q.Point,
                CubicBezierSegment c => c.Point,
                _ => figure.Start,
            };
        }

        // Endpoint-parameterised elliptical arc -> cubic beziers (SVG/W3C implementation-notes
        // algorithm). WPF serialises a Border's non-uniform CornerRadius corners as MIL arc
        // segments; approximating them with a straight line produced chamfered ("triangular")
        // corners. Splits the arc into <=90 deg pieces, each a cubic bezier.
        /// <summary>Test hook: the MIL arc-to-Bézier conversion in isolation.</summary>
        internal static void AddArcAsBeziersForTest(PathFigure figure, Vector2 start, Vector2 end,
            float rx, float ry, float xRotDeg, bool largeArc, bool sweepClockwise)
            => AddArcAsBeziers(figure, start, end, rx, ry, xRotDeg, largeArc, sweepClockwise);

        private static void AddArcAsBeziers(PathFigure figure, Vector2 start, Vector2 end,
            float rx, float ry, float xRotDeg, bool largeArc, bool sweepClockwise)
        {
            if (rx == 0f || ry == 0f || (start.X == end.X && start.Y == end.Y))
            {
                figure.Segments.Add(new LineSegment(end));
                return;
            }

            rx = MathF.Abs(rx);
            ry = MathF.Abs(ry);
            float phi = xRotDeg * MathF.PI / 180f;
            float cosPhi = MathF.Cos(phi), sinPhi = MathF.Sin(phi);

            // Step 1: (x1', y1') in the rotated frame.
            float dx = (start.X - end.X) * 0.5f, dy = (start.Y - end.Y) * 0.5f;
            float x1p = cosPhi * dx + sinPhi * dy;
            float y1p = -sinPhi * dx + cosPhi * dy;

            // Ensure radii are large enough.
            float lambda = (x1p * x1p) / (rx * rx) + (y1p * y1p) / (ry * ry);
            if (lambda > 1f) { float s = MathF.Sqrt(lambda); rx *= s; ry *= s; }

            float rx2 = rx * rx, ry2 = ry * ry, x1p2 = x1p * x1p, y1p2 = y1p * y1p;

            // Step 2: centre (cx', cy') in the rotated frame.
            float num = rx2 * ry2 - rx2 * y1p2 - ry2 * x1p2;
            float den = rx2 * y1p2 + ry2 * x1p2;
            float co = MathF.Sqrt(MathF.Max(0f, num / den));
            if (largeArc == sweepClockwise) co = -co;
            float cxp = co * (rx * y1p / ry);
            float cyp = co * (-ry * x1p / rx);

            // Step 3: centre in the original frame.
            float cx = cosPhi * cxp - sinPhi * cyp + (start.X + end.X) * 0.5f;
            float cy = sinPhi * cxp + cosPhi * cyp + (start.Y + end.Y) * 0.5f;

            // Step 4: start angle and sweep.
            float ux = (x1p - cxp) / rx, uy = (y1p - cyp) / ry;
            float vx = (-x1p - cxp) / rx, vy = (-y1p - cyp) / ry;
            float theta1 = SignedAngle(1f, 0f, ux, uy);
            float dtheta = SignedAngle(ux, uy, vx, vy);
            if (!sweepClockwise && dtheta > 0f) dtheta -= 2f * MathF.PI;
            else if (sweepClockwise && dtheta < 0f) dtheta += 2f * MathF.PI;

            // Split by RADIUS, not just at 90-degree boundaries. The fixed-quadrant rule leaves
            // ~2.7e-4*r of radial error, so a large arc (a rounded panel edge, a gauge sweep)
            // arrives already outside the flattening tolerance and no amount of downstream
            // subdivision can recover it -- the cubics themselves are the wrong shape.
            // This is decode time, so there is no world transform to scale against; the
            // default device tolerance is the best available target and is strictly tighter
            // than what the quadrant rule gave.
            int segs = CurveFlattener.ArcCount(MathF.Max(rx, ry), dtheta, CurveFlattener.DefaultTolerance);
            if (segs < 1) segs = 1;
            float delta = dtheta / segs;
            float sinHalf = MathF.Sin(delta * 0.5f);
            float alpha = sinHalf == 0f ? 0f : (4f / 3f) * (1f - MathF.Cos(delta * 0.5f)) / sinHalf;

            float theta = theta1;
            Vector2 cur = start;
            for (int i = 0; i < segs; i++)
            {
                float theta2 = theta + delta;
                float cosT = MathF.Cos(theta), sinT = MathF.Sin(theta);
                float cosT2 = MathF.Cos(theta2), sinT2 = MathF.Sin(theta2);

                Vector2 p2 = new Vector2(
                    cx + rx * cosT2 * cosPhi - ry * sinT2 * sinPhi,
                    cy + rx * cosT2 * sinPhi + ry * sinT2 * cosPhi);

                // Ellipse tangents (unnormalised d/dtheta).
                Vector2 d1 = new Vector2(
                    -rx * sinT * cosPhi - ry * cosT * sinPhi,
                    -rx * sinT * sinPhi + ry * cosT * cosPhi);
                Vector2 d2 = new Vector2(
                    -rx * sinT2 * cosPhi - ry * cosT2 * sinPhi,
                    -rx * sinT2 * sinPhi + ry * cosT2 * cosPhi);

                figure.Segments.Add(new CubicBezierSegment(
                    new Vector2(cur.X + alpha * d1.X, cur.Y + alpha * d1.Y),
                    new Vector2(p2.X - alpha * d2.X, p2.Y - alpha * d2.Y),
                    p2));

                cur = p2;
                theta = theta2;
            }
        }

        // Signed angle from (ux,uy) to (vx,vy).
        private static float SignedAngle(float ux, float uy, float vx, float vy)
        {
            float dot = ux * vx + uy * vy;
            float len = MathF.Sqrt((ux * ux + uy * uy) * (vx * vx + vy * vy));
            float ang = len == 0f ? 0f : MathF.Acos(Math.Clamp(dot / len, -1f, 1f));
            if (ux * vy - uy * vx < 0f) ang = -ang;
            return ang;
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
