// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// Transforms of the application's, registered in the process (MFTRegisterLocal, MFTRegisterLocalByCLSID), as
// Windows keeps them (MFLocalTransformProbe, LocalTransform.windows.txt):
//
//   - a name must be given (E_POINTER), a class factory need not be: one of none lists, and activates to
//     REGDB_E_CLASSNOTREG;
//   - MFTEnumEx lists them before the system's, in the order they were registered, whenever its flags are 0 or have
//     MFT_ENUM_FLAG_LOCALMFT; their activates hold MFT_PROCESS_LOCAL (1), MF_TRANSFORM_FLAGS (SYNCMFT), the category,
//     and of a CLSID (or of no factory: GUID_NULL) MFT_TRANSFORM_CLSID -- not the name;
//   - activated, a factory is asked for IUnknown, once (the same object again); ShutdownObject answers E_NOTIMPL of
//     a factory's, S_OK of a CLSID's;
//   - MFTUnregisterLocal of a factory not registered, or MFTUnregisterLocalByCLSID of a CLSID not, gives
//     HRESULT_FROM_WIN32(ERROR_NOT_FOUND); MFTUnregisterLocal(NULL) takes every one away.
//
// A CLSID is made of the shim's own transforms of it (there is no COM server of the application's off Windows).
//

using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Wpf.Interop.WebGpu.Direct3D;

namespace Microsoft.Wpf.Interop.WebGpu.MediaFoundation
{
    internal static unsafe class LocalTransforms
    {
        public const uint MFT_ENUM_FLAG_LOCALMFT = 0x10;
        public static readonly Guid MFT_PROCESS_LOCAL_Attribute = new("543186e4-4649-4e65-b588-4aa352aff379");
        public static readonly Guid MF_TRANSFORM_FLAGS_Attribute = new("9359bb7e-6275-46c4-a025-1c01e45f1a86");
        public static readonly Guid MF_TRANSFORM_CATEGORY_Attribute = new("ceabba49-506d-4757-a6ff-66c184987e4e");
        private const int REGDB_E_CLASSNOTREG = unchecked((int)0x80040154), NOT_FOUND = unchecked((int)0x80070490);
        private static readonly Guid IID_IUnknown = new("00000000-0000-0000-C000-000000000046");

        private sealed class Entry
        {
            public IntPtr Factory;                 // AddRef'd; zero of a CLSID's, or of none given
            public Guid? Clsid;
            public Guid Category;
            public MFT_REGISTER_TYPE_INFO[] In = Array.Empty<MFT_REGISTER_TYPE_INFO>(), Out = Array.Empty<MFT_REGISTER_TYPE_INFO>();
        }

        private static readonly List<Entry> s_entries = new();

        private static MFT_REGISTER_TYPE_INFO[] Types(uint n, MFT_REGISTER_TYPE_INFO* t)
        {
            var r = new MFT_REGISTER_TYPE_INFO[t == null ? 0 : n];
            for (int i = 0; i < r.Length; i++) r[i] = t[i];
            return r;
        }

        public static int Register(IntPtr factory, Guid? clsid, Guid* category, char* name, uint inputs, MFT_REGISTER_TYPE_INFO* inTypes, uint outputs, MFT_REGISTER_TYPE_INFO* outTypes)
        {
            if (category == null || name == null) return MF.E_POINTER;
            if (factory != IntPtr.Zero) Com.AddRef(factory);
            lock (s_entries) s_entries.Add(new Entry { Factory = factory, Clsid = clsid, Category = *category, In = Types(inputs, inTypes), Out = Types(outputs, outTypes) });
            return MF.S_OK;
        }

        public static int Unregister(IntPtr factory)
        {
            List<Entry> gone;
            lock (s_entries)
            {
                gone = s_entries.Where(e => factory == IntPtr.Zero || e.Clsid == null && e.Factory == factory).ToList();
                foreach (var e in gone) s_entries.Remove(e);
            }
            foreach (var e in gone) Com.Release(e.Factory);
            return factory == IntPtr.Zero || gone.Count > 0 ? MF.S_OK : NOT_FOUND;
        }

        public static int UnregisterClsid(Guid clsid)
        {
            int n;
            lock (s_entries) n = s_entries.RemoveAll(e => e.Clsid == clsid);
            return n > 0 ? MF.S_OK : NOT_FOUND;
        }

