// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// Media Foundation off Windows, the shim's (Documentation/media-foundation-shim.md): IMFAttributes,
// which media types, samples, activation objects and transforms all are -- a store of keys to typed
// values (UINT32, UINT64, double, GUID, string, blob, IUnknown), in the order they were set, as
// PROPVARIANTs at the ABI. Objects are the Direct3D shim's COM objects: a vtable per interface, the
// attributes' 30 methods at slots 3-32 of each that derives from IMFAttributes.
//

using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using Microsoft.Wpf.Interop.WebGpu.Direct3D;

namespace Microsoft.Wpf.Interop.WebGpu.MediaFoundation
{
    internal static class MF
    {
        public const int S_OK = 0, S_FALSE = 1;
        public const int E_NOTIMPL = unchecked((int)0x80004001), E_NOINTERFACE = unchecked((int)0x80004002), E_POINTER = unchecked((int)0x80004003);
        public const int E_FAIL = unchecked((int)0x80004005), E_INVALIDARG = unchecked((int)0x80070057), E_OUTOFMEMORY = unchecked((int)0x8007000E);
        public const int MF_E_ATTRIBUTENOTFOUND = unchecked((int)0xC00D36E6), MF_E_INVALIDTYPE = unchecked((int)0xC00D36B4);
        public const int MF_E_BUFFERTOOSMALL = unchecked((int)0xC00D36B1), MF_E_INVALIDINDEX = unchecked((int)0xC00D36BF);
        public const int MF_E_INVALIDMEDIATYPE = unchecked((int)0xC00D36B4), MF_E_NOTACCEPTING = unchecked((int)0xC00D36B5);
        public const int MF_E_NO_MORE_TYPES = unchecked((int)0xC00D36B9), MF_E_INVALIDSTREAMNUMBER = unchecked((int)0xC00D36B3);
        public const int MF_E_TRANSFORM_TYPE_NOT_SET = unchecked((int)0xC00D6D60), MF_E_TRANSFORM_STREAM_CHANGE = unchecked((int)0xC00D6D61);
        public const int MF_E_TRANSFORM_NEED_MORE_INPUT = unchecked((int)0xC00D6D72), MF_E_UNSUPPORTED_REPRESENTATION = unchecked((int)0xC00D36B9);
        public const int MF_E_NOT_INITIALIZED = unchecked((int)0xC00D36B6), MF_E_UNSUPPORTED_D3D_TYPE = unchecked((int)0xC00D6D76);
        public const int MF_E_SHUTDOWN = unchecked((int)0xC00D3E85), MF_E_NOSAMPLETIMESTAMP = unchecked((int)0xC00D36BB), MF_E_NO_SAMPLE_DURATION = unchecked((int)0xC00D36BA);

        // VARTYPE, as MF_ATTRIBUTE_TYPE names them
        public const ushort VT_EMPTY = 0, VT_R8 = 5, VT_UNKNOWN = 13, VT_UI4 = 19, VT_UI8 = 21, VT_LPWSTR = 31, VT_CLSID = 72, VT_BLOB = 0x1011;

        public static readonly Guid IID_IMFAttributes = new("2cd2d921-c447-44a7-a13c-4adabfc247e3");
        public static readonly Guid IID_IMFMediaType = new("44ae0fa8-ea31-4109-8d2e-4cae4997c555");
        public static readonly Guid IID_IMFSample = new("c40a00f2-b93a-4d80-ae8c-5a1c634f58e4");
        public static readonly Guid IID_IMFMediaBuffer = new("045fa593-8799-42b8-bc8d-8968c6453507");
        public static readonly Guid IID_IMF2DBuffer = new("7dc9d5f9-9ed9-44ec-9bbf-0600bb589fbb");
        public static readonly Guid IID_IMF2DBuffer2 = new("33ae5ea6-4316-436f-8ddd-d73d22f829ec");
        public static readonly Guid IID_IMFDXGIBuffer = new("e7174cfa-1c9e-48b1-8866-626226bfc258");
        public static readonly Guid IID_IMFActivate = new("7fee9e9a-4a89-47a6-899c-b6a53a70fb67");
        public static readonly Guid IID_IMFTransform = new("bf94c121-5b05-4e6f-8000-ba598961414d");
        public static readonly Guid IID_IMFDXGIDeviceManager = new("eb533d5d-2db6-40f8-97a9-494692014f07");
        public static readonly Guid IID_ICodecAPI = new("901db4c7-31ce-41a2-85dc-8fa0bf41b8da");
        public static readonly Guid IID_IMFShutdown = new("97ec2ea4-0e42-4937-97ac-9d6d328824e1");

