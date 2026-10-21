// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// The page's script, as BrowserInteropHelper.HostScript gave it to an XBAP: the HTML window object of
// the page the application runs in, reached through `dynamic` -- HostScript.document.title = "...",
// HostScript.alert("..."), HostScript.myPageFunction(1, "a"). An XBAP got it from the browser's
// IOleClientSite; on the browser head the page is the host, so it is the page's globalThis.
//
// Member reads and writes go through JSObject's typed accessors; a call goes through one helper in
// browser-window.js (scriptInvoke), because a JSObject cannot call a function by itself. Values cross
// as they did for an XBAP's script object: strings, numbers (double), booleans and null as themselves,
// anything else as another script object.
//

using System;
using System.Dynamic;
using System.Runtime.InteropServices.JavaScript;
using System.Runtime.Versioning;

namespace MS.Internal.Interop
{
    /// <summary>A page object (the window, a document, an element, a function), reached through dynamic.</summary>
    [SupportedOSPlatform("browser")]
    public sealed partial class BrowserHostScript : DynamicObject
    {
        private readonly JSObject _object;
        // a function read as a member: called with its owner as `this`
        private readonly JSObject _owner;
        private readonly string _name;

        private BrowserHostScript(JSObject obj, JSObject owner = null, string name = null)
        {
            _object = obj;
            _owner = owner;
            _name = name;
        }

        /// <summary>The page's window (globalThis), or null when not running in a browser.</summary>
        public static object Window => OperatingSystem.IsBrowser() ? new BrowserHostScript(JSHost.GlobalThis) : null;

        /// <summary>The address of the page the application runs in, or null when not running in a browser.</summary>
        public static Uri PageUri
        {
            get
            {
                if (!OperatingSystem.IsBrowser()) return null;
                try
                {
                    using JSObject location = JSHost.GlobalThis.GetPropertyAsJSObject("location");
                    string href = location?.GetPropertyAsString("href");
                    return href != null && Uri.TryCreate(href, UriKind.Absolute, out Uri uri) ? uri : null;
                }
                catch (JSException)
                {
                    return null;
                }
            }
        }

        public override bool TryGetMember(GetMemberBinder binder, out object result)
        {
            result = Get(_object, binder.Name);
            return true;
        }

        public override bool TrySetMember(SetMemberBinder binder, object value)
        {
            Set(_object, binder.Name, value);
            return true;
        }

        public override bool TryGetIndex(GetIndexBinder binder, object[] indexes, out object result)
        {
            result = indexes.Length == 1 ? Get(_object, Convert.ToString(indexes[0], System.Globalization.CultureInfo.InvariantCulture)) : null;
            return indexes.Length == 1;
        }

        public override bool TrySetIndex(SetIndexBinder binder, object[] indexes, object value)
        {
            if (indexes.Length != 1) return false;
            Set(_object, Convert.ToString(indexes[0], System.Globalization.CultureInfo.InvariantCulture), value);
            return true;
        }

        public override bool TryInvokeMember(InvokeMemberBinder binder, object[] args, out object result)
        {
            result = Wrap(Js.ScriptInvoke(_object, binder.Name, Unwrap(args)), null, null);
            return true;
        }

        /// <summary>A function read as a member, then called: HostScript.alert("x") written as var f = HostScript.alert; f("x").</summary>
        public override bool TryInvoke(InvokeBinder binder, object[] args, out object result)
        {
            if (_owner == null || _name == null)
            {
                result = null;
                return false;
            }
            result = Wrap(Js.ScriptInvoke(_owner, _name, Unwrap(args)), null, null);
            return true;
        }

        public override string ToString() => _object.ToString();

        private static object Get(JSObject obj, string name)
        {
            switch (obj.GetTypeOfProperty(name))
            {
                case "string": return obj.GetPropertyAsString(name);
                case "number": return obj.GetPropertyAsDouble(name);
                case "boolean": return obj.GetPropertyAsBoolean(name);
                case "object":
                case "function":
                    JSObject value = obj.GetPropertyAsJSObject(name);
                    return value == null ? null : new BrowserHostScript(value, obj, name);
                default: return null;   // undefined
            }
        }

        private static void Set(JSObject obj, string name, object value)
        {
            switch (value)
            {
                case null: obj.SetProperty(name, (JSObject)null); break;
                case string s: obj.SetProperty(name, s); break;
                case bool b: obj.SetProperty(name, b); break;
                case BrowserHostScript script: obj.SetProperty(name, script._object); break;
                case JSObject js: obj.SetProperty(name, js); break;
                case IConvertible number when value is not char: obj.SetProperty(name, number.ToDouble(System.Globalization.CultureInfo.InvariantCulture)); break;
                default: obj.SetProperty(name, value.ToString()); break;
            }
        }

        private static object[] Unwrap(object[] args)
        {
            var result = new object[args.Length];
            for (int i = 0; i < args.Length; i++)
            {
                result[i] = args[i] switch
                {
                    BrowserHostScript script => script._object,
                    string or bool or double or JSObject or null => args[i],
                    char c => c.ToString(),
                    IConvertible number => number.ToDouble(System.Globalization.CultureInfo.InvariantCulture),
                    _ => args[i].ToString(),
                };
            }
            return result;
        }

        private static object Wrap(object value, JSObject owner, string name)
            => value is JSObject js ? new BrowserHostScript(js, owner, name) : value;

        private static partial class Js
        {
            private const string Module = "wpfBrowserWindow";

            [JSImport("scriptInvoke", Module)]
            [return: JSMarshalAs<JSType.Any>]
            internal static partial object ScriptInvoke(JSObject target, string name, [JSMarshalAs<JSType.Array<JSType.Any>>] object[] args);
        }
    }
}
