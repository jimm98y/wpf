// The browser head's WinForms message loops. A page's thread cannot block, so Application.Run and
// Form.ShowDialog cannot sit in a GetMessage loop there: each loop is begun and the call returns
// (Application.LoopsReturnAtOnce), as Application.Run does for WPF's dispatcher on the same head.
// This pump then does, a frame at a time, what the loop would have done: timers, the queued messages
// dispatched through the innermost loop (Application.StepLoop), Application.Idle when the queue drains,
// the windows' frames and input (PresentationHost.Tick), and the loop's exit check. A loop that is
// over ends (Application.EndLoop) with whatever followed it -- the rest of ShowDialog -- and the one
// under it carries on.
//
// What cannot be had is a caller that waits: ShowDialog returns DialogResult.None while its dialog is
// up, and the code after it runs at once.

extern alias WindowsBase;

using System.Collections.Generic;
using System.Threading.Tasks;
using BrowserWindow = WindowsBase::MS.Internal.Interop.BrowserWindow;

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

		// innermost last; the page has one thread
		private static readonly List<Loop> s_loops = new List<Loop> ();
		private static bool s_pumping;

		/// <summary>A loop begun by Application.RunLoop: stepped from now on, until it is over.</summary>
		internal static void Begin (Application.LoopState state, Action after)
		{
			s_loops.Add (new Loop { State = state, After = after });
			if (!s_pumping) {
				s_pumping = true;
				_ = PumpAsync ();
			}
		}

		private static async Task PumpAsync ()
		{
			try {
				while (s_loops.Count > 0) {
					// The page's next frame (requestAnimationFrame), as WPF's dispatcher pump waits for it.
					// Not on the captured context: that is WinForms' own once a control exists, which posts
					// the continuation as a message to the very queue this pump is the one to drain.
					await BrowserWindow.NextFrameAsync ().ConfigureAwait (false);
					Round ();
				}
			} finally {
				s_pumping = false;
			}
		}

		/// <summary>One round of the innermost loop; the milliseconds until the next timer (-1: none).</summary>
		private static int Round ()
		{
			int nextTimer = -1;
			try {
				// the page's input since the last frame: each form's host takes its own (BrowserFormHost)
				BrowserWindow.PumpEvents ();
				nextTimer = XplatUIWebGpu.GetInstance ().TickTimers ();
				Loop loop = s_loops [s_loops.Count - 1];
				MSG msg = new MSG ();
				while (XplatUI.PeekMessage (loop.State.QueueId, ref msg, IntPtr.Zero, 0, 0, 0x0001 /* PM_REMOVE */)) {
					if (msg.message == Msg.WM_QUIT)
						loop.State.Quit = true;
					else
						XplatUIWebGpu.NoteMessageHandled ();
					if (loop.State.Quit || !Application.StepLoop (loop.State, ref msg)) {
						End (loop);
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
					End (loop);
			} catch (Exception e) {
				Application.OnThreadException (e);
			}
			return nextTimer;
		}

		private static void End (Loop loop)
		{
			s_loops.Remove (loop);
			Application.EndLoop (loop.State);
			loop.After?.Invoke ();
		}
	}
}
