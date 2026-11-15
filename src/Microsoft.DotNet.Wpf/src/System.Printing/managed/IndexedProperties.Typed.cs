// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// The typed properties of a print system object's PropertiesCollection, as the C++/CLI System.Printing has them, each
// as PrintStringProperty is (IndexedProperties.cs): a value of its type (anything else ignored), dirty once changed
// and committed by its object; and the dictionary that holds them by name.

using System.Collections;
using System.Runtime.Serialization;

namespace System.Printing.IndexedProperties
{
    public sealed class PrintBooleanProperty : PrintProperty
    {
        private bool _value;

        public PrintBooleanProperty(string attributeName)
            : base(attributeName)
        {
        }

        public PrintBooleanProperty(string attributeName, object attributeValue)
            : base(attributeName)
        {
            Value = attributeValue;
        }

        public override object Value
        {
            get => _value;
            set
            {
                if (value is not bool incoming)
                {
                    return;
                }

                if (Equals(_value, incoming))
                {
                    return;
                }

                _value = incoming;
                Changed();
            }
        }

        protected sealed override void InternalDispose(bool disposing)
        {
            if (IsDisposed)
            {
                return;
            }

            if (disposing)
            {
                _value = default;
            }

            base.InternalDispose(disposing);
        }

        public static implicit operator bool(PrintBooleanProperty attribRef) => attribRef is null ? default : attribRef._value;
    }

    public sealed class PrintByteArrayProperty : PrintProperty
    {
        private byte[] _value;

        public PrintByteArrayProperty(string attributeName)
            : base(attributeName)
        {
        }

        public PrintByteArrayProperty(string attributeName, object attributeValue)
            : base(attributeName)
        {
            Value = attributeValue;
        }

        public override object Value
        {
            get => _value;
            set
            {
                if (value is not byte[] incoming)
                {
                    if (value is null) { incoming = null; } else { return; }
                }

                if (Equals(_value, incoming))
                {
                    return;
                }

                _value = incoming;
                Changed();
            }
        }

        protected sealed override void InternalDispose(bool disposing)
        {
            if (IsDisposed)
            {
                return;
            }

            if (disposing)
            {
                _value = default;
            }

            base.InternalDispose(disposing);
        }

        public static implicit operator byte[](PrintByteArrayProperty attribRef) => attribRef is null ? null : attribRef._value;
    }

    public sealed class PrintDateTimeProperty : PrintProperty
    {
        private System.DateTime _value;

        public PrintDateTimeProperty(string attributeName)
            : base(attributeName)
        {
        }

        public PrintDateTimeProperty(string attributeName, object attributeValue)
            : base(attributeName)
        {
            Value = attributeValue;
        }

        public override object Value
        {
            get => _value;
            set
            {
                if (value is not System.DateTime incoming)
                {
                    return;
                }

                if (Equals(_value, incoming))
                {
                    return;
                }

                _value = incoming;
                Changed();
            }
        }

        protected sealed override void InternalDispose(bool disposing)
        {
            if (IsDisposed)
            {
                return;
            }

            if (disposing)
            {
                _value = default;
            }

            base.InternalDispose(disposing);
        }

        public static implicit operator System.ValueType(PrintDateTimeProperty attribRef) => attribRef is null ? null : attribRef._value;
    }

    public sealed class PrintDriverProperty : PrintProperty
    {
        private System.Printing.PrintDriver _value;

        public PrintDriverProperty(string attributeName)
            : base(attributeName)
        {
        }

        public PrintDriverProperty(string attributeName, object attributeValue)
            : base(attributeName)
        {
            Value = attributeValue;
        }

        public override object Value
        {
            get => _value;
            set
            {
                if (value is not System.Printing.PrintDriver incoming)
                {
                    if (value is null) { incoming = null; } else { return; }
                }

                if (Equals(_value, incoming))
                {
                    return;
                }

                _value = incoming;
                Changed();
            }
        }

