// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Xunit;

// The renderer and the WebGPU device are not safe to drive from several threads, and the tests share
// one device (see GpuFixture). Collections must therefore run one at a time. Within the GPU
// collection xunit already serialises; this makes it true for the whole assembly, including the
// CPU-only tests that would otherwise interleave with GPU work.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
