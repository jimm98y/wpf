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
[assembly: InternalsVisibleTo("WgpuInterop.AdapterProbe")]
[assembly: InternalsVisibleTo("WaylandSpike")]
[assembly: InternalsVisibleTo("IosSpike")]
[assembly: InternalsVisibleTo("WgpuInterop.SurfaceDemo")]
[assembly: InternalsVisibleTo("WgpuInterop.CurveFidelityTest")]
[assembly: InternalsVisibleTo("WgpuInterop.ScaleProbe")]
[assembly: InternalsVisibleTo("WgpuInterop.LocalCacheProbe")]
// The cross-platform test suite (tests/WgpuInterop.Tests). Per-test-app entries are removed as
// their apps are folded into it; the ones left belong to apps not yet migrated.
[assembly: InternalsVisibleTo("WgpuInterop.Tests")]
[assembly: InternalsVisibleTo("WgpuInterop.LiveCompositionTest")]
[assembly: InternalsVisibleTo("WgpuInterop.WasmSpike")]
// WinForms-on-WebGPU host: presents the Mono System.Windows.Forms composite through the shared
// WebGPU present path (surface + WgpuSceneRenderer image quad), cross-platform (mac/win/browser).
[assembly: InternalsVisibleTo("WinFormsHost")]
// GPU-rasterization proof: draws WinForms-style controls (bevels + text) as WgpuSceneRenderer
// primitives (no libgdiplus), de-risking the System.Drawing-backend swap.
[assembly: InternalsVisibleTo("WinFormsGpuRaster")]
// The reverse embedding of the gallery's WindowsFormsHost: a WPF element tree hosted inside a
// WinForms app (ElementHost). It wraps the scene HostedWpfContent publishes in a scale/clip visual
// before handing it to the WinForms present path, so it needs the scene-graph types.
[assembly: InternalsVisibleTo("WpfInWinForms")]
// Our vendored System.Drawing's GPU-raster backend (SceneRecorder) records Graphics verbs into the
// scene graph, so it needs the internal Scene/renderer types.
[assembly: InternalsVisibleTo("Mono.System.Drawing")]
