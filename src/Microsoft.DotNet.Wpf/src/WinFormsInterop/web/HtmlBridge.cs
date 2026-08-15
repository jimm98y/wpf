// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// The one place the synchronous HtmlDocument object model meets the asynchronous engine.
//
// Every property on HtmlDocument/HtmlElement/HtmlWindow is a plain synchronous read -- that is the
// API WinForms shipped and it cannot change -- while every engine behind IWebViewBackend answers
// through a Task. This class is the whole of the accommodation, kept in one file so the trade is
// stated once rather than repeated at forty call sites.
//
// HOW THE WAIT WORKS, and why it is not simply .Result. The engine delivers its answer by posting to
// the application's message loop; blocking the thread on the task would therefore prevent the very
// message that completes it, and deadlock. So the wait PUMPS the loop (Application.DoEvents) until
// the task finishes. The cost is honest and worth writing down: script and UI events run while the
// application is inside a property getter, so a handler can be reentered. WPF's WebBrowser.InvokeScript
// makes exactly the same trade with a DispatcherFrame, and the old mshtml object model had the same
// exposure for the same reason -- IDispatch calls into the page ran script too.
//
// THE PARKING SLOTS. Most elements can be named by an expression that re-finds them
// (document.getElementById("x"), el.children[3]). Two cannot: an element created with
// CreateElement and not yet inserted, and a filtered collection. Those are parked in an array on the
// page, and the wrapper holds its index. The array is cleared on every navigation, because the
// elements in it belong to a document that no longer exists -- keeping them would leak one entry per
// created element for the lifetime of the control.
//

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading.Tasks;
using MS.Internal.Interop.WebView;

namespace System.Windows.Forms
{
    internal sealed class HtmlBridge
    {
        /// <summary>The page-side array that holds elements with no expression of their own.</summary>
        private const string SlotArray = "window.__wfSlots";

        private readonly Func<IWebViewBackend> _backend;
        private int _nextSlot;

        internal HtmlBridge(Func<IWebViewBackend> backend)
        {
            _backend = backend;
        }

        /// <summary>Called on every navigation: the parked elements belonged to the old document.</summary>
        internal void OnDocumentChanged()
        {
            _nextSlot = 0;
        }

        internal static string SlotExpression(int slot) =>
            SlotArray + "[" + slot.ToString(CultureInfo.InvariantCulture) + "]";

        /// <summary>Evaluate an expression and keep the value, returning the slot that names it.</summary>
        internal int Park(string expression)
        {
            int slot = _nextSlot++;

            Eval(SlotArray + " = " + SlotArray + " || []; " +
                 SlotExpression(slot) + " = (" + expression + ");");

            return slot;
        }

        /// <summary>Evaluate for effect, discarding the result.</summary>
        internal void Eval(string script) => Run(script);

        /// <summary>Evaluate and return the raw JSON.</summary>
        internal string EvalRaw(string expression) => Run(expression);

        internal string EvalString(string expression) => WebViewScript.FromJson(Run(expression)) as string;

        internal bool EvalBool(string expression) => WebViewScript.FromJson(Run(expression)) is true;

        internal double EvalDouble(string expression) =>
            WebViewScript.FromJson(Run(expression)) is double d ? d : 0;

        /// <summary>Whether the expression yields something other than null/undefined.</summary>
        internal bool Exists(string expression) =>
            EvalBool("(function(){try{return !!(" + expression + ");}catch(e){return false;}})()");

        /// <summary>
        /// Evaluate a call and convert its result, for InvokeScript/InvokeMember. Identical to
        /// <see cref="EvalRaw"/> except that the result is decoded, which is what those two return.
        /// </summary>
        internal object Invoke(string expression) => WebViewScript.FromJson(Run(expression));

        /// <summary>
        /// Run script and wait for the answer by pumping the message loop. See the file remarks for
        /// why this cannot block instead.
        /// </summary>
        private string Run(string script)
        {
            IWebViewBackend backend = _backend();

            if (backend is null || !backend.IsAttached)
            {
                // Every caller is a property getter on a document that does not exist yet. Returning
                // "null" makes those read as null/0/false, which is what a not-yet-loaded document
                // should look like -- and is what the mshtml wrappers did when there was no document.
                return "null";
            }

            Task<string> pending;

            try
            {
                pending = backend.ExecuteScriptAsync(script);
            }
            catch (InvalidOperationException)
            {
                return "null";
            }

            // A deadline, because a page mid-navigation can leave a script queued and a property read
            // would otherwise hang the application rather than answer.
            //
            // Kept SHORT on purpose. This wait is on the UI thread, so the budget is the longest the
            // window may stop responding for -- and it is spent in full on every read that cannot be
            // answered, which is what a not-yet-loaded document produces. A page that is running
            // normally answers in single-digit milliseconds; a page that cannot answer in two seconds
            // is not going to be helped by ten, it is just going to freeze the application five times
            // longer while it fails.
            DateTime deadline = DateTime.UtcNow.AddSeconds(2);

            while (!pending.IsCompleted && DateTime.UtcNow < deadline)
            {
                // BOTH pumps, and both are needed.
                //
                // Application.DoEvents drains the driver's own managed message queue, which is what
                // keeps the WinForms half of the application alive while we wait. It does NOT reach
                // the real Win32 queue -- and the engine answers through a COM callback dispatched
                // from there, so waiting on DoEvents alone never sees the result and every property
                // on the object model reads back null after the timeout below.
                Application.DoEvents();
                PresentationHost.Current?.Pump();

                System.Threading.Thread.Sleep(1);
            }

            if (!pending.IsCompleted || pending.IsFaulted)
            {
                return "null";
            }

            return pending.Result;
        }

        /// <summary>
        /// Read a JSON array of integers, for the two geometry properties that need one. Not a
        /// general parser: the only arrays it sees are the ones the callers above construct.
        /// </summary>
        internal static int[] ReadIntArray(string json)
        {
            if (string.IsNullOrEmpty(json))
            {
                return Array.Empty<int>();
            }

            string trimmed = json.Trim();

            if (trimmed.Length < 2 || trimmed[0] != '[' || trimmed[trimmed.Length - 1] != ']')
            {
                return Array.Empty<int>();
            }

            string body = trimmed.Substring(1, trimmed.Length - 2).Trim();

            if (body.Length == 0)
            {
                return Array.Empty<int>();
            }

            string[] parts = body.Split(',');
            var values = new List<int>(parts.Length);

            foreach (string part in parts)
            {
                if (double.TryParse(part.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double v))
                {
                    values.Add((int)Math.Round(v));
                }
            }

            return values.ToArray();
        }
    }
}
