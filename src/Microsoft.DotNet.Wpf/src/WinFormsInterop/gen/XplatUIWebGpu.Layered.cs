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
			_paintVersion++;
		}

		/// <summary>The window's layered picture, if it has one.</summary>
		private bool TryGetLayeredScene(IntPtr handle, out object scene)
		{
			if (_layered.TryGetValue(handle, out SceneVisual sv) && Hwnd.ObjectFromHandle(handle) != null)
			{
				scene = sv;
				return true;
			}
			_layered.Remove(handle);
			scene = null;
			return false;
		}
	}
}
