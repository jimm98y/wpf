// The public surface .NET (Core) WinForms has and Mono's never had.
//
// A library compiled against .NET's WinForms calls these directly, so without them it fails with
// MissingMethodException or TypeLoadException the first time it touches one, however unimportant the
// call: every app created from the .NET template calls Application.SetHighDpiMode from Main (through
// the generated ApplicationConfiguration.Initialize). The types that .NET puts in
// System.Windows.Forms.Primitives are declared here too; that assembly's identity forwards to them
// (see ../../swf-primitives-facade).
//
// Semantics are .NET's where this WinForms can honour them, and an accepted no-op where it cannot. The
// high-DPI mode is honoured (ScaleHelper): per-monitor aware applications lay out in device pixels.

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
		internal static Font DefaultFontOverride;

		/// <summary>The mode the application runs in: its manifest's, else the one
		/// <see cref="SetHighDpiMode"/> set, else DpiUnaware -- as stock reports the process's.</summary>
		public static HighDpiMode HighDpiMode => ScaleHelper.Mode;

		/// <summary>
		/// Sets the mode, as .NET does: only before the first window, and not over a mode the
		/// application's manifest declares (Windows has applied that one at startup).
		/// </summary>
		public static bool SetHighDpiMode (HighDpiMode highDpiMode)
		{
			if (!Enum.IsDefined (typeof (HighDpiMode), highDpiMode))
				throw new InvalidEnumArgumentException (nameof (highDpiMode), (int) highDpiMode, typeof (HighDpiMode));
			if (ScaleHelper.IsResolved || ScaleHelper.ManifestMode != null)
				return false;
			ScaleHelper.s_requested = highDpiMode;
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

		private int _deviceDpi, _originalDeviceDpi;

		/// <summary>The DPI the control lays out for (.NET's DeviceDpiInternal): the system DPI until
		/// its window knows its display's.</summary>
		internal int DeviceDpiInternal {
			get => _deviceDpi == 0 ? ScaleHelper.InitialSystemDpi : _deviceDpi;
			set => _deviceDpi = value;
		}

		/// <summary>The DPI before the last change (.NET's OriginalDeviceDpiInternal).</summary>
		internal int OriginalDeviceDpiInternal {
			get => _originalDeviceDpi == 0 ? DeviceDpiInternal : _originalDeviceDpi;
			set => _originalDeviceDpi = value;
		}

		/// <summary>The DPI of the display the control is on, for a per-monitor (v2) aware
		/// application; the system DPI otherwise (96 for one that is not DPI-aware).</summary>
		[Browsable (false)]
		[EditorBrowsable (EditorBrowsableState.Advanced)]
		public int DeviceDpi => ScaleHelper.IsThreadPerMonitorV2Aware ? DeviceDpiInternal : ScaleHelper.InitialSystemDpi;

		/// <summary>Logical (96-dpi) to device pixels at <see cref="DeviceDpi"/>.</summary>
		public int LogicalToDeviceUnits (int value) => ScaleHelper.ScaleToDpi (value, DeviceDpi);

		/// <summary>Logical (96-dpi) to device pixels at <see cref="DeviceDpi"/>.</summary>
		public Size LogicalToDeviceUnits (Size value) => ScaleHelper.ScaleToDpi (value, DeviceDpi);

		/// <summary>Scales a bitmap from logical to device pixels at <see cref="DeviceDpi"/>.</summary>
		public void ScaleBitmapLogicalToDevice (ref Bitmap logicalBitmap)
		{
			logicalBitmap = ScaleHelper.ScaleToDpi (logicalBitmap, DeviceDpi, disposeBitmap: true);
		}

		static readonly object DpiChangedBeforeParentEvent = new object ();
		static readonly object DpiChangedAfterParentEvent = new object ();

		/// <summary>Occurs when the DPI of the display the control is on changes, before its top-level
		/// window is told (WM_DPICHANGED_BEFOREPARENT).</summary>
		public event EventHandler DpiChangedBeforeParent {
			add { Events.AddHandler (DpiChangedBeforeParentEvent, value); }
			remove { Events.RemoveHandler (DpiChangedBeforeParentEvent, value); }
		}

		/// <summary>Occurs after the top-level window has handled a DPI change (WM_DPICHANGED_AFTERPARENT).</summary>
		public event EventHandler DpiChangedAfterParent {
			add { Events.AddHandler (DpiChangedAfterParentEvent, value); }
			remove { Events.RemoveHandler (DpiChangedAfterParentEvent, value); }
		}

		[EditorBrowsable (EditorBrowsableState.Advanced)]
		protected virtual void OnDpiChangedBeforeParent (EventArgs e)
		{
			((EventHandler) Events [DpiChangedBeforeParentEvent])?.Invoke (this, e);
		}

		[EditorBrowsable (EditorBrowsableState.Advanced)]
		protected virtual void OnDpiChangedAfterParent (EventArgs e)
		{
			((EventHandler) Events [DpiChangedAfterParentEvent])?.Invoke (this, e);
		}

		/// <summary>Called when the control's DPI changes, for it to rescale what it keeps in device
		/// pixels (.NET: nothing in the base).</summary>
		[EditorBrowsable (EditorBrowsableState.Advanced)]
		protected virtual void RescaleConstantsForDpi (int deviceDpiOld, int deviceDpiNew)
		{
		}

		internal void RescaleConstantsForDpiInternal (int deviceDpiOld, int deviceDpiNew) => RescaleConstantsForDpi (deviceDpiOld, deviceDpiNew);
	}
}

namespace System.Windows.Forms
{
	/// <summary>The data of a <see cref="Form.DpiChanged"/> event.</summary>
	public sealed class DpiChangedEventArgs : CancelEventArgs
	{
		internal DpiChangedEventArgs (int old, Message m)
		{
			DeviceDpiOld = old;
			DeviceDpiNew = (short) ((long) m.WParam & 0xFFFF);
			var r = m.LParam == IntPtr.Zero ? default (XplatUIWebGpu.DpiRect)
				: System.Runtime.InteropServices.Marshal.PtrToStructure<XplatUIWebGpu.DpiRect> (m.LParam);
			SuggestedRectangle = Rectangle.FromLTRB (r.Left, r.Top, r.Right, r.Bottom);
		}

		public int DeviceDpiOld { get; }

		public int DeviceDpiNew { get; }

		public Rectangle SuggestedRectangle { get; }

		public override string ToString () => $"was: {DeviceDpiOld}, now: {DeviceDpiNew}";
	}

	public delegate void DpiChangedEventHandler (object sender, DpiChangedEventArgs e);
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
