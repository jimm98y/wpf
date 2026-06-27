// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// 3D decode: turns WPF's Viewport3D / Visual3D / Model3D command stream into the renderer's
// flat Viewport3DDraw (camera + one directional light + ambient + world-transformed meshes).
// A Viewport3DVisual is a 2D node (it occupies a rect in its parent 2D visual) whose subtree
// is a Visual3D graph; each frame Realize3D() walks that graph, accumulates 3D transforms, and
// emits a single Viewport3DDraw into the 2D visual's content so WgpuSceneRenderer can project
// it into the viewport rect. Struct offsets mirror src/Common/Graphics/Generated/wgx_commands.cs.
//

using System;
using System.Collections.Generic;
using System.Numerics;

namespace Microsoft.Wpf.Interop.WebGpu.Composition.Protocol
{
    internal sealed partial class MilcoreEngine
    {
        private readonly Dictionary<uint, Camera3D> _cameras = new();
        private readonly Dictionary<uint, MeshGeometry3D> _meshes = new();
        private readonly Dictionary<uint, MaterialDef> _materials = new();
        private readonly Dictionary<uint, Matrix4x4> _xform3D = new();          // translate/scale/matrix (precomputed)
        private readonly Dictionary<uint, RotateXform3D> _rotateXforms = new(); // rotation resolved lazily (animated)
        private readonly Dictionary<uint, List<uint>> _xform3DGroups = new();
        private readonly Dictionary<uint, AxisAngle> _rotations = new();
        private readonly Dictionary<uint, Model3DNode> _models3D = new();
        private readonly Dictionary<uint, Visual3DNode> _visuals3D = new();
        private readonly Dictionary<uint, Viewport3DState> _viewports3D = new();

        private struct MaterialDef { public RgbaColor Color; public uint Brush; }
        private struct AxisAngle { public Vector3 Axis; public float Angle; }
        private struct RotateXform3D { public Vector3 Center; public uint RotationHandle; }

        private sealed class Model3DNode
        {
            public uint TransformHandle;
            public List<uint>? GroupChildren;       // Model3DGroup
            public uint MeshHandle, MaterialHandle;  // GeometryModel3D
            public int LightKind;                    // 1 = ambient, 2 = directional
            public RgbaColor LightColor;
            public Vector3 LightDir;
        }

        private sealed class Visual3DNode
        {
            public uint ContentHandle;
            public uint TransformHandle;
            public readonly List<uint> Children = new();
        }

        private sealed class Viewport3DState
        {
            public uint CameraHandle;
            public Rect Viewport;
            public uint ChildHandle;
        }

        private sealed class LightAcc
        {
            public bool HasDir;
            public DirectionalLight3D Dir = new(new Vector3(0, 0, -1), RgbaColor.FromBytes(255, 255, 255, 255));
            public RgbaColor Ambient;
        }

        private Viewport3DState Vp(uint h) => _viewports3D.TryGetValue(h, out Viewport3DState? s) ? s : (_viewports3D[h] = new Viewport3DState());
        private Visual3DNode V3(uint h) => _visuals3D.TryGetValue(h, out Visual3DNode? s) ? s : (_visuals3D[h] = new Visual3DNode());

        // MilReader is a value type; take it by ref so reads advance the caller's position.
        private static Vector3 Pt3(ref MilReader r) { float x = r.F32(), y = r.F32(), z = r.F32(); return new Vector3(x, y, z); }
        private static RgbaColor Col(ref MilReader r) { float cr = r.F32(), cg = r.F32(), cb = r.F32(), ca = r.F32(); return new RgbaColor(cr, cg, cb, ca); }

