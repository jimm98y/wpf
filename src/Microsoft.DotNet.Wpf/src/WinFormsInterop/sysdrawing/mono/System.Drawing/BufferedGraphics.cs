//
// Copyright (C) 2005 Novell, Inc (http://www.novell.com)
//
// Permission is hereby granted, free of charge, to any person obtaining
// a copy of this software and associated documentation files (the
// "Software"), to deal in the Software without restriction, including
// without limitation the rights to use, copy, modify, merge, publish,
// distribute, sublicense, and/or sell copies of the Software, and to
// permit persons to whom the Software is furnished to do so, subject to
// the following conditions:
//
// The above copyright notice and this permission notice shall be
// included in all copies or substantial portions of the Software.
//
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND,
// EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF
// MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND
// NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE
// LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION
// OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION
// WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
//
// Authors:
//
//   Jordi Mas i Hernadez <jordimash@gmail.com>
//
//


using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Serialization;

namespace System.Drawing
{
	public sealed class BufferedGraphics : IDisposable
	{
		private Rectangle size;
		private Bitmap membmp = null;
		private Graphics target = null;
		private Graphics source = null;

		private BufferedGraphics ()
		{

		}

		private IntPtr targetDc;
		private bool disposed;

		internal BufferedGraphics (Graphics targetGraphics, Rectangle targetRectangle)
		{
			size = targetRectangle;
			target = targetGraphics;
			// An empty area still has a (1 x 1) buffer, as System.Drawing's DIB does.
			membmp = new Bitmap (Math.Max (1, size.Width), Math.Max (1, size.Height));
		}

		internal BufferedGraphics (IntPtr dc, Rectangle targetRectangle) : this ((Graphics) null, targetRectangle)
		{
			targetDc = dc;
		}

		~BufferedGraphics ()
		{
			Dispose (false);
		}

	      	public Graphics Graphics {
			get {
				// Disposed, the buffer's Graphics is gone (null).
				if (disposed)
					return null;
				if (source == null) {
					source = Graphics.FromImage (membmp);
				}

				return source;
			}
		}

	      	public void Dispose ()
		{
			Dispose (true);
			System.GC.SuppressFinalize (this);
		}

		private void Dispose (bool disposing)
		{
			if (disposing == false)
				return;
			disposed = true;

			if (membmp != null) {
				membmp.Dispose ();
				membmp = null;
			}

			if (source != null) {
				source.Dispose ();
				source = null;
			}

			target = null;
		}

	      	public void Render ()
		{
			if (target != null)
				Render (target);
			else
				Render (targetDc);
		}

	      	public void Render (Graphics target)
		{
			if (target == null)
				return;
			// System.Drawing renders through the buffer's own Graphics, which a disposed buffer has not.
			if (disposed)
				throw new NullReferenceException ();
			target.DrawImage (membmp, size);
		}

		// The buffer onto a DC: BitBlt, which does nothing to no DC.
	      	public void Render (IntPtr targetDC)
		{
			if (disposed)
				throw new NullReferenceException ();
			if (targetDC == IntPtr.Zero)
				return;
			using (Graphics g = Graphics.FromHdc (targetDC))
				g.DrawImage (membmp, size);
		}
	}
}

