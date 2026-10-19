using WpfMac.Bootstrap.Win32;
using Xunit;

namespace WpfMac.Tests;

/// <summary>
/// Which of an app's assemblies get the Direct3D shim's d3d11/dxgi/d3d9 on Windows (WPF_D3D11_SHIM;
/// WpfMac.Bootstrap's Direct3DShim). Off Windows every assembly does: there is no other Direct3D.
/// </summary>
public class Direct3DShimTests
{
    private static void With(string? spec, Action body)
    {
        string? was = Environment.GetEnvironmentVariable("WPF_D3D11_SHIM");
        Environment.SetEnvironmentVariable("WPF_D3D11_SHIM", spec);
        try
        {
            Direct3DShim.Register((_, _, _) => { });   // reads the setting (the exports' assembly is not here)
            body();
        }
        finally { Environment.SetEnvironmentVariable("WPF_D3D11_SHIM", was); }
    }

    [Fact]
    public void By_default_SharpDX_and_DXEngine_are_redirected_and_nothing_else()
        => With(null, () =>
        {
            Assert.False(Direct3DShim.Disabled);
            Assert.True(Direct3DShim.Covers("SharpDX.Direct3D11"));
            Assert.True(Direct3DShim.Covers("sharpdx"));
            Assert.True(Direct3DShim.Covers("Ab3d.DXEngine.Wpf"));
            Assert.False(Direct3DShim.Covers("SharpMediaFoundationInterop.WPF"));
            Assert.False(Direct3DShim.Covers("MyApp"));
        });

    [Fact]
    public void A_list_names_exact_assemblies_and_prefixes()
        => With("Foo;Bar*", () =>
        {
            Assert.True(Direct3DShim.Covers("Foo"));
            Assert.False(Direct3DShim.Covers("FooBar"));
            Assert.True(Direct3DShim.Covers("Bar.Rendering"));
            Assert.False(Direct3DShim.Covers("SharpDX.Direct3D11"));
        });

    [Fact]
    public void All_covers_every_assembly_and_0_turns_it_off()
    {
        With("all", () => Assert.True(Direct3DShim.Covers("Anything")));
        With("0", () => Assert.True(Direct3DShim.Disabled));
    }
}
