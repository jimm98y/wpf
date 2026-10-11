// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Xunit;

// One UI thread, one window at a time. xunit runs distinct COLLECTIONS in parallel, and these tests
// show real top-level windows and then assert what the framework concluded about them -- two of them
// at once would race for the same dispatcher and the same screen.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
