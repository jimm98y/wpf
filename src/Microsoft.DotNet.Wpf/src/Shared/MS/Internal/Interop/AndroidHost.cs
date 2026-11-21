// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// The Java-side half of the Android windowing backend: everything MS.Internal.Interop.AndroidWindow
// cannot do from WindowsBase, because it needs android.* types (see the header of AndroidWindow.cs).
// Two classes:
//
//   WpfSurfaceView  one WPF window: a SurfaceView whose Surface becomes the ANativeWindow wgpu
//                   renders into. Reports that surface's lifetime and its touches back into
//                   AndroidWindow.
//   AndroidHost     the IAndroidHost implementation: creates/positions/destroys those views inside
//                   the activity on the main Looper (OnMain), and answers density/screen queries.
//
// It is NOT compiled into WindowsBase -- it needs the Mono.Android bindings, which WindowsBase must
// not reference. It lives here, beside AndroidWindow.cs, because it is a head PAYLOAD that every
// net10.0-android app links into itself, exactly as browser-window.js in this same folder is a
// payload the browser head loads. Both heads in this repo (tests/AndroidSpike and
// samples/wpf-gallery-android) <Compile Include> this one file rather than keeping a copy.
//

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Android.App;
using Android.Content;
using Android.Content.Res;
using Android.Runtime;
using Android.Graphics;
using Android.Views;
using Android.Views.InputMethods;
using Android.OS;
using Android.Widget;

namespace MS.Internal.Interop;

/// <summary>
/// Android system properties, the only configuration channel that reaches an app from
/// <c>adb shell setprop</c>. Environment variables cannot: an Android app inherits its environment
/// from zygote, so `adb shell VAR=x am start` has no effect. android.os.SystemProperties is @hide,
/// so there is no binding for it; the NDK's __system_property_get is the supported way in.
/// </summary>
internal static class AndroidSystemProperties
{
    public static string Get(string name)
    {
        var buf = new byte[92];   // PROP_VALUE_MAX
        int len = __system_property_get(name, buf);
        return len > 0 ? System.Text.Encoding.UTF8.GetString(buf, 0, len) : "";
    }

    [DllImport("c")] private static extern int __system_property_get(string name, byte[] value);
}

/// <summary>
/// Copies the app's bundled fonts out of the APK so SystemFontCatalog can find them.
///
/// Android's own /system/fonts covers text (Roboto and the Notos), but NOT the two families WPF's
/// Fluent theme draws its glyph icons from -- Segoe Fluent Icons / Segoe MDL2 Assets. Those are
/// substituted with the "Symbols" font this repo ships (sdk/WpfWebGpu.Sdk/web/fonts), which the iOS
/// and browser heads bundle too; without it every icon in the gallery falls through to a text font
/// and renders as a missing-glyph box.
///
/// The catalog enumerates a real DIRECTORY, and an Android asset lives inside the APK where there is
/// no path to enumerate -- so the assets are copied out once, into the same
/// AppContext.BaseDirectory/fonts the catalog's Android branch probes. This is the Android spelling
/// of what the browser head does when it writes the same fonts into the wasm VFS at /fonts.
/// </summary>
internal static class AndroidFonts
{
    public static void ExtractBundled(Context context)
    {
        try
        {
            string dir = System.IO.Path.Combine(AppContext.BaseDirectory, "fonts");
            System.IO.Directory.CreateDirectory(dir);

            AssetManager assets = context.Assets!;
            foreach (string name in assets.List("fonts") ?? Array.Empty<string>())
            {
                // Extracted once per install. An updated font needs a reinstall (or clear-data),
                // which is also when the APK's assets change, so the two stay in step.
                string dest = System.IO.Path.Combine(dir, name);
                if (System.IO.File.Exists(dest)) continue;

                using System.IO.Stream src = assets.Open("fonts/" + name);
                using System.IO.FileStream dst = System.IO.File.Create(dest);
                src.CopyTo(dst);
            }
        }
        catch (Exception e)
        {
            // Not fatal: text still renders from /system/fonts, only the glyph icons are lost.
            Console.WriteLine($"WPF Android: could not extract the bundled fonts: {e}");
        }
    }
}

/// <summary>
/// A single WPF window's native view. Its Surface — and therefore the ANativeWindow wgpu renders
/// into — exists only between <see cref="SurfaceCreated"/> and <see cref="SurfaceDestroyed"/>, which
/// is the whole reason AndroidWindow keeps a synthetic handle and looks the live one up.
///
/// A SurfaceView, which is the same shape every other head in this repo uses: a surface the GPU
/// scans out DIRECTLY, with no intermediate copy (a UIView whose backing layer is a CAMetalLayer on
/// iOS, an HWND swap chain on Windows, a canvas on the browser). A TextureView would route every
/// frame through an extra copy into the view hierarchy's own render target.
///
/// One Android-specific consequence: SurfaceViews are punched through the view hierarchy by the
/// system compositor rather than drawn in it, so normal view z-order does NOT order two of them.
/// A floating window asks to be a media overlay to land above the app's own, and a popup to be on
/// top, above them all and the frames drawn round them (see AndroidHost.CreateView).
///
/// NB: this is the arrangement that first surfaced as
///     BufferQueueProducer: connect: already connected (cur=1 req=1)
///     libEGL: eglCreateWindowSurface: native_window_api_connect failed ... EGL_BAD_ALLOC
/// and a fatal Rust panic inside wgpuSurfaceConfigure. That was NOT a SurfaceView problem -- it was
/// wgpu creating a surface for every enabled backend, with the Vulkan one connecting the window as
/// an EGL producer and holding it (see WgpuContext.PreferredBackends). It reproduced identically on
/// a TextureView and even on a private ImageReader, and is fixed at the source.
/// </summary>
internal sealed class WpfSurfaceView : SurfaceView, ISurfaceHolderCallback
{
    private readonly IntPtr _wpfHandle;

    /// <summary>Taken out of the activity (DestroyView): what its surface still reports is no longer the window's,
    /// which may have a view in another host by now (AndroidWindow.Rehost: the panel app gone immersive).</summary>
    public bool Detached { get; set; }

    /// <summary>The app's own window, which fills the activity; the others float.</summary>
    public bool FillsScreen { get; init; }

    /// <summary>A popup's surface (AndroidWindow.IsSeeThroughPopup): translucent, and composed above the
    /// activity's window and every other surface (AndroidHost.CreateView).</summary>
    public bool IsPopup { get; init; }

    public WpfSurfaceView(Context context, IntPtr wpfHandle) : base(context)
    {
        _wpfHandle = wpfHandle;
        Holder!.AddCallback(this);
        Focusable = true;
    }

    // ---- text input -------------------------------------------------------------
    //
    // A SurfaceView is not a text editor, so Android never offers it the soft keyboard and nothing
    // can be typed into a WPF TextBox. These two overrides are the whole contract for opting in:
    // say yes to being an editor, and hand back an InputConnection for the input method to talk to.

    // Android only asks for this when an accessibility service is actually bound, so it doubles as
    // the "a screen reader exists" signal -- the role WM_GETOBJECT plays on Windows.
    public override Android.Views.Accessibility.AccessibilityNodeProvider? AccessibilityNodeProvider
    {
        get
        {
            AndroidWindow.InvokeOnUi(() => { AndroidAccessibility.Attach(); return true; });
            return _a11yProvider ??= new WpfAccessibilityNodeProvider(this, _wpfHandle);
        }
    }

    private WpfAccessibilityNodeProvider? _a11yProvider;

    public override bool OnCheckIsTextEditor() => true;

    public override IInputConnection? OnCreateInputConnection(EditorInfo? outAttrs)
    {
        if (outAttrs != null)
        {
            // TYPE_CLASS_TEXT. The multiline/password flags are set per focus in ShowSoftKeyboard,
            // which is the only place that knows which control took focus.
            outAttrs.InputType = Android.Text.InputTypes.ClassText;
            outAttrs.ImeOptions = ImeFlags.NoFullscreen | ImeFlags.NoExtractUi;
        }

        // fullEditor:false -- this connection reports edits outwards rather than maintaining an
        // Editable of its own; WPF's document is the single source of truth for the text.
        return new WpfInputConnection(this, _wpfHandle);
    }

    public void SurfaceCreated(ISurfaceHolder holder) => Report(holder, 0, 0);

    public void SurfaceChanged(ISurfaceHolder holder, [GeneratedEnum] Android.Graphics.Format format, int width, int height)
        => Report(holder, width, height);

    public void SurfaceDestroyed(ISurfaceHolder holder)
    {
        if (!Detached) AndroidWindow.NotifySurfaceDestroyed(_wpfHandle);
    }

    private void Report(ISurfaceHolder holder, int width, int height)
    {
        if (Detached) return;
        Surface? surface = holder.Surface;
        if (surface is null || !surface.IsValid) return;

        // ANativeWindow_fromSurface is the one place a JNIEnv is needed, and it is why this half of
        // the backend has to live in a .NET-for-Android assembly at all. It returns a NEW reference;
        // AndroidWindow takes its own, so drop ours immediately afterwards.
        IntPtr nativeWindow = ANativeWindow_fromSurface(JNIEnv.Handle, surface.Handle);
        if (nativeWindow == IntPtr.Zero) return;

        try
        {
            // surfaceCreated carries no size; the view's own is correct by then, and surfaceChanged
            // (which always follows, and fires again on every rotation) supplies the authoritative one.
            AndroidWindow.NotifySurfaceChanged(
                _wpfHandle,
                nativeWindow,
                width > 0 ? width : Width,
                height > 0 ? height : Height);
        }
        finally
        {
            ANativeWindow_release(nativeWindow);
        }
    }

    public override bool OnTouchEvent(MotionEvent? e) => WpfInput.OnTouch(_wpfHandle, WpfInput.AtWindow(this, _wpfHandle, e)) || base.OnTouchEvent(e);

    public override bool OnGenericMotionEvent(MotionEvent? e) => WpfInput.OnGenericMotion(_wpfHandle, WpfInput.AtWindow(this, _wpfHandle, e)) || base.OnGenericMotionEvent(e);

    [DllImport("android")] private static extern IntPtr ANativeWindow_fromSurface(IntPtr env, IntPtr surface);
    [DllImport("android")] private static extern void ANativeWindow_release(IntPtr window);
}

/// <summary>
/// The native view of a WPF window drawn into the windows under it -- a child window (an
/// ElementHost's tree), or a popup where popups have no surface of their own
/// (AndroidWindow.IsSeeThroughPopup): a plain, fully transparent View that exists ONLY to receive
/// touches.
///
/// Its PIXELS come from somewhere else: the compositor draws the window straight into the surfaces
/// of the windows under it (NativePlatform.PopupsShareOwnerSurface). But WPF still needs it to be a
/// real window for INPUT: mouse capture, hit testing and every message route key off its handle,
/// and a touch landing on the owner's view would be delivered with the OWNER's handle and ignored by
/// the popup. Hence a view with no content of its own, sitting exactly where the window is.
/// </summary>
internal sealed class WpfPopupView : View
{
    private readonly IntPtr _wpfHandle;

    /// <summary>The WPF window this is the touch target of.</summary>
    public IntPtr Handle => _wpfHandle;

    public WpfPopupView(Context context, IntPtr wpfHandle) : base(context)
    {
        _wpfHandle = wpfHandle;
        SetBackgroundColor(Android.Graphics.Color.Transparent);
        Focusable = true;
    }

    public override bool OnTouchEvent(MotionEvent? e) => WpfInput.OnTouch(_wpfHandle, WpfInput.AtWindow(this, _wpfHandle, e)) || base.OnTouchEvent(e);

    public override bool OnGenericMotionEvent(MotionEvent? e) => WpfInput.OnGenericMotion(_wpfHandle, WpfInput.AtWindow(this, _wpfHandle, e)) || base.OnGenericMotionEvent(e);
}

/// <summary>Windows 11's frame round a window that has one (AndroidWindow.HasFrame), drawn into a canvas of the outer
/// rectangle's size, as the DWM draws it: the shadow in the resize margin outside the visible edge, the edge, the
/// caption with the title and the close button -- white, or grey for a window that is not active, the close button
/// red under the pointer. The headset's host draws it into a surface of its own (AndroidXrHost), a flat activity's
/// into a view under the window's (WpfFrameView).</summary>
internal static class AndroidFramePainter
{
    public static void Draw(Canvas canvas, AndroidWindow w, int width, int height)
    {
        w.GetFrameInsets(out int left, out int top, out _, out _);
        float scale = (float)w.GetBackingScale();
        bool active = w.IsActiveWindow;
        canvas.DrawColor(Color.Transparent, PorterDuff.Mode.Clear!);
        using var paint = new Paint();
        int margin = Math.Max(0, left - 1);
        int vl = margin, vt = 0, vr = width - margin, vb = height - margin;      // the visible window
        // the shadow: a ring a pixel wide per step out, each pixel in one ring only
        for (int i = margin; i > 0; i--)
        {
            float k = (float)(margin - i + 1) / Math.Max(1, margin);
            paint.Color = Color.Argb((int)(28 * k * k), 0, 0, 0);
            int st = vt + Math.Min(i, 2), sb = vb + i - 1;
            canvas.DrawRect(vl - i, st, vl - i + 1, sb + 1, paint);
            canvas.DrawRect(vr + i - 1, st, vr + i, sb + 1, paint);
            canvas.DrawRect(vl - i + 1, sb, vr + i - 1, sb + 1, paint);
        }
        paint.Color = active ? Color.White : Color.Rgb(243, 243, 243);
        canvas.DrawRect(vl, vt, vr, vb, paint);
        float line = Math.Max(1, MathF.Round(scale));
        paint.SetStyle(Paint.Style.Stroke!);
        paint.StrokeWidth = line;
        paint.Color = active ? Color.Rgb(120, 120, 120) : Color.Rgb(170, 170, 170);
        canvas.DrawRect(vl + line / 2, vt + line / 2, vr - line / 2, vb - line / 2, paint);
        paint.SetStyle(Paint.Style.Fill!);

        // the close button, 46 pixels at 96 dpi across the caption's right end
        int closeRight = vr - 1, closeLeft = w.FrameHasCloseBox ? closeRight - (int)MathF.Round(46 * scale) : closeRight;
        if (w.FrameHasCloseBox)
        {
            int state = w.FrameState;
            if (state != 0)
            {
                paint.Color = state == AndroidWindow.FrameClosePressed ? Color.Rgb(200, 66, 58) : Color.Rgb(196, 43, 28);
                canvas.DrawRect(closeLeft, vt + 1, closeRight, top, paint);
            }
            float cx = (closeLeft + closeRight) / 2f, cy = (vt + 1 + top) / 2f, arm = 5 * scale;
            paint.AntiAlias = true;
            paint.SetStyle(Paint.Style.Stroke!);
            paint.StrokeWidth = scale;
            paint.Color = state != 0 ? Color.White : active ? Color.Black : Color.Rgb(150, 150, 150);
            canvas.DrawLine(cx - arm, cy - arm, cx + arm, cy + arm, paint);
            canvas.DrawLine(cx + arm, cy - arm, cx - arm, cy + arm, paint);
            paint.SetStyle(Paint.Style.Fill!);
        }

        // the title: the caption font (9 points, 12 pixels at 96 dpi) from the left edge to the buttons
        paint.AntiAlias = true;
        paint.TextSize = 12 * scale;
        paint.Color = active ? Color.Black : Color.Rgb(150, 150, 150);
        float textLeft = vl + 1 + 11 * scale, room = closeLeft - 4 * scale - textLeft;
        string title = w.Title;
        if (room > 0 && paint.MeasureText(title) > room)
        {
            while (title.Length > 0 && paint.MeasureText(title + "\u2026") > room) title = title[..^1];
            title += "\u2026";
        }
        Paint.FontMetrics metrics = paint.GetFontMetrics()!;
        float baseline = (vt + 1 + top) / 2f - (metrics.Ascent + metrics.Descent) / 2f;
        if (room > 0) canvas.DrawText(title, textLeft, baseline, paint);
    }
}

