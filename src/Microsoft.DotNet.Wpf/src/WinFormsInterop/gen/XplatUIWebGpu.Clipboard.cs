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
    /// The system clipboard, as the driver needs it. Installed by WindowsFormsHost, which can reach
    /// WPF's Clipboard; null when nothing is hosting, and then only the in-process store is used.
    /// </summary>
    internal interface IClipboardBridge
    {
        bool TryGetText(out string text);
        void SetText(string text);
        bool ContainsText();
        void Clear();
    }

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

        /// <summary>A non-zero token: WinForms only checks it against zero.</summary>
        internal override IntPtr ClipboardOpen(bool primary_selection) => (IntPtr)1;

        internal override void ClipboardClose(IntPtr handle) { }

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
                ClipboardBridge?.Clear();
                return;
            }

            if (IsTextFormat(id) && obj is string text)
            {
                ClipboardBridge?.SetText(text);
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

                if (ClipboardBridge != null && ClipboardBridge.ContainsText())
                {
                    // Text another application put there is available even though this process never
                    // stored it, so the id has to exist whether or not anything asked for it yet.
                    foreach (string name in TextFormats)
                    {
                        if (!s_formatIds.TryGetValue(name, out int id))
                        {
                            id = s_nextFormatId++;
                            s_formatIds[name] = id;
                            s_formatNames[id] = name;
                        }
                        if (!formats.Contains(id)) formats.Add(id);
                    }
                }
            }
            return formats.ToArray();
        }

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
