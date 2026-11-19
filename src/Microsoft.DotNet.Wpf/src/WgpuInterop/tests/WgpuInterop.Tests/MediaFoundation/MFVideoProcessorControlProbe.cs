// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// Windows' video processor's IMFVideoProcessorControl(2), applied (MF_PROBE_VPROC_CONTROL=<file> writes the report):
// a picture of 8x6 whose every pixel is its own, through the mirrors, the rotations (of the override, of the input
// type's MF_MT_VIDEO_ROTATION), the source and destination rectangles, the border color and the constriction size --
// each output whole, in hex, or the failure.
//

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Wpf.Interop.WebGpu.MediaFoundation;
using Xunit;

namespace WgpuInterop.Tests.MediaFoundation
{
    public sealed unsafe class MFVideoProcessorControlProbe
    {
        private static string Hr(int hr) => $"0x{hr:x8}";
        private static readonly Guid MAJOR = new("48eba18e-f8c9-4687-bf11-0a74c9f96a8f"), SUB = new("f7e34c9a-42e8-4714-b74b-cb29d72c35e5");
        private static readonly Guid Video = new("73646976-0000-0010-8000-00aa00389b71");
        private static readonly Guid FRAME_SIZE = new("1652c33d-d6b2-4012-b834-72030849a37d"), FRAME_RATE = new("c459a2e8-3d2c-4e44-b132-fee5156c7bb0");
        private static readonly Guid INTERLACE = new("e2724bb8-e676-4806-b4b2-a8d6efb44ccd"), PAR = new("c6376a1e-8d0a-4027-be45-6d9a0ad39bb6");
        private static readonly Guid ROTATION = new("c380465d-2271-428c-9b83-ecea3b4a85c1"), STRIDE = new("644b4e48-1e02-4516-b0eb-c01ca9d49ac6");
        private static readonly Guid IID_Control2 = new("bde633d3-e1dc-4a7f-a693-bbae399c4a20");
        private static Guid Fourcc(string s) => new((uint)(s[0] | s[1] << 8 | s[2] << 16 | s[3] << 24), 0, 0x10, 0x80, 0, 0, 0xaa, 0, 0x38, 0x9b, 0x71);
        private static readonly Guid NV12 = Fourcc("NV12"), RGB32 = new("00000016-0000-0010-8000-00aa00389b71");
        private static IntPtr Fn(IntPtr o, int slot) => MFReader.Fn(o, slot);

        private const int W = 8, H = 6;

        /// <summary>RGB32 of 8x6, top-down: blue 16 + 28 x, green 16 + 36 y, red 200 -- every pixel its own.</summary>
        private static byte[] Rgb()
        {
            var b = new byte[W * H * 4];
            for (int y = 0; y < H; y++) for (int x = 0; x < W; x++) { int o = (y * W + x) * 4; b[o] = (byte)(16 + 28 * x); b[o + 1] = (byte)(16 + 36 * y); b[o + 2] = 200; b[o + 3] = 255; }
            return b;
        }

        /// <summary>NV12 of 8x6: luma 16 + 8 (y W + x), chroma U 64 + 32 cx, V 64 + 40 cy.</summary>
        private static byte[] Nv12()
        {
            var b = new byte[W * H * 3 / 2];
            for (int y = 0; y < H; y++) for (int x = 0; x < W; x++) b[y * W + x] = (byte)(16 + 4 * (y * W + x));
            for (int cy = 0; cy < H / 2; cy++) for (int cx = 0; cx < W / 2; cx++) { b[W * H + cy * W + cx * 2] = (byte)(64 + 32 * cx); b[W * H + cy * W + cx * 2 + 1] = (byte)(64 + 40 * cy); }
            return b;
        }

        private static IntPtr Type(Guid sub, int w, int h, uint? rotation = null, bool stride = false, (uint N, uint D)? par = null)
        {
            var t = MFReader.MediaType();
            MFReader.SetGuid(t, MAJOR, Video); MFReader.SetGuid(t, SUB, sub);
            MFReader.SetU64(t, FRAME_SIZE, MF.Pack((uint)w, (uint)h)); MFReader.SetU64(t, FRAME_RATE, MF.Pack(25, 1));
            MFReader.SetU32(t, INTERLACE, 2); MFReader.SetU64(t, PAR, MF.Pack(par?.N ?? 1, par?.D ?? 1));
            if (rotation is { } r) MFReader.SetU32(t, ROTATION, r);
            if (stride) MFReader.SetU32(t, STRIDE, (uint)(sub == RGB32 ? w * 4 : w));
            return t;
        }

