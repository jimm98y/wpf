// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
//
// Ported from dotnet/winforms (System.Windows.Forms.Design.EventHandlerService): a stack of handlers, the newest
// first; GetHandler answers the newest one of the type asked for (the last answer cached). The Mono stub threw.

using System.Collections.Generic;

namespace System.Windows.Forms.Design
{
	public sealed class EventHandlerService
	{
		private object _lastHandler;
		private Type _lastHandlerType;
		private EventHandler _changedEvent;
		private readonly LinkedList<object> _handlers = new LinkedList<object> ();

		public EventHandlerService (Control focusWnd)
		{
			FocusWindow = focusWnd;
		}

		public event EventHandler EventHandlerChanged {
			add => _changedEvent += value;
			remove => _changedEvent -= value;
		}

		public Control FocusWindow { get; }

		public object GetHandler (Type handlerType)
		{
			if (handlerType == null)
				throw new ArgumentNullException (nameof (handlerType));
			if (_lastHandlerType == null)
				return null;
			if (handlerType == _lastHandlerType)
				return _lastHandler;
			foreach (object handler in _handlers) {
				if (handlerType.IsInstanceOfType (handler)) {
					_lastHandler = handler;
					_lastHandlerType = handlerType;
					return handler;
				}
			}
			return null;
		}

		public void PopHandler (object handler)
		{
			if (handler == null)
				throw new ArgumentNullException (nameof (handler));
			if (_handlers.Remove (handler)) {
				_lastHandler = null;
				_lastHandlerType = null;
				_changedEvent?.Invoke (this, EventArgs.Empty);
			}
		}

		public void PushHandler (object handler)
		{
			if (handler == null)
				throw new ArgumentNullException (nameof (handler));
			_handlers.AddFirst (handler);
			_lastHandlerType = handler.GetType ();
			_lastHandler = handler;
			_changedEvent?.Invoke (this, EventArgs.Empty);
		}
	}
}
