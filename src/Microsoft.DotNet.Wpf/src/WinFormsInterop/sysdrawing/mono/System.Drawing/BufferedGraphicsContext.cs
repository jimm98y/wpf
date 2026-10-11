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
//   Jordi Mas i Hernandez <jordimash@gmail.com>
//
//


using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Serialization;

namespace System.Drawing
{
	public sealed class BufferedGraphicsContext : IDisposable
	{
		private Size max_buffer;

		public BufferedGraphicsContext ()
		{
			// System.Drawing's default: buffers up to 225 x 96 are kept for reuse.
			max_buffer = new Size (225, 96);
		}

		~BufferedGraphicsContext ()
		{
		}

		public BufferedGraphics Allocate (Graphics targetGraphics, Rectangle targetRectangle)
		{
			// System.Drawing asks the target for its DC (Graphics.GetHdc, with the Graphics' own
			// checks) and makes a buffer compatible with it; with no target there is no DC.
			if (targetGraphics == null)
				throw new ArgumentNullException ("hdc");
			targetGraphics.CheckUsable ();
			BufferedGraphics graphics = new BufferedGraphics (targetGraphics, targetRectangle);
			return graphics;
		}

		public BufferedGraphics Allocate (IntPtr targetDC, Rectangle targetRectangle)
		{
			if (targetDC == IntPtr.Zero)
				throw new ArgumentNullException ("hdc");
			return new BufferedGraphics (targetDC, targetRectangle);
		}

		public void Dispose ()
		{
			System.GC.SuppressFinalize (this);
		}

		public void Invalidate ()
		{
		}

		public Size MaximumBuffer {
			get {return max_buffer; }
			set {
				if (value.Width <= 0 || value.Height <= 0)
					throw new ArgumentException (SR.Format (SR.InvalidArgument, "MaximumBuffer", value), "value");

				max_buffer = value;
			}
		}

	}
}

