// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// A minimal 3D scene model mirroring WPF's 3D (Viewport3D / Camera /
// GeometryModel3D / MeshGeometry3D / DirectionalLight). 3D content is drawn into
// the 2D visual tree by a Viewport3DDraw primitive: the renderer rasterizes the
// meshes with a perspective camera, directional + ambient diffuse lighting and a
// depth buffer into an offscreen texture, then composites that as a 2D layer.
//

using System.Collections.Generic;
using System.Numerics;

namespace Microsoft.Wpf.Interop.WebGpu.Composition
{
    /// <summary>A triangle mesh: positions, per-vertex normals and indices.</summary>
    internal sealed class MeshGeometry3D
    {
        public Vector3[] Positions { get; }
        public Vector3[] Normals { get; }
        public int[] Indices { get; }

        public MeshGeometry3D(Vector3[] positions, Vector3[] normals, int[] indices)
        {
            Positions = positions; Normals = normals; Indices = indices;
        }
    }

    /// <summary>A right-handed perspective camera (WPF's PerspectiveCamera).</summary>
    internal readonly struct Camera3D
    {
        public readonly Vector3 Position;
        public readonly Vector3 LookDirection;
        public readonly Vector3 UpDirection;
        public readonly float FieldOfView; // vertical, degrees
        public readonly float NearPlane;
        public readonly float FarPlane;

        public Camera3D(Vector3 position, Vector3 lookDirection, Vector3 upDirection, float fieldOfView,
            float nearPlane = 0.125f, float farPlane = 1000f)
        {
            Position = position; LookDirection = lookDirection; UpDirection = upDirection;
            FieldOfView = fieldOfView; NearPlane = nearPlane; FarPlane = farPlane;
        }
    }

    /// <summary>A directional light (WPF's DirectionalLight).</summary>
    internal readonly struct DirectionalLight3D
    {
        public readonly Vector3 Direction; // direction the light travels
        public readonly RgbaColor Color;

        public DirectionalLight3D(Vector3 direction, RgbaColor color)
        {
            Direction = direction; Color = color;
        }
    }

    /// <summary>A mesh with a diffuse material colour and a model transform.</summary>
    internal sealed class Model3D
    {
        public MeshGeometry3D Mesh { get; }
        public RgbaColor DiffuseColor { get; }
        public Matrix4x4 Transform { get; }

        public Model3D(MeshGeometry3D mesh, RgbaColor diffuseColor, Matrix4x4 transform)
        {
            Mesh = mesh; DiffuseColor = diffuseColor; Transform = transform;
        }

        public Model3D(MeshGeometry3D mesh, RgbaColor diffuseColor)
            : this(mesh, diffuseColor, Matrix4x4.Identity)
        {
        }
    }

    /// <summary>Draws 3D content (the analog of WPF's Viewport3DVisual).</summary>
    internal sealed class Viewport3DDraw : DrawingPrimitive
    {
        public Camera3D Camera { get; }
        public DirectionalLight3D Light { get; }
        public RgbaColor AmbientColor { get; }
        public List<Model3D> Models { get; }

        public Viewport3DDraw(Camera3D camera, DirectionalLight3D light, RgbaColor ambientColor, List<Model3D> models)
        {
            Camera = camera; Light = light; AmbientColor = ambientColor; Models = models;
        }
    }
}
