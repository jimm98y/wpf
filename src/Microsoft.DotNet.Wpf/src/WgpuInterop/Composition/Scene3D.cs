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

        /// <summary>Explicit view/projection matrices (WPF MatrixCamera); used verbatim when
        /// HasMatrix is set, bypassing the look-at/FOV construction.</summary>
        public readonly bool HasMatrix;
        public readonly Matrix4x4 ViewMatrix;
        public readonly Matrix4x4 ProjMatrix;

        public Camera3D(Vector3 position, Vector3 lookDirection, Vector3 upDirection, float fieldOfView,
            float nearPlane = 0.125f, float farPlane = 1000f, bool orthographic = false, float width = 0f)
        {
            Position = position; LookDirection = lookDirection; UpDirection = upDirection;
            FieldOfView = fieldOfView; NearPlane = nearPlane; FarPlane = farPlane;
            Orthographic = orthographic; Width = width;
            HasMatrix = false; ViewMatrix = Matrix4x4.Identity; ProjMatrix = Matrix4x4.Identity;
        }

        public Camera3D(Matrix4x4 viewMatrix, Matrix4x4 projMatrix)
        {
            HasMatrix = true; ViewMatrix = viewMatrix; ProjMatrix = projMatrix;
            // Camera position (for specular) recovered from the inverse view; identity fallback.
            Position = Matrix4x4.Invert(viewMatrix, out Matrix4x4 inv) ? inv.Translation : Vector3.Zero;
            LookDirection = new Vector3(0, 0, -1); UpDirection = new Vector3(0, 1, 0);
            FieldOfView = 0f; NearPlane = 0f; FarPlane = 0f; Orthographic = false; Width = 0f;
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
        /// <summary>Optional straight-RGBA diffuse texture (a static image); null = none.</summary>
        public readonly byte[]? Texture;
        public readonly int TexWidth, TexHeight;
        /// <summary>Optional LIVE 2D content (a VisualBrush's visual) rendered to a GPU texture on the
        /// fly and sampled as the diffuse texture — interactive 2D-in-3D, no CPU readback.</summary>
        public readonly SceneVisual? TextureVisual;
        public readonly Rect TexVisualBounds;   // the visual's content bounds (local 2D space)
        /// <summary>When true, the diffuse texture is also added as EMISSIVE (self-lit) — a
        /// WPF EmissiveMaterial sharing the diffuse brush; makes a live 2D UI read like a screen.</summary>
        public readonly bool EmissiveTextured;
        /// <summary>True when the material tree is EmissiveMaterial only (no Diffuse/Specular material).
        /// WPF's EmissiveMaterial is UNLIT (self-illuminated: scene lights/ambient don't affect it) but
        /// composites alpha-OVER by its texel alpha. Rendered as the bare emissive colour so scene
        /// lighting can't wash a grey semi-transparent texture (e.g. a lattice) to white.</summary>
        public readonly bool EmissiveOnly;

        public bool HasTexture => (Texture is not null && TexWidth > 0) || TextureVisual is not null;

        public Material3D(RgbaColor diffuse, RgbaColor specular, float specularPower, RgbaColor emissive,
            byte[]? texture = null, int texWidth = 0, int texHeight = 0,
            SceneVisual? textureVisual = null, Rect texVisualBounds = default, bool emissiveTextured = false,
            bool emissiveOnly = false)
        {
            Diffuse = diffuse; Specular = specular; SpecularPower = specularPower; Emissive = emissive;
            Texture = texture; TexWidth = texWidth; TexHeight = texHeight;
            TextureVisual = textureVisual; TexVisualBounds = texVisualBounds; EmissiveTextured = emissiveTextured;
            EmissiveOnly = emissiveOnly;
        }

        public static Material3D Diffuse3D(RgbaColor c)
            => new(c, new RgbaColor(0, 0, 0, 0), 1f, new RgbaColor(0, 0, 0, 0));
    }

    /// <summary>A mesh with front/back materials and a model transform.</summary>
    internal sealed class Model3D
    {
        public MeshGeometry3D Mesh { get; }
        public Material3D Material { get; }
        public Matrix4x4 Transform { get; }
        /// <summary>Material for back faces (WPF GeometryModel3D.BackMaterial); a side without a
        /// material is culled. Front-only remains the common case.</summary>
        public Material3D BackMaterial { get; }
        public bool HasFrontMaterial { get; }
        public bool HasBackMaterial { get; }

        /// <summary>Legacy accessor: the material's diffuse colour.</summary>
        public RgbaColor DiffuseColor => Material.Diffuse;

        public Model3D(MeshGeometry3D mesh, Material3D material, Matrix4x4 transform)
        {
            Mesh = mesh; Material = material; Transform = transform; HasFrontMaterial = true;
        }

        public Model3D(MeshGeometry3D mesh, Material3D material, bool hasFront,
            Material3D backMaterial, bool hasBack, Matrix4x4 transform)
        {
            Mesh = mesh; Material = material; Transform = transform;
            HasFrontMaterial = hasFront; BackMaterial = backMaterial; HasBackMaterial = hasBack;
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
