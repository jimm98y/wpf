// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// OpenXR, as much of it as AndroidXrHost uses, against Khronos's loader for Android (libopenxr_loader.so,
// org.khronos.openxr:openxr_loader_for_android, Apache-2.0 OR MIT), which the bundle carries when it is
// made with --xr. Layouts and values are openxr.h's and openxr_platform.h's (1.1.65); arm64's natural
// alignment is C's, so the structs are plain sequential ones.
//
// The core entry points are the loader's exports; the extensions' are asked of the instance
// (xrGetInstanceProcAddr). EGL is here only for the graphics binding a session needs: every layer this
// host shows is an Android Surface (XR_KHR_android_surface_swapchain), so nothing is ever drawn with GL.
//

#nullable enable

using System;
using System.Runtime.InteropServices;
using System.Text;

namespace MS.Internal.Interop
{
    internal static unsafe class Xr
    {
        private const string Loader = "openxr_loader";

        public const int Success = 0;
        public const int EventUnavailable = 4;    // XR_EVENT_UNAVAILABLE
        public const int SessionLossPending = 3;   // XR_SESSION_LOSS_PENDING

        // XrStructureType
        public const int TypeInstanceCreateInfo = 3, TypeSystemGetInfo = 4, TypeSessionCreateInfo = 8, TypeSwapchainCreateInfo = 9,
            TypeSessionBeginInfo = 10, TypeFrameEndInfo = 12, TypeEventDataBuffer = 16, TypeEventDataInstanceLossPending = 17,
            TypeEventDataSessionStateChanged = 18, TypeActionStateBoolean = 23, TypeActionStateFloat = 24, TypeActionStateVector2f = 25,
            TypeActionSetCreateInfo = 28, TypeActionCreateInfo = 29, TypeFrameWaitInfo = 33, TypeCompositionLayerQuad = 36,
            TypeReferenceSpaceCreateInfo = 37, TypeActionSpaceCreateInfo = 38, TypeSpaceLocation = 42, TypeFrameState = 44,
            TypeFrameBeginInfo = 46, TypeInteractionProfileSuggestedBinding = 51, TypeActionStateGetInfo = 58,
            TypeSessionActionSetsAttachInfo = 60, TypeActionsSyncInfo = 61,
            TypeInstanceCreateInfoAndroid = 1000008000, TypeGraphicsBindingOpenGlesAndroid = 1000024001,
            TypeGraphicsRequirementsOpenGles = 1000024003, TypeLoaderInitInfoAndroid = 1000089000,
            TypeExtensionProperties = 2, TypeCompositionLayerImageLayout = 1000040000,
            TypeViewLocateInfo = 6, TypeView = 7, TypeViewState = 11,
            TypeCompositionLayerProjection = 35, TypeCompositionLayerProjectionView = 48,
            TypePassthroughCreateInfo = 1000118001, TypePassthroughLayerCreateInfo = 1000118002, TypeCompositionLayerPassthrough = 1000118003;

        public const ulong PassthroughIsRunningAtCreation = 1;
        public const int PassthroughLayerPurposeReconstruction = 0;

        public const ulong ImageLayoutVerticalFlip = 1;

        public const int FormFactorHeadMountedDisplay = 1;
        public const int ViewConfigurationPrimaryStereo = 2;
        public const int BlendModeOpaque = 1;
        public const int ReferenceSpaceView = 1, ReferenceSpaceLocal = 2, ReferenceSpaceStage = 3;
        public const int EyeBoth = 0, EyeLeft = 1, EyeRight = 2;
        public const int ActionBoolean = 1, ActionFloat = 2, ActionVector2f = 3, ActionPose = 4;
        public const int StateIdle = 1, StateReady = 2, StateSynchronized = 3, StateVisible = 4, StateFocused = 5,
            StateStopping = 6, StateLossPending = 7, StateExiting = 8;
        public const ulong LocationOrientationValid = 1, LocationPositionValid = 2;
        public const ulong LayerBlendTextureSourceAlpha = 2;
        public const ulong SwapchainUsageColorAttachment = 1, SwapchainUsageSampled = 0x20;

