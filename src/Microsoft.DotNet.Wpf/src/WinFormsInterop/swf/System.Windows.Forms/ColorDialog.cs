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
// Copyright (c) 2006 Alexander Olk
//
// Authors:
//	Alexander Olk	alex.olk@googlemail.com
//

// The colour dialog. Where the platform has a colour chooser of its own it is that one (see
// PlatformBridge: ChooseColor on Windows, NSColorPanel on macOS, UIColorPickerViewController on
// iOS and <input type=color> in a browser, the last two through the WPF host); the managed dialog
// here is for the heads that have none (Linux, Android, headless).
//
// The managed dialog is laid out and draws as Windows' own: comdlg32's CHOOSECOLOR template as
// Windows 11 shows it, read off the running dialog (control ids, rectangles at 96 DPI, styles, and
// its pixels) and reproduced control for control, in the dialog font (MS Shell Dlg 8, which is
// Microsoft Sans Serif 8.25):
//   "&Basic colors:"    6,7   210x15    box 720   6,23  210x140  8x6 swatches
//   "&Custom colors:"   6,172 210x15    box 721   6,189 210x46   8x2 swatches
//   "&Define Custom Colors >>" 6,244 207x23   OK 6,270 66x23   Cancel 78,270   &Help 150,270 (CC_SHOWHELP)
//   rainbow 710  228,7 177x189 (static edge)    luminance 702  420,7 12x189, its arrow right of it
//   sample 709   228,202 60x42  "Color" 228,245 30x15 (right)  "|S&olid" 258,245 30x15
//   "Hu&e:" 291,205 30x15 edit 324,202 27x20   "&Sat:" 291,228 / 324,224   "&Lum:" 291,250 / 324,247
//   "&Red:" 365,205 36x15 edit 404,202 27x20   "&Green:" 365,228 / 404,224 "Bl&ue:" 365,250 / 404,247
//   "&Add to Custom Colors" 228,270 213x23
// in a 222x299 client, 447x299 fully open. A swatch is a sunken 19x17 edge (DrawEdge EDGE_SUNKEN)
// at a 26x22 pitch from the box's (4,3), the selected one ringed in black one pixel out and the
// focused one in a focus rectangle three out. The rainbow is comdlg32's: hue across in steps of 4,
// saturation down in steps of 8, at luminance 120, each block from (extent * step / 240); the
// luminance bar is the current hue and saturation in luminance steps of 8. The colour arithmetic is
// comdlg32's integer HLS (RGBtoHLS / HLStoRGB, HLSMAX 240), so the six numbers read as Windows'.

using System.ComponentModel;
using System.Drawing;
using System;

namespace System.Windows.Forms {
	[DefaultProperty ("Color")]
	public class ColorDialog : CommonDialog {
		#region Local Variables
		private int options;
		private Color color = Color.Black;
		private readonly int [] customColors = new int [16];

		private StaticText basicLabel, customLabel, colorLabel, solidLabel;
		private StaticText hueLabel, satLabel, lumLabel, redLabel, greenLabel, blueLabel;
		private SwatchGrid basicGrid, customGrid;
		private Button defineButton, okButton, cancelButton, helpButton, addButton;
		private RainbowBox rainbow;
		private LumScroll lumScroll, lumArrow;
		private SampleBox sample;
		private NumberBox hueBox, satBox, lumBox, redBox, greenBox, blueBox;

		// The colour being edited, as comdlg32 keeps it: RGB and its HLS (0..240) side by side.
		internal Color current = Color.Black;
		internal int hue, sat, lum;
		private bool updating;

		// Where "Add to Custom Colors" puts the next colour.
		private int customTarget;

		private const int RANGE = 240;
		private const int ClientHeight = 299, BasicWidth = 222, FullWidth = 447;

		/// <summary>Windows' 48 basic colours, row by row, as COLORREF-free RGB.</summary>
		internal static readonly int [] BasicColors = {
			0xFF8080, 0xFFFF80, 0x80FF80, 0x00FF80, 0x80FFFF, 0x0080FF, 0xFF80C0, 0xFF80FF,
			0xFF0000, 0xFFFF00, 0x80FF00, 0x00FF40, 0x00FFFF, 0x0080C0, 0x8080C0, 0xFF00FF,
			0x804040, 0xFF8040, 0x00FF00, 0x008080, 0x004080, 0x8080FF, 0x800040, 0xFF0080,
			0x800000, 0xFF8000, 0x008000, 0x008040, 0x0000FF, 0x0000A0, 0x800080, 0x8000FF,
			0x400000, 0x804000, 0x004000, 0x004040, 0x000080, 0x000040, 0x400040, 0x400080,
			0x000000, 0x808000, 0x808040, 0x808080, 0x408080, 0xC0C0C0, 0x400040, 0xFFFFFF,
		};
		#endregion	// Local Variables

