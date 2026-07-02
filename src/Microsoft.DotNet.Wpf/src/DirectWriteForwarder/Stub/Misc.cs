// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// Compile-only stub. See DirectWriteForwarderStub.csproj for details.
// Mirrors LocalizedStrings.h, LocalizedErrorMsgs.h, TextItemizer.h.

using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;

namespace MS.Internal
{
    // Mirrors ItemSpan.h's "MS::Internal::Span" (a ref struct declared inside the
    // DirectWriteForwarder assembly itself). On Windows the real vcxproj exposes it to
    // PresentationCore via InternalsVisibleTo; PresentationCore's MS/internal/Span.cs
    // (SpanVector, SpanRider, ...) consumes this type but does NOT declare it. Here it is
    // public so PresentationCore can bind the bare identifier "Span" without an
    // InternalsVisibleTo/strong-name handshake against this stub.
    public class Span
    {
        public Span(object element, int length)
        {
            this.element = element;
            this.length = length;
        }

        public object element;
        public int length;
    }
}

namespace MS.Internal.Text.TextInterface
{
    // Managed-backed off-Windows. DWrite exposes a localized string table (name-table
    // strings keyed by locale); WPF mainly reads index 0 / the en-us entry and enumerates
    // (culture -> string) pairs. We back it with a small ordered locale/value list.
    public sealed class LocalizedStrings : IDictionary<CultureInfo, string>
    {
        private readonly List<string> _locales = new();
        private readonly List<string> _values = new();

        public unsafe LocalizedStrings(Native.IDWriteFactory* localizedStrings)
        {
        }

        public LocalizedStrings()
        {
        }

        // Single-entry (en-us) table for a plain family/face name.
        internal static LocalizedStrings FromString(string value)
        {
            var s = new LocalizedStrings();
            s._locales.Add("en-us");
            s._values.Add(value ?? string.Empty);
            return s;
        }

        public uint StringsCount => (uint)_values.Count;

        public bool FindLocaleName(string localeName, out uint index)
        {
            for (int i = 0; i < _locales.Count; i++)
            {
                if (string.Equals(_locales[i], localeName, StringComparison.OrdinalIgnoreCase))
                {
                    index = (uint)i;
                    return true;
                }
            }
            index = 0;
            return false;
        }

        public string GetLocaleName(uint index) => _locales[(int)index];

        public string GetString(uint index) => _values[(int)index];

        private CultureInfo CultureAt(int i)
        {
            try { return CultureInfo.GetCultureInfo(_locales[i]); }
            catch { return CultureInfo.InvariantCulture; }
        }

        public void Add(CultureInfo key, string value)
        {
            _locales.Add(key?.Name ?? "en-us");
            _values.Add(value ?? string.Empty);
        }

        public bool ContainsKey(CultureInfo key) => FindLocaleName(key?.Name ?? string.Empty, out _);

        public ICollection<CultureInfo> Keys
        {
            get
            {
                var list = new List<CultureInfo>(_locales.Count);
                for (int i = 0; i < _locales.Count; i++) list.Add(CultureAt(i));
                return list;
            }
        }

        public bool Remove(CultureInfo key) => throw new NotSupportedException();

        public bool TryGetValue(CultureInfo key, out string value)
        {
            if (FindLocaleName(key?.Name ?? string.Empty, out uint idx)) { value = _values[(int)idx]; return true; }
            value = null;
            return false;
        }

        public ICollection<string> Values => _values.AsReadOnly();

        public string this[CultureInfo key]
        {
            get => TryGetValue(key, out string v) ? v : null;
            set => throw new NotSupportedException();
        }

        public void Add(KeyValuePair<CultureInfo, string> item) => Add(item.Key, item.Value);

        public void Clear() { _locales.Clear(); _values.Clear(); }

        public bool Contains(KeyValuePair<CultureInfo, string> item) => ContainsKey(item.Key);

        public void CopyTo(KeyValuePair<CultureInfo, string>[] array, int arrayIndex)
        {
            for (int i = 0; i < _values.Count; i++)
                array[arrayIndex + i] = new KeyValuePair<CultureInfo, string>(CultureAt(i), _values[i]);
        }

        public int Count => _values.Count;

        public bool IsReadOnly => true;

        public bool Remove(KeyValuePair<CultureInfo, string> item) => throw new NotSupportedException();

        public IEnumerator<KeyValuePair<CultureInfo, string>> GetEnumerator()
        {
            for (int i = 0; i < _values.Count; i++)
                yield return new KeyValuePair<CultureInfo, string>(CultureAt(i), _values[i]);
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    public static class LocalizedErrorMsgs
    {
        public static string EnumeratorNotStarted { get; set; }

        public static string EnumeratorReachedEnd { get; set; }
    }

    // Mirrors TextItemizer.h. Used only as a parameter type of
    // TextAnalyzer.AnalyzeExtendedCharactersAndDigits; no PresentationCore consumer constructs
    // this type directly on the managed side (it is only ever produced/consumed within the
    // native DirectWrite wrapper).
    public sealed class TextItemizer
    {
        private static readonly PlatformNotSupportedException NotSupported =
            new PlatformNotSupportedException("DirectWrite text/font shaping is not available on this platform.");

        public unsafe IList<MS.Internal.Span> Itemize(CultureInfo numberCulture, byte* pCharAttribute, uint textLength)
        {
            throw NotSupported;
        }

        public void SetIsDigit(uint textPosition, uint textLength, bool isDigit)
        {
            throw NotSupported;
        }
    }
}
