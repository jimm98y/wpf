// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// The scripting bridge: turning a managed call into JavaScript, and a JSON result back into a
// managed value.
//
// This is the layer WebBrowser.InvokeScript, WebBrowser.ObjectForScripting and the whole WinForms
// HtmlDocument object model rest on, because every engine behind IWebViewBackend offers exactly one
// primitive -- "evaluate this string, give me JSON back". It is also the layer where being merely
// close is a security bug rather than a rendering glitch: the script name and its arguments both
// come from application code, and both are pasted into a string a browser will parse. A quote that
// escapes the literal does not produce a wrong answer, it produces arbitrary script execution in
// every app that forwards a user-supplied string to InvokeScript.
//
// None of this needs a web engine, a window, or a built fork, which is why it is tested here rather
// than in a per-platform harness -- the same reason DragDropBackendTests tests Android's drag
// sequencing on a developer's laptop.
//

using System;
using MS.Internal.Interop.WebView;
using Xunit;

namespace Wpf.WebView.Tests
{
    public class WebViewScriptTests
    {
        // ---- the injection surface -------------------------------------------------------------

        [Fact]
        public void ArgumentQuotesCannotEscapeTheStringLiteral()
        {
            // The classic break-out: close the string, run something, re-open it.
            string hostile = "\"); alert('pwned'); (\"";
            string js = WebViewScript.BuildInvokeExpression("f", new object[] { hostile });

            // The invariant is NOT "the payload's characters are absent" -- inside a correctly
            // quoted literal they are present and inert, which is the whole point. It is that the
            // argument appears as exactly one well-formed JSON string that decodes back to what was
            // passed. If any quote had escaped the literal, this text would not round-trip.
            string literal = WebViewScript.ToJson(hostile);

            Assert.Contains(literal, js);
            Assert.Equal(hostile, WebViewScript.ReadJsonString(literal));

            // And the quotes really are escaped rather than merely balanced by luck.
            Assert.DoesNotContain("(\"\");", js);
        }

        [Fact]
        public void ScriptNameCannotSmuggleAnExpression()
        {
            // Single quotes and brackets need no JSON escaping and are inert inside a double-quoted
            // key, so the assertion is about SHAPE: the whole name collapses to one property lookup.
            string hostile = "evil'](); alert(1); window['x";
            string js = WebViewScript.BuildInvokeExpression(hostile, null);

            // An exact prefix, not a substring count: the name itself contains the text "window[",
            // so counting occurrences would count the inert copy inside the literal.
            Assert.StartsWith(
                "(function(){var f=window[" + WebViewScript.ToJson(hostile) + "];",
                js);
        }

        [Fact]
        public void ScriptNameWithADoubleQuoteIsEscaped()
        {
            // The one character that COULD close the key. It must come back escaped.
            string hostile = "a\"]);alert(1);//";
            string js = WebViewScript.BuildInvokeExpression(hostile, null);

            Assert.Contains("\\\"", js);
            Assert.StartsWith(
                "(function(){var f=window[" + WebViewScript.ToJson(hostile) + "];",
                js);
        }

        [Fact]
        public void BackslashIsEscapedBeforeQuote()
        {
            // A lone trailing backslash would otherwise escape the closing quote and swallow it.
            string js = WebViewScript.ToJson("ends with \\");
            Assert.Equal("\"ends with \\\\\"", js);
        }

        [Theory]
        [InlineData(0x2028)]   // LINE SEPARATOR
        [InlineData(0x2029)]   // PARAGRAPH SEPARATOR
        public void JavaScriptUnsafeSeparatorsAreEscaped(int codePoint)
        {
            // Legal raw in JSON, historically NOT legal raw in a JavaScript string literal -- and
            // this JSON is about to become one.
            string js = WebViewScript.ToJson("a" + (char)codePoint + "b");

            Assert.DoesNotContain(((char)codePoint).ToString(), js);
            Assert.Contains("\\u" + codePoint.ToString("x4"), js);
        }

        [Fact]
        public void LessThanIsEscapedSoAScriptTagCannotBeClosed()
        {
            string js = WebViewScript.ToJson("</script>");
            Assert.DoesNotContain("<", js);
            Assert.Contains("\\u003c", js);
        }

        [Fact]
        public void ControlCharactersAreEscaped()
        {
            // A raw control character is not legal JSON at all.
            string js = WebViewScript.ToJson("a\u0001b");
            Assert.Contains("\\u0001", js);
        }

        [Fact]
        public void KnownEscapesUseTheirShortForm()
        {
            Assert.Equal("\"\\n\"", WebViewScript.ToJson("\n"));
            Assert.Equal("\"\\r\"", WebViewScript.ToJson("\r"));
            Assert.Equal("\"\\t\"", WebViewScript.ToJson("\t"));
            Assert.Equal("\"\\b\"", WebViewScript.ToJson("\b"));
            Assert.Equal("\"\\f\"", WebViewScript.ToJson("\f"));
        }

        // ---- call-expression shape -------------------------------------------------------------

        [Fact]
        public void DottedNameBecomesNestedLookup()
        {
            string js = WebViewScript.BuildInvokeExpression("outer.inner.fn", null);

            Assert.Contains("window[\"outer\"][\"inner\"][\"fn\"]", js);
        }

        [Fact]
        public void ArgumentsAreEmittedInOrder()
        {
            string js = WebViewScript.BuildInvokeExpression("f", new object[] { 1, "two", true });

            int a = js.IndexOf("1", StringComparison.Ordinal);
            int b = js.IndexOf("\"two\"", StringComparison.Ordinal);
            int c = js.IndexOf("true", StringComparison.Ordinal);

            Assert.True(a < b && b < c, "arguments must keep their order");
        }

