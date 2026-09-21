// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// Draws a specimen and hands back its pixels. Compiled into both programs, like Specimens.cs, so
// that both sides do the identical thing with the identical code.
//
// The pixels travel as a RAW file, not a PNG: two stacks encoding the same image through two
// different PNG writers can disagree about filters, bit depth and palette while the image is the
// same, and a comparison that goes through both codecs is measuring them as well. Four bytes of
// width, four of height, then BGRA, is a format neither side can get creatively wrong.
//

using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace WinFormsControlParity
{
    internal static class SpecimenRenderer
    {
        /// <summary>Render one specimen and return its pixels as BGRA, row by row from the top.
        /// </summary>
        internal static byte[] Render(Specimen spec, out int width, out int height)
        {
            width = spec.Width;
            height = spec.Height;

            // A control paints differently, or not at all, until it belongs to a created window: a
            // theme asks the control for its parent's back colour, and several ask whether there is
            // a handle at all. So each specimen gets a real (never shown) form to live on.
            using var host = new Form { ClientSize = new Size(spec.Width + 20, spec.Height + 20) };
            Control c = spec.Create();
            c.Left = 10;
            c.Top = 10;
            c.Width = spec.Width;
            c.Height = spec.Height;
            host.Controls.Add(c);

            // Touching Handle is what forces the window to exist. CreateControl() will not do it:
            // it gives up on a control that is not visible, and a form that has never been shown is
            // not. Controls that measure something before they can draw -- LinkLabel builds its
            // pieces in OnHandleCreated, TabControl lays its tabs out -- then had nothing to draw
            // and produced an empty bitmap, which reads exactly like the control being unimplemented.
            IntPtr unused = host.Handle;
            unused = c.Handle;
            c.Refresh();

            using var bmp = new Bitmap(spec.Width, spec.Height, PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(bmp))
                g.Clear(SystemColors.Control);   // the same ground under both, so only the ink differs
            c.DrawToBitmap(bmp, new Rectangle(0, 0, spec.Width, spec.Height));

            var pixels = new byte[spec.Width * spec.Height * 4];
            BitmapData data = bmp.LockBits(new Rectangle(0, 0, spec.Width, spec.Height),
                                           ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try
            {
                for (int y = 0; y < spec.Height; y++)
                    Marshal.Copy(data.Scan0 + y * data.Stride, pixels, y * spec.Width * 4, spec.Width * 4);
            }
            finally
            {
                bmp.UnlockBits(data);
            }
            return pixels;
        }

        /// <summary>Render every specimen into <paramref name="dir"/> as name.raw.</summary>
        internal static int RenderAll(string dir)
        {
            Directory.CreateDirectory(dir);
            int done = 0;
            foreach (Specimen spec in Specimens.All())
            {
                try
                {
                    byte[] pixels = Render(spec, out int w, out int h);
                    using var fs = new FileStream(Path.Combine(dir, spec.Name + ".raw"), FileMode.Create);
                    using var bw = new BinaryWriter(fs);
                    bw.Write(w);
                    bw.Write(h);
                    bw.Write(pixels);
                    done++;
                }
                catch (Exception e)
                {
                    // A specimen one side cannot draw is worth knowing about by name, and is not a
                    // reason to lose the other thirty.
                    File.WriteAllText(Path.Combine(dir, spec.Name + ".failed"), e.ToString());
                }
            }
            return done;
        }
    }
}
