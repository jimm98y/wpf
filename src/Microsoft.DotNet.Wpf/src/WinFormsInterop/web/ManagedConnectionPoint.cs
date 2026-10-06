// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// A managed stand-in for a COM connection point.
//
// WinForms hosts that needed more than the WebBrowser control exposed have always reached past it
// and sunk the Internet Explorer ActiveX object's dispinterface directly:
//
//     cookie = new AxHost.ConnectionPointCookie(this.ActiveXInstance, sink, typeof(DWebBrowserEvents2));
//
// which is the documented workaround for NewWindow not carrying the target URL. That code is
// everywhere, it is what "compatible with the WinForms WebBrowser" means in practice, and there is
// no ActiveX here to connect to.
//
// So the connection point is provided in managed code instead. WebBrowser.ActiveXInstance returns
// an event source implementing IConnectionPointSource; ConnectionPointCookie hands the sink to it;
// and the control raises events by NAME on whatever sinks are connected. The sink's interface is
// declared by the host, so it is matched by method name and shape rather than by type - exactly how
// a dispinterface behaves, and the reason this works without either side knowing the other.
//

using System;
using System.Collections.Generic;
using System.Reflection;

namespace System.Windows.Forms
{
    /// <summary>Implemented by an object that can accept managed event sinks.</summary>
    internal interface IConnectionPointSource
    {
        void Connect(object sink, Type eventInterface);
        void Disconnect(object sink);
    }

    /// <summary>
    /// Holds the sinks connected to one object and dispatches to them by method name.
    /// </summary>
    internal sealed class ManagedConnectionPoint : IConnectionPointSource
    {
        private readonly List<object> _sinks = new List<object>();

        public void Connect(object sink, Type eventInterface)
        {
            if (sink is null)
            {
                return;
            }

            lock (_sinks)
            {
                _sinks.Add(sink);
            }
        }

        public void Disconnect(object sink)
        {
            lock (_sinks)
            {
                _sinks.Remove(sink);
            }
        }

        /// <summary>
        /// Invoke <paramref name="method"/> on every connected sink that declares it, passing
        /// <paramref name="args"/> by reference so the sink can write back (a dispinterface event
        /// signals "cancel" that way). Returns the possibly-updated arguments.
        /// </summary>
        /// <remarks>
        /// A sink that throws must not take the engine's callback down with it, so each is isolated.
        /// Missing methods are not an error: a dispinterface only ever raises what the sink declared.
        /// </remarks>
        public void Raise(string method, object[] args)
        {
            object[] snapshot;
            lock (_sinks)
            {
                if (_sinks.Count == 0)
                {
                    return;
                }

                snapshot = _sinks.ToArray();
            }

            foreach (object sink in snapshot)
            {
                MethodInfo m = sink.GetType().GetMethod(
                    method,
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

                if (m is null || m.GetParameters().Length != args.Length)
                {
                    continue;
                }

                try
                {
                    m.Invoke(sink, args);
                }
                catch (TargetInvocationException)
                {
                }
            }
        }

        /// <summary>
        /// Connect <paramref name="sink"/> to <paramref name="source"/> if the source offers a
        /// managed connection point. Returns null when it does not, which is what tells
        /// AxHost.ConnectionPointCookie that this really is a COM object it cannot handle.
        /// </summary>
        internal static IConnectionPointSource TryConnect(object source, object sink, Type eventInterface)
        {
            var cp = source as IConnectionPointSource;
            if (cp is null)
            {
                return null;
            }

            cp.Connect(sink, eventInterface);
            return cp;
        }
    }
}
