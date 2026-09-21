// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Xunit;

// Every test here owns a real foreground window and injects contacts into the system's pointer
// queue. Two of those running at once would inject into each other's window. Run one at a time.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
