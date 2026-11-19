// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace WgpuInterop.Tests.Harness
{
    /// <summary>What the compiled-in probes ask of the test harness: no GPU tests here.</summary>
    internal static class Requirements
    {
        public static string? GpuUnavailable => "the browser's media report runs no GPU test";
    }
}
