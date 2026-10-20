// The Windows Runtime's own plumbing (combase: HSTRINGs, activation factories), which every app that
// uses a WinRT API through CsWinRT calls first, and the one runtime class an app here has been found to
// construct: Windows.Devices.Geolocation.Geolocator (GraphWorX's map layer makes one whenever it loads).
//
// Every Windows since 8 has both, so an app never expects them to fail. A runtime class with no
// implementation here is "not registered" (REGDB_E_CLASSNOTREG), as on a Windows that does not have
// it. The Geolocator is a Windows machine's whose location access the app was not granted: it exists,
// takes its settings, reports NotInitialized, refuses a position with E_ACCESSDENIED and never raises
// PositionChanged.

using System.Runtime.InteropServices;

namespace WpfMac.Win32;

public static unsafe partial class Exports
{
    private const int REGDB_E_CLASSNOTREG = unchecked((int)0x80040154);
    private const int E_NOINTERFACE = unchecked((int)0x80004002);
    private const int E_POINTER = unchecked((int)0x80004003);
    private const int E_ACCESSDENIED = unchecked((int)0x80070005);
    private const int E_OUTOFMEMORY = unchecked((int)0x8007000E);

    // ---- HSTRING ------------------------------------------------------------------------------
    //
    // { int kind; uint length; char* buffer; } -- kind 1: a reference string in the caller's
    // HSTRING_HEADER (24 bytes on 64-bit, 20 on 32), buffer the caller's; kind 2: allocated here, the
    // characters right after the header, freed by WindowsDeleteString.

    private const int HStringReference = 1, HStringAllocated = 2;

    private static nint NewHString(char* chars, uint length)
    {
        byte* h = (byte*)NativeMemory.Alloc((nuint)(16 + 2 * (length + 1)));
        if (h == null) return 0;
        char* buffer = (char*)(h + 16);
        if (length > 0) new ReadOnlySpan<char>(chars, (int)length).CopyTo(new Span<char>(buffer, (int)length));
        buffer[length] = '\0';
        *(int*)h = HStringAllocated;
        *(uint*)(h + 4) = length;
        *(char**)(h + 8) = buffer;
        return (nint)h;
    }

    [Win32Export("WindowsCreateString")]
    public static int WindowsCreateString(char* source, uint length, nint* result)
    {
        if (result == null) return E_INVALIDARG;
        if (source == null && length != 0) return E_POINTER;
        if (length == 0) { *result = 0; return S_OK; }           // the empty string is NULL
        nint h = NewHString(source, length);
        if (h == 0) return E_OUTOFMEMORY;
        *result = h;
        return S_OK;
    }

    [Win32Export("WindowsCreateStringReference")]
    public static int WindowsCreateStringReference(char* source, uint length, byte* header, nint* result)
    {
        if (result == null || header == null) return E_INVALIDARG;
        if (source == null && length != 0) return E_POINTER;
        if (length == 0) { *result = 0; return S_OK; }
        if (source[length] != '\0') return E_INVALIDARG;         // a reference string must be NUL-terminated
        *(int*)header = HStringReference;
        *(uint*)(header + 4) = length;
        *(char**)(header + 8) = source;
        *result = (nint)header;
        return S_OK;
    }

    [Win32Export("WindowsDeleteString")]
    public static int WindowsDeleteString(nint h)
    {
        if (h != 0 && *(int*)h == HStringAllocated) NativeMemory.Free((void*)h);
        return S_OK;
    }

    [Win32Export("WindowsDuplicateString")]
    public static int WindowsDuplicateString(nint h, nint* result)
    {
        if (result == null) return E_INVALIDARG;
        if (h == 0) { *result = 0; return S_OK; }
        nint copy = NewHString(*(char**)((byte*)h + 8), *(uint*)((byte*)h + 4));
        if (copy == 0) return E_OUTOFMEMORY;
        *result = copy;
        return S_OK;
    }