        private sealed class Case
        {
            public string Name = "";
            public Guid In = RGB32, Out = RGB32;
            public int OutW = W, OutH = H;
            public uint? InRotation;
            public (uint N, uint D) InPar = (1, 1), OutPar = (1, 1);
            public byte Prefill;                            // the output buffer's bytes before
            public bool ControlFirst;                       // the control's calls before the types
            public Func<IntPtr, List<string>, int>? Control; // the control's calls, logged
        }

        // IMFVideoProcessorControl(2): 3 SetBorderColor, 4 SetSourceRectangle, 5 SetDestinationRectangle, 6 SetMirror,
        // 7 SetRotation, 8 SetConstrictionSize, 9 SetRotationOverride
        private static int Border(IntPtr c, byte b, byte g, byte r, byte a) { uint argb = (uint)(b | g << 8 | r << 16 | a << 24); return ((delegate* unmanaged<IntPtr, uint*, int>)Fn(c, 3))(c, &argb); }
        private static int Rect(IntPtr c, int slot, int l, int t, int r, int b) { var rc = stackalloc int[4] { l, t, r, b }; return ((delegate* unmanaged<IntPtr, int*, int>)Fn(c, slot))(c, rc); }
        private static int Mirror(IntPtr c, int m) => ((delegate* unmanaged<IntPtr, int, int>)Fn(c, 6))(c, m);
        private static int Rotation(IntPtr c, int r) => ((delegate* unmanaged<IntPtr, int, int>)Fn(c, 7))(c, r);
        private static int Constriction(IntPtr c, int w, int h) { var s = stackalloc int[2] { w, h }; return ((delegate* unmanaged<IntPtr, int*, int>)Fn(c, 8))(c, s); }
        private static int Null(IntPtr c, int slot) => ((delegate* unmanaged<IntPtr, void*, int>)Fn(c, slot))(c, null);
        private static int Override(IntPtr c, uint r) => ((delegate* unmanaged<IntPtr, uint, int>)Fn(c, 9))(c, r);

        private static Func<IntPtr, List<string>, int> Calls(params (string, Func<IntPtr, int>)[] calls) => (c, log) =>
        {
            foreach (var (name, f) in calls) log.Add($"    {name} {Hr(f(c))}");
            return 0;
        };

