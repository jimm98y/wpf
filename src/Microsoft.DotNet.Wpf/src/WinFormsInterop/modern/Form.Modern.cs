// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// What .NET's Form has that Mono's did not, ported from .NET's Form: the frame's attributes Windows 11's DWM draws
// (FormCornerPreference, FormBorderColor, FormCaptionBackColor, FormCaptionTextColor) and the window's display
// affinity (FormScreenCaptureMode), each handed to the host that has the window (XplatUIWebGpu.FrameAttributesRequested:
// DwmSetWindowAttribute and SetWindowDisplayAffinity on Windows, the window's sharing type on macOS, the frame the
// browser's host draws); ShowAsync and ShowDialogAsync; MdiChildrenMinimizedAnchorBottom.

using System.ComponentModel;
using System.Drawing;
using System.Threading;
using System.Threading.Tasks;

namespace System.Windows.Forms
{
	/// <summary>How the corners of a form's window are rounded (DWM_WINDOW_CORNER_PREFERENCE).</summary>
	public enum FormCornerPreference
	{
		Default = 0,
		DoNotRound = 1,
		Round = 2,
		RoundSmall = 3,
	}

	/// <summary>Whether a form's window shows in screen captures and recordings (its display affinity).</summary>
	public enum ScreenCaptureMode
	{
		Allow = 0,
		HideContent = 1,
		HideWindow = 2,
	}

	public partial class Form
	{
		private static readonly object s_formCornerPreferenceChanged = new object ();
		private static readonly object s_formBorderColorChanged = new object ();
		private static readonly object s_formCaptionBackColorChanged = new object ();
		private static readonly object s_formCaptionTextColorChanged = new object ();

		private FormCornerPreference _cornerPreference;
		private Color _borderColor = Color.Empty, _captionBackColor = Color.Empty, _captionTextColor = Color.Empty;
		private ScreenCaptureMode _screenCaptureMode;
		private readonly object _asyncLock = new object ();
		private TaskCompletionSource _nonModalFormCompletion;
		private TaskCompletionSource<DialogResult> _modalFormCompletion;

		/// <summary>Whether minimized MDI children are lined up along the bottom of the MDI client (as Windows does),
		/// rather than along its top.</summary>
		[MWFCategory ("Window Style")]
		[DefaultValue (true)]
		public bool MdiChildrenMinimizedAnchorBottom { get; set; } = true;

		/// <summary>Whether the window shows in screen captures: hidden, or shown black.</summary>
		[MWFCategory ("Window Style")]
		[DefaultValue (ScreenCaptureMode.Allow)]
		public ScreenCaptureMode FormScreenCaptureMode {
			get => _screenCaptureMode;
			set {
				if (_screenCaptureMode == value)
					return;
				if (!TopLevel && !DesignMode)
					throw new InvalidOperationException ("Screen capture mode can only be set on top-level forms.");
				if (value < ScreenCaptureMode.Allow || value > ScreenCaptureMode.HideWindow)
					throw new InvalidEnumArgumentException (nameof (value), (int) value, typeof (ScreenCaptureMode));
				_screenCaptureMode = value;
				FrameAttributesChanged ();
			}
		}

		private bool ShouldSerializeFormScreenCaptureMode () => _screenCaptureMode != ScreenCaptureMode.Allow;

		private void ResetFormScreenCaptureMode () => FormScreenCaptureMode = ScreenCaptureMode.Allow;

		/// <summary>How the window's corners are rounded.</summary>
		[MWFCategory ("Window Style")]
		[DefaultValue (FormCornerPreference.Default)]
		public FormCornerPreference FormCornerPreference {
			get => _cornerPreference;
			set {
				if (value == _cornerPreference)
					return;
				if (value < FormCornerPreference.Default || value > FormCornerPreference.RoundSmall)
					throw new ArgumentOutOfRangeException (nameof (value));
				_cornerPreference = value;
				FrameAttributesChanged ();
				OnFormCornerPreferenceChanged (EventArgs.Empty);
			}
		}