    [Win32Export("WindowsGetStringRawBuffer")]
    public static char* WindowsGetStringRawBuffer(nint h, uint* length)
    {
        if (h == 0)
        {
            if (length != null) *length = 0;
            return EmptyHStringBuffer();
        }
        if (length != null) *length = *(uint*)((byte*)h + 4);
        return *(char**)((byte*)h + 8);
    }

    private static char* s_emptyBuffer;
    private static char* EmptyHStringBuffer() => s_emptyBuffer != null ? s_emptyBuffer : (s_emptyBuffer = (char*)NativeMemory.AllocZeroed(2));

    [Win32Export("WindowsGetStringLen")] public static uint WindowsGetStringLen(nint h) => h == 0 ? 0 : *(uint*)((byte*)h + 4);
    [Win32Export("WindowsIsStringEmpty")] public static int WindowsIsStringEmpty(nint h) => h == 0 || *(uint*)((byte*)h + 4) == 0 ? TRUE : FALSE;

    private static string HStringText(nint h) => h == 0 ? "" : new string(*(char**)((byte*)h + 8), 0, (int)*(uint*)((byte*)h + 4));

    // ---- activation -------------------------------------------------------------------------

    [Win32Export("RoInitialize")] public static int RoInitialize(int type) => S_OK;
    [Win32Export("RoUninitialize")] public static void RoUninitialize() { }

    [Win32Export("RoGetActivationFactory")]
    public static int RoGetActivationFactory(nint classId, Guid* iid, nint* factory)
    {
        if (factory == null) return E_POINTER;
        *factory = 0;
        string name = HStringText(classId);
        if (name != GeolocatorClass)
        {
            LogOnce("winrt:" + name, $"win32 shim: WinRT class {name} is not registered here (REGDB_E_CLASSNOTREG)");
            return REGDB_E_CLASSNOTREG;
        }
        nint f = WinRTObject.New(WinRTObject.Factory);
        int hr = WinRTObject.QueryInterface(f, iid, factory);
        WinRTObject.Release(f);
        return hr;
    }

    [Win32Export("RoActivateInstance")]
    public static int RoActivateInstance(nint classId, nint* instance)
    {
        if (instance == null) return E_POINTER;
        *instance = 0;
        string name = HStringText(classId);
        if (name != GeolocatorClass) return REGDB_E_CLASSNOTREG;
        *instance = WinRTObject.New(WinRTObject.Geolocator);
        return S_OK;
    }

    private const string GeolocatorClass = "Windows.Devices.Geolocation.Geolocator";

    /// <summary>The COM objects: { vtable, refs, kind, desired accuracy, movement threshold, report
    /// interval, next event token }, in native memory, their methods [UnmanagedCallersOnly].</summary>
    private static class WinRTObject
    {
        public const int Factory = 1, Geolocator = 2;

        private static readonly Guid IID_IUnknown = new("00000000-0000-0000-C000-000000000046");
        private static readonly Guid IID_IInspectable = new("AF86E2E0-B12D-4C6A-9C5A-D7AA65101E90");
        private static readonly Guid IID_IAgileObject = new("94EA2B94-E9CC-49E0-C0FF-EE64CA8F5B90");
        private static readonly Guid IID_IActivationFactory = new("00000035-0000-0000-C000-000000000046");
        private static readonly Guid IID_IGeolocator = new("A9C3BF62-4524-4989-8AA9-DE019D2E551F");

        [StructLayout(LayoutKind.Sequential)]
        private struct Obj
        {
            public nint* Vtbl;
            public int Refs;
            public int Kind;
            public int DesiredAccuracy;
            public double MovementThreshold;
            public uint ReportInterval;
            public long NextToken;
        }

        private static readonly nint* s_factoryVt = BuildFactory();
        private static readonly nint* s_geolocatorVt = BuildGeolocator();

