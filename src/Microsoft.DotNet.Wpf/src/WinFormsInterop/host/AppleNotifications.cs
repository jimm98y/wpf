// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// A NotifyIcon's balloon tip on Apple's platforms: a notification of the user-notification centre (UserNotifications,
// the same on macOS and iOS), as Windows 10 shows a balloon as a toast. Its delegate says when the user clicked it or
// closed it, and a timer that the banner has gone from the screen; the icon's window is told as the Windows shell
// tells it (NIN_BALLOONSHOW, NIN_BALLOONUSERCLICK, NIN_BALLOONTIMEOUT in lParam of its callback message). The centre
// takes notifications only from an application that is a bundle (every iOS app; a Mac app packaged as a .app).
//

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows.Forms;

internal static unsafe class AppleNotifications
{
    private const string ObjC = "/usr/lib/libobjc.dylib";
    internal const int NIN_BALLOONSHOW = 0x402, NIN_BALLOONHIDE = 0x403, NIN_BALLOONTIMEOUT = 0x404, NIN_BALLOONUSERCLICK = 0x405;

    private static readonly object s_lock = new();
    private static readonly Dictionary<string, IntPtr> s_windows = new();     // identifier -> icon window
    private static readonly HashSet<string> s_done = new();
    private static readonly Dictionary<string, System.Threading.Timer> s_timers = new();
    private static IntPtr s_center;

    [DllImport(ObjC)] private static extern IntPtr objc_getClass(string name);
    [DllImport(ObjC)] private static extern IntPtr sel_registerName(string name);
    [DllImport(ObjC)] private static extern IntPtr objc_allocateClassPair(IntPtr superclass, string name, UIntPtr extra);
    [DllImport(ObjC)] private static extern void objc_registerClassPair(IntPtr cls);
    [DllImport(ObjC)] [return: MarshalAs(UnmanagedType.I1)] private static extern bool class_addMethod(IntPtr cls, IntPtr sel, IntPtr imp, string types);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr Send(IntPtr r, IntPtr s);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr Send(IntPtr r, IntPtr s, IntPtr a);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr Send(IntPtr r, IntPtr s, IntPtr a, IntPtr b);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr Send(IntPtr r, IntPtr s, IntPtr a, IntPtr b, IntPtr c);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr SendCategory(IntPtr r, IntPtr s, IntPtr a, IntPtr b, IntPtr c, nuint d);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern void SendAuthorize(IntPtr r, IntPtr s, nuint options, IntPtr block);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr SendStr(IntPtr r, IntPtr s, [MarshalAs(UnmanagedType.LPUTF8Str)] string a);
    [DllImport("/usr/lib/libSystem.dylib")] private static extern IntPtr dlopen(string path, int mode);

    private static IntPtr Cls(string n) => objc_getClass(n);
    private static IntPtr Sel(string n) => sel_registerName(n);
    private static IntPtr NSStr(string s) => SendStr(Cls("NSString"), Sel("stringWithUTF8String:"), s ?? "");
    private static string Str(IntPtr s) => s == IntPtr.Zero ? null : Marshal.PtrToStringUTF8(Send(s, Sel("UTF8String")));

