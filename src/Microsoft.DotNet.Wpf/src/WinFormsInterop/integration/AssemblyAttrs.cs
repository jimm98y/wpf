// The same xmlns mappings the Windows-only WindowsFormsIntegration declares, so that
// <WindowsFormsHost/> resolves in the DEFAULT WPF namespace with no xmlns prefix in the app's XAML
// -- which is how every existing app writes it. Without these the markup compiler fails with
// "the tag 'WindowsFormsHost' does not exist in XML namespace .../presentation", and the type being
// present in a referenced assembly makes no difference.

[assembly: System.Windows.Markup.XmlnsDefinition(
    "http://schemas.microsoft.com/winfx/2006/xaml/presentation",
    "System.Windows.Forms.Integration")]

[assembly: System.Windows.Markup.XmlnsDefinition(
    "http://schemas.microsoft.com/netfx/2007/xaml/presentation",
    "System.Windows.Forms.Integration")]

// The interop-seam tests (tests/CrossPlatform/Wpf.WinFormsInterop.Tests). The drag-and-drop
// adapters are internal because nothing outside this assembly should be constructing them, but they
// are also the part most worth testing directly: they translate between two stacks' data objects,
// and a mistake there is invisible until a real drag carries the wrong thing.
//
// The public key is the ECMA standard one the test project public-signs with; a strong-named
// assembly only accepts strong-named friends.
[assembly: System.Runtime.CompilerServices.InternalsVisibleTo(
    "Wpf.WinFormsInterop.Tests, PublicKey=00000000000000000400000000000000")]
