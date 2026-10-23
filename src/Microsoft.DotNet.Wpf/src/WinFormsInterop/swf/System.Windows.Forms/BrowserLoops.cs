// The browser head's WinForms message loops. The app's thread there is not free to sit in a GetMessage
// loop: on the single-threaded runtime it is the page's own, which must return to the browser for
// anything to happen, and on either runtime it is the thread whose JavaScript event loop delivers the
// page's frames and every other JS callback (JSSynchronizationContext). So Application.Run's loop is
// begun and the call returns, as Application.Run does for WPF's dispatcher on the same head, and this
// pump does, a frame at a time, what the loop would have done: timers, the queued messages dispatched
// through the innermost loop (Application.StepLoop), Application.Idle when the queue drains, the
// windows' frames and input (PresentationHost.Tick), and the loop's exit check. A loop that is over ends
// (Application.EndLoop) with whatever followed it -- the rest of ShowDialog -- and the one under it
// carries on.
//
// A modal loop has a caller that waits for it: ShowDialog returns the dialog's result, and the code
// after it reads it (a login dialog's credentials). On the multithreaded runtime the app's code runs on
// a worker (the runtime's "deputy" thread), which may block, so a modal loop runs there to its end, as
// on Windows (RunBlocking): the same rounds, paced by a short wait instead of the page's frame, and with
// the thread's JS queue drained between them -- the page's frames for WPF's dispatcher, async
// continuations, the WebSocket a dialog talks through -- as Windows' modal loop dispatches everything
// sent to the thread. On the single-threaded runtime ShowDialog cannot wait: it returns
// DialogResult.None while its dialog is up, and the code after it runs at once.

extern alias WindowsBase;

using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using BrowserWindow = WindowsBase::MS.Internal.Interop.BrowserWindow;
using BrowserBlocking = WindowsBase::MS.Internal.Interop.BrowserBlocking;

namespace System.Windows.Forms
{
	[System.Runtime.Versioning.SupportedOSPlatform ("browser")]
	internal static class BrowserLoops
	{
		private sealed class Loop
		{
			public Application.LoopState State;
			public Action After;
		}

		// innermost last; the app has one UI thread
		private static readonly List<Loop> s_loops = new List<Loop> ();
		private static bool s_pumping;
		// modal loops running to their end on this thread (RunBlocking): the frame pump stands aside
		private static int s_blocking;

		private static readonly bool s_trace = Environment.GetEnvironmentVariable ("WF_TRACE_BROWSER") == "1";
		private static int s_rounds;

		/// <summary>Whether a modal loop may block: the multithreaded runtime, on which the app's code
		/// runs on a worker.</summary>
		internal static bool CanBlock => BrowserBlocking.CanBlock;

		/// <summary>A loop begun by Application.RunLoop: stepped from now on, until it is over.</summary>
		internal static void Begin (Application.LoopState state, Action after)
		{
			if (s_trace)
				Console.WriteLine ($"[browser loops] begin {(state.Modal ? "modal" : "main")} loop on thread {Environment.CurrentManagedThreadId}, context {SynchronizationContext.Current?.GetType ().Name ?? "none"}");
			BrowserBlocking.NoteContext ();
			s_loops.Add (new Loop { State = state, After = after });
			if (!s_pumping) {
				s_pumping = true;
				_ = PumpAsync ();
			}
		}

		/// <summary>A modal loop run to its end on this thread (CanBlock); its ending is the caller's.</summary>
		internal static void RunBlocking (Application.LoopState state)
		{
			if (s_trace)
				Console.WriteLine ($"[browser loops] modal loop runs to its end on thread {Environment.CurrentManagedThreadId}");
			var loop = new Loop { State = state };
			s_loops.Add (loop);
			s_blocking++;
			try {
				while (s_loops.Contains (loop)) {
					int nextTimer = Round (loop, endsHere: false);
					if (!s_loops.Contains (loop))
						break;
					try {
						BrowserBlocking.DrainJSQueue ();
					} catch (Exception e) {
						Application.OnThreadException (e);
					}
					if (!s_loops.Contains (loop))
						break;
					// about a frame, or less when a timer is due sooner
					BrowserBlocking.Wait (nextTimer < 0 ? 16 : Math.Clamp (nextTimer, 1, 16));
				}
			} finally {
				s_blocking--;
				s_loops.Remove (loop);
			}
			Application.EndLoop (state);
		}

		private static async Task PumpAsync ()
		{
			try {
				while (s_loops.Count > 0) {
					// The page's next frame (requestAnimationFrame), as WPF's dispatcher pump waits for it, on
					// the page's thread. Not on the captured context: that is WinForms' own once a control
					// exists, which posts the continuation as a message to the very queue this pump drains.
					if (s_trace && s_rounds < 3)
						Console.WriteLine ($"[browser loops] waiting on thread {Environment.CurrentManagedThreadId}, context {SynchronizationContext.Current?.GetType ().FullName ?? "none"}");
					await BrowserWindow.NextFrameOnPage ();
					// a modal loop running to its end steps the loops itself, and this frame arrived
					// through its drain of the JS queue (DrainJSQueue): not again from in there
					if (s_blocking > 0)
						continue;
					Round (s_loops [s_loops.Count - 1], endsHere: true);
				}
			} finally {
				s_pumping = false;
			}
		}

		/// <summary>One round of <paramref name="loop"/>, the innermost; the milliseconds until the next
		/// timer (-1: none). A loop begun by Begin ends here when it is over (<paramref name="endsHere"/>);
		/// one run by RunBlocking is only taken off the list, and its runner ends it.</summary>
		private static int Round (Loop loop, bool endsHere)
		{
			if (s_trace && s_rounds++ < 3)
				Console.WriteLine ($"[browser loops] round {s_rounds} on thread {Environment.CurrentManagedThreadId}");
			int nextTimer = -1;
			try {
				// the page's input since the last frame: each form's host takes its own (BrowserFormHost)
				BrowserWindow.PumpEvents ();
				nextTimer = XplatUIWebGpu.GetInstance ().TickTimers ();
				MSG msg = new MSG ();
				while (XplatUI.PeekMessage (loop.State.QueueId, ref msg, IntPtr.Zero, 0, 0, 0x0001 /* PM_REMOVE */)) {
					if (msg.message == Msg.WM_QUIT)
						loop.State.Quit = true;
					else
						XplatUIWebGpu.NoteMessageHandled ();
					if (loop.State.Quit || !Application.StepLoop (loop.State, ref msg)) {
						Over (loop, endsHere);
						return nextTimer;
					}
				}
				if (XplatUIWebGpu.TakeIdleOwed ())
					XplatUI.RaiseIdle (EventArgs.Empty);
				PresentationHost.Tick ();

				// The heartbeat GetMessage hands a loop when nothing is queued: the host delivers input
				// by calling WndProc directly, so no message passes through the loop, and a dialog whose
				// button set DialogResult is only noticed by the exit check that follows a message.
				msg = new MSG { hwnd = IntPtr.Zero, message = Msg.WM_NULL };
				if (Application.CurrentLoopFormGone () || !Application.StepLoop (loop.State, ref msg) || loop.State.Quit)
					Over (loop, endsHere);
			} catch (Exception e) {
				Application.OnThreadException (e);
			}
			return nextTimer;
		}

		private static void Over (Loop loop, bool endsHere)
		{
			s_loops.Remove (loop);
			if (!endsHere)
				return;
			Application.EndLoop (loop.State);
			loop.After?.Invoke ();
		}
	}
}