        [Fact]
        public void NoArgumentsProducesAnEmptyCall()
        {
            Assert.EndsWith("return f();})()", WebViewScript.BuildInvokeExpression("f", null));
        }

        [Fact]
        public void CallingANonFunctionIsRejectedInThePage()
        {
            // Otherwise `f(...)` on a non-function surfaces as an opaque engine error.
            Assert.Contains("typeof f!=='function'", WebViewScript.BuildInvokeExpression("f", null));
        }

        [Theory]
        [InlineData("")]
        [InlineData(null)]
        public void EmptyScriptNameIsRejected(string name)
        {
            Assert.ThrowsAny<ArgumentException>(() => WebViewScript.BuildInvokeExpression(name, null));
        }

        [Theory]
        [InlineData("a..b")]
        [InlineData(".leading")]
        [InlineData("trailing.")]
        public void MalformedDottedNameIsRejected(string name)
        {
            // Letting these through indexes the global object with "" and fails deep inside the page
            // with an error the app cannot connect back to its call.
            Assert.Throws<ArgumentException>(() => WebViewScript.BuildInvokeExpression(name, null));
        }

        // ---- argument marshalling --------------------------------------------------------------

        [Fact]
        public void NullBecomesJsonNull()
        {
            Assert.Equal("null", WebViewScript.ToJson(null));
        }

        [Fact]
        public void DoublesUseInvariantFormatting()
        {
            // On a comma-decimal culture a naive ToString() emits "1,5", which is TWO arguments once
            // it is pasted into the call.
            System.Globalization.CultureInfo previous = System.Globalization.CultureInfo.CurrentCulture;
            try
            {
                System.Globalization.CultureInfo.CurrentCulture =
                    new System.Globalization.CultureInfo("de-DE");

                Assert.Equal("1.5", WebViewScript.ToJson(1.5));
            }
            finally
            {
                System.Globalization.CultureInfo.CurrentCulture = previous;
            }
        }

        [Theory]
        [InlineData(double.NaN)]
        [InlineData(double.PositiveInfinity)]
        [InlineData(double.NegativeInfinity)]
        public void NonFiniteNumbersAreRejected(double value)
        {
            // These are neither JSON nor JavaScript literals, so they would arrive as a syntax error
            // inside the page rather than as a value.
            Assert.Throws<ArgumentException>(() => WebViewScript.ToJson(value));
        }

        [Fact]
        public void UnsupportedTypeIsRejectedRatherThanStringified()
        {
            // The old WebOC marshalled through a VARIANT; anything it could not carry must fail
            // loudly rather than arrive as "System.Object[]".
            Assert.Throws<ArgumentException>(() => WebViewScript.ToJson(new object[0]));
        }

        [Fact]
        public void BooleansUseJavaScriptSpelling()
        {
            Assert.Equal("true", WebViewScript.ToJson(true));
            Assert.Equal("false", WebViewScript.ToJson(false));
        }

        // ---- result marshalling ----------------------------------------------------------------

        [Fact]
        public void UndefinedAndNullBothComeBackAsNull()
        {
            Assert.Null(WebViewScript.FromJson("null"));
            Assert.Null(WebViewScript.FromJson("undefined"));
            Assert.Null(WebViewScript.FromJson(""));
            Assert.Null(WebViewScript.FromJson(null));
        }

        [Fact]
        public void ScalarsRoundTrip()
        {
            Assert.Equal(true, WebViewScript.FromJson("true"));
            Assert.Equal(false, WebViewScript.FromJson("false"));
            Assert.Equal(42d, WebViewScript.FromJson("42"));
            Assert.Equal("hi", WebViewScript.FromJson("\"hi\""));
        }

        [Fact]
        public void NumbersParseInvariantlyRegardlessOfCulture()
        {
            System.Globalization.CultureInfo previous = System.Globalization.CultureInfo.CurrentCulture;
            try
            {
                System.Globalization.CultureInfo.CurrentCulture =
                    new System.Globalization.CultureInfo("de-DE");

                Assert.Equal(1.5d, WebViewScript.FromJson("1.5"));
            }
            finally
            {
                System.Globalization.CultureInfo.CurrentCulture = previous;
            }
        }

        [Fact]
        public void StringEscapesAreDecoded()
        {
            Assert.Equal("a\nb", WebViewScript.FromJson("\"a\\nb\""));
            Assert.Equal("q\"q", WebViewScript.FromJson("\"q\\\"q\""));
            Assert.Equal("\u00e9", WebViewScript.FromJson("\"\\u00e9\""));
        }

        [Fact]
        public void StructuredResultsComeBackAsRawJson()
        {
            // InvokeScript is typed `object` and always has been; handing back the text is honest
            // and lossless where guessing at a managed shape would not be.
            Assert.Equal("{\"a\":1}", WebViewScript.FromJson("{\"a\":1}"));
            Assert.Equal("[1,2]", WebViewScript.FromJson("[1,2]"));
        }

        [Fact]
        public void EveryEscapeWeEmitCanBeReadBack()
        {
            // The two halves must agree, or a string survives the trip out and is mangled coming in.
            string original = "quote\" back\\ newline\n tab\t less< sep\u2028 ctrl\u0001 unicode\u00e9";

            Assert.Equal(original, WebViewScript.FromJson(WebViewScript.ToJson(original)));
        }

        [Fact]
        public void MalformedResultIsRejected()
        {
            Assert.Throws<FormatException>(() => WebViewScript.ReadJsonString("\"unterminated"));
            Assert.Throws<FormatException>(() => WebViewScript.ReadJsonString("\"bad\\qescape\""));
        }
    }
}
