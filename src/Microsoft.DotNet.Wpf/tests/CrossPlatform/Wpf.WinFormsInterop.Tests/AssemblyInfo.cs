// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Xunit;

// The WinForms driver is a process-wide singleton with a message queue, and these tests mutate its
// drop-target registry. Running classes in parallel would have them tread on each other's state.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
