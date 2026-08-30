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
// Copyright (c) 2004-2005 Novell, Inc.
//
// Authors:
//	Jordi Mas i Hernandez, jordi@ximian.com
//	Peter Dennis Bartok, pbartok@novell.com
//

using System.Collections;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Reflection;

namespace System.Windows.Forms
{
	internal enum UIIcon {
		PlacesRecentDocuments,
		PlacesDesktop,
		PlacesPersonal,
		PlacesMyComputer,
		PlacesMyNetwork,
		MessageBoxError,
		MessageBoxQuestion,
		MessageBoxWarning,
		MessageBoxInfo,
		
		NormalFolder
	}
	
	internal struct CPColor {
		internal Color Dark;
		internal Color DarkDark;
		internal Color Light;
		internal Color LightLight;
		
		internal static CPColor Empty;
	}
	
	// Implements a pool of system resources
	internal class SystemResPool
	{
		private Hashtable pens = new Hashtable ();
		private Hashtable dashpens = new Hashtable ();
		private Hashtable sizedpens = new Hashtable ();
		private Hashtable solidbrushes = new Hashtable ();
		private Hashtable hatchbrushes = new Hashtable ();
		private Hashtable uiImages = new Hashtable();
		private Hashtable cpcolors = new Hashtable ();
		
		public SystemResPool () {}
		
		public Pen GetPen (Color color)
		{
			int hash = color.ToArgb ();

			lock (pens) {
				Pen res = pens [hash] as Pen;
				if (res != null)
					return res;
			
				Pen pen = new Pen (color);
				pens.Add (hash, pen);
				return pen;
			}
		}
		
		public Pen GetDashPen (Color color, DashStyle dashStyle)
		{
			string hash = color.ToString() + dashStyle;

			lock (dashpens) {
				Pen res = dashpens [hash] as Pen;
				if (res != null)
					return res;
			
				Pen pen = new Pen (color);
				pen.DashStyle = dashStyle;
				dashpens [hash] = pen;
				return pen;
			}
		}
		
		public Pen GetSizedPen (Color color, int size)
		{
			string hash = color.ToString () + size;
			
			lock (sizedpens) {
				Pen res = sizedpens [hash] as Pen;
				if (res != null)
					return res;
			
				Pen pen = new Pen (color, size);
				sizedpens [hash] = pen;
				return pen;
			}
		}
		
		public SolidBrush GetSolidBrush (Color color)
		{
			int hash = color.ToArgb ();

			lock (solidbrushes) {
				SolidBrush res = solidbrushes [hash] as SolidBrush;
				if (res != null)
					return res;
			
				SolidBrush brush = new SolidBrush (color);
				solidbrushes.Add (hash, brush);
				return brush;
			}
		}		
		
		public HatchBrush GetHatchBrush (HatchStyle hatchStyle, Color foreColor, Color backColor)
		{
			string hash = ((int)hatchStyle).ToString () + foreColor.ToString () + backColor.ToString ();

			lock (hatchbrushes) {
				HatchBrush brush = (HatchBrush) hatchbrushes[hash];
				if (brush == null) {
					brush = new HatchBrush (hatchStyle, foreColor, backColor);
					hatchbrushes.Add (hash, brush);
				}
				return brush;
			}
		}
		
		public void AddUIImage (Image image, string name, int size)
		{
			string hash = name + size.ToString();

			lock (uiImages) {
				if (uiImages.Contains (hash))
					return;
				uiImages.Add (hash, image);
			}
		}
		
		public Image GetUIImage(string name, int size)
		{
			string hash = name + size.ToString();
			
			Image image = uiImages [hash] as Image;
			
			return image;
		}
		
		public CPColor GetCPColor (Color color)
		{
			lock (cpcolors) {
				object tmp = cpcolors [color];
			
				if (tmp == null) {
					CPColor cpcolor = new CPColor ();
					cpcolor.Dark = ControlPaint.Dark (color);
					cpcolor.DarkDark = ControlPaint.DarkDark (color);
					cpcolor.Light = ControlPaint.Light (color);
					cpcolor.LightLight = ControlPaint.LightLight (color);
				
					cpcolors.Add (color, cpcolor);

					return cpcolor;
				}
			
				return (CPColor)tmp;
			}
		}
	}

	internal abstract class Theme
	{
		protected Array syscolors;
		Font default_font;
		protected Color defaultWindowBackColor;
		protected Color defaultWindowForeColor;
		internal SystemResPool ResPool = new SystemResPool ();
		private int[] knownColorTable;

		protected Theme ()
		{
		}

		private void SetSystemColors (KnownColor kc, Color value)
		{
			if (knownColorTable == null) {
				// Through the assembly that defines Color, not by NAME. This port's drawing assembly is
				// called Mono.System.Drawing, so asking for it as "System.Drawing" returned null and every
				// setter below became a silent no-op -- the same fault that left PrintDialog unable to
				// print. Nothing assigns these today, which is the only reason it has not been noticed.
				Type knownColorTableClass = typeof (Color).Assembly.GetType ("System.Drawing.KnownColorTable");
				if (knownColorTableClass != null)
				{
					MethodInfo ensureMethod = knownColorTableClass.GetMethod ("EnsureColorTable", BindingFlags.Static | BindingFlags.NonPublic);
					if (ensureMethod != null) {
						ensureMethod.Invoke(null, new object[]{});
						FieldInfo colorTableField = knownColorTableClass.GetField ("s_colorTable", BindingFlags.Static | BindingFlags.NonPublic);
						if (colorTableField != null) {
							knownColorTable = colorTableField.GetValue (null) as int[];
						}
					}
				}
			}
			if (knownColorTable != null) {
				knownColorTable[(int)kc] = value.ToArgb();
			}
		}

		/* OS Feature support */
		public abstract Version Version {
			get;
		}

		/* Default properties */
		public virtual Color ColorScrollBar {
			get { return SystemColors.ScrollBar; }
			set { SetSystemColors (KnownColor.ScrollBar, value); }
		}

		public virtual Color ColorDesktop {
			get { return SystemColors.Desktop;}
			set { SetSystemColors (KnownColor.Desktop, value); }
		}

		public virtual Color ColorActiveCaption {
			get { return SystemColors.ActiveCaption;}
			set { SetSystemColors (KnownColor.ActiveCaption, value); }
		}

