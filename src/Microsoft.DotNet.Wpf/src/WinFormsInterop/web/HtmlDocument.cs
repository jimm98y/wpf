// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// HtmlDocument / HtmlElement / HtmlElementCollection / HtmlWindow -- WinForms' DOM object model,
// rebuilt on top of script.
//
// The originals were thin wrappers over mshtml: every property was an IDispatch call onto a COM
// object that lived in the same process as the page. Nothing outside the IE host has that. What
// every engine behind IWebViewBackend does have is "evaluate this string, give me JSON back", so
// that is what this is built from -- each element holds a JavaScript EXPRESSION that re-finds it,
// and every property turns into an evaluation of that expression plus a member access.
//
// Holding an expression rather than a handle is the design decision worth explaining. A handle into
// the page would have to be kept alive across navigations and garbage collections on the other side
// of the process boundary, and released deterministically from a finalizer that cannot run script.
// An expression is just a string: it costs nothing to keep, it survives being copied around, and if
// the element is gone by the time it is used, the evaluation returns null and the property reports
// what is true NOW rather than what was true when the wrapper was made.
//
// THE SYNCHRONOUS PROBLEM. This API is entirely synchronous -- `doc.GetElementById("x").InnerText`
// is a property read -- and every engine is asynchronous. The bridge waits by pumping the WinForms
// message loop (Application.DoEvents) rather than blocking on the task, because the engine delivers
// its answer through that same loop; blocking would deadlock. That is the same trade the WPF
// WebBrowser.InvokeScript makes with a DispatcherFrame, and it carries the same caveat: script runs
// while the application waits, so a handler can be reentered. The alternative -- refusing to
// implement the API at all -- would mean no existing WinForms code that touches Document compiles.
//

using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using MS.Internal.Interop.WebView;

namespace System.Windows.Forms
{
    /// <summary>The document currently loaded in a <see cref="WebBrowser"/>.</summary>
    public sealed partial class HtmlElement : IEquatable<HtmlElement>
    {
        private readonly HtmlBridge _bridge;

        /// <summary>The JavaScript expression that re-finds this element. See the file remarks.</summary>
        private readonly string _path;

        internal HtmlElement(HtmlBridge bridge, string path)
        {
            _bridge = bridge;
            _path = path;
        }

        internal string Path => _path;

        public string Id
        {
            get => GetAttribute("id");
            set => SetAttribute("id", value);
        }

        public string Name
        {
            get => GetAttribute("name");
            set => SetAttribute("name", value);
        }

        public string TagName => _bridge.EvalString(_path + ".tagName");

        public string InnerHtml
        {
            get => _bridge.EvalString(_path + ".innerHTML");
            set => _bridge.Eval(_path + ".innerHTML = " + WebViewScript.ToJson(value));
        }

        public string InnerText
        {
            get => _bridge.EvalString(_path + ".innerText");
            set => _bridge.Eval(_path + ".innerText = " + WebViewScript.ToJson(value));
        }

        public string OuterHtml
        {
            get => _bridge.EvalString(_path + ".outerHTML");
            set => _bridge.Eval(_path + ".outerHTML = " + WebViewScript.ToJson(value));
        }

        public string OuterText
        {
            get => _bridge.EvalString(_path + ".outerText");
            set => _bridge.Eval(_path + ".outerText = " + WebViewScript.ToJson(value));
        }

        /// <summary>
        /// The element's inline style, as the CSS text of its style attribute.
        /// </summary>
        /// <remarks>
        /// The original reads and writes style.cssText, so assigning REPLACES the whole inline
        /// style rather than merging - which is what callers expect ("visibility:hidden;display:none"
        /// clears whatever was there). Reading an element with no style attribute yields an empty
        /// string, not null, matching the original.
        /// </remarks>
        public string Style
        {
            get => _bridge.EvalString(_path + ".style.cssText");
            set => _bridge.Eval(_path + ".style.cssText = " + WebViewScript.ToJson(value ?? string.Empty));
        }

        public bool Enabled
        {
            get => !_bridge.EvalBool(_path + ".disabled");
            set => _bridge.Eval(_path + ".disabled = " + (value ? "false" : "true"));
        }

        public HtmlElement Parent =>
            _bridge.Exists(_path + ".parentElement")
                ? new HtmlElement(_bridge, _path + ".parentElement")
                : null;

        public HtmlElementCollection Children => new HtmlElementCollection(_bridge, _path + ".children");

        public HtmlDocument Document => new HtmlDocument(_bridge, _path + ".ownerDocument");

        /// <summary>The element's offset rectangle, in CSS pixels.</summary>
        public System.Drawing.Rectangle OffsetRectangle
        {
            get
            {
                string json = _bridge.EvalRaw(
                    "(function(){var r=" + _path + ".getBoundingClientRect();" +
                    "return [Math.round(r.left),Math.round(r.top),Math.round(r.width),Math.round(r.height)];})()");

                int[] v = HtmlBridge.ReadIntArray(json);

                return v.Length == 4
                    ? new System.Drawing.Rectangle(v[0], v[1], v[2], v[3])
                    : System.Drawing.Rectangle.Empty;
            }
        }

        public int ScrollTop
        {
            get => (int)_bridge.EvalDouble(_path + ".scrollTop");
            set => _bridge.Eval(_path + ".scrollTop = " + value);
        }