		public event EventHandler FormCornerPreferenceChanged {
			add { Events.AddHandler (s_formCornerPreferenceChanged, value); }
			remove { Events.RemoveHandler (s_formCornerPreferenceChanged, value); }
		}

		protected virtual void OnFormCornerPreferenceChanged (EventArgs e) =>
			(Events [s_formCornerPreferenceChanged] as EventHandler)?.Invoke (this, e);

		/// <summary>The window's border colour; Empty for the system's. What was set: the window manager is not
		/// asked back.</summary>
		[MWFCategory ("Window Style")]
		public Color FormBorderColor {
			get => _borderColor;
			set {
				if (value == _borderColor)
					return;
				_borderColor = value;
				FrameAttributesChanged ();
				OnFormBorderColorChanged (EventArgs.Empty);
			}
		}

		private bool ShouldSerializeFormBorderColor () => !_borderColor.IsEmpty;

		private void ResetFormBorderColor () => FormBorderColor = Color.Empty;

		public event EventHandler FormBorderColorChanged {
			add { Events.AddHandler (s_formBorderColorChanged, value); }
			remove { Events.RemoveHandler (s_formBorderColorChanged, value); }
		}

		protected virtual void OnFormBorderColorChanged (EventArgs e) =>
			(Events [s_formBorderColorChanged] as EventHandler)?.Invoke (this, e);

		/// <summary>The title bar's background colour; Empty for the system's.</summary>
		[MWFCategory ("Window Style")]
		public Color FormCaptionBackColor {
			get => _captionBackColor;
			set {
				if (value == _captionBackColor)
					return;
				_captionBackColor = value;
				FrameAttributesChanged ();
				OnFormCaptionBackColorChanged (EventArgs.Empty);
			}
		}

		private bool ShouldSerializeFormCaptionBackColor () => !_captionBackColor.IsEmpty;

		private void ResetFormCaptionBackColor () => FormCaptionBackColor = Color.Empty;

		public event EventHandler FormCaptionBackColorChanged {
			add { Events.AddHandler (s_formCaptionBackColorChanged, value); }
			remove { Events.RemoveHandler (s_formCaptionBackColorChanged, value); }
		}

		protected virtual void OnFormCaptionBackColorChanged (EventArgs e) =>
			(Events [s_formCaptionBackColorChanged] as EventHandler)?.Invoke (this, e);

		/// <summary>The title bar's text colour; Empty for the system's.</summary>
		[MWFCategory ("Window Style")]
		public Color FormCaptionTextColor {
			get => _captionTextColor;
			set {
				if (value == _captionTextColor)
					return;
				_captionTextColor = value;
				FrameAttributesChanged ();
				OnFormCaptionTextColorChanged (EventArgs.Empty);
			}
		}

		private bool ShouldSerializeFormCaptionTextColor () => !_captionTextColor.IsEmpty;

		private void ResetFormCaptionTextColor () => FormCaptionTextColor = Color.Empty;

		public event EventHandler FormCaptionTextColorChanged {
			add { Events.AddHandler (s_formCaptionTextColorChanged, value); }
			remove { Events.RemoveHandler (s_formCaptionTextColorChanged, value); }
		}

		protected virtual void OnFormCaptionTextColorChanged (EventArgs e) =>
			(Events [s_formCaptionTextColorChanged] as EventHandler)?.Invoke (this, e);

		/// <summary>Whether any of the frame's attributes is not the system's: what a host need not apply otherwise.</summary>
		internal bool HasFrameAttributes =>
			_cornerPreference != FormCornerPreference.Default || !_borderColor.IsEmpty || !_captionBackColor.IsEmpty
			|| !_captionTextColor.IsEmpty || _screenCaptureMode != ScreenCaptureMode.Allow;

