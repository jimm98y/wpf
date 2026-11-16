// Clipboard for the hosted-WinForms driver.
//
// Every Clipboard entry point was a generated stub: ClipboardOpen returned zero, ClipboardStore did
// nothing, ClipboardRetrieve returned null. So Ctrl+C and Ctrl+V inside a hosted WinForms TextBox
// silently did nothing at all -- not an error, not a message, just no effect, which is the hardest
// kind of missing feature to notice from the outside.
//
// Two stores, because a clipboard is really two things at once.
//
// TEXT goes to the SYSTEM clipboard, through a bridge the host installs (the driver cannot reach
// WPF's Clipboard itself -- this assembly does not reference it, deliberately). That is what makes
// copy from a hosted control paste into another application, and paste bring in what another
// application copied.
//
// EVERYTHING ELSE goes to a process-local store keyed by format. WinForms lets an application put
// arbitrary CLR objects on the clipboard, and there is no honest way to hand a live object to
// another process; Windows manages it with delayed rendering and COM marshalling that has no
// counterpart here. In-process copy and paste of an application's own objects therefore works, and
// crossing to another application works for text. Silently serialising objects to bytes and hoping
// would be worse than either.
//

using System.Collections.Generic;

namespace System.Windows.Forms
{
    /// <summary>
    /// The system clipboard, as the driver needs it. On Windows, user32's (Win32ClipboardBridge, from
    /// the start); elsewhere installed by WindowsFormsHost, which can reach WPF's Clipboard -- null
    /// when nothing is hosting, and then only the in-process store is used.
    /// </summary>
    internal interface IClipboardBridge
    {
        bool TryGetText(out string text);
        void SetText(string text);
        bool ContainsText();
        void Clear();

        /// <summary>Replaces the clipboard with every kind a copy offers, in one write (any may be null).
        /// A bridge that carries text only puts the text there.</summary>
        void SetFormats(string text, byte[] png, string html, string rtf, string[] files)
        {
            if (text != null) SetText(text);
            else Clear();
        }

        bool Contains(ClipKind kind) => kind == ClipKind.Text && ContainsText();
        byte[] GetPng() => null;
        /// <summary>An HTML fragment (not Windows' CF_HTML, which the driver makes of it).</summary>
        string GetHtml() => null;
        string GetRtf() => null;
        string[] GetFiles() => null;
    }

    /// <summary>The kinds of content a system clipboard carries across applications.</summary>
    internal enum ClipKind { Text, Png, Html, Rtf, Files }

    internal partial class XplatUIWebGpu : XplatUIDriver
    {
        internal static IClipboardBridge ClipboardBridge;

        // Format name <-> id. The ids are this process's own: nothing outside sees them, they only
        // have to be stable for as long as the process runs, and WinForms asks for them by name.
        private static readonly Dictionary<string, int> s_formatIds = new Dictionary<string, int>();
        private static readonly Dictionary<int, string> s_formatNames = new Dictionary<int, string>();
        private static int s_nextFormatId = 1;

        /// <summary>Objects put on the clipboard that are not plain text, keyed by format id.</summary>
        private static readonly Dictionary<int, object> s_localClipboard = new Dictionary<int, object>();

        // What a copy put on the clipboard since ClipboardOpen, written to the system clipboard in one
        // go at ClipboardClose: WinForms stores a data object one format at a time, and each format
        // written to the system clipboard on its own replaced the one before.
        private static bool s_pending;
        private static string s_pendingText, s_pendingHtml, s_pendingRtf;
        private static byte[] s_pendingPng;
        private static string[] s_pendingFiles;

        /// <summary>A non-zero token: WinForms only checks it against zero.</summary>
        internal override IntPtr ClipboardOpen(bool primary_selection)
        {
            FlushPending();
            return (IntPtr)1;
        }

        internal override void ClipboardClose(IntPtr handle) => FlushPending();

        private static void FlushPending()
        {
            if (!s_pending) return;
            s_pending = false;
            string text = s_pendingText, html = s_pendingHtml, rtf = s_pendingRtf;
            byte[] png = s_pendingPng;
            string[] files = s_pendingFiles;
            s_pendingText = s_pendingHtml = s_pendingRtf = null;
            s_pendingPng = null;
            s_pendingFiles = null;
            ClipboardBridge?.SetFormats(text, png, html, rtf, files);
        }

        internal override int ClipboardGetID(IntPtr handle, string format)
        {
            lock (s_formatIds)
            {
                if (!s_formatIds.TryGetValue(format, out int id))
                {
                    id = s_nextFormatId++;
                    s_formatIds[format] = id;
                    s_formatNames[id] = format;
                }
                return id;
            }
        }

