// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// The detours wpfrun installs in every app it runs on macOS (WpfWebGpu.Detours), installed here as the
// test assembly loads, so the tests see what an app sees: SystemColors from the stack's user32 rather
// than the Windows XP palette .NET compiles in off Windows (a disabled caption drawn in #ACA899 where
// Windows draws #6D6D6D), STA threads, machine-wide folders. Built only where wpfrun's payload exists
// (Wpf.WinFormsInterop.Tests.csproj); without it the runtime's own behaviour stands.
//

#if WPFRUN_DETOURS
namespace Wpf.WinFormsInterop.Tests
{
    internal static class PlatformDetours
    {
        [System.Runtime.CompilerServices.ModuleInitializer]
        internal static void Install()
        {
            string state = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "wpf-winforms-tests-state");
            System.IO.Directory.CreateDirectory(state);
            WpfWebGpu.Detours.Detours.InstallAll(state, (_, message) => System.Console.Error.WriteLine(message));
        }
    }
}
#endif
