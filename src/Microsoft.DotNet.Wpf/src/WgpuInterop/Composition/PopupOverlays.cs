// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// Popups that have no surface of their own, drawn into the windows under them.
//
// Where a popup cannot present itself (see NativePlatform.PopupsShareOwnerSurface), its scene is
// drawn into other windows' surfaces instead. It used to be drawn into every window at the same
// place, the popup's position in the activity or UIWindow, which is right for the app's own window
// (it starts at that space's origin) and wrong for every other one: a floating window starts where
// it sits, so the popup came out offset by the window's own position, and what of it lay past that
// window's edge was simply not there, the window's surface ending at its edge.
//
// So every window draws the part of each popup that lies over it, placed in ITS coordinates. A
// popup reaching past a dialog is then drawn partly by the dialog and partly by the window under
// it, and between them it is whole -- on top of both, as a popup is on Windows, and with its
// shadow and rounded corners blended over what each window drew, since the blending happens in
// that window's own pass and needs no see-through surface. The app's own window fills the screen,
// so a popup WPF has kept on the screen always has a window under every part of it.
//

using System;
using System.Collections.Generic;
using System.Numerics;

namespace Microsoft.Wpf.Interop.WebGpu.Composition
{
    /// <summary>A popup drawn into the windows under it: its scene, and its window's rectangle in device
    /// pixels in the space every window's origin is given in.</summary>
    internal readonly struct PopupPlacement
    {
        public PopupPlacement(SceneVisual scene, int x, int y, int width, int height)
        {
            Scene = scene; X = x; Y = y; Width = width; Height = height;
        }

        public SceneVisual Scene { get; }
        public int X { get; }
        public int Y { get; }
        public int Width { get; }
        public int Height { get; }
    }

    internal static class PopupOverlays
    {
        /// <summary>
        /// The scene a window at (<paramref name="windowX"/>, <paramref name="windowY"/>) of the given size
        /// presents: its own, with every popup that lies over it drawn above it in its coordinates, in the
        /// order given (later over earlier). The window's own scene comes back untouched when no popup
        /// falls on it, so it is not wrapped for nothing.
        /// </summary>
        public static SceneVisual Compose(SceneVisual root, int windowX, int windowY, int width, int height,
                                          IReadOnlyList<PopupPlacement>? popups)
        {
            if (popups is null || popups.Count == 0) return root;

            SceneVisual? composite = null;
            foreach (PopupPlacement p in popups)
            {
                if (!Overlaps(p, windowX, windowY, width, height)) continue;
                if (composite is null)
                {
                    composite = new SceneVisual();
                    composite.Children.Add(root);
                }
                composite.Children.Add(Place(p, windowX, windowY));
            }
            return composite ?? root;
        }

        /// <summary>Whether any of the popup lies over the window.</summary>
        public static bool Overlaps(PopupPlacement p, int windowX, int windowY, int width, int height)
            => p.Width > 0 && p.Height > 0 && width > 0 && height > 0
               && p.X < windowX + width && windowX < p.X + p.Width
               && p.Y < windowY + height && windowY < p.Y + p.Height;

        /// <summary>
        /// The popup moved to where it lies in the window's coordinates, and clipped to its own window's
        /// rectangle as a window of its own would be: nothing it draws past its edge -- a shadow's blur
        /// beyond the margin WPF gave it -- is seen on Windows either. The window's surface clips it to
        /// the window's edge in turn, and the window under that draws the rest.
        /// </summary>
        private static SceneVisual Place(PopupPlacement p, int windowX, int windowY)
        {
            var placed = new SceneVisual
            {
                Offset = new Vector2(p.X - windowX, p.Y - windowY),
                Clip = new Rect(0, 0, p.Width, p.Height),
            };
            placed.Children.Add(p.Scene);
            return placed;
        }
    }
}
