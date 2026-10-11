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
// Copyright (c) 2004-2006 Novell, Inc.
//
// Authors:
//	Jordi Mas i Hernandez, jordi@ximian.com
//
//


using System;

namespace System.Windows.Forms
{
	internal class ThemeEngine
	{
		static private Theme theme = null;
		
		static ThemeEngine ()
		{
			// The Windows 11 look, drawn by this port in managed code, on every head. The classic
			// theme stays selectable (WF_THEME=classic) for an application whose custom drawing
			// assumes its two-pixel bevels.
			//
			// There is deliberately no uxtheme-backed theme any more. It drew by asking uxtheme,
			// through a device context taken with Graphics.GetHdc -- there is no such context here, a
			// Graphics records into a GPU scene -- and it could only ever have worked on Windows. The
			// public renderers (CheckBoxRenderer, VisualStyleRenderer...) draw through the same
			// managed theme via VisualStylesWin11.
			string requested = Environment.GetEnvironmentVariable ("WF_THEME");

			if (string.Equals (requested, "classic", StringComparison.OrdinalIgnoreCase))
				theme = new ThemeWin32Classic ();
			else
				theme = new ThemeWin11 ();
		}
		
			
		public static Theme Current {
			get { return theme; }
		}
		
	}
}