        public static ulong MakeVersion(ulong major, ulong minor, ulong patch) => (major << 48) | (minor << 32) | patch;

        [StructLayout(LayoutKind.Sequential)] public struct Quaternion { public float X, Y, Z, W; }
        [StructLayout(LayoutKind.Sequential)] public struct Vector3 { public float X, Y, Z; }
        [StructLayout(LayoutKind.Sequential)] public struct Vector2 { public float X, Y; }
        [StructLayout(LayoutKind.Sequential)] public struct Pose { public Quaternion Orientation; public Vector3 Position; }
        [StructLayout(LayoutKind.Sequential)] public struct Extent2Df { public float Width, Height; }
        [StructLayout(LayoutKind.Sequential)] public struct Rect2Di { public int X, Y, Width, Height; }

        public static Pose Identity => new Pose { Orientation = new Quaternion { W = 1 } };

        [StructLayout(LayoutKind.Sequential)]
        public struct ApplicationInfo
        {
            public fixed byte ApplicationName[128];
            public uint ApplicationVersion;
            public fixed byte EngineName[128];
            public uint EngineVersion;
            public ulong ApiVersion;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct InstanceCreateInfo
        {
            public int Type; public void* Next; public ulong CreateFlags;
            public ApplicationInfo ApplicationInfo;
            public uint EnabledApiLayerCount; public byte** EnabledApiLayerNames;
            public uint EnabledExtensionCount; public byte** EnabledExtensionNames;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct InstanceCreateInfoAndroid { public int Type; public void* Next; public IntPtr ApplicationVM; public IntPtr ApplicationActivity; }

        [StructLayout(LayoutKind.Sequential)]
        public struct LoaderInitInfoAndroid { public int Type; public void* Next; public IntPtr ApplicationVM; public IntPtr ApplicationContext; }

        [StructLayout(LayoutKind.Sequential)]
        public struct SystemGetInfo { public int Type; public void* Next; public int FormFactor; }

        [StructLayout(LayoutKind.Sequential)]
        public struct GraphicsRequirementsOpenGles { public int Type; public void* Next; public ulong MinApiVersionSupported; public ulong MaxApiVersionSupported; }

        [StructLayout(LayoutKind.Sequential)]
        public struct GraphicsBindingOpenGlesAndroid { public int Type; public void* Next; public IntPtr Display; public IntPtr Config; public IntPtr Context; }

        [StructLayout(LayoutKind.Sequential)]
        public struct SessionCreateInfo { public int Type; public void* Next; public ulong CreateFlags; public ulong SystemId; }

        [StructLayout(LayoutKind.Sequential)]
        public struct SessionBeginInfo { public int Type; public void* Next; public int PrimaryViewConfigurationType; }

        [StructLayout(LayoutKind.Sequential)]
        public struct EventDataBuffer { public int Type; public void* Next; public fixed byte Varying[4000]; }

        [StructLayout(LayoutKind.Sequential)]
        public struct EventDataSessionStateChanged { public int Type; public void* Next; public ulong Session; public int State; public long Time; }

        [StructLayout(LayoutKind.Sequential)]
        public struct FrameWaitInfo { public int Type; public void* Next; }

        [StructLayout(LayoutKind.Sequential)]
        public struct FrameState { public int Type; public void* Next; public long PredictedDisplayTime; public long PredictedDisplayPeriod; public uint ShouldRender; }

        [StructLayout(LayoutKind.Sequential)]
        public struct FrameBeginInfo { public int Type; public void* Next; }

        [StructLayout(LayoutKind.Sequential)]
        public struct FrameEndInfo { public int Type; public void* Next; public long DisplayTime; public int EnvironmentBlendMode; public uint LayerCount; public void** Layers; }

        [StructLayout(LayoutKind.Sequential)]
        public struct ReferenceSpaceCreateInfo { public int Type; public void* Next; public int ReferenceSpaceType; public Pose PoseInReferenceSpace; }

        [StructLayout(LayoutKind.Sequential)]
        public struct SwapchainSubImage { public ulong Swapchain; public Rect2Di ImageRect; public uint ImageArrayIndex; }

        [StructLayout(LayoutKind.Sequential)]
        public struct CompositionLayerQuad
        {
            public int Type; public void* Next; public ulong LayerFlags; public ulong Space; public int EyeVisibility;
            public SwapchainSubImage SubImage; public Pose Pose; public Extent2Df Size;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct ExtensionProperties
        {
            public int Type; public void* Next; public fixed byte ExtensionName[128]; public uint ExtensionVersion;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct ViewLocateInfo
        {
            public int Type; public void* Next; public int ViewConfigurationType; public long DisplayTime; public ulong Space;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct ViewState
        {
            public int Type; public void* Next; public ulong ViewStateFlags;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct Fov { public float AngleLeft, AngleRight, AngleUp, AngleDown; }

        /// <summary>XrView: where an eye is and what it sees.</summary>
        [StructLayout(LayoutKind.Sequential)]
        public struct View
        {
            public int Type; public void* Next; public Pose Pose; public Fov Fov;
        }

        /// <summary>XrCompositionLayerProjectionView: an eye's image, with the pose and field of view it was drawn for.</summary>
        [StructLayout(LayoutKind.Sequential)]
        public struct CompositionLayerProjectionView
        {
            public int Type; public void* Next; public Pose Pose; public Fov Fov; public SwapchainSubImage SubImage;
        }

        /// <summary>XrCompositionLayerProjection: a view for each eye, filling it.</summary>
        [StructLayout(LayoutKind.Sequential)]
        public struct CompositionLayerProjection
        {
            public int Type; public void* Next; public ulong LayerFlags; public ulong Space; public uint ViewCount; public CompositionLayerProjectionView* Views;
        }

        /// <summary>XrPassthroughCreateInfoFB (XR_FB_passthrough).</summary>
        [StructLayout(LayoutKind.Sequential)]
        public struct PassthroughCreateInfo
        {
            public int Type; public void* Next; public ulong Flags;
        }

        /// <summary>XrPassthroughLayerCreateInfoFB.</summary>
        [StructLayout(LayoutKind.Sequential)]
        public struct PassthroughLayerCreateInfo
        {
            public int Type; public void* Next; public ulong Passthrough; public ulong Flags; public int Purpose;
        }

        /// <summary>XrCompositionLayerPassthroughFB: the cameras' view of the room, as a layer.</summary>
        [StructLayout(LayoutKind.Sequential)]
        public struct CompositionLayerPassthrough
        {
            public int Type; public void* Next; public ulong Flags; public ulong Space; public ulong LayerHandle;
        }

        /// <summary>XrCompositionLayerImageLayoutFB (XR_FB_composition_layer_image_layout), chained to a layer.</summary>
        [StructLayout(LayoutKind.Sequential)]
        public struct CompositionLayerImageLayout
        {
            public int Type; public void* Next; public ulong Flags;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct SwapchainCreateInfo
        {
            public int Type; public void* Next; public ulong CreateFlags; public ulong UsageFlags; public long Format;
            public uint SampleCount, Width, Height, FaceCount, ArraySize, MipCount;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct SpaceLocation { public int Type; public void* Next; public ulong LocationFlags; public Pose Pose; }

        [StructLayout(LayoutKind.Sequential)]
        public struct ActionSetCreateInfo { public int Type; public void* Next; public fixed byte ActionSetName[64]; public fixed byte LocalizedActionSetName[128]; public uint Priority; }

        [StructLayout(LayoutKind.Sequential)]
        public struct ActionCreateInfo
        {
            public int Type; public void* Next; public fixed byte ActionName[64]; public int ActionType;
            public uint CountSubactionPaths; public ulong* SubactionPaths; public fixed byte LocalizedActionName[128];
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct ActionSuggestedBinding { public ulong Action; public ulong Binding; }

        [StructLayout(LayoutKind.Sequential)]
        public struct InteractionProfileSuggestedBinding { public int Type; public void* Next; public ulong InteractionProfile; public uint CountSuggestedBindings; public ActionSuggestedBinding* SuggestedBindings; }

        [StructLayout(LayoutKind.Sequential)]
        public struct SessionActionSetsAttachInfo { public int Type; public void* Next; public uint CountActionSets; public ulong* ActionSets; }

        [StructLayout(LayoutKind.Sequential)]
        public struct ActiveActionSet { public ulong ActionSet; public ulong SubactionPath; }

        [StructLayout(LayoutKind.Sequential)]
        public struct ActionsSyncInfo { public int Type; public void* Next; public uint CountActiveActionSets; public ActiveActionSet* ActiveActionSets; }

        [StructLayout(LayoutKind.Sequential)]
        public struct ActionStateGetInfo { public int Type; public void* Next; public ulong Action; public ulong SubactionPath; }

        [StructLayout(LayoutKind.Sequential)]
        public struct ActionStateBoolean { public int Type; public void* Next; public uint CurrentState; public uint ChangedSinceLastSync; public long LastChangeTime; public uint IsActive; }

        [StructLayout(LayoutKind.Sequential)]
        public struct ActionStateFloat { public int Type; public void* Next; public float CurrentState; public uint ChangedSinceLastSync; public long LastChangeTime; public uint IsActive; }

        [StructLayout(LayoutKind.Sequential)]
        public struct ActionStateVector2f { public int Type; public void* Next; public Vector2 CurrentState; public uint ChangedSinceLastSync; public long LastChangeTime; public uint IsActive; }

        [StructLayout(LayoutKind.Sequential)]
        public struct ActionSpaceCreateInfo { public int Type; public void* Next; public ulong Action; public ulong SubactionPath; public Pose PoseInActionSpace; }

        // ---- the loader's exports (core) ----
        [DllImport(Loader)] public static extern int xrGetInstanceProcAddr(ulong instance, byte* name, IntPtr* function);
        [DllImport(Loader)] public static extern int xrEnumerateInstanceExtensionProperties(byte* layerName, uint capacity, uint* count, ExtensionProperties* properties);
        [DllImport(Loader)] public static extern int xrCreateInstance(InstanceCreateInfo* info, ulong* instance);
        [DllImport(Loader)] public static extern int xrDestroyInstance(ulong instance);
        [DllImport(Loader)] public static extern int xrGetSystem(ulong instance, SystemGetInfo* info, ulong* systemId);
        [DllImport(Loader)] public static extern int xrCreateSession(ulong instance, SessionCreateInfo* info, ulong* session);
        [DllImport(Loader)] public static extern int xrDestroySession(ulong session);
        [DllImport(Loader)] public static extern int xrBeginSession(ulong session, SessionBeginInfo* info);
        [DllImport(Loader)] public static extern int xrEndSession(ulong session);
        [DllImport(Loader)] public static extern int xrRequestExitSession(ulong session);
        [DllImport(Loader)] public static extern int xrPollEvent(ulong instance, EventDataBuffer* eventData);
        [DllImport(Loader)] public static extern int xrWaitFrame(ulong session, FrameWaitInfo* info, FrameState* state);
        [DllImport(Loader)] public static extern int xrBeginFrame(ulong session, FrameBeginInfo* info);
        [DllImport(Loader)] public static extern int xrEndFrame(ulong session, FrameEndInfo* info);
        [DllImport(Loader)] public static extern int xrCreateReferenceSpace(ulong session, ReferenceSpaceCreateInfo* info, ulong* space);
        [DllImport(Loader)] public static extern int xrLocateSpace(ulong space, ulong baseSpace, long time, SpaceLocation* location);
        [DllImport(Loader)] public static extern int xrLocateViews(ulong session, ViewLocateInfo* info, ViewState* state, uint capacity, uint* count, View* views);
        [DllImport(Loader)] public static extern int xrDestroySpace(ulong space);
        [DllImport(Loader)] public static extern int xrDestroySwapchain(ulong swapchain);
        [DllImport(Loader)] public static extern int xrEnumerateSwapchainFormats(ulong session, uint capacity, uint* count, long* formats);
        [DllImport(Loader)] public static extern int xrStringToPath(ulong instance, byte* pathString, ulong* path);
        [DllImport(Loader)] public static extern int xrCreateActionSet(ulong instance, ActionSetCreateInfo* info, ulong* actionSet);
        [DllImport(Loader)] public static extern int xrCreateAction(ulong actionSet, ActionCreateInfo* info, ulong* action);
        [DllImport(Loader)] public static extern int xrSuggestInteractionProfileBindings(ulong instance, InteractionProfileSuggestedBinding* suggested);
        [DllImport(Loader)] public static extern int xrAttachSessionActionSets(ulong session, SessionActionSetsAttachInfo* info);
        [DllImport(Loader)] public static extern int xrSyncActions(ulong session, ActionsSyncInfo* info);
        [DllImport(Loader)] public static extern int xrGetActionStateBoolean(ulong session, ActionStateGetInfo* info, ActionStateBoolean* state);
        [DllImport(Loader)] public static extern int xrGetActionStateFloat(ulong session, ActionStateGetInfo* info, ActionStateFloat* state);
        [DllImport(Loader)] public static extern int xrGetActionStateVector2f(ulong session, ActionStateGetInfo* info, ActionStateVector2f* state);
        [DllImport(Loader)] public static extern int xrCreateActionSpace(ulong session, ActionSpaceCreateInfo* info, ulong* space);

        // ---- extension entry points, asked of the instance (or of none, for the loader's own) ----
        public static IntPtr Proc(ulong instance, string name)
        {
            byte[] bytes = Encoding.ASCII.GetBytes(name + "\0");
            IntPtr fn = IntPtr.Zero;
            fixed (byte* p = bytes)
                if (xrGetInstanceProcAddr(instance, p, &fn) != Success) return IntPtr.Zero;
            return fn;
        }

        public static ulong Path(ulong instance, string path)
        {
            byte[] bytes = Encoding.ASCII.GetBytes(path + "\0");
            ulong result = 0;
            fixed (byte* p = bytes) xrStringToPath(instance, p, &result);
            return result;
        }

        public static void Copy(string text, byte* destination, int capacity)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(text);
            int n = Math.Min(bytes.Length, capacity - 1);
            for (int i = 0; i < n; i++) destination[i] = bytes[i];
            destination[n] = 0;
        }

        // ---- poses ----
        public static Vector3 Rotate(Quaternion q, Vector3 v)
        {
            // v' = v + 2w (u x v) + 2 u x (u x v), u the quaternion's vector part
            float ux = q.X, uy = q.Y, uz = q.Z, w = q.W;
            float cx = uy * v.Z - uz * v.Y, cy = uz * v.X - ux * v.Z, cz = ux * v.Y - uy * v.X;
            float ccx = uy * cz - uz * cy, ccy = uz * cx - ux * cz, ccz = ux * cy - uy * cx;
            return new Vector3 { X = v.X + 2 * (w * cx + ccx), Y = v.Y + 2 * (w * cy + ccy), Z = v.Z + 2 * (w * cz + ccz) };
        }
    }

    /// <summary>EGL, for the context an OpenXR session's OpenGL ES binding names; nothing is drawn with it.</summary>
    internal static unsafe class Egl
    {
        private const string Lib = "EGL";
        public const int None = 0x3038, RedSize = 0x3024, GreenSize = 0x3023, BlueSize = 0x3022, AlphaSize = 0x3021,
            RenderableType = 0x3040, SurfaceType = 0x3033, PbufferBit = 0x0001, OpenGlEs3Bit = 0x0040,
            ContextClientVersion = 0x3098, Width = 0x3057, Height = 0x3056;

        [DllImport(Lib)] public static extern IntPtr eglGetDisplay(IntPtr displayId);
        [DllImport(Lib)] public static extern uint eglInitialize(IntPtr display, int* major, int* minor);
        [DllImport(Lib)] public static extern uint eglChooseConfig(IntPtr display, int* attribs, IntPtr* configs, int configSize, int* numConfig);
        [DllImport(Lib)] public static extern IntPtr eglCreateContext(IntPtr display, IntPtr config, IntPtr shareContext, int* attribs);
        [DllImport(Lib)] public static extern IntPtr eglCreatePbufferSurface(IntPtr display, IntPtr config, int* attribs);
        [DllImport(Lib)] public static extern uint eglMakeCurrent(IntPtr display, IntPtr draw, IntPtr read, IntPtr context);
    }
}
