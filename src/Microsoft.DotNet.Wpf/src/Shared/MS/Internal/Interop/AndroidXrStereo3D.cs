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

        private static void Follow(object view, object provider)
        {
            s_followed.Add((new WeakReference(view), provider));
            Update(view, provider);
            if (s_following) return;
            s_following = true;
            System.Windows.Media.CompositionTarget.Rendering += (_, _) =>
            {
                // DXEngine draws when its scene or camera changes, and the head is neither: the eyes moved,
                // each view is drawn again from where they are now
                bool eyesMoved = s_headTracked && EyesMoved();
                for (int i = s_followed.Count - 1; i >= 0; i--)
                {
                    if (s_followed[i].View.Target is not { } v) { s_followed.RemoveAt(i); continue; }
                    Update(v, s_followed[i].Provider);
                    if (eyesMoved) Redraw(v);
                }
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
        private static System.Numerics.Vector3 s_drawnLeft, s_drawnRight;

        private static bool EyesMoved()
        {
            if (!AndroidXrHost.TryGetEyes(out var left, out var right)) return false;
            if (System.Numerics.Vector3.DistanceSquared(left, s_drawnLeft) < 0.0005f * 0.0005f
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
                    float l = (-halfWide - dx) * n / dz, r = (halfWide - dx) * n / dz, bt = (-halfHigh - dy) * n / dz, t = (halfHigh - dy) * n / dz;
                    var eyeView = rightHanded ? System.Numerics.Matrix4x4.CreateLookAt(eye, eye + forward, upAxis) : System.Numerics.Matrix4x4.CreateLookAtLeftHanded(eye, eye + forward, upAxis);
                    var eyeProjection = rightHanded ? System.Numerics.Matrix4x4.CreatePerspectiveOffCenter(l, r, bt, t, n, f) : System.Numerics.Matrix4x4.CreatePerspectiveOffCenterLeftHanded(l, r, bt, t, n, f);

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
