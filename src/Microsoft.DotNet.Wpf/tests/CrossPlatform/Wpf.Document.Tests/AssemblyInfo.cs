// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Xunit;

// Document layout runs on the same process-wide font catalog and MediaContext as the text tests,
// and a FlowDocument's paginator is not thread-safe. Run one at a time.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
