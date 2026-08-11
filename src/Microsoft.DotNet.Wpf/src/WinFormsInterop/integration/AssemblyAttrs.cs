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
