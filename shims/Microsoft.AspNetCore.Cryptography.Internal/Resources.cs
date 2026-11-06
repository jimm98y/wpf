// The assembly's strings (Runtime/'s Resources.resx), as ASP.NET Core's build generates them.

using System.Globalization;

namespace Microsoft.AspNetCore.Cryptography.Internal;

internal static class Resources
{
    internal static string BCryptAlgorithmHandle_ProviderNotFound => "A provider could not be found for algorithm '{0}'.";
    internal static string BCRYPT_KEY_LENGTHS_STRUCT_InvalidKeyLength => "The key length {0} is invalid. Valid key lengths are {1} to {2} bits (step size {3}).";
    internal static string Platform_Windows7Required => "This operation requires Windows 7 / Windows Server 2008 R2 or later.";
    internal static string Platform_Windows8Required => "This operation requires Windows 8 / Windows Server 2012 or later.";

    internal static string FormatBCryptAlgorithmHandle_ProviderNotFound(object? p0) =>
        string.Format(CultureInfo.CurrentCulture, BCryptAlgorithmHandle_ProviderNotFound, p0);

    internal static string FormatBCRYPT_KEY_LENGTHS_STRUCT_InvalidKeyLength(object? p0, object? p1, object? p2, object? p3) =>
        string.Format(CultureInfo.CurrentCulture, BCRYPT_KEY_LENGTHS_STRUCT_InvalidKeyLength, p0, p1, p2, p3);
}
