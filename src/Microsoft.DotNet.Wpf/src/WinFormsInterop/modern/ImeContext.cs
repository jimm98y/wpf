// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// A window's input method context, ported from .NET's ImeContext and ImeModeConversion (Control.Ime.cs). Windows keeps
// one per window in IMM32 (ImmGetContext, its open status, its conversion mode); here it is kept by the window's
// handle, with the same rules: what an IME mode sets and clears of the conversion bits, and what the bits read back as
// for the input language's table (Japanese, Korean, Chinese; none for another language).

using System.Collections.Generic;

namespace System.Windows.Forms
{
	public static class ImeContext
	{
		// IMM32's conversion mode bits (IME_CMODE_*)
		internal const int CModeNative = 0x1, CModeKatakana = 0x2, CModeFullShape = 0x8;

		private sealed class Context
		{
			public bool Open;
			public int Conversion;
		}

		private static readonly object s_lock = new object ();
		// a window's context: present while it is associated (enabled), absent once disabled
		private static readonly Dictionary<IntPtr, Context> s_contexts = new Dictionary<IntPtr, Context> ();
		private static readonly HashSet<IntPtr> s_disabled = new HashSet<IntPtr> ();

		private static Context Get (IntPtr handle)
		{
			lock (s_lock) {
				if (handle == IntPtr.Zero || s_disabled.Contains (handle))
					return null;
				if (!s_contexts.TryGetValue (handle, out Context context))
					s_contexts [handle] = context = new Context ();
				return context;
			}
		}

		/// <summary>Closes the window's input method and takes its context away (ImmAssociateContext(NULL)).</summary>
		public static void Disable (IntPtr handle)
		{
			if (ImeModeConversion.InputLanguageTable == ImeModeConversion.UnsupportedTable)
				return;
			if (IsOpen (handle))
				SetOpenStatus (false, handle);
			lock (s_lock)
				s_disabled.Add (handle);
		}

		/// <summary>Gives the window its context back and opens the input method.</summary>
		public static void Enable (IntPtr handle)
		{
			if (ImeModeConversion.InputLanguageTable == ImeModeConversion.UnsupportedTable)
				return;
			lock (s_lock)
				s_disabled.Remove (handle);
			if (!IsOpen (handle))
				SetOpenStatus (true, handle);
		}

		/// <summary>The IME mode the window's context is in, read through the input language's table.</summary>
		public static ImeMode GetImeMode (IntPtr handle)
		{
			ImeMode [] table = ImeModeConversion.InputLanguageTable;
			if (table == ImeModeConversion.UnsupportedTable)
				return ImeMode.Inherit;

			Context context = Get (handle);
			if (context == null)
				return ImeMode.Disable;
			if (!context.Open)
				return table [ImeModeConversion.ImeClosed];

			int conversion = context.Conversion;
			bool full = (conversion & CModeFullShape) != 0;
			if ((conversion & CModeNative) != 0) {
				if ((conversion & CModeKatakana) != 0)
					return full ? table [ImeModeConversion.ImeNativeFullKatakana] : table [ImeModeConversion.ImeNativeHalfKatakana];
				return full ? table [ImeModeConversion.ImeNativeFullHiragana] : table [ImeModeConversion.ImeNativeHalfHiragana];
			}
			return full ? table [ImeModeConversion.ImeAlphaFull] : table [ImeModeConversion.ImeAlphaHalf];
		}

		/// <summary>Whether the window's input method is open.</summary>
		public static bool IsOpen (IntPtr handle) => Get (handle)?.Open ?? false;

		/// <summary>Puts the window's context in <paramref name="imeMode"/>.</summary>
		public static void SetImeStatus (ImeMode imeMode, IntPtr handle)
		{
			if (imeMode == ImeMode.Inherit || imeMode == ImeMode.NoControl)
				return;

			ImeMode [] table = ImeModeConversion.InputLanguageTable;
			if (table == ImeModeConversion.UnsupportedTable)
				return;

			if (imeMode == ImeMode.Disable)
				Disable (handle);
			else
				Enable (handle);

			switch (imeMode) {
			case ImeMode.NoControl:
			case ImeMode.Disable:
				return;
			case ImeMode.On:
				// active (Chinese On, Japanese Hiragana, Korean Hangul), whatever the language
				imeMode = ImeMode.Hiragana;
				break;
			case ImeMode.Off:
				if (table == ImeModeConversion.JapaneseTable) {
					// Japanese takes Off as Close
					SetOpenStatus (false, handle);
					return;
				}
				// Chinese: Alpha, to tell Off from Close
				imeMode = ImeMode.Alpha;
				break;
			case ImeMode.Close:
				if (table == ImeModeConversion.KoreanTable) {
					// Korean has no Close
					imeMode = ImeMode.Alpha;
					break;
				}
				SetOpenStatus (false, handle);
				return;
			}

			if (ImeModeConversion.ImeModeConversionBits.TryGetValue (imeMode, out ImeModeConversion bits)) {
				Context context = Get (handle);
				if (context != null) {
					lock (s_lock)
						context.Conversion = (context.Conversion | bits.SetBits) & ~bits.ClearBits;
				}
			}
		}

