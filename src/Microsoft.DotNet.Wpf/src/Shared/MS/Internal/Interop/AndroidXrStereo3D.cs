// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// A headset's depth for 3D the app renders itself through Direct3D: Ab3d's DXEngine, behind a D3DImage.
// The stack renders a Viewport3D once for each eye (WgpuSceneRenderer.Render3D), but DXEngine's scene and
// camera are its own, and what reaches the stack is one finished image. DXEngine has a stereo mode of its
// own: a VirtualRealityProvider in the DXScene renders the left eye's view and the right eye's side by side
// into the one image, from cameras it moves apart itself. On a headset whose windows are stereo pairs
// (AndroidXrHost, WPF_XR_STEREO), each DXViewportView's scene is put in that mode as it is made, and the
// renderer gives each eye its half of the D3DImage (WgpuSceneRenderer.SideBySideExternalSwitch).
//
// The app and the library are not changed: the view is found as WPF loads it, by the members it has
// (Ab3d.DirectX.Controls: a DXScene, a DXSceneInitialized event), and the mode is set through the
// library's public API by reflection. WPF_XR_DX_STEREO=0 leaves DXEngine as it is; WPF_XR_DX_EYE=<world
// units> sets the eye separation (otherwise the camera's focus distance / 30, as a Viewport3D's), and
// WPF_XR_DX_CONVERGE=0 leaves the cameras' parallel images unshifted (everything in front of the window).
// Each eye is then drawn from where it is (EyeTracker below; WPF_XR_DX_HEAD=0 keeps the fixed pair).

#nullable enable

using System;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Media3D;
using System.Windows.Threading;

namespace MS.Internal.Interop
{
    internal static class AndroidXrStereo3D
    {
        private const string ViewNamespace = "Ab3d.DirectX.Controls";
        private static bool s_enabled;
        private static readonly ConditionalWeakTable<object, object> s_seen = new();

        /// <summary>From the XR host as it starts, when its windows are stereo pairs.</summary>
        public static void Enable()
        {
            if (s_enabled || Environment.GetEnvironmentVariable("WPF_XR_DX_STEREO") == "0") return;
            s_enabled = true;
            EventManager.RegisterClassHandler(typeof(FrameworkElement), FrameworkElement.LoadedEvent, new RoutedEventHandler(OnLoaded), handledEventsToo: true);
        }

        private static void OnLoaded(object sender, RoutedEventArgs e)
        {
            // WPF's own 3D (not one inside a DXEngine view, which is the view's): a fullscreen target too
            if (sender is Viewport3D viewport && System.Windows.Media.VisualTreeHelper.GetParent(viewport)?.GetType().Namespace != ViewNamespace)
            {
                if (!s_seen.TryGetValue(viewport, out _))
                {
                    s_seen.Add(viewport, viewport);
                    s_wpfViewports.Add(new WeakReference(viewport));
                    s_dispatcher ??= viewport.Dispatcher;
                    EnsureRendering();
                }
                return;
            }
            Type type = sender.GetType();
            if (type.Namespace != ViewNamespace) return;
            PropertyInfo? sceneProperty = type.GetProperty("DXScene");
            if (sceneProperty == null || s_seen.TryGetValue(sender, out _)) return;
            s_seen.Add(sender, sender);
            if (sceneProperty.GetValue(sender) is { } scene) { Apply(sender, scene); return; }
            // the scene is made with the device, later
            EventInfo? initialized = type.GetEvent("DXSceneInitialized");
            if (initialized?.EventHandlerType == null) { Log($"{type.Name}: no DXScene, no DXSceneInitialized"); return; }
            MethodInfo handler = typeof(AndroidXrStereo3D).GetMethod(nameof(OnSceneInitialized), BindingFlags.NonPublic | BindingFlags.Static)!;
            initialized.AddEventHandler(sender, Delegate.CreateDelegate(initialized.EventHandlerType, handler));
        }

        private static void OnSceneInitialized(object? sender, EventArgs e)
        {
            if (sender?.GetType().GetProperty("DXScene")?.GetValue(sender) is { } scene) Apply(sender, scene);
        }

