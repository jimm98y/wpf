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
// Copyright (C) 2004-2006 Novell, Inc.
//
// Authors:
//		Jordi Mas i Hernandez	jordi@ximian.com
//		Peter Dennis Bartok	pbartok@novell.com
//
//

using System.Drawing;
using System.ComponentModel;
using System.Drawing.Imaging;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace System.Windows.Forms
{
	[DefaultProperty ("Value")]
	[DefaultBindingProperty ("Value")]
	[ClassInterface (ClassInterfaceType.AutoDispatch)]
	[ComVisible (true)]
	public class ProgressBar : Control
	{
		#region Local Variables
		private int maximum;
		private int minimum;
		internal int step;
		internal int val;
		internal DateTime start = DateTime.Now;
		internal Rectangle client_area = new Rectangle ();
		internal ProgressBarStyle style;
		Timer marquee_timer;
		bool right_to_left_layout;
		private static readonly Color defaultForeColor = SystemColors.Highlight;
		#endregion	// Local Variables

		#region events
		static object RightToLeftLayoutChangedEvent = new object ();
		
		[Browsable (false)]
		[EditorBrowsable (EditorBrowsableState.Never)]
		public new event EventHandler BackgroundImageChanged {
			add { base.BackgroundImageChanged += value; }
			remove { base.BackgroundImageChanged -= value; }
		}
		
		[Browsable(false)]
		[EditorBrowsable(EditorBrowsableState.Never)]
		public new event EventHandler BackgroundImageLayoutChanged {
			add	{ base.BackgroundImageLayoutChanged += value; }
			remove { base.BackgroundImageLayoutChanged -= value; }
		}
		
		[Browsable (false)]
		[EditorBrowsable (EditorBrowsableState.Never)]
		public new event EventHandler CausesValidationChanged {
			add { base.CausesValidationChanged += value; }
			remove { base.CausesValidationChanged -= value; }
		}
		
		[Browsable (false)]
		[EditorBrowsable (EditorBrowsableState.Never)]
		public new event EventHandler DoubleClick {
			add { base.DoubleClick += value; }
			remove { base.DoubleClick -= value; }
		}
		
		[Browsable (false)]
		[EditorBrowsable (EditorBrowsableState.Never)]
		public new event EventHandler Enter {
			add { base.Enter += value; }
			remove { base.Enter -= value; }
		}
		
		[Browsable (false)]
		[EditorBrowsable (EditorBrowsableState.Never)]
		public new event EventHandler FontChanged {
			add { base.FontChanged += value; }
			remove { base.FontChanged -= value; }
		}
		
		[Browsable (false)]
		[EditorBrowsable (EditorBrowsableState.Never)]
		public new event EventHandler ImeModeChanged {
			add { base.ImeModeChanged += value; }
			remove { base.ImeModeChanged -= value; }
		}
		
		[Browsable (false)]
		[EditorBrowsable (EditorBrowsableState.Never)]
		public new event KeyEventHandler KeyDown {
			add { base.KeyDown += value; }
			remove { base.KeyDown -= value; }
		}
		
		[Browsable (false)]
		[EditorBrowsable (EditorBrowsableState.Never)]
		public new event KeyPressEventHandler KeyPress {
			add { base.KeyPress += value; }
			remove { base.KeyPress -= value; }
		}
		
		[Browsable (false)]
		[EditorBrowsable (EditorBrowsableState.Never)]
		public new event KeyEventHandler KeyUp {
			add { base.KeyUp += value; }
			remove { base.KeyUp -= value; }
		}
		
		[Browsable (false)]
		[EditorBrowsable (EditorBrowsableState.Never)]
		public new event EventHandler Leave {
			add { base.Leave += value; }
			remove { base.Leave -= value; }
		}
		
		[EditorBrowsable(EditorBrowsableState.Never)]
		[Browsable(false)]
		public new event MouseEventHandler MouseDoubleClick {
			add { base.MouseDoubleClick += value; }
			remove { base.MouseDoubleClick -= value; }
		}
		
		[Browsable(false)]
		[EditorBrowsable(EditorBrowsableState.Never)]
		public new event EventHandler PaddingChanged {
			add { base.PaddingChanged += value; }
			remove { base.PaddingChanged -= value; }
		}
		
		[Browsable (false)]
		[EditorBrowsable (EditorBrowsableState.Never)]
		public new event PaintEventHandler Paint {
			add { base.Paint += value; }
			remove { base.Paint -= value; }
		}
		
		public event EventHandler RightToLeftLayoutChanged {
			add { Events.AddHandler (RightToLeftLayoutChangedEvent, value); }
			remove { Events.RemoveHandler (RightToLeftLayoutChangedEvent, value); }
		}
		
		[Browsable (false)]
		[EditorBrowsable (EditorBrowsableState.Never)]
		public new event EventHandler TabStopChanged {
			add { base.TabStopChanged += value; }
			remove { base.TabStopChanged -= value; }
		}
		
		[Browsable (false)]
		[EditorBrowsable (EditorBrowsableState.Never)]
		public new event EventHandler TextChanged {
			add { base.TextChanged += value; }
			remove { base.TextChanged -= value; }
		}
		#endregion Events

		#region Public Constructors
		public ProgressBar()
		{
			maximum = 100;
			minimum = 0;
			step = 10;
			val = 0;

			base.Resize += new EventHandler (OnResizeTB);

			SetStyle (ControlStyles.UserPaint | 
				ControlStyles.Selectable | 
				ControlStyles.ResizeRedraw | 
				ControlStyles.Opaque |
				ControlStyles.UseTextForAccessibility
				, false);

			force_double_buffer = true;
			
			ForeColor = defaultForeColor;
		}
		#endregion	// Public Constructors

		#region Public Instance Properties

		[Browsable (false)]
		[EditorBrowsable (EditorBrowsableState.Never)]
		public override bool AllowDrop
		{
			get { return base.AllowDrop; }
			set {
				base.AllowDrop = value;
			}
		}

		// Setting this property in MS .Net 1.1 does not have any visual effect and it
		// does not fire a BackgroundImageChanged event
		[Browsable (false)]
		[EditorBrowsable (EditorBrowsableState.Never)]
		public override Image BackgroundImage
		{
			get { return base.BackgroundImage; }
			set { base.BackgroundImage = value; }
		}

		[Browsable(false)]
		[EditorBrowsable(EditorBrowsableState.Never)]
		public override ImageLayout BackgroundImageLayout {
				get	{ return base.BackgroundImageLayout; }
				set { base.BackgroundImageLayout = value; }
		}

		[Browsable (false)]
		[EditorBrowsable (EditorBrowsableState.Never)]
		public new bool CausesValidation
		{
			get { return base.CausesValidation; }
			set { base.CausesValidation = value; }
		}

		protected override CreateParams CreateParams
		{
			get { return base.CreateParams;	}
		}

		protected override ImeMode DefaultImeMode
		{
			get { return base.DefaultImeMode; }
		}

		protected override Size DefaultSize
		{
			get { return ThemeEngine.Current.ProgressBarDefaultSize; }
		}

		[EditorBrowsable(EditorBrowsableState.Never)]
		protected override bool DoubleBuffered {
				get { return base.DoubleBuffered; }
				set { base.DoubleBuffered = value; }
		}

		// Setting this property in MS .Net 1.1 does not have any visual effect and it
		// does not fire a FontChanged event
		[Browsable (false)]
		[EditorBrowsable (EditorBrowsableState.Never)]
		public override Font Font
		{
			get { return base.Font; }
			set { base.Font = value; }
		}

		[Browsable (false)]
		[EditorBrowsable (EditorBrowsableState.Never)]
		public new ImeMode ImeMode
		{
			get { return base.ImeMode; }
			set { base.ImeMode = value; }
		}

		[RefreshProperties(RefreshProperties.Repaint)]
		[DefaultValue (100)]
		public int Maximum
		{
			get {
				return maximum;
			}
			set {
				if (value < 0)
					throw new ArgumentOutOfRangeException ("Maximum", 
						string.Format("Value '{0}' must be greater than or equal to 0.", value ));

				maximum = value;
				minimum = Math.Min (minimum, maximum);
				val = Math.Min (val, maximum);
				Refresh ();
			}
		}

		[RefreshProperties(RefreshProperties.Repaint)]
		[DefaultValue (0)]
		public int Minimum {
			get {
				return minimum;
			}
			set {
				if (value < 0)
					throw new ArgumentOutOfRangeException ("Minimum", 
						string.Format("Value '{0}' must be greater than or equal to 0.", value ));

				minimum = value;
				maximum = Math.Max (maximum, minimum);
				val = Math.Max (val, minimum);
				Refresh ();
			}
		}

		[DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
		[Browsable(false)]
		[EditorBrowsable(EditorBrowsableState.Never)]
		public new Padding Padding {
			get { return base.Padding; }
			set { base.Padding = value; }
		}
			
		[Localizable(true)]
		[DefaultValue(false)]
		[MonoTODO ("RTL is not supported")]
		public virtual bool RightToLeftLayout {
				get { return right_to_left_layout;}
				set	{ 
					if (right_to_left_layout != value) {
						right_to_left_layout = value;
						OnRightToLeftLayoutChanged (EventArgs.Empty);
					}
				}		
		}

		[DefaultValue (10)]
		public int Step
		{
			get { return step; }
			set {
				step = value;
				Refresh ();
			}
		}

		[Browsable (true)]
		[DefaultValue (ProgressBarStyle.Blocks)]
		[EditorBrowsable (EditorBrowsableState.Always)]
		public ProgressBarStyle Style {
			get {
				return style;
			}

			set {
				if (value != ProgressBarStyle.Blocks && value != ProgressBarStyle.Continuous
						&& value != ProgressBarStyle.Marquee)
					throw new InvalidEnumArgumentException ("value", unchecked((int)value), typeof (ProgressBarStyle));
				if (style != value) {
					style = value;

					if (style == ProgressBarStyle.Marquee) {
						if (marquee_timer == null) {
							marquee_timer = new Timer ();
							marquee_timer.Interval = 10;
							marquee_timer.Tick += new EventHandler (marquee_timer_Tick);
						}
						marquee_timer.Start ();
					} else {
						if (marquee_timer != null) {
							marquee_timer.Stop ();
						}
						Refresh ();
					}
				}
			}
		}

		void marquee_timer_Tick (object sender, EventArgs e)
		{
			Invalidate ();
		}
		
		int marquee_animation_speed = 100;
		[DefaultValue (100)]
		public int MarqueeAnimationSpeed {
			get {
				return marquee_animation_speed;
			}

			set {
				marquee_animation_speed = value;
			}
		}

		[Browsable (false)]
		[EditorBrowsable (EditorBrowsableState.Never)]
		public new bool TabStop
		{
			get { return base.TabStop; }
			set { base.TabStop = value; }
		}

		[Browsable (false)]
		[EditorBrowsable (EditorBrowsableState.Never)]
		[Bindable(false)]
		public override string Text
		{
			get { return base.Text; }
			set { base.Text = value; }
		}

		[Bindable(true)]
		[DefaultValue (0)]
		public int Value
		{
			get {
				return val;
			}
			set {
				if (value < Minimum || value > Maximum)
					throw new ArgumentOutOfRangeException ("Value", string.Format("'{0}' is not a valid value for 'Value'. 'Value' should be between 'Minimum' and 'Maximum'", value));
				val = value;
				SetHighlightTimer (true);
				Refresh ();
			}
		}


		#endregion	// Protected Instance Properties

		#region Public Instance Methods
		
		protected override void CreateHandle ()
		{
			base.CreateHandle ();
		}

		public void Increment (int value)
		{
			if (Style == ProgressBarStyle.Marquee)
				throw new InvalidOperationException ("Increment should not be called if the style is Marquee.");

			int newValue = Value + value;

			if (newValue < Minimum)
				newValue = Minimum;

			if (newValue > Maximum)
				newValue = Maximum;

			Value = newValue;
			Refresh ();
		}

		protected override void OnHandleCreated (EventArgs e)
		{
			base.OnHandleCreated (e);

			UpdateAreas ();
			SetHighlightTimer (true);
		}

		protected override void OnBackColorChanged (EventArgs e)
		{
			base.OnBackColorChanged (e);
		}
			
		protected override void OnForeColorChanged (EventArgs e)
		{
			base.OnForeColorChanged (e);
		}
			
		// ---- the themed highlight -------------------------------------------------------------
		//
		// comctl32's Progress_SetHighlightTimer / Progress_TimerProc / Progress_PaintThemed: a bar
		// whose value is strictly between its minimum and maximum waits a second (the theme's 1000),
		// then repaints at the theme's 30 frames a second; every themed paint moves the glow on by
		// the theme's 12 pixels (see ThemeWin11.DrawModernProgressBar), and when it has run off the
		// end the bar goes quiet and the wait starts again.

		/// <summary>Where the glow is, in pixels along the fill; meaningful while HighlightActive.</summary>
		internal int highlight_pos;
		internal bool HighlightActive => highlight_step != null && highlight_step.Enabled;
		Timer highlight_delay, highlight_step;

		/// <summary>How many bars are sweeping right now: a capture that must hold still waits for 0.</summary>
		internal static int s_active_highlights;

		bool HighlightAllowed => IsHandleCreated && Style != ProgressBarStyle.Marquee && val != Minimum && val != Maximum
					  && ThemeEngine.Current.ProgressBarAnimates;

		void SetHighlightTimer (bool start)
		{
			if (!start || !HighlightAllowed) {
				highlight_delay?.Stop ();
				return;
			}
			if (HighlightActive || (highlight_delay != null && highlight_delay.Enabled))
				return;
			if (highlight_delay == null) {
				highlight_delay = new Timer { Interval = 1000 };
				highlight_delay.Tick += delegate {
					highlight_delay.Stop ();
					if (!HighlightAllowed)
						return;
					highlight_pos = 0;
					if (highlight_step == null) {
						highlight_step = new Timer { Interval = 1000 / 30 };
						highlight_step.Tick += delegate { Invalidate (); };
					}
					highlight_step.Start ();
					s_active_highlights++;
				};
			}
			highlight_delay.Start ();
		}

		/// <summary>The glow has left the fill: stop, and wait to go again.</summary>
		internal void EndHighlight ()
		{
			if (HighlightActive) {
				highlight_step.Stop ();
				s_active_highlights--;
			}
			highlight_pos = 0;
			SetHighlightTimer (true);
		}

		protected override void OnHandleDestroyed (EventArgs e)
		{
			if (HighlightActive) {
				highlight_step.Stop ();
				s_active_highlights--;
			}
			highlight_delay?.Stop ();
			Animation.Forget (this);
			base.OnHandleDestroyed (e);
		}
			
		[EditorBrowsable(EditorBrowsableState.Advanced)]
		protected virtual void OnRightToLeftLayoutChanged(EventArgs e)
		{
			EventHandler eh = (EventHandler) Events [RightToLeftLayoutChangedEvent];
			if (eh != null)
				eh (this, e);
		}
			
		public void PerformStep ()
		{
			if (Style == ProgressBarStyle.Marquee)
				throw new InvalidOperationException ("PerformStep should not be called if the style is Marquee.");

			Increment (Step);
		}

		[EditorBrowsable (EditorBrowsableState.Never)]
		public override void ResetForeColor ()
		{
			ForeColor = defaultForeColor;
		}

		public override string ToString()
		{
			return string.Format ("{0}, Minimum: {1}, Maximum: {2}, Value: {3}",
				GetType().FullName,
				Minimum.ToString (),
				Maximum.ToString (),
				Value.ToString () );
		}

		#endregion	// Public Instance Methods

		#region Private Instance Methods

		private void UpdateAreas ()
		{
			client_area.X = client_area.Y = 2;
			client_area.Width = Width - 4;
			client_area.Height = Height - 4;
		}

		private void OnResizeTB (Object o, EventArgs e)
		{
			if (Width <= 0 || Height <= 0)
				return;

			UpdateAreas ();
			Invalidate();	// Invalidate the full surface, blocks will not match
		}

		internal override void OnPaintInternal (PaintEventArgs pevent)
		{
			ThemeEngine.Current.DrawProgressBar (pevent.Graphics, pevent.ClipRectangle, this);
		}

		#endregion
	}
}