        private static IEnumerable<Case> Cases()
        {
            yield return new Case { Name = "none" };
            yield return new Case { Name = "none, NV12 in", In = NV12, Out = NV12 };
            foreach (int m in new[] { 0, 1, 2, 3, 4 })
                yield return new Case { Name = $"mirror {m}", Control = Calls(($"SetMirror({m})", c => Mirror(c, m))) };
            yield return new Case { Name = "mirror 1, NV12", In = NV12, Out = NV12, Control = Calls(("SetMirror(1)", c => Mirror(c, 1))) };
            yield return new Case { Name = "mirror 2, NV12", In = NV12, Out = NV12, Control = Calls(("SetMirror(2)", c => Mirror(c, 2))) };
            yield return new Case { Name = "mirror 1 before the types", ControlFirst = true, Control = Calls(("SetMirror(1)", c => Mirror(c, 1))) };
            foreach (uint r in new uint[] { 0, 90, 180, 270, 45, 360 })
                yield return new Case { Name = $"override {r}", Control = Calls(($"SetRotationOverride({r})", c => Override(c, r))) };
            yield return new Case { Name = "override 90, out 6x8", OutW = H, OutH = W, Control = Calls(("SetRotationOverride(90)", c => Override(c, 90))) };
            yield return new Case { Name = "override 270, out 6x8", OutW = H, OutH = W, Control = Calls(("SetRotationOverride(270)", c => Override(c, 270))) };
            yield return new Case { Name = "override 90, out 6x8, NV12", In = NV12, Out = NV12, OutW = H, OutH = W, Control = Calls(("SetRotationOverride(90)", c => Override(c, 90))) };
            yield return new Case { Name = "out 6x8", OutW = H, OutH = W };
            foreach (uint r in new uint[] { 90, 180, 270 })
            {
                int ow = r == 180 ? W : H, oh = r == 180 ? H : W;
                yield return new Case { Name = $"input rotation {r}", InRotation = r, OutW = ow, OutH = oh };
                yield return new Case { Name = $"input rotation {r}, SetRotation(0)", InRotation = r, OutW = ow, OutH = oh, Control = Calls(("SetRotation(0)", c => Rotation(c, 0))) };
                yield return new Case { Name = $"input rotation {r}, SetRotation(1)", InRotation = r, OutW = ow, OutH = oh, Control = Calls(("SetRotation(1)", c => Rotation(c, 1))) };
            }
            yield return new Case { Name = "input rotation 90, override 90", InRotation = 90, OutW = W, OutH = H, Control = Calls(("SetRotationOverride(90)", c => Override(c, 90))) };
            yield return new Case { Name = "input rotation 90, out 8x6", InRotation = 90 };
            yield return new Case { Name = "SetRotation(2)", Control = Calls(("SetRotation(2)", c => Rotation(c, 2))) };
            yield return new Case { Name = "mirror 1, override 90", OutW = H, OutH = W, Control = Calls(("SetMirror(1)", c => Mirror(c, 1)), ("SetRotationOverride(90)", c => Override(c, 90))) };
            yield return new Case { Name = "mirror 2, override 90", OutW = H, OutH = W, Control = Calls(("SetMirror(2)", c => Mirror(c, 2)), ("SetRotationOverride(90)", c => Override(c, 90))) };
            yield return new Case { Name = "source 2,1,6,4, out 4x3", OutW = 4, OutH = 3, Control = Calls(("SetSourceRectangle(2,1,6,4)", c => Rect(c, 4, 2, 1, 6, 4))) };
            yield return new Case { Name = "source 2,1,6,4, NV12, out 4x3", In = NV12, Out = NV12, OutW = 4, OutH = 3, Control = Calls(("SetSourceRectangle(2,1,6,4)", c => Rect(c, 4, 2, 1, 6, 4))) };
            yield return new Case { Name = "source 2,2,6,6, NV12, out 4x4", In = NV12, Out = NV12, OutW = 4, OutH = 4, Control = Calls(("SetSourceRectangle(2,2,6,6)", c => Rect(c, 4, 2, 2, 6, 6))) };
            yield return new Case { Name = "source 0,0,4,3, out 8x6", Control = Calls(("SetSourceRectangle(0,0,4,3)", c => Rect(c, 4, 0, 0, 4, 3))) };
            yield return new Case { Name = "source 0,0,16,12", Control = Calls(("SetSourceRectangle(0,0,16,12)", c => Rect(c, 4, 0, 0, 16, 12))) };
            yield return new Case { Name = "source 4,2,2,1 (inverted)", Control = Calls(("SetSourceRectangle(4,2,2,1)", c => Rect(c, 4, 4, 2, 2, 1))) };
            yield return new Case { Name = "source 0,0,0,0", Control = Calls(("SetSourceRectangle(0,0,0,0)", c => Rect(c, 4, 0, 0, 0, 0))) };
            yield return new Case { Name = "source null", Control = Calls(("SetSourceRectangle(null)", c => Null(c, 4))) };
            yield return new Case { Name = "destination 2,1,6,4", Control = Calls(("SetDestinationRectangle(2,1,6,4)", c => Rect(c, 5, 2, 1, 6, 4))) };
            yield return new Case { Name = "destination 2,1,6,4, border", Control = Calls(("SetBorderColor(11 22 33 44)", c => Border(c, 0x11, 0x22, 0x33, 0x44)), ("SetDestinationRectangle(2,1,6,4)", c => Rect(c, 5, 2, 1, 6, 4))) };
            yield return new Case { Name = "destination 2,2,6,4, border, NV12", In = NV12, Out = NV12, Control = Calls(("SetBorderColor(11 22 33 44)", c => Border(c, 0x11, 0x22, 0x33, 0x44)), ("SetDestinationRectangle(2,2,6,4)", c => Rect(c, 5, 2, 2, 6, 4))) };
            yield return new Case { Name = "destination 2,2,6,4, border, RGB to NV12", Out = NV12, Control = Calls(("SetBorderColor(11 22 33 44)", c => Border(c, 0x11, 0x22, 0x33, 0x44)), ("SetDestinationRectangle(2,2,6,4)", c => Rect(c, 5, 2, 2, 6, 4))) };
            yield return new Case { Name = "destination 2,2,6,4, border, NV12 to RGB", In = NV12, Control = Calls(("SetBorderColor(11 22 33 44)", c => Border(c, 0x11, 0x22, 0x33, 0x44)), ("SetDestinationRectangle(2,2,6,4)", c => Rect(c, 5, 2, 2, 6, 4))) };
            yield return new Case { Name = "destination 0,0,8,6, border", Control = Calls(("SetBorderColor(11 22 33 44)", c => Border(c, 0x11, 0x22, 0x33, 0x44)), ("SetDestinationRectangle(0,0,8,6)", c => Rect(c, 5, 0, 0, 8, 6))) };
            yield return new Case { Name = "destination -2,-1,6,5", Control = Calls(("SetDestinationRectangle(-2,-1,6,5)", c => Rect(c, 5, -2, -1, 6, 5))) };
            yield return new Case { Name = "destination 4,3,12,9", Control = Calls(("SetDestinationRectangle(4,3,12,9)", c => Rect(c, 5, 4, 3, 12, 9))) };
            yield return new Case { Name = "destination 0,0,0,0", Control = Calls(("SetDestinationRectangle(0,0,0,0)", c => Rect(c, 5, 0, 0, 0, 0))) };
            yield return new Case { Name = "destination null", Control = Calls(("SetDestinationRectangle(null)", c => Null(c, 5))) };
            yield return new Case { Name = "border only", Control = Calls(("SetBorderColor(11 22 33 44)", c => Border(c, 0x11, 0x22, 0x33, 0x44))) };
            yield return new Case { Name = "border null", Control = Calls(("SetBorderColor(null)", c => Null(c, 3))) };
            yield return new Case { Name = "border, out 8x8 (letterbox?)", OutW = 8, OutH = 8, Control = Calls(("SetBorderColor(11 22 33 44)", c => Border(c, 0x11, 0x22, 0x33, 0x44))) };
            yield return new Case { Name = "source and destination 2,1,6,4", Control = Calls(("SetSourceRectangle(2,1,6,4)", c => Rect(c, 4, 2, 1, 6, 4)), ("SetDestinationRectangle(2,1,6,4)", c => Rect(c, 5, 2, 1, 6, 4))) };
            yield return new Case { Name = "source 2,1,6,4, destination 0,0,4,3, override 90, out 6x8", OutW = H, OutH = W, Control = Calls(("SetSourceRectangle(2,1,6,4)", c => Rect(c, 4, 2, 1, 6, 4)), ("SetDestinationRectangle(0,0,4,3)", c => Rect(c, 5, 0, 0, 4, 3)), ("SetRotationOverride(90)", c => Override(c, 90))) };
            yield return new Case { Name = "destination 1,1,4,3, mirror 1", Control = Calls(("SetDestinationRectangle(1,1,4,3)", c => Rect(c, 5, 1, 1, 4, 3)), ("SetMirror(1)", c => Mirror(c, 1))) };
            // the order of the crop, rotation, mirror, rectangles; what is outside the destination; the pixel aspect ratio
            yield return new Case { Name = "input rotation 90, mirror 1, out 6x8", InRotation = 90, OutW = H, OutH = W, Control = Calls(("SetMirror(1)", c => Mirror(c, 1))) };
            yield return new Case { Name = "input rotation 90, mirror 2, out 6x8", InRotation = 90, OutW = H, OutH = W, Control = Calls(("SetMirror(2)", c => Mirror(c, 2))) };
            yield return new Case { Name = "input rotation 90, source 0,0,4,3, out 6x8", InRotation = 90, OutW = H, OutH = W, Control = Calls(("SetSourceRectangle(0,0,4,3)", c => Rect(c, 4, 0, 0, 4, 3))) };
            yield return new Case { Name = "input rotation 90, source 2,1,5,5, out 6x8", InRotation = 90, OutW = H, OutH = W, Control = Calls(("SetSourceRectangle(2,1,5,5)", c => Rect(c, 4, 2, 1, 5, 5))) };
            yield return new Case { Name = "input rotation 90, destination 0,0,3,4, out 6x8", InRotation = 90, OutW = H, OutH = W, Control = Calls(("SetDestinationRectangle(0,0,3,4)", c => Rect(c, 5, 0, 0, 3, 4))) };
            yield return new Case { Name = "input rotation 90, destination 0,0,4,3, out 6x8", InRotation = 90, OutW = H, OutH = W, Control = Calls(("SetDestinationRectangle(0,0,4,3)", c => Rect(c, 5, 0, 0, 4, 3))) };
            yield return new Case { Name = "input rotation 90, NV12, out 6x8", InRotation = 90, In = NV12, Out = NV12, OutW = H, OutH = W };
            yield return new Case { Name = "mirror 1, source 0,0,4,3, out 4x3", OutW = 4, OutH = 3, Control = Calls(("SetMirror(1)", c => Mirror(c, 1)), ("SetSourceRectangle(0,0,4,3)", c => Rect(c, 4, 0, 0, 4, 3))) };
            yield return new Case { Name = "mirror 2, destination 0,0,4,3", Control = Calls(("SetMirror(2)", c => Mirror(c, 2)), ("SetDestinationRectangle(0,0,4,3)", c => Rect(c, 5, 0, 0, 4, 3))) };
            yield return new Case { Name = "destination 2,1,6,4, prefilled", Prefill = 0x5a, Control = Calls(("SetDestinationRectangle(2,1,6,4)", c => Rect(c, 5, 2, 1, 6, 4))) };
            yield return new Case { Name = "out 8x8, prefilled", Prefill = 0x5a, OutW = 8, OutH = 8 };
            yield return new Case { Name = "destination 2,2,6,4, NV12, prefilled", In = NV12, Out = NV12, Prefill = 0x5a, Control = Calls(("SetDestinationRectangle(2,2,6,4)", c => Rect(c, 5, 2, 2, 6, 4))) };
            yield return new Case { Name = "destination 2,2,6,6, NV12", In = NV12, Out = NV12, Control = Calls(("SetDestinationRectangle(2,2,6,6)", c => Rect(c, 5, 2, 2, 6, 6))) };
            yield return new Case { Name = "destination 1,1,7,4, NV12", In = NV12, Out = NV12, Control = Calls(("SetDestinationRectangle(1,1,7,4)", c => Rect(c, 5, 1, 1, 7, 4))) };
            yield return new Case { Name = "destination 1,1,5,4", Control = Calls(("SetDestinationRectangle(1,1,5,4)", c => Rect(c, 5, 1, 1, 5, 4))) };
            yield return new Case { Name = "out 8x8, NV12", In = NV12, Out = NV12, OutW = 8, OutH = 8 };
            yield return new Case { Name = "out 8x8, border, NV12", In = NV12, Out = NV12, OutW = 8, OutH = 8, Control = Calls(("SetBorderColor(11 22 33 44)", c => Border(c, 0x11, 0x22, 0x33, 0x44))) };
            yield return new Case { Name = "out 8x8, RGB to NV12", Out = NV12, OutW = 8, OutH = 8 };
            yield return new Case { Name = "out 8x8, NV12 to RGB", In = NV12, OutW = 8, OutH = 8 };
            yield return new Case { Name = "out 8x4", OutW = 8, OutH = 4 };
            yield return new Case { Name = "out 4x6", OutW = 4, OutH = 6 };
            yield return new Case { Name = "input PAR 2:1, out 8x6", InPar = (2, 1) };
            yield return new Case { Name = "input PAR 1:2, out 8x6", InPar = (1, 2) };
            yield return new Case { Name = "output PAR 2:1, out 8x6", OutPar = (2, 1) };
            yield return new Case { Name = "input PAR 4:3, out 8x8", InPar = (4, 3), OutW = 8, OutH = 8 };
            yield return new Case { Name = "source and destination 2,1,6,4, NV12", In = NV12, Out = NV12, Control = Calls(("SetSourceRectangle(2,2,6,4)", c => Rect(c, 4, 2, 2, 6, 4)), ("SetDestinationRectangle(2,2,6,4)", c => Rect(c, 5, 2, 2, 6, 4))) };
            yield return new Case { Name = "source and destination 1,1,5,4", Control = Calls(("SetSourceRectangle(1,1,5,4)", c => Rect(c, 4, 1, 1, 5, 4)), ("SetDestinationRectangle(1,1,5,4)", c => Rect(c, 5, 1, 1, 5, 4))) };
            yield return new Case { Name = "source 1,1,5,4, destination 2,1,6,4", Control = Calls(("SetSourceRectangle(1,1,5,4)", c => Rect(c, 4, 1, 1, 5, 4)), ("SetDestinationRectangle(2,1,6,4)", c => Rect(c, 5, 2, 1, 6, 4))) };
            yield return new Case { Name = "source 2,1,6,4, destination 1,1,5,4", Control = Calls(("SetSourceRectangle(2,1,6,4)", c => Rect(c, 4, 2, 1, 6, 4)), ("SetDestinationRectangle(1,1,5,4)", c => Rect(c, 5, 1, 1, 5, 4))) };
            yield return new Case { Name = "source 0,0,8,6, destination 0,0,8,6", Control = Calls(("SetSourceRectangle(0,0,8,6)", c => Rect(c, 4, 0, 0, 8, 6)), ("SetDestinationRectangle(0,0,8,6)", c => Rect(c, 5, 0, 0, 8, 6))) };
            yield return new Case { Name = "source and destination 2,1,6,4, prefilled", Prefill = 0x5a, Control = Calls(("SetSourceRectangle(2,1,6,4)", c => Rect(c, 4, 2, 1, 6, 4)), ("SetDestinationRectangle(2,1,6,4)", c => Rect(c, 5, 2, 1, 6, 4))) };
            yield return new Case { Name = "source and destination 2,1,6,4, RGB to NV12", Out = NV12, Control = Calls(("SetSourceRectangle(2,2,6,4)", c => Rect(c, 4, 2, 2, 6, 4)), ("SetDestinationRectangle(2,2,6,4)", c => Rect(c, 5, 2, 2, 6, 4))) };
            yield return new Case { Name = "source 2,1,6,4, destination 2,1,6,4, mirror 1", Control = Calls(("SetSourceRectangle(2,1,6,4)", c => Rect(c, 4, 2, 1, 6, 4)), ("SetDestinationRectangle(2,1,6,4)", c => Rect(c, 5, 2, 1, 6, 4)), ("SetMirror(1)", c => Mirror(c, 1))) };
            yield return new Case { Name = "source 2,1,6,4 then destination null", Control = Calls(("SetSourceRectangle(2,1,6,4)", c => Rect(c, 4, 2, 1, 6, 4)), ("SetDestinationRectangle(null)", c => Null(c, 5))) };
            yield return new Case { Name = "destination 2,1,6,4 then null", Control = Calls(("SetDestinationRectangle(2,1,6,4)", c => Rect(c, 5, 2, 1, 6, 4)), ("SetDestinationRectangle(null)", c => Null(c, 5))) };
            yield return new Case { Name = "destination 2,1,6,4 then 0,0,0,0", Control = Calls(("SetDestinationRectangle(2,1,6,4)", c => Rect(c, 5, 2, 1, 6, 4)), ("SetDestinationRectangle(0,0,0,0)", c => Rect(c, 5, 0, 0, 0, 0))) };
            yield return new Case { Name = "mirror 1 then 0", Control = Calls(("SetMirror(1)", c => Mirror(c, 1)), ("SetMirror(0)", c => Mirror(c, 0))) };
            yield return new Case { Name = "constriction 4,3", Control = Calls(("SetConstrictionSize(4,3)", c => Constriction(c, 4, 3))) };
            yield return new Case { Name = "constriction null", Control = Calls(("SetConstrictionSize(null)", c => Null(c, 8))) };
        }