        // Dispatches one 3D MILCMD (reader positioned just after the 4-byte command id).
        private void Decode3D(Mil id, MilReader r)
        {
            switch (id)
            {
                case Mil.Viewport3DVisualSetCamera:
                {
                    uint vh = r.U32(); Vp(vh).CameraHandle = r.U32();
                    break;
                }
                case Mil.Viewport3DVisualSetViewport:
                {
                    uint vh = r.U32();
                    double x = r.F64(), y = r.F64(), w = r.F64(), h = r.F64();
                    Vp(vh).Viewport = new Rect((float)x, (float)y, (float)w, (float)h);
                    break;
                }
                case Mil.Viewport3DVisualSet3DChild:
                {
                    uint vh = r.U32(); Vp(vh).ChildHandle = r.U32();
                    break;
                }
                case Mil.Visual3DSetContent:
                {
                    uint vh = r.U32(); V3(vh).ContentHandle = r.U32();
                    break;
                }
                case Mil.Visual3DSetTransform:
                {
                    uint vh = r.U32(); V3(vh).TransformHandle = r.U32();
                    break;
                }
                case Mil.Visual3DInsertChildAt:
                {
                    Visual3DNode n = V3(r.U32());
                    uint child = r.U32();
                    int index = (int)r.U32();
                    if (index < 0 || index > n.Children.Count) index = n.Children.Count;
                    n.Children.Insert(index, child);
                    break;
                }
                case Mil.Visual3DRemoveChild:
                {
                    Visual3DNode n = V3(r.U32()); n.Children.Remove(r.U32());
                    break;
                }
                case Mil.Visual3DRemoveAllChildren:
                {
                    V3(r.U32()).Children.Clear();
                    break;
                }
                case Mil.PerspectiveCamera:
                {
                    uint h = r.U32();
                    double near = r.F64(), far = r.F64(), fov = r.F64();
                    Vector3 pos = Pt3(ref r);
                    r.U32();                 // htransform
                    Vector3 look = Pt3(ref r);
                    r.U32();                 // hNearPlaneDistanceAnimations
                    Vector3 up = Pt3(ref r);
                    _cameras[h] = new Camera3D(pos, look, up, (float)fov, (float)Math.Max(near, 0.001), (float)far);
                    break;
                }
                case Mil.AmbientLight:
                {
                    uint h = r.U32(); RgbaColor c = Col(ref r);
                    _models3D[h] = new Model3DNode { LightKind = 1, LightColor = c };
                    break;
                }
                case Mil.DirectionalLight:
                {
                    uint h = r.U32(); RgbaColor c = Col(ref r); Vector3 dir = Pt3(ref r);
                    _models3D[h] = new Model3DNode { LightKind = 2, LightColor = c, LightDir = dir };
                    break;
                }
                case Mil.GeometryModel3D:
                {
                    uint h = r.U32();
                    uint htransform = r.U32(), hgeometry = r.U32(), hmaterial = r.U32();
                    r.U32();                 // hbackMaterial
                    _models3D[h] = new Model3DNode { TransformHandle = htransform, MeshHandle = hgeometry, MaterialHandle = hmaterial };
                    break;
                }
                case Mil.Model3DGroup:
                {
                    uint h = r.U32();
                    uint htransform = r.U32();
                    uint childrenSize = r.U32();
                    var children = new List<uint>();
                    for (uint i = 0; i + 4 <= childrenSize; i += 4) children.Add(r.U32());
                    _models3D[h] = new Model3DNode { TransformHandle = htransform, GroupChildren = children };
                    break;
                }
                case Mil.MeshGeometry3D:
                {
                    uint h = r.U32();
                    uint posSize = r.U32(), normSize = r.U32(), texSize = r.U32(), idxSize = r.U32();
                    var positions = new Vector3[posSize / 12];
                    for (int i = 0; i < positions.Length; i++) positions[i] = Pt3(ref r);
                    var normals = new Vector3[normSize / 12];
                    for (int i = 0; i < normals.Length; i++) normals[i] = Pt3(ref r);
                    r.Bytes((int)texSize);   // texture coordinates: not used by the diffuse-colour shader
                    var indices = new int[idxSize / 4];
                    for (int i = 0; i < indices.Length; i++) indices[i] = (int)r.U32();
                    if (normals.Length != positions.Length) normals = ComputeNormals(positions, indices);
                    _meshes[h] = new MeshGeometry3D(positions, normals, indices);
                    break;
                }
                case Mil.DiffuseMaterial:
                {
                    uint h = r.U32();
                    RgbaColor color = Col(ref r);
                    Col(ref r);                  // ambientColor (folded into the viewport ambient instead)
                    uint hbrush = r.U32();
                    _materials[h] = new MaterialDef { Color = color, Brush = hbrush };
                    break;
                }
                case Mil.AxisAngleRotation3D:
                {
                    uint h = r.U32();
                    double angle = r.F64();
                    Vector3 axis = Pt3(ref r);
                    _rotations[h] = new AxisAngle { Axis = axis, Angle = (float)angle };
                    break;
                }
                case Mil.TranslateTransform3D:
                {
                    uint h = r.U32();
                    double ox = r.F64(), oy = r.F64(), oz = r.F64();
                    _xform3D[h] = Matrix4x4.CreateTranslation((float)ox, (float)oy, (float)oz);
                    break;
                }
                case Mil.ScaleTransform3D:
                {
                    uint h = r.U32();
                    double sx = r.F64(), sy = r.F64(), sz = r.F64(), cx = r.F64(), cy = r.F64(), cz = r.F64();
                    _xform3D[h] = Matrix4x4.CreateScale((float)sx, (float)sy, (float)sz, new Vector3((float)cx, (float)cy, (float)cz));
                    break;
                }
                case Mil.RotateTransform3D:
                {
                    uint h = r.U32();
                    double cx = r.F64(), cy = r.F64(), cz = r.F64();
                    r.U32(); r.U32(); r.U32();   // hCenter{X,Y,Z}Animations
                    uint hrotation = r.U32();
                    _rotateXforms[h] = new RotateXform3D { Center = new Vector3((float)cx, (float)cy, (float)cz), RotationHandle = hrotation };
                    break;
                }
                case Mil.MatrixTransform3D:
                {
                    uint h = r.U32();
                    _xform3D[h] = new Matrix4x4(
                        r.F32(), r.F32(), r.F32(), r.F32(),
                        r.F32(), r.F32(), r.F32(), r.F32(),
                        r.F32(), r.F32(), r.F32(), r.F32(),
                        r.F32(), r.F32(), r.F32(), r.F32());
                    break;
                }
                case Mil.Transform3DGroup:
                {
                    uint h = r.U32();
                    uint childrenSize = r.U32();
                    var children = new List<uint>();
                    for (uint i = 0; i + 4 <= childrenSize; i += 4) children.Add(r.U32());
                    _xform3DGroups[h] = children;
                    break;
                }
            }
        }