/// <summary>A window's frame in a flat activity: a view of the outer rectangle just under the window's surface,
/// drawn by AndroidFramePainter; its touches are the window's, measured from its client (WpfInput.AtWindow), which
/// takes those on the frame as DefWindowProc does (AndroidWindow: the caption moves it, the edges size it, the close
/// button closes it).</summary>
internal sealed class WpfFrameView : View
{
    private readonly IntPtr _wpfHandle;

    public WpfFrameView(Context context, IntPtr wpfHandle) : base(context)
    {
        _wpfHandle = wpfHandle;
        SetBackgroundColor(Android.Graphics.Color.Transparent);
    }

    protected override void OnDraw(Canvas? canvas)
    {
        if (canvas == null || AndroidWindow.FromHandle(_wpfHandle) is not { HasFrame: true } w || Width <= 0 || Height <= 0) return;
        AndroidFramePainter.Draw(canvas, w, Width, Height);
    }

    public override bool OnTouchEvent(MotionEvent? e) => WpfInput.OnTouch(_wpfHandle, WpfInput.AtWindow(this, _wpfHandle, e)) || base.OnTouchEvent(e);

    public override bool OnGenericMotionEvent(MotionEvent? e) => WpfInput.OnGenericMotion(_wpfHandle, WpfInput.AtWindow(this, _wpfHandle, e)) || base.OnGenericMotionEvent(e);
}

/// <summary>Translates Android motion events into the mouse messages WPF consumes. Shared by both
/// view kinds, which differ only in whether they also carry a rendering surface.</summary>
internal static class WpfInput
{
    /// <summary>The event in the coordinates of the window the view stands for: a move the window was
    /// given reaches its view a layout pass later (SetViewFrame, on this thread), and Android measures a
    /// touch from where the view is. Dragging a dialog by its caption, each touch was taken against the
    /// position just asked for while measured from the one before it, so the dialog overshot, jumped
    /// back and forth and was thrown off the screen.</summary>
    public static MotionEvent? AtWindow(View view, IntPtr handle, MotionEvent? e)
    {
        if (e is null || view is WpfSurfaceView { FillsScreen: true } || AndroidWindow.FromHandle(handle) is not { } window) return e;
        window.GetClientScreenOriginPixels(out int x, out int y);
        float dx = view.Left - x, dy = view.Top - y;
        if (dx != 0 || dy != 0) e.OffsetLocation(dx, dy);
        return e;
    }

    public static bool OnTouch(IntPtr handle, MotionEvent? e)
    {
        if (e is null) return false;

        // One pointer drives the mouse (see AndroidWindow's touch notes). Coordinates are already
        // in device pixels, relative to the view — exactly what WPF's off-Windows input path wants.
        int kind = e.ActionMasked switch
        {
            MotionEventActions.Down or MotionEventActions.PointerDown => 1,
            MotionEventActions.Move => 0,
            MotionEventActions.Up or MotionEventActions.PointerUp or MotionEventActions.Cancel => 2,
            _ => -1,
        };
        if (kind < 0) return false;

        // ---- the contacts, all of them ----
        //
        // A MotionEvent carries every pointer currently down, so which one this event is ABOUT
        // matters: a down or up names its pointer through ActionIndex and says nothing about the
        // others, while a move carries a fresh position for all of them at once. Treating the whole
        // batch as one contact is what limited this head to a single finger.
        //
        // BEFORE the mouse, not after. The contact is the informative report -- it is what tells the
        // seam the pen's pressure, tilt and which end of it this is -- and the ink path reads that
        // state when the mouse BUTTON-DOWN arrives. Reported the other way round, a stroke opens with
        // whatever the previous contact left behind, and an eraser held to the screen draws.
        //
        // Except for the primary pointer's LIFT, which goes after the mouse. A press is held back
        // until the touch is known not to be a press-and-hold right click (TouchHold), and a tap
        // releases it only on the way up -- so a contact lifted first had already taken its pen
        // state with it, and a quick dab with the eraser end landed as a dot of ink.
        bool primaryChanged = e.ActionMasked != MotionEventActions.PointerDown
                           && e.ActionMasked != MotionEventActions.PointerUp;
        bool liftAfterMouse = kind == 2 && primaryChanged && e.ActionMasked != MotionEventActions.Cancel;

        if (e.ActionMasked == MotionEventActions.Cancel)
        {
            for (int i = 0; i < e.PointerCount; i++)
            {
                AndroidWindow.NotifyTouchContact(handle, 3, e.GetPointerId(i), 0, 0, -1, double.NaN, double.NaN);
            }
        }
        else if (kind == 0)
        {
            for (int i = 0; i < e.PointerCount; i++)
            {
                ReportContact(handle, 0, e, i);
            }
        }
        else if (!liftAfterMouse)
        {
            ReportContact(handle, kind, e, e.ActionIndex);
        }

        // ---- the mouse, from the primary pointer ----
        //
        // Still needed: a TouchDevice raises Touch events and drives Manipulation but does not
        // promote itself to the mouse, so without this a tap would stop clicking buttons. Only the
        // FIRST pointer does it -- a second finger must not drag the cursor.
        if (primaryChanged)
        {
            // A press must be preceded by a move so WPF's mouse position is current before the
            // button-down lands; AndroidWindow does not synthesize it, because a real mouse (below)
            // does not need it.
            if (kind == 1) AndroidWindow.NotifyTouch(handle, 0, (int)e.GetX(), (int)e.GetY());
            AndroidWindow.NotifyTouch(handle, kind, (int)e.GetX(), (int)e.GetY());
        }

        if (liftAfterMouse) ReportContact(handle, kind, e, e.ActionIndex);

        return true;
    }

    /// <summary>Reports the pointer at <paramref name="index"/> within the event.</summary>
    /// <remarks>
    /// Raw axes only: what they mean -- pressure that is real only from a stylus, the spherical tilt,
    /// the touch ellipse as a contact box -- is worked out in WindowsBase (AndroidPenInput), where it
    /// is tested.
    /// </remarks>
    private static void ReportContact(IntPtr handle, int kind, MotionEvent e, int index)
    {
        // The eraser end reports as its own tool type, and measures the same axes as the tip.
        MotionEventToolType toolType = e.GetToolType(index);
        bool isEraser = toolType == MotionEventToolType.Eraser;
        bool isStylus = toolType == MotionEventToolType.Stylus || isEraser;

        // The barrel button is BUTTON_STYLUS_PRIMARY, which is also what Android itself treats as
        // the pen's secondary click. Not the eraser: Android reports the inverted end as a tool
        // type of its own, and a button held on the tip is still the tip. A finger has no buttons.
        bool? barrel = isStylus ? (e.ButtonState & MotionEventButtonState.StylusPrimary) != 0 : null;

        // A move carries the samples Android batched into it since the last one -- MotionEvent's
        // HISTORY, oldest first -- which a stylus fills at its own rate, well above the display's.
        double[]? history = null;
        int historySize = kind == 0 ? e.HistorySize : 0;
        if (historySize > 0)
        {
            const int stride = AndroidWindow.MotionHistoryStride;
            history = new double[historySize * stride];
            for (int h = 0; h < historySize; h++)
            {
                int o = h * stride;
                history[o] = e.GetHistoricalX(index, h);
                history[o + 1] = e.GetHistoricalY(index, h);
                history[o + 2] = e.GetHistoricalPressure(index, h);
                history[o + 3] = e.GetHistoricalAxisValue(Axis.Tilt, index, h);
                history[o + 4] = e.GetHistoricalAxisValue(Axis.Orientation, index, h);
                history[o + 5] = e.GetHistoricalTouchMajor(index, h);
                history[o + 6] = e.GetHistoricalTouchMinor(index, h);
                history[o + 7] = e.GetHistoricalEventTime(h);
            }
        }

        AndroidWindow.NotifyMotionContact(handle, kind, e.GetPointerId(index), (int)e.GetX(index), (int)e.GetY(index),
                                          e.GetPressure(index), e.GetAxisValue(Axis.Tilt, index),
                                          e.GetAxisValue(Axis.Orientation, index),
                                          e.GetTouchMajor(index), e.GetTouchMinor(index),
                                          isStylus, isEraser, barrel, history);
    }

    public static bool OnGenericMotion(IntPtr handle, MotionEvent? e)
    {
        // A real mouse wheel or a trackpad: Android reports it as a scroll DISTANCE in "clicks"
        // rather than as touches, so it never reaches OnTouchEvent at all.
        if (e is null || e.ActionMasked != MotionEventActions.Scroll) return false;

        float clicks = e.GetAxisValue(Axis.Vscroll);
        if (clicks == 0) return false;

        // One "click" is a line-ish; scale it to the logical distance AndroidWindow turns into
        // wheel notches (3 lines of 16px per notch, as everywhere else in WPF).
        AndroidWindow.NotifyScroll(handle, clicks * 48.0, (int)e.GetX(), (int)e.GetY());
        return true;
    }
}

/// <summary>
/// The activity-side half of the backend. Install one in <see cref="AndroidWindow.Host"/> before any
/// WPF window is created, and WPF windows become views inside the root layout passed to the ctor.
///
/// Deliberately NOT a <c>Java.Lang.Object</c>: the Android SDK generates a Java Callable Wrapper for
/// every managed type that is one, and doing so means resolving every interface that type implements
/// — including <see cref="IAndroidHost"/>, which lives in WindowsBase and is not a Java type at all
/// (build error XA4204).
/// </summary>
/// <summary>
/// The input method's end of a WPF text field.
///
/// Android composes in two stages, exactly as IMM32 and Wayland do: setComposingText carries the
/// reading being converted (drawn inline, underlined) and commitText the finished text. Both are
/// forwarded straight into WindowsBase, which turns them into the same WPF composition every other
/// platform produces.
///
/// BaseInputConnection is used with fullEditor:false, so it keeps no Editable of its own: the
/// document lives in WPF, and answering from a private copy here would let the two drift.
/// </summary>
internal sealed class WpfInputConnection : BaseInputConnection
{
    private readonly IntPtr _handle;

    public WpfInputConnection(View targetView, IntPtr handle)
        : base(targetView, fullEditor: false)
    {
        _handle = handle;
    }

    public override bool SetComposingText(Java.Lang.ICharSequence? text, int newCursorPosition)
    {
        AndroidWindow.NotifyComposingText(text?.ToString() ?? string.Empty, newCursorPosition);
        return true;
    }

    public override bool CommitText(Java.Lang.ICharSequence? text, int newCursorPosition)
    {
        AndroidWindow.NotifyCommitText(text?.ToString() ?? string.Empty, newCursorPosition);
        return true;
    }

    public override bool FinishComposingText()
    {
        AndroidWindow.NotifyFinishComposing();
        return true;
    }

    public override bool DeleteSurroundingText(int beforeLength, int afterLength)
    {
        AndroidWindow.NotifyDeleteSurrounding(beforeLength, afterLength);
        return true;
    }

    // Hardware keyboards and the delete key on some IMEs arrive as key events rather than as
    // InputConnection calls. Backspace is the one that matters for text: without it, deleting during
    // a composition does nothing.
    public override bool SendKeyEvent(KeyEvent? e)
    {
        if (e is { Action: KeyEventActions.Down, KeyCode: Keycode.Del })
        {
            AndroidWindow.NotifyDeleteSurrounding(1, 0);
            return true;
        }
        return base.SendKeyEvent(e);
    }
}

/// <summary>
/// TalkBack's view of a WPF window.
///
/// A SurfaceView draws everything itself, so to an accessibility service it is one opaque
/// rectangle. Android.Views.Accessibility.AccessibilityNodeProvider is Android's answer to exactly that: the view publishes a
/// tree of VIRTUAL nodes addressed by integer id, which is a direct fit for the automation tree.
///
/// Node data arrives from WindowsBase as one JSON string per node rather than a dozen property
/// calls, because each of those would be a separate JNI transition and TalkBack walks whole
/// subtrees at a time.
///
/// Each node is filled in as the matching android.widget class fills in its own: a text field as
/// TextView does (text, selection, granularities, input type), a list or grid as ListView and
/// GridView do (CollectionInfo, and CollectionItemInfo on the items), a scrolling node as
/// ScrollView does (scrollable, and only the scroll actions that can still move it).
/// </summary>
internal sealed class WpfAccessibilityNodeProvider : Android.Views.Accessibility.AccessibilityNodeProvider
{
    private readonly View _view;
    private readonly IntPtr _handle;

    // The Act* bits of AndroidAccessibility's node JSON.
    private const int ActClick = 1, ActExpand = 2, ActCollapse = 4, ActScrollForward = 8, ActScrollBackward = 16,
        ActScrollUp = 32, ActScrollDown = 64, ActScrollLeft = 128, ActScrollRight = 256, ActShowOnScreen = 512,
        ActSetSelection = 1024, ActTraverse = 2048, ActSetText = 4096, ActDismiss = 8192;

    // The Bundle keys of the action arguments: the values of AccessibilityNodeInfo's
    // ACTION_ARGUMENT_* constants, which are part of the platform's stable contract.
    private const string ArgSelectionStart = "ACTION_ARGUMENT_SELECTION_START_INT";
    private const string ArgSelectionEnd = "ACTION_ARGUMENT_SELECTION_END_INT";
    private const string ArgGranularity = "ACTION_ARGUMENT_MOVEMENT_GRANULARITY_INT";
    private const string ArgExtendSelection = "ACTION_ARGUMENT_EXTEND_SELECTION_BOOLEAN";
    private const string ArgSetText = "ACTION_ARGUMENT_SET_TEXT_CHARSEQUENCE";

    public WpfAccessibilityNodeProvider(View view, IntPtr handle)
    {
        _view = view;
        _handle = handle;
    }

    private static bool AtLeast(BuildVersionCodes level) => Build.VERSION.SdkInt >= level;