		#region Public Constructors
		public ColorDialog () : base()
		{
			form = new DialogForm (this);
			form.SuspendLayout ();

			form.Text = "Color";
			form.FormBorderStyle = FormBorderStyle.FixedDialog;
			form.MinimizeBox = false;
			form.MaximizeBox = false;
			form.ShowIcon = false;
			form.ShowInTaskbar = false;
			form.StartPosition = FormStartPosition.CenterScreen;
			form.AutoScaleMode = AutoScaleMode.None;
			form.Font = DialogFont;
			form.ClientSize = new Size (BasicWidth, ClientHeight);

			basicLabel = new StaticText ("&Basic colors:", 6, 7, 210, 15);
			basicGrid = new SwatchGrid (this, 8, 6) { Location = new Point (6, 23), Size = new Size (210, 140) };
			for (int i = 0; i < BasicColors.Length; i++)
				basicGrid.Colors [i] = Color.FromArgb ((BasicColors [i] >> 16) & 0xFF, (BasicColors [i] >> 8) & 0xFF, BasicColors [i] & 0xFF);
			customLabel = new StaticText ("&Custom colors:", 6, 172, 210, 15);
			customGrid = new SwatchGrid (this, 8, 2) { Location = new Point (6, 189), Size = new Size (210, 46) };

			defineButton = new Button { Text = "&Define Custom Colors >>", Location = new Point (6, 244), Size = new Size (207, 23) };
			okButton = new Button { Text = "OK", Location = new Point (6, 270), Size = new Size (66, 23), DialogResult = DialogResult.OK };
			cancelButton = new Button { Text = "Cancel", Location = new Point (78, 270), Size = new Size (66, 23), DialogResult = DialogResult.Cancel };
			helpButton = new Button { Text = "&Help", Location = new Point (150, 270), Size = new Size (66, 23) };

			rainbow = new RainbowBox (this) { Location = new Point (228, 7), Size = new Size (177, 189) };
			// The bar, and the arrow to the right of it in a strip of its own: the arrow reaches past
			// the bar's ends by its half height, and a control spanning both would cover the red box.
			lumScroll = new LumScroll (this, false) { Location = new Point (420, 7), Size = new Size (12, 189) };
			lumArrow = new LumScroll (this, true) { Location = new Point (432, 0), Size = new Size (15, 210) };
			sample = new SampleBox (this) { Location = new Point (228, 202), Size = new Size (60, 42) };
			colorLabel = new StaticText ("Color", 228, 245, 30, 15) { TextAlign = ContentAlignment.TopRight };
			solidLabel = new StaticText ("|S&olid", 258, 245, 30, 15);

			hueLabel = new StaticText ("Hu&e:", 291, 205, 30, 15) { TextAlign = ContentAlignment.TopRight };
			hueBox = new NumberBox (this, 239) { Location = new Point (324, 202) };
			satLabel = new StaticText ("&Sat:", 291, 228, 30, 15) { TextAlign = ContentAlignment.TopRight };
			satBox = new NumberBox (this, RANGE) { Location = new Point (324, 224) };
			lumLabel = new StaticText ("&Lum:", 291, 250, 30, 15) { TextAlign = ContentAlignment.TopRight };
			lumBox = new NumberBox (this, RANGE) { Location = new Point (324, 247) };
			redLabel = new StaticText ("&Red:", 365, 205, 36, 15) { TextAlign = ContentAlignment.TopRight };
			redBox = new NumberBox (this, 255) { Location = new Point (404, 202) };
			greenLabel = new StaticText ("&Green:", 365, 228, 36, 15) { TextAlign = ContentAlignment.TopRight };
			greenBox = new NumberBox (this, 255) { Location = new Point (404, 224) };
			blueLabel = new StaticText ("Bl&ue:", 365, 250, 36, 15) { TextAlign = ContentAlignment.TopRight };
			blueBox = new NumberBox (this, 255) { Location = new Point (404, 247) };
			addButton = new Button { Text = "&Add to Custom Colors", Location = new Point (228, 270), Size = new Size (213, 23) };

			// Windows' tab order, which is also its z-order.
			Control [] order = { basicLabel, basicGrid, customLabel, customGrid, defineButton, okButton, cancelButton,
				helpButton, rainbow, lumScroll, lumArrow, sample, colorLabel, solidLabel, hueLabel, hueBox, satLabel, satBox,
				lumLabel, lumBox, redLabel, redBox, greenLabel, greenBox, blueLabel, blueBox, addButton };
			for (int i = 0; i < order.Length; i++)
				order [i].TabIndex = i;
			form.Controls.AddRange (order);

			form.AcceptButton = okButton;
			form.CancelButton = cancelButton;
			helpButton.Hide ();

			form.ResumeLayout (false);

			defineButton.Click += (s, e) => OpenFull ();
			addButton.Click += (s, e) => AddToCustomColors ();
			helpButton.Click += (s, e) => OnHelpRequest (e);
			form.FormClosed += (s, e) => {
				if (form.DialogResult == DialogResult.OK)
					BuildResult ();
			};

			Reset ();
		}
		#endregion	// Public Constructors

		/// <summary>The dialog font: MS Shell Dlg at 8 point, which Windows maps to Microsoft Sans
		/// Serif, as FontDialog asks for it.</summary>
		private static Font DialogFont => new Font ("Microsoft Sans Serif", 8.25f);

		#region Public Instance Properties
		public Color Color {
			get { return color; }
			set { color = value.IsEmpty ? Color.Black : value; }
		}

