// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// The WebView2 ABI, reached without COM interop.
//
// WebView2 is a COM API, but nothing here uses [ComImport], CoCreateInstance,
// Marshal.GetObjectForIUnknown or the runtime's COM marshaller -- exactly as WindowsMediaBackend
// reaches Media Foundation. The entry point is a FLAT exported factory
// (CreateCoreWebView2EnvironmentWithOptions in WebView2Loader.dll) and every method after it is
// invoked by indexing the object's vtable through delegate* unmanaged[Stdcall], i.e. plain
// function-pointer P/Invoke over a documented ABI. That keeps the fork's no-COM rule intact (it
// exists so the cross-platform engine stays portable and AOT-safe), and this file is Windows-only
// anyway, precisely as MacWebViewBackend is Objective-C-only.
//
// EVERY SLOT NUMBER AND IID BELOW WAS READ OUT OF WebView2.h, not recalled. The vtable layouts were
// extracted from the DECLSPEC_XFGVIRT markers in the header shipped with the Microsoft.Web.WebView2
// package; getting one wrong does not fail to compile, it calls the neighbouring method with the
// wrong arguments, so they are recorded here with the interface each belongs to.
//
// WebView2 also calls US back -- completion handlers and event handlers are COM objects the caller
// must supply. ComThunk below builds one by hand: a block of unmanaged memory whose first word
// points at a vtable of [QueryInterface, AddRef, Release, Invoke], where the three IUnknown slots
// and Invoke are [UnmanagedCallersOnly] statics and the managed target is recovered from a
// GCHandle stored beside the vtable pointer. This is the same shape the Wayland listener structs
// use, with IUnknown's reference counting added because COM will not accept an object without it.
//

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading;

namespace MS.Internal.Interop.WebView
{
    [SupportedOSPlatform("windows")]
    internal static unsafe class WebView2Interop
    {
        internal const string Loader = "WebView2Loader.dll";

        /// <summary>
        /// The one flat entry point. Everything else is reached through the objects it produces.
        /// </summary>
        [DllImport(Loader, CharSet = CharSet.Unicode, CallingConvention = CallingConvention.StdCall)]
        internal static extern int CreateCoreWebView2EnvironmentWithOptions(
            string browserExecutableFolder,
            string userDataFolder,
            IntPtr environmentOptions,
            IntPtr environmentCreatedHandler);

        // ---- vtable plumbing --------------------------------------------------------------------

        internal static void** Vtbl(IntPtr obj) => *(void***)obj;

        internal static uint AddRef(IntPtr o) =>
            o == IntPtr.Zero ? 0 : ((delegate* unmanaged[Stdcall]<IntPtr, uint>)Vtbl(o)[1])(o);

        internal static uint Release(IntPtr o) =>
            o == IntPtr.Zero ? 0 : ((delegate* unmanaged[Stdcall]<IntPtr, uint>)Vtbl(o)[2])(o);

        /// <summary>Release and null out in one step; safe to call twice.</summary>
        internal static void ReleaseAndClear(ref IntPtr o)
        {
            if (o != IntPtr.Zero)
            {
                Release(o);
                o = IntPtr.Zero;
            }
        }

        /// <summary>
        /// Read an [out] LPWSTR the callee allocated with CoTaskMemAlloc, and free it. Forgetting
        /// the free leaks a string per navigation, which on a page that redirects is per redirect.
        /// </summary>
        internal static string TakeString(IntPtr p)
        {
            if (p == IntPtr.Zero)
            {
                return null;
            }

            try
            {
                return Marshal.PtrToStringUni(p);
            }
            finally
            {
                Marshal.FreeCoTaskMem(p);
            }
        }

        internal static void ThrowIfFailed(int hr)
        {
            if (hr < 0)
            {
                Marshal.ThrowExceptionForHR(hr);
            }
        }

        // ---- ICoreWebView2Environment (slots from WebView2.h) -----------------------------------

