using System;
using System.Collections.Generic;
using CoreAI.Mods.Rbx.Datatypes;
using CoreAI.Mods.Rbx.Instances;
using CoreAI.Mods.Rbx.Instances.Adornments;
using CoreAI.Mods.Rbx.Spatial;
using UnityEngine;
using UnityEngine.Rendering;

namespace CoreAI.Mods.Rbx.Binding
{
    /// <summary>How <see cref="UnityRbxHighlightBackend"/> draws.</summary>
    public enum RbxHighlightDrawMode
    {
        /// <summary>Each part's own mesh, tinted, with a silhouette outline (the stencil shader).</summary>
        Outline = 0,

        /// <summary>Twelve edges of each part's box in the outline colour: where the outline shader cannot run.</summary>
        Wireframe = 1
    }

    /// <summary>One draw the backend submits for a frame (the test and diagnostics view of <see cref="UnityRbxHighlightBackend.Draw"/>).</summary>
    public readonly struct RbxHighlightDraw
    {
        public RbxHighlightDraw(InstanceId highlightId, InstanceId partId, bool outline, Matrix4x4 matrix, Color color)
        {
            HighlightId = highlightId;
            PartId = partId;
            IsOutline = outline;
            Matrix = matrix;
            Color = color;
        }

        public InstanceId HighlightId { get; }

        public InstanceId PartId { get; }

        /// <summary>True for an outline (or a wireframe edge), false for a fill.</summary>
        public bool IsOutline { get; }

        public Matrix4x4 Matrix { get; }

        public Color Color { get; }
    }

    /// <summary>
    /// The default <see cref="IRbxHighlightBackend"/>. In <see cref="RbxHighlightDrawMode.Outline"/> it draws every
    /// covered part's own mesh twice a frame with <c>Graphics.RenderMesh</c>: a tinted fill that marks the stencil, then
    /// the mesh grown by <see cref="OutlinePixels"/> where the stencil is not marked, which is the silhouette ring. It
    /// creates no GameObject and no collider, so rays, clicks and the camera never stop on a highlight. Where the
    /// shader is not supported (or <c>forceWireframe</c> is set) it draws twelve thin edges of each part's box in the
    /// outline colour instead.
    /// </summary>
    public sealed class UnityRbxHighlightBackend : IRbxHighlightBackend, IDisposable
    {
        /// <summary>The outline shader, under a Resources folder so every player build includes it.</summary>
        public const string ShaderName = "CoreAI/Rbx/Highlight";

        /// <summary>Outline width on screen.</summary>
        public const float OutlinePixels = 3f;

        /// <summary>Edge thickness of the wireframe fallback, studs.</summary>
        public const float WireframeThicknessStuds = 0.12f;

        private const int FillQueue = 3100;
        private const int OutlineQueue = 3101;

        private static readonly int ColorId = Shader.PropertyToID("_Color");
        private static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");
        private static readonly int OutlinePixelsId = Shader.PropertyToID("_OutlinePixels");
        private static readonly int ZTestId = Shader.PropertyToID("_ZTest");
        private static readonly int StencilCompId = Shader.PropertyToID("_StencilComp");
        private static readonly int StencilPassId = Shader.PropertyToID("_StencilPass");
        private static readonly int DepthOffsetId = Shader.PropertyToID("_DepthOffset");

        private readonly InstanceGameObjectBinder _binder;
        private readonly Dictionary<InstanceId, Entry> _entries = new();
        private readonly List<RbxHighlightDraw> _frame = new();
        private Material _fillOnTop;
        private Material _fillOccluded;
        private Material _outlineOnTop;
        private Material _outlineOccluded;
        private Material _wire;
        private Mesh _cube;

        /// <param name="binder">Maps part ids to their renderers and boxes.</param>
        /// <param name="forceWireframe">Draw the wireframe fallback even where the outline shader runs.</param>
        public UnityRbxHighlightBackend(InstanceGameObjectBinder binder, bool forceWireframe = false)
        {
            _binder = binder ?? throw new ArgumentNullException(nameof(binder));
            Shader shader = forceWireframe ? null : Shader.Find(ShaderName);
            Mode = shader != null && shader.isSupported ? RbxHighlightDrawMode.Outline : RbxHighlightDrawMode.Wireframe;
            if (Mode == RbxHighlightDrawMode.Outline)
            {
                _fillOnTop = NewMaterial(shader, "Highlight fill (on top)", FillQueue, CompareFunction.Always,
                    CompareFunction.Always, StencilOp.Replace, 0f);
                _fillOccluded = NewMaterial(shader, "Highlight fill (occluded)", FillQueue, CompareFunction.LessEqual,
                    CompareFunction.Always, StencilOp.Replace, 0f);
                _outlineOnTop = NewMaterial(shader, "Highlight outline (on top)", OutlineQueue, CompareFunction.Always,
                    CompareFunction.NotEqual, StencilOp.Keep, OutlinePixels);
                _outlineOccluded = NewMaterial(shader, "Highlight outline (occluded)", OutlineQueue,
                    CompareFunction.LessEqual, CompareFunction.NotEqual, StencilOp.Keep, OutlinePixels);
            }
        }

