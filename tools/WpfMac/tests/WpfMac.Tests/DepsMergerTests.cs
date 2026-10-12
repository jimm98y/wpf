using System.Reflection;
using System.Reflection.Emit;
using System.Text.Json.Nodes;
using Xunit;

namespace WpfMac.Tests;

public class DepsMergerTests
{
    private const string Target = ".NETCoreApp,Version=v10.0";

    /// <summary>A real (empty) assembly with the given identity, as the merger reads versions from the file.</summary>
    private static void Emit(string path, string name, string version, string fileVersion)
    {
        var builder = new PersistedAssemblyBuilder(new AssemblyName(name) { Version = Version.Parse(version) }, typeof(object).Assembly);
        builder.SetCustomAttribute(new CustomAttributeBuilder(typeof(AssemblyFileVersionAttribute).GetConstructor(new[] { typeof(string) })!, new object[] { fileVersion }));
        builder.DefineDynamicModule(name).DefineType("Marker", TypeAttributes.Public).CreateType();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        builder.Save(path);
    }

    private sealed class Layout
    {
        public string AppDir = TestEnvironment.NewDirectory("app");
        public string PayloadDir = Path.Combine(TestEnvironment.NewDirectory("overlay"), Locations.PayloadName);
        public JsonObject AppTarget = new();
        public JsonObject AppLibraries = new();
        public JsonObject PayloadTarget = new();
        public JsonObject PayloadLibraries = new();

        public void AppPackage(string id, string version, string asmVersion, string fileVersion)
        {
            Emit(Path.Combine(AppDir, id + ".dll"), id, asmVersion, fileVersion);
            AppTarget[$"{id}/{version}"] = new JsonObject { ["runtime"] = new JsonObject { [$"lib/net10.0/{id}.dll"] = new JsonObject { ["assemblyVersion"] = asmVersion, ["fileVersion"] = fileVersion } } };
            AppLibraries[$"{id}/{version}"] = new JsonObject { ["type"] = "package" };
        }

        public void PayloadLibrary(string id, string version, string type, string asmVersion, string fileVersion, string? declaredFileVersion = null)
        {
            Directory.CreateDirectory(PayloadDir);
            Emit(Path.Combine(PayloadDir, id + ".dll"), id, asmVersion, fileVersion);
            PayloadTarget[$"{id}/{version}"] = new JsonObject { ["runtime"] = new JsonObject { [type == "package" ? $"lib/net10.0/{id}.dll" : $"{id}.dll"] = new JsonObject { ["assemblyVersion"] = asmVersion, ["fileVersion"] = declaredFileVersion ?? fileVersion } } };
            PayloadLibraries[$"{id}/{version}"] = new JsonObject { ["type"] = type };
        }

        public (JsonObject Deps, DepsMerger Merger) Merge(string rid = "osx-arm64")
        {
            Directory.CreateDirectory(PayloadDir);
            File.WriteAllText(Path.Combine(AppDir, "App.dll"), "");
            File.WriteAllText(Path.Combine(AppDir, "App.runtimeconfig.json"), "{}");
            var app = AppFiles.Resolve(Path.Combine(AppDir, "App.dll"));
            var appDeps = new JsonObject { ["runtimeTarget"] = new JsonObject { ["name"] = Target }, ["targets"] = new JsonObject { [Target] = AppTarget }, ["libraries"] = AppLibraries };
            var payloadDeps = new JsonObject { ["runtimeTarget"] = new JsonObject { ["name"] = Target + "/" + rid }, ["targets"] = new JsonObject { [Target + "/" + rid] = PayloadTarget }, ["libraries"] = PayloadLibraries };
            var merger = new DepsMerger(rid);
            return (merger.Merge(app, appDeps, PayloadDir, payloadDeps), merger);
        }
    }

    private static JsonObject Lib(JsonObject deps, string key) => (JsonObject)deps["targets"]![Target]![key]!;

    private static IEnumerable<string> OverlayRuntime(JsonObject deps) => Lib(deps, DepsMerger.OverlayLibrary)["runtime"]!.AsObject().Select(kv => kv.Key);

    [Fact]
    public void A_stack_owned_assembly_replaces_the_apps_copy_whatever_its_version()
    {
        var l = new Layout();
        l.AppPackage("System.Management", "10.0.7", "10.0.0.7", "10.0.725.0");
        l.PayloadLibrary("System.Management", "10.0.10-dev", "project", "10.0.0.0", "10.0.0.0");
        var (deps, merger) = l.Merge();
        Assert.Null(Lib(deps, "System.Management/10.0.7")["runtime"]);
        Assert.Contains("System.Management.dll", OverlayRuntime(deps));
        Assert.Contains("System.Management", merger.Replaced);
    }

