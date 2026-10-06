// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// .NET's ProfessionalColorTable, the colours the professional tool strip renderer draws with. Mono's
// table was its own guess at them; this is .NET's, so a tool strip matches a stock one: on Windows
// 11 the theme is Aero, and Aero takes InitSystemColors -- every colour a blend of SystemColors.

#nullable enable

using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms.VisualStyles;

namespace System.Windows.Forms;

/// <summary>Provides colors used for Microsoft Office display elements.</summary>
public class ProfessionalColorTable
{
	private enum KnownColors
	{
		msocbvcrCBBdrOuterDocked = 0,
		msocbvcrCBBdrOuterFloating = 1,
		msocbvcrCBBkgd = 2,
		msocbvcrCBCtlBdrMouseDown = 3,
		msocbvcrCBCtlBdrMouseOver = 4,
		msocbvcrCBCtlBdrSelected = 5,
		msocbvcrCBCtlBdrSelectedMouseOver = 6,
		msocbvcrCBCtlBkgd = 7,
		msocbvcrCBCtlBkgdLight = 8,
		msocbvcrCBCtlBkgdMouseDown = 9,
		msocbvcrCBCtlBkgdMouseOver = 10,
		msocbvcrCBCtlBkgdSelected = 11,
		msocbvcrCBCtlBkgdSelectedMouseOver = 12,
		msocbvcrCBCtlText = 13,
		msocbvcrCBCtlTextDisabled = 14,
		msocbvcrCBCtlTextLight = 15,
		msocbvcrCBCtlTextMouseDown = 16,
		msocbvcrCBCtlTextMouseOver = 17,
		msocbvcrCBDockSeparatorLine = 18,
		msocbvcrCBDragHandle = 19,
		msocbvcrCBDragHandleShadow = 20,
		msocbvcrCBDropDownArrow = 21,
		msocbvcrCBGradMainMenuHorzBegin = 22,
		msocbvcrCBGradMainMenuHorzEnd = 23,
		msocbvcrCBGradMenuIconBkgdDroppedBegin = 24,
		msocbvcrCBGradMenuIconBkgdDroppedEnd = 25,
		msocbvcrCBGradMenuIconBkgdDroppedMiddle = 26,
		msocbvcrCBGradMenuTitleBkgdBegin = 27,
		msocbvcrCBGradMenuTitleBkgdEnd = 28,
		msocbvcrCBGradMouseDownBegin = 29,
		msocbvcrCBGradMouseDownEnd = 30,
		msocbvcrCBGradMouseDownMiddle = 31,
		msocbvcrCBGradMouseOverBegin = 32,
		msocbvcrCBGradMouseOverEnd = 33,
		msocbvcrCBGradMouseOverMiddle = 34,
		msocbvcrCBGradOptionsBegin = 35,
		msocbvcrCBGradOptionsEnd = 36,
		msocbvcrCBGradOptionsMiddle = 37,
		msocbvcrCBGradOptionsMouseOverBegin = 38,
		msocbvcrCBGradOptionsMouseOverEnd = 39,
		msocbvcrCBGradOptionsMouseOverMiddle = 40,
		msocbvcrCBGradOptionsSelectedBegin = 41,
		msocbvcrCBGradOptionsSelectedEnd = 42,
		msocbvcrCBGradOptionsSelectedMiddle = 43,
		msocbvcrCBGradSelectedBegin = 44,
		msocbvcrCBGradSelectedEnd = 45,
		msocbvcrCBGradSelectedMiddle = 46,
		msocbvcrCBGradVertBegin = 47,
		msocbvcrCBGradVertEnd = 48,
		msocbvcrCBGradVertMiddle = 49,
		msocbvcrCBIconDisabledDark = 50,
		msocbvcrCBIconDisabledLight = 51,
		msocbvcrCBLabelBkgnd = 52,
		msocbvcrCBLowColorIconDisabled = 53,
		msocbvcrCBMainMenuBkgd = 54,
		msocbvcrCBMenuBdrOuter = 55,
		msocbvcrCBMenuBkgd = 56,
		msocbvcrCBMenuCtlText = 57,
		msocbvcrCBMenuCtlTextDisabled = 58,
		msocbvcrCBMenuIconBkgd = 59,
		msocbvcrCBMenuIconBkgdDropped = 60,
		msocbvcrCBMenuShadow = 61,
		msocbvcrCBMenuSplitArrow = 62,
		msocbvcrCBOptionsButtonShadow = 63,
		msocbvcrCBShadow = 64,
		msocbvcrCBSplitterLine = 65,
		msocbvcrCBSplitterLineLight = 66,
		msocbvcrCBTearOffHandle = 67,
		msocbvcrCBTearOffHandleMouseOver = 68,
		msocbvcrCBTitleBkgd = 69,
		msocbvcrCBTitleText = 70,
		msocbvcrDisabledFocuslessHighlightedText = 71,
		msocbvcrDisabledHighlightedText = 72,
		msocbvcrDlgGroupBoxText = 73,
		msocbvcrDocTabBdr = 74,
		msocbvcrDocTabBdrDark = 75,
		msocbvcrDocTabBdrDarkMouseDown = 76,
		msocbvcrDocTabBdrDarkMouseOver = 77,
		msocbvcrDocTabBdrLight = 78,
		msocbvcrDocTabBdrLightMouseDown = 79,
		msocbvcrDocTabBdrLightMouseOver = 80,
		msocbvcrDocTabBdrMouseDown = 81,
		msocbvcrDocTabBdrMouseOver = 82,
		msocbvcrDocTabBdrSelected = 83,
		msocbvcrDocTabBkgd = 84,
		msocbvcrDocTabBkgdMouseDown = 85,
		msocbvcrDocTabBkgdMouseOver = 86,
		msocbvcrDocTabBkgdSelected = 87,
		msocbvcrDocTabText = 88,
		msocbvcrDocTabTextMouseDown = 89,
		msocbvcrDocTabTextMouseOver = 90,
		msocbvcrDocTabTextSelected = 91,
		msocbvcrDWActiveTabBkgd = 92,
		msocbvcrDWActiveTabText = 93,
		msocbvcrDWActiveTabTextDisabled = 94,
		msocbvcrDWInactiveTabBkgd = 95,
		msocbvcrDWInactiveTabText = 96,
		msocbvcrDWTabBkgdMouseDown = 97,
		msocbvcrDWTabBkgdMouseOver = 98,
		msocbvcrDWTabTextMouseDown = 99,
		msocbvcrDWTabTextMouseOver = 100,
		msocbvcrFocuslessHighlightedBkgd = 101,
		msocbvcrFocuslessHighlightedText = 102,
		msocbvcrGDHeaderBdr = 103,
		msocbvcrGDHeaderBkgd = 104,
		msocbvcrGDHeaderCellBdr = 105,
		msocbvcrGDHeaderCellBkgd = 106,
		msocbvcrGDHeaderCellBkgdSelected = 107,
		msocbvcrGDHeaderSeeThroughSelection = 108,
		msocbvcrGSPDarkBkgd = 109,
		msocbvcrGSPGroupContentDarkBkgd = 110,
		msocbvcrGSPGroupContentLightBkgd = 111,
		msocbvcrGSPGroupContentText = 112,
		msocbvcrGSPGroupContentTextDisabled = 113,
		msocbvcrGSPGroupHeaderDarkBkgd = 114,
		msocbvcrGSPGroupHeaderLightBkgd = 115,
		msocbvcrGSPGroupHeaderText = 116,
		msocbvcrGSPGroupline = 117,
		msocbvcrGSPHyperlink = 118,
		msocbvcrGSPLightBkgd = 119,
		msocbvcrHyperlink = 120,
		msocbvcrHyperlinkFollowed = 121,
		msocbvcrJotNavUIBdr = 122,
		msocbvcrJotNavUIGradBegin = 123,
		msocbvcrJotNavUIGradEnd = 124,
		msocbvcrJotNavUIGradMiddle = 125,
		msocbvcrJotNavUIText = 126,
		msocbvcrListHeaderArrow = 127,
		msocbvcrNetLookBkgnd = 128,
		msocbvcrOABBkgd = 129,
		msocbvcrOBBkgdBdr = 130,
		msocbvcrOBBkgdBdrContrast = 131,
		msocbvcrOGMDIParentWorkspaceBkgd = 132,
		msocbvcrOGRulerActiveBkgd = 133,
		msocbvcrOGRulerBdr = 134,
		msocbvcrOGRulerBkgd = 135,
		msocbvcrOGRulerInactiveBkgd = 136,
		msocbvcrOGRulerTabBoxBdr = 137,
		msocbvcrOGRulerTabBoxBdrHighlight = 138,
		msocbvcrOGRulerTabStopTicks = 139,
		msocbvcrOGRulerText = 140,
		msocbvcrOGTaskPaneGroupBoxHeaderBkgd = 141,
		msocbvcrOGWorkspaceBkgd = 142,
		msocbvcrOLKFlagNone = 143,
		msocbvcrOLKFolderbarDark = 144,
		msocbvcrOLKFolderbarLight = 145,
		msocbvcrOLKFolderbarText = 146,
		msocbvcrOLKGridlines = 147,
		msocbvcrOLKGroupLine = 148,
		msocbvcrOLKGroupNested = 149,
		msocbvcrOLKGroupShaded = 150,
		msocbvcrOLKGroupText = 151,
		msocbvcrOLKIconBar = 152,
		msocbvcrOLKInfoBarBkgd = 153,
		msocbvcrOLKInfoBarText = 154,
		msocbvcrOLKPreviewPaneLabelText = 155,
		msocbvcrOLKTodayIndicatorDark = 156,
		msocbvcrOLKTodayIndicatorLight = 157,
		msocbvcrOLKWBActionDividerLine = 158,
		msocbvcrOLKWBButtonDark = 159,
		msocbvcrOLKWBButtonLight = 160,
		msocbvcrOLKWBDarkOutline = 161,
		msocbvcrOLKWBFoldersBackground = 162,
		msocbvcrOLKWBHoverButtonDark = 163,
		msocbvcrOLKWBHoverButtonLight = 164,
		msocbvcrOLKWBLabelText = 165,
		msocbvcrOLKWBPressedButtonDark = 166,
		msocbvcrOLKWBPressedButtonLight = 167,
		msocbvcrOLKWBSelectedButtonDark = 168,
		msocbvcrOLKWBSelectedButtonLight = 169,
		msocbvcrOLKWBSplitterDark = 170,
		msocbvcrOLKWBSplitterLight = 171,
		msocbvcrPlacesBarBkgd = 172,
		msocbvcrPPOutlineThumbnailsPaneTabAreaBkgd = 173,
		msocbvcrPPOutlineThumbnailsPaneTabBdr = 174,
		msocbvcrPPOutlineThumbnailsPaneTabInactiveBkgd = 175,
		msocbvcrPPOutlineThumbnailsPaneTabText = 176,
		msocbvcrPPSlideBdrActiveSelected = 177,
		msocbvcrPPSlideBdrActiveSelectedMouseOver = 178,
		msocbvcrPPSlideBdrInactiveSelected = 179,
		msocbvcrPPSlideBdrMouseOver = 180,
		msocbvcrPubPrintDocScratchPageBkgd = 181,
		msocbvcrPubWebDocScratchPageBkgd = 182,
		msocbvcrSBBdr = 183,
		msocbvcrScrollbarBkgd = 184,
		msocbvcrToastGradBegin = 185,
		msocbvcrToastGradEnd = 186,
		msocbvcrWPBdrInnerDocked = 187,
		msocbvcrWPBdrOuterDocked = 188,
		msocbvcrWPBdrOuterFloating = 189,
		msocbvcrWPBkgd = 190,
		msocbvcrWPCtlBdr = 191,
		msocbvcrWPCtlBdrDefault = 192,
		msocbvcrWPCtlBdrDisabled = 193,
		msocbvcrWPCtlBkgd = 194,
		msocbvcrWPCtlBkgdDisabled = 195,
		msocbvcrWPCtlText = 196,
		msocbvcrWPCtlTextDisabled = 197,
		msocbvcrWPCtlTextMouseDown = 198,
		msocbvcrWPGroupline = 199,
		msocbvcrWPInfoTipBkgd = 200,
		msocbvcrWPInfoTipText = 201,
		msocbvcrWPNavBarBkgnd = 202,
		msocbvcrWPText = 203,
		msocbvcrWPTextDisabled = 204,
		msocbvcrWPTitleBkgdActive = 205,
		msocbvcrWPTitleBkgdInactive = 206,
		msocbvcrWPTitleTextActive = 207,
		msocbvcrWPTitleTextInactive = 208,
		msocbvcrXLFormulaBarBkgd = 209,
		ButtonSelectedHighlight = 210,
		ButtonPressedHighlight = 211,
		ButtonCheckedHighlight = 212,
		lastKnownColor = ButtonCheckedHighlight
	}