		public virtual Color ColorInactiveCaption {
			get { return SystemColors.InactiveCaption;}
			set { SetSystemColors (KnownColor.InactiveCaption, value); }
		}

		public virtual Color ColorMenu {
			get { return SystemColors.Menu;}
			set { SetSystemColors (KnownColor.Menu, value); }
		}

		public virtual Color ColorWindow {
			get { return SystemColors.Window;}
			set { SetSystemColors (KnownColor.Window, value); }
		}

		public virtual Color ColorWindowFrame {
			get { return SystemColors.WindowFrame;}
			set { SetSystemColors (KnownColor.WindowFrame, value); }
		}

		public virtual Color ColorMenuText {
			get { return SystemColors.MenuText;}
			set { SetSystemColors (KnownColor.MenuText, value); }
		}

		public virtual Color ColorWindowText {
			get { return SystemColors.WindowText;}
			set { SetSystemColors (KnownColor.WindowText, value); }
		}

		public virtual Color ColorActiveCaptionText {
			get { return SystemColors.ActiveCaptionText;}
			set { SetSystemColors (KnownColor.ActiveCaptionText, value); }
		}

		public virtual Color ColorActiveBorder {
			get { return SystemColors.ActiveBorder;}
			set { SetSystemColors (KnownColor.ActiveBorder, value); }
		}

		public virtual Color ColorInactiveBorder{
			get { return SystemColors.InactiveBorder;}
			set { SetSystemColors (KnownColor.InactiveBorder, value); }
		}

		public virtual Color ColorAppWorkspace {
			get { return SystemColors.AppWorkspace;}
			set { SetSystemColors (KnownColor.AppWorkspace, value); }
		}

		public virtual Color ColorHighlight {
			get { return SystemColors.Highlight;}
			set { SetSystemColors (KnownColor.Highlight, value); }
		}

		public virtual Color ColorHighlightText {
			get { return SystemColors.HighlightText;}
			set { SetSystemColors (KnownColor.HighlightText, value); }
		}

		/// <summary>What fills a combo box's drop-down button before the glyph goes on. Classic
		/// paints it as a raised control-coloured button; a modern one blends it into the field.</summary>
		public virtual Color ComboBoxDropDownButtonBackColor (ComboBox comboBox)
		{
			return ColorControl;
		}

		/// <summary>Draw a property grid's expander. Classic uses a boxed +/-, Windows a chevron.</summary>
		public virtual void DrawPropertyGridExpander (Graphics dc, Rectangle bounds, bool expanded, bool category, Color foreColor)
		{
			if (!category)
				dc.FillRectangle (Brushes.White, bounds);

			Pen pen = ResPool.GetPen (foreColor);
			dc.DrawRectangle (pen, bounds);
			dc.DrawLine (pen, bounds.X + 2, bounds.Y + 4, bounds.X + 6, bounds.Y + 4);
			if (!expanded)
				dc.DrawLine (pen, bounds.X + 4, bounds.Y + 2, bounds.X + 4, bounds.Y + 6);
		}

		/// <summary>The renderer a tool bar of the given appearance should draw itself with.
		/// Controls that build their own tool bar -- the property grid does -- ask here rather than
		/// hard-coding one, so the answer follows the theme.</summary>
		/// <summary>How much taller a row is in a menu Windows draws itself. None in the classic
		/// look, which draws every menu alike.</summary>
		public virtual int SystemMenuExtraRowHeight {
			get { return 0; }
		}

		/// <summary>How much wider Windows makes a menu it draws itself than the same entries in a
		/// strip menu. It leaves more air round a caption than the strips do. None in the
		/// classic look, which draws every menu alike.</summary>
		public virtual int SystemMenuExtraWidth {
			get { return 0; }
		}

		/// <summary>The renderer for a menu of the old kind -- one Windows would draw itself.
		/// The classic theme draws it like any other menu.</summary>
		/// <summary>The renderer every tool strip and menu is drawn with by default. A theme that
		/// paints its strips differently -- a modern one draws a soft edge down the right of a tool
		/// bar where the professional renderer draws a flat line -- hands out its own.</summary>
		public virtual ToolStripRenderer CreateToolStripRenderer ()
		{
			return new ToolStripProfessionalRenderer (ColorTable);
		}

		public virtual ToolStripRenderer CreateSystemMenuRenderer ()
		{
			return null;
		}

		public virtual ToolStripRenderer CreateToolBarRenderer (ToolBarAppearance appearance)
		{
			if (appearance == ToolBarAppearance.Flat)
				return new ToolStripSystemRenderer ();

			ProfessionalColorTable table = new ProfessionalColorTable ();
			table.UseSystemColors = true;
			return new ToolStripProfessionalRenderer (table);
		}

		/// <summary>The tool strip and menu colours this theme wants the professional renderer to
		/// use. Classic keeps the Office 2003 table it has always used; a modern theme returns its
		/// own, so the renderer follows the theme instead of being fixed at construction.</summary>
		public virtual ProfessionalColorTable ColorTable {
			get {
				if (color_table == null)
					color_table = new ProfessionalColorTable ();
				return color_table;
			}
		}
		private ProfessionalColorTable color_table;

		public virtual Color ColorControl {
			get { return SystemColors.Control;}
			set { SetSystemColors (KnownColor.Control, value); }
		}

		public virtual Color ColorControlDark {
			get { return SystemColors.ControlDark;}
			set { SetSystemColors (KnownColor.ControlDark, value); }
		}

		public virtual Color ColorGrayText {
			get { return SystemColors.GrayText;}
			set { SetSystemColors (KnownColor.GrayText, value); }
		}

		public virtual Color ColorControlText {
			get { return SystemColors.ControlText;}
			set { SetSystemColors (KnownColor.ControlText, value); }
		}

		public virtual Color ColorInactiveCaptionText {
			get { return SystemColors.InactiveCaptionText;}
			set { SetSystemColors (KnownColor.InactiveCaptionText, value); }
		}

		public virtual Color ColorControlLight {
			get { return SystemColors.ControlLight;}
			set { SetSystemColors (KnownColor.ControlLight, value); }
		}