    [Fact]
    public void Between_two_ordinary_packages_the_newer_wins_the_app()
    {
        var l = new Layout();
        l.AppPackage("Newtonsoft.Json", "13.0.3", "13.0.0.0", "13.0.3.27908");
        l.PayloadLibrary("Newtonsoft.Json", "13.0.1", "package", "13.0.0.0", "13.0.1.25517");
        var (deps, _) = l.Merge();
        Assert.NotNull(Lib(deps, "Newtonsoft.Json/13.0.3")["runtime"]);
        Assert.DoesNotContain("Newtonsoft.Json.dll", OverlayRuntime(deps));
    }

    [Fact]
    public void Between_two_ordinary_packages_the_newer_wins_the_overlay()
    {
        var l = new Layout();
        l.AppPackage("System.CodeDom", "10.0.0", "10.0.0.0", "10.0.25.0");
        l.PayloadLibrary("System.CodeDom", "10.0.10", "package", "10.0.0.0", "10.0.1025.0");
        var (deps, merger) = l.Merge();
        Assert.Null(Lib(deps, "System.CodeDom/10.0.0")["runtime"]);
        Assert.Contains("System.CodeDom.dll", OverlayRuntime(deps));
        Assert.Contains("System.CodeDom", merger.Replaced);
    }

    [Fact]
    public void A_package_asset_the_SDK_overwrote_counts_as_the_stacks()
    {
        // Declared 10.0.25 in the payload's deps.json, but the file is a shim (99.x): stack-owned.
        var l = new Layout();
        l.AppPackage("System.Drawing.Common", "10.0.5", "10.0.0.0", "10.0.525.0");
        l.PayloadLibrary("System.Drawing.Common", "10.0.0", "package", "10.0.0.0", "99.0.0.0", declaredFileVersion: "10.0.25.0");
        var (deps, merger) = l.Merge();
        Assert.Contains("System.Drawing.Common", merger.Replaced);
        Assert.Contains("System.Drawing.Common.dll", OverlayRuntime(deps));
    }

    [Fact]
    public void Another_platforms_runtimeTargets_are_left_out()
    {
        var l = new Layout();
        Emit(Path.Combine(l.AppDir, "runtimes/win/lib/net10.0/Lib.dll"), "Lib", "1.0.0.0", "1.0.0.0");
        Emit(Path.Combine(l.AppDir, "runtimes/unix/lib/net10.0/Lib.dll"), "Lib", "1.0.0.0", "1.0.0.0");
        l.AppTarget["Lib/1.0.0"] = new JsonObject
        {
            ["runtimeTargets"] = new JsonObject
            {
                ["runtimes/win/lib/net10.0/Lib.dll"] = new JsonObject { ["rid"] = "win", ["assetType"] = "runtime" },
                ["runtimes/unix/lib/net10.0/Lib.dll"] = new JsonObject { ["rid"] = "unix", ["assetType"] = "runtime" },
            },
        };
        l.AppLibraries["Lib/1.0.0"] = new JsonObject { ["type"] = "package" };
        var (deps, _) = l.Merge();
        var kept = Lib(deps, "Lib/1.0.0")["runtimeTargets"]!.AsObject().Select(kv => kv.Key).ToArray();
        Assert.Equal(new[] { "runtimes/unix/lib/net10.0/Lib.dll" }, kept);
    }

    [Fact]
    public void App_libraries_resolve_from_the_app_folder_and_the_overlay_from_its_own()
    {
        var l = new Layout();
        l.AppPackage("Some.Lib", "1.0.0", "1.0.0.0", "1.0.0.0");
        var (deps, _) = l.Merge();
        Assert.Equal(Path.GetFileName(l.AppDir), (string?)deps["libraries"]!["Some.Lib/1.0.0"]!["path"]);
        Assert.Equal("package", (string?)deps["libraries"]!["Some.Lib/1.0.0"]!["type"]);
        Assert.Equal(Locations.PayloadName, (string?)deps["libraries"]![DepsMerger.OverlayLibrary]!["path"]);
        // A flat app folder: lib/net10.0/X.dll is found at X.dll, which is what the deps now says.
        Assert.Equal("Some.Lib.dll", Lib(deps, "Some.Lib/1.0.0")["runtime"]!.AsObject().Single().Key);
    }

    [Fact]
    public void Native_files_beside_the_payload_are_put_on_the_native_path()
    {
        var l = new Layout();
        Directory.CreateDirectory(l.PayloadDir);
        File.WriteAllBytes(Path.Combine(l.PayloadDir, "libwgpu_native.dylib"), new byte[] { 0xCF, 0xFA, 0xED, 0xFE });
        var (deps, _) = l.Merge();
        Assert.Contains("libwgpu_native.dylib", Lib(deps, DepsMerger.OverlayLibrary)["native"]!.AsObject().Select(kv => kv.Key));
    }

    [Theory]
    [InlineData("osx-arm64", new[] { "osx-arm64", "osx", "unix", "any" })]
    [InlineData("linux-x64", new[] { "linux-x64", "linux", "unix", "any" })]
    [InlineData("win-x64", new[] { "win-x64", "win", "any" })]
    public void RidChain_is_the_portable_chain(string rid, string[] expected) => Assert.Equal(expected, HostInfo.RidChain(rid));
}