        /// <summary>Whether this backend draws outlines or the wireframe fallback.</summary>
        public RbxHighlightDrawMode Mode { get; }

        /// <summary>Highlights this backend currently draws.</summary>
        public int Count => _entries.Count;

        /// <summary>The draws of the last <see cref="Draw"/> (tests and diagnostics).</summary>
        public IReadOnlyList<RbxHighlightDraw> LastFrame => _frame;

        /// <inheritdoc />
        public void Apply(InstanceId highlightId, in RbxHighlightRenderState state)
        {
            if (!_entries.TryGetValue(highlightId, out Entry entry))
            {
                entry = new Entry();
                _entries[highlightId] = entry;
            }

            entry.State = state;
            entry.Parts.Clear();
            for (int index = 0; index < state.Parts.Count; index++)
            {
                entry.Parts.Add(new PartDraw { Id = state.Parts[index] });
            }

            entry.Fill ??= new MaterialPropertyBlock();
            entry.Outline ??= new MaterialPropertyBlock();
            Color fill = Tint(state.FillColor, state.FillTransparency);
            Color outline = Tint(state.OutlineColor, state.OutlineTransparency);
            entry.FillColor = fill;
            entry.OutlineColor = outline;
            entry.Fill.SetColor(ColorId, fill);
            entry.Fill.SetColor(BaseColorId, fill);
            entry.Outline.SetColor(ColorId, outline);
            entry.Outline.SetColor(BaseColorId, outline);
        }

        /// <inheritdoc />
        public void Release(InstanceId highlightId)
        {
            _entries.Remove(highlightId);
        }

        /// <summary>
        /// Submits this frame's draws; call once per rendered frame after the parts moved (the world host does, in
        /// LateUpdate). <paramref name="submit"/> false only fills <see cref="LastFrame"/> (edit-mode tests).
        /// </summary>
        public void Draw(bool submit = true)
        {
            _frame.Clear();
            foreach (KeyValuePair<InstanceId, Entry> pair in _entries)
            {
                Entry entry = pair.Value;
                bool onTop = entry.State.DepthMode == RbxHighlightDepthMode.AlwaysOnTop;
                for (int index = 0; index < entry.Parts.Count; index++)
                {
                    PartDraw part = entry.Parts[index];
                    if (Mode == RbxHighlightDrawMode.Outline)
                    {
                        DrawMesh(pair.Key, entry, ref part, onTop, submit);
                        entry.Parts[index] = part;
                    }
                    else
                    {
                        DrawWireframe(pair.Key, entry, part.Id, submit);
                    }
                }
            }
        }

        /// <summary>Destroys the materials and the edge mesh.</summary>
        public void Dispose()
        {
            _entries.Clear();
            _frame.Clear();
            DestroyObject(ref _fillOnTop);
            DestroyObject(ref _fillOccluded);
            DestroyObject(ref _outlineOnTop);
            DestroyObject(ref _outlineOccluded);
            DestroyObject(ref _wire);
            DestroyObject(ref _cube);
        }

        private void DrawMesh(InstanceId highlightId, Entry entry, ref PartDraw part, bool onTop, bool submit)
        {
            if (part.Renderer == null || part.Filter == null)
            {
                part.Renderer = null;
                part.Filter = null;
                if (!_binder.TryGetPartRenderer(part.Id, out Renderer renderer)
                    || !renderer.TryGetComponent(out MeshFilter filter))
                {
                    return;
                }

                part.Renderer = renderer;
                part.Filter = filter;
            }

            Mesh mesh = part.Filter.sharedMesh;
            if (mesh == null || !part.Renderer.enabled || !part.Renderer.gameObject.activeInHierarchy)
            {
                return;
            }

            Matrix4x4 matrix = part.Renderer.localToWorldMatrix;
            _frame.Add(new RbxHighlightDraw(highlightId, part.Id, false, matrix, entry.FillColor));
            _frame.Add(new RbxHighlightDraw(highlightId, part.Id, true, matrix, entry.OutlineColor));
            if (!submit)
            {
                return;
            }

            Bounds bounds = part.Renderer.bounds;
            bounds.Expand(1f);
            RenderParams fill = Params(onTop ? _fillOnTop : _fillOccluded, entry.Fill, bounds);
            RenderParams outline = Params(onTop ? _outlineOnTop : _outlineOccluded, entry.Outline, bounds);
            for (int sub = 0; sub < mesh.subMeshCount; sub++)
            {
                Graphics.RenderMesh(in fill, mesh, sub, matrix);
                Graphics.RenderMesh(in outline, mesh, sub, matrix);
            }
        }