		public virtual Color ColorControlDarkDark {
			get { return SystemColors.ControlDarkDark;}
			set { SetSystemColors (KnownColor.ControlDarkDark, value); }
		}

		public virtual Color ColorControlLightLight {
			get { return SystemColors.ControlLightLight;}
			set { SetSystemColors (KnownColor.ControlLightLight, value); }
		}

		public virtual Color ColorButtonFace {
			get { return SystemColors.ButtonFace;}
			set { SetSystemColors (KnownColor.ButtonFace, value); }
		}

		public virtual Color ColorInfoText {
			get { return SystemColors.InfoText;}
			set { SetSystemColors (KnownColor.InfoText, value); }
		}

		public virtual Color ColorInfo {
			get { return SystemColors.Info;}
			set { SetSystemColors (KnownColor.Info, value); }
		}

		public virtual Color ColorHotTrack {
			get { return SystemColors.HotTrack;}
			set { SetSystemColors (KnownColor.HotTrack, value);}
		}

		public virtual Color DefaultControlBackColor {
			get { return ColorControl; }
			set { ColorControl = value; }
		}

		public virtual Color DefaultControlForeColor {
			get { return ColorControlText; }
			set { ColorControlText = value; }
		}

		public virtual Font DefaultFont {
			get { return default_font ?? (default_font = SystemFonts.DefaultFont); }
		}

		public virtual Color DefaultWindowBackColor {
			get { return defaultWindowBackColor; }
		}

		public virtual Color DefaultWindowForeColor {
			get { return defaultWindowForeColor; }
		}

		public virtual Color GetColor (XplatUIWin32.GetSysColorIndex idx)
		{
			return (Color) syscolors.GetValue ((int)idx);
		}

		public virtual void SetColor (XplatUIWin32.GetSysColorIndex idx, Color color)
		{
			syscolors.SetValue (color, (int) idx);
		}

		// Theme/UI specific defaults
		public virtual ArrangeDirection ArrangeDirection  {
			get {
				return ArrangeDirection.Down;
			}
		}

		public virtual ArrangeStartingPosition ArrangeStartingPosition {
			get {
				return ArrangeStartingPosition.BottomLeft;
			}
		}

		public virtual int BorderMultiplierFactor { get { return 1; } }
		
		public virtual Size BorderSizableSize {
			get {
				return new Size (3, 3);
			}
		}

		// A calendar's cell size and the margin around its grid are both Windows metrics, and the
		// classic theme's are not Windows'. Measured against a stock MonthCalendar at Segoe UI 9:
		// the cell is 31 x 15 where the classic formula gives 29 x 16, and the grid sits five
		// pixels inside the control on every side rather than one. Ask for both rather than
		// baking either into MonthCalendar.
		/// <summary>Whether a progress bar's look changes over time, and the control therefore
		/// has to keep repainting itself.</summary>
		public virtual bool ProgressBarAnimates => false;

		public virtual Size MonthCalendarCellSize (MonthCalendar mc, Size proposed)
		{
			return proposed;
		}

		public virtual int MonthCalendarMargin (MonthCalendar mc)
		{
			return 1;
		}

		public virtual Size Border3DSize {
			get {
				return XplatUI.Border3DSize;
			}
		}

		public virtual Size BorderStaticSize {
			get {
				return new Size(1, 1);
			}
		}

		public virtual Size BorderSize {
			get {
				return XplatUI.BorderSize;
			}
		}

		public virtual Size CaptionButtonSize {
			get {
				return XplatUI.CaptionButtonSize;
			}
		}

		public virtual int CaptionHeight {
			get {
				return XplatUI.CaptionHeight;
			}
		}

		public virtual Size DoubleClickSize {
			get {
				return new Size(4, 4);
			}
		}

		public virtual int DoubleClickTime {
			get {
				return XplatUI.DoubleClickTime;
			}
		}

		public virtual Size FixedFrameBorderSize {
			get {
				return XplatUI.FixedFrameBorderSize;
			}
		}

		public virtual Size FrameBorderSize {
			get {
				return XplatUI.FrameBorderSize;
			}
		}

		public virtual int HorizontalFocusThickness { get { return 1; } }
		
		public virtual int HorizontalScrollBarArrowWidth {
			get {
				return 16;
			}
		}

		public virtual int HorizontalScrollBarHeight {
			get {
				return 16;
			}
		}

		public virtual int HorizontalScrollBarThumbWidth {
			get {
				return 16;
			}
		}

		public virtual Size IconSpacingSize {
			get {
				return new Size(75, 75);
			}
		}

		public virtual bool MenuAccessKeysUnderlined {
			get {
				return XplatUI.MenuAccessKeysUnderlined;
			}
		}
		
		public virtual Size MenuBarButtonSize {
			get { return XplatUI.MenuBarButtonSize; }
		}
		
		public virtual Size MenuButtonSize {
			get {
				return XplatUI.MenuButtonSize;
			}
		}

		public virtual Size MenuCheckSize {
			get {
				return new Size(13, 13);
			}
		}

		public virtual Font MenuFont {
			get {
				return default_font ?? (default_font = SystemFonts.DefaultFont);
			}
		}

		public virtual int MenuHeight {
			get {
				return XplatUI.MenuHeight;
			}
		}

		public virtual int MouseWheelScrollLines {
			get {
				return 3;
			}
		}

		public virtual bool RightAlignedMenus {
			get {
				return false;
			}
		}

		public virtual Size ToolWindowCaptionButtonSize {
			get {
				return XplatUI.ToolWindowCaptionButtonSize;
			}
		}

		public virtual int ToolWindowCaptionHeight {
			get {
				return XplatUI.ToolWindowCaptionHeight;
			}
		}

		public virtual int VerticalFocusThickness { get { return 1; } }

		public virtual int VerticalScrollBarArrowHeight {
			get {
				return 16;
			}
		}

		public virtual int VerticalScrollBarThumbHeight {
			get {
				return 16;
			}
		}

		public virtual int VerticalScrollBarWidth {
			get {
				return 16;
			}
		}

		public abstract Font WindowBorderFont {
			get;
		}

		public int Clamp (int value, int lower, int upper)
		{
			if (value < lower) return lower;
			else if (value > upper) return upper;
			else return value;
		}

