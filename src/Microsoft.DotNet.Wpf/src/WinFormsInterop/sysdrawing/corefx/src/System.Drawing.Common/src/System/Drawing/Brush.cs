// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.ComponentModel;

namespace System.Drawing
{
    // A managed brush: every kind keeps its own state, drawn by the managed GDI+ engine
    // (WebGpuBackend.Gdip) or recorded for the GPU. There is no native brush behind any of them.
    public abstract class Brush : MarshalByRefObject, ICloneable, IDisposable
    {
        public abstract object Clone();

        // Kept for API compatibility (a derived brush may hand one in); nothing native ever uses it.
        private IntPtr _nativeBrush;
        protected internal void SetNativeBrush(IntPtr brush) => _nativeBrush = brush;
        internal void SetNativeBrushInternal(IntPtr brush) => _nativeBrush = brush;

        [Browsable(false), EditorBrowsable(EditorBrowsableState.Never)]
        internal IntPtr NativeBrush => _nativeBrush;

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            _nativeBrush = IntPtr.Zero;
            _disposed = true;
        }

        // GDI+'s brush is gone after Dispose: every call System.Drawing makes with its handle comes
        // back InvalidParameter. What .NET caches managed-side (a solid brush's colour) still answers.
        private bool _disposed;
        internal bool IsDisposed => _disposed;

        internal void Live()
        {
            if (_disposed)
                throw new ArgumentException(SR.GdiplusInvalidParameter);
        }

        ~Brush() => Dispose(false);
    }
}
