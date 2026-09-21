// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// iOS/iPadOS file dialogs: UIDocumentPickerViewController, reached through the Objective-C runtime
// exactly as CocoaDialogs reaches NSOpenPanel.
//
// Before this file the iOS head had no file dialog: CommonItemDialog fell through to "return false",
// so OpenFileDialog.ShowDialog reported a cancellation of a dialog the user never saw.
//
// The shape differs from macOS in the one way that matters, and it is not a detail of this port:
// NSOpenPanel has -runModal and UIDocumentPickerViewController does not. There is no modal file
// picker on iOS at all -- the controller is PRESENTED and the answer arrives later on a delegate.
// That is why these methods return a Task and why CommonDialog needed ShowDialogAsync; see the
// remarks there.
//
// Two iOS specifics are worth stating because both are silent failures otherwise:
//
//   * A picked document is security-scoped. The URL is useless until
//     -startAccessingSecurityScopedResource is called, and it leaks a kernel resource if the
//     matching stop is skipped. The file is therefore COPIED into the app's own temporary
//     directory while access is held, and that copy's path is what comes back -- which is also
//     what makes the result usable by ordinary File.ReadAllBytes, as WPF callers expect.
//
//   * asCopy:YES on the open picker asks iOS for a copy the app owns outright. Without it the
//     picker hands back a reference to a file in another app's container that can vanish.
//

using System;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading.Tasks;

namespace MS.Internal.Interop
{
    /// <summary>File dialogs for the iOS head, callable from any WPF assembly.</summary>
    [SupportedOSPlatform("ios")]
    public static unsafe class UIKitDialogs
    {
        /// <summary>True when running on iOS/iPadOS, where the picker is reachable.</summary>
        public static bool IsAvailable => OperatingSystem.IsIOS();

        // The pending pick. UIDocumentPicker is presented one at a time (it is full-screen or a
        // sheet), so a single slot is enough and a second call while one is open is a bug in the
        // caller rather than a case to queue.
        private static TaskCompletionSource<string[]> s_pending;

        private static IntPtr s_delegateClass;
        private static IntPtr s_delegateInstance;

        /// <summary>
        ///  Presents the document picker and completes with the chosen paths, or an empty array when
        ///  the user cancelled.
        /// </summary>
        /// <param name="contentTypes">
        ///  Uniform Type Identifiers to allow ("public.plain-text"), or null for any file.
        /// </param>
        public static Task<string[]> ShowOpenPanelAsync(string[] contentTypes, bool multiple, bool directory)
        {
            if (!IsAvailable) return Task.FromResult(Array.Empty<string>());

            IntPtr controller = objc_getClass("UIDocumentPickerViewController");
            if (controller == IntPtr.Zero) return Task.FromResult(Array.Empty<string>());

            IntPtr presenter = RootViewController();
            if (presenter == IntPtr.Zero) return Task.FromResult(Array.Empty<string>());

            // A pick already in flight: refuse rather than losing the first one's completion.
            if (s_pending is not null && !s_pending.Task.IsCompleted)
            {
                return Task.FromResult(Array.Empty<string>());
            }

            var completion = new TaskCompletionSource<string[]>(TaskCreationOptions.RunContinuationsAsynchronously);
            s_pending = completion;

            try
            {
                IntPtr picker = Send(Send(controller, Sel("alloc")),
                                     Sel("initForOpeningContentTypes:asCopy:"),
                                     ContentTypeArray(contentTypes, directory),
                                     (byte)1);

                SendVoidBool(picker, Sel("setAllowsMultipleSelection:"), multiple);
                SendVoidPtr(picker, Sel("setDelegate:"), DelegateInstance());

                // animated:YES, completion:nil.
                SendPresent(presenter, Sel("presentViewController:animated:completion:"),
                            picker, 1, IntPtr.Zero);
            }
            catch (Exception)
            {
                s_pending = null;
                return Task.FromResult(Array.Empty<string>());
            }

            return completion.Task;
        }

        /// <summary>
        ///  Presents the export picker for a file the application has already written, letting the
        ///  user choose where it goes. Completes with the exported path, or null when cancelled.
        /// </summary>
        /// <remarks>
        ///  This is the iOS spelling of "save as": there is no way to obtain a destination path
        ///  BEFORE the write, because the app has no access to the file system outside its own
        ///  container. So SaveFileDialog hands back a path in the app's temporary directory, the
        ///  application writes there as usual, and the export happens afterwards -- the same shape
        ///  the browser head uses for a download.
        /// </remarks>
        public static Task<string[]> ShowExportPanelAsync(string path)
        {
            if (!IsAvailable || string.IsNullOrEmpty(path)) return Task.FromResult(Array.Empty<string>());

            IntPtr controller = objc_getClass("UIDocumentPickerViewController");
            IntPtr presenter = RootViewController();
            if (controller == IntPtr.Zero || presenter == IntPtr.Zero)
            {
                return Task.FromResult(Array.Empty<string>());
            }

            if (s_pending is not null && !s_pending.Task.IsCompleted)
            {
                return Task.FromResult(Array.Empty<string>());
            }

            var completion = new TaskCompletionSource<string[]>(TaskCreationOptions.RunContinuationsAsynchronously);
            s_pending = completion;

            try
            {
                IntPtr url = Send(objc_getClass("NSURL"), Sel("fileURLWithPath:"), NSStr(path));
                IntPtr urls = Send(objc_getClass("NSArray"), Sel("arrayWithObject:"), url);

                IntPtr picker = Send(Send(controller, Sel("alloc")),
                                     Sel("initForExportingURLs:asCopy:"), urls, (byte)1);

                SendVoidPtr(picker, Sel("setDelegate:"), DelegateInstance());
                SendPresent(presenter, Sel("presentViewController:animated:completion:"),
                            picker, 1, IntPtr.Zero);
            }
            catch (Exception)
            {
                s_pending = null;
                return Task.FromResult(Array.Empty<string>());
            }

            return completion.Task;
        }

