// The Windows Runtime's own plumbing (combase: HSTRINGs, activation factories), which every app that
// uses a WinRT API through CsWinRT calls first, and the runtime classes an app here has been found to
// use: Windows.Devices.Geolocation.Geolocator (app's map layer makes one whenever it loads) and
// Windows.Networking.Proximity.ProximityDevice (an AR tag's page looks for an NFC reader).
//
// Every Windows since 8 has them all, so an app never expects them to fail. A runtime class with no
// implementation here is "not registered" (REGDB_E_CLASSNOTREG), as on a Windows that does not have
// it. The Geolocator is a Windows machine's whose location access the app was not granted: it exists,
// takes its settings, reports NotInitialized, refuses a position with E_ACCESSDENIED and never raises
// PositionChanged. ProximityDevice is a Windows machine's without an NFC reader: GetDefault gives
// null, FromId finds nothing.

using System.Runtime.InteropServices;

namespace WpfWebGpu.Win32;

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

    // ---- error information ------------------------------------------------------------------
    //
    // combase's, which CsWinRT reaches through GetProcAddress (not a P/Invoke) when it first throws or
    // checks an HRESULT, and fails if one is missing. The thread's restricted error info is the one
    // SetRestrictedErrorInfo left, taken (and cleared) by GetRestrictedErrorInfo. No language exception
    // is originated: there is no error reporting here to carry it, so RoOriginateLanguageException
    // stores nothing (FALSE), and an unhandled error is only logged.

    [ThreadStatic] private static nint t_restrictedErrorInfo;

    private static uint ComAddRef(nint unknown) => ((delegate* unmanaged<nint, uint>)(*(nint**)unknown)[1])(unknown);
    private static uint ComRelease(nint unknown) => ((delegate* unmanaged<nint, uint>)(*(nint**)unknown)[2])(unknown);

    [UnmanagedCallersOnly]
    private static int GetRestrictedErrorInfoEntry(nint* info)
    {
        if (info == null) return E_POINTER;
        *info = t_restrictedErrorInfo;
        t_restrictedErrorInfo = 0;
        return *info == 0 ? S_FALSE : S_OK;
    }

    [UnmanagedCallersOnly]
    private static int SetRestrictedErrorInfoEntry(nint info)
    {
        if (info != 0) ComAddRef(info);
        nint old = t_restrictedErrorInfo;
        t_restrictedErrorInfo = info;
        if (old != 0) ComRelease(old);
        return S_OK;
    }

    [UnmanagedCallersOnly]
    private static int RoOriginateLanguageExceptionEntry(int error, nint message, nint languageException) => FALSE;

    [UnmanagedCallersOnly]
    private static int RoReportUnhandledErrorEntry(nint info)
    {
        LogOnce("winrt:unhandled", "win32 shim: RoReportUnhandledError: an unhandled WinRT error was reported");
        return S_OK;
    }

    /// <summary>The native entry point of a Windows DLL's function that is called through a pointer
    /// from GetProcAddress, or 0: the WinRT error functions (combase, and the api sets forwarding to it).</summary>
    internal static nint NativeEntry(string dll, string name)
    {
        if (!dll.Equals("combase", StringComparison.OrdinalIgnoreCase) && !dll.StartsWith("api-ms-win-core-winrt-error-", StringComparison.OrdinalIgnoreCase))
            return 0;
        return name switch
        {
            "GetRestrictedErrorInfo" => (nint)(delegate* unmanaged<nint*, int>)&GetRestrictedErrorInfoEntry,
            "SetRestrictedErrorInfo" => (nint)(delegate* unmanaged<nint, int>)&SetRestrictedErrorInfoEntry,
            "RoOriginateLanguageException" => (nint)(delegate* unmanaged<int, nint, nint, int>)&RoOriginateLanguageExceptionEntry,
            "RoReportUnhandledError" => (nint)(delegate* unmanaged<nint, int>)&RoReportUnhandledErrorEntry,
            _ => 0,
        };
    }

    // ---- activation -------------------------------------------------------------------------

    [Win32Export("RoInitialize")] public static int RoInitialize(int type) => S_OK;
    [Win32Export("RoUninitialize")] public static void RoUninitialize() { }

    [Win32Export("RoGetActivationFactory")]
    public static int RoGetActivationFactory(nint classId, Guid* iid, nint* factory)
    {
        if (factory == null) return E_POINTER;
        *factory = 0;
        string name = HStringText(classId);
        int kind = name switch
        {
            GeolocatorClass => WinRTObject.Factory,
            ProximityDeviceClass => WinRTObject.ProximityFactory,
            _ => 0,
        };
        if (kind == 0)
        {
            LogOnce("winrt:" + name, $"win32 shim: WinRT class {name} is not registered here (REGDB_E_CLASSNOTREG)");
            return REGDB_E_CLASSNOTREG;
        }
        nint f = WinRTObject.New(kind);
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
        if (name == ProximityDeviceClass) return E_NOTIMPL;            // a class with no default constructor
        if (name != GeolocatorClass) return REGDB_E_CLASSNOTREG;
        *instance = WinRTObject.New(WinRTObject.Geolocator);
        return S_OK;
    }

    private const string GeolocatorClass = "Windows.Devices.Geolocation.Geolocator";
    private const string ProximityDeviceClass = "Windows.Networking.Proximity.ProximityDevice";

    /// <summary>ProximityDevice.GetDeviceSelector's: the NFC (GUID_DEVINTERFACE_NFP) interfaces enabled.</summary>
    private const string ProximityDeviceSelector =
        "System.Devices.InterfaceClassGuid:=\"{FB3842CD-9E2A-4F83-8FCC-4B0761139AE9}\" AND System.Devices.InterfaceEnabled:=System.StructuredQueryType.Boolean#True";

    /// <summary>The COM objects: { vtable, refs, kind, desired accuracy, movement threshold, report
    /// interval, next event token, owner }, in native memory, their methods [UnmanagedCallersOnly]. A
    /// class's statics are a tear-off of its factory (their slots follow IInspectable's as
    /// IActivationFactory's do), which holds the factory as its owner and answers for it.</summary>
    private static class WinRTObject
    {
        public const int Factory = 1, Geolocator = 2, ProximityFactory = 3, ProximityStatics = 4;

        private static readonly Guid IID_IUnknown = new("00000000-0000-0000-C000-000000000046");
        private static readonly Guid IID_IInspectable = new("AF86E2E0-B12D-4C6A-9C5A-D7AA65101E90");
        private static readonly Guid IID_IAgileObject = new("94EA2B94-E9CC-49E0-C0FF-EE64CA8F5B90");
        private static readonly Guid IID_IActivationFactory = new("00000035-0000-0000-C000-000000000046");
        private static readonly Guid IID_IGeolocator = new("A9C3BF62-4524-4989-8AA9-DE019D2E551F");
        private static readonly Guid IID_IProximityDeviceStatics = new("914BA01D-F6E1-47C4-A14C-148A1903D0C6");

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
            public Obj* Owner;
        }

        private static readonly nint* s_factoryVt = BuildFactory();
        private static readonly nint* s_geolocatorVt = BuildGeolocator();
        private static readonly nint* s_proximityStaticsVt = BuildProximityStatics();

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

        private static nint* BuildProximityStatics()
        {
            nint* vt = Common(9);
            vt[6] = (nint)(delegate* unmanaged<Obj*, nint*, int>)&GetDeviceSelector;
            vt[7] = (nint)(delegate* unmanaged<Obj*, nint*, int>)&GetDefault;
            vt[8] = (nint)(delegate* unmanaged<Obj*, nint, nint*, int>)&FromId;
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
            o->Vtbl = kind switch
            {
                Geolocator => s_geolocatorVt,
                ProximityStatics => s_proximityStaticsVt,
                _ => s_factoryVt,
            };
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
            if (self->Kind == ProximityStatics)
            {
                if (g != IID_IProximityDeviceStatics) return QueryInterfaceCore(self->Owner, iid, result);
                Interlocked.Increment(ref self->Refs);
                *result = (nint)self;
                return S_OK;
            }
            if (self->Kind == ProximityFactory && g == IID_IProximityDeviceStatics)
            {
                Obj* statics = (Obj*)New(ProximityStatics);
                Interlocked.Increment(ref self->Refs);
                statics->Owner = self;
                *result = (nint)statics;
                return S_OK;
            }
            bool ok = g == IID_IUnknown || g == IID_IInspectable || g == IID_IAgileObject
                || ((self->Kind == Factory || self->Kind == ProximityFactory) && g == IID_IActivationFactory)
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
            if (refs == 0)
            {
                Obj* owner = self->Owner;
                NativeMemory.Free(self);
                if (owner != null) ReleaseCore(owner);
            }
            return (uint)refs;
        }

        [UnmanagedCallersOnly]
        private static int GetIids(Obj* self, uint* count, Guid** iids)
        {
            if (count == null || iids == null) return E_POINTER;
            *iids = (Guid*)Marshal.AllocCoTaskMem(sizeof(Guid));
            **iids = self->Kind switch
            {
                Geolocator => IID_IGeolocator,
                ProximityStatics => IID_IProximityDeviceStatics,
                _ => IID_IActivationFactory,
            };
            *count = 1;
            return S_OK;
        }

        [UnmanagedCallersOnly]
        private static int GetRuntimeClassName(Obj* self, nint* name)
        {
            if (name == null) return E_POINTER;
            if (self->Kind != Geolocator) { *name = 0; return S_OK; }
            fixed (char* p = GeolocatorClass) *name = NewHString(p, (uint)GeolocatorClass.Length);
            return S_OK;
        }

        [UnmanagedCallersOnly] private static int GetTrustLevel(Obj* self, int* level) { if (level != null) *level = 0; return S_OK; }   // BaseTrust

        [UnmanagedCallersOnly]
        private static int ActivateInstance(Obj* self, nint* instance)
        {
            if (instance == null) return E_POINTER;
            *instance = 0;
            if (self->Kind == ProximityFactory) return E_NOTIMPL;
            *instance = New(Geolocator);
            return S_OK;
        }

        [UnmanagedCallersOnly]
        private static int GetDeviceSelector(Obj* self, nint* selector)
        {
            if (selector == null) return E_POINTER;
            fixed (char* p = ProximityDeviceSelector) *selector = NewHString(p, (uint)ProximityDeviceSelector.Length);
            return *selector == 0 ? E_OUTOFMEMORY : S_OK;
        }

        // no NFC reader: no default device (null, S_OK), and no device by any id
        [UnmanagedCallersOnly]
        private static int GetDefault(Obj* self, nint* device)
        {
            if (device == null) return E_POINTER;
            *device = 0;
            return S_OK;
        }

        [UnmanagedCallersOnly]
        private static int FromId(Obj* self, nint deviceId, nint* device)
        {
            if (device == null) return E_POINTER;
            *device = 0;
            return unchecked((int)0x80070490);                       // HRESULT_FROM_WIN32(ERROR_NOT_FOUND)
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
