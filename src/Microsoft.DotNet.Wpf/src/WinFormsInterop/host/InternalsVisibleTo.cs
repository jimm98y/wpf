// ElementHost (WindowsFormsIntegration) plugs a hosted WPF scene into this assembly's present path
// through EmbeddedScenes, and needs the driver's window/scale to place its HwndSource. That seam is
// internal on purpose: it is how the two halves of the stack meet, not public API.

using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("WindowsFormsIntegration")]