        // Flatten each Viewport3DVisual's 3D subtree into a Viewport3DDraw (rebuilt every frame
        // so animated cameras/rotations/materials are picked up).
        private void Realize3D()
        {
            foreach (KeyValuePair<uint, Viewport3DState> kv in _viewports3D)
            {
                if (!_visuals.TryGetValue(kv.Key, out SceneVisual? v)) continue;
                v.Content.Clear();
                Viewport3DState vp = kv.Value;
                if (vp.ChildHandle == 0 || !_cameras.TryGetValue(vp.CameraHandle, out Camera3D cam)) continue;

                var models = new List<Model3D>();
                var lights = new LightAcc();
                WalkVisual3D(vp.ChildHandle, Matrix4x4.Identity, models, lights);
                if (models.Count == 0) continue;
                v.Content.Add(new Viewport3DDraw(cam, lights.Dir, lights.Ambient, models, vp.Viewport));
            }
        }

        private void WalkVisual3D(uint handle, Matrix4x4 parent, List<Model3D> models, LightAcc lights)
        {
            if (!_visuals3D.TryGetValue(handle, out Visual3DNode? node)) return;
            Matrix4x4 m = ResolveTransform3D(node.TransformHandle) * parent;
            if (node.ContentHandle != 0) WalkModel3D(node.ContentHandle, m, models, lights);
            foreach (uint child in node.Children) WalkVisual3D(child, m, models, lights);
        }

