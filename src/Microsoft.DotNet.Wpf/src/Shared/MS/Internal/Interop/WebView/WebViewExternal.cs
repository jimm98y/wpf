// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// window.external, re-created over a web view's message channel for the controls whose page reached an
// object of the host's that way (the WebOC's ObjectForScripting: WPF's WebBrowser, WinForms' WebBrowser).
//
// The page's side is a script: any member of window.external is a function that posts
// "<prefix>{id, member, args}" and returns a promise. The host's side answers such a message by
// invoking the member on its object by reflection and posting "<prefix>{id, result}" (or error) back,
// which settles the promise. Link-compiled into PresentationFramework and System.Windows.Forms, which
// cannot reference each other, as the rest of the seam is.
//

using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;

namespace MS.Internal.Interop.WebView
{
    internal static class WebViewExternal
    {
        /// <summary>The page's window.external, its calls posted with <paramref name="prefix"/>; the
        /// object is marked with <paramref name="marker"/> so it is installed once per document.</summary>
        internal static string Script(string prefix, string marker) => @"
(function () {
    if (window.external && window.external." + marker + @") { return; }
    var pending = {}, next = 0;
    window.chrome.webview.addEventListener('message', function (e) {
        var d = e.data;
        if (typeof d !== 'string' || d.indexOf('" + prefix + @"') !== 0) { return; }
        var reply = JSON.parse(d.substring(" + prefix.Length + @"));
        var slot = pending[reply.id];
        if (!slot) { return; }
        delete pending[reply.id];
        if (reply.error) { slot.reject(new Error(reply.error)); } else { slot.resolve(reply.result); }
    });
    window.external = new Proxy({ " + marker + @": true }, {
        get: function (target, name) {
            if (name in target) { return target[name]; }
            return function () {
                var id = ++next, a = Array.prototype.slice.call(arguments);
                var p = new Promise(function (res, rej) { pending[id] = { resolve: res, reject: rej }; });
                window.chrome.webview.postMessage('" + prefix + @"' +
                    JSON.stringify({ id: id, member: String(name), args: a }));
                return p;
            };
        }
    });
})();";

        /// <summary>The answer to a page's call (<paramref name="payload"/>, the message after its prefix)
        /// on <paramref name="target"/>: "{id, result}" or "{id, error}", or null when it names no member.</summary>
        internal static string Answer(object target, string payload)
        {
            // The id is a number and travels as-is; the member is a JSON *string*, so it arrives
            // still quoted and has to be decoded before it can name a member. Passing the raw token
            // to reflection looks up a member literally called "\"Greet\"", which fails in a way
            // that surfaces to the page as a rejected promise and nowhere else.
            string id = ReadJsonMember(payload, "id");
            string member = WebViewScript.FromJson(ReadJsonMember(payload, "member")) as string;

            if (string.IsNullOrEmpty(member))
            {
                return null;
            }

            try
            {
                object result = InvokeScriptingMember(target, member, payload);
                return "{\"id\":" + id + ",\"result\":" + WebViewScript.ToJson(result) + "}";
            }
            catch (Exception ex)
            {
                // The page is holding a promise; rejecting it is the only way the script author
                // learns that the host member threw.
                return "{\"id\":" + id + ",\"error\":" +
                       WebViewScript.WriteJsonString(ex.GetBaseException().Message) + "}";
            }
        }

        private static object InvokeScriptingMember(object target, string member, string payload)
        {
            MethodInfo method = target.GetType().GetMethod(
                member, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);

            if (method is null)
            {
                PropertyInfo property = target.GetType().GetProperty(
                    member, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);

                if (property is null)
                {
                    throw new MissingMemberException(target.GetType().FullName, member);
                }

                return property.GetValue(target);
            }

            ParameterInfo[] parameters = method.GetParameters();
            List<string> raw = ReadJsonArray(payload);
            var converted = new object[parameters.Length];

            for (int i = 0; i < parameters.Length; i++)
            {
                object value = i < raw.Count ? WebViewScript.FromJson(raw[i]) : null;

                converted[i] = value is null
                    ? null
                    : Convert.ChangeType(value, parameters[i].ParameterType,
                                         System.Globalization.CultureInfo.InvariantCulture);
            }

            return method.Invoke(target, converted);
        }

        /// <summary>
        /// Read one scalar member out of the bridge's own message. This is not a general JSON
        /// parser and does not need to be: the only JSON it ever sees is the object the shim above
        /// constructs, whose shape is fixed.
        /// </summary>
        private static string ReadJsonMember(string json, string name)
        {
            int at = json.IndexOf("\"" + name + "\":", StringComparison.Ordinal);

            if (at < 0)
            {
                return "null";
            }

            int start = at + name.Length + 3;
            int end = start;
            bool inString = false;

            while (end < json.Length)
            {
                char c = json[end];

                if (c == '"' && (end == start || json[end - 1] != '\\'))
                {
                    inString = !inString;
                }
                else if (!inString && (c == ',' || c == '}'))
                {
                    break;
                }

                end++;
            }

            return json.Substring(start, end - start).Trim();
        }

        /// <summary>The elements of the shim's "args" array, still as JSON text.</summary>
        private static List<string> ReadJsonArray(string json)
        {
            var items = new List<string>();
            int at = json.IndexOf("\"args\":[", StringComparison.Ordinal);

            if (at < 0)
            {
                return items;
            }

            int i = at + "\"args\":[".Length;
            int depth = 0;
            bool inString = false;
            var current = new StringBuilder();

            for (; i < json.Length; i++)
            {
                char c = json[i];

                if (c == '"' && json[i - 1] != '\\')
                {
                    inString = !inString;
                }

                if (!inString)
                {
                    if (c == '[' || c == '{')
                    {
                        depth++;
                    }
                    else if (c == ']' && depth == 0)
                    {
                        break;
                    }
                    else if (c == ']' || c == '}')
                    {
                        depth--;
                    }
                    else if (c == ',' && depth == 0)
                    {
                        items.Add(current.ToString().Trim());
                        current.Clear();
                        continue;
                    }
                }

                current.Append(c);
            }

            if (current.Length > 0)
            {
                items.Add(current.ToString().Trim());
            }

            return items;
        }
    }
}
