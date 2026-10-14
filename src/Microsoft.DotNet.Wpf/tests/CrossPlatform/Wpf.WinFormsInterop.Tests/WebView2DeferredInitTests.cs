// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// EnsureCoreWebView2Async on a WinForms WebView2 that has no parent or handle yet: deferred until
// the control is placed, as Microsoft's control does -- never an exception (see the WPF twin in
// Wpf.Window.Tests).
//

using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Web.WebView2.WinForms;
using Xunit;

namespace Wpf.WinFormsInterop.Tests
{
    public class WebView2DeferredInitTests
    {
        [Fact]
        public void EnsureBeforeTheControlHasAHandleIsDeferredNotAnError()
        {
            Task? init = null;
            Exception? failure = null;
            var t = new Thread(() =>
            {
                try
                {
                    using var view = new WebView2();
                    init = view.EnsureCoreWebView2Async();
                    Assert.Same(init, view.EnsureCoreWebView2Async());
                }
                catch (Exception ex) { failure = ex; }
            });
            t.SetApartmentState(ApartmentState.STA);
            t.Start();
            t.Join();
            if (failure != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
            Assert.NotNull(init);
            Assert.False(init!.IsFaulted, init.Exception?.ToString());
        }
    }
}
