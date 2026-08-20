// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("WindowsBase.Tests, PublicKey=00000000000000000400000000000000")]

// The cross-platform windowing-head tests (tests/CrossPlatform/Wpf.Platform.Tests). They drive the
// IPlatformWindow heads directly, below WPF, and the clipboard/input seams they need
// (PlatformClipboard) are internal to this assembly. Same PublicKey as WindowsBase.Tests above: the
// repo signs with the ECMA open key, and a strong-named assembly only accepts strong-named friends.
[assembly: InternalsVisibleTo("Wpf.Platform.Tests, PublicKey=00000000000000000400000000000000")]
[assembly: InternalsVisibleTo("Wpf.Window.Tests, PublicKey=00000000000000000400000000000000")]

// The cross-platform printing tests (tests/CrossPlatform/Wpf.Printing.Tests). They install a stand-in
// print backend through PlatformPrint, which is the only way to exercise the printing stack on a
// machine that has no printers -- and the only way to assert on what a backend is HANDED, which is
// where the interesting mistakes are.
[assembly: InternalsVisibleTo("Wpf.Printing.Tests, PublicKey=00000000000000000400000000000000")]