        private static void Apply(object view, object scene)
        {
            try
            {
                Assembly engine = scene.GetType().Assembly;
                Type? providerType = SafeTypes(engine).FirstOrDefault(t => t.Name == "SplitScreenVirtualRealityProvider");
                MethodInfo? initialize = scene.GetType().GetMethods().FirstOrDefault(m => m.Name == "InitializeVirtualRealityRendering" && m.GetParameters().Length == 1);
                if (providerType == null || initialize == null) { Log("the engine has no split-screen stereo"); return; }

                float eye = EyeSeparation(view);
                object? provider = null;
                foreach (ConstructorInfo ctor in providerType.GetConstructors().OrderByDescending(c => c.GetParameters().Length))
                {
                    ParameterInfo[] ps = ctor.GetParameters();
                    var args = new object?[ps.Length];
                    bool ok = true;
                    for (int i = 0; i < ps.Length && ok; i++)
                    {
                        Type pt = ps[i].ParameterType;
                        string name = ps[i].Name ?? "";
                        // parallax: how far the cameras turn in toward each other; a headset's eyes look parallel
                        // (toe-in gives each eye a keystone the two cannot fuse)
                        if (pt == typeof(float) || pt == typeof(double))
                        {
                            float v = name.Contains("arallax", StringComparison.OrdinalIgnoreCase) ? 0f : eye;
                            args[i] = pt == typeof(float) ? (object)v : (double)v;
                        }
                        else if (pt == typeof(bool)) args[i] = name.Contains("arallel", StringComparison.Ordinal) || (ps[i].HasDefaultValue && ps[i].DefaultValue is true);
                        else if (pt.IsEnum && Enum.GetNames(pt).Contains("SideBySide")) args[i] = Enum.Parse(pt, "SideBySide");
                        else if (ps[i].HasDefaultValue) args[i] = ps[i].DefaultValue;
                        else ok = false;
                    }
                    if (!ok) continue;
                    provider = ctor.Invoke(args);
                    Log($"{providerType.Name}({string.Join(", ", ps.Select((p, i) => $"{p.Name}={args[i]}"))})");
                    break;
                }
                if (provider == null) { Log("no constructor of " + providerType.Name + " to call"); return; }
                initialize.Invoke(scene, new[] { provider });
                AppContext.SetData("WpfWebGpu.Xr.SideBySideD3DImages", true);
                Follow(view, provider);
                HeadTrack(view, scene, provider);
                // drawn again in the new mode
                view.GetType().GetMethod("Refresh", Type.EmptyTypes)?.Invoke(view, null);
                Log($"{view.GetType().Name}: stereo, eye separation {eye}");
                if (view.GetType().GetProperty("UsedGraphicsProfile")?.GetValue(view) is { } profile)
                    Log($"graphics profile {profile.GetType().GetProperty("Name")?.GetValue(profile)}: multisampling {profile.GetType().GetProperty("PreferedMultisampleCount")?.GetValue(profile)}, supersampling {profile.GetType().GetProperty("SupersamplingCount")?.GetValue(profile)}");
            }
            catch (Exception ex) { Log("could not put the 3D engine in stereo: " + (ex.InnerException ?? ex)); }
        }

        // ---- following the camera ---------------------------------------------------------------
        //
        // The eye separation is the focus distance / 30, so zooming keeps the depth; and with parallel cameras
        // the focus point's disparity is then e / (4 z tan(fov/2)) of the width for each eye -- 1 / (120 tan
        // (fov/2)), whatever the distance -- by which the renderer moves each eye's half toward the middle
        // (WgpuSceneRenderer's convergence), so the focus point is at the window's depth.

        private static readonly System.Collections.Generic.List<(WeakReference View, object Provider)> s_followed = new();
        private static bool s_following;

        private static System.Windows.Threading.Dispatcher? s_dispatcher;

        private static void Follow(object view, object provider)
        {
            if (view is DispatcherObject d) s_dispatcher = d.Dispatcher;
            s_followed.Add((new WeakReference(view), provider));
            Update(view, provider);
            EnsureRendering();
        }

        private static readonly System.Collections.Generic.List<WeakReference> s_wpfViewports = new();

        private static void EnsureRendering()
        {
            if (s_following) return;
            s_following = true;
            System.Windows.Media.CompositionTarget.Rendering += (_, _) =>
            {
                // DXEngine draws when its scene or camera changes, and the head is neither: the eyes moved,
                // each view is drawn again from where they are now
                // filling the eyes, every frame: a head is never still, and a frame not drawn again is one the
                // headset moves the last one for (by its pose, not its depth: the scene jumped). It keeps up (72
                // frames a second at 3998x2160, measured with the head still and every frame drawn)
                bool eyesMoved = s_headTracked && (EyesMoved() || s_fullView != null || s_redrawAlways);
                s_ticks++;
                if (eyesMoved) s_redraws++;
                long now = Environment.TickCount64;
                if (s_fullView != null && now - s_ratesSince >= 2000)
                {
                    if (s_ratesSince != 0) Log($"rates: {s_ticks * 1000.0 / (now - s_ratesSince):0.0} frames, {s_redraws * 1000.0 / (now - s_ratesSince):0.0} redraws, {s_eyeRuns * 1000.0 / (now - s_ratesSince):0.0} eye cameras a second; a panel frame {(s_lagCount > 0 ? s_lagSum / s_lagCount : 0):0.0} ms after its render's start, {(s_lagCount > 0 ? (double)s_rendersPerDraw / s_lagCount : 0):0.00} renders a panel frame{CameraNote()}");
                    s_ticks = s_redraws = s_eyeRuns = 0;
                    s_lagSum = 0; s_lagCount = s_rendersPerDraw = 0;
                    s_ratesSince = now;
                }
                var rects = new System.Collections.Generic.List<Rect>();
                for (int i = s_followed.Count - 1; i >= 0; i--)
                {
                    if (s_followed[i].View.Target is not { } v) { s_followed.RemoveAt(i); continue; }
                    Update(v, s_followed[i].Provider);
                    if (eyesMoved) Redraw(v);
                    if (v is FrameworkElement fe && fe.IsVisible && fe.ActualWidth > 0 && PresentationSource.FromVisual(fe) != null)
                    {
                        try { rects.Add(new Rect(fe.PointToScreen(new Point(0, 0)), fe.PointToScreen(new Point(fe.ActualWidth, fe.ActualHeight)))); }
                        catch (InvalidOperationException) { }
                    }
                }
                for (int i = s_wpfViewports.Count - 1; i >= 0; i--)
                {
                    if (s_wpfViewports[i].Target is not Viewport3D vp) { s_wpfViewports.RemoveAt(i); continue; }
                    if (vp.IsVisible && vp.ActualWidth > 0 && PresentationSource.FromVisual(vp) != null)
                    {
                        try { rects.Add(new Rect(vp.PointToScreen(new Point(0, 0)), vp.PointToScreen(new Point(vp.ActualWidth, vp.ActualHeight)))); }
                        catch (InvalidOperationException) { }
                    }
                }
                lock (s_rectsLock) s_viewRects = rects;
                // the fullscreen view's image size, as the engine renders it now
                if (s_fullView?.Target is { } full && full.GetType().GetProperty("DXFinalPixelSize")?.GetValue(full) is { } size)
                {
                    int w = Convert.ToInt32(size.GetType().GetField("Width")?.GetValue(size) ?? size.GetType().GetProperty("Width")?.GetValue(size) ?? 0);
                    int h = Convert.ToInt32(size.GetType().GetField("Height")?.GetValue(size) ?? size.GetType().GetProperty("Height")?.GetValue(size) ?? 0);
                    lock (s_rectsLock) { s_fullWidth = w; s_fullHeight = h; }
                }
                else lock (s_drawnLock) { s_drawn[0] = s_drawn[1] = default; }
            };
        }