		[MonoInternalNote ("Figure out where to point for My Network Places")]
		public virtual string Places(UIIcon index) {
			switch (index) {
				case UIIcon.PlacesRecentDocuments: {
					// Default = "Recent Documents"
					return Environment.GetFolderPath(Environment.SpecialFolder.Recent);
				}

				case UIIcon.PlacesDesktop: {
					// Default = "Desktop"
					return Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
				}

				case UIIcon.PlacesPersonal: {
					// Default = "My Documents"
					return Environment.GetFolderPath(Environment.SpecialFolder.Personal);
				}

				case UIIcon.PlacesMyComputer: {
					// Default = "My Computer"
					return Environment.GetFolderPath(Environment.SpecialFolder.MyComputer);
				}

				case UIIcon.PlacesMyNetwork: {
					// Default = "My Network Places"
					return "/tmp";
				}

				default: {
					throw new ArgumentOutOfRangeException("index", index, "Unsupported place");
				}
			}
		}

		//
		// This routine fetches images embedded as assembly resources (not
		// resgen resources).  It optionally scales the image to fit the
		// specified size x dimension (it adjusts y automatically to fit that).
		//
		private Image GetSizedResourceImage(string name, int width)
		{
			Image image = ResPool.GetUIImage (name, width);
			if (image != null)
				return image;
			
			string	fullname;

			if (width > 0) {
				// Try name_width
				fullname = String.Format("{1}_{0}", name, width);
				image = ResourceImageLoader.Get (fullname);
				if (image != null){
					ResPool.AddUIImage (image, name, width);
					return image;
				}
			}

			// Just try name
			image = ResourceImageLoader.Get (name);
			if (image == null)
				return null;
			
			ResPool.AddUIImage (image, name, 0);
			if (image.Width != width && width != 0){
				Console.Error.WriteLine ("warning: requesting icon that not been tuned {0}_{1} {2}", width, name, image.Width);
				int height = (image.Height * width)/image.Width;
				Bitmap b = new Bitmap (width, height);
				using (Graphics g = Graphics.FromImage (b))
					g.DrawImage (image, 0, 0, width, height);
				ResPool.AddUIImage (b, name, width);

				return b;
			}
			return image;
		}
		
		public virtual Image Images(UIIcon index) {
			return Images(index, 0);
		}
			
		public virtual Image Images(UIIcon index, int size) {
			switch (index) {
				case UIIcon.PlacesRecentDocuments:
					return GetSizedResourceImage ("document-open.png", size);
				case UIIcon.PlacesDesktop:
					return GetSizedResourceImage ("user-desktop.png", size);
				case UIIcon.PlacesPersonal:
					return GetSizedResourceImage ("user-home.png", size);
				case UIIcon.PlacesMyComputer:
					return GetSizedResourceImage ("computer.png", size);
				case UIIcon.PlacesMyNetwork:
					return GetSizedResourceImage ("folder-remote.png", size);

				// Icons for message boxes
				case UIIcon.MessageBoxError:
					return GetSizedResourceImage ("dialog-error.png", size);
				case UIIcon.MessageBoxInfo:
					return GetSizedResourceImage ("dialog-information.png", size);
				case UIIcon.MessageBoxQuestion:
					return GetSizedResourceImage ("dialog-question.png", size);
				case UIIcon.MessageBoxWarning:
					return GetSizedResourceImage ("dialog-warning.png", size);
				
				// misc Icons
				case UIIcon.NormalFolder:
					return GetSizedResourceImage ("folder.png", size);

				default: {
					throw new ArgumentException("Invalid Icon type requested", "index");
				}
			}
		}

		public virtual Image Images(string mimetype, string extension, int size) {
			return null;
		}

		#region Principal Theme Methods
		// To let the theme now that a change of defaults (colors, etc) was detected and force a re-read (and possible recreation of cached resources)
		public abstract void ResetDefaults();

		// If the theme writes directly to a window instead of a device context
		public abstract bool DoubleBufferingSupported {get;}
		#endregion	// Principal Theme Methods

		#region	OwnerDraw Support
		public abstract void DrawOwnerDrawBackground (DrawItemEventArgs e);
		public abstract void DrawOwnerDrawFocusRectangle (DrawItemEventArgs e);
		#endregion	// OwnerDraw Support

		#region Button
		public abstract Size CalculateButtonAutoSize (Button button);
		public abstract void CalculateButtonTextAndImageLayout (Graphics g, ButtonBase b, out Rectangle textRectangle, out Rectangle imageRectangle);
		public abstract void DrawButton (Graphics g, Button b, Rectangle textBounds, Rectangle imageBounds, Rectangle clipRectangle);
		public abstract void DrawFlatButton (Graphics g, ButtonBase b, Rectangle textBounds, Rectangle imageBounds, Rectangle clipRectangle);
		public abstract void DrawPopupButton (Graphics g, Button b, Rectangle textBounds, Rectangle imageBounds, Rectangle clipRectangle);
		#endregion	// Button

		#region ButtonBase
		// Drawing
		public abstract void DrawButtonBase(Graphics dc, Rectangle clip_area, ButtonBase button);

		// Sizing
		public abstract Size ButtonBaseDefaultSize{get;}
		#endregion	// ButtonBase

		#region CheckBox
		public abstract Size CalculateCheckBoxAutoSize (CheckBox checkBox);
		public abstract void CalculateCheckBoxTextAndImageLayout (ButtonBase b, Point offset, out Rectangle glyphArea, out Rectangle textRectangle, out Rectangle imageRectangle);
		public abstract void DrawCheckBox (Graphics g, CheckBox cb, Rectangle glyphArea, Rectangle textBounds, Rectangle imageBounds, Rectangle clipRectangle);
		public abstract void DrawCheckBox (Graphics dc, Rectangle clip_area, CheckBox checkbox);

		#endregion	// CheckBox
		
		#region CheckedListBox
		// Drawing
		public abstract void DrawCheckedListBoxItem (CheckedListBox ctrl, DrawItemEventArgs e);
		#endregion // CheckedListBox
		