    /// <summary>Whether this process is an application bundle the notification centre accepts notifications from.</summary>
    internal static bool Available()
    {
        if (OperatingSystem.IsIOS()) return true;
        IntPtr bundle = Send(Cls("NSBundle"), Sel("mainBundle"));
        string path = bundle == IntPtr.Zero ? null : Str(Send(bundle, Sel("bundlePath")));
        return bundle != IntPtr.Zero && Send(bundle, Sel("bundleIdentifier")) != IntPtr.Zero
               && path != null && path.EndsWith(".app", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The balloon of the icon whose window is <paramref name="window"/>: one notification an icon, a second
    /// replacing the first.</summary>
    internal static void Show(IntPtr window, string title, string text)
    {
        IntPtr center = Center();
        if (center == IntPtr.Zero) return;
        string id = "wf-balloon-" + window.ToInt64();
        lock (s_lock) { s_windows[id] = window; s_done.Remove(id); }
        IntPtr content = Send(Send(Cls("UNMutableNotificationContent"), Sel("alloc")), Sel("init"));
        Send(content, Sel("setTitle:"), NSStr(title));
        Send(content, Sel("setBody:"), NSStr(text));
        Send(content, Sel("setCategoryIdentifier:"), NSStr("wf-balloon"));
        IntPtr request = Send(Cls("UNNotificationRequest"), Sel("requestWithIdentifier:content:trigger:"), NSStr(id), content, IntPtr.Zero);
        Send(center, Sel("addNotificationRequest:withCompletionHandler:"), request, IntPtr.Zero);
        XplatUI.PostMessage(window, (Msg)0x0400, IntPtr.Zero, (IntPtr)NIN_BALLOONSHOW);
        // the banner goes from the screen after a few seconds (into the centre's list): the balloon timed out
        var timer = new System.Threading.Timer(_ => Finish(id, NIN_BALLOONTIMEOUT));
        lock (s_lock)
        {
            if (s_timers.Remove(id, out System.Threading.Timer old)) old.Dispose();
            s_timers[id] = timer;
        }
        timer.Change(6000, System.Threading.Timeout.Infinite);
    }

    private static void Finish(string id, int message)
    {
        IntPtr window;
        lock (s_lock)
        {
            if (!s_windows.TryGetValue(id, out window) || !s_done.Add(id)) return;
        }
        XplatUI.PostMessage(window, (Msg)0x0400, IntPtr.Zero, (IntPtr)message);
    }

    /// <summary>The centre, with this application's delegate and the balloon category (whose dismissal is reported),
    /// permission asked once.</summary>
    private static IntPtr Center()
    {
        if (s_center != IntPtr.Zero) return s_center;
        if (OperatingSystem.IsMacOS()) dlopen("/System/Library/Frameworks/UserNotifications.framework/UserNotifications", 1);
        IntPtr center = Send(Cls("UNUserNotificationCenter"), Sel("currentNotificationCenter"));
        if (center == IntPtr.Zero) return IntPtr.Zero;
        IntPtr cls = Cls("WfNotificationDelegate");
        if (cls == IntPtr.Zero)
        {
            cls = objc_allocateClassPair(Cls("NSObject"), "WfNotificationDelegate", UIntPtr.Zero);
            class_addMethod(cls, Sel("userNotificationCenter:willPresentNotification:withCompletionHandler:"),
                (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, IntPtr, IntPtr, IntPtr, void>)&WillPresent, "v@:@@@?");
            class_addMethod(cls, Sel("userNotificationCenter:didReceiveNotificationResponse:withCompletionHandler:"),
                (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, IntPtr, IntPtr, IntPtr, void>)&DidReceive, "v@:@@@?");
            objc_registerClassPair(cls);
        }
        Send(center, Sel("setDelegate:"), Send(Send(cls, Sel("alloc")), Sel("init")));
        // the user closing the banner is reported only for a category that asks for it (CustomDismissAction)
        IntPtr category = SendCategory(Cls("UNNotificationCategory"), Sel("categoryWithIdentifier:actions:intentIdentifiers:options:"),
                                       NSStr("wf-balloon"), Send(Cls("NSArray"), Sel("array")), Send(Cls("NSArray"), Sel("array")), 1);
        Send(center, Sel("setNotificationCategories:"), Send(Cls("NSSet"), Sel("setWithObject:"), category));
        IntPtr answered = MS.Internal.Interop.ObjCBlock.Create((IntPtr)(delegate* unmanaged<IntPtr, byte, IntPtr, void>)&Answered, IntPtr.Zero);
        SendAuthorize(center, Sel("requestAuthorizationWithOptions:completionHandler:"), (nuint)(4 | 2), answered);   // alert, sound
        s_center = center;
        return center;
    }

    [UnmanagedCallersOnly]
    private static void Answered(IntPtr block, byte granted, IntPtr error) { }

    /// <summary>A notification arriving while the application is in front: shown all the same, as a balloon is.</summary>
    [UnmanagedCallersOnly]
    private static void WillPresent(IntPtr self, IntPtr sel, IntPtr center, IntPtr notification, IntPtr handler)
    {
        try { CallBlock(handler, (nuint)((1 << 4) | (1 << 3) | (1 << 1))); } catch (Exception) { }   // banner, list, sound
    }

    [UnmanagedCallersOnly]
    private static void DidReceive(IntPtr self, IntPtr sel, IntPtr center, IntPtr response, IntPtr handler)
    {
        try
        {
            string action = Str(Send(response, Sel("actionIdentifier")));
            string id = Str(Send(Send(Send(response, Sel("notification")), Sel("request")), Sel("identifier")));
            if (id != null)
                Finish(id, action == "com.apple.UNNotificationDismissActionIdentifier" ? NIN_BALLOONTIMEOUT : NIN_BALLOONUSERCLICK);
        }
        catch (Exception) { }
        try { CallBlock(handler); } catch (Exception) { }
    }

    // a block's invoke function is 16 bytes into it (isa, flags, reserved)
    private static void CallBlock(IntPtr block, nuint arg) { if (block != IntPtr.Zero) ((delegate* unmanaged<IntPtr, nuint, void>)*(IntPtr*)((byte*)block + 16))(block, arg); }
    private static void CallBlock(IntPtr block) { if (block != IntPtr.Zero) ((delegate* unmanaged<IntPtr, void>)*(IntPtr*)((byte*)block + 16))(block); }
}
