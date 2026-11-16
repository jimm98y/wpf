// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#nullable enable

using System.Collections.Generic;
using System.IO;
using System.Windows.Media.Imaging;
using MS.Internal.Interop;

namespace System.Windows;

/// <summary>
///  Off-Windows <see cref="IDataObject"/> that never touches OLE. Text and image
///  formats are backed by the macOS general pasteboard (<see cref="MacClipboard"/>)
///  so copy/paste interops with other applications; any other format is kept in an
///  in-process dictionary so the object still round-trips within the app.
/// </summary>
/// <remarks>
///  The Windows <see cref="DataObject"/> eagerly builds a native OLE composition in
///  its constructor (GlobalInterfaceTable / CoCreateInstance), which throws
///  <see cref="DllNotFoundException"/> for OLE32.dll on macOS. This type is the
///  substitute the <see cref="Clipboard"/> uses off-Windows.
/// </remarks>
internal sealed class MacDataObject : IDataObject
{
    // Formats we bridge to the real system pasteboard as UTF-8 text.
    private static bool IsTextFormat(string format) =>
        format == DataFormats.UnicodeText
        || format == DataFormats.Text
        || format == DataFormats.OemText
        || format == DataFormats.StringFormat;

    // Formats we bridge to the real system pasteboard as a PNG image.
    private static bool IsImageFormat(string format) =>
        format == DataFormats.Bitmap
        || format == DataFormats.Dib
        || format == "PNG";

    // Formats the system clipboard also carries across applications: HTML (the CF_HTML string WPF's
    // "HTML Format" holds; the system gets the fragment), RTF and a file list.
    private static bool IsHtmlFormat(string format) => format == DataFormats.Html;
    private static bool IsRtfFormat(string format) => format == DataFormats.Rtf;
    private static bool IsFileFormat(string format) => format == DataFormats.FileDrop;

    // Non-bridged formats live here for the lifetime of this object.
    private readonly Dictionary<string, object?> _store = new(StringComparer.Ordinal);

    /// <summary>Empties both the system pasteboard and the in-process store.</summary>
    public void Clear()
    {
        PlatformClipboard.Clear();
        _store.Clear();
    }

    public object? GetData(string format) => GetData(format, autoConvert: true);

    public object? GetData(Type format) => GetData(format.FullName!, autoConvert: true);

    public object? GetData(string format, bool autoConvert)
    {
        if (string.IsNullOrEmpty(format))
        {
            return null;
        }

        if (IsTextFormat(format))
        {
            return PlatformClipboard.GetString();
        }

        if (IsImageFormat(format))
        {
            byte[]? png = PlatformClipboard.GetData(ClipboardFormat.Png);
            return png is null ? null : DecodePng(png);
        }

        if (IsHtmlFormat(format) && PlatformClipboard.GetData(ClipboardFormat.Html) is { } html)
        {
            return PlatformClipboard.ToCfHtml(System.Text.Encoding.UTF8.GetString(html));
        }

        if (IsRtfFormat(format) && PlatformClipboard.GetData(ClipboardFormat.Rtf) is { } rtf)
        {
            return System.Text.Encoding.UTF8.GetString(rtf);
        }

        if (IsFileFormat(format) && PlatformClipboard.GetFileList() is { } files)
        {
            return files;
        }

        return _store.TryGetValue(format, out object? value) ? value : null;
    }

    public bool GetDataPresent(string format) => GetDataPresent(format, autoConvert: true);

    public bool GetDataPresent(Type format) => GetDataPresent(format.FullName!, autoConvert: true);

    public bool GetDataPresent(string format, bool autoConvert)
    {
        if (string.IsNullOrEmpty(format))
        {
            return false;
        }

        if (IsTextFormat(format))
        {
            return PlatformClipboard.ContainsString();
        }

        if (IsImageFormat(format))
        {
            return PlatformClipboard.ContainsData(ClipboardFormat.Png);
        }

        if ((IsHtmlFormat(format) && PlatformClipboard.ContainsData(ClipboardFormat.Html))
            || (IsRtfFormat(format) && PlatformClipboard.ContainsData(ClipboardFormat.Rtf))
            || (IsFileFormat(format) && PlatformClipboard.ContainsData(ClipboardFormat.FileList)))
        {
            return true;
        }

        return _store.ContainsKey(format);
    }

    public string[] GetFormats() => GetFormats(autoConvert: true);

