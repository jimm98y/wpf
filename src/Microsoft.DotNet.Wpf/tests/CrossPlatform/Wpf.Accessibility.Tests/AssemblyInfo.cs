// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Xunit;

// Automation peers hang off live elements and the WM_GETOBJECT test owns a real window; neither
// tolerates another test touching the dispatcher underneath it. Run one at a time.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