	private Dictionary<KnownColors, Color>? _professionalRGB;

	private bool _usingSystemColors;

	private bool _useSystemColors;

	// DisplayInformation.LowResolution: a display of 8 bits per pixel or fewer. None the port draws on.
	private static bool LowResolution => false;

	private Dictionary<KnownColors, Color> ColorTable
	{
		get
		{
			if (UseSystemColors)
			{
				if (!_usingSystemColors || _professionalRGB == null)
				{
					if (_professionalRGB == null)
					{
						_professionalRGB = new Dictionary<KnownColors, Color>(212);
					}
					InitSystemColors(ref _professionalRGB);
				}
			}
			else if (ToolStripManager.VisualStylesEnabled)
			{
				if (_usingSystemColors || _professionalRGB == null)
				{
					if (_professionalRGB == null)
					{
						_professionalRGB = new Dictionary<KnownColors, Color>(212);
					}
					InitThemedColors(ref _professionalRGB);
				}
			}
			else if (!_usingSystemColors || _professionalRGB == null)
			{
				if (_professionalRGB == null)
				{
					_professionalRGB = new Dictionary<KnownColors, Color>(212);
				}
				InitSystemColors(ref _professionalRGB);
			}
			return _professionalRGB;
		}
	}

	/// <summary>Gets or sets a value indicating whether to use <see cref="T:System.Drawing.SystemColors" /> rather than colors that match the current visual style.</summary>
	/// <returns>
	///   <see langword="true" /> to use <see cref="T:System.Drawing.SystemColors" />; otherwise, <see langword="false" />. The default is <see langword="false" />.</returns>
	public bool UseSystemColors
	{
		get
		{
			return _useSystemColors;
		}
		set
		{
			if (_useSystemColors != value)
			{
				_useSystemColors = value;
				ResetRGBTable();
			}
		}
	}

	/// <summary>Gets the solid color used when the button is selected.</summary>
	/// <returns>A <see cref="T:System.Drawing.Color" /> that is the solid color used when the button is selected.</returns>
	public virtual Color ButtonSelectedHighlight => FromKnownColor(KnownColors.ButtonSelectedHighlight);

	/// <summary>Gets the border color to use with <see cref="P:System.Windows.Forms.ProfessionalColorTable.ButtonSelectedHighlight" />.</summary>
	/// <returns>A <see cref="T:System.Drawing.Color" /> that is the border color to use with <see cref="P:System.Windows.Forms.ProfessionalColorTable.ButtonSelectedHighlight" />.</returns>
	public virtual Color ButtonSelectedHighlightBorder => ButtonPressedBorder;

	/// <summary>Gets the solid color used when the button is pressed.</summary>
	/// <returns>A <see cref="T:System.Drawing.Color" /> that is the solid color used when the button is pressed.</returns>
	public virtual Color ButtonPressedHighlight => FromKnownColor(KnownColors.ButtonPressedHighlight);

	/// <summary>Gets the border color to use with <see cref="P:System.Windows.Forms.ProfessionalColorTable.ButtonPressedHighlight" />.</summary>
	/// <returns>A <see cref="T:System.Drawing.Color" /> that is the border color to use with <see cref="P:System.Windows.Forms.ProfessionalColorTable.ButtonPressedHighlight" />.</returns>
	public virtual Color ButtonPressedHighlightBorder => SystemColors.Highlight;

	/// <summary>Gets the solid color used when the button is checked.</summary>
	/// <returns>A <see cref="T:System.Drawing.Color" /> that is the solid color used when the button is checked.</returns>
	public virtual Color ButtonCheckedHighlight => FromKnownColor(KnownColors.ButtonCheckedHighlight);

	/// <summary>Gets the border color to use with <see cref="P:System.Windows.Forms.ProfessionalColorTable.ButtonCheckedHighlight" />.</summary>
	/// <returns>A <see cref="T:System.Drawing.Color" /> that is the border color to use with <see cref="P:System.Windows.Forms.ProfessionalColorTable.ButtonCheckedHighlight" />.</returns>
	public virtual Color ButtonCheckedHighlightBorder => SystemColors.Highlight;