		[DefaultValue(true)]
		public virtual bool AllowFullOpen {
			get { return !GetOption (ColorDialogRequest.CC_PREVENTFULLOPEN); }
			set { SetOption (ColorDialogRequest.CC_PREVENTFULLOPEN, !value); }
		}

		/// <summary>Has no effect on a display with more than 256 colours, as in .NET.</summary>
		[DefaultValue(false)]
		public virtual bool AnyColor {
			get { return GetOption (ColorDialogRequest.CC_ANYCOLOR); }
			set { SetOption (ColorDialogRequest.CC_ANYCOLOR, value); }
		}

		[DefaultValue(false)]
		public virtual bool FullOpen {
			get { return GetOption (ColorDialogRequest.CC_FULLOPEN); }
			set { SetOption (ColorDialogRequest.CC_FULLOPEN, value); }
		}

		/// <summary>The sixteen custom colours, as COLORREFs (0x00BBGGRR), as .NET keeps them: a copy
		/// comes out, and what goes in is copied, white past its end.</summary>
		[Browsable(false)]
		[DesignerSerializationVisibility (DesignerSerializationVisibility.Hidden)]
		public int[] CustomColors {
			get { return (int []) customColors.Clone (); }
			set {
				int length = value == null ? 0 : Math.Min (value.Length, 16);
				if (length > 0)
					Array.Copy (value, 0, customColors, 0, length);
				for (int i = length; i < 16; i++)
					customColors [i] = 0x00FFFFFF;
			}
		}

		[DefaultValue(false)]
		public virtual bool ShowHelp {
			get { return GetOption (ColorDialogRequest.CC_SHOWHELP); }
			set { SetOption (ColorDialogRequest.CC_SHOWHELP, value); }
		}

		[DefaultValue(false)]
		public virtual bool SolidColorOnly {
			get { return GetOption (ColorDialogRequest.CC_SOLIDCOLOR); }
			set { SetOption (ColorDialogRequest.CC_SOLIDCOLOR, value); }
		}
		#endregion	// Public Instance Properties

		#region Public Instance Methods
		public override void Reset ()
		{
			options = 0;
			color = Color.Black;
			CustomColors = null;
		}

		public override string ToString ()
		{
			return base.ToString () + ",  Color: " + Color.ToString ();
		}

		/// <summary>The platform's colour chooser, not waited for: see CommonDialog.ShowDialogAsync.
		/// iOS and the browser can only answer this way.</summary>
		public override async System.Threading.Tasks.Task<DialogResult> ShowDialogAsync ()
		{
			IColorDialogBridge bridge = PlatformBridge;
			if (bridge != null) {
				ColorDialogRequest request = PlatformRequest ();
				bool? answer = await bridge.ShowAsync (request);
				if (answer.HasValue) {
					if (answer.Value)
						AcceptPlatformAnswer (request);
					return answer.Value ? DialogResult.OK : DialogResult.Cancel;
				}
			}
			skipBridge = true;
			try {
				return await base.ShowDialogAsync ();
			} finally {
				skipBridge = false;
			}
		}

		public override System.Threading.Tasks.Task<DialogResult> ShowDialogAsync (IWin32Window owner)
			=> ShowDialogAsync ();
		#endregion	// Public Instance Methods

		#region Protected Instance Properties
		/// <summary>ChooseColor's hInstance: the module, as .NET passes it. Windows reads it only
		/// for a dialog template.</summary>
		protected virtual IntPtr Instance {
			get { return IntPtr.Zero; }
		}

		/// <summary>The CC_* flags the properties stand for, as .NET's ColorDialog keeps them.</summary>
		protected virtual int Options {
			get { return options; }
		}
		#endregion	// Protected Instance Properties

		#region Platform dialog
		/// <summary>The platform's colour dialog where there is one, or null for none.</summary>
		internal static IColorDialogBridge PlatformBridge {
			get => s_bridge ?? Win32ColorDialogBridge.Default ?? MacColorDialogBridge.Default;
			set => s_bridge = value;
		}

		private static IColorDialogBridge s_bridge;
		private bool skipBridge;

		/// <summary>What the platform's dialog is asked: ChooseColor's terms, from the properties.</summary>
		internal ColorDialogRequest PlatformRequest ()
		{
			int flags = Options | ColorDialogRequest.CC_RGBINIT;
			if (!AllowFullOpen)
				flags &= ~ColorDialogRequest.CC_FULLOPEN;
			return new ColorDialogRequest {
				Color = color,
				CustomColors = (int []) customColors.Clone (),
				Flags = flags,
				Instance = Instance,
				Dialog = this,
			};
		}

		internal void AcceptPlatformAnswer (ColorDialogRequest request)
		{
			// .NET keeps the colour it was given (a named one stays named) when the dialog hands
			// the same value back.
			if (ColorDialogRequest.ToColorRef (request.Color) != ColorDialogRequest.ToColorRef (color) || request.Color.A != 255)
				color = ColorDialogRequest.FromColorRef (ColorDialogRequest.ToColorRef (request.Color));
			for (int i = 0; i < 16 && i < request.CustomColors.Length; i++)
				customColors [i] = request.CustomColors [i];
		}
		#endregion