    public override Android.Views.Accessibility.AccessibilityNodeInfo? CreateAccessibilityNodeInfo(int virtualViewId)
    {
        // HOST_VIEW_ID is the view itself: it reports the WPF root as its only child, and is not
        // an accessible element in its own right.
        if (virtualViewId == AndroidAccessibility.HostViewId)
        {
            Android.Views.Accessibility.AccessibilityNodeInfo host = Android.Views.Accessibility.AccessibilityNodeInfo.Obtain(_view)!;
            _view.OnInitializeAccessibilityNodeInfo(host);

            int r = AndroidWindow.InvokeOnUi(() => AndroidAccessibility.GetRootId(_handle));
            if (r != AndroidAccessibility.InvalidNode) host.AddChild(_view, r);
            return host;
        }

        string json = AndroidWindow.InvokeOnUi(() => AndroidAccessibility.GetNodeJson(virtualViewId));
        if (string.IsNullOrEmpty(json)) return null;   // the node is gone; Android expects null

        Android.Views.Accessibility.AccessibilityNodeInfo info = Android.Views.Accessibility.AccessibilityNodeInfo.Obtain(_view, virtualViewId)!;
        var n = new AccessibilityJson(json);

        info.PackageName = _view.Context?.PackageName;
        info.ClassName = n.Str("cls");
        info.ContentDescription = n.Str("name");
        info.Enabled = n.Int("en") != 0;
        info.Focusable = n.Int("fo") != 0;
        info.Checkable = n.Int("ck") != 0;
        info.Checked = n.Int("chk") != 0;
        info.Selected = n.Int("sel") != 0;
        info.VisibleToUser = true;

        string text = n.Str("text");
        if (text.Length != 0) info.Text = text;

        // A text field, as TextView describes one: TalkBack reads "edit box", offers the editing
        // and reading-granularity menus, and says "password" from these.
        bool editable = n.Int("ed") != 0;
        bool password = n.Int("pw") != 0;
        bool multiLine = n.Int("ml") != 0;
        if (editable) info.Editable = true;
        if (password) info.Password = true;
        if (multiLine) info.MultiLine = true;
        if (editable || password)
        {
            info.InputType = Android.Text.InputTypes.ClassText
                | (password ? Android.Text.InputTypes.TextVariationPassword : 0)
                | (multiLine ? Android.Text.InputTypes.TextFlagMultiLine : 0);
        }
        if (n.Has("ss")) info.SetTextSelection(n.Int("ss"), n.Int("se"));
        int granularities = n.Int("gran");
        if (granularities != 0) info.MovementGranularities = (Android.Views.Accessibility.MovementGranularity)granularities;

        // "List, 12 items" and "item 3 of 12" / "row 2, column 4": TalkBack works them out from
        // these two, not from the children.
        if (n.Int("coll") != 0)
        {
            var mode = n.Int("csm") switch
            {
                2 => Android.Views.Accessibility.SelectionMode.Multiple,
                1 => Android.Views.Accessibility.SelectionMode.Single,
                _ => Android.Views.Accessibility.SelectionMode.None,
            };
            info.SetCollectionInfo(Android.Views.Accessibility.AccessibilityNodeInfo.CollectionInfo.Obtain(
                n.Int("cr"), n.Int("cc"), n.Int("chi") != 0, mode));
        }
        if (n.Int("item") != 0)
        {
            info.SetCollectionItemInfo(Android.Views.Accessibility.AccessibilityNodeInfo.CollectionItemInfo.Obtain(
                n.Int("ir"), Math.Max(n.Int("irs"), 1), n.Int("ic"), Math.Max(n.Int("ics"), 1), n.Int("ih") != 0, info.Selected));
        }

        if (n.Int("scr") != 0) info.Scrollable = true;

        // A window's title as its pane's: TalkBack announces a pane by it as it appears.
        string pane = n.Str("pane");
        if (pane.Length != 0 && AtLeast(BuildVersionCodes.P)) info.PaneTitle = pane;

        var bounds = new Android.Graphics.Rect(n.Int("x"), n.Int("y"),
                                               n.Int("x") + n.Int("w"), n.Int("y") + n.Int("h"));
        info.SetBoundsInScreen(bounds);

        int actions = n.Int("act");
        if ((actions & ActClick) != 0) { info.Clickable = true; info.AddAction(Android.Views.Accessibility.Action.Click); }
        if ((actions & ActExpand) != 0) info.AddAction(Android.Views.Accessibility.Action.Expand);
        if ((actions & ActCollapse) != 0) info.AddAction(Android.Views.Accessibility.Action.Collapse);
        if ((actions & ActScrollForward) != 0) info.AddAction(Android.Views.Accessibility.Action.ScrollForward);
        if ((actions & ActScrollBackward) != 0) info.AddAction(Android.Views.Accessibility.Action.ScrollBackward);
        if ((actions & ActSetSelection) != 0) info.AddAction(Android.Views.Accessibility.Action.SetSelection);
        if ((actions & ActTraverse) != 0)
        {
            info.AddAction(Android.Views.Accessibility.Action.NextAtMovementGranularity);
            info.AddAction(Android.Views.Accessibility.Action.PreviousAtMovementGranularity);
        }
        if ((actions & ActSetText) != 0) info.AddAction(Android.Views.Accessibility.Action.SetText);
        if ((actions & ActDismiss) != 0) { info.Dismissable = true; info.AddAction(Android.Views.Accessibility.Action.Dismiss); }

        // The directional scrolls and show-on-screen exist from API 23 only as action objects; the
        // older integer actions have no ids for them.
        if (AtLeast(BuildVersionCodes.M))
        {
            if ((actions & ActScrollUp) != 0) info.AddAction(Android.Views.Accessibility.AccessibilityNodeInfo.AccessibilityAction.ActionScrollUp!);
            if ((actions & ActScrollDown) != 0) info.AddAction(Android.Views.Accessibility.AccessibilityNodeInfo.AccessibilityAction.ActionScrollDown!);
            if ((actions & ActScrollLeft) != 0) info.AddAction(Android.Views.Accessibility.AccessibilityNodeInfo.AccessibilityAction.ActionScrollLeft!);
            if ((actions & ActScrollRight) != 0) info.AddAction(Android.Views.Accessibility.AccessibilityNodeInfo.AccessibilityAction.ActionScrollRight!);
            if ((actions & ActShowOnScreen) != 0) info.AddAction(Android.Views.Accessibility.AccessibilityNodeInfo.AccessibilityAction.ActionShowOnScreen!);
        }
        info.AddAction(Android.Views.Accessibility.Action.AccessibilityFocus);

        int parent = AndroidWindow.InvokeOnUi(() => AndroidAccessibility.GetParentId(virtualViewId));
        if (parent == AndroidAccessibility.InvalidNode) info.SetParent(_view);
        else info.SetParent(_view, parent);

        foreach (int child in AndroidWindow.InvokeOnUi(() => AndroidAccessibility.GetChildIds(virtualViewId)))
        {
            info.AddChild(_view, child);
        }

        return info;
    }

    public override bool PerformAction(int virtualViewId, [GeneratedEnum] Android.Views.Accessibility.Action action, Bundle? arguments)
    {
        if (action == Android.Views.Accessibility.Action.NextAtMovementGranularity ||
            action == Android.Views.Accessibility.Action.PreviousAtMovementGranularity)
        {
            return Traverse(virtualViewId, action, arguments);
        }

        string? argument = null;
        int kind = action switch
        {
            Android.Views.Accessibility.Action.Click => 0,
            Android.Views.Accessibility.Action.AccessibilityFocus => 1,
            Android.Views.Accessibility.Action.Focus => 1,
            Android.Views.Accessibility.Action.Expand => 2,
            Android.Views.Accessibility.Action.Collapse => 3,
            Android.Views.Accessibility.Action.SetText => 4,
            Android.Views.Accessibility.Action.SetSelection => 5,
            Android.Views.Accessibility.Action.ScrollForward => 6,
            Android.Views.Accessibility.Action.ScrollBackward => 7,
            Android.Views.Accessibility.Action.Dismiss => 13,
            _ => ActionObjectKind((int)action),
        };

        if (kind == 4)
        {
            argument = arguments?.GetCharSequence(ArgSetText) ?? string.Empty;
        }
        else if (kind == 5)
        {
            // Both absent is TextView's "clear the selection"; WindowsBase reads -1 as absent.
            int start = arguments?.GetInt(ArgSelectionStart, -1) ?? -1;
            int end = arguments?.GetInt(ArgSelectionEnd, -1) ?? -1;
            argument = start.ToString(System.Globalization.CultureInfo.InvariantCulture) + "," +
                       end.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        return kind >= 0 && AndroidWindow.InvokeOnUi(() => AndroidAccessibility.PerformAction(virtualViewId, kind, argument));
    }

    // The actions that are only action objects (API 23+), by their resource ids.
    private static int ActionObjectKind(int id)
    {
        if (!AtLeast(BuildVersionCodes.M)) return -1;
        if (id == Android.Views.Accessibility.AccessibilityNodeInfo.AccessibilityAction.ActionScrollUp!.Id) return 8;
        if (id == Android.Views.Accessibility.AccessibilityNodeInfo.AccessibilityAction.ActionScrollDown!.Id) return 9;
        if (id == Android.Views.Accessibility.AccessibilityNodeInfo.AccessibilityAction.ActionScrollLeft!.Id) return 10;
        if (id == Android.Views.Accessibility.AccessibilityNodeInfo.AccessibilityAction.ActionScrollRight!.Id) return 11;
        if (id == Android.Views.Accessibility.AccessibilityNodeInfo.AccessibilityAction.ActionShowOnScreen!.Id) return 12;
        return -1;
    }

    /// <summary>
    /// Reading by character, word, line or paragraph. Moves the accessibility cursor in WindowsBase,
    /// then sends what View.traverseAtGranularity sends: TalkBack speaks the segment between the
    /// event's from and to indices out of its text, and nothing is spoken without the event.
    /// </summary>
    private bool Traverse(int virtualViewId, Android.Views.Accessibility.Action action, Bundle? arguments)
    {
        int granularity = arguments?.GetInt(ArgGranularity, 0) ?? 0;
        bool extend = arguments?.GetBoolean(ArgExtendSelection, false) ?? false;
        bool forward = action == Android.Views.Accessibility.Action.NextAtMovementGranularity;

        (bool moved, int from, int to, string json) = AndroidWindow.InvokeOnUi(() =>
        {
            bool ok = AndroidAccessibility.MoveAtGranularity(virtualViewId, granularity, forward, extend, out int s, out int e);
            return (ok, s, e, ok ? AndroidAccessibility.GetNodeJson(virtualViewId) : string.Empty);
        });
        if (!moved) return false;

        if (_view.Context?.GetSystemService(Context.AccessibilityService) is Android.Views.Accessibility.AccessibilityManager { IsEnabled: true })
        {
            var n = new AccessibilityJson(json);
            var e = Android.Views.Accessibility.AccessibilityEvent.Obtain(Android.Views.Accessibility.EventTypes.ViewTextTraversedAtMovementGranularity)!;
            e.PackageName = _view.Context?.PackageName;
            e.ClassName = n.Str("cls");
            e.SetSource(_view, virtualViewId);
            e.Text?.Add(new Java.Lang.String(n.Str("text")));
            e.FromIndex = from;
            e.ToIndex = to;
            e.Action = (Android.AccessibilityServices.GlobalAction)(int)action;   // the binding types the int as a global action
            e.MovementGranularity = (Android.Views.Accessibility.MovementGranularity)granularity;
            _view.Parent?.RequestSendAccessibilityEvent(_view, e);
        }
        return true;
    }
}

/// <summary>
/// A minimal reader for the flat, machine-generated JSON WindowsBase sends (a node, an event).
/// Deliberately not System.Text.Json: this runs on the Android head where the linker is aggressive,
/// the shape is fixed and known, and the dependency would have to survive trimming for no benefit.
/// </summary>
internal readonly struct AccessibilityJson
{
    private readonly string _json;
    public AccessibilityJson(string json) { _json = json ?? string.Empty; }

    public bool Has(string key) => _json.IndexOf("\"" + key + "\":", StringComparison.Ordinal) >= 0;

    public string Str(string key)
    {
        int i = _json.IndexOf("\"" + key + "\":\"", StringComparison.Ordinal);
        if (i < 0) return string.Empty;
        i += key.Length + 4;

        var sb = new System.Text.StringBuilder();
        for (; i < _json.Length && _json[i] != '"'; i++)
        {
            if (_json[i] == '\\' && i + 1 < _json.Length)
            {
                i++;
                char c = _json[i];
                // A control character other than these three comes as a four-digit escape, kept
                // so that the offsets into a text still count it.
                if (c == 'u' && i + 4 < _json.Length
                    && int.TryParse(_json.AsSpan(i + 1, 4), System.Globalization.NumberStyles.HexNumber,
                                    System.Globalization.CultureInfo.InvariantCulture, out int code))
                {
                    sb.Append((char)code);
                    i += 4;
                    continue;
                }
                sb.Append(c switch { 'n' => '\n', 'r' => '\r', 't' => '\t', _ => c });
            }
            else sb.Append(_json[i]);
        }
        return sb.ToString();
    }

    public int Int(string key)
    {
        int i = _json.IndexOf("\"" + key + "\":", StringComparison.Ordinal);
        if (i < 0) return 0;
        i += key.Length + 3;

        int end = i;
        while (end < _json.Length && (char.IsDigit(_json[end]) || _json[end] == '-' || _json[end] == '.')) end++;
        return double.TryParse(_json.AsSpan(i, end - i), System.Globalization.NumberStyles.Float,
                               System.Globalization.CultureInfo.InvariantCulture, out double d) ? (int)d : 0;
    }
}


/// <summary>
/// Hands an already-rendered PDF to Android's print framework.
///
/// PrintDocumentAdapter is a Java abstract class, so it has to live here in the head payload rather
/// than in WindowsBase, exactly as WpfAccessibilityNodeProvider does and for the same reason: unlike
/// iOS, a Java subclass cannot be conjured through the runtime, because it is bytecode.
///
/// The framework's model is a callback one: it asks for a layout, then asks for the pages to be
/// written to a file descriptor, and may ask again whenever the user changes paper or range in the
/// print UI. This adapter renders NOTHING on demand -- it holds bytes that were rendered before the
/// UI opened. That is not laziness: rendering walks a WPF visual tree, which has thread affinity,
/// and onWrite arrives on a binder thread.
///
/// The consequence, and it is a real limitation: changing the paper size in the print UI re-lays out
/// nothing, because the document was paginated for the size the app chose. Android scales the pages
/// to fit instead.
/// </summary>
internal sealed class WpfPrintDocumentAdapter : Android.Print.PrintDocumentAdapter
{
    private readonly string _jobName;
    private readonly byte[] _document;
    private readonly Android.Print.PrintAttributes.MediaSize _mediaSize;

    internal WpfPrintDocumentAdapter(string jobName, byte[] document, Android.Print.PrintAttributes.MediaSize mediaSize)
    {
        _jobName = jobName;
        _document = document;
        _mediaSize = mediaSize;
    }

    public override void OnLayout(Android.Print.PrintAttributes? oldAttributes,
                                  Android.Print.PrintAttributes? newAttributes,
                                  Android.OS.CancellationSignal? cancellationSignal,
                                  LayoutResultCallback? callback,
                                  Android.OS.Bundle? extras)
    {
        if (cancellationSignal?.IsCanceled == true)
        {
            callback?.OnLayoutCancelled();
            return;
        }

        var info = new Android.Print.PrintDocumentInfo.Builder(_jobName)
            .SetContentType(Android.Print.PrintContentType.Document)!
            .SetPageCount(Android.Print.PrintDocumentInfo.PageCountUnknown)!
            .Build();

        // "true" says the content changed since the last layout. Saying false when it did not lets
        // Android skip re-writing the pages, but the document here never changes, so the honest and
        // cheap answer is that the first layout is a change and later ones are not.
        callback?.OnLayoutFinished(info, oldAttributes == null);
    }

