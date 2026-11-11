using System;
using System.Collections.Generic;
using CoreAI.Logging;
using CoreAI.Mods.Rbx.Datatypes;
using CoreAI.Mods.Rbx.Instances;
using CoreAI.Mods.Rbx.Instances.Terrain;
using CoreAI.Mods.Rbx.Rendering;
using CoreAI.Mods.Rbx.Spatial;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace CoreAI.Mods.Rbx.Binding.Terrain
{
    /// <summary>
    /// The default Terrain renderer: per chunk one GameObject with a MeshFilter, a MeshRenderer and a
    /// MeshCollider for the solid faces, and a child with a transparent Water surface and no collider.
    /// A store change queues only the chunks its <see cref="TerrainChangeSet.RemeshChunks"/> names;
    /// <see cref="ProcessDirty"/> re-meshes them under a budget on the main thread.
    /// </summary>
    /// <remarks>
    /// Materials are the shared handles of the existing material catalog (the same Grass a Part shows),
    /// one submesh per material, with no MaterialPropertyBlock, so every chunk stays on the SRP Batcher
    /// path and draw calls grow with visible chunks and the materials in them, never with cells.
    /// WHY no jobs: the same code runs in a WebGL player, which has no worker threads, and a chunk
    /// meshes in a fraction of a millisecond; baking colliders off-thread is a desktop-only option.
    /// </remarks>
    public sealed class RbxTerrainRenderer : IRbxTerrainRenderer
    {
        private readonly RbxTerrain _terrain;
        private readonly IRbxMaterialProvider<Material> _materials;
        private readonly bool _ownsMaterials;
        private readonly ITerrainChunkMesher _mesher;
        private readonly ILog _log;
        private readonly GameObject _root;
        private readonly TerrainChunkMeshBuffers _buffers = new();
        private readonly RbxTerrainMeshBuilder _builder = new();
        private readonly Queue<TerrainChunkCoord> _dirty = new();
        private readonly HashSet<long> _dirtyKeys = new();
        private readonly Dictionary<long, ChunkView> _views = new();
        private readonly Dictionary<Collider, TerrainChunkCoord> _colliders = new();
        private readonly Dictionary<int, Material[]> _materialSets = new();
        private readonly Material[] _slotMaterials = new Material[TerrainMaterials.SlotCount];
        private readonly List<TerrainChunkCoord> _lastProcessed = new();
        private readonly List<TerrainChunkCoord> _coordScratch = new();
        private bool _disposed;

        /// <summary>
        /// Draws <paramref name="terrain"/> under <paramref name="parent"/>. A null
        /// <paramref name="materials"/> uses the hybrid texture/procedural catalog; a null
        /// <paramref name="mesher"/> uses <see cref="BlockyTerrainChunkMesher"/>. Every chunk already
        /// in the store is queued, so a loaded world appears without a write.
        /// </summary>
        public RbxTerrainRenderer(RbxTerrain terrain, Transform parent,
            IRbxMaterialProvider<Material> materials = null, ITerrainChunkMesher mesher = null, ILog log = null)
        {
            _terrain = terrain ?? throw new ArgumentNullException(nameof(terrain));
            _ownsMaterials = materials == null;
            _materials = materials ?? new RbxTextureMaterialProvider();
            _mesher = mesher ?? new BlockyTerrainChunkMesher();
            _log = log;
            _root = new GameObject("Terrain Chunks");
            if (parent != null)
            {
                _root.transform.SetParent(parent, false);
            }

            _root.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
            _terrain.Store.Changed += OnStoreChanged;
            _terrain.Store.CopyChunkCoords(_coordScratch);
            for (int index = 0; index < _coordScratch.Count; index++)
            {
                Enqueue(_coordScratch[index]);
            }
        }

        /// <inheritdoc />
        public RbxTerrain Terrain => _terrain;

        /// <inheritdoc />
        public int PendingChunkCount => _dirty.Count;

        /// <summary>The GameObject every chunk object sits under.</summary>
        public GameObject Root => _root;

        /// <summary>Chunks that currently have a rendered or colliding object.</summary>
        public int ChunkObjectCount => _views.Count;

        /// <summary>Chunk re-meshes since construction (a PM/EM counter).</summary>
        public long RemeshCount { get; private set; }

        /// <summary>The chunks the last <see cref="ProcessDirty"/> call re-meshed; reused between calls.</summary>
        public IReadOnlyList<TerrainChunkCoord> LastProcessedChunks => _lastProcessed;

        /// <inheritdoc />
        public int ProcessDirty(int maxChunks, double maxMilliseconds)
        {
            _lastProcessed.Clear();
            if (_disposed || _dirty.Count == 0)
            {
                return 0;
            }

            double start = Time.realtimeSinceStartupAsDouble;
            int processed = 0;
            while (_dirty.Count > 0 && (processed == 0
                                        || (processed < maxChunks
                                            && (Time.realtimeSinceStartupAsDouble - start) * 1000d < maxMilliseconds)))
            {
                TerrainChunkCoord coord = _dirty.Dequeue();
                _dirtyKeys.Remove(coord.Key);
                Remesh(coord);
                _lastProcessed.Add(coord);
                processed++;
            }

            return processed;
        }

        /// <summary>The object of a chunk, or false when the chunk draws nothing.</summary>
        public bool TryGetChunkObject(TerrainChunkCoord coord, out GameObject chunkObject)
        {
            if (_views.TryGetValue(coord.Key, out ChunkView view))
            {
                chunkObject = view.Solid;
                return true;
            }

            chunkObject = null;
            return false;
        }

        /// <inheritdoc />
        public bool TryResolveHit(Collider collider, Vector3 pointUnity, Vector3 normalUnity,
            out InstanceId terrainId, out RbxMaterialId material, out RbxVector3 normalStuds)
        {
            terrainId = default;
            material = default;
            normalStuds = default;
            if (_disposed || collider == null || !_colliders.ContainsKey(collider))
            {
                return false;
            }

            if (!TerrainRaycastMath.TryResolveHit(_terrain.Store, RbxSpace.FromUnity(pointUnity),
                    RbxSpace.DirectionFromUnity(normalUnity), out int _, out int _, out int _, out byte slot,
                    out normalStuds))
            {
                return false;
            }

            terrainId = _terrain.Id;
            material = TerrainMaterials.ToMaterialId(slot);
            return true;
        }

        /// <summary>Stops observing the store and destroys every chunk object and mesh.</summary>
        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _terrain.Store.Changed -= OnStoreChanged;
            foreach (KeyValuePair<long, ChunkView> pair in _views)
            {
                DestroyView(pair.Value);
            }

            _views.Clear();
            _colliders.Clear();
            _dirty.Clear();
            _dirtyKeys.Clear();
            DestroyObject(_root);
            if (_ownsMaterials && _materials is IDisposable disposable)
            {
                disposable.Dispose();
            }
        }

        private void OnStoreChanged(TerrainChangeSet change)
        {
            IReadOnlyList<TerrainChunkCoord> remesh = change.RemeshChunks;
            for (int index = 0; index < remesh.Count; index++)
            {
                Enqueue(remesh[index]);
            }
        }

        private void Enqueue(TerrainChunkCoord coord)
        {
            if (_dirtyKeys.Add(coord.Key))
            {
                _dirty.Enqueue(coord);
            }
        }

        private void Remesh(TerrainChunkCoord coord)
        {
            RemeshCount++;
            _mesher.BuildChunk(_terrain.Store, coord, _buffers);
            long key = coord.Key;
            if (_buffers.QuadCount == 0)
            {
                if (_views.TryGetValue(key, out ChunkView empty))
                {
                    DestroyView(empty);
                    _views.Remove(key);
                }

                return;
            }

            if (!_views.TryGetValue(key, out ChunkView view))
            {
                view = CreateView(coord);
                _views.Add(key, view);
            }

            int solidFaces = _builder.BuildSolid(_buffers, view.SolidMesh, out int slotMask);
            bool hasSolid = solidFaces > 0;
            view.Renderer.enabled = hasSolid;
            if (hasSolid)
            {
                view.Renderer.sharedMaterials = MaterialsFor(slotMask);
            }

            // WHY null first: assigning the same Mesh again does not re-cook it, so the collider would
            // keep the shape from before this write.
            view.Collider.sharedMesh = null;
            if (hasSolid)
            {
                view.Collider.sharedMesh = view.SolidMesh;
            }

            view.Collider.enabled = hasSolid;
            int waterFaces = _buffers.GetQuads(TerrainMaterials.Water).Count;
            if (waterFaces > 0)
            {
                EnsureWater(view, coord);
                _builder.BuildWater(_buffers, view.WaterMesh);
                view.Water.SetActive(true);
            }
            else if (view.Water != null)
            {
                view.Water.SetActive(false);
                view.WaterMesh.Clear();
            }
        }

        private ChunkView CreateView(TerrainChunkCoord coord)
        {
            GameObject solid = new("Chunk " + coord);
            solid.transform.SetParent(_root.transform, false);
            Mesh mesh = new() { name = "Terrain " + coord };
            mesh.MarkDynamic();
            solid.AddComponent<MeshFilter>().sharedMesh = mesh;
            MeshRenderer renderer = solid.AddComponent<MeshRenderer>();
            MeshCollider collider = solid.AddComponent<MeshCollider>();
            _colliders[collider] = coord;
            return new ChunkView(solid, mesh, renderer, collider);
        }

        private void EnsureWater(ChunkView view, TerrainChunkCoord coord)
        {
            if (view.Water != null)
            {
                return;
            }

            GameObject water = new("Water " + coord);
            water.transform.SetParent(view.Solid.transform, false);
            Mesh mesh = new() { name = "Terrain water " + coord };
            mesh.MarkDynamic();
            water.AddComponent<MeshFilter>().sharedMesh = mesh;
            MeshRenderer renderer = water.AddComponent<MeshRenderer>();
            renderer.sharedMaterial = WaterMaterial();
            renderer.shadowCastingMode = ShadowCastingMode.Off;
            view.Water = water;
            view.WaterMesh = mesh;
        }

        private Material[] MaterialsFor(int slotMask)
        {
            if (_materialSets.TryGetValue(slotMask, out Material[] cached))
            {
                return cached;
            }

            int count = 0;
            for (int slot = 0; slot < TerrainMaterials.SlotCount; slot++)
            {
                if ((slotMask & (1 << slot)) != 0)
                {
                    count++;
                }
            }

            Material[] set = new Material[count];
            int next = 0;
            for (int slot = 0; slot < TerrainMaterials.SlotCount; slot++)
            {
                if ((slotMask & (1 << slot)) != 0)
                {
                    set[next++] = Resolve((byte)slot);
                }
            }

            _materialSets.Add(slotMask, set);
            return set;
        }

        private Material WaterMaterial()
        {
            return Resolve(TerrainMaterials.Water);
        }

        private Material Resolve(byte slot)
        {
            Material cached = _slotMaterials[slot];
            if (cached != null)
            {
                return cached;
            }

            RbxMaterialId id = TerrainMaterials.ToMaterialId(slot);
            if (!_materials.TryGetMaterial(in id, out Material material))
            {
                _log?.Warn("[CoreAI.RbxApi] Terrain material " + id + " is missing from the material catalog; "
                           + "its cells draw with the diagnostic fallback.", LogTag.World);
            }

            _slotMaterials[slot] = material;
            return material;
        }

        private void DestroyView(ChunkView view)
        {
            if (view.Collider != null)
            {
                _colliders.Remove(view.Collider);
            }

            DestroyObject(view.SolidMesh);
            DestroyObject(view.WaterMesh);
            DestroyObject(view.Solid);
        }

        private static void DestroyObject(Object target)
        {
            if (target == null)
            {
                return;
            }

            if (Application.isPlaying)
            {
                Object.Destroy(target);
            }
            else
            {
                Object.DestroyImmediate(target);
            }
        }

        private sealed class ChunkView
        {
            public ChunkView(GameObject solid, Mesh solidMesh, MeshRenderer renderer, MeshCollider collider)
            {
                Solid = solid;
                SolidMesh = solidMesh;
                Renderer = renderer;
                Collider = collider;
            }

            public GameObject Solid { get; }

            public Mesh SolidMesh { get; }

            public MeshRenderer Renderer { get; }

            public MeshCollider Collider { get; }

            public GameObject Water { get; set; }

            public Mesh WaterMesh { get; set; }
        }
    }
}
