// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// A button's and a tool strip item's ICommand (Command, CommandParameter), ported from .NET's
// ICommandBindingTargetProvider: the command enables the component while it can execute and is executed on a click;
// and BindableComponent, the bindable base .NET's ToolStripItem has (BindingContext, DataBindings).

using System.ComponentModel;
using System.Drawing;
using System.Windows.Input;

namespace System.Windows.Forms
{
	/// <summary>A component with a command: what ButtonBase and ToolStripItem share.</summary>
	internal interface ICommandBindingTargetProvider
	{
		ICommand Command { get; }
		object CommandParameter { get; }
		bool Enabled { get; set; }
		bool? PreviousEnabledStatus { get; set; }
		void RaiseCommandChanged (EventArgs e);
		void RaiseCommandCanExecuteChanged (EventArgs e);
	}

	internal static class CommandBinding
	{
		/// <summary>Sets <paramref name="field"/> to <paramref name="command"/>: listening to whether it can execute,
		/// enabling the component by it, and giving back the Enabled it had when the command goes.</summary>
		public static void Set (ICommandBindingTargetProvider component, ICommand command, ref ICommand field, EventHandler canExecuteChanged)
		{
			if (Equals (field, command))
				return;

			if (field != null) {
				field.CanExecuteChanged -= canExecuteChanged;
				if (command == null && component.PreviousEnabledStatus.HasValue) {
					component.Enabled = component.PreviousEnabledStatus.Value;
					component.PreviousEnabledStatus = null;
				}
			}

			field = command;
			component.RaiseCommandChanged (EventArgs.Empty);

			if (field == null)
				return;

			field.CanExecuteChanged += canExecuteChanged;
			component.PreviousEnabledStatus ??= component.Enabled;
			component.Enabled = field.CanExecute (component.CommandParameter);
		}

		public static void CanExecuteChanged (ICommandBindingTargetProvider component, EventArgs e)
		{
			component.RaiseCommandCanExecuteChanged (e);
			component.Enabled = component.Command?.CanExecute (component.CommandParameter) ?? false;
		}

		public static void Execute (ICommandBindingTargetProvider component) =>
			component.Command?.Execute (component.CommandParameter);
	}

	/// <summary>A component that takes part in data binding: a binding context and its bindings.</summary>
	public abstract class BindableComponent : Component, IBindableComponent
	{
		internal static readonly object s_bindingContextChangedEvent = new object ();

		private ControlBindingsCollection _dataBindings;
		private BindingContext _bindingContext;

		[Browsable (false)]
		[EditorBrowsable (EditorBrowsableState.Advanced)]
		[DesignerSerializationVisibility (DesignerSerializationVisibility.Hidden)]
		public BindingContext BindingContext {
			get => _bindingContext ??= new BindingContext ();
			set {
				if (!Equals (_bindingContext, value)) {
					_bindingContext = value;
					OnBindingContextChanged (EventArgs.Empty);
				}
			}
		}

		[EditorBrowsable (EditorBrowsableState.Advanced)]
		public event EventHandler BindingContextChanged {
			add { Events.AddHandler (s_bindingContextChangedEvent, value); }
			remove { Events.RemoveHandler (s_bindingContextChangedEvent, value); }
		}

		[EditorBrowsable (EditorBrowsableState.Advanced)]
		protected virtual void OnBindingContextChanged (EventArgs e)
		{
			if (_bindingContext != null) {
				for (int i = 0; i < DataBindings.Count; i++)
					BindingContext.UpdateBinding (BindingContext, DataBindings [i]);
			}
			(Events [s_bindingContextChangedEvent] as EventHandler)?.Invoke (this, e);
		}

		[DesignerSerializationVisibility (DesignerSerializationVisibility.Content)]
		[RefreshProperties (RefreshProperties.All)]
		[ParenthesizePropertyName (true)]
		public ControlBindingsCollection DataBindings => _dataBindings ??= new ControlBindingsCollection (this);
	}

	public abstract partial class ButtonBase : ICommandBindingTargetProvider
	{
		private static readonly object s_commandChangedEvent = new object ();
		private static readonly object s_commandCanExecuteChangedEvent = new object ();
		private static readonly object s_commandParameterChangedEvent = new object ();
		private ICommand _command;
		private object _commandParameter;

		/// <summary>The command executed when the button is clicked, enabling the button while it can execute.</summary>
		[Bindable (true)]
		[Browsable (false)]
		[DesignerSerializationVisibility (DesignerSerializationVisibility.Hidden)]
		[MWFCategory ("Data")]
		public ICommand Command {
			get => _command;
			set => CommandBinding.Set (this, value, ref _command, OnCommandCanExecuteChangedProc);
		}

