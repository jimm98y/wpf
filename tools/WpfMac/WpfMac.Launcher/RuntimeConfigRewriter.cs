// The runtimeconfig an unmodified app runs with on the WebGPU stack.
//
// Microsoft.WindowsDesktop.App is dropped on EVERY OS, Windows included: it is where stock WPF and
// WinForms come from, and a framework assembly always beats an app-local one, so leaving it in would
// run the app on stock WPF no matter what the deps.json says. Everything the framework provided that
// the app still needs is in the overlay. Microsoft.NETCore.App (and anything else, e.g. ASP.NET Core)
// stays exactly as the app asked for it.

using System.Text.Json.Nodes;

namespace WpfMac;

internal static class RuntimeConfigRewriter
{
    public const string WindowsDesktop = "Microsoft.WindowsDesktop.App";
    public const string NetCore = "Microsoft.NETCore.App";

    public static JsonObject Rewrite(JsonObject appConfig, JsonObject? payloadConfig, IReadOnlyDictionary<string, string> launcherProperties)
    {
        var config = (JsonObject)appConfig.DeepClone();
        var options = config["runtimeOptions"] as JsonObject
            ?? throw new UserError("the app's runtimeconfig.json has no runtimeOptions.");

        if (options["includedFrameworks"] != null)
        {
            throw new UserError("the app is self-contained: it carries its own (Windows) runtime, which cannot run here. Only framework-dependent apps can be launched.");
        }

        // "framework" (one) or "frameworks" (several): normalise to the array form.
        var frameworks = new JsonArray();
        if (options["framework"] is JsonObject single) frameworks.Add(single.DeepClone());
        if (options["frameworks"] is JsonArray many) foreach (var f in many) frameworks.Add(f?.DeepClone());
        options.Remove("framework");

        string? desktopVersion = null;
        var kept = new JsonArray();
        foreach (var f in frameworks)
        {
            string? name = f?["name"]?.GetValue<string>();
            if (string.Equals(name, WindowsDesktop, StringComparison.OrdinalIgnoreCase))
            {
                desktopVersion = f?["version"]?.GetValue<string>();
                continue;
            }
            kept.Add(f?.DeepClone());
        }
        if (!kept.Any(f => string.Equals(f?["name"]?.GetValue<string>(), NetCore, StringComparison.OrdinalIgnoreCase)))
        {
            // WindowsDesktop.App implies NETCore.App at the same version; say so explicitly once it is gone.
            kept.Insert(0, new JsonObject { ["name"] = NetCore, ["version"] = desktopVersion ?? "10.0.0" });
        }
        options["frameworks"] = kept;

        var props = options["configProperties"] as JsonObject ?? new JsonObject();
        options["configProperties"] = props;
        if (payloadConfig?["runtimeOptions"]?["configProperties"] is JsonObject payloadProps)
        {
            foreach (var (key, value) in payloadProps)
            {
                if (!props.ContainsKey(key)) props[key] = value?.DeepClone();
            }
        }
        foreach (var (key, value) in launcherProperties)
        {
            if (key == "STARTUP_HOOKS" && props[key] is JsonValue existing && existing.TryGetValue(out string? hooks) && hooks.Length > 0)
            {
                props[key] = value + Path.PathSeparator + hooks;     // ours first: it fixes BaseDirectory
            }
            else
            {
                props[key] = value;
            }
        }
        return config;
    }
}