		#region Protected Instance Methods
		protected override bool RunDialog (IntPtr hwndOwner)
		{
			// The platform's own dialog wherever there is one; the managed form below is for when
			// there is none.
			IColorDialogBridge bridge = skipBridge ? null : PlatformBridge;
			if (bridge != null) {
				ColorDialogRequest request = PlatformRequest ();
				bool? answer = bridge.Show (request);
				if (answer.HasValue) {
					ranOnPlatform = true;
					if (answer.Value)
						AcceptPlatformAnswer (request);
					return answer.Value;
				}
			}

			PrepareForm ();
			return true;
		}
		#endregion	// Protected Instance Methods

		#region The managed dialog
		/// <summary>Lays the managed dialog out for the properties and starts it on Color, as
		/// ChooseColor's WM_INITDIALOG does.</summary>
		internal void PrepareForm ()
		{
			bool full = FullOpen && AllowFullOpen;
			form.ClientSize = new Size (full ? FullWidth : BasicWidth, ClientHeight);
			defineButton.Enabled = AllowFullOpen && !full;
			helpButton.Visible = ShowHelp;
			SetFullControlsVisible (full);

			for (int i = 0; i < 16; i++)
				customGrid.Colors [i] = ColorDialogRequest.FromColorRef (customColors [i]);
			customTarget = 0;

			// The starting colour's box is selected: a basic one, else a custom one, else the first
			// basic box (which Windows rings although the colour is not in it).
			Color start = ColorDialogRequest.FromColorRef (ColorDialogRequest.ToColorRef (color));
			int b = basicGrid.IndexOf (start);
			int c = b < 0 ? customGrid.IndexOf (start) : -1;
			basicGrid.Selected = b >= 0 ? b : (c >= 0 ? -1 : 0);
			customGrid.Selected = c;
			if (c >= 0)
				customTarget = c;
			SetFromRgb (start, null);
			form.ActiveControl = c >= 0 ? customGrid : basicGrid;
		}

		private void SetFullControlsVisible (bool visible)
		{
			foreach (Control c in new Control [] { rainbow, lumScroll, lumArrow, sample, colorLabel, solidLabel, hueLabel, hueBox,
				satLabel, satBox, lumLabel, lumBox, redLabel, redBox, greenLabel, greenBox, blueLabel, blueBox, addButton })
				c.Visible = visible;
		}

		private void OpenFull ()
		{
			if (!AllowFullOpen)
				return;
			form.ClientSize = new Size (FullWidth, ClientHeight);
			SetFullControlsVisible (true);
			defineButton.Enabled = false;
			hueBox.Focus ();
		}

		private void AddToCustomColors ()
		{
			customGrid.Colors [customTarget] = current;
			customGrid.Invalidate ();
			customTarget = (customTarget + 1) % 16;
		}

		private void BuildResult ()
		{
			Color chosen = Color.FromArgb (current.R, current.G, current.B);
			if (ColorDialogRequest.ToColorRef (chosen) != ColorDialogRequest.ToColorRef (color) || color.A != 255)
				color = chosen;
			for (int i = 0; i < 16; i++)
				customColors [i] = ColorDialogRequest.ToColorRef (customGrid.Colors [i]);
		}

		/// <summary>A swatch was chosen in one of the boxes: it becomes the colour, and only one box
		/// shows a selection.</summary>
		internal void OnSwatchChosen (SwatchGrid grid, int index)
		{
			if (grid == basicGrid) {
				customGrid.Selected = -1;
			} else {
				basicGrid.Selected = -1;
				customTarget = index;
			}
			SetFromRgb (grid.Colors [index], null);
		}

		/// <summary>A new colour from its RGB (a swatch, the red/green/blue boxes): HLS follows.</summary>
		internal void SetFromRgb (Color rgb, NumberBox source)
		{
			current = Color.FromArgb (rgb.R, rgb.G, rgb.B);
			RgbToHls (current, out hue, out lum, out sat);
			Refresh (source);
		}

		/// <summary>A new colour from its HLS (the rainbow, the luminance bar, the hue/sat/lum boxes).</summary>
		internal void SetFromHls (int h, int l, int s, NumberBox source)
		{
			hue = Math.Clamp (h, 0, RANGE - 1);
			lum = Math.Clamp (l, 0, RANGE);
			sat = Math.Clamp (s, 0, RANGE);
			current = HlsToRgb (hue, lum, sat);
			Refresh (source);
		}

		private void Refresh (NumberBox source)
		{
			if (updating)
				return;
			updating = true;
			try {
				if (source != hueBox) hueBox.Value = hue;
				if (source != satBox) satBox.Value = sat;
				if (source != lumBox) lumBox.Value = lum;
				if (source != redBox) redBox.Value = current.R;
				if (source != greenBox) greenBox.Value = current.G;
				if (source != blueBox) blueBox.Value = current.B;
			} finally {
				updating = false;
			}
			rainbow.Invalidate ();
			lumScroll.Invalidate ();
			lumArrow.Invalidate ();
			sample.Invalidate ();
		}

		internal void OnNumberTyped (NumberBox box, int value)
		{
			if (updating)
				return;
			if (box == hueBox || box == satBox || box == lumBox)
				SetFromHls (hueBox.Value, lumBox.Value, satBox.Value, box);
			else
				SetFromRgb (Color.FromArgb (redBox.Value, greenBox.Value, blueBox.Value), box);
		}

