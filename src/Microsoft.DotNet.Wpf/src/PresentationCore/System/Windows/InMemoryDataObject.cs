// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#nullable enable

using System.Collections.Generic;

namespace System.Windows;

/// <summary>
///  A plain in-process <see cref="IDataObject"/>: a format-to-value dictionary and nothing else.
/// </summary>
/// <remarks>
///  <para>
///   Off Windows this stands in for <see cref="DataObject"/>, which cannot be constructed there at
///   all -- its shared System.Private.Windows.Ole composition eagerly builds a native OLE adapter
///   (GlobalInterfaceTable -> CoCreateInstance -> OLE32.dll -> DllNotFoundException). Use
///   <see cref="DataObjectFactory"/> rather than picking between the two at each call site.
///  </para>
///  <para>
///   Unlike <see cref="MacDataObject"/> this never touches the system pasteboard, and that
///   distinction is the whole point of having both: a drag, or a data object an app is still
///   filling in, must not overwrite what the user has on the clipboard. The pasteboard-backed one
///   is only correct once the data has actually been handed to <see cref="Clipboard"/>.
///  </para>
/// </remarks>
internal sealed class InMemoryDataObject : IDataObject
{
    // The text formats DataObject's autoConvert treats as interchangeable. Storing UnicodeText and
    // reading Text (or the reverse) returns the string, as it would on Windows.
    private static readonly string[] s_textFormats =
    {
        DataFormats.UnicodeText, DataFormats.Text, DataFormats.StringFormat, DataFormats.OemText
    };

    private static bool IsTextFormat(string format)
    {
        foreach (string text in s_textFormats)
        {
            if (string.Equals(text, format, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private readonly Dictionary<string, object?> _store = new(StringComparer.Ordinal);

    internal InMemoryDataObject()
    {
    }

    /// <summary>
    ///  Wraps a bare value, the way <c>new DataObject(object)</c> does on Windows.
    /// </summary>
    /// <remarks>
    ///  A <see cref="string"/>[] is stored as <see cref="DataFormats.FileDrop"/> rather than under
    ///  its type name: this is the only shape a file drag can arrive in, and the platform drag
    ///  sources key their "what can I offer" decision off FileDrop being present.
    /// </remarks>
    internal InMemoryDataObject(object data)
    {
        switch (data)
        {
            case null:
                break;
            case string text:
                _store[DataFormats.UnicodeText] = text;
                break;
            case string[] files:
                _store[DataFormats.FileDrop] = files;
                break;
            default:
                _store[data.GetType().FullName!] = data;
                break;
        }
    }

    /// <summary>The format the value for <paramref name="format"/> is actually stored under.</summary>
    private string? StoredAs(string format)
    {
        if (string.IsNullOrEmpty(format))
        {
            return null;
        }

        if (_store.ContainsKey(format))
        {
            return format;
        }

        if (IsTextFormat(format))
        {
            foreach (string alias in s_textFormats)
            {
                if (_store.ContainsKey(alias))
                {
                    return alias;
                }
            }
        }

        return null;
    }

    public object? GetData(string format) => GetData(format, autoConvert: true);

    public object? GetData(Type format) => GetData(format.FullName!, autoConvert: true);

    public object? GetData(string format, bool autoConvert)
    {
        if (string.IsNullOrEmpty(format))
        {
            return null;
        }

        string? stored = autoConvert ? StoredAs(format) : (_store.ContainsKey(format) ? format : null);
        return stored is null ? null : _store[stored];
    }

    public bool GetDataPresent(string format) => GetDataPresent(format, autoConvert: true);

    public bool GetDataPresent(Type format) => GetDataPresent(format.FullName!, autoConvert: true);

    public bool GetDataPresent(string format, bool autoConvert) =>
        !string.IsNullOrEmpty(format)
        && (autoConvert ? StoredAs(format) is not null : _store.ContainsKey(format));

    public string[] GetFormats() => GetFormats(autoConvert: true);

    public string[] GetFormats(bool autoConvert)
    {
        var formats = new List<string>(_store.Keys);
        if (!autoConvert)
        {
            return formats.ToArray();
        }

        // Anything GetDataPresent answers true for has to be listed here too, or a caller that
        // enumerates formats and copies them across (Clipboard.SetDataObject off Windows does
        // exactly that) loses the aliases.
        foreach (string alias in s_textFormats)
        {
            if (!formats.Contains(alias) && StoredAs(alias) is not null)
            {
                formats.Add(alias);
            }
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
        if (!string.IsNullOrEmpty(format))
        {
            _store[format] = data;
        }
    }
}

/// <summary>
///  Creates the kind of <see cref="IDataObject"/> the current platform can actually construct.
/// </summary>
/// <remarks>
///  Every place that used to say <c>new DataObject(...)</c> on a path reachable off Windows goes
///  through here instead. See <see cref="InMemoryDataObject"/> for why a bare <c>new DataObject()</c>
///  throws there -- it does so in the constructor, so there is no partially-working fallback to be
///  had by catching it later.
/// </remarks>
internal static class DataObjectFactory
{
    /// <summary>An empty data object.</summary>
    internal static IDataObject Create() =>
        OperatingSystem.IsWindows() ? new DataObject() : new InMemoryDataObject();

    /// <summary>
    ///  A data object holding <paramref name="data"/>.
    /// </summary>
    /// <remarks>
    ///  The Windows arm deliberately wraps anything that is not already a <see cref="DataObject"/>,
    ///  including a foreign <see cref="IDataObject"/>: OLE needs an <c>IComDataObject</c>, which only
    ///  <see cref="DataObject"/> implements. Off Windows there is no such requirement, so a data
    ///  object an app built itself is passed through with the formats it offers intact.
    /// </remarks>
    internal static IDataObject Create(object data) =>
        OperatingSystem.IsWindows()
            ? (data as DataObject ?? new DataObject(data))
            : (data as IDataObject ?? new InMemoryDataObject(data));
}