        protected sealed override void InternalDispose(bool disposing)
        {
            if (IsDisposed)
            {
                return;
            }

            if (disposing)
            {
                _value = default;
            }

            base.InternalDispose(disposing);
        }

        public static implicit operator System.Printing.PrintDriver(PrintDriverProperty attribRef) => attribRef is null ? null : attribRef._value;
    }

    public sealed class PrintInt32Property : PrintProperty
    {
        private int _value;

        public PrintInt32Property(string attributeName)
            : base(attributeName)
        {
        }

        public PrintInt32Property(string attributeName, object attributeValue)
            : base(attributeName)
        {
            Value = attributeValue;
        }

        public override object Value
        {
            get => _value;
            set
            {
                if (value is not int incoming)
                {
                    return;
                }

                if (Equals(_value, incoming))
                {
                    return;
                }

                _value = incoming;
                Changed();
            }
        }

        protected sealed override void InternalDispose(bool disposing)
        {
            if (IsDisposed)
            {
                return;
            }

            if (disposing)
            {
                _value = default;
            }

            base.InternalDispose(disposing);
        }

        public static implicit operator int(PrintInt32Property attribRef) => attribRef is null ? default : attribRef._value;
    }

    public sealed class PrintJobPriorityProperty : PrintProperty
    {
        private System.Printing.PrintJobPriority _value;

        public PrintJobPriorityProperty(string attributeName)
            : base(attributeName)
        {
        }

        public PrintJobPriorityProperty(string attributeName, object attributeValue)
            : base(attributeName)
        {
            Value = attributeValue;
        }

        public override object Value
        {
            get => _value;
            set
            {
                if (value is not System.Printing.PrintJobPriority incoming)
                {
                    return;
                }

                if (Equals(_value, incoming))
                {
                    return;
                }

                _value = incoming;
                Changed();
            }
        }

        protected sealed override void InternalDispose(bool disposing)
        {
            if (IsDisposed)
            {
                return;
            }

            if (disposing)
            {
                _value = default;
            }

            base.InternalDispose(disposing);
        }

        public static implicit operator System.Printing.PrintJobPriority(PrintJobPriorityProperty attribRef) => attribRef is null ? default : attribRef._value;
    }

    public sealed class PrintJobStatusProperty : PrintProperty
    {
        private System.Printing.PrintJobStatus _value;

        public PrintJobStatusProperty(string attributeName)
            : base(attributeName)
        {
        }

        public PrintJobStatusProperty(string attributeName, object attributeValue)
            : base(attributeName)
        {
            Value = attributeValue;
        }

        public override object Value
        {
            get => _value;
            set
            {
                if (value is not System.Printing.PrintJobStatus incoming)
                {
                    return;
                }

                if (Equals(_value, incoming))
                {
                    return;
                }

                _value = incoming;
                Changed();
            }
        }

        protected sealed override void InternalDispose(bool disposing)
        {
            if (IsDisposed)
            {
                return;
            }

            if (disposing)
            {
                _value = default;
            }

            base.InternalDispose(disposing);
        }

        public static implicit operator System.Printing.PrintJobStatus(PrintJobStatusProperty attribRef) => attribRef is null ? default : attribRef._value;
    }

    public sealed class PrintPortProperty : PrintProperty
    {
        private System.Printing.PrintPort _value;

        public PrintPortProperty(string attributeName)
            : base(attributeName)
        {
        }

        public PrintPortProperty(string attributeName, object attributeValue)
            : base(attributeName)
        {
            Value = attributeValue;
        }

        public override object Value
        {
            get => _value;
            set
            {
                if (value is not System.Printing.PrintPort incoming)
                {
                    if (value is null) { incoming = null; } else { return; }
                }

                if (Equals(_value, incoming))
                {
                    return;
                }

                _value = incoming;
                Changed();
            }
        }

        protected sealed override void InternalDispose(bool disposing)
        {
            if (IsDisposed)
            {
                return;
            }

            if (disposing)
            {
                _value = default;
            }

            base.InternalDispose(disposing);
        }