        private static void Update(object view, object provider)
        {
            Viewport3D? viewport = view.GetType().GetProperty("Viewport3D")?.GetValue(view) as Viewport3D;
            if (viewport?.Camera is not PerspectiveCamera camera || camera.LookDirection.Length <= 0) return;
            float eye = EyeSeparation(view);
            PropertyInfo? separation = provider.GetType().GetProperty("EyeSeparation");
            if (separation?.GetValue(provider) is float now && MathF.Abs(now - eye) > eye * 0.01f) separation.SetValue(provider, eye);
            double tan = Math.Tan(camera.FieldOfView * Math.PI / 360);
            double converge = tan > 0 ? eye / (4 * camera.LookDirection.Length * tan) : 0;
            // drawn from where the eyes are, the focus plane is the window's already
            if (Environment.GetEnvironmentVariable("WPF_XR_DX_CONVERGE") == "0" || s_headTracked) converge = 0;
            if (AppContext.GetData("WpfWebGpu.Xr.SideBySideConvergence") is not double was || Math.Abs(was - converge) > 1e-4)
            {
                AppContext.SetData("WpfWebGpu.Xr.SideBySideConvergence", converge);
                Log($"convergence {converge:0.0000} of the width (fov {camera.FieldOfView:0.#}, focus {camera.LookDirection.Length:0.##}, eye {eye:0.###})");
            }
        }

        // ---- drawn from where the eyes are --------------------------------------------------------
        //
        // A stereo pair from fixed cameras is a 3D photograph: the head moving, it shears with the head. So each
        // eye's view is drawn from where that eye is: a step of our own in DXEngine's per-eye loop, after the
        // provider's (BeginVirtualRealityRenderingStep), gives the eye a camera of its own (a MatrixCamera as the
        // RenderingContext's UsedCamera). The view's rectangle on the window (metres in the headset's space) is
        // the app camera's picture at its focus distance (scene units); the eye, where it is in front of the
        // rectangle, is put at the same place in the scene, looking as the app camera looks, through the
        // rectangle (an off-axis frustum). The window is then a window onto the scene: what is at the focus
        // distance lies in the window's plane, what is nearer in front of it, and a head moved sees round it.
        // DXScene.Camera is the app's still: hit testing, the mono picture. WPF_XR_DX_HEAD=0: the fixed pair.

        private static bool s_headTracked;
        // a benchmark: the fullscreen view drawn every frame, the head still (WPF_XR_DX_REDRAW_ALWAYS=1)
        private static readonly bool s_redrawAlways = Environment.GetEnvironmentVariable("WPF_XR_DX_REDRAW_ALWAYS") == "1";
        private static int s_ticks, s_redraws, s_eyeRuns;
        private static long s_ratesSince;

        // what each eye's half of the fullscreen view was drawn for: the projection layer says so
        private static readonly object s_drawnLock = new object();
        private static readonly (System.Numerics.Vector3 Position, System.Numerics.Quaternion Orientation, Xr.Fov Fov)[] s_drawn = new (System.Numerics.Vector3, System.Numerics.Quaternion, Xr.Fov)[2];

        // the views of each image published, by its number (XrExternalPanel numbers them as the app publishes):
        // the XR host draws the latest into the panel and submits the views of the number it drew
        private static readonly (long Number, (System.Numerics.Vector3 Position, System.Numerics.Quaternion Orientation, Xr.Fov Fov) Left,
                                 (System.Numerics.Vector3 Position, System.Numerics.Quaternion Orientation, Xr.Fov Fov) Right)[] s_published
            = new (long, (System.Numerics.Vector3, System.Numerics.Quaternion, Xr.Fov), (System.Numerics.Vector3, System.Numerics.Quaternion, Xr.Fov))[8];