    public override void OnWrite(Android.Print.PageRange[]? pages,
                                 Android.OS.ParcelFileDescriptor? destination,
                                 Android.OS.CancellationSignal? cancellationSignal,
                                 WriteResultCallback? callback)
    {
        if (destination == null)
        {
            callback?.OnWriteFailed("no destination");
            return;
        }

        try
        {
            using (var output = new Android.OS.ParcelFileDescriptor.AutoCloseOutputStream(destination))
            {
                output.Write(_document, 0, _document.Length);
                output.Flush();
            }

            if (cancellationSignal?.IsCanceled == true)
            {
                callback?.OnWriteCancelled();
                return;
            }

            // The whole document, whatever range was asked for. Android renders the PDF itself and
            // extracts the requested pages from it, so writing everything is correct and is what the
            // platform's own PdfDocument-based samples do.
            callback?.OnWriteFinished(new[] { Android.Print.PageRange.AllPages! });
        }
        catch (Java.IO.IOException e)
        {
            callback?.OnWriteFailed(e.Message);
        }
    }
}

/// <summary>
/// The Java-facing drag listener, one per view, forwarding android.view.DragEvent into WindowsBase.
///
/// Every WPF window's view gets one whether or not anything in it has AllowDrop set: which element
/// is under the pointer is not known until the drag arrives, and WPF decides. A registration that is
/// too narrow shows the user a drag passing over a window that would in fact have taken the data --
/// the same reasoning the Cocoa backend gives for registering every type it can map.
/// </summary>
internal sealed class WpfDragListener : Java.Lang.Object, View.IOnDragListener
{
    private readonly IntPtr _wpfHandle;

    public WpfDragListener(IntPtr wpfHandle) => _wpfHandle = wpfHandle;

    public bool OnDrag(View? view, DragEvent? e)
    {
        if (e is null) return false;

        int x = (int)e.GetX();
        int y = (int)e.GetY();
        // what WPF is told is read here, on the main thread the event belongs to; WPF hears it on its own
        string[] types = e.Action is DragAction.Started or DragAction.Location or DragAction.Drop ? MimeTypes(e) : Array.Empty<string>();

        switch (e.Action)
        {
            case DragAction.Started:
                // Returning false here unsubscribes this view from the WHOLE drag, drop included.
                return AndroidWindow.InvokeOnUi(() => AndroidDragDrop.NotifyDragStarted(_wpfHandle, types));

            case DragAction.Entered:
                // Carries no usable position; the location event that follows raises WPF's DragEnter.
                return true;

            case DragAction.Location:
                AndroidWindow.InvokeOnUi(() => { AndroidDragDrop.NotifyDragLocation(_wpfHandle, x, y, types); return true; });
                return true;

            case DragAction.Exited:
                AndroidWindow.InvokeOnUi(() => { AndroidDragDrop.NotifyDragExited(_wpfHandle); return true; });
                return true;

            case DragAction.Drop:
                ReadClip(e, out string[]? text, out string[]? uris);
                return AndroidWindow.InvokeOnUi(() => AndroidDragDrop.NotifyDrop(_wpfHandle, x, y, types, text, uris));

            case DragAction.Ended:
                AndroidWindow.InvokeOnUi(() => { AndroidDragDrop.NotifyDragEnded(_wpfHandle); return true; });
                return true;

            default:
                return false;
        }
    }

    private static string[] MimeTypes(DragEvent e)
    {
        ClipDescription? description = e.ClipDescription;
        if (description is null) return Array.Empty<string>();

        var types = new string[description.MimeTypeCount];
        for (int i = 0; i < types.Length; i++) types[i] = description.GetMimeType(i) ?? string.Empty;
        return types;
    }

    /// <summary>
    /// The dropped items, split by kind. ClipData is non-null ONLY on ACTION_DROP -- Android's own
    /// rule, so an app cannot read what is merely passing over it -- which is why this is the one
    /// place data is pulled out of a drag.
    /// </summary>
    private static void ReadClip(DragEvent e, out string[]? text, out string[]? uris)
    {
        text = null;
        uris = null;

        ClipData? clip = e.ClipData;
        if (clip is null) return;

        List<string>? texts = null;
        List<string>? links = null;
        for (int i = 0; i < clip.ItemCount; i++)
        {
            ClipData.Item? item = clip.GetItemAt(i);
            if (item is null) continue;

            if (item.Uri is Android.Net.Uri uri)
            {
                (links ??= new List<string>()).Add(uri.ToString()!);
            }
            else if (!string.IsNullOrEmpty(item.Text))
            {
                (texts ??= new List<string>()).Add(item.Text!);
            }
        }

        text = texts?.ToArray();
        uris = links?.ToArray();
    }
}

/// <summary>
/// The shadow the user drags. A stock View.DragShadowBuilder snapshots the view it is given, and the
/// view here is the whole WPF window -- a full-screen ghost following the finger, and a blank one at
/// that, since a SurfaceView's pixels are not in the view hierarchy's draw at all. So this draws a
/// small translucent tile instead: something is under the finger, and it is not the entire app.
/// </summary>
internal sealed class WpfDragShadow : View.DragShadowBuilder
{
    private const int SizeDip = 48;

    private readonly int _size;
    private readonly Paint _paint;

    public WpfDragShadow(View view) : base(view)
    {
        _size = Math.Max(1, (int)Math.Round(SizeDip * view.Resources!.DisplayMetrics!.Density));
        _paint = new Paint(PaintFlags.AntiAlias) { Color = Android.Graphics.Color.Argb(140, 120, 120, 120) };
    }

    public override void OnProvideShadowMetrics(Android.Graphics.Point? outShadowSize, Android.Graphics.Point? outShadowTouchPoint)
    {
        outShadowSize?.Set(_size, _size);
        outShadowTouchPoint?.Set(_size / 2, _size / 2);   // centred on the finger
    }

    public override void OnDrawShadow(Canvas? canvas)
    {
        if (canvas is null) return;
        float radius = _size * 0.2f;
        canvas.DrawRoundRect(0f, 0f, _size, _size, radius, radius, _paint);
    }
}

internal sealed class AndroidHost : IAndroidHost, IAndroidAccessibilityHost, IAndroidAccessibilityEventHost, IAndroidPrintHost, IAndroidClipboardHost, IAndroidDialogHost, IAndroidDragDropHost
{
    private readonly Activity _activity;
    private readonly FrameLayout _root;
    private readonly Android.OS.Handler _handler;
    private readonly Dictionary<IntPtr, View> _views = new();

    public bool IsNightMode => _activity.Resources?.Configuration is { } c && (c.UiMode & UiMode.NightMask) == UiMode.NightYes;


    /// <summary>
    /// Puts Android's system print UI up with a rendered document. Must run on the UI thread, which
    /// is where a print request from WPF already is.
    /// </summary>
    public bool Print(string jobName, byte[] document, double pageWidth, double pageHeight) => OnMain(() => PrintCore(jobName, document, pageWidth, pageHeight));

    private bool PrintCore(string jobName, byte[] document, double pageWidth, double pageHeight)
    {
        if (document == null || document.Length == 0) return false;

        var manager = (Android.Print.PrintManager?)_activity.GetSystemService(Context.PrintService);
        if (manager == null) return false;

        // Android states media sizes in THOUSANDTHS OF AN INCH; WPF measures in 96ths. A page that
        // comes out at 96/1000 of its size is what happens when this conversion is missed.
        int milsWide = (int)Math.Round(pageWidth / 96.0 * 1000.0);
        int milsHigh = (int)Math.Round(pageHeight / 96.0 * 1000.0);

        var media = new Android.Print.PrintAttributes.MediaSize("wpf-page", "Document", milsWide, milsHigh);

        var attributes = new Android.Print.PrintAttributes.Builder()
            .SetMediaSize(media)!
            .SetResolution(new Android.Print.PrintAttributes.Resolution("wpf", "WPF", 300, 300))!
            .SetMinMargins(Android.Print.PrintAttributes.Margins.NoMargins!)!
            .Build();

        var adapter = new WpfPrintDocumentAdapter(jobName, document, media);
        manager.Print(jobName, adapter, attributes);
        return true;
    }

    public AndroidHost(Activity activity, FrameLayout root)
    {
        _activity = activity;
        _root = root;
        _handler = new Android.OS.Handler(Android.OS.Looper.MainLooper!);
        // the web views (WebView2, WebBrowser): android.webkit.WebView, which the seam reaches through
        // AppContext (AndroidWebViewRegistration) -- this activity's, the latest one's
        _webViews = new AndroidWebViewHost(this);
        AppContext.SetData("MS.Internal.Interop.WebView.Android.Invoke", (Func<string, object[], object?>)_webViews.Invoke);
    }

    private readonly AndroidWebViewHost _webViews;

    // ---- for the web views (AndroidWebViewHost) ----------------------------------

    internal Activity Activity => _activity;

    internal FrameLayout Root => _root;

    internal T RunOnMain<T>(Func<T> work) => OnMain(work);

    internal void Post(Action work) => _handler.Post(work);

    /// <summary>Where a window's client area is in the activity, in device pixels, whether it is shown,
    /// and its view; false when the handle names no window.</summary>
    internal bool WindowPlace(IntPtr handle, out int x, out int y, out bool visible, out View? view)
    {
        x = y = 0; visible = false; view = null;
        if (!_views.TryGetValue(handle, out view) || view is null) return false;
        if (view.LayoutParameters is FrameLayout.LayoutParams lp && lp.Width != ViewGroup.LayoutParams.MatchParent)
        {
            x = lp.LeftMargin;
            y = lp.TopMargin;
        }
        visible = view.Visibility == ViewStates.Visible;
        return true;
    }

    // ---- the hardware keyboard -------------------------------------------------

    /// <summary>
    /// A hardware key for the app, from the activity's dispatchKeyEvent -- before any view, so the keys
    /// reach the app whichever view Android has focused, as Windows gives them to the active window.
    /// False for what the system keeps (Back, Home, the volume) and for a repeat Android synthesises
    /// while a key is held, which arrives as another down, as Windows' auto-repeat does.
    /// </summary>
    public bool DispatchKey(KeyEvent? e)
    {
        if (e is null || e.Action is not (KeyEventActions.Down or KeyEventActions.Up)) return false;
        return AndroidWindow.NotifyKey(e.Action == KeyEventActions.Down, (int)e.KeyCode, e.GetUnicodeChar((int)e.MetaState), (int)e.MetaState);
    }

    // ---- the app's thread ------------------------------------------------------

    /// <summary>
    /// Runs the app's <paramref name="main"/> on a UI thread of its own -- WPF's dispatcher blocks there
    /// as on a desktop, which Android's main Looper cannot let it do -- and ends the process when it
    /// returns, as an app's process ends with its Main on Windows. Call it once the activity has its
    /// content view (after the first layout pass), from the main thread.
    /// </summary>
    public void RunApp(Action main)
    {
        // the stack Windows gives a main thread is 1 MB; a desktop app's layout and BAML recursion is
        // that deep, and a phone's default for a new thread is less
        var thread = new System.Threading.Thread(() =>
        {
            try { main(); }
            catch (Exception e) { Console.WriteLine($"WPF Android: the app ended with an exception: {e}"); }
            _activity.RunOnUiThread(() =>
            {
                _activity.FinishAndRemoveTask();
                Java.Lang.JavaSystem.Exit(0);
            });
        }, 8 * 1024 * 1024)
        {
            Name = "UI",
        };
        thread.Start();
    }

    // ---- the main Looper -------------------------------------------------------
    //
    // The app runs on a UI thread of its own -- WPF's dispatcher blocks there as on every desktop
    // head, so its modal loops work -- and Android's views belong to the main Looper's thread. What
    // this host does to a view, the clipboard, the print or drag service is done there, and the
    // caller waits for it, as a SendMessage to another thread's window waits on Windows. The main
    // thread never waits on the app's (its callbacks are posted to the app's dispatcher), so the
    // wait cannot deadlock.

