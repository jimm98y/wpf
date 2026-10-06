// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Xunit;

// These tests touch no shared cache and no dispatcher, so parallelism would be safe -- but they are
// microseconds each, and matching the sibling suites keeps one story about how the tests run.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
