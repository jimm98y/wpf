// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// Compiled INSTEAD of the tests when the WPF fork has not been built (see the csproj). The tests
// bind against WindowsBase/PresentationCore/PresentationFramework from artifacts/bin, so without
// them there is nothing to compile; failing the build would block anyone working only on the
// renderer, so the project builds empty and says why.
