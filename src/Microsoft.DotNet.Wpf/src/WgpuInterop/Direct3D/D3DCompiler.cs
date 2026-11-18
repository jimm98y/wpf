// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// d3dcompiler_47's compile entry points over the shim's own HLSL compiler (Hlsl/HlslCompiler.cs):
// the D3D_SHADER_MACRO list, the application's ID3DInclude (or D3D_COMPILE_STANDARD_FILE_INCLUDE,
// files relative to the including file), and ID3DBlob for the code and the error text.
//

using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Wpf.Interop.WebGpu.Direct3D.Hlsl;

namespace Microsoft.Wpf.Interop.WebGpu.Direct3D
{
    /// <summary>ID3DBlob (ID3D10Blob): a buffer of native memory.</summary>
    internal sealed unsafe class D3DBlob : ComObject
    {
        private static readonly Guid IID_ID3D10Blob = new("8BA5FB08-5195-40e2-AC58-0D989C3A0102");
        private static readonly Vtable s_vt = Build();

        private readonly byte* _data;
        private readonly nuint _size;

        private D3DBlob(nuint size) : base(s_vt)
        {
            _size = size;
            _data = (byte*)NativeMemory.AllocZeroed(Math.Max(size, 1));
        }

        public static D3DBlob Create(ReadOnlySpan<byte> bytes)
        {
            var b = new D3DBlob((nuint)bytes.Length);
            bytes.CopyTo(new Span<byte>(b._data, bytes.Length));
            return b;
        }

        public static D3DBlob Create(nuint size) => new(size);

        /// <summary>Text as fxc's error blob holds it: ASCII, NUL-terminated.</summary>
        public static D3DBlob Text(string s)
        {
            var bytes = Encoding.UTF8.GetBytes(s);
            var b = new D3DBlob((nuint)bytes.Length + 1);
            bytes.CopyTo(new Span<byte>(b._data, bytes.Length));
            return b;
        }

        private static Vtable Build()
        {
            var vt = new Vtable("ID3DBlob", 5, IID_ID3D10Blob);
            vt.Set(3, (delegate* unmanaged<IntPtr, IntPtr>)&GetBufferPointer);
            vt.Set(4, (delegate* unmanaged<IntPtr, nuint>)&GetBufferSize);
            return vt;
        }

        [UnmanagedCallersOnly]
        private static IntPtr GetBufferPointer(IntPtr self) => From<D3DBlob>(self) is { } b ? (IntPtr)b._data : IntPtr.Zero;

        [UnmanagedCallersOnly]
        private static nuint GetBufferSize(IntPtr self) => From<D3DBlob>(self)?._size ?? 0;

        protected override void OnFinalRelease() => NativeMemory.Free(_data);
    }

    internal static unsafe class D3DCompiler
    {
        private const int E_FAIL = unchecked((int)0x80004005);
        private static readonly IntPtr StandardFileInclude = (IntPtr)1;

        public static int Compile(ReadOnlySpan<byte> src, string? name, void* defines, void* include, byte* entry, byte* target, uint flags, IntPtr* code, IntPtr* errors)
        {
            if (code != null) *code = IntPtr.Zero;
            if (errors != null) *errors = IntPtr.Zero;
            if (target == null) return HResult.E_INVALIDARG;
            string source = Encoding.UTF8.GetString(src.IndexOf((byte)0) is int z and >= 0 ? src[..z] : src);
            string file;
            fixed (byte* ps = src) file = string.IsNullOrEmpty(name) ? "Shader@0x" + ((nint)ps).ToString("X8") : name!;
            string ep = entry != null ? Marshal.PtrToStringUTF8((IntPtr)entry) ?? "main" : "main";
            string profile = Marshal.PtrToStringUTF8((IntPtr)target) ?? "";

            var macros = new List<(string, string)>();
            if (defines != null)
                for (IntPtr* m = (IntPtr*)defines; m[0] != IntPtr.Zero; m += 2)
                    macros.Add((Marshal.PtrToStringUTF8(m[0]) ?? "", m[1] == IntPtr.Zero ? "" : Marshal.PtrToStringUTF8(m[1]) ?? ""));

            var opened = new List<IntPtr>();
            var dataOf = new Dictionary<string, IntPtr>();
            Func<string, bool, string, string?>? includer = null;
            if ((IntPtr)include == StandardFileInclude)
            {
                string dir = Path.GetDirectoryName(Path.GetFullPath(file)) ?? ".";
                includer = (n, system, from) =>
                {
                    string baseDir = from == file || string.IsNullOrEmpty(from) ? dir : Path.GetDirectoryName(from) ?? dir;
                    string p = Path.Combine(baseDir, n);
                    return File.Exists(p) ? File.ReadAllText(p) : File.Exists(Path.Combine(dir, n)) ? File.ReadAllText(Path.Combine(dir, n)) : null;
                };
            }
            else if (include != null)
            {
                IntPtr* vt = *(IntPtr**)include;
                var open = (delegate* unmanaged[Stdcall]<void*, int, byte*, void*, void**, uint*, int>)vt[0];
                includer = (n, system, from) =>
                {
                    var nameBytes = Encoding.UTF8.GetBytes(n + "\0");
                    void* data = null; uint bytes = 0;
                    dataOf.TryGetValue(from, out IntPtr parent);
                    int hr;
                    fixed (byte* pn = nameBytes) hr = open(include, system ? 1 : 0, pn, (void*)parent, &data, &bytes);
                    if (hr < 0 || data == null) return null;
                    opened.Add((IntPtr)data);
                    dataOf[n] = (IntPtr)data;
                    return Encoding.UTF8.GetString(new ReadOnlySpan<byte>(data, (int)bytes).TrimEnd((byte)0));
                };
            }

            HlslCompiler.Result r;
            try
            {
                r = HlslCompiler.Compile(source, file, macros, includer, ep, profile, flags);
            }
            catch (Exception e)
            {
                D3DLog.Error("D3DCompile", e);
                r = new HlslCompiler.Result(null, $"{file}: error X3000: internal compiler error: {e.Message}\n");
            }
            finally
            {
                if (include != null && (IntPtr)include != StandardFileInclude)
                {
                    IntPtr* vt = *(IntPtr**)include;
                    var close = (delegate* unmanaged[Stdcall]<void*, void*, int>)vt[1];
                    foreach (var d in opened) close(include, (void*)d);
                }
            }

            if (r.Messages.Length > 0 && errors != null) *errors = D3DBlob.Text(r.Messages).Pointer;
            if (!r.Succeeded)
            {
                D3DLog.Once("d3dcompile:" + ep + profile, $"D3DCompile of {ep} ({profile}) failed: {r.Messages.Trim()}");
                return E_FAIL;
            }
            if (code != null) *code = D3DBlob.Create(r.Code).Pointer;
            return HResult.S_OK;
        }
    }
}
