using System.Text.Json.Nodes;
using Xunit;

namespace WpfMac.Tests;

public class RuntimeConfigRewriterTests
{
    private static JsonObject Config(string json) => (JsonObject)JsonNode.Parse(json)!;

    private static readonly Dictionary<string, string> NoProperties = new();

    [Fact]
    public void Drops_WindowsDesktop_and_keeps_NETCore_at_its_version()
    {
        var app = Config("""{ "runtimeOptions": { "tfm": "net10.0", "frameworks": [ { "name": "Microsoft.NETCore.App", "version": "10.0.0" }, { "name": "Microsoft.WindowsDesktop.App", "version": "10.0.0" } ] } }""");
        var result = RuntimeConfigRewriter.Rewrite(app, null, NoProperties);
        var frameworks = result["runtimeOptions"]!["frameworks"]!.AsArray();
        Assert.Single(frameworks);
        Assert.Equal("Microsoft.NETCore.App", (string?)frameworks[0]!["name"]);
    }

    [Fact]
    public void A_lone_WindowsDesktop_framework_becomes_NETCore_at_the_same_version()
    {
        var app = Config("""{ "runtimeOptions": { "framework": { "name": "Microsoft.WindowsDesktop.App", "version": "10.0.3" } } }""");
        var options = RuntimeConfigRewriter.Rewrite(app, null, NoProperties)["runtimeOptions"]!;
        Assert.Null(options["framework"]);
        var only = Assert.Single(options["frameworks"]!.AsArray());
        Assert.Equal("Microsoft.NETCore.App", (string?)only!["name"]);
        Assert.Equal("10.0.3", (string?)only["version"]);
    }

    [Fact]
    public void Other_frameworks_stay()
    {
        var app = Config("""{ "runtimeOptions": { "frameworks": [ { "name": "Microsoft.NETCore.App", "version": "10.0.0" }, { "name": "Microsoft.AspNetCore.App", "version": "10.0.0" }, { "name": "Microsoft.WindowsDesktop.App", "version": "10.0.0" } ] } }""");
        var names = RuntimeConfigRewriter.Rewrite(app, null, NoProperties)["runtimeOptions"]!["frameworks"]!.AsArray().Select(f => (string?)f!["name"]);
        Assert.Equal(new[] { "Microsoft.NETCore.App", "Microsoft.AspNetCore.App" }, names);
    }

    [Fact]
    public void The_apps_properties_win_over_the_payloads_and_ours_win_over_both()
    {
        var app = Config("""{ "runtimeOptions": { "framework": { "name": "Microsoft.NETCore.App", "version": "10.0.0" }, "configProperties": { "A": "app", "B": "app" } } }""");
        var payload = Config("""{ "runtimeOptions": { "configProperties": { "A": "payload", "C": "payload" } } }""");
        var props = RuntimeConfigRewriter.Rewrite(app, payload, new Dictionary<string, string> { ["B"] = "launcher" })["runtimeOptions"]!["configProperties"]!;
        Assert.Equal("app", (string?)props["A"]);
        Assert.Equal("launcher", (string?)props["B"]);
        Assert.Equal("payload", (string?)props["C"]);
    }

    [Fact]
    public void Our_startup_hook_runs_before_the_apps_own()
    {
        var app = Config("""{ "runtimeOptions": { "configProperties": { "STARTUP_HOOKS": "/app/Hook.dll" } } }""");
        var props = RuntimeConfigRewriter.Rewrite(app, null, new Dictionary<string, string> { ["STARTUP_HOOKS"] = "/wpfmac/Boot.dll" })["runtimeOptions"]!["configProperties"]!;
        Assert.Equal("/wpfmac/Boot.dll" + Path.PathSeparator + "/app/Hook.dll", (string?)props["STARTUP_HOOKS"]);
    }

    [Fact]
    public void A_self_contained_app_is_refused()
    {
        var app = Config("""{ "runtimeOptions": { "includedFrameworks": [ { "name": "Microsoft.NETCore.App", "version": "10.0.0" } ] } }""");
        Assert.Throws<UserError>(() => RuntimeConfigRewriter.Rewrite(app, null, NoProperties));
    }

    [Fact]
    public void The_input_is_not_modified()
    {
        var app = Config("""{ "runtimeOptions": { "framework": { "name": "Microsoft.WindowsDesktop.App", "version": "10.0.0" } } }""");
        string before = app.ToJsonString();
        RuntimeConfigRewriter.Rewrite(app, null, new Dictionary<string, string> { ["X"] = "y" });
        Assert.Equal(before, app.ToJsonString());
    }
}
