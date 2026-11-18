// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// Which popups on the mobile heads present through a see-through surface of their own, and which are
// drawn into the windows under them (NativePlatform.PopupsShareOwnerSurface).
//
// A popup drawn into its owner ended at the owner's edge: a drop-down near the bottom of a dialog was
// cut off where the dialog did. Where the platform can give a popup a surface that keeps its alpha it
// now has one -- a see-through CAMetalLayer on iOS, a translucent surface view on Android's Vulkan --
// and the rest are drawn into every window under them. These pin the decision, made in the windowing
// layer when the view is made: a view is made with a surface or without one, so it cannot change later.
//
// Android's head is driven through a host that records what it was asked for; AndroidWindow needs no
// Android to run. iOS's view creation needs UIKit, so only its decision is.
//

using System;
using System.Collections.Generic;
using MS.Internal.Interop;
using Xunit;

namespace Wpf.Platform.Tests
{
    public sealed class AndroidPopupSurfaceTests : IDisposable
    {
        private const int WS_POPUP = unchecked((int)0x80000000), WS_CHILD = 0x40000000;

        private readonly IAndroidHost _priorHost = AndroidWindow.Host;
        private readonly Func<bool> _priorAlpha = AndroidWindow.SurfacesKeepAlpha;
        private readonly List<AndroidWindow> _windows = new();

        public void Dispose()
        {
            foreach (AndroidWindow w in _windows) w.Destroy();
            AndroidWindow.Host = _priorHost;
            AndroidWindow.SurfacesKeepAlpha = _priorAlpha;
        }

        /// <summary>A host that makes nothing, and remembers whether each view was asked for with a surface.</summary>
        private sealed class RecordingHost : IAndroidHost
        {
            public bool Popups { get; init; } = true;
            public readonly Dictionary<IntPtr, bool> OwnSurface = new();

            public bool PopupSurfaces => Popups;
            public bool CreateView(IntPtr handle, int x, int y, int width, int height, bool borderless, bool fillsScreen, bool ownSurface)
            {
                OwnSurface[handle] = ownSurface;
                return true;
            }
            public void SetViewFrame(IntPtr handle, int x, int y, int width, int height) { }
            public void SetViewVisible(IntPtr handle, bool visible) { }
            public void DestroyView(IntPtr handle) { }
            public void ShowSoftKeyboard(IntPtr handle, bool multiline, bool password) { }
            public void HideSoftKeyboard(IntPtr handle) { }
            public void SetImeCursorRect(IntPtr handle, int x, int y, int width, int height) { }
            public double Density => 2;
            public void GetScreenPixels(out int width, out int height) { width = 1280; height = 800; }
        }

        private AndroidWindow Borderless(int style)
        {
            var w = new AndroidWindow { FrameStyle = style };
            w.Create("popup", 120, 280, 200, 300, borderless: true);
            _windows.Add(w);
            return w;
        }

        [Fact]
        public void APopup_PresentsThroughASeeThroughSurfaceOfItsOwn_WhereSurfacesKeepAlpha()
        {
            var host = new RecordingHost();
            AndroidWindow.Host = host;
            AndroidWindow.SurfacesKeepAlpha = () => true;     // the Vulkan backend

            AndroidWindow popup = Borderless(WS_POPUP);

            Assert.True(popup.IsSeeThroughPopup);
            Assert.True(host.OwnSurface[popup.Handle], "the popup's view was made without a surface");
            // ...so the compositor presents it itself, not into the windows under it
            Assert.False(AndroidWindow.IsPopupWindow(popup.Handle));
            // ...through a surface with alpha, for its rounded chrome and shadow
            Assert.False(AndroidWindow.IsWindowOpaque(popup.Handle));
        }

        [Fact]
        public void APopup_IsDrawnIntoTheWindowsUnderIt_WhereSurfacesLoseTheirAlpha()
        {
            var host = new RecordingHost();
            AndroidWindow.Host = host;
            AndroidWindow.SurfacesKeepAlpha = () => false;    // GLES: black round the corners

            AndroidWindow popup = Borderless(WS_POPUP);

            Assert.False(popup.IsSeeThroughPopup);
            Assert.False(host.OwnSurface[popup.Handle]);
            Assert.True(AndroidWindow.IsPopupWindow(popup.Handle));
        }

        [Fact]
        public void APopup_IsDrawnIntoTheWindowsUnderIt_OnAHostThatGivesPopupsNoSurface()
        {
            var host = new RecordingHost { Popups = false };   // an XR desktop of panels
            AndroidWindow.Host = host;
            AndroidWindow.SurfacesKeepAlpha = () => true;

            AndroidWindow popup = Borderless(WS_POPUP);

            Assert.False(popup.IsSeeThroughPopup);
            Assert.False(host.OwnSurface[popup.Handle]);
            Assert.True(AndroidWindow.IsPopupWindow(popup.Handle));
        }

        [Fact]
        public void AChildWindow_IsNeverAPopupWithASurface()
        {
            var host = new RecordingHost();
            AndroidWindow.Host = host;
            AndroidWindow.SurfacesKeepAlpha = () => true;

            AndroidWindow child = Borderless(WS_CHILD);   // an ElementHost's tree: drawn into the window it lies in

            Assert.False(child.IsSeeThroughPopup);
            Assert.False(host.OwnSurface[child.Handle]);
            Assert.True(AndroidWindow.IsPopupWindow(child.Handle));
        }

        [Fact]
        public void TheDecision_IsTheViewsAndOutlivesAChangeOfBackend()
        {
            var host = new RecordingHost();
            AndroidWindow.Host = host;
            bool keepsAlpha = false;                           // no device yet when the popup was made
            AndroidWindow.SurfacesKeepAlpha = () => keepsAlpha;

            AndroidWindow popup = Borderless(WS_POPUP);
            keepsAlpha = true;

            // made without a surface, it stays drawn into the windows under it: asked to present itself
            // now, it would present nothing
            Assert.False(popup.IsSeeThroughPopup);
            Assert.True(AndroidWindow.IsPopupWindow(popup.Handle));
        }
    }

    public sealed class UIKitPopupSurfaceTests
    {
        private const int WS_POPUP = unchecked((int)0x80000000), WS_CHILD = 0x40000000;

        [Fact]
        public void APopup_HasASeeThroughLayerOfItsOwn()
            => Assert.True(UIKitWindow.PresentsSeeThrough(borderless: true, ownSurface: false, WS_POPUP));

        [Fact]
        public void AChildWindow_AFormsBorderlessWindowAndAFramedOne_DoNot()
        {
            Assert.False(UIKitWindow.PresentsSeeThrough(borderless: true, ownSurface: false, WS_CHILD));
            Assert.False(UIKitWindow.PresentsSeeThrough(borderless: true, ownSurface: true, WS_POPUP));
            Assert.False(UIKitWindow.PresentsSeeThrough(borderless: false, ownSurface: false, 0x00CF0000));
        }

        [Fact]
        public void PopupsCanBeDrawnIntoTheWindowsUnderThemAgain()
        {
            bool prior = UIKitWindow.PopupsPresentThemselves;
            try
            {
                UIKitWindow.PopupsPresentThemselves = false;    // WPF_IOS_COMPOSITE_POPUPS=1
                Assert.False(UIKitWindow.PresentsSeeThrough(borderless: true, ownSurface: false, WS_POPUP));
            }
            finally
            {
                UIKitWindow.PopupsPresentThemselves = prior;
            }
        }

        [Fact]
        public void AWindowItDoesNotKnow_IsOpaque()
            => Assert.True(UIKitWindow.IsWindowOpaque((IntPtr)0x7fff0000));
    }
}
