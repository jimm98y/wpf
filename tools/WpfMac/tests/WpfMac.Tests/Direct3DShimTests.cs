using WpfMac.Bootstrap.Win32;
using Xunit;

namespace WpfMac.Tests;

/// <summary>
/// Which of an app's assemblies get the Direct3D shim's d3d11/dxgi/d3d9/d3dcompiler_47 on Windows (WPF_D3D11_SHIM;
/// WpfMac.Bootstrap's Direct3DShim): every one unless the setting narrows it. Off Windows every assembly does.
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

    /// <summary>One implementation on every OS (2026-10-19): by default every app assembly gets the shim on
    /// Windows too, the real Direct3D only by WPF_D3D11_SHIM (a diagnostic and test-oracle switch).</summary>
    [Fact]
    public void By_default_every_app_assembly_is_redirected()
        => With(null, () =>
        {
            Assert.False(Direct3DShim.Disabled);
            Assert.True(Direct3DShim.Covers("SharpDX.Direct3D11"));
            Assert.True(Direct3DShim.Covers("Ab3d.DXEngine.Wpf"));
            Assert.True(Direct3DShim.Covers("SharpMediaFoundationInterop.WPF"));
            Assert.True(Direct3DShim.Covers("MyApp"));
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
