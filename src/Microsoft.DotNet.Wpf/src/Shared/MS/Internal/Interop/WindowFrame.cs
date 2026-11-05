// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// Windows' frame round a WPF window's client, on a head whose window layer draws it (iOS, visionOS: UIKitWindow):
// what on Windows the DWM draws and DefWindowProc answers for. A window with a caption (WS_CAPTION: a dialog, a tool
// window, a MessageBox's) has its outer rectangle WPF moves and sizes (Window.Left, Width: GetWindowRect), its client
// inside it (GetClientRect, WM_SIZE), and a press on the frame moves the window (the caption), sizes it (the edges)
// or closes it (the close button), as DefWindowProc's loops do. Without it a dialog came up as a bare client,
// neither movable nor closable.
//
// This is the frame's geometry and behaviour, in the client's device pixels; the window layer draws it (its look:
// FrameState, IsActive) and runs the loops it asks for (IWindowFrameOwner).
//

#nullable enable

using System;

namespace MS.Internal.Interop
{
    /// <summary>What a window does for its frame: the loops and the window state DefWindowProc runs.</summary>
    internal interface IWindowFrameOwner
    {
        /// <summary>The client's size, device pixels.</summary>
        void GetClientPixelSize(out int width, out int height);
        double FrameScale { get; }
        bool FrameMaximized { get; }
        void FrameActivate();
        void FrameBeginMove();
        void FrameBeginSize(int hitTest);
        void FrameToggleMaximized();
        void FrameClose();
        /// <summary>The frame's look changed (its close button): it is drawn again.</summary>
        void FrameChanged();
    }

    internal sealed class WindowFrame
    {
        internal const int HTNOWHERE = 0, HTCLIENT = 1, HTCAPTION = 2, HTLEFT = 10, HTRIGHT = 11, HTTOP = 12, HTTOPLEFT = 13,
            HTTOPRIGHT = 14, HTBOTTOM = 15, HTBOTTOMLEFT = 16, HTBOTTOMRIGHT = 17, HTBORDER = 18, HTCLOSE = 20;
        internal const int CloseHot = 1, ClosePressed = 2;

        private readonly IWindowFrameOwner _owner;
        private bool _closeHover, _closeHeld, _pressed;
        private long _captionClickTime;
        private int _captionClickX, _captionClickY;

        private WindowFrame(IWindowFrameOwner owner) => _owner = owner;

        /// <summary>The frame a top-level window of <paramref name="style"/> (WS_*) has, null for none: Windows 11's
        /// (AdjustWindowRectExForDpi) -- a caption, with or without a sizing frame, 8 pixels on three sides and 31 on top
        /// at 96 dpi (XplatUIWebGpu.Windows11Frame: a dialog frame of 3 that does not scale, a border and the padded
        /// border of 4 that do, the caption of 22 and a pixel).</summary>
        internal static WindowFrame? For(IWindowFrameOwner owner, int style, double scale)
        {
            const int WS_CAPTION = 0x00C00000, WS_THICKFRAME = 0x00040000, WS_SYSMENU = 0x00080000, WS_CHILD = 0x40000000;
            if ((style & WS_CAPTION) != WS_CAPTION || (style & WS_CHILD) != 0) return null;
            int M(int v) => (int)Math.Floor(v * scale + 0.5);
            int edge = 3 + M(1) + M(4);
            return new WindowFrame(owner)
            {
                Left = edge, Right = edge, Bottom = edge, Top = edge + M(22) + 1,
                Sizable = (style & WS_THICKFRAME) != 0,
                HasCloseBox = (style & WS_SYSMENU) != 0,
            };
        }

        /// <summary>The frame round the client, device pixels.</summary>
        internal int Left { get; private init; }
        internal int Top { get; private init; }
        internal int Right { get; private init; }
        internal int Bottom { get; private init; }
        internal bool Sizable { get; private init; }
        internal bool HasCloseBox { get; private init; }

        /// <summary>The resize margin outside the visible edge (Windows 11's: transparent, under a soft shadow).</summary>
        internal int Margin => Math.Max(0, Left - 1);

