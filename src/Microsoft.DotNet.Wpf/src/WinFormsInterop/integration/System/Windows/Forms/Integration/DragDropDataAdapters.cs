// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// Carrying a drag's payload across the WPF/WinForms boundary.
//
// The two stacks each define their own IDataObject -- System.Windows.IDataObject and
// System.Windows.Forms.IDataObject -- with the same shape and no relationship. A drag that starts
// in one and ends in the other has to cross that line, and copying the data would be both wasteful
// and wrong: a drag can carry a live object, and formats are produced lazily by design (a source
// that advertises ten formats usually only ever has one asked for).
//
// So these adapt rather than convert. Each is a thin forwarder holding the other side's object, and
// the only thing either really has to know is that FORMAT NAMES already agree: both stacks use the
// Win32 clipboard names ("Text", "UnicodeText", "FileDrop"), so nothing needs translating.
//
// The autoConvert overloads are the one asymmetry. WinForms takes a flag saying whether to fall
// back on converting between related formats (Text and UnicodeText, say); WPF has no such
// parameter and always behaves as autoConvert:true does. Passing it through would mean claiming a
// precision the WPF side cannot deliver, so the flag is honoured where it can be -- as a request
// NOT to convert, which is answerable by asking for the exact format -- and otherwise ignored.
//

using System;
using SWF = System.Windows.Forms;
using WpfIDataObject = System.Windows.IDataObject;

namespace System.Windows.Forms.Integration
{
    /// <summary>Presents a WPF data object to WinForms code.</summary>
    internal sealed class WpfDataAsWinForms : SWF.IDataObject
    {
        private readonly WpfIDataObject _wpf;

        internal WpfDataAsWinForms(WpfIDataObject wpf) => _wpf = wpf;

        /// <summary>The object being wrapped, so a round trip can unwrap instead of double-wrapping.</summary>
        internal WpfIDataObject Wrapped => _wpf;

        public object GetData(string format) => _wpf.GetData(format);
        public object GetData(string format, bool autoConvert) => _wpf.GetData(format, autoConvert);
        public object GetData(Type format) => _wpf.GetData(format);

        public bool GetDataPresent(string format) => _wpf.GetDataPresent(format);
        public bool GetDataPresent(string format, bool autoConvert) => _wpf.GetDataPresent(format, autoConvert);
        public bool GetDataPresent(Type format) => _wpf.GetDataPresent(format);

        public string[] GetFormats() => _wpf.GetFormats();
        public string[] GetFormats(bool autoConvert) => _wpf.GetFormats(autoConvert);

        public void SetData(object data) => _wpf.SetData(data);
        public void SetData(string format, bool autoConvert, object data) => _wpf.SetData(format, data, autoConvert);
        public void SetData(string format, object data) => _wpf.SetData(format, data);
        public void SetData(Type format, object data) => _wpf.SetData(format, data);
    }

    /// <summary>Presents a WinForms data object to WPF code.</summary>
    internal sealed class WinFormsDataAsWpf : WpfIDataObject
    {
        private readonly SWF.IDataObject _winForms;

        internal WinFormsDataAsWpf(SWF.IDataObject winForms) => _winForms = winForms;

        internal SWF.IDataObject Wrapped => _winForms;

        public object GetData(string format) => _winForms.GetData(format);
        public object GetData(string format, bool autoConvert) => _winForms.GetData(format, autoConvert);
        public object GetData(Type format) => _winForms.GetData(format);

        public bool GetDataPresent(string format) => _winForms.GetDataPresent(format);
        public bool GetDataPresent(string format, bool autoConvert) => _winForms.GetDataPresent(format, autoConvert);
        public bool GetDataPresent(Type format) => _winForms.GetDataPresent(format);

        public string[] GetFormats() => _winForms.GetFormats();
        public string[] GetFormats(bool autoConvert) => _winForms.GetFormats(autoConvert);

        public void SetData(object data) => _winForms.SetData(data);
        public void SetData(string format, object data) => _winForms.SetData(format, data);
        public void SetData(string format, object data, bool autoConvert) => _winForms.SetData(format, autoConvert, data);
        public void SetData(Type format, object data) => _winForms.SetData(format, data);
    }

    internal static class DragDropData
    {
        /// <summary>
        /// The WinForms view of a WPF drag payload, unwrapping rather than stacking a second adapter
        /// when the payload started on the WinForms side and is coming home.
        /// </summary>
        internal static SWF.IDataObject ToWinForms(WpfIDataObject wpf)
            => wpf switch
            {
                null => null,
                WinFormsDataAsWpf wrapper => wrapper.Wrapped,
                _ => new WpfDataAsWinForms(wpf),
            };

        /// <summary>
        /// The WPF view of a WinForms drag payload. Control.DoDragDrop takes a plain object, which
        /// may already be a data object or may be the payload itself (a string, most often) -- WPF's
        /// DataObject knows how to wrap the latter, so only the former needs adapting.
        /// </summary>
        internal static WpfIDataObject ToWpf(object data)
            => data switch
            {
                null => null,
                WpfDataAsWinForms wrapper => wrapper.Wrapped,
                WpfIDataObject alreadyWpf => alreadyWpf,
                SWF.IDataObject winForms => new WinFormsDataAsWpf(winForms),
                _ => Wrap(data),
            };

        /// <summary>
        /// A bare payload (a string, an image, any CLR object) as a WPF data object.
        ///
        /// Built on the WinForms DataObject and adapted, rather than on System.Windows.DataObject:
        /// that type implements interfaces from System.Private.Windows.Core, so merely naming it
        /// here would force this assembly to reference one it deliberately does not. The payload is
        /// coming FROM WinForms in any case, so its own container is the natural one to put it in.
        /// </summary>
        private static WpfIDataObject Wrap(object data)
            => new WinFormsDataAsWpf(new SWF.DataObject(data));
    }
}
