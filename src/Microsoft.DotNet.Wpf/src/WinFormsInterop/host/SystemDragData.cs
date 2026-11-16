// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// The data of a drag between a WinForms form and another application, as it crosses: the MIME types the
// system's drag and drop carries (text, a file list as file URIs, HTML, RTF, a PNG) on one side, the
// formats Windows applications read (UnicodeText, FileDrop paths, CF_HTML under "HTML Format", "Rich
// Text Format", a Bitmap) on the other. One for every head whose host makes a form a system drag source
// and drop target (CocoaHost on macOS, WaylandFormHost on Linux).
//

extern alias WindowsBase;

using System.Collections.Generic;
using System.Drawing;
using PlatformClipboard = WindowsBase::MS.Internal.Interop.PlatformClipboard;

namespace System.Windows.Forms
{
    internal static class SystemDragData
    {
        internal const string MimeText = "text/plain;charset=utf-8", MimePlain = "text/plain", MimeUriList = "text/uri-list",
                              MimeHtml = "text/html", MimeRtf = "text/rtf", MimePng = "image/png";
        /// <summary>WindowsBase's PlatformDragDrop.InProcessMime: a drag this process started, whose data
        /// object it keeps (<see cref="Current"/>).</summary>
        internal const string MimeInProcess = "application/x-wpf-dragdrop";

        /// <summary>The data object of the drag a form of this process started, for a drop inside the process.</summary>
        internal static IDataObject Current;

        /// <summary>The drag's data as a WinForms data object, in the formats Windows applications read:
        /// read now, while the drag's data can be read.</summary>
        internal static IDataObject ToDataObject(string[] mimeTypes, Func<string, byte[]> read)
        {
            var types = new HashSet<string>(mimeTypes, StringComparer.Ordinal);
            if (types.Contains(MimeInProcess) && Current != null) return Current;

            var data = new DataObject();
            string Utf8(string mime) => read(mime) is { } b ? Text.Encoding.UTF8.GetString(b) : null;
            try
            {
                if (types.Contains(MimeUriList) && read(MimeUriList) is { } list && PlatformClipboard.DecodeFileList(list) is { } files)
                    data.SetData(DataFormats.FileDrop, files);
                string text = types.Contains(MimeText) ? Utf8(MimeText) : types.Contains(MimePlain) ? Utf8(MimePlain) : null;
                if (text != null) { data.SetData(DataFormats.UnicodeText, text); data.SetData(DataFormats.Text, text); }
                if (types.Contains(MimeHtml) && Utf8(MimeHtml) is { } html) data.SetData(DataFormats.Html, PlatformClipboard.ToCfHtml(html));
                if (types.Contains(MimeRtf) && Utf8(MimeRtf) is { } rtf) data.SetData(DataFormats.Rtf, rtf);
                if (types.Contains(MimePng) && read(MimePng) is { } png)
                {
                    using var ms = new IO.MemoryStream(png);
                    using var decoded = new Bitmap(ms);
                    data.SetData(DataFormats.Bitmap, new Bitmap(decoded));
                }
            }
            catch (Exception e) { Console.Error.WriteLine("drag and drop: reading the drag's data threw: " + e.Message); }
            return data;
        }

        /// <summary>What a drag of <paramref name="dataObject"/> offers other applications, by MIME type: text,
        /// a file list as file URIs, HTML (the fragment of CF_HTML), RTF and a PNG, and always the in-process
        /// marker.</summary>
        internal static Dictionary<string, byte[]> Payload(IDataObject dataObject)
        {
            var payload = new Dictionary<string, byte[]>(StringComparer.Ordinal) { [MimeInProcess] = Array.Empty<byte>() };
            try
            {
                if (Get(dataObject, DataFormats.UnicodeText, DataFormats.Text, DataFormats.StringFormat) is string text)
                    payload[MimeText] = Text.Encoding.UTF8.GetBytes(text);
                if (Get(dataObject, DataFormats.FileDrop) is { } files && FilePaths(files) is { Length: > 0 } paths)
                    payload[MimeUriList] = PlatformClipboard.EncodeFileList(paths);
                if (Get(dataObject, DataFormats.Html) is string html)
                    payload[MimeHtml] = Text.Encoding.UTF8.GetBytes(PlatformClipboard.HtmlFragment(html));
                if (Get(dataObject, DataFormats.Rtf) is string rtf)
                    payload[MimeRtf] = Text.Encoding.UTF8.GetBytes(rtf);
                if (Get(dataObject, DataFormats.Bitmap) is Image image)
                {
                    using var ms = new IO.MemoryStream();
                    image.Save(ms, Drawing.Imaging.ImageFormat.Png);
                    payload[MimePng] = ms.ToArray();
                }
            }
            catch (Exception e) { Console.Error.WriteLine("drag and drop: preparing the drag's data threw: " + e.Message); }
            return payload;
        }

        private static object Get(IDataObject data, params string[] formats)
        {
            foreach (string f in formats)
            {
                try { if (data.GetDataPresent(f) && data.GetData(f) is { } v) return v; } catch { }
            }
            return null;
        }

        private static string[] FilePaths(object value) => value switch
        {
            string[] a => a,
            Collections.Specialized.StringCollection c => Linq.Enumerable.ToArray(Linq.Enumerable.Cast<string>(c)),
            string one => new[] { one },
            _ => null,
        };
    }
}
