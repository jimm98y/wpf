//
// System.Windows.Forms.Design.AxWrapperGen.cs
//
// Author:
//   Gert Driesen (drieseng@users.sourceforge.net)
//
// (C) 2004 Novell
//

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

using System.Collections;

namespace System.Windows.Forms.Design
{
	public class AxWrapperGen
	{
		// AxWrapperGen writes the AxHost wrapper source for an ActiveX control's type library; ActiveX is not
		// supported by this stack (see AxHost), so there is no wrapper to generate.
		public AxWrapperGen (Type axType)
		{
			throw new PlatformNotSupportedException (OperatingSystem.IsWindows ()
				? "ActiveX controls cannot be hosted by the WebGPU WinForms stack: it does not activate COM objects or site them in native windows."
				: "ActiveX controls are a Windows COM technology and are not available on this platform.");
		}

		public static ArrayList GeneratedSources = new ArrayList ();
	}
}
