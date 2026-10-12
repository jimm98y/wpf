extern alias RegistryShim;

using System.Text;
using RegistryShim::Microsoft.Win32;
using Xunit;

namespace WpfMac.Tests;

public class RegFileTests
{
    [Theory]
    [InlineData("\"plain\"", "plain", RegistryValueKind.String)]
    [InlineData("\"C:\\\\dir\\\\\"", "C:\\dir\\", RegistryValueKind.String)]
    [InlineData("\"say \\\"hi\\\"\"", "say \"hi\"", RegistryValueKind.String)]
    [InlineData("hex(2):25,00,54,00,4d,00,50,00,25,00,00,00", "%TMP%", RegistryValueKind.ExpandString)]
    public void Parses_strings(string data, string expected, RegistryValueKind kind)
    {
        var (value, k) = RegFile.ParseData(data);
        Assert.Equal(expected, value);
        Assert.Equal(kind, k);
    }

    [Fact]
    public void Parses_numbers_binary_and_multi_strings()
    {
        Assert.Equal((-1, RegistryValueKind.DWord), RegFile.ParseData("dword:ffffffff"));
        Assert.Equal((10_000_000_000L, RegistryValueKind.QWord), RegFile.ParseData("hex(b):00,e4,0b,54,02,00,00,00"));
        var (bin, kind) = RegFile.ParseData("hex:de,ad,be,ef");
        Assert.Equal(RegistryValueKind.Binary, kind);
        Assert.Equal(new byte[] { 0xde, 0xad, 0xbe, 0xef }, bin);
        var (multi, mkind) = RegFile.ParseData("hex(7):61,00,00,00,62,00,63,00,00,00,00,00");
        Assert.Equal(RegistryValueKind.MultiString, mkind);
        Assert.Equal(new[] { "a", "bc" }, multi);
    }

    [Theory]
    [InlineData("text", RegistryValueKind.String)]
    [InlineData(42, RegistryValueKind.DWord)]
    [InlineData(10_000_000_000L, RegistryValueKind.QWord)]
    public void Formats_what_it_parses(object value, RegistryValueKind kind)
    {
        var (back, backKind) = RegFile.ParseData(RegFile.FormatData(value, kind));
        Assert.Equal(value, back);
        Assert.Equal(kind, backKind);
    }

    [Fact]
    public void Imports_a_regedit_file_into_the_registry_apps_see()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "on Windows the shim is the real registry");
        string key = "Software\\WpfMacTests\\" + Guid.NewGuid().ToString("N");
        string text = $"""
            Windows Registry Editor Version 5.00

            ; a comment
            [HKEY_CURRENT_USER\{key}]
            @="default"
            "Name"="value"
            "Count"=dword:00000007
            "Blob"=hex:01,02,03,\
              04,05
            "Gone"="x"
            "Gone"=-

            [HKEY_CURRENT_USER\{key}\Child]
            "Flag"=dword:00000001

            [-HKEY_CURRENT_USER\{key}\Child]
            """;
        var (keys, values, deleted) = RegFile.Import(text);
        // Keys: the key and Child (its deletion is counted as one); values: all six assignments.
        Assert.Equal((2, 6, 2), (keys, values, deleted));

        using RegistryKey k = Registry.CurrentUser.OpenSubKey(key)!;
        Assert.Equal("default", k.GetValue(""));
        Assert.Equal("value", k.GetValue("Name"));
        Assert.Equal(7, k.GetValue("Count"));
        Assert.Equal(new byte[] { 1, 2, 3, 4, 5 }, k.GetValue("Blob"));
        Assert.Null(k.GetValue("Gone"));
        Assert.Empty(k.GetSubKeyNames());
    }

    [Fact]
    public void An_export_imports_back_to_the_same_export()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "on Windows the shim is the real registry");
        string key = "Software\\WpfMacTests\\" + Guid.NewGuid().ToString("N");
        using (RegistryKey k = Registry.CurrentUser.CreateSubKey(key))
        {
            k.SetValue("S", "a \"quoted\" C:\\path\\");
            k.SetValue("D", 5);
            k.SetValue("Q", 1L << 40);
            k.SetValue("B", new byte[] { 9, 8, 7 });
            k.SetValue("M", new[] { "x", "y" });
            k.CreateSubKey("Sub").SetValue("", "default");
        }
        string dir = TestEnvironment.NewDirectory("reg");
        string first = Path.Combine(dir, "1.reg"), second = Path.Combine(dir, "2.reg");
        Assert.Equal(2, RegFile.Export("HKCU\\" + key, first));

        string text = File.ReadAllText(first);
        Assert.StartsWith("Windows Registry Editor Version 5.00", text);
        Assert.Contains($"[HKEY_CURRENT_USER\\{key}]", text);         // full hive names, as regedit requires
        Assert.Equal(new byte[] { 0xFF, 0xFE }, File.ReadAllBytes(first)[..2]);   // UTF-16LE, as regedit writes

        Registry.CurrentUser.DeleteSubKeyTree(key);
        RegFile.Import(text);
        RegFile.Export("HKEY_CURRENT_USER\\" + key, second);
        Assert.Equal(File.ReadAllBytes(first), File.ReadAllBytes(second));
    }

    [Fact]
    public void Rejects_what_is_not_a_reg_file() =>
        Assert.Throws<UserError>(() => RegFile.Import("[HKEY_CURRENT_USER\\X]\n\"a\"=\"b\""));
}