        public static readonly Guid MF_MT_MAJOR_TYPE = new("48eba18e-f8c9-4687-bf11-0a74c9f96a8f"), MF_MT_SUBTYPE = new("f7e34c9a-42e8-4714-b74b-cb29d72c35e5");
        public static readonly Guid MF_MT_FRAME_SIZE = new("1652c33d-d6b2-4012-b834-72030849a37d"), MF_MT_FRAME_RATE = new("c459a2e8-3d2c-4e44-b132-fee5156c7bb0");
        public static readonly Guid MF_MT_PIXEL_ASPECT_RATIO = new("c6376a1e-8d0a-4027-be45-6d9a0ad39bb6"), MF_MT_INTERLACE_MODE = new("e2724bb8-e676-4806-b4b2-a8d6efb44ccd");
        public static readonly Guid MF_MT_DEFAULT_STRIDE = new("644b4e48-1e02-4516-b0eb-c01ca9d49ac6"), MF_MT_ALL_SAMPLES_INDEPENDENT = new("c9173739-5e56-461c-b713-46fb995cb95f");
        public static readonly Guid MF_MT_FIXED_SIZE_SAMPLES = new("b8ebefaf-b718-4e04-b0a9-116775e3321b"), MF_MT_SAMPLE_SIZE = new("dad3ab78-1990-408b-bce2-eba673dacc10");
        public static readonly Guid MF_MT_COMPRESSED = new("3afd0cee-18f2-4ba5-a110-8bea502e1f92");
        public static readonly Guid MF_MT_MINIMUM_DISPLAY_APERTURE = new("d7388766-18fe-48c6-a177-ee894867c8c4"), MF_MT_GEOMETRIC_APERTURE = new("66758743-7e5f-400d-980a-aa8596c85696");
        public static readonly Guid MF_MT_VIDEO_NOMINAL_RANGE = new("c21b8ee5-b956-4071-8daf-325edf5cab11");
        public static readonly Guid MF_MT_MPEG_SEQUENCE_HEADER = new("3c036de7-3ad0-4c9e-9216-ee6d6ac21cb3"), MF_MT_USER_DATA = new("b6bc765f-4c3b-40a4-bd51-2535b66fe09d");
        public static readonly Guid MF_SA_D3D11_AWARE = new("206b4fc8-fcf9-4c51-afe3-9764369e33a0"), MF_SA_D3D_AWARE = new("eaa35c29-775e-488e-9b61-b3283e49583b");
        public static readonly Guid MF_SA_D3D11_BINDFLAGS = new("eacf97ad-065c-4408-bee3-fdcbfd128be2"), MF_SA_MINIMUM_OUTPUT_SAMPLE_COUNT = new("851745d5-c3d6-476d-9527-498ef2d10d18");
        public static readonly Guid MF_LOW_LATENCY = new("9c27891a-ed7a-40e1-88e8-b22727a024ee"), MF_TRANSFORM_ASYNC = new("f81a699a-649a-497d-8c73-29f8fed6ad7a");
        public static readonly Guid MFT_FRIENDLY_NAME_Attribute = new("314ffbae-5b41-4c95-9c19-4e7d586face3"), MFT_TRANSFORM_CLSID_Attribute = new("6821c42b-65a4-4e82-99bc-9a88205ecd0c");
        public static readonly Guid MFT_INPUT_TYPES_Attributes = new("4276c9b1-759d-4bf3-9cd0-0d723d138f96"), MFT_OUTPUT_TYPES_Attributes = new("8eae8cf3-a44f-4306-ba5c-bf5dda242818");
        public static readonly Guid MFT_ENUM_HARDWARE_URL_Attribute = new("2fb866ac-b078-4942-ab6c-003d05cda674"), MFT_SUPPORT_DYNAMIC_FORMAT_CHANGE = new("53476a11-3f13-49fb-ac42-ee2733c96741");
        public static readonly Guid MFT_DECODER_EXPOSE_OUTPUT_TYPES_IN_NATIVE_ORDER = new("ef80833f-f8fa-44d9-80d8-41ed6232670c");
        public static readonly Guid MFT_CATEGORY_VIDEO_DECODER = new("d6c02d4b-6833-45b4-971a-05a4b04bab91");
        public static readonly Guid MFSampleExtension_CleanPoint = new("9cdf01d8-a0f0-43ba-b077-eaa06cbd728a"), MFSampleExtension_Discontinuity = new("9cdf01d9-a0f0-43ba-b077-eaa06cbd728a");
        public static readonly Guid MFMediaType_Video = new("73646976-0000-0010-8000-00aa00389b71");
        public static readonly Guid MFVideoFormat_NV12 = new("3231564e-0000-0010-8000-00aa00389b71"), MFVideoFormat_H264 = new("34363248-0000-0010-8000-00aa00389b71");
        public static readonly Guid MFVideoFormat_H264_ES = new("3f40f4f0-5622-4ff8-b6d8-a17a584bee5e"), MFVideoFormat_HEVC = new("43564548-0000-0010-8000-00aa00389b71");
        public static readonly Guid MFVideoFormat_IYUV = new("56555949-0000-0010-8000-00aa00389b71"), MFVideoFormat_YV12 = new("32315659-0000-0010-8000-00aa00389b71");
        public static readonly Guid MFVideoFormat_I420 = new("30323449-0000-0010-8000-00aa00389b71"), MFVideoFormat_YUY2 = new("32595559-0000-0010-8000-00aa00389b71");

