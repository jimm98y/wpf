// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// Turning managed calls into JavaScript, and JavaScript results back into managed values.
//
// Every engine behind IWebViewBackend offers exactly one scripting primitive: "evaluate this string
// and give me the result as JSON". Everything the controls promise is built from that here --
// WebBrowser.InvokeScript (call a NAMED function with arguments), WebBrowser.ObjectForScripting
// (window.external), and the whole WinForms HtmlDocument/HtmlElement object model. Doing it in one
// place means the escaping rules are written once and, more to the point, are testable without a
// web engine anywhere near the machine.
//
// The escaping is the substance. A script name and its arguments both come from application code
// and both end up inside a string that a browser will parse, so anything less than exact quoting is
// a script-injection bug in every app that passes a user-supplied string to InvokeScript. The JSON
// writer below therefore escapes the full set the spec requires plus the two LINE/PARAGRAPH
// SEPARATOR characters -- legal in JSON, but historically NOT legal raw in a JavaScript string
// literal, which is what this JSON is about to be pasted into.
//
// The reader is deliberately small: it parses the JSON subset an engine can return for a scalar
// result (string, number, true/false/null) and hands anything structural back as its raw text.
// WebBrowser.InvokeScript is typed `object` and its callers have always had to cope with whatever
// the script returned, so returning the JSON text for an object or array is honest and lossless,
// where guessing at a managed shape would not be.
//

using System;
using System.Globalization;
using System.Text;

namespace MS.Internal.Interop.WebView
{
    internal static class WebViewScript
    {
        // Legal raw in JSON, but historically NOT legal raw inside a JavaScript string literal --
        // and this JSON is about to be pasted into one. Written as code points rather than as
        // literal characters so the escaping cannot itself be lost to a source-encoding accident.
        private const char LineSeparator = (char)0x2028;
        private const char ParagraphSeparator = (char)0x2029;

        /// <summary>
        /// Build the expression that calls <paramref name="scriptName"/> with
        /// <paramref name="args"/>, for WebBrowser.InvokeScript.
        /// </summary>
        /// <remarks>
        /// The name is emitted as a dotted path of bracket lookups off the global object rather than
        /// interpolated raw, so "foo.bar" reaches the nested function while a name carrying quotes or
        /// parentheses cannot close the expression and start a new statement.
        /// </remarks>
        internal static string BuildInvokeExpression(string scriptName, object[] args)
        {
            if (string.IsNullOrEmpty(scriptName))
            {
                throw new ArgumentNullException(nameof(scriptName));
            }

            var sb = new StringBuilder();
            sb.Append("(function(){var f=window");

            foreach (string part in scriptName.Split('.'))
            {
                // An empty segment means a malformed name like "a..b" or a leading dot. Letting it
                // through would index the global object with "" and fail deep inside the page with
                // an error the app cannot connect to its call.
                if (part.Length == 0)
                {
                    throw new ArgumentException(SRScriptNameInvalid, nameof(scriptName));
                }

                sb.Append("[").Append(WriteJsonString(part)).Append("]");
            }

            sb.Append(";if(typeof f!=='function'){throw new Error('not a function');}return f(");

            if (args != null)
            {
                for (int i = 0; i < args.Length; i++)
                {
                    if (i > 0)
                    {
                        sb.Append(",");
                    }

                    sb.Append(ToJson(args[i]));
                }
            }

            sb.Append(");})()");
            return sb.ToString();
        }

        /// <summary>
        /// Serialise one InvokeScript argument. Only the primitives the old WebOC could marshal
        /// through a VARIANT are supported; anything else throws rather than silently arriving as
        /// the string "System.Object[]".
        /// </summary>
        internal static string ToJson(object value)
        {
            switch (value)
            {
                case null:
                    return "null";
                case string s:
                    return WriteJsonString(s);
                case bool b:
                    return b ? "true" : "false";
                case char c:
                    return WriteJsonString(c.ToString());
                case float f:
                    return WriteJsonNumber(f, f.ToString("R", CultureInfo.InvariantCulture));
                case double d:
                    return WriteJsonNumber(d, d.ToString("R", CultureInfo.InvariantCulture));
                case decimal m:
                    return m.ToString(CultureInfo.InvariantCulture);
                case sbyte or byte or short or ushort or int or uint or long or ulong:
                    return Convert.ToString(value, CultureInfo.InvariantCulture);
                case DateTime dt:
                    // ISO 8601 round-trips into `new Date(...)` on the far side and sorts correctly
                    // as a string until it gets there.
                    return WriteJsonString(dt.ToString("o", CultureInfo.InvariantCulture));
                default:
                    throw new ArgumentException(SRScriptArgumentNotSupported);
            }
        }

