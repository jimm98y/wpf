// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// What .NET's Control has that Mono's did not, ported from .NET's Control (Control.cs, Control_InvokeAsync.cs,
// Control.Ime.cs): the ambient DataContext, InvokeAsync, the drag that carries an image and the drag of JSON data,
// IsAncestorSiteInDesignMode, and the ImeMode a control may override (ImeModeBase).

using System.ComponentModel;
using System.Drawing;
using System.Threading;
using System.Threading.Tasks;

namespace System.Windows.Forms
{
	public partial class Control
	{
		private static readonly object s_dataContextEvent = new object ();
		private object _dataContext;
		private bool _hasDataContext;

		/// <summary>The data context for data binding: an ambient property, the parent's unless set.</summary>
		[Browsable (false)]
		[Bindable (true)]
		[MWFCategory ("Data")]
		public virtual object DataContext {
			get => _hasDataContext ? _dataContext : parent?.DataContext;
			set {
				if (Equals (value, DataContext))
					return;

				// Set as the parent's again: inherited from it again.
				if (_hasDataContext && Equals (parent?.DataContext, value)) {
					_hasDataContext = false;
					_dataContext = null;
					OnDataContextChanged (EventArgs.Empty);
					return;
				}

				_dataContext = value;
				_hasDataContext = true;
				OnDataContextChanged (EventArgs.Empty);
			}
		}

		private bool ShouldSerializeDataContext () => _hasDataContext;

		private void ResetDataContext ()
		{
			_hasDataContext = false;
			_dataContext = null;
		}

		[MWFCategory ("Data")]
		[Browsable (true)]
		[EditorBrowsable (EditorBrowsableState.Advanced)]
		public event EventHandler DataContextChanged {
			add { Events.AddHandler (s_dataContextEvent, value); }
			remove { Events.RemoveHandler (s_dataContextEvent, value); }
		}

		[EditorBrowsable (EditorBrowsableState.Advanced)]
		protected virtual void OnDataContextChanged (EventArgs e)
		{
			if (AnyDisposingInHierarchy ())
				return;

			(Events [s_dataContextEvent] as EventHandler)?.Invoke (this, e);

			if (child_controls != null) {
				for (int i = 0; i < child_controls.Count; i++)
					child_controls [i].OnParentDataContextChanged (e);
			}
		}

		[EditorBrowsable (EditorBrowsableState.Advanced)]
		protected virtual void OnParentDataContextChanged (EventArgs e)
		{
			if (_hasDataContext) {
				// The same as the parent's now: inherited from it again, and the value seen has not changed.
				if (Equals (_dataContext, parent?.DataContext)) {
					_hasDataContext = false;
					_dataContext = null;
				}
				return;
			}

			OnDataContextChanged (e);
		}

		/// <summary>Whether an ancestor of this control is sited, and its site is in design mode.</summary>
		[Browsable (false)]
		[EditorBrowsable (EditorBrowsableState.Advanced)]
		[DesignerSerializationVisibility (DesignerSerializationVisibility.Hidden)]
		public bool IsAncestorSiteInDesignMode =>
			SitedParentSite (this) is ISite parentSite && parentSite.DesignMode;

		private static ISite SitedParentSite (Control control)
		{
			if (control == null)
				throw new ArgumentNullException (nameof (control));
			return (control.Site != null && control.Site.DesignMode) || control.Parent == null
				? control.Site
				: SitedParentSite (control.Parent);
		}

		#region InvokeAsync
		private const string NoMarshalingThread = "Invoke or BeginInvoke cannot be called on a control until the window handle has been created.";

		/// <summary>Runs <paramref name="callback"/> on the control's thread, the task completing when it has.</summary>
		public async Task InvokeAsync (Action callback, CancellationToken cancellationToken = default)
		{
			if (callback == null)
				throw new ArgumentNullException (nameof (callback));
			if (!IsHandleCreated)
				throw new InvalidOperationException (NoMarshalingThread);
			if (cancellationToken.IsCancellationRequested)
				return;

			var completion = new TaskCompletionSource (TaskCreationOptions.RunContinuationsAsynchronously);
			using (cancellationToken.Register (() => completion.TrySetCanceled (cancellationToken), useSynchronizationContext: false)) {
				BeginInvoke ((Action) WrappedAction);
				await completion.Task.ConfigureAwait (false);
			}

			void WrappedAction ()
			{
				try {
					if (cancellationToken.IsCancellationRequested) {
						completion.TrySetCanceled (cancellationToken);
						return;
					}
					callback ();
					completion.TrySetResult ();
				} catch (Exception ex) {
					DelegateFailed (completion, ex, cancellationToken);
				}
			}
		}

