// UpdateLayeredWindow: a layered window's picture is the bitmap it was last given, not what it
// paints. Win32 composites that bitmap with its per-pixel alpha (and a constant alpha on top) and
// never shows the window's WM_PAINT output. Here the bitmap becomes the window's scene, and what
// the window paints is still recorded but not composited while it has one.
//
// It is how splash screens with soft edges and drop shadows that are separate windows (DotNetBar's
// menus) are drawn. Shown as an ordinary window instead, a shadow was an opaque rectangle the
// colour of the control background, laid over the menu it was meant to shade.
using System.Collections.Generic;
using Microsoft.Wpf.Interop.WebGpu.Composition;

namespace System.Windows.Forms {
	internal partial class XplatUIWebGpu {
		private readonly Dictionary<IntPtr, SceneVisual> _layered = new Dictionary<IntPtr, SceneVisual>();
		private readonly Dictionary<IntPtr, (byte[] Rgba, int Width, int Height)> _layeredPixels = new Dictionary<IntPtr, (byte[], int, int)>();

		/// <summary>Whether the mouse goes through a window at a point of it: a layered window is
		/// hit only where its picture is not fully transparent, and not at all if it is also
		/// WS_EX_TRANSPARENT -- as in Win32. A drop shadow is a layered window laid over the menu it
		/// shades, and every click on the menu went to the shadow instead.</summary>
		private bool PassesMouseThrough(Hwnd hwnd, int x, int y)
		{
			if ((hwnd.initial_ex_style & WindowExStyles.WS_EX_LAYERED) == 0) return false;
			if ((hwnd.initial_ex_style & WindowExStyles.WS_EX_TRANSPARENT) != 0) return true;
			if (!_layeredPixels.TryGetValue(hwnd.Handle, out var px)) return false;
			if (x < 0 || y < 0 || x >= px.Width || y >= px.Height) return true;
			return px.Rgba[(y * px.Width + x) * 4 + 3] == 0;
		}

		/// <summary>Give a window the picture UpdateLayeredWindow hands it: <paramref name="rgba"/> is
		/// straight (not premultiplied) RGBA, <paramref name="width"/> by <paramref name="height"/>,
		/// shown at the window's origin with <paramref name="opacity"/> on top of its own alpha.</summary>
		internal void SetLayeredImage(IntPtr handle, byte[] rgba, int width, int height, float opacity)
		{
			T($"SetLayeredImage h=0x{handle.ToInt64():x} {width}x{height} opacity={opacity}");
			if (Hwnd.ObjectFromHandle(handle) == null || rgba == null || width <= 0 || height <= 0) return;
			var sv = new SceneVisual { Opacity = opacity };
			sv.Content.Add(new GeometryFill(new RectangleGeometry(new Rect(0, 0, width, height)), new ImageBrush(rgba, width, height)));
			_layered[handle] = sv;
			_layeredPixels[handle] = (rgba, width, height);
			_paintVersion++;
		}

		private readonly Dictionary<IntPtr, (byte[] Bgra, int Width, int Height, byte Alpha)> _layeredBits = new Dictionary<IntPtr, (byte[], int, int, byte)>();

		/// <summary>The same picture exactly as the app handed it to UpdateLayeredWindow (premultiplied
		/// BGRA, the blend's constant alpha), for a host that can put it on screen through the real
		/// UpdateLayeredWindow: rendering the straight copy again cost a level on partly transparent
		/// edges.</summary>
		internal void SetLayeredBits(IntPtr handle, byte[] bgra, int width, int height, byte alpha)
		{
			if (Hwnd.ObjectFromHandle(handle) == null || bgra == null) return;
			_layeredBits[handle] = (bgra, width, height, alpha);
		}

		/// <summary>The window's UpdateLayeredWindow bits, or null.</summary>
		internal object GetLayeredBits(IntPtr handle)
			=> _layered.ContainsKey(handle) && _layeredBits.TryGetValue(handle, out var b) ? b : null;

		/// <summary>Whether UpdateLayeredWindow has given the window a picture (a host shows such a
		/// window see-through, Win32Host.Layered).</summary>
		internal bool HasLayeredImage(IntPtr handle) => _layered.ContainsKey(handle) && Hwnd.ObjectFromHandle(handle) != null;

		/// <summary>The window's layered picture, if it has one.</summary>
		private bool TryGetLayeredScene(IntPtr handle, out object scene)
		{
			if (_layered.TryGetValue(handle, out SceneVisual sv) && Hwnd.ObjectFromHandle(handle) != null)
			{
				scene = sv;
				return true;
			}
			_layered.Remove(handle);
			_layeredPixels.Remove(handle);
			scene = null;
			return false;
		}
	}
}
