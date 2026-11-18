// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// A popup with no surface of its own (Android's GLES backend, an XR desktop, a child window) is drawn
// into the windows under it (PopupOverlays). It used to be drawn into every window at its position in
// the activity, which is right for the app's own window and wrong for a floating one: there it came
// out offset by the window's position, and what lay past the window's edge was gone. Each window now
// draws the part of the popup over it, in its own coordinates, so that between them it is whole.
//

using System.Linq;
using System.Numerics;
using Microsoft.Wpf.Interop.WebGpu.Composition;
using WgpuInterop.Tests.Harness;
using Xunit;

namespace WgpuInterop.Tests.Compositing
{
    public sealed class PopupOverlayTests : RendererTestBase
    {
        public PopupOverlayTests(GpuFixture gpu) : base(gpu) { }

        private static SceneVisual Filled(int width, int height, RgbaColor color)
        {
            var v = new SceneVisual();
            v.Content.Add(new GeometryFill(new RectangleGeometry(new Rect(0, 0, width, height)), color));
            return v;
        }

        [Fact]
        public void WithoutPopups_TheWindowsSceneIsPassedThrough()
        {
            var root = new SceneVisual();
            Assert.Same(root, PopupOverlays.Compose(root, 0, 0, 100, 100, null));
            Assert.Same(root, PopupOverlays.Compose(root, 0, 0, 100, 100, new PopupPlacement[0]));
        }

        [Fact]
        public void APopupNotOverAWindow_LeavesItsSceneUntouched()
        {
            var root = new SceneVisual();
            var popup = new PopupPlacement(new SceneVisual(), 500, 500, 50, 50);
            // a dialog at (100,100) 200x200 ends at (300,300); the popup is well clear of it
            Assert.Same(root, PopupOverlays.Compose(root, 100, 100, 200, 200, new[] { popup }));
            // touching an edge is not overlapping it
            var touching = new PopupPlacement(new SceneVisual(), 300, 100, 50, 50);
            Assert.Same(root, PopupOverlays.Compose(root, 100, 100, 200, 200, new[] { touching }));
        }

        [Fact]
        public void APopupIsPlacedInTheCoordinatesOfTheWindowDrawingIt()
        {
            var root = new SceneVisual();
            var scene = new SceneVisual();
            // a floating dialog at (100,100); its combo's drop-down opens at (120,280) in the activity
            var popup = new PopupPlacement(scene, 120, 280, 200, 300);

            SceneVisual composite = PopupOverlays.Compose(root, 100, 100, 300, 200, new[] { popup });

            Assert.NotSame(root, composite);
            Assert.Same(root, composite.Children[0]);           // the window first, the popup over it
            SceneVisual placed = composite.Children[1];
            Assert.Same(scene, placed.Children.Single());
            // where the popup is in the DIALOG, not in the activity: it used to be drawn at (120,280)
            Assert.Equal(new Vector2(20, 180), placed.Offset);
            // and clipped to its own window, as a window of its own is
            Assert.True(placed.Clip.HasValue);
            Rect clip = placed.Clip!.Value;
            Assert.Equal((0f, 0f, 200f, 300f), (clip.X, clip.Y, clip.Width, clip.Height));
        }

        [Fact]
        public void APopupPastItsOwnersEdge_IsDrawnByTheWindowUnderItToo()
        {
            var main = new SceneVisual();
            var dialog = new SceneVisual();
            var popup = new PopupPlacement(new SceneVisual(), 120, 280, 200, 300);   // the dialog ends at y=300
            var popups = new[] { popup };

            SceneVisual inDialog = PopupOverlays.Compose(dialog, 100, 100, 300, 200, popups);
            SceneVisual inMain = PopupOverlays.Compose(main, 0, 0, 1280, 800, popups);

            // both draw it, each where it is in their own coordinates
            Assert.Equal(new Vector2(20, 180), inDialog.Children[1].Offset);
            Assert.Equal(new Vector2(120, 280), inMain.Children[1].Offset);

            // and what each window shows of it (its rectangle cut by the window's) is the whole popup:
            // the dialog the top 20 rows, the app's window the rest
            int covered = 0;
            for (int y = popup.Y; y < popup.Y + popup.Height; y++)
            for (int x = popup.X; x < popup.X + popup.Width; x++)
            {
                bool inD = x >= 100 && x < 400 && y >= 100 && y < 300;
                bool inM = x >= 0 && x < 1280 && y >= 0 && y < 800;
                if (inD || inM) covered++;
            }
            Assert.Equal(popup.Width * popup.Height, covered);
        }

        [Fact]
        public void PopupsKeepTheirOrder_LaterOverEarlier()
        {
            var root = new SceneVisual();
            SceneVisual menu = new(), submenu = new();
            SceneVisual composite = PopupOverlays.Compose(root, 0, 0, 400, 400, new[]
            {
                new PopupPlacement(menu, 10, 10, 100, 100),
                new PopupPlacement(new SceneVisual(), 900, 900, 10, 10),   // over another window only
                new PopupPlacement(submenu, 100, 20, 100, 100),
            });

            Assert.Equal(3, composite.Children.Count);
            Assert.Same(menu, composite.Children[1].Children[0]);
            Assert.Same(submenu, composite.Children[2].Children[0]);
        }

        /// <summary>
        /// The pictures the two windows present, stacked as the screen stacks them (the dialog over the
        /// app's window), show the popup whole, red to its last row -- not cut off at the dialog's bottom
        /// edge, where the dialog's surface ends.
        /// </summary>
        [Fact]
        public void TheScreen_ShowsAPopupPastItsOwnerWhole()
        {
            const int ScreenW = 96, ScreenH = 96;
            const int DialogX = 16, DialogY = 16, DialogW = 48, DialogH = 32;    // ends at y=48
            var red = RgbaColor.FromBytes(255, 0, 0, 255);
            var blue = RgbaColor.FromBytes(0, 0, 255, 255);
            var popup = new PopupPlacement(Filled(24, 40, red), 24, 40, 24, 40);  // y 40..80: past the dialog
            var popups = new[] { popup };

            byte[] mainPx = Render(PopupOverlays.Compose(new SceneVisual(), 0, 0, ScreenW, ScreenH, popups), ScreenW, ScreenH);
            byte[] dialogPx = Render(PopupOverlays.Compose(Filled(DialogW, DialogH, blue), DialogX, DialogY, DialogW, DialogH, popups), DialogW, DialogH);

            // the screen: the app's window, the dialog's picture over it where the dialog lies
            var screen = (byte[])mainPx.Clone();
            for (int y = 0; y < DialogH; y++)
                System.Array.Copy(dialogPx, y * DialogW * 4, screen, ((DialogY + y) * ScreenW + DialogX) * 4, DialogW * 4);
            var img = new Image(screen, ScreenW, ScreenH);

            for (int y = popup.Y + 1; y < popup.Y + popup.Height - 1; y++)
            {
                img.AssertPixel(popup.X + 1, y, 255, 0, 0, 8, "popup's left column");
                img.AssertPixel(popup.X + popup.Width - 2, y, 255, 0, 0, 8, "popup's right column");
            }
            img.AssertPixel(20, 20, 0, 0, 255, 8, "the dialog beside the popup");
            img.AssertPixel(60, 70, 255, 255, 255, 8, "the app's window beside the popup");
        }
    }
}