		#region ComboBox
		// Drawing
		public abstract void DrawComboBoxItem (ComboBox ctrl, DrawItemEventArgs e);
		public abstract void DrawFlatStyleComboButton (Graphics graphics, Rectangle rectangle, ButtonState state);
		public abstract void ComboBoxDrawNormalDropDownButton (ComboBox comboBox, Graphics g, Rectangle clippingArea, Rectangle area, ButtonState state);
		public abstract bool ComboBoxNormalDropDownButtonHasTransparentBackground (ComboBox comboBox, ButtonState state);
		public abstract bool ComboBoxDropDownButtonHasHotElementStyle (ComboBox comboBox);
		public abstract void ComboBoxDrawBackground (ComboBox comboBox, Graphics g, Rectangle clippingArea, FlatStyle style);
		public abstract bool CombBoxBackgroundHasHotElementStyle (ComboBox comboBox);

		/// <summary>What the editable part of a combo box is filled with. Its own background unless the
		/// theme washes the control when the pointer is over it.</summary>
		public virtual Color ComboBoxFieldBackColor (ComboBox comboBox)
		{
			return comboBox.BackColor;
		}
		#endregion	// ComboBox

		#region Control
		public abstract Font GetLinkFont (Control control);
		#endregion	// Control
		
		#region Datagrid
		public abstract int DataGridPreferredColumnWidth { get; }
		public abstract int DataGridMinimumColumnCheckBoxHeight { get; }
		public abstract int DataGridMinimumColumnCheckBoxWidth { get; }
		
		// Default colours
		public abstract Color DataGridAlternatingBackColor { get; }
		public abstract Color DataGridBackColor { get; }
		public abstract Color DataGridBackgroundColor { get; }
		public abstract Color DataGridCaptionBackColor { get; }
		public abstract Color DataGridCaptionForeColor { get; }
		public abstract Color DataGridGridLineColor { get; }
		public abstract Color DataGridHeaderBackColor { get; }
		public abstract Color DataGridHeaderForeColor { get; }
		public abstract Color DataGridLinkColor { get; }
		public abstract Color DataGridLinkHoverColor { get; }
		public abstract Color DataGridParentRowsBackColor { get; }
		public abstract Color DataGridParentRowsForeColor { get; }
		public abstract Color DataGridSelectionBackColor { get; }
		public abstract Color DataGridSelectionForeColor { get; }
		// Paint		
		public abstract void DataGridPaint (PaintEventArgs pe, DataGrid grid);
		public abstract void DataGridPaintCaption (Graphics g, Rectangle clip, DataGrid grid);
		public abstract void DataGridPaintColumnHeaders (Graphics g, Rectangle clip, DataGrid grid);
		public abstract void DataGridPaintColumnHeader (Graphics g, Rectangle bounds, DataGrid grid, int col);
		public abstract void DataGridPaintRowContents (Graphics g, int row, Rectangle row_rect, bool is_newrow, Rectangle clip, DataGrid grid);
		public abstract void DataGridPaintRowHeader (Graphics g, Rectangle bounds, int row, DataGrid grid);
		public abstract void DataGridPaintRowHeaderArrow (Graphics g, Rectangle bounds, DataGrid grid);
		public abstract void DataGridPaintRowHeaderStar (Graphics g, Rectangle bounds, DataGrid grid);
		public abstract void DataGridPaintParentRows (Graphics g, Rectangle bounds, DataGrid grid);
		public abstract void DataGridPaintParentRow (Graphics g, Rectangle bounds, DataGridDataSource row, DataGrid grid);
		public abstract void DataGridPaintRows (Graphics g, Rectangle cells, Rectangle clip, DataGrid grid);
		public abstract void DataGridPaintRow (Graphics g, int row, Rectangle row_rect, bool is_newrow, Rectangle clip, DataGrid grid);
		public abstract void DataGridPaintRelationRow (Graphics g, int row, Rectangle row_rect, bool is_newrow, Rectangle clip, DataGrid grid);
		
		#endregion // Datagrid

		#region DataGridView
		#region DataGridViewHeaderCell
		#region DataGridViewRowHeaderCell
		public abstract bool DataGridViewRowHeaderCellDrawBackground (DataGridViewRowHeaderCell cell, Graphics g, Rectangle bounds);
		public abstract bool DataGridViewRowHeaderCellDrawSelectionBackground (DataGridViewRowHeaderCell cell);

		/// <summary>What a data grid rules its cells with.</summary>
		public virtual Color DataGridViewGridColor {
			get { return Color.FromKnownColor (KnownColor.ControlDark); }
		}

		/// <summary>The ink for a row header's arrow and its text. The classic theme turns both
		/// white when the row is selected, because it fills the header with the selection colour;
		/// a theme that only washes the header keeps them dark.</summary>
		public virtual Color DataGridViewRowHeaderCellForeColor (DataGridViewRowHeaderCell cell,
									  DataGridViewCellStyle style, bool selected)
		{
			return selected ? style.SelectionForeColor : style.ForeColor;
		}
		public abstract bool DataGridViewRowHeaderCellDrawBorder (DataGridViewRowHeaderCell cell, Graphics g, Rectangle bounds);
		#endregion
		#region DataGridViewColumnHeaderCell
		public abstract bool DataGridViewColumnHeaderCellDrawBackground (DataGridViewColumnHeaderCell cell, Graphics g, Rectangle bounds);
		public abstract bool DataGridViewColumnHeaderCellDrawBorder (DataGridViewColumnHeaderCell cell, Graphics g, Rectangle bounds);
		#endregion
		public abstract bool DataGridViewHeaderCellHasPressedStyle (DataGridView dataGridView);
		public abstract bool DataGridViewHeaderCellHasHotStyle (DataGridView dataGridView);
		#endregion
		#endregion

		#region DateTimePicker
		public abstract void DrawDateTimePicker(Graphics dc, Rectangle clip_rectangle, DateTimePicker dtp);
		public abstract bool DateTimePickerBorderHasHotElementStyle { get; }
		public abstract Rectangle DateTimePickerGetDropDownButtonArea (DateTimePicker dateTimePicker);
		public abstract Rectangle DateTimePickerGetDateArea (DateTimePicker dateTimePicker);
		public abstract bool DateTimePickerDropDownButtonHasHotElementStyle { get; }
		#endregion 	// DateTimePicker

		#region GroupBox
		// Drawing
		public abstract void DrawGroupBox (Graphics dc,  Rectangle clip_area, GroupBox box);

