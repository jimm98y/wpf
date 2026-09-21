// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// Browser file dialogs, shaped like CocoaDialogs and PortalDialogs so OpenFileDialog and
// SaveFileDialog need only a one-line arm each -- except that these return a Task, because nothing
// in a browser can answer a file picker synchronously and no amount of design here can change that.
//
// Before this file the browser head had no file dialog: CommonItemDialog.RunDialogPortable had arms
// for macOS and Linux and fell through to "return false" everywhere else, so OpenFileDialog.ShowDialog
// reported that the user had cancelled a dialog they were never shown. That is indistinguishable
// from a real cancellation, which is why it could sit there looking like correct behaviour.
//
// What a browser can and cannot do is described above pickFilesAsync in browser-window.js; the
// short version is that a picked file arrives as NAME AND BYTES with no path, and a save is a
// download to a place the page is never told. Both are bridged through the wasm virtual file system
// so that application code keeps working on ordinary paths.
//

using System;
using System.Runtime.InteropServices.JavaScript;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Threading.Tasks;

namespace MS.Internal.Interop
{
    /// <summary>File dialogs for the browser head, callable from any WPF assembly.</summary>
    [SupportedOSPlatform("browser")]
    public static partial class BrowserDialogs
    {
        /// <summary>True when running in a browser, where the JS bridge is usable.</summary>
        public static bool IsAvailable => OperatingSystem.IsBrowser();

        /// <summary>
        ///  Shows the file picker. Returns the chosen paths inside the wasm virtual file system, or
        ///  an empty array when the user cancelled.
        /// </summary>
        /// <param name="accept">
        ///  An HTML accept list ("*.txt,*.csv" style becomes ".txt,.csv"), or null for everything.
        /// </param>
        public static async Task<string[]> ShowOpenPanelAsync(string accept, bool multiple, bool directory)
        {
            if (!IsAvailable) return Array.Empty<string>();

            string json;
            try
            {
                json = await Js.PickFiles(accept ?? string.Empty, multiple, directory).ConfigureAwait(true);
            }
            catch (JSException)
            {
                return Array.Empty<string>();
            }

            return ParseNames(json);
        }

        /// <summary>
        ///  Reserves a path for a save. The caller writes the file there and then calls
        ///  <see cref="OfferDownload"/>, which is the step that actually reaches the user.
        /// </summary>
        public static string ReserveSavePath(string suggestedName)
        {
            if (!IsAvailable) return null;

            try
            {
                return Js.ReserveSavePath(suggestedName ?? string.Empty);
            }
            catch (JSException)
            {
                return null;
            }
        }

        /// <summary>Offers a file the application has written as a browser download.</summary>
        public static bool OfferDownload(string path, string mimeType)
        {
            if (!IsAvailable || string.IsNullOrEmpty(path)) return false;

            try
            {
                return Js.OfferDownload(path, mimeType ?? string.Empty);
            }
            catch (JSException)
            {
                return false;
            }
        }

        /// <summary>
        ///  Turns WPF's filter string into an HTML accept attribute.
        /// </summary>
        /// <remarks>
        ///  WPF filters look like "Text files|*.txt;*.log|All files|*.*": description and patterns
        ///  alternating, patterns separated by semicolons. HTML wants a comma-separated list of
        ///  extensions or MIME types, and has no concept of named filter GROUPS at all -- a browser
        ///  file picker shows one list. So every extension from every group is offered, which is the
        ///  closest honest translation; "*.*" means no restriction and clears the list entirely,
        ///  because an accept list containing ".*" would match nothing.
        /// </remarks>
        public static string FilterToAccept(string filter)
        {
            if (string.IsNullOrWhiteSpace(filter)) return null;

            string[] parts = filter.Split('|');
            var accept = new System.Collections.Generic.List<string>();

            // Odd indices are the pattern groups; even ones are their human-readable descriptions.
            for (int i = 1; i < parts.Length; i += 2)
            {
                foreach (string pattern in parts[i].Split(';', StringSplitOptions.RemoveEmptyEntries))
                {
                    string trimmed = pattern.Trim();
                    if (trimmed is "*.*" or "*") return null;      // no restriction at all

                    int dot = trimmed.LastIndexOf('.');
                    if (dot < 0) continue;

                    string extension = trimmed.Substring(dot);
                    if (extension.Length > 1 && !accept.Contains(extension))
                    {
                        accept.Add(extension);
                    }
                }
            }

            return accept.Count == 0 ? null : string.Join(",", accept);
        }

        private static string[] ParseNames(string json)
        {
            if (string.IsNullOrEmpty(json)) return Array.Empty<string>();

            try
            {
                using JsonDocument document = JsonDocument.Parse(json);
                JsonElement root = document.RootElement;

                if (!root.TryGetProperty("ok", out JsonElement ok) || !ok.GetBoolean())
                {
                    return Array.Empty<string>();
                }

                if (!root.TryGetProperty("names", out JsonElement names)
                    || names.ValueKind != JsonValueKind.Array)
                {
                    return Array.Empty<string>();
                }

                var result = new string[names.GetArrayLength()];
                int i = 0;
                foreach (JsonElement name in names.EnumerateArray())
                {
                    result[i++] = name.GetString();
                }
                return result;
            }
            catch (JsonException)
            {
                return Array.Empty<string>();
            }
        }

        /// <summary>The browser-window.js exports, bound the same way BrowserWindow binds its own.</summary>
        private static partial class Js
        {
            private const string Module = "wpfBrowserWindow";

            [JSImport("pickFilesAsync", Module)]
            internal static partial Task<string> PickFiles(string accept, bool multiple, bool directory);

            [JSImport("reserveSavePath", Module)]
            internal static partial string ReserveSavePath(string suggestedName);

            [JSImport("offerDownload", Module)]
            internal static partial bool OfferDownload(string path, string mimeType);
        }
    }
}