        public static implicit operator System.Printing.PrintPort(PrintPortProperty attribRef) => attribRef is null ? null : attribRef._value;
    }

    public sealed class PrintProcessorProperty : PrintProperty
    {
        private System.Printing.PrintProcessor _value;

        public PrintProcessorProperty(string attributeName)
            : base(attributeName)
        {
        }

        public PrintProcessorProperty(string attributeName, object attributeValue)
            : base(attributeName)
        {
            Value = attributeValue;
        }

        public override object Value
        {
            get => _value;
            set
            {
                if (value is not System.Printing.PrintProcessor incoming)
                {
                    if (value is null) { incoming = null; } else { return; }
                }

                if (Equals(_value, incoming))
                {
                    return;
                }

                _value = incoming;
                Changed();
            }
        }

        protected sealed override void InternalDispose(bool disposing)
        {
            if (IsDisposed)
            {
                return;
            }

            if (disposing)
            {
                _value = default;
            }

            base.InternalDispose(disposing);
        }

        public static implicit operator System.Printing.PrintProcessor(PrintProcessorProperty attribRef) => attribRef is null ? null : attribRef._value;
    }

    public sealed class PrintQueueAttributeProperty : PrintProperty
    {
        private System.Printing.PrintQueueAttributes _value;

        public PrintQueueAttributeProperty(string attributeName)
            : base(attributeName)
        {
        }

        public PrintQueueAttributeProperty(string attributeName, object attributeValue)
            : base(attributeName)
        {
            Value = attributeValue;
        }

        public override object Value
        {
            get => _value;
            set
            {
                if (value is not System.Printing.PrintQueueAttributes incoming)
                {
                    return;
                }

                if (Equals(_value, incoming))
                {
                    return;
                }

                _value = incoming;
                Changed();
            }
        }

        protected sealed override void InternalDispose(bool disposing)
        {
            if (IsDisposed)
            {
                return;
            }

            if (disposing)
            {
                _value = default;
            }

            base.InternalDispose(disposing);
        }

        public static implicit operator System.Printing.PrintQueueAttributes(PrintQueueAttributeProperty attributeRef) => attributeRef is null ? default : attributeRef._value;
    }

    public sealed class PrintQueueProperty : PrintProperty
    {
        private System.Printing.PrintQueue _value;

        public PrintQueueProperty(string attributeName)
            : base(attributeName)
        {
        }

        public PrintQueueProperty(string attributeName, object attributeValue)
            : base(attributeName)
        {
            Value = attributeValue;
        }

        public override object Value
        {
            get => _value;
            set
            {
                if (value is not System.Printing.PrintQueue incoming)
                {
                    if (value is null) { incoming = null; } else { return; }
                }

                if (Equals(_value, incoming))
                {
                    return;
                }

                _value = incoming;
                Changed();
            }
        }

        protected sealed override void InternalDispose(bool disposing)
        {
            if (IsDisposed)
            {
                return;
            }

            if (disposing)
            {
                _value = default;
            }

            base.InternalDispose(disposing);
        }

        public static implicit operator System.Printing.PrintQueue(PrintQueueProperty attribRef) => attribRef is null ? null : attribRef._value;
    }

    public sealed class PrintQueueStatusProperty : PrintProperty
    {
        private System.Printing.PrintQueueStatus _value;

        public PrintQueueStatusProperty(string attributeName)
            : base(attributeName)
        {
        }

        public PrintQueueStatusProperty(string attributeName, object attributeValue)
            : base(attributeName)
        {
            Value = attributeValue;
        }

        public override object Value
        {
            get => _value;
            set
            {
                if (value is not System.Printing.PrintQueueStatus incoming)
                {
                    return;
                }

                if (Equals(_value, incoming))
                {
                    return;
                }

                _value = incoming;
                Changed();
            }
        }

        protected sealed override void InternalDispose(bool disposing)
        {
            if (IsDisposed)
            {
                return;
            }

            if (disposing)
            {
                _value = default;
            }

            base.InternalDispose(disposing);
        }