		// what .NET sets on the window when the attribute changes, and when its handle is created (OnHandleCreated)
		private void FrameAttributesChanged ()
		{
			if (IsHandleCreated)
				XplatUIWebGpu.FrameAttributesRequested?.Invoke (this);
		}

		internal void ApplyFrameAttributesOnHandleCreated ()
		{
			if (HasFrameAttributes)
				XplatUIWebGpu.FrameAttributesRequested?.Invoke (this);
		}

		#region ShowAsync
		/// <summary>Shows the form (not modally), the task completing when it is closed.</summary>
		public Task ShowAsync (IWin32Window owner = null)
		{
			lock (_asyncLock) {
				if (_nonModalFormCompletion != null || _modalFormCompletion != null)
					throw new InvalidOperationException ("The form has already been shown asynchronously.");
				_nonModalFormCompletion = new TaskCompletionSource (new WeakReference<Form> (this), TaskCreationOptions.RunContinuationsAsynchronously);
			}

			SynchronizationContext syncContext = SyncContextForAsync ();
			TaskCompletionSource completion = _nonModalFormCompletion;
			syncContext.Post (_ => {
				try {
					Show (owner);
				} catch (Exception ex) {
					completion.TrySetException (ex);
					lock (_asyncLock)
						_nonModalFormCompletion = null;
				}
			}, null);
			return completion.Task;
		}

		/// <summary>Shows the form modally, the task giving its dialog result when it is closed.</summary>
		public Task<DialogResult> ShowDialogAsync () => ShowDialogAsyncInternal (null);

		/// <summary>Shows the form modally over <paramref name="owner"/>, the task giving its dialog result.</summary>
		public Task<DialogResult> ShowDialogAsync (IWin32Window owner) => ShowDialogAsyncInternal (owner);

		private Task<DialogResult> ShowDialogAsyncInternal (IWin32Window owner)
		{
			lock (_asyncLock) {
				if (_nonModalFormCompletion != null || _modalFormCompletion != null)
					throw new InvalidOperationException ("The form has already been shown asynchronously.");
				_modalFormCompletion = new TaskCompletionSource<DialogResult> (new WeakReference<Form> (this), TaskCreationOptions.RunContinuationsAsynchronously);
			}

			SynchronizationContext syncContext = SyncContextForAsync ();
			TaskCompletionSource<DialogResult> completion = _modalFormCompletion;
			syncContext.Post (_ => {
				try {
					completion.TrySetResult (ShowDialog (owner));
				} catch (Exception ex) {
					completion.TrySetException (ex);
				} finally {
					lock (_asyncLock)
						_modalFormCompletion = null;
				}
			}, null);
			return completion.Task;
		}

		private static SynchronizationContext SyncContextForAsync ()
		{
			if (SynchronizationContext.Current == null)
				WindowsFormsSynchronizationContext.InstallIfNeeded ();
			return SynchronizationContext.Current
				?? throw new InvalidOperationException ("No WindowsFormsSynchronizationContext to show the form asynchronously.");
		}

		/// <summary>The form closed (OnFormClosed) or disposed: an awaited ShowAsync ends.</summary>
		internal void CompleteShowAsync ()
		{
			TaskCompletionSource completion;
			lock (_asyncLock) {
				completion = _nonModalFormCompletion;
				_nonModalFormCompletion = null;
			}
			completion?.TrySetResult ();
		}

		/// <summary>An exception from the FormClosed handlers: the awaited ShowAsync's, where there is one.</summary>
		internal bool FailShowAsync (Exception ex)
		{
			TaskCompletionSource completion;
			lock (_asyncLock) {
				completion = _nonModalFormCompletion;
				_nonModalFormCompletion = null;
			}
			return completion != null && completion.TrySetException (ex);
		}
		#endregion
	}

	internal partial class XplatUIWebGpu
	{
		/// <summary>Set by a host: apply the form's frame attributes (corners, border and caption colours, display
		/// affinity) to its real window.</summary>
		internal static Action<Form> FrameAttributesRequested;
	}
}