		internal void Accept ()
		{
			form.DialogResult = DialogResult.OK;
		}
		#endregion

		#region comdlg32's HLS arithmetic
		/// <summary>comdlg32's RGBtoHLS: integer HLS on 0..240, an achromatic colour's hue 160.</summary>
		internal static void RgbToHls (Color c, out int h, out int l, out int s)
		{
			const int HLSMAX = RANGE, RGBMAX = 255, UNDEFINED = HLSMAX * 2 / 3;
			int r = c.R, g = c.G, b = c.B;
			int cMax = Math.Max (Math.Max (r, g), b), cMin = Math.Min (Math.Min (r, g), b);
			l = ((cMax + cMin) * HLSMAX + RGBMAX) / (2 * RGBMAX);
			if (cMax == cMin) {
				s = 0;
				h = UNDEFINED;
				return;
			}
			if (l <= HLSMAX / 2)
				s = ((cMax - cMin) * HLSMAX + (cMax + cMin) / 2) / (cMax + cMin);
			else
				s = ((cMax - cMin) * HLSMAX + (2 * RGBMAX - cMax - cMin) / 2) / (2 * RGBMAX - cMax - cMin);
			int rd = ((cMax - r) * (HLSMAX / 6) + (cMax - cMin) / 2) / (cMax - cMin);
			int gd = ((cMax - g) * (HLSMAX / 6) + (cMax - cMin) / 2) / (cMax - cMin);
			int bd = ((cMax - b) * (HLSMAX / 6) + (cMax - cMin) / 2) / (cMax - cMin);
			if (r == cMax)
				h = bd - gd;
			else if (g == cMax)
				h = HLSMAX / 3 + rd - bd;
			else
				h = 2 * HLSMAX / 3 + gd - rd;
			if (h < 0)
				h += HLSMAX;
			if (h > HLSMAX)
				h -= HLSMAX;
		}

		private static int HueToRgb (int n1, int n2, int h)
		{
			const int HLSMAX = RANGE;
			if (h < 0) h += HLSMAX;
			if (h > HLSMAX) h -= HLSMAX;
			if (h < HLSMAX / 6)
				return n1 + ((n2 - n1) * h + HLSMAX / 12) / (HLSMAX / 6);
			if (h < HLSMAX / 2)
				return n2;
			if (h < HLSMAX * 2 / 3)
				return n1 + ((n2 - n1) * (HLSMAX * 2 / 3 - h) + HLSMAX / 12) / (HLSMAX / 6);
			return n1;
		}

		/// <summary>comdlg32's HLStoRGB.</summary>
		internal static Color HlsToRgb (int h, int l, int s)
		{
			const int HLSMAX = RANGE, RGBMAX = 255;
			int r, g, b;
			if (s == 0) {
				r = g = b = l * RGBMAX / HLSMAX;
			} else {
				int m2 = l <= HLSMAX / 2 ? (l * (HLSMAX + s) + HLSMAX / 2) / HLSMAX : l + s - (l * s + HLSMAX / 2) / HLSMAX;
				int m1 = 2 * l - m2;
				r = (HueToRgb (m1, m2, h + HLSMAX / 3) * RGBMAX + HLSMAX / 2) / HLSMAX;
				g = (HueToRgb (m1, m2, h) * RGBMAX + HLSMAX / 2) / HLSMAX;
				b = (HueToRgb (m1, m2, h - HLSMAX / 3) * RGBMAX + HLSMAX / 2) / HLSMAX;
			}
			return Color.FromArgb (Math.Clamp (r, 0, 255), Math.Clamp (g, 0, 255), Math.Clamp (b, 0, 255));
		}
		#endregion

		#region Options
		private bool GetOption (int option) => (options & option) != 0;

		private void SetOption (int option, bool value)
		{
			if (value)
				options |= option;
			else
				options &= ~option;
		}
		#endregion

		#region Controls
		/// <summary>An SS_LEFT / SS_RIGHT static: DrawText at the control's edge with no padding.</summary>
		internal sealed class StaticText : Label
		{
			public StaticText (string text, int x, int y, int w, int h)
			{
				Text = text;
				Location = new Point (x, y);
				Size = new Size (w, h);
				AutoSize = false;
				TabStop = false;
			}

			protected override void OnPaint (PaintEventArgs e)
			{
				TextFormatFlags flags = TextFormatFlags.Top | TextFormatFlags.WordBreak | TextFormatFlags.ExpandTabs
					| TextFormatFlags.NoPadding
					| (TextAlign == ContentAlignment.TopRight ? TextFormatFlags.Right : TextFormatFlags.Left);
				if (!ShowKeyboardCues)
					flags |= TextFormatFlags.HidePrefix;
				TextRenderer.DrawText (e.Graphics, Text, Font, ClientRectangle, Enabled ? ForeColor : SystemColors.GrayText, flags);
			}

			// A mnemonic moves the focus to the next control in tab order, as a dialog static does.
			protected override bool ProcessMnemonic (char charCode)
			{
				if (!IsMnemonic (charCode, Text) || Parent == null)
					return false;
				Parent.SelectNextControl (this, true, false, true, false);
				return true;
			}
		}

