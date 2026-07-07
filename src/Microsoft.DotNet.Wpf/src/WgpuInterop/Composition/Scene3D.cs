// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// A 3D scene model mirroring WPF's 3D (Viewport3D / Camera / GeometryModel3D /
// MeshGeometry3D / lights / materials). 3D content is drawn into the 2D visual tree
// by a Viewport3DDraw primitive: the renderer rasterizes the meshes with a camera,
// Blinn-Phong lighting (directional + point + spot lights, diffuse/specular/emissive
// materials, optional diffuse texture) and a depth buffer into an offscreen texture,
// then composites that as a 2D layer.
//

using System.Collections.Generic;
using System.Numerics;

namespace Microsoft.Wpf.Interop.WebGpu.Composition
{
    /// <summary>A triangle mesh: positions, per-vertex normals, texture coords and indices.</summary>
    internal sealed class MeshGeometry3D
    {
        public Vector3[] Positions { get; }
        public Vector3[] Normals { get; }
        public int[] Indices { get; }
        /// <summary>Per-vertex texture coordinates (empty if the mesh is untextured).</summary>
        public Vector2[] TexCoords { get; }

        public MeshGeometry3D(Vector3[] positions, Vector3[] normals, int[] indices)
            : this(positions, normals, indices, System.Array.Empty<Vector2>()) { }

        public MeshGeometry3D(Vector3[] positions, Vector3[] normals, int[] indices, Vector2[] texCoords)
        {
            Positions = positions; Normals = normals; Indices = indices; TexCoords = texCoords;
        }
    }

    /// <summary>A perspective (or orthographic) camera (WPF's Perspective/OrthographicCamera).</summary>
    internal readonly struct Camera3D
    {
        public readonly Vector3 Position;
        public readonly Vector3 LookDirection;
        public readonly Vector3 UpDirection;
        public readonly float FieldOfView; // horizontal, degrees (matches WPF PerspectiveCamera.FieldOfView)
        public readonly float NearPlane;
        public readonly float FarPlane;
        public readonly bool Orthographic;
        public readonly float Width;        // orthographic view width (world units)

        public Camera3D(Vector3 position, Vector3 lookDirection, Vector3 upDirection, float fieldOfView,
            float nearPlane = 0.125f, float farPlane = 1000f, bool orthographic = false, float width = 0f)
        {
            Position = position; LookDirection = lookDirection; UpDirection = upDirection;
            FieldOfView = fieldOfView; NearPlane = nearPlane; FarPlane = farPlane;
            Orthographic = orthographic; Width = width;
        }
    }

    internal enum Light3DKind { Ambient = 0, Directional = 1, Point = 2, Spot = 3 }

    /// <summary>A light source (WPF's DirectionalLight / PointLight / SpotLight / AmbientLight).</summary>
    internal readonly struct Light3D
    {
        public readonly Light3DKind Kind;
        public readonly RgbaColor Color;
        public readonly Vector3 Direction;      // directional / spot: direction the light travels
        public readonly Vector3 Position;       // point / spot
        public readonly float Range;            // point / spot: max reach (0 = infinite)
        public readonly float ConstantAtten, LinearAtten, QuadraticAtten;
        public readonly float InnerConeCos, OuterConeCos;   // spot: cosines of the cone half-angles

        public Light3D(Light3DKind kind, RgbaColor color, Vector3 direction, Vector3 position,
            float range, float ca, float la, float qa, float innerConeCos, float outerConeCos)
        {
            Kind = kind; Color = color; Direction = direction; Position = position;
            Range = range; ConstantAtten = ca; LinearAtten = la; QuadraticAtten = qa;
            InnerConeCos = innerConeCos; OuterConeCos = outerConeCos;
        }

        public static Light3D Directional(Vector3 dir, RgbaColor color)
            => new(Light3DKind.Directional, color, dir, Vector3.Zero, 0, 1, 0, 0, 1, 1);
        public static Light3D Point(Vector3 pos, RgbaColor color, float range, float ca, float la, float qa)
            => new(Light3DKind.Point, color, Vector3.Zero, pos, range, ca, la, qa, 1, 1);
    }

