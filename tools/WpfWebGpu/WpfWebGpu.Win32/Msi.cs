// msi: the Windows Installer's product queries, answered as msi.dll answers them -- from the registry,
// where the Installer keeps every product it installed (per machine):
//
//   HKLM\SOFTWARE\Classes\Installer\UpgradeCodes\<upgrade code, packed>      a value per related product
//   HKLM\SOFTWARE\Classes\Installer\Products\<product code, packed>          ProductName, PackageCode, ...
//   HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Installer\UserData\S-1-5-18\Products\<packed>\InstallProperties
//                                                                            DisplayVersion, InstallLocation, ...
//
// The registry is the stack's (a seeded installation carries these keys where its export had them), so an
// app asking which version of its product is installed gets the answer Windows gave; where none is
// registered it gets what Windows gives then, "no related products" and "unknown product".

using Microsoft.Win32;

namespace WpfWebGpu.Win32;

public static unsafe partial class Exports
{
    // msi.dll returns its error codes (UINT) rather than setting the last error
    private const uint MsiSuccess = 0, MsiInvalidParameter = 87, MsiMoreData = 234, MsiNoMoreItems = 259,
        MsiUnknownProduct = 1605, MsiUnknownProperty = 1608;

    private const string InstallerClasses = @"SOFTWARE\Classes\Installer";
    private const string MachineProducts = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Installer\UserData\S-1-5-18\Products";

    [Win32Export("MsiEnumRelatedProductsW")]
    public static uint MsiEnumRelatedProductsW(char* upgradeCode, uint reserved, uint index, char* productBuffer)
    {
        if (reserved != 0 || productBuffer == null || PackedGuid(upgradeCode == null ? null : new string(upgradeCode)) is not string packed)
            return MsiInvalidParameter;
        using RegistryKey? key = Registry.LocalMachine.OpenSubKey($@"{InstallerClasses}\UpgradeCodes\{packed}");
        // the related products, each a value named by its packed product code, in the order the key keeps them
        string[] related = key?.GetValueNames().Where(n => UnpackedGuid(n) != null).ToArray() ?? Array.Empty<string>();
        if (index >= related.Length) return MsiNoMoreItems;
        WriteUtf16(productBuffer, 39, UnpackedGuid(related[index])!);
        return MsiSuccess;
    }

    [Win32Export("MsiGetProductInfoW")]
    public static uint MsiGetProductInfoW(char* product, char* property, char* valueBuffer, uint* valueLength)
    {
        if (product == null || property == null || (valueBuffer != null && valueLength == null)) return MsiInvalidParameter;
        if (PackedGuid(new string(product)) is not string packed) return MsiInvalidParameter;

        using RegistryKey? advertised = Registry.LocalMachine.OpenSubKey($@"{InstallerClasses}\Products\{packed}");
        using RegistryKey? installed = Registry.LocalMachine.OpenSubKey($@"{MachineProducts}\{packed}\InstallProperties");
        if (advertised == null && installed == null) return MsiUnknownProduct;

        string name = new string(property);
        string? value = name switch
        {
            // what the advertisement holds
            "ProductName" or "PackageCode" or "Language" or "AssignmentType" or "ProductIcon" or "Transforms" or "Version" or "InstanceType"
                => Text(advertised?.GetValue(name)) ?? (name == "Version" ? Text(installed?.GetValue("Version")) : null),
            "PackageName" => Text(advertised?.OpenSubKey("SourceList")?.GetValue("PackageName")),
            // what the installation recorded (msi.dll's INSTALLPROPERTY_* names, some stored under other names)
            "VersionString" => Text(installed?.GetValue("DisplayVersion")),
            "InstalledProductName" => Text(installed?.GetValue("DisplayName")),
            "Publisher" or "InstallLocation" or "InstallSource" or "InstallDate" or "LocalPackage" or "HelpLink" or "HelpTelephone"
                or "URLInfoAbout" or "URLUpdateInfo" or "VersionMajor" or "VersionMinor" or "ProductID" or "RegCompany" or "RegOwner"
                => Text(installed?.GetValue(name)),
            _ => null,
        };
        if (value == null) return MsiUnknownProperty;

        // the length asked for, or the value: a count without the terminator, ERROR_MORE_DATA where it does not fit
        if (valueLength == null) return MsiSuccess;
        if (valueBuffer == null) { *valueLength = (uint)value.Length; return MsiSuccess; }
        if (*valueLength <= value.Length)
        {
            if (*valueLength > 0) WriteUtf16(valueBuffer, (int)*valueLength, value);
            *valueLength = (uint)value.Length;
            return MsiMoreData;
        }
        WriteUtf16(valueBuffer, (int)*valueLength, value);
        *valueLength = (uint)value.Length;
        return MsiSuccess;
    }

    private static string? Text(object? value) => value switch
    {
        null => null,
        int i => ((uint)i).ToString(System.Globalization.CultureInfo.InvariantCulture),
        _ => value.ToString(),
    };

    /// <summary>
    /// A GUID as the Installer keys it: {12345678-9ABC-DEF0-1234-56789ABCDEF0} as 87654321CBA90FED21436587A9CBED0F
    /// -- the first three groups reversed, the rest byte by byte with each byte's two digits swapped. Null for a
    /// string that is not a braced GUID.
    /// </summary>
    internal static string? PackedGuid(string? guid)
    {
        if (guid is not { Length: 38 } || guid[0] != '{' || guid[37] != '}' || !Guid.TryParseExact(guid, "B", out _)) return null;
        string hex = guid.Substring(1, 36).Replace("-", "").ToUpperInvariant();
        var packed = new System.Text.StringBuilder(32);
        packed.Append(Reverse(hex[..8])).Append(Reverse(hex[8..12])).Append(Reverse(hex[12..16]));
        for (int i = 16; i < 32; i += 2) packed.Append(hex[i + 1]).Append(hex[i]);
        return packed.ToString();
    }

    /// <summary>The braced GUID a packed one stands for; null for a name that is not one.</summary>
    internal static string? UnpackedGuid(string packed)
    {
        if (packed.Length != 32 || !packed.All(Uri.IsHexDigit)) return null;
        string p = packed.ToUpperInvariant();
        var hex = new System.Text.StringBuilder(32);
        hex.Append(Reverse(p[..8])).Append(Reverse(p[8..12])).Append(Reverse(p[12..16]));
        for (int i = 16; i < 32; i += 2) hex.Append(p[i + 1]).Append(p[i]);
        string h = hex.ToString();
        return $"{{{h[..8]}-{h[8..12]}-{h[12..16]}-{h[16..20]}-{h[20..]}}}";
    }

    private static string Reverse(string s) { var a = s.ToCharArray(); Array.Reverse(a); return new string(a); }
}