        private static void Published(long number)
        {
            lock (s_drawnLock)
            {
                s_published[number & 7] = (number, s_drawn[0], s_drawn[1]);
                s_publishedTime[number & 7] = s_drawnTime;
                s_publishedClip[number & 7] = (s_drawnClip[0], s_drawnClip[1]);
            }
            // how long after its render a frame reaches the panel (the render a frame behind would pair views wrong)
            long since = System.Diagnostics.Stopwatch.GetTimestamp() - s_renderStarted;
            s_lagSum += since * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            s_lagCount++;
            s_rendersPerDraw += s_rendersSinceDraw;
            s_rendersSinceDraw = 0;
        }

        private static long s_renderStarted;
        private static double s_lagSum;
        private static int s_lagCount, s_rendersSinceDraw, s_rendersPerDraw;

        // the display time of the headset frame whose eyes a drawing was made for (AndroidXrHost.EyesTime), by its number
        private static long s_drawnTime;
        private static readonly long[] s_publishedTime = new long[8];
        // each eye's near and far planes (metres), by the number: the depth read back to distances (XrExternalPanel)
        private static readonly System.Numerics.Vector2[] s_drawnClip = new System.Numerics.Vector2[2];
        private static readonly (System.Numerics.Vector2 Left, System.Numerics.Vector2 Right)[] s_publishedClip = new (System.Numerics.Vector2, System.Numerics.Vector2)[8];

        internal static bool TryGetDrawnClip(long number, out System.Numerics.Vector2 left, out System.Numerics.Vector2 right)
        {
            lock (s_drawnLock)
            {
                (left, right) = s_publishedClip[number & 7];
                return s_published[number & 7].Number == number && left.X > 0 && right.X > 0;
            }
        }

        internal static long DrawnTime(long number)
        {
            lock (s_drawnLock) return s_published[number & 7].Number == number ? s_publishedTime[number & 7] : 0;
        }

        internal static bool TryGetDrawnViews(long number, out (System.Numerics.Vector3 Position, System.Numerics.Quaternion Orientation, Xr.Fov Fov) left,
                                              out (System.Numerics.Vector3 Position, System.Numerics.Quaternion Orientation, Xr.Fov Fov) right)
        {
            lock (s_drawnLock)
            {
                var entry = s_published[number & 7];
                left = entry.Left;
                right = entry.Right;
                if (entry.Number != number) return false;
            }
            return left.Orientation != default && right.Orientation != default;
        }

        // where the 3D views are on the desktop (device pixels), for the headset's fullscreen (AndroidXrHost)
        private static readonly object s_rectsLock = new object();
        private static System.Collections.Generic.List<Rect> s_viewRects = new();

        // the view the headset shows alone (AndroidXrHost's fullscreen): rendered FullHeight pixels high whatever
        // its size, its own image drawn on a panel of its own (XrExternalPanel)
        private static WeakReference? s_fullView;
        private static double s_fullDpiX, s_fullDpiY;
        private static int s_fullWidth, s_fullHeight;
        // about the headset's own pixels (a Quest 3's 2064x2208 an eye). Not a multiple of the view's: a 1000 high
        // view made an 8000x4325 image, multisampled more than the GPU's memory, and no frame of it was drawn
        private const double FullHeight = 2160;

        /// <summary>The view at <paramref name="rect"/> (desktop pixels) rendered sharper, for a fullscreen of it.</summary>
        // the model in fullscreen as the controllers have it: turned about its upright (radians), scaled, moved
        // (metres, the room's), from where the view's rectangle put it; from scratch each time fullscreen begins
        private static float s_turn, s_scale = 1f;
        private static System.Numerics.Vector3 s_move;

        /// <summary>From the headset's frame loop: the model turned, scaled and moved by so much more.</summary>
        internal static void Manipulate(float turn, float scale, System.Numerics.Vector3 move)
        {
            lock (s_drawnLock)
            {
                s_turn += turn;
                s_scale = Math.Clamp(s_scale * scale, 0.05f, 20f);
                s_move += move;
            }
        }