        /// <summary>A PROPVARIANT's size: 16 bytes with 4-byte pointers, 24 with 8-byte ones (its BLOB is a count and a pointer).</summary>
        public static int PropVariantSize => IntPtr.Size == 8 ? 24 : 16;
        public static int BlobPointerOffset => IntPtr.Size == 8 ? 16 : 12;

        public static ulong Pack(uint hi, uint lo) => (ulong)hi << 32 | lo;
        public static (uint Hi, uint Lo) Unpack(ulong v) => ((uint)(v >> 32), (uint)v);
    }

    /// <summary>One attribute's value.</summary>
    internal sealed class MFValue
    {
        public ushort Type;
        public ulong U;
        public double D;
        public Guid G;
        public string? S;
        public byte[]? B;
        public IntPtr Unknown;          // AddRef'd while held

        public static MFValue Of(uint v) => new() { Type = MF.VT_UI4, U = v };
        public static MFValue Of(ulong v) => new() { Type = MF.VT_UI8, U = v };
        public static MFValue Of(Guid v) => new() { Type = MF.VT_CLSID, G = v };
        public static MFValue Of(string v) => new() { Type = MF.VT_LPWSTR, S = v };
        public static MFValue Of(byte[] v) => new() { Type = MF.VT_BLOB, B = v };

        public bool SameAs(MFValue o) => Type == o.Type && Type switch
        {
            MF.VT_UI4 or MF.VT_UI8 => U == o.U,
            MF.VT_R8 => D.Equals(o.D),
            MF.VT_CLSID => G == o.G,
            MF.VT_LPWSTR => S == o.S,
            MF.VT_BLOB => B!.AsSpan().SequenceEqual(o.B),
            MF.VT_UNKNOWN => Unknown == o.Unknown,
            _ => true,
        };

        public unsafe void Release() { if (Unknown != IntPtr.Zero) ((delegate* unmanaged<IntPtr, uint>)(*(IntPtr**)Unknown)[2])(Unknown); Unknown = IntPtr.Zero; }
    }

    /// <summary>The attributes of an object: keys in the order they were first set.</summary>
    internal sealed unsafe class MFAttributeStore
    {
        private readonly List<Guid> _order = new();
        private readonly Dictionary<Guid, MFValue> _items = new();
        public readonly object Lock = new();

        public int Count { get { lock (Lock) return _order.Count; } }

        public MFValue? Get(in Guid k) { lock (Lock) return _items.TryGetValue(k, out var v) ? v : null; }

        public void Set(in Guid k, MFValue v)
        {
            lock (Lock)
            {
                if (_items.TryGetValue(k, out var old)) old.Release();
                else _order.Add(k);
                _items[k] = v;
            }
        }

