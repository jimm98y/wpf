// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// Diagnostic switches read once.
//
// The text and raster paths carry dozens of WPF_* switches for probing them against GDI. Most are
// static readonly fields, read when their class first loads; the ones inside method bodies were
// read again on every call -- per glyph, per size -- and Environment.GetEnvironmentVariable walks
// the whole environment block and allocates the value each time. On the first frame of a window
// full of text that was 80 ms of reading switches nobody had set.
//
// A switch read through here is read the first time it is asked for and remembered for the life
// of the process, which is what a static readonly field gives the rest of them.
//
using System;
using System.Collections.Concurrent;

namespace Microsoft.Wpf.Interop.WebGpu.Composition
{
    internal static class EnvVar
    {
        private static readonly ConcurrentDictionary<string, string?> s_values = new(StringComparer.Ordinal);

        /// <summary>The variable's value when first asked for, or null when it is not set.</summary>
        internal static string? Get(string name) => s_values.GetOrAdd(name, static n => Environment.GetEnvironmentVariable(n));
    }
}
