// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Xunit;

// The font catalog and the typeface caches behind it are process-wide and built lazily on first
// use, so tests that measure text share them. Running collections in parallel would have several
// threads racing that first build for no gain -- these tests are milliseconds each.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