        private static string Out(IntPtr t, Case c, bool stride)
        {
            var picture = c.In == NV12 ? Nv12() : Rgb();
            var input = MFWriter.Sample(picture, 0, 400_000);
            int hr = ((delegate* unmanaged<IntPtr, uint, IntPtr, uint, int>)Fn(t, 24))(t, 0, input, 0);
            MFReader.Release(input);
            if (hr < 0) return $"input {Hr(hr)}";
            var room = new byte[4096]; room.AsSpan().Fill(c.Prefill);
            IntPtr mine = MFWriter.Sample(room, 0, 0);
            var odb = stackalloc byte[4 * sizeof(IntPtr)];
            new Span<byte>(odb, 4 * sizeof(IntPtr)).Clear();
            *(IntPtr*)(odb + IntPtr.Size) = mine;
            uint status;
            hr = ((delegate* unmanaged<IntPtr, uint, uint, byte*, uint*, int>)Fn(t, 25))(t, 0, 1, odb, &status);
            IntPtr got = *(IntPtr*)(odb + IntPtr.Size);
            if (hr < 0) { MFReader.Release(mine); return $"output {Hr(hr)}"; }
            var bytes = MFReader.SampleBytes(got, out _);
            MFReader.Release(got);
            return $"{bytes.Length} bytes: {Convert.ToHexString(bytes).ToLowerInvariant()}";
        }

