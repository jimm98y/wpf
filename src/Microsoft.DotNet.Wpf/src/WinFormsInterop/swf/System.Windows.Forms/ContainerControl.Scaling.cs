// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// Auto-scaling and DPI-change scaling, ported from .NET's ContainerControl. A designer writes
// AutoScaleDimensions (the font or DPI the form was laid out at) and AutoScaleMode; when the form
// runs where the font measures differently -- a 200% display -- the controls are scaled by the
// ratio when layout resumes (OnChildLayoutResuming / OnLayoutResuming), and again by the ratio of
// the two DPIs when the form moves to another display (ScaleContainerForDpi).

using System.Collections.Specialized;
using System.ComponentModel;
using System.Drawing;

namespace System.Windows.Forms
{
	public partial class ContainerControl
	{
		private SizeF _autoScaleDimensions = SizeF.Empty;
		private SizeF _currentAutoScaleDimensions = SizeF.Empty;
		private AutoScaleMode _autoScaleMode = AutoScaleMode.Inherit;

		// A top-level window already scaled by the rectangle WM_DPICHANGED suggested.
		private bool _isScaledByDpiChangedEvent;

		private bool _stateScalingNeededOnLayout;
		private bool _stateScalingChild;
		private bool _stateParentChanged;

		private const string FontMeasureString = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ";

		/// <summary>The factor a child container that inherits its mode is scaled by in a DPI
		/// change: its parent's, as it was before the parent's font changed.</summary>
		internal SizeF _currentAutoScaleFactor = new SizeF (1F, 1F);

		/// <summary>The DPI or font the form was designed at (the designer writes it); when it
		/// differs from <see cref="CurrentAutoScaleDimensions"/> the controls are scaled by the ratio.</summary>
		[Localizable (true)]
		[Browsable (false)]
		[EditorBrowsable (EditorBrowsableState.Advanced)]
		[DesignerSerializationVisibility (DesignerSerializationVisibility.Hidden)]
		public SizeF AutoScaleDimensions {
			get => _autoScaleDimensions;
			set {
				if (value.Width < 0 || value.Height < 0)
					throw new ArgumentOutOfRangeException (nameof (value), value, "AutoScaleDimensions must not be negative.");
				_autoScaleDimensions = value;
				if (!_autoScaleDimensions.IsEmpty)
					LayoutScalingNeeded ();
			}
		}

		/// <summary>CurrentAutoScaleDimensions over AutoScaleDimensions; 1 when nothing was designed.</summary>
		protected SizeF AutoScaleFactor {
			get {
				_currentAutoScaleFactor = GetCurrentAutoScaleFactor (CurrentAutoScaleDimensions, AutoScaleDimensions);
				return _currentAutoScaleFactor;
			}
		}

		internal static SizeF GetCurrentAutoScaleFactor (SizeF currentAutoScaleDimensions, SizeF savedAutoScaleDimensions)
			=> savedAutoScaleDimensions.IsEmpty
				? new SizeF (1.0F, 1.0F)
				: new SizeF (currentAutoScaleDimensions.Width / savedAutoScaleDimensions.Width,
					currentAutoScaleDimensions.Height / savedAutoScaleDimensions.Height);

		[Browsable (false)]
		[EditorBrowsable (EditorBrowsableState.Advanced)]
		[DesignerSerializationVisibility (DesignerSerializationVisibility.Hidden)]
		public AutoScaleMode AutoScaleMode {
			get => _autoScaleMode;
			set {
				if (!Enum.IsDefined (typeof (AutoScaleMode), value))
					throw new InvalidEnumArgumentException (nameof (value), (int) value, typeof (AutoScaleMode));
				bool scalingNeeded = false;
				if (value != _autoScaleMode) {
					// Dimensions designed for another mode mean nothing in this one.
					if (_autoScaleMode != AutoScaleMode.Inherit)
						_autoScaleDimensions = SizeF.Empty;
					_currentAutoScaleDimensions = SizeF.Empty;
					_autoScaleMode = value;
					scalingNeeded = true;
				}
				OnAutoScaleModeChanged ();
				if (scalingNeeded)
					LayoutScalingNeeded ();
			}
		}

