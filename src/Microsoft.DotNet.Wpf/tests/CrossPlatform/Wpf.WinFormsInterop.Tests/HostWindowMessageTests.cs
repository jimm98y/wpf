// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// What Windows tells a top-level window when the system acts on it, as the hosts now report it:
// - activation: WM_NCACTIVATE, then WM_ACTIVATE (a form that draws its own caption shows it active or
//   inactive from the first);
// - the application's own activation: WM_ACTIVATEAPP to every top-level window, once per change
//   (DotNetBar closes its menus from it when the user switches to another application);
// - the user's move or size: WM_ENTERSIZEMOVE and WM_EXITSIZEMOVE, Form.ResizeBegin and ResizeEnd.
//

using System;
using System.Collections.Generic;
using System.Drawing;
using Xunit;
using SWF = System.Windows.Forms;

namespace Wpf.WinFormsInterop.Tests
{
    public class HostWindowMessageTests
    {
        private sealed class Recorder : SWF.Form
        {
            public readonly List<string> Log = new();

            protected override void WndProc(ref SWF.Message m)
            {
                switch (m.Msg)
                {
                    case 0x86: Log.Add($"ncactivate {(long)m.WParam}"); break;
                    case 0x06: Log.Add($"activate {(long)m.WParam & 0xffff}"); break;
                    case 0x1C: Log.Add($"activateapp {(long)m.WParam}"); break;
                }
                base.WndProc(ref m);
            }
        }

        [Fact]
        public void Activation_TellsTheCaptionFirst()
        {
            SWF.XplatUIWebGpu driver = SWF.XplatUIWebGpu.GetInstance();
            using var form = new Recorder { ClientSize = new Size(200, 100) };
            Assert.NotEqual(IntPtr.Zero, form.Handle);
            form.Log.Clear();

            driver.HostActivate(form.Handle.ToInt64(), true);
            driver.HostActivate(form.Handle.ToInt64(), false);

            Assert.Equal(new[] { "ncactivate 1", "activate 1", "ncactivate 0", "activate 0" }, form.Log);
        }

        [Fact]
        public void TheApplicationsActivation_ReachesEveryTopLevelWindowOncePerChange()
        {
            SWF.XplatUIWebGpu driver = SWF.XplatUIWebGpu.GetInstance();
            using var a = new Recorder { ClientSize = new Size(200, 100) };
            using var b = new Recorder { ClientSize = new Size(200, 100) };
            Assert.NotEqual(a.Handle, b.Handle);   // windows to tell
            driver.HostApplicationActive(true);
            a.Log.Clear(); b.Log.Clear();

            driver.HostApplicationActive(false);
            driver.HostApplicationActive(false);   // no change: nothing more
            driver.HostApplicationActive(true);

            Assert.Equal(new[] { "activateapp 0", "activateapp 1" }, a.Log);
            Assert.Equal(new[] { "activateapp 0", "activateapp 1" }, b.Log);
        }

        [Fact]
        public void TheUsersMoveOrSize_RaisesResizeBeginAndResizeEnd()
        {
            SWF.XplatUIWebGpu driver = SWF.XplatUIWebGpu.GetInstance();
            using var form = new SWF.Form { ClientSize = new Size(200, 100) };
            Assert.NotEqual(IntPtr.Zero, form.Handle);
            var log = new List<string>();
            form.ResizeBegin += (s, e) => log.Add("begin");
            form.ResizeEnd += (s, e) => log.Add("end");

            driver.HostSizeMove(form.Handle, true);
            driver.HostSizeMove(form.Handle, false);

            Assert.Equal(new[] { "begin", "end" }, log);
        }
    }
}