	/// <summary>Gets the border color to use with the <see cref="P:System.Windows.Forms.ProfessionalColorTable.ButtonPressedGradientBegin" />, <see cref="P:System.Windows.Forms.ProfessionalColorTable.ButtonPressedGradientMiddle" />, and <see cref="P:System.Windows.Forms.ProfessionalColorTable.ButtonPressedGradientEnd" /> colors.</summary>
	/// <returns>A <see cref="T:System.Drawing.Color" /> that is the border color to use with the <see cref="P:System.Windows.Forms.ProfessionalColorTable.ButtonPressedGradientBegin" />, <see cref="P:System.Windows.Forms.ProfessionalColorTable.ButtonPressedGradientMiddle" />, and <see cref="P:System.Windows.Forms.ProfessionalColorTable.ButtonPressedGradientEnd" /> colors.</returns>
	public virtual Color ButtonPressedBorder => FromKnownColor(KnownColors.msocbvcrCBCtlBdrMouseOver);

	/// <summary>Gets the border color to use with the <see cref="P:System.Windows.Forms.ProfessionalColorTable.ButtonSelectedGradientBegin" />, <see cref="P:System.Windows.Forms.ProfessionalColorTable.ButtonSelectedGradientMiddle" />, and <see cref="P:System.Windows.Forms.ProfessionalColorTable.ButtonSelectedGradientEnd" /> colors.</summary>
	/// <returns>A <see cref="T:System.Drawing.Color" /> that is the border color to use with the <see cref="P:System.Windows.Forms.ProfessionalColorTable.ButtonSelectedGradientBegin" />, <see cref="P:System.Windows.Forms.ProfessionalColorTable.ButtonSelectedGradientMiddle" />, and <see cref="P:System.Windows.Forms.ProfessionalColorTable.ButtonSelectedGradientEnd" /> colors.</returns>
	public virtual Color ButtonSelectedBorder => FromKnownColor(KnownColors.msocbvcrCBCtlBdrMouseOver);

	/// <summary>Gets the starting color of the gradient used when the button is checked.</summary>
	/// <returns>A <see cref="T:System.Drawing.Color" /> that is the starting color of the gradient used when the button is checked.</returns>
	public virtual Color ButtonCheckedGradientBegin => FromKnownColor(KnownColors.msocbvcrCBGradSelectedBegin);

	/// <summary>Gets the middle color of the gradient used when the button is checked.</summary>
	/// <returns>A <see cref="T:System.Drawing.Color" /> that is the middle color of the gradient used when the button is checked.</returns>
	public virtual Color ButtonCheckedGradientMiddle => FromKnownColor(KnownColors.msocbvcrCBGradSelectedMiddle);

	/// <summary>Gets the end color of the gradient used when the button is checked.</summary>
	/// <returns>A <see cref="T:System.Drawing.Color" /> that is the end color of the gradient used when the button is checked.</returns>
	public virtual Color ButtonCheckedGradientEnd => FromKnownColor(KnownColors.msocbvcrCBGradSelectedEnd);

	/// <summary>Gets the starting color of the gradient used when the button is selected.</summary>
	/// <returns>A <see cref="T:System.Drawing.Color" /> that is the starting color of the gradient used when the button is selected.</returns>
	public virtual Color ButtonSelectedGradientBegin => FromKnownColor(KnownColors.msocbvcrCBGradMouseOverBegin);

	/// <summary>Gets the middle color of the gradient used when the button is selected.</summary>
	/// <returns>A <see cref="T:System.Drawing.Color" /> that is the middle color of the gradient used when the button is selected.</returns>
	public virtual Color ButtonSelectedGradientMiddle => FromKnownColor(KnownColors.msocbvcrCBGradMouseOverMiddle);

	/// <summary>Gets the end color of the gradient used when the button is selected.</summary>
	/// <returns>A <see cref="T:System.Drawing.Color" /> that is the end color of the gradient used when the button is selected.</returns>
	public virtual Color ButtonSelectedGradientEnd => FromKnownColor(KnownColors.msocbvcrCBGradMouseOverEnd);

	/// <summary>Gets the starting color of the gradient used when the button is pressed.</summary>
	/// <returns>A <see cref="T:System.Drawing.Color" /> that is the starting color of the gradient used when the button is pressed.</returns>
	public virtual Color ButtonPressedGradientBegin => FromKnownColor(KnownColors.msocbvcrCBGradMouseDownBegin);

	/// <summary>Gets the middle color of the gradient used when the button is pressed.</summary>
	/// <returns>A <see cref="T:System.Drawing.Color" /> that is the middle color of the gradient used when the button is pressed.</returns>
	public virtual Color ButtonPressedGradientMiddle => FromKnownColor(KnownColors.msocbvcrCBGradMouseDownMiddle);

	/// <summary>Gets the end color of the gradient used when the button is pressed.</summary>
	/// <returns>A <see cref="T:System.Drawing.Color" /> that is the end color of the gradient used when the button is pressed.</returns>
	public virtual Color ButtonPressedGradientEnd => FromKnownColor(KnownColors.msocbvcrCBGradMouseDownEnd);

	/// <summary>Gets the solid color to use when the button is checked and gradients are being used.</summary>
	/// <returns>A <see cref="T:System.Drawing.Color" /> that is the solid color to use when the button is checked and gradients are being used.</returns>
	public virtual Color CheckBackground => FromKnownColor(KnownColors.msocbvcrCBCtlBkgdSelected);

	/// <summary>Gets the solid color to use when the button is checked and selected and gradients are being used.</summary>
	/// <returns>A <see cref="T:System.Drawing.Color" /> that is the solid color to use when the button is checked and selected and gradients are being used.</returns>
	public virtual Color CheckSelectedBackground => FromKnownColor(KnownColors.msocbvcrCBCtlBkgdSelectedMouseOver);

	/// <summary>Gets the solid color to use when the button is checked and selected and gradients are being used.</summary>
	/// <returns>A <see cref="T:System.Drawing.Color" /> that is the solid color to use when the button is checked and selected and gradients are being used.</returns>
	public virtual Color CheckPressedBackground => FromKnownColor(KnownColors.msocbvcrCBCtlBkgdSelectedMouseOver);

	/// <summary>Gets the color to use for shadow effects on the grip (move handle).</summary>
	/// <returns>A <see cref="T:System.Drawing.Color" /> that is the color to use for shadow effects on the grip (move handle).</returns>
	public virtual Color GripDark => FromKnownColor(KnownColors.msocbvcrCBDragHandle);

	/// <summary>Gets the color to use for highlight effects on the grip (move handle).</summary>
	/// <returns>A <see cref="T:System.Drawing.Color" /> that is the color to use for highlight effects on the grip (move handle).</returns>
	public virtual Color GripLight => FromKnownColor(KnownColors.msocbvcrCBDragHandleShadow);

	/// <summary>Gets the starting color of the gradient used in the image margin of a <see cref="T:System.Windows.Forms.ToolStripDropDownMenu" />.</summary>
	/// <returns>A <see cref="T:System.Drawing.Color" /> that is the starting color of the gradient used in the image margin of a <see cref="T:System.Windows.Forms.ToolStripDropDownMenu" />.</returns>
	public virtual Color ImageMarginGradientBegin => FromKnownColor(KnownColors.msocbvcrCBGradVertBegin);

	/// <summary>Gets the middle color of the gradient used in the image margin of a <see cref="T:System.Windows.Forms.ToolStripDropDownMenu" />.</summary>
	/// <returns>A <see cref="T:System.Drawing.Color" /> that is the middle color of the gradient used in the image margin of a <see cref="T:System.Windows.Forms.ToolStripDropDownMenu" />.</returns>
	public virtual Color ImageMarginGradientMiddle => FromKnownColor(KnownColors.msocbvcrCBGradVertMiddle);

	/// <summary>Gets the end color of the gradient used in the image margin of a <see cref="T:System.Windows.Forms.ToolStripDropDownMenu" />.</summary>
	/// <returns>A <see cref="T:System.Drawing.Color" /> that is the end color of the gradient used in the image margin of a <see cref="T:System.Windows.Forms.ToolStripDropDownMenu" />.</returns>
	public virtual Color ImageMarginGradientEnd
	{
		get
		{
			if (!_usingSystemColors)
			{
				return FromKnownColor(KnownColors.msocbvcrCBGradVertEnd);
			}
			return SystemColors.Control;
		}
	}

	/// <summary>Gets the starting color of the gradient used in the image margin of a <see cref="T:System.Windows.Forms.ToolStripDropDownMenu" /> when an item is revealed.</summary>
	/// <returns>A <see cref="T:System.Drawing.Color" /> that is the starting color of the gradient used in the image margin of a <see cref="T:System.Windows.Forms.ToolStripDropDownMenu" /> when an item is revealed.</returns>
	public virtual Color ImageMarginRevealedGradientBegin => FromKnownColor(KnownColors.msocbvcrCBGradMenuIconBkgdDroppedBegin);

