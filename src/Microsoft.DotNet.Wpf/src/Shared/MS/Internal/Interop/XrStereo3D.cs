// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// A headset's 3D views: the app's 3D seen with two eyes, and a 3D view filling the eyes. Whichever headset it is --
// an OpenXR runtime (AndroidXrHost), WebXR (BrowserXrHost), Apple Vision Pro's immersive space (VisionXrHost) --
// gives the eyes and the desktop's place in the room (XrHeadset); this, compiled into the app, does the rest the
// same on each, so that the experience is the same on each.
//
// Two kinds of 3D view. WPF's own (a Viewport3D): seen through its window, the compositor draws it once for each
// eye (WgpuSceneRenderer.Render3D); filling the eyes, the compositor draws it for the eyes' cameras into a
// picture of its own (DrawWpfFullscreen, WgpuSceneRenderer.Headset.cs). A 3D engine's behind a D3DImage (Ab3d's
// DXEngine, found by reflection, by its namespace: no reference to it): its stereo mode renders the eyes side by
// side (a VirtualRealityProvider in its DXScene), each from where the eye is (EyeTracker below). Either picture
// goes to the headset's panel (XrExternalPanel), numbered, its eyes kept by the number, and is moved to where the
// eyes are when it is shown -- by its depth (the panel's reprojection), or by the headset's compositor.
//
// Filling the eyes, the controllers (or the hands) turn the model about its upright, scale it and move it
// (Manipulate); its camera's own navigation is the app's, through the window.
//
// The 3D views are noted as they load (Watch, from when the app's assembly is) and followed once a headset is
// there (Enable). WPF_XR_DX_STEREO=0 leaves DXEngine as it is; WPF_XR_DX_EYE=<world units> sets the eye
// separation (otherwise the camera's focus distance / 30, as a Viewport3D's), WPF_XR_DX_CONVERGE=0 leaves the
// cameras' parallel images unshifted, WPF_XR_DX_HEAD=0 keeps the fixed pair; WPF_XR_FULL_HEIGHT the fullscreen
// picture's height.

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
    internal static class XrStereo3D
    {
        private const string ViewNamespace = "Ab3d.DirectX.Controls";
        private static bool s_enabled;
        private static readonly ConditionalWeakTable<object, object> s_seen = new();

        // what loaded before a headset was there (a browser's session begins long after): taken up when one is
        private static bool s_watching;
        private static readonly System.Collections.Generic.List<WeakReference> s_pending = new();
        private static readonly ConditionalWeakTable<object, object> s_pendingSeen = new();

        /// <summary>From the XR host as it starts, when its windows are stereo pairs: the 3D views followed from now on,
        /// those already loaded included.</summary>
        public static void Enable()
        {
            if (Environment.GetEnvironmentVariable("WPF_XR_DX_STEREO") == "0") return;
            if (s_enabled) { RedrawWindows(); return; }     // a session again
            Watch();
            s_enabled = true;
            WeakReference[] pending;
            lock (s_pending) { pending = s_pending.ToArray(); s_pending.Clear(); }
            // on the views' own thread: a headset comes on its activity's (Android's main), and a second time
            // there are views waiting (Disable's)
            foreach (WeakReference r in pending)
            {
                if (r.Target is not { } element) continue;
                if (element is DispatcherObject d && !d.CheckAccess()) d.Dispatcher.BeginInvoke(new Action(() => Seen(element)));
                else Seen(element);
            }
            RedrawWindows();
        }

        // the engine views put in their stereo mode (Apply), with the eye camera's step HeadTrack added to each
        private static readonly System.Collections.Generic.List<(WeakReference View, object Scene)> s_applied = new();
        private static readonly ConditionalWeakTable<object, object> s_eyeSteps = new();

        /// <summary>The headset gone (an immersive activity back to its 2D panel): the views drawn as they are on a
        /// screen again -- the engine's out of its stereo mode, the eye camera's step out of its loop -- and waiting
        /// for a headset that comes again (Enable), as they did before the first.</summary>
        public static void Disable()
        {
            Dispatcher? dispatcher = s_dispatcher ?? Application.Current?.Dispatcher;
            if (dispatcher != null && !dispatcher.CheckAccess()) { dispatcher.BeginInvoke(new Action(Disable)); return; }
            if (!s_enabled) return;
            if (s_fullView != null || s_fullWpf != null) EndFullscreen();
            s_enabled = false;
            s_headTracked = false;
            foreach ((WeakReference view, object scene) in s_applied)
            {
                try
                {
                    if (s_eyeSteps.TryGetValue(scene, out object? step)
                        && scene.GetType().GetProperty("RenderingSteps")?.GetValue(scene) is { } steps)
                        steps.GetType().GetMethods().FirstOrDefault(m => m.Name == "Remove" && m.GetParameters().Length == 1)?.Invoke(steps, new[] { step });
                    s_eyeSteps.Remove(scene);
                    scene.GetType().GetMethods().FirstOrDefault(m => m.Name == "InitializeVirtualRealityRendering" && m.GetParameters().Length == 1)
                        ?.Invoke(scene, new object?[] { null });
                    if (view.Target is { } v)
                    {
                        s_seen.Remove(v);
                        lock (s_pending) { s_pendingSeen.AddOrUpdate(v, v); s_pending.Add(new WeakReference(v)); }
                        v.GetType().GetMethod("Refresh", Type.EmptyTypes)?.Invoke(v, null);
                    }
                }
                catch (Exception e) { Log("could not take a view out of stereo: " + (e.InnerException ?? e).Message); }
            }
            s_applied.Clear();
            s_followed.Clear();
            AppContext.SetData("WpfWebGpu.Xr.SideBySideD3DImages", false);
            Log("the headset gone: the views drawn for a screen");
            RedrawWindows();
        }

        /// <summary>Every window drawn again: a headset that came later (WebXR's session) shows the windows as stereo
        /// pairs, which a window becomes as it presents -- and one with nothing changed does not present.</summary>
        private static void RedrawWindows()
        {
            if (AppContext.GetData("WpfWebGpu.Xr.RedrawWindows") is not Action redraw) return;
            Dispatcher? dispatcher = s_dispatcher ?? Application.Current?.Dispatcher;
            if (dispatcher == null || dispatcher.CheckAccess()) redraw();
            else dispatcher.BeginInvoke(redraw);
        }

        /// <summary>The 3D views noted as they load, from the start: an app's 3D is loaded before a headset that comes
        /// later (WebXR's session, visionOS's immersive space) is there, and is followed once it is (Enable).</summary>
        private static void Watch()
        {
            if (s_watching) return;
            s_watching = true;
            EventManager.RegisterClassHandler(typeof(FrameworkElement), FrameworkElement.LoadedEvent, new RoutedEventHandler(OnLoaded), handledEventsToo: true);
            // A Viewport3D with no Loaded handler of its own is not told it loaded: WPF broadcasts Loaded only into
            // the parts of a tree where an element (or its style) has one, not for a class handler. Its first
            // layout raises SizeChanged on it, whoever listens.
            EventManager.RegisterClassHandler(typeof(Viewport3D), FrameworkElement.SizeChangedEvent,
                new SizeChangedEventHandler((sender, _) => Seen(sender)), handledEventsToo: true);
        }

        private static void OnLoaded(object sender, RoutedEventArgs e) => Seen(sender);

        /// <summary>An element loaded or laid out: a 3D view to follow, if it is one and is new.</summary>
        private static void Seen(object sender)
        {
            if (!s_enabled)
            {
                if (sender is not Viewport3D && sender.GetType().Namespace != ViewNamespace) return;
                lock (s_pending)
                {
                    if (s_pendingSeen.TryGetValue(sender, out _)) return;
                    s_pendingSeen.Add(sender, sender);
                    s_pending.Add(new WeakReference(sender));
                }
                return;
            }
            // WPF's own 3D (not one inside a DXEngine view, which is the view's): a fullscreen target too
            if (sender is Viewport3D viewport && System.Windows.Media.VisualTreeHelper.GetParent(viewport)?.GetType().Namespace != ViewNamespace)
            {
                if (!s_seen.TryGetValue(viewport, out _))
                {
                    s_seen.Add(viewport, viewport);
                    Log($"a WPF Viewport3D in a {System.Windows.Media.VisualTreeHelper.GetParent(viewport)?.GetType().Name ?? "nothing"}");
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
            MethodInfo handler = typeof(XrStereo3D).GetMethod(nameof(OnSceneInitialized), BindingFlags.NonPublic | BindingFlags.Static)!;
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
                s_applied.Add((new WeakReference(view), scene));
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
                else if (s_fullWpf?.Target is Viewport3D wpfFull) DrawWpfFullscreen(wpfFull);
                else lock (s_drawnLock) { s_drawn[0] = s_drawn[1] = default; }
                DrawPanelForHost();
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
        private static readonly (System.Numerics.Vector3 Position, System.Numerics.Quaternion Orientation, System.Numerics.Vector4 Fov)[] s_drawn = new (System.Numerics.Vector3, System.Numerics.Quaternion, System.Numerics.Vector4)[2];

        // the views of each image published, by its number (XrExternalPanel numbers them as the app publishes):
        // the XR host draws the latest into the panel and submits the views of the number it drew
        private static readonly (long Number, (System.Numerics.Vector3 Position, System.Numerics.Quaternion Orientation, System.Numerics.Vector4 Fov) Left,
                                 (System.Numerics.Vector3 Position, System.Numerics.Quaternion Orientation, System.Numerics.Vector4 Fov) Right)[] s_published
            = new (long, (System.Numerics.Vector3, System.Numerics.Quaternion, System.Numerics.Vector4), (System.Numerics.Vector3, System.Numerics.Quaternion, System.Numerics.Vector4))[8];

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

        // the display time of the headset frame whose eyes a drawing was made for (XrHeadset.EyesTime), by its number
        private static long s_drawnTime;
        private static readonly long[] s_publishedTime = new long[8];
        // each eye's near and far planes (metres), by the number: the depth read back to distances (XrExternalPanel)
        private static readonly System.Numerics.Vector2[] s_drawnClip = new System.Numerics.Vector2[2];
        private static readonly (System.Numerics.Vector2 Left, System.Numerics.Vector2 Right)[] s_publishedClip = new (System.Numerics.Vector2, System.Numerics.Vector2)[8];

        /// <summary>For the panel's latest image (its number): each eye's drawn position and near plane (metres), turn,
        /// field of view's tangents (left, right, up, down), then its position now and the far plane, turn and
        /// tangents now -- 24 floats an eye, what the panel moves the picture by (XrExternalPanel.Reproject.cs); null
        /// while the views it was drawn for are not known. The eyes now: position, turn, field of view's angles.</summary>
        internal static float[]? MovedViews(long number,
            (System.Numerics.Vector3 Position, System.Numerics.Quaternion Orientation, System.Numerics.Vector4 Fov) nowLeft,
            (System.Numerics.Vector3 Position, System.Numerics.Quaternion Orientation, System.Numerics.Vector4 Fov) nowRight)
        {
            if (!TryGetDrawnViews(number, out var drawnLeft, out var drawnRight)) return null;
            if (!TryGetDrawnClip(number, out var clipLeft, out var clipRight)) return null;
            var u = new float[48];
            void Put(int at, (System.Numerics.Vector3 Position, System.Numerics.Quaternion Orientation, System.Numerics.Vector4 Fov) drawn,
                     (System.Numerics.Vector3 Position, System.Numerics.Quaternion Orientation, System.Numerics.Vector4 Fov) now, System.Numerics.Vector2 clip)
            {
                u[at + 0] = drawn.Position.X; u[at + 1] = drawn.Position.Y; u[at + 2] = drawn.Position.Z; u[at + 3] = clip.X;
                u[at + 4] = drawn.Orientation.X; u[at + 5] = drawn.Orientation.Y; u[at + 6] = drawn.Orientation.Z; u[at + 7] = drawn.Orientation.W;
                u[at + 8] = MathF.Tan(drawn.Fov.X); u[at + 9] = MathF.Tan(drawn.Fov.Y); u[at + 10] = MathF.Tan(drawn.Fov.Z); u[at + 11] = MathF.Tan(drawn.Fov.W);
                u[at + 12] = now.Position.X; u[at + 13] = now.Position.Y; u[at + 14] = now.Position.Z; u[at + 15] = clip.Y;
                u[at + 16] = now.Orientation.X; u[at + 17] = now.Orientation.Y; u[at + 18] = now.Orientation.Z; u[at + 19] = now.Orientation.W;
                u[at + 20] = MathF.Tan(now.Fov.X); u[at + 21] = MathF.Tan(now.Fov.Y); u[at + 22] = MathF.Tan(now.Fov.Z); u[at + 23] = MathF.Tan(now.Fov.W);
            }
            Put(0, drawnLeft, nowLeft, clipLeft);
            Put(24, drawnRight, nowRight, clipRight);
            return u;
        }

        // ---- for a headset host in another assembly (the browser's and visionOS's are WindowsBase's): this
        //      class's entry points through AppContext, as the host's are (XrHeadset)

        [System.Runtime.CompilerServices.ModuleInitializer]
        internal static void Offer()
        {
            Watch();
            AppContext.SetData("WpfWebGpu.Xr.Core.Enable", (Action)Enable);
            AppContext.SetData("WpfWebGpu.Xr.Core.BeginFullscreen", (Action<double, double, double, double>)((x, y, w, h) => BeginFullscreen(new Rect(x, y, w, h))));
            AppContext.SetData("WpfWebGpu.Xr.Core.EndFullscreen", (Action)EndFullscreen);
            AppContext.SetData("WpfWebGpu.Xr.Core.ViewAt", (Func<int, int, double[]?>)((x, y) =>
                TryGetViewRect(x, y, out Rect r) ? new[] { r.X, r.Y, r.Width, r.Height } : null));
            // the first 3D view there is (a check with nothing to point with: a simulator's WPF_XR_AUTO_FULLSCREEN)
            AppContext.SetData("WpfWebGpu.Xr.Core.FirstView", (Func<double[]?>)(() =>
            {
                lock (s_rectsLock) return s_viewRects.Count > 0 ? new[] { s_viewRects[0].X, s_viewRects[0].Y, s_viewRects[0].Width, s_viewRects[0].Height } : null;
            }));
            AppContext.SetData("WpfWebGpu.Xr.Core.Manipulate", (Action<float, float, float, float, float>)((turn, scale, mx, my, mz) =>
                Manipulate(turn, scale, new System.Numerics.Vector3(mx, my, mz))));
            AppContext.SetData("WpfWebGpu.Xr.Core.MovedViews", (Func<long, float[], float[]?>)((number, now) => now.Length < 22 ? null : MovedViews(number,
                (new System.Numerics.Vector3(now[0], now[1], now[2]), new System.Numerics.Quaternion(now[3], now[4], now[5], now[6]), new System.Numerics.Vector4(now[7], now[8], now[9], now[10])),
                (new System.Numerics.Vector3(now[11], now[12], now[13]), new System.Numerics.Quaternion(now[14], now[15], now[16], now[17]), new System.Numerics.Vector4(now[18], now[19], now[20], now[21])))));
            AppContext.SetData("WpfWebGpu.Xr.Core.HasFullscreenImage", (Func<bool>)(() => TryGetFullscreenImage(out _, out _)));
            AppContext.SetData("WpfWebGpu.Xr.Core.DrawsSideBySide", (Func<bool>)(() => DrawsWpfFullscreen || AppContext.GetData("WpfWebGpu.Xr.SideBySideD3DImages") is true));
        }

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

        internal static bool TryGetDrawnViews(long number, out (System.Numerics.Vector3 Position, System.Numerics.Quaternion Orientation, System.Numerics.Vector4 Fov) left,
                                              out (System.Numerics.Vector3 Position, System.Numerics.Quaternion Orientation, System.Numerics.Vector4 Fov) right)
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

        /// <summary>The fullscreen image's height: WPF_XR_FULL_HEIGHT, or the host's (WpfWebGpu.Xr.FullHeight: a compositor
        /// that takes it from memory, visionOS's, wants it smaller), or FullHeight.</summary>
        private static double FullscreenHeight() =>
            double.TryParse(Environment.GetEnvironmentVariable("WPF_XR_FULL_HEIGHT"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double h) && h > 0 ? h
            : AppContext.GetData("WpfWebGpu.Xr.FullHeight") is double host && host > 0 ? host : FullHeight;

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
                if (view == null)
                {
                    // WPF's own 3D: drawn for the eyes by the compositor, each frame (DrawWpfFullscreen)
                    foreach (WeakReference r in s_wpfViewports)
                        if (r.Target is Viewport3D vp && Matches(vp, rect)) { s_fullWpf = new WeakReference(vp); Log("fullscreen: a WPF Viewport3D, drawn for the eyes"); return; }
                    Log("no view at the fullscreen's rectangle");
                    return;
                }
                PropertyInfo? dx = view.GetType().GetProperty("DpiScaleX"), dy = view.GetType().GetProperty("DpiScaleY");
                if (dx == null || dy == null) return;
                s_fullDpiX = (double)dx.GetValue(view)!;
                s_fullDpiY = (double)dy.GetValue(view)!;
                // each eye's half shaped as its field of view (tangent width against height), within the GPU's limit
                double aspect = XrHeadset.TryGetEyeView(true, out _, out _, out var tan) && tan.Z - tan.W > 0 ? (tan.Y - tan.X) / (tan.Z - tan.W) : 1;
                double high = FullscreenHeight();
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

        /// <summary>A host whose frames are not its own (the browser's: the page's) has the fullscreen panel drawn here, on
        /// the app's frame: the latest picture moved to where the eyes are now, into the surface the host gives
        /// (WpfWebGpu.Xr.PreparePanel: the size, its target), and the host told which picture is there
        /// (WpfWebGpu.Xr.PanelDrawn). An OpenXR host draws it on its own frame loop instead.</summary>
        private static void DrawPanelForHost()
        {
            if (AppContext.GetData("WpfWebGpu.Xr.CoreDrawsPanel") is not true) return;
            if (s_fullView == null && !DrawsWpfFullscreen) return;
            long largest = (AppContext.GetData("WpfWebGpu.Xr.LargestExternal") as Func<long>)?.Invoke() ?? 0;
            int w = (int)(largest >> 32), h = (int)(largest & 0xFFFFFFFF);
            if (w > 0 && h > 0) { s_panelWidth = w; s_panelHeight = h; }
            if (s_panelWidth <= 0 || s_panelHeight <= 0) return;
            if (AppContext.GetData("WpfWebGpu.Xr.PreparePanel") is not Func<int, int, IntPtr> prepare) return;
            IntPtr target = prepare(s_panelWidth, s_panelHeight);
            (AppContext.GetData("WpfWebGpu.Xr.ExternalTarget") as Func<IntPtr, int, int, bool>)?.Invoke(target, s_panelWidth, s_panelHeight);
            // a compositor that takes the picture from memory and moves it itself, by the head's pose it was drawn for
            // (visionOS's): the latest picture as it is, and the eyes' number it was drawn for (DrawnTime)
            if (AppContext.GetData("WpfWebGpu.Xr.SubmitPicture") is Action<IntPtr, int, int, int, long> submit)
            {
                // with its depth where the compositor takes it (WpfWebGpu.Xr.SubmitDepth: each eye's cells' nearest and
                // the planes it was drawn with), which then moves the picture by it and puts it among what else is there
                if (AppContext.GetData("WpfWebGpu.Xr.SubmitDepth") is Action<IntPtr, int, int, int, float[], long> submitDepth
                    && AppContext.GetData("WpfWebGpu.Xr.ExternalReadLatestDepth") is Func<Action<IntPtr, int, int, int, IntPtr, int, int, int, long>, long> readDepth)
                {
                    readDepth((pixels, width, height, row, cells, cw, ch, cellsRow, number) =>
                    {
                        long eyes = DrawnTime(number);
                        submit(pixels, width, height, row, eyes);
                        if (cells != IntPtr.Zero && TryGetDrawnClip(number, out var left, out var right))
                            submitDepth(cells, cw, ch, cellsRow, new[] { left.X, left.Y, right.X, right.Y }, eyes);
                    });
                    return;
                }
                (AppContext.GetData("WpfWebGpu.Xr.ExternalReadLatest") as Func<Action<IntPtr, int, int, int, long>, long>)?.Invoke(
                    (pixels, width, height, row, number) => submit(pixels, width, height, row, DrawnTime(number)));
                return;
            }
            if (!XrHeadset.TryGetEyeView(true, out var lp, out var lq, out _) || !XrHeadset.TryGetEyeView(false, out var rp, out var rq, out _)) return;
            var nowLeft = (lp, lq, XrHeadset.EyeAngles(true));
            var nowRight = (rp, rq, XrHeadset.EyeAngles(false));
            long number = (AppContext.GetData("WpfWebGpu.Xr.ExternalDrawLatest") as Func<Func<long, float[]?>, long>)?.Invoke(k => MovedViews(k, nowLeft, nowRight)) ?? -1;
            if (number >= 0) (AppContext.GetData("WpfWebGpu.Xr.PanelDrawn") as Action<long>)?.Invoke(number);
        }
        private static int s_panelWidth, s_panelHeight;

        // the WPF Viewport3D the headset shows alone, if it is one (not a 3D engine's view: s_fullView)
        private static WeakReference? s_fullWpf;
        /// <summary>Whether the fullscreen view is a WPF Viewport3D, drawn for the eyes side by side.</summary>
        internal static bool DrawsWpfFullscreen => s_fullWpf?.Target != null;
        private static bool s_wpfLogged;

        /// <summary>A WPF Viewport3D filling the eyes: its camera taken as a 3D engine's is (FullscreenEye), the eyes'
        /// cameras from it, and its scene drawn for them by the compositor, which publishes the picture to the
        /// headset's panel with its depth (WpfCompositionSink.Headset.cs) -- the same panel, eyes and controls as
        /// an engine's view. Each frame: a head is never still.</summary>
        private static void DrawWpfFullscreen(Viewport3D viewport)
        {
            if (viewport.Camera is not PerspectiveCamera camera || camera.LookDirection.Length <= 0 || viewport.ActualWidth <= 0 || viewport.ActualHeight <= 0) return;
            if (PresentationSource.FromVisual(viewport) is not System.Windows.Interop.HwndSource source || source.RootVisual == null || source.CompositionTarget == null) return;
            if (AppContext.GetData("WpfWebGpu.Xr.RenderViewport3D") is not Func<IntPtr, float[], float[], int, int, bool> render) return;
            if (!XrHeadset.TryGetEyeView(true, out _, out _, out var tan) || tan.Z - tan.W <= 0) return;

            // its rectangle in its window's device pixels (the compositor finds it by that), and in the room
            Rect inRoot = viewport.TransformToAncestor(source.RootVisual).TransformBounds(new Rect(0, 0, viewport.ActualWidth, viewport.ActualHeight));
            System.Windows.Media.Matrix toDevice = source.CompositionTarget.TransformToDevice;
            Point d0 = toDevice.Transform(inRoot.TopLeft), d1 = toDevice.Transform(inRoot.BottomRight);
            Point topLeft = viewport.PointToScreen(new Point(0, 0)), bottomRight = viewport.PointToScreen(new Point(viewport.ActualWidth, viewport.ActualHeight));
            var a = XrHeadset.DesktopToSpace(topLeft.X, topLeft.Y);
            var b = XrHeadset.DesktopToSpace(bottomRight.X, bottomRight.Y);
            var rectCentre = (a + b) / 2;
            float halfWideM = MathF.Abs(b.X - a.X) / 2, halfHighM = MathF.Abs(b.Y - a.Y) / 2;
            if (halfWideM <= 0 || halfHighM <= 0) return;

            // the camera as the window's 3D pass has it (right-handed; WPF's field of view is horizontal)
            var position = new System.Numerics.Vector3((float)camera.Position.X, (float)camera.Position.Y, (float)camera.Position.Z);
            var look = new System.Numerics.Vector3((float)camera.LookDirection.X, (float)camera.LookDirection.Y, (float)camera.LookDirection.Z);
            var up = new System.Numerics.Vector3((float)camera.UpDirection.X, (float)camera.UpDirection.Y, (float)camera.UpDirection.Z);
            float focus = look.Length();
            var forward = look / focus;
            var rightAxis = System.Numerics.Vector3.Cross(forward, up);
            if (rightAxis.LengthSquared() < 1e-8f) return;
            rightAxis = System.Numerics.Vector3.Normalize(rightAxis);
            var upAxis = System.Numerics.Vector3.Cross(rightAxis, forward);
            float tanHalf = MathF.Tan((float)camera.FieldOfView * MathF.PI / 360f);
            float aspect = (float)(viewport.ActualWidth / viewport.ActualHeight);
            float halfWide = focus * tanHalf, halfHigh = halfWide / aspect;
            float s = (halfWide / halfWideM + halfHigh / halfHighM) / 2;
            // an infinite far plane (WPF's default) bounded: the scene well beyond what the camera looks at
            float far = double.IsFinite(camera.FarPlaneDistance) ? (float)camera.FarPlaneDistance : focus * 100f;
            var worldUp = System.Numerics.Vector3.Normalize(up);
            var centre = position + forward * focus;
            var cameras = new float[64];
            for (int e = 0; e < 2; e++)
            {
                if (!FullscreenEye(e == 0, true, rightAxis, upAxis, forward, worldUp, centre, rectCentre, s, far, halfWide, out var eyeView, out var eyeProjection, out _, out _)) return;
                Put(eyeView, e * 32);
                Put(eyeProjection, e * 32 + 16);
            }
            void Put(System.Numerics.Matrix4x4 m, int at)
            {
                cameras[at] = m.M11; cameras[at + 1] = m.M12; cameras[at + 2] = m.M13; cameras[at + 3] = m.M14;
                cameras[at + 4] = m.M21; cameras[at + 5] = m.M22; cameras[at + 6] = m.M23; cameras[at + 7] = m.M24;
                cameras[at + 8] = m.M31; cameras[at + 9] = m.M32; cameras[at + 10] = m.M33; cameras[at + 11] = m.M34;
                cameras[at + 12] = m.M41; cameras[at + 13] = m.M42; cameras[at + 14] = m.M43; cameras[at + 15] = m.M44;
            }

            // each eye's half shaped as its field of view, as an engine's view is rendered (BeginFullscreen)
            double high = FullscreenHeight();
            int height = (int)Math.Round(high), eyeWidth = (int)Math.Round(high * (tan.Y - tan.X) / (tan.Z - tan.W));
            float[] rect = { (float)d0.X, (float)d0.Y, (float)(d1.X - d0.X), (float)(d1.Y - d0.Y) };
            bool drawn = render(source.Handle, rect, cameras, 2 * eyeWidth, height);
            if (drawn) lock (s_rectsLock) { s_fullWidth = 2 * eyeWidth; s_fullHeight = height; }
            if (!s_wpfLogged) { s_wpfLogged = true; Log(drawn ? $"WPF fullscreen drawn at {2 * eyeWidth}x{height}, scene {s:0.##} units/m" : "WPF fullscreen: the compositor has no such viewport"); }
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
            s_fullWpf = null;
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
            // an engine's view or a WPF Viewport3D (drawn for the eyes)
            return (s_fullView != null || DrawsWpfFullscreen) && width > 0 && height > 0;
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
        /// <summary>The 3D views known (engines' and WPF's, visible), for the XR host's log.</summary>
        internal static string ViewsNote()
        {
            lock (s_rectsLock) return $"{s_viewRects.Count} view(s) known, {s_wpfViewports.Count} WPF Viewport3D(s) seen, the frame handler {(s_following ? "on" : "off")}";
        }

        internal static bool TryGetViewRect(int x, int y, out Rect rect)
        {
            lock (s_rectsLock)
            {
                // the view under the point only: B elsewhere is the rest of the app (an immersive app's way back to
                // its panel), not the nearest view
                foreach (Rect r in s_viewRects)
                    if (r.Contains(x, y)) { rect = r; return true; }
                rect = Rect.Empty;
                return false;
            }
        }
        private static System.Numerics.Vector3 s_drawnLeft, s_drawnRight;

        private static System.Numerics.Quaternion s_drawnTurn;

        private static bool EyesMoved()
        {
            if (!XrHeadset.TryGetEyes(out var left, out var right)) return false;
            // filling the eyes, a head turned shows another part of the scene too
            bool turned = false;
            if (XrHeadset.IsFullscreen && XrHeadset.TryGetEyeView(true, out _, out var turn, out _))
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
            s_eyeSteps.AddOrUpdate(scene, step);
            s_headTracked = true;
            AppContext.SetData("WpfWebGpu.Xr.SideBySideConvergence", 0.0);
            Log("each eye drawn from where it is");
        }

        /// <summary>An eye's camera filling the eye (the fullscreen view): drawn as the eye sees, its pose and its field
        /// of view, the scene where the view's rectangle put it, as the controllers turned, scaled and moved it. The
        /// scene camera given by its axes (right, up, forward: the scene's frame, of either handedness), the point it
        /// looks at (<paramref name="centre"/>), the scene's units a metre there (<paramref name="s"/>) and its far plane;
        /// <paramref name="worldUp"/> the scene's up; the view's rectangle's centre in the room. The eye's view and
        /// projection (depth 0 near to 1 far), and its near and far planes; recorded as the eye drawn for. A 3D
        /// engine's view and a WPF Viewport3D both; false when the headset does not know the eye.</summary>
        private static bool FullscreenEye(bool left, bool rightHanded, System.Numerics.Vector3 rightAxis, System.Numerics.Vector3 upAxis,
            System.Numerics.Vector3 forward, System.Numerics.Vector3 worldUp, System.Numerics.Vector3 centre, System.Numerics.Vector3 rectCentre,
            float s, float far, float halfWide, out System.Numerics.Matrix4x4 eyeView, out System.Numerics.Matrix4x4 eyeProjection, out float n, out float f)
        {
            eyeView = eyeProjection = default;
            n = f = 0;
            if (!XrHeadset.TryGetEyeView(left, out var eyePosition, out var eyeTurn, out var tan)) return false;
            // level: the scene's up (the WPF camera's UpDirection) is the room's, the camera's heading straight ahead
            // (a camera looking down on a floor tilted it into a slope)
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
            var eye = centre + Scene(from) * s;
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
                s_drawn[left ? 0 : 1] = (eyePosition, eyeTurn, XrHeadset.EyeAngles(left));
                s_drawnClip[left ? 0 : 1] = new System.Numerics.Vector2(n / s, f / s);     // metres
                s_drawnTime = XrHeadset.EyesTime;
            }
            return true;
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
                    if (!XrHeadset.TryGetEyes(out System.Numerics.Vector3 leftEye, out System.Numerics.Vector3 rightEye)) return;
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
                    var a = XrHeadset.DesktopToSpace(topLeft.X, topLeft.Y);
                    var b = XrHeadset.DesktopToSpace(bottomRight.X, bottomRight.Y);
                    var rectCentre = (a + b) / 2;
                    float halfWideM = MathF.Abs(b.X - a.X) / 2, halfHighM = MathF.Abs(b.Y - a.Y) / 2;
                    if (halfWideM <= 0 || halfHighM <= 0) return;
                    var d = (left ? leftEye : rightEye) - rectCentre;
                    float sx = halfWide / halfWideM, sy = halfHigh / halfHighM, s = (sx + sy) / 2;
                    float dx = d.X * sx, dy = d.Y * sy, dz = MathF.Max(d.Z * s, near * 2);

                    var eye = centre + rightAxis * dx + upAxis * dy - forward * dz;
                    float n = near, f = far + MathF.Max(0, dz - focus);
                    System.Numerics.Matrix4x4 eyeView, eyeProjection;
                    var wpfUp = element.GetType().GetProperty("Viewport3D")?.GetValue(element) is Viewport3D v3 && v3.Camera is ProjectionCamera wc && wc.UpDirection.Length > 0
                        ? System.Numerics.Vector3.Normalize(new System.Numerics.Vector3((float)wc.UpDirection.X, (float)wc.UpDirection.Y, (float)wc.UpDirection.Z))
                        : upAxis;
                    // filling the eye (FullscreenEye), or before the window: off-axis from where the eye is
                    if (!(XrHeadset.IsFullscreen && ReferenceEquals(s_fullView?.Target, element)
                        && FullscreenEye(left, rightHanded, rightAxis, upAxis, forward, wpfUp, centre, rectCentre, s, far, halfWide, out eyeView, out eyeProjection, out n, out f)))
                    {
                        n = near;
                        f = far + MathF.Max(0, dz - focus);
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
