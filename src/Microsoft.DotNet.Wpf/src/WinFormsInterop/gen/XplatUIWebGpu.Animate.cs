// AnimateWindow: showing or hiding a window with the roll, slide, centre or fade user32 animates
// it with. Win32 renders each frame from the window's own painting; here the window's composited
// scene -- and its children's, which are scenes of their own -- is revealed by a clip that grows
// from an edge (roll), the same clip with the content moving in behind it (slide), a clip growing
// from the middle (centre), or an opacity (blend).
//
// AnimateWindow blocks for the length of the animation in Win32. Here it starts it and returns:
// the frames are presented by the host as it presents anything else, and a window being hidden
// is hidden when its animation ends. The animation runs by the clock, as user32's does: the host
// is told when one starts and presents it from a timer of its own until it ends, whatever loop is
// pumping and whether or not anything else happens -- and presents the finished picture again for
// a moment after, as a frame presented once does not always reach the screen.
using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;
using Microsoft.Wpf.Interop.WebGpu.Composition;

namespace System.Windows.Forms {
	internal partial class XplatUIWebGpu {
		private const int AW_HOR_POSITIVE = 0x1, AW_HOR_NEGATIVE = 0x2, AW_VER_POSITIVE = 0x4, AW_VER_NEGATIVE = 0x8,
		                  AW_CENTER = 0x10, AW_HIDE = 0x10000, AW_ACTIVATE = 0x20000, AW_SLIDE = 0x40000, AW_BLEND = 0x80000;

		private sealed class Reveal {
			public long Start;
			public int Duration;
			public int Flags;
		}

		private readonly Dictionary<IntPtr, Reveal> _reveals = new Dictionary<IntPtr, Reveal>();
		private int _animationsEnded;

		/// <summary>Whether an AnimateWindow is in progress (the host keeps presenting while one is).</summary>
		internal bool Animating => _reveals.Count > 0;

		/// <summary>How many animations have run to their end; the host presents the finished picture
		/// again for a while each time this moves.</summary>
		internal int AnimationsEnded => _animationsEnded;
		private static readonly Stopwatch s_revealClock = Stopwatch.StartNew();

		/// <summary>user32's AnimateWindow: false, as there, when the window is already shown (or
		/// hidden) or the flags ask for nothing it can do.</summary>
		internal bool AnimateWindow(IntPtr handle, int time, int flags)
		{
			Hwnd hwnd = Hwnd.ObjectFromHandle(handle);
			if (hwnd == null) return false;
			bool hide = (flags & AW_HIDE) != 0;
			T($"AnimateWindow h=0x{handle.ToInt64():x} time={time} flags=0x{flags:x} visible={hwnd.visible}");
			if (hide ? !hwnd.visible : hwnd.visible) return false;
			// Roll and slide need a direction; centre and blend ignore it.
			bool directed = (flags & (AW_HOR_POSITIVE | AW_HOR_NEGATIVE | AW_VER_POSITIVE | AW_VER_NEGATIVE)) != 0;
			bool animates = (flags & (AW_CENTER | AW_BLEND)) != 0 || directed;
			if (animates)
				_reveals[handle] = new Reveal { Start = s_revealClock.ElapsedMilliseconds, Duration = time > 0 ? time : 200, Flags = flags };
			if (!hide) SetVisible(handle, true, (flags & AW_ACTIVATE) != 0);
			else if (!animates) SetVisible(handle, false, false);
			_paintVersion++;
			if (animates) PresentationHost.AnimationStarted();
			return true;
		}

		/// <summary>Ends the animations that have run their time (hiding a window that was being
		/// hidden), and keeps the host presenting while any is still running.</summary>
		private void AdvanceReveals()
		{
			if (_reveals.Count == 0) return;
			long now = s_revealClock.ElapsedMilliseconds;
			foreach (KeyValuePair<IntPtr, Reveal> r in new List<KeyValuePair<IntPtr, Reveal>>(_reveals))
			{
				if (now - r.Value.Start < r.Value.Duration && Hwnd.ObjectFromHandle(r.Key) != null) continue;
				_reveals.Remove(r.Key);
				_animationsEnded++;
				if ((r.Value.Flags & AW_HIDE) != 0) SetVisible(r.Key, false, false);
			}
			_paintVersion++;
		}

		/// <summary>The window's scene as the animation of it, or of the window it is inside, shows
		/// it now; the scene itself when nothing is animating it.</summary>
		private object ApplyReveal(IntPtr handle, object scene)
		{
			if (_reveals.Count == 0 || !(scene is SceneVisual sv)) return scene;
			Hwnd hwnd = Hwnd.ObjectFromHandle(handle);
			if (hwnd == null) return scene;

			// The animated window and this one's place in it.
			float offX = 0, offY = 0;
			Hwnd root = hwnd;
			Reveal reveal = null;
			for (Hwnd h = hwnd; h != null; h = h.parent)
			{
				if (_reveals.TryGetValue(h.Handle, out reveal)) { root = h; break; }
				offX += h.x;
				offY += h.y;
			}
			if (reveal == null) return scene;

			float p = Math.Clamp((s_revealClock.ElapsedMilliseconds - reveal.Start) / (float)reveal.Duration, 0f, 1f);
			if ((reveal.Flags & AW_HIDE) != 0) p = 1f - p;
			float w = root.width, h2 = root.height;
			var outer = new SceneVisual();
			var shift = Vector2.Zero;
			int f = reveal.Flags;
			if ((f & AW_BLEND) != 0)
			{
				outer.Opacity = p;
			}
			else if ((f & AW_CENTER) != 0)
			{
				float cw = w * p, ch = h2 * p;
				outer.Clip = new Rect((w - cw) / 2 - offX, (h2 - ch) / 2 - offY, cw, ch);
			}
			else
			{
				float x = 0, y = 0, cw = w, ch = h2;
				if ((f & AW_HOR_POSITIVE) != 0) { cw = w * p; if ((f & AW_SLIDE) != 0) shift.X = cw - w; }
				else if ((f & AW_HOR_NEGATIVE) != 0) { cw = w * p; x = w - cw; if ((f & AW_SLIDE) != 0) shift.X = x; }
				if ((f & AW_VER_POSITIVE) != 0) { ch = h2 * p; if ((f & AW_SLIDE) != 0) shift.Y = ch - h2; }
				else if ((f & AW_VER_NEGATIVE) != 0) { ch = h2 * p; y = h2 - ch; if ((f & AW_SLIDE) != 0) shift.Y = y; }
				outer.Clip = new Rect(x - offX, y - offY, cw, ch);
			}
			var moved = new SceneVisual { Offset = shift };
			moved.Children.Add(sv);
			outer.Children.Add(moved);
			return outer;
		}
	}
}
