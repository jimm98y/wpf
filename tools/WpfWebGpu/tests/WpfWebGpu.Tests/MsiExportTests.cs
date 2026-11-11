extern alias RegistryShim;

using System.Reflection;
using RegistryShim::Microsoft.Win32;
using Xunit;

namespace WpfWebGpu.Tests;

// msi's product queries in the stack's user-mode Windows (WpfWebGpu.Win32/Msi.cs), answered from the registry the
// way msi.dll answers them: the Installer's keys of a product, packed GUIDs and all.
public unsafe class MsiExportTests
{
    // Office 2007's product code, whose packed form is well known; an upgrade code with every digit different
    private const string Upgrade = "{12345678-9ABC-DEF0-1234-56789ABCDEF0}";
    private const string Product = "{90120000-0030-0000-0000-0000000FF1CE}";

    private static MethodInfo Export(string name) => Win32Exports.Method(name);

    private static uint Enum(string upgrade, uint index, out string product)
    {
        char* buffer = stackalloc char[39];
        fixed (char* code = upgrade)
        {
            uint r = (uint)Export("MsiEnumRelatedProductsW").Invoke(null, new object[] { Pointer.Box(code, typeof(char*)), 0u, index, Pointer.Box(buffer, typeof(char*)) })!;
            product = r == 0 ? new string(buffer) : "";
            return r;
        }
    }

    private static uint Info(string product, string property, uint capacity, out string value, out uint length)
    {
        char* buffer = stackalloc char[64];
        uint size = capacity;
        fixed (char* p = product)
        fixed (char* q = property)
        {
            uint r = (uint)Export("MsiGetProductInfoW").Invoke(null, new object[]
            {
                Pointer.Box(p, typeof(char*)), Pointer.Box(q, typeof(char*)),
                Pointer.Box(capacity == 0 ? null : buffer, typeof(char*)), Pointer.Box(&size, typeof(uint*)),
            })!;
            value = r == 0 && capacity > 0 ? new string(buffer) : "";
            length = size;
            return r;
        }
    }

    [Fact]
    public void A_product_registered_with_the_Installer_is_found_by_its_upgrade_code_and_reports_its_version()
    {
        Assert.Equal(259u, Enum(Upgrade, 0, out _));                                   // nothing related yet: ERROR_NO_MORE_ITEMS
        Assert.Equal(1605u, Info(Product, "VersionString", 64, out _, out _));         // ERROR_UNKNOWN_PRODUCT

        // as the Installer registers a per-machine product (GUIDs packed: groups reversed, then byte by byte swapped)
        const string packedUpgrade = "87654321CBA90FED21436587A9CBED0F";
        const string packedProduct = "00002109030000000000000000F01FEC";
        using (var k = Registry.LocalMachine.CreateSubKey($@"SOFTWARE\Classes\Installer\UpgradeCodes\{packedUpgrade}"))
            k.SetValue(packedProduct, "");
        using (var k = Registry.LocalMachine.CreateSubKey($@"SOFTWARE\Classes\Installer\Products\{packedProduct}"))
            k.SetValue("ProductName", "Product");
        using (var k = Registry.LocalMachine.CreateSubKey($@"SOFTWARE\Microsoft\Windows\CurrentVersion\Installer\UserData\S-1-5-18\Products\{packedProduct}\InstallProperties"))
        {
            k.SetValue("DisplayVersion", "11.1.123.4");
            k.SetValue("VersionMajor", 11, RegistryValueKind.DWord);
        }

        Assert.Equal(0u, Enum(Upgrade, 0, out string found));
        Assert.Equal(Product, found);
        Assert.Equal(259u, Enum(Upgrade, 1, out _));

        Assert.Equal(0u, Info(Product, "VersionString", 64, out string version, out uint length));
        Assert.Equal(("11.1.123.4", 10u), (version, length));
        Assert.Equal(0u, Info(Product, "ProductName", 64, out string name, out _));
        Assert.Equal("Product", name);
        Assert.Equal(0u, Info(Product, "VersionMajor", 64, out string major, out _));
        Assert.Equal("11", major);
        Assert.Equal(1608u, Info(Product, "NoSuchProperty", 64, out _, out _));       // ERROR_UNKNOWN_PROPERTY
        // the length alone, and a buffer too small: the length without the terminator, ERROR_MORE_DATA
        Assert.Equal(0u, Info(Product, "VersionString", 0, out _, out length));
        Assert.Equal(10u, length);
        Assert.Equal(234u, Info(Product, "VersionString", 5, out _, out length));
        Assert.Equal(10u, length);
        Assert.Equal(87u, Enum("not a guid", 0, out _));                              // ERROR_INVALID_PARAMETER
    }
}
