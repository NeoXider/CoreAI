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
    /// A store change queues the chunks its <see cref="TerrainChangeSet.RemeshChunks"/> names, plus a
    /// light-only refresh of the chunks whose sky exposure it moved (<see cref="TerrainSkyExposure"/>);
    /// <see cref="ProcessDirty"/> works through them under a budget on the main thread.
    /// </summary>
    /// <remarks>
    /// Materials are the material catalog's own (the same Grass a Part shows) in a terrain copy that
    /// reads the sky exposure from the mesh's vertex colours; one submesh per material, with no
    /// MaterialPropertyBlock, so every chunk stays on the SRP Batcher path and draw calls grow with
    /// visible chunks and the materials in them, never with cells.
    /// WHY no jobs: the same code runs in a WebGL player, which has no worker threads, and a chunk
    /// meshes in a fraction of a millisecond; baking colliders off-thread is a desktop-only option.
    /// </remarks>
    public sealed class RbxTerrainRenderer : IRbxTerrainRenderer, IRbxTerrainColliderReadiness, IRbxTerrainSolidCells
    {
        /// <summary>Material property the terrain copy sets to 1: read the exposure from the vertex colour's alpha.</summary>
        public const string SkyExposureSourceProperty = "_RbxSkyExposureSource";

        /// <summary>
        /// Light-only refreshes one <see cref="ProcessDirty"/> call still runs once its chunk or time budget is
        /// spent, so the light a write changed is drawn in the frame its geometry is.
        /// </summary>
        public const int MaxLightRefreshesPastBudget = 32;

        /// <summary>Material property a block twin sets to 1: one whole tile per cell face, on the cell grid.</summary>
        public const string CellTilesProperty = "_RbxCellTiles";

        /// <summary>Material property a block twin sets to the look's corner shade strength (the mesh colour's RGB).</summary>
        public const string CornerShadeProperty = "_RbxCornerShade";

        private const string TexturedShaderResource = "CoreAIRbxMaterials/RbxTexturedSurface";
        private const string TexturedShaderName = "CoreAI/Rbx/Textured Surface";
        private const string EmissionKeyword = "_RBX_EMISSION";

        private static readonly int SkyExposureSourceId = Shader.PropertyToID(SkyExposureSourceProperty);
        private static readonly int CellTilesId = Shader.PropertyToID(CellTilesProperty);
        private static readonly int CornerShadeId = Shader.PropertyToID(CornerShadeProperty);

        private readonly RbxTerrain _terrain;
        private readonly IRbxMaterialProvider<Material> _materials;
        private readonly bool _ownsMaterials;
        private readonly ITerrainChunkMesher _mesher;
        private readonly ILog _log;
        private readonly GameObject _root;
        private readonly TerrainChunkMeshBuffers _buffers = new();
        private readonly RbxTerrainMeshBuilder _builder = new();
        private readonly TerrainSkyExposure _exposure;
        private readonly TerrainBlockLook _blockLook;
        private readonly Queue<TerrainChunkCoord> _dirty = new();
        private readonly Dictionary<long, bool> _dirtyFull = new();
        private readonly Dictionary<long, ChunkView> _views = new();
        private readonly Dictionary<Collider, TerrainChunkCoord> _colliders = new();
        private readonly Dictionary<int, Material[]> _materialSets = new();
        private readonly List<Material> _ownedCopies = new();
        private readonly Material[] _slotMaterials = new Material[TerrainMaterials.SlotCount];
        private readonly List<TerrainChunkCoord> _lastProcessed = new();
        private readonly List<TerrainChunkCoord> _coordScratch = new();
        private bool _disposed;

        /// <summary>
        /// Draws <paramref name="terrain"/> under <paramref name="parent"/>. A null
        /// <paramref name="materials"/> uses the hybrid texture/procedural catalog; a null
        /// <paramref name="mesher"/> uses <see cref="BlockyTerrainChunkMesher"/>. Every chunk already
        /// in the store is queued, so a loaded world appears without a write. Light fades out
        /// <paramref name="skyReach"/> cells past a cave mouth (<see cref="TerrainSkyExposure.Reach"/>). Cells are
        /// drawn by <paramref name="blockLook"/>; null paints blocks (<see cref="TerrainBlockLook.Default"/>) with the
        /// default catalog, and draws every material from <paramref name="materials"/> when a host hands its own
        /// (<see cref="TerrainBlockLook.PhotoSets"/>).
        /// </summary>
        public RbxTerrainRenderer(RbxTerrain terrain, Transform parent,
            IRbxMaterialProvider<Material> materials = null, ITerrainChunkMesher mesher = null, ILog log = null,
            int skyReach = TerrainSkyExposure.DefaultReach, TerrainBlockLook blockLook = null)
        {
            _terrain = terrain ?? throw new ArgumentNullException(nameof(terrain));
            _exposure = new TerrainSkyExposure(terrain.Store, skyReach);
            // WHY photo sets for a host's own provider: a host that brings its own materials expects to see them.
            _blockLook = blockLook ?? (materials == null ? TerrainBlockLook.Default : TerrainBlockLook.PhotoSets);
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
                Enqueue(_coordScratch[index], true);
            }
        }

        /// <inheritdoc />
        public RbxTerrain Terrain => _terrain;

        /// <inheritdoc />
        public bool IsSolidCell(int x, int y, int z)
        {
            byte material = _terrain.Store.GetMaterial(x, y, z);
            return material != TerrainMaterials.Air && material != TerrainMaterials.Water;
        }

        /// <inheritdoc />
        public int PendingChunkCount => _dirtyFull.Count;

        /// <summary>The GameObject every chunk object sits under.</summary>
        public GameObject Root => _root;

        /// <summary>Chunks that currently have a rendered or colliding object.</summary>
        public int ChunkObjectCount => _views.Count;

        /// <summary>Chunk re-meshes since construction (a PM/EM counter); light-only refreshes are not counted.</summary>
        public long RemeshCount { get; private set; }

        /// <summary>Light-only refreshes since construction: chunks whose exposure changed while their faces did not.</summary>
        public long LightRefreshCount { get; private set; }

        /// <summary>The sky exposure this renderer lights its chunks with; also answers how exposed any cell is.</summary>
        public TerrainSkyExposure SkyExposure => _exposure;

        /// <summary>How this renderer draws each material as a cell: a painted block tile or the catalog's surface.</summary>
        public TerrainBlockLook BlockLook => _blockLook;

        /// <summary>The chunks the last <see cref="ProcessDirty"/> call re-meshed or relit; reused between calls.</summary>
        public IReadOnlyList<TerrainChunkCoord> LastProcessedChunks => _lastProcessed;

        /// <inheritdoc />
        public int ProcessDirty(int maxChunks, double maxMilliseconds)
        {
            _lastProcessed.Clear();
            if (_disposed || _dirtyFull.Count == 0)
            {
                return 0;
            }

            double start = Time.realtimeSinceStartupAsDouble;
            int processed = 0;
            int relitPastBudget = 0;
            while (_dirty.Count > 0)
            {
                TerrainChunkCoord coord = _dirty.Peek();
                // WHY a skipped coordinate: EnsureColliders may have built a chunk out of turn, leaving its
                // old place in the queue behind.
                if (!_dirtyFull.TryGetValue(coord.Key, out bool full))
                {
                    _dirty.Dequeue();
                    continue;
                }

                bool withinBudget = processed == 0 || (processed < maxChunks
                                                       && (Time.realtimeSinceStartupAsDouble - start) * 1000d
                                                       < maxMilliseconds);
                // WHY light refreshes may run past the budget: a write queues them right behind its own chunks,
                // and the light it changed must land in the same frame as the geometry; they cost no collider.
                bool ridesAlong = !full && relitPastBudget < MaxLightRefreshesPastBudget;
                if (!withinBudget && !ridesAlong)
                {
                    break;
                }

                _dirty.Dequeue();
                _dirtyFull.Remove(coord.Key);
                Refresh(coord, full);
                _lastProcessed.Add(coord);
                if (withinBudget)
                {
                    processed++;
                }
                else
                {
                    relitPastBudget++;
                }
            }

            return processed + relitPastBudget;
        }

        /// <inheritdoc />
        public int EnsureColliders(Bounds unityBounds)
        {
            if (_disposed || _dirtyFull.Count == 0)
            {
                return 0;
            }

            RbxVector3 first = RbxSpace.FromUnity(unityBounds.min);
            RbxVector3 second = RbxSpace.FromUnity(unityBounds.max);
            double cell = TerrainBoxRasterizer.CellStuds;
            int minX = (int)Math.Floor(Math.Min(first.X, second.X) / cell) >> TerrainChunkCoord.Shift;
            int minY = (int)Math.Floor(Math.Min(first.Y, second.Y) / cell) >> TerrainChunkCoord.Shift;
            int minZ = (int)Math.Floor(Math.Min(first.Z, second.Z) / cell) >> TerrainChunkCoord.Shift;
            int maxX = (int)Math.Floor(Math.Max(first.X, second.X) / cell) >> TerrainChunkCoord.Shift;
            int maxY = (int)Math.Floor(Math.Max(first.Y, second.Y) / cell) >> TerrainChunkCoord.Shift;
            int maxZ = (int)Math.Floor(Math.Max(first.Z, second.Z) / cell) >> TerrainChunkCoord.Shift;
            int built = 0;
            for (int cy = minY; cy <= maxY; cy++)
            {
                for (int cz = minZ; cz <= maxZ; cz++)
                {
                    for (int cx = minX; cx <= maxX; cx++)
                    {
                        TerrainChunkCoord coord = new(cx, cy, cz);
                        if (!_dirtyFull.TryGetValue(coord.Key, out bool full) || !full)
                        {
                            continue;
                        }

                        _dirtyFull.Remove(coord.Key);
                        Refresh(coord, true);
                        built++;
                    }
                }
            }

            return built;
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

        /// <summary>True while chunk <paramref name="coord"/> waits for a re-mesh that changes its faces or collider.</summary>
        public bool IsGeometryPending(TerrainChunkCoord coord)
        {
            return _dirtyFull.TryGetValue(coord.Key, out bool full) && full;
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

        /// <summary>Stops observing the store and destroys every chunk object, mesh and terrain material copy.</summary>
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
            _dirtyFull.Clear();
            DestroyObject(_root);
            for (int index = 0; index < _ownedCopies.Count; index++)
            {
                DestroyObject(_ownedCopies[index]);
            }

            _ownedCopies.Clear();
            if (_ownsMaterials && _materials is IDisposable disposable)
            {
                disposable.Dispose();
            }
        }

        private void OnStoreChanged(TerrainChangeSet change)
        {
            _exposure.OnStoreChanged(change);
            IReadOnlyList<TerrainChunkCoord> remesh = change.RemeshChunks;
            for (int index = 0; index < remesh.Count; index++)
            {
                Enqueue(remesh[index], true);
            }

            _coordScratch.Clear();
            _exposure.CollectLightAffectedChunks(change, _coordScratch);
            for (int index = 0; index < _coordScratch.Count; index++)
            {
                if (_views.ContainsKey(_coordScratch[index].Key))
                {
                    Enqueue(_coordScratch[index], false);
                }
            }
        }

        private void Enqueue(TerrainChunkCoord coord, bool full)
        {
            long key = coord.Key;
            if (_dirtyFull.TryGetValue(key, out bool queuedFull))
            {
                if (full && !queuedFull)
                {
                    _dirtyFull[key] = true;
                }

                return;
            }

            _dirtyFull.Add(key, full);
            _dirty.Enqueue(coord);
        }

        private void Refresh(TerrainChunkCoord coord, bool full)
        {
            long key = coord.Key;
            if (!full)
            {
                if (_views.TryGetValue(key, out ChunkView lit))
                {
                    Relight(coord, lit);
                }

                return;
            }

            RemeshCount++;
            _mesher.BuildChunk(_terrain.Store, coord, _buffers);
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

            _exposure.PrepareChunk(coord);
            int solidFaces = _builder.BuildSolid(_buffers, view.SolidMesh, out int slotMask, _exposure);
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
                _builder.BuildWater(_buffers, view.WaterMesh, _exposure);
                view.Water.SetActive(true);
            }
            else if (view.Water != null)
            {
                view.Water.SetActive(false);
                view.WaterMesh.Clear();
            }
        }

        private void Relight(TerrainChunkCoord coord, ChunkView view)
        {
            LightRefreshCount++;
            _mesher.BuildChunk(_terrain.Store, coord, _buffers);
            _exposure.PrepareChunk(coord);
            bool solidMatches = _builder.UpdateSolidExposure(_buffers, view.SolidMesh, _exposure);
            bool waterMatches = view.WaterMesh == null || !view.Water.activeSelf
                ? _buffers.GetQuads(TerrainMaterials.Water).Count == 0
                : _builder.UpdateWaterExposure(_buffers, view.WaterMesh, _exposure);
            if (!solidMatches || !waterMatches)
            {
                // WHY a full rebuild: the faces no longer match the mesh, so a write this renderer was not
                // told about changed them; rebuilding is always correct.
                Refresh(coord, true);
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
            bool found = _materials.TryGetMaterial(in id, out Material material);
            TerrainBlockStyle style = _blockLook.StyleOf(slot);
            Material block = style == TerrainBlockStyle.Photo ? null : BlockTwin(slot, style, material);
            if (block != null)
            {
                _slotMaterials[slot] = block;
                return block;
            }

            if (!found)
            {
                _log?.Warn("[CoreAI.RbxApi] Terrain material " + id + " is missing from the material catalog; "
                           + "its cells draw with the diagnostic fallback.", LogTag.World);
            }

            material = TerrainCopy(material);
            if (slot == TerrainMaterials.Water)
            {
                ApplyWaterLook(material);
            }

            _slotMaterials[slot] = material;
            return material;
        }

        /// <summary>Tunes the cells' own copy of the water (<see cref="TerrainBlockLook.Water"/>); a Part's water is untouched.</summary>
        private void ApplyWaterLook(Material water)
        {
            TerrainWaterLook look = _blockLook.Water;
            if (!look.Overrides || water == null || !_ownedCopies.Contains(water) || !water.HasProperty(BlockIds.WaveScale))
            {
                return;
            }

            water.SetFloat(BlockIds.WaveScale, look.WaveScale);
            water.SetFloat(BlockIds.WaveStrength, look.WaveStrength);
            water.SetFloat(BlockIds.WaterSmoothness, look.Smoothness);
            water.SetFloat(BlockIds.WaterAbsorption, look.Absorption);
            float alpha = water.GetColor(BlockIds.MaterialColor).a;
            water.SetColor(BlockIds.MaterialColor, new Color(look.Red, look.Green, look.Blue, alpha));
        }

        /// <summary>
        /// The block twin of a material: the textured surface with the painted tile of <paramref name="style"/>, one
        /// whole tile per cell face, the sky exposure from the vertex colours, and the catalog material's glow (lava).
        /// Null when the textured shader is not in the build, so the catalog surface is drawn instead.
        /// </summary>
        /// <remarks>
        /// WHY a material of its own and not a keyword: the block look is textures and material floats on a twin no
        /// Part ever draws with, so the Part's surface is untouched, no shader variant is added and the chunks stay
        /// on the SRP Batcher.
        /// </remarks>
        private Material BlockTwin(byte slot, TerrainBlockStyle style, Material source)
        {
            Shader shader = source != null && source.HasProperty(CellTilesId) ? source.shader : TexturedShader();
            if (shader == null
                || !RbxTerrainBlockTextures.TryGet(style, out Texture2D albedo, out Texture2D normal,
                    out Texture2D roughness))
            {
                return null;
            }

            Material twin = new(shader)
            {
                name = "Terrain block " + TerrainMaterials.GetName(slot),
                hideFlags = HideFlags.HideAndDontSave,
                enableInstancing = true
            };
            twin.SetTexture(BlockIds.BaseMap, albedo);
            twin.SetTexture(BlockIds.BumpMap, normal);
            twin.SetTexture(BlockIds.RoughnessMap, roughness);
            twin.SetColor(BlockIds.BaseColor, Color.white);
            twin.SetColor(BlockIds.Color, Color.white);
            twin.SetColor(BlockIds.MaterialColor, Color.white);
            twin.SetFloat(BlockIds.PartColorInfluence, 0f);
            // WHY one cell per tile: a 4-stud cell edge then always falls on a whole tile in world space.
            twin.SetFloat(BlockIds.TextureScale, 1f / RbxSpace.LengthToUnity((float)TerrainBoxRasterizer.CellStuds));
            twin.SetFloat(BlockIds.TextureAspect, 1f);
            twin.SetFloat(BlockIds.BumpScale, 1f);
            twin.SetFloat(BlockIds.RoughnessScale, 1f);
            twin.SetFloat(BlockIds.InvertRoughness, 0f);
            twin.SetFloat(BlockIds.CavityStrength, 0f);
            twin.SetFloat(BlockIds.MetalAlbedoLift, 0f);
            twin.DisableKeyword("_RBX_METALLIC_MAP");
            twin.DisableKeyword("_RBX_OCCLUSION_MAP");
            twin.DisableKeyword("_RBX_NORMAL_DIRECTX");
            bool glows = source != null && source.IsKeywordEnabled(EmissionKeyword)
                                        && source.HasProperty(BlockIds.EmissionColor);
            if (glows)
            {
                twin.SetColor(BlockIds.EmissionColor, source.GetColor(BlockIds.EmissionColor));
                twin.SetFloat(BlockIds.EmissionThreshold, source.GetFloat(BlockIds.EmissionThreshold));
                twin.EnableKeyword(EmissionKeyword);
            }
            else
            {
                twin.DisableKeyword(EmissionKeyword);
            }

            twin.SetFloat(SkyExposureSourceId, 1f);
            twin.SetFloat(CellTilesId, 1f);
            twin.SetFloat(CornerShadeId, _blockLook.CornerShade);
            _ownedCopies.Add(twin);
            return twin;
        }

        private static Shader TexturedShader()
        {
            Shader shader = Resources.Load<Shader>(TexturedShaderResource);
            return shader != null ? shader : Shader.Find(TexturedShaderName);
        }

        /// <summary>
        /// The copy of a catalog material that reads the sky exposure from the vertex colours. A material
        /// whose shader has no exposure input is drawn as it is.
        /// </summary>
        /// <remarks>
        /// WHY a copy and not a keyword or a property block: a Part never draws with the copy, so it never
        /// reads a vertex colour its mesh does not have; the switch is a material float, so no shader
        /// variant is added; and without a property block the chunks stay on the SRP Batcher.
        /// WHY one copy per slot, taken at once: the catalog lends its materials (a pooled variant may be set up for
        /// another material by the next lookup), so copies shared by source object drew every slot with the material
        /// resolved first; <see cref="Resolve"/> already keeps one material per slot.
        /// </remarks>
        private Material TerrainCopy(Material source)
        {
            if (source == null || !source.HasProperty(SkyExposureSourceId))
            {
                return source;
            }

            Material copy = new(source)
            {
                name = source.name,
                hideFlags = source.hideFlags,
                enableInstancing = source.enableInstancing
            };
            copy.SetFloat(SkyExposureSourceId, 1f);
            _ownedCopies.Add(copy);
            return copy;
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

        private static class BlockIds
        {
            public static readonly int BaseMap = Shader.PropertyToID("_BaseMap");
            public static readonly int BumpMap = Shader.PropertyToID("_BumpMap");
            public static readonly int RoughnessMap = Shader.PropertyToID("_RoughnessMap");
            public static readonly int BaseColor = Shader.PropertyToID("_BaseColor");
            public static readonly int Color = Shader.PropertyToID("_Color");
            public static readonly int MaterialColor = Shader.PropertyToID("_MaterialColor");
            public static readonly int PartColorInfluence = Shader.PropertyToID("_PartColorInfluence");
            public static readonly int TextureScale = Shader.PropertyToID("_TextureScale");
            public static readonly int TextureAspect = Shader.PropertyToID("_TextureAspect");
            public static readonly int BumpScale = Shader.PropertyToID("_BumpScale");
            public static readonly int RoughnessScale = Shader.PropertyToID("_RoughnessScale");
            public static readonly int InvertRoughness = Shader.PropertyToID("_InvertRoughness");
            public static readonly int CavityStrength = Shader.PropertyToID("_CavityStrength");
            public static readonly int MetalAlbedoLift = Shader.PropertyToID("_MetalAlbedoLift");
            public static readonly int EmissionColor = Shader.PropertyToID("_EmissionColor");
            public static readonly int EmissionThreshold = Shader.PropertyToID("_EmissionThreshold");
            public static readonly int WaveScale = Shader.PropertyToID("_PatternScale");
            public static readonly int WaveStrength = Shader.PropertyToID("_BumpStrength");
            public static readonly int WaterSmoothness = Shader.PropertyToID("_Smoothness");
            public static readonly int WaterAbsorption = Shader.PropertyToID("_Absorption");
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
