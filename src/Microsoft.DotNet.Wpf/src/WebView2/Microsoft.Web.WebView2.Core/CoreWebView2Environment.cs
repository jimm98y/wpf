// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// CoreWebView2Environment and its options.
//
// On Windows this is a real object: the environment is what CreateCoreWebView2EnvironmentWithOptions
// produces, and it owns the browser process and the user-data folder shared by every view built from
// it. On the other heads the platform engine has no such notion -- a WKWebView or an
// android.webkit.WebView is created directly -- so the environment is a carrier for the options an
// application supplies, and the backend reads what it can honour.
//
// It is kept because applications construct one explicitly, hold it, and pass it to
// EnsureCoreWebView2Async; removing it would break their source even though nothing behind it
// differs per environment on most heads.
//

using System;
using System.Threading.Tasks;

namespace Microsoft.Web.WebView2.Core
{
    /// <summary>Options for <see cref="CoreWebView2Environment.CreateAsync"/>.</summary>
    public class CoreWebView2EnvironmentOptions
    {
        public CoreWebView2EnvironmentOptions()
        {
        }

        public CoreWebView2EnvironmentOptions(string additionalBrowserArguments = null,
                                              string language = null,
                                              string targetCompatibleBrowserVersion = null,
                                              bool allowSingleSignOnUsingOSPrimaryAccount = false)
        {
            AdditionalBrowserArguments = additionalBrowserArguments;
            Language = language;
            TargetCompatibleBrowserVersion = targetCompatibleBrowserVersion;
            AllowSingleSignOnUsingOSPrimaryAccount = allowSingleSignOnUsingOSPrimaryAccount;
        }

        /// <summary>
        /// Command-line arguments for the browser process. Chromium-specific, and therefore read
        /// only by the Windows head; carried unchanged so an application that sets them is not
        /// rewritten.
        /// </summary>
        public string AdditionalBrowserArguments { get; set; }

        public string Language { get; set; }

        public string TargetCompatibleBrowserVersion { get; set; }

        public bool AllowSingleSignOnUsingOSPrimaryAccount { get; set; }
    }

    /// <summary>The environment a <see cref="CoreWebView2"/> is created in.</summary>
    public class CoreWebView2Environment
    {
        private CoreWebView2Environment(string browserExecutableFolder, string userDataFolder,
                                        CoreWebView2EnvironmentOptions options)
        {
            BrowserExecutableFolder = browserExecutableFolder;
            UserDataFolder = userDataFolder;
            Options = options ?? new CoreWebView2EnvironmentOptions();
        }

        public string BrowserExecutableFolder { get; }

        public string UserDataFolder { get; }

        internal CoreWebView2EnvironmentOptions Options { get; }

        /// <summary>
        /// The engine's version. Answered per head, and never fabricated: where the engine does not
        /// report a version this is empty rather than a plausible-looking number.
        /// </summary>
        public string BrowserVersionString { get; internal set; } = string.Empty;

        public static Task<CoreWebView2Environment> CreateAsync(
            string browserExecutableFolder = null,
            string userDataFolder = null,
            CoreWebView2EnvironmentOptions options = null)
        {
            // Deliberately not creating anything yet. On Windows the real environment is built when
            // the control attaches (the backend owns that two-step handshake); on the other heads
            // there is nothing to build at all. Creating one here would either duplicate the
            // browser process or invent an object with no engine behind it.
            return Task.FromResult(new CoreWebView2Environment(browserExecutableFolder, userDataFolder, options));
        }

        /// <summary>
        /// The version of the installed engine, or null when none is installed.
        /// </summary>
        /// <remarks>
        /// On Windows this is the real thing: the loader exports
        /// GetAvailableCoreWebView2BrowserVersionString, and applications use it exactly to decide
        /// whether to prompt the user to install the Evergreen runtime, so answering from that
        /// export rather than guessing is the whole point of the method.
        ///
        /// Elsewhere there is no separately installed runtime to interrogate -- the engine is part
        /// of the OS -- so this answers null, which is the same thing the real API says when nothing
        /// is available and is what a caller's "should I prompt?" check already handles.
        /// </remarks>
        public static string GetAvailableBrowserVersionString(string browserExecutableFolder = null)
        {
            if (!OperatingSystem.IsWindows())
            {
                return null;
            }

            IntPtr version = IntPtr.Zero;

            try
            {
                if (GetAvailableCoreWebView2BrowserVersionString(browserExecutableFolder, out version) < 0 ||
                    version == IntPtr.Zero)
                {
                    return null;
                }

                return System.Runtime.InteropServices.Marshal.PtrToStringUni(version);
            }
            catch (DllNotFoundException)
            {
                // No loader beside the application: there is no runtime to report.
                return null;
            }
            finally
            {
                if (version != IntPtr.Zero)
                {
                    System.Runtime.InteropServices.Marshal.FreeCoTaskMem(version);
                }
            }
        }

        [System.Runtime.InteropServices.DllImport(
            "WebView2Loader.dll",
            CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        private static extern int GetAvailableCoreWebView2BrowserVersionString(
            string browserExecutableFolder, out IntPtr versionInfo);
    }
}