        /// <summary>
        /// Convert a scalar JSON result to a managed value: string, double, bool or null. Objects
        /// and arrays come back as their raw JSON text (see the file remarks).
        /// </summary>
        internal static object FromJson(string json)
        {
            if (string.IsNullOrEmpty(json))
            {
                return null;
            }

            string t = json.Trim();

            if (t == "null" || t == "undefined")
            {
                return null;
            }

            if (t == "true")
            {
                return true;
            }

            if (t == "false")
            {
                return false;
            }

            if (t.Length > 1 && t[0] == '"' && t[t.Length - 1] == '"')
            {
                return ReadJsonString(t);
            }

            if (double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out double number))
            {
                return number;
            }

            // An object, an array, or something no engine should have produced. Hand back the text.
            return t;
        }

        /// <summary>
        /// Quote and escape <paramref name="value"/> as a JSON string, including the surrounding
        /// quotes.
        /// </summary>
        internal static string WriteJsonString(string value)
        {
            if (value == null)
            {
                return "null";
            }

            var sb = new StringBuilder(value.Length + 2);
            sb.Append('"');

            foreach (char ch in value)
            {
                switch (ch)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\b': sb.Append("\\b"); break;
                    case '\f': sb.Append("\\f"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        // Controls must be escaped to be legal JSON. U+2028/U+2029 are legal JSON but
                        // were not legal raw inside a JavaScript string literal, and this text is
                        // about to become one -- so they are escaped too. '<' is escaped so the
                        // sequence "</script>" cannot appear literally when a caller embeds the
                        // result in a document.
                        if (ch < 0x20 || ch == LineSeparator || ch == ParagraphSeparator || ch == '<')
                        {
                            sb.Append("\\u").Append(((int)ch).ToString("x4", CultureInfo.InvariantCulture));
                        }
                        else
                        {
                            sb.Append(ch);
                        }
                        break;
                }
            }

            sb.Append('"');
            return sb.ToString();
        }

        /// <summary>Decode a JSON string literal, quotes included.</summary>
        internal static string ReadJsonString(string quoted)
        {
            if (quoted == null || quoted.Length < 2 || quoted[0] != '"' || quoted[quoted.Length - 1] != '"')
            {
                throw new FormatException(SRScriptResultMalformed);
            }

            var sb = new StringBuilder(quoted.Length - 2);

            for (int i = 1; i < quoted.Length - 1; i++)
            {
                char ch = quoted[i];

                if (ch != '\\')
                {
                    sb.Append(ch);
                    continue;
                }

                if (++i >= quoted.Length - 1)
                {
                    throw new FormatException(SRScriptResultMalformed);
                }

                switch (quoted[i])
                {
                    case '"': sb.Append('"'); break;
                    case '\\': sb.Append('\\'); break;
                    case '/': sb.Append('/'); break;
                    case 'b': sb.Append('\b'); break;
                    case 'f': sb.Append('\f'); break;
                    case 'n': sb.Append('\n'); break;
                    case 'r': sb.Append('\r'); break;
                    case 't': sb.Append('\t'); break;
                    case 'u':
                        if (i + 4 >= quoted.Length)
                        {
                            throw new FormatException(SRScriptResultMalformed);
                        }

                        sb.Append((char)int.Parse(quoted.AsSpan(i + 1, 4), NumberStyles.HexNumber,
                                                  CultureInfo.InvariantCulture));
                        i += 4;
                        break;
                    default:
                        throw new FormatException(SRScriptResultMalformed);
                }
            }

            return sb.ToString();
        }

        /// <summary>
        /// A number that JSON can carry. NaN and the infinities are not JSON and are not JavaScript
        /// literals either, so they would arrive as a syntax error inside the page rather than as a
        /// value -- refuse them here where the caller can still be told which argument was wrong.
        /// </summary>
        private static string WriteJsonNumber(double value, string text)
        {
            if (double.IsNaN(value) || double.IsInfinity(value))
            {
                throw new ArgumentException(SRScriptArgumentNotSupported);
            }

            return text;
        }

        // These live as constants rather than SR lookups because this file is compiled into three
        // assemblies whose resource tables are separate; the WPF and WinForms edges translate to
        // their own SR strings when they surface an error to an application.
        internal const string SRScriptNameInvalid =
            "The script name contains an empty segment.";

        internal const string SRScriptArgumentNotSupported =
            "Only strings, numbers, booleans, DateTime and null can be passed to script.";

        internal const string SRScriptResultMalformed =
            "The script result was not well-formed JSON.";
    }
}