        public static implicit operator System.Printing.PrintQueueStatus(PrintQueueStatusProperty attributeRef) => attributeRef is null ? default : attributeRef._value;
    }

    public sealed class PrintServerLoggingProperty : PrintProperty
    {
        private System.Printing.PrintServerEventLoggingTypes _value;

        public PrintServerLoggingProperty(string attributeName)
            : base(attributeName)
        {
        }

        public PrintServerLoggingProperty(string attributeName, object attributeValue)
            : base(attributeName)
        {
            Value = attributeValue;
        }

        public override object Value
        {
            get => _value;
            set
            {
                if (value is not System.Printing.PrintServerEventLoggingTypes incoming)
                {
                    return;
                }

                if (Equals(_value, incoming))
                {
                    return;
                }

                _value = incoming;
                Changed();
            }
        }

        protected sealed override void InternalDispose(bool disposing)
        {
            if (IsDisposed)
            {
                return;
            }

            if (disposing)
            {
                _value = default;
            }

            base.InternalDispose(disposing);
        }

        public static implicit operator System.Printing.PrintServerEventLoggingTypes(PrintServerLoggingProperty attribRef) => attribRef is null ? default : attribRef._value;
    }

    public sealed class PrintServerProperty : PrintProperty
    {
        private System.Printing.PrintServer _value;

        public PrintServerProperty(string attributeName)
            : base(attributeName)
        {
        }

        public PrintServerProperty(string attributeName, object attributeValue)
            : base(attributeName)
        {
            Value = attributeValue;
        }

        public override object Value
        {
            get => _value;
            set
            {
                if (value is not System.Printing.PrintServer incoming)
                {
                    if (value is null) { incoming = null; } else { return; }
                }

                if (Equals(_value, incoming))
                {
                    return;
                }

                _value = incoming;
                Changed();
            }
        }

        protected sealed override void InternalDispose(bool disposing)
        {
            if (IsDisposed)
            {
                return;
            }

            if (disposing)
            {
                _value = default;
            }

            base.InternalDispose(disposing);
        }

        public static implicit operator System.Printing.PrintServer(PrintServerProperty attribRef) => attribRef is null ? null : attribRef._value;
    }

    public sealed class PrintStreamProperty : PrintProperty
    {
        private System.IO.Stream _value;

        public PrintStreamProperty(string attributeName)
            : base(attributeName)
        {
        }

        public PrintStreamProperty(string attributeName, object attributeValue)
            : base(attributeName)
        {
            Value = attributeValue;
        }

        public override object Value
        {
            get => _value;
            set
            {
                if (value is not System.IO.Stream incoming)
                {
                    if (value is null) { incoming = null; } else { return; }
                }

                if (Equals(_value, incoming))
                {
                    return;
                }

                _value = incoming;
                Changed();
            }
        }

        protected sealed override void InternalDispose(bool disposing)
        {
            if (IsDisposed)
            {
                return;
            }

            if (disposing)
            {
                _value = default;
            }

            base.InternalDispose(disposing);
        }

        public static implicit operator System.IO.Stream(PrintStreamProperty attributeRef) => attributeRef is null ? null : attributeRef._value;
    }

    public sealed class PrintSystemTypeProperty : PrintProperty
    {
        private System.Type _value;

        public PrintSystemTypeProperty(string attributeName)
            : base(attributeName)
        {
        }

        public PrintSystemTypeProperty(string attributeName, object attributeValue)
            : base(attributeName)
        {
            Value = attributeValue;
        }

        public override object Value
        {
            get => _value;
            set
            {
                if (value is not System.Type incoming)
                {
                    if (value is null) { incoming = null; } else { return; }
                }

                if (Equals(_value, incoming))
                {
                    return;
                }

                _value = incoming;
                Changed();
            }
        }