        public bool Delete(in Guid k)
        {
            lock (Lock)
            {
                if (!_items.Remove(k, out var old)) return false;
                old.Release();
                _order.Remove(k);
                return true;
            }
        }

        public void Clear()
        {
            lock (Lock)
            {
                foreach (var v in _items.Values) v.Release();
                _items.Clear();
                _order.Clear();
            }
        }

        public (Guid Key, MFValue Value)? At(int i) { lock (Lock) return i < _order.Count ? (_order[i], _items[_order[i]]) : null; }

        public List<(Guid, MFValue)> All() { lock (Lock) return _order.Select(k => (k, _items[k])).ToList(); }

        public uint U32(in Guid k, uint fallback = 0) => Get(k) is { Type: MF.VT_UI4 } v ? (uint)v.U : fallback;
        public ulong U64(in Guid k, ulong fallback = 0) => Get(k) is { Type: MF.VT_UI8 } v ? v.U : fallback;
        public Guid Guid(in Guid k) => Get(k) is { Type: MF.VT_CLSID } v ? v.G : System.Guid.Empty;
        public void Set(in Guid k, uint v) => Set(k, MFValue.Of(v));
        public void Set(in Guid k, ulong v) => Set(k, MFValue.Of(v));
        public void Set(in Guid k, Guid v) => Set(k, MFValue.Of(v));

        // ---- PROPVARIANT ----------------------------------------------------------------------------

        public static void Write(MFValue v, byte* pv)
        {
            new Span<byte>(pv, MF.PropVariantSize).Clear();
            *(ushort*)pv = v.Type;
            switch (v.Type)
            {
                case MF.VT_UI4: *(uint*)(pv + 8) = (uint)v.U; break;
                case MF.VT_UI8: *(ulong*)(pv + 8) = v.U; break;
                case MF.VT_R8: *(double*)(pv + 8) = v.D; break;
                case MF.VT_CLSID:
                {
                    var g = (Guid*)Marshal.AllocCoTaskMem(16);
                    *g = v.G;
                    *(IntPtr*)(pv + 8) = (IntPtr)g;
                    break;
                }
                case MF.VT_LPWSTR: *(IntPtr*)(pv + 8) = Marshal.StringToCoTaskMemUni(v.S); break;
                case MF.VT_BLOB:
                {
                    var p = Marshal.AllocCoTaskMem(Math.Max(1, v.B!.Length));
                    Marshal.Copy(v.B, 0, p, v.B.Length);
                    *(uint*)(pv + 8) = (uint)v.B.Length;
                    *(IntPtr*)(pv + MF.BlobPointerOffset) = p;
                    break;
                }
                case MF.VT_UNKNOWN:
                    if (v.Unknown != IntPtr.Zero) ((delegate* unmanaged<IntPtr, uint>)(*(IntPtr**)v.Unknown)[1])(v.Unknown);
                    *(IntPtr*)(pv + 8) = v.Unknown;
                    break;
            }
        }

        public static MFValue? Read(byte* pv)
        {
            ushort vt = *(ushort*)pv;
            switch (vt)
            {
                case MF.VT_UI4: return MFValue.Of(*(uint*)(pv + 8));
                case MF.VT_UI8: return MFValue.Of(*(ulong*)(pv + 8));
                case MF.VT_R8: return new MFValue { Type = MF.VT_R8, D = *(double*)(pv + 8) };
                case MF.VT_CLSID: { var g = *(Guid**)(pv + 8); return g == null ? null : MFValue.Of(*g); }
                case MF.VT_LPWSTR: return MFValue.Of(Marshal.PtrToStringUni(*(IntPtr*)(pv + 8)) ?? "");
                case MF.VT_BLOB:
                {
                    uint n = *(uint*)(pv + 8);
                    var b = new byte[n];
                    if (n > 0) Marshal.Copy(*(IntPtr*)(pv + MF.BlobPointerOffset), b, 0, (int)n);
                    return MFValue.Of(b);
                }
                case MF.VT_UNKNOWN:
                {
                    var u = *(IntPtr*)(pv + 8);
                    if (u != IntPtr.Zero) ((delegate* unmanaged<IntPtr, uint>)(*(IntPtr**)u)[1])(u);
                    return new MFValue { Type = MF.VT_UNKNOWN, Unknown = u };
                }
            }
            return null;
        }
    }