		/// <summary>The DPI or font this control has at run time, in the terms of AutoScaleMode.</summary>
		[Browsable (false)]
		[EditorBrowsable (EditorBrowsableState.Advanced)]
		public SizeF CurrentAutoScaleDimensions {
			get {
				if (_currentAutoScaleDimensions.IsEmpty)
					_currentAutoScaleDimensions = GetCurrentAutoScaleDimensions (Font);
				return _currentAutoScaleDimensions;
			}
		}

		internal SizeF GetCurrentAutoScaleDimensions (Font font)
		{
			switch (AutoScaleMode) {
			case AutoScaleMode.Font:
				return GetFontAutoScaleDimensions (font);
			case AutoScaleMode.Dpi:
				return ScaleHelper.IsThreadPerMonitorV2Aware
					? new SizeF (DeviceDpiInternal, DeviceDpiInternal)
					: new SizeF (ScaleHelper.InitialSystemDpi, ScaleHelper.InitialSystemDpi);
			default:
				return AutoScaleDimensions;
			}
		}

		/// <summary>Whether the container must be rescaled when its parent is, in a DPI change,
		/// whether its font is its own or inherited.</summary>
		internal bool IsDpiChangeScalingRequired { get; set; }

		/// <summary>
		/// Windows' dialog-unit measure of a font, as .NET clones it: the height is TEXTMETRIC's
		/// tmHeight, the width the extent of the alphabet over 52 for a proportional face and
		/// tmAveCharWidth for a fixed-pitch one -- measured on a DC at the screen's DPI.
		/// </summary>
		internal static SizeF GetFontAutoScaleDimensions (Font font)
		{
			float emPx = font.SizeInPoints * System.Drawing.WebGpuBackend.DisplayDpi.Y / 72f;
			string family = font.FontFamily.Name;
			int height;
			if (System.Drawing.WebGpuBackend.TextMetrics.TryGetGdiLineMetrics (family, font.Bold, font.Italic, emPx, out int asc, out int desc) && asc + desc > 0)
				height = asc + desc;
			else
				height = font.Height;

			Size wide = TextRenderer.MeasureText ("W", font, Size.Empty, TextFormatFlags.NoPadding);
			Size narrow = TextRenderer.MeasureText ("i", font, Size.Empty, TextFormatFlags.NoPadding);
			int width;
			if (wide.Width == narrow.Width
			    && System.Drawing.WebGpuBackend.TextMetrics.TryGetAverageCharWidth (family, font.Bold, font.Italic, emPx, out int ave) && ave > 0) {
				width = ave;   // fixed pitch: tmAveCharWidth
			} else {
				Size extent = TextRenderer.MeasureText (FontMeasureString, font, Size.Empty, TextFormatFlags.NoPadding);
				// Intentional integer round off, for Win32 compatibility.
				width = (int) Math.Round (extent.Width / (float) FontMeasureString.Length);
			}
			return new SizeF (width, height);
		}

		private static void EnableRequiredScaling (Control start, bool enable)
		{
			start.RequiredScalingEnabled = enable;
			foreach (Control c in start.Controls.GetAllControls ())
				EnableRequiredScaling (c, enable);
		}

		/// <summary>An auto-scale property changed: scale on the next layout.</summary>
		private void LayoutScalingNeeded ()
		{
			EnableRequiredScaling (this, true);
			_stateScalingNeededOnLayout = true;
			// Not suspended: lay out now, as otherwise nothing says when.
			if (layout_suspended == 0)
				PerformLayout (this, "Bounds");
		}

		/// <summary>For Form, to keep the obsolete AutoScale in step.</summary>
		private protected virtual void OnAutoScaleModeChanged ()
		{
		}

		internal override void OnChildLayoutResuming (Control child, bool performLayout)
		{
			base.OnChildLayoutResuming (child, performLayout);

			// A Dpi-mode container does not scale its children here.
			if (AutoScaleMode == AutoScaleMode.Dpi)
				return;

			// The designer's code resumes layout with ResumeLayout(false) then PerformLayout: scale
			// the children before their layout engines get to them.
			if (!_stateScalingChild
			    && !performLayout
			    && AutoScaleMode != AutoScaleMode.None && AutoScaleMode != AutoScaleMode.Inherit
			    && _stateScalingNeededOnLayout
			    && (AutoScaleFactor.Width != 1.0F || AutoScaleFactor.Height != 1.0F)) {
				_stateScalingChild = true;
				try {
					child.Scale (AutoScaleFactor, SizeF.Empty, this);
				} finally {
					_stateScalingChild = false;
				}
			}
		}