		private void OnCommandCanExecuteChangedProc (object sender, EventArgs e) => CommandBinding.CanExecuteChanged (this, e);

		[MWFCategory ("Data")]
		[EditorBrowsable (EditorBrowsableState.Advanced)]
		public event EventHandler CommandCanExecuteChanged {
			add { Events.AddHandler (s_commandCanExecuteChangedEvent, value); }
			remove { Events.RemoveHandler (s_commandCanExecuteChangedEvent, value); }
		}

		[MWFCategory ("Data")]
		[EditorBrowsable (EditorBrowsableState.Advanced)]
		public event EventHandler CommandChanged {
			add { Events.AddHandler (s_commandChangedEvent, value); }
			remove { Events.RemoveHandler (s_commandChangedEvent, value); }
		}

		/// <summary>The parameter the command is executed with.</summary>
		[Bindable (true)]
		[MWFCategory ("Data")]
		[TypeConverter (typeof (StringConverter))]
		public object CommandParameter {
			get => _commandParameter;
			set {
				if (!Equals (_commandParameter, value)) {
					_commandParameter = value;
					OnCommandParameterChanged (EventArgs.Empty);
				}
			}
		}

		private void ResetCommandParameter () => CommandParameter = null;

		private bool ShouldSerializeCommandParameter () => _commandParameter is string parameter && !string.IsNullOrEmpty (parameter);

		[MWFCategory ("Data")]
		[EditorBrowsable (EditorBrowsableState.Advanced)]
		public event EventHandler CommandParameterChanged {
			add { Events.AddHandler (s_commandParameterChangedEvent, value); }
			remove { Events.RemoveHandler (s_commandParameterChangedEvent, value); }
		}

		protected override void OnClick (EventArgs e)
		{
			base.OnClick (e);
			OnRequestCommandExecute (e);
		}

		[EditorBrowsable (EditorBrowsableState.Advanced)]
		protected virtual void OnCommandChanged (EventArgs e) => (Events [s_commandChangedEvent] as EventHandler)?.Invoke (this, e);

		[EditorBrowsable (EditorBrowsableState.Advanced)]
		protected virtual void OnCommandCanExecuteChanged (EventArgs e) => (Events [s_commandCanExecuteChangedEvent] as EventHandler)?.Invoke (this, e);

		[EditorBrowsable (EditorBrowsableState.Advanced)]
		protected virtual void OnCommandParameterChanged (EventArgs e) => (Events [s_commandParameterChangedEvent] as EventHandler)?.Invoke (this, e);

		/// <summary>Executes the command, if there is one.</summary>
		[EditorBrowsable (EditorBrowsableState.Advanced)]
		protected virtual void OnRequestCommandExecute (EventArgs e) => CommandBinding.Execute (this);

		bool? ICommandBindingTargetProvider.PreviousEnabledStatus { get; set; }
		void ICommandBindingTargetProvider.RaiseCommandChanged (EventArgs e) => OnCommandChanged (e);
		void ICommandBindingTargetProvider.RaiseCommandCanExecuteChanged (EventArgs e) => OnCommandCanExecuteChanged (e);
	}

	public abstract partial class ToolStripItem : ICommandBindingTargetProvider
	{
		private static readonly object s_commandChangedEvent = new object ();
		private static readonly object s_commandCanExecuteChangedEvent = new object ();
		private static readonly object s_commandParameterChangedEvent = new object ();
		private static readonly object s_selectedChangedEvent = new object ();
		private static readonly object s_internalEnabledChangedEvent = new object ();
		private ICommand _command;
		private object _commandParameter;

		/// <summary>The command executed when the item is clicked, enabling the item while it can execute.</summary>
		[Bindable (true)]
		[Browsable (false)]
		[DesignerSerializationVisibility (DesignerSerializationVisibility.Hidden)]
		[MWFCategory ("Data")]
		public ICommand Command {
			get => _command;
			set => CommandBinding.Set (this, value, ref _command, OnCommandCanExecuteChangedProc);
		}

		private void OnCommandCanExecuteChangedProc (object sender, EventArgs e) => CommandBinding.CanExecuteChanged (this, e);

