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
    public sealed class LocalizedStrings : IDictionary<CultureInfo, string>
    {
        private static readonly PlatformNotSupportedException NotSupported =
            new PlatformNotSupportedException("DirectWrite text/font shaping is not available on this platform.");

        public unsafe LocalizedStrings(Native.IDWriteFactory* localizedStrings)
        {
        }

        public LocalizedStrings()
        {
        }

        public uint StringsCount
        {
            get { throw NotSupported; }
        }

        public bool FindLocaleName(string localeName, out uint index)
        {
            throw NotSupported;
        }

        public string GetLocaleName(uint index)
        {
            throw NotSupported;
        }

        public string GetString(uint index)
        {
            throw NotSupported;
        }

        public void Add(CultureInfo key, string value)
        {
            throw new NotSupportedException();
        }

        public bool ContainsKey(CultureInfo key)
        {
            throw NotSupported;
        }

        public ICollection<CultureInfo> Keys
        {
            get { throw NotSupported; }
        }

        public bool Remove(CultureInfo key)
        {
            throw new NotSupportedException();
        }

        public bool TryGetValue(CultureInfo key, out string value)
        {
            throw NotSupported;
        }

        public ICollection<string> Values
        {
            get { throw NotSupported; }
        }

        public string this[CultureInfo key]
        {
            get { throw NotSupported; }
            set { throw new NotSupportedException(); }
        }

        public void Add(KeyValuePair<CultureInfo, string> item)
        {
            throw new NotSupportedException();
        }

        public void Clear()
        {
            throw new NotSupportedException();
        }

        public bool Contains(KeyValuePair<CultureInfo, string> item)
        {
            throw new NotImplementedException();
        }

        public void CopyTo(KeyValuePair<CultureInfo, string>[] array, int arrayIndex)
        {
            throw NotSupported;
        }

        public int Count
        {
            get { throw NotSupported; }
        }

        public bool IsReadOnly
        {
            get { return true; }
        }

        public bool Remove(KeyValuePair<CultureInfo, string> item)
        {
            throw new NotSupportedException();
        }

        public IEnumerator<KeyValuePair<CultureInfo, string>> GetEnumerator()
        {
            throw NotSupported;
        }

        IEnumerator IEnumerable.GetEnumerator()
        {
            return GetEnumerator();
        }
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