    private T OnMain<T>(Func<T> work)
    {
        if (Android.OS.Looper.MyLooper() == Android.OS.Looper.MainLooper) return work();
        T result = default!;
        Exception? error = null;
        using var done = new System.Threading.ManualResetEventSlim();
        _handler.Post(() =>
        {
            try { result = work(); }
            catch (Exception e) { error = e; }
            finally { done.Set(); }
        });
        AndroidWindow.WaitServingMain(done.WaitHandle);
        if (error != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(error);
        return result;
    }

    private void OnMain(Action work) => OnMain(() => { work(); return true; });

    // ---- views ----------------------------------------------------------------

    public bool CreateView(IntPtr handle, int x, int y, int width, int height, bool borderless, bool fillsScreen, bool ownSurface)
        => OnMain(() => CreateViewCore(handle, x, y, width, height, borderless, fillsScreen, ownSurface));

    // ---- notifications (a NotifyIcon's balloon tip) ------------------------------------------------------
    //
    // Windows 10 shows a balloon as a toast; here it is a notification of the app's, in a channel of its own. A click
    // and a swipe away come back through a receiver of the app's (registered as it runs, not exported) and are told
    // to the icon's window as the shell tells it (AndroidWindow.NotificationEvent: NIN_BALLOONUSERCLICK, TIMEOUT).

    private const string NotificationChannel = "wpfwebgpu-notifications";
    private string? _notificationAction;
    private NotificationReceiver? _notificationReceiver;

    public bool ShowNotification(int tag, string title, string text, int kind)
    {
        Activity activity = _activity;
        if (activity.GetSystemService(Context.NotificationService) is not NotificationManager manager) return false;
        if (Build.VERSION.SdkInt >= BuildVersionCodes.O && manager.GetNotificationChannel(NotificationChannel) is null)
            manager.CreateNotificationChannel(new NotificationChannel(NotificationChannel, "Notifications", NotificationImportance.High));
        // Android 13: an app asks for leave to notify (the manifest declares it); the first balloon asks
        if (Build.VERSION.SdkInt >= BuildVersionCodes.Tiramisu
            && activity.CheckSelfPermission("android.permission.POST_NOTIFICATIONS") != Android.Content.PM.Permission.Granted)
            OnMain(() => activity.RequestPermissions(new[] { "android.permission.POST_NOTIFICATIONS" }, 0x5EED));
        if (_notificationReceiver is null)
        {
            _notificationAction = activity.PackageName + ".WPFWEBGPU_NOTIFICATION";
            _notificationReceiver = new NotificationReceiver();
            var filter = new IntentFilter(_notificationAction);
            if (Build.VERSION.SdkInt >= BuildVersionCodes.Tiramisu) activity.RegisterReceiver(_notificationReceiver, filter, ReceiverFlags.NotExported);
            else activity.RegisterReceiver(_notificationReceiver, filter);
        }
        PendingIntent Pending(int what)
        {
            var intent = new Intent(_notificationAction).SetPackage(activity.PackageName).PutExtra("tag", tag).PutExtra("what", what);
            return PendingIntent.GetBroadcast(activity, tag * 4 + what, intent, PendingIntentFlags.Immutable | PendingIntentFlags.UpdateCurrent)!;
        }
        int icon = kind switch { 2 or 3 => Android.Resource.Drawable.IcDialogAlert, _ => Android.Resource.Drawable.IcDialogInfo };
#pragma warning disable CA1422 // Notification.Builder(Context) is the one below API 26
        Notification.Builder builder = Build.VERSION.SdkInt >= BuildVersionCodes.O
            ? new Notification.Builder(activity, NotificationChannel)
            : new Notification.Builder(activity);
#pragma warning restore CA1422
        builder.SetContentTitle(title).SetContentText(text).SetSmallIcon(icon).SetAutoCancel(true)
               .SetContentIntent(Pending(2)).SetDeleteIntent(Pending(3));
        manager.Notify("wpfwebgpu", tag, builder.Build());
        AndroidWindow.NotifyNotification(tag, 1);
        return true;
    }

    private sealed class NotificationReceiver : BroadcastReceiver
    {
        public override void OnReceive(Context? context, Intent? intent)
        {
            if (intent is null) return;
            int tag = intent.GetIntExtra("tag", -1), what = intent.GetIntExtra("what", 0);
            if (tag >= 0 && what > 0) AndroidWindow.NotifyNotification(tag, what);
        }
    }

    public void BringToFront(IntPtr handle) => OnMain(() =>
    {
        if (!_views.TryGetValue(handle, out View? view) || view is null) return;
        // the frame first, then the window over it: brought to the front, not taken out and put back -- a view taken
        // out of the activity has its touch cancelled, and a dialog's caption pressed (which activates it) lost the
        // drag it began, its release taken at the frame's corner
        if (_frames.TryGetValue(handle, out View? frameView)) frameView.BringToFront();
        view.BringToFront();
        PlaceFrame(handle);
        // a floating window's surface is composed above the app's own (SurfaceViews are layers of their
        // own, stacked by this rather than by the view order); a popup's is on top already
        if (view is WpfSurfaceView { FillsScreen: false, IsPopup: false } surface) surface.SetZOrderMediaOverlay(true);
        // its child windows come with it, just above it (SetViewParent), and its web views over it --
        // a SurfaceView's own pixels in the view order are a hole through to its surface, which covered
        // a web view left under it...
        RaiseChildren(handle);
        _webViews.RaiseOver(handle);
        // ...and the popups stay above it, as they are drawn: a popup's surface is on top of every
        // window's (or its pixels composed over every window, WpfPopupView), so its view must lie over
        // theirs too, for touches go by the view order -- under a window brought to the front, a menu or
        // a floating tool window showed and could not be touched. Not a child window (an ElementHost's
        // WPF content), which is a touch target of the same kind: raised over every window, it lay over
        // a dialog shown above its form and took the dialog's touches.
        var popups = new List<View>();
        for (int i = 0; i < _root.ChildCount; i++)
        {
            View? child = _root.GetChildAt(i);
            if (child is WpfPopupView popup && !_parents.ContainsKey(popup.Handle)) popups.Add(popup);
            else if (child is WpfSurfaceView { IsPopup: true } surfacePopup) popups.Add(surfacePopup);
        }
        foreach (View popup in popups) popup.BringToFront();
        UpdateModalAccessibility();
    });

    /// <summary>A popup has a see-through surface of its own here, on top of every window (CreateView).</summary>
    public bool PopupSurfaces => true;

    public void SetViewTranslucent(IntPtr handle) => OnMain(() =>
    {
        // a SurfaceView's surface is opaque unless its format says otherwise; composed in the media-overlay
        // layer (a floating window's, BringToFront), what it leaves transparent shows the windows under it
        if (_views.TryGetValue(handle, out View? view) && view is WpfSurfaceView surface)
            surface.Holder?.SetFormat(Android.Graphics.Format.Translucent);
    });

    // child window -> parent window (SetViewParent)
    private readonly Dictionary<IntPtr, IntPtr> _parents = new();

    public void SetViewParent(IntPtr child, IntPtr parent) => OnMain(() =>
    {
        if (child == parent || !_views.ContainsKey(child) || !_views.ContainsKey(parent)) return;
        _parents[child] = parent;
        PlaceChildren(parent);
    });

    /// <summary>The child windows of <paramref name="parent"/> (and theirs) to the front, in turn: run
    /// right after the parent itself came to the front, they end up just above it.</summary>
    private void RaiseChildren(IntPtr parent)
    {
        foreach (IntPtr child in ChildrenOf(parent))
        {
            if (_views.TryGetValue(child, out View? view) && view is not null) view.BringToFront();
            RaiseChildren(child);
        }
    }

    /// <summary>The child windows of <paramref name="parent"/> (and theirs) just above it in the view order,
    /// wherever it is: a child given its parent after windows were stacked over the parent.</summary>
    private void PlaceChildren(IntPtr parent)
    {
        if (!_views.TryGetValue(parent, out View? parentView) || parentView is null) return;
        int index = _root.IndexOfChild(parentView);
        foreach (IntPtr child in ChildrenOf(parent))
        {
            if (!_views.TryGetValue(child, out View? view) || view is null) continue;
            // a child is a touch target with no surface of its own (WpfPopupView): moving it in the
            // order costs nothing
            if (_root.IndexOfChild(view) != index + 1)
            {
                var lp = view.LayoutParameters;
                _root.RemoveView(view);
                index = _root.IndexOfChild(parentView);
                _root.AddView(view, index + 1, lp);
            }
            PlaceChildren(child);
            index = _root.IndexOfChild(view);
        }
    }

    private List<IntPtr> ChildrenOf(IntPtr parent)
    {
        var children = new List<IntPtr>();
        foreach (var (child, p) in _parents) if (p == parent) children.Add(child);
        return children;
    }

    private bool CreateViewCore(IntPtr handle, int x, int y, int width, int height, bool borderless, bool fillsScreen, bool ownSurface)
    {
        // A window drawn into the windows under it needs no surface of its own -- only a touch target.
        // See WpfPopupView. A window has one: the app's own, a dialog floating over it, a borderless
        // form, and a popup (AndroidWindow.IsSeeThroughPopup).
        bool popup = borderless && ownSurface && AndroidWindow.FromHandle(handle) is { IsSeeThroughPopup: true };
        View view = borderless && !ownSurface
            ? new WpfPopupView(_activity, handle)
            : new WpfSurfaceView(_activity, handle) { FillsScreen = fillsScreen, IsPopup = popup };
        if (view is WpfSurfaceView { IsPopup: true } popupSurface)
        {
            // A popup's surface keeps its alpha -- its rounded chrome and drop shadow lie on a transparent
            // background, which the system compositor blends over what is under it -- and is composed on
            // top: above the app's window, so above the frames drawn in it round the floating windows,
            // and above their surfaces (media overlays), as a popup is over every window on Windows.
            // Two popups on top are stacked as they were made: a submenu over its menu, a tooltip over both.
            popupSurface.Holder?.SetFormat(Android.Graphics.Format.Translucent);
            popupSurface.SetZOrderOnTop(true);
        }
        // a floating window's surface composes above the app's own; see BringToFront
        else if (view is WpfSurfaceView floating && !fillsScreen) floating.SetZOrderMediaOverlay(true);

        // A top-level window IS the activity's content area, so it must FOLLOW it -- MatchParent is
        // the Android spelling of the autoresizing mask UIKitWindow gives its view. Sizing it to the
        // pixels WPF asked for instead pins it to whatever the screen was when it was created, and a
        // rotation then leaves WPF rendering into the old portrait rectangle on a landscape screen.
        // Popups are the opposite: they are placed at an absolute pixel offset, which FrameLayout
        // expresses as gravity + margins (there is no non-obsolete AbsoluteLayout).
        var lp = !fillsScreen
            ? new FrameLayout.LayoutParams(width, height)
            {
                Gravity = GravityFlags.Top | GravityFlags.Left,
                LeftMargin = x,
                TopMargin = y,
            }
            : new FrameLayout.LayoutParams(
                ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.MatchParent);

        // Every window listens for drags; WPF decides whether the element under the pointer wants one.
        view.SetOnDragListener(new WpfDragListener(handle));

        _root.AddView(view, lp);
        _views[handle] = view;
        // Windows' frame round a window with a caption (AndroidWindow.HasFrame): a view of the outer rectangle just
        // under the window's, which it draws and takes the frame's touches of
        if (!fillsScreen && AndroidWindow.FromHandle(handle) is { HasFrame: true } framed)
        {
            framed.GetFrameRect(out int fx, out int fy, out int fw, out int fh);
            var frame = new WpfFrameView(_activity, handle);
            var flp = new FrameLayout.LayoutParams(fw, fh) { Gravity = GravityFlags.Top | GravityFlags.Left, LeftMargin = fx, TopMargin = fy };
            _root.AddView(frame, _root.IndexOfChild(view), flp);
            _frames[handle] = frame;
        }

        return true;
    }

    // the frames of the windows that have one (WpfFrameView), by the window
    private readonly Dictionary<IntPtr, View> _frames = new();

    /// <summary>A flat activity's windows are framed as Windows frames them: a dialog's caption, edges and close button.</summary>
    public bool DrawsFrames => true;

    public void FrameChanged(IntPtr handle) => OnMain(() => { if (_frames.TryGetValue(handle, out View? frame)) frame.Invalidate(); });

    /// <summary>A window's frame to its outer rectangle, and just under the window in the view order.</summary>
    private void PlaceFrame(IntPtr handle)
    {
        if (!_frames.TryGetValue(handle, out View? frame) || !_views.TryGetValue(handle, out View? view) || view is null) return;
        if (AndroidWindow.FromHandle(handle) is { HasFrame: true } framed)
        {
            framed.GetFrameRect(out int fx, out int fy, out int fw, out int fh);
            var flp = (FrameLayout.LayoutParams)frame.LayoutParameters!;
            if (flp.LeftMargin != fx || flp.TopMargin != fy || flp.Width != fw || flp.Height != fh)
            {
                flp.LeftMargin = fx; flp.TopMargin = fy; flp.Width = fw; flp.Height = fh;
                frame.LayoutParameters = flp;
            }
        }
        frame.Visibility = view.Visibility;
        frame.Invalidate();
    }

    public void SetViewFrame(IntPtr handle, int x, int y, int width, int height) => OnMain(() => SetViewFrameCore(handle, x, y, width, height));

    private void SetViewFrameCore(IntPtr handle, int x, int y, int width, int height)
    {
        if (!_views.TryGetValue(handle, out View? view)) return;

        var lp = (FrameLayout.LayoutParams)view.LayoutParameters!;
        if (lp.Width == ViewGroup.LayoutParams.MatchParent)
            return;   // a top-level window follows the activity; see CreateView

        lp.LeftMargin = x;
        lp.TopMargin = y;
        lp.Width = width;
        lp.Height = height;
        view.LayoutParameters = lp;
        PlaceFrame(handle);
        _webViews.WindowMoved(handle);
    }

    public void SetViewVisible(IntPtr handle, bool visible) => OnMain(() => SetViewVisibleCore(handle, visible));

    private void SetViewVisibleCore(IntPtr handle, bool visible)
    {
        if (!_views.TryGetValue(handle, out View? view) || view is null) return;
        // Gone, not Invisible: an Invisible view still occupies its slot for layout and hit-testing,
        // and a hidden WPF window must not intercept touches meant for what is behind it.
        view.Visibility = visible ? ViewStates.Visible : ViewStates.Gone;
        PlaceFrame(handle);
        _webViews.WindowMoved(handle);
        UpdateModalAccessibility();
    }

    public void InvalidateAccessibilityNode(IntPtr handle, int nodeId) => OnMain(() => InvalidateAccessibilityNodeCore(handle, nodeId));

    private void InvalidateAccessibilityNodeCore(IntPtr handle, int nodeId)
    {
        if (!_views.TryGetValue(handle, out View? view) || view is null) return;
        view.SendAccessibilityEvent(Android.Views.Accessibility.EventTypes.WindowContentChanged);
    }

    public void SendAccessibilityEvent(IntPtr handle, int nodeId, string eventJson) => OnMain(() => SendAccessibilityEventCore(handle, nodeId, eventJson));

    /// <summary>
    /// One event of AndroidAccessibility's vocabulary, sent from a virtual node as the matching
    /// widget sends it: through the parent's requestSendAccessibilityEvent, which is how an
    /// AccessibilityNodeProvider's events reach the service with the virtual node as their source.
    /// </summary>
    private void SendAccessibilityEventCore(IntPtr handle, int nodeId, string eventJson)
    {
        if (!_views.TryGetValue(handle, out View? view) || view is null) return;
        var n = new AccessibilityJson(eventJson);
        int kind = n.Int("ev");

        if (kind == 5)
        {
            WindowPaneChanged(handle, view, n.Str("pane"), n.Int("modal") != 0);
            return;
        }

        Android.Views.Accessibility.EventTypes type = kind switch
        {
            1 => Android.Views.Accessibility.EventTypes.ViewTextChanged,
            2 => Android.Views.Accessibility.EventTypes.ViewTextSelectionChanged,
            3 => Android.Views.Accessibility.EventTypes.ViewScrolled,
            4 => Android.Views.Accessibility.EventTypes.ViewSelected,
            _ => 0,
        };
        if (type == 0) return;
        if (_activity.GetSystemService(Context.AccessibilityService) is not Android.Views.Accessibility.AccessibilityManager { IsEnabled: true })
            return;

        var e = Android.Views.Accessibility.AccessibilityEvent.Obtain(type)!;
        e.PackageName = _activity.PackageName;
        e.ClassName = n.Str("cls");
        e.SetSource(view, nodeId);
        switch (kind)
        {
            case 1:
                e.FromIndex = n.Int("from");
                e.AddedCount = n.Int("add");
                e.RemovedCount = n.Int("rem");
                e.BeforeText = n.Str("before");
                e.Text?.Add(new Java.Lang.String(n.Str("text")));
                e.Password = n.Int("pw") != 0;
                break;
            case 2:
                e.FromIndex = n.Int("from");
                e.ToIndex = n.Int("to");
                e.ItemCount = n.Int("count");
                e.Text?.Add(new Java.Lang.String(n.Str("text")));
                e.Password = n.Int("pw") != 0;
                break;
            case 3:
                e.ScrollX = n.Int("sx");
                e.ScrollY = n.Int("sy");
                e.MaxScrollX = n.Int("mx");
                e.MaxScrollY = n.Int("my");
                if (Build.VERSION.SdkInt >= BuildVersionCodes.P)
                {
                    e.ScrollDeltaX = n.Int("dx");
                    e.ScrollDeltaY = n.Int("dy");
                }
                break;
            case 4:
                e.Text?.Add(new Java.Lang.String(n.Str("text")));
                break;
        }
        view.Parent?.RequestSendAccessibilityEvent(view, e);
    }

    // ---- modal windows ----------------------------------------------------------
    //
    // Every WPF window is a view of its own in the one activity, so to TalkBack a modal dialog's
    // view is just one more beside the window it blocks, and swiping walks straight out of it into
    // controls that cannot be used. An AlertDialog does not have that problem because it is a
    // window of its own; the nearest Android has for views is importantForAccessibility: the views
    // under the topmost modal window are hidden with their descendants, as the dialog's own window
    // hides the activity, and given back when it closes.

    private readonly HashSet<IntPtr> _modal = new();
    private readonly HashSet<View> _hiddenByModal = new();

    private void WindowPaneChanged(IntPtr handle, View view, string pane, bool modal)
    {
        // A floating window's title is its pane's, which TalkBack announces as it appears; the app's
        // own window fills the activity, whose label already says it.
        if (Build.VERSION.SdkInt >= BuildVersionCodes.P && view is WpfSurfaceView { FillsScreen: false, IsPopup: false })
        {
            string? title = pane.Length != 0 ? pane : null;
            if (view.AccessibilityPaneTitle != title) view.AccessibilityPaneTitle = title;
        }

        if (modal) _modal.Add(handle);
        else _modal.Remove(handle);
        UpdateModalAccessibility();
    }

    private void UpdateModalAccessibility()
    {
        if (_modal.Count == 0 && _hiddenByModal.Count == 0) return;

        View? top = null;
        int topIndex = -1;
        foreach (IntPtr handle in _modal)
        {
            if (!_views.TryGetValue(handle, out View? view) || view is null || view.Visibility != ViewStates.Visible) continue;
            int index = _root.IndexOfChild(view);
            if (index > topIndex) { topIndex = index; top = view; }
        }

        // Only what is UNDER it: a popup the dialog opens (a combo box's list) is above it, and
        // must stay readable.
        foreach (View? view in _views.Values)
        {
            if (view is null) continue;
            bool hide = top != null && view != top && _root.IndexOfChild(view) < topIndex;
            if (hide)
            {
                if (_hiddenByModal.Add(view)) view.ImportantForAccessibility = ImportantForAccessibility.NoHideDescendants;
            }
            else if (_hiddenByModal.Remove(view))
            {
                view.ImportantForAccessibility = ImportantForAccessibility.Auto;
            }
        }
    }

    public void ShowSoftKeyboard(IntPtr handle, bool multiline, bool password) => OnMain(() => ShowSoftKeyboardCore(handle, multiline, password));

    private void ShowSoftKeyboardCore(IntPtr handle, bool multiline, bool password)
    {
        if (!_views.TryGetValue(handle, out View? view) || view is null) return;

        // Focus first: the input method attaches to the focused view, and asking for the keyboard
        // over an unfocused one silently does nothing.
        view.FocusableInTouchMode = true;
        view.RequestFocus();

        if (_activity.GetSystemService(Context.InputMethodService) is InputMethodManager imm)
        {
            imm.ShowSoftInput(view, ShowFlags.Implicit);
        }
    }

    public void HideSoftKeyboard(IntPtr handle) => OnMain(() => HideSoftKeyboardCore(handle));

    private void HideSoftKeyboardCore(IntPtr handle)
    {
        if (!_views.TryGetValue(handle, out View? view) || view is null) return;

        if (_activity.GetSystemService(Context.InputMethodService) is InputMethodManager imm)
        {
            imm.HideSoftInputFromWindow(view.WindowToken, HideSoftInputFlags.None);
        }
    }

    public void SetImeCursorRect(IntPtr handle, int x, int y, int width, int height) => OnMain(() => SetImeCursorRectCore(handle, x, y, width, height));

    private void SetImeCursorRectCore(IntPtr handle, int x, int y, int width, int height)
    {
        if (!_views.TryGetValue(handle, out View? view) || view is null) return;

        if (_activity.GetSystemService(Context.InputMethodService) is InputMethodManager imm)
        {
            // updateCursorAnchorInfo is the richer API, but it needs a full editor; the rectangle
            // alone is what keeps the candidate strip off the caret.
            imm.UpdateCursor(view, x, y, x + width, y + height);
        }
    }

    public void DestroyView(IntPtr handle) => OnMain(() => DestroyViewCore(handle));

    private void DestroyViewCore(IntPtr handle)
    {
        if (!_views.Remove(handle, out View? view)) return;
        _webViews.WindowDestroyed(handle);
        if (view is WpfSurfaceView surfaceView) surfaceView.Detached = true;
        // a modal window closing gives the windows under it back to TalkBack
        _modal.Remove(handle);
        _hiddenByModal.Remove(view);
        UpdateModalAccessibility();
        if (_frames.Remove(handle, out View? frame)) { _root.RemoveView(frame); frame.Dispose(); }
        _parents.Remove(handle);
        foreach (IntPtr child in ChildrenOf(handle)) _parents.Remove(child);
        _root.RemoveView(view);
        view.Dispose();
    }

    // ---- drag and drop --------------------------------------------------------
    //
    // IAndroidDragDropHost: the outgoing half. The incoming half is WpfDragListener, attached to
    // every view in CreateView. See the header of AndroidDragDrop.cs for what Android's model can
    // and cannot carry.

    public bool StartDrag(IntPtr handle, string[] mimeTypes, string? text, string[]? uris) => OnMain(() => StartDragCore(handle, mimeTypes, text, uris));

    private bool StartDragCore(IntPtr handle, string[] mimeTypes, string? text, string[]? uris)
    {
        if (!_views.TryGetValue(handle, out View? view) || view is null) return false;

        // startDragAndDrop, and DRAG_FLAG_GLOBAL with it, is API 24. Below that a drag can only be
        // local, and this head has a better local drag already (ManagedDragLoop) -- so decline and
        // let the caller use it rather than start a worse one.
        if (Build.VERSION.SdkInt < BuildVersionCodes.N) return false;

        ClipData? clip = BuildClip(mimeTypes, text, uris);
        if (clip is null) return false;

        // GLOBAL takes the drag outside the app (in multi-window; harmless otherwise), and
        // GLOBAL_URI_READ is what makes a dropped file readable by whoever receives it -- without it
        // a file drop arrives as a URI the other app is not permitted to open.
        // The flags parameter is bound as a plain int, not as DragFlags.
        return view.StartDragAndDrop(clip, new WpfDragShadow(view), null,
                                     (int)(DragFlags.Global | DragFlags.GlobalUriRead));
    }

    /// <summary>
    /// A ClipData carrying what Android can carry, described by everything WPF offered.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The description lists every MIME type of the drag, including ones with no item behind them:
    /// a receiver picks from that list, and our own windows recognise the in-process marker in it
    /// and take the original data object instead of anything on the wire. An item is still needed --
    /// a ClipData must have at least one -- so a drag with neither text nor URIs gets an empty text
    /// item to hang the description on.
    /// </para>
    /// <para>
    /// A <c>file://</c> URI is the exception, and it is not a small one: a global drag calls
    /// ClipData.prepareToLeaveProcess, and StrictMode turns any raw file URI still in the clip into
    /// a FileUriExposedException -- which is exactly what WPF's DataFormats.FileDrop produces. Making
    /// a file shareable needs a content:// URI from a FileProvider, and a FileProvider is a manifest
    /// entry in the APPLICATION, which a program that merely hosts WPF has not got. This is the same
    /// wall AndroidClipboard documents for non-text data. So content: URIs are carried, file: ones
    /// are dropped, and text/uri-list is not advertised when nothing survived to serve it -- better
    /// than promising a format and handing over an exception.
    /// </para>
    /// </remarks>
    private static ClipData? BuildClip(string[] mimeTypes, string? text, string[]? uris)
    {
        List<ClipData.Item>? items = null;
        bool droppedFileUri = false;
        if (uris is not null)
        {
            foreach (string uri in uris)
            {
                if (string.IsNullOrEmpty(uri)) continue;
                if (uri.StartsWith("file:", StringComparison.OrdinalIgnoreCase)) { droppedFileUri = true; continue; }
                (items ??= new List<ClipData.Item>()).Add(new ClipData.Item(Android.Net.Uri.Parse(uri)));
            }
        }

        if (droppedFileUri)
        {
            Console.WriteLine("WPF Android: file:// URIs were left out of a drag -- Android only lets a " +
                              "content:// URI leave the process, which needs a FileProvider in the app.");
        }

        string[] types = mimeTypes is { Length: > 0 } ? mimeTypes : new[] { ClipDescription.MimetypeTextPlain };
        if (items is null) types = Array.FindAll(types, t => t != "text/uri-list");
        if (types.Length == 0) types = new[] { ClipDescription.MimetypeTextPlain };
        var description = new ClipDescription("WPF", types);

        ClipData? clip = null;
        if (items is not null)
        {
            foreach (ClipData.Item item in items)
            {
                if (clip is null) clip = new ClipData(description, item);
                else clip.AddItem(item);
            }
        }

        if (text is not null)
        {
            var item = new ClipData.Item(text);
            if (clip is null) clip = new ClipData(description, item);
            else clip.AddItem(item);
        }

        return clip ?? new ClipData(description, new ClipData.Item(string.Empty));
    }

    // ---- clipboard ------------------------------------------------------------
    //
    // IAndroidClipboardHost, so a WPF Ctrl+C reaches the SYSTEM clipboard and can be pasted into
    // another app. android.content.ClipboardManager needs a Context, which is why this half lives in
    // the payload rather than in WindowsBase; see the header of AndroidClipboard.cs.
    //
    // Main-thread only: the app's UI thread is not the main Looper's, so each call is run there (OnMain).

    private ClipboardManager? Clipboard =>
        _activity.GetSystemService(Context.ClipboardService) as ClipboardManager;

    public void Clear() => OnMain(() => ClearCore());

    private void ClearCore()
    {
        ClipboardManager? clipboard = Clipboard;
        if (clipboard is null) return;

        // ClearPrimaryClip is API 28+. Below that the nearest thing is an empty clip, which leaves
        // the clipboard holding an empty string rather than nothing -- the observable difference is
        // only whether a paste target thinks it has something to paste, and an empty string is the
        // closer of the two available answers.
        if (Build.VERSION.SdkInt >= BuildVersionCodes.P)
        {
            clipboard.ClearPrimaryClip();
        }
        else
        {
            clipboard.PrimaryClip = ClipData.NewPlainText("", "");
        }
    }

    public void SetText(string value) => OnMain(() => SetTextCore(value));

    private void SetTextCore(string value)
    {
        ClipboardManager? clipboard = Clipboard;
        if (clipboard is null) return;

        // The first argument is a user-visible LABEL, not the payload: Android shows it in the
        // clipboard UI on some versions. The app's own name is the honest thing to put there.
        clipboard.PrimaryClip = ClipData.NewPlainText(_activity.PackageName ?? "WPF", value ?? "");
    }

    public string? GetText() => OnMain(() => GetTextCore());

    private string? GetTextCore()
    {
        ClipboardManager? clipboard = Clipboard;
        ClipData? clip = clipboard?.PrimaryClip;
        if (clip is null || clip.ItemCount == 0) return null;

        // CoerceToText rather than Item.Text: a clip put there by another app may be a URI or an
        // Intent, and coercion is Android's own documented way of asking "what would this look like
        // as text", which is exactly what a paste into a TextBox wants.
        return clip.GetItemAt(0)?.CoerceToText(_activity);
    }

    // Anything but text: Android's clipboard carries a content:// URI, which the app's own provider
    // (ClipboardProvider, below) serves from its cache -- the clip's bytes in a file there. Another
    // app reads it with the permission Android grants a clipboard URI's reader (grantUriPermissions);
    // a clip another app put there is read through the content resolver the same way.

    public bool SetData(string mimeType, byte[] data) => OnMain(() => SetDataCore(mimeType, data));

    private bool SetDataCore(string mimeType, byte[] data)
    {
        ClipboardManager? clipboard = Clipboard;
        if (clipboard is null || data is null) return false;
        Java.IO.File folder = ClipboardProvider.Folder(_activity);
        folder.Mkdirs();
        // only the current clip is kept: what was copied before is no longer on the clipboard
        foreach (Java.IO.File old in folder.ListFiles() ?? Array.Empty<Java.IO.File>()) old.Delete();
        string name = "clip-" + DateTime.UtcNow.Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture) + ClipboardProvider.ExtensionOf(mimeType);
        System.IO.File.WriteAllBytes(System.IO.Path.Combine(folder.AbsolutePath, name), data);
        Android.Net.Uri uri = Android.Net.Uri.Parse($"content://{_activity.PackageName}{ClipboardProvider.AuthoritySuffix}/{name}")!;
        clipboard.PrimaryClip = ClipData.NewUri(_activity.ContentResolver, _activity.PackageName ?? "WPF", uri);
        return true;
    }

