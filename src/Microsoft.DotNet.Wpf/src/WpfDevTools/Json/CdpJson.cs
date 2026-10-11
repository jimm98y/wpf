// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// CDP message framing, written with Utf8JsonWriter.
//
// Deliberately NOT JsonSerializer: reflection-based serialization is the thing
// the Android head already had to back away from (see AndroidHost.cs), and the
// iOS and WASM heads trim. CDP payloads are heterogeneous enough that a DTO per
// message would be more code than the writer calls anyway.
//

using System;
using System.Buffers;
using System.Text;
using System.Text.Json;

namespace Microsoft.Wpf.DevTools.Json
{
    internal static class CdpJson
    {
        /// <summary>CDP error codes, from the protocol's JSON-RPC dialect.</summary>
        internal const int ErrorInvalidRequest = -32600;
        internal const int ErrorMethodNotFound = -32601;
        internal const int ErrorInternal = -32603;
        internal const int ErrorServer = -32000;

        /// <summary>{"id":N,"result":{...}} -- writeResult fills the result object's members.</summary>
        internal static string Response(int id, int? sessionId, Action<Utf8JsonWriter>? writeResult)
        {
            return Build(w =>
            {
                w.WriteNumber("id", id);
                w.WriteStartObject("result");
                writeResult?.Invoke(w);
                w.WriteEndObject();
                WriteSessionId(w, sessionId);
            });
        }

        /// <summary>{"method":"Domain.event","params":{...}}</summary>
        internal static string Event(string method, int? sessionId, Action<Utf8JsonWriter>? writeParams)
        {
            return Build(w =>
            {
                w.WriteString("method", method);
                w.WriteStartObject("params");
                writeParams?.Invoke(w);
                w.WriteEndObject();
                WriteSessionId(w, sessionId);
            });
        }

        internal static string Error(int id, int? sessionId, int code, string message)
        {
            return Build(w =>
            {
                w.WriteNumber("id", id);
                w.WriteStartObject("error");
                w.WriteNumber("code", code);
                w.WriteString("message", message);
                w.WriteEndObject();
                WriteSessionId(w, sessionId);
            });
        }

        private static void WriteSessionId(Utf8JsonWriter w, int? sessionId)
        {
            if (sessionId.HasValue)
                w.WriteString("sessionId", sessionId.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        internal static string Build(Action<Utf8JsonWriter> write)
        {
            var buffer = new ArrayBufferWriter<byte>(512);
            using (var w = new Utf8JsonWriter(buffer))
            {
                w.WriteStartObject();
                write(w);
                w.WriteEndObject();
            }
            return Encoding.UTF8.GetString(buffer.WrittenSpan);
        }

        // --------------------------------------------------------------
        // Reading command parameters
        //
        // Every accessor is total. A frontend sends parameters this code has
        // never seen and omits ones it considers defaulted, so a missing or
        // wrong-typed member is a normal Tuesday, not an error worth failing a
        // command over.
        // --------------------------------------------------------------

        internal static int GetInt(JsonElement parent, string name, int fallback = 0)
        {
            if (parent.ValueKind == JsonValueKind.Object &&
                parent.TryGetProperty(name, out JsonElement e) &&
                e.ValueKind == JsonValueKind.Number &&
                e.TryGetInt32(out int value))
            {
                return value;
            }
            return fallback;
        }

        /// <summary>
        /// A number that may or may not be whole. Coordinates come through here rather than
        /// GetInt because TryGetInt32 REFUSES a JSON number with a fraction: a frontend that
        /// sends 274.5 for a mouse position would silently be read as 0, and the picker would
        /// answer for the top-left corner on every hover instead of failing visibly.
        /// </summary>
        internal static double GetDouble(JsonElement parent, string name, double fallback = 0)
        {
            if (parent.ValueKind == JsonValueKind.Object &&
                parent.TryGetProperty(name, out JsonElement e) &&
                e.ValueKind == JsonValueKind.Number &&
                e.TryGetDouble(out double value))
            {
                return value;
            }
            return fallback;
        }

        internal static string? GetString(JsonElement parent, string name)
        {
            if (parent.ValueKind == JsonValueKind.Object &&
                parent.TryGetProperty(name, out JsonElement e) &&
                e.ValueKind == JsonValueKind.String)
            {
                return e.GetString();
            }
            return null;
        }

        internal static bool GetBool(JsonElement parent, string name, bool fallback = false)
        {
            if (parent.ValueKind == JsonValueKind.Object &&
                parent.TryGetProperty(name, out JsonElement e))
            {
                if (e.ValueKind == JsonValueKind.True) return true;
                if (e.ValueKind == JsonValueKind.False) return false;
            }
            return fallback;
        }

        internal static JsonElement GetObject(JsonElement parent, string name)
        {
            if (parent.ValueKind == JsonValueKind.Object &&
                parent.TryGetProperty(name, out JsonElement e))
            {
                return e;
            }
            return default;
        }
    }
}
