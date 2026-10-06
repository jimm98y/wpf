// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// Getting onto the UI thread, on a head that may not have the UI thread you expect.
//
// Commands arrive on a socket thread and every one of them reads live UI objects,
// so each has to be run on the thread that owns them. For WPF that is
// Dispatcher.Invoke. For a WinForms app it is NOT: a WinForms message loop does
// not pump the WPF dispatcher queue, so Dispatcher.Invoke from a background
// thread would sit there until it timed out -- every command, five seconds each,
// on a head where the inspector otherwise works fine.
//
// Which one applies cannot be decided up front. A pure WPF app has a pumped
// dispatcher; a pure WinForms app has none; and a WinForms app hosting WPF
// through ElementHost has one that IS pumped, by ElementHost's own per-frame
// Pump(). Rather than guess from what is loaded, this tries the dispatcher once
// with a short deadline and, if that deadline passes while a WinForms form is
// available, switches to Control.Invoke for good.
//
// WinForms is reached reflectively for the same reason as everywhere else here:
// most heads never deploy it.
//

using System;
using System.Reflection;
using System.Threading;
using System.Windows.Threading;

namespace Microsoft.Wpf.DevTools
{
    internal sealed class UiMarshaller
    {
        /// <summary>
        /// How long to give the WPF dispatcher before concluding nothing is pumping it.
        /// Short: on a head where it IS pumped the wait is single-digit milliseconds
        /// (the macOS dispatcher wakes about every 8ms even at idle), so a second is
        /// already far beyond "busy" and well into "not being serviced".
        /// </summary>
        private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(1);

        private readonly Dispatcher _dispatcher;
        private readonly TimeSpan _timeout;
        private volatile bool _useWinForms;
        private volatile bool _decided;

        private static MethodInfo? s_controlInvoke;
        private static PropertyInfo? s_invokeRequired;

        internal UiMarshaller(Dispatcher dispatcher, TimeSpan timeout)
        {
            _dispatcher = dispatcher;
            _timeout = timeout;
        }

        internal Dispatcher Dispatcher => _dispatcher;

        /// <summary>Run on the UI thread and return the result. Throws TimeoutException if it cannot get there.</summary>
        internal T Invoke<T>(Func<T> body)
        {
            if (_useWinForms)
                return InvokeOnForm(body);

            if (_decided)
                return _dispatcher.Invoke(body, DispatcherPriority.Send, CancellationToken.None, _timeout);

            // First command: find out whether anything is pumping the dispatcher.
            try
            {
                T result = _dispatcher.Invoke(body, DispatcherPriority.Send, CancellationToken.None, ProbeTimeout);
                _decided = true;
                return result;
            }
            catch (TimeoutException)
            {
                if (!WinFormsAvailable())
                {
                    // No WinForms to fall back to, so the dispatcher is simply busy.
                    // Decide nothing and let the caller's real timeout apply next time.
                    _decided = true;
                    return _dispatcher.Invoke(body, DispatcherPriority.Send, CancellationToken.None, _timeout);
                }

                DevToolsServer.Log("the WPF dispatcher is not being pumped; marshalling through WinForms instead");
                _useWinForms = true;
                _decided = true;
                return InvokeOnForm(body);
            }
        }

        internal void Invoke(Action body)
            => Invoke<object?>(() => { body(); return null; });

        private T InvokeOnForm<T>(Func<T> body)
        {
            object? form = null;
            foreach (object root in WinFormsTree.Roots())
            {
                form = root;
                break;
            }

            if (form == null || s_controlInvoke == null)
                throw new TimeoutException("no WinForms form is available to marshal onto.");

            // Already there (the dump timer, a call from the UI thread): Control.Invoke
            // from the owning thread deadlocks on some paths, so short-circuit.
            if (s_invokeRequired?.GetValue(form) is bool required && !required)
                return body();

            T result = default!;
            Exception? failure = null;

            var work = new Func<object?>(() =>
            {
                try { result = body(); }
                catch (Exception e) { failure = e; }
                return null;
            });

            s_controlInvoke.Invoke(form, new object[] { work });

            if (failure != null)
                throw failure;

            return result;
        }

        private static bool WinFormsAvailable()
        {
            if (!WinFormsTree.Available)
                return false;

            if (s_controlInvoke == null)
            {
                Type? control = Type.GetType("System.Windows.Forms.Control, System.Windows.Forms", throwOnError: false);
                s_controlInvoke = control?.GetMethod("Invoke", BindingFlags.Public | BindingFlags.Instance,
                                                     binder: null, types: new[] { typeof(Delegate) }, modifiers: null);
                s_invokeRequired = control?.GetProperty("InvokeRequired", BindingFlags.Public | BindingFlags.Instance);
            }

            return s_controlInvoke != null && WinFormsTree.Roots().Count > 0;
        }
    }
}
