// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// Desktop appearance: whether the user has asked for a dark theme, and notification when that
// changes.
//
// The cross-desktop answer is xdg-desktop-portal's Settings interface, reading
// org.freedesktop.appearance / color-scheme. That is the same source GTK 4, Qt 6, Firefox and
// Chromium consult, so WPF agrees with the rest of the session, and it works inside a Flatpak
// sandbox where gsettings would not.
//
// Three fallbacks, because the portal API changed shape over time and not every desktop ships one:
//   1. Settings.ReadOne  -- current API, returns v(u)
//   2. Settings.Read     -- older API, returns v(v(u)); kept because portals below 0.15 only have it
//   3. gsettings         -- GNOME-only, but works with no portal installed at all
//   4. light             -- the historical off-Windows answer
//

using System;
using System.Diagnostics;
using System.Runtime.Versioning;

namespace MS.Internal.Interop.Wayland
{
    [SupportedOSPlatform("linux")]
    internal static class LinuxDesktopSettings
    {
        private const string PortalService = "org.freedesktop.portal.Desktop";
        private const string PortalPath = "/org/freedesktop/portal/desktop";
        private const string SettingsInterface = "org.freedesktop.portal.Settings";
        private const string AppearanceNamespace = "org.freedesktop.appearance";
        private const string ColorSchemeKey = "color-scheme";

        // org.freedesktop.appearance color-scheme: 0 = no preference, 1 = prefer dark, 2 = prefer light.
        private const uint ColorSchemePreferDark = 1;

        private static bool s_queried;
        private static bool s_isDark;
        private static bool s_watching;

        /// <summary>Raised (on the pump thread) when the desktop's colour scheme changes.</summary>
        internal static event Action? SystemAppearanceChanged;

        internal static bool IsSystemDarkTheme()
        {
            if (s_queried) return s_isDark;
            s_queried = true;
            s_isDark = QueryColorScheme();
            return s_isDark;
        }

        private static bool QueryColorScheme()
        {
            try
            {
                if (DBusLite.IsAvailable)
                {
                    if (DBusLite.CallReadUInt32Variant(PortalService, PortalPath, SettingsInterface, "ReadOne",
                            new[] { AppearanceNamespace, ColorSchemeKey }, out uint scheme))
                    {
                        return scheme == ColorSchemePreferDark;
                    }

                    if (DBusLite.CallReadUInt32Variant(PortalService, PortalPath, SettingsInterface, "Read",
                            new[] { AppearanceNamespace, ColorSchemeKey }, out scheme))
                    {
                        return scheme == ColorSchemePreferDark;
                    }
                }
            }
            catch { }

            return QueryGSettings();
        }

        /// <summary>
        /// GNOME's own setting, for a session with no portal. Shelling out is acceptable HERE and
        /// nowhere else in this port: it runs at most once, reads a single word, and the alternative
        /// is being permanently wrong about the user's theme.
        /// </summary>
        private static bool QueryGSettings()
        {
            try
            {
                using var process = Process.Start(new ProcessStartInfo
                {
                    FileName = "gsettings",
                    Arguments = "get org.gnome.desktop.interface color-scheme",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                });
                if (process is null) return false;
                if (!process.WaitForExit(2000))
                {
                    try { process.Kill(); } catch { }
                    return false;
                }
                string output = process.StandardOutput.ReadToEnd();
                return output.Contains("prefer-dark", StringComparison.Ordinal);
            }
            catch
            {
                return false;   // no gsettings: light, the historical answer
            }
        }

        /// <summary>
        /// Subscribe to live colour-scheme changes. The signal is dispatched from the WPF pump (see
        /// WaylandDisplay.ReadEvents), so no background thread and no cross-thread marshalling.
        /// </summary>
        internal static void EnsureWatching()
        {
            if (s_watching || !DBusLite.IsAvailable) return;
            s_watching = true;

            DBusLite.AddMatch($"type='signal',interface='{SettingsInterface}',member='SettingChanged'");
        }

        /// <summary>Handle one incoming D-Bus signal; called by the pump for every signal received.</summary>
        internal static void OnSignal(string iface, string member, IntPtr message)
        {
            if (!string.Equals(iface, SettingsInterface, StringComparison.Ordinal) ||
                !string.Equals(member, "SettingChanged", StringComparison.Ordinal))
            {
                return;
            }

            // SettingChanged(s namespace, s key, v value)
            System.Collections.Generic.List<string> args = DBusLite.GetStringArgs(message);
            if (args.Count < 2 ||
                !string.Equals(args[0], AppearanceNamespace, StringComparison.Ordinal) ||
                !string.Equals(args[1], ColorSchemeKey, StringComparison.Ordinal))
            {
                return;
            }

            bool wasDark = s_isDark;
            if (DBusLite.TryGetUInt32Arg(message, 2, out uint scheme))
            {
                s_isDark = scheme == ColorSchemePreferDark;
            }
            else
            {
                s_isDark = QueryColorScheme();
            }
            s_queried = true;

            if (s_isDark != wasDark)
            {
                SystemAppearanceChanged?.Invoke();
            }
        }
    }
}
