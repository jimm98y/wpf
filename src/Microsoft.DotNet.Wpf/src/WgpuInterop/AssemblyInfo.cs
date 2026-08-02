// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Runtime.CompilerServices;

// The WebGPU binding is internal API (it becomes part of PresentationCore's
// native seam). The Phase-0 smoke test lives in a separate assembly and needs
// access to drive the device path directly.
// The DirectWriteForwarder replacement (off-Windows) reuses this assembly's managed OpenType
// font stack (Composition.Text) to back PresentationCore's MS.Internal.Text.TextInterface layer,
// so it needs access to the internal font types.
[assembly: InternalsVisibleTo("DirectWriteForwarder")]
[assembly: InternalsVisibleTo("WgpuInterop.SmokeTest")]
[assembly: InternalsVisibleTo("IosSpike")]
[assembly: InternalsVisibleTo("WgpuInterop.RenderTest")]
[assembly: InternalsVisibleTo("WgpuInterop.SurfaceDemo")]
[assembly: InternalsVisibleTo("WgpuInterop.DuceTest")]
[assembly: InternalsVisibleTo("WgpuInterop.BrushTest")]
[assembly: InternalsVisibleTo("WgpuInterop.TextTest")]
[assembly: InternalsVisibleTo("WgpuInterop.AAPathTest")]
[assembly: InternalsVisibleTo("WgpuInterop.CurveFidelityTest")]
[assembly: InternalsVisibleTo("WgpuInterop.ScaleProbe")]
[assembly: InternalsVisibleTo("WgpuInterop.ShaderValidationTest")]
[assembly: InternalsVisibleTo("WgpuInterop.DeterminismProbe")]
[assembly: InternalsVisibleTo("WgpuInterop.RenderBaselineTest")]
[assembly: InternalsVisibleTo("WgpuInterop.BlurKernelTest")]
[assembly: InternalsVisibleTo("WgpuInterop.ShaderEffectTest")]
[assembly: InternalsVisibleTo("WgpuInterop.GuidelineTest")]
[assembly: InternalsVisibleTo("WgpuInterop.DrawingLeafTest")]
[assembly: InternalsVisibleTo("WgpuInterop.IconFallbackTest")]
[assembly: InternalsVisibleTo("WgpuInterop.RenderOptionsTest")]
[assembly: InternalsVisibleTo("WgpuInterop.BitmapCacheTest")]
[assembly: InternalsVisibleTo("WgpuInterop.CompositingModeTest")]
[assembly: InternalsVisibleTo("WgpuInterop.LocalCacheProbe")]
[assembly: InternalsVisibleTo("WgpuInterop.VideoTest")]
[assembly: InternalsVisibleTo("WgpuInterop.StabilityTest")]
[assembly: InternalsVisibleTo("WgpuInterop.FontTest")]
[assembly: InternalsVisibleTo("WgpuInterop.CffTest")]
[assembly: InternalsVisibleTo("WgpuInterop.GammaTest")]
[assembly: InternalsVisibleTo("WgpuInterop.TtcTest")]
[assembly: InternalsVisibleTo("WgpuInterop.ColorGlyphTest")]
[assembly: InternalsVisibleTo("WgpuInterop.StrokeTest")]
[assembly: InternalsVisibleTo("WgpuInterop.StyledStrokeTest")]
[assembly: InternalsVisibleTo("WgpuInterop.BrushStrokeTest")]
[assembly: InternalsVisibleTo("WgpuInterop.CompositeGlyphTest")]
[assembly: InternalsVisibleTo("WgpuInterop.GroupOpacityTest")]
[assembly: InternalsVisibleTo("WgpuInterop.EffectTest")]
[assembly: InternalsVisibleTo("WgpuInterop.KerningTest")]
[assembly: InternalsVisibleTo("WgpuInterop.ClipTest")]
[assembly: InternalsVisibleTo("WgpuInterop.RadialGradientTest")]
[assembly: InternalsVisibleTo("WgpuInterop.SpreadTest")]
[assembly: InternalsVisibleTo("WgpuInterop.TileTest")]
[assembly: InternalsVisibleTo("WgpuInterop.RoundedRectTest")]
[assembly: InternalsVisibleTo("WgpuInterop.EllipseTest")]
[assembly: InternalsVisibleTo("WgpuInterop.HitTestTest")]
[assembly: InternalsVisibleTo("WgpuInterop.GeometryGroupTest")]
[assembly: InternalsVisibleTo("WgpuInterop.OpacityMaskTest")]
[assembly: InternalsVisibleTo("WgpuInterop.DrawGeometryTest")]
[assembly: InternalsVisibleTo("WgpuInterop.CombinedGeometryTest")]
[assembly: InternalsVisibleTo("WgpuInterop.Viewport3DTest")]
[assembly: InternalsVisibleTo("WgpuInterop.MediaContextTest")]
[assembly: InternalsVisibleTo("WgpuInterop.LiveCompositionTest")]
[assembly: InternalsVisibleTo("WgpuInterop.MilDecodeTest")]
[assembly: InternalsVisibleTo("WgpuInterop.GlyphRunTest")]
[assembly: InternalsVisibleTo("WgpuInterop.VisualClipTest")]
[assembly: InternalsVisibleTo("WgpuInterop.EffectDecodeTest")]
[assembly: InternalsVisibleTo("WgpuInterop.ImageTest")]
[assembly: InternalsVisibleTo("WgpuInterop.TransformTest")]
[assembly: InternalsVisibleTo("WgpuInterop.OpacityMaskDecodeTest")]
[assembly: InternalsVisibleTo("WgpuInterop.ManagedFontTest")]
[assembly: InternalsVisibleTo("WgpuInterop.WasmSpike")]
// WinForms-on-WebGPU host: presents the Mono System.Windows.Forms composite through the shared
// WebGPU present path (surface + WgpuSceneRenderer image quad), cross-platform (mac/win/browser).
[assembly: InternalsVisibleTo("WinFormsHost")]
// GPU-rasterization proof: draws WinForms-style controls (bevels + text) as WgpuSceneRenderer
// primitives (no libgdiplus), de-risking the System.Drawing-backend swap.
[assembly: InternalsVisibleTo("WinFormsGpuRaster")]
// Our vendored System.Drawing's GPU-raster backend (SceneRecorder) records Graphics verbs into the
// scene graph, so it needs the internal Scene/renderer types.
[assembly: InternalsVisibleTo("Mono.System.Drawing")]
