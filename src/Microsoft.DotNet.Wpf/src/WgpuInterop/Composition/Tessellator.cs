// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// Turns filled geometry into triangles. This is the cross-platform replacement
// for the geometry rasterization milcore performs on the CPU/GPU
// (MilUtility_PathGeometry* + the hw/sw fill pipelines). The slice covers the
// two geometries the scene model exposes today; arbitrary path filling
// (curves, concave outlines, even-odd/nonzero rules, anti-aliasing) is future
// work and would plug in here.
//

using System.Numerics;

namespace Microsoft.Wpf.Interop.WebGpu.Composition
{
    /// <summary>Triangle mesh in a visual's local coordinate space.</summary>
    internal readonly struct Mesh
    {
        public readonly Vector2[] Positions;
        public readonly uint[] Indices;

        public Mesh(Vector2[] positions, uint[] indices)
        {
            Positions = positions;
            Indices = indices;
        }
    }

    internal static class Tessellator
    {
        public static Mesh Tessellate(Geometry geometry) => geometry switch
        {
            RectangleGeometry r => TessellateRectangle(r.Rect),
            PolygonGeometry p => TessellateConvexFan(p.Points),
            _ => new Mesh(System.Array.Empty<Vector2>(), System.Array.Empty<uint>()),
        };

        private static Mesh TessellateRectangle(Rect r)
        {
            // Two triangles, CCW in a y-down space is the same winding we cull
            // off (cullMode None in the pipeline), so winding is not significant.
            var positions = new[]
            {
                new Vector2(r.X, r.Y),
                new Vector2(r.X + r.Width, r.Y),
                new Vector2(r.X + r.Width, r.Y + r.Height),
                new Vector2(r.X, r.Y + r.Height),
            };
            var indices = new uint[] { 0, 1, 2, 0, 2, 3 };
            return new Mesh(positions, indices);
        }

        private static Mesh TessellateConvexFan(Vector2[] points)
        {
            if (points.Length < 3)
                return new Mesh(System.Array.Empty<Vector2>(), System.Array.Empty<uint>());

            var indices = new uint[(points.Length - 2) * 3];
            int k = 0;
            for (uint i = 1; i < points.Length - 1; i++)
            {
                indices[k++] = 0;
                indices[k++] = i;
                indices[k++] = i + 1;
            }
            return new Mesh(points, indices);
        }
    }
}