    public string[] GetFormats(bool autoConvert)
    {
        var formats = new List<string>();
        if (PlatformClipboard.ContainsString())
        {
            formats.Add(DataFormats.UnicodeText);
            formats.Add(DataFormats.Text);
        }

        if (PlatformClipboard.ContainsData(ClipboardFormat.Png))
        {
            formats.Add(DataFormats.Bitmap);
        }

        if (PlatformClipboard.ContainsData(ClipboardFormat.Html)) formats.Add(DataFormats.Html);
        if (PlatformClipboard.ContainsData(ClipboardFormat.Rtf)) formats.Add(DataFormats.Rtf);
        if (PlatformClipboard.ContainsData(ClipboardFormat.FileList)) formats.Add(DataFormats.FileDrop);

        foreach (string key in _store.Keys)
        {
            if (!formats.Contains(key)) formats.Add(key);
        }
        return formats.ToArray();
    }

    public void SetData(object? data)
    {
        if (data is not null)
        {
            SetData(data.GetType().FullName!, data);
        }
    }

    public void SetData(string format, object? data) => SetData(format, data, autoConvert: true);

    public void SetData(Type format, object? data) => SetData(format.FullName!, data, autoConvert: true);

    public void SetData(string format, object? data, bool autoConvert)
    {
        if (string.IsNullOrEmpty(format))
        {
            return;
        }

        if (IsTextFormat(format))
        {
            PlatformClipboard.SetString(data?.ToString() ?? string.Empty);
            return;
        }

        if (IsImageFormat(format) && data is BitmapSource bitmap)
        {
            PlatformClipboard.SetData(ClipboardFormat.Png, EncodePng(bitmap));
            return;
        }

        if (ToSystem(format, data) is { } item)
        {
            PlatformClipboard.SetFormats(new[] { item });
            return;
        }

        _store[format] = data;
    }

    /// <summary>
    ///  Puts every format of <paramref name="source"/> on the clipboard: what the system clipboard
    ///  carries across applications in one write, the rest in process. Set one format at a time, each
    ///  system write replaced the one before -- a rich text box's copy (text, RTF, XAML) reached other
    ///  applications as whichever bridged format came last.
    /// </summary>
    public void SetAll(IDataObject source)
    {
        var items = new List<KeyValuePair<ClipboardFormat, byte[]>>();
        bool text = false, image = false;
        foreach (string format in source.GetFormats())
        {
            object? data = source.GetData(format);
            if (IsTextFormat(format))
            {
                if (!text && data is not null)
                {
                    items.Add(new(ClipboardFormat.Text, System.Text.Encoding.UTF8.GetBytes(data.ToString() ?? string.Empty)));
                    text = true;
                }
            }
            else if (IsImageFormat(format))
            {
                if (!image && data is BitmapSource bitmap)
                {
                    items.Add(new(ClipboardFormat.Png, EncodePng(bitmap)));
                    image = true;
                }
            }
            else if (ToSystem(format, data) is { } item)
            {
                items.Add(item);
            }
            else
            {
                _store[format] = data;
            }
        }
        PlatformClipboard.SetFormats(items);
    }

    private static KeyValuePair<ClipboardFormat, byte[]>? ToSystem(string format, object? data)
    {
        if (IsHtmlFormat(format) && data is string html)
        {
            return new(ClipboardFormat.Html, System.Text.Encoding.UTF8.GetBytes(PlatformClipboard.HtmlFragment(html)));
        }
        if (IsRtfFormat(format) && data is string rtf)
        {
            return new(ClipboardFormat.Rtf, System.Text.Encoding.UTF8.GetBytes(rtf));
        }
        if (IsFileFormat(format))
        {
            string[]? files = data switch
            {
                string[] a => a,
                System.Collections.Specialized.StringCollection c => System.Linq.Enumerable.ToArray(System.Linq.Enumerable.Cast<string>(c)),
                string one => new[] { one },
                _ => null,
            };
            if (files is { Length: > 0 })
            {
                return new(ClipboardFormat.FileList, PlatformClipboard.EncodeFileList(files));
            }
        }
        return null;
    }

    private static byte[] EncodePng(BitmapSource bitmap)
    {
        PngBitmapEncoder encoder = new();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using MemoryStream stream = new();
        encoder.Save(stream);
        return stream.ToArray();
    }

    private static BitmapSource? DecodePng(byte[] png)
    {
        // Use the managed decoder (BitmapFrame's off-Windows path); the native PngBitmapDecoder
        // pulls in wpfgfx_cor3.dll / WIC, which is unavailable off-Windows.
        try
        {
            using MemoryStream stream = new(png);
            return ManagedImageDecoder.Decode(uri: null, stream);
        }
        catch (Exception)
        {
            // An image placed by another app may be in a representation the managed decoder
            // can't read; degrade to "no image" rather than throwing out of Clipboard.GetImage.
            return null;
        }
    }
}
