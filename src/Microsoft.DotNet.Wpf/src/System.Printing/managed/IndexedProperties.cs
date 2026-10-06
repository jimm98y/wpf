// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// PrintProperty / PrintStringProperty -- the part of System.Printing's indexed-property model that
// something actually exercises.
//
// The rest of that model (PrintBooleanProperty, PrintInt32Property, PrintPropertyDictionary, the
// PrintSystemObject property bag over the Windows spooler...) stays absent on purpose, for the
// reason given in System.Printing.Managed.csproj: a member that answers plausibly is how a page
// prints blank and nobody finds out until it is on paper. These two are different. They are a plain
// name/value holder with a change notification, they touch nothing outside this file, and their
// behaviour is pinned by System.Printing.Tests -- so there is a real implementation to write and a
// test that says whether it is right.
//
// Semantics are taken from the C++/CLI original (CPP/src/PrintSystemAttributeValue.cpp), including
// the two that are easy to get wrong:
//   * assigning a non-string to Value is silently ignored rather than throwing or clearing;
//   * a value assigned normally marks the property DIRTY, while one assigned during internal
//     initialisation marks it INITIALIZED instead. Only the spooler-backed paths ever set
//     IsInternallyInitialized, so for ordinary construction IsInitialized stays false.
//

using System.Runtime.Serialization;

namespace System.Printing
{
    /// <summary>
    /// Change-notification delegates for the indexed properties. Internal, as in the original --
    /// the public contract exposes the properties, not the notifications.
    /// </summary>
    internal static class PrintSystemDelegates
    {
        internal delegate void StringValueChanged(string newValue);
    }
}

namespace System.Printing.IndexedProperties
{
    /// <summary>
    /// Base of the name/value pairs that describe a print system object.
    /// </summary>
    public abstract class PrintProperty : IDisposable, IDeserializationCallback
    {
        private string _name;

        protected PrintProperty(string attributeName)
        {
            _name = attributeName;
        }

        ~PrintProperty()
        {
            Dispose(false);
        }

        /// <summary>Name identifier of this property. Null once disposed.</summary>
        public virtual string Name => _name;

        /// <summary>The value of the property-value pair this object represents.</summary>
        public abstract object Value { get; set; }

        protected bool IsDisposed { get; set; }

        /// <summary>
        /// Whether the value came from the print system rather than from a caller. Set only by the
        /// internal-initialisation path; a value assigned through <see cref="Value"/> leaves this
        /// false and marks the property dirty instead.
        /// </summary>
        protected internal bool IsInitialized { get; protected set; }

        /// <summary>Set while the print system is populating the value, to distinguish that from a caller's assignment.</summary>
        internal bool IsInternallyInitialized { get; set; }

        /// <summary>Whether this property carries a change handler wired to its owner.</summary>
        internal bool IsLinked { get; set; }

        /// <summary>Whether the value has been changed by a caller and not yet committed.</summary>
        internal bool IsDirty { get; set; }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing) => InternalDispose(disposing);

        protected virtual void InternalDispose(bool disposing)
        {
            _name = null;
            IsDisposed = true;
        }

        public virtual void OnDeserialization(object sender)
        {
        }
    }

    /// <summary>
    /// A print system property whose value is a string.
    /// </summary>
    public sealed class PrintStringProperty : PrintProperty
    {
        private string _value;
        private PrintSystemDelegates.StringValueChanged _changeHandler;

        public PrintStringProperty(string attributeName)
            : base(attributeName)
        {
        }

        public PrintStringProperty(string attributeName, object attributeValue)
            : base(attributeName)
        {
            Value = attributeValue;
        }

        internal PrintStringProperty(string attributeName, MulticastDelegate changeHandler)
            : base(attributeName)
        {
            _changeHandler = (PrintSystemDelegates.StringValueChanged)changeHandler;
            IsLinked = true;
        }

        internal PrintStringProperty(string attributeName, object attributeValue, MulticastDelegate changeHandler)
            : base(attributeName)
        {
            // Assigned before Value so the handler sees the initial assignment, as the original does.
            _changeHandler = (PrintSystemDelegates.StringValueChanged)changeHandler;
            Value = attributeValue;
            IsLinked = true;
        }

        public override object Value
        {
            get => _value;

            set
            {
                // Anything that is not a string (and not null) is ignored -- not thrown, not cleared.
                if (value is not null && value is not string)
                {
                    return;
                }

                string incoming = (string)value;
                if (string.Equals(_value, incoming, StringComparison.Ordinal))
                {
                    return;
                }

                _value = incoming;

                _changeHandler?.Invoke(_value);

                if (IsInternallyInitialized)
                {
                    IsInternallyInitialized = false;
                    IsInitialized = true;
                    IsDirty = false;
                }
                else
                {
                    IsDirty = true;
                }
            }
        }

        internal static PrintProperty Create(string attributeName) =>
            new PrintStringProperty(attributeName);

        internal static PrintProperty Create(string attributeName, object attributeValue) =>
            new PrintStringProperty(attributeName, attributeValue);

        internal static PrintProperty Create(string attributeName, MulticastDelegate changeHandler) =>
            new PrintStringProperty(attributeName, changeHandler);

        internal static PrintProperty Create(string attributeName, object attributeValue, MulticastDelegate changeHandler) =>
            new PrintStringProperty(attributeName, attributeValue, changeHandler);

        protected sealed override void InternalDispose(bool disposing)
        {
            if (IsDisposed)
            {
                return;
            }

            lock (this)
            {
                if (!IsDisposed && disposing)
                {
                    _value = null;
                    _changeHandler = null;
                }

                base.InternalDispose(disposing);
                IsDisposed = true;
            }
        }

        public static implicit operator string(PrintStringProperty attributeRef) => attributeRef?._value;
    }
}