    public byte[]? GetData(string mimeType) => OnMain(() => GetDataCore(mimeType));

    private byte[]? GetDataCore(string mimeType)
    {
        ClipData? clip = Clipboard?.PrimaryClip;
        if (clip is null || clip.ItemCount == 0) return null;
        Android.Net.Uri? uri = clip.GetItemAt(0)?.Uri;
        ContentResolver? resolver = _activity.ContentResolver;
        if (uri is null || resolver is null) return null;
        try
        {
            string? type = resolver.GetType(uri);
            using System.IO.Stream? input = resolver.OpenInputStream(uri);
            if (input is null) return null;
            var bytes = new System.IO.MemoryStream();
            input.CopyTo(bytes);
            if (type == mimeType || mimeType == "application/octet-stream") return bytes.ToArray();
            // an image of another kind (JPEG, WebP, ...) for a PNG asked for: decoded and encoded as PNG
            if (mimeType == AndroidClipboard.TypePng && type is not null && type.StartsWith("image/", StringComparison.Ordinal))
            {
                using Bitmap? bitmap = BitmapFactory.DecodeByteArray(bytes.ToArray(), 0, (int)bytes.Length);
                if (bitmap is null) return null;
                var png = new System.IO.MemoryStream();
                bitmap.Compress(Bitmap.CompressFormat.Png!, 100, png);
                return png.ToArray();
            }
            return null;
        }
        catch (Java.Lang.Exception) { return null; }
        catch (System.IO.IOException) { return null; }
    }

    public bool ContainsData(string mimeType) => OnMain(() =>
    {
        ClipDescription? description = Clipboard?.PrimaryClipDescription;
        if (description is null) return false;
        return description.HasMimeType(mimeType) || (mimeType == AndroidClipboard.TypePng && description.HasMimeType("image/*"));
    });

    // ---- message boxes --------------------------------------------------------
    //
    // IAndroidDialogHost.ShowAlertAsync: the platform's AlertDialog. It has three button slots, and
    // they are filled in WinForms/WPF order so the first button is the POSITIVE one (the default,
    // trailing), the second NEGATIVE and the third NEUTRAL -- Yes/No/Cancel reads Cancel ... No Yes,
    // as Android lays out a three-button alert. A box with more than three buttons cannot be one, and
    // the caller draws its own instead (-1).

