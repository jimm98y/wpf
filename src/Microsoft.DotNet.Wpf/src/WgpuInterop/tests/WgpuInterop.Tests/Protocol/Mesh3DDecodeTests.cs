// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// MeshGeometry3D decoding, specifically what happens to triangles that name a vertex the mesh does
// not have.
//
// WPF tolerates them: an over-long TriangleIndices list just leaves those triangles undrawn, and
// hand-written mesh generators produce them constantly (a stack/slice loop that emits `(stack+1)*n`
// rows for its LAST stack overruns the vertex array by a whole row). WebGPU does not tolerate them
// at all -- the out-of-range fetch fails the draw and the 3D pass resolves EMPTY -- so one bad
// triangle used to blank the entire Viewport3D rather than cost a few triangles. Filtering at decode
// is what keeps those apps looking the way they do on milcore.
//

using Microsoft.Wpf.Interop.WebGpu.Composition;
using Microsoft.Wpf.Interop.WebGpu.Composition.Protocol;
using WgpuInterop.Tests.Harness;
using Xunit;

namespace WgpuInterop.Tests.Protocol
{
    public sealed class Mesh3DDecodeTests
    {
        private const uint HMesh = 40;

        // Four vertices of a unit quad; normals all +Z.
        private static readonly float[] Positions =
        {
            -1, -1, 0,
             1, -1, 0,
             1,  1, 0,
            -1,  1, 0,
        };
        private static readonly float[] Normals = { 0, 0, 1, 0, 0, 1, 0, 0, 1, 0, 0, 1 };

        private static MeshGeometry3D Decode(uint[] indices)
        {
            var e = new MilcoreEngine();
            e.SubmitCommand(MilCmd.MeshGeometry3D(HMesh, Positions, Normals, indices));
            Assert.True(e.TryGetMesh(HMesh, out MeshGeometry3D mesh), "the mesh should decode");
            return mesh;
        }

        [Fact]
        public void ValidIndices_SurviveUntouched()
        {
            MeshGeometry3D mesh = Decode(new uint[] { 0, 1, 2, 0, 2, 3 });

            Assert.Equal(4, mesh.Positions.Length);
            Assert.Equal(new[] { 0, 1, 2, 0, 2, 3 }, mesh.Indices);
        }

        /// <summary>
        /// The generator overrun: the good triangles must still be there. Asserting only that nothing
        /// is out of range would pass for an implementation that dropped the mesh entirely -- which is
        /// the bug this guards against.
        /// </summary>
        [Fact]
        public void TrianglesNamingAMissingVertex_AreDroppedAndTheRestKept()
        {
            MeshGeometry3D mesh = Decode(new uint[]
            {
                0, 1, 2,      // valid
                2, 4, 3,      // vertex 4 does not exist
                0, 2, 3,      // valid
                7, 8, 9,      // none of these exist
            });

            Assert.Equal(new[] { 0, 1, 2, 0, 2, 3 }, mesh.Indices);
        }

        /// <summary>A trailing index that is not a whole triangle is not a triangle at all.</summary>
        [Fact]
        public void PartialTrailingTriangle_IsDropped()
        {
            MeshGeometry3D mesh = Decode(new uint[] { 0, 1, 2, 0, 2 });

            Assert.Equal(new[] { 0, 1, 2 }, mesh.Indices);
        }

        /// <summary>
        /// Nothing drawable is a legitimate outcome; it must not throw, and normals must still be
        /// computed (the mesh can be updated with valid indices later on the same handle).
        /// </summary>
        [Fact]
        public void AllTrianglesInvalid_LeavesAnEmptyIndexList()
        {
            MeshGeometry3D mesh = Decode(new uint[] { 9, 10, 11 });

            Assert.Empty(mesh.Indices);
            Assert.Equal(4, mesh.Positions.Length);
        }
    }
}
