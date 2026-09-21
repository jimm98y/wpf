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
			// The modern Windows look is the default, as it is on Windows 11 itself. The classic
			// theme is still here and still selectable -- an application that wants the pre-visual-
			// styles look, or one whose custom drawing assumes those two-pixel bevels, sets
			// WF_THEME=classic and gets exactly what it got before.
			//
			// Application.EnableVisualStyles() deliberately does NOT select ThemeVisualStyles here,
			// and that is the opposite of what it looks like it should do. ThemeVisualStyles does not
			// draw anything itself: it asks uxtheme to, through a real device context taken with
			// Graphics.GetHdc. There is no such context on this port -- a Graphics here records into
			// a GPU scene -- so GetHdc throws, and it throws inside OnPaint of the first Button on
			// the form. Since EnableVisualStyles is the first line of Program.cs in every application
			// the Visual Studio template has ever generated, that made ordinary WinForms applications
			// die on their first paint.
			//
			// ThemeWin11 IS the visual-styles look; it is this port's own drawing of it, and it needs
			// nothing from uxtheme. So asking for visual styles gets the visual-styles appearance,
			// which is what the caller wanted. The uxtheme-backed theme stays reachable by name for a
			// GDI-backed surface that can satisfy it.
			string requested = Environment.GetEnvironmentVariable ("WF_THEME");

			if (string.Equals (requested, "classic", StringComparison.OrdinalIgnoreCase)) {
				theme = new ThemeWin32Classic ();
			} else if (string.Equals (requested, "visualstyles", StringComparison.OrdinalIgnoreCase)) {
				theme = new ThemeVisualStyles ();
			} else {
				theme = new ThemeWin11 ();
			}
		}
		
			
		public static Theme Current {
			get { return theme; }
		}
		
	}
}
