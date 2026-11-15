// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// The rest of XpsDocumentWriter's surface: an XPS file written as it is (its fixed document sequence), and the
// C++/CLI events' raise_ methods, which the original's events expose as public members.

using System.IO;
using System.Windows.Documents.Serialization;
using System.Windows.Xps.Packaging;

namespace System.Windows.Xps
{
    public partial class XpsDocumentWriter
    {
        /// <summary>Writes the XPS document at <paramref name="documentPath"/>.</summary>
        public void Write(string documentPath) => Write(documentPath, XpsDocumentNotificationLevel.ReceiveNotificationEnabled);

        public void Write(string documentPath, XpsDocumentNotificationLevel notificationLevel)
        {
            ArgumentNullException.ThrowIfNull(documentPath);
            using var document = Open(documentPath);
            Write(document.GetFixedDocumentSequence());
        }

        /// <summary>Writes the XPS document at <paramref name="documentPath"/> without waiting.</summary>
        public void WriteAsync(string documentPath) => WriteAsync(documentPath, XpsDocumentNotificationLevel.ReceiveNotificationEnabled);

        public void WriteAsync(string documentPath, XpsDocumentNotificationLevel notificationLevel)
        {
            ArgumentNullException.ThrowIfNull(documentPath);
            XpsDocument document = Open(documentPath);
            WritingCompletedEventHandler done = null;
            done = (s, e) =>
            {
                WritingCompleted -= done;
                document.Close();
            };
            WritingCompleted += done;
            WriteAsync(document.GetFixedDocumentSequence());
        }

        // made by its (path, access) constructor, named at run time: its other constructors take System.IO.Packaging's
        // Package, which this assembly does not otherwise reference
        private static XpsDocument Open(string path) =>
            (XpsDocument)Activator.CreateInstance(typeof(XpsDocument), path, FileAccess.Read);

        public virtual void raise_WritingCancelled(object sender, WritingCancelledEventArgs args) => WritingCancelled?.Invoke(sender, args);

        public virtual void raise_WritingCompleted(object sender, WritingCompletedEventArgs e) => WritingCompleted?.Invoke(sender, e);

        public virtual void raise_WritingPrintTicketRequired(object sender, WritingPrintTicketRequiredEventArgs e) => WritingPrintTicketRequired?.Invoke(sender, e);

        public virtual void raise_WritingProgressChanged(object sender, WritingProgressChangedEventArgs e) => WritingProgressChanged?.Invoke(sender, e);
    }
}
