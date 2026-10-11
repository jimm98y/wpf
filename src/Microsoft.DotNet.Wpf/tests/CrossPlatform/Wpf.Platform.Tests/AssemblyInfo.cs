// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Xunit;

// Windowing is not safe to drive from several threads, and these tests create real windows.
//
// This was missing until a second test class was added, and the failure was thoroughly misleading:
// xunit runs distinct COLLECTIONS in parallel, so the new classes raced the shared-window fixture,
// the Wayland head died natively, and the whole host went down before reporting anything. The
// visible symptom was every test "failing" -- including one that creates no window at all -- with an
// empty results log and a wall of Gtk-CRITICAL. Nothing pointed at parallelism.
//
// The renderer suite has carried the same attribute from the start for the same reason (one shared
// GPU device); a per-assembly setting does not inherit, so it has to be stated here too.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
