// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Xunit;

namespace Wpf.WinFormsInterop.Tests
{
    /// <summary>
    /// The tests that change or depend on the process's DPI state: WinForms' mode (ScaleHelper) is one
    /// for the whole process, OverrideForTests swaps it for everyone, and a test that puts a form on
    /// the 240-dpi monitor computes what it expects for one particular mode. Run in parallel with each
    /// other they saw each other's mode -- a popup expected at 96 dpi, placed at 240 -- so they run
    /// alone, one after another.
    /// </summary>
    [CollectionDefinition(Name, DisableParallelization = true)]
    public sealed class ProcessDpiStateCollection
    {
        public const string Name = "Process DPI state";
    }
}