        public int ScrollLeft
        {
            get => (int)_bridge.EvalDouble(_path + ".scrollLeft");
            set => _bridge.Eval(_path + ".scrollLeft = " + value);
        }

        public string GetAttribute(string attributeName) =>
            _bridge.EvalString(_path + ".getAttribute(" + WebViewScript.ToJson(attributeName) + ")");

        public void SetAttribute(string attributeName, string value) =>
            _bridge.Eval(_path + ".setAttribute(" + WebViewScript.ToJson(attributeName) + ", " +
                         WebViewScript.ToJson(value) + ")");

        public HtmlElementCollection GetElementsByTagName(string tagName) =>
            new HtmlElementCollection(_bridge,
                _path + ".getElementsByTagName(" + WebViewScript.ToJson(tagName) + ")");

        public HtmlElement AppendChild(HtmlElement newElement)
        {
            _bridge.Eval(_path + ".appendChild(" + newElement._path + ")");
            return newElement;
        }

        public HtmlElement InsertAdjacentElement(HtmlElementInsertionOrientation orient, HtmlElement newElement)
        {
            string where = orient switch
            {
                HtmlElementInsertionOrientation.BeforeBegin => "beforebegin",
                HtmlElementInsertionOrientation.AfterBegin => "afterbegin",
                HtmlElementInsertionOrientation.BeforeEnd => "beforeend",
                _ => "afterend",
            };

            _bridge.Eval(_path + ".insertAdjacentElement(" + WebViewScript.ToJson(where) + ", " +
                         newElement._path + ")");
            return newElement;
        }

        public void RemoveFocus() => _bridge.Eval(_path + ".blur()");

        public void Focus() => _bridge.Eval(_path + ".focus()");

        public void ScrollIntoView(bool alignWithTop) =>
            _bridge.Eval(_path + ".scrollIntoView(" + (alignWithTop ? "true" : "false") + ")");

        /// <summary>Call a method on this element.</summary>
        public object InvokeMember(string methodName) => InvokeMember(methodName, null);

        /// <summary>Call a method on this element, with arguments.</summary>
        public object InvokeMember(string methodName, params object[] parameter)
        {
            var sb = new StringBuilder();
            sb.Append(_path).Append('[').Append(WebViewScript.ToJson(methodName)).Append("](");

            if (parameter is not null)
            {
                for (int i = 0; i < parameter.Length; i++)
                {
                    if (i > 0)
                    {
                        sb.Append(',');
                    }

                    sb.Append(WebViewScript.ToJson(parameter[i]));
                }
            }

            sb.Append(')');
            return _bridge.Invoke(sb.ToString());
        }

        public bool Equals(HtmlElement other) =>
            other is not null && _bridge.EvalBool("(" + _path + ") === (" + other._path + ")");
    }

    /// <summary>Where <see cref="HtmlElement.InsertAdjacentElement"/> puts the new element.</summary>
    public enum HtmlElementInsertionOrientation
    {
        BeforeBegin = 0,
        AfterBegin = 1,
        BeforeEnd = 2,
        AfterEnd = 3,
    }

    /// <summary>A live collection of elements.</summary>
    public sealed partial class HtmlElementCollection : ICollection
    {
        private readonly HtmlBridge _bridge;
        private readonly string _path;

        internal HtmlElementCollection(HtmlBridge bridge, string path)
        {
            _bridge = bridge;
            _path = path;
        }

        /// <summary>
        /// How many elements the collection has RIGHT NOW. It is re-read from the page every time,
        /// because the underlying collection is live and the document may have changed since the
        /// wrapper was made.
        /// </summary>
        public int Count => (int)_bridge.EvalDouble("(" + _path + ") ? (" + _path + ").length : 0");

        public HtmlElement this[int index]
        {
            get
            {
                string item = "(" + _path + ")[" + index + "]";
                return _bridge.Exists(item) ? new HtmlElement(_bridge, item) : null;
            }
        }

        public HtmlElement this[string elementId]
        {
            get
            {
                string item = "(" + _path + ")[" + WebViewScript.ToJson(elementId) + "]";
                return _bridge.Exists(item) ? new HtmlElement(_bridge, item) : null;
            }
        }

        public HtmlElementCollection GetElementsByName(string name)
        {
            // Filtered in the page and parked, so the result is one collection rather than a round
            // trip per candidate element.
            int slot = _bridge.Park(
                "Array.prototype.slice.call(" + _path + ").filter(function(e){" +
                "return e.getAttribute && e.getAttribute('name') === " + WebViewScript.ToJson(name) + ";})");

            return new HtmlElementCollection(_bridge, HtmlBridge.SlotExpression(slot));
        }

        public IEnumerator GetEnumerator()
        {
            int count = Count;
            var items = new List<HtmlElement>(count);

            for (int i = 0; i < count; i++)
            {
                items.Add(this[i]);
            }

            return items.GetEnumerator();
        }

        // ICollection, as .NET's collection implements it (Count is that interface's, which is what
        // makes it a virtual slot code compiled against .NET can bind to).
        void ICollection.CopyTo(Array array, int index)
        {
            foreach (object item in this) array.SetValue(item, index++);
        }

        object ICollection.SyncRoot => this;
        bool ICollection.IsSynchronized => false;
    }

    /// <summary>The window an <see cref="HtmlDocument"/> is displayed in.</summary>
}