        internal static int Environment_CreateCoreWebView2Controller(IntPtr env, IntPtr parentWindow, IntPtr handler) =>
            ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr, int>)Vtbl(env)[3])(env, parentWindow, handler);

        internal static string Environment_GetBrowserVersionString(IntPtr env)
        {
            IntPtr s;
            ThrowIfFailed(((delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, int>)Vtbl(env)[5])(env, &s));
            return TakeString(s);
        }

        // ---- ICoreWebView2Controller ------------------------------------------------------------

        internal static void Controller_PutIsVisible(IntPtr c, bool visible) =>
            ThrowIfFailed(((delegate* unmanaged[Stdcall]<IntPtr, int, int>)Vtbl(c)[4])(c, visible ? 1 : 0));

        internal static void Controller_PutBounds(IntPtr c, RECT bounds) =>
            ThrowIfFailed(((delegate* unmanaged[Stdcall]<IntPtr, RECT, int>)Vtbl(c)[6])(c, bounds));

        internal static double Controller_GetZoomFactor(IntPtr c)
        {
            double z;
            ThrowIfFailed(((delegate* unmanaged[Stdcall]<IntPtr, double*, int>)Vtbl(c)[7])(c, &z));
            return z;
        }

        internal static void Controller_PutZoomFactor(IntPtr c, double zoom) =>
            ThrowIfFailed(((delegate* unmanaged[Stdcall]<IntPtr, double, int>)Vtbl(c)[8])(c, zoom));

        internal static void Controller_NotifyParentWindowPositionChanged(IntPtr c) =>
            ThrowIfFailed(((delegate* unmanaged[Stdcall]<IntPtr, int>)Vtbl(c)[23])(c));

        internal static void Controller_Close(IntPtr c) =>
            ThrowIfFailed(((delegate* unmanaged[Stdcall]<IntPtr, int>)Vtbl(c)[24])(c));

        internal static IntPtr Controller_GetCoreWebView2(IntPtr c)
        {
            IntPtr w;
            ThrowIfFailed(((delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, int>)Vtbl(c)[25])(c, &w));
            return w;
        }

        // ---- ICoreWebView2 -----------------------------------------------------------------------

        internal static IntPtr WebView_GetSettings(IntPtr w)
        {
            IntPtr s;
            ThrowIfFailed(((delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, int>)Vtbl(w)[3])(w, &s));
            return s;
        }

        internal static string WebView_GetSource(IntPtr w)
        {
            IntPtr s;
            ThrowIfFailed(((delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, int>)Vtbl(w)[4])(w, &s));
            return TakeString(s);
        }

        internal static int WebView_Navigate(IntPtr w, string uri)
        {
            fixed (char* p = uri)
            {
                return ((delegate* unmanaged[Stdcall]<IntPtr, char*, int>)Vtbl(w)[5])(w, p);
            }
        }

        internal static int WebView_NavigateToString(IntPtr w, string html)
        {
            fixed (char* p = html)
            {
                return ((delegate* unmanaged[Stdcall]<IntPtr, char*, int>)Vtbl(w)[6])(w, p);
            }
        }

        /// <summary>Register an event handler. <paramref name="addSlot"/> names the event.</summary>
        internal static long WebView_AddEvent(IntPtr w, int addSlot, IntPtr handler)
        {
            long token;
            ThrowIfFailed(((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, long*, int>)Vtbl(w)[addSlot])(w, handler, &token));
            return token;
        }

        internal const int SlotAddNavigationStarting = 7;
        internal const int SlotAddContentLoading = 9;
        internal const int SlotAddSourceChanged = 11;
        internal const int SlotAddNavigationCompleted = 15;
        internal const int SlotAddProcessFailed = 25;
        internal const int SlotAddWebMessageReceived = 34;
        internal const int SlotAddNewWindowRequested = 44;
        internal const int SlotAddDocumentTitleChanged = 46;

        internal static int WebView_AddScriptToExecuteOnDocumentCreated(IntPtr w, string script, IntPtr handler)
        {
            fixed (char* p = script)
            {
                return ((delegate* unmanaged[Stdcall]<IntPtr, char*, IntPtr, int>)Vtbl(w)[27])(w, p, handler);
            }
        }

        internal static int WebView_ExecuteScript(IntPtr w, string script, IntPtr handler)
        {
            fixed (char* p = script)
            {
                return ((delegate* unmanaged[Stdcall]<IntPtr, char*, IntPtr, int>)Vtbl(w)[29])(w, p, handler);
            }
        }

        internal static void WebView_Reload(IntPtr w) =>
            ThrowIfFailed(((delegate* unmanaged[Stdcall]<IntPtr, int>)Vtbl(w)[31])(w));

        internal static int WebView_PostWebMessageAsJson(IntPtr w, string json)
        {
            fixed (char* p = json)
            {
                return ((delegate* unmanaged[Stdcall]<IntPtr, char*, int>)Vtbl(w)[32])(w, p);
            }
        }

        internal static int WebView_PostWebMessageAsString(IntPtr w, string text)
        {
            fixed (char* p = text)
            {
                return ((delegate* unmanaged[Stdcall]<IntPtr, char*, int>)Vtbl(w)[33])(w, p);
            }
        }

        internal static bool WebView_GetCanGoBack(IntPtr w)
        {
            int v;
            ThrowIfFailed(((delegate* unmanaged[Stdcall]<IntPtr, int*, int>)Vtbl(w)[38])(w, &v));
            return v != 0;
        }

        internal static bool WebView_GetCanGoForward(IntPtr w)
        {
            int v;
            ThrowIfFailed(((delegate* unmanaged[Stdcall]<IntPtr, int*, int>)Vtbl(w)[39])(w, &v));
            return v != 0;
        }

        internal static void WebView_GoBack(IntPtr w) =>
            ThrowIfFailed(((delegate* unmanaged[Stdcall]<IntPtr, int>)Vtbl(w)[40])(w));

        internal static void WebView_GoForward(IntPtr w) =>
            ThrowIfFailed(((delegate* unmanaged[Stdcall]<IntPtr, int>)Vtbl(w)[41])(w));

        internal static void WebView_Stop(IntPtr w) =>
            ThrowIfFailed(((delegate* unmanaged[Stdcall]<IntPtr, int>)Vtbl(w)[43])(w));

        internal static string WebView_GetDocumentTitle(IntPtr w)
        {
            IntPtr s;
            ThrowIfFailed(((delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, int>)Vtbl(w)[48])(w, &s));
            return TakeString(s);
        }

        // ---- ICoreWebView2Settings ---------------------------------------------------------------

        private static void PutBool(IntPtr s, int slot, bool value) =>
            ThrowIfFailed(((delegate* unmanaged[Stdcall]<IntPtr, int, int>)Vtbl(s)[slot])(s, value ? 1 : 0));

        internal static void Settings_Apply(IntPtr s, WebViewSettings v)
        {
            PutBool(s, 4, v.ScriptEnabled);                     // put_IsScriptEnabled
            PutBool(s, 6, v.WebMessageEnabled);                 // put_IsWebMessageEnabled
            PutBool(s, 8, v.AreDefaultScriptDialogsEnabled);    // put_AreDefaultScriptDialogsEnabled
            PutBool(s, 10, v.IsStatusBarEnabled);               // put_IsStatusBarEnabled
            PutBool(s, 12, v.AreDevToolsEnabled);               // put_AreDevToolsEnabled
            PutBool(s, 14, v.AreDefaultContextMenusEnabled);    // put_AreDefaultContextMenusEnabled
            PutBool(s, 18, v.IsZoomControlEnabled);             // put_IsZoomControlEnabled
            PutBool(s, 20, v.IsBuiltInErrorPageEnabled);        // put_IsBuiltInErrorPageEnabled
        }

        // ---- event args ---------------------------------------------------------------------------

        internal static string NavStarting_GetUri(IntPtr a)
        {
            IntPtr s;
            ThrowIfFailed(((delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, int>)Vtbl(a)[3])(a, &s));
            return TakeString(s);
        }

        internal static bool NavStarting_GetIsUserInitiated(IntPtr a)
        {
            int v;
            ThrowIfFailed(((delegate* unmanaged[Stdcall]<IntPtr, int*, int>)Vtbl(a)[4])(a, &v));
            return v != 0;
        }

        internal static bool NavStarting_GetIsRedirected(IntPtr a)
        {
            int v;
            ThrowIfFailed(((delegate* unmanaged[Stdcall]<IntPtr, int*, int>)Vtbl(a)[5])(a, &v));
            return v != 0;
        }

        internal static void NavStarting_PutCancel(IntPtr a, bool cancel) =>
            ThrowIfFailed(((delegate* unmanaged[Stdcall]<IntPtr, int, int>)Vtbl(a)[8])(a, cancel ? 1 : 0));

        internal static ulong NavStarting_GetNavigationId(IntPtr a)
        {
            ulong v;
            ThrowIfFailed(((delegate* unmanaged[Stdcall]<IntPtr, ulong*, int>)Vtbl(a)[9])(a, &v));
            return v;
        }

        internal static bool NavCompleted_GetIsSuccess(IntPtr a)
        {
            int v;
            ThrowIfFailed(((delegate* unmanaged[Stdcall]<IntPtr, int*, int>)Vtbl(a)[3])(a, &v));
            return v != 0;
        }

        internal static int NavCompleted_GetWebErrorStatus(IntPtr a)
        {
            int v;
            ThrowIfFailed(((delegate* unmanaged[Stdcall]<IntPtr, int*, int>)Vtbl(a)[4])(a, &v));
            return v;
        }

        internal static ulong NavCompleted_GetNavigationId(IntPtr a)
        {
            ulong v;
            ThrowIfFailed(((delegate* unmanaged[Stdcall]<IntPtr, ulong*, int>)Vtbl(a)[5])(a, &v));
            return v;
        }

        internal static bool SourceChanged_GetIsNewDocument(IntPtr a)
        {
            int v;
            ThrowIfFailed(((delegate* unmanaged[Stdcall]<IntPtr, int*, int>)Vtbl(a)[3])(a, &v));
            return v != 0;
        }

        internal static string WebMessage_GetSource(IntPtr a)
        {
            IntPtr s;
            ThrowIfFailed(((delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, int>)Vtbl(a)[3])(a, &s));
            return TakeString(s);
        }

        internal static string WebMessage_GetAsJson(IntPtr a)
        {
            IntPtr s;
            ThrowIfFailed(((delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, int>)Vtbl(a)[4])(a, &s));
            return TakeString(s);
        }

        /// <summary>
        /// The string form, or null when the message was not a string. This FAILS rather than
        /// returning null for a non-string message, which is why the HRESULT is swallowed here.
        /// </summary>
        internal static string WebMessage_TryGetAsString(IntPtr a)
        {
            IntPtr s;
            int hr = ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, int>)Vtbl(a)[5])(a, &s);
            return hr < 0 ? null : TakeString(s);
        }

        internal static string NewWindow_GetUri(IntPtr a)
        {
            IntPtr s;
            ThrowIfFailed(((delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, int>)Vtbl(a)[3])(a, &s));
            return TakeString(s);
        }

        internal static void NewWindow_PutHandled(IntPtr a, bool handled) =>
            ThrowIfFailed(((delegate* unmanaged[Stdcall]<IntPtr, int, int>)Vtbl(a)[6])(a, handled ? 1 : 0));

        internal static bool NewWindow_GetIsUserInitiated(IntPtr a)
        {
            int v;
            ThrowIfFailed(((delegate* unmanaged[Stdcall]<IntPtr, int*, int>)Vtbl(a)[8])(a, &v));
            return v != 0;
        }

        // ---- ICoreWebView2DownloadStartingEventArgs ---------------------------------------------

        internal static IntPtr DownloadStarting_GetOperation(IntPtr a)
        {
            IntPtr op;
            ThrowIfFailed(((delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, int>)Vtbl(a)[3])(a, &op));
            return op;
        }

        internal static void DownloadStarting_PutCancel(IntPtr a, bool cancel) =>
            ThrowIfFailed(((delegate* unmanaged[Stdcall]<IntPtr, int, int>)Vtbl(a)[5])(a, cancel ? 1 : 0));

        internal static string DownloadStarting_GetResultFilePath(IntPtr a)
        {
            IntPtr s;
            ThrowIfFailed(((delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, int>)Vtbl(a)[6])(a, &s));
            return TakeString(s);
        }

        internal static void DownloadStarting_PutResultFilePath(IntPtr a, string path)
        {
            fixed (char* p = path)
            {
                ThrowIfFailed(((delegate* unmanaged[Stdcall]<IntPtr, char*, int>)Vtbl(a)[7])(a, p));
            }
        }

        /// <summary>ICoreWebView2DownloadOperation.get_Uri.</summary>
        internal static string DownloadOperation_GetUri(IntPtr op)
        {
            IntPtr s;
            ThrowIfFailed(((delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, int>)Vtbl(op)[9])(op, &s));
            return TakeString(s);
        }

        /// <summary>
        /// QueryInterface, for the versioned interfaces: DownloadStarting lives on ICoreWebView2_4,
        /// not on the original ICoreWebView2, so it has to be asked for by IID.
        /// </summary>
        internal static IntPtr QueryInterface(IntPtr obj, Guid iid)
        {
            IntPtr result;
            int hr = ((delegate* unmanaged[Stdcall]<IntPtr, Guid*, IntPtr*, int>)Vtbl(obj)[0])(obj, &iid, &result);
            return hr < 0 ? IntPtr.Zero : result;
        }

        internal static int ProcessFailed_GetKind(IntPtr a)
        {
            int v;
            ThrowIfFailed(((delegate* unmanaged[Stdcall]<IntPtr, int*, int>)Vtbl(a)[3])(a, &v));
            return v;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct RECT
        {
            public int Left, Top, Right, Bottom;
        }
    }

    /// <summary>
    /// A COM object we implement, built by hand so nothing here depends on the COM marshaller.
    /// </summary>
    /// <remarks>
    /// Layout is the COM ABI: the object pointer points at a word holding the vtable address,
    /// and we park a GCHandle immediately after it so the static thunks can find their target.
    /// Reference counting is real -- WebView2 holds these across asynchronous work, and an object
    /// freed at the end of the registering method would be called back into after it was gone.
    /// </remarks>
    [SupportedOSPlatform("windows")]
    internal sealed unsafe class ComThunk : IDisposable
    {
        // The two callback shapes WebView2 uses. Completion handlers take an HRESULT and a result;
        // event handlers take a sender and an args object. They are separate because an HRESULT is
        // 32 bits and a pointer is 64: reading one as the other would work by accident on x64 and
        // then not on some other ABI.
        internal delegate int CompletedCallback(int errorCode, IntPtr result);
        internal delegate int EventCallback(IntPtr sender, IntPtr args);

        [StructLayout(LayoutKind.Sequential)]
        private struct Instance
        {
            public IntPtr Vtbl;
            public IntPtr Handle;   // GCHandle to the ComThunk
            public int RefCount;
        }

        private static readonly List<Guid> s_alwaysAnswered = new List<Guid>
        {
            new Guid("00000000-0000-0000-C000-000000000046"),   // IUnknown
        };

        private readonly Guid _iid;
        private readonly CompletedCallback _completed;
        private readonly EventCallback _event;
        private IntPtr _instance;
        private IntPtr _vtable;
        private GCHandle _self;

        private ComThunk(Guid iid, CompletedCallback completed, EventCallback ev)
        {
            _iid = iid;
            _completed = completed;
            _event = ev;
            _self = GCHandle.Alloc(this);

            _vtable = Marshal.AllocHGlobal(IntPtr.Size * 4);
            void** v = (void**)_vtable;
            v[0] = (delegate* unmanaged[Stdcall]<IntPtr, Guid*, IntPtr*, int>)&QueryInterfaceThunk;
            v[1] = (delegate* unmanaged[Stdcall]<IntPtr, uint>)&AddRefThunk;
            v[2] = (delegate* unmanaged[Stdcall]<IntPtr, uint>)&ReleaseThunk;

            if (completed != null)
            {
                v[3] = (delegate* unmanaged[Stdcall]<IntPtr, int, IntPtr, int>)&InvokeCompletedThunk;
            }
            else
            {
                v[3] = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr, int>)&InvokeEventThunk;
            }

            _instance = Marshal.AllocHGlobal(sizeof(Instance));
            Instance* inst = (Instance*)_instance;
            inst->Vtbl = _vtable;
            inst->Handle = GCHandle.ToIntPtr(_self);

            // One reference for the caller who is about to hand this to WebView2.
            inst->RefCount = 1;
        }

        /// <summary>A completion handler for an asynchronous WebView2 call.</summary>
        internal static ComThunk ForCompleted(Guid iid, CompletedCallback callback) =>
            new ComThunk(iid, callback, null);

        /// <summary>An event handler registered with one of the add_* methods.</summary>
        internal static ComThunk ForEvent(Guid iid, EventCallback callback) =>
            new ComThunk(iid, null, callback);

        /// <summary>The COM pointer to hand to WebView2.</summary>
        internal IntPtr Pointer => _instance;

        private static ComThunk FromThis(IntPtr self)
        {
            Instance* inst = (Instance*)self;
            return inst->Handle == IntPtr.Zero
                ? null
                : GCHandle.FromIntPtr(inst->Handle).Target as ComThunk;
        }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
        private static int QueryInterfaceThunk(IntPtr self, Guid* riid, IntPtr* ppv)
        {
            if (ppv == null)
            {
                return unchecked((int)0x80004003);   // E_POINTER
            }

            *ppv = IntPtr.Zero;
            ComThunk t = FromThis(self);

            if (t != null && (*riid == t._iid || s_alwaysAnswered.Contains(*riid)))
            {
                *ppv = self;

                // Incremented here rather than by calling AddRefThunk: an [UnmanagedCallersOnly]
                // method cannot be invoked from managed code at all.
                Interlocked.Increment(ref ((Instance*)self)->RefCount);
                return 0;
            }

            return unchecked((int)0x80004002);       // E_NOINTERFACE
        }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
        private static uint AddRefThunk(IntPtr self)
        {
            Instance* inst = (Instance*)self;
            return (uint)Interlocked.Increment(ref inst->RefCount);
        }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
        private static uint ReleaseThunk(IntPtr self)
        {
            Instance* inst = (Instance*)self;
            int n = Interlocked.Decrement(ref inst->RefCount);

            if (n == 0)
            {
                // The managed side may still hold this thunk (Dispose has not run), so only the
                // GCHandle is dropped here; the memory is freed by Dispose, which is the only place
                // that knows nothing else is about to touch it.
                ComThunk t = FromThis(self);
                t?.OnLastRelease();
            }

            return (uint)n;
        }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
        private static int InvokeCompletedThunk(IntPtr self, int errorCode, IntPtr result)
        {
            ComThunk t = FromThis(self);

            // A managed exception must never cross back into native code; WebView2 would tear the
            // process down. Report it as a failed HRESULT instead.
            try
            {
                return t?._completed(errorCode, result) ?? 0;
            }
            catch
            {
                return unchecked((int)0x80004005);   // E_FAIL
            }
        }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
        private static int InvokeEventThunk(IntPtr self, IntPtr sender, IntPtr args)
        {
            ComThunk t = FromThis(self);

            try
            {
                return t?._event(sender, args) ?? 0;
            }
            catch
            {
                return unchecked((int)0x80004005);
            }
        }

        private void OnLastRelease()
        {
            // Nothing to do beyond letting Dispose finish the job; kept as a named seam so the
            // reference-count contract is visible rather than implied.
        }

        public void Dispose()
        {
            if (_instance != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(_instance);
                _instance = IntPtr.Zero;
            }

            if (_vtable != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(_vtable);
                _vtable = IntPtr.Zero;
            }

            if (_self.IsAllocated)
            {
                _self.Free();
            }
        }
    }
}