    public Task<int> ShowAlertAsync(string title, string message, string[] buttons, int cancelIndex)
    {
        if (buttons is null || buttons.Length == 0 || buttons.Length > 3) return Task.FromResult(-1);

        var completion = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        void Show()
        {
            try
            {
                var builder = new AlertDialog.Builder(_activity);
                if (!string.IsNullOrEmpty(title)) builder.SetTitle(title);
                builder.SetMessage(message ?? string.Empty);
                builder.SetPositiveButton(buttons[0], (s, e) => completion.TrySetResult(0));
                if (buttons.Length > 1) builder.SetNegativeButton(buttons[1], (s, e) => completion.TrySetResult(1));
                if (buttons.Length > 2) builder.SetNeutralButton(buttons[2], (s, e) => completion.TrySetResult(2));
                builder.SetCancelable(cancelIndex >= 0);
                builder.SetOnCancelListener(new AlertCancel(() => completion.TrySetResult(cancelIndex)));
                builder.Show();
            }
            catch (Exception e)
            {
                Console.WriteLine($"WPF Android: the alert could not be shown: {e}");
                completion.TrySetResult(-1);
            }
        }
        _activity.RunOnUiThread(Show);
        return completion.Task;
    }

    private sealed class AlertCancel : Java.Lang.Object, IDialogInterfaceOnCancelListener
    {
        private readonly Action _cancelled;
        public AlertCancel(Action cancelled) => _cancelled = cancelled;
        public void OnCancel(IDialogInterface? dialog) => _cancelled();
    }

    // ---- file dialogs ---------------------------------------------------------
    //
    // IAndroidDialogHost, over the Storage Access Framework. See AndroidDialogs.cs for why this is
    // asynchronous and why the results are copied into the cache directory rather than handed back
    // as content:// URIs.
    //
    // The activity has to forward its OnActivityResult to HandleActivityResult below; a head that
    // forgets would otherwise leave every pick pending for ever, which is why OnActivityDestroyed
    // completes anything outstanding.

    private const int RequestOpen = 0x57_50_01;      // arbitrary, distinct, and unlikely to collide
    private const int RequestOpenTree = 0x57_50_02;
    private const int RequestCreate = 0x57_50_03;

    private TaskCompletionSource<string[]>? _pendingPick;
    private TaskCompletionSource<bool>? _pendingExport;
    private string? _exportSource;

    public Task<string[]> PickFilesAsync(string[]? mimeTypes, bool multiple, bool directory)
    {
        if (_pendingPick is not null && !_pendingPick.Task.IsCompleted)
        {
            return Task.FromResult(Array.Empty<string>());
        }

        var completion = new TaskCompletionSource<string[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingPick = completion;

        try
        {
            if (directory)
            {
                _activity.StartActivityForResult(new Intent(Intent.ActionOpenDocumentTree), RequestOpenTree);
                return completion.Task;
            }

            var intent = new Intent(Intent.ActionOpenDocument);
            intent.AddCategory(Intent.CategoryOpenable);
            intent.SetType(SingleMimeType(mimeTypes));

            if (mimeTypes is { Length: > 1 })
            {
                intent.PutExtra(Intent.ExtraMimeTypes, ToMimeTypes(mimeTypes));
            }

            if (multiple)
            {
                intent.PutExtra(Intent.ExtraAllowMultiple, true);
            }

            _activity.StartActivityForResult(intent, RequestOpen);
        }
        catch (Exception e)
        {
            Console.WriteLine($"WPF Android: the file picker could not start: {e}");
            _pendingPick = null;
            return Task.FromResult(Array.Empty<string>());
        }

        return completion.Task;
    }

    public Task<bool> ExportFileAsync(string path, string mimeType)
    {
        if (string.IsNullOrEmpty(path) || !System.IO.File.Exists(path)) return Task.FromResult(false);

        if (_pendingExport is not null && !_pendingExport.Task.IsCompleted)
        {
            return Task.FromResult(false);
        }

        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingExport = completion;
        _exportSource = path;

        try
        {
            var intent = new Intent(Intent.ActionCreateDocument);
            intent.AddCategory(Intent.CategoryOpenable);
            intent.SetType(string.IsNullOrEmpty(mimeType) ? "application/octet-stream" : mimeType);
            intent.PutExtra(Intent.ExtraTitle, System.IO.Path.GetFileName(path));

            _activity.StartActivityForResult(intent, RequestCreate);
        }
        catch (Exception e)
        {
            Console.WriteLine($"WPF Android: the save picker could not start: {e}");
            _pendingExport = null;
            return Task.FromResult(false);
        }

        return completion.Task;
    }

    public string ReserveSavePath(string suggestedName)
    {
        string name = string.IsNullOrEmpty(suggestedName) ? "document" : suggestedName;
        string directory = System.IO.Path.Combine(
            _activity.CacheDir?.AbsolutePath ?? System.IO.Path.GetTempPath(),
            "wpf-save-" + Guid.NewGuid().ToString("N"));

        System.IO.Directory.CreateDirectory(directory);
        return System.IO.Path.Combine(directory, name);
    }

    /// <summary>
    ///  Forwarded from the activity's OnActivityResult. Returns true when the result was one of
    ///  ours, so the caller can fall through to its own handling otherwise.
    /// </summary>
    public bool HandleActivityResult(int requestCode, Result resultCode, Intent? data)
    {
        switch (requestCode)
        {
            case RequestOpen:
            case RequestOpenTree:
                CompletePick(resultCode, data, requestCode == RequestOpenTree);
                return true;

            case RequestCreate:
                CompleteExport(resultCode, data);
                return true;

            default:
                return false;
        }
    }

    /// <summary>Fails anything still outstanding, so a torn-down activity does not hang an await.</summary>
    public void CancelPendingDialogs()
    {
        _pendingPick?.TrySetResult(Array.Empty<string>());
        _pendingPick = null;
        _pendingExport?.TrySetResult(false);
        _pendingExport = null;
    }

    private void CompletePick(Result resultCode, Intent? data, bool tree)
    {
        TaskCompletionSource<string[]>? pending = _pendingPick;
        _pendingPick = null;
        if (pending is null) return;

        if (resultCode != Result.Ok || data is null)
        {
            pending.TrySetResult(Array.Empty<string>());
            return;
        }

        try
        {
            var paths = new List<string>();

            // A multi-select result puts the URIs in ClipData and leaves Data null; a single
            // selection is the other way round. Both shapes have to be read or multi-select
            // silently returns nothing.
            if (data.ClipData is { ItemCount: > 0 } clip)
            {
                for (int i = 0; i < clip.ItemCount; i++)
                {
                    string? copied = CopyContentUri(clip.GetItemAt(i)?.Uri, tree);
                    if (copied is not null) paths.Add(copied);
                }
            }
            else if (data.Data is { } uri)
            {
                string? copied = CopyContentUri(uri, tree);
                if (copied is not null) paths.Add(copied);
            }

            pending.TrySetResult(paths.ToArray());
        }
        catch (Exception e)
        {
            Console.WriteLine($"WPF Android: reading the picked file failed: {e}");
            pending.TrySetResult(Array.Empty<string>());
        }
    }

    private void CompleteExport(Result resultCode, Intent? data)
    {
        TaskCompletionSource<bool>? pending = _pendingExport;
        string? source = _exportSource;
        _pendingExport = null;
        _exportSource = null;

        if (pending is null) return;

        if (resultCode != Result.Ok || data?.Data is null || source is null || !System.IO.File.Exists(source))
        {
            pending.TrySetResult(false);
            return;
        }

        try
        {
            using System.IO.Stream? destination = _activity.ContentResolver?.OpenOutputStream(data.Data);
            if (destination is null) { pending.TrySetResult(false); return; }

            using System.IO.FileStream input = System.IO.File.OpenRead(source);
            input.CopyTo(destination);
            pending.TrySetResult(true);
        }
        catch (Exception e)
        {
            Console.WriteLine($"WPF Android: exporting the saved file failed: {e}");
            pending.TrySetResult(false);
        }
    }

    /// <summary>
    ///  Copies a content:// document into the app's cache and returns that path.
    /// </summary>
    /// <remarks>
    ///  Nothing in .NET can open a content:// URI, and the grant on one is not permanent, so a path
    ///  handed straight to File.ReadAllBytes would fail. A local copy is what makes the result mean
    ///  what a WPF caller expects FileName to mean.
    /// </remarks>
    private string? CopyContentUri(Android.Net.Uri? uri, bool tree)
    {
        if (uri is null) return null;

        // A directory pick yields a TREE uri, which is not a document and cannot be opened. There is
        // no local path for a folder either, so the tree uri's own string is returned: an
        // application that asked for a folder gets an identifier it can hand back to the platform,
        // which is the most that exists here.
        if (tree) return uri.ToString();

        try
        {
            string name = QueryDisplayName(uri) ?? "document";
            string directory = System.IO.Path.Combine(
                _activity.CacheDir?.AbsolutePath ?? System.IO.Path.GetTempPath(),
                "wpf-picked-" + Guid.NewGuid().ToString("N"));
            System.IO.Directory.CreateDirectory(directory);

            string destination = System.IO.Path.Combine(directory, name);

            using System.IO.Stream? input = _activity.ContentResolver?.OpenInputStream(uri);
            if (input is null) return null;

            using System.IO.FileStream output = System.IO.File.Create(destination);
            input.CopyTo(output);
            return destination;
        }
        catch (Exception e)
        {
            Console.WriteLine($"WPF Android: could not copy {uri}: {e}");
            return null;
        }
    }

    private string? QueryDisplayName(Android.Net.Uri uri)
    {
        try
        {
            using Android.Database.ICursor? cursor = _activity.ContentResolver?.Query(
                uri, new[] { Android.Provider.IOpenableColumns.DisplayName }, null, null, null);

            if (cursor is not null && cursor.MoveToFirst())
            {
                int column = cursor.GetColumnIndex(Android.Provider.IOpenableColumns.DisplayName);
                if (column >= 0) return cursor.GetString(column);
            }
        }
        catch (Exception)
        {
            // Fall through to the default name; a missing display name is not worth failing a pick.
        }

        return null;
    }

    /// <summary>
    ///  The single type an ACTION_OPEN_DOCUMENT intent filters on. EXTRA_MIME_TYPES narrows it
    ///  further when there is more than one; the intent's own type has to stay broad or the extra
    ///  is ignored.
    /// </summary>
    private static string SingleMimeType(string[]? mimeTypes)
        => mimeTypes is { Length: 1 } ? ToMimeType(mimeTypes[0]) : "*/*";

    private static string[] ToMimeTypes(string[] extensions)
    {
        var types = new string[extensions.Length];
        for (int i = 0; i < extensions.Length; i++) types[i] = ToMimeType(extensions[i]);
        return types;
    }

    /// <summary>
    ///  Maps a bare extension to a MIME type through Android's own table, which is the one the
    ///  document providers agree with.
    /// </summary>
    private static string ToMimeType(string extension)
    {
        if (string.IsNullOrEmpty(extension)) return "*/*";

        string bare = extension.TrimStart('.').ToLowerInvariant();
        string? mime = Android.Webkit.MimeTypeMap.Singleton?.GetMimeTypeFromExtension(bare);
        return string.IsNullOrEmpty(mime) ? "*/*" : mime;
    }

    // ---- metrics --------------------------------------------------------------

    public double Density => _activity.Resources?.DisplayMetrics?.Density ?? 1.0;

    /// <summary>
    /// The display's refresh rate. WindowManager.DefaultDisplay is deprecated but is the only route
    /// below API 30, and Activity.Display (API 30+) is the supported one; either answers the same
    /// question, so take whichever this device offers.
    /// </summary>
    public double RefreshRateHz
    {
        get
        {
            try
            {
                Android.Views.Display? display = Build.VERSION.SdkInt >= BuildVersionCodes.R
                    ? _activity.Display
                    : _activity.WindowManager?.DefaultDisplay;
                float hz = display?.RefreshRate ?? 0f;
                return hz > 0f ? hz : 0.0;
            }
            catch (Exception e)
            {
                Console.WriteLine($"WPF Android: could not read the display refresh rate: {e.Message}");
                return 0.0;
            }
        }
    }

    public void GetScreenPixels(out int width, out int height)
    {
        // The root's laid-out size once there is one (it excludes the system bars, which is what a
        // WPF "work area" means); before the first layout pass, the display metrics are all we have.
        width = _root.Width;
        height = _root.Height;
        if (width > 0 && height > 0) return;

        Android.Util.DisplayMetrics? m = _activity.Resources?.DisplayMetrics;
        width = m?.WidthPixels ?? 0;
        height = m?.HeightPixels ?? 0;
    }
}


/// <summary>
///  What the app puts on the clipboard that is not text, served to the app that pastes it: the files of
///  its cache's clipboard folder, read-only, as content://&lt;package&gt;.clipboard/&lt;file&gt;. Not
///  exported; a reader gets the permission Android grants for a clipboard URI (grantUriPermissions).
///  The authority is the package's (${applicationId}), which the Packager's template rewrite keeps.
/// </summary>
[ContentProvider(new[] { "${applicationId}.clipboard" }, Name = "wpfrun.host.ClipboardProvider", Exported = false, GrantUriPermissions = true)]
public sealed class ClipboardProvider : ContentProvider
{
    internal const string AuthoritySuffix = ".clipboard";

    internal static Java.IO.File Folder(Context context) => new Java.IO.File(context.CacheDir, "clipboard");

    internal static string ExtensionOf(string mimeType) => mimeType switch
    {
        "image/png" => ".png",
        "image/jpeg" => ".jpg",
        "text/html" => ".html",
        "text/plain" => ".txt",
        _ => ".bin",
    };

    private static string TypeOf(string? name) => System.IO.Path.GetExtension(name ?? "") switch
    {
        ".png" => "image/png",
        ".jpg" => "image/jpeg",
        ".html" => "text/html",
        ".txt" => "text/plain",
        _ => "application/octet-stream",
    };

    private Java.IO.File? FileOf(Android.Net.Uri uri)
    {
        if (Context is null) return null;
        // the folder's own files only: the URI's last segment, its name alone
        var file = new Java.IO.File(Folder(Context), System.IO.Path.GetFileName(uri.LastPathSegment ?? ""));
        return file.Exists() ? file : null;
    }

    public override bool OnCreate() => true;

    public override string? GetType(Android.Net.Uri uri) => TypeOf(uri.LastPathSegment);

    public override ParcelFileDescriptor? OpenFile(Android.Net.Uri uri, string mode)
    {
        Java.IO.File file = FileOf(uri) ?? throw new Java.IO.FileNotFoundException(uri.ToString());
        return ParcelFileDescriptor.Open(file, ParcelFileMode.ReadOnly);
    }

    public override Android.Database.ICursor? Query(Android.Net.Uri uri, string[]? projection, string? selection, string[]? selectionArgs, string? sortOrder)
    {
        Java.IO.File? file = FileOf(uri);
        if (file is null) return null;
        var cursor = new Android.Database.MatrixCursor(new[] { Android.Provider.IOpenableColumns.DisplayName, Android.Provider.IOpenableColumns.Size });
        cursor.AddRow(new Java.Lang.Object[] { new Java.Lang.String(file.Name), new Java.Lang.Long(file.Length()) });
        return cursor;
    }

    public override Android.Net.Uri? Insert(Android.Net.Uri uri, ContentValues? values) => null;

