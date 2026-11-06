namespace WpfWebGpu.Packager;

/// <summary>A problem with the Packager's input the user can fix; reported without a stack trace.</summary>
public sealed class PackagerError(string message) : Exception(message);
