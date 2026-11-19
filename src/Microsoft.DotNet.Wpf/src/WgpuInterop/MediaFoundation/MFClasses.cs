// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// Media Foundation's COM classes an application makes by CLSID (CoCreateInstance): what the shim's CoCreateInstance
// (WpfWebGpu.Win32) asks of this assembly for a class it has not.
//

using System;

namespace Microsoft.Wpf.Interop.WebGpu.MediaFoundation
{
    internal static unsafe class MFClasses
    {
        private const int REGDB_E_CLASSNOTREG = unchecked((int)0x80040154);

        /// <summary>What the shim's CoCreateInstance calls (WpfWebGpu.Win32, by reflection: it references not this assembly).</summary>
        public static int CreateInstance(Guid clsid, Guid iid, out nint ppv)
        {
            IntPtr p;
            int hr = Create(&clsid, &iid, &p);
            ppv = p;
            return hr;
        }

        /// <summary>An instance of the class, as its interface; REGDB_E_CLASSNOTREG of a class there is none of.</summary>
        public static int Create(Guid* clsid, Guid* iid, IntPtr* ppv)
        {
            if (ppv == null || clsid == null || iid == null) return MF.E_POINTER;
            *ppv = IntPtr.Zero;
            Direct3D.ComObject? made = *clsid == MFVideoProcessor.Clsid ? new MFVideoProcessor() : *clsid == MFColorConverter.Clsid ? new MFColorConverter() : null;
            if (made == null) return REGDB_E_CLASSNOTREG;
            int hr = Com.QueryInterface(made.Pointer, *iid, out var p);
            made.Release();
            *ppv = p;
            return hr;
        }
    }
}