    public override int Delete(Android.Net.Uri uri, string? selection, string[]? selectionArgs) => 0;

    public override int Update(Android.Net.Uri uri, ContentValues? values, string? selection, string[]? selectionArgs) => 0;
}

/// <summary>
/// The web views of the app's windows: android.webkit.WebView, for the seam's AndroidWebViewBackend
/// (WebView2, WebBrowser), which reaches it through AppContext data with the commands named in
/// AndroidWebViewCommands (AndroidWebViewRegistration explains why not an interface).
/// </summary>
/// <remarks>
/// A web view is a view of the activity's, laid over its window: placed at the window's client position
/// plus the control's, above the window's view in the view order, moving and hiding with the window --
/// in the full-screen window and in a floating one (a dialog) alike, as the emulator shows (Android 15).
///
/// The page's side is the window.chrome.webview the other heads give it: postMessage reaches the host
/// through prompt() (WebChromeClient.onJsPrompt answers it at once), which needs nothing but the
/// platform -- no Java method exported to the page -- and the host's messages and scripts go in through
/// evaluateJavascript. That shim and the document scripts are run as each page starts (onPageStarted),
/// and for content handed over as a string they are put before it, so its own scripts find them.
///
/// Every command comes from the app's UI thread and is done on the main Looper's (the caller waits for
/// an answer); every event is raised on the main thread, and the backend takes it to the control's.
/// </remarks>
internal sealed class AndroidWebViewHost
{
    private const string MessageMarker = "__wpfWebMessage";

    private const string Shim = @"(function () {
    if (window.chrome && window.chrome.webview) { return; }
    var listeners = [];
    window.chrome = window.chrome || {};
    window.chrome.webview = {
        postMessage: function (m) { prompt('" + MessageMarker + @"', typeof m === 'string' ? m : JSON.stringify(m)); },
        addEventListener: function (t, f) { if (t === 'message') { listeners.push(f); } },
        removeEventListener: function (t, f) {
            if (t !== 'message') { return; }
            var i = listeners.indexOf(f); if (i >= 0) { listeners.splice(i, 1); }
        },
        __deliver: function (data) {
            var e = { data: data };
            for (var i = 0; i < listeners.length; i++) { listeners[i](e); }
        }
    };
})();";

    private sealed class Entry
    {
        public Android.Webkit.WebView View = null!;
        public IntPtr Parent;
        public int X, Y, Width = 1, Height = 1;
        public bool Visible = true;
        public Action<string, string>? Sink;
        public readonly List<string> DocumentScripts = new();
        public string? StartedByLink;
        public bool Failed;
    }

    private readonly AndroidHost _host;
    private readonly Dictionary<int, Entry> _entries = new();
    private int _nextId;

    internal AndroidWebViewHost(AndroidHost host) => _host = host;

    /// <summary>A command of AndroidWebViewCommands, from the app's thread.</summary>
    internal object? Invoke(string command, object[] args) => _host.RunOnMain(() => InvokeOnMain(command, args));

    private object? InvokeOnMain(string command, object[] args)
    {
        if (command == "create")
            return Create((IntPtr)args[0], (int)args[1], (int)args[2], (int)args[3], (int)args[4]);

        if (!_entries.TryGetValue((int)args[0], out Entry? e)) return null;
        Android.Webkit.WebView view = e.View;
        switch (command)
        {
            case "destroy": Destroy((int)args[0]); return null;
            case "setBounds": e.X = (int)args[1]; e.Y = (int)args[2]; e.Width = (int)args[3]; e.Height = (int)args[4]; Place(e); return null;
            case "setVisible": e.Visible = (bool)args[1]; Place(e); return null;
            case "navigate": view.LoadUrl((string)args[1]); return null;
            case "navigateToString":
                // the shim and the document scripts before the content's own scripts
                view.LoadDataWithBaseURL(null, "<script>" + StartScript(e) + "</script>" + (string)args[1], "text/html", "utf-8", null);
                return null;
            case "navigateWithPost":
            {
                string uri = (string)args[1];
                byte[]? body = args[2] as byte[];
                string? headers = args[3] as string;
                if (body is { Length: > 0 }) view.PostUrl(uri, body);
                else view.LoadUrl(uri, Headers(headers));
                return null;
            }
            case "reload":
                if ((bool)args[1]) view.ClearCache(false);
                view.Reload();
                return null;
            case "stop": view.StopLoading(); return null;
            case "goBack": view.GoBack(); return null;
            case "goForward": view.GoForward(); return null;
            case "canGoBack": return view.CanGoBack();
            case "canGoForward": return view.CanGoForward();
            case "getSource": return view.Url;
            case "getTitle": return view.Title;
            case "executeScript":
            {
                var completed = (Action<string>)args[2];
                view.EvaluateJavascript((string)args[1], new ScriptResult(completed));
                return null;
            }
            case "addDocumentScript":
                e.DocumentScripts.Add((string)args[1]);
                return null;
            case "postMessage":
                view.EvaluateJavascript("window.chrome && window.chrome.webview && window.chrome.webview.__deliver(" + (string)args[1] + ");", null);
                return null;
            case "setZoom":
                if (view.Settings is { } zoom) zoom.TextZoom = (int)Math.Round((double)args[1] * 100);
                return null;
            case "clearData":
                view.ClearCache(true);
                Android.Webkit.CookieManager.Instance?.RemoveAllCookies(null);
                Android.Webkit.WebStorage.Instance?.DeleteAllData();
                return null;
            case "capturePreview": Capture(e, (bool)args[1], (Action<byte[]?>)args[2]); return null;
            case "setEventSink": e.Sink = (Action<string, string>)args[1]; return null;
            default: throw new NotSupportedException("web view command " + command);
        }
    }

    private int Create(IntPtr parent, int x, int y, int width, int height)
    {
        if (!_host.WindowPlace(parent, out _, out _, out _, out View? window)) return 0;
        var e = new Entry { Parent = parent, X = x, Y = y, Width = Math.Max(1, width), Height = Math.Max(1, height) };
        var view = new Android.Webkit.WebView(_host.Activity);
        if (view.Settings is { } settings)
        {
            settings.JavaScriptEnabled = true;
            settings.DomStorageEnabled = true;
            settings.SetSupportMultipleWindows(true);
            settings.JavaScriptCanOpenWindowsAutomatically = true;
            settings.MediaPlaybackRequiresUserGesture = false;
        }
        e.View = view;
        int id = ++_nextId;
        view.SetWebViewClient(new Client(this, id));
        view.SetWebChromeClient(new Chrome(this, id));
        view.SetDownloadListener(new Downloads(this, id));
        _entries[id] = e;
        // above its window, as a child window is over its parent's client area
        _host.Root.AddView(view, _host.Root.IndexOfChild(window) + 1, new FrameLayout.LayoutParams(e.Width, e.Height) { Gravity = GravityFlags.Top | GravityFlags.Left });
        Place(e);
        return id;
    }

    private void Destroy(int id)
    {
        if (!_entries.Remove(id, out Entry? e)) return;
        _host.Root.RemoveView(e.View);
        e.View.StopLoading();
        e.View.Destroy();
    }

    /// <summary>A window moved, was resized, shown or hidden: its web views with it.</summary>
    internal void WindowMoved(IntPtr window)
    {
        foreach (Entry e in _entries.Values)
            if (e.Parent == window) Place(e);
    }

    /// <summary>A window was brought to the front: its web views over it.</summary>
    internal void RaiseOver(IntPtr window)
    {
        foreach (Entry e in _entries.Values)
            if (e.Parent == window) e.View.BringToFront();
    }

    /// <summary>A window is gone, and its web views with it.</summary>
    internal void WindowDestroyed(IntPtr window)
    {
        foreach (int id in new List<int>(_entries.Keys))
            if (_entries[id].Parent == window) Destroy(id);
    }

    private void Place(Entry e)
    {
        bool shown = _host.WindowPlace(e.Parent, out int wx, out int wy, out bool windowVisible, out View? window) && windowVisible && e.Visible;
        // over its window in the view order (see RaiseOver)
        if (window is not null && _host.Root.IndexOfChild(e.View) < _host.Root.IndexOfChild(window)) e.View.BringToFront();
        var lp = (FrameLayout.LayoutParams)e.View.LayoutParameters!;
        if (lp.LeftMargin != wx + e.X || lp.TopMargin != wy + e.Y || lp.Width != e.Width || lp.Height != e.Height)
        {
            lp.LeftMargin = wx + e.X;
            lp.TopMargin = wy + e.Y;
            lp.Width = e.Width;
            lp.Height = e.Height;
            e.View.LayoutParameters = lp;
        }
        e.View.Visibility = shown ? ViewStates.Visible : ViewStates.Gone;
    }

    private static string StartScript(Entry e) => Shim + string.Concat(e.DocumentScripts.ConvertAll(s => "\n;" + s));

    private static IDictionary<string, string> Headers(string? headers)
    {
        var map = new Dictionary<string, string>();
        foreach (string line in (headers ?? "").Split('\n'))
        {
            int colon = line.IndexOf(':');
            if (colon > 0) map[line.Substring(0, colon).Trim()] = line.Substring(colon + 1).Trim();
        }
        return map;
    }

    private void Raise(int id, string kind, string payload)
    {
        if (_entries.TryGetValue(id, out Entry? e)) e.Sink?.Invoke(kind, payload ?? "");
    }

    // the view drawn into a bitmap, compressed off the main thread
    private void Capture(Entry e, bool png, Action<byte[]?> completed)
    {
        Bitmap? bitmap = null;
        try
        {
            bitmap = Bitmap.CreateBitmap(Math.Max(1, e.View.Width), Math.Max(1, e.View.Height), Bitmap.Config.Argb8888!);
            e.View.Draw(new Canvas(bitmap));
        }
        catch (Exception) { completed(null); return; }
        Task.Run(() =>
        {
            try
            {
                using var stream = new System.IO.MemoryStream();
                bitmap.Compress(png ? Bitmap.CompressFormat.Png! : Bitmap.CompressFormat.Jpeg!, 90, stream);
                completed(stream.ToArray());
            }
            catch (Exception) { completed(null); }
            finally { bitmap.Recycle(); }
        });
    }

    private sealed class ScriptResult : Java.Lang.Object, Android.Webkit.IValueCallback
    {
        private readonly Action<string> _completed;
        public ScriptResult(Action<string> completed) => _completed = completed;
        public void OnReceiveValue(Java.Lang.Object? value) => _completed(value?.ToString() ?? "null");
    }

    private sealed class Client : Android.Webkit.WebViewClient
    {
        private readonly AndroidWebViewHost _owner;
        private readonly int _id;
        public Client(AndroidWebViewHost owner, int id) { _owner = owner; _id = id; }

        public override bool ShouldOverrideUrlLoading(Android.Webkit.WebView? view, Android.Webkit.IWebResourceRequest? request)
        {
            string url = request?.Url?.ToString() ?? "";
            if (request?.IsForMainFrame == true && _owner._entries.TryGetValue(_id, out Entry? e))
            {
                e.StartedByLink = url;
                _owner.Raise(_id, "navigationStarting", url);
            }
            return false;
        }

        public override void OnPageStarted(Android.Webkit.WebView? view, string? url, Bitmap? favicon)
        {
            if (!_owner._entries.TryGetValue(_id, out Entry? e)) return;
            e.Failed = false;
            // a navigation the page did not start itself (a Navigate, a reload) has had no NavigationStarting
            if (e.StartedByLink != url) _owner.Raise(_id, "navigationStarting", url ?? "");
            e.StartedByLink = null;
            view?.EvaluateJavascript(StartScript(e), null);
            _owner.Raise(_id, "contentLoading", url ?? "");
        }

        public override void OnPageFinished(Android.Webkit.WebView? view, string? url)
        {
            if (!_owner._entries.TryGetValue(_id, out Entry? e) || e.Failed) return;
            _owner.Raise(_id, "navigationCompleted", url ?? "");
        }

        public override void OnReceivedError(Android.Webkit.WebView? view, Android.Webkit.IWebResourceRequest? request, Android.Webkit.WebResourceError? error)
        {
            if (request?.IsForMainFrame != true || !_owner._entries.TryGetValue(_id, out Entry? e)) return;
            e.Failed = true;
            _owner.Raise(_id, "navigationFailed", error?.DescriptionFormatted?.ToString() ?? "");
        }
    }

    private sealed class Chrome : Android.Webkit.WebChromeClient
    {
        private readonly AndroidWebViewHost _owner;
        private readonly int _id;
        public Chrome(AndroidWebViewHost owner, int id) { _owner = owner; _id = id; }

        // window.chrome.webview.postMessage
        public override bool OnJsPrompt(Android.Webkit.WebView? view, string? url, string? message, string? defaultValue, Android.Webkit.JsPromptResult? result)
        {
            if (message != MessageMarker) return base.OnJsPrompt(view, url, message, defaultValue, result);
            result?.Confirm("");
            _owner.Raise(_id, "webMessage", defaultValue ?? "");
            return true;
        }

        public override void OnReceivedTitle(Android.Webkit.WebView? view, string? title) => _owner.Raise(_id, "titleChanged", title ?? "");

        // window.open, a target=_blank link: the address it asks for, found by a view of its own that only listens
        public override bool OnCreateWindow(Android.Webkit.WebView? view, bool isDialog, bool isUserGesture, Message? resultMsg)
        {
            if (resultMsg?.Obj is not Android.Webkit.WebView.WebViewTransport transport) return false;
            var probe = new Android.Webkit.WebView(_owner._host.Activity);
            probe.SetWebViewClient(new NewWindowProbe(_owner, _id, probe));
            transport.WebView = probe;
            resultMsg.SendToTarget();
            return true;
        }
    }

    private sealed class NewWindowProbe : Android.Webkit.WebViewClient
    {
        private readonly AndroidWebViewHost _owner;
        private readonly int _id;
        private readonly Android.Webkit.WebView _probe;
        public NewWindowProbe(AndroidWebViewHost owner, int id, Android.Webkit.WebView probe) { _owner = owner; _id = id; _probe = probe; }

        public override bool ShouldOverrideUrlLoading(Android.Webkit.WebView? view, Android.Webkit.IWebResourceRequest? request)
        {
            _owner.Raise(_id, "newWindow", request?.Url?.ToString() ?? "");
            _owner._host.Post(() => _probe.Destroy());
            return true;
        }

        public override void OnPageStarted(Android.Webkit.WebView? view, string? url, Bitmap? favicon)
        {
            if (string.IsNullOrEmpty(url) || url == "about:blank") return;
            view?.StopLoading();
            _owner.Raise(_id, "newWindow", url);
            _owner._host.Post(() => _probe.Destroy());
        }
    }

    private sealed class Downloads : Java.Lang.Object, Android.Webkit.IDownloadListener
    {
        private readonly AndroidWebViewHost _owner;
        private readonly int _id;
        public Downloads(AndroidWebViewHost owner, int id) { _owner = owner; _id = id; }

        public void OnDownloadStart(string? url, string? userAgent, string? contentDisposition, string? mimetype, long contentLength)
            => _owner.Raise(_id, "downloadStarting", url ?? "");
    }
}