        internal static List<string> Report()
        {
            MFReader.Startup();
            if (OperatingSystem.IsWindows()) MFVideoProcessorProbe.Init();
            var log = new ProgressLog("MF_PROBE_VPROC_CONTROL");
            foreach (var c in Cases())
            {
                log.Add($"{c.Name}:");
                int hr = MFVideoProcessorProbe.Make(MFVideoProcessorProbe.VideoProcessorClsid, out var t);
                if (hr < 0) { log.Add($"  create {Hr(hr)}"); continue; }
                var iid = IID_Control2; IntPtr ctl;
                hr = ((delegate* unmanaged<IntPtr, Guid*, IntPtr*, int>)Fn(t, 0))(t, &iid, &ctl);
                if (hr < 0) { log.Add($"  control {Hr(hr)}"); MFReader.Release(t); continue; }
                var lines = new List<string>();
                if (c.ControlFirst) c.Control?.Invoke(ctl, lines);
                var i = Type(c.In, W, H, c.InRotation, true, c.InPar); var o = Type(c.Out, c.OutW, c.OutH, null, true, c.OutPar);
                int ihr = ((delegate* unmanaged<IntPtr, uint, IntPtr, uint, int>)Fn(t, 15))(t, 0, i, 0);
                int ohr = ihr < 0 ? ihr : ((delegate* unmanaged<IntPtr, uint, IntPtr, uint, int>)Fn(t, 16))(t, 0, o, 0);
                MFReader.Release(i); MFReader.Release(o);
                if (ihr < 0 || ohr < 0) lines.Add($"  types {Hr(ihr)} {Hr(ohr)}");
                if (!c.ControlFirst) c.Control?.Invoke(ctl, lines);
                log.AddRange(lines);
                if (ihr >= 0 && ohr >= 0) log.Add("  " + Out(t, c, true));
                MFReader.Release(ctl);
                MFReader.Release(t);
            }
            return log;
        }

