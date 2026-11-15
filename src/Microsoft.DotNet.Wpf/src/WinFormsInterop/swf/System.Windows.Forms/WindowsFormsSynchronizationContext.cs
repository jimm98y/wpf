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
// Copyright (c) 2007 Novell, Inc.
//

using System;
using System.Threading;
using System.ComponentModel;


// Some implementation details:
// http://msdn.microsoft.com/msdnmag/issues/06/06/NETMatters/default.aspx
// The body is .NET's (MIT): a context posts to the thread it was made on, through a control of that thread. Mono's
// posted every thread's work through ONE static control, made by whichever thread first touched the type: a second
// UI thread's awaits and posts went to the first's queue, and were lost once that thread had ended (a splash screen's
// thread, a test's).
namespace System.Windows.Forms
{
	public sealed class WindowsFormsSynchronizationContext : SynchronizationContext, IDisposable
	{
		private Control _controlToSendTo;
		private WeakReference _destinationThread;

		[ThreadStatic] private static bool t_doNotAutoInstall;
		[ThreadStatic] private static bool t_inSyncContextInstallation;
		[ThreadStatic] private static SynchronizationContext t_previousSyncContext;
		// the thread's marshaling control (.NET's ThreadContext.MarshalingControl): one per thread, made on it
		[ThreadStatic] private static Control t_marshalingControl;

		#region Public Constructor
		public WindowsFormsSynchronizationContext ()
		{
			DestinationThread = Thread.CurrentThread;
			_controlToSendTo = MarshalingControl ();
		}

		private WindowsFormsSynchronizationContext (Control marshalingControl, Thread destinationThread)
		{
			_controlToSendTo = marshalingControl;
			DestinationThread = destinationThread;
		}
		#endregion

		private static Control MarshalingControl ()
		{
			Control control = t_marshalingControl;
			if (control == null || control.IsDisposed) {
				bool was = t_inSyncContextInstallation;
				t_inSyncContextInstallation = true;
				try {
					control = new Control ();
					control.CreateControl ();
				} finally {
					t_inSyncContextInstallation = was;
				}
				t_marshalingControl = control;
			}
			return control;
		}

		private Thread DestinationThread {
			get => _destinationThread != null && _destinationThread.IsAlive ? _destinationThread.Target as Thread : null;
			set {
				if (value != null)
					_destinationThread = new WeakReference (value);
			}
		}

		#region Public Properties
		/// <summary>Whether a control created on this thread installs a context where it has none.</summary>
		[EditorBrowsable (EditorBrowsableState.Advanced)]
		public static bool AutoInstall {
			get => !t_doNotAutoInstall;
			set => t_doNotAutoInstall = !value;
		}
		#endregion

		#region Public Methods
		public override SynchronizationContext CreateCopy () => new WindowsFormsSynchronizationContext (_controlToSendTo, DestinationThread);

		public void Dispose ()
		{
			_controlToSendTo = null;
		}

		public override void Post (SendOrPostCallback d, object state)
		{
			_controlToSendTo?.BeginInvoke (d, new object [] { state });
		}

		public override void Send (SendOrPostCallback d, object state)
		{
			Thread destinationThread = DestinationThread;
			if (destinationThread == null || !destinationThread.IsAlive)
				throw new InvalidAsynchronousStateException ("The destination thread no longer exists.");
			_controlToSendTo?.Invoke (d, new object [] { state });
		}

		/// <summary>Puts back the context the thread had before one was installed, and stops installing.</summary>
		public static void Uninstall () => Uninstall (turnOffAutoInstall: true);

		internal static void Uninstall (bool turnOffAutoInstall)
		{
			if (AutoInstall && SynchronizationContext.Current is WindowsFormsSynchronizationContext context) {
				context.Dispose ();
				SynchronizationContext.SetSynchronizationContext (t_previousSyncContext);
				t_previousSyncContext = null;
			}
			if (turnOffAutoInstall)
				AutoInstall = false;
		}

		/// <summary>A control was created: where the thread has no context of its own (none, or the plain base one),
		/// this one goes in. A WPF thread keeps its DispatcherSynchronizationContext.</summary>
		internal static void InstallIfNeeded ()
		{
			if (!AutoInstall || t_inSyncContextInstallation)
				return;
			if (SynchronizationContext.Current == null)
				t_previousSyncContext = null;
			if (t_previousSyncContext != null)
				return;
			t_inSyncContextInstallation = true;
			try {
				SynchronizationContext current = SynchronizationContext.Current;
				if (current == null || current.GetType () == typeof (SynchronizationContext)) {
					t_previousSyncContext = current;
					SynchronizationContext.SetSynchronizationContext (new WindowsFormsSynchronizationContext ());
				}
			} finally {
				t_inSyncContextInstallation = false;
			}
		}
		#endregion
	}
}