    /// <summary>A COM object whose interfaces include IMFAttributes (slots 3-32 of their vtables).</summary>
    internal abstract unsafe class MFAttributesObject : ComObject
    {
        public readonly MFAttributeStore Attributes = new();

        protected MFAttributesObject(params Vtable[] vts) : base(vts) { }

        protected override void OnFinalRelease() => Attributes.Clear();

        private static MFAttributeStore? S(IntPtr self) => (From(self) as MFAttributesObject)?.Attributes;

        /// <summary>The 30 IMFAttributes methods at slots 3-32.</summary>
        public static void FillAttributes(Vtable vt)
        {
            vt.Set(3, (delegate* unmanaged<IntPtr, Guid*, byte*, int>)&GetItem);
            vt.Set(4, (delegate* unmanaged<IntPtr, Guid*, uint*, int>)&GetItemType);
            vt.Set(5, (delegate* unmanaged<IntPtr, Guid*, byte*, int*, int>)&CompareItem);
            vt.Set(6, (delegate* unmanaged<IntPtr, IntPtr, uint, int*, int>)&Compare);
            vt.Set(7, (delegate* unmanaged<IntPtr, Guid*, uint*, int>)&GetUINT32);
            vt.Set(8, (delegate* unmanaged<IntPtr, Guid*, ulong*, int>)&GetUINT64);
            vt.Set(9, (delegate* unmanaged<IntPtr, Guid*, double*, int>)&GetDouble);
            vt.Set(10, (delegate* unmanaged<IntPtr, Guid*, Guid*, int>)&GetGUID);
            vt.Set(11, (delegate* unmanaged<IntPtr, Guid*, uint*, int>)&GetStringLength);
            vt.Set(12, (delegate* unmanaged<IntPtr, Guid*, char*, uint, uint*, int>)&GetString);
            vt.Set(13, (delegate* unmanaged<IntPtr, Guid*, IntPtr*, uint*, int>)&GetAllocatedString);
            vt.Set(14, (delegate* unmanaged<IntPtr, Guid*, uint*, int>)&GetBlobSize);
            vt.Set(15, (delegate* unmanaged<IntPtr, Guid*, byte*, uint, uint*, int>)&GetBlob);
            vt.Set(16, (delegate* unmanaged<IntPtr, Guid*, IntPtr*, uint*, int>)&GetAllocatedBlob);
            vt.Set(17, (delegate* unmanaged<IntPtr, Guid*, Guid*, IntPtr*, int>)&GetUnknown);
            vt.Set(18, (delegate* unmanaged<IntPtr, Guid*, byte*, int>)&SetItem);
            vt.Set(19, (delegate* unmanaged<IntPtr, Guid*, int>)&DeleteItem);
            vt.Set(20, (delegate* unmanaged<IntPtr, int>)&DeleteAllItems);
            vt.Set(21, (delegate* unmanaged<IntPtr, Guid*, uint, int>)&SetUINT32);
            vt.Set(22, (delegate* unmanaged<IntPtr, Guid*, ulong, int>)&SetUINT64);
            vt.Set(23, (delegate* unmanaged<IntPtr, Guid*, double, int>)&SetDouble);
            vt.Set(24, (delegate* unmanaged<IntPtr, Guid*, Guid*, int>)&SetGUID);
            vt.Set(25, (delegate* unmanaged<IntPtr, Guid*, char*, int>)&SetString);
            vt.Set(26, (delegate* unmanaged<IntPtr, Guid*, byte*, uint, int>)&SetBlob);
            vt.Set(27, (delegate* unmanaged<IntPtr, Guid*, IntPtr, int>)&SetUnknown);
            vt.Set(28, (delegate* unmanaged<IntPtr, int>)&LockStore);
            vt.Set(29, (delegate* unmanaged<IntPtr, int>)&UnlockStore);
            vt.Set(30, (delegate* unmanaged<IntPtr, uint*, int>)&GetCount);
            vt.Set(31, (delegate* unmanaged<IntPtr, uint, Guid*, byte*, int>)&GetItemByIndex);
            vt.Set(32, (delegate* unmanaged<IntPtr, IntPtr, int>)&CopyAllItems);
        }

