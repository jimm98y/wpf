// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Drawing.WebGpuBackend.Gdip;

namespace System.Drawing.Drawing2D
{
    // A managed GDI+ path iterator (WebGpuBackend.Gdip.GpPathIterator), over a snapshot of the path
    // taken when it is made, as GDI+ iterates a copy of the path's data.
    public sealed class GraphicsPathIterator : MarshalByRefObject, IDisposable
    {
        private readonly GpPathIterator _it;

        public GraphicsPathIterator(GraphicsPath path)
        {
            _it = new GpPathIterator(path?.gp);
        }

        public void Dispose()
        {
            _disposed = true;
            GC.SuppressFinalize(this);
        }

        // GDI+'s object is gone after Dispose: every call System.Drawing makes with its handle (zero)
        // comes back InvalidParameter.
        private bool _disposed;
        internal bool IsDisposed => _disposed;

        internal void Live()
        {
            if (_disposed)
                throw new ArgumentException("Parameter is not valid.");
        }


        public int NextSubpath(out int startIndex, out int endIndex, out bool isClosed)
        {
            Live();
            return _it.NextSubpath(out startIndex, out endIndex, out isClosed);
        }

        // DpPathIterator::NextSubpath(DpPath*): the subpath's points become the path's.
        public int NextSubpath(GraphicsPath path, out bool isClosed)
        {
            Live();
            // GdipPathIterNextSubpathPath with no path (none, or a disposed one): nothing, and Ok.
            if (path == null || path.IsDisposed)
            {
                isClosed = false;
                return 0;
            }
            int n = _it.NextSubpath(out int start, out _, out isClosed);
            if (path != null) path.gp = n > 0 ? _it.Slice(start, n) : new GpPath();
            return n;
        }

        public int NextPathType(out byte pathType, out int startIndex, out int endIndex)
        {
            Live();
            return _it.NextPathType(out pathType, out startIndex, out endIndex);
        }

        public int NextMarker(out int startIndex, out int endIndex)
        {
            Live();
            return _it.NextMarker(out startIndex, out endIndex);
        }

        public int NextMarker(GraphicsPath path)
        {
            Live();
            if (path == null || path.IsDisposed)
            {
                return 0;
            }
            int n = _it.NextMarker(out int start, out _);
            if (path != null) path.gp = n > 0 ? _it.Slice(start, n) : new GpPath();
            return n;
        }

        public int Count
        {
            get
            {
                Live();
                return _it.Count;
            }
        }

        public int SubpathCount
        {
            get
            {
                Live();
                return _it.Valid ? _it.SubpathCount : 0;
            }
        }

        public bool HasCurve()
        {
            Live();
            return _it.Valid && _it.HasCurve;
        }

        public void Rewind()
        {
            Live();
            _it.Rewind();
        }

        public int Enumerate(ref PointF[] points, ref byte[] types)
        {
            // System.Drawing's own checks: both arrays, of one length, room for every point.
            ArgumentNullException.ThrowIfNull(points);
            ArgumentNullException.ThrowIfNull(types);
            if (points.Length != types.Length || points.Length < Count)
                throw SafeNativeMethods.Gdip.StatusException(SafeNativeMethods.Gdip.InvalidParameter);
            if (points.Length == 0)
                return 0;
            Live();
            return _it.Enumerate(points, types);
        }

        public int CopyData(ref PointF[] points, ref byte[] types, int startIndex, int endIndex)
        {
            ArgumentNullException.ThrowIfNull(points);
            ArgumentNullException.ThrowIfNull(types);
            if (points.Length != types.Length || endIndex < 0 || startIndex < 0 || endIndex < startIndex
                || endIndex - startIndex + 1 > points.Length || endIndex >= Count)
                throw SafeNativeMethods.Gdip.StatusException(SafeNativeMethods.Gdip.InvalidParameter);
            Live();
            return _it.CopyData(points, types, startIndex, endIndex);
        }
    }
}
