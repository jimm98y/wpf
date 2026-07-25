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

    // Non-bridged formats live here for the lifetime of this object.
    private readonly Dictionary<string, object?> _store = new(StringComparer.Ordinal);

    /// <summary>Empties both the system pasteboard and the in-process store.</summary>
    public void Clear()
    {
        MacClipboard.Clear();
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
            return MacClipboard.GetString();
        }

        if (IsImageFormat(format))
        {
            byte[]? png = MacClipboard.GetData(MacClipboard.TypePng);
            return png is null ? null : DecodePng(png);
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
            return MacClipboard.ContainsString();
        }

        if (IsImageFormat(format))
        {
            return MacClipboard.ContainsData(MacClipboard.TypePng);
        }

        return _store.ContainsKey(format);
    }

    public string[] GetFormats() => GetFormats(autoConvert: true);

    public string[] GetFormats(bool autoConvert)
    {
        var formats = new List<string>();
        if (MacClipboard.ContainsString())
        {
            formats.Add(DataFormats.UnicodeText);
            formats.Add(DataFormats.Text);
        }

        if (MacClipboard.ContainsData(MacClipboard.TypePng))
        {
            formats.Add(DataFormats.Bitmap);
        }

        formats.AddRange(_store.Keys);
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
            MacClipboard.SetString(data?.ToString() ?? string.Empty);
            return;
        }

        if (IsImageFormat(format) && data is BitmapSource bitmap)
        {
            MacClipboard.SetData(MacClipboard.TypePng, EncodePng(bitmap));
            return;
        }

        _store[format] = data;
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
