// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// WebBrowser.Document over a modern engine: the page's document as an object whose members are the
// DOM's, read, written and called by script.
//
// The WebOC handed back its mshtml document, a COM object an application read through IDispatch --
// `dynamic` in C#, late binding in VB -- or cast to an mshtml interface. The first is what this
// gives: every member access becomes a script evaluation in the page, waited for as InvokeScript
// waits; an object it answers (an element, a collection, the body) is kept in a table on the page and
// comes back as another of these, so identity holds and the next access reaches the same node.
// Names are matched without regard to case where the exact one is missing, as IDispatch matched
// them. A cast to an mshtml interface still fails: no COM object stands behind these.
//

using System.Dynamic;
using System.Globalization;
using System.Text;
using MS.Internal.Interop.WebView;

namespace System.Windows.Controls
{
    public sealed partial class WebBrowser
    {
        /// <summary>The page-side helpers, defined by the first access after a navigation.</summary>
        private const string DomHelpers =
            "if (!window.__wpfDomRef) {" +
            " window.__wpfDom = [];" +
            " window.__wpfDomRef = function (v) {" +
            "  if (v === null || v === undefined) return 'n';" +
            "  var t = typeof v;" +
            "  if (t === 'object' || t === 'function') { window.__wpfDom.push(v); return 'r' + (window.__wpfDom.length - 1); }" +
            "  return 'v' + JSON.stringify(v); };" +
            " window.__wpfDomKey = function (o, n) {" +
            "  if (n in o) return n; var l = n.toLowerCase();" +
            "  for (var k in o) { if (k.toLowerCase() === l) return k; } return n; };" +
            "}";

        /// <summary>A DOM expression evaluated in the page: null, a scalar, or another DOM object.</summary>
        internal object DomEvaluate(string expression)
        {
            if (WebViewBackend is null || !WebViewBackend.IsAttached)
            {
                throw new InvalidOperationException(SR.CannotInvokeScript);
            }

            string script = "(function () { " + DomHelpers + " return window.__wpfDomRef(" + expression + "); })()";
            object raw = WebViewScript.FromJson(WaitForScript(WebViewBackend.ExecuteScriptAsync(script)));
            if (raw is not string tagged || tagged.Length == 0 || tagged[0] == 'n')
            {
                return null;
            }

            if (tagged[0] == 'r')
            {
                return new WebViewDomObject(this, string.Concat("window.__wpfDom[", tagged.AsSpan(1), "]"));
            }

            object value = WebViewScript.FromJson(tagged.Substring(1));
            // an integral number came from a property mshtml typed as a long (length, offsetWidth...)
            if (value is double d && d == Math.Floor(d) && d >= int.MinValue && d <= int.MaxValue)
            {
                return (int)d;
            }

            return value;
        }

        /// <summary>A script's result, waited for while the dispatcher keeps running (as InvokeScript waits).</summary>
        private string WaitForScript(System.Threading.Tasks.Task<string> pending)
        {
            if (!pending.IsCompleted)
            {
                var frame = new System.Windows.Threading.DispatcherFrame();
                pending.ContinueWith(
                    _ => Dispatcher.BeginInvoke((Action)(() => frame.Continue = false)),
                    System.Threading.Tasks.TaskScheduler.Default);
                System.Windows.Threading.Dispatcher.PushFrame(frame);
            }

            if (pending.IsFaulted)
            {
                throw new InvalidOperationException(SR.CannotInvokeScript, pending.Exception?.GetBaseException());
            }

            return pending.Result;
        }
    }

    /// <summary>A DOM object of a WebBrowser's page (the document, an element, a collection), its members
    /// reached by script.</summary>
    internal sealed class WebViewDomObject : DynamicObject
    {
        private readonly WebBrowser _owner;

        /// <summary>The script expression that names the object in the page.</summary>
        internal readonly string Path;

        internal WebViewDomObject(WebBrowser owner, string path)
        {
            _owner = owner;
            Path = path;
        }

        private static string Name(string name) => WebViewScript.WriteJsonString(name);

        private string Arg(object value) => value is WebViewDomObject dom ? dom.Path : WebViewScript.ToJson(value);

        private string Args(object[] args)
        {
            var sb = new StringBuilder();
            for (int i = 0; i < args.Length; i++)
            {
                if (i > 0) sb.Append(", ");
                sb.Append(Arg(args[i]));
            }

            return sb.ToString();
        }

        public override bool TryGetMember(GetMemberBinder binder, out object result)
        {
            result = _owner.DomEvaluate($"(function (o) {{ return o[window.__wpfDomKey(o, {Name(binder.Name)})]; }})({Path})");
            return true;
        }

        public override bool TrySetMember(SetMemberBinder binder, object value)
        {
            _owner.DomEvaluate($"(function (o, v) {{ o[window.__wpfDomKey(o, {Name(binder.Name)})] = v; }})({Path}, {Arg(value)})");
            return true;
        }

        public override bool TryInvokeMember(InvokeMemberBinder binder, object[] args, out object result)
        {
            result = _owner.DomEvaluate($"(function (o) {{ var f = o[window.__wpfDomKey(o, {Name(binder.Name)})]; return f.apply(o, [{Args(args)}]); }})({Path})");
            return true;
        }

        /// <summary>A collection's item (all[0], forms["name"]): item() where the collection has it, as mshtml's do.</summary>
        public override bool TryGetIndex(GetIndexBinder binder, object[] indexes, out object result)
        {
            result = _owner.DomEvaluate($"(function (o, i) {{ return typeof o.item === 'function' ? o.item(i) : o[i]; }})({Path}, {Arg(indexes.Length > 0 ? indexes[0] : null)})");
            return true;
        }

        public override bool TrySetIndex(SetIndexBinder binder, object[] indexes, object value)
        {
            _owner.DomEvaluate($"(function (o, i, v) {{ o[i] = v; }})({Path}, {Arg(indexes.Length > 0 ? indexes[0] : null)}, {Arg(value)})");
            return true;
        }

        /// <summary>Calling the object itself: a collection called with an index (VB's all(0)), or a function.</summary>
        public override bool TryInvoke(InvokeBinder binder, object[] args, out object result)
        {
            result = _owner.DomEvaluate($"(function (o) {{ var a = [{Args(args)}]; return typeof o === 'function' ? o.apply(null, a) : (typeof o.item === 'function' ? o.item(a[0]) : o[a[0]]); }})({Path})");
            return true;
        }

        public override string ToString() => _owner.DomEvaluate($"String({Path})") as string ?? string.Empty;

        public override bool Equals(object obj) => obj is WebViewDomObject other && ReferenceEquals(other._owner, _owner)
            && _owner.DomEvaluate($"({Path}) === ({other.Path})") is true;

        public override int GetHashCode() => _owner.GetHashCode();
    }
}