        [UnmanagedCallersOnly]
        private static int GetItem(IntPtr self, Guid* k, byte* pv)
        {
            if (S(self) is not { } s || k == null) return MF.E_POINTER;
            var v = s.Get(*k);
            if (v == null) return MF.MF_E_ATTRIBUTENOTFOUND;
            if (pv != null) MFAttributeStore.Write(v, pv);
            return MF.S_OK;
        }

        [UnmanagedCallersOnly]
        private static int GetItemType(IntPtr self, Guid* k, uint* t)
        {
            if (S(self) is not { } s || k == null || t == null) return MF.E_POINTER;
            var v = s.Get(*k);
            if (v == null) return MF.MF_E_ATTRIBUTENOTFOUND;
            *t = v.Type;
            return MF.S_OK;
        }

        [UnmanagedCallersOnly]
        private static int CompareItem(IntPtr self, Guid* k, byte* pv, int* result)
        {
            if (S(self) is not { } s || k == null || pv == null || result == null) return MF.E_POINTER;
            var mine = s.Get(*k);
            var theirs = MFAttributeStore.Read(pv);
            *result = mine != null && theirs != null && mine.SameAs(theirs) ? 1 : 0;
            theirs?.Release();
            return MF.S_OK;
        }

        /// <summary>MF_ATTRIBUTES_MATCH_TYPE: 0 ours in theirs, 1 theirs in ours, 2 all items, 3 intersection, 4 smaller.</summary>
        [UnmanagedCallersOnly]
        private static int Compare(IntPtr self, IntPtr theirs, uint match, int* result)
        {
            if (S(self) is not { } s || result == null) return MF.E_POINTER;
            var other = (From(theirs) as MFAttributesObject)?.Attributes;
            if (other == null) return MF.E_NOTIMPL;
            *result = Matches(s, other, match) ? 1 : 0;
            return MF.S_OK;
        }

        internal static bool Matches(MFAttributeStore a, MFAttributeStore b, uint match)
        {
            bool In(MFAttributeStore x, MFAttributeStore y) => x.All().All(kv => y.Get(kv.Item1) is { } v && v.SameAs(kv.Item2));
            switch (match)
            {
                case 0: return In(a, b);
                case 1: return In(b, a);
                case 2: return a.Count == b.Count && In(a, b);
                case 3: return a.All().All(kv => b.Get(kv.Item1) is not { } v || v.SameAs(kv.Item2));
                default: return a.Count <= b.Count ? In(a, b) : In(b, a);
            }
        }

        private static int Typed(IntPtr self, Guid* k, ushort vt, out MFValue? v)
        {
            v = null;
            if (S(self) is not { } s || k == null) return MF.E_POINTER;
            v = s.Get(*k);
            if (v == null) return MF.MF_E_ATTRIBUTENOTFOUND;
            if (v.Type != vt) return MF.MF_E_INVALIDTYPE;
            return MF.S_OK;
        }

        [UnmanagedCallersOnly] private static int GetUINT32(IntPtr self, Guid* k, uint* r) { int hr = Typed(self, k, MF.VT_UI4, out var v); if (hr == 0 && r != null) *r = (uint)v!.U; return hr; }
        [UnmanagedCallersOnly] private static int GetUINT64(IntPtr self, Guid* k, ulong* r) { int hr = Typed(self, k, MF.VT_UI8, out var v); if (hr == 0 && r != null) *r = v!.U; return hr; }
        [UnmanagedCallersOnly] private static int GetDouble(IntPtr self, Guid* k, double* r) { int hr = Typed(self, k, MF.VT_R8, out var v); if (hr == 0 && r != null) *r = v!.D; return hr; }
        [UnmanagedCallersOnly] private static int GetGUID(IntPtr self, Guid* k, Guid* r) { int hr = Typed(self, k, MF.VT_CLSID, out var v); if (hr == 0 && r != null) *r = v!.G; return hr; }
        [UnmanagedCallersOnly] private static int GetStringLength(IntPtr self, Guid* k, uint* r) { int hr = Typed(self, k, MF.VT_LPWSTR, out var v); if (hr == 0 && r != null) *r = (uint)v!.S!.Length; return hr; }

        [UnmanagedCallersOnly]
        private static int GetString(IntPtr self, Guid* k, char* buf, uint size, uint* length)
        {
            int hr = Typed(self, k, MF.VT_LPWSTR, out var v);
            if (hr != 0) return hr;
            string str = v!.S!;
            if (length != null) *length = (uint)str.Length;
            if (buf == null || size < str.Length + 1) return MF.MF_E_BUFFERTOOSMALL;
            str.AsSpan().CopyTo(new Span<char>(buf, str.Length));
            buf[str.Length] = '\0';
            return MF.S_OK;
        }