		[EditorBrowsable (EditorBrowsableState.Advanced)]
		protected override void OnFontChanged (EventArgs e)
		{
			// A font change -- also the one a DPI change makes -- rescales by the new ratio.
			if (AutoScaleMode != AutoScaleMode.None && IsHandleCreated) {
				_currentAutoScaleDimensions = SizeF.Empty;
				SuspendAllLayout (this);
				try {
					PerformAutoScale (!RequiredScalingEnabled, excludedBounds: true, causedByFontChanged: true);
				} finally {
					ResumeAllLayout (this, performLayout: false);
				}
			}
			base.OnFontChanged (e);
		}

		protected override void OnLayout (LayoutEventArgs e)
		{
			PerformNeededAutoScaleOnLayout ();
			base.OnLayout (e);
		}

		internal override void OnLayoutResuming (bool performLayout)
		{
			PerformNeededAutoScaleOnLayout ();
			base.OnLayoutResuming (performLayout);
		}

		protected override void OnParentChanged (EventArgs e)
		{
			_stateParentChanged = !RequiredScalingEnabled;
			base.OnParentChanged (e);
		}

		/// <summary>Scales the controls by the auto-scale factor.</summary>
		public void PerformAutoScale () => PerformAutoScale (includedBounds: true, excludedBounds: true);

		private void PerformAutoScale (bool includedBounds, bool excludedBounds, bool causedByFontChanged = false)
		{
			bool suspended = false;
			try {
				if (AutoScaleMode != AutoScaleMode.None) {
					SuspendAllLayout (this);
					suspended = true;

					SizeF autoScaleFactor = AutoScaleFactor;
					// A child container that inherits its mode and keeps no dimensions of its own
					// is scaled by its parent's factor.
					if (AutoScaleMode == AutoScaleMode.Inherit)
						autoScaleFactor = GetParentAutoScaleFactor ();

					if (autoScaleFactor.Width != 1.0F || autoScaleFactor.Height != 1.0F) {
						SizeF included = includedBounds ? autoScaleFactor : SizeF.Empty;
						SizeF excluded = excludedBounds ? autoScaleFactor : SizeF.Empty;
						Scale (included, excluded, this, causedByFontChanged);
					}
					_autoScaleDimensions = CurrentAutoScaleDimensions;
				}
			} finally {
				if (includedBounds) {
					_stateScalingNeededOnLayout = false;
					EnableRequiredScaling (this, enable: false);
				}
				_stateParentChanged = false;
				if (suspended)
					ResumeAllLayout (this, performLayout: false);
			}
		}

		private void PerformNeededAutoScaleOnLayout ()
		{
			if (_stateScalingNeededOnLayout)
				PerformAutoScale (_stateScalingNeededOnLayout, false);
		}

		private SizeF GetParentAutoScaleFactor ()
		{
			Control parentControl = Parent;
			while (parentControl != null && (!(parentControl is ContainerControl cc) || cc.AutoScaleMode == AutoScaleMode.Inherit))
				parentControl = parentControl.Parent;
			return parentControl is ContainerControl container ? container._currentAutoScaleFactor : new SizeF (1F, 1F);
		}

		internal static void ResumeAllLayout (Control start, bool performLayout)
		{
			foreach (Control c in start.Controls.GetAllControls ())
				ResumeAllLayout (c, performLayout);
			start.ResumeLayout (performLayout);
		}

		internal static void SuspendAllLayout (Control start)
		{
			start.SuspendLayout ();
			start.cached_preferred_size = Size.Empty;
			foreach (Control c in start.Controls.GetAllControls ())
				SuspendAllLayout (c);
		}

