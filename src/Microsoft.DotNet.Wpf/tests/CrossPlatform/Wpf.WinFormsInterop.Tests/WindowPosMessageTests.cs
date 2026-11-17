// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// SetWindowPos asks the window first, as Windows does: WM_WINDOWPOSCHANGING with a WINDOWPOS it may change
// (a control that keeps itself square) or forbid (SWP_NOMOVE, SWP_NOSIZE), and the window goes where the
// WINDOWPOS says. WM_WINDOWPOSCHANGED then carries a WINDOWPOS of where it is -- it was sent with none, and
// a window procedure that reads it read through a null pointer.
//

using System;
using System.Drawing;
using System.Runtime.InteropServices;
using Xunit;
using SWF = System.Windows.Forms;

namespace Wpf.WinFormsInterop.Tests
{
    public class WindowPosMessageTests
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct WINDOWPOS
        {
            public IntPtr hwnd, hwndInsertAfter;
            public int x, y, cx, cy;
            public uint flags;
        }

        private const uint SWP_NOSIZE = 0x1, SWP_NOMOVE = 0x2;

        private sealed class Square : SWF.Control
        {
            public bool Pinned;
            public WINDOWPOS? Changed;

            protected override void WndProc(ref SWF.Message m)
            {
                if (m.Msg == 0x46)   // WM_WINDOWPOSCHANGING
                {
                    var p = Marshal.PtrToStructure<WINDOWPOS>(m.LParam);
                    if ((p.flags & SWP_NOSIZE) == 0) p.cy = p.cx;     // keeps itself square
                    if (Pinned) p.flags |= SWP_NOMOVE;                // and, pinned, where it is
                    Marshal.StructureToPtr(p, m.LParam, false);
                }
                else if (m.Msg == 0x47 && m.LParam != IntPtr.Zero)   // WM_WINDOWPOSCHANGED
                    Changed = Marshal.PtrToStructure<WINDOWPOS>(m.LParam);
                base.WndProc(ref m);
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT { public int left, top, right, bottom; }

        /// <summary>A form that keeps itself square while it is sized, from WM_SIZING.</summary>
        private sealed class SquareForm : SWF.Form
        {
            public int Edge;

            protected override void WndProc(ref SWF.Message m)
            {
                if (m.Msg == 0x214)   // WM_SIZING
                {
                    Edge = (int)(long)m.WParam;
                    var r = Marshal.PtrToStructure<RECT>(m.LParam);
                    r.bottom = r.top + (r.right - r.left);
                    Marshal.StructureToPtr(r, m.LParam, false);
                    m.Result = (IntPtr)1;
                    return;
                }
                base.WndProc(ref m);
            }
        }

        [Fact]
        public void EachStepOfAResize_AsksTheWindow()
        {
            SWF.XplatUIWebGpu driver = SWF.XplatUIWebGpu.GetInstance();
            using var form = new SquareForm { ClientSize = new Size(200, 200) };
            Assert.NotEqual(IntPtr.Zero, form.Handle);
            Rectangle asked = driver.HostSizing(form.Handle, 8, new Rectangle(100, 100, 300, 180));
            Assert.Equal(8, form.Edge);                                   // WMSZ_BOTTOMRIGHT
            Assert.Equal(new Rectangle(100, 100, 300, 300), asked);       // the form's own answer
        }

        [Fact]
        public void TheWindowGoesWhereItsWindowPosChangingSays()
        {
            using var form = new SWF.Form { ClientSize = new Size(400, 300) };
            var square = new Square { Bounds = new Rectangle(10, 10, 50, 50) };
            form.Controls.Add(square);
            form.CreateControl();
            Assert.NotEqual(IntPtr.Zero, square.Handle);              // a window to send the messages to

            square.Size = new Size(120, 40);
            Assert.Equal(new Size(120, 120), square.Size);            // the handler made it square

            square.Pinned = true;
            square.Bounds = new Rectangle(80, 90, 60, 20);
            Assert.Equal(new Point(10, 10), square.Location);         // SWP_NOMOVE: it stayed
            Assert.Equal(new Size(60, 60), square.Size);              // and the size was still its own

            Assert.NotNull(square.Changed);                           // WM_WINDOWPOSCHANGED carried a WINDOWPOS
            WINDOWPOS c = square.Changed!.Value;
            Assert.Equal(square.Handle, c.hwnd);
            Assert.Equal((10, 10, 60, 60), (c.x, c.y, c.cx, c.cy));   // where the window is now
        }
    }
}