        [UnmanagedCallersOnly]
        private static int GetAllocatedString(IntPtr self, Guid* k, IntPtr* p, uint* length)
        {
            int hr = Typed(self, k, MF.VT_LPWSTR, out var v);
            if (hr != 0) return hr;
            if (p != null) *p = Marshal.StringToCoTaskMemUni(v!.S);
            if (length != null) *length = (uint)v!.S!.Length;
            return MF.S_OK;
        }

        [UnmanagedCallersOnly] private static int GetBlobSize(IntPtr self, Guid* k, uint* r) { int hr = Typed(self, k, MF.VT_BLOB, out var v); if (hr == 0 && r != null) *r = (uint)v!.B!.Length; return hr; }

        [UnmanagedCallersOnly]
        private static int GetBlob(IntPtr self, Guid* k, byte* buf, uint size, uint* length)
        {
            int hr = Typed(self, k, MF.VT_BLOB, out var v);
            if (hr != 0) return hr;
            var b = v!.B!;
            if (length != null) *length = (uint)b.Length;
            if (size < b.Length) return MF.MF_E_BUFFERTOOSMALL;
            if (buf != null) b.CopyTo(new Span<byte>(buf, b.Length));
            return MF.S_OK;
        }

        [UnmanagedCallersOnly]
        private static int GetAllocatedBlob(IntPtr self, Guid* k, IntPtr* p, uint* length)
        {
            int hr = Typed(self, k, MF.VT_BLOB, out var v);
            if (hr != 0) return hr;
            var b = v!.B!;
            var mem = Marshal.AllocCoTaskMem(Math.Max(1, b.Length));
            Marshal.Copy(b, 0, mem, b.Length);
            if (p != null) *p = mem; else Marshal.FreeCoTaskMem(mem);
            if (length != null) *length = (uint)b.Length;
            return MF.S_OK;
        }

        [UnmanagedCallersOnly]
        private static int GetUnknown(IntPtr self, Guid* k, Guid* riid, IntPtr* ppv)
        {
            int hr = Typed(self, k, MF.VT_UNKNOWN, out var v);
            if (hr != 0) return hr;
            if (ppv == null || riid == null) return MF.E_POINTER;
            if (v!.Unknown == IntPtr.Zero) { *ppv = IntPtr.Zero; return MF.E_NOINTERFACE; }
            return ((delegate* unmanaged<IntPtr, Guid*, IntPtr*, int>)(*(IntPtr**)v.Unknown)[0])(v.Unknown, riid, ppv);
        }

        [UnmanagedCallersOnly]
        private static int SetItem(IntPtr self, Guid* k, byte* pv)
        {
            if (S(self) is not { } s || k == null || pv == null) return MF.E_POINTER;
            var v = MFAttributeStore.Read(pv);
            if (v == null) return MF.MF_E_INVALIDTYPE;
            s.Set(*k, v);
            return MF.S_OK;
        }

        [UnmanagedCallersOnly] private static int DeleteItem(IntPtr self, Guid* k) { if (S(self) is { } s && k != null) s.Delete(*k); return MF.S_OK; }
        [UnmanagedCallersOnly] private static int DeleteAllItems(IntPtr self) { S(self)?.Clear(); return MF.S_OK; }
        [UnmanagedCallersOnly] private static int SetUINT32(IntPtr self, Guid* k, uint v) { if (S(self) is not { } s || k == null) return MF.E_POINTER; s.Set(*k, v); return MF.S_OK; }
        [UnmanagedCallersOnly] private static int SetUINT64(IntPtr self, Guid* k, ulong v) { if (S(self) is not { } s || k == null) return MF.E_POINTER; s.Set(*k, v); return MF.S_OK; }
        [UnmanagedCallersOnly] private static int SetDouble(IntPtr self, Guid* k, double v) { if (S(self) is not { } s || k == null) return MF.E_POINTER; s.Set(*k, new MFValue { Type = MF.VT_R8, D = v }); return MF.S_OK; }
        [UnmanagedCallersOnly] private static int SetGUID(IntPtr self, Guid* k, Guid* v) { if (S(self) is not { } s || k == null || v == null) return MF.E_POINTER; s.Set(*k, *v); return MF.S_OK; }
        [UnmanagedCallersOnly] private static int SetString(IntPtr self, Guid* k, char* v) { if (S(self) is not { } s || k == null || v == null) return MF.E_POINTER; s.Set(*k, MFValue.Of(new string(v))); return MF.S_OK; }