        /// <summary>
        /// The shim's video processor applies its control's mirror, rotations, rectangles and border color as Windows' does
        /// (VideoProcessorControl.windows.txt): where each picture lies, its border, what is left as it was, to the byte; the
        /// scaled pictures' bytes within 12 of Windows' (its scaler's filter is its own), of the strongest downscales (to a
        /// third, through the constriction) within 32.
        /// </summary>
        [Fact]
        public void TheVideoProcessorsControl_IsWindows()
        {
            Assert.SkipWhen(OperatingSystem.IsWindows(), "Windows' video processor is the fixture");
            var windows = File.ReadAllLines(Path.Combine(TestFiles.Root, "MediaFoundation", "VideoProcessorControl.windows.txt"));
            var ours = Report();
            Assert.Equal(windows.Length, ours.Count);
            string name = "";
            for (int i = 0; i < windows.Length; i++)
            {
                string w = windows[i], o = ours[i];
                if (!w.StartsWith(' ')) name = w;
                if (w == o) continue;
                Assert.True(w.Contains(" bytes: ") && o.Contains(" bytes: "), $"line {i}: Windows '{w}', ours '{o}'");
                var a = Convert.FromHexString(w.Split(": ")[1]); var b = Convert.FromHexString(o.Split(": ")[1]);
                Assert.Equal(a.Length, b.Length);
                int most = name.Contains("NV12 to RGB") || name.Contains("constriction") ? 32 : 12;
                for (int k = 0; k < a.Length; k++) Assert.True(Math.Abs(a[k] - b[k]) <= most, $"{name} byte {k}: Windows {a[k]}, ours {b[k]}");
            }
        }

        [Fact]
        public void Probe()
        {
            Assert.SkipWhen(Environment.GetEnvironmentVariable("MF_PROBE_VPROC_CONTROL") is not { Length: > 0 }, "MF_PROBE_VPROC_CONTROL=<file> writes the report");
            File.WriteAllLines(Environment.GetEnvironmentVariable("MF_PROBE_VPROC_CONTROL")!, Report());
        }
    }
}
