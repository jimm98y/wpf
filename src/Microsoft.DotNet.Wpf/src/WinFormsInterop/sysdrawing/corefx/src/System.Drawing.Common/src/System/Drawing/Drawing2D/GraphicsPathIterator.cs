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
            GC.SuppressFinalize(this);
        }

        public int NextSubpath(out int startIndex, out int endIndex, out bool isClosed)
            => _it.NextSubpath(out startIndex, out endIndex, out isClosed);

        // DpPathIterator::NextSubpath(DpPath*): the subpath's points become the path's.
        public int NextSubpath(GraphicsPath path, out bool isClosed)
        {
            int n = _it.NextSubpath(out int start, out _, out isClosed);
            if (path != null) path.gp = n > 0 ? _it.Slice(start, n) : new GpPath();
            return n;
        }

        public int NextPathType(out byte pathType, out int startIndex, out int endIndex)
            => _it.NextPathType(out pathType, out startIndex, out endIndex);

        public int NextMarker(out int startIndex, out int endIndex)
            => _it.NextMarker(out startIndex, out endIndex);

        public int NextMarker(GraphicsPath path)
        {
            int n = _it.NextMarker(out int start, out _);
            if (path != null) path.gp = n > 0 ? _it.Slice(start, n) : new GpPath();
            return n;
        }

        public int Count => _it.Count;

        public int SubpathCount => _it.Valid ? _it.SubpathCount : 0;

        public bool HasCurve() => _it.Valid && _it.HasCurve;

        public void Rewind() => _it.Rewind();

        public int Enumerate(ref PointF[] points, ref byte[] types)
        {
            if (points.Length != types.Length)
                throw SafeNativeMethods.Gdip.StatusException(SafeNativeMethods.Gdip.InvalidParameter);
            return _it.Enumerate(points, types);
        }

        public int CopyData(ref PointF[] points, ref byte[] types, int startIndex, int endIndex)
        {
            if ((points.Length != types.Length) || (endIndex - startIndex + 1 > points.Length))
                throw SafeNativeMethods.Gdip.StatusException(SafeNativeMethods.Gdip.InvalidParameter);
            return _it.CopyData(points, types, startIndex, endIndex);
        }
    }
}