		/// <summary>Opens or closes the window's input method.</summary>
		public static void SetOpenStatus (bool open, IntPtr handle)
		{
			if (ImeModeConversion.InputLanguageTable == ImeModeConversion.UnsupportedTable)
				return;
			Context context = Get (handle);
			if (context != null) {
				lock (s_lock)
					context.Open = open;
			}
		}

		/// <summary>A window destroyed: its context goes with it.</summary>
		internal static void Forget (IntPtr handle)
		{
			lock (s_lock) {
				s_contexts.Remove (handle);
				s_disabled.Remove (handle);
			}
		}
	}

	/// <summary>The conversion bits an IME mode sets and clears, and the tables the bits read back through.</summary>
	public readonly struct ImeModeConversion
	{
		private static volatile Dictionary<ImeMode, ImeModeConversion> s_imeModeConversionBits;

		internal int SetBits { get; init; }
		internal int ClearBits { get; init; }

		internal const int ImeDisabled = 1;
		internal const int ImeDirectInput = 2;
		internal const int ImeClosed = 3;
		internal const int ImeNativeInput = 4;
		internal const int ImeNativeFullHiragana = 4;
		internal const int ImeNativeHalfHiragana = 5;
		internal const int ImeNativeFullKatakana = 6;
		internal const int ImeNativeHalfKatakana = 7;
		internal const int ImeAlphaFull = 8;
		internal const int ImeAlphaHalf = 9;

		private static readonly ImeMode [] s_japaneseTable = {
			ImeMode.Inherit, ImeMode.Disable, ImeMode.Off, ImeMode.Off, ImeMode.Hiragana,
			ImeMode.Hiragana, ImeMode.Katakana, ImeMode.KatakanaHalf, ImeMode.AlphaFull, ImeMode.Alpha,
		};

		private static readonly ImeMode [] s_koreanTable = {
			ImeMode.Inherit, ImeMode.Disable, ImeMode.Alpha, ImeMode.Alpha, ImeMode.HangulFull,
			ImeMode.Hangul, ImeMode.HangulFull, ImeMode.Hangul, ImeMode.AlphaFull, ImeMode.Alpha,
		};

		private static readonly ImeMode [] s_chineseTable = {
			ImeMode.Inherit, ImeMode.Disable, ImeMode.Off, ImeMode.Close, ImeMode.On,
			ImeMode.OnHalf, ImeMode.On, ImeMode.OnHalf, ImeMode.Off, ImeMode.Off,
		};

		private static readonly ImeMode [] s_unsupportedTable = Array.Empty<ImeMode> ();

		internal static ImeMode [] ChineseTable => s_chineseTable;
		internal static ImeMode [] JapaneseTable => s_japaneseTable;
		internal static ImeMode [] KoreanTable => s_koreanTable;
		internal static ImeMode [] UnsupportedTable => s_unsupportedTable;

		/// <summary>The table of the current input language: by its language id, as Windows' keyboard layout
		/// handle carries it.</summary>
		internal static ImeMode [] InputLanguageTable {
			get {
				int lcid = InputLanguage.CurrentInputLanguage?.Culture?.LCID ?? 0;
				switch (lcid) {
				case 0x0404: case 0x0804: case 0x0c04: case 0x1004: case 0x1404:
					return s_chineseTable;
				case 0x0412: case 0x0812:
					return s_koreanTable;
				case 0x0411:
					return s_japaneseTable;
				default:
					return s_unsupportedTable;
				}
			}
		}

		public static Dictionary<ImeMode, ImeModeConversion> ImeModeConversionBits =>
			s_imeModeConversionBits ??= new Dictionary<ImeMode, ImeModeConversion> (7) {
				{ ImeMode.Hiragana, new ImeModeConversion { SetBits = ImeContext.CModeFullShape | ImeContext.CModeNative, ClearBits = ImeContext.CModeKatakana } },
				{ ImeMode.Katakana, new ImeModeConversion { SetBits = ImeContext.CModeFullShape | ImeContext.CModeKatakana | ImeContext.CModeNative, ClearBits = 0 } },
				{ ImeMode.KatakanaHalf, new ImeModeConversion { SetBits = ImeContext.CModeKatakana | ImeContext.CModeNative, ClearBits = ImeContext.CModeFullShape } },
				{ ImeMode.AlphaFull, new ImeModeConversion { SetBits = ImeContext.CModeFullShape, ClearBits = ImeContext.CModeKatakana | ImeContext.CModeNative } },
				{ ImeMode.Alpha, new ImeModeConversion { SetBits = 0, ClearBits = ImeContext.CModeFullShape | ImeContext.CModeKatakana | ImeContext.CModeNative } },
				{ ImeMode.HangulFull, new ImeModeConversion { SetBits = ImeContext.CModeFullShape | ImeContext.CModeNative, ClearBits = 0 } },
				{ ImeMode.Hangul, new ImeModeConversion { SetBits = ImeContext.CModeNative, ClearBits = ImeContext.CModeFullShape } },
				{ ImeMode.OnHalf, new ImeModeConversion { SetBits = ImeContext.CModeNative, ClearBits = ImeContext.CModeKatakana | ImeContext.CModeFullShape } },
			};

		public static bool IsCurrentConversionTableSupported => InputLanguageTable != UnsupportedTable;
	}
}
