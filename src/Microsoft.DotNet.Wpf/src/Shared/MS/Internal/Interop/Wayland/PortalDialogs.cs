// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// File dialogs via xdg-desktop-portal, shaped like CocoaDialogs so OpenFileDialog/SaveFileDialog
// need only a one-line arm each.
//
// The portal is the right target rather than GTK directly: it is the cross-desktop API (GNOME, KDE
// and the rest each supply their own backend), the dialog is drawn by the DESKTOP rather than by
// us, so it matches the user's file manager and bookmarks, and it is the only thing that works from
// inside a Flatpak sandbox.
//
// The awkward part is that portals are ASYNCHRONOUS while OpenFileDialog.ShowDialog() is not. The
// method call returns a Request handle immediately and the user's answer arrives later as a
// Response signal. Two consequences shape the code below:
//
//   * The match rule must be registered BEFORE the call is sent. Registering it afterwards races
//     the reply: the user could answer a portal dialog before we started listening, and the answer
//     would be dropped.
//
//   * While waiting, the WAYLAND pump has to keep running. Blocking purely on D-Bus would freeze
//     the app's own rendering behind the dialog, and a client that stops responding to the
//     compositor gets flagged unresponsive.
//

using System;
using System.Collections.Generic;
using System.Runtime.Versioning;

namespace MS.Internal.Interop.Wayland
{
    [SupportedOSPlatform("linux")]
    internal static class PortalDialogs
    {
        private const string PortalService = "org.freedesktop.portal.Desktop";
        private const string PortalPath = "/org/freedesktop/portal/desktop";
        private const string FileChooserInterface = "org.freedesktop.portal.FileChooser";
        private const string RequestInterface = "org.freedesktop.portal.Request";

        // Portal response codes.
        private const uint ResponseSuccess = 0;
        private const uint ResponseCancelled = 1;

        private static int s_tokenCounter;

        private sealed class PendingRequest
        {
            public string Path = string.Empty;
            public bool Completed;
            public uint Response = ResponseCancelled;
            public List<string> Uris = new();
        }

        private static PendingRequest? s_pending;

        internal static bool IsAvailable => DBusLite.IsAvailable;

        /// <summary>Show the portal's open dialog. Returns the chosen paths, or an empty array.</summary>
        internal static string[] ShowOpenPanel(string title, string initialDirectory, bool allowMultiple, bool chooseDirectories)
        {
            var options = new List<(string, object)>
            {
                ("multiple", allowMultiple),
                ("directory", chooseDirectories),
            };
            return Run("OpenFile", title, initialDirectory, options);
        }

        /// <summary>Show the portal's save dialog. Returns the chosen path, or null.</summary>
        internal static string? ShowSavePanel(string title, string initialDirectory, string defaultFileName)
        {
            var options = new List<(string, object)>();
            if (!string.IsNullOrEmpty(defaultFileName))
            {
                options.Add(("current_name", defaultFileName));
            }
            string[] result = Run("SaveFile", title, initialDirectory, options);
            return result.Length > 0 ? result[0] : null;
        }

        private static string[] Run(string method, string title, string initialDirectory, List<(string, object)> options)
        {
            if (!DBusLite.IsAvailable) return Array.Empty<string>();

            // Our own handle_token, so the Request path is predictable and can be matched before the
            // call goes out. The portal derives the path from the caller's unique bus name with the
            // dots replaced by underscores.
            string token = "wpf" + (uint)System.Threading.Interlocked.Increment(ref s_tokenCounter) + "_" + Environment.ProcessId;
            options.Add(("handle_token", token));

            string sender = DBusLite.UniqueName.TrimStart(':').Replace('.', '_');
            string expectedPath = $"/org/freedesktop/portal/desktop/request/{sender}/{token}";

            var pending = new PendingRequest { Path = expectedPath };
            s_pending = pending;

            try
            {
                // BEFORE the call: see the file header.
                DBusLite.AddMatch($"type='signal',interface='{RequestInterface}',member='Response',path='{expectedPath}'");

                if (!string.IsNullOrEmpty(initialDirectory))
                {
                    // current_folder is a byte array in the spec; passing it as a string is not
                    // portable across backends, so the directory is left to the portal's own memory
                    // of where the user last was -- which is usually the better answer anyway.
                }

                if (!DBusLite.CallPortalWithOptions(PortalService, PortalPath, FileChooserInterface, method,
                        ParentWindowHandle(), title ?? string.Empty, options.ToArray(), out string actualPath))
                {
                    return Array.Empty<string>();
                }

                if (!string.Equals(actualPath, expectedPath, StringComparison.Ordinal))
                {
                    // The portal chose a different path than predicted (an older implementation).
                    // Match the real one too rather than waiting for a signal that never comes.
                    pending.Path = actualPath;
                    DBusLite.AddMatch($"type='signal',interface='{RequestInterface}',member='Response',path='{actualPath}'");
                }

                // Pump both buses until the user answers. Wayland is pumped alongside D-Bus so the
                // app keeps painting behind the dialog.
                DateTime deadline = DateTime.UtcNow.AddMinutes(10);
                while (!pending.Completed && DateTime.UtcNow < deadline)
                {
                    WaylandDisplay.ReadEvents(16);
                }

                if (pending.Response != ResponseSuccess) return Array.Empty<string>();

                var paths = new List<string>(pending.Uris.Count);
                foreach (string uri in pending.Uris)
                {
                    string? path = UriToPath(uri);
                    if (path is not null) paths.Add(path);
                }
                return paths.ToArray();
            }
            finally
            {
                s_pending = null;
            }
        }

        /// <summary>
        /// Handle one D-Bus signal; called by the pump for every signal received.
        /// </summary>
        internal static void OnSignal(string iface, string member, IntPtr message)
        {
            PendingRequest? pending = s_pending;
            if (pending is null) return;
            if (!string.Equals(iface, RequestInterface, StringComparison.Ordinal) ||
                !string.Equals(member, "Response", StringComparison.Ordinal))
            {
                return;
            }
            if (!string.Equals(DBusLite.GetPath(message), pending.Path, StringComparison.Ordinal)) return;

            if (DBusLite.TryParsePortalResponse(message, "uris", out uint response, out List<string> uris))
            {
                pending.Response = response;
                pending.Uris = uris;
            }
            pending.Completed = true;
        }

        /// <summary>
        /// The parent-window identifier the portal uses to place the dialog. Associating it properly
        /// needs xdg-foreign (an exported surface handle, "wayland:&lt;handle&gt;"); an empty string
        /// means "no parent", which every backend accepts and which shows an unparented dialog.
        /// </summary>
        private static string ParentWindowHandle() => string.Empty;

        /// <summary>file:// URI to a local path, with percent-decoding.</summary>
        private static string? UriToPath(string uri)
        {
            if (string.IsNullOrEmpty(uri)) return null;
            try
            {
                const string filePrefix = "file://";
                if (uri.StartsWith(filePrefix, StringComparison.Ordinal))
                {
                    return Uri.UnescapeDataString(uri[filePrefix.Length..]);
                }
                // A non-file URI (a document-portal or network location) has no local path.
                return uri.StartsWith("/", StringComparison.Ordinal) ? uri : null;
            }
            catch
            {
                return null;
            }
        }
    }
}
