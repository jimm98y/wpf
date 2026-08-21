//
// System.Windows.Forms.Design.ShortcutKeysEditor.cs
//
// Author:
//	Atsushi Enomoto (atsushi@ximian.com)
//
// Copyright (C) 2007 Novell, Inc.
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


using System;
using System.ComponentModel.Design;

namespace System.Windows.Forms.Design
{
	public class WindowsFormsDesignerOptionService : DesignerOptionService
	{
		private DesignerOptions compatibility_options;

		public WindowsFormsDesignerOptionService ()
		{
		}

		public virtual DesignerOptions CompatibilityOptions {
			get {
				if (compatibility_options == null)
					compatibility_options = new DesignerOptions ();
				return compatibility_options;
			}
		}

		// The shape every Windows Forms designer host expects to find:
		//   (root) -> "WindowsFormsDesigner" -> "General", backed by CompatibilityOptions.
		// Options.Properties walks to that leaf, so leaving this unimplemented threw
		// NotImplementedException out of the option service's constructor and the designer
		// reported it as "Failed to load designer. Check the source code for syntax errors".
		protected override void PopulateOptionCollection (DesignerOptionService.DesignerOptionCollection options)
		{
			if (options.Parent != null)
				return;

			DesignerOptionCollection designer = CreateOptionCollection (options, "WindowsFormsDesigner", null);
			CreateOptionCollection (designer, "General", CompatibilityOptions);
		}
	}
}