	/// <summary>Gets the middle color of the gradient used in the image margin of a <see cref="T:System.Windows.Forms.ToolStripDropDownMenu" /> when an item is revealed.</summary>
	/// <returns>A <see cref="T:System.Drawing.Color" /> that is the middle color of the gradient used in the image margin of a <see cref="T:System.Windows.Forms.ToolStripDropDownMenu" /> when an item is revealed.</returns>
	public virtual Color ImageMarginRevealedGradientMiddle => FromKnownColor(KnownColors.msocbvcrCBGradMenuIconBkgdDroppedMiddle);

	/// <summary>Gets the end color of the gradient used in the image margin of a <see cref="T:System.Windows.Forms.ToolStripDropDownMenu" /> when an item is revealed.</summary>
	/// <returns>A <see cref="T:System.Drawing.Color" /> that is the end color of the gradient used in the image margin of a <see cref="T:System.Windows.Forms.ToolStripDropDownMenu" /> when an item is revealed.</returns>
	public virtual Color ImageMarginRevealedGradientEnd => FromKnownColor(KnownColors.msocbvcrCBGradMenuIconBkgdDroppedEnd);

	/// <summary>Gets the starting color of the gradient used in the <see cref="T:System.Windows.Forms.MenuStrip" />.</summary>
	/// <returns>A <see cref="T:System.Drawing.Color" /> that is the starting color of the gradient used in the <see cref="T:System.Windows.Forms.MenuStrip" />.</returns>
	public virtual Color MenuStripGradientBegin => FromKnownColor(KnownColors.msocbvcrCBGradMainMenuHorzBegin);

	/// <summary>Gets the end color of the gradient used in the <see cref="T:System.Windows.Forms.MenuStrip" />.</summary>
	/// <returns>A <see cref="T:System.Drawing.Color" /> that is the end color of the gradient used in the <see cref="T:System.Windows.Forms.MenuStrip" />.</returns>
	public virtual Color MenuStripGradientEnd => FromKnownColor(KnownColors.msocbvcrCBGradMainMenuHorzEnd);

	/// <summary>Gets the solid color to use when a <see cref="T:System.Windows.Forms.ToolStripMenuItem" /> other than the top-level <see cref="T:System.Windows.Forms.ToolStripMenuItem" /> is selected.</summary>
	/// <returns>A <see cref="T:System.Drawing.Color" /> that is the solid color to use when a <see cref="T:System.Windows.Forms.ToolStripMenuItem" /> other than the top-level <see cref="T:System.Windows.Forms.ToolStripMenuItem" /> is selected.</returns>
	public virtual Color MenuItemSelected => FromKnownColor(KnownColors.msocbvcrCBCtlBkgdMouseOver);

	/// <summary>Gets the border color to use with a <see cref="T:System.Windows.Forms.ToolStripMenuItem" />.</summary>
	/// <returns>A <see cref="T:System.Drawing.Color" /> that is the border color to use with a <see cref="T:System.Windows.Forms.ToolStripMenuItem" />.</returns>
	public virtual Color MenuItemBorder => FromKnownColor(KnownColors.msocbvcrCBCtlBdrSelected);

	/// <summary>Gets the color that is the border color to use on a <see cref="T:System.Windows.Forms.MenuStrip" />.</summary>
	/// <returns>A <see cref="T:System.Drawing.Color" /> that is the border color to use on a <see cref="T:System.Windows.Forms.MenuStrip" />.</returns>
	public virtual Color MenuBorder => FromKnownColor(KnownColors.msocbvcrCBMenuBdrOuter);

	/// <summary>Gets the starting color of the gradient used when the <see cref="T:System.Windows.Forms.ToolStripMenuItem" /> is selected.</summary>
	/// <returns>A <see cref="T:System.Drawing.Color" /> that is the starting color of the gradient used when the <see cref="T:System.Windows.Forms.ToolStripMenuItem" /> is selected.</returns>
	public virtual Color MenuItemSelectedGradientBegin => FromKnownColor(KnownColors.msocbvcrCBGradMouseOverBegin);

	/// <summary>Gets the end color of the gradient used when the <see cref="T:System.Windows.Forms.ToolStripMenuItem" /> is selected.</summary>
	/// <returns>A <see cref="T:System.Drawing.Color" /> that is the end color of the gradient used when the <see cref="T:System.Windows.Forms.ToolStripMenuItem" /> is selected.</returns>
	public virtual Color MenuItemSelectedGradientEnd => FromKnownColor(KnownColors.msocbvcrCBGradMouseOverEnd);

	/// <summary>Gets the starting color of the gradient used when a top-level <see cref="T:System.Windows.Forms.ToolStripMenuItem" /> is pressed.</summary>
	/// <returns>A <see cref="T:System.Drawing.Color" /> that is the starting color of the gradient used when a top-level <see cref="T:System.Windows.Forms.ToolStripMenuItem" /> is pressed.</returns>
	public virtual Color MenuItemPressedGradientBegin => FromKnownColor(KnownColors.msocbvcrCBGradMenuTitleBkgdBegin);

	/// <summary>Gets the middle color of the gradient used when a top-level <see cref="T:System.Windows.Forms.ToolStripMenuItem" /> is pressed.</summary>
	/// <returns>A <see cref="T:System.Drawing.Color" /> that is the middle color of the gradient used when a top-level <see cref="T:System.Windows.Forms.ToolStripMenuItem" /> is pressed.</returns>
	public virtual Color MenuItemPressedGradientMiddle => FromKnownColor(KnownColors.msocbvcrCBGradMenuIconBkgdDroppedMiddle);

	/// <summary>Gets the end color of the gradient used when a top-level <see cref="T:System.Windows.Forms.ToolStripMenuItem" /> is pressed.</summary>
	/// <returns>A <see cref="T:System.Drawing.Color" /> that is the end color of the gradient used when a top-level <see cref="T:System.Windows.Forms.ToolStripMenuItem" /> is pressed.</returns>
	public virtual Color MenuItemPressedGradientEnd => FromKnownColor(KnownColors.msocbvcrCBGradMenuTitleBkgdEnd);

	/// <summary>Gets the starting color of the gradient used in the <see cref="T:System.Windows.Forms.ToolStripContainer" />.</summary>
	/// <returns>A <see cref="T:System.Drawing.Color" /> that is the starting color of the gradient used in the <see cref="T:System.Windows.Forms.ToolStripContainer" />.</returns>
	public virtual Color RaftingContainerGradientBegin => FromKnownColor(KnownColors.msocbvcrCBGradMainMenuHorzBegin);

	/// <summary>Gets the end color of the gradient used in the <see cref="T:System.Windows.Forms.ToolStripContainer" />.</summary>
	/// <returns>A <see cref="T:System.Drawing.Color" /> that is the end color of the gradient used in the <see cref="T:System.Windows.Forms.ToolStripContainer" />.</returns>
	public virtual Color RaftingContainerGradientEnd => FromKnownColor(KnownColors.msocbvcrCBGradMainMenuHorzEnd);

	/// <summary>Gets the color to use to for shadow effects on the <see cref="T:System.Windows.Forms.ToolStripSeparator" />.</summary>
	/// <returns>A <see cref="T:System.Drawing.Color" /> that is the color to use to for shadow effects on the <see cref="T:System.Windows.Forms.ToolStripSeparator" />.</returns>
	public virtual Color SeparatorDark => FromKnownColor(KnownColors.msocbvcrCBSplitterLine);

	/// <summary>Gets the color to use to for highlight effects on the <see cref="T:System.Windows.Forms.ToolStripSeparator" />.</summary>
	/// <returns>A <see cref="T:System.Drawing.Color" /> that is the color to use to for highlight effects on the <see cref="T:System.Windows.Forms.ToolStripSeparator" />.</returns>
	public virtual Color SeparatorLight => FromKnownColor(KnownColors.msocbvcrCBSplitterLineLight);

	public virtual Color StatusStripBorder => SystemColors.ButtonHighlight;

	/// <summary>Gets the starting color of the gradient used on the <see cref="T:System.Windows.Forms.StatusStrip" />.</summary>
	/// <returns>A <see cref="T:System.Drawing.Color" /> that is the starting color of the gradient used on the <see cref="T:System.Windows.Forms.StatusStrip" />.</returns>
	public virtual Color StatusStripGradientBegin => FromKnownColor(KnownColors.msocbvcrCBGradMainMenuHorzBegin);

	/// <summary>Gets the end color of the gradient used on the <see cref="T:System.Windows.Forms.StatusStrip" />.</summary>
	/// <returns>A <see cref="T:System.Drawing.Color" /> that is the end color of the gradient used on the <see cref="T:System.Windows.Forms.StatusStrip" />.</returns>
	public virtual Color StatusStripGradientEnd => FromKnownColor(KnownColors.msocbvcrCBGradMainMenuHorzEnd);