		/// <summary>
		/// A container scales its own children by its own factor (except those that still need
		/// scaling, which take the one given), and leaves its own position to the control that
		/// asked: an outer container knows where it goes.
		/// </summary>
		internal override void Scale (SizeF includedFactor, SizeF excludedFactor, Control requestingControl, bool causedByFontChanged = false)
		{
			if (AutoScaleMode == AutoScaleMode.Inherit) {
				base.Scale (includedFactor, excludedFactor, requestingControl, causedByFontChanged);
				return;
			}

			SizeF ourExcludedFactor = excludedFactor;
			SizeF childIncludedFactor = includedFactor;
			if (!ourExcludedFactor.IsEmpty)
				ourExcludedFactor = AutoScaleFactor;
			// Not scaling: do not scale the internal ones either.
			if (AutoScaleMode == AutoScaleMode.None)
				childIncludedFactor = AutoScaleFactor;

			SuspendLayout ();
			try {
				SizeF ourExternalContainerFactor = ourExcludedFactor;
				if (!excludedFactor.IsEmpty && Parent != null) {
					ourExternalContainerFactor = SizeF.Empty;
					bool scaleUs = requestingControl != this || _stateParentChanged || causedByFontChanged;
					if (!scaleUs) {
						bool dt = Site?.DesignMode ?? false;
						bool parentDt = Parent.Site?.DesignMode ?? false;
						if (dt && !parentDt)
							scaleUs = true;
					}
					if (scaleUs)
						ourExternalContainerFactor = excludedFactor;
				}

				// A top-level window WM_DPICHANGED has already sized.
				if (!_isScaledByDpiChangedEvent)
					ScaleControl (includedFactor, ourExternalContainerFactor);

				if (!_doNotScaleChildren)
					ScaleChildControls (childIncludedFactor, ourExcludedFactor, requestingControl, causedByFontChanged);
			} finally {
				ResumeLayout (false);
			}
		}

		/// <summary>Scales the container's minimum and maximum size (Form overrides it).</summary>
		protected virtual void ScaleMinMaxSize (float xScaleFactor, float yScaleFactor, bool updateContainerSize = true)
		{
		}

		[EditorBrowsable (EditorBrowsableState.Advanced)]
		protected override void RescaleConstantsForDpi (int deviceDpiOld, int deviceDpiNew)
		{
			if (deviceDpiNew == deviceDpiOld)
				return;
			base.RescaleConstantsForDpi (deviceDpiOld, deviceDpiNew);

			// An inherited font that the parent does not scale (a designer's hosted form): scale
			// for the new font explicitly.
			if (IsFontSet ())
				return;
			SuspendLayout ();
			try {
				OnFontChanged (EventArgs.Empty);
			} finally {
				ResumeLayout (false);
			}
			Parent?.PerformLayout (this, "Font");
		}

		/// <summary>
		/// The window moved to a display of another DPI: take the font at the new DPI, and with it
		/// the new auto-scale dimensions and the scaling of the controls; a top-level window is put
		/// where Windows suggested (WM_DPICHANGED's rectangle), which also sizes it.
		/// </summary>
		internal void ScaleContainerForDpi (int deviceDpiNew, int deviceDpiOld, Rectangle suggestedRectangle)
		{
			ClearAllPreferredSizeCaches (this);
			SuspendAllLayout (this);
			try {
				if (ScaleHelper.ScaleTopLevelFormMinMaxSizeForDpi) {
					// The factor is the change in the form's bounds: AutoScaleFactor is updated only
					// after the font change below.
					float xScaleFactor = (float) suggestedRectangle.Width / Width;
					float yScaleFactor = (float) suggestedRectangle.Height / Height;
					ScaleMinMaxSize (xScaleFactor, yScaleFactor, updateContainerSize: false);
				}

				// The suggested rectangle sizes the window; the font change must not scale it again.
				_isScaledByDpiChangedEvent = true;

				Font fontForDpi = GetScaledFont (Font, deviceDpiNew, deviceDpiOld);
				ScaledControlFont = fontForDpi;
				if (IsFontSet ()) {
					SetScaledFont (fontForDpi);
				} else {
					SuspendLayout ();
					try {
						OnFontChanged (EventArgs.Empty);
					} finally {
						ResumeLayout (false);
					}
					Parent?.PerformLayout (this, "Font");
				}

				if (IsHandleCreated)
					SetBounds (suggestedRectangle.X, suggestedRectangle.Y, suggestedRectangle.Width, suggestedRectangle.Height, BoundsSpecified.All);
			} finally {
				// Lay out: the DPI change moved everything.
				ResumeAllLayout (this, true);
				_isScaledByDpiChangedEvent = false;
			}
		}

		private static void ClearAllPreferredSizeCaches (Control c)
		{
			c.cached_preferred_size = Size.Empty;
			foreach (Control child in c.Controls.GetAllControls ())
				ClearAllPreferredSizeCaches (child);
		}
	}
}