		/// <summary>A box of swatches (comdlg32's COLOR_BOX1 / COLOR_CUSTOM1): sunken 19x17 swatches
		/// at a 26x22 pitch from (4,3), arrows to move, a click or Space to choose, a double click to
		/// choose and close.</summary>
		internal sealed class SwatchGrid : Control
		{
			private readonly ColorDialog owner;
			internal readonly int Columns, Rows;
			internal readonly Color [] Colors;
			private int selected = -1;

			internal const int PitchX = 26, PitchY = 22, OriginX = 4, OriginY = 3, SwatchWidth = 19, SwatchHeight = 17;

			public SwatchGrid (ColorDialog owner, int columns, int rows)
			{
				this.owner = owner;
				Columns = columns;
				Rows = rows;
				Colors = new Color [columns * rows];
				for (int i = 0; i < Colors.Length; i++)
					Colors [i] = Color.White;
				SetStyle (ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint
					| ControlStyles.Selectable, true);
				TabStop = true;
			}

			public int Selected {
				get { return selected; }
				set { selected = value; Invalidate (); }
			}

			public int IndexOf (Color c)
			{
				for (int i = 0; i < Colors.Length; i++)
					if (Colors [i].R == c.R && Colors [i].G == c.G && Colors [i].B == c.B)
						return i;
				return -1;
			}

			internal static Rectangle SwatchRect (int index, int columns)
				=> new Rectangle (OriginX + (index % columns) * PitchX, OriginY + (index / columns) * PitchY, SwatchWidth, SwatchHeight);

			private int HitTest (Point p)
			{
				for (int i = 0; i < Colors.Length; i++) {
					Rectangle r = SwatchRect (i, Columns);
					r.Inflate (3, 2);
					if (r.Contains (p))
						return i;
				}
				return -1;
			}

			protected override void OnPaint (PaintEventArgs e)
			{
				Graphics g = e.Graphics;
				using (var back = new SolidBrush (BackColor))
					g.FillRectangle (back, ClientRectangle);
				for (int i = 0; i < Colors.Length; i++) {
					Rectangle r = SwatchRect (i, Columns);
					DrawSunken (g, r);
					using (var fill = new SolidBrush (Colors [i]))
						g.FillRectangle (fill, r.X + 2, r.Y + 2, r.Width - 4, r.Height - 4);
					if (i == selected) {
						Rectangle ring = r;
						ring.Inflate (1, 1);
						Frame (g, ring, Color.Black, Color.Black);
					}
				}
				int focus = selected >= 0 ? selected : 0;
				if (Focused && ShowFocusCues) {
					Rectangle f = SwatchRect (focus, Columns);
					f.Inflate (3, 3);
					ControlPaint.DrawFocusRectangle (g, f);
				}
			}

			/// <summary>DrawEdge(EDGE_SUNKEN): shadow and dark shadow top-left, highlight and light
			/// bottom-right.</summary>
			internal static void DrawSunken (Graphics g, Rectangle r)
			{
				// Whole-pixel fills, not pen lines: a one-pixel pen is centred on the pixel edge and
				// blends into its neighbours.
				Frame (g, r, SystemColors.ControlDark, SystemColors.ControlLightLight);
				r.Inflate (-1, -1);
				Frame (g, r, SystemColors.ControlDarkDark, SystemColors.ControlLight);
			}

			private void Choose (int index)
			{
				if (index < 0 || index >= Colors.Length)
					return;
				selected = index;
				Invalidate ();
				owner.OnSwatchChosen (this, index);
			}

			protected override void OnMouseDown (MouseEventArgs e)
			{
				base.OnMouseDown (e);
				Focus ();
				int i = HitTest (e.Location);
				if (i >= 0)
					Choose (i);
			}

			protected override void OnMouseDoubleClick (MouseEventArgs e)
			{
				base.OnMouseDoubleClick (e);
				if (HitTest (e.Location) >= 0)
					owner.Accept ();
			}

			protected override void OnGotFocus (EventArgs e) { base.OnGotFocus (e); Invalidate (); }
			protected override void OnLostFocus (EventArgs e) { base.OnLostFocus (e); Invalidate (); }

			protected override bool IsInputKey (Keys keyData)
			{
				switch (keyData & Keys.KeyCode) {
				case Keys.Left: case Keys.Right: case Keys.Up: case Keys.Down: case Keys.Space:
					return true;
				}
				return base.IsInputKey (keyData);
			}

			protected override void OnKeyDown (KeyEventArgs e)
			{
				int at = selected >= 0 ? selected : 0;
				int col = at % Columns, row = at / Columns;
				switch (e.KeyCode) {
				case Keys.Left: col = (col + Columns - 1) % Columns; break;
				case Keys.Right: col = (col + 1) % Columns; break;
				case Keys.Up: row = (row + Rows - 1) % Rows; break;
				case Keys.Down: row = (row + 1) % Rows; break;
				case Keys.Space: break;
				default: base.OnKeyDown (e); return;
				}
				Choose (row * Columns + col);
				e.Handled = true;
			}
		}

