//
// System.Windows.Forms.Design.MaskDescriptor.cs
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
using System.Globalization;

namespace System.Windows.Forms.Design
{
	// Ported from dotnet/winforms (System.Windows.Forms.Design.MaskDescriptor). Mono threw from Culture and from
	// both IsValidMaskDescriptor overloads, and compared descriptors by reference.
	public abstract class MaskDescriptor
	{
		protected MaskDescriptor ()
		{
		}

		public virtual CultureInfo Culture => System.Threading.Thread.CurrentThread.CurrentCulture;

		public abstract string Mask { get; }

		public abstract string Name { get; }

		public abstract string Sample { get; }

		public abstract Type ValidatingType { get; }

		public static bool IsValidMaskDescriptor (MaskDescriptor maskDescriptor)
		{
			return IsValidMaskDescriptor (maskDescriptor, out string _);
		}

		/// <summary>.NET's check: the three required properties are set, the sample fits the mask, and it parses
		/// as the validating type.</summary>
		public static bool IsValidMaskDescriptor (MaskDescriptor maskDescriptor, out string validationErrorDescription)
		{
			validationErrorDescription = string.Empty;
			if (maskDescriptor == null) {
				validationErrorDescription = "The provided MaskDescriptor is null.";
				return false;
			}
			if (string.IsNullOrEmpty (maskDescriptor.Mask) || string.IsNullOrEmpty (maskDescriptor.Name) || string.IsNullOrEmpty (maskDescriptor.Sample)) {
				validationErrorDescription = "One or more required properties are null or empty.";
				return false;
			}

			var provider = new MaskedTextProvider (maskDescriptor.Mask, maskDescriptor.Culture);
			using (var box = new MaskedTextBox (provider) {
				SkipLiterals = true,
				ResetOnPrompt = true,
				ResetOnSpace = true,
				ValidatingType = maskDescriptor.ValidatingType,
				FormatProvider = maskDescriptor.Culture,
				Culture = maskDescriptor.Culture,
			}) {
				box.TypeValidationCompleted += (s, e) => { if (!e.IsValidInput) ((MaskedTextBox) s).Tag = e.Message; };
				box.MaskInputRejected += (s, e) => ((MaskedTextBox) s).Tag = RejectedMessage (e);
				box.Text = maskDescriptor.Sample;
				if (box.Tag == null && maskDescriptor.ValidatingType != null)
					box.ValidateText ();
				if (box.Tag != null)
					validationErrorDescription = box.Tag.ToString ();
			}
			return validationErrorDescription.Length == 0;
		}

		/// <summary>MaskedTextBoxDesigner.GetMaskInputRejectedErrorMessage, with .NET's text.</summary>
		internal static string RejectedMessage (MaskInputRejectedEventArgs e)
		{
			string hint;
			switch (e.RejectionHint) {
			case MaskedTextResultHint.AsciiCharacterExpected: hint = "ASCII character expected"; break;
			case MaskedTextResultHint.AlphanumericCharacterExpected: hint = "Alphanumeric character expected"; break;
			case MaskedTextResultHint.DigitExpected: hint = "Digit expected"; break;
			case MaskedTextResultHint.LetterExpected: hint = "Letter expected"; break;
			case MaskedTextResultHint.SignedDigitExpected: hint = "Signed digit expected"; break;
			case MaskedTextResultHint.PromptCharNotAllowed: hint = "Prompt character disallowed as input"; break;
			case MaskedTextResultHint.UnavailableEditPosition: hint = "Unavailable edit position"; break;
			case MaskedTextResultHint.NonEditPosition: hint = "Non edit position"; break;
			case MaskedTextResultHint.PositionOutOfRange: hint = "Position out of range"; break;
			default: hint = "Invalid input character"; break;
			}
			return string.Format (CultureInfo.CurrentCulture, "Error at position {0}: {1}.", e.Position, hint);
		}

		/// <summary>Valid descriptors are equal when their masks and validating types are; others by reference.</summary>
		public override bool Equals (object maskDescriptor)
		{
			MaskDescriptor descriptor = maskDescriptor as MaskDescriptor;
			if (!IsValidMaskDescriptor (descriptor) || !IsValidMaskDescriptor (this))
				return this == maskDescriptor;
			return Mask == descriptor.Mask && ValidatingType == descriptor.ValidatingType;
		}

		public override int GetHashCode ()
		{
			return string.Concat (Mask, ValidatingType?.ToString ()).GetHashCode ();
		}

		public override string ToString ()
		{
			return $"{GetType ()}<Name={Name ?? "null"}, Mask={Mask ?? "null"}, ValidatingType={(ValidatingType != null ? ValidatingType.ToString () : "null")}";
		}
	}
}