		// Sizing
		public abstract Size GroupBoxDefaultSize{get;}
		#endregion	// GroupBox

		#region HScrollBar
		public abstract Size HScrollBarDefaultSize{get;}	// Default size of the scrollbar
		#endregion	// HScrollBar

		#region ListBox
		// Drawing
		public abstract void DrawListBoxItem (ListBox ctrl, DrawItemEventArgs e);
		#endregion	// ListBox
		
		#region ListView
		// Drawing
		public abstract void DrawListViewItems (Graphics dc, Rectangle clip_rectangle, ListView control);
		public abstract void DrawListViewHeader (Graphics dc, Rectangle clip_rectangle, ListView control);
		public abstract void DrawListViewHeaderDragDetails (Graphics dc, ListView control, ColumnHeader drag_column, int target_x);
		public abstract bool ListViewHasHotHeaderStyle { get; }

		// Sizing
		public abstract int ListViewGetHeaderHeight (ListView listView, Font font);

		/// <summary>Where the date sits inside a picker, measured from the top of the control.
		/// The classic look pins it two pixels below the frame and stays as it was.</summary>
		public virtual int DateTimePickerTextTop (DateTimePicker dateTimePicker, int textHeight)
		{
			return 2;
		}

		/// <summary>How much wider a drop-down menu is than the widest caption in it: the strip
		/// down the left where an icon would go, the column on the right for a shortcut, and the
		/// air around them. The classic figures stay as they were.</summary>
		public virtual int ToolStripDropDownMenuMargin (bool imageMargin)
		{
			return imageMargin ? 68 : 47;
		}

		/// <summary>The hairline a details view leaves below each row. Windows separates them;
		/// the classic theme packs them, and stays as it was.</summary>
		public virtual int ListViewDetailsRowGap {
			get { return 0; }
		}
		public abstract Size ListViewCheckBoxSize { get; }
		public abstract int ListViewColumnHeaderHeight { get; }
		public abstract int ListViewDefaultColumnWidth { get; }
		public abstract int ListViewVerticalSpacing { get; }
		public abstract int ListViewEmptyColumnWidth { get; }
		public abstract int ListViewHorizontalSpacing { get; }
		public abstract Size ListViewDefaultSize { get; }
		public abstract int ListViewGroupHeight { get; }
		public abstract int ListViewItemPaddingWidth { get; }
		public abstract int ListViewTileWidthFactor { get; }
		public abstract int ListViewTileHeightFactor { get; }
		#endregion	// ListView
		
		#region Menus
		public abstract void CalcItemSize (Graphics dc, MenuItem item, int y, int x, bool menuBar);
		public abstract void CalcPopupMenuSize (Graphics dc, Menu menu);
		public abstract int CalcMenuBarSize (Graphics dc, Menu menu, int width);
		public abstract void DrawMenuBar (Graphics dc, Menu menu, Rectangle rect);
		public abstract void DrawMenuItem (MenuItem item, DrawItemEventArgs e);
		public abstract void DrawPopupMenu (Graphics dc, Menu menu, Rectangle cliparea, Rectangle rect);
		#endregion 	// Menus

		#region MonthCalendar
		public abstract void DrawMonthCalendar(Graphics dc, Rectangle clip_rectangle, MonthCalendar month_calendar);
		#endregion 	// MonthCalendar

		#region Panel
		// Sizing
		public abstract Size PanelDefaultSize{get;}
		#endregion	// Panel

		#region PictureBox
		// Drawing
		public abstract void DrawPictureBox (Graphics dc, Rectangle clip, PictureBox pb);

		// Sizing
		public abstract Size PictureBoxDefaultSize{get;}
		#endregion	// PictureBox

		#region PrintPreviewControl
		public abstract int PrintPreviewControlPadding{get;}
		public abstract Size PrintPreviewControlGetPageSize (PrintPreviewControl preview);
		public abstract void PrintPreviewControlPaint (PaintEventArgs pe, PrintPreviewControl preview, Size page_image_size);
		#endregion      // PrintPreviewControl

		#region ProgressBar
		// Drawing
		public abstract void DrawProgressBar (Graphics dc, Rectangle clip_rectangle, ProgressBar progress_bar);

		// Sizing
		public abstract Size ProgressBarDefaultSize{get;}
		#endregion	// ProgressBar

		#region RadioButton
		// Drawing
		public abstract Size CalculateRadioButtonAutoSize (RadioButton rb);
		public abstract void CalculateRadioButtonTextAndImageLayout (ButtonBase b, Point offset, out Rectangle glyphArea, out Rectangle textRectangle, out Rectangle imageRectangle);
		public abstract void DrawRadioButton (Graphics g, RadioButton rb, Rectangle glyphArea, Rectangle textBounds, Rectangle imageBounds, Rectangle clipRectangle);
		public abstract void DrawRadioButton (Graphics dc, Rectangle clip_rectangle, RadioButton radio_button);

		// Sizing
		public abstract Size RadioButtonDefaultSize{get;}
		#endregion	// RadioButton

		#region ScrollBar
		// Drawing
		//public abstract void DrawScrollBar (Graphics dc, Rectangle area, ScrollBar bar, ref Rectangle thumb_pos, ref Rectangle first_arrow_area, ref Rectangle second_arrow_area, ButtonState first_arrow, ButtonState second_arrow, ref int scrollbutton_width, ref int scrollbutton_height, bool vert);
		public abstract void DrawScrollBar (Graphics dc, Rectangle clip_rectangle, ScrollBar bar);

		// Sizing
		/// <summary>Fill the square where a horizontal and a vertical scroll bar meet. Windows puts
		/// a panel there; leaving it unpainted let whatever lay underneath show through, so a cell's
		/// text appeared in the corner of a list that had both bars.</summary>
		public virtual void DrawScrollBarCorner (Graphics dc, Rectangle area)
		{
			if (area.Width > 0 && area.Height > 0)
				dc.FillRectangle (ResPool.GetSolidBrush (ColorControl), area);
		}

		public abstract int ScrollBarButtonSize {get;}		// Size of the scroll button

		public abstract bool ScrollBarHasHotElementStyles { get; }

		public abstract bool ScrollBarHasPressedThumbStyle { get; }

		public abstract bool ScrollBarHasHoverArrowButtonStyle { get; }
		#endregion	// ScrollBar