        /// <summary>The close button's width, device pixels (46 at 96 dpi).</summary>
        internal int CloseWidth => (int)Math.Round(46 * _owner.FrameScale);

        /// <summary>The close button's look: 0, the pointer over it (CloseHot), or held down on it (ClosePressed).</summary>
        internal int State => _closeHover ? (_closeHeld ? ClosePressed : CloseHot) : 0;

        /// <summary>WM_NCHITTEST for a point relative to the client's origin (device pixels), as Windows 11 answers it.</summary>
        internal int HitTest(int x, int y)
        {
            _owner.GetClientPixelSize(out int cw, out int ch);
            if (x >= 0 && y >= 0 && x < cw && y < ch) return HTCLIENT;
            int ox = x + Left, oy = y + Top, ow = cw + Left + Right, oh = ch + Top + Bottom;
            if (ox < 0 || oy < 0 || ox >= ow || oy >= oh) return HTNOWHERE;
            double scale = _owner.FrameScale;
            int margin = Margin;
            if (HasCloseBox && oy >= 1 && oy < Top && ox < ow - margin - 1 && ox >= ow - margin - 1 - CloseWidth) return HTCLOSE;
            if (Sizable && !_owner.FrameMaximized)
            {
                bool left = ox < Left, right = ox >= ow - Right;
                bool top = oy < (int)Math.Round(4 * scale), bottom = oy >= oh - Bottom;
                if (top && left) return HTTOPLEFT;
                if (top && right) return HTTOPRIGHT;
                if (bottom && left) return HTBOTTOMLEFT;
                if (bottom && right) return HTBOTTOMRIGHT;
                if (left) return HTLEFT;
                if (right) return HTRIGHT;
                if (top) return HTTOP;
                if (bottom) return HTBOTTOM;
            }
            if (oy < Top && ox >= margin && ox < ow - margin) return HTCAPTION;
            return HTBORDER;
        }

        private void Look(bool hover, bool held)
        {
            if (_closeHover == hover && _closeHeld == held) return;
            _closeHover = hover;
            _closeHeld = held;
            _owner.FrameChanged();
        }

        /// <summary>A touch on the frame (0 move, 1 down, 2 up; relative to the client, device pixels), as
        /// DefWindowProc takes a press on the non-client area: true when it was the frame's.</summary>
        internal bool Touch(int kind, int x, int y)
        {
            int hit = HitTest(x, y);
            switch (kind)
            {
                case 0:
                    Look(hit == HTCLOSE && HasCloseBox && (!_pressed || _closeHeld), _closeHeld);
                    return _pressed;
                case 1:
                    if (hit == HTCLIENT) return false;
                    if (hit == HTNOWHERE) return true;
                    _pressed = true;
                    _owner.FrameActivate();
                    if (hit == HTCLOSE) { Look(true, true); return true; }
                    if (hit == HTCAPTION)
                    {
                        // a second click on the caption maximizes the window, or restores it (SM_CXDOUBLECLK's square)
                        long now = Environment.TickCount64;
                        int slop = (int)Math.Round(2 * _owner.FrameScale);
                        bool second = now - _captionClickTime <= 500 && Math.Abs(x - _captionClickX) <= slop && Math.Abs(y - _captionClickY) <= slop;
                        _captionClickTime = second ? 0 : now;
                        _captionClickX = x;
                        _captionClickY = y;
                        if (second && Sizable) _owner.FrameToggleMaximized();
                        else _owner.FrameBeginMove();
                        return true;
                    }
                    if (hit >= HTLEFT && hit <= HTBOTTOMRIGHT && Sizable && !_owner.FrameMaximized) _owner.FrameBeginSize(hit);
                    return true;
                case 2:
                    if (!_pressed) return false;
                    _pressed = false;
                    bool close = _closeHeld && hit == HTCLOSE;
                    Look(false, false);
                    if (close) _owner.FrameClose();
                    return true;
                default:
                    return hit != HTCLIENT;
            }
        }

        /// <summary>The move or size loop the press began has ended (its release went to the loop).</summary>
        internal void LoopEnded() => _pressed = false;
    }
}