	/// <summary>Gets the border color to use on the bottom edge of the <see cref="T:System.Windows.Forms.ToolStrip" />.</summary>
	/// <returns>A <see cref="T:System.Drawing.Color" /> that is the border color to use on the bottom edge of the <see cref="T:System.Windows.Forms.ToolStrip" />.</returns>
	public virtual Color ToolStripBorder => FromKnownColor(KnownColors.msocbvcrCBShadow);

	/// <summary>Gets the solid background color of the <see cref="T:System.Windows.Forms.ToolStripDropDown" />.</summary>
	/// <returns>A <see cref="T:System.Drawing.Color" /> that is the solid background color of the <see cref="T:System.Windows.Forms.ToolStripDropDown" />.</returns>
	public virtual Color ToolStripDropDownBackground => FromKnownColor(KnownColors.msocbvcrCBMenuBkgd);

	/// <summary>Gets the starting color of the gradient used in the <see cref="T:System.Windows.Forms.ToolStrip" /> background.</summary>
	/// <returns>A <see cref="T:System.Drawing.Color" /> that is the starting color of the gradient used in the <see cref="T:System.Windows.Forms.ToolStrip" /> background.</returns>
	public virtual Color ToolStripGradientBegin => FromKnownColor(KnownColors.msocbvcrCBGradVertBegin);

	/// <summary>Gets the middle color of the gradient used in the <see cref="T:System.Windows.Forms.ToolStrip" /> background.</summary>
	/// <returns>A <see cref="T:System.Drawing.Color" /> that is the middle color of the gradient used in the <see cref="T:System.Windows.Forms.ToolStrip" /> background.</returns>
	public virtual Color ToolStripGradientMiddle => FromKnownColor(KnownColors.msocbvcrCBGradVertMiddle);

	/// <summary>Gets the end color of the gradient used in the <see cref="T:System.Windows.Forms.ToolStrip" /> background.</summary>
	/// <returns>A <see cref="T:System.Drawing.Color" /> that is the end color of the gradient used in the <see cref="T:System.Windows.Forms.ToolStrip" /> background.</returns>
	public virtual Color ToolStripGradientEnd => FromKnownColor(KnownColors.msocbvcrCBGradVertEnd);

	/// <summary>Gets the starting color of the gradient used in the <see cref="T:System.Windows.Forms.ToolStripContentPanel" />.</summary>
	/// <returns>A <see cref="T:System.Drawing.Color" /> that is the starting color of the gradient used in the <see cref="T:System.Windows.Forms.ToolStripContentPanel" />.</returns>
	public virtual Color ToolStripContentPanelGradientBegin => FromKnownColor(KnownColors.msocbvcrCBGradMainMenuHorzBegin);

	/// <summary>Gets the end color of the gradient used in the <see cref="T:System.Windows.Forms.ToolStripContentPanel" />.</summary>
	/// <returns>A <see cref="T:System.Drawing.Color" /> that is the end color of the gradient used in the <see cref="T:System.Windows.Forms.ToolStripContentPanel" />.</returns>
	public virtual Color ToolStripContentPanelGradientEnd => FromKnownColor(KnownColors.msocbvcrCBGradMainMenuHorzEnd);

	/// <summary>Gets the starting color of the gradient used in the <see cref="T:System.Windows.Forms.ToolStripPanel" />.</summary>
	/// <returns>A <see cref="T:System.Drawing.Color" /> that is the starting color of the gradient used in the <see cref="T:System.Windows.Forms.ToolStripPanel" />.</returns>
	public virtual Color ToolStripPanelGradientBegin => FromKnownColor(KnownColors.msocbvcrCBGradMainMenuHorzBegin);

	/// <summary>Gets the end color of the gradient used in the <see cref="T:System.Windows.Forms.ToolStripPanel" />.</summary>
	/// <returns>A <see cref="T:System.Drawing.Color" /> that is the end color of the gradient used in the <see cref="T:System.Windows.Forms.ToolStripPanel" />.</returns>
	public virtual Color ToolStripPanelGradientEnd => FromKnownColor(KnownColors.msocbvcrCBGradMainMenuHorzEnd);

	/// <summary>Gets the starting color of the gradient used in the <see cref="T:System.Windows.Forms.ToolStripOverflowButton" />.</summary>
	/// <returns>A <see cref="T:System.Drawing.Color" /> that is the starting color of the gradient used in the <see cref="T:System.Windows.Forms.ToolStripOverflowButton" />.</returns>
	public virtual Color OverflowButtonGradientBegin => FromKnownColor(KnownColors.msocbvcrCBGradOptionsBegin);

	/// <summary>Gets the middle color of the gradient used in the <see cref="T:System.Windows.Forms.ToolStripOverflowButton" />.</summary>
	/// <returns>A <see cref="T:System.Drawing.Color" /> that is the middle color of the gradient used in the <see cref="T:System.Windows.Forms.ToolStripOverflowButton" />.</returns>
	public virtual Color OverflowButtonGradientMiddle => FromKnownColor(KnownColors.msocbvcrCBGradOptionsMiddle);

	/// <summary>Gets the end color of the gradient used in the <see cref="T:System.Windows.Forms.ToolStripOverflowButton" />.</summary>
	/// <returns>A <see cref="T:System.Drawing.Color" /> that is the end color of the gradient used in the <see cref="T:System.Windows.Forms.ToolStripOverflowButton" />.</returns>
	public virtual Color OverflowButtonGradientEnd => FromKnownColor(KnownColors.msocbvcrCBGradOptionsEnd);

	internal Color ComboBoxButtonGradientBegin => MenuItemPressedGradientBegin;

	internal Color ComboBoxButtonGradientEnd => MenuItemPressedGradientEnd;

	internal Color ComboBoxButtonSelectedGradientBegin => MenuItemSelectedGradientBegin;

	internal Color ComboBoxButtonSelectedGradientEnd => MenuItemSelectedGradientEnd;

	internal Color ComboBoxButtonPressedGradientBegin => ButtonPressedGradientBegin;

	internal Color ComboBoxButtonPressedGradientEnd => ButtonPressedGradientEnd;

	internal Color ComboBoxButtonOnOverflow => ToolStripDropDownBackground;

	internal Color ComboBoxBorder => ButtonSelectedHighlightBorder;

	internal Color TextBoxBorder => ButtonSelectedHighlightBorder;

	/// <summary>Initializes a new instance of the <see cref="T:System.Windows.Forms.ProfessionalColorTable" /> class.</summary>
	public ProfessionalColorTable()
	{
	}

	private Color FromKnownColor(KnownColors color)
	{
		return ColorTable[color];
	}

	private void ResetRGBTable()
	{
		_professionalRGB?.Clear();
		_professionalRGB = null;
	}

	private static Color GetAlphaBlendedColor(Graphics g, Color src, Color dest, int alpha)
	{
		int red = (src.R * alpha + (255 - alpha) * dest.R) / 255;
		int green = (src.G * alpha + (255 - alpha) * dest.G) / 255;
		int blue = (src.B * alpha + (255 - alpha) * dest.B) / 255;
		int alpha2 = (src.A * alpha + (255 - alpha) * dest.A) / 255;
		// .NET asks the screen DC for the nearest colour, which on a true-colour display is the colour.
		return Color.FromArgb(alpha2, red, green, blue);
	}

	private static Color GetAlphaBlendedColorHighRes(Graphics? graphics, Color src, Color dest, int alpha)
	{
		int num;
		int num2;
		if (alpha < 100)
		{
			num = 100 - alpha;
			num2 = 100;
		}
		else
		{
			num = 1000 - alpha;
			num2 = 1000;
		}
		int red = (alpha * src.R + num * dest.R + num2 / 2) / num2;
		int green = (alpha * src.G + num * dest.G + num2 / 2) / num2;
		int blue = (alpha * src.B + num * dest.B + num2 / 2) / num2;
		return Color.FromArgb(red, green, blue);
	}

	private static void InitCommonColors(ref Dictionary<KnownColors, Color> rgbTable)
	{
		if (!LowResolution)
		{
			{
				Graphics? scope = null;
				rgbTable[KnownColors.ButtonPressedHighlight] = GetAlphaBlendedColor(scope, SystemColors.Window, GetAlphaBlendedColor(scope, SystemColors.Highlight, SystemColors.Window, 160), 50);
				rgbTable[KnownColors.ButtonCheckedHighlight] = GetAlphaBlendedColor(scope, SystemColors.Window, GetAlphaBlendedColor(scope, SystemColors.Highlight, SystemColors.Window, 80), 20);
				rgbTable[KnownColors.ButtonSelectedHighlight] = rgbTable[KnownColors.ButtonCheckedHighlight];
				return;
			}
		}
		rgbTable[KnownColors.ButtonPressedHighlight] = SystemColors.Highlight;
		rgbTable[KnownColors.ButtonCheckedHighlight] = SystemColors.ControlLight;
		rgbTable[KnownColors.ButtonSelectedHighlight] = SystemColors.ControlLight;
	}