		[MWFCategory ("Data")]
		[EditorBrowsable (EditorBrowsableState.Advanced)]
		public event EventHandler CommandCanExecuteChanged {
			add { Events.AddHandler (s_commandCanExecuteChangedEvent, value); }
			remove { Events.RemoveHandler (s_commandCanExecuteChangedEvent, value); }
		}

		[MWFCategory ("Data")]
		[EditorBrowsable (EditorBrowsableState.Advanced)]
		public event EventHandler CommandChanged {
			add { Events.AddHandler (s_commandChangedEvent, value); }
			remove { Events.RemoveHandler (s_commandChangedEvent, value); }
		}

		/// <summary>The parameter the command is executed with.</summary>
		[Bindable (true)]
		[MWFCategory ("Data")]
		[TypeConverter (typeof (StringConverter))]
		public object CommandParameter {
			get => _commandParameter;
			set {
				if (!Equals (_commandParameter, value)) {
					_commandParameter = value;
					OnCommandParameterChanged (EventArgs.Empty);
				}
			}
		}

		private void ResetCommandParameter () => CommandParameter = null;

		private bool ShouldSerializeCommandParameter () => _commandParameter is string parameter && !string.IsNullOrEmpty (parameter);

		[MWFCategory ("Data")]
		[EditorBrowsable (EditorBrowsableState.Advanced)]
		public event EventHandler CommandParameterChanged {
			add { Events.AddHandler (s_commandParameterChangedEvent, value); }
			remove { Events.RemoveHandler (s_commandParameterChangedEvent, value); }
		}

		[EditorBrowsable (EditorBrowsableState.Advanced)]
		protected virtual void OnCommandChanged (EventArgs e) => (Events [s_commandChangedEvent] as EventHandler)?.Invoke (this, e);

		[EditorBrowsable (EditorBrowsableState.Advanced)]
		protected virtual void OnCommandCanExecuteChanged (EventArgs e) => (Events [s_commandCanExecuteChangedEvent] as EventHandler)?.Invoke (this, e);

		[EditorBrowsable (EditorBrowsableState.Advanced)]
		protected virtual void OnCommandParameterChanged (EventArgs e) => (Events [s_commandParameterChangedEvent] as EventHandler)?.Invoke (this, e);

		/// <summary>Executes the command, if there is one.</summary>
		[EditorBrowsable (EditorBrowsableState.Advanced)]
		protected virtual void OnRequestCommandExecute (EventArgs e) => CommandBinding.Execute (this);

		bool? ICommandBindingTargetProvider.PreviousEnabledStatus { get; set; }
		void ICommandBindingTargetProvider.RaiseCommandChanged (EventArgs e) => OnCommandChanged (e);
		void ICommandBindingTargetProvider.RaiseCommandCanExecuteChanged (EventArgs e) => OnCommandCanExecuteChanged (e);

		/// <summary>Occurs when the item becomes selected or stops being (hover, keyboard, a menu opening).</summary>
		public event EventHandler SelectedChanged {
			add { Events.AddHandler (s_selectedChangedEvent, value); }
			remove { Events.RemoveHandler (s_selectedChangedEvent, value); }
		}

		protected virtual void OnSelectedChanged (EventArgs e) => (Events [s_selectedChangedEvent] as EventHandler)?.Invoke (this, e);

		internal event EventHandler InternalEnabledChanged {
			add { Events.AddHandler (s_internalEnabledChangedEvent, value); }
			remove { Events.RemoveHandler (s_internalEnabledChangedEvent, value); }
		}

		internal void OnInternalEnabledChanged (EventArgs e) => (Events [s_internalEnabledChangedEvent] as EventHandler)?.Invoke (this, e);

		/// <summary>The renderer of the tool strip the item is on.</summary>
		protected internal virtual ToolStripRenderer Renderer => Owner != null ? Owner.Renderer : Parent?.Renderer;

		/// <summary>Begins a drag of <paramref name="data"/>, showing <paramref name="dragImage"/>.</summary>
		public DragDropEffects DoDragDrop (object data, DragDropEffects allowedEffects, Bitmap dragImage, Point cursorOffset, bool useDefaultDragImage)
		{
			Control host = Parent ?? (Control) Owner;
			if (host == null)
				return DoDragDrop (data, allowedEffects);
			host.CurrentDragImage = dragImage == null && !useDefaultDragImage ? null
				: new Control.DragImageState { Image = dragImage, CursorOffset = cursorOffset, UseDefault = useDefaultDragImage };
			try {
				return DoDragDrop (data, allowedEffects);
			} finally {
				host.CurrentDragImage = null;
			}
		}
	}
}
