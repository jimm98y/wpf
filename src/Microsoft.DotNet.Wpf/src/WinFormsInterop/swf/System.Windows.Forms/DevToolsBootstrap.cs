// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// Starts the CDP visual-tree inspector for a WinForms app, if it ships one and asks for it.
//
// The WPF side has its own copy of this in PresentationCore, triggered from HwndSource. A
// WinForms Form never creates an HwndSource -- it presents through this stack's own host --
// so that trigger never fires here, and the two heads need separate ones.
//
// Duplicated rather than shared because there is nothing to share it through: this assembly
// does not reference PresentationCore, and adding a reference so two heads can agree on an
// environment variable would be a far worse trade than thirty lines. The parsing itself is
// NOT duplicated: both bootstraps call StartFromEnvironment, which owns it.
//
// Off unless WPF_DEVTOOLS is set, and a no-op when the inspector is not deployed.

using System;
using System.Reflection;

namespace System.Windows.Forms
{
	internal static class DevToolsBootstrap
	{
		private const string EnableEnvVar = "WPF_DEVTOOLS";
		private const string DevToolsAssembly = "Microsoft.Wpf.DevTools";
		private const string DevToolsType = "Microsoft.Wpf.DevTools.DevToolsServer";
		private const string StartMethod = "StartFromEnvironment";

		private static bool s_attempted;

		/// <summary>
		/// Start the inspector, at most once per process. Called as the message loop begins,
		/// which is the first moment there is a form tree worth looking at.
		/// </summary>
		internal static void EnsureStarted ()
		{
			if (s_attempted)
				return;

			s_attempted = true;

			// Checked here as well as in StartFromEnvironment so that the common case -- no
			// inspector wanted -- does not load an assembly to find that out.
			if (string.IsNullOrEmpty (Environment.GetEnvironmentVariable (EnableEnvVar)))
				return;

			try {
				Assembly asm = LoadDevTools ();
				MethodInfo start = asm?.GetType (DevToolsType, throwOnError: false)
					?.GetMethod (StartMethod, BindingFlags.Public | BindingFlags.Static,
						     binder: null, types: Type.EmptyTypes, modifiers: null);

				if (start == null) {
					Console.Error.WriteLine ("[wpf-devtools] " + DevToolsAssembly + " was not found or its entry point has moved.");
					return;
				}

				start.Invoke (null, null);
			} catch (Exception e) {
				// Never fatal. An inspector that will not start must not stop the app.
				Console.Error.WriteLine ("[wpf-devtools] " + e.GetType ().Name + ": " + e.Message);
			}
		}

		/// <summary>
		/// Assembly.Load, then the application directory -- running an app as `dotnet App.dll`
		/// builds the assembly list from App.deps.json, so an assembly that is merely PRESENT
		/// beside the app is invisible to Assembly.Load alone.
		/// </summary>
		private static Assembly LoadDevTools ()
		{
			try {
				return Assembly.Load (DevToolsAssembly);
			} catch (Exception e) when (e is System.IO.FileNotFoundException or System.IO.FileLoadException or BadImageFormatException) {
			}

			try {
				string path = System.IO.Path.Combine (AppContext.BaseDirectory, DevToolsAssembly + ".dll");
				return System.IO.File.Exists (path) ? Assembly.LoadFrom (path) : null;
			} catch {
				return null;
			}
		}
	}
}