        private void WalkModel3D(uint handle, Matrix4x4 parent, List<Model3D> models, LightAcc lights)
        {
            if (!_models3D.TryGetValue(handle, out Model3DNode? node)) return;
            Matrix4x4 m = ResolveTransform3D(node.TransformHandle) * parent;

            if (node.GroupChildren is not null)
            {
                foreach (uint child in node.GroupChildren) WalkModel3D(child, m, models, lights);
            }
            else if (node.LightKind == 1)
            {
                lights.Ambient = new RgbaColor(
                    Math.Min(1f, lights.Ambient.R + node.LightColor.R),
                    Math.Min(1f, lights.Ambient.G + node.LightColor.G),
                    Math.Min(1f, lights.Ambient.B + node.LightColor.B), 1f);
            }
            else if (node.LightKind == 2)
            {
                Vector3 dir = Vector3.TransformNormal(node.LightDir, m);
                if (dir.LengthSquared() > 1e-6f) dir = Vector3.Normalize(dir);
                lights.Dir = new DirectionalLight3D(dir, node.LightColor);
                lights.HasDir = true;
            }
            else if (node.MeshHandle != 0 && _meshes.TryGetValue(node.MeshHandle, out MeshGeometry3D? mesh))
            {
                models.Add(new Model3D(mesh, ResolveMaterial(node.MaterialHandle), m));
            }
        }

        private Matrix4x4 ResolveTransform3D(uint handle)
        {
            if (handle == 0) return Matrix4x4.Identity;
            if (_xform3D.TryGetValue(handle, out Matrix4x4 m)) return m;
            if (_rotateXforms.TryGetValue(handle, out RotateXform3D rt))
            {
                Matrix4x4 rot = Matrix4x4.Identity;
                if (_rotations.TryGetValue(rt.RotationHandle, out AxisAngle aa) && aa.Axis.LengthSquared() > 1e-6f)
                    rot = Matrix4x4.CreateFromAxisAngle(Vector3.Normalize(aa.Axis), aa.Angle * (MathF.PI / 180f));
                return Matrix4x4.CreateTranslation(-rt.Center) * rot * Matrix4x4.CreateTranslation(rt.Center);
            }
            if (_xform3DGroups.TryGetValue(handle, out List<uint>? children))
            {
                Matrix4x4 acc = Matrix4x4.Identity;
                foreach (uint c in children) acc *= ResolveTransform3D(c);
                return acc;
            }
            return Matrix4x4.Identity;
        }

        private RgbaColor ResolveMaterial(uint handle)
        {
            if (_materials.TryGetValue(handle, out MaterialDef def))
                return _solidBrushes.TryGetValue(def.Brush, out RgbaColor b) ? b : def.Color;
            return RgbaColor.FromBytes(204, 204, 204, 255);
        }

        private static Vector3[] ComputeNormals(Vector3[] positions, int[] indices)
        {
            var normals = new Vector3[positions.Length];
            for (int i = 0; i + 2 < indices.Length; i += 3)
            {
                int a = indices[i], b = indices[i + 1], c = indices[i + 2];
                if (a >= positions.Length || b >= positions.Length || c >= positions.Length) continue;
                Vector3 n = Vector3.Cross(positions[b] - positions[a], positions[c] - positions[a]);
                normals[a] += n; normals[b] += n; normals[c] += n;
            }
            for (int i = 0; i < normals.Length; i++)
                normals[i] = normals[i].LengthSquared() > 1e-6f ? Vector3.Normalize(normals[i]) : new Vector3(0, 0, 1);
            return normals;
        }
    }
}
