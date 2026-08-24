// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Runtime.CompilerServices;

// The indexed properties keep Create and PrintSystemDelegates internal, as the C++/CLI original did
// (they sit in its `internal:` section, and the public contract exposes the properties rather than
// the notifications). The tests exercise both, so they need the same access the original's tests had.
//
// Two keys, because the test projects are not signed alike: System.Printing.Tests sets
// StrongNameKeyId=Open and so carries the open-source key below, while the older suites (e.g.
// WindowsBase.Tests) do not set it and carry the ECMA key. Granting only one produces CS0281 --
// "friend access was granted, but the public key of the output assembly does not match" -- which
// reads like a missing InternalsVisibleTo and is really a mismatched one.
[assembly: InternalsVisibleTo("System.Printing.Tests, PublicKey=00240000048000009400000006020000002400005253413100040000010001004b86c4cb78549b34bab61a3b1800e23bfeb5b3ec390074041536a7e3cbd97f5f04cf0f857155a8928eaa29ebfd11cfbbad3ba70efea7bda3226c6a8d370a4cd303f714486b6ebc225985a638471e6ef571cc92a4613c00b8fa65d61ccee0cbe5f36330c9a01f4183559f1bef24cc2917c6d913e3a541333a1d05d9bed22b38cb")]
[assembly: InternalsVisibleTo("Wpf.Printing.Tests, PublicKey=00000000000000000400000000000000")]
