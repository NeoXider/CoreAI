using System;
using System.Collections.Generic;
using CoreAI.Mods.Rbx.Datatypes;
using CoreAI.Mods.Rbx.Instances;
using CoreAI.Mods.Rbx.Instances.Adornments;
using CoreAI.Mods.Rbx.Spatial;
using UnityEngine;

namespace CoreAI.Mods.Rbx.Binding
{
    /// <summary>
    /// The default <see cref="IRbxSelectionBoxBackend"/>: one object per drawn box holding twelve thin unlit cubes on
    /// its edges and, while <c>SurfaceTransparency</c> is below 1, one cube on its faces. No object has a collider, so
    /// rays, clicks and the camera's pull-in never stop on a selection box.
    /// </summary>
    public sealed class UnityRbxSelectionBoxBackend : IRbxSelectionBoxBackend, IDisposable
    {
        /// <summary>Name of the object that holds every drawn box.</summary>
        public const string RootName = "Rbx SelectionBoxes";

        private static readonly int ColorPropertyId = Shader.PropertyToID("_Color");
        private static readonly int BaseColorPropertyId = Shader.PropertyToID("_BaseColor");

        private readonly Dictionary<InstanceId, Entry> _entries = new();
        private readonly MaterialPropertyBlock _block = new();
        private GameObject _root;
        private Material _material;
        private Mesh _cube;

        /// <summary>Boxes this backend currently draws.</summary>
        public int Count => _entries.Count;

        /// <summary>The object drawn for <paramref name="boxId"/>, or null (tests and diagnostics).</summary>
        public GameObject GetDrawn(InstanceId boxId)
        {
            return _entries.TryGetValue(boxId, out Entry entry) ? entry.Root : null;
        }

        /// <inheritdoc />
        public void Apply(InstanceId boxId, in RbxSelectionBoxRenderState state)
        {
            if (!_entries.TryGetValue(boxId, out Entry entry) || entry.Root == null)
            {
                entry = Create(boxId);
                _entries[boxId] = entry;
            }

            (Vector3 position, Quaternion rotation) = RbxSpace.ToUnityPose(state.CFrame);
            entry.Root.transform.SetPositionAndRotation(position, rotation);
            Vector3 size = RbxSpace.SizeToUnity(state.Size);
            Vector3 half = new(Mathf.Abs(size.x) * 0.5f, Mathf.Abs(size.y) * 0.5f, Mathf.Abs(size.z) * 0.5f);
            float thickness = Mathf.Max(0f, RbxSpace.LengthToUnity(state.LineThickness));
            int edge = 0;
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

                    Transform line = entry.Edges[edge++].transform;
                    line.localPosition = centre;
                    line.localScale = scale;
                }
            }

            bool edgesShown = state.Transparency < 1f && thickness > 0f;
            Paint(entry.EdgeRenderers, state.Color, state.Transparency, edgesShown);
            entry.Surface.transform.localScale = new Vector3(half.x * 2f, half.y * 2f, half.z * 2f);
            Paint(entry.SurfaceRenderer, state.SurfaceColor, state.SurfaceTransparency, state.SurfaceTransparency < 1f);
        }

        /// <inheritdoc />
        public void Release(InstanceId boxId)
        {
            if (_entries.Remove(boxId, out Entry entry) && entry.Root != null)
            {
                DestroyObject(entry.Root);
            }
        }

        /// <summary>Destroys every drawn box and the shared material and mesh.</summary>
        public void Dispose()
        {
            foreach (KeyValuePair<InstanceId, Entry> pair in _entries)
            {
                if (pair.Value.Root != null)
                {
                    DestroyObject(pair.Value.Root);
                }
            }

            _entries.Clear();
            if (_root != null)
            {
                DestroyObject(_root);
                _root = null;
            }

            if (_material != null)
            {
                DestroyObject(_material);
                _material = null;
            }

            if (_cube != null)
            {
                DestroyObject(_cube);
                _cube = null;
            }
        }

        private Entry Create(InstanceId boxId)
        {
            EnsureShared();
            GameObject root = new("SelectionBox " + boxId.Value);
            root.transform.SetParent(_root.transform, false);
            Entry entry = new() { Root = root };
            for (int index = 0; index < 12; index++)
            {
                GameObject line = NewCube("Edge " + index, root.transform, out MeshRenderer renderer);
                entry.Edges[index] = line;
                entry.EdgeRenderers[index] = renderer;
            }

            entry.Surface = NewCube("Surface", root.transform, out MeshRenderer surface);
            entry.SurfaceRenderer = surface;
            return entry;
        }

        private GameObject NewCube(string name, Transform parent, out MeshRenderer renderer)
        {
            GameObject cube = new(name);
            cube.transform.SetParent(parent, false);
            cube.AddComponent<MeshFilter>().sharedMesh = _cube;
            renderer = cube.AddComponent<MeshRenderer>();
            renderer.sharedMaterial = _material;
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            renderer.receiveShadows = false;
            return cube;
        }

        private void Paint(MeshRenderer[] renderers, RbxColor3 color, float transparency, bool shown)
        {
            for (int index = 0; index < renderers.Length; index++)
            {
                Paint(renderers[index], color, transparency, shown);
            }
        }

        private void Paint(MeshRenderer renderer, RbxColor3 color, float transparency, bool shown)
        {
            renderer.enabled = shown;
            if (!shown)
            {
                return;
            }

            Color tint = new(color.R, color.G, color.B, 1f - Mathf.Clamp01(transparency));
            renderer.GetPropertyBlock(_block);
            _block.SetColor(ColorPropertyId, tint);
            _block.SetColor(BaseColorPropertyId, tint);
            renderer.SetPropertyBlock(_block);
        }

        private void EnsureShared()
        {
            if (_root == null)
            {
                _root = new GameObject(RootName);
            }

            if (_cube == null)
            {
                GameObject probe = GameObject.CreatePrimitive(PrimitiveType.Cube);
                _cube = UnityEngine.Object.Instantiate(probe.GetComponent<MeshFilter>().sharedMesh);
                _cube.name = "SelectionBox edge";
                DestroyObject(probe);
            }

            if (_material == null)
            {
                // WHY Sprites/Default: an unlit, alpha-blended, tintable shader every player build includes, on the
                // built-in pipeline and on URP alike, so a selection box never depends on a project asset.
                Shader shader = Shader.Find("Sprites/Default") ?? Shader.Find("Unlit/Color");
                _material = new Material(shader) { name = "SelectionBox", renderQueue = 3100 };
            }
        }

        private static void DestroyObject(UnityEngine.Object target)
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

        private sealed class Entry
        {
            public GameObject Root;
            public readonly GameObject[] Edges = new GameObject[12];
            public readonly MeshRenderer[] EdgeRenderers = new MeshRenderer[12];
            public GameObject Surface;
            public MeshRenderer SurfaceRenderer;
        }
    }
}
