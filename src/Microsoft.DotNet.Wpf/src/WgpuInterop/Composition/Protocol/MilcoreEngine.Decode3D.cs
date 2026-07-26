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
        private readonly Dictionary<uint, System.Numerics.Quaternion> _quatRotations = new(); // QuaternionRotation3D
        private readonly Dictionary<uint, Model3DNode> _models3D = new();
        private readonly Dictionary<uint, Visual3DNode> _visuals3D = new();
        private readonly Dictionary<uint, Viewport3DState> _viewports3D = new();

        // Kind: 0 diffuse, 1 specular, 2 emissive, 3 group.
        private struct MaterialDef
        {
            public int Kind;
            public RgbaColor Color;
            public float SpecularPower;
            public uint Brush;
            public List<uint>? GroupChildren;
        }
        private struct AxisAngle { public Vector3 Axis; public float Angle; }
        private struct RotateXform3D { public Vector3 Center; public uint RotationHandle; }

        private sealed class Model3DNode
        {
            public uint TransformHandle;
            public List<uint>? GroupChildren;       // Model3DGroup
            public uint MeshHandle, MaterialHandle, BackMaterialHandle;  // GeometryModel3D
            public int LightKind;                    // 1 ambient, 2 directional, 3 point, 4 spot
            public RgbaColor LightColor;
            public Vector3 LightDir;
            public Vector3 LightPos;
            public float Range, ConstAtten, LinearAtten, QuadAtten, InnerConeDeg, OuterConeDeg;
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
            public readonly List<Light3D> Lights = new();
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
                case Mil.OrthographicCamera:
                {
                    // MILCMD_ORTHOGRAPHICCAMERA: near@8, far@16, width@24, position@32, htransform@44,
                    // lookDirection@48, hNear@60, upDirection@64.
                    uint h = r.U32();
                    double near = r.F64(), far = r.F64(), w = r.F64();
                    Vector3 pos = Pt3(ref r);
                    r.U32();                 // htransform
                    Vector3 look = Pt3(ref r);
                    r.U32();                 // hNearPlaneDistanceAnimations
                    Vector3 up = Pt3(ref r);
                    _cameras[h] = new Camera3D(pos, look, up, 0f, (float)Math.Max(near, 0.001), (float)far, orthographic: true, width: (float)w);
                    break;
                }
                case Mil.MatrixCamera:
                {
                    // MILCMD_MATRIXCAMERA: viewMatrix (D3DMATRIX, 16 floats), projectionMatrix
                    // (16 floats), htransform. The matrices are used verbatim (WPF applies no
                    // viewport-aspect correction for MatrixCamera).
                    uint h = r.U32();
                    Matrix4x4 view = Mat16(ref r), proj = Mat16(ref r);
                    r.U32();                 // htransform (ignored, like the other cameras)
                    _cameras[h] = new Camera3D(view, proj);
                    break;

                    static Matrix4x4 Mat16(ref MilReader rr) => new(
                        rr.F32(), rr.F32(), rr.F32(), rr.F32(),
                        rr.F32(), rr.F32(), rr.F32(), rr.F32(),
                        rr.F32(), rr.F32(), rr.F32(), rr.F32(),
                        rr.F32(), rr.F32(), rr.F32(), rr.F32());
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
                case Mil.PointLight:
                {
                    // MILCMD_POINTLIGHT: color@8, range@24, const@32, linear@40, quad@48, position@56.
                    uint h = r.U32(); RgbaColor c = Col(ref r);
                    double range = r.F64(), ca = r.F64(), la = r.F64(), qa = r.F64();
                    Vector3 pos = Pt3(ref r);
                    _models3D[h] = new Model3DNode
                    {
                        LightKind = 3, LightColor = c, LightPos = pos,
                        Range = (float)range, ConstAtten = (float)ca, LinearAtten = (float)la, QuadAtten = (float)qa,
                    };
                    break;
                }
                case Mil.SpotLight:
                {
                    // MILCMD_SPOTLIGHT: color@8, range@24, const@32, linear@40, quad@48, outerCone@56,
                    // innerCone@64, position@72, htransform@84, direction@88.
                    uint h = r.U32(); RgbaColor c = Col(ref r);
                    double range = r.F64(), ca = r.F64(), la = r.F64(), qa = r.F64();
                    double outer = r.F64(), inner = r.F64();
                    Vector3 pos = Pt3(ref r);
                    r.U32();                 // htransform
                    Vector3 dir = Pt3(ref r);
                    _models3D[h] = new Model3DNode
                    {
                        LightKind = 4, LightColor = c, LightPos = pos, LightDir = dir,
                        Range = (float)range, ConstAtten = (float)ca, LinearAtten = (float)la, QuadAtten = (float)qa,
                        InnerConeDeg = (float)inner, OuterConeDeg = (float)outer,
                    };
                    break;
                }
                case Mil.GeometryModel3D:
                {
                    uint h = r.U32();
                    uint htransform = r.U32(), hgeometry = r.U32(), hmaterial = r.U32(), hback = r.U32();
                    _models3D[h] = new Model3DNode { TransformHandle = htransform, MeshHandle = hgeometry, MaterialHandle = hmaterial, BackMaterialHandle = hback };
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
                    // MilPoint2D (2 DOUBLES) per coord -- unlike positions/normals (MilPoint3F floats).
                    var texCoords = new Vector2[texSize / 16];
                    for (int i = 0; i < texCoords.Length; i++) { float u = (float)r.F64(), vv = (float)r.F64(); texCoords[i] = new Vector2(u, vv); }
                    var indices = new int[idxSize / 4];
                    for (int i = 0; i < indices.Length; i++) indices[i] = (int)r.U32();
                    if (normals.Length != positions.Length) normals = ComputeNormals(positions, indices);
                    _meshes[h] = new MeshGeometry3D(positions, normals, indices, texCoords);
                    break;
                }
                case Mil.DiffuseMaterial:
                {
                    uint h = r.U32();
                    RgbaColor color = Col(ref r);
                    Col(ref r);                  // ambientColor (folded into the viewport ambient instead)
                    uint hbrush = r.U32();
                    _materials[h] = new MaterialDef { Kind = 0, Color = color, Brush = hbrush };
                    break;
                }
                case Mil.SpecularMaterial:
                {
                    // MILCMD_SPECULARMATERIAL: color@8, specularPower@24, hbrush@32.
                    uint h = r.U32();
                    RgbaColor color = Col(ref r);
                    double power = r.F64();
                    uint hbrush = r.U32();
                    _materials[h] = new MaterialDef { Kind = 1, Color = color, SpecularPower = (float)power, Brush = hbrush };
                    break;
                }
                case Mil.EmissiveMaterial:
                {
                    // MILCMD_EMISSIVEMATERIAL: color@8, hbrush@24.
                    uint h = r.U32();
                    RgbaColor color = Col(ref r);
                    uint hbrush = r.U32();
                    _materials[h] = new MaterialDef { Kind = 2, Color = color, Brush = hbrush };
                    break;
                }
                case Mil.MaterialGroup:
                {
                    // MILCMD_MATERIALGROUP: ChildrenSize@8, then child handles.
                    uint h = r.U32();
                    uint childrenSize = r.U32();
                    var children = new List<uint>();
                    for (uint i = 0; i + 4 <= childrenSize; i += 4) children.Add(r.U32());
                    _materials[h] = new MaterialDef { Kind = 3, GroupChildren = children };
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
                case Mil.QuaternionRotation3D:
                {
                    // MILCMD_QUATERNIONROTATION3D: Handle@4, MilQuaternionF quaternion@8 (x,y,z,w as float32).
                    // Used by trackball-style interactive rotation (RotateTransform3D + QuaternionRotation3D);
                    // without this case the rotation handle resolved to nothing and the model never rotated.
                    uint h = r.U32();
                    float qx = r.F32(), qy = r.F32(), qz = r.F32(), qw = r.F32();
                    _quatRotations[h] = new System.Numerics.Quaternion(qx, qy, qz, qw);
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
            _brushes3DLive.Clear();   // re-collected below; RealizeContentBrushes (which runs first) uses last frame's set
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
                v.Content.Add(new Viewport3DDraw(cam, lights.Lights, lights.Ambient, models, vp.Viewport));
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
                lights.Lights.Add(Light3D.Directional(dir, node.LightColor));
            }
            else if (node.LightKind == 3)
            {
                Vector3 pos = Vector3.Transform(node.LightPos, m);
                lights.Lights.Add(Light3D.Point(pos, node.LightColor, node.Range, node.ConstAtten, node.LinearAtten, node.QuadAtten));
            }
            else if (node.LightKind == 4)
            {
                Vector3 pos = Vector3.Transform(node.LightPos, m);
                Vector3 dir = Vector3.TransformNormal(node.LightDir, m);
                float innerCos = MathF.Cos(node.InnerConeDeg * (MathF.PI / 180f) * 0.5f);
                float outerCos = MathF.Cos(node.OuterConeDeg * (MathF.PI / 180f) * 0.5f);
                lights.Lights.Add(new Light3D(Light3DKind.Spot, node.LightColor, dir, pos,
                    node.Range, node.ConstAtten, node.LinearAtten, node.QuadAtten, innerCos, outerCos));
            }
            else if (node.MeshHandle != 0 && _meshes.TryGetValue(node.MeshHandle, out MeshGeometry3D? mesh))
            {
                // WPF sidedness: Material = front faces, BackMaterial = back faces; a side without
                // a material is culled (a model with neither draws nothing).
                bool hasFront = node.MaterialHandle != 0, hasBack = node.BackMaterialHandle != 0;
                if (hasFront || hasBack)
                    models.Add(new Model3D(mesh,
                        hasFront ? ResolveMaterial(node.MaterialHandle) : default, hasFront,
                        hasBack ? ResolveMaterial(node.BackMaterialHandle) : default, hasBack, m));
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
                else if (_quatRotations.TryGetValue(rt.RotationHandle, out System.Numerics.Quaternion q) && q.LengthSquared() > 1e-12f)
                    rot = Matrix4x4.CreateFromQuaternion(System.Numerics.Quaternion.Normalize(q));
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

        private struct MatAcc
        {
            public RgbaColor Diffuse, Specular, Emissive;
            public float SpecPower;
            public byte[]? TexPx; public int TexW, TexH;
            public SceneVisual? TexVisual; public Rect TexBounds;
            public bool EmissiveTex;
            public bool SawDiffuse, SawSpecular, SawEmissive;
        }

        // Resolves a WPF material (Diffuse/Specular/Emissive, possibly a MaterialGroup) into the
        // renderer's combined Material3D, including a diffuse texture when the diffuse brush is an
        // image / rasterized visual brush.
        private Material3D ResolveMaterial(uint handle)
        {
            if (!_materials.ContainsKey(handle))
                return Material3D.Diffuse3D(RgbaColor.FromBytes(204, 204, 204, 255));
            var acc = new MatAcc
            {
                Diffuse = new RgbaColor(0, 0, 0, 1),
                Specular = new RgbaColor(0, 0, 0, 0),
                Emissive = new RgbaColor(0, 0, 0, 0),
                SpecPower = 1f,
            };
            AccMaterial(handle, ref acc);
            // EmissiveMaterial with no Diffuse/Specular material -> additive, unlit (WPF glow semantics).
            bool emissiveOnly = acc.SawEmissive && !acc.SawDiffuse && !acc.SawSpecular;
            return new Material3D(acc.Diffuse, acc.Specular, acc.SpecPower, acc.Emissive,
                acc.TexPx, acc.TexW, acc.TexH, acc.TexVisual, acc.TexBounds, acc.EmissiveTex, emissiveOnly);
        }

        private void AccMaterial(uint handle, ref MatAcc acc)
        {
            if (!_materials.TryGetValue(handle, out MaterialDef def)) return;
            switch (def.Kind)
            {
                case 0:   // diffuse (colour and/or texture)
                    acc.SawDiffuse = true;
                    acc.Diffuse = _solidBrushes.TryGetValue(def.Brush, out RgbaColor d) ? d : def.Color;
                    // A VisualBrush/DrawingBrush -> render its LIVE 2D content into a GPU texture in
                    // the 3D pass (interactive 2D-in-3D). A plain image brush -> upload its pixels.
                    (SceneVisual? tv, Rect tb) = ResolveTextureVisual(def.Brush);
                    if (tv is not null) { acc.TexVisual = tv; acc.TexBounds = tb; }
                    else
                    {
                        (byte[]? px, int tw, int th) = ResolveTexture(def.Brush);
                        if (px is not null) { acc.TexPx = px; acc.TexW = tw; acc.TexH = th; }
                    }
                    // A textured diffuse surface: the texture IS the albedo, so the modulating diffuse
                    // colour must be WHITE (a non-solid brush leaves def.Color black, which would
                    // multiply the texture to black in the shader).
                    if (acc.TexVisual is not null || acc.TexPx is not null)
                        acc.Diffuse = new RgbaColor(1, 1, 1, 1);
                    break;
                case 1:   // specular
                    acc.SawSpecular = true;
                    acc.Specular = _solidBrushes.TryGetValue(def.Brush, out RgbaColor s) ? s : def.Color;
                    acc.SpecPower = def.SpecularPower;
                    break;
                case 2:   // emissive (solid, or a textured/visual brush -> self-lit texture)
                {
                    acc.SawEmissive = true;
                    (SceneVisual? etv, Rect etb) = ResolveTextureVisual(def.Brush);
                    (byte[]? epx, int ew, int eh) = etv is null ? ResolveTexture(def.Brush) : (null, 0, 0);
                    if (etv is not null || epx is not null)
                    {
                        acc.EmissiveTex = true;
                        acc.Emissive = new RgbaColor(1, 1, 1, 1);   // full-brightness texture
                        if (acc.TexVisual is null && acc.TexPx is null)   // share as the diffuse texture
                        {
                            if (etv is not null) { acc.TexVisual = etv; acc.TexBounds = etb; }
                            else { acc.TexPx = epx; acc.TexW = ew; acc.TexH = eh; }
                            acc.Diffuse = new RgbaColor(1, 1, 1, 1);   // texture is the albedo
                        }
                    }
                    else
                        acc.Emissive = _solidBrushes.TryGetValue(def.Brush, out RgbaColor e) ? e : def.Color;
                    break;
                }
                case 3:   // group: later children layer over earlier
                    if (def.GroupChildren is not null)
                        foreach (uint c in def.GroupChildren) AccMaterial(c, ref acc);
                    break;
            }
        }

        // For a VisualBrush/DrawingBrush diffuse brush, returns the LIVE 2D source wrapped so its
        // content bounds map to a [0,pw]x[0,ph] texture, plus that target size. The renderer draws
        // this into a GPU texture each frame (no CPU readback) -> live/interactive 2D-in-3D.
        private (SceneVisual?, Rect) ResolveTextureVisual(uint brushHandle)
        {
            if (brushHandle == 0 || !_contentBrushes.TryGetValue(brushHandle, out (uint Source, bool IsDrawing) cb))
                return (null, default);
            SceneVisual? source = cb.IsDrawing
                ? BuildDrawingVisual(cb.Source)
                : (_visuals.TryGetValue(cb.Source, out SceneVisual? v) ? v : null);
            if (source is null)
            {
                if (s_dbg3d) System.Console.WriteLine($"3D-TEXVIS brush={brushHandle} src={cb.Source} NOT FOUND (drawing={cb.IsDrawing})");
                return (null, default);
            }
            Rect b = VisualSubtreeBounds(source, source.LocalToParent);
            if (s_dbg3d) System.Console.WriteLine($"3D-TEXVIS brush={brushHandle} src={cb.Source} kids={source.Children.Count} content={source.Content.Count} bounds={b.X},{b.Y} {b.Width}x{b.Height}");
            if (b.Width <= 0.01f || b.Height <= 0.01f) return (null, default);

            _brushes3DLive.Add(brushHandle);
            const int supersample = 2;
            int pw = Math.Clamp((int)MathF.Ceiling(b.Width * supersample), 1, 1024);
            int ph = Math.Clamp((int)MathF.Ceiling(b.Height * supersample), 1, 1024);
            var wrapper = new SceneVisual
            {
                Transform = Matrix3x2.CreateTranslation(-b.X, -b.Y) * Matrix3x2.CreateScale(pw / b.Width, ph / b.Height),
            };
            wrapper.Children.Add(source);
            return (wrapper, new Rect(0, 0, pw, ph));
        }

        private static readonly bool s_dbg3d = Environment.GetEnvironmentVariable("WPF_DBG_3DTEX") == "1";

        // Resolves a brush handle to raw texture pixels (image brush, or a visual brush already
        // rasterized into _bitmaps). Returns (null, 0, 0) for solid/gradient/unknown brushes.
        private (byte[]?, int, int) ResolveTexture(uint brushHandle)
        {
            if (brushHandle == 0) return (null, 0, 0);
            if (_imageBrushes.TryGetValue(brushHandle, out MilImageBrush ib))
            {
                if (_bitmaps.TryGetValue(ib.ImageHandle, out MilBitmap bmp))
                    return (bmp.Rgba, bmp.Width, bmp.Height);
                if (s_dbg3d) System.Console.WriteLine($"3D-TEX brush={brushHandle} imgHandle={ib.ImageHandle} NOT in _bitmaps (bitmaps={_bitmaps.Count}) content={_contentBrushes.ContainsKey(brushHandle)}");
            }
            else if (s_dbg3d)
                System.Console.WriteLine($"3D-TEX brush={brushHandle} not imageBrush; solid={_solidBrushes.ContainsKey(brushHandle)} content={_contentBrushes.ContainsKey(brushHandle)} imgBrushes={_imageBrushes.Count}");
            return (null, 0, 0);
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