        /// <summary>A path in the app's temporary directory, for the write that precedes an export.</summary>
        public static string ReserveSavePath(string suggestedName)
        {
            string name = string.IsNullOrEmpty(suggestedName) ? "document" : suggestedName;
            string directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                                                      "wpf-save-" + Guid.NewGuid().ToString("N"));
            System.IO.Directory.CreateDirectory(directory);
            return System.IO.Path.Combine(directory, name);
        }

        // ---- the delegate ----------------------------------------------------------------

        private static IntPtr DelegateInstance()
        {
            if (s_delegateInstance != IntPtr.Zero) return s_delegateInstance;

            if (s_delegateClass == IntPtr.Zero)
            {
                s_delegateClass = objc_getClass("WpfDocumentPickerDelegate");
                if (s_delegateClass == IntPtr.Zero)
                {
                    s_delegateClass = objc_allocateClassPair(objc_getClass("NSObject"),
                                                             "WpfDocumentPickerDelegate", UIntPtr.Zero);

                    class_addMethod(s_delegateClass, Sel("documentPicker:didPickDocumentsAtURLs:"),
                        (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, IntPtr, void>)&DidPickImp, "v@:@@");
                    class_addMethod(s_delegateClass, Sel("documentPickerWasCancelled:"),
                        (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, void>)&WasCancelledImp, "v@:@");

                    objc_registerClassPair(s_delegateClass);
                }
            }

            s_delegateInstance = Send(Send(s_delegateClass, Sel("alloc")), Sel("init"));
            return s_delegateInstance;
        }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvCdecl) })]
        private static void DidPickImp(IntPtr self, IntPtr sel, IntPtr picker, IntPtr urls)
        {
            TaskCompletionSource<string[]> pending = s_pending;
            s_pending = null;
            if (pending is null) return;

            try
            {
                nint count = SendNInt(urls, Sel("count"));
                var paths = new System.Collections.Generic.List<string>((int)count);

                for (nint i = 0; i < count; i++)
                {
                    IntPtr url = SendPtrNInt(urls, Sel("objectAtIndex:"), i);
                    string copied = CopyIntoTemp(url);
                    if (copied is not null) paths.Add(copied);
                }

                pending.TrySetResult(paths.ToArray());
            }
            catch (Exception)
            {
                pending.TrySetResult(Array.Empty<string>());
            }
        }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvCdecl) })]
        private static void WasCancelledImp(IntPtr self, IntPtr sel, IntPtr picker)
        {
            TaskCompletionSource<string[]> pending = s_pending;
            s_pending = null;
            pending?.TrySetResult(Array.Empty<string>());
        }

        /// <summary>
        ///  Copies a security-scoped document into the app's temporary directory and returns that
        ///  path. See the file header for why the copy is not optional.
        /// </summary>
        private static string CopyIntoTemp(IntPtr url)
        {
            if (url == IntPtr.Zero) return null;

            bool scoped = SendBool(url, Sel("startAccessingSecurityScopedResource"));
            try
            {
                IntPtr pathString = Send(url, Sel("path"));
                if (pathString == IntPtr.Zero) return null;

                string source = Marshal.PtrToStringUTF8(Send(pathString, Sel("UTF8String")));
                if (string.IsNullOrEmpty(source) || !System.IO.File.Exists(source)) return null;

                string directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                                                          "wpf-picked-" + Guid.NewGuid().ToString("N"));
                System.IO.Directory.CreateDirectory(directory);

                string destination = System.IO.Path.Combine(directory, System.IO.Path.GetFileName(source));
                System.IO.File.Copy(source, destination, overwrite: true);
                return destination;
            }
            catch (Exception)
            {
                return null;
            }
            finally
            {
                if (scoped) SendVoid(url, Sel("stopAccessingSecurityScopedResource"));
            }
        }

        // ---- UIKit helpers ---------------------------------------------------------------

        /// <summary>
        ///  The controller a picker can be presented from: the key window's root.
        /// </summary>
        /// <remarks>
        ///  -keyWindow on UIApplication is deprecated and returns nil in a multi-scene app, which is
        ///  every iPadOS app, so the connected scenes are walked instead. The head's own window is
        ///  the one that answers isKeyWindow.
        /// </remarks>
        private static IntPtr RootViewController()
        {
            IntPtr application = Send(objc_getClass("UIApplication"), Sel("sharedApplication"));
            if (application == IntPtr.Zero) return IntPtr.Zero;

            IntPtr scenes = Send(application, Sel("connectedScenes"));
            IntPtr enumerator = Send(scenes, Sel("objectEnumerator"));

            for (IntPtr scene = Send(enumerator, Sel("nextObject"));
                 scene != IntPtr.Zero;
                 scene = Send(enumerator, Sel("nextObject")))
            {
                if (!SendBoolPtr(scene, Sel("isKindOfClass:"), objc_getClass("UIWindowScene"))) continue;

                IntPtr windows = Send(scene, Sel("windows"));
                nint count = SendNInt(windows, Sel("count"));

                for (nint i = 0; i < count; i++)
                {
                    IntPtr window = SendPtrNInt(windows, Sel("objectAtIndex:"), i);
                    if (!SendBool(window, Sel("isKeyWindow"))) continue;

                    IntPtr root = Send(window, Sel("rootViewController"));
                    if (root != IntPtr.Zero) return root;
                }
            }

            return IntPtr.Zero;
        }

        /// <summary>
        ///  Builds the UTType array the open picker filters on. A null or empty list means any file.
        /// </summary>
        private static IntPtr ContentTypeArray(string[] contentTypes, bool directory)
        {
            IntPtr utType = objc_getClass("UTType");

            if (directory)
            {
                IntPtr folder = utType == IntPtr.Zero ? IntPtr.Zero : Send(utType, Sel("folderType"));
                return folder == IntPtr.Zero
                    ? Send(objc_getClass("NSArray"), Sel("array"))
                    : Send(objc_getClass("NSArray"), Sel("arrayWithObject:"), folder);
            }

            if (contentTypes is null || contentTypes.Length == 0 || utType == IntPtr.Zero)
            {
                IntPtr any = utType == IntPtr.Zero ? IntPtr.Zero : Send(utType, Sel("itemType"));
                return any == IntPtr.Zero
                    ? Send(objc_getClass("NSArray"), Sel("array"))
                    : Send(objc_getClass("NSArray"), Sel("arrayWithObject:"), any);
            }

            IntPtr array = Send(Send(objc_getClass("NSMutableArray"), Sel("alloc")), Sel("init"));
            foreach (string identifier in contentTypes)
            {
                IntPtr type = Send(utType, Sel("typeWithIdentifier:"), NSStr(identifier));
                if (type != IntPtr.Zero) SendVoidPtr(array, Sel("addObject:"), type);
            }

            return SendNInt(array, Sel("count")) == 0
                ? ContentTypeArray(null, false)
                : array;
        }

        // ---- Objective-C runtime ----------------------------------------------------

        private const string ObjC = "/usr/lib/libobjc.A.dylib";

        private static IntPtr Sel(string name) => sel_registerName(name);

        private static IntPtr NSStr(string s) =>
            SendPtrUtf8(objc_getClass("NSString"), Sel("stringWithUTF8String:"), s ?? string.Empty);

        [DllImport(ObjC)] private static extern IntPtr objc_getClass(string name);
        [DllImport(ObjC)] private static extern IntPtr sel_registerName(string name);
        [DllImport(ObjC)] private static extern IntPtr objc_allocateClassPair(IntPtr superclass, string name, UIntPtr extraBytes);
        [DllImport(ObjC)] private static extern void objc_registerClassPair(IntPtr cls);
        [DllImport(ObjC)] [return: MarshalAs(UnmanagedType.I1)] private static extern bool class_addMethod(IntPtr cls, IntPtr sel, IntPtr imp, string types);

        [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr Send(IntPtr receiver, IntPtr selector);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr Send(IntPtr receiver, IntPtr selector, IntPtr arg);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr Send(IntPtr receiver, IntPtr selector, IntPtr arg1, byte arg2);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern void SendVoid(IntPtr receiver, IntPtr selector);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern void SendVoidPtr(IntPtr receiver, IntPtr selector, IntPtr arg);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern void SendVoidBool(IntPtr receiver, IntPtr selector, [MarshalAs(UnmanagedType.I1)] bool arg);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern void SendPresent(IntPtr receiver, IntPtr selector, IntPtr controller, byte animated, IntPtr completion);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern nint SendNInt(IntPtr receiver, IntPtr selector);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr SendPtrNInt(IntPtr receiver, IntPtr selector, nint arg);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] [return: MarshalAs(UnmanagedType.I1)] private static extern bool SendBool(IntPtr receiver, IntPtr selector);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] [return: MarshalAs(UnmanagedType.I1)] private static extern bool SendBoolPtr(IntPtr receiver, IntPtr selector, IntPtr arg);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr SendPtrUtf8(IntPtr receiver, IntPtr selector, [MarshalAs(UnmanagedType.LPUTF8Str)] string arg);
    }
}
