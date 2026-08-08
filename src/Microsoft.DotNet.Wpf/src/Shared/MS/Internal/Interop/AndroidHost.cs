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
//                   the activity, answers density/screen queries, and owns the Choreographer pump.
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
using Android.App;
using Android.Content;
using Android.Content.Res;
using Android.Runtime;
using Android.Graphics;
using Android.Views;
using Android.Views.InputMethods;
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
/// Popups have to ask to be a media overlay (see AndroidHost.CreateView) to land above the window
/// they belong to.
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
        => AndroidWindow.NotifySurfaceDestroyed(_wpfHandle);

    private void Report(ISurfaceHolder holder, int width, int height)
    {
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

    public override bool OnTouchEvent(MotionEvent? e) => WpfInput.OnTouch(_wpfHandle, e) || base.OnTouchEvent(e);

    public override bool OnGenericMotionEvent(MotionEvent? e) => WpfInput.OnGenericMotion(_wpfHandle, e) || base.OnGenericMotionEvent(e);

    [DllImport("android")] private static extern IntPtr ANativeWindow_fromSurface(IntPtr env, IntPtr surface);
    [DllImport("android")] private static extern void ANativeWindow_release(IntPtr window);
}

/// <summary>
/// A WPF popup's native view: a plain, fully transparent View that exists ONLY to receive touches.
///
/// Its PIXELS come from somewhere else. A popup on Android is a child of the same activity as the
/// window it belongs to, so the compositor draws it straight into that window's surface
/// (NativePlatform.PopupsShareOwnerSurface) rather than giving it a surface of its own -- which on
/// the GLES backend could not be transparent anyway, leaving a black box around the popup's rounded
/// chrome and drop shadow. But WPF still needs the popup to be a real window for INPUT: mouse
/// capture, hit testing and every message route key off its handle, and a touch landing on the
/// owner's view would be delivered with the OWNER's handle and ignored by the popup. Hence a view
/// with no content of its own, sitting exactly where the popup is.
/// </summary>
internal sealed class WpfPopupView : View
{
    private readonly IntPtr _wpfHandle;

    public WpfPopupView(Context context, IntPtr wpfHandle) : base(context)
    {
        _wpfHandle = wpfHandle;
        SetBackgroundColor(Android.Graphics.Color.Transparent);
        Focusable = true;
    }

    public override bool OnTouchEvent(MotionEvent? e) => WpfInput.OnTouch(_wpfHandle, e) || base.OnTouchEvent(e);

    public override bool OnGenericMotionEvent(MotionEvent? e) => WpfInput.OnGenericMotion(_wpfHandle, e) || base.OnGenericMotionEvent(e);
}

/// <summary>Translates Android motion events into the mouse messages WPF consumes. Shared by both
/// view kinds, which differ only in whether they also carry a rendering surface.</summary>
internal static class WpfInput
{
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

        // A press must be preceded by a move so WPF's mouse position is current before the
        // button-down lands; AndroidWindow does not synthesize it, because a real mouse (below)
        // does not need it.
        if (kind == 1) AndroidWindow.NotifyTouch(handle, 0, (int)e.GetX(), (int)e.GetY());
        AndroidWindow.NotifyTouch(handle, kind, (int)e.GetX(), (int)e.GetY());
        return true;
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
/// (build error XA4204). The one genuinely Java-facing piece, the Choreographer callback, is a
/// nested class instead.
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

internal sealed class AndroidHost : IAndroidHost
{
    private readonly Activity _activity;
    private readonly FrameLayout _root;
    private readonly Android.OS.Handler _handler;
    private readonly Dictionary<IntPtr, View> _views = new();
    private readonly FrameCallback _frameCallback;

    private Action? _tick;
    private bool _paused;
    private bool _posted;

    public AndroidHost(Activity activity, FrameLayout root)
    {
        _activity = activity;
        _root = root;
        _handler = new Android.OS.Handler(Android.OS.Looper.MainLooper!);
        _frameCallback = new FrameCallback(this);
    }

    /// <summary>The Java-facing shim Choreographer calls back into (see the class remarks).</summary>
    private sealed class FrameCallback : Java.Lang.Object, Choreographer.IFrameCallback
    {
        private readonly AndroidHost _host;
        public FrameCallback(AndroidHost host) => _host = host;
        public void DoFrame(long frameTimeNanos) => _host.DoFrame();
    }