        internal override void ClipboardStore(IntPtr handle, object obj, int id,
            XplatUI.ObjectToClipboard converter, bool copy)
        {
            // A null object is WinForms' "clear the clipboard", which arrives with no format.
            if (obj == null)
            {
                lock (s_formatIds) s_localClipboard.Clear();
                s_pending = false;
                s_pendingText = s_pendingHtml = s_pendingRtf = null;
                s_pendingPng = null;
                s_pendingFiles = null;
                ClipboardBridge?.Clear();
                return;
            }

            if (ClipboardBridge != null)
            {
                string name = FormatName(id);
                if (IsTextFormat(id) && obj is string text) { s_pendingText ??= text; s_pending = true; }
                else if (name == HtmlFormat && obj is string html) { s_pendingHtml = HtmlFragment(html); s_pending = true; }
                else if (name == RtfFormat && obj is string rtf) { s_pendingRtf = rtf; s_pending = true; }
                else if (IsImageFormat(name) && obj is System.Drawing.Image image && s_pendingPng == null)
                {
                    try
                    {
                        using var ms = new IO.MemoryStream();
                        image.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
                        s_pendingPng = ms.ToArray();
                        s_pending = true;
                    }
                    catch { }
                }
                else if (name == FileDropFormat && FilePaths(obj) is { Length: > 0 } files) { s_pendingFiles = files; s_pending = true; }
            }

            lock (s_formatIds)
            {
                s_localClipboard[id] = obj;
            }
        }

        internal override object ClipboardRetrieve(IntPtr handle, int id,
            XplatUI.ClipboardToObject converter)
        {
            // The SYSTEM clipboard wins for text: something another application copied since is
            // newer than whatever this process last put in its own store, and reading the stale
            // local copy is exactly the bug that makes paste look broken.
            if (IsTextFormat(id) && ClipboardBridge != null && ClipboardBridge.TryGetText(out string text))
            {
                return text;
            }
            // ...and so does it for the other kinds it carries across applications
            if (ClipboardBridge != null)
            {
                string name = FormatName(id);
                try
                {
                    if (name == HtmlFormat && ClipboardBridge.GetHtml() is { } html) return ToCfHtml(html);
                    if (name == RtfFormat && ClipboardBridge.GetRtf() is { } rtf) return rtf;
                    if (name == FileDropFormat && ClipboardBridge.GetFiles() is { } files) return files;
                    if (IsImageFormat(name) && ClipboardBridge.GetPng() is { } png)
                    {
                        using var ms = new IO.MemoryStream(png);
                        using var decoded = new System.Drawing.Bitmap(ms);
                        return new System.Drawing.Bitmap(decoded);   // not tied to the stream
                    }
                }
                catch { }
            }

            lock (s_formatIds)
            {
                return s_localClipboard.TryGetValue(id, out object value) ? value : null;
            }
        }

        internal override int[] ClipboardAvailableFormats(IntPtr handle)
        {
            var formats = new List<int>();
            lock (s_formatIds)
            {
                formats.AddRange(s_localClipboard.Keys);

                if (ClipboardBridge != null)
                {
                    // What another application put there is available even though this process never
                    // stored it, so the ids have to exist whether or not anything asked for them yet.
                    if (ClipboardBridge.ContainsText()) foreach (string name in TextFormats) AddFormat(formats, name);
                    if (ClipboardBridge.Contains(ClipKind.Html)) AddFormat(formats, HtmlFormat);
                    if (ClipboardBridge.Contains(ClipKind.Rtf)) AddFormat(formats, RtfFormat);
                    if (ClipboardBridge.Contains(ClipKind.Png)) AddFormat(formats, "Bitmap");
                    if (ClipboardBridge.Contains(ClipKind.Files)) AddFormat(formats, FileDropFormat);
                }
            }
            return formats.ToArray();
        }

        // under s_formatIds
        private static void AddFormat(List<int> formats, string name)
        {
            if (!s_formatIds.TryGetValue(name, out int id))
            {
                id = s_nextFormatId++;
                s_formatIds[name] = id;
                s_formatNames[id] = name;
            }
            if (!formats.Contains(id)) formats.Add(id);
        }

        // DataFormats' names for what crosses to other applications besides text
        private const string HtmlFormat = "HTML Format", RtfFormat = "Rich Text Format", FileDropFormat = "FileDrop";

        private static string FormatName(int id)
        {
            lock (s_formatIds) return s_formatNames.TryGetValue(id, out string name) ? name : null;
        }

        private static bool IsImageFormat(string name)
            => name == "Bitmap" || name == "DeviceIndependentBitmap" || name == "PNG" || name == "System.Drawing.Bitmap";

        private static string[] FilePaths(object obj) => obj switch
        {
            string[] a => a,
            Collections.Specialized.StringCollection c => System.Linq.Enumerable.ToArray(System.Linq.Enumerable.Cast<string>(c)),
            string one => new[] { one },
            _ => null,
        };

        /// <summary>The fragment of Windows' CF_HTML; a string without its header is the HTML.</summary>
        internal static string HtmlFragment(string cfHtml) => PlatformClipboardBridge.HtmlFragment(cfHtml);

        /// <summary>An HTML fragment as Windows' CF_HTML, which applications parse "HTML Format" as.</summary>
        internal static string ToCfHtml(string fragment) => PlatformClipboardBridge.ToCfHtml(fragment);

        private static readonly string[] TextFormats = { "Text", "UnicodeText", "System.String" };

        private static bool IsTextFormat(int id)
        {
            lock (s_formatIds)
            {
                if (!s_formatNames.TryGetValue(id, out string name)) return false;
                foreach (string text in TextFormats)
                {
                    if (string.Equals(name, text, StringComparison.Ordinal)) return true;
                }
                return false;
            }
        }
    }
}