        private static bool Matches(MFT_REGISTER_TYPE_INFO[] registered, MFT_REGISTER_TYPE_INFO* asked)
        {
            if (asked == null || registered.Length == 0) return true;
            return registered.Any(r => (asked->guidMajorType == Guid.Empty || r.guidMajorType == asked->guidMajorType)
                                     && (asked->guidSubtype == Guid.Empty || r.guidSubtype == asked->guidSubtype));
        }

        /// <summary>The activates of the transforms registered of the category and types, in the order of their registration.</summary>
        public static List<MFActivate> Find(Guid category, MFT_REGISTER_TYPE_INFO* input, MFT_REGISTER_TYPE_INFO* output)
        {
            List<Entry> of;
            lock (s_entries) of = s_entries.Where(e => e.Category == category && Matches(e.In, input) && Matches(e.Out, output)).ToList();
            var r = new List<MFActivate>();
            foreach (var e in of)
            {
                IntPtr factory = e.Factory;
                if (factory != IntPtr.Zero) Com.AddRef(factory);
                Guid? clsid = e.Clsid;
                var a = clsid is { } c
                    ? new MFActivate(() => Create(c), MF.S_OK)
                    : new MFActivate(() => factory == IntPtr.Zero ? (REGDB_E_CLASSNOTREG, IntPtr.Zero) : FromFactory(factory), MF.E_NOTIMPL, () => Com.Release(factory));
                a.Attributes.Set(MFT_PROCESS_LOCAL_Attribute, MFValue.Of(1u));
                a.Attributes.Set(MF_TRANSFORM_FLAGS_Attribute, MFValue.Of(1u));
                a.Attributes.Set(MF_TRANSFORM_CATEGORY_Attribute, e.Category);
                if (clsid != null || factory == IntPtr.Zero) a.Attributes.Set(MF.MFT_TRANSFORM_CLSID_Attribute, clsid ?? Guid.Empty);
                r.Add(a);
            }
            return r;
        }

        /// <summary>IClassFactory::CreateInstance of IUnknown, as Windows' local activate asks it.</summary>
        private static (int, IntPtr) FromFactory(IntPtr factory)
        {
            var iid = IID_IUnknown; IntPtr unk;
            int hr = ((delegate* unmanaged<IntPtr, IntPtr, Guid*, IntPtr*, int>)Com.Fn(factory, 3))(factory, IntPtr.Zero, &iid, &unk);
            return hr < 0 ? (hr, IntPtr.Zero) : (hr, unk);
        }

        /// <summary>The shim's own transform of a CLSID: the H.264 and AAC encoders, the decoders, the classes of MFClasses.</summary>
        private static (int, IntPtr) Create(Guid clsid)
        {
            ComObject? t = clsid == MFH264Encoder.Clsid ? new MFH264Encoder() : clsid == MFAacEncoder.Clsid ? new MFAacEncoder() : null;
            if (t == null && MFVideoDecoder.Registered.FirstOrDefault(i => i.Clsid == clsid) is { } v) t = new MFVideoDecoder(v);
            if (t == null && MFAudioDecoder.Registered.FirstOrDefault(i => i.Clsid == clsid) is { } au) t = new MFAudioDecoder(au);
            if (t == null)
            {
                // a class of Media Foundation's an application makes by CLSID (the video processor)
                var c = clsid; var iid = new Guid("00000000-0000-0000-C000-000000000046"); IntPtr unk;
                int hr = MFClasses.Create(&c, &iid, &unk);
                return hr < 0 ? (hr, IntPtr.Zero) : (hr, unk);
            }
            return (MF.S_OK, t.Pointer);
        }

        /// <summary>The first transform registered of the category and types that activates, as an IMFTransform (AddRef'd); zero where none.</summary>
        public static IntPtr First(Guid category, Guid major, Guid input, Guid output)
        {
            var i = new MFT_REGISTER_TYPE_INFO { guidMajorType = major, guidSubtype = input };
            var o = new MFT_REGISTER_TYPE_INFO { guidMajorType = major, guidSubtype = output };
            foreach (var a in Find(category, input == Guid.Empty ? null : &i, output == Guid.Empty ? null : &o))
            {
                try
                {
                    var iid = MF.IID_IMFTransform; IntPtr t;
                    if (((delegate* unmanaged<IntPtr, Guid*, IntPtr*, int>)Com.Fn(a.Pointer, 33))(a.Pointer, &iid, &t) >= 0) return t;
                }
                finally { a.Release(); }
            }
            return IntPtr.Zero;
        }
    }
}