        internal static void BeginFullscreen(Rect rect)
        {
            lock (s_drawnLock) { s_turn = 0f; s_scale = 1f; s_move = default; }
            AppContext.SetData("WpfWebGpu.Xr.ExternalPublished", (Action<long>)Published);
            lock (s_drawnLock) Array.Clear(s_published);
            lock (s_rectsLock) { s_fullWidth = s_fullHeight = 0; }
            // the views are the UI thread's (this is the headset's frame loop): found and changed there
            if (s_dispatcher == null) return;
            s_dispatcher.BeginInvoke(() =>
            {
                FrameworkElement? view = null;
                foreach (var (viewRef, _) in s_followed)
                    if (viewRef.Target is FrameworkElement fe && Matches(fe, rect)) { view = fe; break; }
                if (view == null) { Log("no view at the fullscreen's rectangle"); return; }
                PropertyInfo? dx = view.GetType().GetProperty("DpiScaleX"), dy = view.GetType().GetProperty("DpiScaleY");
                if (dx == null || dy == null) return;
                s_fullDpiX = (double)dx.GetValue(view)!;
                s_fullDpiY = (double)dy.GetValue(view)!;
                // each eye's half shaped as its field of view (tangent width against height), within the GPU's limit
                double aspect = AndroidXrHost.TryGetEyeView(true, out _, out _, out var tan) && tan.Z - tan.W > 0 ? (tan.Y - tan.X) / (tan.Z - tan.W) : 1;
                double high = double.TryParse(Environment.GetEnvironmentVariable("WPF_XR_FULL_HEIGHT"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double h) && h > 0 ? h : FullHeight;
                double sy = high / Math.Max(1, view.ActualHeight * s_fullDpiY), sx = 2 * view.ActualHeight * s_fullDpiY * sy * aspect / Math.Max(1, view.ActualWidth * s_fullDpiX);
                double over = Math.Max(view.ActualWidth * s_fullDpiX * sx / 8000, view.ActualHeight * s_fullDpiY * sy / 8000);
                if (over > 1) { sx /= over; sy /= over; }
                dx.SetValue(view, s_fullDpiX * sx);
                dy.SetValue(view, s_fullDpiY * sy);
                Resized(view);
                s_fullView = new WeakReference(view);
                Redraw(view);      // at its new size now, not when the head next moves
                Log($"fullscreen view rendered at {dx.GetValue(view):0.##} x {dy.GetValue(view):0.##} its scale");
            });
        }

        // the fullscreen view's WPF camera, for the rates line: whether the app's orbit and zoom reach it
        private static string CameraNote()
        {
            if (s_fullView?.Target is not FrameworkElement view || view.GetType().GetProperty("Viewport3D")?.GetValue(view) is not Viewport3D v3 || v3.Camera is not ProjectionCamera c) return "";
            var l = c.LookDirection;
            return FormattableString.Invariant($"; camera at {c.Position.X:0.#},{c.Position.Y:0.#},{c.Position.Z:0.#} looking {l.X:0.##},{l.Y:0.##},{l.Z:0.##} ({l.Length:0.#} long)");
        }

        internal static void EndFullscreen()
        {
            if (s_fullView?.Target is not FrameworkElement view) { s_fullView = null; return; }
            s_fullView = null;
            s_dispatcher?.BeginInvoke(() =>
            {
                view.GetType().GetProperty("DpiScaleX")?.SetValue(view, s_fullDpiX);
                view.GetType().GetProperty("DpiScaleY")?.SetValue(view, s_fullDpiY);
                Resized(view);
            });
        }

        // the engine sizes its image as the view's size changes (OnRenderSizeChanged, with the DPI scale), not
        // as the scale does: told its size changed, to the size it has
        private static void Resized(FrameworkElement view)
        {
            try
            {
                MethodInfo? changed = view.GetType().GetMethod("OnRenderSizeChanged", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public, null, new[] { typeof(SizeChangedInfo) }, null);
                changed?.Invoke(view, new object[] { new SizeChangedInfo(view, view.RenderSize, true, true) });
            }
            catch (Exception e) { Log("could not resize the view's image: " + (e.InnerException ?? e).Message); }
        }

        /// <summary>The fullscreen view's image size (device pixels), once the engine renders it so.</summary>
        internal static bool TryGetFullscreenImage(out int width, out int height)
        {
            lock (s_rectsLock) { width = s_fullWidth; height = s_fullHeight; }
            return s_fullView != null && width > 0 && height > 0;
        }

        private static bool Matches(FrameworkElement fe, Rect rect)
        {
            try
            {
                Point a = fe.PointToScreen(new Point(0, 0));
                return Math.Abs(a.X - rect.X) < 2 && Math.Abs(a.Y - rect.Y) < 2;
            }
            catch (InvalidOperationException) { return false; }
        }

        /// <summary>The 3D view at a desktop point, else the first there is; false when there is none.</summary>
        internal static bool TryGetViewRect(int x, int y, out Rect rect)
        {
            lock (s_rectsLock)
            {
                foreach (Rect r in s_viewRects)
                    if (r.Contains(x, y)) { rect = r; return true; }
                rect = s_viewRects.Count > 0 ? s_viewRects[0] : Rect.Empty;
                return s_viewRects.Count > 0;
            }
        }
        private static System.Numerics.Vector3 s_drawnLeft, s_drawnRight;

        private static System.Numerics.Quaternion s_drawnTurn;

        private static bool EyesMoved()
        {
            if (!AndroidXrHost.TryGetEyes(out var left, out var right)) return false;
            // filling the eyes, a head turned shows another part of the scene too
            bool turned = false;
            if (AndroidXrHost.IsFullscreen && AndroidXrHost.TryGetEyeView(true, out _, out var turn, out _))
            {
                turned = MathF.Abs(System.Numerics.Quaternion.Dot(turn, s_drawnTurn)) < 0.99999f;
                if (turned) s_drawnTurn = turn;
            }
            if (!turned && System.Numerics.Vector3.DistanceSquared(left, s_drawnLeft) < 0.0005f * 0.0005f
                && System.Numerics.Vector3.DistanceSquared(right, s_drawnRight) < 0.0005f * 0.0005f) return false;
            s_drawnLeft = left;
            s_drawnRight = right;
            return true;
        }

        private static void Redraw(object view)
        {
            try { view.GetType().GetMethod("RenderScene", new[] { typeof(bool), typeof(bool) })?.Invoke(view, new object[] { true, false }); }
            catch (Exception e) { Log("could not draw the view again: " + (e.InnerException ?? e).Message); }
        }

        private static void HeadTrack(object view, object scene, object provider)
        {
            if (Environment.GetEnvironmentVariable("WPF_XR_DX_HEAD") == "0") return;
            Assembly engine = scene.GetType().Assembly;
            Type? stepType = SafeTypes(engine).FirstOrDefault(t => t.Name == "CustomActionRenderingStep");
            Type? cameraType = SafeTypes(engine).FirstOrDefault(t => t.Name == "MatrixCamera");
            PropertyInfo? action = stepType?.GetProperty("CustomAction");
            FieldInfo? begin = null;
            for (Type? t = provider.GetType(); t != null && begin == null; t = t.BaseType)
                begin = t.GetField("beginVirtualRealityRenderingStep", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            object? steps = scene.GetType().GetProperty("RenderingSteps")?.GetValue(scene);
            MethodInfo? addAfter = steps?.GetType().GetMethods().FirstOrDefault(m => m.Name == "AddAfter" && m.GetParameters().Length == 2);
            if (stepType == null || cameraType == null || action?.PropertyType == null || begin?.GetValue(provider) is not { } beginStep || addAfter == null)
            {
                Log("no way into the engine's per-eye loop: the fixed pair");
                return;
            }
            var tracker = new EyeTracker(view, scene, cameraType);
            object step = Activator.CreateInstance(stepType, "XrEyeCamera", "the eye's camera where the eye is")!;
            action.SetValue(step, Delegate.CreateDelegate(action.PropertyType, tracker, typeof(EyeTracker).GetMethod(nameof(EyeTracker.Run))!));
            addAfter.Invoke(steps, new[] { beginStep, step });
            s_headTracked = true;
            AppContext.SetData("WpfWebGpu.Xr.SideBySideConvergence", 0.0);
            Log("each eye drawn from where it is");
        }

        private sealed class EyeTracker
        {
            private readonly WeakReference _view;
            private readonly object _scene;
            private readonly object _left, _right;          // MatrixCamera
            private readonly Type _matrix;
            private readonly FieldInfo[] _fields = new FieldInfo[16];
            private bool _logged;

            public EyeTracker(object view, object scene, Type cameraType)
            {
                _view = new WeakReference(view);
                _scene = scene;
                _left = Activator.CreateInstance(cameraType)!;
                _right = Activator.CreateInstance(cameraType)!;
                _matrix = cameraType.GetProperty("View")!.PropertyType;
                for (int i = 0; i < 16; i++) _fields[i] = _matrix.GetField($"M{i / 4 + 1}{i % 4 + 1}")!;
            }

            /// <summary>The engine's step, in its per-eye loop (the render thread, which is the UI thread here).</summary>
            public void Run(object renderingContext)
            {
                s_eyeRuns++;
                if (renderingContext.GetType().GetProperty("VirtualRealityContext")?.GetValue(renderingContext) is { } vrc
                    && vrc.GetType().GetProperty("CurrentEye")?.GetValue(vrc)?.ToString() == "Left")
                {
                    s_renderStarted = System.Diagnostics.Stopwatch.GetTimestamp();
                    s_rendersSinceDraw++;
                }
                try
                {
                    if (!AndroidXrHost.TryGetEyes(out System.Numerics.Vector3 leftEye, out System.Numerics.Vector3 rightEye)) return;
                    if (_view.Target is not FrameworkElement element || element.ActualWidth <= 0 || !element.IsVisible) return;
                    object? vr = renderingContext.GetType().GetProperty("VirtualRealityContext")?.GetValue(renderingContext);
                    bool left = vr?.GetType().GetProperty("CurrentEye")?.GetValue(vr)?.ToString() != "Right";
                    object? appCamera = _scene.GetType().GetProperty("Camera")?.GetValue(_scene);
                    if (appCamera == null) return;
                    var view = ToNumerics(appCamera.GetType().GetProperty("View")!.GetValue(appCamera)!);
                    var projection = ToNumerics(appCamera.GetType().GetProperty("Projection")!.GetValue(appCamera)!);
                    float near = (float)appCamera.GetType().GetProperty("NearPlaneDistance")!.GetValue(appCamera)!;
                    float far = (float)appCamera.GetType().GetProperty("FarPlaneDistance")!.GetValue(appCamera)!;
                    if (!System.Numerics.Matrix4x4.Invert(view, out var world) || projection.M11 == 0 || projection.M22 == 0) return;
                    bool rightHanded = projection.M34 < 0;
                    var rightAxis = new System.Numerics.Vector3(world.M11, world.M12, world.M13);
                    var upAxis = new System.Numerics.Vector3(world.M21, world.M22, world.M23);
                    var zAxis = new System.Numerics.Vector3(world.M31, world.M32, world.M33);
                    var position = new System.Numerics.Vector3(world.M41, world.M42, world.M43);
                    var forward = rightHanded ? -zAxis : zAxis;

                    // the focus distance: the WPF camera's look direction's length, as the eye separation's
                    float focus = element.GetType().GetProperty("Viewport3D")?.GetValue(element) is Viewport3D vp && vp.Camera is ProjectionCamera pc && pc.LookDirection.Length > 0
                        ? (float)pc.LookDirection.Length : 10f;
                    float halfWide = focus / projection.M11, halfHigh = focus / projection.M22;   // the picture at the focus distance
                    var centre = position + forward * focus;

                    // the view's rectangle in the headset's space, and the eye in front of it
                    Point topLeft = element.PointToScreen(new Point(0, 0));
                    Point bottomRight = element.PointToScreen(new Point(element.ActualWidth, element.ActualHeight));
                    var a = AndroidXrHost.DesktopToSpace(topLeft.X, topLeft.Y);
                    var b = AndroidXrHost.DesktopToSpace(bottomRight.X, bottomRight.Y);
                    var rectCentre = (a + b) / 2;
                    float halfWideM = MathF.Abs(b.X - a.X) / 2, halfHighM = MathF.Abs(b.Y - a.Y) / 2;
                    if (halfWideM <= 0 || halfHighM <= 0) return;
                    var d = (left ? leftEye : rightEye) - rectCentre;
                    float sx = halfWide / halfWideM, sy = halfHigh / halfHighM, s = (sx + sy) / 2;
                    float dx = d.X * sx, dy = d.Y * sy, dz = MathF.Max(d.Z * s, near * 2);

                    var eye = centre + rightAxis * dx + upAxis * dy - forward * dz;
                    float n = near, f = far + MathF.Max(0, dz - focus);
                    System.Numerics.Matrix4x4 eyeView, eyeProjection;
                    if (AndroidXrHost.IsFullscreen && ReferenceEquals(s_fullView?.Target, element)
                        && AndroidXrHost.TryGetEyeView(left, out var eyePosition, out var eyeTurn, out var tan))
                    {
                        // filling the eye: drawn as the eye sees, its pose and its field of view, the scene where the
                        // view's rectangle put it -- level: the scene's up (the WPF camera's UpDirection) is the room's,
                        // the camera's heading straight ahead (a camera looking down on a floor tilted it into a slope)
                        var worldUp = element.GetType().GetProperty("Viewport3D")?.GetValue(element) is Viewport3D v3 && v3.Camera is ProjectionCamera wc && wc.UpDirection.Length > 0
                            ? System.Numerics.Vector3.Normalize(new System.Numerics.Vector3((float)wc.UpDirection.X, (float)wc.UpDirection.Y, (float)wc.UpDirection.Z))
                            : upAxis;
                        // the camera's own axes tilted level (not new ones crossed: the engine's frame may be the
                        // mirror of WPF's, and a mirrored view culls the faces it should show)
                        var ahead = forward - worldUp * System.Numerics.Vector3.Dot(forward, worldUp);
                        ahead = ahead.LengthSquared() > 1e-6f ? System.Numerics.Vector3.Normalize(ahead) : forward;
                        var levelRight = rightAxis - worldUp * System.Numerics.Vector3.Dot(rightAxis, worldUp);
                        levelRight = levelRight.LengthSquared() > 1e-6f ? System.Numerics.Vector3.Normalize(levelRight) : rightAxis;
                        // the scene's up as the camera's frame has it (its up tilted level), so the three stay a frame of
                        // the camera's handedness whatever the WPF up's convention
                        worldUp = System.Numerics.Vector3.Dot(worldUp, upAxis) < 0 ? -worldUp : worldUp;
                        System.Numerics.Vector3 Scene(System.Numerics.Vector3 local) => levelRight * local.X + worldUp * local.Y - ahead * local.Z;
                        // the model as the controllers turned, scaled and moved it (Manipulate): the eye taken the other
                        // way about the model's anchor, the scale its units a metre
                        float turned, scaled; System.Numerics.Vector3 moved;
                        lock (s_drawnLock) { turned = s_turn; scaled = s_scale; moved = s_move; }
                        var undo = System.Numerics.Quaternion.CreateFromAxisAngle(System.Numerics.Vector3.UnitY, -turned);
                        s /= scaled;
                        var lookLocal = System.Numerics.Vector3.Transform(System.Numerics.Vector3.Transform(-System.Numerics.Vector3.UnitZ, eyeTurn), undo);
                        var upLocal = System.Numerics.Vector3.Transform(System.Numerics.Vector3.Transform(System.Numerics.Vector3.UnitY, eyeTurn), undo);
                        var from = System.Numerics.Vector3.Transform(eyePosition - (rectCentre + moved), undo);
                        eye = centre + Scene(from) * s;
                        eyeView = rightHanded ? System.Numerics.Matrix4x4.CreateLookAt(eye, eye + Scene(lookLocal), Scene(upLocal)) : System.Numerics.Matrix4x4.CreateLookAtLeftHanded(eye, eye + Scene(lookLocal), Scene(upLocal));
                        // the app camera's far plane is fitted to its own distance (the engine's near/far optimization);
                        // an eye farther off than the camera, the scene crossed it as the head moved
                        f = (far + from.Length() * s + halfWide * 2) * 4;
                        // and its near plane to the camera's nearest object: an eye leaning in came nearer than that.
                        // A centimetre from the eye instead (a table model looked at closely came within five).
                        n = 0.01f * s;
                        float fl = n * tan.X, fr = n * tan.Y, fu = n * tan.Z, fd = n * tan.W;
                        eyeProjection = rightHanded ? System.Numerics.Matrix4x4.CreatePerspectiveOffCenter(fl, fr, fd, fu, n, f) : System.Numerics.Matrix4x4.CreatePerspectiveOffCenterLeftHanded(fl, fr, fd, fu, n, f);
                        lock (s_drawnLock)
                        {
                            s_drawn[left ? 0 : 1] = (eyePosition, eyeTurn, AndroidXrHost.EyeFov(left));
                            s_drawnClip[left ? 0 : 1] = new System.Numerics.Vector2(n / s, f / s);     // metres
                            s_drawnTime = AndroidXrHost.EyesTime;
                        }
                    }
                    else
                    {
                        float l = (-halfWide - dx) * n / dz, r = (halfWide - dx) * n / dz, bt = (-halfHigh - dy) * n / dz, t = (halfHigh - dy) * n / dz;
                        eyeView = rightHanded ? System.Numerics.Matrix4x4.CreateLookAt(eye, eye + forward, upAxis) : System.Numerics.Matrix4x4.CreateLookAtLeftHanded(eye, eye + forward, upAxis);
                        eyeProjection = rightHanded ? System.Numerics.Matrix4x4.CreatePerspectiveOffCenter(l, r, bt, t, n, f) : System.Numerics.Matrix4x4.CreatePerspectiveOffCenterLeftHanded(l, r, bt, t, n, f);
                    }

                    object camera = left ? _left : _right;
                    Type ct = camera.GetType();
                    ct.GetProperty("ParentDXScene")?.SetValue(camera, _scene);
                    ct.GetProperty("NearPlaneDistance")?.SetValue(camera, n);
                    ct.GetProperty("FarPlaneDistance")?.SetValue(camera, f);
                    ct.GetProperty("AspectRatio")?.SetValue(camera, halfWide / halfHigh);
                    ct.GetProperty("View")!.SetValue(camera, FromNumerics(eyeView));
                    ct.GetProperty("Projection")!.SetValue(camera, FromNumerics(eyeProjection));
                    renderingContext.GetType().GetProperty("UsedCamera")!.SetValue(renderingContext, camera);
                    if (!_logged)
                    {
                        _logged = true;
                        Log($"eye camera: {(left ? "left" : "right")} at {d.X:0.000},{d.Y:0.000},{d.Z:0.000} m from the view's centre; scene {s:0.0} units/m, focus {focus:0.##}, {(rightHanded ? "right" : "left")}-handed");
                    }
                }
                catch (Exception e) { if (!_logged) { _logged = true; Log("eye camera failed: " + e); } }
            }

            private System.Numerics.Matrix4x4 ToNumerics(object m)
            {
                var r = new float[16];
                for (int i = 0; i < 16; i++) r[i] = (float)_fields[i].GetValue(m)!;
                return new System.Numerics.Matrix4x4(r[0], r[1], r[2], r[3], r[4], r[5], r[6], r[7], r[8], r[9], r[10], r[11], r[12], r[13], r[14], r[15]);
            }

            private object FromNumerics(System.Numerics.Matrix4x4 m)
            {
                object boxed = Activator.CreateInstance(_matrix)!;
                float[] v = { m.M11, m.M12, m.M13, m.M14, m.M21, m.M22, m.M23, m.M24, m.M31, m.M32, m.M33, m.M34, m.M41, m.M42, m.M43, m.M44 };
                for (int i = 0; i < 16; i++) _fields[i].SetValue(boxed, v[i]);
                return boxed;
            }
        }

        /// <summary>WPF_XR_DX_EYE, or the focus distance (the camera's LookDirection's length) / 30.</summary>
        private static float EyeSeparation(object view)
        {
            if (float.TryParse(Environment.GetEnvironmentVariable("WPF_XR_DX_EYE"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float set)) return set;
            Viewport3D? viewport = view.GetType().GetProperty("Viewport3D")?.GetValue(view) as Viewport3D;
            if (viewport?.Camera is ProjectionCamera camera && camera.LookDirection.Length > 0) return (float)(camera.LookDirection.Length / 30);
            return 0.1f;
        }

        private static Type[] SafeTypes(Assembly a)
        {
            try { return a.GetTypes(); }
            catch (ReflectionTypeLoadException e) { return e.Types.Where(t => t != null).ToArray()!; }
        }

        private static void Log(string message) => Console.WriteLine("[xr 3d] " + message);
    }
}
