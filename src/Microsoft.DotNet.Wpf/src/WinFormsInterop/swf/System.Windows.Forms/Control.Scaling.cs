// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// Auto-scaling, ported from .NET's Control: which bounds still need scaling (RequiredScaling, set
// by every SetBounds until a container scales them), the included / excluded factor pair a
// container's PerformAutoScale hands down, and per-control scaling of bounds, padding, margin and
// minimum / maximum size. Mono's simpler model scaled differently from .NET's at any factor but 1,
// which on a 200% display is every form a designer laid out.

using System.Drawing;
using System.Windows.Forms.Layout;

namespace System.Windows.Forms
{
	public partial class Control
	{
		private const byte RequiredScalingEnabledMask = 0x10;
		private const byte RequiredScalingMask = 0x0F;

		// Bits 0-3: the BoundsSpecified still to be scaled; bit 4: whether that is tracked at all.
		private byte _requiredScaling = RequiredScalingEnabledMask | (byte) BoundsSpecified.All;

		/// <summary>Certain controls (PropertyGrid) scale their children themselves.</summary>
		internal bool _doNotScaleChildren;

		/// <summary>The bounds of the control that still need scaling: set by SetBounds, cleared
		/// when a container scales the control.</summary>
		internal BoundsSpecified RequiredScaling {
			get => (_requiredScaling & RequiredScalingEnabledMask) != 0
				? (BoundsSpecified) (_requiredScaling & RequiredScalingMask)
				: BoundsSpecified.None;
			set {
				byte enableBit = (byte) (_requiredScaling & RequiredScalingEnabledMask);
				_requiredScaling = (byte) (((int) value & RequiredScalingMask) | enableBit);
			}
		}

		/// <summary>Whether RequiredScaling is tracked; if not it is always None.</summary>
		internal bool RequiredScalingEnabled {
			get => (_requiredScaling & RequiredScalingEnabledMask) != 0;
			set {
				byte scaling = (byte) (_requiredScaling & RequiredScalingMask);
				_requiredScaling = scaling;
				if (value)
					_requiredScaling |= RequiredScalingEnabledMask;
			}
		}

		/// <summary>Whether the control is being scaled (ScaleControl).</summary>
		internal bool ScalingInProgress { get; private set; }

		/// <summary>SetBounds marks the bounds it was given as needing scaling.</summary>
		internal void InitScaling (BoundsSpecified specified)
		{
			_requiredScaling |= (byte) ((int) specified & RequiredScalingMask);
		}

		/// <summary>Called when the last ResumeLayout is made, layout still suspended: tells the
		/// parent (OnChildLayoutResuming), where a container scales its children.</summary>
		internal virtual void OnLayoutResuming (bool performLayout)
		{
			parent?.OnChildLayoutResuming (this, performLayout);
		}

		/// <summary>A child is about to resume its layout.</summary>
		internal virtual void OnChildLayoutResuming (Control child, bool performLayout)
		{
			parent?.OnChildLayoutResuming (child, performLayout);
		}

		/// <summary>
		/// Scales the control and its children. <paramref name="includedFactor"/> applies to the
		/// bounds in <see cref="RequiredScaling"/>, <paramref name="excludedFactor"/> to the rest;
		/// an empty factor scales nothing.
		/// </summary>
		internal virtual void Scale (SizeF includedFactor, SizeF excludedFactor, Control requestingControl, bool causedByFontChanged = false)
		{
			SuspendLayout ();
			try {
				ScaleControl (includedFactor, excludedFactor);
				if (!_doNotScaleChildren)
					ScaleChildControls (includedFactor, excludedFactor, requestingControl, causedByFontChanged);
			} finally {
				ResumeLayout (false);
			}
			parent?.PerformLayout (this, "Bounds");
		}

		internal void ScaleChildControls (SizeF includedFactor, SizeF excludedFactor, Control requestingControl, bool causedByFontChanged = false)
		{
			if (!ScaleChildren)
				return;
			Control [] children = Controls.GetAllControls ();
			for (int i = 0; i < children.Length; i++) {
				Control child = children [i];
				// Container controls get their own font change and scale themselves then.
				if (child is ContainerControl && causedByFontChanged)
					continue;
				child.Scale (includedFactor, excludedFactor, requestingControl, causedByFontChanged);
			}
		}

		internal void ScaleControl (SizeF includedFactor, SizeF excludedFactor)
		{
			try {
				ScalingInProgress = true;

				BoundsSpecified includedSpecified = BoundsSpecified.None;
				BoundsSpecified excludedSpecified = BoundsSpecified.None;

				if (!includedFactor.IsEmpty)
					includedSpecified = RequiredScaling;
				if (!excludedFactor.IsEmpty)
					excludedSpecified |= (~RequiredScaling & BoundsSpecified.All);

				if (includedSpecified != BoundsSpecified.None)
					ScaleControl (includedFactor, includedSpecified);
				if (excludedSpecified != BoundsSpecified.None)
					ScaleControl (excludedFactor, excludedSpecified);

				if (!includedFactor.IsEmpty)
					RequiredScaling = BoundsSpecified.None;
			} finally {
				ScalingInProgress = false;
			}
		}

		/// <summary>Scales a size by the factors, but not along an axis the control's style fixes.</summary>
		internal Size ScaleSize (Size startSize, float x, float y)
		{
			Size size = startSize;
			if (!GetStyle (ControlStyles.FixedWidth))
				size.Width = (int) Math.Round (size.Width * x);
			if (!GetStyle (ControlStyles.FixedHeight))
				size.Height = (int) Math.Round (size.Height * y);
			return size;
		}

		/// <summary>The window's frame (AdjustWindowRectEx's adornments) at a DPI: what scaling
		/// leaves alone, since Windows sizes it. Scroll bars are not adornments (AdjustWindowRectEx
		/// does not count them); a control whose .NET window has other styles than this stack's
		/// says what .NET's would have.</summary>
		internal virtual Size AdornmentsAt (int dpi)
		{
			CreateParams cp = CreateParams;
			if ((cp.Style & (int) WindowStyles.WS_CHILD) != 0) {
				int edge = XplatUIWebGpu.BorderEdgeOf (cp.Style, cp.ExStyle);
				return new Size (2 * edge, 2 * edge);
			}
			Padding f = XplatUIWebGpu.TopLevelFrame (cp.Style, cp.ExStyle, false, ScaleHelper.IsDeviceScaled ? dpi : ScaleHelper.OneHundredPercentLogicalDpi);
			return new Size (f.Horizontal, f.Vertical);
		}

		/// <summary>Whether a container control above the control scales it.</summary>
		internal static bool IsScaledByParent (Control control)
		{
			Control parentControl = control.Parent;
			while (parentControl != null && !(parentControl is ContainerControl))
				parentControl = parentControl.Parent;
			return parentControl is ContainerControl;
		}
	}
}