        [UnmanagedCallersOnly]
        private static int SetBlob(IntPtr self, Guid* k, byte* buf, uint size)
        {
            if (S(self) is not { } s || k == null || (buf == null && size > 0)) return MF.E_POINTER;
            s.Set(*k, MFValue.Of(new ReadOnlySpan<byte>(buf, (int)size).ToArray()));
            return MF.S_OK;
        }

        [UnmanagedCallersOnly]
        private static int SetUnknown(IntPtr self, Guid* k, IntPtr u)
        {
            if (S(self) is not { } s || k == null) return MF.E_POINTER;
            if (u != IntPtr.Zero) ((delegate* unmanaged<IntPtr, uint>)(*(IntPtr**)u)[1])(u);
            s.Set(*k, new MFValue { Type = MF.VT_UNKNOWN, Unknown = u });
            return MF.S_OK;
        }

        [UnmanagedCallersOnly] private static int LockStore(IntPtr self) { if (S(self) is { } s) System.Threading.Monitor.Enter(s.Lock); return MF.S_OK; }
        [UnmanagedCallersOnly] private static int UnlockStore(IntPtr self) { if (S(self) is { } s && System.Threading.Monitor.IsEntered(s.Lock)) System.Threading.Monitor.Exit(s.Lock); return MF.S_OK; }
        [UnmanagedCallersOnly] private static int GetCount(IntPtr self, uint* n) { if (S(self) is not { } s || n == null) return MF.E_POINTER; *n = (uint)s.Count; return MF.S_OK; }

        [UnmanagedCallersOnly]
        private static int GetItemByIndex(IntPtr self, uint i, Guid* k, byte* pv)
        {
            if (S(self) is not { } s) return MF.E_POINTER;
            if (s.At((int)i) is not { } item) return MF.E_INVALIDARG;
            if (k != null) *k = item.Key;
            if (pv != null) MFAttributeStore.Write(item.Value, pv);
            return MF.S_OK;
        }

        /// <summary>Into any IMFAttributes, through its SetItem (slot 18).</summary>
        [UnmanagedCallersOnly]
        private static int CopyAllItems(IntPtr self, IntPtr dest)
        {
            if (S(self) is not { } s || dest == IntPtr.Zero) return MF.E_POINTER;
            if (From(dest) is MFAttributesObject ours)
            {
                ours.Attributes.Clear();
                foreach (var (k, v) in s.All()) ours.Attributes.Set(k, Copy(v));
                return MF.S_OK;
            }
            ((delegate* unmanaged<IntPtr, int>)(*(IntPtr**)dest)[20])(dest);
            var pv = stackalloc byte[24];
            foreach (var (k, v) in s.All())
            {
                MFAttributeStore.Write(v, pv);
                var key = k;
                ((delegate* unmanaged<IntPtr, Guid*, byte*, int>)(*(IntPtr**)dest)[18])(dest, &key, pv);
                MFExports.PropVariantClear(pv);
            }
            return MF.S_OK;
        }

        internal static MFValue Copy(MFValue v)
        {
            var c = new MFValue { Type = v.Type, U = v.U, D = v.D, G = v.G, S = v.S, B = v.B?.ToArray(), Unknown = v.Unknown };
            if (c.Unknown != IntPtr.Zero) ((delegate* unmanaged<IntPtr, uint>)(*(IntPtr**)c.Unknown)[1])(c.Unknown);
            return c;
        }
    }

    /// <summary>IMFAttributes alone (MFCreateAttributes).</summary>
    internal sealed unsafe class MFAttributeBag : MFAttributesObject
    {
        private static readonly Vtable s_vt = Build();
        private static Vtable Build() { var vt = new Vtable("IMFAttributes", 33, MF.IID_IMFAttributes); FillAttributes(vt); return vt; }
        public MFAttributeBag() : base(s_vt) { }
    }
}