		/// <summary>comdlg32's rainbow (COLOR_RAINBOW): hue across, saturation down, at luminance 120,
		/// in blocks of hue 4 and saturation 8, with the cross hair at the current hue and saturation.</summary>
		internal sealed class RainbowBox : Control
		{
			private readonly ColorDialog owner;
			private bool dragging;
			internal const int HUEINC = 4, SATINC = 8;

			public RainbowBox (ColorDialog owner)
			{
				this.owner = owner;
				SetStyle (ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint, true);
				SetStyle (ControlStyles.Selectable, false);
				TabStop = false;
			}

			private Rectangle Inner => new Rectangle (1, 1, Width - 2, Height - 2);

			/// <summary>The inner block for a hue/saturation step: comdlg32's (extent * step / RANGE).</summary>
			internal static void Paint (Graphics g, Rectangle inner)
			{
				int top = 0;
				for (int s = RANGE; s > 0; s -= SATINC) {
					int bottom = inner.Height * (RANGE - s + SATINC) / RANGE;
					int left = 0;
					for (int h = 0; h < RANGE - 1; h += HUEINC) {
						int right = inner.Width * (h + HUEINC) / RANGE;
						using (var b = new SolidBrush (HlsToRgb (h, RANGE / 2, s)))
							g.FillRectangle (b, inner.X + left, inner.Y + top, right - left, bottom - top);
						left = right;
					}
					top = bottom;
				}
			}

			protected override void OnPaint (PaintEventArgs e)
			{
				Graphics g = e.Graphics;
				StaticEdge (g, new Rectangle (0, 0, Width, Height));
				Rectangle inner = Inner;
				// Filled block by block rather than from a cached bitmap: a drawn image is sampled,
				// and sampling smears each block's first column into its neighbour.
				Paint (g, inner);
				if (dragging)
					return;
				// The cross hair: four 3-pixel-wide arms, 5 long, 5 out from the point.
				int cx = inner.X + owner.hue * inner.Width / RANGE;
				int cy = inner.Y + (RANGE - owner.sat) * inner.Height / RANGE;
				Region old = g.Clip;
				g.SetClip (inner);
				g.FillRectangle (Brushes.Black, cx - 1, cy - 9, 3, 5);
				g.FillRectangle (Brushes.Black, cx - 1, cy + 5, 3, 5);
				g.FillRectangle (Brushes.Black, cx - 9, cy - 1, 5, 3);
				g.FillRectangle (Brushes.Black, cx + 5, cy - 1, 5, 3);
				g.Clip = old;
			}

			private void Track (Point p)
			{
				Rectangle inner = Inner;
				int x = Math.Clamp (p.X - inner.X, 0, inner.Width - 1);
				int y = Math.Clamp (p.Y - inner.Y, 0, inner.Height - 1);
				int h = Math.Min (RANGE - 1, x * RANGE / inner.Width);
				int s = RANGE - y * RANGE / inner.Height;
				owner.SetFromHls (h, owner.lum, s, null);
			}

			protected override void OnMouseDown (MouseEventArgs e)
			{
				base.OnMouseDown (e);
				if (e.Button != MouseButtons.Left)
					return;
				dragging = true;
				Capture = true;
				Track (e.Location);
			}

			protected override void OnMouseMove (MouseEventArgs e)
			{
				base.OnMouseMove (e);
				if (dragging)
					Track (e.Location);
			}

			protected override void OnMouseUp (MouseEventArgs e)
			{
				base.OnMouseUp (e);
				if (!dragging)
					return;
				dragging = false;
				Capture = false;
				Invalidate ();
			}

			protected override void OnMouseDoubleClick (MouseEventArgs e)
			{
				base.OnMouseDoubleClick (e);
				owner.Accept ();
			}

		}

		/// <summary>WS_EX_STATICEDGE: shadow top-left, highlight bottom-right, one pixel.</summary>
		internal static void StaticEdge (Graphics g, Rectangle r)
			=> Frame (g, r, SystemColors.ControlDark, SystemColors.ControlLightLight);

		/// <summary>A one-pixel frame in whole-pixel fills: topLeft along the top and left edges
		/// (short of the far corners), bottomRight along the bottom and right ones.</summary>
		internal static void Frame (Graphics g, Rectangle r, Color topLeft, Color bottomRight)
		{
			using (var b = new SolidBrush (topLeft)) {
				g.FillRectangle (b, r.X, r.Y, r.Width - 1, 1);
				g.FillRectangle (b, r.X, r.Y, 1, r.Height - 1);
			}
			using (var b = new SolidBrush (bottomRight)) {
				g.FillRectangle (b, r.X, r.Bottom - 1, r.Width, 1);
				g.FillRectangle (b, r.Right - 1, r.Y, 1, r.Height);
			}
		}

		/// <summary>The luminance bar (COLOR_LUMSCROLL), or the strip right of it holding its arrow:
		/// the current hue and saturation from luminance 240 at the top to 0 at the bottom in steps
		/// of 8, and a black arrow at the current luminance pointing into it.</summary>
		internal sealed class LumScroll : Control
		{
			private readonly ColorDialog owner;
			private readonly bool arrow;
			private bool dragging;
			internal const int LUMINC = 8, InnerHeight = 187, ArrowHalf = 13;

