// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#if !WGPU_IOS && !WGPU_BROWSER

using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;

namespace Microsoft.Wpf.Interop.WebGpu
{
    internal static unsafe partial class Wgpu
    {
        // Installing from Wgpu's type initializer rather than a [ModuleInitializer] ties the work to
        // the first use of the P/Invokes themselves: calling any DllImport declared on this type
        // runs the cctor first. A module initializer would run earlier and for callers that never
        // touch the GPU at all, and this repo has already been bitten by one of those firing at an
        // awkward moment (see the DPI-awareness note in HwndSource).
        static Wgpu() => NativeLoad.Install();
    }

    /// <summary>
    /// Finds wgpu-native when it is NOT sitting next to the application.
    ///
    /// The ordinary (RID-specific) build copies one platform's library beside the app, where the
    /// default P/Invoke probing finds it and none of this runs. A PORTABLE publish
    /// (-p:WpfWebGpuPortable=true) cannot do that: one output has to serve Windows, Linux and macOS
    /// on both architectures, and win-x64 and win-arm64 are both called wgpu_native.dll, so the
    /// libraries live in the layout the platform itself distinguishes them by --
    /// runtimes/&lt;rid&gt;/native/ -- and something has to pick.
    ///
    /// The framework normally does that picking from deps.json, but only for libraries that arrived
    /// as NuGet package assets. Ours are laid out by the SDK's own targets, so the resolver below
    /// does it instead. That keeps the portable layout a packaging decision rather than forcing the
    /// native backend to be republished as one runtime package per RID.
    /// </summary>
    internal static class NativeLoad
    {
        private static bool s_installed;

        /// <summary>
        /// Installs the resolver. Idempotent: SetDllImportResolver throws
        /// InvalidOperationException if an assembly is given two resolvers, and this is reachable
        /// from more than one entry point.
        /// </summary>
        internal static void Install()
        {
            if (s_installed) return;
            s_installed = true;

            try
            {
                NativeLibrary.SetDllImportResolver(typeof(Wgpu).Assembly, Resolve);
            }
            catch (InvalidOperationException)
            {
                // Something already installed one for this assembly (the iOS spike does exactly
                // that to redirect to the statically linked image). Theirs wins; the default
                // probing this class adds to is still in place.
            }
        }

        private static IntPtr Resolve(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
        {
            if (!string.Equals(libraryName, Wgpu.Library, StringComparison.Ordinal))
            {
                return IntPtr.Zero;      // not ours: let the default logic run
            }

            // Default probing FIRST, so the RID-specific layout (library beside the app) keeps
            // behaving exactly as it did, and a host that has its own opinion still wins. This does
            // not recurse: TryLoad(name, assembly, path) runs the built-in logic, not this resolver.
            if (NativeLibrary.TryLoad(libraryName, assembly, searchPath, out IntPtr handle))
            {
                return handle;
            }

            string? appDir = AppContext.BaseDirectory;
            if (string.IsNullOrEmpty(appDir)) return IntPtr.Zero;

            foreach (string rid in CandidateRids())
            {
                string dir = Path.Combine(appDir, "runtimes", rid, "native");
                string candidate = Path.Combine(dir, NativeFileName);
                if (!File.Exists(candidate)) continue;

                if (NativeLibrary.TryLoad(candidate, out handle))
                {
                    // ANGLE lives in the same folder and is loaded by wgpu-native itself, BY NAME,
                    // through the OS loader -- which searches the application directory, not this
                    // one. Loading it here by full path puts it in the process under the base name
                    // the later request uses, so that request finds the already-loaded module.
                    // Best effort: a machine with a working D3D12/Vulkan backend never asks.
                    PreloadAngle(dir);
                    return handle;
                }
            }

            return IntPtr.Zero;          // nothing found: the caller reports DllNotFoundException
        }

        /// <summary>The file name the OS loader expects, which differs by more than extension.</summary>
        private static string NativeFileName =>
            RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "wgpu_native.dll"
            : RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? "libwgpu_native.dylib"
            : "libwgpu_native.so";

        /// <summary>
        /// RIDs to try, most specific first. The composed one (win-arm64, linux-x64, ...) is what
        /// the SDK names its folders; $(RuntimeInformation.RuntimeIdentifier) is tried too because a
        /// host may report something more specific (win10-x64) or an alias we did not compose.
        /// </summary>
        private static string[] CandidateRids()
        {
            string os =
                RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "win" :
                RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? "osx" :
                "linux";

            string arch = RuntimeInformation.ProcessArchitecture switch
            {
                Architecture.X64 => "x64",
                Architecture.Arm64 => "arm64",
                Architecture.X86 => "x86",
                Architecture.Arm => "arm",
                _ => RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant(),
            };

            string composed = os + "-" + arch;
            string reported = RuntimeInformation.RuntimeIdentifier;

            return string.Equals(composed, reported, StringComparison.OrdinalIgnoreCase)
                ? new[] { composed }
                : new[] { composed, reported };
        }

        private static void PreloadAngle(string dir)
        {
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return;

            // Order matters only in that libEGL depends on libGLESv2; loading either by full path
            // is enough to satisfy the other's by-name lookup, so both are attempted and neither is
            // required.
            foreach (string name in new[] { "libGLESv2.dll", "libEGL.dll" })
            {
                string path = Path.Combine(dir, name);
                if (File.Exists(path))
                {
                    NativeLibrary.TryLoad(path, out _);
                }
            }
        }
    }
}

#endif