    /// <summary>Retained for tests / callers that build a single directional light.</summary>
    internal readonly struct DirectionalLight3D
    {
        public readonly Vector3 Direction;
        public readonly RgbaColor Color;
        public DirectionalLight3D(Vector3 direction, RgbaColor color) { Direction = direction; Color = color; }
    }

    /// <summary>A surface material: diffuse (optionally textured), specular (Blinn-Phong) and emissive.</summary>
    internal readonly struct Material3D
    {
        public readonly RgbaColor Diffuse;
        public readonly RgbaColor Specular;
        public readonly float SpecularPower;
        public readonly RgbaColor Emissive;
        /// <summary>Optional straight-RGBA diffuse texture; null = untextured.</summary>
        public readonly byte[]? Texture;
        public readonly int TexWidth, TexHeight;

        public Material3D(RgbaColor diffuse, RgbaColor specular, float specularPower, RgbaColor emissive,
            byte[]? texture = null, int texWidth = 0, int texHeight = 0)
        {
            Diffuse = diffuse; Specular = specular; SpecularPower = specularPower; Emissive = emissive;
            Texture = texture; TexWidth = texWidth; TexHeight = texHeight;
        }

        public static Material3D Diffuse3D(RgbaColor c)
            => new(c, new RgbaColor(0, 0, 0, 0), 1f, new RgbaColor(0, 0, 0, 0));
    }

    /// <summary>A mesh with a material and a model transform.</summary>
    internal sealed class Model3D
    {
        public MeshGeometry3D Mesh { get; }
        public Material3D Material { get; }
        public Matrix4x4 Transform { get; }

        /// <summary>Legacy accessor: the material's diffuse colour.</summary>
        public RgbaColor DiffuseColor => Material.Diffuse;

        public Model3D(MeshGeometry3D mesh, Material3D material, Matrix4x4 transform)
        {
            Mesh = mesh; Material = material; Transform = transform;
        }

        public Model3D(MeshGeometry3D mesh, RgbaColor diffuseColor, Matrix4x4 transform)
            : this(mesh, Material3D.Diffuse3D(diffuseColor), transform) { }

        public Model3D(MeshGeometry3D mesh, RgbaColor diffuseColor)
            : this(mesh, Material3D.Diffuse3D(diffuseColor), Matrix4x4.Identity) { }
    }

    /// <summary>Draws 3D content (the analog of WPF's Viewport3DVisual).</summary>
    internal sealed class Viewport3DDraw : DrawingPrimitive
    {
        public Camera3D Camera { get; }
        public IReadOnlyList<Light3D> Lights { get; }
        public RgbaColor AmbientColor { get; }
        public List<Model3D> Models { get; }

        /// <summary>Legacy accessor: the first directional light, or a default.</summary>
        public DirectionalLight3D Light
        {
            get
            {
                foreach (Light3D l in Lights)
                    if (l.Kind == Light3DKind.Directional)
                        return new DirectionalLight3D(l.Direction, l.Color);
                return new DirectionalLight3D(new Vector3(0, 0, -1), RgbaColor.FromBytes(255, 255, 255, 255));
            }
        }

        /// <summary>The 2D rect (in the hosting visual's local space) the 3D scene projects into.
        /// An empty rect means the whole render target (used by the standalone 3D test).</summary>
        public Rect Viewport { get; }

        // New API: full light list.
        public Viewport3DDraw(Camera3D camera, IReadOnlyList<Light3D> lights, RgbaColor ambientColor, List<Model3D> models, Rect viewport)
        {
            Camera = camera; Lights = lights; AmbientColor = ambientColor; Models = models; Viewport = viewport;
        }

        // Legacy API: single directional light (used by Viewport3DTest).
        public Viewport3DDraw(Camera3D camera, DirectionalLight3D light, RgbaColor ambientColor, List<Model3D> models)
            : this(camera, light, ambientColor, models, default) { }

        public Viewport3DDraw(Camera3D camera, DirectionalLight3D light, RgbaColor ambientColor, List<Model3D> models, Rect viewport)
            : this(camera, new[] { Light3D.Directional(light.Direction, light.Color) }, ambientColor, models, viewport) { }
    }
}