        private void DrawWireframe(InstanceId highlightId, Entry entry, InstanceId partId, bool submit)
        {
            if (!_binder.TryGetPartProperties(partId, out PartProperties properties))
            {
                return;
            }

            EnsureWire();
            (Vector3 position, Quaternion rotation) = RbxSpace.ToUnityPose(properties.CFrame);
            Vector3 size = RbxSpace.SizeToUnity(properties.Size);
            Vector3 half = new(Mathf.Abs(size.x) * 0.5f, Mathf.Abs(size.y) * 0.5f, Mathf.Abs(size.z) * 0.5f);
            float thickness = RbxSpace.LengthToUnity(WireframeThicknessStuds);
            Matrix4x4 pose = Matrix4x4.TRS(position, rotation, Vector3.one);
            RenderParams wire = submit ? Params(_wire, entry.Outline, new Bounds(position, size + Vector3.one)) : default;
            for (int axis = 0; axis < 3; axis++)
            {
                for (int corner = 0; corner < 4; corner++)
                {
                    float a = (corner & 1) == 0 ? -1f : 1f;
                    float b = (corner & 2) == 0 ? -1f : 1f;
                    Vector3 centre;
                    Vector3 scale;
                    switch (axis)
                    {
                        case 0:
                            centre = new Vector3(0f, a * half.y, b * half.z);
                            scale = new Vector3(half.x * 2f + thickness, thickness, thickness);
                            break;
                        case 1:
                            centre = new Vector3(a * half.x, 0f, b * half.z);
                            scale = new Vector3(thickness, half.y * 2f + thickness, thickness);
                            break;
                        default:
                            centre = new Vector3(a * half.x, b * half.y, 0f);
                            scale = new Vector3(thickness, thickness, half.z * 2f + thickness);
                            break;
                    }

                    Matrix4x4 matrix = pose * Matrix4x4.TRS(centre, Quaternion.identity, scale);
                    _frame.Add(new RbxHighlightDraw(highlightId, partId, true, matrix, entry.OutlineColor));
                    if (submit)
                    {
                        Graphics.RenderMesh(in wire, _cube, 0, matrix);
                    }
                }
            }
        }

        private static RenderParams Params(Material material, MaterialPropertyBlock block, Bounds bounds)
        {
            return new RenderParams(material)
            {
                matProps = block,
                shadowCastingMode = ShadowCastingMode.Off,
                receiveShadows = false,
                worldBounds = bounds,
                lightProbeUsage = LightProbeUsage.Off,
                reflectionProbeUsage = ReflectionProbeUsage.Off
            };
        }

        private void EnsureWire()
        {
            if (_cube == null)
            {
                GameObject probe = GameObject.CreatePrimitive(PrimitiveType.Cube);
                // WHY hidden before the deferred destroy: in Play Mode it would otherwise draw for one frame as a grey
                // cube at the world origin.
                probe.SetActive(false);
                _cube = UnityEngine.Object.Instantiate(probe.GetComponent<MeshFilter>().sharedMesh);
                _cube.name = "Highlight edge";
                DestroyNow(probe);
            }

            if (_wire == null)
            {
                // WHY Sprites/Default: the fallback runs where the outline shader does not, so it takes the unlit,
                // tintable shader every player build includes, as the selection boxes do.
                Shader shader = Shader.Find("Sprites/Default") ?? Shader.Find("Unlit/Color");
                _wire = new Material(shader) { name = "Highlight wireframe", renderQueue = OutlineQueue };
            }
        }

        private static Material NewMaterial(Shader shader, string name, int queue, CompareFunction depth,
            CompareFunction stencil, StencilOp pass, float outlinePixels)
        {
            Material material = new(shader) { name = name, renderQueue = queue };
            material.SetFloat(ZTestId, (float)depth);
            material.SetFloat(StencilCompId, (float)stencil);
            material.SetFloat(StencilPassId, (float)pass);
            material.SetFloat(OutlinePixelsId, outlinePixels);
            // WHY a pull toward the camera only for the occluded fill: it draws the part's own surface again under a
            // depth test, and without the offset the two surfaces would fight over the same depth.
            material.SetFloat(DepthOffsetId, depth == CompareFunction.LessEqual && outlinePixels <= 0f ? -1f : 0f);
            return material;
        }

        private static Color Tint(RbxColor3 color, float transparency)
        {
            return new Color(color.R, color.G, color.B, 1f - Mathf.Clamp01(transparency));
        }

        private static void DestroyObject<T>(ref T target) where T : UnityEngine.Object
        {
            if (target != null)
            {
                DestroyNow(target);
            }

            target = null;
        }

        private static void DestroyNow(UnityEngine.Object target)
        {
            if (Application.isPlaying)
            {
                UnityEngine.Object.Destroy(target);
            }
            else
            {
                UnityEngine.Object.DestroyImmediate(target);
            }
        }

        private struct PartDraw
        {
            public InstanceId Id;
            public Renderer Renderer;
            public MeshFilter Filter;
        }

        private sealed class Entry
        {
            public RbxHighlightRenderState State;
            public readonly List<PartDraw> Parts = new();
            public MaterialPropertyBlock Fill;
            public MaterialPropertyBlock Outline;
            public Color FillColor;
            public Color OutlineColor;
        }
    }
}