		/// <summary>Runs <paramref name="callback"/> on the control's thread, the task giving what it returns.</summary>
		public async Task<T> InvokeAsync<T> (Func<T> callback, CancellationToken cancellationToken = default)
		{
			if (callback == null)
				throw new ArgumentNullException (nameof (callback));
			if (!IsHandleCreated)
				throw new InvalidOperationException (NoMarshalingThread);
			if (cancellationToken.IsCancellationRequested)
				return default;

			var completion = new TaskCompletionSource<T> (TaskCreationOptions.RunContinuationsAsynchronously);
			using (cancellationToken.Register (() => completion.TrySetCanceled (cancellationToken), useSynchronizationContext: false)) {
				BeginInvoke ((Action) WrappedCallback);
				return await completion.Task.ConfigureAwait (false);
			}

			void WrappedCallback ()
			{
				try {
					if (cancellationToken.IsCancellationRequested) {
						completion.TrySetCanceled (cancellationToken);
						return;
					}
					completion.TrySetResult (callback ());
				} catch (Exception ex) {
					DelegateFailed (completion, ex, cancellationToken);
				}
			}
		}

		/// <summary>Starts <paramref name="callback"/> on the control's thread, the task completing when what it
		/// returns has.</summary>
		public async Task InvokeAsync (Func<CancellationToken, ValueTask> callback, CancellationToken cancellationToken = default)
		{
			if (callback == null)
				throw new ArgumentNullException (nameof (callback));
			if (!IsHandleCreated)
				throw new InvalidOperationException (NoMarshalingThread);
			if (cancellationToken.IsCancellationRequested)
				return;

			var completion = new TaskCompletionSource (TaskCreationOptions.RunContinuationsAsynchronously);
			CancellationTokenRegistration registration = cancellationToken.Register (
				() => completion.TrySetCanceled (cancellationToken), useSynchronizationContext: false);

			BeginInvoke ((Action) (async () => await WrappedCallbackAsync ().ConfigureAwait (false)));
			await completion.Task.ConfigureAwait (false);

			async Task WrappedCallbackAsync ()
			{
				try {
					using (registration) {
						if (cancellationToken.IsCancellationRequested) {
							completion.TrySetCanceled (cancellationToken);
							return;
						}
						await callback (cancellationToken).ConfigureAwait (false);
						completion.TrySetResult ();
					}
				} catch (Exception ex) {
					DelegateFailed (completion, ex, cancellationToken);
				}
			}
		}

		/// <summary>Starts <paramref name="callback"/> on the control's thread, the task giving what what it returns
		/// gives.</summary>
		public async Task<T> InvokeAsync<T> (Func<CancellationToken, ValueTask<T>> callback, CancellationToken cancellationToken = default)
		{
			if (callback == null)
				throw new ArgumentNullException (nameof (callback));
			if (!IsHandleCreated)
				throw new InvalidOperationException (NoMarshalingThread);
			if (cancellationToken.IsCancellationRequested)
				return default;

			var completion = new TaskCompletionSource<T> (TaskCreationOptions.RunContinuationsAsynchronously);
			CancellationTokenRegistration registration = cancellationToken.Register (
				() => completion.TrySetCanceled (cancellationToken), useSynchronizationContext: false);

			BeginInvoke ((Action) (async () => await WrappedCallbackAsync ().ConfigureAwait (false)));
			return await completion.Task.ConfigureAwait (false);

			async Task WrappedCallbackAsync ()
			{
				try {
					using (registration) {
						if (cancellationToken.IsCancellationRequested) {
							completion.TrySetCanceled (cancellationToken);
							return;
						}
						completion.TrySetResult (await callback (cancellationToken).ConfigureAwait (false));
					}
				} catch (Exception ex) {
					DelegateFailed (completion, ex, cancellationToken);
				}
			}
		}

		private static void DelegateFailed (TaskCompletionSource completion, Exception ex, CancellationToken cancellationToken)
		{
			if (ex is OperationCanceledException oce && oce.CancellationToken == cancellationToken)
				completion.TrySetCanceled (cancellationToken);
			else
				completion.TrySetException (ex);
		}

