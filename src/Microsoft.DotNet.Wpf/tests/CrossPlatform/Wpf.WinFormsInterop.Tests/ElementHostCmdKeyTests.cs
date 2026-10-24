// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// A key the hosted WPF tree leaves unhandled goes on to WinForms' ProcessCmdKey, from the ElementHost up
// to its form, as the Windows-only ElementHost does (its ProcessCmdKey offers the key to WPF first). A
// key WPF handles goes no further. GraphWorX deletes the selected canvas element from its configurator
// control's ProcessCmdKey, and Delete did nothing.
//

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Reflection;
using System.Threading;
using System.Windows.Forms.Integration;
using Xunit;
using SWF = System.Windows.Forms;
using SWI = System.Windows.Input;

namespace Wpf.WinFormsInterop.Tests
{
    public class ElementHostCmdKeyTests
    {
        /// <summary>A container that records the command keys that reach it, as GraphWorX's control does.</summary>
        private sealed class CmdHost : SWF.UserControl
        {
            public readonly List<SWF.Keys> Seen = new();
            protected override bool ProcessCmdKey(ref SWF.Message msg, SWF.Keys keyData)
            {
                Seen.Add(keyData);
                return keyData == SWF.Keys.Delete || base.ProcessCmdKey(ref msg, keyData);
            }
        }

        [Fact]
        public void AKeyHostedWpfLeavesUnhandled_ReachesProcessCmdKey_AndAHandledOneDoesNot()
        {
            Assert.SkipUnless(OperatingSystem.IsWindows(), "a real hosted WPF window");
            Exception? failure = null;
            var t = new Thread(() =>
            {
                try { Run(); }
                catch (Exception ex) { failure = ex; }
            });
            t.TrySetApartmentState(ApartmentState.STA);
            t.Start();
            Assert.True(t.Join(TimeSpan.FromSeconds(60)));
            if (failure != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        }

        private static void Run()
        {
            using var form = new SWF.Form
            {
                Text = "ElementHostCmdKeyTest", StartPosition = SWF.FormStartPosition.Manual,
                Location = new Point(8000, 80), ClientSize = new Size(300, 200), ShowInTaskbar = false,
            };
            var cmd = new CmdHost { Dock = SWF.DockStyle.Fill };
            var canvas = new System.Windows.Controls.Canvas { Background = System.Windows.Media.Brushes.White, Focusable = true };
            // The hosted content handles A itself, as a WPF text box would.
            canvas.KeyDown += (s, e) => { if (e.Key == SWI.Key.A) e.Handled = true; };
            var host = new ElementHost { Dock = SWF.DockStyle.Fill, Child = canvas };
            cmd.Controls.Add(host);
            form.Controls.Add(cmd);
            form.Show();
            Pump(() => host.IsChildLive, 10);
            Assert.True(host.IsChildLive, "the hosted tree never rendered");
            var source = (System.Windows.Interop.HwndSource)System.Windows.PresentationSource.FromVisual(canvas)!;
            SWI.Keyboard.Focus(canvas);

            Press(source, 0x2E);   // Delete: WPF leaves it
            Press(source, 0x41);   // A: WPF handles it
            Pump(() => false, 0.3);

            Assert.Contains(SWF.Keys.Delete, cmd.Seen);
            Assert.DoesNotContain(SWF.Keys.A, cmd.Seen);
            form.Close();
        }

        /// <summary>A key press and release as WPF's keyboard provider reports one for the window.</summary>
        private static void Press(System.Windows.Interop.HwndSource source, int vk)
        {
            var pc = typeof(System.Windows.Media.Visual).Assembly;
            Type rawT = pc.GetType("System.Windows.Input.RawKeyboardInputReport")!, actT = pc.GetType("System.Windows.Input.RawKeyboardActions")!;
            Type argsT = pc.GetType("System.Windows.Input.InputReportEventArgs")!;
            var preview = (System.Windows.RoutedEvent)typeof(SWI.InputManager).GetField("PreviewInputReportEvent", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)!.GetValue(null)!;
            const BindingFlags Any = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
            foreach (string action in new[] { "KeyDown", "KeyUp" })
            {
                object report = Activator.CreateInstance(rawT, Any, null,
                    new object[] { source, SWI.InputMode.Foreground, Environment.TickCount, Enum.Parse(actT, action), 0, false, false, vk, IntPtr.Zero }, null)!;
                var args = (SWI.InputEventArgs)Activator.CreateInstance(argsT, Any, null, new object[] { SWI.Keyboard.PrimaryDevice, report }, null)!;
                args.RoutedEvent = preview;
                SWI.InputManager.Current.ProcessInput(args);
            }
        }

        private static void Pump(Func<bool> until, double seconds)
        {
            for (var end = DateTime.UtcNow.AddSeconds(seconds); DateTime.UtcNow < end && !until();)
            {
                SWF.Application.DoEvents();
                s_tick?.Invoke(null, null);
                Thread.Sleep(15);
            }
        }

        private static readonly MethodInfo? s_tick = typeof(SWF.Control).Assembly.GetType("System.Windows.Forms.PresentationHost")
            ?.GetMethod("Tick", BindingFlags.NonPublic | BindingFlags.Static);
    }
}