        protected sealed override void InternalDispose(bool disposing)
        {
            if (IsDisposed)
            {
                return;
            }

            if (disposing)
            {
                _value = default;
            }

            base.InternalDispose(disposing);
        }

        public static implicit operator System.Type(PrintSystemTypeProperty attribRef) => attribRef is null ? null : attribRef._value;
    }

    public sealed class PrintThreadPriorityProperty : PrintProperty
    {
        private System.Threading.ThreadPriority _value;

        public PrintThreadPriorityProperty(string attributeName)
            : base(attributeName)
        {
        }

        public PrintThreadPriorityProperty(string attributeName, object attributeValue)
            : base(attributeName)
        {
            Value = attributeValue;
        }

        public override object Value
        {
            get => _value;
            set
            {
                if (value is not System.Threading.ThreadPriority incoming)
                {
                    return;
                }

                if (Equals(_value, incoming))
                {
                    return;
                }

                _value = incoming;
                Changed();
            }
        }

        protected sealed override void InternalDispose(bool disposing)
        {
            if (IsDisposed)
            {
                return;
            }

            if (disposing)
            {
                _value = default;
            }

            base.InternalDispose(disposing);
        }

        public static implicit operator System.Threading.ThreadPriority(PrintThreadPriorityProperty attribRef) => attribRef is null ? default : attribRef._value;
    }

    public sealed class PrintTicketProperty : PrintProperty
    {
        private System.Printing.PrintTicket _value;

        public PrintTicketProperty(string attributeName)
            : base(attributeName)
        {
        }

        public PrintTicketProperty(string attributeName, object attributeValue)
            : base(attributeName)
        {
            Value = attributeValue;
        }

        public override object Value
        {
            get => _value;
            set
            {
                if (value is not System.Printing.PrintTicket incoming)
                {
                    if (value is null) { incoming = null; } else { return; }
                }

                if (Equals(_value, incoming))
                {
                    return;
                }

                _value = incoming;
                Changed();
            }
        }

        protected sealed override void InternalDispose(bool disposing)
        {
            if (IsDisposed)
            {
                return;
            }

            if (disposing)
            {
                _value = default;
            }

            base.InternalDispose(disposing);
        }

        public static implicit operator System.Printing.PrintTicket(PrintTicketProperty attribRef) => attribRef is null ? null : attribRef._value;
    }

    /// <summary>A print system object's properties, by name.</summary>
    public class PrintPropertyDictionary : Hashtable, ISerializable, IDeserializationCallback, IDisposable
    {
        public PrintPropertyDictionary()
        {
        }

#pragma warning disable SYSLIB0051
        protected PrintPropertyDictionary(SerializationInfo info, StreamingContext context)
            : base(info, context)
        {
        }
#pragma warning restore SYSLIB0051

        /// <summary>Adds <paramref name="attributeValue"/> under its name.</summary>
        public void Add(PrintProperty attributeValue)
        {
            ArgumentNullException.ThrowIfNull(attributeValue);
            Add(attributeValue.Name, attributeValue);
        }

        public PrintProperty GetProperty(string attribName) => this[attribName] as PrintProperty;

        /// <summary>Replaces the property of that name: its value, where there is one, else the property itself.</summary>
        public void SetProperty(string attribName, PrintProperty attribValue)
        {
            ArgumentNullException.ThrowIfNull(attribValue);
            if (this[attribName] is PrintProperty existing && existing.GetType() == attribValue.GetType())
            {
                existing.Value = attribValue.Value;
                return;
            }

            this[attribName] = attribValue;
        }

#pragma warning disable CS0672, SYSLIB0051
        public override void GetObjectData(SerializationInfo info, StreamingContext context) => base.GetObjectData(info, context);
#pragma warning restore CS0672, SYSLIB0051

        public override void OnDeserialization(object sender) => base.OnDeserialization(sender);

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool A_0)
        {
            if (!A_0)
            {
                return;
            }

            foreach (object value in Values)
            {
                (value as PrintProperty)?.Dispose();
            }

            Clear();
        }
    }
}