	private void InitSystemColors(ref Dictionary<KnownColors, Color> rgbTable)
	{
		_usingSystemColors = true;
		InitCommonColors(ref rgbTable);
		Color buttonFace = SystemColors.ButtonFace;
		Color buttonShadow = SystemColors.ButtonShadow;
		Color highlight = SystemColors.Highlight;
		Color window = SystemColors.Window;
		Color empty = Color.Empty;
		Color controlText = SystemColors.ControlText;
		Color buttonHighlight = SystemColors.ButtonHighlight;
		Color grayText = SystemColors.GrayText;
		Color highlightText = SystemColors.HighlightText;
		Color windowText = SystemColors.WindowText;
		Color value = buttonFace;
		Color value2 = buttonFace;
		Color value3 = buttonFace;
		Color value4 = highlight;
		Color value5 = highlight;
		bool lowResolution = LowResolution;
		bool highContrast = SystemInformation.HighContrast;
		if (lowResolution)
		{
			value4 = window;
		}
		else if (!highContrast)
		{
			value = GetAlphaBlendedColorHighRes(null, buttonFace, window, 23);
			value2 = GetAlphaBlendedColorHighRes(null, buttonFace, window, 50);
			value3 = SystemColors.ButtonFace;
			value4 = GetAlphaBlendedColorHighRes(null, highlight, window, 30);
			value5 = GetAlphaBlendedColorHighRes(null, highlight, window, 50);
		}
		if (lowResolution | highContrast)
		{
			rgbTable[KnownColors.msocbvcrCBBkgd] = buttonFace;
			rgbTable[KnownColors.msocbvcrCBCtlBkgdSelectedMouseOver] = SystemColors.ControlLight;
			rgbTable[KnownColors.msocbvcrCBDragHandle] = controlText;
			rgbTable[KnownColors.msocbvcrCBGradMainMenuHorzEnd] = buttonFace;
			rgbTable[KnownColors.msocbvcrCBGradOptionsBegin] = buttonShadow;
			rgbTable[KnownColors.msocbvcrCBGradOptionsMiddle] = buttonShadow;
			rgbTable[KnownColors.msocbvcrCBGradMenuIconBkgdDroppedBegin] = buttonShadow;
			rgbTable[KnownColors.msocbvcrCBGradMenuIconBkgdDroppedMiddle] = buttonShadow;
			rgbTable[KnownColors.msocbvcrCBGradMenuIconBkgdDroppedEnd] = buttonShadow;
			rgbTable[KnownColors.msocbvcrCBMenuBdrOuter] = controlText;
			rgbTable[KnownColors.msocbvcrCBMenuBkgd] = window;
			rgbTable[KnownColors.msocbvcrCBSplitterLine] = buttonShadow;
		}
		else
		{
			rgbTable[KnownColors.msocbvcrCBBkgd] = GetAlphaBlendedColorHighRes(null, window, buttonFace, 165);
			rgbTable[KnownColors.msocbvcrCBCtlBkgdSelectedMouseOver] = GetAlphaBlendedColorHighRes(null, highlight, window, 50);
			rgbTable[KnownColors.msocbvcrCBDragHandle] = GetAlphaBlendedColorHighRes(null, buttonShadow, window, 75);
			rgbTable[KnownColors.msocbvcrCBGradMainMenuHorzEnd] = GetAlphaBlendedColorHighRes(null, buttonFace, window, 205);
			rgbTable[KnownColors.msocbvcrCBGradOptionsBegin] = GetAlphaBlendedColorHighRes(null, buttonFace, window, 70);
			rgbTable[KnownColors.msocbvcrCBGradOptionsMiddle] = GetAlphaBlendedColorHighRes(null, buttonFace, window, 90);
			rgbTable[KnownColors.msocbvcrCBGradMenuIconBkgdDroppedBegin] = GetAlphaBlendedColorHighRes(null, buttonFace, window, 40);
			rgbTable[KnownColors.msocbvcrCBGradMenuIconBkgdDroppedMiddle] = GetAlphaBlendedColorHighRes(null, buttonFace, window, 70);
			rgbTable[KnownColors.msocbvcrCBGradMenuIconBkgdDroppedEnd] = GetAlphaBlendedColorHighRes(null, buttonFace, window, 90);
			rgbTable[KnownColors.msocbvcrCBMenuBdrOuter] = GetAlphaBlendedColorHighRes(null, controlText, buttonShadow, 20);
			rgbTable[KnownColors.msocbvcrCBMenuBkgd] = GetAlphaBlendedColorHighRes(null, buttonFace, window, 143);
			rgbTable[KnownColors.msocbvcrCBSplitterLine] = GetAlphaBlendedColorHighRes(null, buttonShadow, window, 70);
		}
		rgbTable[KnownColors.msocbvcrCBCtlBkgdSelected] = (lowResolution ? SystemColors.ControlLight : highlight);
		rgbTable[KnownColors.msocbvcrCBBdrOuterDocked] = buttonFace;
		rgbTable[KnownColors.msocbvcrCBBdrOuterDocked] = buttonShadow;
		rgbTable[KnownColors.msocbvcrCBBdrOuterFloating] = buttonShadow;
		rgbTable[KnownColors.msocbvcrCBCtlBdrMouseDown] = highlight;
		rgbTable[KnownColors.msocbvcrCBCtlBdrMouseOver] = highlight;
		rgbTable[KnownColors.msocbvcrCBCtlBdrSelected] = highlight;
		rgbTable[KnownColors.msocbvcrCBCtlBdrSelectedMouseOver] = highlight;
		rgbTable[KnownColors.msocbvcrCBCtlBkgd] = empty;
		rgbTable[KnownColors.msocbvcrCBCtlBkgdLight] = window;
		rgbTable[KnownColors.msocbvcrCBCtlBkgdMouseDown] = highlight;
		rgbTable[KnownColors.msocbvcrCBCtlBkgdMouseOver] = window;
		rgbTable[KnownColors.msocbvcrCBCtlText] = controlText;
		rgbTable[KnownColors.msocbvcrCBCtlTextDisabled] = buttonShadow;
		rgbTable[KnownColors.msocbvcrCBCtlTextLight] = grayText;
		rgbTable[KnownColors.msocbvcrCBCtlTextMouseDown] = highlightText;
		rgbTable[KnownColors.msocbvcrCBCtlTextMouseOver] = windowText;
		rgbTable[KnownColors.msocbvcrCBDockSeparatorLine] = empty;
		rgbTable[KnownColors.msocbvcrCBDragHandleShadow] = window;
		rgbTable[KnownColors.msocbvcrCBDropDownArrow] = empty;
		rgbTable[KnownColors.msocbvcrCBGradMainMenuHorzBegin] = buttonFace;
		rgbTable[KnownColors.msocbvcrCBGradMouseOverEnd] = value4;
		rgbTable[KnownColors.msocbvcrCBGradMouseOverBegin] = value4;
		rgbTable[KnownColors.msocbvcrCBGradMouseOverMiddle] = value4;
		rgbTable[KnownColors.msocbvcrCBGradOptionsEnd] = buttonShadow;
		rgbTable[KnownColors.msocbvcrCBGradOptionsMouseOverBegin] = empty;
		rgbTable[KnownColors.msocbvcrCBGradOptionsMouseOverEnd] = empty;
		rgbTable[KnownColors.msocbvcrCBGradOptionsMouseOverMiddle] = empty;
		rgbTable[KnownColors.msocbvcrCBGradOptionsSelectedBegin] = empty;
		rgbTable[KnownColors.msocbvcrCBGradOptionsSelectedEnd] = empty;
		rgbTable[KnownColors.msocbvcrCBGradOptionsSelectedMiddle] = empty;
		rgbTable[KnownColors.msocbvcrCBGradSelectedBegin] = empty;
		rgbTable[KnownColors.msocbvcrCBGradSelectedEnd] = empty;
		rgbTable[KnownColors.msocbvcrCBGradSelectedMiddle] = empty;
		rgbTable[KnownColors.msocbvcrCBGradVertBegin] = value;
		rgbTable[KnownColors.msocbvcrCBGradVertMiddle] = value2;
		rgbTable[KnownColors.msocbvcrCBGradVertEnd] = value3;
		rgbTable[KnownColors.msocbvcrCBGradMouseDownBegin] = value5;
		rgbTable[KnownColors.msocbvcrCBGradMouseDownMiddle] = value5;
		rgbTable[KnownColors.msocbvcrCBGradMouseDownEnd] = value5;
		rgbTable[KnownColors.msocbvcrCBGradMenuTitleBkgdBegin] = value;
		rgbTable[KnownColors.msocbvcrCBGradMenuTitleBkgdEnd] = value2;
		rgbTable[KnownColors.msocbvcrCBIconDisabledDark] = buttonShadow;
		rgbTable[KnownColors.msocbvcrCBIconDisabledLight] = buttonFace;
		rgbTable[KnownColors.msocbvcrCBLabelBkgnd] = buttonShadow;
		rgbTable[KnownColors.msocbvcrCBLowColorIconDisabled] = empty;
		rgbTable[KnownColors.msocbvcrCBMainMenuBkgd] = buttonFace;
		rgbTable[KnownColors.msocbvcrCBMenuCtlText] = windowText;
		rgbTable[KnownColors.msocbvcrCBMenuCtlTextDisabled] = grayText;
		rgbTable[KnownColors.msocbvcrCBMenuIconBkgd] = empty;
		rgbTable[KnownColors.msocbvcrCBMenuIconBkgdDropped] = buttonShadow;
		rgbTable[KnownColors.msocbvcrCBMenuShadow] = empty;
		rgbTable[KnownColors.msocbvcrCBMenuSplitArrow] = buttonShadow;
		rgbTable[KnownColors.msocbvcrCBOptionsButtonShadow] = empty;
		rgbTable[KnownColors.msocbvcrCBShadow] = rgbTable[KnownColors.msocbvcrCBBkgd];
		rgbTable[KnownColors.msocbvcrCBSplitterLineLight] = buttonHighlight;
		rgbTable[KnownColors.msocbvcrCBTearOffHandle] = empty;
		rgbTable[KnownColors.msocbvcrCBTearOffHandleMouseOver] = empty;
		rgbTable[KnownColors.msocbvcrCBTitleBkgd] = buttonShadow;
		rgbTable[KnownColors.msocbvcrCBTitleText] = buttonHighlight;
		rgbTable[KnownColors.msocbvcrDisabledFocuslessHighlightedText] = grayText;
		rgbTable[KnownColors.msocbvcrDisabledHighlightedText] = grayText;
		rgbTable[KnownColors.msocbvcrDlgGroupBoxText] = controlText;
		rgbTable[KnownColors.msocbvcrDocTabBdr] = buttonShadow;
		rgbTable[KnownColors.msocbvcrDocTabBdrDark] = buttonFace;
		rgbTable[KnownColors.msocbvcrDocTabBdrDarkMouseDown] = highlight;
		rgbTable[KnownColors.msocbvcrDocTabBdrDarkMouseOver] = SystemColors.MenuText;
		rgbTable[KnownColors.msocbvcrDocTabBdrLight] = buttonFace;
		rgbTable[KnownColors.msocbvcrDocTabBdrLightMouseDown] = highlight;
		rgbTable[KnownColors.msocbvcrDocTabBdrLightMouseOver] = SystemColors.MenuText;
		rgbTable[KnownColors.msocbvcrDocTabBdrMouseDown] = highlight;
		rgbTable[KnownColors.msocbvcrDocTabBdrMouseOver] = SystemColors.MenuText;
		rgbTable[KnownColors.msocbvcrDocTabBdrSelected] = buttonShadow;
		rgbTable[KnownColors.msocbvcrDocTabBkgd] = buttonFace;
		rgbTable[KnownColors.msocbvcrDocTabBkgdMouseDown] = highlight;
		rgbTable[KnownColors.msocbvcrDocTabBkgdMouseOver] = highlight;
		rgbTable[KnownColors.msocbvcrDocTabBkgdSelected] = window;
		rgbTable[KnownColors.msocbvcrDocTabText] = controlText;
		rgbTable[KnownColors.msocbvcrDocTabTextMouseDown] = highlightText;
		rgbTable[KnownColors.msocbvcrDocTabTextMouseOver] = highlight;
		rgbTable[KnownColors.msocbvcrDocTabTextSelected] = windowText;
		rgbTable[KnownColors.msocbvcrDWActiveTabBkgd] = buttonFace;
		rgbTable[KnownColors.msocbvcrDWActiveTabBkgd] = buttonShadow;
		rgbTable[KnownColors.msocbvcrDWActiveTabText] = buttonFace;
		rgbTable[KnownColors.msocbvcrDWActiveTabText] = controlText;
		rgbTable[KnownColors.msocbvcrDWActiveTabTextDisabled] = buttonShadow;
		rgbTable[KnownColors.msocbvcrDWActiveTabTextDisabled] = controlText;
		rgbTable[KnownColors.msocbvcrDWInactiveTabBkgd] = buttonFace;
		rgbTable[KnownColors.msocbvcrDWInactiveTabBkgd] = buttonShadow;
		rgbTable[KnownColors.msocbvcrDWInactiveTabText] = buttonHighlight;
		rgbTable[KnownColors.msocbvcrDWInactiveTabText] = controlText;
		rgbTable[KnownColors.msocbvcrDWTabBkgdMouseDown] = buttonFace;
		rgbTable[KnownColors.msocbvcrDWTabBkgdMouseOver] = buttonFace;
		rgbTable[KnownColors.msocbvcrDWTabTextMouseDown] = controlText;
		rgbTable[KnownColors.msocbvcrDWTabTextMouseOver] = controlText;
		rgbTable[KnownColors.msocbvcrFocuslessHighlightedBkgd] = buttonFace;
		rgbTable[KnownColors.msocbvcrFocuslessHighlightedBkgd] = SystemColors.InactiveCaption;
		rgbTable[KnownColors.msocbvcrFocuslessHighlightedText] = controlText;
		rgbTable[KnownColors.msocbvcrFocuslessHighlightedText] = SystemColors.InactiveCaptionText;
		rgbTable[KnownColors.msocbvcrGDHeaderBdr] = highlight;
		rgbTable[KnownColors.msocbvcrGDHeaderBkgd] = window;
		rgbTable[KnownColors.msocbvcrGDHeaderCellBdr] = buttonShadow;
		rgbTable[KnownColors.msocbvcrGDHeaderCellBkgd] = buttonFace;
		rgbTable[KnownColors.msocbvcrGDHeaderCellBkgdSelected] = empty;
		rgbTable[KnownColors.msocbvcrGDHeaderSeeThroughSelection] = highlight;
		rgbTable[KnownColors.msocbvcrGSPDarkBkgd] = buttonFace;
		rgbTable[KnownColors.msocbvcrGSPDarkBkgd] = window;
		rgbTable[KnownColors.msocbvcrGSPGroupContentDarkBkgd] = window;
		rgbTable[KnownColors.msocbvcrGSPGroupContentLightBkgd] = window;
		rgbTable[KnownColors.msocbvcrGSPGroupContentText] = windowText;
		rgbTable[KnownColors.msocbvcrGSPGroupContentTextDisabled] = grayText;
		rgbTable[KnownColors.msocbvcrGSPGroupHeaderDarkBkgd] = window;
		rgbTable[KnownColors.msocbvcrGSPGroupHeaderLightBkgd] = window;
		rgbTable[KnownColors.msocbvcrGSPGroupHeaderText] = controlText;
		rgbTable[KnownColors.msocbvcrGSPGroupHeaderText] = windowText;
		rgbTable[KnownColors.msocbvcrGSPGroupline] = buttonShadow;
		rgbTable[KnownColors.msocbvcrGSPGroupline] = window;
		rgbTable[KnownColors.msocbvcrGSPHyperlink] = empty;
		rgbTable[KnownColors.msocbvcrGSPLightBkgd] = window;
		rgbTable[KnownColors.msocbvcrHyperlink] = empty;
		rgbTable[KnownColors.msocbvcrHyperlinkFollowed] = empty;
		rgbTable[KnownColors.msocbvcrJotNavUIBdr] = buttonShadow;
		rgbTable[KnownColors.msocbvcrJotNavUIBdr] = windowText;
		rgbTable[KnownColors.msocbvcrJotNavUIGradBegin] = buttonFace;
		rgbTable[KnownColors.msocbvcrJotNavUIGradBegin] = window;
		rgbTable[KnownColors.msocbvcrJotNavUIGradEnd] = window;
		rgbTable[KnownColors.msocbvcrJotNavUIGradMiddle] = buttonFace;
		rgbTable[KnownColors.msocbvcrJotNavUIGradMiddle] = window;
		rgbTable[KnownColors.msocbvcrJotNavUIText] = windowText;
		rgbTable[KnownColors.msocbvcrListHeaderArrow] = controlText;
		rgbTable[KnownColors.msocbvcrNetLookBkgnd] = empty;
		rgbTable[KnownColors.msocbvcrOABBkgd] = buttonShadow;
		rgbTable[KnownColors.msocbvcrOBBkgdBdr] = buttonShadow;
		rgbTable[KnownColors.msocbvcrOBBkgdBdrContrast] = window;
		rgbTable[KnownColors.msocbvcrOGMDIParentWorkspaceBkgd] = buttonShadow;
		rgbTable[KnownColors.msocbvcrOGRulerActiveBkgd] = window;
		rgbTable[KnownColors.msocbvcrOGRulerBdr] = controlText;
		rgbTable[KnownColors.msocbvcrOGRulerBkgd] = buttonFace;
		rgbTable[KnownColors.msocbvcrOGRulerInactiveBkgd] = buttonShadow;
		rgbTable[KnownColors.msocbvcrOGRulerTabBoxBdr] = buttonShadow;
		rgbTable[KnownColors.msocbvcrOGRulerTabBoxBdrHighlight] = buttonHighlight;
		rgbTable[KnownColors.msocbvcrOGRulerTabStopTicks] = buttonShadow;
		rgbTable[KnownColors.msocbvcrOGRulerText] = windowText;
		rgbTable[KnownColors.msocbvcrOGTaskPaneGroupBoxHeaderBkgd] = buttonFace;
		rgbTable[KnownColors.msocbvcrOGWorkspaceBkgd] = buttonShadow;
		rgbTable[KnownColors.msocbvcrOLKFlagNone] = buttonHighlight;
		rgbTable[KnownColors.msocbvcrOLKFolderbarDark] = buttonShadow;
		rgbTable[KnownColors.msocbvcrOLKFolderbarLight] = buttonShadow;
		rgbTable[KnownColors.msocbvcrOLKFolderbarText] = window;
		rgbTable[KnownColors.msocbvcrOLKGridlines] = buttonShadow;
		rgbTable[KnownColors.msocbvcrOLKGroupLine] = buttonShadow;
		rgbTable[KnownColors.msocbvcrOLKGroupNested] = buttonFace;
		rgbTable[KnownColors.msocbvcrOLKGroupShaded] = buttonFace;
		rgbTable[KnownColors.msocbvcrOLKGroupText] = buttonShadow;
		rgbTable[KnownColors.msocbvcrOLKIconBar] = buttonFace;
		rgbTable[KnownColors.msocbvcrOLKInfoBarBkgd] = buttonFace;
		rgbTable[KnownColors.msocbvcrOLKInfoBarText] = controlText;
		rgbTable[KnownColors.msocbvcrOLKPreviewPaneLabelText] = windowText;
		rgbTable[KnownColors.msocbvcrOLKTodayIndicatorDark] = highlight;
		rgbTable[KnownColors.msocbvcrOLKTodayIndicatorLight] = buttonFace;
		rgbTable[KnownColors.msocbvcrOLKWBActionDividerLine] = buttonShadow;
		rgbTable[KnownColors.msocbvcrOLKWBButtonDark] = buttonFace;
		rgbTable[KnownColors.msocbvcrOLKWBButtonLight] = buttonFace;
		rgbTable[KnownColors.msocbvcrOLKWBButtonLight] = buttonHighlight;
		rgbTable[KnownColors.msocbvcrOLKWBDarkOutline] = buttonShadow;
		rgbTable[KnownColors.msocbvcrOLKWBFoldersBackground] = window;
		rgbTable[KnownColors.msocbvcrOLKWBHoverButtonDark] = empty;
		rgbTable[KnownColors.msocbvcrOLKWBHoverButtonLight] = empty;
		rgbTable[KnownColors.msocbvcrOLKWBLabelText] = windowText;
		rgbTable[KnownColors.msocbvcrOLKWBPressedButtonDark] = empty;
		rgbTable[KnownColors.msocbvcrOLKWBPressedButtonLight] = empty;
		rgbTable[KnownColors.msocbvcrOLKWBSelectedButtonDark] = empty;
		rgbTable[KnownColors.msocbvcrOLKWBSelectedButtonLight] = empty;
		rgbTable[KnownColors.msocbvcrOLKWBSplitterDark] = buttonShadow;
		rgbTable[KnownColors.msocbvcrOLKWBSplitterLight] = buttonFace;
		rgbTable[KnownColors.msocbvcrOLKWBSplitterLight] = buttonShadow;
		rgbTable[KnownColors.msocbvcrPlacesBarBkgd] = buttonFace;
		rgbTable[KnownColors.msocbvcrPPOutlineThumbnailsPaneTabAreaBkgd] = buttonFace;
		rgbTable[KnownColors.msocbvcrPPOutlineThumbnailsPaneTabBdr] = buttonShadow;
		rgbTable[KnownColors.msocbvcrPPOutlineThumbnailsPaneTabInactiveBkgd] = buttonFace;
		rgbTable[KnownColors.msocbvcrPPOutlineThumbnailsPaneTabText] = windowText;
		rgbTable[KnownColors.msocbvcrPPSlideBdrActiveSelected] = highlight;
		rgbTable[KnownColors.msocbvcrPPSlideBdrActiveSelectedMouseOver] = highlight;
		rgbTable[KnownColors.msocbvcrPPSlideBdrInactiveSelected] = grayText;
		rgbTable[KnownColors.msocbvcrPPSlideBdrMouseOver] = highlight;
		rgbTable[KnownColors.msocbvcrPubPrintDocScratchPageBkgd] = buttonFace;
		rgbTable[KnownColors.msocbvcrPubWebDocScratchPageBkgd] = buttonFace;
		rgbTable[KnownColors.msocbvcrSBBdr] = buttonShadow;
		rgbTable[KnownColors.msocbvcrScrollbarBkgd] = buttonShadow;
		rgbTable[KnownColors.msocbvcrToastGradBegin] = buttonFace;
		rgbTable[KnownColors.msocbvcrToastGradEnd] = buttonFace;
		rgbTable[KnownColors.msocbvcrWPBdrInnerDocked] = empty;
		rgbTable[KnownColors.msocbvcrWPBdrOuterDocked] = buttonFace;
		rgbTable[KnownColors.msocbvcrWPBdrOuterFloating] = buttonShadow;
		rgbTable[KnownColors.msocbvcrWPBkgd] = window;
		rgbTable[KnownColors.msocbvcrWPCtlBdr] = buttonShadow;
		rgbTable[KnownColors.msocbvcrWPCtlBdrDefault] = buttonShadow;
		rgbTable[KnownColors.msocbvcrWPCtlBdrDefault] = controlText;
		rgbTable[KnownColors.msocbvcrWPCtlBdrDisabled] = buttonShadow;
		rgbTable[KnownColors.msocbvcrWPCtlBkgd] = buttonFace;
		rgbTable[KnownColors.msocbvcrWPCtlBkgdDisabled] = buttonFace;
		rgbTable[KnownColors.msocbvcrWPCtlText] = controlText;
		rgbTable[KnownColors.msocbvcrWPCtlTextDisabled] = buttonShadow;
		rgbTable[KnownColors.msocbvcrWPCtlTextMouseDown] = highlightText;
		rgbTable[KnownColors.msocbvcrWPGroupline] = buttonShadow;
		rgbTable[KnownColors.msocbvcrWPInfoTipBkgd] = SystemColors.Info;
		rgbTable[KnownColors.msocbvcrWPInfoTipText] = SystemColors.InfoText;
		rgbTable[KnownColors.msocbvcrWPNavBarBkgnd] = buttonFace;
		rgbTable[KnownColors.msocbvcrWPText] = controlText;
		rgbTable[KnownColors.msocbvcrWPText] = windowText;
		rgbTable[KnownColors.msocbvcrWPTextDisabled] = grayText;
		rgbTable[KnownColors.msocbvcrWPTitleBkgdActive] = highlight;
		rgbTable[KnownColors.msocbvcrWPTitleBkgdInactive] = buttonFace;
		rgbTable[KnownColors.msocbvcrWPTitleTextActive] = highlightText;
		rgbTable[KnownColors.msocbvcrWPTitleTextInactive] = controlText;
		rgbTable[KnownColors.msocbvcrXLFormulaBarBkgd] = buttonFace;
	}




	// The port has one theme, Windows 11's managed one, and .NET reads that as Aero ("aero.msstyles"),
	// for which InitThemedColors falls through to the system colours. The Luna and Royale tables are
	// only reachable on XP themes and are left out.
	private void InitThemedColors(ref Dictionary<KnownColors, Color> rgbTable)
	{
		InitSystemColors(ref rgbTable);
	}

}