		#region StatusBar
		// Drawing
		public abstract void DrawStatusBar (Graphics dc, Rectangle clip_rectangle, StatusBar sb);

		// Sizing
		public abstract int StatusBarSizeGripWidth {get;}		// Size of Resize area
		public abstract int StatusBarHorzGapWidth {get;}	// Gap between panels
		public abstract Size StatusBarDefaultSize{get;}
		#endregion	// StatusBar

		#region TabControl
		public abstract Size TabControlDefaultItemSize {get; }
		/// <summary>The hairline along the top of a status strip. The classic theme highlights it in
		/// white; Windows draws a light grey rule there.</summary>
		/// <summary>What the header strip is painted with past the last column.</summary>
		public virtual Color ListViewHeaderStripColor => ColorControl;

		public virtual Color StatusStripTopEdgeColor => Color.White;

		public abstract Point TabControlDefaultPadding {get; }
		public abstract int TabControlMinimumTabWidth {get; }
		public abstract Rectangle TabControlSelectedDelta { get; }
		public abstract int TabControlSelectedSpacing { get; }
		public abstract int TabPanelOffsetX { get; }
		public abstract int TabPanelOffsetY { get; }
		public abstract int TabControlColSpacing { get; }
		public abstract Point TabControlImagePadding { get; }
		public abstract int TabControlScrollerWidth { get; }

		public abstract Rectangle TabControlGetDisplayRectangle (TabControl tab);
		public abstract Rectangle TabControlGetPanelRect (TabControl tab);
		public abstract Size TabControlGetSpacing (TabControl tab);
		public abstract void DrawTabControl (Graphics dc, Rectangle area, TabControl tab);

		/// <summary>The colour a tab page's own background should be, or Color.Empty for "no
		/// opinion", which leaves the page inheriting its TabControl's colour as it always did.
		/// <para>A theme that draws the tab body in a colour of its own has to say so here, because
		/// the PAGE is a real control that paints itself on top of that body afterwards: the body
		/// came out right and was then covered over by a page painted the control face, and the tab
		/// control looked a shade too grey with no code anywhere drawing it that way.</para></summary>
		public virtual Color TabPageBackColor (TabPage page)
		{
			return Color.Empty;
		}
		#endregion

		#region TextBoxBase
		public abstract void TextBoxBaseFillBackground (TextBoxBase textBoxBase, Graphics g, Rectangle clippingArea);
		public abstract bool TextBoxBaseHandleWmNcPaint (TextBoxBase textBoxBase, ref Message m);
		public abstract bool TextBoxBaseShouldPaintBackground (TextBoxBase textBoxBase);
		#endregion

		#region	ToolBar
		// Drawing
		public abstract void DrawToolBar (Graphics dc, Rectangle clip_rectangle, ToolBar control);

		// Sizing
		public abstract int ToolBarGripWidth {get;}		 // Grip width for the ToolBar
		public abstract int ToolBarImageGripWidth {get;}	 // Grip width for the Image on the ToolBarButton
		public abstract int ToolBarSeparatorWidth {get;}	 // width of the separator
		public abstract int ToolBarDropDownWidth { get; }	 // width of the dropdown arrow rect
		public abstract int ToolBarDropDownArrowWidth { get; }	 // width for the dropdown arrow on the ToolBarButton
		public abstract int ToolBarDropDownArrowHeight { get; }	 // height for the dropdown arrow on the ToolBarButton
		public abstract Size ToolBarDefaultSize{get;}

		public abstract bool ToolBarHasHotElementStyles (ToolBar toolBar);
		public abstract bool ToolBarHasHotCheckedElementStyles { get; }
		#endregion	// ToolBar

		#region ToolTip
		public abstract void DrawToolTip(Graphics dc, Rectangle clip_rectangle, ToolTip.ToolTipWindow control);
		public abstract Size ToolTipSize(ToolTip.ToolTipWindow tt, string text);
		public abstract bool ToolTipTransparentBackground { get; }
		#endregion	// ToolTip
		
		#region BalloonWindow
		public abstract void ShowBalloonWindow (IntPtr handle, int timeout, string title, string text, ToolTipIcon icon);
		public abstract void HideBalloonWindow (IntPtr handle);
		public abstract void DrawBalloonWindow (Graphics dc, Rectangle clip, NotifyIcon.BalloonWindow control);
		public abstract Rectangle BalloonWindowRect (NotifyIcon.BalloonWindow control);
		#endregion	// BalloonWindow

		#region TrackBar
		// Drawing
		public abstract void DrawTrackBar (Graphics dc, Rectangle clip_rectangle, TrackBar tb);

		// Sizing
		public abstract Size TrackBarDefaultSize{get; }		// Default size for the TrackBar control
		
		public abstract int TrackBarValueFromMousePosition (int x, int y, TrackBar tb);

		public abstract bool TrackBarHasHotThumbStyle { get; }
		#endregion	// TrackBar

		#region UpDownBase
		public abstract void UpDownBaseDrawButton (Graphics g, Rectangle bounds, bool top, VisualStyles.PushButtonState state);
		public abstract bool UpDownBaseHasHotButtonStyle { get; }
		#endregion

		#region VScrollBar
		public abstract Size VScrollBarDefaultSize{get;}	// Default size of the scrollbar
		#endregion	// VScrollBar

		#region TreeView
		/// <summary>The status strip's resize grip, so a theme can draw the one its Windows draws.
		/// The classic look is six boxes, each a tan square with a white one below and right.
		/// </summary>
		public virtual void StatusStripSizingGrip (Graphics g, Rectangle rect)
		{
			for (int i = 0; i < 6; i++) {
				int x = rect.Right - (i == 3 ? 13 : i == 1 || i == 5 ? 9 : 5);
				int y = rect.Bottom - (i == 4 ? 13 : i == 2 || i == 5 ? 9 : 5);
				g.DrawRectangle (Pens.White, x + 1, y + 1, 1, 1);
				g.DrawRectangle (ResPool.GetPen (Color.FromArgb (172, 168, 153)), x, y, 1, 1);
			}
		}

		/// <summary>The colour of a tree's connector lines, or Empty to derive one from the
		/// background. Windows draws them in a fixed grey of its own.</summary>
		public virtual Color TreeViewLineColor (TreeView tv) => Color.Empty;

