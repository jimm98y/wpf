// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("WindowsBase.Tests, PublicKey=00000000000000000400000000000000")]

// The cross-platform windowing-head tests (tests/CrossPlatform/Wpf.Platform.Tests). They drive the
// IPlatformWindow heads directly, below WPF, and the clipboard/input seams they need
// (PlatformClipboard) are internal to this assembly. Same PublicKey as WindowsBase.Tests above: the
// repo signs with the ECMA open key, and a strong-named assembly only accepts strong-named friends.
[assembly: InternalsVisibleTo("Wpf.Platform.Tests, PublicKey=00000000000000000400000000000000")]