			public LumScroll (ColorDialog owner, bool arrow)
			{
				this.owner = owner;
				this.arrow = arrow;
				SetStyle (ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint, true);
				SetStyle (ControlStyles.Selectable, false);
				TabStop = false;
			}

			/// <summary>The top of the bar's gradient in this control: inside the bar's edge, or (in
			/// the arrow strip, which starts 7 above the bar) at 8.</summary>
			private int InnerTop => arrow ? 8 : 1;

			/// <summary>Where each luminance block starts down the bar: the block for luminance L is
			/// centred on L's position, and the first and last are half blocks of the same height.</summary>
			internal static int Boundary (int k, int height)
				=> k >= RANGE / LUMINC ? height - Boundary (1, height)
					: (int) Math.Ceiling (((LUMINC * k - LUMINC / 2) * height - 24) / (double) RANGE);

			/// <summary>The arrow's tip, down from the gradient's top. It spans one pixel less than the
			/// bar: at luminance 150 Windows points at 77, at 90 at 124.</summary>
			internal static int TipOffset (int lum) => (RANGE - lum) * (InnerHeight - 1) / RANGE;

			protected override void OnPaint (PaintEventArgs e)
			{
				Graphics g = e.Graphics;
				using (var back = new SolidBrush (BackColor))
					g.FillRectangle (back, ClientRectangle);
				if (arrow) {
					int tip = InnerTop + TipOffset (owner.lum);
					for (int dy = -ArrowHalf; dy <= ArrowHalf; dy++) {
						int x = 1 + Math.Abs (dy);
						if (x < Width)
							g.FillRectangle (Brushes.Black, x, tip + dy, Width - x, 1);
					}
					return;
				}
				StaticEdge (g, new Rectangle (0, 0, Width, Height));
				var inner = new Rectangle (1, 1, Width - 2, Height - 2);
				int top = 0, k = 0;
				for (int l = RANGE; l >= 0; l -= LUMINC) {
					k++;
					int bottom = l == 0 ? inner.Height : Boundary (k, inner.Height);
					using (var b = new SolidBrush (HlsToRgb (owner.hue, l, owner.sat)))
						g.FillRectangle (b, inner.X, inner.Y + top, inner.Width, bottom - top);
					top = bottom;
				}
			}

			private void Track (int y)
			{
				int l = RANGE - Math.Clamp (y - InnerTop, 0, InnerHeight - 1) * RANGE / (InnerHeight - 1);
				owner.SetFromHls (owner.hue, l, owner.sat, null);
			}

			protected override void OnMouseDown (MouseEventArgs e)
			{
				base.OnMouseDown (e);
				if (e.Button != MouseButtons.Left)
					return;
				dragging = true;
				Capture = true;
				Track (e.Y);
			}

			protected override void OnMouseMove (MouseEventArgs e)
			{
				base.OnMouseMove (e);
				if (dragging)
					Track (e.Y);
			}

			protected override void OnMouseUp (MouseEventArgs e)
			{
				base.OnMouseUp (e);
				dragging = false;
				Capture = false;
			}
		}

		/// <summary>The current colour (COLOR_CURRENT): "Color" on the left half and "Solid" on the
		/// right, the same colour on any display with more than 256 colours.</summary>
		internal sealed class SampleBox : Control
		{
			private readonly ColorDialog owner;

			public SampleBox (ColorDialog owner)
			{
				this.owner = owner;
				SetStyle (ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint, true);
				SetStyle (ControlStyles.Selectable, false);
				TabStop = false;
			}

			protected override void OnPaint (PaintEventArgs e)
			{
				StaticEdge (e.Graphics, new Rectangle (0, 0, Width, Height));
				using (var b = new SolidBrush (owner.current))
					e.Graphics.FillRectangle (b, 1, 1, Width - 2, Height - 2);
			}
		}

		/// <summary>One of the six number edits: 27x20, up to three digits, clamped to its range.</summary>
		internal sealed class NumberBox : TextBox
		{
			private readonly ColorDialog owner;
			private readonly int max;
			private bool setting;

			public NumberBox (ColorDialog owner, int max)
			{
				this.owner = owner;
				this.max = max;
				AutoSize = false;
				Size = new Size (27, 20);
				MaxLength = 3;
			}

			public int Value {
				get {
					int.TryParse (Text, out int v);
					return Math.Clamp (v, 0, max);
				}
				set {
					setting = true;
					try {
						Text = value.ToString ();
					} finally {
						setting = false;
					}
				}
			}

			protected override void OnKeyPress (KeyPressEventArgs e)
			{
				if (!char.IsControl (e.KeyChar) && !char.IsDigit (e.KeyChar))
					e.Handled = true;
				base.OnKeyPress (e);
			}

			protected override void OnTextChanged (EventArgs e)
			{
				base.OnTextChanged (e);
				if (setting || Text.Length == 0)
					return;
				if (int.TryParse (Text, out int v) && v > max)
					Value = max;
				owner.OnNumberTyped (this, Value);
			}

			protected override void OnLostFocus (EventArgs e)
			{
				base.OnLostFocus (e);
				if (Text.Length == 0)
					Value = 0;
			}
		}
		#endregion
	}
}