        private static nint* Common(int slots)
        {
            var vt = (nint*)NativeMemory.AllocZeroed((nuint)slots, (nuint)sizeof(nint));
            vt[0] = (nint)(delegate* unmanaged<Obj*, Guid*, nint*, int>)&QueryInterfaceImpl;
            vt[1] = (nint)(delegate* unmanaged<Obj*, uint>)&AddRefImpl;
            vt[2] = (nint)(delegate* unmanaged<Obj*, uint>)&ReleaseImpl;
            vt[3] = (nint)(delegate* unmanaged<Obj*, uint*, Guid**, int>)&GetIids;
            vt[4] = (nint)(delegate* unmanaged<Obj*, nint*, int>)&GetRuntimeClassName;
            vt[5] = (nint)(delegate* unmanaged<Obj*, int*, int>)&GetTrustLevel;
            return vt;
        }

        private static nint* BuildFactory()
        {
            nint* vt = Common(7);
            vt[6] = (nint)(delegate* unmanaged<Obj*, nint*, int>)&ActivateInstance;
            return vt;
        }

        private static nint* BuildGeolocator()
        {
            nint* vt = Common(19);
            vt[6] = (nint)(delegate* unmanaged<Obj*, int*, int>)&GetDesiredAccuracy;
            vt[7] = (nint)(delegate* unmanaged<Obj*, int, int>)&SetDesiredAccuracy;
            vt[8] = (nint)(delegate* unmanaged<Obj*, double*, int>)&GetMovementThreshold;
            vt[9] = (nint)(delegate* unmanaged<Obj*, double, int>)&SetMovementThreshold;
            vt[10] = (nint)(delegate* unmanaged<Obj*, uint*, int>)&GetReportInterval;
            vt[11] = (nint)(delegate* unmanaged<Obj*, uint, int>)&SetReportInterval;
            vt[12] = (nint)(delegate* unmanaged<Obj*, int*, int>)&GetLocationStatus;
            vt[13] = (nint)(delegate* unmanaged<Obj*, nint*, int>)&GetGeopositionAsync;
            vt[14] = (nint)(delegate* unmanaged<Obj*, long, long, nint*, int>)&GetGeopositionAsyncWithAge;
            vt[15] = (nint)(delegate* unmanaged<Obj*, nint, long*, int>)&AddEvent;       // PositionChanged
            vt[16] = (nint)(delegate* unmanaged<Obj*, long, int>)&RemoveEvent;
            vt[17] = (nint)(delegate* unmanaged<Obj*, nint, long*, int>)&AddEvent;       // StatusChanged
            vt[18] = (nint)(delegate* unmanaged<Obj*, long, int>)&RemoveEvent;
            return vt;
        }

        public static nint New(int kind)
        {
            var o = (Obj*)NativeMemory.AllocZeroed((nuint)sizeof(Obj));
            o->Vtbl = kind == Factory ? s_factoryVt : s_geolocatorVt;
            o->Refs = 1;
            o->Kind = kind;
            o->ReportInterval = 0;
            o->NextToken = 1;
            return (nint)o;
        }

        public static int QueryInterface(nint o, Guid* iid, nint* result) => QueryInterfaceCore((Obj*)o, iid, result);
        public static void Release(nint o) => ReleaseCore((Obj*)o);

        [UnmanagedCallersOnly] private static int QueryInterfaceImpl(Obj* self, Guid* iid, nint* result) => QueryInterfaceCore(self, iid, result);

        private static int QueryInterfaceCore(Obj* self, Guid* iid, nint* result)
        {
            if (result == null) return E_POINTER;
            *result = 0;
            Guid g = *iid;
            bool ok = g == IID_IUnknown || g == IID_IInspectable || g == IID_IAgileObject
                || (self->Kind == Factory && g == IID_IActivationFactory)
                || (self->Kind == Geolocator && g == IID_IGeolocator);
            if (!ok) return E_NOINTERFACE;
            Interlocked.Increment(ref self->Refs);
            *result = (nint)self;
            return S_OK;
        }

