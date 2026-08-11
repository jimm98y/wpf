// ElementHost (WindowsFormsIntegration) plugs a hosted WPF scene into this assembly's present path
// through EmbeddedScenes, and needs the driver's window/scale to place its HwndSource. That seam is
// internal on purpose: it is how the two halves of the stack meet, not public API.

using System.Runtime.CompilerServices;

// The public key is the ECMA one both assemblies are public-signed with: a strong-named assembly
// may only befriend a named public key, so this has to move in lockstep with SignAssembly there.
[assembly: InternalsVisibleTo("WindowsFormsIntegration, PublicKey=00000000000000000400000000000000")]
