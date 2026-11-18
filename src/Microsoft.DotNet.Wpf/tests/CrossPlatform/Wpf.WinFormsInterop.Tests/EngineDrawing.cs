// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// The parity batteries hold the GDI+ engine to gdiplus.dll byte for byte. A Graphics on a Bitmap records its drawing
// by default (deferred bitmaps: drawn as a scene when the bitmap is drawn, rasterized approximately when its pixels
// are read), so for them this thread's bitmaps are drawn by the engine, as WF_DEFER_BITMAPS=0 draws every bitmap.
// The switch is the stand-in's own, reached by name; it is this thread's, so the tests running beside them keep
// deferring.
//

#nullable disable

using System;
using System.Reflection;

namespace Wpf.WinFormsInterop.Tests
{
    internal sealed class EngineDrawing : IDisposable
    {
        private static readonly FieldInfo s_flag = typeof(System.Drawing.Bitmap).GetField("EngineOnThisThread", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new MissingFieldException("System.Drawing.Bitmap", "EngineOnThisThread");

        private readonly bool _was;

        private EngineDrawing()
        {
            _was = (bool)s_flag.GetValue(null);
            s_flag.SetValue(null, true);
        }

        /// <summary>This thread's bitmaps drawn by the engine until disposed.</summary>
        public static EngineDrawing Scope() => new EngineDrawing();

        public void Dispose() => s_flag.SetValue(null, _was);
    }
}
