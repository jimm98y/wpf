// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// PresentationFramework raises the off-Windows appearance change through this assembly's internal
// entry point (SystemEvents.NotifySystemAppearanceChanged), so that an app subscribing to
// UserPreferenceChanged hears about a Dark/Light switch on macOS and Linux exactly as it does on
// Windows.
//
// The alternative was to duplicate the detection here: the portal/D-Bus query on Linux and the
// NSAppearance one on macOS both already exist in the platform layer, complete with their fallbacks
// and their change signals, and a second copy in this assembly would be two implementations of one
// question, drifting apart. So the knowledge stays where it is and only the notification crosses.
//
// The key is PresentationFramework's real one (token 31bf3856ad364e35): this assembly is
// public-signed with the Microsoft key, and an InternalsVisibleTo naming the wrong key silently
// grants nothing.
[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("PresentationFramework, PublicKey=0024000004800000940000000602000000240000525341310004000001000100b5fc90e7027f67871e773a8fde8938c81dd402ba65b9201d60593e96c492651e889cc13f1415ebb53fac1131ae0bd333c5ee6021672d9718ea31a8aebd0da0072f25d87dba6fc90ffd598ed4da35e44c398c454307e8e33b8426143daec9f596836f97c8f74750e5975c64e2189f45def46b2a2b1247adc3652bf5c308055da9")]