		private static void DelegateFailed<T> (TaskCompletionSource<T> completion, Exception ex, CancellationToken cancellationToken)
		{
			if (ex is OperationCanceledException oce && oce.CancellationToken == cancellationToken)
				completion.TrySetCanceled (cancellationToken);
			else
				completion.TrySetException (ex);
		}
		#endregion

		#region Drag and drop
		/// <summary>The image a drag started by <see cref="DoDragDrop(object, DragDropEffects, Bitmap, Point, bool)"/>
		/// shows, the GiveFeedback event's to change; null for none.</summary>
		internal DragImageState CurrentDragImage;

		internal sealed class DragImageState
		{
			public Bitmap Image;
			public Point CursorOffset;
			public bool UseDefault;
		}

		/// <summary>Begins a drag of <paramref name="data"/>, showing <paramref name="dragImage"/> with the cursor at
		/// <paramref name="cursorOffset"/> in it.</summary>
		public DragDropEffects DoDragDrop (object data, DragDropEffects allowedEffects, Bitmap dragImage, Point cursorOffset, bool useDefaultDragImage)
		{
			CurrentDragImage = dragImage == null && !useDefaultDragImage ? null
				: new DragImageState { Image = dragImage, CursorOffset = cursorOffset, UseDefault = useDefaultDragImage };
			try {
				return DoDragDrop (data, allowedEffects);
			} finally {
				CurrentDragImage = null;
			}
		}

		/// <summary>Begins a drag of <paramref name="data"/>, stored as JSON unless it is a type the clipboard
		/// handles itself.</summary>
		public DragDropEffects DoDragDropAsJson<T> (T data, DragDropEffects allowedEffects) =>
			DoDragDropAsJson (data, allowedEffects, dragImage: null, cursorOffset: default, useDefaultDragImage: false);

		/// <inheritdoc cref="DoDragDropAsJson{T}(T, DragDropEffects)"/>
		public DragDropEffects DoDragDropAsJson<T> (T data, DragDropEffects allowedEffects, Bitmap dragImage, Point cursorOffset, bool useDefaultDragImage)
		{
			if (data == null)
				throw new ArgumentNullException (nameof (data));
			if (typeof (T) == typeof (DataObject))
				throw new InvalidOperationException (string.Format (JsonData.CannotJsonSerializeDataObject, nameof (DoDragDrop)));

			DataObject dataObject = new DataObject ();
			dataObject.SetDataAsJson (data);
			return DoDragDrop (dataObject, allowedEffects, dragImage, cursorOffset, useDefaultDragImage);
		}
		#endregion

		#region IME
		/// <summary>The control's IME mode as it is kept: what <see cref="ImeMode"/> gives (OnHalf as On) and sets,
		/// for a control to override (a read-only text box's is Disable).</summary>
		protected virtual ImeMode ImeModeBase {
			get {
				if (ime_mode == ImeMode.Inherit)
					return parent != null ? parent.ImeMode : ImeMode.NoControl;
				return ime_mode;
			}
			set {
				if (value < ImeMode.Inherit || value > ImeMode.OnHalf)
					throw new InvalidEnumArgumentException (nameof (value), (int) value, typeof (ImeMode));
				if (ime_mode != value) {
					ime_mode = value;
					OnImeModeChanged (EventArgs.Empty);
				}
			}
		}

		private static ImeMode s_propagatingImeMode = ImeMode.Inherit;

		/// <summary>The IME mode of a control whose own is NoControl: changed only by the user, it carries the mode
		/// the input method was left in from control to control.</summary>
		protected static ImeMode PropagatingImeMode {
			get {
				if (s_propagatingImeMode == ImeMode.Inherit) {
					// the focused window's, as the IME context Windows keeps for it
					ImeMode imeMode = ImeMode.Inherit;
					IntPtr focus = XplatUI.GetFocus ();
					if (focus != IntPtr.Zero)
						imeMode = ImeContext.GetImeMode (focus);
					PropagatingImeMode = imeMode;
				}
				return s_propagatingImeMode;
			}
			private set {
				switch (value) {
				case ImeMode.NoControl:
				case ImeMode.Disable:
					// never what is propagated
					return;
				}
				s_propagatingImeMode = value;
			}
		}
		#endregion
	}
}
