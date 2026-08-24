// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// Description:
//      Loads the Chrome DevTools Protocol inspector, if the app ships it and asks for it.
//
//      On Windows the visual tree is explored through Visual Studio's Live Visual Tree, which
//      an app exposes over the IVisualTreeService3/XamlDiagnostics COM contract. There is no
//      Visual Studio on the other heads, so the inspector is a CDP endpoint that any Chrome
//      can attach to instead. It lives in Microsoft.Wpf.DevTools, which references
//      PresentationFramework, so it can only be reached from here by reflection -- the same
//      arrangement DUCE.ManagedComposition uses to reach the WebGPU renderer.
//
//      Off unless WPF_DEVTOOLS is set, and a no-op when the assembly is not deployed.

using System.Reflection;

namespace System.Windows.Diagnostics
{
    internal static class DevToolsBootstrap
    {
        private const string EnableEnvVar = "WPF_DEVTOOLS";
        private const string DevToolsAssembly = "Microsoft.Wpf.DevTools";
        private const string DevToolsType = "Microsoft.Wpf.DevTools.DevToolsServer";
        private const string StartMethod = "Start";

        private const int DefaultPort = 9222;

        private static bool s_attempted;

        private static bool IsOff(string value)
        {
            return string.Equals(value, "0", StringComparison.Ordinal) ||
                   string.Equals(value, "false", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(value, "off", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(value, "no", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        ///     Start the inspector, at most once per process. Called when a top-level source
        ///     appears, because that is the first moment there is a tree worth inspecting and
        ///     the one path every head shares.
        /// </summary>
        internal static void EnsureStarted()
        {
            if (s_attempted)
            {
                return;
            }

            s_attempted = true;

            string setting = Environment.GetEnvironmentVariable(EnableEnvVar);
            if (string.IsNullOrEmpty(setting) || IsOff(setting))
            {
                return;
            }

            // WPF_DEVTOOLS=9333 names a port; anything else that got this far means "on, default
            // port". Note that "1" is handled as an on-switch BEFORE it is handled as a number:
            // it parses as a perfectly valid port, so leaving it to TryParse would turn the
            // ordinary way of enabling a feature into a request to bind port 1.
            int port = DefaultPort;
            if (int.TryParse(setting, out int parsed) && parsed > 1 && parsed <= 65535)
            {
                port = parsed;
            }

            try
            {
                Assembly asm = LoadDevTools();
                if (asm == null)
                {
                    Report($"{DevToolsAssembly}.dll was not found. Reference the package, or drop the assembly next to the app.");
                    return;
                }

                Type type = asm.GetType(DevToolsType, throwOnError: false);
                MethodInfo start = type?.GetMethod(StartMethod, BindingFlags.Public | BindingFlags.Static,
                                                   binder: null, types: new[] { typeof(int) }, modifiers: null);
                if (start == null)
                {
                    // The bootstrap binds Start(int) BY NAME across an assembly boundary, so a
                    // rename on either side turns the inspector off rather than failing a build.
                    // Say so loudly; a test pins the contract.
                    Report($"{DevToolsType}.{StartMethod}(int) was not found; the inspector's entry point has moved.");
                    return;
                }

                start.Invoke(null, new object[] { port });
            }
            catch (Exception e)
            {
                // Never fatal: an inspector that will not start must not stop the app from
                // showing its window. But it asked to be started, so it does not fail silently.
                Report($"{e.GetType().Name}: {e.Message}");
            }
        }

        /// <summary>
        ///     Assembly.Load first, then the application directory.
        ///
        ///     The second half is not belt-and-braces. Running an app as `dotnet App.dll` builds
        ///     the assembly list from App.deps.json, so an assembly that is merely PRESENT in the
        ///     folder -- which is exactly how you would add an inspector to an app you do not want
        ///     to modify -- is invisible to Assembly.Load. Probing beside the app is what makes
        ///     "drop it in and set the variable" work for an app that never referenced it.
        /// </summary>
        private static Assembly LoadDevTools()
        {
            try
            {
                return Assembly.Load(DevToolsAssembly);
            }
            catch (Exception e) when (e is System.IO.FileNotFoundException or System.IO.FileLoadException or BadImageFormatException)
            {
            }

            try
            {
                string path = System.IO.Path.Combine(AppContext.BaseDirectory, DevToolsAssembly + ".dll");
                return System.IO.File.Exists(path) ? Assembly.LoadFrom(path) : null;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        ///     stderr, and only ever after WPF_DEVTOOLS was set: someone asked for this, so a
        ///     reason it did not happen is wanted. It stays off everyone else's console.
        /// </summary>
        private static void Report(string message)
        {
            try
            {
                Console.Error.WriteLine("[wpf-devtools] " + message);
            }
            catch
            {
            }
        }
    }
}