        [UnmanagedCallersOnly] private static uint AddRefImpl(Obj* self) => (uint)Interlocked.Increment(ref self->Refs);

        [UnmanagedCallersOnly] private static uint ReleaseImpl(Obj* self) => ReleaseCore(self);

        private static uint ReleaseCore(Obj* self)
        {
            int refs = Interlocked.Decrement(ref self->Refs);
            if (refs == 0) NativeMemory.Free(self);
            return (uint)refs;
        }

        [UnmanagedCallersOnly]
        private static int GetIids(Obj* self, uint* count, Guid** iids)
        {
            if (count == null || iids == null) return E_POINTER;
            *iids = (Guid*)Marshal.AllocCoTaskMem(sizeof(Guid));
            **iids = self->Kind == Factory ? IID_IActivationFactory : IID_IGeolocator;
            *count = 1;
            return S_OK;
        }

        [UnmanagedCallersOnly]
        private static int GetRuntimeClassName(Obj* self, nint* name)
        {
            if (name == null) return E_POINTER;
            if (self->Kind == Factory) { *name = 0; return S_OK; }
            fixed (char* p = GeolocatorClass) *name = NewHString(p, (uint)GeolocatorClass.Length);
            return S_OK;
        }

        [UnmanagedCallersOnly] private static int GetTrustLevel(Obj* self, int* level) { if (level != null) *level = 0; return S_OK; }   // BaseTrust

        [UnmanagedCallersOnly]
        private static int ActivateInstance(Obj* self, nint* instance)
        {
            if (instance == null) return E_POINTER;
            *instance = New(Geolocator);
            return S_OK;
        }

        [UnmanagedCallersOnly] private static int GetDesiredAccuracy(Obj* self, int* v) { *v = self->DesiredAccuracy; return S_OK; }
        [UnmanagedCallersOnly] private static int SetDesiredAccuracy(Obj* self, int v) { self->DesiredAccuracy = v; return S_OK; }
        [UnmanagedCallersOnly] private static int GetMovementThreshold(Obj* self, double* v) { *v = self->MovementThreshold; return S_OK; }
        [UnmanagedCallersOnly] private static int SetMovementThreshold(Obj* self, double v) { self->MovementThreshold = v; return S_OK; }
        [UnmanagedCallersOnly] private static int GetReportInterval(Obj* self, uint* v) { *v = self->ReportInterval; return S_OK; }
        [UnmanagedCallersOnly] private static int SetReportInterval(Obj* self, uint v) { self->ReportInterval = v; return S_OK; }
        [UnmanagedCallersOnly] private static int GetLocationStatus(Obj* self, int* v) { *v = 4; return S_OK; }   // PositionStatus.NotInitialized

        [UnmanagedCallersOnly]
        private static int GetGeopositionAsync(Obj* self, nint* operation)
        {
            if (operation != null) *operation = 0;
            LogOnce("winrt:geoposition", "win32 shim: Geolocator.GetGeopositionAsync: no location access here (E_ACCESSDENIED)");
            return E_ACCESSDENIED;
        }

        [UnmanagedCallersOnly]
        private static int GetGeopositionAsyncWithAge(Obj* self, long maximumAge, long timeout, nint* operation)
        {
            if (operation != null) *operation = 0;
            return E_ACCESSDENIED;
        }

        // The handler is not kept: no position, and no status change, ever comes.
        [UnmanagedCallersOnly]
        private static int AddEvent(Obj* self, nint handler, long* token)
        {
            if (token == null) return E_POINTER;
            *token = Interlocked.Increment(ref self->NextToken);
            return S_OK;
        }

        [UnmanagedCallersOnly] private static int RemoveEvent(Obj* self, long token) => S_OK;
    }
}
