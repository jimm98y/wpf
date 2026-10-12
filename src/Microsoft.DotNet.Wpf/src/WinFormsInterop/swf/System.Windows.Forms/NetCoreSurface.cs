// The public surface .NET (Core) WinForms has and Mono's never had.
//
// A library compiled against .NET's WinForms calls these directly, so without them it fails with
// MissingMethodException or TypeLoadException the first time it touches one, however unimportant the
// call: every app created from the .NET template calls Application.SetHighDpiMode from Main (through
// the generated ApplicationConfiguration.Initialize). The types that .NET puts in
// System.Windows.Forms.Primitives are declared here too; that assembly's identity forwards to them
// (see ../../swf-primitives-facade).
//
// Semantics are .NET's where this WinForms can honour them, and an accepted no-op where it cannot: the
// WebGPU host renders at the display's scale already, so there is no DPI mode to switch.

using System.ComponentModel;
using System.Drawing;

namespace System.Windows.Forms
{
	/// <summary>.NET's high-DPI modes (System.Windows.Forms.Primitives).</summary>
	public enum HighDpiMode
	{
		DpiUnaware = 0,
		SystemAware = 1,
		PerMonitor = 2,
		PerMonitorV2 = 3,
		DpiUnawareGdiScaled = 4,
	}

	public sealed partial class Application
	{
		private static HighDpiMode s_highDpiMode = HighDpiMode.PerMonitorV2;
		internal static Font DefaultFontOverride;

		/// <summary>The mode set by <see cref="SetHighDpiMode"/>; the host always renders per-monitor.</summary>
		public static HighDpiMode HighDpiMode => s_highDpiMode;

		/// <summary>
		/// Records the mode and reports success, as .NET does when called before the first window is
		/// created. Scaling itself is the host's: it already renders at each display's scale.
		/// </summary>
		public static bool SetHighDpiMode (HighDpiMode highDpiMode)
		{
			if (!Enum.IsDefined (typeof (HighDpiMode), highDpiMode))
				throw new InvalidEnumArgumentException (nameof (highDpiMode), (int) highDpiMode, typeof (HighDpiMode));
			s_highDpiMode = highDpiMode;
			return true;
		}

		/// <summary>Sets the font <see cref="Control.DefaultFont"/> returns for the rest of the process.</summary>
		public static void SetDefaultFont (Font font)
		{
			if (font == null)
				throw new ArgumentNullException (nameof (font));
			DefaultFontOverride = font;
		}
	}

	public partial class Control
	{
		/// <summary>.NET's typed overload of <see cref="BeginInvoke(Delegate)"/>.</summary>
		public IAsyncResult BeginInvoke (Action method)
		{
			return BeginInvoke ((Delegate) method);
		}

		/// <summary>.NET's typed overload of <see cref="Invoke(Delegate)"/>.</summary>
		public void Invoke (Action method)
		{
			Invoke ((Delegate) method);
		}

		/// <summary>.NET's typed overload of <see cref="Invoke(Delegate)"/>.</summary>
		public T Invoke<T> (Func<T> method)
		{
			return (T) Invoke ((Delegate) method);
		}

		/// <summary>The DPI the control is laid out for; the host scales the whole surface, so 96.</summary>
		[Browsable (false)]
		[EditorBrowsable (EditorBrowsableState.Advanced)]
		public int DeviceDpi => 96;

		/// <summary>Logical to device pixels at <see cref="DeviceDpi"/>.</summary>
		public int LogicalToDeviceUnits (int value) => value;

		/// <summary>Logical to device pixels at <see cref="DeviceDpi"/>.</summary>
		public Size LogicalToDeviceUnits (Size value) => value;

		/// <summary>Scales a bitmap from logical to device pixels; at 96 DPI it is unchanged.</summary>
		public void ScaleBitmapLogicalToDevice (ref Bitmap logicalBitmap)
		{
		}
	}
}

namespace System.Windows.Forms.Automation
{
	/// <summary>Live-region politeness (System.Windows.Forms.Primitives).</summary>
	public enum AutomationLiveSetting
	{
		Off = 0,
		Polite = 1,
		Assertive = 2,
	}

	/// <summary>Kinds of UI Automation notification (System.Windows.Forms.Primitives).</summary>
	public enum AutomationNotificationKind
	{
		ItemAdded = 0,
		ItemRemoved = 1,
		ActionCompleted = 2,
		ActionAborted = 3,
		Other = 4,
	}

	/// <summary>How a UI Automation notification is processed (System.Windows.Forms.Primitives).</summary>
	public enum AutomationNotificationProcessing
	{
		ImportantAll = 0,
		ImportantMostRecent = 1,
		All = 2,
		MostRecent = 3,
		CurrentThenMostRecent = 4,
	}

	/// <summary>A control that is a live region (System.Windows.Forms.Primitives).</summary>
	public interface IAutomationLiveRegion
	{
		AutomationLiveSetting LiveSetting { get; set; }
	}
}
