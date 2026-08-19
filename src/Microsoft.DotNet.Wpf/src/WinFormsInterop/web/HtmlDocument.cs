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
    public sealed class HtmlDocument
    {
        private readonly HtmlBridge _bridge;

        internal HtmlDocument(HtmlBridge bridge)
        {
            _bridge = bridge;
        }

        /// <summary>The document's title.</summary>
        public string Title
        {
            get => _bridge.EvalString("document.title");
            set => _bridge.Eval("document.title = " + WebViewScript.ToJson(value));
        }

        /// <summary>The document's URL, or null before anything has loaded.</summary>
        public Uri Url
        {
            get
            {
                string href = _bridge.EvalString("document.location ? document.location.href : null");
                return Uri.TryCreate(href, UriKind.Absolute, out Uri parsed) ? parsed : null;
            }
        }

        /// <summary>The document's domain.</summary>
        public string Domain => _bridge.EvalString("document.domain");

        /// <summary>The &lt;body&gt; element, or null if the document has none yet.</summary>
        public HtmlElement Body =>
            _bridge.Exists("document.body") ? new HtmlElement(_bridge, "document.body") : null;

        /// <summary>The root &lt;html&gt; element.</summary>
        public HtmlElement DocumentElement =>
            _bridge.Exists("document.documentElement")
                ? new HtmlElement(_bridge, "document.documentElement")
                : null;

        /// <summary>The window this document is displayed in.</summary>
        public HtmlWindow Window => new HtmlWindow(_bridge);

        /// <summary>The element with the given id, or null.</summary>
        public HtmlElement GetElementById(string id)
        {
            string expression = "document.getElementById(" + WebViewScript.ToJson(id) + ")";
            return _bridge.Exists(expression) ? new HtmlElement(_bridge, expression) : null;
        }

        /// <summary>All elements with the given tag name.</summary>
        public HtmlElementCollection GetElementsByTagName(string tagName)
        {
            string expression = "document.getElementsByTagName(" + WebViewScript.ToJson(tagName) + ")";
            return new HtmlElementCollection(_bridge, expression);
        }

        /// <summary>Every element in the document.</summary>
        public HtmlElementCollection All => new HtmlElementCollection(_bridge, "document.all");

        /// <summary>Create a detached element. Append it with <see cref="HtmlElement.AppendChild"/>.</summary>
        public HtmlElement CreateElement(string elementTag)
        {
            // Parked on a document-scoped array so a later call can still name it: a created element
            // has no id and no position in the tree, so there would otherwise be no expression that
            // finds it again.
            int slot = _bridge.Park("document.createElement(" + WebViewScript.ToJson(elementTag) + ")");
            return new HtmlElement(_bridge, HtmlBridge.SlotExpression(slot));
        }

        /// <summary>Call a function defined in the page.</summary>
        public object InvokeScript(string scriptName) => InvokeScript(scriptName, null);

        /// <summary>Call a function defined in the page, with arguments.</summary>
        public object InvokeScript(string scriptName, object[] args) =>
            _bridge.Invoke(WebViewScript.BuildInvokeExpression(scriptName, args));

        /// <summary>Replace the document's contents.</summary>
        public void Write(string text) =>
            _bridge.Eval("document.open(); document.write(" + WebViewScript.ToJson(text) + "); document.close();");

        /// <summary>The document's cookies, in the usual "a=1; b=2" form.</summary>
        public string Cookie
        {
            get => _bridge.EvalString("document.cookie");
            set => _bridge.Eval("document.cookie = " + WebViewScript.ToJson(value));
        }

        public bool Equals(HtmlDocument other) => other is not null;
    }

    /// <summary>One element in an <see cref="HtmlDocument"/>.</summary>
    public sealed class HtmlElement
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

        public HtmlDocument Document => new HtmlDocument(_bridge);

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
    public sealed class HtmlElementCollection : IEnumerable
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
    }

    /// <summary>The window an <see cref="HtmlDocument"/> is displayed in.</summary>
    public sealed class HtmlWindow
    {
        private readonly HtmlBridge _bridge;

        internal HtmlWindow(HtmlBridge bridge)
        {
            _bridge = bridge;
        }

        public HtmlDocument Document => new HtmlDocument(_bridge);

        public string Name
        {
            get => _bridge.EvalString("window.name");
            set => _bridge.Eval("window.name = " + WebViewScript.ToJson(value));
        }

        public Uri Url
        {
            get
            {
                string href = _bridge.EvalString("window.location.href");
                return Uri.TryCreate(href, UriKind.Absolute, out Uri parsed) ? parsed : null;
            }
        }

        public System.Drawing.Size Size
        {
            get
            {
                string json = _bridge.EvalRaw("[window.innerWidth, window.innerHeight]");
                int[] v = HtmlBridge.ReadIntArray(json);
                return v.Length == 2 ? new System.Drawing.Size(v[0], v[1]) : System.Drawing.Size.Empty;
            }
        }

        public void Alert(string message) => _bridge.Eval("window.alert(" + WebViewScript.ToJson(message) + ")");

        public bool Confirm(string message) =>
            _bridge.EvalBool("window.confirm(" + WebViewScript.ToJson(message) + ")");

        public string Prompt(string message, string defaultInputValue) =>
            _bridge.EvalString("window.prompt(" + WebViewScript.ToJson(message) + ", " +
                               WebViewScript.ToJson(defaultInputValue) + ")");

        public void ScrollTo(int x, int y) => _bridge.Eval("window.scrollTo(" + x + ", " + y + ")");

        public void Navigate(string urlString) =>
            _bridge.Eval("window.location.href = " + WebViewScript.ToJson(urlString));

        /// <summary>
        /// Open a new browser window. Not implemented: nothing behind this seam can hand back a
        /// second window as an HtmlWindow -- a popup belongs to the engine, not to this process --
        /// and returning null would look like a blocked popup rather than an unimplemented call.
        /// </summary>
        public HtmlWindow Open(string urlString, string target, string windowOptions, bool replaceEntry) =>
            throw new NotSupportedException(
                "Opening a new browser window is not supported by this engine.");

        public object DomWindow =>
            throw new NotSupportedException(
                "There is no COM window object behind this engine. Use the WebBrowser's script and " +
                "message APIs instead.");
    }
}