    // ---- views ----------------------------------------------------------------

    public bool CreateView(IntPtr handle, int x, int y, int width, int height, bool borderless)
    {
        // A popup is drawn into its owner's surface, so it needs no surface of its own -- only a
        // touch target. See WpfPopupView.
        View view = borderless
            ? new WpfPopupView(_activity, handle)
            : new WpfSurfaceView(_activity, handle);

        // A top-level window IS the activity's content area, so it must FOLLOW it -- MatchParent is
        // the Android spelling of the autoresizing mask UIKitWindow gives its view. Sizing it to the
        // pixels WPF asked for instead pins it to whatever the screen was when it was created, and a
        // rotation then leaves WPF rendering into the old portrait rectangle on a landscape screen.
        // Popups are the opposite: they are placed at an absolute pixel offset, which FrameLayout
        // expresses as gravity + margins (there is no non-obsolete AbsoluteLayout).
        var lp = borderless
            ? new FrameLayout.LayoutParams(width, height)
            {
                Gravity = GravityFlags.Top | GravityFlags.Left,
                LeftMargin = x,
                TopMargin = y,
            }
            : new FrameLayout.LayoutParams(
                ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.MatchParent);

        _root.AddView(view, lp);
        _views[handle] = view;
        return true;
    }

    public void SetViewFrame(IntPtr handle, int x, int y, int width, int height)
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
    }

    public void ShowSoftKeyboard(IntPtr handle, bool multiline, bool password)
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

    public void HideSoftKeyboard(IntPtr handle)
    {
        if (!_views.TryGetValue(handle, out View? view) || view is null) return;

        if (_activity.GetSystemService(Context.InputMethodService) is InputMethodManager imm)
        {
            imm.HideSoftInputFromWindow(view.WindowToken, HideSoftInputFlags.None);
        }
    }

    public void SetImeCursorRect(IntPtr handle, int x, int y, int width, int height)
    {
        if (!_views.TryGetValue(handle, out View? view) || view is null) return;

        if (_activity.GetSystemService(Context.InputMethodService) is InputMethodManager imm)
        {
            // updateCursorAnchorInfo is the richer API, but it needs a full editor; the rectangle
            // alone is what keeps the candidate strip off the caret.
            imm.UpdateCursor(view, x, y, x + width, y + height);
        }
    }

    public void DestroyView(IntPtr handle)
    {
        if (!_views.Remove(handle, out View? view)) return;
        _root.RemoveView(view);
        view.Dispose();
    }

    // ---- metrics --------------------------------------------------------------

    public double Density => _activity.Resources?.DisplayMetrics?.Density ?? 1.0;

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

    // ---- the Choreographer pump -----------------------------------------------
    //
    // Choreographer is Android's requestAnimationFrame: one display-aligned callback per frame,
    // delivered on the UI thread. It is one-shot, so the pump re-posts itself for as long as it is
    // running — and simply stops re-posting when the dispatcher parks it, which is what makes an
    // idle WPF app cost nothing (see Dispatcher.PumpAndroidTick).

    public bool StartFrameCallback(Action tick)
    {
        _tick = tick;
        _paused = false;
        Post();
        return true;
    }

    public void StopFrameCallback()
    {
        _tick = null;
        _paused = true;
        Choreographer.Instance?.RemoveFrameCallback(_frameCallback);
        _posted = false;
    }

    public void SetFrameCallbackPaused(bool paused)
    {
        _paused = paused;
        if (!paused) Post();
    }

    public void RequestWake()
    {
        // Called from ANY thread (Dispatcher.BeginInvoke off the UI thread is the point), and
        // Choreographer is thread-affine, so hop first.
        _activity.RunOnUiThread(() => SetFrameCallbackPaused(false));
    }

    public void ScheduleWake(double seconds)
    {
        _handler.RemoveCallbacksAndMessages(null);
        _handler.PostDelayed(() => SetFrameCallbackPaused(false), (long)Math.Max(0.0, seconds * 1000.0));
    }

    private void DoFrame()
    {
        _posted = false;
        if (_paused || _tick is null) return;

        _tick();
        Post();      // no-op if the tick just parked us
    }

    private void Post()
    {
        if (_paused || _posted || _tick is null) return;
        _posted = true;
        Choreographer.Instance!.PostFrameCallback(_frameCallback);
    }
}