		public abstract Size TreeViewDefaultSize { get; }
		public abstract void TreeViewDrawNodePlusMinus (TreeView treeView, TreeNode node, Graphics dc, int x, int middle);
		#endregion

		#region Managed window
		public abstract void DrawManagedWindowDecorations (Graphics dc, Rectangle clip, InternalWindowManager wm);
		public abstract int ManagedWindowTitleBarHeight (InternalWindowManager wm);
		public abstract int ManagedWindowBorderWidth (InternalWindowManager wm);
		public abstract int ManagedWindowIconWidth (InternalWindowManager wm);
		public abstract Size ManagedWindowButtonSize (InternalWindowManager wm);
		public abstract void ManagedWindowSetButtonLocations (InternalWindowManager wm);
		public abstract Rectangle ManagedWindowGetTitleBarIconArea (InternalWindowManager wm);
		public abstract Size ManagedWindowGetMenuButtonSize (InternalWindowManager wm);
		public abstract bool ManagedWindowTitleButtonHasHotElementStyle (TitleButton button, Form form);
		public abstract void ManagedWindowDrawMenuButton (Graphics dc, TitleButton button, Rectangle clip, InternalWindowManager wm);
		public abstract void ManagedWindowOnSizeInitializedOrChanged (Form form);
		public const int ManagedWindowSpacingAfterLastTitleButton = 2;
		#endregion

		#region	ControlPaint Methods
		public abstract void CPDrawBorder (Graphics graphics, Rectangle bounds, Color leftColor, int leftWidth,
			ButtonBorderStyle leftStyle, Color topColor, int topWidth, ButtonBorderStyle topStyle,
			Color rightColor, int rightWidth, ButtonBorderStyle rightStyle, Color bottomColor,
			int bottomWidth, ButtonBorderStyle bottomStyle);

		public abstract void CPDrawBorder (Graphics graphics, RectangleF bounds, Color leftColor, int leftWidth,
			ButtonBorderStyle leftStyle, Color topColor, int topWidth, ButtonBorderStyle topStyle,
			Color rightColor, int rightWidth, ButtonBorderStyle rightStyle, Color bottomColor,
			int bottomWidth, ButtonBorderStyle bottomStyle);

		public abstract void CPDrawBorder3D (Graphics graphics, Rectangle rectangle, Border3DStyle style, Border3DSide sides);
		public abstract void CPDrawBorder3D (Graphics graphics, Rectangle rectangle, Border3DStyle style, Border3DSide sides, Color control_color);
		public abstract void CPDrawButton (Graphics graphics, Rectangle rectangle, ButtonState state);
		public abstract void CPDrawCaptionButton (Graphics graphics, Rectangle rectangle, CaptionButton button, ButtonState state);
		public abstract void CPDrawCheckBox (Graphics graphics, Rectangle rectangle, ButtonState state);
		public abstract void CPDrawComboButton (Graphics graphics, Rectangle rectangle, ButtonState state);
		public abstract void CPDrawContainerGrabHandle (Graphics graphics, Rectangle bounds);
		public abstract void CPDrawFocusRectangle (Graphics graphics, Rectangle rectangle, Color foreColor, Color backColor);
		public abstract void CPDrawGrabHandle (Graphics graphics, Rectangle rectangle, bool primary, bool enabled);
		public abstract void CPDrawGrid (Graphics graphics, Rectangle area, Size pixelsBetweenDots, Color backColor);
		public abstract void CPDrawImageDisabled (Graphics graphics, Image image, int x, int y, Color background);
		public abstract void CPDrawLockedFrame (Graphics graphics, Rectangle rectangle, bool primary);
		public abstract void CPDrawMenuGlyph (Graphics graphics, Rectangle rectangle, MenuGlyph glyph, Color color, Color backColor);
		public abstract void CPDrawMixedCheckBox (Graphics graphics, Rectangle rectangle, ButtonState state);
		public abstract void CPDrawRadioButton (Graphics graphics, Rectangle rectangle, ButtonState state);
		public abstract void CPDrawReversibleFrame (Rectangle rectangle, Color backColor, FrameStyle style);
		public abstract void CPDrawReversibleLine (Point start, Point end, Color backColor);
		public abstract void CPDrawScrollButton (Graphics graphics, Rectangle rectangle, ScrollButton button, ButtonState state);
		public abstract void CPDrawSelectionFrame (Graphics graphics, bool active, Rectangle outsideRect, Rectangle insideRect,
			Color backColor);
		public abstract void CPDrawSizeGrip (Graphics graphics, Color backColor, Rectangle bounds);
		public abstract void CPDrawStringDisabled (Graphics graphics, string s, Font font, Color color, RectangleF layoutRectangle,
			StringFormat format);
		public abstract void CPDrawStringDisabled (IDeviceContext dc, string s, Font font, Color color, Rectangle layoutRectangle, TextFormatFlags format);
		public abstract void CPDrawVisualStyleBorder (Graphics graphics, Rectangle bounds);
		/// <summary>Draw a control's window border -- the frame Windows paints in the non-client
		/// area for WS_EX_CLIENTEDGE and WS_BORDER. This driver does not model a non-client area
		/// (a window and its client are the same rectangle), so the border is painted over the
		/// window's own outer edge once the control has finished drawing; without it a TextBox, a
		/// ListBox and a TreeView came up with no border at all.</summary>
		/// <param name="control">The control the window belongs to, for its focused / hovered /
		/// enabled state; null when it cannot be resolved.</param>
		/// <param name="sunken">WS_EX_CLIENTEDGE -- an input field. False is a plain WS_BORDER.</param>
		public virtual void DrawControlBorder (Graphics dc, Rectangle bounds, Control control, bool sunken)
		{
			if (sunken)
				CPDrawBorder3D (dc, bounds, Border3DStyle.Sunken, Border3DSide.All);
			else
				dc.DrawRectangle (ResPool.GetPen (ColorControlDark),
						  bounds.X, bounds.Y, bounds.Width - 1, bounds.Height - 1);
		}

		public abstract void CPDrawBorderStyle (Graphics dc, Rectangle area, BorderStyle border_style);
		#endregion	// ControlPaint Methods
	}
}
