// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Runtime.CompilerServices;

// The WebGPU binding is internal API (it becomes part of PresentationCore's
// native seam). The Phase-0 smoke test lives in a separate assembly and needs
// access to drive the device path directly.
[assembly: InternalsVisibleTo("WgpuInterop.SmokeTest")]
[assembly: InternalsVisibleTo("WgpuInterop.RenderTest")]
[assembly: InternalsVisibleTo("WgpuInterop.SurfaceDemo")]
[assembly: InternalsVisibleTo("WgpuInterop.DuceTest")]
[assembly: InternalsVisibleTo("WgpuInterop.BrushTest")]
[assembly: InternalsVisibleTo("WgpuInterop.TextTest")]
[assembly: InternalsVisibleTo("WgpuInterop.AAPathTest")]
