//
// System.Windows.Forms.Design.DesignerOptions.cs
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
using System.ComponentModel;
using System.Drawing;

namespace System.Windows.Forms.Design
{
	public class DesignerOptions
	{
		// The designer's own defaults, as documented for this class: everything on except snap
		// lines, on an 8x8 grid. A host overwrites whichever of these it has an opinion about --
		// SharpDevelop sets all eight from its own settings the moment its option service is
		// constructed, which is why these have to be settable and not just readable.
		private bool enable_in_situ_editing = true;
		private Size grid_size = new Size (8, 8);
		private bool object_bound_smart_tag_auto_show = true;
		private bool show_grid = true;
		private bool snap_to_grid = true;
		private bool use_optimized_code_generation = true;
		private bool use_smart_tags = true;
		private bool use_snap_lines = false;

		public DesignerOptions ()
		{
		}

		[Browsable (false)]
		public virtual bool EnableInSituEditing {
			get { return enable_in_situ_editing; }
			set { enable_in_situ_editing = value; }
		}

		public virtual Size GridSize {
			get { return grid_size; }
			set { grid_size = value; }
		}

		public virtual bool ObjectBoundSmartTagAutoShow {
			get { return object_bound_smart_tag_auto_show; }
			set { object_bound_smart_tag_auto_show = value; }
		}

		public virtual bool ShowGrid {
			get { return show_grid; }
			set { show_grid = value; }
		}

		public virtual bool SnapToGrid {
			get { return snap_to_grid; }
			set { snap_to_grid = value; }
		}

		public virtual bool UseOptimizedCodeGeneration {
			get { return use_optimized_code_generation; }
			set { use_optimized_code_generation = value; }
		}

		public virtual bool UseSmartTags {
			get { return use_smart_tags; }
			set { use_smart_tags = value; }
		}

		public virtual bool UseSnapLines {
			get { return use_snap_lines; }
			set { use_snap_lines = value; }
		}
	}
}
