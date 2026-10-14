// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// EnsureCoreWebView2Async on a WebView2 that is in no visual tree yet -- what an app does in its
// control's constructor. Microsoft's control defers the engine until the control is loaded and
// completes the task then; it is not an error. Ours threw InvalidOperationException, which stopped
// Workbench's main window half way through its construction (its HelpSideBar calls it in a
// constructor) and left the app "Connecting to the server..." for good.
//

using System;
using System.Threading.Tasks;
using Microsoft.Web.WebView2.Wpf;
using Xunit;

namespace Wpf.Window.Tests
{
    public class WebView2DeferredInitTests
    {
        [Fact]
        public void EnsureBeforeTheControlIsInATreeIsDeferredNotAnError()
        {
            Task? init = null;
            UiThread.Invoke(() =>
            {
                var view = new WebView2();
                init = view.EnsureCoreWebView2Async();      // must not throw, nor return a faulted task
            });
            Assert.NotNull(init);
            Assert.False(init!.IsFaulted, init.Exception?.ToString());
            Assert.False(init.IsCompleted);                 // it completes when the engine is built
        }

        [Fact]
        public void EveryCallBeforeTheEngineAnswersWithTheSameTask()
        {
            UiThread.Invoke(() =>
            {
                var view = new WebView2();
                Assert.Same(view.EnsureCoreWebView2Async(), view.EnsureCoreWebView2Async());
            });
        }
    }
}
