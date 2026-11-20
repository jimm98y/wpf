// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// Media Foundation's video renderer of a control's handle: its frame drawn over the control, in its form's host only
// (WgpuInterop's Composition/VideoSurfaces.cs, host/VideoSurfaceScene.cs), letterboxed of the picture's shape.
//

using System;
using System.Drawing;
using System.Linq;
using Microsoft.Wpf.Interop.WebGpu.Composition;
using Microsoft.Wpf.Interop.WebGpu.MediaFoundation;
using Microsoft.Wpf.Interop.WebGpu.MediaFoundation.Session;
using Xunit;
using SWF = System.Windows.Forms;

namespace Wpf.WinFormsInterop.Tests
{
    public unsafe class VideoSurfaceTests
    {
        private static MFMediaType Nv12(int w, int h)
        {
            var t = new MFMediaType();
            t.Attributes.Set(MF.MF_MT_MAJOR_TYPE, MF.MFMediaType_Video);
            t.Attributes.Set(MF.MF_MT_SUBTYPE, MF.MFVideoFormat_NV12);
            t.Attributes.Set(MF.MF_MT_FRAME_SIZE, MF.Pack((uint)w, (uint)h));
            return t;
        }

        [Fact]
        public void TheVideoRenderer_DrawsItsFrameOverTheControl()
        {
            SWF.XplatUIWebGpu driver = SWF.XplatUIWebGpu.GetInstance();
            using var form = new SWF.Form { StartPosition = SWF.FormStartPosition.Manual, Location = new Point(9400, 4800), ClientSize = new Size(320, 240), ShowInTaskbar = false };
            var panel = new SWF.Panel { Bounds = new Rectangle(20, 30, 200, 100) };
            form.Controls.Add(panel);
            form.Show();
            SWF.Application.DoEvents();

            var r = new MFVideoRenderer(panel.Handle);
            try
            {
                var handler = (VideoTypeHandler)r.Stream.Handler;
                handler.Current = Nv12(64, 48);
                // a grey frame
                var bytes = new byte[64 * 48 * 3 / 2];
                Array.Fill(bytes, (byte)128);
                var s = new MFSample();
                var buf = new MFMemoryBuffer((uint)bytes.Length);
                byte* p = buf.LockBytes(true);
                bytes.CopyTo(new Span<byte>(p, bytes.Length));
                buf.UnlockBytes();
                buf.CurrentLength = (uint)bytes.Length;
                s.Buffers.Add(buf.Pointer);
                int before = VideoSurfaces.Version;
                r.Deliver(s.Pointer, MF.MFMediaType_Video, 0, 400_000);
                s.Release();
                Assert.True(VideoSurfaces.Version > before);

                Point fo = driver.ScreenLocation(SWF.Hwnd.ObjectFromHandle(form.Handle));
                var scenes = SWF.EmbeddedScenes.Get(fo.X, fo.Y);
                Assert.NotNull(scenes);
                var (scene, x, y) = Assert.Single(scenes!);
                Point po = driver.ScreenLocation(SWF.Hwnd.ObjectFromHandle(panel.Handle));
                Assert.Equal((po.X - fo.X, po.Y - fo.Y), (x, y));
                // the 4:3 picture in the 200x100 panel: 133x100, centred, the black border at its sides
                var v = Assert.IsType<SceneVisual>(scene);
                var frame = Assert.Single(v.Children);
                Assert.Equal(new System.Numerics.Vector2(33, 0), frame.Offset);
                var fill = Assert.IsType<GeometryFill>(Assert.Single(frame.Content));
                var brush = Assert.IsType<ImageBrush>(fill.Brush);
                Assert.NotNull(brush.External);
                Assert.Equal((64, 48), (brush.External!.Width, brush.External.Height));
                Assert.Equal(2, v.Content.Count);

                // in another form's host: not there
                Assert.Null(SWF.EmbeddedScenes.Get(fo.X + 1000, fo.Y));
            }
            finally { r.Shutdown(); r.Release(); }
            Assert.Null(SWF.EmbeddedScenes.Get(0, 0));
        }
    }
}
